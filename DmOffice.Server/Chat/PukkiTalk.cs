using System.Globalization;
using System.Text.RegularExpressions;
using DmOffice.Notes;

namespace DmOffice.Chat;

/// <summary>
/// Pukkis kleine Worte (ohne KI): Begrüßung, Befinden, wer er ist, Dank, Abschied, Gefühle, Witze, Uhrzeit, Datum,
/// Kalenderwoche, Feiertage, einfache Rechnungen - und was er nicht weiß (Wetter, Nachrichten, Lexikon), sagt er ehrlich.
/// Dazu: Ist ein Satz eine Frage (dann legt Pukki nie etwas an)? Ist er Unsinn?
/// </summary>
public static partial class Pukki
{
    private const RegexOptions I = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private const string Greeting = @"(?:hallo|hallöchen|hallihallo|hi|hey|huhu|moin moin|moinsen|moin|servus|servas|grüß dich|grüss dich|gruess dich|grüß gott|grüss gott|griaß di|grias di|grüezi|guten morgen|guten tag|guten abend|gutn morgen|tach|tag|hello|good morning|good evening|yo|na du|na|hallo zusammen|hey du|hi du)(?:\s+(?:du|pukki|lieber pukki))?";
    private static readonly Regex GreetOnly = new(@"^" + Greeting + @"(?:[\s!.?,]+" + Greeting + @")*[\s!.?,]*$", I);
    private static readonly Regex HowAreYou = new(@"what'?s up|wassup|was geht ab|wie geht'?s|wie gehts|wie geht es dir|wie geht es|alles (gut|klar|ok|okay|fit) bei dir|wie läuft'?s|wie laeuft'?s|wie läuft es|^was geht|how are you|how'?s it going|wie ist die lage|wie schaut'?s aus bei dir|geht'?s dir gut|geht es dir gut|bist du gut drauf|wie fühlst du dich|und dir\??$|und selbst\??$|und bei dir\??$", I);
    private static readonly Regex WhoAreYou = new(@"wer bist du|was bist du|who are you|stell dich (mal )?vor|wie hei(ß|ss)t du|wie ist dein name|hast du einen namen|what'?s your name|dein name\b|was ist pukki|wer ist pukki", I);
    private static readonly Regex WhatCanYouDo = new(@"was kannst du|what can you do|wobei kannst du (mir )?helfen|wie funktionierst du|was kann ich dich fragen|was kann ich (hier )?(machen|tun)|zeig mir,? was du kannst|^anleitung|^befehle|^kommandos|^funktionen|wof(ü|ue)r bist du|wozu bist du|kannst du mir helfen|^hilf mir|^ich brauche hilfe|^help me|^hilfe[\s!?.]*$|wie geht das hier|wie benutze ich dich|was machst du eigentlich", I);
    private static readonly Regex Creator = new(@"wer hat dich (gemacht|gebaut|programmiert|erfunden|erschaffen|entwickelt|geschrieben)|wer ist dein (chef|erfinder|entwickler|papa|vater|schöpfer|programmierer)|who made you|who built you|woher kommst du|wo kommst du her", I);
    private static readonly Regex AreYouAi = new(@"bist du (eine |ein |echt |wirklich )*(ki|k\.i\.|künstliche intelligenz|kuenstliche intelligenz|ai|bot|roboter|chatbot|mensch|computer|maschine|chatgpt|gpt|siri|alexa|programm|echt)\b|are you (an? )?(ai|bot|human|robot|real)|bist du echt|bist du lebendig", I);
    private static readonly Regex Clever = new(@"bist du (schlau|intelligent|klug|dumm|blöd|doof)\b", I);
    private static readonly Regex Age = new(@"wie alt bist du|how old are you|wann hast du geburtstag|wann bist du geboren", I);
    private static readonly Regex WhereAreYou = new(@"wo wohnst du|wo lebst du|wo bist du\b|where do you live|where are you", I);
    private static readonly Regex YourFeelings = new(@"hast du gef(ü|ue)hle|bist du (glücklich|traurig|einsam)\b|träumst du|kannst du (fühlen|lieben|träumen)", I);
    private static readonly Regex YourDay = new(@"was machst du (gerade|so|heute|eigentlich den ganzen tag)|schläfst du|bist du (müde|wach)\b|kannst du schlafen|langweilst du dich|hast du (heute |morgen |mal )?zeit|hast du feierabend|was treibst du", I);
    private static readonly Regex Yummy = new(@"^(mm+h*|hmm+|mjam+|njam+)?\s*(lecker|köstlich|yummy|mjam|njam)[\s!.]*$", I);
    private static readonly Regex WeatherSaid = new(@"^(es (regnet|schneit|hagelt|stürmt|stuermt|gewittert|nieselt|ist (so |total |echt )?(kalt|warm|heiß|heiss|schön|schoen|neblig|windig|glatt))|die sonne scheint|draußen ist es \w+|draussen ist es \w+|was für ein (schönes|schoenes|mieses|schreckliches) wetter|schönes wetter heute|mistwetter|sauwetter)[\s!.]*$", I);
    private static readonly Regex Oops = new(@"^(sorry|entschuldigung|entschuldige|sorry,? (vertippt|verschrieben|falsch getippt|mein fehler)|vertippt|verschrieben|tippfehler|hoppla|mein fehler)[\s!.]*$", I);
    private static readonly Regex Maybe = new(@"^(jein|vielleicht|mal sehen|mal schauen|schauen wir mal|mal gucken|weiß noch nicht|weiss noch nicht|ich überlege noch|ich überleg noch|kommt drauf an)[\s!.]*$", I);
    private static readonly Regex Love = new(@"ich (liebe|mag) dich|ich hab dich (lieb|gern)|magst du mich|liebst du mich|willst du mich heiraten|heirate mich|du bist (süß|suess|niedlich|knuffig)|bist du single|hast du (eine freundin|einen freund|eine frau|einen mann)|i love you", I);
    private static readonly Regex Compliment = new(@"du bist (so |echt |wirklich |voll |der |die |ein |eine |richtig )*(toll|super|klasse|spitze|beste|bester|cool|genial|großartig|grossartig|lieb|nett|schlau|klug|hilfreich|schatz|held|star|gut|witzig|lustig)|gut gemacht|toll gemacht|super gemacht|klasse gemacht|braver pukki|guter pukki|lieb von dir|nett von dir|well done|good job|good bot|guter bot|danke,? (das ist|du bist) (lieb|toll|super)|du hilfst mir sehr|ich mag dich|ich finde dich (so |echt |richtig )?(nett|toll|super|lieb|cool|klasse|gut|praktisch|hilfreich)", I);
    private static readonly Regex Insult = new(@"du bist (so |echt |wirklich |voll |total |ein |eine |richtig |so ein )*(doof|blöd|bloed|dumm|nutzlos|scheiße|scheisse|schlecht|langsam|unfähig|unfaehig|idiot|bescheuert|nervig|peinlich|zu nichts zu gebrauchen|lahm|kacke|mist|depp|trottel)|du nervst|halt die klappe|halt's maul|halts maul|^idiot|^dummkopf|^depp|^blödmann|blöder bot|dummer bot|scheiß bot|scheiss bot|^nutzlos|du kannst (ja )?nichts|you suck|^stupid|shut up|verpiss dich|du verstehst (ja )?(gar )?nichts|du bist keine hilfe", I);
    private static readonly Regex Frust = new(@"^(scheiße|scheisse|scheiß|mist|so ein mist|verdammt|verdammt nochmal|mann|menno|oh mann|ach mann|kacke|verflixt|shit|damn|fuck|grr+|argh+|aaah+|nervig|ärgerlich|ach menno)[\s!.]*$", I);
    private static readonly Regex Thanks = new(@"\b(danke|dankeschön|dank|merci|thx|thanks|thank you|vergelt'?s gott|cheers|danki|dankö)\b", I);
    private static readonly Regex Bye = new(@"^(tschüss|tschüs|tschuess|tschau|ciao|bye|bye bye|bis bald|bis später|bis spaeter|bis morgen|bis dann|bis nachher|bis gleich|bis die tage|auf wiedersehen|wiedersehen|adieu|ade|baba|pfiat di|pfiat eich|mach'?s gut|machs gut|ich geh jetzt|ich gehe jetzt|ich muss los|ich muss jetzt los|ich bin (dann )?weg|see you|cu|ciao kakao|tschö|tschüssi|servus und tschüss|man sieht sich|schönen abend( noch)?|schönen tag( noch)?|schönes wochenende)\b[\s!.,]*(pukki)?[\s!.]*$", I);
    private static readonly Regex Night = new(@"^(gute nacht|nacht|n8|schlaf gut|good night|ich geh(e)? (jetzt )?schlafen|ich geh(e)? (jetzt )?ins bett|ab ins bett|süße träume)\b[\s!.,]*(pukki)?[\s!.]*$", I);
    private static readonly Regex Tired = new(@"^(ich bin|bin) (so |total |echt |sehr |voll |ziemlich |richtig |mega |einfach |fix und |hunde)*(müde|muede|kaputt|erschöpft|erschoepft|fertig|platt|groggy|k\.o\.|ko)\b|^müde[\s!.]*$|^gähn", I);
    private static readonly Regex Bored = new(@"mir ist (so |total |echt |sehr |voll )*langweilig|ich langweile mich|^langweilig[\s!.]*$|^(mir ist )?öde", I);
    private static readonly Regex Sad = new(@"^(ich bin|bin) (so |total |echt |sehr |voll |ziemlich |einfach )*(traurig|einsam|unglücklich|ungluecklich|deprimiert|down|allein|niedergeschlagen)\b|mir geht('?s| es) (gar |echt |total |richtig |so )*(schlecht|nicht gut|mies|beschissen|dreckig)|ich fühle mich (so )?(schlecht|einsam|allein|traurig|leer)|ich hab(e)? (so )?(angst|sorgen|kummer|liebeskummer)|^ich will nicht mehr", I);
    private static readonly Regex Stress = new(@"^(ich bin|bin) (so |total |echt |sehr |voll |ziemlich )*(gestresst|im stress|überfordert|ueberfordert|genervt|gehetzt)\b|ich hab(e)? (so |echt |total )*viel zu tun|alles (ist )?zu viel|^stress[\s!.]*$|ich schaff(e)? das (alles )?nicht|ich weiß nicht,? wo mir der kopf steht", I);
    private static readonly Regex Sick = new(@"^(ich bin|bin) (leider |total |echt )*(krank|erkältet|erkaeltet|verschnupft)\b|ich fühle mich krank|ich hab(e)? (fieber|kopfschmerzen|kopfweh|schnupfen|husten|grippe|halsschmerzen|bauchschmerzen|zahnschmerzen|rückenschmerzen|eine erkältung|corona|migräne|migraene)|mir ist (so )?(schlecht|übel|uebel|schwindelig)", I);
    private static readonly Regex Hungry = new(@"^(ich hab(e)?|hab) (so |total |echt |voll |riesen|mega |einen )*(hunger|kohldampf)|^ich bin hungrig|^hunger[\s!.]*$|^was soll ich (heute )?(essen|kochen)\b|^was (koche|esse) ich (heute|morgen)?\s*\??$", I);
    private static readonly Regex Thirsty = new(@"^(ich hab(e)?|hab) (so |total |echt |voll )*durst|^ich bin durstig|^durst[\s!.]*$", I);
    private static readonly Regex Happy = new(@"^(ich bin|bin) (so |total |echt |sehr |voll |richtig |mega )*(glücklich|gluecklich|froh|happy|gut drauf|gut gelaunt|zufrieden|stolz)\b|^ich freu(e)? mich|^juhu|^yay|^hurra|^jippie|^endlich wochenende|^endlich feierabend|^feierabend[\s!.]*$|^geschafft[\s!.]*$", I);
    private static readonly Regex ColdWarm = new(@"^mir ist (so |total |echt )*(kalt|warm|heiß|heiss)\b", I);
    private static readonly Regex NeedRest = new(@"^(ich brauche|ich brauch|brauche) (eine |einen |mal |dringend )*(pause|urlaub|ruhe|schlaf|erholung|auszeit|kaffee|schokolade|umarmung)\b", I);
    private static readonly Regex JokeRx = new(@"(erzähl|erzähle|erzaehl|sag|kennst du|weißt du|weisst du|mach|hast du)( mir| uns)?( mal| noch| bitte| doch)? (einen|nen|n|ein|noch einen) (witz|joke|scherz)|^witz\b|^joke\b|tell me a joke|bring mich zum lachen|sag (mal )?(was|etwas) lustiges|^(noch )?ein witz|^lustig\b", I);
    private static readonly Regex Laugh = new(@"^(ha(ha)+h?|hah|hihi+|hehe+|lol+|lach|xd+|:d|:-d|rofl|lmao|haha+|hahah+|😂+|🤣+|😆+|😄+)[\s!.]*$", I);
    private static readonly Regex Hmm = new(@"^(hm+|hmm+|äh+|ähm+|öhm+|uff|puh|tja|naja|na ja|ach so|achso|aha+|ah|oh|oha|ok cool|interessant|krass|wow|echt|wirklich|ach was|soso|so so|okidoki|gut zu wissen|verstehe|verstanden|aha okay|na gut|nagut|na schön|egal|keine ahnung|keine ahnung,? was ich will|weiß nicht|weiss nicht|weiß ich nicht|k\.?a\.?|kp|ka)[\s!.?]*$", I);
    private static readonly Regex Huh = new(@"^(hä+|häh|hae|wie bitte|was\?+|bitte\?+|wie meinst du das|versteh(e)? ich nicht|ich versteh(e)? (dich )?nicht|ich verstehe nicht|what\?+|huh|sorry\?|hä\?)[\s!.?]*$", I);
    private static readonly Regex TestRx = new(@"^(test|testen|test test|test 123|test 1 2 3|testing|ping|bist du da|bist du wach|hallo\?+|hörst du mich|hoerst du mich|funktionierst du|geht das|klappt das|bist du online|lebst du noch)[\s?!.]*$", I);
    private static readonly Regex Fine = new(@"^((ja|jo|ok|okay)[\s,!.]+)+(ja|jo|ok|okay)[\s!.]*$|^(ja|jo|ok|okay|okey|oki|alles klar|klar|passt|gut|super|prima|toll|cool|perfekt|great|fine|top|klasse|wunderbar|fein|schön|nice|geil|sehr schön|sehr gut|läuft|jawohl|in ordnung|gerne|gern|alles klar dann|bestens|spitze|genial|ausgezeichnet|einverstanden|abgemacht|gebongt|geht klar|mach ich|wird gemacht|jep|jap|yes|sure|cool danke|ok danke|okay danke|top danke|super danke|alles gut|schon gut|passt schon)[\s!.]*$", I);
    private static readonly Regex MoodGood = new(@"^(mir geht'?s|mir geht es|es geht mir|mir gehts|bei mir|ich bin|auch|danke,?)?\s*(auch\s+)?(gut|super|prima|toll|bestens|blendend|klasse|spitze|fantastisch|großartig|wunderbar|sehr gut|ganz gut|gut gut|ausgezeichnet|nicht schlecht|alles gut|passt|okay|ok|fein|top)\b[\s,!.]*(danke|und dir|und selbst|und bei dir)?[\s?!.]*$", I);
    private static readonly Regex MoodBad = new(@"^(mir geht'?s|mir geht es|es geht mir|mir gehts|ich bin)?\s*(nicht so gut|nicht gut|schlecht|geht so|mies|beschissen|naja|so lala|so la la|mäßig|maessig|müde|traurig|gestresst|krank|durchwachsen|nicht so toll|könnte besser sein)\b[\s,!.]*(danke|und dir)?[\s?!.]*$", I);
    private static readonly Regex Weather = new(@"\b(wetter|regnet|regnen|regen|schneit|schnee|sonnig|scheint die sonne|gewitter|temperatur|grad (draußen|draussen|heute|morgen)|wie warm|wie kalt|wird es (heute |morgen )?(warm|kalt|heiß|heiss|regnen)|regenschirm|schirm mitnehmen|weather|forecast|is it raining|unwetter|glatteis|nebel|wind|stürmisch)\b", I);
    /// <summary>Bitten um Wissen oder Text, die nichts anlegen sollen: „Erklär mir …“, „Schreib mir ein Gedicht …“,
    /// „Gib mir Tipps …“, „Ich brauche ein Rezept für …“ - mit KI beantwortet die, sonst sagt Pukki ehrlich, dass er das nicht kann.</summary>
    /// <summary>Nach einem Rezept gefragt: „Ich brauche ein Rezept für Pizzateig“, „Hast du ein Rezept für Lasagne?“, „Wie backe ich Pizzateig?“</summary>
    private static readonly Regex RecipeAsk = new(@"\brezept(?:e)?\s+(?:für|fuer|zu|von|mit)\s+(?:einen\s+|eine\s+|ein\s+|einem\s+|einer\s+)?(?<dish>[^?!.]+)|^wie\s+(?:mache|koche|backe|kocht|backt|macht|bereite)\s+(?:ich|man)\s+(?:einen\s+|eine\s+|ein\s+)?(?<dish>[^?!.]+?)(?:\s+zu)?\s*[?!.]*$", I);

