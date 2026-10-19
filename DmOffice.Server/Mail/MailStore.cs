using System.IO.Compression;
using System.Text.Json;
using DmOffice.Core;
using MailKit;
using Microsoft.Data.Sqlite;
using MimeKit;

namespace DmOffice.Mail;

public record Addr(string? Name, string Address);

public sealed record FolderRow(string Account, string Path, string Name, string Delimiter, string? Special,
    bool Selectable, long UidValidity, long ModSeq, int Unread, int Total, string? Synced);

public sealed record MailRow(long Id, string Account, string Folder, uint Uid, string? MessageId, string Subject,
    Addr? From, List<Addr> To, List<Addr> Cc, DateTimeOffset Date, long Size, bool Seen, bool Flagged, bool Answered,
    bool Forwarded, bool HasAttachments, string Preview, double? Spam, bool Cached, List<string> Keywords, bool? Focus = null);

/// <summary>
/// Die Mails eines Benutzers in seiner Datenbank: Kopfzeilen sofort,
/// die vollstaendige Nachricht (mit Anhaengen, GZip) nach und nach.
/// Lesen, Liste und Suche gehen nur hierher, nicht zum Server.
/// </summary>
public static class MailStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ---------------- Ordner ----------------

    public static List<FolderRow> Folders(UserDb db, string account) =>
        db.Query("SELECT account,path,name,delimiter,special,selectable,uidvalidity,modseq,unread,total,synced FROM mail_folders WHERE account=$a",
            ReadFolder, ("$a", account));

    public static FolderRow? Folder(UserDb db, string account, string path) =>
        db.Query("SELECT account,path,name,delimiter,special,selectable,uidvalidity,modseq,unread,total,synced FROM mail_folders WHERE account=$a AND path=$p",
            ReadFolder, ("$a", account), ("$p", path)).FirstOrDefault();

    private static FolderRow ReadFolder(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
        r.IsDBNull(4) ? null : r.GetString(4), r.GetInt64(5) != 0, r.GetInt64(6), r.GetInt64(7), r.GetInt32(8), r.GetInt32(9),
        r.IsDBNull(10) ? null : r.GetString(10));

    /// <summary>Ordnerliste vom Server uebernehmen; was dort fehlt, faellt hier samt Mails weg.</summary>
    public static void ReplaceFolders(UserDb db, string account, List<(string Path, string Name, string Delimiter, string? Special, bool Selectable)> folders)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var existing = UserDb.Query(conn, "SELECT path FROM mail_folders WHERE account=$a", r => r.GetString(0), ("$a", account)).ToHashSet();
        foreach (var f in folders)
        {
            UserDb.Exec(conn, @"INSERT INTO mail_folders(account,path,name,delimiter,special,selectable) VALUES($a,$p,$n,$d,$s,$sel)
                ON CONFLICT(account,path) DO UPDATE SET name=$n, delimiter=$d, special=$s, selectable=$sel",
                ("$a", account), ("$p", f.Path), ("$n", f.Name), ("$d", f.Delimiter), ("$s", f.Special), ("$sel", f.Selectable ? 1 : 0));
            existing.Remove(f.Path);
        }
        foreach (var gone in existing)
        {
            DeleteFts(conn, "account=$a AND folder=$f", ("$a", account), ("$f", gone));
            UserDb.Exec(conn, "DELETE FROM mail WHERE account=$a AND folder=$f", ("$a", account), ("$f", gone));
            UserDb.Exec(conn, "DELETE FROM mail_folders WHERE account=$a AND path=$f", ("$a", account), ("$f", gone));
        }
        tx.Commit();
    }

    public static void SetFolderState(UserDb db, string account, string path, long uidValidity, long modSeq, int unread, int total) =>
        db.Exec("UPDATE mail_folders SET uidvalidity=$u, modseq=$m, unread=$un, total=$t, synced=$s WHERE account=$a AND path=$p",
            ("$u", uidValidity), ("$m", modSeq), ("$un", unread), ("$t", total), ("$s", DateTime.UtcNow.ToString("o")),
            ("$a", account), ("$p", path));

    public static void WipeFolder(UserDb db, string account, string path)
    {
        Ensure(db);
        using var conn = db.Open();
        DeleteFts(conn, "account=$a AND folder=$f", ("$a", account), ("$f", path));
        UserDb.Exec(conn, "DELETE FROM mail WHERE account=$a AND folder=$f", ("$a", account), ("$f", path));
        UserDb.Exec(conn, "DELETE FROM mail_fetch_failed WHERE account=$a AND folder=$f", ("$a", account), ("$f", path));
    }

    public static void ForgetAccount(UserDb db, string account)
    {
        Ensure(db);
        using var conn = db.Open();
        DeleteFts(conn, "account=$a", ("$a", account));
        UserDb.Exec(conn, "DELETE FROM mail WHERE account=$a", ("$a", account));
        UserDb.Exec(conn, "DELETE FROM mail_folders WHERE account=$a", ("$a", account));
        UserDb.Exec(conn, "DELETE FROM kv WHERE key LIKE $k", ("$k", "mail." + account + ".%"));
        UserDb.Exec(conn, "DELETE FROM mail_processed WHERE account=$a", ("$a", account));
        UserDb.Exec(conn, "DELETE FROM mail_fetch_failed WHERE account=$a", ("$a", account));
    }

    // ---------------- Eigene Tabellen ----------------

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<UserDb, object> Ensured = new();

    /// <summary>Tabellen fuer den Mail-Sync anlegen, einmal je Datenbank:
    /// mail_processed - von Spamfilter und Regeln bearbeitete Mails (je Konto, nach Message-ID);
    /// mail_fetch_failed - Mails, deren Inhalt sich nicht laden liess (spaeter erneut).</summary>
    internal static void Ensure(UserDb db)
    {
        if (Ensured.TryGetValue(db, out _)) return;
        db.Exec(@"CREATE TABLE IF NOT EXISTS mail_processed (
                account TEXT NOT NULL, key TEXT NOT NULL, stage INTEGER NOT NULL, tries INTEGER NOT NULL DEFAULT 0, at INTEGER NOT NULL,
                PRIMARY KEY(account, key)) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS mail_processed_at ON mail_processed(at);
            CREATE TABLE IF NOT EXISTS mail_fetch_failed (
                account TEXT NOT NULL, folder TEXT NOT NULL, uid INTEGER NOT NULL, tries INTEGER NOT NULL, next INTEGER NOT NULL,
                PRIMARY KEY(account, folder, uid)) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS mail_followup (
                account TEXT NOT NULL, message_id TEXT NOT NULL, due TEXT,
                PRIMARY KEY(account, message_id)) WITHOUT ROWID;");
        Ensured.AddOrUpdate(db, true);
    }

    private static void DeleteFts(SqliteConnection conn, string where, params (string, object?)[] args) =>
        UserDb.Exec(conn, $"DELETE FROM mail_fts WHERE rowid IN (SELECT id FROM mail WHERE {where})", args);

    // ---------------- Mails ----------------

    public static HashSet<uint> Uids(UserDb db, string account, string folder) =>
        db.Query("SELECT uid FROM mail WHERE account=$a AND folder=$f", r => (uint)r.GetInt64(0), ("$a", account), ("$f", folder)).ToHashSet();

    public static void Insert(UserDb db, string account, string folder, IEnumerable<IMessageSummary> summaries)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        foreach (var s in summaries)
        {
            var e = s.Envelope;
            var from = e?.From.Mailboxes.FirstOrDefault();
            var to = Addrs(e?.To);
            var cc = Addrs(e?.Cc);
            var flags = s.Flags ?? MessageFlags.None;
            var refs = s.References?.FirstOrDefault() ?? e?.InReplyTo ?? e?.MessageId;
            var date = (e?.Date ?? s.InternalDate ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds();
            var id = (long)UserDb.Query(conn, @"INSERT INTO mail(account,folder,uid,message_id,in_reply_to,thread,subject,from_name,from_addr,recipients,date,size,
                    seen,flagged,answered,forwarded,has_att,keywords)
                VALUES($a,$f,$u,$mid,$irt,$th,$s,$fn,$fa,$rc,$d,$sz,$seen,$fl,$an,$fw,$att,$kw)
                ON CONFLICT(account,folder,uid) DO UPDATE SET seen=$seen, keywords=$kw RETURNING id", r => r.GetInt64(0),
                ("$a", account), ("$f", folder), ("$u", (long)s.UniqueId.Id), ("$mid", e?.MessageId), ("$irt", e?.InReplyTo), ("$th", refs),
                ("$s", e?.Subject ?? ""), ("$fn", string.IsNullOrEmpty(from?.Name) ? null : from!.Name), ("$fa", from?.Address),
                ("$rc", JsonSerializer.Serialize(new { to, cc }, Json)), ("$d", date), ("$sz", (long)(s.Size ?? 0)),
                ("$seen", flags.HasFlag(MessageFlags.Seen) ? 1 : 0), ("$fl", flags.HasFlag(MessageFlags.Flagged) ? 1 : 0),
                ("$an", flags.HasFlag(MessageFlags.Answered) ? 1 : 0), ("$fw", s.Keywords?.Contains("$Forwarded") == true ? 1 : 0),
                ("$att", s.Attachments?.Any() == true ? 1 : 0), ("$kw", KeywordText(s.Keywords))).First();
            UserDb.Exec(conn, "DELETE FROM mail_fts WHERE rowid=$id", ("$id", id));
            UserDb.Exec(conn, "INSERT INTO mail_fts(rowid,subject,sender,recipients,body) VALUES($id,$s,$sn,$rc,'')",
                ("$id", id), ("$s", e?.Subject ?? ""), ("$sn", $"{from?.Name} {from?.Address}"),
                ("$rc", string.Join(" ", to.Concat(cc).Select(x => $"{x.Name} {x.Address}"))));
        }
        tx.Commit();
    }

    /// <summary>Die vollstaendige Nachricht ablegen, mit Text fuer die Suche.</summary>
    public static void StoreRaw(UserDb db, string account, string folder, uint uid, byte[] raw, MimeMessage message)
    {
        var text = BodyText(message);
        var preview = Preview(text);
        bool hasAtt = message.Attachments.Any();
        using var conn = db.Open();
        var id = UserDb.Query(conn, "SELECT id FROM mail WHERE account=$a AND folder=$f AND uid=$u", r => r.GetInt64(0),
            ("$a", account), ("$f", folder), ("$u", (long)uid)).FirstOrDefault();
        if (id == 0) return;
        UserDb.Exec(conn, "INSERT INTO mail_raw(id, raw) VALUES($id, $r) ON CONFLICT(id) DO UPDATE SET raw=$r", ("$id", id), ("$r", Compress(raw)));
        UserDb.Exec(conn, "UPDATE mail SET cached=1, preview=$p, has_att=$h WHERE id=$id", ("$p", preview), ("$h", hasAtt ? 1 : 0), ("$id", id));
        UserDb.Exec(conn, "UPDATE mail_fts SET body=$b WHERE rowid=$id", ("$b", text.Length > 30_000 ? text[..30_000] : text), ("$id", id));
        Ensure(db);
        UserDb.Exec(conn, "DELETE FROM mail_fetch_failed WHERE account=$a AND folder=$f AND uid=$u", ("$a", account), ("$f", folder), ("$u", (long)uid));
    }

    /// <summary>Nur den Inhalt ablegen (fuer Messungen und Umzug).</summary>
    internal static void StoreRawRaw(SqliteConnection conn, long id, byte[] gz) =>
        UserDb.Exec(conn, "INSERT INTO mail_raw(id, raw) VALUES($id, $r) ON CONFLICT(id) DO UPDATE SET raw=$r; UPDATE mail SET cached=1 WHERE id=$id;", ("$r", gz), ("$id", id));

    public static void SetSpamScore(UserDb db, string account, string folder, uint uid, double score) =>
        db.Exec("UPDATE mail SET spam=$s WHERE account=$a AND folder=$f AND uid=$u", ("$s", score), ("$a", account), ("$f", folder), ("$u", (long)uid));

    // Mails, deren Laden zuletzt fehlschlug, bis zum naechsten Versuch auslassen
    private const string NotFailing =
        " AND NOT EXISTS (SELECT 1 FROM mail_fetch_failed x WHERE x.account=mail.account AND x.folder=mail.folder AND x.uid=mail.uid AND x.next > $now)";

    /// <summary>Mails ohne Inhalt hier, neueste zuerst. Mit slim nur die, deren Inhalt "Schlank" behaelt.</summary>
    public static List<(uint Uid, long Size)> MissingBodies(UserDb db, string account, string folder, long maxBytes, int limit, SlimLimit? slim = null)
    {
        Ensure(db);
        var (with, keep) = slim == null ? ("", "") : ($"WITH {KeepCte} ", " AND id IN (SELECT id FROM keep)");
        return db.Query($"{with}SELECT uid, size FROM mail WHERE account=$a AND folder=$f AND cached=0 AND size <= $m{keep}{NotFailing} ORDER BY date DESC LIMIT $l",
            r => ((uint)r.GetInt64(0), r.GetInt64(1)), SlimArgs(slim, ("$a", account), ("$f", folder), ("$m", maxBytes), ("$l", limit),
            ("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds())));
    }

    /// <summary>Wie MissingBodies mit slim, aber fuer alle Ordner auf einmal - die Auswahl rechnet nur einmal.</summary>
    public static ILookup<string, (uint Uid, long Size)> MissingBodies(UserDb db, string account, long maxBytes, SlimLimit slim)
    {
        Ensure(db);
        return db.Query($"WITH {KeepCte} SELECT folder, uid, size FROM mail WHERE account=$a AND cached=0 AND size <= $m AND id IN (SELECT id FROM keep){NotFailing} ORDER BY date DESC",
                r => (Folder: r.GetString(0), Uid: (uint)r.GetInt64(1), Size: r.GetInt64(2)),
                SlimArgs(slim, ("$a", account), ("$m", maxBytes), ("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds())))
            .ToLookup(x => x.Folder, x => (x.Uid, x.Size));
    }

    public static int MissingCount(UserDb db, string account, long maxBytes, SlimLimit? slim = null)
    {
        Ensure(db);
        var (with, keep) = slim == null ? ("", "") : ($"WITH {KeepCte} ", " AND id IN (SELECT id FROM keep)");
        return Convert.ToInt32(db.Scalar($"{with}SELECT COUNT(*) FROM mail WHERE account=$a AND cached=0 AND size <= $m{keep}{NotFailing}",
            SlimArgs(slim, ("$a", account), ("$m", maxBytes), ("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds()))));
    }

    // ---------------- Speichermodus "Schlank" ----------------

    /// <summary>Grenzen fuer "Schlank": Inhalte der Mails, die seit Since (Unix-ms) kamen oder geoeffnet
    /// wurden - die juengsten zuerst, bis sie zusammen Budget Bytes erreichen (Groesse laut Server).</summary>
    public sealed record SlimLimit(long Since, long Budget)
    {
        public static SlimLimit? Of(MailAccount a, DateTimeOffset now) =>
            a.Slim ? new(now.AddDays(-a.SlimDays).ToUnixTimeMilliseconds(), a.SlimMb * 1024L * 1024L) : null;

        /// <summary>Groessere Mails nie vorab - sonst verdraengt eine einzige alle anderen.
        /// Geoeffnet bleiben sie bis zum naechsten Aufraeumen.</summary>
        public long Big => Budget / 4;
    }

    /// <summary>Welche Mails eines Kontos ihren Inhalt behalten - neu angekommene und zuletzt geoeffnete zuerst.</summary>
    private const string KeepCte = @"keep(id) AS (SELECT id FROM (
            SELECT id, SUM(size) OVER (ORDER BY MAX(date, COALESCE(opened, 0)) DESC, id DESC ROWS UNBOUNDED PRECEDING) AS run
            FROM mail WHERE account=$a AND size <= $big AND (date >= $since OR opened >= $since)) WHERE run <= $budget)";

    private static (string, object?)[] SlimArgs(SlimLimit? slim, params (string, object?)[] args) =>
        slim == null ? args : args.Concat(new (string, object?)[] { ("$since", slim.Since), ("$budget", slim.Budget), ("$big", slim.Big) }).ToArray();

    /// <summary>Aufraeumer fuer "Schlank": Inhalte und Anhaenge ausserhalb der Grenzen verwerfen. Kopfdaten,
    /// Flags und Vorschau bleiben; die Suche findet die Mail weiter ueber Kopf und Vorschau. Gibt Zahl
    /// und Bytes (komprimiert) zurueck - frei wird der Platz in der Datei erst mit UserDb.ReclaimIncremental/Compact.</summary>
    public static (int Count, long Bytes) Discard(UserDb db, string account, SlimLimit slim)
    {
        using var conn = db.Open();
        var drop = UserDb.Query(conn, $@"WITH {KeepCte} SELECT m.id, length(r.raw) FROM mail m JOIN mail_raw r ON r.id = m.id
                WHERE m.account=$a AND m.id NOT IN (SELECT id FROM keep)",
            r => (Id: r.GetInt64(0), Bytes: r.GetInt64(1)), SlimArgs(slim, ("$a", account)));
        foreach (var chunk in drop.Chunk(500))
        {
            // Nur Zahlen aus der Datenbank - direkt einsetzen geht
            var ids = string.Join(",", chunk.Select(x => x.Id));
            using var tx = conn.BeginTransaction();
            UserDb.Exec(conn, $"DELETE FROM mail_raw WHERE id IN ({ids})");
            UserDb.Exec(conn, $"UPDATE mail SET cached=0 WHERE id IN ({ids})");
            UserDb.Exec(conn, $"UPDATE mail_fts SET body=(SELECT preview FROM mail WHERE mail.id = mail_fts.rowid) WHERE rowid IN ({ids})");
            tx.Commit();
        }
        return (drop.Count, drop.Sum(x => x.Bytes));
    }

    /// <summary>Geoeffnet: der Inhalt zaehlt bei "Schlank" wieder als juengst (bleibt hier).</summary>
    public static void Opened(UserDb db, string account, string folder, uint uid) =>
        db.Exec("UPDATE mail SET opened=$t WHERE account=$a AND folder=$f AND uid=$u",
            ("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), ("$a", account), ("$f", folder), ("$u", (long)uid));

    /// <summary>Mails ohne Inhalt und ohne Vorschau - bei "Schlank" holt der Sync nur die Vorschau.</summary>
    public static List<uint> MissingPreviews(UserDb db, string account, string folder, int limit) =>
        db.Query("SELECT uid FROM mail WHERE account=$a AND folder=$f AND cached=0 AND preview='' ORDER BY date DESC LIMIT $l",
            r => (uint)r.GetInt64(0), ("$a", account), ("$f", folder), ("$l", limit));

    /// <summary>Vorschau vom Server (ohne Inhalt) ablegen, auch fuer die Suche.</summary>
    public static void SetPreviews(UserDb db, string account, string folder, IEnumerable<(uint Uid, string? Text)> items)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        foreach (var (uid, text) in items)
        {
            // Leer heisst "noch nicht geholt" - eine Mail ohne Text bekommt ein Leerzeichen
            var p = Preview(text ?? "");
            if (p.Length == 0) p = " ";
            var id = UserDb.Query(conn, "UPDATE mail SET preview=$p WHERE account=$a AND folder=$f AND uid=$u AND cached=0 RETURNING id",
                r => r.GetInt64(0), ("$p", p), ("$a", account), ("$f", folder), ("$u", (long)uid)).FirstOrDefault();
            if (id != 0) UserDb.Exec(conn, "UPDATE mail_fts SET body=$p WHERE rowid=$id", ("$p", p), ("$id", id));
        }
        tx.Commit();
    }

    /// <summary>Mails eines Ordners nach UID, nur die ohne Inhalt hier - fuer Treffer der Suche beim Anbieter.</summary>
    public static List<MailRow> NotCached(UserDb db, string account, string folder, IEnumerable<uint> uids)
    {
        var result = new List<MailRow>();
        foreach (var chunk in uids.Chunk(500))
            result.AddRange(db.Query($"SELECT {RowColumns} FROM mail WHERE account=$a AND folder=$f AND cached=0 AND uid IN ({string.Join(",", chunk)})",
                ReadRow, ("$a", account), ("$f", folder)));
        return result;
    }

    /// <summary>Laden fehlgeschlagen: spaeter erneut, mit wachsendem Abstand (10 min, 20 min ... hoechstens ein Tag).</summary>
    public static void BodyFailed(UserDb db, string account, string folder, uint uid)
    {
        Ensure(db);
        using var conn = db.Open();
        var tries = UserDb.Query(conn, "SELECT tries FROM mail_fetch_failed WHERE account=$a AND folder=$f AND uid=$u", r => r.GetInt32(0),
            ("$a", account), ("$f", folder), ("$u", (long)uid)).FirstOrDefault() + 1;
        var wait = TimeSpan.FromMinutes(Math.Min(24 * 60, 10 * Math.Pow(2, Math.Min(tries - 1, 10))));
        UserDb.Exec(conn, @"INSERT INTO mail_fetch_failed(account,folder,uid,tries,next) VALUES($a,$f,$u,$t,$n)
            ON CONFLICT(account,folder,uid) DO UPDATE SET tries=$t, next=$n",
            ("$a", account), ("$f", folder), ("$u", (long)uid), ("$t", tries), ("$n", (DateTimeOffset.UtcNow + wait).ToUnixTimeSeconds()));
    }

    // ---------------- Bearbeitet (Spamfilter und Regeln) ----------------

    /// <summary>Stufen in mail_processed: Weiterleitung schon raus, Kopien schon angelegt (bei
    /// Wiederholung nach einem Fehler nicht noch einmal), fertig.</summary>
    public const int StageForwarded = 1, StageCopied = 2, StageDone = 3;

    /// <summary>Schluessel je Mail, ueber Ordner und Verschieben hinweg gleich:
    /// die Message-ID, ohne sie ein Hash aus Betreff, Datum und Absender.</summary>
    public static Dictionary<uint, string> ProcessKeys(UserDb db, string account, string folder)
    {
        var result = new Dictionary<uint, string>();
        foreach (var (uid, mid, subject, date, from) in db.Query("SELECT uid, message_id, subject, date, from_addr FROM mail WHERE account=$a AND folder=$f",
                     r => ((uint)r.GetInt64(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2), r.GetInt64(3), r.IsDBNull(4) ? null : r.GetString(4)),
                     ("$a", account), ("$f", folder)))
        {
            var id = mid?.Trim().Trim('<', '>').Trim();
            result[uid] = !string.IsNullOrEmpty(id)
                ? "mid:" + id
                : "h:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(subject + "|" + date + "|" + from)))[..32];
        }
        return result;
    }

    /// <summary>Stufe und Fehlversuche der bekannten Schluessel eines Kontos.</summary>
    public static Dictionary<string, (int Stage, int Tries)> Processed(UserDb db, string account)
    {
        Ensure(db);
        return db.Query("SELECT key, stage, tries FROM mail_processed WHERE account=$a", r => (Key: r.GetString(0), Stage: r.GetInt32(1), Tries: r.GetInt32(2)), ("$a", account))
            .ToDictionary(x => x.Key, x => (x.Stage, x.Tries));
    }

    public static void SetProcessed(UserDb db, string account, IEnumerable<string> keys, int stage)
    {
        Ensure(db);
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var k in keys)
            UserDb.Exec(conn, @"INSERT INTO mail_processed(account,key,stage,tries,at) VALUES($a,$k,$s,0,$t)
                ON CONFLICT(account,key) DO UPDATE SET stage=MAX(stage,$s), at=$t", ("$a", account), ("$k", k), ("$s", stage), ("$t", now));
        tx.Commit();
    }

    /// <summary>Fehlversuch zaehlen; gibt die Zahl der Versuche zurueck.</summary>
    public static int ProcessFailed(UserDb db, string account, string key)
    {
        Ensure(db);
        using var conn = db.Open();
        UserDb.Exec(conn, @"INSERT INTO mail_processed(account,key,stage,tries,at) VALUES($a,$k,0,1,$t)
            ON CONFLICT(account,key) DO UPDATE SET tries=tries+1, at=$t", ("$a", account), ("$k", key), ("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        return UserDb.Query(conn, "SELECT tries FROM mail_processed WHERE account=$a AND key=$k", r => r.GetInt32(0), ("$a", account), ("$k", key)).First();
    }

    /// <summary>Alte Eintraege wegraeumen.</summary>
    public static void PruneProcessed(UserDb db, TimeSpan age)
    {
        Ensure(db);
        db.Exec("DELETE FROM mail_processed WHERE at < $t", ("$t", (DateTimeOffset.UtcNow - age).ToUnixTimeSeconds()));
    }

    public static void SetFlags(UserDb db, string account, string folder, uint uid, MessageFlags flags, IEnumerable<string>? keywords = null)
    {
        db.Exec("UPDATE mail SET seen=$s, flagged=$f, answered=$an, keywords=COALESCE($kw, keywords) WHERE account=$a AND folder=$fo AND uid=$u",
            ("$s", flags.HasFlag(MessageFlags.Seen) ? 1 : 0), ("$f", flags.HasFlag(MessageFlags.Flagged) ? 1 : 0),
            ("$an", flags.HasFlag(MessageFlags.Answered) ? 1 : 0), ("$kw", keywords == null ? null : KeywordText(keywords)),
            ("$a", account), ("$fo", folder), ("$u", (long)uid));
    }

    /// <summary>Nur unsere Kategorien (dm_...) und $Label1..5 merken - Systemschlagworte wie $Forwarded nicht.</summary>
    private static string KeywordText(IEnumerable<string>? keywords)
    {
        var list = (keywords ?? Enumerable.Empty<string>())
            .Where(k => k.StartsWith("dm_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("$label", StringComparison.OrdinalIgnoreCase))
            .Select(k => k.ToLowerInvariant()).Distinct().ToList();
        return list.Count == 0 ? "" : " " + string.Join(" ", list) + " ";
    }

    public static void SetKeyword(UserDb db, string account, string folder, IEnumerable<uint> uids, string key, bool on)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        foreach (var uid in uids)
        {
            var current = UserDb.Query(conn, "SELECT keywords FROM mail WHERE account=$a AND folder=$f AND uid=$u", r => r.GetString(0),
                ("$a", account), ("$f", folder), ("$u", (long)uid)).FirstOrDefault();
            if (current == null) continue;
            var set = current.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
            if (on) set.Add(key); else set.Remove(key);
            UserDb.Exec(conn, "UPDATE mail SET keywords=$k WHERE account=$a AND folder=$f AND uid=$u",
                ("$k", KeywordText(set)), ("$a", account), ("$f", folder), ("$u", (long)uid));
        }
        tx.Commit();
    }

    public static void SetFlag(UserDb db, string account, string folder, IEnumerable<uint> uids, string column, bool value)
    {
        if (column is not ("seen" or "flagged" or "answered" or "forwarded")) throw new ArgumentException(column);
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        foreach (var uid in uids)
            UserDb.Exec(conn, $"UPDATE mail SET {column}=$v WHERE account=$a AND folder=$f AND uid=$u",
                ("$v", value ? 1 : 0), ("$a", account), ("$f", folder), ("$u", (long)uid));
        tx.Commit();
        RecountUnread(conn, account, folder);
    }

    /// <summary>Ungelesen-Zaehler nach einer Aenderung hier - bis der naechste
    /// Sync den Wert vom Server bringt.</summary>
    private static void RecountUnread(SqliteConnection conn, string account, string folder)
    {
        var local = UserDb.Query(conn, "SELECT COUNT(*) FROM mail WHERE account=$a AND folder=$f AND seen=0", r => r.GetInt32(0), ("$a", account), ("$f", folder)).First();
        // Bei "nur N Monate" kann der Server mehr Ungelesene haben als hier liegen - dann den Unterschied behalten
        var known = UserDb.Query(conn, "SELECT value FROM kv WHERE key=$k", r => r.GetString(0), ("$k", $"mail.{account}.outside.{folder}")).FirstOrDefault();
        UserDb.Exec(conn, "UPDATE mail_folders SET unread=$u WHERE account=$a AND path=$f",
            ("$u", local + (int.TryParse(known, out var k) ? k : 0)), ("$a", account), ("$f", folder));
    }

    /// <summary>Ganzer Ordner gelesen - auch der Zaehler fuer Mails ausserhalb des Sync-Zeitraums.</summary>
    public static void MarkFolderRead(UserDb db, string account, string folder)
    {
        using var conn = db.Open();
        UserDb.Exec(conn, "UPDATE mail SET seen=1 WHERE account=$a AND folder=$f AND seen=0", ("$a", account), ("$f", folder));
        UserDb.Exec(conn, "DELETE FROM kv WHERE key=$k", ("$k", $"mail.{account}.outside.{folder}"));
        UserDb.Exec(conn, "UPDATE mail_folders SET unread=0 WHERE account=$a AND path=$f", ("$a", account), ("$f", folder));
    }

    public static void Delete(UserDb db, string account, string folder, IEnumerable<uint> uids)
    {
        Ensure(db);
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        foreach (var uid in uids)
        {
            UserDb.Exec(conn, "DELETE FROM mail_fts WHERE rowid IN (SELECT id FROM mail WHERE account=$a AND folder=$f AND uid=$u)",
                ("$a", account), ("$f", folder), ("$u", (long)uid));
            UserDb.Exec(conn, "DELETE FROM mail WHERE account=$a AND folder=$f AND uid=$u", ("$a", account), ("$f", folder), ("$u", (long)uid));
            UserDb.Exec(conn, "DELETE FROM mail_fetch_failed WHERE account=$a AND folder=$f AND uid=$u", ("$a", account), ("$f", folder), ("$u", (long)uid));
        }
        tx.Commit();
        RecountUnread(conn, account, folder);
    }

    private const string RowColumns =
        "id,account,folder,uid,message_id,subject,from_name,from_addr,recipients,date,size,seen,flagged,answered,forwarded,has_att,preview,spam,cached,keywords,focus";

    /// <summary>Dieselben Spalten mit Tabellenkuerzel "m." - jede einzeln, nicht per Ersetzen (uid, message_id).</summary>
    private static readonly string Prefixed = string.Join(",", RowColumns.Split(',').Select(c => "m." + c.Trim()));

    private static MailRow ReadRow(SqliteDataReader r)
    {
        var json = r.GetString(8);
        // Vorgabe der Spalte ist "[]" - nur ein Objekt enthaelt Empfaenger
        var rc = json.StartsWith('{') ? JsonSerializer.Deserialize<Recipients>(json, Json) ?? new Recipients() : new Recipients();
        return new MailRow(r.GetInt64(0), r.GetString(1), r.GetString(2), (uint)r.GetInt64(3), r.IsDBNull(4) ? null : r.GetString(4),
            r.GetString(5), r.IsDBNull(7) ? null : new Addr(r.IsDBNull(6) ? null : r.GetString(6), r.GetString(7)),
            rc.To ?? new(), rc.Cc ?? new(), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(9)), r.GetInt64(10),
            r.GetInt64(11) != 0, r.GetInt64(12) != 0, r.GetInt64(13) != 0, r.GetInt64(14) != 0, r.GetInt64(15) != 0,
            r.GetString(16), r.IsDBNull(17) ? null : r.GetDouble(17), r.GetInt64(18) != 0,
            r.GetString(19).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList(), r.IsDBNull(20) ? null : r.GetInt64(20) != 0);
    }

    private sealed class Recipients
    {
        public List<Addr>? To { get; set; }
        public List<Addr>? Cc { get; set; }
    }

    /// <summary>Kein Ordner auf dem Server: alle gekennzeichneten Mails eines Kontos (wie "Markiert" bei Proton).</summary>
    public const string FlaggedFolder = "__flagged";

    /// <summary>Kein Ordner auf dem Server: die Posteingaenge aller Konten zusammen ("Alle Posteingaenge").</summary>
    public const string UnifiedFolder = "__unified";

    private const string NotDeleted =
        " AND NOT EXISTS (SELECT 1 FROM mail_folders mf WHERE mf.account=mail.account AND mf.path=mail.folder AND mf.special IN ('trash','junk'))";

    /// <summary>Prio-Posteingang: Mails als Relevant (true) oder Sonstige einordnen.</summary>
    public static void SetFocus(UserDb db, string account, string folder, IEnumerable<uint> uids, bool focused)
    {
        foreach (var uid in uids)
            db.Exec("UPDATE mail SET focus=$v WHERE account=$a AND folder=$f AND uid=$u",
                ("$v", focused ? 1 : 0), ("$a", account), ("$f", folder), ("$u", (long)uid));
    }

    /// <summary>Ungelesene in Relevant und Sonstige - fuer die Reiter ueber der Liste.</summary>
    public static (int Focused, int Other) FocusUnread(UserDb db, string account, string folder) =>
        db.Query("SELECT COALESCE(SUM(COALESCE(focus,1)=1),0), COALESCE(SUM(focus=0),0) FROM mail WHERE account=$a AND folder=$f AND seen=0",
            r => (r.GetInt32(0), r.GetInt32(1)), ("$a", account), ("$f", folder)).First();

    public static (int Total, int Unread) FlaggedCounts(UserDb db, string account) =>
        db.Query($"SELECT COUNT(*), COALESCE(SUM(seen=0),0) FROM mail WHERE account=$a AND flagged=1{NotDeleted}",
            r => (r.GetInt32(0), r.GetInt32(1)), ("$a", account)).First();

    public static (int Total, List<MailRow> Items) List(UserDb db, string account, string folder, int page, int size, string? filter)
    {
        var scope = folder == FlaggedFolder ? "account=$a AND flagged=1" + NotDeleted : "account=$a AND folder=$f";
        var where = scope + filter switch
        {
            "unread" => " AND seen=0",
            "flagged" => " AND flagged=1",
            "attachments" => " AND has_att=1",
            // Prio-Posteingang: noch nicht eingeordnet zaehlt als Relevant
            "focused" => " AND COALESCE(focus,1)=1",
            "other" => " AND focus=0",
            // Kategorie: "cat:dm_rechnung"
            var f when f != null && f.StartsWith("cat:") => " AND keywords LIKE $kw",
            _ => ""
        };
        var kw = filter != null && filter.StartsWith("cat:") ? "% " + filter[4..].ToLowerInvariant() + " %" : null;
        var total = Convert.ToInt32(db.Scalar($"SELECT COUNT(*) FROM mail WHERE {where}", ("$a", account), ("$f", folder), ("$kw", kw)));
        var items = db.Query($"SELECT {RowColumns} FROM mail WHERE {where} ORDER BY date DESC LIMIT $n OFFSET $o", ReadRow,
            ("$a", account), ("$f", folder), ("$n", size), ("$o", page * size), ("$kw", kw));
        return (total, items);
    }

    // ---------------- E-Mail-Nachverfolgung ----------------
    // Gekennzeichnete Mails (IMAP \Flagged) sind Aufgaben; das Faelligkeitsdatum steht hier,
    // je Konto nach Message-ID - es bleibt also, wenn die Mail verschoben wird.

    public static void SetFollowUpDue(UserDb db, string account, string folder, IEnumerable<uint> uids, string? due)
    {
        Ensure(db);
        foreach (var uid in uids)
        {
            var mid = db.Scalar("SELECT message_id FROM mail WHERE account=$a AND folder=$f AND uid=$u",
                ("$a", account), ("$f", folder), ("$u", (long)uid)) as string;
            if (string.IsNullOrEmpty(mid)) continue;
            db.Exec("INSERT INTO mail_followup(account,message_id,due) VALUES($a,$m,$d) ON CONFLICT(account,message_id) DO UPDATE SET due=$d",
                ("$a", account), ("$m", mid), ("$d", due));
        }
    }

    /// <summary>Alle gekennzeichneten Mails (ohne Papierkorb, Junk, Entwuerfe) mit Faelligkeit.</summary>
    public static List<(MailRow Mail, string? Due)> FollowUps(UserDb db, int limit = 500)
    {
        Ensure(db);
        var (_, _, items) = Query(db, new MailCriteria { Flagged = true }, 0, limit);
        var dues = db.Query("SELECT account, message_id, due FROM mail_followup WHERE due IS NOT NULL",
            r => (r.GetString(0) + "\n" + r.GetString(1), r.GetString(2))).ToDictionary(x => x.Item1, x => x.Item2);
        return items.Select(m => (m, m.MessageId != null && dues.TryGetValue(m.Account + "\n" + m.MessageId, out var d) ? d : null)).ToList();
    }

    public const string SearchPrefix = "sf:";
    public const string CategoryPrefix = "cat:";

    /// <summary>Mails nach Kriterien ueber alle Konten - fuer Suchordner und Kategorie-Ordner.</summary>
    public static (int Total, int Unread, List<MailRow> Items) Query(UserDb db, MailCriteria c, int page, int size, string? filter = null)
    {
        var where = new List<string>();
        var args = new List<(string, object?)>();
        string P(object? v) { var n = "$p" + args.Count; args.Add((n, v)); return n; }

        var join = "";
        if (!string.IsNullOrWhiteSpace(c.Text) && FtsQuery(c.Text) is { Length: > 0 } match)
        {
            join = " JOIN mail_fts ON mail_fts.rowid = m.id";
            where.Add("mail_fts MATCH " + P(match));
        }
        if (c.Accounts is { Count: > 0 }) where.Add("m.account IN (" + string.Join(",", c.Accounts.Select(a => P(a))) + ")");
        if (c.Folders is { Count: > 0 })
            where.Add("(" + string.Join(" OR ", c.Folders.Select(f => f.Split('|', 2)).Where(x => x.Length == 2)
                .Select(x => $"(m.account={P(x[0])} AND m.folder={P(x[1])})")) + ")");
        if (c.Categories is { Count: > 0 })
            where.Add("(" + string.Join(" OR ", c.Categories.Select(k => "m.keywords LIKE " + P("% " + k.ToLowerInvariant() + " %"))) + ")");
        if (c.Days is int days && days > 0) where.Add("m.date >= " + P(DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeMilliseconds()));
        if (DateTime.TryParse(c.From, out var from)) where.Add("m.date >= " + P(new DateTimeOffset(from.Date).ToUnixTimeMilliseconds()));
        if (DateTime.TryParse(c.To, out var to)) where.Add("m.date < " + P(new DateTimeOffset(to.Date.AddDays(1)).ToUnixTimeMilliseconds()));
        if (c.Unread || filter == "unread") where.Add("m.seen=0");
        if (c.Flagged || filter == "flagged") where.Add("m.flagged=1");
        if (c.Attachments || filter == "attachments") where.Add("m.has_att=1");
        if (filter != null && filter.StartsWith(CategoryPrefix)) where.Add("m.keywords LIKE " + P("% " + filter[4..].ToLowerInvariant() + " %"));
        if (!c.IncludeDeleted)
            where.Add("NOT EXISTS (SELECT 1 FROM mail_folders mf WHERE mf.account=m.account AND mf.path=m.folder AND mf.special IN ('trash','junk'))");
        // Entwuerfe gehoeren nicht in Suchordner
        where.Add("NOT EXISTS (SELECT 1 FROM mail_folders mf WHERE mf.account=m.account AND mf.path=m.folder AND mf.special='drafts')");

        var w = where.Count == 0 ? "1=1" : string.Join(" AND ", where);
        var (total, unread) = db.Query($"SELECT COUNT(*), COALESCE(SUM(m.seen=0),0) FROM mail m{join} WHERE {w}",
            r => (r.GetInt32(0), r.GetInt32(1)), args.ToArray()).First();
        var all = args.Concat(new (string, object?)[] { ("$n", size), ("$o", page * size) }).ToArray();
        var items = db.Query($"SELECT {Prefixed} FROM mail m{join} WHERE {w} ORDER BY m.date DESC LIMIT $n OFFSET $o", ReadRow, all);
        return (total, unread, items);
    }

    /// <summary>Kriterien fuer einen virtuellen Ordner ("sf:&lt;id&gt;" oder "cat:&lt;schlagwort&gt;"), sonst null.</summary>
    public static MailCriteria? Virtual(UserSettings settings, string folder) =>
        folder.StartsWith(SearchPrefix) ? settings.SearchFolders.FirstOrDefault(f => f.Id == folder[SearchPrefix.Length..])?.Criteria ?? new MailCriteria { Accounts = new() { "-" } } // unbekannt: nichts
        : folder.StartsWith(CategoryPrefix) ? new MailCriteria { Categories = new() { folder[CategoryPrefix.Length..] } }
        : null;

    /// <summary>Volltextsuche, auf Wunsch auf ein Konto und einen Ordner beschraenkt.
    /// Woerter werden als Praefix gesucht; "von:" und "betreff:" schraenken die Spalte ein.</summary>
    /// <param name="withDeleted">Auch Papierkorb und Junk.</param>
    /// <param name="relevance">Nach Treffergüte (BM25) statt nach Datum.</param>
    public static List<MailRow> Search(UserDb db, string query, string? account, string? folder, int limit = 200,
        bool withDeleted = true, bool relevance = false)
    {
        var match = FtsQuery(query);
        if (match.Length == 0) return new();
        var where = "1=1";
        if (!string.IsNullOrEmpty(account)) where += " AND m.account=$a";
        if (!string.IsNullOrEmpty(folder)) where += " AND m.folder=$f";
        if (!withDeleted)
            where += " AND NOT EXISTS (SELECT 1 FROM mail_folders mf WHERE mf.account=m.account AND mf.path=m.folder AND mf.special IN ('trash','junk'))";
        // Betreff und Absender wiegen mehr als der Text
        var order = relevance ? "bm25(mail_fts, 10.0, 5.0, 2.0, 1.0), m.date DESC" : "m.date DESC";
        return db.Query($@"SELECT {Prefixed} FROM mail_fts JOIN mail m ON m.id = mail_fts.rowid
                           WHERE mail_fts MATCH $q AND {where} ORDER BY {order} LIMIT $l", ReadRow,
            ("$q", match), ("$a", account), ("$f", folder), ("$l", limit));
    }

    internal static string FtsQuery(string query)
    {
        var parts = new List<string>();
        foreach (var raw in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var word = raw;
            string? column = null;
            foreach (var (prefix, col) in new[] { ("von:", "sender"), ("from:", "sender"), ("an:", "recipients"), ("to:", "recipients"),
                         ("betreff:", "subject"), ("subject:", "subject") })
                if (word.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { column = col; word = word[prefix.Length..]; }
            word = new string(word.Where(c => char.IsLetterOrDigit(c) || c is '@' or '.' or '-' or '_').ToArray());
            // FTS5 trennt an . @ - selbst; in Anfuehrungszeichen bleibt es eine Wortfolge.
            // Nur Satzzeichen ("-") ergaebe eine leere Wortfolge und damit keinen Treffer.
            if (!word.Any(char.IsLetterOrDigit)) continue;
            var term = "\"" + word + "\"*";
            parts.Add(column == null ? term : column + ":" + term);
        }
        return string.Join(" AND ", parts);
    }

    /// <summary>Dieselbe Suche als IMAP SEARCH fuer den Anbieter (Speichermodus "Schlank"): freie Woerter im
    /// ganzen Text (TEXT), von:/an:/betreff: in ihrem Feld. Null, wenn kein freies Wort dabei ist - Kopfdaten
    /// liegen ohnehin hier, da findet die eigene Suche schon alles.</summary>
    public static MailKit.Search.SearchQuery? ImapQuery(string query)
    {
        var parts = new List<MailKit.Search.SearchQuery>();
        bool free = false;
        foreach (var raw in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var word = raw;
            string? column = null;
            foreach (var (prefix, col) in new[] { ("von:", "sender"), ("from:", "sender"), ("an:", "recipients"), ("to:", "recipients"),
                         ("betreff:", "subject"), ("subject:", "subject") })
                if (word.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { column = col; word = word[prefix.Length..]; }
            word = new string(word.Where(c => char.IsLetterOrDigit(c) || c is '@' or '.' or '-' or '_').ToArray());
            if (!word.Any(char.IsLetterOrDigit)) continue;
            free |= column == null;
            parts.Add(column switch
            {
                "sender" => MailKit.Search.SearchQuery.FromContains(word),
                "recipients" => MailKit.Search.SearchQuery.ToContains(word).Or(MailKit.Search.SearchQuery.CcContains(word)),
                "subject" => MailKit.Search.SearchQuery.SubjectContains(word),
                _ => MailKit.Search.SearchQuery.MessageContains(word),
            });
        }
        return free ? parts.Aggregate((a, b) => a.And(b)) : null;
    }

    /// <summary>Mail per Message-ID (mit oder ohne spitze Klammern) - Posteingang zuerst, sonst die neueste.</summary>
    public static MailRow? ByMessageId(UserDb db, string account, string messageId)
    {
        var mid = messageId.Trim().Trim('<', '>');
        return db.Query($"SELECT {RowColumns} FROM mail WHERE account=$a AND (message_id=$m OR message_id=$b) ORDER BY folder='INBOX' DESC, date DESC LIMIT 1",
            ReadRow, ("$a", account), ("$m", mid), ("$b", "<" + mid + ">")).FirstOrDefault();
    }

    public static MailRow? Row(UserDb db, string account, string folder, uint uid) =>
        db.Query($"SELECT {RowColumns} FROM mail WHERE account=$a AND folder=$f AND uid=$u", ReadRow,
            ("$a", account), ("$f", folder), ("$u", (long)uid)).FirstOrDefault();

    public static byte[]? Raw(UserDb db, string account, string folder, uint uid) =>
        db.Scalar("SELECT r.raw FROM mail m JOIN mail_raw r ON r.id = m.id WHERE m.account=$a AND m.folder=$f AND m.uid=$u", ("$a", account), ("$f", folder), ("$u", (long)uid)) is byte[] gz
            ? Decompress(gz) : null;

    /// <summary>Gespeicherte Mails eines Ordners - fuer das Lernen des Spamfilters.</summary>
    public static IEnumerable<(string Account, string Folder, uint Uid, byte[] Raw)> Cached(UserDb db, string? special, int limit)
    {
        var rows = db.Query(@"SELECT m.account, m.folder, m.uid FROM mail m JOIN mail_folders f ON f.account=m.account AND f.path=m.folder
                              WHERE f.special IS $s AND m.cached=1 ORDER BY m.date DESC LIMIT $l",
            r => (r.GetString(0), r.GetString(1), (uint)r.GetInt64(2)), ("$s", special), ("$l", limit));
        foreach (var (a, f, u) in rows)
            if (Raw(db, a, f, u) is { } raw) yield return (a, f, u, raw);
    }

    // ---------------- Hilfen ----------------

    private static List<Addr> Addrs(InternetAddressList? list) =>
        list?.Mailboxes.Select(a => new Addr(string.IsNullOrEmpty(a.Name) ? null : a.Name, a.Address)).ToList() ?? new();

    public static string BodyText(MimeMessage m)
    {
        var text = m.TextBody;
        if (string.IsNullOrWhiteSpace(text) && m.HtmlBody is { } html) text = MailHtml.ToText(html);
        return (text ?? "").Replace("\r", "");
    }

    private static string Preview(string text)
    {
        // Zitate und Leerzeilen am Anfang zaehlen nicht
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('>'));
        var p = string.Join(" ", lines);
        return p.Length > 200 ? p[..200] : p;
    }

    public static byte[] Compress(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true)) gz.Write(data);
        return ms.ToArray();
    }

    public static byte[] Decompress(byte[] data)
    {
        using var gz = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        return ms.ToArray();
    }
}
