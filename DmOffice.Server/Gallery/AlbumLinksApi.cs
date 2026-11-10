using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using DmOffice.Core;
using DmOffice.Dav;
using Microsoft.AspNetCore.DataProtection;

namespace DmOffice.Gallery;

/// <summary>
/// Alben öffentlich zeigen (/a/{token} ist die Seite, /api/public-album/{token} die Schnittstelle): live und nur ansehen,
/// wahlweise mit Ablauf, Passwort und Herunterladen der Originale. Schutz wie bei den Datei-Links (PublicApi): Token mit
/// 144 Bit Zufall, gesucht über den Hash und dann in fester Zeit verglichen, falsche Tokens und Passwörter zählen wie falsche
/// Anmeldungen, Anfragen je Adresse gebremst. Die Seite sieht weder Pfade noch Benutzerkennungen: jedes Foto hat nur eine
/// eigene Kennung je Link (HMAC aus dem Token), über die Vorschau und Original gehen.
/// </summary>
public static class AlbumLinksApi
{
    public record LinkBody(int? Days, string? Password, bool Download);
    public record AuthBody(string Password);

    private const int Page = 300;

    private static string Ip(HttpContext http) => "album-link-ip:" + Auth.IpKey(http.Connection.RemoteIpAddress);
    private static IResult Gone(string message, int status = 404) => Results.Json(new { error = message }, statusCode: status);

    private sealed record Access(AlbumLink Link, string? Password, Album Album);

