using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using DmOffice.Core;

namespace DmOffice.Storage;

/// <summary>
/// pCloud als externer Ordner: Anmeldung nach OAuth 2 (Code-Flow, https://docs.pcloud.com/methods/oauth_2.0/authorize.html)
/// mit der eigenen pCloud-App des Benutzers (Client-ID und Secret, in seiner verschlüsselten Datenbank). Ablauf:
/// start liefert die Adresse bei pCloud; nach der Zustimmung kommt der Browser an callback zurück, der Server tauscht den
/// Code beim Server der Region des Kontos gegen ein Token (pCloud-Token laufen nicht ab und haben kein Refresh). Dann
/// einen Ordner wählen und einbinden - Token und Region landen im Ordner in der verschlüsselten Datenbank des Benutzers,
/// der Browser sieht das Token nie.
/// </summary>
public static class PCloudFolders
{
    public const string AuthorizeUrl = "https://my.pcloud.com/oauth2/authorize";
    public const string CallbackPath = "/api/files/external/pcloud/callback";
    /// <summary>Die pCloud-App des Benutzers, in seiner verschlüsselten Datenbank.</summary>
    public const string AppKey = "files.pcloud.app";

    public sealed class PCloudApp
    {
        public string ClientId { get; set; } = "";
        public string Secret { get; set; } = "";
    }

