using System.Globalization;
using System.Text.RegularExpressions;
using DmOffice.Core;
using DmOffice.Gallery;

namespace DmOffice.Dms;

/// <summary>Ein Ablageprofil: wo und unter welchem Namen DMS ein Dokument ablegt.</summary>
public sealed class DmsProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = "Standard";
    /// <summary>„files“ (Dateiablage), „tresor“ oder „external“ (externer Ordner <see cref="Mount"/>, unverschlüsselt).</summary>
    public string Target { get; set; } = "files";
    /// <summary>Externer Ordner (Kennung) bei Ziel „external“.</summary>
    public string? Mount { get; set; }
    /// <summary>Extern: geänderte Angaben neu in die PDF schreiben (Archivfassungen vom DMS).</summary>
    public bool WriteMeta { get; set; } = true;
    /// <summary>Ordner unter dem Zielordner für Dokumente in der Inbox (noch nicht bestätigt): flach, nur nach dem Namensmuster.</summary>
    public string InboxFolder { get; set; } = DmsFiling.DefaultInbox;
    public string Folder { get; set; } = "Dokumente";
    public string Folders { get; set; } = "{correspondent}/{doc_year}";
    /// <summary>Dateiname ohne Endung.</summary>
    public string Names { get; set; } = "{doc_date} {doctype} {title}";
    /// <summary>Text für leere Platzhalter.</summary>
    public string Unknown { get; set; } = "Unbekannt";
    /// <summary>Ablage folgt den Angaben: bei Änderungen nach dem Muster neu ablegen.</summary>
    public bool Follow { get; set; } = true;
    /// <summary>Vorbelegt beim Hochladen, für Mail und Teilen.</summary>
    public bool Default { get; set; }
    /// <summary>Sprache der Oberfläche beim Speichern: „de“ oder „en“ (Monatsnamen).</summary>
    public string Language { get; set; } = "de";
}

/// <summary>
/// Ablageprofile vom DMS (docs/dms-konzept.md, „Ablageprofile“): Zielordner, Ordner- und Namensmuster mit Platzhaltern wie bei den
/// Importprofilen der Galerie (<see cref="ImportProfiles"/>), aber mit eigenen Platzhaltern und nur in die eigene Ablage.
/// </summary>
public static class DmsFiling
{
    public const string Key = "dms.profiles";
    public const string DefaultInbox = ".INBOX";

    public static readonly string[] Tokens = ["correspondent", "correspondent_path", "doctype", "doctype_path", "tag", "tags", "title",
        "doc_date", "doc_year", "doc_month", "doc_month_name", "doc_day", "import_date", "import_year", "import_month", "import_month_name", "import_day",
        "original_name", "source", "sender"];

    private static readonly object SaveLock = new();

    /// <summary>Sprache für das erste Profil (die Oberfläche teilt sie beim ersten Aufruf mit).</summary>
    [ThreadStatic] internal static string? FirstLanguageOverride;
    private static string FirstLanguage => FirstLanguageOverride == "en" ? "en" : "de";

    /// <summary>Die Profile; beim ersten Mal das Profil „Standard“ (Dokumente).</summary>
    public static List<DmsProfile> Of(UserSpace space)
    {
        lock (SaveLock)
        {
            var list = space.Db.GetJson<List<DmsProfile>>(Key);
            if (list.Count > 0) return list;
            var first = new DmsProfile { Default = true, Language = FirstLanguage };
            if (first.Language == "en") { first.Name = "Default"; first.Folder = "Documents"; first.Unknown = "Unknown"; }
            list = [first];
            space.Db.SetJson(Key, list);
            DmsHost.Current?.ExcludeFromGallery(space, first.Folder);
            return list;
        }
    }

    public static DmsProfile Default(UserSpace space)
    {
        var list = Of(space);
        return list.FirstOrDefault(p => p.Default) ?? list[0];
    }

    public static DmsProfile? Find(UserSpace space, string? id) => id == null ? null : Of(space).FirstOrDefault(p => p.Id == id);

    private static readonly Regex Token = new(@"\{([a-z_]+)\}", RegexOptions.Compiled);

    public static List<string> Unknown(string pattern) =>
        Token.Matches(pattern).Select(m => m.Groups[1].Value).Where(t => !Tokens.Contains(t)).Distinct().ToList();

