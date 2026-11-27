using System.Net;
using System.Text.Json;
using DmOffice.Core;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>Release-Liste: was in welcher Version freigeschaltet ist, DMOFFICE_FEATURES und die Sperre der API.</summary>
[Collection("server")]
public class FeaturesTests : IClassFixture<FilesServerFixture>
{
    private readonly FilesServerFixture _fx;
    public FeaturesTests(FilesServerFixture fx) => _fx = fx;

    // [feature:journal]
    private static readonly Dictionary<string, string> List = new() { ["chat"] = "2.0", ["journal"] = "2.3", ["coach"] = "2.4", ["intern"] = "aus" };
    // [feature:journal:else]
    // private static readonly Dictionary<string, string> List = new() { ["chat"] = "2.0", ["neu"] = "2.3", ["spaeter"] = "2.4", ["intern"] = "aus" };
    // [/feature:journal]

    [Fact]
    public void Version_entscheidet_was_freigeschaltet_ist()
    {
        // [feature:journal]
        Assert.Equal(new[] { "coach", "intern", "journal" }, Features.Compute("2.0", List, null).OrderBy(x => x));
        Assert.Equal(new[] { "coach", "intern" }, Features.Compute("2.3", List, null).OrderBy(x => x));
        // [feature:journal:else]
        // Assert.Equal(new[] { "intern", "neu", "spaeter" }, Features.Compute("2.0", List, null).OrderBy(x => x));
        // Assert.Equal(new[] { "intern", "spaeter" }, Features.Compute("2.3", List, null).OrderBy(x => x));
        // [/feature:journal]
        Assert.Equal(new[] { "intern" }, Features.Compute("2.4", List, null).OrderBy(x => x));
        // Nicht aufgeführt: gilt immer
        Assert.DoesNotContain("audio", Features.Compute("2.0", List, null));
    }

    [Fact]
    public void Testserver_schalten_mit_DMOFFICE_FEATURES_frei()
    {
        Assert.Empty(Features.Compute("2.0", List, "all"));
        // [feature:journal]
        Assert.Equal(new[] { "coach", "intern" }, Features.Compute("2.0", List, "+journal").OrderBy(x => x));
        // [feature:journal:else]
        // Assert.Equal(new[] { "intern", "spaeter" }, Features.Compute("2.0", List, "+neu").OrderBy(x => x));
        // [/feature:journal]
        Assert.Contains("chat", Features.Compute("2.0", List, "-chat"));
    }

