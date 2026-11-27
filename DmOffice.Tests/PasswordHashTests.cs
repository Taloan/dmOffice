using System.Security.Cryptography;
using DmOffice.Core;

namespace DmOffice.Tests;

/// <summary>Passwort-Hashes: neue mit den heutigen Runden, alte gelten weiter und werden beim Anmelden nachgezogen;
/// Wiederherstellungscodes lang genug für einen einfachen Hash; Hinweis auf einen schwachen Hauptschlüssel.</summary>
public class PasswordHashTests
{
    /// <summary>Hash, wie ihn frühere Versionen mit 210 000 Runden geschrieben haben.</summary>
    private static string OldHash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 210_000, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2$210000${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    [Fact]
    public void Neue_Hashes_mit_600000_Runden_alte_gelten_weiter()
    {
        var fresh = Passwords.Hash("geheim-123456");
        Assert.StartsWith("pbkdf2$600000$", fresh);
        Assert.False(Passwords.Outdated(fresh));
        var old = OldHash("geheim-123456");
        Assert.True(Passwords.Outdated(old));
        Assert.True(Passwords.Verify("geheim-123456", old));
        Assert.False(Passwords.Verify("falsch", old));
        Assert.False(Passwords.Outdated(null));
    }

    [Fact]
    public async Task Alter_Hash_wird_beim_Anmelden_nachgezogen()
    {
        var dir = Directory.CreateTempSubdirectory("dmpw").FullName;
        var paths = new DataPaths(dir);
        var users = new UserStore(paths, new Vault(paths, "test-schluessel-" + Guid.NewGuid()));
        var u = users.Create("anna", "Anna", "geheim-123456", false);
        var stamp = u.Stamp;
        users.Update(u.Id, x => x.PasswordHash = OldHash("geheim-123456"));
        u = users.ById(u.Id)!;
        Assert.True(await Passwords.VerifyLimitedAsync("geheim-123456", u.PasswordHash));
        users.UpgradeHash(u, "geheim-123456");
        var after = users.ById(u.Id)!;
        Assert.StartsWith("pbkdf2$600000$", after.PasswordHash);
        Assert.True(Passwords.Verify("geheim-123456", after.PasswordHash));
        // Andere Sitzungen bleiben angemeldet
        Assert.Equal(stamp, after.Stamp);
        // Hat sich der Hash inzwischen geändert (neues Passwort), bleibt der neue
        users.Update(u.Id, x => x.PasswordHash = Passwords.Hash("anderes-123456"));
        users.UpgradeHash(u, "geheim-123456");
        Assert.True(Passwords.Verify("anderes-123456", users.ById(u.Id)!.PasswordHash));
    }

    [Fact]
    public void Wiederherstellungscodes_lang_und_alte_gelten_weiter()
    {
        var codes = Totp.NewRecoveryCodes();
        Assert.Equal(10, codes.Count);
        Assert.All(codes, c => Assert.Matches("^[a-z2-9]{5}-[a-z2-9]{5}-[a-z2-9]{5}$", c));
        Assert.Equal(10, codes.Distinct().Count());
        // Eingabe mit Großbuchstaben oder ohne Bindestriche zählt gleich
        Assert.Equal(Passwords.TokenHash(codes[0]), Passwords.TokenHash(codes[0].ToUpperInvariant().Replace("-", "")));
        // Frühere Codes (zwei Gruppen) werden unverändert geprüft
        Assert.Equal(Passwords.TokenHash("abcde-fghjk"), Passwords.TokenHash("ABCDEFGHJK"));
    }

    [Fact]
    public void Kurzer_Hauptschluessel_faellt_auf()
    {
        Assert.False(Vault.LooksWeak(null));
        Assert.False(Vault.LooksWeak(""));
        Assert.False(Vault.LooksWeak(Convert.ToHexString(RandomNumberGenerator.GetBytes(32))));
        Assert.True(Vault.LooksWeak("meinpasswort"));
        Assert.True(Vault.LooksWeak(new string('a', 64)));
    }
}
