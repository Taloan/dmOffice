using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using DmOffice.Core;
using MailKit;
using MimeKit;

namespace DmOffice.Mail;

/// <summary>
/// Catchall-Aliase über eine eigene Catch-all-Domain (wie SimpleLogin, aber ohne Fremddienst): Der Hoster leitet alles an
/// „…@domain“ an ein Postfach weiter, das dmOffice abholt. Hier steht, welche Aliase es gibt, wofür sie gedacht sind und ob sie
/// gesperrt sind. Beim Abholen (MailSync) wird jede Mail des Postfachs ihrem Alias zugeordnet - auch nach der Weiterleitung, über
/// Delivered-To, X-Original-To, Envelope-To, X-Forwarded-To, An/Cc und zuletzt „Received … for“. Beim Einstufen neuer Mails
/// (MailSync.ProcessOne): gesperrte Aliase und auf Wunsch unbekannte Adressen in Junk oder den Papierkorb (Grund für „Warum?“ in
/// SpamSignals.Judge), sonst in echte IMAP-Ordner - „domain“ für unbekannte, „domain/alias“ je angelegtem Alias (CatchallFolders).
/// Einstellungen im kv „mail.aliases“, Aliase in „mail_alias“, Zuordnungen je Message-ID in „mail_alias_hit“ (CREATE IF NOT EXISTS).
/// </summary>
public static partial class CatchallAliases
{
    public sealed class Config
    {
        /// <summary>Catch-all-Domains, klein (z. B. „example.com“).</summary>
        public List<string> Domains { get; set; } = [];
        /// <summary>Postfach, in dem die Mails ankommen (MailAccount.Id). Leer: aus.</summary>
        public string? Account { get; set; }
        /// <summary>Mails an Adressen, die nicht in der Liste stehen: „accept“ (in den Ordner der Domain) oder „junk“.</summary>
        public string Unknown { get; set; } = "accept";
        /// <summary>Was „Sperren“ mit einem Klick bedeutet: „junk“ oder „delete“ (Papierkorb).</summary>
        public string BlockAs { get; set; } = "junk";
        /// <summary>Antworten auf Mails an einen Catchall-Alias mit dieser als Absender (nur wenn der SMTP-Server des Postfachs das zulässt).</summary>
        public bool ReplyAs { get; set; }
    }

    /// <param name="Blocked">null: offen; „junk“ oder „delete“: gesperrt, neue Mails dorthin</param>
    /// <param name="Auto">Von selbst aufgenommen (frühere Fassung; heute legt nur der Benutzer Aliase an)</param>
    /// <param name="Folder">IMAP-Ordner des Alias („domain/alias“), sobald angelegt</param>
    /// <param name="ExpireKind">Entfernen nach Zeit für neue Mails (MailExpiry.Trash oder Destroy), null: aus</param>
    /// <param name="ExpireDays">… so viele Tage nach dem Eingang</param>
    public sealed record Alias(string Address, string Note, DateTimeOffset Created, string? Blocked, bool Auto, int Count, DateTimeOffset? Last,
        string? Folder = null, string? ExpireKind = null, int? ExpireDays = null);

    /// <summary>Adresse der Domain, die nicht in der Liste steht, aber Post bekommen hat - zum Übernehmen.</summary>
    public sealed record Unknown(string Address, int Count, DateTimeOffset Last);

    public sealed record Sender(string Address, string? Name, int Count);

    /// <param name="Action">„junk“ oder „delete“</param>
    /// <param name="Rule">„alias-blocked“ oder „alias-unknown“ (für „Warum?“)</param>
    public sealed record Verdict(string Alias, string Action, string Rule);

    private const string ConfigKey = "mail.aliases";

    /// <summary>Kopfzeilen mit dem eigentlichen Empfänger, in dieser Reihenfolge - vor An/Cc, weil dort bei Bcc und Verteilern
    /// etwas anderes steht. Received zuletzt (nur „for &lt;…&gt;“).</summary>
    private static readonly string[] Envelope = ["Delivered-To", "X-Original-To", "Envelope-To", "X-Envelope-To", "X-Forwarded-To"];