    /// <summary>Profil anlegen oder ändern (Id vorhanden). Prüft Muster und Ordner, nimmt den Ordner aus der Galerie.</summary>
    public static DmsProfile Save(UserSpace space, DmsProfile p, IDmsFiles files, IDmsExternal? external = null)
    {
        p.Name = (p.Name ?? "").Trim();
        if (p.Name.Length == 0) throw new ArgumentException("Bitte einen Namen eingeben.");
        if (p.Target is not ("files" or "tresor" or "external")) throw new ArgumentException("Unbekanntes Ziel.");
        if (p.Target == "tresor" && !Core.Tresor.Exists(space.User.Id)) throw new ArgumentException("Für dieses Ziel muss zuerst ein Tresor eingerichtet sein.");
        if (p.Target == "external")
        {
            if (string.IsNullOrEmpty(p.Mount) || external?.MountInfo(space, p.Mount) is not { } info) throw new ArgumentException("Bitte einen externen Ordner wählen.");
            if (info.ReadOnly) throw new ArgumentException("Der externe Ordner ist nur zum Lesen freigegeben; dort kann das DMS nichts ablegen.");
        }
        else p.Mount = null;
        p.Folder = p.Target == "external" ? Storage.StoragePath.Clean(p.Folder) : Dav.FileStore.Normalize(p.Folder ?? "");
        if (p.Folder.Length == 0) throw new ArgumentException("Bitte einen Zielordner angeben.");
        if (p.Target == "files" && files.Protected(space, p.Folder)) throw new ArgumentException("In diesem Ordner kann das DMS nichts ablegen.");
        foreach (var pattern in new[] { p.Folders ?? "", p.Names ?? "" })
            if (Unknown(pattern) is { Count: > 0 } bad) throw new ArgumentException("Unbekannter Platzhalter: {" + string.Join("}, {", bad) + "}");
        if (string.IsNullOrWhiteSpace(p.Names)) throw new ArgumentException("Bitte ein Namensmuster angeben.");
        if (DmsSources.Of(space.Db).Any(s => DmsSources.Overlap(s.Mount, s.Folder, p)))
            throw new ArgumentException("Quellordner und Zielordner des Ablageprofils dürfen nicht ineinander liegen.");
        p.InboxFolder = string.IsNullOrWhiteSpace(p.InboxFolder) ? DefaultInbox : p.InboxFolder.Trim();
        if (p.InboxFolder.IndexOfAny(['/', '\\']) >= 0 || p.InboxFolder is "." or ".." || p.InboxFolder.Any(char.IsControl))
            throw new ArgumentException("Der Inbox-Ordner ist ein einzelner Ordnername ohne Schrägstrich.");
        p.Folders ??= "";
        p.Unknown = string.IsNullOrWhiteSpace(p.Unknown) ? "Unbekannt" : p.Unknown.Trim();
        lock (SaveLock)
        {
            var list = Of(space);
            if (list.Any(x => x.Id != p.Id && x.Name.Equals(p.Name, StringComparison.CurrentCultureIgnoreCase)))
                throw new ArgumentException($"Ein Ablageprofil „{p.Name}“ gibt es schon.");
            var i = list.FindIndex(x => x.Id == p.Id);
            if (i < 0) list.Add(p); else list[i] = p;
            if (p.Default) foreach (var x in list) x.Default = x.Id == p.Id;
            if (!list.Any(x => x.Default)) list[0].Default = true;
            space.Db.SetJson(Key, list);
        }
        // Galerie zeigt den Tresor ohnehin nie
        if (p.Target == "files") files.ExcludeFromGallery(space, p.Folder);
        return p;
    }

    /// <summary>Profil löschen; das letzte bleibt. Dokumente des Profils behalten ihren Ort (ohne Profil folgen sie dem Standard).</summary>
    public static void Delete(UserSpace space, string id)
    {
        lock (SaveLock)
        {
            var list = Of(space);
            if (list.Count <= 1) throw new ArgumentException("Das letzte Ablageprofil lässt sich nicht löschen.");
            if (list.RemoveAll(p => p.Id == id) == 0) throw new KeyNotFoundException("Ablageprofil nicht gefunden.");
            if (!list.Any(x => x.Default)) list[0].Default = true;
            space.Db.SetJson(Key, list);
        }
    }

    // ---------------- Muster ----------------

    /// <summary>Angaben eines Dokuments für die Platzhalter.</summary>
    public sealed record Facts(List<string> Correspondent, List<string> Doctype, List<string> Tags, string Title, DateOnly? DocDate,
        DateTime Imported, string OriginalName, string Source, string? Sender);

    public static Facts FactsOf(UserSpace space, DmsDoc d, string sourceName)
    {
        var all = Dms.Entries(space.Db);
        var byId = all.ToDictionary(e => e.Id);
        DateOnly? date = d.DocDate != null && DateOnly.TryParseExact(d.DocDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var x) ? x : null;
        return new Facts(d.Correspondent != null && byId.ContainsKey(d.Correspondent) ? Dms.PathOf(all, d.Correspondent) : [],
            d.Doctype != null && byId.ContainsKey(d.Doctype) ? Dms.PathOf(all, d.Doctype) : [],
            d.Tags.Where(t => byId.ContainsKey(t.Id)).Select(t => byId[t.Id].Name).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList(),
            d.Title, date, TimeZoneInfo.ConvertTimeFromUtc(d.Imported, space.Zone), Path.GetFileNameWithoutExtension(d.OriginalName), sourceName, d.Sender);
    }

    /// <summary>Beispiel für die Vorschau.</summary>
    public static Facts Example(string lang) => lang == "en"
        ? new(["Tax office"], ["Notice"], ["tax", "2025"], "Income tax 2025", new DateOnly(2026, 3, 14), new DateTime(2026, 3, 16, 9, 30, 0), "scan_0042", "Scanner", null)
        : new(["Finanzamt"], ["Bescheid"], ["steuer", "2025"], "Einkommensteuer 2025", new DateOnly(2026, 3, 14), new DateTime(2026, 3, 16, 9, 30, 0), "scan_0042", "Scanner", null);

