using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DmOffice.Core;
using DmOffice.Mail;

namespace DmOffice.Tests;

/// <summary>Eigene Textbausteine beim Schreiben (Kennung „mailsnippets“): Anlegen, Ändern, Löschen, Bereinigen des Texts
/// (keine Skripte, eingebettete Bilder bleiben), Grenzen und der Schalter.</summary>
[Collection("server")]
public class MailSnippetsTests : IClassFixture<FilesServerFixture>, IDisposable
{
    private readonly FilesServerFixture _fx;
    private readonly List<string> _before = Features.InReleaseOff.ToList();

    public MailSnippetsTests(FilesServerFixture fx)
    {
        _fx = fx;
        Features.Set(_before.Where(x => x != Features.MailSnippets));
    }

    public void Dispose() => Features.Set(_before);

    private async Task<HttpClient> SignedIn()
    {
        var c = _fx.Client(signedIn: false, cookies: true);
        var r = await c.PostAsJsonAsync("/api/auth/login", new { login = FilesServerFixture.Login, password = "geheim-123456" });
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return c;
    }

    [Fact]
    public void Text_wird_bereinigt_Bilder_bleiben()
    {
        var html = MailSnippets.Clean("<style>body{display:none}</style><p onclick=\"x()\">Hallo <b>Welt</b></p><script>alert(1)</script><img src=\"data:image/png;base64,AAAA\">");
        Assert.DoesNotContain("script", html);
        Assert.DoesNotContain("onclick", html);
        Assert.DoesNotContain("<style", html);
        Assert.Contains("<b>Welt</b>", html);
        Assert.Contains("data:image/png;base64,AAAA", html);
    }

    [Fact]
    public async Task Anlegen_aendern_loeschen()
    {
        var c = await SignedIn();
        var r = await c.PostAsJsonAsync("/api/mail/snippets", new { title = "Grüße", code = "Gr", html = "<div>Viele Grüße</div>" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var created = await r.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetString()!;
        Assert.Equal("gr", created.GetProperty("code").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/mail/snippets", new { title = "", html = "<p>x</p>" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/mail/snippets", new { title = "x", code = "a b", html = "<p>x</p>" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/mail/snippets", new { title = "x", html = new string('a', MailSnippets.MaxHtml + 1) })).StatusCode);

        r = await c.PutAsJsonAsync($"/api/mail/snippets/{id}", new { title = "Grüße kurz", code = "", html = "<div>LG</div>" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var list = await c.GetFromJsonAsync<JsonElement>("/api/mail/snippets");
        var mine = list.EnumerateArray().Single(x => x.GetProperty("id").GetString() == id);
        Assert.Equal("Grüße kurz", mine.GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Null, mine.GetProperty("code").ValueKind);

        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/mail/snippets/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.DeleteAsync($"/api/mail/snippets/{id}")).StatusCode);
    }

    [Fact]
    public async Task Ausgeschaltet_404()
    {
        var c = await SignedIn();
        Features.Set(_before.Append(Features.MailSnippets));
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/mail/snippets")).StatusCode);
    }
}
