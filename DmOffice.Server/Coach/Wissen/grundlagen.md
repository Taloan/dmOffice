# Coach – Grundlagen für Pukki

Wissenskarten, aus denen Pukki bei Fragen zu Ernährung und Gesundheit antwortet (docs/dmcoach-konzept.md, „Coach-Wissen für Pukki“).
Eigene Worte, angelehnt an die Referenzwerte und Empfehlungen von DGE, EFSA und WHO – nichts abgeschrieben.

Aufbau einer Karte: `## id: Titel`, dann Zeilen `Stichwörter:` (Komma, mehrteilige erlaubt), `Art:` (wissen, gesundheit, verhalten;
bei gesundheit hängt Pukki den Hinweis an, dass das keine Beratung ersetzt), optional `Nicht bei:` (minderjährig, schwanger,
untergewicht – dann gibt Pukki stattdessen den Hinweis auf Ärztin oder Arzt) und `Persönlich:` (ein Satz mit Platzhaltern aus dem
Profil: {bmr} {tdee} {kcal} {protein} {carbs} {fat} {fiber} {water} {bmi} {weight}; fehlt ein Wert oder sind Zahlen ausgeblendet,
entfällt der Satz). Danach der Text. Keine Beratung zu Medikamenten und keine Aussagen zu Beschwerden – das fängt Pukki vorher ab.

## makros: Makronährstoffe – Eiweiß, Kohlenhydrate und Fett
Stichwörter: makros, makronährstoffe, nährstoffverteilung, verteilung, eiweiß kohlenhydrate fett, protein carbs fett, macros
Art: wissen
Persönlich: Deine Tagesziele im Coach: etwa {protein} g Eiweiß, {carbs} g Kohlenhydrate und {fat} g Fett.

Makronährstoffe liefern Energie: Eiweiß und Kohlenhydrate je Gramm etwa 4 kcal, Fett etwa 9 kcal. Für Erwachsene empfiehlt die DGE gut die Hälfte der Energie aus Kohlenhydraten (am besten Vollkorn, Gemüse, Hülsenfrüchte), rund 30 % aus Fett und 0,8 g Eiweiß je kg Körpergewicht, ab 65 Jahren 1,0 g.
Je nach Ziel verschiebt sich das: Beim Abnehmen hilft mehr Eiweiß (etwa 1,2–1,6 g/kg), satt zu bleiben und Muskeln zu erhalten; beim Muskelaufbau sind 1,2–2,0 g/kg üblich. Low-Carb und Keto senken die Kohlenhydrate zugunsten von Fett. Entscheidend für das Gewicht bleibt die Energiebilanz – die Verteilung beeinflusst vor allem Sättigung, Leistung und Muskeln.

## eiweiss: Eiweiß (Protein)
Stichwörter: eiweiß, eiweiss, protein, proteine, eiweißquelle, eiweißquellen, proteinquelle, eiweißbedarf, proteinbedarf, aminosäuren
Art: wissen
Persönlich: Dein Eiweißziel im Coach liegt bei etwa {protein} g am Tag.

Eiweiß braucht der Körper für Muskeln, Organe, Enzyme und das Immunsystem. Gute Quellen sind Hülsenfrüchte (Linsen, Bohnen, Kichererbsen), Soja und Tofu, Milchprodukte (Quark, Skyr, Joghurt, Käse), Eier, Fisch, Geflügel, Fleisch, Nüsse und Vollkorngetreide.
Pflanzliches Eiweiß wird wertvoller, wenn man kombiniert – etwa Bohnen mit Reis oder Linsen mit Brot. Über den Tag verteilt (20–30 g je Mahlzeit) nutzt es der Körper gut. Für Gesunde sind auch höhere Mengen bis etwa 2 g je kg unbedenklich; wer eine Nierenerkrankung hat, bespricht die Eiweißmenge mit der Ärztin oder dem Arzt.

## kohlenhydrate: Kohlenhydrate
Stichwörter: kohlenhydrate, kohlehydrate, carbs, stärke, vollkorn, weißmehl, glykämisch, glykämischer index, blutzuckerspiegel, sättigung
Art: wissen
Persönlich: Dein Ziel im Coach: etwa {carbs} g Kohlenhydrate am Tag.

Kohlenhydrate sind der wichtigste Brennstoff für Gehirn und Muskeln. Günstig sind solche mit vielen Ballaststoffen: Vollkornbrot, Haferflocken, Naturreis, Kartoffeln, Hülsenfrüchte, Gemüse und Obst – sie machen länger satt und lassen den Blutzucker langsamer steigen.
Weißmehl, Süßigkeiten und gesüßte Getränke liefern schnelle Energie, aber kaum Nährstoffe. Freien Zucker empfehlen WHO und DGE auf unter 10 % der Energie zu begrenzen – bei 2000 kcal sind das höchstens 50 g, besser weniger.

## fett: Fett und Fettqualität
Stichwörter: fett, fette, fettsäuren, gesättigte fettsäuren, ungesättigte fettsäuren, omega 3, omega-3, omega 6, transfette, öl, öle, rapsöl, olivenöl, butter
Art: wissen
Persönlich: Dein Fettziel im Coach: etwa {fat} g am Tag.

Fett liefert Energie, Bausteine für Zellen und hilft, die Vitamine A, D, E und K aufzunehmen. Wichtiger als die Menge ist die Art: Ungesättigte Fettsäuren aus Rapsöl, Olivenöl, Nüssen, Samen und fettem Seefisch sind günstig; Rapsöl, Leinöl und Walnüsse liefern pflanzliches Omega-3.
Gesättigte Fette (Butter, Sahne, fettes Fleisch, Wurst, Käse, Kokos- und Palmfett) sollten weniger als ein Zehntel der Energie ausmachen. Transfette aus stark erhitzten oder gehärteten Fetten (manches Frittierte und Gebäck) meidet man am besten ganz.

## ballaststoffe: Ballaststoffe
Stichwörter: ballaststoffe, ballaststoff, faser, fasern, vollkorn, sättigung, flohsamen, leinsamen, hülsenfrüchte
Art: wissen
Persönlich: Dein Ziel: mindestens {fiber} g Ballaststoffe am Tag.

