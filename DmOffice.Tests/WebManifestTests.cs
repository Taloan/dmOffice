using System.Text.Json.Nodes;
using DmOffice.Core;

namespace DmOffice.Tests;

/// <summary>Manifest der installierbaren App: mit Kennung linkhandler als Programm für mailto: und webcal:.</summary>
public class WebManifestTests
{
    [Fact]
    public void Protocol_handlers_fuer_mailto_und_webcal()
    {
        var json = WebManifest.WithProtocolHandlers("""{ "name": "dmOffice", "description": "E-Mail, Kalender", "start_url": "/" }""");
        var root = JsonNode.Parse(json)!.AsObject();
        Assert.Equal("dmOffice", (string?)root["name"]);
        Assert.Equal("E-Mail, Kalender", (string?)root["description"]);
        var handlers = root["protocol_handlers"]!.AsArray();
        Assert.Equal(["mailto", "webcal"], handlers.Select(h => (string?)h!["protocol"]));
        Assert.All(handlers, h => Assert.Equal("/?handle=%s", (string?)h!["url"]));
        Assert.Contains("\"/?handle=%s\"", json);
    }
}
