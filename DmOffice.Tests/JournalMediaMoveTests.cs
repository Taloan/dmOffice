using System.Net.Http.Json;
using System.Text.Json;
using DmOffice.Dav;
using DmOffice.Journal;

namespace DmOffice.Tests;

/// <summary>Tagebuch-Medien im Systemordner: unsichtbar in Dateiablage und Suche; Umzug aus dem alten Ordner „Tagebuch“.</summary>
[Collection("server")]
public class JournalMediaMoveTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public JournalMediaMoveTests(ChatFixture fx) => _fx = fx;

    [Fact]
    public async Task Alte_Medien_ziehen_um_eigene_Dateien_bleiben()
    {
        var space = _fx.Space("anna");
        var db = space.Db;
        var journal = JournalStore.Journals(db).FirstOrDefault() ?? JournalStore.Create(db, "Tagebuch", null);
        FileStore.EnsureDir(db, "Tagebuch/2025/01");
        FileStore.Put(db, "Tagebuch/2025/01/bild.jpg", [1, 2, 3], "image/jpeg");
        FileStore.Put(db, "Tagebuch/eigene-notiz.txt", "meins"u8.ToArray(), "text/plain");
        JournalStore.Save(db, journal, new JournalInput
        {
            Journal = journal.Id, Date = new DateTime(2025, 1, 5, 10, 0, 0, DateTimeKind.Utc), Html = "<p>Winter</p>",
            Media = [new JournalMedia("Tagebuch/2025/01/bild.jpg", "image/jpeg", 3)],
        });

        Assert.Equal(1, JournalMediaMove.Run(space));
        Assert.Null(FileStore.Get(db, "Tagebuch/2025/01/bild.jpg"));
        Assert.Null(FileStore.Get(db, "Tagebuch/2025"));
        Assert.NotNull(FileStore.Get(db, "sys-dmoffice-journal/2025/01/bild.jpg"));
        Assert.NotNull(FileStore.Get(db, "Tagebuch/eigene-notiz.txt"));
        var entry = Assert.Single(JournalStore.Entries(db, journal));
        Assert.Equal("sys-dmoffice-journal/2025/01/bild.jpg", Assert.Single(entry.Media).Path);
        // Zweiter Durchgang: nichts mehr unter „Tagebuch/“
        Assert.Equal(0, JournalMediaMove.Run(space));

        // Übrig gebliebener leerer Monatsordner (Foto ohne Eintrag gelöscht): beim nächsten Start weg, eigene Dateien bleiben
        FileStore.EnsureDir(db, "Tagebuch/2026/10");
        Assert.Equal(0, JournalMediaMove.Run(space));
        Assert.Null(FileStore.Get(db, "Tagebuch/2026"));
        Assert.NotNull(FileStore.Get(db, "Tagebuch/eigene-notiz.txt"));
        // Ein selbst angelegter leerer Ordner „Tagebuch“ bleibt
        FileStore.Delete(db, "Tagebuch/eigene-notiz.txt");
        JournalMediaMove.Run(space);
        Assert.NotNull(FileStore.Get(db, "Tagebuch"));

        // Der Systemordner erscheint weder in der Dateiablage noch in der Suche
        var root = await (await _fx.As("anna").GetAsync("/api/files/?path=")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.DoesNotContain(root.GetProperty("items").EnumerateArray(), i => i.GetProperty("name").GetString() == JournalMediaStore.Root);
        var found = await (await _fx.As("anna").GetAsync("/api/files/search?q=bild")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.DoesNotContain(found.GetProperty("items").EnumerateArray(), i => i.GetProperty("path").GetString()!.StartsWith(JournalMediaStore.Root));
    }
}
