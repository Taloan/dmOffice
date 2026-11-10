using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MimeKit;

namespace DmOffice.Tests;

/// <summary>
/// Kleiner SMTP-Server fuer Tests: nimmt alles an, was ankommt (ohne Anmeldung, ohne TLS), und merkt sich jede Mail
/// mit den Empfaengern aus dem Umschlag (RCPT TO). Genug fuer MailKit mit SecureSocketOptions.None.
/// </summary>
public sealed class FakeSmtp : IDisposable
{
    public sealed record Received(List<string> To, MimeMessage Message);

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentQueue<Received> _mails = new();

    public int Port { get; }

    public FakeSmtp()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoop);
    }

    /// <summary>Alle seit dem letzten Aufruf angekommenen Mails.</summary>
    public List<Received> Take()
    {
        var list = new List<Received>();
        while (_mails.TryDequeue(out var r)) list.Add(r);
        return list;
    }

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (Exception) { return; }
            _ = Task.Run(() => Serve(client));
        }
    }

    private async Task Serve(TcpClient client)
    {
        using var _ = client;
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.Latin1);
        using var writer = new StreamWriter(stream, Encoding.Latin1) { NewLine = "\r\n", AutoFlush = true };
        var to = new List<string>();
        await writer.WriteLineAsync("220 fake.test ESMTP");
        try
        {
            while (await reader.ReadLineAsync(_stop.Token) is { } line)
            {
                var cmd = line.Length >= 4 ? line[..4].ToUpperInvariant() : line.ToUpperInvariant();
                switch (cmd)
                {
                    case "EHLO":
                        await writer.WriteLineAsync("250-fake.test");
                        await writer.WriteLineAsync("250 8BITMIME");
                        break;
                    case "HELO":
                    case "NOOP":
                        await writer.WriteLineAsync("250 OK");
                        break;
                    case "MAIL":
                    case "RSET":
                        to.Clear();
                        await writer.WriteLineAsync("250 OK");
                        break;
                    case "RCPT":
                        var a = line.IndexOf('<');
                        var b = line.IndexOf('>');
                        if (a >= 0 && b > a) to.Add(line[(a + 1)..b].ToLowerInvariant());
                        await writer.WriteLineAsync("250 OK");
                        break;
                    case "DATA":
                        await writer.WriteLineAsync("354 Weiter");
                        var data = new StringBuilder();
                        while (await reader.ReadLineAsync(_stop.Token) is { } l && l != ".")
                            data.Append(l.StartsWith("..") ? l[1..] : l).Append("\r\n");
                        var message = MimeMessage.Load(new MemoryStream(Encoding.Latin1.GetBytes(data.ToString())));
                        _mails.Enqueue(new Received(to.ToList(), message));
                        to.Clear();
                        await writer.WriteLineAsync("250 Angenommen");
                        break;
                    case "QUIT":
                        await writer.WriteLineAsync("221 Tschuess");
                        return;
                    default:
                        await writer.WriteLineAsync("502 Nicht unterstuetzt");
                        break;
                }
            }
        }
        catch (Exception) { /* Verbindung weg */ }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }
}
