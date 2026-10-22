using System.Security.Claims;
using DmOffice.Core;

namespace DmOffice;

/// <summary>
/// Migrations-Assistent: was an Datenaenderungen ansteht oder laeuft (Schemaschritte, Auslagerung der Dateiinhalte), wie viel
/// Platz sie brauchen und ob er reicht, die Sicherungen vor dem Umbau. GET /api/update/state kennt jeder angemeldete Benutzer
/// (die Anmeldung braucht seine Datenbank nicht - so kommt er auch dann an diese Auskunft, wenn die Datenbank noch wartet);
/// bedienen darf nur die Verwaltung (/api/admin/update).
/// </summary>
public static class UpdateApi
{
    public record StartBody(bool WithoutBackup = false, bool ConfirmWithoutBackup = false);
    public record PauseBody(bool Paused);
    public record SettingsBody(int KeepDays);

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/update/state", (HttpContext http, UpdateCenter update, UserStore users) =>
        {
            var user = users.ById(http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "") ?? throw new UnauthorizedAccessException();
            var plan = update.Plan(user);
            return Results.Ok(new
            {
                isAdmin = user.IsAdmin,
                // Seine Datenbank wartet: die Oberflaeche zeigt dann nur den Assistenten statt der Programme
                mine = new { pending = plan.Pending, plan.State, plan.From, plan.To, plan.MissingBytes, plan.BackupMissingBytes, plan.NeedsConsent },
                attention = user.IsAdmin && update.AnyAttention(),
            });
        }).RequireAuthorization();

        var api = app.MapGroup("/api/admin/update").RequireAuthorization("admin");

        api.MapGet("/", (UpdateCenter update) => Results.Ok(update.Describe()));

        // "Jetzt starten": grosse Umbauten freigeben. Ohne Sicherung nur mit ausdruecklicher Bestaetigung.
        api.MapPost("/start", (StartBody body, UpdateCenter update) =>
        {
            if (body.WithoutBackup && !body.ConfirmWithoutBackup)
                return Results.BadRequest(new { error = "Ohne Sicherung fortfahren muss ausdrücklich bestätigt werden." });
            update.Start(body.WithoutBackup);
            return Results.Ok(update.Describe());
        });

        api.MapPost("/pause", (PauseBody body, UpdateCenter update) =>
        {
            update.SetPaused(body.Paused);
            return Results.Ok(update.Describe());
        });

        // Platz wurde frei, Ordner geleert: erneut pruefen
        api.MapPost("/recheck", (UpdateCenter update) =>
        {
            update.Start(withoutBackup: false, approve: false);
            return Results.Ok(update.Describe());
        });

        api.MapPut("/settings", (SettingsBody body, UpdateCenter update) =>
        {
            update.SetKeepDays(body.KeepDays);
            return Results.Ok(update.Describe());
        });

        api.MapDelete("/backups/{name}", (string name, DataPaths paths, UpdateCenter update) =>
            PreUpdateBackup.Delete(paths, name) ? Results.Ok(update.Describe()) : Results.NotFound(new { error = "Sicherung nicht gefunden." }));
    }
}
