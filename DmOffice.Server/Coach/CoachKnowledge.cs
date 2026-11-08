using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DmOffice.Core;

namespace DmOffice.Coach;

/// <summary>
/// Coach-Wissen für Pukki (docs/dmcoach-konzept.md, „Coach-Wissen für Pukki“): Der Coach spricht nicht selbst, er beantwortet
/// Pukkis Fragen zu Ernährung und Gesundheit - aus den eigenen Tagebuchdaten, aus den Nährwerten des BLS und aus Wissenskarten
/// (Coach/Wissen/grundlagen.md). Feste Grenzen vor allem anderen: keine Aussagen zu eigenen Beschwerden, keine Beratung zu
/// Medikamenten (auch nicht über die KI), bei akuten Notfallzeichen nur der Notruf, bei Zeichen einer Essstörung behutsam der Weg
/// zur Hilfe. Alles ohne KI, auf dem Server.
/// </summary>
public static partial class CoachKnowledge
{
    /// <summary>Kind: refuse (Beschwerden, Medikamente), emergency, gentle (Essstörung), data (eigene Daten), food (Nährwerte), card.
    /// Grounding: das allgemeine Wissen dazu für die KI; Personal: eigene Werte, die die KI nur mit Einwilligung bekommt.</summary>
    public sealed record Answer(string Text, string Kind, string? CardId = null, string? Grounding = null, string? Personal = null)
    {
        /// <summary>Feste Grenze: geht nie an die KI.</summary>
        public bool Final => Kind is "refuse" or "emergency" or "gentle";
    }

    public sealed record Card(string Id, string Title, string[] Keys, string Art, string[] NotFor, string? Personal, string Text);

    /// <summary>Frage zu Beschwerden („Was hilft gegen Kopfschmerzen?“) - freundlich ausweichen, nichts beurteilen.</summary>
    public const string Complaint = "Da bin ich leider der Falsche – zu Beschwerden möchte ich nichts sagen. Bitte frag eine Ärztin, einen Arzt " +
        "oder eine andere Fachkraft. Wenn du magst, trage ich dir einen Termin ein, zum Beispiel mit „Termin Hausarzt morgen 9 Uhr“.";
    /// <summary>Beschwerde erzählt („Ich habe Kopfschmerzen“): erst Mitgefühl, dann dasselbe.</summary>
    public const string ComplaintSaid = "Das tut mir leid – Gute Besserung! " + Complaint;
    public const string Medication = "Zu Medikamenten berate ich nicht – bitte frag deine Ärztin, deinen Arzt oder die Apotheke.";
    public const string Emergency = "Das klingt nach einem Notfall. Ruf bitte sofort den Notruf 112 an.";
    public const string Disclaimer = "Das ist allgemeines Wissen und ersetzt keine Beratung durch Ärztin, Arzt oder Ernährungsfachkraft.";

    // ---------------- Grenzen ----------------

    private const RegexOptions I = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    [GeneratedRegex(@"\b(medikament\w*|arznei\w*|tablette\w*|pille\w*|antibabypille|zäpfchen|salbe\w*|dosis|dosierung\w*|überdosis|beipackzettel|nebenwirkung\w*|wechselwirkung\w*|rezeptpflichtig\w*|verschreib\w*|verschrieben|schmerzmittel\w*|schlafmittel\w*|beruhigungsmittel\w*|abführmittel\w*|antibiotik\w*|antidepressiv\w*|betablocker\w*|blutdrucksenker\w*|blutverdünn\w*|cholesterinsenker\w*|statin\w*|insulin|insulinspritze\w*|metformin|ibuprofen|paracetamol|aspirin|diclofenac|novalgin|metamizol|kortison|cortison|marcumar|eliquis|xarelto|ramipril|bisoprolol|l-thyroxin|thyroxin|ozempic|wegovy|mounjaro|semaglutid|tirzepatid|abnehmspritze\w*|pille danach)\b", I)]
    private static partial Regex MedicationRx();

    /// <summary>Beschwerden, auf die sich eine Frage oder Aussage beziehen kann.</summary>
    [GeneratedRegex(@"\b(\w*schmerz\w*|\w*weh\b|tut\s+\w*\s*weh|übel\w*|erbrech\w*|übergeb\w*|kotz\w*|durchfall\w*|verstopf\w*|blähung\w*|sodbrennen|aufstoßen|schwindel\w*|schwindlig|migräne|fieber\w*|husten|schnupfen|erkält\w*|grippe|ausschlag\w*|juck\w*|kribbel\w*|taub\w*|herzrasen|herzstolpern|herzklopfen|atemnot|krampf\w*|krämpfe|zitter\w*|entzünd\w*|geschwollen|schwellung\w*|blut(e|et|ung\w*)?\b|bluten|ohnmacht\w*|ohnmächtig|unterzucker\w*|hypo\b|allergische\s+reaktion|beschwerden|symptom\w*|krank\b|kränklich|verletz\w*|wunde\b|gebrochen|verstaucht|prellung|brennen\w*|stechen\w*|druck\s+auf\s+der\s+brust|wassereinlagerung\w*|haarausfall|müdigkeit|erschöpf\w*|benommen|muskelkater|sonnenbrand)\b", I)]
    private static partial Regex SymptomRx();

    /// <summary>Bezug auf sich selbst (oder Angehörige) bzw. die Frage nach einer Abhilfe - dann ist es eine Frage zu Beschwerden.</summary>
    [GeneratedRegex(@"\b(ich|mir|mich|mein\w*|wir|uns|unser\w*|kind|sohn|tochter|mann|frau|partner\w*|mama|papa|mutter|vater|oma|opa)\b|\b(hilft|helfen|tun|mittel|hausmittel|machen)\s+(\w+\s+){0,2}(gegen|bei)\b|\bgegen\s+\w+\s*(schmerz\w*|weh)\b|\b(ist|sind)\s+(das|es)\s+(gefährlich|schlimm|normal)\b", I)]
    private static partial Regex SelfRx();

