using System.Text.RegularExpressions;
using DmOffice.Dav;
using DmOffice.Mail;
using DmOffice.Notes;
using DmOffice.Pim;
using MimeKit;
using SkiaSharp;

namespace DmOffice.Core;

/// <summary>
/// Demo-Server (DMOFFICE_DEMO=1, z. B. play.dmoffice.de für Play-Tester und die Prüfung durch Google): ein gemeinsames
/// Demo-Konto mit erfundenen Mails, Terminen, Aufgaben, Notizen, Bildern und DMS-Dokumenten. Gesperrt ist alles, was nach außen
/// geht oder andere betrifft: Mailkonten, Mailversand, Freigabelinks, Einladungen, Freigaben, externe Ordner, Datensicherung,
/// Kalender-Abos, Rezeptimport, Mail-Quellen und Paperless-Import des DMS, dazu Passwort, Zwei-Faktor und der Tresor
/// (<see cref="FeaturesOff"/>). Gezeigt wird nur, was die Version enthält (features.json) - DMOFFICE_FEATURES bleibt leer. Zurückgesetzt wird von außen: Container stoppen,
/// Datenordner leeren, starten - beim Start legt <see cref="Seed"/> alles neu an, mit Daten rund um den heutigen Tag.
/// </summary>
public static class Demo
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("DMOFFICE_DEMO") == "1";
    public static string Login => Environment.GetEnvironmentVariable("DMOFFICE_DEMO_LOGIN") is { Length: > 0 } l ? l : "demo";
    public static string Password => Environment.GetEnvironmentVariable("DMOFFICE_DEMO_PASSWORD") is { Length: >= 8 } p ? p : "dmoffice-demo";
    /// <summary>Speicher für das Demo-Konto (MB), Vorgabe 50 GB.</summary>
    private static long QuotaMb => long.TryParse(Environment.GetEnvironmentVariable("DMOFFICE_DEMO_QUOTA_MB"), out var mb) && mb > 0 ? mb : 51_200;

    // Schreibende Aufrufe, die in der Demo nichts zu suchen haben (Pfad ohne /api, Methode nicht GET)
    private static readonly Regex Blocked = new(@"^/api/(" + string.Join("|",
        @"auth/password", @"auth/2fa/.*",
        @"settings/accounts.*",
        @"mail/accounts.*", @"mail/[^/]+/send", @"mail/[^/]+/invite",
        @"files/links.*", @"files/shares", @"gallery/albums/\d+/links.*",
        @"emergency(/.*)?", @"finance(/.*)?",
        @"files/external(/.*)?", @"plainbackup(/.*)?", @"pim/subscriptions(/.*)?", @"notes/recipes/(import|scan/ai)",
        @"dms/(mail-sources|paperless)(/.*)?", @"gallery/imports(/.*)?", @"tresor(/.*)?",
        @"(pim|notes|lists|tasks|calendar)/.*shares.*") + ")$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// In der Demo immer aus, auch wenn die Version sie enthält: der Tresor (ein gemeinsames Konto - wer zuerst ein Passwort setzt,
    /// sperrt alle anderen aus) und die Importprofile der Galerie (lesen aus externen Ordnern).
    /// </summary>
    public static readonly string[] FeaturesOff = [Features.Vault, Features.ImportProfiles];

    /// <summary>Gesperrte Aufrufe mit 403 und einer kurzen Meldung beantworten.</summary>
    public static async Task Guard(HttpContext http, Func<Task> next)
    {
        if (!HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method) && Blocked.IsMatch(http.Request.Path.Value ?? ""))
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            await http.Response.WriteAsJsonAsync(new { error = "In der Demo nicht möglich." });
            return;
        }
        await next();
    }

    /// <summary>
    /// Leerer Server: nur das Demo-Konto mit Inhalten anlegen. Kein Verwaltungskonto - so gibt es niemanden zum Teilen und
    /// nirgends einen Namen, der auf eine Verwaltung schließen ließe.
    /// </summary>
    public static void Seed(IServiceProvider services, ILogger log)
    {
        var users = services.GetRequiredService<UserStore>();
        if (!users.Empty) return;
        services.GetRequiredService<ServerConfig>().Update(s =>
        {
            s.DefaultFileQuotaMb = QuotaMb;
            // Nichts nach außen: keine eigenen externen Ordner, keine Datensicherung
            s.ExternalWebDavForUsers = s.ExternalOneDriveForUsers = s.ExternalPCloudForUsers = false;
            s.PlainBackupOff = true;
        });
        var user = users.Create(Login, "Demo", Password, false);
        var space = services.GetRequiredService<Spaces>().Get(user.Id);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, space.Zone));
        Pim(space, today);
        NotesOf(space);
        Mails(space, today);
        Pictures(space, today, services.GetRequiredService<DmOffice.Gallery.AlbumStore>());
        Documents(space, today);
        log.LogInformation("Demo: Konto {Login} mit Beispieldaten angelegt", Login);
    }

    // ---------------- Kalender und Aufgaben ----------------

    private static void Pim(UserSpace space, DateOnly today)
    {
        var db = space.Db;
        // Genau ein Kalender und eine Aufgabenliste (statt der Vorgaben)
        var cal = PimStore.CreateCollection(db, PimStore.Calendar, "kalender", "Kalender", "#0F6CBD", "VEVENT");
        var list = PimStore.CreateCollection(db, PimStore.Calendar, "aufgaben", "Aufgaben", "#107C10", "VTODO");
        PimStore.CreateCollection(db, PimStore.AddressBook, "contacts", "Kontakte", null, "");
        db.Set("pim.defaults", "1");

        string At(int day, int h, int m = 0) => new DateTimeOffset(today.AddDays(day).ToDateTime(new TimeOnly(h, m)),
            space.Zone.GetUtcOffset(today.AddDays(day).ToDateTime(new TimeOnly(h, m)))).ToString("o");
        void Ev(string title, int day, int h, int m, int minutes, string? location = null, string? description = null)
        {
            var start = today.AddDays(day).ToDateTime(new TimeOnly(h, m));
            var end = start.AddMinutes(minutes);
            CalendarModel.SaveEvent(space, new EventInput
            {
                Calendar = cal.Id, Title = title, Location = location, Description = description, Notify = false,
                Start = At(day, h, m), End = new DateTimeOffset(end, space.Zone.GetUtcOffset(end)).ToString("o"),
            });
        }
        void AllDay(string title, int day, int days = 1) => CalendarModel.SaveEvent(space, new EventInput
        {
            Calendar = cal.Id, Title = title, AllDay = true, Busy = false, Notify = false,
            Start = today.AddDays(day).ToString("yyyy-MM-dd"), End = today.AddDays(day + days).ToString("yyyy-MM-dd"),
        });

        Ev("Teambesprechung", 0, 9, 30, 60, "Besprechungsraum 2", "Wochenplanung und offene Punkte");
        Ev("Mittagessen mit Jana", 0, 12, 30, 60, "Café am Markt");
        Ev("Zahnarzt", 1, 8, 15, 45, "Praxis Dr. Sommer");
        Ev("Kundentermin Müller GmbH", 1, 14, 0, 90, "Online", "Angebot besprechen");
        Ev("Laufgruppe", 2, 18, 30, 60, "Stadtpark");
        Ev("Elternabend", 3, 19, 0, 90, "Grundschule");
        Ev("Projekt-Review", 4, 10, 0, 60);
        Ev("Kino mit Freunden", 5, 20, 0, 150, "Lichtspielhaus");
        Ev("Brunch bei Oma", 6, 11, 0, 120);
        Ev("Physiotherapie", -1, 16, 0, 45);
        Ev("Werkstatt: Auto abholen", -2, 17, 0, 30, "Autohaus Weber");
        AllDay("Geburtstag Paul 🎂", 2);
        AllDay("Urlaub Ostsee", 12, 5);

        void Task(string title, int? dueDay = null, int priority = 0, bool done = false, string? description = null) =>
            CalendarModel.SaveTask(space, new TaskInput
            {
                List = list.Id, Title = title, Priority = priority, Completed = done, Description = description,
                Due = dueDay is int d ? today.AddDays(d).ToString("yyyy-MM-dd") : null,
            });
        Task("Steuererklärung abgeben", 5, 1, description: "Belege liegen im Ordner „Steuer“");
        Task("Geschenk für Paul besorgen", 1, 5);
        Task("Reifenwechsel buchen", 3);
        Task("Fotos vom Urlaub sortieren");
        Task("Versicherung kündigen", -1, 1);
        Task("Rückruf Handwerker", 0);
        Task("Bücher zur Bibliothek bringen", -3, done: true);
        Task("Wasserfilter tauschen", -5, done: true);
    }

    // ---------------- Notizen ----------------

    private static void NotesOf(UserSpace space)
    {
        var notes = NotesDb.Of(space);
        void Note(params string[] lines)
        {
            var id = notes.CreateNote(null);
            var html = "<div data-notizi=\"1\">" + string.Concat(lines.Select(l => "<p>" + System.Net.WebUtility.HtmlEncode(l) + "</p>")) + "</div>";
            notes.SaveNote(id, html, string.Join("\n", lines));
        }
        Note("Willkommen bei dmOffice", "Das ist ein Demo-Konto. Alles hier ist erfunden und wird jede Nacht zurückgesetzt.",
            "Probier ruhig alles aus: Termine anlegen, Aufgaben abhaken, Notizen schreiben, Dateien hochladen.");
        Note("Einkaufsideen fürs Wochenende", "Brot vom Bäcker", "Äpfel und Birnen", "Kaffeebohnen", "Blumen für Oma");
        Note("Urlaub Ostsee", "Unterkunft: Ferienwohnung Seeblick", "Packliste: Badesachen, Regenjacke, Ladekabel, Bücher",
            "Ausflüge: Leuchtturm, Fischmarkt, Radtour an der Küste");
        Note("Rezept: Pfannkuchen", "250 g Mehl, 500 ml Milch, 3 Eier, 1 Prise Salz", "Alles verrühren, 15 Minuten ruhen lassen, in Butter ausbacken.");
        Note("Ideen für Pauls Geburtstag", "Brettspiel", "Gutschein Kletterhalle", "Buch über Raumfahrt");
    }

    // ---------------- Bilder ----------------

    /// <summary>Vier gezeichnete Bilder (keine Fotos von irgendwem), klein genug fürs Kontingent, und ein Album.</summary>
    private static void Pictures(UserSpace space, DateOnly today, DmOffice.Gallery.AlbumStore albums)
    {
        FileStore.Mkcol(space.Db, "Fotos");
        static byte[] Jpeg(SKSurface s) { using var img = s.Snapshot(); using var d = img.Encode(SKEncodedImageFormat.Jpeg, 82); return d.ToArray(); }
        const int W = 1200, H = 800;

        using (var s = SKSurface.Create(new SKImageInfo(W, H)))
        {
            var c = s.Canvas;
            using var sky = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, H * 0.6f),
                [new SKColor(0x2B, 0x3A, 0x67), new SKColor(0xF2, 0x8A, 0x5C), new SKColor(0xFB, 0xD3, 0x8B)], SKShaderTileMode.Clamp) };
            c.DrawRect(0, 0, W, H, sky);
            using var sun = new SKPaint { Color = new SKColor(0xFF, 0xE9, 0xA8), IsAntialias = true };
            c.DrawCircle(W * 0.62f, H * 0.56f, 70, sun);
            using var sea = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, H * 0.6f), new SKPoint(0, H),
                [new SKColor(0x3C, 0x6E, 0x91), new SKColor(0x1D, 0x3B, 0x53)], SKShaderTileMode.Clamp) };
            c.DrawRect(0, H * 0.6f, W, H * 0.4f, sea);
            using var glint = new SKPaint { Color = new SKColor(0xFF, 0xE9, 0xA8, 0x90), IsAntialias = true };
            for (var i = 0; i < 9; i++) c.DrawRect(W * 0.62f - 60 + i * 7 % 40, H * 0.62f + i * 22, 120 - i * 10, 4, glint);
            using var sand = new SKPaint { Color = new SKColor(0xE8, 0xC9, 0x8F), IsAntialias = true };
            using var dune = new SKPath();
            dune.MoveTo(0, H * 0.82f); dune.QuadTo(W * 0.3f, H * 0.74f, W * 0.55f, H * 0.86f); dune.QuadTo(W * 0.8f, H * 0.95f, W, H * 0.88f);
            dune.LineTo(W, H); dune.LineTo(0, H); dune.Close();
            c.DrawPath(dune, sand);
            FileStore.Put(space.Db, "Fotos/Ostsee Abendlicht.jpg", Jpeg(s), "image/jpeg", mtime: today.AddDays(-9).ToDateTime(new TimeOnly(19, 40)));
        }

        using (var s = SKSurface.Create(new SKImageInfo(W, H)))
        {
            var c = s.Canvas;
            using var sky = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, H * 0.5f),
                [new SKColor(0x8E, 0xC5, 0xF0), new SKColor(0xD8, 0xEE, 0xFB)], SKShaderTileMode.Clamp) };
            c.DrawRect(0, 0, W, H, sky);
            using var grass = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, H * 0.45f), new SKPoint(0, H),
                [new SKColor(0x7C, 0xB3, 0x42), new SKColor(0x3F, 0x7D, 0x20)], SKShaderTileMode.Clamp) };
            c.DrawRect(0, H * 0.45f, W, H * 0.55f, grass);
            var rnd = new Random(7);
            SKColor[] colors = [new(0xE5, 0x39, 0x35), new(0xFD, 0xD8, 0x35), new(0xFF, 0xFF, 0xFF), new(0x8E, 0x24, 0xAA), new(0xFB, 0x8C, 0x00)];
            using var petal = new SKPaint { IsAntialias = true };
            using var core = new SKPaint { Color = new SKColor(0x6D, 0x4C, 0x41), IsAntialias = true };
            for (var i = 0; i < 140; i++)
            {
                var y = H * 0.5f + (float)Math.Pow(rnd.NextDouble(), 0.7) * H * 0.48f;
                var x = (float)rnd.NextDouble() * W;
                var r = 4 + (y - H * 0.5f) / H * 26;
                petal.Color = colors[rnd.Next(colors.Length)];
                for (var k = 0; k < 5; k++)
                {
                    var a = k * Math.PI * 2 / 5;
                    c.DrawCircle(x + (float)Math.Cos(a) * r, y + (float)Math.Sin(a) * r, r * 0.8f, petal);
                }
                c.DrawCircle(x, y, r * 0.6f, core);
            }
            FileStore.Put(space.Db, "Fotos/Blumenwiese.jpg", Jpeg(s), "image/jpeg", mtime: today.AddDays(-23).ToDateTime(new TimeOnly(11, 15)));
        }

        // Leuchtturm an der Küste (Urlaub)
        using (var s = SKSurface.Create(new SKImageInfo(W, H)))
        {
            var c = s.Canvas;
            using var sky = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, H * 0.65f),
                [new SKColor(0x5D, 0x9C, 0xEC), new SKColor(0xCF, 0xE8, 0xFA)], SKShaderTileMode.Clamp) };
            c.DrawRect(0, 0, W, H, sky);
            using var sea = new SKPaint { Color = new SKColor(0x2F, 0x6F, 0x9F), IsAntialias = true };
            c.DrawRect(0, H * 0.65f, W, H * 0.35f, sea);
            using var rock = new SKPaint { Color = new SKColor(0x6B, 0x6E, 0x70), IsAntialias = true };
            using var cliff = new SKPath();
            cliff.MoveTo(W * 0.45f, H); cliff.LineTo(W * 0.55f, H * 0.7f); cliff.LineTo(W * 0.85f, H * 0.66f); cliff.LineTo(W, H * 0.72f); cliff.LineTo(W, H); cliff.Close();
            c.DrawPath(cliff, rock);
            using var white = new SKPaint { Color = SKColors.White, IsAntialias = true };
            using var red = new SKPaint { Color = new SKColor(0xD3, 0x2F, 0x2F), IsAntialias = true };
            float x0 = W * 0.68f, top = H * 0.22f, bottom = H * 0.67f;
            for (var i = 0; i < 5; i++)
            {
                float y1 = top + (bottom - top) * i / 5, y2 = top + (bottom - top) * (i + 1) / 5;
                using var band = new SKPath();
                band.MoveTo(x0 - 26 - i * 4, y1); band.LineTo(x0 + 26 + i * 4, y1); band.LineTo(x0 + 30 + i * 4, y2); band.LineTo(x0 - 30 - i * 4, y2); band.Close();
                c.DrawPath(band, i % 2 == 0 ? red : white);
            }
            using var lamp = new SKPaint { Color = new SKColor(0xFF, 0xE0, 0x82), IsAntialias = true };
            c.DrawRect(x0 - 20, top - 40, 40, 40, lamp);
            c.DrawRect(x0 - 28, top - 50, 56, 10, red);
            FileStore.Put(space.Db, "Fotos/Leuchtturm.jpg", Jpeg(s), "image/jpeg", mtime: today.AddDays(-8).ToDateTime(new TimeOnly(15, 5)));
        }

        // Berge im Morgennebel
        using (var s = SKSurface.Create(new SKImageInfo(W, H)))
        {
            var c = s.Canvas;
            using var sky = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, H),
                [new SKColor(0xF6, 0xD7, 0xC3), new SKColor(0xC9, 0xD6, 0xEA)], SKShaderTileMode.Clamp) };
            c.DrawRect(0, 0, W, H, sky);
            SKColor[] layers = [new(0x9A, 0xA8, 0xC3), new(0x6F, 0x80, 0xA3), new(0x46, 0x56, 0x7A), new(0x2A, 0x36, 0x52)];
            var rnd = new Random(3);
            for (var l = 0; l < layers.Length; l++)
            {
                using var p = new SKPaint { Color = layers[l], IsAntialias = true };
                using var ridge = new SKPath();
                var baseY = H * (0.42f + l * 0.14f);
                ridge.MoveTo(0, H); ridge.LineTo(0, baseY);
                for (var x = 0; x <= W; x += 80) ridge.LineTo(x, baseY - (float)rnd.NextDouble() * 120 + 40);
                ridge.LineTo(W, H); ridge.Close();
                c.DrawPath(ridge, p);
            }
            FileStore.Put(space.Db, "Fotos/Berge im Nebel.jpg", Jpeg(s), "image/jpeg", mtime: today.AddDays(-40).ToDateTime(new TimeOnly(7, 30)));
        }

        // Album mit den Urlaubsbildern
        var album = albums.Create(space.User.Id, "Urlaub Ostsee", DmOffice.Gallery.Album.Fixed, null);
        var refs = new[] { "Fotos/Ostsee Abendlicht.jpg", "Fotos/Leuchtturm.jpg" }
            .Select(p => FileStore.Get(space.Db, p)).Where(f => f != null).Select(f => "f:" + f!.FileId).ToList();
        albums.Add(album, space.User.Id, refs, space.User.Id);
    }

    // ---------------- Mails ----------------

    private static void Mails(UserSpace space, DateOnly today)
    {
        var db = space.Db;
        var account = new MailAccount { Name = "Demo-Postfach", Email = "demo@play.dmoffice.de", DisplayName = "Demo" };
        space.UpdateSettings(s => s.MailAccounts.Add(account));
        MailStore.ReplaceFolders(db, account.Id, [("INBOX", "INBOX", "/", "inbox", true), ("Gesendet", "Gesendet", "/", "sent", true),
            ("Papierkorb", "Papierkorb", "/", "trash", true), ("Junk", "Junk", "/", "junk", true)]);
        uint uid = 0;
        void Mail(string folder, string fromName, string from, string subject, string text, int daysAgo, int hour, bool seen, bool flagged = false)
        {
            uid++;
            var sent = folder == "Gesendet";
            var date = new DateTimeOffset(today.AddDays(-daysAgo).ToDateTime(new TimeOnly(hour, 7 * (int)uid % 60)), TimeSpan.Zero);
            var msg = new MimeMessage();
            msg.From.Add(sent ? new MailboxAddress("Demo", account.Email) : new MailboxAddress(fromName, from));
            msg.To.Add(sent ? new MailboxAddress(fromName, from) : new MailboxAddress("Demo", account.Email));
            msg.Subject = subject;
            msg.Date = date;
            msg.MessageId = $"demo-{uid}@play.dmoffice.de";
            msg.Body = new TextPart("plain") { Text = text };
            using var ms = new MemoryStream();
            msg.WriteTo(ms);
            var raw = ms.ToArray();
            var who = msg.From.Mailboxes.First();
            var to = msg.To.Mailboxes.Select(m => new Addr(m.Name, m.Address)).ToList();
            db.Exec(@"INSERT INTO mail(account,folder,uid,message_id,subject,from_name,from_addr,recipients,date,size,seen,flagged,has_att)
                      VALUES($a,$f,$u,$mid,$s,$fn,$fa,$rc,$d,$sz,$seen,$fl,0)",
                ("$a", account.Id), ("$f", folder), ("$u", (long)uid), ("$mid", "<" + msg.MessageId + ">"), ("$s", subject),
                ("$fn", who.Name), ("$fa", who.Address), ("$rc", System.Text.Json.JsonSerializer.Serialize(new { to, cc = new List<Addr>() }, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))),
                ("$d", date.ToUnixTimeMilliseconds()), ("$sz", (long)raw.Length), ("$seen", seen ? 1 : 0), ("$fl", flagged ? 1 : 0));
            MailStore.StoreRaw(db, account.Id, folder, uid, raw, msg);
        }
        Mail("INBOX", "Jana Berger", "jana.berger@example.org", "Mittagessen heute?",
            "Hallo,\n\nbleibt es bei heute 12:30 im Café am Markt? Ich bringe die Unterlagen mit.\n\nViele Grüße\nJana", 0, 8, false);
        Mail("INBOX", "Müller GmbH", "angebote@mueller-gmbh.example", "Ihr Angebot Nr. 2481",
            "Sehr geehrte Damen und Herren,\n\nanbei die Eckdaten zu unserem Angebot. Gern besprechen wir Details morgen im Termin.\n\nMit freundlichen Grüßen\nMüller GmbH", 0, 7, false, true);
        Mail("INBOX", "Paketdienst", "versand@paket.example", "Ihre Sendung kommt morgen",
            "Ihre Sendung 00340434 wird morgen zwischen 10 und 14 Uhr zugestellt.", 1, 18, true);
        Mail("INBOX", "Grundschule Am Park", "sekretariat@grundschule.example", "Einladung zum Elternabend",
            "Liebe Eltern,\n\nwir laden herzlich zum Elternabend ein. Themen: Klassenfahrt und Schulfest.\n\nIhr Klassenteam", 2, 10, true);
        Mail("INBOX", "Laufgruppe Stadtpark", "info@laufgruppe.example", "Neue Strecke am Mittwoch",
            "Hallo zusammen,\n\ndiesen Mittwoch laufen wir die große Runde um den See. Treffpunkt wie immer am Haupteingang.", 3, 19, true);
        Mail("INBOX", "Oma Hilde", "hilde@example.net", "Brunch am Sonntag",
            "Ihr Lieben,\n\nich freue mich auf Sonntag! Bringt gern Brötchen mit, um den Rest kümmere ich mich.\n\nEure Oma", 4, 9, true);
        Mail("INBOX", "Stadtbibliothek", "ausleihe@bibliothek.example", "Erinnerung: Rückgabe",
            "Die Leihfrist für zwei Medien endet bald. Sie können online verlängern.", 6, 7, true);
        Mail("Gesendet", "Jana Berger", "jana.berger@example.org", "Re: Projektplanung",
            "Hallo Jana,\n\ndanke für die Übersicht, ich schaue sie mir bis Donnerstag an.\n\nGruß", 2, 15, true);
        // Vom Spamfilter aussortiert
        Mail("Junk", "Gewinnspiel-Team", "win@gewinn-jetzt.example", "Sie haben gewonnen!!! Jetzt Preis sichern",
            "Herzlichen Glückwunsch! Sie wurden als Gewinner ausgewählt. Klicken Sie jetzt, um Ihren Preis zu sichern. Nur heute!", 1, 3, false);
        MailStore.SetSpamScore(db, account.Id, "Junk", uid, 0.99);
        Mail("Junk", "Kundenservice", "service@konto-pruefung.example", "Ihr Konto wird gesperrt",
            "Ihr Konto wird in 24 Stunden gesperrt. Bestätigen Sie jetzt Ihre Daten über den folgenden Link.", 3, 2, false);
        MailStore.SetSpamScore(db, account.Id, "Junk", uid, 0.97);
    }

    // ---------------- DMS ----------------

    /// <summary>
    /// Drei erfundene Schreiben als PDF mit Text (keine Texterkennung nötig) und die Listen, nach denen das DMS sie selbst einordnet:
    /// Dokumentarten (Vorschlag), Korrespondenten und Tags mit Suchwörtern. Eingeordnet wird nach dem Start im Hintergrund.
    /// </summary>
    private static void Documents(UserSpace space, DateOnly today)
    {
        var db = space.Db;
        DmOffice.Dms.Dms.Ensure(db);
        DmOffice.Dms.Dms.CreateSuggestion(db, "de");
        // „Rechnung“ gleich über Wörter zuordnen, statt erst zu lernen
        if (DmOffice.Dms.Dms.Entries(db, DmOffice.Dms.Dms.Doctype).FirstOrDefault(e => e.Name == "Rechnung") is { } invoice)
            DmOffice.Dms.Dms.Update(db, invoice.Id, null, null, null, DmOffice.Dms.Dms.Any, "Rechnung Jahresabrechnung Beitragsrechnung", null);
        void Entry(string kind, string name, string words, string? color = null) =>
            DmOffice.Dms.Dms.Create(db, kind, name, null, color, DmOffice.Dms.Dms.Any, words, false);
        Entry(DmOffice.Dms.Dms.Correspondent, "Stadtwerke Musterstadt", "Stadtwerke");
        Entry(DmOffice.Dms.Dms.Correspondent, "Hausrat Versicherung AG", "Hausrat");
        Entry(DmOffice.Dms.Dms.Correspondent, "Autohaus Weber", "Autohaus");
        Entry(DmOffice.Dms.Dms.Tag, "Haus", "Abschlag Hausrat Wohnung", "#0F6CBD");
        Entry(DmOffice.Dms.Dms.Tag, "Auto", "Fahrzeug Inspektion", "#C50F1F");
        Entry(DmOffice.Dms.Dms.Tag, "Steuer", "Jahresabrechnung Beitrag", "#107C10");

        var de = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
        string D(int days) => today.AddDays(days).ToString("dd.MM.yyyy", de);
        Pdf("Stadtwerke Jahresabrechnung.pdf", "Stadtwerke Musterstadt", "Jahresabrechnung Strom", D(-12),
            ["Sehr geehrte Kundin, sehr geehrter Kunde,", "anbei erhalten Sie Ihre Jahresabrechnung für Strom.",
             "Verbrauch: 2.430 kWh", "Gesamtbetrag: 812,40 EUR", "Bereits gezahlte Abschläge: 780,00 EUR", "Nachzahlung: 32,40 EUR",
             "Ihr neuer monatlicher Abschlag beträgt 68,00 EUR.", "Mit freundlichen Grüßen", "Ihre Stadtwerke Musterstadt"]);
        Pdf("Hausrat Beitragsrechnung.pdf", "Hausrat Versicherung AG", "Beitragsrechnung Hausratversicherung", D(-30),
            ["Versicherungsschein-Nr. HR-204 518", "Für den Zeitraum vom 01.01. bis 31.12. berechnen wir den Jahresbeitrag",
             "Ihrer Hausratversicherung.",
             "Versicherte Wohnung: Lindenweg 12", "Jahresbeitrag: 96,00 EUR", "Der Betrag wird zum Monatsende abgebucht.",
             "Mit freundlichen Grüßen", "Hausrat Versicherung AG"]);
        Pdf("Autohaus Rechnung Inspektion.pdf", "Autohaus Weber", "Rechnung Inspektion", D(-2),
            ["Rechnung Nr. 2026-1187", "Fahrzeug: Kombi, amtl. Kennzeichen MS-DM 123", "Inspektion nach Herstellervorgabe: 189,00 EUR",
             "Bremsflüssigkeit erneuert: 49,90 EUR", "Summe netto: 200,76 EUR", "zzgl. 19 % MwSt.: 38,14 EUR", "Gesamtbetrag: 238,90 EUR",
             "Zahlbar innerhalb von 14 Tagen ohne Abzug.", "Vielen Dank für Ihren Auftrag!", "Autohaus Weber"]);

        void Pdf(string name, string sender, string title, string date, string[] lines)
        {
            // Standardschrift Helvetica, nichts eingebettet - ein paar KB statt rund 1 MB je Dokument (10 MB Kontingent)
            var text = new System.Text.StringBuilder();
            void Line(string font, int size, int x, int top, string t, string grey = "0 g") =>
                text.Append($"BT {grey} /{font} {size} Tf 1 0 0 1 {x} {842 - top} Tm ({PdfText(t)}) Tj ET\n");
            Line("F2", 16, 56, 70, sender);
            Line("F1", 9, 56, 140, "Demo Muster · Lindenweg 12 · 12345 Musterstadt", "0.35 g");
            Line("F1", 11, 400, 190, "Musterstadt, " + date);
            Line("F2", 13, 56, 230, title);
            var y = 270;
            foreach (var l in lines) { Line("F1", 11, 56, y, l); y += 22; }
            var objects = new[]
            {
                "<< /Type /Catalog /Pages 2 0 R >>",
                "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R /F2 5 0 R >> >> /Contents 6 0 R >>",
                "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
                "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>",
                $"<< /Length {text.Length} >>\nstream\n{text}endstream",
                $"<< /Title ({PdfText(title)}) /Author ({PdfText(sender)}) /Creator (dmOffice Demo) >>",
            };
            var pdf = new System.Text.StringBuilder("%PDF-1.4\n");
            var offsets = new List<int>();
            for (var i = 0; i < objects.Length; i++) { offsets.Add(pdf.Length); pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
            var xref = pdf.Length;
            pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
            foreach (var o in offsets) pdf.Append($"{o:D10} 00000 n \n");
            pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R /Info 7 0 R >>\nstartxref\n{xref}\n").Append("%EOF\n");
            using var output = new MemoryStream(System.Text.Encoding.ASCII.GetBytes(pdf.ToString()));
            DmOffice.Dms.DmsImport.ImportAsync(space, output, name, "application/pdf", new DmOffice.Dms.DmsImport.Info("upload"), CancellationToken.None)
                .GetAwaiter().GetResult();
        }
    }

    /// <summary>Text für einen PDF-String: Sonderzeichen maskiert, Umlaute als Oktalcode (WinAnsi = Latin-1 für diese Zeichen).</summary>
    private static string PdfText(string t)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in t)
            if (c is '\\' or '(' or ')') sb.Append('\\').Append(c);
            else if (c < 128) sb.Append(c);
            else if (c < 256) sb.Append('\\').Append(Convert.ToString(c, 8).PadLeft(3, '0'));
            else sb.Append('?');
        return sb.ToString();
    }
}
