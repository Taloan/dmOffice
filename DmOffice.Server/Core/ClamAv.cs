using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace DmOffice.Core;

/// <summary>
/// Gespräch mit clamd (ClamAV) über TCP: INSTREAM (Datei in Stücken, „stream: OK“ oder „stream: &lt;Name&gt; FOUND“), PING und
/// VERSION (Programm- und Signaturstand). Protokoll: https://docs.clamav.net/manual/Usage/Scanning.html#clamd - Befehle mit
/// „z“ davor und Null am Ende, Stücke mit vier Byte Länge (Network Byte Order), Ende mit Länge 0.
/// </summary>
public static class ClamAv
{
    public enum Verdict { Clean, Infected, Error }
    public sealed record Result(Verdict Verdict, string? Name = null, string? Error = null)
    {
        public static readonly Result Clean = new(Verdict.Clean);
    }

    private const int Chunk = 64 * 1024;

    public static async Task<Result> ScanAsync(string host, int port, Stream data, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port, cts.Token);
            await using var net = tcp.GetStream();
            await net.WriteAsync("zINSTREAM\0"u8.ToArray(), cts.Token);
            var buf = new byte[4 + Chunk];
            int n;
            while ((n = await data.ReadAsync(buf.AsMemory(4, Chunk), cts.Token)) > 0)
            {
                BinaryPrimitives.WriteUInt32BigEndian(buf, (uint)n);
                await net.WriteAsync(buf.AsMemory(0, 4 + n), cts.Token);
            }
            await net.WriteAsync(new byte[4], cts.Token);
            var reply = await ReadReply(net, cts.Token);
            return Parse(reply);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new Result(Verdict.Error, Error: "Der Virenscanner antwortet nicht rechtzeitig."); }
        catch (Exception e) when (e is SocketException or IOException) { return new Result(Verdict.Error, Error: "Der Virenscanner ist nicht erreichbar (" + e.Message + ")."); }
    }

    /// <summary>Antwort auf INSTREAM: „stream: OK“, „stream: Win.Test.EICAR_HDB-1 FOUND“ oder „… ERROR“ (etwa Größengrenze).</summary>
    public static Result Parse(string reply)
    {
        var r = reply.Trim('\0', ' ', '\n', '\r');
        if (r.EndsWith(" OK", StringComparison.Ordinal) || r == "OK") return Result.Clean;
        if (r.EndsWith(" FOUND", StringComparison.Ordinal))
        {
            var name = r[..^" FOUND".Length];
            var colon = name.IndexOf(": ", StringComparison.Ordinal);
            return new Result(Verdict.Infected, Name: colon >= 0 ? name[(colon + 2)..] : name);
        }
        if (r.Contains("size limit", StringComparison.OrdinalIgnoreCase)) return new Result(Verdict.Error, Error: "Die Datei ist größer als die Grenze des Virenscanners (StreamMaxLength).");
        return new Result(Verdict.Error, Error: "Antwort des Virenscanners: " + r);
    }

    /// <summary>Erreichbar? Dann Programm- und Signaturstand („ClamAV 1.4.1/27450/Mon Oct 26 08:12:00 2026“).</summary>
    public static async Task<(bool Ok, string? Version, string? Error)> PingAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            if (await Command(host, port, "zPING\0", cts.Token) != "PONG") return (false, null, "clamd antwortet nicht mit PONG.");
            return (true, await Command(host, port, "zVERSION\0", cts.Token), null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return (false, null, "Der Virenscanner antwortet nicht rechtzeitig."); }
        catch (Exception e) when (e is SocketException or IOException) { return (false, null, "Der Virenscanner ist nicht erreichbar (" + e.Message + ")."); }
    }

    private static async Task<string> Command(string host, int port, string command, CancellationToken ct)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port, ct);
        await using var net = tcp.GetStream();
        await net.WriteAsync(Encoding.ASCII.GetBytes(command), ct);
        return (await ReadReply(net, ct)).Trim('\0', ' ', '\n', '\r');
    }

    private static async Task<string> ReadReply(NetworkStream net, CancellationToken ct)
    {
        var ms = new MemoryStream();
        var b = new byte[512];
        int n;
        while ((n = await net.ReadAsync(b, ct)) > 0)
        {
            ms.Write(b, 0, n);
            if (b[n - 1] == 0 || ms.Length > 4096) break;
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
