using System.Text;
using System.Text.Json.Nodes;
using DmOffice.Core.Cryptomator;

namespace DmOffice.Tests;

/// <summary>Veränderte Tresordateien (dir.c9r, masterkey.cryptomator) dürfen den Server weder in eine Schleife noch in Speichermangel führen.</summary>
public class CryptomatorLimitsTests
{
    private static async Task<(string Dir, CryptomatorVault Vault)> NewVault()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cm-lim-" + Guid.NewGuid().ToString("N"));
        var key = Masterkey.Generate();   // gehört dem Tresor
        var v = LocalVault.Create(dir, key, "pw", scryptCost: 1024);
        v.CreateDirectory("A");
        v.CreateDirectory("A/B");
        await v.WriteAsync("A/B/x.txt", new MemoryStream("x"u8.ToArray()));
        await v.WriteAsync("oben.txt", new MemoryStream("o"u8.ToArray()));
        return (dir, v);
    }

    [Theory]
    [InlineData("root")]
    [InlineData("parent")]
    [InlineData("self")]
    public async Task Ordner_der_auf_einen_Ordner_darueber_zeigt_fuehrt_in_keine_Schleife(string target)
    {
        var (dir, v) = await NewVault();
        try
        {
            v.CreateDirectory("A/B/C");
            var (node, id) = target switch
            {
                "root" => ("A/B", ""),                       // leere dir.c9r = Wurzel
                "parent" => ("A/B", v.DirIdOf("A")!),
                _ => ("A/B/C", v.DirIdOf("A/B")!),           // C zeigt auf B, in dem es liegt
            };
            File.WriteAllText(Path.Combine(dir, v.NodeFor(node), VaultNames.DirFile), id);
            Assert.Null(v.DirIdOf(node));
            Assert.Throws<DirectoryNotFoundException>(() => v.List(node));
            Assert.NotNull(v.OfflineFiles(""));
            v.Delete("A");
            // Die Wurzel bleibt unberührt
            Assert.Contains(v.List(""), e => e.Name == "oben.txt");
            Assert.DoesNotContain(v.List(""), e => e.Name == "A");
        }
        finally { v.Dispose(); Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Gemeinsame_Kennung_zweier_Ordner_wird_nur_einmal_abgelaufen()
    {
        var (dir, v) = await NewVault();
        try
        {
            v.CreateDirectory("A/C");
            // C und B teilen sich eine Kennung, B zeigt zusätzlich in C hinein: ohne Merkliste liefe das Ablaufen im Kreis
            v.CreateDirectory("A/B/D");
            File.WriteAllText(Path.Combine(dir, v.NodeFor("A/B/D"), VaultNames.DirFile), v.DirIdOf("A/C")!);
            v.CreateDirectory("A/C/E");
            File.WriteAllText(Path.Combine(dir, v.NodeFor("A/C/E"), VaultNames.DirFile), v.DirIdOf("A/B")!);
            Assert.NotNull(v.OfflineFiles("A"));
            v.Delete("A");
            Assert.Contains(v.List(""), e => e.Name == "oben.txt");
        }
        finally { v.Dispose(); Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Zu_tiefe_Pfade_werden_abgelehnt()
    {
        var (dir, v) = await NewVault();
        try
        {
            var deep = string.Join('/', Enumerable.Repeat("a", CryptomatorVault.MaxDepth + 1));
            Assert.Throws<ArgumentException>(() => v.DirIdOf(deep));
        }
        finally { v.Dispose(); Directory.Delete(dir, recursive: true); }
    }

    private static byte[] WithScrypt(byte[] file, int n, int r)
    {
        var json = JsonNode.Parse(file)!;
        json["scryptCostParam"] = n;
        json["scryptBlockSize"] = r;
        return Encoding.UTF8.GetBytes(json.ToJsonString());
    }

    [Theory]
    [InlineData(1 << 20, 8)]
    [InlineData(1 << 18, 8)]
    [InlineData(1 << 16, 16)]
    public void Zu_hohe_scrypt_Werte_werden_vor_dem_Ableiten_abgelehnt(int n, int r)
    {
        using var key = Masterkey.Generate();
        var file = MasterkeyFile.Write(key, "pw", cost: 1024);
        Assert.Throws<FormatException>(() => MasterkeyFile.Read(WithScrypt(file, n, r), "pw"));
    }

    [Fact]
    public void Cryptomator_Vorgabe_bleibt_lesbar()
    {
        using var key = Masterkey.Generate();
        var file = MasterkeyFile.Write(key, "pw", cost: 32768);
        using var back = MasterkeyFile.Read(file, "pw");
        Assert.NotNull(back);
    }
}
