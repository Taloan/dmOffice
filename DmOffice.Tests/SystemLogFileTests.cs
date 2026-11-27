using DmOffice.Core;

namespace DmOffice.Tests;

/// <summary>Systemprotokoll auf Dauer (syslogfile): Tagesdateien, Aufräumen, Größenbremse, nur gültige Tage lesbar.</summary>
public class SystemLogFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dmoffice-syslog-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static readonly DateTime Day1 = new(2026, 11, 17, 23, 59, 58, DateTimeKind.Utc);

    [Fact]
    public void Eintraege_landen_je_Tag_in_eigener_Datei_eine_Zeile_je_Eintrag()
    {
        var w = new SystemLogFile(_dir, now: () => Day1);
        Exception boom;
        try { throw new InvalidOperationException("kaputt\nzweite Zeile", new IOException("innen")); } catch (Exception x) { boom = x; }
        w.Write(Day1, "Warning", "Mail.MailSync", "Mail-Sync a@b: Zeitüberschreitung", boom);
        w.Write(Day1.AddSeconds(3), "Information", "Storage.PlainBackupService", "Datenexport x: 3 Dateien\tfertig", (string?)null);
        w.Dispose();

        var first = File.ReadAllLines(Path.Combine(_dir, "dmoffice-2026-11-17.log"));
        var line = Assert.Single(first);
        var parts = line.Split('\t');
        Assert.Equal(5, parts.Length);
        Assert.Equal("2026-11-17T23:59:58.000Z", parts[0]);
        Assert.Equal("Warning", parts[1]);
        Assert.Equal("Mail.MailSync", parts[2]);
        Assert.Contains("InvalidOperationException: kaputt", parts[4]);
        Assert.Contains("IOException: innen", parts[4]);
        Assert.Contains("SystemLogFileTests", parts[4]); // Stapel, einzeilig

        var second = Assert.Single(File.ReadAllLines(Path.Combine(_dir, "dmoffice-2026-11-18.log")));
        Assert.StartsWith("2026-11-18T00:00:01.000Z\tInformation\tStorage.PlainBackupService\tDatenexport x: 3 Dateien fertig\t", second);

        var read = new SystemLogFile(_dir).Read("2026-11-17");
        Assert.NotNull(read);
        var e = Assert.Single(read.Value.Entries);
        Assert.Equal("Mail.MailSync", e.Category);
        Assert.Equal(Day1, e.At);
        Assert.NotNull(e.Exception);
    }

    [Fact]
    public void Nach_Neustart_am_selben_Tag_wird_angehaengt()
    {
        var w = new SystemLogFile(_dir, now: () => Day1);
        w.Write(Day1, "Error", "Http", "500 GET /a", (string?)null);
        w.Dispose();
        w = new SystemLogFile(_dir, now: () => Day1);
        w.Write(Day1, "Error", "Http", "500 GET /b", (string?)null);
        w.Dispose();
        Assert.Equal(2, File.ReadAllLines(Path.Combine(_dir, "dmoffice-2026-11-17.log")).Length);
    }

    [Fact]
    public void Dateien_aelter_als_30_Tage_werden_geloescht()
    {
        Directory.CreateDirectory(_dir);
        var today = new DateTime(2026, 11, 18, 4, 0, 0, DateTimeKind.Utc);
        foreach (var d in new[] { "2026-10-01", "2026-10-18", "2026-10-19", "2026-11-18" }) File.WriteAllText(Path.Combine(_dir, $"dmoffice-{d}.log"), "x\n");
        File.WriteAllText(Path.Combine(_dir, "anderes.log"), "bleibt");
        var w = new SystemLogFile(_dir, now: () => today);
        w.Dispose(); // Aufräumen beim Start, Dispose wartet den Ablauf ab
        var left = Directory.GetFiles(_dir).Select(Path.GetFileName).Order().ToList();
        Assert.Equal(["anderes.log", "dmoffice-2026-10-19.log", "dmoffice-2026-11-18.log"], left);

        // Täglich: ein Tag später ist auch der 19.10. dran
        var later = today.AddDays(1);
        var w2 = new SystemLogFile(_dir, now: () => later);
        w2.Cleanup();
        w2.Dispose();
        Assert.False(File.Exists(Path.Combine(_dir, "dmoffice-2026-10-19.log")));
        Assert.Equal(["2026-11-18"], w2.Days().Select(d => d.Day));
    }

    [Fact]
    public void Groessenbremse_schreibt_Hinweis_und_verwirft_den_Rest_des_Tages()
    {
        var w = new SystemLogFile(_dir, maxBytes: 1000, now: () => Day1);
        for (var i = 0; i < 100; i++) w.Write(Day1, "Warning", "Test", "Meldung Nummer " + i + new string('x', 40), (string?)null);
        w.Write(Day1.AddDays(1), "Warning", "Test", "neuer Tag", (string?)null);
        w.Dispose();
        var path = Path.Combine(_dir, "dmoffice-2026-11-17.log");
        var lines = File.ReadAllLines(path);
        Assert.Contains("Tagesgrenze", lines[^1]);
        Assert.Equal(1, lines.Count(l => l.Contains("Tagesgrenze")));
        Assert.True(new FileInfo(path).Length < 1200);
        Assert.Contains("neuer Tag", File.ReadAllText(Path.Combine(_dir, "dmoffice-2026-11-18.log")));

        // Neustart am selben Tag: bleibt gebremst, kein zweiter Hinweis
        w = new SystemLogFile(_dir, maxBytes: 1000, now: () => Day1);
        w.Write(Day1, "Warning", "Test", "noch eine", (string?)null);
        w.Dispose();
        Assert.Equal(lines.Length, File.ReadAllLines(path).Length);
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("..%2F..%2Fetc")]
    [InlineData("2026-11-17/../../x")]
    [InlineData("2026-11-17.log")]
    [InlineData("dmoffice-2026-11-17")]
    [InlineData("2026-13-01")]
    [InlineData("2026-02-30")]
    [InlineData(" 2026-11-17")]
    [InlineData("2026-11-17\n")]
    [InlineData("")]
    [InlineData(null)]
    public void Nur_gueltige_Tage_fuehren_zu_einer_Datei(string? day)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "dmoffice-2026-11-17.log"), "");
        var w = new SystemLogFile(_dir);
        Assert.Null(w.PathFor(day));
        Assert.Null(w.Read(day));
        Assert.Null(w.OpenRead(day));
        w.Dispose();
    }

    [Fact]
    public void Gueltiger_Tag_bleibt_im_Protokollordner()
    {
        var w = new SystemLogFile(_dir);
        Assert.Equal(Path.Combine(Path.GetFullPath(_dir), "dmoffice-2026-11-17.log"), w.PathFor("2026-11-17"));
        Assert.Null(w.Read("2026-11-16")); // gibt es nicht
        w.Dispose();
    }

    [Fact]
    public void Nicht_beschreibbarer_Ordner_blockiert_nicht()
    {
        // Ordnerpfad ist eine Datei: Schreiben scheitert, Write kehrt trotzdem zurück
        Directory.CreateDirectory(_dir);
        var blocked = Path.Combine(_dir, "datei");
        File.WriteAllText(blocked, "");
        var w = new SystemLogFile(blocked, now: () => Day1);
        for (var i = 0; i < 10; i++) w.Write(Day1, "Error", "Test", "x", (string?)null);
        w.Dispose();
        Assert.Empty(w.Days());
    }

    [Theory]
    [InlineData("DmOffice.Storage.PlainBackupService", true)]
    [InlineData("DmOffice.Core.S3Backup", true)]
    [InlineData("DmOffice.Mail.MailSync", true)]
    [InlineData("Microsoft.Hosting.Lifetime", true)]
    [InlineData("DmOffice.Mail.MailApi", false)]
    [InlineData("Microsoft.AspNetCore.Hosting.Diagnostics", false)]
    public void Informationen_nur_aus_Hintergrundaufgaben(string category, bool wanted) =>
        Assert.Equal(wanted, SystemLogFile.InfoWanted(category));
}
