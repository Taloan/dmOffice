using System.Text.RegularExpressions;

namespace DmOffice.Chat;

/// <summary>
/// Pukkis Alltagswortschatz (Stufe 5, Schritt 1 „freie Sätze verstehen“): Einkauf nach Abteilungen, Wörter, die nach
/// Termin oder nach Aufgabe klingen, und die Wortwerkzeuge (zerlegen, Grundformen, Umlaute) - übertragen aus dem
/// Berater von journali (plugins/advisor/kern/woerter.mjs, AGPL, derselbe Autor). Die große Assoziationstabelle mit
/// Familien-Themen folgt in Schritt 2.
/// </summary>
public static partial class PukkiWords
{
    [GeneratedRegex(@"\d+(?:[.,]\d+)?|\p{L}[\p{L}\p{N}-]*")]
    private static partial Regex WordRx();

    /// <summary>Füllwörter, die nichts über die Absicht sagen.</summary>
    public static readonly HashSet<string> Filler = new(StringComparer.Ordinal)
    {
        "der", "die", "das", "den", "dem", "des", "ein", "eine", "einen", "einem", "einer", "und", "oder", "mit", "für", "fuer", "von", "zu", "zum", "zur",
        "im", "in", "an", "am", "auf", "aus", "bei", "ist", "sind", "ich", "wir", "sie", "es", "noch", "mal", "bitte", "auch", "mir", "uns", "mein", "meine",
        "unser", "unsere", "dann", "doch", "schon", "nicht", "kein", "keine", "the", "a", "an", "and", "or", "with", "for", "of", "to", "on", "please",
        "vom", "beim", "ins", "ans",
    };

