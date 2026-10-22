using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Authentication;

namespace DmOffice;

/// <summary>
/// Ansteigende Bremse fuer Anfragen, die NICHT angemeldet sind - je Adresse (IPv4, bei IPv6 je /64, damit ein
/// Angreifer nicht einfach durch die Adressen seines Netzes rotiert).
///
/// Jede Adresse hat ein Guthaben (Token-Bucket). Jede nicht angemeldete Anfrage kostet 1, eine abgewiesene
/// (401/403/404 - typisch fuer Durchprobieren und Abtasten) kostet 3. Ist das Guthaben aufgebraucht, sperrt die
/// Bremse die Adresse - erst kurz, bei jedem weiteren Verstoss doppelt so lange (bis zu einer Stunde). Ohne neuen
/// Verstoss sinkt die Stufe mit der Zeit wieder. Angemeldete Anfragen kosten nichts und werden auch von einer
/// gesperrten Adresse durchgelassen (Haushalt hinter derselben Adresse wie ein Angreifer).
///
/// Ergaenzt die Sperre nach Fehlversuchen (Auth.Failed) und die Bremse "public" fuer Freigabelinks.
/// </summary>
public sealed class AnonThrottle
{
    public sealed record Settings
    {
        /// <summary>Guthaben einer Adresse (so viele Anfragen am Stueck).</summary>
        public double Burst { get; init; } = 300;
        /// <summary>Nachschub je Sekunde (4/s = 240 je Minute auf Dauer).</summary>
        public double PerSecond { get; init; } = 4;
        public TimeSpan FirstBlock { get; init; } = TimeSpan.FromSeconds(30);
        public TimeSpan MaxBlock { get; init; } = TimeSpan.FromHours(1);
        /// <summary>Nach so langer Ruhe ohne Verstoss sinkt die Stufe um eins.</summary>
        public TimeSpan Decay { get; init; } = TimeSpan.FromMinutes(30);
        /// <summary>Hoechstzahl gemerkter Adressen (Speichergrenze bei Angriffen von sehr vielen Adressen).</summary>
        public int MaxEntries { get; init; } = 50_000;
    }

    /// <summary>Ergebnis des Zaehlens: neu gesperrt? Stufe und Dauer der Sperre.</summary>
    public readonly record struct Outcome(bool NewBlock, int Level, TimeSpan Block);

    /// <summary>Merker in HttpContext.Items: die Anfrage war auf anderem Weg legitim (z. B. ein entschluesselter
    /// Umschlag oder ein gueltiges WOPI-Token) und zaehlt nicht als unangemeldet.</summary>
    public const string TrustedItem = "dm:throttle-trusted";

    private sealed class Entry
    {
        public double Tokens;
        public DateTime Refilled;
        public int Level;
        public DateTime BlockedUntil;
        public DateTime LastViolation;
        public DateTime Seen;
    }

    private readonly Settings _s;
    private readonly Func<DateTime> _now;
    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private DateTime _lastPrune;

    public AnonThrottle(Settings? settings = null, Func<DateTime>? clock = null)
    {
        _s = settings ?? new Settings();
        _now = clock ?? (() => DateTime.UtcNow);
    }

    public int Count => _entries.Count;

