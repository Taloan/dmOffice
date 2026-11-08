using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DmOffice.Dms;
using SkiaSharp;

namespace DmOffice.Finance;

/// <summary>Spalte der Positionstabelle. Field: pos, description, qty, price, total.</summary>
public sealed class TplColumn
{
    public string Field { get; set; } = "description";
    public string Label { get; set; } = "";
    /// <summary>Anteil an der Breite (die Spalten werden auf die Seitenbreite verteilt).</summary>
    public double Width { get; set; } = 1;
}

public sealed class TplFact
{
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>Baustein einer Vorlage. Type: text, image, recipient, facts, table, total, note, rule, spacer, pagebreak, columns.</summary>
public sealed class TplBlock
{
    public string Type { get; set; } = "text";
    public string Text { get; set; } = "";
    public bool Bold { get; set; }
    public bool Grey { get; set; }
    /// <summary>Schriftgröße; 0: Grundschrift der Vorlage.</summary>
    public double Size { get; set; }
    /// <summary>left, center, right</summary>
    public string Align { get; set; } = "left";
    /// <summary>Abstand (spacer) in Punkt.</summary>
    public double Gap { get; set; } = 10;
    /// <summary>Bild als data:-Adresse (PNG oder JPEG).</summary>
    public string? Image { get; set; }
    /// <summary>Bildbreite in Punkt.</summary>
    public double ImageWidth { get; set; } = 120;
    public List<TplFact>? Facts { get; set; }
    public List<TplColumn>? Columns { get; set; }
    /// <summary>columns: nebeneinanderliegende Spalten mit eigenen Bausteinen.</summary>
    public List<List<TplBlock>>? Cells { get; set; }
}

public sealed class TplLayout
{
    public double Margin { get; set; } = 56;
    public double FontSize { get; set; } = 10;
    public List<TplBlock> Header { get; set; } = [];
    public List<TplBlock> Body { get; set; } = [];
    public List<TplBlock> Footer { get; set; } = [];
}

/// <summary>
/// Vorlagen für Belege: Aufbau als JSON aus Kopfzeile, Inhalt und Fußzeile, Satz mit SkiaSharp (feste Koordinaten). Derselbe Satz liefert PDF und die Vorschau,
/// darum bricht die Vorschau genauso um wie der Beleg. Vorbild für den Aufbau ist der Datenblatt-Designer von journali, der Code ist eigen.
/// </summary>
public static class FinanceTemplates
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static string Serialize(TplLayout l) => JsonSerializer.Serialize(l, Json);
    public static TplLayout Parse(string json) => JsonSerializer.Deserialize<TplLayout>(json, Json) ?? Default();

    /// <summary>Platzhalter, die in Texten erlaubt sind.</summary>
    public static readonly string[] Keys =
    [
        "firma", "anschrift", "anschrift_zeile", "kontakt", "steuernummer", "ust_id", "bank", "iban", "bic",
        "art", "nummer_art", "nummer", "kundennummer", "datum", "leistungsdatum", "frist_art", "frist", "bezug", "grundlage_art", "grundlage", "betreff", "einleitung", "schluss", "gesamt", "seite", "seiten",
    ];

    private static readonly string[] Types = ["text", "image", "recipient", "facts", "table", "total", "note", "rule", "spacer", "pagebreak", "columns"];
    private static readonly string[] CellTypes = ["text", "image", "recipient", "facts", "rule", "spacer"];
    private static readonly string[] Fields = ["pos", "description", "qty", "price", "total"];
    private static readonly Regex Placeholder = new(@"\{([A-Za-z_]+)\}", RegexOptions.Compiled);
    private const int MaxImageBytes = 1024 * 1024;

