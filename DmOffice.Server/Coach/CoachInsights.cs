using DmOffice.Core;

namespace DmOffice.Coach;

/// <summary>
/// Hinweise im Coach („Metawissen“, docs/dmcoach-konzept.md): was über die bekannten Regeln (nicht rauchen, wenig Alkohol,
/// Bewegung) hinaus für ein langes, gesundes Leben belegt ist. Drei Arten:
/// - aus dem eigenen Tagebuch der letzten 14 Tage (Hülsenfrüchte, Nüsse, Ballaststoffe, Fisch, Wurst, Fermentiertes, Eiweiß ab 60),
/// - passend zum Profil (Alter, Ernährungsform, Jahreszeit),
/// - allgemeines Wissen (Blutwerte, Kraft, soziale Kontakte, Zähne; Gehyptes ohne Beleg).
/// Immer mit Studienangabe, ohne Heilversprechen; Nahrungsergänzung nur als Information mit „Spiegel messen lassen“ bzw.
/// „mit Arzt oder Apotheke absprechen“ - keine Empfehlung zur Einnahme.
/// </summary>
public static class CoachInsights
{
    /// <summary>Kind: diary (aus dem Tagebuch), profile (zum Profil), know (Wissen), hype (gehypt, nicht belegt). Why: woran er es sieht.</summary>
    public sealed record Insight(string Key, string Kind, string Title, string Text, string? Source, string? Why = null);

    // Wortstämme in den Namen der Tagebucheinträge (BLS- und Produktnamen, eigene Einträge)
    private static readonly string[] Legumes = ["linse", "bohne", "kichererbse", "erbse", "hummus", "falafel", "dal ", "dhal", "edamame", "lupine"];
    private static readonly string[] Nuts = ["nuss", "nüsse", "mandel", "walnuss", "cashew", "pistazie", "pekan", "macadamia", "haselnuss", "erdnuss"];
    private static readonly string[] Fish = ["fisch", "lachs", "hering", "makrele", "forelle", "thunfisch", "sardine", "kabeljau", "seelachs", "dorsch", "matjes", "sprotte"];
    private static readonly string[] Processed = ["wurst", "würstchen", "schinken", "salami", "speck", "bacon", "aufschnitt", "lyoner", "mortadella", "leberkäse", "cabanossi", "mettwurst", "fleischwurst", "chorizo"];
    private static readonly string[] Fermented = ["joghurt", "jogurt", "kefir", "sauerkraut", "kimchi", "skyr", "buttermilch", "miso", "kombucha", "dickmilch"];

    private static bool Has(CoachStore.Entry e, string[] words)
    {
        var n = " " + e.Name.ToLowerInvariant() + " ";
        // Nuss-Nougat-Creme, Kaffeebohnen u. Ä. zählen nicht
        if (n.Contains("nougat") || n.Contains("kaffee")) return false;
        return words.Any(n.Contains);
    }

