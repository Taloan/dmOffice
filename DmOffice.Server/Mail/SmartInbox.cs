using System.Collections.Concurrent;
using DmOffice.Core;
using MimeKit;

namespace DmOffice.Mail;

/// <summary>Alte Smart-Kategorie (nur noch für die Übernahme gelesener Einstellungen).</summary>
public sealed class SmartCategory
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Color { get; set; }
}

/// <summary>
/// Lernende Kategorien (Smart Inbox, docs/kategorien-konzept.md): eine Kategorie ist das Etikett an der Mail (IMAP-Schlagwort
/// „dm_…“, bleibt beim Verschieben); Kategorien mit „lernt“ (MailCategory.Smart) setzt dmOffice bei neu eingehenden Mails selbst.
/// Jede Kategorie entscheidet für sich ja/nein - eine Mail kann mehrere tragen:
///   1. Absender fest zugeordnet („Immer für diesen Absender“)
///   2. Absender mindestens zweimal „gehört dazu“ und nie „gehört nicht dazu“ (und umgekehrt) - ebenso die Domain des Absenders
///      (nicht bei Freemail); hinter einem Alias-Dienst (SimpleLogin …) zählt der eigentliche Absender
///   3. Wörter (Kategorie gegen den Rest, wie der Spamfilter: nur die deutlichsten Merkmale, Betreff doppelt, dazu Wortstämme),
///      wenn genug gelernt ist und das Ergebnis eindeutig
///   4. Voreinstellung der mitgelieferten Kategorien (Preset → Rules) - nicht, wenn eine andere Kategorie schon über den
///      gelernten Absender feststeht (sonst bekäme die Mail neben der gewollten noch „Newsletter“ o. Ä.)
/// Gelernt wird nur von Hand: Etikett gesetzt = gehört dazu, entfernt = gehört nicht dazu. Eigene Tabellen (label_*).
/// </summary>
public static class SmartInbox
{
    /// <summary>Mitgelieferte Kategorien: Voreinstellung (Rules), Schlagwort, Name, Farbe.</summary>
    public static readonly (string Preset, string Key, string Name, string Color)[] BuiltIn =
    [
        ("orders", "dm_bestellungen", "Bestellungen & Rechnungen", "#C239B3"), ("travel", "dm_reisen", "Reisen", "#0099BC"),
        ("contracts", "dm_vertraege", "Verträge & Konten", "#5C2E91"), ("newsletter", "dm_newsletter", "Newsletter", "#7A7574"),
        ("social", "dm_sozial", "Soziale Netzwerke", "#0078D4"), ("notify", "dm_benachrichtigungen", "Benachrichtigungen", "#498205"),
    ];

    /// <summary>Gelernt wird erst ab so vielen Mails je Seite, und nur bei eindeutigem Ergebnis.</summary>
    private const int MinTrained = 3;
    private const double MinProb = 0.85;

    private static readonly ConcurrentDictionary<UserDb, bool> Ready = new();

