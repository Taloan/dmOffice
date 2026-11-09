using System.Text;
using System.Text.RegularExpressions;
using DmOffice.Chat;
using DmOffice.Core;
using DmOffice.Notes;
using DmOffice.Pim;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace DmOffice.Tests;

/// <summary>
/// Pukkis Sprachkorpus: rund tausend Sätze und kurze Gespräche (Pukki/korpus-*.tsv) direkt durch Pukki.Answer - mit
/// Erwartung je Satz. Fragen dürfen nie etwas anlegen, Unsinn auch nicht. Was ein Satz anlegt, wird danach wieder
/// zurückgenommen, damit der Bestand für die nächsten Sätze gleich bleibt.
/// </summary>
/// <remarks>
/// Spalten (Tab): Gespräch (leer: Einzelsatz; gleiche Kennung: nacheinander, Pukki merkt sich den Zusammenhang),
/// Art, Satz, Erwartung. Erwartung: Teile mit „ ;; “ getrennt - +Text (kommt vor), -Text (kommt nicht vor),
/// ~Regex, !Regex (darf nicht passen), @neu (legt etwas an), @nichts (legt nichts an), @knopf:Beschriftung.
/// Arten ohne @: talk, zeit, frage, unsinn → @nichts; termin, aufgabe, notiz, einkauf, essen → @neu.
/// PUKKI_KORPUS=Pfad schreibt alle Antworten in eine Datei (zum Nachlesen).
/// </remarks>
[Collection("server")]
public class PukkiCorpusTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    private readonly ITestOutputHelper _out;
    public PukkiCorpusTests(ChatFixture fx, ITestOutputHelper output) { _fx = fx; _out = output; }

    private sealed record Line(string File, int No, string Dialog, string Kind, string Text, string Expect);

    private static readonly HashSet<string> ReadKinds = ["talk", "zeit", "frage", "unsinn"];
    private static readonly HashSet<string> WriteKinds = ["termin", "aufgabe", "notiz", "einkauf", "essen"];

    private static List<Line> Load()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Pukki");
        var lines = new List<Line>();
        foreach (var file in Directory.GetFiles(dir, "korpus-*.tsv").OrderBy(f => f, StringComparer.Ordinal))
        {
            var no = 0;
            foreach (var raw in File.ReadAllLines(file, Encoding.UTF8))
            {
                no++;
                if (raw.Length == 0 || raw.StartsWith('#')) continue;
                var c = raw.Split('\t');
                if (c.Length < 3) throw new InvalidDataException($"{Path.GetFileName(file)}:{no}: zu wenige Spalten");
                // \n im Satz steht für einen Zeilenumbruch
                lines.Add(new Line(Path.GetFileName(file), no, c[0].Trim(), c[1].Trim(), c[2].Replace("\\n", "\n"), c.Length > 3 ? c[3].Trim() : ""));
            }
        }
        return lines;
    }

    /// <summary>
    /// Der Bestand, auf den sich die Sätze beziehen: Termine (eigene und im geteilten Kalender „Familie“ von Ben), Aufgaben,
    /// Einkaufsliste „Einkaufen“ (Milch, Brot, 6 Eier; Möhren in der Vorschlagsliste), Essensplan mit Rezepten, Notiz, Kontakte.
    /// </summary>
    private void Seed(UserSpace space)
    {
        var db = space.Db;
        PimStore.EnsureDefaults(db);
        var now = Pukki.Now(space);
        var today = DateOnly.FromDateTime(now);
        var cal = Mail.Invites.DefaultCalendar(space);
        string Utc(DateOnly d, int h, int m = 0) => Dav.Ics.ToUtc(d.ToDateTime(new TimeOnly(h, m)), space.Zone).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        void Event(UserSpace owner, long calendar, string title, DateOnly d, int? h, int m = 0, string? location = null, List<AttendeeInput>? people = null)
        {
            var e = new EventInput { Calendar = calendar, Title = title, AllDay = h == null, Location = location, Attendees = people, Notify = false };
            if (h is int hh) { e.Start = Utc(d, hh, m); e.End = Utc(d, hh + 1, m); }
            else { e.Start = d.ToString("yyyy-MM-dd"); e.End = d.AddDays(1).ToString("yyyy-MM-dd"); }
            CalendarModel.SaveEvent(owner, e);
        }
        Event(space, cal.Id, "Zahnarzt Kontrolle", today.AddDays(1), 15);
        Event(space, cal.Id, "Friseur", today.AddDays(3), 10);
        Event(space, cal.Id, "Beethoven-Konzert", today.AddDays(4), 19, 30, "Stadthalle",
            [new AttendeeInput { Email = "anna@example.org", Name = "Anna" }, new AttendeeInput { Email = "ben@example.org", Name = "Ben" }]);
        Event(space, cal.Id, "Geburtstag Oma", today.AddDays(5), null);
        Event(space, cal.Id, "Elternabend", today.AddDays(9), 19);

        // Bens Kalender „Familie“, mit Anna geteilt: die Zahnreinigung kommt vor Annas eigenem Zahnarzttermin
        var ben = _fx.Space("ben");
        PimStore.EnsureDefaults(ben.Db);
        var family = PimStore.CreateCollection(ben.Db, PimStore.Calendar, "familie", "Familie", null, "VEVENT");
        _fx.Factory.Services.GetRequiredService<ShareStore>().Set(ben.User.Id, family.Id, [space.User.Id]);
        Event(ben, family.Id, "Zahnreinigung", today.AddDays(1), 9, 0, "Zahnarztpraxis am Markt");

        // Aufgaben (Standardliste), Einkaufsliste „Einkaufen“, Essensplan
        var tasks = PimStore.Collections(db, PimStore.Calendar).First(c => c.Components.Contains("VTODO") && Lists.TypeOf(db, c.Id) == Lists.Tasks);
        CalendarModel.SaveTask(space, new TaskInput { List = tasks.Id, Title = "Steuer machen", Due = today.ToString("yyyy-MM-dd") });
        CalendarModel.SaveTask(space, new TaskInput { List = tasks.Id, Title = "Rechnung bezahlen", Due = today.AddDays(-2).ToString("yyyy-MM-dd") });
        CalendarModel.SaveTask(space, new TaskInput { List = tasks.Id, Title = "Fahrrad reparieren" });
        var shop = PimStore.CreateCollection(db, PimStore.Calendar, "einkaufen", "Einkaufen", null, "VTODO");
        Lists.Update(db, shop.Id, Lists.Shop, null);
        foreach (var (name, qty, section) in new[] { ("Milch", (string?)null, "Kühlregal"), ("Brot", null, "Brot & Backwaren"), ("Eier", "6", "Kühlregal") })
            CalendarModel.SaveTask(space, new TaskInput { List = shop.Id, Title = name, Item = name, Qty = qty, Section = section });
        Lists.Learn(db, shop.Id, "Möhren", null, "Obst & Gemüse");

        // Rezepte: Lasagne (Zutaten nur zum Teil auf der Liste), Pfannkuchen (alle Zutaten auf der Liste)
        var notes = NotesDb.Of(space);
        string Recipe(string title, params string[] ingredients)
        {
            var id = notes.CreateNote(null);
            var lines = new[] { title, "Zutaten" }.Concat(ingredients).Concat(["Zubereitung", "Alles zusammen zubereiten."]).ToList();
            notes.SaveNote(id, "<div data-notizi=\"1\">" + string.Concat(lines.Select(l => $"<p>{l}</p>")) + "</div>", string.Join("\r\n", lines));
            return notes.GetNote(id)!.SyncId;
        }
        var lasagne = Recipe("Lasagne", "500 ml Milch", "250 g Lasagneplatten", "500 g Hackfleisch", "1 Päckchen Tomatensoße");
        var pancakes = Recipe("Pfannkuchen", "2 Eier", "250 ml Milch");

        var meal = PimStore.CreateCollection(db, PimStore.Calendar, "essensplan", "Essensplan", null, "VEVENT");
        CalendarKinds.Set(db, meal.Id, CalendarKinds.Meal);
        void Meal(string dish, string kind, DateOnly d, int h, string? recipe = null) =>
            CalendarModel.SaveEvent(space, new EventInput { Calendar = meal.Id, Title = dish, Categories = [kind], Busy = false, Start = Utc(d, h), End = Utc(d, h + 1), Recipe = recipe });
        Meal("Linsensuppe", "Mittagessen", today, 12);
        Meal("Spaghetti", "Abendessen", today.AddDays(1), 18);
        Meal("Pfannkuchen", "Mittagessen", today.AddDays(2), 12, pancakes);
        Meal("Lasagne", "Abendessen", PukkiParse.Parse("Samstag", now).Date!.Value, 18, lasagne);

        var n = notes.CreateNote(null);
        notes.SaveNote(n, "<div data-notizi=\"1\"><p>Ideen für den Urlaub</p><p>Ostsee, Fahrräder mitnehmen</p></div>", "Ideen für den Urlaub\r\nOstsee, Fahrräder mitnehmen");
        var book = PimStore.Collections(db, PimStore.AddressBook).First().Id;
        ContactModel.Save(space, new ContactDto { Book = book, DisplayName = "Hilde Berger", Given = "Hilde", Family = "Berger", Nickname = "Oma",
            Phones = [new TypedValue("home", "030 123456")], Birthday = today.AddDays(5).AddYears(-80).ToString("yyyy-MM-dd") });
        ContactModel.Save(space, new ContactDto { Book = book, DisplayName = "Dr. Weiß", Org = "Zahnarztpraxis am Markt",
            Emails = [new TypedValue("work", "praxis@zahnarzt-weiss.example")], Phones = [new TypedValue("work", "030 987654")] });
    }

    private static string Check(Line l, Pukki.Reply reply)
    {
        var text = reply.Text;
        var made = reply.Actions?.Any(a => a.Key == "undo") == true;
        var errors = new List<string>();
        // Erwartetes Datum (etwa „3.11.“) liegt heute, morgen oder übermorgen: Pukki sagt dann zu Recht „heute“, „morgen“, „übermorgen“
        static bool NearDate(string want, string text)
        {
            var m = Regex.Match(want.Trim(), @"^(\d{1,2})\.(\d{1,2})\.$");
            if (!m.Success) return false;
            var today = DateTime.Today;
            for (var days = 0; days <= 2; days++)
            {
                var d = today.AddDays(days);
                if (d.Day != int.Parse(m.Groups[1].Value) || d.Month != int.Parse(m.Groups[2].Value)) continue;
                // „morgen“ steckt auch in „übermorgen“ - Umlaut-Schreibweisen spielen so keine Rolle
                if (days == 0 ? text.Contains("heute", StringComparison.OrdinalIgnoreCase) : text.Contains("morgen", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
        var parts = l.Expect.Split(" ;; ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (!parts.Any(p => p is "@neu" or "@nichts"))
        {
            if (ReadKinds.Contains(l.Kind)) parts.Add("@nichts");
            else if (WriteKinds.Contains(l.Kind)) parts.Add("@neu");
        }
        foreach (var p in parts)
        {
            var ok = p[0] switch
            {
                '+' => text.Contains(p[1..], StringComparison.OrdinalIgnoreCase) || NearDate(p[1..], text),
                '-' => !text.Contains(p[1..], StringComparison.OrdinalIgnoreCase),
                '~' => Regex.IsMatch(text, p[1..], RegexOptions.IgnoreCase),
                '!' => !Regex.IsMatch(text, p[1..], RegexOptions.IgnoreCase),
                '@' when p == "@neu" => made,
                '@' when p == "@nichts" => !made,
                '@' when p.StartsWith("@knopf:") => reply.Actions?.Any(a => a.Label.Contains(p[7..], StringComparison.OrdinalIgnoreCase)) == true,
                _ => throw new InvalidDataException($"{l.File}:{l.No}: unbekannte Erwartung {p}"),
            };
            if (!ok) errors.Add(p);
        }
        return string.Join(" ", errors);
    }

    [Fact]
    public void Korpus()
    {
        var services = _fx.Factory.Services;
        var chat = services.GetRequiredService<ChatStore>();
        var space = _fx.Space("anna");
        // PUKKI_VERSATZ=Stunden: Pukkis Uhr verstellen - so lässt sich der Korpus an jedem Wochentag und spätabends prüfen
        Pukki.Shift = TimeSpan.FromHours(double.TryParse(Environment.GetEnvironmentVariable("PUKKI_VERSATZ"), System.Globalization.CultureInfo.InvariantCulture, out var h) ? h : 0);
        Seed(space);
        Pukki.Ensure(chat, space.User.Id);
        var room = chat.DirectRoom(space.User.Id, Pukki.Id)!.Value;
        var lines = Load();
        var failures = new List<string>();
        var report = new StringBuilder();
        var watch = System.Diagnostics.Stopwatch.StartNew();

        // Gespräche am Stück, Einzelsätze jeder für sich
        var groups = new List<List<Line>>();
        foreach (var l in lines)
            if (l.Dialog.Length > 0 && groups.Count > 0 && groups[^1][0].Dialog == l.Dialog) groups[^1].Add(l);
            else groups.Add([l]);

        var shopLists = PimStore.Collections(space.Db, PimStore.Calendar).Where(x => Lists.TypeOf(space.Db, x.Id) == Lists.Shop).Select(x => x.Id).ToList();
        HashSet<(long, string)> History() => shopLists.SelectMany(id => Lists.Get(space.Db, id).History.Select(h => (id, h.Name))).ToHashSet();
        var history = History();
        foreach (var g in groups)
        {
            Pukki.Reset(space.User.Id);
            var posted = new List<long>();
            foreach (var l in g)
            {
                Pukki.Reply reply;
                try { reply = Pukki.Answer(services, space, l.Text, chat, room); }
                catch (Exception e) { reply = new Pukki.Reply("FEHLER " + e.GetType().Name + ": " + e.Message); }
                posted.Add(chat.Post(Pukki.Id, room, reply.Text, refs: reply.Refs, actions: reply.Actions));
                var err = Check(l, reply);
                var buttons = reply.Actions is { Count: > 0 } a ? " [" + string.Join(" | ", a.Select(x => x.Label)) + "]" : "";
                report.Append($"{(err.Length > 0 ? "FAIL" : "ok  ")}\t{l.File}:{l.No}\t{l.Dialog}\t{l.Kind}\t{l.Text.Replace("\n", "\\n")}\t=> {reply.Text.Replace("\n", " / ")}{buttons}");
                if (err.Length > 0) report.Append($"\t!! {err}");
                report.AppendLine();
                if (err.Length > 0) failures.Add($"{l.File}:{l.No} „{l.Text}“ → „{reply.Text}“{buttons} verfehlt: {err}");
            }
            // Angelegtes zurücknehmen - der Bestand bleibt für die nächsten Sätze gleich
            var recent = chat.Messages(space.User.Id, room, limit: 50);
            foreach (var m in recent.Where(m => posted.Contains(m.Id) && m.Actions?.Any(a => a.Key == "undo") == true))
                Pukki.Act(services, chat, space, m, "undo");
            // Was die Einkaufslisten dabei gelernt haben (Vorschlagsliste), wieder vergessen
            foreach (var (id, name) in History().Except(history)) Lists.Forget(space.Db, id, name);
        }
        Pukki.Reset(space.User.Id);
        Pukki.Shift = TimeSpan.Zero;

        if (Environment.GetEnvironmentVariable("PUKKI_KORPUS") is { Length: > 0 } path) File.WriteAllText(path, report.ToString(), Encoding.UTF8);
        _out.WriteLine($"{lines.Count} Sätze in {groups.Count} Gruppen, {failures.Count} daneben, {watch.Elapsed.TotalSeconds:0.0} s");
        Assert.True(failures.Count == 0, $"{failures.Count} von {lines.Count} Sätzen daneben:\n" + string.Join("\n", failures));
    }
}
