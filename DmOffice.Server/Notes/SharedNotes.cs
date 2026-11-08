using DmOffice.Core;
using DmOffice.Pim;

namespace DmOffice.Notes;

/// <summary>
/// Wo eine Notiz oder ein Ordner liegt, wie der angemeldete Benutzer darauf zugreift: in der eigenen Datenbank
/// oder - mit Freigabe - in der des Besitzers. Share/Tree gesetzt: ein geteilter Ordner samt Unterordnern.
/// </summary>
/// Vault: eine notizi-Datenbank im Tresor (Ordnername) - ohne Freigaben, nur bei offenem Tresor.
public sealed record NoteScope(UserSpace Space, NotesDb Db, Share? Share, bool CanWrite, HashSet<string>? Tree, bool ShowOnly = false, long? Only = null, string? Vault = null)
{
    /// <summary>Nur gezeigt (Version 2): ansehen in dmOffice, keine Anhaenge herunterladen - sonst 403.</summary>
    public NoteScope NotShowOnly() => ShowOnly ? throw new UnauthorizedAccessException("Nur gezeigt – herunterladen ist nicht erlaubt.") : this;

    public bool IsShared => Share != null;
    public string OwnerName => Space.User.DisplayName;

    public NoteScope Writable() => CanWrite ? this : throw new UnauthorizedAccessException("Nur lesbar – der Besitzer hat dir keine Bearbeitung erlaubt.");
    /// <summary>Nur dem Besitzer vorbehalten (endgueltig loeschen, archivieren, aus dem Ordner herausnehmen).</summary>
    public NoteScope OwnerOnly() => !IsShared ? this : throw new UnauthorizedAccessException("Das darf nur der Besitzer des Ordners.");

    /// <summary>Only: eine einzeln geteilte Notiz (Version 2) - der Zugriff gilt nur fuer sie.</summary>
    public bool Covers(NoteRow n) => Only != null ? n.Id == Only : Tree == null || (n.Folder != null && Tree.Contains(n.Folder));

    /// <summary>Kennung der Notiz in der Oberflaeche: eigene = Id, geteilte = -(Freigabe * 2^32 + Id) - kollidiert nie mit eigenen.</summary>
    public long Wire(long id) => Share == null ? id : -((long)Share.Id * SharedNotes.Stride + id);
}

public static class SharedNotes
{
    public const long Stride = 1L << 32;

    /// <summary>Die eigene Datenbank - unter /api/notes/vault/{vault} die notizi-Datenbank im Tresor (Tresor auf diesem Gerät offen, sonst 423).</summary>
    public static NoteScope Own(HttpContext http)
    {
        var s = http.Space();
        if (VaultOf(http) is not { } vault) return new(s, NotesDb.Of(s), null, true, null);
        if (!Tresor.IsOpen(http)) throw new TresorClosedException();
        return new(s, TresorNotes.Of(s, vault) ?? throw new KeyNotFoundException("Die Datenbank liegt nicht im Tresor."), null, true, null, Vault: vault);
    }

    public static string? VaultOf(HttpContext http) => http.GetRouteValue("vault") as string;

    /// <summary>Freigabe des angemeldeten Benutzers - null, wenn es sie nicht (mehr) gibt oder er nicht dazugehoert.</summary>
    private static NoteScope? Of(HttpContext http, Share share, string me)
    {
        if (share.Folder == null || !share.Members.Contains(me)) return null;
        UserSpace owner;
        try { owner = http.RequestServices.GetRequiredService<Spaces>().Get(share.Owner); } catch { return null; }
        var db = NotesDb.Of(owner);
        var tree = db.FolderTree(share.Folder);
        return tree.Count == 0 ? null : new NoteScope(owner, db, share, !share.ReadOnly.Contains(me), tree, share.IsShowOnly(me));
    }

    /// <summary>Alle mit mir geteilten Ordner, die es noch gibt.</summary>
    public static List<NoteScope> WithMe(HttpContext http)
    {
        // Im Tresor gibt es keine Freigaben
        if (VaultOf(http) != null) return [];
        var me = http.Space().User.Id;
        return http.RequestServices.GetRequiredService<ShareStore>().All().Where(s => s.Folder != null && s.Members.Contains(me))
            .Select(s => Of(http, s, me)).OfType<NoteScope>().ToList();
    }

