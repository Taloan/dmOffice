using System.Collections.Concurrent;
using DmOffice.Core;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;

namespace DmOffice.Mail;

/// <summary>
/// Neue Mails nach Sekunden statt im Minutentakt (Kennung mailpush): je IMAP-Konto eine stille Verbindung, die im Posteingang
/// mit IDLE (RFC 2177) wartet. Meldet der Mailserver dort Neues, Gelöschtes oder geänderte Markierungen, stößt sie den
/// gewohnten schnellen Abgleich an (<see cref="MailSync.Kick"/>) - geholt wird wie bisher nur dort.
///
///   - Höchstens eine solche Verbindung je Konto und <see cref="MaxConnections"/> auf dem ganzen Server.
///   - Nicht für deaktivierte und gesperrte Postfächer, nicht für Microsoft-Konten (Anmeldung per Token, bleiben im Takt).
///   - Kann der Server kein IDLE, bleibt es beim Takt; neuer Versuch erst nach Stunden oder geändertem Zugang.
///   - IDLE wird alle <see cref="Renew"/> erneuert; abgerissene Verbindungen werden mit wachsendem Abstand neu aufgebaut,
///     danach ein Abgleich für das, was in der Lücke ankam.
///   - Solange die Verbindung steht, läuft der Takt von MailSync nur noch als Sicherheitsnetz (<see cref="MailSync.QuickEveryPush"/>).
/// </summary>
public sealed class MailPush : BackgroundService
{
    public static bool On => Features.On(Features.MailPush);

    /// <summary>RFC 2177: IDLE spätestens nach 29 Minuten erneuern - manche Server trennen sonst.</summary>
    internal static TimeSpan Renew { get; set; } = TimeSpan.FromMinutes(25);
    /// <summary>So oft wird geprüft, welche Konten eine Verbindung brauchen (neu, geändert, entfernt).</summary>
    internal static TimeSpan CheckEvery { get; set; } = TimeSpan.FromSeconds(20);
    /// <summary>Meldungen innerhalb dieser Zeit ergeben einen Abgleich (Markieren vieler Mails meldet jede einzeln).</summary>
    internal static TimeSpan Settle { get; set; } = TimeSpan.FromMilliseconds(300);
    /// <summary>Kann der Server kein IDLE: erst nach dieser Zeit wieder fragen.</summary>
    internal static readonly TimeSpan NoIdleRetry = TimeSpan.FromHours(6);
    /// <summary>Obergrenze aller IDLE-Verbindungen des Servers; darüber bleiben weitere Konten im Takt.</summary>
    internal const int MaxConnections = 200;

    internal sealed class Watch(string signature, CancellationTokenSource stop)
    {
        public readonly string Signature = signature;
        public readonly CancellationTokenSource Stop = stop;
        public Task Task = Task.CompletedTask;
        public volatile bool Live;
        public int Pending;
        public int Failures;
    }

    private readonly Func<IEnumerable<UserSpace>> _working;
    private readonly MailSync _sync;
    private readonly ILogger<MailPush> _log;
    private readonly ConcurrentDictionary<string, Watch> _watches = new();
    private bool _fullLogged;

    public MailPush(Spaces spaces, MailSync sync, ILogger<MailPush> log) : this(spaces.Working, sync, log) { }

    /// <summary>Für Tests: Benutzer ohne <see cref="Spaces"/>.</summary>
    internal MailPush(Func<IEnumerable<UserSpace>> working, MailSync sync, ILogger<MailPush> log)
    {
        _working = working;
        _sync = sync;
        _log = log;
        sync.PushLive = Live;
    }

    private static string Key(string user, string account) => user + "|" + account;

    /// <summary>Steht die IDLE-Verbindung dieses Kontos gerade?</summary>
    public bool Live(string user, string account) => _watches.TryGetValue(Key(user, account), out var w) && w.Live;

    /// <summary>Zahl der Konten mit IDLE-Verbindung (stehend oder im Wiederaufbau).</summary>
    internal int Count => _watches.Count;

    /// <summary>Bekommt dieses Konto eine IDLE-Verbindung?</summary>
    internal static bool Eligible(MailAccount a) =>
        !string.IsNullOrEmpty(a.ImapHost) && a.Auth != MailAuth.Microsoft && !a.Off
        ;

    /// <summary>Alles, was die Verbindung betrifft - ändert sich etwas davon, wird sie neu aufgebaut.</summary>
    private static string SignatureOf(MailAccount a) =>
        string.Join("|", a.ImapHost, a.ImapPort, a.ImapSecurity, a.ImapUser, a.ImapPassword, a.AcceptAnyCertificate);

