using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Konscious.Security.Cryptography;

namespace DmOffice.Core;

/// <summary>Was über den Tresor einer Person offen auf der Platte liegt (users/&lt;id&gt;.tresor.json): nur Hüllen um den Schlüssel.</summary>
public sealed class TresorMeta
{
    public int Version { get; set; } = 1;
    public DateTime Created { get; set; } = DateTime.UtcNow;
    /// <summary>Öffentlicher Schlüssel (P-256, SubjectPublicKeyInfo, Base64) - für Neues bei geschlossenem Tresor.</summary>
    public string PublicKey { get; set; } = "";
    /// <summary>Privater Schlüssel dazu (PKCS#8), mit dem Tresor-Schlüssel verschlossen.</summary>
    public string PrivateKey { get; set; } = "";
    /// <summary>Wiederherstellungsschlüssel (Text), mit dem Tresor-Schlüssel verschlossen - zum erneuten Drucken.</summary>
    public string RecoveryKey { get; set; } = "";
    /// <summary>
    /// Masterkey des Cryptomator-Tresors (64 Byte, docs/tresor-cryptomator-konzept.md, 2.4), mit dem Tresor-Schlüssel verschlossen.
    /// Leer: Tresor von vor dem Cryptomator-Format, Dateien im BlobStore.
    /// </summary>
    public string Vault { get; set; } = "";
    public List<TresorWrap> Wraps { get; set; } = new();
    public TresorSettings Settings { get; set; } = new();
}

/// <summary>Eine Hülle um den Tresor-Schlüssel: Passwort, Wiederherstellungscode, Wiederherstellungsschlüssel, Gerät.</summary>
public sealed class TresorWrap
{
    public const string Password = "password", Code = "code", Recovery = "recovery", Remember = "remember", Biometric = "biometric", Passkey = "passkey";
    /// <summary>Passkey: Kennung der Anmeldeinformation (Base64url) und das Salz für die PRF-Erweiterung (Base64) - beides nicht geheim.</summary>
    public string? CredentialId { get; set; }
    public string? PrfSalt { get; set; }
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = Password;
    /// <summary>Salz für die Ableitung (Base64).</summary>
    public string Salt { get; set; } = "";
    /// <summary>Verschlossener Tresor-Schlüssel: Nonce, Prüfsumme, Inhalt (AES-GCM, Base64).</summary>
    public string Data { get; set; } = "";
    /// <summary>Argon2id beim Passwort: Speicher in KiB und Durchläufe.</summary>
    public int Memory { get; set; }
    public int Iterations { get; set; }
    /// <summary>Gerät: Anzeigename.</summary>
    public string? Name { get; set; }
    public DateTime Created { get; set; } = DateTime.UtcNow;
}

public sealed class TresorSettings
{
    public const string Hour = "1h", EightHours = "8h", Day = "1d", Never = "never";
    public static readonly string[] AutoCloseValues = [Hour, EightHours, Day, Never];
    /// <summary>Automatisch schließen nach so langer Zeit ohne Nutzung (je Gerät).</summary>
    public string AutoClose { get; set; } = Never;
    /// <summary>Schließen auf allen Geräten, wenn die Verwaltung Passwort oder Zwei-Faktor zurücksetzt - immer an: sonst könnte ein
    /// Admin nach dem Zurücksetzen über WebDAV in den offenen Tresor (notizi). Früher abschaltbar, gespeicherte Werte gelten nicht mehr.</summary>
    public bool CloseOnAdminReset { get => true; set { } }
    /// <summary>App: beim Sperren des Handys schließen.</summary>
    public bool CloseOnScreenLock { get; set; }
    /// <summary>System-Tresor über Nextcloud und WebDAV bereitstellen (sichtbar und abgleichbar). Vorgabe aus.</summary>
    public bool Dav { get; set; }

    public TimeSpan? Idle => AutoClose switch { Hour => TimeSpan.FromHours(1), EightHours => TimeSpan.FromHours(8), Day => TimeSpan.FromDays(1), _ => null };
}

/// <summary>Tresor geschlossen (auf diesem Gerät bzw. überall) - 423 { locked: "tresor" }.</summary>
public sealed class TresorClosedException(string message = "Der Tresor ist geschlossen.") : Exception(message);

/// <summary>
/// Tresor (docs/tresor-konzept.md, „Entscheidung 29.10.2026“): je Person eine eigene, verschlüsselte Datenbank samt Dateien. Ihr
/// Schlüssel ist zufällig und liegt nie offen auf dem Server, nur in Hüllen: Tresor-Passwort (Argon2id), zehn
/// Wiederherstellungscodes, Wiederherstellungsschlüssel, Geräte (Fingerabdruck in der App, „nie schließen“). Offen ist er je Gerät;
/// solange er auf mindestens einem Gerät offen ist, hält der Server den Schlüssel im Arbeitsspeicher, danach verwirft er ihn.
/// Neues bei geschlossenem Tresor legt der Server mit dem öffentlichen Schlüssel ab (<see cref="Drop"/>); übernommen wird es
/// beim nächsten Öffnen (<see cref="Absorbers"/>).
/// </summary>
public static class Tresor
{
    public const string Cookie = "dm_tresor";
    public const string Locked = "tresor";

    /// <summary>Ablage der Daten (beim Start gesetzt, in Tests ein eigener Ordner).</summary>
    public static DataPaths? Paths { get; set; }

    /// <summary>Benutzerdatenbank einer Person (beim Start gesetzt) - dort liegt der System-Tresor in der Dateiablage. Ohne: kein
    /// Cryptomator-Tresor, die Dateien kommen in den BlobStore.</summary>
    public static Func<string, UserDb>? UserDbOf { get; set; }

    /// <summary>scrypt für masterkey.cryptomator (Vorgabe wie Cryptomator); Tests nehmen weniger.</summary>
    public static int? ScryptCost { get; set; }

    /// <summary>Uhr (Tests stellen sie vor).</summary>
    public static Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;

    /// <summary>Argon2id fürs Tresor-Passwort: Speicher (KiB) und Durchläufe. Tests nehmen weniger.</summary>
    public static int ArgonMemory { get; set; } = 64 * 1024;
    public static int ArgonIterations { get; set; } = 3;
    private static readonly SemaphoreSlim Argon = new(2);

    /// <summary>Übernahme von Neuem beim Öffnen je Art (Stufe 2 ff.: Tagebuch, Coach, notizi, Gesichter).</summary>
    public static readonly ConcurrentDictionary<string, Action<TresorDb, byte[]>> Absorbers = new();
    /// <summary>Nach dem Öffnen (und Übernehmen): z. B. alte Gesichtsdaten aus der Benutzerdatenbank umziehen. Fehler verhindern das Öffnen nicht.</summary>
    public static event Action<string, TresorDb>? Opened;
    /// <summary>Der Schlüssel ist weg (überall geschlossen): z. B. offene Notizen-Datenbanken aus dem Tresor freigeben.</summary>
    public static event Action<string>? Closed;

    private static DataPaths P => Paths ?? throw new InvalidOperationException("Tresor nicht eingerichtet.");
    private static string MetaPath(string userId) => Path.Combine(P.Users, userId + ".tresor.json");
    public static string DbPath(string userId) => Path.Combine(P.Users, userId + ".tresor.db");
    public static string BlobDir(string userId) => Path.Combine(P.UserBlobs(userId), "tresor");
    private static string InboxDir(string userId) => Path.Combine(P.Users, userId + ".tresor.in");
    /// <summary>Notizen der notizi-Datenbanken im Tresor (je Datenbank eine Datei, verschlüsselt mit einem Schlüssel aus dem Tresor-Schlüssel).</summary>
    public static string NotesDir(string userId) => Path.Combine(P.Users, userId + ".tresor.notes");

