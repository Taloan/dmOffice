using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using DmOffice.Core;
using DmOffice.Sdk;
using Microsoft.Data.Sqlite;

namespace DmOffice.Plugins;

/// <summary>
/// Plugins im Serverprozess (docs/plugins-konzept.md 3, 4.2): beim Start plugins/*/plugin.json lesen, Siegel und Kern-Stufe
/// prüfen, die Assembly in den Standard-Ladekontext laden und den Einstieg (IDmOfficePlugin) anlegen. Ein fehlender Ordner ist
/// ein fehlendes Plugin. Ausgeschaltet (Verwaltung → Plugins) bleibt ein Plugin geladen; seine Routen antworten mit 404, seine
/// Jobs ruhen. Nur mit der Kennung „plugins“ - ohne sie liest der Kern keinen Plugin-Ordner und verhält sich wie zuvor.
/// </summary>
public static class PluginHost
{
    /// <summary>Ein gefundenes Plugin: Manifest (null, wenn unlesbar), Ordner, Zustand und Grund, warum es nicht läuft.</summary>
    public sealed class Entry
    {
        public required string Key { get; init; }
        public required string Directory { get; init; }
        public PluginManifest? Manifest { get; init; }
        public IDmOfficePlugin? Instance { get; set; }
        public PluginContext? Context { get; set; }
        /// <summary>Wer gesiegelt hat (dmOffice oder Betreiber), null ohne gültiges Siegel.</summary>
        public string? Signer { get; set; }
        /// <summary>Grund, warum das Plugin nicht geladen ist; null: geladen.</summary>
        public string? Problem { get; set; }
        public bool Loaded => Problem == null;
    }

    private static readonly List<Entry> _all = [];
    private static readonly Dictionary<string, AssemblyDependencyResolver> Resolvers = new(StringComparer.Ordinal);
    private static Func<ServerSettings>? _settings;

    public static IReadOnlyList<Entry> All => _all;
    public static IEnumerable<Entry> Loaded => _all.Where(e => e.Loaded);

    /// <summary>Ordner der Plugins: DMOFFICE_PLUGINS, sonst „plugins“ neben dem Programm.</summary>
    public static string Root => Environment.GetEnvironmentVariable("DMOFFICE_PLUGINS") is { Length: > 0 } dir ? dir : Path.Combine(AppContext.BaseDirectory, "plugins");

    /// <summary>Kernbereiche, die ein Plugin unter „requires“ nennen kann (dazu jede Kennung aus features.json).</summary>
    public static readonly string[] CoreAreas = ["mail", "chat", "pukki", "calendar", "contacts", "tasks", "notes", "files", "gallery", "audio", "dms", "vault", "overview"];

    /// <summary>
    /// Ist das Plugin eingeschaltet? Ohne Eintrag in den Einstellungen gilt autoInstall aus plugin.json. Setzt es andere Plugins
    /// voraus („requires“), müssen auch diese an sein.
    /// </summary>
    public static bool On(string key) => On(key, []);

    /// <summary><paramref name="seen"/>: schon gefragte Schlüssel - ein Ring von Voraussetzungen endet hier, statt endlos zu fragen.</summary>
    private static bool On(string key, HashSet<string> seen)
    {
        if (!Features.On(Features.Plugins)) return false;
        if (!seen.Add(key)) return true;
        var e = _all.FirstOrDefault(x => x.Key == key);
        if (e is not { Loaded: true }) return false;
        var s = _settings?.Invoke().Plugins;
        var self = s != null && s.TryGetValue(key, out var p) && p.On is bool on ? on : e.Manifest!.AutoInstall;
        return self;
    }

    /// <summary>Für Tests: Zustand zurücksetzen.</summary>
    internal static void Reset() { _all.Clear(); Resolvers.Clear(); _settings = null; UserDb.PluginSteps = null; TresorDb.PluginSteps = null; }

    /// <summary>Für Tests: Einstellungen ohne ServerConfig.</summary>
    internal static void UseSettings(Func<ServerSettings> settings) => _settings = settings;

