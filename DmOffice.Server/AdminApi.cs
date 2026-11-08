using System.Security.Claims;
using DmOffice.Core;
using DmOffice.Mail;

namespace DmOffice;

public static class AdminApi
{
    /// <summary>Unter Linux nie als Serverordner: Programm-, Prozess- und Systemordner.</summary>
    private static readonly string[] SystemDirs = OperatingSystem.IsLinux()
        ? ["/app", "/proc", "/sys", "/dev", "/etc", "/root", "/run", "/boot", "/bin", "/sbin", "/lib", "/lib64", "/usr", "/var/run", "/var/lib/docker", "/home/app"]
        : [];

    public record NewUser(string Login, string DisplayName, string Password, bool IsAdmin);
    public record EditUser(string? DisplayName, string? Password, bool? IsAdmin, long? FileQuotaMb = null, bool DefaultFileQuota = false, bool? FilesOff = null, bool? AiAllowed = null);
    /// <summary>Pukki mit KI. ApiKey: null = unveraendert, "" = loeschen.</summary>
    public record ExternalBody(List<DmOffice.Storage.ExternalFolderDef> Folders, bool WebDavForUsers, bool WebDavPrivate, bool? OneDriveForUsers = null,
        bool? PCloudForUsers = null);
    public record BackupBody(bool Enabled, string? At, int? KeepCount = null, int? EveryHours = null);
    public record FilesBackupBody(bool Enabled, bool Own, int Days, bool Forever, string? Target, string? Endpoint, string? Region, string? Bucket, string? Prefix, string? AccessKey, string? SecretKey,
        string? WebDavUrl, string? WebDavUser, string? WebDavPassword, string? Folder);
    public record BackupTargetBody(bool Own, string? Target, string? Endpoint, string? Region, string? Bucket, string? Prefix, string? AccessKey, string? SecretKey,
        string? WebDavUrl, string? WebDavUser, string? WebDavPassword, string? Folder);
    public record CacheBody(bool Limited, double TargetGb, double MinFreeGb);
    public record StorageBody(bool Mirror, string? Endpoint, string? Region, string? Bucket, string? Prefix, string? AccessKey, string? SecretKey,
        string? Target = null, string? WebDavUrl = null, string? WebDavUser = null, string? WebDavPassword = null, string? Folder = null);
    public record AiBody(string? Provider, string? Endpoint, string? Model, string? ApiKey, bool WebSearch = false, string? Location = null);
    public record ServerBody(long DefaultFileQuotaMb);
    public record SpamListsBody(string? Key);
    public record SharedJunkBody(bool On);
    public record SpamRulesBody(bool On, List<SpamRule> Rules);
    public record SpamRuleCheckBody(SpamRule Rule, string? Sample);
    public record SpamRuleImportBody(string Text);
    public record VirusBody(bool ClamAv, string? Host, int Port, int MaxMb, bool Required);
    public record VirusTestBody(string? Host, int? Port);
    public record PhotoEngineBody(bool On);
    public record SearchBody(string Level, List<string>? Groups, List<string>? Folders, int MaxMb, string? Read, string Check);
    public record ModuleBody(string Id, bool On);

    /// <summary>Alles, was der Benutzer belegt (Datenbank und Dateien); null, wenn die Datenbank gerade nicht zu oeffnen ist.</summary>
    private static long? FilesUsed(Spaces spaces, string id)
    {
        try { return DmOffice.Core.UserStorage.Used(spaces.Get(id).Db); }
        catch { return null; }
    }

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api/admin").RequireAuthorization("admin");

        api.MapPut("/settings", (ServerBody body, ServerConfig config) =>
        {
            config.Update(s => s.DefaultFileQuotaMb = Math.Max(0, body.DefaultFileQuotaMb));
            return Results.Ok();
        });

        // Hauptschalter der Module: serverweit an/aus. released: in dieser Version freigeschaltet (sonst ohne Wirkung)
        object Modules() => Features.Modules.Select(id => new { id, on = Features.On(id), released = Features.InRelease(id) }).ToList();
        // Eingebaute Photo-Engine: an/aus. external: läuft über PHOTO_ENGINE_URL auf einem anderen Rechner, der Schalter wirkt dann nicht
        object EngineState(ServerConfig config) => new { builtIn = DmOffice.Dav.PhotoEngine.BuiltInAvailable, external = DmOffice.Dav.PhotoEngine.ExternalUrl != null, on = !config.Current.PhotoEngineOff, running = DmOffice.Dav.PhotoEngine.BuiltInRunning };
        api.MapGet("/photo-engine", (ServerConfig config) => EngineState(config));
        api.MapPut("/photo-engine", (PhotoEngineBody body, ServerConfig config) =>
        {
            config.Update(s => s.PhotoEngineOff = !body.On);
            if (!body.On) DmOffice.Dav.PhotoEngine.StopBuiltIn();
            return Results.Ok(EngineState(config));
        });
        api.MapGet("/modules", Modules);
        api.MapPut("/modules", (ModuleBody body, ServerConfig config) =>
        {
            if (!Features.Modules.Contains(body.Id)) return Results.BadRequest(new { error = "Unbekanntes Modul." });
            config.Update(s =>
            {
                var off = (s.ModulesOff ?? []).Where(x => x != body.Id).ToList();
                if (!body.On) off.Add(body.Id);
                s.ModulesOff = off;
            });
            return Results.Ok(Modules());
        });

        // Hoechstwerte fuer Versionen, Papierkorb und Freigabelinks (Einstellungen -> Dateien der Benutzer)
        api.MapPut("/file-limits", (FileLimits body) =>
        {
            static int Clamp(int v, int max) => Math.Clamp(v, 0, max);
            var l = new FileLimits
            {
                VersionsDays = Clamp(body.VersionsDays, 36500), VersionsMb = Clamp(body.VersionsMb, 10_000_000),
                TrashDays = Clamp(body.TrashDays, 3650), TrashMb = Clamp(body.TrashMb, 10_000_000),
                LinkMaxDownloads = Clamp(body.LinkMaxDownloads, 10_000), LinkDays = Clamp(body.LinkDays, 3650),
                ZipMb = Clamp(body.ZipMb, 10_000_000),
            };
            app.Services.GetRequiredService<ServerConfig>().Update(s => s.FileLimits = l);
            return Results.Ok(l);
        });

        api.MapGet("/status", (Vault vault, DataPaths paths, ServerConfig config) => new
        {
            defaultFileQuotaMb = config.Current.DefaultFileQuotaMb,
            aiConfigured = Chat.PukkiAi.Configured(config),
            fileLimits = config.Current.FileLimits,
            keyInDataDir = vault.KeyInDataDir,
            oldKeyFile = vault.OldKeyFileLeft,
            dataDir = paths.Root,
            preUpdateBytes = PreUpdateBackup.TotalBytes(paths),
            version = SettingsApi.Version()
        });

        // Alte Schluesseldatei nach dem Umstieg auf DMOFFICE_MASTER_KEY loeschen (die Variable muss gelten)
        api.MapGet("/old-master-key", (Vault vault) =>
            vault.OldKeyFileLeft ? Results.File(File.ReadAllBytes(vault.KeyFile), "application/octet-stream", "master.key") : Results.NotFound());
        api.MapDelete("/old-master-key", (Vault vault) =>
        {
            if (!vault.OldKeyFileLeft) return Results.NotFound();
            vault.DeleteOldKeyFile();
            return Results.Ok();
        });

