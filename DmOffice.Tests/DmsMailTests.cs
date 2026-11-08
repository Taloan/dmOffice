using System.Text;
using DmOffice.Core;
using DmOffice.Dms;
using MimeKit;
using D = DmOffice.Dms.Dms;

namespace DmOffice.Tests;

public class DmsMailTests
{
    private static (UserSpace Space, DmsHost Host) NewSpace()
    {
        var dir = Directory.CreateTempSubdirectory("dmdmsmail");
        var db = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "t");
        var host = new DmsHost(new ServerConfig(new DataPaths(Path.Combine(dir.FullName, "data"))));
        DmsHost.Current = host;
        return (new UserSpace(new UserRecord { Login = "t", Id = "u" + Guid.NewGuid().ToString("N")[..6] }, db), host);
    }

    private static byte[] Mail(string subject, string from, string body, params (string Name, string Mime, byte[] Data)[] attachments)
    {
        var m = new MimeMessage();
        m.From.Add(MailboxAddress.Parse(from));
        m.To.Add(MailboxAddress.Parse("ich@example.org"));
        m.Subject = subject;
        m.Date = new DateTimeOffset(2026, 3, 14, 9, 0, 0, TimeSpan.Zero);
        m.MessageId = Guid.NewGuid().ToString("N") + "@example.org";
        var b = new BodyBuilder { TextBody = body };
        foreach (var (name, mime, data) in attachments) b.Attachments.Add(name, data, ContentType.Parse(mime));
        m.Body = b.ToMessageBody();
        using var ms = new MemoryStream();
        m.WriteTo(ms);
        return ms.ToArray();
    }

    [Fact]
    public void Mail_als_PDF_mit_Kopf_und_Text()
    {
        var eml = Mail("Ihre Rechnung März", "Stadtwerke <rechnung@stadtwerke.example>", "Sehr geehrte Damen und Herren,\n\nanbei die Rechnung. " + new string('x', 3000));
        var pdf = DmsMailPdf.Make(eml, h => h, default);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
        // Der Text ist Text (durchsuchbar)
        var text = Search.TextExtract.Extract(pdf, "m.pdf", "application/pdf", ocr: false);
        Assert.Contains("Rechnung", text.Text);
        Assert.Contains("stadtwerke", text.Text);
    }

    [Fact]
    public void Muster_Filter_und_Anhaenge()
    {
        Assert.True(DmsMailSources.Matches("*.pdf, *rechnung*", "Beleg.PDF"));
        Assert.True(DmsMailSources.Matches("*.pdf, *rechnung*", "Rechnung_2026.jpg"));
        Assert.False(DmsMailSources.Matches("*.pdf", "logo.png"));
        Assert.Null(DmsMailSources.Matches("", "x"));

        var m = MimeMessage.Load(new MemoryStream(Mail("Rechnung", "a@b.de", "Text", ("r.pdf", "application/pdf", Encoding.ASCII.GetBytes("%PDF-1.4 x")),
            ("logo.png", "image/png", [1, 2, 3]))));
        var s = new DmsMailSource { FilterFrom = "b.de", FilterSubject = "rechnung", Include = "*.pdf" };
        Assert.True(DmsMailSources.Fits(s, m, h => h));
        Assert.Equal(["r.pdf"], DmsMailSources.Parts(s, m).Select(p => p.Name));
        Assert.False(DmsMailSources.Fits(new DmsMailSource { FilterBody = "Mahnung" }, m, h => h));
    }

    [Fact]
    public async Task Mailordner_als_Quelle_legt_ab_merkt_sich_und_handelt_danach()
    {
        var (space, host) = NewSpace();
        var mail = new FakeMail();
        mail.Add("Ihre Rechnung", "Stadtwerke <rechnung@stadtwerke.example>", ("rechnung.txt", "text/plain", Encoding.UTF8.GetBytes("Rechnung Strom 02.01.2026")));
        mail.Add("Newsletter", "news@example.org");
        mail.Add("Noch eine Rechnung", "Stadtwerke <rechnung@stadtwerke.example>", ("r2.txt", "text/plain", Encoding.UTF8.GetBytes("Rechnung Wasser 03.01.2026")));
        var s = DmsMailSources.Save(space, new DmsMailSource { Name = "Rechnungen", Account = "acc", Folder = "INBOX", FilterSubject = "rechnung",
            Scope = "both", Action = "seen", CorrespondentMode = "sender" }, mail);

        var n = await DmsMailSources.RunDueAsync(space, mail, h => h, new HashSet<string>(), default);
        Assert.Equal(4, n);
        await DmsImport.ProcessQueueAsync(space, host, 100, default);
        var docs = D.Docs(space.Db, new D.Filter()).Items;
        Assert.Equal(4, docs.Count);
        Assert.Equal(2, docs.Count(d => d.OriginalName.EndsWith(".eml")));
        // Korrespondent aus dem Absender, einmal angelegt mit Zuweisung „Absender“
        var corr = D.Entries(space.Db, D.Correspondent).Single();
        Assert.Equal("Stadtwerke", corr.Name);
        Assert.Equal(D.Sender, corr.Match);
        Assert.All(docs, d => Assert.Equal(corr.Id, d.Correspondent));
        // Ganze Mail: Titel aus dem Betreff, Anhang: aus dem Dateinamen
        Assert.Contains(docs, d => d.Title == "Ihre Rechnung");
        Assert.Contains(docs, d => d.Title == "rechnung");
        Assert.Equal(2, mail.Actions.Count(a => a == "seen"));
        // Herkunft: über Konto und Message-ID zu finden
        Assert.Equal(2, D.ByRef(space.Db, "acc|" + mail.Items[0].MessageId).Count);

        // Zweiter Lauf: nichts Neues
        DmsMailSources.Update(space.Db, s.Id, x => x.LastRun = null);
        Assert.Equal(0, await DmsMailSources.RunDueAsync(space, mail, h => h, new HashSet<string>(), default));
        Assert.Equal(2, mail.Actions.Count);
    }

    private sealed class FakeMail : IDmsMail
    {
        public readonly List<DmsMailItem> Items = [];
        private readonly Dictionary<uint, byte[]> raw = [];
        public readonly List<string> Actions = [];

        public void Add(string subject, string from, params (string, string, byte[])[] attachments)
        {
            var data = Mail(subject, from, "Hallo", attachments);
            var uid = (uint)Items.Count + 1;
            raw[uid] = data;
            Items.Add(new DmsMailItem("INBOX", uid, MimeMessage.Load(new MemoryStream(data)).MessageId, DateTimeOffset.UtcNow.AddDays(-1)));
        }

        public List<(string Id, string Name)> Accounts(UserSpace space) => [("acc", "Test")];
        public List<(string Path, string Name)> Folders(UserSpace space, string account) => [("INBOX", "Posteingang")];
        public List<DmsMailItem> List(UserSpace space, string account, string folder, DateTimeOffset since) => Items.Where(i => i.Date >= since).ToList();
        public Task<byte[]> RawAsync(UserSpace space, string account, string folder, uint uid, CancellationToken ct) => Task.FromResult(raw[uid]);
        public void After(UserSpace space, string account, string folder, uint uid, string action, string? target) => Actions.Add(action);
    }
}
