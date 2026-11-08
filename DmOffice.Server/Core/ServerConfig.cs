using System.Text.Json;

namespace DmOffice.Core;

/// <summary>Einstellungen fuer den ganzen Server (nicht je Benutzer) - data/server.json.</summary>
public sealed class ServerSettings
{
    /// <summary>Speicher fuer Dateien (WebDAV, Dateien-Ansicht, notizi-Abgleich) je Benutzer in MB. 0: unbegrenzt. Vorgabe 100 GB.</summary>
    public long DefaultFileQuotaMb { get; set; } = 100 * 1024;

    /// <summary>Virenprüfung mit ClamAV (clamd über TCP). Aus, bis der Admin sie einschaltet (VirusScan).</summary>
    public bool ClamAv { get; set; }
    /// <summary>Rechner von clamd, etwa „clamav“ (Container im selben Netz) oder eine IP.</summary>
    public string? ClamAvHost { get; set; }
    public int ClamAvPort { get; set; } = 3310;
    /// <summary>Größere Dateien werden nicht geprüft (clamd: StreamMaxLength, Vorgabe 100 MB).</summary>
    public int ClamAvMaxMb { get; set; } = 100;
    /// <summary>Ohne erreichbaren Scanner nichts annehmen (sonst: weiter mit Eintrag im Protokoll).</summary>
    public bool ClamAvRequired { get; set; }

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

    /// <summary>Pukki mit KI (Version 2): API-Schluessel von Anthropic, mit dem Hauptschluessel eingepackt. Leer: aus.</summary>
    public string? AiApiKey { get; set; }
    /// <summary>Spamlisten (Version 2): Zugangsschluessel fuer Spamhaus (Data Query Service), eingepackt. Leer: oeffentliche Abfrage.</summary>
    public string? SpamhausKey { get; set; }
    /// <summary>anthropic (Claude) oder openai (OpenAI-kompatibel: OpenAI, Ollama, LM Studio ...).</summary>
    public string AiProvider { get; set; } = "anthropic";
    /// <summary>Nur OpenAI-kompatibel: Basisadresse, etwa https://api.openai.com/v1 oder http://ollama:11434/v1.</summary>
    public string? AiEndpoint { get; set; }
    /// <summary>Modell; leer bei Claude: PukkiAi.ClaudeModels[0].</summary>
    public string? AiModel { get; set; }
    /// <summary>Nur Claude: darf im Internet suchen (Wetter, Nachrichten ...); die Suchbegriffe gehen dann an die Websuche von Anthropic.</summary>
    public bool AiWebSearch { get; set; }
    /// <summary>Ort fuer die Websuche, etwa „Muenchen“ - damit „Wie wird das Wetter morgen?“ ohne Ortsangabe klappt.</summary>
    public string? AiLocation { get; set; }

