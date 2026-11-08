using SkiaSharp;

namespace DmOffice.Dav;

/// <summary>
/// Regler für Fotos (wie in Lightroom, bewusst wenige und mit engen Bereichen): Belichtung, Kontrast, Lichter, Tiefen, Weiß, Schwarz,
/// Temperatur, Tönung, Dynamik, Sättigung. Tonkurve und Weißabgleich als Tabelle je Kanal, Sättigung als Farbmatrix - schnell auch
/// bei 40 MP. Dazu die Kennzahlen eines Bildes (Histogramm) für den Vorschlag der KI.
/// </summary>
public static class PhotoAdjust
{
    /// <summary>Alle Werte 0 = unverändert. Belichtung in Blenden (±1,5), die übrigen ±60 (Kontrast, Lichter, Tiefen), sonst ±50.</summary>
    /// <param name="Dehaze">Dunst entfernen (−100 … +100, wie Lightroom; + entfernt Dunst)</param>
    /// <param name="Clarity">Klarheit (−100 … +100, Mitteltonkontrast) - rechnet nur die Photo-Engine, ohne sie bleibt er wirkungslos</param>
    public sealed record Values(double Exposure = 0, double Contrast = 0, double Highlights = 0, double Shadows = 0, double Whites = 0, double Blacks = 0,
        double Temperature = 0, double Tint = 0, double Vibrance = 0, double Saturation = 0, double Dehaze = 0, double Clarity = 0)
    {
        /// <summary>
        /// Farbmischer (wie Lightroom und CameraImporter): je Farbband Farbton, Sättigung und Luminanz, −100 … +100, als einzelne Werte
        /// „hsl_h_red“, „hsl_s_blue“ … (flach wie die übrigen Regler - so liest und schreibt sie auch die App).
        /// </summary>
        [System.Text.Json.Serialization.JsonExtensionData]
        public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }

        /// <summary>Wert des Farbmischers (0, wenn nicht gesetzt).</summary>
        public double Hsl(char part, string band) =>
            Extra != null && Extra.TryGetValue($"hsl_{part}_{band}", out var e) && e.ValueKind == System.Text.Json.JsonValueKind.Number ? e.GetDouble() : 0;

        [System.Text.Json.Serialization.JsonIgnore]
        public bool HasMixer => Bands.Any(b => Math.Abs(Hsl('h', b.Name)) >= 0.5 || Math.Abs(Hsl('s', b.Name)) >= 0.5 || Math.Abs(Hsl('l', b.Name)) >= 0.5);

        // Nicht mitschicken: die App liest die Werte als Zahlen (ein Wahrheitswert brach dort den Vorschlag ab)
        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsNeutral => Math.Abs(Exposure) < 0.005 && new[] { Contrast, Highlights, Shadows, Whites, Blacks, Temperature, Tint, Vibrance, Saturation, Dehaze, Clarity }.All(v => Math.Abs(v) < 0.5)
            && !HasMixer;

