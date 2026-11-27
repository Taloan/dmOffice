using System.Text;
using DmOffice.Core;
using DmOffice.Storage;

namespace DmOffice.Tests;

/// <summary>
/// Fingerabdruck des SFTP-Servers bestätigen (sftptrust): Stand neu/bestätigt/geändert, schon gemerkte gelten als bestätigt,
/// ohne Bestätigung wird nicht verbunden. Der Teil mit echtem Server läuft nur mit DMOFFICE_SFTP_TEST (wie in SftpDriverTests).
/// </summary>
[Collection("server")]
public class SftpTrustTests
{
    private static async Task With(bool on, Func<string, Task> run)
    {
        var before = Features.InReleaseOff.ToList();
        var hosts = SftpDriver.KnownHosts;
        Features.Set(on ? before.Where(x => x != Features.SftpTrust) : before.Append(Features.SftpTrust).Distinct());
        SftpDriver.KnownHosts = Path.Combine(Directory.CreateTempSubdirectory("dmsftp").FullName, "hosts.json");
        try { await run(SftpDriver.KnownHosts); }
        finally { Features.Set(before); SftpDriver.KnownHosts = hosts; }
    }

    [Fact]
    public Task Stand_neu_bestaetigt_geaendert() => With(true, file =>
    {
        Assert.Equal("new", SftpDriver.HostState("Box.example.org", 23, "SHA256:aaa", out var known));
        Assert.Null(known);
        Assert.False(File.Exists(file));   // Abfragen merkt nichts
        SftpDriver.ConfirmHost("Box.example.org", 23, "SHA256:aaa");
        Assert.Equal("trusted", SftpDriver.HostState("box.example.org", 23, "SHA256:aaa", out _));
        Assert.Equal("changed", SftpDriver.HostState("box.example.org", 23, "SHA256:bbb", out known));
        Assert.Equal("SHA256:aaa", known);
        Assert.Equal("new", SftpDriver.HostState("box.example.org", 22, "SHA256:aaa", out _));   // anderer Port: eigener Eintrag
        SftpDriver.ConfirmHost("box.example.org", 23, "SHA256:bbb");   // bewusst neu bestätigt: ersetzt den alten
        Assert.Equal("trusted", SftpDriver.HostState("box.example.org", 23, "SHA256:bbb", out _));
        Assert.Equal("changed", SftpDriver.HostState("box.example.org", 23, "SHA256:aaa", out _));
        return Task.CompletedTask;
    });

    [Fact]
    public Task Frueher_gemerkter_Schluessel_gilt_als_bestaetigt() => With(true, file =>
    {
        // So hat der Treiber vor der Bestätigung gemerkt (beim ersten Verbinden)
        File.WriteAllText(file, """{"nas.example.org:2222":"SHA256:alt"}""");
        Assert.Equal("trusted", SftpDriver.HostState("nas.example.org", 2222, "SHA256:alt", out _));
        Assert.Equal("changed", SftpDriver.HostState("nas.example.org", 2222, "SHA256:neu", out var known));
        Assert.Equal("SHA256:alt", known);
        return Task.CompletedTask;
    });

    [Fact]
    public void Meldung_nennt_Fingerabdruecke()
    {
        var neu = new SftpHostKeyException(new SftpDriver.HostKeyInfo("nas", 22, "SHA256:x", "ssh-ed25519", null, "new"));
        Assert.Contains("SHA256:x", neu.Message);
        Assert.Contains("Bestätigung", neu.Message);
        var anders = new SftpHostKeyException(new SftpDriver.HostKeyInfo("nas", 22, "SHA256:y", "ssh-ed25519", "SHA256:x", "changed"));
        Assert.Contains("SHA256:y", anders.Message);
        Assert.Contains("bisher SHA256:x", anders.Message);
        Assert.IsAssignableFrom<UnauthorizedAccessException>(anders);
    }

    [Fact]
    public Task Echter_Server_erst_nach_Bestaetigung_verbunden() => With(true, async file =>
    {
        var env = Environment.GetEnvironmentVariable("DMOFFICE_SFTP_TEST");
        if (string.IsNullOrEmpty(env)) return;   // ohne Testserver übersprungen
        var (url, user, pass) = Parse(env);
        SftpDriver.TryParse(url, out var host, out var port, out _, out _);

        // Unbestätigt: nicht verbunden, nichts gemerkt (anderes Passwort: eigenes Ziel ohne ruhende Verbindungen anderer Tests;
        // der Schlüssel kommt vor der Anmeldung)
        using (var d = new SftpDriver(url, user, pass + "-a"))
        {
            var e = await Assert.ThrowsAsync<SftpHostKeyException>(() => d.ListAsync("", default));
            Assert.Equal("new", e.Info.State);
            Assert.StartsWith("SHA256:", e.Info.Fingerprint);
            // Gleich danach (Pause nach dem Fehlschlag): weiterhin der Fingerabdruck, kein allgemeiner Verbindungsfehler
            var again = await Assert.ThrowsAsync<SftpHostKeyException>(() => d.ListAsync("", default));
            Assert.Equal(e.Info.Fingerprint, again.Info.Fingerprint);
        }
        Assert.False(File.Exists(file));

        // Abfragen ohne Anmeldung: derselbe Fingerabdruck samt Schlüsseltyp
        var info = await SftpDriver.ProbeHostAsync(host, port, null, default);
        Assert.Equal("new", info.State);
        Assert.StartsWith("ssh-", info.KeyType);

        // Bestätigt: verbunden (ohne die Pause nach dem abgelehnten Versuch abzuwarten)
        SftpDriver.ConfirmHost(host, port, info.Fingerprint);
        using (var d = new SftpDriver(url, user, pass))
            await d.ListAsync("", default);
        Assert.Equal("trusted", (await SftpDriver.ProbeHostAsync(host, port, null, default)).State);

        // Geändert (anderer bestätigter Abdruck): nicht verbunden, beide Abdrücke in der Meldung
        File.WriteAllText(file, $$"""{"{{host.ToLowerInvariant()}}:{{port}}":"SHA256:frueher"}""");
        using (var d = new SftpDriver(url, user, pass + "-b"))
        {
            var e = await Assert.ThrowsAsync<SftpHostKeyException>(() => d.ListAsync("", default));
            Assert.Equal("changed", e.Info.State);
            Assert.Equal("SHA256:frueher", e.Info.Known);
            Assert.Equal(info.Fingerprint, e.Info.Fingerprint);
        }
        Assert.Contains("SHA256:frueher", File.ReadAllText(file));   // nicht still überschrieben
    });

    [Fact]
    public Task Echter_Server_ohne_Schalter_wie_bisher_gemerkt() => With(false, async file =>
    {
        var env = Environment.GetEnvironmentVariable("DMOFFICE_SFTP_TEST");
        if (string.IsNullOrEmpty(env)) return;
        var (url, user, pass) = Parse(env);
        // Falsches Passwort: der Schlüssel wird vor der Anmeldung gemerkt, keine Verbindung bleibt für andere Tests liegen
        using var d = new SftpDriver(url, user, pass + "-c");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => d.ListAsync("", default));
        Assert.Contains("SHA256:", File.ReadAllText(file));
    });

    private static (string Url, string User, string Pass) Parse(string env)
    {
        var u = new Uri(env);
        return ($"sftp://{u.Host}:{u.Port}{u.AbsolutePath}", Uri.UnescapeDataString(u.UserInfo.Split(':')[0]), Uri.UnescapeDataString(u.UserInfo.Split(':', 2)[1]));
    }
}
