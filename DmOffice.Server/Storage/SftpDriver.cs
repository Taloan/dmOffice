using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace DmOffice.Storage;

/// <summary>
/// Ein Speicher über SFTP (SSH): NAS, Linux-Server, Storage Box. Adresse „sftp://server:port/ordner“, optional
/// „?connections=4“; „sftp://server/~/ordner“ ist relativ zum Startordner des Kontos. Angemeldet wird mit Passwort oder
/// SSH-Schlüssel (im Passwortfeld, PEM bzw. OpenSSH). Höchstens <see cref="Connections"/> Verbindungen gleichzeitig je Ziel - viele
/// Anbieter begrenzen die Zahl je Konto. Der Schlüssel des Servers wird beim ersten Verbinden gemerkt
/// (<see cref="KnownHosts"/>); meldet er später einen anderen, bricht der Treiber ab.
// [feature:sftptrust]
/// Mit „sftptrust“ merkt sich der Treiber nichts selbst: Neue und geänderte Schlüssel bestätigt der Benutzer in der Oberfläche
/// (<see cref="ProbeHostAsync"/>, <see cref="ConfirmHost"/>), bis dahin wird nicht verbunden (<see cref="SftpHostKeyException"/>).
// [/feature:sftptrust]
/// </summary>
public sealed class SftpDriver : IStorageDriver
{
    public const int DefaultConnections = 4, MaxConnections = 16;

    private readonly string _host, _connectTo, _user, _root;
    private readonly int _port;
    private readonly AuthenticationMethod _auth;
    private readonly Target _target;
    private bool _disposed;

    /// <summary>Gleichzeitige Verbindungen dieses Ziels.</summary>
    public int Connections { get; }
    public int Parallel => Connections;

    /// <param name="url">sftp://server[:port]/ordner[?connections=n]</param>
    /// <param name="secret">Passwort oder privater SSH-Schlüssel (beginnt mit „-----BEGIN“)</param>
    /// <param name="allowAddress">Prüfung der aufgelösten Adresse (Benutzer-Mounts: kein Heimnetz); null: alles erlaubt</param>
    public SftpDriver(string url, string? user, string? secret, Func<IPAddress, bool>? allowAddress = null)
    {
        if (!TryParse(url, out var host, out var port, out var root, out var connections))
            throw new ArgumentException("Die Adresse muss die Form sftp://server:port/ordner haben.");
        if (string.IsNullOrWhiteSpace(user)) throw new ArgumentException("Für SFTP bitte einen Benutzernamen angeben.");
        _host = host; _port = port; _root = root; _user = user.Trim();
        Connections = connections;
        _auth = IsPrivateKey(secret)
            ? new PrivateKeyAuthenticationMethod(_user, new PrivateKeyFile(new MemoryStream(Encoding.UTF8.GetBytes(secret!.Trim() + "\n"))))
            : new PasswordAuthenticationMethod(_user, secret ?? "");
        _connectTo = Resolve(host, allowAddress);
        _target = Target.Of(_connectTo, _port, _user, secret, connections);
    }

    /// <summary>Adresse, mit der verbunden wird: mit Prüfung vorab aufgelöst und geprüft - verbunden wird mit genau dieser
    /// Adresse (kein zweites Auflösen dazwischen); ohne Prüfung der Name selbst.</summary>
    private static string Resolve(string host, Func<IPAddress, bool>? allowAddress)
    {
        if (allowAddress == null) return host;
        var addresses = IPAddress.TryParse(host, out var ip) ? [ip] : Dns.GetHostAddresses(host);
        var ok = addresses.FirstOrDefault(allowAddress)
            ?? throw new UnauthorizedAccessException("Diese Adresse liegt im Heimnetz oder ist nicht erlaubt.");
        return ok.ToString();
    }

    public static bool IsPrivateKey(string? secret) => secret != null && secret.TrimStart().StartsWith("-----BEGIN", StringComparison.Ordinal);