    /// <summary>Externe Ordner: Ordner auf dem Server, die der Admin Benutzern freigibt.</summary>
    public List<DmOffice.Storage.ExternalFolderDef> ExternalFolders { get; set; } = new();
    /// <summary>Benutzer duerfen eigene WebDAV-Ordner einbinden.</summary>
    public bool ExternalWebDavForUsers { get; set; }
    /// <summary>Benutzer duerfen eigene OneDrive-Ordner einbinden (Microsoft Graph, Anmeldung je Ordner).</summary>
    public bool ExternalOneDriveForUsers { get; set; }
    /// <summary>Benutzer duerfen eigene pCloud-Ordner einbinden (pCloud-API, Anmeldung je Ordner mit der eigenen pCloud-App des Benutzers).</summary>
    public bool ExternalPCloudForUsers { get; set; }
    /// <summary>Coach: Garmin Personal API auch für Benutzer ohne Adminrechte (zuerst nur Admins, docs/dmcoach-konzept.md).</summary>
    public bool CoachGarminForAll { get; set; }
    /// <summary>Früher: Schalter der Audiothek - nur noch für die einmalige Übernahme in <see cref="ModulesOff"/>.</summary>
    public bool Audio { get; set; }
    /// <summary>Vom Admin ausgeschaltete Module (Features.Modules): weg aus der Leiste, API 404, Hintergrundarbeit ruht.
    /// null: noch nicht übernommen (ServerConfig setzt es beim Laden).</summary>
    public List<string>? ModulesOff { get; set; }
    /// <summary>Gemeinsames Junk-Wissen: „Junk“-Klicks als Fingerabdrücke für alle (Mail/JunkPrints.cs) - aus, bis der Admin es einschaltet.</summary>
    public bool SharedJunk { get; set; }
    /// <summary>Zusätzliche Erkennungsregeln (Mail/SpamRules.cs) verwenden - aus, bis der Admin sie einschaltet.</summary>
    public bool SpamRulesOn { get; set; }
    /// <summary>Die Erkennungsregeln; null: die mitgelieferten Startregeln (SpamRules.Defaults).</summary>
    public List<DmOffice.Mail.SpamRule>? SpamRules { get; set; }
    /// <summary>... auch mit Adressen im Heimnetz (sonst nur oeffentliche - gegen Abfragen des Heimnetzes ueber dmOffice).</summary>
    public bool ExternalWebDavPrivate { get; set; }
    /// <summary>Datensicherung für Benutzer gesperrt (Vorgabe: erlaubt). Eingerichtete Sicherungen ruhen dann.</summary>
    public bool PlainBackupOff { get; set; }
    /// <summary>Speicherziel fuer Kopie und Sicherung: s3, webdav oder folder.</summary>
    public string StorageTarget { get; set; } = "s3";
    /// <summary>WebDAV-Ziel: Adresse (Wurzel), Benutzername, Passwort (eingepackt).</summary>
    public string? StorageWebDavUrl { get; set; }
    public string? StorageWebDavUser { get; set; }
    public string? StorageWebDavPassword { get; set; }
    /// <summary>Ordner-Ziel: Pfad im Container (etwa ein eingebundenes /backup).</summary>
    public string? StorageFolder { get; set; }
    /// <summary>Kopie der Dateiinhalte in einen S3-kompatiblen Speicher (Version 2): an/aus.</summary>
    public bool S3Mirror { get; set; }
    /// <summary>Adresse, etwa http://192.168.178.10:3900 (Garage) oder https://fsn1.your-objectstorage.com (Hetzner).</summary>
    public string? S3Endpoint { get; set; }
    /// <summary>Region; Garage: garage, sonst meist us-east-1 oder die des Anbieters.</summary>
    public string? S3Region { get; set; }
    public string? S3Bucket { get; set; }
    /// <summary>Ordner im Bucket (leer: oberste Ebene).</summary>
    public string? S3Prefix { get; set; } = "dmoffice";
    public string? S3AccessKey { get; set; }
    /// <summary>Geheimer Schluessel, mit dem Hauptschluessel eingepackt.</summary>
    public string? S3SecretKey { get; set; }
    /// <summary>Eingebaute Photo-Engine ausgeschaltet (Verwaltung → Module): keine Foto-Analyse, RAW-Entwicklung und Personen.</summary>
    public bool PhotoEngineOff { get; set; }
    /// <summary>Naechtliche Sicherung der Datenbanken nach S3 (S3Backup).</summary>
    public bool S3Backup { get; set; }
    /// <summary>Uhrzeit der Sicherung (HH:mm, Ortszeit des Servers).</summary>
    public string? S3BackupAt { get; set; } = "03:00";
    /// <summary>Frueher: Staende so viele Tage aufbewahren. Gilt nur noch, bis <see cref="S3BackupKeepCount"/> gesetzt ist (BackupRules.KeepCount).</summary>
    public int S3BackupKeepDays { get; set; } = 30;
    /// <summary>So viele Staende des Core-Backups bewahrt dmOffice am Ziel auf; 0: noch aus den frueheren Tagen umzurechnen.</summary>
    public int S3BackupKeepCount { get; set; }
    /// <summary>Wie oft gesichert wird, in Stunden: 1, 3, 6, 12, 24 (täglich, Vorgabe), 168 (wöchentlich) oder 720 (monatlich) - ab S3BackupAt.</summary>
    public int S3BackupEveryHours { get; set; } = 24;
    /// <summary>
    /// Files-Backup (docs/datei-backup-konzept.md): haelt die Dateiinhalte fuer die Haltedauer vor - setzt das Core-Backup voraus.
    /// Ohne eigenes Ziel liegen sie im Storage Extender (Storage Extender), dort gilt dann die Haltedauer statt der Frist der Spiegelung.
    /// </summary>
    public bool FilesBackup { get; set; }
    /// <summary>Haltedauer in Tagen; wirksam ist mindestens BackupRules.MinHoldDays.</summary>
    public int FilesBackupDays { get; set; }
    public bool FilesBackupForever { get; set; }
    public bool FilesBackupOwnTarget { get; set; }
    /// <summary>Die Kopie am Ziel des Storage Extender ist vollständig; erst dann gibt er die längere Frist für Gelöschtes auf.</summary>
    public bool FilesBackupCopied { get; set; }
    public string FilesBackupTarget { get; set; } = "s3";
    public string? FilesBackupS3Endpoint { get; set; }
    public string? FilesBackupS3Region { get; set; }
    public string? FilesBackupS3Bucket { get; set; }
    public string? FilesBackupS3Prefix { get; set; } = "dmoffice";
    public string? FilesBackupS3AccessKey { get; set; }
    public string? FilesBackupS3SecretKey { get; set; }
    public string? FilesBackupWebDavUrl { get; set; }
    public string? FilesBackupWebDavUser { get; set; }
    public string? FilesBackupWebDavPassword { get; set; }
    public string? FilesBackupFolder { get; set; }
    /// <summary>
    /// Core-Backup an ein eigenes Ziel statt an den Storage Extender (etwa: Dateien zu Hause in Garage, Core-Backup klein
    /// und billig bei Hetzner). Felder wie beim Storage Extender; Geheimnisse mit dem Hauptschluessel eingepackt.
    /// </summary>
    public bool BackupOwnTarget { get; set; }
    public string BackupTarget { get; set; } = "s3";
    public string? BackupS3Endpoint { get; set; }
    public string? BackupS3Region { get; set; }
    public string? BackupS3Bucket { get; set; }
    public string? BackupS3Prefix { get; set; } = "dmoffice";
    public string? BackupS3AccessKey { get; set; }
    public string? BackupS3SecretKey { get; set; }
    public string? BackupWebDavUrl { get; set; }
    public string? BackupWebDavUser { get; set; }
    public string? BackupWebDavPassword { get; set; }
    public string? BackupFolder { get; set; }
    /// <summary>Wann die Zugangsdaten für den Notfall zuletzt abgeholt wurden, und ihr Fingerabdruck damals (StorageProfile).</summary>
    public DateTime? StorageProfileAt { get; set; }
    public string? StorageProfileStamp { get; set; }
    /// <summary>Frueher: Betriebsart (mirror, cache). Gilt nur noch, bis <see cref="S3CacheLimited"/> gesetzt ist: mirror heisst keine Begrenzung.</summary>
    public string S3Mode { get; set; } = "mirror";
    /// <summary>Cache zusaetzlich auf <see cref="S3CacheTargetGb"/> begrenzen? Ohne Begrenzung nutzt er das Volume bis zur Reserve.</summary>
    public bool? S3CacheLimited { get; set; }
    /// <summary>Zwischenspeicher: Zielwert in GB - darauf verdraengt der Abgleich in Ruhe.</summary>
    public double S3CacheTargetGb { get; set; } = 100;
    /// <summary>Frueher: Hoechstwert des Zwischenspeichers in GB - ersetzt durch <see cref="S3MinFreePercent"/>, nur noch fuer alte Einstellungen.</summary>
    public double S3CacheMaxGb { get; set; } = 500;
    /// <summary>
    /// Mit Storage Extender: so viel Prozent des lokalen Speichervolumens bleiben immer frei. Wird es knapp, nimmt dmOffice keine
    /// bereinigt dmOffice den Cache auch unter die Zielgroesse und reicht Geoeffnetes nur durch; laesst sich nichts verdraengen, nimmt es keine neuen Dateien an.
    /// </summary>
    public int S3MinFreePercent { get; set; } = 10;
    /// <summary>So viele GB bleiben auf dem lokalen Speichervolumen immer frei. Null: frueher in Prozent eingestellt (<see cref="S3MinFreePercent"/>).</summary>
    public double? S3MinFreeGb { get; set; }
}

