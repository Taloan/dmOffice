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
}
