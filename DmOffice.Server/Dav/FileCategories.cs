using System.Collections.Concurrent;
using System.Security.Cryptography;
using DmOffice.Core;
using DmOffice.Mail;
using DmOffice.Search;

namespace DmOffice.Dav;

/// <summary>
/// Kategorien und Tags der eigenen Dateien (docs/datei-kategorien-konzept.md): eine verschachtelte Taxonomie, die Zuordnung
/// haengt an der fileid (bleibt beim Verschieben und Umbenennen). Kategorien mit „lernt“ lernen aus Zuweisungen von Hand,
/// mit „automatisch“ bekommen neue Dateien sie selbst (markiert, bis bestaetigt oder entfernt). Tags sind frei, ohne Lernen.
/// Eigene Tabellen per CREATE IF NOT EXISTS (file_cats, file_cat_items, file_tags, fcat_*).
/// </summary>
public static class FileCategories
{
    public sealed record Category(string Id, string? Parent, string Name, string? Color, bool Learn, bool Auto, int Sort);
    public sealed record Assigned(string Id, bool Auto);

    // Gelernt wird erst ab 3 Dateien je Seite und nur bei eindeutigem Ergebnis (Learner, wie bei Mail)
    /// <summary>So lange wartet eine neue Datei auf die Inhaltssuche, bevor sie ohne ihren Text eingeordnet wird.</summary>
    private const long WaitForText = 3600;

    private static readonly ConcurrentDictionary<UserDb, bool> Ready = new();
    private static long Unix() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public static void Ensure(UserDb db)
    {
        if (Ready.ContainsKey(db)) return;
        db.Exec(@"CREATE TABLE IF NOT EXISTS file_cats (id TEXT PRIMARY KEY, parent TEXT NULL, name TEXT NOT NULL, color TEXT NULL,
    learn INTEGER NOT NULL DEFAULT 1, auto INTEGER NOT NULL DEFAULT 1, sort INTEGER NOT NULL DEFAULT 0);
CREATE TABLE IF NOT EXISTS file_cat_items (fileid INTEGER NOT NULL, cat TEXT NOT NULL, auto INTEGER NOT NULL DEFAULT 0, added INTEGER NOT NULL, PRIMARY KEY(fileid, cat)) WITHOUT ROWID;
CREATE INDEX IF NOT EXISTS file_cat_items_cat ON file_cat_items(cat);
CREATE TABLE IF NOT EXISTS file_tags (fileid INTEGER NOT NULL, tag TEXT NOT NULL COLLATE NOCASE, PRIMARY KEY(fileid, tag)) WITHOUT ROWID;
CREATE INDEX IF NOT EXISTS file_tags_tag ON file_tags(tag);
CREATE TABLE IF NOT EXISTS fcat_tokens (token TEXT NOT NULL, cat TEXT NOT NULL, pos INTEGER NOT NULL DEFAULT 0, neg INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(token, cat)) WITHOUT ROWID;
CREATE TABLE IF NOT EXISTS fcat_trained (fileid INTEGER NOT NULL, cat TEXT NOT NULL, label INTEGER NOT NULL, PRIMARY KEY(fileid, cat)) WITHOUT ROWID;
CREATE TABLE IF NOT EXISTS fcat_queue (path TEXT PRIMARY KEY, added INTEGER NOT NULL);
CREATE TRIGGER IF NOT EXISTS fcat_new AFTER INSERT ON files WHEN new.is_dir = 0 AND EXISTS (SELECT 1 FROM file_cats WHERE auto = 1 AND learn = 1) BEGIN
    INSERT OR IGNORE INTO fcat_queue(path, added) VALUES (new.path, CAST(strftime('%s','now') AS INTEGER));
END;");
        Ready[db] = true;
    }

    // ---------------- Kategorien ----------------

    public static List<Category> All(UserDb db)
    {
        Ensure(db);
        return db.Query("SELECT id, parent, name, color, learn, auto, sort FROM file_cats ORDER BY sort, name COLLATE NOCASE",
            r => new Category(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.GetInt64(4) != 0, r.GetInt64(5) != 0, r.GetInt32(6)));
    }

    /// <summary>Die Kategorie und alle darunter.</summary>
    public static HashSet<string> Below(List<Category> all, string id)
    {
        var set = new HashSet<string> { id };
        for (bool more = true; more;)
        {
            more = false;
            foreach (var c in all)
                if (c.Parent != null && set.Contains(c.Parent) && set.Add(c.Id)) more = true;
        }
        return set;
    }

