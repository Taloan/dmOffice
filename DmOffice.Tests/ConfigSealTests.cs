using System.Text;
using System.Text.Json.Nodes;
using DmOffice.Core;

namespace DmOffice.Tests;

/// <summary>Siegel für users.json und server.json: Schreiben durch dmOffice, Prüfung beim Start, Neuversiegeln, Schlüsselwechsel.</summary>
public class ConfigSealTests
{
    private const string EnvKey = "0123456789abcdef0123456789abcdef0123456789abcdef";

    private static DataPaths NewData() => new(Directory.CreateTempSubdirectory("dmseal").FullName);

    private static string Users(DataPaths p) => Path.Combine(p.Root, "users.json");

    [Fact]
    public void Schreiben_durch_dmOffice_besteht_die_Pruefung()
    {
        var paths = NewData();
        var vault = new Vault(paths, EnvKey);
        var users = new UserStore(paths, vault);
        users.Create("anna", "Anna", "geheim-geheim-1", isAdmin: true);
        new ServerConfig(paths, vault).Update(s => s.DefaultFileQuotaMb = 77);
        Assert.True(File.Exists(Path.Combine(paths.Root, ConfigSeal.FileName)));
        var r = ConfigSeal.Check(paths, vault);
        Assert.Equal(ConfigSeal.State.Ok, r.State);
        Assert.Equal(["users.json", "server.json"], r.Files);
        // Siegel ohne Klartext-Inhalte
        Assert.DoesNotContain("anna", File.ReadAllText(Path.Combine(paths.Root, ConfigSeal.FileName)));
    }

    [Fact]
    public void Von_Hand_geaenderte_Benutzerliste_haelt_den_Start_an_bis_zum_Neuversiegeln()
    {
        var paths = NewData();
        var vault = new Vault(paths, EnvKey);
        new UserStore(paths, vault).Create("anna", "Anna", "geheim-geheim-1", isAdmin: false);
        new ServerConfig(paths, vault).Update(s => s.DefaultFileQuotaMb = 1);
        File.WriteAllText(Users(paths), File.ReadAllText(Users(paths)).Replace("\"IsAdmin\": false", "\"IsAdmin\": true"));

        var r = ConfigSeal.Check(paths, vault);
        Assert.Equal(ConfigSeal.State.Refused, r.State);
        Assert.Equal(["users.json"], r.Files);
        Assert.Contains(ConfigSeal.Command, r.Message);
        // Bleibt so, bis jemand bewusst neu versiegelt
        Assert.Equal(ConfigSeal.State.Refused, ConfigSeal.Check(paths, vault).State);

        var o = new StringWriter();
        Assert.Equal(0, ConfigSeal.Run(new Vault(paths, EnvKey, readOnly: true), paths, o));
        Assert.Contains("users.json", o.ToString());
        Assert.Contains("Verwaltungsrecht: anna", o.ToString());
        Assert.Equal(ConfigSeal.State.Ok, ConfigSeal.Check(paths, vault).State);
    }

    [Fact]
    public void Geloeschte_oder_mit_anderem_Schluessel_versiegelte_Datei_wird_erkannt()
    {
        var paths = NewData();
        var vault = new Vault(paths, EnvKey);
        new UserStore(paths, vault).Create("anna", "Anna", "geheim-geheim-1", isAdmin: true);
        File.Move(Users(paths), Users(paths) + ".weg");
        Assert.Equal(ConfigSeal.State.Refused, ConfigSeal.Check(paths, vault).State);
        File.Move(Users(paths) + ".weg", Users(paths));

        // Mit einem fremden Schlüssel versiegelt: passt nicht
        var other = new Vault(NewData(), "ein-ganz-anderer-hauptschluessel-0123456789");
        ConfigSeal.Reseal(paths.Root, other.Key);
        Assert.Equal(ConfigSeal.State.Refused, ConfigSeal.Check(paths, vault).State);

        // Unlesbares Siegel ebenso
        File.WriteAllText(Path.Combine(paths.Root, ConfigSeal.FileName), "{ kaputt");
        var r = ConfigSeal.Check(paths, vault);
        Assert.Equal(ConfigSeal.State.Refused, r.State);
        Assert.Contains(ConfigSeal.FileName, r.Files);
    }

    [Fact]
    public void Ohne_Siegel_werden_vorhandene_Dateien_einmal_versiegelt()
    {
        var paths = NewData();
        var vault = new Vault(paths, EnvKey);
        Assert.Equal(ConfigSeal.State.Ok, ConfigSeal.Check(paths, vault).State);
        File.WriteAllText(Users(paths), "[]");
        File.WriteAllText(Path.Combine(paths.Root, "server.json"), "{}");
        var r = ConfigSeal.Check(paths, vault);
        Assert.Equal(ConfigSeal.State.SealedNow, r.State);
        Assert.Equal(2, r.Files.Count);
        Assert.Equal(ConfigSeal.State.Ok, ConfigSeal.Check(paths, vault).State);
    }