    public static TplLayout Default() => new()
    {
        Body =
        [
            new() { Type = "text", Text = "{firma} · {anschrift_zeile}", Size = 8, Grey = true },
            new() { Type = "spacer", Gap = 14 },
            new() { Type = "recipient" },
            new() { Type = "spacer", Gap = 24 },
            new() { Type = "text", Text = "{art}", Size = 16, Bold = true },
            new() { Type = "spacer", Gap = 6 },
            new() { Type = "facts", Facts =
            [
                new() { Label = "{nummer_art}", Value = "{nummer}" }, new() { Label = "Kundennummer", Value = "{kundennummer}" }, new() { Label = "Datum", Value = "{datum}" },
                new() { Label = "Storniert Rechnung", Value = "{bezug}" }, new() { Label = "{grundlage_art}", Value = "{grundlage}" }, new() { Label = "Leistungsdatum", Value = "{leistungsdatum}" }, new() { Label = "{frist_art}", Value = "{frist}" },
            ] },
            new() { Type = "spacer", Gap = 10 },
            new() { Type = "text", Text = "{betreff}", Bold = true },
            new() { Type = "spacer", Gap = 4 },
            new() { Type = "text", Text = "{einleitung}" },
            new() { Type = "spacer", Gap = 8 },
            new() { Type = "table", Columns =
            [
                new() { Field = "description", Label = "Leistung", Width = 50 }, new() { Field = "qty", Label = "Menge", Width = 15 },
                new() { Field = "price", Label = "Einzelpreis", Width = 18 }, new() { Field = "total", Label = "Betrag", Width = 17 },
            ] },
            new() { Type = "total", Text = "Gesamtbetrag" },
            new() { Type = "spacer", Gap = 14 },
            new() { Type = "note" },
            new() { Type = "spacer", Gap = 8 },
            new() { Type = "text", Text = "{schluss}" },
        ],
        Footer =
        [
            new() { Type = "rule" },
            new() { Type = "text", Size = 8, Grey = true, Text = "{firma}[ · {kontakt}]\n[Steuernummer: {steuernummer}][ · USt-IdNr.: {ust_id}]\n[{bank}][ · IBAN: {iban}][ · BIC: {bic}]" },
            new() { Type = "text", Size = 8, Grey = true, Align = "right", Text = "Seite {seite} von {seiten}" },
        ],
    };

    private static IEnumerable<TplBlock> All(TplLayout l) => l.Header.Concat(l.Body).Concat(l.Footer).SelectMany(Flat);
    private static IEnumerable<TplBlock> Flat(TplBlock b) => b.Cells == null ? [b] : b.Cells.SelectMany(c => c).SelectMany(Flat).Prepend(b);

    /// <summary>
    /// Prüft eine Vorlage. Empfänger, Positionen, Gesamtbetrag und der Hinweis nach § 19 UStG müssen im Inhalt stehen, damit keine Vorlage einen Beleg ohne diese Angaben erzeugt.
    /// </summary>
    public static void Check(TplLayout l)
    {
        if (l.Margin is < 20 or > 120) throw new ArgumentException("Der Seitenrand muss zwischen 20 und 120 Punkt liegen.");
        if (l.FontSize is < 7 or > 14) throw new ArgumentException("Die Grundschrift muss zwischen 7 und 14 Punkt liegen.");
        var all = All(l).ToList();
        if (all.Count > 300) throw new ArgumentException("Die Vorlage hat zu viele Bausteine (höchstens 300).");
        foreach (var b in all)
        {
            if (!Types.Contains(b.Type)) throw new ArgumentException("Unbekannter Baustein.");
            if (b.Size != 0 && b.Size is < 5 or > 40) throw new ArgumentException("Die Schriftgröße muss zwischen 5 und 40 Punkt liegen.");
            if (b.Gap is < 0 or > 400) throw new ArgumentException("Der Abstand muss zwischen 0 und 400 Punkt liegen.");
            if (b.ImageWidth is < 5 or > 480) throw new ArgumentException("Die Bildbreite muss zwischen 5 und 480 Punkt liegen.");
            foreach (var text in new[] { b.Text }.Concat((b.Facts ?? []).SelectMany(f => new[] { f.Label, f.Value })).Concat((b.Columns ?? []).Select(c => c.Label)))
                CheckText(text);
            if (b.Type == "image" && b.Image != null) CheckImage(b.Image);
            if (b.Cells != null)
            {
                if (b.Cells.Count is < 1 or > 4) throw new ArgumentException("Bitte 1 bis 4 Spalten verwenden.");
                if (b.Cells.SelectMany(c => c).Any(x => !CellTypes.Contains(x.Type))) throw new ArgumentException("Dieser Baustein lässt sich nicht in eine Spalte legen.");
            }
        }
        var body = l.Body.SelectMany(Flat).ToList();
        foreach (var (type, name) in new[] { ("recipient", "Empfänger"), ("table", "Positionen"), ("total", "Gesamtbetrag"), ("note", "Hinweis nach § 19 UStG") })
            if (!body.Any(b => b.Type == type)) throw new ArgumentException($"Der Baustein „{name}“ muss im Inhalt stehen.");
        foreach (var region in new[] { l.Header, l.Footer })
            if (region.SelectMany(Flat).Any(b => b.Type is "table" or "total" or "note" or "pagebreak")) throw new ArgumentException("Positionen, Gesamtbetrag, Hinweis und Seitenumbruch gehören in den Inhalt.");
        foreach (var t in body.Where(b => b.Type == "table"))
        {
            var cols = t.Columns ?? [];
            if (cols.Any(c => !Fields.Contains(c.Field) || c.Width <= 0)) throw new ArgumentException("Ungültige Spalte in den Positionen.");
            foreach (var (field, name) in new[] { ("description", "Leistung"), ("qty", "Menge"), ("total", "Betrag") })
                if (cols.All(c => c.Field != field)) throw new ArgumentException($"Die Spalte „{name}“ muss in den Positionen stehen.");
        }
        if (body.Count(b => b.Type == "table") > 1) throw new ArgumentException("Die Positionen dürfen nur einmal vorkommen.");
    }