Ballaststoffe sind Pflanzenbestandteile, die der Dünndarm nicht verdaut. Sie machen satt, halten die Verdauung in Schwung, lassen den Blutzucker langsamer steigen, senken das Cholesterin und füttern die guten Darmbakterien. Die DGE empfiehlt mindestens 30 g am Tag – die meisten Menschen essen deutlich weniger.
Viel steckt in Vollkornprodukten, Haferflocken, Hülsenfrüchten, Gemüse, Obst, Nüssen und Samen. Am besten langsam steigern und genug trinken, dann gewöhnt sich der Bauch daran.

## vitamine: Vitamine
Stichwörter: vitamine, vitamin, vitamin a, vitamin b, vitamin c, vitamin d, vitamin e, vitamin k, b-vitamine, folat, folsäure, vitamin b12, b12, vitaminmangel
Art: wissen

Vitamine braucht der Körper in kleinen Mengen für Stoffwechsel, Immunsystem, Nerven, Blutbildung und Knochen. Die fettlöslichen (A, D, E, K) speichert er, die wasserlöslichen (B-Vitamine, C) muss man laufend zuführen.
Eine bunte Ernährung mit viel Gemüse, Obst, Vollkorn und Hülsenfrüchten deckt den Bedarf meist. Kritisch sind vor allem Vitamin D (bildet die Haut mit Sonnenlicht, im Winter oft zu wenig), Folat (wichtig vor und in der Schwangerschaft) und Vitamin B12 (fast nur in tierischen Lebensmitteln – bei veganer Ernährung immer ergänzen). Ob ein Mangel besteht, zeigt nur eine Blutuntersuchung beim Arzt.

## mineralstoffe: Mineralstoffe und Spurenelemente
Stichwörter: mineralstoffe, mineralien, spurenelemente, calcium, kalzium, magnesium, kalium, natrium, eisen, zink, jod, selen, fluorid, mineralstoffmangel
Art: wissen

Mineralstoffe (Calcium, Magnesium, Kalium, Natrium) und Spurenelemente (Eisen, Zink, Jod, Selen) bauen Knochen und Zähne auf, steuern Nerven und Muskeln, transportieren Sauerstoff (Eisen) und sind Teil von Hormonen (Jod in der Schilddrüse) und Enzymen.
Gute Quellen: Calcium in Milchprodukten, grünem Gemüse und calciumreichem Mineralwasser; Magnesium in Vollkorn, Nüssen und Hülsenfrüchten; Kalium in Obst, Gemüse und Kartoffeln; Eisen in Fleisch, Hülsenfrüchten und Vollkorn (pflanzliches zusammen mit Vitamin C); Jod in Seefisch und Jodsalz. Wenn du wissen willst, was viel davon enthält, frag mich zum Beispiel „Was enthält viel Magnesium?“.

## energiebilanz: Energiebilanz – Grundumsatz, Leistungsumsatz, Defizit
Stichwörter: energiebilanz, grundumsatz, leistungsumsatz, gesamtumsatz, kalorienbedarf, energiebedarf, kaloriendefizit, defizit, kalorienüberschuss, überschuss, pal, tdee, bmr, kalorien
Art: wissen
Persönlich: Mit deinem Profil: Grundumsatz etwa {bmr} kcal, Gesamtbedarf etwa {tdee} kcal, dein Tagesziel {kcal} kcal.

Der Grundumsatz ist die Energie, die der Körper in völliger Ruhe braucht; Coach schätzt ihn nach der Formel von Mifflin-St Jeor aus Gewicht, Größe, Alter und Geschlecht. Mit der Alltagsaktivität (PAL, etwa 1,4 bei sitzender Arbeit) ergibt sich der Gesamtbedarf; Sport kommt dazu.
Wer weniger isst, als er verbraucht, nimmt ab: Ein Kilo Körperfett entspricht grob 7000 kcal, ein Defizit von 300–500 kcal am Tag bringt also etwa 0,3–0,5 kg pro Woche. Zum Zunehmen braucht es entsprechend einen kleinen Überschuss. Alle Formeln sind Schätzungen – der Gewichtsverlauf über einige Wochen zeigt, wie gut sie für dich passen.

## trinken: Trinken und Elektrolyte
Stichwörter: trinken, wasser, flüssigkeit, flüssigkeitsbedarf, wie viel trinken, getränke, durst, elektrolyte, elektrolyt, natrium, mineralwasser, isotonisch, schwitzen, dehydriert
Art: wissen
Persönlich: Dein Trinkziel im Coach: etwa {water} ml am Tag.

Erwachsene brauchen nach der DGE rund 1,5 Liter aus Getränken am Tag, dazu kommt Wasser aus dem Essen. Bei Hitze, Fieber, viel Bewegung und im Alter (das Durstgefühl lässt nach) mehr. Am besten sind Wasser, Mineralwasser und ungesüßter Tee; Kaffee zählt in üblichen Mengen mit.
Elektrolyte wie Natrium und Kalium gehen mit dem Schweiß verloren. Bei normalem Sport ersetzen Wasser und die nächste Mahlzeit sie gut. Erst bei langen Einheiten (über 60–90 Minuten) oder starkem Schwitzen helfen natriumhaltige Getränke, zum Beispiel eine Saftschorle mit einer Prise Salz.

## darm: Verdauung und Darmflora (Mikrobiom)
Stichwörter: verdauung, darm, darmflora, mikrobiom, darmbakterien, magen, magen-darm, probiotika, präbiotika, fermentiert, sauerkraut, kefir, joghurt, darmgesundheit
Art: wissen

Im Magen wird die Nahrung zerkleinert und angedaut, im Dünndarm werden die Nährstoffe aufgenommen, im Dickdarm verarbeiten Billionen Bakterien die Reste – vor allem Ballaststoffe. Daraus entstehen unter anderem kurzkettige Fettsäuren, die die Darmschleimhaut versorgen.
Eine vielfältige Darmflora mag viele verschiedene Pflanzen (Faustregel: 30 verschiedene in der Woche), Ballaststoffe und fermentierte Lebensmittel wie Joghurt, Kefir oder Sauerkraut. Für Probiotika als Nahrungsergänzung ist ein Nutzen nur in bestimmten Fällen belegt.

