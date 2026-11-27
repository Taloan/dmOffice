using System.Net;
using System.Text;
using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Dms;
using DmOffice.Storage;
using Microsoft.AspNetCore.Http;

namespace DmOffice.Tests;

/// <summary>Ausschnitte und Inhalte, die ganz in den Speicher gelesen werden: nie mehr als verlangt bzw. die Grenze.</summary>
public class RemoteReadLimitsTests
{
    /// <summary>Strom ohne Ende, der mitzählt, wie viel gelesen wurde.</summary>
    private sealed class EndlessStream : Stream
    {
        public long Consumed;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => Consumed; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            for (var i = 0; i < count; i++) buffer[offset + i] = (byte)((Consumed + i) % 251);
            Consumed += count;
            return count;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>OneDrive, das den Ausschnitt nicht beachtet: antwortet mit 200 und einem Inhalt ohne Ende.</summary>
    private sealed class IgnoresRange(EndlessStream body, HttpStatusCode code) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(code) { Content = new StreamContent(body) });
    }

    [Fact]
    public async Task OneDrive_Ausschnitt_liest_ohne_206_nur_bis_zum_Ende_des_Ausschnitts()
    {
        var body = new EndlessStream();
        using var http = new HttpClient(new IgnoresRange(body, HttpStatusCode.OK));
        var d = new OneDriveDriver(_ => Task.FromResult("t"), http);
        var got = await d.ReadRangeAsync("a.bin", 1000, 16, CancellationToken.None);
        Assert.Equal(16, got.Length);
        Assert.Equal((byte)(1000 % 251), got[0]);
        Assert.Equal((byte)(1015 % 251), got[15]);
        // Gelesen: Anfang überspringen und der Ausschnitt, nicht die ganze Antwort
        Assert.True(body.Consumed < 1000 + 16 + 200_000, $"gelesen: {body.Consumed}");
    }

    [Fact]
    public async Task OneDrive_Ausschnitt_mit_206_schneidet_ueberlangen_Inhalt_ab()
    {
        var body = new EndlessStream();
        using var http = new HttpClient(new IgnoresRange(body, HttpStatusCode.PartialContent));
        var d = new OneDriveDriver(_ => Task.FromResult("t"), http);
        var got = await d.ReadRangeAsync("a.bin", 5_000_000, 8, CancellationToken.None);
        Assert.Equal(8, got.Length);
        Assert.Equal((byte)0, got[0]);
        Assert.True(body.Consumed < 200_000, $"gelesen: {body.Consumed}");
    }

    [Fact]
    public async Task RangeRead_kurzer_Inhalt_liefert_was_da_ist()
    {
        Assert.Equal(new byte[] { 3, 4 }, await RangeRead.TakeAsync(new MemoryStream([1, 2, 3, 4]), 2, 10, CancellationToken.None));
        Assert.Empty(await RangeRead.TakeAsync(new MemoryStream([1, 2]), 5, 10, CancellationToken.None));
    }

    [Fact]
    public async Task DMS_Dokument_aus_externem_Ordner_wird_hoechstens_bis_zur_Grenze_gelesen()
    {
        var body = new EndlessStream();
        var e = await Assert.ThrowsAsync<ArgumentException>(() => DmsHost.ReadDocumentAsync(body, CancellationToken.None, 1024 * 1024));
        Assert.Contains("zu groß", e.Message);
        Assert.True(body.Consumed <= 1024 * 1024 + 81920, $"gelesen: {body.Consumed}");
        Assert.Equal(5, (await DmsHost.ReadDocumentAsync(new MemoryStream(new byte[5]), CancellationToken.None, 1024)).Length);
    }

    private static DefaultHttpContext Request(Stream body, long? length)
    {
        var http = new DefaultHttpContext();
        http.Request.Body = body;
        http.Request.ContentLength = length;
        return http;
    }

    [Fact]
    public async Task DAV_PUT_ohne_Laengenangabe_liest_hoechstens_die_Grenze()
    {
        var body = new EndlessStream();
        await Assert.ThrowsAsync<ReadLimit.TooLargeException>(() => DavServer.BodyBytes(Request(body, null), 100_000));
        Assert.True(body.Consumed <= 100_000 + 81920, $"gelesen: {body.Consumed}");
    }

    [Fact]
    public async Task DAV_PUT_mit_zu_grosser_Angabe_liest_gar_nicht()
    {
        var body = new EndlessStream();
        await Assert.ThrowsAsync<ReadLimit.TooLargeException>(() => DavServer.BodyBytes(Request(body, DavServer.MaxPimObject + 1), DavServer.MaxPimObject));
        Assert.Equal(0, body.Consumed);
    }

    [Fact]
    public async Task DAV_PUT_kleiner_Eintrag_kommt_ganz_an()
    {
        var ics = Encoding.UTF8.GetBytes("BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n");
        Assert.Equal(ics, await DavServer.BodyBytes(Request(new MemoryStream(ics), ics.Length), DavServer.MaxPimObject));
        // Angegebene Länge kleiner als der Inhalt: gelesen wird bis zur Grenze, nicht nur bis zur Angabe
        Assert.Equal(ics, await DavServer.BodyBytes(Request(new MemoryStream(ics), 3), DavServer.MaxPimObject));
        Assert.True(DavServer.MaxNotiziFile >= 32L * 1024 * 1024);
    }
}