    /// <summary>
    /// Plugins suchen und laden (vor builder.Build(), damit ConfigureServices greift). Ohne Kennung „plugins“ geschieht nichts.
    /// </summary>
    public static void Discover(IServiceCollection services, ILogger? log = null, string? root = null, IReadOnlyList<(string Name, byte[] Spki)>? keys = null)
    {
        if (!Features.On(Features.Plugins)) return;
        root ??= Root;
        if (!System.IO.Directory.Exists(root)) return;
        keys ??= PluginSeal.TrustedKeys();
        AssemblyLoadContext.Default.Resolving -= Resolve;
        AssemblyLoadContext.Default.Resolving += Resolve;
        foreach (var dir in System.IO.Directory.GetDirectories(root).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith('.')) continue;
            PluginManifest? m = null;
            Entry entry;
            try { m = PluginManifest.Read(dir); entry = new Entry { Key = m.Key, Directory = dir, Manifest = m }; }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                _all.Add(new Entry { Key = name, Directory = dir, Problem = ex.Message });
                continue;
            }
            _all.Add(entry);
            entry.Problem = Check(entry, keys);
            if (entry.Problem != null) { log?.LogWarning("Plugin {Key} nicht geladen: {Reason}", entry.Key, entry.Problem); continue; }
            try { Load(entry, services); }
            catch (Exception ex)
            {
                entry.Problem = "Laden fehlgeschlagen: " + ex.Message;
                log?.LogError(ex, "Plugin {Key} nicht geladen", entry.Key);
            }
        }
        if (Loaded.Any(e => e.Instance != null))
        {
            UserDb.PluginSteps = PluginSchemas.ApplyUser;
            TresorDb.PluginSteps = PluginSchemas.ApplyTresor;
        }
    }


    private static string? Check(Entry e, IReadOnlyList<(string Name, byte[] Spki)> keys)
    {
        var m = e.Manifest!;
        if (_all.Count(x => x.Key == m.Key) > 1) return "Schlüssel doppelt vergeben.";
        if (!PluginManifest.CoreFits(m.Core)) return $"Passt nicht zu dieser Kern-Schnittstelle (Stufe {SdkInfo.Level}, Plugin braucht „{m.Core}“).";
        foreach (var r in m.Requires)
        {
            if (!CoreAreas.Contains(r) && !Features.Known(r))
                return $"Braucht „{r}“, das es hier nicht gibt.";
            else if (Features.Known(r) && !Features.InRelease(r)) return $"Braucht „{r}“, das in dieser Version nicht freigeschaltet ist.";
        }
        // Server-Teile laufen mit allen Rechten im Prozess: nur mit gültigem Siegel. Reine Web-Teile ebenso - sie laufen in der
        // Sitzung der Person
        var seal = PluginSeal.Verify(e.Directory, m, keys);
        if (!seal.Valid) return seal.Reason;
        e.Signer = seal.Signer;
        if (m.Server != null && !File.Exists(Path.Combine(e.Directory, m.Server.Assembly))) return "Server-Teil fehlt: " + m.Server.Assembly;
        if (m.Web != null && !File.Exists(Path.Combine(e.Directory, m.Web.Entry))) return "Web-Teil fehlt: " + m.Web.Entry;
        return null;
    }

    private static void Load(Entry e, IServiceCollection services)
    {
        var m = e.Manifest!;
        e.Context = new PluginContext(m.Key, m.Version, e.Directory, () => On(m.Key), () => SettingsOf(m.Key));
        if (m.Server == null) return;
        var path = Path.GetFullPath(Path.Combine(e.Directory, m.Server.Assembly));
        Resolvers[path] = new AssemblyDependencyResolver(path);
        var asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
        var types = asm.GetExportedTypes().Where(t => typeof(IDmOfficePlugin).IsAssignableFrom(t) && t is { IsClass: true, IsAbstract: false }).ToList();
        if (types.Count != 1) throw new InvalidDataException($"Erwartet genau eine Klasse mit IDmOfficePlugin, gefunden {types.Count}.");
        e.Instance = (IDmOfficePlugin)Activator.CreateInstance(types[0])!;
        e.Instance.ConfigureServices(services, e.Context);
    }

    /// <summary>Abhängigkeiten eines Plugins (NuGet-Pakete neben seiner DLL) - was der Kern selbst mitbringt, nimmt er vorher.</summary>
    private static Assembly? Resolve(AssemblyLoadContext ctx, AssemblyName name)
    {
        foreach (var r in Resolvers.Values)
            if (r.ResolveAssemblyToPath(name) is { } p && File.Exists(p)) return ctx.LoadFromAssemblyPath(p);
        return null;
    }

    private static JsonElement? SettingsOf(string key) =>
        _settings?.Invoke().Plugins is { } s && s.TryGetValue(key, out var p) ? p.Settings : null;

    /// <summary>Nach builder.Build(): Einstellungen anbinden, Routen der geladenen Plugins anlegen (Sperre bei „aus“).</summary>
    public static void Map(WebApplication app)
    {
        if (app.Services.GetService<ServerConfig>() is { } config) _settings = () => config.Current;
        foreach (var e in Loaded.Where(e => e.Instance != null))
        {
            var key = e.Key;
            // Nur mit Anmeldung (wie die Module des Kerns); Wege ohne Anmeldung nur über MapPublic unter /p/<key>
            var group = app.MapGroup("").RequireAuthorization();
            group.AddEndpointFilter(async (ctx, next) => On(key) ? await next(ctx) : Results.NotFound());
            // Wege mit Anmeldung nur unter /api/<key>; was ein Plugin anderswo anlegt, kommt dorthin und antwortet 404
            var outside = 0;
            ((IEndpointConventionBuilder)group).Finally(b =>
            {
                if (b is not Microsoft.AspNetCore.Routing.RouteEndpointBuilder rb || OwnRoute(Text(rb.RoutePattern), key)) return;
                var n = Interlocked.Increment(ref outside);
                app.Logger.LogWarning("Plugin {Key}: Weg {Route} liegt außerhalb des eigenen Bereichs unter /api und bleibt gesperrt", key, Text(rb.RoutePattern));
                rb.RoutePattern = Microsoft.AspNetCore.Routing.Patterns.RoutePatternFactory.Parse($"/api/{key}/-/outside/{n}");
                rb.RequestDelegate = ctx => { ctx.Response.StatusCode = StatusCodes.Status404NotFound; return Task.CompletedTask; };
            });
            var open = app.MapGroup("/p/" + key).RequireRateLimiting("public");
            open.AddEndpointFilter(async (ctx, next) => On(key) ? await next(ctx) : Results.NotFound());
            // Wege nur für Admins: eigene Gruppe unter /api/<key>/admin mit Admin-Rolle
            var admin = app.MapGroup("/api/" + key + "/admin").RequireAuthorization("admin");
            admin.AddEndpointFilter(async (ctx, next) => On(key) ? await next(ctx) : Results.NotFound());
            try { e.Instance!.Map(group, e.Context!); e.Instance!.MapPublic(open, e.Context!); e.Instance!.MapAdmin(admin, e.Context!); }
            catch (Exception ex)
            {
                e.Problem = "Routen fehlgeschlagen: " + ex.Message;
                app.Logger.LogError(ex, "Plugin {Key}: Routen nicht angelegt", key);
            }
        }
    }

    /// <summary>S11: Weg unter /p/&lt;key&gt; für einen eigenen Rechnernamen einer öffentlichen Seite; null, wenn kein eingeschaltetes Plugin ihn beansprucht.</summary>
    public static string? HostPath(HttpContext http)
    {
        if (http.Request.Host.Host.Length == 0) return null;
        foreach (var e in Loaded)
        {
            if (e.Instance == null || !On(e.Key)) continue;
            string? p;
            try { p = e.Instance.PublicHostPath(http, e.Context!); }
            catch { continue; }
            if (p is { Length: > 0 } && p[0] == '/' && !p.Contains("..")) return "/p/" + e.Key + p.TrimEnd('/');
        }
        return null;
    }

    /// <summary>S11: Anfragen an eigene Rechnernamen auf den Weg des Plugins umschreiben (vor UseRouting); /api, /p und Dateien der Oberfläche bleiben.</summary>
    public static void UseHosts(WebApplication app)
    {
        var web = app.Environment.WebRootFileProvider;
        app.Use(async (ctx, next) =>
        {
            var req = ctx.Request;
            if (!req.Path.StartsWithSegments("/api") && !req.Path.StartsWithSegments("/p") && HostPath(ctx) is { } to
                && (req.Path.Value is null or "/" || !web.GetFileInfo(req.Path.Value).Exists))
                req.Path = to + (req.Path.Value is null or "/" ? "" : req.Path.Value);
            await next();
        });
    }

    private static string Text(Microsoft.AspNetCore.Routing.Patterns.RoutePattern p) => p.RawText ?? "/" + string.Join("/", p.PathSegments.Select(seg =>
        string.Concat(seg.Parts.Select(part => part is Microsoft.AspNetCore.Routing.Patterns.RoutePatternLiteralPart l ? l.Content : "{}"))));

    /// <summary>Liegt der Weg unter /api/&lt;key&gt;?</summary>
    internal static bool OwnRoute(string? pattern, string key)
    {
        var p = "/" + (pattern ?? "").TrimStart('/');
        var own = "/api/" + key;
        return p.StartsWith(own, StringComparison.OrdinalIgnoreCase) && (p.Length == own.Length || p[own.Length] == '/');
    }

    /// <summary>Bereiche im Datenexport aller eingeschalteten Plugins.</summary>
    public static IEnumerable<(Entry Plugin, IExportArea Area)> ExportAreas() =>
        Loaded.Where(e => e.Instance != null && On(e.Key)).SelectMany(e => SafeAreas(e).Select(a => (e, a)));

    private static IReadOnlyList<IExportArea> SafeAreas(Entry e)
    {
        try { return e.Instance!.ExportAreas; } catch (Exception) { return []; }
    }
}