## lowcarb: Low-Carb
Stichwörter: low carb, lowcarb, low-carb, wenig kohlenhydrate, kohlenhydratarm, kohlenhydratreduziert, ohne kohlenhydrate, atkins
Art: wissen
Nicht bei: minderjährig, schwanger, untergewicht

Low-Carb heißt weniger Kohlenhydrate – meist 50–130 g am Tag – und dafür mehr Eiweiß, Fett und Gemüse. Vorteile: Viele werden satter, verzichten auf Süßes und Weißmehl und nehmen dadurch ab; Blutzucker und Blutfette können sich verbessern.
Nachteile: Ballaststoffe kommen leicht zu kurz, wenn Vollkorn und Hülsenfrüchte wegfallen, und die Auswahl ist kleiner. Auf lange Sicht nehmen Low-Carb und eine fettarme Ernährung ähnlich viel ab – am besten ist die Form, die man durchhält. Gemüse, Nüsse, Fisch und gute Öle halten Low-Carb ausgewogen.

## keto: Ketogene Ernährung (Keto)
Stichwörter: keto, ketogen, ketogene ernährung, ketose, ketonkörper, keto-grippe, keto grippe
Art: gesundheit
Nicht bei: minderjährig, schwanger, untergewicht

Bei Keto bleiben die Kohlenhydrate so niedrig (meist unter 50 g, oft 20–30 g am Tag), dass der Körper aus Fett Ketonkörper bildet und diese als Energie nutzt. Dafür kommen etwa 70 % der Energie aus Fett.
Manche werden dabei sehr satt und nehmen gut ab. Nachteile: In den ersten Tagen sind Müdigkeit und Kopfweh („Keto-Grippe“) häufig, Obst, Vollkorn und Hülsenfrüchte fallen weg, Ballaststoffe fehlen leicht, und bei manchen steigen die Blutfette. Wer eine Erkrankung hat oder regelmäßig Medikamente nimmt, spricht vorher mit der Ärztin oder dem Arzt.

## fasten: Intervallfasten
Stichwörter: fasten, intervallfasten, intermittierendes fasten, 16:8, 16 8, 14:10, 18:6, 5:2, dinner cancelling, essenspause, fastenzeit, heilfasten
Art: gesundheit
Nicht bei: minderjährig, schwanger, untergewicht

Beim Intervallfasten wechseln Essens- und Fastenzeiten: bei 16:8 isst man in 8 Stunden des Tages, bei 5:2 an zwei Tagen der Woche nur etwa 500–600 kcal. Studien zeigen, dass es vor allem über die geringere Kalorienmenge wirkt – ähnlich gut wie andere Wege, aber für viele leichter durchzuhalten.
Nicht geeignet ist es für Schwangere und Stillende, Kinder und Jugendliche, bei Untergewicht und Essstörungen; wer Medikamente nimmt oder krank ist, bespricht es vorher mit der Ärztin oder dem Arzt. Im Coach gibt es unter „Fasten“ einen Timer mit Erinnerung.

## highprotein: High-Protein (eiweißreiche Ernährung)
Stichwörter: high protein, high-protein, eiweißreich, eiweißreiche ernährung, proteinreich, eiweißdiät, proteindiät, proteinshake, eiweißshake, eiweißpulver
Art: wissen

Eiweißreich zu essen (etwa 1,2–2 g je kg Körpergewicht) macht satt, erhält beim Abnehmen die Muskeln und unterstützt den Muskelaufbau mit Krafttraining. Am besten gelingt das mit normalen Lebensmitteln: Quark, Skyr, Hüttenkäse, Eier, Hülsenfrüchte, Tofu, Fisch und Geflügel.
Shakes und Pulver sind bequem, aber nicht nötig. Achte darauf, dass Gemüse, Vollkorn und Ballaststoffe nicht zu kurz kommen. Bei einer Nierenerkrankung gehört die Eiweißmenge in ärztliche Hände.

## paleo: Paleo (Steinzeit-Ernährung)
Stichwörter: paleo, steinzeit, steinzeiternährung, steinzeitdiät, urzeit-ernährung
Art: wissen

Paleo orientiert sich an dem, was Menschen vor der Landwirtschaft gegessen haben sollen: Fleisch, Fisch, Eier, Gemüse, Obst, Nüsse – ohne Getreide, Hülsenfrüchte, Milchprodukte, Zucker und stark Verarbeitetes.
Der Verzicht auf Fertigprodukte und Zucker ist ein Plus. Nachteile: Vollkorn und Hülsenfrüchte gehören zu den gesündesten Lebensmitteln überhaupt, ohne Milch wird Calcium knapp, und der Fleischanteil ist oft hoch. Dass Paleo anderen ausgewogenen Ernährungsformen überlegen ist, ist wissenschaftlich nicht belegt.

## vegan: Vegan – bedarfsdeckend essen
Stichwörter: vegan, veganer, veganerin, vegane ernährung, pflanzlich, pflanzenbasiert, plant based, ohne tierische produkte
Art: wissen

Vegan geht für Erwachsene gut, wenn man ein paar Nährstoffe im Blick hat. Vitamin B12 muss immer ergänzt werden – es steckt praktisch nur in tierischen Lebensmitteln. Dazu achten: Jod (Jodsalz, eventuell Algen nur in kleinen Mengen), Omega-3 (Lein-, Raps-, Walnussöl, eventuell Algenöl), Eisen und Zink (Hülsenfrüchte, Vollkorn, Samen – Vitamin C dazu verbessert die Aufnahme), Calcium (angereicherte Pflanzendrinks, calciumreiches Mineralwasser, grünes Gemüse) und Vitamin D.
Eiweiß liefern Hülsenfrüchte, Soja, Tofu, Tempeh, Seitan, Nüsse und Vollkorn. Für Schwangere, Stillende, Kinder und Jugendliche nur mit ärztlicher oder fachlicher Begleitung und regelmäßigen Blutwerten.

## vegetarisch: Vegetarisch
Stichwörter: vegetarisch, vegetarier, vegetarierin, vegetarische ernährung, ohne fleisch, fleischlos, ovo-lacto, flexitarisch, flexitarier
Art: wissen

