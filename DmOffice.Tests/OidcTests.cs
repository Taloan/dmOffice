using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DmOffice.Core;

namespace DmOffice.Tests;

/// <summary>Anmeldung über einen Identitätsanbieter: Prüfung des ID-Tokens (Signatur, Aussteller, Empfänger, Ablauf, nonce) und neue Anmeldenamen.</summary>
public class OidcTests
{
    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string B64(object o) => B64(JsonSerializer.SerializeToUtf8Bytes(o));

    private static string Jwt(string alg, string kid, object payload, Func<byte[], byte[]> sign)
    {
        var head = B64(new { alg, kid, typ = "JWT" });
        var body = B64(payload);
        return head + "." + body + "." + B64(sign(Encoding.ASCII.GetBytes(head + "." + body)));
    }

    private static object Claims(string nonce = "n1", string aud = "dmoffice", long? exp = null, string iss = "https://sso.example.org/realms/dm") => new
    {
        iss, aud, sub = "abc-123", nonce, exp = exp ?? DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds(),
        preferred_username = "anna", email = "anna@example.org", name = "Anna Beispiel", groups = new[] { "/dmoffice", "andere" },
    };

    [Fact]
    public void RS256_und_ES256_werden_geprueft_falscher_Schluessel_und_none_abgelehnt()
    {
        using var rsa = RSA.Create(2048);
        var p = rsa.ExportParameters(false);
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var q = ec.ExportParameters(false);
        var keys = JsonDocument.Parse(JsonSerializer.Serialize(new { keys = new object[]
        {
            new { kty = "RSA", kid = "r1", use = "sig", n = B64(p.Modulus!), e = B64(p.Exponent!) },
            new { kty = "EC", kid = "e1", crv = "P-256", x = B64(q.Q.X!), y = B64(q.Q.Y!) },
        } })).RootElement.GetProperty("keys");

        var rs = Jwt("RS256", "r1", Claims(), d => rsa.SignData(d, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        Assert.True(Oidc.VerifySignature(rs, keys));
        var es = Jwt("ES256", "e1", Claims(), d => ec.SignData(d, HashAlgorithmName.SHA256));
        Assert.True(Oidc.VerifySignature(es, keys));

        // Anderer Schlüssel, verändertes Token, „none“ und HMAC
        using var other = RSA.Create(2048);
        Assert.False(Oidc.VerifySignature(Jwt("RS256", "r1", Claims(), d => other.SignData(d, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)), keys));
        var parts = rs.Split('.');
        Assert.False(Oidc.VerifySignature(parts[0] + "." + B64(Claims(nonce: "anders")) + "." + parts[2], keys));
        Assert.False(Oidc.VerifySignature(B64(new { alg = "none" }) + "." + parts[1] + ".", keys));
        Assert.False(Oidc.VerifySignature(Jwt("HS256", "r1", Claims(), d => HMACSHA256.HashData(Encoding.ASCII.GetBytes("x"), d)), keys));
    }

    [Fact]
    public void Aussteller_Empfaenger_Ablauf_und_nonce_muessen_passen()
    {
        static string Unsigned(object c) => "e30." + B64(c) + ".x";
        var now = DateTimeOffset.UtcNow;
        const string iss = "https://sso.example.org/realms/dm";
        var ok = Oidc.CheckClaims(Unsigned(Claims()), iss + "/", "dmoffice", "n1", now);
        Assert.NotNull(ok);
        Assert.Equal(["dmoffice", "andere"], Oidc.Groups(ok!.Value));
        Assert.Null(Oidc.CheckClaims(Unsigned(Claims(iss: "https://evil.example")), iss, "dmoffice", "n1", now));
        Assert.Null(Oidc.CheckClaims(Unsigned(Claims(aud: "anderer")), iss, "dmoffice", "n1", now));
        Assert.Null(Oidc.CheckClaims(Unsigned(Claims(exp: now.AddMinutes(-5).ToUnixTimeSeconds())), iss, "dmoffice", "n1", now));
        Assert.Null(Oidc.CheckClaims(Unsigned(Claims(nonce: "n2")), iss, "dmoffice", "n1", now));
        // Empfänger als Liste
        Assert.NotNull(Oidc.CheckClaims(Unsigned(new { iss, aud = new[] { "account", "dmoffice" }, sub = "s", nonce = "n1", exp = now.AddMinutes(1).ToUnixTimeSeconds() }), iss, "dmoffice", "n1", now));
    }

    [Fact]
    public void Neuer_Anmeldename_gesaeubert_und_frei()
    {
        var dir = Directory.CreateTempSubdirectory("dmoidc");
        var paths = new DataPaths(dir.FullName);
        var users = new UserStore(paths, new Vault(paths, null));
        users.Create("anna", "Anna", "geheim-genug", false);
        Assert.Equal("anna2", Oidc.NewLogin(users, "Anna", null));
        Assert.Equal("max.mustermann", Oidc.NewLogin(users, null, "Max.Mustermann@example.org"));
        Assert.Equal("j-rgen-m-ller", Oidc.NewLogin(users, "Jürgen Müller", null));
        Assert.Equal("benutzer", Oidc.NewLogin(users, "!", null));
    }

    [Fact]
    public void Vorgemerktes_Konto_nur_ueber_bestaetigte_Mail_oder_Einladung()
    {
        var max = new UserRecord { Id = "m", Login = "max", OidcExpected = "max@example.org" };
        var name = new UserRecord { Id = "n", Login = "anna", OidcExpected = "Anna.B" };
        var chef = new UserRecord { Id = "c", Login = "chef", OidcExpected = "chef@example.org", IsAdmin = true };
        var fertig = new UserRecord { Id = "f", Login = "fertig", OidcExpected = "fertig@example.org", OidcSubject = "x|1" };
        var all = new[] { max, name, chef, fertig };
        Assert.Same(max, Oidc.Reserved(all, "MAX@example.org", true));
        // Unbestätigte E-Mail zählt nicht, ein Anmeldename nie (auch nicht, wenn er wie die Adresse aussieht)
        Assert.Null(Oidc.Reserved(all, "max@example.org", false));
        Assert.Null(Oidc.Reserved(all, "Anna.B", true));
        // Keine Administratoren, keine schon verknüpften Konten
        Assert.Null(Oidc.Reserved(all, "chef@example.org", true));
        Assert.Null(Oidc.Reserved(all, "fertig@example.org", true));
        Assert.Null(Oidc.Reserved(all, null, true));

        // Einladung: einmaliger Code mit Ablauf, nicht für Administratoren
        var now = DateTimeOffset.UtcNow;
        var gast = new UserRecord { Id = "g", Login = "gast", OidcExpected = Oidc.InviteValue("geheim-123", now.AddDays(7)) };
        var alt = new UserRecord { Id = "a", Login = "alt", OidcExpected = Oidc.InviteValue("alt-456", now.AddMinutes(-1)) };
        var admin = new UserRecord { Id = "x", Login = "admin", IsAdmin = true, OidcExpected = Oidc.InviteValue("adm-789", now.AddDays(7)) };
        var invites = new[] { gast, alt, admin };
        Assert.Same(gast, Oidc.Invited(invites, "geheim-123", now));
        Assert.Null(Oidc.Invited(invites, "geheim-124", now));
        Assert.Null(Oidc.Invited(invites, "alt-456", now));
        Assert.Null(Oidc.Invited(invites, "adm-789", now));
        // Eine Einladung zählt nie als vorgemerkte E-Mail
        Assert.Null(Oidc.Reserved(invites, gast.OidcExpected, true));
    }

    [Theory]
    [InlineData("https://sso.example.org/realms/x", true)]
    [InlineData("http://sso.example.org", false)]
    [InlineData("http://192.168.1.5:8080", true)]
    [InlineData("http://localhost:9000", true)]
    [InlineData("http://pocket-id", true)]
    [InlineData("http://idp.local", true)]
    [InlineData("http://8.8.8.8", false)]
    [InlineData("https://sso.example.org/x#", false)]
    [InlineData("https://sso.example.org/x?a=1", false)]
    [InlineData("https://user:pw@sso.example.org", false)]
    [InlineData("file:///etc/passwd", false)]
    public void Aussteller_nur_https_oder_eigenes_Netz(string url, bool ok) => Assert.Equal(ok, Oidc.BadUrl(url, issuer: true) == null);
}
