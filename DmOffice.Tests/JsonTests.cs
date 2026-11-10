using System.Text.Json;
using DmOffice.Pim;

namespace DmOffice.Tests;

public class JsonTests
{
    [Fact]
    public void ContactDto_aus_Oberflaeche()
    {
        var json = """{"book":2,"given":"Max","family":"Müller","emails":[{"type":"work","value":"max@acme.de"}]}""";
        var c = JsonSerializer.Deserialize<ContactDto>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("max@acme.de", c.Emails[0].Value);
    }
}
