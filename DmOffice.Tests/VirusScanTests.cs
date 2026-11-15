using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DmOffice.Core;
using DmOffice.Dav;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>Virenprüfung mit ClamAV - gegen einen nachgebauten clamd (meldet die EICAR-Testdatei, alles andere ist sauber).</summary>
[Collection("server")]
public class VirusScanTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public VirusScanTests(ChatFixture fx) => _fx = fx;

    /// <summary>Kleiner clamd: PING, VERSION, INSTREAM (Stücke mit Länge, Ende mit 0).</summary>
    private sealed class FakeClamd : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public int Scans;

        public FakeClamd()
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    TcpClient c;
                    try { c = await _listener.AcceptTcpClientAsync(_stop.Token); } catch { return; }
                    _ = Task.Run(() => Serve(c));
                }
            });
        }

        private async Task Serve(TcpClient c)
        {
            using (c)
            {
                var s = c.GetStream();
                var cmd = new StringBuilder();
                var one = new byte[1];
                while (await s.ReadAsync(one) == 1 && one[0] != 0) cmd.Append((char)one[0]);
                string reply;
                switch (cmd.ToString())
                {
                    case "zPING": reply = "PONG"; break;
                    case "zVERSION": reply = "ClamAV 1.4.1/27450/Mon Oct 26 08:00:00 2026"; break;
                    case "zINSTREAM":
                        Interlocked.Increment(ref Scans);
                        var data = new MemoryStream();
                        var len = new byte[4];
                        while (true)
                        {
                            await s.ReadExactlyAsync(len);
                            var n = (int)BinaryPrimitives.ReadUInt32BigEndian(len);
                            if (n == 0) break;
                            var buf = new byte[n];
                            await s.ReadExactlyAsync(buf);
                            data.Write(buf);
                        }
                        reply = Encoding.ASCII.GetString(data.ToArray()).Contains("EICAR-STANDARD-ANTIVIRUS-TEST-FILE") ? "stream: Win.Test.EICAR_HDB-1 FOUND" : "stream: OK";
                        break;
                    default: reply = "UNKNOWN COMMAND"; break;
                }
                await s.WriteAsync(Encoding.ASCII.GetBytes(reply + "\0"));
            }
        }

        public void Dispose() { _stop.Cancel(); _listener.Stop(); }
    }

    [Theory]
    [InlineData("stream: OK", ClamAv.Verdict.Clean, null)]
    [InlineData("stream: Win.Test.EICAR_HDB-1 FOUND\0", ClamAv.Verdict.Infected, "Win.Test.EICAR_HDB-1")]
    [InlineData("INSTREAM size limit exceeded. ERROR", ClamAv.Verdict.Error, null)]
    public void Antworten_von_clamd(string reply, ClamAv.Verdict verdict, string? name)
    {
        var r = ClamAv.Parse(reply);
        Assert.Equal(verdict, r.Verdict);
        Assert.Equal(name, r.Name);
    }

    [Fact]
    public async Task Fund_wird_abgelehnt_Sauberes_gespeichert_Ausfall_nach_Einstellung()
    {
        var config = _fx.Factory.Services.GetRequiredService<ServerConfig>();
        using var clamd = new FakeClamd();
        var space = _fx.Space("ben");
        var c = _fx.As("ben");
        try
        {
            // Aus: nichts wird geprüft
            FileStore.Put(space.Db, "/virus-aus.txt", VirusScan.Eicar, "text/plain");
            Assert.Equal(0, clamd.Scans);

            config.Update(s => { s.ClamAv = true; s.ClamAvHost = "127.0.0.1"; s.ClamAvPort = clamd.Port; s.ClamAvRequired = false; });
            // Sauber: gespeichert
            FileStore.Put(space.Db, "/sauber.txt", "Hallo"u8.ToArray(), "text/plain");
            Assert.NotNull(FileStore.Get(space.Db, "/sauber.txt"));
            // Fund beim Hochladen: 400 mit Name, nicht gespeichert, in den Funden
            var form = new MultipartFormDataContent { { new ByteArrayContent(VirusScan.Eicar), "files", "eicar.com" } };
            var r = await c.PostAsync("/api/files/upload?path=" + Uri.EscapeDataString("/"), form);
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            Assert.Contains("EICAR", await r.Content.ReadAsStringAsync());
            Assert.Null(FileStore.Get(space.Db, "/eicar.com"));
            Assert.Contains(VirusScan.Findings(), f => f.Virus == "Win.Test.EICAR_HDB-1" && f.Where.EndsWith("eicar.com"));
            // Auch als Bild einer Notiz
            var img = new MultipartFormDataContent { { new ByteArrayContent(VirusScan.Eicar), "file", "bild.png" } };
            Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync("/api/notes/images", img)).StatusCode);
            // Auch beim Import ins DMS: abgelehnt, kein Dokument
            (await c.GetAsync("/api/dms/profiles")).EnsureSuccessStatusCode();
            DmOffice.Dms.Dms.Ensure(space.Db);
            var dmsBefore = Convert.ToInt32(space.Db.Scalar("SELECT COUNT(*) FROM dms_docs"));
            var doc = new MultipartFormDataContent { { new ByteArrayContent(VirusScan.Eicar), "file", "rechnung.pdf" } };
            var dr = await c.PostAsync("/api/dms/upload", doc);
            Assert.Equal(HttpStatusCode.BadRequest, dr.StatusCode);
            Assert.Contains("EICAR", await dr.Content.ReadAsStringAsync());
            Assert.Equal(dmsBefore, Convert.ToInt32(space.Db.Scalar("SELECT COUNT(*) FROM dms_docs")));

            // Scanner weg: weiter (Vorgabe) - oder nichts annehmen
            config.Update(s => s.ClamAvPort = 1);
            FileStore.Put(space.Db, "/ohne-scanner.txt", "x"u8.ToArray(), "text/plain");
            config.Update(s => s.ClamAvRequired = true);
            Assert.Throws<VirusFoundException>(() => FileStore.Put(space.Db, "/abgelehnt.txt", "x"u8.ToArray(), "text/plain"));
            Assert.Null(FileStore.Get(space.Db, "/abgelehnt.txt"));

            // Was der Test-Knopf der Verwaltung tut: erreichbar, Stand, EICAR wird gefunden
            var (ok, version, _) = await ClamAv.PingAsync("127.0.0.1", clamd.Port, CancellationToken.None);
            Assert.True(ok);
            Assert.StartsWith("ClamAV", version);
            var eicar = await ClamAv.ScanAsync("127.0.0.1", clamd.Port, new MemoryStream(VirusScan.Eicar), TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.Equal("Win.Test.EICAR_HDB-1", eicar.Name);
            Assert.False((await ClamAv.PingAsync("127.0.0.1", 1, CancellationToken.None)).Ok);
            // Die Verwaltung ist nicht für Benutzer (und nicht mit App-Passwort)
            Assert.False((await c.GetAsync("/api/admin/virus")).IsSuccessStatusCode);
        }
        finally
        {
            config.Update(s => { s.ClamAv = false; s.ClamAvHost = null; s.ClamAvPort = 3310; s.ClamAvRequired = false; });
            foreach (var p in new[] { "/virus-aus.txt", "/sauber.txt", "/ohne-scanner.txt" }) try { FileStore.Delete(space.Db, p); } catch { }
        }
    }
}
