using System.Text.Json;
using DmOffice.Core;
using DmOffice.Dav;

namespace DmOffice.Pim;

/// <summary>
/// Besondere Aufgabenlisten: Einkauf (Menge, Einheit, Abteilung, Vorschlaege aus frueheren Eintraegen)
/// und Checkliste (abhaken und fuer das naechste Mal zuruecksetzen). Die Eintraege bleiben normale
/// VTODO - Menge, Einheit, Artikel und Abteilung stehen zusaetzlich in X-DM-Eigenschaften, der Titel
/// ("2 kg Kartoffeln") bleibt fuer andere Programme lesbar.
/// </summary>
public static class Lists
{
    public const string Tasks = "tasks", Shop = "shop", Check = "check";

    public sealed class Meta
    {
        public string Type { get; set; } = Tasks;
        /// <summary>Abteilungen in der Reihenfolge des Laufwegs.</summary>
        public List<string> Sections { get; set; } = new();
        /// <summary>Abgehakte Eintraege: "place" (bleiben stehen), "bottom" (rutschen nach unten), "hide" (ausgeblendet). Null: Vorgabe des Typs.</summary>
        public string? Done { get; set; }
        /// <summary>Was schon einmal auf der Liste stand - fuer Vorschlaege beim Tippen.</summary>
        public List<Known> History { get; set; } = new();
    }

    public sealed class Known
    {
        public string Name { get; set; } = "";
        public string? Unit { get; set; }
        public string? Section { get; set; }
        public int Count { get; set; }
        public DateTime Last { get; set; }
        /// <summary>Kennung des Fotos (welche Marke, welche Packung) - die Bilddaten liegen eigens, siehe <see cref="PhotoKey"/>.</summary>
        public string? Photo { get; set; }
    }

    public static readonly string[] DefaultShopSections =
    {
        "Obst & Gemüse", "Brot & Backwaren", "Kühlregal", "Fleisch & Wurst", "Käse", "Tiefkühl",
        "Konserven & Vorrat", "Getränke", "Süßes & Snacks", "Drogerie", "Haushalt", "Sonstiges",
    };

    private const int MaxHistory = 3000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string Key(long id) => "pim.list." + id;
    private static string PhotoKey(string photo) => "pim.listphoto." + photo;
    /// <summary>Fotos werden auf diese Kantenlaenge verkleinert - reicht, um im Laden die Packung zu erkennen.</summary>
    public const int PhotoSize = 800;

    public static Meta Get(UserDb db, long id)
    {
        Meta? m = null;
        try { if (db.Get(Key(id)) is { } s) m = JsonSerializer.Deserialize<Meta>(s, Json); } catch (JsonException) { }
        m ??= new Meta();
        if (m.Type == Shop && m.Sections.Count == 0) m.Sections = DefaultShopSections.ToList();
        return m;
    }

    public static void Save(UserDb db, long id, Meta m) => db.Set(Key(id), JsonSerializer.Serialize(m, Json));

    public static void Remove(UserDb db, long id) => db.Set(Key(id), null);

    public static string TypeOf(UserDb db, long id) => Get(db, id).Type;

    /// <summary>Typ und Abteilungen aendern; unbekannte Typen werden zu "tasks".</summary>
    public static Meta Update(UserDb db, long id, string? type, List<string>? sections, string? done = null)
    {
        var m = Get(db, id);
        if (done != null) m.Done = done is "place" or "bottom" or "hide" ? done : null;
        if (type != null) m.Type = type is Shop or Check ? type : Tasks;
        if (sections != null)
            m.Sections = sections.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(60).ToList();
        if (m.Type == Shop && m.Sections.Count == 0) m.Sections = DefaultShopSections.ToList();
        Save(db, id, m);
        return m;
    }

