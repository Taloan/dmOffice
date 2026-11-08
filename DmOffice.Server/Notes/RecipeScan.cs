using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DmOffice.Chat;
using DmOffice.Coach;
using DmOffice.Core;
using DmOffice.Search;
using SkiaSharp;

namespace DmOffice.Notes;

/// <summary>
/// Rezept-Scan (docs/rezept-scan-konzept.md): Kochbuchseiten (Fotos, Scans, PDF) per Tesseract lesen und daraus einen Entwurf
/// machen - Titel, Portionen, Zeiten, Zutaten, Schritte, Tipp. Nach der Prüfansicht entsteht die Notiz mit RecipeImport.ToNote,
/// also in derselben Gestalt wie ein Rezept per Link (Essensplan, Einkaufsliste und Coach erkennen sie ohne Änderung).
/// Die Erkennung sind Regeln auf Zeilen mit Position und Schrifthöhe (TSV von Tesseract); auf Wunsch hilft die KI mit dem Text.
/// </summary>
public static class RecipeScan
{
    /// <summary>Eine erkannte Zeile. Block: Absatz bei Tesseract bzw. durch Leerzeilen getrennt; Height: Schrifthöhe.</summary>
    public sealed record Line(int Page, int Block, double Left, double Top, double Height, string Text);

    /// <summary>
    /// Entwurf für die Prüfansicht. Zutaten und Schritte als Text: eine Zeile je Zutat bzw. Schritt; eine kurze Zeile mit Doppelpunkt
    /// am Ende ist ein Unterabschnitt („Für den Teig:“).
    /// </summary>
    public sealed record Draft(string Title, string Yield, string PrepTime, string CookTime, string TotalTime, string Description,
        string Ingredients, string Steps, string Tip, string Source, string Calories = "");

    /// <summary>Ergebnis: ein Entwurf je Rezept (mehrere Rezepte auf einer Seite werden getrennt). Images: Seiten verkleinert für die KI.</summary>
    public sealed record Result(List<Draft> Drafts, string Text, List<string> Hints)
    {
        [System.Text.Json.Serialization.JsonIgnore] public List<byte[]> Images { get; init; } = [];
    }

    public const int MaxPages = 10;
    public const long MaxPageBytes = 15 * 1024 * 1024;
    private static readonly TimeSpan PageTimeout = TimeSpan.FromSeconds(60);
    // Texterkennung ist teuer: nie mehr als zwei Scans gleichzeitig
    private static readonly SemaphoreSlim Gate = new(2);

    // ---------------- Seiten lesen ----------------

    /// <summary>Seiten erkennen und den Entwurf bauen. Bilder werden aufgerichtet und verkleinert, PDFs als Bilder gerendert.</summary>
    public static async Task<Result> ScanAsync(IReadOnlyList<(string Name, byte[] Data)> files, CancellationToken ct)
    {
        if (!Ocr.Available) throw new InvalidOperationException(Ocr.Current.Reason ?? "Keine Texterkennung.");
        var hints = new List<string>();
        var lines = new List<Line>();
        var small = new List<byte[]>();
        await Gate.WaitAsync(ct);
        try
        {
            await Task.Run(() =>
            {
                var page = 0;
                foreach (var (name, data) in files)
                {
                    var images = IsPdf(data) ? Ocr.PdfPages(data, MaxPages, PageTimeout, ct) : [data];
                    foreach (var img in images)
                    {
                        if (++page > MaxPages) { hints.Add($"Mehr als {MaxPages} Seiten – die übrigen wurden nicht gelesen."); return; }
                        var jpeg = Jpeg(img, 3500, 90);
                        if (jpeg == null) { hints.Add($"Seite {page} ({name}) ist kein lesbares Bild."); continue; }
                        if (Jpeg(img, 1600, 80) is { } s) small.Add(s);
                        try { lines.AddRange(FromTsv(Ocr.ImageTsv(jpeg, ".jpg", PageTimeout, ct), page)); }
                        catch (TimeoutException) { hints.Add($"Seite {page}: Texterkennung dauerte zu lange, die Seite fehlt."); }
                    }
                }
            }, ct);
        }
        finally { Gate.Release(); }
        var text = RawText(lines);
        if (text.Count(char.IsLetter) < 40)
        {
            hints.Insert(0, "Auf den Seiten wurde kaum Text erkannt. Bei besserem Licht und ohne Spiegelung neu aufnehmen.");
            return new Result([Empty with { Description = text.Trim() }], text, hints) { Images = small };
        }
        var (drafts, found) = AnalyzeAll(lines);
        hints.AddRange(found);
        return new Result(drafts, text, hints) { Images = small };
    }

    private static readonly Draft Empty = new("", "", "", "", "", "", "", "", "", "");

    private static bool IsPdf(byte[] d) => d.Length > 4 && d[0] == '%' && d[1] == 'P' && d[2] == 'D' && d[3] == 'F';

