using System.Diagnostics;
using System.Security.Claims;

namespace DmOffice.Core;

/// <summary>
/// Eine Protokollzeile je /api-Anfrage, die länger als 2 s dauert: Pfad ohne Query, Dauer, Kennung des Benutzers.
/// Kein Inhalt, keine Query (darin können Suchbegriffe oder Adressen stehen) - das Protokoll liest der Admin.
/// </summary>
public static class SlowRequests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(2);

    /// <summary>Warten mit Absicht (Long-Poll wie /api/chat/wait), und der verschlüsselte Umschlag /api/e2e/{benutzer}/{gerät}:
    /// die Anfrage darin läuft noch einmal durch die Kette und bekommt ihre eigene Zeile mit dem echten Pfad.</summary>
    internal static bool Skip(PathString path) =>
        path.Value is { } p && (p.EndsWith("/wait", StringComparison.Ordinal)
            || (path.StartsWithSegments("/api/e2e") && p.Count(c => c == '/') == 4 && !p.EndsWith("/register", StringComparison.Ordinal)));

    public static void Use(WebApplication app)
    {
        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DmOffice.SlowRequests");
        app.Use(async (ctx, next) =>
        {
            if (!ctx.Request.Path.StartsWithSegments("/api") || Skip(ctx.Request.Path)) { await next(); return; }
            var t = Stopwatch.StartNew();
            try { await next(); }
            finally
            {
                // Die Anmeldung kommt erst danach in der Kette; nach next() steht der Benutzer fest
                if (t.Elapsed > Limit)
                    log.LogWarning("Langsame Anfrage: {Method} {Path} {Ms} ms, Benutzer {User}, Status {Status}",
                        ctx.Request.Method, ctx.Request.Path.Value, (long)t.Elapsed.TotalMilliseconds,
                        ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "-", ctx.Response.StatusCode);
            }
        });
    }
}
