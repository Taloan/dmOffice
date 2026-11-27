using System.Security.Cryptography;
using System.Text.Json;

namespace DmOffice.Core;

public sealed class UserRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>Anmeldename, klein, ohne Leerzeichen - steht auch in den
    /// DAV-Adressen (/dav/calendars/&lt;login&gt;/).</summary>
    public string Login { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public bool IsAdmin { get; set; }
    /// <summary>Schluessel der Benutzerdatenbank, eingepackt mit dem Hauptschluessel.</summary>
    public string WrappedKey { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.UtcNow;

    /// <summary>TOTP-Geheimnis, eingepackt mit dem Hauptschluessel. Null: keine Zwei-Faktor-Anmeldung.</summary>
    public string? TotpSecret { get; set; }
    /// <summary>Waehrend der Einrichtung - gilt erst, wenn ein Code dazu bestaetigt ist.</summary>
    public string? TotpPending { get; set; }
    /// <summary>Zuletzt angenommenes Zeitfenster - derselbe Code gilt nicht zweimal.</summary>
    public long TotpLastStep { get; set; }
    /// <summary>Hashes der noch unbenutzten Wiederherstellungscodes.</summary>
    public List<string> RecoveryCodes { get; set; } = new();

    /// <summary>Speicher fuer Dateien in MB. Null: Vorgabe des Servers, 0: unbegrenzt.</summary>
    public long? FileQuotaMb { get; set; }

    /// <summary>
    /// Dateiablage abgeschaltet (Admin): kein Bereich "Dateien", WebDAV nur noch fuer den
    /// Abgleichordner von notizi - die Notizen gehen weiter.
    /// </summary>
    public bool FilesOff { get; set; }

    /// <summary>Pukki darf fuer diesen Benutzer die KI fragen (Version 2, vom Admin freigegeben).</summary>
    public bool AiAllowed { get; set; }


    /// <summary>Wechselt bei Passwort, Rolle und 2FA - aeltere Anmelde-Cookies gelten dann nicht mehr.</summary>
    public string Stamp { get; set; } = "";

    public bool TwoFactor => TotpSecret != null;

    /// <summary>Verknüpftes Konto beim Identitätsanbieter: „Aussteller|sub“ (Oidc); null: keins</summary>
    public string? OidcSubject { get; set; }

    /// <summary>Von der Verwaltung vorgemerkt: Anmeldename oder bestätigte E-Mail-Adresse beim Identitätsanbieter. Bei der ersten
    /// Anmeldung über den Anbieter mit diesem Namen wird das Konto verknüpft (danach gilt nur noch <see cref="OidcSubject"/>).</summary>
    public string? OidcExpected { get; set; }

    /// <summary>Über den Identitätsanbieter angelegt, noch ohne eigenes Passwort (das zufällige kennt niemand): ein eigenes lässt sich
    /// ohne das bisherige festlegen; die Verknüpfung lässt sich erst danach lösen.</summary>
    public bool NoOwnPassword { get; set; }

    /// <summary>Von der Verwaltung gesperrt (Vorstufe zum Löschen): keine Anmeldung, keine App-Passwörter, keine Hintergrundarbeit;
    /// die Daten bleiben. Wirkt nur mit der Kennung „userlock“ (<see cref="LockedOut"/>).</summary>
    public bool Locked { get; set; }

    /// <summary>Gesperrt und die Sperre gilt in dieser Version.</summary>
    public bool LockedOut => Locked && Features.On(Features.UserLock);

    public void NewStamp() => Stamp = Guid.NewGuid().ToString("N");
}

/// <summary>
/// users.json: wer sich anmelden darf. Nur Namen, Passwort-Hashes und
/// eingepackte Schluessel - alles andere liegt in der verschluesselten
/// Datenbank des Benutzers.
/// </summary>
public sealed class UserStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _path;
    private readonly Vault _vault;
    private readonly object _lock = new();
    private List<UserRecord> _users;

    public UserStore(DataPaths paths, Vault vault)
    {
        _path = Path.Combine(paths.Root, "users.json");
        _vault = vault;
        _users = File.Exists(_path)
            ? JsonSerializer.Deserialize<List<UserRecord>>(File.ReadAllText(_path), Json) ?? new()
            : new();
        Rewrap();
    }

    /// <summary>Gilt jetzt der Schluessel aus der Umgebung und lag vorher
    /// einer in der Datei: alle Benutzerschluessel umpacken, Datei loeschen.</summary>
    private void Rewrap()
    {
        if (_vault.Previous == null) return;
        foreach (var u in _users)
        {
            u.WrappedKey = RewrapOne(u.WrappedKey)!;
            // Das TOTP-Geheimnis ist ebenso eingepackt - sonst scheitert danach jede Anmeldung mit Code
            u.TotpSecret = RewrapOne(u.TotpSecret);
            u.TotpPending = RewrapOne(u.TotpPending);
        }
        Save();
        // Die Schluesseldatei loescht der Tresor selbst, wenn alles umgepackt ist (Vault.Migrate)
    }

    private string? RewrapOne(string? wrapped)
    {
        if (wrapped == null) return null;
        try { return _vault.Wrap(Vault.Unwrap(_vault.Previous!, wrapped)); }
        catch (CryptographicException) { return wrapped; /* schon mit dem neuen eingepackt */ }
    }

    public bool Empty { get { lock (_lock) return _users.Count == 0; } }

    public List<UserRecord> All() { lock (_lock) return _users.Select(Copy).ToList(); }

    public UserRecord? ById(string id) { lock (_lock) return _users.FirstOrDefault(u => u.Id == id) is { } u ? Copy(u) : null; }

    public UserRecord? ByLogin(string login)
    {
        login = Normalize(login);
        lock (_lock) return _users.FirstOrDefault(u => u.Login == login) is { } u ? Copy(u) : null;
    }

    public static string Normalize(string login) => login.Trim().ToLowerInvariant();

    /// <summary>Hoechstlaenge des Anzeigenamens - er steht in Listen, Chat und Kopfzeilen.</summary>
    public const int MaxDisplayName = 100;

    private static string DisplayNameOf(string displayName, string login)
    {
        if (displayName?.Trim().Length > MaxDisplayName) throw new ArgumentException($"Der Anzeigename ist zu lang (höchstens {MaxDisplayName} Zeichen).");
        return string.IsNullOrWhiteSpace(displayName) ? login : displayName.Trim();
    }

    // Am Anfang ein Buchstabe oder eine Ziffer, keine zwei Punkte hintereinander: „..“ oder „.x“ ergäben als WebDAV-Adresse
    // (/dav/files/<login>/) einen Pfad, den Programme als „eine Ebene höher“ lesen
    public static bool ValidLogin(string login) =>
        login.Length is >= 2 and <= 40 && char.IsAsciiLetterOrDigit(login[0]) && !login.Contains("..")
        && login.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');

    /// <summary>Hoechstlaenge eines Passworts - länger bringt keine Sicherheit, nur Rechenzeit beim Prüfen.</summary>
    public const int MaxPassword = 1000;

    /// <summary>Was an einem neuen Passwort (mindestens 8 Zeichen schon geprüft) nicht passt, sonst null.</summary>
    public static string? PasswordProblem(string password) =>
        string.IsNullOrWhiteSpace(password) ? "Das Passwort braucht neben Leerzeichen auch andere Zeichen."
        : password.Length > MaxPassword ? $"Das Passwort ist zu lang (höchstens {MaxPassword} Zeichen)." : null;

    public UserRecord Create(string login, string displayName, string password, bool isAdmin)
    {
        login = Normalize(login);
        if (!ValidLogin(login)) throw new ArgumentException("Anmeldename: 2–40 Zeichen, nur a–z, 0–9, Punkt, Bindestrich, Unterstrich; am Anfang ein Buchstabe oder eine Ziffer.");
        if (password.Length < 8) throw new ArgumentException("Passwort: mindestens 8 Zeichen.");
        if (PasswordProblem(password) is { } weak) throw new ArgumentException(weak);
        lock (_lock)
        {
            if (_users.Any(u => u.Login == login)) throw new ArgumentException("Den Anmeldenamen gibt es schon.");
            var user = new UserRecord
            {
                Login = login,
                DisplayName = DisplayNameOf(displayName, login),
                PasswordHash = Passwords.Hash(password),
                IsAdmin = isAdmin,
                WrappedKey = _vault.Wrap(RandomNumberGenerator.GetBytes(32)),
            };
            _users.Add(user);
            Save();
            return Copy(user);
        }
    }

    /// <summary>Das erste Verwaltungskonto - Pruefung auf "noch leer" und Anlegen unter einer Sperre, damit nicht
    /// zwei gleichzeitige Anfragen zwei Admins erzeugen. Gibt null zurueck, wenn schon eingerichtet.</summary>
    public UserRecord? CreateFirstAdmin(string login, string displayName, string password)
    {
        login = Normalize(login);
        if (!ValidLogin(login)) throw new ArgumentException("Anmeldename: 2–40 Zeichen, nur a–z, 0–9, Punkt, Bindestrich, Unterstrich; am Anfang ein Buchstabe oder eine Ziffer.");
        if (password.Length < 8) throw new ArgumentException("Passwort: mindestens 8 Zeichen.");
        if (PasswordProblem(password) is { } weak) throw new ArgumentException(weak);
        lock (_lock)
        {
            if (_users.Count != 0) return null;
            var user = new UserRecord
            {
                Login = login,
                DisplayName = DisplayNameOf(displayName, login),
                PasswordHash = Passwords.Hash(password),
                IsAdmin = true,
                WrappedKey = _vault.Wrap(RandomNumberGenerator.GetBytes(32))
            };
            _users.Add(user);
            Save();
            return Copy(user);
        }
    }

    /// <summary>Nach erfolgreicher Anmeldung: Passwort-Hash mit weniger Runden als heute neu bilden (ohne neuen Stempel,
    /// andere Sitzungen bleiben angemeldet). Nur, wenn der Hash inzwischen nicht geändert wurde.</summary>
    public void UpgradeHash(UserRecord u, string password)
    {
        if (!Passwords.Outdated(u.PasswordHash) || string.IsNullOrEmpty(password)) return;
        var fresh = Passwords.Hash(password);
        lock (_lock)
        {
            var x = _users.FirstOrDefault(r => r.Id == u.Id);
            if (x == null || x.PasswordHash != u.PasswordHash) return;
            x.PasswordHash = fresh;
            Save();
        }
    }

    public void Update(string id, Action<UserRecord> change)
    {
        lock (_lock)
        {
            var u = _users.FirstOrDefault(x => x.Id == id) ?? throw new KeyNotFoundException();
            change(u);
            Save();
        }
    }

    public void Delete(string id)
    {
        lock (_lock)
        {
            _users.RemoveAll(u => u.Id == id);
            Save();
        }
    }

    public byte[] DbKey(UserRecord u) => _vault.Unwrap(u.WrappedKey);

    public string Wrap(byte[] secret) => _vault.Wrap(secret);
    public byte[] Unwrap(string wrapped) => _vault.Unwrap(wrapped);

    private void Save()
    {
        // [feature:cfgseal]
        ConfigSeal.Save(_path, JsonSerializer.Serialize(_users, Json), _vault);
        // [feature:cfgseal:else]
        // var tmp = _path + ".tmp";
        // File.WriteAllText(tmp, JsonSerializer.Serialize(_users, Json));
        // File.Move(tmp, _path, overwrite: true);
        // [/feature:cfgseal]
    }

    private static UserRecord Copy(UserRecord u) => JsonSerializer.Deserialize<UserRecord>(JsonSerializer.Serialize(u))!;
}

