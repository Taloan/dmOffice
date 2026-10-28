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
/// Demo-Konto mit erfundenen Mails, Terminen, Aufgaben und Notizen. Mailkonten, Mailversand, Freigabelinks, Einladungen,
/// Freigaben an andere sowie Passwort und Zwei-Faktor sind gesperrt. Zurückgesetzt wird von außen: Container stoppen,
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
        @"guests(/.*)?", @"emergency(/.*)?",
        @"(pim|notes|lists|tasks|calendar)/.*shares.*") + ")$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
        services.GetRequiredService<ServerConfig>().Update(s => s.DefaultFileQuotaMb = QuotaMb);
        var user = users.Create(Login, "Demo", Password, false);
        var space = services.GetRequiredService<Spaces>().Get(user.Id);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, space.Zone));
        Pim(space, today);
        NotesOf(space);
        Mails(space, today);
        Pictures(space, today);
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

    /// <summary>Zwei gezeichnete Bilder (keine Fotos von irgendwem): Strand im Abendlicht und eine Blumenwiese, klein genug fürs Kontingent.</summary>
    private static void Pictures(UserSpace space, DateOnly today)
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
    }

    // ---------------- Mails ----------------

    private static void Mails(UserSpace space, DateOnly today)
    {
        var db = space.Db;
        var account = new MailAccount { Name = "Demo-Postfach", Email = "demo@play.dmoffice.de", DisplayName = "Demo" };
        space.UpdateSettings(s => s.MailAccounts.Add(account));
        MailStore.ReplaceFolders(db, account.Id, [("INBOX", "INBOX", "/", "inbox", true), ("Gesendet", "Gesendet", "/", "sent", true),
            ("Papierkorb", "Papierkorb", "/", "trash", true)]);
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
    }
}
