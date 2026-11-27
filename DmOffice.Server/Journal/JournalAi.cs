using System.Globalization;
using System.Net;
using DmOffice.Chat;
using DmOffice.Core;

namespace DmOffice.Journal;

/// <summary>
/// Tagebuch mit KI: einen Eintrag kurz, mittel oder lang ausformulieren lassen. Der Benutzer wählt, was mitgeht (Bewegung,
/// Termine, Ort, Wetter, Schlagzeilen); seine Wahl wird die Vorgabe fürs nächste Mal. Nur mit eingerichteter und für ihn
/// freigegebener KI; ohne Auswahl gehen nur Datum, Stimmung und sein eigener Text hinaus.
/// </summary>
public static class JournalAi
{
    public const string Key = "journal.ai";

    public sealed class Settings
    {
        /// <summary>short, medium, long</summary>
        public string Length { get; set; } = "medium";
        public bool Fitness { get; set; } = true;
        public bool Events { get; set; } = true;
        public bool Place { get; set; } = true;
        public bool Weather { get; set; } = true;
        public bool News { get; set; }
        /// <summary>Fotos des Eintrags mitschicken (Schalter aiphotos)</summary>
        public bool Photos { get; set; } = true;
    }

    public sealed record WriteBody(string Date, string? Length, bool Fitness, bool Events, bool Place, bool Weather, bool News,
        string? PlaceName, string? WeatherText, int? Mood, string? Title, string? Notes, string? Lang, long? Journal = null,
        bool Photos = false, List<string>? PhotoPaths = null);

    private static readonly string[] Moods = ["", "sehr schlecht", "schlecht", "mittel", "gut", "sehr gut"];

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api/journal/ai").RequireAuthorization();

        api.MapGet("", (HttpContext http, ServerConfig config, UserStore users) =>
        {
            var space = http.Space();
            return new
            {
                allowed = PukkiAi.Allowed(config, users.ById(space.User.Id)), search = PukkiAi.CanSearch(config),
                feeds = PukkiBriefing.Of(space.Db).Sources.Count > 0, settings = space.Db.GetJson<Settings>(Key),
            };
        });