    /// <summary>Einen Eintrag merken: Einheit und Abteilung gelten beim naechsten Mal als Vorschlag.</summary>
    public static void Learn(UserDb db, long id, string item, string? unit, string? section)
    {
        item = item.Trim();
        if (item.Length == 0 || item.Length > 200) return;
        var m = Get(db, id);
        if (m.Type == Tasks) return;
        var k = m.History.FirstOrDefault(h => string.Equals(h.Name, item, StringComparison.OrdinalIgnoreCase));
        if (k == null) { k = new Known { Name = item }; m.History.Add(k); }
        k.Count++;
        k.Last = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(unit)) k.Unit = unit.Trim();
        if (!string.IsNullOrWhiteSpace(section))
        {
            k.Section = section.Trim();
            if (!m.Sections.Contains(k.Section, StringComparer.OrdinalIgnoreCase)) m.Sections.Add(k.Section);
        }
        if (m.History.Count > MaxHistory)
        {
            var keep = m.History.OrderByDescending(h => h.Last).Take(MaxHistory).ToList();
            foreach (var old in m.History.Except(keep)) DropPhoto(db, old);
            m.History = keep;
        }
        Save(db, id, m);
    }

    /// <summary>
    /// Aus einer Aufgabe, die ueber CalDAV kam: neuer Artikel oder geaenderter Artikel/Abteilung - wie CalendarModel.SaveTask
    /// (nicht jedes Abhaken). Fehlerhafte Daten werden still uebergangen, die Aufgabe ist da schon gespeichert.
    /// </summary>
    public static void LearnFromIcs(UserDb db, long id, string? before, string after)
    {
        static (string? Item, string? Unit, string? Section) Read(string? ics)
        {
            if (ics == null) return (null, null, null);
            try
            {
                var t = Ics.Parse(ics).First(r => r.Name == "VCALENDAR").Of("VTODO").FirstOrDefault();
                return t == null ? (null, null, null) : (t.Text("X-DM-ITEM"), t.Text("X-DM-UNIT"), t.Text("X-DM-SECTION"));
            }
            catch { return (null, null, null); }
        }
        var now = Read(after);
        if (string.IsNullOrWhiteSpace(now.Item)) return;
        var was = Read(before);
        if (string.Equals(was.Item, now.Item, StringComparison.OrdinalIgnoreCase) && was.Section == now.Section) return;
        Learn(db, id, now.Item, now.Unit, now.Section);
    }

    /// <summary>Einen Vorschlag vergessen (Tippfehler).</summary>
    public static void Forget(UserDb db, long id, string item)
    {
        var m = Get(db, id);
        var gone = m.History.Where(h => string.Equals(h.Name, item.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        if (gone.Count == 0) return;
        foreach (var h in gone) { DropPhoto(db, h); m.History.Remove(h); }
        Save(db, id, m);
    }

    /// <summary>
    /// Foto zu einem Artikel (auch einem, der noch nie auf der Liste stand): verkleinert als WebP in der Datenbank,
    /// damit es mit den Listen gesichert wird. Null: kein Bild. Ersetzt ein vorhandenes Foto.
    /// </summary>
    public static string? SetPhoto(UserDb db, long id, string item, byte[] image)
    {
        item = item.Trim();
        if (item.Length == 0 || item.Length > 200) throw new ArgumentException("Ungültiger Artikel.");
        if (Thumbs.FromBytes(image, PhotoSize) is not { } webp) return null;
        var m = Get(db, id);
        var k = m.History.FirstOrDefault(h => string.Equals(h.Name, item, StringComparison.OrdinalIgnoreCase));
        if (k == null) { k = new Known { Name = item, Last = DateTime.UtcNow }; m.History.Add(k); }
        DropPhoto(db, k);
        k.Photo = Guid.NewGuid().ToString("N")[..16];
        db.Set(PhotoKey(k.Photo), Convert.ToBase64String(webp));
        Save(db, id, m);
        return k.Photo;
    }

    public static void RemovePhoto(UserDb db, long id, string item)
    {
        var m = Get(db, id);
        var k = m.History.FirstOrDefault(h => string.Equals(h.Name, item.Trim(), StringComparison.OrdinalIgnoreCase));
        if (k?.Photo == null) return;
        DropPhoto(db, k);
        Save(db, id, m);
    }

    /// <summary>Bilddaten, wenn das Foto zu dieser Liste gehoert (sonst null - fremde Kennungen fuehren nirgendwohin).</summary>
    public static byte[]? Photo(UserDb db, long id, string photo)
    {
        if (!Get(db, id).History.Any(h => h.Photo == photo)) return null;
        return db.Get(PhotoKey(photo)) is { } b64 ? Convert.FromBase64String(b64) : null;
    }

    private static void DropPhoto(UserDb db, Known k)
    {
        if (k.Photo == null) return;
        db.Set(PhotoKey(k.Photo), null);
        k.Photo = null;
    }
}
