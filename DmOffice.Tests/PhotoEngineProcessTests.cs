using System.Diagnostics;
using System.Text.Json;
using DmOffice.Dav;

namespace DmOffice.Tests;

/// <summary>Start bei Bedarf, Leerlauf, Absturz und abgeschottete Umgebung - mit einer kleinen Ersatz-Engine. Ohne Python auf dem Rechner: nichts zu prüfen.</summary>
public class PhotoEngineProcessTests : IDisposable
{
    private const string Stub = """
        import argparse, json, os, sys
        from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
        class H(BaseHTTPRequestHandler):
            def log_message(self, *a): pass
            def reply(self, code, data):
                b = json.dumps(data).encode()
                self.send_response(code); self.send_header("Content-Length", str(len(b))); self.end_headers(); self.wfile.write(b)
            def do_GET(self):
                if self.path == "/health": self.reply(200, {"status": "ok"})
                elif self.path == "/env": self.reply(200, dict(os.environ))
                elif self.path == "/exit": self.reply(200, {}); self.wfile.flush(); os._exit(3)
                else: self.reply(404, {})
            def do_POST(self):
                self.reply(200 if self.headers.get("X-Api-Key") == os.environ["PHOTO_ENGINE_TOKEN"] else 401, {})
        ap = argparse.ArgumentParser(); ap.add_argument("--host"); ap.add_argument("--port", type=int); a = ap.parse_args()
        ThreadingHTTPServer((a.host, a.port), H).serve_forever()
        """;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly string _home = Directory.CreateTempSubdirectory("engine-").FullName;
    private readonly PhotoEngineProcess? _engine;

    public PhotoEngineProcessTests()
    {
        Directory.CreateDirectory(Path.Combine(_home, "engine"));
        File.WriteAllText(Path.Combine(_home, "engine", "__init__.py"), "");
        File.WriteAllText(Path.Combine(_home, "engine", "server.py"), Stub.Replace("\r\n", "\n"));
        if (FindPython() is { } python) _engine = new PhotoEngineProcess(_home, python, TimeSpan.Zero);
    }

    private static string? FindPython()
    {
        foreach (var name in new[] { "python3", "python" })
            try
            {
                using var p = Process.Start(new ProcessStartInfo(name, "-c \"import sys;print(sys.executable)\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
                if (p == null) continue;
                var path = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit();
                if (p.ExitCode == 0 && File.Exists(path)) return path;
            }
            catch (System.ComponentModel.Win32Exception) { }
        return null;
    }

    public void Dispose()
    {
        _engine?.Dispose();
        try { Directory.Delete(_home, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task Startet_erst_beim_ersten_Auftrag_und_verlangt_den_Schluessel()
    {
        if (_engine == null) return;
        Assert.False(_engine.Running);
        using var lease = await _engine.AcquireAsync(default);
        Assert.NotNull(lease);
        Assert.True(_engine.Running);
        Assert.StartsWith("http://127.0.0.1:", lease.Url);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await Http.PostAsync(lease.Url + "/develop", null)).StatusCode);
        using var req = new HttpRequestMessage(HttpMethod.Post, lease.Url + "/develop");
        req.Headers.Add("X-Api-Key", lease.Token);
        Assert.True((await Http.SendAsync(req)).IsSuccessStatusCode);
    }

    [Fact]
    public async Task Hauptschluessel_bleibt_draussen()
    {
        if (_engine == null) return;
        Environment.SetEnvironmentVariable("DMOFFICE_TEST_GEHEIM", "geheim");
        try
        {
            using var lease = await _engine.AcquireAsync(default);
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(lease!.Url + "/env"));
            var names = doc.RootElement.EnumerateObject().Select(x => x.Name).ToList();
            Assert.DoesNotContain("DMOFFICE_TEST_GEHEIM", names);
            Assert.DoesNotContain("DMOFFICE_MASTER_KEY", names);
            Assert.Equal("1", doc.RootElement.GetProperty("PHOTO_ENGINE_MAX_JOBS").GetString());
        }
        finally { Environment.SetEnvironmentVariable("DMOFFICE_TEST_GEHEIM", null); }
    }

    [Fact]
    public async Task Leerlauf_beendet_sie_aber_nicht_mitten_im_Auftrag()
    {
        if (_engine == null) return;
        var lease = await _engine.AcquireAsync(default);
        _engine.CheckIdle();
        Assert.True(_engine.Running);
        lease!.Dispose();
        _engine.CheckIdle();
        for (var i = 0; i < 50 && _engine.Running; i++) await Task.Delay(100);
        Assert.False(_engine.Running);
        // Der nächste Auftrag startet sie wieder
        using var again = await _engine.AcquireAsync(default);
        Assert.NotNull(again);
        Assert.True(_engine.Running);
    }

    [Fact]
    public async Task Nach_Absturz_startet_sie_beim_naechsten_Auftrag_neu()
    {
        if (_engine == null) return;
        string first;
        using (var lease = await _engine.AcquireAsync(default))
        {
            first = lease!.Url;
            try { await Http.GetAsync(first + "/exit"); } catch (HttpRequestException) { }
        }
        for (var i = 0; i < 50 && _engine.Running; i++) await Task.Delay(100);
        Assert.False(_engine.Running);
        using var next = await _engine.AcquireAsync(default);
        Assert.NotNull(next);
        Assert.True((await Http.GetAsync(next.Url + "/health")).IsSuccessStatusCode);
    }

    [Fact]
    public async Task Startet_sie_nicht_kommt_null_und_kein_sofortiger_neuer_Versuch()
    {
        var broken = new PhotoEngineProcess(_home, Path.Combine(_home, "gibt-es-nicht"), TimeSpan.Zero);
        Assert.Null(await broken.AcquireAsync(default));
        Assert.Null(await broken.AcquireAsync(default));
        Assert.False(broken.Running);
    }

    [Fact]
    public void Ohne_Einstellung_keine_eingebaute_Engine()
    {
        if (Environment.GetEnvironmentVariable("PHOTO_ENGINE_HOME") == null) Assert.Null(PhotoEngineProcess.FromEnvironment());
    }
}
