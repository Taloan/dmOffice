using System.Text.Json;
using DmOffice.Core;

namespace DmOffice.Pim;

/// <summary>Eine Aufgabenliste, die ihr Besitzer mit anderen Benutzern dieses Servers teilt.</summary>
public sealed class Share
{
    public int Id { get; set; }
    public string Owner { get; set; } = "";
    /// <summary>Id der Liste in der Datenbank des Besitzers (bei Notizordnern 0).</summary>
    public long Collection { get; set; }
    /// <summary>Notizordner (sync_id in der Datenbank des Besitzers) samt Unterordnern; sonst null = Kalender/Liste.</summary>
    public string? Folder { get; set; }
    public List<string> Members { get; set; } = new();
    /// <summary>Teilmenge von Members, die nur lesen darf. Fehlt in aelteren Dateien: alle duerfen bearbeiten.</summary>
    public List<string> ReadOnly { get; set; } = new();
}

/// <summary>
/// Geteilte Kalender und Listen - data/shares.json. Die Liste bleibt in der Datenbank des Besitzers; wer sie
/// geteilt bekommt, arbeitet ueber den Server darin (die Schluessel hat der Server ohnehin).
/// Fuer Mitglieder hat eine geteilte Liste die Id -(Share.Id), damit sie nicht mit eigenen kollidiert.
/// </summary>
public sealed class ShareStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _path;
    private readonly object _lock = new();
    private List<Share> _shares;

    public ShareStore(DataPaths paths)
    {
        _path = Path.Combine(paths.Root, "shares.json");
        _shares = File.Exists(_path) ? JsonSerializer.Deserialize<List<Share>>(File.ReadAllText(_path), Json) ?? new() : new();
    }

    public List<Share> All() { lock (_lock) return _shares.Select(Copy).ToList(); }

    public Share? ById(int id) { lock (_lock) return _shares.FirstOrDefault(s => s.Id == id) is { } s ? Copy(s) : null; }

    public Share? Of(string owner, long collection)
    {
        lock (_lock) return _shares.FirstOrDefault(s => s.Folder == null && s.Owner == owner && s.Collection == collection) is { } s ? Copy(s) : null;
    }

    /// <summary>Die Freigabe eines Notizordners des Besitzers.</summary>
    public Share? OfFolder(string owner, string folder)
    {
        lock (_lock) return _shares.FirstOrDefault(s => s.Folder == folder && s.Owner == owner) is { } s ? Copy(s) : null;
    }

    /// <summary>Mitglieder setzen; leer hebt die Freigabe auf. readOnly: wer davon nur lesen darf.</summary>
    public void Set(string owner, long collection, IEnumerable<string> members, IEnumerable<string>? readOnly = null) =>
        SetCore(owner, collection, null, members, readOnly);

    /// <summary>Wie <see cref="Set"/>, fuer einen Notizordner.</summary>
    public void SetFolder(string owner, string folder, IEnumerable<string> members, IEnumerable<string>? readOnly = null) =>
        SetCore(owner, 0, folder, members, readOnly);

    private void SetCore(string owner, long collection, string? folder, IEnumerable<string> members, IEnumerable<string>? readOnly)
    {
        var list = members.Where(m => m != owner).Distinct().ToList();
        var ro = (readOnly ?? Array.Empty<string>()).Where(list.Contains).Distinct().ToList();
        lock (_lock)
        {
            var s = _shares.FirstOrDefault(x => x.Owner == owner && x.Collection == collection && x.Folder == folder);
            if (list.Count == 0) { if (s != null) _shares.Remove(s); }
            else if (s == null) _shares.Add(new Share { Id = _shares.Count == 0 ? 1 : _shares.Max(x => x.Id) + 1, Owner = owner, Collection = collection, Folder = folder, Members = list, ReadOnly = ro });
            else { s.Members = list; s.ReadOnly = ro; }
            Save();
        }
    }

    /// <summary>Ein Mitglied steigt aus.</summary>
    public void Leave(int id, string member)
    {
        lock (_lock)
        {
            var s = _shares.FirstOrDefault(x => x.Id == id);
            if (s == null || !s.Members.Remove(member)) return;
            s.ReadOnly.Remove(member);
            if (s.Members.Count == 0) _shares.Remove(s);
            Save();
        }
    }

    /// <summary>Liste geloescht oder Benutzer weg: Freigaben aufraeumen.</summary>
    public void Forget(string user, long? collection = null)
    {
        lock (_lock)
        {
            int n = _shares.RemoveAll(s => s.Owner == user && (collection == null || (s.Folder == null && s.Collection == collection)));
            if (collection == null) foreach (var s in _shares) { n += s.Members.Remove(user) ? 1 : 0; s.ReadOnly.Remove(user); }
            n += _shares.RemoveAll(s => s.Members.Count == 0);
            if (n > 0) Save();
        }
    }

    /// <summary>Notizordner geloescht: seine Freigabe weg.</summary>
    public void ForgetFolder(string owner, string folder)
    {
        lock (_lock) if (_shares.RemoveAll(s => s.Owner == owner && s.Folder == folder) > 0) Save();
    }

    private void Save()
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_shares, Json));
        File.Move(tmp, _path, overwrite: true);
    }

    private static Share Copy(Share s) => new() { Id = s.Id, Owner = s.Owner, Collection = s.Collection, Folder = s.Folder, Members = s.Members.ToList(), ReadOnly = s.ReadOnly.ToList() };
}

