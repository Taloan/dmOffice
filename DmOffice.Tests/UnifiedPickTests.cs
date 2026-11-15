using DmOffice.Core;
using DmOffice.Mail;
using Xunit;

namespace DmOffice.Tests;

/// <summary>„Alle Posteingänge“: Konten lassen sich abwählen (Kennung „unifiedpick“), etwa ein Sammelpostfach.</summary>
public class UnifiedPickTests
{
    [Fact]
    public void Abgewaehltes_Konto_fehlt_in_Alle_Posteingaenge_nur_wenn_die_Funktion_frei_ist()
    {
        var settings = new UserSettings();
        settings.MailAccounts.Add(new MailAccount { Id = "privat", Email = "a@example.org" });
        settings.MailAccounts.Add(new MailAccount { Id = "sammel", Email = "support@example.org", InUnified = false });
        var before = Features.InReleaseOff.ToList();
        try
        {
            Features.Set(before.Where(x => x != Features.UnifiedPick));
            Assert.Equal(["privat"], MailStore.UnifiedAccounts(settings).Select(a => a.Id));
            Features.Set([.. before, Features.UnifiedPick]);
            Assert.Equal(["privat", "sammel"], MailStore.UnifiedAccounts(settings).Select(a => a.Id));
        }
        finally { Features.Set(before); }
        // Ältere Einstellungen ohne das Feld: dabei
        var old = System.Text.Json.JsonSerializer.Deserialize<MailAccount>("""{"Id":"x","Email":"x@example.org"}""")!;
        Assert.True(old.InUnified);
    }
}
