using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Mail;
using Microsoft.Data.Sqlite;
using MimeKit;

namespace DmOffice.Search;

/// <summary>Volltextsuche je Benutzer: Inhalte von Anhaengen und Dateien durchsuchbar machen, auf Wunsch mit OCR.</summary>
public sealed class TextIndexSettings
{
    public bool Enabled { get; set; }
    public bool Ocr { get; set; }
}

/// <summary>Treffer im Inhalt einer Datei der Ablage. Im Ausschnitt markieren <see cref="TextIndex.MarkStart"/> und
/// <see cref="TextIndex.MarkEnd"/> die gefundenen Woerter (Oberflaeche und App heben sie hervor).</summary>
public sealed record FileHit(string Path, string Name, long Size, string? Mime, DateTime Modified, string Method, string Snippet);

/// <summary>Treffer im Anhang einer Mail (Part = Nummer wie in der Lese-Ansicht) oder einer Notiz (Blob = Name in note_blobs,
/// Part -1): Name und Ausschnitt.</summary>
public sealed record AttachmentMatch(string Name, int Part, string Method, string Snippet, string? Blob = null);

/// <summary>
/// Der Suchindex in der verschluesselten Benutzerdatenbank: text_docs (Quelle, Name, Art der Erkennung, Zustand,
/// spaeter Schlagwoerter) und text_fts (FTS5 mit dem Text). Mail-Anhaenge haengen an Konto + Message-ID - so
/// uebersteht der Text das Verschieben (lokal Loeschen und im Ziel neu holen) und den Speichermodus "Schlank",
/// der nur den Inhalt verwirft. Dateien haengen am Pfad; Trigger auf files ziehen Loeschen, Verschieben und
/// Umbenennen in derselben Transaktion nach und reihen Neues ein. Die Arbeit macht <see cref="TextIndexer"/>.
/// </summary>
public static class TextIndex
{
    /// <summary>Markiert im Ausschnitt Anfang und Ende eines Treffers (Zeichen aus dem privaten Bereich).</summary>
    public const char MarkStart = '', MarkEnd = '';

    /// <summary>Schalter in der Datenbank, den die Trigger lesen: nur dann wird eingereiht.</summary>
    internal const string OnKey = "textindex.on";
    private const string LastKey = "textindex.last";

    /// <summary>Teil-Nummer eines Merkers "Mail gelesen, aber ohne lesbaren Anhang".</summary>
    private const int NoPart = -1;
    /// <summary>Angehaengte Mails (message/rfc822) zaehlen ab hier, damit sie sich nicht mit den MIME-Teilen ueberschneiden.</summary>
    private const int MessagePartBase = 1000;

    private const string Now = "CAST(strftime('%s','now') AS INTEGER)";
    // Dateiname aus dem Pfad: alles nach dem letzten Schraegstrich
    private const string NewName = "substr(new.path, length(rtrim(new.path, replace(new.path, '/', ''))) + 1)";

