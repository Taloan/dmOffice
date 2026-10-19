using DmOffice.Core;
using DmOffice.Mail;
using MimeKit;

namespace DmOffice.Tests;

/// <summary>
/// Beispielmails fuer die Screenshots der Doku (tools/docs) - erfundene Person, erfundene Firmen.
/// Laeuft nur mit DMOFFICE_DOCS_SEED=&lt;Datenverzeichnis&gt;; der Benutzer "alex" muss angelegt sein.
/// </summary>
public class DocsSeed
{
    private const string Me = "alex@beispiel.de";

    [Fact]
    public void Beispielpostfach()
    {
        var dir = Environment.GetEnvironmentVariable("DMOFFICE_DOCS_SEED");
        if (dir == null) return;
        var paths = new DataPaths(dir);
        var users = new UserStore(paths, new Vault(paths, null));
        var user = users.ByLogin("alex") ?? throw new InvalidOperationException("Erst den Benutzer alex anlegen.");
        var db = new UserDb(paths.UserDb(user.Id), users.DbKey(user), user.Id);

        var settings = db.GetJson<UserSettings>("settings");
        var account = settings.MailAccounts.FirstOrDefault(a => a.Name == "Privat");
        if (account == null)
        {
            account = new MailAccount
            {
                Name = "Privat", Email = Me, DisplayName = "Alex Berger", ImapHost = "", SmtpHost = "",
                Signature = "<p>Viele Grüße<br>Alex</p>"
            };
            settings.MailAccounts.Add(account);
            db.SetJson("settings", settings);
        }

        MailStore.ReplaceFolders(db, account.Id, new()
        {
            ("INBOX", "INBOX", "/", "inbox", true), ("Sent", "Sent", "/", "sent", true), ("Drafts", "Drafts", "/", "drafts", true),
            ("Trash", "Trash", "/", "trash", true), ("Junk", "Junk", "/", "junk", true), ("Archive", "Archive", "/", "archive", true),
            ("Projekte", "Projekte", "/", null, true), ("Projekte/Hausbau", "Hausbau", "/", null, true), ("Verein", "Verein", "/", null, true),
        });

        // (Ordner, Name, Adresse, Betreff, HTML, gelesen, markiert, Anhang, Stunden zurueck, Kategorien)
        var samples = new (string Folder, string Name, string Addr, string Subject, string Html, bool Seen, bool Flag, string? Att, int Hours, string Kw)[]
        {
            ("INBOX", "Jana Wolff", "jana.wolff@example.org", "Grillabend am Samstag?",
                "<p>Hallo Alex,</p><p>wir wollen am <b>Samstag ab 18 Uhr</b> grillen. Hast du Lust? Bring gern Salat mit.</p><p>Liebe Grüße<br>Jana</p>",
                false, false, null, 1, " dm_privat "),
            ("INBOX", "Elektro Sommer GmbH", "info@elektro-sommer.example", "Angebot Nr. 2026-0418 – Wallbox",
                "<p>Sehr geehrter Herr Berger,</p><p>vielen Dank für Ihre Anfrage. Im Anhang finden Sie unser Angebot für die Installation einer Wallbox.</p>" +
                "<table border=1 cellpadding=6 style='border-collapse:collapse'><tr><th>Position</th><th>Preis</th></tr><tr><td>Wallbox 11 kW</td><td>749,00 €</td></tr>" +
                "<tr><td>Installation</td><td>480,00 €</td></tr></table><p>Mit freundlichen Grüßen<br>Elektro Sommer GmbH</p>",
                false, true, "Angebot-2026-0418.pdf", 3, " dm_rechnung dm_wichtig "),
            ("INBOX", "Stadtwerke Musterstadt", "kundenservice@stadtwerke.example", "Ihre Jahresabrechnung 2026",
                "<p>Guten Tag,</p><p>Ihre Jahresabrechnung steht im Kundenportal bereit. Das Guthaben von 84,20 € überweisen wir in den nächsten Tagen.</p>",
                true, false, "Jahresabrechnung-2026.pdf", 20, " dm_rechnung "),
            ("INBOX", "Tom Becker", "tom.becker@example.net", "Fotos vom Ausflug",
                "<p>Hi Alex,</p><p>hier wie versprochen die Fotos vom Ausflug an den See. War ein toller Tag!</p><p>Tom</p>",
                true, false, null, 28, " dm_familie "),
            ("INBOX", "Hausverwaltung Lindenhof", "verwaltung@lindenhof.example", "Ankündigung: Treppenhausreinigung",
                "<p>Sehr geehrte Mieterinnen und Mieter,</p><p>am Dienstag wird das Treppenhaus gereinigt. Bitte stellen Sie keine Schuhe in den Flur.</p>",
                true, false, null, 50, ""),
            ("INBOX", "Laura Neumann", "laura.neumann@firma.example", "Abstimmung Projektplan Q4",
                "<p>Hallo Alex,</p><p>kannst du bis Freitag einen Blick auf den Projektplan werfen? Die Meilensteine stehen in Abschnitt 3.</p><p>Danke!<br>Laura</p>",
                false, true, "Projektplan-Q4.xlsx", 75, " dm_arbeit "),
            ("INBOX", "Sportverein Blau-Weiß", "vorstand@blau-weiss.example", "Einladung zur Jahreshauptversammlung",
                "<p>Liebe Mitglieder,</p><p>wir laden herzlich zur Jahreshauptversammlung am 14. November um 19:30 Uhr ins Vereinsheim ein.</p>",
                true, false, null, 100, ""),
            ("INBOX", "Online-Shop Mega", "deals@mega-shop.example", "Nur heute: 70 % auf alles!!!",
                "<h1 style='color:#c00'>MEGA SALE</h1><p>Jetzt zugreifen – nur solange der Vorrat reicht!</p><img src='https://tracker.mega-shop.example/open.gif'>",
                true, false, null, 130, ""),
            ("INBOX", "Bibliothek Musterstadt", "ausleihe@bibliothek.example", "Erinnerung: Rückgabe bis 25.10.",
                "<p>Guten Tag,</p><p>die Leihfrist für „Der Garten im Herbst“ endet am 25.10. Sie können die Ausleihe online verlängern.</p>",
                true, false, null, 170, ""),
            ("Sent", "Alex Berger", Me, "Re: Grillabend am Samstag?",
                "<p>Hallo Jana,</p><p>ich bin dabei und bringe Nudelsalat mit!</p><p>Viele Grüße<br>Alex</p>", true, false, null, 0, ""),
            ("Projekte/Hausbau", "Architekturbüro Klein", "post@klein-architektur.example", "Pläne Erdgeschoss – Version 3",
                "<p>Hallo Herr Berger,</p><p>anbei die überarbeiteten Pläne mit dem größeren Hauswirtschaftsraum.</p>",
                true, false, "EG-Plan-v3.pdf", 240, " dm_wichtig "),
        };

        var uids = new Dictionary<string, uint>();
        foreach (var x in samples)
        {
            var uid = uids[x.Folder] = (uids.TryGetValue(x.Folder, out var u) ? u : 0) + 1;
            var m = new MimeMessage();
            m.From.Add(new MailboxAddress(x.Name, x.Addr));
            m.To.Add(x.Folder == "Sent" ? new MailboxAddress("Jana Wolff", "jana.wolff@example.org") : new MailboxAddress("Alex Berger", Me));
            m.Subject = x.Subject;
            m.Date = DateTimeOffset.Now.AddHours(-x.Hours).AddMinutes(-7);
            m.MessageId = $"docs-{x.Folder.Replace('/', '-')}-{uid}@dmoffice";
            var b = new BodyBuilder { HtmlBody = x.Html };
            if (x.Att != null) b.Attachments.Add(x.Att, System.Text.Encoding.ASCII.GetBytes("%PDF-1.4 Beispiel"), new ContentType("application", "pdf"));
            m.Body = b.ToMessageBody();
            using var ms = new MemoryStream();
            m.WriteTo(ms);

            var to = m.To.Mailboxes.First();
            db.Exec(@"INSERT OR REPLACE INTO mail(account,folder,uid,message_id,subject,from_name,from_addr,recipients,date,size,seen,flagged,has_att,keywords,answered)
                      VALUES($a,$f,$u,$mid,$s,$fn,$fa,$rc,$d,$sz,$seen,$fl,$att,$kw,$an)",
                ("$a", account.Id), ("$f", x.Folder), ("$u", (long)uid), ("$mid", m.MessageId), ("$s", x.Subject), ("$fn", x.Name), ("$fa", x.Addr),
                ("$rc", $"{{\"to\":[{{\"name\":\"{to.Name}\",\"address\":\"{to.Address}\"}}],\"cc\":[]}}"),
                ("$d", m.Date.ToUnixTimeMilliseconds()), ("$sz", ms.Length), ("$seen", x.Seen ? 1 : 0), ("$fl", x.Flag ? 1 : 0),
                ("$att", x.Att != null ? 1 : 0), ("$kw", x.Kw), ("$an", x.Subject.StartsWith("Grillabend") ? 1 : 0));
            var id = Convert.ToInt64(db.Scalar("SELECT id FROM mail WHERE account=$a AND folder=$f AND uid=$u", ("$a", account.Id), ("$f", x.Folder), ("$u", (long)uid)));
            db.Exec("DELETE FROM mail_fts WHERE rowid=$i", ("$i", id));
            db.Exec("INSERT INTO mail_fts(rowid,subject,sender,recipients,body) VALUES($i,$s,$f,'','')", ("$i", id), ("$s", x.Subject), ("$f", x.Name + " " + x.Addr));
            MailStore.StoreRaw(db, account.Id, x.Folder, uid, ms.ToArray(), m);
        }
        db.Exec(@"UPDATE mail_folders SET unread=(SELECT COUNT(*) FROM mail WHERE account=$a AND folder=mail_folders.path AND seen=0),
                  total=(SELECT COUNT(*) FROM mail WHERE account=$a AND folder=mail_folders.path) WHERE account=$a", ("$a", account.Id));
        // Werbung als "unsicher" markiert zeigen
        db.Exec("UPDATE mail SET spam=0.6 WHERE account=$a AND subject LIKE 'Nur heute%'", ("$a", account.Id));

        // Zusaetzliche Beispiele fuer die Blog-Bilder (tools/docs/make-blog.sh) - nur mit DMOFFICE_DOCS_BLOG=1
        if (Environment.GetEnvironmentVariable("DMOFFICE_DOCS_BLOG") == "1") Blogpostfach(db, account);
    }

    private static string Iso(DateTime local) => local.ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
    private static string IcsUtc(DateTime local) => local.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture);
    private static string De(DateTime d, string format) => d.ToString(format, System.Globalization.CultureInfo.GetCultureInfo("de-DE"));

