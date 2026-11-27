using MailKit;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DmOffice.Core;
using MimeKit;

namespace DmOffice.Mail;

/// <summary>
/// Welche Bestätigung gehört wem - data/forward-confirm.json, nur SHA-256 des Tokens mit Besitzer. Adresse, Ablauf und Code
/// stehen in den Einstellungen des Besitzers (ForwardTarget).
/// </summary>
public sealed class ForwardConfirmIndex
{
    private sealed record Entry(string Hash, string Owner);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _path;
    private readonly object _lock = new();
    private readonly List<Entry> _entries;

    public ForwardConfirmIndex(DataPaths paths)
    {
        _path = Path.Combine(paths.Root, "forward-confirm.json");
        _entries = File.Exists(_path) ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(_path), Json) ?? new() : new();
    }

    public void Add(string owner, string hash)
    {
        lock (_lock) { _entries.RemoveAll(e => e.Hash == hash); _entries.Add(new Entry(hash, owner)); Save(); }
    }

    public void Remove(string hash)
    {
        lock (_lock) { if (_entries.RemoveAll(e => e.Hash == hash) > 0) Save(); }
    }

    public string? Owner(string hash)
    {
        lock (_lock) return _entries.FirstOrDefault(e => e.Hash == hash)?.Owner;
    }

    /// <summary>Benutzer gelöscht: alle seine Einträge weg.</summary>
    public void Forget(string owner)
    {
        lock (_lock) { if (_entries.RemoveAll(e => e.Owner == owner) > 0) Save(); }
    }

    private void Save()
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_entries, Json));
        File.Move(tmp, _path, overwrite: true);
    }
}

/// <summary>
/// Kennung ruleforward: Weiterleitungs- und Umleitungsziele gehen erst nach Bestätigung durch den Empfänger. Eigene Adressen
/// (Mailkonten) gelten als bestätigt, ebenso alles, was vor der Umstellung schon in Regeln stand.
/// </summary>
public static class ForwardTargets
{
    public static readonly TimeSpan Valid = TimeSpan.FromDays(7);
    public const int MaxCodeTries = 5;
    /// <summary>Frühestens so lange nach der letzten Bestätigungsmail eine neue.</summary>
    public static readonly TimeSpan ResendPause = TimeSpan.FromMinutes(1);

    public static bool On => Features.On(Features.RuleForward);

    public static string Norm(string address) => address.Trim().ToLowerInvariant();

    public static IEnumerable<string> Split(string value) => value.Split(',', ';').Select(Norm).Where(IsAddress);

    /// <summary>Mailadresse in der üblichen Form Name@Domain.tld: ein @, Domain mit Punkt, keine Leerzeichen, spitzen Klammern o. ä.
    /// Gleiche Regel wie in der Oberfläche (RulesSettings.tsx, MAIL_RE).</summary>
    public static bool IsAddress(string address) => address.Length <= 254 && AddressRe.IsMatch(address);

    private static readonly System.Text.RegularExpressions.Regex AddressRe = new(
        @"^[\p{L}\p{N}.!#$%&'*+/=?^_`{|}~-]+@[\p{L}\p{N}](?:[\p{L}\p{N}-]*[\p{L}\p{N}])?(?:\.[\p{L}\p{N}](?:[\p{L}\p{N}-]*[\p{L}\p{N}])?)+$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Teile eines Ziel-Felds (Komma/Semikolon), die keine Mailadresse sind.</summary>
    public static List<string> Invalid(string value) => value.Split(',', ';').Select(x => x.Trim()).Where(x => x.Length > 0 && !IsAddress(x)).ToList();

    /// <summary>Alle Ziele von Weiterleiten und Umleiten in den Regeln.</summary>
    public static HashSet<string> Of(IEnumerable<MailRule> rules) =>
        rules.SelectMany(r => r.Actions).Where(a => a.Type is "forward" or "redirect").SelectMany(a => Split(a.Value)).ToHashSet();

    /// <summary>8BITMIME des SMTP-Servers merken (nur bei Änderung speichern).</summary>
    public static void Note8Bit(UserSpace space, string accountId, bool has)
    {
        if (space.Settings.Smtp8BitMime?.TryGetValue(accountId, out var old) == true && old == has) return;
        space.UpdateSettings(s => (s.Smtp8BitMime ??= new())[accountId] = has);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> Probing = new();
    /// <summary>Gescheiterte Abfrage: frühestens nach einer Stunde wieder.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> ProbeFailed = new();

    /// <summary>8BITMIME im Hintergrund abfragen (Anmelden am SMTP-Server, Fähigkeiten lesen, trennen). Fehler: nichts merken.</summary>
    public static void Probe8Bit(UserSpace space, MailAccount account)
    {
        if (ProbeFailed.TryGetValue(account.Id, out var at) && DateTime.UtcNow - at < TimeSpan.FromHours(1)) return;
        if (!Probing.TryAdd(account.Id, 0)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var smtp = await ImapPool.SmtpAsync(account, cts.Token);
                Note8Bit(space, account.Id, smtp.Capabilities.Contains(MailKit.Net.Smtp.SmtpCapability.EightBitMime));
                await smtp.DisconnectAsync(true, cts.Token);
            }
            catch { ProbeFailed[account.Id] = DateTime.UtcNow; }
            finally { Probing.TryRemove(account.Id, out _); }
        });
    }