    /// <summary>Diese Kopfzeilen holt MailSync beim Abholen mit (nur für das Postfach der Catchall-Aliase).</summary>
    public static readonly string[] FetchHeaders = [.. Envelope, "Received"];

    private static void Ensure(UserDb db)
    {
        db.Exec(@"CREATE TABLE IF NOT EXISTS mail_alias (address TEXT PRIMARY KEY, note TEXT NOT NULL DEFAULT '', created INTEGER NOT NULL,
                    blocked TEXT, auto INTEGER NOT NULL DEFAULT 0, folder TEXT, expire_kind TEXT, expire_days INTEGER) WITHOUT ROWID");
        // Spalten, die nach der ersten Fassung dazukamen
        var cols = db.Query("SELECT name FROM pragma_table_info('mail_alias')", r => r.GetString(0)).ToHashSet();
        foreach (var (col, type) in new[] { ("folder", "TEXT"), ("expire_kind", "TEXT"), ("expire_days", "INTEGER") })
            if (!cols.Contains(col)) db.Exec($"ALTER TABLE mail_alias ADD COLUMN {col} {type}");
        db.Exec(@"CREATE TABLE IF NOT EXISTS mail_alias_hit (message_id TEXT PRIMARY KEY, alias TEXT NOT NULL, from_addr TEXT, from_name TEXT,
                    at INTEGER NOT NULL) WITHOUT ROWID");
        db.Exec("CREATE INDEX IF NOT EXISTS mail_alias_hit_alias ON mail_alias_hit(alias, at)");
    }

    // ---------------- Einstellungen ----------------

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static Config Load(UserDb db)
    {
        try { return db.Get(ConfigKey) is { Length: > 0 } s ? JsonSerializer.Deserialize<Config>(s, Json) ?? new() : new(); }
        catch (JsonException) { return new(); }
    }

    /// <summary>Speichern; Domains werden geprüft und vereinheitlicht. Ungültiges: ArgumentException.</summary>
    public static Config Save(UserDb db, Config c, IEnumerable<MailAccount> accounts)
    {
        var domains = new List<string>();
        foreach (var raw in c.Domains)
        {
            var d = raw.Trim().TrimStart('@').TrimEnd('.').ToLowerInvariant();
            if (d.Length == 0) continue;
            if (PhishCheck.Ascii(d) is not { } ascii || !DomainRx().IsMatch(ascii)) throw new ArgumentException($"„{raw.Trim()}“ ist keine Domain.");
            if (!domains.Contains(ascii)) domains.Add(ascii);
        }
        if (!string.IsNullOrEmpty(c.Account) && !accounts.Any(a => a.Id == c.Account)) throw new ArgumentException("Dieses Postfach gibt es nicht.");
        var clean = new Config
        {
            Domains = domains,
            Account = string.IsNullOrEmpty(c.Account) ? null : c.Account,
            Unknown = c.Unknown == "junk" ? "junk" : "accept",
            BlockAs = c.BlockAs == "delete" ? "delete" : "junk",
            ReplyAs = c.ReplyAs,
        };
        db.Set(ConfigKey, JsonSerializer.Serialize(clean, Json));
        return clean;
    }

    /// <summary>Gelten Catchall-Aliase für dieses Postfach?</summary>
    /// <summary>Für dieses Postfach eingerichtet - und in dieser Version freigeschaltet (Release-Liste).</summary>
    public static bool ActiveFor(Config c, string accountId) => Features.On(Features.Catchall) && c.Domains.Count > 0 && c.Account == accountId;

    public static bool ActiveFor(UserDb db, MailAccount account) => ActiveFor(Load(db), account.Id);

    // ---------------- Zuordnung ----------------

    /// <summary>Der Catchall-Alias, an den eine Mail ging (klein), oder null. Erst die Kopfzeilen mit dem eigentlichen Empfänger
    /// (nach einer Weiterleitung steht dort oft noch die ursprüngliche Adresse), dann An/Cc, zuletzt „Received … for“.</summary>
    public static string? Find(IReadOnlyCollection<string> domains, IEnumerable<(string Name, string Value)> headers, IEnumerable<string?> toCc)
    {
        if (domains.Count == 0) return null;
        var list = headers.ToList();
        foreach (var name in Envelope)
            foreach (var (n, v) in list)
                if (n.Equals(name, StringComparison.OrdinalIgnoreCase))
                    foreach (var a in Addresses(v))
                        if (Matches(domains, a)) return a;
        foreach (var a in toCc)
            if (a != null && Normalize(a) is { } x && Matches(domains, x)) return x;
        foreach (var (n, v) in list)
            if (n.Equals("Received", StringComparison.OrdinalIgnoreCase))
                foreach (Match m in ReceivedForRx().Matches(v))
                    if (Normalize(m.Groups[1].Value) is { } x && Matches(domains, x)) return x;
        return null;
    }