    /// <summary>Weitere erfundene Mails: unsicher, Newsletter mit Bildern, Paket, Flug, Tisch, Einladung, Rechnungen, Nachverfolgung.</summary>
    private static void Blogpostfach(UserDb db, MailAccount account)
    {
        MailStore.ReplaceFolders(db, account.Id, new()
        {
            ("INBOX", "INBOX", "/", "inbox", true), ("Sent", "Sent", "/", "sent", true), ("Drafts", "Drafts", "/", "drafts", true),
            ("Trash", "Trash", "/", "trash", true), ("Junk", "Junk", "/", "junk", true), ("Archive", "Archive", "/", "archive", true),
            ("Projekte", "Projekte", "/", null, true), ("Projekte/Hausbau", "Hausbau", "/", null, true), ("Verein", "Verein", "/", null, true),
            ("Rechnungen", "Rechnungen", "/", null, true),
        });

        var today = DateTime.Today;
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var tomorrow = today.AddDays(1);
        var flight = today.AddDays(14).AddHours(7).AddMinutes(15);
        var dinner = monday.AddDays(3).AddHours(12).AddMinutes(30);
        var invite = monday.AddDays(2).AddHours(14).AddMinutes(30);
        // JSON-LD wie in echten Buchungsmails
        static string Ld(string json) => "<html><head><script type=\"application/ld+json\">" + json + "</script></head><body>";

        var parcelHtml = Ld($$$"""
            {"@context":"https://schema.org","@type":"ParcelDelivery",
             "expectedArrivalFrom":"{{{Iso(tomorrow.AddHours(10))}}}","expectedArrivalUntil":"{{{Iso(tomorrow.AddHours(14))}}}",
             "carrier":{"@type":"Organization","name":"Nordlicht Paketdienst"},"trackingNumber":"NL4711202610",
             "itemShipped":{"@type":"Product","name":"Hochbeet aus Lärchenholz"},
             "partOfOrder":{"@type":"Order","orderNumber":"GL-20931","merchant":{"@type":"Organization","name":"Gartenhaus Lindner"} } }
            """) +
            "<p>Hallo Alex Berger,</p><p>gute Nachrichten: Ihre Bestellung <b>GL-20931</b> ist unterwegs und kommt voraussichtlich <b>morgen zwischen 10:00 und 14:00 Uhr</b>.</p>" +
            "<table cellpadding=6 style='border-collapse:collapse;background:#f5f5f5'><tr><td>Artikel</td><td><b>Hochbeet aus Lärchenholz</b></td></tr>" +
            "<tr><td>Zusteller</td><td>Nordlicht Paketdienst</td></tr><tr><td>Sendungsnummer</td><td>NL4711202610</td></tr></table>" +
            "<p>Viel Freude beim Gärtnern!<br>Ihr Team vom Gartenhaus Lindner</p>";

        var flightHtml = Ld($$$"""
            {"@context":"https://schema.org","@type":"FlightReservation","reservationNumber":"K7QZ2M",
             "underName":{"@type":"Person","name":"Alex Berger"},
             "reservationFor":{"@type":"Flight","flightNumber":"123","airline":{"@type":"Airline","name":"Beispiel Air","iataCode":"XY"},
              "departureAirport":{"@type":"Airport","name":"Frankfurt am Main","iataCode":"FRA"},"departureTime":"{{{Iso(flight)}}}",
              "arrivalAirport":{"@type":"Airport","name":"Lissabon","iataCode":"LIS"},"arrivalTime":"{{{Iso(flight.AddHours(3).AddMinutes(20))}}}"}}
            """) +
            "<p>Hallo Alex Berger,</p><p>vielen Dank für Ihre Buchung. Hier Ihre Reisedaten:</p>" +
            "<table cellpadding=6 style='border-collapse:collapse;background:#f5f5f5'><tr><td>Buchungscode</td><td><b>K7QZ2M</b></td></tr>" +
            $"<tr><td>Flug</td><td>XY 123 Frankfurt → Lissabon</td></tr><tr><td>Abflug</td><td>{De(flight, "dddd, dd.MM.yyyy, HH:mm")} Uhr</td></tr></table>" +
            "<p>Online-Check-in ist 24 Stunden vor Abflug möglich.</p><p>Gute Reise!<br>Ihr Team von Beispiel Air</p>";

        var dinnerHtml = Ld($$$"""
            {"@context":"https://schema.org","@type":"FoodEstablishmentReservation","reservationNumber":"T-5521",
             "startTime":"{{{Iso(dinner)}}}","endTime":"{{{Iso(dinner.AddHours(2))}}}","partySize":"4",
             "reservationFor":{"@type":"FoodEstablishment","name":"Restaurant Lindenblüte",
              "address":{"@type":"PostalAddress","streetAddress":"Am Markt 3","addressLocality":"Musterstadt"} } }
            """) +
            $"<p>Guten Tag Herr Berger,</p><p>wir freuen uns auf Ihren Besuch am <b>{De(dinner, "dddd, dd.MM.")} um {De(dinner, "HH:mm")} Uhr</b> für 4 Personen.</p><p>Herzliche Grüße<br>Restaurant Lindenblüte</p>";

        var newsletterHtml =
            "<table width='100%' cellpadding=0 style='max-width:620px;margin:auto;font-family:Arial'>" +
            "<tr><td><img src='https://bilder.gartenhaus-lindner.example/nl/2026-10/kopf.jpg' width=620 height=160 alt='Gartenbrief Oktober'></td></tr>" +
            "<tr><td style='padding:16px 0'><h2 style='color:#2e6b30;margin:0 0 8px'>Jetzt Blumenzwiebeln setzen</h2>" +
            "<p>Tulpen, Narzissen und Krokusse wollen vor dem ersten Frost in die Erde. Unsere Tipps für ein buntes Frühjahr.</p></td></tr>" +
            "<tr><td><table width='100%'><tr><td width='50%'><img src='https://bilder.gartenhaus-lindner.example/nl/2026-10/zwiebeln.jpg' width=290 height=180 alt='Blumenzwiebeln'></td>" +
            "<td width='50%'><img src='https://bilder.gartenhaus-lindner.example/nl/2026-10/laub.jpg' width=290 height=180 alt='Laub als Mulch'></td></tr></table></td></tr>" +
            "<tr><td style='padding:12px 0'><h3 style='margin:0 0 6px'>Laub ist kein Abfall</h3><p>Als Mulch schützt es Beete – und Igel finden darunter ein Winterquartier.</p></td></tr>" +
            "<tr><td style='font-size:12px;color:#777'>Sie erhalten diesen Gartenbrief, weil Sie ihn bestellt haben. Abmelden</td></tr></table>" +
            "<img src='https://zaehler.gartenhaus-lindner.example/open.gif?id=8812' width=1 height=1>";

        var ics = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Firma Beispiel//Kalender//DE\r\nMETHOD:REQUEST\r\nBEGIN:VEVENT\r\n" +
            "UID:docs-workshop-roadmap@firma.example\r\nDTSTAMP:" + IcsUtc(DateTime.Now) + "\r\nSEQUENCE:0\r\n" +
            "DTSTART:" + IcsUtc(invite) + "\r\nDTEND:" + IcsUtc(invite.AddHours(1)) + "\r\n" +
            "SUMMARY:Workshop Roadmap 2027\r\nLOCATION:Besprechungsraum 3.02\r\n" +
            "ORGANIZER;CN=Laura Neumann:mailto:laura.neumann@firma.example\r\n" +
            "ATTENDEE;CN=Alex Berger;ROLE=REQ-PARTICIPANT;PARTSTAT=NEEDS-ACTION;RSVP=TRUE:mailto:" + Me + "\r\n" +
            "ATTENDEE;CN=Paul Hoffmann;ROLE=OPT-PARTICIPANT;PARTSTAT=NEEDS-ACTION;RSVP=TRUE:mailto:p.hoffmann@hoffmann-ag.example\r\n" +
            "END:VEVENT\r\nEND:VCALENDAR\r\n";

        // (Ordner, Name, Adresse, Betreff, HTML, gelesen, markiert, Anhang, Stunden zurueck, Kategorien, Spam, faellig in Tagen, Einladung)
        var samples = new (string Folder, string Name, string Addr, string Subject, string Html, bool Seen, bool Flag, string? Att, double Hours, string Kw, double? Spam, int? Due, string? Ics)[]
        {
            ("INBOX", "Kundenservice Kontoprüfung", "service@konto-pruefung.example", "Ihr Kundenkonto: Bitte Daten bis morgen bestätigen",
                "<p>Sehr geehrte Kundin, sehr geehrter Kunde,</p><p>bei Ihrem Kundenkonto ist eine Überprüfung nötig. Bitte bestätigen Sie Ihre Daten bis morgen, sonst wird das Konto eingeschränkt.</p>" +
                "<p><a href='https://konto-pruefung.example/bestaetigen'>Jetzt bestätigen</a></p><p>Ihr Kundenservice</p>",
                false, false, null, 0.4, "", 0.47, null, null),
            ("INBOX", "Gartenhaus Lindner", "versand@gartenhaus-lindner.example", "Ihre Bestellung GL-20931 ist unterwegs", parcelHtml,
                false, false, null, 0.8, "", null, null, null),
            ("INBOX", "Laura Neumann", "laura.neumann@firma.example", "Einladung: Workshop Roadmap 2027",
                $"<p>Hallo Alex,</p><p>ich lade dich zum Workshop zur Roadmap 2027 ein – am {De(invite, "dddd, dd.MM.")} um {De(invite, "HH:mm")} Uhr im Besprechungsraum 3.02.</p><p>Viele Grüße<br>Laura</p>",
                false, false, null, 1.6, " dm_arbeit ", null, null, ics),
            ("INBOX", "Beispiel Air", "buchung@beispiel-air.example", "Buchungsbestätigung K7QZ2M – Frankfurt → Lissabon", flightHtml,
                true, false, null, 2.2, "", null, null, null),
            ("INBOX", "Gartenhaus Lindner", "newsletter@gartenhaus-lindner.example", "Gartenbrief Oktober: Zwiebeln setzen, Laub nutzen", newsletterHtml,
                false, false, null, 4.5, "", null, null, null),
            ("INBOX", "Restaurant Lindenblüte", "reservierung@lindenbluete.example", "Ihre Tischreservierung T-5521", dinnerHtml,
                true, false, null, 5.5, "", null, null, null),
            ("INBOX", "Stadtwerke Musterstadt", "kundenservice@stadtwerke.example", "Ihre Rechnung Oktober 2026",
                "<p>Guten Tag,</p><p>Ihre Rechnung für Oktober 2026 über 96,40 € steht im Kundenportal bereit. Der Betrag wird am 30.10. abgebucht.</p>",
                true, false, "Rechnung-2026-10.pdf", 7, " dm_rechnung ", null, null, null),
            ("INBOX", "Gartenbau Grünfink", "buero@gruenfink.example", "Rechnung Nr. 4711 – Heckenschnitt",
                "<p>Hallo Herr Berger,</p><p>anbei unsere Rechnung für den Heckenschnitt vom 12.10. Zahlbar innerhalb von 14 Tagen.</p><p>Gartenbau Grünfink</p>",
                true, true, "Rechnung-4711.pdf", 26, " dm_rechnung ", null, 3, null),
            ("INBOX", "Steuerbüro Hartmann", "kanzlei@hartmann-steuer.example", "Unterlagen für die Steuererklärung 2025",
                "<p>Hallo Herr Berger,</p><p>für die Steuererklärung fehlen uns noch die Nebenkostenabrechnung und die Spendenbelege. Können Sie uns diese schicken?</p>",
                true, true, null, 52, "", null, 7, null),
            ("INBOX", "Buchhandlung Seitenweise", "shop@seitenweise.example", "Ihre Rechnung zur Bestellung 10-2291",
                "<p>Vielen Dank für Ihren Einkauf! Ihre Rechnung finden Sie im Anhang.</p>",
                true, false, "Rechnung-10-2291.pdf", 80, " dm_rechnung ", null, null, null),
            ("INBOX", "Funknetz Beispiel", "rechnung@funknetz.example", "Rechnung Mobilfunk September",
                "<p>Ihre Mobilfunkrechnung für September beträgt 19,99 €.</p>",
                true, false, "Mobilfunk-2026-09.pdf", 200, " dm_rechnung ", null, null, null),
        };

        foreach (var x in samples)
        {
            var uid = (uint)(Convert.ToInt64(db.Scalar("SELECT COALESCE(MAX(uid),0) FROM mail WHERE account=$a AND folder=$f", ("$a", account.Id), ("$f", x.Folder))) + 1);
            var m = new MimeMessage();
            m.From.Add(new MailboxAddress(x.Name, x.Addr));
            m.To.Add(new MailboxAddress("Alex Berger", Me));
            m.Subject = x.Subject;
            m.Date = DateTimeOffset.Now.AddHours(-x.Hours);
            m.MessageId = $"docs-blog-{x.Folder.Replace('/', '-')}-{uid}@dmoffice";
            if (x.Ics != null)
            {
                var cal = new TextPart("calendar") { Text = x.Ics };
                cal.ContentType.Parameters.Add("method", "REQUEST");
                cal.ContentType.Charset = "utf-8";
                m.Body = new MultipartAlternative
                {
                    new TextPart("plain") { Text = "Einladung: Workshop Roadmap 2027" },
                    new TextPart("html") { Text = x.Html },
                    cal,
                };
            }
            else
            {
                var b = new BodyBuilder { HtmlBody = x.Html };
                if (x.Att != null) b.Attachments.Add(x.Att, System.Text.Encoding.ASCII.GetBytes("%PDF-1.4 Beispiel"), new ContentType("application", "pdf"));
                m.Body = b.ToMessageBody();
            }
            using var ms = new MemoryStream();
            m.WriteTo(ms);

            db.Exec(@"INSERT OR REPLACE INTO mail(account,folder,uid,message_id,subject,from_name,from_addr,recipients,date,size,seen,flagged,has_att,keywords,answered)
                      VALUES($a,$f,$u,$mid,$s,$fn,$fa,$rc,$d,$sz,$seen,$fl,$att,$kw,0)",
                ("$a", account.Id), ("$f", x.Folder), ("$u", (long)uid), ("$mid", m.MessageId), ("$s", x.Subject), ("$fn", x.Name), ("$fa", x.Addr),
                ("$rc", $"{{\"to\":[{{\"name\":\"Alex Berger\",\"address\":\"{Me}\"}}],\"cc\":[]}}"),
                ("$d", m.Date.ToUnixTimeMilliseconds()), ("$sz", ms.Length), ("$seen", x.Seen ? 1 : 0), ("$fl", x.Flag ? 1 : 0),
                ("$att", x.Att != null ? 1 : 0), ("$kw", x.Kw));
            var id = Convert.ToInt64(db.Scalar("SELECT id FROM mail WHERE account=$a AND folder=$f AND uid=$u", ("$a", account.Id), ("$f", x.Folder), ("$u", (long)uid)));
            db.Exec("DELETE FROM mail_fts WHERE rowid=$i", ("$i", id));
            db.Exec("INSERT INTO mail_fts(rowid,subject,sender,recipients,body) VALUES($i,$s,$f,'','')", ("$i", id), ("$s", x.Subject), ("$f", x.Name + " " + x.Addr));
            MailStore.StoreRaw(db, account.Id, x.Folder, uid, ms.ToArray(), m);
            if (x.Spam is double spam) MailStore.SetSpamScore(db, account.Id, x.Folder, uid, spam);
            if (x.Due is int due) MailStore.SetFollowUpDue(db, account.Id, x.Folder, new[] { uid }, today.AddDays(due).ToString("yyyy-MM-dd"));
        }

        // Faelligkeiten fuer die schon markierten Beispielmails: eine ueberfaellig, eine morgen
        void Due(string subjectStart, int days)
        {
            var uid = db.Scalar("SELECT uid FROM mail WHERE account=$a AND folder='INBOX' AND subject LIKE $s", ("$a", account.Id), ("$s", subjectStart + "%"));
            if (uid != null) MailStore.SetFollowUpDue(db, account.Id, "INBOX", new[] { (uint)Convert.ToInt64(uid) }, today.AddDays(days).ToString("yyyy-MM-dd"));
        }
        Due("Abstimmung Projektplan Q4", -2);
        Due("Angebot Nr. 2026-0418", 1);

        Newsletter(db, account);

        db.Exec(@"UPDATE mail_folders SET unread=(SELECT COUNT(*) FROM mail WHERE account=$a AND folder=mail_folders.path AND seen=0),
                  total=(SELECT COUNT(*) FROM mail WHERE account=$a AND folder=mail_folders.path) WHERE account=$a", ("$a", account.Id));
    }

    /// <summary>Prio-Posteingang und Newsletter abmelden: Verteiler-Mails mit List-Unsubscribe, fest eingeordnet
    /// (Relevant/Sonstige), damit die Bilder nicht vom Zufall des Filters abhaengen.</summary>
    private static void Newsletter(UserDb db, MailAccount account)
    {
        // (Name, Adresse, Betreff, HTML, gelesen, Stunden zurueck, List-Unsubscribe, One-Click)
        var samples = new (string Name, string Addr, string Subject, string Html, bool Seen, double Hours, string Unsub, bool OneClick)[]
        {
            ("Kurbel & Kette Radsport", "news@kurbel-kette.example", "Herbstputz fürs Fahrrad: 5 Tipps und 15 % auf Pflegemittel",
                "<table width='100%' style='max-width:620px;margin:auto;font-family:Arial'><tr><td style='background:#1f4e5f;color:#fff;padding:18px'>" +
                "<h2 style='margin:0'>Kurbel &amp; Kette – Werkstattbrief</h2></td></tr><tr><td style='padding:16px 0'>" +
                "<h3 style='margin:0 0 8px'>Fit durch Herbst und Winter</h3><ol><li>Kette reinigen und ölen</li><li>Bremsbeläge prüfen</li>" +
                "<li>Licht testen</li><li>Reifendruck anpassen</li><li>Schaltung nachstellen lassen</li></ol>" +
                "<p>Bis Sonntag 15 % auf alle Pflegemittel in unserer Werkstatt am Bahnhof.</p></td></tr>" +
                "<tr><td style='font-size:12px;color:#777'>Sie erhalten den Werkstattbrief, weil Sie ihn bestellt haben.</td></tr></table>",
                false, 2.8, "<https://abmelden.kurbel-kette.example/u/7f3a91>, <mailto:abmelden@kurbel-kette.example?subject=unsubscribe>", true),
            ("Lichtspiele Musterstadt", "programm@lichtspiele.example", "Kinoprogramm ab Donnerstag: Herbstfilmtage",
                "<p>Liebe Kinofreunde,</p><p>ab Donnerstag zeigen wir bei den Herbstfilmtagen zwölf Filme aus Europa – mit Gesprächsrunde am Samstag.</p>" +
                "<p>Ihr Team der Lichtspiele</p>",
                false, 9, "<mailto:programm-abmelden@lichtspiele.example?subject=abmelden>", false),
            ("Kochkiste", "rezepte@kochkiste.example", "Wochenrezepte: Kürbis in allen Varianten",
                "<p>Hallo,</p><p>diese Woche: Kürbissuppe mit Ingwer, Ofenkürbis mit Feta und Kürbisbrot.</p><p>Guten Appetit!<br>Ihre Kochkiste</p>",
                true, 30, "<https://kochkiste.example/newsletter/abmelden?id=55120>", false),
            ("Cloudspeicher Beispiel", "noreply@cloudspeicher.example", "Ihr Speicher ist zu 80 % belegt",
                "<p>Guten Tag,</p><p>Ihr Online-Speicher ist zu 80 % belegt. Mit einem größeren Tarif haben Sie wieder Platz.</p>",
                true, 60, "", false),
        };
        foreach (var x in samples)
        {
            var uid = (uint)(Convert.ToInt64(db.Scalar("SELECT COALESCE(MAX(uid),0) FROM mail WHERE account=$a AND folder='INBOX'", ("$a", account.Id))) + 1);
            var m = new MimeMessage();
            m.From.Add(new MailboxAddress(x.Name, x.Addr));
            m.To.Add(new MailboxAddress("Alex Berger", Me));
            m.Subject = x.Subject;
            m.Date = DateTimeOffset.Now.AddHours(-x.Hours);
            m.MessageId = $"docs-blog-nl-{uid}@dmoffice";
            if (x.Unsub.Length > 0)
            {
                m.Headers.Add("List-Id", $"<{x.Addr.Split('@')[1]}>");
                m.Headers.Add("List-Unsubscribe", x.Unsub);
                if (x.OneClick) m.Headers.Add("List-Unsubscribe-Post", "List-Unsubscribe=One-Click");
            }
            m.Body = new BodyBuilder { HtmlBody = x.Html }.ToMessageBody();
            using var ms = new MemoryStream();
            m.WriteTo(ms);
            db.Exec(@"INSERT OR REPLACE INTO mail(account,folder,uid,message_id,subject,from_name,from_addr,recipients,date,size,seen,flagged,has_att,keywords,answered)
                      VALUES($a,'INBOX',$u,$mid,$s,$fn,$fa,$rc,$d,$sz,$seen,0,0,'',0)",
                ("$a", account.Id), ("$u", (long)uid), ("$mid", m.MessageId), ("$s", x.Subject), ("$fn", x.Name), ("$fa", x.Addr),
                ("$rc", $"{{\"to\":[{{\"name\":\"Alex Berger\",\"address\":\"{Me}\"}}],\"cc\":[]}}"),
                ("$d", m.Date.ToUnixTimeMilliseconds()), ("$sz", ms.Length), ("$seen", x.Seen ? 1 : 0));
            var id = Convert.ToInt64(db.Scalar("SELECT id FROM mail WHERE account=$a AND folder='INBOX' AND uid=$u", ("$a", account.Id), ("$u", (long)uid)));
            db.Exec("DELETE FROM mail_fts WHERE rowid=$i", ("$i", id));
            db.Exec("INSERT INTO mail_fts(rowid,subject,sender,recipients,body) VALUES($i,$s,$f,'','')", ("$i", id), ("$s", x.Subject), ("$f", x.Name + " " + x.Addr));
            MailStore.StoreRaw(db, account.Id, "INBOX", uid, ms.ToArray(), m);
        }

        // Fest einordnen: alles Relevant, Verteiler und Werbung nach Sonstige
        db.Exec("UPDATE mail SET focus=1 WHERE account=$a AND folder='INBOX'", ("$a", account.Id));
        db.Exec(@"UPDATE mail SET focus=0 WHERE account=$a AND folder='INBOX' AND (message_id LIKE 'docs-blog-nl-%'
                  OR subject LIKE 'Gartenbrief%' OR subject LIKE 'Nur heute%')", ("$a", account.Id));
    }
}
