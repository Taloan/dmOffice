using DmOffice.Journal;
using DmOffice.Pim;

namespace DmOffice.Tests;

/// <summary>Tagebuch-Vorschlag „Erledigt“: abgehakte Aufgaben des Tages, aber keine Artikel von Einkaufslisten.</summary>
[Collection("server")]
public class JournalDoneTasksTests : IClassFixture<ChatFixture>
{
    private readonly ChatFixture _fx;
    public JournalDoneTasksTests(ChatFixture fx) => _fx = fx;

    private static string Todo(string uid, string summary) =>
        $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:test\r\nBEGIN:VTODO\r\nUID:{uid}\r\nDTSTAMP:20261030T120000Z\r\nSUMMARY:{summary}\r\n" +
        "STATUS:COMPLETED\r\nCOMPLETED:20261030T120000Z\r\nEND:VTODO\r\nEND:VCALENDAR\r\n";

    [Fact]
    public void Erledigt_ohne_Einkaufsliste()
    {
        var space = _fx.Space("anna");
        PimStore.EnsureDefaults(space.Db);
        var tasks = PimStore.CreateCollection(space.Db, PimStore.Calendar, "erledigt-test", "Haushalt", null, "VTODO");
        var shop = PimStore.CreateCollection(space.Db, PimStore.Calendar, "einkauf-test", "Einkauf", null, "VTODO");
        Lists.Update(space.Db, shop.Id, Lists.Shop, null);
        PimStore.Put(space.Db, tasks, "muell.ics", Todo("muell-test", "Mülltonne rausstellen"));
        PimStore.Put(space.Db, shop, "sahne.ics", Todo("sahne-test", "Sahne"));

        var done = JournalApi.DoneTasks(space, new DateOnly(2026, 10, 30));
        Assert.Contains("Mülltonne rausstellen", done);
        Assert.DoesNotContain("Sahne", done);
    }
}
