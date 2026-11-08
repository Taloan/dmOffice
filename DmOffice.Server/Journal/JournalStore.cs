using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Pim;

namespace DmOffice.Journal;

/// <summary>Ort eines Eintrags: Name (frei) und Koordinaten (aus dem Foto oder vom Handy).</summary>
public sealed record JournalPlace(string? Name, double? Lat, double? Lon);

/// <summary>Foto, Video, Sprachaufnahme oder Datei eines Eintrags - liegt in der Dateiablage.</summary>
public sealed record JournalMedia(string Path, string Mime, long? Size = null, int? Width = null, int? Height = null, DateTime? Taken = null);

/// <summary>Verweis auf etwas anderes in dmOffice: Notiz (sync_id), Datei (Pfad), Termin („Kalender/uri“).</summary>
public sealed record JournalRef(string Type, string Id, string Name);

/// <summary>Ein Tagebucheintrag, wie ihn Web und App bekommen und schicken.</summary>
public sealed record JournalEntry(
    string Id, long Journal, string Uri, string Etag, DateTime Date, string? Title, string Html, string Text,
    int? Mood, List<string> Tags, JournalPlace? Place, bool Private, List<JournalMedia> Media, List<JournalRef> Refs,
    bool ShowEvents, DateTime? Created, DateTime? Modified,
    /// <summary>Wetter als kurzer Text („21 / 12 °C“, „sonnig, 18 °C“)</summary>
    string? Weather = null,
    /// <summary>Herkunft beim Import („diarium:2026-09-08_180907953“) - ein zweiter Import legt nichts doppelt an</summary>
    string? Import = null,
    /// <summary>Geteiltes Tagebuch: wem es gehört (null: eigenes) und ob man darin schreiben darf</summary>
    string? Owner = null, bool CanWrite = true);

/// <summary>
/// Tagebuch (Version 2): je Tagebuch eine Sammlung mit der Komponente VJOURNAL, je Eintrag ein iCalendar-Objekt - so gleichen
/// auch CalDAV-Programme (etwa jtx Board) es ab, und die Benutzerdatenbank braucht keine neuen Tabellen.
/// Datum DTSTART (UTC), Titel SUMMARY, Text als HTML in X-ALT-DESC und als reiner Text in DESCRIPTION, Schlagwörter CATEGORIES,
/// Ort LOCATION/GEO, privat CLASS:PRIVATE, Stimmung X-DM-MOOD (1-5), Wetter X-DM-WEATHER, Medien ATTACH (dmoffice:files/…), Verweise X-DM-REF.
/// </summary>
public static class JournalStore
{
    public const string Component = "VJOURNAL";
    private const string FilesScheme = "dmoffice:files/";

    /// <summary>Eigene Tagebücher; ohne eins wird „Mein Tagebuch“ angelegt.</summary>
    public static List<Collection> Journals(UserDb db)
    {
        var list = PimStore.Collections(db, PimStore.Calendar).Where(IsJournal).ToList();
        if (list.Count > 0 || db.Get("journal.created") == "1") return list;
        db.Set("journal.created", "1");
        return [PimStore.CreateCollection(db, PimStore.Calendar, "tagebuch", "Mein Tagebuch", "#7B1DD3", Component)];
    }

    public static bool IsJournal(Collection c) => c.Components.Split(',').Contains(Component) && !c.Components.Contains("VEVENT");

