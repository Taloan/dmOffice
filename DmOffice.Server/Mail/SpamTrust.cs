using System.Text.Json;
using DmOffice.Core;
using MimeKit;

namespace DmOffice.Mail;

/// <summary>
/// Sichere Absender, die man nach „Kein Junk“ selbst einträgt (neben Adressbuch und Gesendet, SafeSenders): eine Adresse oder eine
/// ganze Domain („@sparkasse-koelnbonn.de“). Ein Eintrag gilt nur für Mails, die der eigene Mailserver als wirklich von dieser Domain
/// bestätigt hat (DMARC oder passende DKIM-Signatur) - eine gefälschte Absenderadresse bekommt das Vertrauen nicht. Für Freemail-Domains
/// (gmail.com, gmx.de …) gibt es nur einzelne Adressen: Dort kann sich jeder eine Adresse anlegen, und sie bestehen DMARC echt.
/// </summary>
public static class SafeList
{
    private const string Key = "spam.safe";
    public const int Max = 500;

    /// <summary>Domains, bei denen sich jeder eine Adresse anlegen kann.</summary>
    public static readonly HashSet<string> Freemail = new(StringComparer.OrdinalIgnoreCase)
    {
        "gmail.com", "googlemail.com", "gmx.de", "gmx.net", "gmx.at", "gmx.ch", "gmx.com", "web.de", "t-online.de", "outlook.com", "outlook.de",
        "hotmail.com", "hotmail.de", "live.com", "live.de", "live.at", "msn.com", "yahoo.com", "yahoo.de", "ymail.com", "icloud.com", "me.com",
        "mac.com", "aol.com", "aol.de", "mail.de", "freenet.de", "posteo.de", "posteo.net", "mailbox.org", "proton.me", "protonmail.com",
        "pm.me", "arcor.de", "online.de", "email.de", "kabelmail.de", "vodafonemail.de", "chello.at", "aon.at", "a1.net", "bluewin.ch",
        "tutanota.com", "tuta.io", "zoho.com", "zohomail.com", "zohomail.eu", "yandex.com", "mail.ru", "gmx.eu", "1und1.de",
    };

    public static List<string> Of(UserDb db) => db.Get(Key) is { } s ? JsonSerializer.Deserialize<List<string>>(s) ?? [] : [];

    private static void Save(UserDb db, List<string> list) => db.Set(Key, JsonSerializer.Serialize(list));

    /// <summary>Was sich zu einer Mail eintragen ließe: Adresse, registrierbare Domain, ob der Mailserver sie bestätigt hat, ob Freemail.</summary>
    public sealed record Check(string Address, string Domain, bool Verified, bool Freemail, bool AddressListed, bool DomainListed);

    public static Check Inspect(UserDb db, MimeMessage m, SpamSignals.Result r)
    {
        var address = m.From.Mailboxes.FirstOrDefault()?.Address?.Trim().ToLowerInvariant() ?? "";
        var host = PhishCheck.Ascii(PhishCheck.DomainOf(address)) ?? "";
        var domain = host.Length == 0 ? "" : PhishCheck.Registrable(host);
        var list = Of(db);
        // Alias-Dienste (SimpleLogin, Firefox Relay …): die Domain gehört dem Dienst, nicht dem Absender - wie Freemail nur einzelne
        // Adressen; bei Firefox Relay ist die Adresse die eigene Tarnadresse (für alle Absender gleich), da hilft auch die nicht
        var shared = Forwarders.Decode(address, m.From.Mailboxes.FirstOrDefault()?.Name) is { PerSender: false };
        return new Check(address, domain, domain.Length > 0 && r.VerifiedDomain == domain && !shared,
            Freemail.Contains(domain) || Forwarders.ServiceOf(domain) != null, list.Contains(address), list.Contains("@" + domain));
    }

    /// <summary>Gilt ein Eintrag für diese Mail? Nur mit bestätigter Absenderdomain.</summary>
    public static bool Trusts(UserDb db, MimeMessage m, SpamSignals.Result r)
    {
        if (r.VerifiedDomain == null) return false;
        var c = Inspect(db, m, r);
        return c.Verified && (c.AddressListed || (c.DomainListed && !c.Freemail));
    }