    /// <summary>
    /// Adressen der eigenen Mailkonten. Als eigene gilt eine Kontoadresse, die schon vor dem Festhalten eingetragen war
    /// (<see cref="UserSettings.OwnAddresses"/>), die der Anmeldename des Kontos ist oder die die Verwaltung eingerichtet hat.
    /// Eine später frei eingetragene, abweichende Adresse braucht die Bestätigung durch den Empfänger wie jedes andere Ziel.
    /// </summary>
    public static HashSet<string> Own(UserSettings s) =>
        s.MailAccounts.Where(a => s.OwnAddresses == null || Verified(s.OwnAddresses, a)).Select(a => Norm(a.Email)).Where(x => x.Contains('@')).ToHashSet();

    private static bool Verified(List<string> known, MailAccount a)
    {
        var e = Norm(a.Email);
        return known.Contains(e) || e == Norm(a.ImapUser) || e == Norm(a.SmtpUser);
    }

    /// <summary>Erstes Mal (innerhalb von UpdateSettings, vor einer Änderung an den Konten): die bisherigen Kontoadressen
    /// gelten weiter als eigene - nichts hört still auf zu funktionieren.</summary>
    public static void Remember(UserSettings s)
    {
        if (!On || s.OwnAddresses != null) return;
        s.OwnAddresses = s.MailAccounts.Select(a => Norm(a.Email)).Where(x => x.Contains('@')).Distinct().ToList();
    }

    /// <summary>Adresse, die die Verwaltung eingerichtet hat, gilt als eigene (innerhalb von UpdateSettings).</summary>
    public static void Trust(UserSettings s, string address)
    {
        Remember(s);
        var a = Norm(address);
        if (s.OwnAddresses != null && !s.OwnAddresses.Contains(a)) s.OwnAddresses.Add(a);
    }

    /// <summary>
    /// Darf an diese Adresse gesendet werden? Ohne Kennung immer (wie bisher). Noch nie eingerichtet (Liste null): alles, was in
    /// Regeln steht, ist Altbestand und gilt.
    /// </summary>
    public static bool Allowed(UserSettings s, string address)
    {
        if (!On || s.ForwardTargets == null) return true;
        var a = Norm(address);
        return Own(s).Contains(a) || s.ForwardTargets.Any(t => t.Address == a && t.Confirmed);
    }

    /// <summary>Erstes Mal: Ziele, die schon in Regeln stehen, gelten als bestätigt - nichts hört still auf zu funktionieren.</summary>
    public static void Ensure(UserSpace space)
    {
        if (!On || (space.Settings.ForwardTargets != null && space.Settings.OwnAddresses != null)) return;
        space.UpdateSettings(s => { s.ForwardTargets ??= Migrated(s.Rules); Remember(s); });
    }

    private static List<ForwardTarget> Migrated(IEnumerable<MailRule> rules) =>
        Of(rules).Select(a => new ForwardTarget { Address = a, Confirmed = true, ConfirmedAt = DateTime.UtcNow }).ToList();

    /// <summary>
    /// Regeln werden gespeichert (innerhalb von UpdateSettings, s hat noch die alten Regeln): Ziele, die in keiner Regel mehr stehen,
    /// fallen weg; neue kommen dazu - mit Kennung unbestätigt, ohne (nach der Passwortabfrage) bestätigt. Rückgabe: neue
    /// unbestätigte Adressen und Token-Hashes der weggefallenen.
    /// </summary>
    public static (List<string> Pending, List<string> Dropped) Update(UserSettings s, IReadOnlyCollection<MailRule> rules)
    {
        // Ohne Kennung und noch nie eingerichtet: so lassen - beim Einschalten zählt dann alles als Altbestand
        if (!On && s.ForwardTargets == null) return ([], []);
        Remember(s);
        var list = s.ForwardTargets ?? Migrated(s.Rules);
        var want = Of(rules);
        var own = Own(s);
        var dropped = list.Where(t => !want.Contains(t.Address) && t.TokenHash != null).Select(t => t.TokenHash!).ToList();
        list.RemoveAll(t => !want.Contains(t.Address));
        var pending = new List<string>();
        foreach (var a in want.Where(a => !own.Contains(a) && list.All(t => t.Address != a)))
        {
            list.Add(new ForwardTarget { Address = a, Confirmed = !On, ConfirmedAt = On ? null : DateTime.UtcNow });
            if (On) pending.Add(a);
        }
        s.ForwardTargets = list;
        return (pending, dropped);
    }

