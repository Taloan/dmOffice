using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace DmOffice.Core;

/// <summary>
/// Systemprotokoll auf Dauer (Kennung syslogfile): dieselben Warnungen und Fehler wie <see cref="SystemLog"/> im Speicher, dazu
/// Informationsmeldungen ausgewählter Hintergrundaufgaben (<see cref="InfoCategories"/>), je Tag eine Datei
/// &lt;Daten&gt;/logs/dmoffice-JJJJ-MM-TT.log. Eine Zeile je Eintrag: Zeit (UTC, ISO), Stufe, Bereich, Meldung, Ausnahme - durch
/// Tabulatoren getrennt. Geschrieben wird gepuffert in einem eigenen Ablauf: Wer protokolliert, wartet nie auf die Platte; ist der
/// Puffer voll oder die Platte nicht beschreibbar, gehen Zeilen verloren (einmal auf der Konsole gemeldet), der Server läuft weiter.
/// Dateien älter als <see cref="KeepDays"/> Tage löscht der Ablauf beim Start und einmal täglich; je Tag höchstens
/// <see cref="DefaultMaxBytes"/>, danach eine Hinweiszeile und nichts mehr bis zum nächsten Tag.
/// </summary>
public sealed partial class SystemLogFile : IDisposable
{
    public const int KeepDays = 30;
    public const long DefaultMaxBytes = 50L * 1024 * 1024;
    private const int MaxMessage = 8000, MaxException = 2000, StackFrames = 6;

    /// <summary>Der laufende Schreiber des Servers (null: Kennung aus oder noch nicht gestartet).</summary>
    public static SystemLogFile? Current { get; private set; }

    /// <summary>Logger-Kategorien, deren Informationsmeldungen mit in die Datei gehen (Präfix): Datenexport, Sicherungen, Storage
    /// Extender, Datenänderungen, Mail-Abruf, Start und Ende des Servers.</summary>
    internal static readonly string[] InfoCategories =
    [
        "DmOffice.Storage.PlainBackupService", "DmOffice.Core.S3Backup", "DmOffice.Core.FilesBackup", "DmOffice.Core.S3Mirror",
        "DmOffice.Core.UpdateRunner", "DmOffice.Core.DbHealth", "DmOffice.Core.DbRescue", "DmOffice.Mail.MailSync",
        "Microsoft.Hosting.Lifetime",
    ];

    public static bool InfoWanted(string category) => InfoCategories.Any(p => category.StartsWith(p, StringComparison.Ordinal));

    /// <summary>Beim Start des Servers: Schreiber für &lt;Daten&gt;/logs anlegen.</summary>
    public static void Start(string dataRoot) => Current ??= new SystemLogFile(Path.Combine(dataRoot, "logs"));

    /// <summary>Beim Herunterfahren: Puffer leeren, Datei schließen.</summary>
    public static void Stop() => Current?.Dispose();