    public static Collection Create(UserDb db, string name, string? color)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("Bitte einen Namen eingeben.");
        Journals(db);
        return PimStore.CreateCollection(db, PimStore.Calendar, Guid.NewGuid().ToString("N")[..12], name, color, Component);
    }

    public static Collection Require(UserDb db, long id) =>
        PimStore.Find(db, id) is { } c && IsJournal(c) ? c : throw new KeyNotFoundException("Dieses Tagebuch gibt es nicht.");

    /// <summary>Alle Einträge eines Tagebuchs (unsortiert); kaputte Objekte fallen weg.</summary>
    public static IEnumerable<JournalEntry> Entries(UserDb db, Collection journal) => Entries(new PimJournal(db, journal));

    public static IEnumerable<JournalEntry> Entries(IJournalBox box)
    {
        foreach (var o in box.Objects())
            if (Read(o) is { } e) yield return e;
    }

    public static JournalEntry? Read(DavObject o)
    {
        Component? j;
        try { j = Ics.Parse(o.Data).FirstOrDefault(c => c.Name == "VCALENDAR")?.Of(Component).FirstOrDefault(); }
        catch (Exception) { return null; }
        if (j == null) return null;
        var date = Ics.Time(j.Get("DTSTART"), TimeZoneInfo.Utc, out _) ?? Stamp(j.Text("CREATED")) ?? DateTime.UtcNow;
        var html = j.Get("X-ALT-DESC") is { } alt && (alt.Param("FMTTYPE") ?? "").Equals("text/html", StringComparison.OrdinalIgnoreCase)
            ? alt.Text : PlainToHtml(j.Text("DESCRIPTION") ?? "");
        JournalPlace? place = null;
        double? lat = null, lon = null;
        if (j.Text("GEO") is { } geo && geo.Split(';') is [var a, var b]
            && double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var la) && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var lo))
            (lat, lon) = (la, lo);
        if (j.Text("LOCATION") is { Length: > 0 } || lat != null) place = new JournalPlace(j.Text("LOCATION"), lat, lon);
        var media = j.All("ATTACH").Select(p =>
        {
            var v = p.Value.Trim();
            if (!v.StartsWith(FilesScheme, StringComparison.OrdinalIgnoreCase)) return null;
            return new JournalMedia(Uri.UnescapeDataString(v[FilesScheme.Length..]), p.Param("FMTTYPE") ?? "application/octet-stream",
                long.TryParse(p.Param("X-DM-SIZE"), out var s) ? s : null, int.TryParse(p.Param("X-DM-W"), out var w) ? w : null,
                int.TryParse(p.Param("X-DM-H"), out var h) ? h : null, Stamp(p.Param("X-DM-TAKEN")));
        }).OfType<JournalMedia>().ToList();
        var refs = j.All("X-DM-REF").Select(p =>
        {
            try { return JsonSerializer.Deserialize<JournalRef>(p.Text, Json); } catch (JsonException) { return null; }
        }).OfType<JournalRef>().ToList();
        var tags = j.All("CATEGORIES").SelectMany(p => SplitList(p.Value)).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new JournalEntry(o.Collection + "/" + o.Uri, o.Collection, o.Uri, o.Etag, date, j.Text("SUMMARY"), html, j.Text("DESCRIPTION") ?? "",
            int.TryParse(j.Text("X-DM-MOOD"), out var mood) && mood is >= 1 and <= 5 ? mood : null, tags, place,
            (j.Text("CLASS") ?? "").Equals("PRIVATE", StringComparison.OrdinalIgnoreCase), media, refs, j.Text("X-DM-EVENTS") == "1",
            Stamp(j.Text("CREATED")), Stamp(j.Text("LAST-MODIFIED")), j.Text("X-DM-WEATHER"), j.Text("X-DM-IMPORT"));
    }

    /// <summary>Verweise auf Medien umschreiben ([map]: neuer Pfad oder null = bleibt). Zurück: wie viele Einträge sich änderten.</summary>
    public static int RewriteMedia(UserDb db, Collection journal, Func<string, string?> map)
    {
        var box = new PimJournal(db, journal);
        var changed = 0;
        foreach (var o in box.Objects())
        {
            if (MapMedia(o.Data, map) is not { } data) continue;
            box.Put(o.Uri, data, o.Etag);
            changed++;
        }
        return changed;
    }

    /// <summary>Medien eines iCalendar-Objekts umschreiben ([map]: neuer Pfad oder null = bleibt). Null: nichts geändert.</summary>
    public static string? MapMedia(string data, Func<string, string?> map)
    {
        var vcal = Ics.Parse(data).FirstOrDefault(c => c.Name == "VCALENDAR");
        var j = vcal?.Of(Component).FirstOrDefault();
        if (j == null) return null;
        var any = false;
        foreach (var p in j.All("ATTACH"))
        {
            var v = p.Value.Trim();
            if (!v.StartsWith(FilesScheme, StringComparison.OrdinalIgnoreCase)) continue;
            if (map(Uri.UnescapeDataString(v[FilesScheme.Length..])) is not { } to) continue;
            p.Value = FilesScheme + Uri.EscapeDataString(to);
            any = true;
        }
        return any ? vcal!.ToString() : null;
    }

    /// <summary>Eintrag speichern (neu ohne uri); mit etag nur, wenn er noch gilt. Zurück: der gespeicherte Eintrag.</summary>
    public static JournalEntry Save(UserDb db, Collection journal, JournalInput input) => Save(new PimJournal(db, journal), input);

    public static JournalEntry Save(IJournalBox box, JournalInput input)
    {
        Component vcal, j;
        string uri;
        string? etag = null;
        if (string.IsNullOrEmpty(input.Uri))
        {
            uri = PimStore.NewUri(".ics");
            vcal = CalendarModel.NewCalendar();
            j = new Component(Component);
            j.Set("UID", Guid.NewGuid().ToString("D"));
            j.Set("CREATED", Ics.Stamp());
            vcal.Children.Add(j);
        }
        else
        {
            uri = input.Uri;
            var obj = box.Object(uri) ?? throw new KeyNotFoundException("Diesen Eintrag gibt es nicht.");
            if (!string.IsNullOrEmpty(input.Etag) && input.Etag != obj.Etag) throw new PreconditionFailedException();
            etag = obj.Etag;
            vcal = Ics.Parse(obj.Data).First(c => c.Name == "VCALENDAR");
            j = vcal.Of(Component).First();
        }
        Fill(j, input);
        box.Put(uri, vcal.ToString(), etag);
        return Read(box.Object(uri)!)!;
    }

    private static void Fill(Component j, JournalInput input)
    {
        j.Set("DTSTAMP", Ics.Stamp());
        j.Set("LAST-MODIFIED", Ics.Stamp());
        j.Set("DTSTART", Ics.Utc(DateTime.SpecifyKind(input.Date, input.Date.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : input.Date.Kind)));
        j.SetText("SUMMARY", string.IsNullOrWhiteSpace(input.Title) ? null : input.Title.Trim());
        var html = JournalHtml.Clean(input.Html ?? "");
        j.SetText("DESCRIPTION", JournalHtml.Plain(html));
        j.Remove("X-ALT-DESC");
        if (html.Length > 0) j.Props.Add(new Prop("X-ALT-DESC", Ics.Escape(html)).With("FMTTYPE", "text/html"));
        j.Remove("CATEGORIES");
        var tags = (input.Tags ?? []).Select(t => t.Trim().TrimStart('#')).Where(t => t.Length is > 0 and <= 40).Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToList();
        if (tags.Count > 0) j.Set("CATEGORIES", string.Join(",", tags.Select(Ics.Escape)));
        if (input.Mood is >= 1 and <= 5) j.Set("X-DM-MOOD", input.Mood.Value.ToString(CultureInfo.InvariantCulture)); else j.Remove("X-DM-MOOD");
        if (input.Private) j.Set("CLASS", "PRIVATE"); else j.Remove("CLASS");
        if (input.ShowEvents) j.Set("X-DM-EVENTS", "1"); else j.Remove("X-DM-EVENTS");
        j.SetText("X-DM-WEATHER", string.IsNullOrWhiteSpace(input.Weather) ? null : input.Weather.Trim());
        if (!string.IsNullOrEmpty(input.Import)) j.SetText("X-DM-IMPORT", input.Import);
        j.SetText("LOCATION", string.IsNullOrWhiteSpace(input.Place?.Name) ? null : input.Place!.Name!.Trim());
        if (input.Place?.Lat is double lat && input.Place.Lon is double lon && Math.Abs(lat) <= 90 && Math.Abs(lon) <= 180)
            j.Set("GEO", lat.ToString("0.######", CultureInfo.InvariantCulture) + ";" + lon.ToString("0.######", CultureInfo.InvariantCulture));
        else j.Remove("GEO");
        j.Remove("ATTACH");
        foreach (var m in input.Media ?? [])
        {
            var path = FileStore.Normalize(m.Path);
            if (path.Length == 0) continue;
            var p = new Prop("ATTACH", FilesScheme + Uri.EscapeDataString(path)).With("FMTTYPE", Safe(m.Mime) ?? "application/octet-stream");
            if (m.Size is long s) p.With("X-DM-SIZE", s.ToString(CultureInfo.InvariantCulture));
            if (m.Width is int w) p.With("X-DM-W", w.ToString(CultureInfo.InvariantCulture));
            if (m.Height is int h) p.With("X-DM-H", h.ToString(CultureInfo.InvariantCulture));
            if (m.Taken is DateTime t) p.With("X-DM-TAKEN", Ics.Utc(DateTime.SpecifyKind(t, t.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : t.Kind)));
            j.Props.Add(p);
        }
        j.Remove("X-DM-REF");
        foreach (var r in (input.Refs ?? []).Where(r => r.Type is "note" or "file" or "event" && r.Id.Length > 0).Take(50))
            j.Props.Add(new Prop("X-DM-REF", Ics.Escape(JsonSerializer.Serialize(r, Json))));
    }

    /// <summary>MIME-Typ ohne Zeichen, die in einem iCalendar-Parameter stören.</summary>
    private static string? Safe(string? mime) => mime != null && Regex.IsMatch(mime, @"^[\w.+-]+/[\w.+-]+$") ? mime.ToLowerInvariant() : null;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static DateTime? Stamp(string? v) => v != null && DateTime.TryParseExact(v.Trim(), "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : null;

    /// <summary>Kommagetrennte iCalendar-Liste (mit \, als Komma im Wert).</summary>
    private static IEnumerable<string> SplitList(string raw) =>
        Regex.Split(raw, @"(?<!\\),").Select(x => Ics.Unescape(x).Trim());

    private static string PlainToHtml(string text) => text.Length == 0 ? "" :
        string.Concat(text.Replace("\r", "").Split('\n').Select(l => "<p>" + System.Net.WebUtility.HtmlEncode(l) + "</p>"));
}

