using System.Text.Json;
using System.Text.Json.Nodes;
using DmOffice.Core;

namespace DmOffice.Storage;

/// <summary>
/// Server-Mounts (Verwaltung → Speicher → Server-Mounts) in jeder Art, die dmOffice als Treiber kennt: Pfad im Container wie bisher,
/// dazu WebDAV, SFTP, OneDrive und pCloud (Schalter <see cref="Features.MountDrivers"/>). Freigabe je Benutzer, „Nur lesen“, Suche,
/// Auswahl der obersten Ebene und der gemeinsame Vorschau-Speicher der Galerie hängen an der Kennung und gelten für jede Art.
/// Passwörter, Schlüssel und Token liegen mit dem Hauptschlüssel eingepackt in der Server-Konfiguration und gehen nie an den Browser.
/// Ist der Schalter aus, wirken nur Pfade im Container; andere Einträge bleiben in der Konfiguration stehen.
/// </summary>
public static class ServerMounts
{
    public const string Folder = "folder";
    public static readonly string[] Kinds = [Folder, UserMount.WebDav, UserMount.OneDrive, UserMount.PCloud];

    /// <summary>Art des Server-Mounts; ohne Angabe (so gespeichert, bevor es andere Arten gab) ein Pfad im Container.</summary>
    public static string KindOf(ExternalFolderDef f) => string.IsNullOrEmpty(f.Kind) ? Folder : f.Kind;
    public static bool IsFolder(ExternalFolderDef f) => KindOf(f) == Folder;

    /// <summary>Die wirksamen Server-Mounts: ohne den Schalter nur Pfade im Container.</summary>
    public static IEnumerable<ExternalFolderDef> Active(ServerSettings s) =>
        s.ExternalFolders.Where(f => IsFolder(f) || Features.On(Features.MountDrivers));

    /// <summary>Treiber eines Server-Mounts. Adressen werden nicht eingeschränkt - Server-Mounts legt nur der Admin an.</summary>
    public static IStorageDriver Open(ServerConfig config, ExternalFolderDef f)
    {
        switch (KindOf(f))
        {
            case Folder:
                return new FolderDriver(f.Path);
            case UserMount.WebDav:
                return RemoteDrivers.Open(f.Url ?? "", f.User, config.Unwrap(f.Password));
            case UserMount.OneDrive:
                var id = f.Id;
                var refresh = config.Unwrap(f.RefreshToken);
                return new OneDriveDriver(ct => ExternalFolders.TokenAsync(TokenKey(id), refresh, (_, previous, fresh) => Rotated(config, id, previous, fresh), ct),
                    ExternalFolders.GraphHttp, f.Drive ?? "", f.Item ?? "root");
            case UserMount.PCloud:
                return new PCloudDriver(PCloudFolders.Http, config.Unwrap(f.Token) ?? "", f.Host ?? "", f.Folder ?? "") { Wait = PCloudFolders.Wait };
            default:
                throw new KeyNotFoundException("Diese Art von Server-Mount gibt es nicht.");
        }
    }

    /// <summary>Wo der Mount liegt (ohne Zugangsdaten). Ändert sich das, baut die Suche ihren Index neu auf.</summary>
    public static string Location(ExternalFolderDef f) => KindOf(f) switch
    {
        Folder => f.Path,
        UserMount.OneDrive => "onedrive|" + f.Drive + "|" + f.Item,
        UserMount.PCloud => "pcloud|" + f.Host + "|" + f.Folder,
        var k => k + "|" + f.Url + "|" + f.User,
    };

    private static string TokenKey(string id) => "server-mount:" + id;

    /// <summary>Microsoft hat das Refresh-Token ausgetauscht: das neue speichern, wenn noch das bisherige eingetragen ist.</summary>
    public static void Rotated(ServerConfig config, string id, string previous, string fresh)
    {
        if (config.Current.ExternalFolders.FirstOrDefault(x => x.Id == id) is not { } now || config.Unwrap(now.RefreshToken) != previous) return;
        config.Update(s =>
        {
            if (s.ExternalFolders.FirstOrDefault(x => x.Id == id) is { } d && config.Unwrap(d.RefreshToken) == previous) d.RefreshToken = config.Wrap(fresh);
        });
    }

    private static readonly string[] Secrets = ["password", "refreshToken", "token", "flow"];

