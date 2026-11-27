using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading.Channels;
using P = DmOffice.GateKeeper.GateKeeperProtocol;

namespace DmOffice.GateKeeper;

/// <summary>Rahmenbedingungen eines Tunnels: Heimnetz erlaubt, DNS-Server des Servers, Prüfung der Namen, Zähler.</summary>
public sealed class GkTunnelOptions
{
    public bool AllowPrivate { get; init; }
    /// <summary>Nur für Tests: Verbindungen zu dieser Maschine (sonst nie).</summary>
    public bool AllowLoopback { get; init; }
    public IPEndPoint? DnsUpstream { get; init; }
    /// <summary>Erreicht der Server Ziele über IPv6? Ohne: AAAA-Anfragen bleiben ohne Adresse, IPv6-Ziele bekommen sofort RESET.</summary>
    public Func<bool> Ipv6 { get; init; } = () => true;
    public required Func<string?, GkVerdict> Check { get; init; }
    public Action? OnConnection { get; init; }
    public Action? OnDns { get; init; }
    public Action<string>? OnBlocked { get; init; }
    /// <summary>Verbindung beendet, die tatsächlich zum Ziel bestand: Bytes von der App, Bytes zur App.</summary>
    public Action<long, long>? OnTraffic { get; init; }
    /// <summary>Zeit bis zur Entscheidung, wenn der Anfang einer Verbindung (TLS, HTTP) ausbleibt.</summary>
    public TimeSpan PeekTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>Wie oft der Tunnel prüft, ob er noch gelten darf (Konto, Schalter).</summary>
    public TimeSpan RecheckEvery { get; init; } = TimeSpan.FromMinutes(5);
    public Func<bool>? StillAllowed { get; init; }
    /// <summary>Nur für Tests: Ports, an denen SNI bzw. Host vor dem Verbinden gelesen werden.</summary>
    internal int TlsPort { get; init; } = 443;
    internal int HttpPort { get; init; } = 80;
}

/// <summary>
/// Ein Tunnel von GateKeeper (Protokoll <see cref="GateKeeperProtocol"/>): nimmt Verbindungswünsche der App an, prüft bei
/// Port 443 den SNI und bei Port 80 den Host vor dem Verbinden, verbindet selbst (gewöhnliche Sockets, keine besonderen
/// Rechte) und reicht die Daten unverändert durch. DNS-Anfragen beantwortet er selbst (gesperrt) oder über den Resolver des
/// Servers. Inhalte werden nicht gespeichert.
/// </summary>
public sealed class GateKeeperTunnel(WebSocket ws, GkTunnelOptions options, ILogger? log = null)
{
    public const int MaxStreams = 512;

    private readonly Channel<byte[]> _out = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<uint, GkStream> _streams = new();
    private readonly CancellationTokenSource _cts = new();

