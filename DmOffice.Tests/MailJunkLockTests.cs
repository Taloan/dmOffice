using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DmOffice.Core;
using DmOffice.Mail;
using MimeKit;

namespace DmOffice.Tests;

/// <summary>Kennung junklock: Junk-Mails (Junk-Ordner oder Spamwert über der Grenze) - Anhänge und Weiterverwenden auch über die Schnittstelle gesperrt.</summary>
[Collection("server")]
public class MailJunkLockTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public MailJunkLockTests(ChatFixture fx) => _fx = fx;

    [Fact]
    public async Task Junk_sperrt_Anhaenge_und_Weiterverwenden()
    {
        var before = Features.InReleaseOff.ToList();
        try
        {
            Features.Set([]);
            var space = _fx.Space("dora");
            if (!space.Settings.MailAccounts.Any(a => a.Id == "acc-dora"))
                space.UpdateSettings(s => s.MailAccounts.Add(new MailAccount { Id = "acc-dora", Name = "Privat", Email = "dora@mailbox.example" }));
            var db = space.Db;
            MailStore.ReplaceFolders(db, "acc-dora", new() { ("INBOX", "Posteingang", "/", "inbox", true), ("Junk", "Junk", "/", "junk", true) });
            void Put(string folder, uint uid)
            {
                var m = new MimeMessage();
                m.From.Add(new MailboxAddress("Shop", "info@shop.example"));
                m.To.Add(new MailboxAddress("Dora", "dora@mailbox.example"));
                m.Subject = "Rechnung";
                var b = new BodyBuilder { TextBody = "Anbei" };
                b.Attachments.Add("rechnung.pdf", "%PDF-1.4 test"u8.ToArray(), new ContentType("application", "pdf"));
                m.Body = b.ToMessageBody();
                var ms = new MemoryStream(); m.WriteTo(ms);
                db.Exec(@"INSERT INTO mail(account,folder,uid,message_id,subject,from_name,from_addr,recipients,date,size,seen,flagged,has_att,cached)
                          VALUES('acc-dora',$f,$u,$id,'Rechnung','Shop','info@shop.example','{}',$d,10,0,0,1,1)",
                    ("$f", folder), ("$u", (long)uid), ("$id", m.MessageId), ("$d", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                MailStore.StoreRaw(db, "acc-dora", folder, uid, ms.ToArray(), m);
            }
            Put("Junk", 1); Put("INBOX", 2); Put("INBOX", 3);
            MailStore.SetSpamScore(db, "acc-dora", "INBOX", 2, 0.99);

            Assert.True(MailApi.Junk(space, "acc-dora", "Junk", 1));
            Assert.True(MailApi.Junk(space, "acc-dora", "INBOX", 2));
            Assert.False(MailApi.Junk(space, "acc-dora", "INBOX", 3));

            var http = _fx.As("dora");
            async Task Locked(HttpResponseMessage r)
            {
                Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
                Assert.True((await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("junk").GetBoolean());
            }
            // Anhang ansehen/herunterladen: Index 1 = rechnung.pdf (0 = Text)
            var parts = MailCompose.Parts(await MailApi.Load(space, space.Account("acc-dora"), "INBOX", 3, new ImapPool(), default));
            var pdf = parts.FindIndex(p => p.ContentType.MimeType == "application/pdf");
            Assert.True(pdf >= 0);
            await Locked(await http.GetAsync($"/api/mail/acc-dora/part?folder=Junk&uid=1&index={pdf}"));
            await Locked(await http.GetAsync($"/api/mail/acc-dora/part?folder=INBOX&uid=2&index={pdf}"));
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"/api/mail/acc-dora/part?folder=INBOX&uid=3&index={pdf}")).StatusCode);
            // In Ablage, als Notiz, ins DMS, Abmelden, Einladung beantworten
            await Locked(await http.PostAsJsonAsync("/api/mail/acc-dora/part/save", new { folder = "Junk", uid = 1, index = pdf, dir = "" }));
            await Locked(await http.PostAsJsonAsync("/api/mail/acc-dora/to-note", new { folder = "Junk", uid = 1, attachments = true }));
            await Locked(await http.PostAsJsonAsync("/api/mail/acc-dora/to-dms", new { folder = "Junk", uid = 1, whole = true }));
            await Locked(await http.PostAsJsonAsync("/api/mail/acc-dora/unsubscribe", new { folder = "INBOX", uid = 2 }));
            await Locked(await http.PostAsJsonAsync("/api/mail/acc-dora/invite", new { folder = "Junk", uid = 1, response = "accept", send = false }));
            // Termin-Erkennung liefert nichts
            Assert.Equal(HttpStatusCode.NoContent, (await http.GetAsync("/api/mail/acc-dora/smart?folder=Junk&uid=1")).StatusCode);

            // Schalter aus: wie bisher
            Features.Set([Features.JunkLock]);
            Assert.False(MailApi.Junk(space, "acc-dora", "Junk", 1));
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"/api/mail/acc-dora/part?folder=Junk&uid=1&index={pdf}")).StatusCode);
        }
        finally { Features.Set(before); }
    }
}
