using DmOffice.Core;

namespace DmOffice.GateKeeper;

/// <summary>
/// /api/gatekeeper (Kennung gatekeeper, 2.2, docs/gatekeeper-konzept.md): eigene Regeln je Benutzer, Statistik, Tunnel für die
/// App; /api/admin/gatekeeper: Filterlisten und Einstellungen des Servers. Ein/Aus und die Auswahl der Apps bleiben auf dem
/// Handy: der Server erfährt weder installierte Apps noch Gerätenamen.
/// </summary>
public static class GateKeeperApi
{
    public record RuleBody(string Domain, string Action);
    public record AdminBody(int? RefreshHours, bool? AllowPrivate);
    public record ListBody(string? Url, string? Name, bool? Enabled);
    public record RefreshBody(long? Id);

    /// <summary>Höchstens so viele Tunnel gleichzeitig je Benutzer (mehrere Geräte).</summary>
    public const int MaxTunnelsPerUser = 5;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> Tunnels = new();

    /// <summary>
    /// Summen eines Zeitraums. traffic = durchgereichte Bytes; median = mittlere Größe einer Verbindung (Median);
    /// saved = gesperrt × median, eine Schätzung des eingesparten Datenverkehrs.
    /// </summary>
    private static object Totals(List<GkDay> days)
    {
        long blocked = days.Sum(d => d.Blocked), up = days.Sum(d => d.Up), down = days.Sum(d => d.Down);
        var median = GkSizes.Median(days.Aggregate((long[]?)null, (a, d) => GkSizes.Add(a, d.Sizes)));
        return new
        {
            connections = days.Sum(d => d.Connections), dns = days.Sum(d => d.Dns), blocked,
            up, down, traffic = up + down, median, saved = blocked * median,
        };
    }

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api/gatekeeper").RequireAuthorization();

        api.MapGet("", (HttpContext http, GateKeeperStore store, GateKeeperLists lists) =>
        {
            var me = http.Space().User.Id;
            var today = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            var days = store.Days(me, 30);
            return Results.Ok(new
            {
                rules = store.Rules(me),
                today = Totals(days.Where(d => d.Day == today).ToList()), month = Totals(days),
                top = store.Hits(me, "hits", 5),
                filter = new { lists = store.Lists().Count(l => l.Enabled), entries = lists.Current.Count },
            });
        });

        api.MapGet("/rules", (HttpContext http, GateKeeperStore store) => Results.Ok(store.Rules(http.Space().User.Id)));

        api.MapPost("/rules", (RuleBody body, HttpContext http, GateKeeperStore store) =>
            Results.Ok(store.SetRule(http.Space().User.Id, body.Domain ?? "", body.Action ?? "")));

        api.MapDelete("/rules", (string domain, HttpContext http, GateKeeperStore store) =>
        {
            store.DeleteRule(http.Space().User.Id, domain);
            return Results.NoContent();
        });

        api.MapGet("/stats", (HttpContext http, GateKeeperStore store, GateKeeperStats stats, string? order, int? limit, int? days) =>
        {
            stats.Flush();
            var me = http.Space().User.Id;
            var list = store.Days(me, Math.Clamp(days ?? 30, 1, GateKeeperStore.KeepDays));
            return Results.Ok(new { days = list, totals = Totals(list), hits = store.Hits(me, order ?? "last", limit ?? 200) });
        });

        api.MapDelete("/stats", (HttpContext http, GateKeeperStore store, GateKeeperStats stats) =>
        {
            stats.Flush();
            store.ClearStats(http.Space().User.Id);
            return Results.NoContent();
        });

        // Eine Adresse prüfen: wäre sie gesperrt, und wodurch?
        api.MapGet("/check", (string domain, HttpContext http, GateKeeperStore store, GateKeeperLists lists) =>
        {
            var name = GateKeeperFilter.Normalize(domain) ?? throw new ArgumentException(GateKeeperStore.BadAddress);
            var rules = store.RulesOf(http.Space().User.Id);
            var v = GateKeeperFilter.Check(name, lists.Current, rules.Allow, rules.Block);
            return Results.Ok(new { domain = name, blocked = v.Blocked, source = v.Source });
        });