    /// <summary>Einkauf: Abteilung (wie Lists.DefaultShopSections) zu Wörtern.</summary>
    public static readonly Dictionary<string, string[]> Shop = new()
    {
        ["Obst & Gemüse"] = [
            "apfel", "äpfel", "banane", "bananen", "birne", "birnen", "orange", "orangen", "mandarine", "mandarinen", "zitrone", "zitronen", "limette", "limetten",
            "traube", "trauben", "weintrauben", "erdbeere", "erdbeeren", "himbeere", "himbeeren", "heidelbeere", "heidelbeeren", "blaubeeren", "kiwi", "kiwis",
            "mango", "ananas", "melone", "wassermelone", "pfirsich", "pfirsiche", "pflaume", "pflaumen", "kirsche", "kirschen", "tomate", "tomaten",
            "cherrytomaten", "gurke", "gurken", "salatgurke", "paprika", "zwiebel", "zwiebeln", "knoblauch", "kartoffel", "kartoffeln", "karotte", "karotten",
            "möhre", "möhren", "salat", "kopfsalat", "eisbergsalat", "spinat", "brokkoli", "blumenkohl", "zucchini", "aubergine", "pilze", "champignons",
            "lauch", "porree", "sellerie", "radieschen", "avocado", "avocados", "ingwer", "petersilie", "schnittlauch", "basilikum", "kräuter", "mais",
            "kohlrabi", "rotkohl", "weißkohl", "kürbis", "rucola", "feldsalat", "frühlingszwiebeln", "obst", "gemüse", "apples", "bananas", "tomatoes", "potatoes",
            "beeren", "granatapfel", "feigen", "datteln", "nektarine", "nektarinen", "aprikose", "aprikosen", "marillen", "paradeiser", "erdäpfel", "karfiol",
            "fisolen", "kren", "meerrettich", "rosenkohl", "grünkohl", "chinakohl", "spargel", "fenchel", "rote bete", "rote beete", "pastinaken", "süßkartoffeln",
            "süßkartoffel", "chili", "chilis", "peperoni", "minze", "dill", "koriander", "rosmarin", "thymian", "salbei", "schalotten", "lauchzwiebeln", "radicchio",
            "lollo", "endivie", "staudensellerie", "zuckerschoten", "erdbeeren", "johannisbeeren", "stachelbeeren", "brombeeren", "physalis", "papaya", "maracuja",
            "kokosnuss", "pomelo", "grapefruit", "clementinen", "clementine", "rhabarber", "quitten", "salatherz", "babyspinat", "sprossen", "gurkerl",
        ],
        ["Brot & Backwaren"] = [
            "brot", "brötchen", "semmel", "semmeln", "toast", "toastbrot", "baguette", "croissant", "croissants", "brezel", "brezeln", "laugenbrezel",
            "vollkornbrot", "knäckebrot", "zwieback", "kuchen", "bread", "rolls", "weckerl", "weckle", "schrippen", "schrippe", "kipferl", "brezn", "krapfen",
            "berliner", "pfannkuchen", "laugenstangen", "laugenbrötchen", "mischbrot", "roggenbrot", "dinkelbrot", "sandwichbrot", "tortillas", "wraps",
            "fladenbrot", "ciabatta", "muffins", "donuts", "hörnchen", "buns", "burgerbrötchen", "hotdogbrötchen", "aufbackbrötchen", "zopf", "stollen",
        ],
        ["Kühlregal"] = [
            "milch", "hafermilch", "butter", "joghurt", "jogurt", "quark", "sahne", "schlagsahne", "schmand", "eier", "ei", "frischkäse", "margarine",
            "pudding", "hefe", "tofu", "buttermilch", "kefir", "skyr", "milk", "eggs", "yoghurt", "sojamilch", "mandelmilch", "vollmilch", "h-milch",
            "fettarme milch", "topfen", "schlagobers", "obers", "rahm", "sauerrahm", "creme fraiche", "crème fraîche", "mascarpone", "ricotta", "hüttenkäse",
            "kaffeesahne", "kondensmilch", "pizzateig", "blätterteig", "hefeteig", "mürbeteig", "flammkuchenteig", "trockenhefe", "naturjoghurt", "fruchtjoghurt",
            "griechischer joghurt", "eiweiß", "milchreis", "grießbrei", "smoothie", "orangensaft frisch",
        ],
        ["Fleisch & Wurst"] = [
            "fleisch", "hackfleisch", "hack", "hähnchen", "hähnchenbrust", "hühnchen", "pute", "putenbrust", "schnitzel", "wurst", "würstchen", "wiener",
            "salami", "schinken", "speck", "bratwurst", "steak", "rindfleisch", "schweinefleisch", "aufschnitt", "leberwurst", "lachs", "fisch", "thunfisch",
            "garnelen", "chicken", "meat", "faschiertes", "leberkäse", "leberkäs", "extrawurst", "frankfurter", "debrecziner", "krakauer", "kabanossi",
            "mortadella", "putenschnitzel", "hähnchenschenkel", "chicken nuggets", "nuggets", "fischfilet", "forelle", "matjes", "hering", "krabben", "muscheln",
            "räucherlachs", "gulasch", "gulaschfleisch", "rouladen", "kassler", "kasseler", "hackbraten", "frikadellen", "buletten", "cevapcici", "lammfleisch",
            "entenbrust", "wildlachs", "seelachs", "kabeljau", "dorade", "sardinen", "sardellen", "mettwurst", "teewurst", "blutwurst", "weißwurst", "weißwürste",
            "rostbratwürste", "minutensteaks", "geschnetzeltes",
        ],
        ["Käse"] = [
            "käse", "gouda", "emmentaler", "mozzarella", "parmesan", "feta", "camembert", "brie", "reibekäse", "bergkäse", "schafskäse", "cheese",
            "cheddar", "gorgonzola", "halloumi", "hirtenkäse", "raclettekäse", "scheibenkäse", "streichkäse", "schmelzkäse", "butterkäse", "tilsiter",
            "edamer", "harzer", "ziegenkäse", "pecorino", "grana padano", "appenzeller", "gruyère", "manchego",
        ],
        ["Tiefkühl"] = [
            "tiefkühlpizza", "pizza", "eis", "eiscreme", "pommes", "fischstäbchen", "tiefkühlgemüse", "erbsen", "eiswürfel", "kroketten", "rösti",
            "tk-erbsen", "tk-spinat", "rahmspinat", "tiefkühlbeeren", "speiseeis", "wedges", "bratkartoffeln", "backofenpommes", "tiefkühlbrötchen",
        ],
        ["Konserven & Vorrat"] = [
            "nudeln", "spaghetti", "penne", "lasagneplatten", "reis", "mehl", "zucker", "salz", "pfeffer", "öl", "olivenöl", "rapsöl", "essig", "ketchup",
            "senf", "mayonnaise", "mayo", "honig", "marmelade", "konfitüre", "nutella", "müsli", "haferflocken", "cornflakes", "linsen", "kichererbsen",
            "bohnen", "dosentomaten", "tomatenmark", "passata", "brühe", "gewürze", "backpulver", "vanillezucker", "mandeln", "suppe", "soße", "sauce",
            "pesto", "rice", "pasta", "sugar", "flour", "grieß", "couscous", "bulgur", "quinoa", "polenta", "tortellini", "ravioli", "gnocchi", "spätzle",
            "maultaschen", "knödel", "semmelknödel", "fusilli", "farfalle", "makkaroni", "bandnudeln", "eiernudeln", "glasnudeln", "reisnudeln", "basmatireis",
            "risottoreis", "milchreis", "croutons", "nachos", "salsa", "hummus", "oliven", "kapern", "essiggurken", "gewürzgurken", "sauerkraut", "mais in der dose",
            "sojasoße", "sojasauce", "currypaste", "kokosmilch", "zimt", "paprikapulver", "oregano", "curry", "currypulver", "muskat", "muskatnuss", "vanille",
            "puderzucker", "brauner zucker", "kakaopulver", "schokostreusel", "sahnesteif", "gelatine", "speisestärke", "rosinen", "walnüsse", "haselnüsse",
            "cashews", "cashewkerne", "pistazien", "sonnenblumenkerne", "kürbiskerne", "pinienkerne", "chiasamen", "leinsamen", "erdnussbutter", "tomatensoße",
            "tomatensauce", "tomatensosse", "pizzasoße", "bratensoße", "gemüsebrühe", "hühnerbrühe", "fond", "thunfischdosen", "ravioli dose", "apfelmus",
            "kompott", "pfirsiche in dose", "ahornsirup", "sirup", "agavendicksaft", "süßstoff", "knäcke", "kartoffelpüree", "püree", "semmelbrösel", "paniermehl",
            "lorbeer", "lorbeerblätter", "kümmel", "chiliflocken", "knoblauchpulver", "kräutersalz", "brühwürfel", "instantnudeln", "fertigsuppe", "dosensuppe",
            "kaffeefilter", "filtertüten", "kaffeekapseln", "pads", "babybrei", "babynahrung", "gläschen", "säuglingsnahrung", "pre-milch",
        ],
        ["Getränke"] = [
            "wasser", "mineralwasser", "sprudel", "saft", "apfelsaft", "orangensaft", "cola", "limo", "limonade", "bier", "wein", "rotwein", "weißwein",
            "sekt", "kaffee", "kaffeebohnen", "tee", "kakao", "kaffeepads", "kapseln", "schorle", "apfelschorle", "eistee", "water", "juice", "beer", "wine",
            "coffee", "tea", "prosecco", "gin", "whisky", "wodka", "rum", "schnaps", "likör", "energydrink", "spezi", "radler", "fanta", "sprite", "almdudler",
            "multivitaminsaft", "traubensaft", "johannisbeersaft", "tonic", "tonic water", "ginger ale", "alkoholfreies bier", "rosé", "roséwein", "glühwein",
            "pfefferminztee", "kamillentee", "früchtetee", "grüner tee", "schwarztee", "espresso", "milchkaffee", "stilles wasser", "kohlensäure", "sodastream",
        ],
        ["Süßes & Snacks"] = [
            "schokolade", "chips", "kekse", "plätzchen", "gummibärchen", "bonbons", "süßigkeiten", "popcorn", "salzstangen", "nüsse", "erdnüsse", "riegel",
            "müsliriegel", "chocolate", "cookies", "lakritz", "kaugummi", "pralinen", "waffeln", "studentenfutter", "cracker", "brezeln", "flips", "erdnussflips",
            "schokoriegel", "gummitiere", "marshmallows", "zartbitterschokolade", "milchschokolade", "nougat", "marzipan", "lebkuchen", "dominosteine",
        ],
        ["Drogerie"] = [
            "zahnpasta", "zahnbürste", "zahnbürsten", "zahnseide", "shampoo", "duschgel", "seife", "flüssigseife", "deo", "deodorant", "creme", "handcreme",
            "sonnencreme", "windeln", "feuchttücher", "taschentücher", "tempos", "klopapier", "toilettenpapier", "wattepads", "wattestäbchen", "rasierer",
            "rasierklingen", "binden", "tampons", "pflaster", "haarspray", "spülung", "lippenpflege", "shampoo", "soap", "ibuprofen", "paracetamol", "aspirin",
            "nasenspray", "hustenbonbons", "hustensaft", "vitamine", "kondome", "rasierschaum", "rasiergel", "nagellackentferner", "haargummis", "abschminktücher",
            "bodylotion", "gesichtscreme", "lippenbalsam", "sonnenspray", "mückenspray", "insektenspray", "mundspülung", "zahnseidesticks", "slipeinlagen",
            "babyöl", "wundschutzcreme", "haarkur", "haargel", "kontaktlinsenmittel", "desinfektionsmittel", "handseife", "fieberthermometer", "wärmflasche",
            "katzenfutter", "hundefutter", "katzenstreu", "vogelfutter", "leckerlis", "nassfutter", "trockenfutter", "kauknochen",
            "toilet paper", "toothpaste", "shampoo", "diapers", "tissues",
        ],
        ["Haushalt"] = [
            "spülmittel", "waschmittel", "weichspüler", "müllbeutel", "mülltüten", "küchenrolle", "küchenpapier", "alufolie", "frischhaltefolie",
            "backpapier", "batterien", "glühbirne", "glühbirnen", "kerzen", "servietten", "schwämme", "schwamm", "putzmittel", "reiniger", "allzweckreiniger",
            "spülmaschinentabs", "tabs", "entkalker", "gefrierbeutel", "streichhölzer", "toiletpaper", "spülmaschinensalz", "klarspüler", "regeneriersalz",
            "wc-reiniger", "glasreiniger", "essigreiniger", "scheuermilch", "spültücher", "lappen", "mikrofasertuch", "staubsaugerbeutel", "zahnstocher",
            "teelichter", "grillanzünder", "grillkohle", "feuerzeug", "pappteller", "plastikbecher", "strohhalme", "luftballons", "geschenkpapier", "klebeband",
            "tesafilm", "druckerpapier", "druckerpatronen", "toner", "stifte", "kugelschreiber", "hefte", "kleber", "blumenerde", "dünger", "vogelsand",
            "colorwaschmittel", "vollwaschmittel", "feinwaschmittel", "fleckentferner", "wäscheklammern", "bügelwasser", "kalkreiniger", "abflussreiniger",
            "handschuhe", "einmalhandschuhe", "müllsäcke", "biomüllbeutel", "briefumschläge", "briefmarken", "panzerband", "sekundenkleber", "kabelbinder",
        ],
    };

