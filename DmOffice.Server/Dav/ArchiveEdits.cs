using System.IO.Compression;
using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DmOffice.Core;

namespace DmOffice.Dav;

/// <summary>
/// Offene Änderungen an einem ZIP: sie sammeln sich, bis das Archiv gespeichert oder verworfen wird. Der Stand liegt in der
/// Datenbank dessen, der ändert (Schlüssel „archive.edit:…“), hinzugefügte Inhalte als Blobs in seinem BlobStore - ein Neustart
/// verliert nichts. Nach <see cref="Idle"/> ohne Änderung (spätestens <see cref="MaxAge"/> nach der ersten) wird aufgeräumt:
/// unverknüpfte Blobs löscht <see cref="FileStore.SweepBlobs"/> nach einem Tag, darum muss der Stand vorher weg sein.
/// Gespeichert wird als neues ZIP über <see cref="FileStore.PutAsync"/>: das bisherige bleibt als Version.
/// </summary>
public static class ArchiveEdits
{
    public static readonly TimeSpan Idle = TimeSpan.FromHours(12);
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(20);
    private const string Prefix = "archive.edit:";

    /// <summary>Eine Datei im neuen Archiv: aus dem alten (E: Pfad dort) oder neu (B: Blob beim Bearbeiter).</summary>
    public sealed class Item
    {
        public string? E { get; set; }
        public string? B { get; set; }
        public long S { get; set; }
        public DateTime M { get; set; }
        /// <summary>Ersetzt eine Datei, die es im Archiv schon gab (Anzeige „geändert“ statt „neu“).</summary>
        public bool R { get; set; }
    }

    /// <summary>Der ganze künftige Inhalt (Dateien und Ordner) - bei höchstens <see cref="Archives.MaxEditEntries"/> Einträgen klein genug.</summary>
    public sealed class State
    {
        public string Owner { get; set; } = "";
        /// <summary>Pfad beim Besitzer.</summary>
        public string Path { get; set; } = "";
        /// <summary>Pfad, wie der Bearbeiter ihn sieht (für die Liste offener Änderungen).</summary>
        public string Shown { get; set; } = "";
        /// <summary>ETag des Archivs, auf dem die Änderungen aufbauen.</summary>
        public string Etag { get; set; } = "";
        public DateTime Started { get; set; }
        public DateTime Touched { get; set; }
        public int Changes { get; set; }
        public Dictionary<string, Item> Files { get; set; } = new(StringComparer.Ordinal);
        public HashSet<string> Dirs { get; set; } = new(StringComparer.Ordinal);

        public bool Exists(string path) => Files.ContainsKey(path) || Dirs.Contains(path);
    }