    /// <summary>Die Oberkategorien (ohne die Kategorie selbst), die naechste zuerst.</summary>
    public static List<string> Above(List<Category> all, string id)
    {
        var byId = all.ToDictionary(c => c.Id);
        var list = new List<string>();
        for (var c = byId.GetValueOrDefault(id); c?.Parent is { } p && byId.TryGetValue(p, out var up) && !list.Contains(p) && p != id; c = up) list.Add(p);
        return list;
    }

    /// <summary>Name mit Oberkategorien, etwa „Finanzen › Steuer“.</summary>
    public static string FullName(List<Category> all, string id)
    {
        var byId = all.ToDictionary(c => c.Id);
        return string.Join(" › ", Above(all, id).AsEnumerable().Reverse().Append(id).Select(x => byId.GetValueOrDefault(x)?.Name ?? "?"));
    }

    private static string CleanName(string? name)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) throw new ArgumentException("Bitte einen Namen eingeben.");
        if (n.Length > 80) throw new ArgumentException("Der Name ist zu lang (höchstens 80 Zeichen).");
        return n;
    }

    private static void CheckUnique(List<Category> all, string? parent, string name, string? self)
    {
        if (all.Any(c => c.Parent == parent && c.Id != self && string.Equals(c.Name, name, StringComparison.CurrentCultureIgnoreCase)))
            throw new ArgumentException($"Eine Kategorie „{name}“ gibt es hier schon.");
    }

    public static Category Create(UserDb db, string? name, string? parent, string? color = null, bool learn = true, bool auto = true)
    {
        var all = All(db);
        var n = CleanName(name);
        if (parent != null && all.All(c => c.Id != parent)) throw new KeyNotFoundException("Oberkategorie nicht gefunden.");
        CheckUnique(all, parent, n, null);
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        var sort = all.Where(c => c.Parent == parent).Select(c => c.Sort + 1).DefaultIfEmpty(0).Max();
        db.Exec("INSERT INTO file_cats(id, parent, name, color, learn, auto, sort) VALUES($i,$p,$n,$c,$l,$a,$s)",
            ("$i", id), ("$p", parent), ("$n", n), ("$c", color), ("$l", learn ? 1 : 0), ("$a", learn && auto ? 1 : 0), ("$s", sort));
        return new Category(id, parent, n, color, learn, learn && auto, sort);
    }

    /// <summary>Aendern: nur, was angegeben ist. Neue Oberkategorie "" = oberste Ebene.</summary>
    public static Category Update(UserDb db, string id, string? name, string? parent, string? color, bool? learn, bool? auto)
    {
        var all = All(db);
        var c = all.FirstOrDefault(x => x.Id == id) ?? throw new KeyNotFoundException("Kategorie nicht gefunden.");
        var newParent = parent == null ? c.Parent : parent.Length == 0 ? null : parent;
        if (newParent != null)
        {
            if (all.All(x => x.Id != newParent)) throw new KeyNotFoundException("Oberkategorie nicht gefunden.");
            if (Below(all, id).Contains(newParent)) throw new ArgumentException("Eine Kategorie kann nicht unter sich selbst liegen.");
        }
        var n = name == null ? c.Name : CleanName(name);
        CheckUnique(all, newParent, n, id);
        var l = learn ?? c.Learn;
        var a = l && (auto ?? c.Auto);
        var col = color == null ? c.Color : color.Length == 0 ? null : color;
        db.Exec("UPDATE file_cats SET parent=$p, name=$n, color=$c, learn=$l, auto=$a WHERE id=$i",
            ("$i", id), ("$p", newParent), ("$n", n), ("$c", col), ("$l", l ? 1 : 0), ("$a", a ? 1 : 0));
        return c with { Parent = newParent, Name = n, Color = col, Learn = l, Auto = a };
    }

    /// <summary>Loeschen: Unterkategorien ruecken eine Ebene hoch, Zuordnungen und Gelerntes der Kategorie verschwinden.</summary>
    public static void Delete(UserDb db, string id)
    {
        var all = All(db);
        var c = all.FirstOrDefault(x => x.Id == id) ?? throw new KeyNotFoundException("Kategorie nicht gefunden.");
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        UserDb.Exec(conn, "UPDATE file_cats SET parent=$p WHERE parent=$i", ("$p", c.Parent), ("$i", id));
        foreach (var t in new[] { "file_cat_items", "fcat_tokens", "fcat_trained" })
            UserDb.Exec(conn, $"DELETE FROM {t} WHERE cat=$i", ("$i", id));
        UserDb.Exec(conn, "DELETE FROM file_cats WHERE id=$i", ("$i", id));
        tx.Commit();
    }

    /// <summary>Vorschlag für den Anfang (nur bei leerer Liste): eine übliche Ordnung für Unterlagen im Haushalt.</summary>
    public static readonly (string Name, string[] Children)[] Suggestion =
    [
        ("Finanzen", ["Bank", "Rechnungen", "Steuer", "Versicherungen"]),
        ("Verträge", []), ("Wohnen", []), ("Fahrzeug", []), ("Gesundheit", []), ("Arbeit", []),
        ("Behörden & Ausweise", []), ("Schule & Ausbildung", []),
    ];

    /// <summary>Derselbe Vorschlag mit englischen Namen (Oberfläche auf Englisch).</summary>
    public static readonly (string Name, string[] Children)[] SuggestionEn =
    [
        ("Finances", ["Bank", "Invoices", "Taxes", "Insurance"]),
        ("Contracts", []), ("Home", []), ("Vehicle", []), ("Health", []), ("Work", []),
        ("Authorities & IDs", []), ("School & Education", []),
    ];

    public static int CreateSuggestion(UserDb db, string? lang = null)
    {
        if (All(db).Count > 0) return 0;
        int n = 0;
        string[] palette = ["#0F6CBD", "#8764B8", "#CA5010", "#13A10E", "#D13438", "#00B7C3", "#C19C00", "#E3008C"];
        var list = lang == "en" ? SuggestionEn : Suggestion;
        foreach (var (name, kids) in list)
        {
            // Unterkategorien in der Farbe ihrer Oberkategorie
            var color = palette[Array.IndexOf(list.Select(x => x.Name).ToArray(), name) % palette.Length];
            var top = Create(db, name, null, color); n++;
            foreach (var k in kids) { Create(db, k, top.Id, color); n++; }
        }
        return n;
    }

    /// <summary>Je Kategorie: zugeordnete Dateien (direkt) und davon noch nicht bestaetigte.</summary>
    public static Dictionary<string, (int Count, int Auto)> Counts(UserDb db)
    {
        Ensure(db);
        return db.Query(@"SELECT i.cat, COUNT(*), COALESCE(SUM(i.auto), 0) FROM file_cat_items i JOIN files f ON f.fileid = i.fileid GROUP BY i.cat",
            r => (r.GetString(0), r.GetInt32(1), r.GetInt32(2))).ToDictionary(x => x.Item1, x => (x.Item2, x.Item3));
    }

    public static List<(string Tag, int Count)> Tags(UserDb db)
    {
        Ensure(db);
        return db.Query(@"SELECT MIN(t.tag), COUNT(*) FROM file_tags t JOIN files f ON f.fileid = t.fileid GROUP BY t.tag COLLATE NOCASE ORDER BY 1 COLLATE NOCASE",
            r => (r.GetString(0), r.GetInt32(1)));
    }

    // ---------------- Dateien einer Kategorie / eines Tags ----------------

    /// <summary>Dateien in der Kategorie und allen darunter.</summary>
    public static List<FileEntry> FilesIn(UserDb db, string id)
    {
        var all = All(db);
        if (all.All(c => c.Id != id)) throw new KeyNotFoundException("Kategorie nicht gefunden.");
        var ids = Below(all, id).ToList();
        var names = ids.Select((_, i) => "$c" + i).ToList();
        return FileStore.ByFileIds(db, $"SELECT fileid FROM file_cat_items WHERE cat IN ({string.Join(",", names)})",
            ids.Select((c, i) => (names[i], (object?)c)).ToArray());
    }

    public static List<FileEntry> FilesTagged(UserDb db, string tag)
    {
        Ensure(db);
        return FileStore.ByFileIds(db, "SELECT fileid FROM file_tags WHERE tag = $t COLLATE NOCASE", ("$t", tag.Trim()));
    }

    /// <summary>
    /// Suche: Dateien (fileid), deren Kategorie (voller Name, auch eine darüber) oder Tag den Begriff enthält - ohne Groß- und
    /// Kleinschreibung und Akzente.
    /// </summary>
    public static HashSet<long> Matching(UserDb db, string term)
    {
        Ensure(db);
        var cmp = System.Globalization.CultureInfo.InvariantCulture.CompareInfo;
        const System.Globalization.CompareOptions o = System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace;
        var all = All(db);
        var cats = all.Where(c => cmp.IndexOf(FullName(all, c.Id), term, o) >= 0).SelectMany(c => Below(all, c.Id)).ToHashSet();
        var tags = Tags(db).Select(t => t.Tag).Where(t => cmp.IndexOf(t, term, o) >= 0).ToList();
        var result = new HashSet<long>();
        if (cats.Count > 0)
            foreach (var (f, cat) in db.Query("SELECT fileid, cat FROM file_cat_items", r => (r.GetInt64(0), r.GetString(1)))) if (cats.Contains(cat)) result.Add(f);
        foreach (var t in tags)
            foreach (var f in db.Query("SELECT fileid FROM file_tags WHERE tag = $t COLLATE NOCASE", r => r.GetInt64(0), ("$t", t))) result.Add(f);
        return result;
    }

    /// <summary>Kategorien und Tags je Datei (fileid).</summary>
    public static Dictionary<long, (List<Assigned> Cats, List<string> Tags)> Of(UserDb db, IEnumerable<long> fileIds)
    {
        Ensure(db);
        var result = new Dictionary<long, (List<Assigned>, List<string>)>();
        foreach (var chunk in fileIds.Distinct().Chunk(400))
        {
            var names = chunk.Select((_, i) => "$f" + i).ToList();
            var args = chunk.Select((f, i) => (names[i], (object?)f)).ToArray();
            var inList = string.Join(",", names);
            foreach (var f in chunk) result[f] = ([], []);
            foreach (var (f, cat, auto) in db.Query($"SELECT fileid, cat, auto FROM file_cat_items WHERE fileid IN ({inList})", r => (r.GetInt64(0), r.GetString(1), r.GetInt64(2) != 0), args))
                result[f].Item1.Add(new Assigned(cat, auto));
            foreach (var (f, tag) in db.Query($"SELECT fileid, tag FROM file_tags WHERE fileid IN ({inList}) ORDER BY tag COLLATE NOCASE", r => (r.GetInt64(0), r.GetString(1)), args))
                result[f].Item2.Add(tag);
        }
        return result;
    }

    // ---------------- Zuordnen ----------------

    /// <summary>
    /// Von Hand: Kategorien setzen oder entfernen, Tags dazu oder weg. Gesetzt = gehört dazu (bei einer automatischen: bestaetigt),
    /// entfernt = gehört nicht dazu - beides lernt, wenn die Kategorie lernt.
    /// </summary>
    public static void Set(UserSpace space, IEnumerable<FileEntry> files, IEnumerable<string>? add, IEnumerable<string>? remove,
        IEnumerable<string>? addTags = null, IEnumerable<string>? removeTags = null)
    {
        var db = space.Db;
        var all = All(db);
        var byId = all.ToDictionary(c => c.Id);
        var plus = (add ?? []).Distinct().ToList();
        var minus = (remove ?? []).Distinct().ToList();
        foreach (var c in plus.Concat(minus)) if (!byId.ContainsKey(c)) throw new KeyNotFoundException("Kategorie nicht gefunden.");
        var tagsPlus = (addTags ?? []).Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        if (tagsPlus.Any(t => t.Length > 60)) throw new ArgumentException("Ein Tag ist zu lang (höchstens 60 Zeichen).");
        var tagsMinus = (removeTags ?? []).Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
        var root = space.Settings.Notizi.Root ?? "";
        foreach (var f in files)
        {
            if (f.IsDir || f.FileId == 0 || FileStore.InRoot(f.Path, root)) continue;
            HashSet<string>? tokens = null;
            HashSet<string> Tok() => tokens ??= Tokens(db, f);
            foreach (var c in plus)
            {
                var was = db.Scalar("SELECT auto FROM file_cat_items WHERE fileid=$f AND cat=$c", ("$f", f.FileId), ("$c", c));
                if (was is long a && a == 0) continue;
                if (was == null) db.Exec("INSERT INTO file_cat_items(fileid, cat, auto, added) VALUES($f,$c,0,$n)", ("$f", f.FileId), ("$c", c), ("$n", Unix()));
                else db.Exec("UPDATE file_cat_items SET auto = 0 WHERE fileid=$f AND cat=$c", ("$f", f.FileId), ("$c", c));
                // Gehört dazu - auch zu den Oberkategorien
                foreach (var x in Above(all, c).Prepend(c))
                    if (byId[x].Learn) Train(db, f.FileId, Tok(), x, true);
            }
            foreach (var c in minus)
            {
                if (db.Exec("DELETE FROM file_cat_items WHERE fileid=$f AND cat=$c", ("$f", f.FileId), ("$c", c)) == 0) continue;
                if (byId[c].Learn) Train(db, f.FileId, Tok(), c, false);
            }
            foreach (var t in tagsPlus) db.Exec("INSERT OR IGNORE INTO file_tags(fileid, tag) VALUES($f,$t)", ("$f", f.FileId), ("$t", t));
            foreach (var t in tagsMinus) db.Exec("DELETE FROM file_tags WHERE fileid=$f AND tag=$t COLLATE NOCASE", ("$f", f.FileId), ("$t", t));
        }
    }

    // ---------------- Lernen ----------------

    /// <summary>Merkmale einer Datei: Woerter im Namen, Dateityp, Ordnernamen, der ganze Ordner und - wenn gelesen - ihr Text.</summary>
    internal static HashSet<string> Tokens(UserDb db, FileEntry f)
    {
        var t = new HashSet<string>();
        foreach (var w in SpamFilter.Words(Path.GetFileNameWithoutExtension(f.Name))) t.Add("n:" + w);
        var ext = Path.GetExtension(f.Name).TrimStart('.').ToLowerInvariant();
        if (ext.Length > 0) t.Add("ext:" + ext);
        var parent = FileStore.Parent(f.Path) ?? "";
        if (parent.Length > 0)
        {
            t.Add("in:" + parent.ToLowerInvariant());
            foreach (var seg in parent.Split('/')) t.Add("d:" + seg.ToLowerInvariant());
        }
        var body = db.Query(@"SELECT x.body FROM text_docs d JOIN text_fts x ON x.rowid = d.id WHERE d.kind = 'file' AND d.path = $p AND d.status = 'ok' LIMIT 1",
            r => r.GetString(0), ("$p", f.Path)).FirstOrDefault();
        if (body != null)
            foreach (var w in SpamFilter.Words(body.Length > 6000 ? body[..6000] : body)) t.Add(w);
        return t;
    }

    /// <summary>Schon so gelernt: nichts; anders gelernt: umlernen.</summary>
    private static void Train(UserDb db, long fileId, HashSet<string> tokens, string cat, bool positive)
    {
        var label = positive ? 1 : -1;
        var before = db.Scalar("SELECT label FROM fcat_trained WHERE fileid=$f AND cat=$c", ("$f", fileId), ("$c", cat)) is long b ? (int)b : 0;
        if (before == label) return;
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        foreach (var t in tokens)
        {
            if (before == 1) UserDb.Exec(conn, "UPDATE fcat_tokens SET pos = MAX(0, pos - 1) WHERE token=$t AND cat=$c", ("$t", t), ("$c", cat));
            if (before == -1) UserDb.Exec(conn, "UPDATE fcat_tokens SET neg = MAX(0, neg - 1) WHERE token=$t AND cat=$c", ("$t", t), ("$c", cat));
            UserDb.Exec(conn, positive
                ? "INSERT INTO fcat_tokens(token, cat, pos) VALUES($t,$c,1) ON CONFLICT(token, cat) DO UPDATE SET pos = pos + 1"
                : "INSERT INTO fcat_tokens(token, cat, neg) VALUES($t,$c,1) ON CONFLICT(token, cat) DO UPDATE SET neg = neg + 1", ("$t", t), ("$c", cat));
        }
        UserDb.Exec(conn, "INSERT INTO fcat_trained(fileid, cat, label) VALUES($f,$c,$l) ON CONFLICT(fileid, cat) DO UPDATE SET label=$l", ("$f", fileId), ("$c", cat), ("$l", label));
        tx.Commit();
    }

    // ---------------- Entscheiden ----------------

    /// <summary>Die Kategorien, die eine neue Datei automatisch bekommt (nur die genaueste, wenn auch Oberkategorien passen).</summary>
    public static List<string> Decide(UserDb db, FileEntry f)
    {
        var all = All(db);
        var candidates = all.Where(c => c.Learn && c.Auto).ToList();
        if (candidates.Count == 0) return [];
        var tokens = Tokens(db, f);
        // Gelernte Woerter dieser Datei, nach Wort gruppiert
        var byToken = new Dictionary<string, Dictionary<string, (int Pos, int Neg)>>();
        using (var conn = db.Open())
            foreach (var chunk in tokens.Chunk(400))
            {
                var names = chunk.Select((_, i) => "$t" + i).ToList();
                foreach (var (tok, cat, p, n) in UserDb.Query(conn, $"SELECT token, cat, pos, neg FROM fcat_tokens WHERE (pos > 0 OR neg > 0) AND token IN ({string.Join(",", names)})",
                             r => (r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3)), chunk.Select((t, i) => (names[i], (object?)t)).ToArray()))
                {
                    if (!byToken.TryGetValue(tok, out var per)) byToken[tok] = per = new();
                    per[cat] = (p, n);
                }
            }
        var trained = db.Query("SELECT cat, COALESCE(SUM(label > 0),0), COALESCE(SUM(label < 0),0) FROM fcat_trained GROUP BY cat",
            r => (r.GetString(0), r.GetInt32(1), r.GetInt32(2))).ToDictionary(x => x.Item1, x => (Pos: x.Item2, Neg: x.Item3));
        var refused = db.Query("SELECT cat FROM fcat_trained WHERE fileid=$f AND label < 0", r => r.GetString(0), ("$f", f.FileId)).ToHashSet();
        var folder = "in:" + (FileStore.Parent(f.Path) ?? "").ToLowerInvariant();

        var yes = new List<string>();
        foreach (var c in candidates)
        {
            if (refused.Contains(c.Id)) continue;
            // Ober- und Unterkategorien zaehlen nicht als „der Rest“
            var related = Below(all, c.Id);
            related.UnionWith(Above(all, c.Id));
            bool? decided = null;
            if (folder != "in:" && byToken.TryGetValue(folder, out var inFolder) && inFolder.TryGetValue(c.Id, out var fs))
            {
                if (fs.Pos >= 2 && fs.Neg == 0) decided = true;
                else if (fs.Neg >= 2 && fs.Pos == 0) decided = false;
            }
            decided ??= Bayes(c.Id, related, tokens, byToken, trained);
            if (decided == true) yes.Add(c.Id);
        }
        return yes.Where(c => !yes.Any(o => o != c && Above(all, o).Contains(c))).ToList();
    }

    private static bool? Bayes(string cat, HashSet<string> related, HashSet<string> tokens,
        Dictionary<string, Dictionary<string, (int Pos, int Neg)>> byToken, Dictionary<string, (int Pos, int Neg)> trained) =>
        Learner.Decide(Learner.Probability(cat, related, tokens, byToken, trained));

    // ---------------- Neue Dateien einordnen ----------------

    /// <summary>Wartende neue Dateien einordnen. Mit Inhaltssuche warten sie (bis zu einer Stunde) auf ihren Text.</summary>
    public static int ProcessQueue(UserSpace space, int max = 200, CancellationToken ct = default)
    {
        var db = space.Db;
        Ensure(db);
        var queued = db.Query("SELECT path, added FROM fcat_queue ORDER BY added LIMIT $n", r => (Path: r.GetString(0), Added: r.GetInt64(1)), ("$n", max));
        if (queued.Count == 0) return 0;
        var waitText = space.Settings.TextIndex.Enabled;
        var root = space.Settings.Notizi.Root ?? "";
        int done = 0;
        foreach (var (path, added) in queued)
        {
            if (ct.IsCancellationRequested) break;
            var f = FileStore.Get(db, path);
            if (f != null && !f.IsDir && f.FileId != 0 && !FileStore.InRoot(path, root))
            {
                if (waitText && Unix() - added < WaitForText && TextExtract.Supported(f.Name, f.Mime)
                    && db.Scalar("SELECT 1 FROM text_queue WHERE kind = 'file' AND ref = $p", ("$p", path)) != null)
                    continue;
                foreach (var c in Decide(db, f))
                    db.Exec("INSERT OR IGNORE INTO file_cat_items(fileid, cat, auto, added) VALUES($f,$c,1,$n)", ("$f", f.FileId), ("$c", c), ("$n", Unix()));
            }
            db.Exec("DELETE FROM fcat_queue WHERE path=$p", ("$p", path));
            done++;
        }
        return done;
    }

    /// <summary>
    /// Vorhandene Dateien ohne Kategorie nachträglich einordnen: in die Warteschlange (wie neue Dateien), der Job arbeitet sie
    /// in Portionen ab. Nur mit einer lernenden, automatischen Kategorie; nicht im notizi-Ordner. Gibt die Zahl zurück.
    /// </summary>
    /// <param name="folders">Nur in diesen Ordnern (samt Unterordnern); „“ = nur die Dateien direkt in „Meine Dateien“. Null: überall.</param>
    public static int QueueExisting(UserSpace space, IReadOnlyCollection<string>? folders = null)
    {
        var db = space.Db;
        Ensure(db);
        if (db.Scalar("SELECT 1 FROM file_cats WHERE auto = 1 AND learn = 1 LIMIT 1") == null) return 0;
        var paths = Uncategorized(space)
            .Where(p => folders == null || folders.Any(f => f.Length == 0 ? !p.Contains('/') : FileStore.InRoot(p, f) && p.Length > f.Length)).ToList();
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        // Etwas in der Vergangenheit, damit sie nicht auf die Inhaltssuche warten (der Text ist bei altem Bestand meist schon da)
        var added = Unix() - WaitForText;
        foreach (var p in paths) UserDb.Exec(conn, "INSERT OR IGNORE INTO fcat_queue(path, added) VALUES($p, $a)", ("$p", p), ("$a", added));
        tx.Commit();
        return paths.Count;
    }

    /// <summary>
    /// Vor „Vorhandene Dateien einordnen“: wie viele Dateien ohne Kategorie es gibt (eigene Ablage, ohne notizi-Ordner) und
    /// welche Kategorien schon genug gelernt haben, um überhaupt etwas zuzuordnen (je mindestens <see cref="Learner.MinTrained"/>
    /// von Hand zugeordnete und entfernte Dateien).
    /// </summary>
    public static (int Files, List<string> Ready, List<string> Learning) ExistingPreview(UserSpace space)
    {
        var db = space.Db;
        Ensure(db);
        var files = Uncategorized(space).Count;
        var trained = db.Query("SELECT cat, COALESCE(SUM(label > 0),0), COALESCE(SUM(label < 0),0) FROM fcat_trained GROUP BY cat",
            r => (r.GetString(0), r.GetInt32(1), r.GetInt32(2))).ToDictionary(x => x.Item1, x => (Pos: x.Item2, Neg: x.Item3));
        var all = All(db);
        var auto = all.Where(c => c.Learn && c.Auto).ToList();
        // Wie Learner.Probability: abgelehnt zählt, und was zu anderen (nicht verwandten) Kategorien gehört, ist „der Rest“
        bool Enough(Category c)
        {
            var related = Below(all, c.Id); related.UnionWith(Above(all, c.Id));
            var pos = trained.TryGetValue(c.Id, out var own) ? own.Pos : 0;
            var neg = (trained.TryGetValue(c.Id, out var o2) ? o2.Neg : 0) + trained.Where(x => x.Key != c.Id && !related.Contains(x.Key)).Sum(x => x.Value.Pos);
            return pos >= Learner.MinTrained && neg >= Learner.MinTrained;
        }
        return (files, auto.Where(Enough).Select(c => c.Name).ToList(), auto.Where(c => !Enough(c)).Select(c => c.Name).ToList());
    }

    /// <summary>Dateien ohne Kategorie in der eigenen Ablage (ohne notizi-Ordner und Tresor-Ordner).</summary>
    private static List<string> Uncategorized(UserSpace space)
    {
        var root = space.Settings.Notizi.Root ?? "";
        return space.Db.Query("SELECT path FROM files WHERE is_dir = 0 AND COALESCE(fileid, 0) <> 0 AND fileid NOT IN (SELECT fileid FROM file_cat_items)", r => r.GetString(0))
            .Where(p => !FileStore.InRoot(p, root) && !FileStore.InRoot(p, FileStore.SystemVault)).ToList();
    }

    /// <summary>
    /// Probelauf für den Zuordnungsassistenten: einige zufällige Dateien ohne Kategorie aus den gewählten Ordnern und was das
    /// Gelernte ihnen geben würde (leer: keine passende Kategorie). Schreibt nichts.
    /// </summary>
    public static List<(string Path, List<string> Cats)> Sample(UserSpace space, IReadOnlyCollection<string>? folders, int n = 12)
    {
        var db = space.Db;
        Ensure(db);
        var names = All(db).ToDictionary(c => c.Id, c => c.Name);
        var pick = Uncategorized(space)
            .Where(p => folders == null || folders.Any(f => f.Length == 0 ? !p.Contains('/') : FileStore.InRoot(p, f) && p.Length > f.Length))
            .OrderBy(_ => Random.Shared.Next()).Take(n).ToList();
        var outp = new List<(string, List<string>)>();
        foreach (var p in pick)
            if (FileStore.Get(db, p) is { IsDir: false } f)
                outp.Add((p, Decide(db, f).Select(c => names.GetValueOrDefault(c, c)).ToList()));
        return outp;
    }

    /// <summary>Für den Zuordnungsassistenten: Dateien ohne Kategorie je Ordner der ersten und zweiten Ebene („“ = direkt in „Meine Dateien“).</summary>
    public static List<(string Folder, int Count)> UncategorizedByFolder(UserSpace space)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var p in Uncategorized(space))
        {
            var parts = p.Split('/');
            if (parts.Length == 1) { counts[""] = counts.GetValueOrDefault("") + 1; continue; }
            counts[parts[0]] = counts.GetValueOrDefault(parts[0]) + 1;
            if (parts.Length > 2) { var sub = parts[0] + "/" + parts[1]; counts[sub] = counts.GetValueOrDefault(sub) + 1; }
        }
        return counts.OrderBy(x => x.Key, StringComparer.CurrentCultureIgnoreCase).Select(x => (x.Key, x.Value)).ToList();
    }

    /// <summary>Unbestätigte (automatische) Zuordnungen entfernen - einer Kategorie oder aller. Von Hand Zugeordnetes bleibt.
    /// Das Entfernen lehrt nichts (anders als Entfernen von Hand an einer Datei).</summary>
    public static int RemoveUnconfirmed(UserDb db, string? cat)
    {
        Ensure(db);
        return cat == null
            ? db.Exec("DELETE FROM file_cat_items WHERE auto = 1")
            : db.Exec("DELETE FROM file_cat_items WHERE auto = 1 AND cat = $c", ("$c", cat));
    }

    /// <summary>Was an endgueltig verschwundenen Dateien hing (nicht mehr da und nicht im Papierkorb).</summary>
    public static void Orphans(UserDb db)
    {
        Ensure(db);
        const string gone = "NOT IN (SELECT fileid FROM files WHERE fileid IS NOT NULL) AND fileid NOT IN (SELECT fileid FROM file_trash_items WHERE fileid IS NOT NULL)";
        foreach (var t in new[] { "file_cat_items", "file_tags" })
            db.Exec($"DELETE FROM {t} WHERE fileid {gone}");
    }
}

/// <summary>Hintergrund: neue Dateien einordnen (FileCategories.ProcessQueue), stündlich Verwaistes wegräumen.</summary>
public sealed class FileCategoryJob(Spaces spaces, ILogger<FileCategoryJob> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Release-Liste: in dieser Version nicht freigeschaltet - nichts im Hintergrund
        if (!DmOffice.Core.Features.On(DmOffice.Core.Features.FileCategories)) return;
        try { await Task.Delay(TimeSpan.FromSeconds(40), ct); } catch (OperationCanceledException) { return; }
        var cleaned = DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            var clean = DateTime.UtcNow - cleaned > TimeSpan.FromHours(1);
            await Task.Run(() =>
            {
                foreach (var space in spaces.All())
                {
                    if (ct.IsCancellationRequested) return;
                    try
                    {
                        FileCategories.ProcessQueue(space, ct: ct);
                        if (clean) FileCategories.Orphans(space.Db);
                    }
                    catch (Exception ex) { log.LogWarning("Datei-Kategorien {User}: {Error}", space.User.Login, ex.Message); }
                }
            }, CancellationToken.None);
            if (clean) cleaned = DateTime.UtcNow;
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch (OperationCanceledException) { return; }
        }
    }
}