    /// <summary>Notfallzeichen - ohne Diagnose: nur, dass jemand dringend Hilfe braucht.</summary>
    [GeneratedRegex(@"\b(keine\s+luft|atemnot|atmet\s+nicht|atmet\s+kaum|ringt\s+nach\s+luft|brustschmerz\w*|schmerz\w*\s+in\s+der\s+brust|druck\s+auf\s+der\s+brust|bewusstlos\w*|ohnmächtig|nicht\s+ansprechbar|reagiert\s+(gar\s+)?nicht(\s+mehr)?|zusammengeklappt|zusammengebrochen|zusammengesackt|umgekippt|kollabiert|liegt\s+(\w+\s+){0,3}(am|auf\s+dem)\s+boden|zuckt|zuckend\w*|zuckungen|krampfanfall|krampft|krampfend|blau\s+angelaufen|lippen\s+sind\s+blau|blut\w*\s+(stark|sehr|heftig)|stark\w*\s+blut\w*|lähmung|gelähmt|gesicht\s+hängt|kann\s+nicht\s+(mehr\s+)?sprechen|allergischer\s+schock|zunge\s+schwillt|hals\s+schwillt|vergiftet|vergiftung|verschluckt\s+und|erstick\w*|ertrinkt|ertrunken|herzinfarkt|schlaganfall|herzstillstand)\b", I)]
    private static partial Regex EmergencyRx();

    /// <summary>Jemand Bestimmtes ist betroffen (dann ist es ein Notfall jetzt, keine Wissensfrage).</summary>
    [GeneratedRegex(@"\b(ich|mir|mich|mein\w*|dein\w*|er|sie|kind|baby|jemand|person|mann|frau|freund\w*|kolleg\w*|vater|mutter|papa|mama|oma|opa|bruder|schwester|sohn|tochter|nachbar\w*|partner\w*|gast|gäste)\b", I)]
    private static partial Regex SomeoneRx();

    /// <summary>Allgemein gefragt („Was tun, wenn jemand bewusstlos ist?“, „Woran erkennt man einen Schlaganfall?“).</summary>
    [GeneratedRegex(@"\b(wenn|falls|man|erkennt|anzeichen|was\s+ist\s+(ein|eine)|was\s+passiert)\b", I)]
    private static partial Regex HypotheticalRx();

    [GeneratedRegex(@"\b(suizid\w*|selbstmord\w*|umbringen|nicht\s+mehr\s+leben|will\s+sterben|mich\s+töten)\b", I)]
    private static partial Regex CrisisRx();

    public const string EmergencyInfo = "Wenn das gerade passiert: Ruf bitte sofort den Notruf 112 an – die Leitstelle sagt dir am Telefon, was zu tun ist.";

    [GeneratedRegex(@"\b(ich|mir|mich)\b.{0,40}\b(übergebe\s+mich\s+(absichtlich|nach\s+dem\s+essen)|erbreche\s+(absichtlich|nach\s+dem\s+essen)|kotze\s+nach\s+dem\s+essen|hungere|esse\s+(fast\s+|gar\s+)?nichts\s+mehr|will\s+nichts\s+(mehr\s+)?essen|darf\s+nichts\s+essen|hasse\s+meinen\s+körper|ekel\w*\s+mich\s+vor\s+mir|fress-?attacke\w*|essattacke\w*|ess-?anfall\w*|angst\s+(vor\s+dem|zu)\s+essen)\b|\b(magersucht|bulimie|binge)\b.{0,30}\b(ich|mir|mich)\b|\b(ich|mir|mich)\b.{0,30}\b(magersucht|bulimie|binge)", I)]
    private static partial Regex EatingDisorderRx();

    /// <summary>
    /// Feste Grenzen vor allem anderen (nie über die KI): Notfall, Essstörung, Medikamente, eigene Beschwerden. Null: keine Grenze
    /// berührt. Aufträge mit Tag oder Uhrzeit („Termin Hausarzt morgen 9 Uhr wegen Rückenschmerzen“) sind kein Fall dafür -
    /// das prüft der Aufrufer.
    /// </summary>
    public static Answer? Guard(string text)
    {
        var t = text.Trim();
        if (CrisisRx().IsMatch(t))
            return new Answer("Es tut mir leid, dass es dir so schlecht geht. Bitte hol dir jetzt Hilfe: In akuter Gefahr wähle den Notruf 112. " +
                "Die Telefonseelsorge ist rund um die Uhr für dich da – in Deutschland unter 0800 111 0 111, in Österreich unter 142.", "emergency");
        // Notfall: keine Vermutung, was es ist - nur Hilfe holen. Allgemein gefragt: derselbe Hinweis, mit KI darf sie mehr sagen
        var info = false;
        if (EmergencyRx().IsMatch(t))
        {
            if (HypotheticalRx().IsMatch(t) || !SomeoneRx().IsMatch(t)) info = true;
            else
            {
                var self = !Regex.IsMatch(t, @"\b(mein\w*|dein\w*|er|sie|kind|baby|jemand|person|mann|frau|freund\w*|kolleg\w*|vater|mutter|papa|mama|oma|opa|bruder|schwester|sohn|tochter|nachbar\w*|partner\w*|gast|gäste)\b", I);
                return new Answer(self ? Emergency + " Wenn du nicht selbst anrufen kannst, bitte jemanden in deiner Nähe darum."
                    : Emergency + " Bleib bei der Person – die Leitstelle sagt dir am Telefon, was du tun kannst.", "emergency");
            }
        }
        if (EatingDisorderRx().IsMatch(t))
            return new Answer("Danke, dass du mir das schreibst – das klingt, als ginge es dir mit dem Essen gerade nicht gut. Dabei bin ich nicht die richtige Hilfe. " +
                "Bitte sprich mit deiner Hausärztin, deinem Hausarzt oder einer Beratungsstelle für Essstörungen – dort hilft man dir, ohne zu urteilen. Du musst das nicht allein schaffen.", "gentle", "essstoerung");
        if (MedicationRx().IsMatch(t)) return new Answer(Medication, "refuse");
        // „Letzte Woche war ich krank“ erzählt von früher - „seit gestern“ ist jetzt
        var past = Regex.IsMatch(t, @"\b(war|waren|hatte|hatten|gewesen|damals|früher|letzte\s+woche|letzten\s+monat|letztes\s+jahr)\b", I) && !Regex.IsMatch(t, @"\bseit\b", I);
        if (SymptomRx().IsMatch(t) && SelfRx().IsMatch(t) && !past)
            return new Answer(t.EndsWith('?') || KnowledgeRx().IsMatch(t) ? Complaint : ComplaintSaid, "refuse");
        return info ? new Answer(EmergencyInfo, "emergency-info") : null;
    }

