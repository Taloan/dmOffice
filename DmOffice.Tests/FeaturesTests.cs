using System.Net;
using DmOffice.Core;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>Release-Liste: was in welcher Version freigeschaltet ist, DMOFFICE_FEATURES und die Sperre der API.</summary>
[Collection("server")]
public class FeaturesTests : IClassFixture<FilesServerFixture>
{
    private readonly FilesServerFixture _fx;
    public FeaturesTests(FilesServerFixture fx) => _fx = fx;

    private static readonly Dictionary<string, string> List = new() { ["chat"] = "2.0", ["neu"] = "2.3", ["spaeter"] = "2.4", ["intern"] = "aus" };

    [Fact]
    public void Version_entscheidet_was_freigeschaltet_ist()
    {
        Assert.Equal(new[] { "intern", "neu", "spaeter" }, Features.Compute("2.0", List, null).OrderBy(x => x));
        Assert.Equal(new[] { "intern", "spaeter" }, Features.Compute("2.3", List, null).OrderBy(x => x));
        Assert.Equal(new[] { "intern" }, Features.Compute("2.4", List, null).OrderBy(x => x));
        // Nicht aufgeführt: gilt immer
        Assert.DoesNotContain("audio", Features.Compute("2.0", List, null));
    }

    [Fact]
    public void Testserver_schalten_mit_DMOFFICE_FEATURES_frei()
    {
        Assert.Empty(Features.Compute("2.0", List, "all"));
        Assert.Equal(new[] { "intern", "spaeter" }, Features.Compute("2.0", List, "+neu").OrderBy(x => x));
        Assert.Contains("chat", Features.Compute("2.0", List, "-chat"));
    }

    [Fact]
    public async Task Pfade_nicht_freigeschalteter_Funktionen_antworten_mit_404()
    {
        Assert.Equal(Features.Vault, Features.OfPath("/api/tresor/state"));
        Assert.Equal(Features.Dms, Features.OfPath("/api/mail/abc/to-dms"));
        Assert.Null(Features.OfPath("/api/mail/abc/messages"));
        Assert.Equal(Features.ImportProfiles, Features.OfPath("/api/gallery/imports/abc/run"));
        Assert.Null(Features.OfPath("/api/files/list"));

        var c = _fx.Client();
        var config = _fx.Factory.Services.GetRequiredService<ServerConfig>();
        var before = Features.InReleaseOff.ToList();
        var wasOff = config.Current.ModulesOff;
        try
        {
            config.Update(s => s.ModulesOff = []);
            Features.Set([Features.Dms]);
            Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/dms/docs")).StatusCode);
            var state = await c.GetStringAsync("/api/auth/state");
            Assert.Contains("\"featuresOff\":[\"dms\",", state);
            // Nicht enthaltene Funktionen meldet der Server immer als aus (die App blendet sie sonst ein)
            Assert.Contains("\"coach\"", state);
            Features.Set([]);
            Assert.NotEqual(HttpStatusCode.NotFound, (await c.GetAsync("/api/dms/docs")).StatusCode);
        }
        finally { Features.Set(before); config.Update(s => s.ModulesOff = wasOff); }
    }

    [Fact]
    public void Pfade_der_Module()
    {
        Assert.Equal(Features.Audio, Features.OfPath("/api/audio/tracks"));
        Assert.Equal(Features.Gallery, Features.OfPath("/api/gallery/sources"));
        Assert.Equal(Features.Gallery, Features.OfPath("/api/public-album/abc/items"));
        Assert.Null(Features.OfPath("/api/gallery/info"));
        Assert.Equal(Features.Notes, Features.OfPath("/api/notes/folders"));
        Assert.Equal(Features.Vault, Features.OfPath("/api/notes/vault/x/folders"));
        Assert.Equal(Features.Tasks, Features.OfPath("/api/pim/tasks"));
        Assert.Equal(Features.Tasks, Features.OfPath("/api/pim/lists/3"));
        Assert.Null(Features.OfPath("/api/pim/events"));
        Assert.Null(Features.OfPath("/api/pim/collections/calendars"));
        Assert.Equal(Features.Office, Features.OfPath("/api/office/open"));
        Assert.Equal(Features.Office, Features.OfPath("/wopi/files/abc"));
        Assert.Equal(Features.Office, Features.OfPath("/onlyoffice/callback"));
        Assert.Null(Features.OfPath("/api/admin/office"));
        Assert.Null(Features.OfPath("/api/files/download"));
    }


    [Fact]
    public async Task Uebersicht_blendet_Widgets_aus_und_behaelt_sie()
    {
        var c = _fx.Client();
        var before = Features.InReleaseOff.ToList();
        try
        {
            Features.Set([]);
            // Vorgabe enthält die Notizen
            Assert.Contains("\"notes\"", await c.GetStringAsync("/api/dashboard?device=desktop"));
            Features.Set([Features.Notes]);
            var shown = await c.GetStringAsync("/api/dashboard?device=desktop");
            Assert.DoesNotContain("\"notes\"", shown);
            Assert.DoesNotContain("\"notes\"", await c.GetStringAsync("/api/dashboard/types"));
            Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/dashboard/w/notes")).StatusCode);
            // Speichern ohne das ausgeblendete Widget löscht es nicht
            var put = await c.PutAsync("/api/dashboard?device=desktop", new StringContent(shown, System.Text.Encoding.UTF8, "application/json"));
            Assert.True(put.IsSuccessStatusCode);
            Features.Set([]);
            Assert.Contains("\"notes\"", await c.GetStringAsync("/api/dashboard?device=desktop"));
        }
        finally { Features.Set(before); await c.DeleteAsync("/api/dashboard?device=desktop"); }
    }
}
