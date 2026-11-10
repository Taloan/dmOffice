using System.Reflection;
using DmOffice.Core;
using DmOffice.Mail;
using MailKit;
using MailStore = DmOffice.Mail.MailStore;
using MimeKit;

namespace DmOffice.Tests;

/// <summary>Speichermodus "Schlank": Aufraeumer, Suche ueber die Vorschau und Nachladen beim Oeffnen.</summary>
public class MailSlimTests
{
    private const string Filler = "Lorem ipsum dolor sit amet, consetetur sadipscing elitr, sed diam nonumy eirmod tempor invidunt ut labore. ";

    private static MimeMessage Message(string subject, byte[]? attachment = null)
    {
        var m = new MimeMessage();
        m.From.Add(new MailboxAddress("Anna", "anna@x.de"));
        m.To.Add(new MailboxAddress("Ich", "ich@x.de"));
        m.Subject = subject;
        // Vorschau: die ersten 200 Zeichen - "geheimwort" steht weit dahinter
        var text = new TextPart("plain") { Text = "Vorschauwort " + string.Concat(Enumerable.Repeat(Filler, 5)) + " geheimwort" };
        if (attachment == null) m.Body = text;
        else
        {
            var mixed = new Multipart("mixed") { text };
            mixed.Add(new MimePart("application", "octet-stream")
            {
                FileName = "daten.bin", Content = new MimeContent(new MemoryStream(attachment)), ContentTransferEncoding = ContentEncoding.Base64,
            });
            m.Body = mixed;
        }
        return m;
    }

    private static byte[] Bytes(MimeMessage m)
    {
        using var ms = new MemoryStream();
        m.WriteTo(ms);
        return ms.ToArray();
    }