    /// <summary>Konto, das die Bestätigungsmail sendet: das der ersten Regel mit diesem Ziel, bei „Alle Konten“ das Standardkonto.</summary>
    public static MailAccount? Sender(UserSettings s, string address)
    {
        var a = Norm(address);
        var rule = s.Rules.FirstOrDefault(r => r.Actions.Any(x => x.Type is "forward" or "redirect" && Split(x.Value).Contains(a)));
        return (rule?.AccountId is { } id ? s.MailAccounts.FirstOrDefault(m => m.Id == id) : null) ?? s.DefaultAccount();
    }

    public static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    /// <summary>256 Bit Zufall, URL-tauglich.</summary>
    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Sechs Ziffern zum Eintippen im Regeln-Dialog (höchstens <see cref="MaxCodeTries"/> Versuche).</summary>
    public static string NewCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    /// <summary>
    /// Neue Bestätigung für ein Ziel: Token und Code erzeugen, beim Ziel eintragen (alte ungültig) und im Index vermerken.
    /// Rückgabe null: das Ziel gibt es nicht (mehr) oder es ist schon bestätigt.
    /// </summary>
    public static (string Token, string Code, MailAccount? Account)? Prepare(UserSpace space, ForwardConfirmIndex index, string address, DateTime now)
    {
        var a = Norm(address);
        string token = NewToken(), code = NewCode();
        string? old = null;
        bool ok = false;
        MailAccount? account = null;
        space.UpdateSettings(s =>
        {
            if (s.ForwardTargets?.FirstOrDefault(t => t.Address == a) is not { Confirmed: false } t) return;
            account = Sender(s, a);
            old = t.TokenHash;
            t.TokenHash = Hash(token);
            t.CodeHash = Hash(a + "|" + code);
            t.CodeTries = 0;
            t.SentAt = now;
            t.Expires = now + Valid;
            t.Error = account == null ? "Kein Mailkonto zum Senden." : null;
            t.Language = account?.Language == "en" ? "en" : "de";
            ok = true;
        });
        if (!ok) return null;
        if (old != null) index.Remove(old);
        index.Add(space.User.Id, Hash(token));
        return (token, code, account);
    }

    /// <summary>Die Bestätigungsmail: Link (Seite mit Knopf) und Code.</summary>
    public static MimeMessage Message(MailAccount account, string address, string link, string code, string owner)
    {
        bool en = account.Language == "en";
        var subject = en ? "Please confirm: forwarding mail to this address" : "Bitte bestätigen: Mails an diese Adresse weiterleiten";
        var text = en
            ? $"{owner} ({account.Email}) has set up dmOffice to forward mail to {address}.\n\n"
              + $"Nothing will be sent to this address until it is confirmed.\n\nConfirm: {link}\n\nOr enter this code in dmOffice: {code}\n\n"
              + "The link and code are valid for 7 days. If you did not expect this, ignore this mail."
            : $"{owner} ({account.Email}) hat in dmOffice eingerichtet, Mails an {address} weiterzuleiten.\n\n"
              + $"Bis zur Bestätigung geht nichts an diese Adresse.\n\nBestätigen: {link}\n\nOder diesen Code in dmOffice eingeben: {code}\n\n"
              + "Link und Code gelten 7 Tage. Falls du das nicht erwartet hast, ignoriere diese Mail einfach.";
        var html = "<p>" + string.Join("</p><p>", text.Split("\n\n").Select(p => WebUtility.HtmlEncode(p)
            .Replace(WebUtility.HtmlEncode(link), $"<a href=\"{WebUtility.HtmlEncode(link)}\">{WebUtility.HtmlEncode(link)}</a>"))) + "</p>";
        var m = MailCompose.Build(account, new MailCompose.Draft { To = address, Subject = subject, Html = html }, []);
        // Bestätigungsmail selbst nicht von Regeln weiterleiten lassen (Schleife)
        m.Headers.Add("X-dmOffice-Forwarded", "1");
        return m;
    }

