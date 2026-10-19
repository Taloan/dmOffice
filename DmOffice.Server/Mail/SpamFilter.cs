using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DmOffice.Core;
using MimeKit;

namespace DmOffice.Mail;

/// <summary>
/// Lernender Spamfilter nach dem Verfahren von SpamBayes: Woerter werden
/// gezaehlt, je nachdem ob sie in Junk oder in guten Mails vorkamen; eine
/// neue Mail bekommt aus ihren auffaelligsten Woertern (Chi-Quadrat nach
/// Fisher/Robinson) einen Wert zwischen 0 (gut) und 1 (Junk). Je Benutzer,
/// gelernt aus seinen Klicks auf "Junk" und "Kein Junk".
/// </summary>
public static partial class SpamFilter
{
    // Robinsons Werte, wie in SpamBayes voreingestellt
    private const double UnknownStrength = 0.45, UnknownProb = 0.5, MinStrength = 0.1;
    private const int MaxDiscriminators = 150;

    public static (int Spam, int Ham) Counts(UserDb db) =>
        (int.TryParse(db.Get("spam.nspam"), out var s) ? s : 0, int.TryParse(db.Get("spam.nham"), out var h) ? h : 0);

    // ---------------- Woerter ----------------

    public static HashSet<string> Tokens(MimeMessage m)
    {
        var tokens = new HashSet<string>();
        foreach (var w in Words(m.Subject ?? "")) tokens.Add("subject:" + w);
        foreach (var from in m.From.Mailboxes)
        {
            tokens.Add("from:" + from.Address.ToLowerInvariant());
            var at = from.Address.IndexOf('@');
            if (at > 0) tokens.Add("from-domain:" + from.Address[(at + 1)..].ToLowerInvariant());
            if (!string.IsNullOrEmpty(from.Name)) foreach (var w in Words(from.Name)) tokens.Add("from-name:" + w);
        }
        if (m.Headers[HeaderId.XMailer] is { Length: > 0 } mailer) tokens.Add("x-mailer:" + mailer.ToLowerInvariant().Split(' ')[0]);
        tokens.Add("to-count:" + Math.Min(10, m.To.Count + m.Cc.Count));
        tokens.Add("has-html:" + (m.HtmlBody != null));
        tokens.Add("has-text:" + (m.TextBody != null));
        tokens.Add("attachments:" + Math.Min(5, m.Attachments.Count()));
        foreach (var a in m.Attachments.OfType<MimePart>())
            if (Path.GetExtension(a.FileName ?? "") is { Length: > 0 } ext) tokens.Add("attach-ext:" + ext.ToLowerInvariant());

        var html = m.HtmlBody ?? "";
        foreach (Match url in UrlRx().Matches(html + " " + (m.TextBody ?? "")))
        {
            var host = url.Groups[1].Value.ToLowerInvariant();
            tokens.Add("url:" + host);
            var parts = host.Split('.');
            if (parts.Length >= 2) tokens.Add("url-domain:" + string.Join(".", parts[^2..]));
        }

        foreach (var w in Words(MailStore.BodyText(m).Length > 50_000 ? MailStore.BodyText(m)[..50_000] : MailStore.BodyText(m)))
            tokens.Add(w);
        return tokens;
    }

    private static IEnumerable<string> Words(string text)
    {
        foreach (Match w in WordRx().Matches(text.ToLowerInvariant()))
        {
            var s = w.Value;
            if (s.Length < 3) continue;
            if (s.Length > 20) { yield return "skip:" + s[0] + " " + (s.Length / 10 * 10); continue; }
            if (s.All(char.IsDigit)) continue;
            yield return s;
        }
    }

    // ---------------- Bewerten ----------------

    public static double Score(UserDb db, MimeMessage m) => Score(db, Tokens(m));

    public static double Score(UserDb db, HashSet<string> tokens)
    {
        var (nspam, nham) = Counts(db);
        if (nspam == 0 || nham == 0) return 0.5;

        var counts = Lookup(db, tokens);
        var probs = new List<double>();
        foreach (var t in tokens)
        {
            counts.TryGetValue(t, out var c);
            double spamRatio = (double)c.Spam / nspam, hamRatio = (double)c.Ham / nham;
            double p = spamRatio + hamRatio == 0 ? UnknownProb : spamRatio / (spamRatio + hamRatio);
            int n = c.Spam + c.Ham;
            double f = (UnknownStrength * UnknownProb + n * p) / (UnknownStrength + n);
            if (Math.Abs(f - 0.5) >= MinStrength) probs.Add(Math.Clamp(f, 0.01, 0.99));
        }
        if (probs.Count == 0) return 0.5;
        probs = probs.OrderByDescending(p => Math.Abs(p - 0.5)).Take(MaxDiscriminators).ToList();

        // Wie SpamBayes (chi-combining): p ist je Wort die Junk-Wahrscheinlichkeit.
        // h aus ln(p): nahe 1, wenn die Woerter fuer gut sprechen (kleine p);
        // s aus ln(1-p): nahe 1, wenn sie fuer Junk sprechen (grosse p).
        double sumLnP = probs.Sum(p => Math.Log(p)), sumLnNotP = probs.Sum(p => Math.Log(1 - p));
        double h = 1 - Chi2Q(-2 * sumLnP, 2 * probs.Count);
        double s = 1 - Chi2Q(-2 * sumLnNotP, 2 * probs.Count);
        return (s - h + 1) / 2;   // nahe 1: Junk
    }