    public static string? Of(Config c, MimeMessage m) =>
        Find(c.Domains, m.Headers.Select(h => (h.Field, h.Value)), m.To.Mailboxes.Concat(m.Cc.Mailboxes).Select(x => x.Address));

    public static string? Of(Config c, IMessageSummary s) =>
        Find(c.Domains, s.Headers?.Select(h => (h.Field, h.Value)) ?? [],
            (s.Envelope?.To.Mailboxes ?? []).Concat(s.Envelope?.Cc.Mailboxes ?? []).Select(x => x.Address));

    private static IEnumerable<string> Addresses(string value)
    {
        if (InternetAddressList.TryParse(value, out var list) && list.Mailboxes.Any())
        {
            foreach (var mb in list.Mailboxes) if (Normalize(mb.Address) is { } a) yield return a;
            yield break;
        }
        foreach (Match m in AddressRx().Matches(value)) if (Normalize(m.Value) is { } a) yield return a;
    }

    private static string? Normalize(string address)
    {
        var a = address.Trim().Trim('<', '>').ToLowerInvariant();
        var at = a.LastIndexOf('@');
        return at > 0 && at < a.Length - 1 ? a : null;
    }

    private static bool Matches(IReadOnlyCollection<string> domains, string address) =>
        domains.Contains(PhishCheck.Ascii(address[(address.LastIndexOf('@') + 1)..]) ?? "");

    /// <summary>Beim Abholen: Mails des Postfachs ihrem Catchall-Alias zuordnen (auch unbekannte Adressen, zum Übernehmen).</summary>
    public static void Record(UserDb db, Config c, IEnumerable<IMessageSummary> summaries)
    {
        foreach (var s in summaries)
            if (Of(c, s) is { } alias)
            {
                var from = s.Envelope?.From.Mailboxes.FirstOrDefault();
                Hit(db, c, alias, s.Envelope?.MessageId, from?.Address, from?.Name, s.Envelope?.Date ?? s.InternalDate ?? DateTimeOffset.UtcNow);
            }
    }

    /// <summary>Eine Mail an eine Adresse der Domain merken (Message-ID einmal). Unbekannte kommen nicht von selbst in die Liste -
    /// sonst legte Spam an erfundene Adressen Aliase und Ordner an; man übernimmt sie mit einem Klick (Create).</summary>
    public static void Hit(UserDb db, Config c, string alias, string? messageId, string? fromAddr, string? fromName, DateTimeOffset at)
    {
        Ensure(db);
        if (string.IsNullOrEmpty(messageId)) return;
        db.Exec("INSERT INTO mail_alias_hit(message_id, alias, from_addr, from_name, at) VALUES($m, $a, $fa, $fn, $t) ON CONFLICT(message_id) DO NOTHING",
            ("$m", messageId), ("$a", alias), ("$fa", fromAddr?.ToLowerInvariant()), ("$fn", string.IsNullOrEmpty(fromName) ? null : fromName),
            ("$t", at.ToUnixTimeMilliseconds()));
    }

    /// <summary>Beim Einstufen einer neuen Mail: gesperrter Catchall-Alias (oder unbekannte bei „Junk“)? Dann wohin.</summary>
    public static Verdict? Judge(UserDb db, MailAccount account, MimeMessage m)
    {
        var c = Load(db);
        if (!ActiveFor(c, account.Id) || Of(c, m) is not { } alias) return null;
        var row = Get(db, alias);
        if (row == null) return c.Unknown == "junk" ? new Verdict(alias, "junk", "alias-unknown") : null;
        return row.Blocked is { } b ? new Verdict(alias, b == "delete" ? "delete" : "junk", "alias-blocked") : null;
    }

