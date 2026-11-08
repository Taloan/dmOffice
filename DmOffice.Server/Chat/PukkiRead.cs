using System.Globalization;
using System.Text.RegularExpressions;
using DmOffice.Core;
using DmOffice.Notes;
using DmOffice.Pim;

namespace DmOffice.Chat;

/// <summary>
/// Pukki schaut nach - Fragen legen nie etwas an: Einkaufsliste („Ist Milch auf der Liste?“, „Was brauchen wir?“), Vorrat
/// („Haben wir noch Milch?“ - das weiß er nicht, sagt aber, was auf der Liste steht), Zutaten eines geplanten Gerichts,
/// Aufgaben, Termine eines Tages oder einer Woche, ein bestimmter Termin (wann, wo, wer, mit Erinnerung), Geburtstage,
/// Kontakte, Notizen und der Essensplan.
/// </summary>
public static partial class Pukki
{
    /// <summary>Fragen nach Weltwissen mit einem Tag darin (Menge, Preis, Wetter, Ergebnis) - keine Frage an den eigenen Kalender.</summary>
    private static readonly Regex WorldQuestionRx = new(@"^(?:und\s+|aber\s+|sag mal,?\s+)?(wie\s*viel(e)?|wie hoch|wie teuer|was kostet|wer gewinnt|wer spielt|wie spielt|wie wird|wie warm|wie kalt|regnet es)\b|\b(lotto|jackpot|eurojackpot|wetter|aktie|börse|kurs|bitcoin|spielstand|tabelle)\b", RegexOptions.IgnoreCase);

    private const string ListWord = @"(?:einkaufs)?(?:liste|zettel)|einkaufszettel|shoppingliste";

    private static readonly Regex ShopCheckRx = new(
        @"^(?:und\s+)?(?:ist|sind|steht|stehen|steh)\s+(?:die\s+|der\s+|das\s+)?(?<item>.+?)\s+(?:schon\s+|noch\s+|denn\s+|auch\s+|bereits\s+|eigentlich\s+)*(?:auf|in)\s+(?:der|die|meiner|unserer|meine|unsere|dem|unserem|meinem)?\s*(?:" + ListWord + @")\b.*$" +
        @"|^(?:und\s+)?(?:ist|sind|steht|stehen)\s+(?:die\s+|der\s+|das\s+)?(?<item>.+?)\s+(?:schon\s+|bereits\s+|denn\s+)*(?:aufgeschrieben|notiert|drauf|eingetragen|vermerkt)\b.*$" +
        @"|^(?:hab|habe|haben|hast)\s+(?:ich|wir|du)\s+(?:die\s+|der\s+|das\s+)?(?<item>.+?)\s+(?:schon\s+|bereits\s+|denn\s+)*(?:aufgeschrieben|notiert|auf der (?:einkaufs)?liste|auf die (?:einkaufs)?liste (?:geschrieben|gesetzt)|draufgeschrieben|eingetragen)\b.*$" +
        @"|^(?:brauchen|müssen|muessen|sollen)\s+wir\s+(?:noch\s+|auch\s+|wieder\s+|eigentlich\s+)*(?<item>.+?)(?:\s+(?:kaufen|holen|besorgen|mitbringen))?\s*\??$" +
        @"|^(?:brauche|muss)\s+ich\s+(?:noch\s+|auch\s+|wieder\s+)*(?<item>.+?)\s+(?:kaufen|holen|besorgen|mitbringen)\s*\??$" +
        @"|^is\s+(?<item>.+?)\s+on\s+the\s+(?:shopping\s+)?list\b.*$", I);

    private static readonly Regex StockRx = new(
        @"^(?:und\s+)?(?:haben|hab|habe|hast)\s+(?:wir|ich|du)\s+(?:noch\s+|genug\s+|überhaupt\s+|eigentlich\s+|denn\s+|zu hause\s+|daheim\s+|im haus\s+)*(?<item>.+?)(?:\s+(?:da|im haus|zu hause|zuhause|daheim|im kühlschrank|vorrätig|übrig|im schrank|im keller))?\s*\??$" +
        @"|^(?:ist|sind)\s+(?:noch\s+|genug\s+)+(?<item>.+?)(?:\s+(?:da|im haus|zu hause|daheim|im kühlschrank|vorrätig|übrig))?\s*\??$" +
        @"|^(?:ist|sind)\s+(?:die\s+|der\s+|das\s+)?(?<item>.+?)\s+(?:da|im haus|im kühlschrank|vorrätig|übrig|alle|leer|aus|aufgebraucht)\s*\??$" +
        @"|^(?:gibt es|gibts|gibt's)\s+(?:noch\s+|genug\s+)+(?<item>.+?)\s*\??$" +
        @"|^do we (?:still )?have\s+(?:any\s+)?(?<item>.+?)\??$", I);

    private static readonly Regex ShowShopRx = new(
        @"^(?:zeig|zeige|öffne|oeffne|show|lies|lies mir)\s+(?:mir\s+|uns\s+|mal\s+|bitte\s+)*(?:die\s+|meine\s+|unsere\s+)?(?:" + ListWord + @")\b" +
        @"|^(?:die\s+|meine\s+|unsere\s+)?(?:" + ListWord + @"|einkauf|shopping list)(?:\s+(?:zeigen|anzeigen|bitte|vorlesen|ansehen))?\s*\??$" +
        @"|^(?:und\s+)?(?:was|welche\w*)\b(?!.*\b(?:aufgabe|aufgaben|to-?do\w*|to-do-liste|zutaten)\b).*\b(?:einkaufsliste|einkaufszettel|liste|einkaufen|einkauf|kaufen|besorgen|mitbringen|holen|brauchen wir|brauche ich|fehlt|fehlen)\b" +
        @"|^what'?s on (?:the|my) (?:shopping )?list", I);

    private static readonly Regex TasksRx = new(
        @"^(?:und\s+)?(?:welche|was für|wie viele|wieviele|zeig\w*|meine|offene|alle|habe ich|hab ich|gibt es|sind)\b.*\b(?:aufgaben|aufgabe|todos?|to-?dos?|to-do-liste|todo-liste|todoliste|erledigungen)\b" +
        @"|^(?:meine\s+|offene\s+|alle\s+)?(?:aufgaben|todos|to-dos|to-do-liste|todo-liste|todoliste)(?:\s+(?:für\s+)?(?:heute|morgen|übermorgen|diese woche))?\s*\??$" +
        @"|^(?:und\s+)?was\s+(?:muss|soll|sollte|müsste|muesste|musst)\s+(?:ich|wir|du)\b.*\b(?:erledigen|machen|tun|schaffen|noch)\b" +
        @"|^(?:und\s+)?was\s+(?:ist|steht|bleibt|wäre)\s+(?:noch\s+|heute\s+|morgen\s+)*(?:zu tun|zu erledigen|offen|liegen geblieben)" +
        @"|\b(?:überfällig|ueberfaellig|überfällige|liegen geblieben)\b" +
        @"|^(?:hab|habe)\s+ich\s+(?:was|etwas|irgendwas)\s+vergessen" +
        @"|^was\s+steht\s+auf\s+(?:meiner|der)\s+(?:to-?do|aufgaben)", I);