        /// <summary>In die erlaubten Bereiche (auch für Werte der KI); vom Farbmischer nur dessen Werte.</summary>
        public Values Clamped() => new(Math.Clamp(Exposure, -2, 2), Math.Clamp(Contrast, -60, 60), Math.Clamp(Highlights, -60, 60), Math.Clamp(Shadows, -60, 60),
            Math.Clamp(Whites, -50, 50), Math.Clamp(Blacks, -50, 50), Math.Clamp(Temperature, -50, 50), Math.Clamp(Tint, -50, 50),
            Math.Clamp(Vibrance, -50, 50), Math.Clamp(Saturation, -50, 50), Math.Clamp(Dehaze, -100, 100), Math.Clamp(Clarity, -100, 100))
        {
            Extra = HasMixer ? Bands.SelectMany(b => "hsl".Select(p => (Key: $"hsl_{p}_{b.Name}", V: Math.Round(Math.Clamp(Hsl(p, b.Name), -100, 100)))))
                .Where(x => x.V != 0).ToDictionary(x => x.Key, x => System.Text.Json.JsonSerializer.SerializeToElement(x.V)) : null,
        };
    }

    /// <summary>Farbbänder des Farbmischers mit ihrer Mitte (Farbton in Grad), wie in CameraImporter.</summary>
    public static readonly (string Name, double Center)[] Bands =
        [("red", 0), ("orange", 30), ("yellow", 60), ("green", 120), ("aqua", 180), ("blue", 240), ("purple", 285), ("magenta", 320)];

    /// <summary>
    /// Farbmischer auf ein Bild (an Ort und Stelle): je Pixel die Bänder weich gewichtet (smoothstep, 40° breit, auf 1 normiert), Farbton
    /// bis ±25°, Sättigung und Luminanz anteilig. Graue und fast graue Pixel bleiben, wie sie sind - sie haben keinen Farbton.
    /// </summary>
    public static void Mixer(SKBitmap bmp, Values v)
    {
        if (!v.HasMixer) return;
        if (bmp.ColorType != SKColorType.Rgba8888 && bmp.ColorType != SKColorType.Bgra8888) return;
        // Je Grad Farbton: Verschiebung, Faktor Sättigung, Faktor Helligkeit
        var dh = new float[360]; var fs = new float[360]; var fl = new float[360];
        for (var deg = 0; deg < 360; deg++)
        {
            double sw = 1e-6, ah = 0, aS = 0, al = 0;
            foreach (var (name, center) in Bands)
            {
                var d = Math.Abs(((deg - center + 180) % 360 + 360) % 360 - 180);
                var w = Math.Clamp(1 - d / 40.0, 0, 1); w = w * w * (3 - 2 * w);
                sw += w; ah += w * v.Hsl('h', name); aS += w * v.Hsl('s', name); al += w * v.Hsl('l', name);
            }
            dh[deg] = (float)(ah / sw / 100 * 25); fs[deg] = (float)(aS / sw / 100); fl[deg] = (float)(al / sw / 100 * 0.6);
        }
        var bgra = bmp.ColorType == SKColorType.Bgra8888;
        var width = bmp.Width; var stride = bmp.RowBytes;
        var ptr = bmp.GetPixels();
        // Zeilenweise kopiert (ohne unsicheren Code): lesen, rechnen, zurückschreiben
        Parallel.For(0, bmp.Height, () => new byte[width * 4], (y, _, row) =>
        {
            var at = ptr + (nint)((long)y * stride);
            System.Runtime.InteropServices.Marshal.Copy(at, row, 0, row.Length);
            {
                for (var x = 0; x < width; x++)
                {
                    var p = row.AsSpan(x * 4, 4);
                    float r = p[bgra ? 2 : 0] / 255f, g = p[1] / 255f, b = p[bgra ? 0 : 2] / 255f;
                    var max = Math.Max(r, Math.Max(g, b)); var min = Math.Min(r, Math.Min(g, b)); var c = max - min;
                    if (c < 0.004f || max <= 0) continue;
                    var sat = c / max;
                    var h = max == r ? (g - b) / c % 6 : max == g ? (b - r) / c + 2 : (r - g) / c + 4;
                    h *= 60; if (h < 0) h += 360;
                    var k = (int)h % 360;
                    // Kaum gesättigt: weniger Wirkung (sonst färbte das Rot-Band graue Flächen)
                    var amt = Math.Clamp(sat / 0.15f, 0, 1);
                    var nh = h + dh[k] * amt; if (nh < 0) nh += 360; if (nh >= 360) nh -= 360;
                    var ns = Math.Clamp(sat * (1 + fs[k] * amt), 0, 1);
                    var nv = Math.Clamp(max * (1 + fl[k] * amt), 0, 1);
                    // HSV -> RGB
                    var cc = nv * ns; var hh = nh / 60; var xx = cc * (1 - Math.Abs(hh % 2 - 1)); var m = nv - cc;
                    float rr, gg, bb;
                    if (hh < 1) (rr, gg, bb) = (cc, xx, 0f); else if (hh < 2) (rr, gg, bb) = (xx, cc, 0f); else if (hh < 3) (rr, gg, bb) = (0f, cc, xx);
                    else if (hh < 4) (rr, gg, bb) = (0f, xx, cc); else if (hh < 5) (rr, gg, bb) = (xx, 0f, cc); else (rr, gg, bb) = (cc, 0f, xx);
                    p[bgra ? 2 : 0] = (byte)Math.Round((rr + m) * 255); p[1] = (byte)Math.Round((gg + m) * 255); p[bgra ? 0 : 2] = (byte)Math.Round((bb + m) * 255);
                }
            }
            System.Runtime.InteropServices.Marshal.Copy(row, 0, at, row.Length);
            return row;
        }, _ => { });
        bmp.NotifyPixelsChanged();
    }

    // Filmkurve F(s) = sRGB((tanh(k·s)/tanh(k))^g), an Lightroom gemessen (31.10.2026, Belichtung −2 auf ein JPG, Abweichung < 2/255).
    // Das Bild gilt als F(Szene); belichtet wird die Szene: F(F⁻¹(x)·2^EV). Abdunkeln drückt die Mitten stärker als die Lichter, die
    // Farben bleiben kräftig; Aufhellen läuft weich in die Schulter. Dieselbe Kurve hat die Photo-Engine (engine/develop.py).
    private const double FilmK = 3.776, FilmG = 1.33;
    private static double Film(double s)
    {
        var t = Math.Pow(Math.Clamp(Math.Tanh(FilmK * s) / Math.Tanh(FilmK), 0, 1), FilmG);
        return t <= 0.0031308 ? t * 12.92 : 1.055 * Math.Pow(t, 1 / 2.4) - 0.055;
    }

    public static double Exposure(double x, double ev)
    {
        if (x <= 0) return 0;
        // F⁻¹ durch Halbieren (F steigt streng)
        double lo = 0, hi = 1;
        for (var i = 0; i < 40; i++) { var m = (lo + hi) / 2; if (Film(m) < x) lo = m; else hi = m; }
        return Film((lo + hi) / 2 * Math.Pow(2, ev));
    }

    /// <summary>Tonkurve (0..1 → 0..1) aus Belichtung, Kontrast, Lichtern, Tiefen, Weiß und Schwarz.</summary>
    public static double Curve(double x, Values v)
    {
        // Belichtung wie Lightroom und die Photo-Engine: Verschiebung vor einer Filmkurve (auf R, G, B gleich)
        if (Math.Abs(v.Exposure) > 0.001) x = Exposure(x, v.Exposure);
        x = Math.Clamp(x, 0, 1);
        // Tiefen hellen den unteren Bereich auf (Schwarz bleibt schwarz), Lichter dämpfen den oberen (Weiß bleibt weiß)
        x += v.Shadows / 100 * 1.6 * x * (1 - x) * (1 - x);
        x += v.Highlights / 100 * 1.6 * x * x * (1 - x);
        // Kontrast: S-Kurve um die Mitte, an den Enden ohne Wirkung
        x += v.Contrast / 100 * 2.0 * (x - 0.5) * x * (1 - x);
        // Weiß und Schwarz: die Enden der Kurve
        x += v.Whites / 100 * 0.6 * Math.Pow(x, 4) * (1 - x) * 4;
        x += v.Blacks / 100 * 0.6 * Math.Pow(1 - x, 4) * x * 4;
        return Math.Clamp(x, 0, 1);
    }

    /// <summary>Angewendet auf ein Bild (neues Bild, das alte bleibt).</summary>
    public static SKBitmap Apply(SKBitmap src, Values v)
    {
        // Ohne Engine: Dunst entfernen grob als mehr Kontrast, etwas tieferes Schwarz und etwas mehr Sättigung (Klarheit bleibt ihr vorbehalten)
        if (Math.Abs(v.Dehaze) >= 0.5)
            v = v with { Contrast = v.Contrast + v.Dehaze * 0.4, Blacks = v.Blacks - Math.Max(0, v.Dehaze) * 0.15, Saturation = v.Saturation + Math.Max(0, v.Dehaze) * 0.15 };
        // Weißabgleich: wärmer = mehr Rot, weniger Blau; Tönung: weniger Grün = magenta
        var mr = 1 + v.Temperature / 100 * 0.18;
        var mb = 1 - v.Temperature / 100 * 0.18;
        var mg = 1 - v.Tint / 100 * 0.12;
        byte[] Table(double mult)
        {
            var t = new byte[256];
            for (var i = 0; i < 256; i++) t[i] = (byte)Math.Round(Curve(Math.Clamp(i / 255.0 * mult, 0, 1), v) * 255);
            return t;
        }
        var alpha = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        using var table = SKColorFilter.CreateTable(alpha, Table(mr), Table(mg), Table(mb));
        // Sättigung (Dynamik wirkt hier etwas schwächer als Sättigung - eine echte Dynamik schont schon gesättigte Farben)
        var s = (float)(1 + (v.Saturation + v.Vibrance * 0.6) / 100);
        const float lr = 0.2126f, lg = 0.7152f, lb = 0.0722f;
        float[] m =
        [
            lr * (1 - s) + s, lg * (1 - s), lb * (1 - s), 0, 0,
            lr * (1 - s), lg * (1 - s) + s, lb * (1 - s), 0, 0,
            lr * (1 - s), lg * (1 - s), lb * (1 - s) + s, 0, 0,
            0, 0, 0, 1, 0,
        ];
        using var sat = SKColorFilter.CreateColorMatrix(m);
        using var both = SKColorFilter.CreateCompose(sat, table);
        var dst = new SKBitmap(src.Width, src.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(dst);
        using var paint = new SKPaint { ColorFilter = both };
        canvas.DrawBitmap(src, 0, 0, paint);
        canvas.Flush();
        Mixer(dst, v);
        return dst;
    }

    /// <summary>Kennzahlen der Helligkeit (0–255): Mittel, Median, Perzentile, Anteile fast weiß/schwarz, Mittel der Bildmitte.</summary>
    public sealed record Stats(double Mean, int Median, int P1, int P5, int P95, int P99, double ClipHigh, double ClipLow, double CenterMean);

    public static Stats Measure(SKBitmap bmp)
    {
        var hist = new long[256];
        double center = 0; long centerN = 0;
        // Jedes zweite Pixel reicht
        for (var y = 0; y < bmp.Height; y += 2)
            for (var x = 0; x < bmp.Width; x += 2)
            {
                var c = bmp.GetPixel(x, y);
                var l = (int)Math.Round(0.2126 * c.Red + 0.7152 * c.Green + 0.0722 * c.Blue);
                hist[l]++;
                if (x > bmp.Width / 3 && x < bmp.Width * 2 / 3 && y > bmp.Height / 3 && y < bmp.Height * 2 / 3) { center += l; centerN++; }
            }
        var n = hist.Sum();
        int Pct(double p) { long acc = 0; for (var i = 0; i < 256; i++) { acc += hist[i]; if (acc >= n * p) return i; } return 255; }
        var mean = Enumerable.Range(0, 256).Sum(i => (double)i * hist[i]) / Math.Max(1, n);
        return new Stats(Math.Round(mean, 1), Pct(0.5), Pct(0.01), Pct(0.05), Pct(0.95), Pct(0.99),
            Math.Round(100.0 * hist.Skip(250).Sum() / Math.Max(1, n), 2), Math.Round(100.0 * hist.Take(6).Sum() / Math.Max(1, n), 2),
            Math.Round(center / Math.Max(1, centerN), 1));
    }

    /// <summary>Aufrecht gedrehtes, verkleinertes Bild (längste Kante höchstens <paramref name="max"/>) aus einem JPEG.</summary>
    public static SKBitmap Small(byte[] jpeg, int max)
    {
        using var codec = SKCodec.Create(new MemoryStream(jpeg)) ?? throw new ArgumentException("Das Foto ließ sich nicht lesen.");
        var info = codec.Info;
        var scale = Math.Min(1f, (float)max / Math.Max(info.Width, info.Height));
        var dims = codec.GetScaledDimensions(scale);
        var bmp = new SKBitmap(new SKImageInfo(dims.Width, dims.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        codec.GetPixels(bmp.Info, bmp.GetPixels());
        if (Math.Max(bmp.Width, bmp.Height) > max)
        {
            var f = (float)max / Math.Max(bmp.Width, bmp.Height);
            var resized = bmp.Resize(new SKImageInfo((int)(bmp.Width * f), (int)(bmp.Height * f)), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            bmp.Dispose(); bmp = resized;
        }
        var upright = PhotoEdit.Upright(bmp, codec.EncodedOrigin);
        if (!ReferenceEquals(upright, bmp)) bmp.Dispose();
        return upright;
    }

    public static byte[] Jpeg(SKBitmap bmp, int quality)
    {
        using var img = SKImage.FromBitmap(bmp);
        return img.Encode(SKEncodedImageFormat.Jpeg, quality).ToArray();
    }
}
