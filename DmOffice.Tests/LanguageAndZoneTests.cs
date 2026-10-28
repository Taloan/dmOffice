using DmOffice.Office;
using Microsoft.AspNetCore.Http;

namespace DmOffice.Tests;

/// <summary>Zeitzonen aus aller Welt (GitHub #38) und Sprache des Office-Editors nach der Oberfläche (GitHub #37).</summary>
public class LanguageAndZoneTests
{
    [Theory]
    [InlineData("Australia/Sydney")]
    [InlineData("Pacific/Auckland")]
    [InlineData("America/Sao_Paulo")]
    public void Zeitzone_aus_aller_Welt_wird_angenommen(string zone)
    {
        // Dieselbe Prüfung wie beim Speichern der Einstellungen (PUT /api/settings/general)
        Assert.NotNull(DmOffice.Dav.Ics.Zone(zone));
        Assert.Null(DmOffice.Dav.Ics.Zone("Nowhere/Nothing"));
    }

    [Theory]
    [InlineData("?lang=en", "de-DE,de", "en")]
    [InlineData("?lang=de", "en-US", "de")]
    [InlineData("", "de-AT,de;q=0.9", "de")]
    [InlineData("", "fr-FR,fr", "en")]
    [InlineData("?lang=xx", "", "en")]
    public void Office_Sprache_folgt_der_Oberflaeche_sonst_dem_Browser(string query, string accept, string expected)
    {
        var http = new DefaultHttpContext();
        http.Request.QueryString = new QueryString(query);
        if (accept.Length > 0) http.Request.Headers.AcceptLanguage = accept;
        Assert.Equal(expected, WopiApi.LangOf(http));
    }
}
