using System.Diagnostics;
using DmOffice.Core;
using DmOffice.Mail;
using Xunit.Abstractions;

namespace DmOffice.Tests;

/// <summary>
/// Messung mit 40.000 Mails samt Inhalt in einer SQLCipher-Datenbank.
/// Laeuft nur mit DMOFFICE_PERF=1 (dauert und braucht ~1 GB Platz).
/// </summary>
public class MailPerfTests
{
    private readonly ITestOutputHelper _out;
    public MailPerfTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Viertzigtausend_Mails()
    {
        if (Environment.GetEnvironmentVariable("DMOFFICE_PERF") != "1") return;
        var dir = Directory.CreateTempSubdirectory("dmperf");
        var db = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "t");
        MailStore.ReplaceFolders(db, "a", Enumerable.Range(0, 20)
            .Select(i => (i == 0 ? "INBOX" : $"Ordner{i}", i == 0 ? "INBOX" : $"Ordner{i}", "/", i == 0 ? "inbox" : (string?)null, true)).ToList());

        var plain = new byte[20_000];
        new Random(1).NextBytes(plain);
        var raw = MailStore.Compress(plain);
        var sw = Stopwatch.StartNew();
        using (var conn = db.Open())
        using (var tx = conn.BeginTransaction())
        {
            for (int i = 0; i < 40_000; i++)
            {
                var folder = i % 20 == 0 ? "INBOX" : $"Ordner{i % 20}";
                var id = UserDb.Query(conn, @"INSERT INTO mail(account,folder,uid,subject,from_name,from_addr,date,seen,flagged)
                    VALUES('a',$f,$u,$s,'Absender','a@x.de',$d,$seen,$fl) RETURNING id", r => r.GetInt64(0),
                    ("$f", folder), ("$u", (long)i), ("$s", $"Betreff {i} Rechnung Angebot"), ("$d", 1_700_000_000_000L + i * 60_000L),
                    ("$seen", i % 7 == 0 ? 0 : 1), ("$fl", i % 500 == 0 ? 1 : 0)).First();
                UserDb.Exec(conn, "INSERT INTO mail_fts(rowid,subject,sender,recipients,body) VALUES($i,$s,'Absender a@x.de','',$b)",
                    ("$i", id), ("$s", $"Betreff {i} Rechnung Angebot"), ("$b", $"Text der Mail Nummer {i} mit etwas Inhalt zum Suchen"));
                MailStore.StoreRawRaw(conn, id, raw);
            }
            tx.Commit();
        }
        _out.WriteLine($"Anlegen: {sw.ElapsedMilliseconds} ms, Datei {new FileInfo(Path.Combine(dir.FullName, "u.db")).Length / 1024 / 1024} MB");

        void Time(string name, Action a)
        {
            a(); // warm
            var t = Stopwatch.StartNew();
            for (int i = 0; i < 5; i++) a();
            _out.WriteLine($"{name}: {t.Elapsed.TotalMilliseconds / 5:F1} ms");
        }
        Time("Ordnerliste + Markiert-Zaehlung", () => { MailStore.Folders(db, "a"); MailStore.FlaggedCounts(db, "a"); });
        Time("Liste Posteingang (100)", () => MailStore.List(db, "a", "INBOX", 0, 100, null));
        Time("Liste ungelesen", () => MailStore.List(db, "a", "INBOX", 0, 100, "unread"));
        Time("Ordner Markiert", () => MailStore.List(db, "a", MailStore.FlaggedFolder, 0, 100, null));
        Time("Volltextsuche", () => MailStore.Search(db, "rechnung", "a", null));
        Time("Suche nach Relevanz", () => MailStore.Search(db, "nummer 1234", "a", null, relevance: true));
        Time("Fehlende Inhalte zaehlen", () => MailStore.MissingCount(db, "a", 50L << 20));
        Time("Eine Mail lesen", () => MailStore.Raw(db, "a", "INBOX", 20_000));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { dir.Delete(true); } catch (IOException) { /* Windows gibt die Datei verzoegert frei */ }
    }
}