    /// <summary>
    /// Vor jeder KI-Frage: die festen Grenzen, dazu kurze Nachrichten, die nur eine Beschwerde nennen („Kopfschmerzen?“). Allgemeine
    /// Wissensfragen („Wie lange dauert es, bis man verblutet?“) bleiben bei der KI.
    /// </summary>
    public static Answer? GuardForAi(string text)
    {
        if (Guard(text) is { } g) return g.Kind == "emergency-info" ? null : g;
        var t = text.Trim();
        return SymptomRx().IsMatch(t) && Bls.Words(Bls.Fold(t)).Length <= 4 ? new Answer(Complaint, "refuse") : null;
    }

    // ---------------- Karten ----------------

    private static readonly Lazy<List<Card>> CardsLazy = new(LoadCards);
    public static IReadOnlyList<Card> Cards => CardsLazy.Value;

    private static readonly Lazy<Dictionary<string, string[]>> AssocLazy = new(LoadAssociations);

    private static Stream Resource(string name) =>
        typeof(CoachKnowledge).Assembly.GetManifestResourceStream("DmOffice.Coach.Wissen." + name)
        ?? throw new InvalidOperationException("Coach-Wissen fehlt im Programm: " + name);

    private static List<Card> LoadCards()
    {
        using var r = new StreamReader(Resource("grundlagen.md"), Encoding.UTF8);
        return ParseCards(r.ReadToEnd());
    }

    /// <summary>Karten aus dem Markdown: „## id: Titel“, Kopfzeilen, dann der Text bis zur nächsten Karte.</summary>
    public static List<Card> ParseCards(string md)
    {
        var cards = new List<Card>();
        foreach (var block in Regex.Split(md.Replace("\r", ""), @"^## ", RegexOptions.Multiline).Skip(1))
        {
            var lines = block.Split('\n');
            var head = Regex.Match(lines[0], @"^(?<id>[a-z0-9-]+):\s*(?<title>.+)$");
            if (!head.Success) continue;
            string[] keys = [], notFor = [];
            string art = "wissen";
            string? personal = null;
            var text = new StringBuilder();
            foreach (var line in lines.Skip(1))
            {
                var kv = Regex.Match(line, @"^(Stichwörter|Art|Nicht bei|Persönlich):\s*(.*)$");
                if (kv.Success && text.Length == 0)
                {
                    var v = kv.Groups[2].Value.Trim();
                    switch (kv.Groups[1].Value)
                    {
                        case "Stichwörter": keys = v.Split(',').Select(k => Bls.Fold(k.Trim())).Where(k => k.Length > 1).Distinct().ToArray(); break;
                        case "Art": art = v; break;
                        case "Nicht bei": notFor = v.Split(',').Select(x => x.Trim()).ToArray(); break;
                        case "Persönlich": personal = v; break;
                    }
                }
                else if (line.Trim().Length > 0 || text.Length > 0) text.Append(line).Append('\n');
            }
            cards.Add(new Card(head.Groups["id"].Value, head.Groups["title"].Value.Trim(), keys, art, notFor, personal,
                Regex.Replace(text.ToString().Trim(), @"\n{3,}", "\n\n")));
        }
        return cards;
    }

    /// <summary>Assoziationen (Coach/Wissen/assoziationen.json): Wort → Karten, etwa „grunkohl“ → ballaststoffe, obstgemuese.</summary>
    private static Dictionary<string, string[]> LoadAssociations()
    {
        using var s = Resource("assoziationen.json");
        var raw = JsonSerializer.Deserialize<Dictionary<string, string[]>>(s) ?? [];
        return raw.Where(kv => !kv.Key.StartsWith('_')).ToDictionary(kv => Bls.Fold(kv.Key), kv => kv.Value);
    }

    /// <summary>Die passendste Karte: Stichwörter (mehrteilige im Satz, einzelne als Wort, lange auch als Wortanfang oder -teil),
    /// dazu Assoziationen schwach. Null unter 2 Punkten.</summary>
    public static Card? Match(string question)
    {
        var folded = " " + string.Join(' ', Bls.Words(Bls.Fold(question))) + " ";
        var words = Bls.Words(folded.Trim());
        Card? best = null;
        double top = 0;
        var assoc = new Dictionary<string, double>();
        foreach (var w in words)
            if (AssocLazy.Value.TryGetValue(w, out var ids))
                foreach (var id in ids) assoc[id] = assoc.GetValueOrDefault(id) + 1.5;
        foreach (var card in Cards)
        {
            double score = assoc.GetValueOrDefault(card.Id);
            foreach (var k in card.Keys)
            {
                if (k.Contains(' ')) { if (folded.Contains(" " + k + " ")) score += 3; continue; }
                if (words.Contains(k)) score += 2;
                else if (k.Length >= 5 && words.Any(w => w.StartsWith(k, StringComparison.Ordinal) || (k.Length >= 6 && w.Length > k.Length && w.Contains(k, StringComparison.Ordinal)))) score += 1;
            }
            if (score > top) { top = score; best = card; }
        }
        return top >= 2 ? best : null;
    }

