using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DmOffice.Core;

namespace DmOffice.Tests;

/// <summary>Push an die Handy-App: Tokens je App-Passwort, Fingerabdruck, JWT fuer Google.</summary>
public class PushTests
{
    private static UserDb NewDb()
    {
        var dir = Directory.CreateTempSubdirectory("dmpush");
        return new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "t");
    }

    [Fact]
    public void Token_faellt_mit_dem_App_Passwort()
    {
        var db = NewDb();
        AppPasswords.Create(db, "Handy");
        var pw = AppPasswords.List(db).Single();
        Push.Register(db, pw.Id, "tok-1");
        Push.Register(db, pw.Id, "tok-1");   // doppelt: bleibt eins
        Assert.Single(Push.Tokens(db));
        AppPasswords.Delete(db, pw.Id);
        Assert.Empty(Push.Tokens(db));
    }

    [Fact]
    public void Leeres_Token_wird_abgewiesen()
    {
        var db = NewDb();
        AppPasswords.Create(db, "Handy");
        Assert.Throws<ArgumentException>(() => Push.Register(db, AppPasswords.List(db).Single().Id, " "));
    }

    [Fact]
    public void Fingerabdruck_aendert_sich_mit_Kalender_und_Mail()
    {
        var db = NewDb();
        DmOffice.Pim.PimStore.EnsureDefaults(db);
        var a = Push.Fingerprint(db, 0);
        Assert.Equal(a, Push.Fingerprint(db, 0));
        Assert.NotEqual(a, Push.Fingerprint(db, 1));
        var cal = DmOffice.Pim.PimStore.Collections(db, DmOffice.Pim.PimStore.Calendar).First();
        DmOffice.Pim.PimStore.Put(db, cal, "e.ics", "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VEVENT\r\nUID:e1\r\nDTSTART:20270101T100000Z\r\nDTEND:20270101T110000Z\r\nSUMMARY:x\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");
        Assert.NotEqual(a, Push.Fingerprint(db, 0));
    }

    [Fact]
    public void Jwt_ist_mit_dem_Dienstkonto_signiert()
    {
        using var rsa = RSA.Create(2048);
        var pem = rsa.ExportPkcs8PrivateKeyPem();
        var now = new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
        var jwt = Fcm.BuildJwt("dmoffice@projekt.iam.gserviceaccount.com", pem, "https://oauth2.googleapis.com/token", now);
        var parts = jwt.Split('.');
        Assert.Equal(3, parts.Length);
        static byte[] Dec(string s) { s = s.Replace('-', '+').Replace('_', '/'); return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=')); }
        Assert.True(rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Dec(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        var claims = JsonDocument.Parse(Dec(parts[1])).RootElement;
        Assert.Equal("dmoffice@projekt.iam.gserviceaccount.com", claims.GetProperty("iss").GetString());
        Assert.Equal("https://www.googleapis.com/auth/firebase.messaging", claims.GetProperty("scope").GetString());
        Assert.Equal(claims.GetProperty("iat").GetInt64() + 3600, claims.GetProperty("exp").GetInt64());
    }

    [Fact]
    public void Push_Register_ist_fuer_das_App_Passwort_frei() => Assert.True(Auth.AppPasswordAllowed("/api/push/register"));
}
