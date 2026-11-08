using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace DmOffice.Tests;

/// <summary>„Rückgängig“ direkt nach dem Löschen von Kontakten und Terminen.</summary>
[Collection("server")]
public class PimUndoTests : IClassFixture<FilesServerFixture>
{
    private readonly FilesServerFixture _fx;
    public PimUndoTests(FilesServerFixture fx) => _fx = fx;

    private static async Task<long> FirstId(HttpClient c, string kind)
    {
        using var doc = JsonDocument.Parse(await c.GetStringAsync("/api/pim/collections/" + kind));
        return doc.RootElement[0].GetProperty("id").GetInt64();
    }

    [Fact]
    public async Task Geloeschter_Kontakt_kommt_mit_Rueckgaengig_zurueck()
    {
        var c = _fx.Client();
        var book = await FirstId(c, "addressbooks");
        const string card = "BEGIN:VCARD\r\nVERSION:3.0\r\nUID:undo-kontakt\r\nFN:Inge Beispiel\r\nEND:VCARD\r\n";
        (await c.PostAsJsonAsync($"/api/pim/contacts/{book}/undo-kontakt.vcf/restore", new { data = card })).EnsureSuccessStatusCode();
        var deleted = await c.DeleteAsync($"/api/pim/contacts/{book}/undo-kontakt.vcf");
        deleted.EnsureSuccessStatusCode();
        using var answer = JsonDocument.Parse(await deleted.Content.ReadAsStringAsync());
        var undo = answer.RootElement.GetProperty("undo").GetString();
        Assert.Contains("Inge Beispiel", undo);
        Assert.DoesNotContain("Inge Beispiel", await c.GetStringAsync("/api/pim/contacts"));
        (await c.PostAsJsonAsync($"/api/pim/contacts/{book}/undo-kontakt.vcf/restore", new { data = undo })).EnsureSuccessStatusCode();
        Assert.Contains("Inge Beispiel", await c.GetStringAsync("/api/pim/contacts"));
    }

    [Fact]
    public async Task Geloeschter_Termin_liefert_seinen_Stand_fuer_Rueckgaengig()
    {
        var c = _fx.Client();
        var calendar = await FirstId(c, "calendars");
        const string ics = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//test//EN\r\nBEGIN:VEVENT\r\nUID:undo-termin\r\nDTSTAMP:20261105T100000Z\r\nDTSTART:20261110T100000Z\r\nDTEND:20261110T110000Z\r\nSUMMARY:Zahnarzt\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        (await c.PostAsJsonAsync($"/api/pim/events/{calendar}/undo-termin.ics/restore", new { data = ics })).EnsureSuccessStatusCode();
        var deleted = await c.DeleteAsync($"/api/pim/events/{calendar}/undo-termin.ics");
        deleted.EnsureSuccessStatusCode();
        using var answer = JsonDocument.Parse(await deleted.Content.ReadAsStringAsync());
        var undo = answer.RootElement.GetProperty("undo").GetString();
        Assert.Contains("Zahnarzt", undo);
        (await c.PostAsJsonAsync($"/api/pim/events/{calendar}/undo-termin.ics/restore", new { data = undo })).EnsureSuccessStatusCode();
        Assert.Contains("Zahnarzt", await c.GetStringAsync($"/api/pim/events/{calendar}/undo-termin.ics/ics"));
    }
}