Vegetarisch mit Milch und Eiern ist gut bedarfsdeckend möglich und wird mit viel Gemüse, Vollkorn und Hülsenfrüchten oft gesünder als der Durchschnitt. Eiweiß kommt aus Milchprodukten, Eiern, Hülsenfrüchten, Tofu und Nüssen.
Ein Auge haben sollte man auf Eisen (Hülsenfrüchte, Vollkorn, Haferflocken, dazu Vitamin C), Jod (Jodsalz, Milch) und Omega-3 (Lein- und Rapsöl, Walnüsse). B12 ist mit Milch und Eiern meist ausreichend, bei wenig davon lohnt ein Blick auf die Blutwerte.

## etikett: Nährwerttabelle und Zutatenliste lesen
Stichwörter: nährwerttabelle, nährwertangabe, etikett, verpackung, zutatenliste, zutaten, nutri-score, nutriscore, clean eating, e-nummern, zusatzstoffe, versteckter zucker, je 100 g, kleingedrucktes
Art: wissen

Die Nährwerttabelle steht immer je 100 g bzw. 100 ml – damit lassen sich Produkte vergleichen; Angaben „pro Portion“ beziehen sich oft auf kleine Portionen. Die Zutatenliste ist nach Menge sortiert: Was vorne steht, ist am meisten drin. Allergene sind hervorgehoben.
Zucker versteckt sich hinter vielen Namen: Glukosesirup, Fruktose, Dextrose, Maltodextrin, Gerstenmalzextrakt, Dicksaft, Honig. Eine kurze Zutatenliste mit bekannten Zutaten ist meist ein gutes Zeichen („Clean Eating“), der Nutri-Score (A bis E) hilft beim Vergleich innerhalb einer Produktgruppe.

## abnehmen: Nachhaltig abnehmen
Stichwörter: abnehmen, gewicht verlieren, gewichtsverlust, abspecken, diät, diäten, übergewicht, adipositas, fettabbau, bauchfett, jo-jo, jojo, jo-jo-effekt, schlank werden, wie nehme ich ab
Art: verhalten
Nicht bei: minderjährig, schwanger, untergewicht
Persönlich: Dein Tagesziel im Coach: {kcal} kcal bei einem Gesamtbedarf von etwa {tdee} kcal.

Nachhaltig gelingt Abnehmen mit einem kleinen Defizit (300–500 kcal am Tag) statt einer Crash-Diät – das hält man durch, und der Jo-Jo-Effekt bleibt aus. Hilfreich: viel Gemüse und Eiweiß (machen satt), weniger gesüßte Getränke und Snacks nebenbei, regelmäßige Mahlzeiten, Bewegung im Alltag plus Krafttraining für die Muskeln und genug Schlaf.
Das Gewicht schwankt täglich um ein bis zwei Kilo (vor allem Wasser) – schau auf den Trend über Wochen. Bei starkem Übergewicht (BMI ab 30) kann die Hausärztin oder der Hausarzt mit Programmen helfen, die die Krankenkasse oft bezahlt.

## zunehmen: Zunehmen bei Untergewicht
Stichwörter: zunehmen, gewicht zunehmen, untergewicht, zu dünn, zu leicht, appetitlos, kein appetit, masse aufbauen
Art: gesundheit

Untergewicht (BMI unter 18,5) oder ungewollter Gewichtsverlust sollte ärztlich abgeklärt werden – dahinter kann mehr stecken als zu wenig Essen.
Zum Zunehmen hilft ein kleiner Überschuss von 300–500 kcal am Tag: lieber mehrere kleine Mahlzeiten, energiedichte und gesunde Lebensmittel wie Nüsse, Nussmus, Avocado, Öle, Vollmilchprodukte, Trockenobst und Haferflocken, dazu Krafttraining, damit Muskeln statt nur Fett aufgebaut werden.

## sport: Essen rund um den Sport
Stichwörter: sport, training, vor dem training, nach dem training, während dem training, workout, laufen, joggen, krafttraining, muskelaufbau, muskeln aufbauen, ausdauer, marathon, regeneration, fitness, gym
Art: wissen

Vor dem Training: 2–3 Stunden vorher eine normale Mahlzeit mit Kohlenhydraten, kurz vorher höchstens etwas Leichtes wie eine Banane – wenig Fett und Ballaststoffe, dann drückt nichts. Während: Bei Einheiten unter einer Stunde reicht Wasser; bei längeren helfen 30–60 g Kohlenhydrate pro Stunde (Banane, Saftschorle, Riegel).
Danach: Eiweiß (20–30 g) und Kohlenhydrate unterstützen Regeneration und Muskelaufbau – zum Beispiel Quark mit Obst, ein Käsebrot oder eine normale Mahlzeit. Für Muskelaufbau zählt vor allem, über den Tag genug Eiweiß zu essen und regelmäßig zu trainieren.

## bewegung: Bewegung und Schritte
Stichwörter: bewegung, schritte, schrittziel, 10000 schritte, zehntausend schritte, spazieren, spazierengehen, gehen, aktiv, aktivität, sitzen, inaktiv, ausdauer, wie viel bewegung
Art: wissen

Die WHO empfiehlt Erwachsenen 150–300 Minuten mäßige Bewegung pro Woche (zügiges Gehen, Radfahren) oder 75–150 Minuten intensive, dazu an zwei Tagen Krafttraining. Jede Bewegung zählt, auch zehn Minuten am Stück.
Bei Schritten zeigen Studien schon ab etwa 7000 am Tag einen deutlichen Nutzen; 10.000 sind ein gutes Ziel, aber keine magische Grenze. Langes Sitzen zwischendurch unterbrechen hilft zusätzlich. Im Coach kann das Schrittziel sich langsam an dein Niveau anpassen.

## schwangerschaft: Schwangerschaft und Stillzeit
Stichwörter: schwanger, schwangerschaft, stillzeit, stillen, kinderwunsch, baby, mutter, listerien, toxoplasmose, rohmilch, schwangere
Art: gesundheit

