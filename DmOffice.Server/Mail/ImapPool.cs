using System.Collections.Concurrent;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using DmOffice.Core;

namespace DmOffice.Mail;

/// <summary>
/// Eine offene IMAP-Verbindung je Konto, nacheinander benutzt. Der Aufbau
/// (TLS, Anmeldung) kostet bei manchen Servern eine Sekunde - jedes Mal neu
/// waere in der Liste spuerbar. Geht die Verbindung verloren, wird beim
/// naechsten Zugriff neu verbunden, einmal.
/// </summary>
public sealed class ImapPool : IDisposable
{
    private sealed class Slot
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public ImapClient? Client;
        public string Signature = "";
        public DateTime LastUse;
    }

    private readonly ConcurrentDictionary<string, Slot> _slots = new();
    private readonly Timer _reaper;

    /// <summary>Getrennte Verbindungen fuer die Oberflaeche und den Sync -
    /// ein langer Erst-Sync soll kein Verschieben oder Oeffnen aufhalten.</summary>
    public const string Ui = "ui", Sync = "sync";

    /// <summary>OAuth2 fuer Microsoft-Konten - beim Start gesetzt (Program.cs).</summary>
    public static MicrosoftOAuth? OAuth { get; set; }

    public ImapPool()
    {
        // Nach zehn Minuten ohne Zugriff schliessen - Server trennen sonst
        // selbst, und eine halbtote Verbindung haengt beim naechsten Mal.
        _reaper = new Timer(_ => Reap(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>Demo-Postfach (DMOFFICE_DEMO): ohne Server - statt „hostname cannot be empty“ eine verständliche Meldung.
    /// Auch für Aktionen, die erst später zum Server gehen (Entfernen nach Zeit).</summary>
    public static void RequireServer(MailAccount account)
    {
        if (string.IsNullOrEmpty(account.ImapHost))
            throw new UnauthorizedAccessException(DmOffice.Core.Demo.Enabled
                ? "Im Demo-Postfach werden Mails nur angezeigt – Verschieben, Wiedervorlage, Löschen, Entfernen nach Zeit und Markieren gehen in der Demo nicht."
                : "Für dieses Konto ist kein IMAP-Server eingetragen.");
    }

    public async Task<T> UseAsync<T>(MailAccount account, string lane, Func<ImapClient, Task<T>> work, CancellationToken ct = default)
    {
        RequireServer(account);
        // [feature:mailaccountoff]
        // Deaktiviert: auch keine schon offene Verbindung weiterverwenden
        if (account.Off) throw new AuthenticationException("Dieses Mailkonto ist deaktiviert.");
        // [/feature:mailaccountoff]
        var slot = _slots.GetOrAdd(account.Id + "|" + lane, _ => new Slot());
        await slot.Gate.WaitAsync(ct);
        try
        {
            ImapClient client;
            // Nur der Aufbau (und die Pruefung einer alten Verbindung) wird wiederholt -
            // die Arbeit selbst nicht, sie kann schon etwas bewirkt haben (APPEND, MOVE).
            try { client = await ConnectedAsync(slot, account, ct); }
            catch (Exception ex) when (IsConnectionProblem(ex))
            {
                Drop(slot);
                client = await ConnectedAsync(slot, account, ct);
            }
            slot.LastUse = DateTime.UtcNow;
            try { return await work(client); }
            catch (Exception ex) when (IsConnectionProblem(ex))
            {
                // Halbtote Verbindung nicht wiederverwenden
                Drop(slot);
                throw;
            }
        }
        finally
        {
            slot.Gate.Release();
        }
    }

    public Task UseAsync(MailAccount account, string lane, Func<ImapClient, Task> work, CancellationToken ct = default) =>
        UseAsync<bool>(account, lane, async c => { await work(c); return true; }, ct);

    /// <summary>Verbindungen eines Kontos schliessen (Konto geloescht, Zugang geaendert).</summary>
    public void Close(string accountId)
    {
        foreach (var (key, slot) in _slots)
        {
            if (!key.StartsWith(accountId + "|")) continue;
            if (!slot.Gate.Wait(0)) continue;
            try { Drop(slot); } finally { slot.Gate.Release(); }
        }
    }

    /// <summary>Verbindung weg oder kaputt - kein Fehler einer einzelnen Mail oder eines Ordners.</summary>
    internal static bool IsConnectionProblem(Exception ex) =>
        ex is ServiceNotConnectedException or ImapProtocolException or IOException
            or System.Net.Sockets.SocketException;

    private static string SignatureOf(MailAccount a) =>
        string.Join("|", a.ImapHost, a.ImapPort, a.ImapSecurity, a.ImapUser, a.Auth, a.ImapPassword, a.AcceptAnyCertificate);

    private static readonly TimeSpan IdleCheck = TimeSpan.FromSeconds(30);

    private static async Task<ImapClient> ConnectedAsync(Slot slot, MailAccount account, CancellationToken ct)
    {
        var sig = SignatureOf(account);
        if (slot.Client is { IsConnected: true, IsAuthenticated: true } c && slot.Signature == sig)
        {
            // Laenger unbenutzt: erst pruefen, ob der Server noch da ist - ein Fehler hier
            // zaehlt zum Aufbau und darf wiederholt werden, einer in der Arbeit nicht.
            if (DateTime.UtcNow - slot.LastUse > IdleCheck) await c.NoOpAsync(ct);
            return c;
        }
        Drop(slot);

        var client = new ImapClient { Timeout = 60_000 };
        try
        {
            if (account.AcceptAnyCertificate) client.ServerCertificateValidationCallback = (_, _, _, _) => true;
            await ConnectCheckedAsync(client, account, account.ImapHost, account.ImapPort, account.ImapSecurity, ct);
            await LoginAsync(client, account, account.ImapUser, account.ImapPassword, ct);
        }
        catch
        {
            client.Dispose();
            throw;
        }
        slot.Client = client;
        slot.Signature = sig;
        return client;
    }

    /// <summary>
    /// Verbinden mit genau den Adressen, die beim Verbinden geprüft wurden: der Name wird hier aufgelöst und nur an erlaubte
    /// Adressen verbunden (nie der Server selbst, link-lokal, Multicast - wie beim Speichern des Kontos). Ein Name, der nach dem
    /// Speichern auf 127.0.0.1 umgestellt wird, erreicht so nichts. Das Heimnetz bleibt erlaubt (selbst gehostete Mail).
    /// </summary>
    /// <summary>Gesperrte Ziele (Adresse, Port). Tests lassen ihren eigenen Mailserver auf 127.0.0.1 zu.</summary>
    internal static Func<System.Net.IPAddress, int, bool> Refused { get; set; } = (a, _) => DmOffice.Storage.ExternalFolders.NeverAllowed(a);

    /// <param name="keepAlive">TCP-Keepalive für lange stille Verbindungen (IDLE): eine unterwegs abgerissene Verbindung
    /// (Router, NAT) fällt nach wenigen Minuten auf, statt bis zur nächsten Erneuerung als offen zu gelten.</param>
    public static async Task ConnectCheckedAsync(MailService client, MailAccount account, string host, int port, Security security, CancellationToken ct, bool keepAlive = false)
    {
        bool anyAddress = false;
        if (port <= 0) port = security == Security.Ssl ? (client is SmtpClient ? 465 : 993) : (client is SmtpClient ? 587 : 143);
        var name = host.Trim().Trim('[', ']');
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(client.Timeout > 0 ? client.Timeout : 60_000);
        var addrs = System.Net.IPAddress.TryParse(name, out var literal) ? [literal] : await System.Net.Dns.GetHostAddressesAsync(name, limit.Token);
        var ok = anyAddress ? addrs : addrs.Where(a => !Refused(a, port)).ToArray();
        if (ok.Length == 0) throw new IOException("Dieser Server ist nicht erlaubt.");
        var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
        try
        {
            if (keepAlive)
            {
                socket.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Socket, System.Net.Sockets.SocketOptionName.KeepAlive, true);
                try
                {
                    socket.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Tcp, System.Net.Sockets.SocketOptionName.TcpKeepAliveTime, 120);
                    socket.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Tcp, System.Net.Sockets.SocketOptionName.TcpKeepAliveInterval, 30);
                    socket.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Tcp, System.Net.Sockets.SocketOptionName.TcpKeepAliveRetryCount, 4);
                }
                catch (System.Net.Sockets.SocketException) { /* System ohne diese Einstellungen: Vorgaben des Betriebssystems */ }
            }
            await socket.ConnectAsync(ok, port, limit.Token);
            // MailKit übernimmt den Socket; der Name gilt für TLS (SNI, Zertifikat)
            await client.ConnectAsync(socket, name, port, Options(security), ct);
        }
        catch { socket.Dispose(); throw; }
    }

    public static SecureSocketOptions Options(Security s) => s switch
    {
        Security.Ssl => SecureSocketOptions.SslOnConnect,
        Security.StartTls => SecureSocketOptions.StartTls,
        _ => SecureSocketOptions.None
    };

    /// <summary>Anmelden mit Passwort oder - bei Microsoft-Konten - XOAUTH2 mit frischem Access-Token.</summary>
    public static async Task LoginAsync(MailService client, MailAccount account, string user, string? password, CancellationToken ct)
    {
        // [feature:mailaccountoff]
        // Deaktiviertes Postfach: keine Verbindung zum Anbieter, weder IMAP noch SMTP
        if (account.Off) throw new AuthenticationException("Dieses Mailkonto ist deaktiviert.");
        // [/feature:mailaccountoff]
        if (account.Auth == MailAuth.Microsoft)
        {
            var oauth = OAuth ?? throw new AuthenticationException("Microsoft-Anmeldung ist auf diesem Server nicht eingerichtet (DMOFFICE_MS_CLIENT_ID).");
            var token = await oauth.AccessTokenAsync(account, ct);
            await client.AuthenticateAsync(new SaslMechanismOAuth2(user, token), ct);
        }
        else await client.AuthenticateAsync(user, password ?? "", ct);
    }

    public static async Task<SmtpClient> SmtpAsync(MailAccount account, CancellationToken ct)
    {
        // Sonst meldet MailKit "The host name cannot be empty. (Parameter 'host')"
        if (string.IsNullOrWhiteSpace(account.SmtpHost))
            throw new InvalidOperationException("Für dieses Konto ist kein Postausgangsserver (SMTP) eingetragen.");
        var smtp = new SmtpClient { Timeout = 60_000 };
        try
        {
            if (account.AcceptAnyCertificate) smtp.ServerCertificateValidationCallback = (_, _, _, _) => true;
            await ConnectCheckedAsync(smtp, account, account.SmtpHost, account.SmtpPort, account.SmtpSecurity, ct);
            var user = string.IsNullOrEmpty(account.SmtpUser) ? account.ImapUser : account.SmtpUser;
            var password = string.IsNullOrEmpty(account.SmtpPassword) ? account.ImapPassword : account.SmtpPassword;
            if (!string.IsNullOrEmpty(user)) await LoginAsync(smtp, account, user, password, ct);
            return smtp;
        }
        catch
        {
            smtp.Dispose();
            throw;
        }
    }

    private static void Drop(Slot slot)
    {
        var c = slot.Client;
        slot.Client = null;
        if (c == null) return;
        try { if (c.IsConnected) c.Disconnect(true); } catch { }
        c.Dispose();
    }

    private void Reap()
    {
        foreach (var slot in _slots.Values)
        {
            if (slot.Client == null || DateTime.UtcNow - slot.LastUse < TimeSpan.FromMinutes(10)) continue;
            if (!slot.Gate.Wait(0)) continue;
            try { Drop(slot); } finally { slot.Gate.Release(); }
        }
    }

    public void Dispose()
    {
        _reaper.Dispose();
        foreach (var slot in _slots.Values) Drop(slot);
    }
}
