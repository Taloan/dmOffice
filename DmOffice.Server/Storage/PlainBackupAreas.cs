using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Pim;
using Microsoft.Data.Sqlite;

namespace DmOffice.Storage;

/// <summary>
/// Was die Datensicherung je Bereich ins Ziel legt (docs/datensicherung-konzept.md, „Bereiche und Formate“): Dateien als
/// Originale, Kalender und Aufgaben als .ics je Sammlung, Kontakte als .vcf je Adressbuch, Notizen als .md mit Anhängen, DMS
/// als Liste (CSV, JSON) plus getrennt gehaltene Originale, Tagebuch als .md je Eintrag, Coach als CSV, Alben als JSON.
/// Nur Pfade, Stempel und wie der Inhalt entsteht - gelesen wird erst beim Übertragen, eins nach dem anderen.
/// </summary>
public static class PlainBackupAreas
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Welche Bereiche es für diese Person gibt (Module der Verwaltung, Dateiablage nicht abgeschaltet).</summary>
    public static List<string> Available(UserSpace space)
    {
        var filesOn = !FileStore.FilesOff(space.Db);
        var list = new List<string>();
        foreach (var a in PlainBackup.AllAreas)
        {
            var on = a switch
            {
                PlainBackup.Files => filesOn,
                PlainBackup.Gallery => filesOn && Features.On(Features.Gallery),
                PlainBackup.Tasks => Features.On(Features.Tasks),
                PlainBackup.Notes => Features.On(Features.Notes),
                PlainBackup.Dms => filesOn && Features.On(Features.Dms),
                PlainBackup.Journal => Features.On(Features.Journal),
                PlainBackup.Coach => Features.On(Features.Coach),
                PlainBackup.Finance => Features.On(Features.Finance),
                _ => true,
            };
            if (on) list.Add(a);
        }
        return list;
    }

    /// <summary>Alle Einträge für das Ziel, in fester Reihenfolge (damit gleiche Namen bei jedem Lauf gleich aufgelöst werden).</summary>
    public static List<PlainItem> Items(UserSpace space, PlainBackupSettings s, IServiceProvider? services)
    {
        var db = space.Db;
        var on = Available(space).Where(s.Areas.Contains).ToHashSet();
        var skip = s.Skip.ToHashSet(StringComparer.Ordinal);
        var names = new PlainBackup.Names(PlainLang.For(s.Lang));
        var items = new List<PlainItem>();
        // Pfade der Dateien im Ziel - auch für Verweise aus Tagebuch, DMS und Alben, wenn „Dateien“ selbst abgewählt ist
        var fileMap = new Dictionary<string, string>(StringComparer.Ordinal);
        // Nicht unter „Dateien“: der Abgleichordner von notizi (dieselben Notizen stehen als .md unter „Notizen“) und die Kopien
        // von Mail-Anhängen zum Ansehen im Office-Editor (nach drei Tagen wieder weg). Der Tresor (dmVAULT) geht unverändert mit.
        var notiziRoot = space.Settings.Notizi.Root ?? "";
        if (!FileStore.FilesOff(db))
            foreach (var f in FileStore.Descendants(db, ""))
            {
                // Medien des Tagebuchs kommen mit dem Tagebuch in dessen Ordner, nicht unter „Dateien“
                if (f.IsDir || FileStore.InRoot(f.Path, DmOffice.Journal.JournalMediaStore.Root)) continue;
                if (FileStore.InRoot(f.Path, DmOffice.Mail.MailAttachmentCopy.Root) || FileStore.InRoot(f.Path, notiziRoot)) continue;
                var target = names.Take(PlainBackup.Join(names.L.Files, f.Path));
                fileMap[f.Path] = target;
                if (!on.Contains(PlainBackup.Files) || Skipped(skip, f.Path)) continue;
                var path = f.Path;
                items.Add(new PlainItem(target, "f:" + f.Etag.Trim('"'), f.Size, () => FileStore.OpenRead(db, path), null, Modified: f.Modified));
            }
        if (on.Contains(PlainBackup.Gallery) && services?.GetService(typeof(DmOffice.Gallery.AlbumStore)) is DmOffice.Gallery.AlbumStore albums
            && albums.ForUser(space.User.Id).Count > 0)
            items.Add(new PlainItem(PlainBackup.Join(names.L.Gallery, names.L.Albums), null, 0, null, o => Albums(o, space, albums, fileMap), Collection: true));
        if (on.Contains(PlainBackup.Calendar) || on.Contains(PlainBackup.Tasks))
            foreach (var c in PimStore.Collections(db, PimStore.Calendar).Where(c => !DmOffice.Journal.JournalStore.IsJournal(c)))
            {
                foreach (var (area, dir, component) in new[] { (PlainBackup.Calendar, names.L.Calendar, "VEVENT"), (PlainBackup.Tasks, names.L.Tasks, "VTODO") })
                {
                    if (!on.Contains(area) || skip.Contains(area + ":" + c.Uri)) continue;
                    if (db.Scalar("SELECT 1 FROM objects WHERE collection=$c AND component=$k LIMIT 1", ("$c", c.Id), ("$k", component)) == null) continue;
                    var coll = c;
                    items.Add(new PlainItem(names.Take(PlainBackup.Join(dir, Name(c.Name, c.Uri) + ".ics")), "c:" + c.Id + ":" + Synctoken(db, c.Id) + ":" + c.Name,
                        0, null, o => { WriteIcs(db, coll, component, o); return Task.CompletedTask; }, Collection: true));
                }
            }
        if (on.Contains(PlainBackup.Contacts))
            foreach (var c in PimStore.Collections(db, PimStore.AddressBook))
            {
                if (skip.Contains(PlainBackup.Contacts + ":" + c.Uri)) continue;
                if (db.Scalar("SELECT 1 FROM objects WHERE collection=$c AND component='VCARD' LIMIT 1", ("$c", c.Id)) == null) continue;
                var coll = c;
                items.Add(new PlainItem(names.Take(PlainBackup.Join(names.L.Contacts, Name(c.Name, c.Uri) + ".vcf")), "c:" + c.Id + ":" + Synctoken(db, c.Id) + ":" + c.Name,
                    0, null, o => { WriteVcf(db, coll, o); return Task.CompletedTask; }, Collection: true));
            }
        if (on.Contains(PlainBackup.Notes)) items.AddRange(NoteItems(space, skip, names));
        if (on.Contains(PlainBackup.Dms)) items.AddRange(DmsItems(space, names, fileMap));
        if (on.Contains(PlainBackup.Journal)) items.AddRange(JournalItems(space, skip, names));
        if (on.Contains(PlainBackup.Coach)) items.AddRange(CoachItems(space, names));
        if (on.Contains(PlainBackup.Finance)) items.AddRange(FinanceItems(space, names));
        return items;
    }

    /// <summary>Liegt der Pfad in einem abgewählten Ordner der Dateiablage?</summary>
    private static bool Skipped(HashSet<string> skip, string path)
    {
        for (var p = path; ; p = p[..p.LastIndexOf('/')])
        {
            if (skip.Contains(PlainBackup.Files + ":" + p)) return true;
            if (!p.Contains('/')) return false;
        }
    }

    private static string Name(string name, string fallback) => string.IsNullOrWhiteSpace(name) ? fallback : name.Trim();

    private static long Synctoken(UserDb db, long id) => Convert.ToInt64(db.Scalar("SELECT synctoken FROM collections WHERE id=$i", ("$i", id)) ?? 0L);

    // ---------------- Kalender, Aufgaben, Kontakte ----------------

    /// <summary>Alle Objekte einer Sammlung mit dieser Komponente als ein VCALENDAR: Zeitzonen je TZID einmal, Kopfzeilen der
    /// einzelnen Objekte weg. Zeile für Zeile, ohne das Ganze im Speicher zu halten.</summary>
    public static void WriteIcs(UserDb db, Collection c, string component, Stream output)
    {
        using var w = new StreamWriter(output, Utf8, 65536, leaveOpen: true) { NewLine = "\r\n" };
        w.WriteLine("BEGIN:VCALENDAR");
        w.WriteLine("VERSION:2.0");
        w.WriteLine("PRODID:-//dmOffice//Datensicherung//DE");
        w.WriteLine("X-WR-CALNAME:" + Escape(c.Name));
        var zones = new HashSet<string>(StringComparer.Ordinal);
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT data FROM objects WHERE collection=$c AND component=$k ORDER BY id";
        cmd.Parameters.AddWithValue("$c", c.Id);
        cmd.Parameters.AddWithValue("$k", component);
        using var r = cmd.ExecuteReader();
        while (r.Read()) CopyComponents(r.GetString(0), w, zones);
        w.WriteLine("END:VCALENDAR");
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\n", "\\n").Replace("\r", "");

    /// <summary>Die Komponenten unter VCALENDAR übernehmen; VTIMEZONE nur, wenn ihre TZID noch nicht da war.</summary>
    private static void CopyComponents(string data, StreamWriter w, HashSet<string> zones)
    {
        var depth = 0;
        List<string>? block = null;
        string? tzid = null;
        foreach (var raw in data.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            var continued = line[0] is ' ' or '\t';
            if (!continued && line.StartsWith("BEGIN:", StringComparison.OrdinalIgnoreCase))
            {
                depth++;
                if (depth == 1) continue;   // VCALENDAR selbst
                if (depth == 2) { block = []; tzid = null; }
            }
            if (depth >= 2)
            {
                block!.Add(line);
                if (depth == 2 && !continued && line.StartsWith("TZID:", StringComparison.OrdinalIgnoreCase)) tzid = line[5..];
            }
            if (!continued && line.StartsWith("END:", StringComparison.OrdinalIgnoreCase))
            {
                if (depth == 2 && block != null)
                {
                    var isZone = block[0].Equals("BEGIN:VTIMEZONE", StringComparison.OrdinalIgnoreCase);
                    if (!isZone || tzid == null || zones.Add(tzid))
                        foreach (var l in block) w.WriteLine(l);
                    block = null;
                }
                depth = Math.Max(0, depth - 1);
            }
        }
    }

    /// <summary>Alle Visitenkarten eines Adressbuchs hintereinander, wie gespeichert.</summary>
    public static void WriteVcf(UserDb db, Collection c, Stream output)
    {
        using var w = new StreamWriter(output, Utf8, 65536, leaveOpen: true) { NewLine = "\r\n" };
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT data FROM objects WHERE collection=$c AND component='VCARD' ORDER BY id";
        cmd.Parameters.AddWithValue("$c", c.Id);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            foreach (var l in r.GetString(0).Split('\n'))
                if (l.TrimEnd('\r') is { Length: > 0 } line) w.WriteLine(line);
    }

    // ---------------- Notizen ----------------

    private static readonly Regex InlineImage = new("""<img\b[^>]*?\ssrc\s*=\s*["']blob:([^"']+)["'][^>]*>""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static IEnumerable<PlainItem> NoteItems(UserSpace space, HashSet<string> skip, PlainBackup.Names names)
    {
        var db = space.Db;
        if (db.Scalar("SELECT 1 FROM sqlite_master WHERE name='notes'") == null) yield break;
        var notes = DmOffice.Notes.NotesDb.Of(space);
        var folders = notes.AllFolders().ToDictionary(f => f.SyncId);
        // Ordnerpfad aus der Kette der übergeordneten Ordner (gegen Kreise begrenzt)
        string? FolderPath(string? id)
        {
            var parts = new List<string>();
            for (var i = 0; id != null && i < 32; i++)
            {
                if (skip.Contains(PlainBackup.Notes + ":" + id)) return null;
                if (!folders.TryGetValue(id, out var f)) break;
                parts.Insert(0, f.Name);
                id = f.Parent;
            }
            return string.Join('/', parts);
        }
        foreach (var (list, archived) in new[] { (notes.GetAllActive(), false), (notes.GetArchived(), true) })
            foreach (var n in list.OrderBy(n => n.Created).ThenBy(n => n.Id))
            {
                if (skip.Contains(PlainBackup.Notes + ":") && n.Folder == null) continue;
                if (FolderPath(n.Folder) is not { } folder) continue;
                var dir = archived ? PlainBackup.Join(names.L.Notes, names.L.Archive, folder) : PlainBackup.Join(names.L.Notes, folder);
                var title = string.IsNullOrWhiteSpace(n.DisplayTitle) ? names.L.Note : n.DisplayTitle.Trim();
                if (title.Length > 80) title = title[..80];
                var md = names.Take(dir + "/" + PlainBackup.Segment(title) + ".md");
                var attachDir = md[..^3] + names.L.Attachments;
                // Bilder: Name mit passender Endung (aus den ersten Bytes), Anhänge mit ihrem Namen
                var files = new List<(string Blob, string Path)>();
                var images = new Dictionary<string, string>(StringComparer.Ordinal);
                var i = 0;
                foreach (var blob in n.Images.Distinct())
                {
                    var head = db.Scalar("SELECT substr(data, 1, 12) FROM note_blobs WHERE name=$n", ("$n", blob)) as byte[];
                    if (head == null) continue;
                    var p = names.Take(attachDir + "/" + PlainBackup.Segment(names.L.Image + " " + ++i + ImageExt(head)));
                    images[blob] = p;
                    files.Add((blob, p));
                }
                foreach (var a in n.Attachments)
                {
                    if (db.Scalar("SELECT 1 FROM note_blobs WHERE name=$n", ("$n", a.Blob)) == null) continue;
                    files.Add((a.Blob, names.Take(attachDir + "/" + PlainBackup.Segment(string.IsNullOrWhiteSpace(a.Name) ? a.Blob : a.Name))));
                }
                var note = n;
                var arch = archived;
                yield return new PlainItem(md, "n:" + n.Modified.ToString("o") + ":" + n.Folder + ":" + arch + ":" + string.Join(',', n.Tags ?? []), 0, null,
                    o => { NoteMarkdown(o, note, arch, images, files.Where(f => !images.ContainsValue(f.Path)).Select(f => f.Path), md); return Task.CompletedTask; });
                foreach (var (blob, path) in files)
                {
                    var b = blob;
                    var size = Convert.ToInt64(db.Scalar("SELECT length(data) FROM note_blobs WHERE name=$n", ("$n", b)) ?? 0L);
                    yield return new PlainItem(path, "b:" + b, size, () => BlobColumn(db, "note_blobs", b), null);
                }
            }
    }

    private static string ImageExt(byte[] h) =>
        h.Length >= 3 && h[0] == 0xFF && h[1] == 0xD8 ? ".jpg"
        : h.Length >= 4 && h[0] == 0x89 && h[1] == 'P' && h[2] == 'N' && h[3] == 'G' ? ".png"
        : h.Length >= 4 && h[0] == 'G' && h[1] == 'I' && h[2] == 'F' ? ".gif"
        : h.Length >= 12 && h[0] == 'R' && h[1] == 'I' && h[2] == 'F' && h[3] == 'F' && h[8] == 'W' && h[9] == 'E' && h[10] == 'B' && h[11] == 'P' ? ".webp"
        : h.Length >= 12 && h[4] == 'f' && h[5] == 't' && h[6] == 'y' && h[7] == 'p' ? ".heic"
        : ".bin";

    /// <summary>Eine Notiz als Markdown mit Kopf; Bilder an ihrer Stelle, Anhänge als Liste am Ende (Verweise relativ zur .md).</summary>
    private static void NoteMarkdown(Stream o, DmOffice.Notes.NoteRow n, bool archived, Dictionary<string, string> images, IEnumerable<string> attachments, string md)
    {
        static string Q(string s) => JsonSerializer.Serialize(s, Json);
        var baseDir = md[..(md.LastIndexOf('/') + 1)];
        string Rel(string p) => p.StartsWith(baseDir, StringComparison.Ordinal) ? p[baseDir.Length..] : p;
        var marks = new List<string>();
        var html = InlineImage.Replace(n.Html, m =>
        {
            if (!images.TryGetValue(m.Groups[1].Value, out var p)) return "";
            marks.Add(p);
            return " zzdmimg" + (marks.Count - 1) + "zz ";
        });
        var text = DmOffice.Journal.JournalExport.HtmlToMarkdown(html);
        for (var i = 0; i < marks.Count; i++) text = text.Replace("zzdmimg" + i + "zz", "![](<" + Rel(marks[i]) + ">)");
        var sb = new StringBuilder("---\n");
        sb.Append("title: ").Append(Q(n.DisplayTitle)).Append('\n');
        sb.Append("created: ").Append(n.Created.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("modified: ").Append(n.Modified.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append('\n');
        if (n.Tags is { Count: > 0 } tags) sb.Append("tags: [").Append(string.Join(", ", tags.Select(Q))).Append("]\n");
        if (n.Important) sb.Append("important: true\n");
        if (archived) sb.Append("archived: true\n");
        sb.Append("---\n\n").Append(text.Trim()).Append('\n');
        var rest = attachments.ToList();
        if (rest.Count > 0)
        {
            sb.Append('\n');
            foreach (var a in rest) sb.Append("- [").Append(a[(a.LastIndexOf('/') + 1)..]).Append("](<").Append(Rel(a)).Append(">)\n");
        }
        o.Write(Utf8.GetBytes(sb.ToString()));
    }

    /// <summary>Inhalt einer BLOB-Spalte als Stream (eigene Verbindung bis zum Schließen).</summary>
    private static Stream BlobColumn(UserDb db, string table, string name)
    {
        var conn = db.Open();
        try
        {
            var rowid = UserDb.Query(conn, $"SELECT rowid FROM {table} WHERE name=$n", r => r.GetInt64(0), ("$n", name)).FirstOrDefault();
            if (rowid == 0) throw new KeyNotFoundException("Inhalt nicht gefunden.");
            return new Owned(new SqliteBlob(conn, table, "data", rowid, readOnly: true), conn);
        }
        catch { conn.Dispose(); throw; }
    }

    private sealed class Owned(Stream inner, IDisposable owner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { inner.Dispose(); owner.Dispose(); }
            base.Dispose(disposing);
        }
    }

    // ---------------- Finanzen ----------------

    /// <summary>Ausgestellte Belege als PDF, dazu Einträge je Jahr, Belegliste, Kunden und Protokoll als Tabellen.</summary>
    private static IEnumerable<PlainItem> FinanceItems(UserSpace space, PlainBackup.Names names)
    {
        var db = space.Db;
        if (db.Scalar("SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'fin_docs'") == null) yield break;
        var dir = names.L.Finance;
        static Func<Stream, Task> Bytes(Func<byte[]> make) => async o => await o.WriteAsync(make());
        foreach (var d in DmOffice.Finance.FinanceStore.Docs(db, null).Where(d => d.State == DmOffice.Finance.FinanceStore.IssuedState).AsEnumerable().Reverse())
        {
            var pdf = DmOffice.Finance.FinanceStore.Pdf(db, d.Id);
            yield return new PlainItem(names.Take(PlainBackup.Join(dir, names.L.Receipts, DmOffice.Dms.DmsImport.SafeName(d.Number ?? d.Id.ToString()) + ".pdf")), "fin:" + d.PdfSha, pdf.Length, () => new MemoryStream(pdf), null);
        }
        foreach (var y in DmOffice.Finance.FinanceStore.Years(db).Select(y => y.Year).Where(y => DmOffice.Finance.FinanceStore.Entries(db, y).Count > 0))
            yield return new PlainItem(names.Take(PlainBackup.Join(dir, string.Format(CultureInfo.InvariantCulture, names.L.EntriesYear, y))), null, 0, null, Bytes(() => DmOffice.Finance.FinanceApi.EntriesCsv(db, y)));
        yield return new PlainItem(names.Take(PlainBackup.Join(dir, names.L.ReceiptList)), null, 0, null, Bytes(() => DmOffice.Finance.FinanceApi.DocsCsv(db)));
        yield return new PlainItem(names.Take(PlainBackup.Join(dir, names.L.Customers)), null, 0, null, Bytes(() => DmOffice.Finance.FinanceApi.CustomersCsv(db)));
        yield return new PlainItem(names.Take(PlainBackup.Join(dir, names.L.Log)), null, 0, null, Bytes(() => DmOffice.Finance.FinanceApi.LogCsv(db)));
    }

    // ---------------- DMS ----------------

    private static IEnumerable<PlainItem> DmsItems(UserSpace space, PlainBackup.Names names, Dictionary<string, string> fileMap)
    {
        var db = space.Db;
        if (db.Scalar("SELECT 1 FROM sqlite_master WHERE name='dms_docs'") == null) yield break;
        var docs = new List<DmOffice.Dms.DmsDoc>();
        for (var offset = 0; ; offset += 500)
        {
            var (page, total) = DmOffice.Dms.Dms.Docs(db, new DmOffice.Dms.Dms.Filter(Sort: "imported"), offset, 500);
            docs.AddRange(page);
            if (page.Count == 0 || docs.Count >= total) break;
        }
        // Getrennt gehaltene Originale (es gibt eine Archivfassung): eigener Ordner
        var visible = docs.Select(d => d.Id).ToHashSet();
        var originals = new Dictionary<long, string>();
        foreach (var (id, blob, name, size) in db.Query("SELECT id, original_blob, original_name, size FROM dms_docs WHERE original_blob IS NOT NULL ORDER BY id",
                     r => (r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3))))
        {
            if (!visible.Contains(id)) continue;
            var path = names.Take(PlainBackup.Join(names.L.Dms, names.L.Originals, id + " " + name));
            originals[id] = path;
            var b = blob;
            yield return new PlainItem(path, "o:" + b, size, () => db.Blobs.OpenRead(b), null);
        }
        var all = DmOffice.Dms.Dms.Entries(db);
        string? Full(string? id) => id == null ? null : string.Join(" › ", DmOffice.Dms.Dms.PathOf(all, id));
        var rows = docs.Select(d => new DmsRow(
            d.Mount == null && d.Path != null ? fileMap.GetValueOrDefault(d.Path) : null,
            d.Mount != null ? d.Path : null,
            originals.GetValueOrDefault(d.Id), d.Title, d.DocDate, Full(d.Correspondent), Full(d.Doctype),
            d.Tags.Select(t => Full(t.Id)!).ToList(), d.OriginalName, d.Imported, d.Inbox, d.Mime)).ToList();
        yield return new PlainItem(names.Take(PlainBackup.Join(names.L.Dms, names.L.DmsList + ".csv")), null, 0, null, o =>
        {
            using var w = new StreamWriter(o, new UTF8Encoding(true), 65536, leaveOpen: true) { NewLine = "\r\n" };
            w.WriteLine("Pfad;Externer Ordner;Original;Titel;Dokumentdatum;Korrespondent;Dokumenttyp;Tags;Originalname;Importiert;Inbox;Typ");
            foreach (var r in rows)
                w.WriteLine(string.Join(';', new[] { r.Path, r.External, r.Original, r.Title, r.DocDate, r.Correspondent, r.Doctype, string.Join(", ", r.Tags),
                    r.OriginalName, r.Imported.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), r.Inbox ? "ja" : "", r.Mime }.Select(Csv)));
            return Task.CompletedTask;
        }, Collection: true);
        yield return new PlainItem(names.Take(PlainBackup.Join(names.L.Dms, names.L.DmsList + ".json")), null, 0, null,
            o => JsonSerializer.SerializeAsync(o, rows, Json), Collection: true);
    }

    private sealed record DmsRow(string? Path, string? External, string? Original, string Title, string? DocDate, string? Correspondent, string? Doctype,
        List<string> Tags, string OriginalName, DateTime Imported, bool Inbox, string? Mime);

    private static string Csv(string? v)
    {
        if (string.IsNullOrEmpty(v)) return "";
        // Formeln in Tabellenprogrammen nicht ausführen lassen
        if (v[0] is '=' or '+' or '-' or '@') v = "'" + v;
        return v.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }

    // ---------------- Tagebuch ----------------

    /// <summary>
    /// Je Tagebuch ein Ordner, darin je Jahr die Einträge als .md und die Fotos, Videos und Sprachaufnahmen unter „Media“.
    /// Die Einträge verweisen mit relativen Pfaden darauf („Media/Name.jpg“).
    /// </summary>
    private static IEnumerable<PlainItem> JournalItems(UserSpace space, HashSet<string> skip, PlainBackup.Names names)
    {
        var db = space.Db;
        foreach (var j in DmOffice.Journal.JournalStore.Journals(db))
        {
            // Je Jahresordner: Ablagepfad des Mediums → Pfad im Ziel (dasselbe Bild an zwei Einträgen nur einmal)
            var placed = new Dictionary<(string Dir, string Path), string?>();
            if (skip.Contains(PlainBackup.Journal + ":" + j.Uri)) continue;
            var entries = DmOffice.Journal.JournalStore.Entries(db, j).ToList();
            var local = entries.ToDictionary(e => e.Id, e => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(e.Date, DateTimeKind.Utc), space.Zone));
            var perDay = entries.GroupBy(e => local[e.Id].Date).ToDictionary(g => g.Key, g => g.Count());
            foreach (var e in entries.OrderBy(e => e.Date).ThenBy(e => e.Uri, StringComparer.Ordinal))
            {
                var at = local[e.Id];
                var stem = at.ToString(perDay[at.Date] > 1 ? "yyyy-MM-dd HHmm" : "yyyy-MM-dd", CultureInfo.InvariantCulture);
                var dir = PlainBackup.Join(names.L.Journal, Name(j.Name, j.Uri), at.ToString("yyyy", CultureInfo.InvariantCulture));
                var path = names.Take(PlainBackup.Join(dir, stem + ".md"));
                var media = new List<DmOffice.Journal.JournalExport.MediaOut>();
                foreach (var m in e.Media)
                {
                    if (!placed.TryGetValue((dir, m.Path), out var target))
                    {
                        target = null;
                        if (FileStore.Get(db, m.Path) is { IsDir: false } f)
                        {
                            target = names.Take(PlainBackup.Join(dir, "Media", Path.GetFileName(m.Path)));
                            var from = f.Path;
                            yield return new PlainItem(target, "f:" + f.Etag.Trim('"'), f.Size, () => FileStore.OpenRead(db, from), null, Modified: f.Modified);
                        }
                        placed[(dir, m.Path)] = target;
                    }
                    // Relativ zum Jahresordner, in dem auch die .md liegt
                    media.Add(new DmOffice.Journal.JournalExport.MediaOut(target == null ? null : "x/" + target[(dir.Length + 1)..],
                        Path.GetFileName(m.Path), m.Mime, m.Size, m.Width, m.Height, m.Taken));
                }
                var entry = e;
                yield return new PlainItem(path, "j:" + e.Etag + ":" + stem, 0, null,
                    o => o.WriteAsync(Utf8.GetBytes(DmOffice.Journal.JournalExport.Markdown(entry, at, media, "x"))).AsTask());
            }
        }
    }

    // ---------------- Coach ----------------

    private static IEnumerable<PlainItem> CoachItems(UserSpace space, PlainBackup.Names names)
    {
        var db = space.Db;
        var tables = db.Query("SELECT name FROM sqlite_master WHERE type='table' AND name LIKE 'coach\\_%' ESCAPE '\\' ORDER BY name", r => r.GetString(0));
        if (tables.Count == 0) yield break;
        yield return new PlainItem(names.Take(PlainBackup.Join(names.L.Coach, names.L.Profile)), null, 0, null,
            o => JsonSerializer.SerializeAsync(o, DmOffice.Coach.CoachStore.Profile(db), Json), Collection: true);
        foreach (var table in tables)
        {
            var t = table;
            yield return new PlainItem(names.Take(PlainBackup.Join(names.L.Coach, t["coach_".Length..] + ".csv")), null, 0, null, o =>
            {
                using var w = new StreamWriter(o, new UTF8Encoding(true), 65536, leaveOpen: true) { NewLine = "\r\n" };
                using var conn = db.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT * FROM \"{t.Replace("\"", "\"\"")}\" ORDER BY rowid";
                using var r = cmd.ExecuteReader();
                w.WriteLine(string.Join(';', Enumerable.Range(0, r.FieldCount).Select(i => Csv(r.GetName(i)))));
                while (r.Read())
                    w.WriteLine(string.Join(';', Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? ""
                        : Csv(Convert.ToString(r.GetValue(i) is byte[] ? "" : r.GetValue(i), CultureInfo.InvariantCulture)))));
                return Task.CompletedTask;
            }, Collection: true);
        }
    }

    // ---------------- Galerie ----------------

    private static Task Albums(Stream o, UserSpace space, DmOffice.Gallery.AlbumStore store, Dictionary<string, string> fileMap)
    {
        var me = space.User.Id;
        var list = store.ForUser(me).OrderBy(a => a.Id).Select(a => new
        {
            a.Name,
            Kind = a.Kind == DmOffice.Gallery.Album.Smart ? "smart" : "fest",
            Rule = a.Kind == DmOffice.Gallery.Album.Smart ? a.Rule : null,
            Own = a.Owner == me,
            Photos = a.Kind == DmOffice.Gallery.Album.Smart ? null : store.Items(a.Id).Where(i => i.Owner == me).Select(i =>
                i.Ref.StartsWith("f:", StringComparison.Ordinal) && long.TryParse(i.Ref[2..], out var id) && FileStore.ById(space.Db, id) is { } f
                    ? fileMap.GetValueOrDefault(f.Path) ?? f.Path : i.Ref).ToList(),
        }).ToList();
        return JsonSerializer.SerializeAsync(o, list, Json);
    }
}