    private static readonly Regex BirthdayRx = new(@"\b(?:geburtstag\w*|birthday)\b", I);
    private static readonly Regex BirthdayWhoRx = new(@"^(?:und\s+)?wann\s+(?:hat|haben|ist)\s+(?<who>.+?)\s+geburtstag|geburtstag\s+(?:von|vom|von der)\s+(?<who>.+?)[\s?!.]*$|^(?:wann\s+ist\s+)?(?<who>\p{L}+)s\s+geburtstag", I);
    private static readonly Regex ContactRx = new(
        @"(?:(?:telefon|handy|mobil|festnetz|haus|privat)?nummer|telefon|handy|e-?mail(?:-?adresse)?|mailadresse|adresse|anschrift|kontaktdaten|kontakt)\s+(?:von|vom|von der|von dem|der|des|für|fuer|zu|zur|zum)\s+(?<who>.+?)[\s?!.]*$" +
        @"|^(?:wie|wo)\s+(?:erreiche|erreich|kontaktiere|finde)\s+ich\s+(?<who>.+?)[\s?!.]*$|^wo\s+wohnt\s+(?<who>.+?)[\s?!.]*$|^(?:wie\s+ist\s+)?(?<who>\p{L}+)s\s+(?:telefon|handy)?nummer|^(?:wie ist die|was ist die|hast du die)\s+(?:telefon|handy)?nummer\s+(?:von|vom)\s+(?<who>.+?)[\s?!.]*$", I);
    private static readonly Regex NotesRx = new(@"^(?:und\s+)?(?:welche|was für)\s+notizen|^(?:zeig|zeige)\s+(?:mir\s+)?(?:meine\s+|die\s+)?(?:letzten\s+|neuesten\s+)?notizen|^(?:meine|letzte|letzten|neueste|neuesten)\s+notizen|^notizen\s*\??$|^was\s+(?:hab|habe)\s+ich\s+(?:mir\s+)?(?:notiert|aufgeschrieben)|^(?:hab|habe)\s+ich\s+notizen", I);
    private static readonly Regex NextEventRx = new(@"^(?:und\s+)?(?:wann|was)\s+(?:ist|kommt|wäre|waere)\s+(?:mein|der|unser|denn)?\s*(?:mein\s+)?nächste[rn]?\s+termin|^(?:mein\s+)?nächste[rn]?\s+termin\s*\??$|^was\s+(?:kommt|steht|ist)\s+als\s+nächstes|^was\s+kommt\s+(?:jetzt|heute)\s+noch|^was\s+steht\s+(?:heute\s+)?noch\s+an", I);
    private static readonly Regex WhenRx = new(@"^(?:und\s+)?wann\s+(?<verb>ist|war|sind|waren|kommt|kommen|habe\s+ich|hab\s+ich|haben\s+wir|hatte\s+ich|hatten\s+wir|muss\s+ich|müssen\s+wir|muessen\s+wir|soll\s+ich|geht'?s|geht|gehen\s+wir|fahre\s+ich|fahren\s+wir|treffe\s+ich|treffen\s+wir|beginnt|fängt|startet|findet)\s+(?:ich\s+|wir\s+)?(?:(?:noch\s*mal|nochmal|wieder|denn|eigentlich|das nächste mal|zuletzt|das letzte mal|letztes mal)\s+)*(?:(?:zum|zur|zu|ins|in die|in den|beim|bei der|bei|der|die|das|den|dem|mein|meine|meinen|unser|unsere)\s+)?(?:(?:nächste[nrsm]?|naechste[nrsm]?|letzte[nrsm]?)\s+)?(?<q>.+?)(?:\s+(?:los|an|dran|statt|hin|wieder|termin))?[\s?!.]*$", I);
    private static readonly Regex WhereRx = new(@"^(?:und\s+)?wo\s+(?:ist|sind|war|findet|steigt|treffen wir uns (?:zum|zur|beim)?)\s+(?:denn\s+|eigentlich\s+|nochmal\s+)*(?:der|die|das|den|dem|mein|meine|unser|unsere)?\s*(?:nächste\w*\s+)?(?<q>.+?)(?:\s+statt)?[\s?!.]*$", I);
    private static readonly Regex WhoComesRx = new(@"^(?:und\s+)?wer\s+(?:kommt|geht|ist|fährt|kommen|sind|war)\s+(?:noch\s+|alles\s+|denn\s+|eigentlich\s+)*(?:mit\s+)?(?:dabei\s+)?(?:zum|zur|zu|ins|in die|beim|bei der|bei|mit zum|mit zur)?\s*(?:dem|der|den|das)?\s*(?<q>.+?)(?:\s+mit|\s+dabei)?[\s?!.]*$", I);
    private static readonly Regex IngredientsRx = new(@"\b(?:zutaten|alles für|alles da für|alles im haus für|alles zusammen für|was fehlt (?:mir |uns )?(?:noch )?für|was brauche ich für|was brauchen wir für|einkaufen für|alles (?:dafür|da)\b)", I);

    /// <summary>Wörter, die in einer Frage nach dem Tag nichts über das Was sagen.</summary>
    private static readonly HashSet<string> DayFiller = new(StringComparer.OrdinalIgnoreCase)
    {
        "was", "steht", "stehen", "an", "ansteht", "anstehen", "gibt", "gibts", "termine", "termin", "plan", "überblick", "übersicht", "uebersicht",
        "tagesübersicht", "tagesueberblick", "wann", "habe", "hab", "hast", "zu", "tun", "los", "dran", "wie", "sieht", "aus", "tag", "bin", "frei", "zeit",
        "vor", "mache", "mach", "machen", "haben", "kalender", "agenda", "programm", "so", "denn", "eigentlich", "alles", "etwas", "irgendwas", "welche",
        "sind", "liegt", "demnächst", "bald", "my", "schedule", "plans", "do", "have", "any", "appointments", "meetings", "what", "whats", "what's", "is",
        "schaut", "schauts", "ausschauen", "heute", "today", "morgen", "tomorrow", "gibt's", "kommt", "drin", "eingetragen", "planung", "verplant", "wir",
        "ich", "mein", "meine", "meinem", "im", "mir", "noch", "dann", "auch", "und", "mal", "bitte", "zeig", "zeige", "läuft", "laeuft", "geht", "erledigen",
    };