    /// <summary>Wissensfrage („Was ist …“, „Wie viel …“, „Tipps …“, „Erklär mir …“) - nicht „Haben wir noch Zucker?“ oder Termine.</summary>
    [GeneratedRegex(@"^(?:und\s+|aber\s+|also\s+|sag mal,?\s+)?(was|wie|warum|wieso|weshalb|welche\w*|wo|soll|sollte|darf|kann|könnte|ist|sind|hilft|helfen|macht|stimmt|brauche|braucht|gibt es|gibt's|bringt|lohnt|worauf|wann)\b|\b(erklär\w*|tipps?|ratschl\w*|infos?|information\w*|was weißt du|was weisst du|erzähl\w*|kannst du mir|was kann ich|was soll ich|was tun|was hilft|wie schaffe ich|wie kann ich|woran liegt|warum|wieso|weshalb)\b", I)]
    private static partial Regex KnowledgeRx();

    [GeneratedRegex(@"\b(haben wir|brauchen wir|einkauf\w*|liste|termin\w*|kalender|aufgabe\w*|erinner\w*|notiz\w*|morgen|übermorgen|nächste woche|wann ist|was steht|steht an|essensplan|zu essen|kochen wir|gibt es heute|gibt's heute|gibt es noch|gibt's noch|ist noch|sind noch|noch da|da|vorrätig|vorrat|im haus|zuhause|daheim|alle zutaten|zutaten (für|dafür))\s*\??$|\b(haben wir|brauchen wir|einkauf\w*|liste|termin\w*|kalender|aufgabe\w*|erinner\w*|notiz\w*|morgen|übermorgen|nächste woche|wann ist|was steht|steht an|essensplan|zu essen|kochen wir|gibt es heute|gibt's heute|gibt es noch|gibt's noch|alle zutaten|vorrätig)\b", I)]
    private static partial Regex NotCoachRx();

    // ---------------- Antworten ----------------

    /// <summary>
    /// Antwort des Coachs auf eine Frage, null: keine Coach-Frage. Reihenfolge: eigene Daten, Nährwerte, Karten. Die Grenzen
    /// (<see cref="Guard"/>) prüft der Aufrufer vorher.
    /// </summary>
    public static Answer? Ask(UserSpace space, CoachFoods foods, string question, DateTime now)
    {
        var t = question.Trim();
        var today = DateOnly.FromDateTime(now);
        var p = CoachStore.Profile(space.Db);
        var targets = CoachTargetsCalc.Compute(p, CoachStore.Weight(space.Db, today), today);
        if (OwnData(space, t, today, p, targets, now) is { } data) return data;
        if (NotCoachRx().IsMatch(t)) return null;
        if (FoodQuestion(space, foods, t, today, p) is { } food) return food;
        if (!KnowledgeRx().IsMatch(t)) return null;
        var card = Match(t);
        return card == null ? null : FromCard(card, p, targets);
    }

    /// <summary>Text einer Karte für dieses Profil: bei „Nicht bei“ der Hinweis statt Tipps, der persönliche Satz nur mit allen
    /// Werten und ohne ausgeblendete Zahlen, bei Gesundheitsthemen der Hinweis, dass es keine Beratung ersetzt.</summary>
    public static Answer FromCard(Card card, CoachProfile p, CoachTargets targets)
    {
        var minor = targets.Age is int age && age < 18;
        var under = targets.Bmi is double bmi && bmi < 18.5;
        string? stop = null;
        if (card.NotFor.Contains("minderjährig") && minor)
            stop = "Unter 18 gebe ich keine Tipps zum Abnehmen, zu Diäten oder zum Fasten – dein Körper wächst noch und braucht alles, was er bekommt. Wenn du dir Gedanken um dein Gewicht oder dein Essen machst, sprich gern mit deinen Eltern oder deiner Kinderärztin bzw. deinem Kinderarzt.";
        else if (card.NotFor.Contains("schwanger") && p.Pregnant)
            stop = "In Schwangerschaft und Stillzeit gebe ich dazu keine Tipps – Diäten und Fasten sind dann nicht vorgesehen. Sprich bitte mit deiner Frauenärztin, deinem Frauenarzt oder deiner Hebamme.";
        else if (card.NotFor.Contains("untergewicht") && under)
            stop = "Bei deinem Gewicht gebe ich dazu keine Tipps – dein BMI liegt unter 18,5. Wenn du dir Sorgen um dein Gewicht machst, sprich bitte mit einer Ärztin oder einem Arzt.";
        if (stop != null) return new Answer(stop, "gentle", card.Id);

        var personal = Personal(card.Personal, p, targets);
        var text = card.Text + (personal != null ? "\n\n" + personal : "") + (card.Art == "gesundheit" ? "\n\n" + Disclaimer : "");
        return new Answer(text, "card", card.Id, $"Wissen aus dem Coach („{card.Title}“):\n{card.Text}", personal);
    }