    /// <summary>Wartezeit vor dem n-ten Wiederaufbau (n ab 1): 5 s, 15 s, 30 s, 1, 2, 5, dann 10 Minuten; abgelehnte
    /// Anmeldung gleich 30 Minuten, damit der Anbieter das Konto nicht wegen vieler Fehlversuche sperrt.</summary>
    internal static TimeSpan Backoff(int failures, bool auth)
    {
        if (auth) return TimeSpan.FromMinutes(30);
        int[] steps = [5, 15, 30, 60, 120, 300, 600];
        return TimeSpan.FromSeconds(steps[Math.Clamp(failures - 1, 0, steps.Length - 1)]);
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(5), stop); } catch (OperationCanceledException) { return; }
        while (!stop.IsCancellationRequested)
        {
            try { Reconcile(stop); }
            catch (Exception ex) { _log.LogWarning("Mail-Push: {Error}", ex.Message); }
            try { await Task.Delay(CheckEvery, stop); } catch (OperationCanceledException) { break; }
        }
        foreach (var key in _watches.Keys) StopWatch(key);
    }

    /// <summary>Verbindungen an die Konten anpassen: neue aufbauen, entfallene und geänderte schließen.</summary>
    internal void Reconcile(CancellationToken stop)
    {
        var want = new Dictionary<string, (UserSpace Space, MailAccount Account, string Signature)>();
        if (On)
            foreach (var space in _working())
                foreach (var a in space.Settings.ActiveMailAccounts())
                    if (Eligible(a)) want[Key(space.User.Id, a.Id)] = (space, a, SignatureOf(a));

        foreach (var (key, w) in _watches)
            if (!want.TryGetValue(key, out var x) || x.Signature != w.Signature) StopWatch(key);

        foreach (var (key, x) in want)
        {
            if (_watches.ContainsKey(key)) continue;
            if (_watches.Count >= MaxConnections)
            {
                if (!_fullLogged) _log.LogWarning("Mail-Push: {Max} Verbindungen erreicht, weitere Konten im Takt", MaxConnections);
                _fullLogged = true;
                break;
            }
            var w = new Watch(x.Signature, CancellationTokenSource.CreateLinkedTokenSource(stop));
            _watches[key] = w;
            w.Task = Task.Run(() => RunAsync(w, x.Space, x.Account, w.Stop.Token), CancellationToken.None);
        }
    }

    private void StopWatch(string key)
    {
        if (!_watches.TryRemove(key, out var w)) return;
        w.Live = false;
        try { w.Stop.Cancel(); } catch (ObjectDisposedException) { }
        _ = w.Task.ContinueWith(_ => w.Stop.Dispose(), TaskScheduler.Default);
    }

    private sealed class NoIdleException() : Exception("Server ohne IDLE");

    private async Task RunAsync(Watch w, UserSpace space, MailAccount account, CancellationToken ct)
    {
        var gap = false;
        while (!ct.IsCancellationRequested)
        {
            var started = DateTime.UtcNow;
            TimeSpan wait;
            try
            {
                await SessionAsync(w, space, account, gap, ct);
                continue;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (NoIdleException)
            {
                _log.LogInformation("Mail-Push {Account}: Server kann kein IDLE, Abruf im Takt", LogNames.Of(space, account));
                wait = NoIdleRetry;
            }
            catch (Exception ex)
            {
                // Lief die Verbindung eine Weile, zählt der Abbruch als erster
                if (DateTime.UtcNow - started > TimeSpan.FromMinutes(2)) w.Failures = 0;
                w.Failures++;
                gap = true;
                wait = Backoff(w.Failures, ex is AuthenticationException);
                if (w.Failures <= 2 || ex is AuthenticationException)
                    _log.LogInformation("Mail-Push {Account}: {Error} - neuer Versuch in {Seconds} s", LogNames.Of(space, account), MailSync.Readable(ex), (int)wait.TotalSeconds);
            }
            finally { w.Live = false; }
            try { await Task.Delay(wait, ct); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task SessionAsync(Watch w, UserSpace space, MailAccount account, bool gap, CancellationToken ct)
    {
        using var client = new ImapClient { Timeout = 60_000 };
        if (account.AcceptAnyCertificate) client.ServerCertificateValidationCallback = (_, _, _, _) => true;
        try
        {
            await ImapPool.ConnectCheckedAsync(client, account, account.ImapHost, account.ImapPort, account.ImapSecurity, ct, keepAlive: true);
            await ImapPool.LoginAsync(client, account, account.ImapUser, account.ImapPassword, ct);
            if (!client.Capabilities.Contains(MailKit.Net.Imap.ImapCapability.Idle)) throw new NoIdleException();
            var inbox = client.Inbox ?? throw new NoIdleException();
            await inbox.OpenAsync(FolderAccess.ReadOnly, ct);

            void Changed(object? sender, EventArgs e) => Signal(w, space.User.Id, account.Id, inbox.FullName);
            inbox.CountChanged += Changed;
            inbox.MessageExpunged += Changed;
            inbox.MessagesVanished += Changed;
            inbox.MessageFlagsChanged += Changed;
            inbox.ModSeqChanged += Changed;

            w.Live = true;
            w.Failures = 0;
            // Was während der Unterbrechung ankam
            if (gap) _sync.Kick(space.User.Id, account.Id, inbox.FullName);

            while (true)
            {
                using var done = CancellationTokenSource.CreateLinkedTokenSource(ct);
                done.CancelAfter(Renew);
                await client.IdleAsync(done.Token, ct);
                ct.ThrowIfCancellationRequested();
            }
        }
        finally
        {
            w.Live = false;
            try { if (client.IsConnected) await client.DisconnectAsync(true, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        }
    }

    /// <summary>Eine Meldung des Servers: nach kurzer Pause (weitere Meldungen sammeln) einen Abgleich des Ordners anstoßen.</summary>
    private void Signal(Watch w, string user, string account, string folder)
    {
        if (Interlocked.Exchange(ref w.Pending, 1) == 1) return;
        _ = Task.Delay(Settle).ContinueWith(_ =>
        {
            Volatile.Write(ref w.Pending, 0);
            _sync.Kick(user, account, folder);
        }, TaskScheduler.Default);
    }
}
