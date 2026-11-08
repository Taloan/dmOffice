using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace DmOffice.Dav;

/// <summary>Zugang zur Photo-Engine für einen Auftrag. Solange er offen ist, beendet der Leerlauf die eingebaute Engine nicht.</summary>
public sealed class PhotoEngineLease(string url, string? token, Action? done = null) : IDisposable
{
    public string Url { get; } = url;
    public string? Token { get; } = token;
    private Action? _done = done;
    public void Dispose() => Interlocked.Exchange(ref _done, null)?.Invoke();
}

/// <summary>
/// Die eingebaute Photo-Engine (photo-engine/, im Image unter /opt/photo-engine): ein eigener Python-Prozess, den dmOffice erst beim
/// ersten Auftrag startet und nach einer Weile ohne Auftrag wieder beendet - im Leerlauf kostet sie so keinen Speicher.
/// Stürzt sie ab, startet sie beim nächsten Auftrag neu. Sie hört nur auf 127.0.0.1, mit einem Schlüssel je Start, und bekommt
/// von der Umgebung nur das Nötigste (kein Hauptschlüssel). Bei Speichermangel beendet der Kernel zuerst sie, nicht dmOffice.
/// </summary>
public sealed class PhotoEngineProcess(string home, string python, TimeSpan idleAfter) : IDisposable
{
    /// <summary>Höchstens ein Auftrag /develop gleichzeitig und das Bildflächen-Limit: Vollauflösung braucht bis etwa 1 GB je Auftrag.</summary>
    private const string MaxJobs = "1", MaxPixels = "120000000";
    private static readonly TimeSpan StartLimit = TimeSpan.FromSeconds(60), RetryAfter = TimeSpan.FromMinutes(1);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

    private readonly SemaphoreSlim _starting = new(1, 1);
    private readonly Lock _gate = new();
    private Process? _process;
    private string? _url, _token;
    private int _active;
    private DateTime _lastUsed = DateTime.UtcNow, _retryAt = DateTime.MinValue;
    private Timer? _idle;

    public ILogger? Log { get; set; }

    /// <summary>Die Engine im Image: PHOTO_ENGINE_HOME zeigt auf ihren Ordner; null, wenn sie hier nicht dabei ist.</summary>
    public static PhotoEngineProcess? FromEnvironment()
    {
        if (Environment.GetEnvironmentVariable("PHOTO_ENGINE_HOME") is not { Length: > 0 } home) return null;
        var python = Environment.GetEnvironmentVariable("PHOTO_ENGINE_PYTHON") is { Length: > 0 } p ? p : Path.Combine(home, "venv", "bin", "python");
        return File.Exists(Path.Combine(home, "engine", "server.py")) && File.Exists(python) ? new PhotoEngineProcess(home, python, TimeSpan.FromMinutes(10)) : null;
    }

    public bool Running { get { lock (_gate) return Alive; } }
    private bool Alive => _process is { HasExited: false };

    /// <summary>Zugang für einen Auftrag, startet die Engine wenn nötig; null, wenn sie nicht startet (dann erst nach einer Minute wieder versuchen).</summary>
    public async Task<PhotoEngineLease?> AcquireAsync(CancellationToken ct)
    {
        if (TryLease() is { } running) return running;
        await _starting.WaitAsync(ct);
        try
        {
            if (TryLease() is { } other) return other;   // gestartet, während dieser Aufruf wartete
            if (DateTime.UtcNow < _retryAt) return null;
            if (!await StartAsync(ct)) { _retryAt = DateTime.UtcNow + RetryAfter; return null; }
            return TryLease();
        }
        finally { _starting.Release(); }
    }

    private PhotoEngineLease? TryLease()
    {
        lock (_gate)
        {
            if (!Alive) return null;
            _active++;
            _lastUsed = DateTime.UtcNow;
            return new PhotoEngineLease(_url!, _token, Release);
        }
    }

    private void Release()
    {
        lock (_gate) { _active--; _lastUsed = DateTime.UtcNow; }
    }

