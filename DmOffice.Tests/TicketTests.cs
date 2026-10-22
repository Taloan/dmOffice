using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DmOffice.Core;

namespace DmOffice.Tests;

/// <summary>Tickets der Handy-App: grosse Dateien und Streams ausserhalb des Umschlags ohne App-Passwort.</summary>
[Collection("server")]
public class TicketTests : IClassFixture<FilesServerFixture>
{
    private readonly FilesServerFixture _fx;
    public TicketTests(FilesServerFixture fx) => _fx = fx;

    private static async Task<string> Ticket(HttpClient c, string method, string url, bool stream = false)
    {
        var r = await c.PostAsJsonAsync("/api/tickets", new { method, url, stream });
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}");
        var o = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(Tickets.Header, o.GetProperty("header").GetString());
        return o.GetProperty("ticket").GetString()!;
    }

    private Task<string> Ticket(string method, string url, bool stream = false) => Ticket(_fx.Client(), method, url, stream);

    /// <summary>Anonymer Client, der nur das Ticket mitschickt - so wie die App ausserhalb des Umschlags.</summary>
    private HttpClient WithTicket(string ticket)
    {
        var c = _fx.Client(signedIn: false);
        c.DefaultRequestHeaders.Add(Tickets.Header, ticket);
        return c;
    }

    private static MultipartFormDataContent Form(string name, byte[] data) => new() { { new ByteArrayContent(data), "files", name } };

    private async Task Put(string dir, string name, string text)
    {
        var r = await _fx.Client().PostAsync($"/api/files/upload?path={Uri.EscapeDataString(dir)}", Form(name, Encoding.UTF8.GetBytes(text)));
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Download_mit_Ticket_nur_fuer_genau_diese_Anfrage()
    {
        await Put("tk", "gross.bin", "inhalt");
        await Put("tk", "andere.bin", "geheim");
        var url = "/api/files/download?path=tk%2Fgross.bin";
        var c = WithTicket(await Ticket("GET", url));

        Assert.Equal("inhalt", await c.GetStringAsync(url));
        // Mehrfach (Wiederaufnahme, Bereiche) innerhalb der Laufzeit
        var range = new HttpRequestMessage(HttpMethod.Get, url);
        range.Headers.Range = new RangeHeaderValue(0, 1);
        Assert.Equal(HttpStatusCode.PartialContent, (await c.SendAsync(range)).StatusCode);

        // Andere Datei, zusaetzliche oder fehlende Werte, andere Methode, andere Route: alles abgelehnt
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/files/download?path=tk/andere.bin")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync(url + "&version=1")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync(url + "&inline=true")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync(url + "&path=tk/andere.bin")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/files/download")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsync("/api/files/upload?path=tk%2Fgross.bin", Form("x.txt", [1]))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/files/?path=tk")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/mail/accounts")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("/api/tickets", new { method = "GET", url = "/api/files/download?path=tk%2Fandere.bin" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/dav/files/" + FilesServerFixture.Login + "/tk/andere.bin")).StatusCode);
        // Gefaelscht oder kaputt
        var t = await Ticket("GET", url);
        Assert.Equal(HttpStatusCode.Unauthorized, (await WithTicket(t[..^4] + "AAAA").GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await WithTicket("quatsch").GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task Abspielen_und_fruehere_Version()
    {
        await Put("tk", "film.mp4", "v1");
        await Put("tk", "film.mp4", "v2");
        var versions = JsonDocument.Parse(await _fx.Client().GetStringAsync("/api/files/versions?path=tk/film.mp4")).RootElement.GetProperty("items");
        var id = versions[0].GetProperty("id").GetInt64();

        var stream = WithTicket(await Ticket("GET", "/api/files/download?path=tk%2Ffilm.mp4&inline=true", stream: true));
        Assert.Equal("v2", await stream.GetStringAsync("/api/files/download?path=tk%2Ffilm.mp4&inline=true"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await stream.GetAsync("/api/files/download?path=tk%2Ffilm.mp4")).StatusCode);

        var old = WithTicket(await Ticket("GET", $"/api/files/download?path=tk%2Ffilm.mp4&version={id}"));
        Assert.Equal("v1", await old.GetStringAsync($"/api/files/download?path=tk%2Ffilm.mp4&version={id}"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await old.GetAsync($"/api/files/download?path=tk%2Ffilm.mp4&version={id + 1}")).StatusCode);
    }

    [Fact]
    public async Task Schreibendes_Ticket_gilt_einmal()
    {
        var url = "/api/files/upload?path=tk%2Fhoch&conflict=rename&mtime=1700000000000";
        var t = await Ticket("POST", url);
        // Anderer Ordner oder andere Konfliktregel: abgelehnt (verbraucht nichts)
        Assert.Equal(HttpStatusCode.Unauthorized, (await WithTicket(t).PostAsync("/api/files/upload?path=tk&conflict=rename&mtime=1700000000000", Form("video.mp4", [1]))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await WithTicket(t).PostAsync("/api/files/upload?path=tk%2Fhoch&conflict=replace&mtime=1700000000000", Form("video.mp4", [1]))).StatusCode);

        var r = await WithTicket(t).PostAsync(url, Form("video.mp4", [1, 2, 3]));
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        var file = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("files")[0];
        Assert.Equal("tk/hoch/video.mp4", file.GetProperty("path").GetString());
        Assert.Equal(64, file.GetProperty("sha256").GetString()!.Length);
        // Ein zweites Mal geht nicht (Wiedereinspielen)
        Assert.Equal(HttpStatusCode.Unauthorized, (await WithTicket(t).PostAsync(url, Form("video.mp4", [9]))).StatusCode);
    }

    [Fact]
    public async Task Auch_fuer_andere_Bereiche_als_die_Dateiablage()
    {
        // Allgemein nutzbar (etwa Chat-Uploads in v2): hier am Beispiel einer Leseanfrage an die Mail
        var t = await Ticket("GET", "/api/mail/accounts");
        Assert.Equal(HttpStatusCode.OK, (await WithTicket(t).GetAsync("/api/mail/accounts")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await WithTicket(t).GetAsync("/api/mail/status")).StatusCode);
    }

    [Fact]
    public async Task Widerrufenes_App_Passwort_beendet_das_Ticket()
    {
        await Put("tk", "widerruf.txt", "x");
        var pw = AppPasswords.Create(_fx.Db, "Zweites Handy");
        var c = _fx.Client(signedIn: false);
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{FilesServerFixture.Login}:{pw}")));
        var url = "/api/files/download?path=tk%2Fwiderruf.txt";
        var t = await Ticket(c, "GET", url);
        Assert.Equal("x", await WithTicket(t).GetStringAsync(url));

        var id = AppPasswords.List(_fx.Db).Single(e => e.Name == "Zweites Handy").Id;
        AppPasswords.Delete(_fx.Db, id);
        Assert.Equal(HttpStatusCode.Unauthorized, (await WithTicket(t).GetAsync(url)).StatusCode);
    }

    [Theory]
    [InlineData("GET", "/api/auth/state")]
    [InlineData("POST", "/api/auth/2fa/begin")]
    [InlineData("GET", "/api/e2e/key")]
    [InlineData("POST", "/api/tickets")]
    [InlineData("GET", "/api/admin/users")]
    [InlineData("GET", "/api/settings/app-passwords")]
    [InlineData("GET", "/dav/files/berta/x.txt")]
    [InlineData("GET", "/api/mail/../auth/state")]
    [InlineData("GET", "/api/mail/%2e%2e/admin/users")]
    [InlineData("GET", "/api/files/..%2fadmin")]
    [InlineData("GET", "https://example.com/api/files/download")]
    [InlineData("GET", "//example.com/api/files/download")]
    [InlineData("GET", "api/files/download")]
    [InlineData("DELETE", "/api/files/download?path=x")]
    [InlineData("PROPFIND", "/api/files/download?path=x")]
    public async Task Kein_Ticket_fuer_Anmeldung_Verwaltung_und_Ausbrueche(string method, string url)
    {
        var r = await _fx.Client().PostAsJsonAsync("/api/tickets", new { method, url });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Ohne_Anmeldung_kein_Ticket() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await _fx.Client(signedIn: false).PostAsJsonAsync("/api/tickets", new { method = "GET", url = "/api/files/download?path=x" })).StatusCode);

    [Fact]
    public void Ticket_passt_nur_zur_eigenen_Anfrage()
    {
        var t = new Ticket("u", "s", null, "GET", "/api/files/download", "path=a%2Fb.txt&inline=true", "id");
        Microsoft.AspNetCore.Http.HttpRequest Req(string method, string path, string query)
        {
            var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext();
            ctx.Request.Method = method; ctx.Request.Path = path; ctx.Request.QueryString = new Microsoft.AspNetCore.Http.QueryString(query);
            return ctx.Request;
        }
        Assert.True(Tickets.Matches(t, Req("GET", "/api/files/download", "?path=a%2Fb.txt&inline=true")));
        Assert.True(Tickets.Matches(t, Req("GET", "/api/files/download", "?inline=true&path=a/b.txt")));   // Reihenfolge und Kodierung egal
        Assert.True(Tickets.Matches(t, Req("HEAD", "/api/files/download", "?path=a/b.txt&inline=true")));
        Assert.False(Tickets.Matches(t, Req("POST", "/api/files/download", "?path=a/b.txt&inline=true")));
        Assert.False(Tickets.Matches(t, Req("GET", "/api/files/Download", "?path=a/b.txt&inline=true")));
        Assert.False(Tickets.Matches(t, Req("GET", "/api/files/download", "?path=a/b.txt")));
        Assert.False(Tickets.Matches(t, Req("GET", "/api/files/download", "?path=a/b.txt&path=c&inline=true")));
        Assert.False(Tickets.Matches(t with { M = "POST" }, Req("HEAD", "/api/files/download", "?path=a/b.txt&inline=true")));
    }
}
