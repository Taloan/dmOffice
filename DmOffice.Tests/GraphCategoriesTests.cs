using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DmOffice.Core;
using DmOffice.Mail;
using DmOffice.Pim;
using MailKit;

namespace DmOffice.Tests;

/// <summary>Mail-Kategorien bei Microsoft-Konten ueber Graph statt IMAP-Schlagworte. Microsoft ist ein Fake.</summary>
[Collection("GraphCategories")]
public class GraphCategoriesTests : IDisposable
{
    private sealed class FakeGraph(Func<string, string, string, (HttpStatusCode, string)> answer) : HttpMessageHandler
    {
        public readonly List<(string Method, string Path, string Body)> Requests = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
            var path = Uri.UnescapeDataString(request.RequestUri!.PathAndQuery).Replace("/v1.0", "");
            Requests.Add((request.Method.Method, path, body));
            var (status, json) = answer(request.Method.Method, path, body);
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    public void Dispose() => GraphCategories.ClientFactory = null;

    private static (UserSpace Space, MailAccount Account) Setup()
    {
        var dir = Directory.CreateTempSubdirectory("dmgraphcat");
        var db = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "t");
        var space = new UserSpace(new UserRecord { Login = "t" }, db);
        var account = new MailAccount { Id = "acc", Email = "anna@outlook.com", Auth = MailAuth.Microsoft, OAuthRefreshToken = "RT", GraphRefreshToken = "GRT", SyncMonths = 0 };
        space.UpdateSettings(s => s.MailAccounts.Add(account));
        foreach (var (folder, uid, mid) in new[] { ("INBOX", 1u, "m1@example.org"), ("INBOX", 2u, "m2@example.org"), ("Archiv", 7u, "m1@example.org") })
            db.Exec("INSERT INTO mail(account,folder,uid,message_id,subject,date,keywords) VALUES('acc',$f,$u,$m,'Betreff',0,'')", ("$f", folder), ("$u", (long)uid), ("$m", mid));
        return (space, account);
    }

    private static FakeGraph Use(Func<string, string, string, (HttpStatusCode, string)> answer)
    {
        var fake = new FakeGraph(answer);
        GraphCategories.ClientFactory = _ => new GraphClient(new HttpClient(fake), _ => Task.FromResult("TOKEN"));
        return fake;
    }

    private static string Keywords(UserDb db, string folder, uint uid) =>
        (string)db.Scalar("SELECT keywords FROM mail WHERE account='acc' AND folder=$f AND uid=$u", ("$f", folder), ("$u", (long)uid))!;

    [Fact]
    public void Nur_Microsoft_Konten_mit_Graph_Token()
    {
        Assert.True(GraphCategories.Uses(new MailAccount { Auth = MailAuth.Microsoft, GraphRefreshToken = "x" }));
        Assert.False(GraphCategories.Uses(new MailAccount { Auth = MailAuth.Microsoft }));
        Assert.False(GraphCategories.Uses(new MailAccount { Auth = MailAuth.Password, GraphRefreshToken = "x" }));
    }

    [Fact]
    public async Task Setzen_sucht_die_Mail_ueber_die_Message_ID_legt_die_Hauptkategorie_an_und_merkt_sich_die_Kennung()
    {
        var (space, account) = Setup();
        var fake = Use((m, p, _) => (m, p) switch
        {
            ("GET", "/me/outlook/masterCategories") => (HttpStatusCode.OK, """{"value":[{"id":"1","displayName":"Privat","color":"preset4"}]}"""),
            ("POST", "/me/outlook/masterCategories") => (HttpStatusCode.Created, "{}"),
            ("GET", _) when p.StartsWith("/me/messages?$filter=internetMessageId eq '<m1@example.org>'") => (HttpStatusCode.OK, """{"value":[{"id":"G1","categories":["Privat"]}]}"""),
            ("GET", "/me/messages/G1?$select=id,categories") => (HttpStatusCode.OK, """{"id":"G1","categories":["Privat","Rechnung"]}"""),
            ("PATCH", "/me/messages/G1") => (HttpStatusCode.OK, "{}"),
            _ => (HttpStatusCode.NotFound, "{}"),
        });

        await GraphCategories.SetAsync(space, account, "INBOX", [1], "dm_rechnung", true, default);
        var post = Assert.Single(fake.Requests, r => r.Method == "POST");
        Assert.Equal("Rechnung", (string?)JsonNode.Parse(post.Body)!["displayName"]);
        Assert.StartsWith("preset", (string?)JsonNode.Parse(post.Body)!["color"]);
        var patch = Assert.Single(fake.Requests, r => r.Method == "PATCH");
        Assert.Equal(new[] { "Privat", "Rechnung" }, JsonNode.Parse(patch.Body)!["categories"]!.AsArray().Select(x => (string)x!));
        Assert.Equal(" dm_privat dm_rechnung ", Keywords(space.Db, "INBOX", 1));

        // Zweites Mal: Kennung aus dem Gedaechtnis, keine Suche
        await GraphCategories.SetAsync(space, account, "INBOX", [1], "dm_rechnung", false, default);
        Assert.Single(fake.Requests, r => r.Path.Contains("$filter"));
        Assert.Equal(new[] { "Privat" }, JsonNode.Parse(fake.Requests.Last(r => r.Method == "PATCH").Body)!["categories"]!.AsArray().Select(x => (string)x!));
        Assert.DoesNotContain("dm_rechnung", Keywords(space.Db, "INBOX", 1));
    }

