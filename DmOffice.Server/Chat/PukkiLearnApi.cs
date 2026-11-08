using DmOffice.Core;

namespace DmOffice.Chat;

/// <summary>Daumen neben Pukkis Antworten, Lernliste und Vorlieben je Benutzer (Version 2) - bleibt beim Benutzer.</summary>
public static class PukkiLearnApi
{
    public record RateBody(long Message, int Value);
    public record MailBody(bool Allowed);

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api/chat/pukki").RequireAuthorization();
        // Früher konnten Sätze an die Verwaltung gehen - was davon noch gespeichert ist, kommt weg
        app.Services.GetRequiredService<ChatStore>().DeleteKv("pukki.learn.shared");

        // Pukki darf meine Mails lesen (Schalter unter Einstellungen → Chat; eigener Schlüssel, damit ältere Apps ihn nicht zurücksetzen)
        api.MapGet("/mail", (HttpContext http) => new { allowed = Pukki.MailAllowed(http.Space().Db) });
        api.MapPut("/mail", (MailBody body, HttpContext http) =>
        {
            Pukki.SetMailAllowed(http.Space().Db, body.Allowed);
            return Results.Ok(new { allowed = body.Allowed });
        });

        // Morgenüberblick: Wetter und Schlagzeilen (eigene Einstellungen)
        PukkiBriefing.Map(api);

        // Meine Bewertungen (Nachricht → 1 / -1), damit die Daumen nach dem Neuladen gefaerbt sind
        api.MapGet("/ratings", (HttpContext http) => Pukki.Learned(http.Space()).Ratings);

        api.MapPost("/rate", (RateBody body, HttpContext http, ChatStore chat, IServiceProvider services) =>
        {
            if (body.Value is < -1 or > 1) return Results.BadRequest(new { error = "Ungültige Bewertung." });
            var space = http.Space();
            Pukki.Rate(services, chat, space, chat.Message(space.User.Id, body.Message), body.Value);
            return Results.Ok();
        });

        // Lernliste und Vorlieben: sieht nur der Benutzer selbst
        api.MapGet("/learn", (HttpContext http) =>
        {
            var d = Pukki.Learned(http.Space());
            return new
            {
                entries = d.Entries.OrderByDescending(e => e.At),
                prefs = d.Prefs.Where(p => p.Score >= 2).OrderBy(p => p.Word).GroupBy(p => (p.Kind, p.Word))
                    .Select(g => g.OrderByDescending(p => p.Score).First()),
            };
        });
        api.MapDelete("/learn/{message:long}", (long message, HttpContext http) =>
        {
            Pukki.ForgetEntry(http.Space(), message);
            return Results.Ok();
        });
        api.MapDelete("/prefs/{kind}/{word}", (string kind, string word, HttpContext http) =>
        {
            Pukki.ForgetPref(http.Space(), kind, word);
            return Results.Ok();
        });

    }
}