public static class Passwords
{
    /// <summary>Runden für neue Hashes. Ältere Hashes (210 000) gelten weiter und werden beim nächsten Anmelden nachgezogen
    /// (<see cref="Outdated"/>).</summary>
    public const int Iterations = 600_000;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    // Hoechstens so viele Passwortpruefungen ohne Anmeldung gleichzeitig: jede rechnet 600 000 PBKDF2-Runden.
    // Ohne Grenze legte eine Flut von Anmeldeversuchen alle Kerne lahm - auch fuer die Angemeldeten.
    private static readonly SemaphoreSlim AnonGate = new(Math.Max(1, Environment.ProcessorCount / 2));

    /// <summary>Wie <see cref="Verify"/>, aber fuer Wege ohne Anmeldung (Login, Login-Flow, Link-Passwort): begrenzt
    /// gleichzeitig. Null, wenn der Server zu lange beschaeftigt ist (dann 503 statt Warteschlange ohne Ende).</summary>
    public static async Task<bool?> VerifyLimitedAsync(string password, string? stored, CancellationToken ct = default)
    {
        if (!await AnonGate.WaitAsync(TimeSpan.FromSeconds(15), ct)) return null;
        try
        {
            var ok = Verify(password, stored);
            // Gleiche Rechenzeit wie ein aktueller Hash (auch der für unbekannte Namen): ein Konto mit altem Hash fällt nicht auf
            if (Rounds(stored) is int r and < Iterations) Rfc2898DeriveBytes.Pbkdf2("-", PadSalt, Iterations - r, HashAlgorithmName.SHA256, 32);
            return ok;
        }
        finally { AnonGate.Release(); }
    }

