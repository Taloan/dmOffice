using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DmOffice.Core;

namespace DmOffice.Pim;

/// <summary>Eine lokale Sammlung, die mit einem Kalender, Kontaktordner oder einer To-Do-Liste bei Microsoft verbunden ist.</summary>
public sealed class GraphLink
{
    public long Collection { get; set; }
    /// <summary>Mailkonto (MailAccount.Id), dessen Graph-Token gilt.</summary>
    public string Account { get; set; } = "";
    /// <summary>calendar, contacts oder tasks.</summary>
    public string Kind { get; set; } = "";
    public string RemoteId { get; set; } = "";
    /// <summary>Name bei Microsoft - aendert er sich dort, folgt der lokale.</summary>
    public string? RemoteName { get; set; }
    /// <summary>Kontakte und Aufgaben: wo das naechste Abholen weitermacht (Graph-Delta).</summary>
    public string? DeltaLink { get; set; }
    public DateTime? LastSync { get; set; }
    public string? LastError { get; set; }
    /// <summary>Zuletzt aufgeloeste Konflikte ("Titel: Fassung von Microsoft verworfen"), hoechstens 20.</summary>
    public List<string> Conflicts { get; set; } = new();
}

/// <summary>
/// Verbindungen zu Microsoft (docs/microsoft-graph-konzept.md), im Schluessel-Wert-Speicher wie die Abos. Verbundene
/// Sammlungen sind beschreibbar: lokale Aenderungen landen in graph_outbox (PimStore.Queue) und gehen zu Microsoft.
/// Welches lokale Objekt zu welchem bei Microsoft gehoert, steht in graph_map.
/// </summary>
public static class Graph
{
    public const string Key = "pim.graph";
    public const string Calendar = "calendar", Contacts = "contacts", Tasks = "tasks";

    public static List<GraphLink> All(UserDb db) => db.GetJson<List<GraphLink>>(Key);
    private static void Save(UserDb db, List<GraphLink> list) => db.SetJson(Key, list);
    public static GraphLink? Of(UserDb db, long collection) => All(db).FirstOrDefault(l => l.Collection == collection);
    public static bool IsLinked(UserDb db, long collection) => All(db).Any(l => l.Collection == collection);

    public static void Update(UserDb db, long collection, Action<GraphLink> change)
    {
        lock (db)
        {
            var list = All(db);
            if (list.FirstOrDefault(l => l.Collection == collection) is { } l) { change(l); Save(db, list); }
        }
    }

    public static void Add(UserDb db, GraphLink link) { lock (db) { var list = All(db); list.RemoveAll(x => x.Collection == link.Collection); list.Add(link); Save(db, list); } }
    public static void Remove(UserDb db, long collection) { lock (db) { var list = All(db); list.RemoveAll(x => x.Collection == collection); Save(db, list); } }

    /// <summary>Verbindung loesen und die lokale Kopie entfernen - bei Microsoft bleibt alles.</summary>
    public static void Unlink(UserDb db, GraphLink link)
    {
        Remove(db, link.Collection);
        if (PimStore.Find(db, link.Collection) != null)
        {
            PimStore.DeleteCollection(db, link.Collection);
            Lists.Remove(db, link.Collection);
        }
    }

    /// <summary>Alle Verbindungen eines Kontos loesen (Konto geloescht, Bereich abgeschaltet).</summary>
    public static void UnlinkAccount(UserDb db, string account, string? kind = null)
    {
        foreach (var l in All(db).Where(l => l.Account == account && (kind == null || l.Kind == kind))) Unlink(db, l);
    }

    // ---------------- Zuordnung und Warteschlange ----------------

    public sealed record MapRow(string Uri, string RemoteId, string? ChangeKey, string? Etag);
    public sealed record OutboxRow(string Uri, string Op, string At, int Tries, string? Error);

