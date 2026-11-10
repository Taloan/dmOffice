using DmOffice.Core;

namespace DmOffice.Dashboard;

/// <summary>Ein Widget der Übersicht: Art, Größe (1 klein, 2 mittel = zwei Spalten, 3 groß = zwei Spalten, zwei Reihen), eigener Titel, Einstellungen.</summary>
public sealed class Widget
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Type { get; set; } = "";
    public int Size { get; set; } = 1;
    /// <summary>Feste Stelle im Raster der breiten Ansicht (Spalte, Reihe ab 0); ohne: die erste freie Stelle. Das Handy ordnet der Reihe nach.</summary>
    public int? X { get; set; }
    public int? Y { get; set; }
    public string? Title { get; set; }
    /// <summary>Einstellungen je Art (Kalender-Ids, Zeitraum, Konten …) - als lose Werte, damit Web und App dieselben speichern.</summary>
    public Dictionary<string, System.Text.Json.JsonElement>? Opts { get; set; }
    /// <summary>Stapel: mehrere Widgets an einer Stelle zum Durchwischen; smart = je nach Tageszeit das passende vorn.</summary>
    public List<Widget>? Stack { get; set; }
    public bool Smart { get; set; }
}

/// <summary>Hintergrundbild der Übersicht: keins, ein Foto aus der Galerie (Pfad), ein hochgeladenes, oder jeden Tag eins aus einem Album.</summary>
public sealed class Background
{
    public string Kind { get; set; } = "none";   // none | gallery | upload | album
    public string? Path { get; set; }
    public long? Album { get; set; }
    /// <summary>Abdunkeln in Prozent (0–80).</summary>
    public int Dim { get; set; } = 25;
}

/// <summary>Die Zusammenstellung eines Benutzers - liegt auf dem Server und gilt auf allen Geräten.</summary>
public sealed class Layout
{
    public List<Widget> Widgets { get; set; } = new();
    /// <summary>Spaltenzahl, zu der die Stellen der Widgets gehören (breite Ansicht) - damit eine Scrollleiste oder etwas Breite nichts verschiebt.</summary>
    public int? Cols { get; set; }
    public Background Background { get; set; } = new();
    /// <summary>Noch nie gespeichert: dann die Vorgabe.</summary>
    public bool Saved { get; set; }
}

/// <summary>
/// Übersicht (Dashboard, Version 2): konfigurierbare Widgets in Web und App. Zusammenstellung und Hintergrund liegen in der
/// Benutzerdatenbank (kv „dashboard“, Bild unter „dashboard.bg“ als WebP bis 1600 px) - keine neue Tabelle, keine Migration.
/// </summary>
public static class DashboardStore
{
    public const string Key = "dashboard", ImageKey = "dashboard.bg";
    public static readonly string[] Types =
    [
        "today", "events", "meals", "tasks", "shopping", "mail", "chat", "pukki", "weather", "news", "photos", "notes", "files",
        "birthdays", "countdown", "bookmarks", "storage", "system",
    ];

    /// <summary>Widget-Art bekannt und ihr Modul eingeschaltet (Verwaltung): Aufgaben und Einkaufsliste,
    /// Notizen, Fotos nur mit ihrem Bereich. Gespeicherte Widgets einer Art, die diese Version nicht kennt, bleiben unsichtbar.</summary>
    public static bool Available(string type) => type switch
    {
        "tasks" or "shopping" => Features.On(Features.Tasks),
        "notes" => Features.On(Features.Notes),
        "photos" => Features.On(Features.Gallery),
        _ => type == "stack" || Types.Contains(type),
    };

    /// <summary>Zum Ausliefern: Widgets nicht freigeschalteter Arten weglassen (auch in Stapeln). Gespeichert bleiben sie -
    /// siehe <see cref="Save"/> -, damit sie beim Freischalten wieder da sind.</summary>
    public static Layout Visible(Layout l)
    {
        static Widget Strip(Widget w) => w.Stack == null ? w : new Widget
        {
            Id = w.Id, Type = w.Type, Size = w.Size, X = w.X, Y = w.Y, Title = w.Title, Opts = w.Opts, Smart = w.Smart,
            Stack = w.Stack.Where(x => Available(x.Type)).ToList(),
        };
        return new Layout
        {
            Widgets = l.Widgets.Where(w => Available(w.Type)).Select(Strip).ToList(),
            Cols = l.Cols, Background = l.Background, Saved = l.Saved,
        };
    }

    /// <summary>
    /// Je Geräteart eine eigene Zusammenstellung: „desktop“ (Web auf breitem Bildschirm) und „phone“ (App, Web auf dem Handy) -
    /// was auf dem einen gut aussieht, passt auf dem anderen selten. Das Handy fängt mit einer Kopie der Desktop-Fassung an.
    /// </summary>
    public static string Device(string? device) => device == "phone" ? "phone" : "desktop";
    private static string KeyOf(string device) => device == "phone" ? Key + ".phone" : Key;
    private static string ImageKeyOf(string device) => device == "phone" ? ImageKey + ".phone" : ImageKey;

