using DmOffice.Dav;

namespace DmOffice.Tests;

/// <summary>Hochgeladen als application/octet-stream (Nextcloud-Client, Sync-Apps): Typ nach der Endung, sonst gilt ein Foto nicht als Bild.</summary>
[Collection("server")]
public class FileMimeTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public FileMimeTests(ChatFixture fx) => _fx = fx;

    [Fact]
    public void Octet_stream_nach_Endung()
    {
        var db = _fx.Space("anna").Db;
        FileStore.Put(db, "mime-test_ILCE-7CM2.JPG", [0xFF, 0xD8, 0xFF], "application/octet-stream");
        Assert.Equal("image/jpeg", FileStore.Get(db, "mime-test_ILCE-7CM2.JPG")!.Mime);
        FileStore.Put(db, "mime-test.unbekannt", [1, 2, 3], "application/octet-stream");
        Assert.Equal("application/octet-stream", FileStore.Get(db, "mime-test.unbekannt")!.Mime);

        // Altbestand: einmal nachbestimmen
        db.Exec("UPDATE files SET mime = 'application/octet-stream' WHERE path = 'mime-test_ILCE-7CM2.JPG'");
        Assert.True(FileMeta.FixMimes(db) >= 1);
        Assert.Equal("image/jpeg", FileStore.Get(db, "mime-test_ILCE-7CM2.JPG")!.Mime);
    }
}
