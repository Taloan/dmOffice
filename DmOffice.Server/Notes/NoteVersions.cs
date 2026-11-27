using DmOffice.Core;

namespace DmOffice.Notes;

/// <summary>
/// Frühere Versionen des Inhalts einer Notiz (Kennung notesversions). Sie liegen in derselben Datenbank wie die Notiz (bei geteilten
/// Notizen also beim Besitzer, im Tresor im Tresor) und zählen damit zum Speicher wie die Notiz selbst. Der Abgleich mit notizi sieht
/// sie nicht: eigene Tabellen, keine Revision, kein Grabstein.
///
/// Gebündelt statt bei jedem automatischen Speichern: Vor einer Änderung bleibt der bisherige Stand als Version, wenn
/// - es noch keine Version gibt oder die letzte mindestens <see cref="Gap"/> alt ist,
/// - jemand anderes (anderes Gerät, anderer Browser, andere Person) den bisherigen Stand geschrieben hat, oder
/// - die Änderung groß ist (mindestens 100 Zeichen und ein Viertel des Textes auf einmal, etwa versehentlich viel gelöscht).
/// Leere Stände (frisch angelegte Notiz) und ein Stand, der schon als letzte Version vorliegt, kommen nicht hinzu.
/// Je Notiz höchstens <see cref="Keep"/> Versionen; ältere als <see cref="MaxAge"/> fallen weg, die letzten <see cref="KeepAlways"/> bleiben.
/// </summary>
public static class NoteVersions
{
    public const string Schema = @"CREATE TABLE IF NOT EXISTS note_versions (id INTEGER PRIMARY KEY AUTOINCREMENT, sync_id TEXT NOT NULL,
    at TEXT NOT NULL, kept INTEGER NOT NULL, source TEXT NULL, device TEXT NULL, author TEXT NULL,
    title TEXT NOT NULL, html TEXT NOT NULL, plain TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS note_versions_note ON note_versions(sync_id, id);
CREATE TABLE IF NOT EXISTS note_version_head (sync_id TEXT PRIMARY KEY, source TEXT NULL, device TEXT NULL, author TEXT NULL);";

    /// <summary>Mindestabstand zwischen zwei Versionen derselben Quelle.</summary>
    public static readonly TimeSpan Gap = TimeSpan.FromMinutes(10);
    /// <summary>Höchstens so viele Versionen je Notiz.</summary>
    public const int Keep = 50;
    /// <summary>Ältere Versionen fallen weg ...</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(90);
    /// <summary>... außer den letzten so vielen.</summary>
    public const int KeepAlways = 5;

    /// <summary>Wer einen Stand geschrieben hat: web (Browser), notizi (Abgleich, Device = Gerätename), dmoffice (Pukki, Rezepte, Aufgaben).</summary>
    public sealed record Source(string Kind, string? Device = null, string? Author = null)
    {
        public static readonly Source DmOffice = new("dmoffice");
        public bool Same(Source? o) => o != null && o.Kind == Kind && o.Device == Device && o.Author == Author;
    }

    /// <summary>Eintrag der Liste (ohne Inhalt).</summary>
    public sealed record Entry(long Id, DateTime At, string? Source, string? Device, string? Author, string Title, int Length);
    /// <summary>Eine Version mit Inhalt.</summary>
    public sealed record Version(long Id, DateTime At, string? Source, string? Device, string? Author, string Title, string Html, string Plain);

    /// <summary>Stand einer Notiz vor der Änderung.</summary>
    public sealed record State(string Html, string Plain, string Title, DateTime Modified);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ISqlStore, object> Ready = new();

    /// <summary>Tabellen anlegen; beim ersten Mal je Datenbank auch zu alte Versionen aller Notizen aufräumen.</summary>
    public static void Ensure(ISqlStore db)
    {
        if (Ready.TryGetValue(db, out _)) return;
        db.Exec(Schema);
        PruneAll(db, DateTime.UtcNow);
        Ready.AddOrUpdate(db, new object());
    }

    /// <summary>
    /// Vor dem Überschreiben des Inhalts: den bisherigen Stand als Version aufheben, wenn es sich lohnt (siehe oben), und merken, wer
    /// den neuen Stand geschrieben hat. Ohne Kennung notesversions: nichts.
    /// </summary>
    public static void BeforeChange(ISqlStore db, string syncId, State old, string newHtml, string newPlain, Source by, DateTime now)
    {
        if (!Features.On(Features.NotesVersions) || old.Html == newHtml) return;
        Ensure(db);
        var head = Head(db, syncId);
        var last = db.Query("SELECT kept FROM note_versions WHERE sync_id=$s ORDER BY id DESC LIMIT 1", r => (long?)r.GetInt64(0), ("$s", syncId))
            .FirstOrDefault();
        var due = last == null
                  || now - DateTimeOffset.FromUnixTimeSeconds(last.Value).UtcDateTime >= Gap
                  || (head != null && !head.Same(by))
                  || Big(old.Plain, newPlain);
        if (due) Add(db, syncId, old, head, now);
        SetHead(db, syncId, by);
    }

    /// <summary>Den Stand auf jeden Fall aufheben (vor dem Wiederherstellen) - außer er ist leer oder schon die letzte Version.</summary>
    public static void KeepNow(ISqlStore db, string syncId, State state, DateTime now)
    {
        Ensure(db);
        Add(db, syncId, state, Head(db, syncId), now);
    }

