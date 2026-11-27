using System.Text;

namespace DmOffice.Core;

/// <summary>Verwaltung → Sicherheit: Übersicht, Sperrliste, Erlaubt-Liste, Ereignisse, Einstellungen des Security Centers.</summary>
public static class SecurityCenterApi
{
    public record BlockBody(string Address, int? Days, string? Note);
    public record AllowBody(string Cidr, string? Note);
    public record SettingsBody(bool Traps, int FirstDays, int SeenDays, int KeepDays, bool AbuseCheck = false, int AbuseScore = 90, int AbuseDays = 7, bool AbuseReport = false);
    public record KeyBody(string? Key);
    public record ProxyBody(string Address); // [feature:proxyconfirm]

    private static SecurityCenter Sc => SecurityCenter.Shared ?? throw new InvalidOperationException("Das Security Center ist abgeschaltet (DMOFFICE_SECURITY=off).");

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api/admin/security").RequireAuthorization("admin");

        api.MapGet("", (HttpContext http) =>
        {
            if (SecurityCenter.Shared is not { } sc) return Results.Ok(new { off = true });
            var now = DateTime.UtcNow;
            var ip = http.Connection.RemoteIpAddress;
            return Results.Ok(new
            {
                off = false,
                settings = sc.Current,
                day = sc.Counts(now.AddDays(-1)),
                week = sc.Counts(now.AddDays(-7)),
                top = sc.Top(now.AddDays(-7)).Select(x => new { key = x.Key, count = x.Count, last = x.Last }),
                blocks = sc.Blocks(),
                allow = sc.AllowList(),
                builtIn = SecurityCenter.BuiltIn,
                abuseKey = sc.AbuseKeySet,
                // Eigene Adresse, wie der Server sie sieht - zum Eintragen in die Erlaubt-Liste
                me = ip == null ? null : new { address = (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString(), allowed = sc.Allowed(ip) },
            });
        });

        api.MapGet("/events", (string? q, int? limit, long? before) => Sc.Events(q, limit ?? 500, before));

        // Ereignisse als CSV (Tabulator), etwa für eine Anzeige oder den Provider
        api.MapGet("/events.csv", (string? q) =>
        {
            var sb = new StringBuilder("Zeit (UTC)\tAdresse\tArt\tMethode\tPfad\tUser-Agent\tHinweis\n");
            foreach (var e in Sc.Events(q, 5000))
                sb.Append(e.At.ToString("yyyy-MM-dd HH:mm:ss")).Append('\t').Append(e.Key).Append('\t').Append(Clean(e.Kind)).Append('\t').Append(Clean(e.Method)).Append('\t')
                  .Append(Clean(e.Path)).Append('\t').Append(Clean(e.Agent)).Append('\t').Append(Clean(e.Detail)).Append('\n');
            return Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray(), "text/tab-separated-values; charset=utf-8",
                $"sicherheit-{DateTime.UtcNow:yyyy-MM-dd}.tsv");
        });

        // Von Hand sperren: Days leer = dauerhaft
        api.MapPost("/block", (BlockBody body, HttpContext http) =>
        {
            var net = SecurityCenter.ParseNet(body.Address);
            var key = AnonThrottle.KeyOf(net.BaseAddress) ?? throw new ArgumentException("Diese Adresse lässt sich nicht sperren.");
            if (Sc.Allowed(net.BaseAddress)) throw new ArgumentException("Die Adresse steht in der Erlaubt-Liste.");
            var b = Sc.BlockNow(key, "von Hand gesperrt", body.Days is > 0 ? DateTime.UtcNow.AddDays(body.Days.Value) : null, http.Space().User.Login, body.Note);
            Sc.Manual(key, http.Space().User.Login, body.Note);
            return Results.Ok(b);
        });
        api.MapDelete("/block", (string key, HttpContext http) => Sc.Unblock(key, http.Space().User.Login) ? Results.Ok() : Results.NotFound(new { error = "Diese Sperre gibt es nicht." }));

        api.MapPost("/allow", (AllowBody body) => { Sc.Allow(body.Cidr, body.Note); return Results.Ok(); });
        api.MapDelete("/allow", (string cidr) => { Sc.Disallow(cidr); return Results.Ok(); });

        api.MapPut("/settings", (SettingsBody body) =>
        {
            Sc.Save(new SecurityCenter.Settings(body.Traps, body.FirstDays, body.SeenDays, body.KeepDays, body.AbuseCheck, body.AbuseScore, body.AbuseDays, body.AbuseReport));
            return Results.Ok();
        });

        // AbuseIPDB: Schlüssel speichern (leer = entfernen) und prüfen - angezeigt wird er nie
        api.MapPut("/abuseipdb/key", (KeyBody body) => { Sc.SaveAbuseKey(body.Key); return Results.Ok(); });
        api.MapPost("/abuseipdb/verify", async (KeyBody body, CancellationToken ct) =>
            await Sc.VerifyAbuseKeyAsync(body.Key, ct) is { } error ? Results.BadRequest(new { error }) : Results.Ok());

        // [feature:proxyconfirm]
        // Reverse-Proxys: wer X-Forwarded-* schickt; bestätigte (und nach dem Update übernommene) gelten als Proxy
        api.MapGet("/proxies", (HttpContext http) =>
        {
            var env = Environment.GetEnvironmentVariable("DMOFFICE_TRUSTED_PROXIES");
            var pc = ProxyConfirm.Shared;
            // Über welchen Absender kam diese Anfrage (vom Forwarded-Headers-Schritt vermerkt)?
            var original = http.Request.Headers["X-Original-For"].FirstOrDefault();
            string? via = null;
            if (original != null && System.Net.IPEndPoint.TryParse(original, out var ep)) via = ProxyConfirm.Normal(ep.Address).ToString();
            return Results.Ok(new
            {
                active = pc != null,
                env = string.IsNullOrWhiteSpace(env) ? null : env,
                learnUntil = pc?.LearnUntil,
                learning = pc?.Learning(DateTime.UtcNow) == true,
                via,
                list = pc?.List() ?? [],
            });
        });
        api.MapPost("/proxies/confirm", (ProxyBody body, HttpContext http) =>
        {
            (ProxyConfirm.Shared ?? throw new InvalidOperationException("Die Liste der Reverse-Proxys ist nicht aktiv.")).Confirm(body.Address, http.Space().User.Login);
            return Results.Ok();
        });
        api.MapDelete("/proxies", (string address) =>
            ProxyConfirm.Shared?.Remove(address) == true ? Results.Ok() : Results.NotFound(new { error = "Diesen Absender gibt es nicht in der Liste." }));
        // [/feature:proxyconfirm]
    }

    /// <summary>Eine Zelle der TSV-Datei: ohne Tabulator und Zeilenumbruch, und was ein Tabellenprogramm als Formel liest
    /// (=, +, -, @ am Anfang), mit vorangestelltem ' als Text (wie CsvCell in den Finanzen).</summary>
    internal static string Clean(string? s)
    {
        var text = (s ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
        return text.Length > 0 && "=+-@".Contains(text[0]) ? "'" + text : text;
    }
}