    /// <summary>Nachschauen - null: keine Lesefrage.</summary>
    private static Reply? Read(Ctx c, string t)
    {
        var mem = c.Mem;
        var question = IsQuestion(t);

        if (IngredientsRx.IsMatch(t) && (question || t.StartsWith("was", StringComparison.OrdinalIgnoreCase))) return Ingredients(c, t);
        var sc = ShopCheckRx.Match(t);
        if (sc.Success && (Regex.IsMatch(t, ListWord + @"|aufgeschrieben|notiert|drauf", I) || IsShopish(c, sc.Groups["item"].Value)))
            return ShopCheck(c, sc.Groups["item"].Value);
        // „Wurde die Milch schon gekauft?“ - wer was gekauft hat, weiß Pukki nicht; aber ob sie noch offen auf der Liste steht
        var bought = Regex.Match(t, @"^(?:wurde|wurden|ist|sind|hat\s+jemand|habt\s+ihr|hast\s+du|haben\s+wir|hab\s+ich|habe\s+ich)\s+(?:(?:die|den|das|schon|denn|eigentlich)\s+)*(?<item>.+?)\s+(?:schon\s+)?(?:gekauft|besorgt|geholt|mitgebracht)\b", I);
        if (bought.Success && ShopItems(c.Space, bought.Groups["item"].Value, force: true) is { Count: > 0 } boughtItems)
        {
            var item = boughtItems[0];
            var on = ListsWith(ShopLists(c), item.Name);
            c.Mem.Topic = "shop"; c.Mem.Subject = item.Name;
            return $"Das weiß ich leider nicht. {item.Name} {Steht([item.Name])} " + (on.Count > 0 ? $"aber noch offen {OnLists(on)}." : "auf keiner Einkaufsliste mehr.");
        }
        var st = StockRx.Match(t);
        if (st.Success && IsShopish(c, st.Groups["item"].Value)) return Stock(c, st.Groups["item"].Value);
        if (ShowShopRx.IsMatch(t) && !BirthdayRx.IsMatch(t)) return ShowShop(c);
        if (TasksRx.IsMatch(t) && (question || !Regex.IsMatch(t, @"^(?:aufgabe|todo)\s+\S", I))) return TasksAnswer(c, PukkiParse.Parse(t, c.Now).Date, overdueOnly: Regex.IsMatch(t, @"überfällig|ueberfaellig|vergessen|liegen geblieben", I));
        if (BirthdayRx.IsMatch(t) && (question || Regex.IsMatch(t, @"^(nächste|kommende|anstehende)", I)))
        {
            var who = BirthdayWhoRx.Match(t);
            return who.Success && !Regex.IsMatch(who.Groups["who"].Value, @"^(heute|morgen|jemand|wer|wir|bald|diese woche|niemand|einer)$", I) ? BirthdayOf(c, who.Groups["who"].Value) : Birthdays(c, t);
        }
        var ct = ContactRx.Match(t);
        if (ct.Success) return ContactAnswer(c, ct.Groups["who"].Value, null, address: Regex.IsMatch(t, @"adresse|anschrift|wohnt", I));
        // „Und wie ist ihre Nummer?“ - die Person aus dem Gespräch
        if (mem.Subject is { Length: > 0 } person && mem.Topic is "birthday" or "contact"
            && Regex.IsMatch(t, @"^(?:und\s+)?(?:wie\s+ist\s+|was\s+ist\s+|hast\s+du\s+|kennst\s+du\s+)?(?:ihre|seine|deren|dessen|die)\s+(?:telefon|handy|mobil)?(?:nummer|adresse|e-?mail(?:-?adresse)?|anschrift)\b", I))
            return ContactAnswer(c, person, null, address: Regex.IsMatch(t, @"adresse|anschrift", I));
        if (NotesRx.IsMatch(t)) return NotesAnswer(c);
        if (MealQuestion(c.Services, c.Space, t, c.Now) != null) return MealAnswer(c, t);
        if (NextEventRx.IsMatch(t)) return NextEvent(c);

        // Ein bestimmter Termin: wann, wo, wer kommt mit
        if (WhenRx.Match(t) is { Success: true } when && !Regex.IsMatch(when.Groups["q"].Value, @"^(heute|morgen|übermorgen|das|es)$", I))
        {
            var q = when.Groups["q"].Value;
            if (Regex.IsMatch(q, @"^(termin|termine|nächsten termin|naechsten termin)$", I)) return NextEvent(c);
            // „Wann war ich zuletzt beim Friseur?“ - der letzte vergangene
            var past = Regex.IsMatch(when.Groups["verb"].Value, @"^(war|waren|hatte|hatten)", I) || Regex.IsMatch(t, @"\b(zuletzt|letzte[nrs]?\s+mal|das letzte mal)\b", I);
            return NamedDay(c.Today, q) is { } named ? Countdown(c, named.Name) ?? EventInfo(c, q) : EventInfo(c, q, detail: past ? "last" : null);
        }
        // „Wo ist das?“, „Wer kommt mit?“ - der Termin aus dem Gespräch
        if (mem.Event is { } focus && Regex.IsMatch(t, @"^(?:und\s+)?(?:wo\s+(?:ist|findet)\s+(?:das|der|die|es|er|sie)(?:\s+statt)?|wer\s+(?:kommt|ist|geht)\s+(?:noch\s+|alles\s+)*(?:mit|dabei))[\s?!.]*$", I))
            return EventInfo(c, focus.Title, detail: t.TrimStart().StartsWith("wo", StringComparison.OrdinalIgnoreCase) || t.Contains(" wo ", StringComparison.OrdinalIgnoreCase) ? "where" : "who");
        if (WhereRx.Match(t) is { Success: true } where && FindEvents(c, where.Groups["q"].Value).Count > 0) return EventInfo(c, where.Groups["q"].Value, detail: "where");
        if (WhoComesRx.Match(t) is { Success: true } who2 && FindEvents(c, who2.Groups["q"].Value).Count > 0) return EventInfo(c, who2.Groups["q"].Value, detail: "who");

        // Woche, Wochenende
        var week = WeekRange(c, t);
        var w = PukkiParse.Parse(t, c.Now);
        var rest = PukkiWords.Words(w.Title).Where(x => !DayFiller.Contains(x) && x is not ("woche" or "wochenende" or "nächste" or "naechste" or "diese" or "kommende" or "tage" or "nächsten" or "naechsten" or "sieben" or "dieser" or "weekend" or "week" or "next" or "this")).ToList();
        if (week != null && rest.Count == 0 && (question || PukkiWords.Words(t).Count <= 4)) return WeekOverview(c, week.Value.From, week.Value.Days, week.Value.Label);

        // Ein Tag: „Was steht morgen an?“, „morgen?“, „Hab ich heute Termine?“
        var lower = t.ToLowerInvariant();
        if (Regex.IsMatch(lower, @"\b(wie sieht|wie schaut)\b.*\b(mein|der|unser) tag\b|\btagesplan\b|\bmein tag heute\b")) return DayOverview(c.Services, c.Space, mem, c.Today, c.Today);
        if (rest.Count == 0 && w.Start == null && (w.Date != null || Regex.IsMatch(lower, @"\b(heute|today|überblick|übersicht|uebersicht|termine|agenda|tagesübersicht|kalender)\b")))
            return DayOverview(c.Services, c.Space, mem, w.Date ?? c.Today, c.Today);

        // Suchen im eigenen Bestand (Schritt 3): „Was hatten wir zum Zahnarzt?“, „Such Quantenphysik“
        if (PukkiSearch.TryAnswer(c.Services, c.Space, t) is { } found) return found;

        // Frage mit Tag und Stichwort: „Ist morgen Zahnarzt?“, „Muss ich am Freitag zum Friseur?“
        if (question && w.Date is { } day && w.Repeat == null)
        {
            // „Hab ich morgen um 15 Uhr Zeit?“ - liegt dann etwas?
            if (w.Start is { } at && rest.Count == 0)
            {
                c.Mem.Topic = "day"; c.Mem.Day = day;
                var busy = DayEvents(c.Services, c.Space, day).Where(e => e.AllDay ? false : e.Start <= at && at < (e.End > e.Start ? e.End : e.Start.AddHours(1))).ToList();
                var onDay = PukkiParse.OnDay(day, c.Today);
                return busy.Count == 0 ? $"{Cap(onDay)} um {at:HH:mm} Uhr ist nichts eingetragen."
                    : $"{Cap(onDay)} um {at:HH:mm} Uhr steht schon etwas an: " + string.Join(", ", busy.Select(e => $"„{e.Title}“ ({e.Start:HH:mm}–{e.End:HH:mm})")) + ".";
            }
            // „Wie viel ist morgen im Lotto-Jackpot?“, „Wie wird morgen das Wetter?“ - Weltwissen, kein Kalender:
            // passt kein Termin dazu, nicht den Tagesüberblick zeigen, sondern weiter (KI bzw. ehrlich „weiß ich nicht“)
            if (WorldQuestionRx.IsMatch(t) && !DayEvents(c.Services, c.Space, day).Any(e => PukkiSearch.Matches(e.Title, rest.Where(k => k.Length >= 3).ToList())))
                return null;
            return DayQuestion(c, day, rest);
        }
        // „Milch?“ - ein Artikel mit Fragezeichen: steht er auf der Liste?
        if (t.EndsWith('?') && PukkiWords.Words(t).Count <= 3 && ShopItems(c.Space, t.TrimEnd('?', ' ')) is { Count: > 0 } items && items.All(i => i.Near == null))
            return ShopCheck(c, t.TrimEnd('?', ' '));
        if (t.EndsWith('?') && PukkiWords.Words(t).Count <= 3 && PukkiWords.Words(t).Any(x => PukkiWords.In(x, PukkiWords.EventWords)) && FindEvents(c, t.TrimEnd('?', ' ')).Count > 0)
            return EventInfo(c, t.TrimEnd('?', ' '));
        return null;
    }

    private static bool IsShopish(Ctx c, string item) =>
        PukkiParse.Parse(item, c.Now) is { Date: null, Start: null } && ShopItems(c.Space, item) is { Count: > 0 };

    // ---------------- Einkaufsliste ----------------

    /// <summary>Alle Einkaufslisten, die ich sehe: eigene und mit mir geteilte.</summary>
    private static List<Target> ShopLists(Ctx c)
    {
        PimStore.EnsureDefaults(c.Space.Db);
        var result = PimStore.Collections(c.Space.Db, PimStore.Calendar)
            .Where(x => x.Components.Contains("VTODO") && Lists.TypeOf(c.Space.Db, x.Id) == Lists.Shop).Select(x => new Target(c.Space, x, false)).ToList();
        foreach (var (_, owner, coll) in SharedLists.For(c.Services.GetRequiredService<ShareStore>(), c.Services.GetRequiredService<Spaces>(), c.Space.User.Id))
            if (coll.Components.Contains("VTODO") && Lists.TypeOf(owner.Db, coll.Id) == Lists.Shop) result.Add(new Target(owner, coll, true));
        return result;
    }

    /// <summary>Auf welchen Einkaufslisten steht der Artikel offen?</summary>
    private static List<string> ListsWith(List<Target> lists, string name)
    {
        var key = ItemKey(name);
        return lists.Where(l => OpenItems(l.Owner, l.Coll).ContainsKey(key)).Select(l => l.Coll.Name).ToList();
    }