    public async Task RunAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        var token = linked.Token;
        Send(P.HelloFrame());
        var sender = SendLoopAsync(token);
        var recheck = RecheckLoopAsync(token);
        try { await ReceiveLoopAsync(token); }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (IOException) { }
        finally
        {
            _cts.Cancel();
            foreach (var s in _streams.Values) s.Dispose();
            _streams.Clear();
            _out.Writer.TryComplete();
            try { await sender; } catch (Exception) { }
            try { await recheck; } catch (Exception) { }
            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var close = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, close.Token); } catch (Exception) { }
            }
        }
    }

    internal int OpenStreams => _streams.Count;
    private GkTunnelOptions Options => options;

    private void Send(byte[] frame) => _out.Writer.TryWrite(frame);

    private async Task SendLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var f in _out.Reader.ReadAllAsync(ct))
                await ws.SendAsync(f, WebSocketMessageType.Binary, true, ct);
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { _cts.Cancel(); }
        catch (IOException) { _cts.Cancel(); }
    }

    private async Task RecheckLoopAsync(CancellationToken ct)
    {
        if (options.StillAllowed == null) return;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(options.RecheckEvery, ct);
                if (!options.StillAllowed()) { _cts.Cancel(); return; }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buf = new byte[P.MaxMessage];
        while (!ct.IsCancellationRequested)
        {
            int len = 0;
            WebSocketReceiveResult r;
            do
            {
                if (len >= buf.Length) { await ws.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, null, ct); return; }
                r = await ws.ReceiveAsync(new ArraySegment<byte>(buf, len, buf.Length - len), ct);
                if (r.MessageType == WebSocketMessageType.Close) return;
                len += r.Count;
            } while (!r.EndOfMessage);
            if (r.MessageType != WebSocketMessageType.Binary) continue;
            var msg = buf.AsSpan(0, len).ToArray();
            if (P.Parse(msg) is not { } frame) continue;
            Dispatch(frame.Type, frame.Stream, frame.Payload, ct);
        }
    }

    private void Dispatch(byte type, uint id, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        switch (type)
        {
            case P.Open:
                Open(id, payload.Span, ct);
                break;
            case P.Data:
                if (payload.Length > P.MaxData) { Abort(id, P.Reason.Protocol); break; }
                if (_streams.TryGetValue(id, out var d)) d.FromApp(payload.ToArray());
                break;
            case P.Fin:
                if (_streams.TryGetValue(id, out var f)) f.FinFromApp();
                break;
            case P.Reset:
                if (_streams.TryRemove(id, out var x)) x.Dispose();
                break;
            case P.Window:
                if (payload.Length == 4 && _streams.TryGetValue(id, out var w)) w.Grant((int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(payload.Span), P.InitialWindow * 4L));
                break;
            case P.Dns:
                var query = payload.ToArray();
                _ = Task.Run(async () =>
                {
                    var answer = await AnswerDnsAsync(query, ct);
                    if (answer != null) Send(P.Frame(P.Dns, id, answer));
                }, ct);
                break;
        }
    }

    private void Abort(uint id, P.Reason reason)
    {
        if (_streams.TryRemove(id, out var s)) s.Dispose();
        Send(P.ResetFrame(id, reason));
    }

    /// <summary>Darf eine Verbindung zu dieser Adresse aufgebaut werden? Nie zu dieser Maschine, ins eigene Netz nur mit Erlaubnis.</summary>
    internal bool AddressAllowed(IPAddress a)
    {
        if (options.AllowLoopback && IPAddress.IsLoopback(a)) return true;
        if (DmOffice.Storage.ExternalFolders.NeverAllowed(a)) return false;
        return options.AllowPrivate || !DmOffice.Storage.ExternalFolders.Private(a);
    }

    private void Open(uint id, ReadOnlySpan<byte> payload, CancellationToken ct)
    {
        var to = P.ParseOpen(payload);
        if (to == null || _streams.ContainsKey(id)) { Send(P.ResetFrame(id, P.Reason.Protocol)); return; }
        if (_streams.Count >= MaxStreams) { Send(P.ResetFrame(id, P.Reason.Abort)); return; }
        options.OnConnection?.Invoke();
        // TCP-DNS an die Adresse im VPN: selbst beantworten
        if (to.Port == 53 && (to.Address.Equals(P.VirtualDns4) || to.Address.Equals(P.VirtualDns6)))
        {
            var dns = new GkStream(this, id, to, GkStream.Mode.Dns, ct);
            _streams[id] = dns;
            return;
        }
        if (!AddressAllowed(to.Address)) { Send(P.ResetFrame(id, P.Reason.NotAllowed)); return; }
        // Ohne IPv6 beim Server: gar nicht erst versuchen, die App nimmt dann IPv4
        if (to.AddressFamily == AddressFamily.InterNetworkV6 && !to.Address.IsIPv4MappedToIPv6 && !options.Ipv6())
        { Send(P.ResetFrame(id, P.Reason.ConnectFailed)); return; }
        var mode = to.Port == options.TlsPort ? GkStream.Mode.Tls : to.Port == options.HttpPort ? GkStream.Mode.Http : GkStream.Mode.Plain;
        var s = new GkStream(this, id, to, mode, ct);
        _streams[id] = s;
        if (mode == GkStream.Mode.Plain) s.StartConnect();
        else s.StartPeekTimer();
    }

    // ------------------------------------------------------------------ DNS

    internal async Task<byte[]?> AnswerDnsAsync(byte[] query, CancellationToken ct)
    {
        if (GateKeeperFilter.ReadDnsQuestion(query) is not { } q) return null;
        options.OnDns?.Invoke();
        var verdict = options.Check(q.Name);
        if (verdict.Blocked)
        {
            options.OnBlocked?.Invoke(GateKeeperFilter.Normalize(q.Name) ?? q.Name);
            return GateKeeperFilter.BlockedDnsAnswer(query);
        }
        // Server ohne IPv6: keine IPv6-Adressen an die App, sonst versucht sie Ziele, die der Server nicht erreicht
        if (q.Type == GateKeeperFilter.TypeAaaa && !options.Ipv6()) return GateKeeperFilter.EmptyDnsAnswer(query);
        if (options.DnsUpstream is not { } upstream) return GateKeeperFilter.FailedDnsAnswer(query);
        try
        {
            var answer = await GateKeeperDns.QueryUdpAsync(upstream, query, ct);
            // Abgeschnitten (TC): über TCP noch einmal
            if (answer != null && answer.Length > 2 && (answer[2] & 0x02) != 0)
                answer = await GateKeeperDns.QueryTcpAsync(upstream, query, ct) ?? answer;
            return answer ?? GateKeeperFilter.FailedDnsAnswer(query);
        }
        catch (Exception e) when (e is SocketException or IOException or TimeoutException)
        {
            return GateKeeperFilter.FailedDnsAnswer(query);
        }
    }

    // ------------------------------------------------------------------ ein Strom

    private sealed class GkStream(GateKeeperTunnel tunnel, uint id, IPEndPoint to, GkStream.Mode mode, CancellationToken ct) : IDisposable
    {
        public enum Mode { Plain, Tls, Http, Dns }

        private readonly object _lock = new();
        private readonly CancellationTokenSource _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        private readonly Channel<byte[]> _toRemote = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        private readonly SemaphoreSlim _credit = new(0);
        private int _window = P.InitialWindow;
        private List<byte>? _peek = mode is Mode.Tls or Mode.Http ? new() : null;
        private bool _decided = mode is Mode.Plain;
        private Socket? _socket;
        private int _disposed;
        // TCP-DNS: gesammelte Bytes bis zu einer vollständigen Nachricht
        private readonly List<byte> _dnsIn = new();
        // Durchgereichte Bytes (für die Statistik)
        private long _up, _down;

        public void Grant(int bytes)
        {
            Interlocked.Add(ref _window, bytes);
            _credit.Release();
        }

        public void StartPeekTimer() => _ = Task.Run(async () =>
        {
            try { await Task.Delay(tunnel.Options.PeekTimeout, _cts.Token); }
            catch (OperationCanceledException) { return; }
            Decide(null, timeout: true);
        });

        public void FromApp(byte[] data)
        {
            if (mode == Mode.Dns) { DnsData(data); return; }
            Interlocked.Add(ref _up, data.Length);
            lock (_lock)
            {
                if (!_decided && _peek != null)
                {
                    _peek.AddRange(data);
                    var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_peek);
                    string? name;
                    var state = mode == Mode.Tls ? GateKeeperFilter.ReadSni(span, out name) : GateKeeperFilter.ReadHttpHost(span, out name);
                    if (state == GateKeeperFilter.ParseState.NeedMore && _peek.Count < GateKeeperFilter.MaxPeek) return;
                    DecideLocked(state == GateKeeperFilter.ParseState.Done ? name : null);
                    return;
                }
            }
            _toRemote.Writer.TryWrite(data);
        }

        public void FinFromApp()
        {
            if (mode == Mode.Dns) { tunnel.Send(P.Frame(P.Fin, id, [])); Close(); return; }
            lock (_lock)
            {
                if (!_decided) { DecideLocked(null); }
            }
            _toRemote.Writer.TryWrite([]);
        }

        private void Decide(string? name, bool timeout)
        {
            lock (_lock) { if (!_decided) DecideLocked(name); }
        }

        private void DecideLocked(string? name)
        {
            _decided = true;
            if (name != null && tunnel.Options.Check(name) is { Blocked: true })
            {
                tunnel.Options.OnBlocked?.Invoke(GateKeeperFilter.Normalize(name) ?? name);
                _peek = null;
                if (tunnel._streams.TryRemove(id, out _)) Dispose();
                tunnel.Send(P.ResetFrame(id, P.Reason.Blocked));
                return;
            }
            var first = _peek?.ToArray();
            _peek = null;
            if (first is { Length: > 0 }) _toRemote.Writer.TryWrite(first);
            StartConnect();
        }

        public void StartConnect() => _ = Task.Run(ConnectAsync);

        private async Task ConnectAsync()
        {
            var socket = new Socket(to.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                timeout.CancelAfter(tunnel.Options.ConnectTimeout);
                await socket.ConnectAsync(to, timeout.Token);
            }
            catch (Exception)
            {
                socket.Dispose();
                if (!_cts.IsCancellationRequested) tunnel.Abort(id, P.Reason.ConnectFailed);
                return;
            }
            lock (_lock)
            {
                if (_disposed != 0) { socket.Dispose(); return; }
                _socket = socket;
            }
            var writer = WriteLoopAsync(socket);
            var reader = ReadLoopAsync(socket);
            await Task.WhenAll(writer, reader);
            Close();
        }

        private async Task WriteLoopAsync(Socket socket)
        {
            try
            {
                await foreach (var chunk in _toRemote.Reader.ReadAllAsync(_cts.Token))
                {
                    if (chunk.Length == 0)
                    {
                        try { socket.Shutdown(SocketShutdown.Send); } catch (SocketException) { }
                        return;
                    }
                    var sent = 0;
                    while (sent < chunk.Length) sent += await socket.SendAsync(chunk.AsMemory(sent), SocketFlags.None, _cts.Token);
                    tunnel.Send(P.WindowFrame(id, chunk.Length));
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) when (e is SocketException or ObjectDisposedException) { Fail(); }
        }

        private async Task ReadLoopAsync(Socket socket)
        {
            var buf = new byte[P.MaxData];
            try
            {
                while (true)
                {
                    while (Volatile.Read(ref _window) <= 0) await _credit.WaitAsync(_cts.Token);
                    var max = Math.Min(buf.Length, Volatile.Read(ref _window));
                    var n = await socket.ReceiveAsync(buf.AsMemory(0, max), SocketFlags.None, _cts.Token);
                    if (n == 0)
                    {
                        tunnel.Send(P.Frame(P.Fin, id, []));
                        // Warten, bis die App ihre Richtung schließt (oder der Strom abbricht)
                        return;
                    }
                    Interlocked.Add(ref _window, -n);
                    Interlocked.Add(ref _down, n);
                    tunnel.Send(P.Frame(P.Data, id, buf.AsSpan(0, n)));
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) when (e is SocketException or ObjectDisposedException) { Fail(); }
        }

        private void Fail()
        {
            if (_cts.IsCancellationRequested) return;
            tunnel.Abort(id, P.Reason.Abort);
        }

        /// <summary>Beide Richtungen zu Ende: Strom ohne Abbruch entfernen.</summary>
        private void Close()
        {
            if (tunnel._streams.TryRemove(id, out _)) Dispose();
        }

        // TCP-DNS: Nachrichten mit 2 Byte Länge davor, jede wie eine UDP-Anfrage beantworten
        private void DnsData(byte[] data)
        {
            List<byte[]> queries = [];
            lock (_lock)
            {
                _dnsIn.AddRange(data);
                if (_dnsIn.Count > 64 * 1024) { tunnel.Abort(id, P.Reason.Protocol); return; }
                while (_dnsIn.Count >= 2)
                {
                    int len = _dnsIn[0] << 8 | _dnsIn[1];
                    if (_dnsIn.Count < 2 + len) break;
                    queries.Add(_dnsIn.GetRange(2, len).ToArray());
                    _dnsIn.RemoveRange(0, 2 + len);
                }
            }
            tunnel.Send(P.WindowFrame(id, data.Length));
            foreach (var q in queries)
                _ = Task.Run(async () =>
                {
                    var answer = await tunnel.AnswerDnsAsync(q, _cts.Token);
                    if (answer == null || answer.Length > 65535) return;
                    var framed = new byte[2 + answer.Length];
                    BinaryPrimitives.WriteUInt16BigEndian(framed, (ushort)answer.Length);
                    answer.CopyTo(framed, 2);
                    // Antworten sind klein; das Fenster der App reicht dafür
                    for (int off = 0; off < framed.Length; off += P.MaxData)
                        tunnel.Send(P.Frame(P.Data, id, framed.AsSpan(off, Math.Min(P.MaxData, framed.Length - off))));
                });
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _cts.Cancel();
            _toRemote.Writer.TryComplete();
            bool connected;
            lock (_lock)
            {
                connected = _socket != null;
                try { _socket?.Dispose(); } catch (Exception) { }
            }
            if (connected && mode != Mode.Dns) tunnel.Options.OnTraffic?.Invoke(Interlocked.Read(ref _up), Interlocked.Read(ref _down));
        }
    }
}

