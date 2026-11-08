using DmOffice.Chat;
using DmOffice.Mail;
using MimeKit;

namespace DmOffice.Core;

/// <summary>
/// Tresor-Konzept Stufe 1 (docs/tresor-konzept.md): Ändert die Verwaltung etwas an der Sicherheit eines Kontos (Passwort gesetzt,
/// Zwei-Faktor aufgehoben, Verwaltungsrecht), erfährt die Person es sofort - von Pukki im Chat (also auch per Push
/// aufs Handy) und per Mail an sich selbst über ihr erstes Mailkonto. Alles steht im Sicherheitsprotokoll des Kontos (Einstellungen →
/// Sicherheit) und im Server-Protokoll. Eigene Änderungen stehen nur im Protokoll.
/// </summary>
public static class SecurityNotice
{
    public sealed record Entry(DateTime At, string What, string? By);

    private const string Key = "security.log";
    private const int Keep = 100;

    public static List<Entry> Of(UserDb db) => db.GetJson<List<Entry>>(Key) ?? [];

    /// <summary>Eigene Änderung (Passwort, Zwei-Faktor): nur ins Protokoll.</summary>
    public static void Own(IServiceProvider services, string userId, string what) => Write(services, userId, what, null);

    /// <summary>Die Verwaltung hat etwas geändert: Protokoll, Server-Protokoll, Pukki und Mail.</summary>
    public static void ByAdmin(IServiceProvider services, string userId, string adminId, string what)
    {
        var users = services.GetRequiredService<UserStore>();
        if (users.ById(userId) is not { } u) return;
        // Selbst in der Verwaltung am eigenen Konto: keine Nachricht an sich selbst
        if (adminId == userId) { Write(services, userId, what, null); return; }
        var admin = users.ById(adminId);
        var by = admin == null ? "?" : string.IsNullOrWhiteSpace(admin.DisplayName) ? admin.Login : admin.DisplayName;
        Write(services, userId, what, by);
        var log = services.GetRequiredService<ILoggerFactory>().CreateLogger("DmOffice.Security");
        log.LogWarning("Sicherheit: {What} für {User} durch {Admin}", what, u.Login, admin?.Login ?? adminId);

        var text = $"🔐 {by} hat in der Verwaltung an deinem Konto etwas geändert: {what}. " +
                   "Warst du das nicht selbst oder hattest du nicht darum gebeten, wende dich bitte an deinen Administrator. Das Sicherheitsprotokoll steht unter Einstellungen → Sicherheit.";
        try
        {
            var chat = services.GetRequiredService<ChatStore>();
            Pukki.Ensure(chat, userId);
            var room = Pukki.RoomOf(chat, userId) ?? chat.OpenDirect(userId, Pukki.Id);
            chat.Post(Pukki.Id, room, text);
        }
        catch (Exception e) when (e is not OutOfMemoryException) { log.LogWarning("Sicherheitsnachricht an {User} (Chat): {Error}", u.Login, e.Message); }
        try { MailToSelf(services, userId, what, by); }
        catch (Exception e) when (e is not OutOfMemoryException) { log.LogWarning("Sicherheitsnachricht an {User} (Mail): {Error}", u.Login, e.Message); }
    }

