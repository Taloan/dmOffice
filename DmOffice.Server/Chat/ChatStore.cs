using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DmOffice.Core;
using Microsoft.Data.Sqlite;

namespace DmOffice.Chat;

/// <summary>
/// Verweis in einer Nachricht: eine Datei oder Notiz bleibt beim Besitzer, der Chat zeigt sie nur.
/// Name, Typ und Groesse sind der Stand beim Senden - fuer die Anzeige, auch wenn das Original fehlt.
/// </summary>
/// <param name="Mode">show (Zeigen: nur ansehen) | send (Senden: darf eine Kopie speichern) | share (Teilen, ab Stufe 3).
/// Aeltere Verweise ohne Art verhalten sich wie send.</param>
/// <param name="Expires">Zeigen mit Sichtbarkeitsdauer: danach abgelaufen (das Original bleibt).</param>
/// <param name="Revoked">vom Besitzer zurueckgezogen.</param>
/// <param name="RootMessage">Weitergeleitet: Nachricht mit dem urspruenglichen Verweis - zieht der Besitzer dort zurueck,
/// ist es auch hier weg.</param>
/// <param name="Collection">Termin, Aufgabe, Kontakt: Sammlung (Kalender, Liste, Adressbuch) beim Besitzer.</param>
/// <param name="Item">Termin, Aufgabe, Kontakt: Kennung des Objekts in der Sammlung (Uri).</param>
public sealed record ChatRef(string Type, string Owner, string Name, string? Mime = null, long? Size = null, long? FileId = null, string? Note = null,
    string? Mode = null, DateTime? Expires = null, bool Revoked = false, long? RootMessage = null, int? RootIndex = null,
    long? Collection = null, string? Item = null, double? Lat = null, double? Lon = null, double? Accuracy = null,
    DateTime? LiveUntil = null, DateTime? At = null)
{
    public const string TypeFile = "file";
    public const string TypeNote = "note";
    public const string TypeFolder = "folder";
    /// <summary>Album der Galerie (Version 2), FileId = Album; nur Zeigen.</summary>
    public const string TypeAlbum = "album";
    public const string TypeEvent = "event";
    public const string TypeTask = "task";
    public const string TypeContact = "contact";
    /// <summary>Standort (Stufe 7): Lat/Lon/Accuracy (Meter), Name "Standort". Live-Teilen: LiveUntil (hoechstens 30 Minuten),
    /// At = Zeit der letzten Position; Aktualisierungen ersetzen die Position im selben Verweis.</summary>
    public const string TypeLocation = "location";
    public const string Show = "show";
    public const string Send = "send";
    public const string Share = "share";

    [JsonIgnore] public string EffectiveMode => string.IsNullOrEmpty(Mode) ? Send : Mode;
}

/// <summary>Was die Verwaltung einem Benutzer im Chat erlaubt.</summary>
public sealed record ChatRights(string User, bool Expiring = true, bool LocationRequests = false);

/// <summary>Gruppenraum (Stufe 4): ein Kalender, eine Liste, ein Ordner oder Notizordner beim Besitzer, geteilt mit der Gruppe.
/// Ref: Sammlungs-Id (calendar, list), fileid des Ordners (folder) oder sync_id des Notizordners (notes).</summary>
public sealed record ChatSpace(long Room, string Kind, string Owner, string Ref, DateTime Created)
{
    public const string Calendar = "calendar", List = "list", Folder = "folder", Notes = "notes";
    public static readonly string[] Kinds = [Calendar, List, Folder, Notes];
}

public sealed record ChatReaction(string Emoji, List<string> Users);

/// <summary>Knopf unter einer Nachricht (Pukki: Rückgängig, Auswahl). Data bleibt auf dem Server.</summary>
public sealed record ChatAction(string Key, string Label, string? Data = null);

public sealed record ChatMessage(long Id, long Room, string Sender, DateTime Created, DateTime? Edited, bool Deleted, string Kind,
    string? Text, long? ReplyTo, List<ChatRef> Refs, List<ChatReaction> Reactions, long Seq, List<string>? Mentions = null, List<ChatAction>? Actions = null,
    /// <summary>Pukkis Stimmung zur Nachricht (Pose aus den Bögen: frage, daumen, checkliste …), null: keine besondere.</summary>
    string? Mood = null);

/// <param name="Color">persoenliche Farbe der Unterhaltung (nur beim eigenen Eintrag sinnvoll), null: Akzentfarbe.</param>
/// <param name="Delivered">bis zu dieser Nachricht hat ein Gerät des Mitglieds schon abgeholt (Zustellung)</param>
public sealed record ChatMember(string Id, bool Admin, long LastRead, bool Muted, string? Color = null, long Delivered = 0);

public sealed record ChatRoom(long Id, string Kind, string? Title, DateTime Created, string CreatedBy, long Seq, List<ChatMember> Members,
    ChatMessage? Last, int Unread, int Mentioned = 0);

/// <summary>Neue Nachricht - fuer den Push an die Handys der Empfaenger.</summary>
public sealed record ChatPosted(long Room, long Message, string Sender, IReadOnlyList<string> Recipients, IReadOnlyList<string>? Mentioned = null);

/// <summary>
/// Der Chat: Unterhaltungen zu zweit und in Gruppen, gespeichert in einer eigenen SQLCipher-Datenbank
/// (data/chat.db) - nicht in den Benutzerdatenbanken, denn eine Nachricht gehoert allen in der Unterhaltung.
/// Der Schluessel liegt mit dem Hauptschluessel eingepackt in data/keys/chat.key.
///
/// Jede Aenderung zaehlt einen gemeinsamen Zaehler (seq) hoch und stempelt damit Nachricht und Unterhaltung.
/// Geraete holen mit <see cref="Changes"/> alles seit ihrem letzten Stand und warten mit <see cref="WaitAsync"/>
/// auf Neues (Long-Poll - geht auch durch den verschluesselten Umschlag).
/// </summary>
public sealed class ChatStore
{
    public const string Direct = "direct";
    public const string Group = "group";
    public const string KindText = "text";
    public const string KindSystem = "system";
    public const int MaxText = 10_000;
    public const int MaxTitle = 100;

    private readonly string _connectionString;
    private readonly object _write = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _waiters = new();
    // Wer gerade an einer Antwort arbeitet (Pukki fragt die KI): je Benutzer die Unterhaltungen - nur im Speicher,
    // mit Zaehler, damit der Long-Poll eine Aenderung auch zwischen zwei Abfragen nicht verpasst
    private readonly ConcurrentDictionary<string, (long Version, long[] Rooms)> _busy = new();

    /// <summary>Eine neue Nachricht ist da (nach dem Speichern, ausserhalb der Sperre).</summary>
    public event Action<ChatPosted>? Posted;

    static ChatStore() => SQLitePCL.Batteries_V2.Init();

    public ChatStore(DataPaths paths, Vault vault) : this(Path.Combine(paths.Root, "chat.db"), LoadKey(paths, vault)) { }

