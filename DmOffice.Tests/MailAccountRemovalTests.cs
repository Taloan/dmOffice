using DmOffice.Core;
using DmOffice.Mail;
using MailStore = DmOffice.Mail.MailStore;

namespace DmOffice.Tests;

/// <summary>Konto entfernen: aus Datenschutzgruenden darf nichts von dessen Mails in der Datenbank bleiben.</summary>
public class MailAccountRemovalTests
{
    [Fact]
    public void Removing_an_account_leaves_no_rows_of_it_in_any_table()
    {
        var dir = Directory.CreateTempSubdirectory("dmforget");
        var db = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "t");
        MailStore.ReplaceFolders(db, "a", new() { ("INBOX", "INBOX", "/", "inbox", true) });
        MailStore.ReplaceFolders(db, "b", new() { ("INBOX", "INBOX", "/", "inbox", true) });
        MailStore.ForgetAccount(db, "none");   // legt die Hilfstabellen an
        MailQueue.Ensure(db);
        db.Exec("CREATE TABLE IF NOT EXISTS smart_done (account TEXT NOT NULL, message_id TEXT NOT NULL, at INTEGER NOT NULL, PRIMARY KEY(account, message_id)) WITHOUT ROWID");
        foreach (var acc in new[] { "a", "b" })
        {
            for (int i = 1; i <= 3; i++)
            {
                db.Exec("INSERT INTO mail(account,folder,uid,subject,from_name,from_addr,date,size,message_id) VALUES($a,'INBOX',$u,'Geheim','Anna','anna@x.de',1,10,$m)",
                    ("$a", acc), ("$u", (long)i), ("$m", $"<{acc}{i}@x>"));
                var id = Convert.ToInt64(db.Scalar("SELECT id FROM mail WHERE account=$a AND uid=$u", ("$a", acc), ("$u", (long)i)));
                db.Exec("INSERT INTO mail_fts(rowid,subject,sender,recipients,body) VALUES($i,'Geheim','Anna','','Inhalt')", ("$i", id));
                db.Exec("INSERT INTO mail_raw(id, raw) VALUES($i, x'0102')", ("$i", id));
            }
            db.Exec("INSERT INTO mail_followup(account,message_id,due) VALUES($a,'<x>',NULL)", ("$a", acc));
            db.Exec("INSERT INTO smart_done(account,message_id,at) VALUES($a,'<x>',1)", ("$a", acc));
            db.Exec("INSERT INTO mail_queue(account,source,draft,raw,send_at,created,subject,recipients) VALUES($a,'s','d',x'01',1,1,'Geheim','x@y.de')", ("$a", acc));
            db.Exec("INSERT INTO mail_processed(account,key,stage,at) VALUES($a,'k',1,1)", ("$a", acc));
            db.Exec("INSERT INTO mail_fetch_failed(account,folder,uid,tries,next) VALUES($a,'INBOX',9,1,1)", ("$a", acc));
            db.Exec("INSERT INTO kv(key,value) VALUES($k,'v')", ("$k", $"mail.{acc}.state"));
        }

        MailStore.ForgetAccount(db, "a");

        using var conn = db.Open();
        var leftovers = new List<string>();
        foreach (var table in UserDb.Query(conn, "SELECT name FROM sqlite_master WHERE type='table'", r => r.GetString(0)))
        {
            if (table.Contains("_fts")) continue;   // Hilfstabellen des Suchindex, unten ueber mail_fts geprueft
            var cols = UserDb.Query(conn, $"SELECT name FROM pragma_table_info('{table}')", r => r.GetString(0));
            if (!cols.Contains("account")) continue;
            var n = Convert.ToInt64(UserDb.Query(conn, $"SELECT COUNT(*) FROM {table} WHERE account='a'", r => r.GetInt64(0)).First());
            if (n > 0) leftovers.Add($"{table}: {n}");
        }
        Assert.Empty(leftovers);
        Assert.Equal(0L, Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM kv WHERE key LIKE 'mail.a.%'")));
        // der Inhalt: Rohdaten und Suchindex nur noch von Konto b
        Assert.Equal(3L, Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM mail_raw")));
        Assert.Equal(3L, Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM mail_fts")));
        Assert.Equal(3L, Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM mail")));
    }
}
