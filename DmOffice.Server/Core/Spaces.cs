using System.Collections.Concurrent;
using System.Security.Claims;

namespace DmOffice.Core;

/// <summary>Alles, was einem angemeldeten Benutzer gehoert.</summary>
public sealed class UserSpace
{
    public UserRecord User { get; private set; }
    public UserDb Db { get; }
    private readonly object _lock = new();
    private UserSettings? _settings;

    /// <summary>Einstellungen geaendert - Mail-Sync und Notizen lesen neu.</summary>
    public event Action? SettingsChanged;

    public UserSpace(UserRecord user, UserDb db)
    {
        User = user;
        Db = db;
    }

    internal void Refresh(UserRecord user) => User = user;

    public UserSettings Settings
    {
        get
        {
            lock (_lock)
            {
                _settings ??= Db.GetJson<UserSettings>("settings");
                // Kopie: Aenderungen gehen nur ueber UpdateSettings
                return System.Text.Json.JsonSerializer.Deserialize<UserSettings>(System.Text.Json.JsonSerializer.Serialize(_settings))!;
            }
        }
    }

    public void UpdateSettings(Action<UserSettings> change)
    {
        lock (_lock)
        {
            var s = Settings;
            change(s);
            Db.SetJson("settings", s);
            _settings = s;
        }
        SettingsChanged?.Invoke();
    }

    public MailAccount Account(string id) =>
        Settings.MailAccounts.FirstOrDefault(a => a.Id == id) ?? throw new KeyNotFoundException("Konto nicht gefunden.");

    public TimeZoneInfo Zone => DeviceZone.Value ?? Dav.Ics.Zone(Settings.TimeZone) ?? TimeZoneInfo.Utc;

    private static readonly AsyncLocal<TimeZoneInfo?> DeviceZone = new();

    private sealed class ZoneScope(TimeZoneInfo? before) : IDisposable
    {
        public void Dispose() => DeviceZone.Value = before;
    }

    /// <summary>Innerhalb des Blocks gilt die Zeitzone des Geräts statt der des Kontos (null: die des Kontos).</summary>
    public static IDisposable UseZone(TimeZoneInfo? zone)
    {
        var scope = new ZoneScope(DeviceZone.Value);
        DeviceZone.Value = zone;
        return scope;
    }
}

/// <summary>
/// Die Benutzerbereiche, einmal geoeffnet und dann gehalten - auch fuer die
/// Hintergrunddienste (Mail-Sync, Regeln, Notizen), die ohne Anmeldung laufen.
/// </summary>
public sealed class Spaces
{
    private readonly ConcurrentDictionary<string, Lazy<UserSpace>> _open = new();
    private readonly UserStore _users;
    private readonly DataPaths _paths;
    private readonly UpdateCenter? _update;

    public event Action<UserSpace>? Opened;

    public Spaces(UserStore users, DataPaths paths, UpdateCenter? update = null)
    {
        _users = users;
        _paths = paths;
        _update = update;
        if (update != null) update.IsOpen = IsOpen;
    }

    /// <summary>Ist die Datenbank des Benutzers geoeffnet - und damit auf dem neuesten Stand?</summary>
    public bool IsOpen(string userId) => _open.TryGetValue(userId, out var l) && l.IsValueCreated;

    public UserSpace Get(string userId)
    {
        var lazy = _open.GetOrAdd(userId, id => new Lazy<UserSpace>(() =>
        {
            var user = _users.ById(id) ?? throw new KeyNotFoundException("Benutzer gibt es nicht.");
            // Vorabpruefung: Platz, Sicherung, Freigabe - erst dann darf die Datenbank umgebaut werden
            var migrating = _update?.Gate(user) ?? false;
            UserDb db;
            try { db = new UserDb(_paths.UserDb(id), _users.DbKey(user), id, _paths.UserBlobs(id)); }
            catch (Exception ex) when (migrating)
            {
                // Jeder Schritt ist eine Transaktion: die Datenbank steht noch auf der Version davor
                _update!.Failed(id, ex);
                throw new UpdatePendingException("Die Datenänderung ist gescheitert, die Daten sind unverändert: " + ex.Message, "error");
            }
            if (migrating) _update!.Succeeded(id);
            return new UserSpace(user, db);
        }));
        // [feature:migrationprogress]
        // Laeuft die Datenaenderung schon, nicht auf ihr Ende warten (Ladekreis, haengende Hintergrunddienste): sofort
        // „laeuft gerade“ melden - die Oberflaeche zeigt den Fortschritt. Vor dem try, damit der laufende Umbau bleibt.
        if (!lazy.IsValueCreated && Features.On(Features.MigrationProgress) && Activity.Get(userId, Activity.Migration) is { State: "running" } run)
            throw new UpdatePendingException("Deine Daten werden gerade auf den neuen Stand gebracht" +
                (run.Total is > 0 ? $" (Schritt {(run.Done ?? 0) + 1} von {run.Total})." : "."), "running");
        // [/feature:migrationprogress]
        try { return lazy.Value; }
        catch
        {
            // Lazy merkt sich die Ausnahme - gescheitertes Oeffnen nicht behalten, beim naechsten Mal neu versuchen
            _open.TryRemove(new KeyValuePair<string, Lazy<UserSpace>>(userId, lazy));
            throw;
        }
    }

    /// <summary>Alle Benutzer - geoeffnet beim ersten Zugriff.</summary>
    public IEnumerable<UserSpace> All()
    {
        foreach (var u in _users.All())
        {
            UserSpace? s = null;
            try
            {
                bool fresh = !_open.ContainsKey(u.Id);
                s = Get(u.Id);
                s.Refresh(u);
                if (fresh) Opened?.Invoke(s);
            }
            catch { /* Schluessel passt nicht - beim naechsten Mal */ }
            if (s != null) yield return s;
        }
    }

    /// <summary>Alle Benutzer, für die Hintergrundarbeit läuft (Mailabruf, Abgleich, DMS-Quellen …): ohne gesperrte Konten.
    /// Sicherungen und Aufräumarbeiten nehmen weiter <see cref="All"/> - die Daten gesperrter Konten bleiben.</summary>
    public IEnumerable<UserSpace> Working() => All().Where(s => !s.User.LockedOut);

    public void Forget(string userId) => _open.TryRemove(userId, out _);

    public UserSpace Current(ClaimsPrincipal principal) =>
        Get(principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new UnauthorizedAccessException());
}

public static class HttpUser
{
    public static UserSpace Space(this HttpContext http) =>
        http.RequestServices.GetRequiredService<Spaces>().Current(http.User);
}
