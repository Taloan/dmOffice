using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DmOffice.Core;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>Korrekturen für 2.1 an der Schnittstelle: Release-Liste, Eingaben prüfen, Zellen der TSV-Datei, Ziele von Abos und Abmeldelinks.</summary>
[Collection("server")]
public class Korrekturen21ApiTests : IClassFixture<FilesServerFixture>
{
    private readonly FilesServerFixture _fx;
    public Korrekturen21ApiTests(FilesServerFixture fx) => _fx = fx;

    private async Task<HttpClient> SignedIn()
    {
        var c = _fx.Client(signedIn: false, cookies: true);
        var r = await c.PostAsJsonAsync("/api/auth/login", new { login = FilesServerFixture.Login, password = "geheim-123456" });
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return c;
    }

    private static StringContent Body(string text) => new(text, Encoding.UTF8, "application/json");

    [Fact]
    public async Task Release_Liste_gilt_fuer_Notfall_und_auch_ohne_Anmeldung()
    {
        Assert.Equal(Features.Emergency, Features.OfPath("/api/auth/emergency/abc"));
        Assert.Equal(Features.Audio, Features.OfPath("/api/audio/tracks"));
        Assert.Null(Features.OfPath("/api/auth/login"));

        var config = _fx.Factory.Services.GetRequiredService<ServerConfig>();
        var before = Features.InReleaseOff.ToList();
        var wasOff = config.Current.ModulesOff;
        try
        {
            config.Update(s => s.ModulesOff = []);
            Features.Set([]);
            // Freigeschaltet: ohne Anmeldung 401
            Assert.Equal(HttpStatusCode.Unauthorized, (await _fx.Client(signedIn: false).GetAsync("/api/audio/tracks")).StatusCode);
            // Nicht freigeschaltet: 404, mit und ohne Anmeldung
            Features.Set([Features.Audio, Features.Emergency]);
            Assert.Equal(HttpStatusCode.NotFound, (await _fx.Client(signedIn: false).GetAsync("/api/audio/tracks")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await _fx.Client(signedIn: false).PostAsync("/api/auth/emergency/1", null)).StatusCode);
            var c = await SignedIn();
            Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsync("/api/auth/emergency/1", null)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/audio/tracks")).StatusCode);
        }
        finally { Features.Set(before); config.Update(s => s.ModulesOff = wasOff); }
    }


    [Fact]
    public async Task Einstellungen_der_Oberflaeche_nur_als_Objekt()
    {
        var c = _fx.Client();
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsync("/api/settings/ui", Body("[]"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsync("/api/settings/ui", Body("\"text\""))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsync("/api/settings/ui", Body("{kaputt"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.PutAsync("/api/settings/ui", Body("{\"language\":\"de\"}"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/chat/rooms")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/chat/changes")).StatusCode);

        // Schon gespeichert (ältere Version): Leser bleiben bei der Vorgabe
        var before = _fx.Db.Get("ui.prefs");
        try
        {
            _fx.Db.Set("ui.prefs", "[]");
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/chat/rooms")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/chat/changes")).StatusCode);
            var spaces = _fx.Factory.Services.GetRequiredService<Spaces>();
            var space = spaces.All().First(s => s.User.Login == FilesServerFixture.Login);
            Assert.False(DmOffice.Chat.Pukki.WantsEnglish(space));
            Assert.Null(DmOffice.Chat.ChatApi.LookOf(space.User.Id).Accent);
        }
        finally { _fx.Db.Set("ui.prefs", before ?? "{}"); }
    }

