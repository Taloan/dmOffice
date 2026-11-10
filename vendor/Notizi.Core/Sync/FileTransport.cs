using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Notizi.Sync
{
    /// <summary>
    /// Abgleichordner als gewoehnlicher Ordner auf der Platte - den ein
    /// OneDrive- oder Nextcloud-Client uebertraegt (docs/konzept-sync.md,
    /// Abschnitt 7, Ordner-Modus). Kein Netzwerkcode, keine Anmeldung.
    /// </summary>
    public sealed class FileTransport : ISyncTransport, ISyncBrowser
    {
        public string Root { get; }

        public FileTransport(string root) => Root = Path.GetFullPath(root);

        public Task<byte[]?> ReadAsync(string path, CancellationToken ct = default)
        {
            var full = Full(path);
            return Task.FromResult(File.Exists(full) ? File.ReadAllBytes(full) : null);
        }

        public Task<string> WriteAsync(string path, byte[] data, string? expectedTag = null, CancellationToken ct = default)
        {
            var full = Full(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);

            // Nicht atomar wie bei WebDAV - zwischen Pruefen und Schreiben
            // koennte der Sync-Client eine Datei hinlegen. Das Fenster ist
            // klein, und was dabei durchrutscht, legt der Client selbst als
            // Konfliktkopie ab; die liest der Abgleich wie einen Konflikt.
            if (expectedTag != null)
            {
                var current = File.Exists(full) ? TagOf(new FileInfo(full)) : "";
                if (current != expectedTag) throw new SyncPreconditionException(path);
            }

            // Erst daneben schreiben, dann umbenennen: Ein Sync-Client, der
            // mitten im Schreiben zugreift, sieht entweder die alte oder die
            // neue Datei, nie eine halbe. Er uebergeht .tmp, der Abgleich auch.
            var temp = full + ".tmp";
            File.WriteAllBytes(temp, data);
            File.Move(temp, full, overwrite: true);
            return Task.FromResult(TagOf(new FileInfo(full)));
        }

        public Task DeleteAsync(string path, CancellationToken ct = default)
        {
            var full = Full(path);
            if (File.Exists(full)) File.Delete(full);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RemoteFile>> ListAsync(string folder, CancellationToken ct = default)
        {
            var dir = Full(folder);
            IReadOnlyList<RemoteFile> result = Directory.Exists(dir)
                ? new DirectoryInfo(dir).EnumerateFiles()
                    .Where(f => !f.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                    .Select(f => new RemoteFile(f.Name, TagOf(f)))
                    .ToList()
                : Array.Empty<RemoteFile>();
            return Task.FromResult(result);
        }

        public Task EnsureFolderAsync(string folder, CancellationToken ct = default)
        {
            Directory.CreateDirectory(Full(folder));
            return Task.CompletedTask;
        }

        /// <summary>Unterordner - wie <see cref="WebDavTransport.BrowseAsync"/>.
        /// Versteckte und solche ohne Leserecht bleiben weg.</summary>
        public Task<(IReadOnlyList<string> Folders, bool IsSyncFolder)> BrowseAsync(string folder, CancellationToken ct = default)
        {
            var dir = new DirectoryInfo(Full(folder));
            var folders = new List<string>();
            bool isSync = false;
            if (dir.Exists)
            {
                try
                {
                    folders = dir.EnumerateDirectories()
                        .Where(d => !d.Name.StartsWith('.') && (d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                        .Select(d => d.Name)
                        .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
                        .ToList();
                }
                catch (UnauthorizedAccessException) { }
                isSync = File.Exists(Path.Combine(dir.FullName, SyncStore.InfoFile));
            }
            return Task.FromResult<(IReadOnlyList<string>, bool)>((folders, isSync));
        }

        public void Dispose() { }

        /// <summary>Zeitstempel und Groesse. Ein Hash waere sicherer, kostete
        /// aber jeden Durchgang das Lesen jeder Datei.</summary>
        private static string TagOf(FileInfo file) =>
            file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) + ":" +
            file.Length.ToString(CultureInfo.InvariantCulture);

        private string Full(string path)
        {
            var full = Path.GetFullPath(Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(Root, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Pfad ausserhalb des Abgleichordners: " + path);
            return full;
        }
    }
}