    /// <summary>Zustand eines Alias für Liste und Lese-Ansicht: „open“, „blocked“ oder „unknown“ (nicht in der Liste).</summary>
    public static string StateOf(UserDb db, string address) => Get(db, address) is { } a ? a.Blocked != null ? "blocked" : "open" : "unknown";

    /// <summary>Für die Mailliste: an welchen Alias jede Mail ging (über die Message-ID), mit Zustand.</summary>
    public static List<MailRow> Tag(UserDb db, List<MailRow> rows)
    {
        var c = Load(db);
        if (c.Domains.Count == 0 || c.Account == null) return rows;
        var ids = rows.Where(r => r.Account == c.Account && r.MessageId != null).Select(r => r.MessageId!).Distinct().ToList();
        if (ids.Count == 0) return rows;
        Ensure(db);
        var found = new Dictionary<string, (string Alias, string State)>();
        foreach (var chunk in ids.Chunk(500))
        {
            var names = chunk.Select((_, i) => "$m" + i).ToArray();
            foreach (var (mid, alias, state) in db.Query($@"SELECT h.message_id, h.alias, CASE WHEN a.address IS NULL THEN 'unknown' WHEN a.blocked IS NULL THEN 'open' ELSE 'blocked' END
                         FROM mail_alias_hit h LEFT JOIN mail_alias a ON a.address=h.alias WHERE h.message_id IN ({string.Join(",", names)})",
                         r => (r.GetString(0), r.GetString(1), r.GetString(2)), chunk.Select((m, i) => (names[i], (object?)m)).ToArray()))
                found[mid] = (alias, state);
        }
        return found.Count == 0 ? rows : rows.Select(r => r.Account == c.Account && r.MessageId != null && found.TryGetValue(r.MessageId, out var a)
            ? r with { Alias = a.Alias, AliasState = a.State } : r).ToList();
    }

    // ---------------- Aliase ----------------

    public static Alias? Get(UserDb db, string address) => List(db, address.Trim().ToLowerInvariant()).FirstOrDefault();

