using System.Diagnostics;
using DmOffice.Core;
using DmOffice.Mail;
using MailKit;
using MailStore = DmOffice.Mail.MailStore;
using MimeKit;

namespace DmOffice.Tests;

/// <summary>Kennung mailprefetch: neue Mails kommen samt Inhalt in die Datenbank, bevor sie gemeldet werden.</summary>
public class MailPrefetchTests
{
    private static MimeMessage Message(uint uid, int textBytes = 2_000)
    {
        var m = new MimeMessage();
        m.From.Add(new MailboxAddress("Anna", "anna@x.de"));
        m.To.Add(new MailboxAddress("Ich", "ich@x.de"));
        m.Subject = "Mail " + uid;
        m.MessageId = $"m{uid}@x.de";
        m.Body = new BodyBuilder { HtmlBody = "<p>Hallo " + uid + "</p>" + new string('x', textBytes), TextBody = "Hallo " + uid }.ToMessageBody();
        return m;
    }

    private static byte[] Bytes(MimeMessage m)
    {
        using var ms = new MemoryStream();
        m.WriteTo(ms);
        return ms.ToArray();
    }

    private static IMessageSummary Summary(uint uid, long size)
    {
        var env = new Envelope { Subject = "Mail " + uid, MessageId = $"m{uid}@x.de", Date = DateTimeOffset.UtcNow };
        env.From.Add(new MailboxAddress("Anna", "anna@x.de"));
        return new MessageSummary((int)uid) { UniqueId = new UniqueId(uid), Envelope = env, Flags = MessageFlags.None, Size = (uint)size };
    }

