using System.Collections.Generic;

namespace Notizi.Sync
{
    /// <summary>Eine Notiz oder ein Ordner auf diesem Geraet.</summary>
    public sealed record LocalEntry(string Id, string Kind, long Rev);

    /// <summary>
    /// Stand beim letzten erfolgreichen Abgleich eines Elements:
    /// Revision hier und Kennung der Datei dort. Weicht eines davon ab, hat
    /// sich seitdem etwas geaendert. Kind "gone": ein Grabstein.
    /// </summary>
    /// <param name="Base">Die zuletzt abgeglichene Version (SyncItem.ToBytes)
    /// - der Ausgangsstand fuer das dreiseitige Zusammenfuehren.</param>
    public sealed record SyncState(string Id, string Kind, long BaseRev, string RemoteTag, byte[]? Base = null);

    /// <summary>
    /// Die Seite des Geraets - auf Windows die Datenbank von notizi, auf
    /// Android die der App. Der Abgleich (<see cref="SyncEngine"/>) kennt nur
    /// diese Schnittstelle.
    /// </summary>
    public interface ISyncLocal
    {
        /// <summary>Alle Notizen und Ordner, auch Papierkorb und Archiv.</summary>
        IReadOnlyList<LocalEntry> Entries();

        /// <summary>Endgueltig Geloeschtes, noch nicht als Grabstein
        /// abgeglichen: ID und was es war.</summary>
        IReadOnlyList<(string Id, string Kind)> Tombstones();
        void ForgetTombstone(string id);

        IReadOnlyDictionary<string, SyncState> States();
        void SaveState(SyncState state);
        void RemoveState(string id);

        FolderItem ExportFolder(string id);
        NoteItem ExportNote(string id, IBlobStore blobs);

        /// <summary>Legt an oder uebernimmt. Fehlt der Elternordner, kommt
        /// der Ordner auf die oberste Ebene.</summary>
        void ApplyFolder(FolderItem item);

        /// <summary>Legt an oder uebernimmt. False, wenn ein Blob fehlt -
        /// dann bleibt alles, wie es war.</summary>
        bool ApplyNote(NoteItem item, IBlobStore blobs);

        bool FolderExists(string id);

        /// <summary>Loescht endgueltig, ohne einen Grabstein zu
        /// hinterlassen - der Grabstein kam ja von dort.</summary>
        void DeleteLocal(string id, string kind);

        long RevOf(string id, string kind);

        /// <summary>Gibt einem Element eine andere GUID - beim
        /// Zusammenfuehren mit seinem Gegenstueck dort.</summary>
        void Rekey(string oldId, string newId, string kind);
    }
}
