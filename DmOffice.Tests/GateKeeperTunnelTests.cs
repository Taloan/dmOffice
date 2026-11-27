using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using DmOffice.Core;
using DmOffice.GateKeeper;
using DmOffice.Plugins;
using P = DmOffice.GateKeeper.GateKeeperProtocol;

namespace DmOffice.Tests;

/// <summary>GateKeeper-Tunnel (Protokoll v1) über ein WebSocket-Paar auf Loopback: Verbinden, Sperren, DNS.</summary>
public sealed class GateKeeperTunnelTests : IAsyncDisposable
{
    private readonly TcpListener _echo = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(30));
    private readonly List<string> _blocked = [];
    private readonly List<(long Up, long Down)> _traffic = [];
    private WebSocket? _client, _server;
    private Task? _run;

    public GateKeeperTunnelTests()
    {
        _echo.Start();
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await _echo.AcceptTcpClientAsync(_cts.Token); } catch (Exception) { return; }
                _ = Task.Run(async () =>
                {
                    using (c)
                    {
                        var s = c.GetStream();
                        var buf = new byte[4096];
                        int n;
                        try { while ((n = await s.ReadAsync(buf, _cts.Token)) > 0) await s.WriteAsync(buf.AsMemory(0, n), _cts.Token); }
                        catch (Exception) { }
                    }
                });
            }
        });
    }

    private int EchoPort => ((IPEndPoint)_echo.LocalEndpoint).Port;

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _echo.Stop();
        _client?.Abort();
        _server?.Abort();
        if (_run != null) try { await _run; } catch (Exception) { }
    }

    /// <summary>Tunnel starten; liefert die App-Seite des WebSockets (HELLO schon gelesen).</summary>
    private async Task StartAsync(GkListEntries lists, Func<GkTunnelOptions, GkTunnelOptions>? tweak = null, IPEndPoint? dns = null)
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var tc = new TcpClient();
        await tc.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)l.LocalEndpoint).Port);
        var sc = await l.AcceptTcpClientAsync();
        _client = WebSocket.CreateFromStream(tc.GetStream(), false, null, TimeSpan.Zero);
        _server = WebSocket.CreateFromStream(sc.GetStream(), true, null, TimeSpan.Zero);
        var opts = new GkTunnelOptions
        {
            AllowLoopback = true,
            DnsUpstream = dns,
            Check = name => GateKeeperFilter.Check(name, lists, new HashSet<string>(), new HashSet<string>()),
            OnBlocked = d => { lock (_blocked) _blocked.Add(d); },
            OnTraffic = (up, down) => { lock (_traffic) _traffic.Add((up, down)); },
            TlsPort = EchoPort,
            PeekTimeout = TimeSpan.FromSeconds(2),
        };
        if (tweak != null) opts = tweak(opts);
        _run = new GateKeeperTunnel(_server, opts).RunAsync(_cts.Token);
        var hello = await ReceiveAsync();
        Assert.Equal(P.Hello, hello.Type);
    }

    private async Task<(byte Type, uint Stream, byte[] Payload)> ReceiveAsync()
    {
        var buf = new byte[P.MaxMessage];
        int len = 0;
        WebSocketReceiveResult r;
        do
        {
            r = await _client!.ReceiveAsync(new ArraySegment<byte>(buf, len, buf.Length - len), _cts.Token);
            len += r.Count;
        } while (!r.EndOfMessage);
        var f = P.Parse(buf.AsMemory(0, len))!.Value;
        return (f.Type, f.Stream, f.Payload.ToArray());
    }

    /// <summary>Nächster Rahmen außer WINDOW.</summary>
    private async Task<(byte Type, uint Stream, byte[] Payload)> NextAsync()
    {
        while (true)
        {
            var f = await ReceiveAsync();
            if (f.Type != P.Window) return f;
        }
    }

    private Task SendAsync(byte[] frame) => _client!.SendAsync(frame, WebSocketMessageType.Binary, true, _cts.Token);

    [Fact]
    public async Task Daten_gehen_durch_und_kommen_zurueck()
    {
        await StartAsync(new GkListEntries());
        await SendAsync(P.OpenFrame(1, new IPEndPoint(IPAddress.Loopback, EchoPort)));
        // EchoPort ist hier der TLS-Port: Erst ein ClientHello ohne Sperre, danach Daten
        var hello = await GateKeeperFilterTests.ClientHelloAsync("allowed.example");
        await SendAsync(P.Frame(P.Data, 1, hello));
        var echoed = new List<byte>();
        while (echoed.Count < hello.Length)
        {
            var f = await NextAsync();
            Assert.Equal(P.Data, f.Type);
            Assert.Equal(1u, f.Stream);
            echoed.AddRange(f.Payload);
        }
        Assert.Equal(hello, echoed.ToArray());
        await SendAsync(P.Frame(P.Fin, 1, []));
        var fin = await NextAsync();
        Assert.Equal(P.Fin, fin.Type);
        Assert.Empty(_blocked);
        // Nach dem Ende zählt die Verbindung mit ihren Bytes in beide Richtungen
        await SendAsync(P.ResetFrame(1, P.Reason.Abort));
        for (int i = 0; i < 50 && _traffic.Count == 0; i++) await Task.Delay(20);
        lock (_traffic) Assert.Equal(((long)hello.Length, (long)hello.Length), Assert.Single(_traffic));
    }

    [Fact]
    public async Task Andere_Ports_verbinden_sofort_ohne_Pruefung()
    {
        await StartAsync(new GkListEntries(), o => new GkTunnelOptions
        {
            AllowLoopback = true, Check = o.Check, OnBlocked = o.OnBlocked, TlsPort = 443,
        });
        await SendAsync(P.OpenFrame(5, new IPEndPoint(IPAddress.Loopback, EchoPort)));
        await SendAsync(P.Frame(P.Data, 5, "hallo"u8));
        var f = await NextAsync();
        Assert.Equal(P.Data, f.Type);
        Assert.Equal("hallo", Encoding.ASCII.GetString(f.Payload));
    }

    [Fact]
    public async Task Gesperrter_SNI_ergibt_RESET_Blocked()
    {
        await StartAsync(GateKeeperFilter.Parse("||ads.example^"));
        await SendAsync(P.OpenFrame(3, new IPEndPoint(IPAddress.Loopback, EchoPort)));
        await SendAsync(P.Frame(P.Data, 3, await GateKeeperFilterTests.ClientHelloAsync("x.ads.example")));
        var f = await NextAsync();
        Assert.Equal(P.Reset, f.Type);
        Assert.Equal(3u, f.Stream);
        Assert.Equal((byte)P.Reason.Blocked, f.Payload[0]);
        Assert.Equal(["x.ads.example"], _blocked);
    }

    [Fact]
    public async Task Gesperrter_HTTP_Host_ergibt_RESET_Blocked()
    {
        await StartAsync(GateKeeperFilter.Parse("0.0.0.0 ads.example.com"), o => new GkTunnelOptions
        {
            AllowLoopback = true, Check = o.Check, OnBlocked = o.OnBlocked, HttpPort = EchoPort,
        });
        await SendAsync(P.OpenFrame(4, new IPEndPoint(IPAddress.Loopback, EchoPort)));
        await SendAsync(P.Frame(P.Data, 4, "GET / HTTP/1.1\r\nHost: ads.example.com\r\n\r\n"u8));
        var f = await NextAsync();
        Assert.Equal(P.Reset, f.Type);
        Assert.Equal((byte)P.Reason.Blocked, f.Payload[0]);
    }

    [Fact]
    public async Task Eigenes_Netz_und_Loopback_ohne_Erlaubnis_ergibt_NotAllowed()
    {
        await StartAsync(new GkListEntries(), o => new GkTunnelOptions { Check = o.Check });
        await SendAsync(P.OpenFrame(1, new IPEndPoint(IPAddress.Parse("192.168.1.10"), 80)));
        await SendAsync(P.OpenFrame(2, new IPEndPoint(IPAddress.Loopback, EchoPort)));
        await SendAsync(P.OpenFrame(3, new IPEndPoint(IPAddress.Parse("169.254.169.254"), 80)));
        for (int i = 0; i < 3; i++)
        {
            var f = await NextAsync();
            Assert.Equal(P.Reset, f.Type);
            Assert.Equal((byte)P.Reason.NotAllowed, f.Payload[0]);
        }
    }

    [Fact]
    public async Task Kaputtes_OPEN_ergibt_RESET_Protocol()
    {
        await StartAsync(new GkListEntries());
        await SendAsync(P.Frame(P.Open, 9, [4, 1, 2]));
        var f = await NextAsync();
        Assert.Equal(P.Reset, f.Type);
        Assert.Equal((byte)P.Reason.Protocol, f.Payload[0]);
    }

    [Fact]
    public async Task DNS_gesperrt_beantwortet_der_Tunnel_sonst_der_Resolver()
    {
        // Gefälschter Resolver: antwortet mit der Anfrage, QR gesetzt
        using var upstream = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var r = await upstream.ReceiveAsync(_cts.Token);
                    var a = r.Buffer.ToArray();
                    a[2] |= 0x80;
                    await upstream.SendAsync(a, r.RemoteEndPoint, _cts.Token);
                }
            }
            catch (Exception) { }
        });
        await StartAsync(GateKeeperFilter.Parse("||ads.example^"), dns: (IPEndPoint)upstream.Client.LocalEndPoint!);

        await SendAsync(P.Frame(P.Dns, 77, GateKeeperFilterTests.DnsQuery("ads.example", 1, 0x0101)));
        var blocked = await NextAsync();
        Assert.Equal(P.Dns, blocked.Type);
        Assert.Equal(77u, blocked.Stream);
        Assert.Equal([0, 4, 0, 0, 0, 0], blocked.Payload[^6..]);

        var q = GateKeeperFilterTests.DnsQuery("ok.example", 1, 0x0202);
        await SendAsync(P.Frame(P.Dns, 78, q));
        var passed = await NextAsync();
        Assert.Equal(78u, passed.Stream);
        Assert.Equal(q.Length, passed.Payload.Length);
        Assert.True((passed.Payload[2] & 0x80) != 0);
        Assert.Equal(["ads.example"], _blocked);
    }

    [Fact]
    public async Task DNS_ueber_TCP_an_die_Adresse_im_VPN()
    {
        await StartAsync(GateKeeperFilter.Parse("||ads.example^"));
        await SendAsync(P.OpenFrame(11, new IPEndPoint(P.VirtualDns4, 53)));
        var q = GateKeeperFilterTests.DnsQuery("ads.example", 28);
        var framed = new byte[2 + q.Length];
        BinaryPrimitives.WriteUInt16BigEndian(framed, (ushort)q.Length);
        q.CopyTo(framed, 2);
        await SendAsync(P.Frame(P.Data, 11, framed));
        var f = await NextAsync();
        Assert.Equal(P.Data, f.Type);
        Assert.Equal(11u, f.Stream);
        Assert.Equal(f.Payload.Length - 2, BinaryPrimitives.ReadUInt16BigEndian(f.Payload));
        Assert.All(f.Payload[^16..], b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task Ohne_IPv6_AAAA_leer_und_IPv6_Ziele_sofort_RESET()
    {
        await StartAsync(new GkListEntries(), o => new GkTunnelOptions { AllowLoopback = true, Check = o.Check, Ipv6 = () => false });
        // AAAA: NOERROR ohne Einträge (ohne Resolver käme sonst SERVFAIL)
        await SendAsync(P.Frame(P.Dns, 5, GateKeeperFilterTests.DnsQuery("www.example.org", 28, 0x0505)));
        var aaaa = await NextAsync();
        Assert.Equal(5u, aaaa.Stream);
        Assert.Equal(0x80, aaaa.Payload[3]);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(aaaa.Payload.AsSpan(6)));
        // A geht wie bisher zum Resolver (hier keiner: SERVFAIL)
        await SendAsync(P.Frame(P.Dns, 6, GateKeeperFilterTests.DnsQuery("www.example.org", 1, 0x0606)));
        Assert.Equal(2, (await NextAsync()).Payload[3] & 0x0f);
        // IPv6-Ziel: sofort RESET, ohne Verbindungsversuch
        await SendAsync(P.OpenFrame(7, new IPEndPoint(IPAddress.Parse("2606:4700::1"), 443)));
        var f = await NextAsync();
        Assert.Equal(P.Reset, f.Type);
        Assert.Equal(7u, f.Stream);
        Assert.Equal((byte)P.Reason.ConnectFailed, f.Payload[0]);
    }

    [Fact]
    public void IPv6_global_nur_2000_bis_3fff()
    {
        Assert.True(GateKeeperIpv6.GlobalUnicast(IPAddress.Parse("2a02:2e0:3fe:1001::1")));
        Assert.False(GateKeeperIpv6.GlobalUnicast(IPAddress.Parse("fe80::1")));
        Assert.False(GateKeeperIpv6.GlobalUnicast(IPAddress.Parse("fd00:6d6f:6666::2")));
        Assert.False(GateKeeperIpv6.GlobalUnicast(IPAddress.IPv6Loopback));
        Assert.False(GateKeeperIpv6.GlobalUnicast(IPAddress.Parse("::ffff:1.2.3.4")));
        Assert.False(GateKeeperIpv6.GlobalUnicast(IPAddress.Parse("1.2.3.4")));
    }

    [Fact]
    public async Task Tunnel_endet_wenn_er_nicht_mehr_gelten_darf()
    {
        var allowed = true;
        await StartAsync(new GkListEntries(), o => new GkTunnelOptions
        {
            Check = o.Check, StillAllowed = () => allowed, RecheckEvery = TimeSpan.FromMilliseconds(100),
        });
        allowed = false;
        await _run!.WaitAsync(TimeSpan.FromSeconds(5));
    }
}

