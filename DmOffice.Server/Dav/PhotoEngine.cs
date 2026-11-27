using SkiaSharp;
using System.Net.Http.Headers;
using System.Text.Json;

namespace DmOffice.Dav;

/// <summary>
/// Anbindung an die Photo-Engine (photo-engine/): beurteilt ein Foto ohne KI (Histogramm, Gesichter, Weißabgleich, Szene) und liefert
/// Reglerwerte (/analyze); /develop wendet Werte an - die Werte sind auf genau diese Umsetzung abgestimmt. Im Image eingebaut und über
/// die Verwaltung abschaltbar (PhotoEngineProcess). Eine Engine auf einem anderen Rechner: PHOTO_ENGINE_URL (z. B. http://photo-engine:8765)
/// und optional PHOTO_ENGINE_TOKEN (Header X-Api-Key), die eingebaute startet dann nicht.
/// Ohne Engine oder wenn sie nicht antwortet: dmOffice rechnet selbst (PhotoAdjust).
/// </summary>
public static class PhotoEngine
{
    // Vollauflösung braucht bei 200-MP-Fotos bis etwa 2 Minuten; Vorschauen begrenzt DevelopAsync selbst kürzer
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(200) };

    private static readonly Lazy<PhotoEngineProcess?> BuiltIn = new(PhotoEngineProcess.FromEnvironment);

    /// <summary>In der Verwaltung ausgeschaltet? Beim Start gesetzt (Program.cs).</summary>
    public static Func<bool> BuiltInOff { get; set; } = () => false;

    public static string? ExternalUrl => Environment.GetEnvironmentVariable("PHOTO_ENGINE_URL") is { Length: > 0 } u ? u.TrimEnd('/') : null;
    /// <summary>Ist die eingebaute Engine in diesem Image dabei?</summary>
    public static bool BuiltInAvailable => BuiltIn.Value != null;
    public static bool BuiltInRunning => BuiltIn.Value?.Running == true;
    public static bool Configured => ExternalUrl != null || (BuiltInAvailable && !BuiltInOff());

    public static void Startup(ILogger log)
    {
        if (BuiltIn.Value is not { } engine) return;
        engine.Log = log;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => engine.Stop();
    }

    /// <summary>Eingebaute Engine sofort beenden (ausgeschaltet).</summary>
    public static void StopBuiltIn() => BuiltIn.Value?.Stop();

    /// <summary>Zugang für einen Auftrag; die eingebaute Engine startet dabei, wenn nötig. null: keine Engine.</summary>
    private static async Task<PhotoEngineLease?> ConnectAsync(CancellationToken ct)
    {
        if (ExternalUrl is { } url) return new PhotoEngineLease(url, Environment.GetEnvironmentVariable("PHOTO_ENGINE_TOKEN"));
        return !BuiltInOff() && BuiltIn.Value is { } engine ? await engine.AcquireAsync(ct) : null;
    }

    private static HttpRequestMessage Request(PhotoEngineLease lease, HttpMethod m, string path)
    {
        var req = new HttpRequestMessage(m, lease.Url + path);
        if (lease.Token is { Length: > 0 } token) req.Headers.Add("X-Api-Key", token);
        return req;
    }

    /// <summary>Vorschlag: Reglerwerte und eine lesbare Begründung; null, wenn die Engine nicht antwortet.</summary>
    public static async Task<(PhotoAdjust.Values Values, string? Analysis)?> AnalyzeAsync(byte[] jpeg, string? exifJson, ILogger? log, CancellationToken ct)
    {
        using var lease = await ConnectAsync(ct);
        if (lease == null) return null;
        try
        {
            using var req = Request(lease, HttpMethod.Post, "/analyze");
            req.Content = new ByteArrayContent(jpeg);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            if (exifJson != null) req.Headers.TryAddWithoutValidation("X-Exif", exifJson);
            using var res = await Http.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode) { log?.LogWarning("Photo-Engine /analyze: {Status} {Body}", (int)res.StatusCode, body.Length > 200 ? body[..200] : body); return null; }
            using var doc = JsonDocument.Parse(body);
            var g = doc.RootElement.TryGetProperty("global", out var gl) ? gl : default;
            double V(string n) => g.ValueKind == JsonValueKind.Object && g.TryGetProperty(n, out var e) && e.TryGetDouble(out var d) ? d : 0;
            var values = new PhotoAdjust.Values(V("exposure_ev"), V("contrast"), V("highlights"), V("shadows"), V("whites"), V("blacks"),
                V("temperature_shift"), V("tint_shift"), V("vibrance"), V("saturation"), V("dehaze"), V("clarity")).Clamped();
            var analysis = doc.RootElement.TryGetProperty("analysis", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
            return (values, analysis);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            log?.LogWarning("Photo-Engine nicht erreichbar: {Error}", e.GetBaseException().Message);
            return null;
        }
    }

    /// <summary>Werte anwenden: mit maxEdge eine Vorschau (aufrecht), ohne das Original in voller Größe (Metadaten bleiben, Drehangabe unverändert).</summary>
    /// <summary>
    /// Vorschau als JPEG (/preview) für Formate, die dmOffice selbst nicht dekodiert (TIFF, auch 16 Bit; Kennung tiffpreview), aufrecht und
    /// höchstens <paramref name="maxEdge"/> groß. Null: Engine aus oder nicht erreichbar (nichts merken, später noch einmal); leer: Datei nicht
    /// lesbar (merken, kein Vorschaubild).
    /// </summary>
    public static Task<byte[]?> PreviewAsync(byte[] image, int maxEdge, ILogger? log, CancellationToken ct) =>
        PreviewAsync(() => new ByteArrayContent(image), maxEdge, log, ct);

    /// <summary>Wie oben, die Datei wird aus dem Stream durchgereicht statt ganz in den Speicher geladen.</summary>
    public static Task<byte[]?> PreviewAsync(Stream image, int maxEdge, ILogger? log, CancellationToken ct) =>
        PreviewAsync(() =>
        {
            var c = new StreamContent(image, 81920);
            if (image.CanSeek) c.Headers.ContentLength = image.Length - image.Position;
            return c;
        }, maxEdge, log, ct);

    private static async Task<byte[]?> PreviewAsync(Func<HttpContent> body, int maxEdge, ILogger? log, CancellationToken ct)
    {
        if (!Configured) return null;
        using var lease = await ConnectAsync(ct);
        if (lease == null) return null;
        try
        {
            using var req = Request(lease, HttpMethod.Post, "/preview?max_edge=" + maxEdge + "&quality=90");
            req.Content = body();
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(90));
            using var res = await Http.SendAsync(req, limit.Token);
            if (res.StatusCode == System.Net.HttpStatusCode.BadRequest) return [];
            if (!res.IsSuccessStatusCode) { log?.LogWarning("Photo-Engine /preview: {Status}", (int)res.StatusCode); return null; }
            var bytes = await res.Content.ReadAsByteArrayAsync(limit.Token);
            return PhotoEdit.IsJpeg(bytes) ? bytes : [];
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            log?.LogWarning("Photo-Engine nicht erreichbar: {Error}", e.GetBaseException().Message);
            return null;
        }
    }

    // [feature:lenscorr]
    /// <param name="lens">Objektivkorrektur (Block „lens“, LensProfiles.Block); null: keine</param>
    public static async Task<byte[]?> DevelopAsync(byte[] jpeg, PhotoAdjust.Values v, int? maxEdge, int quality, ILogger? log, CancellationToken ct,
        System.Text.Json.Nodes.JsonObject? lens = null)
    // [feature:lenscorr:else]
    // public static async Task<byte[]?> DevelopAsync(byte[] jpeg, PhotoAdjust.Values v, int? maxEdge, int quality, ILogger? log, CancellationToken ct)
    // [/feature:lenscorr]
    {
        using var lease = await ConnectAsync(ct);
        if (lease == null) return null;
        try
        {
            var body = new System.Text.Json.Nodes.JsonObject
            {
                ["global"] = JsonSerializer.SerializeToNode(new
                {
                    exposure_ev = v.Exposure, contrast = v.Contrast, highlights = v.Highlights, shadows = v.Shadows, whites = v.Whites, blacks = v.Blacks,
                    temperature_shift = v.Temperature, tint_shift = v.Tint, vibrance = v.Vibrance, saturation = v.Saturation, dehaze = v.Dehaze, clarity = v.Clarity,
                }),
                ["crop"] = new System.Text.Json.Nodes.JsonObject { ["straighten_deg"] = 0 },
            };
            if (lens != null) body["lens"] = lens.DeepClone();   // [feature:lenscorr]
            var corrections = body.ToJsonString();
            using var req = Request(lease, HttpMethod.Post, "/develop?quality=" + quality + (maxEdge is int m ? "&max_edge=" + m : ""));
            req.Content = new ByteArrayContent(jpeg);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            req.Headers.TryAddWithoutValidation("X-Corrections", corrections);
            // Vorschau: höchstens 30 s, dann rechnet dmOffice selbst; Vollauflösung: bis zum Zeitlimit des Clients
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (maxEdge != null) limit.CancelAfter(TimeSpan.FromSeconds(30));
            // 413: Foto zu groß für die Engine (PHOTO_ENGINE_MAX_PIXELS) - wie jeder Fehler: dmOffice rechnet selbst
            using var res = await Http.SendAsync(req, limit.Token);
            if (!res.IsSuccessStatusCode) { log?.LogWarning("Photo-Engine /develop: {Status}", (int)res.StatusCode); return null; }
            var bytes = await res.Content.ReadAsByteArrayAsync(limit.Token);
            if (!PhotoEdit.IsJpeg(bytes)) return null;
            // Den Farbmischer kennt die Engine nicht: den rechnet dmOffice danach selbst
            if (v.HasMixer && SKBitmap.Decode(bytes) is { } dev)
                using (dev)
                {
                    using var rgba = dev.Copy(SKColorType.Rgba8888);
                    PhotoAdjust.Mixer(rgba, v);
                    bytes = PhotoAdjust.Jpeg(rgba, quality);
                }
            return bytes;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            log?.LogWarning("Photo-Engine nicht erreichbar: {Error}", e.GetBaseException().Message);
            return null;
        }
    }
// [feature:lenscorr]

    /// <summary>
    /// Auftrag an die Engine mit JSON-Antwort (Objektivprofile lesen, Schachbrett finden, Verzeichnung bestimmen). Status 0: keine Engine
    /// oder keine Antwort.
    /// </summary>
    public static async Task<(int Status, System.Text.Json.Nodes.JsonNode? Json)> CallAsync(string path, byte[] body, string contentType, int seconds, CancellationToken ct)
    {
        if (!Configured) return (0, null);
        using var lease = await ConnectAsync(ct);
        if (lease == null) return (0, null);
        try
        {
            using var req = Request(lease, HttpMethod.Post, path);
            req.Content = new ByteArrayContent(body);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(seconds));
            using var res = await Http.SendAsync(req, limit.Token);
            var text = await res.Content.ReadAsStringAsync(limit.Token);
            System.Text.Json.Nodes.JsonNode? json = null;
            try { json = System.Text.Json.Nodes.JsonNode.Parse(text); } catch (JsonException) { }
            return ((int)res.StatusCode, json);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { return (0, null); }
    }
// [/feature:lenscorr]
}