    /// <summary>Gerichte (für den Essensplan): was man kocht oder isst, ohne Einkaufswort zu sein.</summary>
    public static readonly HashSet<string> DishWords = new(StringComparer.Ordinal)
    {
        "lasagne", "gulasch", "pizza", "suppe", "eintopf", "auflauf", "nudelauflauf", "kartoffelauflauf", "curry", "chili", "chili con carne", "risotto",
        "pfannkuchen", "eierkuchen", "palatschinken", "kaiserschmarrn", "burger", "pommes", "bratwurst", "döner", "sushi", "salat", "tacos", "quiche",
        "flammkuchen", "rouladen", "braten", "schweinebraten", "sauerbraten", "käsespätzle", "kartoffelsalat", "bratkartoffeln", "frikadellen", "buletten",
        "fleischpflanzerl", "rührei", "spiegelei", "omelett", "sandwich", "toast", "spaghetti", "bolognese", "carbonara", "schnitzel", "wiener schnitzel",
        "linsensuppe", "erbsensuppe", "kartoffelsuppe", "tomatensuppe", "gemüsesuppe", "hühnersuppe", "nudelsuppe", "gemüsepfanne", "reispfanne", "wok",
        "fischstäbchen", "maultaschen", "knödel", "spätzle", "raclette", "fondue", "grillen", "gegrilltes", "steak", "hähnchen", "brathähnchen", "ente",
        "gans", "fisch", "lachs", "nudeln", "reis", "kartoffeln", "pellkartoffeln", "salzkartoffeln", "kartoffelpüree", "püree", "pfannengemüse",
        "ratatouille", "falafel", "hummus", "wraps", "burritos", "enchiladas", "lasagna", "moussaka", "gyros", "souvlaki", "paella", "tortilla",
        "gnocchi", "tortellini", "ravioli", "pasta", "pesto", "müsli", "porridge", "brei", "milchreis", "grießbrei", "waffeln", "crêpes", "crepes",
        "kuchen", "brote", "käsebrote", "abendbrot", "brotzeit", "jause", "vesper", "resteessen", "reste", "sauerkraut", "leberkäse", "weißwürste",
        "currywurst", "hotdogs", "hot dogs", "nuggets", "chicken nuggets", "cordon bleu", "gyoza", "ramen", "pho", "bowl", "buddha bowl", "poke bowl",
        "ofengemüse", "gefüllte paprika", "kohlrouladen", "königsberger klopse", "senfeier", "linsen", "dal", "kichererbsencurry", "couscous",
    };

