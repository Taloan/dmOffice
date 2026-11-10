using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using DmOffice.Core.Cryptomator;

namespace DmOffice.Tests;

/// <summary>
/// Cryptomator-Format (Vault-Format 8) für den Tresor (docs/tresor-cryptomator-konzept.md, Abschnitt 7): Testvektoren, Lesen eines
/// mit Cryptomator (cryptofs 2.10, cryptolib 2.2) angelegten Tresors aus Fixtures/cryptomator und - mit installierter
/// Cryptomator-App - die Gegenprobe, dass Cryptomator einen von dmOffice geschriebenen Tresor liest.
/// </summary>
public class CryptomatorFormatTests
{
    private const string ReferencePassword = "Pässwört ü 1";
    private static string Fixtures => Path.Combine(AppContext.BaseDirectory, "Fixtures", "cryptomator");

    private static byte[] Hex(string s) => Convert.FromHexString(s.Replace(" ", ""));

    // ---------------- Testvektoren ----------------

    [Fact]
    public void AesSiv_RFC5297_A1_deterministisch()
    {
        // K = K1 (MAC) || K2 (CTR)
        var key = Hex("fffefdfc fbfaf9f8 f7f6f5f4 f3f2f1f0 f0f1f2f3 f4f5f6f7 f8f9fafb fcfdfeff");
        var ad = Hex("10111213 14151617 18191a1b 1c1d1e1f 20212223 24252627");
        var plain = Hex("11223344 55667788 99aabbcc ddee");
        var expected = Hex("85632d07 c6e8f37f 950acd32 0a2ecc93 40c02b96 90c4dc04 daef7f6a fe5c");
        var mac = key[..16];
        var ctr = key[16..];
        Assert.Equal(expected, AesSiv.Encrypt(ctr, mac, plain, ad));
        Assert.Equal(plain, AesSiv.Decrypt(ctr, mac, expected, ad));
        // Verändert oder falsche zugehörige Daten: nichts
        var bad = (byte[])expected.Clone();
        bad[^1] ^= 1;
        Assert.Null(AesSiv.Decrypt(ctr, mac, bad, ad));
        Assert.Null(AesSiv.Decrypt(ctr, mac, expected, Hex("00")));
    }

    [Fact]
    public void AesSiv_RFC5297_A2_mehrere_zugehoerige_Daten()
    {
        var key = Hex("7f7e7d7c 7b7a7978 77767574 73727170 40414243 44454647 48494a4b 4c4d4e4f");
        var ad1 = Hex("00112233 44556677 8899aabb ccddeeff deaddada deaddada ffeeddcc bbaa9988 77665544 33221100");
        var ad2 = Hex("10203040 50607080 90a0");
        var nonce = Hex("09f91102 9d74e35b d84156c5 635688c0");
        var plain = Hex("74686973 20697320 736f6d65 20706c61 696e7465 78742074 6f20656e 63727970 74207573 696e6720 5349562d 414553");
        var expected = Hex("7bdb6e3b 432667eb 06f4d14b ff2fbd0f cb900f2f ddbe4043 26601965 c889bf17 dba77ceb 094fa663 b7a3f748 ba8af829 ea64ad54 4a272e9c 485b62a3 fd5c0d");
        Assert.Equal(expected, AesSiv.Encrypt(key[16..], key[..16], plain, ad1, ad2, nonce));
        Assert.Equal(plain, AesSiv.Decrypt(key[16..], key[..16], expected, ad1, ad2, nonce));
    }

    [Fact]
    public void AesSiv_leerer_Klartext_und_Blockgrenzen()
    {
        var ctr = RandomNumberGenerator.GetBytes(32);
        var mac = RandomNumberGenerator.GetBytes(32);
        foreach (var n in new[] { 0, 1, 15, 16, 17, 31, 32, 33, 100 })
        {
            var p = RandomNumberGenerator.GetBytes(n);
            var c = AesSiv.Encrypt(ctr, mac, p, [1, 2, 3]);
            Assert.Equal(16 + n, c.Length);
            Assert.Equal(p, AesSiv.Decrypt(ctr, mac, c, [1, 2, 3]));
        }
    }