    private static string? Personal(string? template, CoachProfile p, CoachTargets t)
    {
        if (template == null || p.HideNumbers) return null;
        var values = new Dictionary<string, double?>
        {
            ["bmr"] = t.Bmr, ["tdee"] = t.Tdee, ["kcal"] = t.Kcal, ["protein"] = t.Protein, ["carbs"] = t.Carbs, ["fat"] = t.Fat,
            ["fiber"] = t.Fiber, ["water"] = t.WaterMl, ["bmi"] = t.Bmi, ["weight"] = p.WeightKg,
        };
        var missing = false;
        var s = Regex.Replace(template, @"\{(\w+)\}", m =>
        {
            if (values.GetValueOrDefault(m.Groups[1].Value) is double v) return Num(v, m.Groups[1].Value == "bmi" ? 1 : 0);
            missing = true;
            return "";
        });
        return missing ? null : s;
    }

    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");
    private static string Num(double v, int digits = 0) => Math.Round(v, digits).ToString(digits == 0 ? "#,0" : "#,0.#", De);

    // ---------------- Eigene Daten ----------------

    [GeneratedRegex(@"\b(ich|mir|mein\w*)\b", I)] private static partial Regex MeRx();

    private static Answer? OwnData(UserSpace space, string t, DateOnly today, CoachProfile p, CoachTargets targets, DateTime now)
    {
        var heute = Regex.IsMatch(t, @"\bheute\b", I);
        if (!(MeRx().IsMatch(t) || heute) || NotCoachRx().IsMatch(t) && !heute) return null;
        var db = space.Db;
        var hide = p.HideNumbers;
        var entries = CoachStore.Diary(db, today);
        double eaten = entries.Sum(e => e.Kcal);

        // Wochenrückblick
        if (Regex.IsMatch(t, @"(wie\s+(war|lief|läuft)\s+(meine|die)\s+woche|wochenrückblick|wochenbilanz|mein(en)?\s+fortschritt|wie\s+(läuft|geht)\s+es\s+(mit\s+)?(meinem\s+)?(abnehmen|coach|ernährung))", I))
            return Week(db, today, p, targets);

        // Kalorien übrig
        if (Regex.IsMatch(t, @"(darf|kann|soll)\s+ich\s+(heute\s+)?(noch|insgesamt)|(kalorien|kcal)\s+(habe|hab)\s+ich\s+(heute\s+)?(noch|übrig)|wie\s*viel\w*\s+(kalorien|kcal)\s+(sind|habe ich)\s+(noch\s+)?übrig|noch\s+essen\b", I)
            && !Regex.IsMatch(t, @"\b(eiweiß|eiweiss|protein|kohlenhydrate|fett|zucker|ballaststoffe|wasser|trinken)\b", I))
        {
            if (targets.Kcal is not double goal) return new Answer("Dafür fehlen im Coach noch Angaben – trag im Profil Geburtsdatum, Geschlecht, Größe und Gewicht ein, dann rechne ich dein Tagesziel aus.", "data");
            if (hide) return new Answer($"Du hast heute etwa {Num(Math.Min(999, eaten / goal * 100))} % deines Tagesziels gegessen.", "data", Personal: "Heute gegessen: " + Num(eaten / goal * 100) + " % des Ziels");
            var left = goal - eaten;
            var text = left >= 0 ? $"Du hast heute {Num(eaten)} kcal gegessen – bis zu deinem Ziel von {Num(goal)} kcal bleiben noch etwa {Num(left)} kcal."
                : $"Du hast heute {Num(eaten)} kcal gegessen, etwa {Num(-left)} kcal über deinem Ziel von {Num(goal)} kcal. Kein Grund zur Sorge – ein Tag zählt wenig, morgen geht es normal weiter.";
            return new Answer(text, "data", Personal: text);
        }

        // Nährstoffe heute
        var nm = Regex.Match(t, @"wie\s*viel\w*\s+(?<n>kalorien|kcal|eiweiß|eiweiss|protein|kohlenhydrate|carbs|fett|ballaststoffe|zucker|salz)\s+(habe|hab|hatte)\s+ich", I);
        if (nm.Success)
        {
            var n = Bls.Fold(nm.Groups["n"].Value);
            var (label, value, goal, unit) = n switch
            {
                "kalorien" or "kcal" => ("Kalorien", eaten, targets.Kcal, "kcal"),
                "eiweiss" or "protein" => ("Eiweiß", entries.Sum(e => e.Protein), targets.Protein, "g"),
                "kohlenhydrate" or "carbs" => ("Kohlenhydrate", entries.Sum(e => e.Carbs), targets.Carbs, "g"),
                "fett" => ("Fett", entries.Sum(e => e.Fat), targets.Fat, "g"),
                "ballaststoffe" => ("Ballaststoffe", entries.Sum(e => e.Fiber ?? 0), (double?)targets.Fiber, "g"),
                "zucker" => ("Zucker", entries.Sum(e => e.Sugar ?? 0), (double?)null, "g"),
                _ => ("Salz", entries.Sum(e => e.Salt ?? 0), 6, "g"),
            };
            if (entries.Count == 0) return new Answer("Heute ist im Coach noch nichts eingetragen.", "data");
            if (hide && unit == "kcal") return new Answer(goal is double g0 ? $"Du hast heute etwa {Num(value / g0 * 100)} % deines Tagesziels gegessen." : "Heute ist schon etwas eingetragen.", "data");
            var text = $"{label} heute: {Num(value, unit == "g" ? 1 : 0)} {unit}" + (goal is double g ? $" von etwa {Num(g)} {unit}." : ".");
            return new Answer(text, "data", Personal: text);
        }

        // Wasser
        if (Regex.IsMatch(t, @"\b(wasser|getrunken|trinkziel|trinkmenge|genug\s+trinke?n?)\b", I))
        {
            var ml = CoachStore.WaterMl(db, today);
            var goal = targets.WaterMl;
            var text = ml >= goal ? $"Du hast heute {Num(ml / 1000.0, 2)} l getrunken – dein Ziel von {Num(goal / 1000.0, 2)} l ist geschafft. 💧"
                : $"Du hast heute {Num(ml / 1000.0, 2)} l getrunken, bis zu deinem Ziel von {Num(goal / 1000.0, 2)} l fehlen noch etwa {Num((goal - ml) / 1000.0, 2)} l.";
            return new Answer(text, "data", Personal: text);
        }

        // Schritte
        if (Regex.IsMatch(t, @"\bschritte?\b|\bschrittziel\b", I))
        {
            var act = CoachStore.ActivityOf(db, today);
            var goal = CoachStore.StepGoal(db, today);
            if (act?.Steps is not int steps) return new Answer($"Für heute sind noch keine Schritte da. Dein Ziel liegt bei {Num(goal)} Schritten – verbinde die Handy-App mit Health Connect oder trag sie im Coach von Hand ein.", "data");
            var text = steps >= goal ? $"Du bist heute {Num(steps)} Schritte gegangen – Ziel von {Num(goal)} geschafft! 🎉"
                : $"Du bist heute {Num(steps)} Schritte gegangen, bis zu deinem Ziel von {Num(goal)} fehlen noch {Num(goal - steps)}.";
            return new Answer(text, "data", Personal: text);
        }

        // Fasten
        if (Regex.IsMatch(t, @"\bmein\w*\s+fast\w*|\bfaste\s+ich\b|\bfastest\b|\bich\s+(noch\s+)?fasten\b|\bich\s+\w+\s+noch\s+fasten\b", I))
        {
            if (CoachFasting.Current(db) is not { } fast) return new Answer("Du fastest gerade nicht. Im Coach unter „Fasten“ kannst du ein Fasten starten.", "data");
            var nowUtc = DateTime.UtcNow;
            var end = fast.Start.AddHours(fast.Goal);
            var endLocal = TimeZoneInfo.ConvertTimeFromUtc(end, space.Zone);
            var hours = fast.Hours(nowUtc);
            var text = end <= nowUtc ? $"Du fastest seit {Num(hours, 1)} Stunden – dein Ziel von {Num(fast.Goal)} Stunden ist erreicht. Du kannst das Fasten im Coach beenden."
                : $"Du fastest seit {Num(hours, 1)} Stunden. Dein Ziel von {Num(fast.Goal)} Stunden erreichst du um {endLocal:HH:mm} Uhr" + (endLocal.Date != now.Date ? $" ({endLocal.ToString("dddd", De)})." : ".");
            return new Answer(text, "data", Personal: text);
        }

        // Gewicht
        if (Regex.IsMatch(t, @"(wie\s*viel\s+wiege\s+ich|mein\s+gewicht|was\s+wiege\s+ich)", I) && !Regex.IsMatch(t, @"\bsollte\b", I))
        {
            var w = CoachStore.Weight(db, today);
            if (w == null) return new Answer("Im Coach ist noch kein Gewicht eingetragen.", "data");
            if (hide) return new Answer("Dein Gewicht steht im Coach – Zahlen hast du im Profil ausgeblendet.", "data");
            var text = $"Zuletzt eingetragen: {Num(w.Value, 1)} kg.";
            return new Answer(text, "data", Personal: text);
        }
        return null;
    }

