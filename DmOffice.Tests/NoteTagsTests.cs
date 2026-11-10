using DmOffice.Notes;

namespace DmOffice.Tests;

/// <summary>Schlagwörter aus der Oberfläche: bereinigt, ohne Doppelte (Groß/klein gleich), ohne führendes #.</summary>
public class NoteTagsTests
{
    [Fact]
    public void Bereinigt_und_ohne_Doppelte()
    {
        var tags = NotesDb.CleanTags(["  #Familie ", "familie", "recipe", "", "  ", "Zwei   Worte", new string('x', 41)]);
        Assert.Equal(["Familie", "recipe", "Zwei Worte"], tags);
    }
}
