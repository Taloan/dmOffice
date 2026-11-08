using DmOffice.Core;
using DmOffice.Storage;

namespace DmOffice.Audio;

/// <summary>
/// Playlists der Audiothek (Stufe 2): eigene Playlists je Benutzer und Auto-Playlists aus dem Hörverlauf. Einträge hängen wie
/// der Hörverlauf an externem Ordner und Pfad - nimmt man einen Audiothek-Ordner heraus, bleiben sie und erscheinen wieder,
/// sobald der Ordner zurück ist. Nach außen sind es Kennungen „Quelle/Pfad“ wie in der Titelliste.
/// </summary>
public static class AudioPlaylists
{
    public const int MaxItems = 10_000;
    public const int AutoSize = 100;
    /// <summary>„Selten gehört“: zuletzt vor mehr als so vielen Tagen gehört</summary>
    public const int RareDays = 90;

    /// <summary>Kennungen der Auto-Playlists - in dieser Reihenfolge in der Seitenleiste.</summary>
    public static readonly string[] AutoKinds = ["added", "recent", "most", "rare", "never"];

    public static void Ensure(UserDb db)
    {
        AudioFolders.Ensure(db);
        db.Exec(@"CREATE TABLE IF NOT EXISTS audio_playlist(id TEXT PRIMARY KEY, name TEXT NOT NULL, created INTEGER NOT NULL, modified INTEGER NOT NULL) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS audio_playlist_item(playlist TEXT NOT NULL, pos INTEGER NOT NULL, mount TEXT NOT NULL, path TEXT NOT NULL, PRIMARY KEY(playlist, pos)) WITHOUT ROWID;");
    }

    public sealed record Info(string Id, string Name, int Count, long Modified, string? Cover);

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>Sichtbare Titel: (externer Ordner, Pfad) → Kennung und Cover - der erste sichtbare Audiothek-Ordner gewinnt.</summary>
    private static Dictionary<(string, string), (string Id, string? Cover)> Present(UserSpace space, ServerConfig config)
    {
        var mounts = AudioFolders.Of(space.Db).ToDictionary(s => s.Id, s => s.Mount);
        var map = new Dictionary<(string, string), (string, string?)>();
        foreach (var t in AudioFolders.List(space, config))
            if (mounts.TryGetValue(t.Source, out var mount)) map.TryAdd((mount, t.Path), (t.Source + "/" + t.Path, t.Cover));
        return map;
    }

    private static List<(string Mount, string Path)> Items(UserDb db, string id) =>
        db.Query("SELECT mount, path FROM audio_playlist_item WHERE playlist = $p ORDER BY pos", r => (r.GetString(0), r.GetString(1)), ("$p", id));

    public static List<Info> List(UserSpace space, ServerConfig config)
    {
        Ensure(space.Db);
        var present = Present(space, config);
        return space.Db.Query("SELECT id, name, modified FROM audio_playlist ORDER BY name COLLATE NOCASE", r => (r.GetString(0), r.GetString(1), r.GetInt64(2)))
            .Select(p =>
            {
                var items = Items(space.Db, p.Item1).Where(present.ContainsKey).Select(k => present[k]).ToList();
                return new Info(p.Item1, p.Item2, items.Count, p.Item3, items.FirstOrDefault(x => x.Cover != null).Cover);
            }).ToList();
    }

    /// <summary>Titel einer Playlist (nur sichtbare, in ihrer Reihenfolge); KeyNotFound, wenn es sie nicht gibt.</summary>
    public static (string Name, List<string> Tracks) Get(UserSpace space, ServerConfig config, string id)
    {
        Ensure(space.Db);
        var name = space.Db.Scalar("SELECT name FROM audio_playlist WHERE id = $i", ("$i", id)) as string ?? throw new KeyNotFoundException("Diese Playlist gibt es nicht.");
        var present = Present(space, config);
        return (name, Items(space.Db, id).Where(present.ContainsKey).Select(k => present[k].Id).ToList());
    }

