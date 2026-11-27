using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DmOffice.Core;
using DmOffice.Mail;
using Microsoft.Extensions.Logging.Abstractions;

namespace DmOffice.Tests;

/// <summary>Kennung mailpush: IMAP IDLE stößt den Abgleich an, der Long-Poll meldet die neue Version.</summary>
public class MailPushTests
{
    /// <summary>Kleiner IMAP-Server: Anmeldung, Posteingang, IDLE; der Test schickt Meldungen an die wartenden Verbindungen.</summary>
    private sealed class FakeImap : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly bool _idle;
        private readonly ConcurrentDictionary<int, StreamWriter> _idling = new();
        private readonly ConcurrentBag<TcpClient> _clients = new();
        private int _next;
        public int Connections;
        public int IdleCommands;
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public FakeImap(bool idle = true)
        {
            _idle = idle;
            _listener.Start();
            _ = AcceptLoop();
        }

        private async Task AcceptLoop()
        {
            while (true)
            {
                TcpClient c;
                try { c = await _listener.AcceptTcpClientAsync(); } catch { return; }
                Interlocked.Increment(ref Connections);
                _clients.Add(c);
                _ = Serve(c, Interlocked.Increment(ref _next));
            }
        }

        private async Task Serve(TcpClient c, int id)
        {
            try
            {
                var stream = c.GetStream();
                var r = new StreamReader(stream, Encoding.ASCII);
                var w = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
                var caps = "IMAP4rev1" + (_idle ? " IDLE" : "");
                await w.WriteLineAsync($"* OK [CAPABILITY {caps}] bereit");
                string? idleTag = null;
                while (await r.ReadLineAsync() is { } line)
                {
                    if (idleTag != null)
                    {
                        if (line.Trim().Equals("DONE", StringComparison.OrdinalIgnoreCase))
                        {
                            _idling.TryRemove(id, out _);
                            await w.WriteLineAsync($"{idleTag} OK IDLE beendet");
                            idleTag = null;
                        }
                        continue;
                    }
                    var parts = line.Split(' ', 3);
                    var tag = parts[0];
                    var cmd = parts.Length > 1 ? parts[1].ToUpperInvariant() : "";
                    var rest = parts.Length > 2 ? parts[2] : "";
                    switch (cmd)
                    {
                        case "CAPABILITY":
                            await w.WriteLineAsync($"* CAPABILITY {caps}");
                            await w.WriteLineAsync($"{tag} OK fertig");
                            break;
                        case "LIST":
                            if (rest.EndsWith("\"\"")) await w.WriteLineAsync("* LIST (\\Noselect) \"/\" \"\"");
                            else await w.WriteLineAsync("* LIST (\\HasNoChildren) \"/\" \"INBOX\"");
                            await w.WriteLineAsync($"{tag} OK fertig");
                            break;
                        case "EXAMINE":
                        case "SELECT":
                            await w.WriteLineAsync("* FLAGS (\\Seen \\Deleted)");
                            await w.WriteLineAsync("* 1 EXISTS");
                            await w.WriteLineAsync("* 0 RECENT");
                            await w.WriteLineAsync("* OK [UIDVALIDITY 7] uids");
                            await w.WriteLineAsync("* OK [UIDNEXT 2] next");
                            await w.WriteLineAsync($"{tag} OK [{(cmd == "EXAMINE" ? "READ-ONLY" : "READ-WRITE")}] fertig");
                            break;
                        case "IDLE":
                            idleTag = tag;
                            Interlocked.Increment(ref IdleCommands);
                            await w.WriteLineAsync("+ warte");
                            _idling[id] = w;
                            break;
                        case "LOGOUT":
                            await w.WriteLineAsync("* BYE tschüss");
                            await w.WriteLineAsync($"{tag} OK fertig");
                            c.Close();
                            return;
                        default:
                            await w.WriteLineAsync($"{tag} OK fertig");
                            break;
                    }
                }
            }
            catch { /* Verbindung weg */ }
            finally { _idling.TryRemove(id, out _); }
        }

        public int Idling => _idling.Count;

        /// <summary>Unaufgeforderte Meldung an alle, die gerade in IDLE warten.</summary>
        public async Task Announce(string line)
        {
            foreach (var w in _idling.Values) { try { await w.WriteLineAsync(line); } catch { } }
        }

        /// <summary>Alle Verbindungen hart trennen (Neustart des Mailservers).</summary>
        public void DropAll()
        {
            foreach (var c in _clients) { try { c.Client.Close(); } catch { } }
            _clients.Clear();
            _idling.Clear();
        }

