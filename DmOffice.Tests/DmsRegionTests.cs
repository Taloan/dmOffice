using DmOffice.Dms;

namespace DmOffice.Tests;

/// <summary>Text in einem Rahmen auf der Seite (Feld aus dem Dokument übernehmen): nur die Wörter im Bereich, Zeile für Zeile.</summary>
public class DmsRegionTests
{
    // A4: 595 x 842 Punkt; Zeilen bei y = 760, 738,4 und 716,8 (Ursprung unten links)
    private static readonly byte[] Pdf = Samples.Pdf(12, "Meldebescheinigung zur Sozialversicherung", "gemaess Par. 25 DEUEV", "Datum: 27.01.2026");

    private static double Y(double pt) => 1 - pt / 842;

    [Fact]
    public void Nur_die_Zeile_im_Rahmen()
    {
        Assert.Equal("Meldebescheinigung zur Sozialversicherung", DmsRegion.FromTextLayer(Pdf, 0, 0, Y(775), 1, Y(752)));
        Assert.Equal("Datum: 27.01.2026", DmsRegion.FromTextLayer(Pdf, 0, 0, Y(730), 1, Y(705)));
    }

    [Fact]
    public void Mehrere_Zeilen_von_oben_nach_unten()
    {
        Assert.Equal("Meldebescheinigung zur Sozialversicherung\ngemaess Par. 25 DEUEV\nDatum: 27.01.2026", DmsRegion.FromTextLayer(Pdf, 0, 0, 0, 1, 1));
    }

    /// <summary>Wie die Textschicht einer Texterkennung: jeder Buchstabe einzeln gesetzt, ohne Leerzeichen dazwischen.</summary>
    private static byte[] Glyphs(params string[] lines)
    {
        var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        double y = 760;
        foreach (var line in lines)
        {
            double x = 40;
            foreach (var c in line)
            {
                // Wortabstand deutlich, zwischen den Buchstaben ein Hauch - wie bei einer Textschicht aus Texterkennung
                if (c == ' ') { x += 4; continue; }
                var letters = page.AddText(c.ToString(), 12, new UglyToad.PdfPig.Core.PdfPoint(x, y), font);
                x = letters[^1].EndBaseLine.X + 0.4;
            }
            y -= 21.6;
        }
        return builder.Build();
    }

    [Fact]
    public void Einzeln_gesetzte_Buchstaben_ergeben_Woerter()
    {
        var pdf = Glyphs("Fujifilm 18/1.4 R LM WR", "Seriennummer: 4AA00402");
        Assert.Equal("Fujifilm 18/1.4 R LM WR\nSeriennummer: 4AA00402", DmsRegion.FromTextLayer(pdf, 0, 0, 0, 1, 1));
    }

    [Fact]
    public void Leer_ohne_Text_im_Rahmen_oder_bei_falscher_Seite()
    {
        Assert.Equal("", DmsRegion.FromTextLayer(Pdf, 0, 0, Y(400), 1, Y(300)));
        Assert.Equal("", DmsRegion.FromTextLayer(Pdf, 3, 0, 0, 1, 1));
    }
}
