using System.Globalization;
using System.Text.Json;
using DmOffice.Core;

namespace DmOffice.Journal;

/// <summary>
/// Wetter (Core/WeatherLookup) und Ortsname zu einem Tagebucheintrag - nur, wenn der Benutzer es einschaltet (Ort und Datum gehen dabei an Dienste
/// außerhalb): Wetter von Open-Meteo (kostenlos, ohne Anmeldung, auch rückwirkend aus dem Archiv), Ortsname von OpenStreetMap
/// (Nominatim, höchstens eine Anfrage je Sekunde).
/// </summary>
public static class JournalLookup
{
    private const string Key = "journal.lookup";
    private static readonly SemaphoreSlim NominatimGate = new(1, 1);
    private static DateTime _lastNominatim = DateTime.MinValue;

    public static bool Enabled(UserDb db) => db.Get(Key) == "1";
    public static void SetEnabled(UserDb db, bool on) => db.Set(Key, on ? "1" : null);

    /// <summary>Ortsname zu Koordinaten: Ort bzw. Stadtteil und Land, z. B. „Füssen, Deutschland“.</summary>
    public static async Task<string?> PlaceName(HttpClient http, double lat, double lon, CancellationToken ct)
    {
        await NominatimGate.WaitAsync(ct);
        try
        {
            var wait = TimeSpan.FromSeconds(1.1) - (DateTime.UtcNow - _lastNominatim);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            _lastNominatim = DateTime.UtcNow;
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://nominatim.openstreetmap.org/reverse?format=jsonv2&zoom=14&accept-language=de"
                + $"&lat={lat.ToString("0.######", CultureInfo.InvariantCulture)}&lon={lon.ToString("0.######", CultureInfo.InvariantCulture)}");
            req.Headers.UserAgent.ParseAdd("dmOffice/2.0 (Tagebuch; https://github.com/Taloan/dmOffice)");
            using var res = await http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("address", out var a)) return null;
            string? Get(params string[] keys) => keys.Select(k => a.TryGetProperty(k, out var v) ? v.GetString() : null).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            var place = Get("city", "town", "village", "municipality", "suburb", "hamlet", "county");
            var country = Get("country");
            return place == null ? country : country == null ? place : $"{place}, {country}";
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException) { return null; }
        finally { NominatimGate.Release(); }
    }
}
