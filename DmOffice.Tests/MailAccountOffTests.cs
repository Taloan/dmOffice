using System.Net;
using System.Net.Http.Json;
using DmOffice.Core;
using DmOffice.Mail;
using MailKit.Security;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>Kennung mailaccountoff: deaktiviertes Mailkonto ruht (kein Abgleich, nicht im Mailbereich, kein Senden), die Daten bleiben.</summary>
[Collection("server")]
public class MailAccountOffTests : IClassFixture<FilesServerFixture>
{
    private readonly FilesServerFixture _fx;
    public MailAccountOffTests(FilesServerFixture fx) => _fx = fx;

    [Fact]
    public async Task Deaktiviertes_Konto_wird_nicht_abgeglichen_aktiviert_wieder()
    {
        var before = Features.InReleaseOff.ToList();
        try
        {
            Features.Set([]);
            // Server, der nicht antwortet: ein Verbindungsversuch scheitert mit einem Netzfehler, nicht mit „deaktiviert“
            var a = new MailAccount { Id = "a", ImapHost = "127.0.0.1", ImapPort = 1, ImapUser = "x", ImapPassword = "y", Disabled = true };
            var b = new MailAccount { Id = "b" };
            var s = new UserSettings { MailAccounts = [a, b] };
            Assert.Equal(["b"], s.ActiveMailAccounts().Select(x => x.Id));
            Assert.Equal(["a"], s.OffMailAccountIds());

            using var pool = new ImapPool();
            var ex = await Assert.ThrowsAsync<AuthenticationException>(() => pool.UseAsync(a, ImapPool.Sync, _ => Task.CompletedTask));
            Assert.Contains("deaktiviert", ex.Message);

            // Aktivieren: der Abgleich verbindet wieder (hier scheitert erst die Verbindung)
            a.Disabled = false;
            Assert.Equal(["a", "b"], s.ActiveMailAccounts().Select(x => x.Id));
            var again = await Record.ExceptionAsync(() => pool.UseAsync(a, ImapPool.Sync, _ => Task.CompletedTask));
            Assert.NotNull(again);
            Assert.False(again is AuthenticationException && again.Message.Contains("deaktiviert"));

            // Ohne Freischaltung zählt das Feld nicht
            a.Disabled = true;
            Features.Set([Features.MailAccountOff]);
            Assert.Equal(["a", "b"], s.ActiveMailAccounts().Select(x => x.Id));
        }
        finally { Features.Set(before); }
    }

