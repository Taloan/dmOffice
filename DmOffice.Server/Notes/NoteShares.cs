using System.Text.Json;
using DmOffice.Core;
using DmOffice.Pim;

namespace DmOffice.Notes;

/// <summary>
/// Eine einzelne Notiz, die ihr Besitzer mit anderen Benutzern dieses Servers teilt (Version 2). Nur in dmOffice:
/// Der Abgleich mit notizi kennt sie nicht, sie bleibt in der Datenbank des Besitzers.
/// </summary>
public sealed class NoteShare
{
    public int Id { get; set; }
    public string Owner { get; set; } = "";
    /// <summary>sync_id der Notiz beim Besitzer (bleibt beim Verschieben in andere Ordner gleich).</summary>
    public string Note { get; set; } = "";
    public List<string> Members { get; set; } = new();
    /// <summary>Teilmenge von Members, die nur lesen darf (Senden).</summary>
    public List<string> ReadOnly { get; set; } = new();
    /// <summary>Teilmenge von ReadOnly, die nur zeigen darf.</summary>
    public List<string> ShowOnly { get; set; } = new();

    public string LevelOf(string member) => ShowOnly.Contains(member) ? "show" : ReadOnly.Contains(member) ? "send" : "share";
}

/// <summary>Einzeln geteilte Notizen - data/noteshares.json (getrennt von shares.json, damit 1.x davon nichts merkt).</summary>
public sealed class NoteShareStore
{
    /// <summary>
    /// Kennungen in der Oberflaeche: Ordner-Freigaben nutzen die Share-Id, einzelne Notizen Basis + Id -
    /// so bleibt die Notiz-Kennung -(Freigabe * 2^32 + Id) eindeutig und in JavaScript genau (unter 2^53).
    /// </summary>
    public const int WireBase = 1 << 19;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _path;
    private readonly object _lock = new();
    private List<NoteShare> _shares;

    public NoteShareStore(DataPaths paths)
    {
        _path = Path.Combine(paths.Root, "noteshares.json");
        _shares = File.Exists(_path) ? JsonSerializer.Deserialize<List<NoteShare>>(File.ReadAllText(_path), Json) ?? new() : new();
    }

    public List<NoteShare> All() { lock (_lock) return _shares.Select(Copy).ToList(); }

    public NoteShare? ById(int id) { lock (_lock) return _shares.FirstOrDefault(s => s.Id == id) is { } s ? Copy(s) : null; }

    public NoteShare? Of(string owner, string note) { lock (_lock) return _shares.FirstOrDefault(s => s.Owner == owner && s.Note == note) is { } s ? Copy(s) : null; }

    /// <summary>Mitglieder setzen; leer hebt die Freigabe auf. Zeigen ist eine Teilmenge von nur lesen.</summary>
    public void Set(string owner, string note, IEnumerable<string> members, IEnumerable<string>? readOnly = null, IEnumerable<string>? showOnly = null)
    {
        var list = members.Where(m => m != owner).Distinct().ToList();
        var show = (showOnly ?? Array.Empty<string>()).Where(list.Contains).Distinct().ToList();
        var ro = (readOnly ?? Array.Empty<string>()).Concat(show).Where(list.Contains).Distinct().ToList();
        lock (_lock)
        {
            var s = _shares.FirstOrDefault(x => x.Owner == owner && x.Note == note);
            if (list.Count == 0) { if (s != null) _shares.Remove(s); }
            else if (s == null) _shares.Add(new NoteShare { Id = _shares.Count == 0 ? 1 : _shares.Max(x => x.Id) + 1, Owner = owner, Note = note, Members = list, ReadOnly = ro, ShowOnly = show });
            else { s.Members = list; s.ReadOnly = ro; s.ShowOnly = show; }
            Save();
        }
    }

    /// <summary>Ein Mitglied will die Notiz nicht mehr sehen.</summary>
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

    /// <summary>Benutzer weg: seine Freigaben und seine Mitgliedschaften aufraeumen.</summary>
    public void Forget(string user)
    {
        lock (_lock)
        {
            int n = _shares.RemoveAll(s => s.Owner == user);
            foreach (var s in _shares) { n += s.Members.Remove(user) ? 1 : 0; s.ReadOnly.Remove(user); s.ShowOnly.Remove(user); }
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

    private static NoteShare Copy(NoteShare s) => new() { Id = s.Id, Owner = s.Owner, Note = s.Note, Members = s.Members.ToList(), ReadOnly = s.ReadOnly.ToList(), ShowOnly = s.ShowOnly.ToList() };
}

public static class SharedSingleNotes
{
    /// <summary>
    /// Zugriff auf eine einzeln geteilte Notiz wie auf einen geteilten Ordner: Die Freigabe erscheint als Share mit
    /// WireBase + Id (damit Wire() die Kennung bildet), Only begrenzt auf genau diese Notiz, Tree ist leer -
    /// in einen anderen Ordner verschieben geht so nicht.
    /// </summary>
    public static NoteScope? Scope(HttpContext http, NoteShare ns, string me)
    {
        if (!ns.Members.Contains(me)) return null;
        UserSpace owner;
        try { owner = http.RequestServices.GetRequiredService<Spaces>().Get(ns.Owner); } catch { return null; }
        var db = NotesDb.Of(owner);
        if (db.BySyncId(ns.Note) is not { Archived: null } n) return null;
        var share = new Share { Id = NoteShareStore.WireBase + ns.Id, Owner = ns.Owner, Members = ns.Members.ToList(), ReadOnly = ns.ReadOnly.ToList(), ShowOnly = ns.ShowOnly.ToList() };
        return new NoteScope(owner, db, share, !ns.ReadOnly.Contains(me), new HashSet<string>(), ns.ShowOnly.Contains(me), n.Id);
    }

    /// <summary>Alle mit mir einzeln geteilten Notizen, die es noch gibt (nicht archiviert, nicht im Papierkorb).</summary>
    public static List<(NoteScope Scope, NoteRow Note)> WithMe(HttpContext http)
    {
        var me = http.Space().User.Id;
        var list = new List<(NoteScope, NoteRow)>();
        foreach (var ns in http.RequestServices.GetRequiredService<NoteShareStore>().All().Where(s => s.Members.Contains(me)))
            if (Scope(http, ns, me) is { } sc && sc.Db.GetNote(sc.Only!.Value) is { } n) list.Add((sc, n));
        return list;
    }
}