/// <summary>Datenhaltung von GateKeeper (plugins/gatekeeper.db): Listen, Regeln, Statistik; App-Listen und Auswahl bleiben auf dem Handy.</summary>
public sealed class GateKeeperStoreTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("dmgk");
    private readonly GateKeeperStore _store;

    private readonly ServerDataStore _data;

    public GateKeeperStoreTests()
    {
        var paths = new DataPaths(_dir.FullName);
        _data = new ServerDataStore(paths, new Vault(paths, null));
        _store = new GateKeeperStore(_data);
    }

    public void Dispose()
    {
        try { _dir.Delete(true); } catch { /* Testordner */ }
    }

    [Fact]
    public void Listen_anlegen_aendern_entfernen()
    {
        Assert.Throws<ArgumentException>(() => _store.AddList("ftp://x.example/list.txt", null));
        var l = _store.AddList("https://lists.example/hosts.txt", null);
        Assert.Equal("lists.example", l.Name);
        Assert.True(l.Enabled);
        Assert.Throws<ArgumentException>(() => _store.AddList("https://lists.example/hosts.txt", "doppelt"));
        _store.ListFetched(l.Id, "ok", 42);
        Assert.Equal(42, _store.List(l.Id)!.Entries);
        _store.UpdateList(l.Id, "Meine Liste", false, "https://lists.example/other.txt");
        var u = _store.List(l.Id)!;
        Assert.Equal(("Meine Liste", false, 0, (long?)null), (u.Name, u.Enabled, u.Entries, u.Fetched));
        _store.DeleteList(l.Id);
        Assert.Empty(_store.Lists());
    }

    [Fact]
    public void Einstellungen_mit_Grenzen()
    {
        Assert.Equal(24, _store.RefreshHours);
        _store.RefreshHours = 1000;
        Assert.Equal(168, _store.RefreshHours);
        Assert.False(_store.AllowPrivate);
        _store.AllowPrivate = true;
        Assert.True(_store.AllowPrivate);
    }

    [Fact]
    public void Keine_App_Listen_Geraete_oder_Auswahl_in_der_Datenbank()
    {
        var tables = _data.Open(GateKeeperStore.Key).Query("SELECT name FROM sqlite_master WHERE type='table'", r => r.GetString(0));
        Assert.DoesNotContain("gk_app", tables);
        Assert.DoesNotContain("gk_user", tables);
    }

    [Fact]
    public void Frueher_gemeldete_Apps_und_Auswahl_werden_beim_Start_geloescht()
    {
        // Datenbank im Stand vor der Datensparsamkeit (Schema 2) mit gemeldeten Apps, Gerätenamen und Auswahl
        var dir = Directory.CreateTempSubdirectory("dmgk-alt");
        try
        {
            var paths = new DataPaths(dir.FullName);
            var data = new ServerDataStore(paths, new Vault(paths, null));
            var db = data.Open(GateKeeperStore.Key);
            db.Apply(GateKeeperStore.Key, GateKeeperStore.Schema.Where(s => s.Version <= 2).ToList());
            db.Execute("INSERT INTO gk_user (user, enabled, packages) VALUES ('anna', 1, '[\"com.example.app\"]')");
            db.Execute("INSERT INTO gk_app (user, device, package, label, seen) VALUES ('anna', 'Pixel 8', 'com.example.app', 'Beispiel', 1)");
            db.Execute("INSERT INTO gk_rule (user, domain, action, created) VALUES ('anna', 'ads.example', 'block', 1)");

            var store = new GateKeeperStore(data);
            var tables = db.Query("SELECT name FROM sqlite_master WHERE type='table'", r => r.GetString(0));
            Assert.DoesNotContain("gk_app", tables);
            Assert.DoesNotContain("gk_user", tables);
            // Eigene Regeln bleiben
            Assert.Equal("ads.example", Assert.Single(store.Rules("anna")).Domain);
            // Ein zweiter Start ändert nichts mehr
            Assert.Single(new GateKeeperStore(data).Rules("anna"));
            // Auch in der Datei steht der Gerätename nicht mehr (secure_delete)
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var f in Directory.GetFiles(Path.Combine(dir.FullName, "plugins")))
                Assert.DoesNotContain("Pixel 8", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(f)));
        }
        finally { try { dir.Delete(true); } catch { /* Testordner */ } }
    }

    [Fact]
    public void Eigene_Regeln()
    {
        Assert.Throws<ArgumentException>(() => _store.SetRule("anna", "x.example", "maybe"));
        _store.SetRule("anna", "Ads.Example.", "block");
        _store.SetRule("anna", "good.ads.example", "allow");
        var r = _store.RulesOf("anna");
        Assert.Equal(["ads.example"], r.Block);
        Assert.Equal(["good.ads.example"], r.Allow);
        _store.SetRule("anna", "ads.example", "allow");
        Assert.Empty(_store.RulesOf("anna").Block);
        _store.DeleteRule("anna", "ADS.example");
        Assert.Single(_store.Rules("anna"));
        Assert.Empty(_store.Rules("ben"));

        _store.Forget("anna");
        Assert.Empty(_store.Rules("anna"));
    }

    [Fact]
    public void Statistik_zaehlt_und_wird_zurueckgesetzt()
    {
        var stats = new GateKeeperStats(_store, Microsoft.Extensions.Logging.Abstractions.NullLogger<GateKeeperStats>.Instance);
        stats.Connection("anna");
        stats.Connection("anna");
        stats.Dns("anna");
        stats.Blocked("anna", "ads.example");
        stats.Blocked("anna", "ads.example");
        stats.Blocked("anna", "t.example");
        stats.Flush();
        stats.Blocked("anna", "ads.example");
        stats.Flush();
        var day = Assert.Single(_store.Days("anna", 30));
        Assert.Equal((2L, 1L, 4L), (day.Connections, day.Dns, day.Blocked));
        var hits = _store.Hits("anna", "hits", 10);
        Assert.Equal(("ads.example", 3L), (hits[0].Domain, hits[0].Hits));
        Assert.Empty(_store.Days("ben", 30));
        _store.ClearStats("anna");
        Assert.Empty(_store.Days("anna", 30));
        Assert.Empty(_store.Hits("anna", "last", 10));
    }

    [Fact]
    public void Statistik_zaehlt_Datenverkehr_und_schaetzt_den_Median()
    {
        var stats = new GateKeeperStats(_store, Microsoft.Extensions.Logging.Abstractions.NullLogger<GateKeeperStats>.Instance);
        // drei kleine Verbindungen (je 3000 Bytes) und eine große (100 MB): der Median bleibt bei den kleinen
        for (int i = 0; i < 3; i++) stats.Traffic("anna", 1000, 2000);
        stats.Flush();
        stats.Traffic("anna", 100, 100_000_000);
        stats.Flush();
        var day = Assert.Single(_store.Days("anna", 30));
        Assert.Equal((3100L, 100_006_000L), (day.Up, day.Down));
        var median = GkSizes.Median(day.Sizes);
        Assert.InRange(median, 2048, 4096);
        var (all, people) = Assert.Single(_store.AllDays(30));
        Assert.Equal((1, 3100L), (people, all.Up));
        Assert.Equal(0, GkSizes.Bucket(0));
        Assert.Equal(1, GkSizes.Bucket(1));
        Assert.Equal(GkSizes.Count - 1, GkSizes.Bucket(long.MaxValue));
    }
}
