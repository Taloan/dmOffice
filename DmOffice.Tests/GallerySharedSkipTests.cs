using DmOffice.Gallery;

namespace DmOffice.Tests;

/// <summary>Der Galerie-Scan gleicht den gemeinsamen Bestand nur ab, wenn sich dort etwas geändert hat: jeder Schreibzugriff zählt, Lesen nicht.</summary>
public class GallerySharedSkipTests
{
    [Fact]
    public void Schreiben_zaehlt_Lesen_nicht()
    {
        var dir = Directory.CreateTempSubdirectory("dmshared").FullName;
        try
        {
            var shared = new GalleryShared(Path.Combine(dir, "_shared.db"), new byte[32]);
            var start = shared.Changes;
            shared.Put("m1", "a.jpg", 1, 2, null, [1, 2, 3]);
            Assert.Equal(start + 1, shared.Changes);
            _ = shared.Index("m1");
            _ = shared.Find("m1", "a.jpg", 1);
            _ = shared.Thumb("m1", "a.jpg", 1);
            Assert.Equal(start + 1, shared.Changes);
            shared.Offer("m1", "b.jpg", 2, 1, null, null);
            shared.Restamp("m1", "a.jpg", 1, 5);
            shared.Remove("m1", "b.jpg");
            Assert.Equal(start + 4, shared.Changes);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}
