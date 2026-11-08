using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Pim;

namespace DmOffice.Journal;

/// <summary>
/// Import aus Diarium (Export als ZIP): eine JSON-Datei mit allen Einträgen (date ohne Zeitzone, heading, html, tags, people,
/// tracker, location [Breite, Länge], weather „21 / 12 °C“) und je Eintrag ein Ordner media/&lt;yyyy-MM-dd_HHmmssfff&gt;/ mit
/// den Bildern. Die ZIP liegt in der Dateiablage (auch groß: in Stücken hochgeladen); gelesen wird Datei für Datei.
/// Läuft im Hintergrund; ein zweiter Import derselben Einträge legt nichts doppelt an.
/// </summary>
public static class JournalImport
{
    public sealed class Status
    {
        public bool Running { get; set; }
        public string? File { get; set; }
        public int Total { get; set; }
        public int Done { get; set; }
        public int Created { get; set; }
        public int Skipped { get; set; }
        public int Media { get; set; }
        public string? Error { get; set; }
        public DateTime? Finished { get; set; }
    }

    private static readonly ConcurrentDictionary<string, Status> Jobs = new();

    public static Status Of(string user) => Jobs.TryGetValue(user, out var s) ? s : new Status();

    private sealed class Item
    {
        public string? Date { get; set; }
        public string? Heading { get; set; }
        public string? Html { get; set; }
        public List<JsonElement>? Tags { get; set; }
        public List<JsonElement>? People { get; set; }
        public List<double>? Location { get; set; }
        public string? Weather { get; set; }
    }

