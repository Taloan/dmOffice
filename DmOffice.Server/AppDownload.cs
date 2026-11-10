using System.Security.Cryptography;
using System.Text.Json;

namespace DmOffice;

/// <summary>
/// Android-App vom eigenen Server (Version 2): Beim Bau des Images kommt die App aus demselben Stand mit hinein
/// (/app/apk/dmOffice.apk und app.json mit Version und Stand). So passen App und Server immer zusammen; Tester in der Familie
/// bekommen sie über die Adresse des Servers, und die App holt ihre Updates von dort.
///   GET /app/dmoffice.apk   die App (ohne Anmeldung - Installieren kommt vor dem Anmelden)
///   GET /api/app/info       Version, versionCode, Größe, SHA-256 und ob sie zum Stand des Servers gehört
/// </summary>
public static class AppDownload
{
    /// <summary>Sha: Stand des App-Builds; Server: Stand des Images, zu dem der Build-Ablauf die App passend befand.</summary>
    private sealed record Info(string? Version, int VersionCode, string? Sha, string? Name, string? Server = null);

    private static string Dir => Path.Combine(AppContext.BaseDirectory, "apk");
    private static string ApkPath => Path.Combine(Dir, "dmOffice.apk");

    private static readonly Lazy<(Info Info, long Size, string Sha256)?> Current = new(() =>
    {
        if (!File.Exists(ApkPath)) return null;
        Info info;
        try { info = JsonSerializer.Deserialize<Info>(File.ReadAllText(Path.Combine(Dir, "app.json")), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new(null, 0, null, null, null); }
        catch (Exception e) when (e is IOException or JsonException) { info = new(null, 0, null, null); }
        using var s = File.OpenRead(ApkPath);
        return (info, s.Length, Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant());
    });

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/app/info", () =>
        {
            if (Current.Value is not { } c) return Results.Ok(new { available = false });
            // Der Stand des Servers steht hinten in seiner Version („v2-2026.10.23-18a5ea0“)
            var own = SettingsApi.Version();
            var matches = new[] { c.Info.Sha, c.Info.Server }.Any(x => x is { Length: >= 7 } && own.EndsWith(x, StringComparison.OrdinalIgnoreCase));
            return Results.Ok(new { available = true, version = c.Info.Version, versionCode = c.Info.VersionCode, size = c.Size, sha256 = c.Sha256, sha = c.Info.Sha, matches });
        }).AllowAnonymous();

        app.MapGet("/app/dmoffice.apk", () => Current.Value is { } c
            ? Results.File(ApkPath, "application/vnd.android.package-archive", c.Info.Name ?? "dmOffice.apk", enableRangeProcessing: true)
            : Results.NotFound()).AllowAnonymous();
    }
}