    /// <summary>Bild aufrecht (EXIF), höchstens maxSide Pixel an der langen Seite, als JPEG; null, wenn es kein Bild ist.</summary>
    internal static byte[]? Jpeg(byte[] data, int maxSide, int quality)
    {
        using var codec = SKCodec.Create(new MemoryStream(data));
        if (codec == null) return null;
        using var raw = SKBitmap.Decode(codec);
        if (raw == null) return null;
        var up = DmOffice.Dav.PhotoEdit.Upright(raw, codec.EncodedOrigin);
        SKBitmap? scaled = null;
        try
        {
            var src = up;
            var s = (double)maxSide / Math.Max(up.Width, up.Height);
            if (s < 1)
            {
                scaled = new SKBitmap(new SKImageInfo(Math.Max(1, (int)(up.Width * s)), Math.Max(1, (int)(up.Height * s)), SKColorType.Rgba8888, SKAlphaType.Premul));
                using var c = new SKCanvas(scaled);
                c.Clear(SKColors.White);
                c.Scale((float)s);
                using var img0 = SKImage.FromBitmap(up);
                c.DrawImage(img0, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                c.Flush();
                src = scaled;
            }
            using var img = SKImage.FromBitmap(src);
            using var enc = img.Encode(SKEncodedImageFormat.Jpeg, quality);
            return enc?.ToArray();
        }
        finally
        {
            scaled?.Dispose();
            if (!ReferenceEquals(up, raw)) up.Dispose();
        }
    }

    /// <summary>
    /// TSV von Tesseract zu Zeilen: Wörter je Zeile (Block, Absatz, Zeile) zusammen; eine große Lücke in der Zeile (zwei Spalten
    /// einer Zutatentabelle) trennt sie. Block = Absatz.
    /// </summary>
    public static List<Line> FromTsv(string tsv, int page)
    {
        var words = new List<(int Block, int Line, double L, double T, double W, double H, string Text)>();
        foreach (var row in tsv.Split('\n'))
        {
            var c = row.TrimEnd('\r').Split('\t');
            if (c.Length < 12 || c[0] != "5") continue;
            var text = c[11].Trim();
            if (text.Length == 0) continue;
            if (!int.TryParse(c[2], out var block) || !int.TryParse(c[3], out var par) || !int.TryParse(c[4], out var ln)) continue;
            double.TryParse(c[6], NumberStyles.Float, CultureInfo.InvariantCulture, out var l);
            double.TryParse(c[7], NumberStyles.Float, CultureInfo.InvariantCulture, out var t);
            double.TryParse(c[8], NumberStyles.Float, CultureInfo.InvariantCulture, out var w);
            double.TryParse(c[9], NumberStyles.Float, CultureInfo.InvariantCulture, out var h);
            words.Add((block * 1000 + par, ln, l, t, w, h, text));
        }
        var result = new List<Line>();
        foreach (var g in words.GroupBy(w => (w.Block, w.Line)))
        {
            var ws = g.OrderBy(w => w.L).ToList();
            var height = Median(ws.Select(w => w.H));
            var part = new List<(double L, double T, double W, double H, string Text)>();
            void Flush()
            {
                if (part.Count == 0) return;
                result.Add(new Line(page, g.Key.Block, part[0].L, part.Min(p => p.T), Median(part.Select(p => p.H)), string.Join(' ', part.Select(p => p.Text))));
                part.Clear();
            }
            foreach (var w in ws)
            {
                if (part.Count > 0 && w.L - (part[^1].L + part[^1].W) > Math.Max(3 * height, 40)) Flush();
                part.Add((w.L, w.T, w.W, w.H, w.Text));
            }
            Flush();
        }
        return result;
    }

    /// <summary>Klartext zu Zeilen (Tests, KI-Antwort): Leerzeile = neuer Absatz, Seitenvorschub = neue Seite, alle gleich hoch.</summary>
    public static List<Line> FromText(string text)
    {
        var result = new List<Line>();
        int page = 1, block = 0, n = 0;
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            if (raw.Contains('\f')) { page++; block++; n = 0; continue; }
            if (raw.Trim().Length == 0) { block++; continue; }
            // Einzug (zwei Leerzeichen): wie eine zweite Spalte daneben
            result.Add(new Line(page, block, raw.Length - raw.TrimStart().Length, n++ * 20, 10, raw.Trim()));
        }
        return result;
    }

    private static double Median(IEnumerable<double> values)
    {
        var v = values.OrderBy(x => x).ToList();
        return v.Count == 0 ? 0 : v[v.Count / 2];
    }

    private static string RawText(List<Line> lines)
    {
        var sb = new StringBuilder();
        Line? prev = null;
        foreach (var l in lines)
        {
            if (prev != null && (prev.Page != l.Page || prev.Block != l.Block)) sb.Append('\n');
            sb.Append(l.Text).Append('\n');
            prev = l;
        }
        return sb.ToString().TrimEnd();
    }

    // ---------------- Erkennung ----------------

    private enum Mode { Auto, Ingredients, Steps, Tip }

    private static readonly Regex IngredientsHeading = new(@"^(?:Zutaten|Ingredients|Das brauchen Sie|Sie brauchen|Das braucht man|Einkaufsliste)\b\s*(?<rest>.*)$", RegexOptions.IgnoreCase);
    private static readonly Regex StepsHeading = new(@"^(?:Zubereitung|Anleitung|So geht['’`´]?s|Und so geht['’`´]?s|So wird['’`´]?s gemacht|Arbeitsschritte|Instructions|Method|Directions|Preparation)\s*[:.]?\s*(?<rest>.*)$", RegexOptions.IgnoreCase);
    private static readonly Regex TipHeading = new(@"^(?:Mein Tipp|Extra-Tipp|Küchentipp|Tipps?|Tip|Hinweis|Variante|Varianten|Info)\b\s*[:!.]?\s*(?<rest>.*)$", RegexOptions.IgnoreCase);

