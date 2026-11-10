using System.Collections.Concurrent;

namespace DmOffice.Core;

/// <summary>
/// Systemprotokoll für die Verwaltung: Warnungen und Fehler des Servers seit dem Start (die letzten 2000), dazu Antworten 429
/// und 5xx mit Pfad (ohne Abfrageteil - der kann Tokens enthalten). Nur im Speicher; das vollständige Protokoll bleibt das des
/// Containers (docker logs). Inhalte von Mails, Dateien oder Passwörter schreibt dmOffice nicht ins Protokoll.
/// </summary>
public sealed class SystemLog : ILoggerProvider
{
    /// <summary>Ein Eintrag; Zeit und Meldung ändern sich nur bei zusammengefassten Serien (429 von derselben Adresse).</summary>
    public sealed class Entry(long id, DateTime at, string level, string category, string message, string? exception)
    {
        public long Id { get; } = id;
        public DateTime At { get; set; } = at;
        public string Level { get; } = level;
        public string Category { get; } = category;
        public string Message { get; set; } = message;
        public string? Exception { get; } = exception;
        /// <summary>Überholt (etwa ein verbrauchter Einrichtungscode): nicht mehr anzeigen.</summary>
        [System.Text.Json.Serialization.JsonIgnore] public bool Withdrawn { get; set; }
    }

    /// <summary>Gebremste Anfragen je Adresse: kommen weitere innerhalb von <see cref="BurstWindow"/>, zählt derselbe Eintrag hoch
    /// (ein Scanner, der hundert Pfade probiert, füllt sonst das ganze Protokoll).</summary>
    private sealed class Burst { public required Entry Entry; public int Count; public DateTime Last; }
    private static readonly ConcurrentDictionary<string, Burst> Bursts = new();
    private static readonly TimeSpan BurstWindow = TimeSpan.FromMinutes(10);

    private const int Keep = 2000;
    private static readonly ConcurrentQueue<Entry> Entries = new();
    private static long _next;

    public static Entry Add(LogLevel level, string category, string message, Exception? e = null)
    {
        var entry = new Entry(Interlocked.Increment(ref _next), DateTime.UtcNow, level.ToString(), Short(category), message,
            e == null ? null : e.GetType().Name + ": " + e.Message);
        Entries.Enqueue(entry);
        while (Entries.Count > Keep && Entries.TryDequeue(out _)) { }
        return entry;
    }

    /// <summary>429 festhalten - weitere von derselben Adresse kurz danach im selben Eintrag.</summary>
    internal static void Throttled(string ip, string request)
    {
        var now = DateTime.UtcNow;
        if (Bursts.TryGetValue(ip, out var b) && now - b.Last < BurstWindow)
            lock (b)
            {
                b.Count++; b.Last = now;
                b.Entry.At = now;
                b.Entry.Message = $"429 – {b.Count} Anfragen von {ip} gebremst, zuletzt {request}";
                return;
            }
        Bursts[ip] = new Burst { Entry = Add(LogLevel.Warning, "Http", $"429 {request} ({ip})"), Count = 1, Last = now };
        foreach (var old in Bursts.Where(x => now - x.Value.Last > BurstWindow).Select(x => x.Key).ToList()) Bursts.TryRemove(old, out _);
    }

    public static List<Entry> All() => Entries.Where(e => !e.Withdrawn).Reverse().ToList();

    /// <summary>Einträge, deren Meldung diesen Text enthält, nicht mehr anzeigen.</summary>
    public static void Withdraw(string text)
    {
        foreach (var e in Entries) if (e.Message.Contains(text, StringComparison.Ordinal)) e.Withdrawn = true;
    }

    /// <summary>Microsoft.AspNetCore.Server.Kestrel → Kestrel, DmOffice.Mail.MailSync → Mail.MailSync</summary>
    private static string Short(string category)
    {
        if (category.StartsWith("DmOffice.", StringComparison.Ordinal)) return category[9..];
        var dot = category.LastIndexOf('.');
        return dot > 0 ? category[(dot + 1)..] : category;
    }

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName);
    public void Dispose() { }

    private sealed class Logger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            Add(logLevel, category, formatter(state, exception), exception);
        }
    }

    /// <summary>Antworten 429 (gebremst) und 5xx (Serverfehler) festhalten - sie erscheinen sonst nur als Meldung im Browser.</summary>
    public static void Use(WebApplication app) => app.Use(async (ctx, next) =>
    {
        await next();
        var status = ctx.Response.StatusCode;
        var ip = Auth.IpKey(ctx.Connection.RemoteIpAddress);
        if (status == 429) Throttled(ip, $"{ctx.Request.Method} {ctx.Request.Path}");
        else if (status >= 500) Add(LogLevel.Error, "Http", $"{status} {ctx.Request.Method} {ctx.Request.Path} ({ip})");
    });
}
