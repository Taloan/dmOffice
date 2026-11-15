using DmOffice.Core;

namespace DmOffice.Tests;

/// <summary>Arbeitsordner der Werkzeuge: unter dem Datenverzeichnis, nach Gebrauch und beim Start leer.</summary>
[Collection("server")]
public class WorkDirTests
{
    [Fact]
    public void Unter_dem_Datenverzeichnis_und_beim_Start_aufgeraeumt()
    {
        var data = Directory.CreateTempSubdirectory("dmwork").FullName;
        try
        {
            // Rest eines abgebrochenen Laufs
            var old = Path.Combine(data, WorkDir.FolderName, "dms-alt");
            Directory.CreateDirectory(old);
            File.WriteAllText(Path.Combine(old, "in.pdf"), "rest");
            WorkDir.Init(data);
            Assert.False(Directory.Exists(old));

            var dir = WorkDir.Create("dms");
            Assert.StartsWith(Path.Combine(data, WorkDir.FolderName) + Path.DirectorySeparatorChar, dir);
            File.WriteAllText(Path.Combine(dir, "in.pdf"), "inhalt");
            Directory.CreateDirectory(Path.Combine(dir, "sub"));
            File.WriteAllText(Path.Combine(dir, "sub", "p1.jpg"), "seite");
            WorkDir.Delete(dir);
            Assert.False(Directory.Exists(dir));
            // Ocr nutzt denselben Ort
            var ocr = DmOffice.Search.Ocr.TempDir();
            Assert.StartsWith(Path.Combine(data, WorkDir.FolderName), ocr);
            DmOffice.Search.Ocr.Clean(ocr);
            Assert.False(Directory.Exists(ocr));
        }
        finally { Directory.Delete(data, true); }
    }
}