    private static readonly Regex AskFor = new(@"^(?:(?:bitte|kannst du( mir)?|könntest du( mir)?|koenntest du( mir)?)\s+)?(?:(?:erklär|erklaer|erkläre|erklaere)\w*\s+(?:mir|uns)\b|(?:(?:schreib|schreibe|verfass|verfasse)\s+(?:mir|uns)|(?:formulier|formuliere|dichte)(?:\s+(?:mir|uns))?)\s+(?:bitte\s+)?(?:ein|eine|einen)\s+(?:[\wäöüß-]+\s+){0,2}?[\wäöüß-]*(?:gedicht|text|brief|mail|geschichte|witz|lied|rede|einladung|glückwunsch|glueckwunsch|gruß|gruss|grüße|gruesse|absage|bewerbung|zusammenfassung|entschuldigung|nachricht|kündigung|kuendigung|reim|spruch|wünsche|wuensche)\w*\b|(?:gib|nenn|nenne|sag)\s+(?:mir|uns)\s+(?:mal\s+)?(?:ein paar\s+|einige\s+|\d+\s+)?(?:tipps?|ideen?|ratschläge|ratschlaege|rat|vorschläge|vorschlaege|beispiele|gründe|gruende|ein rezept|einen tipp|eine idee|eine anleitung)\b|(?:ich brauche|ich bräuchte|ich braeuchte|ich suche|such mir|hast du|kennst du)\s+(?:mal\s+)?(?:ein|eine|einen|ein paar)?\s*(?:rezept|anleitung|tipps?|ideen?|vorschlag|vorschläge|gedicht|witz|spruch)\b|(?:erzähl|erzaehl|erzähle|erzaehle)\s+(?:mir|uns)\s+(?:einen witz|eine geschichte|etwas|was)\b|(?:fass|fasse)\s+.+\s+zusammen\b|(?:übersetz|uebersetz)\w*\b)", I);

