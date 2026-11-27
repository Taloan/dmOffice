using DmOffice.Core;
using DmOffice.Mail;
using Xunit;

namespace DmOffice.Tests;

/// <summary>„Kein Betrug“: Die Warnung bleibt für diese eine Mail ausgeblendet, ein Virenfund bleibt sichtbar.</summary>
public class MailPhishDismissedTests
{
    [Fact]
    public void Ausgeblendet_nur_fuer_diese_Mail_Virus_bleibt()
    {
        var dir = Directory.CreateTempSubdirectory("dmphish");
        var db = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "t");
        List<PhishCheck.Finding> found = [new("auth", true, Detail: "dmarc"), new("virus", true, Detail: "EICAR")];
        Assert.Equal(2, PhishDismissed.Apply(db, "<a@shop.example>", found)!.Count);
        PhishDismissed.Mark(db, "<a@shop.example>");
        Assert.Equal("virus", Assert.Single(PhishDismissed.Apply(db, "<a@shop.example>", found)!).Kind);
        Assert.Null(PhishDismissed.Apply(db, "<a@shop.example>", [new("auth", true, Detail: "dmarc")]));
        Assert.Equal(2, PhishDismissed.Apply(db, "<b@shop.example>", found)!.Count);
    }
}
