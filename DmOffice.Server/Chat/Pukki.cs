using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using DmOffice.Core;
using DmOffice.Notes;
using DmOffice.Pim;

namespace DmOffice.Chat;

/// <summary>
/// Pukki (Stufe 5), der Assistent - zuerst ohne KI. Er ist kein Benutzerkonto, sondern ein fester Gesprächspartner mit
/// der Kennung "pukki": Jeder bekommt eine Unterhaltung mit ihm (blendet man sie aus, bleibt sie weg). Er versteht
/// Kurzbefehle (Termin, Aufgabe, Notiz, Überblick), beantwortet in Gruppen „@Pukki …“ aus dem Gruppen-Kalender bzw.
/// der Gruppen-Liste, schickt morgens den Tagesüberblick und erinnert an Termine (Einstellungen → Chat).
/// Wie er Sätze versteht: PukkiDialog.cs (Rückfragen, Bezüge, Korrekturen), PukkiTalk.cs (kleine Worte, Fragen,
/// Unsinn), PukkiRead.cs (Nachschauen), hier das Anlegen. Grundsatz: Fragen legen nie etwas an.
/// </summary>
public static partial class Pukki
{
    public const string Id = "pukki";
    public const string Name = "Pukki";

    /// <summary>Pukkis Antwort: Text, bei Suchantworten Verweise auf die Treffer (Stufe 5, Schritt 3), Knöpfe (Rückgängig, Auswahl).</summary>
    /// <param name="Unknown">Pukki wusste keine Antwort - mit KI fragt der Dienst dann Claude (PukkiAi)</param>
    public sealed record Reply(string Text, IReadOnlyList<ChatRef>? Refs = null, IReadOnlyList<ChatAction>? Actions = null, bool Unknown = false)
    {
        public static implicit operator Reply(string text) => new(text);
    }

    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");
    private static readonly ConcurrentDictionary<string, byte> Ensured = new();

    public const string Help =
        "Ich bin Pukki. Das kann ich schon:\n" +
        "• termin morgen 15 Uhr Zahnarzt – trage ich in deinen Kalender ein (auch „jeden Montag 18 Uhr Training“)\n" +
        "• aufgabe Milch kaufen (gern mit Tag: aufgabe Freitag Steuer) – kommt in deine Aufgaben\n" +
        "• notiz Ideen für den Urlaub – lege ich als Notiz an\n" +
        "• Milch, 6 Eier und Brot – kommt auf die Einkaufsliste\n" +
        "• abendessen Spaghetti (gern mit Tag: morgen mittags gibt es Suppe) – kommt in deinen Essensplan\n" +
        "• erinnere mich morgen um 9 an den Müll – kommt als Aufgabe in deine Liste, ich melde mich zur Uhrzeit; dazu „Welche Erinnerungen habe ich?“, „Erinnerung Müll auf 10 Uhr verschieben“, „Erinnerung Müll löschen“\n" +
        "• timer 20 Minuten (gern mit Namen: Pizza-Timer 15 Minuten) – ich melde mich, wenn er abgelaufen ist; dazu „Welche Timer laufen?“, „Timer um 5 Minuten verlängern“, „Timer abbrechen“\n" +
        "• Fragen wie „Was steht morgen an?“, „Was steht auf der Einkaufsliste?“, „Welche Aufgaben sind offen?“, „Wann ist der Zahnarzt?“ oder „Was gibt es heute zu essen?“\n" +
        "Passt etwas nicht, schreib einfach „nein“ oder zum Beispiel „mach 16 Uhr draus“.\n" +
        "In Gruppen: „@Pukki wer holt heute die Kinder?“ – ich schaue in den Gruppen-Kalender, „@Pukki Liste“ in die Gruppen-Liste.\n" +
        "Morgens schicke ich dir den Tagesüberblick und ich erinnere dich an Termine – einstellen unter Einstellungen → Chat.";

    /// <summary>Die Unterhaltung mit Pukki anlegen, wenn es sie noch nie gab - mit Begrüßung. Ausgeblendet bleibt ausgeblendet.</summary>
    public static void Ensure(ChatStore chat, string user, bool english = false)
    {
        if (!Ensured.TryAdd(user, 0)) return;
        if (chat.DirectRoom(user, Id) != null) return;
        var room = chat.OpenDirect(user, Id);
        chat.Post(Id, room, english ? "Hello! " + HelpEn : "Hallo! " + Help);
    }

    /// <summary>Unterhaltung mit Pukki, in der der Benutzer noch Mitglied ist - sonst null (ausgeblendet).</summary>
    public static long? RoomOf(ChatStore chat, string user) =>
        chat.DirectRoom(user, Id) is { } r && chat.IsMember(r, user) ? r : null;

    // ---------------- Gedächtnis ----------------

    /// <summary>
    /// Was Pukki sich je Benutzer merkt (30 Minuten): die offene Rückfrage - darauf beziehen sich „ja“, „nein“ und die
    /// nächste Antwort - und den Gesprächszusammenhang (Thema, Tag, Artikel, zuletzt Angelegtes), damit „Und morgen?“
    /// oder „Mach 16 Uhr draus“ verstanden werden. Was er angelegt hat, steht außerdem im Knopf „Rückgängig“.
    /// </summary>
    private sealed class Memory
    {
        /// <summary>Art der offenen Rückfrage: kind, when, what, shop-add, shop-more, shop-what, shop-typo, ingredients, reminder.</summary>
        public string? Asked;
        public string? Pending;
        public string? Suggest;
        public List<string>? Items;
        public string? ListOwner;
        public long ListId;
        public DateTime PendingAt;
        /// <summary>Eine offene Rückfrage fiel weg, weil etwas anderes kam - „hat sich erledigt“ danach bezieht sich noch auf sie.</summary>
        public DateTime? DroppedAt;

        /// <summary>Thema der letzten Antwort: day, meal, tasks, shop, when, contact, joke, mood, birthday, notes, unknown.</summary>
        public string? Topic;
        public DateOnly? Day;
        public string? Subject;
        public Func<Reply>? Why;
        public List<(string Dish, string? Recipe, DateOnly Day)>? Dishes;
        /// <summary>Der zuletzt besprochene Termin (für Erinnerung, „Ist das deiner?“, Kontaktdaten).</summary>
        public (string Owner, long Calendar, string Uri, string Start, string Title)? Event;
        public bool EventReminder, EventWritable, EventUpcoming;

        public bool JustMade;
        public string? LastKind;
        public string? LastSource;
        public List<Created>? LastMade;
        public int Jokes;
        public DateTime At = DateTime.UtcNow;
    }

    /// <summary>Etwas, das Pukki angelegt oder geändert hat - zum Zurücknehmen. Kind: event, task, note, done (abgehakt), reminder.</summary>
    private sealed record Created(string Kind, string Owner, long Collection, string? Uri, long Note, string Label, string? Extra = null);

    private static readonly ConcurrentDictionary<string, Memory> Memories = new();
    private static readonly TimeSpan Remember = TimeSpan.FromMinutes(30);

    /// <summary>Gedächtnis vergessen (Tests, Abmelden).</summary>
    internal static void Reset(string user) => Memories.TryRemove(user, out _);

    private static Memory MemoryOf(string user)
    {
        var mem = Memories.GetOrAdd(user, _ => new Memory());
        if (DateTime.UtcNow - mem.At > Remember) Memories[user] = mem = new Memory();
        return mem;
    }

    /// <summary>Was zu einer Antwort gehört: Dienste, Benutzer, Gedächtnis, Uhrzeit, Unterhaltung.</summary>
    private sealed record Ctx(IServiceProvider Services, UserSpace Space, Memory Mem, DateTime Now, ChatStore? Chat, long Room)
    {
        public DateOnly Today => DateOnly.FromDateTime(Now);
    }

    // ---------------- Kurzbefehle und freie Sätze ----------------