In der Schwangerschaft steigt der Energiebedarf erst ab dem zweiten Drittel und nur wenig (etwa 250 kcal, im letzten Drittel etwa 500 kcal) – „für zwei essen“ gilt nicht, wohl aber für Vitamine und Mineralstoffe. Folsäure sollte schon ab Kinderwunsch ergänzt werden; ob Jod, Eisen oder anderes dazukommt, entscheiden Frauenärztin, Frauenarzt oder Hebamme.
Meiden: Alkohol ganz, rohes Fleisch und rohen Fisch, Rohmilch und Rohmilchkäse, rohe Eier (wegen Listerien, Toxoplasmose, Salmonellen); Kaffee höchstens 2–3 Tassen. Diäten und Fasten sind in Schwangerschaft und Stillzeit nicht vorgesehen. In der Stillzeit ist der Bedarf leicht erhöht, viel trinken hilft.

## kinder: Kinder und Jugendliche
Stichwörter: kinder, kind, jugendliche, jugendlicher, teenager, schulkind, kleinkind, pausenbrot, schulessen, wachstum, pubertät, kinderernährung
Art: wissen

Kinder und Jugendliche wachsen – sie brauchen regelmäßige Mahlzeiten mit allem, was dazugehört, und keine Diäten. Gut sind Wasser und ungesüßter Tee als Getränke, Gemüse und Obst zu jeder Mahlzeit, Vollkorn, Milchprodukte, wenig Süßes und Gesüßtes. Kinder essen nach Hunger – Zwang („Teller leer“) und Süßes als Belohnung sind ungünstig.
Gemeinsame Mahlzeiten und Erwachsene als Vorbild wirken mehr als Regeln. Macht ihr euch Sorgen ums Gewicht oder ums Essverhalten eures Kindes, ist die Kinderärztin oder der Kinderarzt die richtige Adresse.

## senioren: Essen im Alter
Stichwörter: alter, ältere, senioren, seniorin, rentner, ab 65, ab 70, im alter, älter werden, muskelabbau, sarkopenie
Art: wissen

Im Alter sinkt der Energiebedarf, der Bedarf an Nährstoffen aber nicht – also lieber nährstoffreich als viel essen. Eiweiß wird wichtiger (etwa 1,0 g je kg), um Muskeln zu erhalten, am besten zusammen mit Kraftübungen.
Das Durstgefühl lässt nach: bewusst trinken, etwa ein Glas zu jeder Mahlzeit und zwischendurch. Vitamin D (wenig Sonne, die Haut bildet weniger), Calcium und B12 verdienen einen Blick – ob etwas ergänzt werden sollte, klärt die Hausärztin oder der Hausarzt. Bei kleinem Appetit helfen mehrere kleine Mahlzeiten.

## emotional: Essen aus Frust, Stress oder Langeweile
Stichwörter: emotionales essen, frustessen, stressessen, aus frust, aus langeweile, langeweile, stress, frust, trösten, belohnen, nervennahrung, kummerspeck, essen aus gefühlen
Art: verhalten

Fast alle essen manchmal nicht aus Hunger, sondern weil sie gestresst, traurig, müde oder gelangweilt sind – Essen beruhigt kurz. Das ist menschlich und kein Versagen.
Hilfreich ist ein kurzer Check vor dem Griff zum Snack: Kommt der Hunger langsam und würde auch ein Brot satt machen (körperlich)? Oder plötzlich und nur auf etwas Bestimmtes (Gefühl)? Bei Gefühlshunger hilft oft, das Gefühl zu benennen und etwas anderes zu probieren – kurz rausgehen, jemandem schreiben, ein Glas Wasser, fünf Minuten Pause. Wenn du magst: Was ist es bei dir meistens – Stress, Langeweile oder etwas anderes?

## gewohnheiten: Gewohnheiten ändern
Stichwörter: gewohnheit, gewohnheiten, routine, routinen, durchhalten, dranbleiben, motivation, disziplin, neue gewohnheit, vorsatz, vorsätze, anfangen, schweinehund, inneren schweinehund
Art: verhalten

Neue Gewohnheiten gelingen mit kleinen, konkreten Schritten statt großer Vorsätze: lieber „zum Frühstück eine Handvoll Beeren“ als „ab jetzt gesund“. Koppel das Neue an etwas, das du sowieso tust („Nach dem Zähneputzen trinke ich ein Glas Wasser“), und plane mit Wenn-dann-Sätzen („Wenn ich nachmittags Hunger habe, nehme ich Joghurt statt Keksen“).
Mach es dir leicht: Obst sichtbar hinstellen, Süßes nicht vorrätig haben. Bis etwas automatisch läuft, dauert es im Schnitt etwa zwei Monate – ein ausgelassener Tag macht dabei nichts kaputt. Was wäre ein kleiner erster Schritt, der für dich gut passen würde?

## plateau: Stillstand beim Abnehmen (Plateau)
Stichwörter: plateau, stillstand, nehme nicht mehr ab, nicht mehr ab, nehme nicht ab, nimmt nicht ab, waage bewegt sich nicht, gewicht stagniert, stagniert, stagnation, keine fortschritte
Art: verhalten
Nicht bei: minderjährig, schwanger, untergewicht

Ein Stillstand gehört fast immer dazu. Das Gewicht schwankt durch Wasser (Salz, Kohlenhydrate, Zyklus, Training) oft so stark, dass es Fettverlust tagelang überdeckt – schau auf den Trend über zwei bis drei Wochen und miss zusätzlich den Bauchumfang.
Mit sinkendem Gewicht sinkt auch der Bedarf; Coach rechnet dein Ziel mit dem neuesten Gewicht nach. Prüf ehrlich, ob sich kleine Extras eingeschlichen haben (Getränke, Öl, Snacks nebenbei), und ob Schlaf und Bewegung stimmen. Das Wichtigste: dranbleiben – du hast schon viel geschafft.

## rueckfall: Ausrutscher und Rückfälle
Stichwörter: rückfall, rückfälle, ausrutscher, gesündigt, sünde, versagt, zu viel gegessen, schlechtes gewissen, heißhungerattacke, alles kaputt, wieder angefangen, frustriert, aufgeben
Art: verhalten

