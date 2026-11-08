using System.Text.Json;
using DmOffice.Core;
using DmOffice.Dav;

namespace DmOffice.Chat;

/// <summary>
/// Profilbilder (Version 2): je Benutzer ein Bild (WebP, 256 Pixel) im Schlüssel/Wert-Speicher seiner Datenbank - kein neues
/// Schema. Ohne Bild zeigen Web und App den ersten Buchstaben des Benutzernamens auf seiner Akzentfarbe (aus den
/// Oberflächen-Einstellungen „ui.prefs“). Liegt unter /api/chat, damit auch die App (App-Passwort) herankommt.
/// Pukki ist eine von fünf Figuren - jeder wählt seine; die Bilder bringen Web und App selbst mit.
/// </summary>
public static partial class ChatApi
{
    private const string AvatarKey = "profile.avatar", AvatarVersionKey = "profile.avatar.v";
    private const string PukkiFigureKey = "pukki.figure";

    /// <summary>Figuren für Pukki; die erste ist die Vorgabe.</summary>
    public static readonly string[] PukkiFigures = ["panda", "walross", "baer", "zauberer", "walross-zauberer"];

    /// <summary>Die Figur, als die Pukki bei diesem Benutzer erscheint.</summary>
    internal static string PukkiFigure(UserDb db) => db.Get(PukkiFigureKey) is { } f && PukkiFigures.Contains(f) ? f : PukkiFigures[0];
    private static Spaces? _lookSpaces;

    /// <summary>Wie jemand aussieht: Version des Profilbilds (null: keins) und Akzentfarbe (#rrggbb, null: Vorgabe).</summary>
    internal static (string? Avatar, string? Accent) LookOf(string user)
    {
        if (user == Pukki.Id || _lookSpaces == null) return (null, null);
        try
        {
            var db = _lookSpaces.Get(user).Db;
            string? accent = null;
            if (db.Get("ui.prefs") is { } prefs)
                using (var doc = JsonDocument.Parse(prefs))
                    if (doc.RootElement.TryGetProperty("accent", out var a) && a.ValueKind == JsonValueKind.String && a.GetString() is { } c
                        && System.Text.RegularExpressions.Regex.IsMatch(c, "^#[0-9a-fA-F]{6}$"))
                        accent = c;
            return (db.Get(AvatarVersionKey), accent);
        }
        catch (Exception e) when (e is KeyNotFoundException or JsonException) { return (null, null); }
    }

    private static void MapAvatars(RouteGroupBuilder api, Spaces spaces)
    {
        _lookSpaces = spaces;

        api.MapGet("/people/{id}/avatar", (string id, HttpContext http, Spaces s) =>
        {
            if (id == "me") id = http.Space().User.Id;
            var db = s.Get(id).Db;
            if (db.Get(AvatarKey) is not { } b64) return Results.NotFound();
            // Mit Version in der Adresse ändert sich das Bild nie - lange zwischenspeichern
            http.Response.Headers.CacheControl = http.Request.Query.ContainsKey("v") ? "private, max-age=31536000, immutable" : "private, no-cache";
            return Results.Bytes(Convert.FromBase64String(b64), "image/webp");
        });

        // Eigenes Bild setzen: Bildbytes roh im Körper (JPEG, PNG, WebP, HEIC je nach Skia); quadratisch schneidet der Client zu
        api.MapPut("/people/me/avatar", async (HttpRequest request, HttpContext http, CancellationToken ct) =>
        {
            // Groesse vor dem Lesen begrenzen - sonst landete ein riesiger Koerper erst ganz im Speicher
            if (http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = 15 * 1024 * 1024;
            if (request.ContentLength > 15 * 1024 * 1024) throw new ArgumentException("Das Bild ist zu groß (höchstens 15 MB).");
            using var ms = new MemoryStream();
            await request.Body.CopyToAsync(ms, ct);
            if (ms.Length > 15 * 1024 * 1024) throw new ArgumentException("Das Bild ist zu groß (höchstens 15 MB).");
            // Richtig herum gedreht (EXIF) und verkleinert, dann quadratisch aus der Mitte auf 256 Pixel
            var fitted = Thumbs.FromBytes(ms.ToArray(), 512) ?? throw new ArgumentException("Das ist kein Bild, das dmOffice lesen kann.");
            byte[] webp;
            using (var bmp = SkiaSharp.SKBitmap.Decode(fitted))
            {
                var side = Math.Min(bmp.Width, bmp.Height);
                var src = SkiaSharp.SKRectI.Create((bmp.Width - side) / 2, (bmp.Height - side) / 2, side, side);
                using var square = new SkiaSharp.SKBitmap(256, 256);
                using (var canvas = new SkiaSharp.SKCanvas(square))
                using (var paint = new SkiaSharp.SKPaint { IsAntialias = true })
                    canvas.DrawImage(SkiaSharp.SKImage.FromBitmap(bmp), src, new SkiaSharp.SKRect(0, 0, 256, 256), new SkiaSharp.SKSamplingOptions(SkiaSharp.SKCubicResampler.Mitchell), paint);
                using var img = SkiaSharp.SKImage.FromBitmap(square);
                webp = img.Encode(SkiaSharp.SKEncodedImageFormat.Webp, 85).ToArray();
            }
            var db = http.Space().Db;
            var version = DateTime.UtcNow.Ticks.ToString("x");
            db.Set(AvatarKey, Convert.ToBase64String(webp));
            db.Set(AvatarVersionKey, version);
            return Results.Ok(new { avatar = version });
        });

        // Eigene Figur für Pukki - getrennt von den Chat-Einstellungen, damit ältere Apps sie beim Speichern nicht zurücksetzen
        api.MapPut("/pukki/figure", (PukkiFigureBody body, HttpContext http) =>
        {
            if (!PukkiFigures.Contains(body.Figure)) throw new ArgumentException("Diese Figur gibt es nicht.");
            http.Space().Db.Set(PukkiFigureKey, body.Figure);
            return Results.Ok(new { figure = body.Figure });
        });

        api.MapDelete("/people/me/avatar", (HttpContext http) =>
        {
            var db = http.Space().Db;
            db.Set(AvatarKey, null);
            db.Set(AvatarVersionKey, null);
            return Results.Ok();
        });
    }
}

public sealed record PukkiFigureBody(string Figure);
