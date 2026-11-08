using DmOffice.Dav;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>Geänderte Sidecar-Datei in der Ablage: genau ihr Foto wird neu gelesen, die anderen im Ordner nicht.</summary>
[Collection("server")]
public class SidecarRefreshTests : IClassFixture<FilesServerFixture>
{
    private readonly FilesServerFixture _fx;
    public SidecarRefreshTests(FilesServerFixture fx) => _fx = fx;

    [Fact]
    public void Neue_Xmp_markiert_nur_ihr_Foto()
    {
        var db = _fx.Db;
        FileMeta.Ensure(db);
        FileStore.EnsureDir(db, "Sc");
        FileStore.Put(db, "Sc/IMG_1.arw", new byte[100], "image/x-sony-arw");
        FileStore.Put(db, "Sc/IMG_2.arw", new byte[100], "image/x-sony-arw");
        db.Exec("INSERT OR REPLACE INTO file_meta(path, etag, ok) SELECT path, etag, 1 FROM files WHERE path IN ('Sc/IMG_1.arw', 'Sc/IMG_2.arw')");
        FileStore.Put(db, "Sc/IMG_1.xmp", "<x/>"u8.ToArray(), "application/rdf+xml");
        string? Etag(string p) => db.Scalar("SELECT etag FROM file_meta WHERE path = $p", ("$p", p)) as string;
        Assert.Null(Etag("Sc/IMG_1.arw"));
        Assert.NotNull(Etag("Sc/IMG_2.arw"));
    }
}