    [Fact]
    public void Abbruch_zwischen_Siegel_und_Datei_gilt_bis_zum_naechsten_Start()
    {
        var paths = NewData();
        var vault = new Vault(paths, EnvKey);
        new UserStore(paths, vault).Create("anna", "Anna", "geheim-geheim-1", isAdmin: true);
        var old = File.ReadAllBytes(Users(paths));
        // Siegel schon für den neuen Inhalt geschrieben, Datei noch alt
        ConfigSeal.Record(paths.Root, "users.json", Encoding.UTF8.GetBytes("[]"), vault.Key);
        Assert.Equal(ConfigSeal.State.Ok, ConfigSeal.Check(paths, vault).State);
        Assert.Equal(old, File.ReadAllBytes(Users(paths)));
        // Der Start hat den vorigen Wert weggeräumt: der nicht geschriebene Inhalt gilt nicht mehr
        File.WriteAllText(Users(paths), "[]");
        Assert.Equal(ConfigSeal.State.Refused, ConfigSeal.Check(paths, vault).State);
    }

    [Fact]
    public void Umstieg_von_der_Schluesseldatei_auf_die_Variable_versiegelt_mit_dem_neuen_Schluessel()
    {
        var paths = NewData();
        var fileVault = new Vault(paths, null);
        new UserStore(paths, fileVault).Create("anna", "Anna", "geheim-geheim-1", isAdmin: true);
        new ServerConfig(paths, fileVault).Update(s => s.OfficeJwtSecret = fileVault.Wrap(Encoding.UTF8.GetBytes("jwt")));

        var envVault = new Vault(paths, EnvKey);
        Assert.True(envVault.Migrated > 0);
        Assert.Equal(ConfigSeal.State.Ok, ConfigSeal.Check(paths, envVault).State);
        // Wie beim Start: Benutzerliste umpacken und speichern
        _ = new UserStore(paths, envVault);
        envVault.DeleteOldKeyFile();
        Assert.Equal(ConfigSeal.State.Ok, ConfigSeal.Check(paths, new Vault(paths, EnvKey)).State);
    }

    [Fact]
    public void Vor_dem_Umstieg_geaenderte_Datei_bleibt_erkannt()
    {
        var paths = NewData();
        var fileVault = new Vault(paths, null);
        new UserStore(paths, fileVault).Create("anna", "Anna", "geheim-geheim-1", isAdmin: false);
        var users = JsonNode.Parse(File.ReadAllText(Users(paths)))!;
        users[0]!["IsAdmin"] = true;
        File.WriteAllText(Users(paths), users.ToJsonString());
        var envVault = new Vault(paths, EnvKey);
        Assert.Equal(ConfigSeal.State.Refused, ConfigSeal.Check(paths, envVault).State);
    }

    [Fact]
    public void Eingespielter_Stand_ohne_Siegel_wird_beim_Start_neu_versiegelt()
    {
        var paths = NewData();
        var vault = new Vault(paths, EnvKey);
        new UserStore(paths, vault).Create("leer", "Leer", "geheim-geheim-1", isAdmin: true);
        var pending = Path.Combine(paths.Root, S3Backup.PendingFolder);
        Directory.CreateDirectory(pending);
        File.WriteAllText(Path.Combine(pending, "backup.json"), "{}");
        File.WriteAllText(Path.Combine(pending, "users.json"), "[]");
        Assert.True(S3Backup.ApplyPending(paths) > 0);
        Assert.False(File.Exists(Path.Combine(paths.Root, ConfigSeal.FileName)));
        Assert.Equal(ConfigSeal.State.SealedNow, ConfigSeal.Check(paths, vault).State);
    }

    [Fact]
    public void Eingespielter_Stand_mit_Siegel_wird_geprueft()
    {
        var source = NewData();
        var vault = new Vault(source, EnvKey);
        new UserStore(source, vault).Create("anna", "Anna", "geheim-geheim-1", isAdmin: true);
        var paths = NewData();
        var pending = Path.Combine(paths.Root, S3Backup.PendingFolder);
        Directory.CreateDirectory(pending);
        File.WriteAllText(Path.Combine(pending, "backup.json"), "{}");
        File.Copy(Users(source), Path.Combine(pending, "users.json"));
        File.Copy(Path.Combine(source.Root, ConfigSeal.FileName), Path.Combine(pending, ConfigSeal.FileName));
        Assert.True(S3Backup.ApplyPending(paths) > 0);
        Assert.Equal(ConfigSeal.State.Ok, ConfigSeal.Check(paths, new Vault(paths, EnvKey)).State);
    }
}