    private static readonly Regex TimeRx = new(
        @"(?<label>Zubereitungszeit|Zubereitung|Arbeitszeit|Vorbereitungszeit|Vorbereitung|Zeitaufwand|Prep(?:aration)? time|Koch-/Backzeit|Backzeit|Backen|Kochzeit|Garzeit|Bratzeit|Cook(?:ing)? time|Ruhezeit|Kühlzeit|Wartezeit|Gehzeit|Marinierzeit|Gesamtzeit|Total time)\s*:?\s*(?:ca\.|etwa|circa)?\s*(?<value>\d+(?:[.,]\d+)?(?:\s*(?:-|–|bis)\s*\d+)?\s*(?:Std\.?|Stunden?|h\b|Min\.?|Minuten|min\b)(?:\s*(?:und\s*)?\d+\s*(?:Min\.?|Minuten|min\b))?)",
        RegexOptions.IgnoreCase);

    private const string NumWords = "ein|eine|einen|zwei|drei|vier|fünf|sechs|sieben|acht|neun|zehn|zwölf";
    private static readonly Regex YieldFor = new(@"^(?:Zutaten\s+)?(?:für|fuer|fur|reicht für|ergibt|menge|portionen|personen|servings|serves|makes)\s*:?\s*(?:ca\.|etwa)?\s*(?<n>\d+|" + NumWords + @")\b\s*(?<rest>.*)$", RegexOptions.IgnoreCase);
    private static readonly Regex YieldPlain = new(@"^(?<n>\d+)\s*(?<rest>Portionen|Personen|Pers\.|Stück)\s*:?$", RegexOptions.IgnoreCase);
    private static readonly Regex StepNumber = new(@"^(?:Schritt\s+)?(?<n>\d{1,2})\s*[.):]\s*(?<rest>.*)$", RegexOptions.IgnoreCase);
    private static readonly Regex Participle = new(@"^ge\p{Ll}+(?:t|en)\b");

    /// <summary>Zeilen (in Lesereihenfolge) zum Entwurf; dazu Hinweise, was fehlt.</summary>
    public static (Draft Draft, List<string> Hints) Analyze(List<Line> input) => AnalyzeOne(Prepare(input));

    /// <summary>
    /// Alle Rezepte der Seiten: ein neues Rezept beginnt mit einer Titelzeile (eigener Absatz, keine Überschrift), wenn das vorige
    /// schon Zutaten und Zubereitung hat und die Titelzeile größer geschrieben ist oder darunter Portionen, „Zutaten“ oder eine
    /// Zutatenliste folgen. Hinweise je Rezept mit Nummer, wenn es mehrere sind.
    /// </summary>
    public static (List<Draft> Drafts, List<string> Hints) AnalyzeAll(List<Line> input)
    {
        var segments = Split(Prepare(input));
        var drafts = new List<Draft>();
        var hints = new List<string>();
        foreach (var seg in segments)
        {
            var (d, h) = AnalyzeOne(seg);
            drafts.Add(d);
            hints.AddRange(segments.Count > 1 ? h.Select(x => $"Rezept {drafts.Count}: {x}") : h);
        }
        return (drafts, hints);
    }

    private static List<List<Line>> Split(List<Line> lines)
    {
        var pars = lines.GroupBy(l => (l.Page, l.Block)).Select(g => g.ToList()).ToList();
        var median = Median(lines.Select(l => l.Height));
        var segments = new List<List<Line>> { new() };
        bool ing = false, prose = false;
        for (int i = 0; i < pars.Count; i++)
        {
            var p = pars[i];
            var texts = p.Select(l => l.Text).ToList();
            var first = texts[0];
            if (ing && prose && p.Count <= 2 && IsTitleCandidate(first) && char.IsUpper(first[0]) && !first.EndsWith(':') && !IsSectionLine(first))
            {
                var next = i + 1 < pars.Count ? pars[i + 1].Select(l => l.Text).ToList() : null;
                var starts = next != null && (IngredientsHeading.IsMatch(next[0]) || Yield(next[0]) != null || TimeRx.IsMatch(next[0]) || Kind(next) == 'I');
                if (p[0].Height > median * 1.3 || starts)
                {
                    segments.Add([]);
                    ing = prose = false;
                }
            }
            foreach (var t in texts)
            {
                if (IngredientsHeading.IsMatch(t) && t.Length <= 50) ing = true;
                if (StepsHeading.Match(t) is { Success: true } sh && sh.Groups["rest"].Value.Length == 0) prose = true;
            }
            var k = Kind(texts);
            if (k == 'I') ing = true;
            if (k == 'P' && ing) prose = true;
            segments[^1].AddRange(p);
        }
        return segments.Where(s => s.Count > 0).ToList();
    }

    private static bool IsSectionLine(string line) =>
        Regex.IsMatch(line, @"^(?:Für|Fuer|Fur)\s+(?:den|die|das|eine?n?|zum|zur)\s", RegexOptions.IgnoreCase)
        || Regex.IsMatch(line, @"^(?:Außerdem|Ausserdem|Zum Servieren|Zum Garnieren|Zum Bestreuen|Zum Anrichten|Dazu)$", RegexOptions.IgnoreCase);