    /// <summary>Allgemeine Wörter, die nie ein Einkaufsartikel sind („ich brauche Urlaub“, „wir brauchen Hilfe“).</summary>
    public static readonly HashSet<string> NotItems = new(StringComparer.Ordinal)
    {
        "urlaub", "pause", "ruhe", "hilfe", "zeit", "schlaf", "geld", "liebe", "erholung", "abwechslung", "termin", "termine", "rat", "tipp", "idee",
        "ideen", "plan", "lösung", "glück", "nerven", "kraft", "mut", "motivation", "frieden", "spaß", "frische luft", "luft", "bewegung", "dich", "mich",
        "dir", "mir", "uns", "euch", "das", "es", "was", "nichts", "etwas", "alles", "noch", "mehr", "weniger",
    };

    /// <summary>Wörter, die nach einem Termin klingen (Ort, Anlass, Person).</summary>
    public static readonly HashSet<string> EventWords = new(StringComparer.Ordinal)
    {
        "termin", "arzt", "ärztin", "zahnarzt", "zahnärztin", "kinderarzt", "hausarzt", "frauenarzt", "augenarzt", "tierarzt", "friseur", "frisör",
        "meeting", "besprechung", "treffen", "verabredung", "training", "probe", "chorprobe", "elternabend", "elterngespräch", "sprechstunde",
        "untersuchung", "vorsorge", "impfung", "physio", "physiotherapie", "massage", "kino", "theater", "konzert", "party", "feier", "geburtstag",
        "geburtstagsfeier", "hochzeit", "besuch", "abendessen", "mittagessen", "frühstück", "brunch", "schwimmen", "fußball", "turnen", "reiten",
        "tennis", "musikschule", "klavierunterricht", "gitarrenunterricht", "unterricht", "nachhilfe", "kurs", "abholen", "flug", "zug", "abfahrt",
        "urlaub", "ferien", "werkstatt", "tüv", "inspektion", "vorstellungsgespräch", "gottesdienst", "beerdigung", "sitzung", "konferenz", "call",
        "doctor", "dentist", "meeting", "appointment", "party", "birthday", "dinner", "lunch", "training",
        "doktor", "ordination", "zahnarzttermin", "arzttermin", "friseurtermin", "kontrolle", "orthopäde", "hautarzt", "hno", "psychologe", "therapie",
        "ergotherapie", "logopädie", "osteopath", "heilpraktiker", "yoga", "pilates", "fitness", "fitnessstudio", "gym", "joggen", "laufen", "radtour",
        "wandern", "ausflug", "museum", "zoo", "spielplatz", "schwimmbad", "sauna", "restaurant", "essen gehen", "kaffee trinken", "kaffeetrinken", "jause",
        "weihnachten", "heiligabend", "silvester", "ostern", "pfingsten", "fest", "weihnachtsfeier", "betriebsfeier", "firmenfeier", "geburtstagsparty", "kindergeburtstag", "jubiläum", "taufe", "kommunion", "konfirmation", "firmung", "abschlussfeier",
        "elternsprechtag", "schulfest", "sommerfest", "laternenumzug", "flohmarkt", "markt", "messe", "lesung", "vortrag", "seminar", "workshop",
        "webinar", "schulung", "prüfung", "klausur", "klassenarbeit", "test", "telko", "telefonkonferenz", "videocall", "teams", "zoom", "interview",
        "notartermin", "notar", "anwalt", "bank", "bankberatung", "behörde", "amt", "bürgeramt", "finanzamt", "zulassungsstelle", "handwerker",
        "schornsteinfeger", "ablesung", "lieferung", "umzug", "einzug", "auszug", "reise", "abflug", "ankunft", "kur", "chor", "orchester", "auftritt",
        "aufführung", "vorstellung", "spiel", "fußballspiel", "match", "turnier", "wettkampf", "lauf", "marathon", "kita", "schule", "hort", "babysitter",
        "elternzeit", "dienstreise", "schicht", "frühschicht", "spätschicht", "nachtschicht", "konzert", "oper", "musical", "festival", "zirkus", "date",
        "stammtisch", "spieleabend", "kaffeeklatsch", "brunch", "kegeln", "bowling", "minigolf", "kletterhalle", "reitstunde", "fahrstunde", "führerschein",
    };

