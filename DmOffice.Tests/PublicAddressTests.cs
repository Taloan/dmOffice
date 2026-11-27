using DmOffice.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DmOffice.Tests;

/// <summary>Öffentliche Adresse des Servers (Kennung publicurl): Prüfung der Eingabe, Vorrang Umgebungsvariable, ohne Einstellung wie bisher.</summary>
public class PublicAddressTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dmpublicurl").FullName;
    private readonly ServerConfig _config;

    public PublicAddressTests() => _config = new ServerConfig(new DataPaths(_dir));

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(PublicAddress.EnvVar, null);
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private HttpContext Request(string scheme = "http", string host = "192.168.178.20:8080", string pathBase = "")
    {
        var http = new DefaultHttpContext { RequestServices = new ServiceCollection().AddSingleton(_config).BuildServiceProvider() };
        http.Request.Scheme = scheme;
        http.Request.Host = new HostString(host);
        http.Request.PathBase = pathBase;
        return http;
    }

    [Theory]
    [InlineData("https://office.example.de", "https://office.example.de")]
    [InlineData("  https://Office.Example.DE/  ", "https://office.example.de")]
    [InlineData("http://office.example.de:8443", "http://office.example.de:8443")]
    [InlineData("https://office.example.de:443/", "https://office.example.de")]
    [InlineData("https://[2001:db8::1]:8443", "https://[2001:db8::1]:8443")]
    public void Gueltige_Adressen_in_einheitlicher_Form(string raw, string expected)
    {
        Assert.Equal(expected, PublicAddress.Normalize(raw, out var error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData("office.example.de")]
    [InlineData("ftp://office.example.de")]
    [InlineData("https://office.example.de/dmoffice")]
    [InlineData("https://office.example.de/?a=1")]
    [InlineData("https://office.example.de/#x")]
    [InlineData("https://anna:geheim@office.example.de")]
    [InlineData("/relativ")]
    public void Ungueltige_Adressen_mit_Meldung(string raw)
    {
        Assert.Null(PublicAddress.Normalize(raw, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Leer_ohne_Fehler()
    {
        Assert.Null(PublicAddress.Normalize("  ", out var error));
        Assert.Null(error);
    }

    [Fact]
    public void Ohne_Einstellung_die_Adresse_der_Anfrage()
    {
        var http = Request(pathBase: "/office");
        Assert.Equal("http://192.168.178.20:8080", PublicAddress.Origin(http));
        Assert.Equal("http://192.168.178.20:8080/office", PublicAddress.Base(http));
    }

    [Fact]
    public void Eingestellte_Adresse_gilt_statt_Host_der_Anfrage()
    {
        _config.Update(s => s.PublicUrl = "https://office.example.de");
        var http = Request(host: "anderer.example");
        Assert.Equal("https://office.example.de", PublicAddress.Origin(http));
        Assert.Equal("https://office.example.de", PublicAddress.Base(http));
        Assert.Equal("http://anderer.example", PublicAddress.FromRequest(http));
    }

    [Fact]
    public void Umgebungsvariable_hat_Vorrang_ungueltige_zaehlt_nicht()
    {
        _config.Update(s => s.PublicUrl = "https://office.example.de");
        Environment.SetEnvironmentVariable(PublicAddress.EnvVar, "https://cloud.example.org/");
        Assert.Equal("https://cloud.example.org", PublicAddress.Origin(Request()));
        Environment.SetEnvironmentVariable(PublicAddress.EnvVar, "cloud.example.org/pfad");
        Assert.Equal("https://office.example.de", PublicAddress.Origin(Request()));
    }

    [Fact]
    public void Kennung_aus_wie_bisher()
    {
        _config.Update(s => s.PublicUrl = "https://office.example.de");
        var before = Features.InReleaseOff.ToList();
        Features.Set(before.Append(Features.PublicUrl));
        try { Assert.Equal("http://192.168.178.20:8080", PublicAddress.Base(Request())); }
        finally { Features.Set(before); }
    }
}
