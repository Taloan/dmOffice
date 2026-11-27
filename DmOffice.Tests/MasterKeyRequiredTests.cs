using DmOffice.Core;

namespace DmOffice.Tests;

/// <summary>Start ohne eigenen Hauptschlüssel: neue Installationen starten nicht, bestehende mit Schlüsseldatei laufen weiter.</summary>
public class MasterKeyRequiredTests
{
    private static DataPaths NewData() => new(Path.Combine(Path.GetTempPath(), "dmkey" + Guid.NewGuid().ToString("N")));

    [Fact]
    public void Neues_Datenverzeichnis_ohne_Schluessel_startet_nicht()
    {
        var paths = NewData();
        var msg = Vault.StartRefused(paths, null);
        Assert.NotNull(msg);
        Assert.Contains("DMOFFICE_MASTER_KEY ist nicht gesetzt", msg);
        Assert.Contains("openssl rand -hex 32", msg);
        Assert.NotNull(Vault.StartRefused(paths, "  "));
        // Keine Schlüsseldatei angelegt
        Assert.False(File.Exists(Path.Combine(paths.Root, "master.key")));
    }

    [Fact]
    public void Leere_Benutzerliste_gilt_als_neue_Installation()
    {
        var paths = NewData();
        File.WriteAllText(Path.Combine(paths.Root, "users.json"), "[]");
        Assert.Contains("DMOFFICE_MASTER_KEY ist nicht gesetzt", Vault.StartRefused(paths, null));
    }

    [Fact]
    public void Mit_Umgebungsvariable_startet_jede_Installation()
    {
        var paths = NewData();
        Assert.Null(Vault.StartRefused(paths, "0123456789abcdef0123456789abcdef"));
        File.WriteAllText(Path.Combine(paths.Root, "users.json"), "[{\"Name\":\"a\"}]");
        Assert.Null(Vault.StartRefused(paths, "0123456789abcdef0123456789abcdef"));
    }

    [Fact]
    public void Bestehende_Installation_mit_Schluesseldatei_startet_weiter()
    {
        var paths = NewData();
        _ = new Vault(paths, null);
        Assert.True(File.Exists(Path.Combine(paths.Root, "master.key")));
        File.WriteAllText(Path.Combine(paths.Root, "users.json"), "[{\"Name\":\"a\"}]");
        Assert.Null(Vault.StartRefused(paths, null));
    }

    [Fact]
    public void Benutzer_ohne_jeden_Schluessel_nennt_den_bisherigen_Wert()
    {
        var paths = NewData();
        File.WriteAllText(Path.Combine(paths.Root, "users.json"), "[{\"Name\":\"a\"}]");
        var msg = Vault.StartRefused(paths, null);
        Assert.NotNull(msg);
        Assert.Contains("bisherigen", msg);
        File.WriteAllText(Path.Combine(paths.Root, "users.json"), "kaputt{");
        Assert.Contains("bisherigen", Vault.StartRefused(paths, null));
    }
}