    internal static Answer Week(UserDb db, DateOnly today, CoachProfile p, CoachTargets targets)
    {
        var days = CoachStore.Stats(db, today.AddDays(-6), today);
        var logged = days.Where(d => d.Entries > 0).ToList();
        if (logged.Count == 0) return new Answer("In den letzten sieben Tagen ist im Coach nichts eingetragen. Fang einfach mit der nächsten Mahlzeit an – ich helfe dir gern.", "data");
        var sb = new StringBuilder($"Deine letzten sieben Tage: An {logged.Count} Tagen hast du eingetragen.");
        if (!p.HideNumbers && targets.Kcal is double goal)
        {
            var avg = logged.Average(d => d.Kcal);
            sb.Append($" Im Schnitt {Num(avg)} kcal am Tag bei einem Ziel von {Num(goal)} kcal.");
        }
        var water = days.Count(d => d.Water >= targets.WaterMl);
        if (water > 0) sb.Append($" Dein Trinkziel hast du an {water} Tagen geschafft.");
        var steps = days.Where(d => d.Steps != null).ToList();
        if (steps.Count > 0) sb.Append($" Schritte im Schnitt: {Num(steps.Average(d => d.Steps!.Value))}.");
        var weights = days.Where(d => d.Weight != null).ToList();
        if (!p.HideNumbers && weights.Count >= 2)
        {
            var diff = weights[^1].Weight!.Value - weights[0].Weight!.Value;
            sb.Append(Math.Abs(diff) < 0.2 ? " Dein Gewicht ist stabil." : $" Gewicht: {(diff < 0 ? "−" : "+")}{Num(Math.Abs(diff), 1)} kg.");
        }
        sb.Append(" Was möchtest du dir für die nächste Woche vornehmen?");
        return new Answer(sb.ToString(), "data", Personal: sb.ToString());
    }

    // ---------------- Nährwerte ----------------

    private sealed record Nutrient(string Key, string Label, string Unit, Func<Nutrients, double?>? Macro, string? Micro, string? Reference);

