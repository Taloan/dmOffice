using System.Collections.Concurrent;
using DmOffice.Core;
using DmOffice.Storage;

namespace DmOffice.Audio;

/// <summary>
/// Audiothek-Ordner neu zuordnen (wie <see cref="DmOffice.Gallery.GalleryRelink"/>): derselbe Ordner zeigt auf einen anderen externen
/// Ordner oder Pfad. Titel mit Tags und Cover, Hörverlauf und Wiedergabelisten bleiben; die Pfade werden umgerechnet. Danach sucht
/// ein Abgleich im Hintergrund jeden Titel am neuen Ort; was dort fehlt, geht aus der Audiothek.
/// </summary>
public static class AudioRelink
{
    private static readonly ConcurrentDictionary<string, bool> Busy = new();

    public static bool IsBusy(string userId, string sourceId) => Busy.ContainsKey(userId + "/" + sourceId);

    public sealed record Probe(int Checked, int Found, int Total);

    public static string? Problem(UserSpace space, ServerConfig config, AudioSource src, string mount, string path)
    {
        if (!ExternalFolders.MountsOf(space, config).Any(m => m.Id == mount)) return "Diesen externen Ordner gibt es nicht.";
        if (src.Mount == mount && src.Path == path) return "Der Audiothek-Ordner zeigt schon auf diesen Ordner.";
        if (AudioFolders.Of(space.Db).Any(s => s.Id != src.Id && s.Mount == mount && s.Path == path)) return "Dieser Ordner ist schon in der Audiothek.";
        return null;
    }

    public static async Task<Probe> ProbeAsync(UserSpace space, ServerConfig config, AudioSource src, string mount, string path, CancellationToken ct)
    {
        var db = space.Db;
        AudioFolders.Ensure(db);
        var total = Convert.ToInt32(db.Scalar("SELECT COUNT(*) FROM audio_track WHERE source = $s", ("$s", src.Id)) ?? 0L);
        var sample = db.Query($"SELECT path, size FROM audio_track WHERE source = $s ORDER BY random() LIMIT {FolderRelink.SampleMax}",
                r => (Path: FolderRelink.Map(src.Path, path, r.GetString(0)), Size: r.GetInt64(1)), ("$s", src.Id))
            .Where(x => x.Path != null).Select(x => (x.Path!, x.Size)).ToList();
        var (driver, _) = ExternalFolders.Open(space, config, mount);
        using (driver)
        {
            if (path.Length > 0 && await driver.StatAsync(path, ct) is not { IsDir: true }) throw new ArgumentException("Diesen Ordner gibt es nicht.");
            var (n, found) = await FolderRelink.ProbeAsync(driver, sample, ct);
            return new Probe(n, found, total);
        }
    }

    /// <summary>Umstellen; gibt einen Fehlertext zurück oder null.</summary>
    public static string? Apply(UserSpace space, ServerConfig config, string id, string mount, string path)
    {
        var user = space.User.Id;
        var key = user + "/" + id;
        var db = space.Db;
        Busy[key] = true;
        try
        {
            if (AudioScanJob.IsBusy(user, id)) return "Der Ordner wird gerade eingelesen. Bitte gleich noch einmal versuchen.";
            var list = AudioFolders.Of(db);
            if (list.FirstOrDefault(s => s.Id == id) is not { } src) throw new KeyNotFoundException("Nicht gefunden.");
            if (Problem(space, config, src, mount, path) is { } problem) return problem;
            AudioFolders.Ensure(db);
            AudioPlaylists.Ensure(db);
            var (oldMount, oldRoot) = (src.Mount, src.Path);
            var (f, t) = (FolderRelink.Prefix(oldRoot), FolderRelink.Prefix(path));
            var rows = db.Query("SELECT path FROM audio_track WHERE source = $s", r => r.GetString(0), ("$s", id));
            // Tags und Cover für alle (Server-Ordner): unter dem neuen Ort übernehmen
            // Nur aus einem Server-Ordner, auf den die Person noch Zugriff hat
            if (AudioFolders.Shared is { } shared && config.Current.ExternalFolders.Any(x => x.Id == oldMount) && config.Current.ExternalFolders.Any(x => x.Id == mount)
                && ExternalFolders.MountsOf(space, config).Any(m => m.Id == oldMount))
                shared.CopyAll(oldMount, mount, rows.Select(p => (From: p, To: FolderRelink.Map(oldRoot, path, p))).Where(x => x.To != null).Select(x => (x.From, x.To!)));
            using (var conn = db.Open())
            using (var tx = conn.BeginTransaction())
            {
                if (f.Length > 0) UserDb.Exec(conn, "DELETE FROM audio_track WHERE source = $s AND substr(path, 1, length($f)) <> $f", ("$s", id), ("$f", f));
                FolderRelink.RenameKeys(conn, "audio_track", "path", f, t, "source = $s", ("$s", id));
                UserDb.Exec(conn, "UPDATE audio_track SET mount = $m WHERE source = $s", ("$m", mount), ("$s", id));
                // Hörverlauf und Wiedergabelisten hängen an externem Ordner und Pfad
                UserDb.Exec(conn, "UPDATE audio_play SET mount = $nm, path = $t || substr(path, length($f) + 1) WHERE mount = $om AND substr(path, 1, length($f)) = $f",
                    ("$nm", mount), ("$om", oldMount), ("$f", f), ("$t", t));
                UserDb.Exec(conn, "UPDATE audio_playlist_item SET mount = $nm, path = $t || substr(path, length($f) + 1) WHERE mount = $om AND substr(path, 1, length($f)) = $f",
                    ("$nm", mount), ("$om", oldMount), ("$f", f), ("$t", t));
                // Statistik: am neuen Ort zusammenführen, falls dort schon etwas steht
                var stats = UserDb.Query(conn, "SELECT path, plays, skips, last FROM audio_stat WHERE mount = $om AND substr(path, 1, length($f)) = $f",
                    r => (Path: r.GetString(0), Plays: r.GetInt64(1), Skips: r.GetInt64(2), Last: r.IsDBNull(3) ? (long?)null : r.GetInt64(3)), ("$om", oldMount), ("$f", f));
                foreach (var x in stats)
                {
                    UserDb.Exec(conn, "DELETE FROM audio_stat WHERE mount = $om AND path = $p", ("$om", oldMount), ("$p", x.Path));
                    UserDb.Exec(conn, @"INSERT INTO audio_stat(mount, path, plays, skips, last) VALUES($m, $p, $pl, $sk, $l)
                        ON CONFLICT(mount, path) DO UPDATE SET plays = plays + excluded.plays, skips = skips + excluded.skips,
                            last = CASE WHEN last IS NULL OR excluded.last > last THEN excluded.last ELSE last END",
                        ("$m", mount), ("$p", t + x.Path[f.Length..]), ("$pl", x.Plays), ("$sk", x.Skips), ("$l", x.Last));
                }
                UserDb.Exec(conn, "UPDATE audio_ver SET v = v + 1 WHERE id = 1");
                tx.Commit();
            }
            src.Mount = mount;
            src.Path = path;
            src.Error = null;
            src.Relink = new RelinkState { At = DateTime.UtcNow, Pending = true, Total = rows.Count };
            AudioFolders.Save(db, list);
            AudioScanJob.Request(user, id);
            return null;
        }
        finally { Busy.TryRemove(key, out _); }
    }