    public static List<Insight> For(UserSpace space)
    {
        var db = space.Db;
        var today = CoachStore.Today(space);
        var p = CoachStore.Profile(db);
        var t = CoachTargetsCalc.Compute(p, CoachStore.Weight(db, today), today);
        var list = new List<Insight>();

        // ---------------- aus dem Tagebuch (nur mit genug Einträgen - sonst sagt das Fehlen nichts) ----------------
        var days = Enumerable.Range(1, 14).Select(i => today.AddDays(-i)).Select(d => CoachStore.Diary(db, d)).Where(e => e.Count >= 2).ToList();
        if (days.Count >= 5)
        {
            int DaysWith(string[] w) => days.Count(d => d.Any(e => Has(e, w)));
            int Count(string[] w) => days.Sum(d => d.Count(e => Has(e, w)));
            var n = days.Count;
            var legumes = DaysWith(Legumes);
            if (legumes < Math.Max(2, n / 4))
                list.Add(new("legumes", "diary", "Öfter Hülsenfrüchte", "Linsen, Bohnen und Kichererbsen mehrmals pro Woche, gern täglich – sie kommen in allen besonders langlebigen Bevölkerungen am beständigsten vor.",
                    "Blue-Zones-Erhebungen; mediterrane Ernährung (PREDIMED)", $"an {legumes} von {n} Tagen mit Einträgen"));
            var nuts = DaysWith(Nuts);
            if (nuts < n / 2)
                list.Add(new("nuts", "diary", "Eine Handvoll Nüsse am Tag", "Etwa 30 g ungesalzene Nüsse täglich gehen in großen Kohorten mit rund 20 % geringerer Sterblichkeit einher.",
                    "Kohorten-Metaanalysen zu Nusskonsum", $"an {nuts} von {n} Tagen"));
            var fiberDays = days.Where(d => d.Any(e => e.Fiber != null)).ToList();
            if (fiberDays.Count >= 3)
            {
                var fiber = fiberDays.Average(d => d.Sum(e => e.Fiber ?? 0));
                if (fiber < 25)
                    list.Add(new("fiber", "diary", "Mehr Ballaststoffe", "30 g und mehr pro Tag aus Vollkorn, Hülsenfrüchten, Gemüse und Obst – die meisten liegen bei etwa 20 g.",
                        "DGE-Referenzwert; Metaanalysen zu Ballaststoffen", $"im Schnitt {fiber:0} g pro Tag"));
            }
            var fish = Count(Fish);
            if (fish < 2)
                list.Add(new("fish", "diary", "Fisch ein- bis zweimal pro Woche", "Gern fettreicher Fisch wie Hering, Makrele oder Lachs.",
                    "DGE; mediterrane Ernährung", $"{fish}× in {n} Tagen"));
            var processed = Count(Processed);
            if (processed > n / 3)
                list.Add(new("processed", "diary", "Weniger Wurst", "Bei verarbeitetem Fleisch (Wurst, Schinken, Salami) ist der Zusammenhang mit Darmkrebs und Herzkrankheiten am klarsten. Rotes Fleisch ist in Grenzen in Ordnung (etwa 300–500 g pro Woche).",
                    "WHO/IARC-Einstufung verarbeiteten Fleisches; DGE", $"{processed}× in {n} Tagen"));
            var fermented = DaysWith(Fermented);
            if (fermented < n / 4)
                list.Add(new("fermented", "diary", "Etwas Fermentiertes", "Joghurt, Kefir, Sauerkraut oder Kimchi – in einer Studie stieg damit die Vielfalt der Darmflora, Entzündungsmarker sanken.",
                    "Stanford-Studie (Wastyk et al., Cell 2021)", $"an {fermented} von {n} Tagen"));
            if (t.Age is int age && age >= 60 && CoachStore.Weight(db, today) is double kg && kg > 0)
            {
                var protein = days.Average(d => d.Sum(e => e.Protein)) / kg;
                if (protein < 1.0)
                    list.Add(new("protein60", "diary", "Ab 60 eher mehr Eiweiß", "Etwa 1,0–1,2 g pro kg Körpergewicht – Muskelabbau gehört zu den stärksten Vorboten von Gebrechlichkeit. Am besten zusammen mit Krafttraining.",
                        "DGE-Referenzwert ab 65; Studien zu Sarkopenie", $"im Schnitt {protein:0.0} g pro kg"));
            }
        }

        // ---------------- passend zum Profil ----------------
        if (p.Diet == "vegan")
            list.Add(new("b12vegan", "profile", "Vitamin B12 bei veganer Ernährung", "B12 kommt fast nur in tierischen Lebensmitteln vor; ein Mangel schadet Nerven und Gehirn schleichend. Den Spiegel ärztlich prüfen lassen.",
                "DGE-Positionspapier vegane Ernährung"));
        if (t.Age is int a60 && a60 >= 60)
        {
            list.Add(new("b12age", "profile", "Vitamin B12 ab etwa 60", "Die Aufnahme aus der Nahrung sinkt mit dem Alter, ebenso bei Metformin oder Magensäureblockern. Den Spiegel beim Hausarzt prüfen lassen.",
                "Leitlinien zu B12-Mangel im Alter"));
        }
        if (today.Month is >= 10 or <= 3)
            list.Add(new("vitd", "profile", "Vitamin D im Winter", "In Deutschland ist der Spiegel im Winter oft niedrig. Ohne Mangel bringt Ergänzen kaum etwas – erst den Spiegel (25-OH-D) messen lassen.",
                "VITAL-Studie; RKI/DGE zu Vitamin D"));

        // ---------------- allgemeines Wissen ----------------
        list.Add(new("blood", "know", "Blutwerte kennen", "Blutdruck, LDL bzw. ApoB, HbA1c – und einmal im Leben Lipoprotein(a). Lp(a) ist erblich, durch Lebensstil nicht beeinflussbar und bei etwa jedem Fünften erhöht. Der Check-up 35 der Krankenkasse deckt einen Teil ab.",
            "ESC-Leitlinien zu Dyslipidämie"));
        list.Add(new("fitness", "know", "Kraft und Ausdauer", "Muskelkraft und maximale Sauerstoffaufnahme (VO₂max) gehören zu den stärksten bekannten Vorhersagewerten für die Lebenserwartung – es kommt eher auf die Intensität an als auf „Sport ja oder nein“.",
            "Kohortenstudien zu kardiorespiratorischer Fitness"));
        list.Add(new("social", "know", "Soziale Kontakte", "Einsamkeit wirkt in Metaanalysen auf die Sterblichkeit ähnlich stark wie Rauchen.", "Holt-Lunstad et al., Metaanalyse 2010"));
        list.Add(new("teeth", "know", "Zähne und Zahnfleisch", "Parodontitis hängt mit Herz-Kreislauf-Erkrankungen zusammen – regelmäßige Kontrolle und Zahnreinigung lohnen sich.", "Konsensus Parodontitis und Herz-Kreislauf"));
        list.Add(new("upf", "know", "Weniger hochverarbeitete Fertigprodukte", "Einer der beständigsten Befunde der Ernährungsforschung der letzten Jahre.", "Umbrella-Review, BMJ 2024"));
        list.Add(new("supplements", "know", "Nahrungsergänzung nur bei Bedarf", "Sinnvoll ist sie fast nur bei einem Mangel oder einer Lücke in der Ernährung (etwa Vitamin D, B12, Omega-3 bei wenig Fisch, Kreatin mit Krafttraining ab 50). Bei Medikamenten vorher mit Arzt oder Apotheke sprechen – Omega-3 verstärkt zum Beispiel Blutverdünner.",
            "VITAL, DO-HEALTH, COSMOS"));
        list.Add(new("hype", "hype", "Gehypt, beim Menschen nicht belegt", "NAD-Booster (NMN, NR), Resveratrol, Spermidin, Senolytika, Kollagen: Beim Menschen fehlt bisher der Nachweis für ein längeres oder gesünderes Leben. Hochdosierte Antioxidantien können sogar schaden (Beta-Carotin bei Rauchern, Vitamin E).",
            "ATBC- und SELECT-Studie"));
        return list;
    }

    public static void Map(WebApplication app) =>
        app.MapGroup("/api/coach").RequireAuthorization().MapGet("/insights", (HttpContext http) => For(http.Space()));
}
