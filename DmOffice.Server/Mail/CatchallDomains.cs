using System.Text.Json;
using DmOffice.Core;

namespace DmOffice.Mail;

/// <summary>
/// Catchall-Aliase je Domain (Kennung catchalldomains, Konzept docs/catchall-domains-konzept.md): Jede Domain gehört zu genau einem
/// Postfach und hat eigene Einstellungen - welche Aliase angenommen werden, ob die Mails im Posteingang bleiben, von wem Antworten
/// kommen. Die frühere Form (ein Postfach für alle Domains, „Unbekannte“, „Antworten mit dem Alias“) wird beim ersten Speichern
/// übernommen (<see cref="Migrate"/>); bis dahin und ohne Kennung leitet <see cref="SitesOf"/> die Domains daraus ab.
/// </summary>
public static partial class CatchallAliases
{
    public sealed class Site
    {
        /// <summary>Domain, klein und in ASCII (Punycode).</summary>
        public string Domain { get; set; } = "";
        /// <summary>Postfach, das die Mails der Domain abholt (MailAccount.Id).</summary>
        public string? Account { get; set; }
        /// <summary>„all“: alle Aliase der Domain annehmen; „known“: nur angelegte, der Rest gleich in Junk.</summary>
        public string Accept { get; set; } = "all";
        /// <summary>Mails bleiben im Posteingang; „Catchall-Aliase“ ist dann nur eine Ansicht (über mail_alias_hit).</summary>
        public bool Inbox { get; set; }
        /// <summary>Antworten von: null = dem Alias, an den die Mail ging; sonst MailAccount.Id eines eingerichteten Mailkontos.</summary>
        public string? ReplyFrom { get; set; }
    }

    /// <summary>Die frühere Form je Domain: bisheriges Postfach, Posteingang aus, bisheriger Wert für „annehmen“; wer „Antworten mit dem
    /// Alias“ aus hatte, antwortet von der Adresse des Postfachs. Es ändert sich nichts von selbst.</summary>
    public static List<Site> Migrate(Config c) =>
        c.Domains.Select(d => new Site
        {
            Domain = d,
            Account = c.Account,
            Accept = c.Unknown == "junk" ? "known" : "all",
            Inbox = false,
            ReplyFrom = c.ReplyAs ? null : c.Account,
        }).ToList();

    /// <summary>Die Domains mit ihren Einstellungen - ohne Kennung oder vor dem ersten Speichern aus der früheren Form.</summary>
    public static List<Site> SitesOf(Config c) =>
        Features.On(Features.CatchallDomains) && c.Sites != null ? c.Sites : Migrate(c);

    public static Site? SiteFor(Config c, string address)
    {
        var at = address.LastIndexOf('@');
        var d = PhishCheck.Ascii((at >= 0 ? address[(at + 1)..] : address).Trim().ToLowerInvariant()) ?? "";
        return SitesOf(c).FirstOrDefault(s => s.Domain == d);
    }

    /// <summary>Domains als Liste speichern. Prüft Domain und Postfach; jede Domain einmal. Die frühere Form wird mitgeführt
    /// (Domains, Account = Postfach der ersten Domain), damit alles ohne Kennung weiter passt. Ungültiges: ArgumentException.</summary>
    public static Config SaveSites(UserDb db, IEnumerable<Site> sites, IEnumerable<MailAccount> accounts)
    {
        var ids = accounts.Select(a => a.Id).ToHashSet();
        var list = new List<Site>();
        foreach (var raw in sites)
        {
            var d = (raw.Domain ?? "").Trim().TrimStart('@').TrimEnd('.').ToLowerInvariant();
            if (d.Length == 0) continue;
            if (PhishCheck.Ascii(d) is not { } ascii || !DomainRx().IsMatch(ascii)) throw new ArgumentException($"„{raw.Domain?.Trim()}“ ist keine Domain.");
            if (list.Any(s => s.Domain == ascii)) throw new ArgumentException($"Die Domain „{ascii}“ steht schon in der Liste.");
            if (string.IsNullOrEmpty(raw.Account) || !ids.Contains(raw.Account)) throw new ArgumentException($"Für „{ascii}“ ein Postfach wählen.");
            if (!string.IsNullOrEmpty(raw.ReplyFrom) && !ids.Contains(raw.ReplyFrom)) throw new ArgumentException("Dieses Mailkonto gibt es nicht.");
            list.Add(new Site
            {
                Domain = ascii,
                Account = raw.Account,
                Accept = raw.Accept == "known" ? "known" : "all",
                Inbox = raw.Inbox,
                ReplyFrom = string.IsNullOrEmpty(raw.ReplyFrom) ? null : raw.ReplyFrom,
            });
        }
        var old = Load(db);
        var c = new Config
        {
            Domains = list.Select(s => s.Domain).ToList(),
            Account = list.FirstOrDefault()?.Account,
            Unknown = list.FirstOrDefault()?.Accept == "known" ? "junk" : "accept",
            BlockAs = old.BlockAs,
            ReplyAs = list.Count > 0 && list.All(s => s.ReplyFrom == null),
            Sites = list,
        };
        db.Set(ConfigKey, JsonSerializer.Serialize(c, Json));
        return c;
    }

    /// <summary>Einstellungen in der Form je Domain - beim ersten Aufruf mit Kennung einmal übernommen und gespeichert.</summary>
    public static Config Migrated(UserDb db)
    {
        var c = Load(db);
        if (!Features.On(Features.CatchallDomains) || c.Sites != null) return c;
        c.Sites = Migrate(c);
        db.Set(ConfigKey, JsonSerializer.Serialize(c, Json));
        return c;
    }

