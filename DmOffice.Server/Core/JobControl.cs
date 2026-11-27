using System.Collections.Concurrent;
using System.Text.Json;

namespace DmOffice.Core;

/// <summary>
/// Hintergrundjobs anhalten, fortsetzen, überspringen, abbrechen (Kennung jobcontrol). Gilt für Galerie-Ordner
/// einlesen, Suche in Server-Mounts, Importprofile, Übertragungen und Datenexport (Sicherungen nicht).
/// <list type="bullet">
/// <item><b>Anhalten</b>: der laufende Lauf hält an der nächsten Prüfstelle an, und es beginnt keiner, bis „Fortsetzen“ - übersteht einen
/// Neustart (job-control.json). Jobs mit Zeitplan machen danach dort weiter, wo sie standen (ihr Stand liegt in der Datenbank);
/// eine Übertragung wartet an der Stelle.</item>
/// <item><b>Überspringen</b>: der Lauf endet und der Job ruht bis zu seinem nächsten Termin (den nennt der Job).</item>
/// <item><b>Abbrechen</b>: nur der laufende Lauf endet; der nächste beginnt wie geplant.</item>
/// </list>
/// Jeder Benutzer steuert seine eigenen Jobs, der Admin alle; Jobs des Servers (Suche in Server-Mounts) gehören <see cref="Server"/>.
/// Die Jobs fragen an Prüfstellen <see cref="CheckAsync"/> und vor dem Beginn <see cref="Blocked"/>.
/// </summary>
public static class JobControl
{
    /// <summary>Besitzer der Jobs, die dem ganzen Server gehören.</summary>
    public const string Server = "*";
    public const string DataExport = "data-export";
    /// <summary>Steuerbare Jobs.</summary>
    public static readonly string[] Kinds = [Activity.GalleryScan, Activity.MountIndex, Activity.GalleryImport, Activity.FilesTransfer, DataExport,
        Activity.PhotoExport, // [feature:photoexport]
        Activity.GalleryMedium, // [feature:galmedium]
        Activity.LensApply, // [feature:lenscorr]
        Activity.LensLib, // [feature:lenslib]
    ];
    /// <summary>Jobs ohne Zeitplan: Anhalten wartet an der Prüfstelle, statt den Lauf zu beenden; Überspringen = Abbrechen.</summary>
    private static readonly string[] OneOff = [Activity.FilesTransfer,
        Activity.PhotoExport, // [feature:photoexport]
        Activity.LensApply, // [feature:lenscorr]
        Activity.LensLib, // [feature:lenslib]
    ];

    /// <summary>Der Lauf endet auf Wunsch (Anhalten, Überspringen, Abbrechen) - kein Fehler.</summary>
    public sealed class Stopped(string reason) : Exception(reason);

    private sealed record State(bool Paused, DateTime? SkipUntil);
    private static readonly ConcurrentDictionary<string, State> States = new();
    private static readonly ConcurrentDictionary<string, int> Active = new();
    private static readonly ConcurrentDictionary<string, string> StopRequests = new();
    private static string? _file;

    private static string Key(string user, string kind) => user + "|" + kind;

    /// <summary>Für die Jobs: wird nach „Fortsetzen“ oder nach dem Ende einer Sperre aufgerufen, damit sie gleich weitermachen.</summary>
    public static event Action<string, string>? Resumed;