    public static MapRow? MapByRemote(UserDb db, long coll, string remoteId) =>
        db.Query("SELECT uri, remote_id, change_key, etag FROM graph_map WHERE collection=$c AND remote_id=$r",
            r => new MapRow(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)),
            ("$c", coll), ("$r", remoteId)).FirstOrDefault();

    public static MapRow? MapByUri(UserDb db, long coll, string uri) =>
        db.Query("SELECT uri, remote_id, change_key, etag FROM graph_map WHERE collection=$c AND uri=$u",
            r => new MapRow(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)),
            ("$c", coll), ("$u", uri)).FirstOrDefault();

    public static List<MapRow> Map(UserDb db, long coll) =>
        db.Query("SELECT uri, remote_id, change_key, etag FROM graph_map WHERE collection=$c",
            r => new MapRow(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)),
            ("$c", coll));

    public static void SetMap(UserDb db, long coll, string uri, string remoteId, string? changeKey, string? etag) =>
        db.Exec(@"INSERT INTO graph_map(collection,uri,remote_id,change_key,etag) VALUES($c,$u,$r,$k,$e)
                  ON CONFLICT(collection,uri) DO UPDATE SET remote_id=$r, change_key=$k, etag=$e",
            ("$c", coll), ("$u", uri), ("$r", remoteId), ("$k", changeKey), ("$e", etag));

    public static void Unmap(UserDb db, long coll, string uri) =>
        db.Exec("DELETE FROM graph_map WHERE collection=$c AND uri=$u", ("$c", coll), ("$u", uri));

    public static List<OutboxRow> Outbox(UserDb db, long coll) =>
        db.Query("SELECT uri, op, at, tries, error FROM graph_outbox WHERE collection=$c ORDER BY at",
            r => new OutboxRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.IsDBNull(4) ? null : r.GetString(4)), ("$c", coll));

    public static OutboxRow? Pending(UserDb db, long coll, string uri) =>
        db.Query("SELECT uri, op, at, tries, error FROM graph_outbox WHERE collection=$c AND uri=$u",
            r => new OutboxRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.IsDBNull(4) ? null : r.GetString(4)),
            ("$c", coll), ("$u", uri)).FirstOrDefault();

    /// <summary>Erledigt - nur, wenn seither keine neuere Aenderung dazukam (at gleich).</summary>
    public static void Done(UserDb db, long coll, string uri, string? at = null) =>
        db.Exec("DELETE FROM graph_outbox WHERE collection=$c AND uri=$u AND ($a IS NULL OR at=$a)", ("$c", coll), ("$u", uri), ("$a", at));

    public static void Failed(UserDb db, long coll, string uri, string error) =>
        db.Exec("UPDATE graph_outbox SET tries=tries+1, error=$e WHERE collection=$c AND uri=$u", ("$c", coll), ("$u", uri), ("$e", error));

    /// <summary>Wartende Aenderungen je Sammlung: (alle, davon mindestens dreimal gescheitert).</summary>
    public static (int Pending, int Failing) Waiting(UserDb db, long coll) =>
        db.Query("SELECT COUNT(*), COALESCE(SUM(tries >= 3),0) FROM graph_outbox WHERE collection=$c",
            r => (r.GetInt32(0), r.GetInt32(1)), ("$c", coll)).First();
}

/// <summary>Fehler von Microsoft Graph, mit Status und Fehlercode (syncStateNotFound, ErrorItemNotFound ...).</summary>
public sealed class GraphException(int status, string? code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string? Code { get; } = code;
    /// <summary>Delta-Stand verfallen - von vorn abholen.</summary>
    public bool Resync => Status == 410 || Code is "syncStateNotFound" or "resyncRequired" or "SyncStateNotFound" or "SyncStateInvalid";
}

/// <summary>
/// Aufrufe an Microsoft Graph v1.0 mit dem Graph-Token eines Kontos. 429 und 503: so lange warten, wie Retry-After
/// sagt (hoechstens zwei Minuten), dann noch einmal - hoechstens dreimal.
/// </summary>
public sealed class GraphClient
{
    public const string Root = "https://graph.microsoft.com/v1.0";
    private readonly HttpClient _http;
    private readonly Func<CancellationToken, Task<string>> _token;