Ein Ausrutscher ist kein Rückfall und schon gar kein Scheitern – er gehört zu jeder Veränderung dazu. Ein einzelner Tag ändert am Verlauf über Wochen kaum etwas. Ungünstig ist nur das „Jetzt ist eh alles egal“-Denken.
Mach einfach mit der nächsten Mahlzeit normal weiter, ohne zu hungern oder zu kompensieren. Und schau neugierig statt streng hin: Was war heute anders – Stress, Hunger, eine Einladung? Daraus lässt sich etwas für das nächste Mal lernen.

## heisshunger: Hunger und Heißhunger
Stichwörter: heißhunger, hunger, ständig hunger, immer hunger, hungrig, süßhunger, lust auf süßes, nicht satt, sättigung, satt werden, snacken, naschen
Art: verhalten

Heißhunger entsteht oft durch lange Pausen, zu kleine oder zu einseitige Mahlzeiten, wenig Schlaf oder Stress. Was länger satt macht: Eiweiß, Ballaststoffe und Volumen (Gemüse, Vollkorn, Hülsenfrüchte, Quark, Eier) – und regelmäßig essen, bevor der Hunger übermächtig wird.
Manchmal ist es auch Durst. Gegen Lust auf Süßes hilft eine geplante kleine Portion besser als ein strenges Verbot. Wann kommt der Heißhunger bei dir meistens?

## schlaf: Schlaf und Essen
Stichwörter: schlaf, schlafen, müde, müdigkeit, schlafmangel, zu wenig schlaf, schlafqualität, einschlafen, spät essen, abends essen
Art: wissen

Wer zu wenig schläft, hat am nächsten Tag meist mehr Hunger und mehr Lust auf Süßes und Fettiges – die Hormone für Hunger und Sättigung geraten aus dem Takt. 7–9 Stunden sind für Erwachsene ein guter Bereich.
Für einen guten Schlaf hilft eine leichte letzte Mahlzeit zwei bis drei Stunden vor dem Schlafen, Kaffee nur bis zum frühen Nachmittag und wenig Alkohol (er stört den Tiefschlaf). Mit einer Uhr zeigt Coach deinen Schlaf in der Statistik.

## laktose: Laktoseintoleranz
Stichwörter: laktose, laktoseintoleranz, lactose, milchzucker, laktosefrei, milchunverträglichkeit, laktase
Art: gesundheit

Bei Laktoseintoleranz fehlt es an dem Enzym, das Milchzucker spaltet. Wie viel vertragen wird, ist sehr verschieden – meist geht etwas. Ob eine Laktoseintoleranz vorliegt, klärt die Ärztin oder der Arzt, zum Beispiel mit einem Atemtest.
Gereifter Hartkäse (Bergkäse, Parmesan, Emmentaler, Gouda) ist praktisch laktosefrei, Joghurt und Kefir werden oft besser vertragen, dazu gibt es laktosefreie Milchprodukte. Damit Calcium nicht zu kurz kommt: Käse, laktosefreie Produkte, calciumreiches Mineralwasser, grünes Gemüse.

## fruktose: Fruktosemalabsorption
Stichwörter: fruktose, fructose, fruchtzucker, fruktosemalabsorption, fruktoseintoleranz, sorbit, sorbitol
Art: gesundheit

Bei der Fruktosemalabsorption nimmt der Dünndarm Fruchtzucker nur begrenzt auf. Wichtig: Davon zu unterscheiden ist die sehr seltene angeborene Fruktoseintoleranz, bei der Fruktose streng gemieden werden muss – die Diagnose gehört in ärztliche Hände.
Bei der Malabsorption hilft meist ein Vorgehen in Stufen: kurz fruktosearm essen, dann austesten, was in welcher Menge geht. Obst mit viel Fruktose (Apfel, Birne, Trockenobst), Säfte und Honig sind oft schwieriger, Traubenzucker in der Mahlzeit kann die Aufnahme verbessern, Sorbit (Zuckeraustauschstoff) verschlechtert sie. Eine Ernährungsfachkraft begleitet das gut.

## histamin: Histaminintoleranz
Stichwörter: histamin, histaminintoleranz, histaminarm, dao, diaminoxidase
Art: gesundheit

Histaminintoleranz ist umstritten und schwer festzustellen – die Abklärung gehört zu Ärztin oder Arzt, am besten mit einer Ernährungsfachkraft. Histamin entsteht vor allem, wenn Lebensmittel reifen, gären oder lange lagern.
Viel davon steckt in lange gereiftem Käse, Rohwurst und Salami, Sauerkraut, Rotwein und Sekt, Fischkonserven und nicht ganz frischem Fisch, auch Tomaten und Spinat werden oft schlecht vertragen. Frisch kochen und Reste rasch essen hilft. Strenge Verbote auf Dauer sind meist nicht nötig – das Ziel ist, die eigene Grenze zu kennen.

## zoeliakie: Zöliakie und glutenfrei
Stichwörter: zöliakie, zoeliakie, gluten, glutenfrei, glutenunverträglichkeit, glutensensitivität, weizen, weizenunverträglichkeit, sprue
Art: gesundheit

Zöliakie ist eine Autoimmunerkrankung: Schon kleine Mengen Gluten schädigen den Dünndarm. Die einzige Behandlung ist lebenslang streng glutenfrei – kein Weizen, Dinkel, Roggen, Gerste, Grünkern, auch nicht als Spuren; Hafer nur als ausdrücklich glutenfreier Hafer.
Wichtig: Die Diagnose stellt die Ärztin oder der Arzt, solange man noch Gluten isst – wer vorher umstellt, verfälscht die Tests. Natürlich glutenfrei sind Reis, Mais, Hirse, Buchweizen, Quinoa, Kartoffeln, Hülsenfrüchte, Obst, Gemüse, Fleisch, Fisch und Milch. Ohne Zöliakie bringt glutenfreie Ernährung keinen gesundheitlichen Vorteil.

## allergene: Die 14 Hauptallergene
Stichwörter: allergene, allergen, allergie, allergien, lebensmittelallergie, nussallergie, erdnussallergie, kennzeichnung, 14 allergene
Art: gesundheit