    /// <summary>Wörter, die nach einer Aufgabe klingen (etwas erledigen).</summary>
    public static readonly HashSet<string> TaskWords = new(StringComparer.Ordinal)
    {
        "anrufen", "bezahlen", "überweisen", "abgeben", "erledigen", "kündigen", "beantragen", "bestellen", "zurückschicken", "zurückgeben",
        "reparieren", "putzen", "aufräumen", "waschen", "bügeln", "staubsaugen", "saugen", "mähen", "gießen", "hausaufgaben", "lernen", "schreiben",
        "antworten", "mailen", "ausfüllen", "unterschreiben", "buchen", "reservieren", "abmelden", "anmelden", "verlängern", "einreichen",
        "drucken", "kaufen", "besorgen", "abholen", "vorbereiten", "packen", "einpacken", "schicken", "zahlen", "rechnung", "steuer", "steuererklärung",
        "call", "pay", "buy", "fix", "clean", "send", "write", "book", "order", "return",
        "rausbringen", "runterbringen", "wegbringen", "bringen", "aufhängen", "abhängen", "abwaschen", "spülen", "tanken", "entrümpeln", "ausmisten",
        "sortieren", "abschicken", "einwerfen", "wegwerfen", "erneuern", "prüfen", "checken", "klären", "organisieren", "planen", "recherchieren",
        "vereinbaren", "absagen", "zusagen", "kümmern", "holen", "zurückrufen", "melden", "nachfragen", "fragen", "mitbringen", "ausdrucken", "scannen",
        "kopieren", "hochladen", "herunterladen", "runterladen", "installieren", "updaten", "aktualisieren", "sichern", "wechseln", "tauschen", "ersetzen",
        "montieren", "streichen", "renovieren", "entkalken", "lüften", "füttern", "machen", "erledigen", "besprechen", "beantworten", "überweisen",
        "abmelden", "ummelden", "anmelden", "verschicken", "versenden", "einkaufen", "wischen", "fegen", "kehren", "abstauben", "wäsche", "fenster",
        "keller", "dachboden", "garage", "müll", "altglas", "altpapier", "pfand", "leergut", "zurückbringen", "abgeben", "einlösen", "verkaufen",
        "inserieren", "reklamieren", "umtauschen", "kontrollieren", "ablesen", "reinigen", "sauber", "aufräumen", "ausräumen", "einräumen", "abtauen",
        "rasen", "hecke", "unkraut", "jäten", "pflanzen", "umtopfen", "schneiden", "gießen", "geschenk", "geschenke", "karte", "brief", "formular",
        "antrag", "unterlagen", "dokumente", "vertrag", "versicherung", "kündigung", "reifen", "ölwechsel", "hausaufgabe", "referat", "präsentation",
        "bericht", "angebot", "bewerbung", "lebenslauf", "steuererklaerung", "rückmelden", "bescheid", "geben", "denken", "erinnern", "suchen",
        "finden", "reservieren", "bestellen", "recherchieren", "zusammenstellen", "ausfüllen", "beantragen", "ausmachen", "abklären", "nachschauen",
    };