/// <summary>Ein Kalender oder eine Liste, wie der angemeldete Benutzer sie sieht: eigene oder geteilte. CanWrite: eigene oder mit Bearbeitungsrecht geteilt.</summary>
public sealed record ListRef(UserSpace Space, long Collection, long PublicId, Share? Share, bool CanWrite = true)
{
    /// <summary>Aenderungen nur mit Bearbeitungsrecht - sonst 403.</summary>
    public ListRef Writable() => CanWrite ? this : throw new UnauthorizedAccessException("Nur lesbar – der Besitzer hat dir keine Bearbeitung erlaubt.");
}

public static class SharedLists
{
    /// <summary>
    /// Id aus der Oberflaeche aufloesen: positiv = eigene Sammlung, negativ = mit mir geteilt (-Share.Id).
    /// Fremde Sammlungen ohne Freigabe: KeyNotFound - wie eine, die es nicht gibt.
    /// </summary>
    public static ListRef Resolve(HttpContext http, long id)
    {
        var me = http.Space();
        if (id >= 0) return new ListRef(me, id, id, null);
        var share = http.RequestServices.GetRequiredService<ShareStore>().ById((int)-id);
        if (share == null || share.Folder != null || !share.Members.Contains(me.User.Id)) throw new KeyNotFoundException("Nicht gefunden.");
        var owner = http.RequestServices.GetRequiredService<Spaces>().Get(share.Owner);
        if (PimStore.Find(owner.Db, share.Collection) == null) throw new KeyNotFoundException("Nicht gefunden.");
        return new ListRef(owner, share.Collection, id, share, !share.ReadOnly.Contains(me.User.Id));
    }

    /// <summary>Alle mit mir geteilten Sammlungen, die es noch gibt.</summary>
    public static IEnumerable<(Share Share, UserSpace Owner, Collection Coll)> WithMe(HttpContext http) =>
        For(http.RequestServices.GetRequiredService<ShareStore>(), http.RequestServices.GetRequiredService<Spaces>(), http.Space().User.Id);

    public static IEnumerable<(Share Share, UserSpace Owner, Collection Coll)> For(ShareStore shares, Spaces spaces, string userId)
    {
        foreach (var s in shares.All().Where(s => s.Folder == null && s.Members.Contains(userId)))
        {
            UserSpace owner;
            try { owner = spaces.Get(s.Owner); } catch { continue; }
            if (PimStore.Find(owner.Db, s.Collection) is { } c) yield return (s, owner, c);
        }
    }

    /// <summary>"Familie (von Anna)" - so heisst ein geteilter Kalender bei den Empfaengern.</summary>
    public static string DisplayName(Collection c, UserSpace owner) => $"{c.Name} (von {owner.User.DisplayName})";
}