    // ---------------- Hüllen und Schlüssel ----------------

    private static readonly ConcurrentDictionary<string, object> Gates = new();
    private static object Gate(string userId) => Gates.GetOrAdd(userId, _ => new object());

    public static bool Exists(string userId) => File.Exists(MetaPath(userId));

    /// <summary>System-Tresor über Nextcloud und WebDAV bereitgestellt? (je WebDAV-Eintrag gefragt - aus dem Zwischenspeicher)</summary>
    public static bool DavEnabled(string userId)
    {
        if (Paths == null) return false;
        var p = MetaPath(userId);
        if (!File.Exists(p)) return false;
        var at = File.GetLastWriteTimeUtc(p);
        if (DavCache.TryGetValue(userId, out var c) && c.At == at) return c.On;
        var on = Meta(userId)?.Settings.Dav == true;
        DavCache[userId] = (at, on);
        return on;
    }
    private static readonly ConcurrentDictionary<string, (DateTime At, bool On)> DavCache = new();

    public static TresorMeta? Meta(string userId)
    {
        var p = MetaPath(userId);
        return File.Exists(p) ? JsonSerializer.Deserialize<TresorMeta>(File.ReadAllText(p)) : null;
    }

    private static void Save(string userId, TresorMeta m)
    {
        var p = MetaPath(userId);
        var tmp = p + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(m, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, p, overwrite: true);
    }

    private static void Update(string userId, Action<TresorMeta> change)
    {
        lock (Gate(userId))
        {
            var m = Meta(userId) ?? throw new InvalidOperationException("Es ist kein Tresor eingerichtet.");
            change(m);
            Save(userId, m);
        }
    }

