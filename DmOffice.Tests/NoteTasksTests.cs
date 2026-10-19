using DmOffice.Core;
using DmOffice.Notes;

namespace DmOffice.Tests;

/// <summary>Systemliste "Notizi-Aufgaben": Kaestchen aus Notizen lesen und abhaken.</summary>
public class NoteTasksTests
{
    private static NotesDb NewDb()
    {
        var dir = Directory.CreateTempSubdirectory("dmnotetasks");
        var db = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "t");
        return NotesDb.Of(new UserSpace(new UserRecord { Login = "t" }, db));
    }

    [Fact]
    public void Kaestchen_lesen_und_abhaken()
    {
        var notes = NewDb();
        var id = notes.CreateNote(null);
        notes.SaveNote(id,
            "<div data-notizi=\"1\"><p>Einkauf</p><p>☐ Milch &amp; Brot</p><ul><li>☑ <b>Butter</b></li><li>☐ Eier</li></ul><p>Preis ☐ nicht am Anfang</p></div>",
            "Einkauf\r\n☐ Milch & Brot\r\n☑ Butter\r\n☐ Eier\r\nPreis ☐ nicht am Anfang");

        var tasks = NoteTasks.All(notes);
        Assert.Equal(new[] { "Milch & Brot", "Butter", "Eier" }, tasks.Select(x => x.Text));
        Assert.Equal(new[] { false, true, false }, tasks.Select(x => x.Done));
        Assert.All(tasks, x => Assert.Equal(id, x.Note));

        var eier = tasks[2];
        Assert.True(NoteTasks.Set(notes, id, eier.Block, eier.N, eier.Text, true));
        var note = notes.GetNote(id)!;
        Assert.Contains("<li>☑ Eier</li>", note.Html);
        Assert.Contains("☑ Eier", note.Plain);
        Assert.Contains("Preis ☐ nicht", note.Html);   // fremdes Zeichen mitten im Text bleibt

        // Anderer Text an der Stelle (inzwischen geaendert): nichts tun
        Assert.False(NoteTasks.Set(notes, id, eier.Block, eier.N, "Quark", false));
        Assert.Contains("<li>☑ Eier</li>", notes.GetNote(id)!.Html);
    }
}
