using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using DmOffice.GateKeeper;

namespace DmOffice.Tests;

/// <summary>GateKeeper (Kennung gatekeeper): Filterlisten lesen, Namen prüfen, SNI/Host lesen, DNS-Antworten.</summary>
public class GateKeeperFilterTests
{
    private static readonly HashSet<string> None = [];
    private static HashSet<string> S(params string[] names) => [.. names];

    [Fact]
    public void Listen_Hosts_Domainliste_und_AdBlock_werden_gelesen()
    {
        var e = GateKeeperFilter.Parse("""
            # Kommentar
            ! AdBlock-Kommentar
            [Adblock Plus 2.0]
            0.0.0.0 ads.example.com tracker.example.com # zwei Namen
            127.0.0.1 localhost
            0.0.0.0 0.0.0.0
            ::1 ip6-localhost
            ||doubleads.example^
            ||withpath.example/banner
            ||withopt.example^$third-party
            @@||good.doubleads.example^
            example.org##.banner
            plainlist.example
            *.wild.example
            /regex/
            192.168.1.1
            """);
        Assert.Equal(["ads.example.com", "tracker.example.com"], e.Exact.Order());
        Assert.Equal(["doubleads.example", "plainlist.example"], e.Tree.Order());
        Assert.Equal(["good.doubleads.example"], e.Allow);
        Assert.Equal(5, e.Count);
    }

    [Fact]
    public void Hosts_Eintrag_gilt_nur_fuer_genau_den_Namen_Domainregel_samt_Unterdomains()
    {
        var e = GateKeeperFilter.Parse("0.0.0.0 ads.example.com\n||tracker.example^\n");
        Assert.True(GateKeeperFilter.Check("ads.example.com", e, None, None).Blocked);
        Assert.False(GateKeeperFilter.Check("x.ads.example.com", e, None, None).Blocked);
        Assert.True(GateKeeperFilter.Check("a.b.tracker.example", e, None, None).Blocked);
        Assert.True(GateKeeperFilter.Check("TRACKER.example.", e, None, None).Blocked);
        Assert.False(GateKeeperFilter.Check("nottracker.example", e, None, None).Blocked);
        Assert.False(GateKeeperFilter.Check(null, e, None, None).Blocked);
    }

    [Fact]
    public void Reihenfolge_eigene_Erlaubnis_eigene_Sperre_Listen_Erlaubnis_Listen_Sperre()
    {
        var e = GateKeeperFilter.Parse("||ads.example^\n@@||ok.ads.example^\n");
        Assert.Equal(new GkVerdict(true, "list"), GateKeeperFilter.Check("x.ads.example", e, None, None));
        Assert.Equal(new GkVerdict(false, "list-allow"), GateKeeperFilter.Check("a.ok.ads.example", e, None, None));
        // eigene Sperre schlägt die Erlaubnis der Liste
        Assert.Equal(new GkVerdict(true, "user-block"), GateKeeperFilter.Check("ok.ads.example", e, None, S("ads.example")));
        // eigene Erlaubnis schlägt alles
        Assert.Equal(new GkVerdict(false, "user-allow"), GateKeeperFilter.Check("x.ads.example", e, S("x.ads.example"), S("ads.example")));
        Assert.Equal(GkVerdict.Pass, GateKeeperFilter.Check("other.example", e, None, None));
    }

    [Theory]
    [InlineData("Example.COM.", "example.com")]
    [InlineData("*.example.com", "example.com")]
    [InlineData("bad name", null)]
    [InlineData("a..b", null)]
    [InlineData("", null)]
    public void Namen_werden_vereinheitlicht(string input, string? expected) => Assert.Equal(expected, GateKeeperFilter.Normalize(input));

    // ------------------------------------------------------------ SNI