    private static readonly Regex Outside = new(@"\b(nachrichten|news|schlagzeilen|fußballergebnis|fussballergebnis|bundesliga|lottozahlen|börse|boerse|aktienkurs|aktien|bitcoin|dax|google|internet|wikipedia|youtube|spotify|netflix|radio|fernsehprogramm|tv-programm|spiel (mal )?musik|spiele musik|play music|musik (an|abspielen|spielen)|licht an|licht aus|mach das licht|heizung|staubsaugerroboter|wecker stellen|stell (einen |mir einen )?wecker|timer stellen|stell (einen |mir einen )?timer|übersetze|uebersetze|übersetz|navi|navigation|route nach|wie komme ich nach|verkehr|stau)\b", I);
    private static readonly Regex TimeRx = new(@"wie sp(ä|ae)t|wie ?viel uhr|uhrzeit\b|what time|wie spät ist'?s|sag mir die zeit|zeit ist es|haben wir (es )?(gerade|jetzt)\b.*uhr", I);
    private static readonly Regex DateRx = new(@"welcher tag ist (heute|morgen|übermorgen)|welches datum|der wievielte|den wievielten|welchen tag haben wir|welcher wochentag|was (ist|haben wir) heute für ein tag|was für ein tag ist heute|^datum\??$|heutiges datum|today'?s date|what day is (it|today)|welches datum ist|welcher tag ist morgen|was ist heute für ein datum|welchen haben wir heute", I);
    private static readonly Regex WeekRx = new(@"\bkw\b|kalenderwoche|welche woche (ist|haben wir)|in welcher woche|week number|welche wochennummer", I);
    private static readonly Regex YearRx = new(@"welches jahr|what year|in welchem jahr (sind|leben) wir", I);
    private static readonly Regex MeaningRx = new(@"sinn des lebens|meaning of life|^42$|antwort auf alle fragen", I);
    private static readonly Regex WieSchoen = new(@"^(wie|so) (schön|schoen|toll|cool|lustig|süß|suess|nett|lieb|praktisch|super|klasse)[\s!.]*$", I);
    private static readonly Regex MyNameRx = new(@"^(ich hei(ß|ss)e|mein name ist|nenn mich|call me|ich bin der|ich bin die)\s+(?<n>\p{L}[\p{L}\-]*)[\s!.]*$", I);
    private static readonly Regex WhoAmI = new(@"wie hei(ß|ss)e ich|wer bin ich|kennst du mich|weißt du,? wer ich bin|weisst du,? wer ich bin|wie ist mein name", I);
    private static readonly Regex English = new(@"sprichst du englisch|kannst du englisch|do you speak english|speak english|english please|in english|auf englisch", I);
    private static readonly Regex CanYou = new(@"^(kannst|könntest|koenntest|can|could)\s+(du|you)\s+(auch\s+|eigentlich\s+|denn\s+|überhaupt\s+)*(?<what>.+?)[\s?!.]*$", I);
    private static readonly Regex MathRx = new(@"^(?:was ist|was sind|wie ?viel ist|wieviel sind|wie viel sind|was ergibt|was ergeben|was gibt|rechne|berechne|what is|what'?s|calculate)?\s*(?<a>-?\d+(?:[.,]\d+)?)\s*(?<op>\+|-|−|\*|x|×|mal|plus|minus|geteilt durch|durch|/|:|hoch|times|divided by)\s*(?<b>-?\d+(?:[.,]\d+)?)\s*(?:=\s*)?[?!.]*\s*$", I);
    private static readonly Regex PercentRx = new(@"^(?:was sind|was ist|wie ?viel sind|wieviel sind|rechne)?\s*(?<p>\d+(?:[.,]\d+)?)\s*(?:%|prozent)\s+(?:von|aus)\s+(?<b>\d+(?:[.,]\d+)?)\s*\??$", I);
    private static readonly Regex SqrtRx = new(@"^(?:was ist (?:die )?)?(?:wurzel|quadratwurzel) (?:aus|von) (?<a>\d+(?:[.,]\d+)?)\s*\??$", I);
    private static readonly Regex Holiday = new(@"\b(weihnachten|heiligabend|heilig abend|silvester|neujahr|ostern|ostersonntag|ostermontag|karfreitag|pfingsten|pfingstmontag|himmelfahrt|christi himmelfahrt|vatertag|muttertag|nikolaus|nikolaustag|halloween|valentinstag|fasching|karneval|rosenmontag|fronleichnam|tag der deutschen einheit|tag der arbeit|erster mai|1\. mai|allerheiligen|reformationstag|nationalfeiertag|advent|erster advent|1\. advent)\b", I);
    private static readonly Regex CountdownRx = new(@"^(?:wie ?(?:lange|lang|viele tage|viel tage)|in wie vielen tagen|wieviele tage)\s+(?:ist es\s+|sind es\s+|noch\s+|dauert es\s+|haben wir\s+)*(?:noch\s+)?(?:bis\s+(?:zum\s+|zur\s+|zu\s+|an\s+)?|ist\s+)?(?<what>.+?)[\s?!.]*$", I);
    private static readonly Regex FeiertagRx = new(@"(ist|wäre) (heute|morgen|übermorgen|am \p{L}+) (ein |kein )?feiertag|wann ist (der )?nächste(r)? feiertag|nächste(r)? feiertag|welche feiertage", I);

