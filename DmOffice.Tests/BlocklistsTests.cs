using System.Net;
using DmOffice.Core;
using DmOffice.Mail;
using MimeKit;

namespace DmOffice.Tests;

/// <summary>Globale Spamlisten: Absender-Server aus Received, Domains, Antworten deuten, Treffer als Merkmal im Filter.</summary>
[Collection("server")]
public class BlocklistsTests
{
    private static MimeMessage Mail()
    {
        var m = new MimeMessage();
        m.From.Add(new MailboxAddress("Shop", "angebot@billig-shop.example"));
        m.To.Add(new MailboxAddress("Alex", "alex@beispiel.de"));
        m.Subject = "Nur heute";
        m.MessageId = Guid.NewGuid().ToString("N") + "@test";
        m.Headers.Add(HeaderId.Received, "from mx01.internal ([10.0.0.5]) by mxint.gmx.net with LMTP; Sat, 24 Oct 2026 10:00:00 +0200");
        m.Headers.Add(HeaderId.Received, "from spammer.example (unknown [203.0.113.9]) by mx.gmx.net ([212.227.15.1]) with ESMTPS; Sat, 24 Oct 2026 09:59:58 +0200");
        m.Body = new TextPart("html") { Text = "<a href=\"https://www.klick-hier.example/x\">Jetzt</a> <a href=\"http://news.shop.co.uk/a\">b</a>" };
        return m;
    }

    [Fact]
    public void Absender_Server_und_Domains()
    {
        var m = Mail();
        Assert.Equal(IPAddress.Parse("203.0.113.9"), Blocklists.SenderIp(m));
        Assert.Equal(["billig-shop.example", "klick-hier.example", "shop.co.uk"], Blocklists.Domains(m));
        Assert.True(Blocklists.IsListing(IPAddress.Parse("127.0.0.2")));
        Assert.False(Blocklists.IsListing(IPAddress.Parse("127.0.0.1")));       // URIBL/SURBL: abgelehnt
        Assert.False(Blocklists.IsListing(IPAddress.Parse("127.255.255.254"))); // Spamhaus: öffentlicher Resolver
    }

    [Fact]
    public async Task Treffer_werden_gemerkt_und_gelernt()
    {
        var dir = Directory.CreateTempSubdirectory("dmrbl");
        var db = new UserDb(Path.Combine(dir.FullName, "u.db"), new byte[32], "t");
        var asked = new List<string>();
        Blocklists.ResolveOverride = (name, _) =>
        {
            asked.Add(name);
            return Task.FromResult(name is "9.113.0.203.key123.zen.dq.spamhaus.net" or "klick-hier.example.multi.surbl.org"
                ? [IPAddress.Parse("127.0.0.2")] : Array.Empty<IPAddress>());
        };
        try
        {
            var m = Mail();
            var hits = await Blocklists.CheckAsync(db, m, ["zen", "surbl", "psbl"], "key123", CancellationToken.None);
            Assert.Equal(["zen", "surbl"], hits);
            Assert.Contains("9.113.0.203.psbl.surriel.com", asked);
            Assert.Equal(["rbl:zen", "rbl:surbl"], Blocklists.Tokens(db, m));
            // Junk gelernt: die Treffer zählen als Merkmal mit
            SpamFilter.Train(db, m, spam: true);
            Assert.Equal(1L, db.Scalar("SELECT spam FROM spam_tokens WHERE token='rbl:zen'"));
        }
        finally { Blocklists.ResolveOverride = null; }
    }
}
