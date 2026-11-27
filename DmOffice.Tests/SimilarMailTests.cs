using DmOffice.Core;
using DmOffice.Mail;

namespace DmOffice.Tests;

/// <summary>„Ähnliche Mails“: frühere Bestellung desselben Abos vor anderen Bestellungen desselben Absenders.</summary>
public class SimilarMailTests
{
    private static UserDb NewDb(out Action<long, string, string, string, string, int> add)
    {
        var dir = Directory.CreateTempSubdirectory("dmsimilar");
        var db = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "t");
        MailStore.ReplaceFolders(db, "a", new()
        {
            ("INBOX", "INBOX", "/", "inbox", true), ("Trash", "Trash", "/", "trash", true), ("Drafts", "Drafts", "/", "drafts", true),
        });
        add = (uid, folder, from, subject, body, daysAgo) =>
        {
            db.Exec(@"INSERT INTO mail(account,folder,uid,subject,from_name,from_addr,date) VALUES('a',$f,$u,$s,'',$fr,$d)",
                ("$f", folder), ("$u", uid), ("$s", subject), ("$fr", from), ("$d", DateTimeOffset.UtcNow.AddDays(-daysAgo).ToUnixTimeMilliseconds()));
            var id = Convert.ToInt64(db.Scalar("SELECT id FROM mail WHERE folder=$f AND uid=$u", ("$f", folder), ("$u", uid)));
            db.Exec("INSERT INTO mail_fts(rowid,subject,sender,recipients,body) VALUES($i,$s,$fr,'',$b)", ("$i", id), ("$s", subject), ("$fr", from), ("$b", body));
        };
        return db;
    }

    [Fact]
    public void Gleiches_Abo_vor_anderen_Bestellungen_desselben_Absenders()
    {
        var db = NewDb(out var add);
        add(1, "INBOX", "bestellung@shop.example", "Deine Bestellung: Kaffeebohnen Spar-Abo Crema", "Vielen Dank für deine Bestellung. Kaffeebohnen Spar-Abo Crema, Lieferung alle 4 Wochen.", 0);
        add(2, "INBOX", "bestellung@shop.example", "Deine Bestellung: Kaffeebohnen Spar-Abo Crema", "Vielen Dank für deine Bestellung. Kaffeebohnen Spar-Abo Crema, Lieferung alle 4 Wochen.", 400);
        add(3, "INBOX", "bestellung@shop.example", "Deine Bestellung: USB-Kabel", "Vielen Dank für deine Bestellung. USB-Kabel, Lieferung morgen.", 30);
        add(4, "INBOX", "bestellung@shop.example", "Deine Bestellung: Druckerpapier", "Vielen Dank für deine Bestellung. Druckerpapier, Lieferung morgen.", 60);
        add(5, "INBOX", "info@verein.example", "Sommerfest", "Einladung zum Sommerfest mit Kaffee und Kuchen", 10);
        add(6, "Drafts", "ich@beispiel.de", "Kaffeebohnen Spar-Abo Crema", "Entwurf zu Kaffeebohnen Spar-Abo Crema", 1);
        add(7, "Trash", "bestellung@shop.example", "Deine Bestellung: Kaffeebohnen Spar-Abo Crema", "Kaffeebohnen Spar-Abo Crema", 200);
        for (int i = 0; i < 20; i++) add(100 + i, "INBOX", $"p{i}@andere.example", $"Thema {i}", $"Irgendein Text {i} über Wetter und Garten", 100 + i);

        var hits = SimilarMail.Find(db, "a", "INBOX", 1);
        // Gleiches Abo (auch im Papierkorb) sehr ähnlich, dort das jüngere zuerst
        Assert.Equal(new uint[] { 7, 2 }, hits.Where(h => h.Similar == SimilarMail.VerySimilar).Select(h => h.Uid).ToArray());
        // Andere Bestellungen desselben Absenders weiter unten, Entwürfe nie, die Mail selbst nicht
        var others = hits.Where(h => h.Uid is 3 or 4).ToList();
        Assert.Equal(2, others.Count);
        Assert.All(others, h => Assert.True(h.Similar > SimilarMail.VerySimilar));
        Assert.DoesNotContain(hits, h => h.Uid is 1 or 6);
        // Papierkorb nur auf Wunsch
        Assert.Contains(hits, h => h.Uid == 7);
        Assert.DoesNotContain(SimilarMail.Find(db, "a", "INBOX", 1, withDeleted: false), h => h.Uid == 7);
        // Zu einer Bestellung ohne Vorgänger sind die anderen Bestellungen desselben Shops nicht „sehr ähnlich“
        Assert.DoesNotContain(SimilarMail.Find(db, "a", "INBOX", 3), h => h.Similar == SimilarMail.VerySimilar);
        // Innerhalb einer Stufe nach Datum
        foreach (var g in hits.GroupBy(h => h.Similar)) Assert.Equal(g.OrderByDescending(h => h.Date).Select(h => h.Id), g.Select(h => h.Id));
    }

    [Fact]
    public void Woerter_wie_der_Volltextindex()
    {
        Assert.Equal(["kaffeebohnen", "spar", "abo", "creme", "brulee", "12345"], SimilarMail.Tokens("Kaffeebohnen Spar-Abo: Crème brûlée, Nr. 12345 / 42").ToArray());
    }
}
