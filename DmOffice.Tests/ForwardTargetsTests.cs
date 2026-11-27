using System.Net;
using System.Net.Http.Json;
using System.Text;
using DmOffice.Core;
using DmOffice.Mail;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using MimeKit.Cryptography;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Security;

namespace DmOffice.Tests;

/// <summary>Kennung ruleforward: Weiterleiten/Umleiten nur an bestätigte Ziele, Umleiten lässt DKIM gültig, Kopie in andere Konten.</summary>
[Collection("server")]
public class ForwardTargetsTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public ForwardTargetsTests(ChatFixture fx) => _fx = fx;

    private static MailRule Forward(string type, string to, string? account = null) =>
        new() { Name = "R", AccountId = account, Conditions = [new() { Field = "subject", Op = "contains", Value = "x" }], Actions = [new() { Type = type, Value = to }] };

    private static T WithFeature<T>(Func<T> f)
    {
        var before = Features.InReleaseOff.ToList();
        Features.Set(before.Where(x => x != Features.RuleForward));
        try { return f(); } finally { Features.Set(before); }
    }

    private static async Task WithFeature(Func<Task> f)
    {
        var before = Features.InReleaseOff.ToList();
        Features.Set(before.Where(x => x != Features.RuleForward));
        try { await f(); } finally { Features.Set(before); }
    }

    [Fact]
    public void Neue_Kontoadresse_gilt_nur_mit_passendem_Anmeldenamen_als_eigene() => WithFeature(() =>
    {
        var s = new UserSettings { MailAccounts = [new MailAccount { Id = "a1", Email = "alt@example.net", ImapUser = "login1" }] };
        // Erster Abgleich: die bisherige Adresse bleibt eigene, auch mit abweichendem Anmeldenamen
        ForwardTargets.Update(s, []);
        Assert.Equal(["alt@example.net"], s.OwnAddresses);
        // Später eingetragen: abweichender Anmeldename braucht Bestätigung, gleicher Anmeldename gilt
        s.MailAccounts.Add(new MailAccount { Id = "a2", Email = "fremd@example.org", ImapUser = "login2" });
        s.MailAccounts.Add(new MailAccount { Id = "a3", Email = "Neu@Example.org", ImapUser = "neu@example.org" });
        Assert.False(ForwardTargets.Allowed(s, "fremd@example.org"));
        Assert.True(ForwardTargets.Allowed(s, "neu@example.org"));
        Assert.True(ForwardTargets.Allowed(s, "alt@example.net"));
        Assert.Equal(["fremd@example.org"], ForwardTargets.Update(s, [Forward("forward", "fremd@example.org, neu@example.org")]).Pending);
        // Von der Verwaltung eingerichtet: gilt
        ForwardTargets.Trust(s, "Fremd@example.org");
        Assert.True(ForwardTargets.Allowed(s, "fremd@example.org"));
        return 0;
    });

    [Fact]
    public void Altbestand_gilt_bestaetigt_neues_Ziel_wartet_eigene_Adressen_gelten() => WithFeature(() =>
    {
        var s = new UserSettings { Rules = [Forward("forward", "alt@example.org")], MailAccounts = [new MailAccount { Id = "a1", Email = "Ich@Example.net" }] };
        // Noch nie eingerichtet: alles, was in Regeln steht, gilt
        Assert.Null(s.ForwardTargets);
        Assert.True(ForwardTargets.Allowed(s, "alt@example.org"));
        var (pending, _) = ForwardTargets.Update(s, [Forward("forward", "alt@example.org, neu@example.org"), Forward("redirect", "ich@example.net")]);
        Assert.Equal(["neu@example.org"], pending);
        Assert.True(ForwardTargets.Allowed(s, "ALT@example.org"));
        Assert.False(ForwardTargets.Allowed(s, "neu@example.org"));
        // Eigene Adresse: ohne Eintrag und ohne Bestätigung
        Assert.True(ForwardTargets.Allowed(s, "ich@example.net"));
        Assert.DoesNotContain(s.ForwardTargets!, t => t.Address == "ich@example.net");
        // Aus allen Regeln entfernt: vergessen - wieder eingetragen wartet es erneut
        ForwardTargets.Update(s, [Forward("forward", "neu@example.org")]);
        Assert.DoesNotContain(s.ForwardTargets!, t => t.Address == "alt@example.org");
        Assert.Equal(["alt@example.org"], ForwardTargets.Update(s, [Forward("forward", "neu@example.org, alt@example.org")]).Pending);
        return 0;
    });

    [Fact]
    public void Ohne_Kennung_wie_bisher()
    {
        var before = Features.InReleaseOff.ToList();
        Features.Set(before.Append(Features.RuleForward));
        try
        {
            var s = new UserSettings { Rules = [Forward("forward", "alt@example.org")] };
            Assert.Empty(ForwardTargets.Update(s, [Forward("forward", "neu@example.org")]).Pending);
            Assert.Null(s.ForwardTargets);
            Assert.True(ForwardTargets.Allowed(s, "neu@example.org"));
            // Umleiten gibt es ohne Kennung nicht, Weiterleiten wie bisher
            var m = new MimeMessage { Subject = "x" };
            var o = Rules.Evaluate([Forward("redirect", "a@example.org"), Forward("forward", "b@example.org")], "a1", m);
            Assert.Empty(o.RedirectTo);
            Assert.Equal(["b@example.org"], o.ForwardTo);
            // Ein anderes Konto als Ziel gibt es ohne Kennung nicht
            Assert.Null(MailSync.OtherAccount(_fx.Space("ben"), new MailAccount { Id = "a1" }, "a2|INBOX"));
        }
        finally { Features.Set(before); }
    }

    [Fact]
    public void Umleiten_und_Schleifenschutz() => WithFeature(() =>
    {
        var m = new MimeMessage { Subject = "x" };
        var o = Rules.Evaluate([Forward("redirect", "a@example.org; b@example.org")], "a1", m);
        Assert.Equal(["a@example.org", "b@example.org"], o.RedirectTo);
        var redirected = MailCompose.Redirect(new MailAccount { Email = "ich@example.net" }, m, "a@example.org");
        Assert.Equal("1", redirected.Headers["X-dmOffice-Forwarded"]);
        Assert.Equal("ich@example.net", redirected.Headers[HeaderId.ResentFrom]);
        Assert.Equal("a@example.org", redirected.Headers[HeaderId.ResentTo]);
        Assert.NotNull(redirected.Headers[HeaderId.ResentDate]);
        // Schon umgeleitet: nicht noch einmal
        Assert.Empty(Rules.Evaluate([Forward("redirect", "a@example.org"), Forward("forward", "c@example.org")], "a1", redirected).RedirectTo);
        Assert.Empty(Rules.Evaluate([Forward("forward", "c@example.org")], "a1", redirected).ForwardTo);
        return 0;
    });

    /// <summary>DNS-Eintrag sel._domainkey.example.org mit dem öffentlichen Schlüssel.</summary>
    private sealed class Keys(string pem) : IDnsResolver
    {
        public DnsTxtResponse QueryTxt(string name, CancellationToken ct = default)
        {
            if (name != "sel._domainkey.example.org") return new DnsTxtResponse(DnsQueryStatus.NonExistentDomain);
            var key = (AsymmetricKeyParameter)new PemReader(new StringReader(pem)).ReadObject();
            var der = Org.BouncyCastle.X509.SubjectPublicKeyInfoFactory.CreateSubjectPublicKeyInfo(key).GetEncoded();
            return new DnsTxtResponse(["v=DKIM1; k=rsa; p=" + Convert.ToBase64String(der)]);
        }
        public Task<DnsTxtResponse> QueryTxtAsync(string name, CancellationToken ct = default) => Task.FromResult(QueryTxt(name, ct));
    }

    [Fact]
    public async Task Umleiten_laesst_DKIM_gueltig()
    {
        var gen = new RsaKeyPairGenerator();
        gen.Init(new KeyGenerationParameters(new SecureRandom(), 2048));
        var pair = gen.GenerateKeyPair();
        var pub = new StringWriter();
        new PemWriter(pub).WriteObject(pair.Public);
        var signer = new DkimSigner(pair.Private, "example.org", "sel") { HeaderCanonicalizationAlgorithm = DkimCanonicalizationAlgorithm.Relaxed, BodyCanonicalizationAlgorithm = DkimCanonicalizationAlgorithm.Simple };
        var m = new MimeMessage();
        m.From.Add(MailboxAddress.Parse("absender@example.org"));
        m.To.Add(MailboxAddress.Parse("ich@example.net"));
        m.Subject = "Grüße mit Umlauten";
        m.Date = DateTimeOffset.Now;
        m.MessageId = "abc@example.org";
        m.Body = new Multipart("mixed") { new TextPart("plain") { Text = "Hallo – äöü\r\nZeile 2\r\n" }, new MimePart("application", "pdf") { FileName = "a.pdf", Content = new MimeContent(new MemoryStream(new byte[] { 1, 2, 3, 4 })) } };
        m.Prepare(EncodingConstraint.SevenBit);
        signer.Sign(m, [HeaderId.From, HeaderId.To, HeaderId.Subject, HeaderId.Date, HeaderId.MessageId]);
        // Wie beim Empfang: aus den Bytes geladen
        using var raw = new MemoryStream();
        m.WriteTo(raw);
        raw.Position = 0;
        var received = MimeMessage.Load(raw);
        var verifier = new DkimVerifier(new Keys(pub.ToString()));
        var sig = received.Headers[received.Headers.IndexOf(HeaderId.DkimSignature)];
        Assert.Equal(DkimSignatureStatus.Pass, (await verifier.VerifyAsync(received, sig)).Status);
        await WithFeature(async () =>
        {
            var r = MailCompose.Redirect(new MailAccount { Email = "ich@example.net" }, received, "ziel@example.com");
            // Wie beim Versand: geschrieben und beim Empfänger wieder geladen
            using var sent = new MemoryStream();
            r.WriteTo(sent);
            sent.Position = 0;
            var there = MimeMessage.Load(sent);
            Assert.Equal(DkimSignatureStatus.Pass, (await verifier.VerifyAsync(there, there.Headers[there.Headers.IndexOf(HeaderId.DkimSignature)])).Status);
            // Original Byte für Byte unter den neuen Kopfzeilen
            var text = Encoding.UTF8.GetString(sent.ToArray());
            Assert.EndsWith(Encoding.UTF8.GetString(raw.ToArray()), text);
        });
    }

    private async Task<HttpClient> Browser(string login)
    {
        var b = _fx.Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = true });
        (await b.PostAsJsonAsync("/api/auth/login", new { login, password = "geheim-123456" })).EnsureSuccessStatusCode();
        return b;
    }

    private sealed record TargetsView(List<string> Own, List<TargetView> Targets);
    private sealed record No8BitView(List<string> No8bit);

    [Fact]
    public async Task Ohne_8BITMIME_meldet_forward_targets_das_Konto_bei_Umleiten() => await WithFeature(async () =>
    {
        var space = _fx.Space("ben");
        var accounts = space.Settings.MailAccounts.ToList(); var rules = space.Settings.Rules.ToList();
        try
        {
            space.UpdateSettings(s =>
            {
                s.MailAccounts = [new MailAccount { Id = "k8a", Email = "a@example.net", SmtpHost = "smtp.example.invalid" }, new MailAccount { Id = "k8b", Email = "b@example.net", SmtpHost = "smtp.example.invalid" }];
                s.Rules = [Forward("redirect", "a@example.net", "k8a"), Forward("forward", "b@example.net", "k8b")];
                s.Smtp8BitMime = null;
            });
            ForwardTargets.Note8Bit(space, "k8a", false);
            ForwardTargets.Note8Bit(space, "k8b", false);
            var b = await Browser("ben");
            var v = await b.GetFromJsonAsync<No8BitView>("/api/settings/forward-targets");
            // Nur Konten mit Umleiten-Regel
            Assert.Equal(["k8a"], v!.No8bit);
            ForwardTargets.Note8Bit(space, "k8a", true);
            v = await b.GetFromJsonAsync<No8BitView>("/api/settings/forward-targets");
            Assert.Empty(v!.No8bit);
        }
        finally { space.UpdateSettings(s => { s.MailAccounts = accounts; s.Rules = rules; s.Smtp8BitMime = null; }); }
    });
    private sealed record TargetView(string Address, bool Confirmed, bool Expired, bool Locked, string? Error);

    [Fact]
    public async Task Bestaetigen_per_Link_einmalig_abgelaufen_und_Code() => await WithFeature(async () =>
    {
        var space = _fx.Space("ben");
        var index = _fx.Factory.Services.GetRequiredService<ForwardConfirmIndex>();
        // Altbestand: steht schon in einer Regel, bevor es die Bestätigung gab
        space.UpdateSettings(s => { s.Rules = [Forward("forward", "alt@example.org")]; s.ForwardTargets = null; });
        var b = await Browser("ben");
        var view = await b.GetFromJsonAsync<TargetsView>("/api/settings/forward-targets");
        Assert.Contains(view!.Targets, t => t.Address == "alt@example.org" && t.Confirmed);

        // Neues Ziel: Passwort wie bisher, dann wartet es (ohne Mailkonto: Fehler am Ziel, nichts gesendet)
        var rules = new[] { new { name = "R", conditions = new[] { new { field = "subject", op = "contains", value = "x" } },
            actions = new[] { new { type = "forward", value = "alt@example.org" }, new { type = "redirect", value = "neu@example.org, zwei@example.org" } } } };
        Assert.Equal(HttpStatusCode.Forbidden, (await b.PutAsJsonAsync("/api/settings/rules", rules)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await b.PutAsJsonAsync("/api/settings/rules", new { rules, password = "geheim-123456" })).StatusCode);
        // Der Versand der Bestätigungsmails läuft im Hintergrund - abwarten, sonst überschriebe er die Token unten
        for (int i = 0; i < 100 && space.Settings.ForwardTargets!.Count(t => t.SentAt != null) < 2; i++) await Task.Delay(50);
        view = await b.GetFromJsonAsync<TargetsView>("/api/settings/forward-targets");
        Assert.Contains(view!.Targets, t => t.Address == "neu@example.org" && !t.Confirmed && t.Error != null);
        Assert.True(ForwardTargets.Allowed(space.Settings, "alt@example.org"));
        Assert.False(ForwardTargets.Allowed(space.Settings, "neu@example.org"));

        // Link: Öffnen bestätigt nicht (Link-Scanner), erst der Knopf (POST); danach ist der Link verbraucht
        var (token, code, _) = ForwardTargets.Prepare(space, index, "neu@example.org", DateTime.UtcNow)!.Value;
        Assert.True(token.Length >= 43);
        var anon = _fx.Factory.CreateClient();
        var page = await anon.GetAsync("/forward-confirm/" + token);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("method=\"post\"", await page.Content.ReadAsStringAsync());
        Assert.False(ForwardTargets.Allowed(space.Settings, "neu@example.org"));
        Assert.Equal(HttpStatusCode.OK, (await anon.PostAsync("/forward-confirm/" + token, null)).StatusCode);
        Assert.True(ForwardTargets.Allowed(space.Settings, "neu@example.org"));
        Assert.Equal(HttpStatusCode.NotFound, (await anon.PostAsync("/forward-confirm/" + token, null)).StatusCode);
        // Der Code aus derselben Mail gilt danach auch nicht mehr
        Assert.Equal(ForwardTargets.Result.Unknown, ForwardTargets.ConfirmCode(space, index, "neu@example.org", code, DateTime.UtcNow));

        // Abgelaufen (nach 7 Tagen): Link bestätigt nicht und ist weg
        var old = ForwardTargets.Prepare(space, index, "zwei@example.org", DateTime.UtcNow.AddDays(-8))!.Value;
        Assert.Equal(HttpStatusCode.Gone, (await anon.PostAsync("/forward-confirm/" + old.Token, null)).StatusCode);
        Assert.False(ForwardTargets.Allowed(space.Settings, "zwei@example.org"));
        Assert.Equal(ForwardTargets.Result.Expired, ForwardTargets.ConfirmCode(space, index, "zwei@example.org", old.Code, DateTime.UtcNow));

        // Code im Regeln-Dialog: fünf falsche sperren, nach neuer Mail klappt der richtige
        var fresh = ForwardTargets.Prepare(space, index, "zwei@example.org", DateTime.UtcNow)!.Value;
        var wrong = fresh.Code == "000000" ? "111111" : "000000";
        for (int i = 0; i < ForwardTargets.MaxCodeTries - 1; i++)
            Assert.Equal(HttpStatusCode.BadRequest, (await b.PostAsJsonAsync("/api/settings/forward-targets/confirm", new { address = "zwei@example.org", code = wrong })).StatusCode);
        Assert.Equal(ForwardTargets.Result.TooManyTries, ForwardTargets.ConfirmCode(space, index, "zwei@example.org", wrong, DateTime.UtcNow));
        Assert.Equal(ForwardTargets.Result.TooManyTries, ForwardTargets.ConfirmCode(space, index, "zwei@example.org", fresh.Code, DateTime.UtcNow));
        fresh = ForwardTargets.Prepare(space, index, "zwei@example.org", DateTime.UtcNow)!.Value;
        Assert.Equal(HttpStatusCode.OK, (await b.PostAsJsonAsync("/api/settings/forward-targets/confirm", new { address = "zwei@example.org", code = fresh.Code })).StatusCode);
        Assert.True(ForwardTargets.Allowed(space.Settings, "zwei@example.org"));

        // Tagesgrenze über alle Neusendungen: danach gilt auch der richtige Code erst am nächsten Tag
        var day = DateTime.UtcNow.AddDays(30);
        space.UpdateSettings(s => s.ForwardTargets!.Add(new ForwardTarget { Address = "drei@example.org" }));
        for (int round = 0; round * ForwardTargets.MaxCodeTries < ForwardTargets.MaxCodeTriesPerDay; round++)
        {
            var p = ForwardTargets.Prepare(space, index, "drei@example.org", day)!.Value;
            var bad = p.Code == "000000" ? "111111" : "000000";
            for (int i = 0; i < ForwardTargets.MaxCodeTries; i++) ForwardTargets.ConfirmCode(space, index, "drei@example.org", bad, day);
        }
        var last = ForwardTargets.Prepare(space, index, "drei@example.org", day)!.Value;
        Assert.Equal(ForwardTargets.Result.TooManyToday, ForwardTargets.ConfirmCode(space, index, "drei@example.org", last.Code, day));
        last = ForwardTargets.Prepare(space, index, "drei@example.org", day.AddDays(1.1))!.Value;
        Assert.Equal(ForwardTargets.Result.Confirmed, ForwardTargets.ConfirmCode(space, index, "drei@example.org", last.Code, day.AddDays(1.1)));

        // Unbekannter Link
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/forward-confirm/" + ForwardTargets.NewToken())).StatusCode);
        space.UpdateSettings(s => { s.Rules = []; s.ForwardTargets = null; });
    });

    [Fact]
    public void Kopie_in_anderes_eigenes_Konto() => WithFeature(() =>
    {
        var space = _fx.Space("ben");
        space.UpdateSettings(s => s.MailAccounts = [new MailAccount { Id = "a1", Email = "a@example.net" }, new MailAccount { Id = "a2", Email = "b@example.net" }]);
        try
        {
            var a1 = space.Settings.MailAccounts[0];
            var other = MailSync.OtherAccount(space, a1, "a2|Archiv/2026");
            Assert.Equal(("a2", "Archiv/2026"), (other!.Value.Account.Id, other.Value.Folder));
            // Eigenes Konto oder unbekanntes: kein anderes Konto; der eigene Ordner bleibt ein Ordner
            Assert.Null(MailSync.OtherAccount(space, a1, "a1|INBOX"));
            Assert.Null(MailSync.OtherAccount(space, a1, "zz|INBOX"));
            Assert.Equal("INBOX", MailSync.Local(a1, "a1|INBOX"));
            Assert.Equal("Archiv", MailSync.Local(a1, "Archiv"));
        }
        finally { space.UpdateSettings(s => s.MailAccounts = []); }
        return 0;
    });
}