    private static string CleanName(string? name)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) throw new ArgumentException("Bitte einen Namen eingeben.");
        return n.Length > 200 ? n[..200] : n;
    }

    /// <summary>Kennungen „Quelle/Pfad“ zu (externer Ordner, Pfad); unbekannte fallen weg.</summary>
    private static List<(string Mount, string Path)> Keys(UserSpace space, ServerConfig config, IEnumerable<string>? ids)
    {
        var list = new List<(string, string)>();
        foreach (var id in ids ?? [])
        {
            try { var (src, path) = AudioFolders.Resolve(space, config, id); list.Add((src.Mount, path)); }
            catch (KeyNotFoundException) { }
        }
        return list;
    }

    private static void Write(UserDb db, string id, List<(string Mount, string Path)> items)
    {
        if (items.Count > MaxItems) throw new ArgumentException($"Eine Playlist hat höchstens {MaxItems:N0} Titel.");
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        UserDb.Exec(conn, "DELETE FROM audio_playlist_item WHERE playlist = $p", ("$p", id));
        for (var i = 0; i < items.Count; i++)
            UserDb.Exec(conn, "INSERT INTO audio_playlist_item(playlist, pos, mount, path) VALUES($p, $i, $m, $a)", ("$p", id), ("$i", i), ("$m", items[i].Mount), ("$a", items[i].Path));
        UserDb.Exec(conn, "UPDATE audio_playlist SET modified = $t WHERE id = $p", ("$t", Now()), ("$p", id));
        tx.Commit();
    }

    public static string Create(UserSpace space, ServerConfig config, string? name, IEnumerable<string>? tracks)
    {
        Ensure(space.Db);
        var id = Guid.NewGuid().ToString("N")[..12];
        space.Db.Exec("INSERT INTO audio_playlist(id, name, created, modified) VALUES($i, $n, $t, $t)", ("$i", id), ("$n", CleanName(name)), ("$t", Now()));
        Write(space.Db, id, Keys(space, config, tracks));
        return id;
    }

    private static void Need(UserDb db, string id)
    {
        Ensure(db);
        if (db.Scalar("SELECT 1 FROM audio_playlist WHERE id = $i", ("$i", id)) == null) throw new KeyNotFoundException("Diese Playlist gibt es nicht.");
    }

    public static void Rename(UserSpace space, string id, string? name)
    {
        Need(space.Db, id);
        space.Db.Exec("UPDATE audio_playlist SET name = $n, modified = $t WHERE id = $i", ("$n", CleanName(name)), ("$t", Now()), ("$i", id));
    }

    /// <summary>Titel anhängen (auch doppelt - eine Playlist darf einen Titel mehrmals haben).</summary>
    public static int Add(UserSpace space, ServerConfig config, string id, IEnumerable<string> tracks)
    {
        Need(space.Db, id);
        var items = Items(space.Db, id);
        var add = Keys(space, config, tracks);
        items.AddRange(add);
        Write(space.Db, id, items);
        return add.Count;
    }

    /// <summary>
    /// Neue Reihenfolge der sichtbaren Titel (verschoben oder entfernt). Einträge aus gerade nicht sichtbaren Ordnern bleiben
    /// erhalten und rücken ans Ende - die Oberfläche kennt sie nicht und kann sie nicht mitschicken.
    /// </summary>
    public static void SetTracks(UserSpace space, ServerConfig config, string id, IEnumerable<string> tracks)
    {
        Need(space.Db, id);
        var present = Present(space, config);
        var hidden = Items(space.Db, id).Where(k => !present.ContainsKey(k)).ToList();
        Write(space.Db, id, [.. Keys(space, config, tracks), .. hidden]);
    }

    public static void Delete(UserSpace space, string id)
    {
        Need(space.Db, id);
        space.Db.Exec("DELETE FROM audio_playlist_item WHERE playlist = $i; DELETE FROM audio_playlist WHERE id = $i", ("$i", id));
    }

    /// <summary>
    /// Auto-Playlist aus Titelliste und Hörverlauf: added (zuletzt hinzugefügt, nach Dateidatum), recent (zuletzt gehört),
    /// most (am meisten gehört), rare (schon gehört, aber nicht in den letzten 90 Tagen, die am seltensten gehörten zuerst),
    /// never (nie gehört, zufällig). Je höchstens 100 Titel.
    /// </summary>
    public static List<string> Auto(UserSpace space, ServerConfig config, string kind, DateTime? now = null)
    {
        var tracks = AudioFolders.List(space, config);
        var stats = AudioFolders.Stats(space, config);
        string Id(AudioFolders.Track t) => t.Source + "/" + t.Path;
        AudioFolders.Stat? St(AudioFolders.Track t) => stats.GetValueOrDefault(Id(t));
        var before = new DateTimeOffset(now ?? DateTime.UtcNow).AddDays(-RareDays).ToUnixTimeMilliseconds();
        IEnumerable<AudioFolders.Track> pick = kind switch
        {
            "added" => tracks.OrderByDescending(t => t.Modified),
            "recent" => tracks.Where(t => St(t)?.Last != null).OrderByDescending(t => St(t)!.Last),
            "most" => tracks.Where(t => St(t)?.Plays > 0).OrderByDescending(t => St(t)!.Plays).ThenByDescending(t => St(t)!.Last),
            "rare" => tracks.Where(t => St(t) is { Plays: > 0, Last: { } l } && l < before).OrderBy(t => St(t)!.Plays).ThenBy(t => St(t)!.Last),
            "never" => tracks.Where(t => (St(t)?.Plays ?? 0) == 0).OrderBy(_ => Random.Shared.Next()),
            _ => throw new KeyNotFoundException("Diese Auto-Playlist gibt es nicht."),
        };
        return pick.Take(AutoSize).Select(Id).ToList();
    }
}
