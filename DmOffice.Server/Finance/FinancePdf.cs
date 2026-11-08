using System.Globalization;
using DmOffice.Dms;
using SkiaSharp;

namespace DmOffice.Finance;

/// <summary>PDF für Belege und die Jahresübersicht, gesetzt mit SkiaSharp (Text bleibt Text).</summary>
public static class FinancePdf
{
    private const float PageWidth = 595, PageHeight = 842, Margin = 56, Body = 10, Small = 8, Line = 1.35f;
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    public static string Money(long cents) => (cents / 100m).ToString("#,##0.00", De) + " €";
    internal static string Day(string? iso) => DateOnly.TryParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.ToString("dd.MM.yyyy", De) : "";
    internal static string Qty(long milli) => (milli / 1000m).ToString("#,##0.###", De);

    /// <summary>Seiten mit fortlaufendem y und Seitenwechsel.</summary>
    private sealed class Sheet : IDisposable
    {
        private readonly MemoryStream _out = new();
        private readonly SKDocument _doc;
        private readonly SKFont _body, _bold, _small, _title;
        private readonly SKPaint _ink = new() { Color = SKColors.Black, IsAntialias = true }, _grey = new() { Color = new SKColor(0x55, 0x55, 0x55), IsAntialias = true },
            _rule = new() { Color = new SKColor(0x99, 0x99, 0x99), StrokeWidth = 0.7f };
        private SKCanvas? _canvas;
        public float Y;

        public Sheet(string title, string author)
        {
            var (regular, bold) = DmsMailPdf.Fonts.Value;
            _body = new SKFont(regular, Body); _bold = new SKFont(bold, Body); _small = new SKFont(regular, Small); _title = new SKFont(bold, 16);
            _doc = SKDocument.CreatePdf(_out, new SKDocumentPdfMetadata { Title = title, Author = author, Creator = "dmOffice Finanzen", Producer = "dmOffice", Creation = DateTime.Now, Modified = DateTime.Now, PdfA = true });
            NewPage();
        }

        public void NewPage()
        {
            if (_canvas != null) _doc.EndPage();
            _canvas = _doc.BeginPage(PageWidth, PageHeight);
            Y = Margin;
        }

        public void Need(float h) { if (Y + h > PageHeight - Margin) NewPage(); }

        private SKFont Font(char kind) => kind switch { 'b' => _bold, 's' => _small, 't' => _title, _ => _body };

        public void Text(string text, float x, float width, char font = 'r', bool grey = false)
        {
            var f = Font(font);
            var h = f.Size * Line;
            foreach (var para in text.Replace("\r\n", "\n").Split('\n'))
                foreach (var line in para.Length == 0 ? [""] : DmsMailPdf.Wrap(para, f, width))
                {
                    Need(h);
                    Y += f.Size;
                    if (line.Length > 0) _canvas!.DrawText(line, x, Y, SKTextAlign.Left, f, grey ? _grey : _ink);
                    Y += h - f.Size;
                }
        }

        public void Right(string text, float right, char font = 'r') => _canvas!.DrawText(text, right, Y, SKTextAlign.Right, Font(font), _ink);
        public void Left(string text, float x, char font = 'r') => _canvas!.DrawText(text, x, Y, SKTextAlign.Left, Font(font), _ink);
        public void Rule() { _canvas!.DrawLine(Margin, Y, PageWidth - Margin, Y, _rule); }
        public static int LinesOf(string text, float width, SKFont font) => text.Split('\n').Sum(p => Math.Max(1, DmsMailPdf.Wrap(p, font, width).Count()));
        public IEnumerable<string> Wrapped(string text, float width) => text.Replace("\r\n", "\n").Split('\n').SelectMany(p => p.Length == 0 ? [""] : DmsMailPdf.Wrap(p, _body, width));

        public byte[] Finish()
        {
            _doc.EndPage();
            _doc.Close();
            return _out.ToArray();
        }

        public void Dispose()
        {
            _doc.Dispose(); _body.Dispose(); _bold.Dispose(); _small.Dispose(); _title.Dispose(); _ink.Dispose(); _grey.Dispose(); _rule.Dispose(); _out.Dispose();
        }
    }

    private const float Width = PageWidth - 2 * Margin;

    /// <summary>Jahresübersicht: Einnahmen und Ausgaben je Kategorie, Überschuss. Keine amtliche Anlage EÜR.</summary>
    public static byte[] Summary(FinanceStore.Summary sum, FinanceSettings s)
    {
        using var p = new Sheet($"Einnahmen und Ausgaben {sum.Year}", s.Name);
        p.Text($"Einnahmen und Ausgaben {sum.Year}", Margin, Width, 't');
        p.Y += 4;
        if (s.Name.Length > 0) p.Text(s.Name, Margin, Width);
        p.Text(sum.Closed ? "Jahr abgeschlossen" : "Jahr noch nicht abgeschlossen", Margin, Width, 'r', grey: true);
        p.Y += 12;
        void Block(string title, string kind, long total)
        {
            p.Need(40);
            p.Text(title, Margin, Width, 'b');
            foreach (var c in sum.Categories.Where(c => c.Kind == kind))
            {
                p.Need(16);
                p.Y += Body;
                p.Left(c.Name, Margin);
                p.Right(Money(c.Cents), PageWidth - Margin);
                p.Y += 4;
            }
            p.Need(24);
            p.Y += 2; p.Rule(); p.Y += Body + 3;
            p.Left("Summe", Margin, 'b');
            p.Right(Money(total), PageWidth - Margin, 'b');
            p.Y += 16;
        }
        Block("Einnahmen", FinanceStore.Income, sum.Income);
        Block("Ausgaben", FinanceStore.Expense, sum.Expense);
        p.Need(24);
        p.Y += Body;
        p.Left("Überschuss", Margin, 'b');
        p.Right(Money(sum.Surplus), PageWidth - Margin, 'b');
        return p.Finish();
    }
}