    /// <summary>Antwort auf eine Nachricht in der eigenen Unterhaltung mit Pukki.</summary>
    public static Reply Answer(IServiceProvider services, UserSpace space, string text, ChatStore? chat = null, long room = 0)
    {
        var mem = MemoryOf(space.User.Id);
        lock (mem)
        {
            if (mem.Asked != null && DateTime.UtcNow - mem.PendingAt > Remember) ClearAsk(mem);
            var c = new Ctx(services, space, mem, Now(space), chat, room);
            Reply reply;
            try { reply = Respond(c, Normalize(text)); }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or KeyNotFoundException)
            {
                reply = "Das hat nicht geklappt: " + e.Message;
            }
            Note(mem, reply);
            mem.At = DateTime.UtcNow;
            return reply;
        }
    }

    /// <summary>Der Weg durch einen Satz - die Reihenfolge ist wichtig: erst die offene Rückfrage, dann Zurücknehmen und
    /// Korrekturen, Befehle, kleine Worte, Bezüge, Sätze in Befehlsform, Nachschauen; Fragen legen nie etwas an.</summary>
    /// <summary>Wissen von Pukki erbeten statt etwas aufzutragen: „Du wirst doch wissen …“, „kannst du recherchieren …“, „weißt du …“.</summary>
    /// <summary>„Haben wir schon erledigt“, „hat sich erledigt“, „vergiss es“: die offene Rückfrage fallen lassen (nicht „ist erledigt“ - das hakt ab).</summary>
    private static readonly Regex DropAskRx = new(
        @"^(?:(?:das\s+)?(?:hat\s+sich|haben\s+wir|hab(?:e)?\s+ich|ist)\s+(?:das\s+)?(?:schon|bereits)\s+erledigt|(?:das\s+)?hat\s+sich\s+erledigt|(?:schon|bereits)\s+erledigt|vergiss\s+(?:es|das)|(?:brauch(?:e|t|en)?\s+(?:ich|wir)?\s*|ist\s+)nicht\s+mehr\s+nötig|brauch(?:e|en)?\s+(?:ich|wir)\s+nicht\s+mehr)\b[\s.!]*$",
        RegexOptions.IgnoreCase);

    /// <summary>„Recherchiere …“, „Such im Internet nach …“, „Google …“, „Finde heraus …“: im Netz nachsehen lassen - kein Auftrag.</summary>
    private static readonly Regex ResearchRx = new(
        @"^(?:bitte\s+|pukki,?\s+)?(?:recherchier(?:e)?|google|googel(?:e)?|such(?:e)?\s+(?:mal\s+|bitte\s+|doch\s+)*(?:im\s+(?:internet|netz|web)|online)|schau\s+(?:mal\s+|bitte\s+|doch\s+)*(?:im\s+(?:internet|netz|web)|online)|find(?:e)?\s+(?:mal\s+|bitte\s+)*(?:heraus|raus))\b",
        RegexOptions.IgnoreCase);

    /// <summary>Karte erbeten: „Zeig mir X auf der Karte“, „Karte von X“, „Erstelle einen Kartenausschnitt“ - nur eindeutig,
    /// „Karte für Oma kaufen“ bleibt ein Auftrag.</summary>
    private static readonly Regex MapRx = new(
        @"\bkartenausschnitt|\b(?:land|straßen|strassen|wander|übersichts|uebersichts)karte\b|\bstadtplan\b|\bauf\s+(?:der|einer)\s+(?:land)?karte\b|\b(?:zeig|zeige|erstell|erstelle|mach|mache)\b.*\bkarte\b|^\s*(?:eine\s+)?karte\s+(?:von|vom|zu|zum|zur|des|der)\b",
        RegexOptions.IgnoreCase);
    private static readonly Regex MapPlaceRx = new(
        @"\bkarte\s+(?:von|vom|für|fuer|zu|zum|zur|der|des|über|ueber)\s+(?<p>[^?!.]+)|\b(?:zeig|zeige)\s+(?:mir\s+|uns\s+)?(?<p>[^?!.]+?)\s+auf\s+(?:der|einer)\s+(?:land)?karte|\bwo\s+(?:liegt|ist)\s+(?<p>[^?!.]+?)\s+auf\s+der\s+karte",
        RegexOptions.IgnoreCase);

    /// <summary>
    /// Karte: mit genanntem Ort ein Link auf OpenStreetMap (geht ohne KI); ohne Ort („Erstelle einen Kartenausschnitt“) weiß die KI
    /// aus dem Gespräch, welcher Ort gemeint ist, und antwortet selbst mit einem Kartenlink (siehe PukkiAi).
    /// </summary>
    private static Reply Map(Ctx c, string t)
    {
        var m = MapPlaceRx.Match(t);
        var place = m.Success ? Regex.Replace(m.Groups["p"].Value.Trim(), @"^(?:der|die|das|den|dem|ein|eine|einen|mal)\s+", "", RegexOptions.IgnoreCase).Trim() : "";
        if (place.Length < 2 || Regex.IsMatch(place, @"^(?:es|das|dort|da|ihn|sie|ihm|hier|den\s+ort|die\s+stelle)$", RegexOptions.IgnoreCase))
            return Unknown(c, t);
        c.Mem.Topic = "map";
        var url = "https://www.openstreetmap.org/search?query=" + Uri.EscapeDataString(place);
        return $"Hier auf der Karte: [{place}]({url})";
    }

    private static readonly Regex AskKnowledgeRx = new(@"\b(du|wirst|kannst|könntest|koenntest|solltest)\s+(?:\w+\s+){0,3}?(wissen|recherchieren|nachschauen|nachsehen|herausfinden|rausfinden|googeln|nachgucken|nachlesen)\b|\b(weißt|weisst)\s+du\b", RegexOptions.IgnoreCase);

    private static Reply Respond(Ctx c, string t)
    {
        var mem = c.Mem;
        if (t.Length == 0) return "Ja? Ich bin da – schreib mir einfach, was ansteht.";
        // Antwort auf „Wie geht's?“ („super, und dir?“) vor allem anderen
        if (mem.Topic == "mood" && Talk(c, t) is { } mood) return mood;

        // Begrüßung, Dank oder Anrede vorweg: „Hallo Pukki, was steht heute an?“
        if (Lead(t) is { } lead)
        {
            if (lead.Rest.Length == 0) return Talk(c, t) ?? lead.Answer;
            var inner = Respond(c, lead.Rest);
            return lead.Prefix.Length == 0 || inner.Text.StartsWith(lead.Prefix.TrimEnd(' ', '!')) ? inner : inner with { Text = lead.Prefix + inner.Text };
        }

        // Die offene Rückfrage zuerst - „ja“ und „nein“ beziehen sich immer auf sie; „hat sich erledigt“ lässt sie fallen,
        // auch wenn zwischendurch etwas anderes kam
        if (DropAskRx.IsMatch(t) && (mem.Asked != null || mem.DroppedAt is { } dropped && DateTime.UtcNow - dropped < Remember))
        {
            ClearAsk(mem);
            mem.DroppedAt = null;
            return "Gut, dann lasse ich das.";
        }
        if (mem.Asked != null)
        {
            if (OnPending(c, t) is { } answered) return answered;
            ClearAsk(mem);
            mem.DroppedAt = DateTime.UtcNow;
        }

        // Timer vor Korrekturen: „Timer auf 5 Minuten ändern“ betrifft keinen Eintrag, „Welche Timer laufen?“ nicht das Laufen (PukkiTimer.cs)
        if (TimerReply(c, t) is { } timer) return timer;
        // Erinnerungen zeigen, löschen, verschieben (PukkiReminders.cs)
        if (ReminderReply(c, t) is { } reminders) return reminders;

        // Zurücknehmen und Korrekturen
        if (UndoRx.IsMatch(t)) return UndoLast(c.Services, c.Chat, c.Space, c.Room, mem);
        if (NoRx.IsMatch(t)) return mem.JustMade ? UndoLast(c.Services, c.Chat, c.Space, c.Room, mem) : "Okay.";
        if (Correct(c, t) is { } fixd) return fixd;
        // „Ist erledigt“, „Blumen gießen erledigt“: fällige Aufgabe abhaken
        if (BareDoneRx.Match(t) is { Success: true } dm) return DoneSaid(c, dm.Groups["what"].Value.Trim());
        // „Als Notiz speichern“: die letzte Antwort (etwa ein Rezept von der KI) aufheben
        if (SaveLast(c, t) is { } saved) return saved;

        // Ausdrückliche Befehle (auch vertippt: „temin morgen 9 Uhr …“)
        var (word, rest) = Split(t);
        // Kurzbefehle aus einem Buchstaben („a Oma anrufen“) nur mit echtem Inhalt - „a b c“ ist kein Auftrag
        if (word.Length == 1 && char.IsLetter(word[0]) && rest.Length > 0 && Tokens(rest).All(x => x.Length <= 2)) word = "";
        switch (CommandWord(word))
        {
            case "hilfe" or "help" or "?": return L(Help, HelpEn);
            case "termin" or "event" or "appointment" or "t":
                // „Termin beim Zahnarzt vereinbaren“ ist eine Aufgabe
                if (Regex.IsMatch(rest, @"\b(vereinbaren|ausmachen|machen|absagen|verschieben|buchen|besorgen|klären|abstimmen)$", RegexOptions.IgnoreCase) && PukkiParse.Parse(rest, c.Now) is { Date: null, Start: null })
                    return Do(c.Services, c.Space, mem, "task", t, c.Now);
                return Do(c.Services, c.Space, mem, "event", rest, c.Now);
            case "aufgabe" or "task" or "todo" or "a":
                if (rest.Length == 0 && word is "todo" or "todos") break;
                return Do(c.Services, c.Space, mem, "task", rest, c.Now);
            case "notiz" or "note" or "n": return Do(c.Services, c.Space, mem, "note", rest, c.Now);
            case "einkauf" or "einkaufen" or "einkaufsliste" or "shopping" or "einkaufszettel":
                return rest.Length == 0 || IsQuestion(rest) || Regex.IsMatch(rest, @"^(zeigen|anzeigen|zeig|bitte|vorlesen|ansehen|anschauen|list|liste|öffnen)[\s?!.]*$", RegexOptions.IgnoreCase)
                    ? ShowShop(c) : Do(c.Services, c.Space, mem, "shop", rest, c.Now);
            case "essen" or "essensplan" or "meal": return Do(c.Services, c.Space, mem, "meal", rest, c.Now);
        }

        // Fragen nach Mails (Version 2): nur mit Erlaubnis, sonst fragt Pukki danach
        if (MailQuestion(c, t) is { } mail) return mail;
        // Kleine Worte: Begrüßung, Dank, Gefühle, Uhrzeit, Rechnen …
        if (Talk(c, t) is { } talk) return talk;
        // Bezüge auf das Gespräch: „Und morgen?“, „Warum nicht?“, „Und Brot auch“
        if (FollowUp(c, t) is { } follow) return follow;
        // „Du wirst doch wissen …“, „Weißt du, wann …“: eine Bitte um Wissen - nachschauen oder ehrlich sagen, nie als Auftrag umschreiben
        if (AskKnowledgeRx.IsMatch(t)) return Read(c, t) ?? Unknown(c, t);
        // „Recherchiere wo …“, „Such im Internet …“: im Netz nachsehen (mit KI) - nie als Aufgabe notieren
        if (ResearchRx.IsMatch(t)) return Unknown(c, t);
        // „Zeig mir X auf der Karte“, „Erstelle einen Kartenausschnitt“: Kartenlink statt Notiz oder Aufgabe
        if (MapRx.IsMatch(t)) return Map(c, t);
        // Sätze in Befehlsform: „Schreib Käse auf die Liste“, „Kannst du morgen 15 Uhr Zahnarzt eintragen?“
        if (Rewrite(c, t) is { } rewritten) return rewritten;
        // Nachschauen: Einkaufsliste, Aufgaben, Termine, Essen, Geburtstage, Kontakte
        if (Read(c, t) is { } read) return read;
        // Eine Frage, auf die Pukki keine Antwort hat: ehrlich sagen - nie etwas anlegen
        if (IsQuestion(t)) return Unknown(c, t);
        // Unsinn, Tastaturgewitter, nur Zeichen
        if (Nonsense(c, t) is { } nonsense) return nonsense;

        return Understand(c.Services, c.Space, mem, t, c.Now);
    }

    private static string Hello(DateTime now) => now.Hour is >= 5 and < 11 ? "guten Morgen" : now.Hour is >= 18 and < 23 ? "guten Abend" : "hallo";

    /// <summary>Die Begrüßung erwidern: „Servus“ bekommt „Servus“, „Guten Abend“ ein „Guten Abend“, sonst nach der Uhrzeit.</summary>
    private static string Greet(string said, DateTime now)
    {
        var s = said.ToLowerInvariant();
        return s.Contains("guten morgen") || s.Contains("gutn morgen") || s.Contains("good morning") ? "Guten Morgen"
            : s.Contains("guten abend") || s.Contains("good evening") ? "Guten Abend"
            : s.Contains("guten tag") ? "Guten Tag"
            : s.StartsWith("servus") || s.StartsWith("servas") || s.StartsWith("griaß") || s.StartsWith("grias") ? "Servus"
            : s.StartsWith("moin") ? "Moin"
            : s.Contains("grüß gott") || s.Contains("grüss gott") ? "Grüß Gott"
            : s.StartsWith("grüezi") ? "Grüezi"
            : Cap(Hello(now));
    }

    /// <summary>
    /// Ein freier Satz ohne Befehlswort: mit Uhrzeit ein Termin (mit „bis“ oder Erledigungswort eine Aufgabe), mit Datum je
    /// nach Wort Termin oder Aufgabe, Lebensmittel und Haushaltsartikel auf die Einkaufsliste, Erledigungswörter als
    /// Aufgabe. Sonst fragt Pukki nach. Fragen kommen hier nie an.
    /// </summary>
    private static Reply Understand(IServiceProvider services, UserSpace space, Memory mem, string t, DateTime now)
    {
        var w = PukkiParse.Parse(t, now);
        var words = PukkiWords.Words(w.Title);
        var lower = t.ToLowerInvariant();
        var today = DateOnly.FromDateTime(now);
        var task = words.Any(x => PukkiWords.In(x, PukkiWords.TaskWords));
        var evt = words.Any(x => PukkiWords.In(x, PukkiWords.EventWords));
        // „bis Freitag Steuer“ - aber nicht „9:30 bis 11 Elternabend“ (das „bis“ gehört dort zur Uhrzeit)
        var until = Regex.IsMatch(w.Title, @"\b(bis|until|by|spätestens|spaetestens)\b", RegexOptions.IgnoreCase) || Regex.IsMatch(lower, @"\b(spätestens|spaetestens)\b");

        // Nur ein Tag: Überblick („morgen“, „Freitag“)
        if (words.Count == 0 && w.Date != null && w.Start == null)
            return DayOverview(services, space, mem, w.Date.Value, today);

        // Mahlzeiten: „abendessen Spaghetti“, „morgen mittag gibt es Linsensuppe“ - in den Essensplan
        if (IsMeal(space, t)) return Do(services, space, mem, "meal", t, now);

        // Einkauf: alle Teile sind Lebensmittel oder Haushaltsartikel (oder stehen schon in der Einkaufsliste)
        if (w.Start == null && w.Date == null && ShopItems(space, t) is { Count: > 0 } items && !(task && items.All(i => i.Near != null)))
            return AddShop(services, space, items, t, mem: mem);

        // Langer oder mehrzeiliger Text (Brief, Kopiertes): eher eine Notiz - erst fragen
        if (w.Start == null && (t.Contains('\n') || t.Length > 100)) return Ask(mem, t, "note", L("Soll ich das als Notiz anlegen?", "Should I save that as a note?"), "note", "task", "event");

        // „Gestern war ich im Kino“ erzählt etwas - Vergangenes trägt Pukki nicht von sich aus ein
        if (w.Date is { } past && past < today)
            return "Das liegt in der Vergangenheit – das trage ich nicht von mir aus ein. Soll es trotzdem in den Kalender, schreib „termin“ davor.";

        // „Ben soll den Müll rausbringen“ (Version 2): eine Aufgabe für jemanden
        if (SplitAssignee(t).Who is { } who && !Regex.IsMatch(who, "^(was|wer|wie|wo|wann|es|das|man|ich)$", RegexOptions.IgnoreCase) && !evt)
            return Do(services, space, mem, "task", t, now);
        if (w.Start != null)
            return Do(services, space, mem, until || (task && !evt) ? "task" : "event", t, now);
        // Eine Aussage („Der Himmel ist blau“, „Ich mag Pizza“, „Heute ist ein schöner Tag“): zuhören statt anlegen
        if (!task && !evt && Statement(t))
            return "Verstehe. 🙂 Wenn ich mir das merken soll, schreib „notiz“ davor – oder sag mir, was ich für dich tun kann.";
        if (w.Date != null)
        {
            if (until || (task && !evt)) return Do(services, space, mem, "task", t, now);
            if (evt || w.Repeat != null || w.Until != null) return Do(services, space, mem, "event", t, now);
            if (w.Title.Length == 0) return DayOverview(services, space, mem, w.Date.Value, today);
            return Ask(mem, t, "event", SpeaksEnglish ? $"Should I add “{Cap(w.Title)}” as an event for {DayWords(w.Date.Value, today)}?" : $"Soll ich „{Cap(w.Title)}“ für {PukkiParse.DayText(w.Date.Value, today)} als Termin eintragen?", "event", "task", "note");
        }
        if (task) return Do(services, space, mem, "task", t, now);
        if (evt) return AskWhen(mem, t, Cap(w.Title));
        if (t.Length > 60 || t.Contains('\n')) return Ask(mem, t, "note", L("Soll ich das als Notiz anlegen?", "Should I save that as a note?"), "note", "task", "event");
        return Ask(mem, t, "task", SpeaksEnglish ? $"Should I add “{Cap(t.TrimEnd('.', '!'))}” as a task?" : $"Soll ich „{Cap(t.TrimEnd('.', '!'))}“ als Aufgabe notieren?", "task", "event", "note", "shop");
    }

    /// <summary>Rückfrage: merken und die möglichen Antworten als Knöpfe (Text geht weiter: „ja“, „aufgabe“ …).</summary>
    private static Reply Ask(Memory mem, string text, string? suggest, string question, params string[] kinds)
    {
        SetAsk(mem, "kind", text, suggest);
        if (kinds.Length == 0) return question;
        var actions = kinds.Select((k, i) => new ChatAction("do" + i, KindLabel(k), Pack(new ActData(Kind: k, Text: text)))).ToList();
        actions.Add(new ChatAction("do" + kinds.Length, L("Nein", "No"), Pack(new ActData(Kind: "none"))));
        return new Reply(question, Actions: actions);
    }

    /// <summary>„Wann ist der Zahnarzt?“ - die nächste Antwort mit Tag oder Uhrzeit macht daraus einen Termin.</summary>
    private static Reply AskWhen(Memory mem, string text, string title, bool remind = false)
    {
        SetAsk(mem, "when", text, remind ? "remind" : null);
        var question = remind
            ? L($"Wann soll ich dich an „{title}“ erinnern? Schreib mir zum Beispiel „morgen um 9“.", $"When should I remind you about “{title}”? For example: “tomorrow at 9”.")
            : L($"Wann ist „{title}“? Schreib mir zum Beispiel „morgen 15 Uhr“ – oder ich notiere es ohne Termin als Aufgabe.", $"When is “{title}”? For example: “tomorrow 3pm” – or I'll add it as a task without a date.");
        return remind ? question : new Reply(question, Actions: [new ChatAction("do0", L("Als Aufgabe", "As task"), Pack(new ActData(Kind: "task", Text: text))), new ChatAction("do1", L("Nein", "No"), Pack(new ActData(Kind: "none")))]);
    }

    /// <summary>Anlegen - ohne Ziel dort, wo Pukki es sonst hinlegt (to: gewählter Kalender bzw. gewählte Liste).</summary>
    private static Reply Do(IServiceProvider services, UserSpace space, Memory mem, string kind, string rest, DateTime now, Target? to = null) => kind switch
    {
        "event" => NewEvent(services, space, mem, rest, now, to),
        "task" => NewTask(services, space, rest, now, to),
        "note" => NewNote(services, space, rest),
        "meal" => NewMeal(services, space, rest, now, to),
        _ => ShopItems(space, rest, force: true) is { Count: > 0 } items ? AddShop(services, space, items, rest, to, mem) : "Was soll auf die Einkaufsliste? Zum Beispiel: Milch, 6 Eier, Brot",
    };

    /// <summary>Zurücknehmen, was ein Knopf „Rückgängig“ trägt - die Namen dessen, was noch da war.</summary>
    private static List<string> UndoItems(IServiceProvider services, IEnumerable<Created>? made)
    {
        var spaces = services.GetRequiredService<Spaces>();
        var done = new List<string>();
        foreach (var c in made ?? [])
        {
            try
            {
                var owner = spaces.Get(c.Owner);
                if (c.Kind == "note") NotesDb.Of(owner).TrashNote(c.Note);
                else if (c.Kind == "done") Reopen(owner, c);
                else if (c.Kind == "reminder") SetReminder(owner, c.Collection, c.Uri!, c.Extra!, null);
                else if (PimStore.Find(owner.Db, c.Collection) is { } coll && c.Uri != null) PimStore.Delete(owner.Db, coll, c.Uri);
                done.Add(c.Label);
            }
            catch (Exception) { /* schon weg */ }
        }
        return done;
    }

    /// <summary>Titel säubern: Artikel und „zu“ vor dem Verb weg, groß anfangen („die Blumen zu gießen“ → „Blumen gießen“).</summary>
    private static string CleanTitle(string title)
    {
        var s = Regex.Replace(title.Trim(), @"^(?:den|die|das|dem|der|ins|beim|bei der|bei dem|einen|eine|ein)\s+", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s+zu\s+(\p{L}+(?:en|ern|eln))$", " $1", RegexOptions.IgnoreCase);
        // „rauszubringen“ → „rausbringen“
        s = Regex.Replace(s, @"\b(raus|rein|ab|an|auf|aus|ein|mit|nach|vor|weg|zurück|her|hin|runter|rüber|fest|los|um|durch)zu(\p{L}+en)$", "$1$2", RegexOptions.IgnoreCase);
        s = s.Trim().TrimEnd('.', '!', ',', ';', ':').Trim();
        return Cap(s);
    }

    private static Reply NewEvent(IServiceProvider services, UserSpace space, Memory mem, string rest, DateTime now, Target? to, string? uri = null, int? reminder = null)
    {
        var w = PukkiParse.Parse(rest, now);
        // „einen Termin beim Zahnarzt“ → „Zahnarzt“
        var title = CleanTitle(Regex.Replace(w.Title, @"^(?:(?:einen|einem|eine|ein)\s+)?termin\s+(?:beim|bei der|bei dem|bei|zum|zur)\s+", "", RegexOptions.IgnoreCase));
        if (title.Length == 0)
        {
            if (w.Date == null && w.Start == null) return L("Wie heißt der Termin? Zum Beispiel: termin morgen 15 Uhr Zahnarzt", "What is the event called? For example: appointment tomorrow 3pm dentist");
            SetAsk(mem, "what", rest, reminder != null ? "remind" : null);
            return L("Wie heißt der Termin? Schreib mir einfach den Namen, zum Beispiel „Zahnarzt“.", "What is the event called? Just write the name, for example “Dentist”.");
        }
        if (w.Date == null && w.Start == null) return AskWhen(mem, rest, title, reminder != null);
        var today = DateOnly.FromDateTime(now);
        // Nur eine Uhrzeit: heute, ist sie schon vorbei, morgen
        var day = w.Date ?? (w.Start!.Value < TimeOnly.FromDateTime(now) ? today.AddDays(1) : today);
        // Gelernt (👍, „Anderer Kalender“): „Zahnarzt“ gehoert bei diesem Benutzer in „Gesundheit“
        var target = to ?? (uri == null ? Preferred(services, space, "event", title) : null) ?? new Target(space, Mail.Invites.DefaultCalendar(space), false);
        var cal = target.Coll;
        var input = new EventInput { Calendar = cal.Id, Title = title, AllDay = w.Start == null, Uri = uri, RRule = uri == null ? w.Repeat : null, Reminder = reminder, Notify = false };
        if (w.Start is { } st)
        {
            var start = day.ToDateTime(st);
            var end = w.End is { } en ? day.ToDateTime(en) : start.AddHours(1);
            input.Start = Iso(Dav.Ics.ToUtc(start, space.Zone));
            input.End = Iso(Dav.Ics.ToUtc(end, space.Zone));
        }
        else { input.Start = day.ToString("yyyy-MM-dd"); input.End = (w.Until is { } u && u > day ? u : day).AddDays(1).ToString("yyyy-MM-dd"); }
        // Art (Schritt 2): als Kategorie, damit sie auch in anderen Kalendern ankommt
        var art = PukkiArts.Classify(title);
        if (art != null) input.Categories = [art.De];
        var id = CalendarModel.SaveEvent(target.Owner, input);
        var made = new List<Created> { new("event", target.Owner.User.Id, cal.Id, id[(id.IndexOf('/') + 1)..], 0, L("Termin „" + title + "“", "event “" + title + "”")) };
        var when = w.Until is { } until && until > day && w.Start == null
            ? $"{DayWords(day, today)}{L(" bis ", " to ")}{DayWords(until, today)}"
            : DayWords(day, today) + (w.Start is { } s ? ", " + s.ToString("HH:mm") + (w.End is { } e ? "–" + e.ToString("HH:mm") : "") : L(" (ganztägig)", " (all day)"));
        var what = $"{(art != null ? art.Icon + " " : "")}{title}";
        // Serie: „jeden Montag um 18:00 Training, erstmals Montag, 26.10.“
        var artName = art == null ? "" : $" ({L(art.De, art.En)})";
        var text = w.Repeat != null && uri == null
            ? SpeaksEnglish
                ? $"Added: {what}, repeating{(w.Start is { } rsEn ? " at " + rsEn.ToString("HH:mm") : "")}, first on {DayWords(day, today)} – in calendar “{cal.Name}”{artName}."
                : $"Eingetragen: {w.RepeatText ?? "regelmäßig"}{(w.Start is { } rs ? " um " + rs.ToString("HH:mm") : "")} {what}, erstmals {PukkiParse.DayText(day, today)} – im Kalender „{cal.Name}“{artName}."
            : SpeaksEnglish
                ? $"{(uri == null ? "Added" : "Changed")}: {when} {what} – in calendar “{cal.Name}”{artName}."
                : $"{(uri == null ? "Eingetragen" : "Geändert")}: {when} {what} – im Kalender „{cal.Name}“{artName}.";
        if (reminder != null)
            text += ChatSettings.Of(space.Db).PukkiReminders
                ? SpeaksEnglish
                    ? $" I'll remind you {(w.Start is { } rEn ? "at " + rEn.ToString("HH:mm") : "in the morning")}."
                    : $" Ich erinnere dich {(w.Start is { } r ? "um " + r.ToString("HH:mm") + " Uhr" : "am Morgen")}."
                : L(" Erinnerungen sind bei dir ausgeschaltet – einschalten kannst du sie unter Einstellungen → Chat.", " Your reminders are switched off – you can turn them on under Settings → Chat.");
        return Made(text, made, "event", rest, uri == null ? Targets(services, space, "event").Count : 0);
    }

    /// <summary>Jemand hat dir eine Aufgabe zugewiesen (Version 2): Pukki sagt es dir, mit der Aufgabe zum Antippen.</summary>
    public static void TaskAssigned(IServiceProvider services, string by, string to, string owner, long collection, string uri, string title, string list)
    {
        var chat = services.GetRequiredService<ChatStore>();
        var users = services.GetRequiredService<UserStore>();
        if (users.ById(to) == null) return;
        Ensure(chat, to);
        var room = chat.DirectRoom(to, Id) ?? chat.OpenDirect(to, Id);
        var who = users.ById(by)?.DisplayName ?? "Jemand";
        chat.Post(Id, room, $"{who} hat dir die Aufgabe „{title}“ zugewiesen – in „{list}“.",
            refs: [new ChatRef(ChatRef.TypeTask, owner, title, Mode: ChatRef.Show, Collection: collection, Item: uri)]);
    }

    /// <summary>„Ben soll den Müll rausbringen“, „für Ben: Müll rausbringen“, „Müll rausbringen – für Ben“: wer, und der Rest.</summary>
    internal static (string? Who, string Text) SplitAssignee(string text)
    {
        var m = Regex.Match(text, @"^(?<who>[\p{L}][\p{L}\-\.]*)\s+(?:soll|sollte|muss|müsste|kann)\s+(?<rest>.+)$", RegexOptions.IgnoreCase);
        if (!m.Success) m = Regex.Match(text, @"^(?:aufgabe\s+)?(?:für|fuer)\s+(?<who>[\p{L}][\p{L}\-\.]*)\s*[:,]?\s+(?<rest>.+)$", RegexOptions.IgnoreCase);
        if (!m.Success) m = Regex.Match(text, @"^(?<rest>.+?)\s*(?:[-–:,]\s*)?(?:für|fuer)\s+(?<who>[\p{L}][\p{L}\-\.]*)\s*$", RegexOptions.IgnoreCase);
        return m.Success ? (m.Groups["who"].Value, m.Groups["rest"].Value) : (null, text);
    }

    /// <summary>Meint das Wort diesen Benutzer? Vorname, ganzer Name oder Anmeldename.</summary>
    private static bool Matches(UserRecord u, string word) =>
        string.Equals(u.Login, word, StringComparison.OrdinalIgnoreCase) || string.Equals(u.DisplayName, word, StringComparison.OrdinalIgnoreCase)
        || string.Equals(u.DisplayName.Split(' ')[0], word, StringComparison.OrdinalIgnoreCase);

    private static Reply NewTask(IServiceProvider services, UserSpace space, string rest, DateTime now, Target? to, string? uri = null)
    {
        // Zuweisen (Version 2): „Ben soll …“ - nur an jemanden, der die Liste sieht
        var (whoWord, restOnly) = SplitAssignee(rest);
        UserRecord? assignee = null;
        if (whoWord != null)
        {
            var self = Regex.IsMatch(whoWord, "^(ich|mich|mir|me|i)$", RegexOptions.IgnoreCase);
            var listRef = to != null ? new ListRef(to.Owner, to.Coll.Id, to.Coll.Id, null) : null;
            var people = listRef != null ? SharedLists.People(services, listRef) : new List<UserRecord> { space.User };
            assignee = self ? space.User : people.FirstOrDefault(u => Matches(u, whoWord));
            if (assignee != null) rest = restOnly;
            else if (!self && services.GetRequiredService<UserStore>().All().Any(u => Matches(u, whoWord)))
                return listRef == null
                    ? $"{whoWord} kann ich nur in einer Gruppe etwas zuweisen – dort gibt es eine gemeinsame Liste. Hier lege ich Aufgaben nur für dich an."
                    : $"{whoWord} sieht die Liste „{to!.Coll.Name}“ nicht – zuweisen kann ich nur an {JoinAnd(people.Select(p => p.DisplayName).ToList())}.";
        }
        var w = PukkiParse.Parse(rest, now);
        // „Rechnung bis Freitag bezahlen“ - das „bis“ gehörte zum Tag
        var title = CleanTitle(Regex.Replace(Regex.Replace(w.Title, @"(?:^|\s+)(?:bis|until|by|spätestens|spaetestens)$|^(?:bis|spätestens|spaetestens)\s+", "", RegexOptions.IgnoreCase),
            @"\s+(?:bis|spätestens|spaetestens)\s+", " ", RegexOptions.IgnoreCase));
        if (title.Length == 0 || Regex.IsMatch(title, @"^(aufgabe|aufgaben|todo|task|termin|notiz)$", RegexOptions.IgnoreCase))
            return L("Was soll auf die Liste? Zum Beispiel: aufgabe Steuer machen", "What should go on the list? For example: task do taxes");
        PimStore.EnsureDefaults(space.Db);
        var target = to ?? (uri == null ? Preferred(services, space, "task", title) : null) ?? PimStore.Collections(space.Db, PimStore.Calendar)
            .Where(c => c.Components.Contains("VTODO") && Lists.TypeOf(space.Db, c.Id) == Lists.Tasks && !Subscriptions.IsSubscribed(space.Db, c.Id))
            .Select(c => new Target(space, c, false)).FirstOrDefault()
            ?? throw new InvalidOperationException("Ich finde keine Aufgabenliste.");
        var list = target.Coll;
        var input = new TaskInput { List = list.Id, Title = title, Uri = uri, Assignee = assignee?.Id };
        var today = DateOnly.FromDateTime(now);
        var dueDay = w.Until ?? w.Date;
        if (dueDay != null || w.Start != null)
        {
            var day = dueDay ?? today;
            if (w.Start is { } st) { input.Due = Iso(Dav.Ics.ToUtc(day.ToDateTime(st), space.Zone)); input.DueAllDay = false; }
            else input.Due = day.ToString("yyyy-MM-dd");
        }
        var id = CalendarModel.SaveTask(target.Owner, input);
        var made = new List<Created> { new("task", target.Owner.User.Id, list.Id, id[(id.IndexOf('/') + 1)..], 0, L("Aufgabe „" + title + "“", "task “" + title + "”")) };
        var dueWord = L(" – fällig ", " – due ");
        var due = dueDay is { } dd ? dueWord + DayWords(dd, today) + (w.Start is { } s ? " " + s.ToString("HH:mm") : "")
            : w.Start is { } s2 ? dueWord + DayWords(today, today) + " " + s2.ToString("HH:mm") : "";
        var repeat = w.Repeat != null ? L(" Wiederholen kann ich Aufgaben noch nicht – ich habe sie einmal notiert.", " I can't repeat tasks yet – I've added it once.") : "";
        var assigned = assignee == null ? "" : assignee.Id == space.User.Id ? L(" – für dich", " – for you") : L($" – zugewiesen an {assignee.DisplayName}", $" – assigned to {assignee.DisplayName}");
        if (assignee != null && assignee.Id != space.User.Id) TaskAssigned(services, space.User.Id, assignee.Id, target.Owner.User.Id, list.Id, id[(id.IndexOf('/') + 1)..], title, list.Name);
        return Made(SpeaksEnglish ? $"{(uri == null ? "Noted" : "Changed")}: {title}{due}{assigned} – in “{list.Name}”.{repeat}" : $"{(uri == null ? "Notiert" : "Geändert")}: {title}{due}{assigned} – in „{list.Name}“.{repeat}", made, "task", rest, uri == null ? Targets(services, space, "task").Count : 0);
    }

    private static Reply NewNote(IServiceProvider services, UserSpace space, string rest)
    {
        var text = rest.Trim();
        if (text.Length == 0) return L("Was soll in die Notiz? Zum Beispiel: notiz Ideen für den Urlaub", "What should the note say? For example: note holiday ideas");
        var db = NotesDb.Of(space);
        var id = db.CreateNote(null);
        var html = "<div data-notizi=\"1\">" + string.Concat(text.Split('\n').Select(l => "<p>" + WebUtility.HtmlEncode(l.TrimEnd('\r')).Replace("&nbsp;", "&#160;") + "</p>")) + "</div>";
        db.SaveNote(id, html, text.Replace("\n", "\r\n"));
        services.GetRequiredService<NotesSync>().Kick(space.User.Id, TimeSpan.FromSeconds(3));
        var first = text.Split('\n')[0];
        var label = first.Length > 60 ? first[..60] + "…" : first;
        return Made(SpeaksEnglish ? $"Note created: “{label}”." : $"Notiz angelegt: „{label}“.", [new Created("note", space.User.Id, 0, null, id, L("Notiz „" + label + "“", "note “" + label + "”"))], "note", rest, 0);
    }

    // ---------------- Einkaufsliste ----------------

    /// <summary>Ein Artikel: Name, Menge, Einheit, Abteilung; Near: ein bekannter Artikel, der so ähnlich heißt (Tippfehler?); Line: wie geschrieben.</summary>
    private sealed record ShopItem(string Name, string? Qty, string? Unit, string? Section, string? Near = null, bool FromHistory = false, string? Line = null);

    private static readonly Regex QtyRx = new(
        @"^(?<qty>\d+(?:[.,]\d+)?|\d+/\d+|ein|eine|einen|zwei|drei|vier|fünf|sechs|sieben|acht|neun|zehn|zwölf|ein paar|etwas|halbe?s?)\s*(?<unit>kg|g|gr|gramm|kilo|l|liter|ml|cl|dl|stück|stk|packung|packungen|pck|pkg|päckchen|pack|dose|dosen|flasche|flaschen|bund|becher|glas|gläser|tüte|tüten|netz|kiste|kasten|rolle|rollen|el|tl|esslöffel|teelöffel|prise|prisen|zehe|zehen|scheibe|scheiben|tasse|tassen|stange|stangen|msp|würfel|beutel|schale|schalen|kopf|köpfe)?\.?\s+(?<name>.+)$",
        RegexOptions.IgnoreCase);

    private static readonly Dictionary<string, string> NumberWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ein"] = "1", ["eine"] = "1", ["einen"] = "1", ["zwei"] = "2", ["drei"] = "3", ["vier"] = "4", ["fünf"] = "5", ["sechs"] = "6", ["sieben"] = "7",
        ["acht"] = "8", ["neun"] = "9", ["zehn"] = "10", ["zwölf"] = "12",
    };

    /// <summary>
    /// Die Artikel eines Satzes („Milch, 6 Eier und 2 kg Kartoffeln kaufen“), wenn jeder Teil nach Einkauf aussieht (Wortschatz,
    /// schon einmal auf einer eigenen Einkaufsliste, oder so ähnlich geschrieben). force: jeder Teil zählt (Befehl „einkauf …“).
    /// Leer: kein Einkauf.
    /// </summary>
    private static List<ShopItem> ShopItems(UserSpace space, string text, bool force = false)
    {
        var t = Regex.Replace(text.Trim(), @"^(wir brauchen|brauchen|ich brauche|bitte|kaufen|einkaufen|besorgen|und|noch|auch|dann)\s*:?\s*", "", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s+(kaufen|einkaufen|besorgen|mitbringen|holen|bitte|auch|auch noch|noch|dazu|mit)[.!]*$", "", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s+(kaufen|einkaufen|besorgen|mitbringen|holen)[.!]*$", "", RegexOptions.IgnoreCase);
        var known = KnownShopItems(space);
        var parts = Regex.Split(t, @"\s*(?:,|;|\n|\+|&|\bund\b|\band\b|\bsowie\b)\s*", RegexOptions.IgnoreCase)
            .Select(p => Regex.Replace(p.Trim().TrimEnd('.', '!'), @"^(?:(?:noch|no|auch|bitte|etwas|neue[ns]?|ein paar|a paar|paar|a|nen|n|so ein|so eine)\s+)+", "", RegexOptions.IgnoreCase).Trim())
            .Select(p => Regex.Replace(p, @"^(?:-|•|\*|☐)\s*", "")).Where(p => p.Length > 0).ToList();
        // „Milch Eier Brot“ ohne Komma: lauter einzelne Einkaufswörter
        if (parts.Count == 1 && PukkiWords.Words(parts[0]) is { Count: > 1 } ws && ws.All(x => PukkiWords.In(x, PukkiWords.ShopWords)) && !QtyRx.IsMatch(parts[0]) && !PukkiWords.IsShopWord(parts[0]))
            parts = ws.Select(x => char.ToUpper(x[0]) + x[1..]).ToList();
        var items = new List<ShopItem>();
        foreach (var p in parts)
        {
            string name = p, qty = null!, unit = null!;
            var m = QtyRx.Match(p);
            if (m.Success)
            {
                name = m.Groups["name"].Value.Trim();
                var q = m.Groups["qty"].Value;
                qty = NumberWords.TryGetValue(q, out var n) ? n : Regex.IsMatch(q, @"^\d") ? q.Replace(',', '.') : null!;
                unit = m.Groups["unit"].Success && m.Groups["unit"].Value.Length > 0 ? m.Groups["unit"].Value : null!;
                if (qty == null && unit == null) name = p;
            }
            var ws2 = PukkiWords.Words(name);
            if (ws2.Count == 0 || name.Length < 2) { if (force) continue; return new(); }
            var key = name.ToLowerInvariant();
            var exact = known.ContainsKey(key) || PukkiWords.IsShopWord(name);
            // Wörter, die nie Artikel sind („Urlaub“, „Hilfe“) - und lange Sätze sind kein Artikel
            var odd = ws2.Any(x => PukkiWords.NotItems.Contains(x)) || ws2.Count > 4;
            var shopWords = ws2.Count(x => PukkiWords.In(x, PukkiWords.ShopWords));
            // Ein Wort dazu darf sein, wenn es beschreibt („frische Milch“, „Brot vom Bäcker“) - „ich mag Pizza“ ist kein Artikel
            var extra = ws2.Where(x => !PukkiWords.In(x, PukkiWords.ShopWords)).ToList();
            var sentence = Regex.IsMatch(name, @"^(ich|du|er|sie|es|wir|ihr|man|mein|meine|dein|deine|unser|unsere|das|dies|der|die)\b", RegexOptions.IgnoreCase) && !exact;
            var isShop = !odd && !sentence && (exact || (shopWords > 0 && extra.Count <= 1 && extra.All(x => Regex.IsMatch(x, @"^(\p{L}{2,}(e|en|er|es|em)|bio|light|zero|vegan|frisch|klein|groß|gross|laktosefrei|glutenfrei|\d+%?)$", RegexOptions.IgnoreCase))));
            string? near = null;
            var fromHistory = false;
            // Kurze Wörter („jein“ ≠ „Wein“) nur, wenn ausdrücklich etwas auf die Liste soll
            if (!exact && ws2.Count == 1 && !odd && (force || key.Length >= 5))
            {
                // Tippfehler? Erst die eigene Vorschlagsliste, dann der Wortschatz
                near = PukkiWords.Nearest(key, known.Keys);
                if (near != null) fromHistory = true;
                else near = PukkiWords.Nearest(key, PukkiWords.ShopWords);
                if (near != null) near = HistoryName(space, near) ?? Cap(near);
            }
            if (!isShop && near == null && !force) return new();
            if (odd && !force) return new();
            // „MILCH“ → „Milch“, „2 packungen nudeln“ → „2 Packungen Nudeln“
            if (name.Length > 2 && name == name.ToUpperInvariant() && name.Any(char.IsLetter)) name = name.ToLowerInvariant();
            if (unit != null && unit.Length > 3) unit = Cap(unit.ToLowerInvariant());
            name = Cap(name);
            var section = known.TryGetValue(name.ToLowerInvariant(), out var s) && s != null ? s : PukkiWords.SectionOf(near ?? name);
            items.Add(new ShopItem(name, qty, unit, section, near, fromHistory, p));
        }
        return items;
    }

    /// <summary>Wie ein Artikel in der Vorschlagsliste geschrieben ist (Groß/Klein).</summary>
    private static string? HistoryName(UserSpace space, string key)
    {
        foreach (var c in PimStore.Collections(space.Db, PimStore.Calendar).Where(c => c.Components.Contains("VTODO") && Lists.TypeOf(space.Db, c.Id) == Lists.Shop))
            foreach (var k in Lists.Get(space.Db, c.Id).History)
                if (string.Equals(k.Name, key, StringComparison.OrdinalIgnoreCase)) return k.Name;
        return null;
    }

    /// <summary>Was schon auf einer eigenen Einkaufsliste stand (Name → Abteilung).</summary>
    private static Dictionary<string, string?> KnownShopItems(UserSpace space)
    {
        var result = new Dictionary<string, string?>();
        foreach (var c in PimStore.Collections(space.Db, PimStore.Calendar).Where(c => c.Components.Contains("VTODO") && Lists.TypeOf(space.Db, c.Id) == Lists.Shop))
            foreach (var k in Lists.Get(space.Db, c.Id).History)
                result.TryAdd(k.Name.ToLowerInvariant(), k.Section);
        return result;
    }

    /// <summary>Die Einkaufsliste: eine eigene, sonst eine mit mir geteilte, die ich bearbeiten darf (z. B. die der Familie), sonst neu „Einkauf“.</summary>
    private static (UserSpace Owner, Collection Coll) ShopList(IServiceProvider services, UserSpace space)
    {
        PimStore.EnsureDefaults(space.Db);
        var own = PimStore.Collections(space.Db, PimStore.Calendar)
            .FirstOrDefault(c => c.Components.Contains("VTODO") && Lists.TypeOf(space.Db, c.Id) == Lists.Shop && !Subscriptions.IsSubscribed(space.Db, c.Id));
        if (own != null) return (space, own);
        foreach (var (share, owner, coll) in SharedLists.For(services.GetRequiredService<ShareStore>(), services.GetRequiredService<Spaces>(), space.User.Id))
            if (coll.Components.Contains("VTODO") && Lists.TypeOf(owner.Db, coll.Id) == Lists.Shop && !share.ReadOnly.Contains(space.User.Id)) return (owner, coll);
        var created = PimStore.CreateCollection(space.Db, PimStore.Calendar, Guid.NewGuid().ToString("N")[..12], "Einkauf", null, "VTODO");
        Lists.Update(space.Db, created.Id, Lists.Shop, null);
        return (space, created);
    }

    /// <summary>Vergleichsschlüssel eines Artikels: klein, ohne Umlaute, einfache Mehrzahl weg („Eier“ = „Ei“, „Möhren“ = „Möhre“).</summary>
    private static string ItemKey(string name)
    {
        var w = PukkiWords.Fold(name.Trim().ToLowerInvariant());
        foreach (var e in new[] { "en", "er", "n", "s", "e" })
            if (w.EndsWith(e) && w.Length - e.Length >= 3) return w[..^e.Length];
        return w;
    }

    /// <summary>Offene Artikel einer Liste (Schlüssel → Anzeige).</summary>
    private static Dictionary<string, string> OpenItems(UserSpace owner, Collection list)
    {
        var result = new Dictionary<string, string>();
        foreach (var t in CalendarModel.Tasks(owner, [list.Id]).Where(t => !t.Completed))
            result.TryAdd(ItemKey(t.Item ?? t.Title), t.Item ?? t.Title);
        return result;
    }

    private static string ItemText(ShopItem i) => (i.Qty != null ? i.Qty + (i.Unit != null ? " " + i.Unit : "") + " " : "") + i.Name;

    /// <summary>Mehrzahl? („Eier stehen“, „Tomaten stehen“, aber „Milch steht“, „Reis steht“) - eine einfache Regel ohne Wörterbuch.</summary>
    private static bool Plural(string name)
    {
        var w = name.Trim().Split(' ')[^1].ToLowerInvariant();
        if (w is "reis" or "mais" or "eis" or "kürbis" or "ananas" or "kakao" or "lachs" or "hummus" or "couscous" or "milchreis" or "käse" or "kuchen" or "schinken" or "pudding" or "senf" or "essig" or "salz" or "zimt" or "honig") return false;
        return w.EndsWith("en") || w.EndsWith("eln") || w.EndsWith("ern") || w is "eier" or "chips" or "nüsse" or "kekse" or "äpfel" or "pommes" or "würstchen" or "brötchen"
            || (w.EndsWith('s') && w.Length > 4) || (w.EndsWith('n') && w.Length > 4 && !w.EndsWith("ein"));
    }

    private static string Steht(IEnumerable<string> names) => names.Count() == 1 && !Plural(names.First()) ? "steht" : "stehen";

    /// <summary>„A“, „A und B“, „A, B und C“ - wahlweise in „…“.</summary>
    private static string JoinAnd(IEnumerable<string> items, bool quote = false)
    {
        var list = items.Select(i => quote ? $"„{i}“" : i).ToList();
        return list.Count <= 1 ? string.Join("", list) : string.Join(", ", list.Take(list.Count - 1)) + " und " + list[^1];
    }

    /// <summary>
    /// Artikel auf die Einkaufsliste: was dort schon offen steht, kommt nicht doppelt; sieht ein Artikel wie ein Tippfehler
    /// eines bekannten aus, fragt Pukki nach (der Rest kommt gleich auf die Liste). lead: eigener Antwortanfang
    /// („Okay, ich habe …“); more: danach fragen, ob noch etwas dazukommt.
    /// </summary>
    private static Reply AddShop(IServiceProvider services, UserSpace space, List<ShopItem> items, string source, Target? to = null, Memory? mem = null, Func<string, string, string>? lead = null, bool more = false)
    {
        var (owner, list) = to is { } t ? (t.Owner, t.Coll) : ShopList(services, space);
        var open = OpenItems(owner, list);
        var doubt = mem != null ? items.FirstOrDefault(i => i.Near != null && !open.ContainsKey(ItemKey(i.Name))) : null;
        var add = new List<ShopItem>();
        var have = new List<string>();
        foreach (var it in items.Where(i => i != doubt))
        {
            if (open.TryGetValue(ItemKey(it.Name), out var shown)) { if (!have.Contains(shown)) have.Add(shown); }
            else if (!add.Any(a => ItemKey(a.Name) == ItemKey(it.Name))) add.Add(it);
        }
        var made = new List<Created>();
        foreach (var it in add)
        {
            var id = CalendarModel.SaveTask(owner, new TaskInput { List = list.Id, Title = it.Name, Item = it.Name, Qty = it.Qty, Unit = it.Unit, Section = it.Section ?? "Sonstiges" });
            Lists.Learn(owner.Db, list.Id, it.Name, it.Unit, it.Section);
            made.Add(new Created("task", owner.User.Id, list.Id, id[(id.IndexOf('/') + 1)..], 0, it.Name));
        }
        var parts = new List<string>();
        if (add.Count > 0)
            parts.Add(lead != null ? lead(add.Count == 1 && add[0].Qty == null ? add[0].Name : JoinAnd(add.Select(ItemText), quote: true), list.Name)
                : $"Auf der Einkaufsliste „{list.Name}“: {string.Join(", ", add.Select(ItemText))}.");
        if (have.Count > 0)
            parts.Add($"{JoinAnd(have)} {Steht(have)} schon auf der Einkaufsliste „{list.Name}“.");
        var actions = new List<ChatAction>();
        if (doubt != null && mem != null)
        {
            SetAsk(mem, "shop-typo", ItemText(doubt), doubt.Near);
            mem.Items = [doubt.Name];
            mem.ListOwner = owner.User.Id; mem.ListId = list.Id;
            parts.Add(doubt.FromHistory
                ? $"Ich finde „{doubt.Near}“ in der Vorschlagsliste – das hast du schon einmal gekauft. Soll ich stattdessen „{doubt.Near}“ auf die Einkaufsliste setzen?"
                : $"Meinst du „{doubt.Near}“? Soll ich stattdessen „{doubt.Near}“ auf die Einkaufsliste setzen?");
            actions.Add(Say(0, "Ja, " + doubt.Near, "ja"));
            actions.Add(Say(1, "Nein, " + doubt.Name, "nein"));
        }
        else if (more && mem != null && add.Count > 0)
        {
            parts.Add("Möchtest du noch etwas auf die Einkaufsliste?");
            SetAsk(mem, "shop-more", null, null);
            mem.ListOwner = owner.User.Id; mem.ListId = list.Id;
        }
        if (mem != null) { mem.Topic = "shop"; mem.Subject = add.Concat(items).Select(i => i.Name).FirstOrDefault(); }
        var text = string.Join(" ", parts);
        if (made.Count == 0) return new Reply(text, Actions: actions.Count > 0 ? actions : null);
        var reply = Made(text, made, "shop", source, Targets(services, space, "shop").Count);
        return actions.Count == 0 ? reply : reply with { Actions = [.. reply.Actions!, .. actions] };
    }

    // ---------------- Überblick ----------------

    /// <summary>Was an einem Tag ansteht: Termine aus eigenen und geteilten Kalendern (auch Gruppen), fällige Aufgaben. Null: nichts.</summary>
    public static string? Overview(IServiceProvider services, UserSpace space, DateOnly day, DateOnly today)
    {
        var lines = new List<string>();
        foreach (var (title, start, end, allDay, cal, icon, _) in DayEvents(services, space, day))
            lines.Add("• " + (allDay ? L("ganztägig", "all day") : start.ToString("HH:mm") + "–" + end.ToString("HH:mm")) + " " + (icon != null ? icon + " " : "") + title + (cal != null ? $" ({cal})" : ""));
        var tasks = DueTasks(services, space, day, day == today);
        if (tasks.Count > 0)
        {
            if (lines.Count > 0) lines.Add("");
            lines.Add(day == today ? L("Fällig:", "Due:") : L("Fällig ", "Due ") + DayWords(day, today) + ":");
            lines.AddRange(tasks.Select(x => "☐ " + x));
        }
        if (lines.Count == 0) return null;
        var head = day == today ? L("Heute, ", "Today, ") + DateWords(day, "dddd, d. MMMM", "dddd, d MMMM") + ":" : Cap(DayWords(day, today)) + (Math.Abs(day.DayNumber - today.DayNumber) <= 2 ? ", " + DateWords(day, "dddd, d. MMMM", "dddd, d MMMM") : "") + ":";
        return head + "\n" + string.Join("\n", lines);
    }

    /// <summary>Überblick eines Tages als Antwort - mit Gesprächszusammenhang für „Und morgen?“.</summary>
    private static Reply DayOverview(IServiceProvider services, UserSpace space, Memory mem, DateOnly day, DateOnly today)
    {
        mem.Topic = "day"; mem.Day = day;
        return Overview(services, space, day, today) is { } text ? new Reply(text, Actions: DoneActions(services, space, day, today))
            : SpeaksEnglish ? $"Nothing planned for {DayWords(day, today)}." : $"{(day < today ? "Für" : "Für")} {PukkiParse.DayText(day, today)} ist nichts eingetragen.";
    }

    /// <summary>Termine eines Tages (Ortszeit) mit Kalendername bei geteilten Kalendern.</summary>
    public static List<(string Title, TimeOnly Start, TimeOnly End, bool AllDay, string? Calendar, string? Icon, string Uri)> DayEvents(IServiceProvider services, UserSpace space, DateOnly day, IEnumerable<(UserSpace Space, Collection Coll, bool Shared)>? only = null)
    {
        var zone = space.Zone;
        var from = Dav.Ics.ToUtc(day.ToDateTime(TimeOnly.MinValue), zone);
        var to = Dav.Ics.ToUtc(day.AddDays(1).ToDateTime(TimeOnly.MinValue), zone);
        var result = new List<(string, TimeOnly, TimeOnly, bool, string?, DateTime, string?, string)>();
        foreach (var (owner, coll, shared) in only ?? Calendars(services, space))
            foreach (var e in CalendarModel.Events(owner, from, to, [coll.Id]))
            {
                if (e.AllDay)
                {
                    if (!DateOnly.TryParse(e.Start, CultureInfo.InvariantCulture, out var s) || !DateOnly.TryParse(e.End, CultureInfo.InvariantCulture, out var en)) continue;
                    if (s <= day && day < (en > s ? en : s.AddDays(1))) result.Add((e.Title, TimeOnly.MinValue, TimeOnly.MinValue, true, shared ? coll.Name : null, DateTime.MinValue, IconOf(e), e.Uri));
                }
                else
                {
                    var s = Local(e.Start, zone); var en = Local(e.End, zone);
                    if (s < day.AddDays(1).ToDateTime(TimeOnly.MinValue) && en > day.ToDateTime(TimeOnly.MinValue))
                        result.Add((e.Title, TimeOnly.FromDateTime(s < day.ToDateTime(TimeOnly.MinValue) ? day.ToDateTime(TimeOnly.MinValue) : s), TimeOnly.FromDateTime(en), false, shared ? coll.Name : null, s, IconOf(e), e.Uri));
                }
            }
        return result.OrderBy(x => !x.Item4).ThenBy(x => x.Item6).Select(x => (x.Item1, x.Item2, x.Item3, x.Item4, x.Item5, x.Item7, x.Item8)).ToList();
    }

    /// <summary>Symbol der Art eines Termins: aus seinen Kategorien, sonst eingeordnet nach dem Titel.</summary>
    private static string? IconOf(EventDto e) => (PukkiArts.FromCategories(e.Categories) ?? PukkiArts.Classify(e.Title, e.Description))?.Icon;

    /// <summary>Eigene Kalender und mit mir geteilte (auch Gruppen-Kalender), nur Termine, ohne Essenspläne.</summary>
    internal static IEnumerable<(UserSpace, Collection, bool)> Calendars(IServiceProvider services, UserSpace space)
    {
        PimStore.EnsureDefaults(space.Db);
        foreach (var c in PimStore.Collections(space.Db, PimStore.Calendar).Where(c => c.Components.Contains("VEVENT") && CalendarKinds.Of(space.Db, c.Id) == CalendarKinds.Normal))
            yield return (space, c, false);
        foreach (var (_, owner, c) in SharedLists.For(services.GetRequiredService<ShareStore>(), services.GetRequiredService<Spaces>(), space.User.Id))
            if (c.Components.Contains("VEVENT") && CalendarKinds.Of(owner.Db, c.Id) == CalendarKinds.Normal) yield return (owner, c, true);
    }

    /// <summary>Offene Aufgaben, die an dem Tag fällig sind (heute: auch überfällige).</summary>
    private static List<string> DueTasks(IServiceProvider services, UserSpace space, DateOnly day, bool withOverdue, IEnumerable<(UserSpace Space, Collection Coll)>? only = null) =>
        DueTaskItems(services, space, day, withOverdue, only).Select(x => x.Label).ToList();

    /// <summary>Fällige Aufgabe mit Bezug - für den Knopf „Erledigt“ unter dem Überblick.</summary>
    private sealed record DueTask(string Label, string Title, UserSpace Owner, long Collection, string Uri);

    private static List<DueTask> DueTaskItems(IServiceProvider services, UserSpace space, DateOnly day, bool withOverdue, IEnumerable<(UserSpace Space, Collection Coll)>? only = null)
    {
        var lists = only?.ToList() ?? PimStore.Collections(space.Db, PimStore.Calendar).Where(c => c.Components.Contains("VTODO")).Select(c => (space, c))
            .Concat(SharedLists.For(services.GetRequiredService<ShareStore>(), services.GetRequiredService<Spaces>(), space.User.Id)
                .Where(x => x.Coll.Components.Contains("VTODO")).Select(x => (x.Owner, x.Coll))).ToList();
        var result = new List<DueTask>();
        foreach (var (owner, coll) in lists)
            foreach (var t in CalendarModel.Tasks(owner, [coll.Id]).Where(t => !t.Completed && t.Due != null))
            {
                var due = t.DueAllDay ? DateOnly.Parse(t.Due![..10], CultureInfo.InvariantCulture) : DateOnly.FromDateTime(Local(t.Due!, space.Zone));
                if (due == day || (withOverdue && due < day))
                    result.Add(new(t.Title + (due < day ? " (überfällig)" : !t.DueAllDay ? " – " + Local(t.Due!, space.Zone).ToString("HH:mm") : ""), t.Title, owner, coll.Id, t.Uri));
            }
        return result;
    }

    // ---------------- Gruppen-Fragen ----------------

    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "wer", "was", "wann", "wo", "wie", "ist", "sind", "hat", "haben", "holt", "macht", "die", "der", "das", "den", "dem", "ein", "eine",
        "heute", "morgen", "übermorgen", "steht", "an", "ab", "im", "in", "am", "um", "zu", "und", "oder", "es", "gibt", "denn", "noch", "bitte",
        "who", "what", "when", "is", "are", "the", "a", "today", "tomorrow", "pukki", "@pukki",
    };

    /// <summary>„@Pukki …“ in einer Gruppe: Antwort aus dem Gruppen-Kalender bzw. der Gruppen-Liste.</summary>
    public static string GroupAnswer(IServiceProvider services, ChatStore chat, long room, string text, string asker)
    {
        var spaces = services.GetRequiredService<Spaces>();
        var me = spaces.Get(asker);
        var now = Now(me);
        var today = DateOnly.FromDateTime(now);
        // „@Pukki“ darf auch mitten im Satz stehen - gefragt ist, was danach kommt
        var question = Regex.Replace(text, @"^.*?(?<![\p{L}\p{N}])@pukki[,:]?\s*", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var groupSpaces = chat.SpacesOf(room);
        var words = Words(question).ToList();
        if (words.Count == 0 || words.Any(w => w is "hilfe" or "help")) return Help;
        // Was gibt es zu essen? (Essenspläne des Fragenden, auch die geteilten der Familie)
        // In der Gruppe nur eigene Essensplaene und die der Gruppe - nicht, was andere nur mit dem Fragenden teilen
        if (MealQuestion(services, me, question, now, p => p.Owner.User.Id == asker || (groupSpaces.Any(s => s.Kind == ChatSpace.Calendar && s.Owner == p.Owner.User.Id && s.Ref == p.Coll.Id.ToString()))) is { } meal) return meal;

        // Aufgabe fuer jemanden (Version 2): „@Pukki Anna soll den Müll rausbringen“, „@Pukki aufgabe …“ - in die Gruppen-Liste
        if ((SplitAssignee(question).Who is { } who && !Regex.IsMatch(who, "^(was|wer|wie|wo|wann|es|das|man)$", RegexOptions.IgnoreCase)) || Regex.IsMatch(question, @"^aufgabe\b", RegexOptions.IgnoreCase))
        {
            var ls = groupSpaces.FirstOrDefault(s => s.Kind == ChatSpace.List);
            if (ls == null) return "Diese Gruppe hat noch keine Gruppen-Liste – wer die Gruppe verwaltet, richtet sie im Gruppenmenü ein.";
            var owner = spaces.Get(ls.Owner);
            var coll = PimStore.Find(owner.Db, long.Parse(ls.Ref));
            if (coll == null) return "Die Gruppen-Liste gibt es nicht mehr.";
            return NewTask(services, me, Regex.Replace(question, @"^aufgabe[:\s]+", "", RegexOptions.IgnoreCase), now, new Target(owner, coll, true)).Text;
        }

        // Liste: „@Pukki Liste“, „… Einkauf“
        if (words.Any(w => w.StartsWith("liste") || w.StartsWith("einkauf") || w is "list" or "shopping" or "aufgaben"))
        {
            var ls = groupSpaces.FirstOrDefault(s => s.Kind == ChatSpace.List);
            if (ls == null) return "Diese Gruppe hat noch keine Gruppen-Liste – wer die Gruppe verwaltet, richtet sie im Gruppenmenü ein.";
            var owner = spaces.Get(ls.Owner);
            var coll = PimStore.Find(owner.Db, long.Parse(ls.Ref));
            if (coll == null) return "Die Gruppen-Liste gibt es nicht mehr.";
            // Zugewiesene Aufgaben (Version 2) mit dem Namen dahinter
            var users = services.GetRequiredService<UserStore>();
            var open = CalendarModel.Tasks(owner, [coll.Id]).Where(t => !t.Completed)
                .Select(t => "☐ " + t.Title + (t.Assignee is { } a && users.ById(a) is { } u ? " – " + u.DisplayName : "")).ToList();
            return open.Count == 0 ? $"Auf der Liste „{coll.Name}“ ist nichts offen." : $"Offen auf „{coll.Name}“:\n" + string.Join("\n", open.Take(30));
        }

        var cs = groupSpaces.FirstOrDefault(s => s.Kind == ChatSpace.Calendar);
        if (cs == null) return "Diese Gruppe hat noch keinen Gruppen-Kalender – wer die Gruppe verwaltet, richtet ihn im Gruppenmenü ein.";
        var calOwner = spaces.Get(cs.Owner);
        var cal = PimStore.Find(calOwner.Db, long.Parse(cs.Ref));
        if (cal == null) return "Den Gruppen-Kalender gibt es nicht mehr.";
        var day = PukkiParse.Parse(question, now).Date ?? today;
        var events = DayEvents(services, me, day, [(calOwner, cal, false)]);
        var keys = words.Where(w => w.Length >= 4 && !Stop.Contains(w)).ToList();
        var hits = keys.Count == 0 ? events : events.Where(e => keys.Any(k => e.Title.Contains(k, StringComparison.OrdinalIgnoreCase) || k.Contains(e.Title, StringComparison.OrdinalIgnoreCase)
            || e.Title.Split(' ').Any(tw => tw.Length >= 4 && k.StartsWith(tw[..4], StringComparison.OrdinalIgnoreCase)))).ToList();
        var dayText = PukkiParse.DayText(day, today);
        if (events.Count == 0) return $"{Cap(PukkiParse.OnDay(day, today))} steht nichts im Kalender „{cal.Name}“.";
        var list = (hits.Count > 0 ? hits : events).Select(e => "• " + (e.AllDay ? "ganztägig" : e.Start.ToString("HH:mm")) + " " + (e.Icon != null ? e.Icon + " " : "") + e.Title);
        return (hits.Count > 0 || keys.Count == 0 ? $"{Cap(PukkiParse.OnDay(day, today))} im Kalender „{cal.Name}“:\n" : $"Dazu finde ich {PukkiParse.OnDay(day, today)} nichts – das steht im Kalender „{cal.Name}“:\n") + string.Join("\n", list);
    }

    // ---------------- Hilfen ----------------

    private static (string Word, string Tail) Split(string t)
    {
        var i = t.IndexOfAny([' ', ':', '\n']);
        var w = (i < 0 ? t : t[..i]).ToLowerInvariant().Trim('.', '!', ',');
        return (w, i < 0 ? "" : t[(i + 1)..].Trim());
    }

    private static readonly string[] Commands = ["termin", "aufgabe", "notiz", "einkauf", "einkaufsliste", "essensplan"];

    /// <summary>Befehlswort, auch vertippt („temin“, „aufgbe“) - aber nicht „termine“ oder „aufgaben“ (das sind Fragen).</summary>
    private static string CommandWord(string word)
    {
        if (word.Length < 5 || word is "termine" or "aufgaben" or "notizen" or "einkaufen") return word;
        return Commands.FirstOrDefault(c => c == word) ?? Commands.FirstOrDefault(c => PukkiWords.Distance(word, c) == 1 && word.Length >= c.Length - 1) ?? word;
    }

    private static IEnumerable<string> Words(string t) =>
        t.ToLowerInvariant().Split([' ', ',', '.', '?', '!', ':', ';', '\n'], StringSplitOptions.RemoveEmptyEntries);

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpper(s[0]) + s[1..];

    public static DateTime Now(UserSpace space) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow + Shift, space.Zone);

    /// <summary>Nur für Tests: Pukkis Uhr verstellen (der Sprachkorpus prüft so jeden Wochentag und den späten Abend).</summary>
    internal static TimeSpan Shift;

    private static string Iso(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>Zeit vom Server (ISO UTC oder Datum) in Ortszeit.</summary>
    public static DateTime Local(string iso, TimeZoneInfo zone)
    {
        var t = CalendarModel.ParseIso(iso, out var dateOnly);
        return dateOnly ? t : TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(t.ToUniversalTime(), DateTimeKind.Utc), zone);
    }
}

