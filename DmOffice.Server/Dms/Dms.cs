using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using DmOffice.Core;

namespace DmOffice.Dms;

/// <summary>Ein Eintrag einer der drei Listen: Korrespondent (WER), Dokumenttyp (WAS) oder Tag (THEMA).</summary>
public sealed record DmsEntry(string Id, string Kind, string? Parent, string Name, string? Color, string Match, string? MatchText,
    bool CaseSensitive, int Count = 0, string? LastUsed = null);

/// <summary>Ein Tag an einem Dokument; Auto = vom DMS gesetzt und noch nicht bestätigt.</summary>
public sealed record DmsDocTag(string Id, bool Auto);

/// <summary>Ein Dokument mit seinen Angaben (für Web und App).</summary>
public sealed record DmsDoc(long Id, long FileId, string? Path, string State, string Title, string? DocDate, DateTime Imported,
    string? Correspondent, string? Doctype, List<DmsDocTag> Tags, bool Inbox, bool ByAi, string? Error, string OriginalName, string? Mime,
    long Size, bool HasOriginal, bool Archive, bool PdfA, string? Profile, string Source, string? Sender, bool AutoCorrespondent,
    bool AutoDoctype, bool Manual, string? AiSuggestion, bool Trashed, Dms.PaperlessValues? Paperless = null,
    /// <summary>Externer Ordner als Speicherort (Kennung des Ordners); Path ist dann der Pfad darin.</summary>
    string? Mount = null,
    /// <summary>Rolle (etwa „Testament“) und ob dieses Dokument sie gerade innehat - sonst eine frühere Version, ersetzt am RoleReplaced.</summary>
    string? Role = null, bool RoleCurrent = false, DateTime? RoleReplaced = null,
    /// <summary>Betrag in Cent (von Hand eingetragen), null: keiner.</summary>
    long? Amount = null,
    /// <summary>Fixiert: Inhalt nicht änderbar, Dokument nicht löschbar, bis die Fixierung bewusst aufgehoben wird.</summary>
    bool Fixed = false);

/// <summary>
/// DMS (docs/dms-konzept.md): Dokumente sind Dateien der Dateiablage; DMS führt seine Angaben in eigenen Tabellen (dms_*)
/// an der Datei-Kennung. Hier: Tabellen, Listen (WER, WAS, THEMA), Abfragen und das Setzen von Angaben samt Lernen.
/// Andere Teile von dmOffice lesen die dms_-Tabellen nicht; das DMS liest fremde Tabellen nur über <see cref="DmsHost"/>.
/// </summary>
public static class Dms
{
    public const string Correspondent = "correspondent", Doctype = "doctype", Tag = "tag", Role = "role";

    /// <summary>Angaben aus paperless zum Vergleich (Übernahme, docs/dms-konzept.md); Differs: weicht von den Angaben im DMS ab.</summary>
    public sealed record PaperlessValues(string? Correspondent, string? Doctype, List<string> Tags, bool Differs);

    /// <summary>Weicht die Einordnung von der aus paperless ab? (Tags als sortierte Liste verglichen)</summary>
    internal const string PlDiffers = @"(d.pl = 1 AND (COALESCE(d.correspondent, '') <> COALESCE(d.pl_correspondent, '') OR COALESCE(d.doctype, '') <> COALESCE(d.pl_doctype, '')
        OR COALESCE((SELECT group_concat(tag, ',') FROM (SELECT tag FROM dms_doc_tags WHERE doc = d.id ORDER BY tag)), '') <> COALESCE(d.pl_tags, '')))";
    public static readonly string[] Kinds = [Correspondent, Doctype, Tag, Role];

    /// <summary>Arten der Zuweisung.</summary>
    public const string Auto = "auto", None = "none", Any = "any", All = "all", Exact = "exact", Regex = "regex", Sender = "sender";
    public static readonly string[] Matches = [Auto, None, Any, All, Exact, Regex, Sender];

    private static readonly ConditionalWeakTable<object, object> Ready = new();
    internal static long Unix() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>Tabellen vom DMS - in der Benutzerdatenbank und (Dokumente, Text, Gelerntes) in der Tresor-Datenbank.</summary>
    public const string Schema = @"CREATE TABLE IF NOT EXISTS dms_tiles (doc INTEGER NOT NULL, width INTEGER NOT NULL, stamp TEXT NOT NULL, data BLOB NOT NULL,
    PRIMARY KEY(doc, width));
CREATE TABLE IF NOT EXISTS dms_lists (id TEXT PRIMARY KEY, kind TEXT NOT NULL, parent TEXT NULL, name TEXT NOT NULL, color TEXT NULL,
    match TEXT NOT NULL DEFAULT 'auto', match_text TEXT NULL, case_sensitive INTEGER NOT NULL DEFAULT 0);
CREATE TABLE IF NOT EXISTS dms_docs (id INTEGER PRIMARY KEY AUTOINCREMENT, fileid INTEGER NULL, state TEXT NOT NULL DEFAULT 'queued',
    title TEXT NOT NULL DEFAULT '', doc_date TEXT NULL, imported INTEGER NOT NULL, correspondent TEXT NULL, doctype TEXT NULL,
    auto_correspondent INTEGER NOT NULL DEFAULT 0, auto_doctype INTEGER NOT NULL DEFAULT 0, original_name TEXT NOT NULL, mime TEXT NULL,
    size INTEGER NOT NULL DEFAULT 0, original_blob TEXT NULL, archive INTEGER NOT NULL DEFAULT 0, pdfa INTEGER NOT NULL DEFAULT 0,
    sha256 TEXT NOT NULL, source TEXT NOT NULL DEFAULT 'upload', source_ref TEXT NULL, sender TEXT NULL, profile TEXT NULL,
    placed_path TEXT NULL, etag TEXT NULL, manual INTEGER NOT NULL DEFAULT 0, inbox INTEGER NOT NULL DEFAULT 1, by_ai INTEGER NOT NULL DEFAULT 0,
    ai_suggestion TEXT NULL, presets TEXT NULL, tries INTEGER NOT NULL DEFAULT 0, error TEXT NULL,
    pl INTEGER NOT NULL DEFAULT 0, pl_correspondent TEXT NULL, pl_doctype TEXT NULL, pl_tags TEXT NULL);