    /// <summary>Fuer Tests: Datei und Schluessel direkt.</summary>
    public ChatStore(string path, byte[] key)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Password = "x'" + Convert.ToHexString(key) + "'",
            DefaultTimeout = 30
        }.ToString();
        Migrate();
    }

    /// <summary>Schluessel der Chat-Datenbank: beim ersten Start zufaellig, eingepackt mit dem Hauptschluessel.
    /// Wurde der Hauptschluessel gewechselt (Variable statt Datei), mit dem frueheren auspacken und neu einpacken.</summary>
    private static byte[] LoadKey(DataPaths paths, Vault vault)
    {
        Directory.CreateDirectory(paths.Keys);
        var file = Path.Combine(paths.Keys, "chat.key");
        if (!File.Exists(file))
        {
            var fresh = RandomNumberGenerator.GetBytes(32);
            File.WriteAllText(file, vault.Wrap(fresh));
            return fresh;
        }
        var wrapped = File.ReadAllText(file).Trim();
        try { return vault.Unwrap(wrapped); }
        catch (CryptographicException) when (vault.Previous != null)
        {
            var key = Vault.Unwrap(vault.Previous, wrapped);
            File.WriteAllText(file, vault.Wrap(key));
            return key;
        }
    }

    /// <summary>Konsistente, gleich verschluesselte Kopie (S3-Sicherung).</summary>
    public void SnapshotTo(string file) => S3Backup.Vacuum(_connectionString, file);
    internal string ConnectionString => _connectionString;

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=30000; PRAGMA synchronous=NORMAL;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    private void Migrate()
    {
        using var conn = Open();
        var version = Convert.ToInt32(UserDb.Query(conn, "PRAGMA user_version", r => r.GetInt64(0)).First());
        if (version < 1)
        {
            UserDb.Exec(conn, Schema1);
            UserDb.Exec(conn, "PRAGMA user_version=1");
        }
        if (version < 2)
        {
            // Stufe 2: Farbe je Unterhaltung (persoenlich) und Chat-Rechte je Benutzer (Verwaltung)
            UserDb.Exec(conn, "ALTER TABLE members ADD COLUMN color TEXT; CREATE TABLE rights (user TEXT PRIMARY KEY, expiring INTEGER NOT NULL DEFAULT 1, location INTEGER NOT NULL DEFAULT 0);");
            UserDb.Exec(conn, "PRAGMA user_version=2");
        }
        if (version < 3)
        {
            // Stufe 4: Gruppenraeume - je Gruppe hoechstens ein Raum je Art
            UserDb.Exec(conn, "CREATE TABLE spaces (room INTEGER NOT NULL, kind TEXT NOT NULL, owner TEXT NOT NULL, ref TEXT NOT NULL, created TEXT NOT NULL, PRIMARY KEY(room, kind));");
            UserDb.Exec(conn, "PRAGMA user_version=3");
        }
        if (version < 4)
        {
            // @Erwaehnungen (Chat-Komfort): erwaehnte Mitglieder als ",id,id," - so findet instr(mentions, ",id,") sie
            UserDb.Exec(conn, "ALTER TABLE messages ADD COLUMN mentions TEXT;");
            UserDb.Exec(conn, "PRAGMA user_version=4");
        }
        if (version < 5)
        {
            // Knöpfe unter Pukkis Antworten (Rückgängig, Auswahl) - als JSON
            UserDb.Exec(conn, "ALTER TABLE messages ADD COLUMN actions TEXT;");
            UserDb.Exec(conn, "PRAGMA user_version=5");
        }
        if (version < 6)
        {
            // Zustellung: bis zu welcher Nachricht ein Gerät des Mitglieds schon abgeholt hat
            UserDb.Exec(conn, "ALTER TABLE members ADD COLUMN delivered INTEGER NOT NULL DEFAULT 0;");
            UserDb.Exec(conn, "PRAGMA user_version=6");
        }
        if (version < 7)
        {
            // Pukkis Stimmung je Nachricht (Pose in Web und App)
            UserDb.Exec(conn, "ALTER TABLE messages ADD COLUMN mood TEXT;");
            UserDb.Exec(conn, "PRAGMA user_version=7");
        }
    }

    // pair: bei Unterhaltungen zu zweit "a|b" (sortiert) - je Paar nur eine.
    // members.since: erste Nachricht, die ein Mitglied sieht - wer spaeter dazukommt, liest den Verlauf davor nicht.
    // user_seq: Zaehlerstand je Benutzer, wenn er aus einer Unterhaltung geht (die taucht dann in keiner seiner Abfragen mehr auf).
    private const string Schema1 = @"
