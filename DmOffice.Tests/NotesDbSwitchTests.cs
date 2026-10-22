using DmOffice.Core;
using DmOffice.Notes;
using Microsoft.Extensions.Logging.Abstractions;

namespace DmOffice.Tests;

/// <summary>
/// Datenbankwechsel bei den Notizen: Notizen aus Datenbank A duerfen nie in Datenbank B landen - weder ueber
/// den Wechsel in den Einstellungen noch im Notizen-Bereich, auch nicht, wenn der Abgleich zwischendurch aus war.
/// </summary>
public class NotesDbSwitchTests
{
    private static (UserSpace Space, NotesDb Notes, NotesSync Sync) Setup()
    {
        var dir = Directory.CreateTempSubdirectory("dmnoteswitch");
        var db = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "t");
        var space = new UserSpace(new UserRecord { Id = Guid.NewGuid().ToString("N"), Login = "t" }, db);
        space.UpdateSettings(s => { s.Notizi.Enabled = true; s.Notizi.E2eePassword = "geheim-123"; s.Notizi.DbFolder = "A"; });
        return (space, NotesDb.Of(space), new NotesSync(null!, NullLogger<NotesSync>.Instance));
    }

    private static void Switch(UserSpace space, string folder, bool enabled = true) =>
        space.UpdateSettings(s => { s.Notizi.Enabled = enabled; s.Notizi.DbFolder = folder; s.Notizi.SwitchDb(); });

    private static List<string> Titles(NotesDb notes) => notes.GetAllActive().Select(n => n.Plain.Split('\n')[0].Trim()).ToList();

    private static long Note(NotesDb notes, string text)
    {
        var id = notes.CreateNote(null);
        notes.SaveNote(id, $"<div data-notizi=\"1\"><p>{text}</p></div>", text);
        return id;
    }

    [Fact]
    public async Task Notizen_aus_A_landen_nie_in_B_auch_nicht_mit_Abgleich_zwischendurch_aus()
    {
        var (space, notes, sync) = Setup();
        Note(notes, "ICE 517 Rückfahrt");
        await sync.RunAsync(space, CancellationToken.None);
        Assert.Equal(0, notes.PendingCount());

        // Abgleich aus, dann auf eine neue Datenbank B wechseln (wie POST /api/notes/database ohne Abgleich)
        Switch(space, "A", enabled: false);
        Switch(space, "B");
        await sync.RunAsync(space, CancellationToken.None);
        Assert.Null(NotesSync.StateOf(space).Error);
        Assert.Empty(Titles(notes));   // hier geleert, B ist leer geblieben

        Note(notes, "nur in B");
        await sync.RunAsync(space, CancellationToken.None);

        // Zurueck zu A: dort nur die eigene Notiz, nichts aus B
        Switch(space, "A");
        await sync.RunAsync(space, CancellationToken.None);
        Assert.Equal(new[] { "ICE 517 Rückfahrt" }, Titles(notes));

        // und wieder B: nur die Notiz aus B
        Switch(space, "B");
        await sync.RunAsync(space, CancellationToken.None);
        Assert.Equal(new[] { "nur in B" }, Titles(notes));
    }

    [Fact]
    public async Task Offene_Aenderungen_aus_A_bleiben_stehen_statt_nach_B_zu_wandern()
    {
        var (space, notes, sync) = Setup();
        Note(notes, "schon in A");
        await sync.RunAsync(space, CancellationToken.None);
        Note(notes, "noch nicht abgeglichen");

        Switch(space, "B");
        await sync.RunAsync(space, CancellationToken.None);
        Assert.NotNull(NotesSync.StateOf(space).Error);
        Assert.Equal(2, Titles(notes).Count);   // nichts verloren

        Switch(space, "A");
        await sync.RunAsync(space, CancellationToken.None);
        Assert.Null(NotesSync.StateOf(space).Error);
        Assert.Equal(0, notes.PendingCount());

        // B hat nichts davon bekommen
        Switch(space, "B");
        await sync.RunAsync(space, CancellationToken.None);
        Assert.Empty(Titles(notes));
    }

    [Fact]
    public async Task Hier_angelegte_Notizen_gehen_beim_ersten_Abgleich_in_die_gewaehlte_Datenbank()
    {
        var (space, notes, sync) = Setup();
        Switch(space, "", enabled: false);
        Note(notes, "vor dem Einrichten");
        Switch(space, "B");
        await sync.RunAsync(space, CancellationToken.None);
        Assert.Equal(0, notes.PendingCount());

        Switch(space, "A");
        await sync.RunAsync(space, CancellationToken.None);
        Assert.Empty(Titles(notes));
        Switch(space, "B");
        await sync.RunAsync(space, CancellationToken.None);
        Assert.Equal(new[] { "vor dem Einrichten" }, Titles(notes));
    }
}