    /// <summary>Eintragen (scope „address“ oder „domain“). Wirft mit einem Satz für den Benutzer, wenn es nichts nützen würde.</summary>
    public static string Add(UserDb db, MimeMessage m, SpamSignals.Result r, string scope) => Add(db, Inspect(db, m, r), scope);

    /// <summary>Eintragen nach einer vorher (vom Server) gemachten Prüfung - die Mail kann inzwischen verschoben sein.</summary>
    public static string Add(UserDb db, Check c, string scope)
    {
        if (c.Address.Length == 0) throw new ArgumentException("Die Mail hat keinen Absender.");
        if (!c.Verified) throw new ArgumentException("Mails von dieser Domain lassen sich nicht verifizieren – ein Eintrag hilft hier nicht.");
        string entry;
        if (scope == "domain")
        {
            if (c.Freemail) throw new ArgumentException($"Bei {c.Domain} kann sich jeder eine Adresse anlegen – vertrauen lässt sich dort nur einzelnen Adressen.");
            entry = "@" + c.Domain;
        }
        else entry = c.Address;
        var list = Of(db);
        if (!list.Contains(entry))
        {
            if (list.Count >= Max) throw new ArgumentException($"Höchstens {Max} sichere Absender.");
            list.Add(entry);
            Save(db, list);
        }
        return entry;
    }

    // Prüfungen für „Absender vertrauen“ nach „Kein Junk“: Die Mail wandert dabei in den Posteingang und bekommt eine neue Nummer -
    // eingetragen wird darum, was der Server vorher selbst geprüft hat (15 Minuten, je Benutzer)
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string User, Check Check, DateTime At)> Pending = new();

    public static string Remember(string user, Check c)
    {
        foreach (var old in Pending.Where(x => DateTime.UtcNow - x.Value.At > TimeSpan.FromMinutes(15)).Select(x => x.Key).ToList()) Pending.TryRemove(old, out _);
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));
        Pending[token] = (user, c, DateTime.UtcNow);
        return token;
    }

    public static Check? Recall(string user, string token) =>
        Pending.TryGetValue(token, out var p) && p.User == user && DateTime.UtcNow - p.At <= TimeSpan.FromMinutes(15) ? p.Check : null;

    public static void Remove(UserDb db, string entry)
    {
        var list = Of(db);
        if (list.Remove(entry.Trim().ToLowerInvariant())) Save(db, list);
    }
}

/// <summary>
/// Aus dem Junk-Ordner des Mailanbieters lernen: Was dort seit mindestens drei Tagen liegt und weder schon gelernt noch von dmOffice
/// selbst hineingelegt wurde (sonst lernte der Filter nur sich selbst), wird als Junk gelernt. So kommt der Filter schnell zu
/// Hunderten frischer Beispiele aus den großen Filtern von GMX, Outlook & Co. Weil deren Filter sich auch irren, bleiben sichere
/// Absender und nachweislich echte Marken-Mails außen vor, und es wird nie mehr als dreimal so viel Junk wie Gutes gelernt.
/// </summary>
public static class JunkLearn
{
    public static readonly TimeSpan Wait = TimeSpan.FromDays(3);
    private const int PerRun = 150;

    private static void Ensure(UserDb db) =>
        db.Exec("CREATE TABLE IF NOT EXISTS spam_moved (key TEXT PRIMARY KEY, at INTEGER NOT NULL)");

