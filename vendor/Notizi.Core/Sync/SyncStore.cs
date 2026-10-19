using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Notizi.Sync
{
    /// <summary>Ablage fuer Anhaenge und Bilder, aus Sicht eines Elements.</summary>
    public interface IBlobStore
    {
        /// <summary>Legt den Inhalt ab und gibt seinen Namen.</summary>
        string PutBlob(byte[] content);

        /// <summary>Null, wenn (noch) nicht da.</summary>
        byte[]? GetBlob(string name);
    }

    /// <summary>
    /// Sammelt Blobs im Speicher - beim Ausfuehren einer Notiz, damit das
    /// Hochladen danach in Ruhe und asynchron geschehen kann, und beim
    /// Einfuehren, nachdem alles Noetige geholt ist.
    /// </summary>
    public sealed class BlobCollector : IBlobStore
    {
        private readonly SyncCrypto _crypto;
        public Dictionary<string, byte[]> Blobs { get; } = new();

        public BlobCollector(SyncCrypto crypto) => _crypto = crypto;

        public string PutBlob(byte[] content)
        {
            var name = _crypto.BlobName(content);
            Blobs[name] = content;
            return name;
        }

        public byte[]? GetBlob(string name) => Blobs.TryGetValue(name, out var data) ? data : null;
    }

    /// <summary>
    /// Der Abgleichordner (docs/konzept-sync.md, Abschnitt 3), ueber einem
    /// beliebigen Transport:
    ///
    ///     info.json          Verfahren, Salz, Pruefwert
    ///     items/&lt;id&gt;.nz     eine Notiz, ein Ordner oder ein Grabstein
    ///     blobs/&lt;name&gt;.nzb  ein Anhang oder Bild
    ///
    /// Alles ausser info.json ist verschluesselt. Die Art eines Elements
    /// steht in der Datei, nicht im Namen - der Server sieht nur GUIDs.
    /// </summary>
    public sealed class SyncStore
    {
        public const string InfoFile = "info.json";
        public const string ItemFolder = "items";
        public const string BlobFolder = "blobs";

        public ISyncTransport Transport { get; }
        public SyncCrypto Crypto { get; }

        /// <summary>info.json, wie sie beim Oeffnen war.</summary>
        private readonly byte[] _info;

        private SyncStore(ISyncTransport transport, SyncCrypto crypto, byte[] info)
        {
            Transport = transport;
            Crypto = crypto;
            _info = info;
        }

        /// <summary>
        /// Prueft vor einem Durchgang, ob der Abgleichordner noch derselbe ist:
        /// Wurde er inzwischen geleert und neu eingerichtet (anderes Passwort,
        /// anderer Schluessel), darf mit dem alten Schluessel nichts mehr
        /// geschrieben werden - sonst laege dort wieder, was mit dem alten
        /// Passwort lesbar ist, und der neue Schluessel koennte es nicht lesen.
        /// </summary>
        public async Task EnsureSameKeyAsync(CancellationToken ct = default)
        {
            var now = await Transport.ReadAsync(InfoFile, ct).ConfigureAwait(false);
            if (now == null || !now.AsSpan().SequenceEqual(_info)) throw new SyncKeyChangedException();
        }

        public static async Task<bool> IsSetUpAsync(ISyncTransport transport, CancellationToken ct = default) =>
            await transport.ReadAsync(InfoFile, ct).ConfigureAwait(false) != null;

        /// <summary>Richtet einen leeren Abgleichordner ein.</summary>
        public static async Task<SyncStore> CreateAsync(ISyncTransport transport, string password,
            int iterations = SyncCrypto.DefaultIterations, CancellationToken ct = default)
        {
            await transport.EnsureFolderAsync("", ct).ConfigureAwait(false);
            var (crypto, info) = SyncCrypto.Create(password, iterations);
            var bytes = Encoding.UTF8.GetBytes(info.ToJson());
            // "" = nur, wenn es noch keinen gibt: Zwei Geraete, die
            // gleichzeitig einrichten, sollen nicht zwei Schluessel erzeugen.
            await transport.WriteAsync(InfoFile, bytes, "", ct).ConfigureAwait(false);
            await transport.EnsureFolderAsync(ItemFolder, ct).ConfigureAwait(false);
            await transport.EnsureFolderAsync(BlobFolder, ct).ConfigureAwait(false);
            return new SyncStore(transport, crypto, bytes);
        }

        /// <summary>
        /// Neues E2EE-Passwort fuer diesen Ordner. Geschrieben wird nur
        /// info.json - die Dateien bleiben unberuehrt, denn ihr Schluessel
        /// aendert sich nicht; er wird nur neu eingepackt.
        ///
        /// False, wenn das bisherige Passwort nicht passt oder der Ordner
        /// noch im Format 1 liegt. Dort haengt der Dateischluessel am
        /// Passwort, und ein Wechsel hiesse: jede Datei neu verschluesseln.
        ///
        /// Wechseln zwei Geraete gleichzeitig, gewinnt eines; das andere
        /// merkt es daran, dass sein neues Passwort nicht mehr oeffnet.
        /// </summary>
        public static async Task<bool> ChangePasswordAsync(ISyncTransport transport,
            string oldPassword, string newPassword,
            int iterations = SyncCrypto.DefaultIterations, CancellationToken ct = default)
        {
            var data = await transport.ReadAsync(InfoFile, ct).ConfigureAwait(false);
            if (data == null) return false;

            var info = SyncCrypto.Info.FromJson(Encoding.UTF8.GetString(data));
            if (info == null) return false;

            var next = SyncCrypto.ChangePassword(info, oldPassword, newPassword, iterations);
            if (next == null) return false;

            await transport.WriteAsync(InfoFile, Encoding.UTF8.GetBytes(next.ToJson()), null, ct)
                           .ConfigureAwait(false);
            return true;
        }

        /// <summary>Null bei falschem Passwort.</summary>
        public static async Task<SyncStore?> OpenAsync(ISyncTransport transport, string password, CancellationToken ct = default)
        {
            var data = await transport.ReadAsync(InfoFile, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Hier ist kein Abgleichordner eingerichtet.");
            var info = SyncCrypto.Info.FromJson(Encoding.UTF8.GetString(data))
                ?? throw new InvalidOperationException("info.json ist leer.");
            var crypto = SyncCrypto.Open(password, info);
            return crypto == null ? null : new SyncStore(transport, crypto, data);
        }

        // ==================================================================
        //  Elemente
        // ==================================================================

        /// <summary>
        /// Alle Elemente mit Kennung. Konfliktkopien eines Sync-Clients
        /// ("&lt;id&gt; (Konflikt …).nz", "&lt;id&gt;.sync-conflict-….nz") kommen
        /// getrennt in <paramref name="conflicts"/>: Name der Datei und die
        /// ID, zu der sie gehoert.
        /// </summary>
        public async Task<Dictionary<string, string>> ListItemsAsync(
            List<(string File, string Id)>? conflicts = null, CancellationToken ct = default)
        {
            var result = new Dictionary<string, string>();
            foreach (var file in await Transport.ListAsync(ItemFolder, ct).ConfigureAwait(false))
            {
                if (!file.Name.EndsWith(".nz", StringComparison.OrdinalIgnoreCase)) continue;
                var stem = file.Name[..^3];
                if (IsId(stem)) result[stem] = file.Tag;
                else if (stem.Length > 32 && IsId(stem[..32])) conflicts?.Add((file.Name, stem[..32]));
            }
            return result;
        }

        public async Task<SyncItem?> ReadItemAsync(string id, CancellationToken ct = default) =>
            await ReadItemFileAsync(id + ".nz", id, ct).ConfigureAwait(false);

        /// <summary>Liest eine Datei, die zu Element <paramref name="id"/>
        /// gehoert - auch eine Konfliktkopie unter anderem Namen.</summary>
        public async Task<SyncItem?> ReadItemFileAsync(string file, string id, CancellationToken ct = default)
        {
            var data = await Transport.ReadAsync(ItemFolder + "/" + file, ct).ConfigureAwait(false);
            if (data == null) return null;
            var item = SyncItem.FromBytes(Crypto.Unseal("item", id, data));
            if (item.Id != id)
                throw new System.IO.InvalidDataException($"Element {id} traegt die ID {item.Id}.");
            return item;
        }

        /// <summary>Schreibt das Element, siehe ISyncTransport.WriteAsync zu
        /// <paramref name="expectedTag"/>. Gibt die neue Kennung.</summary>
        public Task<string> WriteItemAsync(SyncItem item, string? expectedTag, CancellationToken ct = default)
        {
            if (!IsId(item.Id)) throw new ArgumentException("Keine gueltige Element-ID: " + item.Id);
            var data = Crypto.Seal("item", item.Id, item.ToBytes());
            return Transport.WriteAsync(ItemFolder + "/" + item.Id + ".nz", data, expectedTag, ct);
        }

        public Task DeleteItemFileAsync(string file, CancellationToken ct = default) =>
            Transport.DeleteAsync(ItemFolder + "/" + file, ct);

        // ==================================================================
        //  Name der Datenbank
        // ==================================================================

        public const string NameFile = "name.nz";

        /// <summary>
        /// Wie die Datenbank heisst - damit ein anderes Geraet, das sich mit
        /// diesem Ordner verbindet, sie gleich richtig benennt. Verschluesselt:
        /// Auch ein Name kann etwas verraten.
        /// </summary>
        public Task WriteNameAsync(string name, CancellationToken ct = default) =>
            Transport.WriteAsync(NameFile, Crypto.Seal("meta", "name", System.Text.Encoding.UTF8.GetBytes(name)), null, ct);

        /// <summary>Null, wenn noch keiner hinterlegt ist (aeltere Ordner).</summary>
        public async Task<string?> ReadNameAsync(CancellationToken ct = default)
        {
            var data = await Transport.ReadAsync(NameFile, ct).ConfigureAwait(false);
            if (data == null) return null;
            try { return System.Text.Encoding.UTF8.GetString(Crypto.Unseal("meta", "name", data)); }
            catch (System.Security.Cryptography.CryptographicException) { return null; }
        }

        public const string IdFile = "id.nz";

        /// <summary>
        /// Kennung der Datenbank, die hier abgleicht. Der Name allein reicht
        /// nicht: Zwei verschiedene Datenbanken "Arbeit" auf zwei Geraeten
        /// landeten im selben Ordner und wuerden ohne Rueckfrage eins. Mit der
        /// Kennung sieht ein Geraet beim Verbinden, ob es dieselbe ist.
        /// Verschluesselt wie der Name. Null bei aelteren Ordnern.
        /// </summary>
        public async Task<string?> ReadIdAsync(CancellationToken ct = default)
        {
            var data = await Transport.ReadAsync(IdFile, ct).ConfigureAwait(false);
            if (data == null) return null;
            try { return System.Text.Encoding.UTF8.GetString(Crypto.Unseal("meta", "id", data)); }
            catch (System.Security.Cryptography.CryptographicException) { return null; }
        }

        public Task WriteIdAsync(string id, CancellationToken ct = default) =>
            Transport.WriteAsync(IdFile, Crypto.Seal("meta", "id", System.Text.Encoding.UTF8.GetBytes(id)), null, ct);

        /// <summary>
        /// Die Kennung des Ordners - hat er noch keine (aelterer Ordner, frisch
        /// eingerichtet), bekommt er <paramref name="candidate"/>. Beim
        /// gewoehnlichen Abgleich uebernimmt das Geraet, was zurueckkommt:
        /// Wer hier schon abgleicht, gehoert dazu.
        /// </summary>
        public async Task<string> EnsureIdAsync(string candidate, CancellationToken ct = default)
        {
            if (await ReadIdAsync(ct).ConfigureAwait(false) is { Length: > 0 } id) return id;
            await WriteIdAsync(candidate, ct).ConfigureAwait(false);
            return candidate;
        }

        // ==================================================================
        //  Blobs
        // ==================================================================

        public async Task<HashSet<string>> ListBlobsAsync(CancellationToken ct = default) =>
            (await Transport.ListAsync(BlobFolder, ct).ConfigureAwait(false))
                .Where(f => f.Name.EndsWith(".nzb", StringComparison.OrdinalIgnoreCase))
                .Select(f => f.Name[..^4])
                .Where(IsBlobName)
                .ToHashSet();

        public async Task PutBlobAsync(string name, byte[] content, CancellationToken ct = default)
        {
            if (!IsBlobName(name)) throw new ArgumentException("Kein gueltiger Blobname: " + name);
            await Transport.WriteAsync(BlobFolder + "/" + name + ".nzb", Crypto.Seal("blob", name, content), null, ct)
                           .ConfigureAwait(false);
        }

        /// <summary>Null, wenn nicht da oder nicht der erwartete Inhalt.</summary>
        public async Task<byte[]?> GetBlobAsync(string name, CancellationToken ct = default)
        {
            if (!IsBlobName(name)) return null;
            var data = await Transport.ReadAsync(BlobFolder + "/" + name + ".nzb", ct).ConfigureAwait(false);
            if (data == null) return null;
            var content = Crypto.Unseal("blob", name, data);
            // Der Name ist zugleich die Pruefsumme des Inhalts.
            return Crypto.BlobName(content) == name ? content : null;
        }

        // ==================================================================

        /// <summary>32 Hexzeichen - so sieht jede sync_id aus. Schuetzt auch
        /// davor, dass ein Name aus fremder Hand einen Pfad baut.</summary>
        public static bool IsId(string? id) => id is { Length: 32 } && id.All(Uri.IsHexDigit);

        private static bool IsBlobName(string name) => name.Length == 64 && name.All(Uri.IsHexDigit);
    }
}
