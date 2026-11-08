namespace DmOffice.Dav;

/// <summary>
/// PDF-Vorschau als Seitenbilder für alle Download-Adressen (Dateien, Galerie, Mail-Anhänge, Chat, öffentliche Links):
/// Der PDF-Betrachter des Browsers lässt sich im Rahmen nicht überall einbetten (Vivaldi blockiert ihn unter der CSP der
/// Seite, object-src 'none'), Bilder gehen überall. Eine Download-Adresse mit <c>pdfpages=1</c> antwortet mit der
/// Seitenzahl, mit <c>pdfpage=n</c> (ab 0, dazu <c>width</c>) mit Seite n als JPEG. Rechte und Quelle prüft wie sonst der
/// Download-Pfad selbst - hier wird nur seine Antwort, wenn sie eine PDF ist, in Bilder umgerechnet.
/// </summary>
public static class PdfPages
{
    /// <summary>Größer wird nicht umgerechnet (die Datei steht dafür ganz im Speicher).</summary>
    public const long MaxBytes = 200L * 1024 * 1024;

    /// <summary>Puffer mit Obergrenze: darüber wirft das Schreiben (Full).</summary>
    private sealed class Capped(long max) : MemoryStream
    {
        public bool Full { get; private set; }
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) { Check(count); return base.WriteAsync(buffer, offset, count, ct); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) { Check(buffer.Length); return base.WriteAsync(buffer, ct); }
        private void Check(int n) { if (Length + n > max) { Full = true; throw new IOException("Zu groß für die Vorschau."); } }
    }

    public static void Use(WebApplication app) => app.Use(async (ctx, next) =>
    {
        var query = ctx.Request.Query;
        var wantCount = query.ContainsKey("pdfpages");
        var wantPage = int.TryParse(query["pdfpage"], out var page);
        if (!HttpMethods.IsGet(ctx.Request.Method) || (!wantCount && !wantPage)) { await next(); return; }

        // Antwort des Download-Pfads auffangen statt senden
        var original = ctx.Response.Body;
        // Höchstens MaxBytes auffangen - eine riesige Datei (auch über einen öffentlichen Link) belegt sonst den ganzen Speicher
        using var buffer = new Capped(MaxBytes);
        ctx.Response.Body = buffer;
        var tooBig = false;
        try { await next(); }
        catch (IOException) when (buffer.Full) { tooBig = true; }
        finally { ctx.Response.Body = original; }
        // Auch, wenn eine innere Fehlerbehandlung die Ausnahme schon aufgefangen hat
        if (tooBig || buffer.Full)
        {
            foreach (var h in new[] { "Content-Disposition", "ETag", "Last-Modified", "Accept-Ranges", "Content-Range" }) ctx.Response.Headers.Remove(h);
            ctx.Response.ContentLength = null;
            ctx.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsJsonAsync(new { error = "Keine PDF oder zu groß für die Vorschau." }, ctx.RequestAborted);
            return;
        }

        if (ctx.Response.StatusCode != StatusCodes.Status200OK || buffer.Length == 0)
        {
            buffer.Position = 0;
            await buffer.CopyToAsync(original, ctx.RequestAborted);
            return;
        }
        var data = buffer.ToArray();
        // Die Header der Datei gelten für das Bild nicht mehr
        foreach (var h in new[] { "Content-Disposition", "ETag", "Last-Modified", "Accept-Ranges", "Content-Range" }) ctx.Response.Headers.Remove(h);
        ctx.Response.ContentLength = null;
        if (data.Length > MaxBytes || !IsPdf(data))
        {
            ctx.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsJsonAsync(new { error = "Keine PDF oder zu groß für die Vorschau." }, ctx.RequestAborted);
            return;
        }
        // Kurz im Browser zwischenspeichern (privat)
        ctx.Response.Headers.CacheControl = "private, max-age=600";
        if (wantCount)
        {
            await ctx.Response.WriteAsJsonAsync(new { count = Count(data) }, ctx.RequestAborted);
            return;
        }
        var width = int.TryParse(query["width"], out var w) ? Math.Clamp(w, 200, 2000) : 1000;
        if (Page(data, Math.Max(0, page), width) is not { } jpeg)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        ctx.Response.ContentType = "image/jpeg";
        ctx.Response.ContentLength = jpeg.Length;
        await ctx.Response.Body.WriteAsync(jpeg, ctx.RequestAborted);
    });

    /// <summary>Beginnt die Datei wie eine PDF (%PDF, auch nach ein paar Bytes Vorspann)?</summary>
    public static bool IsPdf(byte[] data) =>
        data.AsSpan(0, Math.Min(data.Length, 1024)).IndexOf("%PDF-"u8) >= 0;

    /// <summary>Seitenzahl, 0 wenn PDFium die Datei nicht lesen kann.</summary>
    public static int Count(byte[] pdf)
    {
        if (PdfTool.Available) return PdfTool.Info(pdf)?.Pages ?? 0;
        if (!Monitor.TryEnter(Thumbs.PdfLock, TimeSpan.FromSeconds(30))) return 0;
        try { return PDFtoImage.Conversion.GetPageCount(pdf, password: null); }
        catch (Exception) { return 0; }
        finally { Monitor.Exit(Thumbs.PdfLock); }
    }

    /// <summary>Höchste Bildhöhe einer Seite: eine sehr schmale, hohe Seite ergäbe bei fester Breite sonst eine riesige Bitmap.</summary>
    private const int MaxHeight = 8000;

    /// <summary>Seite (ab 0) in der Breite als JPEG, weißer Grund; null bei Fehler.</summary>
    public static byte[]? Page(byte[] pdf, int page, int width)
    {
        // Im Container: Poppler als eigener Prozess (Zeit- und Speichergrenze)
        if (PdfTool.Available)
        {
            if (PdfTool.Info(pdf, page) is not { Width: > 0, Height: > 0 } info) return null;
            if (width * info.Height / info.Width > MaxHeight) width = Math.Max(1, (int)(MaxHeight * info.Width / info.Height));
            return PdfTool.Render(pdf, page, width);
        }
        if (!Monitor.TryEnter(Thumbs.PdfLock, TimeSpan.FromSeconds(30))) return null;
        try
        {
            var size = PDFtoImage.Conversion.GetPageSize(pdf, page, password: null);
            if (size.Width <= 0 || size.Height <= 0) return null;
            if (width * size.Height / size.Width > MaxHeight) width = Math.Max(1, (int)(MaxHeight * size.Width / size.Height));
            using var bmp = PDFtoImage.Conversion.ToImage(pdf, page, password: null,
                new PDFtoImage.RenderOptions { Width = width, WithAspectRatio = true, BackgroundColor = SkiaSharp.SKColors.White, WithAnnotations = true });
            using var jpeg = bmp.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 85);
            return jpeg.ToArray();
        }
        catch (Exception) { return null; }
        finally { Monitor.Exit(Thumbs.PdfLock); }
    }
}