/// <summary>Wo die Einträge eines Tagebuchs liegen: Sammlung der Benutzerdatenbank (CalDAV) oder Tagebuch im Tresor.</summary>
public interface IJournalBox
{
    List<DavObject> Objects();
    DavObject? Object(string uri);
    /// <summary>Speichern; mit ifMatch nur, wenn der Stand noch gilt (sonst <see cref="PreconditionFailedException"/>).</summary>
    void Put(string uri, string data, string? ifMatch);
    bool Delete(string uri);
}

/// <summary>Tagebuch als Sammlung in der Benutzerdatenbank.</summary>
public sealed class PimJournal(UserDb db, Collection coll) : IJournalBox
{
    public List<DavObject> Objects() => PimStore.Objects(db, coll.Id);
    public DavObject? Object(string uri) => PimStore.Object(db, coll.Id, uri);
    public void Put(string uri, string data, string? ifMatch) => PimStore.Put(db, coll, uri, data, ifMatch: ifMatch);
    public bool Delete(string uri) => PimStore.Delete(db, coll, uri);
}

/// <summary>Was Web und App zum Speichern schicken.</summary>
public sealed class JournalInput
{
    public long Journal { get; set; }
    /// <summary>Leer: neuer Eintrag.</summary>
    public string? Uri { get; set; }
    public string? Etag { get; set; }
    public DateTime Date { get; set; }
    public string? Title { get; set; }
    public string? Html { get; set; }
    public int? Mood { get; set; }
    public List<string>? Tags { get; set; }
    public JournalPlace? Place { get; set; }
    public bool Private { get; set; }
    public bool ShowEvents { get; set; }
    public string? Weather { get; set; }
    /// <summary>Nur beim Import gesetzt (bleibt danach erhalten)</summary>
    public string? Import { get; set; }
    public List<JournalMedia>? Media { get; set; }
    public List<JournalRef>? Refs { get; set; }
}