    private static UserSpace NewSpace()
    {
        var dir = Directory.CreateTempSubdirectory("dmslim");
        var db = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "t");
        var space = new UserSpace(new UserRecord { Login = "t" }, db);
        space.UpdateSettings(s => s.MailAccounts.Add(new MailAccount { Id = "a", Email = "ich@x.de", Storage = MailAccount.StorageSlim }));
        MailStore.ReplaceFolders(db, "a", new() { ("INBOX", "INBOX", "/", "inbox", true) });
        return space;
    }

    /// <summary>Mail wie nach dem Sync: Kopf, dann der Inhalt; size wie vom Server gemeldet.</summary>
    private static void Add(UserDb db, uint uid, double daysAgo, long size, bool cached = true, byte[]? attachment = null)
    {
        db.Exec("INSERT INTO mail(account,folder,uid,subject,from_name,from_addr,date,size) VALUES('a','INBOX',$u,$s,'Anna','anna@x.de',$d,$sz)",
            ("$u", (long)uid), ("$s", "Mail " + uid), ("$d", DateTimeOffset.UtcNow.AddDays(-daysAgo).ToUnixTimeMilliseconds()), ("$sz", size));
        var id = Convert.ToInt64(db.Scalar("SELECT id FROM mail WHERE uid=$u", ("$u", (long)uid)));
        db.Exec("INSERT INTO mail_fts(rowid,subject,sender,recipients,body) VALUES($i,$s,'Anna anna@x.de','','')", ("$i", id), ("$s", "Mail " + uid));
        if (cached)
        {
            var m = Message("Mail " + uid, attachment);
            MailStore.StoreRaw(db, "a", "INBOX", uid, Bytes(m), m);
        }
    }

    private static bool Cached(UserDb db, uint uid) => MailStore.Row(db, "a", "INBOX", uid)!.Cached;

    [Fact]
    public void Aufraeumer_behaelt_Juengste_bis_zum_Limit_und_Geoeffnete()
    {
        var space = NewSpace();
        var db = space.Db;
        var slim = new MailStore.SlimLimit(DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeMilliseconds(), Budget: 1_000_000);
        for (uint d = 1; d <= 5; d++) Add(db, d, d, 200_000);   // 5 x 200 kB in den letzten Tagen
        Add(db, 10, 0.5, 300_000);                               // neu, aber groesser als ein Viertel des Limits
        Add(db, 20, 60, 200_000);                                // alt
        Add(db, 21, 90, 200_000);                                // alt, aber eben geoeffnet
        MailStore.Opened(db, "a", "INBOX", 21);

        var (count, bytes) = MailStore.Discard(db, "a", slim);

        // Geoeffnet zaehlt als juengst: 21, 1, 2, 3, 4 = 1.000.000 Bytes; 5 passt nicht mehr
        Assert.Equal(new[] { true, true, true, true, false }, new uint[] { 1, 2, 3, 4, 5 }.Select(u => Cached(db, u)).ToArray());
        Assert.True(Cached(db, 21));
        Assert.False(Cached(db, 10));
        Assert.False(Cached(db, 20));
        Assert.Equal(3, count);
        Assert.True(bytes > 0);
        Assert.Null(MailStore.Raw(db, "a", "INBOX", 20));
        Assert.Equal(3, Convert.ToInt32(db.Scalar("SELECT COUNT(*) FROM mail WHERE cached=0")));
        Assert.Equal(5, Convert.ToInt32(db.Scalar("SELECT COUNT(*) FROM mail_raw")));

        // Kopf, Flags und Vorschau bleiben
        var row = MailStore.Row(db, "a", "INBOX", 20)!;
        Assert.Equal("Mail 20", row.Subject);
        Assert.StartsWith("Vorschauwort", row.Preview);

        // Noch einmal: nichts mehr zu tun, und der Sync laedt das Verworfene nicht wieder (keine Schleife)
        Assert.Equal(0, MailStore.Discard(db, "a", slim).Count);
        Assert.Empty(MailStore.MissingBodies(db, "a", "INBOX", long.MaxValue, 100, slim));
        Assert.Equal(0, MailStore.MissingCount(db, "a", long.MaxValue, slim));
        // Vollstaendig: alles fehlende wird wieder geholt
        Assert.Equal(3, MailStore.MissingCount(db, "a", long.MaxValue));
    }

    [Fact]
    public void Sync_holt_bei_Schlank_nur_Inhalte_im_Rahmen()
    {
        var db = NewSpace().Db;
        var slim = new MailStore.SlimLimit(DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeMilliseconds(), Budget: 1_000_000);
        Add(db, 1, 1, 200_000, cached: false);
        Add(db, 2, 2, 200_000, cached: false);
        Add(db, 3, 45, 200_000, cached: false);
        Assert.Equal(new uint[] { 1, 2 }, MailStore.MissingBodies(db, "a", "INBOX", long.MaxValue, 100, slim).Select(x => x.Uid).ToArray());
        Assert.Equal(2, MailStore.MissingCount(db, "a", long.MaxValue, slim));
        Assert.Equal(new uint[] { 1, 2 }, MailStore.MissingBodies(db, "a", long.MaxValue, slim)["INBOX"].Select(x => x.Uid).ToArray());

        // Ohne Inhalt: die Vorschau kommt allein (fuer Liste und Suche); ohne Text ein Leerzeichen, damit sie nicht immer wieder geholt wird
        Assert.Equal(new uint[] { 1, 2, 3 }, MailStore.MissingPreviews(db, "a", "INBOX", 100).ToArray());
        MailStore.SetPreviews(db, "a", "INBOX", new[] { (3u, (string?)"Kurzer Text\r\nzweite Zeile"), (2u, (string?)null) });
        Assert.Equal("Kurzer Text zweite Zeile", MailStore.Row(db, "a", "INBOX", 3)!.Preview);
        Assert.Equal(new uint[] { 1 }, MailStore.MissingPreviews(db, "a", "INBOX", 100).ToArray());
        Assert.Equal(new uint[] { 3 }, MailStore.Search(db, "zweite", "a", null).Select(m => m.Uid).ToArray());
    }

    [Fact]
    public void Suche_findet_Verworfenes_ueber_Kopf_und_Vorschau()
    {
        var db = NewSpace().Db;
        Add(db, 1, 60, 1000);
        Assert.Single(MailStore.Search(db, "geheimwort", "a", null));

        MailStore.Discard(db, "a", new MailStore.SlimLimit(DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeMilliseconds(), 1_000_000));

        Assert.Single(MailStore.Search(db, "vorschauwort", "a", null));
        Assert.Single(MailStore.Search(db, "von:anna", "a", null));
        Assert.Single(MailStore.Search(db, "betreff:mail", "a", null));
        // Der Rest des Textes nur noch beim Anbieter
        Assert.Empty(MailStore.Search(db, "geheimwort", "a", null));
        Assert.Equal(new uint[] { 1 }, MailStore.NotCached(db, "a", "INBOX", new uint[] { 1, 99 }).Select(m => m.Uid).ToArray());
    }

    [Fact]
    public void Suche_beim_Anbieter_nur_mit_freien_Woertern()
    {
        Assert.Null(MailStore.ImapQuery("von:anna betreff:rechnung"));
        Assert.Null(MailStore.ImapQuery("  -  "));
        Assert.Equal("TEXT \"geheimwort\" FROM \"anna\"", Render(MailStore.ImapQuery("geheimwort von:anna")!));

        static string Render(MailKit.Search.SearchQuery q) => q is MailKit.Search.BinarySearchQuery b
            ? Render(b.Left) + " " + Render(b.Right)
            : q is MailKit.Search.TextSearchQuery t ? $"{(t.Term == MailKit.Search.SearchTerm.MessageContains ? "TEXT" : "FROM")} \"{t.Text}\"" : q.Term.ToString();
    }

    [Fact]
    public async Task Verworfenes_wird_beim_Oeffnen_nachgeladen_und_bleibt_dann()
    {
        var space = NewSpace();
        var db = space.Db;
        var account = space.Settings.MailAccounts[0];
        var slim = MailStore.SlimLimit.Of(account, DateTimeOffset.UtcNow)!;
        Add(db, 7, 120, 5_000);
        MailStore.Discard(db, "a", slim);
        Assert.False(Cached(db, 7));

        var folder = FakeFolder.Create("INBOX", uid => Message("Mail " + uid.Id));
        var m = await MailSync.Fetch(folder, space, account, new UniqueId(7), CancellationToken.None);
        Assert.Equal("Mail 7", m!.Subject);
        Assert.Equal(1, ((FakeFolder)(object)folder).Calls);
        Assert.True(Cached(db, 7));
        Assert.Contains("geheimwort", MailStore.BodyText(MimeMessage.Load(new MemoryStream(MailStore.Raw(db, "a", "INBOX", 7)!))));

        // Wie im Lesebereich: als geoeffnet merken - dann verwirft der Aufraeumer es nicht gleich wieder
        MailStore.Opened(db, "a", "INBOX", 7);
        Assert.Equal(0, MailStore.Discard(db, "a", slim).Count);
        // Zweites Oeffnen aus der Datenbank, nicht vom Server
        await MailSync.Fetch(folder, space, account, new UniqueId(7), CancellationToken.None);
        Assert.Equal(1, ((FakeFolder)(object)folder).Calls);
        // Die Suche findet nach dem Nachladen wieder den ganzen Text
        Assert.Single(MailStore.Search(db, "geheimwort", "a", null));
    }

    [Fact]
    public void Platz_wird_in_der_Datei_wieder_frei()
    {
        var space = NewSpace();
        var db = space.Db;
        // Einmal verdichten stellt auf auto_vacuum=INCREMENTAL um
        Assert.False(db.Incremental());
        db.Compact();
        Assert.True(db.Incremental());

        var random = new Random(1);
        for (uint u = 1; u <= 20; u++)
        {
            var data = new byte[300_000];
            random.NextBytes(data);
            Add(db, u, 100 + u, 400_000, attachment: data);
        }
        db.Checkpoint();
        var before = db.SpaceInfo();

        var (count, bytes) = MailCleanup.Slim(space, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        Assert.Equal(20, count);
        Assert.True(bytes > 20 * 300_000L, $"verworfen: {bytes}");
        var after = db.SpaceInfo();
        // Die Anhaenge (zufaellig, also kaum komprimierbar) sind aus der Datei heraus
        Assert.True(after.File + after.Wal < before.File + before.Wal - 5_000_000, $"{before} -> {after}");
        Assert.True(after.Free < 1_000_000, $"frei: {after.Free}");
    }
}

/// <summary>IMAP-Ordner zum Testen: nur FullName und GetMessageAsync, mitgezaehlt.</summary>
public class FakeFolder : DispatchProxy
{
    public string Name = "";
    public Func<UniqueId, MimeMessage> Get = _ => throw new MessageNotFoundException("");
    public int Calls;

    public static IMailFolder Create(string name, Func<UniqueId, MimeMessage> get)
    {
        var f = Create<IMailFolder, FakeFolder>();
        var fake = (FakeFolder)(object)f;
        fake.Name = name;
        fake.Get = get;
        return f;
    }

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        switch (method!.Name)
        {
            case "get_FullName": return Name;
            case "GetMessageAsync" when args![0] is UniqueId uid:
                Calls++;
                return Task.FromResult(Get(uid));
            default: throw new NotSupportedException(method.Name);
        }
    }
}
