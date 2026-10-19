using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Notizi.Sync
{
    /// <summary>Eine Datei im Abgleichordner, mit Kennung ihres Stands.</summary>
    /// <param name="Name">Dateiname ohne Ordner.</param>
    /// <param name="Tag">Aendert sich, sobald sich die Datei aendert - ETag
    /// bei WebDAV, Zeitstempel und Groesse im Ordner.</param>
    public sealed record RemoteFile(string Name, string Tag);

    /// <summary>
    /// Ein Schreiben wurde abgelehnt, weil die Datei inzwischen eine andere
    /// ist als die, auf die sich das Schreiben bezog. Der Abgleich nimmt das
    /// Element im naechsten Durchgang als fremde Aenderung.
    /// </summary>
    /// <summary>Der Abgleichordner wurde neu eingerichtet, seit er geoeffnet
    /// wurde - mit dem alten Schluessel wird nichts mehr geschrieben.</summary>
    public sealed class SyncKeyChangedException : Exception
    {
        public SyncKeyChangedException()
            : base("Der Abgleichordner wurde neu eingerichtet (anderes Passwort). Neu verbinden.") { }
    }

    public sealed class SyncPreconditionException : Exception
    {
        public SyncPreconditionException(string path)
            : base($"{path} wurde inzwischen von einem anderen Geraet geaendert.") { }
    }

    /// <summary>
    /// Die Ablage durchsuchen - Verzeichnis oder WebDAV-Server, beides gleich
    /// bedient: Unterordner zeigen, ob ein Ordner ein Abgleich ist (info.json
    /// darin), neue Ordner anlegen.
    /// </summary>
    public interface ISyncBrowser : IDisposable
    {
        Task<(IReadOnlyList<string> Folders, bool IsSyncFolder)> BrowseAsync(string folder, CancellationToken ct = default);
        Task EnsureFolderAsync(string folder, CancellationToken ct = default);
    }

    /// <summary>
    /// Wie Bytes in den Abgleichordner kommen und wieder heraus. Pfade sind
    /// relativ und mit "/" getrennt: "info.json", "items/&lt;id&gt;.nz",
    /// "blobs/&lt;name&gt;.nzb". Verschluesselt wird darueber, in
    /// <see cref="SyncStore"/> - ein Transport sieht nur Geheimtext.
    /// </summary>
    public interface ISyncTransport
    {
        /// <summary>Null, wenn es die Datei nicht gibt.</summary>
        Task<byte[]?> ReadAsync(string path, CancellationToken ct = default);

        /// <summary>
        /// Schreibt die Datei und gibt ihre neue Kennung zurueck.
        /// <paramref name="expectedTag"/>: nur schreiben, wenn die Datei noch
        /// diese Kennung hat; "" heisst, sie darf noch nicht existieren; null
        /// heisst, ohne Bedingung. Sonst <see cref="SyncPreconditionException"/>.
        /// </summary>
        Task<string> WriteAsync(string path, byte[] data, string? expectedTag = null, CancellationToken ct = default);

        Task DeleteAsync(string path, CancellationToken ct = default);

        /// <summary>Dateien eines Ordners ("items", "blobs"); leer, wenn es
        /// ihn nicht gibt.</summary>
        Task<IReadOnlyList<RemoteFile>> ListAsync(string folder, CancellationToken ct = default);

        /// <summary>Legt einen Ordner an, falls noetig.</summary>
        Task EnsureFolderAsync(string folder, CancellationToken ct = default);
    }
}