    [Fact]
    public void Standardkonto_und_Postausgang()
    {
        var before = Features.InReleaseOff.ToList();
        try
        {
            Features.Set([]);
            var a = new MailAccount { Id = "a" }; var b = new MailAccount { Id = "b" };
            var s = new UserSettings { MailAccounts = [a, b], DefaultMailAccount = "a" };
            a.Disabled = true;
            // Gewähltes Standardkonto deaktiviert: das erste aktive gilt, die Wahl bleibt gespeichert
            Assert.Same(b, s.DefaultAccount());
            Assert.Equal("a", s.DefaultMailAccount);
            a.Disabled = false;
            Assert.Same(a, s.DefaultAccount());

            // Postausgang: Mails des deaktivierten Kontos bleiben liegen, nichts wird verworfen
            var dir = Directory.CreateTempSubdirectory("dmoff");
            var db = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "t");
            var m = new MimeKit.MimeMessage();
            m.From.Add(new MimeKit.MailboxAddress("Ich", "ich@example.org"));
            m.To.Add(new MimeKit.MailboxAddress("Anna", "anna@example.org"));
            m.Body = new MimeKit.TextPart("plain") { Text = "Hallo" };
            var now = DateTime.UtcNow;
            MailQueue.Add(db, "a", "a", new MailCompose.Draft(), m, now);
            MailQueue.Add(db, "b", "b", new MailCompose.Draft(), m, now);
            var due = MailQueue.TakeDue(db, now.AddSeconds(1), new HashSet<string> { "a" });
            Assert.Single(due);
            Assert.Equal(1, MailQueue.CountFor(db, "a"));
            Assert.Single(MailQueue.TakeDue(db, now.AddSeconds(2)));
        }
        finally { Features.Set(before); }
    }

    [Fact]
    public void Mails_bleiben_und_fehlen_nur_in_der_Gesamtansicht()
    {
        var dir = Directory.CreateTempSubdirectory("dmoff");
        var db = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "t");
        foreach (var acc in new[] { "a", "b" })
        {
            MailStore.ReplaceFolders(db, acc, new() { ("INBOX", "INBOX", "/", "inbox", true) });
            db.Exec("INSERT INTO mail(account,folder,uid,subject,from_name,from_addr,date) VALUES($a,'INBOX',1,$s,'Anna','anna@x.de',1000)",
                ("$a", acc), ("$s", "Post " + acc));
        }
        var all = MailStore.Query(db, new MailCriteria(), 0, 50);
        Assert.Equal(2, all.Total);
        var shown = MailStore.Query(db, new MailCriteria(), 0, 50, hidden: new HashSet<string> { "a" });
        Assert.Equal(["Post b"], shown.Items.Select(x => x.Subject));
        // Die Mails des Kontos sind weiter da
        Assert.Single(MailStore.List(db, "a", "INBOX", 0, 50, null).Items);
    }

    [Fact]
    public async Task Ueber_die_API_deaktivieren_und_aktivieren()
    {
        var users = _fx.Factory.Services.GetRequiredService<UserStore>();
        var space = _fx.Factory.Services.GetRequiredService<Spaces>().Get(users.ByLogin(FilesServerFixture.Login)!.Id);
        string id1 = "off1-" + Guid.NewGuid().ToString("N")[..6], id2 = "off2-" + Guid.NewGuid().ToString("N")[..6];
        space.UpdateSettings(s =>
        {
            s.MailAccounts.Add(new MailAccount { Id = id1, Name = "Eins", Email = "eins@example.org" });
            s.MailAccounts.Add(new MailAccount { Id = id2, Name = "Zwei", Email = "zwei@example.org" });
            s.DefaultMailAccount = id1;
        });
        var c = _fx.Client(signedIn: false, cookies: true);
        var login = await c.PostAsJsonAsync("/api/auth/login", new { login = FilesServerFixture.Login, password = "geheim-123456" });
        Assert.True(login.IsSuccessStatusCode, await login.Content.ReadAsStringAsync());
        var before = Features.InReleaseOff.ToList();
        try
        {
            Features.Set([]);
            var r = await c.PutAsJsonAsync($"/api/settings/accounts/{id1}/active", new { active = false });
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var res = await r.Content.ReadFromJsonAsync<Toggle>();
            Assert.True(res!.Disabled);
            Assert.Contains(res.OffNotes, n => n.Kind == "default" && n.Name == "Zwei");

            // Mailbereich: fehlt; Einstellungen und all=true: gekennzeichnet
            var list = await c.GetFromJsonAsync<List<Row>>("/api/mail/accounts");
            Assert.DoesNotContain(list!, x => x.Id == id1);
            Assert.Contains(list!, x => x.Id == id2 && x.IsDefault);
            var full = await c.GetFromJsonAsync<List<Row>>("/api/mail/accounts?all=true");
            Assert.Contains(full!, x => x.Id == id1 && x.Disabled);
            var settings = await c.GetFromJsonAsync<Settings>("/api/settings/");
            Assert.Contains(settings!.Accounts, x => x.Id == id1 && x.Disabled);
            var status = await c.GetStringAsync("/api/mail/status");
            Assert.DoesNotContain(id1, status);

            // Senden aus dem deaktivierten Konto: abgelehnt
            var send = await c.PostAsync($"/api/mail/{id1}/send", new MultipartFormDataContent());
            Assert.Equal(HttpStatusCode.BadRequest, send.StatusCode);
            Assert.Contains("deaktiviert", await send.Content.ReadAsStringAsync());

            // Wieder aktivieren
            r = await c.PutAsJsonAsync($"/api/settings/accounts/{id1}/active", new { active = true });
            Assert.False((await r.Content.ReadFromJsonAsync<Toggle>())!.Disabled);
            list = await c.GetFromJsonAsync<List<Row>>("/api/mail/accounts");
            Assert.Contains(list!, x => x.Id == id1 && x.IsDefault);

            // Ohne Freischaltung: kein Endpunkt
            Features.Set([Features.MailAccountOff]);
            Assert.Equal(HttpStatusCode.NotFound, (await c.PutAsJsonAsync($"/api/settings/accounts/{id1}/active", new { active = false })).StatusCode);
        }
        finally
        {
            Features.Set(before);
            space.UpdateSettings(s => { s.MailAccounts.RemoveAll(a => a.Id == id1 || a.Id == id2); s.DefaultMailAccount = null; });
        }
    }

    private sealed record Note(string Kind, int Count, string? Name);
    private sealed record Toggle(bool Disabled, List<Note> OffNotes);
    private sealed record Row(string Id, bool IsDefault, bool Disabled);
    private sealed record Settings(List<Row> Accounts);
}
