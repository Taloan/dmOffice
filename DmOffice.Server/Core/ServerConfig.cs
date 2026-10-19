using System.Text.Json;

namespace DmOffice.Core;

/// <summary>Einstellungen fuer den ganzen Server (nicht je Benutzer) - data/server.json.</summary>
public sealed class ServerSettings
{
    /// <summary>Speicher fuer Dateien (WebDAV, Dateien-Ansicht, notizi-Abgleich) je Benutzer in MB. 0: unbegrenzt.</summary>
    public long DefaultFileQuotaMb { get; set; }
}

public sealed class ServerConfig
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _path;
    private readonly object _lock = new();
    private ServerSettings _settings;

    public ServerConfig(DataPaths paths)
    {
        _path = Path.Combine(paths.Root, "server.json");
        _settings = File.Exists(_path) ? JsonSerializer.Deserialize<ServerSettings>(File.ReadAllText(_path), Json) ?? new() : new();
    }

    public ServerSettings Current { get { lock (_lock) return JsonSerializer.Deserialize<ServerSettings>(JsonSerializer.Serialize(_settings))!; } }

    public void Update(Action<ServerSettings> change)
    {
        lock (_lock)
        {
            change(_settings);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_settings, Json));
            File.Move(tmp, _path, overwrite: true);
        }
    }

    /// <summary>Wirksame Grenze in Bytes, 0 = unbegrenzt: eigene des Benutzers oder die Vorgabe.</summary>
    public long FileQuotaBytes(UserRecord? u)
    {
        var mb = u?.FileQuotaMb ?? Current.DefaultFileQuotaMb;
        return Math.Max(0, mb) * 1024 * 1024;
    }
}
