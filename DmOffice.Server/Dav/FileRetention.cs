using DmOffice.Core;
using Microsoft.Data.Sqlite;

namespace DmOffice.Dav;

/// <summary>
/// Aufbewahrung je Benutzer: fruehere Versionen und Papierkorb. Einstellungen liegen in der kv-Tabelle der
/// Benutzerdatenbank.
///
/// Versionen, drei Arten:
///  - auto: Ausduennen wie Nextcloud/Time Machine - aus den letzten 24 Stunden eine je Stunde, aus den letzten
///    30 Tagen eine je Tag, danach eine je Woche; aelter als das Hoechstalter faellt weg.
///  - fixed: hoechstens N Versionen je Datei, hoechstens D Tage.
///  - off: keine Versionen.
/// Dazu eine Speichergrenze fuer alle Versionen zusammen (aelteste zuerst weg). Der Papierkorb hat Tage und
/// ebenfalls eine optionale Speichergrenze.
/// </summary>
public static class FileRetention
{
    public sealed record Settings(string VersionsMode, int VersionsMax, int VersionsDays, int VersionsMb, int TrashDays, int TrashMb,
        int LinkMaxDownloads, int LinkDays);

    private const string Prefix = "files.";

    /// <summary>Hoechstwerte des Administrators (Program.cs: aus server.json).</summary>
    public static Func<FileLimits> LimitsOf = () => new FileLimits();

    /// <summary>Wert des Benutzers unter dem Hoechstwert; 0 heisst "unbegrenzt" - mit Hoechstwert also der Hoechstwert.</summary>
    public static int Cap(int value, int max) => max <= 0 ? value : value <= 0 ? max : Math.Min(value, max);

    /// <summary>Die wirksamen Werte: Einstellungen des Benutzers, begrenzt durch die Verwaltung.</summary>
    public static Settings Effective(Settings s, FileLimits l) => s with
    {
        VersionsDays = Cap(s.VersionsDays, l.VersionsDays),
        VersionsMb = Cap(s.VersionsMb, l.VersionsMb),
        TrashDays = Cap(s.TrashDays, l.TrashDays),
        TrashMb = Cap(s.TrashMb, l.TrashMb),
        LinkMaxDownloads = Cap(s.LinkMaxDownloads, l.LinkMaxDownloads),
        LinkDays = Cap(s.LinkDays, l.LinkDays),
    };

    public static Settings Load(UserDb db) => Effective(LoadRaw(db), LimitsOf());

    private static Settings LoadRaw(UserDb db)
    {
        int I(string k, int d) => int.TryParse(db.Get(Prefix + k), out var v) ? v : d;
        var mode = db.Get(Prefix + "versions.mode") is { } m && m is "fixed" or "off" ? m : "auto";
        return new Settings(mode, I("versions.max", 10), I("versions.days", mode == "fixed" ? 30 : 365), I("versions.mb", 0),
            // bisheriger Schluessel fuer den Papierkorb
            int.TryParse(db.Get(FilesApi.TrashDaysKey), out var td) ? td : FileStore.DefaultTrashDays, I("trash.mb", 0),
            I("links.maxDownloads", 10), I("links.days", 0));
    }

    public static void Save(UserDb db, Settings s)
    {
        if (s.VersionsMode is not ("auto" or "fixed" or "off")) throw new ArgumentException("Unbekannte Art der Versionen.");
        s = Effective(s, LimitsOf());
        static int Clamp(int v, int max) => Math.Clamp(v, 0, max);
        db.Set(Prefix + "versions.mode", s.VersionsMode);
        db.Set(Prefix + "versions.max", Math.Clamp(s.VersionsMax, 1, 500).ToString());
        db.Set(Prefix + "versions.days", Clamp(s.VersionsDays, 36500).ToString());
        db.Set(Prefix + "versions.mb", Clamp(s.VersionsMb, 10_000_000).ToString());
        db.Set(FilesApi.TrashDaysKey, Clamp(s.TrashDays, 3650).ToString());
        db.Set(Prefix + "trash.mb", Clamp(s.TrashMb, 10_000_000).ToString());
        db.Set(Prefix + "links.maxDownloads", Clamp(s.LinkMaxDownloads, 10_000).ToString());
        db.Set(Prefix + "links.days", Clamp(s.LinkDays, 3650).ToString());
    }