    private static (Draft Draft, List<string> Hints) AnalyzeOne(List<Line> prepared)
    {
        var lines = prepared.ToList();
        string title = "", yield = "", prep = "", cook = "", total = "", calories = "";
        var nutrition = false;   // Zeile davor war „Nährwerte pro Portion:“ - die Kalorien stehen in der nächsten
        var description = new List<string>();
        var tip = new List<string>();
        var ingredients = new List<string>();
        var steps = new List<string>();

        // Titel: größte Schrift im oberen Teil der ersten Seite (bei gleicher Schrift die erste passende Zeile)
        var titleIdx = FindTitle(lines);
        if (titleIdx >= 0)
        {
            title = lines[titleIdx].Text;
            // Zweizeiliger Titel: direkt darunter, gleich groß, gleicher Absatz
            if (titleIdx + 1 < lines.Count && lines[titleIdx + 1] is var n2 && n2.Block == lines[titleIdx].Block && n2.Page == lines[titleIdx].Page
                && Math.Abs(n2.Height - lines[titleIdx].Height) < lines[titleIdx].Height * 0.15 && IsTitleCandidate(n2.Text) && Bigger(lines, titleIdx))
            {
                title += " " + n2.Text;
                lines.RemoveAt(titleIdx + 1);
            }
            lines.RemoveAt(titleIdx);
        }

        var mode = Mode.Auto;
        var beforeTip = Mode.Auto;
        var numbered = false;
        foreach (var par in lines.GroupBy(l => (l.Page, l.Block)).Select(g => g.ToList()))
        {
            var content = new List<string>();
            foreach (var l in par)
            {
                var text = l.Text;
                // Nährwerte („Pro Portion ca. 520 kcal, 24 g E, 18 g F“): nur die Kalorien je Portion zählen, der Rest der Zeile fällt weg
                if (text.Length <= 160 && (Recipes.Kcal(text) ?? (nutrition ? KcalOnly(text) : null)) is int kcal)
                {
                    if (calories.Length == 0) calories = kcal.ToString(CultureInfo.InvariantCulture);
                    nutrition = false;
                    continue;
                }
                if (NutritionHeading.IsMatch(text)) { nutrition = true; continue; }
                nutrition = false;
                // Zeiten („Zubereitungszeit 30 Min. | Backzeit 45 Min.“) - steht sonst nichts in der Zeile, ist sie damit erledigt
                var rest = TimeRx.Replace(text, m =>
                {
                    var value = TimeValue(m.Groups["value"].Value);
                    switch (TimeKind(m.Groups["label"].Value))
                    {
                        case 'p': if (prep.Length == 0) prep = value; break;
                        case 'c': if (cook.Length == 0) cook = value; break;
                        case 't': if (total.Length == 0) total = value; break;
                        default: description.Add(Capital(m.Groups["label"].Value) + " " + value); break;
                    }
                    return " ";
                });
                if (rest != text)
                {
                    rest = rest.Trim(' ', '|', '·', ',', ';', '/', '-', '–', '•', '.');
                    if (rest.Length == 0) continue;
                    text = rest;
                }
                if (IngredientsHeading.Match(text) is { Success: true } ih && text.Length <= 50)
                {
                    var r = ih.Groups["rest"].Value.Trim(' ', ':', '(', ')');
                    if (r.Length > 0 && Yield(r) is { } y1 && yield.Length == 0) yield = y1;
                    if (r.Length == 0 || Yield(r) != null || r.StartsWith("für", StringComparison.OrdinalIgnoreCase)) { mode = Mode.Ingredients; continue; }
                }
                if (text.Length <= 50 && Yield(text) is { } y)
                {
                    if (yield.Length == 0) yield = y;
                    if (mode == Mode.Auto && ingredients.Count == 0) mode = Mode.Ingredients;
                    continue;
                }
                if (StepsHeading.Match(text) is { Success: true } sh && (sh.Groups["rest"].Value.Length == 0 || text.Contains(':')))
                {
                    if (mode == Mode.Tip) mode = beforeTip;
                    content = Flush(content, ref mode, ref beforeTip, ingredients, steps, description, tip, ref numbered);
                    mode = Mode.Steps;
                    var r = sh.Groups["rest"].Value.Trim();
                    if (r.Length > 0) content.Add(r);
                    continue;
                }
                if (TipHeading.Match(text) is { Success: true } th && (th.Groups["rest"].Value.Length == 0 || Regex.IsMatch(text, @"^\p{L}+[- ]?\p{L}*\s*[:!]")))
                {
                    content = Flush(content, ref mode, ref beforeTip, ingredients, steps, description, tip, ref numbered);
                    if (mode != Mode.Tip) beforeTip = mode;
                    mode = Mode.Tip;
                    var r = th.Groups["rest"].Value.Trim();
                    if (r.Length > 0) content.Add(r);
                    continue;
                }
                content.Add(text);
            }
            Flush(content, ref mode, ref beforeTip, ingredients, steps, description, tip, ref numbered);
        }

        var hints = new List<string>();
        if (title.Length == 0) hints.Add("Kein Titel erkannt.");
        if (ingredients.Count == 0) hints.Add("Keine Zutaten erkannt.");
        if (steps.Count == 0) hints.Add("Keine Zubereitung erkannt.");
        return (new Draft(title, yield, prep, cook, total, string.Join("\n", description), string.Join("\n", ingredients), string.Join("\n", steps),
            string.Join("\n", tip), "", calories), hints);
    }

