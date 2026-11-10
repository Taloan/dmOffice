using System.Text.RegularExpressions;
using DmOffice.Core;
using DmOffice.Pim;

namespace DmOffice.Chat;

/// <summary>
/// Pukki im Gespräch: offene Rückfragen („Soll ich sie aufschreiben?“ → „ja“), Bezüge („Und morgen?“, „Warum nicht?“,
/// „Und Brot auch“), Korrekturen („Mach 16 Uhr draus“, „Nein, Donnerstag“) und Sätze in Befehlsform („Schreib Käse auf
/// die Liste“, „Kannst du morgen 15 Uhr Zahnarzt eintragen?“, „Erinnere mich morgen um 9 an den Müll“). Was Pukki nicht
/// kann (Mails schreiben, Termine absagen, anrufen), sagt er ehrlich - und legt dafür nichts Falsches an.
/// </summary>
public static partial class Pukki
{
    // ---------------- Ja, nein, zurück ----------------

    private static readonly HashSet<string> YesCore = new(StringComparer.OrdinalIgnoreCase)
    {
        "ja", "jo", "jap", "jep", "jup", "jawohl", "yes", "yep", "yeah", "ok", "okay", "okey", "oki", "genau", "gerne", "gern", "klar", "sicher", "natürlich",
        "natuerlich", "unbedingt", "logo", "bitte", "passt", "richtig", "mach", "mache", "machs", "mach's", "schreib", "schreibe", "setz", "setze", "trag",
        "trage", "leg", "lege", "notier", "notiere", "pack", "packe", "nimm", "jaa", "jaaa", "jop", "sure", "yup", "freilich", "eh", "jawoll", "selbstverständlich",
    };

    private static readonly HashSet<string> YesFill = new(StringComparer.OrdinalIgnoreCase)
    {
        "dann", "doch", "das", "es", "sie", "ihn", "auf", "drauf", "ein", "an", "dazu", "rauf", "mal", "schön", "danke", "sehr", "na", "aber", "mir", "uns",
        "die", "der", "den", "liste", "einkaufsliste", "so", "du", "kannst", "schon", "gleich", "jetzt", "alles", "gute", "idee", "auch", "noch", "please",
        "bitteschön", "gerne", "gern", "ja", "machen", "eintragen", "aufschreiben", "draufsetzen", "notieren", "anlegen", "termin", "aufgabe", "the", "thanks",
    };

    private static readonly HashSet<string> NoCore = new(StringComparer.OrdinalIgnoreCase)
    {
        "nein", "nee", "ne", "nö", "noe", "no", "nope", "nicht", "nichts", "nix", "nö", "neee", "neeein", "nah", "keine", "kein", "egal", "abbrechen", "stopp",
        "stop", "später", "spaeter", "lass", "vergiss", "wars", "war's", "alles", "nada",
    };

    private static readonly HashSet<string> NoFill = new(StringComparer.OrdinalIgnoreCase)
    {
        "danke", "lieber", "besser", "eher", "erstmal", "erst", "mal", "jetzt", "gerade", "mehr", "das", "ist", "sonst", "brauche", "brauch", "ich", "es",
        "so", "nötig", "noetig", "dann", "also", "ach", "schon", "gut", "passt", "war", "doch", "thanks", "thank", "you", "bitte", "nötig", "einmal",
        "vielen", "dank", "momentan", "heute", "lass", "mal", "sein", "gerne", "gern", "oh", "ähm", "hm",
    };

    private static List<string> Tokens(string t) =>
        Regex.Matches(t.ToLowerInvariant(), @"[\p{L}\p{N}']+").Select(m => m.Value).ToList();

    /// <summary>Ist das ein Ja („ja“, „ja bitte, schreib sie auf“, „gerne“, „ok“)?</summary>
    private static bool IsYes(string t)
    {
        var s = Regex.Replace(t.ToLowerInvariant(), @"\b(auf jeden fall|na klar|aber sicher|gute idee|sehr gerne?|das wäre (toll|super|nett|gut|lieb)|warum nicht|why not|mach das|mach mal|tu das|bitte ja|ja bitte)\b", "ja");
        var w = Tokens(s);
        return w.Count > 0 && w.Count <= 8 && w.Any(YesCore.Contains) && w.All(x => YesCore.Contains(x) || YesFill.Contains(x));
    }

    /// <summary>Ist das ein Nein („nein“, „nein danke“, „lieber nicht“, „das war's“, „vergiss es“)?</summary>
    private static bool IsNo(string t)
    {
        var w = Tokens(t);
        return w.Count > 0 && w.Count <= 6 && w.Any(NoCore.Contains) && w.All(x => NoCore.Contains(x) || NoFill.Contains(x)) && !w.Contains("doch") || Regex.IsMatch(t.Trim(), @"^(nein|nee|nö|no)\b[,!. ]*(danke|lieber nicht|das war's|das wars)?[.!]*$", RegexOptions.IgnoreCase);
    }

    /// <summary>Ein einfaches Nein - ohne offene Frage nimmt es zurück, was Pukki gerade angelegt hat.</summary>
    private static readonly Regex NoRx = new(@"^(nein|nee|ne|nö|noe|no|nope)([\s,.!]+(nein|nee|nö))*[.!]*$", RegexOptions.IgnoreCase);

    /// <summary>Ausdrücklich zurücknehmen.</summary>
    private static readonly Regex UndoRx = new(
        @"^(falsch|rückgängig|rueckgaengig|undo|storno|stornieren|mach (das|es) (bitte )?rückgängig|(bitte )?lösch(e)? (das|es|den|die|ihn|sie)( (bitte )?wieder)?( bitte)?|(bitte )?entfern(e)? (das|es)( wieder)?|nimm (das|es) (wieder )?zurück|weg damit|doch nicht|ach doch nicht|nein,? doch nicht|(das )?war falsch|das ist falsch|stimmt nicht|das stimmt nicht|vergiss das|das war ein versehen|aus versehen|versehentlich|war nur ein test|oops|ups)[.!]*$",
        RegexOptions.IgnoreCase);

    private static ChatAction Say(int i, string label, string text) => new("say" + i, label, Pack(new ActData(Kind: "say", Text: text)));

    private static void SetAsk(Memory mem, string asked, string? pending, string? suggest)
    {
        mem.Asked = asked; mem.Pending = pending; mem.Suggest = suggest; mem.PendingAt = DateTime.UtcNow;
        mem.Items = null; mem.ListOwner = null; mem.ListId = 0;
    }

    private static void ClearAsk(Memory mem)
    {
        mem.Asked = mem.Pending = mem.Suggest = null; mem.Items = null; mem.ListOwner = null; mem.ListId = 0;
    }

    /// <summary>Nach jeder Antwort: hat Pukki etwas angelegt (Knopf „Rückgängig“)? Dann ist das das Letzte - für „nein“ und Korrekturen.</summary>
    private static void Note(Memory mem, Reply reply)
    {
        var undo = reply.Actions?.FirstOrDefault(a => a.Key == "undo");
        if (undo == null) { mem.JustMade = false; return; }
        var d = Unpack(undo);
        mem.JustMade = true;
        mem.LastMade = d.Made; mem.LastKind = d.Kind; mem.LastSource = d.Text;
        if (d.Kind is "shop" or "meal") mem.Topic = d.Kind;
    }

    // ---------------- Satz vorbereiten ----------------