    private static readonly string[] Jokes =
    [
        "Was macht ein Pirat am Computer? Er drückt die Enter-Taste. 😄",
        "Treffen sich zwei Kalender. Sagt der eine: „Meine Tage sind gezählt.“ 😄",
        "Warum können Geister so schlecht lügen? Weil man durch sie hindurchsieht. 😄",
        "Was ist orange und läuft durch den Wald? Eine Wanderine. 😄",
        "Was sagt die Null zur Acht? „Schicker Gürtel!“ 😄",
        "Was ist grün und klopft an die Tür? Ein Klopfsalat. 😄",
        "Ich wollte einen Witz über die Einkaufsliste erzählen – aber der stand nicht drauf. 😄",
        "Warum nehmen Bienen nie Termine wahr? Sie haben schon genug zu summen. 😄",
    ];

    private static Reply Joke(Memory mem)
    {
        mem.Topic = "joke";
        return Jokes[mem.Jokes++ % Jokes.Length];
    }

    /// <summary>Kleine Worte - null: keine.</summary>
    private static Reply? Talk(Ctx c, string t)
    {
        var mem = c.Mem;
        var words = Tokens(t).Count;
        if (GreetOnly.IsMatch(t))
            return Greet(t, c.Now) + "! Was kann ich für dich tun? Schreib einfach, was ansteht – zum Beispiel „morgen 15 Uhr Zahnarzt“ oder „Milch und Brot“.";
        if (Night.IsMatch(t)) return "Gute Nacht! Schlaf gut – ich passe auf deine Termine auf.";
        if (Bye.IsMatch(t)) return "Bis bald! Ich bin da, wenn du mich brauchst.";
        if (mem.Topic == "mood" && MoodGood.IsMatch(t)) { mem.Topic = null; return "Das freut mich! 😊" + (Regex.IsMatch(t, @"und (dir|selbst|bei dir)", I) ? " Mir geht's auch gut, danke!" : ""); }
        if (mem.Topic == "mood" && MoodBad.IsMatch(t)) { mem.Topic = null; return "Oh, das tut mir leid. Wenn ich dir etwas abnehmen kann – Termine, Aufgaben oder den Einkauf –, schreib es mir einfach."; }
        if (HowAreYou.IsMatch(t) && words <= 8)
        {
            mem.Topic = "mood";
            return t.Contains("und dir", StringComparison.OrdinalIgnoreCase) || t.Contains("und selbst", StringComparison.OrdinalIgnoreCase)
                ? "Mir geht's gut, danke der Nachfrage! Ich passe auf deine Termine auf."
                : "Mir geht's gut, danke! Ich passe auf deine Termine auf. Und dir?";
        }
        if (WhatCanYouDo.IsMatch(t)) return Help;
        if (WhoAreYou.IsMatch(t))
            return "Ich bin Pukki, dein Assistent in dmOffice. Ich trage Termine ein, notiere Aufgaben und Notizen, führe die Einkaufsliste und schaue für dich nach, was ansteht. Was ich alles kann, zeige ich dir, wenn du „Hilfe“ schreibst.";
        if (Creator.IsMatch(t)) return "Ich wurde für dmOffice gebaut – ganz ohne KI, nur mit Regeln und einem Wortschatz. Alles, was du mir schreibst, bleibt bei dir auf dem Server.";
        if (AreYouAi.IsMatch(t)) return "Nein, ich bin keine KI – und auch kein Mensch. Ich verstehe einfache Sätze mit festen Regeln und einem Wortschatz. Dafür bleibt alles bei dir auf dem Server.";
        if (Clever.IsMatch(t)) return "Schlau bin ich nicht besonders – aber fleißig. Ich verstehe einfache Sätze und lerne immer mehr dazu.";
        if (Age.IsMatch(t)) return "Ich bin noch ganz jung – und lerne jeden Tag dazu.";
        if (WhereAreYou.IsMatch(t)) return "Ich wohne hier auf deinem dmOffice-Server – ganz in der Nähe deiner Termine.";
        if (YourFeelings.IsMatch(t)) return "Gefühle habe ich keine – aber wenn ich dir helfen kann, ist für mich ein guter Tag.";
        if (YourDay.IsMatch(t)) return "Ich bin rund um die Uhr für dich da und passe auf deine Termine auf. 🙂";
        if (Yummy.IsMatch(t)) return "Guten Appetit! 😊";
        if (Maybe.IsMatch(t)) return "Kein Problem – lass dir Zeit. Schreib mir einfach, wenn du weißt, was ich tun soll.";
        if (WeatherSaid.IsMatch(t)) return "Danke für den Wetterbericht! 🙂 Selbst nachschauen kann ich das Wetter leider nicht.";
        if (Oops.IsMatch(t)) return "Kein Problem! 🙂";
        if (Love.IsMatch(t)) return "Das ist lieb von dir! Ich mag dich auch – und kümmere mich gern um deine Termine.";
        if (Insult.IsMatch(t)) return "Schade, dass ich dir gerade nicht helfen konnte. Ich lerne noch – sag mir ruhig noch einmal, was du brauchst, gern mit anderen Worten.";
        if (Compliment.IsMatch(t)) return "Danke, das freut mich!";
        if (Frust.IsMatch(t)) return "Oh je! Kann ich dir irgendwie helfen?";
        if (Thanks.IsMatch(t) && words <= 5) return "Gern geschehen!";
        if (MyNameRx.Match(t) is { Success: true } my && !Regex.IsMatch(my.Groups["n"].Value, @"^(müde|traurig|krank|fertig|da|weg|hier|zuhause|gestresst|glücklich|froh)$", I))
            return $"Freut mich, {Cap(my.Groups["n"].Value)}! Ich bin Pukki.";
        if (WhoAmI.IsMatch(t)) return $"Du bist {c.Space.User.DisplayName}. 🙂";
        if (Tired.IsMatch(t)) return "Dann gönn dir eine Pause – ich passe solange auf deine Termine auf.";
        if (Bored.IsMatch(t)) return "Wie wäre es mit einem Spaziergang, einem Buch oder einem Anruf bei jemandem, den du magst? Oder wir räumen zusammen auf – frag mich „Welche Aufgaben sind offen?“.";
        if (Sad.IsMatch(t)) return "Das tut mir leid. Magst du mit jemandem reden, der dir nahesteht? Wenn es dir sehr schlecht geht, hilft auch die Telefonseelsorge – rund um die Uhr in Deutschland unter 0800 111 0 111, in Österreich unter 142.";
        if (Stress.IsMatch(t)) return "Atme einmal tief durch. Schreib mir, was ansteht – ich notiere es, dann geht eins nach dem anderen.";
        if (Sick.IsMatch(t)) return "Gute Besserung! Ruh dich aus. Wenn du wissen willst, welche Termine du absagen musst, frag mich „Was steht heute an?“.";
        if (Hungry.IsMatch(t))
        {
            var meal = MealAnswer(c, Regex.IsMatch(t, @"morgen", I) ? "was gibt es morgen zu essen?" : "was gibt es heute zu essen?");
            return Regex.IsMatch(t, @"hunger|hungrig|kohldampf", I) ? meal with { Text = "Guten Appetit! " + meal.Text } : meal;
        }
        if (Thirsty.IsMatch(t)) return "Dann trink ein großes Glas Wasser! 💧";
        if (Happy.IsMatch(t)) return "Das freut mich für dich! 😊";
        if (ColdWarm.Match(t) is { Success: true } cw)
            return cw.Groups[2].Value.ToLowerInvariant() == "kalt" ? "Oh! Dagegen hilft ein warmer Tee oder eine Jacke." : "Dann trink genug Wasser! 💧";
        if (NeedRest.Match(t) is { Success: true } nr)
            return nr.Groups[3].Value.ToLowerInvariant() switch
            {
                "kaffee" => "Dann gönn dir einen! ☕ Soll er auf die Einkaufsliste? Schreib einfach „Kaffee“.",
                "schokolade" => "Verstehe ich gut! 🍫 Soll sie auf die Einkaufsliste? Schreib einfach „Schokolade“.",
                "umarmung" => "Eine Umarmung kann ich dir leider nicht geben – aber ich wünsche dir von Herzen eine! 🤗",
                "urlaub" => "Den hast du dir bestimmt verdient! Wenn du ihn planst, trage ich ihn gern ein – zum Beispiel „Urlaub vom 1.8. bis 14.8.“.",
                _ => "Dann gönn dir eine – ich passe solange auf alles auf.",
            };
        if (JokeRx.IsMatch(t)) return Joke(mem);
        if (Laugh.IsMatch(t)) return "😄";
        if (Huh.IsMatch(t)) return "Entschuldige, das war wohl nicht klar. Frag mich zum Beispiel „Was steht heute an?“ oder schreib „Hilfe“.";
        if (Hmm.Match(t) is { Success: true } hm)
        {
            var v = hm.Groups[1].Value.ToLowerInvariant();
            if (v.StartsWith("keine ahnung") || v is "egal" or "weiß nicht" or "weiss nicht" or "weiß ich nicht" or "kp" or "ka" or "k.a.") return "Kein Problem – schreib mir einfach, wenn dir etwas einfällt.";
            if (v.StartsWith("hm") || v.StartsWith("äh") || v.StartsWith("öh") || v is "tja" or "naja" or "na ja") return "Lass dir ruhig Zeit. 🙂";
            if (v is "wow" or "krass" or "interessant") return "😊";
            if (v is "echt" or "wirklich") return "Ja, wirklich! 🙂";
            return "👍";
        }
        if (TestRx.Match(t) is { Success: true } test) return test.Value.Trim().StartsWith("ping", StringComparison.OrdinalIgnoreCase) ? "Pong! 🏓" : "Ich bin da und höre zu! 👋";
        if (mem.Topic == "mood" && MoodGood.IsMatch(t)) return "Das freut mich! 😊";
        if (Fine.IsMatch(t)) return "👍";
        if (Calc(t) is { } calc) return calc;
        if (MeaningRx.IsMatch(t)) return "42 – sagt man. Ich würde sagen: gute Zeit mit den Menschen, die dir wichtig sind.";
        if (OnlySymbols(t) is { } symbols) return symbols;
        if (WieSchoen.IsMatch(t)) return "Finde ich auch! 😊";

        // Uhrzeit, Datum, Kalenderwoche, Jahr, Feiertage
        if (TimeRx.IsMatch(t)) return $"Es ist {c.Now:HH:mm} Uhr.";
        if (DateRx.IsMatch(t))
        {
            var day = Regex.IsMatch(t, @"übermorgen|uebermorgen", I) ? c.Today.AddDays(2) : Regex.IsMatch(t, @"\bmorgen\b", I) ? c.Today.AddDays(1) : c.Today;
            var which = day == c.Today ? "Heute" : day == c.Today.AddDays(1) ? "Morgen" : "Übermorgen";
            return $"{which} ist {day.ToString("dddd", De)}, der {day.ToString("d. MMMM yyyy", De)}.";
        }
        if (WeekRx.IsMatch(t))
        {
            var next = Regex.IsMatch(t, @"nächste|naechste|kommende", I);
            var day = next ? c.Today.AddDays(7) : c.Today;
            return $"{(next ? "Nächste Woche ist" : "Wir haben")} Kalenderwoche {ISOWeek.GetWeekOfYear(day.ToDateTime(TimeOnly.MinValue))}.";
        }
        if (YearRx.IsMatch(t)) return $"Wir haben das Jahr {c.Today.Year}.";
        if (FeiertagRx.IsMatch(t)) return HolidayAnswer(c, t);
        if (CountdownRx.Match(t) is { Success: true } cd && Countdown(c, cd.Groups["what"].Value) is { } count) return count;
        if (Holiday.IsMatch(t) && Regex.IsMatch(t, @"^(wann|wie|welche|an welchem|welcher|ist|in wie)", I)) return Countdown(c, Holiday.Match(t).Value) ?? null;

        // Was Pukki nicht weiß oder nicht kann - ehrlich; mit KI fragt der Dienst stattdessen dort nach (Unknown)
        if (Weather.IsMatch(t))
        {
            mem.Why = () => "Weil ich nicht ins Internet schaue – ich weiß nur, was in deinem dmOffice steht.";
            return new Reply("Das Wetter kenne ich leider nicht – ich schaue nicht ins Internet. Ein Blick aus dem Fenster oder in eine Wetter-App hilft dir da besser.", Unknown: true);
        }
        if (Outside.IsMatch(t))
        {
            mem.Why = () => "Weil ich nicht ins Internet schaue und keine Geräte steuere – ich kümmere mich nur um das, was in deinem dmOffice steht.";
            return new Reply("Das kann ich leider nicht – ich schaue nicht ins Internet und steuere keine Geräte. Ich kümmere mich um deine Termine, Aufgaben, Notizen, die Einkaufsliste und den Essensplan.", Unknown: true);
        }
        // Rezepte schaut Pukki zuerst in den eigenen Notizen nach; nichts gefunden: mit KI fragt der Dienst dort
        if (RecipeAsk.Match(t) is { Success: true } ra && OwnRecipes(c, ra.Groups["dish"].Value.Trim()) is { } own) return own;
        if (AskFor.IsMatch(t))
            return new Reply("Dabei kann ich dir leider nicht helfen – ich kümmere mich um deine Termine, Aufgaben, Notizen, die Einkaufsliste und den Essensplan.", Unknown: true);
        if (English.IsMatch(t)) return "Ein paar Sätze verstehe ich auch auf Englisch, zum Beispiel „tomorrow 3pm dentist“ – antworten tue ich auf Deutsch.";
        return null;
    }

