using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Notizi.Sync
{
    /// <summary>Eine Datenbank unter einer Wurzel: ihr Ordner und ihr Name.</summary>
    /// <param name="Folder">Ordnername direkt unter der Wurzel.</param>
    /// <param name="Name">Name aus name.nz; null, wenn er sich nicht lesen
    /// laesst.</param>
    /// <param name="PasswordFits">Das E2EE-Passwort oeffnet sie.</param>
    public sealed record CatalogEntry(string Folder, string? Name, bool PasswordFits)
    {
        public string Title => Name ?? Folder;
    }

    /// <summary>
    /// Die Wurzel des Abgleichs - ein Verzeichnis oder der Server - waehlt der
    /// Benutzer selbst; notizi legt sie nie an. Darunter liegt jede Datenbank
    /// in einem Ordner mit ihrem Namen (docs/konzept-sync.md, Abschnitt 7).
    /// </summary>
    public static class SyncCatalog
    {
        /// <summary>
        /// Die Datenbanken direkt unter der Wurzel. Den Schluessel abzuleiten
        /// kostet je Ordner eine halbe Sekunde - nebeneinander.
        /// </summary>
        public static async Task<List<CatalogEntry>> ScanAsync(
            ISyncBrowser browser, Func<string, ISyncTransport> transportFor, string password, CancellationToken ct = default)
        {
            var (folders, _) = await browser.BrowseAsync("", ct).ConfigureAwait(false);
            var found = await Task.WhenAll(folders.Take(100).Select(async folder =>
            {
                try
                {
                    if (!(await browser.BrowseAsync(folder, ct).ConfigureAwait(false)).IsSyncFolder) return null;
                    var store = await Task.Run(() => SyncStore.OpenAsync(transportFor(folder), password, ct), ct).ConfigureAwait(false);
                    var name = store == null ? null : await store.ReadNameAsync(ct).ConfigureAwait(false);
                    return new CatalogEntry(folder, name, store != null);
                }
                catch
                {
                    return null;
                }
            })).ConfigureAwait(false);
            return found.Where(e => e != null).Select(e => e!)
                        .OrderBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase)
                        .ToList();
        }

        /// <summary>Der Ordnername fuer eine Datenbank: ihr Name, ohne Zeichen,
        /// die ein Dateisystem oder Server nicht mag.</summary>
        public static string FolderName(string name)
        {
            var sb = new StringBuilder();
            foreach (var c in name.Trim())
                sb.Append(c < 32 || "\\/:*?\"<>|".IndexOf(c) >= 0 ? '_' : c);
            var result = sb.ToString().Trim().TrimEnd('.');
            return result.Length == 0 ? "notizi" : result;
        }
    }
}
