using System.Net;
using System.Net.Http.Headers;
using System.Text;
using DmOffice.Core;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>Server-Tests mit eigenem Datenordner nacheinander - Program liest DMOFFICE_DATA beim Start.</summary>
[CollectionDefinition("server", DisableParallelization = true)]
public class ServerCollection { }

/// <summary>
/// notizi-Zugang: ein App-Passwort, das nur den Abgleichordner sieht. Sobald es ihn gibt,
/// sehen alle anderen Zugaenge den Ordner ueber WebDAV nicht mehr.
/// </summary>
[Collection("server")]
public class NotiziScopeTests : IClassFixture<NextcloudServerFixture>
{
    private readonly NextcloudServerFixture _fx;
    public NotiziScopeTests(NextcloudServerFixture fx) => _fx = fx;

    private const string L = NextcloudServerFixture.Login;

    private HttpClient As(string password)
    {
        var c = _fx.Factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{L}:{password}")));
        return c;
    }

    private static Task<HttpResponseMessage> Send(HttpClient c, string method, string url, string? body = null, string depth = "1")
    {
        var req = new HttpRequestMessage(new HttpMethod(method), url);
        if (body != null) req.Content = new StringContent(body);
        if (method == "PROPFIND") req.Headers.Add("Depth", depth);
        return c.SendAsync(req);
    }

    [Fact]
    public async Task Eigener_notizi_Zugang_sieht_nur_den_Ordner_und_versteckt_ihn_vor_allen_anderen()
    {
        var full = As(_fx.AppPassword);
        await Send(full, "MKCOL", $"/dav/files/{L}/notizisync");
        Assert.True((await Send(full, "PUT", $"/dav/files/{L}/notizisync/alt.bin", "a")).IsSuccessStatusCode);
        Assert.True((await Send(full, "PUT", $"/dav/files/{L}/privat.txt", "geheim")).IsSuccessStatusCode);
        // Vorher: der volle Zugang sieht den Ordner noch (bisherige notizi-Verbindungen laufen weiter)
        Assert.Contains("notizisync", await (await Send(full, "PROPFIND", $"/dav/files/{L}/")).Content.ReadAsStringAsync());

        var db = _fx.Factory.Services.GetRequiredService<Spaces>().Get(_fx.Factory.Services.GetRequiredService<UserStore>().ByLogin(L)!.Id).Db;
        var notizi = As(AppPasswords.Create(db, "notizi", AppPasswords.ScopeNotizi));
        // Nur angelegt, noch nie benutzt: die bisherige Verbindung laeuft weiter
        Assert.Contains("notizisync", await (await Send(full, "PROPFIND", $"/dav/files/{L}/")).Content.ReadAsStringAsync());

        // notizi-Zugang: im Ordner alles, ausserhalb nichts
        Assert.Equal((HttpStatusCode)207, (await Send(notizi, "PROPFIND", $"/dav/files/{L}/notizisync/")).StatusCode);
        Assert.True((await Send(notizi, "PUT", $"/dav/files/{L}/notizisync/neu.bin", "n")).IsSuccessStatusCode);
        Assert.Equal("a", await notizi.GetStringAsync($"/dav/files/{L}/notizisync/alt.bin"));
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(notizi, "PROPFIND", $"/dav/files/{L}/")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await notizi.GetAsync($"/dav/files/{L}/privat.txt")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(notizi, "PROPFIND", $"/dav/calendars/{L}/")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await notizi.GetAsync("/api/mail/accounts")).StatusCode);

        // Alle anderen Zugaenge: den Ordner gibt es nicht mehr
        Assert.DoesNotContain("notizisync", await (await Send(full, "PROPFIND", $"/dav/files/{L}/")).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await full.GetAsync($"/dav/files/{L}/notizisync/alt.bin")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(full, "DELETE", $"/dav/files/{L}/notizisync")).StatusCode);
        Assert.Equal("geheim", await full.GetStringAsync($"/dav/files/{L}/privat.txt"));
        Assert.Equal("n", await notizi.GetStringAsync($"/dav/files/{L}/notizisync/neu.bin"));

        // Weiteres Geraet: ein zweiter notizi-Zugang - der erste bleibt gueltig, beide sehen den Ordner
        var second = As(AppPasswords.Create(db, "notizi", AppPasswords.ScopeNotizi));
        Assert.True((await Send(second, "PUT", $"/dav/files/{L}/notizisync/zwei.bin", "z")).IsSuccessStatusCode);
        Assert.Equal("z", await notizi.GetStringAsync($"/dav/files/{L}/notizisync/zwei.bin"));
        Assert.Equal("n", await second.GetStringAsync($"/dav/files/{L}/notizisync/neu.bin"));
    }

    [Fact]
    public async Task Andere_Zugaenge_schreiben_auch_per_MOVE_und_COPY_nicht_in_den_notizi_Ordner()
    {
        // Eigenes Konto: "notizi-Zugang benutzt" bleibt in der Datenbank und soll den Test oben nicht beeinflussen
        var login = "nz" + Guid.NewGuid().ToString("N")[..6];
        var user = _fx.Factory.Services.GetRequiredService<UserStore>().Create(login, "Notizi", "geheim-123456", isAdmin: false);
        var db = _fx.Factory.Services.GetRequiredService<Spaces>().Get(user.Id).Db;
        HttpClient AsUser(string password)
        {
            var c = _fx.Factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{login}:{password}")));
            return c;
        }
        var full = AsUser(AppPasswords.Create(db, "Test"));
        await Send(full, "MKCOL", $"/dav/files/{login}/notizisync");
        // notizi-Zugang einmal benutzen: ab jetzt ist der Ordner fuer alle anderen ausgeblendet
        var notizi = AsUser(AppPasswords.Create(db, "notizi", AppPasswords.ScopeNotizi));
        Assert.Equal((HttpStatusCode)207, (await Send(notizi, "PROPFIND", $"/dav/files/{login}/notizisync/")).StatusCode);
        Assert.True((await Send(notizi, "PUT", $"/dav/files/{login}/notizisync/note.bin", "echt")).IsSuccessStatusCode);

        var name = "mv-" + Guid.NewGuid().ToString("N")[..6] + ".txt";
        Assert.True((await Send(full, "PUT", $"/dav/files/{login}/{name}", "fremd")).IsSuccessStatusCode);
        foreach (var method in new[] { "MOVE", "COPY" })
        {
            var req = new HttpRequestMessage(new HttpMethod(method), $"/dav/files/{login}/{name}");
            req.Headers.Add("Destination", $"http://localhost/dav/files/{login}/notizisync/note.bin");
            req.Headers.Add("Overwrite", "T");
            Assert.Equal(HttpStatusCode.Forbidden, (await full.SendAsync(req)).StatusCode);
        }
        Assert.Equal("echt", await notizi.GetStringAsync($"/dav/files/{login}/notizisync/note.bin"));

        // Ein App-Passwort aus dem Login-Flow (Bereich "dav") sieht den Ordner ebenfalls nicht
        var flow = AsUser(AppPasswords.Create(db, "Nextcloud", AppPasswords.ScopeDav));
        Assert.DoesNotContain("notizisync", await (await Send(flow, "PROPFIND", $"/dav/files/{login}/")).Content.ReadAsStringAsync());
    }
}
