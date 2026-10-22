using System.Net;
using DmOffice.Core;
using DmOffice.Dav;

namespace DmOffice.Mail;

/// <summary>
/// Anhaenge als Freigabelink statt in der Mail (wie in Outlook): Dateien aus der Dateiablage bekommen einen Link, Hochgeladenes
/// landet erst in "E-Mail-Anhänge/&lt;Zeit&gt;" und bekommt dann einen. Ablauf und Abrufe folgen den Vorgaben des Benutzers unter
/// Einstellungen -> Dateien -> Freigabelinks (begrenzt durch die Verwaltung) - gefragt wird nichts.
/// </summary>
public static class MailLinks
{
    public const string Folder = "E-Mail-Anhänge";

    /// <summary>Legt die Links an und gibt den HTML-Abschnitt fuer die Mail zurueck (leer, wenn nichts als Link gehen soll).
    /// Hochgeladene Dateien, die als Link gehen, nimmt es aus <paramref name="files"/> heraus.</summary>
    public static async Task<string> CreateAsync(HttpContext http, UserSpace space, MailAccount account, MailCompose.Draft d, List<IFormFile> files, CancellationToken ct)
    {
        var uploads = files.Where(f => d.LinkUploads.Contains(f.FileName)).ToList();
        if (d.LinkPaths.Count == 0 && uploads.Count == 0) return "";
        var db = space.Db;
        var entries = new List<FileEntry>();

        foreach (var raw in d.LinkPaths.Distinct())
        {
            var path = FileStore.Normalize(raw);
            if (space.User.FilesOff && !FileStore.InRoot(path, space.Settings.Notizi.Root)) throw new ArgumentException("Die Dateiablage ist abgeschaltet.");
            entries.Add(FileStore.Get(db, path) ?? throw new ArgumentException($"Datei nicht gefunden: {raw}"));
        }
        if (uploads.Count > 0)
        {
            if (space.User.FilesOff) throw new ArgumentException("Die Dateiablage ist abgeschaltet - Anhänge lassen sich nicht als Link senden.");
            var dir = Folder + "/" + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss");
            FileStore.EnsureDir(db, dir);
            foreach (var file in uploads)
            {
                using var ms = new MemoryStream();
                await file.CopyToAsync(ms, ct);
                var path = dir + "/" + FileStore.Normalize(Path.GetFileName(file.FileName));
                FileStore.Put(db, path, ms.ToArray(), file.ContentType);
                entries.Add(FileStore.Get(db, path)!);
                files.Remove(file);
            }
        }

        // Vorgaben des Benutzers fuer neue Links (schon durch die Hoechstwerte der Verwaltung begrenzt)
        var s = FileRetention.Load(db);
        DateTime? expires = s.LinkDays > 0 ? DateTime.UtcNow.AddDays(s.LinkDays) : null;
        int? maxDownloads = s.LinkMaxDownloads > 0 ? s.LinkMaxDownloads : null;
        var index = http.RequestServices.GetRequiredService<LinkIndex>();
        var origin = $"{http.Request.Scheme}://{http.Request.Host}";

        var english = account.Language == "en";   // Sprache der Mails des Kontos
        var html = new System.Text.StringBuilder("<div style=\"margin-top:16px\"><p>")
            .Append(english ? "Files to download:" : "Dateien zum Herunterladen:").Append("</p><ul>");
        foreach (var e in entries)
        {
            var link = Links.Create(db, e.FileId, "read", expires, null, maxDownloads);
            index.Add(space.User.Id, link.Id, link.Token);
            html.Append("<li><a href=\"").Append(WebUtility.HtmlEncode(origin + "/s/" + link.Token)).Append("\">").Append(WebUtility.HtmlEncode(e.Name)).Append("</a>");
            if (!e.IsDir) html.Append(" (").Append(Size(e.Size)).Append(')');
            html.Append("</li>");
        }
        html.Append("</ul>");
        var limits = new List<string>();
        if (expires is { } x) limits.Add(english ? $"until {x.ToLocalTime():yyyy-MM-dd}" : $"bis {x.ToLocalTime():dd.MM.yyyy}");
        if (maxDownloads is { } m) limits.Add(english ? $"for {m} downloads" : $"für {m} Abrufe");
        if (limits.Count > 0)
            html.Append("<p style=\"color:#666;font-size:12px\">").Append(english ? "The links are valid " : "Die Links gelten ").Append(string.Join(english ? " and " : " und ", limits)).Append(".</p>");
        return html.Append("</div>").ToString();
    }

    private static string Size(long bytes) =>
        bytes < 1024 * 1024 ? $"{Math.Max(1, bytes / 1024)} KB" : bytes < 1024L * 1024 * 1024 ? $"{bytes / 1024.0 / 1024:0.#} MB" : $"{bytes / 1024.0 / 1024 / 1024:0.#} GB";
}
