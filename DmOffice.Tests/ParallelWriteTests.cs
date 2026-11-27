using System.Diagnostics;
using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Pim;

namespace DmOffice.Tests;

/// <summary>Gleichzeitiges Schreiben in dieselbe Benutzerdatenbank darf nicht mit "database is locked" scheitern.</summary>
public class ParallelWriteTests
{
    private static UserDb NewDb()
    {
        var dir = Directory.CreateTempSubdirectory("dmpar");
        var db = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "t");
        PimStore.EnsureDefaults(db);
        return db;
    }

    private static string Ev(string uid) =>
        $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VEVENT\r\nUID:{uid}\r\nDTSTART:20270105T100000Z\r\nDTEND:20270105T110000Z\r\nSUMMARY:{uid} {new string('x', 2000)}\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

    private static string Card(string uid) =>
        $"BEGIN:VCARD\r\nVERSION:3.0\r\nUID:{uid}\r\nFN:{uid}\r\nN:{uid};;;;\r\nEMAIL:{uid}@example.org\r\nEND:VCARD\r\n";

    /// <summary>Dauerhaft lesende Anfragen (Listen) und ein Checkpoint wie nach dem Mail-Abgleich - wie auf dem Server.</summary>
    private static (Task[] Tasks, CancellationTokenSource Stop) Background(UserDb db)
    {
        var stop = new CancellationTokenSource();
        var readers = Enumerable.Range(0, 8).Select(_ => Task.Factory.StartNew(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                using var c = db.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT data FROM objects";
                using var r = cmd.ExecuteReader();
                r.Read();
                Thread.Sleep(300);   // langsamer Leser: haelt seinen Stand fest
                while (r.Read()) { }
            }
        }, TaskCreationOptions.LongRunning));
        var checkpoint = Task.Factory.StartNew(() =>
        {
            while (!stop.IsCancellationRequested) { db.Checkpoint(); Thread.Sleep(150); }
        }, TaskCreationOptions.LongRunning);
        return (readers.Append(checkpoint).ToArray(), stop);
    }

    [Fact]
    public void Acht_parallele_Speicher_von_Terminen_und_Kontakten_ohne_Fehler()
    {
        var db = NewDb();
        var cal = PimStore.Collections(db, PimStore.Calendar).First();
        var book = PimStore.Collections(db, PimStore.AddressBook).First();
        for (int i = 0; i < 50; i++) PimStore.Put(db, cal, $"s{i}.ics", Ev($"s{i}"));
        var (background, stop) = Background(db);

        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
        const int perWorker = 40;
        var workers = Enumerable.Range(0, 8).Select(w => Task.Factory.StartNew(() =>
        {
            for (int i = 0; i < perWorker; i++)
            {
                try
                {
                    if ((w + i) % 2 == 0) PimStore.Put(db, cal, $"e{w}-{i}.ics", Ev($"e{w}-{i}"));
                    else PimStore.Put(db, book, $"k{w}-{i}.vcf", Card($"k{w}-{i}"));
                }
                catch (Exception e) { errors.Add(e.Message); }
            }
        }, TaskCreationOptions.LongRunning)).ToArray();
        Task.WaitAll(workers);
        stop.Cancel();
        Task.WaitAll(background);

        Assert.Empty(errors);
        Assert.Equal(50 + 8 * perWorker, PimStore.Objects(db, cal.Id).Count + PimStore.Objects(db, book.Id).Count);
    }

    [Fact]
    public void Checkpoint_wartet_nicht_auf_Leser()
    {
        var db = NewDb();
        var cal = PimStore.Collections(db, PimStore.Calendar).First();
        for (int i = 0; i < 20; i++) PimStore.Put(db, cal, $"s{i}.ics", Ev($"s{i}"));
        using var reader = db.Open();
        using var tx = reader.BeginTransaction(deferred: true);
        UserDb.Query(reader, "SELECT count(*) FROM objects", r => r.GetInt64(0));
        PimStore.Put(db, cal, "neu.ics", Ev("neu"));   // der Leser steht jetzt auf einem alten Stand

        var sw = Stopwatch.StartNew();
        db.Checkpoint();
        Assert.True(sw.ElapsedMilliseconds < 2000, $"Checkpoint dauerte {sw.ElapsedMilliseconds} ms");
        // Schreiben geht weiter, obwohl der Leser offen ist
        PimStore.Put(db, cal, "danach.ics", Ev("danach"));
    }

    [Fact]
    public void Paralleles_Hochladen_in_denselben_neuen_Ordner_verliert_nichts()
    {
        var db = NewDb();
        for (int round = 0; round < 5; round++)
        {
            var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
            var start = new Barrier(12);
            // Wie /api/files/upload?path=/Masse/OrdnerNN: jede Anfrage legt fehlende Ordner selbst an
            var jobs = Enumerable.Range(0, 12).Select(i => Task.Factory.StartNew(() =>
            {
                var dir = $"Masse{round}/Ordner/Tief";
                start.SignalAndWait();
                try
                {
                    FileStore.EnsureDir(db, dir);
                    FileStore.PutAsync(db, $"{dir}/d{i}.txt", new MemoryStream(System.Text.Encoding.UTF8.GetBytes("Datei " + i)), "text/plain").GetAwaiter().GetResult();
                }
                catch (Exception e) { errors.Add(e.Message); }
            }, TaskCreationOptions.LongRunning)).ToArray();
            Task.WaitAll(jobs);

            Assert.Empty(errors);
            for (int i = 0; i < 12; i++) Assert.NotNull(FileStore.Get(db, $"Masse{round}/Ordner/Tief/d{i}.txt"));
        }
    }
}
