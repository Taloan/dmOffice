using DmOffice.Core;
using MimeKit;

namespace DmOffice.Mail;

/// <summary>
/// Prio-Posteingang wie "Relevant/Sonstige" in Outlook: jede neue Mail im Posteingang bekommt focus = 1 (Relevant)
/// oder 0 (Sonstige). Entscheidet nach Inhalt und Absender, in dieser Reihenfolge:
///   1. fest gemerkter Absender ("Immer nach Relevant/Sonstige verschieben")
///   2. VIP-Kontakt -> Relevant
///   3. gelernte Woerter (wie der Spamfilter, eigene Tabelle) - sobald je Seite genug gelernt ist und das Ergebnis eindeutig
///   4. Verteiler/Newsletter (List-Id, List-Unsubscribe, Precedence, Auto-Submitted, noreply-Absender) -> Sonstige
///   5. bekannter Absender (Adressbuch, selbst angeschrieben) oder persoenlich an mich -> Relevant, sonst Sonstige
/// Verschiebt man eine Mail zwischen den beiden, lernt der Filter daraus.
/// </summary>
public static class Focus
{
    private const int MinTrained = 5;
    private const double UnknownStrength = 0.45, UnknownProb = 0.5, MinStrength = 0.1;

    private static readonly string[] BulkLocal =
        { "noreply", "no-reply", "donotreply", "do-not-reply", "newsletter", "news", "info", "marketing", "mailer-daemon",
          "notification", "notifications", "notify", "updates", "hello", "team", "service", "support", "shop", "angebote", "werbung" };

    public static bool Classify(UserSpace space, MimeMessage m)
    {
        var db = space.Db;
        var from = m.From.Mailboxes.FirstOrDefault()?.Address?.Trim().ToLowerInvariant() ?? "";
        if (from.Length > 0 && Sender(db, from) is bool fixedChoice) return fixedChoice;
        if (from.Length > 0 && Vips(space).Contains(from)) return true;
        if (Score(db, SpamFilter.Tokens(m)) is double p && Math.Abs(p - 0.5) >= 0.3) return p < 0.5;
        if (IsBulk(m, from)) return false;
        if (SafeSenders.IsKnown(space, m)) return true;
        return IsPersonal(space, m);
    }