    /// <summary>Wert eines Platzhalters (Pfade: Ebenen einzeln, im Ordnermuster mit „/“ getrennt); leer → Text „Unbekannt“ des Profils.</summary>
    private static List<string> Value(string token, Facts f, CultureInfo culture)
    {
        static string D(DateOnly d, string fmt) => d.ToString(fmt, CultureInfo.InvariantCulture);
        static string T(DateTime d, string fmt) => d.ToString(fmt, CultureInfo.InvariantCulture);
        string? v = token switch
        {
            "correspondent" => f.Correspondent.LastOrDefault(),
            "doctype" => f.Doctype.LastOrDefault(),
            "tag" => f.Tags.FirstOrDefault(),
            "tags" => f.Tags.Count == 0 ? null : string.Join(", ", f.Tags),
            "title" => f.Title,
            "doc_date" => f.DocDate is { } d1 ? D(d1, "yyyy-MM-dd") : null,
            "doc_year" => f.DocDate is { } d2 ? D(d2, "yyyy") : null,
            "doc_month" => f.DocDate is { } d3 ? D(d3, "MM") : null,
            "doc_month_name" => f.DocDate is { } d4 ? culture.DateTimeFormat.GetMonthName(d4.Month) : null,
            "doc_day" => f.DocDate is { } d5 ? D(d5, "dd") : null,
            "import_date" => T(f.Imported, "yyyy-MM-dd"),
            "import_year" => T(f.Imported, "yyyy"),
            "import_month" => T(f.Imported, "MM"),
            "import_month_name" => culture.DateTimeFormat.GetMonthName(f.Imported.Month),
            "import_day" => T(f.Imported, "dd"),
            "original_name" => f.OriginalName,
            "source" => f.Source,
            "sender" => f.Sender,
            _ => null,
        };
        if (token == "correspondent_path") return f.Correspondent;
        if (token == "doctype_path") return f.Doctype;
        return string.IsNullOrWhiteSpace(v) ? [] : [v];
    }

    /// <summary>Ordner unter dem Zielordner: Platzhalter ersetzen; Schrägstriche des Musters (und der Pfad-Platzhalter) trennen Ebenen.</summary>
    public static string RenderFolder(DmsProfile p, Facts f)
    {
        var culture = ImportProfiles.CultureOf(p.Language);
        var filled = Token.Replace(p.Folders, m =>
        {
            var parts = Value(m.Groups[1].Value, f, culture);
            return parts.Count == 0 ? ImportProfiles.Safe(p.Unknown) : string.Join("/", parts.Select(ImportProfiles.Safe));
        });
        var segs = filled.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim().TrimEnd('.')).Where(x => x.Length > 0 && x != "..").Select(x => x.Length > 120 ? x[..120].TrimEnd() : x);
        return string.Join('/', segs);
    }

    /// <summary>Dateiname ohne Endung.</summary>
    public static string RenderName(DmsProfile p, Facts f)
    {
        var culture = ImportProfiles.CultureOf(p.Language);
        var filled = Token.Replace(p.Names, m =>
        {
            var parts = Value(m.Groups[1].Value, f, culture);
            return parts.Count == 0 ? p.Unknown : string.Join(" - ", parts);
        });
        var v = ImportProfiles.Safe(Regex.Replace(filled, @"\s+", " ").Trim());
        return v.Length > 150 ? v[..150].TrimEnd() : v;
    }

    /// <summary>
    /// Ganzer Pfad in der Ablage. In der Inbox (noch nicht bestätigt) flach im Inbox-Ordner unter dem Zielordner, sonst nach dem
    /// Ordnermuster.
    /// </summary>
    public static string PathFor(DmsProfile p, Facts f, string ext, bool inbox = false) =>
        Storage.StoragePath.Join(Storage.StoragePath.Join(p.Folder, inbox ? InboxOf(p) : RenderFolder(p, f)), RenderName(p, f) + ext);

    private static string InboxOf(DmsProfile p) => string.IsNullOrWhiteSpace(p.InboxFolder) ? DefaultInbox : p.InboxFolder;

    /// <summary>Liegt der Pfad im Inbox-Ordner des Profils?</summary>
    public static bool InInbox(DmsProfile p, string path) =>
        path.StartsWith(Storage.StoragePath.Join(p.Folder, InboxOf(p)) + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Liegt die Datei schon dort, wo das Muster sie haben will (auch mit „ (2)“ bei gleichem Namen)?</summary>
    public static bool SamePlace(string current, string wanted)
    {
        if (current == wanted) return true;
        if (Dav.FileStore.Parent(current) != Dav.FileStore.Parent(wanted)) return false;
        var (a, b) = (Path.GetFileNameWithoutExtension(current), Path.GetFileNameWithoutExtension(wanted));
        return Path.GetExtension(current) == Path.GetExtension(wanted) && Regex.IsMatch(a, "^" + Regex.Escape(b) + @" \(\d+\)$");
    }
}
