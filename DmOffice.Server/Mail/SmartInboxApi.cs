using DmOffice.Core;

namespace DmOffice.Mail;

/// <summary>Smart Inbox: Einstellungen (Hauptschalter, Relevant/Sonstige, Termine aus Mails, lernende Kategorien, Konten).
/// Die Kategorien selbst stehen unter /api/settings/categories, das Etikett setzt /api/mail/{acc}/category.</summary>
public static class SmartInboxApi
{
    public record AccountOn(string Id, bool On);
    public record SettingsBody(bool? Enabled, bool? Focus, bool? Calendar, bool? Categories, List<AccountOn>? Accounts);

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api/mail").RequireAuthorization();

        api.MapGet("/smart/settings", (HttpContext http) => Results.Ok(State(http.Space())));

        api.MapPut("/smart/settings", (SettingsBody body, HttpContext http) =>
        {
            var space = http.Space();
            space.UpdateSettings(s =>
            {
                if (body.Enabled is bool e) s.SmartInbox = e;
                if (body.Focus is bool f) s.FocusedInbox = f;
                if (body.Calendar is bool c) s.SmartCalendar = c;
                if (body.Categories is bool k) s.SmartCategories = k;
                foreach (var a in body.Accounts ?? [])
                    if (s.MailAccounts.FirstOrDefault(x => x.Id == a.Id) is { } acc) acc.FocusedInbox = a.On;
            });
            return Results.Ok(State(space));
        });

        // Frühere Smart-Kategorien des Posteingangs: gibt es nicht mehr (ältere Apps blenden sie damit aus)
        api.MapGet("/{acc}/smart-inbox", () => Results.Ok(new { enabled = false, categories = Array.Empty<object>() }));
    }

    private static object State(UserSpace space)
    {
        var s = space.Settings;
        return new
        {
            enabled = s.SmartInbox,
            focus = s.FocusedInbox,
            calendar = s.SmartCalendar,
            categories = s.SmartCategories,
            accounts = s.MailAccounts.Select(a => new { id = a.Id, name = a.Name, email = a.Email, on = a.FocusedInbox }),
        };
    }
}