    /// <summary>Bestätigungsmail für ein Ziel senden (neu oder erneut). Fehler beim Versand stehen danach am Ziel.</summary>
    public static async Task SendAsync(UserSpace space, ForwardConfirmIndex index, string address, string origin, ILogger log, CancellationToken ct = default)
    {
        if (Prepare(space, index, address, DateTime.UtcNow) is not { } p) return;
        if (p.Account == null) return;
        string? error = null;
        try
        {
            var link = origin.TrimEnd('/') + "/forward-confirm/" + p.Token;
            var owner = string.IsNullOrWhiteSpace(p.Account.DisplayName) ? space.User.DisplayName : p.Account.DisplayName;
            using var smtp = await ImapPool.SmtpAsync(p.Account, ct);
            await smtp.SendAsync(Message(p.Account, Norm(address), link, p.Code, owner), ct);
            await smtp.DisconnectAsync(true, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = ex.Message;
            log.LogWarning("Regel: Bestätigungsmail an {To} nicht gesendet: {Error}", address, ex.Message);
        }
        var a = Norm(address);
        space.UpdateSettings(s => { if (s.ForwardTargets?.FirstOrDefault(t => t.Address == a) is { } t) t.Error = error; });
    }

    public enum Result { Confirmed, Unknown, Expired, WrongCode, TooManyTries, TooManyToday }

    /// <summary>Über den Link (öffentliche Seite, nach Klick auf „Bestätigen“): Token einmalig, 7 Tage gültig.</summary>
    public static (Result Result, string? Address) ConfirmToken(Spaces spaces, ForwardConfirmIndex index, string token, DateTime now)
    {
        if (token.Length is < 32 or > 64) return (Result.Unknown, null);
        var hash = Hash(token);
        if (index.Owner(hash) is not { } owner) return (Result.Unknown, null);
        UserSpace space;
        try { space = spaces.Get(owner); } catch (KeyNotFoundException) { index.Remove(hash); return (Result.Unknown, null); }
        var result = Result.Unknown;
        string? address = null;
        space.UpdateSettings(s =>
        {
            if (s.ForwardTargets?.FirstOrDefault(t => t.TokenHash != null && CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(t.TokenHash), Encoding.ASCII.GetBytes(hash))) is not { } t) return;
            address = t.Address;
            if (t.Expires is { } e && e <= now) { result = Result.Expired; return; }
            Confirm(t, now);
            result = Result.Confirmed;
        });
        // Einmalig: auch ein abgelaufener Link ist danach weg
        if (result != Result.Unknown) index.Remove(hash);
        return (result, address);
    }