    /// <summary>Für die Verwaltung: alles außer Passwort, Schlüssel und Token - davon nur, ob es sie gibt.</summary>
    public static JsonObject View(ExternalFolderDef f)
    {
        var o = JsonSerializer.SerializeToNode(f, JsonSerializerOptions.Web)!.AsObject();
        foreach (var k in Secrets) o.Remove(k);
        o["kind"] = KindOf(f);
        o["passwordSet"] = !string.IsNullOrEmpty(f.Password);
        o["signedIn"] = !string.IsNullOrEmpty(f.RefreshToken) || !string.IsNullOrEmpty(f.Token);
        return o;
    }

    /// <summary>Verbindung prüfen (auflisten; fehlt der Ordner, wird er angelegt) - in Tests ersetzbar.</summary>
    public static Func<ExternalFolderDef, ServerConfig, CancellationToken, Task> Check { get; set; } = async (f, config, ct) =>
    {
        using var d = Open(config, f);
        try { await d.ListAsync("", ct); }
        catch (KeyNotFoundException) when (KindOf(f) == UserMount.WebDav) { await d.MkdirAsync("", ct); await d.ListAsync("", ct); }
    };

    /// <summary>
    /// Einen Server-Mount aus der Verwaltung zum Speichern vorbereiten (old: der gespeicherte mit derselben Kennung). Geheimnisse
    /// kommen nie aus dem Browser: ein neues Passwort ersetzt das gespeicherte, sonst bleibt es; Token nur aus einer Anmeldung
    /// (Flow) des Admins. Neue oder geänderte Zugänge werden gleich geprüft. done: nach dem Speichern auszuführen (Anmeldung
    /// vergessen). Gibt eine Fehlermeldung zurück oder null.
    /// </summary>
    public static async Task<string?> PrepareAsync(ExternalFolderDef f, ExternalFolderDef? old, ServerConfig config, string adminId,
        DmOffice.Mail.MicrosoftOAuth? oauth, List<Action> done, CancellationToken ct)
    {
        var kind = KindOf(f);
        if (!Kinds.Contains(kind)) return $"„{f.Name}“: diese Art gibt es nicht.";
        if (old != null && KindOf(old) != kind) old = null;
        var flow = f.Flow;
        var secret = f.Password;
        f.Flow = null;
        f.Password = old?.Password; f.RefreshToken = old?.RefreshToken; f.Token = old?.Token;
        if (kind == Folder)
        {
            f.Kind = null;
            f.Url = f.User = f.Password = f.RefreshToken = f.Drive = f.Item = f.Token = f.Host = f.Folder = f.Account = null;
            return null;
        }
        if (!Features.On(Features.MountDrivers)) return $"„{f.Name}“: diese Art ist in dieser Version nicht freigeschaltet.";
        f.Kind = kind;
        f.Path = "";
        var check = old == null;
        switch (kind)
        {
            case UserMount.WebDav:
                f.Url = (f.Url ?? "").Trim();
                f.User = f.User?.Trim() is { Length: > 0 } u ? u : null;
                if (!RemoteDrivers.ValidUrl(f.Url)) return $"„{f.Name}“: {RemoteDrivers.UrlProblem}";
                if (!string.IsNullOrEmpty(secret)) f.Password = config.Wrap(secret);
                else if (old != null && !string.IsNullOrEmpty(old.Password) && (!ExternalFolders.SameHost(old.Url ?? "", f.Url) || old.User != f.User))
                    return $"„{f.Name}“: für eine neue Adresse oder einen anderen Benutzer bitte das Passwort eingeben.";
                check |= old != null && (old.Url != f.Url || old.User != f.User || !string.IsNullOrEmpty(secret));
                f.RefreshToken = f.Drive = f.Item = f.Token = f.Host = f.Folder = f.Account = null;
                break;
            case UserMount.OneDrive:
                f.Url = f.User = f.Password = f.Token = f.Host = null;
                if (!string.IsNullOrEmpty(flow))
                {
                    if (oauth?.Enabled != true || oauth.FilesGrant(adminId, flow) is not { } refresh) return $"„{f.Name}“: erst bei Microsoft anmelden.";
                    try
                    {
                        var drive = await ExternalFolders.Browse(refresh).MyDriveAsync(ct);
                        var item = string.IsNullOrEmpty(f.Item) ? "root" : f.Item;
                        await ExternalFolders.Browse(refresh, drive.Id, item).ListAsync("", ct);
                        var where = string.IsNullOrWhiteSpace(f.Folder) ? "OneDrive" : "OneDrive/" + f.Folder.Trim().Trim('/');
                        f.RefreshToken = config.Wrap(refresh); f.Drive = drive.Id; f.Item = item;
                        f.Account = drive.Owner is { Length: > 0 } o ? o + " · " + where : where;
                    }
                    catch (Exception e) when (e is not OperationCanceledException) { return $"„{f.Name}“: Verbindung fehlgeschlagen: " + e.GetBaseException().Message; }
                    done.Add(() => oauth.Forget(flow));
                }
                else if (old == null || string.IsNullOrEmpty(old.RefreshToken)) return $"„{f.Name}“: erst bei Microsoft anmelden.";
                else { f.Drive = old.Drive; f.Item = old.Item; f.Folder = old.Folder; f.Account = old.Account; }
                check = false;
                break;
            case UserMount.PCloud:
                f.Url = f.User = f.Password = f.RefreshToken = f.Drive = f.Item = null;
                if (!string.IsNullOrEmpty(flow))
                {
                    if (PCloudFolders.Peek(flow, adminId) is not { } signedIn) return $"„{f.Name}“: erst bei pCloud anmelden.";
                    f.Token = config.Wrap(signedIn.Token); f.Host = signedIn.Host; f.Folder = StoragePath.Clean(f.Folder);
                    var where = f.Folder.Length == 0 ? "pCloud" : "pCloud/" + f.Folder;
                    f.Account = signedIn.Account is { Length: > 0 } a ? a + " · " + where : where;
                    check = true;
                    done.Add(() => PCloudFolders.Forget(flow));
                }
                else if (old == null || string.IsNullOrEmpty(old.Token)) return $"„{f.Name}“: erst bei pCloud anmelden.";
                else { f.Host = old.Host; f.Folder = old.Folder; f.Account = old.Account; check = false; }
                break;
        }
        f.Include = (f.Include ?? []).Where(n => n.Length > 0 && n.IndexOfAny(['/', '\\']) < 0 && n is not ("." or "..")).Distinct().ToList();
        if (check)
        {
            try { await Check(f, config, ct); }
            catch (Exception e) when (e is not OperationCanceledException) { return $"„{f.Name}“: Verbindung fehlgeschlagen: " + e.GetBaseException().Message; }
        }
        // Anderer Ort: von der Auswahl der obersten Ebene (und den Ordnern für Inhalte) bleibt nur, was es dort gibt
        if (old != null && Location(old) != Location(f) && (f.Include.Count > 0 || f.ContentFolders.Count > 0))
        {
            try
            {
                using var d = Open(config, f);
                var dirs = (await d.ListAsync("", ct)).Where(e => e.IsDir).Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
                f.Include = f.Include.Where(dirs.Contains).ToList();
                f.ContentFolders = f.ContentFolders.Where(dirs.Contains).ToList();
            }
            catch (Exception e) when (e is not OperationCanceledException) { f.Include = []; f.ContentFolders = []; }
        }
        return null;
    }