/// <summary>
/// Erreicht dieser Server Ziele über IPv6? Prüft beim ersten Aufruf und danach höchstens alle 10 Minuten: UDP-„Verbinden“
/// zu einer öffentlichen IPv6-Adresse (schickt nichts, fragt nur die Route ab) mit einer globalen Absenderadresse.
/// Docker-Netze ohne IPv6 haben keine solche Route. DMOFFICE_GATEKEEPER_IPV6=0/1 legt es fest.
/// </summary>
public static class GateKeeperIpv6
{
    private static readonly IPAddress Probe = IPAddress.Parse("2001:4860:4860::8888");
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(10);
    private static bool _value;
    private static long _checkedAt = long.MinValue;

    public static bool Available
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("DMOFFICE_GATEKEEPER_IPV6");
            if (env is "0" or "false") return false;
            if (env is "1" or "true") return true;
            var now = Environment.TickCount64;
            if (_checkedAt == long.MinValue || now - _checkedAt > Every.TotalMilliseconds)
            {
                _value = Check();
                _checkedAt = now;
            }
            return _value;
        }
    }

    internal static bool Check()
    {
        if (!Socket.OSSupportsIPv6) return false;
        try
        {
            using var s = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
            s.Connect(new IPEndPoint(Probe, 53));
            return s.LocalEndPoint is IPEndPoint { Address: var a } && GlobalUnicast(a);
        }
        catch (SocketException) { return false; }
    }

    /// <summary>Globale Unicast-Adresse (2000::/3), nicht link-lokal, nicht ULA, nicht Loopback.</summary>
    internal static bool GlobalUnicast(IPAddress a) =>
        a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv4MappedToIPv6 && (a.GetAddressBytes()[0] & 0xE0) == 0x20;
}

