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

    private static readonly Dictionary<string, string> List = new() { ["chat"] = "2.0", ["journal"] = "2.3", ["coach"] = "2.4", ["intern"] = "aus" };

    [Fact]
    public void Version_entscheidet_was_freigeschaltet_ist()
    {
        Assert.Equal(new[] { "coach", "intern", "journal" }, Features.Compute("2.0", List, null).OrderBy(x => x));
        Assert.Equal(new[] { "coach", "intern" }, Features.Compute("2.3", List, null).OrderBy(x => x));
        Assert.Equal(new[] { "intern" }, Features.Compute("2.4", List, null).OrderBy(x => x));
        // Nicht aufgeführt: gilt immer
        Assert.DoesNotContain("audio", Features.Compute("2.0", List, null));
    }

    [Fact]
    public void Testserver_schalten_mit_DMOFFICE_FEATURES_frei()
    {
        Assert.Empty(Features.Compute("2.0", List, "all"));
        Assert.Equal(new[] { "coach", "intern" }, Features.Compute("2.0", List, "+journal").OrderBy(x => x));
        Assert.Contains("chat", Features.Compute("2.0", List, "-chat"));
    }

    [Fact]
    public async Task Pfade_nicht_freigeschalteter_Funktionen_antworten_mit_404()
    {
        Assert.Equal(Features.Journal, Features.OfPath("/api/journal/entries"));
        Assert.Equal(Features.MailExpiry, Features.OfPath("/api/mail/abc/expire"));
        Assert.Equal(Features.MailExpiry, Features.OfPath("/api/mail/expiries"));
        Assert.Null(Features.OfPath("/api/mail/abc/messages"));
        Assert.Equal(Features.FileCategories, Features.OfPath("/api/files/categories"));
        Assert.Equal(Features.ImportProfiles, Features.OfPath("/api/gallery/imports/abc/run"));
        Assert.Null(Features.OfPath("/api/files/list"));

        var c = _fx.Client();
        var config = _fx.Factory.Services.GetRequiredService<ServerConfig>();
        var before = Features.InReleaseOff.ToList();
        var wasOff = config.Current.ModulesOff;
        try
        {
            config.Update(s => s.ModulesOff = []);
            Features.Set([Features.Journal]);
            Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/journal/journals")).StatusCode);
            var state = await c.GetStringAsync("/api/auth/state");
            Assert.Contains("\"featuresOff\":[\"journal\"]", state);
            Features.Set([]);
            Assert.NotEqual(HttpStatusCode.NotFound, (await c.GetAsync("/api/journal/journals")).StatusCode);
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
            // Vorgabe enthält den Tagebuch-Rückblick
            Assert.Contains("\"journal\"", await c.GetStringAsync("/api/dashboard?device=desktop"));
            Features.Set([Features.Journal]);
            var shown = await c.GetStringAsync("/api/dashboard?device=desktop");
            Assert.DoesNotContain("\"journal\"", shown);
            Assert.DoesNotContain("\"journal\"", await c.GetStringAsync("/api/dashboard/types"));
            Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/dashboard/w/journal")).StatusCode);
            // Speichern ohne das ausgeblendete Widget löscht es nicht
            var put = await c.PutAsync("/api/dashboard?device=desktop", new StringContent(shown, System.Text.Encoding.UTF8, "application/json"));
            Assert.True(put.IsSuccessStatusCode);
            Features.Set([]);
            Assert.Contains("\"journal\"", await c.GetStringAsync("/api/dashboard?device=desktop"));
        }
        finally { Features.Set(before); await c.DeleteAsync("/api/dashboard?device=desktop"); }
    }
}
