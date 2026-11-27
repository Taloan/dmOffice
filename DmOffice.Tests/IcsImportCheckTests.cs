using DmOffice.Pim;

namespace DmOffice.Tests;

/// <summary>Kalender-Import: Termine mit unlesbaren Zeiten werden nicht uebernommen, sondern gezaehlt.</summary>
public class IcsImportCheckTests
{
    private static string Cal(string body) => $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\n{body}END:VCALENDAR\r\n";

    [Theory]
    [InlineData("BEGIN:VEVENT\r\nUID:a\r\nDTSTART:20270105T100000Z\r\nDTEND:20270105T110000Z\r\nEND:VEVENT\r\n", true)]
    [InlineData("BEGIN:VEVENT\r\nUID:a\r\nDTSTART;VALUE=DATE:20270105\r\nEND:VEVENT\r\n", true)]
    [InlineData("BEGIN:VTODO\r\nUID:a\r\nSUMMARY:ohne Datum\r\nEND:VTODO\r\n", true)]
    [InlineData("BEGIN:VEVENT\r\nUID:a\r\nDTSTART:20261299T250000Z\r\nEND:VEVENT\r\n", false)]
    [InlineData("BEGIN:VEVENT\r\nUID:a\r\nDTSTART:20270105T100000Z\r\nDTEND:kaputt\r\nEND:VEVENT\r\n", false)]
    [InlineData("BEGIN:VEVENT\r\nUID:a\r\nSUMMARY:ohne Beginn\r\nEND:VEVENT\r\n", false)]
    [InlineData("BEGIN:VTODO\r\nUID:a\r\nDUE:20271340\r\nEND:VTODO\r\n", false)]
    [InlineData("BEGIN:VEVENT\r\nUID:a\r\nDTSTART:20270105T100000Z\r\nSUMMARY:{long}\r\nEND:VEVENT\r\n", false)]
    public void Zeiten_lesbar(string body, bool ok) => Assert.Equal(ok, PimApi.Importable(Cal(body.Replace("{long}", new string('x', 5000)))));
}
