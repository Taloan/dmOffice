using System.Text.Json;
using System.Text.RegularExpressions;

namespace DmOffice.Core;

/// <summary>
/// Release-Liste: welche Bereiche und Funktionen in dieser Version freigeschaltet sind. features.json (neben der Assembly)
/// nennt je Kennung die Version, ab der sie gilt, oder „aus“ (in keiner Version); release.txt die Version dieses Stands.
/// Kennungen, die nicht in der Liste stehen, gelten immer. Testserver schalten mit DMOFFICE_FEATURES mehr oder weniger
/// frei: „all“ (alles) oder eine Liste wie „+vault,-dms“.
/// Nicht Freigeschaltetes ist nur inaktiv: Die Pfade der API antworten mit 404, Web und App blenden es aus
/// (/api/auth/state: featuresOff), Hintergrundarbeit und Querverbindungen fragen <see cref="On"/>.
/// Dazu die Hauptschalter der Module (<see cref="Modules"/>): Was der Admin ausschaltet (ServerSettings.ModulesOff), gilt
/// genauso als aus.
/// </summary>
public static class Features
{
    public const string Vault = "vault", ImportProfiles = "importprofiles", Audio = "audio", Gallery = "gallery", Notes = "notes",
        Tasks = "tasks", Office = "office", Dms = "dms";

    /// <summary>
    /// Zusatzmodule, die der Admin serverweit aus- und einschaltet (Reihenfolge wie in der Leiste). Die Grundausstattung
    /// (Mail, Kalender, Kontakte, Aufgaben, Notizen, Dateien, Galerie, Chat) ist nicht abschaltbar - andere Bereiche bauen
    /// darauf auf. Früher gespeicherte Schalter dafür zählen nicht mehr (SetModulesOff).
    /// </summary>
    public static readonly string[] Modules = [Audio, Dms];

    private static HashSet<string> _off = Load();
    // Vom Admin ausgeschaltete Module (ServerSettings.ModulesOff), gesetzt in Use und bei jeder Änderung der Einstellungen
    private static HashSet<string> _modulesOff = new();

    /// <summary>Ist die Funktion in dieser Version freigeschaltet und (bei Modulen) vom Admin eingeschaltet?</summary>
    public static bool On(string id) => !_off.Contains(id) && !_modulesOff.Contains(id);

    /// <summary>Ist die Funktion in dieser Version freigeschaltet (Hauptschalter des Admins außer Acht)?</summary>
    public static bool InRelease(string id) => !_off.Contains(id);

    /// <summary>
    /// Kennungen von Funktionen, die dieser Stand gar nicht enthält. Clients, die sie kennen (die App), blenden sie nur aus, wenn
    /// sie in <see cref="Off"/> stehen - darum meldet der Server sie immer als aus.
    /// </summary>
    internal static readonly string[] NotIncluded = ["journal", "coach", "emergency", "catchall", "mailexpiry", "pgp", "clamav", "filecats", "faces", "finance"];

    /// <summary>Die nicht freigeschalteten, ausgeschalteten oder nicht enthaltenen Kennungen (für Web und App).</summary>
    public static IReadOnlyCollection<string> Off => _off.Union(_modulesOff).Union(NotIncluded).ToList();

    /// <summary>Nur für Tests: die nicht freigeschalteten Kennungen ohne die vom Admin ausgeschalteten Module.</summary>
    internal static IReadOnlyCollection<string> InReleaseOff => _off;

    /// <summary>Nur für Tests: Liste neu festlegen.</summary>
    internal static void Set(IEnumerable<string> off) => _off = off.ToHashSet();

    /// <summary>Vom Admin ausgeschaltete Module übernehmen (unbekannte Kennungen zählen nicht).</summary>
    internal static void SetModulesOff(IEnumerable<string>? ids) => _modulesOff = (ids ?? []).Where(Modules.Contains).ToHashSet();

