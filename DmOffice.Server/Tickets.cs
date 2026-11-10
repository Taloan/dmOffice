using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using DmOffice.Core;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;

namespace DmOffice;

/// <summary>Was ein Ticket erlaubt: genau eine Anfrage (Methode, Pfad, Query-Werte) eines Benutzers.</summary>
/// <param name="U">Benutzer</param>
/// <param name="S">Sicherheitsstempel beim Ausstellen - neues Passwort oder Abmelden ueberall macht das Ticket ungueltig</param>
/// <param name="A">App-Passwort, ueber das es geholt wurde (null: Browser) - wird es widerrufen, endet auch das Ticket</param>
/// <param name="M">Methode (GET, HEAD, POST, PUT)</param>
/// <param name="P">Pfad, dekodiert (wie Request.Path)</param>
/// <param name="Q">Query ohne "?", roh wie vom Client gesendet (verglichen werden die dekodierten Werte)</param>
/// <param name="Id">Zufaellige Kennung: POST und PUT gelten nur einmal</param>
public sealed record Ticket(string U, string S, string? A, string M, string P, string Q, string Id);

/// <summary>
/// Kurzlebige Tickets fuer Anfragen am verschluesselten Umschlag (E2e) vorbei. Der Umschlag haelt jede Anfrage ganz
/// im Speicher und taugt darum nicht fuer grosse Dateien und Streams. Statt dafuer das App-Passwort per Basic nur unter
/// TLS zu senden (ein mitlesender Proxy haette es dann), holt die App im Umschlag ein Ticket fuer genau eine Anfrage und
/// schickt nur dieses im Kopf <see cref="Header"/> mit - fuer die Dateiablage ebenso wie fuer jeden anderen Up- oder
/// Download (Chat, Anhaenge ...). Gebunden an Methode, Pfad und alle Query-Werte; lesend mehrfach (Wiederaufnahme,
/// Bereiche), schreibend nur einmal. Erlaubt ist nur, was das App-Passwort ohnehin darf (Auth.AppPasswordAllowed).
/// Signiert und verschluesselt mit Data Protection (eigener Zweck, wie die WOPI-Tokens).
/// </summary>
public static class Tickets
{
    public const string Header = "X-DM-Ticket";
    public const string Purpose = "dmoffice.ticket";
    /// <summary>Anspruch im Principal einer Ticket-Anfrage: Kennung des Tickets.</summary>
    public const string Claim = "dm:ticket";

    /// <summary>Zeit bis zum Beginn der Anfrage; die Uebertragung selbst darf danach zu Ende laufen.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    /// <summary>Abspielen (stream): der Player holt beim Spulen immer wieder Bereiche, solange das Video offen ist.</summary>
    public static readonly TimeSpan StreamLifetime = TimeSpan.FromHours(4);

    public record Body(string Method, string Url, bool? Stream = null);

