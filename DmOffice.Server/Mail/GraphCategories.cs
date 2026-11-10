using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DmOffice.Core;
using DmOffice.Pim;

namespace DmOffice.Mail;

/// <summary>
/// Mail-Kategorien bei Microsoft-Konten (outlook.com): Outlook nimmt ueber IMAP keine eigenen Schlagworte an, kennt aber
/// Kategorien (message.categories, Hauptkategorien unter /me/outlook/masterCategories). Ist das Konto mit Graph verbunden,
/// gehen Kategorien darueber: der Name ist der Schluessel (dmOffice-Kategorie "Rechnung" = Outlook-Kategorie "Rechnung").
/// Lokal liegen sie wie IMAP-Schlagworte in mail.keywords - Liste, Kategorieordner und Zaehlung bleiben, wie sie sind.
/// graph_mail merkt je Mail (Message-ID) die Graph-Kennung und die Kategorien bei Microsoft; IMAP kann sie nicht
/// ueberschreiben, <see cref="Reapply"/> setzt sie nach jedem Abgleich wieder.
/// </summary>
public static class GraphCategories
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private static readonly ConcurrentDictionary<UserDb, bool> Ready = new();

    /// <summary>Nur fuer Tests: statt des echten Clients.</summary>
    public static Func<MailAccount, GraphClient>? ClientFactory { get; set; }

    /// <summary>Kategorien dieses Kontos gehen ueber Graph (Microsoft-Konto mit verbundenem Graph-Token).</summary>
    public static bool Uses(MailAccount a) => a.Auth == MailAuth.Microsoft && !string.IsNullOrEmpty(a.GraphRefreshToken);

    public const string NotConnected = "Für Kategorien bei Outlook.com: beim E-Mail-Konto „Kalender, Kontakte und Aufgaben“ verbinden.";

    public static GraphClient Client(MailAccount a) => ClientFactory?.Invoke(a)
        ?? new GraphClient(Http, ct => (ImapPool.OAuth ?? throw new MailKit.Security.AuthenticationException("Microsoft-Anmeldung ist nicht eingerichtet.")).GraphTokenAsync(a, ct));

    public static void Ensure(UserDb db)
    {
        if (Ready.ContainsKey(db)) return;
        db.Exec(@"CREATE TABLE IF NOT EXISTS graph_mail (account TEXT NOT NULL, message_id TEXT NOT NULL, remote_id TEXT NULL, cats TEXT NOT NULL DEFAULT '',
    PRIMARY KEY(account, message_id)) WITHOUT ROWID");
        Ready[db] = true;
    }

    /// <summary>Stand des Abgleichs je Konto (Delta-Stand je Ordner, letzter Lauf, Fehler).</summary>
    public sealed class State
    {
        public Dictionary<string, string> Delta { get; set; } = new();
        public DateTime? LastSync { get; set; }
        public string? LastError { get; set; }
    }

    private static string StateKey(string account) => "mail.graphcat." + account;
    public static State StateOf(UserDb db, string account) => db.GetJson<State>(StateKey(account));
    private static void Save(UserDb db, string account, State s) => db.SetJson(StateKey(account), s);

    private static string Mid(string id) => id.Trim().Trim('<', '>');

    // ---------------- Namen und Schluessel ----------------

    /// <summary>Kategorie-Schluessel (dm_...) zu den Namen bei Microsoft; unbekannte Namen werden als Kategorie angelegt.</summary>
    private static List<string> KeysFor(UserSpace space, IEnumerable<string> names, Dictionary<string, string>? colors = null)
    {
        var cats = Settings.Categories.Of(space.Settings);
        var keys = new List<string>();
        var missing = new List<string>();
        foreach (var name in names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (cats.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) is { } c) keys.Add(c.Key);
            else missing.Add(name);
        }
        if (missing.Count == 0) return keys;
        space.UpdateSettings(s =>
        {
            s.Categories ??= Settings.Categories.Of(s);
            foreach (var name in missing)
            {
                if (s.Categories.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) is { } had) { keys.Add(had.Key); continue; }
                var key = Settings.Categories.KeyFor(name, s.Categories.Select(c => c.Key));
                s.Categories.Add(new MailCategory { Key = key, Name = name, Color = colors?.GetValueOrDefault(name) ?? "#0F6CBD" });
                keys.Add(key);
            }
        });
        return keys;
    }

    private static string Text(IEnumerable<string> keys)
    {
        var list = keys.Select(k => k.ToLowerInvariant()).Distinct().Order().ToList();
        return list.Count == 0 ? "" : " " + string.Join(" ", list) + " ";
    }

    /// <summary>Die Farben der Hauptkategorien (preset0 ... preset24) als Hex-Werte, ungefaehr wie Outlook sie zeigt.</summary>
    public static readonly string[] Presets =
    [
        "#E7474E", "#F38E33", "#AB6F48", "#F4D03F", "#4CAF50", "#3BB4AF", "#8C9B3C", "#3C78D8", "#8E5CC4", "#D1406D",
        "#7E90A3", "#4E5D6C", "#A0A0A0", "#5F5F5F", "#202020", "#A4262C", "#CA5010", "#7A4B22", "#B89A00", "#107C10",
        "#00706B", "#5B6E1C", "#1F4E99", "#5C2E91", "#8E192E",
    ];

    /// <summary>Naechstliegendes Preset zu einer Farbe (#rrggbb).</summary>
    public static string PresetFor(string? color)
    {
        static (int, int, int) Rgb(string hex) => hex is { Length: 7 } && hex[0] == '#'
            ? (int.Parse(hex[1..3], NumberStyles.HexNumber), int.Parse(hex[3..5], NumberStyles.HexNumber), int.Parse(hex[5..7], NumberStyles.HexNumber)) : (15, 108, 189);
        var (r, g, b) = Rgb(color ?? "");
        var best = Enumerable.Range(0, Presets.Length).MinBy(i =>
        {
            var (pr, pg, pb) = Rgb(Presets[i]);
            return (r - pr) * (r - pr) + (g - pg) * (g - pg) + (b - pb) * (b - pb);
        });
        return "preset" + best;
    }

    private static string? ColorOf(string? preset) =>
        preset != null && preset.StartsWith("preset") && int.TryParse(preset[6..], out var i) && i >= 0 && i < Presets.Length ? Presets[i] : null;

    /// <summary>Hauptkategorie anlegen, wenn es sie bei Microsoft noch nicht gibt.</summary>
    private static async Task EnsureMasterAsync(GraphClient graph, string name, string? color, CancellationToken ct)
    {
        var (items, _) = await graph.PagesAsync("/me/outlook/masterCategories", ct);
        if (items.Any(i => string.Equals(Str(i, "displayName"), name, StringComparison.OrdinalIgnoreCase))) return;
        try { await graph.SendAsync(HttpMethod.Post, "/me/outlook/masterCategories", new JsonObject { ["displayName"] = name, ["color"] = PresetFor(color) }, ct); }
        catch (GraphException e) when (e.Status == 409) { }   // gleichzeitig angelegt
    }

    // ---------------- Setzen und Entfernen ----------------

    /// <summary>
    /// Kategorie an Mails setzen oder entfernen - bei Microsoft (PATCH categories) und lokal. Die Graph-Kennung kommt
    /// ueber die Message-ID. Mails ohne Message-ID oder bei Microsoft nicht gefunden: nur lokal.
    /// </summary>
    public static async Task SetAsync(UserSpace space, MailAccount account, string folder, IEnumerable<uint> uids, string key, bool on, CancellationToken ct)
    {
        Ensure(space.Db);
        var db = space.Db;
        var cat = Settings.Categories.Of(space.Settings).FirstOrDefault(c => c.Key == key) ?? throw new InvalidOperationException("Unbekannte Kategorie.");
        var graph = Client(account);
        if (on) await EnsureMasterAsync(graph, cat.Name, cat.Color, ct);
        var local = new List<uint>();
        foreach (var uid in uids)
        {
            var mid = db.Scalar("SELECT message_id FROM mail WHERE account=$a AND folder=$f AND uid=$u", ("$a", account.Id), ("$f", folder), ("$u", (long)uid)) as string;
            if (string.IsNullOrWhiteSpace(mid)) { local.Add(uid); continue; }
            mid = Mid(mid);
            var remoteId = db.Scalar("SELECT remote_id FROM graph_mail WHERE account=$a AND message_id=$m", ("$a", account.Id), ("$m", mid)) as string;
            JsonElement? message = null;
            if (remoteId != null)
            {
                try { message = await graph.GetAsync($"/me/messages/{Uri.EscapeDataString(remoteId)}?$select=id,categories", ct); }
                catch (GraphException e) when (e.Status == 404) { remoteId = null; }   // verschoben: neue Kennung
            }
            if (message == null)
            {
                var filter = Uri.EscapeDataString($"internetMessageId eq '<{mid.Replace("'", "''")}>'");
                var (found, _) = await graph.PagesAsync($"/me/messages?$filter={filter}&$select=id,categories&$top=5", ct);
                if (found.Count == 0) { local.Add(uid); continue; }
                message = found[0];
                remoteId = Str(found[0], "id");
            }
            var names = Names(message.Value);
            names.RemoveAll(n => string.Equals(n, cat.Name, StringComparison.OrdinalIgnoreCase));
            if (on) names.Add(cat.Name);
            await graph.SendAsync(HttpMethod.Patch, $"/me/messages/{Uri.EscapeDataString(remoteId!)}", new JsonObject
            {
                ["categories"] = new JsonArray(names.Select(n => (JsonNode)JsonValue.Create(n)!).ToArray()),
            }, ct);
            Remember(db, account.Id, mid, remoteId, Text(KeysFor(space, names)));
        }
        // Bei Microsoft gefunden: dessen Stand (samt dort gesetzter Kategorien), sonst nur lokal
        Reapply(db, account.Id);
        if (local.Count > 0) MailStore.SetKeyword(db, account.Id, folder, local, key, on);
    }

    private static void Remember(UserDb db, string account, string mid, string? remoteId, string cats) =>
        db.Exec(@"INSERT INTO graph_mail(account,message_id,remote_id,cats) VALUES($a,$m,$r,$c)
                  ON CONFLICT(account,message_id) DO UPDATE SET remote_id=COALESCE($r,remote_id), cats=$c",
            ("$a", account), ("$m", mid), ("$r", remoteId), ("$c", cats));

    // ---------------- Abholen ----------------

    /// <summary>Kategorien aller Ordner abholen (Delta je Ordner, beim ersten Mal nur der Zeitraum des Kontos).</summary>
    public static async Task PullAsync(UserSpace space, MailAccount account, CancellationToken ct)
    {
        Ensure(space.Db);
        var db = space.Db;
        var graph = Client(account);
        var state = StateOf(db, account.Id);
        try
        {
            var colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var (masters, _) = await graph.PagesAsync("/me/outlook/masterCategories", ct);
            foreach (var m in masters)
                if (Str(m, "displayName") is { } n && ColorOf(Str(m, "color")) is { } c) colors[n] = c;

            var folders = await FoldersAsync(graph, ct);
            var since = account.SyncMonths > 0 ? DateTime.UtcNow.AddMonths(-account.SyncMonths) : (DateTime?)null;
            foreach (var id in folders)
            {
                var start = $"/me/mailFolders/{Uri.EscapeDataString(id)}/messages/delta?$select=internetMessageId,categories"
                    + (since is DateTime s ? "&$filter=" + Uri.EscapeDataString($"receivedDateTime ge {s:yyyy-MM-dd'T'HH:mm:ss'Z'}") : "");
                List<JsonElement> items;
                string? delta;
                try { (items, delta) = await graph.PagesAsync(state.Delta.GetValueOrDefault(id) ?? start, ct); }
                catch (GraphException e) when (e.Resync && state.Delta.ContainsKey(id)) { (items, delta) = await graph.PagesAsync(start, ct); }
                foreach (var m in items)
                {
                    if (m.TryGetProperty("@removed", out _) || Str(m, "internetMessageId") is not { } imid) continue;
                    Remember(db, account.Id, Mid(imid), Str(m, "id"), Text(KeysFor(space, Names(m), colors)));
                }
                if (delta != null) state.Delta[id] = delta;
            }
            foreach (var gone in state.Delta.Keys.Except(folders).ToList()) state.Delta.Remove(gone);
            state.LastError = null;
        }
        catch (Exception e) when (e is GraphException or HttpRequestException or MailKit.Security.AuthenticationException)
        {
            state.LastError = e is GraphException { Status: 403 }
                ? "Microsoft verweigert den Zugriff auf Mails – beim E-Mail-Konto neu verbinden."
                : e is HttpRequestException ? "Keine Verbindung zu Microsoft." : e.Message;
        }
        state.LastSync = DateTime.UtcNow;
        Save(db, account.Id, state);
        Reapply(db, account.Id);
    }

    /// <summary>Alle Ordner samt Unterordnern (Kennungen).</summary>
    private static async Task<List<string>> FoldersAsync(GraphClient graph, CancellationToken ct)
    {
        var result = new List<string>();
        var queue = new Queue<string>(["/me/mailFolders?$top=100&$select=id,childFolderCount"]);
        while (queue.Count > 0 && result.Count < 500)
        {
            var (items, _) = await graph.PagesAsync(queue.Dequeue(), ct);
            foreach (var f in items)
            {
                if (Str(f, "id") is not { } id) continue;
                result.Add(id);
                if (f.TryGetProperty("childFolderCount", out var n) && n.ValueKind == JsonValueKind.Number && n.GetInt32() > 0)
                    queue.Enqueue($"/me/mailFolders/{Uri.EscapeDataString(id)}/childFolders?$top=100&$select=id,childFolderCount");
            }
        }
        return result;
    }

    /// <summary>Die Kategorien von Microsoft an die lokalen Mails (alle Ordner mit derselben Message-ID). IMAP liefert bei
    /// Outlook keine - was MailSync dort hinschreibt, gilt fuer diese Konten nicht.</summary>
    public static void Reapply(UserDb db, string account)
    {
        Ensure(db);
        db.Exec(@"UPDATE mail SET keywords = g.cats FROM graph_mail g
                  WHERE mail.account=$a AND g.account=$a AND mail.message_id=g.message_id AND mail.keywords <> g.cats", ("$a", account));
    }

    private static List<string> Names(JsonElement m) =>
        m.TryGetProperty("categories", out var c) && c.ValueKind == JsonValueKind.Array
            ? c.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList() : new();

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