    [Fact]
    public void KeyWrap_RFC3394_4_1()
    {
        var wrap = new Org.BouncyCastle.Crypto.Engines.Rfc3394WrapEngine(new Org.BouncyCastle.Crypto.Engines.AesEngine());
        wrap.Init(true, new Org.BouncyCastle.Crypto.Parameters.KeyParameter(Hex("000102030405060708090A0B0C0D0E0F")));
        var data = Hex("00112233445566778899AABBCCDDEEFF");
        Assert.Equal(Hex("1FA68B0A8112B447 AEF34BD8FB5A7B82 9D3E862371D2CFE5"), wrap.Wrap(data, 0, data.Length));
    }

    [Fact]
    public void Scrypt_RFC7914()
    {
        var r = Org.BouncyCastle.Crypto.Generators.SCrypt.Generate("password"u8.ToArray(), "NaCl"u8.ToArray(), 1024, 8, 16, 64);
        Assert.Equal(Hex("fdbabe1c9d3472007856e7190d01e9fe7c6ad7cbc8237830e77376634b3731622eaf30d92e22a3886ff109279d9830dac727afb94a83ee6d8360cbdfa2cc0640"), r);
    }

    [Fact]
    public void Base32_wie_Guava()
    {
        Assert.Equal("", VaultNames.Base32([]));
        Assert.Equal("MY======", VaultNames.Base32("f"u8));
        Assert.Equal("MZXW6YTBOI======", VaultNames.Base32("foobar"u8));
    }

    // ---------------- Bausteine ----------------

    [Fact]
    public void Masterkey_Datei_falsches_Passwort_und_veraenderte_Fassung()
    {
        using var key = Masterkey.Generate();
        var file = MasterkeyFile.Write(key, "geheim ä", cost: 1024);
        using var back = MasterkeyFile.Read(file, "geheim ä");
        Assert.NotNull(back);
        Assert.Equal(key.Raw(), back!.Raw());
        Assert.Null(MasterkeyFile.Read(file, "falsch"));
        // NFC: dasselbe Passwort in zerlegter Form öffnet ebenso
        Assert.NotNull(MasterkeyFile.Read(file, "geheim ä".Normalize(NormalizationForm.FormD)));
        var changed = Encoding.UTF8.GetString(file).Replace("\"version\": 999", "\"version\": 998");
        Assert.Throws<FormatException>(() => MasterkeyFile.Read(Encoding.UTF8.GetBytes(changed), "geheim ä"));
    }

    [Fact]
    public void VaultConfig_Signatur_und_Format()
    {
        using var key = Masterkey.Generate();
        var jwt = VaultConfig.Write(key);
        var cfg = VaultConfig.Read(jwt, key);
        Assert.Equal(8, cfg.Format);
        Assert.Equal(220, cfg.ShorteningThreshold);
        Assert.Equal("masterkeyfile:masterkey.cryptomator", VaultConfig.KidOf(jwt));
        using var other = Masterkey.Generate();
        Assert.Throws<FormatException>(() => VaultConfig.Read(jwt, other));
    }

    [Fact]
    public void Inhalt_Groessen_und_Veraenderung()
    {
        using var key = Masterkey.Generate();
        foreach (var n in new[] { 0, 1, FileContent.ChunkPlain - 1, FileContent.ChunkPlain, FileContent.ChunkPlain + 1, 3 * FileContent.ChunkPlain + 5 })
        {
            var plain = RandomNumberGenerator.GetBytes(n);
            var cipher = FileContent.Encrypt(key, plain);
            Assert.Equal(FileContent.CiphertextSize(n), cipher.Length);
            Assert.Equal(n, FileContent.CleartextSize(cipher.Length));
            Assert.Equal(plain, FileContent.Decrypt(key, cipher));
            using var s = new DecryptingStream(key, new MemoryStream(cipher));
            Assert.Equal(n, s.Length);
            if (n > 10)
            {
                s.Position = n - 10;
                var tail = new byte[10];
                s.ReadExactly(tail);
                Assert.Equal(plain[^10..], tail);
            }
            if (n > 0)
            {
                var bad = (byte[])cipher.Clone();
                bad[^1] ^= 1;
                Assert.Throws<CryptographicException>(() => FileContent.Decrypt(key, bad));
            }
        }
        // Blöcke vertauschen fällt auf (Blocknummer in den zugehörigen Daten)
        var two = FileContent.Encrypt(key, new byte[2 * FileContent.ChunkPlain]);
        var swapped = (byte[])two.Clone();
        two.AsSpan(FileContent.HeaderSize, FileContent.ChunkCipher).CopyTo(swapped.AsSpan(FileContent.HeaderSize + FileContent.ChunkCipher));
        two.AsSpan(FileContent.HeaderSize + FileContent.ChunkCipher, FileContent.ChunkCipher).CopyTo(swapped.AsSpan(FileContent.HeaderSize));
        Assert.Throws<CryptographicException>(() => FileContent.Decrypt(key, swapped));
        // Abgeschnitten um einen ganzen Block: der neue letzte Block ist kein letzter mehr - Länge passt, aber Inhalt fehlt.
        // Cryptomator erkennt das ebenso wenig; geprüft wird nur, dass ein halber Block auffällt.
        Assert.Throws<FormatException>(() => FileContent.CleartextSize(FileContent.HeaderSize + 10));
    }

