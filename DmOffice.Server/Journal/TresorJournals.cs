using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Pim;

namespace DmOffice.Journal;

/// <summary>
/// Tagebücher im Tresor (docs/tresor-konzept.md, „Tagebücher im Tresor“): in der Tresor-Datenbank, Einträge als dieselben
/// iCalendar-Objekte wie in der Benutzerdatenbank, Medien im BlobStore des Tresors unter denselben Pfaden wie im Systemordner
/// („sys-dmoffice-journal/Jahr/Monat/Name“). Suche, Pukki, Übersicht, CalDAV und Sicherung kennen nur die Sammlungen der
/// Benutzerdatenbank und sehen diese Tagebücher nie. Nach außen tragen sie eine Kennung ab <see cref="Base"/>.
/// </summary>
public static class TresorJournals
{
    /// <summary>Kennung nach außen = Base + Zeile; darunter liegen eigene (positiv) und geteilte (negativ) Tagebücher.</summary>
    public const long Base = 1L << 40;
    public static bool Is(long publicId) => publicId >= Base;

    public const string SharedHint = "Tagebücher im Tresor können nicht geteilt werden. Alle Freigaben dieses Tagebuchs werden entfernt.";

    public const string Schema = """
        CREATE TABLE journals (id INTEGER PRIMARY KEY, name TEXT NOT NULL, color TEXT, created TEXT NOT NULL);
        CREATE TABLE journal_entries (journal INTEGER NOT NULL REFERENCES journals(id) ON DELETE CASCADE, uri TEXT NOT NULL,
            etag TEXT NOT NULL, data TEXT NOT NULL, modified TEXT NOT NULL, PRIMARY KEY (journal, uri));
        CREATE TABLE journal_media (path TEXT PRIMARY KEY, blob TEXT NOT NULL, mime TEXT NOT NULL, size INTEGER NOT NULL,
            modified TEXT NOT NULL, thumb_small BLOB, thumb_large BLOB);
        """;

    private static Collection Coll(long id, string name, string? color) =>
        new(id, PimStore.Calendar, "tresor-" + id, name, color, null, JournalStore.Component, 0, 0);