    private static ITimeLimitedDataProtector Protector(IServiceProvider services) =>
        services.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).ToTimeLimitedDataProtector();

    public static (string Token, DateTimeOffset Expires) Issue(IServiceProvider services, Ticket ticket, bool stream)
    {
        var expires = DateTimeOffset.UtcNow + (stream ? StreamLifetime : Lifetime);
        return (Protector(services).Protect(JsonSerializer.Serialize(ticket), expires), expires);
    }

    public static Ticket? Read(IServiceProvider services, string? token, out DateTimeOffset expires)
    {
        expires = default;
        if (string.IsNullOrEmpty(token) || token.Length > 8192) return null;
        try { return JsonSerializer.Deserialize<Ticket>(Protector(services).Unprotect(token, out expires)); }
        catch (CryptographicException) { return null; }
        catch (JsonException) { return null; }
    }

    /// <summary>Lesend (mehrfach gueltig) oder schreibend (einmal).</summary>
    public static bool Reading(string method) => HttpMethods.IsGet(method) || HttpMethods.IsHead(method);

    /// <summary>
    /// Ziel eines Tickets pruefen und zerlegen: relative Adresse unter /api, kein Ausbrechen ueber ".." (roh oder
    /// kodiert, wie im Umschlag), nur was ein App-Passwort darf, nie Anmeldung, Umschlag oder Tickets selbst.
    /// </summary>
    public static (string Path, string Query)? Target(string? url)
    {
        if (string.IsNullOrEmpty(url) || url.Length > 4096 || !url.StartsWith('/') || url.StartsWith("//") || url.Contains("://")) return null;
        var parts = url.Split('?', 2);
        var raw = parts[0];
        var lower = raw.ToLowerInvariant();
        if (raw.Contains('\\') || raw.Contains("..") || lower.Contains("%2e") || lower.Contains("%2f") || lower.Contains("%5c") || raw.Contains('#')) return null;
        var path = PathString.FromUriComponent(raw).Value ?? "";
        if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)) return null;
        foreach (var no in new[] { "/api/auth", "/api/e2e", "/api/tickets" })
            if (path.Equals(no, StringComparison.OrdinalIgnoreCase) || path.StartsWith(no + "/", StringComparison.OrdinalIgnoreCase)) return null;
        if (!Auth.AppPasswordAllowed(path)) return null;
        return (path, parts.Length > 1 ? parts[1] : "");
    }

    /// <summary>Passt die Anfrage genau zum Ticket? Methode (HEAD zaehlt als GET), Pfad und dieselben Query-Werte.</summary>
    public static bool Matches(Ticket t, HttpRequest r)
    {
        bool method = r.Method.Equals(t.M, StringComparison.OrdinalIgnoreCase) || HttpMethods.IsGet(t.M) && HttpMethods.IsHead(r.Method);
        if (!method || !string.Equals(r.Path.Value, t.P, StringComparison.Ordinal)) return false;
        var want = QueryHelpers.ParseQuery(t.Q);
        if (want.Count != r.Query.Count) return false;
        foreach (var (k, v) in want)
            if (!r.Query.TryGetValue(k, out var got) || !got.SequenceEqual(v, StringComparer.Ordinal)) return false;
        return true;
    }

    private static readonly ConcurrentDictionary<string, DateTimeOffset> Used = new();

    /// <summary>Schreibendes Ticket verbrauchen: true beim ersten Mal. Abgelaufene Eintraege fallen dabei heraus.</summary>
    public static bool Consume(string id, DateTimeOffset expires)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (k, v) in Used.ToArray()) if (v < now) Used.TryRemove(k, out _);
        return Used.TryAdd(id, expires);
    }

    /// <summary>POST /api/tickets {method, url, stream?}: Ticket fuer genau diese Anfrage. Nur mit Anmeldung, nie mit einem Ticket.</summary>
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/tickets", (Body body, HttpContext http, UserStore users) =>
        {
            if (http.User.HasClaim(c => c.Type == Claim)) return Results.StatusCode(403);
            var method = (body.Method ?? "").ToUpperInvariant();
            if (method is not ("GET" or "HEAD" or "POST" or "PUT")) return Results.BadRequest(new { error = "Methode nicht erlaubt." });
            if (Target(body.Url) is not { } target) return Results.BadRequest(new { error = "Adresse nicht erlaubt." });
            var user = users.ById(http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "") ?? throw new UnauthorizedAccessException();
            var appPassword = http.User.FindFirstValue(Auth.AppPasswordIdClaim) is { Length: > 0 } a ? a : null;
            var t = new Ticket(user.Id, user.Stamp, appPassword, method, target.Path, target.Query, Guid.NewGuid().ToString("N"));
            var (token, expires) = Issue(http.RequestServices, t, body.Stream == true && Reading(method));
            return Results.Ok(new { ticket = token, expires, header = Header });
        }).RequireAuthorization();
    }

    /// <summary>Fuer die Tests: alles vergessen.</summary>
    public static void Clear() => Used.Clear();
}