    /// <summary>Rezepte aus den Notizen zum Gericht: Titel enthält das Gericht oder umgekehrt („Pizza“ zu „Pizzateig“),
    /// sonst steht es im Text. Null: keins.</summary>
    private static Reply? OwnRecipes(Ctx c, string dish)
    {
        if (dish.Length < 3) return null;
        var db = NotesDb.Of(c.Space);
        var all = Recipes.All(db, db.AllFolders());
        bool Has(string a, string b) => a.Contains(b, StringComparison.CurrentCultureIgnoreCase);
        var found = all.Where(r => Has(r.Title, dish)).ToList();
        if (found.Count == 0) found = all.Where(r => r.Title.Trim().Length >= 4 && Has(dish, r.Title.Trim())).ToList();
        if (found.Count == 0) found = all.Where(r => db.GetNote(r.Id) is { } n && Has(n.Plain, dish)).ToList();
        if (found.Count == 0) return null;
        var refs = new List<ChatRef>();
        foreach (var r in found.Take(5))
            try { refs.Add(ChatApi.RefFor(c.Space, ChatRef.TypeNote, noteId: r.Id)); } catch (KeyNotFoundException) { }
        var text = found.Count == 1 ? $"In deinen Notizen habe ich ein Rezept: „{found[0].Title}“."
            : $"In deinen Notizen habe ich {found.Count} Rezepte:" + string.Concat(found.Take(5).Select(r => "\n📖 " + r.Title)) + (found.Count > 5 ? "\n…" : "");
        return new Reply(text, refs);
    }

