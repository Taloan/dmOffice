using System.IO.Compression;
using DmOffice.Core;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

namespace DmOffice.Dav;

/// <summary>Ordner und Auswahl als ZIP - gestreamt: jede Datei kommt direkt aus der Datenbank in die Antwort,
/// nichts wird vorher ganz im Speicher oder auf der Platte zusammengebaut.</summary>
public static class Zip
{
    /// <summary>Schon gepackt - nochmal komprimieren kostet nur Zeit.</summary>
    private static readonly HashSet<string> Packed = [".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".mp4", ".mov", ".m4v", ".webm", ".mkv",
        ".mp3", ".m4a", ".aac", ".ogg", ".opus", ".flac", ".zip", ".7z", ".rar", ".gz", ".bz2", ".xz", ".docx", ".xlsx", ".pptx", ".odt", ".ods", ".odp", ".pdf"];

    /// <param name="baseDir">Namen im ZIP relativ zu diesem Ordner (meist der aktuelle).</param>
    /// <param name="hidden">Auslassen (der geschuetzte notizi-Ordner).</param>
    public static async Task Write(HttpContext http, UserDb db, string baseDir, IEnumerable<string> paths, Func<string, bool> hidden, string fileName,
        Action? counted = null)
    {
        var roots = paths.Select(FileStore.Normalize).Distinct().Where(p => !hidden(p)).Select(p => FileStore.Get(db, p)).OfType<FileEntry>().ToList();
        if (roots.Count == 0) throw new KeyNotFoundException("Nichts zum Herunterladen.");
        http.Response.ContentType = "application/zip";
        http.Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileNameStar = fileName }.ToString();
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        // ZipArchive schreibt synchron; die Antwort kommt trotzdem stueckweise (gepuffert) beim Browser an
        if (http.Features.Get<IHttpBodyControlFeature>() is { } body) body.AllowSynchronousIO = true;
        counted?.Invoke();
        await http.Response.StartAsync();
        await using var buffered = new BufferedStream(http.Response.Body, 256 * 1024);
        using (var zip = new ZipArchive(buffered, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var root in roots)
            {
                var items = root.IsDir ? new[] { root }.Concat(FileStore.Descendants(db, root.Path)) : [root];
                foreach (var f in items)
                {
                    if (hidden(f.Path)) continue;
                    http.RequestAborted.ThrowIfCancellationRequested();
                    var name = baseDir.Length == 0 ? f.Path : f.Path.StartsWith(baseDir + "/", StringComparison.Ordinal) ? f.Path[(baseDir.Length + 1)..] : f.Path;
                    if (f.IsDir) { if (name.Length > 0) zip.CreateEntry(name + "/").LastWriteTime = f.Modified.Year < 1980 ? DateTime.Now : f.Modified.ToLocalTime(); continue; }
                    var entry = zip.CreateEntry(name, Packed.Contains(Path.GetExtension(f.Name).ToLowerInvariant()) ? CompressionLevel.NoCompression : CompressionLevel.Fastest);
                    entry.LastWriteTime = f.Modified.Year < 1980 ? DateTime.Now : f.Modified.ToLocalTime();
                    using var target = entry.Open();
                    using var source = FileStore.OpenRead(db, f.Path);
                    source.CopyTo(target, 256 * 1024);
                }
            }
        }
        await buffered.FlushAsync();
    }
}