/// <summary>DNS-Anfragen an den Resolver des Servers weiterreichen (UDP, bei abgeschnittenen Antworten TCP).</summary>
public static class GateKeeperDns
{
    private static readonly Lazy<IPEndPoint?> Upstream = new(FindUpstream);

    /// <summary>Resolver: DMOFFICE_GATEKEEPER_DNS (Adresse) oder der erste „nameserver“ aus /etc/resolv.conf.</summary>
    public static IPEndPoint? Default => Upstream.Value;

    private static IPEndPoint? FindUpstream()
    {
        if (IPAddress.TryParse(Environment.GetEnvironmentVariable("DMOFFICE_GATEKEEPER_DNS"), out var env)) return new IPEndPoint(env, 53);
        try
        {
            foreach (var line in File.ReadLines("/etc/resolv.conf"))
            {
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[0] == "nameserver" && IPAddress.TryParse(parts[1].Split('%')[0], out var a)) return new IPEndPoint(a, 53);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    public static async Task<byte[]?> QueryUdpAsync(IPEndPoint server, byte[] query, CancellationToken ct)
    {
        using var s = new Socket(server.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        await s.ConnectAsync(server, ct);
        var buf = new byte[4096];
        for (int attempt = 0; attempt < 2; attempt++)
        {
            await s.SendAsync(query, SocketFlags.None, ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(attempt == 0 ? 2 : 3));
            try
            {
                while (true)
                {
                    var n = await s.ReceiveAsync(buf, SocketFlags.None, timeout.Token);
                    // Nur die Antwort auf genau diese Anfrage (Kennung)
                    if (n >= 12 && buf[0] == query[0] && buf[1] == query[1]) return buf.AsSpan(0, n).ToArray();
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        }
        return null;
    }

    public static async Task<byte[]?> QueryTcpAsync(IPEndPoint server, byte[] query, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var s = new Socket(server.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            await s.ConnectAsync(server, timeout.Token);
            var framed = new byte[2 + query.Length];
            BinaryPrimitives.WriteUInt16BigEndian(framed, (ushort)query.Length);
            query.CopyTo(framed, 2);
            await s.SendAsync(framed, SocketFlags.None, timeout.Token);
            var head = new byte[2];
            if (!await ReadExactly(s, head, timeout.Token)) return null;
            var answer = new byte[BinaryPrimitives.ReadUInt16BigEndian(head)];
            return await ReadExactly(s, answer, timeout.Token) ? answer : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }

    private static async Task<bool> ReadExactly(Socket s, byte[] buf, CancellationToken ct)
    {
        int got = 0;
        while (got < buf.Length)
        {
            var n = await s.ReceiveAsync(buf.AsMemory(got), SocketFlags.None, ct);
            if (n == 0) return false;
            got += n;
        }
        return true;
    }
}