    private static string Label(UserSpace space, ServerConfig config, AudioSource src) =>
        (ExternalFolders.MountsOf(space, config).FirstOrDefault(m => m.Id == src.Mount)?.Name ?? src.Mount) + (src.Path.Length > 0 ? " › " + src.Path.Replace("/", " › ") : "");

    /// <summary>Abgleich nach dem Umstellen (wie in der Galerie). True, wenn er fertig ist.</summary>
    public static async Task<bool> CheckAsync(UserSpace space, ServerConfig config, AudioSource src, ILogger log, CancellationToken ct)
    {
        var db = space.Db;
        AudioFolders.Ensure(db);
        var paths = db.Query("SELECT path FROM audio_track WHERE source = $s ORDER BY path", r => r.GetString(0), ("$s", src.Id));
        var label = Label(space, config, src);
        var total = paths.Count;
        using var job = Activity.Start(space.User.Id, Activity.AudioRelink, $"Abgleich {label}", total);
        List<string> missing;
        try
        {
            var (driver, _) = ExternalFolders.Open(space, config, src.Mount);
            using (driver)
                missing = await FolderRelink.MissingAsync(driver, src.Path, paths,
                    (done, found) => job.Progress(done, total, $"Abgleich {label}: {DmOffice.Core.Num.N(found)} von {DmOffice.Core.Num.N(total)} am neuen Ziel gefunden"),
                    () => AudioFolders.Of(db).FirstOrDefault(x => x.Id == src.Id) is { } c && c.Mount == src.Mount && c.Path == src.Path, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or KeyNotFoundException or HttpRequestException or ArgumentException)
        {
            Update(db, src, s => s.Error = e.Message);
            job.Fail($"{label}: {e.Message}");
            log.LogWarning("Audiothek-Ordner {User}/{Source}: Abgleich nach dem Neuzuordnen: {Error}", space.User.Login, src.Id, e.Message);
            return false;
        }
        AudioFolders.Remove(db, src.Id, missing);
        var found = total - missing.Count;
        Update(db, src, s => { s.Pending = false; s.Error = null; s.Total = total; s.Found = found; s.RemovedCount = missing.Count; s.Removed = missing.Take(FolderRelink.ReportMax).ToList(); });
        job.Finish($"Abgleich {label}: {DmOffice.Core.Num.N(found)} von {DmOffice.Core.Num.N(total)} am neuen Ziel gefunden" + (missing.Count > 0 ? $", {DmOffice.Core.Num.N(missing.Count)} aus der Audiothek entfernt" : ""),
            missing.Take(FolderRelink.ReportMax).ToList());
        log.LogInformation("Audiothek-Ordner {User}/{Source}: Abgleich nach dem Neuzuordnen, {Found} von {Total} gefunden, {Missing} entfernt", space.User.Login, src.Id, found, total, missing.Count);
        return true;
    }

    private static void Update(UserDb db, AudioSource src, Action<RelinkState> change)
    {
        var list = AudioFolders.Of(db);
        if (list.FirstOrDefault(x => x.Id == src.Id) is not { } cur || cur.Mount != src.Mount || cur.Path != src.Path) return;
        cur.Relink ??= new RelinkState { At = DateTime.UtcNow };
        change(cur.Relink);
        AudioFolders.Save(db, list);
    }
}