    /// <summary>Ohne Kopfzeilen (alte Mails ohne Inhalt hier): nur nach Absender.</summary>
    public static bool ClassifyRow(UserSpace space, MailRow r)
    {
        var from = r.From?.Address?.Trim().ToLowerInvariant() ?? "";
        if (from.Length > 0 && Sender(space.Db, from) is bool fixedChoice) return fixedChoice;
        if (from.Length > 0 && Vips(space).Contains(from)) return true;
        if (BulkSender(from)) return false;
        return from.Length > 0 && SafeSenders.Known(space).Contains(from);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<UserDb, (DateTime At, HashSet<string> Set)> VipCache = new();

    /// <summary>VIP-Adressen, zwei Minuten zwischengespeichert - beim Einordnen vieler Mails nicht jedes Mal alle Kontakte lesen.</summary>
    private static HashSet<string> Vips(UserSpace space)
    {
        if (VipCache.TryGetValue(space.Db, out var c) && DateTime.UtcNow - c.At < TimeSpan.FromMinutes(2)) return c.Set;
        var set = Pim.ContactModel.VipAddresses(space);
        VipCache[space.Db] = (DateTime.UtcNow, set);
        return set;
    }

    public static bool IsBulk(MimeMessage m, string from)
    {
        var h = m.Headers;
        if (!string.IsNullOrEmpty(h[HeaderId.ListId]) || !string.IsNullOrEmpty(h[HeaderId.ListUnsubscribe])) return true;
        var prec = (h[HeaderId.Precedence] ?? "").Trim().ToLowerInvariant();
        if (prec is "bulk" or "list" or "junk") return true;
        var auto = (h["Auto-Submitted"] ?? "").Trim().ToLowerInvariant();
        if (auto.Length > 0 && auto != "no") return true;
        if (!string.IsNullOrEmpty(h["X-Campaign"]) || !string.IsNullOrEmpty(h["X-Mailgun-Tag"]) || !string.IsNullOrEmpty(h["Feedback-ID"])) return true;
        return BulkSender(from);
    }

    private static bool BulkSender(string from)
    {
        var at = from.IndexOf('@');
        if (at <= 0) return false;
        var local = from[..at];
        return BulkLocal.Any(b => local == b || local.StartsWith(b + ".") || local.StartsWith(b + "-") || local.StartsWith(b + "_"));
    }

    /// <summary>Direkt an eines meiner Konten und hoechstens fuenf Empfaenger: persoenlich.</summary>
    private static bool IsPersonal(UserSpace space, MimeMessage m)
    {
        var mine = space.Settings.MailAccounts.Select(a => a.Email.ToLowerInvariant()).ToHashSet();
        var to = m.To.Mailboxes.Select(x => x.Address.ToLowerInvariant()).ToList();
        return to.Any(mine.Contains) && to.Count + m.Cc.Mailboxes.Count() <= 5;
    }

    // ---------------- Fest gemerkte Absender ----------------

    public static bool? Sender(UserDb db, string address) =>
        db.Scalar("SELECT focused FROM focus_senders WHERE address=$a", ("$a", address)) is long v ? v != 0 : null;

    public static void SetSender(UserDb db, string address, bool focused) =>
        db.Exec("INSERT INTO focus_senders(address, focused) VALUES($a,$f) ON CONFLICT(address) DO UPDATE SET focused=$f",
            ("$a", address.Trim().ToLowerInvariant()), ("$f", focused ? 1 : 0));

    // ---------------- Lernen nach Inhalt (wie SpamFilter, p = Wahrscheinlichkeit "Sonstige") ----------------

    private static (int Other, int Focused) Counts(UserDb db) =>
        (int.TryParse(db.Get("focus.nother"), out var o) ? o : 0, int.TryParse(db.Get("focus.nfocused"), out var f) ? f : 0);

    public static double? Score(UserDb db, HashSet<string> tokens)
    {
        var (nother, nfocus) = Counts(db);
        if (nother < MinTrained || nfocus < MinTrained) return null;
        var counts = new Dictionary<string, (int Other, int Focused)>();
        using (var conn = db.Open())
            foreach (var chunk in tokens.Chunk(400))
            {
                using var cmd = conn.CreateCommand();
                var names = chunk.Select((_, i) => "$t" + i).ToList();
                cmd.CommandText = $"SELECT token, other, focused FROM focus_tokens WHERE token IN ({string.Join(",", names)})";
                for (int i = 0; i < chunk.Length; i++) cmd.Parameters.AddWithValue(names[i], chunk[i]);
                using var r = cmd.ExecuteReader();
                while (r.Read()) counts[r.GetString(0)] = (r.GetInt32(1), r.GetInt32(2));
            }
        var probs = new List<double>();
        foreach (var t in tokens)
        {
            counts.TryGetValue(t, out var c);
            double oRatio = (double)c.Other / nother, fRatio = (double)c.Focused / nfocus;
            double p = oRatio + fRatio == 0 ? UnknownProb : oRatio / (oRatio + fRatio);
            int n = c.Other + c.Focused;
            double f = (UnknownStrength * UnknownProb + n * p) / (UnknownStrength + n);
            if (Math.Abs(f - 0.5) >= MinStrength) probs.Add(Math.Clamp(f, 0.01, 0.99));
        }
        if (probs.Count == 0) return null;
        probs = probs.OrderByDescending(p => Math.Abs(p - 0.5)).Take(150).ToList();
        // Mittel der Log-Odds - einfacher als chi-combining, reicht fuer zwei Faecher
        var logit = probs.Average(p => Math.Log(p / (1 - p)));
        return 1 / (1 + Math.Exp(-logit * 3));
    }

    /// <summary>Mail als Relevant (focused) bzw. Sonstige lernen; schon so gelernt: nichts, als Gegenteil: umlernen.</summary>
    public static void Train(UserDb db, MimeMessage m, bool focused)
    {
        var key = SpamFilter.KeyOf(m);
        var before = db.Scalar("SELECT focused FROM focus_trained WHERE key=$k", ("$k", key));
        if (before is long b && (b != 0) == focused) return;
        var tokens = SpamFilter.Tokens(m);
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        if (before is long old) Apply(conn, tokens, old != 0, -1);
        Apply(conn, tokens, focused, +1);
        UserDb.Exec(conn, "INSERT INTO focus_trained(key, focused) VALUES($k,$f) ON CONFLICT(key) DO UPDATE SET focused=$f",
            ("$k", key), ("$f", focused ? 1 : 0));
        tx.Commit();
    }

    private static void Apply(Microsoft.Data.Sqlite.SqliteConnection conn, HashSet<string> tokens, bool focused, int delta)
    {
        var col = focused ? "focused" : "other";
        foreach (var t in tokens)
            UserDb.Exec(conn, $@"INSERT INTO focus_tokens(token, {col}) VALUES($t, MAX(0,$d))
                ON CONFLICT(token) DO UPDATE SET {col} = MAX(0, {col} + $d)", ("$t", t), ("$d", delta));
        var key = focused ? "focus.nfocused" : "focus.nother";
        var current = UserDb.Query(conn, "SELECT value FROM kv WHERE key=$k", r => r.GetString(0), ("$k", key)).FirstOrDefault();
        var next = Math.Max(0, (int.TryParse(current, out var c) ? c : 0) + delta);
        UserDb.Exec(conn, "INSERT INTO kv(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v", ("$k", key), ("$v", next.ToString()));
    }

    /// <summary>Noch nicht eingeordnete Mails eines Ordners einordnen - mit Inhalt, wo er hier liegt, sonst nach Absender.</summary>
    public static void Backfill(UserSpace space, string account, string folder, int limit = 500)
    {
        var db = space.Db;
        var rows = db.Query("SELECT uid FROM mail WHERE account=$a AND folder=$f AND focus IS NULL ORDER BY date DESC LIMIT $n",
            r => (uint)r.GetInt64(0), ("$a", account), ("$f", folder), ("$n", limit));
        foreach (var uid in rows)
        {
            bool focused;
            if (MailStore.Raw(db, account, folder, uid) is { } raw)
            {
                try { focused = Classify(space, MimeMessage.Load(new MemoryStream(raw))); }
                catch (FormatException) { continue; }
            }
            else if (MailStore.Row(db, account, folder, uid) is { } row) focused = ClassifyRow(space, row);
            else continue;
            MailStore.SetFocus(db, account, folder, new[] { uid }, focused);
        }
    }
}
