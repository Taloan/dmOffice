using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DmOffice.Core;
using DmOffice.Dav;

namespace DmOffice.Gallery;

/// <summary>
/// Zeitleiste der Galerie in Monatsabschnitten wie bei Immich (docs/galerie-zeitleiste-konzept.md): statt aller Fotos auf
/// einmal erst nur die Monate mit Anzahl, dann die Fotos der Monate, die gerade im Bild sind. Quellen wie bisher: eigene
/// Ablage samt der mit mir geteilten Ordner (ohne ausgeschlossene Ordner) und die Galerie-Ordner. Je Benutzer liegt ein
/// Index im Speicher, neu gebaut, wenn sich etwas ändert (Änderungszähler per Trigger, nicht öfter als alle
/// <see cref="Grace"/>); Filter, Sortierung und Stapel rechnet der Server darauf in Millisekunden.
/// </summary>
public static class GalleryTimeline
{
    /// <summary>Ein Foto oder Video, wie die Galerie es zeigt. Id ist der Pfad in der Galerie (eigene Ablage oder „ext:…“).</summary>
    public sealed record Entry(string Id, string Name, long Size, string? Mime, long Modified, string Etag, long FileId, FileMeta.Row? Meta, string? Description,
        bool Favorite = false, bool Link = false, bool People = false, string? Owner = null, bool? CanWrite = null, bool ShowOnly = false, List<Entry>? Stack = null)
    {
        /// <summary>Aufnahmezeit, sonst Änderungszeit (ms, UTC) - danach sortiert und gruppiert die Galerie.</summary>
        public long Taken => Meta?.Taken ?? Modified;
        public string Ext => Name.LastIndexOf('.') is > 0 and var d ? Name[d..].ToLowerInvariant() : "";
    }

    /// <summary>
    /// Abfrage einer Ansicht: Sortierung, Richtung, JPG+RAW stapeln, nur eine Dateiendung, Filterleiste (wie Smart-Alben),
    /// Gruppe: „source“ = erst nach Quelle (eigene Ablage, je Galerie-Ordner, je Person mit geteilten Ordnern), darin nach Datum.
    /// </summary>
    public sealed record Query(string Sort, bool Desc, bool Stack, string Ext, string? Filter, string Group = "");

    public sealed class Bucket(string key, List<Entry> items, string hash, string? label = null)
    {
        public string Key { get; } = key;
        /// <summary>Überschrift bei Gruppierung nach Quelle (Name der Quelle).</summary>
        public string? Label { get; } = label;
        public List<Entry> Items { get; } = items;
        public string Hash { get; } = hash;
        public byte[]? Json;
    }

    public sealed class View
    {
        public required List<Bucket> Buckets { get; init; }
        public required Dictionary<string, Bucket> ByKey { get; init; }
        /// <summary>Alle (nur nach Dateiendung, ohne Filterleiste und Stapel) - für „n von total“.</summary>
        public int Total { get; init; }
        /// <summary>Dateiendungen mit Anzahl über alle Fotos (für die Auswahl „Alle Formate“).</summary>
        public required Dictionary<string, int> Types { get; init; }
        public required List<Entry> Shown { get; init; }
        public byte[]? BucketsJson;
        public byte[]? AllJson;
    }

    private sealed class Index(string tag, DateTime at, List<Entry> items, Dictionary<string, string>? sources = null)
    {
        public string Tag { get; } = tag;
        /// <summary>Galerie-Ordner: Kennung → Name für die Gruppierung nach Quelle.</summary>
        public Dictionary<string, string> Sources { get; } = sources ?? [];
        public DateTime At { get; } = at;
        public List<Entry> Items { get; } = items;
        public ConcurrentDictionary<Query, View> Views { get; } = new();
        public ConcurrentDictionary<string, object> Facets { get; } = new();
    }

    private static readonly ConcurrentDictionary<string, Index> Cache = new();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new();
    /// <summary>So lange gilt ein älterer Stand weiter, während eingelesen wird (sonst baute jeder Abruf neu). Tests: 0.</summary>
    public static TimeSpan Grace { get; set; } = TimeSpan.FromSeconds(15);
    /// <summary>Seiten bei Sortierung nach Name oder Größe (dann gibt es keine Monate).</summary>
    public const int PageSize = 500;

