using System;
using System.Collections.Generic;

namespace Notizi.Sync
{
    /// <summary>
    /// Ein Link, der eine WebDAV-Verbindung beschreibt - so, wie dmOffice ihn
    /// unter "notizi verbinden" als QR-Code und zum Kopieren anbietet:
    ///
    ///     notizi://webdav?url=&lt;Adresse&gt;&amp;user=&lt;Anmeldename&gt;&amp;password=&lt;App-Passwort&gt;
    ///
    /// UTF-8, alle Werte URL-kodiert; url ist schon die Wurzel des
    /// Abgleichordners. Das E2EE-Passwort steht bewusst nicht darin - der
    /// Server soll es nicht weitergeben koennen, und es bleibt eine eigene
    /// Eingabe.
    ///
    /// Gelesen wird nur; verbunden wird erst, wenn der Benutzer die Angaben
    /// gesehen und uebernommen hat. Ein Link kann von ueberall kommen.
    /// </summary>
    public sealed record SyncLink(Uri Url, string User, string Password)
    {
        public const string Scheme = "notizi";

        /// <summary>Der Rechner, wie er im Dialog genannt wird - damit man sieht, wohin es geht.</summary>
        public string Host => Url.Host;

        /// <summary>
        /// Sucht den Link in einem Text (auch mit Leerraum oder Text drumherum,
        /// etwa aus einer Mail) und liest ihn. Null, wenn keiner darin steht
        /// oder er nicht taugt: andere Art als webdav, Adresse nicht http(s),
        /// Anmeldename fehlt.
        /// </summary>
        public static SyncLink? TryParse(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            int start = text.IndexOf(Scheme + "://", StringComparison.OrdinalIgnoreCase);
            if (start < 0) return null;
            int end = start;
            while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] is not ('"' or '<' or '>')) end++;
            var link = text[start..end];

            var rest = link[(Scheme.Length + 3)..];
            int q = rest.IndexOf('?');
            if (q < 0) return null;
            if (!string.Equals(rest[..q].TrimEnd('/'), "webdav", StringComparison.OrdinalIgnoreCase)) return null;

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in rest[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                var name = Uri.UnescapeDataString((eq < 0 ? pair : pair[..eq]).Replace('+', ' '));
                var value = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
                values.TryAdd(name, value);
            }

            if (!values.TryGetValue("url", out var url) || !values.TryGetValue("user", out var user) || user.Length == 0)
                return null;
            values.TryGetValue("password", out var password);
            if (!url.EndsWith('/')) url += "/";
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) return null;
            return new SyncLink(uri, user, password ?? "");
        }
    }
}
