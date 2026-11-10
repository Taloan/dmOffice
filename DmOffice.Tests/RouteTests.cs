using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>Keine zwei Endpunkte mit derselben Methode und derselben Route - sonst antwortet der Server dort mit einem Fehler.</summary>
[Collection("server")]
public class RouteTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public RouteTests(ChatFixture fx) => _fx = fx;

    [Fact]
    public void Keine_doppelten_Routen()
    {
        _fx.As("anna");   // Server gestartet
        var endpoints = _fx.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();
        var dup = endpoints
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"]).Select(m => m + " " + e.RoutePattern.RawText?.TrimEnd('/')))
            .GroupBy(x => x, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.Empty(dup);
    }
}