/// <summary>Hoechstwerte je Benutzer; 0 = keine Vorgabe (der Benutzer darf auch "unbegrenzt"). Vorgaben: Versionen und Papierkorb
/// 30 Tage und 1 GB, Freigabelinks laufen nach 30 Tagen ab.</summary>
public sealed class FileLimits
{
    public int VersionsDays { get; set; } = 30;
    public int VersionsMb { get; set; } = 1024;
    public int TrashDays { get; set; } = 30;
    public int TrashMb { get; set; } = 1024;
    public int LinkMaxDownloads { get; set; }
    public int LinkDays { get; set; } = 30;
    /// <summary>ZIP-Downloads (Downloadkorb, „Tresor herunterladen“) höchstens so groß; 0 = ohne Grenze. Vorgabe 5 GB.</summary>
    public int ZipMb { get; set; } = 5120;
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
        // Hauptschalter der Module: der frühere Audiothek-Schalter einmalig übernommen - war sie aus (auch neu: Vorgabe aus), bleibt sie aus
        _settings.ModulesOff ??= _settings.Audio ? [] : [Features.Audio];
    }

    /// <summary>Nach jeder Änderung (Update).</summary>
    public event Action? Changed;

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
        Changed?.Invoke();
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

    /// <summary>Passwort des WebDAV-Ziels im Klartext, null wenn keins.</summary>
    public string? StorageWebDavPassword() => Unwrapped(Current.StorageWebDavPassword);

    public void SetStorageWebDavPassword(string? pw) => Update(s => s.StorageWebDavPassword =
        string.IsNullOrEmpty(pw) || _vault == null ? null : _vault.Wrap(System.Text.Encoding.UTF8.GetBytes(pw)));

    /// <summary>Geheimer S3-Schluessel im Klartext, null wenn keiner.</summary>
    public string? S3SecretKey() => Unwrapped(Current.S3SecretKey);

    public void SetS3SecretKey(string? key) => Update(s => s.S3SecretKey =
        string.IsNullOrEmpty(key) || _vault == null ? null : _vault.Wrap(System.Text.Encoding.UTF8.GetBytes(key)));

    /// <summary>Eigenes Ziel des Core-Backups: geheimer S3-Schluessel und WebDAV-Passwort im Klartext.</summary>
    public string? BackupS3SecretKey() => Unwrapped(Current.BackupS3SecretKey);
    public void SetBackupS3SecretKey(string? key) => Update(s => s.BackupS3SecretKey =
        string.IsNullOrEmpty(key) || _vault == null ? null : _vault.Wrap(System.Text.Encoding.UTF8.GetBytes(key)));
    public string? BackupWebDavPassword() => Unwrapped(Current.BackupWebDavPassword);
    public void SetBackupWebDavPassword(string? pw) => Update(s => s.BackupWebDavPassword =
        string.IsNullOrEmpty(pw) || _vault == null ? null : _vault.Wrap(System.Text.Encoding.UTF8.GetBytes(pw)));

    /// <summary>Eigenes Ziel des Files-Backups: geheimer S3-Schluessel und WebDAV-Passwort im Klartext.</summary>
    public string? FilesBackupS3SecretKey() => Unwrapped(Current.FilesBackupS3SecretKey);
    public void SetFilesBackupS3SecretKey(string? key) => Update(s => s.FilesBackupS3SecretKey =
        string.IsNullOrEmpty(key) || _vault == null ? null : _vault.Wrap(System.Text.Encoding.UTF8.GetBytes(key)));
    public string? FilesBackupWebDavPassword() => Unwrapped(Current.FilesBackupWebDavPassword);
    public void SetFilesBackupWebDavPassword(string? pw) => Update(s => s.FilesBackupWebDavPassword =
        string.IsNullOrEmpty(pw) || _vault == null ? null : _vault.Wrap(System.Text.Encoding.UTF8.GetBytes(pw)));

    /// <summary>API-Schluessel fuer Pukki mit KI im Klartext, null wenn keiner.</summary>
    public string? AiApiKey() => Unwrapped(Current.AiApiKey);
    public string? SpamhausKey() => Unwrapped(Current.SpamhausKey);
    public void SetSpamhausKey(string? key) => Update(s => s.SpamhausKey =
        string.IsNullOrEmpty(key) || _vault == null ? null : _vault.Wrap(System.Text.Encoding.UTF8.GetBytes(key)));

    public void SetAiApiKey(string? key) => Update(s => s.AiApiKey =
        string.IsNullOrEmpty(key) || _vault == null ? null : _vault.Wrap(System.Text.Encoding.UTF8.GetBytes(key)));

    private string? Unwrapped(string? wrapped)
    {
        if (string.IsNullOrEmpty(wrapped) || _vault == null) return null;
        foreach (var key in new[] { _vault.Key, _vault.Previous })
        {
            if (key == null) continue;
            try { return System.Text.Encoding.UTF8.GetString(Vault.Unwrap(key, wrapped)); }
            catch (System.Security.Cryptography.CryptographicException) { }
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