        public void Dispose()
        {
            _listener.Stop();
            DropAll();
        }
    }

    private static UserSpace Space(int port, Action<MailAccount>? change = null)
    {
        var dir = Directory.CreateTempSubdirectory("dmpush");
        var db = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "t");
        var space = new UserSpace(new UserRecord { Login = "t" }, db);
        var a = new MailAccount { Id = "a", Email = "ich@x.de", ImapHost = "127.0.0.1", ImapPort = port, ImapSecurity = Security.None, ImapUser = "ich", ImapPassword = "pw" };
        change?.Invoke(a);
        space.UpdateSettings(s => s.MailAccounts.Add(a));
        return space;
    }

    private static async Task Until(Func<bool> cond, TimeSpan max, string what)
    {
        var end = DateTime.UtcNow + max;
        while (!cond())
        {
            if (DateTime.UtcNow > end) throw new Xunit.Sdk.XunitException("Nicht eingetreten: " + what);
            await Task.Delay(50);
        }
    }

    private static bool Kicked(MailSync sync, UserSpace space)
    {
        var s = sync.StateOf(space.User.Id, "a");
        lock (s)
        {
            var hit = s.KickFolders.Contains("INBOX");
            s.KickFolders.Clear();
            return hit;
        }
    }

    private static async Task WithServer(FakeImap server, Func<Task> body)
    {
        var refused = ImapPool.Refused;
        ImapPool.Refused = (a, port) => port != server.Port && refused(a, port);
        try { await body(); }
        finally { ImapPool.Refused = refused; }
    }

    [Fact]
    public async Task Neue_Mail_stoesst_sofort_den_Abgleich_an_und_nach_Abbruch_geht_es_von_selbst_weiter()
    {
        using var server = new FakeImap();
        await WithServer(server, async () =>
        {
            var space = Space(server.Port);
            var sync = new MailSync(null!, new ImapPool(), NullLogger<MailSync>.Instance);
            var push = new MailPush(() => [space], sync, NullLogger<MailPush>.Instance);
            using var stop = new CancellationTokenSource();
            try
            {
                push.Reconcile(stop.Token);
                await Until(() => push.Live(space.User.Id, "a") && server.Idling == 1, TimeSpan.FromSeconds(10), "IDLE steht");
                Assert.True(sync.PushLive!(space.User.Id, "a"));
                Assert.False(Kicked(sync, space));   // erster Aufbau: kein eigener Abgleich, den macht der Takt

                // Neue Mail: der Abgleich des Posteingangs ist angestoßen, nicht erst im Takt
                var t0 = DateTime.UtcNow;
                await server.Announce("* 2 EXISTS");
                await Until(() => Kicked(sync, space), TimeSpan.FromSeconds(3), "Abgleich nach EXISTS");
                Assert.True(DateTime.UtcNow - t0 < TimeSpan.FromSeconds(2));

                // Gelesen-Markierung aus einem anderen Programm
                await server.Announce("* 1 FETCH (FLAGS (\\Seen))");
                await Until(() => Kicked(sync, space), TimeSpan.FromSeconds(3), "Abgleich nach FETCH");

                // Viele Meldungen auf einmal ergeben einen Anstoß, nicht hunderte
                for (var i = 0; i < 50; i++) await server.Announce($"* {i + 1} FETCH (FLAGS (\\Seen))");
                await Until(() => Kicked(sync, space), TimeSpan.FromSeconds(3), "Abgleich nach vielen FETCH");
                await Task.Delay(MailPush.Settle * 3);
                Assert.False(Kicked(sync, space));

                // Mailserver startet neu: Verbindung weg, dann von selbst wieder da - samt Abgleich für die Lücke
                server.DropAll();
                await Until(() => !push.Live(space.User.Id, "a"), TimeSpan.FromSeconds(5), "Abbruch bemerkt");
                await Until(() => push.Live(space.User.Id, "a") && server.Idling == 1, TimeSpan.FromSeconds(15), "wieder verbunden");
                await Until(() => Kicked(sync, space), TimeSpan.FromSeconds(3), "Abgleich nach Wiederaufbau");
                Assert.Equal(1, push.Count);
            }
            finally
            {
                space.UpdateSettings(s => s.MailAccounts.Clear());
                push.Reconcile(CancellationToken.None);
                stop.Cancel();
            }
        });
    }

    [Fact]
    public async Task IDLE_wird_erneuert_und_Aenderungen_am_Konto_bauen_neu_auf()
    {
        using var server = new FakeImap();
        await WithServer(server, async () =>
        {
            var space = Space(server.Port);
            var sync = new MailSync(null!, new ImapPool(), NullLogger<MailSync>.Instance);
            var push = new MailPush(() => [space], sync, NullLogger<MailPush>.Instance);
            var renew = MailPush.Renew;
            MailPush.Renew = TimeSpan.FromMilliseconds(700);
            try
            {
                push.Reconcile(CancellationToken.None);
                await Until(() => server.IdleCommands >= 3, TimeSpan.FromSeconds(10), "IDLE erneuert");
                Assert.Equal(1, server.Connections);   // erneuert in derselben Verbindung

                // Passwort geändert: alte Verbindung zu, neue auf
                space.UpdateSettings(s => s.MailAccounts[0].ImapPassword = "neu");
                push.Reconcile(CancellationToken.None);
                await Until(() => server.Connections == 2 && push.Live(space.User.Id, "a"), TimeSpan.FromSeconds(10), "neu aufgebaut");

                // Konto deaktiviert: keine Verbindung mehr
                space.UpdateSettings(s => s.MailAccounts[0].Disabled = true);
                push.Reconcile(CancellationToken.None);
                Assert.Equal(0, push.Count);
                Assert.False(push.Live(space.User.Id, "a"));
            }
            finally
            {
                MailPush.Renew = renew;
                space.UpdateSettings(s => s.MailAccounts.Clear());
                push.Reconcile(CancellationToken.None);
            }
        });
    }

    [Fact]
    public async Task Ohne_IDLE_bleibt_es_beim_Takt_ohne_staendig_neu_zu_verbinden()
    {
        using var server = new FakeImap(idle: false);
        await WithServer(server, async () =>
        {
            var space = Space(server.Port);
            var sync = new MailSync(null!, new ImapPool(), NullLogger<MailSync>.Instance);
            var push = new MailPush(() => [space], sync, NullLogger<MailPush>.Instance);
            try
            {
                push.Reconcile(CancellationToken.None);
                await Until(() => server.Connections == 1, TimeSpan.FromSeconds(5), "verbunden");
                await Task.Delay(2000);
                Assert.Equal(1, server.Connections);
                Assert.False(push.Live(space.User.Id, "a"));
                Assert.Equal(0, server.IdleCommands);
            }
            finally
            {
                space.UpdateSettings(s => s.MailAccounts.Clear());
                push.Reconcile(CancellationToken.None);
            }
        });
    }

    [Fact]
    public void Nur_IMAP_Konten_mit_Passwort_und_ohne_Sperre()
    {
        Assert.True(MailPush.Eligible(new MailAccount { ImapHost = "imap.x.de" }));
        Assert.False(MailPush.Eligible(new MailAccount { ImapHost = "" }));
        Assert.False(MailPush.Eligible(new MailAccount { ImapHost = "outlook.office365.com", Auth = MailAuth.Microsoft }));
        Assert.False(MailPush.Eligible(new MailAccount { ImapHost = "imap.x.de", Disabled = true }));
    }

    [Fact]
    public void Wiederaufbau_mit_wachsendem_Abstand()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), MailPush.Backoff(1, false));
        Assert.Equal(TimeSpan.FromSeconds(15), MailPush.Backoff(2, false));
        Assert.Equal(TimeSpan.FromMinutes(10), MailPush.Backoff(7, false));
        Assert.Equal(TimeSpan.FromMinutes(10), MailPush.Backoff(100, false));
        Assert.Equal(TimeSpan.FromMinutes(30), MailPush.Backoff(1, true));
    }

    [Fact]
    public async Task Long_Poll_wartet_auf_die_Version_und_kommt_nach_Neustart_gleich_zurueck()
    {
        var sync = new MailSync(null!, new ImapPool(), NullLogger<MailSync>.Instance);
        // Anderer Stand als hier (Server neu gestartet, Version wieder klein): sofort zurück
        Assert.Equal(0, await sync.WaitAsync("u", 57, TimeSpan.FromSeconds(5), default));
        // Gleicher Stand: wartet bis zur Änderung
        var wait = sync.WaitAsync("u", 0, TimeSpan.FromSeconds(10), default);
        await Task.Delay(100);
        Assert.False(wait.IsCompleted);
        sync.Touch("u");
        Assert.Equal(1, await wait.WaitAsync(TimeSpan.FromSeconds(2)));
        // Ohne Änderung: nach der Wartezeit mit demselben Stand
        Assert.Equal(1, await sync.WaitAsync("u", 1, TimeSpan.FromMilliseconds(200), default));
    }
}