        // Tunnel der App (WebSocket). Nur Geräte (Ticket aus dem Umschlag), nicht das Browser-Cookie: ein WebSocket ist ein GET
        // und fiele nicht unter den Schutz gegen Anfragen fremder Seiten.
        api.MapGet("/tunnel", async (HttpContext http, GateKeeperStore store, GateKeeperLists lists, GateKeeperStats stats, UserStore users, ILoggerFactory logs) =>
        {
            if (!http.WebSockets.IsWebSocketRequest) return Results.BadRequest(new { error = "Nur als WebSocket." });
            if (!http.User.HasClaim(c => c.Type == Auth.DeviceClaim)) return Results.StatusCode(403);
            if (http.Request.Query["v"] != GateKeeperProtocol.Version.ToString(System.Globalization.CultureInfo.InvariantCulture))
                return Results.BadRequest(new { error = "Unbekannte Version des Tunnels." });
            var me = http.Space().User.Id;
            var count = Tunnels.AddOrUpdate(me, 1, (_, n) => n + 1);
            try
            {
                if (count > MaxTunnelsPerUser) return Results.StatusCode(429);
                using var ws = await http.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext { KeepAliveInterval = TimeSpan.FromSeconds(30) });
                var stamp = users.ById(me)?.Stamp;
                var options = new GkTunnelOptions
                {
                    AllowPrivate = store.AllowPrivate,
                    DnsUpstream = GateKeeperDns.Default,
                    Ipv6 = () => GateKeeperIpv6.Available,
                    Check = name =>
                    {
                        var rules = store.RulesOf(me);
                        return GateKeeperFilter.Check(name, lists.Current, rules.Allow, rules.Block);
                    },
                    OnConnection = () => stats.Connection(me),
                    OnDns = () => stats.Dns(me),
                    OnBlocked = domain => stats.Blocked(me, domain),
                    OnTraffic = (up, down) => stats.Traffic(me, up, down),
                    // Konto gesperrt, Passwort geändert, abgemeldet, GateKeeper auf dem Server abgeschaltet: Tunnel endet
                    StillAllowed = () => Features.On(Features.GateKeeper) && users.ById(me) is { LockedOut: false } u && u.Stamp == stamp,
                };
                await new GateKeeperTunnel(ws, options, logs.CreateLogger("GateKeeper")).RunAsync(http.RequestAborted);
                return Results.Empty;
            }
            finally { Tunnels.AddOrUpdate(me, 0, (_, n) => Math.Max(0, n - 1)); }
        });

        var admin = app.MapGroup("/api/admin/gatekeeper").RequireAuthorization("admin");

        object AdminView(GateKeeperStore store, GateKeeperLists lists) => new
        {
            lists = store.Lists(), refreshHours = store.RefreshHours, allowPrivate = store.AllowPrivate,
            entries = lists.Current.Count, dns = GateKeeperDns.Default != null,
        };

        admin.MapGet("", (GateKeeperStore store, GateKeeperLists lists) => Results.Ok(AdminView(store, lists)));

        // Alle Personen zusammen: nur Zahlen je Tag, keine Domains (die sieht jede Person nur für sich)
        admin.MapGet("/stats", (GateKeeperStats stats, GateKeeperStore store, int? days) =>
        {
            stats.Flush();
            var n = Math.Clamp(days ?? 30, 1, GateKeeperStore.KeepDays);
            var all = store.AllDays(n);
            var today = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            var list = all.Select(x => x.Day).ToList();
            return Results.Ok(new
            {
                days = all.Select(x => new { x.Day.Day, x.Day.Connections, x.Day.Dns, x.Day.Blocked, x.Day.Up, x.Day.Down, people = x.People }),
                today = Totals(list.Where(d => d.Day == today).ToList()), totals = Totals(list),
                peopleToday = all.Where(x => x.Day.Day == today).Sum(x => x.People), people = store.People(n),
            });
        });

        admin.MapPut("", (AdminBody body, GateKeeperStore store, GateKeeperLists lists) =>
        {
            if (body.RefreshHours is { } h) store.RefreshHours = h;
            if (body.AllowPrivate is { } p) store.AllowPrivate = p;
            return Results.Ok(AdminView(store, lists));
        });

        admin.MapPost("/lists", async (ListBody body, GateKeeperStore store, GateKeeperLists lists) =>
        {
            var l = store.AddList(body.Url ?? "", body.Name);
            // Nicht am Abbruch der Anfrage hängen (Seite geschlossen): sonst bliebe die Liste ungeladen bis zum nächsten Durchgang
            await lists.RefreshAsync(l.Id, CancellationToken.None);
            return Results.Ok(AdminView(store, lists));
        });

        admin.MapPut("/lists/{id:long}", async (long id, ListBody body, GateKeeperStore store, GateKeeperLists lists) =>
        {
            store.UpdateList(id, body.Name, body.Enabled, body.Url);
            // Eingeschaltet, aber noch nie (oder unter anderer Adresse) geladen: gleich laden
            if (store.List(id) is { Enabled: true } l && (l.Fetched == null || body.Url != null)) await lists.RefreshAsync(id, CancellationToken.None);
            else lists.Rebuild();
            return Results.Ok(AdminView(store, lists));
        });

        admin.MapDelete("/lists/{id:long}", (long id, GateKeeperStore store, GateKeeperLists lists) =>
        {
            store.DeleteList(id);
            lists.Remove(id);
            return Results.Ok(AdminView(store, lists));
        });

        // Jetzt laden: eine Liste oder alle eingeschalteten
        admin.MapPost("/refresh", async (RefreshBody? body, GateKeeperStore store, GateKeeperLists lists, CancellationToken ct) =>
        {
            await lists.RefreshAsync(body?.Id, ct);
            return Results.Ok(AdminView(store, lists));
        });
    }
}
