using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DmOffice.Core;
using Microsoft.Data.Sqlite;
using MimeKit;

namespace DmOffice.Mail;

/// <summary>
/// Gemeinsames Junk-Wissen (aus, bis der Admin es einschaltet; jeder kann für sich aussteigen): Klickt jemand „Junk“, merkt sich der
/// Server nur Fingerabdrücke der Mail - geschlüsselte Prüfsummen (HMAC) von Betreff, Absenderdomain, Linkzielen und Textabschnitten,
/// nichts Lesbares. Kommt dieselbe Mail (auch leicht verändert: andere Anrede, andere Zufallskennung) bei jemand anderem an, ist das
/// ein starkes Junk-Signal, ab zwei Meldungen Junk. „Kein Junk“ nimmt die eigene Meldung zurück. Nur ausdrückliche „Junk“-Klicks
/// zählen, nie das automatische Lernen. Die gelernten guten Mails bleiben bei jedem selbst. Meldungen gelten 30 Tage.
/// </summary>
public sealed partial class JunkPrints
{
    /// <summary>Für die statischen Aufrufer (Spamfilter, Mailabruf) - beim Start gesetzt.</summary>
    public static JunkPrints? Shared { get; set; }

    public static readonly TimeSpan Keep = TimeSpan.FromDays(30);
    private const int BottomK = 12, MinShingleMatch = 4, ShingleWords = 4;

    private readonly string _connectionString;
    private readonly byte[] _hmacKey;
    private readonly ServerConfig? _config;

    static JunkPrints() => SQLitePCL.Batteries_V2.Init();

    public JunkPrints(DataPaths paths, Vault vault, ServerConfig config) : this(Path.Combine(paths.Root, "junkprints.db"), LoadKey(paths, vault), config) { }

