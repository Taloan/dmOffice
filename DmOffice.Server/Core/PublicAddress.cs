using Microsoft.AspNetCore.Http;

namespace DmOffice.Core;

/// <summary>
/// Adresse des Servers für Links, die Empfänger außerhalb der aktuellen Browsersitzung öffnen (Abmeldelinks, Freigabelinks,
/// Bestätigungsmails, Einladungen). Ist eine öffentliche Adresse eingestellt, gilt diese, sonst die Adresse der Anfrage
/// (Schema und Host, hinter einem bestätigten Reverse-Proxy aus X-Forwarded-*).
/// </summary>
public static class PublicAddress
{
    /// <summary>Basis mit PathBase, etwa https://office.example.de (ohne Schrägstrich am Ende).</summary>
    public static string Base(HttpContext http) => Origin(http) + http.Request.PathBase;

    /// <summary>Basis ohne PathBase: Schema, Host und Port.</summary>
    public static string Origin(HttpContext http) =>
        // [feature:publicurl]
        Configured(http.RequestServices.GetService(typeof(ServerConfig)) as ServerConfig) ??
        // [/feature:publicurl]
        FromRequest(http);

    /// <summary>Wie bisher: Schema und Host der Anfrage.</summary>
    public static string FromRequest(HttpContext http) => $"{http.Request.Scheme}://{http.Request.Host}";

    // [feature:publicurl]
    public const string EnvVar = "DMOFFICE_PUBLIC_URL";

    /// <summary>Die eingestellte öffentliche Adresse (Umgebungsvariable vor Verwaltung), null wenn keine oder Kennung aus.</summary>
    public static string? Configured(ServerConfig? config) =>
        !Features.On(Features.PublicUrl) ? null : FromEnv() ?? Normalize(config?.Current.PublicUrl, out _);

    /// <summary>DMOFFICE_PUBLIC_URL, geprüft; null wenn nicht gesetzt oder ungültig.</summary>
    public static string? FromEnv() => Normalize(Environment.GetEnvironmentVariable(EnvVar), out _);

    /// <summary>
    /// Prüft eine Eingabe: absolute http(s)-Adresse ohne Pfad, Abfrage, Anker und Zugangsdaten. Liefert die Form
    /// schema://host[:port] (Host klein) oder null; leer ergibt null ohne Fehler.
    /// </summary>
    public static string? Normalize(string? raw, out string? error)
    {
        error = null;
        var s = (raw ?? "").Trim();
        if (s.Length == 0) return null;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https") || string.IsNullOrEmpty(u.Host)
            || !s.Contains("://", StringComparison.Ordinal))
        {
            error = "Bitte eine vollständige Adresse mit https:// oder http:// angeben.";
            return null;
        }
        if (u.UserInfo.Length > 0 || u.AbsolutePath != "/" || u.Query.Length > 0 || u.Fragment.Length > 0 || s.Contains('?') || s.Contains('#'))
        {
            error = "Bitte nur Protokoll, Rechnername und Port angeben, ohne Pfad.";
            return null;
        }
        return u.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
    }
    // [/feature:publicurl]
}