    public string Dir { get; }
    private readonly long _maxBytes;
    private readonly int _keepDays;
    private readonly Func<DateTime> _now;
    private readonly Channel<string> _queue = Channel.CreateBounded<string>(
        new BoundedChannelOptions(20000) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly Task _loop;
    private int _reported, _disposed;

    // Nur im Schreib-Ablauf benutzt
    private StreamWriter? _writer;
    private DateTime _day;
    private long _bytes;
    private bool _capped;
    private DateTime _retryAt;

    internal SystemLogFile(string dir, long maxBytes = DefaultMaxBytes, int keepDays = KeepDays, Func<DateTime>? now = null)
    {
        Dir = Path.GetFullPath(dir);
        _maxBytes = maxBytes;
        _keepDays = keepDays;
        _now = now ?? (() => DateTime.UtcNow);
        _loop = Task.Run(RunAsync);
    }

    /// <summary>Eintrag vormerken - kehrt sofort zurück.</summary>
    public void Write(DateTime at, string level, string category, string message, Exception? e = null) =>
        Enqueue(Format(at, level, category, message, e));

    /// <summary>Wie <see cref="Write(DateTime,string,string,string,Exception?)"/>, Ausnahme schon als Text.</summary>
    public void Write(DateTime at, string level, string category, string message, string? exception) =>
        Enqueue(Format(at, level, category, message, exception));

    private void Enqueue(string line)
    {
        if (!_queue.Writer.TryWrite(line) && Volatile.Read(ref _disposed) == 0) Report("Puffer voll, Einträge verworfen");
    }

    private async Task RunAsync()
    {
        Cleanup();
        var cleaned = _now().Date;
        var reader = _queue.Reader;
        while (true)
        {
            bool more;
            // Höchstens eine Stunde warten: auch ohne Einträge einmal täglich aufräumen
            using (var cts = new CancellationTokenSource(TimeSpan.FromHours(1)))
                try { more = await reader.WaitToReadAsync(cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { more = true; }
            if (!more) break;
            while (reader.TryRead(out var line)) Append(line);
            try { _writer?.Flush(); } catch (Exception e) { Fail(e); }
            if (_now().Date != cleaned) { cleaned = _now().Date; Cleanup(); }
        }
        try { _writer?.Dispose(); } catch (Exception) { /* beim Herunterfahren egal */ }
        _writer = null;
    }

    private void Append(string line)
    {
        // Tag aus der Zeile selbst (beginnt mit JJJJ-MM-TT): Einträge kurz vor Mitternacht landen im richtigen Tag
        if (!DateTime.TryParseExact(line[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) return;
        if (day != _day || _writer == null)
        {
            if (_writer == null && day == _day && _now() < _retryAt) return;
            Open(day);
            if (_writer == null) return;
        }
        if (_capped) return;
        try
        {
            var bytes = Encoding.UTF8.GetByteCount(line) + 1;
            if (_bytes + bytes > _maxBytes)
            {
                _writer.Write(Format(_now(), "Warning", "SystemLog",
                    $"Tagesgrenze von {_maxBytes / (1024 * 1024)} MB erreicht - weitere Einträge dieses Tages verworfen", (string?)null) + "\n");
                _capped = true;
                return;
            }
            _writer.Write(line);
            _writer.Write('\n');
            _bytes += bytes;
        }
        catch (Exception e) { Fail(e); }
    }

    private void Open(DateTime day)
    {
        try { _writer?.Dispose(); } catch (Exception) { /* alte Datei */ }
        _writer = null;
        _day = day;
        try
        {
            Directory.CreateDirectory(Dir);
            var fs = new FileStream(FileFor(day), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            _bytes = fs.Length;
            // Schon voll (Neustart am selben Tag): der Hinweis steht bereits drin
            _capped = _bytes >= _maxBytes;
            _writer = new StreamWriter(fs, new UTF8Encoding(false));
        }
        catch (Exception e) { Fail(e); }
    }

    private void Fail(Exception e)
    {
        Report($"{e.GetType().Name}: {e.Message}");
        try { _writer?.Dispose(); } catch (Exception) { /* schon kaputt */ }
        _writer = null;
        _retryAt = _now().AddMinutes(1);
    }

    private void Report(string what)
    {
        if (Interlocked.Exchange(ref _reported, 1) == 0)
            Console.Error.WriteLine($"Systemprotokoll: {Dir} nicht vollständig geschrieben ({what}) - weiter nur im Speicher.");
    }

    /// <summary>Dateien löschen, deren Tag mehr als <see cref="KeepDays"/> Tage zurückliegt.</summary>
    internal void Cleanup()
    {
        var limit = _now().Date.AddDays(-_keepDays);
        try
        {
            if (!Directory.Exists(Dir)) return;
            foreach (var path in Directory.EnumerateFiles(Dir, "dmoffice-*.log"))
                if (DayOf(Path.GetFileName(path)) is { } d && d.ToDateTime(TimeOnly.MinValue) < limit)
                    try { File.Delete(path); } catch (IOException) { /* nächstes Mal */ } catch (UnauthorizedAccessException) { }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Report(e.Message); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.Writer.TryComplete();
        try { _loop.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { /* Ablauf fängt selbst */ }
    }

    // ---------- Format ----------

    internal static string Format(DateTime at, string level, string category, string message, Exception? e) =>
        Format(at, level, category, message, e == null ? null : Describe(e));

    internal static string Format(DateTime at, string level, string category, string message, string? exception) =>
        string.Join('\t', at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture), level,
            Clean(category, 200), Clean(message, MaxMessage), exception == null ? "" : Clean(exception, MaxException));

    /// <summary>Typ und Meldung, innere Ausnahme, die ersten Zeilen des Stapels - alles in einer Zeile.</summary>
    internal static string Describe(Exception e)
    {
        var sb = new StringBuilder(e.GetType().Name).Append(": ").Append(e.Message);
        if (e.InnerException is { } inner) sb.Append(" → ").Append(inner.GetType().Name).Append(": ").Append(inner.Message);
        var frames = (e.StackTrace ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(StackFrames).ToList();
        if (frames.Count > 0) sb.Append(" | ").Append(string.Join(" | ", frames));
        return sb.ToString();
    }

    private static string Clean(string s, int max)
    {
        s = s.Replace("\r\n", " ⏎ ").Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');
        return s.Length > max ? s[..max] + " …" : s;
    }

    /// <summary>Eine Zeile der Datei als Eintrag (null: unlesbar).</summary>
    internal static SystemLog.Entry? Parse(long id, string line)
    {
        var p = line.Split('\t', 5);
        if (p.Length < 4 || !DateTime.TryParse(p[0], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at)) return null;
        return new SystemLog.Entry(id, at, p[1], p[2], p[3], p.Length > 4 && p[4].Length > 0 ? p[4] : null);
    }

    // ---------- Lesen für die Verwaltung ----------

    [GeneratedRegex(@"^dmoffice-(\d{4}-\d{2}-\d{2})\.log$")]
    private static partial Regex FileName();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$")]
    private static partial Regex DayText();

    private static DateOnly? DayOf(string fileName) =>
        FileName().Match(fileName) is { Success: true } m ? ParseDay(m.Groups[1].Value) : null;

    /// <summary>Tag aus der Anfrage: nur JJJJ-MM-TT und ein gültiges Datum.</summary>
    internal static DateOnly? ParseDay(string? text) =>
        text != null && DayText().IsMatch(text) && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private string FileFor(DateTime day) => Path.Combine(Dir, $"dmoffice-{day:yyyy-MM-dd}.log");

    /// <summary>Pfad der Tagesdatei - nur für einen gültigen Tag, aus dem Datum neu gebildet (nie aus dem Text der Anfrage).</summary>
    internal string? PathFor(string? day) => ParseDay(day) is { } d ? FileFor(d.ToDateTime(TimeOnly.MinValue)) : null;

    /// <summary>Vorhandene Tagesdateien, neueste zuerst.</summary>
    public List<(string Day, long Bytes)> Days()
    {
        var list = new List<(string, long)>();
        try
        {
            if (!Directory.Exists(Dir)) return list;
            foreach (var f in new DirectoryInfo(Dir).EnumerateFiles("dmoffice-*.log"))
                if (DayOf(f.Name) is { } d) list.Add((d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), f.Length));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return list.OrderByDescending(x => x.Item1, StringComparer.Ordinal).ToList();
    }

    /// <summary>Die letzten <paramref name="max"/> Einträge eines Tages (älteste zuerst) und wie viele die Datei insgesamt hat;
    /// null: kein gültiger Tag oder keine Datei.</summary>
    public (List<SystemLog.Entry> Entries, int Total)? Read(string? day, int max = 5000)
    {
        if (PathFor(day) is not { } path || !File.Exists(path)) return null;
        var keep = new Queue<SystemLog.Entry>();
        var total = 0;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            if (Parse(total + 1, line) is not { } e) continue;
            total++;
            keep.Enqueue(e);
            if (keep.Count > max) keep.Dequeue();
        }
        return (keep.ToList(), total);
    }

    /// <summary>Datei zum Herunterladen öffnen (Schreiber läuft weiter); null: kein gültiger Tag oder keine Datei.</summary>
    public Stream? OpenRead(string? day) =>
        PathFor(day) is { } path && File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete) : null;
}