    [Fact]
    public async Task Pfade_nicht_freigeschalteter_Funktionen_antworten_mit_404()
    {
        // [feature:journal]
        Assert.Equal(Features.Journal, Features.OfPath("/api/journal/entries"));
        Assert.Equal(Features.MailExpiry, Features.OfPath("/api/mail/abc/expire"));
        Assert.Equal(Features.MailExpiry, Features.OfPath("/api/mail/expiries"));
        // [feature:journal:else]
        // Assert.Equal(Features.Vault, Features.OfPath("/api/tresor/state"));
        // Assert.Equal(Features.Dms, Features.OfPath("/api/mail/abc/to-dms"));
        // [/feature:journal]
        Assert.Null(Features.OfPath("/api/mail/abc/messages"));
        // [feature:journal]
        Assert.Equal(Features.FileCategories, Features.OfPath("/api/files/categories"));
        // [/feature:journal]
        Assert.Equal(Features.ImportProfiles, Features.OfPath("/api/gallery/imports/abc/run"));
        Assert.Null(Features.OfPath("/api/files/list"));

        var c = _fx.Client();
        var config = _fx.Factory.Services.GetRequiredService<ServerConfig>();
        var before = Features.InReleaseOff.ToList();
        var wasOff = config.Current.ModulesOff;
        try
        {
            config.Update(s => s.ModulesOff = []);
            // [feature:journal]
            Features.Set([Features.Journal]);
            Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/journal/journals")).StatusCode);
            // [feature:journal:else]
            // Features.Set([Features.Dms]);
            // Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/dms/docs")).StatusCode);
            // [/feature:journal]
            var state = await c.GetStringAsync("/api/auth/state");
            // [feature:journal]
            Assert.Contains("journal", JsonDocument.Parse(state).RootElement.GetProperty("featuresOff").EnumerateArray().Select(x => x.GetString()));
            // [feature:journal:else]
            // Assert.Contains("\"featuresOff\":[\"dms\",", state);
            // // Nicht enthaltene Funktionen meldet der Server immer als aus (die App blendet sie sonst ein)
            // Assert.Contains("\"coach\"", state);
            // [/feature:journal]
            Features.Set([]);
            // [feature:journal]
            Assert.NotEqual(HttpStatusCode.NotFound, (await c.GetAsync("/api/journal/journals")).StatusCode);
            // [feature:journal:else]
            // Assert.NotEqual(HttpStatusCode.NotFound, (await c.GetAsync("/api/dms/docs")).StatusCode);
            // [/feature:journal]
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
            // [feature:journal]
            // Vorgabe enthält den Tagebuch-Rückblick
            Assert.Contains("\"journal\"", await c.GetStringAsync("/api/dashboard?device=desktop"));
            Features.Set([Features.Journal]);
            // [feature:journal:else]
            // // Vorgabe enthält die Notizen
            // Assert.Contains("\"notes\"", await c.GetStringAsync("/api/dashboard?device=desktop"));
            // Features.Set([Features.Notes]);
            // [/feature:journal]
            var shown = await c.GetStringAsync("/api/dashboard?device=desktop");
            // [feature:journal]
            Assert.DoesNotContain("\"journal\"", shown);
            Assert.DoesNotContain("\"journal\"", await c.GetStringAsync("/api/dashboard/types"));
            Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/dashboard/w/journal")).StatusCode);
            // [feature:journal:else]
            // Assert.DoesNotContain("\"notes\"", shown);
            // Assert.DoesNotContain("\"notes\"", await c.GetStringAsync("/api/dashboard/types"));
            // Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/dashboard/w/notes")).StatusCode);
            // [/feature:journal]
            // Speichern ohne das ausgeblendete Widget löscht es nicht
            var put = await c.PutAsync("/api/dashboard?device=desktop", new StringContent(shown, System.Text.Encoding.UTF8, "application/json"));
            Assert.True(put.IsSuccessStatusCode);
            Features.Set([]);
            // [feature:journal]
            Assert.Contains("\"journal\"", await c.GetStringAsync("/api/dashboard?device=desktop"));
            // [feature:journal:else]
            // Assert.Contains("\"notes\"", await c.GetStringAsync("/api/dashboard?device=desktop"));
            // [/feature:journal]
        }
        finally { Features.Set(before); await c.DeleteAsync("/api/dashboard?device=desktop"); }
    }

    [Fact]
    public void Angezeigte_Version_folgt_den_freigeschalteten_Funktionen()
    {
        string D(string env) { var (v, e) = Features.Display("2.0", List, Features.Compute("2.0", List, env)); return v + "|" + string.Join(",", e); }
        Assert.Equal("2.0|", D(""));
        // [feature:journal]
        Assert.Equal("2.3|", D("+journal"));          // alle 2.3-Funktionen frei: 2.3
        Assert.Equal("2.0|coach", D("+coach"));                // nur eine Funktion aus 2.4: 2.0 plus coach
        // [feature:journal:else]
        // Assert.Equal("2.3|", D("+neu"));              // alle 2.3-Funktionen frei: 2.3
        // Assert.Equal("2.0|spaeter", D("+spaeter"));            // nur eine Funktion aus 2.4: 2.0 plus spaeter
        // [/feature:journal]
        Assert.Equal("2.4|intern", D("all"));                  // alles: höchste Version plus die nie freigegebene
    }

    [Fact]
    public void Versionsnummer_schaltet_alles_bis_dahin_frei()
    {
        // [feature:journal]
        Assert.Equal(new[] { "coach", "intern" }, Features.Compute("2.0", List, "2.3").OrderBy(x => x));
        Assert.Equal(new[] { "intern" }, Features.Compute("2.0", List, "2.3,+coach").OrderBy(x => x));
        // [feature:journal:else]
        // Assert.Equal(new[] { "intern", "spaeter" }, Features.Compute("2.0", List, "2.3").OrderBy(x => x));
        // Assert.Equal(new[] { "intern" }, Features.Compute("2.0", List, "2.3,+spaeter").OrderBy(x => x));
        // [/feature:journal]
        Assert.Equal("2.3|", string.Join("|", new[] { Features.Display("2.0", List, Features.Compute("2.0", List, "2.3")).Shown, "" }));
    }
}