    /// <summary>Beim Ueberschreiben: Version behalten? Und wie viele je Datei hoechstens (Schutz gegen sehr viele
    /// schnelle Speicherungen auch im Modus auto - das Ausduennen raeumt danach auf).</summary>
    public static (bool Keep, int MaxPerFile) OnSave(Settings s) => s.VersionsMode switch
    {
        "off" => (false, 0),
        "fixed" => (true, s.VersionsMax),
        _ => (true, 200),
    };

    /// <summary>Welche Versionen das Ausduennen behaelt: je Zeitfenster die neueste.</summary>
    public static HashSet<long> Thin(IEnumerable<(long Id, DateTime Saved)> versions, DateTime now, int maxDays)
    {
        var keep = new HashSet<long>();
        var seen = new HashSet<string>();
        foreach (var (id, saved) in versions.OrderByDescending(v => v.Saved).ThenByDescending(v => v.Id))
        {
            var age = now - saved;
            if (maxDays > 0 && age > TimeSpan.FromDays(maxDays)) continue;
            string bucket = age < TimeSpan.FromHours(24) ? "h" + saved.ToString("yyyyMMddHH")
                : age < TimeSpan.FromDays(30) ? "d" + saved.ToString("yyyyMMdd")
                : "w" + System.Globalization.ISOWeek.GetYear(saved) + "-" + System.Globalization.ISOWeek.GetWeekOfYear(saved);
            if (seen.Add(bucket)) keep.Add(id);
        }
        return keep;
    }

    /// <summary>Stuendlich: Versionen nach Art ausduennen bzw. kuerzen, Papierkorb nach Tagen und Grenze leeren.</summary>
    public static int Apply(UserDb db, DateTime now)
    {
        var s = Load(db);
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        int n = 0;
        string Cut(int days) => now.AddDays(-days).ToUniversalTime().ToString("o");

        if (s.VersionsMode == "off") n += UserDb.Exec(conn, "DELETE FROM file_versions");
        else if (s.VersionsMode == "fixed")
        {
            if (s.VersionsDays > 0) n += UserDb.Exec(conn, "DELETE FROM file_versions WHERE saved < $c", ("$c", Cut(s.VersionsDays)));
            n += UserDb.Exec(conn, @"DELETE FROM file_versions WHERE id IN (SELECT id FROM (SELECT id, ROW_NUMBER() OVER (PARTITION BY fileid ORDER BY id DESC) AS r
                FROM file_versions) WHERE r > $m)", ("$m", s.VersionsMax));
        }
        else
        {
            var all = UserDb.Query(conn, "SELECT id, fileid, saved FROM file_versions",
                r => (Id: r.GetInt64(0), File: r.GetInt64(1), Saved: DateTime.Parse(r.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime()));
            foreach (var g in all.GroupBy(v => v.File))
            {
                var keep = Thin(g.Select(v => (v.Id, v.Saved)), now.ToUniversalTime(), s.VersionsDays);
                foreach (var v in g.Where(v => !keep.Contains(v.Id)))
                    n += UserDb.Exec(conn, "DELETE FROM file_versions WHERE id=$i", ("$i", v.Id));
            }
        }
        if (s.VersionsMb > 0) n += TrimToSize(conn, "file_versions", "saved", s.VersionsMb);

        if (s.TrashDays > 0) n += UserDb.Exec(conn, "DELETE FROM file_trash WHERE deleted < $c", ("$c", Cut(s.TrashDays)));
        if (s.TrashMb > 0) n += TrimToSize(conn, "file_trash", "deleted", s.TrashMb);
        tx.Commit();
        FileStore.CollectBlobs(db);
        return n;
    }

    /// <summary>Aelteste Eintraege loeschen, bis die Tabelle unter der Grenze liegt.</summary>
    private static int TrimToSize(SqliteConnection conn, string table, string dateColumn, int mb)
    {
        long limit = mb * 1024L * 1024L;
        long used = Convert.ToInt64(UserDb.Query(conn, $"SELECT COALESCE(SUM(size),0) FROM {table}", r => r.GetInt64(0)).First());
        int n = 0;
        if (used <= limit) return 0;
        foreach (var (id, size) in UserDb.Query(conn, $"SELECT id, size FROM {table} ORDER BY {dateColumn}, id", r => (r.GetInt64(0), r.GetInt64(1))))
        {
            if (used <= limit) break;
            n += UserDb.Exec(conn, $"DELETE FROM {table} WHERE id=$i", ("$i", id));
            used -= size;
        }
        return n;
    }
}