    /// <summary>
    /// Eigene Änderung, die ein Angreifer mit einer übernommenen Sitzung nutzen würde (neuer Zugang, Zwei-Faktor, Passwort):
    /// Protokoll, Pukki und Mail an sich selbst - so fällt sie auf, auch wenn man sie nicht selbst gemacht hat.
    /// </summary>
    /// <param name="login">Neue Anmeldung statt Änderung: anderer Wortlaut in Chat und Mail.</param>
    public static void Important(IServiceProvider services, string userId, string what, bool login = false)
    {
        Write(services, userId, what, null);
        var users = services.GetRequiredService<UserStore>();
        if (users.ById(userId) is not { } u) return;
        var log = services.GetRequiredService<ILoggerFactory>().CreateLogger("DmOffice.Security");
        log.LogInformation("Sicherheit: {What} für {User}", what, u.Login);
        var text = (login ? $"🔐 Neue Anmeldung an deinem Konto: {what}." : $"🔐 An deinem Konto wurde gerade geändert: {what}.")
            + " Warst du das nicht, bitte sofort das Passwort ändern und unter Einstellungen → Sicherheit sowie Einstellungen → Geräte & Sync prüfen.";
        try
        {
            var chat = services.GetRequiredService<ChatStore>();
            Pukki.Ensure(chat, userId);
            var room = Pukki.RoomOf(chat, userId) ?? chat.OpenDirect(userId, Pukki.Id);
            chat.Post(Pukki.Id, room, text);
        }
        catch (Exception e) when (e is not OutOfMemoryException) { log.LogWarning("Sicherheitsnachricht an {User} (Chat): {Error}", u.Login, e.Message); }
        try
        {
            var space = services.GetRequiredService<Spaces>().Get(userId);
            var account = space.Settings.MailAccounts.FirstOrDefault(a => a.Email.Length > 0 && a.SmtpHost.Length > 0);
            if (account == null) return;
            var en = account.Language == "en";
            var m = new MimeMessage();
            var me = new MailboxAddress(string.IsNullOrEmpty(account.DisplayName) ? null : account.DisplayName, account.Email);
            m.From.Add(me);
            m.To.Add(me);
            m.Subject = login
                ? (en ? "dmOffice: new sign-in to your account" : "dmOffice: neue Anmeldung an deinem Konto")
                : (en ? "dmOffice: security change to your account" : "dmOffice: Sicherheitsänderung an deinem Konto");
            m.Body = new TextPart("plain")
            {
                Text = en
                    ? (login ? $"New sign-in to your dmOffice account: {what}" : $"Your dmOffice account was just changed: {what}") + "\n\nIf this was not you, change your password right away and check Settings → Security and Settings → Devices & sync.\n\n" +
                      "This mail was sent by dmOffice from your own mail account to yourself."
                    : (login ? $"Neue Anmeldung an deinem dmOffice-Konto: {what}" : $"An deinem dmOffice-Konto wurde gerade geändert: {what}") + "\n\nWarst du das nicht, bitte sofort das Passwort ändern und unter Einstellungen → Sicherheit sowie Einstellungen → Geräte & Sync prüfen.\n\n" +
                      "Diese Mail hat dmOffice über dein eigenes Mailkonto an dich selbst geschickt.",
            };
            MailQueue.Add(space.Db, account.Id, account.Id, new MailCompose.Draft { To = account.Email, Subject = m.Subject }, m, DateTime.UtcNow);
        }
        catch (Exception e) when (e is not OutOfMemoryException) { log.LogWarning("Sicherheitsnachricht an {User} (Mail): {Error}", u.Login, e.Message); }
    }

    private static void Write(IServiceProvider services, string userId, string what, string? by)
    {
        try
        {
            var db = services.GetRequiredService<Spaces>().Get(userId).Db;
            var list = Of(db);
            list.Insert(0, new Entry(DateTime.UtcNow, what, by));
            if (list.Count > Keep) list.RemoveRange(Keep, list.Count - Keep);
            db.SetJson(Key, list);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            services.GetRequiredService<ILoggerFactory>().CreateLogger("DmOffice.Security").LogWarning("Sicherheitsprotokoll für {User}: {Error}", userId, e.Message);
        }
    }

    /// <summary>Mail an sich selbst über das erste Mailkonto mit Versand (Postausgang). Ohne Mailkonto: nur Chat.</summary>
    private static void MailToSelf(IServiceProvider services, string userId, string what, string by)
    {
        var space = services.GetRequiredService<Spaces>().Get(userId);
        var account = space.Settings.MailAccounts.FirstOrDefault(a => a.Email.Length > 0 && a.SmtpHost.Length > 0);
        if (account == null) return;
        var en = account.Language == "en";
        var m = new MimeMessage();
        var me = new MailboxAddress(string.IsNullOrEmpty(account.DisplayName) ? null : account.DisplayName, account.Email);
        m.From.Add(me);
        m.To.Add(me);
        m.Subject = en ? "dmOffice: security change to your account" : "dmOffice: Sicherheitsänderung an deinem Konto";
        m.Body = new TextPart("plain")
        {
            Text = en
                ? $"{by} changed something about the security of your dmOffice account in the administration: {what}\n\n" +
                  "If you did not ask for this, please talk to the administrator. The security log is under Settings → Security.\n\n" +
                  "This mail was sent by dmOffice from your own mail account to yourself."
                : $"{by} hat in der Verwaltung an der Sicherheit deines dmOffice-Kontos etwas geändert: {what}\n\n" +
                  "Hattest du nicht darum gebeten, wende dich bitte an deinen Administrator. Das Sicherheitsprotokoll steht unter Einstellungen → Sicherheit.\n\n" +
                  "Diese Mail hat dmOffice über dein eigenes Mailkonto an dich selbst geschickt.",
        };
        MailQueue.Add(space.Db, account.Id, account.Id, new MailCompose.Draft { To = account.Email, Subject = m.Subject }, m, DateTime.UtcNow);
    }
}