    // ---------------- Mit Cryptomator angelegt ----------------

    /// <summary>Erwarteter Inhalt (Ausgabe von tools/cryptomator-check dump): Art, Pfad, Größe, SHA-256.</summary>
    private static List<string> Expected() =>
        File.ReadAllLines(Path.Combine(Fixtures, "reference.txt"), Encoding.UTF8).Where(l => l.Length > 0).Select(l => l.Normalize(NormalizationForm.FormC)).Order(StringComparer.Ordinal).ToList();

    private static List<string> Dump(CryptomatorVault v)
    {
        var lines = new List<string>();
        void Walk(string dir)
        {
            foreach (var e in v.List(dir))
            {
                var path = dir + "/" + e.Name;
                if (e.IsDir) { lines.Add($"D\t{path}\t0\t-"); Walk(path); }
                else
                {
                    using var s = v.OpenRead(path);
                    lines.Add($"F\t{path}\t{e.Size}\t{Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant()}");
                }
            }
        }
        Walk("");
        return lines.Select(l => l.Normalize(NormalizationForm.FormC)).Order(StringComparer.Ordinal).ToList();
    }

    [Fact]
    public void Liest_einen_mit_Cryptomator_angelegten_Tresor()
    {
        var copy = Path.Combine(Path.GetTempPath(), "cm-ref-" + Guid.NewGuid().ToString("N"));
        CopyDir(Path.Combine(Fixtures, "reference"), copy);
        try
        {
            Assert.Null(LocalVault.Open(copy, "falsch"));
            using var v = LocalVault.Open(copy, ReferencePassword)!;
            Assert.Equal(Expected(), Dump(v));
        }
        finally { Directory.Delete(copy, recursive: true); }
    }