    private static string Key(string owner, string path) =>
        Prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(owner + "\n" + path)))[..32].ToLowerInvariant();

    /// <summary>Offene Änderungen des Bearbeiters an diesem Archiv - null, wenn keine (oder abgelaufen).</summary>
    public static State? Get(UserDb editor, string owner, string path)
    {
        if (editor.Get(Key(owner, path)) is not { } json) return null;
        var s = JsonSerializer.Deserialize<State>(json);
        if (s == null || Expired(s, DateTime.UtcNow)) { Drop(editor, owner, path, s); return null; }
        return s;
    }

    private static bool Expired(State s, DateTime now) => now - s.Touched > Idle || now - s.Started > MaxAge;

    public static void Put(UserDb editor, State s)
    {
        s.Touched = DateTime.UtcNow;
        s.Changes++;
        editor.Set(Key(s.Owner, s.Path), JsonSerializer.Serialize(s));
    }

    /// <summary>Verwerfen: Stand und hinzugefügte Inhalte weg.</summary>
    public static void Drop(UserDb editor, string owner, string path, State? s)
    {
        if (s != null)
            foreach (var b in s.Files.Values.Select(f => f.B).OfType<string>().Distinct())
                if (BlobStore.ValidId(b)) editor.Blobs.Delete(b);
        editor.Set(Key(owner, path), null);
    }

    /// <summary>Alle offenen Änderungen des Bearbeiters (abgelaufene werden dabei aufgeräumt).</summary>
    public static List<State> All(UserDb editor)
    {
        var list = new List<State>();
        foreach (var key in editor.Query("SELECT key FROM kv WHERE key GLOB 'archive.edit:*'", r => r.GetString(0)))
        {
            State? s;
            try { s = JsonSerializer.Deserialize<State>(editor.Get(key) ?? "null"); }
            catch (JsonException) { s = null; }
            if (s == null) { editor.Set(key, null); continue; }
            if (Expired(s, DateTime.UtcNow)) { Drop(editor, s.Owner, s.Path, s); continue; }
            list.Add(s);
        }
        return list;
    }

    /// <summary>Stündlich (FilesCleanup): Abgelaufenes wegräumen. Gibt die Zahl der verworfenen Stände zurück.</summary>
    public static int Expire(UserDb editor)
    {
        int before = editor.Query("SELECT COUNT(*) FROM kv WHERE key GLOB 'archive.edit:*'", r => r.GetInt32(0)).First();
        return before == 0 ? 0 : before - All(editor).Count;
    }

    /// <summary>Neuer Stand aus dem Verzeichnis: jede Datei kommt aus dem alten Archiv, Ordner auch die nur mittelbar vorhandenen.</summary>
    public static State Start(ArchiveListing listing, string owner, string path, string shown, string etag)
    {
        var s = new State { Owner = owner, Path = path, Shown = shown, Etag = etag, Started = DateTime.UtcNow };
        foreach (var e in listing.Entries)
        {
            if (e.IsDir) s.Dirs.Add(e.Path);
            else s.Files[e.Path] = new Item { E = e.Path, S = e.Size, M = e.Modified?.ToUniversalTime() ?? DateTime.UtcNow };
            for (var p = Parent(e.Path); p.Length > 0; p = Parent(p)) s.Dirs.Add(p);
        }
        // Eine Datei und ein Ordner mit gleichem Namen (kaputtes Archiv): die Datei gewinnt nicht - sonst ließe sich der Ordner nicht öffnen
        foreach (var d in s.Dirs) s.Files.Remove(d);
        return s;
    }

    public static string Parent(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";

    private static bool Under(string path, string dir) => dir.Length == 0 || path == dir || path.StartsWith(dir + "/", StringComparison.Ordinal);

    // ---------------- Ändern ----------------

    /// <summary>Datei hinzufügen oder ersetzen; fehlende Ordner entstehen mit.</summary>
    public static void SetFile(UserDb editor, State s, string path, BlobInfo blob)
    {
        if (s.Dirs.Contains(path)) throw new DavConflictException("Dort liegt im Archiv ein Ordner.");
        for (var p = Parent(path); p.Length > 0; p = Parent(p))
        {
            if (s.Files.ContainsKey(p)) throw new DavConflictException($"„{p}“ ist im Archiv eine Datei, kein Ordner.");
            s.Dirs.Add(p);
        }
        var replaced = s.Files.TryGetValue(path, out var old) && (old.E != null || old.R);
        if (old?.B is { } oldBlob && s.Files.Values.Count(f => f.B == oldBlob) == 1) editor.Blobs.Delete(oldBlob);
        s.Files[path] = new Item { B = blob.Id, S = blob.Size, M = DateTime.UtcNow, R = replaced };
        CheckSize(s);
    }

    public static void Mkdir(State s, string path)
    {
        if (s.Exists(path)) throw new InvalidOperationException("Gibt es schon.");
        for (var p = path; p.Length > 0; p = Parent(p))
        {
            if (s.Files.ContainsKey(p)) throw new DavConflictException($"„{p}“ ist im Archiv eine Datei, kein Ordner.");
            s.Dirs.Add(p);
        }
        CheckSize(s);
    }

    /// <summary>Umbenennen oder verschieben, Ordner samt Inhalt.</summary>
    public static void Move(State s, string from, string to)
    {
        if (from == to) return;
        if (!s.Exists(from)) throw new KeyNotFoundException("Diesen Eintrag gibt es im Archiv nicht.");
        if (s.Exists(to)) throw new InvalidOperationException("Gibt es schon.");
        if (to.StartsWith(from + "/", StringComparison.Ordinal)) throw new ArgumentException("Ziel liegt in der Quelle.");
        var parent = Parent(to);
        if (parent.Length > 0 && !s.Dirs.Contains(parent)) throw new DavConflictException("Übergeordneter Ordner fehlt.");
        string Map(string p) => to + p[from.Length..];
        foreach (var f in s.Files.Keys.Where(k => Under(k, from)).ToList()) { var v = s.Files[f]; s.Files.Remove(f); s.Files[Map(f)] = v; }
        foreach (var d in s.Dirs.Where(k => Under(k, from)).ToList()) { s.Dirs.Remove(d); s.Dirs.Add(Map(d)); }
    }

    /// <summary>Entfernen (Ordner samt Inhalt); hinzugefügte Inhalte, auf die nichts mehr zeigt, gleich löschen.</summary>
    public static void Remove(UserDb editor, State s, string path)
    {
        if (!s.Exists(path)) throw new KeyNotFoundException("Diesen Eintrag gibt es im Archiv nicht.");
        foreach (var f in s.Files.Keys.Where(k => Under(k, path)).ToList())
        {
            var b = s.Files[f].B;
            s.Files.Remove(f);
            if (b != null && s.Files.Values.All(x => x.B != b)) editor.Blobs.Delete(b);
        }
        s.Dirs.RemoveWhere(d => Under(d, path));
    }

    private static void CheckSize(State s)
    {
        if (s.Files.Count + s.Dirs.Count > Archives.MaxEditEntries)
            throw new ArchiveException($"Mehr als {Archives.MaxEditEntries:N0} Einträge – so große Archive lassen sich nur lesen und entpacken.");
        if (s.Files.Values.Sum(f => f.S) > Archives.MaxEditBytes * 2)
            throw new ArchiveException("Das Archiv würde zu groß zum Ändern.");
    }

    // ---------------- Lesen im geänderten Stand ----------------

    /// <summary>Inhalt einer Datei im geänderten Stand: hinzugefügt (Blob beim Bearbeiter) oder aus dem alten Archiv.</summary>
    public static Stream Open(UserDb editor, UserDb owner, State s, ArchiveListing listing, string path, string? password)
    {
        if (!s.Files.TryGetValue(path, out var f)) throw new KeyNotFoundException("Diesen Eintrag gibt es im Archiv nicht.");
        return f.B is { } b ? editor.Blobs.OpenRead(b) : Archives.OpenEntry(owner, s.Path, listing, f.E!, password);
    }

    // ---------------- Speichern ----------------

    /// <summary>
    /// Neues ZIP schreiben und an die Stelle des alten setzen (das alte bleibt als Version). Gestreamt: ein Faden schreibt das ZIP
    /// in eine Pipe, FileStore.PutAsync liest daraus und verschlüsselt blockweise in den BlobStore - es liegt nie ganz im Speicher
    /// oder unverschlüsselt auf der Platte. Unveränderte Einträge werden aus dem alten Archiv ausgepackt und neu gepackt.
    /// ZIP64 schreibt .NET bei Bedarf selbst (über 4 GB oder 65535 Einträge); verschlüsselte ZIPs siehe <see cref="ZipCryptWriter"/>.
    /// </summary>
    /// <param name="target">Pfad beim Besitzer: das Archiv selbst (dann nur, wenn es noch den Stand von s.Etag hat) oder eine Kopie.</param>
    public static async Task<string> SaveAsync(UserDb editor, UserDb owner, State s, ArchiveListing listing, string? password, string target, CancellationToken ct)
    {
        var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 1024 * 1024, resumeWriterThreshold: 512 * 1024));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var writer = Task.Run(() =>
        {
            try
            {
                using (var output = pipe.Writer.AsStream(leaveOpen: true))
                    Write(output, editor, owner, s, listing, password, stop.Token);
                pipe.Writer.Complete();
            }
            catch (Exception ex) { pipe.Writer.Complete(ex); }
        }, CancellationToken.None);
        try
        {
            await using var input = pipe.Reader.AsStream();
            var replace = target == s.Path;
            await FileStore.PutAsync(owner, target, input, "application/zip", ifMatch: replace ? s.Etag : null, ifNoneMatch: replace ? null : "*", ct: ct);
        }
        catch
        {
            stop.Cancel();
            pipe.Reader.Complete();
            await writer;
            throw;
        }
        await writer;
        return target;
    }

    /// <summary>
    /// Das ZIP schreiben: erst die Ordner, dann die Dateien (alphabetisch, wie Explorer und 7-Zip sie zeigen). War das alte ZIP
    /// verschlüsselt, wird das neue genauso verschlüsselt (gleiches Verfahren, gleiches Passwort) - jeder Eintrag, auch hinzugefügte.
    /// </summary>
    internal static void Write(Stream output, UserDb editor, UserDb owner, State s, ArchiveListing listing, string? password, CancellationToken ct)
    {
        using var source = FileStore.OpenRead(owner, s.Path);
        var encryption = listing.Encrypted ? ZipCryptWriter.Inspect(source) : ZipEncryption.None;
        if (encryption != ZipEncryption.None && string.IsNullOrEmpty(password)) throw new ArchivePasswordException(false);
        source.Position = 0;
        // Ohne Verschlüsselung: ZipArchive (ZIP64 bei Bedarf); mit: der eigene Schreiber. ZipArchive schreibt synchron;
        // leaveOpen: die Pipe schließt der Aufrufer
        ZipArchive? zip = encryption == ZipEncryption.None ? new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8) : null;
        var crypt = zip == null ? new ZipCryptWriter(output, password, encryption, () => new BlobTemp(editor)) : null;
        using (zip)
        {
            // Leere Ordner brauchen einen eigenen Eintrag; die übrigen ergeben sich aus den Dateipfaden
            var needed = s.Dirs.Where(d => !s.Files.Keys.Any(f => f.StartsWith(d + "/", StringComparison.Ordinal))
                                           && !s.Dirs.Any(x => x.StartsWith(d + "/", StringComparison.Ordinal))).OrderBy(d => d, StringComparer.Ordinal);
            foreach (var d in needed)
            {
                if (zip != null) zip.CreateEntry(d + "/").LastWriteTime = DateTime.Now;
                else crypt!.AddDirectory(d, DateTime.Now);
            }
            // Dateien aus dem alten Archiv: das Verzeichnis einmal lesen, nicht für jede Datei neu
            using var archive = Archives.OpenArchive(source, password);
            var old = Archives.Index(archive);
            foreach (var (path, item) in s.Files.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                var fast = Zip.Packed.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant());
                var m = DateTime.SpecifyKind(item.M.Year < 1980 ? DateTime.UtcNow : item.M, DateTimeKind.Utc);
                Stream data;
                if (item.B is { } blob) data = editor.Blobs.OpenRead(blob);
                else
                {
                    if (listing.Find(item.E!) is not { } e || !old.TryGetValue(item.E!, out var raw)) throw new ArchiveException($"„{item.E}“ fehlt im bisherigen Archiv.");
                    data = Archives.Limit(Archives.Unlocked(raw.OpenEntryStream), e.Size, e.Path);
                }
                using (data)
                {
                    if (crypt != null) { crypt.AddFile(path, data, m, fast, ct); continue; }
                    var entry = zip!.CreateEntry(path, fast ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                    entry.LastWriteTime = new DateTimeOffset(m).ToLocalTime();
                    using var target = entry.Open();
                    data.CopyTo(target, 256 * 1024);
                }
            }
            crypt?.Finish();
        }
    }

    /// <summary>Zwischenspeicher für einen gepackten Eintrag als Blob (verschlüsselt auf der Platte), danach gelöscht.</summary>
    private sealed class BlobTemp(UserDb db) : ZipCryptWriter.ITemp
    {
        private readonly BlobWriter _writer = db.Blobs.Create();
        private string? _id;
        public Stream Writer => _writer;
        public Stream Read()
        {
            _id = _writer.Complete().Id;
            return _id == null ? new MemoryStream() : db.Blobs.OpenRead(_id);
        }
        public void Dispose()
        {
            _writer.Dispose();
            if (_id != null) db.Blobs.Delete(_id);
        }
    }
}
