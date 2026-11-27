using DmOffice.Core;
using DmOffice.Mail;

namespace DmOffice.Tests;

/// <summary>Ausgeschaltete Kennungen der Mail: weder Schnittstelle noch Sortierung oder Filter.</summary>
[Collection("server")]
public class MailFeaturePathTests
{
    private static void With(string id, bool on, Action run)
    {
        var before = Features.InReleaseOff.ToList();
        Features.Set(on ? before.Where(x => x != id) : before.Append(id).Distinct());
        try { run(); } finally { Features.Set(before); }
    }

    // [feature:catchalldomains]
    [Fact]
    public void Catchall_Domains_aus_sperrt_ihre_Wege()
    {
        With(Features.CatchallDomains, false, () =>
        {
            Assert.True(Features.Blocked("/api/mail/aliases/sites"));
            Assert.True(Features.Blocked("/api/mail/aliases/reply-from"));
            Assert.True(Features.Blocked("/api/mail/aliases/senders"));
        });
        With(Features.CatchallDomains, true, () =>
        {
            if (Features.On(Features.Catchall)) Assert.False(Features.Blocked("/api/mail/aliases/sites"));
        });
    }
    // [/feature:catchalldomains]

    // [feature:mailtocolumn]
    [Fact]
    public void Spalte_An_aus_sortiert_nach_Datum()
    {
        With(Features.MailToColumn, false, () => Assert.Equal("date DESC", MailStore.OrderBy("to")));
        With(Features.MailToColumn, true, () => Assert.Contains("mail_alias_hit", MailStore.OrderBy("to")));
    }
    // [/feature:mailtocolumn]
}