    internal const string Schema = @"
CREATE TABLE text_docs (
    id       INTEGER PRIMARY KEY AUTOINCREMENT,
    kind     TEXT    NOT NULL,              -- mail | file | note
    account  TEXT    NULL,                  -- mail: Konto
    msgid    TEXT    NULL,                  -- mail: Message-ID (bleibt beim Verschieben)
    part     INTEGER NULL,                  -- mail: Nummer des Anhangs, -1 = Merker ohne Anhang
    path     TEXT    NULL,                  -- file: Pfad in der Ablage
    note_id  TEXT    NULL,                  -- note: sync_id der Notiz (bleibt im Abgleich gleich)
    blob     TEXT    NULL,                  -- note: Name des Bildes/Anhangs in note_blobs
    name     TEXT    NOT NULL DEFAULT '',
    etag     TEXT    NULL,                  -- file: gelesener Stand; note: Blob-Name (Inhalt bestimmt den Namen)
    method   TEXT    NOT NULL,              -- text | ocr | none
    status   TEXT    NOT NULL,              -- ok | empty | skipped | error
    note     TEXT    NULL,
    want_ocr INTEGER NOT NULL DEFAULT 0,    -- mit OCR kaeme mehr heraus
    tags     TEXT    NOT NULL DEFAULT '',   -- Stufe 3: Schlagwoerter (Rechnung, Vertrag, Betrag, Datum)
    indexed  INTEGER NOT NULL,
    gone     INTEGER NULL                   -- mail: seit wann keine Mail mehr dazu da ist
);
CREATE INDEX text_docs_mail ON text_docs(account, msgid);
CREATE INDEX text_docs_path ON text_docs(path);
CREATE INDEX text_docs_etag ON text_docs(etag);
CREATE INDEX text_docs_note ON text_docs(note_id);
CREATE INDEX IF NOT EXISTS mail_msgid ON mail(account, message_id);
CREATE VIRTUAL TABLE text_fts USING fts5(name, body, tags, tokenize='unicode61 remove_diacritics 2');
CREATE TABLE text_queue (
    kind  TEXT    NOT NULL,                 -- mail (ref = mail.id) | file (ref = Pfad) | note (ref = sync_id)
    ref   TEXT    NOT NULL,
    added INTEGER NOT NULL,
    tries INTEGER NOT NULL DEFAULT 0,
    next  INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (kind, ref)
) WITHOUT ROWID;

CREATE TRIGGER text_docs_ad AFTER DELETE ON text_docs BEGIN
    DELETE FROM text_fts WHERE rowid = old.id;
END;

CREATE TRIGGER text_files_ai AFTER INSERT ON files
WHEN new.is_dir = 0 AND EXISTS (SELECT 1 FROM kv WHERE key = '" + OnKey + @"') BEGIN
    INSERT OR IGNORE INTO text_queue(kind, ref, added) VALUES ('file', new.path, " + Now + @");
END;
CREATE TRIGGER text_files_au AFTER UPDATE OF etag ON files
WHEN new.is_dir = 0 AND new.etag IS NOT old.etag AND EXISTS (SELECT 1 FROM kv WHERE key = '" + OnKey + @"') BEGIN
    INSERT OR IGNORE INTO text_queue(kind, ref, added) VALUES ('file', new.path, " + Now + @");
END;
CREATE TRIGGER text_files_ad AFTER DELETE ON files WHEN old.is_dir = 0 BEGIN
    DELETE FROM text_docs WHERE kind = 'file' AND path = old.path;
    DELETE FROM text_queue WHERE kind = 'file' AND ref = old.path;
END;
CREATE TRIGGER text_files_mv AFTER UPDATE OF path ON files WHEN old.is_dir = 0 AND new.path IS NOT old.path BEGIN
    UPDATE text_fts SET name = " + NewName + @" WHERE rowid IN (SELECT id FROM text_docs WHERE kind = 'file' AND path = old.path);
    UPDATE text_docs SET path = new.path, name = " + NewName + @" WHERE kind = 'file' AND path = old.path;
    UPDATE OR REPLACE text_queue SET ref = new.path WHERE kind = 'file' AND ref = old.path;
END;

CREATE TRIGGER text_notes_ai AFTER INSERT ON notes
WHEN (new.images <> '[]' OR new.attachments <> '[]') AND EXISTS (SELECT 1 FROM kv WHERE key = '" + OnKey + @"') BEGIN
    INSERT OR IGNORE INTO text_queue(kind, ref, added) VALUES ('note', new.sync_id, " + Now + @");
END;
CREATE TRIGGER text_notes_au AFTER UPDATE OF images, attachments ON notes
WHEN (new.images IS NOT old.images OR new.attachments IS NOT old.attachments) AND EXISTS (SELECT 1 FROM kv WHERE key = '" + OnKey + @"') BEGIN
    INSERT OR IGNORE INTO text_queue(kind, ref, added) VALUES ('note', new.sync_id, " + Now + @");
END;
CREATE TRIGGER text_notes_ad AFTER DELETE ON notes BEGIN
    DELETE FROM text_docs WHERE kind = 'note' AND note_id = old.sync_id;
    DELETE FROM text_queue WHERE kind = 'note' AND ref = old.sync_id;
END;

CREATE TRIGGER text_mail_cached AFTER UPDATE OF cached, has_att ON mail
WHEN new.cached = 1 AND new.has_att = 1 AND new.message_id IS NOT NULL AND EXISTS (SELECT 1 FROM kv WHERE key = '" + OnKey + @"') BEGIN
    INSERT OR IGNORE INTO text_queue(kind, ref, added) VALUES ('mail', CAST(new.id AS TEXT), " + Now + @");
END;
CREATE TRIGGER text_mail_ai AFTER INSERT ON mail WHEN new.message_id IS NOT NULL BEGIN
    UPDATE text_docs SET gone = NULL WHERE kind = 'mail' AND account = new.account AND msgid = new.message_id AND gone IS NOT NULL;
END;
CREATE TRIGGER text_mail_ad AFTER DELETE ON mail BEGIN
    DELETE FROM text_queue WHERE kind = 'mail' AND ref = CAST(old.id AS TEXT);
    UPDATE text_docs SET gone = " + Now + @" WHERE kind = 'mail' AND account = old.account AND msgid = old.message_id AND gone IS NULL
        AND NOT EXISTS (SELECT 1 FROM mail WHERE account = old.account AND message_id = old.message_id);
END;
";

