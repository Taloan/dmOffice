using DmOffice.Core;
using DmOffice.Mail;

namespace DmOffice.Tests;

/// <summary>Kontoeinrichtung: Thunderbird-Autoconfig lesen, Basis-Domain aus dem MX.</summary>
public class MailDiscoveryTests
{
    private const string Xml = """
        <clientConfig version="1.1"><emailProvider id="example.org">
          <incomingServer type="pop3"><hostname>pop.example.org</hostname><port>995</port><socketType>SSL</socketType><username>%EMAILADDRESS%</username></incomingServer>
          <incomingServer type="imap"><hostname>imap.example.org</hostname><port>143</port><socketType>STARTTLS</socketType><username>%EMAILLOCALPART%</username></incomingServer>
          <incomingServer type="imap"><hostname>imap.example.org</hostname><port>993</port><socketType>SSL</socketType><username>%EMAILLOCALPART%</username></incomingServer>
          <outgoingServer type="smtp"><hostname>smtp.%EMAILDOMAIN%</hostname><port>25</port><socketType>plain</socketType></outgoingServer>
          <outgoingServer type="smtp"><hostname>smtp.%EMAILDOMAIN%</hostname><port>587</port><socketType>STARTTLS</socketType></outgoingServer>
        </emailProvider></clientConfig>
        """;

    [Fact]
    public void Liest_Autoconfig_SSL_vor_STARTTLS_nie_Klartext()
    {
        var r = MailDiscovery.Parse(Xml, "anna@example.org");
        Assert.NotNull(r);
        Assert.Equal(("imap.example.org", 993, Security.Ssl, "anna"), (r!.ImapHost, r.ImapPort, r.ImapSecurity, r.UserName));
        Assert.Equal(("smtp.example.org", 587, Security.StartTls), (r.SmtpHost, r.SmtpPort, r.SmtpSecurity));
    }

    [Fact]
    public void Kaputtes_oder_leeres_XML_ergibt_nichts()
    {
        Assert.Null(MailDiscovery.Parse("", "a@b.de"));
        Assert.Null(MailDiscovery.Parse("<html>404</html>", "a@b.de"));
        Assert.Null(MailDiscovery.Parse("<clientConfig><emailProvider/></clientConfig>", "a@b.de"));
    }

    [Theory]
    [InlineData("mx00.ionos.de", "ionos.de")]
    [InlineData("aspmx.l.google.com", "google.com")]
    [InlineData("mx1.example.co.uk", "example.co.uk")]
    [InlineData("mail.de", "mail.de")]
    public void Basis_Domain(string host, string expected) => Assert.Equal(expected, MailDiscovery.BaseDomain(host));

    [Theory]
    [InlineData("anna@live.de", "outlook.office365.com", true, "microsoft")]
    [InlineData("anna@live.de", "outlook.office365.com", false, "password")]
    [InlineData("anna@gmail.com", "imap.gmail.com", true, "app-password")]
    [InlineData("anna@firma.de", "imap.gmail.com", false, "app-password")]
    [InlineData("anna@icloud.com", "imap.mail.me.com", false, "app-password")]
    [InlineData("anna@gmx.de", "imap.gmx.net", true, "password")]
    public void Anmeldeart_aus_Anbieter(string email, string imapHost, bool oauth, string expected) =>
        Assert.Equal(expected, MailDiscovery.AuthFor(email,
            new MailDiscovery.Result("known", imapHost, 993, Security.Ssl, "smtp.x", 587, Security.StartTls, email, true), oauth));
}

/// <summary>Microsoft-Anmeldung: Vorgabe ist die App-Registrierung des Projekts.</summary>
public class MicrosoftClientIdTests
{
    [Theory]
    [InlineData(null, MicrosoftOAuth.ProjectClientId)]
    [InlineData("", MicrosoftOAuth.ProjectClientId)]
    [InlineData("  ", MicrosoftOAuth.ProjectClientId)]
    [InlineData("11111111-2222-3333-4444-555555555555", "11111111-2222-3333-4444-555555555555")]
    [InlineData("off", null)]
    [InlineData("None", null)]
    public void Client_ID(string? env, string? expected) => Assert.Equal(expected, MicrosoftOAuth.ClientIdFrom(env));
}