    private static readonly HashSet<string> RawExt = new(StringComparer.OrdinalIgnoreCase) { ".arw", ".cr2", ".cr3", ".nef", ".nrw", ".orf", ".rw2", ".raf", ".pef", ".dng", ".srw", ".3fr", ".erf" };
    private static readonly string[] LeadExt = [".jpg", ".jpeg", ".heic", ".heif"];

    // ---------------- Änderungszähler der eigenen Ablage ----------------

    private static readonly ConcurrentDictionary<UserDb, bool> Ready = new();

    /// <summary>Zähler für den Stand der eigenen Fotos: jede Änderung an Dateien und Bildangaben zählt hoch (Trigger).</summary>
    public static void Ensure(UserDb db)
    {
        if (Ready.ContainsKey(db)) return;
        FileMeta.Ensure(db);
        db.Exec("CREATE TABLE IF NOT EXISTS gallery_own_ver(id INTEGER PRIMARY KEY CHECK(id = 1), v INTEGER NOT NULL)");
        db.Exec("INSERT OR IGNORE INTO gallery_own_ver(id, v) VALUES(1, 0)");
        const string bump = "BEGIN UPDATE gallery_own_ver SET v = v + 1 WHERE id = 1; END";
        db.Exec($"CREATE TRIGGER IF NOT EXISTS gallery_own_ver_fi AFTER INSERT ON files {bump}");
        db.Exec($"CREATE TRIGGER IF NOT EXISTS gallery_own_ver_fd AFTER DELETE ON files {bump}");
        db.Exec($"CREATE TRIGGER IF NOT EXISTS gallery_own_ver_fu AFTER UPDATE OF path, size, etag, mime, modified ON files {bump}");
        db.Exec($"CREATE TRIGGER IF NOT EXISTS gallery_own_ver_mi AFTER INSERT ON file_meta {bump}");
        db.Exec($"CREATE TRIGGER IF NOT EXISTS gallery_own_ver_md AFTER DELETE ON file_meta {bump}");
        db.Exec($"CREATE TRIGGER IF NOT EXISTS gallery_own_ver_mu AFTER UPDATE ON file_meta {bump}");
        Ready[db] = true;
    }

    private static long OwnVersion(UserDb db) { Ensure(db); return Convert.ToInt64(db.Scalar("SELECT v FROM gallery_own_ver WHERE id = 1") ?? 0L); }