    /// <summary>Nur Zeichen ohne Buchstaben: Emoji bekommen ein Emoji zurück, Satzzeichen und Ziffern ein freundliches Nicht-Verstehen.</summary>
    private static Reply? OnlySymbols(string t)
    {
        if (t.Any(char.IsLetter) || Phone(t)) return null;
        if (t.Contains("👍") || t.Contains("👌")) return "👍";
        if (t.Contains('❤') || t.Contains('♥') || t.Contains("😍") || t.Contains("🥰") || t.Contains("😘")) return "❤️";
        if (t.Contains("😂") || t.Contains("🤣") || t.Contains("😄") || t.Contains("😆") || t.Contains("😀") || t.Contains("😁")) return "😄";
        if (t.Contains("😢") || t.Contains("😭") || t.Contains("☹") || t.Contains("🙁") || t.Contains("😞") || t.Contains("😔")) return "Oh, was ist los? Kann ich dir helfen?";
        if (t.Contains("😡") || t.Contains("😠") || t.Contains("🤬")) return "Oh je! Kann ich dir irgendwie helfen?";
        if (t.Contains("👋")) return "👋 Hallo! Was kann ich für dich tun?";
        if (t.Any(ch => char.IsSurrogate(ch) || ch > 0x2000 && !char.IsPunctuation(ch))) return "🙂";
        if (t.Any(char.IsDigit)) return $"Mit „{t.Trim()}“ kann ich leider nichts anfangen. Schreib mir zum Beispiel „morgen 15 Uhr Zahnarzt“ oder frag „Was steht heute an?“.";
        return "🙂 Schreib mir einfach, was ansteht – oder „Hilfe“, dann zeige ich dir, was ich kann.";
    }

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>„Was ist 7 mal 8?“, „20 Prozent von 50“, „Wurzel aus 16“.</summary>
    private static Reply? Calc(string t)
    {
        static double N(string s) => double.Parse(s.Replace(',', '.'), Inv);
        static string F(double v) => Math.Abs(v - Math.Round(v)) < 1e-9 ? Math.Round(v).ToString("N0", De) : v.ToString("0.####", De);
        var p = PercentRx.Match(t);
        if (p.Success) return $"{F(N(p.Groups["p"].Value))} % von {F(N(p.Groups["b"].Value))} sind {F(N(p.Groups["p"].Value) * N(p.Groups["b"].Value) / 100)}.";
        var r = SqrtRx.Match(t);
        if (r.Success) return $"Die Wurzel aus {F(N(r.Groups["a"].Value))} ist {F(Math.Sqrt(N(r.Groups["a"].Value)))}.";
        var m = MathRx.Match(t);
        if (!m.Success) return null;
        // „14-16“ ohne Frage ist eher ein Zeitraum - nur mit Rechenwort oder Leerzeichen um das Zeichen rechnen
        if (!Regex.IsMatch(t, @"^\s*(was|wie|rechne|berechne|what|calculate)", I) && !Regex.IsMatch(m.Groups["op"].Value, @"\p{L}") && !t.Contains('?') && !Regex.IsMatch(t, @"\d\s+[+\-−*x×/:]\s+\d")) return null;
        var a = N(m.Groups["a"].Value);
        var b = N(m.Groups["b"].Value);
        var op = m.Groups["op"].Value.ToLowerInvariant();
        double result;
        string sign;
        switch (op)
        {
            case "+" or "plus": result = a + b; sign = "+"; break;
            case "-" or "−" or "minus": result = a - b; sign = "−"; break;
            case "*" or "x" or "×" or "mal" or "times": result = a * b; sign = "·"; break;
            case "hoch": result = Math.Pow(a, b); sign = "^"; break;
            default:
                if (b == 0) return "Durch null kann man nicht teilen – das kann nicht einmal ich. 🙂";
                result = a / b; sign = ":"; break;
        }
        if (double.IsInfinity(result) || double.IsNaN(result)) return "Das ist mir zu groß zum Rechnen.";
        return $"{F(a)} {sign} {F(b)} = {F(result)}";
    }

    /// <summary>Ostersonntag (Gaußsche Osterformel, gregorianisch).</summary>
    private static DateOnly Easter(int year)
    {
        int a = year % 19, b = year / 100, c = year % 100, d = b / 4, e = b % 4, f = (b + 8) / 25, g = (b - f + 1) / 3;
        int h = (19 * a + b - d - g + 15) % 30, i = c / 4, k = c % 4, l = (32 + 2 * e + 2 * i - h - k) % 7, m = (a + 11 * h + 22 * l) / 451;
        int month = (h + l - 7 * m + 114) / 31, day = ((h + l - 7 * m + 114) % 31) + 1;
        return new DateOnly(year, month, day);
    }

    /// <summary>Feste und bewegliche Tage eines Jahres (Name, Tag, bundesweiter Feiertag in Deutschland).</summary>
    private static List<(string Name, DateOnly Day, bool Holiday)> Days(int year)
    {
        var easter = Easter(year);
        var may1 = new DateOnly(year, 5, 1);
        var mother = may1.AddDays((7 - (int)may1.DayOfWeek) % 7 + 7);
        var christmas = new DateOnly(year, 12, 25);
        var advent1 = christmas.AddDays(-((int)christmas.DayOfWeek == 0 ? 7 : (int)christmas.DayOfWeek) - 21);
        return
        [
            ("Neujahr", new DateOnly(year, 1, 1), true), ("Heilige Drei Könige", new DateOnly(year, 1, 6), false), ("Valentinstag", new DateOnly(year, 2, 14), false),
            ("Rosenmontag", easter.AddDays(-48), false), ("Karfreitag", easter.AddDays(-2), true), ("Ostersonntag", easter, false), ("Ostermontag", easter.AddDays(1), true),
            ("Tag der Arbeit", may1, true), ("Muttertag", mother, false), ("Christi Himmelfahrt", easter.AddDays(39), true), ("Pfingstsonntag", easter.AddDays(49), false),
            ("Pfingstmontag", easter.AddDays(50), true), ("Fronleichnam", easter.AddDays(60), false), ("Tag der Deutschen Einheit", new DateOnly(year, 10, 3), true),
            ("Nationalfeiertag in Österreich", new DateOnly(year, 10, 26), false), ("Halloween", new DateOnly(year, 10, 31), false), ("Reformationstag", new DateOnly(year, 10, 31), false),
            ("Allerheiligen", new DateOnly(year, 11, 1), false), ("Erster Advent", advent1, false), ("Nikolaus", new DateOnly(year, 12, 6), false), ("Heiligabend", new DateOnly(year, 12, 24), false),
            ("1. Weihnachtstag", christmas, true), ("2. Weihnachtstag", new DateOnly(year, 12, 26), true), ("Silvester", new DateOnly(year, 12, 31), false),
        ];
    }