    /// <summary>Falsche Codes je Benutzer innerhalb eines Tages, über alle Ziele und Neusendungen hinweg.</summary>
    public const int MaxCodeTriesPerDay = 20;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime Start, int Count)> WrongToday = new();

    /// <summary>Über den Code im Regeln-Dialog (angemeldet).</summary>
    public static Result ConfirmCode(UserSpace space, ForwardConfirmIndex index, string address, string code, DateTime now)
    {
        var a = Norm(address);
        var user = space.User.Id;
        if (WrongToday.TryGetValue(user, out var w) && now - w.Start < TimeSpan.FromDays(1) && w.Count >= MaxCodeTriesPerDay)
            return Result.TooManyToday;
        var result = Result.Unknown;
        string? hash = null;
        space.UpdateSettings(s =>
        {
            if (s.ForwardTargets?.FirstOrDefault(t => t.Address == a) is not { Confirmed: false, CodeHash: not null } t) return;
            if (t.Expires is { } e && e <= now) { result = Result.Expired; return; }
            if (t.CodeTries >= MaxCodeTries) { result = Result.TooManyTries; return; }
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(t.CodeHash), Encoding.ASCII.GetBytes(Hash(a + "|" + code.Trim()))))
            {
                t.CodeTries++;
                WrongToday.AddOrUpdate(user, _ => (now, 1), (_, v) => now - v.Start < TimeSpan.FromDays(1) ? (v.Start, v.Count + 1) : (now, 1));
                result = t.CodeTries >= MaxCodeTries ? Result.TooManyTries : Result.WrongCode;
                return;
            }
            hash = t.TokenHash;
            Confirm(t, now);
            result = Result.Confirmed;
        });
        if (hash != null) index.Remove(hash);
        return result;
    }

    private static void Confirm(ForwardTarget t, DateTime now)
    {
        t.Confirmed = true;
        t.ConfirmedAt = now;
        t.TokenHash = null;
        t.CodeHash = null;
        t.CodeTries = 0;
        t.Error = null;
    }

    // ---------------- Öffentliche Seite ----------------

    /// <summary>Seite zum Link: erst ein Klick auf „Bestätigen“ (POST) bestätigt - Link-Scanner, die nur öffnen, bestätigen nichts.</summary>
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/forward-confirm").RequireRateLimiting("public");
        group.MapGet("/{token}", (string token, HttpContext http, Spaces spaces, ForwardConfirmIndex index) =>
        {
            Headers(http);
            var ip = Ip(http);
            if (Auth.Blocked(ip)) return Page("de", "Zu viele Versuche. Bitte später noch einmal.", null, 429);
            // Nur nachsehen, ob es den Link gibt (Sprache der Seite) - bestätigt wird erst beim Absenden
            var (lang, address, state) = Peek(spaces, index, token);
            if (state == Result.Unknown) { Auth.Failed(ip); return Page("de", "Diesen Link gibt es nicht (mehr).", null, 404); }
            if (state == Result.Expired) return Page(lang, lang == "en" ? "This link has expired." : "Dieser Link ist abgelaufen.", null, 410);
            return Page(lang, lang == "en" ? $"Forward mail to {address}?" : $"Mails an {address} weiterleiten lassen?",
                lang == "en" ? "Confirm" : "Bestätigen", 200);
        });
        group.MapPost("/{token}", (string token, HttpContext http, Spaces spaces, ForwardConfirmIndex index) =>
        {
            Headers(http);
            var ip = Ip(http);
            if (Auth.Blocked(ip)) return Page("de", "Zu viele Versuche. Bitte später noch einmal.", null, 429);
            var (lang, _, _) = Peek(spaces, index, token);
            var (result, address) = ConfirmToken(spaces, index, token, DateTime.UtcNow);
            if (result == Result.Unknown) { Auth.Failed(ip); return Page("de", "Diesen Link gibt es nicht (mehr).", null, 404); }
            if (result == Result.Expired) return Page(lang, lang == "en" ? "This link has expired." : "Dieser Link ist abgelaufen.", null, 410);
            return Page(lang, lang == "en" ? $"Confirmed. Mail can now be forwarded to {address}." : $"Bestätigt. Mails können jetzt an {address} weitergeleitet werden.", null, 200);
        }).DisableAntiforgery();
    }

    private static string Ip(HttpContext http) => "fwd-ip:" + Auth.IpKey(http.Connection.RemoteIpAddress);

    private static void Headers(HttpContext http)
    {
        http.Response.Headers["Referrer-Policy"] = "no-referrer";
        http.Response.Headers["X-Robots-Tag"] = "noindex";
        http.Response.Headers["Cache-Control"] = "no-store";
    }

    private static (string Lang, string? Address, Result State) Peek(Spaces spaces, ForwardConfirmIndex index, string token)
    {
        if (token.Length is < 32 or > 64) return ("de", null, Result.Unknown);
        var hash = Hash(token);
        if (index.Owner(hash) is not { } owner) return ("de", null, Result.Unknown);
        try
        {
            var t = spaces.Get(owner).Settings.ForwardTargets?.FirstOrDefault(x => x.TokenHash == hash);
            if (t == null) return ("de", null, Result.Unknown);
            return (t.Language, t.Address, t.Expires is { } e && e <= DateTime.UtcNow ? Result.Expired : Result.Confirmed);
        }
        catch (KeyNotFoundException) { return ("de", null, Result.Unknown); }
    }

    private static IResult Page(string lang, string text, string? button, int status)
    {
        var body = $"<p>{WebUtility.HtmlEncode(text)}</p>"
            + (button == null ? "" : $"<form method=\"post\"><button type=\"submit\">{WebUtility.HtmlEncode(button)}</button></form>");
        var html = $"<!doctype html><html lang=\"{lang}\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">"
            + "<meta name=\"robots\" content=\"noindex\"><title>dmOffice</title><style>body{font-family:system-ui,sans-serif;max-width:520px;margin:15vh auto;padding:0 16px;"
            + "line-height:1.5;color:#222;background:#fafafa}@media(prefers-color-scheme:dark){body{color:#eee;background:#1f1f1f}}"
            + "button{font:inherit;padding:8px 20px;border:0;border-radius:4px;background:#0f6cbd;color:#fff;cursor:pointer}</style></head>"
            + $"<body><h1 style=\"font-size:1.3em\">dmOffice</h1>{body}</body></html>";
        return Results.Content(html, "text/html; charset=utf-8", Encoding.UTF8, status);
    }
}
