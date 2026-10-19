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

    public async Task<T> UseAsync<T>(MailAccount account, string lane, Func<ImapClient, Task<T>> work, CancellationToken ct = default)
    {
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
            await client.ConnectAsync(account.ImapHost, account.ImapPort, Options(account.ImapSecurity), ct);
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

    public static SecureSocketOptions Options(Security s) => s switch
    {
        Security.Ssl => SecureSocketOptions.SslOnConnect,
        Security.StartTls => SecureSocketOptions.StartTls,
        _ => SecureSocketOptions.None
    };

    /// <summary>Anmelden mit Passwort oder - bei Microsoft-Konten - XOAUTH2 mit frischem Access-Token.</summary>
    public static async Task LoginAsync(MailService client, MailAccount account, string user, string? password, CancellationToken ct)
    {
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
            await smtp.ConnectAsync(account.SmtpHost, account.SmtpPort, Options(account.SmtpSecurity), ct);
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
