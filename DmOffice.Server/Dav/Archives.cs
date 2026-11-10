using System.Collections.Concurrent;
using DmOffice.Core;
using System.Security.Cryptography;
using SharpCompress.Archives;
using SharpCompress.Readers;

namespace DmOffice.Dav;

/// <summary>Archiv nicht lesbar, verdächtig (ZIP-Bombe) oder zu groß - 400 mit Text.</summary>
public sealed class ArchiveException(string message) : InvalidOperationException(message);

/// <summary>Verschlüsselt: Passwort fehlt oder stimmt nicht.</summary>
public sealed class ArchivePasswordException(bool wrong)
    : UnauthorizedAccessException(wrong ? "Das Passwort stimmt nicht." : "Das Archiv ist verschlüsselt. Zum Öffnen wird das Passwort gebraucht.")
{
    public bool Wrong => wrong;
}

/// <summary>Ein Eintrag im Archiv. Path: mit „/“ getrennt, ohne führenden und abschließenden Schrägstrich.</summary>
public sealed record ArchiveEntry(string Path, bool IsDir, long Size, long Packed, DateTime? Modified, bool Encrypted)
{
    public string Name => Path[(Path.LastIndexOf('/') + 1)..];
}

/// <summary>Verzeichnis eines Archivs. Unsafe: Einträge mit Pfaden wie „../“ oder absoluten Pfaden (ausgelassen).
/// Bomb: angegebene Größen passen nicht zu einem normalen Archiv - Einträge werden dann nicht ausgepackt.</summary>
public sealed record ArchiveListing(string Format, bool Encrypted, List<ArchiveEntry> Entries, int Unsafe, long Total, bool Bomb)
{
    private Dictionary<string, ArchiveEntry>? _byPath;
    public ArchiveEntry? Find(string path) => (_byPath ??= Entries.GroupBy(e => e.Path).ToDictionary(g => g.Key, g => g.Last())).GetValueOrDefault(path);
}

/// <summary>
/// Archive in der Dateiablage lesen (ZIP, 7z, RAR, TAR, TAR.GZ) mit SharpCompress (MIT): nur das Verzeichnis lesen und
/// Einträge einzeln als Stream öffnen - nie das ganze Archiv auspacken. Jeder Eintrag liefert höchstens so viel, wie das
/// Verzeichnis angibt (sonst Abbruch), dazu Grenzen für Verhältnis und Summe (ZIP-Bombe) und nur saubere Pfade.
/// Schreiben (nur ZIP) in <see cref="ArchiveEdits"/>.
/// </summary>
public static class Archives
{
    /// <summary>Mehr Einträge liest dmOffice nicht ein (das Verzeichnis liegt im Speicher).</summary>
    public const int MaxEntries = 100_000;
    /// <summary>Bis hierher lässt sich ein ZIP ändern, darüber nur lesen und entpacken (wenig Arbeitsspeicher auf kleinen Servern).</summary>
    public const long MaxEditBytes = 2L * 1024 * 1024 * 1024;
    public const int MaxEditEntries = 20_000;
    /// <summary>Ein Eintrag, der mehr als so viel ausgepackt größer ist als gepackt, gilt ab <see cref="BombMinBytes"/> als ZIP-Bombe.</summary>
    public const long MaxRatio = 1000;
    public const long BombMinBytes = 100L * 1024 * 1024;
    /// <summary>Höchstens so viel zusammen ausgepackt (Summe der Angaben im Verzeichnis).</summary>
    public const long MaxTotal = 20L * 1024 * 1024 * 1024;