    internal JunkPrints(string path, byte[] key, ServerConfig? config)
    {
        _config = config;
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Password = "x'" + Convert.ToHexString(key) + "'", DefaultTimeout = 30 }.ToString();
        // Eigener Schlüssel für die Prüfsummen, aus dem der Datenbank abgeleitet
        _hmacKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, key, 32, info: Encoding.UTF8.GetBytes("dmoffice-junkprints"));
        using var conn = Open();
        UserDb.Exec(conn, @"
CREATE TABLE IF NOT EXISTS prints (
    id      INTEGER PRIMARY KEY AUTOINCREMENT,
    user    TEXT    NOT NULL,
    mail    TEXT    NOT NULL,       -- Prüfsumme der Mail-Kennung (zum Zurücknehmen)
    at      INTEGER NOT NULL,
    subject TEXT    NULL,
    sender  TEXT    NULL,
    links   TEXT    NULL,
    UNIQUE (user, mail)
);
CREATE INDEX IF NOT EXISTS prints_subject ON prints(subject);
CREATE INDEX IF NOT EXISTS prints_links ON prints(links);
CREATE TABLE IF NOT EXISTS shingles (hash TEXT NOT NULL, print INTEGER NOT NULL, PRIMARY KEY (hash, print)) WITHOUT ROWID;
CREATE TRIGGER IF NOT EXISTS prints_ad AFTER DELETE ON prints BEGIN DELETE FROM shingles WHERE print = old.id; END;");
    }

    private static byte[] LoadKey(DataPaths paths, Vault vault)
    {
        Directory.CreateDirectory(paths.Keys);
        var file = Path.Combine(paths.Keys, "junkprints.key");
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

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=30000; PRAGMA synchronous=NORMAL;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    /// <summary>Macht dieser Benutzer mit (Admin hat es eingeschaltet, er selbst nicht ausgeschaltet)?</summary>
    public bool On(UserSpace space) =>
        (_config == null || _config.Current.SharedJunk) && space.Settings.Spam.Enabled && space.Settings.Spam.ShareJunk;

    // ---------------- Fingerabdruck ----------------

    public sealed record Print(string? Subject, string? Sender, string? Links, List<string> Shingles);

    private string H(string s) => Convert.ToHexString(HMACSHA256.HashData(_hmacKey, Encoding.UTF8.GetBytes(s)))[..24];

    internal Print Of(MimeMessage m)
    {
        // Betreff ohne Zahlen, Zufallskennungen, „Re:“/„Fwd:“ und Satzzeichen
        var subject = SubjectNoise().Replace((m.Subject ?? "").ToLowerInvariant(), " ");
        subject = string.Join(' ', LetterWords().Matches(subject).Select(x => x.Value));
        var from = m.From.Mailboxes.FirstOrDefault()?.Address ?? "";
        var host = PhishCheck.Ascii(PhishCheck.DomainOf(from));
        var html = m.HtmlBody ?? "";
        var links = LinkHost().Matches(html + " " + (m.TextBody ?? "")).Select(x => PhishCheck.Ascii(x.Groups[1].Value)).OfType<string>()
            .Select(PhishCheck.Registrable).Distinct().Order(StringComparer.Ordinal).Take(20).ToList();
        // Text: Wörter ohne Zahlen, Adressen und Links; Abschnitte aus je vier Wörtern, davon die zwölf mit den kleinsten Prüfsummen
        var text = MailStore.BodyText(m);
        if (text.Length > 50_000) text = text[..50_000];
        text = UrlOrMail().Replace(text.ToLowerInvariant(), " ");
        var words = LetterWords().Matches(text).Select(x => x.Value).Where(w => w.Length >= 2).ToList();
        var shingles = new List<string>();
        if (words.Count >= 12)
        {
            var all = new HashSet<string>();
            for (int i = 0; i + ShingleWords <= words.Count && all.Count < 5000; i++)
                all.Add(H("s|" + string.Join(' ', words.Skip(i).Take(ShingleWords))));
            shingles = all.Order(StringComparer.Ordinal).Take(BottomK).ToList();
        }
        return new Print(subject.Length >= 6 ? H("t|" + subject) : null, host == null ? null : H("d|" + PhishCheck.Registrable(host)),
            links.Count > 0 ? H("l|" + string.Join(',', links)) : null, shingles);
    }

    // ---------------- Melden und Zurücknehmen ----------------

    /// <summary>„Junk“ geklickt: Fingerabdruck merken (ersetzt eine frühere Meldung derselben Mail).</summary>
    public void Report(UserSpace space, MimeMessage m)
    {
        if (!On(space)) return;
        var p = Of(m);
        if (p.Subject == null && p.Shingles.Count == 0) return;
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        var mail = H("m|" + SpamFilter.KeyOf(m));
        UserDb.Exec(conn, "DELETE FROM prints WHERE user = $u AND mail = $m", ("$u", space.User.Id), ("$m", mail));
        var id = UserDb.Query(conn, "INSERT INTO prints(user, mail, at, subject, sender, links) VALUES($u, $m, $t, $s, $d, $l) RETURNING id", r => r.GetInt64(0),
            ("$u", space.User.Id), ("$m", mail), ("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds()), ("$s", p.Subject), ("$d", p.Sender), ("$l", p.Links)).First();
        foreach (var sh in p.Shingles) UserDb.Exec(conn, "INSERT OR IGNORE INTO shingles(hash, print) VALUES($h, $p)", ("$h", sh), ("$p", id));
        UserDb.Exec(conn, "DELETE FROM prints WHERE at < $t", ("$t", DateTimeOffset.UtcNow.Add(-Keep).ToUnixTimeSeconds()));
        tx.Commit();
    }

    /// <summary>„Kein Junk“: die eigene Meldung dieser Mail zurücknehmen.</summary>
    public void Withdraw(UserSpace space, MimeMessage m)
    {
        using var conn = Open();
        UserDb.Exec(conn, "DELETE FROM prints WHERE user = $u AND mail = $m", ("$u", space.User.Id), ("$m", H("m|" + SpamFilter.KeyOf(m))));
    }

    /// <summary>Wie viele andere haben diese Mail (oder eine fast gleiche) in den letzten 30 Tagen als Junk gemeldet?</summary>
    public int Reports(UserSpace space, MimeMessage m)
    {
        if (!On(space)) return 0;
        var p = Of(m);
        using var conn = Open();
        var since = DateTimeOffset.UtcNow.Add(-Keep).ToUnixTimeSeconds();
        var users = new HashSet<string>();
        // Gleicher Betreff und gleicher Absender oder gleiche Linkziele
        if (p.Subject != null)
            foreach (var u in UserDb.Query(conn, @"SELECT DISTINCT user FROM prints WHERE subject = $s AND at >= $t AND user <> $me
                    AND (($d IS NOT NULL AND sender = $d) OR ($l IS NOT NULL AND links = $l))", r => r.GetString(0),
                ("$s", p.Subject), ("$d", p.Sender), ("$l", p.Links), ("$t", since), ("$me", space.User.Id)))
                users.Add(u);
        // Weitgehend gleicher Text (mindestens vier der zwölf Abschnitte)
        if (p.Shingles.Count > 0)
        {
            var names = p.Shingles.Select((_, i) => "$h" + i).ToList();
            var args = p.Shingles.Select((h, i) => ("$h" + i, (object?)h)).Append(("$t", since)).Append(("$me", space.User.Id)).Append(("$n", MinShingleMatch)).ToArray();
            foreach (var u in UserDb.Query(conn, $@"SELECT DISTINCT p.user FROM shingles s JOIN prints p ON p.id = s.print
                    WHERE s.hash IN ({string.Join(',', names)}) AND p.at >= $t AND p.user <> $me
                    GROUP BY s.print HAVING COUNT(*) >= $n", r => r.GetString(0), args))
                users.Add(u);
        }
        return users.Count;
    }

    [GeneratedRegex(@"\b(re|aw|fw|fwd|wg)\s*:|\d+|(?:_{2,}|#)\S*|\b(?:id|ref|nr|no|code)\s*[:#_-]\s*\S+", RegexOptions.IgnoreCase)]
    private static partial Regex SubjectNoise();

    [GeneratedRegex(@"\p{L}+")]
    private static partial Regex LetterWords();

    [GeneratedRegex(@"https?://\S+|www\.\S+|[\w.+-]+@[\w-]+(\.[\w-]+)+")]
    private static partial Regex UrlOrMail();

    [GeneratedRegex(@"https?://([a-z0-9.-]+\.[a-z]{2,})", RegexOptions.IgnoreCase)]
    private static partial Regex LinkHost();
}
