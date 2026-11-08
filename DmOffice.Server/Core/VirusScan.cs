using System.Text.Json;

namespace DmOffice.Core;

/// <summary>Fund des Virenscanners: wird zu 400 mit der Meldung (API und WebDAV behandeln ArgumentException so).</summary>
public sealed class VirusFoundException(string message) : ArgumentException(message);

/// <summary>
/// Virenprüfung mit ClamAV (Verwaltung → Sicherheit, aus bis der Admin sie einschaltet): Dateien beim Eintragen in die Ablage
/// (FileStore.Commit - Web, WebDAV, App, Chat, Tagebuch, Office, Mail-Anhänge), Bilder und Anhänge von Notizen beim Hochladen und
/// neue Mails im Posteingang. Ist der Scanner nicht erreichbar, geht es weiter (Eintrag im Protokoll) - außer der Admin hat
/// „Ohne Scanner nichts annehmen“ gewählt. Funde stehen in data/virus-findings.json (nur Zeit, Benutzer, Ort, Name des Fundes).
/// </summary>
public static class VirusScan
{
    public static ServerConfig? Config { get; set; }
    public static string? DataRoot { get; set; }
    public static ILogger? Log { get; set; }

    public sealed record Finding(DateTime At, string User, string Where, string Virus);

    private static readonly object FindingsLock = new();
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    private static ServerSettings? S => Config?.Current;
    public static bool Enabled => S is { ClamAv: true } s && !string.IsNullOrWhiteSpace(s.ClamAvHost);

    /// <summary>Ein schon geschriebener Blob (vor dem Eintragen). Fund: VirusFoundException - der Aufrufer löscht den Blob.</summary>
    public static void CheckBlob(UserDb db, BlobInfo blob, string where) => CheckBlob(db.Blobs, db.UserId, blob, where);

    /// <summary>Wie oben, für einen beliebigen Speicher (DMS im Tresor).</summary>
    public static void CheckBlob(BlobStore blobs, string user, BlobInfo blob, string where)
    {
        if (!Enabled || blob.Id == null || blob.Size == 0) return;
        var s = S!;
        if (blob.Size > s.ClamAvMaxMb * 1024L * 1024) return;   // größer als die Grenze: nicht prüfen (clamd lehnt es sonst ab)
        ClamAv.Result r;
        using (var stream = blobs.OpenRead(blob.Id))
            r = ClamAv.ScanAsync(s.ClamAvHost!, s.ClamAvPort, stream, Timeout, CancellationToken.None).GetAwaiter().GetResult();
        Handle(r, user, where);
    }

    /// <summary>Inhalt im Speicher (Notizbilder, Anhänge). Fund: VirusFoundException.</summary>
    public static void CheckBytes(byte[] data, string user, string where)
    {
        if (!Enabled || data.Length == 0) return;
        var s = S!;
        if (data.Length > s.ClamAvMaxMb * 1024L * 1024) return;
        var r = ClamAv.ScanAsync(s.ClamAvHost!, s.ClamAvPort, new MemoryStream(data), Timeout, CancellationToken.None).GetAwaiter().GetResult();
        Handle(r, user, where);
    }

    /// <summary>Nur das Urteil (für Mails: kein Abbruch, die Mail wird markiert). Null: nicht geprüft (aus, zu groß, Scanner weg).</summary>
    public static async Task<string?> VirusInAsync(Stream data, long size, string user, string where, CancellationToken ct)
    {
        if (!Enabled) return null;
        var s = S!;
        if (size > s.ClamAvMaxMb * 1024L * 1024) return null;
        var r = await ClamAv.ScanAsync(s.ClamAvHost!, s.ClamAvPort, data, Timeout, ct);
        if (r.Verdict == ClamAv.Verdict.Infected) { Record(user, where, r.Name ?? "?"); return r.Name ?? "?"; }
        if (r.Verdict == ClamAv.Verdict.Error) Log?.LogWarning("Virenscanner: {Where}: {Error}", where, r.Error);
        return null;
    }

    private static void Handle(ClamAv.Result r, string user, string where)
    {
        switch (r.Verdict)
        {
            case ClamAv.Verdict.Infected:
                Record(user, where, r.Name ?? "?");
                throw new VirusFoundException($"„{Name(where)}“ enthält Schadsoftware ({r.Name}) und wurde nicht gespeichert.");
            case ClamAv.Verdict.Error:
                Log?.LogWarning("Virenscanner: {Where}: {Error}", where, r.Error);
                if (S?.ClamAvRequired == true)
                    throw new VirusFoundException("Der Virenscanner ist gerade nicht erreichbar – Hochladen ist erst wieder möglich, wenn er läuft.");
                break;
        }
    }

    private static string Name(string where) => where.Contains('/') ? where[(where.LastIndexOf('/') + 1)..] : where;

    private static string FindingsFile => Path.Combine(DataRoot ?? ".", "virus-findings.json");

    public static void Record(string user, string where, string virus)
    {
        Log?.LogWarning("Virenscanner: {Virus} gefunden ({User})", virus, user);
        lock (FindingsLock)
        {
            var list = Findings();
            list.Insert(0, new Finding(DateTime.UtcNow, user, where, virus));
            try { File.WriteAllText(FindingsFile, JsonSerializer.Serialize(list.Take(100))); } catch (IOException) { }
        }
    }

    public static List<Finding> Findings()
    {
        lock (FindingsLock)
        {
            try { return File.Exists(FindingsFile) ? JsonSerializer.Deserialize<List<Finding>>(File.ReadAllText(FindingsFile)) ?? [] : []; }
            catch (Exception e) when (e is IOException or JsonException) { return []; }
        }
    }

    /// <summary>EICAR-Testdatei (harmlos, jeder Scanner meldet sie) - zum Prüfen, ob der Scanner wirklich findet.</summary>
    public static byte[] Eicar => System.Text.Encoding.ASCII.GetBytes(@"X5O!P%@AP[4\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*");
}
