using System.Text.Json;
using DmOffice.Chat;
using DmOffice.Mail;

namespace DmOffice.Core;

/// <summary>
/// Web Push für Mail und Termine: alle 30 Sekunden je Benutzer mit Abo - neue ungelesene Mail im Posteingang
/// (Absender, Betreff) und fällige Erinnerungen. Es gelten die Schalter unter Einstellungen → Benachrichtigungen
/// („E-Mail“, „Kalender“). Der Chat meldet sich selbst (ChatPush), sofort.
/// </summary>
public sealed class WebPushWatcher(WebPush web, Spaces spaces, IServiceProvider services, ILogger<WebPushWatcher> log) : BackgroundService
{
    private readonly Dictionary<(string User, string Account), uint> _mailSeen = new();
    private readonly Dictionary<string, DateTime> _remindedUntil = new();

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stop);
            while (!stop.IsCancellationRequested)
            {
                foreach (var user in web.Users())
                {
                    try
                    {
                        var space = spaces.Get(user);
                        var (mail, calendar) = Switches(space);
                        if (mail) await NewMail(space, stop);
                        if (calendar) await Reminders(space, stop);
                        else _remindedUntil.Remove(user);
                    }
                    catch (Exception ex) when (!stop.IsCancellationRequested) { log.LogWarning("Web-Push für {User}: {Error}", user, ex.Message); }
                }
                await Task.Delay(TimeSpan.FromSeconds(30), stop);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Die Schalter der Oberfläche (ui.prefs) - ohne Eintrag an.</summary>
    private static (bool Mail, bool Calendar) Switches(UserSpace space)
    {
        try
        {
            using var doc = JsonDocument.Parse(space.Db.Get("ui.prefs") ?? "{}");
            bool On(string k) => !doc.RootElement.TryGetProperty(k, out var v) || v.ValueKind != JsonValueKind.False;
            return (On("notifyMail"), On("notifyCalendar"));
        }
        catch (JsonException) { return (true, true); }
    }

    /// <summary>Neue ungelesene Mail im Posteingang: eine Benachrichtigung je Konto und Runde (die neueste, „+ n weitere“).</summary>
    private async Task NewMail(UserSpace space, CancellationToken ct)
    {
        foreach (var a in space.Settings.MailAccounts)
        {
            var inbox = MailStore.Folders(space.Db, a.Id).FirstOrDefault(f => f.Special == "inbox");
            if (inbox == null) continue;
            var (_, items) = MailStore.List(space.Db, a.Id, inbox.Path, 0, 20, "unread");
            var max = items.Count == 0 ? 0u : items.Max(m => m.Uid);
            var key = (space.User.Id, a.Id);
            // Beim ersten Blick nichts Altes melden
            if (!_mailSeen.TryGetValue(key, out var seen)) { _mailSeen[key] = max; continue; }
            // Kategorien ohne Benachrichtigung (Einstellungen → Kategorien) nicht melden
            var fresh = items.Where(m => m.Uid > seen && !DmOffice.Settings.Categories.Silenced(space.Settings, m.Keywords)).OrderByDescending(m => m.Date).ToList();
            if (max > seen) _mailSeen[key] = max;
            if (fresh.Count == 0) continue;
            var m = fresh[0];
            var from = m.From is { } f ? (string.IsNullOrWhiteSpace(f.Name) ? f.Address : f.Name) : a.Name;
            var body = (string.IsNullOrWhiteSpace(m.Subject) ? "(ohne Betreff)" : m.Subject) + (fresh.Count > 1 ? $" (+ {fresh.Count - 1} weitere)" : "");
            await web.Send(space.User.Id, from ?? "Neue E-Mail", body, "/#/mail", "mail-" + a.Id, ct: ct);
        }
    }

    /// <summary>Erinnerungen, die seit der letzten Runde fällig wurden (eigene und geteilte Kalender).</summary>
    private async Task Reminders(UserSpace space, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var user = space.User.Id;
        if (!_remindedUntil.TryGetValue(user, out var from)) { _remindedUntil[user] = now; return; }
        _remindedUntil[user] = now;
        var today = DateOnly.FromDateTime(Pukki.Now(space));
        foreach (var (title, start, minutes) in PukkiService.DueReminders(services, space, from, now))
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(start, space.Zone);
            var when = PukkiParse.DayText(DateOnly.FromDateTime(local), today) + " " + local.ToString("HH:mm") + (minutes > 0 ? $" (in {PukkiService.Span(minutes)})" : "");
            await web.Send(user, title, when, "/#/calendar", $"reminder-{title}-{start:yyyyMMddHHmm}", urgent: true, ct);
        }
    }
}