    /// <summary>Warten vor einem neuen Versuch - in Tests ersetzbar.</summary>
    public Func<TimeSpan, CancellationToken, Task> Wait { get; set; } = Task.Delay;

    public GraphClient(HttpClient http, Func<CancellationToken, Task<string>> token)
    {
        _http = http;
        _token = token;
    }

    public Task<JsonElement> GetAsync(string url, CancellationToken ct) => SendAsync(HttpMethod.Get, url, null, ct);

    /// <summary>Alle Seiten einer Liste (@odata.nextLink); bei Delta-Abfragen kommt der deltaLink mit.</summary>
    public async Task<(List<JsonElement> Items, string? DeltaLink)> PagesAsync(string url, CancellationToken ct)
    {
        var items = new List<JsonElement>();
        string? next = url;
        while (next != null)
        {
            var page = await GetAsync(next, ct);
            if (page.ValueKind != JsonValueKind.Object) throw new GraphException(0, null, "Unerwartete Antwort von Microsoft.");
            if (page.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Array) items.AddRange(v.EnumerateArray());
            if (page.TryGetProperty("@odata.deltaLink", out var d)) return (items, d.GetString());
            next = page.TryGetProperty("@odata.nextLink", out var n) ? n.GetString() : null;
        }
        return (items, null);
    }

    public async Task<JsonElement> SendAsync(HttpMethod method, string url, JsonNode? body, CancellationToken ct)
    {
        if (!url.StartsWith("https://", StringComparison.Ordinal)) url = Root + url;
        // Nur Microsoft Graph - Folgeadressen (nextLink, deltaLink) kommen von dort, aber man weiss ja nie
        if (!url.StartsWith(Root + "/", StringComparison.Ordinal)) throw new GraphException(0, null, "Unerwartete Adresse von Microsoft.");
        for (int attempt = 1; ; attempt++)
        {
            using var req = new HttpRequestMessage(method, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _token(ct));
            // Zeiten in UTC, Texte ohne HTML
            req.Headers.TryAddWithoutValidation("Prefer", "outlook.timezone=\"UTC\"");
            req.Headers.TryAddWithoutValidation("Prefer", "outlook.body-content-type=\"text\"");
            // Groessere Seiten (Delta der Mails liefert sonst nur 10 je Seite)
            req.Headers.TryAddWithoutValidation("Prefer", "odata.maxpagesize=200");
            if (body != null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var res = await _http.SendAsync(req, ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            if (res.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout && attempt < 3)
            {
                var wait = res.Headers.RetryAfter?.Delta ?? (res.Headers.RetryAfter?.Date is { } at ? at - DateTimeOffset.UtcNow : (TimeSpan?)null) ?? TimeSpan.FromSeconds(5 * attempt);
                await Wait(wait < TimeSpan.Zero ? TimeSpan.Zero : wait > TimeSpan.FromMinutes(2) ? TimeSpan.FromMinutes(2) : wait, ct);
                continue;
            }
            JsonElement json = default;
            if (text.Length > 0)
                try { using var doc = JsonDocument.Parse(text); json = doc.RootElement.Clone(); }
                catch (JsonException) { }
            if (res.IsSuccessStatusCode) return json;
            string? code = null, message = null;
            if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
            {
                code = err.TryGetProperty("code", out var c) ? c.GetString() : null;
                message = err.TryGetProperty("message", out var m) ? m.GetString() : null;
            }
            var status = (int)res.StatusCode;
            throw new GraphException(status, code, status switch
            {
                401 or 403 => "Microsoft verweigert den Zugriff" + (message is { Length: > 0 } ? ": " + message : "."),
                404 => "Bei Microsoft nicht gefunden.",
                429 => "Microsoft bremst gerade (zu viele Anfragen).",
                _ => $"Microsoft antwortet mit Fehler {status}" + (message is { Length: > 0 } ? ": " + message.Split('\n')[0] : "."),
            });
        }
    }
}