    private static string OnLists(List<string> names) => names.Count == 1 ? $"auf der Einkaufsliste „{names[0]}“" : "auf den Einkaufslisten " + JoinAnd(names, quote: true);

    /// <summary>„Ist Milch auf der Liste?“ - ja (auf welcher) oder nein, mit dem Angebot, ihn aufzuschreiben.</summary>
    private static Reply ShopCheck(Ctx c, string itemText)
    {
        var mem = c.Mem;
        var items = ShopItems(c.Space, Regex.Replace(itemText, @"^(?:(?:die|der|das|noch|auch|schon|denn|eigentlich|bereits)\s+)+", "", I), force: true);
        if (items.Count == 0) return "Welchen Artikel meinst du? Frag zum Beispiel: Ist Milch auf der Liste?";
        var lists = ShopLists(c);
        mem.Topic = "shop";
        mem.Subject = items[0].Name;
        var found = items.Select(i => (Item: i, On: ListsWith(lists, i.Name))).ToList();
        var missing = found.Where(f => f.On.Count == 0).Select(f => f.Item).ToList();
        if (missing.Count == 0)
        {
            var names0 = found.Select(f => f.Item.Name).ToList();
            var same = found.Select(f => string.Join("|", f.On)).Distinct().Count() == 1;
            return same ? $"Ja, {JoinAnd(names0)} {Steht(names0)} {OnLists(found[0].On)}."
                : "Ja, das steht alles drauf: " + string.Join(", ", found.Select(f => $"{f.Item.Name} ({string.Join(", ", f.On)})")) + ".";
        }
        var missingNames = missing.Select(m => m.Name).ToList();
        var names = JoinAnd(missingNames);
        var there = found.Where(f => f.On.Count > 0).Select(f => f.Item.Name).ToList();
        var head = there.Count > 0 ? $"{JoinAnd(there)} {Steht(there)} schon drauf. Aber " : "Nein, ";
        var not = lists.Count == 0 ? $"{names} {Steht(missingNames)} nicht auf der Einkaufsliste – du hast noch keine." : $"{names} {Steht(missingNames)} nicht auf der Einkaufsliste.";
        SetAsk(mem, "shop-add", string.Join(", ", missing.Select(ItemText)), null);
        mem.Subject = missing[0].Name;
        return new Reply($"{head}{not} Soll ich {(missing.Count == 1 ? missing[0].Name : "sie")} aufschreiben?", Actions: [Say(0, "Ja", "ja"), Say(1, "Nein", "nein")]);
    }

    /// <summary>„Haben wir noch Milch?“ - das weiß Pukki nicht; auf „warum?“ sagt er, was auf der Einkaufsliste steht.</summary>
    private static Reply Stock(Ctx c, string itemText)
    {
        var mem = c.Mem;
        var items = ShopItems(c.Space, itemText, force: true);
        var item = items.FirstOrDefault();
        mem.Topic = "shop";
        mem.Subject = item?.Name;
        mem.Why = () =>
        {
            if (item == null) return "Weil ich nicht in deine Vorräte schauen kann.";
            var where = item.Section is "Kühlregal" or "Fleisch & Wurst" or "Käse" or "Obst & Gemüse" ? "in den Kühlschrank" : item.Section is "Drogerie" or "Haushalt" ? "in deine Schränke" : "in deine Vorräte";
            var on = ListsWith(ShopLists(c), item.Name);
            var verb = Steht([item.Name]);
            if (on.Count > 0) return $"Weil ich nicht {where} schauen kann. {item.Name} {verb} aber {OnLists(on)}.";
            SetAsk(mem, "shop-add", ItemText(item), null);
            return new Reply($"Weil ich nicht {where} schauen kann. {item.Name} {verb} aber auf keiner Einkaufsliste. Soll ich {(verb == "stehen" ? "sie" : item.Name)} draufsetzen?",
                Actions: [Say(0, "Ja", "ja"), Say(1, "Nein", "nein")]);
        };
        return "Das weiß ich leider nicht.";
    }

    /// <summary>Was auf den Einkaufslisten steht.</summary>
    private static Reply ShowShop(Ctx c)
    {
        var lists = ShopLists(c);
        c.Mem.Topic = "shop";
        if (lists.Count == 0) return "Du hast noch keine Einkaufsliste. Schreib mir einfach, was du brauchst – zum Beispiel „Milch und Brot“ –, dann lege ich eine an.";
        var parts = new List<string>();
        foreach (var l in lists)
        {
            var open = CalendarModel.Tasks(l.Owner, [l.Coll.Id]).Where(x => !x.Completed).ToList();
            var name = l.Coll.Name + (l.Shared ? $" ({l.Owner.User.DisplayName})" : "");
            parts.Add(open.Count == 0 ? $"Die Einkaufsliste „{name}“ ist leer."
                : $"Auf der Einkaufsliste „{name}“ steht:\n" + string.Join("\n", open.Take(40).Select(x => "☐ " + (x.Qty != null ? x.Qty + (x.Unit != null ? " " + x.Unit : "") + " " : "") + (x.Item ?? x.Title)))
                  + (open.Count > 40 ? $"\n… und {open.Count - 40} weitere" : ""));
        }
        return string.Join("\n\n", parts);
    }

    // ---------------- Zutaten ----------------

    /// <summary>„Sind alle Zutaten dafür schon auf der Einkaufsliste?“ - gleicht das Rezept des geplanten Gerichts mit den Einkaufslisten ab.</summary>
    private static Reply Ingredients(Ctx c, string t)
    {
        var mem = c.Mem;
        var dishes = new List<(string Dish, string? Recipe, DateOnly Day)>();
        var w = PukkiParse.Parse(t, c.Now);
        var named = Regex.Match(t, @"\bfür\s+(?:die|den|das|unsere|unseren|meine|meinen)?\s*(?<dish>\p{L}[\p{L}\- ]*?)(?:\s+(?:am|zum|heute|morgen|übermorgen|diese|nächste)\b.*)?[\s?!.]*$", I);
        var dishName = named.Success && !Regex.IsMatch(named.Groups["dish"].Value, @"^(heute|morgen|übermorgen|montag|dienstag|mittwoch|donnerstag|freitag|samstag|sonntag|das essen|dafür)$", I) ? named.Groups["dish"].Value.Trim() : null;
        var plans = MealPlans(c.Services, c.Space);
        if (w.Date is { } day && dishName == null) dishes = PlannedMeals(c, plans, day, 1);
        else if (dishName != null)
        {
            dishes = PlannedMeals(c, plans, c.Today, 21).Where(d => d.Dish.Contains(dishName, StringComparison.OrdinalIgnoreCase) || dishName.Contains(d.Dish, StringComparison.OrdinalIgnoreCase)).Take(1).ToList();
            if (dishes.Count == 0) dishes.Add((Cap(dishName), FindRecipe(c.Space, dishName)?.SyncId, c.Today));
        }
        else if (mem.Dishes is { Count: > 0 } known) dishes = known;
        if (dishes.Count == 0) return "Für welches Gericht? Frag mich zum Beispiel: Habe ich alles für die Lasagne am Samstag?";
        // Mehrere Gerichte: die mit Rezept zählen
        if (dishes.Count > 1 && dishes.Any(d => d.Recipe != null)) dishes = dishes.Where(d => d.Recipe != null).ToList();
        mem.Dishes = dishes;
        mem.Topic = "meal";

        var lists = ShopLists(c);
        var answers = new List<string>();
        var missingAll = new List<string>();
        var notes = NotesDb.Of(c.Space);
        foreach (var (dish, recipe, _) in dishes)
        {
            var note = recipe != null ? notes.BySyncId(recipe) : null;
            var lines = note != null ? Recipes.Ingredients(note) : [];
            if (lines.Count == 0) { answers.Add($"Zu „{dish}“ ist leider keine Zutatenliste hinterlegt."); continue; }
            var missing = lines.Where(line => ShopItems(c.Space, line, force: true) is var parts && parts.Count > 0 && parts.Any(p => ListsWith(lists, p.Name).Count == 0)).ToList();
            if (missing.Count == 0)
            {
                var on = lists.Count == 1 ? $"auf der Einkaufsliste „{lists[0].Coll.Name}“" : "auf deinen Einkaufslisten";
                answers.Add($"Alle Zutaten für „{dish}“ stehen schon {on}.");
            }
            else
            {
                answers.Add($"Von den Zutaten für „{dish}“ finde ich {JoinAnd(missing, quote: true)} auf keiner Einkaufsliste.");
                missingAll.AddRange(missing);
            }
        }
        if (missingAll.Count == 0) return string.Join("\n", answers);
        SetAsk(mem, "ingredients", null, null);
        mem.Items = missingAll;
        return new Reply(string.Join("\n", answers) + " Soll ich die fehlenden Zutaten auf die Einkaufsliste setzen?", Actions: [Say(0, "Ja", "ja"), Say(1, "Nein", "nein")]);
    }