    /// <summary>Adresse zerlegen: Server, Port (Vorgabe 22), Startordner, gleichzeitige Verbindungen.</summary>
    public static bool TryParse(string? url, out string host, out int port, out string root, out int connections)
    {
        host = ""; port = 22; root = ""; connections = DefaultConnections;
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var u) || u.Scheme != "sftp" || string.IsNullOrEmpty(u.Host)) return false;
        host = u.IdnHost.Trim('[', ']');
        port = u.IsDefaultPort || u.Port <= 0 ? 22 : u.Port;
        var path = Uri.UnescapeDataString(u.AbsolutePath);
        // „/~/ordner“: relativ zum Startordner des Kontos; sonst ab der Wurzel des Servers
        root = path.StartsWith("/~", StringComparison.Ordinal) ? path[2..].Trim('/') : path.TrimEnd('/');
        foreach (var part in u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            if (part.Split('=', 2) is [var k, var v] && k.Equals("connections", StringComparison.OrdinalIgnoreCase) && int.TryParse(v, out var n))
                connections = Math.Clamp(n, 1, MaxConnections);
        return true;
    }

    /// <summary>Adresse mit gewünschter Zahl gleichzeitiger Verbindungen (für die Formulare).</summary>
    public static string WithConnections(string url, int connections)
    {
        var u = new UriBuilder(url.Trim());
        var rest = u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Where(p => !p.StartsWith("connections=", StringComparison.OrdinalIgnoreCase));
        u.Query = string.Join("&", rest.Append("connections=" + Math.Clamp(connections, 1, MaxConnections)));
        return u.Uri.ToString();
    }

    // ---------------- Bekannte Server-Schlüssel ----------------

    /// <summary>Datei mit den gemerkten Schlüsseln (Server:Port → SHA256-Fingerabdruck); beim Start gesetzt.</summary>
    public static string? KnownHosts { get; set; }
    private static readonly object HostLock = new();

    private static Dictionary<string, string> LoadHosts()
    {
        try { return KnownHosts != null && File.Exists(KnownHosts) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(KnownHosts)) ?? new() : new(); }
        catch (Exception e) when (e is IOException or JsonException) { return new(); }
    }

    /// <summary>Gemerkten Schlüssel vergessen (der Server hat wirklich einen neuen bekommen).</summary>
    public static void ForgetHost(string host, int port)
    {
        lock (HostLock)
        {
            var hosts = LoadHosts();
            if (hosts.Remove(host.ToLowerInvariant() + ":" + port) && KnownHosts != null) File.WriteAllText(KnownHosts, JsonSerializer.Serialize(hosts));
        }
    }

    /// <summary>Für den Eintrag, wenn ein Schlüssel beim ersten Verbinden gemerkt wird (zum Abgleich mit dem Server).</summary>
    public static ILogger? Log { get; set; }

    /// <summary>Fingerabdruck prüfen: unbekannt → merken; bekannt → muss gleich sein.</summary>
    internal static bool TrustHost(string host, int port, string fingerprint, out string? known)
    {
        var key = host.ToLowerInvariant() + ":" + port;
        lock (HostLock)
        {
            var hosts = LoadHosts();
            if (hosts.TryGetValue(key, out known)) return known == fingerprint;
            hosts[key] = fingerprint;
            if (KnownHosts != null) { Directory.CreateDirectory(Path.GetDirectoryName(KnownHosts)!); File.WriteAllText(KnownHosts, JsonSerializer.Serialize(hosts)); }
            Log?.LogInformation("SFTP: Schlüssel von {Host} beim ersten Verbinden gemerkt ({Fingerprint}). Zum Abgleich auf dem Server: ssh-keygen -lf mit dem Host-Schlüssel.", key, fingerprint);
            return true;
        }
    }

    // [feature:sftptrust]
    /// <summary>Schlüssel eines Servers, wie er sich gerade meldet. State: „trusted“ (bestätigt), „new“ (noch nie bestätigt),
    /// „changed“ (anders als der bestätigte <paramref name="Known"/>).</summary>
    public sealed record HostKeyInfo(string Host, int Port, string Fingerprint, string KeyType, string? Known, string State);

    /// <summary>Stand eines Schlüssels, ohne etwas zu merken. Schon gemerkte Schlüssel (auch aus der Zeit vor der Bestätigung) gelten als bestätigt.</summary>
    internal static string HostState(string host, int port, string fingerprint, out string? known)
    {
        lock (HostLock)
            return LoadHosts().TryGetValue(host.ToLowerInvariant() + ":" + port, out known) ? (known == fingerprint ? "trusted" : "changed") : "new";
    }

    /// <summary>Vom Benutzer bestätigten Schlüssel merken (ersetzt einen früheren); wartende Verbindungen dürfen sofort neu versuchen.</summary>
    public static void ConfirmHost(string host, int port, string fingerprint)
    {
        var key = host.ToLowerInvariant() + ":" + port;
        lock (HostLock)
        {
            var hosts = LoadHosts();
            hosts.TryGetValue(key, out var before);
            hosts[key] = fingerprint;
            if (KnownHosts != null) { Directory.CreateDirectory(Path.GetDirectoryName(KnownHosts)!); File.WriteAllText(KnownHosts, JsonSerializer.Serialize(hosts)); }
            Log?.LogInformation("SFTP: Fingerabdruck von {Host} bestätigt ({Fingerprint}{Before}).", key, fingerprint, before == null ? "" : ", vorher " + before);
        }
        Target.RetryRefused();
        ExternalFolders.ForgetKeyWait();   // Ordner, die daran scheiterten, gleich wieder als erreichbar prüfen
    }

    /// <summary>
    /// Schlüssel des Servers abfragen, ohne sich anzumelden (nur der Schlüsselaustausch, dann Abbruch). Für die Anzeige zum Bestätigen.
    /// </summary>
    /// <param name="allowAddress">wie beim Treiber: Prüfung der aufgelösten Adresse; null: alles erlaubt</param>
    public static async Task<HostKeyInfo> ProbeHostAsync(string host, int port, Func<IPAddress, bool>? allowAddress, CancellationToken ct)
    {
        var connectTo = Resolve(host, allowAddress);
        string? fp = null, type = null;
        using var client = new SftpClient(new Renci.SshNet.ConnectionInfo(connectTo, port, "dmoffice", new NoneAuthenticationMethod("dmoffice")) { Timeout = TimeSpan.FromSeconds(15) });
        client.HostKeyReceived += (_, e) => { fp = "SHA256:" + e.FingerPrintSHA256; type = e.HostKeyName; e.CanTrust = false; };
        try { await client.ConnectAsync(ct); }
        catch (Exception e) when (e is not OperationCanceledException && fp == null)
        {
            throw e is SshConnectionException or SshOperationTimeoutException && await OtherServer(connectTo, port, ct) is { } other ? new IOException(other) : Translate(e, "Verbinden");
        }
        catch (Exception e) when (e is not OperationCanceledException) { }   // Abbruch nach dem Schlüssel: so gewollt
        if (fp == null) throw new IOException("Verbinden: der SFTP-Server ist nicht erreichbar.");
        var state = HostState(host, port, fp, out var known);
        return new HostKeyInfo(host, port, fp, type ?? "", known, state);
    }
    // [/feature:sftptrust]

    // ---------------- Verbindungen ----------------

    /// <summary>
    /// Verbindungen eines Ziels (Server, Port, Konto), gemeinsam für alle Treiber dorthin: Galerie, Suche, Prüfung der Erreichbarkeit
    /// und Dateibereich legen jeweils eigene Treiber an - die Grenze <see cref="Connections"/> gilt so für das Ziel, nicht je Treiber.
    /// Ruhende Verbindungen schließen nach <see cref="IdleClose"/>. Scheitert das Verbinden, wartet das Ziel vor dem nächsten Versuch
    /// (30 s, dann doppelt so lange, höchstens <see cref="MaxPause"/>) - Dauerversuche gegen einen hängenden oder sperrenden Server
    /// (SFTPGo, fail2ban) verlängern dort sonst die Sperre.
    /// </summary>
    private sealed class Target
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Target> All = new();
        public readonly SemaphoreSlim Slots;
        public readonly Stack<(SftpClient Client, DateTime At)> Idle = new();
        public DateTime PauseUntil;
        public int Failures;
        public string? LastError;
        public bool KeyRefused; // [feature:sftptrust]
        public HostKeyInfo? RefusedKey; // [feature:sftptrust]

        private Target(int connections) => Slots = new SemaphoreSlim(connections, connections);

        public static Target Of(string host, int port, string user, string? secret, int connections)
        {
            var key = host.ToLowerInvariant() + "|" + port + "|" + user + "|" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret ?? "")))[..16];
            return All.GetOrAdd(key, _ => new Target(connections));
        }

        // [feature:sftptrust]
        /// <summary>Nach einer Bestätigung: Ziele, die am Schlüssel scheiterten, nicht erst die Pause abwarten lassen.</summary>
        public static void RetryRefused()
        {
            foreach (var t in All.Values)
                lock (t)
                    if (t.KeyRefused) { t.KeyRefused = false; t.PauseUntil = DateTime.MinValue; }
        }
        // [/feature:sftptrust]

        /// <summary>Ruhende Verbindungen, die zu lange nicht gebraucht wurden, schließen (bei jedem Zugriff auf ein Ziel, für alle).</summary>
        public static void CloseIdle()
        {
            var old = DateTime.UtcNow - IdleClose;
            foreach (var t in All.Values)
            {
                List<SftpClient> gone = [];
                lock (t.Idle)
                {
                    if (t.Idle.Count == 0 || t.Idle.All(i => i.At >= old)) continue;
                    var keep = t.Idle.Where(i => i.At >= old).Reverse().ToList();
                    gone.AddRange(t.Idle.Where(i => i.At < old).Select(i => i.Client));
                    t.Idle.Clear();
                    foreach (var k in keep) t.Idle.Push(k);
                }
                foreach (var c in gone) try { c.Dispose(); } catch (Exception) { }
            }
        }
    }

    internal static readonly TimeSpan IdleClose = TimeSpan.FromMinutes(1), FirstPause = TimeSpan.FromSeconds(30), MaxPause = TimeSpan.FromMinutes(10);

    /// <summary>Wartezeit nach der n-ten gescheiterten Verbindung in Folge.</summary>
    internal static TimeSpan PauseAfter(int failures) =>
        TimeSpan.FromTicks(Math.Min(MaxPause.Ticks, FirstPause.Ticks << Math.Clamp(failures - 1, 0, 20)));

    /// <summary>Nächsten Verbindungsversuch sofort erlauben (Prüfen auf Wunsch).</summary>
    public void RetryNow() { lock (_target) _target.PauseUntil = DateTime.MinValue; }

    private async Task<SftpClient> Acquire(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Target.CloseIdle();
        await _target.Slots.WaitAsync(ct);
        try
        {
            lock (_target.Idle)
                while (_target.Idle.Count > 0)
                {
                    var c = _target.Idle.Pop().Client;
                    if (c.IsConnected) return c;
                    try { c.Dispose(); } catch (Exception) { }
                }
            lock (_target)
                if (DateTime.UtcNow < _target.PauseUntil)
                {
                    // [feature:sftptrust]
                    // In der Pause nach einem abgelehnten Schlüssel: weiter sagen, was zu bestätigen ist (die Oberfläche bietet es an)
                    if (_target.KeyRefused && _target.RefusedKey is { } key) throw new SftpHostKeyException(key);
                    // [/feature:sftptrust]
                    throw new IOException($"{_target.LastError ?? "Verbinden: nicht erreichbar"} (nächster Versuch in {Math.Max(1, (int)(_target.PauseUntil - DateTime.UtcNow).TotalSeconds)} s)");
                }
            var client = new SftpClient(new Renci.SshNet.ConnectionInfo(_connectTo, _port, _user, _auth) { Timeout = TimeSpan.FromSeconds(20) }) { OperationTimeout = TimeSpan.FromMinutes(5) };
            string? mismatch = null;
            HostKeyInfo? refused = null; // [feature:sftptrust]
            client.HostKeyReceived += (_, e) =>
            {
                var fp = "SHA256:" + e.FingerPrintSHA256;
                // [feature:sftptrust]
                // Nur bestätigte Schlüssel; neue und geänderte bestätigt der Benutzer in der Oberfläche
                if (DmOffice.Core.Features.On(DmOffice.Core.Features.SftpTrust))
                {
                    var state = HostState(_host, _port, fp, out var seen);
                    e.CanTrust = state == "trusted";
                    if (!e.CanTrust) refused = new HostKeyInfo(_host, _port, fp, e.HostKeyName, seen, state);
                    return;
                }
                // [/feature:sftptrust]
                e.CanTrust = TrustHost(_host, _port, fp, out var known);
                if (!e.CanTrust) mismatch = $"Der Server meldet einen anderen Schlüssel ({fp}) als beim ersten Verbinden ({known}). Wenn er wirklich neu eingerichtet wurde, den gemerkten Schlüssel in der Verwaltung zurücksetzen.";
            };
            try { await client.ConnectAsync(ct); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                client.Dispose();
                Exception error = mismatch != null ? new UnauthorizedAccessException(mismatch)
                    // Antwortet dort etwas anderes als ein SSH-Server (etwa ein FTP-Server), das sagen statt „nicht erreichbar“
                    : e is SshConnectionException or SshOperationTimeoutException && await OtherServer(_connectTo, _port, ct) is { } other ? new IOException(other)
                    : Translate(e, "Verbinden");
                // [feature:sftptrust]
                if (refused != null)
                {
                    error = new SftpHostKeyException(refused);
                    Log?.LogWarning("SFTP: {Host}:{Port} nicht verbunden, Fingerabdruck {Fingerprint} ist nicht bestätigt ({State}, bestätigt: {Known}).",
                        refused.Host, refused.Port, refused.Fingerprint, refused.State == "new" ? "neu" : "geändert", refused.Known ?? "keiner");
                }
                // [/feature:sftptrust]
                lock (_target)
                {
                    _target.KeyRefused = refused != null; // [feature:sftptrust]
                    _target.RefusedKey = refused; // [feature:sftptrust]
                    _target.Failures++;
                    _target.LastError = error.Message.TrimEnd('.', ' ');
                    _target.PauseUntil = DateTime.UtcNow + PauseAfter(_target.Failures);
                }
                throw error;
            }
            lock (_target) { _target.Failures = 0; _target.PauseUntil = DateTime.MinValue; _target.LastError = null; }
            return client;
        }
        catch { _target.Slots.Release(); throw; }
    }

    private void Release(SftpClient c)
    {
        lock (_target.Idle) _target.Idle.Push((c, DateTime.UtcNow));
        _target.Slots.Release();
    }

    private async Task<T> Use<T>(string what, Func<SftpClient, Task<T>> work, CancellationToken ct)
    {
        var c = await Acquire(ct);
        try { return await work(c); }
        catch (Exception e) when (e is not OperationCanceledException && e is not KeyNotFoundException && e is not UnauthorizedAccessException && e is not ArgumentException) { throw Translate(e, what); }
        finally { Release(c); }
    }

    /// <summary>Höchstens so viele Nachfragen „was antwortet dort?“ je <see cref="ProbeWindow"/> über alle Ziele; danach nur die allgemeine Meldung.</summary>
    public const int MaxProbes = 10;
    public static readonly TimeSpan ProbeWindow = TimeSpan.FromMinutes(10);
    private static readonly List<DateTime> Probes = [];

    /// <summary>Darf noch nachgefragt werden? Zählt die Nachfrage mit.</summary>
    internal static bool MayProbe(DateTime? now = null)
    {
        var t = now ?? DateTime.UtcNow;
        lock (Probes)
        {
            Probes.RemoveAll(v => (t - v).Duration() > ProbeWindow);
            if (Probes.Count >= MaxProbes) return false;
            Probes.Add(t);
            return true;
        }
    }

    /// <summary>Erste Zeile des Servers lesen: ein FTP-Server meldet sich mit „220“, ein SSH-Server mit „SSH-“. Meldung, wenn es kein SSH-Server ist; sonst null.
    /// Gedrosselt (<see cref="MayProbe"/>): als Werkzeug zum Abtasten fremder Server taugt es so nicht.</summary>
    internal static async Task<string?> OtherServer(string host, int port, CancellationToken ct)
    {
        if (!MayProbe()) return null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port, cts.Token);
            var buf = new byte[64];
            var n = await tcp.GetStream().ReadAsync(buf, cts.Token);
            return OtherServerText(System.Text.Encoding.ASCII.GetString(buf, 0, n));
        }
        catch (Exception e) when (e is SocketException or IOException or OperationCanceledException && !ct.IsCancellationRequested) { return null; }
    }

    internal static string? OtherServerText(string banner) =>
        banner.Length == 0 || banner.StartsWith("SSH-", StringComparison.Ordinal) ? null
        : banner.StartsWith("220", StringComparison.Ordinal) ? "Verbinden: Unter dieser Adresse antwortet ein FTP-Server. dmOffice unterstützt nur SFTP (über SSH)."
        : "Verbinden: Unter dieser Adresse antwortet kein SFTP-Server.";

    private static Exception Translate(Exception e, string what) => e switch
    {
        SftpPathNotFoundException => new KeyNotFoundException(what + ": nicht gefunden."),
        SftpPermissionDeniedException => new UnauthorizedAccessException(what + ": keine Berechtigung."),
        SshAuthenticationException => new UnauthorizedAccessException(what + ": Anmeldung abgelehnt (Benutzername, Passwort oder Schlüssel)."),
        SocketException or SshConnectionException or SshOperationTimeoutException or ProxyException => new IOException(what + ": der SFTP-Server ist nicht erreichbar."),
        IOException io => io,
        _ => new IOException(what + ": " + e.Message),
    };

    private string Full(string path)
    {
        var rel = StoragePath.Clean(path);
        if (_root.Length == 0) return rel.Length == 0 ? "." : rel;
        return rel.Length == 0 ? _root : _root + "/" + rel;
    }

    private static DriverEntry Entry(string name, Renci.SshNet.Sftp.SftpFileAttributes a) =>
        new(name, a.IsDirectory, a.IsDirectory ? 0 : a.Size, a.LastWriteTimeUtc, null);

    // ---------------- IStorageDriver ----------------

    public Task<List<DriverEntry>> ListAsync(string path, CancellationToken ct) => Use("Auflisten", async c =>
    {
        var list = new List<DriverEntry>();
        await foreach (var f in c.ListDirectoryAsync(Full(path), ct))
        {
            if (f.Name is "." or "..") continue;
            // Verknüpfungen nicht folgen: sie könnten aus dem freigegebenen Ordner heraus zeigen
            if (f.IsSymbolicLink || !(f.IsDirectory || f.IsRegularFile)) continue;
            list.Add(Entry(f.Name, f.Attributes));
        }
        return list;
    }, ct);

    public async Task<DriverEntry?> StatAsync(string path, CancellationToken ct)
    {
        try { return await Use("Abfragen", async c => Entry(Path.GetFileName(StoragePath.Clean(path)), await c.GetAttributesAsync(Full(path), ct)), ct); }
        catch (KeyNotFoundException) { return null; }
    }

    public async Task<Stream> OpenReadAsync(string path, CancellationToken ct)
    {
        var c = await Acquire(ct);
        try { return new Lease(await c.OpenAsync(Full(path), FileMode.Open, FileAccess.Read, ct), () => Release(c)); }
        catch (Exception e) when (e is not OperationCanceledException) { Release(c); throw Translate(e, "Lesen"); }
        catch { Release(c); throw; }
    }

    public Task<byte[]> ReadRangeAsync(string path, long offset, int count, CancellationToken ct) => Use("Lesen", async c =>
    {
        await using var s = await c.OpenAsync(Full(path), FileMode.Open, FileAccess.Read, ct);
        s.Seek(offset, SeekOrigin.Begin);
        var buf = new byte[count];
        var done = 0;
        while (done < count)
        {
            var n = await s.ReadAsync(buf.AsMemory(done, count - done), ct);
            if (n == 0) break;
            done += n;
        }
        return done == count ? buf : buf[..done];
    }, ct);

    public async Task WriteAsync(string path, Stream data, CancellationToken ct)
    {
        var rel = StoragePath.Clean(path);
        await MkdirAsync(StoragePath.Parent(rel), ct);
        await Use("Schreiben", async c =>
        {
            // Erst unter vorläufigem Namen, dann umbenennen: Bricht die Übertragung ab, bleibt die alte Datei ganz
            var target = Full(rel);
            var temp = Full(StoragePath.Join(StoragePath.Parent(rel), ".dmtmp-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant()));
            try
            {
                await c.UploadFileAsync(data, temp, ct);
                try { c.RenameFile(temp, target, true); }
                catch (Exception e) when (e is SshException or NotSupportedException)
                {
                    // Ohne posix-rename (SSH.NET meldet die fehlende Erweiterung mit NotSupportedException): Ziel erst löschen, dann umbenennen
                    try { await c.DeleteFileAsync(target, ct); } catch (SftpPathNotFoundException) { }
                    await c.RenameFileAsync(temp, target, ct);
                }
            }
            catch
            {
                // Abbruch oder Fehler beim Hochladen oder Umbenennen: die Zwischendatei nicht liegen lassen (sonst bei jedem Versuch eine
                // weitere, z. B. bei einem Importprofil im Intervall); das Aufräumen selbst darf den eigentlichen Fehler nicht verdecken
                try { if (c.IsConnected) c.DeleteFile(temp); } catch { }
                throw;
            }
            return true;
        }, ct);
    }

    public Task MkdirAsync(string path, CancellationToken ct) => Use("Ordner anlegen", async c =>
    {
        var rel = StoragePath.Clean(path);
        var cur = "";
        foreach (var part in rel.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            cur = StoragePath.Join(cur, part);
            if (!await c.ExistsAsync(Full(cur), ct)) await c.CreateDirectoryAsync(Full(cur), ct);
        }
        if (_root.Length > 0 && rel.Length == 0 && !await c.ExistsAsync(_root, ct)) await c.CreateDirectoryAsync(_root, ct);
        return true;
    }, ct);

    public Task DeleteAsync(string path, CancellationToken ct) => Use("Löschen", async c =>
    {
        async Task Remove(string full)
        {
            Renci.SshNet.Sftp.SftpFileAttributes a;
            try { a = await c.GetAttributesAsync(full, ct); } catch (SftpPathNotFoundException) { return; }
            if (!a.IsDirectory) { try { await c.DeleteFileAsync(full, ct); } catch (SftpPathNotFoundException) { } return; }
            var kids = new List<string>();
            await foreach (var f in c.ListDirectoryAsync(full, ct)) if (f.Name is not ("." or "..")) kids.Add(full + "/" + f.Name);
            foreach (var k in kids) await Remove(k);
            try { await c.DeleteDirectoryAsync(full, ct); } catch (SftpPathNotFoundException) { }
        }
        if (StoragePath.Clean(path).Length == 0) throw new ArgumentException("Die Wurzel lässt sich nicht löschen.");
        await Remove(Full(path));
        return true;
    }, ct);

    public async Task MoveAsync(string from, string to, CancellationToken ct)
    {
        await MkdirAsync(StoragePath.Parent(StoragePath.Clean(to)), ct);
        await Use("Verschieben", async c =>
        {
            try { c.RenameFile(Full(from), Full(to), true); }
            // Ohne posix-rename: einfaches Umbenennen (scheitert nur, wenn das Ziel schon da ist)
            catch (NotSupportedException) { await c.RenameFileAsync(Full(from), Full(to), ct); }
            return true;
        }, ct);
    }

    public async Task<DriverSpace?> SpaceAsync(CancellationToken ct)
    {
        try
        {
            return await Use("Platz abfragen", async c =>
            {
                var s = await c.GetStatusAsync(_root.Length == 0 ? "." : _root, ct);
                var block = (long)s.BlockSize;
                return (DriverSpace?)new DriverSpace((long)(s.TotalBlocks - s.FreeBlocks) * block, (long)s.AvailableBlocks * block);
            }, ct);
        }
        catch (Exception e) when (e is IOException or NotSupportedException or UnauthorizedAccessException or KeyNotFoundException) { return null; }   // ohne statvfs-Erweiterung
    }

    public Task SetModifiedAsync(string path, DateTime utc, CancellationToken ct) =>
        Use("Zeit setzen", c => { c.SetLastWriteTimeUtc(Full(path), DateTime.SpecifyKind(utc, DateTimeKind.Utc)); return Task.FromResult(true); }, ct);

    /// <summary>Die Verbindungen gehören dem Ziel (<see cref="Target"/>) und schließen dort nach <see cref="IdleClose"/>.</summary>
    public void Dispose() => _disposed = true;

    /// <summary>Lesestrom, der beim Schließen die Verbindung zurückgibt.</summary>
    private sealed class Lease(Stream inner, Action done) : Stream
    {
        private int _closed;
        public override bool CanRead => true;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.ReadAsync(buffer, offset, count, ct);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0) { try { inner.Dispose(); } finally { done(); } }
            base.Dispose(disposing);
        }
    }

    // ---------------- Schlüssel erzeugen ----------------

    /// <summary>Neues Schlüsselpaar (RSA 3072): privater Teil als PEM fürs Passwortfeld, öffentlicher im OpenSSH-Format für den Server.</summary>
    public static (string PrivatePem, string PublicOpenSsh) NewKeyPair(string comment = "dmoffice")
    {
        using var rsa = RSA.Create(3072);
        var p = rsa.ExportParameters(false);
        static void Str(MemoryStream m, byte[] b) { var len = BitConverter.GetBytes(b.Length); if (BitConverter.IsLittleEndian) Array.Reverse(len); m.Write(len); m.Write(b); }
        static byte[] MpInt(byte[] b) => b.Length > 0 && (b[0] & 0x80) != 0 ? [0, .. b] : b;
        using var blob = new MemoryStream();
        Str(blob, Encoding.ASCII.GetBytes("ssh-rsa"));
        Str(blob, MpInt(p.Exponent!));
        Str(blob, MpInt(p.Modulus!));
        return (rsa.ExportRSAPrivateKeyPem(), "ssh-rsa " + Convert.ToBase64String(blob.ToArray()) + " " + comment);
    }
}

