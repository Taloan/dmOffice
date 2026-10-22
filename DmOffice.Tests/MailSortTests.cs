using DmOffice.Core;
using MailStore = DmOffice.Mail.MailStore;

namespace DmOffice.Tests;

/// <summary>Mailliste nach Absender, Betreff, Groesse, Anhang, Gelesen und Kennzeichnung sortieren.</summary>
public class MailSortTests
{
    private static UserDb Db()
    {
        var dir = Directory.CreateTempSubdirectory("dmsort");
        var db = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "t");
        MailStore.ReplaceFolders(db, "a", new() { ("INBOX", "INBOX", "/", "inbox", true) });
        void Add(uint uid, string name, string addr, string subject, long date, long size, bool att, bool seen, bool flagged) =>
            db.Exec("INSERT INTO mail(account,folder,uid,subject,from_name,from_addr,date,size,has_att,seen,flagged) VALUES('a','INBOX',$u,$s,$n,$ad,$d,$z,$at,$se,$f)",
                ("$u", (long)uid), ("$s", subject), ("$n", name), ("$ad", addr), ("$d", date), ("$z", size), ("$at", att ? 1 : 0), ("$se", seen ? 1 : 0), ("$f", flagged ? 1 : 0));
        Add(1, "zora", "z@x.de", "Beta", 1000, 500, false, true, false);
        Add(2, "", "anna@x.de", "alpha", 3000, 9000, true, false, false);   // ohne Namen: Adresse zaehlt
        Add(3, "Ben", "b@x.de", "Gamma", 2000, 100, false, true, true);
        return db;
    }

    private static string[] Uids(UserDb db, string? sort) =>
        MailStore.List(db, "a", "INBOX", 0, 50, null, sort).Items.Select(m => m.Subject).ToArray();

    [Fact]
    public void Sorting_follows_the_chosen_key()
    {
        var db = Db();
        Assert.Equal(new[] { "alpha", "Gamma", "Beta" }, Uids(db, null));        // neueste zuerst (Vorgabe)
        Assert.Equal(new[] { "Beta", "Gamma", "alpha" }, Uids(db, "oldest"));
        Assert.Equal(new[] { "alpha", "Gamma", "Beta" }, Uids(db, "from"));      // anna, Ben, zora - ohne Beachtung der Gross-/Kleinschreibung
        Assert.Equal(new[] { "alpha", "Beta", "Gamma" }, Uids(db, "subject"));
        Assert.Equal(new[] { "alpha", "Beta", "Gamma" }, Uids(db, "size"));      // 9000, 500, 100
        Assert.Equal("alpha", Uids(db, "attachment")[0]);
        Assert.Equal("alpha", Uids(db, "unread")[0]);
        Assert.Equal("Gamma", Uids(db, "flagged")[0]);
        Assert.Equal(new[] { "alpha", "Gamma", "Beta" }, Uids(db, "unbekannt"));  // unbekannter Wert: Vorgabe
    }
}
