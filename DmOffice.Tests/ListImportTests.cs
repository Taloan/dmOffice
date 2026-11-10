using DmOffice.Pim;

namespace DmOffice.Tests;

/// <summary>Vorschlaege aus AnyList (kopiertes HTML) oder einer einfachen Liste uebernehmen.</summary>
public class ListImportTests
{
    private static string Cell(string name, string details = "", string note = "") =>
        $"<div class=\"ALTableCell\" draggable=\"false\"><div class=\"ALTableCellRow\"><div class=\"ALTableCellContents\"><div><div class=\"ALAutocompleteSuggestionItemName\"><span><!-- react-text: 36 -->{name}<!-- /react-text --><!-- react-text: 37 --> <!-- /react-text --><span class=\"ALAutocompleteSuggestionItemDetails\">{details}</span></span></div><div class=\"ALAutocompleteSuggestionItemDetailsContainer\"><div class=\"ALAutocompleteSuggestionItemDetails\" style=\"width: calc(100% - 4px); margin-top: 4px;\">{note}</div></div></div></div></div></div>";

    [Fact]
    public void AnyList_Html_wird_gelesen()
    {
        var html = "<div class=\"ALTableView\">" + Cell("Add \"a\"") + Cell("A-Z Multivitamin") + Cell("Aprikosen", "(2)")
            + Cell("Schmand", "(400 g)") + Cell("Edelsüßpaprika", "(2 TL)") + Cell("Vitamin C &amp; Zink")
            + Cell("(ca. 300 g) Lasagneplatten", "(16)") + Cell("gemischtes Hackfleisch", "(500 g)") + Cell("Gemischtes Hackfleisch")
            + Cell("Bacon") + Cell("Bacon", "(2)") + Cell("Brat Fett flüssig ") + Cell("Rotwein ", "", "Fürstenf<span class=\"ALSearchTextHighlight\">a</span>ss") + "</div>";
        var items = ListImport.Parse(html);
        Assert.Equal(new[] { "A-Z Multivitamin", "Aprikosen", "Schmand", "Edelsüßpaprika", "Vitamin C & Zink", "Lasagneplatten",
            "Gemischtes Hackfleisch", "Bacon", "Brat Fett flüssig", "Rotwein" }, items.Select(x => x.Name));
        Assert.Null(items.Single(x => x.Name == "Aprikosen").Unit);
        Assert.Equal("g", items.Single(x => x.Name == "Schmand").Unit);
        Assert.Equal("TL", items.Single(x => x.Name == "Edelsüßpaprika").Unit);
    }

    [Fact]
    public void Einfache_Liste_und_nur_neue_werden_uebernommen()
    {
        var items = ListImport.Parse("- Milch\r\n☐ Eier\n\n  brot  \nMilch\n");
        Assert.Equal(new[] { "Milch", "Eier", "Brot" }, items.Select(x => x.Name));

        var dir = Directory.CreateTempSubdirectory("dmimport");
        var db = new Core.UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "t");
        Lists.Update(db, 7, Lists.Shop, null);
        Lists.Learn(db, 7, "milch", null, null);
        Assert.Equal(2, ListImport.CountNew(db, 7, items));
        Assert.Equal(2, ListImport.Apply(db, 7, items));
        Assert.Equal(0, ListImport.Apply(db, 7, items));
        var h = Lists.Get(db, 7).History;
        Assert.Equal(3, h.Count);
        Assert.Equal(1, h.Single(x => x.Name == "milch").Count);
    }

    private static string Todo(string item, string? section = null, bool done = false) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VTODO\r\nUID:x\r\nSUMMARY:" + item + "\r\nX-DM-ITEM:" + item + "\r\n"
        + (section != null ? "X-DM-SECTION:" + section + "\r\n" : "") + (done ? "STATUS:COMPLETED\r\n" : "") + "END:VTODO\r\nEND:VCALENDAR\r\n";

    [Fact]
    public void Ueber_CalDAV_eingetragene_Artikel_landen_in_den_Vorschlaegen()
    {
        // Die App traegt per CalDAV ein - neue Artikel lernen, Abhaken nicht mitzaehlen
        var dir = Directory.CreateTempSubdirectory("dmlearn");
        var db = new Core.UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "t");
        Lists.Update(db, 7, Lists.Shop, null);
        Lists.LearnFromIcs(db, 7, null, Todo("Hafermilch", "Kühlregal"));
        Lists.LearnFromIcs(db, 7, Todo("Hafermilch", "Kühlregal"), Todo("Hafermilch", "Kühlregal", done: true));
        var h = Lists.Get(db, 7).History.Single();
        Assert.Equal("Hafermilch", h.Name);
        Assert.Equal(1, h.Count);
        Assert.Equal("Kühlregal", h.Section);
        Lists.LearnFromIcs(db, 7, null, "kaputt");
        Assert.Single(Lists.Get(db, 7).History);
    }

    [Fact]
    public void Foto_am_Artikel_bleibt_beim_Vorschlag_und_geht_mit_ihm()
    {
        var dir = Directory.CreateTempSubdirectory("dmphoto");
        var db = new Core.UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "t");
        Lists.Update(db, 7, Lists.Shop, null);
        using var bmp = new SkiaSharp.SKBitmap(1600, 1200);
        using (var c = new SkiaSharp.SKCanvas(bmp)) c.Clear(SkiaSharp.SKColors.Orange);
        var png = bmp.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100).ToArray();

        // Auch fuer einen Artikel, der noch nie auf der Liste stand
        var first = Lists.SetPhoto(db, 7, "Joghurt", png);
        Assert.NotNull(first);
        Assert.Null(Lists.SetPhoto(db, 7, "Joghurt", [1, 2, 3]));   // kein Bild: nichts geaendert
        Assert.Equal(first, Lists.Get(db, 7).History.Single().Photo);
        var data = Lists.Photo(db, 7, first!)!;
        using (var small = SkiaSharp.SKBitmap.Decode(data)) Assert.Equal(Lists.PhotoSize, Math.Max(small.Width, small.Height));
        Assert.Null(Lists.Photo(db, 8, first!));   // gehoert zu einer anderen Liste

        // Ersetzen raeumt das alte weg, Lernen behaelt das Foto, Vergessen nimmt es mit
        var second = Lists.SetPhoto(db, 7, "joghurt", png)!;
        Assert.NotEqual(first, second);
        Assert.Null(db.Get("pim.listphoto." + first));
        Lists.Learn(db, 7, "Joghurt", "Becher", "Kühlregal");
        Assert.Equal(second, Lists.Get(db, 7).History.Single().Photo);
        Lists.Forget(db, 7, "Joghurt");
        Assert.Null(db.Get("pim.listphoto." + second));

        Lists.SetPhoto(db, 7, "Brot", png);
        Lists.RemovePhoto(db, 7, "Brot");
        Assert.Null(Lists.Get(db, 7).History.Single().Photo);
    }
}