/// <summary>Einstellungen eines Plugins in ServerSettings.Plugins: an/aus (null: autoInstall) und eigene Werte (S5).</summary>
public sealed class PluginSetting
{
    public bool? On { get; set; }
    public JsonElement? Settings { get; set; }
}

/// <summary>
/// S4: Schema je Plugin (docs/plugins-konzept.md 7.7). Stand in plugin_schema (key, version) je Benutzer- und Tresor-Datenbank,
/// angelegt erst, wenn ein Plugin Schritte hat. Jeder Schritt samt Stand in einer Transaktion. Steht die Datenbank auf einem
/// neueren Stand als das Plugin kennt (Plugin zurückgesetzt), bleibt sie unverändert.
/// </summary>
public static class PluginSchemas
{
    private const string Table = "CREATE TABLE IF NOT EXISTS plugin_schema (key TEXT PRIMARY KEY, version INTEGER NOT NULL)";

    public static void ApplyUser(SqliteConnection conn, string userId) => Apply(conn, e => e.UserSchema);
    public static void ApplyTresor(SqliteConnection conn) => Apply(conn, e => e.TresorSchema);

    /// <summary>Höchster Stand eines Plugins in dieser Datenbank, null ohne Eintrag.</summary>
    public static int? VersionOf(SqliteConnection conn, string key) =>
        UserDb.Query(conn, "SELECT version FROM plugin_schema WHERE key=$k", r => (int?)r.GetInt32(0), ("$k", key)).FirstOrDefault();

