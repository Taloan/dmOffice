using DmOffice.Core;
using DmOffice.Notes;
using Notizi.Sync;

namespace DmOffice.Tests;

/// <summary>
/// notizi als zweites Geraet gegen einen laufenden dmOffice: derselbe
/// WebDavTransport und dieselbe SyncEngine wie in der Android-App.
/// Laeuft nur mit DMOFFICE_IT_URL, DMOFFICE_IT_USER, DMOFFICE_IT_TOKEN, DMOFFICE_IT_E2EE.
/// </summary>
public class NotiziInteropTests
{
    [Fact]
    public async Task Zweites_Geraet_liest_und_schreibt_ueber_WebDAV()
    {
        var url = Environment.GetEnvironmentVariable("DMOFFICE_IT_URL");
        if (url == null) return;
        var user = Environment.GetEnvironmentVariable("DMOFFICE_IT_USER")!;
        var token = Environment.GetEnvironmentVariable("DMOFFICE_IT_TOKEN")!;
        var e2ee = Environment.GetEnvironmentVariable("DMOFFICE_IT_E2EE")!;

        var dir = Directory.CreateTempSubdirectory("dmit");
        var db = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "test");
        var local = new NotesDb(db);

        var transport = new WebDavTransport(new Uri(url.TrimEnd('/') + $"/dav/files/{user}/notizisync/notizi%20pr/"), user, token);
        var store = await SyncStore.OpenAsync(transport, e2ee);
        Assert.NotNull(store);

        var first = await new SyncEngine(store!, local, "test-handy").RunAsync();
        Assert.Empty(first.Problems);
        var notes = local.GetAllActive();
        Assert.Contains(notes, n => n.Plain.Contains("Milch"));

        // Auf dem "Handy" schreiben und zurueck
        var id = local.CreateNote(null);
        local.SaveNote(id, "<div data-notizi=\"1\"><p>Vom Handy</p></div>", "Vom Handy");
        var second = await new SyncEngine(store!, local, "test-handy").RunAsync();
        Assert.Equal(1, second.Uploaded);
        Assert.Empty(second.Problems);
    }
}
