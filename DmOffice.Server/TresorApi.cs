using System.Security.Claims;
using DmOffice.Core;

namespace DmOffice;

/// <summary>
/// Tresor (docs/tresor-konzept.md): einrichten, öffnen und schließen je Gerät, Geräte mit offenem Tresor, Tresor-Passwort ändern,
/// Wiederherstellung, Einstellungen, löschen. Inhalte (Dateien, notizi, Gesichter) kommen in eigenen Endpunkten dazu und holen
/// ihre Datenbank über <see cref="Tresor.Db"/> - geschlossen antwortet der Server dann mit 423 { locked: "tresor" }.
/// Die Weboberfläche ruft /api/tresor direkt auf (nicht im Umschlag), damit das Cookie des Tresors ankommt.
/// </summary>
public static class TresorApi
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string User, DateTimeOffset Expires)> DownloadTokens = new();

    public sealed record SetupBody(string Password, string? Name);
    public sealed record OpenBody(string? Password, string? Code, string? DeviceId, string? Secret, bool? Remember, string? Name);
    public sealed record PasswordBody(string Current, string Password);
    public sealed record SettingsBody(string? AutoClose, bool? CloseOnAdminReset, bool? CloseOnScreenLock, bool? Dav = null);
    public sealed record DeleteBody(string Password);
    public sealed record DeviceBody(string? Name);
    public sealed record PasskeyBody(string CredentialId, string Salt, string Secret, string? Name);

    private static UserRecord Me(HttpContext http, UserStore users) =>
        users.ById(http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "") ?? throw new UnauthorizedAccessException();

    private static string FailKey(UserRecord u) => "tresor:" + u.Id;

    /// <summary>Heikles nur kurz nach dem Öffnen mit Passwort, Code oder Passkey.</summary>
    private static IResult NotFresh() =>
        Results.Json(new { error = "Dazu den Tresor schließen und mit dem Tresor-Passwort neu öffnen.", fresh = false }, statusCode: 403);

    private static IResult Closed() =>
        Results.Json(new { error = "Bitte zuerst den Tresor öffnen.", locked = Tresor.Locked }, statusCode: StatusCodes.Status423Locked);

    private static object State(HttpContext http, string userId)
    {
        var m = Tresor.Meta(userId);
        if (m == null) return new { exists = false, user = userId };
        return new
        {
            exists = true,
            // Kennung der Person: der Browser merkt sich ein Gerät je Konto
            user = userId,
            open = Tresor.IsOpen(http, touch: false),
            openAnywhere = Tresor.OpenAnywhere(userId),
            settings = m.Settings,
            codesLeft = m.Wraps.Count(w => w.Kind == TresorWrap.Code),
            // Noch nicht im Cryptomator-Format: wird beim nächsten Öffnen mit dem Tresor-Passwort umgestellt (nicht per Passkey)
            oldFormat = m.Vault.Length == 0,
            // Für Cryptomator am Rechner fehlt masterkey.cryptomator (umgestellt ohne Passwort)
            needsPassword = Tresor.MasterkeyFileMissing(userId),
            // Belegung: dazu der Cryptomator-Tresor in der Ablage (zählt dort schon zum Kontingent)
            size = Tresor.Size(userId) + (http.Space().Db is var sdb && Dav.FileStore.Get(sdb, Dav.FileStore.SystemVault) is { } sv ? Dav.FileStore.TreeSize(sdb, sv) : 0),
            pending = Tresor.Pending(userId),
            devices = Tresor.OpenDevices(http, userId),
            // Gemerkte Geräte und Fingerabdruck (ohne Geheimnis)
            remembered = m.Wraps.Where(w => w.Kind is TresorWrap.Remember or TresorWrap.Biometric).Select(w => new { w.Id, w.Kind, w.Name, w.Created }),
            // Passkeys: Kennung und Salz braucht der Browser schon zum Öffnen (beides nicht geheim)
            passkeys = m.Wraps.Where(w => w.Kind == TresorWrap.Passkey).Select(w => new { w.Id, w.Name, w.Created, w.CredentialId, salt = w.PrfSalt }),
        };
    }

    /// <summary>Mindestens 10 Zeichen und nicht nur ein Zeichen wiederholt: das Passwort schützt auch masterkey.cryptomator in
    /// jeder Sicherung, die lässt sich ohne Bremse durchprobieren.</summary>
    private static bool PasswordOk(string? password) => password is { Length: >= 10 } p && p.Distinct().Count() >= 4;

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api/tresor").RequireAuthorization();

        api.MapGet("/", (HttpContext http, UserStore users) => State(http, Me(http, users).Id));

        // Einrichten: Tresor-Passwort festlegen; zurück kommen zehn Codes und der Wiederherstellungsschlüssel (nur dieses eine Mal so)
        api.MapPost("/setup", async (SetupBody body, HttpContext http, UserStore users) =>
        {
            var u = Me(http, users);
            if (Tresor.Exists(u.Id)) return Results.BadRequest(new { error = "Es ist schon ein Tresor eingerichtet." });
            if (!PasswordOk(body.Password)) return Results.BadRequest(new { error = "Das Tresor-Passwort braucht mindestens 10 Zeichen, nicht nur Wiederholungen." });
            if (Passwords.Verify(body.Password, u.PasswordHash)) return Results.BadRequest(new { error = "Bitte ein anderes Passwort als das zum Anmelden nehmen." });
            var created = await Tresor.CreateAsync(u.Id, body.Password, http.RequestAborted);
            Tresor.Grant(http, u.Id, created.Key, body.Name);
            Tresor.MarkFresh(http, Tresor.ByPassword);
            SecurityNotice.Own(http.RequestServices, u.Id, "Tresor eingerichtet");
            return Results.Ok(new { recoveryCodes = created.Codes, recoveryKey = created.RecoveryKey });
        });

        // Öffnen: Tresor-Passwort, Wiederherstellungscode/-schlüssel oder gemerktes Gerät (Fingerabdruck, „nie schließen“)
        api.MapPost("/open", async (OpenBody body, HttpContext http, UserStore users) =>
        {
            var u = Me(http, users);
            if (!Tresor.Exists(u.Id)) return Results.BadRequest(new { error = "Es ist kein Tresor eingerichtet." });
            var key = FailKey(u);
            if (Auth.Blocked(key)) return Results.Json(new { error = "Zu viele Versuche, bitte kurz warten." }, statusCode: 429);
            byte[]? tresorKey = null;
            if (body.Password is { Length: > 0 } pw) tresorKey = await Tresor.KeyFromPasswordAsync(u.Id, pw, http.RequestAborted);
            else if (body.Code is { Length: > 0 } code) tresorKey = Tresor.KeyFromCode(u.Id, code);
            else if (body.DeviceId is { Length: > 0 } id && body.Secret is { Length: > 0 } secret)
                try { tresorKey = Tresor.KeyFromDevice(u.Id, id, Convert.FromBase64String(secret)); } catch (FormatException) { }
            if (tresorKey == null)
            {
                Auth.Failed(key, "Tresor-Passwort");
                await Task.Delay(500);
                // Gemerktes Gerät, das es nicht mehr gibt (vergessen, Zurücksetzen durch die Verwaltung): eigens melden, das Gerät vergisst es dann
                if (body.DeviceId is { Length: > 0 }) return Results.BadRequest(new { error = "Dieses Gerät ist für den Tresor nicht mehr hinterlegt.", forgotten = true });
                return Results.BadRequest(new { error = body.Code is { Length: > 0 } ? "Code falsch oder schon benutzt." : "Tresor-Passwort falsch." });
            }
            Auth.Succeeded(key);
            // Tresor von vor dem Cryptomator-Format: jetzt zum Cryptomator-Tresor, auch per Passkey oder gemerktem Gerät; ohne Passwort
            // fehlt dann noch masterkey.cryptomator - das kommt beim ersten Öffnen mit dem Passwort dazu
            var typed = body.Password is { Length: > 0 } p ? p : null;
            await Tresor.UpgradeAsync(u.Id, tresorKey, typed, http.RequestAborted);
            if (typed != null) Tresor.EnsureMasterkeyFile(u.Id, tresorKey, typed);
            Tresor.Grant(http, u.Id, tresorKey, body.Name);
            if (typed != null) Tresor.MarkFresh(http, Tresor.ByPassword);
            else if (body.Code is { Length: > 0 }) Tresor.MarkFresh(http, Tresor.ByCode);
            else if (body.DeviceId is { Length: > 0 } did && Tresor.KindOfWrap(u.Id, did) == TresorWrap.Passkey) Tresor.MarkFresh(http, Tresor.ByPasskey);
            else Tresor.MarkFresh(http, null);
            object? device = null;
            if (body.Remember == true && Tresor.Meta(u.Id)!.Settings.AutoClose == TresorSettings.Never)
            {
                var (wrapId, s) = Tresor.Remember(u.Id, tresorKey, TresorWrap.Remember, body.Name);
                device = new { id = wrapId, secret = s };
            }
            return Results.Ok(new { codesLeft = Tresor.CodesLeft(u.Id), usedCode = body.Code is { Length: > 0 }, device });
        });

        // App: Fingerabdruck oder Gesicht hinterlegen - das Geheimnis verwahrt die App hinter der Biometrie
        api.MapPost("/biometric", (DeviceBody body, HttpContext http, UserStore users) =>
        {
            var u = Me(http, users);
            if (!Tresor.OpenedFresh(http)) return NotFresh();
            var (id, secret) = Tresor.Remember(u.Id, Tresor.KeyFor(http), TresorWrap.Biometric, body.Name);
            SecurityNotice.Own(http.RequestServices, u.Id, "Tresor: Fingerabdruck oder Gesicht hinterlegt");
            return Results.Ok(new { id, secret });
        });

        // Dieses Gerät merken (nur bei „nie schließen“): öffnet den Tresor nach einem Neustart des Servers von selbst
        api.MapPost("/remember", (DeviceBody body, HttpContext http, UserStore users) =>
        {
            var u = Me(http, users);
            if (!Tresor.OpenedFresh(http)) return NotFresh();
            var (id, secret) = Tresor.Remember(u.Id, Tresor.KeyFor(http), TresorWrap.Remember, body.Name);
            return Results.Ok(new { id, secret });
        });

        // Passkey hinterlegen (nur mit offenem Tresor): Geheimnis aus der PRF-Erweiterung, dazu Kennung und Salz
        api.MapPost("/passkey", (PasskeyBody body, HttpContext http, UserStore users) =>
        {
            var u = Me(http, users);
            if (!Tresor.OpenedFresh(http)) return NotFresh();
            byte[] salt, secret;
            try { salt = Convert.FromBase64String(body.Salt); secret = Convert.FromBase64String(body.Secret); }
            catch (FormatException) { return Results.BadRequest(new { error = "Der Passkey hat keinen brauchbaren Schlüssel geliefert." }); }
            var id = Tresor.AddPasskey(u.Id, Tresor.KeyFor(http), body.CredentialId, salt, secret, body.Name);
            SecurityNotice.Own(http.RequestServices, u.Id, "Tresor: Passkey hinterlegt");
            return Results.Ok(new { id });
        });

        // Gemerktes Gerät oder Fingerabdruck entfernen - geht auch bei geschlossenem Tresor (nimmt nur einen Weg weg)
        api.MapDelete("/remembered/{id}", (string id, HttpContext http, UserStore users) =>
        {
            Tresor.Forget(Me(http, users).Id, id);
            return Results.Ok();
        });

        api.MapPost("/close", (HttpContext http) => { Tresor.Close(http); return Results.Ok(); });

        // Stufe 7b: Offline-Kopie in der App (docs/tresor-cryptomator-konzept.md, 4). Online entschlüsselt der Server; ohne Netz
        // entschlüsselt die App selbst aus ihrer verschlüsselten Kopie (Tresor-Passwort). Welche verschlüsselten Dateien zu einem
        // Ordner gehören, weiß nur der offene Tresor - die App bekommt die Liste samt ETags und holt die Dateien einzeln.
        api.MapGet("/vault/offline", (string path, HttpContext http) =>
        {
            if (Tresor.Db(http).Files.Storage is not CryptomatorTresorStorage s)
                return Results.NotFound(new { error = "Der Tresor liegt noch nicht im Cryptomator-Format vor." });
            if (!TresorPaths.Is(path) || s.Vault.OfflineFiles(path.Length > 7 ? path[7..] : "", TresorFileStore.Internal) is not { } files)
                return Results.NotFound(new { error = "Den Ordner gibt es im Tresor nicht." });
            var db = http.Space().Db;
            return Results.Ok(new { items = files.Distinct().Select(f => Dav.FileStore.Get(db, FileStoreVaultFiles.DefaultRoot + "/" + f)).OfType<Dav.FileEntry>()
                .Select(e => new { path = e.Path[(FileStoreVaultFiles.DefaultRoot.Length + 1)..], size = e.Size, etag = e.Etag }) });
        });

        // Eine verschlüsselte Datei - unabhängig vom Schalter für Nextcloud und auch bei geschlossenem Tresor (ohne Schlüssel
        // unlesbar). Pfade relativ zum Cryptomator-Tresor.
        static string? VaultPath(string? rel)
        {
            var parts = (rel ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Any(p => p is "." or ".." || p.Contains('\\'))) return null;
            return parts.Length == 0 ? FileStoreVaultFiles.DefaultRoot : FileStoreVaultFiles.DefaultRoot + "/" + string.Join('/', parts);
        }
        api.MapGet("/vault/file", (string path, HttpContext http) =>
        {
            var db = http.Space().Db;
            if (VaultPath(path) is not { } p || Dav.FileStore.Get(db, p) is not { IsDir: false } f) return Results.NotFound();
            http.Response.Headers.ETag = f.Etag;
            http.Response.Headers.CacheControl = "no-store";
            return Results.Stream(Dav.FileStore.OpenRead(db, p), "application/octet-stream");
        });

        // „Tresor herunterladen“ (docs/tresor-cryptomator-konzept.md, 9.1): der System-Tresor, wie er ist - verschlüsselt im
        // Cryptomator-Format, im Web als ZIP ohne Kompression (Methode „Stored“), gestreamt. Größe und Grenze vorher.
        api.MapGet("/download", (HttpContext http, ServerConfig config, UserStore users) =>
        {
            // Offene Datenbank zuerst zurückschreiben, sonst fehlt sie in der Größe
            try { Tresor.Flush(Me(http, users).Id, force: true); } catch (Exception) { }
            var db = http.Space().Db;
            var root = Dav.FileStore.Get(db, FileStoreVaultFiles.DefaultRoot);
            return Results.Ok(new { available = root != null, size = root == null ? 0 : Dav.FileStore.TreeSize(db, root), limit = Dav.FilesApi.ZipLimit(config) });
        });

        // Passwort prüfen; dafür gibt es ein Einmal-Token (2 Minuten) für den Download. So geht das Passwort im Umschlag der App
        // und nie über die Adresse, und Browser wie App laden das ZIP mit einem einfachen GET.
        api.MapPost("/download/check", async (DeleteBody body, HttpContext http, UserStore users) =>
        {
            var u = Me(http, users);
            var key = FailKey(u);
            if (Auth.Blocked(key)) return Results.Json(new { error = "Zu viele Versuche, bitte kurz warten." }, statusCode: 429);
            if (body.Password is not { Length: > 0 } pw || await Tresor.KeyFromPasswordAsync(u.Id, pw, http.RequestAborted) == null)
            {
                Auth.Failed(key, "Tresor-Passwort");
                return Results.BadRequest(new { error = "Tresor-Passwort falsch." });
            }
            Auth.Succeeded(key);
            var now = DateTimeOffset.UtcNow;
            foreach (var (k, v) in DownloadTokens.ToArray()) if (v.Expires < now) DownloadTokens.TryRemove(k, out _);
            var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
            DownloadTokens[token] = (u.Id, now.AddMinutes(2));
            return Results.Ok(new { token });
        });

        // format=tar für die App: Javas ZipInputStream liest gestreamte ZIP-Einträge ohne Kompression nicht (Datendeskriptor)
        api.MapGet("/download/zip", async (string token, string? format, HttpContext http, ServerConfig config, UserStore users) =>
        {
            var u = Me(http, users);
            if (!DownloadTokens.TryRemove(token ?? "", out var t) || t.User != u.Id || t.Expires < DateTimeOffset.UtcNow)
                return Results.BadRequest(new { error = "Bitte erneut mit dem Tresor-Passwort bestätigen." });
            var db = http.Space().Db;
            if (Dav.FileStore.Get(db, FileStoreVaultFiles.DefaultRoot) is not { } root) return Results.NotFound(new { error = "Der Tresor liegt noch nicht im Cryptomator-Format vor." });
            // Stimmiger Stand: offene Datenbank zurückschreiben
            try { Tresor.Flush(u.Id, force: true); } catch (Exception) { }
            var limit = Dav.FilesApi.ZipLimit(config);
            if (limit > 0 && Dav.FileStore.TreeSize(db, root) > limit)
                return Results.Json(new { error = "Größer als die Grenze für ZIP-Downloads - bitte über den Datenexport mit WebDAV-Ziel." }, statusCode: 413);
            var tar = format == "tar";
            http.Response.ContentType = tar ? "application/x-tar" : "application/zip";
            http.Response.Headers.ContentDisposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("attachment") { FileNameStar = tar ? "vault.tar" : "vault.zip" }.ToString();
            http.Response.Headers.CacheControl = "no-store";
            if (http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpBodyControlFeature>() is { } body) body.AllowSynchronousIO = true;
            await http.Response.StartAsync();
            await using var buffered = new BufferedStream(http.Response.Body, 256 * 1024);
            if (tar)
            {
                using (var writer = new System.Formats.Tar.TarWriter(buffered, System.Formats.Tar.TarEntryFormat.Pax, leaveOpen: true))
                {
                    var prefix = FileStoreVaultFiles.DefaultRoot.Length + 1;
                    foreach (var f in Dav.FileStore.Descendants(db, FileStoreVaultFiles.DefaultRoot).OrderBy(f => f.Path, StringComparer.Ordinal))
                    {
                        http.RequestAborted.ThrowIfCancellationRequested();
                        var name = "vault/" + f.Path[prefix..];
                        var entry = new System.Formats.Tar.PaxTarEntry(f.IsDir ? System.Formats.Tar.TarEntryType.Directory : System.Formats.Tar.TarEntryType.RegularFile, name) { ModificationTime = f.Modified };
                        using var s = f.IsDir ? null : Dav.FileStore.OpenRead(db, f.Path);
                        if (s != null) entry.DataStream = s;
                        writer.WriteEntry(entry);
                    }
                }
                await buffered.FlushAsync();
                return Results.Empty;
            }
            using (var zip = new System.IO.Compression.ZipArchive(buffered, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            {
                var prefix = FileStoreVaultFiles.DefaultRoot.Length + 1;
                foreach (var f in Dav.FileStore.Descendants(db, FileStoreVaultFiles.DefaultRoot).OrderBy(f => f.Path, StringComparer.Ordinal))
                {
                    http.RequestAborted.ThrowIfCancellationRequested();
                    var name = "vault/" + f.Path[prefix..];
                    if (f.IsDir) { zip.CreateEntry(name + "/"); continue; }
                    var entry = zip.CreateEntry(name, System.IO.Compression.CompressionLevel.NoCompression);
                    entry.LastWriteTime = f.Modified;
                    using var target = entry.Open();
                    using var s = Dav.FileStore.OpenRead(db, f.Path);
                    s.CopyTo(target);
                }
            }
            await buffered.FlushAsync();
            return Results.Empty;
        });

        // Ein anderes Gerät schließen (z. B. gestohlenes Handy) oder alle
        api.MapPost("/close/{device}", (string device, HttpContext http, UserStore users) =>
            Tresor.CloseDevice(Me(http, users).Id, device) ? Results.Ok() : Results.NotFound(new { error = "Auf diesem Gerät ist der Tresor nicht mehr offen." }));
        api.MapPost("/close-all", (HttpContext http, UserStore users) => { Tresor.CloseAll(Me(http, users).Id); return Results.Ok(); });

        // Tresor-Passwort ändern: altes und neues; der Inhalt bleibt, nur die Hülle wird neu
        api.MapPost("/password", async (PasswordBody body, HttpContext http, UserStore users) =>
        {
            var u = Me(http, users);
            if (!Tresor.Exists(u.Id)) return Results.BadRequest(new { error = "Es ist kein Tresor eingerichtet." });
            if (!PasswordOk(body.Password)) return Results.BadRequest(new { error = "Das Tresor-Passwort braucht mindestens 10 Zeichen, nicht nur Wiederholungen." });
            if (Passwords.Verify(body.Password, u.PasswordHash)) return Results.BadRequest(new { error = "Bitte ein anderes Passwort als das zum Anmelden nehmen." });
            var key = FailKey(u);
            if (Auth.Blocked(key)) return Results.Json(new { error = "Zu viele Versuche, bitte kurz warten." }, statusCode: 429);
            // Nach einem Wiederherstellungscode kennt man das alte nicht mehr: dann reicht der eben mit dem Code geöffnete Tresor
            var tresorKey = body.Current is { Length: > 0 } cur ? await Tresor.KeyFromPasswordAsync(u.Id, cur, http.RequestAborted)
                : Tresor.OpenedFresh(http, Tresor.ByCode) ? Tresor.KeyFor(http) : null;
            if (tresorKey == null)
            {
                Auth.Failed(key, "Tresor-Passwort");
                return Results.BadRequest(new { error = "Bisheriges Tresor-Passwort falsch." });
            }
            await Tresor.ChangePasswordAsync(u.Id, tresorKey, body.Password, http.RequestAborted);
            SecurityNotice.Own(http.RequestServices, u.Id, "Tresor-Passwort geändert");
            return Results.Ok();
        });

        // Neue Wiederherstellungscodes (die alten verfallen) und den Wiederherstellungsschlüssel erneut zeigen - nur mit offenem Tresor
        api.MapPost("/codes", (HttpContext http, UserStore users) =>
        {
            var u = Me(http, users);
            if (!Tresor.IsOpen(http)) return Closed();
            if (!Tresor.OpenedFresh(http)) return NotFresh();
            var codes = Tresor.NewCodes(u.Id, Tresor.KeyFor(http));
            SecurityNotice.Own(http.RequestServices, u.Id, "Tresor: neue Wiederherstellungscodes");
            return Results.Ok(new { recoveryCodes = codes });
        });
        api.MapPost("/recovery-key", (HttpContext http, UserStore users) =>
        {
            var u = Me(http, users);
            if (!Tresor.IsOpen(http)) return Closed();
            if (!Tresor.OpenedFresh(http)) return NotFresh();
            var key = Tresor.NewRecoveryKey(u.Id, Tresor.KeyFor(http));
            SecurityNotice.Own(http.RequestServices, u.Id, "Tresor: neuer Wiederherstellungsschlüssel");
            return Results.Ok(new { recoveryKey = key });
        });
        api.MapGet("/recovery-key", (HttpContext http, UserStore users) =>
        {
            var u = Me(http, users);
            if (!Tresor.IsOpen(http)) return Closed();
            if (!Tresor.OpenedFresh(http)) return NotFresh();
            return Results.Ok(new { recoveryKey = Tresor.RecoveryKey(u.Id, Tresor.KeyFor(http)) });
        });

        api.MapPut("/settings", (SettingsBody body, HttpContext http, UserStore users) =>
        {
            var u = Me(http, users);
            if (!Tresor.IsOpen(http)) return Closed();
            Tresor.ChangeSettings(u.Id, body.AutoClose, body.CloseOnAdminReset, body.CloseOnScreenLock, body.Dav);
            // Sichtbar oder unsichtbar über WebDAV: neue ETags bis zur Wurzel, sonst fragt der Nextcloud-Client nicht neu nach
            if (body.Dav != null) Dav.FileStore.Touch(http.Space().Db, Dav.FileStore.SystemVault);
            return Results.Ok(State(http, u.Id));
        });

        // Tresor löschen: der ganze Inhalt endgültig - nur mit dem Tresor-Passwort
        api.MapDelete("/", async (HttpContext http, UserStore users) =>
        {
            var u = Me(http, users);
            var body = await http.Request.ReadFromJsonAsync<DeleteBody>(http.RequestAborted);
            if (!Tresor.Exists(u.Id)) return Results.BadRequest(new { error = "Es ist kein Tresor eingerichtet." });
            var key = FailKey(u);
            if (Auth.Blocked(key)) return Results.Json(new { error = "Zu viele Versuche, bitte kurz warten." }, statusCode: 429);
            if (body?.Password is not { Length: > 0 } pw || await Tresor.KeyFromPasswordAsync(u.Id, pw, http.RequestAborted) == null)
            {
                Auth.Failed(key, "Tresor-Passwort");
                return Results.BadRequest(new { error = "Tresor-Passwort falsch." });
            }
            Tresor.Delete(u.Id);
            SecurityNotice.Own(http.RequestServices, u.Id, "Tresor gelöscht");
            return Results.Ok();
        });
    }
}
