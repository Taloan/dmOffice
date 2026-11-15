using DmOffice.Core;

namespace DmOffice.Mail;

/// <summary>Entfernen nach Zeit (<see cref="MailExpiry"/>): einplanen, zuruecknehmen, auflisten. Ausgefuehrt wird im Mail-Sync.</summary>
public static class MailExpiryApi
{
    public record ExpireBody(string Folder, List<uint> Uids, DateTime At, string Kind);

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api/mail").RequireAuthorization();

        api.MapGet("/expiries", (HttpContext http) =>
            MailExpiry.All(http.Space().Db).Select(e => new { e.Account, e.Folder, e.Uid, e.MessageId, e.At, e.Kind, e.Subject }));

        api.MapPost("/{acc}/expire", async (string acc, ExpireBody body, HttpContext http, MailSync sync) =>
        {
            var space = http.Space();
            var account = space.Account(acc);
            ImapPool.RequireServer(account);
            if (body.Kind is not (MailExpiry.Trash or MailExpiry.Destroy)) return Results.BadRequest(new { error = "Unbekannte Art." });
            if (body.At.ToUniversalTime() <= DateTime.UtcNow.AddMinutes(1)) return Results.BadRequest(new { error = "Der Zeitpunkt liegt in der Vergangenheit." });
            if (body.Kind == MailExpiry.Trash)
            {
                var trash = MailStore.Folders(space.Db, acc).FirstOrDefault(f => f.Special == "trash");
                if (trash == null) return Results.BadRequest(new { error = "Für dieses Konto gibt es keinen Papierkorb." });
                if (trash.Path == body.Folder) return Results.BadRequest(new { error = "Die Mail liegt schon im Papierkorb." });
            }
            var n = await MailExpiry.PlanAsync(space.Db, acc, body.Folder, body.Uids, body.At, body.Kind);
            sync.Touch(space.User.Id);
            return Results.Ok(new { planned = n });
        });

        api.MapPost("/{acc}/expire/cancel", async (string acc, MailApi.UidsBody body, HttpContext http, MailSync sync) =>
        {
            var space = http.Space();
            space.Account(acc);
            var n = await MailExpiry.CancelAsync(space.Db, acc, body.Folder, body.Uids);
            sync.Touch(space.User.Id);
            return Results.Ok(new { cancelled = n });
        });
    }
}