    /// <summary>Schluessel einer Adresse; null = nicht bremsen (unbekannt oder Loopback, etwa der Umschlag-Innenteil).</summary>
    public static string? KeyOf(IPAddress? ip)
    {
        if (ip == null) return null;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return null;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = ip.GetAddressBytes();
            return Convert.ToHexString(b, 0, 8) + "/64";
        }
        return ip.ToString();
    }

    /// <summary>Wie lange die Adresse noch gesperrt ist, sonst null.</summary>
    public TimeSpan? Blocked(string key)
    {
        if (!_entries.TryGetValue(key, out var e)) return null;
        lock (e)
        {
            var left = e.BlockedUntil - _now();
            return left > TimeSpan.Zero ? left : null;
        }
    }

    /// <summary>Eine nicht angemeldete Anfrage verbuchen.</summary>
    public Outcome Charge(string key, double cost = 1)
    {
        var now = _now();
        Prune(now);
        var e = _entries.GetOrAdd(key, _ => new Entry { Tokens = _s.Burst, Refilled = now, Seen = now });
        lock (e)
        {
            e.Seen = now;
            // Abklingen: je volle Ruhezeit ohne Verstoss eine Stufe weniger
            if (e.Level > 0 && e.LastViolation != default)
            {
                var steps = (int)((now - e.LastViolation).Ticks / _s.Decay.Ticks);
                if (steps > 0)
                {
                    e.Level = Math.Max(0, e.Level - steps);
                    e.LastViolation = now - TimeSpan.FromTicks((now - e.LastViolation).Ticks % _s.Decay.Ticks);
                }
            }
            if (e.BlockedUntil > now) return new Outcome(false, e.Level, e.BlockedUntil - now);
            e.Tokens = Math.Min(_s.Burst, e.Tokens + (now - e.Refilled).TotalSeconds * _s.PerSecond);
            e.Refilled = now;
            e.Tokens -= cost;
            if (e.Tokens >= 0) return new Outcome(false, e.Level, TimeSpan.Zero);
            // Verstoss: sperren, jede weitere Stufe doppelt so lange
            e.Level = Math.Min(e.Level + 1, 20);
            var block = TimeSpan.FromTicks(Math.Min(_s.MaxBlock.Ticks, _s.FirstBlock.Ticks * (1L << Math.Min(e.Level - 1, 16))));
            e.BlockedUntil = now + block;
            // Abklingen zaehlt erst ab dem Ende der Sperre - sonst sanke die Stufe schon waehrend einer langen Sperre
            e.LastViolation = e.BlockedUntil;
            // Nach der Sperre mit halbem Guthaben weiter - wer gleich wieder flutet, ist schnell auf der naechsten Stufe
            e.Tokens = _s.Burst / 2;
            e.Refilled = e.BlockedUntil;
            return new Outcome(true, e.Level, block);
        }
    }

    /// <summary>Nicht endlos wachsen: ruhige, ungesperrte Adressen vergessen; bei sehr vielen die aeltesten.</summary>
    private void Prune(DateTime now)
    {
        if (_entries.Count < _s.MaxEntries / 2 || now - _lastPrune < TimeSpan.FromSeconds(10)) return;
        _lastPrune = now;
        foreach (var (k, e) in _entries)
            if (e.BlockedUntil < now && e.Level == 0 && now - e.Seen > TimeSpan.FromMinutes(5)) _entries.TryRemove(k, out _);
        if (_entries.Count < _s.MaxEntries) return;
        foreach (var (k, _) in _entries.Where(x => x.Value.BlockedUntil < now).OrderBy(x => x.Value.Seen).Take(_entries.Count - _s.MaxEntries / 2).ToList())
            _entries.TryRemove(k, out _);
    }

    /// <summary>Einstellung aus der Umgebung: DMOFFICE_ANON_LIMIT=off schaltet ab, eine Zahl setzt die
    /// Anfragen je Minute auf Dauer (Guthaben = 1,25 Minuten davon). Sonst die Vorgabe.</summary>
    public static AnonThrottle? FromEnvironment()
    {
        var v = Environment.GetEnvironmentVariable("DMOFFICE_ANON_LIMIT")?.Trim();
        if (string.Equals(v, "off", StringComparison.OrdinalIgnoreCase)) return null;
        if (int.TryParse(v, out var perMinute) && perMinute > 0)
            return new AnonThrottle(new Settings { PerSecond = perMinute / 60.0, Burst = Math.Max(60, perMinute * 1.25) });
        return new AnonThrottle();
    }

    /// <summary>Middleware hinter UseAuthentication und VOR UseAuthorization - so werden auch Anfragen gezaehlt,
    /// die die Anmeldepruefung abweist (401), und App-Passwort/Ticket sind nach dem Durchlauf bekannt.</summary>
    public static void Use(WebApplication app, AnonThrottle throttle)
    {
        app.Use(async (ctx, next) =>
        {
            var key = KeyOf(ctx.Connection.RemoteIpAddress);
            if (key == null) { await next(); return; }
            // Gesperrt: nur noch Angemeldete durch. Der Umschlag hat eine eigene Sperre (Auth.Blocked) und
            // ist erst nach dem Entschluesseln als legitim erkennbar - ihn hier nicht vorab abweisen.
            if (throttle.Blocked(key) is { } wait && !IsEnvelope(ctx.Request.Path) && !await PreAuthenticated(ctx))
            {
                await Reject(ctx, wait);
                return;
            }
            await next();
            if (ctx.User.Identity?.IsAuthenticated == true || ctx.Items.ContainsKey(TrustedItem)) return;
            var cost = ctx.Response.StatusCode is 401 or 403 or 404 ? 3 : 1;
            var o = throttle.Charge(key, cost);
            if (o.NewBlock && o.Level >= 3)
                app.Logger.LogWarning("Viele Anfragen ohne Anmeldung von {Address}: gesperrt für {Seconds} s (Stufe {Level})",
                    key, (int)o.Block.TotalSeconds, o.Level);
        });
    }

    private static bool IsEnvelope(PathString path)
    {
        // /api/e2e/{userId}/{deviceId}: der anonyme aeussere Teil des Umschlags
        var p = path.Value ?? "";
        return p.StartsWith("/api/e2e/", StringComparison.OrdinalIgnoreCase) && p.Count(c => c == '/') == 4;
    }

    /// <summary>Schon vor dem Endpunkt angemeldet? Cookie (UseAuthentication) oder App-Passwort/Ticket/Umschlag-Innenteil.
    /// Das Ergebnis des App-Passwort-Verfahrens bleibt fuer die Anfrage zwischengespeichert - die spaetere Pruefung
    /// verwendet es wieder (kein zweiter Fehlversuch, kein zweites Verbrauchen eines Tickets).</summary>
    private static async Task<bool> PreAuthenticated(HttpContext ctx)
    {
        if (ctx.User.Identity?.IsAuthenticated == true) return true;
        var h = ctx.Request.Headers;
        if (!h.ContainsKey("Authorization") && !h.ContainsKey(Tickets.Header) && !h.ContainsKey(E2e.SessionHeader)) return false;
        var r = await ctx.AuthenticateAsync(Auth.AppPasswordScheme);
        return r.Succeeded;
    }

    private static async Task Reject(HttpContext ctx, TimeSpan wait)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
        ctx.Response.StatusCode = 429;
        ctx.Response.Headers.RetryAfter = seconds.ToString();
        if (ctx.Request.Path.StartsWithSegments("/api"))
            await ctx.Response.WriteAsJsonAsync(new { error = $"Zu viele Anfragen ohne Anmeldung. Bitte in {seconds} Sekunden erneut versuchen." });
    }
}