    /// <summary>Damerau-Levenshtein-Abstand (Tippfehler: vertauscht, fehlend, zu viel, falsch), ohne Groß/Klein und Umlaute.</summary>
    public static int Distance(string a, string b)
    {
        a = Fold(a.ToLowerInvariant()); b = Fold(b.ToLowerInvariant());
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        return d[a.Length, b.Length];
    }

    /// <summary>Sieht a wie ein Tippfehler von b aus? Kurze Wörter: ein Fehler, ab acht Buchstaben: zwei.</summary>
    public static bool Near(string a, string b)
    {
        if (a.Length < 4 || b.Length < 4 || Math.Abs(a.Length - b.Length) > 2) return false;
        var dist = Distance(a, b);
        return dist > 0 && dist <= (Math.Min(a.Length, b.Length) >= 8 ? 2 : 1);
    }

    /// <summary>Ist der Artikel (ganz oder sein letztes Wort) ein Wort aus dem Einkaufswortschatz?</summary>
    public static bool IsShopWord(string item)
    {
        var lower = item.ToLowerInvariant().Trim();
        if (ShopWords.Contains(lower) || ShopWords.Contains(Fold(lower))) return true;
        // „frische Milch“ ja, „Lisa mag keine Nüsse“ und „ich bin ein Berliner“ nein
        var raw = lower.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (raw.Length == 1) return In(raw[0], ShopWords);
        return raw.Length == 2 && Regex.IsMatch(raw[0], @"^(\p{L}{2,}(e|en|er|es|em)|bio|frisch|vegan|light)$") && In(raw[1], ShopWords);
    }

