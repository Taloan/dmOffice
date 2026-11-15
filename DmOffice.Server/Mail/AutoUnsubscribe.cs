using System.Net;
using System.Net.Sockets;
using DmOffice.Core;
using MimeKit;

namespace DmOffice.Mail;

/// <summary>
/// Automatisch abmelden (Features.AutoUnsubscribe, Smart Inbox): Wer eine Mail in den Junk-Ordner verschiebt, wird von der
/// Verteilerliste des Absenders abgemeldet - wie bei Proton. Reihenfolge: One-Click (RFC 8058), sonst eine Mail an die
/// mailto-Adresse, sonst ruft der Server den Abmeldelink selbst auf. Dann ist offen, ob die Seite noch eine Bestätigung will.
/// Jeder Versuch steht in einer Liste je Person (Einstellungen → Smart Inbox). Nur Adressen im Internet: Geprüft wird beim
/// Verbinden selbst, auch nach Weiterleitungen und gegen DNS-Tricks.
/// </summary>
public static class AutoUnsubscribe
{
    public const string LogKey = "mail.autounsub.log";
    private const int MaxLog = 200;
    /// <summary>Dieselbe Liste wird so lange nicht noch einmal abgemeldet.</summary>
    private static readonly TimeSpan Again = TimeSpan.FromDays(30);
    public const string Done = "done", Unconfirmed = "unconfirmed", Failed = "failed";

    /// <param name="How">oneclick, mail oder link</param>
    public sealed record Entry(DateTime At, string Account, string Sender, string? Subject, string? List, string Result, string? How);

    private static readonly object LogLock = new();

    private static SocketsHttpHandler Handler(bool redirects) => new()
    {
        AllowAutoRedirect = redirects, MaxAutomaticRedirections = 5, UseCookies = false,
        ConnectCallback = async (ctx, ct) =>
        {
            var addrs = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
            var ip = addrs.FirstOrDefault(Unsubscribe.IsPublic) ?? throw new HttpRequestException("Diese Adresse ist nicht erlaubt.");
            var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try { await socket.ConnectAsync(new IPEndPoint(ip, ctx.DnsEndPoint.Port), ct); return new NetworkStream(socket, ownsSocket: true); }
            catch { socket.Dispose(); throw; }
        },
    };

    // One-Click ohne Weiterleitung (RFC 8058), der Link mit - Abmeldeseiten leiten fast immer weiter
    private static readonly HttpClient Post = new(Handler(false)) { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly HttpClient Get = new(Handler(true)) { Timeout = TimeSpan.FromSeconds(20) };

    static AutoUnsubscribe()
    {
        Get.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; dmOffice/2)");
    }

    /// <summary>Zum Testen: statt ins Netz zu gehen.</summary>
    internal static Func<Unsubscribe.Info, MailAccount, CancellationToken, Task<(string Result, string? How)>>? Attempt { get; set; }

    public static List<Entry> Log(UserDb db) => db.GetJson<List<Entry>>(LogKey);

    public static bool On(UserSpace space, MailAccount account) =>
        Features.On(Features.AutoUnsubscribe) && space.Settings.UnsubscribeOn(account);

    /// <summary>Kennung der Verteilerliste: List-Id, sonst die Absenderadresse.</summary>
    internal static string ListKey(MimeMessage m) =>
        (m.Headers[HeaderId.ListId] is { Length: > 0 } id ? id.Trim() : m.From.Mailboxes.FirstOrDefault()?.Address ?? "").ToLowerInvariant();

    /// <summary>Schon erledigt (oder gerade dabei)? Dann nicht noch einmal.</summary>
    internal static bool Recently(UserDb db, string key, DateTime now) =>
        key.Length > 0 && Log(db).Any(e => e.List == key && e.Result != Failed && now - e.At < Again);

    private static void Add(UserDb db, Entry e)
    {
        lock (LogLock)
        {
            var list = Log(db);
            list.RemoveAll(x => x.At == e.At && x.List == e.List);
            list.Insert(0, e);
            if (list.Count > MaxLog) list.RemoveRange(MaxLog, list.Count - MaxLog);
            db.SetJson(LogKey, list);
        }
    }

    /// <summary>Nach dem Verschieben in den Junk-Ordner: im Hintergrund abmelden, das Verschieben wartet nicht darauf.</summary>
    public static Task? Trigger(UserSpace space, MailAccount account, MimeMessage m)
    {
        if (!On(space, account) || Unsubscribe.Of(m) is not { } info) return null;
        var key = ListKey(m);
        var now = DateTime.UtcNow;
        Entry entry;
        lock (LogLock)
        {
            if (Recently(space.Db, key, now)) return null;
            var sender = m.From.Mailboxes.FirstOrDefault() is { } f ? string.IsNullOrWhiteSpace(f.Name) ? f.Address : $"{f.Name} <{f.Address}>" : "";
            entry = new Entry(now, account.Email, sender, m.Subject, key, Unconfirmed, null);
            Add(space.Db, entry);   // vorläufig - zwei Mails derselben Liste im selben Zug melden nur einmal ab
        }
        return Task.Run(async () =>
        {
            (string Result, string? How) r;
            try { r = await (Attempt ?? RunAsync)(info, account, CancellationToken.None); }
            catch (Exception) { r = (Failed, null); }
            Add(space.Db, entry with { Result = r.Result, How = r.How });
        });
    }

    private static async Task<(string Result, string? How)> RunAsync(Unsubscribe.Info info, MailAccount account, CancellationToken ct)
    {
        if (info.OneClick && info.Url != null)
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, info.Url)
                {
                    Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("List-Unsubscribe", "One-Click")]),
                };
                using var res = await Post.SendAsync(req, ct);
                if (res.IsSuccessStatusCode) return (Done, "oneclick");
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException) { }
        if (info.Mailto != null)
            try
            {
                using var smtp = await ImapPool.SmtpAsync(account, ct);
                await smtp.SendAsync(Unsubscribe.MailFor(account, info.Mailto), ct);
                await smtp.DisconnectAsync(true, ct);
                return (Done, "mail");
            }
            catch (Exception e) when (e is not OperationCanceledException) { }
        if (info.Url != null && Uri.TryCreate(info.Url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
            try
            {
                using var res = await Get.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
                // Die Seite ist geöffnet; ob sie noch einen Klick auf „Bestätigen“ erwartet, lässt sich nicht erkennen
                if (res.IsSuccessStatusCode) return (Unconfirmed, "link");
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { }
        return (Failed, null);
    }
}