    internal static void Apply(SqliteConnection conn, Func<IDmOfficePlugin, IReadOnlyList<SchemaStep>> steps)
    {
        foreach (var e in PluginHost.Loaded.Where(e => e.Instance != null))
        {
            var list = steps(e.Instance!).OrderBy(s => s.Version).ToList();
            if (list.Count == 0) continue;
            UserDb.Exec(conn, Table);
            var have = VersionOf(conn, e.Key) ?? 0;
            foreach (var step in list.Where(s => s.Version > have))
            {
                using var tx = conn.BeginTransaction();
                UserDb.Exec(conn, step.Sql);
                UserDb.Exec(conn, "INSERT INTO plugin_schema (key, version) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET version=excluded.version",
                    ("$k", e.Key), ("$v", step.Version));
                tx.Commit();
            }
        }
    }
}

/// <summary>Daten der Person für Plugins (IUserData) über die Benutzerdatenbank.</summary>
public sealed class UserDataAdapter(UserSpace space) : IUserData
{
    public string UserId => space.User.Id;
    public TimeZoneInfo Zone => space.Zone;
    public int Execute(string sql, params (string Name, object? Value)[] args) => space.Db.Exec(sql, args);
    public List<T> Query<T>(string sql, Func<System.Data.IDataRecord, T> read, params (string Name, object? Value)[] args) => space.Db.Query(sql, r => read(r), args);
    public object? Scalar(string sql, params (string Name, object? Value)[] args) => space.Db.Scalar(sql, args);
}

public sealed class UserDataAccessor : IUserDataAccessor
{
    public IUserData For(HttpContext http) => new UserDataAdapter(http.Space());
}