    /// <summary>Leerzeichen, Anführungen und Anrede („…, Pukki?“) glätten; „Kannst du mir sagen, wann der Zahnarzt ist?“ → „wann ist der Zahnarzt?“.</summary>
    private static string Normalize(string text)
    {
        var t = text.Replace(' ', ' ').Replace("​", "").Replace('’', '\'').Replace('`', '\'').Replace('´', '\'').Trim();
        t = Regex.Replace(t, @"[ \t]+", " ");
        t = Regex.Replace(t, @"^@?pukki\b[,:!.]?\s*", "", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"^(?:@?pukki\b[,:!.]?\s*)+", "", RegexOptions.IgnoreCase);
        // „Pukki?“ - nur angesprochen
        if (t.Length > 0 && !t.Any(char.IsLetterOrDigit) && Regex.IsMatch(text, @"pukki", RegexOptions.IgnoreCase)) return "";
        // „bitte“ und Höflichkeiten vorn ändern nichts am Auftrag: „Wärst du so lieb und trägst … ein“ → „trag … ein“
        t = Regex.Replace(t, @"^(?:bitte|wärst du so (?:lieb|nett|gut)|waerst du so (?:lieb|nett|gut)|sei so (?:lieb|nett|gut)|sei doch so (?:lieb|nett|gut))\s*,?\s*(?:und\s+)?(?=\S)", "", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"^(trägst|traegst)\s+(?!du\b)", "trag ", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"^(schreibst|setzt|packst)\s+(?!du\b)", m => m.Groups[1].Value.ToLowerInvariant() switch { "schreibst" => "schreib ", "setzt" => "setz ", _ => "pack " }, RegexOptions.IgnoreCase);
        // Mundart: „is Butter auf da Listn?“, „brauch ma no a Milch?“
        t = Regex.Replace(t, @"\bauf da\b", "auf der", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\blistn\b", "Liste", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"^is(?=\s+\S+\s+(?:auf|in|noch|da|schon)\b)", "ist", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\b(brauch|hab|ham|hamma|gemma|samma)\s+ma\b", m => m.Groups[1].Value.ToLowerInvariant() switch { "brauch" => "brauchen wir", "hab" or "ham" => "haben wir", _ => m.Value }, RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\b(?:no\s+a|noch\s+a)\b", "noch", RegexOptions.IgnoreCase);
        // „ob Milch noch da ist“ allein ist eine Frage
        if (Regex.Match(t, @"^ob\s+(.+?)\s+(\p{L}+)[\s?!.]*$", RegexOptions.IgnoreCase) is { Success: true } bare) t = bare.Groups[2].Value + " " + bare.Groups[1].Value + "?";
        // „…, Pukki?“ - aber „braver Pukki“ bleibt ganz
        t = Regex.Replace(t, @"(?:\s*,\s*|(?<=\S+\s+\S+)\s+)(lieber |mein |liebe[rs]? )?pukki\b\s*(?=[?!.]*$)", "", RegexOptions.IgnoreCase);
        var m = Regex.Match(t, @"^(?:(?:kannst|könntest|koenntest|kannste) du mir (?:bitte |mal |eben |kurz )*sagen|weißt du(?: eigentlich)?|weisst du(?: eigentlich)?|sag mir(?: mal| bitte)?|sag mal|ich möchte wissen|ich will wissen|ich wüsste gern|ich wuesste gern|verrat mir|erzähl mir)[,:]?\s+(?<q>(?:ob|was|wann|wo|wie|wer|welche\w*|warum|wieso|wieviel|wie viel)\b.+)$", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var q = m.Groups["q"].Value.Trim().TrimEnd('?', '.', '!').Trim();
            // Nebensatz zur Frage: „ob Milch auf der Liste steht“ → „steht Milch auf der Liste“, „wann der Zahnarzt ist“ → „wann ist der Zahnarzt“
            var ob = Regex.Match(q, @"^ob\s+(.+)\s+(\p{L}+)$", RegexOptions.IgnoreCase);
            if (ob.Success) q = ob.Groups[2].Value + " " + ob.Groups[1].Value;
            else
            {
                var w = Regex.Match(q, @"^(wann|wo|was|wer|wie|welche\w*|warum|wieso|wieviel|wie viel)\s+((?:der|die|das|den|dem|mein|meine|meinem|meinen|unser|unsere|unseren)\b.*?)\s+(ist|sind|war|waren|steht|stehen|kommt|kommen|hat|habe|haben|gibt|muss|soll)$", RegexOptions.IgnoreCase);
                if (w.Success) q = w.Groups[1].Value + " " + w.Groups[3].Value + " " + w.Groups[2].Value;
            }
            t = q + "?";
        }
        return t;
    }

    private sealed record LeadPart(string Prefix, string Rest, string Answer);

    private static readonly Regex GreetLead = new(@"^(?<g>hallo|hallöchen|hallihallo|hi|hey|huhu|moin moin|moinsen|moin|servus|servas|grüß dich|grüss dich|gruess dich|grüß gott|grüss gott|griaß di|grias di|grüezi|guten morgen|guten tag|guten abend|gutn morgen|tach|tag|hello|good morning|good evening|yo|na du|na)(?:\s+(?:du|pukki|lieber pukki|mein freund))?\b[\s,.!:;]*(?<rest>.*)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex OtherLead = new(@"^(?<g>danke(?: dir| schön| sehr| vielmals)?|dankeschön|vielen dank|merci|ok(?:ay)?|okey|alles klar|gut|super|prima|toll|perfekt|achso|ach so|aha|also|ähm|äh|hm+|sag mal|du|übrigens|ach ja|noch was|und noch was|ach|oh|so)\s*[,.!:;]+\s*(?<rest>.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);

    /// <summary>Begrüßung, Dank oder Füllwort vorweg - der Rest wird beantwortet, die Begrüßung kommt in die Antwort.</summary>
    private static LeadPart? Lead(string t)
    {
        var g = GreetLead.Match(t);
        // „hallo???“, „hallo hallo“ sind nur eine Begrüßung (bzw. „bist du da?“)
        if (g.Success && (Regex.IsMatch(g.Groups["rest"].Value, @"^[\s?!.]*$") || GreetOnly.IsMatch(t))) return null;
        if (g.Success &&!(g.Groups["g"].Value.Equals("tag", StringComparison.OrdinalIgnoreCase) && g.Groups["rest"].Value.Length > 0)
            && !(g.Groups["g"].Value.Equals("na", StringComparison.OrdinalIgnoreCase) && g.Groups["rest"].Value.Length > 0 && !t.Contains(',')))
        {
            var rest = g.Groups["rest"].Value.Trim();
            var greet = g.Groups["g"].Value.ToLowerInvariant();
            // Die Begrüßung des Benutzers erwidern („Servus“ → „Servus!“), sonst schlicht „Hallo!“ (12 Uhr: keine Tageszeit)
            var prefix = Greet(greet, DateTime.Today.AddHours(12)) + "! ";
            return new LeadPart(prefix, rest, prefix.Trim());
        }
        var o = OtherLead.Match(t);
        if (o.Success)
        {
            var lead = o.Groups["g"].Value.ToLowerInvariant();
            var prefix = lead.StartsWith("dank") || lead.StartsWith("vielen") || lead == "merci" ? "Gern! " : "";
            return new LeadPart(prefix, o.Groups["rest"].Value.Trim(), prefix.Length > 0 ? "Gern geschehen!" : "👍");
        }
        return null;
    }

    // ---------------- Offene Rückfrage ----------------

    /// <summary>Welche Art nennt die Antwort? („als Termin“, „aufgabe“, „auf die Einkaufsliste“, „in den Essensplan“)</summary>
    private static string? KindOf(string t)
    {
        var s = Regex.Replace(t.ToLowerInvariant().Trim().TrimEnd('.', '!'), @"^(nein|ne|nee|doch|lieber|besser|bitte|eher|ja)[,\s]+", "");
        s = Regex.Replace(s, @"^(als|in den|in die|auf die|auf meine|in meine|in meinen|zur|zum)\s+(eine?n?\s+)?", "");
        return s switch
        {
            "termin" or "event" or "kalender" or "in den kalender" or "eintrag im kalender" => "event",
            "aufgabe" or "task" or "todo" or "to-do" or "aufgabenliste" or "erledigung" or "ohne termin" or "ohne datum" or "einfach so" or "nur notieren" => "task",
            "notiz" or "note" or "notizen" => "note",
            "einkauf" or "einkaufsliste" or "liste" or "shopping" or "einkaufszettel" => "shop",
            "essen" or "essensplan" or "mahlzeit" => "meal",
            _ => null,
        };
    }

    /// <summary>Nur ein Wann ohne Was („morgen 15 Uhr“, „am Freitag“)?</summary>
    private static bool OnlyWhen(Ctx c, string t)
    {
        var w = PukkiParse.Parse(t, c.Now);
        return (w.Date != null || w.Start != null) && PukkiWords.Words(w.Title).All(x => x is "termin" or "dann" or "lieber" or "ist" or "wäre" or "erst" or "so" or "ja" or "okay" or "ok");
    }

    /// <summary>Die Liste der offenen Rückfrage (falls sie noch da ist).</summary>
    private static Target? ListTarget(Ctx c, Memory mem)
    {
        if (mem.ListOwner == null || mem.ListId == 0) return null;
        var owner = c.Services.GetRequiredService<Spaces>().Get(mem.ListOwner);
        return PimStore.Find(owner.Db, mem.ListId) is { } coll ? new Target(owner, coll, owner.User.Id != c.Space.User.Id) : null;
    }

    private static string AddedLead(string names, string list) => $"Okay, ich habe {names} auf die Einkaufsliste „{list}“ gesetzt.";

    /// <summary>Antwort auf Pukkis offene Rückfrage - null: die Nachricht ist keine Antwort darauf (die Frage ist dann überholt).</summary>
    private static Reply? OnPending(Ctx c, string t)
    {
        var mem = c.Mem;
        var yes = IsYes(t);
        var no = !yes && IsNo(t);
        switch (mem.Asked)
        {
            // Nach 👎: „Was haettest du erwartet?“ - alles, was jetzt kommt, ist die Erklaerung
            case "explain":
                return Explained(c, t);
            case "kind":
            {
                var pending = mem.Pending ?? "";
                var kind = yes ? mem.Suggest : KindOf(t);
                if (kind != null) { ClearAsk(mem); return Do(c.Services, c.Space, mem, kind, pending, c.Now); }
                if (no) { ClearAsk(mem); return "Gut, dann lasse ich das."; }
                if (yes) return "Als was soll ich es anlegen – als Termin, als Aufgabe oder als Notiz?";
                if (OnlyWhen(c, t)) { ClearAsk(mem); return Do(c.Services, c.Space, mem, "event", t + " " + pending, c.Now); }
                return null;
            }
            case "when":
            {
                var pending = mem.Pending ?? "";
                var remind = mem.Suggest == "remind";
                if (no) { ClearAsk(mem); return "Gut, dann lasse ich das."; }
                var kind = KindOf(t);
                if (kind is "task" or "note" or "shop") { ClearAsk(mem); return Do(c.Services, c.Space, mem, kind, pending, c.Now); }
                if (OnlyWhen(c, t))
                {
                    ClearAsk(mem);
                    return remind ? RemindTask(c, t + " " + pending) ?? "Wann denn? Schreib mir zum Beispiel „morgen 15 Uhr“." : Do(c.Services, c.Space, mem, "event", t + " " + pending, c.Now);
                }
                if (yes) { mem.PendingAt = DateTime.UtcNow; return "Wann denn? Schreib mir zum Beispiel „morgen 15 Uhr“."; }
                return null;
            }
            case "what":
            {
                var pending = mem.Pending ?? "";
                var remind = mem.Suggest == "remind";
                if (no) { ClearAsk(mem); return "Gut, dann lasse ich das."; }
                if (IsQuestion(t) || yes || Talk(c, t) != null) return null;
                var w = PukkiParse.Parse(t, c.Now);
                if (w.Date != null || w.Start != null || w.Title.Length == 0 || t.Length > 80) return null;
                ClearAsk(mem);
                return NewEvent(c.Services, c.Space, mem, pending + " " + t, c.Now, null, reminder: remind ? 0 : null);
            }
            case "shop-add":
            {
                var pending = mem.Pending ?? "";
                var to = ListTarget(c, mem);
                if (yes)
                {
                    ClearAsk(mem);
                    return AddShop(c.Services, c.Space, ShopItems(c.Space, pending, force: true), pending, to, mem, AddedLead, more: true);
                }
                if (no) { ClearAsk(mem); return "Okay."; }
                return null;
            }
            case "shop-more" or "shop-what":
            {
                var to = ListTarget(c, mem);
                if (no) { ClearAsk(mem); return "Okay."; }
                if (yes && Tokens(t).Count <= 3) { var owner = mem.ListOwner; var id = mem.ListId; SetAsk(mem, "shop-what", null, null); mem.ListOwner = owner; mem.ListId = id; return "Was denn? Schreib mir einfach, was auf die Liste soll."; }
                if (IsQuestion(t) || Talk(c, t) != null) return null;
                var w = PukkiParse.Parse(t, c.Now);
                if (w.Date != null || w.Start != null) return null;
                var items = ShopItems(c.Space, Regex.Replace(t, @"^(ja|ok|okay|gerne?)[,\s]+", "", RegexOptions.IgnoreCase), force: true);
                if (items.Count == 0 || items.Count > 15) return null;
                ClearAsk(mem);
                return AddShop(c.Services, c.Space, items, t, to, mem);
            }
            case "shop-typo":
            {
                var near = mem.Suggest ?? "";
                var orig = mem.Items?.FirstOrDefault() ?? "";
                var line = mem.Pending ?? orig;
                var to = ListTarget(c, mem);
                var lower = t.ToLowerInvariant().Trim().TrimEnd('.', '!');
                var takeNear = yes || lower == near.ToLowerInvariant() || Regex.IsMatch(lower, @"^(ja|jo|genau)\b.*" + Regex.Escape(near.ToLowerInvariant()));
                var takeOrig = !takeNear && (no || lower == orig.ToLowerInvariant() || Regex.IsMatch(lower, @"^(nein|nee|ne)\b.*" + Regex.Escape(orig.ToLowerInvariant())));
                if (!takeNear && !takeOrig) return null;
                ClearAsk(mem);
                var text = !takeNear ? line : orig.Length == 0 ? near : Regex.Replace(line, Regex.Escape(orig), near, RegexOptions.IgnoreCase);
                return AddShop(c.Services, c.Space, ShopItems(c.Space, text, force: true), text, to, null,
                    takeNear ? AddedLead : (n, l) => $"Okay, dann setze ich {(n.StartsWith('„') ? n : "„" + n + "“")} auf die Einkaufsliste „{l}“.");
            }
            case "ingredients":
            {
                var items = mem.Items ?? [];
                if (yes)
                {
                    ClearAsk(mem);
                    var shop = items.SelectMany(i => ShopItems(c.Space, i, force: true)).ToList();
                    return AddShop(c.Services, c.Space, shop, string.Join(", ", items), null, null, AddedLead);
                }
                if (no) { ClearAsk(mem); return "Okay."; }
                return null;
            }
            case "reminder":
            {
                if (no) { ClearAsk(mem); return "Okay."; }
                if (ReminderMinutes(t) is { } minutes) { ClearAsk(mem); return RemindEvent(c, minutes); }
                if (yes)
                {
                    mem.PendingAt = DateTime.UtcNow;
                    return new Reply("Wie lange vorher soll ich dich erinnern? Zum Beispiel „1 Stunde vorher“ oder „1 Tag vorher“.",
                        Actions: [Say(0, "1 Stunde vorher", "1 Stunde vorher"), Say(1, "1 Tag vorher", "1 Tag vorher"), Say(2, "Nein", "nein")]);
                }
                return null;
            }
            case "mine":
            {
                var lower = t.ToLowerInvariant();
                var m = Regex.Match(lower, @"^\s*(?:(?:nein|ne|nee|nö)[,.!\s]+)?(?:das\s+)?(?:ist|war|wäre)?\s*(?:nicht\s+meiner|nicht\s+mein(?:\s+termin)?|nicht\s+von\s+mir|gehört\s+(?:mir\s+)?nicht(?:\s+mir)?|von\s+jemand(?:em)?\s+anderem|jemand\s+anderes?|der\s+von\s+jemand\s+anderem|nicht\s+für\s+mich)[.!]*\s*(?<rest>.*)$");
                if (m.Success || no)
                {
                    ClearAsk(mem);
                    var next = EventInfo(c, mem.Subject ?? "", skipCurrent: true);
                    return next with { Text = "Dann suche ich weiter … " + next.Text };
                }
                var y = Regex.Match(lower, @"^\s*(?:(?:ja|jo|genau)[,.!\s]+)?(?:das\s+)?(?:ist|war)?\s*(?:meiner|mein\s+termin|meins|von\s+mir|für\s+mich|gehört\s+mir|ja)\b[.!,]*\s*(?<rest>.*)$", RegexOptions.Singleline);
                if (y.Success || yes)
                {
                    ClearAsk(mem);
                    var rest = y.Success ? t[(t.Length - y.Groups["rest"].Value.Length)..].Trim() : "";
                    if (rest.Length > 0) return Respond(c, rest);
                    return OfferReminder(c, "Alles klar.") ?? "Alles klar.";
                }
                return null;
            }
        }
        return null;
    }

    // ---------------- Korrekturen ----------------

    private static readonly Regex FixMarker = new(@"^(?:nein|ne|nee|nö|doch|lieber|besser|eher|eigentlich|sorry|ups|oops|oh|ach|halt|stopp|moment|korrektur|falsch)\b|\b(?:draus|daraus|verschieb\w*|verleg\w*|änder\w*|aender\w*|stattdessen|umstellen|stell\w* (?:es|das|ihn) (?:auf|um)|statt|sondern)\b", RegexOptions.IgnoreCase);
    private static readonly Regex KindFix = new(@"^(?:(?:nein|ne|nee|doch|lieber|besser|eher|sorry|ach)[,.!\s]+)*(?:(?:lieber|besser|bitte|doch|eher)\s+)*(?:(?:mach|leg|trag)\w*\s+(?:es|das|ihn)\s+)?(?:als|in die|in den|auf die)\s+(termin|aufgabe|notiz|einkaufsliste|liste|essensplan|kalender)\b(?:\s+(?:an|ein|drauf))?[.!]*$|^(?:das\s+)?(?:war|ist|sollte)\s+(?:eine?n?\s+)?(termin|aufgabe|notiz)\s*(?:sein)?[.!]*$", RegexOptions.IgnoreCase);

    /// <summary>
    /// Korrektur am gerade Angelegten: „Mach 16 Uhr draus“, „Nein, Donnerstag“, „verschieb das auf 11 Uhr“, „nein, als Aufgabe“.
    /// Ohne Angelegtes ehrlich: Pukki ändert nur, was er gerade selbst eingetragen hat.
    /// </summary>
    private static Reply? Correct(Ctx c, string t)
    {
        var mem = c.Mem;
        var kindFix = KindFix.Match(t);
        var marker = FixMarker.IsMatch(t);
        if (!kindFix.Success && !marker) return null;
        var last = mem.LastMade?.FirstOrDefault();
        var lastKind = mem.LastKind;
        var source = mem.LastSource ?? "";

        if (kindFix.Success)
        {
            if (last == null || lastKind is not ("event" or "task" or "note" or "shop")) return "Ich weiß gerade nicht, welchen Eintrag du meinst – umstellen kann ich nur, was ich gerade eben angelegt habe.";
            var word = (kindFix.Groups[1].Success ? kindFix.Groups[1].Value : kindFix.Groups[2].Value).ToLowerInvariant();
            var kind = KindOf(word) ?? "task";
            if (kind == lastKind) return $"Das ist schon {KindName(kind)}.";
            UndoItems(c.Services, mem.LastMade);
            mem.LastMade = null;
            return Do(c.Services, c.Space, mem, kind, source, c.Now);
        }

        // Neues Wann: hinter „sondern“, sonst der Satz ohne die Korrekturwörter
        var s = t;
        var sondern = Regex.Match(s, @"\bsondern\b(.*)$", RegexOptions.IgnoreCase);
        if (sondern.Success) s = sondern.Groups[1].Value;
        s = Regex.Replace(s, @"\bstatt\s+(?:um\s+)?\d{1,2}(?:[:.]\d{2})?(?:\s*uhr)?|\bstatt\s+\p{L}+", " ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"^(?:(?:nein|ne|nee|nö|doch|lieber|besser|eher|eigentlich|sorry|ups|oops|oh|ach|halt|stopp|moment|korrektur|falsch|bitte|dann|also|erst)[,.!:\s]+)+", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\b(?:mach|mache|machs|mach's|setz|setze|stell|stelle)\s+(?:es|das|ihn|den termin|die aufgabe)?\s*(?:auf|zu|um)?\b|\b(?:draus|daraus|stattdessen|bitte|lieber|doch|erst|besser)\b|\b(?:verschieb|verschiebe|verleg|verlege|änder|ändere|aender|aendere)\w*\s+(?:es|das|ihn|den termin|die aufgabe|die uhrzeit|die zeit|das datum|den tag|den beginn)?\s*(?:auf|nach|zu|in)?\b", " ", RegexOptions.IgnoreCase);
        var w = PukkiParse.Parse(s, c.Now);
        var rest = PukkiWords.Words(w.Title).Where(x => x is not ("termin" or "uhrzeit" or "zeit" or "datum" or "tag" or "es" or "das" or "ihn" or "sein" or "soll" or "ist" or "wäre" or "war" or "schon" or "um" or "ab")).ToList();
        var changesWhen = w.Date != null || w.Start != null;
        // „Nein, Donnerstag“ ohne etwas Angelegtes: ehrlich statt Unsinn
        if (last == null || lastKind is not ("event" or "task"))
        {
            if (changesWhen || Regex.IsMatch(t, @"\b(draus|daraus|verschieb\w*|verleg\w*|änder\w*|aender\w*)\b", RegexOptions.IgnoreCase))
                return "Ich weiß gerade nicht, welchen Eintrag du meinst. Ändern kann ich nur, was ich gerade eben eingetragen habe – alles andere bitte direkt im Kalender.";
            return null;
        }
        if (!changesWhen)
        {
            if (rest.Count > 0 && Regex.IsMatch(t, @"\b(änder\w*|aender\w*|nenn\w*|heißen|heissen|titel|name|ort)\b", RegexOptions.IgnoreCase))
                return "Den Namen oder Ort kann ich noch nicht ändern. Schreib „nein“, dann nehme ich den Eintrag zurück, und schick ihn mir noch einmal richtig.";
            if (Regex.IsMatch(t, @"\b(draus|daraus|verschieb\w*|verleg\w*|änder\w*|aender\w*)\b", RegexOptions.IgnoreCase))
                return "Was soll ich ändern? Zum Beispiel „mach 16 Uhr draus“ oder „nein, Donnerstag“.";
            return null;
        }
        var old = PukkiParse.Parse(source, c.Now);
        // Neuer Inhalt: „nein, morgen 11 Uhr Zahnarzt“ ersetzt den Eintrag; ohne „nein“ vorn ist es ein neuer Satz
        var oldWords = PukkiWords.Words(old.Title);
        if (rest.Any(x => !oldWords.Contains(x)))
        {
            if (!Regex.IsMatch(t, @"^(nein|ne|nee|nö|falsch|sorry|korrektur|ups|oops)\b", RegexOptions.IgnoreCase)) return null;
            var undone = UndoItems(c.Services, mem.LastMade);
            mem.LastMade = null;
            var again = Understand(c.Services, c.Space, mem, s.Trim(), c.Now);
            return again with { Text = (undone.Count > 0 ? Undone(undone) + " " : "") + again.Text };
        }


        var day = w.Date ?? old.Date;
        var start = w.Start ?? old.Start;
        TimeOnly? end = w.End ?? (w.Start != null && old.Start != null && old.End != null ? w.Start.Value.Add(old.End.Value - old.Start.Value) : w.Start == null ? old.End : null);
        var merged = (day is { } d ? d.ToString("dd.MM.yyyy") + " " : "") + (start is { } st ? "um " + st.ToString("HH:mm") + " " : "")
            + (end is { } en && start != null ? "bis " + en.ToString("HH:mm") + " " : "") + old.Title;
        var owner = c.Services.GetRequiredService<Spaces>().Get(last.Owner);
        var coll = PimStore.Find(owner.Db, last.Collection) ?? throw new KeyNotFoundException("Den Kalender gibt es nicht mehr.");
        var target = new Target(owner, coll, owner.User.Id != c.Space.User.Id);
        return lastKind == "event"
            ? NewEvent(c.Services, c.Space, mem, merged, c.Now, target, uri: last.Uri)
            : NewTask(c.Services, c.Space, merged, c.Now, target, uri: last.Uri);
    }

    private static string KindName(string kind) => kind switch
    {
        "event" => "ein Termin", "task" => "eine Aufgabe", "note" => "eine Notiz", "shop" => "auf der Einkaufsliste", "meal" => "im Essensplan", _ => kind,
    };

    // ---------------- Bezüge auf das Gespräch ----------------

    private static readonly Regex AndRx = new(@"^(?:und|aber|und was ist|was ist mit|wie ist es mit|wie sieht'?s mit|wie sieht es mit|und wie sieht'?s|und wie sieht es|und wie ist es|und für|und wie|and|what about|und dann)\b[\s,]*(?<rest>.*?)[\s?!.]*$", RegexOptions.IgnoreCase);
    private static readonly Regex WhyRx = new(@"^(?:und\s+)?(?:warum|wieso|weshalb|weswegen|wie kommt's|wie kommt das|why)(?:\s+(?:nicht|denn|denn nicht|das|das denn|nicht\?))?[\s?!.]*$", RegexOptions.IgnoreCase);
    private static readonly Regex MoreRx = new(@"^(?:noch\s+(?:einen|einer|ein|eins|mal|einmal)|noch ein witz|nochmal|noch mehr|mehr|weiter|another one|one more)\b[\s!?.]*$", RegexOptions.IgnoreCase);

    /// <summary>„Und morgen?“, „Und am Freitag?“, „Warum nicht?“, „Und Brot auch“, „Und der Friseur?“ - aus dem Gesprächszusammenhang.</summary>
    private static Reply? FollowUp(Ctx c, string t)
    {
        var mem = c.Mem;
        if (WhyRx.IsMatch(t))
            return mem.Why?.Invoke() ?? "Wie meinst du das? Frag mich gern noch einmal mit anderen Worten.";
        if (MoreRx.IsMatch(t) && mem.Topic == "joke") return Joke(mem);

        // „Dann schreib sie auf“, „setz sie drauf“ - der zuletzt besprochene Artikel
        if (mem.Topic == "shop" && mem.Subject is { } item && Regex.IsMatch(t, @"^(?:dann\s+|ok\s+|okay\s+|ja\s+)?(?:schreib|schreibe|setz|setze|pack|packe|notier|notiere|tu|trag)\s+(?:sie|es|ihn|das|die|den)\s+(?:auf|drauf|dazu|rauf|mit auf|auf die liste|ein)[\s!.]*$", RegexOptions.IgnoreCase))
            return AddShop(c.Services, c.Space, ShopItems(c.Space, item, force: true), item, null, mem, AddedLead);

        var and = AndRx.Match(t);
        var rest = and.Success ? and.Groups["rest"].Value.Trim() : null;
        // „Und Brot auch“, „und noch Käse“, „Käse auch noch“ nach einem Einkauf
        if (mem.Topic == "shop")
        {
            var add = Regex.Match(t, @"^(?:und\s+)?(?:(?:schreib|schreibe|setz|setze|pack|packe|notier|notiere|kauf|kaufe|hol|hole|tu)\s+)?(?:noch\s+|auch\s+)*(?<items>.+?)\s+(?:auch|auch noch|noch|dazu|mit|mit drauf|mit auf)[\s!.]*$|^(?:und\s+)?noch\s+(?<items>.+?)[\s!.]*$", RegexOptions.IgnoreCase);
            if (add.Success && !IsQuestion(t))
            {
                var items = ShopItems(c.Space, add.Groups["items"].Value, force: true);
                if (items.Count > 0 && PukkiParse.Parse(add.Groups["items"].Value, c.Now) is { Date: null, Start: null })
                    return AddShop(c.Services, c.Space, items, add.Groups["items"].Value, null, mem);
            }
            if (rest is { Length: > 0 } && t.Contains('?') && PukkiParse.Parse(rest, c.Now) is { Date: null, Start: null })
                return ShopCheck(c, rest);
        }
        if (rest == null) return null;
        if (rest.Length == 0) return mem.Topic == "day" && mem.Day is { } dd ? DayOverview(c.Services, c.Space, mem, dd.AddDays(1), c.Today) : null;

        // „Und am Wochenende?“ - zwei Tage
        if (mem.Topic is "day" or null && Regex.IsMatch(rest, @"^(?:am\s+|übers\s+|das\s+)?(?:wochenende|weekend)$", RegexOptions.IgnoreCase) && WeekRange(c, rest) is { } weekend)
            return WeekOverview(c, weekend.From, weekend.Days, weekend.Label);
        // Ein anderer Tag zum selben Thema
        var w = PukkiParse.Parse(rest, c.Now);
        var leftover = PukkiWords.Words(w.Title).Where(x => x is not ("auch" or "dann" or "so" or "da" or "aus" or "an" or "los" or "zu" or "essen" or "es")).ToList();
        if (w.Date is { } day && leftover.Count == 0)
        {
            // „Und übermorgen auch“ nach einem neuen Termin: denselben Termin noch einmal
            if (mem.JustMade && mem.LastKind == "event" && Regex.IsMatch(rest, @"\bauch\b", RegexOptions.IgnoreCase))
            {
                var old = PukkiParse.Parse(mem.LastSource ?? "", c.Now);
                var again = day.ToString("dd.MM.yyyy") + " " + (old.Start is { } st ? "um " + st.ToString("HH:mm") + " " : "") + (old.End is { } en ? "bis " + en.ToString("HH:mm") + " " : "") + old.Title;
                return Do(c.Services, c.Space, mem, "event", again, c.Now);
            }
            return mem.Topic switch
            {
                "meal" => MealAnswer(c, "was gibt es " + rest + " zu essen?"),
                "tasks" => TasksAnswer(c, day),
                _ => DayOverview(c.Services, c.Space, mem, day, c.Today),
            };
        }
        // „Und morgen Lasagne“ nach einer Mahlzeit: noch eine Mahlzeit
        if (w.Date != null && leftover.Count > 0 && mem.LastKind == "meal" && mem.JustMade && !IsQuestion(t))
        {
            var meal = Regex.Match(mem.LastSource ?? "", @"\b(frühstück|fruehstueck|mittagessen|abendessen|abendbrot|mittags?|abends?)\b", RegexOptions.IgnoreCase);
            return Do(c.Services, c.Space, mem, "meal", rest + (meal.Success && !MealNoun.IsMatch(rest) && !MealTime.IsMatch(rest) ? " " + meal.Value : ""), c.Now);
        }
        var week = WeekRange(c, rest);
        if (week != null && leftover.All(x => x is "woche" or "wochenende" or "nächste" or "naechste" or "diese" or "kommende" or "tage" or "nächsten" or "naechsten"))
            return WeekOverview(c, week.Value.From, week.Value.Days, week.Value.Label);
        // „Und der Friseur?“ nach „Wann ist der Zahnarzt?“
        if (mem.Topic == "when" && rest.Length > 0 && w.Date == null && !QWord.IsMatch(rest)) return EventInfo(c, Regex.Replace(rest, @"^(der|die|das|den|dem|mein|meine|zum|zur)\s+", "", RegexOptions.IgnoreCase));
        if (mem.Topic == "contact" && rest.Length > 0) return ContactAnswer(c, Regex.Replace(rest, @"^(von|vom|von der|von dem|der|die|das)\s+", "", RegexOptions.IgnoreCase), null);
        return null;
    }

    // ---------------- Sätze in Befehlsform ----------------

    private static readonly Regex PoliteRx = new(@"^(?:(?:kannst|könntest|koenntest|würdest|wuerdest|magst|willst|kannste)\s+du|can you|could you|would you|please)\s+(?:mir\s+|uns\s+|bitte\s+|mal\s+|noch\s+|eben\s+|schnell\s+|kurz\s+)*(?<body>.+?)\s+(?<verb>eintragen|einschreiben|aufschreiben|notieren|aufnehmen|hinzufügen|hinzufuegen|anlegen|draufsetzen|draufschreiben|merken|vormerken|speichern|festhalten|setzen|schreiben|packen|tun|erinnern|add|note|remind me)\s*(?:bitte)?\s*[?!.]*$", RegexOptions.IgnoreCase);
    private static readonly Regex OnListRx = new(@"^(?:(?:bitte|dann|ok|okay|und|noch)\s+)*(?:(?:schreib|schreibe|setz|setze|pack|packe|tu|tue|notier|notiere|trag|trage|füg|füge|nimm)\s+(?:mir\s+|uns\s+|bitte\s+|mal\s+|noch\s+|auch\s+|dann\s+)*)?(?<body>.+?)\s+(?:mit\s+)?(?:auf|in|zu|zur)\s+(?:die|der|meine|meiner|unsere|unserer|den)?\s*(?:einkaufsliste|liste|einkaufszettel|zettel|shoppingliste|einkaufsliste\s+\p{L}+)(?:\s+(?:drauf|dazu|hinzu|rauf|setzen|schreiben|packen|tun|bitte))*[\s!.]*$", RegexOptions.IgnoreCase);
    private static readonly Regex ListPrefixRx = new(@"^(?:auf die (?:einkaufs)?liste|für die (?:einkaufs)?liste|einkaufsliste|zur einkaufsliste|einkaufszettel|liste)\s*[:\-]\s*(?<body>.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex BuyRx = new(@"^(?:(?:bitte|dann|und|ok|okay)\s+)*(?:kauf|kaufe|hol|hole|besorg|besorge|bring|bringe|bestell|bestelle|buy|get)\s+(?:mir\s+|uns\s+|ma\s+|mia\s+|bitte\s+|mal\s+|noch\s+|no\s+|auch\s+|dann\s+|du\s+)*(?<body>.+?)(?:\s+mit)?(?:\s+bitte)?[\s!.]*$", RegexOptions.IgnoreCase);
    private static readonly Regex EmptyRx = new(@"^(?:(?:die|der|das|unser|unsere|mein|meine)\s+)?(?<body>.+?)\s+(?:ist|sind|wird|werden|geht|gehen)\s+(?:gleich\s+|fast\s+|bald\s+|schon\s+|jetzt\s+|leider\s+|wieder\s+)*(?:alle|leer|aus|aufgebraucht|ausgegangen|zu ende|knapp|zur neige)[\s!.]*$|^(?:wir haben|ich habe|ich hab|hab|haben|habe)\s+(?:keine?n?|kein)\s+(?<body>.+?)\s+mehr(?:\s+da|\s+im haus)?[\s!.]*$|^(?:kein|keine|keinen)\s+(?<body>.+?)\s+mehr(?:\s+da)?[\s!.]*$|^(?:uns|mir)\s+(?:fehlt|fehlen|geht|gehen|ist|sind)\s+(?:der|die|das)?\s*(?<body>.+?)(?:\s+(?:aus|ausgegangen))?[\s!.]*$|^(?:wir brauchen|ich brauche|brauchen|brauche|we need|i need|need)\s+(?!to\b)(?:noch\s+|dringend\s+|neue[ns]?\s+|mal\s+wieder\s+|wieder\s+)*(?<body>.+?)[\s!.]*$|^(?:es fehlt|es fehlen)\s+(?<body>.+?)[\s!.]*$", RegexOptions.IgnoreCase);
    private static readonly Regex NoteRx = new(@"^(?:notier(?:e)?(?:\s+(?:dir|mir))?|merk(?:e)?\s+dir|merk(?:e)?|halte?\s+fest|schreib(?:e)?\s+(?:dir\s+|mir\s+)?auf|notiz|note|gedanke|idee)\s*[:,]\s*(?<body>.+)$|^(?:notier(?:e)?(?:\s+(?:dir|mir))?|merk(?:e)?\s+dir|halt(?:e)?\s+fest)\s+(?<body>.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex TaskRx = new(@"^(?:ich muss|ich müsste|ich muesste|ich sollte|ich will|ich möchte|ich moechte|wir müssen|wir muessen|wir sollten|muss|müsste|sollte|nicht vergessen|denk dran|denk daran|denke daran|vergiss nicht|todo|to do|to-do|remind me to|i need to|i have to|i must)[:,]?\s+(?:noch\s+|unbedingt\s+|mal\s+|wieder\s+|endlich\s+|dringend\s+|bald\s+|dass\s+ich\s+|dass\s+wir\s+|,\s*)*(?<body>.+?)[\s!.]*$", RegexOptions.IgnoreCase);
    private static readonly Regex NewRx = new(@"^(?:neue[rs]?|new|weitere[rs]?|noch\s+(?:ein(?:e|en)?))\s+(?<kind>termin|aufgabe|notiz|eintrag|event|task|note|todo)\s*[:,\-]?\s*(?<body>.*)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex EnterRx = new(@"^(?:(?:bitte|dann)\s+)?(?:trag|trage)\s+(?:mir\s+|uns\s+|bitte\s+|mal\s+|noch\s+|auch\s+)*(?<body>.+?)\s+(?:in (?:den|meinen) kalender\s+)?ein[\s!.]*$|^(?:schreib|schreibe|setz|setze)\s+(?:in den kalender|in meinen kalender)\s*[:,]?\s*(?<body>.+)$|^(?:kalender|in den kalender)\s*[:\-]\s*(?<body>.+)$", RegexOptions.IgnoreCase);
    private static readonly Regex HaveRx = new(@"^(?:ich hab|ich habe|hab|habe|wir haben|ich bin|wir sind|ich gehe|wir gehen|ich geh|ich fahre|wir fahren|ich muss|wir müssen)\s+(?<body>.+?)[\s!.]*$", RegexOptions.IgnoreCase);
    private static readonly Regex RemindRx = new(@"^(?:(?:bitte|kannst du|könntest du|würdest du)\s+)?(?:erinner|erinnere|erinnre|remind)\s+(?:mich|uns|me)\s*,?\s*(?<body>.+?)(?:\s+erinnern)?[\s!.?]*$", RegexOptions.IgnoreCase);
    private static readonly Regex RefuseRx = new(@"^(?:(?:bitte|dann|und|kannst du|könntest du|kannst du bitte)\s+)*(?:(?<mail>schreib|schreibe|sende|send|schick|schicke|maile|mail|antworte|beantworte|leite)\b.*\b(?:mail|e-mail|email|nachricht|sms|whatsapp|brief|antwort|weiter)\b|(?<call>ruf|rufe|telefonier\w*)\b.*\b(?:an)?|(?<cancel>sag|sage)\b.*\bab\b|(?<cancel2>storniere?|kündige|kuendige)\b.+|(?<pay>überweise|ueberweise|bezahle?|zahle?)\b.+|(?<order>bestell(?:e)?\s+(?:mir|uns|bitte|einen|eine|ein|den|die|das|online)|buche\b|buch\s+(?:mir|uns|bitte|einen|eine|ein|den|die|das)\b).+|(?<delete>lösch\w*|loesch\w*|entfern\w*)\s+(?:den|die|das|meinen|meine|alle)\s+.+)", RegexOptions.IgnoreCase | RegexOptions.Singleline);

    /// <summary>
    /// Sätze, die etwas anlegen sollen, aber anders gebaut sind: höfliche Bitten, „Schreib Käse auf die Liste“, „Milch ist alle“,
    /// „Ich muss noch die Steuer machen“, „Erinnere mich morgen um 9 an den Müll“. Aufträge, die Pukki nicht kann, lehnt er ehrlich ab.
    /// </summary>
    private static Reply? Rewrite(Ctx c, string t)
    {
        var mem = c.Mem;
        // Erinnern: an einen bestehenden Termin („2 Tage vorher ans Konzert“) oder neu („morgen um 9 an den Müll“)
        var rm = RemindRx.Match(t);
        // „… in einer halben Stunde noch mal“: die letzte Erinnerung wiederholen statt eines neuen Termins „Noch mal“
        if (rm.Success) return SnoozeByText(c, rm.Groups["body"].Value.Trim()) ?? Remind(c, rm.Groups["body"].Value.Trim());

        // Höfliche Bitte: „Kannst du morgen 15 Uhr Zahnarzt eintragen?“ → „morgen 15 Uhr Zahnarzt“
        // „Kannst du dir merken, dass …?“ - eine Notiz
        var keep = Regex.Match(t, @"^(?:kannst|könntest|koenntest|würdest|wuerdest)\s+du\s+(?:dir|mir)\s+(?:bitte\s+|mal\s+)*(?:merken|notieren|aufschreiben)\s*,?\s*(?:dass\s+)?(?<body>.+?)[\s?!.]*$", RegexOptions.IgnoreCase);
        if (keep.Success)
        {
            // „…, dass ich die Steuer machen muss“ ist eine Aufgabe
            var must = Regex.Match(keep.Groups["body"].Value, @"^(?:ich|wir)\s+(?:noch\s+)?(?<what>.+?)\s+(?:muss|müssen|muessen|sollte|sollten|will|wollte|möchte)$", RegexOptions.IgnoreCase);
            return must.Success ? Do(c.Services, c.Space, mem, "task", must.Groups["what"].Value, c.Now) : Do(c.Services, c.Space, mem, "note", Cap(keep.Groups["body"].Value), c.Now);
        }
        // „Schreibst du Sahne auf die Liste?“, „Trägst du mir morgen 9 Uhr Friseur ein?“ - wie die Befehlsform
        var you = Regex.Match(t, @"^(?<verb>schreibst|setzt|packst|tust|trägst|traegst|notierst)\s+du\s+(?:mir\s+|uns\s+|bitte\s+|mal\s+|noch\s+)*(?<body>.+?)[\s?!.]*$", RegexOptions.IgnoreCase);
        if (you.Success)
        {
            var verb = you.Groups["verb"].Value.ToLowerInvariant() switch { "schreibst" => "schreib", "setzt" => "setz", "packst" => "pack", "tust" => "tu", "notierst" => "notier dir:", _ => "trag" };
            return Rewrite(c, verb + " " + you.Groups["body"].Value);
        }
        var pol = PoliteRx.Match(t);
        if (pol.Success)
        {
            var verb = pol.Groups["verb"].Value.ToLowerInvariant();
            var body = pol.Groups["body"].Value.Trim();
            if (verb is "erinnern" or "remind me") return Remind(c, body);
            var onList = Regex.Match(body, @"^(?<b>.+?)\s+(?:auf|in)\s+(?:die|meine|unsere)?\s*(?:einkaufs)?liste$", RegexOptions.IgnoreCase);
            if (onList.Success) return ShopFrom(c, onList.Groups["b"].Value, force: true);
            if (verb is "merken" or "festhalten" or "speichern") return Do(c.Services, c.Space, mem, "note", Regex.Replace(body, @"^(?:dass|,\s*dass)\s+", "", RegexOptions.IgnoreCase), c.Now);
            return Understand(c.Services, c.Space, mem, body, c.Now);
        }
        if (IsQuestion(t)) return null;

        // Abhaken: „Milch gekauft“, „Steuer machen ist erledigt“
        if (MarkDone(c, t) is { } done) return done;
        // Aufträge, die Pukki nicht kann
        if (Refuse(c, t) is { } refused) return refused;

        var nw = NewRx.Match(t);
        if (nw.Success)
        {
            var kind = nw.Groups["kind"].Value.ToLowerInvariant() switch { "termin" or "event" => "event", "notiz" or "note" => "note", "eintrag" => null, _ => "task" };
            var body = nw.Groups["body"].Value.Trim();
            return kind == null ? Understand(c.Services, c.Space, mem, body.Length > 0 ? body : t, c.Now) : Do(c.Services, c.Space, mem, kind, body, c.Now);
        }
        var lp = ListPrefixRx.Match(t);
        if (lp.Success) return ShopFrom(c, lp.Groups["body"].Value, force: true);
        var ol = OnListRx.Match(t);
        if (ol.Success && !Regex.IsMatch(ol.Groups["body"].Value, @"^(?:was|wer|wie|wann)\b", RegexOptions.IgnoreCase)) return ShopFrom(c, ol.Groups["body"].Value, force: true);
        var en = EnterRx.Match(t);
        if (en.Success)
        {
            var body = en.Groups["body"].Value.Trim();
            var w = PukkiParse.Parse(body, c.Now);
            return w.Date == null && w.Start == null ? AskWhen(mem, body, CleanTitle(w.Title)) : Do(c.Services, c.Space, mem, "event", body, c.Now);
        }
        var note = NoteRx.Match(t);
        if (note.Success)
        {
            var body = Regex.Replace(note.Groups["body"].Value.Trim(), @"^(?:dass|,\s*dass)\s+", "", RegexOptions.IgnoreCase);
            // „notier Milch“: lauter Einkaufswörter gehören auf die Liste
            if (ShopItems(c.Space, body) is { Count: > 0 } items && items.All(i => i.Near == null)) return AddShop(c.Services, c.Space, items, body, null, mem);
            return Do(c.Services, c.Space, mem, "note", Cap(body), c.Now);
        }
        // „Schreib noch Kekse auf“, „setz Käse mit drauf“ - Einkauf, wenn es danach aussieht
        var up = Regex.Match(t, @"^(?:(?:und|dann|ok|okay)\s+)*(?:schreib|schreibe|setz|setze|notier|notiere|pack|packe)\s+(?:mir\s+|uns\s+|bitte\s+|noch\s+|mal\s+|auch\s+)*(?<body>.+?)\s+(?:auf|mit auf|dazu|drauf|mit drauf|mit)[\s!.]*$", RegexOptions.IgnoreCase);
        if (up.Success && ShopItems(c.Space, up.Groups["body"].Value) is { Count: > 0 } upItems) return AddShop(c.Services, c.Space, upItems, up.Groups["body"].Value, null, mem);
        var buy = BuyRx.Match(t);
        if (buy.Success)
        {
            var body = buy.Groups["body"].Value.Trim();
            if (ShopItems(c.Space, body) is { Count: > 0 } items) return AddShop(c.Services, c.Space, items, body, null, mem);
            if (PukkiParse.Parse(body, c.Now) is { Date: null, Start: null } && PukkiWords.Words(body).Count <= 3)
                return ShopFrom(c, body, force: false);
        }
        var empty = EmptyRx.Match(t);
        if (empty.Success && PukkiParse.Parse(empty.Groups["body"].Value, c.Now) is { Date: null, Start: null })
        {
            var body = empty.Groups["body"].Value.Trim();
            if (ShopFrom(c, body, force: false) is { } shop) return shop;
        }
        var task = TaskRx.Match(t);
        if (task.Success)
        {
            var body = task.Groups["body"].Value.Trim();
            if (Regex.IsMatch(body, @"^(los|weg|schlafen|ins bett|arbeiten|aufs klo|aufs klo gehen|jetzt los)$", RegexOptions.IgnoreCase)) return null;
            var w = PukkiParse.Parse(body, c.Now);
            var evt = PukkiWords.Words(w.Title).Any(x => PukkiWords.In(x, PukkiWords.EventWords));
            if (w.Start != null && evt) return Do(c.Services, c.Space, mem, "event", body, c.Now);
            if (ShopItems(c.Space, body) is { Count: > 0 } items && w.Date == null) return AddShop(c.Services, c.Space, items, body, null, mem);
            return Do(c.Services, c.Space, mem, "task", body, c.Now);
        }
        var have = HaveRx.Match(t);
        if (have.Success)
        {
            var body = have.Groups["body"].Value.Trim();
            var w = PukkiParse.Parse(body, c.Now);
            if (w.Date != null || w.Start != null)
            {
                var title = Regex.Replace(w.Title, @"^(?:noch\s+|einen\s+termin\s+(?:beim|bei der|bei|zum)?\s*|termin\s+(?:beim|bei der|bei|zum)?\s*|(?:zum|zur|ins|in die|beim|bei der|beim|nach)\s+)", "", RegexOptions.IgnoreCase);
                if (title.Length > 0) return Do(c.Services, c.Space, mem, "event", w.Title.Length > 0 ? body.Replace(w.Title, title) : body, c.Now);
            }
        }
        return null;
    }

    /// <summary>Einkauf aus einem Satzteil - force: auch Unbekanntes (ausdrücklich „auf die Liste“), sonst nur, was nach Einkauf aussieht; Unbekanntes wird erfragt.</summary>
    private static Reply? ShopFrom(Ctx c, string body, bool force)
    {
        var items = ShopItems(c.Space, body, force);
        if (items.Count > 0) return AddShop(c.Services, c.Space, items, body, null, c.Mem);
        if (force) return "Was soll auf die Einkaufsliste? Zum Beispiel: Milch, 6 Eier, Brot";
        var words = PukkiWords.Words(body);
        if (words.Count == 0 || words.Count > 3 || words.Any(PukkiWords.NotItems.Contains) || words.Any(x => PukkiWords.In(x, PukkiWords.EventWords))) return null;
        var name = Cap(body.Trim().TrimEnd('.', '!'));
        SetAsk(c.Mem, "shop-add", name, null);
        return new Reply($"„{name}“ kenne ich noch nicht als Einkauf. Soll ich es auf die Einkaufsliste setzen?", Actions: [Say(0, "Ja", "ja"), Say(1, "Nein", "nein")]);
    }

    /// <summary>„Erinnere mich …“: an einen bestehenden Termin („2 Tage vorher ans Konzert“) oder als neuer Termin mit Erinnerung.</summary>
    private static Reply Remind(Ctx c, string body)
    {
        body = Regex.Replace(body, @"^(?:(?:bitte|mich|uns|me|doch|mal)\s+)+", "", RegexOptions.IgnoreCase);
        // „2 Tage vorher ans Konzert“, „am Vortag an den Zahnarzt“
        if (Regex.IsMatch(body, @"\b(vorher|davor|vortag|im voraus|before)\b", RegexOptions.IgnoreCase) && ReminderMinutes(body) is { } minutes)
        {
            var what = Regex.Replace(body, @"(?:\d+|ein|eine|einen|einer|zwei|drei|vier|fünf|sechs|sieben|zehn|zwölf|halbe?n?)?\s*(?:minuten?|min|stunden?|std|tage?n?|wochen?)?\s*(?:vorher|davor|im voraus|before)|am vortag|einen tag vorher", " ", RegexOptions.IgnoreCase);
            what = Regex.Replace(what, @"^\s*(?:an|ans|am|an den|an die|an das|an meinen|an meine|zum|zur|für|fuer|daran)\s+", "", RegexOptions.IgnoreCase).Trim().Trim(',', '.');
            what = Regex.Replace(what, @"^(?:den|die|das|dem|der|meinen|meine|mein)\s+", "", RegexOptions.IgnoreCase).Trim();
            if (what.Length == 0)
            {
                if (c.Mem.Event == null) return "An welchen Termin soll ich dich erinnern? Zum Beispiel: Erinnere mich 2 Tage vorher ans Konzert.";
                return RemindEvent(c, minutes);
            }
            var hit = FindEvents(c, what).FirstOrDefault(h => h.Local >= c.Now);
            if (hit == null) return $"Zu „{Cap(what)}“ finde ich keinen kommenden Termin.";
            Focus(c, hit);
            return RemindEvent(c, minutes);
        }
        // Neu: „morgen um 9 an den Müll“, „in 2 Stunden an den Kuchen“, „daran, dass ich Oma anrufe“
        var text = Regex.Replace(body, @"\b(?:daran|dran)\s*,?\s*(?:dass|das)\s+(?:ich|wir)\s+", " ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"(?:^|\s)(?:an\s+(?:den|die|das|dem|meinen|meine)|ans|an|zu|to)\s+", " ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\s+(?:zu\s+)?(?:erinnern)$", "", RegexOptions.IgnoreCase).Trim();
        var w = PukkiParse.Parse(text, c.Now);
        // Mit Uhrzeit: eine Aufgabe mit Kategorie „Erinnerung“ (PukkiReminders.cs), kein Kalendertermin
        if (w.Start != null && RemindTask(c, text) is { } reminded) return reminded;
        if (w.Date != null)
        {
            // Ohne Uhrzeit: als Aufgabe - sie steht dann im Tagesüberblick am Morgen
            var task = Do(c.Services, c.Space, c.Mem, "task", text, c.Now);
            return task.Actions?.Any(a => a.Key == "undo") == true
                ? task with { Text = task.Text + " Zu einer bestimmten Uhrzeit erinnern kann ich dich nur mit Uhrzeit – so steht es morgens in deinem Tagesüberblick." }
                : task;
        }
        var title = CleanTitle(w.Title);
        if (title.Length == 0) return "Woran soll ich dich erinnern? Zum Beispiel: Erinnere mich morgen um 9 an den Müll.";
        return AskWhen(c.Mem, text, title, remind: true);
    }

    /// <summary>„1 Stunde“, „2 Tage“, „30 min“, „am Vortag“, „eine halbe Stunde“ - in Minuten. Null: keine Dauer.</summary>
    private static int? ReminderMinutes(string t)
    {
        var s = t.ToLowerInvariant();
        if (Regex.IsMatch(s, @"\b(am vortag|tag davor|tag vorher|einen tag|1 tag)\b")) return 1440;
        if (Regex.IsMatch(s, @"\b(halbe stunde|halben stunde)\b")) return 30;
        if (Regex.IsMatch(s, @"\b(zur zeit|zum beginn|wenn es losgeht|pünktlich|puenktlich|zur startzeit)\b")) return 0;
        var m = Regex.Match(s, @"\b(\d+|eine?n?|einer|zwei|drei|vier|fünf|fuenf|sechs|sieben|acht|zehn|zwölf|fünfzehn|zwanzig|dreißig|dreissig)\s*(minuten?|min|stunden?|std|h|tage?n?|wochen?)\b");
        if (!m.Success) return null;
        var n = int.TryParse(m.Groups[1].Value, out var v) ? v : PukkiParse.Numbers.TryGetValue(m.Groups[1].Value, out var w) ? w : 1;
        var unit = m.Groups[2].Value;
        var minutes = unit.StartsWith("min") ? n : unit.StartsWith("st") || unit == "h" ? n * 60 : unit.StartsWith("tag") ? n * 1440 : n * 10080;
        return minutes is > 0 and <= 60 * 24 * 60 ? minutes : null;
    }

    /// <summary>Aufträge, die Pukki nicht ausführen kann: ehrlich ablehnen, Kontaktdaten nennen, wenn es welche gibt.</summary>
    private static Reply? Refuse(Ctx c, string t)
    {
        var m = RefuseRx.Match(t);
        if (!m.Success) return null;
        // „Schreib Brot auf die Liste“, „Sag Bescheid“ sind keine solchen Aufträge
        if (OnListRx.IsMatch(t) || ListPrefixRx.IsMatch(t)) return null;
        string what;
        if (m.Groups["mail"].Success) what = "Mails oder Nachrichten schreiben";
        else if (m.Groups["call"].Success) what = "Anrufen";
        else if (m.Groups["cancel"].Success || m.Groups["cancel2"].Success) what = "Termine absagen oder Verträge kündigen";
        else if (m.Groups["pay"].Success) what = "Bezahlen oder Geld überweisen";
        else if (m.Groups["order"].Success) what = "Bestellen oder Buchen";
        else what = "Bestehende Einträge löschen";
        if (m.Groups["order"].Success && ShopItems(c.Space, Regex.Replace(t, @"^\S+\s+", "")).Count > 0) return null;
        if (m.Groups["call"].Success && !Regex.IsMatch(t, @"\ban\b|telefonier", RegexOptions.IgnoreCase)) return null;

        // Wen betrifft es? „dem Zahnarzt“, „Oma“, sonst das Thema des Gesprächs
        static bool Thing(string w) => Regex.IsMatch(w, @"^(termin|termine|mail|e-mail|email|nachricht|sms|brief|ab|an|mich|mir|uns|bitte|mal|eine|einen|ein|für|fuer|das|es)$", RegexOptions.IgnoreCase);
        var person = Regex.Matches(t, @"\b(?:dem|der|den|an den|an die|an|bei|beim|meinem|meiner|meine|meinen)\s+(?<who>\p{L}[\p{L}\-\.]*)", RegexOptions.IgnoreCase)
            .Select(x => x.Groups["who"].Value).FirstOrDefault(x => !Thing(x))
            ?? Regex.Match(t, @"^\S+\s+(?:mal\s+|bitte\s+|die\s+|den\s+|der\s+|das\s+)?(?<who>\p{L}[\p{L}\-\.]*)", RegexOptions.IgnoreCase).Groups["who"].Value;
        if (Thing(person)) person = "";
        if (person.Length == 0 || Regex.IsMatch(person, @"^(termin|mail|nachricht|vertrag|rechnung|abo|bestellung)$", RegexOptions.IgnoreCase)) person = c.Mem.Subject ?? "";
        var head = what == "Bestehende Einträge löschen"
            ? "Bestehende Einträge löschen kann ich leider nicht – nur zurücknehmen, was ich gerade eben selbst angelegt habe. Alles andere bitte direkt im Kalender oder in der Liste."
            : $"Das kann ich leider nicht für dich tun – {what} kann ich nicht.";
        // Bezahlen, Bestellen: anbieten, es als Aufgabe zu notieren („überweise die Miete“ → „Miete überweisen“)
        if (m.Groups["pay"].Success || m.Groups["order"].Success)
        {
            var words = t.Trim().TrimEnd('.', '!').Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            var verb = words[0].ToLowerInvariant() switch
            {
                "überweise" or "ueberweise" => "überweisen", "bezahle" or "bezahl" => "bezahlen", "zahle" or "zahl" => "zahlen",
                "bestell" or "bestelle" => "bestellen", "buche" or "buch" => "buchen", var v => v,
            };
            var title = CleanTitle(Regex.Replace(string.Join(' ', words.Skip(1)), @"^(?:(?:mir|uns|bitte|mal|noch)\s+)+", "", RegexOptions.IgnoreCase) + " " + verb);
            return Ask(c.Mem, title, "task", $"{head} Soll ich dir „{title}“ als Aufgabe notieren?", "task");
        }
        if (what == "Bestehende Einträge löschen" || person.Length == 0) return head;
        return ContactAnswer(c, person, head);
    }
}
