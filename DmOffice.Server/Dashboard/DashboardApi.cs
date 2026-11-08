using System.Text.Json;
using DmOffice.Chat;
using DmOffice.Core;

namespace DmOffice.Dashboard;

/// <summary>
/// Übersicht (Dashboard, Version 2): Zusammenstellung lesen und speichern, Hintergrundbild, Daten je Widget.
/// Aktionen (abhaken, antworten, anlegen) laufen über die Endpunkte der Bereiche selbst - die Oberflächen kennen sie schon.
/// </summary>
public static class DashboardApi
{
    public record TaskDoneBody(long List, string Uri, bool Done);

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api/dashboard").RequireAuthorization();

        // device: „desktop“ (Vorgabe) oder „phone“ - je Geräteart eine eigene Zusammenstellung
        api.MapGet("/", (string? device, HttpContext http) => DashboardStore.Visible(DashboardStore.Of(http.Space().Db, DashboardStore.Device(device))));
        api.MapPut("/", (Layout body, string? device, HttpContext http) =>
        {
            var d = DashboardStore.Device(device);
            DashboardStore.Save(http.Space().Db, body, d);
            return Results.Ok(DashboardStore.Visible(DashboardStore.Of(http.Space().Db, d)));
        });
        api.MapDelete("/", (string? device, HttpContext http) =>
        {
            var d = DashboardStore.Device(device);
            DashboardStore.Reset(http.Space().Db, d);
            return Results.Ok(DashboardStore.Visible(DashboardStore.Of(http.Space().Db, d)));
        });
        api.MapGet("/types", () => DashboardStore.Types.Where(DashboardStore.Available));

        // Hintergrundbild: hochladen (verkleinert gespeichert), abrufen, entfernen
        api.MapPut("/background", async (HttpRequest request, string? device, HttpContext http, CancellationToken ct) =>
        {
            var d = DashboardStore.Device(device);
            using var m = new MemoryStream();
            await request.Body.CopyToAsync(m, ct);
            if (m.Length > 25_000_000) return Results.BadRequest(new { error = "Das Bild ist zu groß (höchstens 25 MB)." });
            if (m.Length == 0) return Results.BadRequest(new { error = "Kein Bild." });
            DashboardStore.StoreImage(http.Space().Db, m.ToArray(), d);
            var l = DashboardStore.Of(http.Space().Db, d);
            l.Background.Kind = "upload"; l.Background.Path = null;
            DashboardStore.Save(http.Space().Db, l, d);
            return Results.Ok(l);
        }).DisableAntiforgery();
        api.MapGet("/background", (string? device, HttpContext http) =>
        {
            var img = DashboardStore.Image(http.Space().Db, DashboardStore.Device(device));
            if (img == null) return Results.NotFound();
            http.Response.Headers.CacheControl = "private, max-age=3600";
            return Results.File(img, "image/webp");
        });
        api.MapDelete("/background", (string? device, HttpContext http) =>
        {
            var d = DashboardStore.Device(device);
            DashboardStore.RemoveImage(http.Space().Db, d);
            var l = DashboardStore.Of(http.Space().Db, d);
            if (l.Background.Kind == "upload") { l.Background.Kind = "none"; DashboardStore.Save(http.Space().Db, l, d); }
            return Results.Ok(l);
        });

        // Aufgabe oder Einkaufsposten abhaken (und zurück) - aus Web und App gleich, ohne die ganze Aufgabe mitzuschicken
        api.MapPost("/task", (TaskDoneBody body, HttpContext http) =>
        {
            var r = Pim.SharedLists.Resolve(http, body.List).Writable();
            var t = Pim.CalendarModel.Tasks(r.Space, [r.Collection]).FirstOrDefault(x => x.Uri == body.Uri) ?? throw new KeyNotFoundException("Diese Aufgabe gibt es nicht mehr.");
            Pim.CalendarModel.SaveTask(r.Space, new Pim.TaskInput
            {
                List = r.Collection, Uri = t.Uri, Etag = t.Etag, Title = t.Title, Description = t.Description, Due = t.Due, DueAllDay = t.DueAllDay, Priority = t.Priority,
                Completed = body.Done, Percent = body.Done ? 100 : 0, Categories = t.Categories,
            });
            return Results.Ok(new { done = body.Done });
        });

        // Daten eines Widgets - Einstellungen als JSON im Parameter o (wie gespeichert), damit Web und App dasselbe schicken
        api.MapGet("/w/{type}", async (string type, string? o, HttpContext http, ServerConfig config, ChatStore chat, UserStore users, S3Mirror mirror, S3Backup backup,
            IHttpClientFactory f, CancellationToken ct) =>
        {
            Dictionary<string, JsonElement>? opts = null;
            if (!string.IsNullOrWhiteSpace(o))
                try { opts = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(o); } catch (JsonException) { return Results.BadRequest(new { error = "Ungültige Einstellungen." }); }
            var op = new DashboardData.Opts(opts);
            // Release-Liste: Widget-Art in dieser Version nicht freigeschaltet
            if (!DashboardStore.Available(type)) return Results.NotFound();
            object data = type switch
            {
                "today" => await DashboardData.TodayAsync(http, f, ct),
                "events" => DashboardData.Events(http, op),
                "meals" => DashboardData.Meals(http, op),
                "tasks" => DashboardData.Tasks(http, op),
                "shopping" => DashboardData.Shopping(http, op),
                "mail" => DashboardData.Mail(http, op),
                "chat" => DashboardData.Chat(http, chat, users, op),
                "pukki" => DashboardData.PukkiText(http, chat, users),
                "weather" => await DashboardData.WeatherAsync(http, f, ct),
                "news" => await DashboardData.NewsAsync(http, f, op, ct),
                "journal" => DashboardData.Journal(http, op),
                "photos" => await DashboardData.PhotosAsync(http, config, op),
                "notes" => DashboardData.Notes(http, op),
                "files" => DashboardData.Files(http, op),
                "birthdays" => DashboardData.Birthdays(http, users, op),
                "countdown" => DashboardData.Countdown(http, op),
                "storage" => DashboardData.Storage(http),
                // Coach: der heutige Tag wie in der Tagesansicht (Waage, Ring, Schritte bzw. Wasser)
                "coach" or "water" => DmOffice.Coach.CoachApi.Day(http.Space(), DmOffice.Coach.CoachStore.Today(http.Space())),
                "system" => http.User.IsInRole("admin") ? DashboardData.System(http, config, mirror, backup) : throw new UnauthorizedAccessException("Nur für Administratoren."),
                _ => throw new KeyNotFoundException("Unbekanntes Widget."),
            };
            http.Response.Headers.CacheControl = "private, no-cache";
            return Results.Ok(data);
        });
    }
}