    /// <summary>Gerichte im Essensplan ab einem Tag (für Zutaten und Bezüge).</summary>
    private static List<(string Dish, string? Recipe, DateOnly Day)> PlannedMeals(Ctx c, List<Target> plans, DateOnly from, int days)
    {
        var result = new List<(string, string?, DateOnly)>();
        var fromUtc = Dav.Ics.ToUtc(from.ToDateTime(TimeOnly.MinValue), c.Space.Zone);
        foreach (var p in plans)
            foreach (var e in CalendarModel.Events(p.Owner, fromUtc, fromUtc.AddDays(days), [p.Coll.Id]))
            {
                var day = DateOnly.FromDateTime(e.AllDay ? DateTime.Parse(e.Start, CultureInfo.InvariantCulture) : Local(e.Start, c.Space.Zone));
                if (day >= from && day < from.AddDays(days)) result.Add((e.Title, string.IsNullOrEmpty(e.Recipe) ? null : e.Recipe, day));
            }
        return result.OrderBy(x => x.Item3).ToList();
    }

    /// <summary>„Was gibt es heute zu essen?“ mit Gesprächszusammenhang (Tag und Gerichte für „Und morgen?“ und „Zutaten dafür?“).</summary>
    private static Reply MealAnswer(Ctx c, string t)
    {
        var answer = MealQuestion(c.Services, c.Space, t, c.Now) ?? "Was gibt es denn? Zum Beispiel: abendessen Spaghetti";
        var w = PukkiParse.Parse(Regex.Replace(t, @"[?!.,]", " "), c.Now);
        var day = w.Date ?? c.Today;
        c.Mem.Topic = "meal";
        c.Mem.Day = day;
        c.Mem.Dishes = PlannedMeals(c, MealPlans(c.Services, c.Space), day, WeekWord.IsMatch(t) ? 7 : 1);
        return answer;
    }

    // ---------------- Aufgaben ----------------

    /// <summary>Offene Aufgaben: an einem Tag fällig (heute mit überfälligen), nur überfällige, oder alle offenen.</summary>
    private static Reply TasksAnswer(Ctx c, DateOnly? day, bool overdueOnly = false)
    {
        var mem = c.Mem;
        mem.Topic = "tasks";
        mem.Day = day;
        var lists = PimStore.Collections(c.Space.Db, PimStore.Calendar).Where(x => x.Components.Contains("VTODO") && Lists.TypeOf(c.Space.Db, x.Id) == Lists.Tasks).Select(x => (c.Space, x))
            .Concat(SharedLists.For(c.Services.GetRequiredService<ShareStore>(), c.Services.GetRequiredService<Spaces>(), c.Space.User.Id)
                .Where(x => x.Coll.Components.Contains("VTODO") && Lists.TypeOf(x.Owner.Db, x.Coll.Id) == Lists.Tasks).Select(x => (x.Owner, x.Coll))).ToList();
        var open = new List<(string Title, DateOnly? Due, string? Time)>();
        foreach (var (owner, coll) in lists)
            foreach (var x in CalendarModel.Tasks(owner, [coll.Id]).Where(x => !x.Completed))
            {
                DateOnly? due = x.Due == null ? null : x.DueAllDay ? DateOnly.Parse(x.Due[..10], CultureInfo.InvariantCulture) : DateOnly.FromDateTime(Local(x.Due, c.Space.Zone));
                open.Add((x.Title, due, x.Due != null && !x.DueAllDay ? Local(x.Due, c.Space.Zone).ToString("HH:mm") : null));
            }
        string Line((string Title, DateOnly? Due, string? Time) x) => "☐ " + x.Title +
            (x.Due is not { } d ? "" : d < c.Today ? $" (überfällig seit {PukkiParse.DayText(d, c.Today)})" : day == null ? $" – fällig {PukkiParse.DayText(d, c.Today)}{(x.Time != null ? " " + x.Time : "")}" : x.Time != null ? " – " + x.Time : "");
        if (overdueOnly)
        {
            var late = open.Where(x => x.Due < c.Today).OrderBy(x => x.Due).ToList();
            return late.Count == 0 ? "Überfällig ist nichts – sehr gut!" : "Überfällig:\n" + string.Join("\n", late.Select(Line));
        }
        if (day is { } dd)
        {
            var due = open.Where(x => x.Due == dd || (dd == c.Today && x.Due < dd)).OrderBy(x => x.Due).ThenBy(x => x.Time).ToList();
            if (due.Count == 0) return $"{Cap(PukkiParse.OnDay(dd, c.Today))} ist keine Aufgabe fällig." + (open.Count > 0 ? $" Offen sind insgesamt {open.Count} – frag mich „Welche Aufgaben sind offen?“." : "");
            return $"{Cap(PukkiParse.OnDay(dd, c.Today))} zu erledigen:\n" + string.Join("\n", due.Select(Line));
        }
        if (open.Count == 0) return "Du hast keine offenen Aufgaben. 🎉";
        var sorted = open.OrderBy(x => x.Due ?? DateOnly.MaxValue).ThenBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        return "Offene Aufgaben:\n" + string.Join("\n", sorted.Take(25).Select(Line)) + (sorted.Count > 25 ? $"\n… und {sorted.Count - 25} weitere" : "");
    }

    // ---------------- Woche ----------------

    /// <summary>„diese Woche“, „nächste Woche“, „am Wochenende“, „die nächsten Tage“ - Beginn, Anzahl Tage, Text.</summary>
    private static (DateOnly From, int Days, string Label)? WeekRange(Ctx c, string t)
    {
        var today = c.Today;
        if (Regex.IsMatch(t, @"\b(nächste|naechste|kommende|nächsten|naechsten)\s+woche\b|\bnext week\b", I))
        {
            var monday = today.AddDays(7 - (((int)today.DayOfWeek + 6) % 7));
            return (monday, 7, "Nächste Woche");
        }
        if (Regex.IsMatch(t, @"\b(diese|dieser|die)\s+woche\b|\bthis week\b|\b(die\s+)?(nächsten|naechsten|kommenden)\s+(tage|sieben tage|7 tage)\b|\bin der woche\b|^woche\??$", I))
            return (today, 7, "In den nächsten sieben Tagen");
        if (Regex.IsMatch(t, @"\b(wochenende|weekend)\b", I))
        {
            if (today.DayOfWeek == DayOfWeek.Sunday) return (today, 1, "Am Wochenende");
            var sat = today.AddDays(((int)DayOfWeek.Saturday - (int)today.DayOfWeek + 7) % 7);
            return (sat, 2, "Am Wochenende");
        }
        return null;
    }

    private static Reply WeekOverview(Ctx c, DateOnly from, int days, string label)
    {
        c.Mem.Topic = "day";
        c.Mem.Day = from;
        var parts = new List<string>();
        for (var d = from; d < from.AddDays(days); d = d.AddDays(1))
            if (Overview(c.Services, c.Space, d, c.Today) is { } o) parts.Add(o);
        return parts.Count == 0 ? $"{label} ist nichts eingetragen." : string.Join("\n\n", parts);
    }

