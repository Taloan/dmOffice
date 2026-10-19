using DmOffice.Core;
using DmOffice.Mail;

namespace DmOffice.Tests;

public class MailSearchTests
{
    private static UserDb NewDb()
    {
        var dir = Directory.CreateTempSubdirectory("dmsearch");
        var db = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "t");
        MailStore.ReplaceFolders(db, "a", new()
        {
            ("INBOX", "INBOX", "/", "inbox", true), ("Trash", "Trash", "/", "trash", true), ("Archiv", "Archiv", "/", "archive", true),
        });
        void Add(long uid, string folder, string subject, string body, int daysAgo)
        {
            db.Exec(@"INSERT INTO mail(account,folder,uid,subject,from_name,from_addr,date,flagged) VALUES('a',$f,$u,$s,'Anna','anna@x.de',$d,$fl)",
                ("$f", folder), ("$u", uid), ("$s", subject), ("$d", DateTimeOffset.UtcNow.AddDays(-daysAgo).ToUnixTimeMilliseconds()), ("$fl", uid == 1 ? 1 : 0));
            var id = Convert.ToInt64(db.Scalar("SELECT id FROM mail WHERE folder=$f AND uid=$u", ("$f", folder), ("$u", uid)));
            db.Exec("INSERT INTO mail_fts(rowid,subject,sender,recipients,body) VALUES($i,$s,'Anna anna@x.de','',$b)", ("$i", id), ("$s", subject), ("$b", body));
        }
        Add(1, "INBOX", "Einladung zum Sommerfest", "Wir laden ein", 5);
        Add(2, "Archiv", "Rechnung", "Im Anhang die Einladung von gestern", 1);
        Add(3, "Trash", "Einladung alt", "", 0);
        return db;
    }

    [Fact]
    public void Suche_nach_Datum_mit_allen_Spalten()
    {
        var hits = MailStore.Search(NewDb(), "einladung", "a", null);
        Assert.Equal(new uint[] { 3, 2, 1 }, hits.Select(h => h.Uid).ToArray());
        Assert.All(hits, h => Assert.Equal("anna@x.de", h.From!.Address));
    }

    [Fact]
    public void Ohne_Papierkorb_und_nach_Relevanz()
    {
        var hits = MailStore.Search(NewDb(), "einladung", "a", null, withDeleted: false, relevance: true);
        // Treffer im Betreff wiegt mehr als im Text
        Assert.Equal(new uint[] { 1, 2 }, hits.Select(h => h.Uid).ToArray());
    }

    [Fact]
    public void Markiert_ueber_alle_Ordner()
    {
        var (total, items) = MailStore.List(NewDb(), "a", MailStore.FlaggedFolder, 0, 50, null);
        Assert.Equal(1, total);
        Assert.Equal("INBOX", items[0].Folder);
    }

    [Fact]
    public void Kategorie_setzen_und_filtern()
    {
        var db = NewDb();
        MailStore.SetKeyword(db, "a", "INBOX", new uint[] { 1 }, "dm_rechnung", true);
        var (total, items) = MailStore.List(db, "a", "INBOX", 0, 50, "cat:dm_rechnung");
        Assert.Equal(1, total);
        Assert.Equal(new[] { "dm_rechnung" }, items[0].Keywords);
        Assert.Equal(0, MailStore.List(db, "a", "INBOX", 0, 50, "cat:dm_privat").Total);
        // Flags-Abgleich vom Server ersetzt die Schlagworte, Systemschlagworte bleiben draussen
        MailStore.SetFlags(db, "a", "INBOX", 1, MailKit.MessageFlags.Seen | MailKit.MessageFlags.Flagged, new[] { "$Forwarded", "dm_privat" });
        Assert.Equal(new[] { "dm_privat" }, MailStore.List(db, "a", "INBOX", 0, 50, null).Items[0].Keywords);
        Assert.Equal(1, MailStore.List(db, "a", MailStore.FlaggedFolder, 0, 50, "cat:dm_privat").Total);
    }

    [Fact]
    public void Schlagwort_aus_Namen()
    {
        Assert.Equal("dm_spaeter", DmOffice.Settings.Categories.KeyFor("Später", Array.Empty<string>()));
        Assert.Equal("dm_gruen_2", DmOffice.Settings.Categories.KeyFor("Grün!", new[] { "dm_gruen" }));
        Assert.True(DmOffice.Settings.Categories.ValidKey("dm_gruen_2"));
        Assert.False(DmOffice.Settings.Categories.ValidKey("dm_a b"));
    }

    [Fact]
    public void Suchordner_nach_Kriterien()
    {
        var db = NewDb();
        MailStore.SetKeyword(db, "a", "Archiv", new uint[] { 2 }, "dm_rechnung", true);
        // Text + Kategorie
        var (t1, _, i1) = MailStore.Query(db, new MailCriteria { Text = "einladung", Categories = new() { "dm_rechnung" } }, 0, 50);
        Assert.Equal(1, t1);
        Assert.Equal(2u, i1[0].Uid);
        // Papierkorb nur auf Wunsch
        Assert.Equal(2, MailStore.Query(db, new MailCriteria { Text = "einladung" }, 0, 50).Total);
        Assert.Equal(3, MailStore.Query(db, new MailCriteria { Text = "einladung", IncludeDeleted = true }, 0, 50).Total);
        // Ordner und Zeitraum
        Assert.Equal(1, MailStore.Query(db, new MailCriteria { Folders = new() { "a|INBOX" } }, 0, 50).Total);
        Assert.Equal(1, MailStore.Query(db, new MailCriteria { Days = 3 }, 0, 50).Total);
        // Unbekannter Suchordner zeigt nichts
        Assert.Equal(0, MailStore.Query(db, MailStore.Virtual(new UserSettings(), "sf:gibtsnicht")!, 0, 50).Total);
    }

    [Fact]
    public void Nachverfolgung_mit_Faelligkeit()
    {
        var db = NewDb();
        db.Exec("UPDATE mail SET message_id='m1@x' WHERE uid=1 AND folder='INBOX'");
        MailStore.SetFollowUpDue(db, "a", "INBOX", new uint[] { 1 }, "2030-10-24");
        var list = MailStore.FollowUps(db);
        // uid 1 ist markiert (NewDb), Papierkorb zaehlt nicht
        Assert.Single(list);
        Assert.Equal("2030-10-24", list[0].Due);
        // Faelligkeit bleibt, wenn die Mail verschoben wird (gleiche Message-ID)
        db.Exec("UPDATE mail SET folder='Archiv' WHERE message_id='m1@x'");
        Assert.Equal("2030-10-24", MailStore.FollowUps(db)[0].Due);
    }
}
