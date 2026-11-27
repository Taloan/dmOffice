using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DmOffice.Sdk;

namespace DmOffice.Plugins;

/// <summary>
/// plugins/&lt;key&gt;/plugin.json (docs/plugins-konzept.md 4.1): Schlüssel, Version, api-Stufe (REST für App und Web), Namen,
/// passende Stufe der Kern-Schnittstelle („core“), benötigte Kernbereiche, Server- und Web-Teil.
/// </summary>
public sealed partial record PluginManifest
{
    public string Key { get; init; } = "";
    public string Version { get; init; } = "";
    public int Api { get; init; } = 1;
    public Dictionary<string, string> Label { get; init; } = [];
    public Dictionary<string, string> Description { get; init; } = [];
    public string Core { get; init; } = "";
    public List<string> Requires { get; init; } = [];
    public ServerPart? Server { get; init; }
    public WebPart? Web { get; init; }
    public bool AutoInstall { get; init; }

    public sealed record ServerPart(string Assembly);
    public sealed record WebPart(string Entry);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    public string LabelIn(string lang) => Label.GetValueOrDefault(lang) ?? Label.GetValueOrDefault("de") ?? Key;
    public string DescriptionIn(string lang) => Description.GetValueOrDefault(lang) ?? Description.GetValueOrDefault("de") ?? "";

    [GeneratedRegex(@"^[a-z][a-z0-9]{1,30}$")] private static partial Regex KeyRx();
    [GeneratedRegex(@"^\d+\.\d+\.\d+$")] private static partial Regex VersionRx();

    /// <summary>Liest und prüft plugin.json; wirft mit einem Satz für die Verwaltung.</summary>
    public static PluginManifest Read(string dir)
    {
        var path = Path.Combine(dir, "plugin.json");
        PluginManifest m;
        try { m = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("plugin.json ist leer."); }
        catch (JsonException e) { throw new InvalidDataException("plugin.json ist kein gültiges JSON: " + e.Message); }
        if (!KeyRx().IsMatch(m.Key)) throw new InvalidDataException("Schlüssel fehlt oder ist ungültig (Kleinbuchstaben und Ziffern).");
        if (!string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(dir)), m.Key, StringComparison.Ordinal))
            throw new InvalidDataException($"Der Ordner heißt nicht wie der Schlüssel „{m.Key}“.");
        if (!VersionRx().IsMatch(m.Version)) throw new InvalidDataException("Version fehlt oder hat nicht die Form 1.2.3.");
        if (m.Api < 1) throw new InvalidDataException("api-Stufe muss mindestens 1 sein.");
        if (m.Server == null && m.Web == null) throw new InvalidDataException("Weder Server- noch Web-Teil angegeben.");
        foreach (var rel in new[] { m.Server?.Assembly, m.Web?.Entry }.OfType<string>())
            if (!Inside(dir, rel)) throw new InvalidDataException($"Pfad „{rel}“ liegt nicht im Plugin-Ordner.");
        return m;
    }

    /// <summary>Liegt der relative Pfad sicher im Ordner (kein „..“, nicht absolut)?</summary>
    public static bool Inside(string dir, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains('\\')) return false;
        var full = Path.GetFullPath(Path.Combine(dir, relative));
        var root = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.Ordinal);
    }

    /// <summary>Passt die Stufe der Kern-Schnittstelle zum Bereich „core“ (">=1 &lt;2", "1", ">=1")? Leer: nein.</summary>
    public static bool CoreFits(string range, int level = SdkInfo.Level)
    {
        var parts = range.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        foreach (var p in parts)
        {
            var m = Regex.Match(p, @"^(>=|<=|>|<|=)?(\d+)$");
            if (!m.Success) return false;
            var n = int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            var ok = m.Groups[1].Value switch { ">=" => level >= n, "<=" => level <= n, ">" => level > n, "<" => level < n, _ => level == n };
            if (!ok) return false;
        }
        return true;
    }
}
