using System.Globalization;
using System.Text;
using DmOffice.Core;
using Microsoft.Data.Sqlite;

namespace DmOffice.Mail;

/// <summary>
/// „Ähnliche Mails“: zu einer Mail die am besten passenden aus allen Konten. Bewertet werden Absender (Adresse, sonst Domain),
/// Wörter in Betreff und Text - je seltener ein Wort im ganzen Postfach, desto mehr zählt es (die Abo-Bezeichnung viel,
/// „Bestellung“ kaum) -, dazu Anhänge und Kategorien. Ergebnis in drei Stufen, innerhalb einer Stufe nach Datum.
/// </summary>
public static class SimilarMail
{
    public const int VerySimilar = 1, Similar = 2, Remote = 3;

    /// <summary>Anteile an der Bewertung (zusammen höchstens 1).</summary>
    private const double SenderShare = 0.35, TextShare = 0.6, ExtraShare = 0.05;
    private const int MaxTerms = 30, MaxCandidates = 400, MaxSameSender = 150;

    public static List<MailRow> Find(UserDb db, string account, string folder, uint uid, bool withDeleted = true, int limit = 100)
    {
        MailStore.Ensure(db);
        using var conn = db.Open();
        var source = Load(conn, "m.account=$a AND m.folder=$f AND m.uid=$u", ("$a", account), ("$f", folder), ("$u", (long)uid)).FirstOrDefault();
        if (source == null) return [];

        // Wie oft kommt ein Wort im ganzen Postfach vor (Anzahl Mails)? fts5vocab liest den Index, ohne etwas zu speichern
        UserDb.Exec(conn, "CREATE VIRTUAL TABLE IF NOT EXISTS temp.mail_fts_vocab USING fts5vocab(main, mail_fts, 'row')");
        var total = Math.Max(1L, Convert.ToInt64(UserDb.Query(conn, "SELECT COUNT(*) FROM mail_fts", r => r.GetInt64(0))[0]));
        var subjectTerms = Tokens(source.Row.Subject).ToHashSet();
        var bodyTerms = Tokens(source.Row.Subject + " " + source.Body).ToHashSet();
        var df = DocFrequency(conn, bodyTerms);

        // Gewicht je Wort: selten = viel (IDF), im Betreff dreifach; sehr häufige Wörter zählen nicht. Wörter, die nur in dieser Mail
        // stehen („USB-Kabel“ in einer Bestellung), findet keine andere - sie bleiben aber im Maßstab: Sonst wäre jede andere
        // Bestellung desselben Shops über die Wörter der Vorlage „sehr ähnlich“. Nummern nur in dieser Mail (Bestellnummer) nicht
        var weights = new Dictionary<string, (double Subject, double Body)>();
        foreach (var term in bodyTerms)
        {
            var n = df.TryGetValue(term, out var d) ? Math.Max(1, d) : 1;
            if (total > 50 && n > total / 4) continue;
            if (n < 2 && term.All(char.IsDigit)) continue;
            var idf = Math.Log((total + 1.0) / (n + 0.5));
            weights[term] = (subjectTerms.Contains(term) ? 3 * idf : 0, idf);
        }
        var top = weights.OrderByDescending(w => w.Value.Subject + w.Value.Body).Take(MaxTerms).ToDictionary(w => w.Key, w => w.Value);
        var selfText = top.Values.Sum(w => w.Subject + w.Body);
        var findable = top.Keys.Where(t => df.TryGetValue(t, out var n) && n >= 2).ToList();

        // Kandidaten: Treffer auf diese Wörter (nach BM25) und Mails desselben Absenders
        var where = "m.id <> $id AND NOT EXISTS (SELECT 1 FROM mail_folders mf WHERE mf.account=m.account AND mf.path=m.folder AND mf.special"
            + (withDeleted ? "='drafts')" : " IN ('drafts','trash','junk'))");
        var ids = new HashSet<long>();
        if (findable.Count > 0)
        {
            var match = string.Join(" OR ", findable.Select(t => "\"" + t + "\""));
            foreach (var id in UserDb.Query(conn, $@"SELECT m.id FROM mail_fts JOIN mail m ON m.id = mail_fts.rowid WHERE mail_fts MATCH $q AND {where}
                    ORDER BY bm25(mail_fts, 10.0, 5.0, 2.0, 1.0) LIMIT {MaxCandidates}", r => r.GetInt64(0), ("$q", match), ("$id", source.Row.Id)))
                ids.Add(id);
        }
        var from = source.Row.From?.Address;
        if (!string.IsNullOrEmpty(from))
            foreach (var id in UserDb.Query(conn, $"SELECT m.id FROM mail m WHERE m.from_addr = $s COLLATE NOCASE AND {where} ORDER BY m.date DESC LIMIT {MaxSameSender}",
                         r => r.GetInt64(0), ("$s", from), ("$id", source.Row.Id)))
                ids.Add(id);
        if (ids.Count == 0) return [];

        var sourceDomain = SenderDomain(from);
        var candidates = Load(conn, $"m.id IN ({string.Join(",", ids)})")
            // Dieselbe Mail in einem anderen Ordner (Kopie, gesendete Antwort an sich selbst) ist nicht „ähnlich“
            .Where(c => source.Row.MessageId == null || c.Row.MessageId != source.Row.MessageId)
            .Select(c => (c.Row, Subject: Tokens(c.Row.Subject).ToHashSet(), Body: Tokens(c.Row.Subject + " " + c.Body).ToHashSet(),
                SameDomain: sourceDomain != null && sourceDomain == SenderDomain(c.Row.From?.Address)))
            .ToList();

        // Vorlage des Absenders: Was in fast allen seinen Mails steht („Deine Bestellung“, „Lieferung“, Fußzeile), zählt kaum -
        // was diese Mail unter seinen Mails auszeichnet (die Abo-Bezeichnung), voll. Sonst wäre jede Bestellung desselben Shops ähnlich
        var own = candidates.Where(c => c.SameDomain).ToList();
        if (own.Count >= 3)
            foreach (var term in top.Keys.ToList())
            {
                var share = own.Count(c => c.Body.Contains(term)) / (double)own.Count;
                var (ws, wb) = top[term];
                top[term] = (ws * (1 - share), wb * (1 - share));
            }
        selfText = top.Values.Sum(w => w.Subject + w.Body);

        var scored = new List<(MailRow Row, double Score)>();
        foreach (var c in candidates)
        {
            double text = 0;
            foreach (var (term, w) in top)
            {
                if (c.Subject.Contains(term)) text += w.Subject;
                if (c.Body.Contains(term)) text += w.Body;
            }
            var cFrom = c.Row.From?.Address;
            double sender = !string.IsNullOrEmpty(from) && string.Equals(from, cFrom, StringComparison.OrdinalIgnoreCase) ? 1
                : c.SameDomain ? 0.6 : 0;
            double extra = (source.Row.HasAttachments && c.Row.HasAttachments ? 0.5 : 0)
                + (source.Row.Keywords.Intersect(c.Row.Keywords, StringComparer.OrdinalIgnoreCase).Any() ? 0.5 : 0);
            var score = SenderShare * sender + TextShare * (selfText > 0 ? Math.Min(1, text / selfText) : 0) + ExtraShare * extra;
            if (score >= 0.2) scored.Add((c.Row, score));
        }
        return scored.OrderByDescending(x => x.Score).Take(limit)
            .Select(x => x.Row with { Similar = x.Score >= 0.6 ? VerySimilar : x.Score >= 0.45 ? Similar : Remote })
            .OrderBy(r => r.Similar).ThenByDescending(r => r.Date).ToList();
    }

    private sealed record Item(MailRow Row, string Body);

    private static List<Item> Load(SqliteConnection conn, string where, params (string, object?)[] args) =>
        UserDb.Query(conn, $"SELECT {MailStore.Prefixed}, COALESCE(x.body, '') FROM mail m LEFT JOIN mail_fts x ON x.rowid = m.id WHERE {where}",
            r => new Item(MailStore.ReadRow(r), r.GetString(MailStore.RowColumns.Split(',').Length)), args);

    private static Dictionary<string, long> DocFrequency(SqliteConnection conn, IEnumerable<string> terms)
    {
        var result = new Dictionary<string, long>();
        foreach (var chunk in terms.Chunk(300))
        {
            var args = chunk.Select((t, i) => ("$t" + i, (object?)t)).ToArray();
            foreach (var (term, n) in UserDb.Query(conn, $"SELECT term, doc FROM temp.mail_fts_vocab WHERE term IN ({string.Join(",", args.Select(a => a.Item1))})",
                         r => (r.GetString(0), r.GetInt64(1)), args))
                result[term] = n;
        }
        return result;
    }

    private static string? SenderDomain(string? address)
    {
        if (string.IsNullOrEmpty(address) || PhishCheck.Ascii(PhishCheck.DomainOf(address)) is not { } host) return null;
        return PhishCheck.Registrable(host);
    }

    /// <summary>Wörter wie der Volltextindex (unicode61, remove_diacritics 2): klein, ohne Akzente, an allem außer Buchstaben
    /// und Ziffern getrennt. Kurze Wörter und reine Zahlen bis drei Stellen tragen nichts bei.</summary>
    internal static IEnumerable<string> Tokens(string text)
    {
        var sb = new StringBuilder();
        foreach (var ch in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch)) { sb.Append(char.ToLowerInvariant(ch)); continue; }
            if (Take(sb) is { } t) yield return t;
            sb.Clear();
        }
        if (Take(sb) is { } last) yield return last;
    }

    private static string? Take(StringBuilder sb)
    {
        if (sb.Length < 3 || sb.Length > 40) return null;
        var t = sb.ToString();
        return t.All(char.IsDigit) && t.Length < 4 ? null : t;
    }
}
