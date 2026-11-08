using DmOffice.Core;

namespace DmOffice.Tests;

/// <summary>Benachrichtigung je Kategorie (Settings/Categories.Silenced): still nur, wenn alle Kategorien der Mail still sind.</summary>
public class CategoryNotifyTests
{
    [Fact]
    public void Still_nur_wenn_alle_Kategorien_der_Mail_ohne_Benachrichtigung()
    {
        var s = new UserSettings { Categories = [new() { Key = "dm_newsletter", Name = "Newsletter", Quiet = true }, new() { Key = "dm_wichtig", Name = "Wichtig" }] };
        Assert.True(DmOffice.Settings.Categories.Silenced(s, ["DM_Newsletter"]));
        Assert.False(DmOffice.Settings.Categories.Silenced(s, ["dm_newsletter", "dm_wichtig"]));
        Assert.False(DmOffice.Settings.Categories.Silenced(s, []));
        Assert.False(DmOffice.Settings.Categories.Silenced(s, ["dm_unbekannt"]));
        Assert.False(DmOffice.Settings.Categories.Silenced(s, null));
    }
}