    private static readonly byte[] PadSalt = RandomNumberGenerator.GetBytes(16);

    private static int? Rounds(string? stored)
    {
        var parts = stored?.Split('$');
        return parts is { Length: 4 } && parts[0] == "pbkdf2" && int.TryParse(parts[1], out var r) ? r : null;
    }

    /// <summary>Hash mit weniger Runden als heute üblich: nach erfolgreicher Prüfung mit dem Passwort neu bilden.</summary>
    public static bool Outdated(string? stored) => Rounds(stored) is int r && r < Iterations;

    public static bool Verify(string password, string? stored)
    {
        var parts = stored?.Split('$');
        if (parts is not { Length: 4 } || parts[0] != "pbkdf2") return false;
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, int.Parse(parts[1]), HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>App-Passwoerter sind zufaellig (120 Bit) - ein schneller
    /// Hash reicht, und DAV-Programme schicken sie bei jeder Anfrage.</summary>
    public static string TokenHash(string token) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token.Replace("-", "").Replace(" ", "").ToLowerInvariant())));

    public static string NewToken()
    {
        const string alphabet = "abcdefghijkmnpqrstuvwxyz23456789";
        var bytes = RandomNumberGenerator.GetBytes(24);
        var chars = bytes.Select(b => alphabet[b % alphabet.Length]).ToArray();
        // 4 Gruppen à 6 - leichter abzutippen, Bindestriche zaehlen nicht
        return string.Join("-", Enumerable.Range(0, 4).Select(i => new string(chars, i * 6, 6)));
    }
}