    private static void Add(ISqlStore db, string syncId, State old, Source? head, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(old.Plain) && !old.Html.Contains("<img", StringComparison.OrdinalIgnoreCase)) return;
        var lastHtml = db.Scalar("SELECT html FROM note_versions WHERE sync_id=$s ORDER BY id DESC LIMIT 1", ("$s", syncId)) as string;
        if (lastHtml == old.Html) return;
        db.Exec(@"INSERT INTO note_versions(sync_id, at, kept, source, device, author, title, html, plain)
                  VALUES($s, $a, $k, $src, $d, $au, $t, $h, $p)",
            ("$s", syncId), ("$a", old.Modified.ToUniversalTime().ToString("o")), ("$k", new DateTimeOffset(now).ToUnixTimeSeconds()),
            ("$src", head?.Kind), ("$d", head?.Device), ("$au", head?.Author), ("$t", old.Title), ("$h", old.Html), ("$p", old.Plain));
        Prune(db, syncId, now);
    }

    /// <summary>Viel auf einmal geändert: mindestens 100 Zeichen und ein Viertel des bisherigen Textes (gleicher Anfang und
    /// gleiches Ende zählen nicht).</summary>
    public static bool Big(string old, string now)
    {
        int pre = 0, max = Math.Min(old.Length, now.Length);
        while (pre < max && old[pre] == now[pre]) pre++;
        int suf = 0;
        while (suf < max - pre && old[old.Length - 1 - suf] == now[now.Length - 1 - suf]) suf++;
        var changed = Math.Max(old.Length, now.Length) - pre - suf;
        return changed >= 100 && changed * 4 >= old.Length;
    }

    private static void Prune(ISqlStore db, string syncId, DateTime now)
    {
        db.Exec("DELETE FROM note_versions WHERE sync_id=$s AND id NOT IN (SELECT id FROM note_versions WHERE sync_id=$s ORDER BY id DESC LIMIT $k)",
            ("$s", syncId), ("$k", Keep));
        db.Exec(@"DELETE FROM note_versions WHERE sync_id=$s AND kept < $old
                  AND id NOT IN (SELECT id FROM note_versions WHERE sync_id=$s ORDER BY id DESC LIMIT $k)",
            ("$s", syncId), ("$old", new DateTimeOffset(now - MaxAge).ToUnixTimeSeconds()), ("$k", KeepAlways));
    }

    /// <summary>Zu alte Versionen aller Notizen (die letzten <see cref="KeepAlways"/> je Notiz bleiben).</summary>
    public static void PruneAll(ISqlStore db, DateTime now) =>
        db.Exec(@"DELETE FROM note_versions WHERE kept < $old AND id NOT IN (
                    SELECT id FROM (SELECT id, ROW_NUMBER() OVER (PARTITION BY sync_id ORDER BY id DESC) AS n FROM note_versions) WHERE n <= $k)",
            ("$old", new DateTimeOffset(now - MaxAge).ToUnixTimeSeconds()), ("$k", KeepAlways));

    private static Source? Head(ISqlStore db, string syncId) =>
        db.Query("SELECT source, device, author FROM note_version_head WHERE sync_id=$s",
            r => new Source(r.IsDBNull(0) ? "" : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)),
            ("$s", syncId)).FirstOrDefault();

    public static void SetHead(ISqlStore db, string syncId, Source by)
    {
        Ensure(db);
        db.Exec(@"INSERT INTO note_version_head(sync_id, source, device, author) VALUES($s, $k, $d, $a)
                  ON CONFLICT(sync_id) DO UPDATE SET source=$k, device=$d, author=$a",
            ("$s", syncId), ("$k", by.Kind), ("$d", by.Device), ("$a", by.Author));
    }

    public static List<Entry> List(ISqlStore db, string syncId)
    {
        Ensure(db);
        return db.Query("SELECT id, at, source, device, author, title, length(plain) FROM note_versions WHERE sync_id=$s ORDER BY id DESC",
            r => new Entry(r.GetInt64(0), Time(r.GetString(1)), Str(r, 2), Str(r, 3), Str(r, 4), r.GetString(5), r.GetInt32(6)), ("$s", syncId));
    }

    public static Version? Get(ISqlStore db, string syncId, long id)
    {
        Ensure(db);
        return db.Query("SELECT id, at, source, device, author, title, html, plain FROM note_versions WHERE sync_id=$s AND id=$i",
            r => new Version(r.GetInt64(0), Time(r.GetString(1)), Str(r, 2), Str(r, 3), Str(r, 4), r.GetString(5), r.GetString(6), r.GetString(7)),
            ("$s", syncId), ("$i", id)).FirstOrDefault();
    }

    /// <summary>Notiz endgültig weg: ihre Versionen auch.</summary>
    public static void Forget(ISqlStore db, string syncId)
    {
        Ensure(db);
        db.Exec("DELETE FROM note_versions WHERE sync_id=$s; DELETE FROM note_version_head WHERE sync_id=$s;", ("$s", syncId));
    }

    /// <summary>Browser aus dem User-Agent, sprachneutral („Chrome · Windows“) - die Oberfläche zeigt ihn als Gerät.</summary>
    public static string BrowserOf(string ua)
    {
        var browser = ua.Contains("Edg/") ? "Edge" : ua.Contains("OPR/") ? "Opera" : ua.Contains("Firefox/") ? "Firefox"
            : ua.Contains("Chrome/") ? "Chrome" : ua.Contains("Safari/") ? "Safari" : null;
        var os = ua.Contains("Android") ? "Android" : ua.Contains("iPhone") || ua.Contains("iPad") ? "iOS" : ua.Contains("Windows") ? "Windows"
            : ua.Contains("Mac OS X") ? "macOS" : ua.Contains("Linux") ? "Linux" : null;
        return string.Join(" · ", new[] { browser, os }.Where(x => x != null));
    }

    private static string? Str(Microsoft.Data.Sqlite.SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static DateTime Time(string s) =>
        DateTime.Parse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
}
