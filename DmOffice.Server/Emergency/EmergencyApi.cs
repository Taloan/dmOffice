using System.Security.Claims;
using DmOffice.Core;
using DmOffice.Dav;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace DmOffice.Emergency;

/// <summary>Notfallzugang einrichten, anfragen, widersprechen, zuruecknehmen - und das Konto im Notfall oeffnen.</summary>
public static class EmergencyApi
{
    /// <summary>Sitzung im Konto des Besitzers ueber einen gewaehrten Notfallzugang - Wert: Kennung der Anfrage.</summary>
    public const string SessionClaim = "dm:emergency";

    /// <param name="Content">Notfalltext oder Dateien kommen gleich dazu (zählt wie Inhalt, auch wenn noch nichts gespeichert ist).</param>
    public record PackageBody(string? Id, string Name, List<long>? Folders, bool Account, bool Admin, List<string>? Requesters,
        List<string>? ExtraVetoers, List<string>? NoVeto, int WaitDays, bool Content = false);
    public record TextBody(string Html);
    public record FilesBody(List<long> Ids);
    public record CodeBody(string Code);

    /// <summary>Falsche Notfallcodes je Person und Paket - nach fünf Fehlversuchen eine Stunde Pause.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int N, DateTime Since)> Tries = new();
    private const int MaxTries = 5;

    private static EmergencyPackage? Own(HttpContext http, EmergencyStore store, string id) =>
        store.Package(id) is { } p && p.Owner == http.Space().User.Id ? p : null;

    /// <summary>Paket, für das <paramref name="me"/> eine gewährte Anfrage hat.</summary>
    private static EmergencyPackage? Granted(EmergencyStore store, string id, string me) =>
        store.Package(id) is { } p && store.RequestsOf(id).Any(r => r.Requester == me && r.State == EmergencyRequest.Granted) ? p : null;

    /// <summary>Privater Schlüssel aus dem Notfallcode - null: falsch oder zu viele Fehlversuche.</summary>
    private static byte[]? Unlock(EmergencyPackage p, string me, string code, EmergencyStore store)
    {
        var k = me + "/" + p.Id;
        if (Tries.TryGetValue(k, out var t) && t.N >= MaxTries && DateTime.UtcNow - t.Since < TimeSpan.FromHours(1)) return null;
        var key = EmergencyContent.PrivateFromCode(p, code ?? "");
        if (key != null) { Tries.TryRemove(k, out _); return key; }
        Tries.AddOrUpdate(k, (1, DateTime.UtcNow), (_, o) => DateTime.UtcNow - o.Since > TimeSpan.FromHours(1) ? (1, DateTime.UtcNow) : (o.N + 1, o.Since));
        store.Logged(p.Id, null, me, "Falscher Notfallcode");
        return null;
    }

    private static IResult WrongCode(string package, string me) =>
        Results.Json(new
        {
            error = Tries.TryGetValue(me + "/" + package, out var t) && t.N >= MaxTries
                ? "Zu viele falsche Notfallcodes – in einer Stunde wieder." : "Der Notfallcode stimmt nicht.",
        }, statusCode: 403);

    /// <summary>Was eine Notfallsitzung nicht darf: Zugang und Sicherheit des Besitzers aendern, den Notfallzugang selbst verwalten, die Verwaltung.</summary>
    public static bool Blocked(string path, string method)
    {
        var p = path.TrimEnd('/');
        var get = HttpMethods.IsGet(method) || HttpMethods.IsHead(method);
        if (Starts(p, "/api/admin") || Starts(p, "/api/auth/2fa") || Starts(p, "/api/auth/password")) return true;
        // Kein Umschlag (E2E): er liefe mit dem Stempel des Kontos weiter - ohne diese Sperren und ueber ein Zuruecknehmen hinaus
        if (Starts(p, "/api/e2e/register")) return true;
        if (!get && (Starts(p, "/api/emergency") || Starts(p, "/api/settings/app-passwords"))) return true;
        // Notfallcode und Notfalltext des Besitzers nicht über eine Notfallsitzung
        if (Starts(p, "/api/emergency/packages") && (p.EndsWith("/code", StringComparison.OrdinalIgnoreCase) || p.EndsWith("/text", StringComparison.OrdinalIgnoreCase))) return true;
        // Nichts, was nach dem Zurücknehmen weiter Daten nach außen gibt: Weiterleitungen, Datensicherung, externe Ziele, Links
        if (!get && (Starts(p, "/api/settings/rules") || Starts(p, "/api/plainbackup") || Starts(p, "/api/files/external") || Starts(p, "/api/files/links")
            || Starts(p, "/api/gallery/albums") && p.Contains("/links"))) return true;
        return false;
    }