    public static List<Alias> List(UserDb db, string? only = null)
    {
        Ensure(db);
        return db.Query(@"SELECT a.address, a.note, a.created, a.blocked, a.auto,
                            (SELECT COUNT(*) FROM mail_alias_hit h WHERE h.alias=a.address), (SELECT MAX(at) FROM mail_alias_hit h WHERE h.alias=a.address), a.folder,
                            a.expire_kind, a.expire_days
                          FROM mail_alias a" + (only != null ? " WHERE a.address=$a" : "") + " ORDER BY a.address",
            r => new Alias(r.GetString(0), r.GetString(1), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(2)), r.IsDBNull(3) ? null : r.GetString(3),
                r.GetInt64(4) != 0, r.GetInt32(5), r.IsDBNull(6) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(6)), r.IsDBNull(7) ? null : r.GetString(7),
                r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetInt32(9)),
            ("$a", only));
    }

    /// <summary>Adressen der Domain mit Post, die nicht in der Liste stehen (neueste zuerst) - zum Übernehmen.</summary>
    public static List<Unknown> Unknowns(UserDb db, int n = 50)
    {
        Ensure(db);
        return db.Query(@"SELECT h.alias, COUNT(*), MAX(h.at) FROM mail_alias_hit h WHERE NOT EXISTS (SELECT 1 FROM mail_alias a WHERE a.address=h.alias)
                          GROUP BY h.alias ORDER BY 3 DESC LIMIT $n",
            r => new Unknown(r.GetString(0), r.GetInt32(1), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(2))), ("$n", n));
    }

    /// <summary>IMAP-Ordner eines Alias merken (CatchallFolders).</summary>
    public static void SetFolder(UserDb db, string address, string? folder) =>
        db.Exec("UPDATE mail_alias SET folder=$f WHERE address=$a", ("$f", folder), ("$a", address.ToLowerInvariant()));

    /// <summary>IMAP-Ordner der Domain (Mails an unbekannte Adressen), sobald angelegt.</summary>
    public static string? DomainFolder(UserDb db, string domain) => db.Get("mail.aliases.folder." + domain);
    public static void SetDomainFolder(UserDb db, string domain, string folder) => db.Set("mail.aliases.folder." + domain, folder);

    public static string DomainOf(string address) => address[(address.LastIndexOf('@') + 1)..];
    public static string LocalOf(string address) => address[..address.LastIndexOf('@')];

    /// <summary>Wer am häufigsten an diese Adresse schreibt.</summary>
    public static List<Sender> TopSenders(UserDb db, string address, int n = 5)
    {
        Ensure(db);
        return db.Query(@"SELECT from_addr, MAX(from_name), COUNT(*) FROM mail_alias_hit WHERE alias=$a AND from_addr IS NOT NULL
                          GROUP BY from_addr ORDER BY 3 DESC, MAX(at) DESC LIMIT $n",
            r => new Sender(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetInt32(2)), ("$a", address.ToLowerInvariant()), ("$n", n));
    }

    /// <summary>Neue Adresse. Ohne Namen ein zufälliger, lesbarer („fuchs-7k2“). Ohne Domain die erste. Ungültiges: ArgumentException.</summary>
    public static Alias Create(UserDb db, Config c, string? name, string? domain, string? note)
    {
        if (c.Domains.Count == 0) throw new ArgumentException("Erst eine Domain für Catchall-Aliase eintragen.");
        var d = string.IsNullOrWhiteSpace(domain) ? c.Domains[0] : domain.Trim().TrimStart('@').ToLowerInvariant();
        if (!c.Domains.Contains(d)) throw new ArgumentException($"Die Domain „{d}“ ist nicht eingetragen.");
        Ensure(db);
        string local;
        if (string.IsNullOrWhiteSpace(name))
        {
            local = "";
            for (int i = 0; i < 50 && (local.Length == 0 || Get(db, local + "@" + d) != null); i++) local = RandomName();
        }
        else
        {
            local = name.Trim().ToLowerInvariant();
            if (local.Contains('@')) local = local[..local.IndexOf('@')];
            if (!LocalRx().IsMatch(local)) throw new ArgumentException("Erlaubt sind Buchstaben a–z, Ziffern, Punkt, Bindestrich und Unterstrich (höchstens 64 Zeichen, nicht mit Punkt am Anfang oder Ende).");
        }
        var address = local + "@" + d;
        var existing = Get(db, address);
        if (existing is { Auto: false }) throw new ArgumentException($"Die Adresse {address} gibt es schon.");
        // Schon von selbst aufgenommen: jetzt mit Notiz und als selbst angelegt
        db.Exec(@"INSERT INTO mail_alias(address, note, created, auto) VALUES($a, $n, $c, 0)
                  ON CONFLICT(address) DO UPDATE SET note=$n, auto=0",
            ("$a", address), ("$n", (note ?? "").Trim()), ("$c", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        return Get(db, address)!;
    }

    /// <summary>Notiz ändern und/oder sperren (blocked: „junk“, „delete“) bzw. entsperren (leer).</summary>
    public static Alias Update(UserDb db, string address, string? note, string? blocked, bool setBlocked)
    {
        var a = Get(db, address) ?? throw new KeyNotFoundException("Diesen Alias gibt es nicht.");
        if (note != null) db.Exec("UPDATE mail_alias SET note=$n WHERE address=$a", ("$n", note.Trim()), ("$a", a.Address));
        if (setBlocked)
            db.Exec("UPDATE mail_alias SET blocked=$b WHERE address=$a",
                ("$b", blocked is "junk" or "delete" ? blocked : null), ("$a", a.Address));
        return Get(db, a.Address)!;
    }

    /// <summary>Höchstens so viele Tage für Entfernen nach Zeit.</summary>
    public const int MaxExpireDays = 3650;

    /// <summary>Entfernen nach Zeit für neue Mails an diesen Alias: kind MailExpiry.Trash oder Destroy, nach days Tagen; kind leer: aus.
    /// Ungültiges: ArgumentException.</summary>
    public static Alias SetExpiry(UserDb db, string address, string? kind, int? days)
    {
        var a = Get(db, address) ?? throw new KeyNotFoundException("Diesen Alias gibt es nicht.");
        if (string.IsNullOrEmpty(kind)) { db.Exec("UPDATE mail_alias SET expire_kind=NULL, expire_days=NULL WHERE address=$a", ("$a", a.Address)); return Get(db, a.Address)!; }
        if (kind is not (MailExpiry.Trash or MailExpiry.Destroy)) throw new ArgumentException("Unbekannte Art.");
        if (days is not int d || d < 1 || d > MaxExpireDays) throw new ArgumentException($"Tage zwischen 1 und {MaxExpireDays}.");
        db.Exec("UPDATE mail_alias SET expire_kind=$k, expire_days=$d WHERE address=$a", ("$k", kind), ("$d", d), ("$a", a.Address));
        return Get(db, a.Address)!;
    }

    /// <summary>Entfernen nach Zeit für eine neu eingegangene Mail an diesen Alias - null, wenn nichts eingestellt ist.</summary>
    public static MailExpiry.Entry? ExpiryFor(Alias a, string account, string folder, uint uid, MimeMessage m, DateTime now) =>
        a.ExpireKind is { } kind && a.ExpireDays is int days && days > 0
            ? new MailExpiry.Entry { Account = account, Folder = folder, Uid = uid, MessageId = m.MessageId, At = now.AddDays(days), Kind = kind, Subject = m.Subject ?? "" }
            : null;

    public static bool Delete(UserDb db, string address)
    {
        Ensure(db);
        var a = address.Trim().ToLowerInvariant();
        db.Exec("DELETE FROM mail_alias_hit WHERE alias=$a", ("$a", a));
        return db.Exec("DELETE FROM mail_alias WHERE address=$a", ("$a", a)) > 0;
    }

    /// <summary>Darf dieses Postfach als diese Adresse senden? Nur mit „Antworten mit dem Alias“, aus dem Postfach der Domain,
    /// und nur bekannte Adressen der eingetragenen Domains.</summary>
    public static bool CanSendAs(UserDb db, MailAccount account, string address)
    {
        var c = Load(db);
        return c.ReplyAs && ActiveFor(c, account.Id) && Normalize(address) is { } a && Matches(c.Domains, a) && Get(db, a) != null;
    }

    // ---------------- Zufallsnamen ----------------

    /// <summary>Kurze, eindeutige Wörter ohne Umlaute - am Telefon buchstabierbar.</summary>
    private static readonly string[] Words =
    [
        "adler", "ahorn", "amsel", "anker", "birke", "biber", "blitz", "boje", "dachs", "delfin", "eiche", "elch", "falke", "farn",
        "fuchs", "gecko", "hafen", "hase", "hirsch", "igel", "insel", "jaguar", "kiesel", "kiwi", "koala", "komet", "kranich", "lachs",
        "lerche", "linde", "luchs", "marder", "meise", "mond", "moos", "otter", "panda", "pinie", "quelle", "rabe", "regen", "robbe",
        "salbei", "sonne", "specht", "stern", "storch", "tanne", "tiger", "tukan", "ulme", "wal", "welle", "wiesel", "wolke", "zeder",
    ];

    private const string Letters = "abcdefghjkmnpqrstuvwxyz", Digits = "23456789";

    /// <summary>Zufälliger, lesbarer Name: Wort, Bindestrich, Ziffer-Buchstabe-Ziffer („fuchs-7k2“) - ohne verwechselbare Zeichen (0/o, 1/l/i).</summary>
    public static string RandomName() =>
        Words[RandomNumberGenerator.GetInt32(Words.Length)] + "-" + Digits[RandomNumberGenerator.GetInt32(Digits.Length)]
        + Letters[RandomNumberGenerator.GetInt32(Letters.Length)] + Digits[RandomNumberGenerator.GetInt32(Digits.Length)];

    [GeneratedRegex(@"^(?!\.)[a-z0-9._-]{1,64}(?<!\.)$")]
    private static partial Regex LocalRx();

    [GeneratedRegex(@"^(?=.{3,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z0-9-]{2,63}$")]
    private static partial Regex DomainRx();

    [GeneratedRegex(@"[^\s<>""';,()]+@[^\s<>""';,()]+")]
    private static partial Regex AddressRx();

    [GeneratedRegex(@"\bfor\s+<?([^\s<>;]+@[^\s<>;]+?)>?\s*(?:;|$)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex ReceivedForRx();
}
