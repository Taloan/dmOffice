using System.Security.Cryptography;
using System.Text;

namespace DmOffice.Tests;

/// <summary>Verschluesselter Umschlag App <-> dmOffice: Schluesselableitung, Umschlag, Zaehlerfenster.</summary>
public class E2eTests
{
    [Fact]
    public void Beide_Seiten_leiten_denselben_Schluessel_ab()
    {
        using var server = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var device = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var serverPub = E2e.ExportPublic(server);
        var devicePub = E2e.ExportPublic(device);
        Assert.Equal(65, E2e.FromBase64Url(serverPub).Length);

        using var serverSeesDevice = E2e.ImportPublic(devicePub);
        using var deviceSeesServer = E2e.ImportPublic(serverPub);
        var k1 = E2e.DeriveKey(server, serverSeesDevice, devicePub, serverPub);
        var k2 = E2e.DeriveKey(device, deviceSeesServer, devicePub, serverPub);
        Assert.Equal(k1, k2);
        Assert.Equal(32, k1.Length);
    }

    [Fact]
    public void Umschlag_rundreise_und_manipulation()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var plain = Encoding.UTF8.GetBytes("{\"method\":\"GET\",\"path\":\"/api/mail/status\"}");
        var sealed_ = E2e.Seal(key, plain, E2e.Aad("dev1", 7, false));
        Assert.Equal(plain, E2e.Open(key, sealed_, E2e.Aad("dev1", 7, false)));
        // Anderer Zaehler, anderes Geraet, Antwort statt Anfrage: alles abgelehnt
        Assert.ThrowsAny<CryptographicException>(() => E2e.Open(key, sealed_, E2e.Aad("dev1", 8, false)));
        Assert.ThrowsAny<CryptographicException>(() => E2e.Open(key, sealed_, E2e.Aad("dev2", 7, false)));
        Assert.ThrowsAny<CryptographicException>(() => E2e.Open(key, sealed_, E2e.Aad("dev1", 7, true)));
        // Ein gekipptes Bit
        var broken = (byte[])sealed_.Clone(); broken[20] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => E2e.Open(key, broken, E2e.Aad("dev1", 7, false)));
        // Anderer Schluessel
        Assert.ThrowsAny<CryptographicException>(() => E2e.Open(RandomNumberGenerator.GetBytes(32), sealed_, E2e.Aad("dev1", 7, false)));
    }

    [Fact]
    public void Zaehlerfenster_laesst_verspaetete_durch_aber_keine_wiederholung()
    {
        var w = new E2e.CounterWindow(0);
        Assert.True(w.Accept(1));
        Assert.True(w.Accept(3));
        Assert.True(w.Accept(2));      // verspaetet, noch im Fenster
        Assert.False(w.Accept(2));     // Wiederholung
        Assert.False(w.Accept(3));
        Assert.False(w.Accept(0));
        Assert.True(w.Accept(1000));
        Assert.False(w.Accept(4));     // zu alt (Fenster 256)
        Assert.True(w.Accept(900));
        Assert.Equal(1000, w.Highest);
    }

    [Fact]
    public void Nach_Neustart_keine_Wiedereinspielung_der_letzten_Umschlaege()
    {
        // Frisch aus der Datenbank geladen (hoechster bisher 500): das _seen-Set ist leer.
        var w = new E2e.CounterWindow(500);
        Assert.False(w.Accept(500));   // schon benutzt
        Assert.False(w.Accept(400));   // im Fenster, aber vor dem geladenen Stand - kein Wiedereinspielen
        Assert.False(w.Accept(300));
        Assert.True(w.Accept(501));    // neu
        Assert.False(w.Accept(501));   // und nicht zweimal
    }

    [Fact]
    public void Fingerabdruck_wie_in_der_App()
    {
        // Fester Punkt: SHA-256 ueber 0x04 || 64 Nullbytes, die ersten 16 Byte in Vierergruppen (E2e.kt rechnet gleich)
        var raw = new byte[65]; raw[0] = 4;
        var expected = Convert.ToHexString(SHA256.HashData(raw), 0, 16);
        var fp = E2e.Fingerprint(E2e.Base64Url(raw));
        Assert.Equal(39, fp.Length);
        Assert.Equal(expected, fp.Replace(" ", ""));
        Assert.Matches("^([0-9A-F]{4} ){7}[0-9A-F]{4}$", fp);
    }

    [Fact]
    public void Ungueltiger_Schluessel_wird_abgewiesen()
    {
        Assert.Throws<ArgumentException>(() => E2e.ImportPublic(E2e.Base64Url(new byte[10])));
        Assert.Throws<ArgumentException>(() => E2e.ImportPublic(E2e.Base64Url(new byte[65])));
    }

    [Fact]
    public void Browser_Geraet_und_App_Geraet()
    {
        var app = new E2e.Device("a", "pw1", null, "k", 0, DateTime.UtcNow);
        var browser = new E2e.Device("b", null, "stamp", "k", 0, DateTime.UtcNow);
        Assert.False(app.Browser);
        Assert.True(browser.Browser);
    }

    [Fact]
    public void Umschlag_Endpunkte_sind_fuer_das_App_Passwort_frei()
    {
        Assert.True(Auth.AppPasswordAllowed("/api/e2e/register"));
        Assert.False(Auth.AppPasswordAllowed("/api/e2e/abc/def"));   // der Umschlag selbst ist anonym, nicht per Basic
    }
}