    /// <summary>Zu einer Notiz-Kennung aus der Oberflaeche: wo sie liegt und welche Id sie dort hat. Ohne Freigabe: KeyNotFound.</summary>
    public static (NoteScope Scope, long Id) ForNote(HttpContext http, long wire)
    {
        if (wire >= 0) return (Own(http), wire);
        var shareId = (int)(-wire / Stride);
        NoteScope? scope;
        // Einzeln geteilte Notiz (Version 2): eigene Kennungen ab WireBase
        if (shareId >= NoteShareStore.WireBase)
        {
            var ns = http.RequestServices.GetRequiredService<NoteShareStore>().ById(shareId - NoteShareStore.WireBase);
            scope = ns == null ? null : SharedSingleNotes.Scope(http, ns, http.Space().User.Id);
        }
        else
        {
            var share = http.RequestServices.GetRequiredService<ShareStore>().ById(shareId);
            scope = share == null ? null : Of(http, share, http.Space().User.Id);
        }
        var id = -wire % Stride;
        // Archivierte und geloeschte Notizen sieht der Empfaenger nicht mehr
        if (scope?.Db.GetNote(id) is { Deleted: null, Archived: null } n && scope.Covers(n)) return (scope, id);
        throw new KeyNotFoundException("Notiz nicht gefunden.");
    }

    /// <summary>Wo ein Ordner liegt: null/leer und eigene Ordner = eigene Datenbank, sonst ein mit mir geteilter Ordner.</summary>
    public static NoteScope ForFolder(HttpContext http, string? folder)
    {
        var own = Own(http);
        if (string.IsNullOrEmpty(folder) || own.Db.GetFolder(folder) != null) return own;
        return WithMe(http).FirstOrDefault(s => s.Tree!.Contains(folder)) ?? throw new KeyNotFoundException("Ordner nicht gefunden.");
    }

    /// <summary>
    /// Eigene Notiz in einen geteilten Ordner (mit Bearbeitungsrecht): Kopie in der Datenbank des Besitzers - die Bilder
    /// heissen dort nach dessen Schluessel -, danach geht das Original aus der eigenen Datenbank (mit Grabstein fuer den
    /// eigenen Abgleich). Zurueck: die neue Kennung.
    /// </summary>
    public static async Task<long> MoveIntoShared(NoteScope own, long id, NoteScope target, string folder, CancellationToken ct)
    {
        target.Writable();
        var n = own.Db.GetNote(id) ?? throw new KeyNotFoundException();
        var renamed = new Dictionary<string, string>();
        foreach (var name in n.Images.Concat(n.Attachments.Select(a => a.Blob)).Distinct())
            if (own.Db.GetBlob(name) is { } data)
            {
                var now = await NotesSync.BlobNameAsync(target.Space, data, ct);
                target.Db.PutBlob(now, data);
                if (now != name) renamed[name] = now;
            }
        var newId = target.Db.ImportFrom(own.Db, id, folder, renamed);
        own.Db.DeleteNote(id);
        return target.Wire(newId);
    }

    public static NoteScope? TryForFolder(HttpContext http, string folder)
    {
        try { return ForFolder(http, folder); } catch (KeyNotFoundException) { return null; }
    }

    /// <summary>Bilder und Anhaenge: eigene, sonst die einer Notiz in einem geteilten Ordner (ShowOnly: nur gezeigt). Null: nicht lesbar.</summary>
    public static (byte[] Data, bool ShowOnly)? Blob(HttpContext http, string name)
    {
        if (Own(http).Db.GetBlob(name) is { } mine) return (mine, false);
        if (VaultOf(http) != null) return null;
        // Liegt es auch in einem Ordner, der mehr als zeigt, darf es heruntergeladen werden
        (byte[] Data, bool ShowOnly)? found = null;
        foreach (var s in WithMe(http))
            if (s.Db.BlobUsedIn(name, s.Tree!) && s.Db.GetBlob(name) is { } data)
            {
                if (!s.ShowOnly) return (data, false);
                found ??= (data, true);
            }
        // Bilder und Anhaenge einzeln geteilter Notizen (Version 2)
        foreach (var (s, note) in SharedSingleNotes.WithMe(http))
            if ((note.Images.Contains(name) || note.Attachments.Any(a => a.Blob == name)) && s.Db.GetBlob(name) is { } data)
            {
                if (!s.ShowOnly) return (data, false);
                found ??= (data, true);
            }
        return found;
    }
}
