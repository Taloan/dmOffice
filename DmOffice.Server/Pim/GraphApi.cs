using DmOffice.Core;
using DmOffice.Mail;

namespace DmOffice.Pim;

/// <summary>Einstellungen → E-Mail-Konten → Microsoft-Konto: Kalender, Kontakte und Aufgaben verbinden.</summary>
public static class GraphApi
{
    public record ConnectBody(string Flow);
    public record AreasBody(bool? Calendars, bool? Contacts, bool? Tasks);

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api/settings/accounts/{id}/graph").RequireAuthorization();

        static MailAccount Account(UserSpace space, string id) =>
            space.Settings.MailAccounts.FirstOrDefault(a => a.Id == id && a.Auth == MailAuth.Microsoft)
            ?? throw new KeyNotFoundException("Microsoft-Konto nicht gefunden.");

        api.MapGet("", (string id, HttpContext http) =>
        {
            var space = http.Space();
            var a = Account(space, id);
            var links = Graph.All(space.Db).Where(l => l.Account == id).Select(l =>
            {
                var (pending, failing) = Graph.Waiting(space.Db, l.Collection);
                return new
                {
                    l.Collection, name = PimStore.Find(space.Db, l.Collection)?.Name ?? l.RemoteName, l.Kind, l.LastSync, l.LastError,
                    pending, failing, l.Conflicts,
                };
            }).ToList();
            var mail = GraphCategories.StateOf(space.Db, id);
            return Results.Ok(new
            {
                connected = !string.IsNullOrEmpty(a.GraphRefreshToken), calendars = a.GraphCalendars, contacts = a.GraphContacts, tasks = a.GraphTasks, links,
                // Mail-Kategorien gehen bei verbundenem Konto immer ueber Graph
                mail = new { mail.LastSync, mail.LastError },
            });
        });

        // Anmeldung mit den Graph-Scopes per Geraetecode; abgefragt wird wie beim Mailkonto ueber /accounts/microsoft/poll
        api.MapPost("/start", async (string id, HttpContext http, MicrosoftOAuth oauth, CancellationToken ct) =>
        {
            var space = http.Space();
            Account(space, id);
            return oauth.Enabled ? Results.Ok(await oauth.StartAsync(space.User.Id, ct, MicrosoftOAuth.GraphScopes)) : Results.NotFound();
        });

        api.MapPost("/connect", (string id, ConnectBody body, HttpContext http, MicrosoftOAuth oauth) =>
        {
            var space = http.Space();
            Account(space, id);
            if (oauth.GraphGrant(space.User.Id, body.Flow) is not { } token) return Results.BadRequest(new { error = "Erst bei Microsoft anmelden." });
            space.UpdateSettings(s => s.MailAccounts.First(a => a.Id == id).GraphRefreshToken = token);
            oauth.Forget(body.Flow);
            return Results.Ok();
        });

        // Bereiche ein- und ausschalten. Aus: die lokalen Kopien gehen weg, bei Microsoft bleibt alles.
        api.MapPut("", (string id, AreasBody body, HttpContext http, GraphSync sync) =>
        {
            var space = http.Space();
            var a = Account(space, id);
            if (string.IsNullOrEmpty(a.GraphRefreshToken)) return Results.BadRequest(new { error = "Erst verbinden." });
            space.UpdateSettings(s =>
            {
                var x = s.MailAccounts.First(m => m.Id == id);
                if (body.Calendars is bool c) x.GraphCalendars = c;
                if (body.Contacts is bool k) x.GraphContacts = k;
                if (body.Tasks is bool t) x.GraphTasks = t;
            });
            if (body.Calendars == false) Graph.UnlinkAccount(space.Db, id, Graph.Calendar);
            if (body.Contacts == false) Graph.UnlinkAccount(space.Db, id, Graph.Contacts);
            if (body.Tasks == false) Graph.UnlinkAccount(space.Db, id, Graph.Tasks);
            sync.Kick(id);
            return Results.Ok();
        });

        // Gleich abgleichen
        api.MapPost("/sync", (string id, HttpContext http, GraphSync sync) =>
        {
            Account(http.Space(), id);
            sync.Kick(id);
            return Results.Ok();
        });

        // Verbindung trennen: Token weg, lokale Kopien weg
        api.MapDelete("", (string id, HttpContext http) =>
        {
            var space = http.Space();
            Account(space, id);
            Graph.UnlinkAccount(space.Db, id);
            space.Db.Set("mail.graphcat." + id, null);
            space.UpdateSettings(s =>
            {
                var x = s.MailAccounts.First(m => m.Id == id);
                x.GraphRefreshToken = null;
                x.GraphCalendars = x.GraphContacts = x.GraphTasks = false;
            });
            return Results.Ok();
        });
    }
}
