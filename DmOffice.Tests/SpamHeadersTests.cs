using DmOffice.Mail;
using MimeKit;
using Xunit;

namespace DmOffice.Tests;

/// <summary>Urteil des eigenen Mailservers als Merkmal; vom Absender eingeschmuggelte Zeilen zählen nicht.</summary>
public class SpamHeadersTests
{
    private static MimeMessage Parse(string headers) =>
        MimeMessage.Load(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(headers.Replace("\n", "\r\n") + "\r\n\r\nHallo\r\n")));

    [Fact]
    public void SpamAssassin_beim_eigenen_Server()
    {
        var m = Parse("""
            X-Spam-Flag: YES
            X-Spam-Status: Yes, score=7.4 required=5.0 tests=BAYES_99
            Received: from mx.extern.example by mail.meine-domain.de
            Received: from sender.example by mx.extern.example
            From: a@b.example
            Subject: Test
            """);
        Assert.Equal(["srv-spam:yes", "srv-score:high"], SpamHeaders.Tokens(m));
    }

    [Fact]
    public void Eingeschmuggeltes_Urteil_zaehlt_nicht()
    {
        var m = Parse("""
            Received: from mx.extern.example by mail.meine-domain.de
            Received: from sender.example by mx.extern.example
            X-Spam-Status: No, score=-5.0
            X-Spam-Flag: NO
            From: a@b.example
            Subject: Test
            """);
        Assert.Empty(SpamHeaders.Tokens(m));
    }

    [Fact]
    public void Rspamd_und_Microsoft()
    {
        Assert.Contains("srv-score:mid", SpamHeaders.Tokens(Parse("X-Spamd-Result: default: False [3.20 / 15.00];\nFrom: a@b.example\nSubject: x")));
        Assert.Contains("srv-scl:high", SpamHeaders.Tokens(Parse("X-Forefront-Antispam-Report: CIP:1.2.3.4;SCL:5;SRV:;\nFrom: a@b.example\nSubject: x")));
        Assert.Contains("srv-score:low", SpamHeaders.Tokens(Parse("X-Spam-Level: *\nFrom: a@b.example\nSubject: x")));
    }
}