    /// <summary>„Ist morgen Zahnarzt?“ - passt ein Termin des Tages zu den Stichwörtern? Sonst der Überblick.</summary>
    private static Reply DayQuestion(Ctx c, DateOnly day, List<string> keys)
    {
        c.Mem.Topic = "day";
        c.Mem.Day = day;
        var events = DayEvents(c.Services, c.Space, day);
        var terms = keys.Where(k => k.Length >= 3).ToList();
        var hits = terms.Count == 0 ? [] : events.Where(e => PukkiSearch.Matches(e.Title, terms)).ToList();
        var when = PukkiParse.OnDay(day, c.Today);
        if (hits.Count > 0)
            return $"Ja, {when}: " + string.Join(", ", hits.Select(e => (e.AllDay ? "" : e.Start.ToString("HH:mm") + " ") + e.Title)) + ".";
        // „Gibt es morgen Pizza?“ - eine Frage nach dem Essen
        if (terms.Any(k => PukkiWords.In(k, PukkiWords.DishWords) || PukkiWords.In(k, PukkiWords.ShopWords)) && MealPlans(c.Services, c.Space).Count > 0)
        {
            var meals = PlannedMeals(c, MealPlans(c.Services, c.Space), day, 1);
            c.Mem.Topic = "meal";
            c.Mem.Dishes = meals;
            var hit = meals.FirstOrDefault(m => PukkiSearch.Matches(m.Dish, terms));
            if (hit.Dish != null) return $"Ja, {when} gibt es {hit.Dish}.";
            return meals.Count == 0 ? $"{Cap(when)} steht noch nichts im Essensplan."
                : $"Laut Essensplan gibt es {when} {JoinAnd(meals.Select(m => m.Dish))}.";
        }
        var overview = Overview(c.Services, c.Space, day, c.Today);
        var past = day < c.Today;
        if (overview == null) return $"{Cap(when)} {(past ? "stand" : "steht")} nichts im Kalender.";
        return terms.Count > 0 ? $"Dazu finde ich {when} nichts im Kalender. Das {(past ? "stand" : "steht")} an:\n" + overview : overview;
    }

    // ---------------- Termine finden ----------------

    private sealed record Hit(EventDto E, DateTime Local, UserSpace Owner, Collection Coll, bool Shared);

    /// <summary>Termine aus eigenen und geteilten Kalendern (ohne Essenspläne) rund um heute, die zum Suchwort passen - je Termin ein Vorkommen.</summary>
    private static List<Hit> FindEvents(Ctx c, string q)
    {
        // „Zahnarzttermin“ → „Zahnarzt“
        var terms = PukkiSearch.Terms(Regex.Replace(q, @"(?<=\p{L}{3})termin(e)?\b", "", I));
        if (terms.Count == 0) return [];
        var now = DateTime.UtcNow + Shift;
        var hits = new List<Hit>();
        foreach (var (owner, coll, shared) in Calendars(c.Services, c.Space))
            foreach (var e in CalendarModel.Events(owner, now.AddDays(-400), now.AddDays(400), [coll.Id]))
                // Ein Wort im Titel, oder alle irgendwo - nur der Ort allein reicht nicht („Schlachtplatte in Vöhringen“ ist nicht die Biotonne dort)
                if (PukkiSearch.Matches(e.Title, terms) || terms.All(t => PukkiSearch.Matches(e.Title + " " + e.Location + " " + e.Description, [t])))
                    hits.Add(new Hit(e, Local(e.Start, c.Space.Zone), owner, coll, shared));
        return hits.OrderBy(h => h.Local < c.Now.Date ? 1 : 0).ThenBy(h => h.Local < c.Now.Date ? -h.Local.Ticks : h.Local.Ticks).DistinctBy(h => (h.Coll.Id, h.E.Uri)).ToList();
    }

    /// <summary>„am Samstag, 25.10., um 19:30 Uhr“ bzw. „morgen um 15:00 Uhr“, ganztägig ohne Uhrzeit.</summary>
    private static string At(Ctx c, DateTime local, bool allDay)
    {
        var day = DateOnly.FromDateTime(local);
        var on = PukkiParse.OnDay(day, c.Today);
        if (day.Year != c.Today.Year && !on.Contains(day.Year.ToString())) on += day.ToString(" yyyy");
        return allDay ? on : on + (on.StartsWith("am ") ? "," : "") + " um " + local.ToString("H:mm") + " Uhr";
    }

    private static List<string> People(EventDto e) =>
        (e.Attendees ?? []).Select(a => string.IsNullOrWhiteSpace(a.Name) ? a.Email : a.Name!).Where(n => n.Length > 0).Distinct().ToList();

    /// <summary>
    /// „Wann ist das Konzert?“: der nächste passende Termin mit Ort und Teilnehmern, Verweis zum Antippen; im geteilten
    /// Kalender die Rückfrage „Ist das dein Termin?“, sonst - ohne Erinnerung - das Angebot, vorher zu erinnern.
    /// detail: where (nur Ort), who (nur Teilnehmer). skipCurrent: der zuletzt genannte war es nicht - weitersuchen.
    /// </summary>
    private static Reply EventInfo(Ctx c, string q, string? detail = null, bool skipCurrent = false)
    {
        var mem = c.Mem;
        var term = Regex.Replace(q.Trim().TrimEnd('?', '.', '!'), @"^(?:der|die|das|den|dem|mein|meine|meinen|unser|unsere)\s+", "", I).Trim();
        if (skipCurrent && mem.Event is { } cur) (mem.Items ??= []).Add(cur.Uri);
        else if (!skipCurrent) mem.Items = null;
        var skip = mem.Items ?? [];
        mem.Topic = "when";
        mem.Subject = term;
        var all = FindEvents(c, term);
        var hits = all.Where(h => !skip.Contains(h.E.Uri)).ToList();
        var shown = Cap(Regex.Replace(term, @"(?<=\p{L}{3})termin(e)?\b", "", I).Trim());
        if (hits.Count == 0)
        {
            mem.Event = null;
            if (skipCurrent) return $"Einen weiteren Termin zu „{shown}“ finde ich nicht.";
            // Nicht im Kalender: mit KI darf sie nachsehen (etwa ein Fest im Ort), sonst bleibt es bei dieser Antwort
            return new Reply($"Zu „{shown}“ finde ich keinen Termin – weder in den letzten zwölf Monaten noch im kommenden Jahr.", Unknown: true);
        }
        var next = hits.Where(h => h.Local >= c.Now.Date).ToList();
        if (detail == "last")
        {
            var last = hits.Where(h => h.Local < c.Now.Date).OrderByDescending(h => h.Local).FirstOrDefault();
            if (last == null)
                return next.Count > 0 ? $"Einen vergangenen Termin zu „{shown}“ finde ich nicht – der nächste ist „{next[0].E.Title}“ {At(c, next[0].Local, next[0].E.AllDay)}."
                    : $"Einen vergangenen Termin zu „{shown}“ finde ich nicht.";
            Focus(c, last);
            var refsLast = new List<ChatRef>();
            if (last.Owner.User.Id == c.Space.User.Id) try { refsLast.Add(ChatApi.RefFor(last.Owner, ChatRef.TypeEvent, collection: last.Coll.Id, item: last.E.Uri)); }
            catch (Exception ex) when (ex is KeyNotFoundException or ArgumentException or InvalidOperationException) { }
            return new Reply($"Zuletzt war „{last.E.Title}“ {At(c, last.Local, last.E.AllDay)}." + (next.Count > 0 ? $" Der nächste Termin ist {At(c, next[0].Local, next[0].E.AllDay)}." : ""), refsLast);
        }
        var h0 = next.FirstOrDefault() ?? hits[0];
        var e = h0.E;
        Focus(c, h0);
        var refs = new List<ChatRef>();
        if (h0.Owner.User.Id == c.Space.User.Id) try { refs.Add(ChatApi.RefFor(h0.Owner, ChatRef.TypeEvent, collection: h0.Coll.Id, item: e.Uri)); }
        catch (Exception ex) when (ex is KeyNotFoundException or ArgumentException or InvalidOperationException) { }
        var when = At(c, h0.Local, e.AllDay);
        var people = People(e);

        string text;
        if (detail == "where")
            text = string.IsNullOrWhiteSpace(e.Location) ? $"Für „{e.Title}“ {when} ist kein Ort eingetragen." : $"„{e.Title}“ {when} – Ort: {e.Location}.";
        else if (detail == "who")
            text = people.Count == 0 ? $"Für „{e.Title}“ {when} sind keine Teilnehmer eingetragen." : $"Bei „{e.Title}“ {when} sind dabei: {JoinAnd(people)}.";
        else if (next.Count == 0)
            text = $"Zu „{shown}“ steht kein kommender Termin im Kalender. Zuletzt war „{e.Title}“ {At(c, h0.Local, e.AllDay)}.";
        else
        {
            text = $"Ich habe im Kalender „{h0.Coll.Name}“ einen Eintrag zu „{shown}“ gefunden: „{e.Title}“ {when}"
                + (string.IsNullOrWhiteSpace(e.Location) ? "" : $" (Ort: {e.Location})") + "."
                + (people.Count > 0 ? $" Außerdem dabei: {JoinAnd(people)}." : "");
            var more = next.Skip(1).Take(2).Select(x => $"„{x.E.Title}“ {At(c, x.Local, x.E.AllDay)}").ToList();
            if (more.Count > 0) text += "\nDanach: " + string.Join("; ", more) + ".";
        }
        if (next.Count > 0 && h0.Shared && detail == null)
        {
            SetAsk(mem, "mine", null, null);
            return new Reply(text + $"\nDer Kalender „{h0.Coll.Name}“ ist ein geteilter Kalender. Ist das dein Termin oder der von jemand anderem?", refs,
                [Say(0, "Meiner", "meiner"), Say(1, "Nicht meiner", "nicht meiner")]);
        }
        if (detail == null && OfferReminder(c, text) is { } offer) return offer with { Refs = refs };
        return new Reply(text, refs);
    }