/// <summary>
/// Pukki im Hintergrund: beantwortet Nachrichten (eigene Unterhaltung, „@Pukki“ in Gruppen), schickt den
/// Tagesüberblick zur eingestellten Uhrzeit und erinnert an Termine mit Erinnerung.
/// </summary>
public sealed class PukkiService(ChatStore chat, Spaces spaces, IServiceProvider services, ILogger<PukkiService> log) : BackgroundService
{
    private readonly Channel<ChatPosted> _inbox = Channel.CreateUnbounded<ChatPosted>();
    /// <summary>Bis wann Erinnerungen geprüft sind, je Benutzer (nach dem Start: ab jetzt - nichts nachholen).</summary>
    private readonly ConcurrentDictionary<string, DateTime> _remindedUntil = new();
    private readonly ConcurrentDictionary<string, DateTime> _taskRemindedUntil = new();

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        chat.Posted += OnPosted;
        try
        {
            var answers = Task.Run(() => Answers(stop), stop);
            await Task.Delay(TimeSpan.FromSeconds(15), stop);
            while (!stop.IsCancellationRequested)
            {
                foreach (var space in spaces.All())
                {
                    try { Tick(space); }
                    catch (Exception e) { log.LogWarning(e, "Pukki fuer {User}", space.User.Login); }
                }
                await Task.Delay(TimeSpan.FromMinutes(1), stop);
            }
            await answers;
        }
        catch (OperationCanceledException) { }
        finally { chat.Posted -= OnPosted; }
    }

    private void OnPosted(ChatPosted p)
    {
        if (p.Sender != Pukki.Id) _inbox.Writer.TryWrite(p);
    }

    private async Task Answers(CancellationToken stop)
    {
        await foreach (var p in _inbox.Reader.ReadAllAsync(stop))
        {
            try
            {
                var room = chat.Room(p.Sender, p.Room);
                var text = chat.Message(p.Sender, p.Message).Text;
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (room.Kind == ChatStore.Direct && room.Members.Any(m => m.Id == Pukki.Id))
                {
                    // Neue Nachricht: offene Auswahl-Knoepfe sind ueberholt (Text wie „ja“ geht ueber die Rueckfrage)
                    Pukki.CloseChoices(chat, p.Sender, p.Room);
                    var space = spaces.Get(p.Sender);
                    using var device = Pukki.ForDevice(space);
                    var reply = Pukki.Answer(services, space, text, chat, p.Room);
                    // Nicht gewusst: mit KI (wenn der Admin sie fuer diesen Benutzer freigegeben hat) Claude fragen
                    var config = services.GetRequiredService<ServerConfig>();
                    var aiAllowed = PukkiAi.Allowed(config, services.GetRequiredService<UserStore>().ById(p.Sender));
                    // Beschwerden und Medikamente nie an die KI: dann bleibt es bei der festen Antwort
                    if (reply.Unknown && aiAllowed && PukkiGuard.GuardForAi(text) is { } guard) reply = guard.Text;
                    if (reply.Unknown && aiAllowed)
                    {
                        var history = chat.Messages(p.Sender, p.Room, before: p.Message, limit: 8)
                            .Where(m => !m.Deleted && !string.IsNullOrWhiteSpace(m.Text)).OrderBy(m => m.Id)
                            .Select(m => (m.Sender != Pukki.Id, m.Text!)).ToList();
                        // Solange die KI rechnet, zeigt der Chat „Pukki denkt nach …“
                        chat.SetBusy(p.Sender, p.Room, true);
                        try
                        {
                            string? fromAi = null;
                            if (await PukkiAi.Ask(config, text, history, Pukki.Now(space), log, stop) is { } ai) { reply = Pukki.AiReply(ai); fromAi = Pukki.AiMood; }
                            chat.Post(Pukki.Id, p.Room, reply.Text, refs: reply.Refs, actions: reply.Actions, mood: fromAi);
                        }
                        finally { chat.SetBusy(p.Sender, p.Room, false); }
                    }
                    else chat.Post(Pukki.Id, p.Room, reply.Text, refs: reply.Refs, actions: reply.Actions);
                }
                else if (room.Kind == ChatStore.Group && ChatMentions.Has(text, Pukki.Name))
                    using (Pukki.ForDevice(spaces.Get(p.Sender)))
                        chat.Post(Pukki.Id, p.Room, Pukki.GroupAnswer(services, chat, p.Room, text, p.Sender), mentions: [p.Sender]);
            }
            catch (Exception e) { log.LogWarning(e, "Pukki-Antwort in {Room}", p.Room); }
        }
    }

    /// <summary>Je Benutzer und Minute: Tagesüberblick fällig? Erinnerungen fällig?</summary>
    private void Tick(UserSpace space)
    {
        var user = space.User.Id;
        if (Pukki.RoomOf(chat, user) is not { } room) return;
        using var voice = Pukki.ForAccount(space);
        var s = ChatSettings.Of(space.Db);
        var now = Pukki.Now(space);
        var today = DateOnly.FromDateTime(now);

        if (s.PukkiDaily && TimeOnly.TryParse(s.PukkiTime, CultureInfo.InvariantCulture, out var at) && TimeOnly.FromDateTime(now) >= at)
        {
            var key = "pukki.daily." + user;
            if (chat.GetKv(key) != today.ToString("yyyy-MM-dd"))
            {
                chat.SetKv(key, today.ToString("yyyy-MM-dd"));
                // Nur wenn etwas ansteht - leere Tage bleiben still; dazu die Zeitumstellung
                // Wetter und Schlagzeilen (eingeschaltet unter Einstellungen → Chat) - dann kommt der Überblick jeden Tag
                var extra = string.Join("\n\n", new[] { Pukki.ClockChange(space.Zone, today), PukkiBriefing.Text(services, space, today) }.Where(x => x != null));
                if (Pukki.Overview(services, space, today, today) is { } text)
                    chat.Post(Pukki.Id, room, Pukki.L("Guten Morgen! ", "Good morning! ") + text + (extra.Length > 0 ? "\n\n" + extra : ""), actions: Pukki.DoneActions(services, space, today, today));
                else if (extra.Length > 0) chat.Post(Pukki.Id, room, "Guten Morgen! " + extra, mood: "kaffee");
            }
        }

        // Noch einmal erinnern (Knopf oder „noch mal“): kommt auch, wenn Erinnerungen sonst aus sind - ausdrücklich gewünscht
        foreach (var (title, start) in Pukki.DueSnoozes(chat, user, DateTime.UtcNow))
        {
            chat.Post(Pukki.Id, room, Pukki.ReminderText(space, title, start, " (noch einmal)"), actions: Pukki.SnoozeActions(title, start));
            Pukki.RememberReminder(chat, user, title, start);
        }

        // Erinnerungen (Aufgaben mit Kategorie „Erinnerung“): immer melden - ausdrücklich gewünscht
        {
            var utcNow = DateTime.UtcNow;
            var from = _taskRemindedUntil.GetOrAdd(user, utcNow);
            _taskRemindedUntil[user] = utcNow;
            if (from < utcNow)
                foreach (var r in Pukki.DueReminderTasks(space, from, utcNow))
                {
                    chat.Post(Pukki.Id, room, Pukki.ReminderText(space, r.Task.Title, r.AtUtc), actions: Pukki.SnoozeActions(r.Task.Title, r.AtUtc));
                    Pukki.RememberReminder(chat, user, r.Task.Title, r.AtUtc);
                }
        }

        // Abgelaufene Timer: immer melden - ausdrücklich gestellt
        foreach (var timer in Pukki.DueTimers(chat, user, DateTime.UtcNow))
            chat.Post(Pukki.Id, room, Pukki.TimerDoneText(timer));

        if (s.PukkiReminders)
        {
            var utcNow = DateTime.UtcNow;
            var from = _remindedUntil.GetOrAdd(user, utcNow);
            _remindedUntil[user] = utcNow;
            if (from >= utcNow) return;
            foreach (var (title, start, reminder) in DueReminders(services, space, from, utcNow))
            {
                // Mit Knöpfen zum erneuten Erinnern; gemerkt für „erinnere mich in 10 Minuten noch mal“
                chat.Post(Pukki.Id, room, Pukki.ReminderText(space, title, start, reminder > 0 ? $" (in {Span(reminder)})" : null), actions: Pukki.SnoozeActions(title, start));
                Pukki.RememberReminder(chat, user, title, start);
            }
        }
    }

    /// <summary>Termine, deren Erinnerung in (from, to] fällt (eigene und geteilte Kalender).</summary> Auch fuer Web Push.
    public static IEnumerable<(string Title, DateTime StartUtc, int Minutes)> DueReminders(IServiceProvider services, UserSpace space, DateTime from, DateTime to)
    {
        var spaces = services.GetRequiredService<Spaces>();
        var result = new List<(string, DateTime, int)>();
        // Erinnerungen reichen höchstens eine Woche voraus
        var cals = PimStore.Collections(space.Db, PimStore.Calendar).Where(c => c.Components.Contains("VEVENT")).Select(c => (space, c))
            .Concat(SharedLists.For(services.GetRequiredService<ShareStore>(), spaces, space.User.Id).Where(x => x.Coll.Components.Contains("VEVENT")).Select(x => (x.Owner, x.Coll)));
        foreach (var (owner, coll) in cals)
            foreach (var e in CalendarModel.Events(owner, from, to.AddDays(8), [coll.Id]).Where(e => e.Reminder != null))
            {
                var start = e.AllDay ? Dav.Ics.ToUtc(DateTime.Parse(e.Start, CultureInfo.InvariantCulture), space.Zone) : CalendarModel.ParseIso(e.Start, out _).ToUniversalTime();
                var fire = start.AddMinutes(-e.Reminder!.Value);
                if (fire > from && fire <= to) result.Add((e.Title, start, e.Reminder.Value));
            }
        return result;
    }

    internal static string Span(int minutes) => minutes switch
    {
        < 60 => $"{minutes} Minuten",
        < 1440 => minutes % 60 == 0 ? $"{minutes / 60} Stunde{(minutes == 60 ? "" : "n")}" : $"{minutes / 60} Std. {minutes % 60} Min.",
        _ => minutes % 1440 == 0 ? $"{minutes / 1440} Tag{(minutes == 1440 ? "" : "en")}" : $"{minutes / 60} Stunden",
    };
}
