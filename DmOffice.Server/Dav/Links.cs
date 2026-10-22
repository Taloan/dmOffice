using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DmOffice.Core;

namespace DmOffice.Dav;

/// <summary>Ein oeffentlicher Link auf eine Datei oder einen Ordner.</summary>
/// <param name="Mode">"read" ansehen und herunterladen, "upload" dazu hochladen, "drop" nur hochladen (Inhalt bleibt verborgen).</param>
public sealed record FileLink(string Id, string Token, long FileId, string Mode, DateTime? Expires, bool HasPassword, DateTime Created, long Downloads,
    long? MaxDownloads = null)
{
    public bool Expired(DateTime now) => Expires is { } e && e <= now;
    /// <summary>Download-Grenze erreicht - der Link gilt dann als abgelaufen.</summary>
    public bool UsedUp => MaxDownloads is > 0 && Downloads >= MaxDownloads;
}

/// <summary>
/// Welcher Link gehoert wem - data/links.json, nur SHA-256 des Tokens mit Besitzer und Link-Id. Alles Weitere (welche
/// Datei, Passwort, Ablauf) steht verschluesselt in der Datenbank des Besitzers. Wer die Datei liest, erfaehrt keine Pfade
/// und kann aus den Hashes keine Links bauen.
/// </summary>
public sealed class LinkIndex
{
    private sealed record Entry(string Hash, string Owner, string Id);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _path;
    private readonly object _lock = new();
    private List<Entry> _entries;

    public LinkIndex(DataPaths paths)
    {
        _path = Path.Combine(paths.Root, "links.json");
        _entries = File.Exists(_path) ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(_path), Json) ?? new() : new();
    }

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public void Add(string owner, string id, string token)
    {
        lock (_lock) { _entries.Add(new Entry(Hash(token), owner, id)); Save(); }
    }

    public void Remove(string owner, string id)
    {
        lock (_lock) { if (_entries.RemoveAll(e => e.Owner == owner && e.Id == id) > 0) Save(); }
    }

    public (string Owner, string Id)? Find(string token)
    {
        var h = Hash(token);
        lock (_lock) return _entries.FirstOrDefault(e => e.Hash == h) is { } e ? (e.Owner, e.Id) : null;
    }

    /// <summary>Benutzer geloescht: alle seine Links weg.</summary>
    public void Forget(string owner)
    {
        lock (_lock) { if (_entries.RemoveAll(e => e.Owner == owner) > 0) Save(); }
    }

    /// <summary>Abgleich mit der Datenbank des Besitzers (Hintergrunddienst): Links, die es dort nicht mehr gibt, weg.</summary>
    public void Prune(string owner, IReadOnlySet<string> existing)
    {
        lock (_lock) { if (_entries.RemoveAll(e => e.Owner == owner && !existing.Contains(e.Id)) > 0) Save(); }
    }

    private void Save()
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_entries, Json));
        File.Move(tmp, _path, overwrite: true);
    }
}

public static class Links
{
    public static readonly string[] Modes = ["read", "upload", "drop"];

    /// <summary>144 Bit Zufall, URL-tauglich.</summary>
    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', '-').Replace('/', '_');

    private const string Columns = "id, token, fileid, mode, expires, password IS NOT NULL, created, downloads, max_downloads";

    private static FileLink Read(Microsoft.Data.Sqlite.SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetString(3),
        r.IsDBNull(4) ? null : DateTime.Parse(r.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime(),
        r.GetInt64(5) != 0, DateTime.Parse(r.GetString(6), null, System.Globalization.DateTimeStyles.RoundtripKind), r.GetInt64(7),
        r.IsDBNull(8) ? null : r.GetInt64(8));

    /// <summary>0 oder nichts: unbegrenzt; sonst 1 bis 10000.</summary>
    private static long? Max(int? max) => max is > 0 ? Math.Min(max.Value, 10_000) : null;

    public static List<FileLink> List(UserDb db, long? fileId = null) => fileId == null
        ? db.Query($"SELECT {Columns} FROM file_links ORDER BY created DESC", Read)
        : db.Query($"SELECT {Columns} FROM file_links WHERE fileid=$f ORDER BY created DESC", Read, ("$f", fileId));

    public static FileLink? Get(UserDb db, string id) => db.Query($"SELECT {Columns} FROM file_links WHERE id=$i", Read, ("$i", id)).FirstOrDefault();

    public static string? PasswordHash(UserDb db, string id) => db.Scalar("SELECT password FROM file_links WHERE id=$i", ("$i", id)) as string;

    private static string Mode(string? mode) => Modes.Contains(mode) ? mode! : throw new ArgumentException("Unbekannte Art der Freigabe.");

    public static FileLink Create(UserDb db, long fileId, string? mode, DateTime? expires, string? password, int? maxDownloads = null)
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        var token = NewToken();
        db.Exec("INSERT INTO file_links(id,token,fileid,mode,expires,password,created,max_downloads) VALUES($i,$t,$f,$m,$e,$p,$c,$x)",
            ("$i", id), ("$t", token), ("$f", fileId), ("$m", Mode(mode)), ("$e", expires?.ToUniversalTime().ToString("o")),
            ("$p", string.IsNullOrEmpty(password) ? null : Passwords.Hash(password)), ("$c", DateTime.UtcNow.ToString("o")), ("$x", Max(maxDownloads)));
        return Get(db, id)!;
    }

    /// <summary>Art, Ablauf und Passwort aendern; password null laesst es, "" entfernt es.</summary>
    public static void Update(UserDb db, string id, string? mode, DateTime? expires, string? password, int? maxDownloads = null)
    {
        if (Get(db, id) == null) throw new KeyNotFoundException("Link nicht gefunden.");
        db.Exec("UPDATE file_links SET mode=$m, expires=$e, max_downloads=$x WHERE id=$i",
            ("$i", id), ("$m", Mode(mode)), ("$e", expires?.ToUniversalTime().ToString("o")), ("$x", Max(maxDownloads)));
        if (password != null)
            db.Exec("UPDATE file_links SET password=$p WHERE id=$i", ("$i", id), ("$p", password.Length == 0 ? null : Passwords.Hash(password)));
    }

    public static bool Delete(UserDb db, string id) => db.Exec("DELETE FROM file_links WHERE id=$i", ("$i", id)) > 0;

    /// <summary>Einen Abruf zaehlen - in einem Schritt mit der Pruefung, damit gleichzeitige Abrufe die Grenze nicht ueberschreiten.
    /// false: Grenze erreicht.</summary>
    public static bool Take(UserDb db, string id) =>
        db.Exec("UPDATE file_links SET downloads=downloads+1 WHERE id=$i AND (max_downloads IS NULL OR downloads<max_downloads)", ("$i", id)) > 0;

    public static HashSet<string> Ids(UserDb db) => db.Query("SELECT id FROM file_links", r => r.GetString(0)).ToHashSet();
}