    /// <summary>„Für diese Domain immer so“: Antworten der Domain kommen künftig aus diesem Mailkonto.</summary>
    public static Config SetReplyFrom(UserDb db, string domain, string? accountId, IEnumerable<MailAccount> accounts)
    {
        var c = Migrated(db);
        var site = SitesOf(c).FirstOrDefault(s => s.Domain == domain.Trim().ToLowerInvariant()) ?? throw new KeyNotFoundException("Diese Domain ist nicht eingetragen.");
        site.ReplyFrom = string.IsNullOrEmpty(accountId) ? null : accountId;
        return SaveSites(db, SitesOf(c), accounts);
    }

    /// <summary>Absender einer Antwort auf eine Mail an diesen Alias: (Konto, Alias) oder (Konto aus „Antworten von“, null).
    /// null: nichts Besonderes (Domain unbekannt, Alias gesperrt).</summary>
    public static (string Account, string? From)? ReplySender(UserDb db, MailAccount account, string alias)
    {
        var c = Load(db);
        if (!ActiveFor(c, account.Id) || SiteFor(c, alias) is not { } site || site.Account != account.Id) return null;
        if (site.ReplyFrom is { } other) return (other, null);
        return CanSendAs(db, account, alias) ? (account.Id, alias) : null;
    }

    /// <summary>Alle Aliase (angelegte und nicht angelegte) mit Post und Zustand - für „Von“ beim Schreiben (Eintippen und Filtern).</summary>
    public static List<(string Address, string State, string? Account)> Senders(UserDb db)
    {
        var c = Load(db);
        if (!Features.On(Features.Catchall) || c.Domains.Count == 0) return [];
        Ensure(db);
        var known = List(db).ToDictionary(a => a.Address);
        var all = known.Keys.Concat(db.Query("SELECT DISTINCT alias FROM mail_alias_hit", r => r.GetString(0))).Distinct();
        return all.Select(a => (a, known.TryGetValue(a, out var k) ? k.Blocked != null ? "blocked" : "open" : "unknown", SiteFor(c, a)?.Account))
            .Where(x => x.Item2 != "blocked" && x.Item3 != null).OrderBy(x => x.a).ToList();
    }

    // ---------------- Absender abgelehnt ----------------

    /// <summary>Absender ist ein Catchall-Alias dieser Domain, und aus ihr ging noch keine Mail erfolgreich raus? Dann sendet
    /// /send sofort statt verzögert - lehnt der Mailserver den Absender ab, sieht man es im noch offenen Verfassen-Fenster.</summary>
    public static bool FirstSendAs(UserDb db, MailAccount account, string? from)
    {
        if (string.IsNullOrEmpty(from) || !Features.On(Features.CatchallDomains)) return false;
        return SiteFor(Load(db), from) is { } site && db.Get("mail.aliases.sendok." + site.Domain) == null;
    }

    /// <summary>Senden als Alias der Domain hat geklappt - ab jetzt wieder mit der eingestellten Verzögerung.</summary>
    public static void ConfirmSend(UserDb db, string from)
    {
        if (SiteFor(Load(db), from) is { } site) db.Set("mail.aliases.sendok." + site.Domain, "1");
    }

    /// <summary>Hat der Mailserver den Absender abgelehnt (MAIL FROM, oder „sender“/„not owned“ in der Antwort)?</summary>
    public static bool SenderRejected(Exception e) => e is MailKit.Net.Smtp.SmtpCommandException sc
        && (sc.ErrorCode == MailKit.Net.Smtp.SmtpErrorCode.SenderNotAccepted
            || sc.ErrorCode == MailKit.Net.Smtp.SmtpErrorCode.MessageNotAccepted
               && (sc.Message.Contains("sender", StringComparison.OrdinalIgnoreCase) || sc.Message.Contains("not owned", StringComparison.OrdinalIgnoreCase)
                   || sc.Message.Contains("from address", StringComparison.OrdinalIgnoreCase)));

    // ---------------- Ansicht „Catchall-Aliase“ bei „Auch im Posteingang“ ----------------

    /// <summary>Virtueller Ordner: „ca:@domain“ (alle Mails der Domain) oder „ca:alias@domain“.</summary>
    public const string ViewPrefix = "ca:";

    /// <summary>Kriterien für die Ansicht (über alle Ordner des Postfachs, ohne Papierkorb und Junk) - null, wenn es keine solche
    /// Domain gibt.</summary>
    public static MailCriteria? View(UserDb db, string folder)
    {
        if (!folder.StartsWith(ViewPrefix) || !Features.On(Features.CatchallDomains)) return null;
        var key = folder[ViewPrefix.Length..].Trim().ToLowerInvariant();
        if (SiteFor(Load(db), key) is not { Account: { } account }) return null;
        return new MailCriteria { Accounts = [account], Aliases = [key] };
    }

    /// <summary>Ansichten für die Zähler im Ordnerbereich: je Domain mit „Auch im Posteingang“ die Domain und ihre angelegten Aliase.</summary>
    public static IEnumerable<string> Views(UserDb db)
    {
        var c = Load(db);
        if (!Features.On(Features.CatchallDomains)) yield break;
        var aliases = List(db);
        foreach (var site in SitesOf(c).Where(s => s.Inbox))
        {
            yield return ViewPrefix + "@" + site.Domain;
            foreach (var a in aliases.Where(a => DomainOf(a.Address) == site.Domain)) yield return ViewPrefix + a.Address;
        }
    }
}
