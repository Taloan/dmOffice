using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DmOffice.Core;

/// <summary>Welche Absender X-Forwarded-* setzen dürfen.</summary>
public static class ProxyTrust
{
    public static readonly string[] PrivateNets = { "127.0.0.0/8", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "::1/128", "fc00::/7", "fe80::/10" };

    /// <summary>Eingetragene Netze aus DMOFFICE_TRUSTED_PROXIES. Ohne Eintrag: bisher alle privaten Netze;
    /// mit Kennung proxytrust nur die eigene Maschine und die Netze der eigenen Schnittstellen (Docker-Netz,
    /// Gateway mit Portweiterleitung) - ein Gerät im Heimnetz kann dann keine Adresse mehr vorgeben.</summary>
    public static List<IPNetwork> Networks(string? configured, bool strict, IEnumerable<(IPAddress Address, int Prefix)>? own = null)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(IPNetwork.Parse).ToList();
        if (!strict) return PrivateNets.Select(IPNetwork.Parse).ToList();
        var list = new List<IPNetwork> { IPNetwork.Parse("127.0.0.0/8"), IPNetwork.Parse("::1/128") };
        foreach (var (address, prefix) in own ?? OwnSubnets())
        {
            if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal) continue;
            // Nur private Netze der eigenen Schnittstellen; eine öffentliche Adresse macht nicht das ganze Netz zum Proxy
            if (!PrivateNets.Select(IPNetwork.Parse).Any(n => n.Contains(address))) continue;
            var net = new IPNetwork(Mask(address, prefix), prefix);
            if (!list.Contains(net)) list.Add(net);
        }
        return list;
    }

    private static IEnumerable<(IPAddress, int)> OwnSubnets()
    {
        var result = new List<(IPAddress, int)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var a in nic.GetIPProperties().UnicastAddresses)
                    if (a.Address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6 && a.PrefixLength > 0)
                        result.Add((a.Address, a.PrefixLength));
            }
        }
        catch (NetworkInformationException) { }
        return result;
    }

    private static IPAddress Mask(IPAddress address, int prefix)
    {
        var bytes = address.GetAddressBytes();
        for (var i = 0; i < bytes.Length; i++)
        {
            var bits = Math.Clamp(prefix - i * 8, 0, 8);
            bytes[i] &= (byte)(0xFF << (8 - bits));
        }
        return new IPAddress(bytes);
    }
}
