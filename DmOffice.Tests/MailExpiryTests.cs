using DmOffice.Core;
using DmOffice.Mail;
using MimeKit;
using MailStore = DmOffice.Mail.MailStore;

namespace DmOffice.Tests;

/// <summary>Entfernen nach Zeit: Planen, Zuruecknehmen, Papierkorb und Vernichten gegen einen nachgebauten IMAP-Server,
/// verschobene Mail wiederfinden, EXPUNGE ohne UIDPLUS, Regel-Aktionen.</summary>
public class MailExpiryTests
{
    /// <summary>Nachgebauter Server: Ordner mit Mails (UID, Message-ID, \Deleted).</summary>
    private sealed class FakeServer : MailExpiry.IServer
    {
        public sealed class Msg { public uint Uid; public string? Mid; public bool Deleted; }
        public readonly Dictionary<string, List<Msg>> Folders = new();
        private uint _next = 100;
        public bool UidPlus { get; set; } = true;
        public int Expunges;

        public List<Msg> In(string f) => Folders.TryGetValue(f, out var l) ? l : Folders[f] = new();
        public uint Add(string folder, string? mid, uint? uid = null, bool deleted = false)
        {
            var u = uid ?? _next++;
            In(folder).Add(new Msg { Uid = u, Mid = mid, Deleted = deleted });
            return u;
        }

        public Task<List<uint>> FindAsync(string folder, uint uid, string? messageId, CancellationToken ct) =>
            Task.FromResult(In(folder).Where(m => messageId != null ? m.Mid == messageId : uid != 0 && m.Uid == uid).Select(m => m.Uid).ToList());

        public Task<List<uint>> MoveAsync(string folder, IList<uint> uids, string target, CancellationToken ct)
        {
            var moved = In(folder).Where(m => uids.Contains(m.Uid)).ToList();
            In(folder).RemoveAll(moved.Contains);
            return Task.FromResult(moved.Select(m => Add(target, m.Mid)).ToList());
        }

        public Task<List<uint>> DeletedAsync(string folder, CancellationToken ct) =>
            Task.FromResult(In(folder).Where(m => m.Deleted).Select(m => m.Uid).ToList());

        public Task SetDeletedAsync(string folder, IList<uint> uids, bool on, CancellationToken ct)
        {
            foreach (var m in In(folder).Where(m => uids.Contains(m.Uid))) m.Deleted = on;
            return Task.CompletedTask;
        }

        public Task ExpungeAsync(string folder, IList<uint>? uids, CancellationToken ct)
        {
            if (uids != null && !UidPlus) throw new InvalidOperationException("UID EXPUNGE ohne UIDPLUS");
            Expunges++;
            In(folder).RemoveAll(m => m.Deleted && (uids == null || uids.Contains(m.Uid)));
            return Task.CompletedTask;
        }
    }

