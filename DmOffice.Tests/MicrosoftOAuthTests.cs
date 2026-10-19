using System.Net;
using System.Text;
using DmOffice.Core;
using DmOffice.Mail;

namespace DmOffice.Tests;

/// <summary>OAuth2 fuer private Microsoft-Konten: Erneuern, Austausch des Refresh-Tokens, Speichern,
/// Geraetecode. Microsoft ist ein Fake - kein echtes Konto, keine echten Tokens.</summary>
public class MicrosoftOAuthTests
{
    /// <summary>Antwortet der Reihe nach mit den vorgegebenen Texten und merkt sich die Anfragen.</summary>
    private sealed class FakeMicrosoft(params (HttpStatusCode Status, string Json)[] answers) : HttpMessageHandler
    {
        private int _next;
        public readonly List<(string Url, Dictionary<string, string> Form)> Requests = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
            var form = body.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2))
                .ToDictionary(p => WebUtility.UrlDecode(p[0]), p => WebUtility.UrlDecode(p.Length > 1 ? p[1] : ""));
            Requests.Add((request.RequestUri!.ToString(), form));
            var (status, json) = answers[Math.Min(_next++, answers.Length - 1)];
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private static (MicrosoftOAuth OAuth, FakeMicrosoft Fake) Make(params (HttpStatusCode, string)[] answers)
    {
        var fake = new FakeMicrosoft(answers);
        return (new MicrosoftOAuth("client-123", new HttpClient(fake)), fake);
    }

    private static MailAccount Account(string refresh) => new()
    {
        Id = "acc1", Email = "anna@outlook.com", ImapUser = "anna@outlook.com", Auth = MailAuth.Microsoft, OAuthRefreshToken = refresh,
    };

    private static string Token(string access, string? refresh = null, int expires = 3600) =>
        $$"""{"token_type":"Bearer","access_token":"{{access}}","expires_in":{{expires}}{{(refresh == null ? "" : $",\"refresh_token\":\"{refresh}\"")}}}""";

    [Fact]
    public void Ohne_Client_ID_aus()
    {
        Assert.False(new MicrosoftOAuth(null, new HttpClient()).Enabled);
        Assert.False(new MicrosoftOAuth("  ", new HttpClient()).Enabled);
        Assert.True(new MicrosoftOAuth("abc", new HttpClient()).Enabled);
    }

    [Theory]
    [InlineData("anna@outlook.com", true)]
    [InlineData("anna@Hotmail.co.uk", true)]
    [InlineData("anna@live.de", true)]
    [InlineData("anna@msn.com", true)]
    [InlineData("anna@gmx.de", false)]
    [InlineData("anna@firma-mit-office365.de", false)]
    [InlineData("kein-at", false)]
    public void Erkennt_private_Microsoft_Domains(string email, bool expected) =>
        Assert.Equal(expected, MicrosoftOAuth.IsConsumerDomain(email));

    [Fact]
    public async Task Erneuert_mit_Refresh_Token_und_haelt_das_Access_Token()
    {
        var (oauth, fake) = Make((HttpStatusCode.OK, Token("AT1")));
        var a = Account("RT1");

        Assert.Equal("AT1", await oauth.AccessTokenAsync(a, default));
        Assert.Equal("AT1", await oauth.AccessTokenAsync(a, default));   // aus dem Speicher

        var (url, form) = Assert.Single(fake.Requests);
        Assert.Equal(MicrosoftOAuth.Authority + "token", url);
        Assert.Equal("refresh_token", form["grant_type"]);
        Assert.Equal("RT1", form["refresh_token"]);
        Assert.Equal("client-123", form["client_id"]);
        Assert.Contains("https://outlook.office.com/IMAP.AccessAsUser.All", form["scope"]);
        Assert.Contains("https://outlook.office.com/SMTP.Send", form["scope"]);
        Assert.Contains("offline_access", form["scope"]);
    }

    [Fact]
    public async Task Kurz_vor_Ablauf_wird_neu_geholt()
    {
        var (oauth, fake) = Make((HttpStatusCode.OK, Token("AT1", expires: 60)), (HttpStatusCode.OK, Token("AT2")));
        var a = Account("RT1");
        Assert.Equal("AT1", await oauth.AccessTokenAsync(a, default));
        Assert.Equal("AT2", await oauth.AccessTokenAsync(a, default));
        Assert.Equal(2, fake.Requests.Count);
    }

    [Fact]
    public async Task Neues_Refresh_Token_wird_gemeldet_und_weiter_benutzt()
    {
        var (oauth, fake) = Make((HttpStatusCode.OK, Token("AT1", "RT2", expires: 60)), (HttpStatusCode.OK, Token("AT2", "RT3")));
        var rotated = new List<(string, string, string)>();
        oauth.TokenRotated = (id, old, fresh) => rotated.Add((id, old, fresh));

        // Das Konto fragt noch mit RT1 (neues Token noch nicht gespeichert) - erneuert wird trotzdem mit RT2
        await oauth.AccessTokenAsync(Account("RT1"), default);
        await oauth.AccessTokenAsync(Account("RT1"), default);

        Assert.Equal(new[] { "RT1", "RT2" }, fake.Requests.Select(r => r.Form["refresh_token"]));
        Assert.Equal(new[] { ("acc1", "RT1", "RT2"), ("acc1", "RT1", "RT3") }, rotated);
    }

    [Fact]
    public async Task Abgelaufene_Anmeldung_ergibt_verstaendlichen_Fehler()
    {
        var (oauth, _) = Make((HttpStatusCode.BadRequest, """{"error":"invalid_grant","error_description":"AADSTS70000: The refresh token has expired.\r\nTrace ID: x"}"""));
        var ex = await Assert.ThrowsAsync<MailKit.Security.AuthenticationException>(() => oauth.AccessTokenAsync(Account("RT1"), default));
        Assert.Equal("Microsoft-Anmeldung abgelaufen – Konto bearbeiten und neu anmelden.", ex.Message);
    }

    [Fact]
    public async Task Ohne_Token_oder_Client_ID_kein_Aufruf()
    {
        var (oauth, fake) = Make((HttpStatusCode.OK, Token("AT1")));
        await Assert.ThrowsAsync<MailKit.Security.AuthenticationException>(() => oauth.AccessTokenAsync(Account(""), default));
        var off = new MicrosoftOAuth(null, new HttpClient(fake));
        await Assert.ThrowsAsync<MailKit.Security.AuthenticationException>(() => off.AccessTokenAsync(Account("RT1"), default));
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task Geraetecode_Ablauf_bis_zum_Token()
    {
        var (oauth, fake) = Make(
            (HttpStatusCode.OK, """{"device_code":"DC","user_code":"ABCD-EFGH","verification_uri":"https://www.microsoft.com/link","expires_in":900,"interval":5}"""),
            (HttpStatusCode.BadRequest, """{"error":"authorization_pending"}"""),
            (HttpStatusCode.OK, Token("AT1", "RT1")));

        var start = await oauth.StartAsync("u1", default);
        Assert.Equal(("ABCD-EFGH", "https://www.microsoft.com/link", 5), (start.UserCode, start.VerificationUri, start.Interval));
        Assert.Equal(MicrosoftOAuth.Authority + "devicecode", fake.Requests[0].Url);

        Assert.Equal("pending", await oauth.PollAsync("u1", start.Flow, default));
        Assert.Null(oauth.Grant("u1", start.Flow));
        Assert.Equal("ok", await oauth.PollAsync("u1", start.Flow, default));
        Assert.Equal("urn:ietf:params:oauth:grant-type:device_code", fake.Requests[2].Form["grant_type"]);
        Assert.Equal("DC", fake.Requests[2].Form["device_code"]);

        // Nur fuer den Benutzer, der angefangen hat
        Assert.Equal("RT1", oauth.Grant("u1", start.Flow));
        Assert.Null(oauth.Grant("u2", start.Flow));
        Assert.Equal("expired", await oauth.PollAsync("u2", start.Flow, default));

        // Das Access-Token der Anmeldung gilt gleich - der Verbindungstest danach fragt Microsoft nicht noch einmal
        Assert.Equal("AT1", await oauth.AccessTokenAsync(Account("RT1"), default));
        Assert.Equal(3, fake.Requests.Count);

        oauth.Forget(start.Flow);
        Assert.Null(oauth.Grant("u1", start.Flow));
    }

    [Theory]
    [InlineData("""{"error":"authorization_declined"}""", "denied")]
    [InlineData("""{"error":"expired_token"}""", "expired")]
    [InlineData("""{"error":"slow_down"}""", "pending")]
    public async Task Geraetecode_Fehler(string answer, string expected)
    {
        var (oauth, _) = Make(
            (HttpStatusCode.OK, """{"device_code":"DC","user_code":"X","verification_uri":"https://www.microsoft.com/link","expires_in":900,"interval":5}"""),
            (HttpStatusCode.BadRequest, answer));
        var start = await oauth.StartAsync("u1", default);
        Assert.Equal(expected, await oauth.PollAsync("u1", start.Flow, default));
    }

    private static UserSpace Space(string name)
    {
        var dir = Directory.CreateTempSubdirectory(name);
        return new UserSpace(new UserRecord { Login = "t" }, new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "t"));
    }

    [Fact]
    public void Neues_Refresh_Token_landet_im_Konto()
    {
        var space = Space("dmoauth");
        space.UpdateSettings(s => s.MailAccounts.Add(Account("RT1")));

        Assert.True(MicrosoftOAuth.Persist([space], "acc1", "RT1", "RT2"));
        Assert.Equal("RT2", space.Account("acc1").OAuthRefreshToken);

        // Inzwischen neu angemeldet (RT9): das alte Token ueberschreibt nichts
        space.UpdateSettings(s => s.MailAccounts[0].OAuthRefreshToken = "RT9");
        Assert.False(MicrosoftOAuth.Persist([space], "acc1", "RT2", "RT3"));
        Assert.Equal("RT9", space.Account("acc1").OAuthRefreshToken);
        Assert.False(MicrosoftOAuth.Persist([space], "fremd", "RT9", "RT3"));
    }

    [Fact]
    public void Speichern_Token_nur_aus_Anmeldung_und_nie_die_Flow_Kennung()
    {
        var target = new MailAccount();
        // Ein mitgeschicktes Token zaehlt nicht - nur Claim aus einer abgeschlossenen Anmeldung
        SettingsApi.Merge(target, new MailAccount { Email = "anna@live.de", Auth = MailAuth.Microsoft, OAuthRefreshToken = "untergeschoben", OAuthFlow = "f1" });
        Assert.Null(target.OAuthRefreshToken);
        Assert.Null(target.OAuthFlow);
        SettingsApi.Claim(target, "RT1");
        Assert.Equal("RT1", target.OAuthRefreshToken);

        // Bearbeiten ohne neue Anmeldung behaelt das Token
        SettingsApi.Merge(target, new MailAccount { Email = "anna@live.de", Auth = MailAuth.Microsoft });
        SettingsApi.Claim(target, null);
        Assert.Equal("RT1", target.OAuthRefreshToken);

        // Zurueck auf Passwort: Token weg
        SettingsApi.Merge(target, new MailAccount { Email = "anna@live.de", Auth = MailAuth.Password });
        Assert.Null(target.OAuthRefreshToken);
        SettingsApi.Claim(target, "RT2");
        Assert.Null(target.OAuthRefreshToken);

        // Gespeichert wird die Auth-Art als Text, die Flow-Kennung gar nicht
        var space = Space("dmoauthjson");
        space.UpdateSettings(s => s.MailAccounts.Add(new MailAccount { Id = "x", Auth = MailAuth.Microsoft, OAuthRefreshToken = "RT1" }));
        var json = space.Db.Get("settings")!;
        Assert.Contains("\"auth\":\"Microsoft\"", json);
        Assert.DoesNotContain("oAuthFlow", json);
        Assert.Equal(MailAuth.Microsoft, space.Account("x").Auth);
    }
}
