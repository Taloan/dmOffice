using System.Net;
using DmOffice.Core;
using Xunit;

namespace DmOffice.Tests;

public class ProxyTrustTests
{
    private static readonly (IPAddress, int)[] Container = { (IPAddress.Parse("172.18.0.5"), 16), (IPAddress.Parse("127.0.0.1"), 8), (IPAddress.Parse("fe80::1"), 64), (IPAddress.Parse("203.0.113.9"), 24) };

    [Fact]
    public void Eingetragene_Netze_gelten_allein()
    {
        var nets = ProxyTrust.Networks("10.208.85.1/32, 127.0.0.1/32", strict: true, Container);
        Assert.True(nets.Any(n => n.Contains(IPAddress.Parse("10.208.85.1"))));
        Assert.False(nets.Any(n => n.Contains(IPAddress.Parse("172.18.0.1"))));
    }

    [Fact]
    public void Ohne_Kennung_alle_privaten_Netze()
    {
        var nets = ProxyTrust.Networks(null, strict: false, Container);
        Assert.True(nets.Any(n => n.Contains(IPAddress.Parse("192.168.178.20"))));
    }

    [Fact]
    public void Mit_Kennung_nur_eigene_Maschine_und_eigenes_Netz()
    {
        var nets = ProxyTrust.Networks("", strict: true, Container);
        Assert.True(nets.Any(n => n.Contains(IPAddress.Parse("127.0.0.1"))));
        Assert.True(nets.Any(n => n.Contains(IPAddress.Parse("172.18.0.1"))));   // Gateway des Docker-Netzes
        Assert.False(nets.Any(n => n.Contains(IPAddress.Parse("192.168.178.20")))); // Gerät im Heimnetz
        Assert.False(nets.Any(n => n.Contains(IPAddress.Parse("203.0.113.10"))));   // öffentliche Adresse
        Assert.False(nets.Any(n => n.Contains(IPAddress.Parse("fe80::2"))));
    }
}
