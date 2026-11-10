using System.Security.Cryptography;
using DmOffice.Core;

namespace DmOffice.Tests;

/// <summary>Zugangsdaten für den Notfall: nur mit demselben Hauptschlüssel lesbar, auch aus einer ganzen gespeicherten Datei.</summary>
public class StorageProfileTests
{
    private static Vault VaultWith(string key) => new(new DataPaths(Directory.CreateTempSubdirectory("dmprofile").FullName), key);

    [Fact]
    public void Nur_mit_demselben_Hauptschluessel_zu_oeffnen()
    {
        var data = new StorageProfile.Places(new StorageProfile.Data("s3", "http://garage:3900", "garage", "dmoffice", "dmoffice", "GKabc", "geheim", null, null, null, null),
            new StorageProfile.Data("s3", "https://fsn1.your-objectstorage.com", "fsn1", "core", "dmoffice", "HZabc", "auchgeheim", null, null, null, null));
        var line = StorageProfile.Seal(VaultWith("schluessel-eins"), data);
        Assert.StartsWith(StorageProfile.Marker, line);
        Assert.DoesNotContain("geheim", line);
        Assert.DoesNotContain("objectstorage", line);
        // Neuer Server mit demselben DMOFFICE_MASTER_KEY: aus der ganzen Datei (Kopfzeilen davor) gelesen
        var file = "dmOffice – Zugangsdaten\nErstellt heute\n\n" + line + "\n";
        var back = StorageProfile.Open(VaultWith("schluessel-eins"), file)!;
        Assert.Equal(data.Files, back.Files);
        Assert.Equal(data.Core, back.Core);
        // Anderer Schlüssel: klar abgelehnt
        Assert.Throws<AuthenticationTagMismatchException>(() => StorageProfile.Open(VaultWith("schluessel-zwei"), line));
        // Keine Zeile, oder abgeschnitten: nichts
        Assert.Null(StorageProfile.Open(VaultWith("schluessel-eins"), "irgendwas"));
        Assert.Null(StorageProfile.Open(VaultWith("schluessel-eins"), StorageProfile.Marker + "abc"));
    }
}