    private async Task<bool> StartAsync(CancellationToken ct)
    {
        int port;
        using (var free = new TcpListener(IPAddress.Loopback, 0))
        {
            free.Start();
            port = ((IPEndPoint)free.LocalEndpoint).Port;
        }
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        string[] engine = [python, "-m", "engine.server", "--host", "127.0.0.1", "--port", port.ToString()];
        var psi = new ProcessStartInfo { WorkingDirectory = home, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        if (OperatingSystem.IsLinux())
        {
            // Bei Speichermangel zuerst die Engine: oom_score_adj vor dem Start setzen, Kindprozesse erben ihn
            psi.FileName = "/bin/sh";
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("{ echo 800 > /proc/self/oom_score_adj; } 2>/dev/null; exec \"$0\" \"$@\"");
            foreach (var a in engine) psi.ArgumentList.Add(a);
        }
        else
        {
            psi.FileName = python;
            foreach (var a in engine.Skip(1)) psi.ArgumentList.Add(a);
        }
        // Nur das Nötigste: Hauptschlüssel und Zugangsdaten aus der Umgebung von dmOffice bleiben draußen
        psi.Environment.Clear();
        foreach (var name in new[] { "PATH", "LANG", "TZ", "SystemRoot", "TEMP", "TMP" })
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } v) psi.Environment[name] = v;
        psi.Environment["HOME"] = Path.GetTempPath();
        psi.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        psi.Environment["PYTHONUNBUFFERED"] = "1";
        psi.Environment["PHOTO_ENGINE_TOKEN"] = token;
        psi.Environment["PHOTO_ENGINE_MAX_JOBS"] = MaxJobs;
        psi.Environment["PHOTO_ENGINE_MAX_PIXELS"] = MaxPixels;
        psi.Environment["PHOTO_ENGINE_MODELS"] = Path.Combine(home, "models");
        psi.Environment["PHOTO_ENGINE_VERSION"] = Environment.GetEnvironmentVariable("DMOFFICE_VERSION") ?? "dev";

        var errors = new Queue<string>();
        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("kein Prozess");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log?.LogWarning("Photo-Engine startet nicht: {Error}", e.Message);
            return false;
        }
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errors) { errors.Enqueue(e.Data); if (errors.Count > 5) errors.Dequeue(); } };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var url = $"http://127.0.0.1:{port}";
        var stop = DateTime.UtcNow + StartLimit;
        while (!process.HasExited && DateTime.UtcNow < stop)
        {
            try
            {
                using var res = await Http.GetAsync(url + "/health", ct);
                if (res.IsSuccessStatusCode)
                {
                    lock (_gate)
                    {
                        _process?.Dispose();
                        _process = process;
                        _url = url;
                        _token = token;
                        _lastUsed = DateTime.UtcNow;
                        _idle ??= new Timer(_ => CheckIdle(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
                    }
                    Log?.LogInformation("Photo-Engine gestartet");
                    return true;
                }
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { }
            await Task.Delay(200, ct);
        }
        Kill(process);
        string last;
        lock (errors) last = string.Join(" | ", errors);
        Log?.LogWarning("Photo-Engine nicht bereit: {Errors}", last);
        process.Dispose();
        return false;
    }

    /// <summary>Lange kein Auftrag: beenden, gibt den Speicher frei.</summary>
    internal void CheckIdle()
    {
        lock (_gate)
        {
            if (!Alive || _active > 0 || DateTime.UtcNow - _lastUsed < idleAfter) return;
            Kill(_process!);
        }
        Log?.LogInformation("Photo-Engine wegen Leerlauf beendet");
    }

    /// <summary>Sofort beenden (ausgeschaltet, Programmende), auch mitten im Auftrag.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (Alive) Kill(_process!);
        }
    }

    private static void Kill(Process p)
    {
        try { p.Kill(entireProcessTree: true); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
    }

    public void Dispose()
    {
        Stop();
        _idle?.Dispose();
    }
}
