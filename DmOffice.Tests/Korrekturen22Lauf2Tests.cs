using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DmOffice.Audio;
using DmOffice.Core;
using DmOffice.Dms;
using DmOffice.Mail;
using DmOffice.Storage;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;

namespace DmOffice.Tests;

/// <summary>Korrekturen aus Lauf 2 der Anwender-Simulation 2.2 (Befunde 2, 3, 4, 5, 6, 8).</summary>
[Collection("server")]
public class Korrekturen22Lauf2Tests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public Korrekturen22Lauf2Tests(ChatFixture fx) => _fx = fx;

    private async Task<HttpClient> Login(string login)
    {
        var c = _fx.Factory.CreateClient(new() { HandleCookies = true });
        Assert.True((await c.PostAsJsonAsync("/api/auth/login", new { login, password = "geheim-123456" })).IsSuccessStatusCode);
        return c;
    }


    private static MailRule Rule(string type, string value) =>
        new() { Name = "R", Conditions = [new() { Field = "subject", Op = "contains", Value = "x" }], Actions = [new() { Type = type, Value = value }] };

    // Befund 3: ungültige Adressen werden nicht still verworfen, sondern abgelehnt
    [Fact]
    public void Weiterleitung_nur_an_gueltige_Adressen()
    {
        foreach (var bad in new[] { "kein@@unsinn", "@", "a@b", "name@", "@beispiel.de", "a b@c.de" })
        {
            Assert.False(ForwardTargets.IsAddress(bad), bad);
            Assert.NotNull(Rules.Problem(Rule("forward", bad)));
        }
        foreach (var good in new[] { "name@beispiel.de", "Max.Muster+tag@sub.example.org", "jörg@müller.de" }) Assert.True(ForwardTargets.IsAddress(good), good);
        Assert.Equal(["kaputt@"], ForwardTargets.Invalid("ok@example.org, kaputt@"));
        Assert.Null(Rules.Problem(Rule("forward", "ok@example.org; zwei@example.org")));
        Assert.NotNull(Rules.Problem(Rule("redirect", "ok@example.org, @")));
    }

    // Befund 4: Tage außerhalb 1-3650 abgelehnt
    [Fact]
    public void Loeschfrist_nur_1_bis_3650_Tage()
    {
        foreach (var bad in new[] { "-5", "0", "3651", "abc", "" })
            Assert.NotNull(Rules.Problem(Rule("trashAfter", bad)));
        Assert.NotNull(Rules.Problem(Rule("destroyAfter", "-1")));
        Assert.Null(Rules.Problem(Rule("trashAfter", "1")));
        Assert.Null(Rules.Problem(Rule("destroyAfter", "3650")));
    }

    [Fact]
    public async Task Regeln_speichern_lehnt_ungueltige_Werte_ab()
    {
        var before = Features.InReleaseOff.ToList();
        Features.Set(before.Where(x => x != Features.RuleForward));
        try
        {
            var ben = await Login("ben");
            async Task<HttpResponseMessage> Put(MailRule r) => await ben.PutAsJsonAsync("/api/settings/rules", new { rules = new[] { r }, password = "geheim-123456" });
            var r1 = await Put(Rule("forward", "kein@@unsinn"));
            Assert.Equal(HttpStatusCode.BadRequest, r1.StatusCode);
            Assert.Contains("kein@@unsinn", await r1.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.BadRequest, (await Put(Rule("trashAfter", "-5"))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await Put(Rule("trashAfter", "30"))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await ben.PutAsJsonAsync("/api/settings/rules", new { rules = Array.Empty<MailRule>(), password = "geheim-123456" })).StatusCode);
        }
        finally { Features.Set(before); }
    }

    private static byte[] RawMail(string headers) =>
        Encoding.ASCII.GetBytes(headers.Replace("\n", "\r\n") + "\r\n\r\nHallo Welt, das ist der Text.\r\n");

    // Befund 5: Mail ohne Datum - kein „01.01.0001“ im PDF, kein Datum in den Angaben
    [Fact]
    public void Mail_ohne_Datum_zeigt_kein_Jahr_1()
    {
        var eml = RawMail("From: a@example.org\nTo: b@example.org\nSubject: Ohne Datum\nMessage-ID: <x1@example.org>\nMIME-Version: 1.0\nContent-Type: text/plain; charset=utf-8");
        var m = MimeMessage.Load(new MemoryStream(eml));
        Assert.Null(DmsMailPdf.DateOf(m));
        var pdf = DmsMailPdf.Make(eml, h => h, default);
        var text = Search.TextExtract.Extract(pdf, "m.pdf", "application/pdf", ocr: false).Text;
        Assert.DoesNotContain("0001", text);
        // Textauszug aus dem PDF fehlt in manchen Testumgebungen (siehe DmsMailTests) - dann nur das Fehlen prüfen
        if (text.Length > 0) Assert.Contains("Ohne Datum", text);
    }

    // Befund 6: kaputt kodierter Betreff - kein Ersatzzeichen, Titel fällt auf den Dateinamen zurück
    [Fact]
    public void Kaputter_Betreff_ohne_Ersatzzeichen()
    {
        var eml = RawMail("From: a@example.org\nTo: b@example.org\nSubject: =?utf-8?B?//79/w==?=\nDate: Sat, 14 Mar 2026 09:00:00 +0000\nMessage-ID: <x2@example.org>\nMIME-Version: 1.0\nContent-Type: text/plain; charset=utf-8");
        var m = MimeMessage.Load(new MemoryStream(eml));
        Assert.Null(DmsMailPdf.SubjectOf(m));
        Assert.NotNull(DmsMailPdf.DateOf(m));
        var text = Search.TextExtract.Extract(DmsMailPdf.Make(eml, h => h, default), "m.pdf", "application/pdf", ocr: false).Text;
        Assert.DoesNotContain("�", text);
        if (text.Length > 0) Assert.Contains("Hallo Welt", text);
        // Gültiger Betreff bleibt
        var ok = MimeMessage.Load(new MemoryStream(RawMail("From: a@example.org\nSubject: =?utf-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes("Grüße")) + "?=\nMIME-Version: 1.0\nContent-Type: text/plain")));
        Assert.Equal("Grüße", DmsMailPdf.SubjectOf(ok));
    }

    // Befund 8: Paarung verschobener/umbenannter Dateien
    [Fact]
    public void Verschiebungen_werden_nur_eindeutig_erkannt()
    {
        // Umbenannt im selben Ordner, gleiche Größe und Zeit
        Assert.Equal([("A/x.mp3", "A/y.mp3", true)], AudioFolders.Moves([("A/x.mp3", 100, 5)], [("A/y.mp3", 100, 5)]));
        // Verschoben in anderen Ordner, Zeit neu, Name gleich
        Assert.Equal([("A/x.mp3", "B/x.mp3", false)], AudioFolders.Moves([("A/x.mp3", 100, 5)], [("B/x.mp3", 100, 9)]));
        // Anderer Ordner, anderer Name, andere Zeit: keine Zuordnung
        Assert.Empty(AudioFolders.Moves([("A/x.mp3", 100, 5)], [("B/y.mp3", 100, 9)]));
        // Andere Größe: keine Zuordnung
        Assert.Empty(AudioFolders.Moves([("A/x.mp3", 100, 5)], [("A/y.mp3", 101, 5)]));
        // Zwei gleich große mit gleicher Zeit, nur über eindeutige Namen
        var two = AudioFolders.Moves([("A/x.mp3", 100, 5), ("A/z.mp3", 100, 5)], [("B/x.mp3", 100, 5), ("B/z.mp3", 100, 5)]);
        Assert.Equal([("A/x.mp3", "B/x.mp3", true), ("A/z.mp3", "B/z.mp3", true)], two.OrderBy(p => p.From).ToList());
        Assert.Empty(AudioFolders.Moves([("A/x.mp3", 100, 5), ("A/z.mp3", 100, 5)], [("B/p.mp3", 100, 5), ("B/q.mp3", 100, 5)]));
    }

    [Fact]
    public async Task Umbenannter_Titel_behaelt_Playlist_und_Zaehler()
    {
        var config = _fx.Factory.Services.GetRequiredService<ServerConfig>();
        var root = Directory.CreateTempSubdirectory("dmaudio").FullName;
        var before = config.Current.ExternalFolders;
        var wasOff = config.Current.ModulesOff;
        config.Update(s => { s.ExternalFolders = [new ExternalFolderDef { Id = "musik22", Name = "Musik", Path = root }]; s.ModulesOff = []; });
        var cleo = _fx.Space("cleo");
        var client = _fx.As("cleo");
        try
        {
            File.WriteAllBytes(Path.Combine(root, "alt.mp3"), AudioTests.Mp3(400));
            File.WriteAllBytes(Path.Combine(root, "anders.mp3"), AudioTests.Mp3(300));
            await client.PostAsJsonAsync("/api/audio/sources", new { mount = "musik22", path = "" });
            var src = AudioFolders.Of(cleo.Db).Single(x => x.Mount == "musik22");
            await AudioFolders.ScanAsync(cleo, config, src, CancellationToken.None);
            await AudioFolders.TagsAsync(cleo, config, src, DateTime.UtcNow.AddMinutes(1), CancellationToken.None);
            var pl = (await (await client.PostAsJsonAsync("/api/audio/playlists", new { name = "Lauf2", tracks = new[] { src.Id + "/alt.mp3", src.Id + "/anders.mp3" } }))
                .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
            AudioFolders.Played(cleo, config, src.Id + "/alt.mp3", 10_000, null, DateTime.UtcNow);

            Directory.CreateDirectory(Path.Combine(root, "Neu"));
            File.Move(Path.Combine(root, "alt.mp3"), Path.Combine(root, "Neu", "alt.mp3"));
            await AudioFolders.ScanAsync(cleo, config, src, CancellationToken.None);

            var tracks = (await client.GetFromJsonAsync<JsonElement>($"/api/audio/playlists/{pl}")).GetProperty("tracks").EnumerateArray().Select(x => x.GetString()!).ToList();
            Assert.Equal([src.Id + "/Neu/alt.mp3", src.Id + "/anders.mp3"], tracks);
            var most = (await client.GetFromJsonAsync<JsonElement>("/api/audio/playlists/auto/most")).GetProperty("tracks").EnumerateArray().Select(x => x.GetString()!).ToList();
            Assert.Equal(src.Id + "/Neu/alt.mp3", most[0]);
            Assert.DoesNotContain(src.Id + "/alt.mp3", most);

            // Umbenennen (Zeit bleibt): Titel aus dem Dateinamen folgt dem neuen Namen, Playlist bleibt
            File.Move(Path.Combine(root, "Neu", "alt.mp3"), Path.Combine(root, "Neu", "Neuer Name.mp3"));
            await AudioFolders.ScanAsync(cleo, config, src, CancellationToken.None);
            var list = await client.GetFromJsonAsync<JsonElement>("/api/audio/tracks");
            var fields = list.GetProperty("fields").EnumerateArray().Select(f => f.GetString()!).ToList();
            var row = list.GetProperty("tracks").EnumerateArray().Single(t => t[fields.IndexOf("id")].GetString() == src.Id + "/Neu/Neuer Name.mp3");
            Assert.Equal("Neuer Name", row[fields.IndexOf("title")].GetString());
            Assert.Equal("Neu", row[fields.IndexOf("album")].GetString());
            tracks = (await client.GetFromJsonAsync<JsonElement>($"/api/audio/playlists/{pl}")).GetProperty("tracks").EnumerateArray().Select(x => x.GetString()!).ToList();
            Assert.Equal([src.Id + "/Neu/Neuer Name.mp3", src.Id + "/anders.mp3"], tracks);

            await client.DeleteAsync($"/api/audio/playlists/{pl}");
            await client.DeleteAsync("/api/audio/sources/" + src.Id);
        }
        finally
        {
            config.Update(s => { s.ExternalFolders = before; s.ModulesOff = wasOff; });
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    // Befund 9: Dateien ohne Ton (Bild, Text, leer) mit Audio-Endung erscheinen nicht als Titel
    [Fact]
    public async Task Dateien_ohne_Ton_werden_keine_Titel()
    {
        Assert.True(AudioFolders.LooksLikeAudio(AudioTests.Mp3(1).AsSpan(0, 12)));
        Assert.True(AudioFolders.LooksLikeAudio("ID3\u0003\0\0\0\0"u8));
        Assert.False(AudioFolders.LooksLikeAudio("Hallo Welt, kein Ton"u8));
        Assert.False(AudioFolders.LooksLikeAudio(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }));
        var config = _fx.Factory.Services.GetRequiredService<ServerConfig>();
        var root = Directory.CreateTempSubdirectory("dmaudio").FullName;
        var before = config.Current.ExternalFolders;
        var wasOff = config.Current.ModulesOff;
        config.Update(s => { s.ExternalFolders = [new ExternalFolderDef { Id = "musik23", Name = "Musik", Path = root }]; s.ModulesOff = []; });
        var cleo = _fx.Space("cleo");
        var client = _fx.As("cleo");
        try
        {
            File.WriteAllBytes(Path.Combine(root, "echt.mp3"), AudioTests.Mp3(400));
            File.WriteAllText(Path.Combine(root, "text.mp3"), "Hallo Welt, kein Ton");
            File.WriteAllBytes(Path.Combine(root, "bild.flac"), new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D });
            File.WriteAllBytes(Path.Combine(root, "leer.mp3"), []);
            await client.PostAsJsonAsync("/api/audio/sources", new { mount = "musik23", path = "" });
            var src = AudioFolders.Of(cleo.Db).Single(x => x.Mount == "musik23");
            await AudioFolders.ScanAsync(cleo, config, src, CancellationToken.None);
            await AudioFolders.TagsAsync(cleo, config, src, DateTime.UtcNow.AddMinutes(1), CancellationToken.None);
            var list = await client.GetFromJsonAsync<JsonElement>("/api/audio/tracks");
            var ids = list.GetProperty("tracks").EnumerateArray().Select(t => t[0].GetString()!).Where(i => i.StartsWith(src.Id + "/")).ToList();
            Assert.Equal([src.Id + "/echt.mp3"], ids);
            // Nochmal eingelesen: Anzahl wie in der Titelliste
            Assert.Equal(1, await AudioFolders.ScanAsync(cleo, config, src, CancellationToken.None));
            var r = await client.GetAsync("/api/audio/stream?id=" + Uri.EscapeDataString(src.Id + "/text.mp3"));
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, r.StatusCode);
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await client.GetAsync("/api/audio/stream?id=" + Uri.EscapeDataString(src.Id + "/leer.mp3"))).StatusCode);
            await client.DeleteAsync("/api/audio/sources/" + src.Id);
        }
        finally
        {
            config.Update(s => { s.ExternalFolders = before; s.ModulesOff = wasOff; });
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