In der EU müssen 14 Hauptallergene in der Zutatenliste hervorgehoben sein: glutenhaltiges Getreide, Krebstiere, Eier, Fisch, Erdnüsse, Soja, Milch (auch Laktose), Schalenfrüchte (Nüsse), Sellerie, Senf, Sesam, Schwefeldioxid und Sulfite, Lupinen sowie Weichtiere.
Ob und worauf man allergisch ist, klärt die Ärztin oder der Arzt. Im Coach-Profil kannst du deine Allergene eintragen.

## diabetes: Ernährung bei Diabetes Typ 2
Stichwörter: diabetes, diabetes typ 2, typ 2, zucker krankheit, zuckerkrankheit, diabetiker, blutzucker, hba1c, prädiabetes, insulinresistenz
Art: gesundheit

Bei Diabetes Typ 2 kann die Ernährung viel bewirken – das Wichtigste bespricht man mit der Ärztin, dem Arzt oder einer Diabetesberatung. Allgemein gilt: Abnehmen bei Übergewicht verbessert die Blutzuckerwerte oft deutlich. Günstig sind Vollkorn, Hülsenfrüchte, Gemüse, Nüsse und Ballaststoffe; gesüßte Getränke, Süßigkeiten und Weißmehl sind ungünstig. Bewegung senkt den Blutzucker zusätzlich.
Wer Medikamente gegen Diabetes nimmt, ändert Ernährung, Fasten oder Low-Carb nur nach Absprache mit dem Arzt.

## blutdruck: Ernährung bei Bluthochdruck
Stichwörter: blutdruck, bluthochdruck, hypertonie, hoher blutdruck, dash, dash-diät
Art: gesundheit

Bei Bluthochdruck gehört die Behandlung zu Ärztin oder Arzt; die Ernährung kann unterstützen. Allgemein hilft: weniger Salz (die DGE empfiehlt höchstens 6 g am Tag – das meiste steckt in Brot, Wurst, Käse und Fertiggerichten), viel Gemüse, Obst und Hülsenfrüchte (Kalium), wenig Alkohol, bei Übergewicht abnehmen und regelmäßig bewegen.
Die sogenannte DASH-Ernährung fasst das zusammen und senkt den Blutdruck in Studien messbar.

## blutfette: Ernährung bei erhöhten Blutfetten
Stichwörter: cholesterin, ldl, hdl, blutfette, triglyceride, fettstoffwechsel, fettstoffwechselstörung, hohes cholesterin
Art: gesundheit

Erhöhte Blutfette behandelt die Ärztin oder der Arzt; die Ernährung kann unterstützen. Für das LDL-Cholesterin hilft vor allem, gesättigte Fette (Butter, Sahne, fettes Fleisch, Wurst, Käse) durch ungesättigte (Raps-, Olivenöl, Nüsse) zu ersetzen und mehr Ballaststoffe zu essen – besonders Hafer und Hülsenfrüchte.
Bei hohen Triglyceriden wirken weniger Zucker, Weißmehl und Alkohol sowie Abnehmen bei Übergewicht. Fetter Seefisch ein- bis zweimal pro Woche ist günstig.

## essstoerung: Essstörungen
Stichwörter: essstörung, essstörungen, magersucht, anorexie, bulimie, binge eating, binge-eating, essattacke, essattacken, fressattacke, erbrechen nach dem essen
Art: gesundheit

Essstörungen wie Magersucht, Bulimie oder Binge-Eating sind ernsthafte Erkrankungen, und sie sind gut behandelbar – je früher, desto besser. Dafür sind Ärztinnen, Ärzte, Psychotherapeutinnen und Beratungsstellen für Essstörungen da, nicht ein Ernährungs-Coach.
Wenn du dir um dich oder jemanden Sorgen machst: Sprich es an und hol dir Unterstützung bei der Hausärztin, dem Hausarzt oder einer Beratungsstelle für Essstörungen. Du musst das nicht allein schaffen.

## grenzen: Wann zur Fachkraft?
Stichwörter: ernährungsberatung, ernährungsberater, ernährungsberaterin, diätassistent, diätassistentin, ökotrophologe, ernährungstherapie, ernährungsfachkraft, wer hilft, krankenkasse
Art: wissen

Ich kann allgemeines Wissen geben und dir helfen, dein Essen zu überblicken. Bei Krankheiten (etwa Diabetes, Nierenerkrankungen, Unverträglichkeiten), Essstörungen, in der Schwangerschaft, bei Kindern mit Gewichtssorgen und bei allen Fragen zu Beschwerden oder Medikamenten gehört die Beratung zu Ärztin, Arzt oder einer qualifizierten Ernährungsfachkraft (Diätassistenz, Ökotrophologie).
Viele Krankenkassen bezuschussen eine Ernährungsberatung, wenn die Ärztin oder der Arzt sie für nötig hält – fragen lohnt sich.

## tagebuch: Ernährungstagebuch und Messen
Stichwörter: ernährungstagebuch, essensprotokoll, protokoll, aufschreiben, tracken, tracking, kalorien zählen, messen, wiegen, wie oft wiegen, taillenumfang, bauchumfang, körperfett, ist-analyse
Art: wissen

Ein Ernährungstagebuch zeigt ehrlich, wie man isst – am aussagekräftigsten über eine Woche mit Wochenende, zeitnah eingetragen und mit Getränken und Kleinigkeiten nebenbei. Im Coach geht das am schnellsten über „Zuletzt“, Vorlagen, „Wie gestern“ oder „Beschreiben“.
Fürs Gewicht reicht einmal pro Woche, immer zur gleichen Zeit (morgens, nach dem Toilettengang). Der Bauchumfang auf Nabelhöhe ergänzt die Waage gut: Ein erhöhtes Risiko für Herz und Stoffwechsel gilt bei Frauen ab etwa 80 cm, bei Männern ab etwa 94 cm.

## planen: Alltagstauglich planen
Stichwörter: planen, ernährungsplan, essensplan, wochenplan, meal prep, vorkochen, mealprep, einkaufsplanung, was soll ich essen, tellermodell, teller
Art: wissen

