using System.Text.Json;

namespace DmOffice.Core;

/// <summary>Einstellungen fuer den ganzen Server (nicht je Benutzer) - data/server.json.</summary>
public sealed class ServerSettings
{
    /// <summary>Speicher fuer Dateien (WebDAV, Dateien-Ansicht, notizi-Abgleich) je Benutzer in MB. 0: unbegrenzt.</summary>
    public long DefaultFileQuotaMb { get; set; }

    /// <summary>Office-Server (Collabora/CODE, ONLYOFFICE) fuer das Bearbeiten im Browser, etwa https://office.example.org.
    /// Leer: abgeschaltet. dmOffice liest dort /hosting/discovery.</summary>
    public string? OfficeUrl { get; set; }

    /// <summary>Adresse, unter der der Office-Server dmOffice erreicht (WOPISrc). Leer: dieselbe wie im Browser.</summary>
    public string? OfficeWopiBase { get; set; }

    /// <summary>JWT-Geheimnis des ONLYOFFICE Document Servers, mit dem Hauptschluessel eingepackt (Vault.Wrap).</summary>
    public string? OfficeJwtSecret { get; set; }

    /// <summary>Kopfzeile fuer das JWT (ONLYOFFICE: JWT_HEADER), Vorgabe Authorization.</summary>
    public string? OfficeJwtHeader { get; set; }

    /// <summary>Hoechstwerte fuer die Aufbewahrung, die Benutzer unter Einstellungen -> Dateien nicht ueberschreiten.</summary>
    public FileLimits FileLimits { get; set; } = new();

    /// <summary>Wie lange die Sicherungen vor einer Datenaenderung (pre-update) aufbewahrt werden, in Tagen. 0: nie loeschen.</summary>
    public int PreUpdateKeepDays { get; set; } = 14;
}

/// <summary>Hoechstwerte je Benutzer; 0 = keine Vorgabe (der Benutzer darf auch "unbegrenzt").</summary>
public sealed class FileLimits
{
    public int VersionsDays { get; set; }
    public int VersionsMb { get; set; }
    public int TrashDays { get; set; }
    public int TrashMb { get; set; }
    public int LinkMaxDownloads { get; set; }
    public int LinkDays { get; set; }
}

public sealed class ServerConfig
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _path;
    private readonly object _lock = new();
    private ServerSettings _settings;

    private readonly Vault? _vault;

    public ServerConfig(DataPaths paths, Vault? vault = null)
    {
        _vault = vault;
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

    /// <summary>Das JWT-Geheimnis des Office-Servers im Klartext, null wenn keins (oder nicht mehr auszupacken).</summary>
    public string? OfficeJwtSecret()
    {
        var wrapped = Current.OfficeJwtSecret;
        if (string.IsNullOrEmpty(wrapped) || _vault == null) return null;
        foreach (var key in new[] { _vault.Key, _vault.Previous })
        {
            if (key == null) continue;
            try { return System.Text.Encoding.UTF8.GetString(Vault.Unwrap(key, wrapped)); }
            catch (System.Security.Cryptography.CryptographicException) { }
            catch (FormatException) { }
        }
        return null;
    }

    public void SetOfficeJwtSecret(string? secret) => Update(s => s.OfficeJwtSecret =
        string.IsNullOrEmpty(secret) || _vault == null ? null : _vault.Wrap(System.Text.Encoding.UTF8.GetBytes(secret)));

    /// <summary>Wirksame Grenze in Bytes, 0 = unbegrenzt: eigene des Benutzers oder die Vorgabe.</summary>
    public long FileQuotaBytes(UserRecord? u)
    {
        var mb = u?.FileQuotaMb ?? Current.DefaultFileQuotaMb;
        return Math.Max(0, mb) * 1024 * 1024;
    }
}
