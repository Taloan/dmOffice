using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DmOffice.Core;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>Web Push: Verschlüsselung (RFC 8291) wie der Browser sie auspackt, VAPID (RFC 8292), nur bekannte Push-Dienste.</summary>
[Collection("server")]
public class WebPushTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public WebPushTests(ChatFixture fx) => _fx = fx;

    private static byte[] Point(ECDiffieHellman k) { var p = k.ExportParameters(false); return [4, .. p.Q.X!, .. p.Q.Y!]; }

    /// <summary>Auspacken wie der Browser (Empfängerseite von RFC 8291).</summary>
    private static byte[] Decrypt(byte[] body, ECDiffieHellman ua, byte[] auth)
    {
        var salt = body[..16];
        var idlen = body[20];
        var asPublic = body[21..(21 + idlen)];
        var cipher = body[(21 + idlen)..^16];
        var tag = body[^16..];
        using var server = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = asPublic[1..33], Y = asPublic[33..65] } });
        var ecdh = ua.DeriveRawSecretAgreement(server.PublicKey);
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, ecdh, 32, auth, [.. Encoding.ASCII.GetBytes("WebPush: info\0"), .. Point(ua), .. asPublic]);
        var cek = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 16, salt, Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"));
        var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 12, salt, Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"));
        var plain = new byte[cipher.Length];
        using (var gcm = new AesGcm(cek, 16)) gcm.Decrypt(nonce, cipher, tag, plain);
        Assert.Equal(2, plain[^1]);   // Kennzeichen „letzter Datensatz“
        return plain[..^1];
    }

    [Fact]
    public void Verschluesselt_so_dass_der_Browser_es_lesen_kann()
    {
        using var ua = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var auth = RandomNumberGenerator.GetBytes(16);
        var text = Encoding.UTF8.GetBytes("{\"title\":\"Anna\",\"body\":\"Grüße 👋\"}");
        var body = WebPush.Encrypt(text, Point(ua), auth);
        Assert.Equal(4096u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(16, 4)));
        Assert.Equal(text, Decrypt(body, ua, auth));
    }

    [Theory]
    [InlineData("https://fcm.googleapis.com/fcm/send/abc", true)]
    [InlineData("https://updates.push.services.mozilla.com/wpush/v2/abc", true)]
    [InlineData("https://wns2-db5p.notify.windows.com/w/?token=abc", true)]
    [InlineData("https://web.push.apple.com/abc", true)]
    [InlineData("http://fcm.googleapis.com/fcm/send/abc", false)]
    [InlineData("https://fcm.googleapis.com.example.org/x", false)]
    [InlineData("https://fcm.googleapis.com:8443/x", false)]
    [InlineData("https://192.168.178.1/x", false)]
    public void Nur_bekannte_Push_Dienste(string endpoint, bool ok) => Assert.Equal(ok, WebPush.Allowed(endpoint));

    [Fact]
    public async Task Abo_anlegen_VAPID_und_wieder_weg()
    {
        var push = _fx.Factory.Services.GetRequiredService<WebPush>();
        // Nur fuer die Anmeldung im Browser - App-Passwoerter kommen nicht heran
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await _fx.As("anna").GetAsync("/api/push/web/key")).StatusCode);
        var key = push.PublicKey;
        Assert.Equal(65, WebPush.Unb64(key).Length);

        using var ua = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var endpoint = "https://fcm.googleapis.com/fcm/send/test-" + Guid.NewGuid().ToString("N");
        var keys = new { p256dh = WebPush.B64(Point(ua)), auth = WebPush.B64(RandomNumberGenerator.GetBytes(16)) };
        push.Subscribe(_fx.Id("anna"), endpoint, keys.p256dh, keys.auth, "Test");
        Assert.Throws<ArgumentException>(() => push.Subscribe(_fx.Id("anna"), "https://example.org/push", keys.p256dh, keys.auth, null));
        Assert.Throws<ArgumentException>(() => push.Subscribe(_fx.Id("anna"), endpoint + "x", "kurz", keys.auth, null));
        var sub = Assert.Single(push.Of(_fx.Id("anna")));
        Assert.Contains(_fx.Id("anna"), push.Users());

        // VAPID: JWT für den Push-Dienst, mit unserem Schlüssel unterschrieben
        using var req = push.Request(sub, "{}"u8.ToArray(), urgent: false);
        var auth = req.Headers.GetValues("Authorization").Single();
        Assert.StartsWith("vapid t=", auth);
        Assert.EndsWith(", k=" + key, auth);
        var jwt = auth["vapid t=".Length..auth.IndexOf(',')].Split('.');
        var claims = JsonDocument.Parse(WebPush.Unb64(jwt[1])).RootElement;
        Assert.Equal("https://fcm.googleapis.com", claims.GetProperty("aud").GetString());
        var pub = WebPush.Unb64(key);
        using var verify = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = pub[1..33], Y = pub[33..65] } });
        Assert.True(verify.VerifyData(Encoding.ASCII.GetBytes(jwt[0] + "." + jwt[1]), WebPush.Unb64(jwt[2]), HashAlgorithmName.SHA256));
        Assert.Equal("aes128gcm", req.Content!.Headers.ContentEncoding.Single());

        // Fremde Abos kann niemand abbestellen, eigene schon
        push.Unsubscribe(_fx.Id("ben"), endpoint);
        Assert.Single(push.Of(_fx.Id("anna")));
        push.Unsubscribe(_fx.Id("anna"), endpoint);
        Assert.Empty(push.Of(_fx.Id("anna")));
    }
}