    private static (Access? Access, IResult? Error) Resolve(HttpContext http, string token, bool needPassword = true)
    {
        var ip = Ip(http);
        if (Auth.Blocked(ip)) return (null, Gone("Zu viele Versuche. Bitte später noch einmal.", 429));
        var store = http.RequestServices.GetRequiredService<AlbumStore>();
        var hit = token.Length is >= 16 and <= 64 ? store.LinkByToken(token) : null;
        if (hit is not { } h || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(h.Link.Token), Encoding.UTF8.GetBytes(token)))
        {
            Auth.Failed(ip);
            return (null, Gone("Diesen Link gibt es nicht (mehr)."));
        }
        var album = store.Get(h.Link.Album);
        if (album == null) return (null, Gone("Diesen Link gibt es nicht (mehr)."));
        // Besitzer gelöscht oder ohne Dateiablage: wie ein Link, den es nicht gibt
        try { if (FileStore.FilesOff(http.RequestServices.GetRequiredService<Spaces>().Get(album.Owner).Db)) return (null, Gone("Diesen Link gibt es nicht (mehr).")); }
        catch (KeyNotFoundException) { return (null, Gone("Diesen Link gibt es nicht (mehr).")); }
        if (h.Link.Expired(DateTime.UtcNow)) return (null, Gone("Dieser Link ist abgelaufen.", 410));
        if (needPassword && h.Password != null && !Unlocked(http, h.Link, h.Password))
            return (null, Results.Json(new { error = "Passwort nötig.", needsPassword = true, name = album.Name }, statusCode: 401));
        return (new Access(h.Link, h.Password, album), null);
    }

    private static IDataProtector Protector(HttpContext http) =>
        http.RequestServices.GetRequiredService<IDataProtectionProvider>().CreateProtector("dmoffice.album.link");
    private static string CookieName(AlbumLink link) => "dma_" + link.Id;
    private static string Stamp(string password) => LinkIndex.Hash(password)[..16];

    /// <summary>Passwort einmal richtig eingegeben: Cookie für diesen Link (einen Tag), ungültig, wenn sich das Passwort ändert.</summary>
    private static bool Unlocked(HttpContext http, AlbumLink link, string password)
    {
        if (!http.Request.Cookies.TryGetValue(CookieName(link), out var value)) return false;
        try
        {
            var parts = Protector(http).Unprotect(value).Split('|');
            return parts.Length == 3 && parts[0] == link.Id && long.TryParse(parts[1], out var until) && until > DateTime.UtcNow.Ticks && parts[2] == Stamp(password);
        }
        catch (CryptographicException) { return false; }
    }

    // Kennung eines Fotos je Link: HMAC(Token, Schlüssel im Album) - gleich bleibend (Browser kann zwischenspeichern), verrät nichts
    private static string KeyOf(AlbumLink link, string photoKey) =>
        Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes("album-link|" + link.Token), Encoding.UTF8.GetBytes(photoKey)))[..22]
            .Replace('+', '-').Replace('/', '_');

    // Fotos eines Links mit ihren Kennungen, eine Minute gemerkt (eine Seite lädt viele Vorschaubilder auf einmal)
    private static readonly ConcurrentDictionary<string, (DateTime At, List<(string K, AlbumApi.Photo P)> List, Dictionary<string, AlbumApi.Photo> ByK)> Cache = new();

    private static (List<(string K, AlbumApi.Photo P)> List, Dictionary<string, AlbumApi.Photo> ByK) Photos(HttpContext http, Access a)
    {
        if (Cache.TryGetValue(a.Link.Id, out var c) && DateTime.UtcNow - c.At < TimeSpan.FromMinutes(1)) return (c.List, c.ByK);
        var s = http.RequestServices;
        var items = AlbumApi.ItemsOf(a.Album, s.GetRequiredService<Spaces>(), s.GetRequiredService<ServerConfig>(), s.GetRequiredService<AlbumStore>())
            .OrderByDescending(p => p.Meta?.Taken ?? new DateTimeOffset(DateTime.SpecifyKind(p.Modified, DateTimeKind.Utc)).ToUnixTimeMilliseconds())
            .Select(p => (K: KeyOf(a.Link, p.Key), P: p)).ToList();
        var byK = new Dictionary<string, AlbumApi.Photo>(StringComparer.Ordinal);
        foreach (var (k, p) in items) byK[k] = p;
        Cache[a.Link.Id] = (DateTime.UtcNow, items, byK);
        return (items, byK);
    }

    private static AlbumApi.Photo PhotoOf(HttpContext http, Access a, string? k) =>
        k != null && Photos(http, a).ByK.TryGetValue(k, out var p) ? p : throw new KeyNotFoundException("Nicht gefunden.");

    private static object LinkDto(AlbumLink l, HttpContext http) => new
    {
        // Die Adresse setzt die Seite zusammen (location.origin) - hinter einem Reverse-Proxy kennt der Server sie nicht sicher
        l.Id, l.Token, l.Expires, l.HasPassword, l.Download, l.Created, l.Views,
        expired = l.Expired(DateTime.UtcNow),
    };

    public static void Map(WebApplication app)
    {
        // ---------------- Verwalten (Besitzer des Albums) ----------------
        var own = app.MapGroup("/api/gallery/albums/{id:long}/links").RequireAuthorization();
        Album Mine(HttpContext http, long id) =>
            http.RequestServices.GetRequiredService<AlbumStore>().Get(id) is { } a && a.Owner == http.Space().User.Id ? a : throw new KeyNotFoundException("Album nicht gefunden.");

        own.MapGet("", (long id, HttpContext http, AlbumStore store) => { Mine(http, id); return Results.Ok(store.Links(id).Select(l => LinkDto(l, http))); });
        own.MapPost("", (long id, LinkBody body, HttpContext http, AlbumStore store) =>
        {
            Mine(http, id);
            if (store.Links(id).Count >= 20) throw new ArgumentException("Höchstens 20 Links je Album.");
            if (body.Password is { Length: > 200 }) throw new ArgumentException("Das Passwort ist zu lang.");
            DateTime? expires = body.Days is int d && d > 0 ? DateTime.UtcNow.AddDays(Math.Min(d, 3650)) : null;
            return Results.Ok(LinkDto(store.CreateLink(id, expires, body.Password, body.Download), http));
        });
        own.MapDelete("/{link}", (long id, string link, HttpContext http, AlbumStore store) =>
        {
            Mine(http, id);
            Cache.TryRemove(link, out _);
            return store.DeleteLink(id, link) ? Results.Ok() : Results.NotFound();
        });

        // ---------------- Öffentlich ----------------
        var api = app.MapGroup("/api/public-album/{token}").RequireRateLimiting("public");

        api.MapGet("/", (string token, int? offset, HttpContext http, Spaces spaces, AlbumStore store) =>
        {
            var (a, error) = Resolve(http, token);
            if (a == null) return error!;
            var (list, _) = Photos(http, a);
            var from = Math.Max(0, offset ?? 0);
            if (from == 0) store.CountView(a.Link.Id);
            string owner;
            try { owner = spaces.Get(a.Album.Owner).User.DisplayName; } catch (KeyNotFoundException) { owner = ""; }
            return Results.Ok(new
            {
                name = a.Album.Name, owner, expires = a.Link.Expires, download = a.Link.Download, total = list.Count,
                items = list.Skip(from).Take(Page).Select(x => new
                {
                    k = x.K, name = x.P.Name, mime = x.P.Mime, x.P.Etag,
                    taken = x.P.Meta?.Taken ?? new DateTimeOffset(DateTime.SpecifyKind(x.P.Modified, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
                    width = x.P.Meta?.Width, height = x.P.Meta?.Height, title = x.P.Meta?.Title,
                }),
            });
        });

        api.MapPost("/auth", async (string token, AuthBody body, HttpContext http) =>
        {
            var (a, error) = Resolve(http, token, needPassword: false);
            if (a == null) return error!;
            var key = "album-link:" + a.Link.Id;
            if (Auth.Blocked(key) || Auth.Blocked(Ip(http))) return Gone("Zu viele Versuche. Bitte später noch einmal.", 429);
            if (a.Password == null) return Results.Ok();
            var ok = await Passwords.VerifyLimitedAsync(body.Password ?? "", a.Password, http.RequestAborted);
            if (ok == null) return Gone("Der Server ist gerade ausgelastet. Bitte gleich noch einmal versuchen.", 503);
            if (ok == false) { Auth.Failed(key); Auth.Failed(Ip(http)); return Gone("Falsches Passwort.", 403); }
            Auth.Succeeded(key);
            var until = DateTime.UtcNow.AddDays(1);
            http.Response.Cookies.Append(CookieName(a.Link), Protector(http).Protect($"{a.Link.Id}|{until.Ticks}|{Stamp(a.Password)}"), new CookieOptions
            {
                HttpOnly = true, Secure = http.Request.IsHttps, SameSite = SameSiteMode.Strict, Path = "/api/public-album/" + token, Expires = until,
            });
            return Results.Ok();
        });

        api.MapGet("/thumb", async (string token, string? k, int? size, HttpContext http, Spaces spaces, ServerConfig config) =>
        {
            var (a, error) = Resolve(http, token);
            if (a == null) return error!;
            return await AlbumApi.Thumb(http, PhotoOf(http, a, k), spaces, config, size);
        });

        // Original: Videos zum Abspielen immer, sonst nur, wenn der Link das Herunterladen erlaubt
        api.MapGet("/file", async (string token, string? k, bool? download, HttpContext http, Spaces spaces, ServerConfig config) =>
        {
            var (a, error) = Resolve(http, token);
            if (a == null) return error!;
            var p = PhotoOf(http, a, k);
            // Ohne Erlaubnis keine Originale - Bilder zeigt die Seite über die große Vorschau, nur Videos brauchen die Datei zum Abspielen
            if (!a.Link.Download && p.Mime?.StartsWith("video/") != true) return Gone("Herunterladen ist bei diesem Link nicht erlaubt.", 403);
            var save = download == true && a.Link.Download;
            return await AlbumApi.File(http, p, spaces, config, inline: !save, download: a.Link.Download);
        });
    }
}
