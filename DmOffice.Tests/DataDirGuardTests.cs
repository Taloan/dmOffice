using DmOffice.Core;
using DmOffice.Storage;

namespace DmOffice.Tests;

/// <summary>Ordner auf dem Server gegen das Datenverzeichnis prüfen: auch über Verknüpfungen und andere Einbindungen.</summary>
public class DataDirGuardTests
{
    [Fact]
    public void Datenverzeichnis_auch_unter_anderem_Pfad_erkannt()
    {
        var tmp = Directory.CreateTempSubdirectory("dmguard").FullName;
        try
        {
            var paths = new DataPaths(Path.Combine(tmp, "srv", "appdata", "dmoffice"));
            var id = S3Mirror.ServerId(paths);
            var other = Directory.CreateDirectory(Path.Combine(tmp, "andere")).FullName;

            Assert.False(DataDirGuard.Touches(other, paths));
            Assert.True(DataDirGuard.Touches(paths.Root, paths));
            Assert.True(DataDirGuard.Touches(Path.Combine(tmp, "srv"), paths));
            Assert.True(DataDirGuard.Touches(paths.Users, paths));

            // Derselbe Ordner unter einer zweiten Einbindung: gleiche server.id an passender Stelle
            var view = Path.Combine(tmp, "shares");
            Directory.CreateDirectory(Path.Combine(view, "appdata", "dmoffice", "users"));
            File.WriteAllText(Path.Combine(view, "appdata", "dmoffice", S3Mirror.ServerIdFile), id);
            Assert.True(DataDirGuard.Touches(view, paths));
            Assert.True(DataDirGuard.Touches(Path.Combine(view, "appdata"), paths));
            Assert.True(DataDirGuard.Touches(Path.Combine(view, "appdata", "dmoffice", "users"), paths));
            // Andere Kennung: ein anderer Server, kein Treffer
            File.WriteAllText(Path.Combine(view, "appdata", "dmoffice", S3Mirror.ServerIdFile), Guid.NewGuid().ToString("N"));
            Assert.False(DataDirGuard.Touches(view, paths));

            // Verknüpfung auf einen Ordner über dem Datenverzeichnis (wo das System es erlaubt)
            var link = Path.Combine(tmp, "verknuepft");
            try { Directory.CreateSymbolicLink(link, Path.Combine(tmp, "srv")); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { link = null; }
            if (link != null) Assert.True(DataDirGuard.Touches(link, paths));
        }
        finally { try { Directory.Delete(tmp, true); } catch (IOException) { } }
    }
}
