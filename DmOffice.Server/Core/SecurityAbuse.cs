using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DmOffice.Core;

/// <summary>
/// Security Center mit AbuseIPDB (abuseipdb.com, Betreiber in den USA): unbekannte Adressen beim ersten Kontakt prüfen und ab
/// der eingestellten Schwelle sofort sperren, statt zu warten, bis sie hier etwas abtasten; in Fallen Gelaufene auf Wunsch
/// melden. Beides schickt Adressen von Besuchern an den Dienst - darum je ein eigener Schalter, beide aus, bis der Admin sie
/// einschaltet (und die Datenschutzerklärung ergänzt). Das Ergebnis gilt einen Tag; antwortet der Dienst nicht schnell, geht
/// die Anfrage normal weiter.
/// </summary>
public sealed partial class SecurityCenter
{
    private static readonly HttpClient AbuseHttp = new() { BaseAddress = new Uri("https://api.abuseipdb.com/api/v2/"), Timeout = TimeSpan.FromSeconds(10) };
    /// <summary>So lange gilt ein Ergebnis (der kostenlose Zugang erlaubt 1000 Abfragen am Tag).</summary>
    private static readonly TimeSpan ReputationValid = TimeSpan.FromHours(24);
    /// <summary>Länger wartet eine Anfrage nicht auf die Antwort.</summary>
    private static readonly TimeSpan ReputationWait = TimeSpan.FromMilliseconds(1500);

    private readonly ConcurrentDictionary<string, (int Score, DateTime At)> _reputation = new();
    private readonly ConcurrentDictionary<string, Task<int?>> _asking = new();
    private volatile string? _abuseKey;
    private DateTime _abusePausedUntil;

    /// <summary>Für Tests: statt AbuseIPDB zu fragen (Schlüssel, Adresse → Wert oder null).</summary>
    internal static Func<string, string, Task<int?>>? CheckOverride;
    /// <summary>Für Tests: Meldungen hier sammeln statt sie zu senden (Adresse, Kategorien, Kommentar).</summary>
    internal static Action<string, string, string>? ReportOverride;

    public bool AbuseKeySet => !string.IsNullOrEmpty(_abuseKey);

    private void LoadAbuse(SqliteConnection conn, Dictionary<string, string> kv)
    {
        _abuseKey = kv.GetValueOrDefault("abuseKey");
        _reputation.Clear();
        foreach (var (k, score, at) in UserDb.Query(conn, "SELECT key, score, at FROM reputation", r => (r.GetString(0), r.GetInt32(1), r.GetInt64(2))))
            _reputation[k] = (score, Time(at));
    }

    /// <summary>Schlüssel speichern (leer = entfernen). Er liegt nur in der verschlüsselten security.db und wird nie angezeigt.</summary>
    public void SaveAbuseKey(string? key)
    {
        key = key?.Trim();
        using var conn = Open();
        if (string.IsNullOrEmpty(key)) UserDb.Exec(conn, "DELETE FROM kv WHERE key = 'abuseKey'");
        else UserDb.Exec(conn, "INSERT OR REPLACE INTO kv(key, value) VALUES('abuseKey', $v)", ("$v", key));
        _abuseKey = string.IsNullOrEmpty(key) ? null : key;
        _abusePausedUntil = DateTime.MinValue;
    }

    /// <summary>Schlüssel prüfen (der übergebene, sonst der gespeicherte): eine Abfrage für eine bekannte öffentliche Adresse.</summary>
    public async Task<string?> VerifyAbuseKeyAsync(string? key, CancellationToken ct)
    {
        key = string.IsNullOrWhiteSpace(key) ? _abuseKey : key.Trim();
        if (string.IsNullOrEmpty(key)) return "Kein API-Schlüssel eingetragen.";
        try
        {
            using var res = await SendCheckAsync(key, "8.8.8.8", ct);
            return res.StatusCode switch
            {
                HttpStatusCode.OK => null,
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "AbuseIPDB lehnt den Schlüssel ab.",
                HttpStatusCode.TooManyRequests => "Das Tageskontingent bei AbuseIPDB ist aufgebraucht.",
                _ => $"AbuseIPDB antwortet mit {(int)res.StatusCode}.",
            };
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return "AbuseIPDB ist nicht erreichbar: " + e.Message; }
    }

