using DmOffice.Core;
using MailKit;
using MimeKit;
using MailStore = DmOffice.Mail.MailStore;

namespace DmOffice.Tests;

/// <summary>Ansicht „Als Unterhaltungen“ (Kennung mailthreads): Zuordnung beim Einlesen, Migration 22, Gruppieren der Liste.</summary>
[Collection("server")]
public class MailThreadTests
{
    private static UserDb Db()
    {
        var dir = Directory.CreateTempSubdirectory("dmthread");
        var db = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "t");
        MailStore.ReplaceFolders(db, "a", new() { ("INBOX", "INBOX", "/", "inbox", true), ("Sent", "Sent", "/", "sent", true), ("Trash", "Trash", "/", "trash", true) });
        return db;
    }

    private static IMessageSummary Mail(uint uid, string? id, string subject, string from, string to, int minutes,
        string? inReplyTo = null, string[]? refs = null, bool seen = false)
    {
        var env = new Envelope { Subject = subject, MessageId = id, InReplyTo = inReplyTo, Date = new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero).AddMinutes(minutes) };
        env.From.Add(new MailboxAddress(null, from));
        env.To.Add(new MailboxAddress(null, to));
        var s = new MessageSummary((int)uid) { UniqueId = new UniqueId(uid), Envelope = env, Flags = seen ? MessageFlags.Seen : MessageFlags.None, Size = 100 };
        if (refs != null) { s.References = new MessageIdList(); foreach (var r in refs) s.References.Add(r); }
        return s;
    }

    private static string? Thread(UserDb db, string folder, uint uid) =>
        db.Scalar("SELECT thread FROM mail WHERE account='a' AND folder=$f AND uid=$u", ("$f", folder), ("$u", (long)uid)) as string;

    private static void WithThreads(Action act)
    {
        var before = Features.InReleaseOff.ToList();
        Features.Set(before.Where(x => x != Features.MailThreads));
        try { act(); } finally { Features.Set(before); }
    }

    [Fact]
    public void Einlesen_ordnet_Antworten_der_Unterhaltung_zu()
    {
        WithThreads(() =>
        {
            var db = Db();
            // Antwort ohne References, nur In-Reply-To - kommt vor der beantworteten Mail an
            MailStore.Insert(db, "a", "INBOX", [Mail(3, "c@x", "Re: Angebot", "b@example.invalid", "me@example.invalid", 30, inReplyTo: "b@x")]);
            Assert.Equal("b@x", Thread(db, "INBOX", 3));
            // Die beantwortete Mail hängt mit References an der Wurzel a@x: die Antwort zieht mit
            MailStore.Insert(db, "a", "Sent", [Mail(2, "b@x", "Re: Angebot", "me@example.invalid", "b@example.invalid", 20, inReplyTo: "a@x", refs: ["a@x"])]);
            Assert.Equal("a@x", Thread(db, "Sent", 2));
            Assert.Equal("a@x", Thread(db, "INBOX", 3));
            MailStore.Insert(db, "a", "INBOX", [Mail(1, "<a@x>", "Angebot", "b@example.invalid", "me@example.invalid", 0)]);
            Assert.Equal("a@x", Thread(db, "INBOX", 1));
            // Nur In-Reply-To auf eine bekannte Mail: deren Unterhaltung
            MailStore.Insert(db, "a", "INBOX", [Mail(4, "d@x", "Re: Angebot", "b@example.invalid", "me@example.invalid", 40, inReplyTo: "<c@x>")]);
            Assert.Equal("a@x", Thread(db, "INBOX", 4));
        });
    }

    [Fact]
    public void Ohne_Kopfzeilen_hilft_der_Betreff_nur_bei_gleichen_Beteiligten()
    {
        WithThreads(() =>
        {
            var db = Db();
            MailStore.Insert(db, "a", "INBOX", [Mail(1, "a@x", "Termin Mittwoch", "b@example.invalid", "me@example.invalid", 0)]);
            // „AW:“ ohne In-Reply-To/References, gleiche Beteiligte: dieselbe Unterhaltung
            MailStore.Insert(db, "a", "INBOX", [Mail(2, "b@x", "AW: Re: Termin Mittwoch", "b@example.invalid", "me@example.invalid", 60)]);
            Assert.Equal("a@x", Thread(db, "INBOX", 2));
            // Gleicher Betreff, fremder Absender und Empfänger: eigene Unterhaltung
            MailStore.Insert(db, "a", "INBOX", [Mail(3, "c@x", "Re: Termin Mittwoch", "x@example.invalid", "y@example.invalid", 70)]);
            Assert.Equal("c@x", Thread(db, "INBOX", 3));
            // Ohne „Re:“ kein Raten über den Betreff
            MailStore.Insert(db, "a", "INBOX", [Mail(4, "d@x", "Termin Mittwoch", "b@example.invalid", "me@example.invalid", 80)]);
            Assert.Equal("d@x", Thread(db, "INBOX", 4));
            // Älter als 30 Tage: eigene Unterhaltung
            MailStore.Insert(db, "a", "INBOX", [Mail(5, "e@x", "Re: Termin Mittwoch", "b@example.invalid", "me@example.invalid", -60 * 24 * 40)]);
            Assert.Equal("e@x", Thread(db, "INBOX", 5));
        });
    }

    [Fact]
    public void Ohne_Schalter_bleibt_die_Zuordnung_wie_bisher()
    {
        var before = Features.InReleaseOff.ToList();
        Features.Set(before.Append(Features.MailThreads));
        try
        {
            var db = Db();
            MailStore.Insert(db, "a", "INBOX", [Mail(1, "a@x", "Angebot", "b@example.invalid", "me@example.invalid", 0)]);
            MailStore.Insert(db, "a", "INBOX", [Mail(2, "b@x", "Re: Angebot", "b@example.invalid", "me@example.invalid", 10, inReplyTo: "a@x")]);
            MailStore.Insert(db, "a", "INBOX", [Mail(3, "c@x", "Re: Angebot", "b@example.invalid", "me@example.invalid", 20, inReplyTo: "b@x")]);
            Assert.Equal("b@x", Thread(db, "INBOX", 3));
        }
        finally { Features.Set(before); }
    }

    [Fact]
    public void Migration_entfernt_Klammern_und_haengt_reine_Antworten_an()
    {
        var db = Db();
        void Add(uint uid, string? mid, string? irt, string? thread) =>
            db.Exec("INSERT INTO mail(account,folder,uid,message_id,in_reply_to,thread,subject,date) VALUES('a','INBOX',$u,$m,$i,$t,'x',$u)",
                ("$u", (long)uid), ("$m", mid), ("$i", irt), ("$t", thread));
        Add(1, "<a@x>", null, "<a@x>");
        Add(2, "<b@x>", "<a@x>", "<a@x>");
        Add(3, "<c@x>", "<b@x>", "<b@x>");   // alte Zuordnung: nur In-Reply-To
        Add(4, "<d@x>", "<c@x>", "<c@x>");   // Antwort auf Antwort
        Add(5, "<e@x>", null, null);
        db.Exec(UserDb.ThreadSql);
        Assert.Equal(["a@x", "a@x", "a@x", "a@x", "e@x"], db.Query("SELECT thread FROM mail ORDER BY uid", r => r.GetString(0)));
        Assert.Equal(1L, db.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name='mail_thread'"));
    }

    [Fact]
    public void Migration_mit_200000_Mails_bleibt_schnell()
    {
        // Großdaten-Test: ohne passenden Index lief der Umbau quadratisch (Stunden bei großen Postfächern)
        var db = Db();
        db.Exec(@"WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i < 200000)
INSERT INTO mail(account,folder,uid,message_id,in_reply_to,thread,subject,date)
SELECT 'a','INBOX',i,'<m'||i||'@x>', CASE WHEN i % 3 = 0 THEN '<m'||(i-1)||'@x>' END,
       CASE WHEN i % 3 = 0 THEN '<m'||(i-1)||'@x>' ELSE '<m'||i||'@x>' END,'x',i FROM n");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        db.Exec(UserDb.ThreadSql);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60), $"Umbau dauerte {sw.Elapsed}");
        Assert.Equal("m2@x", db.Scalar("SELECT thread FROM mail WHERE uid = 3"));
    }

    [Fact]
    public void Liste_zeigt_je_Unterhaltung_eine_Zeile_mit_Anzahl_und_Gesendetem()
    {
        var db = Db();
        void Add(string folder, uint uid, string mid, string? thread, long date, bool seen) =>
            db.Exec("INSERT INTO mail(account,folder,uid,message_id,thread,subject,from_addr,date,seen) VALUES('a',$f,$u,$m,$t,$s,'b@example.invalid',$d,$se)",
                ("$f", folder), ("$u", (long)uid), ("$m", mid), ("$t", thread), ("$s", "Mail " + uid), ("$d", date), ("$se", seen ? 1 : 0));
        Add("INBOX", 1, "a@x", "a@x", 100, true);
        Add("Sent", 2, "b@x", "a@x", 200, true);       // eigene Antwort
        Add("INBOX", 3, "c@x", "a@x", 300, false);
        Add("INBOX", 4, "d@x", "d@x", 250, false);     // allein
        Add("INBOX", 5, "e@x", null, 50, true);        // ohne Message-ID-Zuordnung
        Add("Trash", 6, "f@x", "a@x", 400, false);     // Papierkorb zählt nicht
        Add("INBOX", 7, "b@x", "a@x", 200, true);      // dieselbe Mail auch im Posteingang: einmal zählen

        var (total, items) = MailStore.Threads(db, "a", "INBOX", 0, 50, null);
        Assert.Equal(3, total);
        Assert.Equal(["Mail 3", "Mail 4", "Mail 5"], items.Select(i => i.Subject));
        Assert.Equal(3, items[0].ThreadCount);
        Assert.Equal(1, items[0].ThreadUnread);
        Assert.Equal(1, items[1].ThreadCount);
        Assert.StartsWith("id:", items[2].ThreadKey);

        // Filter „Ungelesen“ wirkt auf die Zeilen des Ordners
        Assert.Equal(["Mail 3", "Mail 4"], MailStore.Threads(db, "a", "INBOX", 0, 50, "unread").Items.Select(i => i.Subject));
        // Seiten
        Assert.Equal(["Mail 5"], MailStore.Threads(db, "a", "INBOX", 1, 2, null).Items.Select(i => i.Subject));

        var conv = MailStore.Conversation(db, "a", "a@x", "INBOX");
        Assert.Equal(["Mail 3", "Mail 7", "Mail 1"], conv.Select(m => m.Subject));   // b@x aus dem Posteingang, nicht doppelt
        Assert.Equal("Sent", MailStore.Conversation(db, "a", "a@x", "Sent").First(m => m.MessageId == "b@x").Folder);
        Assert.Contains(MailStore.Conversation(db, "a", "a@x", "Trash"), m => m.Folder == "Trash");
        Assert.Single(MailStore.Conversation(db, "a", items[2].ThreadKey!, "INBOX"));
    }

    [Fact]
    public void Betreff_ohne_Vorsaetze()
    {
        Assert.Equal("termin", MailStore.CleanSubject("AW: Re: WG: termin"));
        Assert.Equal("angebot", MailStore.CleanSubject("Re[2]: Fwd: Angebot "));
        Assert.Equal("reise", MailStore.CleanSubject("Reise"));
    }
}