    /// <summary>Welcher Tag ist mit dem Wort gemeint? (das nächste Vorkommen ab heute)</summary>
    private static (string Name, DateOnly Day)? NamedDay(DateOnly today, string word)
    {
        var w = word.ToLowerInvariant().Trim();
        string? name = w switch
        {
            "weihnachten" or "heiligabend" or "heilig abend" => "Heiligabend",
            "silvester" => "Silvester", "neujahr" => "Neujahr", "ostern" or "ostersonntag" => "Ostersonntag", "ostermontag" => "Ostermontag",
            "karfreitag" => "Karfreitag", "pfingsten" => "Pfingstsonntag", "pfingstmontag" => "Pfingstmontag",
            "himmelfahrt" or "christi himmelfahrt" or "vatertag" => "Christi Himmelfahrt", "muttertag" => "Muttertag",
            "nikolaus" or "nikolaustag" => "Nikolaus", "halloween" => "Halloween", "valentinstag" => "Valentinstag",
            "fasching" or "karneval" or "rosenmontag" => "Rosenmontag", "fronleichnam" => "Fronleichnam",
            "tag der deutschen einheit" => "Tag der Deutschen Einheit", "tag der arbeit" or "erster mai" or "1. mai" => "Tag der Arbeit",
            "allerheiligen" => "Allerheiligen", "reformationstag" => "Reformationstag", "nationalfeiertag" => "Nationalfeiertag in Österreich",
            "advent" or "erster advent" or "1. advent" => "Erster Advent",
            _ => null,
        };
        if (name == null) return null;
        var hit = Days(today.Year).Concat(Days(today.Year + 1)).First(d => d.Name == name && d.Day >= today);
        return (hit.Name, hit.Day);
    }

    /// <summary>„am Donnerstag, 24. Dezember“ (anderes Jahr mit Jahr), nahe Tage „morgen, Freitag, 23. Oktober“.</summary>
    private static string LongDay(DateOnly day, DateOnly today)
    {
        var date = day.ToString(day.Year == today.Year ? "dddd, d. MMMM" : "dddd, d. MMMM yyyy", De);
        return Math.Abs(day.DayNumber - today.DayNumber) <= 2 ? PukkiParse.DayText(day, today) + ", " + date : "am " + date;
    }

    private static string Until(DateOnly day, DateOnly today) => (day.DayNumber - today.DayNumber) switch
    {
        0 => "– das ist heute!", 1 => "– das ist schon morgen.", var n => $"– noch {n} Tage.",
    };

    /// <summary>„Wie lange noch bis Weihnachten?“, „Wann ist Ostern?“, „Wie viele Tage bis zum 24.12.?“</summary>
    private static Reply? Countdown(Ctx c, string what)
    {
        what = Regex.Replace(what.Trim(), @"^(?:zum|zur|zu|bis|an|ist|dem|der|die|das)\s+", "", I).Trim();
        if (NamedDay(c.Today, what) is { } named)
            return $"{named.Name} ist {LongDay(named.Day, c.Today)} {Until(named.Day, c.Today)}";
        if (Regex.IsMatch(what, @"^(?:zum\s+)?(?:wochenende|weekend)$", I))
        {
            var sat = c.Today.AddDays(((int)DayOfWeek.Saturday - (int)c.Today.DayOfWeek + 7) % 7);
            return c.Today.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? "Es ist schon Wochenende! 🎉" : $"Bis zum Wochenende sind es noch {sat.DayNumber - c.Today.DayNumber} Tage.";
        }
        if (Regex.IsMatch(what, @"^(?:zum\s+|zur\s+)?(?:feierabend)$", I)) return "Wann dein Feierabend ist, weiß ich leider nicht – aber ich drücke dir die Daumen, dass er bald kommt!";
        // „bis zum 24.12“ - der Schlusspunkt fehlt oft
        var w = PukkiParse.Parse(Regex.IsMatch(what, @"^\d{1,2}\.\d{1,2}$") ? what + "." : what, c.Now);
        if (w.Date is { } d && PukkiWords.Words(w.Title).Count == 0)
        {
            var n = d.DayNumber - c.Today.DayNumber;
            return n switch
            {
                < 0 => $"Der {d.ToString("d. MMMM yyyy", De)} ist schon vorbei.",
                0 => "Das ist heute!",
                1 => "Das ist schon morgen.",
                _ => $"Bis {PukkiParse.DayText(d, c.Today)} sind es noch {n} Tage.",
            };
        }
        return null;
    }

    /// <summary>„Ist heute ein Feiertag?“, „Wann ist der nächste Feiertag?“ - bundesweit in Deutschland.</summary>
    private static Reply HolidayAnswer(Ctx c, string t)
    {
        var all = Days(c.Today.Year).Concat(Days(c.Today.Year + 1)).Where(d => d.Holiday).ToList();
        var w = PukkiParse.Parse(t, c.Now);
        if (Regex.IsMatch(t, @"nächste|naechste|welche", I))
        {
            var next = all.First(d => d.Day >= c.Today);
            return $"Der nächste bundesweite Feiertag: {next.Name} {LongDay(next.Day, c.Today)}. Regionale Feiertage kenne ich nicht alle.";
        }
        var day = w.Date ?? c.Today;
        var hit = all.FirstOrDefault(d => d.Day == day);
        var which = Cap(PukkiParse.OnDay(day, c.Today));
        return hit.Name != null ? $"Ja, {PukkiParse.OnDay(day, c.Today)} ist {hit.Name}." : $"{which} ist kein bundesweiter Feiertag. Regionale Feiertage kenne ich nicht alle.";
    }

    // ---------------- Fragen und Unsinn ----------------

    private static readonly Regex QWord = new(@"^(?:und\s+|aber\s+|also\s+|sag mal,?\s+)?(wer|wen|wem|wessen|was|wann|wo|wohin|woher|wie|warum|wieso|weshalb|weswegen|welche[rsmn]?|wieviel|wieviele|wie viel|wie viele|womit|wofür|wofuer|worüber|worueber|wozu|woran|worauf|who|what|when|where|why|how|which)\b", I);
    private static readonly Regex VerbFirst = new(@"^(?:und\s+|aber\s+|also\s+)?(ist|sind|bin ich|bist du|hast du|hab ich|habe ich|haben wir|habt ihr|hat|gibt es|gibt's|gibts|kannst du|kann ich|könntest du|koenntest du|weißt du|weisst du|kennst du|magst du|darf ich|muss ich|müssen wir|muessen wir|soll ich|sollen wir|sollte ich|wird|werde ich|wirst du|war|waren|stimmt es|steht|stehen|brauchen wir|brauche ich|fehlt|fehlen|kommt|kommen|geht|gehen wir|fahren wir|sehen wir|treffen wir|is|are|do|does|did|can|could|will|have|has)\b", I);

    /// <summary>Eine Frage: mit Fragezeichen, Fragewort vorn oder Verb vorn („Ist Milch auf der Liste“).</summary>
    private static bool IsQuestion(string t)
    {
        var s = t.Trim();
        if (s.EndsWith('?') || s.Contains("?\n")) return true;
        return QWord.IsMatch(s) || VerbFirst.IsMatch(s);
    }

