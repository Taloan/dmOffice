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
    public void Ordner_verschieben_nicht_in_sich_selbst()
    {
        var dir = Directory.CreateTempSubdirectory("dmnotefolders");
        var db = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "t");
        var notes = NotesDb.Of(new UserSpace(new UserRecord { Login = "t" }, db));
        notes.GetFolders(null);   // Tabellen anlegen
        void Folder(string id, string? parent) =>
            db.Exec("INSERT INTO note_folders(sync_id,sync_rev,parent,name,sort_order,created,modified) VALUES($s,1,$p,$s,1,'2026-01-01','2026-01-01')", ("$s", id), ("$p", parent));
        Folder("a", null); Folder("b", "a"); Folder("c", null);

        notes.MoveFolder("c", "b");
        Assert.Equal("b", notes.GetFolder("c")!.Parent);
        Assert.Equal(2L, Convert.ToInt64(db.Scalar("SELECT sync_rev FROM note_folders WHERE sync_id='c'")));   // geht an notizi
        notes.MoveFolder("c", null);
        Assert.Null(notes.GetFolder("c")!.Parent);

        Assert.Throws<ArgumentException>(() => notes.MoveFolder("a", "a"));
        Assert.Throws<ArgumentException>(() => notes.MoveFolder("a", "b"));   // eigener Unterordner
        Assert.Throws<KeyNotFoundException>(() => notes.MoveFolder("a", "gibtsnicht"));
        Assert.Throws<KeyNotFoundException>(() => notes.MoveFolder("gibtsnicht", null));
        Assert.Null(notes.GetFolder("a")!.Parent);
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