    private static void CheckText(string text)
    {
        foreach (Match m in Placeholder.Matches(text))
            if (!Keys.Contains(m.Groups[1].Value)) throw new ArgumentException($"Unbekannter Platzhalter {m.Value}.");
        var depth = 0;
        foreach (var ch in text)
        {
            if (ch == '[' && ++depth > 1) throw new ArgumentException("Eckige Klammern dürfen nicht ineinander liegen.");
            if (ch == ']' && --depth < 0) throw new ArgumentException("Eine eckige Klammer ist nicht geöffnet.");
        }
        if (depth != 0) throw new ArgumentException("Eine eckige Klammer ist nicht geschlossen.");
    }

    private static byte[]? ImageBytes(string data)
    {
        var m = Regex.Match(data, @"^data:image/(png|jpeg);base64,(.+)$", RegexOptions.Singleline);
        if (!m.Success) return null;
        try { return Convert.FromBase64String(m.Groups[2].Value); }
        catch (FormatException) { return null; }
    }

    private static void CheckImage(string data)
    {
        var bytes = ImageBytes(data) ?? throw new ArgumentException("Bilder bitte als PNG oder JPEG einfügen.");
        if (bytes.Length > MaxImageBytes) throw new ArgumentException("Das Bild ist zu groß (höchstens 1 MB).");
        using var bmp = SKBitmap.Decode(bytes) ?? throw new ArgumentException("Das Bild lässt sich nicht lesen.");
    }