    /// <summary>Der bekannte Artikel, der dem Wort am ähnlichsten ist (Tippfehler) - null: keiner.</summary>
    public static string? Nearest(string word, IEnumerable<string> known) =>
        known.Where(k => !k.Contains(' ') && Near(word, k)).OrderBy(k => Distance(word, k)).ThenBy(k => Math.Abs(k.Length - word.Length)).FirstOrDefault();

    /// <summary>Wörter einer Eingabe, klein, ohne Füllwörter.</summary>
    public static List<string> Words(string text)
    {
        var result = new List<string>();
        foreach (Match m in WordRx().Matches(text.ToLowerInvariant()))
        {
            var w = m.Value.Trim('-');
            if (w.Length >= 2 && !Filler.Contains(w) && !result.Contains(w)) result.Add(w);
        }
        return result;
    }

    /// <summary>Das Wort und seine Grundformen ohne die häufigsten Endungen - nur, wenn mindestens vier Zeichen bleiben.</summary>
    public static IEnumerable<string> Forms(string w)
    {
        yield return w;
        foreach (var e in new[] { "en", "er", "e", "n", "s" })
            if (w.EndsWith(e) && w.Length - e.Length >= 4) yield return w[..^e.Length];
    }

    /// <summary>Kommt das Wort (oder eine Grundform, auch ohne Umlaute) in der Menge vor?</summary>
    public static bool In(string w, ISet<string> set) => Forms(w).Any(f => set.Contains(f) || set.Contains(Fold(f)) || Variants(f).Any(set.Contains));

    /// <summary>ae/oe/ue/ss statt Umlaut und ß.</summary>
    public static string Fold(string w) => w.Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Replace("ß", "ss");

    /// <summary>Umgekehrt: aus ae/oe/ue wieder Umlaute (wer am Handy ohne Umlaute tippt).</summary>
    private static IEnumerable<string> Variants(string w)
    {
        if (w.Contains("ae") || w.Contains("oe") || w.Contains("ue")) yield return w.Replace("ae", "ä").Replace("oe", "ö").Replace("ue", "ü");
    }

    private static readonly Lazy<(HashSet<string> All, Dictionary<string, string> Section)> ShopIndex = new(() =>
    {
        var all = new HashSet<string>(StringComparer.Ordinal);
        var section = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (s, words) in Shop)
            foreach (var w in words) { all.Add(w); all.Add(Fold(w)); section.TryAdd(w, s); section.TryAdd(Fold(w), s); }
        return (all, section);
    });

    public static ISet<string> ShopWords => ShopIndex.Value.All;

    /// <summary>Abteilung eines Artikels aus dem Wortschatz (letztes Wort zählt: „frische Milch“ → Kühlregal). Null: unbekannt.</summary>
    public static string? SectionOf(string item)
    {
        foreach (var w in Words(item).AsEnumerable().Reverse())
            foreach (var f in Forms(w).Append(Fold(w)))
                if (ShopIndex.Value.Section.TryGetValue(f, out var s)) return s;
        return null;
    }
}
