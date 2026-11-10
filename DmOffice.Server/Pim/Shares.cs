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
    /// <summary>Teilmenge von ReadOnly, die nur zeigen darf (Version 2): ansehen in dmOffice, kein Export, kein Abgleich auf Geraete.</summary>
    public List<string> ShowOnly { get; set; } = new();

    /// <summary>Stufe eines Mitglieds: show (Zeigen), send (Senden = nur lesen), share (Teilen = bearbeiten).</summary>
    public string LevelOf(string member) => ShowOnly.Contains(member) ? "show" : ReadOnly.Contains(member) ? "send" : "share";

    public bool IsShowOnly(string member) => ShowOnly.Contains(member);
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
    public void Set(string owner, long collection, IEnumerable<string> members, IEnumerable<string>? readOnly = null, IEnumerable<string>? showOnly = null) =>
        SetCore(owner, collection, null, members, readOnly, showOnly);

    /// <summary>Wie <see cref="Set"/>, fuer einen Notizordner.</summary>
    public void SetFolder(string owner, string folder, IEnumerable<string> members, IEnumerable<string>? readOnly = null, IEnumerable<string>? showOnly = null) =>
        SetCore(owner, 0, folder, members, readOnly, showOnly);

    private void SetCore(string owner, long collection, string? folder, IEnumerable<string> members, IEnumerable<string>? readOnly, IEnumerable<string>? showOnly)
    {
        var list = members.Where(m => m != owner).Distinct().ToList();
        // Zeigen ist eine Teilmenge von nur lesen - so greifen alle Schreibsperren weiter
        var show = (showOnly ?? Array.Empty<string>()).Where(list.Contains).Distinct().ToList();
        var ro = (readOnly ?? Array.Empty<string>()).Concat(show).Where(list.Contains).Distinct().ToList();
        lock (_lock)
        {
            var s = _shares.FirstOrDefault(x => x.Owner == owner && x.Collection == collection && x.Folder == folder);
            if (list.Count == 0) { if (s != null) _shares.Remove(s); }
            else if (s == null) _shares.Add(new Share { Id = _shares.Count == 0 ? 1 : _shares.Max(x => x.Id) + 1, Owner = owner, Collection = collection, Folder = folder, Members = list, ReadOnly = ro, ShowOnly = show });
            else { s.Members = list; s.ReadOnly = ro; s.ShowOnly = show; }
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
            s.ShowOnly.Remove(member);
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
            if (collection == null) foreach (var s in _shares) { n += s.Members.Remove(user) ? 1 : 0; s.ReadOnly.Remove(user); s.ShowOnly.Remove(user); }
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

    private static Share Copy(Share s) => new() { Id = s.Id, Owner = s.Owner, Collection = s.Collection, Folder = s.Folder, Members = s.Members.ToList(), ReadOnly = s.ReadOnly.ToList(), ShowOnly = s.ShowOnly.ToList() };
}

/// <summary>Ein Kalender oder eine Liste, wie der angemeldete Benutzer sie sieht: eigene oder geteilte. CanWrite: eigene oder mit Bearbeitungsrecht geteilt.</summary>
public sealed record ListRef(UserSpace Space, long Collection, long PublicId, Share? Share, bool CanWrite = true, bool ShowOnly = false)
{
    /// <summary>Nur gezeigt (Version 2): kein Export, keine Kopie - sonst 403.</summary>
    public ListRef NotShowOnly() => ShowOnly ? throw new UnauthorizedAccessException("Nur gezeigt – exportieren ist nicht erlaubt.") : this;

    /// <summary>Aenderungen nur mit Bearbeitungsrecht - sonst 403.</summary>
    public ListRef Writable() => CanWrite ? this : throw new UnauthorizedAccessException("Nur lesbar – der Besitzer hat dir keine Bearbeitung erlaubt.");
}

public static class SharedLists
{
    /// <summary>
    /// Id aus der Oberflaeche aufloesen: positiv = eigene Sammlung, negativ = mit mir geteilt (-Share.Id).
    /// Fremde Sammlungen ohne Freigabe: KeyNotFound - wie eine, die es nicht gibt. Reservierte Sammlungen
    /// (<see cref="PimStore.IsReserved"/>) ebenso.
    /// </summary>
    public static ListRef Resolve(HttpContext http, long id)
    {
        var me = http.Space();
        if (id >= 0) return new ListRef(me, id, id, null);
        var share = http.RequestServices.GetRequiredService<ShareStore>().ById((int)-id);
        if (share == null || share.Folder != null || !share.Members.Contains(me.User.Id)) throw new KeyNotFoundException("Nicht gefunden.");
        var owner = http.RequestServices.GetRequiredService<Spaces>().Get(share.Owner);
        if (PimStore.Find(owner.Db, share.Collection) is not { } coll || PimStore.IsReserved(coll)) throw new KeyNotFoundException("Nicht gefunden.");
        return new ListRef(owner, share.Collection, id, share, !share.ReadOnly.Contains(me.User.Id), share.IsShowOnly(me.User.Id));
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

    /// <summary>Wem eine Liste gehoert und mit wem sie geteilt ist - an die kann man Aufgaben zuweisen (Version 2).</summary>
    public static List<UserRecord> People(IServiceProvider services, ListRef r)
    {
        var users = services.GetRequiredService<UserStore>();
        var share = r.Share ?? services.GetRequiredService<ShareStore>().All().FirstOrDefault(s => s.Folder == null && s.Owner == r.Space.User.Id && s.Collection == r.Collection);
        var ids = new List<string> { r.Space.User.Id };
        if (share != null) ids.AddRange(share.Members);
        return ids.Distinct().Select(users.ById).OfType<UserRecord>().ToList();
    }

    /// <summary>Liste aus der CalDAV-Adresse der App: eigene Sammlung (uri) oder geteilte ("shared-&lt;id&gt;").</summary>
    public static ListRef? ByUri(HttpContext http, string uri)
    {
        var me = http.Space();
        if (uri.StartsWith("shared-", StringComparison.Ordinal) && int.TryParse(uri[7..], out var id))
        {
            try { return Resolve(http, -id); } catch (KeyNotFoundException) { return null; }
        }
        return PimStore.Find(me.Db, PimStore.Calendar, uri) is { } c ? new ListRef(me, c.Id, c.Id, null) : null;
    }

    /// <summary>"Familie (von Anna)" - so heisst ein geteilter Kalender bei den Empfaengern.</summary>
    public static string DisplayName(Collection c, UserSpace owner) => $"{c.Name} (von {owner.User.DisplayName})";
}
