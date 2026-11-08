using System.Text.RegularExpressions;
using DmOffice.Core;
using MimeKit;

namespace DmOffice.Mail;

/// <summary>
/// Signale, die der lernende Spamfilter aus Wörtern allein nicht sieht: Hat der eigene Mailserver bestätigt, dass die Mail
/// wirklich von der Absenderdomain kommt (DMARC, DKIM), ist der Name verschleiert, steht eine Zufallskennung im Betreff, schreibt
/// ein kostenloses Postfach unter einem Firmennamen („Cloud-Support-Team“ von zohomail.com), ist die Mail gar nicht an einen
/// selbst adressiert, hält der eigene Mailserver sie für Spam? Sie gehen als Merkmale „sig:…“ in den Filter (er lernt, wie viel
/// sie taugen) und greifen ohne Lernen: bestätigte Absender sind nicht „unsicher“, die verdächtigen mindestens „unsicher“.
/// Den Absendernamen auf Marken prüfen wir bewusst nicht (fast jeder Spam nennt eine Marke).
/// </summary>
public static partial class SpamSignals
{
    /// <param name="VerifiedDomain">Absenderdomain, die der eigene Mailserver per DMARC oder passender DKIM-Signatur bestätigt hat</param>
    public sealed record Result(string? Dmarc, bool DmarcFailed, List<string> DkimDomains, string? VerifiedDomain, bool VerifiedBrand,
        bool Obfuscated, bool RandomId, bool List)
    {
        /// <summary>Kostenloses Postfach (gmail.com, zohomail.com …), aber der Name klingt nach Firma oder Dienst.</summary>
        public bool FreemailOrg { get; init; }
        /// <summary>Weder in „An“ noch in „Cc“ steht eine eigene Adresse (und kein Verteiler) - typisch für Massenversand per Bcc.
        /// Nur in <see cref="Judge"/> gesetzt (braucht die eigenen Adressen).</summary>
        public bool NotToMe { get; init; }
        /// <summary>Der eigene Mailserver hat die Mail als Spam gekennzeichnet (X-Spam-Flag: YES).</summary>
        public bool ServerSpam { get; init; }
        /// <summary>So viele andere haben diese Mail als Junk gemeldet (gemeinsames Junk-Wissen, JunkPrints).</summary>
        public int SharedReports { get; init; }
        /// <summary>Zutreffende zusätzliche Erkennungsregeln (SpamRules, vom Admin gepflegt).</summary>
        public List<SpamRule> Rules { get; init; } = [];
        /// <summary>Stärkste feste Wirkung der zutreffenden Regeln: junk, uncertain oder null.</summary>
        /// <summary>Über einen Alias-Dienst (SimpleLogin …) weitergeleitet: eigentlicher Absender - nur zum Einordnen, nicht bestätigt.</summary>
        public Forwarders.Info? Forwarded { get; init; }
        public SpamRule? StrongestRule => Rules.FirstOrDefault(x => x.Action == "junk") ?? Rules.FirstOrDefault(x => x.Action == "uncertain");
    }

    /// <param name="rules">Erkennungsregeln; null: die eingeschalteten (SpamRules.Active)</param>
    public static Result Of(MimeMessage m, IEnumerable<string>? ownHosts = null, IReadOnlyList<SpamRule>? rules = null)
    {
        var from = m.From.Mailboxes.FirstOrDefault();
        var fromHost = PhishCheck.Ascii(PhishCheck.DomainOf(from?.Address ?? ""));
        var fromReg = fromHost == null ? null : PhishCheck.Registrable(fromHost);
        var auth = PhishCheck.Auth(m, ownHosts ?? []);
        string? verified = null;
        if (fromReg != null && (auth.Dmarc == "pass" || auth.DkimDomains.Contains(fromReg))) verified = fromReg;
        var brand = verified != null && PhishCheck.Brands.Contains(verified);
        var name = from?.Name ?? "";
        // Alias-Dienst (vom Mailserver bestätigt): Marke am eigentlichen Absender messen, nicht an simplelogin.co
        var fwd = verified != null && Forwarders.ServiceOf(verified) != null ? Forwarders.Decode(from?.Address, name) : null;
        return new Result(auth.Dmarc, auth.Dmarc == "fail" && auth.Arc != "pass", auth.DkimDomains, verified, brand,
            Obfuscated(name), RandomIdRx().IsMatch(m.Subject ?? ""), m.Headers.Contains(HeaderId.ListUnsubscribe))
        { Rules = SpamRules.Hits(m, rules), Forwarded = fwd, FreemailOrg = fwd == null && FreemailOrgName(name, fromReg), ServerSpam = SpamHeaders.SaysSpam(m) };
    }