    /// <summary>Beispiel für die Vorschau: mit den eigenen Angaben, erfundener Empfänger.</summary>
    public static FinDoc Sample(string kind, int lines)
    {
        var rows = Enumerable.Range(1, Math.Clamp(lines, 1, 200)).Select(i => new FinLine(i == 1 ? "Beratung vor Ort mit einer etwas längeren Beschreibung, die in die nächste Zeile umbricht" : $"Leistung {i}", 1000 + (i % 3) * 500, i % 2 == 0 ? "Std." : "", 8000, (1000 + (i % 3) * 500) * 8000 / 1000)).ToList();
        return new FinDoc(0, kind, FinanceStore.Draft, kind switch { FinanceStore.Offer => "AN-2026-0001", FinanceStore.Order => "AU-2026-0001", _ => "RE-2026-0001" }, DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), kind == FinanceStore.Offer ? "" : "März 2026",
            DateTime.Today.AddDays(14).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "Beispielbetreff", "Vielen Dank für Ihren Auftrag.", "Mit freundlichen Grüßen", "Erika Beispiel\nTestgasse 2\n54321 Testdorf", null,
            rows.Sum(r => r.TotalCents), null, kind == FinanceStore.Cancel ? "RE-2026-0000" : null, null, null, null, 0, false, null, false, null, "K-0001", null, rows);
    }

    private sealed class Run : IDisposable
    {
        private const float W = 595, H = 842, Line = 1.35f;
        private readonly FinDoc _doc;
        private readonly FinanceSettings _s;
        private readonly TplLayout _l;
        private readonly bool _draft;
        private readonly Func<int, SKCanvas?> _begin;
        private readonly Action _end;
        private readonly int _pages;
        private readonly float _m, _base, _cw;
        private readonly Dictionary<(bool, float), SKFont> _fonts = [];
        private readonly Dictionary<string, SKBitmap?> _images = [];
        private readonly SKPaint _ink = new() { Color = SKColors.Black, IsAntialias = true }, _grey = new() { Color = new SKColor(0x55, 0x55, 0x55), IsAntialias = true },
            _rule = new() { Color = new SKColor(0x99, 0x99, 0x99), StrokeWidth = 0.7f }, _mark = new() { Color = new SKColor(0xC0, 0x30, 0x30), IsAntialias = true };
        private SKCanvas? _c;
        private float _y, _top, _bottom;
        public int Page { get; private set; }

        public Run(FinDoc doc, FinanceSettings s, TplLayout l, bool draft, int pages, Func<int, SKCanvas?> begin, Action end)
        {
            (_doc, _s, _l, _draft, _pages, _begin, _end) = (doc, s, l, draft, pages, begin, end);
            _m = (float)l.Margin; _base = (float)l.FontSize; _cw = W - 2 * _m;
        }

        private SKFont Font(bool bold, float size)
        {
            if (_fonts.TryGetValue((bold, size), out var f)) return f;
            var (regular, b) = DmsMailPdf.Fonts.Value;
            return _fonts[(bold, size)] = new SKFont(bold ? b : regular, size);
        }

        private string Slot(string key)
        {
            var heading = FinanceStore.Heading(_doc);
            // Grundlage: Angebot oder Auftrag, auf dem der Beleg aufbaut (bei Stornorechnungen steht die Rechnung unter „bezug“)
            var basis = _doc.Kind != FinanceStore.Cancel && _doc.RefKind is FinanceStore.Offer or FinanceStore.Order && _doc.RefNumber != null;
            string Lines(string t) => string.Join(" · ", t.Replace("\r\n", "\n").Split('\n').Where(x => x.Trim().Length > 0).Select(x => x.Trim()));
            return key switch
            {
                "firma" => _s.Name.Trim(),
                "anschrift" => _s.Address.Trim(),
                "anschrift_zeile" => Lines(_s.Address),
                "kontakt" => _s.Contact.Trim(),
                "steuernummer" => _s.TaxNumber.Trim(),
                "ust_id" => _s.VatId.Trim(),
                "bank" => _s.Bank.Trim(),
                "iban" => _s.Iban.Trim(),
                "bic" => _s.Bic.Trim(),
                "art" => _draft ? heading + " (Entwurf)" : heading,
                "nummer_art" => _doc.Kind switch { FinanceStore.Offer => "Angebotsnummer", FinanceStore.Order => "Auftragsnummer", _ => "Rechnungsnummer" },
                "nummer" => _doc.Number ?? "wird beim Ausstellen vergeben",
                "kundennummer" => _doc.CustomerNumber,
                "datum" => FinancePdf.Day(_doc.DocDate),
                "leistungsdatum" => _doc.Kind == FinanceStore.Offer ? "" : _doc.ServiceDate,
                "frist_art" => _doc.Kind switch { FinanceStore.Offer => "Gültig bis", FinanceStore.Order => "", _ => "Zahlbar bis" },
                "frist" => _doc.Kind is FinanceStore.Cancel or FinanceStore.Order ? "" : FinancePdf.Day(_doc.DueDate),
                "bezug" => _doc.Kind == FinanceStore.Cancel ? _doc.RefNumber ?? "" : "",
                "grundlage_art" => basis ? FinanceStore.SourceName(_doc.RefKind) : "",
                "grundlage" => basis ? _doc.RefNumber! : "",
                "betreff" => _doc.Subject,
                "einleitung" => _doc.Intro,
                "schluss" => _doc.Outro,
                "gesamt" => FinancePdf.Money(_doc.TotalCents),
                "seite" => Page.ToString(CultureInfo.InvariantCulture),
                "seiten" => _pages.ToString(CultureInfo.InvariantCulture),
                _ => "",
            };
        }

        /// <summary>Platzhalter füllen. [ … ] fällt weg, wenn ein Platzhalter darin leer ist. Eine Zeile, die nur aus leeren Platzhaltern bestand, fällt ganz weg.</summary>
        private string Fill(string text)
        {
            var lines = new List<string>();
            foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
            {
                var hadKey = line.Contains('{');
                var section = Regex.Replace(line, @"\[([^\[\]]*)\]", m => Placeholder.Matches(m.Groups[1].Value).Any(k => Slot(k.Groups[1].Value).Trim().Length == 0) ? "" : m.Groups[1].Value);
                var filled = Placeholder.Replace(section, m => Slot(m.Groups[1].Value)).Trim();
                while (filled.StartsWith("· ", StringComparison.Ordinal)) filled = filled[2..];
                if (filled.EndsWith(" ·", StringComparison.Ordinal)) filled = filled[..^2];
                if (filled.Length > 0 || !hadKey && line.Trim().Length == 0 && !line.Contains('[')) lines.Add(filled);
            }
            return string.Join('\n', lines).Trim('\n');
        }

        private SKBitmap? Bitmap(string? data)
        {
            if (data == null) return null;
            if (_images.TryGetValue(data, out var b)) return b;
            var bytes = ImageBytes(data);
            return _images[data] = bytes == null ? null : SKBitmap.Decode(bytes);
        }

        private float Size(TplBlock b) => b.Size > 0 ? (float)b.Size : _base;

        private float Lines(IEnumerable<string> lines, TplBlock b, SKFont f, float x, float w, float y, bool draw)
        {
            var h = f.Size * Line;
            var align = b.Align switch { "center" => SKTextAlign.Center, "right" => SKTextAlign.Right, _ => SKTextAlign.Left };
            var ax = b.Align switch { "center" => x + w / 2, "right" => x + w, _ => x };
            var used = 0f;
            foreach (var line in lines)
            {
                if (draw && _c != null && line.Length > 0) _c.DrawText(line, ax, y + used + f.Size, align, f, b.Grey ? _grey : _ink);
                used += h;
            }
            return used;
        }

        private static IEnumerable<string> Wrap(string text, SKFont f, float w) =>
            text.Split('\n').SelectMany(p => p.Length == 0 ? [""] : DmsMailPdf.Wrap(p, f, w));

        /// <summary>Höhe eines Bausteins; mit draw zeichnet er an (x, y).</summary>
        private float Block(TplBlock b, float x, float w, float y, bool draw)
        {
            switch (b.Type)
            {
                case "text":
                {
                    var t = Fill(b.Text);
                    return t.Length == 0 ? 0 : Lines(Wrap(t, Font(b.Bold, Size(b)), w), b, Font(b.Bold, Size(b)), x, w, y, draw);
                }
                case "recipient":
                    return Lines(Wrap(_doc.Recipient.Trim(), Font(b.Bold, Size(b)), w), b, Font(b.Bold, Size(b)), x, w, y, draw);
                case "note":
                    return Lines(Wrap(FinanceStore.SmallBusinessNote, Font(b.Bold, Size(b)), w), b, Font(b.Bold, Size(b)), x, w, y, draw);
                case "facts":
                {
                    var rows = (b.Facts ?? []).Select(f => (Label: Fill(f.Label), Value: Fill(f.Value))).Where(r => r.Value.Length > 0).Select(r => r.Label.Length > 0 ? $"{r.Label}: {r.Value}" : r.Value);
                    return Lines(rows.SelectMany(r => Wrap(r, Font(b.Bold, Size(b)), w)), b, Font(b.Bold, Size(b)), x, w, y, draw);
                }
                case "rule":
                    if (draw) _c?.DrawLine(x, y + 3, x + w, y + 3, _rule);
                    return 6;
                case "spacer":
                    return (float)b.Gap;
                case "image":
                {
                    var bmp = Bitmap(b.Image);
                    if (bmp == null || bmp.Width == 0) return 0;
                    var iw = Math.Min((float)b.ImageWidth, w);
                    var ih = iw * bmp.Height / bmp.Width;
                    var ix = b.Align switch { "center" => x + (w - iw) / 2, "right" => x + w - iw, _ => x };
                    if (draw && _c != null)
                    {
                        using var img = SKImage.FromBitmap(bmp);
                        _c.DrawImage(img, new SKRect(ix, y, ix + iw, y + ih), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                    }
                    return ih;
                }
                case "columns":
                {
                    var cells = b.Cells ?? [];
                    if (cells.Count == 0) return 0;
                    const float gap = 12;
                    var cw = (w - gap * (cells.Count - 1)) / cells.Count;
                    var most = 0f;
                    for (var i = 0; i < cells.Count; i++)
                    {
                        var used = 0f;
                        foreach (var inner in cells[i]) used += Block(inner, x + i * (cw + gap), cw, y + used, draw);
                        most = Math.Max(most, used);
                    }
                    return most;
                }
                case "total":
                {
                    var f = Font(true, Size(b) > 0 ? Size(b) : _base);
                    if (draw && _c != null)
                    {
                        _c.DrawLine(x, y + 1, x + w, y + 1, _rule);
                        _c.DrawText(Fill(b.Text.Length > 0 ? b.Text : "Gesamtbetrag"), x, y + 6 + f.Size, SKTextAlign.Left, f, _ink);
                        _c.DrawText(FinancePdf.Money(_doc.TotalCents), x + w, y + 6 + f.Size, SKTextAlign.Right, f, _ink);
                    }
                    return 8 + f.Size * Line;
                }
                default:
                    return 0;
            }
        }

        private float Height(List<TplBlock> blocks, float w) => blocks.Sum(b => Block(b, 0, w, 0, false));

        private void NewPage()
        {
            if (Page > 0) _end();
            Page++;
            _c = _begin(Page);
            var head = Height(_l.Header, _cw);
            var foot = Height(_l.Footer, _cw);
            var y = _m;
            foreach (var b in _l.Header) y += Block(b, _m, _cw, y, true);
            y = H - _m - foot;
            foreach (var b in _l.Footer) y += Block(b, _m, _cw, y, true);
            if (_draft) _c?.DrawText("Entwurf", W - _m, _m - 12, SKTextAlign.Right, Font(true, 9), _mark);
            _top = _m + head + (head > 0 ? 8 : 0);
            _bottom = H - _m - foot - (foot > 0 ? 8 : 0);
            // Kopf und Fuß füllen die Seite: wenigstens etwas Platz, sonst käme der Satz nie zu einem Ende
            if (_bottom - _top < 80) _bottom = _top + 80;
            _y = _top;
        }

        /// <summary>Text zeilenweise, damit lange Absätze über die Seite hinaus umbrechen.</summary>
        private void FlowLines(IEnumerable<string> lines, TplBlock b, SKFont f)
        {
            var h = f.Size * Line;
            foreach (var line in lines)
            {
                if (_y + h > _bottom) NewPage();
                _y += Lines([line], b, f, _m, _cw, _y, true);
            }
        }

        private void Table(TplBlock b)
        {
            var cols = b.Columns ?? [];
            var sum = cols.Sum(c => c.Width);
            var f = Font(false, Size(b));
            var bold = Font(true, Size(b));
            var h = f.Size * Line;
            const float pad = 8;
            var widths = cols.Select(c => (float)(_cw * c.Width / sum)).ToList();
            void Head()
            {
                if (_y + bold.Size * 3 + h > _bottom) NewPage();
                var x = _m;
                for (var i = 0; i < cols.Count; i++)
                {
                    var right = cols[i].Field != "description";
                    _c?.DrawText(Fill(cols[i].Label), right ? x + widths[i] : x, _y + bold.Size, right ? SKTextAlign.Right : SKTextAlign.Left, bold, _ink);
                    x += widths[i];
                }
                _y += bold.Size + 5;
                _c?.DrawLine(_m, _y, _m + _cw, _y, _rule);
                _y += 3;
            }
            Head();
            var nr = 0;
            foreach (var l in _doc.Lines)
            {
                nr++;
                var di = cols.FindIndex(c => c.Field == "description");
                var desc = Wrap(l.Description, f, Math.Max(20, widths[di] - pad)).ToList();
                var rh = h * desc.Count + 3;
                if (_y + rh > _bottom) { NewPage(); Head(); }
                // Eine Zeile, die länger als die Seite ist, darf nicht endlos umbrechen: Sie wird über die Seiten verteilt
                var x = _m;
                for (var i = 0; i < cols.Count; i++)
                {
                    var value = cols[i].Field switch
                    {
                        "pos" => nr.ToString(CultureInfo.InvariantCulture),
                        "qty" => (FinancePdf.Qty(l.QtyMilli) + " " + l.Unit).Trim(),
                        "price" => FinancePdf.Money(l.PriceCents),
                        "total" => FinancePdf.Money(l.TotalCents),
                        _ => null,
                    };
                    if (value != null) _c?.DrawText(value, x + widths[i], _y + f.Size, SKTextAlign.Right, f, _ink);
                    x += widths[i];
                }
                var dx = _m + widths.Take(di).Sum();
                foreach (var line in desc)
                {
                    if (_y + h > _bottom) { NewPage(); Head(); }
                    if (line.Length > 0) _c?.DrawText(line, dx, _y + f.Size, SKTextAlign.Left, f, _ink);
                    _y += h;
                }
                _y += 3;
            }
        }

        public void Go()
        {
            NewPage();
            foreach (var b in _l.Body)
            {
                switch (b.Type)
                {
                    case "table": Table(b); continue;
                    case "pagebreak": NewPage(); continue;
                    case "text":
                    {
                        var t = Fill(b.Text);
                        if (t.Length > 0) FlowLines(Wrap(t, Font(b.Bold, Size(b)), _cw), b, Font(b.Bold, Size(b)));
                        continue;
                    }
                }
                var h = Block(b, _m, _cw, _y, false);
                if (_y + h > _bottom && _y > _top) NewPage();
                _y += Block(b, _m, _cw, _y, true);
            }
            _end();
        }

        public void Dispose()
        {
            foreach (var f in _fonts.Values) f.Dispose();
            foreach (var i in _images.Values) i?.Dispose();
            _ink.Dispose(); _grey.Dispose(); _rule.Dispose(); _mark.Dispose();
        }
    }

    private static int CountPages(FinDoc doc, FinanceSettings s, TplLayout l, bool draft)
    {
        using var r = new Run(doc, s, l, draft, 0, _ => null, () => { });
        r.Go();
        return r.Page;
    }

    public static byte[] Pdf(FinDoc doc, FinanceSettings s, TplLayout l, bool draft)
    {
        var pages = CountPages(doc, s, l, draft);
        using var output = new MemoryStream();
        var heading = FinanceStore.Heading(doc);
        using (var pdf = SKDocument.CreatePdf(output, new SKDocumentPdfMetadata { Title = $"{heading} {doc.Number}".Trim(), Author = s.Name, Creator = "dmOffice Finanzen", Producer = "dmOffice", Creation = DateTime.Now, Modified = DateTime.Now, PdfA = true }))
        {
            using var r = new Run(doc, s, l, draft, pages, _ => pdf.BeginPage(595, 842), () => pdf.EndPage());
            r.Go();
            pdf.Close();
        }
        return output.ToArray();
    }

    /// <summary>Eine Seite als Bild, aus demselben Satz wie das PDF.</summary>
    public static (int Pages, byte[] Png) Preview(FinDoc doc, FinanceSettings s, TplLayout l, int page)
    {
        var pages = CountPages(doc, s, l, false);
        page = Math.Clamp(page, 1, pages);
        const float scale = 1.5f;
        using var bmp = new SKBitmap((int)(595 * scale), (int)(842 * scale));
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.White);
        canvas.Scale(scale);
        using (var r = new Run(doc, s, l, false, pages, p => p == page ? canvas : null, () => { })) r.Go();
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 90);
        return (pages, data.ToArray());
    }
}