    // Ein verlorenes Handy (App-Passwort) darf anfragen und widersprechen, aber die Notfallpakete nicht aendern
    private static bool ViaDevice(HttpContext http) => http.User.HasClaim(c => c.Type == Auth.DeviceClaim);
    private const string DeviceError = "Notfallzugänge richtest du im Browser ein.";

    private static bool Starts(string p, string prefix) =>
        p.Equals(prefix, StringComparison.OrdinalIgnoreCase) || p.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);

    public static void Use(WebApplication app) => app.Use(async (ctx, next) =>
    {
        if (ctx.User.FindFirst(SessionClaim) != null && Blocked(ctx.Request.Path, ctx.Request.Method))
        {
            ctx.Response.StatusCode = 403;
            await ctx.Response.WriteAsJsonAsync(new { error = "Im Notfallzugang nicht erlaubt." });
            return;
        }
        await next();
    });

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api/emergency").RequireAuthorization();

        // Fuer das rote Abzeichen oben: offene Anfragen, bei denen ich widersprechen darf; dazu, ob dies eine Notfallsitzung ist
        api.MapGet("/badge", (HttpContext http, EmergencyStore store, EmergencyService svc) =>
        {
            var me = http.Space().User.Id;
            var session = http.User.FindFirstValue(SessionClaim) is { } rid && store.Request(rid) is { } r ? new { requester = svc.Name(r.Requester), owner = svc.Name(me) } : null;
            return new { count = session != null ? 0 : store.ToDecide(me).Count, session };
        });

        api.MapGet("", (HttpContext http, EmergencyStore store, EmergencyService svc, FolderShareStore shares, EmergencyContent content) =>
        {
            var space = http.Space();
            var me = space.User.Id;
            object Req(EmergencyRequest r) => new
            {
                r.Id, r.Requester, requesterName = svc.Name(r.Requester), r.State, r.Created, r.Due, r.Decided,
                decidedByName = r.DecidedBy == null ? null : svc.Name(r.DecidedBy),
            };
            var mine = store.OwnedBy(me).Select(content.Refresh).Select(p =>
            {
                var fromShares = p.Folders.SelectMany(f => shares.Of(me, f)?.Members ?? new List<string>()).ToHashSet();
                return new
                {
                    p.Id, p.Name, p.Account, p.Admin, p.WaitDays, p.Requesters, p.ExtraVetoers, p.NoVeto,
                    folders = p.Folders.Select(f => new { id = f, path = FileStore.ById(space.Db, f)?.Path }),
                    hasKey = p.PublicKey != null, p.HasText, p.TextChanged,
                    files = p.Files.Select(f => new { f.Id, f.Name, f.Path, f.Size, f.Copied, f.Missing }),
                    vetoers = store.Vetoers(p).Where(v => v != me).Select(v => new { id = v, name = svc.Name(v), fromShare = fromShares.Contains(v) }),
                    // Herausgenommene aus den Freigaben - zum Wieder-Hinzunehmen
                    shareMembers = fromShares.Select(v => new { id = v, name = svc.Name(v) }),
                    requests = store.RequestsOf(p.Id).Take(20).Select(Req),
                    log = store.LogOf(p.Id).Take(30).Select(l => new { l.At, who = l.Who == "dmoffice" ? "dmOffice" : svc.Name(l.Who), l.What }),
                };
            });
            var forMe = store.RequestableBy(me).Select(p => new
            {
                p.Id, p.Name, ownerName = svc.Name(p.Owner), p.WaitDays, content = svc.Content(p), p.Account,
                sealedContent = p.PublicKey != null && (p.HasText || p.Files.Count > 0),
                request = store.RequestsOf(p.Id).Where(r => r.Requester == me).Select(Req).FirstOrDefault(),
            });
            var toDecide = store.ToDecide(me).Select(r => store.Package(r.Package) is { } p ? new
            {
                r.Id, packageName = p.Name, ownerName = svc.Name(p.Owner), own = p.Owner == me, requesterName = svc.Name(r.Requester), r.Due, content = svc.Content(p),
            } : null).Where(x => x != null);
            return new { mine, forMe, toDecide };
        });

        // Gewählte Ordner (Kennungen aus dem Ordnerbrowser): Pfad, ob teilbar, und mit wem schon geteilt (sie dürfen widersprechen).
        // Ohne ids: wie bisher alle eigenen Ordner bis drei Ebenen tief
        api.MapGet("/folders", (string? ids, HttpContext http, FolderShareStore shares) =>
        {
            var space = http.Space();
            var db = space.Db;
            var list = new List<object>();
            if (ids != null)
            {
                foreach (var id in ids.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => long.TryParse(x, out var n) ? n : 0).Where(n => n > 0).Distinct())
                    if (FileStore.ById(db, id) is { IsDir: true } f)
                        list.Add(new { id = f.FileId, path = f.Path, shareable = SharedFiles.Shareable(db, f), members = shares.Of(space.User.Id, f.FileId)?.Members ?? new List<string>() });
                return list;
            }
            void Walk(string path, int depth)
            {
                foreach (var f in FileStore.Children(db, path).Where(f => f.IsDir).OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
                {
                    if (!SharedFiles.Shareable(db, f)) continue;
                    // Mitglieder der Freigabe: sie duerfen spaeter widersprechen
                    list.Add(new { id = f.FileId, path = f.Path, members = shares.Of(space.User.Id, f.FileId)?.Members ?? new List<string>() });
                    if (depth < 3) Walk(f.Path, depth + 1);
                }
            }
            Walk("", 1);
            return list;
        });

        api.MapPut("/packages", (PackageBody body, HttpContext http, EmergencyStore store) =>
        {
            if (ViaDevice(http)) return Results.Json(new { error = DeviceError }, statusCode: 403);
            var space = http.Space();
            // Nur Rechte bis zur eigenen Ebene (Admin-Rechte nur, wer selbst Administrator ist)
            if (!EmergencyStore.MayGrant(space.User, body.Admin))
                return Results.Json(new { error = "Im Notfallpaket lassen sich nur Rechte bis zur eigenen Ebene weitergeben." }, statusCode: 403);
            var folders = (body.Folders ?? new()).Distinct().ToList();
            foreach (var f in folders)
                if (FileStore.ById(space.Db, f) is not { } dir || !SharedFiles.Shareable(space.Db, dir))
                    return Results.BadRequest(new { error = "Diesen Ordner gibt es nicht oder er lässt sich nicht teilen." });
            var before = body.Id is { Length: > 0 } bid ? store.Package(bid) : null;
            var p = store.Save(space.User.Id, new EmergencyPackage
            {
                Id = body.Id ?? "", Name = body.Name, Folders = folders, Account = body.Account, Admin = body.Admin,
                Requesters = body.Requesters ?? new(), ExtraVetoers = body.ExtraVetoers ?? new(), NoVeto = body.NoVeto ?? new(), WaitDays = body.WaitDays,
                // Nur für die Prüfung „etwas freigegeben“: Text und Dateien haben eigene Wege
                HasText = body.Content || before?.HasText == true, Files = before?.Files ?? new(),
            });
            return Results.Ok(new { p.Id });
        });

        api.MapDelete("/packages/{id}", (string id, HttpContext http, EmergencyStore store, EmergencyService svc, EmergencyContent content) =>
        {
            if (ViaDevice(http)) return Results.Json(new { error = DeviceError }, statusCode: 403);
            var me = http.Space().User.Id;
            var p = store.Package(id);
            var granted = p == null ? new List<EmergencyRequest>() : store.RequestsOf(id).Where(r => r.State == EmergencyRequest.Granted).ToList();
            store.Delete(me, id);
            content.Forget(id);
            foreach (var r in granted) if (store.Request(r.Id) is { } done) svc.Revoked(p!, done);
            return Results.Ok();
        });

        // ---------------- Notfallcode, Notfalltext, Referenzkopien (Besitzer) ----------------

        // Schlüssel anlegen: Paketschlüssel und Notfallcode in den Tresor (offen); der Code geht an die Seite zum Ausdrucken
        api.MapPost("/packages/{id}/keys", (string id, HttpContext http, EmergencyContent content) =>
        {
            if (ViaDevice(http)) return Results.Json(new { error = DeviceError }, statusCode: 403);
            var code = content.CreateKeys(Tresor.Db(http), http.Space().User.Id, id);
            return Results.Ok(new { code });
        });
        // Notfallcode noch einmal zeigen (zum Ausdrucken) - nur aus dem offenen Tresor
        api.MapGet("/packages/{id}/code", (string id, HttpContext http, EmergencyStore store) =>
        {
            if (Own(http, store, id) is not { } p) return Results.NotFound(new { error = "Notfallpaket nicht gefunden." });
            var k = EmergencyContent.OwnerKeyOf(Tresor.Db(http), p.Id);
            return k == null ? Results.NotFound(new { error = "Für dieses Paket ist im Tresor kein Notfallcode hinterlegt." }) : Results.Ok(new { code = EmergencyVault.Format(k.Code) });
        });
        api.MapGet("/packages/{id}/text", (string id, HttpContext http, EmergencyStore store, EmergencyContent content) =>
        {
            if (Own(http, store, id) is not { } p) return Results.NotFound(new { error = "Notfallpaket nicht gefunden." });
            if (!p.HasText) return Results.Ok(new { html = "" });
            return Results.Ok(new { html = content.ReadText(p, content.PrivateForOwner(Tresor.Db(http), p)) });
        });
        api.MapPut("/packages/{id}/text", (string id, TextBody body, HttpContext http, EmergencyStore store, EmergencyContent content) =>
        {
            if (ViaDevice(http)) return Results.Json(new { error = DeviceError }, statusCode: 403);
            if (Own(http, store, id) is not { } p) return Results.NotFound(new { error = "Notfallpaket nicht gefunden." });
            content.WriteText(p.Owner, p, body.Html);
            return Results.Ok();
        });
        api.MapPost("/packages/{id}/files", (string id, FilesBody body, HttpContext http, EmergencyContent content) =>
        {
            if (ViaDevice(http)) return Results.Json(new { error = DeviceError }, statusCode: 403);
            var me = http.Space().User.Id;
            var failed = new List<string>();
            foreach (var f in body.Ids.Distinct())
                try { content.AddFile(me, id, f); }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException or KeyNotFoundException or IOException) { failed.Add(e.Message); }
            return Results.Ok(new { failed });
        });
        api.MapDelete("/packages/{id}/files/{file}", (string id, string file, HttpContext http, EmergencyContent content) =>
        {
            if (ViaDevice(http)) return Results.Json(new { error = DeviceError }, statusCode: 403);
            content.RemoveFile(http.Space().User.Id, id, file);
            return Results.Ok();
        });

        // ---------------- Nach der Freigabe (Vertrauensperson) ----------------

        // Notfalltext und Dateiliste: Frist abgelaufen (gewährt) und der Notfallcode stimmt
        api.MapPost("/packages/{id}/open", (string id, CodeBody body, HttpContext http, EmergencyStore store, EmergencyContent content) =>
        {
            var me = http.Space().User.Id;
            if (Granted(store, id, me) is not { } p) return Results.NotFound(new { error = "Kein gewährter Notfallzugang." });
            if (Unlock(p, me, body.Code, store) is not { } key) return WrongCode(p.Id, me);
            p = content.Refresh(p);
            return Results.Ok(new
            {
                html = content.ReadText(p, key),
                files = p.Files.Select(f => new { f.Id, f.Name, f.Size, f.Copied, f.Missing }),
            });
        });
        // Datei herunterladen - der Code steht im Kopf der Anfrage, nicht in der Adresse
        api.MapGet("/packages/{id}/files/{file}", (string id, string file, HttpContext http, EmergencyStore store, EmergencyContent content) =>
        {
            var me = http.Space().User.Id;
            if (Granted(store, id, me) is not { } p) return Results.NotFound(new { error = "Kein gewährter Notfallzugang." });
            if (Unlock(p, me, http.Request.Headers["X-Notfallcode"].ToString(), store) is not { } key) return WrongCode(p.Id, me);
            var f = p.Files.FirstOrDefault(x => x.Id == file);
            if (f == null) return Results.NotFound(new { error = "Diese Datei gibt es im Paket nicht." });
            store.Logged(p.Id, null, me, $"Datei „{f.Name}“ heruntergeladen");
            return Results.File(content.ReadFile(p, file, key), DavServer.Mime(f.Name), f.Name);
        });

        // Seite fuer den Link: was es ist, ob ich anfragen darf, wie es steht
        api.MapGet("/packages/{id}", (string id, HttpContext http, EmergencyStore store, EmergencyService svc) =>
        {
            var me = http.Space().User.Id;
            if (store.Package(id) is not { } p || !p.Requesters.Contains(me)) return Results.NotFound(new { error = "Diesen Notfallzugang gibt es nicht oder er ist nicht für dich." });
            var r = store.RequestsOf(id).FirstOrDefault(x => x.Requester == me);
            return Results.Ok(new
            {
                p.Id, p.Name, ownerName = svc.Name(p.Owner), p.WaitDays, content = svc.Content(p), p.Account,
                request = r == null ? null : new { r.Id, r.State, r.Created, r.Due, r.Decided, decidedByName = r.DecidedBy == null ? null : svc.Name(r.DecidedBy) },
            });
        });

        api.MapPost("/packages/{id}/request", (string id, HttpContext http, EmergencyStore store, EmergencyService svc) =>
        {
            var r = store.Ask(id, http.Space().User.Id);
            svc.Asked(store.Package(id)!, r);
            return Results.Ok(new { r.Id, r.Due });
        });

        api.MapPost("/requests/{id}/deny", (string id, HttpContext http, EmergencyStore store, EmergencyService svc) =>
        {
            var r = store.Deny(id, http.Space().User.Id);
            svc.Denied(store.Package(r.Package)!, r);
            return Results.Ok();
        });

        api.MapPost("/requests/{id}/withdraw", (string id, HttpContext http, EmergencyStore store) =>
        {
            store.Withdraw(id, http.Space().User.Id);
            return Results.Ok();
        });

        api.MapPost("/requests/{id}/revoke", (string id, HttpContext http, EmergencyStore store, EmergencyService svc) =>
        {
            var before = store.Request(id);
            var r = store.Revoke(id, http.Space().User.Id);
            var p = store.Package(r.Package)!;
            if (before?.State == EmergencyRequest.Open) svc.Denied(p, r); else svc.Revoked(p, r);
            return Results.Ok();
        });

        // Gewaehrter Zugang zum ganzen Konto: diese Sitzung wird zur Sitzung im Konto des Besitzers (Sicherheit bleibt gesperrt).
        // Unter /api/auth: setzt ein Cookie - laeuft darum nie durch den Umschlag, und Geraete duerfen es nicht
        app.MapPost("/api/auth/emergency/{id}", async (string id, HttpContext http, EmergencyStore store, EmergencyService svc, UserStore users) =>
        {
            var me = http.Space().User.Id;
            if (store.Request(id) is not { State: EmergencyRequest.Granted } r || r.Requester != me
                || store.Package(r.Package) is not { Account: true } p || users.ById(p.Owner) is not { } owner)
                return Results.NotFound(new { error = "Kein gewährter Zugang zu diesem Konto." });
            var principal = Auth.Principal(owner, CookieAuthenticationDefaults.AuthenticationScheme);
            ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(SessionClaim, r.Id));
            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal,
                new AuthenticationProperties { IsPersistent = false, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(12) });
            store.Logged(p.Id, r.Id, me, "Konto geöffnet");
            svc.Entered(p, r);
            return Results.Ok();
        }).RequireAuthorization();
    }
}