    public static void Init(string dataDir)
    {
        _file = Path.Combine(dataDir, "job-control.json");
        try
        {
            if (File.Exists(_file) && JsonSerializer.Deserialize<Dictionary<string, State>>(File.ReadAllText(_file)) is { } all)
                foreach (var (k, v) in all) States[k] = v with { SkipUntil = v.SkipUntil is { } s ? DateTime.SpecifyKind(s, DateTimeKind.Utc) : null };
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
    }

    private static void Save()
    {
        if (_file == null) return;
        var keep = States.Where(x => x.Value.Paused || x.Value.SkipUntil > DateTime.UtcNow).ToDictionary(x => x.Key, x => x.Value);
        try { File.WriteAllText(_file + ".tmp", JsonSerializer.Serialize(keep)); File.Move(_file + ".tmp", _file, overwrite: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public static bool Paused(string user, string kind) => States.TryGetValue(Key(user, kind), out var s) && s.Paused;

    /// <summary>Bis wann übersprungen (UTC); null, wenn nicht oder vorbei.</summary>
    public static DateTime? SkippedUntil(string user, string kind) =>
        States.TryGetValue(Key(user, kind), out var s) && s.SkipUntil > DateTime.UtcNow ? s.SkipUntil : null;

    /// <summary>Darf jetzt kein neuer Lauf beginnen (angehalten oder übersprungen)?</summary>
    public static bool Blocked(string user, string kind) => Features.On(Features.JobControl) && (Paused(user, kind) || SkippedUntil(user, kind) != null);

    /// <summary>Läuft der Job gerade (zwischen <see cref="Run"/> und dessen Ende)?</summary>
    public static bool Running(string user, string kind) => Active.TryGetValue(Key(user, kind), out var n) && n > 0;

    /// <summary>Ein Lauf beginnt; mit „using“ endet er (offene Abbruchwünsche verfallen dann).</summary>
    public static IDisposable Run(string user, string kind)
    {
        var k = Key(user, kind);
        Active.AddOrUpdate(k, 1, (_, n) => n + 1);
        return new Scope(k);
    }

    private sealed class Scope(string key) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 1) return;
            if (Active.AddOrUpdate(key, 0, (_, n) => Math.Max(0, n - 1)) == 0) StopRequests.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Prüfstelle: wirft <see cref="Stopped"/>, wenn der Lauf enden soll. Angehalten: Jobs ohne Zeitplan (Übertragung) warten hier
    /// bis „Fortsetzen“ oder „Abbrechen“; die anderen enden und machen später weiter.
    /// </summary>
    public static async Task CheckAsync(string user, string kind, CancellationToken ct)
    {
        if (!Features.On(Features.JobControl)) return;
        var k = Key(user, kind);
        while (Paused(user, kind) && OneOff.Contains(kind) && !StopRequests.ContainsKey(k)) await Task.Delay(TimeSpan.FromSeconds(1), ct);
        if (StopRequests.TryGetValue(k, out var reason)) throw new Stopped(reason);
        if (Paused(user, kind)) throw new Stopped("Angehalten.");
        if (SkippedUntil(user, kind) != null) throw new Stopped("Übersprungen.");
    }

    /// <summary>Synchrone Prüfstelle (nur für Jobs mit Zeitplan - wartet nie).</summary>
    public static void Check(string user, string kind)
    {
        if (!Features.On(Features.JobControl)) return;
        var k = Key(user, kind);
        if (StopRequests.TryGetValue(k, out var reason)) throw new Stopped(reason);
        if (Paused(user, kind)) throw new Stopped("Angehalten.");
        if (SkippedUntil(user, kind) != null) throw new Stopped("Übersprungen.");
    }

    private static void Set(string user, string kind, Func<State, State> change)
    {
        States.AddOrUpdate(Key(user, kind), _ => change(new State(false, null)), (_, s) => change(s));
        Save();
    }

    public static void Pause(string user, string kind) => Set(user, kind, s => s with { Paused = true });

    public static void Resume(string user, string kind)
    {
        Set(user, kind, _ => new State(false, null));
        Resumed?.Invoke(user, kind);
    }

    /// <summary>Überspringen bis <paramref name="until"/> (der nächste Termin des Jobs); bei Jobs ohne Zeitplan wie Abbrechen.</summary>
    public static void Skip(string user, string kind, DateTime until)
    {
        if (OneOff.Contains(kind)) { Cancel(user, kind); return; }
        Set(user, kind, s => s with { SkipUntil = until });
        if (Running(user, kind)) StopRequests[Key(user, kind)] = "Übersprungen.";
    }

    /// <summary>Nur den laufenden Lauf beenden. False, wenn keiner läuft.</summary>
    public static bool Cancel(string user, string kind)
    {
        if (!Running(user, kind)) return false;
        StopRequests[Key(user, kind)] = "Abgebrochen.";
        return true;
    }

    /// <summary>Ein ausdrücklicher Auftrag („Jetzt neu einlesen“, „Jetzt sichern“) hebt das Überspringen auf - nicht das Anhalten.</summary>
    public static void ClearSkip(string user, string kind)
    {
        if (States.TryGetValue(Key(user, kind), out var s) && s.SkipUntil != null) Set(user, kind, x => x with { SkipUntil = null });
    }

    /// <summary>Nur für Tests.</summary>
    internal static void Reset() { States.Clear(); Active.Clear(); StopRequests.Clear(); _file = null; }
}
