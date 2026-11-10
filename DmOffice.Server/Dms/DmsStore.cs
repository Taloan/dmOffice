using DmOffice.Core;

namespace DmOffice.Dms;

/// <summary>
/// Wo die Dokumente eines Bereichs liegen (docs/dms-konzept.md, „Tresor“): offener Bereich = Benutzerdatenbank und Dateiablage,
/// Tresor = Tresor-Datenbank und Tresor-Dateien. Die Listen (WER, WAS, THEMA), Ablageprofile, Quellen und Einstellungen liegen immer
/// in der Benutzerdatenbank (<see cref="User"/>); Dokumente, Text und Gelerntes im jeweiligen Bereich (<see cref="Sql"/>).
/// Aus einer Benutzerdatenbank wird ohne Weiteres der offene Bereich.
/// </summary>
public sealed class DmsStore : ISqlStore
{
    public UserDb User { get; }
    public ISqlStore Sql { get; }
    public BlobStore Blobs { get; }
    public bool Tresor { get; }
    /// <summary>Pfad einer Datei im Tresor (Ordner/Name) - nur im Tresor.</summary>
    private readonly Func<long, string?>? _pathOf;

    private DmsStore(UserDb user, ISqlStore sql, BlobStore blobs, bool tresor, Func<long, string?>? pathOf)
    {
        (User, Sql, Blobs, Tresor, _pathOf) = (user, sql, blobs, tresor, pathOf);
    }

    public static implicit operator DmsStore(UserDb db) => new(db, db, db.Blobs, false, null);

    /// <summary>Der Tresor einer Person (nur, solange er offen ist).</summary>
    public static DmsStore ForTresor(UserDb user, TresorDb tresor) => new(user, tresor, tresor.Blobs, true, id => tresor.Files.PathOf(id));

    public TresorDb TresorDb => Sql as TresorDb ?? throw new InvalidOperationException("Kein Tresor.");

    public string? PathOf(long fileId) => _pathOf?.Invoke(fileId);

    // ---------------- SQL je Bereich ----------------

    /// <summary>Die Datei zum Dokument (Alias f).</summary>
    internal string FileJoin => Tresor ? "LEFT JOIN files f ON f.id = d.fileid AND f.is_dir = 0" : "LEFT JOIN files f ON f.fileid = d.fileid";
    /// <summary>Pfad der Datei (im Tresor erst der Name; den Pfad setzt <see cref="Dms.ByIds"/>).</summary>
    internal string PathCol => Tresor ? "f.name" : "f.path";
    /// <summary>Sichtbar: Datei liegt in der Ablage (nicht im Papierkorb) oder ist noch in Arbeit.</summary>
    internal string Visible(string alias = "d") => Tresor
        ? $"({alias}.fileid IS NULL OR {alias}.fileid IN (SELECT f0.id FROM files f0))"
        : $"({alias}.fileid IS NULL OR {alias}.fileid IN (SELECT f0.fileid FROM files f0 WHERE f0.fileid IS NOT NULL))";

    // ---------------- ISqlStore ----------------

    public Microsoft.Data.Sqlite.SqliteConnection Open() => Sql.Open();
    public int Exec(string sql, params (string Name, object? Value)[] args) => Sql.Exec(sql, args);
    public object? Scalar(string sql, params (string Name, object? Value)[] args) => Sql.Scalar(sql, args);
    public List<T> Query<T>(string sql, Func<Microsoft.Data.Sqlite.SqliteDataReader, T> read, params (string Name, object? Value)[] args) => Sql.Query(sql, read, args);
}
