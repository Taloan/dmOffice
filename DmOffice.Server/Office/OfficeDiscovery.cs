using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DmOffice.Core;

namespace DmOffice.Office;

/// <summary>Was der Office-Server kann: je Endung die Adresse des Editors (edit) bzw. Betrachters (view), dazu die Proof-Keys.</summary>
public sealed class Discovery
{
    public Dictionary<string, string> Edit { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> View { get; } = new(StringComparer.OrdinalIgnoreCase);
    public RSAParameters? ProofKey { get; init; }
    public RSAParameters? OldProofKey { get; init; }
    /// <summary>"collabora", "onlyoffice" oder "wopi" (unbekannt) - nur fuer Anzeige und kleine Unterschiede.</summary>
    public string Product { get; init; } = "wopi";
    /// <summary>ONLYOFFICE ueber die eigene Docs-API (api.js + Callback) statt WOPI.</summary>
    public bool Native { get; init; }

    /// <summary>Endungen, die in dmOffice wirklich bearbeitet werden sollen (nicht etwa .txt oder Bilder).</summary>
    public static readonly HashSet<string> OfficeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "docx", "docm", "dotx", "doc", "odt", "ott", "rtf",
        "xlsx", "xlsm", "xltx", "xls", "ods", "ots", "csv",
        "pptx", "pptm", "potx", "ppt", "odp", "otp",
    };

    /// <summary>XML von /hosting/discovery lesen. Collabora nennt Apps nach MIME-Typ (ext leer) und zusaetzlich nach Endung,
    /// ONLYOFFICE nur nach Endung - beides wird zu einer Tabelle Endung → urlsrc.</summary>
    public static Discovery Parse(string xml)
    {
        // Keine DTD: die Discovery kommt vom Office-Server ueber das Netz (Entity-Expansion, Laufzeit)
        using var reader = System.Xml.XmlReader.Create(new StringReader(xml), new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null });
        var doc = XDocument.Load(reader);
        var root = doc.Root ?? throw new FormatException("Leere Antwort.");
        if (root.Name.LocalName != "wopi-discovery") throw new FormatException("Keine WOPI-Discovery (Wurzel <wopi-discovery> fehlt).");
        var proof = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "proof-key");
        var text = xml.Contains("onlyoffice", StringComparison.OrdinalIgnoreCase) ? "onlyoffice"
            : xml.Contains("cool.html", StringComparison.OrdinalIgnoreCase) || xml.Contains("loleaflet", StringComparison.OrdinalIgnoreCase) ? "collabora" : "wopi";
        var d = new Discovery
        {
            ProofKey = Key(proof, "modulus", "exponent"),
            OldProofKey = Key(proof, "oldmodulus", "oldexponent"),
            Product = text,
        };
        foreach (var app in root.Descendants().Where(e => e.Name.LocalName == "app"))
        {
            var appName = (string?)app.Attribute("name") ?? "";
            foreach (var action in app.Elements().Where(e => e.Name.LocalName == "action"))
            {
                var name = ((string?)action.Attribute("name") ?? "").ToLowerInvariant();
                var url = (string?)action.Attribute("urlsrc");
                if (string.IsNullOrEmpty(url)) continue;
                // Die Adresse wird im Browser zum Formularziel des Editor-Rahmens: nur http(s), nie javascript: o. ae.
                if (!Uri.TryCreate(url.Split('<')[0], UriKind.Absolute, out var u) || u.Scheme is not ("https" or "http")) continue;
                var ext = ((string?)action.Attribute("ext") ?? "").TrimStart('.');
                var exts = ext.Length > 0 ? [ext] : ExtensionsOf(appName);
                foreach (var e in exts)
                {
                    if (name == "edit") d.Edit.TryAdd(e, url);
                    else if (name is "view" or "embedview" or "view_comment" or "readonly") d.View.TryAdd(e, url);
                }
            }
        }
        // Wo es nur "edit" gibt, ist das auch der Betrachter (der Token sagt dann: nur lesen)
        foreach (var (e, url) in d.Edit) d.View.TryAdd(e, url);
        if (d.Edit.Count == 0 && d.View.Count == 0) throw new FormatException("Die Discovery nennt keine Dateiarten.");
        return d;
    }

    private static RSAParameters? Key(XElement? proof, string mod, string exp)
    {
        var m = (string?)proof?.Attribute(mod);
        var x = (string?)proof?.Attribute(exp);
        if (string.IsNullOrWhiteSpace(m) || string.IsNullOrWhiteSpace(x)) return null;
        try { return new RSAParameters { Modulus = Convert.FromBase64String(m), Exponent = Convert.FromBase64String(x) }; }
        catch (FormatException) { return null; }
    }

    /// <summary>Collabora nennt manche Apps nach MIME-Typ - die passenden Endungen dazu.</summary>
    private static string[] ExtensionsOf(string mime) => mime switch
    {
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => ["docx"],
        "application/vnd.ms-word.document.macroEnabled.12" => ["docm"],
        "application/vnd.openxmlformats-officedocument.wordprocessingml.template" => ["dotx"],
        "application/msword" => ["doc"],
        "application/vnd.oasis.opendocument.text" => ["odt"],
        "application/vnd.oasis.opendocument.text-template" => ["ott"],
        "application/rtf" or "text/rtf" => ["rtf"],
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => ["xlsx"],
        "application/vnd.ms-excel.sheet.macroEnabled.12" => ["xlsm"],
        "application/vnd.openxmlformats-officedocument.spreadsheetml.template" => ["xltx"],
        "application/vnd.ms-excel" => ["xls"],
        "application/vnd.oasis.opendocument.spreadsheet" => ["ods"],
        "application/vnd.oasis.opendocument.spreadsheet-template" => ["ots"],
        "text/csv" => ["csv"],
        "application/vnd.openxmlformats-officedocument.presentationml.presentation" => ["pptx"],
        "application/vnd.ms-powerpoint.presentation.macroEnabled.12" => ["pptm"],
        "application/vnd.openxmlformats-officedocument.presentationml.template" => ["potx"],
        "application/vnd.ms-powerpoint" => ["ppt"],
        "application/vnd.oasis.opendocument.presentation" => ["odp"],
        "application/vnd.oasis.opendocument.presentation-template" => ["otp"],
        _ => [],
    };

    /// <summary>Adresse fuer den iframe: Platzhalter wie &lt;ui=UI_LLCC&amp;&gt; ersetzen oder streichen, WOPISrc anhaengen.</summary>
    public static string ActionUrl(string urlsrc, string wopiSrc, string lang)
    {
        var url = Regex.Replace(urlsrc, "<([^=<>]+)=([^&<>]+)&?>", m => m.Groups[2].Value switch
        {
            "UI_LLCC" or "DC_LLCC" => $"{m.Groups[1].Value}={lang}&",
            _ => "",
        });
        url = url.TrimEnd('&');
        var sep = url.EndsWith('?') ? "" : url.Contains('?') ? "&" : "?";
        // lang und closebutton versteht Collabora; andere Office-Server lassen unbekannte Parameter liegen
        return url + sep + "WOPISrc=" + Uri.EscapeDataString(wopiSrc) + "&lang=" + lang + "&closebutton=1";
    }
}