CREATE INDEX IF NOT EXISTS dms_docs_fileid ON dms_docs(fileid);
CREATE INDEX IF NOT EXISTS dms_docs_sha ON dms_docs(sha256);
CREATE TABLE IF NOT EXISTS dms_doc_tags (doc INTEGER NOT NULL, tag TEXT NOT NULL, auto INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(doc, tag)) WITHOUT ROWID;
CREATE INDEX IF NOT EXISTS dms_doc_tags_tag ON dms_doc_tags(tag);
CREATE TABLE IF NOT EXISTS dms_text (doc INTEGER PRIMARY KEY, body TEXT NOT NULL, method TEXT NOT NULL);
CREATE VIRTUAL TABLE IF NOT EXISTS dms_fts USING fts5(title, body, tokenize = 'unicode61 remove_diacritics 2');
CREATE TABLE IF NOT EXISTS dms_tokens (token TEXT NOT NULL, entry TEXT NOT NULL, pos INTEGER NOT NULL DEFAULT 0, neg INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(token, entry)) WITHOUT ROWID;
CREATE TABLE IF NOT EXISTS dms_trained (doc INTEGER NOT NULL, entry TEXT NOT NULL, label INTEGER NOT NULL, PRIMARY KEY(doc, entry)) WITHOUT ROWID;
CREATE TABLE IF NOT EXISTS dms_seen (source TEXT NOT NULL, path TEXT NOT NULL, etag TEXT NOT NULL, PRIMARY KEY(source, path)) WITHOUT ROWID;";

    public static void Ensure(ISqlStore db)
    {
        var key = db is DmsStore st ? st.Sql : db;
        if (Ready.TryGetValue(key, out _)) return;
        // Einer zugleich: gleichzeitige erste Anfragen fügten sonst dieselbe Spalte zweimal hinzu („duplicate column“)
        lock (key) EnsureLocked(db, key);
    }

    private static void EnsureLocked(ISqlStore db, object key)
    {
        if (Ready.TryGetValue(key, out _)) return;
        db.Exec(Schema);
        // Spalten, die später dazukamen (Übernahme aus paperless: dessen Angaben zum Vergleich)
        var have = db.Query("SELECT name FROM pragma_table_info('dms_docs')", r => r.GetString(0)).ToHashSet();
        // Externer Ordner als Speicherort: Ordner, Pfad, Original im Unterordner Original/, Prüfsumme der abgelegten Datei (Wiedererkennen),
        // Angaben noch in die PDF zu schreiben
        foreach (var (col, type) in new[] { ("pl", "INTEGER NOT NULL DEFAULT 0"), ("pl_correspondent", "TEXT NULL"), ("pl_doctype", "TEXT NULL"), ("pl_tags", "TEXT NULL"),
                     ("ext_mount", "TEXT NULL"), ("ext_path", "TEXT NULL"), ("ext_original", "TEXT NULL"), ("ext_sha", "TEXT NULL"), ("meta_dirty", "INTEGER NOT NULL DEFAULT 0"),
                     ("role", "TEXT NULL"), ("role_since", "INTEGER NULL"), ("amount", "INTEGER NULL"), ("fixed", "INTEGER NOT NULL DEFAULT 0") })
            if (!have.Contains(col)) db.Exec($"ALTER TABLE dms_docs ADD COLUMN {col} {type}");
        // Wer eine Rolle gerade innehat - für das ganze DMS (offener Bereich und Tresor), darum in der Benutzerdatenbank
        (db is DmsStore ds ? ds.User : db).Exec("CREATE TABLE IF NOT EXISTS dms_roles (role TEXT PRIMARY KEY, tresor INTEGER NOT NULL, doc INTEGER NOT NULL, since INTEGER NOT NULL)");
        Ready.AddOrUpdate(key, new object());
    }

    // ---------------- Listen ----------------

    public static List<DmsEntry> Entries(UserDb db, string? kind = null)
    {
        Ensure(db);
        return db.Query("SELECT id, kind, parent, name, color, match, match_text, case_sensitive FROM dms_lists WHERE $k IS NULL OR kind = $k ORDER BY name COLLATE NOCASE",
            r => new DmsEntry(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
                r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.GetInt64(7) != 0), ("$k", kind));
    }

    /// <summary>Alle Einträge mit Anzahl der Dokumente (auch der Unterbegriffe) und dem Dokumentdatum des neuesten.</summary>
    public static List<DmsEntry> EntriesWithCounts(UserDb db)
    {
        Ensure(db);
        var all = Entries(db);
        var direct = new Dictionary<string, List<(long Doc, string? Date)>>();
        void Add(string id, long doc, string? date) { if (!direct.TryGetValue(id, out var l)) direct[id] = l = []; l.Add((doc, date)); }
        foreach (var (doc, c, t, date, role) in db.Query($"SELECT id, correspondent, doctype, doc_date, role FROM dms_docs WHERE state <> 'queued' AND {Visible}",
                     r => (r.GetInt64(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4))))
        {
            if (c != null) Add(c, doc, date);
            if (t != null) Add(t, doc, date);
            if (role != null) Add(role, doc, date);
        }
        foreach (var (doc, tag, date) in db.Query($"SELECT d.id, g.tag, d.doc_date FROM dms_doc_tags g JOIN dms_docs d ON d.id = g.doc WHERE d.state <> 'queued' AND {VisibleD}",
                     r => (r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2))))
            Add(tag, doc, date);
        return all.Select(e =>
        {
            var docs = Below(all, e.Id).SelectMany(x => direct.GetValueOrDefault(x) ?? []).DistinctBy(x => x.Doc).ToList();
            return e with { Count = docs.Count, LastUsed = docs.Select(x => x.Date).Where(d => d != null).Max() };
        }).ToList();
    }

    /// <summary>Der Eintrag und alle darunter.</summary>
    public static HashSet<string> Below(List<DmsEntry> all, string id)
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

    /// <summary>Die Oberbegriffe (ohne den Eintrag selbst), der nächste zuerst.</summary>
    public static List<string> Above(List<DmsEntry> all, string id)
    {
        var byId = all.ToDictionary(c => c.Id);
        var list = new List<string>();
        for (var c = byId.GetValueOrDefault(id); c?.Parent is { } p && byId.TryGetValue(p, out var up) && !list.Contains(p) && p != id; c = up) list.Add(p);
        return list;
    }

    /// <summary>Namen mit Oberbegriffen, der oberste zuerst.</summary>
    public static List<string> PathOf(List<DmsEntry> all, string id)
    {
        var byId = all.ToDictionary(c => c.Id);
        return Above(all, id).AsEnumerable().Reverse().Append(id).Select(x => byId.GetValueOrDefault(x)?.Name ?? "?").ToList();
    }

    /// <summary>Für Titel und Namen: Zeilenumbrüche und Tabs werden Leerzeichen, Steuerzeichen und Zeichen, die die Schreibrichtung umstellen, entfallen.</summary>
    public static string CleanText(string text) =>
        new string(text.Select(c => c is '\r' or '\n' or '\t' ? ' ' : c).Where(c => !Gallery.ImportProfiles.Invisible(c)).ToArray()).Trim();

    private static string CleanName(string? name)
    {
        var n = CleanText(name ?? "");
        if (n.Length == 0) throw new ArgumentException("Bitte einen Namen eingeben.");
        if (n.Length > 120) throw new ArgumentException("Der Name ist zu lang (höchstens 120 Zeichen).");
        return n;
    }

    private static void CheckMatch(string match, string? text)
    {
        if (!Matches.Contains(match)) throw new ArgumentException("Unbekannte Zuweisung.");
        if (match is Any or All or Exact or Regex or Sender && string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Für diese Zuweisung bitte Wörter bzw. einen Ausdruck angeben.");
        if (match == Regex)
            try { _ = new System.Text.RegularExpressions.Regex(text!, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1)); }
            catch (ArgumentException) { throw new ArgumentException("Der reguläre Ausdruck ist ungültig."); }
    }

    public static DmsEntry Create(UserDb db, string kind, string? name, string? parent, string? color, string? match, string? matchText, bool caseSensitive)
    {
        if (!Kinds.Contains(kind)) throw new ArgumentException("Unbekannte Liste.");
        var all = Entries(db, kind);
        var n = CleanName(name);
        if (parent != null && all.All(c => c.Id != parent)) throw new KeyNotFoundException("Oberbegriff nicht gefunden.");
        if (all.Any(c => c.Parent == parent && string.Equals(c.Name, n, StringComparison.CurrentCultureIgnoreCase)))
            throw new ArgumentException($"„{n}“ gibt es hier schon.");
        var m = match ?? Auto;
        if (m == Sender && kind != Correspondent) throw new ArgumentException("„Absender“ gibt es nur für Korrespondenten.");
        // Rollen: ohne Oberbegriff und ohne Zuweisung - eine Rolle vergibt man bewusst
        if (kind == Role) { if (parent != null) throw new ArgumentException("Rollen haben keine Oberbegriffe."); m = None; matchText = null; }
        CheckMatch(m, matchText);
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        db.Exec("INSERT INTO dms_lists(id, kind, parent, name, color, match, match_text, case_sensitive) VALUES($i,$k,$p,$n,$c,$m,$t,$s)",
            ("$i", id), ("$k", kind), ("$p", parent), ("$n", n), ("$c", color), ("$m", m), ("$t", matchText?.Trim()), ("$s", caseSensitive ? 1 : 0));
        return new DmsEntry(id, kind, parent, n, color, m, matchText?.Trim(), caseSensitive);
    }

    /// <summary>Ändern: nur, was angegeben ist. Neuer Oberbegriff "" = oberste Ebene, Farbe "" = keine.</summary>
    public static DmsEntry Update(UserDb db, string id, string? name, string? parent, string? color, string? match, string? matchText, bool? caseSensitive)
    {
        var e = Entries(db).FirstOrDefault(x => x.Id == id) ?? throw new KeyNotFoundException("Eintrag nicht gefunden.");
        var all = Entries(db, e.Kind);
        var newParent = e.Kind == Role ? null : parent == null ? e.Parent : parent.Length == 0 ? null : parent;
        if (newParent != null)
        {
            if (all.All(x => x.Id != newParent)) throw new KeyNotFoundException("Oberbegriff nicht gefunden.");
            if (Below(all, id).Contains(newParent)) throw new ArgumentException("Ein Eintrag kann nicht unter sich selbst liegen.");
        }
        var n = name == null ? e.Name : CleanName(name);
        if (all.Any(c => c.Parent == newParent && c.Id != id && string.Equals(c.Name, n, StringComparison.CurrentCultureIgnoreCase)))
            throw new ArgumentException($"„{n}“ gibt es hier schon.");
        var m = e.Kind == Role ? None : match ?? e.Match;
        if (m == Sender && e.Kind != Correspondent) throw new ArgumentException("„Absender“ gibt es nur für Korrespondenten.");
        var t = e.Kind == Role ? null : matchText == null ? e.MatchText : matchText.Trim();
        CheckMatch(m, t);
        var col = color == null ? e.Color : color.Length == 0 ? null : color;
        var cs = caseSensitive ?? e.CaseSensitive;
        db.Exec("UPDATE dms_lists SET parent=$p, name=$n, color=$c, match=$m, match_text=$t, case_sensitive=$s WHERE id=$i",
            ("$i", id), ("$p", newParent), ("$n", n), ("$c", col), ("$m", m), ("$t", t), ("$s", cs ? 1 : 0));
        return e with { Parent = newParent, Name = n, Color = col, Match = m, MatchText = t, CaseSensitive = cs };
    }

    /// <summary>Löschen: Unterbegriffe rücken eine Ebene hoch, an den Dokumenten verschwindet der Eintrag, die Dokumente bleiben.</summary>
    public static List<long> Delete(UserDb db, string id)
    {
        var e = Entries(db).FirstOrDefault(x => x.Id == id) ?? throw new KeyNotFoundException("Eintrag nicht gefunden.");
        var docs = db.Query("SELECT id FROM dms_docs WHERE correspondent = $i OR doctype = $i UNION SELECT doc FROM dms_doc_tags WHERE tag = $i",
            r => r.GetInt64(0), ("$i", id));
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        UserDb.Exec(conn, "UPDATE dms_lists SET parent=$p WHERE parent=$i", ("$p", e.Parent), ("$i", id));
        UserDb.Exec(conn, "UPDATE dms_docs SET correspondent = NULL, auto_correspondent = 0 WHERE correspondent = $i", ("$i", id));
        UserDb.Exec(conn, "UPDATE dms_docs SET doctype = NULL, auto_doctype = 0 WHERE doctype = $i", ("$i", id));
        UserDb.Exec(conn, "UPDATE dms_docs SET role = NULL, role_since = NULL WHERE role = $i", ("$i", id));
        UserDb.Exec(conn, "DELETE FROM dms_roles WHERE role = $i", ("$i", id));
        foreach (var t in new[] { "dms_doc_tags WHERE tag", "dms_tokens WHERE entry", "dms_trained WHERE entry" })
            UserDb.Exec(conn, $"DELETE FROM {t} = $i", ("$i", id));
        UserDb.Exec(conn, "DELETE FROM dms_lists WHERE id=$i", ("$i", id));
        tx.Commit();
        return docs;
    }

    /// <summary>Vorschlag für den Anfang (nur bei leerer Liste): Dokumenttypen.</summary>
    public static int CreateSuggestion(UserDb db, string? lang)
    {
        if (Entries(db, Doctype).Count > 0) return 0;
        string[] names = lang == "en"
            ? ["Invoice", "Contract", "Certificate", "Notice", "Letter", "Offer", "Manual"]
            : ["Rechnung", "Vertrag", "Bescheinigung", "Bescheid", "Brief", "Angebot", "Anleitung"];
        foreach (var n in names) Create(db, Doctype, n, null, null, Auto, null, false);
        return names.Length;
    }

    // ---------------- Dokumente ----------------

    /// <summary>Sichtbar: Datei liegt in der Ablage (nicht im Papierkorb) oder ist noch in Arbeit.</summary>
    private const string Visible = "(fileid IS NULL OR fileid IN (SELECT f0.fileid FROM files f0 WHERE f0.fileid IS NOT NULL))";
    private const string VisibleD = "(d.fileid IS NULL OR d.fileid IN (SELECT f0.fileid FROM files f0 WHERE f0.fileid IS NOT NULL))";

    private static string DocColumns(DmsStore db) => $@"d.id, COALESCE(d.fileid, 0), {db.PathCol}, d.state, d.title, d.doc_date, d.imported, d.correspondent, d.doctype, d.inbox, d.by_ai,
        d.error, d.original_name, d.mime, d.size, d.original_blob, d.archive, d.pdfa, d.profile, d.source, d.sender, d.auto_correspondent, d.auto_doctype,
        d.manual, d.ai_suggestion, d.pl, d.pl_correspondent, d.pl_doctype, d.pl_tags, {PlDiffers}, d.ext_mount, d.ext_path, d.ext_original IS NOT NULL, d.role, d.amount, d.fixed";

    private static DmsDoc ReadDoc(Microsoft.Data.Sqlite.SqliteDataReader r, Dictionary<long, List<DmsDocTag>> tags)
    {
        var id = r.GetInt64(0);
        var mount = r.IsDBNull(30) ? null : r.GetString(30);
        return new DmsDoc(id, r.GetInt64(1), mount != null ? r.IsDBNull(31) ? null : r.GetString(31) : r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5),
            DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(6)).UtcDateTime, r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8),
            tags.GetValueOrDefault(id) ?? [], r.GetInt64(9) != 0, r.GetInt64(10) != 0, r.IsDBNull(11) ? null : r.GetString(11), r.GetString(12),
            r.IsDBNull(13) ? null : r.GetString(13), r.GetInt64(14), !r.IsDBNull(15) || r.GetInt64(32) != 0, r.GetInt64(16) != 0, r.GetInt64(17) != 0,
            r.IsDBNull(18) ? null : r.GetString(18), r.GetString(19), r.IsDBNull(20) ? null : r.GetString(20), r.GetInt64(21) != 0, r.GetInt64(22) != 0,
            r.GetInt64(23) != 0, r.IsDBNull(24) ? null : r.GetString(24), r.GetInt64(1) != 0 && r.IsDBNull(2),
            r.GetInt64(25) == 0 ? null : new PaperlessValues(r.IsDBNull(26) ? null : r.GetString(26), r.IsDBNull(27) ? null : r.GetString(27),
                r.IsDBNull(28) || r.GetString(28).Length == 0 ? [] : r.GetString(28).Split(',').ToList(), r.GetInt64(29) != 0), mount,
            r.IsDBNull(33) ? null : r.GetString(33), Amount: r.IsDBNull(34) ? null : r.GetInt64(34), Fixed: r.GetInt64(35) != 0);
    }

    private static Dictionary<long, List<DmsDocTag>> TagsOf(DmsStore db, IEnumerable<long>? ids = null)
    {
        var result = new Dictionary<long, List<DmsDocTag>>();
        var rows = ids == null
            ? db.Query("SELECT doc, tag, auto FROM dms_doc_tags", r => (r.GetInt64(0), r.GetString(1), r.GetInt64(2) != 0))
            : ids.Chunk(400).SelectMany(chunk =>
            {
                var names = chunk.Select((_, i) => "$d" + i).ToList();
                return db.Query($"SELECT doc, tag, auto FROM dms_doc_tags WHERE doc IN ({string.Join(",", names)})", r => (r.GetInt64(0), r.GetString(1), r.GetInt64(2) != 0),
                    chunk.Select((d, i) => (names[i], (object?)d)).ToArray());
            }).ToList();
        foreach (var (doc, tag, auto) in rows)
        {
            if (!result.TryGetValue(doc, out var l)) result[doc] = l = [];
            l.Add(new DmsDocTag(tag, auto));
        }
        return result;
    }

    public sealed record Filter(string? Query = null, string? Correspondent = null, string? Doctype = null, List<string>? Tags = null,
        string? DocFrom = null, string? DocTo = null, long? ImportedFrom = null, long? ImportedTo = null, bool? Inbox = null, string? Profile = null,
        string? Mime = null, bool? NoCorrespondent = null, bool? NoDoctype = null, bool? NoTags = null, bool? PaperlessDiffers = null,
        bool? Queued = null, string? Sort = null, bool Desc = false, string? Role = null, bool? NoRole = null, bool? Fixed = null);

    /// <summary>Sortierbare Spalten der Dokumentliste (Klick auf den Spaltenkopf).</summary>
    public static readonly string[] SortKeys = ["date", "title", "correspondent", "doctype", "tags", "role", "imported", "size", "name", "type", "source"];

    /// <summary>Mehr Tags auf einmal filtert niemand; die Abfrage bleibt so überschaubar.</summary>
    private const int MaxFilterTags = 50;

    /// <summary>Dokumente nach Filter, neueste (Dokumentdatum, sonst Import) zuerst. Ober- und Unterbegriffe: ein Oberbegriff schließt die
    /// Unterbegriffe ein. Mit Suchwörtern: Treffer in Titel und Text.</summary>
    public static (List<DmsDoc> Items, int Total) Docs(DmsStore db, Filter f, int offset = 0, int limit = 200)
    {
        Ensure(db);
        var where = new List<string> { db.Visible() };
        var args = new List<(string, object?)>();
        var all = Entries(db.User);
        void InEntries(string column, string id, string name)
        {
            var below = Below(all, id).ToList();
            var ps = below.Select((_, i) => $"${name}{i}").ToList();
            where.Add($"{column} IN ({string.Join(",", ps)})");
            args.AddRange(below.Select((b, i) => (ps[i], (object?)b)));
        }
        if (f.Correspondent != null) InEntries("d.correspondent", f.Correspondent, "c");
        if (f.Doctype != null) InEntries("d.doctype", f.Doctype, "t");
        var ti = 0;
        var filterTags = (f.Tags ?? []).Distinct().ToList();
        if (filterTags.Count > MaxFilterTags) throw new ArgumentException($"Zu viele Tags auf einmal (höchstens {MaxFilterTags}).");
        foreach (var tag in filterTags)
        {
            var below = Below(all, tag).ToList();
            var ps = below.Select((_, i) => $"$g{ti}_{i}").ToList();
            where.Add($"d.id IN (SELECT doc FROM dms_doc_tags WHERE tag IN ({string.Join(",", ps)}))");
            args.AddRange(below.Select((b, i) => (ps[i], (object?)b)));
            ti++;
        }
        if (f.NoCorrespondent == true) where.Add("d.correspondent IS NULL");
        if (f.NoDoctype == true) where.Add("d.doctype IS NULL");
        // Rolle: die Dokumente mit dieser Rolle - das geltende und die früheren Versionen
        if (f.Role != null) { where.Add("d.role = $role"); args.Add(("$role", f.Role)); }
        if (f.NoRole == true) where.Add("d.role IS NULL");
        if (f.Fixed == true) where.Add("d.fixed = 1");
        if (f.NoTags == true) where.Add("d.id NOT IN (SELECT doc FROM dms_doc_tags)");
        if (f.PaperlessDiffers == true) where.Add(PlDiffers);
        if (f.DocFrom != null) { where.Add("d.doc_date >= $df"); args.Add(("$df", f.DocFrom)); }
        if (f.DocTo != null) { where.Add("d.doc_date <= $dt"); args.Add(("$dt", f.DocTo)); }
        if (f.ImportedFrom != null) { where.Add("d.imported >= $if"); args.Add(("$if", f.ImportedFrom)); }
        if (f.ImportedTo != null) { where.Add("d.imported <= $it"); args.Add(("$it", f.ImportedTo)); }
        if (f.Inbox != null) { where.Add("d.inbox = $ib"); args.Add(("$ib", f.Inbox.Value ? 1 : 0)); }
        // Inbox: nur Verarbeitetes; Wartendes steht in der Warteschlange
        if (f.Inbox == true) where.Add("d.state <> 'queued'");
        if (f.Queued != null) where.Add(f.Queued.Value ? "d.state = 'queued'" : "d.state <> 'queued'");
        if (f.Profile != null) { where.Add("d.profile = $pr"); args.Add(("$pr", f.Profile)); }
        if (f.Mime != null) { where.Add("d.mime LIKE $mi"); args.Add(("$mi", f.Mime + "%")); }
        var join = "";
        var order = "COALESCE(d.doc_date, date(d.imported, 'unixepoch')) DESC, d.id DESC";
        if (f.Sort != null && SortKeys.Contains(f.Sort))
        {
            var dir = f.Desc ? "DESC" : "ASC";
            // Korrespondent und Dokumenttyp nach dem vollen Namen (mit übergeordneten Einträgen); die Listen liegen nicht immer in derselben Datenbank
            string ByName(string column, string kind)
            {
                var names = all.Where(e => e.Kind == kind).Select(e => (e.Id, Name: string.Join(" › ", PathOf(all, e.Id)))).OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
                if (names.Count == 0) return "0";
                var ps = names.Select((x, i) => { var p = $"$s{kind[0]}{i}"; args.Add((p, x.Id)); return $"WHEN {p} THEN {i}"; });
                return $"CASE {column} {string.Join(" ", ps)} ELSE {names.Count} END";
            }
            var key = f.Sort switch
            {
                "date" => "COALESCE(d.doc_date, date(d.imported, 'unixepoch'))",
                "title" => "d.title COLLATE NOCASE",
                "correspondent" => ByName("d.correspondent", Correspondent),
                "doctype" => ByName("d.doctype", Doctype),
                "role" => ByName("d.role", Role),
                // Tags: nach dem im Alphabet ersten; ohne Tag ans Ende der Namen
                "tags" => $"COALESCE((SELECT MIN({ByName("g.tag", Tag)}) FROM dms_doc_tags g WHERE g.doc = d.id), {all.Count(e => e.Kind == Tag)})",
                "imported" => "d.imported",
                "amount" => "COALESCE(d.amount, 0)",
                "size" => "d.size",
                "name" => "d.original_name COLLATE NOCASE",
                "type" => "d.mime",
                _ => "d.source",
            };
            order = $"{key} {dir}, d.id {dir}";
        }
        if (!string.IsNullOrWhiteSpace(f.Query) && Search.TextIndex.FtsQuery(f.Query) is { Length: > 0 } match)
        {
            join = "JOIN dms_fts x ON x.rowid = d.id";
            where.Add("dms_fts MATCH $q");
            args.Add(("$q", match));
            if (f.Sort == null) order = "bm25(dms_fts, 4.0, 1.0)";
        }
        var sqlWhere = string.Join(" AND ", where);
        var total = Convert.ToInt32(db.Scalar($"SELECT COUNT(*) FROM dms_docs d {join} WHERE {sqlWhere}", args.ToArray()) ?? 0);
        // Erst die Kennungen in der richtigen Reihenfolge, dann Dokumente samt Tags
        var ids = db.Query($"SELECT d.id FROM dms_docs d {join} WHERE {sqlWhere} ORDER BY {order} LIMIT $l OFFSET $o", r => r.GetInt64(0),
            args.Concat([("$l", (object?)limit), ("$o", (object?)offset)]).ToArray());
        return (ByIds(db, ids), total);
    }

    /// <summary>Dokumente zu Kennungen, in dieser Reihenfolge.</summary>
    public static List<DmsDoc> ByIds(DmsStore db, IReadOnlyCollection<long> ids)
    {
        Ensure(db);
        if (ids.Count == 0) return [];
        var tags = TagsOf(db, ids);
        var roles = Roles(db.User);
        // Rollen, deren geltendes Dokument im Papierkorb liegt: Es zählt nicht mehr als ersetzend; wiederhergestellt gilt es wieder
        var trashedHolders = roles.Count == 0 ? [] : db.User.Query(@"SELECT id FROM dms_docs WHERE role IS NOT NULL AND fileid IS NOT NULL
                AND fileid NOT IN (SELECT fileid FROM files WHERE fileid IS NOT NULL)", r => r.GetInt64(0)).ToHashSet();
        var byId = new Dictionary<long, DmsDoc>();
        foreach (var chunk in ids.Chunk(400))
        {
            var names = chunk.Select((_, i) => "$d" + i).ToList();
            foreach (var d in db.Query($"SELECT {DocColumns(db)} FROM dms_docs d {db.FileJoin} WHERE d.id IN ({string.Join(",", names)})",
                         r => ReadDoc(r, tags), chunk.Select((x, i) => (names[i], (object?)x)).ToArray()))
                // Im Tresor: Pfad aus dem Ordnerbaum; einen Papierkorb gibt es dort nicht
            {
                var x = db.Tresor ? d with { Path = d.FileId != 0 ? db.PathOf(d.FileId) : null, Trashed = false } : d;
                // Rolle: geltend, wenn die Zuordnung auf dieses Dokument zeigt; sonst frühere Version, ersetzt, als die geltende sie bekam
                if (x.Role != null && roles.TryGetValue(x.Role, out var holder))
                    x = holder.Tresor == db.Tresor && holder.Doc == x.Id ? x with { RoleCurrent = true }
                        : !holder.Tresor && trashedHolders.Contains(holder.Doc) ? x
                        : x with { RoleReplaced = DateTimeOffset.FromUnixTimeSeconds(holder.Since).UtcDateTime };
                byId[d.Id] = x;
            }
        }
        return ids.Where(byId.ContainsKey).Select(i => byId[i]).ToList();
    }

    /// <summary>Sichtbare Dokumente einer Herkunft (source_ref), etwa einer Mail.</summary>
    public static List<DmsDoc> ByRef(DmsStore db, string reference)
    {
        Ensure(db);
        return ByIds(db, db.Query($"SELECT id FROM dms_docs d WHERE source_ref = $r AND {db.Visible()} ORDER BY id", r => r.GetInt64(0), ("$r", reference)));
    }

    public static DmsDoc Get(DmsStore db, long id) => ByIds(db, [id]).FirstOrDefault() ?? throw new KeyNotFoundException("Dokument nicht gefunden.");

    /// <summary>Das Dokument zu einer Datei der Ablage, sonst null.</summary>
    public static DmsDoc? OfFile(UserDb db, long fileId)
    {
        Ensure(db);
        return db.Scalar("SELECT id FROM dms_docs WHERE fileid = $f", ("$f", fileId)) is long id ? Get(db, id) : null;
    }

    /// <summary>Liegt hier ein Dokument des DMS (eigene Ablage oder externer Ordner)?</summary>
    public static bool IsDocAt(UserDb db, string? mount, string path)
    {
        if (mount == null) return IsDocPath(db, path);
        Ensure(db);
        return db.Scalar("SELECT 1 FROM dms_docs WHERE ext_mount = $m AND ext_path = $p", ("$m", mount), ("$p", path)) != null;
    }

    public static bool IsDocPath(UserDb db, string path)
    {
        Ensure(db);
        return db.Scalar("SELECT 1 FROM dms_docs d JOIN files f ON f.fileid = d.fileid WHERE f.path = $p", ("$p", path)) != null;
    }

    public static int InboxCount(DmsStore db)
    {
        Ensure(db);
        return Convert.ToInt32(db.Scalar($"SELECT COUNT(*) FROM dms_docs d WHERE inbox = 1 AND state <> 'queued' AND {db.Visible()}") ?? 0);
    }

    /// <summary>In der Warteschlange, gezählt wie die Liste (<see cref="Filter.Queued"/>): nur sichtbare.</summary>
    public static int QueuedCount(DmsStore db)
    {
        Ensure(db);
        return Convert.ToInt32(db.Scalar($"SELECT COUNT(*) FROM dms_docs d WHERE state = 'queued' AND {db.Visible()}") ?? 0);
    }

    public static string? Text(DmsStore db, long id) =>
        db.Query("SELECT body FROM dms_text WHERE doc = $d", r => r.GetString(0), ("$d", id)).FirstOrDefault();

    /// <summary>Text und Titel in den Suchindex.</summary>
    internal static void WriteText(DmsStore db, long id, string title, string body, string method)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        UserDb.Exec(conn, "INSERT OR REPLACE INTO dms_text(doc, body, method) VALUES($d, $b, $m)", ("$d", id), ("$b", body), ("$m", method));
        UserDb.Exec(conn, "DELETE FROM dms_fts WHERE rowid = $d", ("$d", id));
        UserDb.Exec(conn, "INSERT INTO dms_fts(rowid, title, body) VALUES($d, $t, $b)", ("$d", id), ("$t", title), ("$b", body));
        tx.Commit();
    }

    /// <summary>Titel im Suchindex nachziehen.</summary>
    private static void WriteTitle(DmsStore db, long id, string title)
    {
        var body = Text(db, id) ?? "";
        db.Exec("DELETE FROM dms_fts WHERE rowid = $d", ("$d", id));
        db.Exec("INSERT INTO dms_fts(rowid, title, body) VALUES($d, $t, $b)", ("$d", id), ("$t", title), ("$b", body));
    }

    // ---------------- Angaben setzen ----------------

    /// <summary>Änderung von Hand. Null = unverändert; Korrespondent/Dokumenttyp "" = keiner; Datum "" = keins.</summary>
    public sealed record Change(string? Title = null, string? DocDate = null, string? Correspondent = null, string? Doctype = null,
        List<string>? Tags = null, List<string>? AddTags = null, List<string>? RemoveTags = null, bool? Confirm = null, string? Role = null, string? Amount = null);

    // ---------------- Fixiert ----------------

    /// <summary>Gesetzt von Bereichen, die Dokumente verknüpfen: Grund, warum die Fixierung bleiben muss, sonst null.</summary>
    public static Func<DmsStore, long, string?>? FixedReason { get; set; }

    /// <summary>Fixieren: Das Dokument muss in der Ablage liegen (ein noch wartendes geht nur über <see cref="Fix"/>).</summary>
    public static void SetFixed(DmsStore db, long id, bool on)
    {
        Ensure(db);
        var doc = Get(db, id);
        if (on && doc.Trashed) throw new ArgumentException("Ein Dokument im Papierkorb lässt sich nicht fixieren.");
        if (on && doc.State == "queued") throw new ArgumentException("Das Dokument wird noch verarbeitet. Bitte später fixieren.");
        if (!on && FixedReason?.Invoke(db, id) is { } reason) throw new ArgumentException(reason);
        Fix(db, id, on);
    }

    /// <summary>Merkmal setzen, ohne Prüfung (für Bereiche, die ein Dokument selbst ablegen oder verknüpfen).</summary>
    public static void Fix(DmsStore db, long id, bool on = true)
    {
        Ensure(db);
        db.Exec("UPDATE dms_docs SET fixed = $f WHERE id = $d", ("$f", on ? 1 : 0), ("$d", id));
    }

    /// <summary>Liegt an diesem Pfad (mit glob: auch darunter) ein fixiertes Dokument der Dateiablage? Für die Sperre in der Dateiablage.</summary>
    public static bool HasFixed(UserDb db, string path, string? glob)
    {
        Ensure(db);
        return db.Scalar(@"SELECT 1 FROM dms_docs d JOIN files f ON f.fileid = d.fileid
            WHERE d.fixed = 1 AND (f.path = $p OR ($g IS NOT NULL AND f.path GLOB $g)) LIMIT 1", ("$p", path), ("$g", glob)) != null;
    }

    /// <summary>Wer eine Rolle innehat (Bereich, Dokument, seit).</summary>
    public sealed record RoleHolder(bool Tresor, long Doc, long Since);

    public static Dictionary<string, RoleHolder> Roles(UserDb db)
    {
        Ensure(db);
        return db.Query("SELECT role, tresor, doc, since FROM dms_roles", r => (r.GetString(0), new RoleHolder(r.GetInt64(1) != 0, r.GetInt64(2), r.GetInt64(3))))
            .ToDictionary(x => x.Item1, x => x.Item2);
    }

    /// <summary>
    /// Angaben von Hand ändern. Gesetzt = gehört dazu, entfernt = gehört nicht dazu - beides lernt (Einträge mit Zuweisung
    /// „Automatisch“). Bestätigen lernt alle aktuellen Angaben und nimmt das Dokument aus dem Posteingang. Gibt die Kennungen der
    /// Dokumente zurück, deren Angaben sich geändert haben (für das Neu-Ablegen).
    /// </summary>
    public static List<long> Apply(DmsStore db, IEnumerable<long> ids, Change c)
    {
        Ensure(db);
        var all = Entries(db.User);
        var byId = all.ToDictionary(e => e.Id);
        void Check(string? id, string kind)
        {
            if (string.IsNullOrEmpty(id)) return;
            if (!byId.TryGetValue(id, out var e) || e.Kind != kind) throw new KeyNotFoundException("Eintrag nicht gefunden.");
        }
        Check(c.Correspondent, Correspondent);
        Check(c.Doctype, Doctype);
        Check(c.Role, Role);
        var idList = ids.Distinct().ToList();
        if (c.Role is { Length: > 0 } && idList.Count > 1) throw new ArgumentException("Eine Rolle gilt immer nur für ein Dokument.");
        foreach (var t in (c.Tags ?? []).Concat(c.AddTags ?? []).Concat(c.RemoveTags ?? [])) Check(t, Tag);
        string? date = null;
        if (c.DocDate is { Length: > 0 })
        {
            if (!DateOnly.TryParseExact(c.DocDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                throw new ArgumentException("Ungültiges Datum.");
            // Das Datum wandert in Dateinamen und Ordner; Jahre vor 1000 sind Tippfehler
            if (d.Year < 1000) throw new ArgumentException("Ungültiges Datum.");
            date = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        var title = c.Title == null ? null : CleanText(c.Title);
        if (title is { Length: > 300 }) title = title[..300];

        long? amount = null;
        if (c.Amount is { Length: > 0 })
            amount = long.TryParse(c.Amount, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var cents) && Math.Abs(cents) < 100_000_000_000 ? cents
                : throw new ArgumentException("Ungültiger Betrag.");

        var changed = new List<long>();
        foreach (var doc in ByIds(db, idList))
        {
            if (doc.State == "queued") continue;
            // Der Betrag ändert nichts an der Ablage
            if (c.Amount != null) db.Exec("UPDATE dms_docs SET amount = $a WHERE id = $d", ("$a", amount), ("$d", doc.Id));
            HashSet<string>? tokens = null;
            HashSet<string> Tok() => tokens ??= DmsAssign.Tokens(db, doc.Id);
            void Learn(string entry, bool positive)
            {
                foreach (var x in positive ? Above(all, entry).Prepend(entry) : [entry])
                    if (byId.TryGetValue(x, out var e) && e.Match == Auto) Train(db, doc.Id, Tok(), x, positive);
            }
            var any = false;
            if (title != null && title != doc.Title && title.Length > 0)
            {
                db.Exec("UPDATE dms_docs SET title = $t WHERE id = $d", ("$t", title), ("$d", doc.Id));
                WriteTitle(db, doc.Id, title);
                any = true;
            }
            if (c.DocDate != null && date != doc.DocDate)
            {
                db.Exec("UPDATE dms_docs SET doc_date = $t WHERE id = $d", ("$t", date), ("$d", doc.Id));
                any = true;
            }
            foreach (var (kind, value, before, autoBefore, column) in new[]
                     { (Correspondent, c.Correspondent, doc.Correspondent, doc.AutoCorrespondent, "correspondent"), (Doctype, c.Doctype, doc.Doctype, doc.AutoDoctype, "doctype") })
            {
                if (value == null) continue;
                var v = value.Length == 0 ? null : value;
                if (v == before && !autoBefore) continue;
                db.Exec($"UPDATE dms_docs SET {column} = $v, auto_{column} = 0 WHERE id = $d", ("$v", v), ("$d", doc.Id));
                if (before != null && before != v) Learn(before, false);
                if (v != null) Learn(v, true);
                if (v != before) any = true;
            }
            // Rolle: zu jeder Rolle im ganzen DMS ein geltendes Dokument; das bisherige behält die Angabe als frühere Version
            if (c.Role != null)
            {
                var v = c.Role.Length == 0 ? null : c.Role;
                if (v != doc.Role || (v != null && !doc.RoleCurrent))
                {
                    var now = Unix();
                    db.Exec("UPDATE dms_docs SET role = $r, role_since = $n WHERE id = $d", ("$r", v), ("$n", v == null ? null : now), ("$d", doc.Id));
                    // Hatte dieses Dokument die bisherige Rolle inne: sie hat danach keines mehr
                    if (doc.Role != null && doc.RoleCurrent)
                        db.User.Exec("DELETE FROM dms_roles WHERE role = $r AND tresor = $t AND doc = $d", ("$r", doc.Role), ("$t", db.Tresor ? 1 : 0), ("$d", doc.Id));
                    if (v != null)
                        db.User.Exec("INSERT INTO dms_roles(role, tresor, doc, since) VALUES($r, $t, $d, $n) ON CONFLICT(role) DO UPDATE SET tresor = $t, doc = $d, since = $n",
                            ("$r", v), ("$t", db.Tresor ? 1 : 0), ("$d", doc.Id), ("$n", now));
                    any = true;
                }
            }
            var had = doc.Tags.Select(t => t.Id).ToHashSet();
            var want = c.Tags != null ? c.Tags.ToHashSet() : had.Union(c.AddTags ?? []).Except(c.RemoveTags ?? []).ToHashSet();
            if (c.Tags != null || c.AddTags != null || c.RemoveTags != null)
            {
                foreach (var t in had.Except(want))
                {
                    db.Exec("DELETE FROM dms_doc_tags WHERE doc = $d AND tag = $t", ("$d", doc.Id), ("$t", t));
                    Learn(t, false);
                    any = true;
                }
                foreach (var t in want)
                {
                    var wasAuto = doc.Tags.FirstOrDefault(x => x.Id == t)?.Auto;
                    if (wasAuto == false) continue;
                    db.Exec("INSERT INTO dms_doc_tags(doc, tag, auto) VALUES($d, $t, 0) ON CONFLICT(doc, tag) DO UPDATE SET auto = 0", ("$d", doc.Id), ("$t", t));
                    Learn(t, true);
                    if (wasAuto == null) any = true;
                }
            }
            if (c.Confirm == true)
            {
                // Aus der Inbox: jetzt nach dem Ordnermuster ablegen
                if (doc.Inbox) any = true;
                var now = Get(db, doc.Id);
                foreach (var e in new[] { now.Correspondent, now.Doctype }.Concat(now.Tags.Select(t => t.Id)))
                    if (e != null) Learn(e, true);
                db.Exec("UPDATE dms_docs SET inbox = 0, auto_correspondent = 0, auto_doctype = 0, ai_suggestion = NULL WHERE id = $d", ("$d", doc.Id));
                db.Exec("UPDATE dms_doc_tags SET auto = 0 WHERE doc = $d", ("$d", doc.Id));
            }
            if (any) changed.Add(doc.Id);
        }
        return changed;
    }

    /// <summary>
    /// Offene Inbox-Dokumente neu einordnen. Jede Änderung (Bestätigen, Zuordnen, neuer, umbenannter oder verschobener Eintrag)
    /// kann Regeln und Gelerntes anders entscheiden lassen. Geändert wird nur, was noch automatisch ist (nicht von Hand gesetzt,
    /// keine Vorgabe der Quelle): ein Vorschlag der KI für einen neuen Korrespondenten wird zugeordnet, sobald es den Eintrag gibt;
    /// sonst gilt die neue Entscheidung aus Regeln und Gelerntem, wenn es eine gibt. Automatische Tags kommen hinzu, keine fällt weg.
    /// Liefert die geänderten Dokumente (zum neu Ablegen).
    /// </summary>
    public static List<long> Reassess(DmsStore db)
    {
        Ensure(db);
        var all = Entries(db.User);
        // Offene Inbox (auch mit Fehler, nur nicht wartend) und jedes Dokument mit offenem Vorschlag der KI
        var ids = db.Query($"SELECT id FROM dms_docs d WHERE ((inbox = 1 AND state <> 'queued') OR (ai_suggestion IS NOT NULL AND ai_suggestion <> '')) AND {db.Visible()}",
            r => r.GetInt64(0));
        var changed = new List<long>();
        foreach (var doc in ByIds(db, ids))
        {
            var any = false;
            // Vorschlag der KI: den Eintrag gibt es inzwischen (angelegt bei einem anderen Dokument oder in der Verwaltung)
            var suggested = doc.AiSuggestion is { Length: > 0 } s ? FindByName(all, Correspondent, s)?.Id : null;
            if (suggested != null)
            {
                db.Exec("UPDATE dms_docs SET ai_suggestion = NULL WHERE id = $d", ("$d", doc.Id));
                any = true;
            }
            if (!doc.Inbox)
            {
                // Außerhalb der Inbox nur den Vorschlag einlösen, wenn noch kein Korrespondent da ist
                if (suggested != null && doc.Correspondent == null)
                    db.Exec("UPDATE dms_docs SET correspondent = $v, auto_correspondent = 1 WHERE id = $d", ("$v", suggested), ("$d", doc.Id));
                if (any) changed.Add(doc.Id);
                continue;
            }
            var text = Text(db, doc.Id) ?? "";
            var r = DmsAssign.Decide(db, text + "\n" + Path.GetFileNameWithoutExtension(doc.OriginalName), doc.OriginalName, doc.Sender, null);
            foreach (var (value, before, auto, column) in new[]
                     { (suggested ?? r.Correspondent, doc.Correspondent, doc.AutoCorrespondent, "correspondent"), (r.Doctype, doc.Doctype, doc.AutoDoctype, "doctype") })
            {
                if (value == null || value == before || (before != null && !auto)) continue;
                db.Exec($"UPDATE dms_docs SET {column} = $v, auto_{column} = 1 WHERE id = $d", ("$v", value), ("$d", doc.Id));
                any = true;
            }
            foreach (var t in r.Tags.Where(t => doc.Tags.All(x => x.Id != t)))
            {
                db.Exec("INSERT OR IGNORE INTO dms_doc_tags(doc, tag, auto) VALUES($d, $t, 1)", ("$d", doc.Id), ("$t", t));
                any = true;
            }
            if (any) changed.Add(doc.Id);
        }
        return changed;
    }

    /// <summary>Eintrag einer Liste nach Namen, ohne Rücksicht auf Groß- und Kleinschreibung und Leerzeichen; oberste Ebene zuerst.</summary>
    public static DmsEntry? FindByName(IEnumerable<DmsEntry> all, string kind, string name)
    {
        static string Key(string s) => string.Concat(s.Where(c => !char.IsWhiteSpace(c))).ToLowerInvariant();
        var key = Key(name);
        if (key.Length == 0) return null;
        return all.Where(e => e.Kind == kind && Key(e.Name) == key).OrderBy(e => e.Parent == null ? 0 : 1).FirstOrDefault();
    }

    /// <summary>Schon so gelernt: nichts; anders gelernt: umlernen.</summary>
    internal static void Train(DmsStore db, long doc, HashSet<string> tokens, string entry, bool positive)
    {
        var label = positive ? 1 : -1;
        var before = db.Scalar("SELECT label FROM dms_trained WHERE doc=$d AND entry=$e", ("$d", doc), ("$e", entry)) is long b ? (int)b : 0;
        if (before == label) return;
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        foreach (var t in tokens)
        {
            if (before == 1) UserDb.Exec(conn, "UPDATE dms_tokens SET pos = MAX(0, pos - 1) WHERE token=$t AND entry=$e", ("$t", t), ("$e", entry));
            if (before == -1) UserDb.Exec(conn, "UPDATE dms_tokens SET neg = MAX(0, neg - 1) WHERE token=$t AND entry=$e", ("$t", t), ("$e", entry));
            UserDb.Exec(conn, positive
                ? "INSERT INTO dms_tokens(token, entry, pos) VALUES($t,$e,1) ON CONFLICT(token, entry) DO UPDATE SET pos = pos + 1"
                : "INSERT INTO dms_tokens(token, entry, neg) VALUES($t,$e,1) ON CONFLICT(token, entry) DO UPDATE SET neg = neg + 1", ("$t", t), ("$e", entry));
        }
        UserDb.Exec(conn, "INSERT INTO dms_trained(doc, entry, label) VALUES($d,$e,$l) ON CONFLICT(doc, entry) DO UPDATE SET label=$l", ("$d", doc), ("$e", entry), ("$l", label));
        tx.Commit();
    }

    // ---------------- Virtuelle Bäume ----------------

    /// <summary>Ebenen eines Baums: correspondent, doctype, tag, doc_year, doc_month, import_year, import_month, profile.</summary>
    public static readonly string[] Levels = ["correspondent", "doctype", "tag", "role", "doc_year", "doc_month", "import_year", "import_month", "profile"];

    /// <summary>Ein Ordner im virtuellen Baum: Schlüssel (für den Filter), Name, Anzahl, Unterordner.</summary>
    public sealed record Node(string Level, string? Key, string Name, int Count, List<Node> Children);

    /// <summary>
    /// Virtueller Baum aus den Angaben. Verschachtelte Einträge (bei Korrespondent, Dokumenttyp, Tag) werden zu Unterordnern innerhalb
    /// ihrer Ebene; ein Dokument mit mehreren Tags steht unter jedem; ohne Angabe: Schlüssel null („Unbekannt“).
    /// </summary>
    public static List<Node> Tree(UserSpace space, IReadOnlyList<string> levels, Func<string, string> profileName, DmsStore? store = null)
    {
        var db = store ?? space.Db;
        foreach (var l in levels) if (!Levels.Contains(l)) throw new ArgumentException("Unbekannte Ebene: " + l);
        // Jede Ebene höchstens einmal
        levels = levels.Distinct().ToList();
        Ensure(db);
        var docs = ByIds(db, db.Query($"SELECT id FROM dms_docs d WHERE state <> 'queued' AND {db.Visible()}", r => r.GetInt64(0)));
        var all = Entries(db.User);
        var byId = all.ToDictionary(e => e.Id);
        var zone = space.Zone;
        return Build(docs, levels, 0);

        List<Node> Build(List<DmsDoc> set, IReadOnlyList<string> lv, int depth)
        {
            if (depth >= lv.Count) return [];
            var level = lv[depth];
            if (level is "correspondent" or "doctype" or "tag" or "role")
            {
                // Je Dokument die Einträge dieser Ebene (mit Oberbegriffen), dann als verschachtelter Baum
                List<string> Keys(DmsDoc d) => level switch
                {
                    "correspondent" => d.Correspondent is { } c ? [c] : [],
                    "doctype" => d.Doctype is { } t ? [t] : [],
                    "role" => d.Role is { } r ? [r] : [],
                    _ => d.Tags.Select(t => t.Id).ToList(),
                };
                var nodes = new List<Node>();
                List<Node> Entries(string? parent)
                {
                    var result = new List<Node>();
                    foreach (var e in all.Where(e => e.Kind == (level == "tag" ? Tag : level) && e.Parent == parent).OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase))
                    {
                        var below = Below(all, e.Id);
                        var mine = set.Where(d => Keys(d).Any(below.Contains)).ToList();
                        // Auf der ersten Ebene auch Einträge ohne Dokument: Nur so lässt sich das erste Dokument auf sie ziehen
                        if (mine.Count == 0 && depth > 0) continue;
                        var direct = set.Where(d => Keys(d).Contains(e.Id)).ToList();
                        var children = Entries(e.Id);
                        children.AddRange(Build(direct, lv, depth + 1));
                        result.Add(new Node(level, e.Id, e.Name, mine.Count, children));
                    }
                    return result;
                }
                nodes.AddRange(Entries(null));
                var none = set.Where(d => Keys(d).Count == 0).ToList();
                if (none.Count > 0) nodes.Add(new Node(level, null, "", none.Count, Build(none, lv, depth + 1)));
                return nodes;
            }
            string? KeyOf(DmsDoc d)
            {
                var imported = TimeZoneInfo.ConvertTimeFromUtc(d.Imported, zone);
                return level switch
                {
                    "doc_year" => d.DocDate?[..4],
                    "doc_month" => d.DocDate?[..7],
                    "import_year" => imported.ToString("yyyy", CultureInfo.InvariantCulture),
                    "import_month" => imported.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                    _ => d.Profile,
                };
            }
            return set.GroupBy(KeyOf)
                .OrderBy(g => g.Key == null).ThenByDescending(g => level == "profile" ? "" : g.Key, StringComparer.Ordinal)
                .ThenBy(g => level == "profile" ? profileName(g.Key ?? "") : "", StringComparer.CurrentCultureIgnoreCase)
                .Select(g => new Node(level, g.Key, level == "profile" ? profileName(g.Key ?? "") : g.Key ?? "", g.Count(), Build(g.ToList(), lv, depth + 1)))
                .ToList();
        }
    }
}
