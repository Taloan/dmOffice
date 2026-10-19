using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Notizi.Sync
{
    /// <summary>
    /// Abgleichordner auf einem WebDAV-Server - Nextcloud spricht es direkt,
    /// ebenso viele NAS (docs/konzept-sync.md, Abschnitt 7). Fuer Nextcloud
    /// ist die Adresse
    /// https://server/remote.php/dav/files/&lt;benutzer&gt;/&lt;ordner&gt;/
    /// und das Passwort ein App-Passwort aus den Sicherheitseinstellungen.
    ///
    /// Schreiben ist bedingt (If-Match / If-None-Match): Hat ein anderes
    /// Geraet die Datei seit dem letzten Lesen geaendert, lehnt der Server
    /// ab, statt dessen Version zu ueberschreiben.
    /// </summary>
    public sealed class WebDavTransport : ISyncTransport, ISyncBrowser
    {
        private static readonly XNamespace Dav = "DAV:";

        private readonly HttpClient _http;
        private readonly Uri _root;

        public WebDavTransport(Uri root, string user, string password, HttpMessageHandler? handler = null)
        {
            _root = root.AbsoluteUri.EndsWith('/') ? root : new Uri(root.AbsoluteUri + "/");
            _http = handler == null ? new HttpClient() : new HttpClient(handler);
            _http.Timeout = TimeSpan.FromSeconds(60);
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password)));
        }

        public void Dispose() => _http.Dispose();

        private Uri At(string path) => new(_root, string.Join("/", path.Split('/').Select(Uri.EscapeDataString)));

        public async Task<byte[]?> ReadAsync(string path, CancellationToken ct = default)
        {
            using var response = await _http.GetAsync(At(path), ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            Ensure(response, "GET", path);
            return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }

        public async Task<string> WriteAsync(string path, byte[] data, string? expectedTag = null, CancellationToken ct = default)
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, At(path))
            {
                Content = new ByteArrayContent(data)
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            if (expectedTag == "")
                request.Headers.TryAddWithoutValidation("If-None-Match", "*");
            else if (expectedTag != null)
                request.Headers.TryAddWithoutValidation("If-Match", expectedTag);

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.PreconditionFailed)
                throw new SyncPreconditionException(path);
            Ensure(response, "PUT", path);

            if (response.Headers.ETag != null) return response.Headers.ETag.ToString();
            if (response.Headers.TryGetValues("OC-ETag", out var oc)) return Quote(oc.First());

            // Ohne ETag in der Antwort nachfragen.
            var folder = path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
            var name = path[(path.LastIndexOf('/') + 1)..];
            var listed = await ListAsync(folder, ct).ConfigureAwait(false);
            return listed.FirstOrDefault(f => f.Name == name)?.Tag
                ?? throw new InvalidOperationException($"Der Server nennt fuer {path} keine Kennung.");
        }

        public async Task DeleteAsync(string path, CancellationToken ct = default)
        {
            using var response = await _http.SendAsync(new HttpRequestMessage(HttpMethod.Delete, At(path)), ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return;
            Ensure(response, "DELETE", path);
        }

        /// <summary>
        /// Unterordner eines Ordners - zum Durchklicken auf dem Server, um
        /// einen Abgleichordner zu finden. Dazu, ob der Ordner selbst einer
        /// ist (info.json darin).
        /// </summary>
        public async Task<(IReadOnlyList<string> Folders, bool IsSyncFolder)> BrowseAsync(string folder, CancellationToken ct = default)
        {
            var uri = folder.Length == 0 ? _root : new Uri(At(folder).AbsoluteUri.TrimEnd('/') + "/");
            using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), uri)
            {
                Content = new StringContent(
                    "<?xml version=\"1.0\"?><d:propfind xmlns:d=\"DAV:\"><d:prop><d:resourcetype/></d:prop></d:propfind>",
                    Encoding.UTF8, "application/xml")
            };
            request.Headers.Add("Depth", "1");

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            Ensure(response, "PROPFIND", folder);

            var xml = XDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var self = uri.AbsolutePath.TrimEnd('/');
            var folders = new List<string>();
            bool isSyncFolder = false;

            foreach (var entry in xml.Descendants(Dav + "response"))
            {
                var href = (string?)entry.Element(Dav + "href");
                if (href == null) continue;
                var path = Uri.UnescapeDataString(href.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? new Uri(href).AbsolutePath : href).TrimEnd('/');
                if (path.Equals(Uri.UnescapeDataString(self), StringComparison.Ordinal)) continue;

                var name = path.Split('/').Last();
                bool collection = entry.Descendants(Dav + "collection").Any();
                if (collection) folders.Add(name);
                else if (name == SyncStore.InfoFile) isSyncFolder = true;
            }

            folders.Sort(StringComparer.CurrentCultureIgnoreCase);
            return (folders, isSyncFolder);
        }

        public async Task<IReadOnlyList<RemoteFile>> ListAsync(string folder, CancellationToken ct = default)
        {
            var uri = folder.Length == 0 ? _root : new Uri(At(folder).AbsoluteUri.TrimEnd('/') + "/");
            using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), uri)
            {
                Content = new StringContent(
                    "<?xml version=\"1.0\"?><d:propfind xmlns:d=\"DAV:\"><d:prop>" +
                    "<d:getetag/><d:getlastmodified/><d:getcontentlength/><d:resourcetype/>" +
                    "</d:prop></d:propfind>", Encoding.UTF8, "application/xml")
            };
            request.Headers.Add("Depth", "1");

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return Array.Empty<RemoteFile>();
            Ensure(response, "PROPFIND", folder);

            var xml = XDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var result = new List<RemoteFile>();

            foreach (var entry in xml.Descendants(Dav + "response"))
            {
                var href = (string?)entry.Element(Dav + "href");
                if (href == null) continue;

                var props = entry.Descendants(Dav + "prop").FirstOrDefault(p =>
                    ((string?)p.Parent?.Element(Dav + "status") ?? "").Contains(" 200 "));
                if (props == null) continue;
                if (props.Element(Dav + "resourcetype")?.Element(Dav + "collection") != null) continue;

                var name = Uri.UnescapeDataString(href.TrimEnd('/').Split('/').Last());
                if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;

                var tag = (string?)props.Element(Dav + "getetag");
                if (string.IsNullOrEmpty(tag))
                    tag = (string?)props.Element(Dav + "getlastmodified") + ":" + (string?)props.Element(Dav + "getcontentlength");
                result.Add(new RemoteFile(name, tag));
            }

            return result;
        }

        public async Task EnsureFolderAsync(string folder, CancellationToken ct = default)
        {
            var uri = folder.Length == 0 ? _root : new Uri(At(folder).AbsoluteUri.TrimEnd('/') + "/");
            using var response = await _http.SendAsync(new HttpRequestMessage(new HttpMethod("MKCOL"), uri), ct).ConfigureAwait(false);
            // 405: gibt es schon.
            if (response.StatusCode == HttpStatusCode.MethodNotAllowed) return;
            Ensure(response, "MKCOL", folder);
        }

        private static string Quote(string tag) => tag.StartsWith('"') ? tag : "\"" + tag + "\"";

        private static void Ensure(HttpResponseMessage response, string method, string path)
        {
            if (response.IsSuccessStatusCode) return;
            var reason = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "Anmeldung abgelehnt - Benutzer oder App-Passwort stimmt nicht",
                HttpStatusCode.Forbidden => "keine Berechtigung",
                HttpStatusCode.InsufficientStorage => "Speicher auf dem Server voll",
                _ => $"{(int)response.StatusCode} {response.ReasonPhrase}"
            };
            throw new HttpRequestException($"WebDAV {method} {path}: {reason}.", null, response.StatusCode);
        }
    }
}
