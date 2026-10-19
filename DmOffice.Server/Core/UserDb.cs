using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DmOffice.Core;

/// <summary>
/// Die Datenbank eines Benutzers - eine SQLCipher-Datei mit allem, was ihm
/// gehoert: Einstellungen, Mails samt Anhaengen, Kalender, Kontakte,
/// Dateien, Notizen. Wer die Datei ohne Schluessel kopiert, hat nichts.
/// </summary>
public sealed class UserDb
{
    private readonly string _connectionString;
    public string UserId { get; }

    static UserDb() => SQLitePCL.Batteries_V2.Init();

    public UserDb(string path, byte[] key, string userId)
    {
        UserId = userId;
        // "x'…'": roher Schluessel, keine Ableitung - er ist schon zufaellig.
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Password = "x'" + Convert.ToHexString(key) + "'",
            DefaultTimeout = 30
        }.ToString();
        Migrate();
    }

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        // journal_size_limit: nach einem Checkpoint wird das Journal auf 64 MB gekuerzt - sonst bleibt es so gross wie sein Hoechststand
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=30000; PRAGMA synchronous=NORMAL; PRAGMA journal_size_limit=67108864;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    public int Exec(string sql, params (string Name, object? Value)[] args)
    {
        using var conn = Open();
        return Exec(conn, sql, args);
    }

    public static int Exec(SqliteConnection conn, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd.ExecuteNonQuery();
    }

    public object? Scalar(string sql, params (string Name, object? Value)[] args)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var r = cmd.ExecuteScalar();
        return r is DBNull ? null : r;
    }

    public List<T> Query<T>(string sql, Func<SqliteDataReader, T> read, params (string Name, object? Value)[] args)
    {
        using var conn = Open();
        return Query(conn, sql, read, args);
    }

    public static List<T> Query<T>(SqliteConnection conn, string sql, Func<SqliteDataReader, T> read, params (string Name, object? Value)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        using var r = cmd.ExecuteReader();
        var result = new List<T>();
        while (r.Read()) result.Add(read(r));
        return result;
    }

    // ---------------- Pflege ----------------

    /// <summary>Groesse der Datei, des Journals und der freien Seiten darin.</summary>
    public (long File, long Wal, long Free) SpaceInfo()
    {
        using var conn = Open();
        var pageSize = Query(conn, "PRAGMA page_size", r => r.GetInt64(0)).First();
        var free = Query(conn, "PRAGMA freelist_count", r => r.GetInt64(0)).First();
        long Size(string f) => File.Exists(f) ? new FileInfo(f).Length : 0;
        var path = new SqliteConnectionStringBuilder(_connectionString).DataSource;
        return (Size(path), Size(path + "-wal"), free * pageSize);
    }

    /// <summary>
    /// Verdichten: Journal einarbeiten und die Datei ohne freie Seiten neu schreiben. Sperrt die Datenbank
    /// fuer die Dauer - Schreiber warten oder versuchen es beim naechsten Durchgang.
    /// </summary>
    public void Compact()
    {
        using var conn = Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandTimeout = 0;
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE); VACUUM; PRAGMA wal_checkpoint(TRUNCATE);";
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Journal einarbeiten und kuerzen - nach groesseren Schreibvorgaengen.</summary>
    public void Checkpoint()
    {
        using var conn = Open();
        Exec(conn, "PRAGMA wal_checkpoint(TRUNCATE)");
    }

    // ---------------- Schluessel/Wert ----------------

    public string? Get(string key) => Scalar("SELECT value FROM kv WHERE key=$k", ("$k", key)) as string;

    public void Set(string key, string? value)
    {
        if (value == null) Exec("DELETE FROM kv WHERE key=$k", ("$k", key));
        else Exec("INSERT INTO kv(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v", ("$k", key), ("$v", value));
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public T GetJson<T>(string key) where T : new() =>
        Get(key) is { } s ? JsonSerializer.Deserialize<T>(s, Json) ?? new T() : new T();

    public void SetJson<T>(string key, T value) => Set(key, JsonSerializer.Serialize(value, Json));

    // ---------------- Schema ----------------

    private void Migrate()
    {
        using var conn = Open();
        var version = Convert.ToInt32(Query(conn, "PRAGMA user_version", r => r.GetInt64(0)).First());
        if (version < 1)
        {
            Exec(conn, Schema1);
            Exec(conn, "PRAGMA user_version=1");
        }
        if (version < 2)
        {
            // Bewertungen eines noch ungelernten Spamfilters (immer 0.5) waren keine
            Exec(conn, "UPDATE mail SET spam=NULL WHERE spam=0.5");
            Exec(conn, "PRAGMA user_version=2");
        }
        if (version < 3)
        {
            // Mailinhalte in eigene Tabelle: Kopfzeilen bleiben klein, Abfragen ohne Index lesen nicht mehr
            // die ganze Datei (gemessen bei 40.000 Mails: Markiert 7 s -> wenige ms). Einmalig beim Start.
            using var tx = conn.BeginTransaction();
            Exec(conn, @"
CREATE TABLE mail_raw (id INTEGER PRIMARY KEY REFERENCES mail(id) ON DELETE CASCADE, raw BLOB NOT NULL);
INSERT INTO mail_raw(id, raw) SELECT id, raw FROM mail WHERE raw IS NOT NULL;
ALTER TABLE mail ADD COLUMN cached INTEGER NOT NULL DEFAULT 0;
UPDATE mail SET cached = 1 WHERE raw IS NOT NULL;
DROP INDEX IF EXISTS mail_body;
ALTER TABLE mail DROP COLUMN raw;
CREATE INDEX mail_missing ON mail(account, folder, date DESC) WHERE cached = 0;
CREATE INDEX mail_flagged ON mail(account, date DESC) WHERE flagged = 1;
CREATE INDEX mail_unseen ON mail(account, folder, date DESC) WHERE seen = 0;
CREATE INDEX mail_attach ON mail(account, folder, date DESC) WHERE has_att = 1;
PRAGMA user_version=3;");
            tx.Commit();
            Exec(conn, "PRAGMA wal_checkpoint(TRUNCATE)");
        }
        if (Query(conn, "PRAGMA user_version", r => r.GetInt64(0)).First() < 4)
        {
            // IMAP-Schlagworte (Kategorien), mit Leerzeichen eingefasst: " dm_rechnung dm_privat "
            Exec(conn, "ALTER TABLE mail ADD COLUMN keywords TEXT NOT NULL DEFAULT ''; PRAGMA user_version=4;");
        }
        if (Query(conn, "PRAGMA user_version", r => r.GetInt64(0)).First() < 5)
        {
            // Der Spamfilter rechnete bis hierher verkehrt herum - alte Werte sind falsch
            Exec(conn, "UPDATE mail SET spam=NULL WHERE spam IS NOT NULL; PRAGMA user_version=5;");
        }
        if (Query(conn, "PRAGMA user_version", r => r.GetInt64(0)).First() < 6)
        {
            // Geraeteschluessel der Handy-App fuer den verschluesselten Umschlag (E2e.cs), je App-Passwort
            Exec(conn, @"CREATE TABLE e2e_devices (
    id              TEXT PRIMARY KEY,
    app_password_id TEXT NOT NULL REFERENCES app_passwords(id) ON DELETE CASCADE,
    pubkey          TEXT NOT NULL,
    created         TEXT NOT NULL,
    counter         INTEGER NOT NULL DEFAULT 0
); PRAGMA user_version=6;");
        }
        if (Query(conn, "PRAGMA user_version", r => r.GetInt64(0)).First() < 7)
        {
            // Browser-Geraete: kein App-Passwort, dafuer der Stempel der Anmeldung (Passwortwechsel macht sie ungueltig)
            Exec(conn, @"CREATE TABLE e2e_devices_new (
    id              TEXT PRIMARY KEY,
    app_password_id TEXT NULL REFERENCES app_passwords(id) ON DELETE CASCADE,
    stamp           TEXT NULL,
    pubkey          TEXT NOT NULL,
    created         TEXT NOT NULL,
    counter         INTEGER NOT NULL DEFAULT 0
);
INSERT INTO e2e_devices_new(id, app_password_id, stamp, pubkey, created, counter) SELECT id, app_password_id, NULL, pubkey, created, counter FROM e2e_devices;
DROP TABLE e2e_devices;
ALTER TABLE e2e_devices_new RENAME TO e2e_devices;
PRAGMA user_version=7;");
        }
        if (Query(conn, "PRAGMA user_version", r => r.GetInt64(0)).First() < 8)
        {
            // Push-Tokens der Handy-App (Push.cs), fallen mit dem App-Passwort
            Exec(conn, @"CREATE TABLE push_tokens (
    token           TEXT PRIMARY KEY,
    app_password_id TEXT NOT NULL REFERENCES app_passwords(id) ON DELETE CASCADE,
    created         TEXT NOT NULL
); PRAGMA user_version=8;");
        }
        if (Query(conn, "PRAGMA user_version", r => r.GetInt64(0)).First() < 9)
        {
            // Prio-Posteingang (Mail/Focus.cs): 1 Relevant, 0 Sonstige, NULL noch nicht eingeordnet;
            // fest gemerkte Absender und gelernte Woerter wie beim Spamfilter
            Exec(conn, @"ALTER TABLE mail ADD COLUMN focus INTEGER NULL;
CREATE TABLE focus_senders (address TEXT PRIMARY KEY, focused INTEGER NOT NULL);
CREATE TABLE focus_tokens (token TEXT PRIMARY KEY, other INTEGER NOT NULL DEFAULT 0, focused INTEGER NOT NULL DEFAULT 0);
CREATE TABLE focus_trained (key TEXT PRIMARY KEY, focused INTEGER NOT NULL);
PRAGMA user_version=9;");
        }
    }

    private const string Schema1 = @"
CREATE TABLE kv (key TEXT PRIMARY KEY, value TEXT NOT NULL);

CREATE TABLE app_passwords (
    id        TEXT PRIMARY KEY,
    name      TEXT NOT NULL,
    hash      TEXT NOT NULL UNIQUE,
    created   TEXT NOT NULL,
    last_used TEXT NULL
);

-- ================= Mail =================
CREATE TABLE mail_folders (
    account     TEXT NOT NULL,
    path        TEXT NOT NULL,
    name        TEXT NOT NULL,
    delimiter   TEXT NOT NULL DEFAULT '/',
    special     TEXT NULL,
    selectable  INTEGER NOT NULL DEFAULT 1,
    uidvalidity INTEGER NOT NULL DEFAULT 0,
    modseq      INTEGER NOT NULL DEFAULT 0,
    unread      INTEGER NOT NULL DEFAULT 0,
    total       INTEGER NOT NULL DEFAULT 0,
    synced      TEXT NULL,
    PRIMARY KEY (account, path)
);

CREATE TABLE mail (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    account      TEXT    NOT NULL,
    folder       TEXT    NOT NULL,
    uid          INTEGER NOT NULL,
    message_id   TEXT    NULL,
    in_reply_to  TEXT    NULL,
    thread       TEXT    NULL,
    subject      TEXT    NOT NULL DEFAULT '',
    from_name    TEXT    NULL,
    from_addr    TEXT    NULL,
    recipients   TEXT    NOT NULL DEFAULT '[]',
    date         INTEGER NOT NULL,
    size         INTEGER NOT NULL DEFAULT 0,
    seen         INTEGER NOT NULL DEFAULT 0,
    flagged      INTEGER NOT NULL DEFAULT 0,
    answered     INTEGER NOT NULL DEFAULT 0,
    forwarded    INTEGER NOT NULL DEFAULT 0,
    has_att      INTEGER NOT NULL DEFAULT 0,
    preview      TEXT    NOT NULL DEFAULT '',
    spam         REAL    NULL,
    raw          BLOB    NULL,
    UNIQUE (account, folder, uid)
);
CREATE INDEX mail_list ON mail(account, folder, date DESC);
CREATE INDEX mail_body ON mail(account, folder) WHERE raw IS NULL;

CREATE VIRTUAL TABLE mail_fts USING fts5(
    subject, sender, recipients, body, tokenize='unicode61 remove_diacritics 2'
);

CREATE TABLE spam_tokens (token TEXT PRIMARY KEY, spam INTEGER NOT NULL DEFAULT 0, ham INTEGER NOT NULL DEFAULT 0);
-- Welche Mail als was gelernt wurde - damit 'Kein Junk' das Gelernte umkehrt statt doppelt zu zaehlen
CREATE TABLE spam_trained (key TEXT PRIMARY KEY, as_spam INTEGER NOT NULL);

-- ================= Kalender, Aufgaben, Kontakte =================
CREATE TABLE collections (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    kind        TEXT    NOT NULL,              -- calendar | addressbook
    uri         TEXT    NOT NULL,
    name        TEXT    NOT NULL,
    color       TEXT    NULL,
    description TEXT    NULL,
    components  TEXT    NOT NULL DEFAULT '',   -- VEVENT,VTODO
    sort_order  INTEGER NOT NULL DEFAULT 0,
    synctoken   INTEGER NOT NULL DEFAULT 1,
    UNIQUE (kind, uri)
);

CREATE TABLE objects (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    collection  INTEGER NOT NULL REFERENCES collections(id) ON DELETE CASCADE,
    uri         TEXT    NOT NULL,
    uid         TEXT    NULL,
    component   TEXT    NOT NULL,              -- VEVENT | VTODO | VCARD
    etag        TEXT    NOT NULL,
    data        TEXT    NOT NULL,
    first_start INTEGER NULL,                  -- Unix ms; fuer Zeitraum-Abfragen
    last_end    INTEGER NULL,                  -- NULL: unbegrenzt (Serie ohne Ende)
    modified    TEXT    NOT NULL,
    UNIQUE (collection, uri)
);
CREATE INDEX objects_range ON objects(collection, first_start, last_end);

CREATE TABLE changes (
    collection INTEGER NOT NULL REFERENCES collections(id) ON DELETE CASCADE,
    synctoken  INTEGER NOT NULL,
    uri        TEXT    NOT NULL,
    deleted    INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX changes_token ON changes(collection, synctoken);

CREATE VIRTUAL TABLE contacts_fts USING fts5(name, emails, org, phones, tokenize='unicode61 remove_diacritics 2');

-- ================= Dateien (WebDAV) =================
CREATE TABLE files (
    path     TEXT    PRIMARY KEY,              -- ohne fuehrenden/abschliessenden Schraegstrich; '' = Wurzel
    parent   TEXT    NULL,
    is_dir   INTEGER NOT NULL,
    data     BLOB    NULL,
    size     INTEGER NOT NULL DEFAULT 0,
    etag     TEXT    NOT NULL,
    mime     TEXT    NULL,
    created  TEXT    NOT NULL,
    modified TEXT    NOT NULL
);
CREATE INDEX files_parent ON files(parent);
INSERT INTO files(path, parent, is_dir, etag, created, modified) VALUES ('', NULL, 1, 'root', datetime('now'), datetime('now'));

-- ================= Notizen (notizi-Abgleich) =================
CREATE TABLE notes (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    sync_id      TEXT    NOT NULL UNIQUE,
    sync_rev     INTEGER NOT NULL DEFAULT 0,
    folder       TEXT    NULL,
    sort_order   REAL    NULL,
    title        TEXT    NOT NULL DEFAULT '',
    custom_title TEXT    NULL,
    color        TEXT    NOT NULL DEFAULT '#FFF9A8',
    important    INTEGER NOT NULL DEFAULT 0,
    alarm        TEXT    NULL,
    alarm_active INTEGER NOT NULL DEFAULT 1,
    archived     TEXT    NULL,
    deleted      TEXT    NULL,
    created      TEXT    NOT NULL,
    modified     TEXT    NOT NULL,
    html         TEXT    NOT NULL,
    plain        TEXT    NOT NULL DEFAULT '',
    editor       TEXT    NULL,
    images       TEXT    NOT NULL DEFAULT '[]',
    attachments  TEXT    NOT NULL DEFAULT '[]',
    sources      TEXT    NOT NULL DEFAULT '[]'
);
CREATE TABLE note_folders (
    sync_id    TEXT    PRIMARY KEY,
    sync_rev   INTEGER NOT NULL DEFAULT 0,
    parent     TEXT    NULL,
    name       TEXT    NOT NULL,
    sort_order REAL    NOT NULL DEFAULT 0,
    created    TEXT    NOT NULL,
    modified   TEXT    NOT NULL
);
CREATE TABLE note_blobs (name TEXT PRIMARY KEY, data BLOB NOT NULL);
CREATE TABLE note_sync_state (
    sync_id    TEXT    PRIMARY KEY,
    kind       TEXT    NOT NULL,
    base_rev   INTEGER NOT NULL,
    remote_tag TEXT    NOT NULL,
    base_item  BLOB    NULL
);
CREATE TABLE note_tombstones (sync_id TEXT PRIMARY KEY, kind TEXT NOT NULL);
CREATE VIRTUAL TABLE notes_fts USING fts5(
    title, plain, content='notes', content_rowid='id', tokenize='unicode61 remove_diacritics 2'
);
CREATE TRIGGER notes_ai AFTER INSERT ON notes BEGIN
    INSERT INTO notes_fts(rowid, title, plain) VALUES (new.id, new.title, new.plain);
END;
CREATE TRIGGER notes_ad AFTER DELETE ON notes BEGIN
    INSERT INTO notes_fts(notes_fts, rowid, title, plain) VALUES ('delete', old.id, old.title, old.plain);
END;
CREATE TRIGGER notes_au AFTER UPDATE OF title, plain ON notes BEGIN
    INSERT INTO notes_fts(notes_fts, rowid, title, plain) VALUES ('delete', old.id, old.title, old.plain);
    INSERT INTO notes_fts(rowid, title, plain) VALUES (new.id, new.title, new.plain);
END;
";
}