    /// <summary>Diesen Termin merkt sich Pukki fürs Gespräch - mit Erinnerung ja/nein, darf ich ändern, kommt er noch.</summary>
    private static void Focus(Ctx c, Hit h)
    {
        var mem = c.Mem;
        var canWrite = !h.Shared || SharedLists.For(c.Services.GetRequiredService<ShareStore>(), c.Services.GetRequiredService<Spaces>(), c.Space.User.Id)
            .Any(x => x.Owner.User.Id == h.Owner.User.Id && x.Coll.Id == h.Coll.Id && !x.Share.ReadOnly.Contains(c.Space.User.Id));
        mem.Event = (h.Owner.User.Id, h.Coll.Id, h.E.Uri, h.E.Start, h.E.Title);
        mem.EventReminder = h.E.Reminder != null;
        mem.EventWritable = canWrite && h.E.CanWrite;
        mem.EventUpcoming = h.Local >= c.Now.Date;
    }

    /// <summary>Der Termin aus dem Gespräch hat keine Erinnerung: anbieten, vorher zu erinnern. Null: nichts anzubieten.</summary>
    private static Reply? OfferReminder(Ctx c, string text)
    {
        var mem = c.Mem;
        if (mem.Event == null || mem.EventReminder || !mem.EventWritable || !mem.EventUpcoming) return null;
        SetAsk(mem, "reminder", null, null);
        return new Reply(text + "\nFür den Termin ist noch keine Erinnerung eingestellt. Soll ich dich vorher erinnern?",
            Actions: [Say(0, "1 Stunde vorher", "1 Stunde vorher"), Say(1, "1 Tag vorher", "1 Tag vorher"), Say(2, "Nein", "nein")]);
    }

    /// <summary>Erinnerung am Termin aus dem Gespräch setzen (Minuten vorher) - mit „Rückgängig“.</summary>
    private static Reply RemindEvent(Ctx c, int minutes)
    {
        var mem = c.Mem;
        if (mem.Event is not { } ev) return "An welchen Termin soll ich dich erinnern? Frag mich zum Beispiel erst: Wann ist das Konzert?";
        if (!mem.EventWritable) return $"Den Termin „{ev.Title}“ darf ich nicht ändern – der Kalender ist für dich nur lesbar. Eine Erinnerung stellst du dir am besten selbst ein.";
        var owner = c.Services.GetRequiredService<Spaces>().Get(ev.Owner);
        var start = SetReminder(owner, ev.Calendar, ev.Uri, ev.Start, minutes);
        mem.EventReminder = true;
        var fire = Local(start, c.Space.Zone).AddMinutes(-minutes);
        if (CalendarModel.ParseIso(start, out var dateOnly) is var _ && dateOnly) fire = CalendarModel.ParseIso(start, out _).AddMinutes(-minutes);
        var text = $"Okay, ich erinnere dich {At(c, fire, false)}.";
        if (!ChatSettings.Of(c.Space.Db).PukkiReminders) text += " Meine Erinnerungen im Chat sind bei dir ausgeschaltet – einschalten kannst du sie unter Einstellungen → Chat.";
        var made = new List<Created> { new("reminder", ev.Owner, ev.Calendar, ev.Uri, 0, $"Erinnerung an „{ev.Title}“", ev.Start) };
        return Made(text, made, "reminder", ev.Title, 0);
    }

    /// <summary>Erinnerung (Minuten vorher, null: keine) an einem Termin setzen und dabei alles andere lassen. Gibt den Beginn (ISO) zurück.</summary>
    private static string SetReminder(UserSpace owner, long calendar, string uri, string start, int? minutes)
    {
        var at = CalendarModel.ParseIso(start, out _).ToUniversalTime();
        var e = CalendarModel.Events(owner, at.AddDays(-2), at.AddDays(2), [calendar]).FirstOrDefault(x => x.Uri == uri)
            ?? throw new KeyNotFoundException("Den Termin gibt es nicht mehr.");
        CalendarModel.SaveEvent(owner, new EventInput
        {
            Calendar = calendar, Uri = uri, Title = e.Title, Location = e.Location, Description = e.Description, Start = e.Start, End = e.End, AllDay = e.AllDay,
            Busy = e.Busy, Categories = e.Categories, Reminder = minutes, Notify = false, Instance = e.Recurring ? e.Start : null,
        });
        return e.Start;
    }

    /// <summary>Der nächste Termin überhaupt (eigene und geteilte Kalender).</summary>
    private static Reply NextEvent(Ctx c)
    {
        var now = DateTime.UtcNow + Shift;
        Hit? best = null;
        foreach (var (owner, coll, shared) in Calendars(c.Services, c.Space))
            foreach (var e in CalendarModel.Events(owner, now, now.AddDays(366), [coll.Id]))
            {
                var local = Local(e.Start, c.Space.Zone);
                var end = Local(e.End, c.Space.Zone);
                if ((e.AllDay ? DateOnly.FromDateTime(local) < c.Today : end <= c.Now)) continue;
                if (best == null || local < best.Local) best = new Hit(e, local, owner, coll, shared);
            }
        if (best == null) return "In deinem Kalender steht kein kommender Termin.";
        c.Mem.Topic = "when";
        Focus(c, best);
        var text = $"Dein nächster Termin: „{best.E.Title}“ {At(c, best.Local, best.E.AllDay)}" + (string.IsNullOrWhiteSpace(best.E.Location) ? "" : $" (Ort: {best.E.Location})") + ".";
        var refs = new List<ChatRef>();
        if (best.Owner.User.Id == c.Space.User.Id) try { refs.Add(ChatApi.RefFor(best.Owner, ChatRef.TypeEvent, collection: best.Coll.Id, item: best.E.Uri)); }
        catch (Exception ex) when (ex is KeyNotFoundException or ArgumentException or InvalidOperationException) { }
        return new Reply(text, refs);
    }

    // ---------------- Geburtstage, Kontakte, Notizen ----------------

    /// <summary>Wann der Geburtstag in diesem bzw. nächstem Jahr ist (ab heute) und wie alt jemand wird.</summary>
    private static (DateOnly Day, int? Age)? NextBirthday(string? birthday, DateOnly today)
    {
        if (birthday == null || !DateOnly.TryParse(birthday, CultureInfo.InvariantCulture, out var b)) return null;
        DateOnly Make(int y) => new(y, b.Month, b.Month == 2 && b.Day == 29 && !DateTime.IsLeapYear(y) ? 28 : b.Day);
        var next = Make(today.Year);
        if (next < today) next = Make(today.Year + 1);
        return (next, b.Year > 1900 ? next.Year - b.Year : null);
    }

    private static List<ContactDto> Contacts(Ctx c)
    {
        PimStore.EnsureDefaults(c.Space.Db);
        var books = PimStore.Collections(c.Space.Db, PimStore.AddressBook).Select(x => x.Id).ToList();
        return ContactModel.All(c.Space, books);
    }

