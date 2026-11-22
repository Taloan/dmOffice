using System.Text;
using DmOffice.Storage;

namespace DmOffice.Tests;

/// <summary>
/// SFTP-Treiber: Adressen, Verbindungszahl, Schlüsselpaar, gemerkte Server-Schlüssel. Der echte Durchlauf läuft nur mit
/// DMOFFICE_SFTP_TEST=„sftp://benutzer:passwort@server:port/ordner“ (etwa ein atmoz/sftp-Container), sonst wird er übersprungen.
/// </summary>
[Collection("server")]
public class SftpDriverTests
{
    [Fact]
    public void Adresse_zerlegen_mit_Port_Ordner_und_Verbindungen()
    {
        Assert.True(SftpDriver.TryParse("sftp://box.example.org:23/backup/dm?connections=6", out var h, out var p, out var r, out var c));
        Assert.Equal(("box.example.org", 23, "/backup/dm", 6), (h, p, r, c));
        Assert.True(SftpDriver.TryParse("sftp://nas.example.org/~/dmoffice", out h, out p, out r, out c));
        Assert.Equal(("nas.example.org", 22, "dmoffice", SftpDriver.DefaultConnections), (h, p, r, c));
        Assert.True(SftpDriver.TryParse("sftp://nas.example.org?connections=99", out _, out _, out r, out c));
        Assert.Equal(("", SftpDriver.MaxConnections), (r, c));
        Assert.False(SftpDriver.TryParse("https://nas.example.org/dav", out _, out _, out _, out _));
        Assert.Contains("connections=3", SftpDriver.WithConnections("sftp://nas.example.org/x?connections=9", 3));
        Assert.DoesNotContain("connections=9", SftpDriver.WithConnections("sftp://nas.example.org/x?connections=9", 3));
        Assert.True(RemoteDrivers.IsSftp(" SFTP://x"));
    }

    [Fact]
    public void Eigene_SFTP_Ordner_haben_einen_eigenen_Schalter()
    {
        var before = DmOffice.Core.Features.InReleaseOff.ToList();
        DmOffice.Core.Features.Set(before.Where(x => x != DmOffice.Core.Features.Sftp));
        try
        {
            var s = new DmOffice.Core.ServerSettings { ExternalWebDavForUsers = true, ExternalSftpForUsers = false };
            Assert.True(ExternalFolders.KindAllowed(s, "https://cloud.example.org/dav/"));
            Assert.False(ExternalFolders.KindAllowed(s, "sftp://nas.example.org/x"));
            s.ExternalWebDavForUsers = false; s.ExternalSftpForUsers = true;
            Assert.False(ExternalFolders.KindAllowed(s, "https://cloud.example.org/dav/"));
            Assert.True(ExternalFolders.KindAllowed(s, "sftp://nas.example.org/x"));
            // Ohne freigeschaltete Funktion nie
            DmOffice.Core.Features.Set([.. before, DmOffice.Core.Features.Sftp]);
            Assert.False(ExternalFolders.KindAllowed(s, "sftp://nas.example.org/x"));
        }
        finally { DmOffice.Core.Features.Set(before); }
    }

    [Fact]
    public void Erzeugter_Schluessel_ist_lesbar_und_oeffentlicher_Teil_im_OpenSSH_Format()
    {
        var (priv, pub) = SftpDriver.NewKeyPair();
        Assert.True(SftpDriver.IsPrivateKey(priv));
        Assert.StartsWith("ssh-rsa AAAA", pub);
        using var key = new Renci.SshNet.PrivateKeyFile(new MemoryStream(Encoding.UTF8.GetBytes(priv)));
        Assert.NotNull(key.Key);
    }