    public static Layout Of(UserDb db, string device = "desktop")
    {
        var l = db.GetJson<Layout>(KeyOf(device));
        if (l.Saved) return l;
        if (device == "phone") { var d = Of(db, "desktop"); d.Saved = false; return d; }
        return Default();
    }

    /// <summary>Zurück auf die Vorgabe (Handy: wieder die Desktop-Fassung).</summary>
    public static void Reset(UserDb db, string device = "desktop") => db.Set(KeyOf(device), null);

    /// <summary>Vorgabe für den Anfang: Heute, Kalender groß, Aufgaben groß, E-Mail, Chat, Fotos, Notizen.</summary>
    public static Layout Default() => new()
    {
        Widgets =
        [
            new() { Id = "w-today", Type = "today", Size = 2 },
            new() { Id = "w-events", Type = "events", Size = 3 },
            new() { Id = "w-tasks", Type = "tasks", Size = 3 },
            new() { Id = "w-mail", Type = "mail", Size = 1 },
            new() { Id = "w-chat", Type = "chat", Size = 1 },
            new() { Id = "w-photos", Type = "photos", Size = 2 },
            new() { Id = "w-notes", Type = "notes", Size = 2 },
            new() { Id = "w-files", Type = "files", Size = 2 },
        ],
    };

    public static void Save(UserDb db, Layout l, string device = "desktop")
    {
        foreach (var w in l.Widgets.Concat(l.Widgets.SelectMany(w => w.Stack ?? [])))
            if (!Types.Contains(w.Type) && w.Type != "stack") throw new ArgumentException($"Unbekanntes Widget „{w.Type}“.");
        // Widgets nicht freigeschalteter oder unbekannter Arten hat die Oberfläche nicht gesehen: aus der bisherigen Fassung übernehmen
        // (auch aus der Vorgabe, solange noch nie gespeichert)
        var before = Of(db, device);
        {
            var ids = l.Widgets.Select(w => w.Id).ToHashSet();
            l.Widgets.AddRange(before.Widgets.Where(w => !Available(w.Type) && !ids.Contains(w.Id)));
            foreach (var old in before.Widgets.Where(w => w.Stack != null))
                if (l.Widgets.FirstOrDefault(w => w.Id == old.Id && w.Stack != null) is { } now)
                {
                    var inStack = now.Stack!.Select(x => x.Id).ToHashSet();
                    now.Stack.AddRange(old.Stack!.Where(x => !Available(x.Type) && !inStack.Contains(x.Id)));
                }
        }
        l.Saved = true;
        l.Widgets = l.Widgets.Take(60).ToList();
        foreach (var w in l.Widgets.Concat(l.Widgets.SelectMany(w => w.Stack ?? [])))
        {
            w.Size = Math.Clamp(w.Size, 1, 4);
            if (w.X is int x) w.X = Math.Clamp(x, 0, 23);
            if (w.Y is int y) w.Y = Math.Clamp(y, 0, 199);
            if (string.IsNullOrWhiteSpace(w.Id)) w.Id = Guid.NewGuid().ToString("N")[..8];
            if (w.Title is { Length: > 60 }) w.Title = w.Title[..60];
        }
        if (l.Cols is int c) l.Cols = Math.Clamp(c, 1, 12);
        l.Background.Dim = Math.Clamp(l.Background.Dim, 0, 80);
        if (l.Background.Kind is not ("none" or "gallery" or "upload" or "album")) l.Background.Kind = "none";
        db.SetJson(KeyOf(device), l);
    }

    /// <summary>Hochgeladenes Hintergrundbild ablegen (verkleinert auf 1600 px, WebP).</summary>
    public static void StoreImage(UserDb db, byte[] image, string device = "desktop")
    {
        var small = Dav.Thumbs.FromBytes(image, Dav.Thumbs.Large) ?? throw new ArgumentException("Das ist kein Bild, das sich anzeigen lässt.");
        db.Set(ImageKeyOf(device), Convert.ToBase64String(small));
    }

    /// <summary>Hochgeladenes Hintergrundbild der Geräteart; das Handy ohne eigenes nimmt das vom Desktop (es fing mit dessen Zusammenstellung an).</summary>
    public static byte[]? Image(UserDb db, string device = "desktop") =>
        db.Get(ImageKeyOf(device)) is { } b ? Convert.FromBase64String(b) : device == "phone" ? Image(db, "desktop") : null;
    public static void RemoveImage(UserDb db, string device = "desktop") => db.Set(ImageKeyOf(device), null);
}
