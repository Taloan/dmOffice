using System.Net;
using DmOffice.Core;
using Xunit;

namespace DmOffice.Tests;

public class ProxyConfirmTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dmproxy").FullName;
    private string File => Path.Combine(_dir, "proxies.json");
    private static readonly IPNetwork[] Legacy = [IPNetwork.Parse("172.18.0.0/16")];
    private static readonly DateTime T0 = new(2026, 11, 23, 10, 0, 0, DateTimeKind.Utc);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public void Nach_dem_Update_wird_der_bisherige_Proxy_uebernommen()
    {
        var pc = new ProxyConfirm(File, Legacy, T0);
        Assert.True(pc.Trusted(IPAddress.Parse("172.18.0.2"), "office.example", T0.AddMinutes(5)));
        Assert.True(pc.Trusted(IPAddress.Parse("::ffff:172.18.0.2"), null, T0.AddMinutes(6)));
        var e = Assert.Single(pc.List());
        Assert.Equal(ProxyConfirm.Auto, e.State);
        Assert.Equal("office.example", e.Host);
        Assert.Equal(2, e.Hits);
        // Fremder Absender im Heimnetz: nur festgehalten, nicht geglaubt
        Assert.False(pc.Trusted(IPAddress.Parse("192.168.178.20"), null, T0.AddMinutes(7)));
        Assert.Equal(ProxyConfirm.Seen, pc.List().Single(x => x.Address == "192.168.178.20").State);
        // Diese Maschine selbst gilt immer, ohne Eintrag
        Assert.True(pc.Trusted(IPAddress.Loopback, null));
        Assert.Equal(2, pc.List().Count);
        // Übernommener Proxy gilt auch nach der Übernahmezeit und nach einem Neustart
        var again = new ProxyConfirm(File, Legacy, T0.AddDays(3));
        Assert.Equal(T0 + ProxyConfirm.LearningTime, again.LearnUntil);
        Assert.True(again.Trusted(IPAddress.Parse("172.18.0.2"), null, T0.AddDays(3)));
    }

    [Fact]
    public void Neuer_Proxy_erst_nach_Bestaetigung()
    {
        var pc = new ProxyConfirm(File, Legacy, T0);
        var later = T0 + ProxyConfirm.LearningTime + TimeSpan.FromMinutes(1);
        Assert.False(pc.Learning(later));
        // Nach der Übernahmezeit übernimmt auch die bisherige Regel nichts mehr von selbst
        Assert.False(pc.Trusted(IPAddress.Parse("172.18.0.9"), null, later));
        Assert.Equal(ProxyConfirm.Seen, Assert.Single(pc.List()).State);
        pc.Confirm("172.18.0.9", "admin");
        Assert.True(pc.Trusted(IPAddress.Parse("172.18.0.9"), null, later));
        var e = Assert.Single(pc.List());
        Assert.Equal(ProxyConfirm.Confirmed, e.State);
        Assert.Equal("admin", e.By);
        Assert.True(pc.Remove("172.18.0.9"));
        Assert.False(pc.Trusted(IPAddress.Parse("172.18.0.9"), null, later));
        Assert.Throws<KeyNotFoundException>(() => pc.Confirm("203.0.113.1", "admin"));
        Assert.Throws<ArgumentException>(() => pc.Confirm("kein-proxy", "admin"));
    }

    [Fact]
    public void Unbestaetigte_Absender_begrenzt_Host_ohne_Steuerzeichen()
    {
        var pc = new ProxyConfirm(File, [], T0);
        for (var i = 0; i < ProxyConfirm.MaxSeen + 20; i++)
            pc.Trusted(IPAddress.Parse("203.0.113." + (i % 250)), "a\r\nb" + new string('x', 300), T0.AddSeconds(i));
        Assert.Equal(ProxyConfirm.MaxSeen, pc.List().Count);
        Assert.All(pc.List(), e => Assert.Equal(100, e.Host!.Length));
        Assert.DoesNotContain(pc.List(), e => e.Host!.Contains('\n'));
        Assert.DoesNotContain(pc.List(), e => e.Address == "203.0.113.0");
    }
}
