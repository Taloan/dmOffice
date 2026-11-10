using System.Globalization;
using System.Text.Json;

namespace DmOffice.Core;

/// <summary>
/// Wetter zu Ort und Tag von Open-Meteo (kostenlos, ohne Anmeldung): für den Morgenüberblick von Pukki und das Wetter-Widget
/// der Übersicht. Ort und Datum gehen dabei an Open-Meteo - nur, wenn der Benutzer das Wetter einschaltet.
/// </summary>
public static class WeatherLookup
{
    /// <summary>
    /// Wetter am Tag: „sonnig, 22 / 12 °C“ (Höchst- und Tiefstwert). Das Wetter ist das häufigste zwischen 9 und 18 Uhr - der
    /// Tageswert von Open-Meteo ist das schlimmste des ganzen Tages (Morgennebel machte einen sonnigen Tag zu „Nebel“).
    /// Mit Uhrzeit (Ortszeit des Eintrags, 7 bis 20 Uhr) das Wetter dieser Stunde: „sonnig, 16 °C (Tag 18 / 5 °C)“ - abends und
    /// nachts geschrieben gilt der Tag. Null: nichts zu bekommen.
    /// </summary>
    public static async Task<string?> Weather(HttpClient http, double lat, double lon, DateOnly day, CancellationToken ct, int? hour = null)
    {
        if (hour is not (>= 7 and <= 20)) hour = null;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (day > today.AddDays(14)) return null;
        // Das Archiv hat die letzten Tage noch nicht - dafür die Vorhersage mit vergangenen Tagen
        var archive = day < today.AddDays(-5);
        var url = (archive ? "https://archive-api.open-meteo.com/v1/archive" : "https://api.open-meteo.com/v1/forecast")
            + $"?latitude={lat.ToString("0.####", CultureInfo.InvariantCulture)}&longitude={lon.ToString("0.####", CultureInfo.InvariantCulture)}"
            + $"&start_date={day:yyyy-MM-dd}&end_date={day:yyyy-MM-dd}&daily=weather_code,temperature_2m_max,temperature_2m_min&hourly=weather_code,temperature_2m&timezone=auto";
        try
        {
            using var doc = JsonDocument.Parse(await http.GetStringAsync(url, ct));
            var d = doc.RootElement.GetProperty("daily");
            double? Num(string name) => d.TryGetProperty(name, out var a) && a.GetArrayLength() > 0 && a[0].ValueKind == JsonValueKind.Number ? a[0].GetDouble() : null;
            var at = hour is int h ? AtHour(doc.RootElement, h) : null;
            var code = at?.Code ?? Daytime(doc.RootElement) ?? Num("weather_code");
            var max = Num("temperature_2m_max");
            var min = Num("temperature_2m_min");
            if (max == null && code == null) return null;
            var temp = max != null && min != null ? $"{Math.Round(max.Value):0} / {Math.Round(min.Value):0} °C" : max != null ? $"{Math.Round(max.Value):0} °C" : null;
            if (at?.Temp is double now) temp = $"{Math.Round(now):0} °C" + (temp != null ? $" (Tag {temp})" : "");
            return string.Join(", ", new[] { code is double c ? Describe((int)c) : null, temp }.Where(x => x != null));
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or KeyNotFoundException or TaskCanceledException or InvalidOperationException) { return null; }
    }

    /// <summary>Häufigster Wettercode von 9 bis 18 Uhr Ortszeit (bei Gleichstand der spätere), null ohne Stundenwerte.</summary>
    internal static double? Daytime(JsonElement root)
    {
        if (!root.TryGetProperty("hourly", out var h) || !h.TryGetProperty("time", out var t) || !h.TryGetProperty("weather_code", out var c)) return null;
        var codes = new List<int>();
        for (var i = 0; i < Math.Min(t.GetArrayLength(), c.GetArrayLength()); i++)
            if (c[i].ValueKind == JsonValueKind.Number && DateTime.TryParse(t[i].GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) && at.Hour is >= 9 and <= 18)
                codes.Add(c[i].GetInt32());
        if (codes.Count == 0) return null;
        return codes.Select((x, i) => (x, i)).GroupBy(p => p.x).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Max(p => p.i)).First().Key;
    }

    /// <summary>Wettercode und Temperatur der Stunde (Ortszeit), null ohne Stundenwerte.</summary>
    internal static (double? Code, double? Temp)? AtHour(JsonElement root, int hour)
    {
        if (!root.TryGetProperty("hourly", out var h) || !h.TryGetProperty("time", out var t)) return null;
        for (var i = 0; i < t.GetArrayLength(); i++)
        {
            if (!DateTime.TryParse(t[i].GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) || at.Hour != hour) continue;
            double? Get(string name) => h.TryGetProperty(name, out var a) && i < a.GetArrayLength() && a[i].ValueKind == JsonValueKind.Number ? a[i].GetDouble() : null;
            var code = Get("weather_code");
            var temp = Get("temperature_2m");
            return code == null && temp == null ? null : (code, temp);
        }
        return null;
    }

    /// <summary>WMO-Wettercode in Worten.</summary>
    public static string Describe(int code) => code switch
    {
        0 => "sonnig",
        1 or 2 => "heiter",
        3 => "bedeckt",
        45 or 48 => "Nebel",
        >= 51 and <= 57 => "Nieselregen",
        >= 61 and <= 67 => "Regen",
        >= 71 and <= 77 => "Schnee",
        >= 80 and <= 82 => "Regenschauer",
        85 or 86 => "Schneeschauer",
        >= 95 => "Gewitter",
        _ => "wechselhaft",
    };
}