    /// <summary>Merkmale für den lernenden Filter.</summary>
    public static IEnumerable<string> Tokens(Result r)
    {
        yield return "sig:dmarc=" + (r.Dmarc ?? "none");
        foreach (var d in r.DkimDomains.Take(3)) yield return "sig:dkim:" + d;
        if (r.VerifiedBrand) yield return "sig:verified-brand";
        if (r.FreemailOrg) yield return "sig:freemail-org";
        if (r.NotToMe) yield return "sig:not-to-me";
        if (r.Obfuscated) yield return "sig:obfuscated-name";
        if (r.RandomId) yield return "sig:random-id";
        if (r.List) yield return "sig:list";
        if (r.SharedReports > 0) yield return "sig:shared-junk";
        foreach (var x in r.Rules) yield return "sig:rule:" + x.Id;
    }

    /// <summary>
    /// Wert des lernenden Filters mit den festen Regeln verrechnen. trusted: sicherer Absender (Adressbuch, selbst angeschrieben)
    /// bei „Sichere Absender nie Junk“ - dann auch nicht „unsicher“. own: die Mail kommt von einem eigenen Konto (etwa die
    /// Push-Mail der eigenen FRITZ!Box) - dann keine Prüfung auf verdächtige Absender. Rule: welche Regel gegriffen hat (für „Warum?“).
    /// </summary>
    /// <param name="trustRule">Woher das Vertrauen kommt: „trusted“ (Adressbuch, Gesendet) oder „safe-list“ (selbst eingetragen)</param>
    public static (double Score, string? Rule) Adjust(double score, Result r, bool trusted, bool own, SpamSettings settings, string trustRule = "trusted")
    {
        var sure = Math.Max(0, settings.HamCutoff - 0.01);
        if (trusted) return score > sure ? (sure, trustRule) : (score, null);
        // Verschleierter Name („F R I T Z !“, unsichtbare Zeichen): mindestens unsicher, mit Zufallskennung im Betreff eindeutig
        if (r.Obfuscated && !own)
            return r.RandomId
                ? (Math.Max(score, Math.Max(0.97, settings.SpamCutoff)), "obfuscated-random")
                : (Math.Max(score, Math.Max(0.85, settings.HamCutoff)), "obfuscated");
        // Andere im Haushalt haben dieselbe Mail als Junk gemeldet: ab zwei Meldungen Junk, sonst mindestens unsicher
        if (r.SharedReports >= 2) return (Math.Max(score, Math.Max(0.97, settings.SpamCutoff)), "shared-junk");
        if (r.SharedReports == 1) return (Math.Max(score, Math.Max(0.85, settings.HamCutoff)), "shared-junk");
        // Erkennungsregeln des Admins mit fester Wirkung
        if (r.StrongestRule is { } custom)
            return custom.Action == "junk"
                ? (Math.Max(score, Math.Max(0.97, settings.SpamCutoff)), "custom-junk")
                : (Math.Max(score, Math.Max(0.85, settings.HamCutoff)), "custom-uncertain");
        // Der eigene Mailserver hält sie für Spam: mindestens unsicher
        if (r.ServerSpam && !own) return (Math.Max(score, Math.Max(0.85, settings.HamCutoff)), "server-spam");
        // Kostenloses Postfach unter Firmennamen und gar nicht an einen selbst adressiert: mindestens unsicher
        if (r.FreemailOrg && r.NotToMe && !own) return (Math.Max(score, Math.Max(0.85, settings.HamCutoff)), "freemail-org");
        // Nachweislich von Google, Microsoft, PayPal …: nicht „unsicher“ - was der Filter klar als Junk erkannt hat, bleibt Junk
        if (r.VerifiedBrand && score < settings.SpamCutoff) return score > sure ? (sure, "verified-brand") : (score, null);
        // Gefälschte Absenderadresse (DMARC gescheitert, keine Weiterleitung): mindestens unsicher
        if (r.DmarcFailed) return (Math.Max(score, Math.Max(0.6, settings.HamCutoff)), "dmarc-fail");
        // Absender vom eigenen Mailserver bestätigt (DMARC oder passende DKIM-Signatur, kein Alias-Dienst): ein mittlerer Wert heißt
        // dann meist „unbekannter Newsletter“, nicht Junk - also nicht „unsicher“. Was der Filter deutlich als Junk sieht, bleibt es.
        // Nicht bei kostenlosen Postfächern - dort kann jeder ein Konto anlegen, bestätigt ist dann nur das Postfach
        if (r.VerifiedDomain is { } domain && Forwarders.ServiceOf(domain) == null && !SafeList.Freemail.Contains(domain) && score < VerifiedUnsure)
            return score > sure ? (sure, "verified-sender") : (score, null);
        return (score, null);
    }