    /// <summary>Format nach der Endung - null, wenn es keins ist, das dmOffice öffnet.</summary>
    public static string? FormatOf(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.EndsWith(".tar.gz") || n.EndsWith(".tgz")) return "tgz";
        return Path.GetExtension(n) switch { ".zip" => "zip", ".7z" => "7z", ".rar" => "rar", ".tar" => "tar", _ => null };
    }

    /// <summary>Pfad eines Eintrags bereinigt - null bei „..“, absoluten Pfaden, Laufwerksbuchstaben oder Steuerzeichen.</summary>
    public static string? CleanPath(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        var k = key.Replace('\\', '/');
        if (k.StartsWith('/') || k.Length >= 2 && k[1] == ':' && char.IsAsciiLetter(k[0])) return null;
        var parts = k.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(p => p != ".").ToList();
        if (parts.Count == 0 || parts.Any(p => p == ".." || p.Any(char.IsControl) || p.Length > 255)) return null;
        return string.Join("/", parts);
    }

    /// <summary>Pfad aus einer Anfrage (Ordner oder Eintrag im Archiv): wie <see cref="CleanPath"/>, sonst Fehler; "" ist die oberste Ebene.</summary>
    public static string Inner(string? path) => string.IsNullOrEmpty(path?.Trim('/')) ? "" : CleanPath(path) ?? throw new ArgumentException("Ungültiger Pfad im Archiv.");

    private static ReaderOptions Options(string? password) => new() { Password = string.IsNullOrEmpty(password) ? null : password, LeaveStreamOpen = true };

    // ---------------- Verzeichnis (zwischengespeichert) ----------------

    private static readonly ConcurrentDictionary<string, (DateTime At, ArchiveListing Listing)> Cache = new();

    /// <summary>Verzeichnis lesen. Schlüssel aus Besitzer, Pfad und ETag: ändert sich die Datei, wird neu gelesen.</summary>
    public static ArchiveListing List(UserDb db, string path, string etag, string? password)
    {
        var key = db.UserId + "|" + path + "|" + etag + "|" + (password == null ? "" : Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(password)))[..16]);
        if (Cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(10)) { Cache[key] = (DateTime.UtcNow, hit.Listing); return hit.Listing; }
        ArchiveListing listing;
        using (var stream = FileStore.OpenRead(db, path)) listing = Read(stream, FormatOf(path) ?? throw new ArgumentException("Kein unterstütztes Archiv."), password);
        Cache[key] = (DateTime.UtcNow, listing);
        foreach (var old in Cache.OrderByDescending(x => x.Value.At).Skip(16).Select(x => x.Key).ToList()) Cache.TryRemove(old, out _);
        return listing;
    }

    /// <summary>Verzeichnis aus einem Stream (Tests, Prüfen vor dem Speichern).</summary>
    public static ArchiveListing Read(Stream stream, string format, string? password)
    {
        var entries = new List<ArchiveEntry>();
        int bad = 0;
        bool encrypted = false;
        void Add(SharpCompress.Common.IEntry e)
        {
            if (entries.Count + bad >= MaxEntries) throw new ArchiveException($"Das Archiv hat mehr als {MaxEntries:N0} Einträge – das ist zu viel zum Öffnen.");
            if (e.IsEncrypted) encrypted = true;
            if (e.LinkTarget != null) { bad++; return; }   // Verknüpfungen (TAR): nie folgen
            var p = CleanPath(e.Key);
            if (p == null) { bad++; return; }
            // Ohne Zeitstempel steht in TAR oft 0 (1970) - dann lieber keiner
            var modified = e.LastModifiedTime is { Year: > 1970 } m ? m : (DateTime?)null;
            entries.Add(new ArchiveEntry(p, e.IsDirectory, e.IsDirectory ? 0 : Math.Max(0, e.Size), Math.Max(0, e.CompressedSize), modified, e.IsEncrypted));
        }
        try
        {
            if (format == "tgz")
            {
                // Gepacktes TAR: nur der Reihe nach lesbar (der Inhalt wird dabei übersprungen, nicht gespeichert)
                using var reader = ReaderFactory.OpenReader(stream, Options(password));
                while (reader.MoveToNextEntry()) Add(reader.Entry);
            }
            else
            {
                using var archive = Open(stream, format, password);
                foreach (var e in archive.Entries) Add(e);
            }
        }
        catch (Exception ex) when (ex is not (ArchivePasswordException or ArchiveException) && IsPasswordProblem(ex)) { throw new ArchivePasswordException(password != null); }
        catch (Exception ex) when (ex is not (ArchiveException or ArchivePasswordException or OperationCanceledException or BlobUnavailableException))
        {
            throw new ArchiveException("Das Archiv lässt sich nicht lesen – es ist beschädigt oder kein " + Label(format) + ".");
        }
        long total = 0;
        bool bomb = false;
        foreach (var e in entries.Where(e => !e.IsDir))
        {
            total += e.Size;
            // Packed fehlt bei festen (solid) 7z/RAR-Blöcken - dann zählt unten das Verhältnis des ganzen Archivs
            if (e.Packed > 0 && e.Size >= BombMinBytes && e.Size / e.Packed > MaxRatio) bomb = true;
        }
        var length = stream.CanSeek ? stream.Length : 0;
        if (total > MaxTotal || length > 0 && total >= BombMinBytes && total / length > MaxRatio) bomb = true;
        return new ArchiveListing(format, encrypted, entries, bad, total, bomb);
    }

    public static string Label(string format) => format switch
    {
        "zip" => "ZIP-Archiv", "7z" => "7z-Archiv", "rar" => "RAR-Archiv", "tar" => "TAR-Archiv", "tgz" => "TAR.GZ-Archiv", _ => "Archiv",
    };

    /// <summary>Was SharpCompress nicht kann (etwa verschlüsselte ZIPs mit Datenbeschreibung nach jedem Eintrag) - als lesbare Meldung.</summary>
    internal static ArchiveException Unsupported() =>
        new("Dieses Archiv lässt sich hier nicht auspacken (Verschlüsselung in einer Form, die dmOffice nicht liest). Bitte mit einem Programm auf dem Rechner öffnen.");

    /// <summary>Fehlt das Passwort oder ist es falsch? Die Bibliothek meldet das je nach Format unterschiedlich.</summary>
    private static bool IsPasswordProblem(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is NotSupportedException) return false;
            if (e is CryptographicException || e.GetType().Name.Contains("Password", StringComparison.OrdinalIgnoreCase)) return true;
            var m = e.Message;
            if (m.Contains("password", StringComparison.OrdinalIgnoreCase) || m.Contains("encrypted", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // ---------------- Einen Eintrag lesen ----------------

    /// <summary>
    /// Inhalt eines Eintrags als Stream - entpackt beim Lesen, nie ganz im Speicher. Liefert der Eintrag mehr als im Verzeichnis
    /// angegeben, bricht das Lesen ab. Geschlossen wird mit dem Stream auch das Archiv.
    /// </summary>
    public static Stream OpenEntry(UserDb db, string path, ArchiveListing listing, string entryPath, string? password)
    {
        var entry = listing.Find(entryPath) is { IsDir: false } e ? e : throw new KeyNotFoundException("Diesen Eintrag gibt es im Archiv nicht.");
        if (listing.Bomb) throw new ArchiveException("Das Archiv wird nicht ausgepackt: Die angegebenen Größen sehen nach einer ZIP-Bombe aus.");
        if (entry.Encrypted && string.IsNullOrEmpty(password)) throw new ArchivePasswordException(false);
        var source = FileStore.OpenRead(db, path);
        try { return OpenEntry(source, listing.Format, entry, password); }
        catch { source.Dispose(); throw; }
    }

    /// <summary>Eintrag aus einem schon geöffneten Archiv-Stream; der Stream gehört danach dem Ergebnis.</summary>
    public static Stream OpenEntry(Stream source, string format, ArchiveEntry entry, string? password)
    {
        try
        {
            if (format == "tgz")
            {
                var reader = ReaderFactory.OpenReader(source, Options(password));
                while (reader.MoveToNextEntry())
                    if (!reader.Entry.IsDirectory && reader.Entry.LinkTarget == null && CleanPath(reader.Entry.Key) == entry.Path)
                        return new Guard(reader.OpenEntryStream(), entry.Size, entry.Path, [reader, source]);
                throw new KeyNotFoundException("Diesen Eintrag gibt es im Archiv nicht.");
            }
            var archive = Open(source, format, password);
            // Bei mehreren gleichen Namen gilt wie beim Entpacken der letzte
            var hit = archive.Entries.LastOrDefault(e => !e.IsDirectory && e.LinkTarget == null && CleanPath(e.Key) == entry.Path)
                      ?? throw new KeyNotFoundException("Diesen Eintrag gibt es im Archiv nicht.");
            return new Guard(Unlocked(hit.OpenEntryStream), entry.Size, entry.Path, [archive, source]);
        }
        catch (Exception ex) when (ex is not (ArchivePasswordException or ArchiveException) && IsPasswordProblem(ex)) { throw new ArchivePasswordException(password != null); }
    }

    /// <summary>
    /// Mehrere Einträge in einem Durchgang auspacken (Entpacken): TAR.GZ und feste 7z/RAR-Blöcke lassen sich nur der Reihe nach
    /// lesen - einzeln geöffnet finge jeder Eintrag wieder vorne an. Die Reihenfolge ist die des Archivs; bei gleichen Namen
    /// zählt wie beim Lesen der letzte Eintrag.
    /// </summary>
    public static async Task ForEach(UserDb db, string path, ArchiveListing listing, string? password, Func<string, bool> wanted,
        Func<ArchiveEntry, Stream, Task> action, CancellationToken ct)
    {
        if (listing.Bomb) throw new ArchiveException("Das Archiv wird nicht ausgepackt: Die angegebenen Größen sehen nach einer ZIP-Bombe aus.");
        // Bei doppelten Namen nur den letzten (wie Find)
        var last = new Dictionary<string, int>(StringComparer.Ordinal);
        int index = 0;
        async Task Visit(SharpCompress.Common.IEntry e, Func<Stream> open)
        {
            ct.ThrowIfCancellationRequested();
            if (e.IsDirectory || e.LinkTarget != null || CleanPath(e.Key) is not { } p) return;
            var n = index++;
            if (!wanted(p) || last.GetValueOrDefault(p, -1) != n || listing.Find(p) is not { IsDir: false } entry) return;
            if (entry.Encrypted && string.IsNullOrEmpty(password)) throw new ArchivePasswordException(false);
            await using var data = new Guard(Unlocked(open), entry.Size, p, []);
            await action(entry, data);
        }
        void Count(SharpCompress.Common.IEntry e) { if (!e.IsDirectory && e.LinkTarget == null && CleanPath(e.Key) is { } p) last[p] = index++; }
        try
        {
            using var source = FileStore.OpenRead(db, path);
            if (listing.Format == "tgz")
            {
                // Erst zählen (welcher Eintrag bei gleichen Namen der letzte ist) - nur nötig, wenn es Doppelte gibt
                if (listing.Entries.Count != listing.Entries.Select(e => e.Path).Distinct().Count())
                {
                    using (var counter = ReaderFactory.OpenReader(source, Options(password))) while (counter.MoveToNextEntry()) Count(counter.Entry);
                    source.Position = 0;
                }
                else foreach (var e in listing.Entries.Where(e => !e.IsDir)) last[e.Path] = index++;
                index = 0;
                using var reader = ReaderFactory.OpenReader(source, Options(password));
                while (reader.MoveToNextEntry()) await Visit(reader.Entry, reader.OpenEntryStream);
                return;
            }
            using var archive = Open(source, listing.Format, password);
            var all = archive.Entries.ToList();
            foreach (var e in all) Count(e);
            index = 0;
            if (archive.IsSolid)
            {
                using var reader = archive.ExtractAllEntries();
                while (reader.MoveToNextEntry()) await Visit(reader.Entry, reader.OpenEntryStream);
            }
            else foreach (var e in all) await Visit(e, e.OpenEntryStream);
        }
        catch (Exception ex) when (ex is not (ArchivePasswordException or ArchiveException) && IsPasswordProblem(ex)) { throw new ArchivePasswordException(password != null); }
    }

    /// <summary>Archiv mit wahlfreiem Zugriff öffnen (nicht TAR.GZ) - zum Schreiben eines neuen ZIPs aus dem alten.</summary>
    internal static IArchive OpenArchive(Stream source, string? password) => Open(source, "zip", password);

    /// <summary>Nach dem Format öffnen statt raten: die Erkennung von SharpCompress scheitert etwa an einem ZIP, dessen erster
    /// Eintrag verschlüsselt ist und eine Datenbeschreibung hat (so schreiben es Windows und <see cref="ZipCryptWriter"/>).</summary>
    private static IArchive Open(Stream source, string format, string? password) => format switch
    {
        "zip" => SharpCompress.Archives.Zip.ZipArchive.OpenArchive(source, Options(password)),
        "7z" => SharpCompress.Archives.SevenZip.SevenZipArchive.OpenArchive(source, Options(password)),
        "rar" => SharpCompress.Archives.Rar.RarArchive.OpenArchive(source, Options(password)),
        "tar" => SharpCompress.Archives.Tar.TarArchive.OpenArchive(source, Options(password)),
        _ => ArchiveFactory.OpenArchive(source, Options(password)),
    };

    /// <summary>Einträge nach bereinigtem Pfad (bei gleichen Namen der letzte, wie beim Entpacken).</summary>
    internal static Dictionary<string, IArchiveEntry> Index(IArchive archive)
    {
        var map = new Dictionary<string, IArchiveEntry>(StringComparer.Ordinal);
        foreach (var e in archive.Entries)
            if (!e.IsDirectory && e.LinkTarget == null && CleanPath(e.Key) is { } p) map[p] = e;
        return map;
    }

    /// <summary>Öffnen eines Eintrags: ein falsches oder fehlendes Passwort als <see cref="ArchivePasswordException"/>.</summary>
    internal static Stream Unlocked(Func<Stream> open)
    {
        try { return open(); }
        catch (NotSupportedException) { throw Unsupported(); }
        catch (Exception ex) when (ex is not (ArchivePasswordException or ArchiveException) && IsPasswordProblem(ex)) { throw new ArchivePasswordException(true); }
    }

    /// <summary>Entpackten Eintrag auf die angegebene Größe begrenzen (siehe <see cref="Guard"/>).</summary>
    internal static Stream Limit(Stream inner, long max, string name, params IDisposable[] owners) => new Guard(inner, max, name, owners);

    /// <summary>
    /// Begrenzt einen entpackten Eintrag auf die angegebene Größe (ZIP-Bombe mit falscher Angabe im Verzeichnis) und meldet
    /// Lesefehler verständlich. Schließt beim Schließen das Archiv und den Stream darunter.
    /// </summary>
    private sealed class Guard(Stream inner, long max, string name, IDisposable[] owners) : Stream
    {
        private long _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => max;
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            int n;
            try { n = inner.Read(buffer); }
            catch (Exception ex) when (ex is not (ArchivePasswordException or ArchiveException) && IsPasswordProblem(ex)) { throw new ArchivePasswordException(true); }
            catch (Exception ex) when (ex is not (ArchiveException or ArchivePasswordException or OperationCanceledException or BlobUnavailableException))
            {
                throw new ArchiveException($"„{name}“ lässt sich nicht auspacken – das Archiv ist beschädigt.");
            }
            _read += n;
            if (_read > max) throw new ArchiveException($"„{name}“ ist ausgepackt größer als im Archiv angegeben – vermutlich eine ZIP-Bombe. Abgebrochen.");
            return n;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => Task.FromResult(Read(buffer.AsSpan(offset, count)));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => ValueTask.FromResult(Read(buffer.Span));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                foreach (var o in owners) o.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    // ---------------- Passwörter (nur im Speicher, je Benutzer und Archiv) ----------------

    private static readonly ConcurrentDictionary<string, (string Password, DateTime Until)> Passwords = new();

    private static string PasswordKey(string userId, UserDb owner, string path, string etag) => userId + "|" + owner.UserId + "|" + path + "|" + etag;

    /// <summary>Nach dem Entsperren eine halbe Stunde gemerkt (jeder Zugriff verlängert) - nie in eine Adresse oder auf die Platte.</summary>
    public static void Remember(string userId, UserDb owner, string path, string etag, string password)
    {
        Passwords[PasswordKey(userId, owner, path, etag)] = (password, DateTime.UtcNow.AddMinutes(30));
        foreach (var old in Passwords.Where(x => x.Value.Until < DateTime.UtcNow).Select(x => x.Key).ToList()) Passwords.TryRemove(old, out _);
    }

    public static string? PasswordOf(string userId, UserDb owner, string path, string etag)
    {
        var key = PasswordKey(userId, owner, path, etag);
        if (!Passwords.TryGetValue(key, out var p)) return null;
        if (p.Until < DateTime.UtcNow) { Passwords.TryRemove(key, out _); return null; }
        Passwords[key] = (p.Password, DateTime.UtcNow.AddMinutes(30));
        return p.Password;
    }

    public static void Forget(string userId, UserDb owner, string path, string etag) => Passwords.TryRemove(PasswordKey(userId, owner, path, etag), out _);

    /// <summary>Passwort prüfen: Verzeichnis lesen und den kleinsten verschlüsselten Eintrag ein Stück weit auspacken.</summary>
    public static void CheckPassword(UserDb db, string path, string etag, string password)
    {
        var listing = List(db, path, etag, password);
        if (listing.Entries.Where(e => e is { IsDir: false, Encrypted: true }).MinBy(e => e.Size) is not { } probe) return;
        using var s = OpenEntry(db, path, listing, probe.Path, password);
        var buffer = new byte[Math.Min(64 * 1024, Math.Max(1, probe.Size))];
        while (s.Read(buffer) > 0) { }
    }
}