    /// <summary>Eine Frage ohne Antwort: ehrlich sagen, was Pukki weiß und was nicht.</summary>
    private static Reply Unknown(Ctx c, string t)
    {
        var mem = c.Mem;
        mem.Topic = "unknown";
        if (Tokens(t) is { Count: > 0 } words && words.All(FunctionWords.Contains)) return "Wie meinst du das? Frag mich gern noch einmal mit anderen Worten.";
        if (CanYou.Match(t) is { Success: true } can)
        {
            var what = can.Groups["what"].Value.ToLowerInvariant();
            if (Regex.IsMatch(what, @"^(rechnen|mathe|zählen)", I)) return "Ein bisschen: Frag mich zum Beispiel „Was ist 12 mal 7?“.";
            if (Regex.IsMatch(what, @"^(deutsch|englisch|english)", I)) return "Ich verstehe Deutsch und ein paar Sätze auf Englisch – antworten tue ich auf Deutsch.";
            mem.Why = () => "Weil ich nur ein kleiner Assistent bin: Ich kümmere mich um Termine, Aufgaben, Notizen, die Einkaufsliste und den Essensplan.";
            return new Reply("Das kann ich leider nicht. Was ich kann, zeige ich dir, wenn du „Hilfe“ schreibst.", Unknown: true);
        }
        mem.Why = () => "Weil ich nicht ins Internet schaue – ich weiß nur, was in deinem dmOffice steht: Termine, Aufgaben, Notizen, Kontakte, die Einkaufsliste und den Essensplan.";
        return new Reply("Das weiß ich leider nicht – ich schaue nicht ins Internet und bin kein Lexikon. Ich kenne mich mit deinen Terminen, Aufgaben, Notizen, der Einkaufsliste und dem Essensplan aus.", Unknown: true);
    }

    private static readonly string[] Keyboard = ["qwertzuiopü", "qwertyuiop", "asdfghjklöä", "yxcvbnm", "zxcvbnm", "1234567890", "poiuztrewq", "lkjhgfdsa", "mnbvcxy"];
    private static readonly HashSet<string> FunctionWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "und", "oder", "aber", "der", "die", "das", "den", "dem", "ein", "eine", "ich", "du", "er", "sie", "es", "wir", "ihr", "weil", "dass", "also", "so",
        "denn", "doch", "mal", "schon", "noch", "nur", "auch", "hier", "da", "dort", "jetzt", "dann", "wenn", "ob", "zu", "mit", "von", "für", "in", "an",
        "auf", "bla", "blabla", "blablabla", "blub", "lalala", "trallala", "dingsbums", "dings", "irgendwas", "etwas", "nichts", "nix", "is", "the", "and",
        "sowieso", "eben", "halt", "quasi", "irgendwie", "naja", "äh", "ähm", "hmm", "foo", "bar", "baz", "xyz", "abc", "lorem", "ipsum",
    };

    /// <summary>Sieht ein Wort nach Tastaturgewitter aus (keine Vokale, Tastenreihen, dreifache Buchstaben)?</summary>
    private static bool Gibberish(string w)
    {
        var s = w.ToLowerInvariant();
        if (s.Length < 3) return s.Length > 0 && !char.IsDigit(s[0]) && s is not ("ja" or "ok" or "da" or "du" or "zu" or "so" or "ob");
        if (!s.Any(ch => "aeiouyäöü".Contains(ch))) return true;
        if (Regex.IsMatch(s, @"(\p{L})\1\1")) return true;
        if (Keyboard.Any(k => Enumerable.Range(0, Math.Max(0, k.Length - 3)).Any(i => s.Contains(k.Substring(i, 4))))) return true;
        var vowels = s.Count(ch => "aeiouyäöü".Contains(ch));
        return s.Length >= 7 && vowels * 6 < s.Length;
    }

    private static readonly Regex StatementStart = new(@"^(ich|du|er|sie|es|wir|ihr|man|der|die|das|mein|meine|meiner|dein|deine|unser|unsere|euer|eure|dieser|diese|dieses|heute ist|morgen ist|es ist|das ist|da ist|gestern|vorgestern|letzte woche|letzten monat|letztes jahr|neulich|vorhin|eben|gerade)\b", I);
    private static readonly Regex FiniteVerb = new(@"\b(ist|sind|bin|bist|war|waren|hat|habe|hab|mag|mögen|magst|liebe|liebt|finde|findet|schläft|schlaeft|bellt|scheint|regnet|läuft|laeuft|kommt|heißt|heisst|wohnt|arbeitet|spielt|isst|trinkt|fährt|faehrt|lacht|weint|singt|tanzt|glaube|denke|weiß|weiss|fühle|fuehle|kann|will|wird|wurde|gehört|gefällt|schmeckt|riecht|klingt|aussieht)\b", I);

    /// <summary>Eine Aussage statt eines Auftrags: beginnt mit Fürwort oder Artikel und hat ein gebeugtes Verb („Der Hund bellt“).</summary>
    private static bool Statement(string t) =>
        Tokens(t).Count >= 2 && (StatementStart.IsMatch(t.Trim()) || Regex.IsMatch(t, @"sagt man[\s.!]*$", I)) && FiniteVerb.IsMatch(t);

    /// <summary>Sieht aus wie eine Telefonnummer („+49 170 1234567“, „030/123456“)?</summary>
    private static bool Phone(string t) => Regex.IsMatch(t.Trim(), @"^\+?[\d][\d\s/()\-]{5,}$") && t.Count(char.IsDigit) >= 6;

    /// <summary>Unsinn: nur Füllwörter, Tastaturgewitter, lauter „bla“. Null: sieht nach einem echten Satz aus.</summary>
    private static Reply? Nonsense(Ctx c, string t)
    {
        if (Phone(t)) return Ask(c.Mem, t, "note", "Soll ich mir die Telefonnummer als Notiz merken?", "note");
        if (OnlySymbols(t) is { } symbols) return symbols;
        // Ein Link oder eine E-Mail-Adresse allein: als Notiz anbieten
        if (Regex.IsMatch(t, @"^(https?://|www\.)\S+$", I)) return Ask(c.Mem, t, "note", "Soll ich mir den Link als Notiz merken?", "note");
        if (Regex.IsMatch(t, @"^[\w.+-]+@[\w-]+(\.[\w-]+)+$")) return Ask(c.Mem, t, "note", "Soll ich mir die E-Mail-Adresse als Notiz merken?", "note");
        // Quelltext und Befehle für Datenbanken sind kein Auftrag an Pukki
        if (Regex.IsMatch(t, @"<\s*/?\s*\w+[^>]*>|^\s*(select|drop|insert|delete|update|create)\b.*\b(table|from|into|where|set)\b", I))
            return "Das sieht nach Quelltext aus – damit kann ich leider nichts anfangen.";
        var words = Tokens(t).Where(w => !w.All(char.IsDigit)).ToList();
        if (words.Count == 0) return null;
        var known = words.Count(w => PukkiWords.In(w, PukkiWords.ShopWords) || PukkiWords.In(w, PukkiWords.TaskWords) || PukkiWords.In(w, PukkiWords.EventWords));
        if (known > 0) return null;
        if (words.All(w => FunctionWords.Contains(w) || Gibberish(w)) || (words.Count >= 2 && words.Count(Gibberish) * 2 > words.Count))
            return "Das habe ich leider nicht verstanden. Schreib mir zum Beispiel „morgen 15 Uhr Zahnarzt“, „Milch und Brot“ oder frag „Was steht heute an?“.";
        return null;
    }
}