    /// <summary>So viele Junk- und gute Mails braucht der lernende Filter, bevor er allein „unsicher“ meldet.</summary>
    public const int Confident = 50;

    public static bool FewLearned(UserDb db) { var (s, h) = SpamFilter.Counts(db); return s < Confident || h < Confident; }

    /// <summary>Bis zu diesem Wert gilt eine Mail mit bestätigtem Absender nicht als „unsicher“.</summary>
    public const double VerifiedUnsure = 0.65;

    /// <summary>Endgültige Einstufung einer Mail eines Kontos: Wert des lernenden Filters (null: noch zu wenig gelernt - dann
    /// zählen nur die festen Regeln) mit Signalen, sicheren und eigenen Absendern.</summary>
    public static (double? Score, string? Rule, Result Signals) Judge(UserSpace space, MailAccount account, MimeMessage m, double? learned)
    {
        var spam = space.Settings.Spam;
        var r = Of(m, [account.ImapHost, PhishCheck.DomainOf(account.Email)]);
        if (JunkPrints.Shared is { } prints) r = r with { SharedReports = prints.Reports(space, m) };
        r = r with { NotToMe = NotToMe(m, space.Settings.MailAccounts.Select(a => a.Email)) };
        var from = m.From.Mailboxes.FirstOrDefault()?.Address?.ToLowerInvariant() ?? "";
        var own = from.Length > 0 && space.Settings.MailAccounts.Any(a => a.Email.Equals(from, StringComparison.OrdinalIgnoreCase));
        // Gesperrter Catchall-Alias (CatchallAliases): Junk, gleich wer schreibt
        if (CatchallAliases.Judge(space.Db, account, m) is { } alias) return (Math.Max(0.99, spam.SpamCutoff), alias.Rule, r);
        var (trusted, trustRule) = Trusted(space, m, r);
        if (learned is double s)
        {
            var (score, rule) = Adjust(s, r, trusted, own, spam, trustRule);
            // Anfangs wie Thunderbird: solange wenig gelernt ist, nur deutlichen Junk melden - ein mittlerer Wert allein vom
            // lernenden Filter ist dann noch kein Grund für „unsicher“ (die festen Regeln oben gelten weiter)
            if (rule == null && score >= spam.HamCutoff && score < spam.SpamCutoff && FewLearned(space.Db))
                return (Math.Max(0, spam.HamCutoff - 0.01), "few-learned", r);
            return (score, rule, r);
        }
        // Ohne genug Gelerntes nur die eindeutigen Regeln (Markenimitation, Meldungen anderer, Erkennungsregeln mit fester Wirkung)
        if ((!own && (r.Obfuscated || r.ServerSpam || r.FreemailOrg && r.NotToMe) || r.SharedReports > 0 || r.StrongestRule != null) && !trusted)
        {
            var (score, rule) = Adjust(0, r, false, own, spam);
            return (score, rule, r);
        }
        return (null, null, r);
    }