    private static readonly Regex NutritionHeading = new(@"^(?:(?:Nährwerte?|Nährwertangaben|Nutrition)\s+)?(?:pro|je|per)\s+(?:Portion|Person|Stück|serving)\s*:?$|^(?:Nährwerte?|Nährwertangaben|Nutrition)\b[^:]{0,30}:?$", RegexOptions.IgnoreCase);

    /// <summary>„520 kcal“, „ca. 2180 kJ / 520 kcal“ - nach einer Überschrift wie „Pro Portion:“.</summary>
    private static int? KcalOnly(string text) =>
        Regex.Match(text, @"(?<!\d)(\d{2,4})\s*kcal\b", RegexOptions.IgnoreCase) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var n) && n is > 0 and <= 5000 ? n : null;

    /// <summary>Inhalt eines Absatzes einordnen, je nach Abschnitt und Gestalt. Gibt eine leere Liste zurück.</summary>
    private static List<string> Flush(List<string> content, ref Mode mode, ref Mode beforeTip, List<string> ingredients, List<string> steps,
        List<string> description, List<string> tip, ref bool numbered)
    {
        if (content.Count == 0) return content;
        var kind = Kind(content);
        switch (mode)
        {
            case Mode.Tip:
                if (kind == 'I' && Strong(content)) AddIngredients(content, ingredients);
                else tip.Add(Join(content));
                mode = beforeTip;
                break;
            case Mode.Ingredients:
                if (kind == 'P') { mode = Mode.Steps; AddSteps(content, steps, ref numbered); }
                else AddIngredients(content, ingredients);
                break;
            case Mode.Steps:
                if (kind == 'I' && Strong(content)) AddIngredients(content, ingredients);
                else AddSteps(content, steps, ref numbered);
                break;
            default:
                if (kind == 'I') AddIngredients(content, ingredients);
                else if (kind == 'P' && (ingredients.Count > 0 || steps.Count > 0) || content.Any(c => StepNumber.IsMatch(c) && c.Length > 20))
                    AddSteps(content, steps, ref numbered);
                else description.Add(Join(content));
                break;
        }
        return [];
    }

    /// <summary>'I': Zutatenliste, 'P': Fließtext (Sätze), sonst 'S' (kurze Zeilen ohne Mengen - Beschriftungen).</summary>
    private static char Kind(List<string> content)
    {
        int amount = 0, shortLine = 0, sentence = 0;
        foreach (var c in content)
        {
            if (HasAmount(c)) amount++;
            else if (IsShortItem(c)) shortLine++;
            if (Regex.IsMatch(c, @"[.!?]$") && Words(c) > 3 && !HasAmount(c) || Words(c) > 9) sentence++;
        }
        var joined = Join(content);
        if (amount * 2 >= content.Count && sentence * 2 < content.Count) return 'I';
        if (amount > 0 && amount * 4 >= content.Count && (amount + shortLine) * 5 >= content.Count * 4 && sentence == 0) return 'I';
        if (Words(joined) >= 6 && (sentence > 0 || Regex.IsMatch(joined, @"\p{Ll}[.!]\s+\p{Lu}"))) return 'P';
        return 'S';
    }

    /// <summary>Eindeutig eine Zutatenliste: mindestens zwei Zeilen, fast alle mit Menge, kein Satz.</summary>
    private static bool Strong(List<string> content) =>
        content.Count >= 2 && content.Count(HasAmount) * 5 >= content.Count * 3 && !content.Any(c => Regex.IsMatch(c, @"\p{Ll}{3,}[.!]$"));

    private static bool HasAmount(string line)
    {
        if (line.Length > 80 || Words(line) > 12 || StepNumber.Match(line) is { Success: true } sm && sm.Groups["rest"].Value.Length > 25) return false;
        // Ein Satz („Ein Klassiker, der immer gelingt.“) ist keine Zutat
        if (Words(line) > 4 && Regex.IsMatch(line, @"\p{Ll}{2,}[.!?]$")) return false;
        if (!Regex.IsMatch(line, @"^(?:\d|[½¼¾⅓⅔⅛]|(?:" + NumWords + @"|etwas|einige|1/2)\s)", RegexOptions.IgnoreCase)) return false;
        return FoodText.Ingredient(line) is { } i && (i.Amount != null || i.Unit != null);
    }

    private static bool IsShortItem(string line) =>
        line.Length <= 40 && Words(line) <= 5 && !Regex.IsMatch(line, @"[.!?]$") && Regex.IsMatch(line, @"^\p{L}");

    private static int Words(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    private static string Join(List<string> content) => Regex.Replace(string.Join(' ', content), @"\s{2,}", " ").Trim();

    private static void AddIngredients(List<string> content, List<string> into)
    {
        foreach (var raw in content)
        {
            var line = raw.Trim();
            // Unterabschnitt: „Für den Teig:“, „Außerdem“, „Für die Soße“
            if (line.Length <= 45 && !HasAmount(line) && (line.EndsWith(':') || IsSectionLine(line)))
            {
                into.Add(line.TrimEnd(':', ' ') + ":");
                continue;
            }
            if (into.Count > 0 && !into[^1].EndsWith(':') && IsContinuation(into[^1], line))
            {
                into[^1] = into[^1] + " " + line;
                continue;
            }
            into.Add(line);
        }
    }

    /// <summary>Umbrochene Zutatenzeile: vorige endet mit Komma/„und“/offener Klammer, oder die Zeile beginnt mit Klammer oder „gehackt“.</summary>
    private static bool IsContinuation(string prev, string line)
    {
        if (HasAmount(line)) return false;
        if (Regex.IsMatch(prev, @"(?:[,(/-]|\bund|\boder|\bbzw\.)$", RegexOptions.IgnoreCase)) return true;
        if (prev.Count(c => c == '(') > prev.Count(c => c == ')')) return true;
        return line.StartsWith('(') || Participle.IsMatch(line);
    }

    private static void AddSteps(List<string> content, List<string> into, ref bool numbered)
    {
        string? current = null;
        var first = true;
        foreach (var raw in content)
        {
            var line = raw.Trim();
            var m = StepNumber.Match(line);
            if (m.Success && (m.Groups["rest"].Value.Length == 0 || char.IsUpper(m.Groups["rest"].Value[0])) && !HasAmount(line))
            {
                if (current != null) into.Add(current);
                numbered = true;
                current = m.Groups["rest"].Value.Length > 0 ? m.Groups["rest"].Value : "";
                first = false;
                continue;
            }
            if (current == null)
            {
                // Neuer Absatz: setzt den vorigen Schritt fort, wenn der mitten im Satz aufhörte (Spalten- oder Seitenwechsel)
                if (first && into.Count > 0 && (char.IsLower(line[0]) || !Regex.IsMatch(into[^1], @"[.!?:)]$")) && into[^1].Length > 0)
                {
                    current = into[^1];
                    into.RemoveAt(into.Count - 1);
                    current += " " + line;
                }
                else current = line;
            }
            else current = current.Length == 0 ? line : current + " " + line;
            first = false;
        }
        if (current is { Length: > 0 }) into.Add(current);
        // Ein langer Absatz mit „1. … 2. …“ darin: an den Nummern teilen
        for (int i = into.Count - 1; i >= 0; i--)
        {
            var parts = Regex.Split(into[i], @"(?<=[.!])\s+(?=\d{1,2}\.\s+\p{Lu})");
            if (parts.Length < 2) continue;
            into.RemoveAt(i);
            into.InsertRange(i, parts.Select(p => StepNumber.Match(p) is { Success: true } pm ? pm.Groups["rest"].Value : p));
        }
    }

    // ---------------- Titel ----------------

    private static int FindTitle(List<Line> lines)
    {
        if (lines.Count == 0) return -1;
        var page = lines.Min(l => l.Page);
        var onPage = lines.Where(l => l.Page == page).ToList();
        var top = onPage.Min(l => l.Top);
        var bottom = onPage.Max(l => l.Top + l.Height);
        var limit = top + (bottom - top) * 0.4;
        var median = Median(lines.Select(l => l.Height));
        int best = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            if (l.Page != page || l.Top > limit || !IsTitleCandidate(l.Text)) continue;
            if (best < 0) best = i;
            else if (l.Height > lines[best].Height * 1.15) best = i;
        }
        // Keine Zeile deutlich größer: die erste passende Zeile
        return best;
    }

    private static bool Bigger(List<Line> lines, int idx) => lines[idx].Height > Median(lines.Select(l => l.Height)) * 1.15;

    private static bool IsTitleCandidate(string text)
    {
        if (text.Length > 80 || text.Count(char.IsLetter) < 3) return false;
        if (IngredientsHeading.IsMatch(text) || StepsHeading.IsMatch(text) || TipHeading.IsMatch(text) || TimeRx.IsMatch(text) || Yield(text) != null) return false;
        if (HasAmount(text) || Regex.IsMatch(text, @"[.!?]$") && Words(text) > 4 || Words(text) > 10) return false;
        return true;
    }

    // ---------------- Bereinigen ----------------

    /// <summary>Lesefehler beheben, Seitenzahlen weg, Silbentrennung am Zeilenende zusammenfügen.</summary>
    private static List<Line> Prepare(List<Line> input)
    {
        var result = new List<Line>();
        foreach (var l in input)
        {
            var t = Clean(l.Text);
            if (t.Length == 0 || Regex.IsMatch(t, @"^\d{1,3}$") || t.Count(char.IsLetterOrDigit) < 2 && !Regex.IsMatch(t, @"^[½¼¾⅓⅔⅛]$")) continue;
            if (result.Count > 0 && result[^1] is var prev && prev.Page == l.Page && prev.Block == l.Block
                && Regex.IsMatch(prev.Text, @"\p{Ll}[-¬]$") && Regex.IsMatch(t, @"^\p{Ll}"))
            {
                // „Zwie-“ + „beln in Ringe …“
                result[^1] = prev with { Text = prev.Text[..^1] + t };
                continue;
            }
            result.Add(l with { Text = t });
        }
        return result;
    }

    internal static string Clean(string s)
    {
        s = s.Replace("ﬁ", "fi").Replace("ﬂ", "fl").Replace("ﬀ", "ff").Replace("ﬃ", "ffi").Replace("ﬄ", "ffl")
            .Replace('’', '\'').Replace('„', '"').Replace('“', '"').Replace('”', '"').Replace(' ', ' ');
        s = s.Replace("1⁄2", "½").Replace("1⁄4", "¼").Replace("3⁄4", "¾").Replace('⁄', '/');
        s = Regex.Replace(s, @"\s+", " ").Trim();
        // Aufzählungszeichen
        s = Regex.Replace(s, @"^(?:[-–•*·▪►✓☐□■●o]\s+|[•▪►■●]\s*)", "");
        // ½ liest Tesseract gern als „Vz“, „V2“ oder „%“; 1 als l oder I vor Mengen
        s = Regex.Replace(s, @"^(?:Vz|V2|%)(?=\s)", "½");
        s = Regex.Replace(s, @"(?<![\p{L}\d])[lI|]/([234])\b", "1/$1");
        s = Regex.Replace(s, @"(?<![\p{L}\d])[lI](?=\d{1,3}\s*(?:g|kg|ml|l|EL|TL)\b)", "1");
        s = Regex.Replace(s, @"(?<=\d)[Oo](?=\d|\s*(?:g|kg|ml|l)\b)", "0");
        s = Regex.Replace(s, @"(?<=\d)[Oo](?=[Oo]?\s*(?:g|kg|ml|l)\b)", "0");
        s = Regex.Replace(s, @"^Fur(?=\s)", "Für");
        return s.Trim();
    }

    // ---------------- Portionen und Zeiten ----------------

    /// <summary>„Für 4 Personen“ → „4 Portionen“; „Für 1 Springform (26 cm)“ → „1 Springform (26 cm)“; sonst null.</summary>
    internal static string? Yield(string text)
    {
        var t = text.Trim().TrimEnd(':');
        var m = YieldFor.Match(t);
        if (!m.Success) m = YieldPlain.Match(t);
        if (!m.Success) return null;
        var n = m.Groups["n"].Value;
        var num = int.TryParse(n, out var v) ? v : NumberWord(n);
        if (num is < 1 or > 100) return null;
        var rest = m.Groups["rest"].Value.Trim(' ', ':', '.');
        if (rest.Length == 0 || Regex.IsMatch(rest, @"^(?:Personen|Portionen|Pers\.?|Portion|Person|servings|people|persons)\b", RegexOptions.IgnoreCase))
            return num + (num == 1 ? " Portion" : " Portionen");
        if (rest.Length > 40) return null;
        return num + " " + rest;
    }

    private static int NumberWord(string w) => w.ToLowerInvariant() switch
    {
        "ein" or "eine" or "einen" => 1, "zwei" => 2, "drei" => 3, "vier" => 4, "fünf" => 5, "sechs" => 6, "sieben" => 7,
        "acht" => 8, "neun" => 9, "zehn" => 10, "zwölf" => 12, _ => 0,
    };

    private static char TimeKind(string label)
    {
        var l = label.ToLowerInvariant();
        if (l.StartsWith("zubereitung") || l.StartsWith("arbeit") || l.StartsWith("vorbereitung") || l.StartsWith("zeitaufwand") || l.StartsWith("prep")) return 'p';
        if (l.StartsWith("gesamt") || l.StartsWith("total")) return 't';
        if (l.StartsWith("koch") || l.StartsWith("back") || l.StartsWith("gar") || l.StartsWith("brat") || l.StartsWith("cook")) return 'c';
        return 'r';
    }

    /// <summary>„45 Minuten“ → „45 Min.“, „1 Stunde 30 Minuten“ → „1 Std. 30 Min.“, „20-30 min“ → „20–30 Min.“</summary>
    internal static string TimeValue(string v)
    {
        v = Regex.Replace(v, @"\s*(?:-|bis)\s*", "–");
        v = Regex.Replace(v, @"(?<=\d)\s*(?:Stunden|Stunde|Std\.?|h\b)", " Std.", RegexOptions.IgnoreCase);
        v = Regex.Replace(v, @"(?<=\d)\s*(?:Minuten|Min\.?|min\b)", " Min.", RegexOptions.IgnoreCase);
        v = Regex.Replace(v, @"\s*und\s*", " ");
        return Regex.Replace(v, @"\s{2,}", " ").Trim();
    }

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpper(s[0]) + s[1..];

    // ---------------- KI ----------------

    private const string AiInstructions =
        "Du bekommst eine oder mehrere Kochbuchseiten (als Bild und/oder als per Texterkennung erfassten Text mit Lesefehlern). Gib die Rezepte als JSON " +
        "zurück, ohne weiteren Text, in dieser Form: {\"recipes\":[{\"title\":\"\",\"yield\":\"4 Portionen\",\"prepTime\":\"20 Min.\",\"cookTime\":\"\",\"totalTime\":\"\"," +
        "\"calories\":\"\",\"description\":\"\",\"ingredients\":[\"Für den Teig:\",\"200 g Mehl\"],\"steps\":[\"…\"],\"tip\":\"\"}]}. Ein Eintrag je Rezept auf den Seiten. " +
        "calories: Kilokalorien je Portion als Zahl, nur wenn die Seite sie angibt. Übernimm den Wortlaut der Seite, " +
        "korrigiere nur offensichtliche Lesefehler und erfinde nichts. Eine Zeile je Zutat mit Menge und Einheit, ein Eintrag je Schritt ohne Nummer, " +
        "Unterabschnitte als eigene Zeile mit Doppelpunkt am Ende. Seitenzahlen, Kapitelnamen und Text anderer Rezepte weglassen. " +
        "Unbekanntes als leere Zeichenkette.";

    /// <summary>
    /// Die KI liest die Seiten: mit den Seitenbildern, wenn es welche gibt (dazu der erkannte Text als Hilfe), sonst nur den Text.
    /// Null, wenn sie nicht antwortet oder nichts Brauchbares liefert.
    /// </summary>
    public static async Task<List<Draft>?> AiAsync(ServerConfig config, string text, IReadOnlyList<byte[]> images, ILogger? log, CancellationToken ct)
    {
        if (text.Trim().Length == 0 && images.Count == 0) return null;
        var t = text.Length > 12000 ? text[..12000] : text;
        var answer = images.Count > 0
            ? await PukkiAi.See(config, AiInstructions + (t.Trim().Length > 0 ? "\n\nErkannter Text der Seiten:\n" + t : ""),
                images.Take(MaxPages).Select(i => (i, "image/jpeg")).ToList(), log, ct, 4096)
            : await PukkiAi.Write(config, AiInstructions, t, log, ct, 4096);
        return answer == null ? null : FromAiJson(answer);
    }

    /// <summary>Antwort der KI lesen ({"recipes":[…]} oder ein einzelnes Rezept); auch mit Text oder ```json drumherum. Null bei kaputter Antwort.</summary>
    public static List<Draft>? FromAiJson(string answer)
    {
        int a = answer.IndexOf('{'), b = answer.LastIndexOf('}');
        if (a < 0 || b <= a) return null;
        try
        {
            using var doc = JsonDocument.Parse(answer[a..(b + 1)], new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var items = root.TryGetProperty("recipes", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).ToList() : [root];
            var drafts = items.Select(OneFromAi).OfType<Draft>().ToList();
            return drafts.Count == 0 ? null : drafts;
        }
        catch (JsonException) { return null; }
    }

    private static Draft? OneFromAi(JsonElement r)
    {
        string S(string name) => r.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => (v.GetString() ?? "").Trim(),
            JsonValueKind.Number => v.GetRawText(),
            _ => "",
        } : "";
        string L(string name) => r.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.Array => string.Join("\n", v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => Regex.Replace((x.GetString() ?? "").Trim(), @"\s*\n\s*", " ")).Where(x => x.Length > 0)),
            JsonValueKind.String => (v.GetString() ?? "").Trim(),
            _ => "",
        } : "";
        var yield = S("yield");
        if (Regex.IsMatch(yield, @"^\d+$")) yield += yield == "1" ? " Portion" : " Portionen";
        var calories = Regex.Match(S("calories"), @"\d{1,4}").Value;
        var d = new Draft(S("title"), yield, S("prepTime"), S("cookTime"), S("totalTime"), S("description"), L("ingredients"), L("steps"), S("tip"), "", calories);
        return d.Title.Length == 0 && d.Ingredients.Length == 0 && d.Steps.Length == 0 ? null : d;
    }

    // ---------------- Notiz anlegen ----------------

    /// <summary>Geprüfter Entwurf als Rezept in der Gestalt von RecipeImport (Zutaten-/Schritt-Abschnitte aus den Doppelpunkt-Zeilen).</summary>
    public static RecipeImport.Recipe ToRecipe(Draft d) =>
        new(d.Title.Trim().Length > 0 ? d.Title.Trim() : "Rezept", d.Description.Trim(), d.Yield.Trim(), d.PrepTime.Trim(), d.CookTime.Trim(), d.TotalTime.Trim(),
            Sections(d.Ingredients, false), Sections(d.Steps, true), null, d.Source.Trim(), d.Tip.Trim(), (d.Calories ?? "").Trim());

    private static List<RecipeImport.Section> Sections(string text, bool steps)
    {
        var result = new List<RecipeImport.Section>();
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.EndsWith(':') && line.Length <= 60)
            {
                result.Add(new RecipeImport.Section(line.TrimEnd(':', ' '), []));
                continue;
            }
            if (steps && StepNumber.Match(line) is { Success: true } m && m.Groups["rest"].Value.Length > 0) line = m.Groups["rest"].Value;
            line = line.TrimStart('-', '–', '•', '*', '·', '☐', ' ');
            if (line.Length == 0) continue;
            if (result.Count == 0) result.Add(new RecipeImport.Section("", []));
            result[^1].Items.Add(line);
        }
        return result.Where(s => s.Items.Count > 0).ToList();
    }

    /// <summary>Notiz anlegen: Titelbild (verkleinert) oben, Tag „recipe“. Die Seiten selbst bleiben nicht. Gibt die Nummer der Notiz zurück.</summary>
    public static async Task<long> SaveAsync(UserSpace space, NotesDb db, string? vault, Draft draft, string? folder, byte[]? image, CancellationToken ct)
    {
        string? blob = null;
        if (image != null && Jpeg(image, 1600, 85) is { } img)
        {
            blob = await NotesSync.BlobNameAsync(space, img, ct, vault);
            db.PutBlob(blob, img);
        }
        var (html, plain) = RecipeImport.ToNote(ToRecipe(draft), blob);
        var id = db.CreateNote(folder);
        db.SaveNote(id, html, plain);
        db.SetTags(id, [Recipes.Tag]);
        return id;
    }
}
