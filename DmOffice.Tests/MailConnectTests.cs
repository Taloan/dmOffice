using System.Net;
using System.Net.Sockets;
using DmOffice.Core;
using DmOffice.Mail;

namespace DmOffice.Tests;

/// <summary>Mailkonten verbinden nur mit Adressen, die beim Verbinden geprüft sind - nicht mit dem Server selbst.</summary>
public class MailConnectTests
{
    [Fact]
    public async Task Name_auf_den_Server_selbst_wird_beim_Verbinden_abgelehnt()
    {
        // Ein Dienst auf 127.0.0.1, der antworten würde: die Verbindung kommt gar nicht erst zustande
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var accepted = listener.AcceptTcpClientAsync();
            foreach (var host in new[] { "localhost", "127.0.0.1", "[::1]" })
            {
                var account = new MailAccount { Id = "a", ImapHost = host, ImapPort = port, ImapSecurity = Security.None, ImapUser = "x", ImapPassword = "y" };
                using var client = new MailKit.Net.Imap.ImapClient { Timeout = 5000 };
                var ex = await Assert.ThrowsAsync<IOException>(() => ImapPool.ConnectCheckedAsync(client, account, host, port, Security.None, default));
                Assert.Contains("nicht erlaubt", ex.Message);
            }
            Assert.False(accepted.IsCompleted);
        }
        finally { listener.Stop(); }
    }
}