    public static void Ensure(UserDb db)
    {
        if (Ready.ContainsKey(db)) return;
        db.Exec(@"CREATE TABLE IF NOT EXISTS label_tokens (token TEXT NOT NULL, cat TEXT NOT NULL, pos INTEGER NOT NULL DEFAULT 0, neg INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(token, cat)) WITHOUT ROWID;
CREATE TABLE IF NOT EXISTS label_trained (key TEXT NOT NULL, cat TEXT NOT NULL, label INTEGER NOT NULL, PRIMARY KEY(key, cat)) WITHOUT ROWID;
CREATE TABLE IF NOT EXISTS label_senders (address TEXT NOT NULL, cat TEXT NOT NULL, PRIMARY KEY(address, cat)) WITHOUT ROWID;
CREATE TABLE IF NOT EXISTS label_pending (mail INTEGER NOT NULL, cat TEXT NOT NULL, PRIMARY KEY(mail, cat)) WITHOUT ROWID");
        Ready[db] = true;
    }

    /// <summary>Kategorien, die lernen (Schlagwörter).</summary>
    public static List<MailCategory> Learning(UserSettings s) => DmOffice.Settings.Categories.Of(s).Where(c => c.Smart).ToList();

    /// <summary>Was zum Einordnen gebraucht wird - aus der ganzen Mail oder nur aus der Zeile der Liste (ohne Inhalt hier).</summary>
    /// <param name="From">Absender - hinter einem Alias-Dienst (SimpleLogin …) der eigentliche (amazon.de statt simplelogin.co)</param>
    /// <param name="Raw">Absender, wie er in der Mail steht (Antwortadresse des Alias-Dienstes)</param>
    public sealed record Doc(string Key, string From, string FromName, string Subject, string Text, bool Newsletter, bool Notice, string? Kind, string? Raw = null);

    /// <summary>
    /// Eigentlicher Absender hinter einem Alias-Dienst - nur zum Einordnen, nicht als Vertrauen (siehe Forwarders). Ohne Alias-Dienst
    /// die Adresse selbst.
    /// </summary>
    private static (string From, bool Forwarded) Sender(string addr, string? name) =>
        Forwarders.Decode(addr, name) is { } f ? (f.Original, true) : (addr, false);

    public static Doc Of(MimeMessage m, TimeZoneInfo zone)
    {
        var from = m.From.Mailboxes.FirstOrDefault();
        var addr = from?.Address?.Trim().ToLowerInvariant() ?? "";
        var h = m.Headers;
        var auto = (h["Auto-Submitted"] ?? "").Trim().ToLowerInvariant();
        string? kind = null;
        try { kind = SmartMail.Extract(m, zone, m.Date.UtcDateTime).FirstOrDefault()?.Kind; } catch (Exception) { }
        var text = MailStore.BodyText(m);
        var (sender, forwarded) = Sender(addr, from?.Name);
        // Ein Alias-Dienst hängt seinen eigenen Abmeldekopf an (Alias abschalten) - dann sagt er nichts über Newsletter
        var unsubscribe = !forwarded && !string.IsNullOrEmpty(h[HeaderId.ListUnsubscribe]);
        return new Doc(SpamFilter.KeyOf(m), sender, from?.Name ?? "", m.Subject ?? "", text.Length > 4000 ? text[..4000] : text,
            Newsletter: unsubscribe || !string.IsNullOrEmpty(h[HeaderId.ListId]) || NewsLocal(sender),
            Notice: (auto.Length > 0 && auto != "no") || Focus.BulkSender(sender), kind, addr);
    }

    public static Doc Of(MailRow r)
    {
        var addr = r.From?.Address?.Trim().ToLowerInvariant() ?? "";
        var (sender, _) = Sender(addr, r.From?.Name);
        return new Doc(r.MessageId ?? $"{r.Account}|{r.Folder}|{r.Uid}", sender, r.From?.Name ?? "", r.Subject, r.Preview,
            NewsLocal(sender), Focus.BulkSender(sender), null, addr);
    }

    private static readonly string[] NewsLocals = ["newsletter", "news", "marketing", "angebote", "werbung", "promo", "deals", "offers"];
    private static bool NewsLocal(string addr) => addr.IndexOf('@') is > 0 and var at && NewsLocals.Any(n => addr[..at] == n || addr[..at].StartsWith(n + "."));

    private static HashSet<string> Tokens(Doc d)
    {
        var t = new HashSet<string>();
        if (d.From.Length > 0) t.Add("from:" + d.From);
        // Früher Gelerntes hängt noch an der Antwortadresse des Alias-Dienstes
        if (d.Raw is { Length: > 0 } raw && raw != d.From) t.Add("from:" + raw);
        if (Domain(d.From) is { } dom) t.Add("dom:" + dom);
        foreach (var w in SpamFilter.Words(d.FromName)) t.Add("name:" + w);
        // Wortstämme dazu (eigene Merkmale): „Bestellungen“ und „Bestellung“ zählen zusammen, Gelerntes bleibt gültig
        foreach (var w in SpamFilter.Words(d.Subject)) { t.Add("s:" + w); if (Stemmer.Stem(w) is var st && st != w) t.Add("s~" + st); }
        foreach (var w in SpamFilter.Words(d.Text)) { t.Add(w); if (Stemmer.Stem(w) is var st && st != w) t.Add("~" + st); }
        if (d.Newsletter) t.Add("is:newsletter");
        if (d.Notice) t.Add("is:notice");
        if (d.Kind != null) t.Add("kind:" + d.Kind);
        return t;
    }

    private static string? Domain(string addr)
    {
        var at = addr.IndexOf('@');
        if (at <= 0) return null;
        var parts = addr[(at + 1)..].Split('.');
        return parts.Length >= 2 ? string.Join(".", parts[^2..]) : addr[(at + 1)..];
    }

    // ---------------- Voreinstellung der mitgelieferten Kategorien ----------------

    private static readonly string[] SocialDomains = ["facebookmail.com", "facebook.com", "instagram.com", "linkedin.com", "xing.com", "twitter.com", "x.com",
        "tiktok.com", "pinterest.com", "reddit.com", "redditmail.com", "youtube.com", "snapchat.com", "threads.net", "bsky.app", "discord.com", "nextdoor.com"];
    private static readonly string[] TravelDomains = ["booking.com", "airbnb.com", "airbnb.de", "bahn.de", "lufthansa.com", "eurowings.com", "ryanair.com",
        "easyjet.com", "condor.com", "tuifly.com", "tui.com", "expedia.de", "expedia.com", "flixbus.de", "flixbus.com", "hrs.de", "hotels.com", "trivago.de",
        "ab-in-den-urlaub.de", "check24.de", "opodo.de", "skyscanner.net", "sixt.de", "europcar.com", "blablacar.de"];
    private static readonly string[] OrderDomains = ["amazon.de", "amazon.com", "ebay.de", "ebay.com", "paypal.de", "paypal.com", "otto.de", "zalando.de",
        "mediamarkt.de", "saturn.de", "ikea.com", "ikea.de", "lidl.de", "aldi.de", "kaufland.de", "temu.com", "aliexpress.com", "etsy.com", "dhl.de",
        "dpd.de", "hermesworld.com", "myhermes.de", "gls-group.eu", "ups.com", "klarna.com", "apple.com", "galaxus.de", "alternate.de", "cyberport.de"];
    private static readonly string[] TravelWords = ["buchungsbestätigung", "reisebestätigung", "reiseunterlagen", "flugbestätigung", "boarding", "check-in",
        "online-check-in", "ihre reise", "ihr flug", "ihre fahrkarte", "online-ticket", "hotelbuchung", "zimmerreservierung", "booking confirmation",
        "itinerary", "your trip", "your flight", "e-ticket", "reservierungsbestätigung"];
    private static readonly string[] OrderWords = ["bestellung", "bestellbestätigung", "auftragsbestätigung", "versandbestätigung", "zahlungsbestätigung",
        "rechnung", "kaufbeleg", "quittung", "lieferung", "versendet", "zugestellt", "sendung", "gutschrift", "rückerstattung", "erstattung",
        "your order", "order confirmation", "invoice", "receipt", "shipped", "delivered", "refund", "payment received"];
    private static readonly string[] ContractWords = ["vertrag", "kündigung", "tarif", "vertragsänderung", "kundenkonto", "ihr konto", "ihrem konto",
        "passwort", "kennwort", "anmeldung", "sicherheitshinweis", "bestätigen sie", "verifizier", "kontoauszug", "abrechnung", "jahresabrechnung",
        "mitgliedschaft", "versicherung", "police", "datenschutzerklärung", "agb", "nutzungsbedingungen", "abonnement", "abo ",
        "your account", "password", "sign-in", "verify your", "subscription", "terms of service", "privacy policy"];

    /// <summary>Die festen Regeln der mitgelieferten Kategorien (Preset) - greifen, solange für die Kategorie nichts gelernt ist.</summary>
    public static string Rules(Doc d)
    {
        if (d.Kind == "parcel") return "orders";
        if (d.Kind != null) return "travel";
        var host = d.From.IndexOf('@') is > 0 and var at ? d.From[(at + 1)..] : "";
        bool On(string[] list) => host.Length > 0 && list.Any(x => host == x || host.EndsWith("." + x));
        if (On(SocialDomains)) return "social";
        var subject = d.Subject.ToLowerInvariant();
        bool In(string[] words) => words.Any(subject.Contains);
        if (In(TravelWords) || On(TravelDomains) && !In(OrderWords)) return "travel";
        if (In(OrderWords) || On(OrderDomains)) return "orders";
        if (In(ContractWords)) return "contracts";
        if (d.Newsletter) return "newsletter";
        if (d.Notice) return "notify";
        return "";
    }

    // ---------------- Entscheiden ----------------

    /// <summary>Die lernenden Kategorien, die diese Mail bekommt (Schlagwörter, auch mehrere oder keine).</summary>
    public static List<string> Decide(UserSpace space, Doc d)
    {
        var db = space.Db;
        Ensure(db);
        var cats = Learning(space.Settings);
        if (cats.Count == 0) return [];
        var tokens = Tokens(d);
        var stats = TokenStats(db, tokens);
        var trained = TrainedCounts(db);
        var rule = Rules(d);
        var dom = Domain(d.From);
        // Domain als Absender nur, wenn sie für einen Absender steht (nicht gmail.com, nicht der Alias-Dienst selbst)
        var domOk = dom != null && !SafeList.Freemail.Contains(dom) && Forwarders.ServiceOf(dom) == null;
        var bySender = new List<string>();
        var byWords = new List<string>();
        var byPreset = new List<string>();
        foreach (var c in cats)
        {
            bool? yes = null;
            // Absender: der eigentliche hinter einem Alias-Dienst, sonst (früher so gelernt) die Antwortadresse
            var senders = new[] { d.From, d.Raw }.Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList();
            foreach (var a in senders)
                if (yes == null && db.Scalar("SELECT 1 FROM label_senders WHERE address=$a AND cat=$c", ("$a", a!), ("$c", c.Key)) != null) yes = true;
            foreach (var t in senders.Select(a => "from:" + a).Concat(domOk ? ["dom:" + dom] : []))
                if (yes == null && stats.TryGetValue((t, c.Key), out var s))
                {
                    if (s.Pos >= 2 && s.Neg == 0) yes = true;
                    else if (s.Neg >= 2 && s.Pos == 0) yes = false;
                }
            if (yes != null) { if (yes == true) bySender.Add(c.Key); continue; }
            yes = Bayes(c.Key, tokens, stats, trained);
            if (yes != null) { if (yes == true) byWords.Add(c.Key); continue; }
            if (c.Preset is { Length: > 0 } preset && preset == rule) byPreset.Add(c.Key);
        }
        // Steht eine Kategorie über den gelernten Absender fest, kommt keine Voreinstellung dazu
        return [.. bySender, .. byWords, .. (bySender.Count > 0 ? [] : byPreset)];
    }

    private static Dictionary<(string Token, string Cat), (int Pos, int Neg)> TokenStats(UserDb db, HashSet<string> tokens)
    {
        var stats = new Dictionary<(string, string), (int, int)>();
        using var conn = db.Open();
        foreach (var chunk in tokens.Chunk(400))
        {
            using var cmd = conn.CreateCommand();
            var names = chunk.Select((_, i) => "$t" + i).ToList();
            cmd.CommandText = $"SELECT token, cat, pos, neg FROM label_tokens WHERE (pos > 0 OR neg > 0) AND token IN ({string.Join(",", names)})";
            for (int i = 0; i < chunk.Length; i++) cmd.Parameters.AddWithValue(names[i], chunk[i]);
            using var r = cmd.ExecuteReader();
            while (r.Read()) stats[(r.GetString(0), r.GetString(1))] = (r.GetInt32(2), r.GetInt32(3));
        }
        return stats;
    }

    /// <summary>Je Kategorie: gelernte Mails „gehört dazu“ und „gehört nicht dazu“ (Kennung "" = ohne Kategorie gelernt).</summary>
    private static Dictionary<string, (int Pos, int Neg)> TrainedCounts(UserDb db) =>
        db.Query("SELECT cat, COALESCE(SUM(label > 0),0), COALESCE(SUM(label < 0),0) FROM label_trained GROUP BY cat",
            r => (r.GetString(0), r.GetInt32(1), r.GetInt32(2))).ToDictionary(x => x.Item1, x => (x.Item2, x.Item3));

    // Wie der Spamfilter (Robinson): unbekannte Merkmale zählen kaum, schwache (nahe 50 %) gar nicht, nur die deutlichsten
    private const double UnknownStrength = 0.45, MinStrength = 0.1;
    private const int MaxDiscriminators = 30;

    /// <summary>
    /// Kategorie gegen den Rest: „nicht dazu“ sind die ausdrücklich so gelernten Mails und die, die für andere Kategorien
    /// (oder ohne Kategorie) gelernt sind. Je Merkmal, wie stark es für die Kategorie spricht; zusammengerechnet nur die
    /// deutlichsten (Chi-Quadrat wie beim Spamfilter), Betreffwörter doppelt. true/false bei eindeutigem Ergebnis, sonst null.
    /// </summary>
    internal static bool? Bayes(string cat, HashSet<string> tokens, Dictionary<(string Token, string Cat), (int Pos, int Neg)> stats, Dictionary<string, (int Pos, int Neg)> trained)
    {
        var nPos = trained.TryGetValue(cat, out var own) ? own.Pos : 0;
        var nNeg = (trained.TryGetValue(cat, out var o2) ? o2.Neg : 0) + trained.Where(x => x.Key != cat).Sum(x => x.Value.Pos);
        if (nPos < MinTrained || nNeg < MinTrained) return null;
        var byToken = stats.GroupBy(x => x.Key.Token).ToDictionary(g => g.Key, g => g.ToList());
        var probs = new List<(double F, bool Subject)>();
        foreach (var t in tokens)
        {
            if (!byToken.TryGetValue(t, out var rows)) continue;
            var p = rows.Where(x => x.Key.Cat == cat).Sum(x => x.Value.Pos);
            var n = rows.Where(x => x.Key.Cat == cat).Sum(x => x.Value.Neg) + rows.Where(x => x.Key.Cat != cat).Sum(x => x.Value.Pos);
            double posRatio = (double)p / nPos, negRatio = (double)n / nNeg;
            if (posRatio + negRatio == 0) continue;
            var raw = posRatio / (posRatio + negRatio);
            var f = (UnknownStrength * 0.5 + (p + n) * raw) / (UnknownStrength + p + n);
            if (Math.Abs(f - 0.5) >= MinStrength) probs.Add((Math.Clamp(f, 0.01, 0.99), t.StartsWith("s:", StringComparison.Ordinal) || t.StartsWith("s~", StringComparison.Ordinal)));
        }
        if (probs.Count == 0) return null;
        // Die deutlichsten zuerst; ein Betreffwort zählt doppelt
        var top = probs.OrderByDescending(x => Math.Abs(x.F - 0.5)).Take(MaxDiscriminators).SelectMany(x => x.Subject ? new[] { x.F, x.F } : [x.F]).ToList();
        double h = 1 - SpamFilter.Chi2Q(-2 * top.Sum(Math.Log), 2 * top.Count);
        double s = 1 - SpamFilter.Chi2Q(-2 * top.Sum(x => Math.Log(1 - x)), 2 * top.Count);
        var probYes = (s - h + 1) / 2;
        return probYes >= MinProb ? true : 1 - probYes >= MinProb ? false : null;
    }

    // ---------------- Lernen ----------------

    /// <summary>Von Hand: Etikett gesetzt (positive) oder entfernt. Schon so gelernt: nichts; anders gelernt: umlernen.</summary>
    public static void Train(UserDb db, Doc d, string cat, bool positive)
    {
        Ensure(db);
        var label = positive ? 1 : -1;
        var before = db.Scalar("SELECT label FROM label_trained WHERE key=$k AND cat=$c", ("$k", d.Key), ("$c", cat)) is long b ? (int)b : 0;
        if (before == label) return;
        var tokens = Tokens(d);
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        foreach (var t in tokens)
        {
            if (before == 1) UserDb.Exec(conn, "UPDATE label_tokens SET pos = MAX(0, pos - 1) WHERE token=$t AND cat=$c", ("$t", t), ("$c", cat));
            if (before == -1) UserDb.Exec(conn, "UPDATE label_tokens SET neg = MAX(0, neg - 1) WHERE token=$t AND cat=$c", ("$t", t), ("$c", cat));
            UserDb.Exec(conn, positive
                ? "INSERT INTO label_tokens(token, cat, pos) VALUES($t,$c,1) ON CONFLICT(token, cat) DO UPDATE SET pos = pos + 1"
                : "INSERT INTO label_tokens(token, cat, neg) VALUES($t,$c,1) ON CONFLICT(token, cat) DO UPDATE SET neg = neg + 1", ("$t", t), ("$c", cat));
        }
        UserDb.Exec(conn, "INSERT INTO label_trained(key, cat, label) VALUES($k,$c,$l) ON CONFLICT(key, cat) DO UPDATE SET label=$l", ("$k", d.Key), ("$c", cat), ("$l", label));
        tx.Commit();
    }

    /// <summary>„Immer für diesen Absender“.</summary>
    public static void SetSender(UserDb db, string address, string cat)
    {
        Ensure(db);
        db.Exec("INSERT OR IGNORE INTO label_senders(address, cat) VALUES($a,$c)", ("$a", address.Trim().ToLowerInvariant()), ("$c", cat));
    }

    /// <summary>Kategorie gelöscht: Gelerntes dazu vergessen.</summary>
    public static void Drop(UserDb db, string cat)
    {
        Ensure(db);
        foreach (var t in new[] { "label_tokens", "label_trained", "label_senders", "label_pending" })
            db.Exec($"DELETE FROM {t} WHERE cat=$c", ("$c", cat));
    }

    /// <summary>Doc einer gespeicherten Mail (mit Inhalt, wo er hier liegt, sonst aus der Zeile der Liste).</summary>
    public static Doc? OfStored(UserSpace space, string account, string folder, uint uid)
    {
        if (MailStore.Raw(space.Db, account, folder, uid) is { } raw)
            try { return Of(MimeMessage.Load(new MemoryStream(raw)), space.Zone); } catch (FormatException) { }
        return MailStore.Row(space.Db, account, folder, uid) is { } row ? Of(row) : null;
    }

    // ---------------- Übernahme aus der alten Smart Inbox ----------------

    /// <summary>
    /// Einmal je Benutzer: mitgelieferte (nicht ausgeblendete) und eigene Smart-Kategorien werden lernende Kategorien,
    /// das Gelernte zieht auf die Schlagwörter um, bisherige Zuordnungen warten in label_pending aufs Schreiben zum Mailserver.
    /// </summary>
    public static void Migrate(UserSpace space)
    {
        var s = space.Settings;
        if (s.CategoriesMigrated) return;
        var db = space.Db;
        Ensure(db);
        var map = new Dictionary<string, string>();
        space.UpdateSettings(st =>
        {
            var cats = DmOffice.Settings.Categories.Of(st);
            foreach (var b in BuiltIn)
            {
                if (st.SmartHidden.Contains(b.Preset)) continue;
                var existing = cats.FirstOrDefault(c => c.Preset == b.Preset);
                if (existing == null)
                {
                    var key = cats.Any(c => c.Key == b.Key) ? DmOffice.Settings.Categories.KeyFor(b.Name, cats.Select(c => c.Key)) : b.Key;
                    existing = new MailCategory { Key = key, Name = b.Name, Color = b.Color, Smart = true, Preset = b.Preset };
                    cats.Add(existing);
                }
                map[b.Preset] = existing.Key;
            }
            foreach (var c in cats.Where(c => c.Smart)) map["kw:" + c.Key] = c.Key;
            foreach (var u in st.SmartCustom)
            {
                var key = DmOffice.Settings.Categories.KeyFor(u.Name, cats.Select(c => c.Key));
                cats.Add(new MailCategory { Key = key, Name = u.Name, Color = u.Color ?? "#0F6CBD", Smart = true });
                map[u.Id] = key;
            }
            st.Categories = cats;
            st.CategoriesMigrated = true;
        });
        if (db.Scalar("SELECT 1 FROM sqlite_master WHERE type='table' AND name='smart_trained'") == null) return;
        string? New(string old) => old.Length == 0 ? "" : map.GetValueOrDefault(old);
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        foreach (var (token, cat, n) in UserDb.Query(conn, "SELECT token, cat, n FROM smart_tokens WHERE n > 0", r => (r.GetString(0), r.GetString(1), r.GetInt32(2))))
            if (New(cat) is { } to)
                UserDb.Exec(conn, "INSERT INTO label_tokens(token, cat, pos) VALUES($t,$c,$n) ON CONFLICT(token, cat) DO UPDATE SET pos = pos + $n", ("$t", token), ("$c", to), ("$n", n));
        foreach (var (key, cat) in UserDb.Query(conn, "SELECT key, cat FROM smart_trained", r => (r.GetString(0), r.GetString(1))))
            if (New(cat) is { } to)
                UserDb.Exec(conn, "INSERT OR IGNORE INTO label_trained(key, cat, label) VALUES($k,$c,1)", ("$k", key), ("$c", to));
        foreach (var (addr, cat) in UserDb.Query(conn, "SELECT address, cat FROM smart_senders", r => (r.GetString(0), r.GetString(1))))
            if (New(cat) is { Length: > 0 } to)
                UserDb.Exec(conn, "INSERT OR IGNORE INTO label_senders(address, cat) VALUES($a,$c)", ("$a", addr), ("$c", to));
        foreach (var (mail, cat) in UserDb.Query(conn, "SELECT s.mail, s.cat FROM smart_mail s JOIN mail m ON m.id = s.mail WHERE s.cat <> ''", r => (r.GetInt64(0), r.GetString(1))))
            if (New(cat) is { Length: > 0 } to && !cat.StartsWith("kw:"))
                UserDb.Exec(conn, "INSERT OR IGNORE INTO label_pending(mail, cat) VALUES($m,$c)", ("$m", mail), ("$c", to));
        UserDb.Exec(conn, "DROP TABLE IF EXISTS smart_mail; DROP TABLE IF EXISTS smart_tokens; DROP TABLE IF EXISTS smart_trained; DROP TABLE IF EXISTS smart_senders;");
        tx.Commit();
    }

    /// <summary>Wartende Etiketten (aus der Übernahme): je Konto und Ordner gebündelt.</summary>
    public static List<(string Account, string Folder, string Cat, List<uint> Uids)> Pending(UserDb db, int limit = 2000)
    {
        Ensure(db);
        return db.Query(@"SELECT m.account, m.folder, p.cat, m.uid FROM label_pending p JOIN mail m ON m.id = p.mail LIMIT $n",
                r => (r.GetString(0), r.GetString(1), r.GetString(2), (uint)r.GetInt64(3)), ("$n", limit))
            .GroupBy(x => (x.Item1, x.Item2, x.Item3)).Select(g => (g.Key.Item1, g.Key.Item2, g.Key.Item3, g.Select(x => x.Item4).ToList())).ToList();
    }

    public static void DonePending(UserDb db, string account, string folder, string cat, IEnumerable<uint> uids)
    {
        foreach (var uid in uids)
            db.Exec("DELETE FROM label_pending WHERE cat=$c AND mail IN (SELECT id FROM mail WHERE account=$a AND folder=$f AND uid=$u)",
                ("$c", cat), ("$a", account), ("$f", folder), ("$u", (long)uid));
        // Verwaiste (Mail inzwischen weg) wegräumen
        db.Exec("DELETE FROM label_pending WHERE mail NOT IN (SELECT id FROM mail)");
    }
}