    private static async Task<HttpResponseMessage> SendCheckAsync(string key, string ip, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "check?ipAddress=" + Uri.EscapeDataString(ip) + "&maxAgeInDays=90");
        req.Headers.Add("Key", key);
        req.Headers.Accept.ParseAdd("application/json");
        return await AbuseHttp.SendAsync(req, ct);
    }

    /// <summary>Wert der Adresse (0–100) - aus dem Zwischenspeicher oder frisch gefragt; null, wenn nichts zu erfahren war.</summary>
    public async Task<int?> ReputationAsync(string key, IPAddress ip)
    {
        if (_reputation.TryGetValue(key, out var r) && DateTime.UtcNow - r.At < ReputationValid) return r.Score;
        if (_abuseKey is not { Length: > 0 } apiKey || DateTime.UtcNow < _abusePausedUntil) return null;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var task = _asking.GetOrAdd(key, _ => AskAsync(key, ip.ToString(), apiKey));
        try { return await task.WaitAsync(ReputationWait); }
        catch (TimeoutException) { return null; }
    }

    private async Task<int?> AskAsync(string key, string ip, string apiKey)
    {
        try
        {
            int? score;
            if (CheckOverride != null) score = await CheckOverride(apiKey, ip);
            else
            {
                using var res = await SendCheckAsync(apiKey, ip, CancellationToken.None);
                // Kontingent aufgebraucht oder Schlüssel ungültig: bis morgen nicht mehr fragen
                if (res.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    _abusePausedUntil = DateTime.UtcNow.AddHours(24);
                    SystemLog.Add(Microsoft.Extensions.Logging.LogLevel.Warning, "Sicherheit", $"AbuseIPDB antwortet mit {(int)res.StatusCode} - Prüfung bis morgen ausgesetzt.");
                    return null;
                }
                if (!res.IsSuccessStatusCode) return null;
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
                score = doc.RootElement.GetProperty("data").GetProperty("abuseConfidenceScore").GetInt32();
            }
            if (score is int s)
            {
                var now = DateTime.UtcNow;
                _reputation[key] = (s, now);
                using var conn = Open();
                UserDb.Exec(conn, "INSERT OR REPLACE INTO reputation(key, score, at) VALUES($k,$s,$a)", ("$k", key), ("$s", s), ("$a", Unix(now)));
            }
            return score;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
        finally { _asking.TryRemove(key, out _); }
    }

    /// <summary>Bei AbuseIPDB als Angreifer bekannt: festhalten und für die eingestellten Tage sperren (ohne Stufe hochzuzählen, gemeldet wird nicht).</summary>
    public void ReputationBlock(string key, int score, string method, string path, string? agent)
    {
        Log(key, "ruf", method, path, agent, $"AbuseIPDB {score} %");
        BlockNow(key, $"AbuseIPDB: {score} %", DateTime.UtcNow.AddDays(Math.Max(1, _settings.AbuseDays)), null);
    }

    /// <summary>In eine Falle gelaufen: an AbuseIPDB melden, auf Englisch und mit dem Namen des Systems (nur einzelne Adressen, nicht Netze).</summary>
    private void ReportAttack(string key, string kind, string method, string path)
    {
        if (!_settings.AbuseReport || _abuseKey is not { Length: > 0 } apiKey || !IPAddress.TryParse(key, out _)) return;
        // 21 = Web App Attack, 14 = Port Scan (für Scanner im User-Agent zusätzlich), 15 = Hacking
        var categories = kind == "scanner" ? "21,14" : "21,15";
        var comment = Cut((kind == "scanner"
            ? "dmOffice (dmoffice.de) Security Center: scanner user agent, request "
            : "dmOffice (dmoffice.de) Security Center: probing a path that does not exist, without credentials: ") + method + " " + path, 1000);
        if (ReportOverride != null) { ReportOverride(key, categories, comment); return; }
        _ = Task.Run(async () =>
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, "report")
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["ip"] = key, ["categories"] = categories, ["comment"] = comment }),
                };
                req.Headers.Add("Key", apiKey);
                req.Headers.Accept.ParseAdd("application/json");
                using var res = await AbuseHttp.SendAsync(req);
                if (res.IsSuccessStatusCode) Log(key, "gemeldet", method, path, null, "an AbuseIPDB gemeldet");
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { /* Melden ist eine Gefälligkeit - kein Fehler für uns */ }
        });
    }
}