    /// <summary>Einen echten ClientHello aufnehmen: SslStream gegen einen Socket, der nur mitliest.</summary>
    internal static async Task<byte[]> ClientHelloAsync(string host)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var server = await listener.AcceptTcpClientAsync();
        var ssl = new SslStream(client.GetStream(), false, (_, _, _, _) => true);
        var handshake = ssl.AuthenticateAsClientAsync(host);
        var buf = new byte[GateKeeperFilter.MaxPeek];
        int len = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            len += await server.GetStream().ReadAsync(buf.AsMemory(len), cts.Token);
            if (GateKeeperFilter.ReadSni(buf.AsSpan(0, len), out _) != GateKeeperFilter.ParseState.NeedMore) break;
        }
        server.Close();
        try { await handshake; } catch (Exception) { /* Gegenstelle zu */ }
        return buf[..len];
    }

    [Fact]
    public async Task SNI_aus_echtem_ClientHello()
    {
        var hello = await ClientHelloAsync("ads.example.com");
        Assert.Equal(GateKeeperFilter.ParseState.Done, GateKeeperFilter.ReadSni(hello, out var name));
        Assert.Equal("ads.example.com", name);
        // Stückweise: bis zum Ende fehlt etwas
        Assert.Equal(GateKeeperFilter.ParseState.NeedMore, GateKeeperFilter.ReadSni(hello.AsSpan(0, 3), out _));
        Assert.Equal(GateKeeperFilter.ParseState.NeedMore, GateKeeperFilter.ReadSni(hello.AsSpan(0, hello.Length - 1), out _));
    }

    [Fact]
    public async Task SNI_ueber_zwei_TLS_Records_verteilt()
    {
        var hello = await ClientHelloAsync("split.example.org");
        var recLen = BinaryPrimitives.ReadUInt16BigEndian(hello.AsSpan(3));
        var body = hello.AsSpan(5, recLen);
        var half = body.Length / 2;
        var split = new List<byte>();
        foreach (var part in new[] { body[..half].ToArray(), body[half..].ToArray() })
        {
            split.AddRange([0x16, hello[1], hello[2], (byte)(part.Length >> 8), (byte)part.Length]);
            split.AddRange(part);
        }
        var arr = split.ToArray();
        Assert.Equal(GateKeeperFilter.ParseState.NeedMore, GateKeeperFilter.ReadSni(arr.AsSpan(0, 5 + half + 2), out _));
        Assert.Equal(GateKeeperFilter.ParseState.Done, GateKeeperFilter.ReadSni(arr, out var name));
        Assert.Equal("split.example.org", name);
    }

    [Fact]
    public void Kein_TLS_wird_erkannt()
    {
        Assert.Equal(GateKeeperFilter.ParseState.NotMatching, GateKeeperFilter.ReadSni("GET / HTTP/1.1\r\n"u8, out _));
        Assert.Equal(GateKeeperFilter.ParseState.NotMatching, GateKeeperFilter.ReadSni([0x16, 0x03, 0x01, 0x00, 0x05, 0x02, 0, 0, 1, 0], out _));
    }

    [Fact]
    public void HTTP_Host_wird_gelesen()
    {
        Assert.Equal(GateKeeperFilter.ParseState.Done, GateKeeperFilter.ReadHttpHost("GET /x HTTP/1.1\r\nUser-Agent: a\r\nhost: Ads.Example.com:8080\r\n\r\n"u8, out var host));
        Assert.Equal("Ads.Example.com", host);  // ohne Port
        Assert.Equal(GateKeeperFilter.ParseState.Done, GateKeeperFilter.ReadHttpHost("GET / HTTP/1.1\r\nHost: [2001:db8::1]:80\r\n\r\n"u8, out var v6));
        Assert.Equal("2001:db8::1", v6);
        Assert.Equal(GateKeeperFilter.ParseState.NeedMore, GateKeeperFilter.ReadHttpHost("GET /x HTTP/1.1\r\nUser-"u8, out _));
        Assert.Equal(GateKeeperFilter.ParseState.NotMatching, GateKeeperFilter.ReadHttpHost([0x16, 0x03, 0x01, 0, 1, 1], out _));
    }

    // ------------------------------------------------------------ DNS

    internal static byte[] DnsQuery(string name, ushort type, ushort id = 0x1234)
    {
        var q = new List<byte> { (byte)(id >> 8), (byte)id, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var label in name.Split('.'))
        {
            q.Add((byte)label.Length);
            q.AddRange(Encoding.ASCII.GetBytes(label));
        }
        q.AddRange([0, (byte)(type >> 8), (byte)type, 0, 1]);
        return q.ToArray();
    }

    [Fact]
    public void DNS_Frage_wird_gelesen()
    {
        var q = DnsQuery("Ads.Example.com", 28);
        var r = GateKeeperFilter.ReadDnsQuestion(q);
        Assert.NotNull(r);
        Assert.Equal("ads.example.com", GateKeeperFilter.Normalize(r!.Value.Name));
        Assert.Equal(28, r.Value.Type);
        Assert.Equal(q.Length, r.Value.End);
        Assert.Null(GateKeeperFilter.ReadDnsQuestion(q.AsSpan(0, 8)));
    }

    [Fact]
    public void Gesperrt_A_ergibt_0000_AAAA_ergibt_Nulladresse_sonst_NXDOMAIN()
    {
        var a = GateKeeperFilter.BlockedDnsAnswer(DnsQuery("ads.example.com", 1));
        Assert.Equal(0x12, a[0]);
        Assert.Equal(0x34, a[1]);
        Assert.True((a[2] & 0x80) != 0);                      // Antwort
        Assert.Equal(0, a[3] & 0x0F);                           // kein Fehler
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(a.AsSpan(6)));  // eine Antwort
        Assert.Equal([0, 4, 0, 0, 0, 0], a[^6..]);             // Länge 4, 0.0.0.0

        var aaaa = GateKeeperFilter.BlockedDnsAnswer(DnsQuery("ads.example.com", 28));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(aaaa.AsSpan(6)));
        Assert.Equal(16, BinaryPrimitives.ReadUInt16BigEndian(aaaa.AsSpan(aaaa.Length - 18)));
        Assert.All(aaaa[^16..], b => Assert.Equal(0, b));

        var txt = GateKeeperFilter.BlockedDnsAnswer(DnsQuery("ads.example.com", 16));
        Assert.Equal(3, txt[3] & 0x0F);                         // NXDOMAIN
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(txt.AsSpan(6)));

        var fail = GateKeeperFilter.FailedDnsAnswer(DnsQuery("x.example", 1));
        Assert.Equal(2, fail[3] & 0x0F);                        // SERVFAIL
    }

    // ------------------------------------------------------------ Protokoll

    [Fact]
    public void Protokoll_Rahmen_und_OPEN()
    {
        var to4 = new IPEndPoint(IPAddress.Parse("93.184.216.34"), 443);
        var f = GateKeeperProtocol.OpenFrame(7, to4);
        var p = GateKeeperProtocol.Parse(f)!.Value;
        Assert.Equal(GateKeeperProtocol.Open, p.Type);
        Assert.Equal(7u, p.Stream);
        Assert.Equal(to4, GateKeeperProtocol.ParseOpen(p.Payload.Span));

        var to6 = new IPEndPoint(IPAddress.Parse("2001:db8::1"), 8443);
        var p6 = GateKeeperProtocol.Parse(GateKeeperProtocol.OpenFrame(uint.MaxValue, to6))!.Value;
        Assert.Equal(uint.MaxValue, p6.Stream);
        Assert.Equal(to6, GateKeeperProtocol.ParseOpen(p6.Payload.Span));

        Assert.Null(GateKeeperProtocol.Parse(new byte[4]));
        Assert.Null(GateKeeperProtocol.ParseOpen([4, 1, 2, 3, 4, 0, 0]));   // Port 0
        Assert.Null(GateKeeperProtocol.ParseOpen([6, 1, 2, 3, 4, 0, 80]));  // falsche Länge

        var hello = GateKeeperProtocol.Parse(GateKeeperProtocol.HelloFrame())!.Value;
        Assert.Equal(GateKeeperProtocol.Hello, hello.Type);
        Assert.Equal(GateKeeperProtocol.Version, hello.Payload.Span[0]);
        Assert.Equal((uint)GateKeeperProtocol.InitialWindow, BinaryPrimitives.ReadUInt32BigEndian(hello.Payload.Span[1..]));
    }
}
