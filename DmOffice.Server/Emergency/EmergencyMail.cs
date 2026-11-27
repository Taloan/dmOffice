using MailKit;
using System.Net;
using DmOffice.Core;
using DmOffice.Mail;

namespace DmOffice.Emergency;

/// <summary>
/// Mails an Benutzer ohne Konto (Hinweise zum Stand der Anfrage) - über das Standard-Mailkonto des Besitzers,
/// ein anderes Postfach hat dmOffice nicht.
/// </summary>
public static class EmergencyMail
{
    /// <summary>Für Tests: statt zu senden hier ablegen (An, Betreff, Text).</summary>
    public static Action<string, string, string>? Capture;

    /// <summary>Kann der Besitzer Mails senden? Ohne Mailkonto erreicht dmOffice Personen ohne Konto nicht.</summary>
    public static bool CanSend(UserSpace owner) => owner.Settings.DefaultAccount() != null;

    /// <summary>Senden; Rückgabe null oder der Fehler.</summary>
    public static async Task<string?> SendAsync(UserSpace owner, string to, string subject, string text, CancellationToken ct = default)
    {
        if (Capture is { } c) { c(to, subject, text); return null; }
        if (owner.Settings.DefaultAccount() is not { } account) return "Kein Mailkonto zum Senden.";
        var html = "<p>" + string.Join("</p><p>", text.Split("\n\n").Select(p =>
        {
            var e = WebUtility.HtmlEncode(p).Replace("\n", "<br>");
            // Links anklickbar
            return System.Text.RegularExpressions.Regex.Replace(e, @"https?://[^\s<]+", m => $"<a href=\"{m.Value}\">{m.Value}</a>");
        })) + "</p>";
        try
        {
            var m = MailCompose.Build(account, new MailCompose.Draft { To = to, Subject = subject, Html = html }, []);
            // Nicht von Regeln weiterleiten lassen
            m.Headers.Add("X-dmOffice-Forwarded", "1");
            using var smtp = await ImapPool.SmtpAsync(account, ct);
            await smtp.SendAsync(m, ct);
            await smtp.DisconnectAsync(true, ct);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return ex.Message; }
    }

    /// <summary>Fußzeile: woher die Mail kommt.</summary>
    public static string Footer(string owner) =>
        $"Diese Nachricht schickt dmOffice über das Mailkonto von {owner}, weil {owner} dich für einen Notfallzugang eingetragen hat.";
}