    /// <summary>AES-GCM: Nonce (12) | Prüfsumme (16) | Inhalt.</summary>
    internal static byte[] Seal(byte[] key, byte[] plain, string context)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[plain.Length];
        using (var gcm = new AesGcm(key, 16)) gcm.Encrypt(nonce, plain, cipher, tag, Encoding.ASCII.GetBytes(context));
        return [.. nonce, .. tag, .. cipher];
    }

    /// <summary>Gegenstück zu <see cref="Seal"/>; null, wenn Schlüssel oder Inhalt nicht passen.</summary>
    internal static byte[]? Unseal(byte[] key, byte[] data, string context)
    {
        if (data.Length < 28) return null;
        var plain = new byte[data.Length - 28];
        try
        {
            using var gcm = new AesGcm(key, 16);
            gcm.Decrypt(data.AsSpan(0, 12), data.AsSpan(28), data.AsSpan(12, 16), plain, Encoding.ASCII.GetBytes(context));
            return plain;
        }
        catch (CryptographicException) { return null; }
    }

    private static async Task<byte[]> ArgonAsync(string password, byte[] salt, int memory, int iterations, CancellationToken ct)
    {
        await Argon.WaitAsync(ct);
        try
        {
            return await Task.Run(() =>
            {
                using var a = new Argon2id(Encoding.UTF8.GetBytes(password)) { Salt = salt, DegreeOfParallelism = 2, MemorySize = memory, Iterations = iterations };
                return a.GetBytes(32);
            }, ct);
        }
        finally { Argon.Release(); }
    }

    private static byte[] Hkdf(byte[] secret, byte[] salt, string info) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, salt, Encoding.ASCII.GetBytes(info));

    private static async Task<TresorWrap> PasswordWrap(string password, byte[] key, CancellationToken ct)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var kek = await ArgonAsync(password, salt, ArgonMemory, ArgonIterations, ct);
        return new TresorWrap { Kind = TresorWrap.Password, Salt = Convert.ToBase64String(salt), Memory = ArgonMemory, Iterations = ArgonIterations,
            Data = Convert.ToBase64String(Seal(kek, key, "tresor-wrap")) };
    }

    /// <summary>Hülle mit einem Geheimnis hoher Güte (Code, Wiederherstellungsschlüssel, Gerät) - HKDF reicht.</summary>
    private static TresorWrap SecretWrap(string kind, byte[] secret, byte[] key, string? name = null)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        return new TresorWrap { Kind = kind, Salt = Convert.ToBase64String(salt), Name = name,
            Data = Convert.ToBase64String(Seal(Hkdf(secret, salt, "tresor-" + kind), key, "tresor-wrap")) };
    }

    private static byte[]? OpenSecretWrap(TresorWrap w, byte[] secret) =>
        Unseal(Hkdf(secret, Convert.FromBase64String(w.Salt), "tresor-" + w.Kind), Convert.FromBase64String(w.Data), "tresor-wrap");

    // Codes ohne verwechselbare Zeichen (kein I, O, 0, 1): 20 Zeichen = 100 Bit
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    private static string RandomCode(int length)
    {
        var b = RandomNumberGenerator.GetBytes(length);
        return new string(b.Select(x => Alphabet[x % 32]).ToArray());
    }

    /// <summary>Eingabe vereinheitlichen: Bindestriche und Leerzeichen weg, groß, O→0 und I→1 gibt es nicht.</summary>
    public static string NormalizeCode(string code) => new(code.ToUpperInvariant().Where(c => Alphabet.Contains(c)).ToArray());

    private static string Group(string s, int size) => string.Join('-', s.Chunk(size).Select(c => new string(c)));

    public sealed record Created(List<string> Codes, string RecoveryKey, byte[] Key);

    /// <summary>Tresor einrichten: neuer Schlüssel, Schlüsselpaar, Hüllen für Passwort, zehn Codes und den Wiederherstellungsschlüssel.</summary>
    public static async Task<Created> CreateAsync(string userId, string password, CancellationToken ct = default)
    {
        if (!Creating.TryAdd(userId, true)) throw new InvalidOperationException("Der Tresor wird gerade eingerichtet.");
        try { return await CreateLockedAsync(userId, password, ct); }
        finally { Creating.TryRemove(userId, out _); }
    }

    private static readonly ConcurrentDictionary<string, bool> Creating = new();

    private static async Task<Created> CreateLockedAsync(string userId, string password, CancellationToken ct)
    {
        if (Exists(userId)) throw new InvalidOperationException("Es ist schon ein Tresor eingerichtet.");
        var key = RandomNumberGenerator.GetBytes(32);
        using var ec = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var masterkey = UserDbOf != null ? Cryptomator.Masterkey.Generate() : null;
        var codes = Enumerable.Range(0, 10).Select(_ => RandomCode(20)).ToList();
        var recovery = RandomCode(40);
        var m = new TresorMeta
        {
            PublicKey = Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo()),
            PrivateKey = Convert.ToBase64String(Seal(key, ec.ExportPkcs8PrivateKey(), "tresor-private")),
            RecoveryKey = Convert.ToBase64String(Seal(key, Encoding.UTF8.GetBytes(recovery), "tresor-recovery")),
            Vault = masterkey == null ? "" : Convert.ToBase64String(Seal(key, masterkey.Raw(), "tresor-cryptomator")),
        };
        // System-Tresor in der Dateiablage anlegen, masterkey.cryptomator am Tresor-Passwort
        if (masterkey != null)
        {
            var udb = UserDbOf!(userId);
            var files = new DmOffice.FileStoreVaultFiles(udb, DmOffice.FileStoreVaultFiles.DefaultRoot);
            // Ohne tresor.json ein abgebrochener Versuch: beiseite, damit neu eingerichtet werden kann
            if (files.FileExists(Cryptomator.VaultConfig.FileName) || files.FileExists(Cryptomator.MasterkeyFile.FileName) || files.DirExists(Cryptomator.VaultNames.DataDir))
                SetAside(udb);
            await Task.Run(() => Cryptomator.CryptomatorVault.Create(files, masterkey, password, ScryptCost, ownsKey: false).Dispose(), ct);
        }
        m.Wraps.Add(await PasswordWrap(password, key, ct));
        foreach (var c in codes) m.Wraps.Add(SecretWrap(TresorWrap.Code, Encoding.UTF8.GetBytes(c), key));
        m.Wraps.Add(SecretWrap(TresorWrap.Recovery, Encoding.UTF8.GetBytes(recovery), key));
        lock (Gate(userId))
        {
            if (Exists(userId)) throw new InvalidOperationException("Es ist schon ein Tresor eingerichtet.");
            Save(userId, m);
        }
        // Datenbank gleich anlegen, dann ist sie beim ersten Öffnen da
        Open(userId, key).Dispose();
        Keys[userId] = key;
        return new Created(codes.Select(c => Group(c, 5)).ToList(), Group(recovery, 5), key);
    }

    /// <summary>Schlüssel über das Tresor-Passwort; null, wenn es nicht stimmt.</summary>
    public static async Task<byte[]?> KeyFromPasswordAsync(string userId, string password, CancellationToken ct = default)
    {
        var w = Meta(userId)?.Wraps.FirstOrDefault(x => x.Kind == TresorWrap.Password);
        if (w == null) return null;
        var kek = await ArgonAsync(password, Convert.FromBase64String(w.Salt), w.Memory, w.Iterations, ct);
        return Unseal(kek, Convert.FromBase64String(w.Data), "tresor-wrap");
    }

    /// <summary>Schlüssel über einen Wiederherstellungscode (danach verbraucht) oder den Wiederherstellungsschlüssel.</summary>
    public static byte[]? KeyFromCode(string userId, string code)
    {
        var secret = Encoding.UTF8.GetBytes(NormalizeCode(code));
        var m = Meta(userId);
        if (m == null || secret.Length < 20) return null;
        foreach (var w in m.Wraps.Where(x => x.Kind is TresorWrap.Code or TresorWrap.Recovery))
            if (OpenSecretWrap(w, secret) is { } key)
            {
                if (w.Kind == TresorWrap.Code) Update(userId, x => x.Wraps.RemoveAll(y => y.Id == w.Id));
                return key;
            }
        return null;
    }

    /// <summary>Schlüssel über ein gemerktes Gerät (Fingerabdruck, „nie schließen“).</summary>
    public static byte[]? KeyFromDevice(string userId, string wrapId, byte[] secret)
    {
        var w = Meta(userId)?.Wraps.FirstOrDefault(x => x.Id == wrapId && x.Kind is TresorWrap.Remember or TresorWrap.Biometric or TresorWrap.Passkey);
        return w == null ? null : OpenSecretWrap(w, secret);
    }

    /// <summary>Gerät merken: neues Geheimnis, die Hülle bleibt auf dem Server, das Geheimnis nur auf dem Gerät.</summary>
    public static (string Id, string Secret) Remember(string userId, byte[] key, string kind, string? name)
    {
        if (kind == TresorWrap.Remember && Meta(userId)?.Settings.AutoClose != TresorSettings.Never)
            throw new InvalidOperationException("Merken geht nur, wenn der Tresor nie automatisch schließt.");
        var secret = RandomNumberGenerator.GetBytes(32);
        var w = SecretWrap(kind, secret, key, name);
        Update(userId, m => m.Wraps.Add(w));
        return (w.Id, Convert.ToBase64String(secret));
    }

    /// <summary>
    /// Passkey hinterlegen (Web, WebAuthn mit PRF): der Browser liefert zum Salz ein Geheimnis, das nur der Passkey nach Windows Hello,
    /// Fingerabdruck oder PIN wieder erzeugen kann. Auf dem Server liegt nur die Hülle samt Salz und Kennung.
    /// </summary>
    public static string AddPasskey(string userId, byte[] key, string credentialId, byte[] salt, byte[] secret, string? name)
    {
        if (secret.Length < 32 || salt.Length < 16) throw new ArgumentException("Der Passkey hat keinen brauchbaren Schlüssel geliefert.");
        var w = SecretWrap(TresorWrap.Passkey, secret, key, name);
        w.CredentialId = credentialId;
        w.PrfSalt = Convert.ToBase64String(salt);
        Update(userId, m => { m.Wraps.RemoveAll(x => x.Kind == TresorWrap.Passkey && x.CredentialId == credentialId); m.Wraps.Add(w); });
        return w.Id;
    }

    public static void Forget(string userId, string wrapId) =>
        Update(userId, m => m.Wraps.RemoveAll(x => x.Id == wrapId && x.Kind is TresorWrap.Remember or TresorWrap.Biometric or TresorWrap.Passkey));

    /// <summary>Tresor-Passwort ändern: nur die Hülle wird neu, der Inhalt bleibt.</summary>
    public static async Task ChangePasswordAsync(string userId, byte[] key, string password, CancellationToken ct = default)
    {
        var w = await PasswordWrap(password, key, ct);
        // masterkey.cryptomator zieht mit: mit Cryptomator öffnet man den Tresor mit dem Tresor-Passwort
        if (VaultOf(userId, key) is { } vault)
            using (vault) await Task.Run(() => vault.SetPassword(password, ScryptCost), ct);
        else await UpgradeAsync(userId, key, password, ct);
        Update(userId, m => { m.Wraps.RemoveAll(x => x.Kind == TresorWrap.Password); m.Wraps.Add(w); });
    }

    /// <summary>
    /// Tresor von vor dem Cryptomator-Format nachrüsten: beim nächsten Öffnen mit dem Passwort bzw. beim neuen Passwort bekommt er den
    /// Masterkey und den Cryptomator-Tresor in der Ablage (masterkey.cryptomator mit diesem Passwort). Dateien im alten Speicher kommen
    /// mit hinein (früher nur leere Tresore - wer ihn per Passkey öffnete und dann füllte, blieb im alten Format). Die alten Blobs
    /// bleiben liegen, bis der Tresor gelöscht wird.
    /// </summary>
    public static async Task UpgradeAsync(string userId, byte[] key, string? password, CancellationToken ct = default)
    {
        if (UserDbOf == null || Meta(userId) is not { Vault.Length: 0 }) return;
        // Einer zugleich je Person: zwei gleichzeitige Öffnen-Anfragen ziehen nicht doppelt um
        var gate = UpgradeGates.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        // Während des Umzugs gilt der Tresor als geschlossen (DbOf: null, Aufrufe bekommen 423) - sonst schriebe eine
        // gleichzeitige Anfrage in den alten Speicher, der gerade kopiert wird, und ihre Datei ginge verloren
        Upgrading[userId] = true;
        try { await UpgradeLockedAsync(userId, key, password, ct); }
        finally { Upgrading.TryRemove(userId, out _); gate.Release(); }
    }
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> UpgradeGates = new();
    private static readonly ConcurrentDictionary<string, bool> Upgrading = new();

    /// <summary>Was unter dmVAULT/vault liegt, ohne gemerkten Masterkey nicht zu öffnen ist (halber Versuch, oder tresor.json
    /// aus einer älteren Sicherung): beiseite legen statt löschen - vielleicht steckt doch Inhalt darin.</summary>
    private static void SetAside(UserDb udb)
    {
        using (DmOffice.Dav.FileStore.SystemAccess())
            if (DmOffice.Dav.FileStore.Get(udb, DmOffice.FileStoreVaultFiles.DefaultRoot) != null)
                DmOffice.Dav.FileStore.MoveOrCopy(udb, DmOffice.FileStoreVaultFiles.DefaultRoot,
                    DmOffice.Dav.FileStore.SystemVault + "/vault-beiseite-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"), false, false);
    }

    private static async Task UpgradeLockedAsync(string userId, byte[] key, string? password, CancellationToken ct)
    {
        if (Meta(userId) is not { Vault.Length: 0 }) return;
        var udb = UserDbOf!(userId);
        var files = new DmOffice.FileStoreVaultFiles(udb, DmOffice.FileStoreVaultFiles.DefaultRoot);
        if (files.FileExists(Cryptomator.VaultConfig.FileName) || files.DirExists(Cryptomator.VaultNames.DataDir)) SetAside(udb);
        // Geöffnete Datenbank schließen: sie wird nach dem Umstellen mit dem Cryptomator-Speicher neu geöffnet
        if (Dbs.TryRemove(userId, out var open)) open.Dispose();
        using var masterkey = Cryptomator.Masterkey.Generate();
        try
        {
            await Task.Run(async () =>
            {
                using var vault = Cryptomator.CryptomatorVault.Create(files, masterkey, password, ScryptCost, ownsKey: false);
                // Dateien im bisherigen Speicher (Blobs) in den Cryptomator-Tresor übernehmen - Ordner zuerst, Pfade wie im Baum.
                // Die Einträge der Tabelle bleiben; der Cryptomator-Speicher findet die Inhalte über den Pfad.
                using var old = new TresorDb(DbPath(userId), key, BlobDir(userId));
                var target = new DmOffice.CryptomatorTresorStorage(vault);
                // Ein Ordner „.dmoffice“ in der Wurzel hieße wie die internen Daten: als „.dmoffice (alt)“ übernehmen
                static string Target(string path) => path.Equals(DmOffice.TresorFileStore.Internal, StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith(DmOffice.TresorFileStore.Internal + "/", StringComparison.OrdinalIgnoreCase)
                    ? DmOffice.TresorFileStore.Internal + " (alt)" + path[DmOffice.TresorFileStore.Internal.Length..] : path;
                var all = old.Query("SELECT id, is_dir, blob, size FROM files", r => (Id: r.GetInt64(0), Dir: r.GetInt64(1) != 0, Blob: r.IsDBNull(2) ? null : r.GetString(2), Size: r.GetInt64(3)));
                var paths = all.Select(e => (e.Id, e.Dir, e.Blob, e.Size, Path: old.Files.PathOf(e.Id))).Where(e => e.Path is { Length: > 0 }).ToList();
                foreach (var d in paths.Where(e => e.Dir).OrderBy(e => e.Path!.Count(c => c == '/')))
                    if (vault.DirIdOf("/" + Target(d.Path!)) == null) target.CreateFolder(Target(d.Path!));
                foreach (var f in paths.Where(e => !e.Dir))
                {
                    ct.ThrowIfCancellationRequested();
                    await using var src = old.Files.Storage.OpenRead(f.Path!, f.Blob);
                    var (_, written) = await target.WriteAsync(Target(f.Path!), src, long.MaxValue, null, ct);
                    // Erst festhalten, wenn alles vollständig drüben ist
                    if (written != f.Size) throw new IOException($"„{f.Path}“ kam nicht vollständig in den Cryptomator-Tresor ({written} statt {f.Size} Bytes).");
                }
            }, ct);
        }
        catch
        {
            // Nichts halb lassen: der alte Speicher bleibt, wie er war; der angefangene Tresor wird beiseite gelegt, beim nächsten Öffnen neu
            try { SetAside(udb); } catch (Exception) { /* der alte Speicher gilt weiter */ }
            throw;
        }
        var sealedKey = Convert.ToBase64String(Seal(key, masterkey.Raw(), "tresor-cryptomator"));
        Update(userId, m => m.Vault = sealedKey);
        // Schon offene Datenbank (anderes Gerät) neu öffnen, dann mit dem Cryptomator-Speicher
        if (Dbs.TryRemove(userId, out var cached)) cached.Dispose();
    }

    /// <summary>Per Passkey oder gemerktem Gerät umgestellt: masterkey.cryptomator (für Cryptomator am Rechner) fehlt noch - mit dem
    /// Passwort nachtragen.</summary>
    public static void EnsureMasterkeyFile(string userId, byte[] key, string password)
    {
        if (UserDbOf == null) return;
        var files = new DmOffice.FileStoreVaultFiles(UserDbOf(userId), DmOffice.FileStoreVaultFiles.DefaultRoot);
        if (files.FileExists(Cryptomator.MasterkeyFile.FileName)) return;
        if (VaultOf(userId, key) is { } vault) using (vault) vault.SetPassword(password, ScryptCost);
    }

    /// <summary>Cryptomator-Tresor ohne masterkey.cryptomator (noch nicht mit dem Passwort geöffnet)?</summary>
    public static bool MasterkeyFileMissing(string userId) =>
        UserDbOf != null && Meta(userId) is { Vault.Length: > 0 }
        && !new DmOffice.FileStoreVaultFiles(UserDbOf(userId), DmOffice.FileStoreVaultFiles.DefaultRoot).FileExists(Cryptomator.MasterkeyFile.FileName);

    /// <summary>Der Cryptomator-Tresor der Person mit dem Masterkey aus <see cref="TresorMeta.Vault"/>; null bei Tresoren ohne.</summary>
    private static Cryptomator.CryptomatorVault? VaultOf(string userId, byte[] key)
    {
        if (UserDbOf == null || Meta(userId) is not { Vault.Length: > 0 } m) return null;
        var raw = Unseal(key, Convert.FromBase64String(m.Vault), "tresor-cryptomator") ?? throw new CryptographicException("Der Masterkey des Tresors passt nicht.");
        var mk = Cryptomator.Masterkey.FromRaw(raw);
        CryptographicOperations.ZeroMemory(raw);
        return Cryptomator.CryptomatorVault.Open(new DmOffice.FileStoreVaultFiles(UserDbOf(userId), DmOffice.FileStoreVaultFiles.DefaultRoot), mk, ownsKey: true);
    }

    /// <summary>Tresor-Datenbank samt Speicher der Dateien (Cryptomator-Tresor oder BlobStore). Für Wiederherstellen und Tests;
    /// im Betrieb <see cref="DbOf"/>.</summary>
    public static TresorDb OpenDb(string userId, byte[] key) => Open(userId, key);

    private static TresorDb Open(string userId, byte[] key)
    {
        var vault = VaultOf(userId, key);
        if (vault == null) return new TresorDb(DbPath(userId), key, BlobDir(userId));
        var db = new TresorDb(PrepareWork(userId, vault), key, BlobDir(userId), _ => new DmOffice.CryptomatorTresorStorage(vault));
        if (vault.DirIdOf("/.dmoffice") != null && vault.CipherFileOf(DbInVault) != null) RememberInternal(userId, vault);
        // Stand der Arbeitskopie beim Öffnen: was danach geändert wird, geht beim Zurückschreiben in den Tresor
        Flushed[userId] = LastWrite(userId);
        return db;
    }

    // ---------------- Interne Daten im Tresor (Stufe 4) ----------------

    /// <summary>Die Tresor-Datenbank im Cryptomator-Tresor (bleibt SQLCipher, docs/tresor-cryptomator-konzept.md, 3.1).</summary>
    public const string DbInVault = "/.dmoffice/tresor.db";

    /// <summary>Zurückschreiben, wenn so lange nichts an der Datenbank geändert wurde (Entscheidung 13). Tests stellen kürzer.</summary>
    public static TimeSpan WriteBackIdle { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Arbeitskopien, solange der Tresor offen ist (verschlüsselt wie die Datenbank selbst).</summary>
    public static string WorkDir(string userId) => Path.Combine(P.Users, userId + ".tresor.work");
    private static string WorkDb(string userId) => Path.Combine(WorkDir(userId), "tresor.db");

    /// <summary>Zuletzt zurückgeschriebener Stand je Person (Änderungszeit der Arbeitskopie).</summary>
    private static readonly ConcurrentDictionary<string, DateTime> Flushed = new();

    private static DateTime LastWrite(string userId)
    {
        var t = DateTime.MinValue;
        foreach (var f in new[] { WorkDb(userId), WorkDb(userId) + "-wal" })
            if (File.Exists(f) && File.GetLastWriteTimeUtc(f) is var w && w > t) t = w;
        return t;
    }

    /// <summary>
    /// Arbeitskopie bereitstellen: liegt noch eine da (Absturz, Neustart), ist sie der neueste Stand und wird bald zurückgeschrieben;
    /// sonst aus dem Tresor entschlüsseln; sonst die Datenbank von vor dem Cryptomator-Format übernehmen; sonst neu.
    /// </summary>
    private static string PrepareWork(string userId, Cryptomator.CryptomatorVault vault)
    {
        var work = WorkDb(userId);
        Directory.CreateDirectory(WorkDir(userId));
        if (File.Exists(work)) { Flushed[userId] = DateTime.MinValue; return work; }
        if (vault.DirIdOf("/.dmoffice") != null && vault.CipherFileOf(DbInVault) != null)
        {
            var tmp = work + ".tmp";
            using (var from = vault.OpenRead(DbInVault))
            using (var to = File.Create(tmp)) from.CopyTo(to);
            File.Move(tmp, work);
            return work;
        }
        foreach (var side in new[] { "", "-wal", "-shm" })
            if (File.Exists(DbPath(userId) + side)) File.Move(DbPath(userId) + side, work + side);
        return work;
    }

    /// <summary>Wo die internen Daten verschlüsselt liegen: Diese Einträge schreibt nur dmOffice selbst (FileStore).</summary>
    private static void RememberInternal(string userId, Cryptomator.CryptomatorVault vault)
    {
        if (UserDbOf != null) DmOffice.Dav.FileStore.RememberVaultInternal(UserDbOf(userId), [vault.NodeFor("/.dmoffice"), vault.NodeFor(DbInVault)]);
    }

    /// <summary>
    /// Arbeitskopie in den Tresor zurückschreiben: stimmiger Stand (VACUUM INTO, bleibt SQLCipher), atomar ersetzt. force: sofort
    /// (Schließen, Herunterfahren, vor Sicherungen), sonst nur nach <see cref="WriteBackIdle"/> ohne Änderung. true: nichts mehr offen.
    /// </summary>
    public static bool Flush(string userId, bool force)
    {
        if (!Dbs.TryGetValue(userId, out var db) || db.Files.Storage is not DmOffice.CryptomatorTresorStorage storage) return true;
        var last = LastWrite(userId);
        if (Flushed.TryGetValue(userId, out var done) && last <= done) return true;
        if (!force && Now() - last < WriteBackIdle) return false;
        var tmp = WorkDb(userId) + ".flush";
        try
        {
            if (File.Exists(tmp)) File.Delete(tmp);
            db.SnapshotTo(tmp);
            if (storage.Vault.DirIdOf("/.dmoffice") == null) storage.Vault.CreateDirectory("/.dmoffice");
            using (var s = File.OpenRead(tmp)) storage.Vault.WriteAsync(DbInVault, s).GetAwaiter().GetResult();
            RememberInternal(userId, storage.Vault);
            Flushed[userId] = last;
            return true;
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    /// <summary>Alle offenen Tresore: nach Leerlauf (Zeitgeber) bzw. sofort (Herunterfahren, vor Sicherungen).</summary>
    public static void FlushAll(bool force)
    {
        foreach (var user in Dbs.Keys.ToArray())
            try { Flush(user, force); } catch (Exception) { /* nächster Versuch beim nächsten Lauf, die Arbeitskopie bleibt */ }
    }

    /// <summary>Neue Wiederherstellungscodes, die alten verfallen.</summary>
    public static List<string> NewCodes(string userId, byte[] key)
    {
        var codes = Enumerable.Range(0, 10).Select(_ => RandomCode(20)).ToList();
        Update(userId, m =>
        {
            m.Wraps.RemoveAll(x => x.Kind == TresorWrap.Code);
            foreach (var c in codes) m.Wraps.Add(SecretWrap(TresorWrap.Code, Encoding.UTF8.GetBytes(c), key));
        });
        return codes.Select(c => Group(c, 5)).ToList();
    }

    /// <summary>Neuer Wiederherstellungsschlüssel (der alte gilt danach nicht mehr) - etwa wenn das Blatt in falsche Hände kam.</summary>
    public static string NewRecoveryKey(string userId, byte[] key)
    {
        var recovery = RandomCode(40);
        Update(userId, m =>
        {
            m.RecoveryKey = Convert.ToBase64String(Seal(key, Encoding.UTF8.GetBytes(recovery), "tresor-recovery"));
            m.Wraps.RemoveAll(x => x.Kind == TresorWrap.Recovery);
            m.Wraps.Add(SecretWrap(TresorWrap.Recovery, Encoding.UTF8.GetBytes(recovery), key));
        });
        return Group(recovery, 5);
    }

    /// <summary>Wiederherstellungsschlüssel erneut zum Drucken (nur mit offenem Tresor).</summary>
    public static string RecoveryKey(string userId, byte[] key) =>
        Group(Encoding.UTF8.GetString(Unseal(key, Convert.FromBase64String(Meta(userId)!.RecoveryKey), "tresor-recovery")
            ?? throw new InvalidOperationException("Der Wiederherstellungsschlüssel lässt sich nicht lesen.")), 5);

    public static int CodesLeft(string userId) => Meta(userId)?.Wraps.Count(x => x.Kind == TresorWrap.Code) ?? 0;

    public static void ChangeSettings(string userId, string? autoClose, bool? closeOnAdminReset, bool? closeOnScreenLock, bool? dav = null)
    {
        if (dav != null) Update(userId, m => m.Settings.Dav = dav.Value);
        if (autoClose != null && !TresorSettings.AutoCloseValues.Contains(autoClose)) throw new ArgumentException("Unbekannte Zeit.");
        Update(userId, m =>
        {
            if (autoClose != null) m.Settings.AutoClose = autoClose;
            if (closeOnScreenLock != null) m.Settings.CloseOnScreenLock = closeOnScreenLock.Value;
            // Ohne „nie“ darf sich kein Gerät den Tresor mehr merken
            if (m.Settings.AutoClose != TresorSettings.Never) m.Wraps.RemoveAll(x => x.Kind == TresorWrap.Remember);
        });
    }

    // ---------------- Offen je Gerät ----------------

    private sealed class OpenDevice
    {
        public required string UserId;
        public required string Name;
        public DateTime At, Used;
        public bool App;
    }

    /// <summary>Schlüssel der Personen mit offenem Tresor (nur im Arbeitsspeicher).</summary>
    private static readonly ConcurrentDictionary<string, byte[]> Keys = new();
    private static readonly ConcurrentDictionary<string, OpenDevice> Devices = new();
    private static readonly ConcurrentDictionary<string, TresorDb> Dbs = new();

    /// <summary>Kennung dieses Browsers (Cookie dm_tresor); create: anlegen, wenn es noch keine gibt.</summary>
    public static string? BrowserToken(HttpContext http, bool create)
    {
        if (http.Request.Cookies[Cookie] is { Length: >= 20 } c) return c;
        if (!create) return null;
        var token = E2e.Base64Url(RandomNumberGenerator.GetBytes(32));
        http.Response.Cookies.Append(Cookie, token, new CookieOptions
        {
            HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = http.Request.IsHttps, Path = "/", IsEssential = true,
            Expires = DateTimeOffset.UtcNow.AddYears(1),
        });
        return token;
    }

    /// <summary>Zugang: App-Gerät (App-Passwort) oder Browser (Cookie). Null: dieser Zugang kann nicht offen sein.</summary>
    public static string? AccessOf(HttpContext http)
    {
        var id = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(id)) return null;
        if (Auth.DeviceOf(http) is { } device)
        {
            // Hat das Gerät einen Schlüssel für den Umschlag, gilt der Tresor nur über den Umschlag oder ein daraus geholtes
            // Ticket - das App-Passwort allein (etwa mitgelesen) reicht dann nicht
            if (!http.User.HasClaim(c => c.Type is Auth.EnvelopeClaim or DmOffice.Tickets.Claim)
                && E2e.HasDevice(http.RequestServices.GetRequiredService<Spaces>().Get(id).Db, device)) return null;
            return "a:" + id + ":" + device;
        }
        var c = http.Request.Cookies[Cookie];
        return c is { Length: >= 20 } ? "c:" + id + ":" + c : null;
    }

    // ---------------- Frisch geöffnet ----------------
    // Heikles (Passwort ändern, Wiederherstellungsschlüssel, neue Codes, neue Wege zum Öffnen) nur kurz nach einem Öffnen mit einem
    // Geheimnis, das die Person selbst eingibt bzw. bestätigt: Passwort, Code, Passkey - nicht mit einem nur gemerkten Gerät.

    /// <summary>So lange gilt ein Öffnen als frisch.</summary>
    public static TimeSpan FreshFor { get; set; } = TimeSpan.FromMinutes(10);
    public const string ByPassword = "password", ByCode = "code", ByPasskey = "passkey";
    private static readonly ConcurrentDictionary<string, (DateTime At, string How)> Recent = new();

    /// <summary>Nach dem Öffnen vermerken, womit (nur Passwort, Code oder Passkey zählen; null: nicht frisch, etwa gemerktes Gerät).</summary>
    public static void MarkFresh(HttpContext http, string? how)
    {
        if (AccessOf(http) is not { } a) return;
        if (how == null) Recent.TryRemove(a, out _); else Recent[a] = (Now(), how);
    }

    /// <summary>Frisch geöffnet (mit Passwort, Code oder Passkey) - bzw. nur mit einem Code (nach dem man das Passwort neu setzt)?</summary>
    public static bool OpenedFresh(HttpContext http, string? how = null) =>
        IsOpen(http) && AccessOf(http) is { } a && Recent.TryGetValue(a, out var r) && Now() - r.At < FreshFor && (how == null || r.How == how);

    /// <summary>Art eines gemerkten Wegs (Passkey zählt als frisch, gemerktes Gerät und Fingerabdruck in der App nicht).</summary>
    public static string? KindOfWrap(string userId, string wrapId) => Meta(userId)?.Wraps.FirstOrDefault(x => x.Id == wrapId)?.Kind;

    private static bool Fresh(OpenDevice d, DateTime now) =>
        Keys.ContainsKey(d.UserId) && (Meta(d.UserId)?.Settings.Idle is not TimeSpan idle || now - d.Used < idle);

    /// <summary>Abgelaufenes aufräumen; Personen ohne offenes Gerät verlieren den Schlüssel.</summary>
    /// <summary>Abgelaufene Geräte schließen - auch ohne Tresor-Aufruf (<see cref="TresorSweep"/> jede Minute).</summary>
    public static void SweepNow() => Sweep();

    private static void Sweep()
    {
        var now = Now();
        foreach (var (k, d) in Devices.ToArray())
            if (!Fresh(d, now)) Devices.TryRemove(k, out _);
        foreach (var user in Keys.Keys.ToArray())
            if (!Devices.Values.Any(d => d.UserId == user)) Drop(user);
    }

    private static void Drop(string userId)
    {
        // Erst zurückschreiben; gelingt das, ist die Arbeitskopie überflüssig - sonst bleibt sie für das nächste Öffnen
        var flushed = false;
        try { flushed = Flush(userId, force: true); } catch (Exception) { }
        if (Dbs.TryRemove(userId, out var db))
        {
            var vaultMode = db.Files.Storage is DmOffice.CryptomatorTresorStorage;
            db.Dispose();
            if (vaultMode && flushed)
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                try { if (Directory.Exists(WorkDir(userId))) Directory.Delete(WorkDir(userId), recursive: true); } catch (IOException) { }
                Flushed.TryRemove(userId, out _);
            }
        }
        if (Keys.TryRemove(userId, out var key))
        {
            CryptographicOperations.ZeroMemory(key);
            if (Closed != null)
                foreach (var h in Closed.GetInvocationList().Cast<Action<string>>()) try { h(userId); } catch (Exception) { }
        }
    }

    /// <summary>Ein eigener Schlüssel aus dem Tresor-Schlüssel (HKDF) für einen Zweck - null, wenn der Tresor nirgends offen ist.</summary>
    public static byte[]? SubKey(string userId, string purpose)
    {
        if (!OpenAnywhere(userId) || !Keys.TryGetValue(userId, out var key)) return null;
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, key, 32, [], Encoding.UTF8.GetBytes(purpose));
    }

    /// <summary>Öffnen für diesen Zugang mit einem geprüften Schlüssel; übernimmt dabei, was bei geschlossenem Tresor ankam.</summary>
    public static void Grant(HttpContext http, string userId, byte[] key, string? name)
    {
        var now = Now();
        Keys.AddOrUpdate(userId, key, (_, old) => { if (!ReferenceEquals(old, key)) CryptographicOperations.ZeroMemory(old); return key; });
        var device = Auth.DeviceOf(http);
        string access;
        if (device != null) access = "a:" + userId + ":" + device;
        else
        {
            var token = http.Request.Cookies[Cookie] is { Length: >= 20 } c ? c : E2e.Base64Url(RandomNumberGenerator.GetBytes(32));
            access = "c:" + userId + ":" + token;
            http.Response.Cookies.Append(Cookie, token, new CookieOptions
            {
                HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = http.Request.IsHttps, Path = "/", IsEssential = true,
                Expires = DateTimeOffset.UtcNow.AddYears(1),
            });
        }
        Devices[access] = new OpenDevice { UserId = userId, Name = string.IsNullOrWhiteSpace(name) ? (device != null ? "App" : "Browser") : name.Trim()[..Math.Min(name.Trim().Length, 60)],
            At = now, Used = now, App = device != null };
        // Erst Altes umziehen (Gesichter von vor dem Tresor), dann Neues übernehmen - sonst kämen sich die Kennungen in die Quere
        if (Opened != null && DbOf(userId) is { } opened)
            foreach (var h in Opened.GetInvocationList().Cast<Action<string, TresorDb>>()) try { h(userId, opened); } catch (Exception) { }
        // Übernehmen darf das Öffnen nie verhindern - was nicht geht, bleibt liegen
        try { Absorb(userId); } catch (Exception) { }
    }

    /// <summary>Ist der Tresor für diesen Zugang offen? touch: zählt als Nutzung.</summary>
    public static bool IsOpen(HttpContext http, bool touch = true)
    {
        Sweep();
        if (AccessOf(http) is not { } access || !Devices.TryGetValue(access, out var d)) return false;
        if (touch) d.Used = Now();
        return true;
    }

    /// <summary>Offen auf irgendeinem Gerät der Person (notizi-Abgleich über WebDAV, Übernahme im Hintergrund).</summary>
    public static bool OpenAnywhere(string userId)
    {
        Sweep();
        return Keys.ContainsKey(userId);
    }

    /// <summary>Datenbank des Tresors für diese Anfrage - geschlossen: <see cref="TresorClosedException"/>.</summary>
    public static TresorDb Db(HttpContext http)
    {
        var userId = http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new UnauthorizedAccessException();
        if (!IsOpen(http)) throw new TresorClosedException();
        return DbOf(userId) ?? throw new TresorClosedException();
    }

    /// <summary>Datenbank, wenn der Tresor irgendwo offen ist (ohne Gerät: WebDAV).</summary>
    public static TresorDb? DbOf(string userId)
    {
        if (!OpenAnywhere(userId) || !Keys.TryGetValue(userId, out var key) || Upgrading.ContainsKey(userId)) return null;
        return Dbs.GetOrAdd(userId, id => Open(id, key));
    }

    /// <summary>Schlüssel für Änderungen an den Hüllen (Passwort, Codes, Geräte) - nur mit offenem Tresor auf diesem Gerät.</summary>
    public static byte[] KeyFor(HttpContext http)
    {
        var userId = http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new UnauthorizedAccessException();
        if (!IsOpen(http) || !Keys.TryGetValue(userId, out var key)) throw new TresorClosedException();
        return key;
    }

    /// <summary>Schließen auf diesem Zugang (Abmelden, „Tresor schließen“).</summary>
    public static void Close(HttpContext http)
    {
        if (AccessOf(http) is { } access) Devices.TryRemove(access, out _);
        Sweep();
    }

    /// <summary>Ein anderes Gerät schließen (Liste der Geräte mit offenem Tresor).</summary>
    public static bool CloseDevice(string userId, string deviceId)
    {
        var hit = Devices.FirstOrDefault(x => x.Value.UserId == userId && DeviceId(x.Key) == deviceId);
        if (hit.Key == null) return false;
        Devices.TryRemove(hit.Key, out _);
        Sweep();
        return true;
    }

    public static void CloseAll(string userId)
    {
        foreach (var (k, d) in Devices.ToArray()) if (d.UserId == userId) Devices.TryRemove(k, out _);
        Drop(userId);
    }

    /// <summary>Kennung eines offenen Geräts für die Liste - nicht der Zugang selbst (der ist geheim).</summary>
    private static string DeviceId(string access) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(access)))[..16].ToLowerInvariant();

    public sealed record DeviceInfo(string Id, string Name, DateTime At, DateTime Used, bool App, bool Mine);

    public static List<DeviceInfo> OpenDevices(HttpContext http, string userId)
    {
        Sweep();
        var mine = AccessOf(http);
        return Devices.Where(x => x.Value.UserId == userId).OrderByDescending(x => x.Value.Used)
            .Select(x => new DeviceInfo(DeviceId(x.Key), x.Value.Name, x.Value.At, x.Value.Used, x.Value.App, x.Key == mine)).ToList();
    }

    /// <summary>
    /// Verwaltung setzt Passwort zurück oder schaltet Zwei-Faktor ab: schließen auf allen Geräten (Vorgabe), gemerkte Geräte und
    /// Fingerabdruck vergessen - wieder öffnen nur mit Tresor-Passwort oder Code.
    /// </summary>
    public static bool AdminReset(string userId)
    {
        if (Meta(userId) is null) return false;
        Update(userId, x => x.Wraps.RemoveAll(w => w.Kind is TresorWrap.Remember or TresorWrap.Biometric));
        CloseAll(userId);
        return true;
    }

    /// <summary>Tresor samt Inhalt endgültig löschen (Person selbst mit Passwort oder Konto gelöscht).</summary>
    public static void Delete(string userId, bool systemVault = true)
    {
        CloseAll(userId);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        lock (Gate(userId))
        {
            foreach (var f in new[] { MetaPath(userId), DbPath(userId), DbPath(userId) + "-wal", DbPath(userId) + "-shm" })
                if (File.Exists(f)) File.Delete(f);
            if (Directory.Exists(BlobDir(userId))) Directory.Delete(BlobDir(userId), recursive: true);
            if (Directory.Exists(InboxDir(userId))) Directory.Delete(InboxDir(userId), recursive: true);
            if (Directory.Exists(NotesDir(userId))) Directory.Delete(NotesDir(userId), recursive: true);
            if (Directory.Exists(WorkDir(userId))) Directory.Delete(WorkDir(userId), recursive: true);
            // System-Tresor in der Dateiablage (endgültig, ohne Papierkorb)
            if (systemVault && UserDbOf?.Invoke(userId) is { } udb)
                using (DmOffice.Dav.FileStore.SystemAccess()) DmOffice.Dav.FileStore.Delete(udb, DmOffice.Dav.FileStore.SystemVault);
        }
    }

    /// <summary>Belegung für das Kontingent - 0 ohne Tresor oder ohne eingerichtete Ablage (Tests).</summary>
    public static long SizeOf(string userId) => Paths == null || !Exists(userId) ? 0 : Size(userId);

    /// <summary>Belegung: Datenbank, Dateien und noch nicht Übernommenes.</summary>
    public static long Size(string userId)
    {
        long n = 0;
        foreach (var f in new[] { DbPath(userId), DbPath(userId) + "-wal" }) if (File.Exists(f)) n += new FileInfo(f).Length;
        foreach (var d in new[] { BlobDir(userId), InboxDir(userId), NotesDir(userId) })
            if (Directory.Exists(d)) n += Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        return n;
    }

    // ---------------- Sicherung ----------------

    /// <summary>Ordner der Tresor-Dateien in einem Sicherungsstand: tresor-blobs/&lt;Benutzer&gt;/… (zurück nach blobs/&lt;Benutzer&gt;/tresor/…).</summary>
    public const string BackupBlobs = "tresor-blobs";

    /// <summary>Notizen der notizi-Datenbanken im Tresor sichern (Person, Zielordner) - offene als stimmige Kopie (TresorNotes).
    /// Ohne Eintrag: die Dateien, wie sie sind.</summary>
    public static Action<string, string>? NotesBackup { get; set; }

    /// <summary>
    /// Tresor in einen Sicherungsstand (Core-Backup): Hüllen, Datenbank, Abgelegtes und Dateien - alles bleibt mit dem
    /// Tresor-Schlüssel verschlüsselt, Wiederherstellen braucht das Tresor-Passwort oder einen Code. Offen: stimmige Kopie mit dem
    /// Schlüssel im Arbeitsspeicher; zu: die Dateien, wie sie sind (dann schreibt niemand daran). Die Dateien gehen mit, weil der
    /// Storage Extender den Tresor nicht spiegelt.
    /// </summary>
    public static void Backup(string userId, string stage)
    {
        if (!Exists(userId)) return;
        // Stimmiger Stand im Cryptomator-Tresor vor jeder Sicherung
        try { Flush(userId, force: true); } catch (Exception) { }
        var users = Path.Combine(stage, "users");
        Directory.CreateDirectory(users);
        lock (Gate(userId)) File.Copy(MetaPath(userId), Path.Combine(users, userId + ".tresor.json"), overwrite: true);
        var db = Path.Combine(users, userId + ".tresor.db");
        if (DbOf(userId) is { } open) open.SnapshotTo(db);
        else
            foreach (var side in new[] { "", "-wal" })
                if (File.Exists(DbPath(userId) + side)) File.Copy(DbPath(userId) + side, db + side, overwrite: true);
        Copy(InboxDir(userId), Path.Combine(users, userId + ".tresor.in"));
        // Notizen der Datenbanken im Tresor samt noch nicht Abgeglichenem - verschlüsselt mit einem Schlüssel aus dem Tresor-Schlüssel
        var notes = Path.Combine(users, userId + ".tresor.notes");
        if (NotesBackup is { } backupNotes) backupNotes(userId, notes); else Copy(NotesDir(userId), notes);
        Copy(BlobDir(userId), Path.Combine(stage, BackupBlobs, userId));
    }

    private static void Copy(string from, string to)
    {
        if (!Directory.Exists(from)) return;
        foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            if (f.EndsWith(".tmp")) continue;
            var target = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(f, target, overwrite: true);
        }
    }

    /// <summary>Wiederherstellen: Ziel einer Tresor-Datei aus dem Sicherungsstand (relativer Pfad) - null, wenn es keine ist.</summary>
    public static string? RestoreTarget(DataPaths paths, string rel)
    {
        var parts = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (parts.Length < 3 || parts[0] != BackupBlobs) return null;
        if (parts[1].Length != 32 || !parts[1].All(Uri.IsHexDigit) || parts.Skip(2).Any(p => p is "" or "." or "..")) return "";
        return Path.Combine([paths.UserBlobs(parts[1]), "tresor", .. parts[2..]]);
    }

    // ---------------- Neues bei geschlossenem Tresor ----------------

    /// <summary>
    /// Neues ablegen, ohne den Tresor öffnen zu können: verschlüsselt mit dem öffentlichen Schlüssel (ECDH P-256, HKDF, AES-GCM).
    /// Lesen kann es erst der geöffnete Tresor. Ist er gerade offen, wird es gleich übernommen.
    /// </summary>
    public static void Drop(string userId, string kind, byte[] payload)
    {
        var m = Meta(userId) ?? throw new InvalidOperationException("Es ist kein Tresor eingerichtet.");
        using var mine = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var theirs = ECDiffieHellman.Create();
        theirs.ImportSubjectPublicKeyInfo(Convert.FromBase64String(m.PublicKey), out _);
        var eph = mine.ExportSubjectPublicKeyInfo();
        var key = Hkdf(mine.DeriveRawSecretAgreement(theirs.PublicKey), eph, "tresor-drop");
        var plain = Encoding.UTF8.GetBytes(kind).Concat(new byte[] { 0 }).Concat(payload).ToArray();
        var sealedData = Seal(key, plain, "tresor-drop");
        var dir = InboxDir(userId);
        Directory.CreateDirectory(dir);
        // Die Art steht auch im Namen (nicht geheim): so zählt etwa das DMS nur seine eigenen wartenden Dateien
        var name = Now().Ticks.ToString("D20") + "-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant() + "." + kind;
        using (var f = File.Create(Path.Combine(dir, name + ".tmp")))
        {
            f.Write(BitConverter.GetBytes((ushort)eph.Length));
            f.Write(eph);
            f.Write(sealedData);
        }
        File.Move(Path.Combine(dir, name + ".tmp"), Path.Combine(dir, name + ".bin"));
        if (OpenAnywhere(userId)) try { Absorb(userId); } catch (Exception) { }
    }

    /// <summary>Wartet etwas auf Übernahme? Mit kind nur diese Art (ältere Einträge ohne Art im Namen zählen dann nicht).</summary>
    public static int Pending(string userId, string? kind = null) =>
        Directory.Exists(InboxDir(userId)) ? Directory.EnumerateFiles(InboxDir(userId), kind == null ? "*.bin" : "*." + kind + ".bin").Count() : 0;

    /// <summary>Abgelegtes übernehmen (in Reihenfolge); unbekannte Arten bleiben liegen, bis es einen Übernehmer gibt.</summary>
    public static int Absorb(string userId)
    {
        var dir = InboxDir(userId);
        if (!Directory.Exists(dir) || !Keys.TryGetValue(userId, out var key) || DbOf(userId) is not { } db) return 0;
        var m = Meta(userId)!;
        var pkcs8 = Unseal(key, Convert.FromBase64String(m.PrivateKey), "tresor-private") ?? throw new CryptographicException("Privater Schlüssel passt nicht.");
        using var own = ECDiffieHellman.Create();
        own.ImportPkcs8PrivateKey(pkcs8, out _);
        CryptographicOperations.ZeroMemory(pkcs8);
        // Der öffentliche Schlüssel steht offen in tresor.json - passt er nicht zum (verschlüsselten) privaten, hat ihn jemand
        // getauscht: dann hätten andere die Ablagen lesen können. Nichts übernehmen.
        if (!CryptographicOperations.FixedTimeEquals(own.ExportSubjectPublicKeyInfo(), Convert.FromBase64String(m.PublicKey))) return 0;
        int n = 0;
        lock (Gate(userId + ":in"))
            foreach (var file in Directory.EnumerateFiles(dir, "*.bin").Order(StringComparer.Ordinal).ToList())
            {
                var data = File.ReadAllBytes(file);
                var len = BitConverter.ToUInt16(data, 0);
                var eph = data.AsSpan(2, len).ToArray();
                using var theirs = ECDiffieHellman.Create();
                theirs.ImportSubjectPublicKeyInfo(eph, out _);
                var plain = Unseal(Hkdf(own.DeriveRawSecretAgreement(theirs.PublicKey), eph, "tresor-drop"), data[(2 + len)..], "tresor-drop");
                if (plain == null) continue;   // beschädigt: liegen lassen, nicht verlieren
                var zero = Array.IndexOf(plain, (byte)0);
                var kind = Encoding.UTF8.GetString(plain, 0, zero);
                if (!Absorbers.TryGetValue(kind, out var take)) continue;
                // Ein Eintrag, den der Übernehmer nicht verarbeiten kann, blockiert die übrigen nicht: beiseite (.bad), nicht verloren
                try { take(db, plain[(zero + 1)..]); }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    try { File.Move(file, Path.ChangeExtension(file, ".bad"), overwrite: true); } catch (IOException) { }
                    continue;
                }
                File.Delete(file);
                n++;
            }
        return n;
    }

    /// <summary>Nur für Tests: alles zu.</summary>
    internal static void Reset() { foreach (var u in Keys.Keys.ToArray()) Drop(u); Devices.Clear(); Recent.Clear(); }
}

/// <summary>„Automatisch schließen“ greift auch, wenn niemand den Tresor mehr anfragt: jede Minute nachsehen.</summary>
public sealed class TresorSweep : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(ct))
            try { Tresor.SweepNow(); } catch (Exception e) when (e is not OutOfMemoryException) { /* nächste Minute wieder */ }
    }
}
