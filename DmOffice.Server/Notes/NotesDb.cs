using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Notizi.Sync;

using DmOffice.Core;

namespace DmOffice.Notes;

/// <summary>Eine Notiz, wie die App sie zeigt.</summary>
public sealed class NoteRow
{
    public long Id { get; init; }
    public string SyncId { get; init; } = "";
    public string? Folder { get; init; }
    public string Title { get; init; } = "";
    public string? CustomTitle { get; init; }
    public string Color { get; init; } = "#FFF9A8";
    public string Html { get; init; } = "";
    public string Plain { get; init; } = "";
    public DateTime Modified { get; init; }
    public DateTime Created { get; init; }
    public DateTime? Archived { get; init; }
    public DateTime? Deleted { get; init; }
    public bool Important { get; init; }
    public List<string> Images { get; init; } = new();
    public List<AttachmentRef> Attachments { get; init; } = new();

    public string DisplayTitle => string.IsNullOrWhiteSpace(CustomTitle)
        ? (string.IsNullOrWhiteSpace(Title) ? "(ohne Titel)" : Title)
        : CustomTitle!;

    /// <summary>
    /// Die Zeilen unter dem Titel. Die erste Zeile ist nur dann der Titel,
    /// wenn die Notiz keinen eigenen hat - sonst gehoert sie zum Inhalt.
    /// </summary>
    private IEnumerable<string> BodyLines =>
        Plain.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)
             .Skip(string.IsNullOrWhiteSpace(CustomTitle) ? 1 : 0);

    public string Snippet
    {
        get
        {
            var text = string.Join(" · ", BodyLines);
            return text.Length > 120 ? text[..120] + "…" : text;
        }
    }

    /// <summary>Mehr Text fuer einen Zettel: die Zeilen nach dem Titel,
    /// wie sie stehen.</summary>
    public string Preview
    {
        get
        {
            var text = string.Join("\n", BodyLines);
            return text.Length > 400 ? text[..400] + "…" : text;
        }
    }

    public string ModifiedText => Modified.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
}

public sealed class NoteFolder
{
    public string SyncId { get; init; } = "";
    public string? Parent { get; init; }
    public string Name { get; init; } = "";
    public double SortOrder { get; init; }
}

/// <summary>
/// Notizen in der Benutzerdatenbank (Tabellen notes, note_*) - uebernommen
/// aus der Android-App von notizi (MobileDb), damit beide Seiten dieselben
/// Regeln einhalten. Urspruenglich:
/// Die Datenbank der App: eine eigene SQLite-Datei, Notiztext als HTML wie
/// im Abgleichordner. Zugleich die Seite des Geraets fuer den Abgleich
/// (<see cref="ISyncLocal"/>) - wie RepositorySyncLocal in notizi.
///
/// Bewusst schlicht: Anhaenge, Quellen und Bildnamen stehen als JSON an der
/// Notiz, die Bytes der Bilder und Anhaenge in einer eigenen Tabelle.
/// </summary>
public sealed class NotesDb : ISyncLocal
{
    private readonly UserDb _db;

    public NotesDb(UserDb db) => _db = db;

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<UserDb, NotesDb> Cache = new();
    public static NotesDb Of(UserSpace space) => Cache.GetValue(space.Db, db => new NotesDb(db));

    private SqliteConnection Open() => _db.Open();

    private void Exec(string sql, params (string Name, object? Value)[] args) => _db.Exec(sql, args);

    // ==================================================================
    //  Fuer die Oberflaeche
    // ==================================================================

    private const string NoteColumns =
        "id, sync_id, folder, title, custom_title, color, html, plain, modified, archived, deleted, images, attachments, important, created";

