using DmOffice.Core;
using DmOffice.Pim;

namespace DmOffice.Tests;

public class RecentAddressTests
{
    private static UserSpace NewSpace()
    {
        var dir = Directory.CreateTempSubdirectory("dmrecent");
        var db = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "t");
        PimStore.EnsureDefaults(db);
        db.SetJson("mail.recent", new List<ContactStore.Recent>
        {
            new("Anna Schmidt", "anna@x.de", 3, DateTime.UtcNow),
            new("Müller, Max", "max@acme.de", 1, DateTime.UtcNow),
            new(null, "info@shop.de", 7, DateTime.UtcNow),
        });
        return new UserSpace(new UserRecord { Login = "t" }, db);
    }

    [Fact]
    public void Uebernehmen_teilt_Namen_und_ueberspringt_Vorhandene()
    {
        var space = NewSpace();
        var book = PimStore.Collections(space.Db, PimStore.AddressBook).First().Id;
        Assert.Equal(2, ContactStore.AddToBook(space, new[] { "anna@x.de", "max@acme.de" }, book));
        var contacts = ContactModel.All(space, new[] { book });
        Assert.Contains(contacts, c => c.Given == "Anna" && c.Family == "Schmidt");
        Assert.Contains(contacts, c => c.Given == "Max" && c.Family == "Müller");
        // Zweites Mal: steht schon im Adressbuch
        Assert.Equal(0, ContactStore.AddToBook(space, new[] { "ANNA@x.de" }, book));
        Assert.Contains("max@acme.de", ContactStore.KnownAddresses(space));
    }

    [Fact]
    public void Loeschen_nimmt_nur_aus_den_Vorschlaegen()
    {
        var space = NewSpace();
        Assert.Equal(2, ContactStore.Forget(space, new[] { "INFO@shop.de", "anna@x.de" }));
        Assert.Single(ContactStore.Recents(space));
    }
}
