using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;

namespace DmOffice.Core;

/// <summary>
/// Angemeldete Browser einer Person (Sitzungsliste): jede Anmeldung mit Cookie bekommt eine Kennung (Anspruch dm:sid).
/// Gespeichert in der Benutzerdatenbank, im Speicher zwischengehalten. Ein abgemeldeter Eintrag fehlt in der Liste - sein
/// Cookie gilt dann nicht mehr (Auth.Validate). Cookies ohne Kennung (von vor der Liste) gelten weiter, bis „Überall abmelden“.
/// </summary>
public static class CookieSessions
{
    public const string Claim = "dm:sid";
    private const string Key = "auth.sessions";
    /// <summary>Höchstens so viele Einträge je Person - die ältesten fallen heraus (ihr Cookie gilt dann nicht mehr).</summary>
    private const int Max = 50;
    /// <summary>„Zuletzt aktiv“ höchstens so oft schreiben.</summary>
    private static readonly TimeSpan SeenEvery = TimeSpan.FromMinutes(10);

    public sealed class Entry
    {
        public string Id { get; set; } = "";
        public DateTime Created { get; set; }
        public DateTime Seen { get; set; }
        public string Browser { get; set; } = "";
        public string Address { get; set; } = "";
        /// <summary>Stempel der Person bei der Anmeldung: nach neuem Stempel gilt der Eintrag nicht mehr (wie sein Cookie).</summary>
        public string Stamp { get; set; } = "";
    }

    private static readonly ConcurrentDictionary<string, List<Entry>> Cache = new();

    private static UserDb Db(IServiceProvider services, string userId) => services.GetRequiredService<Spaces>().Get(userId).Db;

    private static List<Entry> Load(IServiceProvider services, string userId) =>
        Cache.GetOrAdd(userId, id => Db(services, id).Get(Key) is { } json ? JsonSerializer.Deserialize<List<Entry>>(json) ?? [] : []);

    private static void Save(IServiceProvider services, string userId, List<Entry> list) =>
        Db(services, userId).Set(Key, JsonSerializer.Serialize(list));

    /// <summary>Neue Anmeldung: Eintrag anlegen, Kennung für den Anspruch zurück.</summary>
    public static string Start(HttpContext http, string userId, string stamp)
    {
        var list = Load(http.RequestServices, userId);
        var now = DateTime.UtcNow;
        var e = new Entry
        {
            Id = E2e.Base64Url(RandomNumberGenerator.GetBytes(16)), Created = now, Seen = now,
            Browser = Auth.BrowserName(http.Request.Headers.UserAgent.ToString()), Address = http.Connection.RemoteIpAddress?.ToString() ?? "", Stamp = stamp,
        };
        lock (list)
        {
            list.Add(e);
            if (list.Count > Max) list.RemoveRange(0, list.Count - Max);
            Save(http.RequestServices, userId, list);
        }
        return e.Id;
    }

    /// <summary>Gilt die Sitzung noch? Merkt „zuletzt aktiv“ (gespeichert höchstens alle zehn Minuten).</summary>
    public static bool Valid(HttpContext http, string userId, string sid)
    {
        var list = Load(http.RequestServices, userId);
        lock (list)
        {
            if (list.FirstOrDefault(x => x.Id == sid) is not { } e) return false;
            var now = DateTime.UtcNow;
            if (now - e.Seen > SeenEvery)
            {
                e.Seen = now;
                e.Address = http.Connection.RemoteIpAddress?.ToString() ?? e.Address;
                Save(http.RequestServices, userId, list);
            }
            return true;
        }
    }

    /// <summary>Gültige Sitzungen (Stempel passt), zuletzt aktive zuerst.</summary>
    public static List<Entry> List(IServiceProvider services, string userId, string stamp)
    {
        var list = Load(services, userId);
        lock (list) return list.Where(x => x.Stamp == stamp).OrderByDescending(x => x.Seen).Select(x => new Entry { Id = x.Id, Created = x.Created, Seen = x.Seen, Browser = x.Browser, Address = x.Address, Stamp = x.Stamp }).ToList();
    }

    /// <summary>Eine Sitzung beenden (Abmelden, Abmelden aus der Liste).</summary>
    public static bool End(IServiceProvider services, string userId, string sid)
    {
        var list = Load(services, userId);
        lock (list)
        {
            if (list.RemoveAll(x => x.Id == sid) == 0) return false;
            Save(services, userId, list);
            return true;
        }
    }

    /// <summary>Alle beenden außer keep (Überall abmelden, Passwort geändert, Zurücksetzen durch die Verwaltung).</summary>
    /// <param name="stamp">Neuer Stempel für die behaltene Sitzung.</param>
    public static void EndAll(IServiceProvider services, string userId, string? keep = null, string? stamp = null)
    {
        var list = Load(services, userId);
        lock (list)
        {
            list.RemoveAll(x => x.Id != keep);
            if (stamp != null) foreach (var x in list) x.Stamp = stamp;
            Save(services, userId, list);
        }
    }

    public static string? Of(ClaimsPrincipal user) => user.FindFirstValue(Claim);

    /// <summary>Nur für Tests: Zwischenspeicher leeren.</summary>
    internal static void Forget() => Cache.Clear();
}