    /// <summary>
    /// Stand aller Quellen als ETag, ohne die Fotos zu lesen: Zähler der eigenen Ablage und der Galerie-Ordner, sichtbare
    /// Galerie-Ordner, ausgeschlossene Ordner, geteilte Ordner (mit dem Zähler ihres Besitzers) und die Kennzeichen der Kacheln.
    /// </summary>
    private static string TagOf(HttpContext http, UserSpace space, ServerConfig config, List<FileMount> mounts)
    {
        var sb = new StringBuilder("t1|").Append(OwnVersion(space.Db)).Append('|');
        var (extTag, sources) = GalleryFolders.VersionOf(space, config);
        sb.Append(extTag).Append('|').Append(sources).Append('|').AppendJoin('\n', space.Settings.PhotoExcludes).Append('|');
        foreach (var m in mounts) sb.Append(m.Share.Id).Append(':').Append(OwnVersion(m.Owner.Db)).Append(m.CanWrite ? 'w' : 'r').Append(m.ShowOnly ? 's' : '-').Append(',');
        sb.Append('|').AppendJoin(',', FileStore.FavoriteIds(space.Db).Order()).Append('|').AppendJoin(',', FileStore.SharedIds(space.Db).Order()).Append('|')
            .AppendJoin(',', People(http, space).Order());
        return "\"" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..20].ToLowerInvariant() + "\"";
    }

    private static HashSet<long> People(HttpContext http, UserSpace space) =>
        http.RequestServices.GetRequiredService<FolderShareStore>().All().Where(s => s.Owner == space.User.Id).Select(s => s.Folder).ToHashSet();

    // ---------------- Index ----------------

    private static long Ms(DateTime d) => new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    /// <summary>Alle Fotos und Videos des Benutzers, wie die Galerie sie zeigt (unsortiert).</summary>
    private static List<Entry> Build(HttpContext http, UserSpace space, ServerConfig config, List<FileMount> mounts)
    {
        var list = new List<Entry>();
        var excluded = space.Settings.PhotoExcludes;
        var favorites = FileStore.FavoriteIds(space.Db);
        var links = FileStore.SharedIds(space.Db);
        var people = People(http, space);
        // Bereiche wie bei der Suche in der Ablage: die eigene Wurzel und die mit mir geteilten Ordner
        var root = SharedFiles.Resolve(http, "");
        var areas = mounts.Select(m => new FileLoc(m.Owner, m.Dir.Path, m)).ToList();
        if (!root.Virtual) areas.Insert(0, root);
        bool InWithMe(string p) => mounts.Count > 0 && (p == SharedFiles.Folder || p.StartsWith(SharedFiles.Folder + "/", StringComparison.Ordinal));
        foreach (var a in areas)
        {
            var meta = FileMeta.List(a.Db, null).ToDictionary(m => m.Path, StringComparer.Ordinal);
            // Bearbeitete Fotos: Fingerabdruck am Etag (neue Vorschaubilder)
            var edits = DmOffice.Dav.PhotoEdits.Fingerprints(a.Db);
            foreach (var f in FileStore.Search(a.Db, a.Path, "", int.MaxValue, f => !f.IsDir && !a.Protected(f.Path) && !(!a.Shared && InWithMe(f.Path)) && FileKinds.Of(f) is "image" or "video"))
            {
                var shown = a.Show(f);
                if (excluded.Count > 0 && FileKinds.UnderAny(excluded, shown.Path)) continue;
                list.Add(new Entry(shown.Path, f.Name, f.Size, f.Mime, Ms(f.Modified), DmOffice.Dav.PhotoEdits.Tag(f.Etag, edits.GetValueOrDefault(DmOffice.Dav.PhotoEdits.KeyOf(f))), shown.FileId, meta.GetValueOrDefault(f.Path), meta.GetValueOrDefault(f.Path)?.Description,
                    !a.Shared && favorites.Contains(shown.FileId), !a.Shared && links.Contains(shown.FileId), !a.Shared && people.Contains(shown.FileId),
                    a.Mount?.Owner.User.DisplayName, a.Mount?.CanWrite, a.Mount?.ShowOnly == true));
            }
        }
        var extEdits = DmOffice.Dav.PhotoEdits.Fingerprints(space.Db);
        foreach (var e in GalleryFolders.List(space, config))
            list.Add(new Entry("ext:" + e.Source + "/" + e.Path, e.Path[(e.Path.LastIndexOf('/') + 1)..], e.Size, e.Mime, e.Modified,
                GalleryFolders.TileTag(e, extEdits), 0, e.Meta, e.Description));
        return list;
    }

    private static async Task<Index> IndexAsync(HttpContext http, ServerConfig config)
    {
        var space = http.Space();
        var mounts = SharedFiles.Mounts(http);
        var tag = TagOf(http, space, config, mounts);
        var key = space.User.Id;
        if (Cache.TryGetValue(key, out var ix) && (ix.Tag == tag || DateTime.UtcNow - ix.At < Grace)) return ix;
        var gate = Locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(http.RequestAborted);
        try
        {
            // Wer gewartet hat, nimmt, was der andere eben gebaut hat
            if (Cache.TryGetValue(key, out ix) && (ix.Tag == tag || DateTime.UtcNow - ix.At < Grace)) return ix;
            ix = new Index(tag, DateTime.UtcNow, Build(http, space, config, mounts), SourceNames(space, config));
            Cache[key] = ix;
            return ix;
        }
        finally { gate.Release(); }
    }

    /// <summary>Namen der Galerie-Ordner: „Externer Ordner › Pfad“.</summary>
    private static Dictionary<string, string> SourceNames(UserSpace space, ServerConfig config)
    {
        var mounts = DmOffice.Storage.ExternalFolders.MountsOf(space, config).ToDictionary(m => m.Id, m => m.Name);
        return GalleryFolders.Of(space.Db).ToDictionary(s => s.Id,
            s => string.Join(" › ", new[] { mounts.GetValueOrDefault(s.Mount, s.Mount) }.Concat(s.Path.Split('/', StringSplitOptions.RemoveEmptyEntries))));
    }

    /// <summary>Quelle eines Fotos: Sortierschlüssel (eigene zuerst, dann Galerie-Ordner, dann geteilte) und Name.</summary>
    private static (string Key, string Label) SourceOf(Index ix, Entry e)
    {
        if (e.Id.StartsWith("ext:", StringComparison.Ordinal))
        {
            var slash = e.Id.IndexOf('/', 4);
            var id = e.Id[4..(slash > 4 ? slash : e.Id.Length)];
            var name = ix.Sources.GetValueOrDefault(id, id);
            return ("1" + name.ToLowerInvariant() + "\u0001" + id, name);
        }
        if (e.Owner != null) return ("2" + e.Owner.ToLowerInvariant(), "Geteilt von " + e.Owner);
        return ("0", "Eigene Dateien");
    }

    /// <summary>Alle Fotos und Videos des Benutzers aus dem Index (unsortiert) - für andere Bereiche, etwa die Übersicht.</summary>
    public static async Task<IReadOnlyList<Entry>> AllAsync(HttpContext http, ServerConfig config) => (await IndexAsync(http, config)).Items;

    /// <summary>Den Index eines Benutzers verwerfen (Tests, oder wenn sich etwas ohne Zähler geändert hat).</summary>
    public static void Forget(string userId) => Cache.TryRemove(userId, out _);

    // ---------------- Ansicht: filtern, stapeln, sortieren, in Abschnitte teilen ----------------

    private static bool Passes(Entry e, AlbumApi.Rule? rule, TimeZoneInfo zone, int upTo)
    {
        if (rule == null) return true;
        for (var k = 0; k < upTo && k < rule.Cols.Count && k < rule.Sel.Count; k++)
        {
            if (rule.Sel[k].Count == 0) continue;
            var vals = AlbumApi.Values(rule.Cols[k], e.Name, DateTimeOffset.FromUnixTimeMilliseconds(e.Modified).UtcDateTime, e.Meta, zone, e.Id, e.Description);
            if (!vals.Any(rule.Sel[k].Contains)) return false;
        }
        return true;
    }

    /// <summary>JPG und RAW gleichen Namens im selben Ordner zu einem Stapel (wie web/src/files/common.tsx stackPhotos).</summary>
    public static List<Entry> StackPhotos(List<Entry> list)
    {
        static string Key(Entry e) => e.Id[..^e.Ext.Length].ToLowerInvariant();
        var groups = list.GroupBy(Key).Where(g => g.Count() > 1 && g.Any(e => RawExt.Contains(e.Ext))).ToDictionary(g => g.Key, g => g.ToList());
        if (groups.Count == 0) return list;
        var lead = new Dictionary<string, Entry>();
        foreach (var (k, g) in groups)
            if (LeadExt.Select(x => g.FirstOrDefault(e => e.Ext == x)).FirstOrDefault(e => e != null) is { } head) lead[k] = head;
        var outList = new List<Entry>(list.Count);
        foreach (var e in list)
        {
            if (!lead.TryGetValue(Key(e), out var h)) outList.Add(e);
            else if (ReferenceEquals(h, e)) outList.Add(e with { Stack = groups[Key(e)].Where(x => !ReferenceEquals(x, e)).ToList() });
        }
        return outList;
    }

    private static readonly CompareInfo Names = CultureInfo.GetCultureInfo("de-DE").CompareInfo;
    private const CompareOptions NameOrder = CompareOptions.IgnoreCase | CompareOptions.NumericOrdering;

    private static View Compute(Index ix, Query q, TimeZoneInfo zone, Func<Entry, (string Key, string Label)>? groupOf = null)
    {
        var rule = AlbumApi.ParseRule(q.Filter);
        var byExt = q.Ext.Length == 0 ? ix.Items : ix.Items.Where(e => e.Ext == q.Ext).ToList();
        var types = ix.Items.GroupBy(e => e.Ext).ToDictionary(g => g.Key, g => g.Count());
        var list = rule == null ? byExt : byExt.Where(e => Passes(e, rule, zone, int.MaxValue)).ToList();
        if (q.Stack) list = StackPhotos(list);
        Func<Entry, Entry, int> by = q.Sort switch
        {
            "name" => (a, b) => Names.Compare(a.Name, b.Name, NameOrder),
            "size" => (a, b) => a.Size.CompareTo(b.Size),
            "modified" => (a, b) => a.Modified.CompareTo(b.Modified),
            _ => (a, b) => a.Taken.CompareTo(b.Taken),
        };
        var dir = q.Desc ? -1 : 1;
        var sorted = list.ToList();
        // Nach Quelle: die Gruppe zuerst (eigene, Galerie-Ordner nach Name, geteilte), darin wie gewählt
        groupOf ??= q.Group == "source" ? e => SourceOf(ix, e) : null;
        var source = groupOf != null ? sorted.Distinct().ToDictionary(e => e, e => groupOf(e), ReferenceEqualityComparer.Instance) : null;
        sorted.Sort((a, b) =>
            (source == null ? 0 : string.CompareOrdinal(source[a].Key, source[b].Key)) is var s && s != 0 ? s
            : dir * by(a, b) is var c && c != 0 ? c : string.CompareOrdinal(a.Id, b.Id));
        // Abschnitte: Monate (Zeitzone des Benutzers) bei Sortierung nach Datum, sonst Seiten; nach Quelle je Quelle und Monat
        var buckets = new List<Bucket>();
        var time = q.Sort is "taken" or "modified";
        string KeyOf(Entry e, int n) => (source == null ? "" : "s" + source[e].Key + "|") + (time
            ? TimeZoneInfo.ConvertTimeFromUtc(DateTimeOffset.FromUnixTimeMilliseconds(q.Sort == "modified" ? e.Modified : e.Taken).UtcDateTime, zone).ToString("yyyy-MM", CultureInfo.InvariantCulture)
            : "p" + (n / PageSize).ToString(CultureInfo.InvariantCulture));
        var cur = new List<Entry>();
        string? curKey = null;
        void Close() { if (curKey != null) buckets.Add(new Bucket(curKey, cur, HashOf(cur), source?[cur[0]].Label)); }
        for (var n = 0; n < sorted.Count; n++)
        {
            var k = KeyOf(sorted[n], n);
            if (curKey != null && k != curKey) { Close(); cur = []; }
            curKey = k;
            cur.Add(sorted[n]);
        }
        Close();
        // Nach der Zeit sortiert liegt jeder Monat am Stück - der Schlüssel ist eindeutig
        return new View { Buckets = buckets, ByKey = buckets.ToDictionary(b => b.Key), Total = byExt.Count, Types = types, Shown = sorted };
    }

    /// <summary>Kurzer Fingerabdruck eines Abschnitts: ändert sich etwas, das die Kacheln zeigen, ändert er sich.</summary>
    private static string HashOf(List<Entry> items)
    {
        var sb = new StringBuilder();
        foreach (var e in items)
        {
            sb.Append(e.Id).Append('|').Append(e.Etag).Append('|').Append(e.Meta).Append('|').Append(e.Description).Append(e.Favorite ? 'f' : '-').Append(e.Link ? 'l' : '-')
                .Append(e.People ? 'p' : '-').Append(e.Owner).Append(e.CanWrite).Append(e.ShowOnly ? 's' : '-');
            if (e.Stack != null) foreach (var x in e.Stack) sb.Append('+').Append(x.Id).Append(x.Etag);
            sb.Append('\n');
        }
        return "\"" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16].ToLowerInvariant() + "\"";
    }

    public static Query ParseQuery(string? sort, bool? desc, bool? stack, string? ext, string? filter, string? group = null) => new(
        sort is "modified" or "name" or "size" ? sort : "taken", desc ?? true, stack ?? true,
        string.IsNullOrWhiteSpace(ext) ? "" : (ext.StartsWith('.') ? ext : "." + ext).ToLowerInvariant(),
        AlbumApi.ParseRule(filter) is { } r && r.Sel.Any(s => s.Count > 0) ? filter : null, group is "source" ? group : "");

    public static async Task<View> ViewAsync(HttpContext http, ServerConfig config, Query q)
    {
        var ix = await IndexAsync(http, config);
        return ix.Views.GetOrAdd(q, _ => Compute(ix, q, http.Space().Zone));
    }

    // ---------------- Antworten ----------------

    internal static void Write(Utf8JsonWriter w, Entry e)
    {
        w.WriteStartObject();
        w.WriteString("i", e.Id);
        w.WriteNumber("s", e.Size);
        w.WriteNumber("m", e.Modified);
        if (e.Mime != null) w.WriteString("t", e.Mime);
        w.WriteString("e", e.Etag);
        if (e.FileId != 0) w.WriteNumber("f", e.FileId);
        if (e.Meta is { } m)
        {
            if (m.Taken is long k) w.WriteNumber("k", k);
            if (m.Width is int wd) w.WriteNumber("w", wd);
            if (m.Height is int ht) w.WriteNumber("h", ht);
            if (m.Duration is double du) w.WriteNumber("d", du);
            if (m.Rating is int r) w.WriteNumber("r", r);
            if (m.Label is { } lb) w.WriteString("lb", lb);
            if (m.Pick is int pk && pk != 0) w.WriteNumber("pk", pk);
            if (m.Camera != null) w.WriteString("c", m.Camera);
            if (m.Lens != null) w.WriteString("l", m.Lens);
            if (m.Focal is double fo) w.WriteNumber("fo", fo);
            if (m.Aperture is double ap) w.WriteNumber("a", ap);
            if (m.Exposure is double ex) w.WriteNumber("x", ex);
            if (m.Iso is int iso) w.WriteNumber("o", iso);
            if (m.Title != null) w.WriteString("ti", m.Title);
            if (m.Keywords != null) w.WriteString("kw", m.Keywords);
            if (m.Lat is double la) w.WriteNumber("la", la);
            if (m.Lon is double lo) w.WriteNumber("lo", lo);
        }
        if (e.Description != null) w.WriteString("de", e.Description);
        if (e.Favorite) w.WriteNumber("fv", 1);
        if (e.Link) w.WriteNumber("ln", 1);
        if (e.People) w.WriteNumber("pp", 1);
        if (e.Owner != null) w.WriteString("ow", e.Owner);
        if (e.CanWrite is bool cw) w.WriteBoolean("cw", cw);
        if (e.ShowOnly) w.WriteNumber("so", 1);
        if (e.Stack is { Count: > 0 })
        {
            w.WriteStartArray("st");
            foreach (var x in e.Stack) Write(w, x);
            w.WriteEndArray();
        }
        w.WriteEndObject();
    }

    internal static byte[] Json(Action<Utf8JsonWriter> write)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms)) write(w);
        return ms.ToArray();
    }

    /// <summary>Monate (bzw. Seiten) mit Anzahl und Fingerabdruck - klein, auch bei zehntausenden Fotos.</summary>
    public static byte[] BucketsJson(View v) => v.BucketsJson ??= Json(w =>
    {
        w.WriteStartObject();
        w.WriteNumber("total", v.Total);
        w.WriteNumber("shown", v.Shown.Count);
        w.WriteStartObject("types");
        foreach (var (ext, n) in v.Types.OrderByDescending(x => x.Value)) w.WriteNumber(ext, n);
        w.WriteEndObject();
        w.WriteStartArray("buckets");
        foreach (var b in v.Buckets)
        {
            w.WriteStartObject();
            w.WriteString("k", b.Key);
            if (b.Label != null) w.WriteString("l", b.Label);
            w.WriteNumber("n", b.Items.Count);
            w.WriteString("h", b.Hash.Trim('"'));
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    });

    /// <summary>Alle Fotos auf einmal in derselben kurzen Form (Handy-App: ihr Raster zeichnet ohnehin nur Sichtbares).</summary>
    public static byte[] AllJson(View v) => v.AllJson ??= Json(w =>
    {
        w.WriteStartObject();
        w.WriteStartArray("items");
        foreach (var e in v.Shown) Write(w, e);
        w.WriteEndArray();
        w.WriteEndObject();
    });

    /// <summary>
    /// Suche in der Galerie: eigene Ablage und Galerie-Ordner, nach Name, Titel, Stichwörtern, Beschreibung
    /// (ohne Groß/Klein und Akzente), neueste zuerst, höchstens <paramref name="limit"/>.
    /// </summary>
    public static byte[] SearchJson(View v, string term, int limit = 5000)
    {
        var cmp = CultureInfo.InvariantCulture.CompareInfo;
        const CompareOptions o = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
        bool Hit(string? s) => s != null && cmp.IndexOf(s, term, o) >= 0;
        return Json(w =>
        {
            w.WriteStartObject();
            w.WriteStartArray("items");
            foreach (var e in v.Shown.Where(e => Hit(e.Name) || Hit(e.Meta?.Title) || Hit(e.Meta?.Keywords) || Hit(e.Description)).Take(limit)) Write(w, e);
            w.WriteEndArray();
            w.WriteEndObject();
        });
    }

    /// <summary>Die Fotos eines Abschnitts.</summary>
    public static byte[] BucketJson(Bucket b) => b.Json ??= Json(w =>
    {
        w.WriteStartObject();
        w.WriteString("k", b.Key);
        w.WriteStartArray("items");
        foreach (var e in b.Items) Write(w, e);
        w.WriteEndArray();
        w.WriteEndObject();
    });

    /// <summary>
    /// Werte der Filterleiste mit Anzahl (wie web/src/files/PhotoFilter.tsx): je Spalte zählt nur, was die Spalten links
    /// davon durchlassen. Über die Fotos nach Dateiendung, ohne Stapel - wie bisher im Browser.
    /// </summary>
    public static async Task<byte[]> FacetsAsync(HttpContext http, ServerConfig config, Query q, string[] cols)
    {
        var ix = await IndexAsync(http, config);
        var zone = http.Space().Zone;
        var key = string.Join(",", cols) + "|" + q.Ext + "|" + q.Filter;
        return (byte[])ix.Facets.GetOrAdd(key, _ =>
        {
            var rule = AlbumApi.ParseRule(q.Filter);
            var sel = Enumerable.Range(0, cols.Length).Select(k => rule != null && k < rule.Cols.Count && k < rule.Sel.Count && rule.Cols[k] == cols[k] ? rule.Sel[k] : []).ToList();
            var cascade = new AlbumApi.Rule(cols.ToList(), sel);
            var items = q.Ext.Length == 0 ? ix.Items : ix.Items.Where(e => e.Ext == q.Ext).ToList();
            return Json(w =>
            {
                w.WriteStartObject();
                w.WriteStartArray("cols");
                for (var k = 0; k < cols.Length; k++)
                {
                    var counts = new Dictionary<string, int>();
                    var total = 0;
                    foreach (var e in items)
                    {
                        if (!Passes(e, cascade, zone, k)) continue;
                        total++;
                        foreach (var v in AlbumApi.Values(cols[k], e.Name, DateTimeOffset.FromUnixTimeMilliseconds(e.Modified).UtcDateTime, e.Meta, zone, e.Id, e.Description).Distinct())
                            counts[v] = counts.GetValueOrDefault(v) + 1;
                    }
                    w.WriteStartObject();
                    w.WriteNumber("total", total);
                    w.WriteStartArray("values");
                    foreach (var (v, n) in counts) { w.WriteStartArray(); w.WriteStringValue(v); w.WriteNumberValue(n); w.WriteEndArray(); }
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            });
        });
    }

    /// <summary>Für den Atlas: nur Fotos mit Aufnahmeort, knapp [Id, Breite, Länge, ETag, Typ].</summary>
    public static byte[] GeoJson(View v) => Json(w =>
    {
        w.WriteStartObject();
        w.WriteStartArray("items");
        foreach (var e in v.Shown)
        {
            if (e.Meta?.Lat is not double la || e.Meta.Lon is not double lo) continue;
            w.WriteStartArray();
            w.WriteStringValue(e.Id); w.WriteNumberValue(la); w.WriteNumberValue(lo); w.WriteStringValue(e.Etag);
            if (e.Mime != null) w.WriteStringValue(e.Mime); else w.WriteNullValue();
            w.WriteEndArray();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    });

    /// <summary>ETag aus dem Inhalt (für die Monatsliste: ändert sich ein Monat, ändert sich sein Fingerabdruck darin).</summary>
    public static string EtagOf(byte[] body) => "\"" + Convert.ToHexString(SHA256.HashData(body))[..20].ToLowerInvariant() + "\"";

    /// <summary>JSON mit ETag ausliefern; unverändert nur 304.</summary>
    public static IResult Send(HttpContext http, string etag, Func<byte[]> body)
    {
        http.Response.Headers.CacheControl = "private, no-cache";
        http.Response.Headers.ETag = etag;
        if (http.Request.Headers.IfNoneMatch.ToString() == etag) return Results.StatusCode(304);
        return Results.Bytes(body(), "application/json");
    }
}