        api.MapGet("/users", (UserStore users, DataPaths paths, Spaces spaces, ServerConfig config) => users.All().Select(u => new
        {
            u.Id, u.Login, u.DisplayName, u.IsAdmin, u.Created, u.TwoFactor,
            size = File.Exists(paths.UserDb(u.Id)) ? new FileInfo(paths.UserDb(u.Id)).Length : 0,
            fileQuotaMb = u.FileQuotaMb,
            filesOff = u.FilesOff,
            aiAllowed = u.AiAllowed,
            fileQuota = config.FileQuotaBytes(u),
            files = FilesUsed(spaces, u.Id)
        }));

        // ---------------- Pukki mit KI (Version 2) ----------------

        // Speicher (Version 2): Kopie der Dateiinhalte in einen S3-kompatiblen Speicher
        // Externe Ordner: Ordner auf dem Server fuer Benutzer freigeben, eigene WebDAV-Ordner erlauben
        api.MapGet("/external", (ServerConfig config) => new { folders = config.Current.ExternalFolders, webDavForUsers = config.Current.ExternalWebDavForUsers, webDavPrivate = config.Current.ExternalWebDavPrivate,
            oneDriveForUsers = config.Current.ExternalOneDriveForUsers, microsoft = DmOffice.Storage.ExternalFolders.OAuth?.Enabled == true,
            pCloudForUsers = config.Current.ExternalPCloudForUsers });
        // Suche in Server-Mounts: Stand und Einstellungen je Mount (die Zählung geht über alle Dateien, daher kurz gemerkt)
        object IndexInfo(DmOffice.Storage.ExternalFolderDef f, DmOffice.Storage.ExternalIndex index, DmOffice.Storage.ExternalIndexer indexer)
        {
            var st = index.StateOf(f, TimeSpan.FromSeconds(30));
            return new
            {
                mount = f.Id, level = f.Level, groups = f.Groups, folders = f.ContentFolders, maxMb = f.SearchMaxMb, read = f.SearchRead, check = f.SearchCheck,
                st.Files, st.Dirs, st.Wanted, st.Done, st.Errors, st.LastScan, st.LastSeconds, st.BytesPerSecond, st.Note, st.Parts,
                checking = indexer.Checking?.Mount == f.Name, ocr = DmOffice.Search.Ocr.Available,
            };
        }
        DmOffice.Storage.ExternalFolderDef ServerMount(ServerConfig config, string id) =>
            config.Current.ExternalFolders.FirstOrDefault(f => f.Id == id) ?? throw new KeyNotFoundException("Diesen Server-Mount gibt es nicht.");
        api.MapGet("/external/index", (ServerConfig config, DmOffice.Storage.ExternalIndex index, DmOffice.Storage.ExternalIndexer indexer) =>
            config.Current.ExternalFolders.Select(f => IndexInfo(f, index, indexer)).ToList());
        api.MapPut("/external/index/{id}", (string id, SearchBody body, ServerConfig config, DmOffice.Storage.ExternalIndex index, DmOffice.Storage.ExternalIndexer indexer) =>
        {
            ServerMount(config, id);
            string[] levels = [DmOffice.Storage.ExternalFolderDef.LevelNone, DmOffice.Storage.ExternalFolderDef.LevelNames, DmOffice.Storage.ExternalFolderDef.LevelContents];
            string[] checks = [DmOffice.Storage.ExternalFolderDef.CheckDaily, DmOffice.Storage.ExternalFolderDef.CheckSixHours, DmOffice.Storage.ExternalFolderDef.CheckHourly, DmOffice.Storage.ExternalFolderDef.CheckWeekly, DmOffice.Storage.ExternalFolderDef.CheckManual];
            if (!levels.Contains(body.Level) || !checks.Contains(body.Check) || body.MaxMb is < 1 or > 10240) return Results.BadRequest(new { error = "Diese Einstellung gibt es nicht." });
            config.Update(s =>
            {
                var f = s.ExternalFolders.First(x => x.Id == id);
                f.SearchLevel = body.Level;
                f.SearchGroups = (body.Groups ?? []).Where(DmOffice.Storage.ExternalFolderDef.AllGroups.Contains).Distinct().ToList();
                f.ContentFolders = (body.Folders ?? []).Where(n => n.Length > 0 && n.IndexOfAny(['/', '\\']) < 0).Distinct().ToList();
                f.SearchMaxMb = body.MaxMb;
                f.SearchRead = body.Read == DmOffice.Storage.ExternalFolderDef.ReadNight ? body.Read : DmOffice.Storage.ExternalFolderDef.ReadContinuous;
                f.SearchCheck = body.Check;
            });
            indexer.Kick();
            return Results.Ok(IndexInfo(ServerMount(config, id), index, indexer));
        });
        // „Jetzt prüfen“ und „Alles neu auswerten“ laufen im Hintergrund, die Antwort kommt sofort
        api.MapPost("/external/index/{id}/check", (string id, ServerConfig config, DmOffice.Storage.ExternalIndexer indexer) =>
        {
            ServerMount(config, id);
            indexer.RequestScan(id);
            return Results.Ok();
        });
        api.MapPost("/external/index/{id}/reset", (string id, ServerConfig config, DmOffice.Storage.ExternalIndex index, DmOffice.Storage.ExternalIndexer indexer) =>
        {
            ServerMount(config, id);
            index.RequestReset(id);
            indexer.Kick();
            return Results.Ok();
        });
        api.MapPut("/external", (ExternalBody body, ServerConfig config, DataPaths paths, DmOffice.Storage.ExternalIndexer indexer) =>
        {
            foreach (var f in body.Folders)
            {
                f.Name = f.Name.Trim(); f.Path = f.Path.Trim();
                if (f.Name.Length == 0) return Results.BadRequest(new { error = "Jeder Ordner braucht einen Namen." });
                if (!Path.IsPathFullyQualified(f.Path) || !Directory.Exists(f.Path))
                    return Results.BadRequest(new { error = $"„{f.Name}“: den Pfad {f.Path} gibt es auf dem Server nicht – im Container einbinden und den vollen Pfad angeben." });
                var full = Path.GetFullPath(f.Path).TrimEnd(Path.DirectorySeparatorChar);
                // Nie das Datenverzeichnis selbst (oder etwas darin/darueber) - sonst laege die verschluesselte Ablage offen
                bool Touches(string dir) { var d = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar); return full == d || d.StartsWith(full + Path.DirectorySeparatorChar) || full.StartsWith(d + Path.DirectorySeparatorChar); }
                if (Touches(paths.Root) || Touches(paths.Blobs) || full == Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar))
                    return Results.BadRequest(new { error = $"„{f.Name}“: das Datenverzeichnis von dmOffice und die oberste Ebene lassen sich nicht freigeben." });
                // Nie das Programm selbst oder Systemordner (Programmdateien, Prozessangaben, Konfiguration, Geheimnisse)
                if (Touches(AppContext.BaseDirectory) || SystemDirs.Any(d => full == d || full.StartsWith(d + Path.DirectorySeparatorChar)))
                    return Results.BadRequest(new { error = $"„{f.Name}“: Programm- und Systemordner des Servers lassen sich nicht freigeben." });
                if (config.Current.StorageTarget == "folder" && !string.IsNullOrWhiteSpace(config.Current.StorageFolder) && Overlaps(full, config.Current.StorageFolder))
                    return Results.BadRequest(new { error = $"„{f.Name}“: dort liegt der Storage Extender von dmOffice – über die Dateiablage ließen sich seine Inhalte löschen." });
                if (string.IsNullOrWhiteSpace(f.Id)) { f.Id = Guid.NewGuid().ToString("N")[..12]; f.SearchLevel ??= DmOffice.Storage.ExternalFolderDef.LevelNames; }
                // Nur Namen von Ordnern direkt darin, die es gibt
                f.Include = (f.Include ?? []).Where(n => n.Length > 0 && n.IndexOfAny(['/', '\\']) < 0 && n is not ("." or "..") && Directory.Exists(Path.Combine(full, n))).Distinct().ToList();
            }
            config.Update(s => { s.ExternalFolders = body.Folders; s.ExternalWebDavForUsers = body.WebDavForUsers; s.ExternalWebDavPrivate = body.WebDavPrivate; if (body.OneDriveForUsers is bool od) s.ExternalOneDriveForUsers = od;
                if (body.PCloudForUsers is bool pc) s.ExternalPCloudForUsers = pc; });
            // Suche geändert: gleich abgleichen statt erst in einer Minute
            indexer.Kick();
            return Results.Ok();
        });

        // Datensicherung: freischalten oder sperren, Stand je Person
        DmOffice.Storage.PlainBackupService.MapAdmin(api);

        // Ordner auf dem Server durchblättern (externe Ordner, Sicherungsziel): ohne Pfad die im Container eingebundenen Ordner,
        // sonst die Unterordner. Nur Namen; das Datenverzeichnis ist als gesperrt markiert.
        api.MapGet("/server-folders", (string? path, DataPaths paths) =>
        {
            static string Norm(string p) => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar) is { Length: > 0 } n ? n : Path.DirectorySeparatorChar.ToString();
            bool Blocked(string dir)
            {
                var full = Norm(dir);
                bool Touches(string other) { var d = Norm(other); return full == d || d.StartsWith(full + Path.DirectorySeparatorChar) || full.StartsWith(d + Path.DirectorySeparatorChar); }
                return Touches(paths.Root) || Touches(paths.Blobs) || full == Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar);
            }
            if (string.IsNullOrWhiteSpace(path))
                return Results.Ok(new { path = (string?)null, parent = (string?)null, dirs = MountPoints().Where(Directory.Exists).Select(d => new { name = d, path = d, blocked = Blocked(d) }).ToList() });
            if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path)) return Results.BadRequest(new { error = "Diesen Ordner gibt es auf dem Server nicht." });
            var here = Norm(path);
            List<string> subs;
            try { subs = Directory.EnumerateDirectories(here).Where(d => !Path.GetFileName(d).StartsWith('.')).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).Take(500).ToList(); }
            catch (UnauthorizedAccessException) { subs = []; }
            return Results.Ok(new { path = here, parent = Path.GetDirectoryName(here), dirs = subs.Select(d => new { name = Path.GetFileName(d), path = d, blocked = Blocked(d) }).ToList() });
        });

        api.MapGet("/storage", (ServerConfig config, S3Mirror mirror, S3Backup backup, FilesBackup files) => StorageState(config, mirror, backup, files));
        api.MapPut("/storage", async (StorageBody body, ServerConfig config, S3Mirror mirror, S3Backup backup, FilesBackup files, DataPaths paths, CancellationToken ct) =>
        {
            if (!string.IsNullOrWhiteSpace(body.Endpoint) && !(Uri.TryCreate(body.Endpoint.Trim(), UriKind.Absolute, out var u) && u.Scheme is "http" or "https"))
                return Results.BadRequest(new { error = "Die Adresse muss mit http:// oder https:// beginnen." });
            var target = body.Target is "webdav" or "folder" ? body.Target : "s3";
            if (target == "webdav" && !string.IsNullOrWhiteSpace(body.WebDavUrl) && !(Uri.TryCreate(body.WebDavUrl.Trim(), UriKind.Absolute, out var wu) && wu.Scheme is "http" or "https"))
                return Results.BadRequest(new { error = "Die WebDAV-Adresse muss mit http:// oder https:// beginnen." });
            if (target == "folder" && !string.IsNullOrWhiteSpace(body.Folder) && (!Path.IsPathFullyQualified(body.Folder.Trim()) || !Directory.Exists(body.Folder.Trim())))
                return Results.BadRequest(new { error = "Den Ordner gibt es auf dem Server nicht – im Container einbinden (etwa /backup) und den vollen Pfad angeben." });
            var cur = config.Current;
            // Ordner als Ziel: nie im Datenverzeichnis, nie zugleich ein externer Ordner (dort ließen sich die Inhalte über die Dateiablage löschen)
            if (target == "folder" && !string.IsNullOrWhiteSpace(body.Folder))
            {
                if (Overlaps(body.Folder, paths.Root) || Overlaps(body.Folder, paths.Blobs))
                    return Results.BadRequest(new { error = "Das Datenverzeichnis von dmOffice taugt nicht als Storage Extender – einen eigenen Ordner wählen." });
                if (cur.ExternalFolders.FirstOrDefault(f => Overlaps(body.Folder, f.Path)) is { } ext)
                    return Results.BadRequest(new { error = $"Der Ordner überschneidet sich mit dem externen Ordner „{ext.Name}“ – darüber ließen sich die Inhalte löschen. Einen eigenen Ordner wählen." });
            }
            // Wohin (ohne Zugangsdaten) - der Präfix gilt bei jedem Ziel
            static string Where(string t, string? endpoint, string? bucket, string? prefix, string? url, string? folder) => t switch
            {
                "webdav" => (url ?? "").Trim().TrimEnd('/'),
                "folder" => (folder ?? "").Trim().TrimEnd('/'),
                _ => $"{(endpoint ?? "").Trim().TrimEnd('/')}|{(bucket ?? "").Trim()}",
            } + "|" + (prefix ?? "").Trim().Trim('/');
            // Liegen Dateiinhalte nur im Storage Extender, darf keine Änderung den Weg dorthin abschneiden - geprüft, BEVOR etwas geändert wird:
            // ausschalten und Zugangsdaten löschen erst, wenn alle wieder hier sind; andere Adresse oder Zugangsdaten nur, wenn dort nachweislich alles liegt
            var off = cur.S3Mirror && !body.Mirror;
            var dropsKey = (target == "s3" && body.SecretKey == "") || (target == "webdav" && body.WebDavPassword == "");
            var moves = target != (cur.StorageTarget ?? "s3")
                || Where(target, body.Endpoint, body.Bucket, body.Prefix, body.WebDavUrl, body.Folder) != Where(cur.StorageTarget ?? "s3", cur.S3Endpoint, cur.S3Bucket, cur.S3Prefix, cur.StorageWebDavUrl, cur.StorageFolder)
                || (target == "s3" && ((body.Region ?? "").Trim() != (cur.S3Region ?? "") || (body.AccessKey ?? "").Trim() != (cur.S3AccessKey ?? "") || body.SecretKey is { Length: > 0 }))
                || (target == "webdav" && ((body.WebDavUser ?? "").Trim() != (cur.StorageWebDavUser ?? "") || body.WebDavPassword is { Length: > 0 }));
            if ((off || dropsKey || moves) && mirror.NotHome() is { Count: > 0 } away)
            {
                var only = $"Noch {away.Count:N0} Dateiinhalte ({Size(away.Bytes)}) liegen nur im Storage Extender.";
                if (off || dropsKey)
                    return Results.Json(new { error = only + " Der Storage Extender lässt sich erst ausschalten und die Zugangsdaten lassen sich erst löschen, wenn alle Dateien auch im lokalen Speicherpool liegen. Bei genügend Speicherplatz lädt dmOffice sie automatisch nach." }, statusCode: 409);
                long notThere;
                try
                {
                    using var candidate = S3Mirror.RemoteOf(target, body.Endpoint?.Trim().TrimEnd('/'), body.Region?.Trim(), body.Bucket?.Trim(), body.AccessKey?.Trim(),
                        body.SecretKey ?? config.S3SecretKey(), body.WebDavUrl?.Trim(), body.WebDavUser?.Trim(), body.WebDavPassword ?? config.StorageWebDavPassword(), body.Folder?.Trim());
                    notThere = await mirror.MissingAtAsync(candidate, body.Prefix, ct);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    return Results.Json(new { error = only + $" Mit den neuen Angaben ist er nicht zu erreichen ({S3Remote.Describe(e)}) – nichts geändert." }, statusCode: 409);
                }
                if (notThere > 0)
                    return Results.Json(new { error = only + $" Unter den neuen Angaben fehlen davon {notThere:N0} – nichts geändert." }, statusCode: 409);
            }
            var storageBefore = S3Mirror.LocationOf(config.Current);
            config.Update(s =>
            {
                if (s.StorageTarget != target) s.S3Mirror = false;   // neues Ziel: erst pruefen, dann einschalten
                s.StorageTarget = target; s.StorageWebDavUrl = body.WebDavUrl?.Trim(); s.StorageWebDavUser = body.WebDavUser?.Trim(); s.StorageFolder = body.Folder?.Trim();
                s.S3Endpoint = body.Endpoint?.Trim().TrimEnd('/'); s.S3Region = body.Region?.Trim(); s.S3Bucket = body.Bucket?.Trim();
                s.S3Prefix = body.Prefix?.Trim().Trim('/'); s.S3AccessKey = body.AccessKey?.Trim();
            });
            if (body.SecretKey != null) config.SetS3SecretKey(body.SecretKey.Length == 0 ? null : body.SecretKey);
            if (body.WebDavPassword != null) config.SetStorageWebDavPassword(body.WebDavPassword.Length == 0 ? null : body.WebDavPassword);
            // Das Files-Backup auf diesem Ziel zieht mit um und beginnt dort neu
            if (!config.Current.FilesBackupOwnTarget && S3Mirror.LocationOf(config.Current) != storageBefore) files.TargetChanged();
            // Beim ersten Einschalten die Vorgabe festlegen; aeltere Einstellungen in Prozent bleiben wirksam, bis sie jemand aendert
            config.Update(s =>
            {
                if (body.Mirror && !s.S3Mirror && s.S3MinFreeGb == null) s.S3MinFreeGb = S3Mirror.DefaultMinFreeGb(mirror.LocalVolume().Total);
                s.S3Mirror = body.Mirror && S3Mirror.Configured(s, config.S3SecretKey());
            });
            if (config.Current.S3Mirror) mirror.Kick();
            return Results.Ok(StorageState(config, mirror, backup, files));
        });
        api.MapPost("/storage/test", async (ServerConfig config, CancellationToken ct) =>
        {
            if (!S3Mirror.Configured(config.Current, config.S3SecretKey())) return Results.BadRequest(new { error = "Das Speicherziel ist noch nicht vollständig eingerichtet." });
            try { using var remote = S3Mirror.RemoteOf(config); await S3Mirror.ProbeAsync(remote, config.Current.S3Prefix, ct); return Results.Ok(); }
            catch (Exception e) when (e is not OperationCanceledException) { return Results.BadRequest(new { error = "Verbindung fehlgeschlagen: " + S3Remote.Describe(e) }); }
        });
        // Zugangsdaten für den Notfall: eine mit dem Hauptschlüssel verschlüsselte Zeile (wie die Wiederherstellungscodes bei 2FA)
        api.MapPost("/storage/profile", (ServerConfig config, Vault vault) =>
        {
            if (!S3Mirror.Configured(config.Current, config.S3SecretKey()) && !S3Backup.TargetConfigured(config)) return Results.BadRequest(new { error = "Es ist noch kein Ziel eingerichtet." });
            var text = StorageProfile.Seal(vault, StorageProfile.Current(config));
            var stamp = StorageProfile.Stamp(config);
            config.Update(s => { s.StorageProfileAt = DateTime.UtcNow; s.StorageProfileStamp = stamp; });
            return Results.Ok(new { text });
        });
        api.MapPost("/storage/sync", (S3Mirror mirror) => { mirror.Kick(); return Results.Ok(); });
        // „Der andere Server war dieser vor dem Umzug“: dessen Vermerk am Ziel entfernen
        api.MapPost("/storage/forget-other", async (S3Mirror mirror, CancellationToken ct) => new { removed = await mirror.ForgetOthersAsync(ct) });
        api.MapPost("/storage/backup/forget-other", async (S3Backup backup, CancellationToken ct) => new { removed = await backup.ForgetOthersAsync(ct) });
        // Datenbankprüfung: letzter Bericht und „Jetzt prüfen“
        api.MapGet("/health", (DbHealth health) => new { running = health.Running, report = health.Last });
        // Systemprotokoll: Warnungen und Fehler seit dem Start, neueste zuerst
        api.MapGet("/log", () => new { since = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime(), entries = SystemLog.All() });
        api.MapPost("/health/run", (DbHealth health) => { health.RunNow(); return Results.Ok(); });
        api.MapPost("/storage/restore", (S3Mirror mirror) => { mirror.RequestRestore(); return Results.Ok(); });
        api.MapPut("/storage/backup", (BackupBody body, ServerConfig config, S3Mirror mirror, S3Backup backup, FilesBackup files) =>
        {
            if (!TimeOnly.TryParse(body.At ?? "03:00", System.Globalization.CultureInfo.InvariantCulture, out var at)) return Results.BadRequest(new { error = "Ungültige Uhrzeit." });
            if (body.Enabled && backup.Blocked() is { } why) return Results.BadRequest(new { error = why });
            if (body.EveryHours is int h && !S3Backup.Intervals.Contains(h)) return Results.BadRequest(new { error = "Ungültiger Abstand." });
            config.Update(s => { s.S3Backup = body.Enabled; s.S3BackupAt = at.ToString("HH:mm"); if (body.KeepCount is int keep) s.S3BackupKeepCount = Math.Clamp(keep, 1, 1000); if (body.EveryHours is int every) s.S3BackupEveryHours = every; });
            return Results.Ok(StorageState(config, mirror, backup, files));
        });
        api.MapPut("/storage/mode", (CacheBody body, ServerConfig config, S3Mirror mirror, S3Backup backup, FilesBackup files) =>
        {
            if (body.Limited && body.TargetGb < 1) return Results.BadRequest(new { error = "Die Begrenzung braucht mindestens 1 GB." });
            config.Update(s =>
            {
                s.S3CacheLimited = body.Limited;
                if (body.TargetGb >= 1) s.S3CacheTargetGb = Math.Round(Math.Clamp(body.TargetGb, 1, 1_000_000), 1);
                // Hoechstens die Haelfte des Volumes, sonst passt dort keine Datei mehr
                var half = mirror.LocalVolume() is { Total: > 0 } vol ? Math.Max(1, vol.Total / 2.0 / (1024L * 1024 * 1024)) : 1_000_000;
                s.S3MinFreeGb = Math.Round(Math.Clamp(body.MinFreeGb, 1, half), 1);
            });
            // Der naechste Abgleich verdraengt oder holt zurueck, je nachdem, was jetzt passt
            mirror.Kick();
            return Results.Ok(StorageState(config, mirror, backup, files));
        });
        // Eigenes Ziel fürs Core-Backup (oder zurück auf „wie der Storage Extender“)
        api.MapPut("/storage/backup/target", (BackupTargetBody body, ServerConfig config, S3Mirror mirror, S3Backup backup, FilesBackup files, DataPaths paths) =>
        {
            var target = body.Target is "webdav" or "folder" ? body.Target : "s3";
            if (body.Own)
            {
                if (TargetProblem(target, body.Endpoint, body.WebDavUrl, body.Folder, config, paths) is { } problem) return Results.BadRequest(new { error = problem });
            }
            config.Update(s =>
            {
                s.BackupOwnTarget = body.Own;
                if (!body.Own) return;
                s.BackupTarget = target; s.BackupS3Endpoint = body.Endpoint?.Trim().TrimEnd('/'); s.BackupS3Region = body.Region?.Trim(); s.BackupS3Bucket = body.Bucket?.Trim();
                s.BackupS3Prefix = body.Prefix?.Trim().Trim('/'); s.BackupS3AccessKey = body.AccessKey?.Trim();
                s.BackupWebDavUrl = body.WebDavUrl?.Trim(); s.BackupWebDavUser = body.WebDavUser?.Trim(); s.BackupFolder = body.Folder?.Trim();
            });
            if (body.Own && body.SecretKey != null) config.SetBackupS3SecretKey(body.SecretKey.Length == 0 ? null : body.SecretKey);
            if (body.Own && body.WebDavPassword != null) config.SetBackupWebDavPassword(body.WebDavPassword.Length == 0 ? null : body.WebDavPassword);
            return Results.Ok(StorageState(config, mirror, backup, files));
        });
        // Files-Backup: Schalter, Haltedauer und Ziel (eigenes oder das aus Storage Extender)
        api.MapPut("/storage/files-backup", (FilesBackupBody body, ServerConfig config, S3Mirror mirror, S3Backup backup, FilesBackup files, DataPaths paths) =>
        {
            var target = body.Target is "webdav" or "folder" ? body.Target : "s3";
            if (body.Own && TargetProblem(target, body.Endpoint, body.WebDavUrl, body.Folder, config, paths) is { } problem) return Results.BadRequest(new { error = problem });
            var before = config.Current;
            var whereBefore = (before.FilesBackupOwnTarget, FilesBackup.OwnLocationOf(before), before.FilesBackup);
            string? refused = null;
            config.Update(s =>
            {
                s.FilesBackupOwnTarget = body.Own;
                s.FilesBackupDays = Math.Clamp(body.Days, 0, 36500);
                s.FilesBackupForever = body.Forever;
                if (body.Own)
                {
                    s.FilesBackupTarget = target; s.FilesBackupS3Endpoint = body.Endpoint?.Trim().TrimEnd('/'); s.FilesBackupS3Region = body.Region?.Trim(); s.FilesBackupS3Bucket = body.Bucket?.Trim();
                    s.FilesBackupS3Prefix = body.Prefix?.Trim().Trim('/'); s.FilesBackupS3AccessKey = body.AccessKey?.Trim();
                    s.FilesBackupWebDavUrl = body.WebDavUrl?.Trim(); s.FilesBackupWebDavUser = body.WebDavUser?.Trim(); s.FilesBackupFolder = body.Folder?.Trim();
                    if (FilesBackup.OwnLocationOf(s) == S3Mirror.LocationOf(s)) { refused = "Das ist das Ziel aus Storage Extender. Dafür „Ziel aus Storage Extender übernehmen“ wählen."; s.FilesBackupOwnTarget = false; s.FilesBackup = false; }
                }
            });
            if (refused != null) return Results.BadRequest(new { error = refused });
            if (body.Own && body.SecretKey != null) config.SetFilesBackupS3SecretKey(body.SecretKey.Length == 0 ? null : body.SecretKey);
            if (body.Own && body.WebDavPassword != null) config.SetFilesBackupWebDavPassword(body.WebDavPassword.Length == 0 ? null : body.WebDavPassword);
            if (body.Enabled)
            {
                if (!config.Current.S3Backup) { config.Update(s => s.FilesBackup = false); return Results.BadRequest(new { error = "Das Files-Backup setzt das Core-Backup voraus." }); }
                if (!FilesBackup.Configured(config)) { config.Update(s => s.FilesBackup = false); return Results.BadRequest(new { error = body.Own ? "Das Ziel des Files-Backups ist noch nicht vollständig eingerichtet." : "Im Storage Extender ist kein Ziel eingerichtet und eingeschaltet." }); }
            }
            config.Update(s => s.FilesBackup = body.Enabled);
            var now = config.Current;
            if (whereBefore != (now.FilesBackupOwnTarget, FilesBackup.OwnLocationOf(now), now.FilesBackup)) files.TargetChanged();
            return Results.Ok(StorageState(config, mirror, backup, files));
        });
        api.MapPost("/storage/files-backup/test", async (ServerConfig config, CancellationToken ct) =>
        {
            if (!FilesBackup.Configured(config)) return Results.BadRequest(new { error = "Das Ziel des Files-Backups ist noch nicht vollständig eingerichtet." });
            try { using var remote = FilesBackup.RemoteOf(config); await S3Mirror.ProbeAsync(remote, FilesBackup.PrefixOf(config.Current), ct); return Results.Ok(); }
            catch (Exception e) when (e is not OperationCanceledException) { return Results.BadRequest(new { error = "Verbindung fehlgeschlagen: " + S3Remote.Describe(e) }); }
        });
        api.MapPost("/storage/backup/test", async (ServerConfig config, CancellationToken ct) =>
        {
            if (!S3Backup.TargetConfigured(config)) return Results.BadRequest(new { error = "Das Ziel des Core-Backups ist noch nicht vollständig eingerichtet." });
            try { using var remote = S3Backup.RemoteOf(config); await S3Mirror.ProbeAsync(remote, S3Backup.PrefixOf(config.Current), ct); return Results.Ok(); }
            catch (Exception e) when (e is not OperationCanceledException) { return Results.BadRequest(new { error = "Verbindung fehlgeschlagen: " + S3Remote.Describe(e) }); }
        });
        api.MapPost("/storage/backup/run", (S3Backup backup, FilesBackup files) =>
        {
            if (backup.Blocked() is { } why) return Results.BadRequest(new { error = why });
            backup.RunNow();
            return Results.Ok();
        });
        api.MapGet("/storage/backups", async (ServerConfig config, FilesBackup files, CancellationToken ct) =>
        {
            if (!S3Backup.TargetConfigured(config)) return Results.Ok(Array.Empty<object>());
            try
            {
                using var remote = S3Backup.RemoteOf(config);
                var now = DateTime.UtcNow;
                return Results.Ok((await S3Backup.ListAsync(remote, S3Backup.PrefixOf(config.Current), ct)).Select(x => new { x.Name, x.Created, x.Bytes, files = files.Complete(DateTime.SpecifyKind(x.Created, DateTimeKind.Utc), now) }));
            }
            catch (Exception e) when (e is not OperationCanceledException) { return Results.BadRequest(new { error = S3Remote.Describe(e) }); }
        });

        // Spamlisten: Spamhaus-Zugangsschluessel (der Schluessel selbst geht nie zurueck)
        api.MapGet("/spamlists", (ServerConfig config) => new { hasKey = !string.IsNullOrEmpty(config.SpamhausKey()), sharedJunk = config.Current.SharedJunk });
        // Gemeinsames Junk-Wissen (Mail/JunkPrints.cs) für den ganzen Server ein- oder ausschalten
        api.MapPut("/spamlists/shared", (SharedJunkBody body, ServerConfig config) =>
        {
            config.Update(s => s.SharedJunk = body.On);
            return Results.Ok(new { sharedJunk = body.On });
        });
        // Zusätzliche Erkennungsregeln (Mail/SpamRules.cs): Liste, Prüfen eines Ausdrucks, Einfügen im SpamAssassin-Format
        object RulesState(ServerConfig config)
        {
            var c = config.Current;
            return new
            {
                on = c.SpamRulesOn, defaults = c.SpamRules == null, rules = c.SpamRules ?? SpamRules.Defaults(),
                stats = SpamRules.Stats.ToDictionary(x => x.Key, x => new { hits = x.Value.Hits, timeouts = x.Value.Timeouts }),
            };
        }
        api.MapGet("/spamrules", (ServerConfig config) => RulesState(config));
        api.MapPut("/spamrules", (SpamRulesBody body, ServerConfig config) =>
        {
            var (rules, error) = SpamRules.Clean(body.Rules ?? []);
            if (error != null) return Results.BadRequest(new { error });
            config.Update(s => { s.SpamRulesOn = body.On; s.SpamRules = rules; });
            return Results.Ok(RulesState(config));
        });
        api.MapPost("/spamrules/defaults", (ServerConfig config) =>
        {
            config.Update(s => s.SpamRules = null);
            return Results.Ok(RulesState(config));
        });
        api.MapPost("/spamrules/check", (SpamRuleCheckBody body) =>
        {
            if (SpamRules.Validate(body.Rule) is { } error) return Results.Ok(new { ok = false, error, match = false, found = (string?)null });
            var (match, found) = body.Sample is { Length: > 0 } sample ? SpamRules.Try(body.Rule, sample[..Math.Min(sample.Length, 100_000)]) : (false, null);
            return Results.Ok(new { ok = true, error = (string?)null, match, found });
        });
        api.MapPost("/spamrules/import", (SpamRuleImportBody body) =>
        {
            if ((body.Text ?? "").Length > 500_000) return Results.BadRequest(new { error = "Zu viel Text auf einmal." });
            var r = SpamRules.Import(body.Text ?? "");
            return Results.Ok(new { rules = r.Rules, skipped = r.Skipped });
        });
        api.MapPut("/spamlists", (SpamListsBody body, ServerConfig config) =>
        {
            var key = body.Key?.Trim() ?? "";
            if (key.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(key, "^[a-z0-9]{10,64}$")) return Results.BadRequest(new { error = "Der Schlüssel besteht nur aus Kleinbuchstaben und Ziffern." });
            config.SetSpamhausKey(key);
            return Results.Ok(new { hasKey = key.Length > 0 });
        });
        // Virenprüfung (ClamAV): Einstellung, Test mit EICAR, die letzten Funde
        object VirusState(ServerConfig config)
        {
            var c = config.Current;
            return new { clamAv = c.ClamAv, host = c.ClamAvHost ?? "", port = c.ClamAvPort, maxMb = c.ClamAvMaxMb, required = c.ClamAvRequired, findings = VirusScan.Findings() };
        }
        api.MapGet("/virus", (ServerConfig config) => VirusState(config));
        api.MapPut("/virus", (VirusBody body, ServerConfig config) =>
        {
            var host = body.Host?.Trim() ?? "";
            if (body.ClamAv && host.Length == 0) return Results.BadRequest(new { error = "Bitte den Rechner des Virenscanners angeben." });
            if (host.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(host, @"^[A-Za-z0-9.\-:\[\]]{1,253}$")) return Results.BadRequest(new { error = "Der Rechnername passt nicht." });
            config.Update(s =>
            {
                s.ClamAv = body.ClamAv; s.ClamAvHost = host.Length > 0 ? host : null; s.ClamAvPort = Math.Clamp(body.Port, 1, 65535);
                s.ClamAvMaxMb = Math.Clamp(body.MaxMb, 1, 4000); s.ClamAvRequired = body.Required;
            });
            return Results.Ok(VirusState(config));
        });
        api.MapPost("/virus/test", async (VirusTestBody body, ServerConfig config, CancellationToken ct) =>
        {
            var host = string.IsNullOrWhiteSpace(body.Host) ? config.Current.ClamAvHost : body.Host.Trim();
            var port = body.Port ?? config.Current.ClamAvPort;
            if (string.IsNullOrEmpty(host)) return Results.BadRequest(new { error = "Bitte den Rechner des Virenscanners angeben." });
            var (ok, version, error) = await ClamAv.PingAsync(host, port, ct);
            if (!ok) return Results.Ok(new { ok = false, error });
            // Die harmlose EICAR-Testdatei muss als Fund gemeldet werden - sonst prüft der Scanner nicht wirklich
            var eicar = await ClamAv.ScanAsync(host, port, new MemoryStream(VirusScan.Eicar), TimeSpan.FromSeconds(20), ct);
            return Results.Ok(new { ok = eicar.Verdict == ClamAv.Verdict.Infected, version, eicar = eicar.Name, error = eicar.Verdict == ClamAv.Verdict.Infected ? null
                : eicar.Error ?? "Der Scanner hat die EICAR-Testdatei nicht erkannt." });
        });

        api.MapGet("/ai", (ServerConfig config) => AiState(config));
        api.MapPut("/ai", (AiBody body, ServerConfig config) =>
        {
            var provider = body.Provider == DmOffice.Chat.PukkiAi.OpenAi ? DmOffice.Chat.PukkiAi.OpenAi : DmOffice.Chat.PukkiAi.Anthropic;
            if (provider == DmOffice.Chat.PukkiAi.OpenAi && !string.IsNullOrWhiteSpace(body.Endpoint) && DmOffice.Chat.PukkiAi.EndpointProblem(body.Endpoint) is { } aiProblem)
                return Results.BadRequest(new { error = aiProblem });
            config.Update(s => { s.AiProvider = provider; s.AiEndpoint = body.Endpoint?.Trim(); s.AiModel = body.Model?.Trim(); s.AiWebSearch = body.WebSearch; s.AiLocation = body.Location?.Trim() is { Length: > 0 } l ? l : null; });
            if (body.ApiKey != null) config.SetAiApiKey(body.ApiKey.Trim());
            return Results.Ok(AiState(config));
        });
        // Probe: eine kurze Frage an die eingerichtete KI
        api.MapPost("/ai/test", async (ServerConfig config, ILoggerFactory logs, CancellationToken ct) =>
        {
            if (!DmOffice.Chat.PukkiAi.Configured(config)) return Results.BadRequest(new { error = "Die KI ist noch nicht eingerichtet." });
            var answer = await DmOffice.Chat.PukkiAi.Ask(config, "Sag in einem kurzen Satz Hallo.", [], DateTime.Now, logs.CreateLogger("DmOffice.PukkiAi"), ct);
            return answer == null ? Results.Json(new { error = "Keine Antwort - Schlüssel, Adresse und Modell prüfen (Einzelheiten im Protokoll des Servers)." }, statusCode: 502)
                : Results.Ok(new { answer });
        });

        api.MapPost("/users", (NewUser body, UserStore users) =>
        {
            var u = users.Create(body.Login, body.DisplayName, body.Password, body.IsAdmin);
            return Results.Ok(new { u.Id });
        });

        api.MapPut("/users/{id}", (string id, EditUser body, UserStore users, HttpContext http) =>
        {
            if (body.IsAdmin == false && id == http.User.FindFirstValue(ClaimTypes.NameIdentifier))
                return Results.BadRequest(new { error = "Sich selbst die Verwaltungsrechte entziehen geht nicht." });
            if (body.Password is { Length: > 0 and < 8 }) return Results.BadRequest(new { error = "Passwort: mindestens 8 Zeichen." });
            var before = users.ById(id);
            users.Update(id, u =>
            {
                if (!string.IsNullOrWhiteSpace(body.DisplayName)) u.DisplayName = body.DisplayName.Trim();
                if (!string.IsNullOrEmpty(body.Password)) { u.PasswordHash = Passwords.Hash(body.Password); u.NewStamp(); }
                if (body.IsAdmin is bool a && a != u.IsAdmin) { u.IsAdmin = a; u.NewStamp(); }
                if (body.DefaultFileQuota) u.FileQuotaMb = null;
                else if (body.FileQuotaMb is long q) u.FileQuotaMb = Math.Max(0, q);
                if (body.FilesOff is bool off) u.FilesOff = off;
                if (body.AiAllowed is bool ai) u.AiAllowed = ai;
            });
            // Tresor Stufe 1: die Person erfährt es sofort (Pukki, Mail) und es steht in ihrem Sicherheitsprotokoll
            var adminId = http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
            if (!string.IsNullOrEmpty(body.Password)) { SecurityNotice.ByAdmin(http.RequestServices, id, adminId, "Passwort neu gesetzt"); Tresor.AdminReset(id); CryptomatorPaths.LockAll(id); CookieSessions.EndAll(http.RequestServices, id); }
            if (before != null && body.IsAdmin is bool admin && admin != before.IsAdmin)
                SecurityNotice.ByAdmin(http.RequestServices, id, adminId, admin ? "Verwaltungsrecht erteilt" : "Verwaltungsrecht entzogen");
            return Results.Ok();
        });

        // Handy verloren und keine Wiederherstellungscodes: der Admin schaltet 2FA ab
        api.MapPost("/users/{id}/reset-2fa", (string id, UserStore users, HttpContext http) =>
        {
            users.Update(id, u => { u.TotpSecret = null; u.TotpPending = null; u.RecoveryCodes.Clear(); u.NewStamp(); });
            // Tresor schließt auf allen Geräten (Einstellung der Person, Vorgabe an)
            Tresor.AdminReset(id);
            SecurityNotice.ByAdmin(http.RequestServices, id, http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "", "Zwei-Faktor-Anmeldung abgeschaltet");
            return Results.Ok();
        });

        api.MapDelete("/users/{id}", (string id, HttpContext http) =>
        {
            if (id == http.User.FindFirstValue(ClaimTypes.NameIdentifier))
                return Results.BadRequest(new { error = "Das eigene Konto lässt sich hier nicht löschen." });
            DeleteUser(http.RequestServices, id);
            return Results.Ok();
        });
    }

    /// <summary>Benutzer samt Datenbank, Dateien und allen Freigaben loeschen.</summary>
    public static void DeleteUser(IServiceProvider services, string id)
    {
        // Id ist immer ein 32-stelliges Hex (Guid "N"). Streng pruefen, bevor daraus Dateipfade werden,
        // damit kein "\" oder ".." aus dem Benutzerordner herausfuehrt.
        if (services.GetRequiredService<UserStore>().ById(id) == null || id.Length != 32 || !id.All(Uri.IsHexDigit)) throw new KeyNotFoundException("Benutzer gibt es nicht.");
        Erase(services, id);
    }

    /// <summary>Alles eines Kontos entfernen. systemVault: false, wenn das Konto nicht mehr in users.json steht (seine Datenbank laesst sich dann nicht oeffnen).</summary>
    internal static void Erase(IServiceProvider services, string id, bool systemVault = true)
    {
        var users = services.GetRequiredService<UserStore>();
        var paths = services.GetRequiredService<DataPaths>();
        services.GetRequiredService<DmOffice.Pim.ShareStore>().Forget(id);
        services.GetRequiredService<DmOffice.Dav.FolderShareStore>().Forget(id);
        services.GetRequiredService<DmOffice.Emergency.EmergencyStore>().Forget(id);
        services.GetRequiredService<DmOffice.Gallery.AlbumStore>().Forget(id);
        services.GetRequiredService<DmOffice.Chat.ChatStore>().Forget(id);
        services.GetRequiredService<DmOffice.Notes.NoteShareStore>().Forget(id);
        services.GetRequiredService<DmOffice.Core.WebPush>().Forget(id);
        services.GetRequiredService<DmOffice.Dav.LinkIndex>().Forget(id);
        services.GetRequiredService<MailSync>().Forget(id);
        services.GetRequiredService<Spaces>().Forget(id);
        // Der Tresor geht mit (übergeben lässt er sich nicht)
        Tresor.Delete(id, systemVault);
        users.Delete(id);
        // Das Konto ist ab hier weg. Laeuft gerade noch ein Upload, sind seine Dateien kurz belegt: wiederholen, nicht als Fehler melden
        void Remove(Action action)
        {
            for (var i = 0; ; i++)
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                try { action(); return; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    if (i < 9) { Thread.Sleep(500); continue; }
                    services.GetRequiredService<ILoggerFactory>().CreateLogger("DmOffice.Admin").LogWarning("Konto {Id} entfernt, Daten noch nicht ganz: {Error}", id, e.GetType().Name);
                    return;
                }
            }
        }
        foreach (var f in new[] { "", "-wal", "-shm" })
            Remove(() => { if (File.Exists(paths.UserDb(id) + f)) File.Delete(paths.UserDb(id) + f); });
        // Dateiinhalte (BlobStore) gehoeren dazu
        Remove(() => { if (Directory.Exists(paths.UserBlobs(id))) Directory.Delete(paths.UserBlobs(id), recursive: true); });
        // auch die Kopien aus den Sicherungen vor Datenaenderungen
        Remove(() => PreUpdateBackup.ForgetUser(paths, id));
        // Am Ziel direkt danach: Storage Extender und Files-Backup, nur der Ordner dieses Kontos
        Remove(() => ErasedUsers.Note(paths, id));
        services.GetService<ErasedUsersJob>()?.Kick();
    }

    private static string? TargetProblem(string target, string? endpoint, string? webDavUrl, string? folder, ServerConfig config, DataPaths paths)
    {
        if (target == "s3" && !string.IsNullOrWhiteSpace(endpoint) && !(Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var u) && u.Scheme is "http" or "https"))
            return "Die Adresse muss mit http:// oder https:// beginnen.";
        if (target == "webdav" && !string.IsNullOrWhiteSpace(webDavUrl) && !(Uri.TryCreate(webDavUrl.Trim(), UriKind.Absolute, out var wu) && wu.Scheme is "http" or "https"))
            return "Die WebDAV-Adresse muss mit http:// oder https:// beginnen.";
        if (target == "folder" && !string.IsNullOrWhiteSpace(folder))
        {
            if (!Path.IsPathFullyQualified(folder.Trim()) || !Directory.Exists(folder.Trim()))
                return "Den Ordner gibt es auf dem Server nicht – im Container einbinden (etwa /external/backup) und den vollen Pfad angeben.";
            if (Overlaps(folder, paths.Root) || Overlaps(folder, paths.Blobs))
                return "Das Datenverzeichnis von dmOffice taugt nicht als Ziel – einen eigenen Ordner wählen.";
            if (config.Current.ExternalFolders.FirstOrDefault(f => Overlaps(folder, f.Path)) is { } ext)
                return $"Der Ordner überschneidet sich mit dem externen Ordner „{ext.Name}“ – darüber ließen sich die Sicherungen löschen. Einen eigenen Ordner wählen.";
        }
        return null;
    }

    private static object StorageState(ServerConfig config, S3Mirror mirror, S3Backup backup, FilesBackup files)
    {
        var s = config.Current;
        return new { mirror = s.S3Mirror, endpoint = s.S3Endpoint, region = s.S3Region, bucket = s.S3Bucket, prefix = s.S3Prefix, accessKey = s.S3AccessKey,
            target = s.StorageTarget, webDavUrl = s.StorageWebDavUrl, webDavUser = s.StorageWebDavUser, hasWebDavPassword = !string.IsNullOrEmpty(config.StorageWebDavPassword()), folder = s.StorageFolder,
            hasSecret = !string.IsNullOrEmpty(config.S3SecretKey()), configured = S3Mirror.Configured(s, config.S3SecretKey()), status = mirror.Status, notHome = mirror.NotHomeRecent() is var (count, bytes) ? new { count, bytes } : null,
            backup = new { enabled = s.S3Backup, at = s.S3BackupAt ?? "03:00", keepCount = BackupRules.KeepCount(s), everyHours = s.S3BackupEveryHours, blocked = backup.Blocked(), status = backup.Status,
                overdue = s.S3Backup && backup.Status.LastRun is { } ran && BackupRules.Overdue(s, ran, DateTime.UtcNow), holdDeletes = S3Backup.HoldDeletes(s, paths: files.Paths, DateTime.UtcNow), nextRun = s.S3Backup ? S3Backup.NextRun(s, DateTime.Now, backup.Status.LastRun) : (DateTime?)null,
                own = s.BackupOwnTarget, target = s.BackupTarget, endpoint = s.BackupS3Endpoint, region = s.BackupS3Region, bucket = s.BackupS3Bucket, prefix = s.BackupS3Prefix,
                accessKey = s.BackupS3AccessKey, hasSecret = !string.IsNullOrEmpty(config.BackupS3SecretKey()), webDavUrl = s.BackupWebDavUrl, webDavUser = s.BackupWebDavUser,
                hasWebDavPassword = !string.IsNullOrEmpty(config.BackupWebDavPassword()), folder = s.BackupFolder, configured = S3Backup.TargetConfigured(config) },
            files = new { enabled = BackupRules.FilesOn(s), wanted = s.FilesBackup, own = s.FilesBackupOwnTarget, minDays = BackupRules.MinHoldDays(s), days = s.FilesBackupDays, effectiveDays = BackupRules.HoldDays(s), forever = s.FilesBackupForever,
                target = s.FilesBackupTarget, endpoint = s.FilesBackupS3Endpoint, region = s.FilesBackupS3Region, bucket = s.FilesBackupS3Bucket, prefix = s.FilesBackupS3Prefix, accessKey = s.FilesBackupS3AccessKey,
                hasSecret = !string.IsNullOrEmpty(config.FilesBackupS3SecretKey()), webDavUrl = s.FilesBackupWebDavUrl, webDavUser = s.FilesBackupWebDavUser, hasWebDavPassword = !string.IsNullOrEmpty(config.FilesBackupWebDavPassword()), folder = s.FilesBackupFolder,
                configured = FilesBackup.Configured(config), status = files.Status, restore = files.Restore },
            profile = new { at = s.StorageProfileAt, current = s.StorageProfileStamp != null && s.StorageProfileStamp == StorageProfile.Stamp(config) },
            keepDeletedDays = S3Mirror.KeepDeleted(s) == TimeSpan.MaxValue ? 0 : (int)S3Mirror.KeepDeleted(s).TotalDays, atTargetBytes = mirror.AtTargetTotal().Bytes, limited = S3Mirror.Limited(s), cacheTargetGb = s.S3CacheTargetGb, minFreeGb = Math.Round(S3Mirror.ReserveOf(s, mirror.LocalVolume().Total) / (1024.0 * 1024 * 1024)), rescued = mirror.Rescued,
            volume = mirror.LocalVolume() is var (total, free) && total > 0 ? new { total, free } : null,
            targetSpace = mirror.TargetSpace() is { } ext ? new { total = ext.Total, free = ext.Free, ours = mirror.AtTargetTotal().Bytes } : null };
    }

    private static object AiState(ServerConfig config)
    {
        var s = config.Current;
        return new { provider = s.AiProvider, endpoint = s.AiEndpoint, model = s.AiModel, webSearch = s.AiWebSearch, location = s.AiLocation, hasKey = !string.IsNullOrEmpty(config.AiApiKey()),
            configured = DmOffice.Chat.PukkiAi.Configured(config), claudeModels = DmOffice.Chat.PukkiAi.ClaudeModels };
    }

    /// <summary>
    /// Im Container eingebundene Ordner (Volumes, Bind-Mounts) aus /proc/self/mountinfo - ohne Systemordner. Außerhalb von Linux
    /// (Entwicklung unter Windows): die Laufwerke.
    /// </summary>
    private static List<string> MountPoints()
    {
        const string info = "/proc/self/mountinfo";
        if (!File.Exists(info)) return Directory.GetLogicalDrives().ToList();
        string[] system = ["/proc", "/sys", "/dev", "/etc", "/run", "/tmp", "/usr", "/lib", "/bin", "/sbin", "/var", "/boot", "/root", "/app", "/opt", "/home/app"];
        return File.ReadLines(info)
            .Select(l => l.Split(' '))
            .Where(f => f.Length > 4)
            .Select(f => f[4].Replace("\\040", " "))
            .Where(p => p != "/" && !system.Any(x => p == x || p.StartsWith(x + "/")))
            // Regel: externe Ordner im Container unter /external/<Name> - die stehen vorn
            .Distinct().OrderBy(p => p.StartsWith("/external/") ? 0 : 1).ThenBy(p => p, StringComparer.Ordinal).ToList();
    }


    /// <summary>Größe zum Lesen: „12,3 GB“.</summary>
    private static string Size(long b) => b >= 1L << 30 ? $"{b / (double)(1L << 30):0.#} GB" : b >= 1L << 20 ? $"{b / (double)(1L << 20):0.#} MB" : $"{Math.Max(1, b / 1024):0} KB";


    /// <summary>Liegt der eine Ordner im anderen (oder sind sie gleich)?</summary>
    private static bool Overlaps(string a, string b)
    {
        static string Norm(string p) => Path.GetFullPath(p.Trim()).TrimEnd(Path.DirectorySeparatorChar);
        var x = Norm(a); var y = Norm(b);
        return x == y || x.StartsWith(y + Path.DirectorySeparatorChar) || y.StartsWith(x + Path.DirectorySeparatorChar);
    }

}
