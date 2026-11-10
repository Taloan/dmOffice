using System.Text.RegularExpressions;

namespace DmOffice.Chat;

/// <summary>
/// Feste Grenzen, bevor eine Frage an die KI geht: keine Aussagen zu eigenen Beschwerden, keine Beratung zu Medikamenten,
/// bei akuten Notfallzeichen nur der Notruf, bei Zeichen einer Essstörung behutsam der Weg zur Hilfe. Alles ohne KI, auf dem Server.
/// </summary>
public static partial class PukkiGuard
{
    /// <summary>Kind: refuse (Beschwerden, Medikamente), emergency, emergency-info, gentle (Essstörung).</summary>
    public sealed record Answer(string Text, string Kind);

    /// <summary>Frage zu Beschwerden („Was hilft gegen Kopfschmerzen?“) - freundlich ausweichen, nichts beurteilen.</summary>
    public const string Complaint = "Da bin ich leider der Falsche – zu Beschwerden möchte ich nichts sagen. Bitte frag eine Ärztin, einen Arzt " +
        "oder eine andere Fachkraft. Wenn du magst, trage ich dir einen Termin ein, zum Beispiel mit „Termin Hausarzt morgen 9 Uhr“.";
    /// <summary>Beschwerde erzählt („Ich habe Kopfschmerzen“): erst Mitgefühl, dann dasselbe.</summary>
    public const string ComplaintSaid = "Das tut mir leid – Gute Besserung! " + Complaint;
    public const string Medication = "Zu Medikamenten berate ich nicht – bitte frag deine Ärztin, deinen Arzt oder die Apotheke.";
    public const string Emergency = "Das klingt nach einem Notfall. Ruf bitte sofort den Notruf 112 an.";
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
                "Bitte sprich mit deiner Hausärztin, deinem Hausarzt oder einer Beratungsstelle für Essstörungen – dort hilft man dir, ohne zu urteilen. Du musst das nicht allein schaffen.", "gentle");
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
        return SymptomRx().IsMatch(t) && Regex.Split(t.ToLowerInvariant(), @"[^\p{L}\p{N}]+").Count(w => w.Length > 0) <= 4 ? new Answer(Complaint, "refuse") : null;
    }

    /// <summary>Wissensfrage („Was ist …“, „Wie viel …“, „Tipps …“, „Erklär mir …“) - nicht „Haben wir noch Zucker?“ oder Termine.</summary>
    [GeneratedRegex(@"^(?:und\s+|aber\s+|also\s+|sag mal,?\s+)?(was|wie|warum|wieso|weshalb|welche\w*|wo|soll|sollte|darf|kann|könnte|ist|sind|hilft|helfen|macht|stimmt|brauche|braucht|gibt es|gibt's|bringt|lohnt|worauf|wann)\b|\b(erklär\w*|tipps?|ratschl\w*|infos?|information\w*|was weißt du|was weisst du|erzähl\w*|kannst du mir|was kann ich|was soll ich|was tun|was hilft|wie schaffe ich|wie kann ich|woran liegt|warum|wieso|weshalb)\b", I)]
    private static partial Regex KnowledgeRx();
}