    [Fact]
    public async Task Fehlende_Felder_ergeben_400_mit_Meldung()
    {
        var c = _fx.Client();
        // Tresor löschen ohne Inhalt
        var del = new HttpRequestMessage(HttpMethod.Delete, "/api/tresor/") { Content = Body("") };
        var r = await c.SendAsync(del);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("error", await r.Content.ReadAsStringAsync());

        // Notizinhalt ohne Blöcke, mit falschen Typen, unlesbar
        var id = JsonDocument.Parse(await (await c.PostAsJsonAsync("/api/notes/", new { folder = (string?)null })).Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt64();
        foreach (var bad in new[] { "{\"items\":null}", "[]", "{kaputt", "{\"blocks\":[{\"i\":\"x\"}],\"plain\":\"\"}", "{\"blocks\":[null],\"plain\":\"\"}", "{\"blocks\":[],\"plain\":5}" })
        {
            var res = await c.PutAsync($"/api/notes/{id}/content", Body(bad));
            Assert.True(res.StatusCode == HttpStatusCode.BadRequest, $"{bad}: {(int)res.StatusCode}");
        }

        // Ordner-Kopie und Löschen ohne Auswahl
        r = await c.PostAsJsonAsync("/api/files/external/copy", new { mount = "x", to = "Ziel" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("Nichts ausgewählt.", await r.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/files/external/delete", new { mount = "x" })).StatusCode);

        // Regeln (nur in der angemeldeten Sitzung)
        var s = await SignedIn();
        foreach (var bad in new[] { "", "[null]", "[{\"name\":\"a\",\"conditions\":null}]", "[{\"name\":\"a\",\"actions\":[{\"type\":\"move\",\"value\":null}]}]", "5", "{\"rules\":[{\"id\":-1}]}" })
        {
            var res = await s.PutAsync("/api/settings/rules", Body(bad));
            Assert.True(res.StatusCode == HttpStatusCode.BadRequest, $"{bad}: {(int)res.StatusCode}");
        }
        Assert.Equal(HttpStatusCode.OK, (await s.PutAsync("/api/settings/rules", Body("[]"))).StatusCode);
    }

    [Fact]
    public async Task Aendernde_Cookie_Anfragen_ohne_Sec_Fetch_Site_pruefen_Origin()
    {
        var c = await SignedIn();
        var stamp = Guid.NewGuid().ToString("N")[..8];
        HttpRequestMessage Mkdir(string name, string? origin)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/api/files/mkdir") { Content = JsonContent.Create(new { path = name }) };
            if (origin != null) req.Headers.Add("Origin", origin);
            return req;
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(Mkdir("o-" + stamp + "-a", "https://fremd.example"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(Mkdir("o-" + stamp + "-b", "null"))).StatusCode);
        Assert.True((await c.SendAsync(Mkdir("o-" + stamp + "-c", "http://localhost"))).IsSuccessStatusCode);
        // Ohne beide Köpfe (Programme): erlaubt
        Assert.True((await c.SendAsync(Mkdir("o-" + stamp + "-d", null))).IsSuccessStatusCode);
        // Sec-Fetch-Site same-origin entscheidet vor Origin
        var own = Mkdir("o-" + stamp + "-e", "https://fremd.example");
        own.Headers.Add("Sec-Fetch-Site", "same-origin");
        Assert.True((await c.SendAsync(own)).IsSuccessStatusCode);
    }

    [Theory]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+49", "'+49")]
    [InlineData("-x", "'-x")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("\t=1", " =1")]
    [InlineData("/api/files", "/api/files")]
    [InlineData(null, "")]
    public void Zellen_der_TSV_Datei_als_Text(string? input, string expected) => Assert.Equal(expected, SecurityCenterApi.Clean(input));

    [Theory]
    [InlineData("8.8.8.8", true, true)]
    [InlineData("192.168.1.5", false, true)]
    [InlineData("10.0.0.2", false, true)]
    [InlineData("172.18.0.3", false, true)]
    [InlineData("127.0.0.1", false, false)]
    [InlineData("::1", false, false)]
    [InlineData("169.254.169.254", false, false)]
    [InlineData("::ffff:192.168.1.5", false, true)]
    [InlineData("64:ff9b::7f00:1", false, false)]
    public void Kalender_Abos_erreichen_das_Heimnetz_nur_mit_Erlaubnis(string ip, bool without, bool with)
    {
        var before = DmOffice.Pim.Subscriptions.HomeNet;
        try
        {
            DmOffice.Pim.Subscriptions.HomeNet = () => false;
            Assert.Equal(without, DmOffice.Pim.Subscriptions.Allowed(IPAddress.Parse(ip)));
            DmOffice.Pim.Subscriptions.HomeNet = () => true;
            Assert.Equal(with, DmOffice.Pim.Subscriptions.Allowed(IPAddress.Parse(ip)));
        }
        finally { DmOffice.Pim.Subscriptions.HomeNet = before; }
    }

    [Fact]
    public async Task Kalender_Abo_im_Heimnetz_ohne_Erlaubnis_wird_nicht_angelegt()
    {
        var before = DmOffice.Pim.Subscriptions.HomeNet;
        try
        {
            DmOffice.Pim.Subscriptions.HomeNet = () => false;
            var r = await _fx.Client().PostAsJsonAsync("/api/pim/subscriptions", new { url = "http://10.0.0.2:8080/k.ics", name = "Test" });
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            Assert.Contains("Diese Adresse ist nicht erlaubt.", await r.Content.ReadAsStringAsync());
        }
        finally { DmOffice.Pim.Subscriptions.HomeNet = before; }
    }

    [Theory]
    [InlineData("93.184.216.34", true)]
    [InlineData("64:ff9b::808:808", true)]
    [InlineData("2002:808:808::1", true)]
    [InlineData("64:ff9b::7f00:1", false)]
    [InlineData("64:ff9b::c0a8:101", false)]
    [InlineData("64:ff9b::a9fe:a9fe", false)]
    [InlineData("2002:7f00:1::1", false)]
    [InlineData("2002:c0a8:101::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    public void Oeffentliche_Adresse_auch_in_IPv6_verpackt(string ip, bool expected)
    {
        Assert.Equal(expected, DmOffice.Mail.Unsubscribe.IsPublic(IPAddress.Parse(ip)));
        Assert.Equal(expected, DmOffice.Notes.RecipeImport.IsPublic(IPAddress.Parse(ip)));
    }
}
