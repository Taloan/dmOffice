using DmOffice.Core;
using Microsoft.Extensions.FileProviders;

namespace DmOffice.Plugins;

/// <summary>
/// S15: Liste der Plugins für Web und App (/api/plugins, dazu „plugins“ in /api/auth/state) und Verwaltung → Plugins
/// (/api/admin/plugins). Web-Teile und Sprachdateien unter /plugins/&lt;key&gt;/… - nur geladene, eingeschaltete Plugins.
/// </summary>
public static class PluginApi
{
    /// <summary>Positive Liste (docs/plugins-konzept.md 4.4): Schlüssel, Version, api-Stufe, an/aus; dazu Namen und Web-Einstieg.</summary>
    public static object[] List() => PluginHost.Loaded.Select(e => (object)new
    {
        key = e.Key,
        version = e.Manifest!.Version,
        api = e.Manifest.Api,
        on = PluginHost.On(e.Key),
        label = e.Manifest.Label,
        web = e.Manifest.Web != null && PluginHost.On(e.Key) ? $"/plugins/{e.Key}/{e.Manifest.Web.Entry}" : null,
        langs = PluginHost.On(e.Key) ? Langs(e) : [],
    }).ToArray();

    private static string[] Langs(PluginHost.Entry e)
    {
        var dir = Path.Combine(e.Directory, "lang");
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>().Order().ToArray() : [];
    }

    public sealed record SwitchBody(bool On);

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/plugins", () => List()).RequireAuthorization();

        var admin = app.MapGroup("/api/admin/plugins").RequireAuthorization("admin");
        admin.MapGet("", () => new
        {
            root = PluginHost.Root,
            level = DmOffice.Sdk.SdkInfo.Level,
            plugins = PluginHost.All.Select(e => new
            {
                key = e.Key,
                version = e.Manifest?.Version,
                api = e.Manifest?.Api,
                label = e.Manifest?.Label,
                description = e.Manifest?.Description,
                core = e.Manifest?.Core,
                requires = e.Manifest?.Requires ?? [],
                server = e.Manifest?.Server != null,
                web = e.Manifest?.Web != null,
                signer = e.Signer,
                loaded = e.Loaded,
                problem = e.Problem,
                on = PluginHost.On(e.Key),
            }),
        });
        admin.MapPut("/{key}", (string key, SwitchBody body, ServerConfig config) =>
        {
            if (PluginHost.All.FirstOrDefault(e => e.Key == key) is not { Loaded: true } entry) return Results.NotFound(new { error = "Dieses Plugin ist nicht geladen." });
            config.Update(s =>
            {
                s.Plugins ??= [];
                if (!s.Plugins.TryGetValue(key, out var p)) s.Plugins[key] = p = new PluginSetting();
                p.On = body.On;
            });
            return Results.Ok(new { on = PluginHost.On(key) });
        });
    }

    /// <summary>Statische Dateien eines Plugins: nur web/ und lang/, nur solange es geladen und an ist.</summary>
    public static void UseFiles(WebApplication app)
    {
        app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path.Value ?? "";
            if (!path.StartsWith("/plugins/", StringComparison.Ordinal)) { await next(); return; }
            var parts = path.Split('/', 4);
            if (parts.Length < 4 || !Features.On(Features.Plugins) || PluginHost.All.FirstOrDefault(e => e.Key == parts[2]) is not { Loaded: true } entry
                || !PluginHost.On(entry.Key) || !(parts[3].StartsWith("web/", StringComparison.Ordinal) || parts[3].StartsWith("lang/", StringComparison.Ordinal))
                || !PluginManifest.Inside(entry.Directory, parts[3]) || !File.Exists(Path.Combine(entry.Directory, parts[3])))
            {
                ctx.Response.StatusCode = 404;
                return;
            }
            var file = Path.Combine(entry.Directory, parts[3]);
            var types = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
            ctx.Response.ContentType = types.TryGetContentType(file, out var ct) ? ct : "application/octet-stream";
            // Version im Pfad gibt es nicht: kurz zwischenspeichern, damit ein neues Plugin nach dem Neustart bald ankommt
            ctx.Response.Headers.CacheControl = "no-cache";
            await ctx.Response.SendFileAsync(file);
        });
    }
}
