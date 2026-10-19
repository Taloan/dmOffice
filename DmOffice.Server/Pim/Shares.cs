using System.Text.Json;
using DmOffice.Core;

namespace DmOffice.Pim;

/// <summary>Eine Aufgabenliste, die ihr Besitzer mit anderen Benutzern dieses Servers teilt.</summary>
public sealed class Share
{
    public int Id { get; set; }
    public string Owner { get; set; } = "";
    /// <summary>Id der Liste in der Datenbank des Besitzers.</summary>
    public long Collection { get; set; }
    public List<string> Members { get; set; } = new();
}

/// <summary>
/// Geteilte Listen - data/shares.json. Die Liste bleibt in der Datenbank des Besitzers; wer sie
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
        lock (_lock) return _shares.FirstOrDefault(s => s.Owner == owner && s.Collection == collection) is { } s ? Copy(s) : null;
    }

    /// <summary>Mitglieder setzen; leer hebt die Freigabe auf.</summary>
    public void Set(string owner, long collection, IEnumerable<string> members)
    {
        var list = members.Where(m => m != owner).Distinct().ToList();
        lock (_lock)
        {
            var s = _shares.FirstOrDefault(x => x.Owner == owner && x.Collection == collection);
            if (list.Count == 0) { if (s != null) _shares.Remove(s); }
            else if (s == null) _shares.Add(new Share { Id = _shares.Count == 0 ? 1 : _shares.Max(x => x.Id) + 1, Owner = owner, Collection = collection, Members = list });
            else s.Members = list;
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
            if (s.Members.Count == 0) _shares.Remove(s);
            Save();
        }
    }

    /// <summary>Liste geloescht oder Benutzer weg: Freigaben aufraeumen.</summary>
    public void Forget(string user, long? collection = null)
    {
        lock (_lock)
        {
            int n = _shares.RemoveAll(s => s.Owner == user && (collection == null || s.Collection == collection));
            if (collection == null) foreach (var s in _shares) n += s.Members.Remove(user) ? 1 : 0;
            n += _shares.RemoveAll(s => s.Members.Count == 0);
            if (n > 0) Save();
        }
    }

    private void Save()
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_shares, Json));
        File.Move(tmp, _path, overwrite: true);
    }

    private static Share Copy(Share s) => new() { Id = s.Id, Owner = s.Owner, Collection = s.Collection, Members = s.Members.ToList() };
}

/// <summary>Eine Liste, wie sie der angemeldete Benutzer sieht: eigene oder geteilte.</summary>
public sealed record ListRef(UserSpace Space, long Collection, long PublicId, Share? Share);

public static class SharedLists
{
    /// <summary>
    /// Id aus der Oberflaeche aufloesen: positiv = eigene Liste, negativ = mit mir geteilt (-Share.Id).
    /// Fremde Listen ohne Freigabe: KeyNotFound - wie eine Liste, die es nicht gibt.
    /// </summary>
    public static ListRef Resolve(HttpContext http, long id)
    {
        var me = http.Space();
        if (id >= 0) return new ListRef(me, id, id, null);
        var share = http.RequestServices.GetRequiredService<ShareStore>().ById((int)-id);
        if (share == null || !share.Members.Contains(me.User.Id)) throw new KeyNotFoundException("Liste nicht gefunden.");
        var owner = http.RequestServices.GetRequiredService<Spaces>().Get(share.Owner);
        return new ListRef(owner, share.Collection, id, share);
    }

    /// <summary>Alle mit mir geteilten Listen, die es noch gibt.</summary>
    public static IEnumerable<(Share Share, UserSpace Owner, Collection Coll)> WithMe(HttpContext http)
    {
        var me = http.Space().User.Id;
        var spaces = http.RequestServices.GetRequiredService<Spaces>();
        foreach (var s in http.RequestServices.GetRequiredService<ShareStore>().All().Where(s => s.Members.Contains(me)))
        {
            UserSpace owner;
            try { owner = spaces.Get(s.Owner); } catch { continue; }
            if (PimStore.Find(owner.Db, s.Collection) is { } c) yield return (s, owner, c);
        }
    }
}
