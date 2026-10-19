using System.Security.Cryptography;
using System.Text;
using DmOffice.Core;
using DmOffice.Dav;
using Microsoft.Data.Sqlite;

namespace DmOffice.Pim;

public sealed record Collection(long Id, string Kind, string Uri, string Name, string? Color, string? Description,
    string Components, int SortOrder, long SyncToken);

public sealed record DavObject(long Id, long Collection, string Uri, string? Uid, string Component, string Etag, string Data, string Modified);

/// <summary>Schreiben abgelehnt: If-Match/If-None-Match passt nicht.</summary>
public sealed class PreconditionFailedException : Exception { }

/// <summary>
/// Kalender, Aufgabenlisten und Adressbuecher eines Benutzers. Jedes Objekt
/// ist der Text einer .ics- oder .vcf-Datei, so wie ein Programm ihn
/// geschickt hat - die Weboberflaeche aendert darin nur, was sie kennt.
/// Jede Aenderung zaehlt das Sync-Token der Sammlung hoch (RFC 6578).
/// </summary>
public static class PimStore
{
    public const string Calendar = "calendar", AddressBook = "addressbook";

    public static void EnsureDefaults(UserDb db)
    {
        if (db.Get("pim.defaults") != null) return;
        if (Collections(db, Calendar).Count == 0)
            CreateCollection(db, Calendar, "personal", "Persönlich", "#0F6CBD", "VEVENT,VTODO");
        if (Collections(db, AddressBook).Count == 0)
            CreateCollection(db, AddressBook, "contacts", "Kontakte", null, "");
        db.Set("pim.defaults", "1");
    }

    // ---------------- Sammlungen ----------------

    private const string CollectionColumns = "id,kind,uri,name,color,description,components,sort_order,synctoken";