    private static IResult? Refused(UserSpace space, ServerConfig config) =>
        Features.On(Features.MountDrivers) ? null : Results.NotFound();

    private static IResult? OneDriveRefused(UserSpace space, ServerConfig config) =>
        Refused(space, config) ?? (ExternalFolders.OAuth?.Enabled != true
            ? Results.Json(new { error = "Microsoft-Anmeldung ist auf diesem Server nicht eingerichtet (DMOFFICE_MS_CLIENT_ID)." }, statusCode: 404) : null);

    /// <summary>Verwaltung: anmelden bei Microsoft und pCloud (wie bei den eigenen Zielen) und Ordner der obersten Ebene eines Server-Mounts.</summary>
    public static void MapAdmin(RouteGroupBuilder admin)
    {
        var api = admin.MapGroup("/external/server");
        ExternalFolders.MapOneDriveSignIn(api, OneDriveRefused);
        PCloudFolders.MapSignIn(api.MapGroup("/pcloud"), Refused);

        // Ordner der obersten Ebene eines gespeicherten Server-Mounts (für „In dmOffice erscheinen“ und die Suche)
        api.MapGet("/{id}/top", async (string id, ServerConfig config, CancellationToken ct) =>
        {
            if (Active(config.Current).FirstOrDefault(f => f.Id == id) is not { } f) return Results.NotFound();
            try
            {
                using var d = Open(config, f);
                var dirs = (await d.ListAsync("", ct)).Where(e => e.IsDir).Select(e => e.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                return Results.Ok(new { dirs });
            }
            catch (Exception e) when (e is not OperationCanceledException) { return Results.BadRequest(new { error = "Nicht erreichbar: " + e.GetBaseException().Message }); }
        });
    }
}