CREATE TABLE kv (key TEXT PRIMARY KEY, value TEXT);
INSERT INTO kv(key, value) VALUES('seq', '0');
CREATE TABLE rooms (
    id INTEGER PRIMARY KEY,
    kind TEXT NOT NULL,
    title TEXT,
    pair TEXT UNIQUE,
    created TEXT NOT NULL,
    created_by TEXT NOT NULL,
    seq INTEGER NOT NULL,
    last_message INTEGER
);
CREATE TABLE members (
    room INTEGER NOT NULL REFERENCES rooms(id) ON DELETE CASCADE,
    user TEXT NOT NULL,
    admin INTEGER NOT NULL DEFAULT 0,
    joined TEXT NOT NULL,
    since INTEGER NOT NULL DEFAULT 0,
    last_read INTEGER NOT NULL DEFAULT 0,
    muted INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY(room, user)
);
CREATE INDEX members_user ON members(user);
CREATE TABLE messages (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    room INTEGER NOT NULL REFERENCES rooms(id) ON DELETE CASCADE,
    sender TEXT NOT NULL,
    created TEXT NOT NULL,
    edited TEXT,
    deleted INTEGER NOT NULL DEFAULT 0,
    kind TEXT NOT NULL DEFAULT 'text',
    text TEXT,
    reply_to INTEGER,
    refs TEXT,
    seq INTEGER NOT NULL
);
CREATE INDEX messages_room ON messages(room, id);
CREATE INDEX messages_seq ON messages(seq);
CREATE TABLE reactions (
    message INTEGER NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
    user TEXT NOT NULL,
    emoji TEXT NOT NULL,
    PRIMARY KEY(message, user, emoji)
);
CREATE TABLE user_seq (user TEXT PRIMARY KEY, seq INTEGER NOT NULL);
";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private static string Now() => DateTime.UtcNow.ToString("o");
    private static DateTime Time(string s) => DateTime.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind);

    private static long NextSeq(SqliteConnection conn)
    {
        UserDb.Exec(conn, "UPDATE kv SET value = CAST(value AS INTEGER) + 1 WHERE key='seq'");
        return Convert.ToInt64(UserDb.Query(conn, "SELECT value FROM kv WHERE key='seq'", r => r.GetString(0)).First());
    }

    /// <summary>Schreiben unter einer Sperre in einer Transaktion; danach die Wartenden der betroffenen Benutzer wecken.</summary>
    private T Write<T>(Func<SqliteConnection, (T Result, IEnumerable<string> Wake)> work)
    {
        T result;
        List<string> wake;
        lock (_write)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            var (r, w) = work(conn);
            tx.Commit();
            result = r;
            wake = w.Distinct().ToList();
        }
        foreach (var u in wake)
            if (_waiters.TryRemove(u, out var tcs)) tcs.TrySetResult();
        return result;
    }

    private void Run(Func<SqliteConnection, IEnumerable<string>> work) => Write(conn => (0, work(conn)));

    private static List<string> MemberIds(SqliteConnection conn, long room) =>
        UserDb.Query(conn, "SELECT user FROM members WHERE room=$r", r => r.GetString(0), ("$r", room));

    private static void Touch(SqliteConnection conn, long room, long seq) =>
        UserDb.Exec(conn, "UPDATE rooms SET seq=$s WHERE id=$r", ("$s", seq), ("$r", room));

    private static ChatMember? Member(SqliteConnection conn, long room, string user) =>
        UserDb.Query(conn, "SELECT user, admin, last_read, muted, color, delivered FROM members WHERE room=$r AND user=$u",
            r => new ChatMember(r.GetString(0), r.GetInt64(1) != 0, r.GetInt64(2), r.GetInt64(3) != 0, r.IsDBNull(4) ? null : r.GetString(4), r.GetInt64(5)), ("$r", room), ("$u", user)).FirstOrDefault();

    private static ChatMember RequireMember(SqliteConnection conn, long room, string user) =>
        Member(conn, room, user) ?? throw new KeyNotFoundException("Unterhaltung nicht gefunden.");

    private static string? RoomKind(SqliteConnection conn, long room) =>
        UserDb.Query(conn, "SELECT kind FROM rooms WHERE id=$r", r => r.GetString(0), ("$r", room)).FirstOrDefault();

    /// <summary>Hinweis im Verlauf (angelegt, hinzugefuegt, gegangen, umbenannt) - die Oberflaeche setzt den Text in ihrer Sprache.</summary>
    private static long AddSystem(SqliteConnection conn, long room, string by, long seq, object payload)
    {
        UserDb.Exec(conn, "INSERT INTO messages(room, sender, created, kind, text, seq) VALUES($r,$s,$c,'system',$t,$q)",
            ("$r", room), ("$s", by), ("$c", Now()), ("$t", JsonSerializer.Serialize(payload, Json)), ("$q", seq));
        var id = Convert.ToInt64(UserDb.Query(conn, "SELECT last_insert_rowid()", r => r.GetInt64(0)).First());
        UserDb.Exec(conn, "UPDATE rooms SET last_message=$m, seq=$s WHERE id=$r", ("$m", id), ("$s", seq), ("$r", room));
        return id;
    }

    private static long LastMessageId(SqliteConnection conn, long room) =>
        Convert.ToInt64(UserDb.Query(conn, "SELECT COALESCE(MAX(id),0) FROM messages WHERE room=$r", r => r.GetInt64(0), ("$r", room)).First());

    private static void BumpUser(SqliteConnection conn, string user, long seq) =>
        UserDb.Exec(conn, "INSERT INTO user_seq(user, seq) VALUES($u,$s) ON CONFLICT(user) DO UPDATE SET seq=excluded.seq", ("$u", user), ("$s", seq));

    // ---------------- Unterhaltungen ----------------

    /// <summary>Die Unterhaltung zu zweit - gibt es sie schon, dieselbe (wer sie verlassen hatte, ist wieder dabei).</summary>
    public long OpenDirect(string me, string other)
    {
        if (me == other) throw new ArgumentException("Eine Unterhaltung mit sich selbst geht nicht.");
        var pair = string.CompareOrdinal(me, other) < 0 ? me + "|" + other : other + "|" + me;
        return Write(conn =>
        {
            var id = UserDb.Query(conn, "SELECT id FROM rooms WHERE pair=$p", r => r.GetInt64(0), ("$p", pair)).FirstOrDefault();
            var seq = NextSeq(conn);
            if (id == 0)
            {
                UserDb.Exec(conn, "INSERT INTO rooms(kind, pair, created, created_by, seq) VALUES('direct',$p,$c,$u,$s)",
                    ("$p", pair), ("$c", Now()), ("$u", me), ("$s", seq));
                id = Convert.ToInt64(UserDb.Query(conn, "SELECT last_insert_rowid()", r => r.GetInt64(0)).First());
            }
            // Wer gegangen war, sieht den Verlauf wieder - es ist seine eigene Unterhaltung zu zweit
            foreach (var u in new[] { me, other })
                UserDb.Exec(conn, "INSERT OR IGNORE INTO members(room, user, joined) VALUES($r,$u,$j)", ("$r", id), ("$u", u), ("$j", Now()));
            Touch(conn, id, seq);
            return (id, new[] { me, other });
        });
    }

    /// <summary>Neue Gruppe; wer sie anlegt, verwaltet sie.</summary>
    public long CreateGroup(string me, string title, IEnumerable<string> members)
    {
        title = CheckTitle(title);
        var all = members.Where(m => m != me).Distinct().ToList();
        return Write(conn =>
        {
            var seq = NextSeq(conn);
            UserDb.Exec(conn, "INSERT INTO rooms(kind, title, created, created_by, seq) VALUES('group',$t,$c,$u,$s)",
                ("$t", title), ("$c", Now()), ("$u", me), ("$s", seq));
            var id = Convert.ToInt64(UserDb.Query(conn, "SELECT last_insert_rowid()", r => r.GetInt64(0)).First());
            UserDb.Exec(conn, "INSERT INTO members(room, user, admin, joined) VALUES($r,$u,1,$j)", ("$r", id), ("$u", me), ("$j", Now()));
            foreach (var m in all)
                UserDb.Exec(conn, "INSERT INTO members(room, user, joined) VALUES($r,$u,$j)", ("$r", id), ("$u", m), ("$j", Now()));
            AddSystem(conn, id, me, seq, new { ev = "created", title });
            return (id, all.Append(me));
        });
    }

    private static string CheckTitle(string? title)
    {
        title = (title ?? "").Trim();
        if (title.Length == 0) throw new ArgumentException("Die Gruppe braucht einen Namen.");
        if (title.Length > MaxTitle) throw new ArgumentException($"Der Name ist zu lang (höchstens {MaxTitle} Zeichen).");
        return title;
    }

    private static void RequireGroupAdmin(SqliteConnection conn, long room, string me)
    {
        var m = RequireMember(conn, room, me);
        if (RoomKind(conn, room) != Group) throw new InvalidOperationException("Nur in Gruppen.");
        if (!m.Admin) throw new UnauthorizedAccessException("Nur wer die Gruppe verwaltet, darf das.");
    }

    public void Rename(string me, long room, string title)
    {
        title = CheckTitle(title);
        Run(conn =>
        {
            RequireGroupAdmin(conn, room, me);
            var seq = NextSeq(conn);
            UserDb.Exec(conn, "UPDATE rooms SET title=$t WHERE id=$r", ("$t", title), ("$r", room));
            AddSystem(conn, room, me, seq, new { ev = "renamed", title });
            return MemberIds(conn, room);
        });
    }

    /// <summary>Mitglieder dazu; sie sehen den Verlauf ab jetzt.</summary>
    public void AddMembers(string me, long room, IEnumerable<string> users)
    {
        Run(conn =>
        {
            RequireGroupAdmin(conn, room, me);
            var since = LastMessageId(conn, room);
            var added = new List<string>();
            foreach (var u in users.Distinct())
                if (UserDb.Exec(conn, "INSERT OR IGNORE INTO members(room, user, joined, since, last_read) VALUES($r,$u,$j,$s,$s)",
                        ("$r", room), ("$u", u), ("$j", Now()), ("$s", since)) > 0) added.Add(u);
            if (added.Count == 0) return Array.Empty<string>();
            var seq = NextSeq(conn);
            AddSystem(conn, room, me, seq, new { ev = "added", users = added });
            return MemberIds(conn, room);
        });
    }

    /// <summary>Mitglied entfernen (wer die Gruppe verwaltet) oder selbst gehen.</summary>
    public void RemoveMember(string me, long room, string user)
    {
        Run(conn =>
        {
            if (user != me) RequireGroupAdmin(conn, room, me);
            else RequireMember(conn, room, me);
            return LeaveIn(conn, room, user, me);
        });
    }

    /// <summary>Gehen: aus Gruppen mit Hinweis; bleibt niemand, ist die Unterhaltung weg. Geht der letzte Verwalter,
    /// verwaltet das am laengsten dabei gebliebene Mitglied. Zurueck: wer davon erfahren soll.</summary>
    private static List<string> LeaveIn(SqliteConnection conn, long room, string user, string by)
    {
        var before = MemberIds(conn, room);
        if (!before.Contains(user)) return new();
        var seq = NextSeq(conn);
        UserDb.Exec(conn, "DELETE FROM members WHERE room=$r AND user=$u", ("$r", room), ("$u", user));
        BumpUser(conn, user, seq);
        var rest = before.Where(u => u != user).ToList();
        if (rest.Count == 0)
        {
            UserDb.Exec(conn, "DELETE FROM rooms WHERE id=$r", ("$r", room));
            return before;
        }
        if (RoomKind(conn, room) == Group)
        {
            AddSystem(conn, room, by, seq, user == by ? new { ev = "left", users = new[] { user } } : new { ev = "removed", users = new[] { user } });
            if (Convert.ToInt64(UserDb.Query(conn, "SELECT COUNT(*) FROM members WHERE room=$r AND admin=1", r => r.GetInt64(0), ("$r", room)).First()) == 0)
                UserDb.Exec(conn, "UPDATE members SET admin=1 WHERE room=$r AND user=(SELECT user FROM members WHERE room=$r ORDER BY joined LIMIT 1)", ("$r", room));
        }
        else Touch(conn, room, seq);
        return before;
    }

    /// <summary>Verwalten weitergeben oder abnehmen.</summary>
    public void SetAdmin(string me, long room, string user, bool admin)
    {
        Run(conn =>
        {
            RequireGroupAdmin(conn, room, me);
            RequireMember(conn, room, user);
            if (!admin && Convert.ToInt64(UserDb.Query(conn, "SELECT COUNT(*) FROM members WHERE room=$r AND admin=1 AND user<>$u",
                    r => r.GetInt64(0), ("$r", room), ("$u", user)).First()) == 0)
                throw new InvalidOperationException("Mindestens eine Person muss die Gruppe verwalten.");
            UserDb.Exec(conn, "UPDATE members SET admin=$a WHERE room=$r AND user=$u", ("$a", admin ? 1 : 0), ("$r", room), ("$u", user));
            Touch(conn, room, NextSeq(conn));
            return MemberIds(conn, room);
        });
    }

    public void Mute(string me, long room, bool muted)
    {
        Run(conn =>
        {
            RequireMember(conn, room, me);
            UserDb.Exec(conn, "UPDATE members SET muted=$m WHERE room=$r AND user=$u", ("$m", muted ? 1 : 0), ("$r", room), ("$u", me));
            var seq = NextSeq(conn);
            BumpUser(conn, me, seq);
            return new[] { me };
        });
    }

    /// <summary>Gelesen bis einschliesslich dieser Nachricht (nie zurueck). Die anderen sehen es (gelesen-Haken).</summary>
    public void MarkRead(string me, long room, long upTo)
    {
        Run(conn =>
        {
            var m = RequireMember(conn, room, me);
            var last = LastMessageId(conn, room);
            upTo = Math.Min(upTo, last);
            if (upTo <= m.LastRead) return Array.Empty<string>();
            UserDb.Exec(conn, "UPDATE members SET last_read=$l WHERE room=$r AND user=$u", ("$l", upTo), ("$r", room), ("$u", me));
            Touch(conn, room, NextSeq(conn));
            return MemberIds(conn, room);
        });
    }

    /// <summary>Ein Benutzer wird geloescht: aus allen Unterhaltungen raus. Seine Nachrichten bleiben, ohne Namen.</summary>
    public void Forget(string user)
    {
        Run(conn =>
        {
            var wake = new List<string>();
            foreach (var room in UserDb.Query(conn, "SELECT room FROM members WHERE user=$u", r => r.GetInt64(0), ("$u", user)))
                wake.AddRange(LeaveIn(conn, room, user, user));
            UserDb.Exec(conn, "DELETE FROM user_seq WHERE user=$u", ("$u", user));
            return wake.Where(u => u != user);
        });
    }

    // ---------------- Nachrichten ----------------

    public long Post(string me, long room, string? text, long? replyTo = null, IReadOnlyList<ChatRef>? refs = null, IReadOnlyList<string>? mentions = null,
        IReadOnlyList<ChatAction>? actions = null, string? mood = null)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) text = null;
        refs ??= Array.Empty<ChatRef>();
        if (text == null && refs.Count == 0) throw new ArgumentException("Die Nachricht ist leer.");
        if (text?.Length > MaxText) throw new ArgumentException($"Die Nachricht ist zu lang (höchstens {MaxText} Zeichen).");
        if (refs.Count > 20) throw new ArgumentException("Höchstens 20 Anhänge je Nachricht.");
        // Pukki: Stimmung aus Text, Anhängen und Knöpfen, wenn der Aufrufer keine nennt
        if (me == Pukki.Id) mood ??= Pukki.MoodOf(text, refs, actions);
        else mood = null;
        List<string> recipients = new();
        string? mentioned = null;
        var id = Write(conn =>
        {
            // Pukki (Stufe 5) ist kein Mitglied von Gruppen und schreibt doch hinein (Antwort auf „@Pukki“)
            if (me != Pukki.Id) RequireMember(conn, room, me);
            if (replyTo is { } rt && UserDb.Query(conn, "SELECT 1 FROM messages WHERE id=$i AND room=$r AND id>$s",
                    r => 1, ("$i", rt), ("$r", room), ("$s", SinceOf(conn, room, me))).Count == 0)
                throw new ArgumentException("Die zitierte Nachricht gibt es nicht.");
            var all = MemberIds(conn, room);
            mentioned = MentionList(mentions, all, me);
            var seq = NextSeq(conn);
            UserDb.Exec(conn, "INSERT INTO messages(room, sender, created, kind, text, reply_to, refs, seq, mentions, actions, mood) VALUES($r,$s,$c,'text',$t,$p,$f,$q,$n,$a,$o)",
                ("$r", room), ("$s", me), ("$c", Now()), ("$t", text), ("$p", replyTo),
                ("$f", refs.Count == 0 ? null : JsonSerializer.Serialize(refs, Json)), ("$q", seq), ("$n", mentioned),
                ("$a", actions is { Count: > 0 } ? JsonSerializer.Serialize(actions, Json) : null), ("$o", mood));
            var id = Convert.ToInt64(UserDb.Query(conn, "SELECT last_insert_rowid()", r => r.GetInt64(0)).First());
            UserDb.Exec(conn, "UPDATE rooms SET last_message=$m, seq=$s WHERE id=$r", ("$m", id), ("$s", seq), ("$r", room));
            // Eigene Nachricht ist gelesen
            UserDb.Exec(conn, "UPDATE members SET last_read=$m WHERE room=$r AND user=$u", ("$m", id), ("$r", room), ("$u", me));
            // Stumm geschaltet bekommt trotzdem Bescheid, wer erwaehnt ist
            recipients = UserDb.Query(conn, "SELECT user FROM members WHERE room=$r AND user<>$u AND (muted=0 OR instr($n, ','||user||',')>0)",
                r => r.GetString(0), ("$r", room), ("$u", me), ("$n", mentioned ?? ""));
            return (id, all);
        });
        Posted?.Invoke(new ChatPosted(room, id, me, recipients, mentioned?.Split(',', StringSplitOptions.RemoveEmptyEntries)));
        return id;
    }

    /// <summary>Knöpfe einer Nachricht ersetzen (null/leer: keine mehr) - etwa nach dem Klick.</summary>
    public void SetActions(long message, IReadOnlyList<ChatAction>? actions)
    {
        Run(conn =>
        {
            var room = UserDb.Query(conn, "SELECT room FROM messages WHERE id=$i", r => r.GetInt64(0), ("$i", message)).FirstOrDefault();
            if (room == 0) throw new KeyNotFoundException("Nachricht nicht gefunden.");
            var seq = NextSeq(conn);
            UserDb.Exec(conn, "UPDATE messages SET actions=$a, seq=$s WHERE id=$i",
                ("$a", actions is { Count: > 0 } ? JsonSerializer.Serialize(actions, Json) : null), ("$s", seq), ("$i", message));
            Touch(conn, room, seq);
            return MemberIds(conn, room);
        });
    }

    /// <summary>Knoepfe mit diesem Schluessel aus allen Nachrichten nehmen.</summary>
    public void ClearActionsWith(string key)
    {
        Run(conn =>
        {
            var wake = new List<string>();
            foreach (var (id, room) in UserDb.Query(conn, "SELECT id, room FROM messages WHERE actions LIKE $k", r => (r.GetInt64(0), r.GetInt64(1)), ("$k", "%\"" + key + "\"%")))
            {
                var seq = NextSeq(conn);
                UserDb.Exec(conn, "UPDATE messages SET actions=NULL, seq=$s WHERE id=$i", ("$s", seq), ("$i", id));
                Touch(conn, room, seq);
                wake.AddRange(MemberIds(conn, room));
            }
            return wake.Distinct();
        });
    }

    /// <summary>@Erwaehnungen: nur Mitglieder ausser mir (und Pukki, der antwortet ohnehin) - als ",id,id," bzw. null.</summary>
    private static string? MentionList(IReadOnlyList<string>? mentions, List<string> members, string me)
    {
        var ids = (mentions ?? []).Where(u => u != me && u != Pukki.Id && members.Contains(u)).Distinct().ToList();
        return ids.Count == 0 ? null : "," + string.Join(",", ids) + ",";
    }

    private static long SinceOf(SqliteConnection conn, long room, string user) =>
        UserDb.Query(conn, "SELECT since FROM members WHERE room=$r AND user=$u", r => r.GetInt64(0), ("$r", room), ("$u", user)).FirstOrDefault();

    /// <summary>Nachricht, die ich sehen darf (Mitglied, seit meinem Beitritt) - sonst KeyNotFound.</summary>
    private static (long Room, string Sender, string Kind, bool Deleted) Visible(SqliteConnection conn, long message, string me) =>
        UserDb.Query(conn, @"SELECT m.room, m.sender, m.kind, m.deleted FROM messages m JOIN members b ON b.room=m.room AND b.user=$u
                             WHERE m.id=$i AND m.id>b.since",
            r => (Room: r.GetInt64(0), Sender: r.GetString(1), Kind: r.GetString(2), Deleted: r.GetInt64(3) != 0), ("$i", message), ("$u", me)).FirstOrDefault() is { Room: > 0 } v
            ? v : throw new KeyNotFoundException("Nachricht nicht gefunden.");

    public void Edit(string me, long message, string text, IReadOnlyList<string>? mentions = null)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) throw new ArgumentException("Die Nachricht ist leer.");
        if (text.Length > MaxText) throw new ArgumentException($"Die Nachricht ist zu lang (höchstens {MaxText} Zeichen).");
        Run(conn =>
        {
            var v = Visible(conn, message, me);
            if (v.Sender != me || v.Kind != KindText) throw new UnauthorizedAccessException("Nur eigene Nachrichten lassen sich bearbeiten.");
            if (v.Deleted) throw new InvalidOperationException("Die Nachricht ist gelöscht.");
            RequireUnseen(conn, v.Room, message, me, "bearbeiten");
            var seq = NextSeq(conn);
            UserDb.Exec(conn, "UPDATE messages SET text=$t, edited=$e, seq=$s, mentions=$n WHERE id=$i", ("$t", text), ("$e", Now()), ("$s", seq),
                ("$n", MentionList(mentions, MemberIds(conn, v.Room), me)), ("$i", message));
            Touch(conn, v.Room, seq);
            return MemberIds(conn, v.Room);
        });
    }

    /// <summary>Fuer alle loeschen: eigene Nachricht oder (in Gruppen) als Verwalter. Es bleibt "geloescht" im Verlauf.</summary>
    public void Delete(string me, long message)
    {
        Run(conn =>
        {
            var v = Visible(conn, message, me);
            if (v.Kind != KindText) throw new InvalidOperationException("Hinweise lassen sich nicht löschen.");
            if (v.Sender != me && !(RoomKind(conn, v.Room) == Group && RequireMember(conn, v.Room, me).Admin))
                throw new UnauthorizedAccessException("Nur eigene Nachrichten lassen sich löschen.");
            if (v.Sender == me) RequireUnseen(conn, v.Room, message, me, "löschen");
            if (v.Deleted) return Array.Empty<string>();
            var seq = NextSeq(conn);
            UserDb.Exec(conn, "UPDATE messages SET deleted=1, text=NULL, refs=NULL, seq=$s WHERE id=$i", ("$s", seq), ("$i", message));
            UserDb.Exec(conn, "DELETE FROM reactions WHERE message=$i", ("$i", message));
            Touch(conn, v.Room, seq);
            return MemberIds(conn, v.Room);
        });
    }

    /// <summary>
    /// Eigene Nachricht ändern oder für alle löschen: in Unterhaltungen zu zweit nur, solange der andere sie nicht gelesen
    /// hat - hat er die Lesebestätigungen aus, nur bis zur Zustellung (sonst verriete das Probieren, ob er gelesen hat).
    /// In Gruppen jederzeit.
    /// </summary>
    private void RequireUnseen(SqliteConnection conn, long room, long message, string me, string what)
    {
        if (RoomKind(conn, room) != Direct) return;
        foreach (var (user, read, delivered) in UserDb.Query(conn, "SELECT user, last_read, delivered FROM members WHERE room=$r AND user<>$u",
                     r => (r.GetString(0), r.GetInt64(1), r.GetInt64(2)), ("$r", room), ("$u", me)))
        {
            if (user == Pukki.Id) continue;
            if (Receipts(conn, user) ? read >= message : delivered >= message)
                throw new InvalidOperationException(Receipts(conn, user)
                    ? $"Die Nachricht wurde schon gelesen – {what} geht nicht mehr."
                    : $"Die Nachricht ist schon zugestellt – {what} geht nicht mehr.");
        }
    }

    // ---------------- Zustellung und Lesebestätigungen ----------------

    /// <summary>Ein Gerät von <paramref name="me"/> hat Nachrichten abgeholt: je Unterhaltung bis zu welcher. Neuer Stand nur bei Fortschritt.</summary>
    public void MarkDelivered(string me, IEnumerable<(long Room, long UpTo)> fetched)
    {
        var list = fetched.Where(x => x.UpTo > 0).GroupBy(x => x.Room).Select(g => (Room: g.Key, UpTo: g.Max(x => x.UpTo))).ToList();
        if (list.Count == 0) return;
        Run(conn =>
        {
            var wake = new List<string>();
            foreach (var (room, upTo) in list)
            {
                if (UserDb.Exec(conn, "UPDATE members SET delivered=$d WHERE room=$r AND user=$u AND delivered<$d", ("$d", upTo), ("$r", room), ("$u", me)) == 0) continue;
                var seq = NextSeq(conn);
                Touch(conn, room, seq);
                wake.AddRange(MemberIds(conn, room));
            }
            return wake;
        });
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _receipts = new();

    /// <summary>Lesebestätigungen: dürfen andere sehen, wie weit jemand gelesen hat? (Vorgabe ja)</summary>
    public bool Receipts(string user)
    {
        if (_receipts.TryGetValue(user, out var on)) return on;
        using var conn = Open();
        return Receipts(conn, user);
    }

    private bool Receipts(SqliteConnection conn, string user) => _receipts.GetOrAdd(user, u =>
        UserDb.Query(conn, "SELECT value FROM kv WHERE key=$k", r => r.GetString(0), ("$k", "noreceipts:" + u)).FirstOrDefault() != "1");

    public void SetReceipts(string user, bool on)
    {
        Run(conn =>
        {
            if (on) UserDb.Exec(conn, "DELETE FROM kv WHERE key=$k", ("$k", "noreceipts:" + user));
            else UserDb.Exec(conn, "INSERT INTO kv(key, value) VALUES($k,'1') ON CONFLICT(key) DO UPDATE SET value='1'", ("$k", "noreceipts:" + user));
            _receipts[user] = on;
            // Die anderen sollen den geänderten Lesestand sehen
            var rooms = UserDb.Query(conn, "SELECT room FROM members WHERE user=$u", r => r.GetInt64(0), ("$u", user));
            var wake = new List<string>();
            foreach (var room in rooms) { Touch(conn, room, NextSeq(conn)); wake.AddRange(MemberIds(conn, room)); }
            return wake;
        });
    }

    /// <summary>Reaktion an oder aus (dasselbe Zeichen noch einmal nimmt sie zurueck).</summary>
    public void React(string me, long message, string emoji)
    {
        emoji = (emoji ?? "").Trim();
        if (emoji.Length == 0 || emoji.Length > 16) throw new ArgumentException("Ungültige Reaktion.");
        Run(conn =>
        {
            var v = Visible(conn, message, me);
            if (v.Deleted || v.Kind != KindText) throw new InvalidOperationException("Darauf lässt sich nicht reagieren.");
            if (UserDb.Exec(conn, "DELETE FROM reactions WHERE message=$i AND user=$u AND emoji=$e", ("$i", message), ("$u", me), ("$e", emoji)) == 0)
                UserDb.Exec(conn, "INSERT INTO reactions(message, user, emoji) VALUES($i,$u,$e)", ("$i", message), ("$u", me), ("$e", emoji));
            var seq = NextSeq(conn);
            UserDb.Exec(conn, "UPDATE messages SET seq=$s WHERE id=$i", ("$s", seq), ("$i", message));
            Touch(conn, v.Room, seq);
            return MemberIds(conn, v.Room);
        });
    }

    // ---------------- Lesen ----------------

    private const string MessageColumns = "m.id, m.room, m.sender, m.created, m.edited, m.deleted, m.kind, m.text, m.reply_to, m.refs, m.seq, m.mentions, m.actions, m.mood";

    private static ChatMessage ReadMessage(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1), r.GetString(2), Time(r.GetString(3)), r.IsDBNull(4) ? null : Time(r.GetString(4)),
        r.GetInt64(5) != 0, r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetInt64(8),
        r.IsDBNull(9) ? new() : JsonSerializer.Deserialize<List<ChatRef>>(r.GetString(9), Json) ?? new(), new(), r.GetInt64(10),
        r.IsDBNull(11) ? null : r.GetString(11).Split(',', StringSplitOptions.RemoveEmptyEntries).ToList(),
        r.IsDBNull(12) ? null : JsonSerializer.Deserialize<List<ChatAction>>(r.GetString(12), Json),
        r.IsDBNull(13) ? null : r.GetString(13));

    private static List<ChatMessage> WithReactions(SqliteConnection conn, List<ChatMessage> messages)
    {
        if (messages.Count == 0) return messages;
        var byMessage = new Dictionary<long, List<(string User, string Emoji)>>();
        foreach (var chunk in messages.Select(m => m.Id).Chunk(500))
            foreach (var (msg, user, emoji) in UserDb.Query(conn, $"SELECT message, user, emoji FROM reactions WHERE message IN ({string.Join(",", chunk)}) ORDER BY rowid",
                         r => (r.GetInt64(0), r.GetString(1), r.GetString(2))))
            {
                if (!byMessage.TryGetValue(msg, out var list)) byMessage[msg] = list = new();
                list.Add((user, emoji));
            }
        return messages.Select(m => byMessage.TryGetValue(m.Id, out var l)
            ? m with { Reactions = l.GroupBy(x => x.Emoji).Select(g => new ChatReaction(g.Key, g.Select(x => x.User).ToList())).ToList() }
            : m).ToList();
    }

    /// <summary>Eine Seite Verlauf, aelteste zuerst: die neuesten vor <paramref name="before"/> (ohne: die neuesten ueberhaupt).</summary>
    public List<ChatMessage> Messages(string me, long room, long? before = null, int limit = 50)
    {
        limit = Math.Clamp(limit, 1, 200);
        using var conn = Open();
        RequireMember(conn, room, me);
        var since = SinceOf(conn, room, me);
        var list = UserDb.Query(conn, $"SELECT {MessageColumns} FROM messages m WHERE m.room=$r AND m.id>$s AND m.id<$b ORDER BY m.id DESC LIMIT $l",
            ReadMessage, ("$r", room), ("$s", since), ("$b", before ?? long.MaxValue), ("$l", limit));
        list.Reverse();
        return WithReactions(conn, list);
    }

    /// <summary>
    /// Suche im Chat (Chat-Komfort): Nachrichten, die me sehen darf (ab seinem Beitritt), deren Text oder Anhangsnamen alle
    /// Suchwörter enthalten - Groß/klein und Umlaute egal; die neuesten zuerst. room: nur in dieser Unterhaltung.
    /// </summary>
    public List<ChatMessage> Search(string me, string query, long? room = null, int limit = 50)
    {
        var words = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 2).Select(Fold).ToList();
        if (words.Count == 0) return new();
        using var conn = Open();
        // Die letzten 20 000 sichtbaren Nachrichten reichen für eine Familie; gefiltert wird hier (SQLite kennt keine Umlaute)
        var candidates = UserDb.Query(conn, $"SELECT {MessageColumns} FROM messages m JOIN members b ON b.room=m.room AND b.user=$u " +
            "WHERE m.id>b.since AND m.deleted=0 AND m.kind='text' AND ($room IS NULL OR m.room=$room) ORDER BY m.id DESC LIMIT 20000",
            ReadMessage, ("$u", me), ("$room", room));
        var hits = candidates.Where(m =>
        {
            var hay = Fold(((m.Text ?? "") + " " + string.Join(' ', m.Refs.Select(r => r.Name))).ToLowerInvariant());
            return words.All(hay.Contains);
        }).Take(Math.Clamp(limit, 1, 200)).ToList();
        return WithReactions(conn, hits);
    }

    private static string Fold(string s) => s.Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Replace("ß", "ss");

    public ChatMessage Message(string me, long message)
    {
        using var conn = Open();
        Visible(conn, message, me);
        return WithReactions(conn, UserDb.Query(conn, $"SELECT {MessageColumns} FROM messages m WHERE m.id=$i", ReadMessage, ("$i", message))).Single();
    }

    /// <summary>Alle Unterhaltungen eines Benutzers, die mit der neuesten Nachricht zuerst.</summary>
    public List<ChatRoom> Rooms(string me)
    {
        using var conn = Open();
        var rooms = UserDb.Query(conn, @"SELECT r.id, r.kind, r.title, r.created, r.created_by, r.seq, r.last_message, b.since, b.last_read
                                         FROM rooms r JOIN members b ON b.room=r.id AND b.user=$u",
            r => (Id: r.GetInt64(0), Kind: r.GetString(1), Title: r.IsDBNull(2) ? null : r.GetString(2), Created: Time(r.GetString(3)),
                  By: r.GetString(4), Seq: r.GetInt64(5), Last: r.IsDBNull(6) ? 0 : r.GetInt64(6), Since: r.GetInt64(7), Read: r.GetInt64(8)), ("$u", me));
        var result = new List<ChatRoom>();
        foreach (var x in rooms)
        {
            var members = UserDb.Query(conn, "SELECT user, admin, last_read, muted, color, delivered FROM members WHERE room=$r ORDER BY joined",
                r => new ChatMember(r.GetString(0), r.GetInt64(1) != 0, r.GetInt64(2), r.GetInt64(3) != 0, r.IsDBNull(4) ? null : r.GetString(4), r.GetInt64(5)), ("$r", x.Id));
            var last = x.Last > x.Since
                ? WithReactions(conn, UserDb.Query(conn, $"SELECT {MessageColumns} FROM messages m WHERE m.id=$i", ReadMessage, ("$i", x.Last))).FirstOrDefault()
                : null;
            var unread = Convert.ToInt32(UserDb.Query(conn, "SELECT COUNT(*) FROM messages WHERE room=$r AND id>$l AND id>$s AND sender<>$u AND kind='text' AND deleted=0",
                r => r.GetInt64(0), ("$r", x.Id), ("$l", x.Read), ("$s", x.Since), ("$u", me)).First());
            // @Erwaehnungen: ungelesene, in denen ich vorkomme
            var mentioned = unread == 0 ? 0 : Convert.ToInt32(UserDb.Query(conn, "SELECT COUNT(*) FROM messages WHERE room=$r AND id>$l AND id>$s AND sender<>$u AND kind='text' AND deleted=0 AND instr(mentions, ','||$u||',')>0",
                r => r.GetInt64(0), ("$r", x.Id), ("$l", x.Read), ("$s", x.Since), ("$u", me)).First());
            result.Add(new ChatRoom(x.Id, x.Kind, x.Title, x.Created, x.By, x.Seq, members, last, unread, mentioned));
        }
        return result.OrderByDescending(r => r.Last?.Created ?? r.Created).ToList();
    }

    public ChatRoom Room(string me, long room) =>
        Rooms(me).FirstOrDefault(r => r.Id == room) ?? throw new KeyNotFoundException("Unterhaltung nicht gefunden.");

    /// <summary>Stand fuer einen Benutzer: aendert sich mit allem, was er sehen kann.</summary>
    public long Seq(string me)
    {
        using var conn = Open();
        return SeqOf(conn, me);
    }

    private static long SeqOf(SqliteConnection conn, string me) => Convert.ToInt64(UserDb.Query(conn,
        "SELECT MAX(COALESCE((SELECT MAX(r.seq) FROM rooms r JOIN members b ON b.room=r.id WHERE b.user=$u),0), COALESCE((SELECT seq FROM user_seq WHERE user=$u),0))",
        r => r.GetInt64(0), ("$u", me)).First());

    /// <summary>Ungelesen ueber alle nicht stummen Unterhaltungen (und Erwaehnungen in stummen) - fuer die Zahl am Symbol.</summary>
    public int Unread(string me)
    {
        using var conn = Open();
        return Convert.ToInt32(UserDb.Query(conn, @"SELECT COUNT(*) FROM messages m JOIN members b ON b.room=m.room AND b.user=$u
                WHERE (b.muted=0 OR instr(m.mentions, ','||$u||',')>0) AND m.id>b.last_read AND m.id>b.since AND m.sender<>$u AND m.kind='text' AND m.deleted=0",
            r => r.GetInt64(0), ("$u", me)).First());
    }

    public sealed record ChangeSet(long Seq, List<ChatRoom> Rooms, List<ChatMessage> Messages, bool More);

    /// <summary>
    /// Was sich seit <paramref name="since"/> geaendert hat: alle Unterhaltungen (die Liste ist klein - fehlt eine, ist man
    /// nicht mehr dabei) und die geaenderten Nachrichten darin. Bei sehr vielen: More, dann mit dem neuen Stand weiterfragen.
    /// Unterhaltungen, die ein Geraet noch nicht kennt, laedt es mit <see cref="Messages"/>.
    /// </summary>
    public ChangeSet Changes(string me, long since, int limit = 500)
    {
        using var conn = Open();
        var seq = SeqOf(conn, me);
        var messages = UserDb.Query(conn, $@"SELECT {MessageColumns} FROM messages m JOIN members b ON b.room=m.room AND b.user=$u
                WHERE m.seq>$s AND m.id>b.since ORDER BY m.seq LIMIT $l",
            ReadMessage, ("$u", me), ("$s", since), ("$l", limit + 1));
        var more = messages.Count > limit;
        if (more)
        {
            messages = messages.Take(limit).ToList();
            seq = messages[^1].Seq;
        }
        return new ChangeSet(seq, Rooms(me), WithReactions(conn, messages), more);
    }

    /// <summary>
    /// Warten, bis sich fuer den Benutzer etwas aendert (hoechstens <paramref name="timeout"/>). Zurueck: der neue Stand.
    /// Mehrere Geraete eines Benutzers warten auf dasselbe Signal.
    /// </summary>
    public async Task<long> WaitAsync(string me, long since, TimeSpan timeout, CancellationToken ct, long busySince = -1)
    {
        var tcs = _waiters.GetOrAdd(me, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        var now = Seq(me);
        if (now > since || (busySince >= 0 && Busy(me).Version != busySince)) return now;
        try { await tcs.Task.WaitAsync(timeout, ct); }
        catch (TimeoutException) { }
        return Seq(me);
    }

    /// <summary>Unterhaltungen, in denen gerade jemand an einer Antwort fuer mich arbeitet („Pukki denkt nach …“).</summary>
    public (long Version, long[] Rooms) Busy(string me) => _busy.TryGetValue(me, out var b) ? b : (0, []);

    /// <summary>„Denkt nach“ fuer eine Unterhaltung an- oder ausschalten; weckt die wartenden Geraete.</summary>
    public void SetBusy(string me, long room, bool on)
    {
        _busy.AddOrUpdate(me, _ => (1, on ? [room] : []),
            (_, b) => (b.Version + 1, on ? b.Rooms.Append(room).Distinct().ToArray() : b.Rooms.Where(r => r != room).ToArray()));
        if (_waiters.TryRemove(me, out var tcs)) tcs.TrySetResult();
    }

    // ---------------- Stufe 2: Farbe, Rechte, Verweise aendern ----------------

    /// <summary>Persoenliche Farbe einer Unterhaltung (#RRGGBB) oder null fuer die Akzentfarbe.</summary>
    public void SetColor(string me, long room, string? color)
    {
        if (color != null && !System.Text.RegularExpressions.Regex.IsMatch(color, "^#[0-9A-Fa-f]{6}$")) throw new ArgumentException("Ungültige Farbe.");
        Run(conn =>
        {
            RequireMember(conn, room, me);
            UserDb.Exec(conn, "UPDATE members SET color=$c WHERE room=$r AND user=$u", ("$c", color), ("$r", room), ("$u", me));
            BumpUser(conn, me, NextSeq(conn));
            return new[] { me };
        });
    }

    public ChatRights Rights(string user)
    {
        using var conn = Open();
        return UserDb.Query(conn, "SELECT expiring, location FROM rights WHERE user=$u", r => new ChatRights(user, r.GetInt64(0) != 0, r.GetInt64(1) != 0), ("$u", user))
            .FirstOrDefault() ?? new ChatRights(user);
    }

    public void SetRights(ChatRights rights)
    {
        using var conn = Open();
        UserDb.Exec(conn, "INSERT INTO rights(user, expiring, location) VALUES($u,$e,$l) ON CONFLICT(user) DO UPDATE SET expiring=excluded.expiring, location=excluded.location",
            ("$u", rights.User), ("$e", rights.Expiring ? 1 : 0), ("$l", rights.LocationRequests ? 1 : 0));
    }

    // ---------------- Pukki (Stufe 5) ----------------

    /// <summary>Unterhaltung zu zweit zwischen a und b, wenn es sie gibt (auch wenn einer sie ausgeblendet hat).</summary>
    public long? DirectRoom(string a, string b)
    {
        var pair = string.CompareOrdinal(a, b) < 0 ? a + "|" + b : b + "|" + a;
        using var conn = Open();
        return UserDb.Query(conn, "SELECT id FROM rooms WHERE pair=$p", r => (long?)r.GetInt64(0), ("$p", pair)).FirstOrDefault();
    }

    public bool IsMember(long room, string user) { using var conn = Open(); return Member(conn, room, user) != null; }

    /// <summary>Aufbewahrung: Nachrichten vor <paramref name="before"/> endgültig löschen, samt Reaktionen. Zahl der gelöschten.</summary>
    public int Purge(DateTime before)
    {
        var cut = before.ToUniversalTime().ToString("o");
        return Write(conn =>
        {
            var rooms = UserDb.Query(conn, "SELECT DISTINCT room FROM messages WHERE created<$c", r => r.GetInt64(0), ("$c", cut));
            if (rooms.Count == 0) return (0, Enumerable.Empty<string>());
            UserDb.Exec(conn, "DELETE FROM reactions WHERE message IN (SELECT id FROM messages WHERE created<$c)", ("$c", cut));
            var count = UserDb.Exec(conn, "DELETE FROM messages WHERE created<$c", ("$c", cut));
            var wake = new List<string>();
            foreach (var room in rooms)
            {
                // Letzte Nachricht neu bestimmen; neuer Stand, damit die Listen neu laden
                var seq = NextSeq(conn);
                UserDb.Exec(conn, "UPDATE rooms SET last_message=(SELECT MAX(id) FROM messages WHERE room=$r), seq=$s WHERE id=$r", ("$r", room), ("$s", seq));
                wake.AddRange(MemberIds(conn, room));
            }
            return (count, (IEnumerable<string>)wake);
        });
    }

    public string? GetKv(string key)
    {
        using var conn = Open();
        return UserDb.Query(conn, "SELECT value FROM kv WHERE key=$k", r => r.GetString(0), ("$k", key)).FirstOrDefault();
    }

    /// <summary>Alle Eintraege mit diesem Praefix.</summary>
    public List<(string Key, string Value)> KvLike(string prefix)
    {
        using var conn = Open();
        return UserDb.Query(conn, "SELECT key, value FROM kv WHERE key LIKE $p", r => (r.GetString(0), r.GetString(1)), ("$p", prefix.Replace("%", "") + "%"));
    }

    public void DeleteKv(string key)
    {
        using var conn = Open();
        UserDb.Exec(conn, "DELETE FROM kv WHERE key=$k", ("$k", key));
    }

    public void SetKv(string key, string value)
    {
        using var conn = Open();
        UserDb.Exec(conn, "INSERT INTO kv(key, value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=excluded.value", ("$k", key), ("$v", value));
    }

    // ---------------- Gruppenraeume (Stufe 4) ----------------

    public List<ChatSpace> SpacesOf(long room)
    {
        using var conn = Open();
        return UserDb.Query(conn, "SELECT room, kind, owner, ref, created FROM spaces WHERE room=$r ORDER BY created",
            r => new ChatSpace(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), DateTime.Parse(r.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind)), ("$r", room));
    }

    /// <summary>Mitglieder einer Unterhaltung (fuer die Freigaben der Gruppenraeume).</summary>
    public List<string> MembersOf(long room) { using var conn = Open(); return MemberIds(conn, room); }

    /// <summary>Darf me hier einen Raum einrichten? Nur in Gruppen, nur wer sie verwaltet.</summary>
    public void RequireSpaceAdmin(string me, long room) { using var conn = Open(); RequireGroupAdmin(conn, room, me); }

    public void CheckMember(string me, long room) { using var conn = Open(); RequireMember(conn, room, me); }

    /// <summary>Raum eintragen und im Verlauf melden (alle Mitglieder sehen es).</summary>
    public void AddSpace(string me, long room, string kind, string reference, string name)
    {
        Run(conn =>
        {
            RequireGroupAdmin(conn, room, me);
            if (UserDb.Exec(conn, "INSERT OR IGNORE INTO spaces(room, kind, owner, ref, created) VALUES($r,$k,$o,$f,$c)",
                    ("$r", room), ("$k", kind), ("$o", me), ("$f", reference), ("$c", Now())) == 0)
                throw new InvalidOperationException("Diese Gruppe hat schon so einen Raum.");
            var seq = NextSeq(conn);
            AddSystem(conn, room, me, seq, new { ev = "space", kind, title = name });
            return MemberIds(conn, room);
        });
    }

    /// <summary>Raum austragen (die Daten bleiben beim Besitzer).</summary>
    public void RemoveSpace(long room, string kind)
    {
        Run(conn =>
        {
            UserDb.Exec(conn, "DELETE FROM spaces WHERE room=$r AND kind=$k", ("$r", room), ("$k", kind));
            Touch(conn, room, NextSeq(conn));
            return MemberIds(conn, room);
        });
    }

    /// <summary>
    /// Einen eigenen Verweis aendern (Art umstellen, zurueckziehen) - nur wer ihn verschickt hat und Besitzer ist,
    /// und nur am Original (nicht an Weiterleitungen). Alle in der Unterhaltung sehen die Aenderung.
    /// </summary>
    public void UpdateRef(string me, long message, int index, Func<ChatRef, ChatRef> change)
    {
        Run(conn =>
        {
            var v = Visible(conn, message, me);
            if (v.Deleted) throw new InvalidOperationException("Die Nachricht ist gelöscht.");
            var json = UserDb.Query(conn, "SELECT refs FROM messages WHERE id=$i", r => r.IsDBNull(0) ? null : r.GetString(0), ("$i", message)).FirstOrDefault();
            var refs = json == null ? new List<ChatRef>() : JsonSerializer.Deserialize<List<ChatRef>>(json, Json) ?? new();
            if (index < 0 || index >= refs.Count) throw new KeyNotFoundException();
            var r0 = refs[index];
            if (v.Sender != me || r0.Owner != me || r0.RootMessage != null) throw new UnauthorizedAccessException("Nur wer es verschickt hat, kann das ändern.");
            refs[index] = change(r0);
            var seq = NextSeq(conn);
            UserDb.Exec(conn, "UPDATE messages SET refs=$f, seq=$s WHERE id=$i", ("$f", JsonSerializer.Serialize(refs, Json)), ("$s", seq), ("$i", message));
            Touch(conn, v.Room, seq);
            return MemberIds(conn, v.Room);
        });
    }

    /// <summary>Zustand eines Verweises: ok, expired (abgelaufen) oder revoked (zurueckgezogen) - bei Weiterleitungen samt Original.</summary>
    public string RefState(ChatRef r)
    {
        var now = DateTime.UtcNow;
        if (r.Revoked) return "revoked";
        if (r.Expires is { } e && e < now) return "expired";
        if (r.RootMessage is not { } root) return "ok";
        using var conn = Open();
        var row = UserDb.Query(conn, "SELECT deleted, refs FROM messages WHERE id=$i", x => (Deleted: x.GetInt64(0) != 0, Refs: x.IsDBNull(1) ? null : x.GetString(1)), ("$i", root)).FirstOrDefault();
        if (row.Refs == null || row.Deleted) return "revoked";
        var refs = JsonSerializer.Deserialize<List<ChatRef>>(row.Refs, Json) ?? new();
        if (r.RootIndex is not { } ri || ri < 0 || ri >= refs.Count) return "revoked";
        var orig = refs[ri];
        return orig.Revoked ? "revoked" : orig.Expires is { } oe && oe < now ? "expired" : "ok";
    }

    public sealed record SharedIn(long Room, string Kind, string? Title, List<string> Members, DateTime Last, int Count);

    /// <summary>
    /// Wo eine eigene Datei im Chat verschickt wurde (fuer die Dateiablage: "im Chat geteilt mit ..."): je Unterhaltung, in
    /// der der Besitzer noch Mitglied ist, die anderen Mitglieder, das letzte Mal und wie oft. Geloeschte Nachrichten zaehlen nicht.
    /// </summary>
    public List<SharedIn> SharedFile(string owner, long fileId)
    {
        using var conn = Open();
        var hits = UserDb.Query(conn, @"SELECT m.room, MAX(m.created), COUNT(*) FROM messages m, json_each(m.refs) j
                JOIN members b ON b.room=m.room AND b.user=$o
                WHERE m.deleted=0 AND m.refs IS NOT NULL AND json_extract(j.value,'$.type')='file'
                  AND json_extract(j.value,'$.owner')=$o AND json_extract(j.value,'$.fileId')=$f
                GROUP BY m.room ORDER BY MAX(m.id) DESC",
            r => (Room: r.GetInt64(0), Last: Time(r.GetString(1)), Count: r.GetInt32(2)), ("$o", owner), ("$f", fileId));
        var result = new List<SharedIn>();
        foreach (var h in hits)
        {
            var room = UserDb.Query(conn, "SELECT kind, title FROM rooms WHERE id=$r", r => (Kind: r.GetString(0), Title: r.IsDBNull(1) ? null : r.GetString(1)), ("$r", h.Room)).First();
            result.Add(new SharedIn(h.Room, room.Kind, room.Title, MemberIds(conn, h.Room).Where(u => u != owner).ToList(), h.Last, h.Count));
        }
        return result;
    }

    /// <summary>Darf der Benutzer diese Nachricht sehen? Dann ihre Verweise.</summary>
    public (long Room, string Sender, List<ChatRef> Refs) RefsOf(string me, long message)
    {
        var m = Message(me, message);
        return (m.Room, m.Sender, m.Refs);
    }
}