    /// <summary>Verbindung zu pCloud - in Tests ersetzbar.</summary>
    public static HttpClient Http { get; set; } = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(30) };
    /// <summary>Warten bei Drosselung - in Tests ersetzbar.</summary>
    public static Func<TimeSpan, CancellationToken, Task> Wait { get; set; } = Task.Delay;

    /// <summary>Eine laufende Anmeldung: wem sie gehört, und nach dem Rücksprung Token und Server oder ein Fehler.</summary>
    private sealed class Flow
    {
        public required string UserId { get; init; }
        public required string Redirect { get; init; }
        public required string ClientId { get; init; }
        public required string Secret { get; init; }
        public DateTime Expires { get; init; } = DateTime.UtcNow.AddMinutes(30);
        public string? Token { get; set; }
        public string? Host { get; set; }
        public string? Account { get; set; }
        public string? Error { get; set; }
    }

    private static readonly ConcurrentDictionary<string, Flow> Flows = new();

    /// <summary>pCloud wird angeboten: der Administrator erlaubt es.</summary>
    public static bool Offered(ServerConfig config) => config.Current.ExternalPCloudForUsers;

    private static IResult? Refused(UserSpace space, ServerConfig config) =>
        !config.Current.ExternalPCloudForUsers ? Results.Json(new { error = "Eigene pCloud-Ordner sind nicht freigeschaltet." }, statusCode: 403) : null;

    public static PCloudApp? AppOf(UserSpace space) => space.Db.GetJson<PCloudApp?>(AppKey) is { ClientId.Length: > 0, Secret.Length: > 0 } a ? a : null;

    /// <summary>Rücksprungadresse, wie sie in der pCloud-App eingetragen sein muss.</summary>
    public static string RedirectFor(HttpRequest request) => $"{request.Scheme}://{request.Host}{request.PathBase}{CallbackPath}";

    /// <summary>Neue Anmeldung: Kennung (zugleich state) und Adresse bei pCloud.</summary>
    public static (string Flow, string Url) Start(PCloudApp app, string userId, string redirect)
    {
        foreach (var (key, old) in Flows)
            if (old.Expires < DateTime.UtcNow) Flows.TryRemove(key, out _);
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        Flows[state] = new Flow { UserId = userId, Redirect = redirect, ClientId = app.ClientId, Secret = app.Secret };
        var url = AuthorizeUrl + "?client_id=" + Uri.EscapeDataString(app.ClientId) + "&response_type=code"
                  + "&redirect_uri=" + Uri.EscapeDataString(redirect) + "&state=" + state;
        return (state, url);
    }

    /// <summary>Rücksprung von pCloud: Code beim Server der Region gegen ein Token tauschen.</summary>
    public static async Task<bool> CallbackAsync(string? state, string? code, string? hostname, string? locationId, string? error, CancellationToken ct)
    {
        if (state == null || !Flows.TryGetValue(state, out var flow) || flow.Expires < DateTime.UtcNow || flow.Token != null) return false;
        if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code))
        {
            flow.Error = error == "access_denied" ? "Die Anmeldung bei pCloud wurde abgebrochen." : "pCloud hat keine Anmeldung zurückgegeben.";
            return false;
        }
        try
        {
            var host = PCloudDriver.HostFor(hostname, locationId);
            using var res = await Http.PostAsync($"https://{host}/oauth2_token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = flow.ClientId,
                ["client_secret"] = flow.Secret,
                ["code"] = code,
            }), ct);
            using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var json = doc.RootElement;
            var result = json.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetInt32() : -1;
            if (result != 0 || !json.TryGetProperty("access_token", out var t) || t.GetString() is not { Length: > 0 } token)
            {
                // Meist falsche Client-ID, falsches Secret oder ein schon benutzter Code
                flow.Error = result is >= 4000 and < 6000
                    ? PCloudDriver.Error(result, null, "Anmeldung").Message
                    : $"pCloud lehnt die Anmeldung ab (Fehler {result}) - Client-ID und Client-Secret prüfen.";
                return false;
            }
            var driver = new PCloudDriver(Http, token, host, "") { Wait = Wait };
            flow.Account = await driver.AccountAsync(ct);
            flow.Host = host;
            flow.Token = token;
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            flow.Error = e is ArgumentException or JsonException ? "Unerwartete Antwort von pCloud." : "Anmeldung bei pCloud fehlgeschlagen: " + e.GetBaseException().Message;
            return false;
        }
    }

    private static Flow? Mine(string? flow, string userId) =>
        flow != null && Flows.TryGetValue(flow, out var f) && f.UserId == userId && f.Expires > DateTime.UtcNow ? f : null;

    /// <summary>Treiber für einen eingebundenen pCloud-Ordner.</summary>
    public static PCloudDriver Driver(UserMount m) => new(Http, m.Token ?? "", m.Host ?? "", m.Folder ?? "") { Wait = Wait };

    /// <summary>Replace: ein vorhandenes pCloud-Ziel neu anmelden (Id bleibt) statt ein neues anzulegen.</summary>
    public record AddBody(string Flow, string Name, string? Path, string? Replace = null);
    /// <summary>Client-ID und Secret der eigenen pCloud-App: leer = die gespeicherten nehmen.</summary>
    public record StartBody(string? ClientId, string? ClientSecret);

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api/files/external/pcloud").RequireAuthorization();

        // Die eigene pCloud-App: Client-ID und ob ein Secret gespeichert ist (das Secret selbst geht nicht zurück),
        // dazu die Rücksprungadresse, die in der App bei pCloud stehen muss
        api.MapGet("/app", (HttpContext http, ServerConfig config) =>
        {
            var space = http.Space();
            if (Refused(space, config) is { } refused) return refused;
            var app = space.Db.GetJson<PCloudApp?>(AppKey);
            return Results.Ok(new { clientId = app?.ClientId ?? "", secretSet = app?.Secret is { Length: > 0 }, redirect = RedirectFor(http.Request) });
        });

        api.MapPost("/start", (StartBody? body, HttpContext http, ServerConfig config) =>
        {
            var space = http.Space();
            if (Refused(space, config) is { } refused) return refused;
            // Neu eingetragene Werte gleich speichern; ein leeres Secret behält das gespeicherte
            var id = body?.ClientId?.Trim();
            var secret = body?.ClientSecret?.Trim();
            if (!string.IsNullOrEmpty(id) || !string.IsNullOrEmpty(secret))
                lock (space.Db)
                {
                    var app = space.Db.GetJson<PCloudApp?>(AppKey) ?? new PCloudApp();
                    if (!string.IsNullOrEmpty(id)) app.ClientId = id;
                    if (!string.IsNullOrEmpty(secret)) app.Secret = secret;
                    space.Db.SetJson(AppKey, app);
                }
            if (AppOf(space) is not { } mine) return Results.BadRequest(new { error = "Bitte Client-ID und Client-Secret der eigenen pCloud-App eintragen." });
            var (flow, url) = Start(mine, space.User.Id, RedirectFor(http.Request));
            return Results.Ok(new { flow, url });
        });

        // Abgefragt, bis der Rücksprung da ist
        api.MapGet("/status", (string flow, HttpContext http) =>
        {
            var f = Mine(flow, http.Space().User.Id);
            if (f == null) return Results.NotFound(new { error = "Anmeldung abgelaufen, bitte noch einmal." });
            return Results.Ok(new { state = f.Token != null ? "ok" : f.Error != null ? "error" : "pending", error = f.Error, account = f.Account });
        });

        api.MapGet("/folders", async (string flow, string? path, HttpContext http, ServerConfig config, CancellationToken ct) =>
        {
            var space = http.Space();
            if (Refused(space, config) is { } refused) return refused;
            if (Mine(flow, space.User.Id) is not { Token: { } token, Host: { } host }) return Results.BadRequest(new { error = "Erst bei pCloud anmelden." });
            var folders = await new PCloudDriver(Http, token, host, "") { Wait = Wait }.FoldersAsync(StoragePath.Clean(path), ct);
            return Results.Ok(new { folders });
        });

        api.MapPost("", async (AddBody body, HttpContext http, ServerConfig config, CancellationToken ct) =>
        {
            var space = http.Space();
            if (Refused(space, config) is { } refused) return refused;
            if (string.IsNullOrWhiteSpace(body.Name)) return Results.BadRequest(new { error = "Bitte einen Namen angeben." });
            if (Mine(body.Flow, space.User.Id) is not { Token: { } token, Host: { } host } flow) return Results.BadRequest(new { error = "Erst bei pCloud anmelden." });
            var folder = StoragePath.Clean(body.Path);
            // Den Ordner gibt es und er lässt sich lesen
            await new PCloudDriver(Http, token, host, folder) { Wait = Wait }.ListAsync("", ct);
            var where = folder.Length == 0 ? "pCloud" : "pCloud/" + folder;
            var account = flow.Account is { Length: > 0 } a ? a + " · " + where : where;
            if (body.Replace != null)
            {
                if (!ExternalFolders.Change(space, body.Replace, UserMount.PCloud, m => { m.Name = body.Name.Trim(); m.Token = token; m.Host = host; m.Folder = folder; m.Account = account; }))
                    return Results.NotFound();
                Flows.TryRemove(body.Flow, out _);
                return Results.Ok(ExternalFolders.TargetsOf(space, config).First(m => m.Id == body.Replace));
            }
            var mount = new UserMount
            {
                Name = body.Name.Trim(), Kind = UserMount.PCloud, Token = token, Host = host, Folder = folder, Account = account,
            };
            lock (space.Db)
            {
                var list = ExternalFolders.Own(space);
                list.Add(mount);
                space.Db.SetJson(ExternalFolders.UserKey, list);
            }
            Flows.TryRemove(body.Flow, out _);
            return Results.Ok(new ExternalFolders.Mount(mount.Id, mount.Name, UserMount.PCloud, false));
        });

        // Rücksprung von pCloud: ohne Anmeldung erreichbar (die Kennung der Anmeldung bindet ihn an den Benutzer), eine kleine
        // Seite zum Schließen - das Fenster von dmOffice fragt den Stand ab
        app.MapGet(CallbackPath, async (string? code, string? state, string? hostname, string? locationid, string? error, HttpContext http, CancellationToken ct) =>
        {
            var ok = await CallbackAsync(state, code, hostname, locationid, error, ct);
            var de = http.Request.Headers.AcceptLanguage.ToString().StartsWith("de", StringComparison.OrdinalIgnoreCase);
            var text = ok
                ? de ? "Angemeldet bei pCloud. Dieses Fenster lässt sich schließen." : "Signed in to pCloud. You can close this window."
                : de ? "Anmeldung bei pCloud fehlgeschlagen. Dieses Fenster lässt sich schließen." : "Signing in to pCloud failed. You can close this window.";
            http.Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'";
            http.Response.Headers["Referrer-Policy"] = "no-referrer";
            return Results.Content($"<!doctype html><html><head><meta charset=\"utf-8\"><title>dmOffice</title></head>"
                + $"<body style=\"font-family:sans-serif;padding:2em\"><p>{WebUtility.HtmlEncode(text)}</p></body></html>", "text/html; charset=utf-8");
        }).AllowAnonymous();
    }
}