    private static Collection ReadCollection(SqliteDataReader r) => new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
        r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.GetString(6), r.GetInt32(7), r.GetInt64(8));

    public static List<Collection> Collections(UserDb db, string kind) =>
        db.Query($"SELECT {CollectionColumns} FROM collections WHERE kind=$k ORDER BY sort_order, name", ReadCollection, ("$k", kind));

    public static Collection? Find(UserDb db, string kind, string uri) =>
        db.Query($"SELECT {CollectionColumns} FROM collections WHERE kind=$k AND uri=$u", ReadCollection, ("$k", kind), ("$u", uri)).FirstOrDefault();

    public static Collection? Find(UserDb db, long id) =>
        db.Query($"SELECT {CollectionColumns} FROM collections WHERE id=$i", ReadCollection, ("$i", id)).FirstOrDefault();

    public static Collection CreateCollection(UserDb db, string kind, string uri, string name, string? color, string components)
    {
        if (!ValidSegment(uri)) throw new ArgumentException("Ungültiger Name der Sammlung.");
        db.Exec("INSERT INTO collections(kind,uri,name,color,components,sort_order) VALUES($k,$u,$n,$c,$co,(SELECT COALESCE(MAX(sort_order),0)+1 FROM collections))",
            ("$k", kind), ("$u", uri), ("$n", name), ("$c", color), ("$co", components));
        return Find(db, kind, uri)!;
    }

    public static void UpdateCollection(UserDb db, long id, string? name, string? color, string? description, string? components = null)
    {
        db.Exec(@"UPDATE collections SET name=COALESCE($n,name), color=COALESCE($c,color), description=COALESCE($d,description),
                  components=COALESCE($co,components), synctoken=synctoken+1 WHERE id=$i",
            ("$n", name), ("$c", color), ("$d", description), ("$co", components), ("$i", id));
    }

    public static void DeleteCollection(UserDb db, long id)
    {
        using var conn = db.Open();
        UserDb.Exec(conn, "DELETE FROM contacts_fts WHERE rowid IN (SELECT id FROM objects WHERE collection=$c)", ("$c", id));
        UserDb.Exec(conn, "DELETE FROM collections WHERE id=$c", ("$c", id));
    }

    /// <summary>Ein Pfadteil, der sich gefahrlos in einer Adresse und als Dateiname zeigen laesst.</summary>
    public static bool ValidSegment(string s) =>
        s.Length is > 0 and <= 200 && s != "." && s != ".." && !s.Contains('/') && !s.Contains('\\') && !s.Any(char.IsControl);

    public static string NewUri(string suffix) => Guid.NewGuid().ToString("D").ToUpperInvariant() + suffix;

    // ---------------- Objekte ----------------

    private const string ObjectColumns = "id,collection,uri,uid,component,etag,data,modified";

    private static DavObject ReadObject(SqliteDataReader r) => new(r.GetInt64(0), r.GetInt64(1), r.GetString(2),
        r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7));

    public static List<DavObject> Objects(UserDb db, long collection) =>
        db.Query($"SELECT {ObjectColumns} FROM objects WHERE collection=$c ORDER BY uri", ReadObject, ("$c", collection));

    /// <summary>Nur Adresse und ETag - fuer PROPFIND ohne Inhalt.</summary>
    public static List<(string Uri, string Etag, long Size, string Modified)> Index(UserDb db, long collection) =>
        db.Query("SELECT uri, etag, length(data), modified FROM objects WHERE collection=$c ORDER BY uri",
            r => (r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetString(3)), ("$c", collection));

    public static DavObject? Object(UserDb db, long collection, string uri) =>
        db.Query($"SELECT {ObjectColumns} FROM objects WHERE collection=$c AND uri=$u", ReadObject, ("$c", collection), ("$u", uri)).FirstOrDefault();

    public static DavObject? ByUid(UserDb db, long collection, string uid) =>
        db.Query($"SELECT {ObjectColumns} FROM objects WHERE collection=$c AND uid=$u", ReadObject, ("$c", collection), ("$u", uid)).FirstOrDefault();

    /// <summary>Objekte, deren Zeitraum [start, end) beruehrt - Serien ohne Ende immer.</summary>
    public static List<DavObject> InRange(UserDb db, IEnumerable<long> collections, string component, DateTime? start, DateTime? end)
    {
        var ids = string.Join(",", collections.Select(c => c.ToString()));
        if (ids.Length == 0) return new();
        var where = $"collection IN ({ids}) AND component=$comp";
        if (start != null) where += " AND (last_end IS NULL OR last_end > $s)";
        if (end != null) where += " AND (first_start IS NULL OR first_start < $e)";
        return db.Query($"SELECT {ObjectColumns} FROM objects WHERE {where}", ReadObject, ("$comp", component),
            ("$s", start == null ? null : new DateTimeOffset(start.Value, TimeSpan.Zero).ToUnixTimeMilliseconds()),
            ("$e", end == null ? null : new DateTimeOffset(end.Value, TimeSpan.Zero).ToUnixTimeMilliseconds()));
    }

    /// <summary>
    /// Legt an oder ersetzt. ifMatch: nur, wenn das Objekt noch diesen ETag
    /// hat; ifNoneMatch "*": nur, wenn es noch nicht existiert. Gibt den neuen ETag.
    /// </summary>
    public static string Put(UserDb db, Collection coll, string uri, string data, string? ifMatch = null, string? ifNoneMatch = null, TimeZoneInfo? zone = null)
    {
        if (!ValidSegment(uri)) throw new ArgumentException("Ungültiger Name.");
        var info = Inspect(coll, data, zone ?? TimeZoneInfo.Utc);
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var etag = PutIn(conn, coll, uri, data, info, ifMatch, ifNoneMatch);
        tx.Commit();
        return etag;
    }

    /// <summary>
    /// Verschiebt ein Objekt (auch in eine andere Sammlung) in einer Transaktion:
    /// scheitert das Schreiben ins Ziel, bleibt die Quelle, wie sie war.
    /// </summary>
    public static string Move(UserDb db, Collection from, string fromUri, Collection to, string toUri, bool overwrite = true, TimeZoneInfo? zone = null)
    {
        if (!ValidSegment(toUri)) throw new ArgumentException("Ungültiger Name.");
        if (from.Id == to.Id && fromUri == toUri) throw new ArgumentException("Quelle und Ziel sind gleich.");
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var data = UserDb.Query(conn, "SELECT data FROM objects WHERE collection=$c AND uri=$u", r => r.GetString(0),
            ("$c", from.Id), ("$u", fromUri)).FirstOrDefault() ?? throw new KeyNotFoundException();
        var info = Inspect(to, data, zone ?? TimeZoneInfo.Utc);
        // Erst die Quelle weg - sonst stoesst sich dieselbe UID in derselben Sammlung an ihr
        DeleteIn(conn, from, fromUri, null);
        var etag = PutIn(conn, to, toUri, data, info, null, overwrite ? null : "*");
        tx.Commit();
        return etag;
    }

    private static string PutIn(SqliteConnection conn, Collection coll, string uri, string data, Info info, string? ifMatch, string? ifNoneMatch)
    {
        var etag = "\"" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data)))[..32].ToLowerInvariant() + "\"";
        var existing = UserDb.Query(conn, "SELECT id, etag FROM objects WHERE collection=$c AND uri=$u",
            r => (r.GetInt64(0), r.GetString(1)), ("$c", coll.Id), ("$u", uri)).FirstOrDefault();

        if (ifNoneMatch == "*" && existing.Item1 != 0) throw new PreconditionFailedException();
        if (ifMatch != null && ifMatch != "*" && existing.Item2 != ifMatch) throw new PreconditionFailedException();
        if (ifMatch == "*" && existing.Item1 == 0) throw new PreconditionFailedException();

        // Dieselbe UID unter anderem Namen gibt es nicht doppelt (RFC 4791 5.3.2.1)
        if (info.Uid != null)
        {
            var clash = UserDb.Query(conn, "SELECT uri FROM objects WHERE collection=$c AND uid=$uid AND uri<>$u", r => r.GetString(0),
                ("$c", coll.Id), ("$uid", info.Uid), ("$u", uri)).FirstOrDefault();
            if (clash != null) throw new ArgumentException($"UID schon vergeben: {clash}");
        }

        long id = UserDb.Query(conn, @"INSERT INTO objects(collection,uri,uid,component,etag,data,first_start,last_end,modified)
                VALUES($c,$u,$uid,$comp,$e,$d,$fs,$le,$m)
                ON CONFLICT(collection,uri) DO UPDATE SET uid=$uid, component=$comp, etag=$e, data=$d, first_start=$fs, last_end=$le, modified=$m
                RETURNING id", r => r.GetInt64(0),
            ("$c", coll.Id), ("$u", uri), ("$uid", info.Uid), ("$comp", info.Component), ("$e", etag), ("$d", data),
            ("$fs", info.FirstStart), ("$le", info.LastEnd), ("$m", DateTime.UtcNow.ToString("R"))).First();
        Bump(conn, coll.Id, uri, false);

        if (coll.Kind == AddressBook)
        {
            UserDb.Exec(conn, "DELETE FROM contacts_fts WHERE rowid=$i", ("$i", id));
            UserDb.Exec(conn, "INSERT INTO contacts_fts(rowid,name,emails,org,phones) VALUES($i,$n,$e,$o,$p)",
                ("$i", id), ("$n", info.Name ?? ""), ("$e", info.Emails ?? ""), ("$o", info.Org ?? ""), ("$p", info.Phones ?? ""));
        }
        return etag;
    }

    public static bool Delete(UserDb db, Collection coll, string uri, string? ifMatch = null)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        if (!DeleteIn(conn, coll, uri, ifMatch)) return false;
        tx.Commit();
        return true;
    }

    private static bool DeleteIn(SqliteConnection conn, Collection coll, string uri, string? ifMatch)
    {
        var existing = UserDb.Query(conn, "SELECT id, etag FROM objects WHERE collection=$c AND uri=$u",
            r => (r.GetInt64(0), r.GetString(1)), ("$c", coll.Id), ("$u", uri)).FirstOrDefault();
        if (existing.Item1 == 0) return false;
        if (ifMatch != null && ifMatch != "*" && ifMatch != existing.Item2) throw new PreconditionFailedException();
        UserDb.Exec(conn, "DELETE FROM contacts_fts WHERE rowid=$i", ("$i", existing.Item1));
        UserDb.Exec(conn, "DELETE FROM objects WHERE id=$i", ("$i", existing.Item1));
        Bump(conn, coll.Id, uri, true);
        return true;
    }

    private static void Bump(SqliteConnection conn, long coll, string uri, bool deleted)
    {
        var token = UserDb.Query(conn, "UPDATE collections SET synctoken=synctoken+1 WHERE id=$c RETURNING synctoken", r => r.GetInt64(0), ("$c", coll)).First();
        UserDb.Exec(conn, "DELETE FROM changes WHERE collection=$c AND uri=$u", ("$c", coll), ("$u", uri));
        UserDb.Exec(conn, "INSERT INTO changes(collection,synctoken,uri,deleted) VALUES($c,$t,$u,$d)",
            ("$c", coll), ("$t", token), ("$u", uri), ("$d", deleted ? 1 : 0));
    }

    /// <summary>Was sich seit einem Sync-Token geaendert hat. Null: Token zu alt/unbekannt.</summary>
    public static List<(string Uri, bool Deleted)>? ChangesSince(UserDb db, long coll, long token)
    {
        var oldest = db.Scalar("SELECT MIN(synctoken) FROM changes WHERE collection=$c", ("$c", coll));
        if (token <= 0) return null;
        if (oldest is long o && token < o - 1) return null;
        return db.Query("SELECT uri, deleted FROM changes WHERE collection=$c AND synctoken > $t ORDER BY synctoken",
            r => (r.GetString(0), r.GetInt64(1) != 0), ("$c", coll), ("$t", token));
    }

    // ---------------- Inhalt pruefen ----------------

    private sealed record Info(string? Uid, string Component, long? FirstStart, long? LastEnd,
        string? Name = null, string? Emails = null, string? Org = null, string? Phones = null);

    private static Info Inspect(Collection coll, string data, TimeZoneInfo zone)
    {
        var roots = Ics.Parse(data);
        if (coll.Kind == AddressBook)
        {
            var card = roots.FirstOrDefault(r => r.Name == "VCARD") ?? throw new ArgumentException("Keine vCard.");
            return new Info(card.Text("UID"), "VCARD", null, null,
                Name: card.Text("FN") ?? "",
                Emails: string.Join(" ", card.All("EMAIL").Select(p => p.Text)),
                Org: card.Text("ORG")?.Replace(';', ' '),
                Phones: string.Join(" ", card.All("TEL").Select(p => p.Text)));
        }

        var cal = roots.FirstOrDefault(r => r.Name == "VCALENDAR") ?? throw new ArgumentException("Kein VCALENDAR.");
        var items = cal.Children.Where(c => c.Name is "VEVENT" or "VTODO" or "VJOURNAL").ToList();
        if (items.Count == 0) throw new ArgumentException("Kein Termin und keine Aufgabe.");
        var component = items[0].Name;
        if (coll.Components.Length > 0 && !coll.Components.Split(',').Contains(component))
            throw new ArgumentException($"Gehört nicht in diese Sammlung: {component}");
        var uid = items[0].Text("UID");

        long? first = null, last = null;
        bool unbounded = false;
        foreach (var item in items)
        {
            var (s, e, open) = Range(item, zone);
            if (open) unbounded = true;
            if (s is DateTime sd) first = first == null ? Ms(sd) : Math.Min(first.Value, Ms(sd));
            if (e is DateTime ed) last = last == null ? Ms(ed) : Math.Max(last.Value, Ms(ed));
        }
        if (component != "VEVENT") { first = null; unbounded = true; }   // Aufgaben: immer
        return new Info(uid, component, first, unbounded ? null : last);
    }

    private static long Ms(DateTime t) => new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    private static (DateTime? Start, DateTime? End, bool Unbounded) Range(Component e, TimeZoneInfo zone)
    {
        var start = Ics.Time(e.Get("DTSTART"), zone, out var allDay);
        if (start == null) return (null, null, true);
        var end = Ics.Time(e.Get("DTEND"), zone, out _);
        if (end == null && e.Text("DURATION") is { } dur && TryDuration(dur, out var d)) end = start + d;
        end ??= allDay ? start.Value.AddDays(1) : start;
        // Ganztägig in UTC gespeichert - einen Tag Spielraum fuer Zeitzonen
        if (allDay) { start = start.Value.AddDays(-1); end = end.Value.AddDays(1); }

        if (e.Get("RRULE") is { } rr)
        {
            var parts = rr.Value.Split(';').Select(p => p.Split('=', 2)).Where(p => p.Length == 2)
                .ToDictionary(p => p[0].ToUpperInvariant(), p => p[1]);
            if (parts.TryGetValue("UNTIL", out var until) && Ics.Time(new Prop("X", until), zone, out _) is DateTime u)
                return (start, u + (end - start) + TimeSpan.FromDays(1), false);
            return (start, null, true);   // COUNT oder ohne Ende: grosszuegig
        }
        if (e.Get("RDATE") != null) return (start, null, true);
        return (start, end, false);
    }

    /// <summary>ISO-8601-Dauer wie in iCalendar: P1D, PT1H30M, -PT15M.</summary>
    public static bool TryDuration(string s, out TimeSpan result)
    {
        try
        {
            var neg = s.StartsWith('-');
            result = System.Xml.XmlConvert.ToTimeSpan(s.TrimStart('-', '+'));
            if (neg) result = -result;
            return true;
        }
        catch { result = default; return false; }
    }
}
