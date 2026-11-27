using System.Collections.Concurrent;
using System.Text.Json;
using DmOffice.Core;

namespace DmOffice.Mail;

/// <summary>Zähler für Suchordner, Kategorien und Catchall-Ansichten im Ordnerbereich (GET /api/mail/virtual-counts).
/// Je Benutzer rechnet immer nur ein Aufruf; wer dazukommt, wartet und bekommt dasselbe Ergebnis. Solange sich weder
/// Einstellungen noch Mails geändert haben, kommt das letzte Ergebnis zurück (die Version des Mail-Bereichs zählt dafür nicht:
/// sie springt auch bei Postausgang, Abgleich-Stand usw., und jedes Neuzählen kostet bei großen Postfächern Sekunden). Suchordner mit „letzte N Tage“ oder
/// Suchbegriffen werden jedes Mal gezählt.</summary>
public static class VirtualCounts
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new();
    private static readonly ConcurrentDictionary<string, (string Stamp, Dictionary<string, object> Result)> Last = new();

    public static async Task<Dictionary<string, object>> Get(UserSpace space, CancellationToken ct)
    {
        var user = space.User.Id;
        var gate = Gates.GetOrAdd(user, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try { return await Task.Run(() => Compute(space), ct); }
        finally { gate.Release(); }
    }

    private static Dictionary<string, object> Compute(UserSpace space)
    {
        var db = space.Db;
        var off = space.Settings.OffMailAccountIds();
        var folders = space.Settings.SearchFolders;
        var categories = Settings.Categories.Of(space.Settings).Select(c => c.Key).ToList();
        var views = new List<(string Key, MailCriteria View)>();
        // [feature:catchalldomains]
        foreach (var key in CatchallAliases.Views(db))
            if (CatchallAliases.View(db, key) is { } view) views.Add((key, view));
        // [/feature:catchalldomains]

        var stamp = JsonSerializer.Serialize(new object[]
        {
            off.Order(StringComparer.Ordinal), folders, categories,
            views.Select(v => new object[] { v.Key, v.View }), Fingerprint(db, views.Count > 0),
        });
        // „Letzte N Tage“ hängt an der Uhr, Suchbegriffe am Volltextindex: diese Suchordner jedes Mal zählen
        bool Live(SearchFolder f) => f.Criteria.Days is > 0 || !string.IsNullOrWhiteSpace(f.Criteria.Text);
        var dated = folders.Where(Live).ToList();
        Dictionary<string, object> result;
        if (Last.TryGetValue(space.User.Id, out var last) && last.Stamp == stamp)
            result = new Dictionary<string, object>(last.Result);
        else
        {
            result = new Dictionary<string, object>();
            foreach (var f in folders.Where(f => !Live(f)))
            {
                var (t, u, _) = MailStore.Query(db, f.Criteria, 0, 0, hidden: off);
                result[MailStore.SearchPrefix + f.Id] = new { total = t, unread = u };
            }
            foreach (var (key, (t, u)) in MailStore.CategoryCounts(db, categories, off))
                result[MailStore.CategoryPrefix + key] = new { total = t, unread = u };
            foreach (var (key, view) in views)
            {
                var (t, u, _) = MailStore.Query(db, view, 0, 0, hidden: off);
                result[key] = new { total = t, unread = u };
            }
            Last[space.User.Id] = (stamp, new Dictionary<string, object>(result));
        }
        foreach (var f in dated)
        {
            var (t, u, _) = MailStore.Query(db, f.Criteria, 0, 0, hidden: off);
            result[MailStore.SearchPrefix + f.Id] = new { total = t, unread = u };
        }
        // Reihenfolge wie bisher: Suchordner, Kategorien, Ansichten
        var ordered = new Dictionary<string, object>();
        foreach (var key in folders.Select(f => MailStore.SearchPrefix + f.Id)
                     .Concat(categories.Select(k => MailStore.CategoryPrefix + k)).Concat(views.Select(v => v.Key)))
            if (result.TryGetValue(key, out var v)) ordered[key] = v;
        return ordered;
    }

    /// <summary>Ändert sich, sobald Mails dazukommen, wegfallen, gelesen, markiert, verschlagwortet oder verschoben werden,
    /// auch ohne dass dabei die Version des Benutzers hochgezählt wurde (z. B. „gelesen“ beim Öffnen).</summary>
    private static string Fingerprint(UserDb db, bool aliases)
    {
        // Ein Durchgang über alle Mails kostet bei verschlüsselter Datenbank Sekunden; darum zählen Trigger jede Änderung mit
        var mail = StampOf(db);
        var special = string.Join(";", db.Query("SELECT account, path, COALESCE(special,'') FROM mail_folders WHERE special IN ('trash','junk','drafts') ORDER BY 1,2",
            r => r.GetString(0) + "|" + r.GetString(1) + "|" + r.GetString(2)));
        var hits = "";
        // [feature:catchalldomains]
        if (aliases)
        {
            CatchallAliases.Ensure(db);
            hits = db.Query("SELECT COUNT(*), TOTAL(length(alias)), TOTAL(length(message_id)) FROM mail_alias_hit",
                r => $"{r.GetInt64(0)}/{r.GetDouble(1)}/{r.GetDouble(2)}").First();
        }
        // [/feature:catchalldomains]
        return mail + "#" + special + "#" + hits;
    }

    private static readonly string[] Triggers =
    [
        "CREATE TRIGGER IF NOT EXISTS mail_stamp_ins AFTER INSERT ON mail BEGIN UPDATE mail_stamp SET v=v+1; END",
        "CREATE TRIGGER IF NOT EXISTS mail_stamp_del AFTER DELETE ON mail BEGIN UPDATE mail_stamp SET v=v+1; END",
        "CREATE TRIGGER IF NOT EXISTS mail_stamp_upd AFTER UPDATE OF account, folder, seen, flagged, has_att, keywords, date, message_id ON mail BEGIN UPDATE mail_stamp SET v=v+1; END",
    ];

    /// <summary>Zähler, der bei jeder Änderung an Mails (neu, gelöscht, Konto, Ordner, gelesen, markiert, Anhang, Schlagwörter, Datum) weiterspringt.
    /// Fehlt ein Trigger (neue Datenbank, Tabelle neu angelegt), wird er angelegt und der Zähler springt ebenfalls.</summary>
    internal static string StampOf(UserDb db)
    {
        var have = db.Query("SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name LIKE 'mail_stamp_%'", r => r.GetInt64(0)).First();
        if (have < Triggers.Length)
        {
            using var w = db.BeginWrite();
            UserDb.Exec(w.Conn, "CREATE TABLE IF NOT EXISTS mail_stamp (id INTEGER PRIMARY KEY CHECK (id = 1), v INTEGER NOT NULL)");
            UserDb.Exec(w.Conn, "INSERT OR IGNORE INTO mail_stamp VALUES (1, 0)");
            foreach (var t in Triggers) UserDb.Exec(w.Conn, t);
            UserDb.Exec(w.Conn, "UPDATE mail_stamp SET v=v+1");
            w.Commit();
        }
        return db.Query("SELECT v FROM mail_stamp", r => r.GetInt64(0)).First() + "/" +
               db.Query("SELECT COALESCE(MAX(id),0) FROM mail", r => r.GetInt64(0)).First();
    }
}
