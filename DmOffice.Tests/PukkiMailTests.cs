using System.Net.Http.Json;
using System.Text.Json;
using DmOffice.Chat;
using DmOffice.Core;
using DmOffice.Mail;
using Microsoft.Extensions.DependencyInjection;

namespace DmOffice.Tests;

/// <summary>Pukki und die Mails: nur mit Erlaubnis (Ja/Nein), dann Wiedervorlage, Ungelesenes, Mails von jemandem, heute.</summary>
[Collection("server")]
public class PukkiMailTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public PukkiMailTests(ChatFixture fx) => _fx = fx;

    private ChatStore Chat => _fx.Factory.Services.GetRequiredService<ChatStore>();

    private UserSpace Prepare(string login)
    {
        var space = _fx.Space(login);
        space.UpdateSettings(s => { if (s.MailAccounts.Count == 0) s.MailAccounts.Add(new MailAccount { Id = "m1", Name = "Privat", Email = login + "@x.de" }); });
        var db = space.Db;
        MailStore.ReplaceFolders(db, "m1", new() { ("INBOX", "INBOX", "/", "inbox", true), ("Trash", "Trash", "/", "trash", true) });
        void Add(uint uid, string folder, string subject, string from, int hoursAgo, bool seen, bool flagged)
        {
            db.Exec(@"INSERT INTO mail(account,folder,uid,subject,from_name,from_addr,date,seen,flagged,message_id) VALUES('m1',$f,$u,$s,$n,$a,$d,$se,$fl,$m)",
                ("$f", folder), ("$u", uid), ("$s", subject), ("$n", from), ("$a", from.ToLowerInvariant() + "@firma.de"),
                ("$d", DateTimeOffset.UtcNow.AddHours(-hoursAgo).ToUnixTimeMilliseconds()), ("$se", seen ? 1 : 0), ("$fl", flagged ? 1 : 0), ("$m", $"<{uid}@test>"));
            var id = Convert.ToInt64(db.Scalar("SELECT id FROM mail WHERE folder=$f AND uid=$u", ("$f", folder), ("$u", uid)));
            db.Exec("INSERT INTO mail_fts(rowid,subject,sender,recipients,body) VALUES($i,$s,$n,'','')", ("$i", id), ("$s", subject), ("$n", from + " " + from.ToLowerInvariant() + "@firma.de"));
        }
        Add(1, "INBOX", "Angebot Dachrinne", "Anna", 1, seen: false, flagged: true);
        Add(2, "INBOX", "Rechnung Oktober", "Bernd", 50, seen: true, flagged: false);
        Add(3, "INBOX", "Treffen nächste Woche", "Anna", 2, seen: false, flagged: false);
        Add(4, "Trash", "Alter Kram", "Carl", 3, seen: false, flagged: true);
        // „Morgen“ wie Pukki rechnet: in der Zeitzone des Benutzers (nach UTC schlug der Test zwischen 0 und 2 Uhr fehl)
        MailStore.SetFollowUpDue(db, "m1", "INBOX", [1u], DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, space.Zone)).AddDays(1).ToString("yyyy-MM-dd"));
        return space;
    }

    [Fact]
    public async Task Erst_fragen_dann_Wiedervorlage_und_Ungelesenes()
    {
        var space = Prepare("ben");
        Pukki.Ensure(Chat, _fx.Id("ben"));
        var room = Chat.DirectRoom(_fx.Id("ben"), Pukki.Id)!.Value;
        var services = _fx.Factory.Services;

        // Ohne Erlaubnis: Pukki fragt mit Ja/Nein
        var ask = Pukki.Answer(services, space, "Welche Mails sind auf Wiedervorlage?", Chat, room);
        Assert.StartsWith("Dafür müsste ich in deine Mails schauen. Darf ich das?", ask.Text);
        Assert.Equal([Pukki.MailAllow, Pukki.MailDeny], ask.Actions!.Select(a => a.Key));
        Assert.False(Pukki.MailAllowed(space.Db));

        // „Ja, erlauben“: Schalter an und gleich die Antwort - nur die markierte im Posteingang, nicht die im Papierkorb
        var msg = Chat.Post(Pukki.Id, room, ask.Text, actions: ask.Actions);
        var before = Chat.Messages(_fx.Id("ben"), room).Count;
        var r = await _fx.As("ben").PostAsync($"/api/chat/messages/{msg}/actions/{Pukki.MailAllow}", null);
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        Assert.True(Pukki.MailAllowed(space.Db));
        var answer = Chat.Messages(_fx.Id("ben"), room).Skip(before).Last(m => m.Sender == Pukki.Id).Text!;
        Assert.StartsWith("Danke! Zur Nachverfolgung markiert (1):", answer);
        Assert.Contains("Angebot Dachrinne – Anna", answer);
        Assert.Contains("fällig morgen", answer);
        Assert.DoesNotContain("Alter Kram", answer);

        var unread = Pukki.Answer(services, space, "Was ist ungelesen?", Chat, room).Text;
        Assert.StartsWith("Ungelesen im Posteingang: 2", unread);
        Assert.Contains("Treffen nächste Woche", unread);

        var anna = Pukki.Answer(services, space, "Mails von Anna", Chat, room).Text;
        Assert.StartsWith("Mails von Anna (2", anna);
        Assert.DoesNotContain("Rechnung", anna);

        // Schalter wie in den Einstellungen
        var get = await (await _fx.As("ben").GetAsync("/api/chat/pukki/mail")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(get.GetProperty("allowed").GetBoolean());
        (await _fx.As("ben").PutAsJsonAsync("/api/chat/pukki/mail", new { allowed = false })).EnsureSuccessStatusCode();
        Assert.StartsWith("Dafür müsste ich", Pukki.Answer(services, space, "Hab ich neue Mails?", Chat, room).Text);
    }

    [Fact]
    public void Nein_laesst_die_Mails_in_Ruhe_und_ohne_Konto_gibt_es_nichts()
    {
        var space = _fx.Space("cleo");
        Pukki.Ensure(Chat, _fx.Id("cleo"));
        var room = Chat.DirectRoom(_fx.Id("cleo"), Pukki.Id)!.Value;
        Assert.StartsWith("Du hast in dmOffice kein Mailkonto", Pukki.Answer(_fx.Factory.Services, space, "Welche Mails sind ungelesen?", Chat, room).Text);
        // Aufträge mit dem Wort Mail gehen ihren normalen Weg
        Assert.DoesNotContain("Mailkonto", Pukki.Answer(_fx.Factory.Services, space, "Erinnere mich morgen um 9 an die Mail von Bernd", Chat, room).Text);
        // Ein Satz ohne Frage nach Mails geht weiter wie bisher
        Assert.DoesNotContain("Mails", Pukki.Answer(_fx.Factory.Services, space, "Danke dir", Chat, room).Text);
    }
}