    /// <summary>Import starten (im Hintergrund). Fehler am Anfang (keine Datei, kein Diarium-Export) kommen sofort.</summary>
    public static Status Start(UserSpace space, long journalId, string path)
    {
        var db = space.Db;
        var journal = JournalStore.Require(db, journalId);
        var f = FileStore.Get(db, FileStore.Normalize(path)) ?? throw new KeyNotFoundException("Diese Datei gibt es nicht.");
        if (f.IsDir || !f.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Bitte die ZIP-Datei aus dem Export von dmOffice, Diarium oder Journey wählen.");
        var status = new Status { Running = true, File = f.Path };
        if (Jobs.TryGetValue(space.User.Id, out var old) && old.Running) throw new InvalidOperationException("Es läuft schon ein Import.");
        Jobs[space.User.Id] = status;
        _ = Task.Run(() =>
        {
            // Auch unter Aktivität zu sehen (Kopfleiste), mit Fortschritt je Eintrag
            using var job = Activity.Start(space.User.Id, Activity.JournalImport, detail: f.Name);
            try { Run(space, journal, f.Path, status, job); }
            catch (Exception e)
            {
                status.Error = e is InvalidDataException or JsonException ? "Das ist kein lesbarer Export aus dmOffice, Diarium oder Journey." : e.Message;
                job.Fail(status.Error);
            }
            finally { status.Running = false; status.Finished = DateTime.UtcNow; }
        });
        return status;
    }

    private static void Run(UserSpace space, Collection journal, string path, Status status, Activity.Job job)
    {
        var db = space.Db;
        // ZipArchive braucht einen Strom mit Sprüngen - sonst erst in eine Datei auf dem Server
        using var source = FileStore.OpenRead(db, path);
        Stream zipStream = source;
        string? temp = null;
        if (!source.CanSeek)
        {
            temp = Path.Combine(Path.GetTempPath(), "dmjournal-" + Guid.NewGuid().ToString("N") + ".zip");
            using (var t = File.Create(temp)) source.CopyTo(t);
            zipStream = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.DeleteOnClose);
        }
        using (zipStream)
        using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Read))
        {
            if (zip.GetEntry(JournalExport.JsonName) is { } own) { RunDmOffice(space, journal, zip, own, status, job); return; }
            if (zip.Entries.Any(e => JourneyEntryRx.IsMatch(e.FullName))) { RunJourney(space, journal, zip, status, job); return; }
            var json = zip.Entries.FirstOrDefault(e => !e.FullName.Contains('/') && e.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("Keine Einträge im Export.");
            List<Item> items;
            using (var s = json.Open())
                items = JsonSerializer.Deserialize<List<Item>>(s, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
            status.Total = items.Count;
            var media = zip.Entries.Where(e => e.FullName.StartsWith("media/", StringComparison.Ordinal) && e.Name.Length > 0)
                .GroupBy(e => e.FullName.Split('/')[1]).ToDictionary(g => g.Key, g => g.ToList());
            var known = JournalStore.Entries(db, journal).Select(e => e.Import).Where(x => x != null).ToHashSet();
            foreach (var item in items)
            {
                status.Done++;
                job.Progress(status.Done, status.Total);
                if (!DateTime.TryParse(item.Date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)) { status.Skipped++; continue; }
                local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
                var key = local.ToString("yyyy-MM-dd_HHmmssfff", CultureInfo.InvariantCulture);
                if (known.Contains("diarium:" + key)) { status.Skipped++; continue; }
                var date = Ics.ToUtc(local, space.Zone);
                var attached = new List<JournalMedia>();
                foreach (var m in media.GetValueOrDefault(key) ?? [])
                {
                    using var s = m.Open();
                    var found = JournalMediaStore.Store(space, m.Name, null, s, m.Length, CancellationToken.None).GetAwaiter().GetResult();
                    attached.Add(found.Media.Taken == null ? found.Media with { Taken = date } : found.Media);
                    status.Media++;
                }
                var tags = Names(item.Tags).Concat(Names(item.People)).ToList();
                JournalPlace? place = item.Location is [var lat, var lon] && (lat != 0 || lon != 0) ? new JournalPlace(null, lat, lon) : null;
                JournalStore.Save(db, journal, new JournalInput
                {
                    Journal = journal.Id, Date = date, Title = item.Heading, Html = item.Html ?? "", Tags = tags, Place = place,
                    Weather = item.Weather, Media = attached, Import = "diarium:" + key,
                });
                status.Created++;
            }
        }
    }

    /// <summary>
    /// Eigener Export (JournalExport): tagebuch.json mit allem, Medien an den dort genannten Stellen. Schon vorhanden ist ein Eintrag,
    /// wenn er aus demselben Tagebuch stammt (gleiche Kennung) oder schon einmal importiert wurde (gleiche Herkunft).
    /// </summary>
    private static void RunDmOffice(UserSpace space, Collection journal, ZipArchive zip, ZipArchiveEntry json, Status status, Activity.Job job)
    {
        var db = space.Db;
        JournalExport.Document? doc;
        using (var s = json.Open()) doc = JsonSerializer.Deserialize<JournalExport.Document>(s, JournalExport.Json);
        if (doc?.Format != JournalExport.Format || doc.Entries == null) throw new InvalidDataException("Kein Tagebuch-Export von dmOffice.");
        status.Total = doc.Entries.Count;
        var existing = JournalStore.Entries(db, journal).ToList();
        var known = existing.Select(e => e.Import).OfType<string>().Concat(existing.Select(e => "dmoffice:" + e.Uri)).ToHashSet();
        foreach (var item in doc.Entries)
        {
            status.Done++;
            job.Progress(status.Done, status.Total);
            var key = item.Import ?? "dmoffice:" + item.Uri;
            if (known.Contains(key)) { status.Skipped++; continue; }
            var date = DateTime.SpecifyKind(item.Date, item.Date.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : item.Date.Kind).ToUniversalTime();
            var attached = new List<JournalMedia>();
            foreach (var m in item.Media ?? [])
            {
                if (m.File == null || zip.GetEntry(m.File) is not { } z) continue;
                using var s = z.Open();
                var found = JournalMediaStore.Store(space, m.Name, m.Mime, s, z.Length, CancellationToken.None).GetAwaiter().GetResult();
                attached.Add(found.Media with { Taken = found.Media.Taken ?? m.Taken ?? date });
                status.Media++;
            }
            JournalStore.Save(db, journal, new JournalInput
            {
                Journal = journal.Id, Date = date, Title = item.Title, Html = item.Html ?? "", Mood = item.Mood, Tags = item.Tags ?? [], Place = item.Place,
                Private = item.Private, ShowEvents = item.ShowEvents, Weather = item.Weather, Media = attached, Refs = item.Refs ?? [], Import = key,
            });
            known.Add(key);
            status.Created++;
        }
    }

    /// <summary>Journey: je Eintrag eine JSON-Datei „&lt;Millisekunden&gt;-&lt;hex&gt;.json“, die Fotos daneben in der ZIP.</summary>
    private static readonly System.Text.RegularExpressions.Regex JourneyEntryRx = new(@"^\d{10,}-[0-9a-f]+\.json$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private sealed class JourneyItem
    {
        public string? Id { get; set; }
        public string? Text { get; set; }
        public long DateJournal { get; set; }
        public string? Address { get; set; }
        public double Lat { get; set; }
        public double Lon { get; set; }
        public int Mood { get; set; }
        public string? Label { get; set; }
        public string? Folder { get; set; }
        public JourneyWeather? Weather { get; set; }
        public List<string>? Photos { get; set; }
        public List<JsonElement>? Tags { get; set; }
    }

    private sealed class JourneyWeather
    {
        public double DegreeC { get; set; }
        public string? Description { get; set; }
        public string? Icon { get; set; }
    }

    private static void RunJourney(UserSpace space, Collection journal, ZipArchive zip, Status status, Activity.Job job)
    {
        var db = space.Db;
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
        var files = zip.Entries.Where(e => !e.FullName.Contains('/')).ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
        var entries = zip.Entries.Where(e => JourneyEntryRx.IsMatch(e.FullName)).ToList();
        status.Total = entries.Count;
        var known = JournalStore.Entries(db, journal).Select(e => e.Import).Where(x => x != null).ToHashSet();
        foreach (var entry in entries)
        {
            status.Done++;
            job.Progress(status.Done, status.Total);
            JourneyItem? item;
            using (var s = entry.Open()) item = JsonSerializer.Deserialize<JourneyItem>(s, options);
            if (item == null || item.DateJournal <= 0) { status.Skipped++; continue; }
            var key = "journey:" + (item.Id ?? Path.GetFileNameWithoutExtension(entry.Name));
            if (known.Contains(key)) { status.Skipped++; continue; }
            var date = DateTimeOffset.FromUnixTimeMilliseconds(item.DateJournal).UtcDateTime;
            var attached = new List<JournalMedia>();
            foreach (var name in item.Photos ?? [])
            {
                if (!files.TryGetValue(name, out var m)) continue;
                using var s = m.Open();
                var found = JournalMediaStore.Store(space, m.Name, null, s, m.Length, CancellationToken.None).GetAwaiter().GetResult();
                attached.Add(found.Media.Taken == null ? found.Media with { Taken = date } : found.Media);
                status.Media++;
            }
            // Ohne Ort schreibt Journey die größte Zahl (double.MaxValue) bzw. 0/0
            var hasGeo = Math.Abs(item.Lat) <= 90 && Math.Abs(item.Lon) <= 180 && (item.Lat != 0 || item.Lon != 0);
            var address = string.IsNullOrWhiteSpace(item.Address) ? null : item.Address.Trim();
            JournalPlace? place = hasGeo || address != null ? new JournalPlace(address, hasGeo ? item.Lat : null, hasGeo ? item.Lon : null) : null;
            var tags = Names(item.Tags).Concat(new[] { item.Label, item.Folder }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim())).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            JournalStore.Save(db, journal, new JournalInput
            {
                Journal = journal.Id, Date = date, Html = item.Text ?? "", Tags = tags, Place = place,
                Mood = item.Mood is >= 1 and <= 5 ? item.Mood : null, Weather = JourneyWeatherText(item.Weather), Media = attached, Import = key,
            });
            status.Created++;
        }
    }

    /// <summary>Wetter aus Journey (Symbol wie bei OpenWeather, Temperatur in °C) in derselben Form wie beim Nachschlagen.</summary>
    private static string? JourneyWeatherText(JourneyWeather? w)
    {
        if (w == null) return null;
        var icon = w.Icon is { Length: >= 2 } i ? i[..2] : null;
        var text = icon switch
        {
            "01" => "sonnig", "02" => "heiter", "03" => "bewölkt", "04" => "bedeckt", "09" => "Regenschauer", "10" => "Regen",
            "11" => "Gewitter", "13" => "Schnee", "50" => "Nebel", _ => null,
        };
        var temp = Math.Abs(w.DegreeC) < 100 ? $"{Math.Round(w.DegreeC):0} °C" : null;
        var parts = new[] { text, temp }.Where(x => x != null).ToList();
        return parts.Count > 0 ? string.Join(", ", parts) : null;
    }

    /// <summary>Schlagwörter und Personen: als Text oder als Objekt mit Namen.</summary>
    private static IEnumerable<string> Names(List<JsonElement>? list) =>
        (list ?? []).Select(x => x.ValueKind switch
        {
            JsonValueKind.String => x.GetString(),
            JsonValueKind.Object => x.TryGetProperty("name", out var n) ? n.GetString() : x.TryGetProperty("title", out var t) ? t.GetString() : null,
            _ => null,
        }).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim());
}
