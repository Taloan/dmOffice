using DmOffice.Core;
using Microsoft.AspNetCore.Http;

namespace DmOffice.Tests;

/// <summary>Demo: keine Dateien auf keinem Weg (ein gemeinsames Konto für alle Besucher).</summary>
public class DemoUploadGuardTests
{
    private static HttpRequest Req(string method, string path, string? type = null, long? length = null)
    {
        var c = new DefaultHttpContext();
        c.Request.Method = method;
        c.Request.Path = path;
        if (type != null) c.Request.ContentType = type;
        c.Request.ContentLength = length;
        return c.Request;
    }

    [Theory]
    [InlineData("POST", "/api/chat/rooms/5/upload", "application/json", 10L)]
    [InlineData("POST", "/api/files/uploads", "application/json", 50L)]
    [InlineData("PUT", "/api/files/uploads/abc/1", "application/json", 50L)]
    [InlineData("POST", "/api/dms/upload", "multipart/form-data; boundary=x", 900L)]
    [InlineData("POST", "/api/notes/1/attachments", "multipart/form-data; boundary=x", 100L)]
    [InlineData("POST", "/api/mail/a/drafts", "application/json", 300_000L)]
    [InlineData("POST", "/api/files/something", "application/octet-stream", 10L)]
    [InlineData("PUT", "/remote.php/dav/files/demo/virus.exe", "application/x-msdownload", 10L)]
    [InlineData("MKCOL", "/remote.php/dav/files/demo/neu", null, 0L)]
    [InlineData("PUT", "/dav/calendars/demo/x.ics", "text/calendar", 100L)]
    public void Gesperrt(string method, string path, string? type, long? length) =>
        Assert.True(Demo.CarriesFile(Req(method, path, type, length)));

    [Theory]
    [InlineData("POST", "/api/chat/rooms/5/messages", "application/json", 200L)]
    [InlineData("POST", "/api/auth/login", "application/json", 80L)]
    [InlineData("PUT", "/api/pim/events/1", "application/json", 2_000L)]
    [InlineData("POST", "/api/e2e/user/device", "application/octet-stream", 5_000_000L)]
    public void Erlaubt(string method, string path, string? type, long? length) =>
        Assert.False(Demo.CarriesFile(Req(method, path, type, length)));
}
