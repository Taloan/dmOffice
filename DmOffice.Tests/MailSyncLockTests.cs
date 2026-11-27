using System.Diagnostics;
using DmOffice.Core;
using DmOffice.Mail;
using MailKit;
using MailStore = DmOffice.Mail.MailStore;

namespace DmOffice.Tests;

/// <summary>
/// Viele neue Mails einlesen, waehrend andere schreiben (Oberflaeche, DMS): der Abgleich darf die Benutzer-Datenbank
/// nicht so lange am Stueck belegen, dass andere Schreiber in "database is locked" laufen.
/// </summary>
public class MailSyncLockTests
{
    private static (UserDb Db, DirectoryInfo Dir) NewDb()
    {
        var dir = Directory.CreateTempSubdirectory("dmlock");
        var db = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "t");
        MailStore.ReplaceFolders(db, "a", new() { ("INBOX", "INBOX", "/", "inbox", true), ("Archive", "Archive", "/", null, true) });
        return (db, dir);
    }

    private static void Drop(DirectoryInfo dir)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { dir.Delete(true); } catch (IOException) { /* Windows gibt die Datei verzoegert frei */ }
    }

    private static List<IMessageSummary> Summaries(int from, int count) => Enumerable.Range(from, count).Select(i =>
    {
        var env = new Envelope { Subject = $"Betreff {i}", MessageId = $"m{i}@x", Date = DateTimeOffset.UtcNow.AddMinutes(-i) };
        env.From.Add(new MimeKit.MailboxAddress("Absender", $"a{i % 50}@x.de"));
        return (IMessageSummary)new MessageSummary(i) { UniqueId = new UniqueId((uint)i), Envelope = env, Flags = MessageFlags.None, Size = 1000 };
    }).ToList();

    private static void InsertMany(UserDb db, int n)
    {
        foreach (var batch in Enumerable.Range(1, n).Chunk(250)) MailStore.Insert(db, "a", "Archive", Summaries(batch[0], batch.Length));
    }

    /// <summary>Schreibt laufend etwas (wie Oberflaeche und DMS) und merkt sich die laengste Wartezeit.</summary>
    private sealed class OtherWriter
    {
        private volatile bool _stop;
        private readonly Task _task;
        public long MaxWaitMs;
        public int Failed, Written;
        public OtherWriter(UserDb db) => _task = Task.Run(() =>
        {
            while (!_stop)
            {
                var t = Stopwatch.StartNew();
                try { db.Set("other", Written.ToString()); Written++; } catch { Failed++; }
                MaxWaitMs = Math.Max(MaxWaitMs, t.ElapsedMilliseconds);
                Thread.Sleep(30);
            }
        });
        public void Stop() { _stop = true; _task.Wait(); }
    }

    [Fact]
    public async Task Flags_vieler_Mails_lassen_andere_Schreiber_durch()
    {
        var (db, dir) = NewDb();
        const int n = 20_000;
        InsertMany(db, n);

        // Unveraendert: nichts zu schreiben
        Assert.Equal(0, await MailStore.ApplyFlagsAsync(db, "a", "Archive",
            Enumerable.Range(1, n).Select(i => ((uint)i, MessageFlags.None, (IEnumerable<string>?)Array.Empty<string>()))));

        var other = new OtherWriter(db);
        var changed = await MailStore.ApplyFlagsAsync(db, "a", "Archive",
            Enumerable.Range(1, n).Select(i => ((uint)i, MessageFlags.Seen | (i % 100 == 0 ? MessageFlags.Flagged : MessageFlags.None),
                (IEnumerable<string>?)(i % 1000 == 0 ? new[] { "$label1", "$Forwarded" } : Array.Empty<string>()))));
        other.Stop();
        Assert.Equal(n, changed);
        Assert.Equal(0, other.Failed);
        Assert.True(other.Written > 0);
        Assert.True(other.MaxWaitMs < 3000, $"laengste Wartezeit {other.MaxWaitMs} ms");

        Assert.Equal(0L, db.Scalar("SELECT COUNT(*) FROM mail WHERE folder='Archive' AND seen=0"));
        Assert.Equal(n / 100L, db.Scalar("SELECT COUNT(*) FROM mail WHERE folder='Archive' AND flagged=1"));
        Assert.Equal(" $label1 ", db.Scalar("SELECT keywords FROM mail WHERE folder='Archive' AND uid=1000"));
        // Ohne Schlagworte (Microsoft mit Graph): vorhandene bleiben, Flags kommen trotzdem
        Assert.Equal(1, await MailStore.ApplyFlagsAsync(db, "a", "Archive", new[] { (1000u, MessageFlags.None, (IEnumerable<string>?)null) }));
        Assert.Equal(" $label1 ", db.Scalar("SELECT keywords FROM mail WHERE folder='Archive' AND uid=1000"));
        Assert.Equal(0L, db.Scalar("SELECT seen FROM mail WHERE folder='Archive' AND uid=1000"));
        // Unbekannte UIDs werden uebergangen
        Assert.Equal(0, await MailStore.ApplyFlagsAsync(db, "a", "Archive", new[] { (999_999u, MessageFlags.Seen, (IEnumerable<string>?)null) }));
        Drop(dir);
    }

    [Fact]
    public void Inhalte_vieler_Mails_ablegen_laesst_andere_Schreiber_durch()
    {
        var (db, dir) = NewDb();
        InsertMany(db, 600);
        var other = new OtherWriter(db);
        // Wie DownloadBodies: Portionen zu 20, dazwischen die Pause
        foreach (var batch in Enumerable.Range(1, 600).Chunk(20))
        {
            foreach (var uid in batch)
            {
                var m = new MimeKit.MimeMessage { Subject = $"Betreff {uid}" };
                m.From.Add(new MimeKit.MailboxAddress("A", "a@x.de"));
                m.Body = new MimeKit.TextPart("plain") { Text = string.Join(" ", Enumerable.Range(0, 500).Select(w => $"wort{(w * uid) % 5000}")) };
                using var ms = new MemoryStream();
                m.WriteTo(ms);
                MailStore.StoreRaw(db, "a", "Archive", (uint)uid, ms.ToArray(), m);
            }
            Thread.Sleep(UserDb.Breather);
        }
        other.Stop();
        Assert.Equal(0, other.Failed);
        Assert.True(other.MaxWaitMs < 3000, $"laengste Wartezeit {other.MaxWaitMs} ms");
        Assert.Equal(600L, db.Scalar("SELECT COUNT(*) FROM mail WHERE folder='Archive' AND cached=1"));
        Assert.Equal(600L, db.Scalar("SELECT COUNT(*) FROM mail_raw"));
        Assert.Contains(MailStore.Search(db, "wort499", "a", null), r => r.Uid == 1);
        Drop(dir);
    }

    [Fact]
    public void Checkpoint_haelt_Schreiber_nicht_auf_wenn_ein_Leser_offen_ist()
    {
        var (db, dir) = NewDb();
        db.Set("x", "1");
        using (var reader = db.Open())
        using (var rtx = reader.BeginTransaction(deferred: true))
        {
            // Offener Schnappschuss eines Lesers, danach etwas im Journal, das er nicht sieht
            UserDb.Query(reader, "SELECT COUNT(*) FROM kv", r => r.GetInt64(0));
            db.Set("y", "2");
            var cp = Task.Run(db.Checkpoint);
            Thread.Sleep(100);
            var t = Stopwatch.StartNew();
            db.Set("z", "3");
            Assert.True(t.ElapsedMilliseconds < 3000, $"Schreiber wartete {t.ElapsedMilliseconds} ms");
            cp.Wait();
            rtx.Commit();
        }
        Assert.Equal("3", db.Get("z"));
        Drop(dir);
    }

    [Fact]
    public void Viele_entfernte_Mails_in_Portionen()
    {
        var (db, dir) = NewDb();
        InsertMany(db, 1200);
        MailStore.Delete(db, "a", "Archive", Enumerable.Range(1, 1100).Select(i => (uint)i));
        Assert.Equal(100L, db.Scalar("SELECT COUNT(*) FROM mail WHERE folder='Archive'"));
        Assert.Equal(100L, db.Scalar("SELECT COUNT(*) FROM mail_fts"));
        Drop(dir);
    }
}
