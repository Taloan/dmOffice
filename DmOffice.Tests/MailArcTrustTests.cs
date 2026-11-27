using DmOffice.Mail;
using MimeKit;
using MimeKit.Cryptography;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;
using Xunit;

namespace DmOffice.Tests;

/// <summary>
/// Weitergeleitete Mail: Der eigene Mailserver meldet DMARC fehlgeschlagen und prüft ARC nicht. Ist die ARC-Kette gültig und
/// von einem großen Anbieter versiegelt, gilt dessen Prüfvermerk - sonst bleibt es beim Fehlschlag.
/// </summary>
[Collection("ArcTrust")]
public class MailArcTrustTests : IDisposable
{
    private static readonly IDnsResolver Real = ArcTrust.Dns;
    private readonly Dictionary<string, string> keys = new();
    private readonly RsaPrivateCrtKeyParameters priv;

    public MailArcTrustTests()
    {
        var gen = new RsaKeyPairGenerator();
        gen.Init(new Org.BouncyCastle.Crypto.KeyGenerationParameters(new SecureRandom(), 2048));
        var pair = gen.GenerateKeyPair();
        priv = (RsaPrivateCrtKeyParameters)pair.Private;
        var pub = Convert.ToBase64String(SubjectPublicKeyInfoFactory.CreateSubjectPublicKeyInfo(pair.Public).GetDerEncoded());
        foreach (var d in new[] { "google.com", "evil.example" }) keys[$"arc._domainkey.{d}"] = "v=DKIM1; k=rsa; p=" + pub;
        ArcTrust.Dns = new Fake(keys);
    }

    public void Dispose() => ArcTrust.Dns = Real;

    private sealed class Fake(Dictionary<string, string> keys) : IDnsResolver
    {
        public DnsTxtResponse QueryTxt(string name, CancellationToken ct = default) => new(keys.TryGetValue(name, out var v) ? [v] : []);
        public Task<DnsTxtResponse> QueryTxtAsync(string name, CancellationToken ct = default) => Task.FromResult(QueryTxt(name, ct));
    }

    private sealed class Signer(RsaPrivateCrtKeyParameters key, string domain, string dmarc) : ArcSigner(key, domain, "arc", DkimSignatureAlgorithm.RsaSha256)
    {
        protected override AuthenticationResults GenerateArcAuthenticationResults(FormatOptions options, MimeMessage message, CancellationToken ct)
        {
            AuthenticationResults.TryParse(System.Text.Encoding.ASCII.GetBytes($"mx.{domain}; dmarc={dmarc} header.from=shop.example"), out var r);
            return r!;
        }

        protected override Task<AuthenticationResults> GenerateArcAuthenticationResultsAsync(FormatOptions options, MimeMessage message, CancellationToken ct) =>
            Task.FromResult(GenerateArcAuthenticationResults(options, message, ct));
    }

    private MimeMessage Forwarded(string sealer, string dmarc = "pass", bool changedAfter = false)
    {
        var m = new MimeMessage();
        m.From.Add(new MailboxAddress("Shop", "noreply@shop.example"));
        m.To.Add(new MailboxAddress("", "me@own.test"));
        m.Subject = "Bestellung";
        m.Date = DateTimeOffset.Now;
        m.MessageId = "x1@shop.example";
        m.Body = new TextPart("plain") { Text = "Ihre Rechnung liegt bei.\r\n" };
        new Signer(priv, sealer, dmarc).Sign(m, [HeaderId.From, HeaderId.Subject, HeaderId.Date, HeaderId.MessageId]);
        if (changedAfter) m.Body = new TextPart("plain") { Text = "Bitte hier anmelden: https://shop-login.example\r\n" };
        // Der eigene Mailserver: DKIM kaputt (Weiterleitung), DMARC gescheitert, ARC nicht geprüft
        m.Headers.Insert(0, new Header("Authentication-Results", "mail.own.test; dkim=neutral header.d=shop.example; dmarc=fail header.from=shop.example"));
        return m;
    }

    [Fact]
    public void Gueltige_Kette_eines_grossen_Anbieters_gilt()
    {
        var a = PhishCheck.Auth(Forwarded("google.com"), ["mail.own.test"]);
        Assert.Equal("pass", a.Dmarc);
        Assert.Equal("pass", a.Arc);
        Assert.DoesNotContain(PhishCheck.Analyze(Forwarded("google.com"), new PhishCheck.Context { OwnHosts = ["mail.own.test"] }), f => f.Kind == "auth");
    }

    [Fact]
    public void Kette_einer_beliebigen_Domain_zaehlt_nicht()
    {
        Assert.Equal("fail", PhishCheck.Auth(Forwarded("evil.example"), ["mail.own.test"]).Dmarc);
    }

    [Fact]
    public void Nach_dem_Versiegeln_veraenderte_Mail_zaehlt_nicht()
    {
        var m = Forwarded("google.com", changedAfter: true);
        Assert.Equal("fail", PhishCheck.Auth(m, ["mail.own.test"]).Dmarc);
        Assert.Contains(PhishCheck.Analyze(m, new PhishCheck.Context { OwnHosts = ["mail.own.test"] }), f => f.Kind == "auth");
    }

    [Fact]
    public void Anbieter_meldet_selbst_Fehlschlag()
    {
        Assert.Equal("fail", PhishCheck.Auth(Forwarded("google.com", dmarc: "fail"), ["mail.own.test"]).Dmarc);
    }

    [Fact]
    public void Ohne_Schluessel_im_DNS_zaehlt_die_Kette_nicht()
    {
        keys.Clear();
        Assert.Equal("fail", PhishCheck.Auth(Forwarded("google.com"), ["mail.own.test"]).Dmarc);
    }
}