    /// <summary>dmOffice hat diese Mail selbst nach Junk verschoben - nicht daraus lernen.</summary>
    public static void MarkMoved(UserDb db, MimeMessage m)
    {
        Ensure(db);
        db.Exec("INSERT OR IGNORE INTO spam_moved(key, at) VALUES($k, $t)", ("$k", SpamFilter.KeyOf(m)), ("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    }

    private const string LastKey = "spam.junklearn";

    /// <summary>Ist ein Durchgang fällig (eingeschaltet und der letzte länger her als eingestellt)?</summary>
    public static bool Due(UserSpace space, DateTimeOffset now)
    {
        var spam = space.Settings.Spam;
        if (!spam.Enabled || spam.JunkFolderHours <= 0) return false;
        var last = long.TryParse(space.Db.Get(LastKey), out var s) ? DateTimeOffset.FromUnixTimeSeconds(s) : DateTimeOffset.MinValue;
        return now - last >= TimeSpan.FromHours(spam.JunkFolderHours);
    }

    /// <summary>
    /// Im Junk-Ordner selbst gelöscht: gleich als Junk lernen - Löschen heißt dort „das ist Müll“. Wie beim Lernen aus dem Ordner
    /// nicht bei sicheren Absendern und nachweislich echten Mails großer Marken, und nicht mehr als dreimal so viel Junk wie gute
    /// Mails (mindestens 100). Gibt zurück, ob gelernt wurde.
    /// </summary>
    public static bool LearnDeleted(UserSpace space, MimeMessage m)
    {
        if (!space.Settings.Spam.Enabled) return false;
        var (spamCount, hamCount) = SpamFilter.Counts(space.Db);
        if (spamCount >= Math.Max(3 * hamCount, 100)) return false;
        var r = SpamSignals.Of(m);
        if (r.VerifiedBrand || SpamSignals.Trusted(space, m, r).Trusted) return false;
        SpamFilter.Train(space.Db, m, spam: true);
        return true;
    }

    /// <summary>Ein Durchgang für einen Benutzer; gibt die Zahl der gelernten Mails zurück.</summary>
    public static int Run(UserSpace space, DateTimeOffset? now = null)
    {
        var spam = space.Settings.Spam;
        if (!spam.Enabled || spam.JunkFolderHours <= 0) return 0;
        var db = space.Db;
        Ensure(db);
        db.Exec("CREATE TABLE IF NOT EXISTS spam_trained (key TEXT PRIMARY KEY, as_spam INTEGER NOT NULL)");
        var t = now ?? DateTimeOffset.UtcNow;
        db.Set(LastKey, t.ToUnixTimeSeconds().ToString());
        var before = (t - Wait).ToUnixTimeMilliseconds();
        var after = (t - TimeSpan.FromDays(60)).ToUnixTimeMilliseconds();
        var rows = db.Query(@"SELECT m.account, m.folder, m.uid FROM mail m JOIN mail_folders f ON f.account = m.account AND f.path = m.folder
                              WHERE f.special = 'junk' AND m.cached = 1 AND m.date < $b AND m.date > $a ORDER BY m.date DESC LIMIT 2000",
            r => (r.GetString(0), r.GetString(1), (uint)r.GetInt64(2)), ("$b", before), ("$a", after));
        int learned = 0;
        var (spamCount, hamCount) = SpamFilter.Counts(db);
        foreach (var (acc, folder, uid) in rows)
        {
            // Nicht mehr als dreimal so viel Junk wie gute Mails (mindestens 100) - sonst kippt der Filter Richtung Junk
            if (learned >= PerRun || spamCount >= Math.Max(3 * hamCount, 100)) break;
            if (MailStore.Raw(db, acc, folder, uid) is not { } raw) continue;
            MimeMessage m;
            try { m = MimeMessage.Load(new MemoryStream(raw)); } catch (FormatException) { continue; }
            var key = SpamFilter.KeyOf(m);
            if (db.Scalar("SELECT 1 FROM spam_trained WHERE key=$k UNION ALL SELECT 1 FROM spam_moved WHERE key=$k LIMIT 1", ("$k", key)) != null) continue;
            // Die Filter der Anbieter irren sich oft (Outlook besonders): Mails sicherer Absender und nachweislich echte Mails
            // von Google, Microsoft, PayPal … sind dort meist Fehlgriffe - nicht als Junk lernen
            var r = SpamSignals.Of(m);
            if (r.VerifiedBrand || SpamSignals.Trusted(space, m, r).Trusted) continue;
            SpamFilter.Train(db, m, spam: true);
            learned++;
            spamCount++;
        }
        // Merker nicht ewig aufheben
        db.Exec("DELETE FROM spam_moved WHERE at < $t", ("$t", t.AddDays(-90).ToUnixTimeSeconds()));
        return learned;
    }
}