    [Fact]
    public async Task Abholen_uebernimmt_Kategorien_in_alle_Ordner_und_legt_unbekannte_an()
    {
        var (space, account) = Setup();
        var fake = Use((m, p, _) => (m, p) switch
        {
            ("GET", "/me/outlook/masterCategories") => (HttpStatusCode.OK, """{"value":[{"displayName":"Steuer","color":"preset7"}]}"""),
            ("GET", "/me/mailFolders?$top=100&$select=id,childFolderCount") => (HttpStatusCode.OK, """{"value":[{"id":"F1","childFolderCount":1}]}"""),
            ("GET", "/me/mailFolders/F1/childFolders?$top=100&$select=id,childFolderCount") => (HttpStatusCode.OK, """{"value":[{"id":"F2","childFolderCount":0}]}"""),
            ("GET", "/me/mailFolders/F1/messages/delta?$select=internetMessageId,categories") => (HttpStatusCode.OK,
                """{"value":[{"id":"G1","internetMessageId":"<m1@example.org>","categories":["Steuer","Wichtig"]}],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/me/mailFolders/F1/messages/delta?$deltatoken=D1"}"""),
            ("GET", "/me/mailFolders/F2/messages/delta?$select=internetMessageId,categories") => (HttpStatusCode.OK,
                """{"value":[{"id":"G2","internetMessageId":"<m2@example.org>","categories":[]}],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/me/mailFolders/F2/messages/delta?$deltatoken=D2"}"""),
            _ => (HttpStatusCode.NotFound, "{}"),
        });
        await GraphCategories.PullAsync(space, account, default);

        var steuer = Assert.Single(DmOffice.Settings.Categories.Of(space.Settings), c => c.Name == "Steuer");
        Assert.Equal("dm_steuer", steuer.Key);
        Assert.Equal(GraphCategories.Presets[7], steuer.Color);
        Assert.Equal(" dm_steuer dm_wichtig ", Keywords(space.Db, "INBOX", 1));
        Assert.Equal(" dm_steuer dm_wichtig ", Keywords(space.Db, "Archiv", 7));   // dieselbe Mail in einem anderen Ordner
        Assert.Equal("", Keywords(space.Db, "INBOX", 2));
        var state = GraphCategories.StateOf(space.Db, "acc");
        Assert.Null(state.LastError);
        Assert.EndsWith("D1", state.Delta["F1"]);

        // IMAP-Flags ueberschreiben die Kategorien nicht (MailSync gibt fuer diese Konten keine Schlagworte mit)
        DmOffice.Mail.MailStore.SetFlags(space.Db, "acc", "INBOX", 1, MessageFlags.Seen, null);
        Assert.Equal(" dm_steuer dm_wichtig ", Keywords(space.Db, "INBOX", 1));
        // ... und was doch dazwischenkommt, setzt der naechste Abgleich zurecht
        space.Db.Exec("UPDATE mail SET keywords='' WHERE uid=1");
        GraphCategories.Reapply(space.Db, "acc");
        Assert.Equal(" dm_steuer dm_wichtig ", Keywords(space.Db, "INBOX", 1));
    }

    [Fact]
    public async Task Ohne_Mail_Recht_ein_verstaendlicher_Fehler()
    {
        var (space, account) = Setup();
        Use((_, _, _) => (HttpStatusCode.Forbidden, """{"error":{"code":"ErrorAccessDenied","message":"Access is denied."}}"""));
        await GraphCategories.PullAsync(space, account, default);
        Assert.Contains("neu verbinden", GraphCategories.StateOf(space.Db, "acc").LastError);
    }
}