    private static void CopyDir(string from, string to)
    {
        foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var t = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(t)!);
            File.Copy(f, t);
        }
        foreach (var d in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, d)));
    }

    // ---------------- Von dmOffice geschrieben ----------------

    /// <summary>Denselben Inhalt wie im Referenz-Tresor schreiben (aus der Liste: Ordner, Dateien mit gleicher Größe und Prüfsumme
    /// lassen sich nicht nachbauen - deshalb Inhalt aus dem Referenz-Tresor kopieren).</summary>
    private static async Task<string> WriteLikeReference(string target, string password)
    {
        var copy = Path.Combine(Path.GetTempPath(), "cm-src-" + Guid.NewGuid().ToString("N"));
        CopyDir(Path.Combine(Fixtures, "reference"), copy);
        try
        {
            using var src = LocalVault.Open(copy, ReferencePassword)!;
            using var dst = LocalVault.Create(target, Masterkey.Generate(), password, scryptCost: 1024);
            async Task Copy(string dir)
            {
                foreach (var e in src.List(dir))
                {
                    var path = dir + "/" + e.Name;
                    if (e.IsDir) { dst.CreateDirectory(path); await Copy(path); }
                    else { await using var s = src.OpenRead(path); await dst.WriteAsync(path, s); }
                }
            }
            await Copy("");
        }
        finally { Directory.Delete(copy, recursive: true); }
        return target;
    }

    [Fact]
    public async Task Schreiben_und_wieder_lesen_wie_Cryptomator()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cm-w-" + Guid.NewGuid().ToString("N"));
        try
        {
            await WriteLikeReference(dir, "anderes Passwort");
            using var v = LocalVault.Open(dir, "anderes Passwort")!;
            Assert.Equal(Expected(), Dump(v));
            // Aufbau wie bei Cryptomator: jeder Ordner hat dirid.c9r, lange Namen liegen als .c9s mit name.c9s
            Assert.True(Directory.EnumerateDirectories(dir, "*.c9s", SearchOption.AllDirectories).Any());
            Assert.All(Directory.EnumerateDirectories(dir, "*.c9s", SearchOption.AllDirectories), d => Assert.True(File.Exists(Path.Combine(d, VaultNames.NameFile))));
            var dataDirs = Directory.EnumerateDirectories(Path.Combine(dir, "d")).SelectMany(Directory.EnumerateDirectories).ToList();
            Assert.All(dataDirs, d => Assert.True(File.Exists(Path.Combine(d, VaultNames.DirIdBackup)), d));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// Gegenprobe mit der echten Cryptomator-Bibliothek: dmOffice schreibt, Cryptomator liest (tools/cryptomator-check). Braucht
    /// die Cryptomator-App und ein JDK; läuft nur mit DMOFFICE_CRYPTOMATOR_CHECK=1 (sonst bestanden ohne Prüfung).
    /// </summary>
    [Fact]
    public async Task Gegenprobe_Cryptomator_liest_von_dmOffice_geschriebenen_Tresor()
    {
        if (Environment.GetEnvironmentVariable("DMOFFICE_CRYPTOMATOR_CHECK") != "1") return;
        var dir = Path.Combine(Path.GetTempPath(), "cm-g-" + Guid.NewGuid().ToString("N"));
        try
        {
            await WriteLikeReference(dir, ReferencePassword);
            // Dazu eine große Datei über viele Blöcke (6 MB und ein Rest)
            var big = RandomNumberGenerator.GetBytes(6 * 1024 * 1024 + 4321);
            using (var v = LocalVault.Open(dir, ReferencePassword)!) await v.WriteAsync("/Ebene 1/Große Datei ä.bin", new MemoryStream(big));
            var expected = Expected().Append($"F\t/Ebene 1/Große Datei ä.bin\t{big.Length}\t{Convert.ToHexString(SHA256.HashData(big)).ToLowerInvariant()}")
                .Order(StringComparer.Ordinal).ToList();
            Assert.Equal(expected, await CryptomatorDump(dir));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    /// <summary>Gegenrichtung mit großer Datei: Cryptomator legt an, dmOffice liest (nur mit DMOFFICE_CRYPTOMATOR_CHECK=1).</summary>
    [Fact]
    public async Task Gegenprobe_dmOffice_liest_grossen_von_Cryptomator_angelegten_Tresor()
    {
        if (Environment.GetEnvironmentVariable("DMOFFICE_CRYPTOMATOR_CHECK") != "1") return;
        var src = Path.Combine(Path.GetTempPath(), "cm-s-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(Path.GetTempPath(), "cm-c-" + Guid.NewGuid().ToString("N"));
        try
        {
            var big = RandomNumberGenerator.GetBytes(6 * 1024 * 1024 + 4321);
            Directory.CreateDirectory(Path.Combine(src, "Ordner ü", "tiefer"));
            File.WriteAllBytes(Path.Combine(src, "Ordner ü", "tiefer", "Große Datei.bin"), big);
            var longName = "Lang " + string.Concat(Enumerable.Repeat("ÄÖÜ ", 60)) + ".txt";
            File.WriteAllText(Path.Combine(src, "Ordner ü", longName), "lang");
            Assert.Equal(0, (await RunCheck("create", dir, ReferencePassword, src)).ExitCode);
            using var v = LocalVault.Open(dir, ReferencePassword)!;
            await using (var s = v.OpenRead("/Ordner ü/tiefer/Große Datei.bin")) Assert.Equal(SHA256.HashData(big), SHA256.HashData(s));
            await using (var s = v.OpenRead("/Ordner ü/" + longName)) Assert.Equal("lang", new StreamReader(s).ReadToEnd());
            Assert.Equal(await CryptomatorDump(dir), Dump(v));
        }
        finally
        {
            foreach (var d in new[] { src, dir }) if (Directory.Exists(d)) Directory.Delete(d, recursive: true);
        }
    }

    private static async Task<(int ExitCode, string Output)> RunCheck(params string[] args)
    {
        var script = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "tools", "cryptomator-check", "check.ps1"));
        var psi = new ProcessStartInfo("powershell", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, .. args])
        { RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8, UseShellExecute = false };
        using var p = Process.Start(psi)!;
        var output = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, output);
    }

    private static async Task<List<string>> CryptomatorDump(string dir)
    {
        var (code, output) = await RunCheck("dump", dir, ReferencePassword);
        Assert.Equal(0, code);
        return output.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.StartsWith("D\t") || l.StartsWith("F\t"))
            .Select(l => l.Normalize(NormalizationForm.FormC)).Order(StringComparer.Ordinal).ToList();
    }
}
