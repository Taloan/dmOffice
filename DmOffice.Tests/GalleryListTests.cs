using System.Text.Json;
using DmOffice.Dav;
using DmOffice.Gallery;

namespace DmOffice.Tests;

/// <summary>Kompakte Liste der Galerie-Ordner: Ordner und Texte je einmal, Zeilen in fester Reihenfolge, leere Enden fehlen.</summary>
public class GalleryListTests
{
    [Fact]
    public void Kompakte_Form()
    {
        var meta = new FileMeta.Row("a/b/x.jpg", 1700000000000, "X-E5", "XF 23", 23, 2, 0.01, 400, null, null, 4000, 3000, null, 3, null, null);
        var entries = new List<GalleryFolders.Entry>
        {
            new("s1", "a/b/x.jpg", 100, 1600000000000, "image/jpeg", meta, "Strand"),
            new("s1", "a/b/y.jpg", 200, 1600000001000, "image/jpeg", null),
            new("s1", "top.png", 5, 1, null, new FileMeta.Row("top.png", null, "X-E5", null, null, null, null, null, null, null, null, null, null, null, null, null)),
        };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(GalleryList.Compact(entries), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var root = doc.RootElement;
        Assert.Equal(3, root.GetProperty("v").GetInt32());
        var dirs = root.GetProperty("dirs").EnumerateArray().Select(x => x.GetString()).ToList();
        Assert.Equal(["s1/a/b/", "s1/"], dirs);
        var strs = root.GetProperty("strs").EnumerateArray().Select(x => x.GetString()).ToList();
        var rows = root.GetProperty("rows").EnumerateArray().ToList();
        string? S(JsonElement r, int i) => i < r.GetArrayLength() && r[i].ValueKind == JsonValueKind.Number ? strs[r[i].GetInt32()] : null;

        var x = rows[0];
        Assert.Equal(19, x.GetArrayLength());
        Assert.Equal("s1/a/b/x.jpg", dirs[x[0].GetInt32()] + x[1].GetString());
        Assert.Equal(100, x[2].GetInt64());
        Assert.Equal(1600000000000, x[3].GetInt64());
        Assert.Equal("image/jpeg", S(x, 4));
        Assert.Equal(1700000000000, x[5].GetInt64());
        Assert.Equal("X-E5", S(x, 6));
        Assert.Equal("XF 23", S(x, 7));
        Assert.Equal(400, x[11].GetInt32());
        Assert.Equal(4000, x[12].GetInt32());
        Assert.Equal(3, x[15].GetInt32());
        Assert.Equal("Strand", S(x, 18));

        // Ohne Bildangaben: nur Ordner, Name, Größe, Datum, Typ
        Assert.Equal(5, rows[1].GetArrayLength());
        Assert.Equal(rows[0][4].GetInt32(), rows[1][4].GetInt32());
        // Datei im obersten Ordner, Text nur einmal in strs
        Assert.Equal("s1/top.png", dirs[rows[2][0].GetInt32()] + rows[2][1].GetString());
        Assert.Equal(JsonValueKind.Null, rows[2][4].ValueKind);
        Assert.Equal(rows[0][6].GetInt32(), rows[2][6].GetInt32());
        Assert.Equal(7, rows[2].GetArrayLength());
    }
}