    public static List<Collection> List(TresorDb db) =>
        db.Query("SELECT id, name, color FROM journals ORDER BY name COLLATE NOCASE", r => Coll(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)));

    public static Collection? Find(TresorDb db, long id) =>
        db.Query("SELECT id, name, color FROM journals WHERE id = $i", r => Coll(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)), ("$i", id)).FirstOrDefault();

    public static Collection Require(TresorDb db, long id) => Find(db, id) ?? throw new KeyNotFoundException("Dieses Tagebuch gibt es nicht.");

    public static int Count(TresorDb db, long id) => Convert.ToInt32(db.Scalar("SELECT COUNT(*) FROM journal_entries WHERE journal = $i", ("$i", id)));

    public static Collection Create(TresorDb db, string name, string? color)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("Bitte einen Namen eingeben.");
        db.Exec("INSERT INTO journals(name, color, created) VALUES($n, $c, $t)", ("$n", name), ("$c", color), ("$t", Ics.Stamp()));
        return Find(db, Convert.ToInt64(db.Scalar("SELECT MAX(id) FROM journals")))!;
    }

    public static void Rename(TresorDb db, long id, string name, string? color)
    {
        Require(db, id);
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Bitte einen Namen eingeben.");
        db.Exec("UPDATE journals SET name = $n, color = $c WHERE id = $i", ("$n", name.Trim()), ("$c", color), ("$i", id));
    }

    /// <summary>Tagebuch mit allen Einträgen löschen, dazu seine Medien, die kein anderes Tagebuch im Tresor nutzt.</summary>
    public static void Delete(TresorDb db, long id)
    {
        var paths = JournalStore.Entries(new Box(db, id)).SelectMany(e => e.Media).Select(m => m.Path).ToHashSet();
        using (var conn = db.Open())
        {
            UserDb.Exec(conn, "PRAGMA secure_delete = ON");
            UserDb.Exec(conn, "DELETE FROM journal_entries WHERE journal = $i", ("$i", id));
            UserDb.Exec(conn, "DELETE FROM journals WHERE id = $i", ("$i", id));
        }
        Prune(db, paths);
    }

    /// <summary>Die Einträge eines Tagebuchs im Tresor.</summary>
    public sealed class Box(TresorDb db, long journal) : IJournalBox
    {
        private DavObject Obj(Microsoft.Data.Sqlite.SqliteDataReader r) =>
            new(0, journal, r.GetString(0), null, JournalStore.Component, r.GetString(1), r.GetString(2), r.GetString(3));

        public List<DavObject> Objects() =>
            db.Query("SELECT uri, etag, data, modified FROM journal_entries WHERE journal = $j", Obj, ("$j", journal));

        public DavObject? Object(string uri) =>
            db.Query("SELECT uri, etag, data, modified FROM journal_entries WHERE journal = $j AND uri = $u", Obj, ("$j", journal), ("$u", uri)).FirstOrDefault();

        public void Put(string uri, string data, string? ifMatch)
        {
            if (!PimStore.ValidSegment(uri)) throw new ArgumentException("Ungültiger Name.");
            using var conn = db.Open();
            using var tx = conn.BeginTransaction();
            var now = UserDb.Query(conn, "SELECT etag FROM journal_entries WHERE journal = $j AND uri = $u", r => r.GetString(0), ("$j", journal), ("$u", uri)).FirstOrDefault();
            if (ifMatch != null && now != ifMatch) throw new PreconditionFailedException();
            UserDb.Exec(conn, "INSERT OR REPLACE INTO journal_entries(journal, uri, etag, data, modified) VALUES($j, $u, $e, $d, $m)",
                ("$j", journal), ("$u", uri), ("$e", "\"" + Guid.NewGuid().ToString("N")[..16] + "\""), ("$d", data), ("$m", Ics.Stamp()));
            tx.Commit();
        }

        public bool Delete(string uri)
        {
            var media = Object(uri) is { } o && JournalStore.Read(o) is { } e ? e.Media.Select(m => m.Path).ToList() : [];
            using (var conn = db.Open())
            {
                UserDb.Exec(conn, "PRAGMA secure_delete = ON");
                if (UserDb.Exec(conn, "DELETE FROM journal_entries WHERE journal = $j AND uri = $u", ("$j", journal), ("$u", uri)) == 0) return false;
            }
            Prune(db, media);
            return true;
        }
    }

    // ---------------- Medien ----------------

    public sealed record MediaFile(string Path, string Blob, string Mime, long Size, DateTime Modified);

    public static MediaFile? Media(TresorDb db, string path) =>
        db.Query("SELECT path, blob, mime, size, modified FROM journal_media WHERE path = $p",
            r => new MediaFile(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3), DateTime.Parse(r.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind)),
            ("$p", path)).FirstOrDefault();

    /// <summary>Inhalt lesen (leere Dateien haben keinen Blob).</summary>
    public static Stream Open(TresorDb db, MediaFile m) => m.Blob.Length == 0 ? new MemoryStream() : db.Blobs.OpenRead(m.Blob);

    private static void Forget(TresorDb db, string blob) { if (blob.Length > 0) db.Blobs.Delete(blob); }

    /// <summary>Freier Pfad („Name (2).jpg“), wenn es den schon gibt.</summary>
    public static string FreeName(TresorDb db, string path)
    {
        if (Media(db, path) == null) return path;
        var dir = path.Contains('/') ? path[..path.LastIndexOf('/')] + "/" : "";
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var n = 2; ; n++)
            if (Media(db, $"{dir}{stem} ({n}){ext}") == null) return $"{dir}{stem} ({n}){ext}";
    }

    /// <summary>Datei ablegen (ein vorhandener Pfad wird ersetzt).</summary>
    public static async Task<long> PutAsync(TresorDb db, string path, Stream data, string mime, DateTime? modified, CancellationToken ct)
    {
        var info = await db.Blobs.WriteAsync(data, ct: ct);
        var old = Media(db, path);
        db.Exec("INSERT OR REPLACE INTO journal_media(path, blob, mime, size, modified) VALUES($p, $b, $m, $s, $t)",
            ("$p", path), ("$b", info.Id ?? ""), ("$m", mime), ("$s", info.Size), ("$t", (modified ?? DateTime.UtcNow).ToUniversalTime().ToString("O")));
        if (old != null) Forget(db, old.Blob);
        return info.Size;
    }

    /// <summary>Vorschaubild (WebP) - beim ersten Mal erzeugt und in der Tresor-Datenbank gemerkt; null: kein Bild.</summary>
    public static byte[]? Thumb(TresorDb db, MediaFile m, int size)
    {
        var px = Thumbs.Snap(size);
        var col = px == Thumbs.Small ? "thumb_small" : "thumb_large";
        if (db.Scalar($"SELECT {col} FROM journal_media WHERE path = $p", ("$p", m.Path)) is byte[] stored) return stored;
        if (Thumbs.KindOf(m.Path, m.Mime) == Thumbs.Kind.None || m.Blob.Length == 0) return null;
        var data = db.Blobs.ReadAll(m.Blob);
        var webp = Thumbs.IsRaw(m.Path) ? Thumbs.FromRaw(data, px) : Thumbs.FromBytes(data, px);
        if (webp != null) db.Exec($"UPDATE journal_media SET {col} = $t WHERE path = $p", ("$t", webp), ("$p", m.Path));
        return webp;
    }

    /// <summary>Medien löschen, auf die kein Eintrag im Tresor mehr verweist.</summary>
    public static void Prune(TresorDb db, IEnumerable<string> paths)
    {
        var candidates = paths.ToHashSet();
        if (candidates.Count == 0) return;
        foreach (var j in List(db))
            foreach (var e in JournalStore.Entries(new Box(db, j.Id)))
                foreach (var m in e.Media) candidates.Remove(m.Path);
        foreach (var p in candidates)
        {
            if (Media(db, p) is not { } m) continue;
            using (var conn = db.Open())
            {
                UserDb.Exec(conn, "PRAGMA secure_delete = ON");
                UserDb.Exec(conn, "DELETE FROM journal_media WHERE path = $p", ("$p", p));
            }
            Forget(db, m.Blob);
        }
    }

    // ---------------- Neues bei geschlossenem Tresor ----------------

    public const string DropMedia = "journal-media", DropEntry = "journal-entry";
    /// <summary>Vorläufiger Pfad abgelegter Medien („drop:Kennung/sys-dmoffice-journal/2026/11/Name.jpg“) - beim Übernehmen durch den echten ersetzt.</summary>
    public const string DropPrefix = "drop:";
    private const string LastKey = "journal.last", RemapKey = "journal.remap:";
    private sealed record MediaHeader(string Path, string Mime, DateTime? Modified);

    /// <summary>Vorläufiger Pfad für ein Medium, das bei geschlossenem Tresor abgelegt wird.</summary>
    public static string DropPath(string path) => DropPrefix + Guid.NewGuid().ToString("N") + "/" + path;

    /// <summary>
    /// Tagebuch im Tresor für Neues ohne bestimmtes Tagebuch (Kennung <see cref="Base"/> selbst, oder das gewählte gibt es nicht
    /// mehr): das zuletzt beschriebene, sonst das erste, sonst ein neues „Mein Tagebuch“.
    /// </summary>
    public static Collection Default(TresorDb db) =>
        (long.TryParse(db.Scalar("SELECT value FROM kv WHERE key = $k", ("$k", LastKey)) as string, out var last) ? Find(db, last) : null)
        ?? List(db).FirstOrDefault() ?? Create(db, "Mein Tagebuch", "#7B1DD3");

    /// <summary>Zuletzt beschrieben - Ziel für Neues ohne bestimmtes Tagebuch.</summary>
    public static void Used(TresorDb db, long id) =>
        db.Exec("INSERT OR REPLACE INTO kv(key, value) VALUES($k, $v)", ("$k", LastKey), ("$v", id.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>Höchstens so groß wird ein Foto, Video oder eine Datei bei geschlossenem Tresor (liegt beim Ablegen im Speicher).</summary>
    public const long MaxDropBytes = 200L * 1024 * 1024;

    /// <summary>Medium bei geschlossenem Tresor ablegen (Pfad aus <see cref="DropPath"/>).</summary>
    public static void Drop(UserSpace space, string path, string mime, byte[] data, DateTime? modified)
    {
        if (data.LongLength > MaxDropBytes) throw new ArgumentException($"Bei geschlossenem Tresor höchstens {MaxDropBytes / 1024 / 1024} MB je Datei.");
        var head = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new MediaHeader(path, mime, modified));
        Tresor.Drop(space.User.Id, DropMedia, [.. head, 0, .. data]);
    }

    /// <summary>Neuen Eintrag bei geschlossenem Tresor ablegen.</summary>
    public static void Drop(UserSpace space, JournalInput input)
    {
        input.Uri = null; input.Etag = null; input.Import = null;
        Tresor.Drop(space.User.Id, DropEntry, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(input));
    }

    /// <summary>Übernehmer beim Öffnen des Tresors.</summary>
    public static void Register()
    {
        Tresor.Absorbers[DropMedia] = (db, payload) =>
        {
            var zero = Array.IndexOf(payload, (byte)0);
            var head = System.Text.Json.JsonSerializer.Deserialize<MediaHeader>(payload.AsSpan(0, zero)) ?? throw new InvalidDataException("Kopf fehlt.");
            var rest = head.Path[DropPrefix.Length..];
            var path = FreeName(db, rest[(rest.IndexOf('/') + 1)..]);
            PutAsync(db, path, new MemoryStream(payload, zero + 1, payload.Length - zero - 1), head.Mime, head.Modified, CancellationToken.None).GetAwaiter().GetResult();
            db.Exec("INSERT OR REPLACE INTO kv(key, value) VALUES($k, $v)", ("$k", RemapKey + head.Path), ("$v", path));
        };
        Tresor.Absorbers[DropEntry] = (db, payload) =>
        {
            var input = System.Text.Json.JsonSerializer.Deserialize<JournalInput>(payload, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))
                ?? throw new InvalidDataException("Eintrag fehlt.");
            var coll = (Is(input.Journal) ? Find(db, input.Journal - Base) : null) ?? Default(db);
            // Vorläufige Pfade durch die echten ersetzen; was nicht ankam, fällt weg
            var media = new List<JournalMedia>();
            foreach (var m in input.Media ?? [])
            {
                if (!m.Path.StartsWith(DropPrefix, StringComparison.Ordinal)) { if (Media(db, m.Path) != null) media.Add(m); continue; }
                if (db.Scalar("SELECT value FROM kv WHERE key = $k", ("$k", RemapKey + m.Path)) is string real) media.Add(m with { Path = real });
                db.Exec("DELETE FROM kv WHERE key = $k", ("$k", RemapKey + m.Path));
            }
            input.Media = media;
            input.Journal = Base + coll.Id;
            JournalStore.Save(new Box(db, coll.Id), input);
            Used(db, coll.Id);
        };
    }

    // ---------------- Verschieben ----------------

    /// <summary>
    /// Ein eigenes Tagebuch in den Tresor: Einträge und Medien kommen mit, danach ist die Sammlung weg. Medien aus dem Systemordner
    /// werden endgültig gelöscht (nicht in den Papierkorb), wenn kein anderes Tagebuch sie nutzt; aus der Dateiablage angehängte
    /// Dateien kommen als Kopie in den Systemordner des Tresors, das Original bleibt. Freigaben entfernt der Aufrufer.
    /// </summary>
    public static async Task<Collection> MoveInAsync(UserSpace space, TresorDb db, Collection coll, CancellationToken ct)
    {
        var user = space.Db;
        var to = Create(db, coll.Name, coll.Color);
        var box = new Box(db, to.Id);
        var map = new Dictionary<string, string>();
        foreach (var o in PimStore.Objects(user, coll.Id))
            box.Put(o.Uri, await CopyInAsync(space, db, o.Data, map, ct), null);
        PimStore.DeleteCollection(user, coll.Id);
        PurgeOpen(space, map.Keys);
        // Ohne Tagebuch im offenen Bereich: ein neues leeres - Pukki, Widgets und App schreiben dorthin
        if (JournalStore.Journals(user).Count == 0) JournalStore.Create(user, "Mein Tagebuch", "#7B1DD3");
        return to;
    }

    /// <summary>Aus dem Tresor in den offenen Bereich: neue Sammlung, Medien in den Systemordner, im Tresor gelöscht.</summary>
    public static async Task<Collection> MoveOutAsync(UserSpace space, TresorDb db, long id, CancellationToken ct)
    {
        var user = space.Db;
        var from = Require(db, id);
        var coll = JournalStore.Create(user, from.Name, from.Color);
        var box = new PimJournal(user, coll);
        var map = new Dictionary<string, string>();
        foreach (var o in new Box(db, id).Objects())
            box.Put(o.Uri, await CopyOutAsync(space, db, o.Data, map, ct), null);
        Delete(db, id);
        return coll;
    }

    /// <summary>
    /// Einen Eintrag in ein anderes eigenes Tagebuch verschieben - auch zwischen offenem Bereich und Tresor, Medien wie beim
    /// Verschieben ganzer Tagebücher. Zurück: der Name des Eintrags im Ziel (neu, wenn es ihn dort schon gibt).
    /// </summary>
    public static async Task<string> MoveEntryAsync(UserSpace space, TresorDb? db, IJournalBox from, bool fromTresor, IJournalBox to, bool toTresor,
        string uri, CancellationToken ct)
    {
        var o = from.Object(uri) ?? throw new KeyNotFoundException("Diesen Eintrag gibt es nicht.");
        var map = new Dictionary<string, string>();
        var data = fromTresor == toTresor ? o.Data
            : toTresor ? await CopyInAsync(space, db!, o.Data, map, ct) : await CopyOutAsync(space, db!, o.Data, map, ct);
        var target = to.Object(uri) == null ? uri : PimStore.NewUri(".ics");
        to.Put(target, data, null);
        // Im Tresor räumt das Löschen Medien ab, die kein Eintrag mehr nutzt; im offenen Bereich nur die mitgenommenen
        from.Delete(uri);
        if (!fromTresor && toTresor) PurgeOpen(space, map.Keys);
        return target;
    }

    /// <summary>Medien eines Eintrags in den Tresor kopieren (map: schon kopierte); zurück: der Eintrag mit den Pfaden im Tresor.</summary>
    private static async Task<string> CopyInAsync(UserSpace space, TresorDb db, string data, Dictionary<string, string> map, CancellationToken ct)
    {
        var user = space.Db;
        foreach (var m in JournalStore.Read(Obj(data))?.Media ?? [])
        {
            if (map.ContainsKey(m.Path) || FileStore.Get(user, m.Path) is not { IsDir: false } f) continue;
            var target = FileStore.InRoot(f.Path, JournalMediaStore.Root) ? f.Path
                : $"{JournalMediaStore.Root}/{TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, space.Zone):yyyy/MM}/{f.Name}";
            target = FreeName(db, target);
            await using (var s = FileStore.OpenRead(user, f.Path))
                await PutAsync(db, target, s, string.IsNullOrEmpty(f.Mime) ? JournalMediaStore.MimeOf(f.Name) : f.Mime, f.Modified, ct);
            map[m.Path] = target;
        }
        return JournalStore.MapMedia(data, p => map.GetValueOrDefault(p)) ?? data;
    }

    /// <summary>Medien eines Eintrags aus dem Tresor in den Systemordner kopieren; zurück: der Eintrag mit den neuen Pfaden.</summary>
    private static async Task<string> CopyOutAsync(UserSpace space, TresorDb db, string data, Dictionary<string, string> map, CancellationToken ct)
    {
        var user = space.Db;
        foreach (var m in JournalStore.Read(Obj(data))?.Media ?? [])
        {
            if (map.ContainsKey(m.Path) || Media(db, m.Path) is not { } f) continue;
            var target = FileStore.InRoot(f.Path, JournalMediaStore.Root) ? f.Path : $"{JournalMediaStore.Root}/{Path.GetFileName(f.Path)}";
            if (FileStore.Parent(target) is { Length: > 0 } dir) FileStore.EnsureDir(user, dir);
            target = FileStore.FreeName(user, target);
            await using (var s = Open(db, f))
                await FileStore.PutAsync(user, target, s, f.Mime, ct: ct);
            map[m.Path] = target;
        }
        return JournalStore.MapMedia(data, p => map.GetValueOrDefault(p)) ?? data;
    }

    private static DavObject Obj(string data) => new(0, 0, "", null, JournalStore.Component, "", data, "");

    /// <summary>Mitgenommene Medien aus dem Systemordner endgültig löschen (nicht in den Papierkorb), wenn kein Tagebuch im offenen Bereich sie mehr nutzt.</summary>
    private static void PurgeOpen(UserSpace space, IEnumerable<string> paths)
    {
        var user = space.Db;
        var used = JournalStore.Journals(user).SelectMany(c => JournalStore.Entries(user, c)).SelectMany(e => e.Media).Select(m => m.Path).ToHashSet();
        foreach (var p in paths.Where(p => FileStore.InRoot(p, JournalMediaStore.Root) && !used.Contains(p)).ToList())
        {
            FileStore.Delete(user, p, out var trash);
            if (trash != 0) FileStore.PurgeTrash(user, [trash]);
        }
    }
}