    private static UserSpace NewSpace(string storage = MailAccount.StorageFull)
    {
        var dir = Directory.CreateTempSubdirectory("dmprefetch");
        var db = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "t");
        var space = new UserSpace(new UserRecord { Login = "t" }, db);
        // Ohne IMAP-Server: jeder Abruf beim Anbieter scheiterte (ImapPool.RequireServer)
        space.UpdateSettings(s => s.MailAccounts.Add(new MailAccount { Id = "a", Email = "ich@x.de", Storage = storage }));
        MailStore.ReplaceFolders(db, "a", new() { ("INBOX", "INBOX", "/", "inbox", true) });
        return space;
    }

    private static FakeFolder Fake(IMailFolder f) => (FakeFolder)(object)f;

    [Fact]
    public async Task Neue_Mail_ist_nach_dem_Abgleich_ohne_Abruf_beim_Anbieter_lesbar()
    {
        var space = NewSpace();
        var account = space.Settings.MailAccounts[0];
        var folder = FakeFolder.Create("INBOX", uid => Message(uid.Id));

        var loaded = await MailSync.InsertPrefetched(folder, space, account, "INBOX", [Summary(5, 3_000), Summary(4, 3_000)],
            null, DateTime.UtcNow.AddMinutes(1), null, CancellationToken.None);

        Assert.Equal(2, loaded);
        Assert.Equal(2, Fake(folder).Calls);
        Assert.True(MailStore.Row(space.Db, "a", "INBOX", 5)!.Cached);
        Assert.Contains("Hallo 5", MailStore.Row(space.Db, "a", "INBOX", 5)!.Preview);
        // Öffnen wie im Lesebereich: aus der Datenbank, der Pool (ohne Server) wird nicht gebraucht
        using var pool = new ImapPool();
        var m = await MailApi.Load(space, account, "INBOX", 5, pool, CancellationToken.None);
        Assert.Equal("Mail 5", m.Subject);
        Assert.Equal(2, Fake(folder).Calls);
    }

    [Fact]
    public async Task Grosse_Mails_kommen_ohne_Inhalt_und_werden_nicht_abgewartet()
    {
        var space = NewSpace();
        var account = space.Settings.MailAccounts[0];
        var folder = FakeFolder.Create("INBOX", uid => Message(uid.Id));

        var loaded = await MailSync.InsertPrefetched(folder, space, account, "INBOX", [Summary(9, MailSync.PrefetchMaxBytes + 1)],
            null, DateTime.UtcNow.AddMinutes(1), null, CancellationToken.None);

        Assert.Equal(0, loaded);
        Assert.Equal(0, Fake(folder).Calls);
        var row = MailStore.Row(space.Db, "a", "INBOX", 9)!;
        Assert.False(row.Cached);
        // Kommt wie bisher im Hintergrund nach
        Assert.Contains(MailStore.MissingBodies(space.Db, "a", "INBOX", account.MaxCachedMb * 1024L * 1024L, 10), x => x.Uid == 9);
    }

    [Fact]
    public async Task Nach_Ablauf_der_Zeit_nur_noch_Kopfzeilen()
    {
        var space = NewSpace();
        var account = space.Settings.MailAccounts[0];
        var folder = FakeFolder.Create("INBOX", uid => Message(uid.Id));

        var loaded = await MailSync.InsertPrefetched(folder, space, account, "INBOX", [Summary(3, 3_000)],
            null, DateTime.UtcNow.AddSeconds(-1), null, CancellationToken.None);

        Assert.Equal(0, loaded);
        Assert.Equal(0, Fake(folder).Calls);
        Assert.False(MailStore.Row(space.Db, "a", "INBOX", 3)!.Cached);
    }

    [Fact]
    public async Task Inzwischen_verschwundene_Mail_wird_nicht_eingetragen()
    {
        var space = NewSpace();
        var account = space.Settings.MailAccounts[0];
        var folder = FakeFolder.Create("INBOX", uid => uid.Id == 2 ? throw new MessageNotFoundException("weg") : Message(uid.Id));

        await MailSync.InsertPrefetched(folder, space, account, "INBOX", [Summary(2, 3_000), Summary(1, 3_000)],
            null, DateTime.UtcNow.AddMinutes(1), null, CancellationToken.None);

        Assert.Null(MailStore.Row(space.Db, "a", "INBOX", 2));
        Assert.True(MailStore.Row(space.Db, "a", "INBOX", 1)!.Cached);
    }

    [Fact]
    public async Task Unlesbare_Mail_haelt_die_anderen_nicht_auf()
    {
        var space = NewSpace();
        var account = space.Settings.MailAccounts[0];
        var folder = FakeFolder.Create("INBOX", uid => uid.Id == 2 ? throw new FormatException("kaputt") : Message(uid.Id));

        var loaded = await MailSync.InsertPrefetched(folder, space, account, "INBOX", [Summary(2, 3_000), Summary(1, 3_000)],
            null, DateTime.UtcNow.AddMinutes(1), null, CancellationToken.None);

        Assert.Equal(1, loaded);
        Assert.False(MailStore.Row(space.Db, "a", "INBOX", 2)!.Cached);
        Assert.True(MailStore.Row(space.Db, "a", "INBOX", 1)!.Cached);
    }

    [Fact]
    public async Task Schon_vorliegender_Inhalt_wird_nicht_erneut_geholt()
    {
        var space = NewSpace();
        var account = space.Settings.MailAccounts[0];
        var folder = FakeFolder.Create("INBOX", uid => Message(uid.Id));
        // Gerade gesendet (Gesendet) bzw. in der Junk-Nachprüfung geholt
        SentCache.Remember(space.User.Id, "a", "INBOX", Message(7), 10_000_000);
        var junk = Message(8);

        var loaded = await MailSync.InsertPrefetched(folder, space, account, "INBOX", [Summary(7, 3_000), Summary(8, 3_000)],
            uid => uid == 8 ? (Bytes(junk), junk) : null, DateTime.UtcNow.AddMinutes(1), null, CancellationToken.None);

        Assert.Equal(2, loaded);
        Assert.Equal(0, Fake(folder).Calls);
        Assert.True(MailStore.Row(space.Db, "a", "INBOX", 7)!.Cached);
        Assert.True(MailStore.Row(space.Db, "a", "INBOX", 8)!.Cached);
    }

    [Fact]
    public void Grenze_je_Mail_richtet_sich_nach_Konto_und_Schlank()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(MailSync.PrefetchMaxBytes, MailSync.PrefetchLimit(new MailAccount { MaxCachedMb = 50 }, now));
        Assert.Equal(2L * 1024 * 1024, MailSync.PrefetchLimit(new MailAccount { MaxCachedMb = 2 }, now));
        Assert.Equal(0, MailSync.PrefetchLimit(new MailAccount { MaxCachedMb = 0 }, now));
        var slim = new MailAccount { Storage = MailAccount.StorageSlim, SlimMb = 8 };
        Assert.Equal(MailStore.SlimLimit.Of(slim, now)!.Big, MailSync.PrefetchLimit(slim, now));
    }

    /// <summary>Messung: Öffnen einer vorgeladenen Mail (Datenbank, Aufbereitung) gegenüber dem Abruf beim Anbieter.</summary>
    [Fact]
    public async Task Oeffnen_aus_der_Datenbank_braucht_nur_Millisekunden()
    {
        var space = NewSpace();
        var account = space.Settings.MailAccounts[0];
        var folder = FakeFolder.Create("INBOX", uid => Message(uid.Id, 200_000));
        await MailSync.InsertPrefetched(folder, space, account, "INBOX", [Summary(1, 250_000)], null, DateTime.UtcNow.AddMinutes(1), null, CancellationToken.None);
        using var pool = new ImapPool();
        await MailApi.Load(space, account, "INBOX", 1, pool, CancellationToken.None);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 10; i++) await MailApi.Load(space, account, "INBOX", 1, pool, CancellationToken.None);
        Assert.True(sw.ElapsedMilliseconds < 2_000, $"10× öffnen: {sw.ElapsedMilliseconds} ms");
    }
}
