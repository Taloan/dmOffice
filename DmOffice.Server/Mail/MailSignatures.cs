using DmOffice.Core;

namespace DmOffice.Mail;

/// <summary>
/// Signaturen (Kennung mailsignatures, Konzept docs/catchall-domains-konzept.md): mehrere Signaturen mit Namen, je zugeordnet zu
/// Mailkonten, Catchall-Domains oder einzelnen Aliasen. Beim Verfassen gilt: Alias vor Domain vor Konto; ohne Zuordnung die
/// Signatur aus den Einstellungen des Mailkontos (wie bisher). Im kv „mail.signatures“.
/// </summary>
public static class MailSignatures
{
    public const int MaxCount = 200, MaxName = 100, MaxHtml = 64 * 1024;
    private const string Key = "mail.signatures";

    public sealed class Signature
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Html { get; set; } = "";
        /// <summary>Mailkonten (MailAccount.Id), für die sie gilt.</summary>
        public List<string> Accounts { get; set; } = [];
        /// <summary>Catchall-Domains (klein), für deren Aliase sie gilt.</summary>
        public List<string> Domains { get; set; } = [];
        /// <summary>Einzelne Aliase (klein).</summary>
        public List<string> Aliases { get; set; } = [];
        public DateTime Changed { get; set; }
    }

    public record Body(string? Name, string? Html, List<string>? Accounts, List<string>? Domains, List<string>? Aliases);

    public static List<Signature> All(UserDb db) => db.GetJson<List<Signature>>(Key);

    /// <summary>Welche Signatur gilt für diesen Absender? Alias vor Domain vor Konto; null: keine zugeordnet.</summary>
    /// <param name="account">Mailkonto, aus dem gesendet wird</param>
    /// <param name="from">Absender, wenn nicht die Adresse des Kontos (Alias), sonst null</param>
    public static Signature? Resolve(IReadOnlyList<Signature> all, string account, string? from)
    {
        var a = from?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(a))
        {
            if (all.FirstOrDefault(s => s.Aliases.Contains(a)) is { } byAlias) return byAlias;
            var domain = a[(a.LastIndexOf('@') + 1)..];
            if (all.FirstOrDefault(s => s.Domains.Contains(domain)) is { } byDomain) return byDomain;
        }
        return all.FirstOrDefault(s => s.Accounts.Contains(account));
    }

    private static List<string> Lower(IEnumerable<string>? list) =>
        (list ?? []).Select(x => x.Trim().TrimStart('@').ToLowerInvariant()).Where(x => x.Length > 0).Distinct().ToList();

    private static string? Apply(List<Signature> all, Signature s, Body b)
    {
        var name = (b.Name ?? "").Trim();
        if (name.Length == 0) return "Der Name fehlt.";
        if (name.Length > MaxName) return $"Der Name ist länger als {MaxName} Zeichen.";
        if ((b.Html ?? "").Length > MaxHtml) return "Die Signatur ist zu lang (höchstens 64 KB).";
        s.Name = name;
        s.Html = MailHtml.Sanitize(b.Html ?? "", true, _ => null).Html.Trim();
        s.Accounts = (b.Accounts ?? []).Where(x => x.Length > 0).Distinct().ToList();
        s.Domains = Lower(b.Domains);
        s.Aliases = Lower(b.Aliases);
        s.Changed = DateTime.UtcNow;
        // Jede Zuordnung gehört zu genau einer Signatur: die neue nimmt sie den anderen weg
        foreach (var o in all.Where(o => o.Id != s.Id))
        {
            o.Accounts.RemoveAll(s.Accounts.Contains);
            o.Domains.RemoveAll(s.Domains.Contains);
            o.Aliases.RemoveAll(s.Aliases.Contains);
        }
        return null;
    }

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api/mail/signatures").RequireAuthorization();

        api.MapGet("", (HttpContext http) => All(http.Space().Db).OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase));

        api.MapPost("", (Body body, HttpContext http) =>
        {
            var db = http.Space().Db;
            lock (db)
            {
                var all = All(db);
                if (all.Count >= MaxCount) return Results.BadRequest(new { error = $"Höchstens {MaxCount} Signaturen." });
                var s = new Signature { Id = Guid.NewGuid().ToString("N")[..12] };
                if (Apply(all, s, body) is { } err) return Results.BadRequest(new { error = err });
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
                if (all.FirstOrDefault(x => x.Id == id) is not { } s) return Results.NotFound();
                if (Apply(all, s, body) is { } err) return Results.BadRequest(new { error = err });
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

        // Für App und Verfassen: welche Signatur gilt (Alias vor Domain vor Konto, sonst die des Kontos)
        api.MapGet("/for", (string account, string? from, HttpContext http) =>
        {
            var space = http.Space();
            if (Resolve(All(space.Db), account, from) is { } s) return Results.Ok(new { id = s.Id, name = s.Name, html = s.Html });
            var acc = space.Settings.MailAccounts.FirstOrDefault(a => a.Id == account);
            return Results.Ok(new { id = (string?)null, name = (string?)null, html = acc?.Signature ?? "" });
        });
    }
}
