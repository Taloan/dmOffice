using System.Globalization;
using System.Text.Json;

namespace DmOffice.Dms;

/// <summary>
/// Versionen der Angaben eines Dokuments (Kennung dmshistory): Titel, Datum, Korrespondent, Dokumenttyp, Tags und Betrag vor und
/// nach jeder Änderung von Hand. Eine frühere Version lässt sich wiederherstellen; eine Massenänderung (gleiche Kennung) lässt sich
/// als Ganzes zurücknehmen. Automatisches Einordnen (Inbox) und Rollen (eigene Versionen) zählen nicht.
/// </summary>
public static class DmsHistory
{
    public const string Schema = @"CREATE TABLE IF NOT EXISTS dms_history (id INTEGER PRIMARY KEY AUTOINCREMENT, doc INTEGER NOT NULL, at INTEGER NOT NULL,
    change TEXT NULL, before TEXT NOT NULL, after TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS dms_history_doc ON dms_history(doc);
CREATE INDEX IF NOT EXISTS dms_history_change ON dms_history(change);";

    /// <summary>Höchstens so viele Versionen je Dokument; ältere fallen weg.</summary>
    public const int Keep = 100;

    public sealed record Meta(string Title, string? DocDate, string? Correspondent, string? Doctype, List<string> Tags, long? Amount);
    public sealed record Version(long Id, DateTime At, string? Change, Meta Before, Meta After);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static Meta Of(DmsDoc d) => new(d.Title, d.DocDate, d.Correspondent, d.Doctype, d.Tags.Select(t => t.Id).OrderBy(x => x, StringComparer.Ordinal).ToList(), d.Amount);

    private static string Ser(Meta m) => JsonSerializer.Serialize(m, Json);
    private static Meta De(string s) => JsonSerializer.Deserialize<Meta>(s, Json)!;

    private static void Ensure(DmsStore db)
    {
        db.Exec(Schema);
    }

    /// <summary>Stand vor einer Änderung.</summary>
    public static Dictionary<long, Meta> Snapshot(DmsStore db, IReadOnlyCollection<long> ids) =>
        Dms.ByIds(db, ids).Where(d => d.State != "queued").ToDictionary(d => d.Id, Of);

    /// <summary>Nach der Änderung: für jedes Dokument, dessen Angaben sich geändert haben, eine Version.</summary>
    public static void Record(DmsStore db, Dictionary<long, Meta> before, string? change)
    {
        if (before.Count == 0) return;
        Ensure(db);
        var now = Dms.Unix();
        foreach (var d in Dms.ByIds(db, before.Keys.ToList()))
        {
            var was = Ser(before[d.Id]);
            var isNow = Ser(Of(d));
            if (was == isNow) continue;
            db.Exec("INSERT INTO dms_history(doc, at, change, before, after) VALUES($d, $a, $c, $b, $n)",
                ("$d", d.Id), ("$a", now), ("$c", change), ("$b", was), ("$n", isNow));
            db.Exec("DELETE FROM dms_history WHERE doc = $d AND id NOT IN (SELECT id FROM dms_history WHERE doc = $d ORDER BY id DESC LIMIT $k)",
                ("$d", d.Id), ("$k", Keep));
        }
    }

    public static List<Version> List(DmsStore db, long doc)
    {
        Ensure(db);
        return db.Query("SELECT id, at, change, before, after FROM dms_history WHERE doc = $d ORDER BY id DESC",
            r => new Version(r.GetInt64(0), DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(1)).UtcDateTime, r.IsDBNull(2) ? null : r.GetString(2),
                De(r.GetString(3)), De(r.GetString(4))), ("$d", doc));
    }

    public static Version? Get(DmsStore db, long doc, long id) => List(db, doc).FirstOrDefault(v => v.Id == id);

    /// <summary>Alle Versionen einer Änderung (Massenänderung): Dokument → Stand davor und danach.</summary>
    public static List<(long Doc, Meta Before, Meta After)> OfChange(DmsStore db, string change)
    {
        Ensure(db);
        return db.Query("SELECT doc, before, after FROM dms_history WHERE change = $c ORDER BY id",
            r => (r.GetInt64(0), De(r.GetString(1)), De(r.GetString(2))), ("$c", change));
    }

    /// <summary>Einen Stand als Änderung (alle Angaben auf diesen Stand).</summary>
    public static Dms.Change ToChange(Meta m) => new(
        Title: m.Title, DocDate: m.DocDate ?? "", Correspondent: m.Correspondent ?? "", Doctype: m.Doctype ?? "", Tags: m.Tags,
        Amount: m.Amount?.ToString(CultureInfo.InvariantCulture) ?? "");

    public static bool Same(Meta a, Meta b) => Ser(a) == Ser(b);
}