    /// <summary>Wahrscheinlichkeit, dass Chi-Quadrat mit v Freiheitsgraden mindestens x2 ist.</summary>
    private static double Chi2Q(double x2, int v)
    {
        double m = x2 / 2, sum = Math.Exp(-m), term = sum;
        for (int i = 1; i < v / 2; i++)
        {
            term *= m / i;
            sum += term;
        }
        return Math.Min(sum, 1.0);
    }

    private static Dictionary<string, (int Spam, int Ham)> Lookup(UserDb db, HashSet<string> tokens)
    {
        var result = new Dictionary<string, (int, int)>();
        using var conn = db.Open();
        foreach (var chunk in tokens.Chunk(400))
        {
            using var cmd = conn.CreateCommand();
            var names = chunk.Select((_, i) => "$t" + i).ToList();
            cmd.CommandText = $"SELECT token, spam, ham FROM spam_tokens WHERE token IN ({string.Join(",", names)})";
            for (int i = 0; i < chunk.Length; i++) cmd.Parameters.AddWithValue(names[i], chunk[i]);
            using var r = cmd.ExecuteReader();
            while (r.Read()) result[r.GetString(0)] = (r.GetInt32(1), r.GetInt32(2));
        }
        return result;
    }

    // ---------------- Lernen ----------------

    public static string KeyOf(MimeMessage m) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(m.MessageId ?? (m.Subject + "|" + m.Date.ToUnixTimeSeconds() + "|" + m.From))))[..32];

    /// <summary>Lernt eine Mail als Junk oder gut. Schon als das Gegenteil gelernt:
    /// erst vergessen. Schon genau so gelernt: nichts.</summary>
    public static void Train(UserDb db, MimeMessage m, bool spam)
    {
        var key = KeyOf(m);
        var before = db.Scalar("SELECT as_spam FROM spam_trained WHERE key=$k", ("$k", key));
        if (before is long b && (b != 0) == spam) return;

        var tokens = Tokens(m);
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        if (before is long old) Apply(conn, tokens, old != 0, -1);
        Apply(conn, tokens, spam, +1);
        UserDb.Exec(conn, "INSERT INTO spam_trained(key, as_spam) VALUES($k,$s) ON CONFLICT(key) DO UPDATE SET as_spam=$s",
            ("$k", key), ("$s", spam ? 1 : 0));
        tx.Commit();
    }

    private static void Apply(Microsoft.Data.Sqlite.SqliteConnection conn, HashSet<string> tokens, bool spam, int delta)
    {
        var col = spam ? "spam" : "ham";
        foreach (var t in tokens)
            UserDb.Exec(conn, $@"INSERT INTO spam_tokens(token, {col}) VALUES($t, MAX(0,$d))
                ON CONFLICT(token) DO UPDATE SET {col} = MAX(0, {col} + $d)", ("$t", t), ("$d", delta));
        var key = spam ? "spam.nspam" : "spam.nham";
        var current = UserDb.Query(conn, "SELECT value FROM kv WHERE key=$k", r => r.GetString(0), ("$k", key)).FirstOrDefault();
        var next = Math.Max(0, (int.TryParse(current, out var c) ? c : 0) + delta);
        UserDb.Exec(conn, "INSERT INTO kv(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v", ("$k", key), ("$v", next.ToString()));
    }

    /// <summary>Einmal aus dem Bestand lernen: Junk-Ordner als Junk, Posteingang
    /// und Gesendet als gut. Nur Mails, die schon vollstaendig hier liegen.</summary>
    public static object TrainFromFolders(UserSpace space)
    {
        int spam = 0, ham = 0;
        foreach (var (_, _, _, raw) in MailStore.Cached(space.Db, "junk", 3000))
        {
            Train(space.Db, MimeMessage.Load(new MemoryStream(raw)), true);
            spam++;
        }
        foreach (var special in new[] { "inbox", "sent" })
            foreach (var (_, _, _, raw) in MailStore.Cached(space.Db, special, special == "inbox" ? 3000 : 1000))
            {
                Train(space.Db, MimeMessage.Load(new MemoryStream(raw)), false);
                ham++;
            }
        return new { spam, ham };
    }

    public static void Reset(UserDb db) =>
        db.Exec("DELETE FROM spam_tokens; DELETE FROM spam_trained; DELETE FROM kv WHERE key IN ('spam.nspam','spam.nham');");

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}'\-$€%]*")]
    private static partial Regex WordRx();

    [GeneratedRegex(@"https?://([a-zA-Z0-9.\-]+)")]
    private static partial Regex UrlRx();
}
