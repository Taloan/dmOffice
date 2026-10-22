using DmOffice.Core;
using DmOffice.Pim;

namespace DmOffice.Notes;

/// <summary>
/// Wo eine Notiz oder ein Ordner liegt, wie der angemeldete Benutzer darauf zugreift: in der eigenen Datenbank
/// oder - mit Freigabe - in der des Besitzers. Share/Tree gesetzt: ein geteilter Ordner samt Unterordnern.
/// </summary>
public sealed record NoteScope(UserSpace Space, NotesDb Db, Share? Share, bool CanWrite, HashSet<string>? Tree)
{
    public bool IsShared => Share != null;
    public string OwnerName => Space.User.DisplayName;

    public NoteScope Writable() => CanWrite ? this : throw new UnauthorizedAccessException("Nur lesbar – der Besitzer hat dir keine Bearbeitung erlaubt.");
    /// <summary>Nur dem Besitzer vorbehalten (endgueltig loeschen, archivieren, aus dem Ordner herausnehmen).</summary>
    public NoteScope OwnerOnly() => !IsShared ? this : throw new UnauthorizedAccessException("Das darf nur der Besitzer des Ordners.");

    public bool Covers(NoteRow n) => Tree == null || (n.Folder != null && Tree.Contains(n.Folder));

    /// <summary>Kennung der Notiz in der Oberflaeche: eigene = Id, geteilte = -(Freigabe * 2^32 + Id) - kollidiert nie mit eigenen.</summary>
    public long Wire(long id) => Share == null ? id : -((long)Share.Id * SharedNotes.Stride + id);
}

public static class SharedNotes
{
    public const long Stride = 1L << 32;

    public static NoteScope Own(HttpContext http) { var s = http.Space(); return new(s, NotesDb.Of(s), null, true, null); }

    /// <summary>Freigabe des angemeldeten Benutzers - null, wenn es sie nicht (mehr) gibt oder er nicht dazugehoert.</summary>
    private static NoteScope? Of(HttpContext http, Share share, string me)
    {
        if (share.Folder == null || !share.Members.Contains(me)) return null;
        UserSpace owner;
        try { owner = http.RequestServices.GetRequiredService<Spaces>().Get(share.Owner); } catch { return null; }
        var db = NotesDb.Of(owner);
        var tree = db.FolderTree(share.Folder);
        return tree.Count == 0 ? null : new NoteScope(owner, db, share, !share.ReadOnly.Contains(me), tree);
    }

    /// <summary>Alle mit mir geteilten Ordner, die es noch gibt.</summary>
    public static List<NoteScope> WithMe(HttpContext http)
    {
        var me = http.Space().User.Id;
        return http.RequestServices.GetRequiredService<ShareStore>().All().Where(s => s.Folder != null && s.Members.Contains(me))
            .Select(s => Of(http, s, me)).OfType<NoteScope>().ToList();
    }

    /// <summary>Zu einer Notiz-Kennung aus der Oberflaeche: wo sie liegt und welche Id sie dort hat. Ohne Freigabe: KeyNotFound.</summary>
    public static (NoteScope Scope, long Id) ForNote(HttpContext http, long wire)
    {
        if (wire >= 0) return (Own(http), wire);
        var share = http.RequestServices.GetRequiredService<ShareStore>().ById((int)(-wire / Stride));
        var scope = share == null ? null : Of(http, share, http.Space().User.Id);
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

    /// <summary>Bilder und Anhaenge: eigene, sonst die einer Notiz in einem geteilten Ordner. Null: nicht lesbar.</summary>
    public static byte[]? Blob(HttpContext http, string name)
    {
        if (NotesDb.Of(http.Space()).GetBlob(name) is { } mine) return mine;
        foreach (var s in WithMe(http))
            if (s.Db.BlobUsedIn(name, s.Tree!)) return s.Db.GetBlob(name);
        return null;
    }
}
