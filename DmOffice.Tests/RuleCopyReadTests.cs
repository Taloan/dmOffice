using DmOffice.Core;
using DmOffice.Mail;
using MimeKit;
using Xunit;

namespace DmOffice.Tests;

/// <summary>Regelaktion „Kopieren nach“ mit „Kopie als gelesen markieren“ (Kennung rulecopyread).</summary>
public class RuleCopyReadTests
{
    [Fact]
    public void Kopie_als_gelesen_betrifft_nur_das_Ziel_nicht_das_Original()
    {
        var rule = new MailRule
        {
            Name = "Kopie", Conditions = [new RuleCondition { Field = "to", Op = "contains", Value = "@dmoffice.me" }],
            Actions = [new RuleAction { Type = "copy", Value = "k2|Archive", Read = true }, new RuleAction { Type = "copy", Value = "Ablage" }],
        };
        var m = new MimeMessage { Subject = "Test" };
        m.To.Add(new MailboxAddress("", "info@dmoffice.me"));

        var o = Rules.Evaluate([rule], "a", m);
        Assert.Equal(["k2|Archive", "Ablage"], o.CopyTo);
        Assert.Contains("k2|Archive", o.CopyRead);
        Assert.DoesNotContain("Ablage", o.CopyRead);
        Assert.False(o.MarkRead);
    }
}
