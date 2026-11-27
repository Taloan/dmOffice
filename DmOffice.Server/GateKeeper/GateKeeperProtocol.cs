using System.Buffers.Binary;
using System.Net;

namespace DmOffice.GateKeeper;

/// <summary>
/// Protokoll des GateKeeper-Tunnels (Version 1), ein WebSocket unter /api/gatekeeper/tunnel?v=1, binäre Nachrichten.
/// Jede Nachricht ist ein Rahmen: 1 Byte Typ, 4 Byte Strom-Nummer (Big Endian), danach die Nutzdaten des Typs.
/// Gegenstück in der App: apps/mobile/app/src/gatekeeper/kotlin/at/dimes/dmoffice/gatekeeper/TunnelProtocol.kt.
///
///   HELLO   S→A  Strom 0: Version (1 Byte), Startfenster je Strom (4 Byte)
///   OPEN    A→S  neue TCP-Verbindung: Familie (4|6), Adresse (4|16 Byte), Port (2 Byte)
///   DATA    beide Nutzdaten (höchstens <see cref="MaxData"/>), zählen gegen das Fenster der Gegenseite
///   FIN     beide Senderichtung geschlossen (halb geschlossen)
///   RESET   beide Strom abgebrochen: Grund (1 Byte, <see cref="Reason"/>)
///   WINDOW  beide Fenster erweitern: Bytes (4 Byte) - nachdem die Daten weitergegeben wurden
///   DNS     beide Strom-Nummer = Kennung der Anfrage; Nutzdaten: DNS-Nachricht (wie über UDP)
///
/// Flusskontrolle: jede Seite darf je Strom höchstens so viele Bytes DATA unbestätigt senden, wie das Fenster erlaubt
/// (Start <see cref="InitialWindow"/>); WINDOW gibt weitergereichte Bytes frei.
/// </summary>
public static class GateKeeperProtocol
{
    public const int Version = 1;
    public const byte Hello = 0, Open = 1, Data = 2, Fin = 3, Reset = 4, Window = 5, Dns = 6;
    public const int InitialWindow = 256 * 1024;
    public const int MaxData = 16 * 1024;
    public const int HeaderSize = 5;
    /// <summary>Größte Nachricht, die eine Seite annimmt.</summary>
    public const int MaxMessage = HeaderSize + MaxData + 1024;

    /// <summary>Gründe für RESET.</summary>
    public enum Reason : byte { Abort = 0, Blocked = 1, ConnectFailed = 2, NotAllowed = 3, Protocol = 4 }

    /// <summary>DNS-Server im VPN der App (eigene Adressen, nie im Internet): TCP zu Port 53 dort beantwortet der Server selbst.</summary>
    public static readonly IPAddress VirtualDns4 = IPAddress.Parse("10.111.0.53");
    public static readonly IPAddress VirtualDns6 = IPAddress.Parse("fd00:6d6f:6666::53");

    public static byte[] Frame(byte type, uint stream, ReadOnlySpan<byte> payload)
    {
        var f = new byte[HeaderSize + payload.Length];
        f[0] = type;
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(1), stream);
        payload.CopyTo(f.AsSpan(HeaderSize));
        return f;
    }

    public static byte[] HelloFrame()
    {
        Span<byte> p = stackalloc byte[5];
        p[0] = Version;
        BinaryPrimitives.WriteUInt32BigEndian(p[1..], InitialWindow);
        return Frame(Hello, 0, p);
    }

    public static byte[] WindowFrame(uint stream, int bytes)
    {
        Span<byte> p = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(p, (uint)bytes);
        return Frame(Window, stream, p);
    }

    public static byte[] ResetFrame(uint stream, Reason reason) => Frame(Reset, stream, [(byte)reason]);

    public static byte[] OpenFrame(uint stream, IPEndPoint to)
    {
        var a = to.Address.GetAddressBytes();
        var p = new byte[1 + a.Length + 2];
        p[0] = (byte)(a.Length == 4 ? 4 : 6);
        a.CopyTo(p, 1);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(1 + a.Length), (ushort)to.Port);
        return Frame(Open, stream, p);
    }

    /// <summary>Rahmen zerlegen; null, wenn er zu kurz ist.</summary>
    public static (byte Type, uint Stream, ReadOnlyMemory<byte> Payload)? Parse(ReadOnlyMemory<byte> message)
    {
        if (message.Length < HeaderSize) return null;
        var s = message.Span;
        return (s[0], BinaryPrimitives.ReadUInt32BigEndian(s[1..]), message[HeaderSize..]);
    }

    /// <summary>Ziel aus OPEN lesen; null bei falscher Länge oder Port 0.</summary>
    public static IPEndPoint? ParseOpen(ReadOnlySpan<byte> p)
    {
        if (p.Length == 7 && p[0] == 4 || p.Length == 19 && p[0] == 6)
        {
            var addr = new IPAddress(p.Slice(1, p.Length - 3));
            int port = BinaryPrimitives.ReadUInt16BigEndian(p[^2..]);
            return port == 0 ? null : new IPEndPoint(addr, port);
        }
        return null;
    }
}