    /// <summary>Kontakte, die zu allen Suchwörtern passen (Name, Spitzname, Firma, Titel, Notiz).</summary>
    private static List<ContactDto> FindContacts(Ctx c, string who)
    {
        var terms = PukkiSearch.Terms(who);
        if (terms.Count == 0) return [];
        return Contacts(c).Where(x => terms.All(term => PukkiSearch.Matches(x.DisplayName + " " + x.Nickname + " " + x.Given + " " + x.Family + " " + x.Org + " " + x.Title + " " + x.Note, [term]))).ToList();
    }

    /// <summary>„Wer hat Geburtstag?“ - Kontakte und Geburtstagstermine im Zeitraum (heute, morgen, Woche, sonst 30 Tage).</summary>
    private static Reply Birthdays(Ctx c, string t)
    {
        c.Mem.Topic = "birthday";
        var w = PukkiParse.Parse(t, c.Now);
        var (from, days, label) = w.Date is { } d ? (d, 1, Cap(PukkiParse.OnDay(d, c.Today)))
            : WeekRange(c, t) is { } wr ? (wr.From, wr.Days, wr.Label) : (c.Today, 30, "In den nächsten 30 Tagen");
        var list = new List<(DateOnly Day, string Text, string[] Names)>();
        foreach (var x in Contacts(c))
            if (NextBirthday(x.Birthday, c.Today) is { } nb && nb.Day >= from && nb.Day < from.AddDays(days))
                list.Add((nb.Day, x.DisplayName + (nb.Age is int a ? $" (wird {a})" : ""), new[] { x.Given, x.Family, x.Nickname, x.DisplayName }.Where(s => s.Length >= 3).ToArray()));
        // Geburtstagstermine - ohne die, die schon als Kontakt dastehen („Geburtstag Oma“ = Hilde „Oma“ Berger)
        for (var day = from; day < from.AddDays(days); day = day.AddDays(1))
            foreach (var e in DayEvents(c.Services, c.Space, day).Where(e => Regex.IsMatch(e.Title, @"geburtstag|birthday|🎂", I)))
                if (!list.Any(l => l.Day == day && l.Names.Any(n => e.Title.Contains(n, StringComparison.OrdinalIgnoreCase)))) list.Add((day, e.Title, []));
        if (list.Count == 0) return $"{label} hat niemand Geburtstag – jedenfalls steht nichts in deinen Kontakten oder im Kalender.";
        return $"{label} {(days == 1 ? "hat Geburtstag" : "haben Geburtstag")}:\n" + string.Join("\n", list.OrderBy(l => l.Day).Select(l => $"🎂 {Cap(PukkiParse.DayText(l.Day, c.Today))} – {l.Text}"));
    }

    /// <summary>„Wann hat Oma Geburtstag?“ - aus den Kontakten, sonst aus dem Kalender.</summary>
    private static Reply BirthdayOf(Ctx c, string who)
    {
        c.Mem.Topic = "birthday";
        var name = Regex.Replace(who.Trim(), @"^(?:die|der|das|den|meine|mein|meinen|unsere|unser)\s+", "", I);
        // „Omas Geburtstag“ → Oma
        if (FindContacts(c, name).Count == 0 && name.EndsWith('s') && name.Length > 3) name = name[..^1];
        foreach (var x in FindContacts(c, name))
            if (NextBirthday(x.Birthday, c.Today) is { } nb)
            {
                c.Mem.Subject = x.DisplayName;
                var n = nb.Day.DayNumber - c.Today.DayNumber;
                return $"{x.DisplayName} hat {LongDay(nb.Day, c.Today)} Geburtstag" + (nb.Age is int a ? $" und wird {a}" : "") + (n == 0 ? " – heute!" : n <= 2 ? "." : $" – in {n} Tagen.");
            }
        var hit = FindEvents(c, "geburtstag " + name).FirstOrDefault(h => Regex.IsMatch(h.E.Title, "geburtstag", I) && PukkiSearch.Matches(h.E.Title, PukkiSearch.Terms(name)));
        if (hit != null) return $"„{hit.E.Title}“ steht {At(c, hit.Local, true)} im Kalender.";
        return $"Den Geburtstag von {Cap(name)} finde ich weder in deinen Kontakten noch im Kalender.";
    }

    /// <summary>Kontaktdaten zu einem Namen (oder zur Firma, etwa „Zahnarzt“ → „Zahnarztpraxis am Markt“) - je Angabe eine Zeile, mit Verweis.</summary>
    private static Reply ContactAnswer(Ctx c, string who, string? head, bool address = false)
    {
        var mem = c.Mem;
        var name = Regex.Replace(who.Trim().TrimEnd('?', '.', '!'), @"^(?:die|der|das|den|dem|meine|mein|meinem|meiner|meinen|unsere|unser|unserem|von|vom)\s+", "", I).Trim();
        name = Regex.Replace(name, @"(?<=\p{L}{3})termin(e)?\b", "", I);
        mem.Topic = "contact";
        mem.Subject = name;
        var found = FindContacts(c, name);
        var lines = new List<string>();
        var refs = new List<ChatRef>();
        foreach (var x in found.Take(3))
        {
            lines.Add(x.DisplayName + (x.Org.Length > 0 && x.Org != x.DisplayName ? $" ({x.Org})" : ""));
            lines.AddRange(x.Emails.Select(e => e.Value));
            lines.AddRange(x.Phones.Select(p => p.Value));
            if (address || head == null)
                lines.AddRange(x.Addresses.Select(a => string.Join(", ", new[] { a.Street, (a.Zip + " " + a.City).Trim(), a.Country }.Where(s => !string.IsNullOrWhiteSpace(s)))).Where(s => s.Length > 0));
            try { refs.Add(ChatApi.RefFor(c.Space, ChatRef.TypeContact, collection: x.Book, item: x.Uri)); }
            catch (Exception ex) when (ex is KeyNotFoundException or ArgumentException or InvalidOperationException) { }
            lines.Add("");
        }
        // Ohne Kontakt: Teilnehmer und Organisator des Termins aus dem Gespräch
        if (found.Count == 0 && mem.Event is { } ev && head != null)
        {
            var owner = c.Services.GetRequiredService<Spaces>().Get(ev.Owner);
            var at = CalendarModel.ParseIso(ev.Start, out _).ToUniversalTime();
            var e = CalendarModel.Events(owner, at.AddDays(-2), at.AddDays(2), [ev.Calendar]).FirstOrDefault(x => x.Uri == ev.Uri);
            var mails = (e?.Attendees ?? []).Select(a => (a.Name is { Length: > 0 } n ? n + "\n" : "") + a.Email).Concat(e?.Organizer is { Length: > 0 } o ? [o] : []).Distinct().ToList();
            if (mails.Count > 0) lines.AddRange(mails);
        }
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        if (address && found.Count > 0 && found.All(x => x.Addresses.Count == 0))
            return new Reply($"Für {found[0].DisplayName} ist keine Adresse hinterlegt." + (lines.Count > 1 ? " Das habe ich:\n" + string.Join("\n", lines) : ""), refs);
        if (lines.Count == 0)
            return head != null ? $"{head} Kontaktdaten zu „{Cap(name)}“ finde ich leider keine." : $"Zu „{Cap(name)}“ finde ich keinen Kontakt.";
        var intro = head != null ? $"{head} Ich kann dir aber die Kontaktdaten nennen, die ich zu „{Cap(name)}“ finde:" : found.Count == 1 ? "Hier sind die Kontaktdaten:" : $"Das habe ich zu „{Cap(name)}“:";
        return new Reply(intro + "\n" + string.Join("\n", lines), refs);
    }

    /// <summary>Die letzten Notizen.</summary>
    private static Reply NotesAnswer(Ctx c)
    {
        c.Mem.Topic = "notes";
        var notes = NotesDb.Of(c.Space).GetAllActive().OrderByDescending(n => n.Modified).Take(5).ToList();
        if (notes.Count == 0) return "Du hast noch keine Notizen. Schreib mir zum Beispiel „notiz Ideen für den Urlaub“.";
        var refs = new List<ChatRef>();
        foreach (var n in notes)
            try { refs.Add(ChatApi.RefFor(c.Space, ChatRef.TypeNote, noteId: n.Id)); }
            catch (Exception ex) when (ex is KeyNotFoundException or ArgumentException or InvalidOperationException) { }
        return new Reply("Deine letzten Notizen:\n" + string.Join("\n", notes.Select(n => "📝 " + n.DisplayTitle)), refs);
    }
}