        api.MapPost("/write", async (WriteBody body, HttpContext http, ServerConfig config, UserStore users, IHttpClientFactory f, ILoggerFactory logs, CancellationToken ct) =>
        {
            var space = http.Space();
            if (!PukkiAi.Allowed(config, users.ById(space.User.Id))) throw new UnauthorizedAccessException("Für dich ist keine KI freigeschaltet.");
            // Aus dem Tresor geht nichts an eine KI
            if (body.Journal is long j && TresorJournals.Is(j)) throw new ArgumentException("Für Tagebücher im Tresor gibt es keine KI.");
            if (!DateOnly.TryParseExact(body.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                throw new ArgumentException("Ungültiges Datum.");
            var length = body.Length is "short" or "long" ? body.Length : "medium";
            // Auswahl merken - die Vorgabe fürs nächste Mal
            space.Db.SetJson(Key, new Settings { Length = length, Fitness = body.Fitness, Events = body.Events, Place = body.Place, Weather = body.Weather, News = body.News, Photos = body.Photos });

            var de = CultureInfo.GetCultureInfo("de-DE");
            var facts = new List<string> { "Datum: " + date.ToString("dddd, d. MMMM yyyy", de) };
            var sent = new List<string>();
            if (body.Mood is >= 1 and <= 5) facts.Add("Stimmung: " + Moods[body.Mood.Value]);
            if (!string.IsNullOrWhiteSpace(body.Title)) facts.Add("Titel: " + Clip(body.Title, 200));
            if (!string.IsNullOrWhiteSpace(body.Notes)) facts.Add("Was ich schon notiert habe:\n" + Clip(body.Notes, 4000));
            if (body.Events)
            {
                var events = JournalApi.EventsOf(http, date).ToList();
                if (events.Count > 0)
                {
                    facts.Add("Termine: " + string.Join("; ", events.Select(e => (e.AllDay ? "" : e.Time + " ") + e.Title + (e.Location is { Length: > 0 } l ? " (" + l + ")" : ""))));
                    sent.Add("Termine");
                }
            }
            var place = body.Place && !string.IsNullOrWhiteSpace(body.PlaceName) ? Clip(body.PlaceName, 120) : null;
            if (place != null) { facts.Add("Ort: " + place); sent.Add("Ort"); }
            if (body.Weather && !string.IsNullOrWhiteSpace(body.WeatherText)) { facts.Add("Wetter: " + Clip(body.WeatherText, 120)); sent.Add("Wetter"); }
            // Fotos: erst sachlich beschreiben lassen, die Beschreibung geht dann wie die übrigen Angaben in den Text
            if (Features.On(Features.AiPhotos) && body.Photos && body.Journal is long pj && body.PhotoPaths is { Count: > 0 } paths)
            {
                var images = new List<(byte[] Data, string Mime)>();
                foreach (var path in paths.Distinct().Take(MaxPhotos))
                    if (await JournalApi.PhotoForAi(http, pj, path, ct) is { } img) images.Add((img, "image/webp"));
                if (images.Count > 0)
                {
                    var seen = await PukkiAi.See(config,
                        "Das sind Fotos aus meinem Tagebucheintrag. Beschreibe auf Deutsch in höchstens zwei kurzen Sätzen je Foto sachlich, was zu sehen ist " +
                        "(Ort, Landschaft, Dinge, Tätigkeit, Stimmung). Nenne keine Namen und rate nicht, wer abgebildet ist. Nummeriere die Fotos.",
                        images, logs.CreateLogger("JournalAi"), ct, 600);
                    if (!string.IsNullOrWhiteSpace(seen)) { facts.Add("Auf den Fotos des Tages:\n" + Clip(seen, 2500)); sent.Add("Fotos"); }
                }
            }

            var search = PukkiAi.CanSearch(config);
            var newsTask = "";
            if (body.News)
            {
                // Schlagzeilen aus den eigenen Quellen (Morgenüberblick), sonst sucht die KI selbst, wenn sie darf
                var b = PukkiBriefing.Of(space.Db);
                var heads = new List<PukkiBriefing.Headline>();
                if (b.Sources.Count > 0)
                {
                    var client = f.CreateClient();
                    var feeds = await Task.WhenAll(b.Sources.Select(x => PukkiBriefing.Presets.FirstOrDefault(p => p.Id == x) is { } p
                        ? PukkiBriefing.FeedAsync(client, p.Url, p.Name, ct) : PukkiBriefing.FeedAsync(client, x, new Uri(x).Host.Replace("www.", ""), ct)));
                    // Nur Meldungen von diesem Tag (die Feeds reichen meist nur ein, zwei Tage zurück)
                    var day = feeds.Select(list => list.Where(h => h.At is DateTime at && DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(at.ToUniversalTime(), space.Zone)) == date).ToList()).ToList();
                    heads = PukkiBriefing.Pick(day, new PukkiBriefing.Settings { Count = 3, Prefer = b.Prefer, Exclude = b.Exclude });
                }
                if (heads.Count > 0) { facts.Add("Schlagzeilen des Tages: " + string.Join("; ", heads.Select(h => h.Title + " (" + h.Source + ")"))); sent.Add("Schlagzeilen"); }
                else if (search) { newsTask += $" Suche die drei wichtigsten Schlagzeilen aus Deutschland vom {date.ToString("d. MMMM yyyy", de)}."; sent.Add("Schlagzeilen (Suche)"); }
                if (place != null && search) { newsTask += $" Suche außerdem eine Meldung aus der Gegend um {place} von diesem Tag; findest du keine, lass sie weg."; sent.Add("Meldung aus der Nähe (Suche)"); }
            }

            var words = length switch { "short" => "zwei bis drei Sätze", "long" => "etwa 250 Wörter in drei bis vier Absätzen", _ => "etwa 100 Wörter in ein bis zwei Absätzen" };
            var english = body.Lang == "en";
            var system =
                "Du schreibst einen persönlichen Tagebucheintrag in der Ich-Form für den Benutzer. " +
                (english ? "Write in English. " : "Schreib auf Deutsch. ") +
                $"Länge: {words}. Nutze nur die Angaben unten - erfinde keine Erlebnisse, Personen oder Gefühle; die Stimmung darfst du aufgreifen. " +
                "Was der Benutzer schon notiert hat, ist der Kern: behalte seine Aussagen, ergänze sie nur mit den übrigen Angaben. " +
                "Zahlen (Schritte, Schlaf) beiläufig und gerundet, keine Aufzählung. Was auf den Fotos zu sehen ist, darfst du aufgreifen, ohne die Fotos aufzuzählen. Schlagzeilen nur als kurze Randbemerkung am Ende. " +
                "Gib nur den Text aus: keine Überschrift, kein Markdown, keine Anführungszeichen, Absätze durch eine Leerzeile getrennt." +
                (newsTask.Length > 0 ? " " + newsTask.Trim() + " Nenne keine Quellenlinks." : "");
            var text = await PukkiAi.Write(config, system, string.Join("\n", facts), logs.CreateLogger("JournalAi"), ct)
                ?? throw new InvalidOperationException("Die KI hat gerade nicht geantwortet. Bitte noch einmal versuchen.");
            var html = string.Concat(text.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
                .Select(p => "<p>" + WebUtility.HtmlEncode(p.Trim()).Replace("\n", "<br>") + "</p>"));
            return new { html, sent };
        });
    }

    /// <summary>Höchstens so viele Fotos gehen an die KI</summary>
    public const int MaxPhotos = 6;

    private static string Clip(string s, int max) { s = s.Trim(); return s.Length > max ? s[..max] : s; }
}
