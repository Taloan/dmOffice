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

    /// <summary>Wechselt bei Passwort, Rolle und 2FA - aeltere Anmelde-Cookies gelten dann nicht mehr.</summary>
    public string Stamp { get; set; } = "";

    public bool TwoFactor => TotpSecret != null;

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
        File.Delete(_vault.KeyFile);
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

    public static bool ValidLogin(string login) =>
        login.Length is >= 2 and <= 40 && login.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');

    public UserRecord Create(string login, string displayName, string password, bool isAdmin)
    {
        login = Normalize(login);
        if (!ValidLogin(login)) throw new ArgumentException("Anmeldename: 2–40 Zeichen, nur a–z, 0–9, Punkt, Bindestrich, Unterstrich.");
        if (password.Length < 8) throw new ArgumentException("Passwort: mindestens 8 Zeichen.");
        lock (_lock)
        {
            if (_users.Any(u => u.Login == login)) throw new ArgumentException("Den Anmeldenamen gibt es schon.");
            var user = new UserRecord
            {
                Login = login,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? login : displayName.Trim(),
                PasswordHash = Passwords.Hash(password),
                IsAdmin = isAdmin,
                WrappedKey = _vault.Wrap(RandomNumberGenerator.GetBytes(32))
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
        if (!ValidLogin(login)) throw new ArgumentException("Anmeldename: 2–40 Zeichen, nur a–z, 0–9, Punkt, Bindestrich, Unterstrich.");
        if (password.Length < 8) throw new ArgumentException("Passwort: mindestens 8 Zeichen.");
        lock (_lock)
        {
            if (_users.Count != 0) return null;
            var user = new UserRecord
            {
                Login = login,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? login : displayName.Trim(),
                PasswordHash = Passwords.Hash(password),
                IsAdmin = true,
                WrappedKey = _vault.Wrap(RandomNumberGenerator.GetBytes(32))
            };
            _users.Add(user);
            Save();
            return Copy(user);
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
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_users, Json));
        File.Move(tmp, _path, overwrite: true);
    }

    private static UserRecord Copy(UserRecord u) => JsonSerializer.Deserialize<UserRecord>(JsonSerializer.Serialize(u))!;
}

public static class Passwords
{
    private const int Iterations = 210_000;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    // Hoechstens so viele Passwortpruefungen ohne Anmeldung gleichzeitig: jede rechnet 210 000 PBKDF2-Runden.
    // Ohne Grenze legte eine Flut von Anmeldeversuchen alle Kerne lahm - auch fuer die Angemeldeten.
    private static readonly SemaphoreSlim AnonGate = new(Math.Max(1, Environment.ProcessorCount / 2));

    /// <summary>Wie <see cref="Verify"/>, aber fuer Wege ohne Anmeldung (Login, Login-Flow, Link-Passwort): begrenzt
    /// gleichzeitig. Null, wenn der Server zu lange beschaeftigt ist (dann 503 statt Warteschlange ohne Ende).</summary>
    public static async Task<bool?> VerifyLimitedAsync(string password, string? stored, CancellationToken ct = default)
    {
        if (!await AnonGate.WaitAsync(TimeSpan.FromSeconds(15), ct)) return null;
        try { return Verify(password, stored); }
        finally { AnonGate.Release(); }
    }

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
