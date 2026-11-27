using System.Text;
using DmOffice.Sdk;
using Humanizer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests.Plugin;

public sealed class SampleCounter { public int Count; }

/// <summary>Beispiel: ein Dienst, eine Route, eine Tabelle, ein Export-Bereich.</summary>
public sealed class SamplePlugin : IDmOfficePlugin
{
    public void ConfigureServices(IServiceCollection services, PluginContext context) => services.AddSingleton<SampleCounter>();

    public void Map(IEndpointRouteBuilder routes, PluginContext context)
    {
        routes.MapGet("/api/samplex/other", () => "outside");
        routes.MapGroup("/api/sample/g").MapGet("/x", () => "inside");
        routes.MapGet("/api/sample/hello", (SampleCounter c) => new { text = "plugin_sample_hello".Humanize(), count = ++c.Count });
        // Serverweite Datenbank und angemeldete Person
        routes.MapPost("/api/sample/shared", (HttpContext http, IServerDataAccessor data, IPeople people) =>
        {
            var db = data.For(context.Key);
            db.Transaction(t => t.Execute("INSERT INTO sample_shared (who) VALUES ($w)", ("$w", people.Me(http)?.Login)));
            return new { count = Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM sample_shared")) };
        });
    }

    public void MapAdmin(IEndpointRouteBuilder routes, PluginContext context) =>
        routes.MapGet("/who", (HttpContext http) => PluginUser.Id(http));

    public void MapPublic(IEndpointRouteBuilder routes, PluginContext context) =>
        routes.MapGet("/ping", () => "pong");

    public string? PublicHostPath(HttpContext http, PluginContext context) =>
        string.Equals(http.Request.Host.Host, "hilfe.sample.test", StringComparison.OrdinalIgnoreCase) ? "/ping" : null;

    public IReadOnlyList<SchemaStep> ServerSchema => [new(1, "CREATE TABLE sample_shared (id INTEGER PRIMARY KEY, who TEXT)")];

    public IReadOnlyList<SchemaStep> UserSchema =>
    [
        new(1, "CREATE TABLE sample_items (id INTEGER PRIMARY KEY, name TEXT NOT NULL)"),
        new(2, "INSERT INTO sample_items (name) VALUES ('eins')"),
    ];

    public IReadOnlyList<IExportArea> ExportAreas => [new Area()];

    private sealed class Area : IExportArea
    {
        public string Key => "sample";
        public LocalText Label => new("Beispiel", "Sample");
        public IEnumerable<ExportItem> Items(IUserData user)
        {
            var names = user.Query("SELECT name FROM sample_items ORDER BY id", r => r.GetString(0));
            yield return new ExportItem("liste.txt", async o => await o.WriteAsync(Encoding.UTF8.GetBytes(string.Join("\n", names) + "\n" + "two_words".Humanize())));
        }
    }
}
