using System.Text.RegularExpressions;
using DmOffice.Core;

namespace DmOffice.Mail;

/// <summary>Eigene Textbausteine beim Schreiben von Mails (Kennung „mailsnippets“, 2.2): Titel, optionales Kürzel für „::“
/// und formatierter Text, ohne Platzhalter (die gibt es nur bei den Textbausteinen des Helpdesks). Je Benutzer als JSON
/// unter „mail.snippets“ in der eigenen Datenbank. Ist die Funktion aus, antworten alle Pfade mit 404 (Features.Paths).</summary>
public static partial class MailSnippets
{
    public const int MaxCount = 500, MaxTitle = 200, MaxCode = 40, MaxHtml = 256 * 1024;
    private const string Key = "mail.snippets";

    public sealed class Snippet
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string? Code { get; set; }
        public string Html { get; set; } = "";
        public DateTime Changed { get; set; }
    }

    public record Body(string? Title, string? Code, string? Html);

    public static List<Snippet> All(UserDb db) => db.GetJson<List<Snippet>>(Key);

    /// <summary>Formatierten Text bereinigen wie eine empfangene Mail (keine Skripte, keine Formulare), eingebettete
    /// Bilder (data:) bleiben; Stilblöcke fallen weg, damit sie im Editor nicht die ganze Seite umstellen.</summary>
    public static string Clean(string html) => StyleRx().Replace(MailHtml.Sanitize(html, true, _ => null).Html, "").Trim();

    /// <summary>Eingaben prüfen und übernehmen; Fehlertext oder null.</summary>
    private static string? Apply(Snippet s, Body b)
    {
        var title = (b.Title ?? "").Trim();
        if (title.Length == 0) return "Der Titel fehlt.";
        if (title.Length > MaxTitle) return $"Der Titel ist länger als {MaxTitle} Zeichen.";
        var code = (b.Code ?? "").Trim().ToLowerInvariant();
        if (code.Length > MaxCode) return $"Das Kürzel ist länger als {MaxCode} Zeichen.";
        if (code.Contains(' ')) return "Das Kürzel darf keine Leerzeichen enthalten.";
        var html = b.Html ?? "";
        if (html.Length > MaxHtml) return "Der Text ist zu lang (höchstens 256 KB).";
        html = Clean(html);
        if (html.Length == 0) return "Der Text fehlt.";
        s.Title = title; s.Code = code.Length == 0 ? null : code; s.Html = html; s.Changed = DateTime.UtcNow;
        return null;
    }

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api/mail/snippets").RequireAuthorization();

        api.MapGet("", (HttpContext http) => All(http.Space().Db).OrderBy(s => s.Title, StringComparer.CurrentCultureIgnoreCase));

        api.MapPost("", (Body body, HttpContext http) =>
        {
            var db = http.Space().Db;
            lock (db)
            {
                var all = All(db);
                if (all.Count >= MaxCount) return Results.BadRequest(new { error = $"Höchstens {MaxCount} Textbausteine." });
                var s = new Snippet { Id = Guid.NewGuid().ToString("N")[..12] };
                if (Apply(s, body) is { } err) return Results.BadRequest(new { error = err });
                all.Add(s);
                db.SetJson(Key, all);
                return Results.Ok(s);
            }
        });

        api.MapPut("/{id}", (string id, Body body, HttpContext http) =>
        {
            var db = http.Space().Db;
            lock (db)
            {
                var all = All(db);
                var s = all.FirstOrDefault(x => x.Id == id);
                if (s == null) return Results.NotFound();
                if (Apply(s, body) is { } err) return Results.BadRequest(new { error = err });
                db.SetJson(Key, all);
                return Results.Ok(s);
            }
        });

        api.MapDelete("/{id}", (string id, HttpContext http) =>
        {
            var db = http.Space().Db;
            lock (db)
            {
                var all = All(db);
                if (all.RemoveAll(x => x.Id == id) == 0) return Results.NotFound();
                db.SetJson(Key, all);
                return Results.NoContent();
            }
        });
    }

    [GeneratedRegex(@"<style\b[\s\S]*?</style>", RegexOptions.IgnoreCase)]
    private static partial Regex StyleRx();
}