    /// <summary>So lange bleibt der Text einer Mail, die es nirgends mehr gibt - beim Verschieben kommt sie im Ziel wieder.</summary>
    public static readonly TimeSpan GoneGrace = TimeSpan.FromDays(3);

    private static long Unix() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    // ---------------- Ein- und Ausschalten ----------------

    /// <summary>Einstellungen uebernehmen: Schalter fuer die Trigger setzen, beim Einschalten den Bestand einreihen,
    /// beim Ausschalten den Index loeschen (nichts mehr gespeichert, was niemand sucht).</summary>
    public static void Apply(UserSpace space, TextIndexSettings before)
    {
        var now = space.Settings.TextIndex;
        if (!now.Enabled)
        {
            space.Db.Set(OnKey, null);
            if (before.Enabled) Clear(space.Db);
            return;
        }
        space.Db.Set(OnKey, "1");
        if (!before.Enabled) EnqueueBacklog(space);
        else if (now.Ocr && !before.Ocr) EnqueueWantsOcr(space.Db);
    }

    /// <summary>Alles verwerfen: Index und Warteschlange.</summary>
    public static void Clear(UserDb db)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        UserDb.Exec(conn, "DELETE FROM text_queue; DELETE FROM text_fts; DELETE FROM text_docs;");
        tx.Commit();
        db.Set(LastKey, null);
    }

    /// <summary>Index neu aufbauen: verwerfen und den ganzen Bestand neu einreihen.</summary>
    public static void Rebuild(UserSpace space)
    {
        Clear(space.Db);
        if (space.Settings.TextIndex.Enabled) EnqueueBacklog(space);
    }

    /// <summary>Den Bestand einreihen: Dateien, deren Stand noch nicht gelesen ist, und gespeicherte Mails mit Anhang.</summary>
    public static int EnqueueBacklog(UserSpace space)
    {
        using var conn = space.Db.Open();
        using var tx = conn.BeginTransaction();
        var root = FileStore.Normalize(space.Settings.Notizi.Root ?? "");
        var n = UserDb.Exec(conn, $@"INSERT OR IGNORE INTO text_queue(kind, ref, added)
            SELECT 'file', f.path, 0 FROM files f WHERE f.is_dir = 0 AND NOT ($root <> '' AND (f.path = $root OR substr(f.path, 1, length($root) + 1) = $root || '/'))
              AND NOT EXISTS (SELECT 1 FROM text_docs d WHERE d.kind = 'file' AND d.path = f.path AND d.etag = f.etag)", ("$root", root));
        // Mails: Neueste zuerst (added = Datum der Mail, die Warteschlange arbeitet absteigend)
        n += UserDb.Exec(conn, @"INSERT OR IGNORE INTO text_queue(kind, ref, added)
            SELECT 'mail', CAST(m.id AS TEXT), m.date / 1000 - 4000000000 FROM mail m
            WHERE m.cached = 1 AND m.has_att = 1 AND m.message_id IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM text_docs d WHERE d.kind = 'mail' AND d.account = m.account AND d.msgid = m.message_id)");
        // Notizen mit Bildern oder Anhaengen (entschluesselt in der Datenbank, nie der Abgleichordner selbst)
        n += UserDb.Exec(conn, @"INSERT OR IGNORE INTO text_queue(kind, ref, added)
            SELECT 'note', n.sync_id, 1 FROM notes n WHERE (n.images <> '[]' OR n.attachments <> '[]')
              AND NOT EXISTS (SELECT 1 FROM text_docs d WHERE d.kind = 'note' AND d.note_id = n.sync_id)");
        tx.Commit();
        return n;
    }

    /// <summary>OCR eingeschaltet: was dafuer vorgemerkt ist, erneut lesen.</summary>
    public static int EnqueueWantsOcr(UserDb db)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var n = UserDb.Exec(conn, $@"INSERT OR IGNORE INTO text_queue(kind, ref, added)
            SELECT 'file', path, {Now} FROM text_docs WHERE kind = 'file' AND want_ocr = 1");
        n += UserDb.Exec(conn, $@"INSERT OR IGNORE INTO text_queue(kind, ref, added)
            SELECT DISTINCT 'mail', CAST(m.id AS TEXT), {Now} FROM text_docs d JOIN mail m ON m.account = d.account AND m.message_id = d.msgid
            WHERE d.kind = 'mail' AND d.want_ocr = 1 AND m.cached = 1");
        n += UserDb.Exec(conn, $@"INSERT OR IGNORE INTO text_queue(kind, ref, added)
            SELECT DISTINCT 'note', note_id, {Now} FROM text_docs WHERE kind = 'note' AND want_ocr = 1");
        tx.Commit();
        return n;
    }

    // ---------------- Warteschlange abarbeiten ----------------

    public sealed record Item(string Kind, string Ref, int Tries);

    /// <summary>Die naechsten faelligen Eintraege - Neueste zuerst.</summary>
    public static List<Item> Next(UserDb db, int limit) =>
        db.Query("SELECT kind, ref, tries FROM text_queue WHERE next <= $n ORDER BY added DESC LIMIT $l",
            r => new Item(r.GetString(0), r.GetString(1), r.GetInt32(2)), ("$n", Unix()), ("$l", limit));

    public static bool HasWork(UserDb db) =>
        db.Scalar("SELECT 1 FROM text_queue WHERE next <= $n LIMIT 1", ("$n", Unix())) != null;

    /// <summary>Einen Eintrag bearbeiten. Fehler beim Lesen einer Datei landen als Zustand "error" im Index;
    /// was darueber hinaus scheitert (Datenbank), wird spaeter erneut versucht und nach drei Versuchen als Fehler vermerkt.</summary>
    public static void Process(UserSpace space, Item item, ExtractLimits? limits = null, CancellationToken ct = default)
    {
        void GiveUp(string reason)
        {
            if (item.Kind == "file")
                Write(space.Db, new Doc("file", null, null, null, item.Ref, Path.GetFileName(item.Ref), null, null, null),
                    new Extracted("", "none", "error", reason));
            Dequeue(space.Db, item);
        }
        // Den Versuch VOR dem Lesen zaehlen: bringt ein Eintrag den ganzen Prozess zum Absturz (nicht abfangbar,
        // etwa ein Stack-Ueberlauf in einem Parser), kommt er nach dem Neustart nicht endlos wieder.
        if (item.Tries >= 3) { GiveUp("Abgebrochen nach mehreren Versuchen."); return; }
        space.Db.Exec("UPDATE text_queue SET tries = tries + 1, next = $n WHERE kind = $k AND ref = $r",
            ("$n", Unix() + 300 * (item.Tries + 1)), ("$k", item.Kind), ("$r", item.Ref));
        try
        {
            if (item.Kind == "file") ProcessFile(space, item.Ref, limits, ct);
            else if (item.Kind == "mail") ProcessMail(space, item.Ref, limits, ct);
            else if (item.Kind == "note") ProcessNote(space, item.Ref, limits, ct);
            Dequeue(space.Db, item);
        }
        catch (OperationCanceledException)
        {
            // Herunterfahren ist kein Fehlversuch
            space.Db.Exec("UPDATE text_queue SET tries = max(tries - 1, 0), next = $n WHERE kind = $k AND ref = $r",
                ("$n", Unix()), ("$k", item.Kind), ("$r", item.Ref));
            throw;
        }
        catch (Exception ex)
        {
            if (item.Tries >= 2) GiveUp(ex.Message);
            // sonst: Versuch ist schon gezaehlt und der naechste spaeter angesetzt
        }
    }

    /// <summary>Zeitpunkt des letzten Laufs merken (fuer die Anzeige).</summary>
    public static void MarkRun(UserDb db) => db.Set(LastKey, DateTimeOffset.UtcNow.ToString("o"));

    private static void Dequeue(UserDb db, Item item) =>
        db.Exec("DELETE FROM text_queue WHERE kind = $k AND ref = $r", ("$k", item.Kind), ("$r", item.Ref));

    private sealed record Doc(string Kind, string? Account, string? MsgId, int? Part, string? Path, string Name, string? Etag, string? NoteId, string? Blob);

    private static void ProcessFile(UserSpace space, string path, ExtractLimits? limits, CancellationToken ct)
    {
        var db = space.Db;
        var settings = space.Settings;
        // Der Abgleichordner von notizi ist geschuetzt - nie lesen, nie im Index
        if (FileStore.InRoot(path, settings.Notizi.Root ?? ""))
        {
            db.Exec("DELETE FROM text_docs WHERE kind = 'file' AND path = $p", ("$p", path));
            return;
        }
        var entry = FileStore.Get(db, path);
        if (entry == null || entry.IsDir) return;
        var ocr = settings.TextIndex.Ocr;
        var have = db.Query("SELECT etag, want_ocr FROM text_docs WHERE kind = 'file' AND path = $p", r => (Etag: r.IsDBNull(0) ? null : r.GetString(0), WantsOcr: r.GetInt64(1) != 0), ("$p", path)).FirstOrDefault();
        if (have.Etag == entry.Etag && !(have.WantsOcr && ocr && Ocr.Available)) return;

        var doc = new Doc("file", null, null, null, path, entry.Name, entry.Etag, null, null);
        if (!TextExtract.Supported(entry.Name, entry.Mime)) { Write(db, doc, Extracted.Skip("Dateityp ohne Text")); return; }
        // Gleicher Inhalt schon gelesen (Kopie, Wiederherstellen): Text uebernehmen statt erneut lesen - spart vor allem OCR
        if (Reuse(db, entry.Etag) is { } same) { Write(db, doc, same); return; }

        // Zu gross: gar nicht erst aus dem BlobStore in den Speicher holen
        var max = (limits ?? ExtractLimits.Default).MaxBytes;
        if (entry.Size > max) { Write(db, doc, Extracted.Skip($"größer als {max / 1024 / 1024} MB")); return; }
        var data = FileStore.Data(db, path) ?? Array.Empty<byte>();
        var result = TextExtract.Extract(data, entry.Name, entry.Mime, ocr, limits, ct);
        // Waehrend des Lesens geaendert oder geloescht: der Trigger hat neu eingereiht bzw. alles entfernt
        if (FileStore.Get(db, path)?.Etag != entry.Etag) return;
        Write(db, doc, result);
    }

    private static void ProcessMail(UserSpace space, string reference, ExtractLimits? limits, CancellationToken ct)
    {
        var db = space.Db;
        if (!long.TryParse(reference, out var id)) return;
        var row = db.Query("SELECT m.account, m.message_id, r.raw FROM mail m JOIN mail_raw r ON r.id = m.id WHERE m.id = $id",
            r => (Account: r.GetString(0), MsgId: r.IsDBNull(1) ? null : r.GetString(1), Raw: (byte[])r[2]), ("$id", id)).FirstOrDefault();
        if (row.Raw == null || row.MsgId == null) return;
        var ocr = space.Settings.TextIndex.Ocr;
        var have = db.Query("SELECT want_ocr FROM text_docs WHERE kind = 'mail' AND account = $a AND msgid = $m",
            r => r.GetInt64(0) != 0, ("$a", row.Account), ("$m", row.MsgId));
        if (have.Count > 0 && !(have.Any(w => w) && ocr && Ocr.Available)) return;

        var message = MimeMessage.Load(new MemoryStream(MailStore.Decompress(row.Raw)));
        var found = new List<(Doc Doc, Extracted Result)>();
        var parts = MailCompose.Parts(message);
        for (int i = 0; i < parts.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var p = parts[i];
            // Eingebettete Bilder (Logo, Signatur) sind keine Anhaenge
            if (!p.IsAttachment && p.ContentId != null) continue;
            var name = p.FileName ?? p.ContentType.Name ?? "";
            if (name.Length == 0 && !p.IsAttachment) continue;
            var doc = new Doc("mail", row.Account, row.MsgId, i, null, name.Length > 0 ? name : $"Teil {i + 1}", null, null, null);
            found.Add((doc, TextExtract.Supported(doc.Name, p.ContentType.MimeType)
                ? TextExtract.Extract(MailCompose.Decode(p), doc.Name, p.ContentType.MimeType, ocr, limits, ct)
                : Extracted.Skip("Dateityp ohne Text")));
        }
        int j = 0;
        foreach (var mp in message.Attachments.OfType<MessagePart>())
        {
            if (mp.Message == null) continue;
            var name = mp.ContentDisposition?.FileName ?? mp.ContentType.Name ?? ((mp.Message.Subject ?? "Nachricht") + ".eml");
            var text = TextExtract.MessageText(mp.Message, ocr, limits ?? ExtractLimits.Default, ct, 0);
            text = TextExtract.Clean(text, (limits ?? ExtractLimits.Default).MaxChars);
            found.Add((new Doc("mail", row.Account, row.MsgId, MessagePartBase + j++, null, name, null, null, null), new Extracted(text, "text", text.Length > 0 ? "ok" : "empty")));
        }
        if (found.Count == 0)
            found.Add((new Doc("mail", row.Account, row.MsgId, NoPart, null, "", null, null, null), Extracted.Skip("keine Anhänge")));

        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        // Inzwischen geloescht? Dann nichts schreiben
        if (!UserDb.Query(conn, "SELECT 1 FROM mail WHERE id = $id", r => 1, ("$id", id)).Any()) return;
        UserDb.Exec(conn, "DELETE FROM text_docs WHERE kind = 'mail' AND account = $a AND msgid = $m", ("$a", row.Account), ("$m", row.MsgId));
        foreach (var (doc, result) in found) Insert(conn, doc, result);
        tx.Commit();
    }

    /// <summary>Schon gelesener Text zum selben Inhalt (gleicher ETag bzw. Blob-Name), sonst null.</summary>
    private static Extracted? Reuse(UserDb db, string? etag)
    {
        if (etag == null) return null;
        var same = db.Query(@"SELECT d.method, f.body FROM text_docs d JOIN text_fts f ON f.rowid = d.id
                              WHERE d.etag = $e AND d.status = 'ok' AND d.want_ocr = 0 LIMIT 1",
            r => (Method: r.GetString(0), Body: r.GetString(1)), ("$e", etag)).FirstOrDefault();
        return same.Body != null ? new Extracted(same.Body, same.Method, "ok") : null;
    }

    /// <summary>Bilder und Anhaenge einer Notiz - aus note_blobs, wie der notizi-Abgleich sie entschluesselt abgelegt hat.
    /// Die Dateien im Abgleichordner selbst liest der Index nie.</summary>
    private static void ProcessNote(UserSpace space, string syncId, ExtractLimits? limits, CancellationToken ct)
    {
        var db = space.Db;
        var row = db.Query("SELECT images, attachments FROM notes WHERE sync_id = $s",
            r => (Images: r.GetString(0), Attachments: r.GetString(1)), ("$s", syncId)).FirstOrDefault();
        if (row.Images == null) return;
        var ocr = space.Settings.TextIndex.Ocr;
        var images = System.Text.Json.JsonSerializer.Deserialize<List<string>>(row.Images) ?? new();
        var attachments = System.Text.Json.JsonSerializer.Deserialize<List<Notizi.Sync.AttachmentRef>>(row.Attachments) ?? new();
        var wanted = attachments.Select(a => (Blob: a.Blob, Name: a.Name, Mime: (string?)a.Mime))
            .Concat(images.Select((b, i) => (Blob: b, Name: $"Bild {i + 1}", Mime: (string?)null)))
            .Where(x => !string.IsNullOrEmpty(x.Blob)).DistinctBy(x => x.Blob).ToList();

        // Unveraendert und nichts fuer die OCR offen: fertig
        var have = db.Query("SELECT blob, want_ocr FROM text_docs WHERE kind = 'note' AND note_id = $s",
            r => (Blob: r.IsDBNull(0) ? "" : r.GetString(0), WantsOcr: r.GetInt64(1) != 0), ("$s", syncId));
        var retryOcr = ocr && Ocr.Available && have.Any(h => h.WantsOcr);
        if (have.Count > 0 && !retryOcr && have.Select(h => h.Blob).Where(b => b.Length > 0).ToHashSet().SetEquals(wanted.Select(w => w.Blob))) return;

        var found = new List<(Doc Doc, Extracted Result)>();
        foreach (var (blob, name, mime) in wanted)
        {
            ct.ThrowIfCancellationRequested();
            var doc = new Doc("note", null, null, null, null, name, blob, syncId, blob);
            if (Reuse(db, blob) is { } same) { found.Add((doc, same)); continue; }
            var data = db.Scalar("SELECT data FROM note_blobs WHERE name = $n", ("$n", blob)) as byte[];
            if (data == null) { found.Add((doc, Extracted.Skip("Inhalt fehlt noch"))); continue; }
            // Eingebettete Bilder haben keinen Dateinamen: Art am Inhalt erkennen
            var fileName = Path.GetExtension(name).Length == 0 ? name + TextExtract.SniffImage(data) : name;
            found.Add((doc, TextExtract.Supported(fileName, mime)
                ? TextExtract.Extract(data, fileName, mime, ocr, limits, ct)
                : Extracted.Skip("Dateityp ohne Text")));
        }
        if (found.Count == 0)
            found.Add((new Doc("note", null, null, NoPart, null, "", null, syncId, null), Extracted.Skip("keine Anhänge")));

        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        if (!UserDb.Query(conn, "SELECT 1 FROM notes WHERE sync_id = $s", r => 1, ("$s", syncId)).Any()) return;
        UserDb.Exec(conn, "DELETE FROM text_docs WHERE kind = 'note' AND note_id = $s", ("$s", syncId));
        foreach (var (doc, result) in found) Insert(conn, doc, result);
        tx.Commit();
    }

    /// <summary>Datei-Eintrag ersetzen (alter Stand raus, neuer rein).</summary>
    private static void Write(UserDb db, Doc doc, Extracted result)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        UserDb.Exec(conn, "DELETE FROM text_docs WHERE kind = 'file' AND path = $p", ("$p", doc.Path));
        // Datei inzwischen weg: nichts schreiben (der Trigger hat schon aufgeraeumt)
        if (UserDb.Query(conn, "SELECT 1 FROM files WHERE path = $p AND is_dir = 0", r => 1, ("$p", doc.Path)).Any())
            Insert(conn, doc, result);
        tx.Commit();
    }

    private static void Insert(SqliteConnection conn, Doc doc, Extracted r)
    {
        var id = UserDb.Query(conn, @"INSERT INTO text_docs(kind, account, msgid, part, path, note_id, blob, name, etag, method, status, note, want_ocr, indexed)
            VALUES ($k, $a, $m, $part, $p, $nid, $blob, $n, $e, $method, $s, $note, $w, $t) RETURNING id", r => r.GetInt64(0),
            ("$k", doc.Kind), ("$a", doc.Account), ("$m", doc.MsgId), ("$part", doc.Part), ("$p", doc.Path), ("$nid", doc.NoteId), ("$blob", doc.Blob), ("$n", doc.Name),
            ("$e", doc.Etag), ("$method", r.Method), ("$s", r.Status), ("$note", r.Note), ("$w", r.WantsOcr ? 1 : 0), ("$t", Unix())).First();
        if (r.Text.Length > 0)
            UserDb.Exec(conn, "INSERT INTO text_fts(rowid, name, body, tags) VALUES ($id, $n, $b, '')", ("$id", id), ("$n", doc.Name), ("$b", r.Text));
    }

    // ---------------- Pflege ----------------

    /// <summary>Aufraeumen: Text von Mails, die es seit <see cref="GoneGrace"/> nicht mehr gibt, von entfernten Konten
    /// und alles im Abgleichordner von notizi (falls er nachtraeglich dorthin gelegt wurde).</summary>
    public static int Prune(UserSpace space)
    {
        var db = space.Db;
        var n = db.Exec("DELETE FROM text_docs WHERE kind = 'mail' AND gone IS NOT NULL AND gone < $t", ("$t", Unix() - (long)GoneGrace.TotalSeconds));
        var accounts = space.Settings.MailAccounts.Select(a => a.Id).ToHashSet();
        foreach (var acc in db.Query("SELECT DISTINCT account FROM text_docs WHERE kind = 'mail'", r => r.GetString(0)).Where(a => !accounts.Contains(a)))
            n += db.Exec("DELETE FROM text_docs WHERE kind = 'mail' AND account = $a", ("$a", acc));
        var root = FileStore.Normalize(space.Settings.Notizi.Root ?? "");
        if (root.Length > 0)
            n += db.Exec("DELETE FROM text_docs WHERE kind = 'file' AND (path = $r OR substr(path, 1, length($r) + 1) = $r || '/')", ("$r", root));
        return n;
    }

    // ---------------- Zustand ----------------

    public sealed record ErrorItem(string Kind, string Name, string? Path, string? Note);

    public sealed record Status(bool Enabled, bool Ocr, bool OcrAvailable, string? OcrReason, string OcrLanguages,
        int Done, int WithText, int ByOcr, int Pending, int Errors, int Skipped, DateTimeOffset? LastRun, List<ErrorItem> RecentErrors);

    public static Status State(UserSpace space)
    {
        var db = space.Db;
        var s = space.Settings.TextIndex;
        var counts = db.Query("SELECT status, method, COUNT(*) FROM text_docs WHERE part IS NULL OR part >= 0 GROUP BY status, method",
            r => (Status: r.GetString(0), Method: r.GetString(1), Count: r.GetInt32(2)));
        int Sum(Func<(string Status, string Method, int Count), bool> f) => counts.Where(f).Sum(c => c.Count);
        var pending = Convert.ToInt32(db.Scalar("SELECT COUNT(*) FROM text_queue") ?? 0L);
        var errors = db.Query("SELECT kind, name, path, note FROM text_docs WHERE status = 'error' ORDER BY indexed DESC LIMIT 20",
            r => new ErrorItem(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)));
        DateTimeOffset? last = DateTimeOffset.TryParse(db.Get(LastKey), out var l) ? l : null;
        var tools = Ocr.Current;
        return new Status(s.Enabled, s.Ocr, tools.Available, tools.Reason, tools.Languages,
            Sum(c => c.Status != "error"), Sum(c => c.Status == "ok"), Sum(c => c.Status == "ok" && c.Method == "ocr"),
            pending, Sum(c => c.Status == "error"), Sum(c => c.Status == "skipped"), last, errors);
    }

    // ---------------- Suche ----------------

    /// <summary>Freie Woerter einer Suche als FTS5-Ausdruck (Praefixsuche, alle Woerter muessen vorkommen).
    /// Feldangaben wie "von:" gelten der Mail, nicht dem Anhang - sie bleiben hier aussen vor.</summary>
    internal static string FtsQuery(string query)
    {
        var parts = new List<string>();
        foreach (var raw in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw.Contains(':') && raw.IndexOf(':') < raw.Length - 1 && raw.Split(':')[0].ToLowerInvariant() is "von" or "from" or "an" or "to" or "betreff" or "subject")
                continue;
            var word = new string(raw.Where(c => char.IsLetterOrDigit(c) || c is '@' or '.' or '-' or '_').ToArray());
            if (!word.Any(char.IsLetterOrDigit)) continue;
            parts.Add("\"" + word + "\"*");
        }
        return string.Join(" AND ", parts);
    }

    private static readonly string Snippet = $"snippet(text_fts, 1, '{MarkStart}', '{MarkEnd}', '…', 14)";

    /// <summary>Dateien der Ablage nach Inhalt (und Name), auf Wunsch unterhalb eines Ordners. Ohne notizi-Ordner.</summary>
    public static List<FileHit> SearchFiles(UserSpace space, string query, string? under = null, int limit = 100)
    {
        var match = FtsQuery(query);
        if (match.Length == 0) return new();
        under = FileStore.Normalize(under ?? "");
        var root = space.Settings.Notizi.Root ?? "";
        var hits = space.Db.Query($@"SELECT d.path, d.name, f.size, f.mime, f.modified, d.method, {Snippet}
                FROM text_fts JOIN text_docs d ON d.id = text_fts.rowid JOIN files f ON f.path = d.path
                WHERE text_fts MATCH $q AND d.kind = 'file' AND ($u = '' OR substr(d.path, 1, length($u) + 1) = $u || '/')
                ORDER BY bm25(text_fts, 4.0, 1.0, 2.0) LIMIT $l",
            r => new FileHit(r.GetString(0), r.GetString(1), r.GetInt64(2), r.IsDBNull(3) ? null : r.GetString(3),
                DateTime.Parse(r.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind), r.GetString(5), r.GetString(6)),
            ("$q", match), ("$u", under), ("$l", limit * 2));
        return hits.Where(h => !FileStore.InRoot(h.Path, root)).Take(limit).ToList();
    }

    /// <summary>Notizen (nicht im Papierkorb), in deren Bildern oder Anhaengen alle Woerter vorkommen - je Notiz der beste Treffer,
    /// Schluessel ist die sync_id.</summary>
    public static Dictionary<string, AttachmentMatch> SearchNoteAttachments(UserDb db, string query, int limit = 200)
    {
        var match = FtsQuery(query);
        var result = new Dictionary<string, AttachmentMatch>();
        if (match.Length == 0) return result;
        var rows = db.Query($@"SELECT d.note_id, d.name, d.blob, d.method, {Snippet}
                FROM text_fts JOIN text_docs d ON d.id = text_fts.rowid JOIN notes n ON n.sync_id = d.note_id
                WHERE text_fts MATCH $q AND d.kind = 'note' AND n.deleted IS NULL
                ORDER BY bm25(text_fts, 4.0, 1.0, 2.0) LIMIT $l",
            r => (Note: r.GetString(0), Match: new AttachmentMatch(r.GetString(1), -1, r.GetString(3), r.GetString(4), r.IsDBNull(2) ? null : r.GetString(2))),
            ("$q", match), ("$l", limit * 3));
        foreach (var (note, m) in rows) result.TryAdd(note, m);
        return result;
    }

    /// <summary>Mails, in deren Anhaengen alle freien Woerter vorkommen - je Mail der beste Anhang.</summary>
    /// <param name="where">Zusaetzliche Bedingung an die Mail (Alias m), wie in <see cref="MailStore.Search"/>.</param>
    internal static Dictionary<long, AttachmentMatch> SearchMailAttachments(SqliteConnection conn, string query, string where,
        (string, object?)[] args, int limit)
    {
        var match = FtsQuery(query);
        var result = new Dictionary<long, AttachmentMatch>();
        if (match.Length == 0) return result;
        var rows = UserDb.Query(conn, $@"SELECT m.id, d.name, d.part, d.method, {Snippet}
                FROM text_fts JOIN text_docs d ON d.id = text_fts.rowid
                JOIN mail m ON m.account = d.account AND m.message_id = d.msgid
                WHERE text_fts MATCH $tq AND d.kind = 'mail' AND {where}
                ORDER BY bm25(text_fts, 4.0, 1.0, 2.0) LIMIT $tl",
            r => (Id: r.GetInt64(0), Match: new AttachmentMatch(r.GetString(1), r.GetInt32(2), r.GetString(3), r.GetString(4))),
            args.Concat(new (string, object?)[] { ("$tq", match), ("$tl", limit * 3) }).ToArray());
        foreach (var (id, m) in rows) result.TryAdd(id, m);
        return result;
    }
}
