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

    /// <summary>Inhalte der Dateiablage, einzeln verschluesselt auf der Platte (die Datenbank haelt nur den Verweis).</summary>
    public BlobStore Blobs { get; }

    static UserDb() => SQLitePCL.Batteries_V2.Init();

    /// <param name="blobDir">Ordner fuer die Dateiinhalte; ohne Angabe neben der Datenbank (Tests).</param>
    public UserDb(string path, byte[] key, string userId, string? blobDir = null)
    {
        UserId = userId;
        Blobs = new BlobStore(blobDir ?? path + ".blobs", key);
        // "x'…'": roher Schluessel, keine Ableitung - er ist schon zufaellig.
        _connectionString = ConnectionStringOf(path, key);

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
            // auto_vacuum=INCREMENTAL wirkt erst mit dem VACUUM - danach geht Reclaim ohne Sperre der ganzen Datei
            cmd.CommandText = "PRAGMA auto_vacuum=INCREMENTAL; PRAGMA wal_checkpoint(TRUNCATE); VACUUM; PRAGMA wal_checkpoint(TRUNCATE);";
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Kann die Datei freie Seiten ohne VACUUM abgeben (auto_vacuum=INCREMENTAL)?</summary>
    public bool Incremental()
    {
        using var conn = Open();
        return Query(conn, "PRAGMA auto_vacuum", r => r.GetInt64(0)).First() == 2;
    }

    /// <summary>Freie Seiten an das Dateisystem zurueckgeben - schnell, sperrt nur kurz.
    /// Nur bei auto_vacuum=INCREMENTAL (nach dem ersten Compact); gibt die frei gewordenen Bytes zurueck.</summary>
    public long ReclaimIncremental()
    {
        var before = SpaceInfo();
        using (var conn = Open())
        {
            if (Query(conn, "PRAGMA auto_vacuum", r => r.GetInt64(0)).First() != 2) return 0;
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 0;
            cmd.CommandText = "PRAGMA incremental_vacuum; PRAGMA wal_checkpoint(TRUNCATE);";
            // Jeder Schritt gibt Seiten frei - bis zum Ende lesen
            using var r = cmd.ExecuteReader();
            do { while (r.Read()) { } } while (r.NextResult());
        }
        var after = SpaceInfo();
        return Math.Max(0, before.File + before.Wal - after.File - after.Wal);
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

    /// <summary>Ein Schritt der Schemaversion: laeuft samt dem Setzen von user_version in einer Transaktion - bricht er ab
    /// (Platte voll, Strom weg, Neustart), bleibt die Datenbank auf der vorigen Version und der Schritt laeuft beim
    /// naechsten Start von vorn.</summary>
    public sealed record SchemaStep(int Version, string Title, Action<SqliteConnection> Run);

    private static void Run(SqliteConnection conn, string sql) => Exec(conn, sql);

    /// <summary>Alle Schritte von der leeren Datenbank bis heute. Neue Schritte nur hinten anhaengen, nie umschreiben.</summary>
    public static readonly IReadOnlyList<SchemaStep> Steps =
    [
        new(1, "Grundstruktur", c => Run(c, Schema1)),
        // Bewertungen eines noch ungelernten Spamfilters (immer 0.5) waren keine
        new(2, "Spamfilter bereinigen", c => Run(c, "UPDATE mail SET spam=NULL WHERE spam=0.5")),
        // Mailinhalte in eigene Tabelle: Kopfzeilen bleiben klein, Abfragen ohne Index lesen nicht mehr
        // die ganze Datei (gemessen bei 40.000 Mails: Markiert 7 s -> wenige ms)
        new(3, "Mailinhalte in eigene Tabelle", c => Run(c, @"
CREATE TABLE mail_raw (id INTEGER PRIMARY KEY REFERENCES mail(id) ON DELETE CASCADE, raw BLOB NOT NULL);
INSERT INTO mail_raw(id, raw) SELECT id, raw FROM mail WHERE raw IS NOT NULL;
ALTER TABLE mail ADD COLUMN cached INTEGER NOT NULL DEFAULT 0;
UPDATE mail SET cached = 1 WHERE raw IS NOT NULL;
DROP INDEX IF EXISTS mail_body;
ALTER TABLE mail DROP COLUMN raw;
CREATE INDEX mail_missing ON mail(account, folder, date DESC) WHERE cached = 0;
CREATE INDEX mail_flagged ON mail(account, date DESC) WHERE flagged = 1;
CREATE INDEX mail_unseen ON mail(account, folder, date DESC) WHERE seen = 0;
CREATE INDEX mail_attach ON mail(account, folder, date DESC) WHERE has_att = 1;")),
        // IMAP-Schlagworte (Kategorien), mit Leerzeichen eingefasst: " dm_rechnung dm_privat "
        new(4, "Mail-Schlagwörter", c => Run(c, "ALTER TABLE mail ADD COLUMN keywords TEXT NOT NULL DEFAULT ''")),
        // Der Spamfilter rechnete bis hierher verkehrt herum - alte Werte sind falsch
        new(5, "Spamfilter neu bewerten", c => Run(c, "UPDATE mail SET spam=NULL WHERE spam IS NOT NULL")),
        // Geraeteschluessel der Handy-App fuer den verschluesselten Umschlag (E2e.cs), je App-Passwort
        new(6, "Geräteschlüssel der App", c => Run(c, @"CREATE TABLE e2e_devices (
    id              TEXT PRIMARY KEY,
    app_password_id TEXT NOT NULL REFERENCES app_passwords(id) ON DELETE CASCADE,
    pubkey          TEXT NOT NULL,
    created         TEXT NOT NULL,
    counter         INTEGER NOT NULL DEFAULT 0
)")),
        // Browser-Geraete: kein App-Passwort, dafuer der Stempel der Anmeldung (Passwortwechsel macht sie ungueltig)
        new(7, "Browser-Geräte", c => Run(c, @"CREATE TABLE e2e_devices_new (
    id              TEXT PRIMARY KEY,
    app_password_id TEXT NULL REFERENCES app_passwords(id) ON DELETE CASCADE,
    stamp           TEXT NULL,
    pubkey          TEXT NOT NULL,
    created         TEXT NOT NULL,
    counter         INTEGER NOT NULL DEFAULT 0
);
INSERT INTO e2e_devices_new(id, app_password_id, stamp, pubkey, created, counter) SELECT id, app_password_id, NULL, pubkey, created, counter FROM e2e_devices;
DROP TABLE e2e_devices;
ALTER TABLE e2e_devices_new RENAME TO e2e_devices")),
        // Push-Tokens der Handy-App (Push.cs), fallen mit dem App-Passwort
        new(8, "Push-Tokens", c => Run(c, @"CREATE TABLE push_tokens (
    token           TEXT PRIMARY KEY,
    app_password_id TEXT NOT NULL REFERENCES app_passwords(id) ON DELETE CASCADE,
    created         TEXT NOT NULL
)")),
        // Prio-Posteingang (Mail/Focus.cs): 1 Relevant, 0 Sonstige, NULL noch nicht eingeordnet;
        // fest gemerkte Absender und gelernte Woerter wie beim Spamfilter
        new(9, "Prio-Posteingang", c => Run(c, @"ALTER TABLE mail ADD COLUMN focus INTEGER NULL;
CREATE TABLE focus_senders (address TEXT PRIMARY KEY, focused INTEGER NOT NULL);
CREATE TABLE focus_tokens (token TEXT PRIMARY KEY, other INTEGER NOT NULL DEFAULT 0, focused INTEGER NOT NULL DEFAULT 0);
CREATE TABLE focus_trained (key TEXT PRIMARY KEY, focused INTEGER NOT NULL)")),
        // Speichermodus "Schlank" (MailStore.Discard): wann eine Mail zuletzt geoeffnet wurde
        new(10, "Speichermodus Schlank", c => Run(c, "ALTER TABLE mail ADD COLUMN opened INTEGER NULL")),
        // Nextcloud-Clients: feste Nummer je Datei (oc:fileid), bleibt beim Verschieben und Ueberschreiben,
        // wird nie wiederverwendet (sonst hielte der Client eine neue Datei fuer eine verschobene alte).
        // Das UPDATE schreibt jede Zeile der Dateiablage neu - samt Inhalt, solange der noch in der Datenbank liegt.
        new(11, "Dateinummern für Nextcloud-Clients", c => Run(c, @"ALTER TABLE files ADD COLUMN fileid INTEGER NULL;
UPDATE files SET fileid=rowid;
CREATE UNIQUE INDEX files_fileid ON files(fileid);
CREATE TABLE file_ids (n INTEGER NOT NULL);
INSERT INTO file_ids SELECT COALESCE(MAX(fileid),0) FROM files;
CREATE TRIGGER files_new_fileid AFTER INSERT ON files WHEN NEW.fileid IS NULL BEGIN
    UPDATE file_ids SET n=n+1;
    UPDATE files SET fileid=(SELECT n FROM file_ids) WHERE path=NEW.path;
END")),
        // App-Passwoerter mit Bereich: NULL alles (wie bisher), "notizi" nur der Abgleichordner von notizi
        new(12, "Bereich der App-Passwörter", c => Run(c, "ALTER TABLE app_passwords ADD COLUMN scope TEXT NULL")),
        // Dateiablage (FileStore, FilesApi): Papierkorb (ein Eintrag je Loeschung, darunter alle Zeilen samt Inhalt),
        // fruehere Versionen, Vorschaubilder, Favoriten, zuletzt verwendet, oeffentliche Links. Alles ueber fileid,
        // damit es Umbenennen und Verschieben uebersteht.
        new(13, "Papierkorb, Versionen und Links der Dateiablage", c => Run(c, @"CREATE TABLE file_trash (
    id      INTEGER PRIMARY KEY AUTOINCREMENT,
    path    TEXT    NOT NULL,
    is_dir  INTEGER NOT NULL,
    size    INTEGER NOT NULL,
    deleted TEXT    NOT NULL
);
CREATE TABLE file_trash_items (
    trash_id INTEGER NOT NULL REFERENCES file_trash(id) ON DELETE CASCADE,
    rel      TEXT    NOT NULL,
    is_dir   INTEGER NOT NULL,
    size     INTEGER NOT NULL,
    etag     TEXT    NOT NULL,
    mime     TEXT    NULL,
    created  TEXT    NOT NULL,
    modified TEXT    NOT NULL,
    fileid   INTEGER NULL,
    data     BLOB    NULL,
    PRIMARY KEY (trash_id, rel)
);
CREATE TABLE file_versions (
    id       INTEGER PRIMARY KEY AUTOINCREMENT,
    fileid   INTEGER NOT NULL,
    size     INTEGER NOT NULL,
    etag     TEXT    NOT NULL,
    modified TEXT    NOT NULL,
    saved    TEXT    NOT NULL,
    data     BLOB    NULL
);
CREATE INDEX file_versions_fileid ON file_versions(fileid);
CREATE TABLE file_thumbs (
    fileid INTEGER NOT NULL,
    size   INTEGER NOT NULL,
    etag   TEXT    NOT NULL,
    data   BLOB    NOT NULL,
    PRIMARY KEY (fileid, size)
);
-- Inhalt (BLOB) steht immer zuletzt: Spalten dahinter liest SQLite nur ueber die ganze Ueberlaufkette.
-- In files steht data mittendrin - die Indizes decken alle anderen Spalten ab, Auflisten und Suchen lesen keine Inhalte.
CREATE INDEX files_meta ON files(parent, path, is_dir, size, etag, mime, created, modified, fileid);
CREATE INDEX files_meta_path ON files(path, parent, is_dir, size, etag, mime, created, modified, fileid);
CREATE TABLE file_favorites (fileid INTEGER PRIMARY KEY, added TEXT NOT NULL);
CREATE TABLE file_recent (fileid INTEGER PRIMARY KEY, used TEXT NOT NULL);
CREATE TABLE file_links (
    id        TEXT    PRIMARY KEY,
    token     TEXT    NOT NULL,
    fileid    INTEGER NOT NULL,
    mode      TEXT    NOT NULL,
    expires   TEXT    NULL,
    password  TEXT    NULL,
    created   TEXT    NOT NULL,
    downloads INTEGER NOT NULL DEFAULT 0
)")),
        // Volltextsuche in Anhaengen und Dateien (Search/TextIndex.cs). Version 13 ist die Dateiablage
        // (Papierkorb, Versionen) aus dem parallel entstandenen Zweig - unabhaengig voneinander.
        new(14, "Volltextsuche", c => Run(c, Search.TextIndex.Schema)),
        // Freigabelinks: hoechstens so viele Abrufe, dann laeuft der Link ab (NULL: unbegrenzt)
        new(15, "Abrufgrenze der Freigabelinks", c => Run(c, "ALTER TABLE file_links ADD COLUMN max_downloads INTEGER NULL")),
        // Dateiinhalte raus aus der Datenbank (BlobStore): Spalte blob verweist auf die verschluesselte Datei.
        // Die alten Inhalte (data) lagert FileStore.MigrateBlobs im Hintergrund schrittweise aus - hier nur Spalten.
        // Wird ein Verweis entfernt oder ersetzt, merkt blob_gone ihn vor; FileStore.CollectBlobs loescht die Datei,
        // sobald nichts mehr darauf zeigt (Versionen, Papierkorb und Kopien teilen sich Blobs).
        new(16, "Dateiinhalte als eigene Dateien", c =>
        {
            var sql = new System.Text.StringBuilder("CREATE TABLE blob_gone (id TEXT PRIMARY KEY);\n");
            foreach (var table in BlobTables)
                sql.Append($@"ALTER TABLE {table} ADD COLUMN blob TEXT NULL;
CREATE INDEX {table}_blob ON {table}(blob) WHERE blob IS NOT NULL;
CREATE TRIGGER {table}_blob_del AFTER DELETE ON {table} WHEN OLD.blob IS NOT NULL BEGIN INSERT OR IGNORE INTO blob_gone(id) VALUES(OLD.blob); END;
CREATE TRIGGER {table}_blob_upd AFTER UPDATE OF blob ON {table} WHEN OLD.blob IS NOT NULL AND OLD.blob IS NOT NEW.blob BEGIN INSERT OR IGNORE INTO blob_gone(id) VALUES(OLD.blob); END;
");
            Run(c, sql.ToString());
        }),
        // Schlagwoerter (Tags) der Notizen aus dem notizi-Abgleich, als JSON-Liste. NULL: noch nicht bekannt
        // (die Notiz kam von einem Geraet vor den Tags) - Rezepte tragen "recipe" (Essensplan).
        new(17, "Schlagwörter der Notizen", c => Run(c, "ALTER TABLE notes ADD COLUMN tags TEXT NULL")),
        // Pruefsumme (SHA-256 des Klartexts, hex) je Dateiinhalt: beim Schreiben mitgerechnet, fuer Vorhandenes
        // rechnet FileHashes im Hintergrund nach (NULL: noch unbekannt). Wandert mit Version, Papierkorb und Kopie.
        // Der Index dient "Ist dieser Inhalt schon da?" (App, POST /api/files/hashes) und spaeter Duplikaten.
        new(18, "Prüfsummen der Dateien", c =>
        {
            var sql = new System.Text.StringBuilder();
            foreach (var table in HashTables)
                sql.Append($"ALTER TABLE {table} ADD COLUMN sha256 TEXT NULL;\n");
            Run(c, sql + "CREATE INDEX files_sha256 ON files(sha256) WHERE sha256 IS NOT NULL");
        }),
    ];

    /// <summary>Die Schemaversion, auf die dieser Stand die Datenbank bringt.</summary>
    public static int LatestVersion => Steps[^1].Version;

    /// <summary>Nur fuer Tests, je Benutzer-Id: wird in jedem Schritt vor dem Festschreiben aufgerufen (mit der Versionsnummer);
    /// eine Ausnahme bricht den Schritt ab wie ein Absturz.</summary>
    public static System.Collections.Concurrent.ConcurrentDictionary<string, Action<int>> StepHooks { get; } = new();

    private void Migrate()
    {
        using var conn = Open();
        var version = Convert.ToInt32(Query(conn, "PRAGMA user_version", r => r.GetInt64(0)).First());
        var todo = Steps.Where(s => s.Version > version).ToList();
        if (todo.Count == 0) return;
        using var activity = Activity.Start(UserId, Activity.Migration, total: todo.Count);
        try
        {
            foreach (var step in todo)
            {
                activity.Progress(step.Version - todo[0].Version, detail: step.Title);
                // Jeder Schritt fuer sich, samt user_version: entweder ganz oder gar nicht
                using var tx = conn.BeginTransaction();
                step.Run(conn);
                Exec(conn, "PRAGMA user_version=" + step.Version);
                if (StepHooks.TryGetValue(UserId, out var hook)) hook(step.Version);
                tx.Commit();
            }
        }
        catch (Exception ex) { activity.Fail(ex.Message); throw; }
        Exec(conn, "PRAGMA wal_checkpoint(TRUNCATE)");
    }

    /// <summary>Verbindungsstring mit dem rohen Schluessel (SQLCipher "x'…'").</summary>
    public static string ConnectionStringOf(string path, byte[] key) => new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Password = "x'" + Convert.ToHexString(key) + "'",
        DefaultTimeout = 30
    }.ToString();

    /// <summary>Stand einer vorhandenen Datenbank, ohne sie zu veraendern oder zu migrieren.</summary>
    public sealed record Inspection(int Version, long FileBytes, long WalBytes, long MailRawBytes, long FileDataBytes, long ContentBytes);

    /// <summary>
    /// Liest Version und Groessen einer Datenbank, ohne zu migrieren (fuer die Vorabpruefung): wie gross die Datei ist,
    /// wie viel Mailinhalt (vor Schritt 3) und Dateiinhalt (vor Schritt 11) die Schritte umschreiben muessen und wie viel
    /// Dateiinhalt noch in der Datenbank liegt und in den BlobStore ausgelagert werden muss.
    /// </summary>
    public static Inspection Inspect(string path, byte[] key)
    {
        long Size(string f) => File.Exists(f) ? new FileInfo(f).Length : 0;
        using var conn = new SqliteConnection(ConnectionStringOf(path, key));
        conn.Open();
        Exec(conn, "PRAGMA busy_timeout=30000");
        var version = Convert.ToInt32(Query(conn, "PRAGMA user_version", r => r.GetInt64(0)).First());
        bool Has(string table, string? column = null) =>
            Query(conn, "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$t", r => 1, ("$t", table)).Count > 0
            && (column == null || Query(conn, $"PRAGMA table_info({table})", r => r.GetString(1)).Contains(column));
        long Sum(string sql) => Query(conn, sql, r => r.IsDBNull(0) ? 0L : r.GetInt64(0)).FirstOrDefault();

        long mailRaw = version < 3 && Has("mail", "raw") ? Sum("SELECT SUM(length(raw)) FROM mail") : 0;
        long fileData = version < 11 && Has("files", "size") ? Sum("SELECT SUM(size) FROM files WHERE is_dir=0") : 0;
        // Was noch in der Datenbank statt im BlobStore liegt (data), egal in welcher Tabelle
        long content = 0;
        foreach (var table in BlobTables)
            if (Has(table, "data"))
                content += Sum(table == "file_thumbs" ? $"SELECT SUM(length(data)) FROM {table} WHERE length(data)>0"
                    : $"SELECT SUM(length(data)) FROM {table} WHERE data IS NOT NULL");
        return new Inspection(version, Size(path), Size(path + "-wal"), mailRaw, fileData, content);
    }

    /// <summary>Tabellen mit Pruefsumme des Inhalts (Spalte sha256).</summary>
    public static readonly string[] HashTables = ["files", "file_versions", "file_trash_items"];

    /// <summary>Tabellen mit Dateiinhalten - frueher in data, jetzt als Verweis in blob.</summary>
    public static readonly string[] BlobTables = ["files", "file_versions", "file_trash_items", "file_thumbs"];

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