    [Fact]
    public void Server_Schluessel_wird_gemerkt_und_ein_anderer_abgelehnt()
    {
        var before = SftpDriver.KnownHosts;
        SftpDriver.KnownHosts = Path.Combine(Directory.CreateTempSubdirectory("dmsftp").FullName, "hosts.json");
        try
        {
            Assert.True(SftpDriver.TrustHost("Box.example.org", 23, "SHA256:aaa", out var known));
            Assert.Null(known);
            Assert.True(SftpDriver.TrustHost("box.example.org", 23, "SHA256:aaa", out _));
            Assert.False(SftpDriver.TrustHost("box.example.org", 23, "SHA256:bbb", out known));
            Assert.Equal("SHA256:aaa", known);
            Assert.True(SftpDriver.TrustHost("box.example.org", 22, "SHA256:bbb", out _));   // anderer Port: eigener Eintrag
            SftpDriver.ForgetHost("box.example.org", 23);
            Assert.True(SftpDriver.TrustHost("box.example.org", 23, "SHA256:bbb", out _));
        }
        finally { SftpDriver.KnownHosts = before; }
    }

    [Fact]
    public async Task Echter_Server_alle_Operationen_und_Verbindungsgrenze()
    {
        var env = Environment.GetEnvironmentVariable("DMOFFICE_SFTP_TEST");
        if (string.IsNullOrEmpty(env)) return;   // ohne Testserver übersprungen
        var u = new Uri(env);
        var (user, pass) = (Uri.UnescapeDataString(u.UserInfo.Split(':')[0]), Uri.UnescapeDataString(u.UserInfo.Split(':', 2)[1]));
        var url = $"sftp://{u.Host}:{u.Port}{u.AbsolutePath}?connections=2";
        var before = SftpDriver.KnownHosts;
        SftpDriver.KnownHosts = Path.Combine(Directory.CreateTempSubdirectory("dmsftp").FullName, "hosts.json");
        try
        {
            using var d = new SftpDriver(url, user, pass);
            var dir = "t" + Guid.NewGuid().ToString("N")[..8];
            await d.MkdirAsync(dir + "/a/b", default);
            await d.WriteAsync(dir + "/a/b/x.txt", new MemoryStream(Encoding.UTF8.GetBytes("Hallo SFTP")), default);
            await d.WriteAsync(dir + "/a/b/x.txt", new MemoryStream(Encoding.UTF8.GetBytes("Hallo nochmal")), default);   // ersetzt
            var list = await d.ListAsync(dir + "/a/b", default);
            Assert.Equal("x.txt", Assert.Single(list).Name);   // keine vorläufigen Dateien übrig
            Assert.Equal(13, list[0].Size);
            Assert.Equal("nochmal", Encoding.UTF8.GetString(await d.ReadRangeAsync(dir + "/a/b/x.txt", 6, 7, default)));
            await using (var s = await d.OpenReadAsync(dir + "/a/b/x.txt", default))
                Assert.Equal("Hallo nochmal", await new StreamReader(s).ReadToEndAsync());
            var t = new DateTime(2020, 5, 6, 7, 8, 9, DateTimeKind.Utc);
            await d.SetModifiedAsync(dir + "/a/b/x.txt", t, default);
            Assert.Equal(t, (await d.StatAsync(dir + "/a/b/x.txt", default))!.Modified);
            // Umbenennen scheitert (am Ziel liegt ein Ordner mit Inhalt): Fehler, aber keine Zwischendatei .dmtmp-… bleibt liegen
            await d.WriteAsync(dir + "/z/belegt/f.txt", new MemoryStream(Encoding.UTF8.GetBytes("x")), default);
            await Assert.ThrowsAnyAsync<Exception>(() => d.WriteAsync(dir + "/z/belegt", new MemoryStream(Encoding.UTF8.GetBytes("neu")), default));
            Assert.Equal(["belegt"], (await d.ListAsync(dir + "/z", default)).Select(e => e.Name));
            await d.MoveAsync(dir + "/a/b/x.txt", dir + "/c/y.txt", default);
            Assert.Null(await d.StatAsync(dir + "/a/b/x.txt", default));
            // Mehr gleichzeitige Zugriffe als Verbindungen: warten, nicht scheitern
            await Task.WhenAll(Enumerable.Range(0, 8).Select(i => d.WriteAsync($"{dir}/p/{i}.txt", new MemoryStream(Encoding.UTF8.GetBytes("n" + i)), default)));
            Assert.Equal(8, (await d.ListAsync(dir + "/p", default)).Count);
            await Assert.ThrowsAsync<KeyNotFoundException>(() => d.ListAsync(dir + "/fehlt", default));
            await d.DeleteAsync(dir, default);
            Assert.Null(await d.StatAsync(dir, default));

            // Falsches Passwort: verständlicher Fehler
            using var wrong = new SftpDriver(url, user, "falsch");
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => wrong.ListAsync("", default));
        }
        finally { SftpDriver.KnownHosts = before; }
    }

    [Fact]
    public async Task FTP_Server_an_der_Adresse_wird_erkannt()
    {
        Assert.Null(SftpDriver.OtherServerText("SSH-2.0-OpenSSH_9.6\r\n"));
        Assert.Null(SftpDriver.OtherServerText(""));
        Assert.Contains("FTP-Server", SftpDriver.OtherServerText("220---------- Welcome to Pure-FTPd ----------\r\n"));
        Assert.Contains("kein SFTP-Server", SftpDriver.OtherServerText("HTTP/1.1 400 Bad Request\r\n"));
        // Echter Ablauf: ein Dienst, der sich wie ein FTP-Server meldet
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var serve = Task.Run(async () =>
        {
            using var c = await listener.AcceptTcpClientAsync();
            var hello = System.Text.Encoding.ASCII.GetBytes("220 FTP ready\r\n");
            await c.GetStream().WriteAsync(hello);
            await Task.Delay(500);
        });
        try { Assert.Contains("FTP-Server", await SftpDriver.OtherServer("127.0.0.1", port, default)); }
        finally { await serve; listener.Stop(); }
    }

    [Fact]
    public void Pause_nach_Fehlversuchen_waechst_bis_zur_Grenze()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), SftpDriver.PauseAfter(1));
        Assert.Equal(TimeSpan.FromSeconds(60), SftpDriver.PauseAfter(2));
        Assert.Equal(SftpDriver.MaxPause, SftpDriver.PauseAfter(8));
        Assert.Equal(SftpDriver.MaxPause, SftpDriver.PauseAfter(500));
    }

    [Fact]
    public async Task Gescheitertes_Ziel_wird_nicht_dauernd_angefragt()
    {
        // Server, der annimmt und gleich schließt: zählt, wie oft dmOffice anklopft
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        int accepted = 0;
        using var stop = new CancellationTokenSource();
        var serve = Task.Run(async () =>
        {
            try { while (true) { using var c = await listener.AcceptTcpClientAsync(stop.Token); Interlocked.Increment(ref accepted); } }
            catch (OperationCanceledException) { }
        });
        try
        {
            var url = $"sftp://127.0.0.1:{port}/x";
            using (var first = new SftpDriver(url, "u", "pw-" + Guid.NewGuid()))
            {
                await Assert.ThrowsAnyAsync<Exception>(() => first.ListAsync("", default));
                var after = Volatile.Read(ref accepted);
                Assert.True(after >= 1);
                // Ein zweiter Treiber zum selben Ziel (andere Funktion) wartet die Pause ab, ohne den Server zu fragen
                var secret = "pw-x";
                using var a = new SftpDriver(url, "v", secret);
                await Assert.ThrowsAnyAsync<Exception>(() => a.ListAsync("", default));
                var afterA = Volatile.Read(ref accepted);
                using var b = new SftpDriver(url, "v", secret);
                var e = await Assert.ThrowsAsync<IOException>(() => b.ListAsync("", default));
                Assert.Contains("nächster Versuch", e.Message);
                Assert.Equal(afterA, Volatile.Read(ref accepted));
                // Auf Wunsch sofort neu versuchen
                b.RetryNow();
                await Assert.ThrowsAnyAsync<Exception>(() => b.ListAsync("", default));
                Assert.True(Volatile.Read(ref accepted) > afterA);
            }
        }
        finally { stop.Cancel(); listener.Stop(); await serve; }
    }
}
