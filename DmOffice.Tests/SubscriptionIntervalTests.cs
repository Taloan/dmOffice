using DmOffice.Pim;

namespace DmOffice.Tests;

/// <summary>Kalenderabos: Abstand einschließlich „nur von Hand“.</summary>
public class SubscriptionIntervalTests
{
    [Theory]
    [InlineData(0, 0)]        // nur von Hand
    [InlineData(null, 360)]   // Vorgabe
    [InlineData(1, 15)]
    [InlineData(60, 60)]
    [InlineData(99999, 10080)]
    public void Abstand_aus_der_Eingabe(int? input, int expected) => Assert.Equal(expected, Subscriptions.IntervalOf(input));

    [Fact]
    public void Terminkalender_entsteht_auch_wenn_zuerst_ein_Tagebuch_angelegt_wurde()
    {
        var dir = Directory.CreateTempSubdirectory("dmpimdef");
        try
        {
            var db = new DmOffice.Core.UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "t", Path.Combine(dir.FullName, "blobs"));
            PimStore.CreateCollection(db, PimStore.Calendar, "tagebuch", "Mein Tagebuch", "#7B1DD3", "VJOURNAL");
            PimStore.EnsureDefaults(db);
            Assert.Contains(PimStore.Collections(db, PimStore.Calendar), c => c.Components.Contains("VEVENT"));
            // Wer ihn später löscht, bekommt ihn nicht wieder
            var personal = PimStore.Collections(db, PimStore.Calendar).Single(c => c.Components.Contains("VEVENT"));
            db.Exec("DELETE FROM collections WHERE id = $i", ("$i", personal.Id));
            PimStore.EnsureDefaults(db);
            Assert.DoesNotContain(PimStore.Collections(db, PimStore.Calendar), c => c.Components.Contains("VEVENT"));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { dir.Delete(true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Nur_von_Hand_wird_einmal_geholt_und_dann_nicht_mehr()
    {
        var now = DateTime.UtcNow;
        Assert.True(Subscriptions.Due(new Subscription { Interval = 0, LastFetch = null }, now));
        Assert.False(Subscriptions.Due(new Subscription { Interval = 0, LastFetch = now.AddYears(-3) }, now));
    }

    [Fact]
    public void Mit_Abstand_erst_wenn_er_abgelaufen_ist()
    {
        var now = DateTime.UtcNow;
        Assert.True(Subscriptions.Due(new Subscription { Interval = 60, LastFetch = null }, now));
        Assert.False(Subscriptions.Due(new Subscription { Interval = 60, LastFetch = now.AddMinutes(-30) }, now));
        Assert.True(Subscriptions.Due(new Subscription { Interval = 60, LastFetch = now.AddMinutes(-61) }, now));
    }
}
