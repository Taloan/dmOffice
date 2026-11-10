using DmOffice.Mail;

namespace DmOffice.Tests;

/// <summary>Was zum IMAP-Server unterwegs ist, laesst der Sync in Ruhe - auch bei mehreren Auftraegen fuer dieselbe Mail.</summary>
public class MailPendingTests
{
    [Fact]
    public void Zaehlt_mehrere_Auftraege_und_trennt_Fahne_von_Verschieben()
    {
        var flag = MailPending.Key("u1", "a", "INBOX", 7, removes: false);
        var move = MailPending.Key("u1", "a", "INBOX", 7, removes: true);
        Assert.False(MailPending.Flagging("u1", "a", "INBOX", 7));

        MailPending.Add(flag);
        MailPending.Add(flag);
        Assert.True(MailPending.Flagging("u1", "a", "INBOX", 7));
        Assert.False(MailPending.Removing("u1", "a", "INBOX", 7));
        Assert.False(MailPending.Flagging("u2", "a", "INBOX", 7));

        MailPending.Remove(flag);
        Assert.True(MailPending.Flagging("u1", "a", "INBOX", 7));
        MailPending.Remove(flag);
        Assert.False(MailPending.Flagging("u1", "a", "INBOX", 7));

        MailPending.Add(move);
        Assert.True(MailPending.Removing("u1", "a", "INBOX", 7));
        MailPending.Remove(move);
        Assert.False(MailPending.Removing("u1", "a", "INBOX", 7));
    }
}
