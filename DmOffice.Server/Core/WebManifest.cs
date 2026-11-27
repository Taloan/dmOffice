using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DmOffice.Core;

/// <summary>
/// Manifest der installierbaren App (wwwroot/manifest.webmanifest). Mit Kennung linkhandler meldet sich die installierte App
/// zusätzlich als Programm für E-Mail-Links (mailto:) und Kalender-Abos (webcal:) an. webcals: lässt der Browser nicht zu -
/// erlaubt sind nur „web+…“ und eine feste Liste von Schemata.
/// </summary>
public static class WebManifest
{
    /// <summary>Ziel der Links: Die Oberfläche liest ?handle= beim Start (web/src/linkhandler.ts).</summary>
    public const string HandleUrl = "/?handle=%s";

    private static readonly JsonSerializerOptions Out = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Manifest mit protocol_handlers für mailto und webcal.</summary>
    public static string WithProtocolHandlers(string json)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        root["protocol_handlers"] = new JsonArray(
            new JsonObject { ["protocol"] = "mailto", ["url"] = HandleUrl },
            new JsonObject { ["protocol"] = "webcal", ["url"] = HandleUrl });
        return root.ToJsonString(Out);
    }

    /// <summary>Liefert das Manifest aus - mit protocol_handlers nur, wenn linkhandler an ist; sonst die Datei wie sie ist.</summary>
    public static void Use(WebApplication app)
    {
        app.Use(async (ctx, next) =>
        {
            if (!Features.On(Features.LinkHandler) || ctx.Request.Path.Value != "/manifest.webmanifest"
                || !(HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method)))
            {
                await next();
                return;
            }
            var file = Path.Combine(app.Environment.WebRootPath ?? "wwwroot", "manifest.webmanifest");
            if (!File.Exists(file)) { await next(); return; }
            var body = WithProtocolHandlers(await File.ReadAllTextAsync(file, ctx.RequestAborted));
            ctx.Response.ContentType = "application/manifest+json";
            // Frisch wie die Datei selbst: daran erkennt der Browser eine geänderte App
            ctx.Response.Headers.CacheControl = "no-cache";
            if (HttpMethods.IsHead(ctx.Request.Method)) return;
            await ctx.Response.WriteAsync(body, ctx.RequestAborted);
        });
    }
}