/// <summary>Liest und behaelt die Discovery des eingestellten Office-Servers (eine Stunde, Fehler eine Minute).</summary>
public sealed class OfficeDiscovery(ServerConfig config, IHttpClientFactory http, ILogger<OfficeDiscovery> log)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (string Url, Discovery? Value, string? Error, DateTime At)? _cache;

    public bool Configured => !string.IsNullOrWhiteSpace(config.Current.OfficeUrl);

    /// <summary>Die Discovery, oder null, wenn kein Office-Server eingestellt oder er gerade nicht erreichbar ist.</summary>
    public async Task<Discovery?> Get(CancellationToken ct = default)
    {
        var url = config.Current.OfficeUrl?.Trim();
        if (string.IsNullOrEmpty(url)) return null;
        if (_cache is { } c && c.Url == url && DateTime.UtcNow - c.At < (c.Value != null ? TimeSpan.FromHours(1) : TimeSpan.FromMinutes(1))) return c.Value;
        await _gate.WaitAsync(ct);
        try
        {
            if (_cache is { } c2 && c2.Url == url && DateTime.UtcNow - c2.At < TimeSpan.FromSeconds(5)) return c2.Value;
            var (value, error) = await Fetch(url, ct);
            if (error != null) log.LogWarning("Office-Server {Url}: {Error}", url, error);
            // Proof-Keys koennen wechseln: eine fehlerhafte Abfrage loescht die letzte gute nicht sofort
            if (value == null && _cache is { Value: { } old } prev && prev.Url == url && DateTime.UtcNow - prev.At < TimeSpan.FromHours(6))
            {
                _cache = (url, old, error, DateTime.UtcNow.AddMinutes(-59));
                return old;
            }
            if (value?.ProofKey != null) _proofUrl = url;
            _cache = (url, value, error, DateTime.UtcNow);
            return value;
        }
        finally { _gate.Release(); }
    }

    private string? _proofUrl;
    private DateTime _lastProofRefresh;

    /// <summary>Hat der eingestellte Office-Server schon einmal Proof-Keys geliefert? Dann verlangt die Proof-Pruefung sie.</summary>
    public bool ProofExpected => _proofUrl != null && _proofUrl == config.Current.OfficeUrl?.Trim();

    /// <summary>Fuer die Proof-Pruefung: hoechstens einmal je Minute neu lesen. Die letzte gute Discovery bleibt dabei
    /// erhalten (als veraltet markiert), damit ein fehlgeschlagener Abruf die Pruefung nicht aushebelt.</summary>
    public bool RefreshForProof()
    {
        if (DateTime.UtcNow - _lastProofRefresh < TimeSpan.FromMinutes(1)) return false;
        _lastProofRefresh = DateTime.UtcNow;
        if (_cache is { } c) _cache = (c.Url, c.Value, c.Error, DateTime.UtcNow.AddHours(-2));
        return true;
    }

    /// <summary>Nach einer Aenderung der Einstellung neu lesen (die Verwaltung setzt damit auch die Proof-Erwartung zurueck).</summary>
    public void Forget() { _cache = null; _proofUrl = null; }

    /// <summary>Einmal pruefen, ohne den Zwischenspeicher - fuer die Verwaltung. Fehler als lesbarer Satz.</summary>
    /// <param name="useStored">JWT-Geheimnis und Kopfzeile aus der Einstellung nehmen, wenn keine angegeben sind</param>
    public async Task<(Discovery? Value, string? Error)> Fetch(string baseUrl, CancellationToken ct, string? secret = null, string? header = null, bool useStored = true)
    {
        if (useStored) { secret ??= config.OfficeJwtSecret(); header ??= config.Current.OfficeJwtHeader; }
        if (!Uri.TryCreate(baseUrl.Trim().TrimEnd('/'), UriKind.Absolute, out var b) || (b.Scheme != "https" && b.Scheme != "http"))
            return (null, "Keine gültige Adresse – bitte mit https:// angeben, etwa https://office.example.org.");
        var target = new Uri(b.AbsoluteUri.TrimEnd('/') + "/hosting/discovery");
        try
        {
            using var client = http.CreateClient("office");
            using var res = await client.GetAsync(target, ct);
            if ((int)res.StatusCode is >= 300 and < 400)
                return (null, $"{target} leitet weiter ({(int)res.StatusCode}) nach {res.Headers.Location} – bitte die Adresse ohne Umleitung angeben.");
            // Keine WOPI-Discovery: vielleicht ONLYOFFICE Docs ohne WOPI - dann ueber dessen eigene Schnittstelle
            if (res.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                using var api = await client.GetAsync(b.AbsoluteUri.TrimEnd('/') + "/web-apps/apps/api/documents/api.js", ct);
                if (api.IsSuccessStatusCode && (await api.Content.ReadAsStringAsync(ct)).Contains("DocsAPI"))
                {
                    var (ok, error, _) = await OnlyOffice.Check(http, b.AbsoluteUri, secret, header, ct);
                    return ok ? (OnlyOffice.NativeDiscovery(), null) : (null, error);
                }
            }
            if (!res.IsSuccessStatusCode)
                return (null, $"{target} antwortet mit {(int)res.StatusCode} {res.ReasonPhrase}. Läuft dort ein Office-Server (Collabora/CODE oder ONLYOFFICE mit eingeschaltetem WOPI)?");
            var body = await res.Content.ReadAsStringAsync(ct);
            try { return (Discovery.Parse(body), null); }
            catch (Exception ex) when (ex is FormatException or System.Xml.XmlException)
            {
                return (null, $"{target} liefert keine gültige WOPI-Discovery: {ex.Message}");
            }
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return (null, $"{target} antwortet nicht (Zeitüberschreitung)."); }
        catch (HttpRequestException ex)
        {
            var reason = ex.InnerException is System.Security.Authentication.AuthenticationException
                ? "Das Zertifikat wird nicht akzeptiert" : ex.Message;
            return (null, $"{target} ist nicht erreichbar: {reason}. Ist die Adresse vom dmOffice-Container aus erreichbar?");
        }
    }
}