    private static readonly Nutrient[] NutrientList =
    [
        new("kcal", "Kalorien", "kcal", n => n.Kcal, null, null),
        new("protein", "Eiweiß", "g", n => n.Protein, null, null),
        new("carbs", "Kohlenhydrate", "g", n => n.Carbs, null, null),
        new("sugar", "Zucker", "g", n => n.Sugar, null, null),
        new("fat", "Fett", "g", n => n.Fat, null, null),
        new("satfat", "gesättigte Fettsäuren", "g", n => n.SatFat, null, null),
        new("fiber", "Ballaststoffe", "g", n => n.Fiber, null, "Die DGE empfiehlt mindestens 30 g Ballaststoffe am Tag."),
        new("salt", "Salz", "g", n => n.Salt, null, "Die DGE empfiehlt höchstens 6 g Salz am Tag."),
        new("b12", "Vitamin B12", "µg", null, "VITB12", "Erwachsene brauchen laut DGE etwa 4 µg Vitamin B12 am Tag."),
        new("d", "Vitamin D", "µg", null, "VITD", "Der Referenzwert der DGE liegt bei 20 µg am Tag, wenn die Haut kein Vitamin D bildet; den Großteil bildet sie normalerweise selbst mit Sonnenlicht."),
        new("c", "Vitamin C", "mg", null, "VITC", "Erwachsene brauchen laut DGE etwa 95 mg (Frauen) bis 110 mg (Männer) Vitamin C am Tag."),
        new("folat", "Folat", "µg", null, "FOL", "Erwachsene brauchen laut DGE etwa 300 µg Folat am Tag."),
        new("eisen", "Eisen", "mg", null, "FE", "Laut DGE brauchen Männer etwa 11 mg, Frauen je nach Alter 14 bis 16 mg Eisen am Tag. Pflanzliches Eisen wird mit Vitamin C besser aufgenommen."),
        new("calcium", "Calcium", "mg", null, "CA", "Erwachsene brauchen laut DGE etwa 1000 mg Calcium am Tag."),
        new("magnesium", "Magnesium", "mg", null, "MG", "Erwachsene brauchen laut DGE etwa 300 mg (Frauen) bis 350 mg (Männer) Magnesium am Tag."),
        new("kalium", "Kalium", "mg", null, "K", "Für Erwachsene schätzt die DGE den Bedarf auf etwa 4000 mg Kalium am Tag."),
        new("zink", "Zink", "mg", null, "ZN", "Erwachsene brauchen laut DGE je nach Ernährung etwa 7 bis 16 mg Zink am Tag."),
        new("jod", "Jod", "µg", null, "ID", "Erwachsene brauchen etwa 150 bis 200 µg Jod am Tag; Jodsalz und Seefisch helfen."),
    ];

    private const string NutrientWords = @"kalorien|kcal|energie|brennwert|eiweiß|eiweiss|proteine?|kohlenhydrate|kohlehydrate|carbs|zucker|fett|gesättigte\s+fettsäuren|ballaststoffe|salz|vitamin\s*b\s*12|b12|vitamin\s*d|vitamin\s*c|folat|folsäure|eisen|calcium|kalzium|magnesium|kalium|zink|jod";

    private static Nutrient? NutrientOf(string word)
    {
        var w = Regex.Replace(Bls.Fold(word), @"\s+", "");
        return w switch
        {
            "kalorien" or "kcal" or "energie" or "brennwert" => NutrientList[0],
            "eiweiss" or "protein" or "proteine" => NutrientList[1],
            "kohlenhydrate" or "kohlehydrate" or "carbs" => NutrientList[2],
            "zucker" => NutrientList[3],
            "fett" => NutrientList[4],
            "gesattigtefettsauren" => NutrientList[5],
            "ballaststoffe" => NutrientList[6],
            "salz" => NutrientList[7],
            "vitaminb12" or "b12" => NutrientList[8],
            "vitamind" => NutrientList[9],
            "vitaminc" => NutrientList[10],
            "folat" or "folsaure" => NutrientList[11],
            "eisen" => NutrientList[12],
            "calcium" or "kalzium" => NutrientList[13],
            "magnesium" => NutrientList[14],
            "kalium" => NutrientList[15],
            "zink" => NutrientList[16],
            "jod" => NutrientList[17],
            _ => null,
        };
    }

    [GeneratedRegex(@"^(?:und\s+)?wie\s*viel\w*\s+(?<n>" + NutrientWords + @")\s+(?:hat|haben|enthält|enthalten|steckt|stecken|ist|sind|liefert|liefern)\s+(?:in\s+)?(?<f>.+?)(?:\s+(?:drin|darin|drinnen))?\s*[?!.]*$", I)]
    private static partial Regex HowMuchRx();

    [GeneratedRegex(@"^(?:und\s+)?(?:was|welche\w*)\s+(?:hat|haben|enthält|enthalten|liefert)\s+mehr\s+(?<n>" + NutrientWords + @")\s*[,:]?\s*(?<a>.+?)\s+oder\s+(?<b>.+?)\s*[?!.]*$", I)]
    private static partial Regex CompareRx();

    [GeneratedRegex(@"(?:was|welche\s+lebensmittel|welches\s+lebensmittel|wo|worin|lebensmittel)\s+(?:hat|haben|enthält|enthalten|ist|steckt|stecken|liefert|liefern|mit)\s+(?:besonders\s+|am\s+meisten\s+|sehr\s+|viel\s+)*(?:viel(?:e)?\s+)?(?<n>" + NutrientWords + @")\b", I)]
    private static partial Regex RichRx();

