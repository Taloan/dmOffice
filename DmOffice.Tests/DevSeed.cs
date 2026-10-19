using DmOffice.Core;
using DmOffice.Mail;
using MimeKit;

namespace DmOffice.Tests;

/// <summary>
/// Beispielmails fuer die Oberflaeche, ohne IMAP-Server - nur zum Entwickeln.
/// Laeuft mit DMOFFICE_SEED=&lt;Datenverzeichnis&gt; und DMOFFICE_SEED_USER=&lt;login&gt;.
/// </summary>
public class DevSeed
{
    [Fact]
    public void Beispielmails()
    {
        var dir = Environment.GetEnvironmentVariable("DMOFFICE_SEED");
        if (dir == null) return;
        var paths = new DataPaths(dir);
        var users = new UserStore(paths, new Vault(paths, null));
        var user = users.ByLogin(Environment.GetEnvironmentVariable("DMOFFICE_SEED_USER") ?? "dirk")!;
        var db = new UserDb(paths.UserDb(user.Id), users.DbKey(user), user.Id);

        var settings = db.GetJson<UserSettings>("settings");
        var account = settings.MailAccounts.FirstOrDefault(a => a.Name == "Beispiel");
        if (account == null)
        {
            account = new MailAccount { Name = "Beispiel", Email = "dirk@beispiel.de", DisplayName = "Dirk M", ImapHost = "", SmtpHost = "",
                Signature = "<p>Viele Grüße<br>Dirk</p>" };
            settings.MailAccounts.Add(account);
            db.SetJson("settings", settings);
        }

        MailStore.ReplaceFolders(db, account.Id, new()
        {
            ("INBOX", "INBOX", "/", "inbox", true), ("Sent", "Sent", "/", "sent", true), ("Drafts", "Drafts", "/", "drafts", true),
            ("Trash", "Trash", "/", "trash", true), ("Junk", "Junk", "/", "junk", true), ("Archive", "Archive", "/", "archive", true),
            ("Projekte", "Projekte", "/", null, true), ("Projekte/dmOffice", "dmOffice", "/", null, true),
        });

        var samples = new[]
        {
            ("Anna Schmidt", "anna@example.org", "Treffen am Donnerstag", "<p>Hallo Dirk,</p><p>passt dir <b>Donnerstag um 10 Uhr</b>? Ich bringe die Unterlagen mit.</p><p>Liebe Grüße<br>Anna</p>", false, false, 0),
            ("Max Müller", "max@acme.de", "Angebot 2026-117", "<p>Sehr geehrter Herr M.,</p><p>anbei unser Angebot. Bei Fragen melden Sie sich gern.</p><table border=1 cellpadding=4><tr><td>Position</td><td>Preis</td></tr><tr><td>Unraid-Server</td><td>1.299,00 €</td></tr></table><img src=\"https://tracker.example.com/pixel.gif\">", true, true, 1),
            ("GitHub", "noreply@github.com", "[Taloan/dmOffice] Neuer Push auf master", "<p>3 neue Commits auf <code>master</code>.</p>", true, false, 2),
            ("Newsletter", "news@shop.example", "Nur heute: 50 % auf alles!!!", "<h1 style=\"color:red\">SALE</h1><p>Jetzt zugreifen!</p>", true, false, 3),
            ("Mama", "mama@example.net", "Fotos vom Wochenende", "<p>Hier die Bilder 😊</p>", true, false, 6),
            ("Anna Schmidt", "anna@example.org", "Einladung: Projekt-Review dmOffice", "<p>Hallo Dirk, anbei die Einladung zum Review.</p>", false, false, 0),
        };
        uint uid = 1;
        foreach (var (name, addr, subject, html, seen, att, days) in samples)
        {
            var m = new MimeMessage();
            m.From.Add(new MailboxAddress(name, addr));
            m.To.Add(new MailboxAddress("Dirk M", "dirk@beispiel.de"));
            m.Subject = subject;
            m.Date = DateTimeOffset.Now.AddDays(-days).AddHours(-uid);
            m.MessageId = $"seed-{uid}@dmoffice";
            var b = new BodyBuilder { HtmlBody = html };
            if (subject.StartsWith("Einladung"))
            {
                // Einladung (iMIP) zum Ausprobieren von Zusagen/Absagen
                var day = DateTime.Today.AddDays(3);
                var cal = new TextPart("calendar") { Text = string.Join("\r\n", "BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//DevSeed//DE", "METHOD:REQUEST",
                    "BEGIN:VEVENT", "UID:devseed-invite@dmoffice", "SEQUENCE:0", "DTSTAMP:" + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'"),
                    "DTSTART;TZID=Europe/Berlin:" + day.ToString("yyyyMMdd") + "T100000", "DTEND;TZID=Europe/Berlin:" + day.ToString("yyyyMMdd") + "T113000",
                    "SUMMARY:Projekt-Review dmOffice", "LOCATION:Raum 2.14", "ORGANIZER;CN=Anna Schmidt:mailto:anna@example.org",
                    "ATTENDEE;CN=Dirk M;PARTSTAT=NEEDS-ACTION;RSVP=TRUE:mailto:dirk@beispiel.de", "END:VEVENT", "END:VCALENDAR", "") };
                cal.ContentType.Parameters.Add("method", "REQUEST");
                b.Attachments.Add(cal);
            }
            if (att) b.Attachments.Add("Angebot-2026-117.pdf", System.Text.Encoding.ASCII.GetBytes("%PDF-1.4 Beispiel"), new ContentType("application", "pdf"));
            m.Body = b.ToMessageBody();
            using var ms = new MemoryStream();
            m.WriteTo(ms);

            db.Exec(@"INSERT OR REPLACE INTO mail(account,folder,uid,message_id,subject,from_name,from_addr,recipients,date,size,seen,flagged,has_att)
                      VALUES($a,'INBOX',$u,$mid,$s,$fn,$fa,$rc,$d,$sz,$seen,$fl,$att)",
                ("$a", account.Id), ("$u", (long)uid), ("$mid", m.MessageId), ("$s", subject), ("$fn", name), ("$fa", addr),
                ("$rc", "{\"to\":[{\"name\":\"Dirk M\",\"address\":\"dirk@beispiel.de\"}],\"cc\":[]}"),
                ("$d", m.Date.ToUnixTimeMilliseconds()), ("$sz", ms.Length), ("$seen", seen ? 1 : 0), ("$fl", uid == 2 ? 1 : 0), ("$att", att ? 1 : 0));
            var id = Convert.ToInt64(db.Scalar("SELECT id FROM mail WHERE account=$a AND folder='INBOX' AND uid=$u", ("$a", account.Id), ("$u", (long)uid)));
            db.Exec("DELETE FROM mail_fts WHERE rowid=$i", ("$i", id));
            db.Exec("INSERT INTO mail_fts(rowid,subject,sender,recipients,body) VALUES($i,$s,$f,'','')", ("$i", id), ("$s", subject), ("$f", name + " " + addr));
            MailStore.StoreRaw(db, account.Id, "INBOX", uid, ms.ToArray(), m);
            uid++;
        }
        db.Exec("UPDATE mail_folders SET unread=(SELECT COUNT(*) FROM mail WHERE account=$a AND folder='INBOX' AND seen=0), total=6 WHERE account=$a AND path='INBOX'", ("$a", account.Id));
        if (db.Get($"mail.{account.Id}.spamtest") == null)
        {
            db.Exec("UPDATE mail SET spam=0.55 WHERE account=$a AND uid=4", ("$a", account.Id));
            db.Set($"mail.{account.Id}.spamtest", "1");
        }
    }
}