    private static UserSpace NewSpace(bool trash = true)
    {
        var dir = Directory.CreateTempSubdirectory("dmexpiry");
        var db = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "t");
        var space = new UserSpace(new UserRecord { Login = "t" }, db);
        space.UpdateSettings(s => s.MailAccounts.Add(new MailAccount { Id = "a", Email = "ich@x.de", ImapHost = "imap.x.de" }));
        var folders = new List<(string, string, string, string?, bool)> { ("INBOX", "INBOX", "/", "inbox", true), ("Archiv", "Archiv", "/", "archive", true) };
        if (trash) folders.Add(("Papierkorb", "Papierkorb", "/", "trash", true));
        MailStore.ReplaceFolders(db, "a", folders);
        return space;
    }

    /// <summary>Mail wie nach dem Sync in der Datenbank (mit Inhalt und Suchindex).</summary>
    private static void Row(UserDb db, string folder, uint uid, string? mid, string subject = "Geheim")
    {
        db.Exec("INSERT INTO mail(account,folder,uid,message_id,subject,from_name,from_addr,date,size) VALUES('a',$f,$u,$m,$s,'Anna','anna@x.de',$d,100)",
            ("$f", folder), ("$u", (long)uid), ("$m", mid), ("$s", subject), ("$d", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        var id = Convert.ToInt64(db.Scalar("SELECT id FROM mail WHERE folder=$f AND uid=$u", ("$f", folder), ("$u", (long)uid)));
        db.Exec("INSERT INTO mail_fts(rowid,subject,sender,recipients,body) VALUES($i,$s,'Anna anna@x.de','','')", ("$i", id), ("$s", subject));
        var m = new MimeMessage { Subject = subject, Body = new TextPart("plain") { Text = "Streng geheimer Inhalt" } };
        using var ms = new MemoryStream();
        m.WriteTo(ms);
        MailStore.StoreRaw(db, "a", folder, uid, ms.ToArray(), m);
    }

    private static Task<int> Run(FakeServer server, UserSpace space, HashSet<string>? touched = null) =>
        MailExpiry.RunAsync(server, space, space.Settings.MailAccounts[0], e => e.At <= DateTime.UtcNow, touched ?? [], default);

    /// <summary>Faellig machen, ohne zu warten.</summary>
    private static void Due(UserDb db)
    {
        var list = MailExpiry.All(db);
        foreach (var e in list) e.At = DateTime.UtcNow.AddMinutes(-1);
        db.SetJson("mail.expiries", list);
    }

    [Fact]
    public async Task Planen_und_Zuruecknehmen()
    {
        var space = NewSpace();
        Row(space.Db, "INBOX", 1, "m1@x");
        Row(space.Db, "INBOX", 2, "m2@x");

        Assert.Equal(2, await MailExpiry.PlanAsync(space.Db, "a", "INBOX", [1, 2], DateTime.UtcNow.AddDays(7), MailExpiry.Trash));
        // Neuer Zeitpunkt derselben Art ersetzt, die andere Art kommt dazu
        await MailExpiry.PlanAsync(space.Db, "a", "INBOX", [1], DateTime.UtcNow.AddDays(1), MailExpiry.Trash);
        await MailExpiry.PlanAsync(space.Db, "a", "INBOX", [1], DateTime.UtcNow.AddDays(30), MailExpiry.Destroy);
        var all = MailExpiry.All(space.Db);
        Assert.Equal(3, all.Count);
        Assert.Single(all, e => e.MessageId == "m1@x" && e.Kind == MailExpiry.Trash && e.At < DateTime.UtcNow.AddDays(2));
        Assert.Equal("Geheim", all[0].Subject);

        // Nicht faellig: nichts passiert
        var server = new FakeServer();
        server.Add("INBOX", "m1@x", 1);
        Assert.Equal(0, await Run(server, space));
        Assert.Single(server.In("INBOX"));

        Assert.Equal(2, await MailExpiry.CancelAsync(space.Db, "a", "INBOX", [1]));
        Assert.Single(MailExpiry.All(space.Db), e => e.MessageId == "m2@x");
    }

    [Fact]
    public async Task Papierkorb_verschiebt_und_bleibt_wiederherstellbar()
    {
        var space = NewSpace();
        Row(space.Db, "INBOX", 1, "m1@x");
        var server = new FakeServer();
        server.Add("INBOX", "m1@x", 1);
        await MailExpiry.PlanAsync(space.Db, "a", "INBOX", [1], DateTime.UtcNow.AddDays(1), MailExpiry.Trash);
        Due(space.Db);

        var touched = new HashSet<string>();
        Assert.Equal(1, await Run(server, space, touched));
        Assert.Empty(server.In("INBOX"));
        Assert.Single(server.In("Papierkorb"), m => m.Mid == "m1@x" && !m.Deleted);
        Assert.Contains("Papierkorb", touched);
        Assert.Null(MailStore.Row(space.Db, "a", "INBOX", 1));
        Assert.Empty(MailExpiry.All(space.Db));
    }

    [Fact]
    public async Task Vernichten_tilgt_auf_dem_Server_und_in_dmOffice()
    {
        var space = NewSpace();
        var db = space.Db;
        Row(db, "INBOX", 1, "m1@x");
        db.Exec("INSERT INTO text_docs(kind,account,msgid,part,name,method,status,indexed) VALUES('mail','a','m1@x',0,'rechnung.pdf','text','ok',0)");
        MailStore.SetFollowUpDue(db, "a", "INBOX", [1], "2026-11-01");
        var server = new FakeServer();
        server.Add("INBOX", "m1@x", 1);
        server.Add("INBOX", "andere@x", 2);
        await MailExpiry.PlanAsync(db, "a", "INBOX", [1], DateTime.UtcNow.AddDays(1), MailExpiry.Destroy);
        Due(db);

        Assert.Equal(1, await Run(server, space));
        Assert.Single(server.In("INBOX"), m => m.Mid == "andere@x");
        Assert.Empty(server.In("Papierkorb"));
        Assert.Null(MailStore.Row(db, "a", "INBOX", 1));
        Assert.Empty(MailStore.Search(db, "Geheim", "a", null));
        Assert.Equal(0L, Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM text_docs WHERE msgid='m1@x'")));
        Assert.Equal(0L, Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM mail_followup WHERE message_id='m1@x'")));
        Assert.Equal(0L, Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM mail_raw")));
        Assert.Empty(MailExpiry.All(db));
    }

    [Fact]
    public async Task Verschobene_Mail_wird_ueber_die_Message_ID_gefunden()
    {
        var space = NewSpace();
        Row(space.Db, "INBOX", 1, "m1@x");
        await MailExpiry.PlanAsync(space.Db, "a", "INBOX", [1], DateTime.UtcNow.AddDays(1), MailExpiry.Destroy);
        // Inzwischen ins Archiv verschoben: neue UID, der Abgleich kennt sie dort
        space.Db.Exec("DELETE FROM mail");
        Row(space.Db, "Archiv", 57, "m1@x");
        var server = new FakeServer();
        server.Add("INBOX", "fremd@x", 1);   // dieselbe UID gehoert jetzt einer anderen Mail
        server.Add("Archiv", "m1@x", 57);
        Due(space.Db);

        Assert.Equal(1, await Run(server, space));
        Assert.Single(server.In("INBOX"));
        Assert.Empty(server.In("Archiv"));
        Assert.Null(MailStore.Row(space.Db, "a", "Archiv", 57));
    }

    [Fact]
    public async Task Ohne_UIDPLUS_wird_nichts_anderes_mitgetilgt()
    {
        var server = new FakeServer { UidPlus = false };
        server.Add("INBOX", "meine@x", 1);
        server.Add("INBOX", "fremd-markiert@x", 2, deleted: true);
        server.Add("INBOX", "normal@x", 3);

        await MailExpiry.ExpungeOnlyAsync(server, "INBOX", [1], default);

        Assert.Equal(1, server.Expunges);
        Assert.DoesNotContain(server.In("INBOX"), m => m.Uid == 1);
        // Die fremde Markierung ist wieder gesetzt, die Mail noch da
        Assert.Single(server.In("INBOX"), m => m.Uid == 2 && m.Deleted);
        Assert.Single(server.In("INBOX"), m => m.Uid == 3 && !m.Deleted);
    }

    [Fact]
    public async Task Nicht_mehr_da_wird_nach_einer_Weile_vergessen()
    {
        var space = NewSpace();
        Row(space.Db, "INBOX", 1, "m1@x");
        await MailExpiry.PlanAsync(space.Db, "a", "INBOX", [1], DateTime.UtcNow.AddDays(1), MailExpiry.Trash);
        Due(space.Db);
        var server = new FakeServer();
        Assert.Equal(0, await Run(server, space));
        Assert.Single(MailExpiry.All(space.Db));          // vielleicht nur noch nicht abgeglichen

        var list = MailExpiry.All(space.Db);
        list[0].At = DateTime.UtcNow - MailExpiry.GiveUp - TimeSpan.FromHours(1);
        space.Db.SetJson("mail.expiries", list);
        await Run(server, space);
        Assert.Empty(MailExpiry.All(space.Db));
    }

    [Fact]
    public async Task Schon_im_Papierkorb_gilt_als_erledigt()
    {
        var space = NewSpace();
        Row(space.Db, "INBOX", 1, "m1@x");
        await MailExpiry.PlanAsync(space.Db, "a", "INBOX", [1], DateTime.UtcNow.AddDays(1), MailExpiry.Trash);
        var server = new FakeServer();
        server.Add("Papierkorb", "m1@x", 9);
        Due(space.Db);
        Assert.Equal(1, await Run(server, space));
        Assert.Single(server.In("Papierkorb"));
        Assert.Empty(MailExpiry.All(space.Db));
    }

    [Fact]
    public void Ohne_IMAP_Server_verstaendliche_Meldung()
    {
        var ex = Assert.Throws<UnauthorizedAccessException>(() => ImapPool.RequireServer(new MailAccount { Id = "demo" }));
        Assert.Contains("IMAP", ex.Message);
    }

    [Fact]
    public void Regel_liefert_Fristen()
    {
        var rule = new MailRule
        {
            Name = "Newsletter", Conditions = [new RuleCondition { Field = "from", Op = "contains", Value = "news@" }],
            Actions = [new RuleAction { Type = "trashAfter", Value = "7" }, new RuleAction { Type = "destroyAfter", Value = "30" }],
        };
        var other = new MailRule
        {
            Name = "Kurz", Conditions = [new RuleCondition { Field = "subject", Op = "contains", Value = "Angebot" }],
            Actions = [new RuleAction { Type = "trashAfter", Value = "2" }, new RuleAction { Type = "destroyAfter", Value = "kaputt" }],
        };
        var m = new MimeMessage { Subject = "Angebot der Woche" };
        m.From.Add(new MailboxAddress("Shop", "news@shop.de"));

        var o = Rules.Evaluate([rule, other], "a", m);
        Assert.Equal(2, o.TrashAfterDays);    // die kuerzere Frist
        Assert.Equal(30, o.DestroyAfterDays);

        var plain = new MimeMessage { Subject = "Hallo" };
        plain.From.Add(new MailboxAddress("Anna", "anna@x.de"));
        var none = Rules.Evaluate([rule, other], "a", plain);
        Assert.Null(none.TrashAfterDays);
        Assert.Null(none.DestroyAfterDays);
    }
}