    private static Answer? FoodQuestion(UserSpace space, CoachFoods foods, string t, DateOnly today, CoachProfile p)
    {
        if (CompareRx().Match(t) is { Success: true } cm && NutrientOf(cm.Groups["n"].Value) is { } cn)
        {
            var a = Lookup(space, foods, cm.Groups["a"].Value, today);
            var b = Lookup(space, foods, cm.Groups["b"].Value, today);
            if (a == null || b == null) return new Answer($"Das kann ich nicht vergleichen – „{(a == null ? cm.Groups["a"].Value : cm.Groups["b"].Value).Trim()}“ finde ich in den Nährwerten nicht.", "food");
            var va = Value(cn, a);
            var vb = Value(cn, b);
            if (va == null || vb == null) return new Answer($"Für {cn.Label} fehlen mir bei einem der beiden die Werte.", "food");
            var more = va > vb ? a : b;
            var text = Math.Abs(va.Value - vb.Value) < 0.05 * Math.Max(va.Value, vb.Value)
                ? $"Etwa gleich viel {cn.Label}: {a.Name} {Num(va.Value, 1)} {cn.Unit}, {b.Name} {Num(vb.Value, 1)} {cn.Unit} je 100 g."
                : $"{more.Name} hat mehr {cn.Label}: {a.Name} {Num(va.Value, 1)} {cn.Unit}, {b.Name} {Num(vb.Value, 1)} {cn.Unit} je 100 g.";
            if (Regex.IsMatch(a.Name + b.Name, @"\broh\b", I)) text += " Achtung: Roh und gekocht unterscheiden sich stark – Reis und Nudeln nehmen beim Kochen viel Wasser auf.";
            return new Answer(text + Source(a, b), "food");
        }
        if (HowMuchRx().Match(t) is { Success: true } hm && NutrientOf(hm.Groups["n"].Value) is { } hn)
        {
            var f = Lookup(space, foods, hm.Groups["f"].Value, today);
            if (f == null) return new Answer($"„{Clean(hm.Groups["f"].Value)}“ finde ich in den Nährwerten nicht. Frag mich gern mit einem anderen Wort.", "food");
            if (Value(hn, f) is not double v) return new Answer($"Für {f.Name} habe ich keinen Wert für {hn.Label}.", "food");
            var text = $"{f.Name}: {Num(v, v < 10 ? 1 : 0)} {hn.Unit} {hn.Label} je 100 {(f.Liquid ? "ml" : "g")}";
            var portion = f.Portions.FirstOrDefault(x => x.Grams != 100);
            if (portion != null) text += $", {portion.Label} ({Num(portion.Grams)} {(f.Liquid ? "ml" : "g")}) etwa {Num(v * portion.Grams / 100, v * portion.Grams / 100 < 10 ? 1 : 0)} {hn.Unit}";
            text += ".";
            if (hn.Reference != null) text += " " + hn.Reference;
            return new Answer(text + Source(f), "food");
        }
        if (RichRx().Match(t) is { Success: true } rm && NutrientOf(rm.Groups["n"].Value) is { } rn && rn.Key != "kcal")
        {
            var top = Rich(rn, p).Take(8).ToList();
            if (top.Count == 0) return null;
            var text = $"Viel {rn.Label} (je 100 g) haben zum Beispiel:" + string.Concat(top.Select(e => $"\n• {e.Name}: {Num(e.Value, e.Value < 10 ? 1 : 0)} {rn.Unit}"));
            if (rn.Reference != null) text += "\n\n" + rn.Reference;
            return new Answer(text + "\n\nNährwerte: Bundeslebensmittelschlüssel (BLS 4.0), Max Rubner-Institut.", "food");
        }
        return null;
    }

    private static string Clean(string s) => Regex.Replace(s.Trim(), @"^(?:ein|eine|einer|einem|einen|der|die|das|dem|den|1)\s+", "", I).Trim();

    private static Food? Lookup(UserSpace space, CoachFoods foods, string name, DateOnly today)
    {
        var n = Clean(name);
        n = FoodText.Ingredient(n)?.Name is { Length: > 1 } parsed ? parsed : n;
        if (n.Length < 2) return null;
        return CoachSuggest.Match(space, foods, n, today, recipes: false).Best;
    }

    private static double? Value(Nutrient n, Food f)
    {
        if (n.Macro != null) return n.Macro(f.Per100);
        if (f.Id.StartsWith("bls:", StringComparison.Ordinal) && Bls.Get(f.Id[4..]) is { } e && e.Micro.TryGetValue(n.Micro!, out var v)) return v;
        return null;
    }

    private static string Source(params Food[] fs) =>
        fs.Any(f => f.Source == "bls") ? "\n\nNährwerte: Bundeslebensmittelschlüssel (BLS 4.0), Max Rubner-Institut." : "";

    /// <summary>Lebensmittel mit viel davon: nur Grundlebensmittel (keine Gerichte, Pulver, Extrakte, Nahrungsergänzung), je
    /// Lebensmittel nur eine Sorte; vegan/vegetarisch nach Profil.</summary>
    private static IEnumerable<(string Name, double Value)> Rich(Nutrient n, CoachProfile p)
    {
        var seen = new HashSet<string>();
        var list = Bls.Entries
            .Where(e => e.Code[0] is not ('X' or 'Y' or 'R') && !Regex.IsMatch(e.Name, @"pulver|extrakt|konzentrat|getrocknet|trocken|würze|brühwürfel|instant|angereichert|hefe|innereien|leber|niere|säuglings|nahrung", I))
            .Where(e => p.Diet != "vegan" || !(e.Code[0] is 'M' or 'T' or 'U' or 'V' or 'W' || e.Code.StartsWith("E1", StringComparison.Ordinal)))
            .Where(e => p.Diet != "vegetarian" || e.Code[0] is not ('T' or 'U' or 'V' or 'W'))
            .Select(e => (e.Name, Value: n.Macro != null ? n.Macro(e.Per100) ?? 0 : e.Micro.GetValueOrDefault(n.Micro!)))
            .Where(x => x.Value > 0)
            .OrderByDescending(x => x.Value);
        foreach (var x in list)
        {
            var first = Bls.Fold(Regex.Split(x.Name, @"[ ,(/]")[0]);
            if (seen.Add(first)) yield return x;
        }
    }
}