    private static NoteRow ReadNote(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        SyncId = r.GetString(1),
        Folder = r.IsDBNull(2) ? null : r.GetString(2),
        Title = r.GetString(3),
        CustomTitle = r.IsDBNull(4) ? null : r.GetString(4),
        Color = r.GetString(5),
        Html = r.GetString(6),
        Plain = r.GetString(7),
        Modified = Time(r.GetString(8)),
        Archived = r.IsDBNull(9) ? null : Time(r.GetString(9)),
        Deleted = r.IsDBNull(10) ? null : Time(r.GetString(10)),
        Images = JsonSerializer.Deserialize<List<string>>(r.GetString(11)) ?? new(),
        Attachments = JsonSerializer.Deserialize<List<AttachmentRef>>(r.GetString(12)) ?? new(),
        Important = r.GetInt64(13) != 0,
        Created = Time(r.GetString(14))
    };

    private List<NoteRow> QueryNotes(string where, params (string, object?)[] args)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {NoteColumns} FROM notes n {where};";
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        using var r = cmd.ExecuteReader();
        var result = new List<NoteRow>();
        while (r.Read()) result.Add(ReadNote(r));
        return result;
    }

    /// <summary>Notizen eines Ordners (null: Eingang), ohne Archiv und Papierkorb.</summary>
    public List<NoteRow> GetNotes(string? folder) =>
        QueryNotes("WHERE deleted IS NULL AND archived IS NULL AND folder IS $f ORDER BY sort_order IS NULL, sort_order, modified DESC",
                   ("$f", folder));

    /// <summary>Alle Notizen ausser Archiv und Papierkorb - fuer die Filter.</summary>
    public List<NoteRow> GetAllActive() =>
        QueryNotes("WHERE deleted IS NULL AND archived IS NULL ORDER BY modified DESC");

    public List<NoteRow> GetArchived() =>
        QueryNotes("WHERE deleted IS NULL AND archived IS NOT NULL ORDER BY archived DESC");

    public List<NoteRow> GetTrashed() =>
        QueryNotes("WHERE deleted IS NOT NULL ORDER BY deleted DESC");

    public (int Archived, int Trashed) CountArchiveAndTrash()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT (SELECT COUNT(*) FROM notes WHERE deleted IS NULL AND archived IS NOT NULL),
                                   (SELECT COUNT(*) FROM notes WHERE deleted IS NOT NULL);";
        using var r = cmd.ExecuteReader();
        r.Read();
        return (r.GetInt32(0), r.GetInt32(1));
    }

    public List<NoteRow> Search(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Select(w => "\"" + w.Replace("\"", "") + "\"*");
        var match = string.Join(" ", words);
        if (match.Length == 0) return new();
        return QueryNotes("WHERE n.id IN (SELECT rowid FROM notes_fts WHERE notes_fts MATCH $q) AND deleted IS NULL ORDER BY modified DESC",
                          ("$q", match));
    }

    public NoteRow? GetNote(long id) => QueryNotes("WHERE id = $id", ("$id", id)).FirstOrDefault();

    /// <summary>Ein Ordner; null, wenn es ihn nicht gibt.</summary>
    public NoteFolder? GetFolder(string id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sync_id, parent, name, sort_order FROM note_folders WHERE sync_id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read()
            ? new NoteFolder
            {
                SyncId = r.GetString(0),
                Parent = r.IsDBNull(1) ? null : r.GetString(1),
                Name = r.GetString(2),
                SortOrder = r.GetDouble(3)
            }
            : null;
    }

    public List<NoteFolder> GetFolders(string? parent)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sync_id, parent, name, sort_order FROM note_folders WHERE parent IS $p ORDER BY sort_order, name;";
        cmd.Parameters.AddWithValue("$p", (object?)parent ?? DBNull.Value);
        using var r = cmd.ExecuteReader();
        var result = new List<NoteFolder>();
        while (r.Read())
            result.Add(new NoteFolder
            {
                SyncId = r.GetString(0),
                Parent = r.IsDBNull(1) ? null : r.GetString(1),
                Name = r.GetString(2),
                SortOrder = r.GetDouble(3)
            });
        return result;
    }

    public byte[]? GetBlob(string name)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT data FROM note_blobs WHERE name = $n;";
        cmd.Parameters.AddWithValue("$n", name);
        return cmd.ExecuteScalar() as byte[];
    }

    /// <summary>
    /// Speichert, was im Editor steht. Die Revision steigt, und die Notiz
    /// gilt als "aus einem einfachen Editor" - der PC prueft dann, dass alle
    /// gesperrten Bloecke noch da sind (ProtectedBlocks).
    /// </summary>
    public void SaveNote(long id, string html, string plain)
    {
        // Welche Bilder die Notiz braucht, steht im Text - auch frisch
        // gemalte. Was nicht mehr darin vorkommt, faellt heraus.
        var images = System.Text.RegularExpressions.Regex.Matches(html, "blob:([0-9a-f]{64})")
            .Select(m => m.Groups[1].Value).Distinct().ToList();
        Exec(@"UPDATE notes SET html=$h, plain=$p, title=$t, modified=$m, editor='basic', sync_rev=sync_rev+1, images=$im
               WHERE id=$id AND (html <> $h)",
             ("$h", html), ("$p", plain), ("$t", TitleOf(plain)), ("$m", Now()), ("$id", id),
             ("$im", JsonSerializer.Serialize(images)));
    }

    /// <summary>
    /// Haengt eine Datei an - wie am PC. Gleicher Name schon da: wie dort
    /// mit Zaehler, "Foto (2).jpg".
    /// </summary>
    public void AddAttachment(long id, string fileName, string? mime, byte[] data, string blobName)
    {
        var note = GetNote(id);
        if (note == null) return;
        PutBlob(blobName, data);

        var name = fileName;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (int i = 2; note.Attachments.Any(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); i++)
            name = $"{stem} ({i}){ext}";

        var list = note.Attachments.ToList();
        list.Add(new AttachmentRef
        {
            Blob = blobName, Name = name, Mime = mime, Size = data.LongLength,
            Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant()
        });
        Exec("UPDATE notes SET attachments=$a, modified=$m, sync_rev=sync_rev+1 WHERE id=$id;",
             ("$a", JsonSerializer.Serialize(list)), ("$m", Now()), ("$id", id));
    }

    public void RemoveAttachment(long id, string name)
    {
        var note = GetNote(id);
        if (note == null) return;
        var list = note.Attachments.Where(a => a.Name != name).ToList();
        Exec("UPDATE notes SET attachments=$a, modified=$m, sync_rev=sync_rev+1 WHERE id=$id;",
             ("$a", JsonSerializer.Serialize(list)), ("$m", Now()), ("$id", id));
    }

    /// <summary>Woher die Notiz stammt (Link) - wie beim Browser am PC.</summary>
    public void SetSources(long id, List<SourceRef> sources) =>
        Exec("UPDATE notes SET sources=$s, modified=$m, sync_rev=sync_rev+1 WHERE id=$id;",
             ("$s", JsonSerializer.Serialize(sources)), ("$m", Now()), ("$id", id));

    public void PutBlob(string name, byte[] data) =>
        Exec("INSERT OR IGNORE INTO note_blobs (name, data) VALUES ($n, $d);", ("$n", name), ("$d", data));

    public long CreateNote(string? folder)
    {
        var syncId = Guid.NewGuid().ToString("N");
        Exec(@"INSERT INTO notes (sync_id, sync_rev, folder, created, modified, html, plain, editor)
               VALUES ($s, 1, $f, $m, $m, $h, '', 'basic');",
             ("$s", syncId), ("$f", folder), ("$m", Now()), ("$h", "<div data-notizi=\"1\"><p></p></div>"));
        return QueryNotes("WHERE sync_id = $s", ("$s", syncId)).Single().Id;
    }

    /// <summary>Endgueltig loeschen - mit Grabstein, damit der Abgleich es
    /// den anderen Geraeten sagt.</summary>
    public void DeleteNote(long id)
    {
        var note = GetNote(id);
        if (note == null) return;
        Exec("DELETE FROM notes WHERE id=$id;", ("$id", id));
        Exec("INSERT OR REPLACE INTO note_tombstones (sync_id, kind) VALUES ($s, 'note');", ("$s", note.SyncId));
    }

    /// <summary>Selbst vergebener Titel - wie am PC. Leer: wieder aus der
    /// ersten Zeile.</summary>
    public void SetCustomTitle(long id, string? title) =>
        Exec("UPDATE notes SET custom_title=$t, modified=$m, sync_rev=sync_rev+1 WHERE id=$id AND custom_title IS NOT $t;",
             ("$t", string.IsNullOrWhiteSpace(title) ? null : title.Trim()), ("$m", Now()), ("$id", id));

    public void SetImportant(long id, bool important) =>
        Exec("UPDATE notes SET important=$i, modified=$m, sync_rev=sync_rev+1 WHERE id=$id AND important <> $i;",
             ("$i", important ? 1 : 0), ("$m", Now()), ("$id", id));

    public void SetColor(long id, string color) =>
        Exec("UPDATE notes SET color=$c, modified=$m, sync_rev=sync_rev+1 WHERE id=$id AND color <> $c;",
             ("$c", color), ("$m", Now()), ("$id", id));

    /// <summary>Ins Archiv - wie am PC: aus dem Weg, aber nicht geloescht.</summary>
    public void ArchiveNote(long id) =>
        Exec("UPDATE notes SET archived=$m, modified=$m, sync_rev=sync_rev+1 WHERE id=$id;", ("$m", Now()), ("$id", id));

    /// <summary>Aus Archiv oder Papierkorb zurueck.</summary>
    public void RestoreNote(long id) =>
        Exec("UPDATE notes SET archived=NULL, deleted=NULL, modified=$m, sync_rev=sync_rev+1 WHERE id=$id;", ("$m", Now()), ("$id", id));

    /// <summary>In den Papierkorb - ein gewoehnliches Feld, kein Grabstein.</summary>
    public void TrashNote(long id) =>
        Exec("UPDATE notes SET deleted=$m, modified=$m, sync_rev=sync_rev+1 WHERE id=$id;", ("$m", Now()), ("$id", id));

    public int PendingCount()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT (SELECT COUNT(*) FROM notes n LEFT JOIN note_sync_state s ON s.sync_id = n.sync_id
        WHERE s.sync_id IS NULL OR n.sync_rev <> s.base_rev)
     + (SELECT COUNT(*) FROM note_tombstones);";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public bool IsPending(long id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT n.sync_rev, s.base_rev FROM notes n LEFT JOIN note_sync_state s ON s.sync_id = n.sync_id WHERE n.id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() && (r.IsDBNull(1) || r.GetInt64(0) != r.GetInt64(1));
    }

    /// <summary>Vergisst den Abgleich, behaelt die Notizen - beim Trennen und
    /// vor dem Verbinden mit einem anderen Abgleichordner. Der erste Abgleich
    /// dort fuehrt gleiche Notizen zusammen.</summary>
    public void ForgetSync() => Exec("DELETE FROM note_sync_state; DELETE FROM note_tombstones;");

    /// <summary>Alles weg.</summary>
    public void Clear() =>
        Exec("DELETE FROM notes; DELETE FROM note_folders; DELETE FROM note_blobs; DELETE FROM note_sync_state; DELETE FROM note_tombstones;");

    // ==================================================================
    //  ISyncLocal
    // ==================================================================

    public IReadOnlyList<LocalEntry> Entries()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sync_id, 'folder', sync_rev FROM note_folders UNION ALL SELECT sync_id, 'note', sync_rev FROM notes;";
        using var r = cmd.ExecuteReader();
        var result = new List<LocalEntry>();
        while (r.Read()) result.Add(new LocalEntry(r.GetString(0), r.GetString(1), r.GetInt64(2)));
        return result;
    }

    public IReadOnlyList<(string Id, string Kind)> Tombstones()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sync_id, kind FROM note_tombstones;";
        using var r = cmd.ExecuteReader();
        var result = new List<(string, string)>();
        while (r.Read()) result.Add((r.GetString(0), r.GetString(1)));
        return result;
    }

    public void ForgetTombstone(string id) => Exec("DELETE FROM note_tombstones WHERE sync_id=$s;", ("$s", id));

    public IReadOnlyDictionary<string, SyncState> States()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sync_id, kind, base_rev, remote_tag, base_item FROM note_sync_state;";
        using var r = cmd.ExecuteReader();
        var result = new Dictionary<string, SyncState>();
        while (r.Read())
            result[r.GetString(0)] = new SyncState(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetString(3),
                                                   r.IsDBNull(4) ? null : (byte[])r[4]);
        return result;
    }

    public void SaveState(SyncState state) =>
        Exec(@"INSERT INTO note_sync_state (sync_id, kind, base_rev, remote_tag, base_item) VALUES ($s, $k, $r, $t, $b)
               ON CONFLICT(sync_id) DO UPDATE SET kind=$k, base_rev=$r, remote_tag=$t, base_item=$b;",
             ("$s", state.Id), ("$k", state.Kind), ("$r", state.BaseRev), ("$t", state.RemoteTag), ("$b", state.Base));

    public void RemoveState(string id) => Exec("DELETE FROM note_sync_state WHERE sync_id=$s;", ("$s", id));

    public FolderItem ExportFolder(string id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sync_rev, parent, name, sort_order, created, modified FROM note_folders WHERE sync_id=$s;";
        cmd.Parameters.AddWithValue("$s", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new InvalidOperationException($"Ordner gibt es nicht: {id}");
        return new FolderItem
        {
            Id = id, Rev = r.GetInt64(0), Parent = r.IsDBNull(1) ? null : r.GetString(1),
            Name = r.GetString(2), SortOrder = r.GetDouble(3), Created = Time(r.GetString(4)), Modified = Time(r.GetString(5))
        };
    }

    public NoteItem ExportNote(string id, IBlobStore blobs)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT sync_rev, folder, sort_order, title, custom_title, color, important, alarm, alarm_active,
                                   archived, deleted, created, modified, html, plain, editor, images, attachments, sources
                            FROM notes WHERE sync_id=$s;";
        cmd.Parameters.AddWithValue("$s", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new InvalidOperationException($"Notiz gibt es nicht: {id}");

        // Bilder und Anhaenge: alle in den Sammler - der Abgleich laedt nur
        // hoch, was dort noch fehlt, also das hier Hinzugefuegte. Der Name
        // haengt am Schluessel des Abgleichordners: Stammt einer noch von
        // einem anderen (Umzug, neu eingerichtet), wird er hier berichtigt -
        // sonst verwiese die Notiz dort auf ein Bild, das es unter diesem
        // Namen nicht gibt.
        var images = JsonSerializer.Deserialize<List<string>>(r.GetString(16)) ?? new();
        var attachments = JsonSerializer.Deserialize<List<AttachmentRef>>(r.GetString(17)) ?? new();
        var html = r.GetString(13);
        var renamed = new Dictionary<string, string>();
        foreach (var name in images.Concat(attachments.Select(a => a.Blob)).Distinct())
            if (GetBlob(name) is byte[] data)
            {
                var now = blobs.PutBlob(data);
                if (now != name) renamed[name] = now;
            }
        if (renamed.Count > 0)
        {
            (html, images, attachments) = Renamed(html, images, attachments, renamed);
            KeepRenamed(id, html, images, attachments, renamed);
        }

        return new NoteItem
        {
            Id = id, Rev = r.GetInt64(0),
            Folder = r.IsDBNull(1) ? null : r.GetString(1),
            SortOrder = r.IsDBNull(2) ? null : r.GetDouble(2),
            Title = r.GetString(3), CustomTitle = r.IsDBNull(4) ? null : r.GetString(4),
            Color = r.GetString(5), Important = r.GetInt64(6) != 0,
            Alarm = r.IsDBNull(7) ? null : Time(r.GetString(7)), AlarmActive = r.GetInt64(8) != 0,
            Archived = r.IsDBNull(9) ? null : Time(r.GetString(9)), Deleted = r.IsDBNull(10) ? null : Time(r.GetString(10)),
            Created = Time(r.GetString(11)), Modified = Time(r.GetString(12)),
            Html = html, Plain = r.GetString(14), Editor = r.IsDBNull(15) ? null : r.GetString(15),
            Images = images,
            Attachments = attachments,
            Sources = JsonSerializer.Deserialize<List<SourceRef>>(r.GetString(18)) ?? new()
        };
    }

    private static (string Html, List<string> Images, List<AttachmentRef> Attachments) Renamed(
        string html, List<string> images, List<AttachmentRef> attachments, Dictionary<string, string> renamed)
    {
        foreach (var (from, to) in renamed) html = html.Replace("blob:" + from, "blob:" + to);
        images = images.Select(i => renamed.TryGetValue(i, out var n) ? n : i).ToList();
        foreach (var a in attachments)
            if (renamed.TryGetValue(a.Blob, out var n)) a.Blob = n;
        return (html, images, attachments);
    }

    /// <summary>Die berichtigten Namen auch hier - ohne neue Revision, die
    /// Notiz geht ja gerade so hinaus.</summary>
    private void KeepRenamed(string id, string html, List<string> images, List<AttachmentRef> attachments, Dictionary<string, string> renamed)
    {
        foreach (var (from, to) in renamed)
            if (GetBlob(from) is byte[] data) PutBlob(to, data);
        Exec("UPDATE notes SET html=$h, images=$i, attachments=$a WHERE sync_id=$s;",
             ("$h", html), ("$i", JsonSerializer.Serialize(images)), ("$a", JsonSerializer.Serialize(attachments)), ("$s", id));
    }

    /// <summary>
    /// Nach einem Wechsel des Schluessels (Umzug, neu eingerichtet): Bilder
    /// und Anhaenge aller Notizen auf die Namen des neuen Schluessels bringen.
    /// Betroffene Notizen bekommen eine neue Revision und gehen beim
    /// naechsten Durchgang neu hinaus - auch die, die schon mit alten Namen
    /// oben liegen. Zurueck: wie viele.
    /// </summary>
    public int RekeyBlobs(Func<byte[], string> nameOf)
    {
        var notes = new List<(string Id, string Html, string Images, string Attachments)>();
        using (var conn = Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT sync_id, html, images, attachments FROM notes;";
            using var r = cmd.ExecuteReader();
            while (r.Read()) notes.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));
        }

        int count = 0;
        foreach (var (id, html0, imagesJson, attachmentsJson) in notes)
        {
            var images = JsonSerializer.Deserialize<List<string>>(imagesJson) ?? new();
            var attachments = JsonSerializer.Deserialize<List<AttachmentRef>>(attachmentsJson) ?? new();
            var renamed = new Dictionary<string, string>();
            foreach (var name in images.Concat(attachments.Select(a => a.Blob)).Distinct())
                if (GetBlob(name) is byte[] data && nameOf(data) is var now && now != name)
                    renamed[name] = now;
            if (renamed.Count == 0) continue;

            var (html, imgs, atts) = Renamed(html0, images, attachments, renamed);
            KeepRenamed(id, html, imgs, atts, renamed);
            Exec("UPDATE notes SET sync_rev = sync_rev + 1 WHERE sync_id=$s;", ("$s", id));
            count++;
        }
        return count;
    }

    public void ApplyFolder(FolderItem item) =>
        Exec(@"INSERT INTO note_folders (sync_id, sync_rev, parent, name, sort_order, created, modified)
               VALUES ($s, $r, $p, $n, $o, $c, $m)
               ON CONFLICT(sync_id) DO UPDATE SET sync_rev=$r, parent=$p, name=$n, sort_order=$o, modified=$m;",
             ("$s", item.Id), ("$r", item.Rev), ("$p", item.Parent), ("$n", item.Name), ("$o", item.SortOrder),
             ("$c", Iso(item.Created)), ("$m", Iso(item.Modified)));

    public bool ApplyNote(NoteItem item, IBlobStore blobs)
    {
        foreach (var name in item.Blobs().Distinct())
        {
            var data = blobs.GetBlob(name);
            if (data == null) return false;
            Exec("INSERT OR IGNORE INTO note_blobs (name, data) VALUES ($n, $d);", ("$n", name), ("$d", data));
        }

        Exec(@"INSERT INTO notes (sync_id, sync_rev, folder, sort_order, title, custom_title, color, important, alarm, alarm_active,
                                  archived, deleted, created, modified, html, plain, editor, images, attachments, sources)
               VALUES ($s, $r, $f, $o, $t, $ct, $c, $i, $a, $aa, $ar, $d, $cr, $m, $h, $p, $e, $im, $at, $so)
               ON CONFLICT(sync_id) DO UPDATE SET sync_rev=$r, folder=$f, sort_order=$o, title=$t, custom_title=$ct, color=$c,
                   important=$i, alarm=$a, alarm_active=$aa, archived=$ar, deleted=$d, modified=$m, html=$h, plain=$p,
                   editor=$e, images=$im, attachments=$at, sources=$so;",
             ("$s", item.Id), ("$r", item.Rev), ("$f", item.Folder), ("$o", item.SortOrder),
             ("$t", string.IsNullOrEmpty(item.Title) ? TitleOf(item.Plain) : item.Title), ("$ct", item.CustomTitle),
             ("$c", item.Color), ("$i", item.Important ? 1 : 0), ("$a", item.Alarm is DateTime al ? Iso(al) : null),
             ("$aa", item.AlarmActive ? 1 : 0), ("$ar", item.Archived is DateTime ar ? Iso(ar) : null),
             ("$d", item.Deleted is DateTime d ? Iso(d) : null), ("$cr", Iso(item.Created)), ("$m", Iso(item.Modified)),
             ("$h", item.Html), ("$p", item.Plain), ("$e", item.Editor),
             ("$im", JsonSerializer.Serialize(item.Images)), ("$at", JsonSerializer.Serialize(item.Attachments)),
             ("$so", JsonSerializer.Serialize(item.Sources)));
        return true;
    }

    public bool FolderExists(string id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM note_folders WHERE sync_id=$s;";
        cmd.Parameters.AddWithValue("$s", id);
        return cmd.ExecuteScalar() != null;
    }

    public void DeleteLocal(string id, string kind)
    {
        if (kind == SyncItem.FolderKind)
        {
            // Unterordner mit; die Notizen darin schickt das andere Geraet
            // ohnehin als Papierkorb.
            foreach (var child in GetFolders(id)) DeleteLocal(child.SyncId, kind);
            Exec("DELETE FROM note_folders WHERE sync_id=$s;", ("$s", id));
        }
        else
        {
            Exec("DELETE FROM notes WHERE sync_id=$s;", ("$s", id));
        }
    }

    public long RevOf(string id, string kind)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT sync_rev FROM {(kind == SyncItem.FolderKind ? "note_folders" : "notes")} WHERE sync_id=$s;";
        cmd.Parameters.AddWithValue("$s", id);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? -1L);
    }

    public void Rekey(string oldId, string newId, string kind) =>
        Exec($"UPDATE {(kind == SyncItem.FolderKind ? "note_folders" : "notes")} SET sync_id=$n WHERE sync_id=$o;",
             ("$o", oldId), ("$n", newId));

    // ==================================================================

    /// <summary>Titel aus der ersten Zeile - wie notizi auf dem PC.</summary>
    public static string TitleOf(string plain)
    {
        var line = plain.Replace("\r", "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        return line.Length > 60 ? line[..60].TrimEnd() + "…" : line;
    }

    private static string Now() => Iso(DateTime.UtcNow);
    private static string Iso(DateTime t) => t.ToUniversalTime().ToString("o");
    private static DateTime Time(string s) =>
        DateTime.Parse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
}