    /// <summary>
    /// Sicherer Absender? Adressbuch und Gesendet (bei „Sichere Absender“) - aber nie, wenn die Absenderadresse nachweislich gefälscht
    /// ist (DMARC gescheitert, typisch beim Chef-Betrug mit der Adresse eines Kontakts); dazu die selbst eingetragenen sicheren Absender,
    /// die nur mit bestätigter Absenderdomain gelten (SafeList).
    /// </summary>
    public static (bool Trusted, string Rule) Trusted(UserSpace space, MimeMessage m, Result r)
    {
        if (SafeList.Trusts(space.Db, m, r)) return (true, "safe-list");
        if (space.Settings.Spam.TrustKnown && !r.DmarcFailed && SafeSenders.IsKnown(space, m)) return (true, "trusted");
        return (false, "trusted");
    }

    // ---------------- Einmalige Umstellung ----------------

    private const string UpgradeKey = "spam.signals";

    /// <summary>
    /// Einmal je Benutzer: Die neuen Merkmale „sig:…“ und „srv-…“ (SpamHeaders) zu den schon gelernten Mails nachtragen, die noch hier liegen (Junk als Junk,
    /// Posteingang und Gesendet als gut - so wie gelernt), und den Posteingang neu einstufen. Verschoben wird dabei nichts, nur die
    /// Markierung („unsicher“) richtet sich nach der neuen Einstufung.
    /// </summary>
    public static (int Learned, int Rescored) Upgrade(UserSpace space)
    {
        var db = space.Db;
        if (db.Get(UpgradeKey) == "1") return (0, 0);
        int learned = 0, rescored = 0;
        db.Exec("CREATE TABLE IF NOT EXISTS spam_trained (key TEXT PRIMARY KEY, as_spam INTEGER NOT NULL)");
        foreach (var special in new[] { "junk", "inbox", "sent" })
            foreach (var (_, _, _, raw) in MailStore.Cached(db, special, 3000))
            {
                MimeMessage m;
                try { m = MimeMessage.Load(new MemoryStream(raw)); } catch (FormatException) { continue; }
                if (db.Scalar("SELECT as_spam FROM spam_trained WHERE key=$k", ("$k", SpamFilter.KeyOf(m))) is not long asSpam) continue;
                // Dazu das Urteil des eigenen Mailservers (SpamHeaders, ebenfalls neu)
                SpamFilter.AddTokens(db, Tokens(Of(m)).Concat(SpamHeaders.Tokens(m)), asSpam != 0);
                learned++;
            }
        var spam = space.Settings.Spam;
        var (nspam, nham) = SpamFilter.Counts(db);
        bool active = spam.Enabled && nspam >= spam.MinTrained && nham >= spam.MinTrained;
        if (spam.Enabled)
            foreach (var (acc, folder, uid, raw) in MailStore.Cached(db, "inbox", 2000))
            {
                if (space.Settings.MailAccounts.FirstOrDefault(a => a.Id == acc) is not { } account) continue;
                MimeMessage m;
                try { m = MimeMessage.Load(new MemoryStream(raw)); } catch (FormatException) { continue; }
                // Nur, was schon eingestuft war (oder jetzt eindeutig ist) - händisch als „kein Junk“ markierte (0) bleiben
                var before = db.Scalar("SELECT spam FROM mail WHERE account=$a AND folder=$f AND uid=$u", ("$a", acc), ("$f", folder), ("$u", (long)uid));
                if (before is double b && b == 0) continue;
                var (score, _, _) = Judge(space, account, m, active ? SpamFilter.Score(db, m) : null);
                if (score is double s && (before is not double old || Math.Abs(old - s) > 0.005)) { MailStore.SetSpamScore(db, acc, folder, uid, s); rescored++; }
            }
        db.Set(UpgradeKey, "1");
        return (learned, rescored);
    }

    // ---------------- Absender ----------------