Ein guter Plan passt zum eigenen Alltag statt zu einem Ideal. Ein einfaches Muster für den Teller: die Hälfte Gemüse oder Salat, ein Viertel Eiweiß (Hülsenfrüchte, Fisch, Fleisch, Ei, Tofu, Quark), ein Viertel Vollkorn oder Kartoffeln, dazu etwas gutes Öl.
Hilfreich sind ein paar Lieblingsgerichte, die schnell gehen, einmal kochen und zweimal essen, eine Einkaufsliste und etwas Spielraum – wer an den meisten Tagen gut isst, darf den Rest locker sehen. Im Coach unter „Planen“ entsteht aus deinen Rezepten ein Essensplan samt Einkaufsliste.

## supplemente: Nahrungsergänzungsmittel
Stichwörter: nahrungsergänzung, nahrungsergänzungsmittel, supplemente, supplement, supplementieren, multivitamin, vitamintabletten, kapseln, pulver, brauche ich vitamine, ergänzen
Art: gesundheit

Wer ausgewogen isst, braucht meist keine Nahrungsergänzung – sie ersetzt keine gute Ernährung, und manches schadet in hohen Mengen (etwa Vitamin A, Vitamin D, Eisen). Sinnvoll ist sie in bestimmten Lagen: Vitamin B12 bei veganer Ernährung, Folsäure bei Kinderwunsch und in der frühen Schwangerschaft, bei nachgewiesenem Mangel.
Ob und was du ergänzen solltest, klären Ärztin, Arzt oder Apotheke am besten mit einer Blutuntersuchung – dazu gebe ich keine Empfehlung im Einzelfall.

## bmi: BMI und Taillenumfang
Stichwörter: bmi, body mass index, body-mass-index, normalgewicht, idealgewicht, übergewichtig, untergewichtig, wie viel sollte ich wiegen
Art: wissen
Persönlich: Mit deinem Profil liegt dein BMI bei etwa {bmi}.

Der BMI ist das Gewicht in Kilogramm geteilt durch die Größe in Metern zum Quadrat. Für Erwachsene gilt nach der WHO: unter 18,5 Untergewicht, 18,5 bis 24,9 Normalgewicht, 25 bis 29,9 Übergewicht, ab 30 Adipositas.
Er ist nur ein grobes Maß: Muskeln zählen wie Fett, für Ältere sind etwas höhere Werte in Ordnung, und für Kinder und Jugendliche gelten eigene Kurven. Der Taillenumfang sagt mehr über das Risiko aus, weil vor allem Bauchfett zählt.

## obstgemuese: Obst und Gemüse
Stichwörter: obst, gemüse, 5 am tag, fünf am tag, salat, rohkost, frisches gemüse, tiefkühlgemüse, wie viel obst, wie viel gemüse, smoothie
Art: wissen

Die DGE empfiehlt mindestens drei Portionen Gemüse und zwei Portionen Obst am Tag, zusammen etwa 650 g – eine Portion ist ungefähr eine Handvoll. Gemüse darf dabei gern die Hauptrolle spielen.
Je bunter, desto besser: Jede Farbe bringt andere Pflanzenstoffe mit. Tiefkühlgemüse ist genauso wertvoll wie frisches. Saft und Smoothies zählen höchstens gelegentlich als eine Portion, weil Ballaststoffe fehlen und der Zucker schnell ins Blut geht.

## salz: Salz
Stichwörter: salz, kochsalz, natrium, salzarm, salzig, jodsalz, wie viel salz
Art: wissen

Die DGE empfiehlt höchstens 6 g Salz am Tag, etwa einen gestrichenen Teelöffel; die meisten essen deutlich mehr. Rund drei Viertel stecken in verarbeiteten Lebensmitteln: Brot und Brötchen, Wurst, Käse, Fertiggerichte, Saucen und Knabbereien.
Wenn du salzt, dann mit Jodsalz (mit Fluorid). Kräuter, Zitrone und Gewürze geben Geschmack mit weniger Salz – der Gaumen gewöhnt sich in ein paar Wochen daran.

## zucker: Zucker und Süßes
Stichwörter: zucker, zuckerkonsum, süßigkeiten, süßes, schokolade, zuckerfrei, süßstoff, süßungsmittel, softdrinks, limo, cola, zuckerverzicht
Art: wissen

WHO und DGE raten, freien Zucker (zugesetzt sowie in Honig, Sirup und Saft) auf weniger als 10 % der Energie zu begrenzen – bei 2000 kcal höchstens 50 g, also etwa 12 Teelöffel; weniger ist besser. Den größten Anteil liefern oft Getränke: Ein Glas Limo oder Saft bringt schnell 20–25 g.
Ganz verzichten muss niemand – bewusst genießen und das Süße eher nach einer Mahlzeit als zwischendurch essen ist ein guter Weg. Süßstoffe können beim Umstieg helfen, sind aber kein Muss.

## alkohol: Alkohol
Stichwörter: alkohol, bier, wein, sekt, schnaps, cocktail, alkoholfrei, kater, wie viel alkohol
Art: wissen

Alkohol hat 7 kcal je Gramm – ein großes Bier oder ein Viertel Wein liefert rund 150–200 kcal – und macht Appetit. Die DGE sagt seit 2024 klar: Es gibt keine gesundheitlich unbedenkliche Menge; am besten ist gar kein Alkohol. Wer trinkt, trinkt wenig, nicht täglich und nie in Schwangerschaft und Stillzeit.
Alkoholfreie Biere und Mischgetränke mit viel Wasser sind gute Alternativen.

## koffein: Kaffee und Koffein
Stichwörter: kaffee, koffein, coffein, espresso, energy drink, energydrink, wie viel kaffee, tee, grüner tee, schwarzer tee, mate
Art: wissen

Für gesunde Erwachsene gelten laut EFSA bis zu 400 mg Koffein am Tag als unbedenklich, in der Schwangerschaft bis 200 mg. Eine Tasse Filterkaffee hat etwa 80–100 mg, ein Espresso etwa 60–80 mg, eine Dose Energy-Drink rund 80 mg, schwarzer Tee etwa 30–50 mg.
Kaffee zählt zur Trinkmenge. Am Abend kann Koffein den Schlaf stören – wer empfindlich ist, trinkt den letzten Kaffee am frühen Nachmittag. Energy-Drinks sind für Kinder und Jugendliche nicht geeignet.