    private static HashSet<string> Load()
    {
        var dir = AppContext.BaseDirectory;
        string release;
        Dictionary<string, string> list;
        try { release = File.ReadAllText(Path.Combine(dir, "release.txt")).Trim(); }
        catch (IOException) { release = ""; }
        try { list = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(dir, "features.json"))) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException) { list = new(); }
        var off = Compute(release, list, Environment.GetEnvironmentVariable("DMOFFICE_FEATURES"));
        // Demo-Server: einiges ist dort immer aus (Tresor, Importprofile)
        if (Demo.Enabled) off.UnionWith(Demo.FeaturesOff);
        return off;
    }

    /// <summary>Nicht freigeschaltet: Version der Kennung über der dieses Stands oder „aus“, dann DMOFFICE_FEATURES.</summary>
    internal static HashSet<string> Compute(string release, Dictionary<string, string> list, string? env)
    {
        var off = list.Where(kv => !Released(kv.Value, release)).Select(kv => kv.Key).ToHashSet();
        foreach (var part in (env ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Equals("all", StringComparison.OrdinalIgnoreCase)) off.Clear();
            else if (part.StartsWith('+')) off.Remove(part[1..]);
            else if (part.StartsWith('-')) off.Add(part[1..]);
        }
        return off;
    }

    private static bool Released(string since, string release) =>
        Version.TryParse(since, out var s) && Version.TryParse(release, out var r) && s <= r;

    // Pfade der API je Kennung - wer nicht freigeschaltet ist, bekommt 404
    private static readonly (Regex Path, string Id)[] Paths =
    [
        (new(@"^/api/tresor(/|$)", RegexOptions.IgnoreCase), Vault),
        (new(@"^/api/gallery/imports(/|$)", RegexOptions.IgnoreCase), ImportProfiles),
        (new(@"^/api/dms(/|$)", RegexOptions.IgnoreCase), Dms),
        (new(@"^/api/mail/[^/]+/to-dms$", RegexOptions.IgnoreCase), Dms),
        (new(@"^/api/notes/(vault/|databases/[^/]+/(tresor|out)$)", RegexOptions.IgnoreCase), Vault),
        // Module (Hauptschalter des Admins). Ein Pfad kann zu mehreren Kennungen gehören - aus ist er, wenn eine davon aus ist.
        (new(@"^/api/audio(/|$)", RegexOptions.IgnoreCase), Audio),
        // Galerie: /api/gallery/info schreibt Bildangaben auch für die Dateien-Ansicht und bleibt erreichbar
        (new(@"^/api/(gallery(?!/info$)|public-album)(/|$)", RegexOptions.IgnoreCase), Gallery),
        (new(@"^/api/notes(/|$)", RegexOptions.IgnoreCase), Notes),
        (new(@"^/api/pim/(tasks|lists|list-people)(/|$)", RegexOptions.IgnoreCase), Tasks),
        // Office: nur die Editor-Anbindung - Öffnen und Herunterladen in den Dateien bleibt
        (new(@"^/(api/office|wopi|onlyoffice)(/|$)", RegexOptions.IgnoreCase), Office),
    ];

    /// <summary>Kennung, zu der ein Pfad der API gehört (oder null) - die erste passende.</summary>
    internal static string? OfPath(string path) => Paths.FirstOrDefault(p => p.Path.IsMatch(path)).Id;

    /// <summary>Gehört der Pfad zu einer Kennung, die aus ist?</summary>
    internal static bool Blocked(string path) => Paths.Any(p => !On(p.Id) && p.Path.IsMatch(path));

    public static void Use(WebApplication app)
    {
        // Hauptschalter der Module aus den Einstellungen, auch nach jeder Änderung
        var config = app.Services.GetRequiredService<ServerConfig>();
        SetModulesOff(config.Current.ModulesOff);
        config.Changed += () => SetModulesOff(config.Current.ModulesOff);
        app.Use(async (ctx, next) =>
        {
            if ((_off.Count > 0 || _modulesOff.Count > 0) && Blocked(ctx.Request.Path.Value ?? ""))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await next();
        });
    }
}