    /// <summary>Wörter, mit denen sich Absendernamen als Firma oder Dienst ausgeben („Cloud-Support-Team“, „Kundenservice“).</summary>
    private static readonly HashSet<string> OrgWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "support", "team", "service", "kundenservice", "kundendienst", "kundenbetreuung", "kundencenter", "kundenkonto", "abteilung",
        "sicherheit", "security", "verwaltung", "administrator", "admin", "helpdesk", "billing", "abrechnung", "buchhaltung", "rechnung",
        "zahlung", "zahlungen", "payments", "cloud", "bank", "versand", "zustellung", "paketdienst", "gewinnspiel", "gewinn", "belohnung",
        "umfrage", "kundenumfrage", "mitgliederservice", "postmaster", "webmaster", "noreply", "notification", "benachrichtigung",
    };

    /// <summary>Kostenloses Postfach, aber der Name klingt nach Firma oder Dienst - Firmen schreiben nicht von gmail.com.</summary>
    internal static bool FreemailOrgName(string name, string? fromDomain) =>
        fromDomain != null && SafeList.Freemail.Contains(fromDomain)
        && WordRx().Matches(name.ToLowerInvariant()).Any(w => OrgWords.Contains(w.Value));

    /// <summary>Weder in „An“ noch in „Cc“ eine der eigenen Adressen, und kein Verteiler (der schreibt die Liste hinein).</summary>
    internal static bool NotToMe(MimeMessage m, IEnumerable<string> own)
    {
        if (m.Headers.Contains(HeaderId.ListUnsubscribe) || m.Headers.Contains(HeaderId.ListId)) return false;
        var mine = own.Where(a => a.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var to = m.To.Mailboxes.Concat(m.Cc.Mailboxes).Select(x => x.Address).ToList();
        return mine.Count > 0 && to.Count > 0 && !to.Any(mine.Contains);
    }

    /// <summary>Verschleierter Name: Einzelbuchstaben mit Abständen, unsichtbare Zeichen, Buchstaben aus anderen Alphabeten zwischen lateinischen.</summary>
    internal static bool Obfuscated(string name) =>
        SpacedRx().IsMatch(name) || name.Any(c => c is '​' or '‌' or '‍' or '⁠' or '﻿' or '­')
        || (name.Any(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z') && name.Any(c => c is >= 'Ͱ' and <= 'Ͽ' or >= 'Ѐ' and <= 'ԯ'));

    [GeneratedRegex(@"(?<![\p{L}\p{N}])\p{L}(?:\s\p{L}){2,}(?![\p{L}\p{N}])")]
    private static partial Regex SpacedRx();

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordRx();

    /// <summary>Zufallskennung im Betreff, wie sie Massenversender zum Umgehen von Filtern anhängen: „__ID:ia3q12854589s6“, „#Ref: 8f3k29dk4“.</summary>
    [GeneratedRegex(@"(?:_{2,}|#|\b)(?:id|ref|nr|no|code|ticket)\s*[:#_-]\s*(?=[a-z0-9]*\d)(?=[a-z0-9]*[a-z])[a-z0-9]{8,}\b", RegexOptions.IgnoreCase)]
    private static partial Regex RandomIdRx();
}

/// <summary>
/// Pflege des Spamfilters im Hintergrund: nach dem Start einmal je Benutzer <see cref="SpamSignals.Upgrade"/> (neue Merkmale nachlernen,
/// Posteingang neu einstufen), danach aus dem Junk-Ordner des Mailanbieters lernen (<see cref="JunkLearn"/>), so oft es jeder eingestellt hat (stündlich nachsehen).
/// </summary>
public sealed class SpamMaintenance(Spaces spaces, ILogger<SpamMaintenance> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(2), stop); } catch (OperationCanceledException) { return; }
        while (!stop.IsCancellationRequested)
        {
            foreach (var space in spaces.All())
            {
                if (stop.IsCancellationRequested) return;
                try
                {
                    var (learned, rescored) = await Task.Run(() => SpamSignals.Upgrade(space), stop);
                    if (learned + rescored > 0) log.LogInformation("Spamfilter {User}: neue Merkmale zu {Learned} Mails nachgelernt, {Rescored} Mails neu eingestuft", space.User.Login, learned, rescored);
                    var junk = JunkLearn.Due(space, DateTimeOffset.UtcNow) ? await Task.Run(() => JunkLearn.Run(space), stop) : 0;
                    if (junk > 0) log.LogInformation("Spamfilter {User}: {Count} Mails aus dem Junk-Ordner gelernt", space.User.Login, junk);
                }
                catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning(e, "Spamfilter-Pflege fuer {User}", space.User.Login); }
            }
            try { await Task.Delay(TimeSpan.FromHours(1), stop); } catch (OperationCanceledException) { return; }
        }
    }
}