// [feature:sftptrust]
/// <summary>Verbinden abgelehnt: Der Server meldet einen Schlüssel, den der Benutzer (noch) nicht bestätigt hat.</summary>
public sealed class SftpHostKeyException(SftpDriver.HostKeyInfo info) : UnauthorizedAccessException(Text(info))
{
    public SftpDriver.HostKeyInfo Info { get; } = info;

    private static string Text(SftpDriver.HostKeyInfo i) => i.State == "changed"
        ? $"Verbinden: Der SFTP-Server meldet einen neuen Fingerabdruck ({i.Fingerprint}, bisher {i.Known}). Verbunden wird wieder, sobald er in den Einstellungen dieser Verbindung bestätigt ist."
        : $"Verbinden: Der Fingerabdruck des SFTP-Servers ({i.Fingerprint}) wartet auf Bestätigung in den Einstellungen dieser Verbindung.";
}
// [/feature:sftptrust]

/// <summary>SFTP in der Oberfläche: Schlüsselpaar erzeugen, gemerkten Server-Schlüssel zurücksetzen (nur Verwaltung).</summary>
public static class SftpApi
{
    public record HostBody(string Url);
    public record KeyBody(string? Url = null, string? Mount = null, string? Fingerprint = null); // [feature:sftptrust]

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api/sftp").RequireAuthorization();
        // Neues Schlüsselpaar: der private Teil kommt ins Passwortfeld, den öffentlichen hinterlegt man beim Server
        api.MapPost("/keypair", () =>
        {
            if (!DmOffice.Core.Features.On(DmOffice.Core.Features.Sftp)) return Results.NotFound();
            var (priv, pub) = SftpDriver.NewKeyPair();
            return Results.Ok(new { privateKey = priv, publicKey = pub });
        });
        // Der Server hat wirklich einen neuen Schlüssel bekommen: den gemerkten vergessen, beim nächsten Verbinden gilt der neue
        api.MapPost("/forget-host", (HostBody b) =>
        {
            if (!SftpDriver.TryParse(b.Url, out var host, out var port, out _, out _)) return Results.BadRequest(new { error = "Die Adresse muss die Form sftp://server:port/ordner haben." });
            SftpDriver.ForgetHost(host, port);
            return Results.NoContent();
        }).RequireAuthorization("admin");
        // [feature:sftptrust]
        // Fingerabdruck zum Bestätigen: wie sich der Server gerade meldet (Adresse aus einem Formular oder externer Ordner per Kennung)
        api.MapPost("/host-key", async (KeyBody b, HttpContext http, DmOffice.Core.ServerConfig config, CancellationToken ct) =>
        {
            if (Where(b, http, config, out var host, out var port, out var allow) is { } refused) return refused;
            return Results.Ok(await SftpDriver.ProbeHostAsync(host, port, allow, ct));
        });
        // Bestätigen: nur genau den Fingerabdruck, den der Server jetzt meldet; einen geänderten nur ein Admin
        api.MapPost("/trust-host", async (KeyBody b, HttpContext http, DmOffice.Core.ServerConfig config, CancellationToken ct) =>
        {
            if (Where(b, http, config, out var host, out var port, out var allow) is { } refused) return refused;
            var info = await SftpDriver.ProbeHostAsync(host, port, allow, ct);
            if (info.Fingerprint != b.Fingerprint?.Trim())
                return Results.Json(new { error = "Der Server meldet inzwischen einen anderen Fingerabdruck. Bitte noch einmal prüfen." }, statusCode: 409);
            if (info.State == "changed" && !DmOffice.Core.HttpUser.Space(http).User.IsAdmin)
                return Results.Json(new { error = "Einen geänderten Fingerabdruck bestätigt ein Admin." }, statusCode: 403);
            if (info.State != "trusted") SftpDriver.ConfirmHost(info.Host, info.Port, info.Fingerprint);
            return Results.Ok(info with { Known = info.Fingerprint, State = "trusted" });
        });
        // [/feature:sftptrust]
    }

    // [feature:sftptrust]
    /// <summary>Server und Port für /host-key und /trust-host. Admins: jede Adresse; sonst nur, wenn eigene SFTP-Ordner erlaubt sind,
    /// mit derselben Adressprüfung wie dort und gedrosselt. Server-Mounts nur für Admins.</summary>
    private static IResult? Where(KeyBody b, HttpContext http, DmOffice.Core.ServerConfig config, out string host, out int port, out Func<System.Net.IPAddress, bool>? allow)
    {
        host = ""; port = 22; allow = null;
        var admin = DmOffice.Core.HttpUser.Space(http).User.IsAdmin;
        var url = b.Url;
        var server = false;
        if (!string.IsNullOrEmpty(b.Mount))
        {
            if (ExternalFolders.SftpUrlOf(DmOffice.Core.HttpUser.Space(http), config, b.Mount) is not { } m) return Results.NotFound();
            (url, server) = m;
        }
        if (!SftpDriver.TryParse(url, out host, out port, out _, out _)) return Results.BadRequest(new { error = "Die Adresse muss die Form sftp://server:port/ordner haben." });
        if (admin) return null;
        if (server) return Results.Json(new { error = "Den Fingerabdruck dieses Ordners bestätigt ein Admin." }, statusCode: 403);
        if (!ExternalFolders.SftpOffered(config)) return Results.Json(new { error = "Eigene SFTP-Ordner sind nicht freigeschaltet." }, statusCode: 403);
        if (!MayCheck()) return Results.Json(new { error = "Zu viele Anfragen. Bitte kurz warten und erneut versuchen." }, statusCode: 429);
        allow = ExternalFolders.AllowFor(config.Current);
        return null;
    }

    /// <summary>Höchstens 30 Abfragen je 10 Minuten durch Benutzer ohne Verwaltungsrechte (über alle).</summary>
    private static readonly List<DateTime> Checks = [];
    private static bool MayCheck()
    {
        var now = DateTime.UtcNow;
        lock (Checks)
        {
            Checks.RemoveAll(v => now - v > TimeSpan.FromMinutes(10));
            if (Checks.Count >= 30) return false;
            Checks.Add(now);
            return true;
        }
    }
    // [/feature:sftptrust]
}
