using System.Globalization;
using DmOffice.Core;

namespace DmOffice.Coach;

/// <summary>Coach (docs/dmcoach-konzept.md, Stufe 1): Profil, Tag, Lebensmittel, Tagebuch, Wasser, Körper, Aktivität, Statistik.</summary>
public static class CoachApi
{
    /// <param name="Remember">Freitext/Rezept: diese Zuordnung Wort → Lebensmittel merken (der Benutzer hat gewählt)</param>
    public record DiaryBody(string? Date, string? Meal, string? Food, double? Amount, string? Portion, string? Name, Nutrients? Quick, string? Remember = null);
    public record ParseBody(string Text, string? Meal);

    /// <summary>Anweisung für die Foto-Erkennung: nur eine Liste im Format, das das Beschreiben versteht.</summary>
    private const string PhotoPrompt =
        "Auf dem Foto ist eine Mahlzeit. Nenne die erkennbaren Lebensmittel und Getränke mit geschätzter Menge, auf Deutsch, durch " +
        "Kommas getrennt, jeweils „Menge Einheit Lebensmittel“ (Gramm, Milliliter oder Stück), z. B. „200 g Spaghetti, 150 g Tomatensoße, " +
        "20 g Parmesan“. Schreibe nur diese Liste, ohne Einleitung und ohne Erklärung. Ist kein Essen zu sehen, schreibe nur: KEIN ESSEN";
    public record MapBody(string Name, string Food);
    public record TemplateBody(string? Name, string? Date, string? Meal);
    public record ApplyBody(string? Date, string? Meal);
    public record CopyBody(string From, string? To, string? Meal, string? ToMeal, long[]? Ids = null);
    public record ServingsBody(int Servings);
    public record DiaryChange(double? Amount, string? Meal, string? Portion);
    public record WaterBody(string? Date, int Ml);
    public record BodyBody(string? Date, double? Weight, double? Fat);
    public record ActivityDay(string Date, int? Steps, double? ActiveKcal, double? Distance, int? SleepMin, int? RestingHr, double? Weight, double? BodyFat);
    public record ActivityBody(string? Source, List<ActivityDay> Days);
    public record LocationBody(double Lat, double Lon);
    public record GarminBody(string? Key, int? ValidDays);
    public record ForAllBody(bool On);

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api/coach").RequireAuthorization();

        api.MapGet("/profile", (HttpContext http) =>
        {
            var space = http.Space();
            var today = CoachStore.Today(space);
            var p = CoachStore.Profile(space.Db);
            return new { profile = p, targets = CoachTargetsCalc.Compute(p, CoachStore.Weight(space.Db, today), today), sources = Sources };
        });

        api.MapPut("/profile", (CoachProfile body, HttpContext http) =>
        {
            var space = http.Space();
            Validate(body);
            CoachStore.SaveProfile(space.Db, body);
            var today = CoachStore.Today(space);
            return new { profile = body, targets = CoachTargetsCalc.Compute(body, CoachStore.Weight(space.Db, today), today), sources = Sources };
        });

        // Ein Tag: Ziele (mit Bewegung von der Uhr), Summen, Mahlzeiten, Wasser, Schritte, Gewicht
        api.MapGet("/day", (string? date, HttpContext http) => Day(http.Space(), ParseDay(http.Space(), date)));

        // ---------------- Lebensmittel ----------------

        api.MapGet("/foods/search", (string q, string? meal, HttpContext http, CoachFoods foods) =>
        {
            var space = http.Space();
            var today = CoachStore.Today(space);
            var query = Bls.Words(Bls.Fold(q));
            // Zuerst, was man selbst schon gegessen hat, dann eigene des Haushalts, gemerkte Produkte, BLS
            var mine = query.Length == 0 ? [] : CoachStore.RecentFoods(space.Db, meal, today, 200)
                .Where(r => Bls.Score(query, Bls.Fold(r.Name + " " + r.Brand), Bls.Words(Bls.Fold(r.Name + " " + r.Brand))) > 0).Take(8)
                .Select(r => new Hit(r.Food, r.Name, r.Brand, Source(r.Food), r.Kcal * 100 / Math.Max(r.Amount, 1), r.Amount, r.Portion, true)).ToList();
            var seen = mine.Select(m => m.Id).ToHashSet();
            var hits = new List<Hit>(mine);
            var recipes = query.Length == 0 ? [] : DmOffice.Notes.NotesDb.Of(space).GetAllActive()
                .Where(n => DmOffice.Notes.Recipes.IsRecipe(n.Tags, n.Plain))
                .Select(n => (n, s: Bls.Score(query, Bls.Fold(n.DisplayTitle), Bls.Words(Bls.Fold(n.DisplayTitle))))).Where(x => x.s > 0)
                .OrderByDescending(x => x.s).Take(3).Select(x => CoachRecipes.AsFood(space, foods, x.n.SyncId)).OfType<Food>();
            // Richtwerte (übliche Portionen, Imbiss, Gerichte) vor dem BLS
            foreach (var f in recipes.Concat(foods.SearchOwn(q)).Concat(foods.SearchCached(q)).Concat(Richtwerte.Search(q, 8).Select(Richtwerte.ToFood)).Concat(Bls.Search(q, 40).Select(Bls.ToFood)))
                if (seen.Add(f.Id)) hits.Add(Hit.Of(f));
            return WithImages(hits.Take(50), foods);
        });

        // Markenprodukte bei Open Food Facts nach Namen - eigener Aufruf, damit die eigenen Treffer nicht warten (limited: gerade zu viele Suchen)
        api.MapGet("/foods/search/off", async (string q, CoachFoods foods, IHttpClientFactory http, CancellationToken ct) =>
        {
            var found = await OpenFoodFacts.SearchAsync(foods, http, q, ct);
            return new { hits = WithImages((found ?? []).Select(Hit.Of), foods), limited = found == null };
        });

        // Produktbild (klein, WebP): eigenes hochgeladenes oder einmal von Open Food Facts geholt und gemerkt
        api.MapGet("/foods/{id}/image", async (string id, HttpContext http, CoachFoods foods, IHttpClientFactory f, CancellationToken ct) =>
        {
            var img = foods.Image(id);
            var data = img?.Data;
            if (data == null && img?.Url is { } url)
            {
                data = await OpenFoodFacts.FetchImageAsync(f, url, ct);
                if (data != null) foods.SetImage(id, data);
            }
            if (data == null) return Results.NotFound();
            http.Response.Headers.CacheControl = "private, max-age=604800";
            return Results.File(data, "image/webp");
        });
        api.MapPost("/foods/{id}/image", async (string id, HttpRequest request, CoachFoods foods) =>
        {
            if (!id.StartsWith("own:", StringComparison.Ordinal) || foods.Own(OwnId(id)) == null) throw new KeyNotFoundException("Lebensmittel nicht gefunden.");
            if (!request.HasFormContentType) throw new ArgumentException("Bitte ein Bild auswählen.");
            var file = (await request.ReadFormAsync()).Files.FirstOrDefault() ?? throw new ArgumentException("Bitte ein Bild auswählen.");
            if (file.Length > 20_000_000) throw new ArgumentException("Das Bild ist zu groß (höchstens 20 MB).");
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            var data = DmOffice.Dav.Thumbs.FromBytes(ms.ToArray(), OpenFoodFacts.ImageSize) ?? throw new ArgumentException("Das ist kein lesbares Bild.");
            foods.SetImage(id, data);
            return Results.Ok(new { image = ImagePath(id) });
        });
        api.MapDelete("/foods/{id}/image", (string id, CoachFoods foods) =>
        {
            if (!id.StartsWith("own:", StringComparison.Ordinal)) throw new KeyNotFoundException("Lebensmittel nicht gefunden.");
            foods.SetImage(id, null);
            return Results.NoContent();
        });

        // Vorschläge: was man zu dieser Mahlzeit, an diesem Wochentag, um diese Uhrzeit meist isst (gelernt aus dem Verlauf)
        api.MapGet("/foods/recent", (string? meal, HttpContext http, CoachFoods foods) =>
        {
            var space = http.Space();
            var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, space.Zone);
            return WithImages(CoachSuggest.Suggest(space.Db, meal, CoachStore.Today(space), now)
                .Select(r => new Hit(r.Food, r.Name, r.Brand, Source(r.Food), r.Kcal * 100 / Math.Max(r.Amount, 1), r.Amount, r.Portion, true)), foods);
        });

        // Eine Aktivität von der Uhr: Strecke (von Garmin, gemerkt) und die Fotos, die währenddessen aufgenommen wurden
        api.MapGet("/activities/{id:long}", async (long id, HttpContext http, ServerConfig config, IHttpClientFactory f, CancellationToken ct) =>
        {
            var space = http.Space();
            var a = CoachGarmin.ActivityById(space.Db, id) ?? throw new KeyNotFoundException("Aktivität nicht gefunden.");
            List<double[]> track = [];
            string? trackError = null;
            try { track = await CoachGarmin.TrackAsync(space, f.CreateClient(), id, ct); }
            catch (Exception e) when (e is CoachGarmin.GarminException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            { trackError = e is CoachGarmin.GarminException ? e.Message : "Garmin ist gerade nicht erreichbar."; }
            // Zeitraum in UTC (Garmin nennt die Ortszeit), mit zehn Minuten Puffer davor und danach
            var photos = CoachGeotag.PhotosDuring(space, config, a);
            // Strecke ohne Zeiten (die braucht nur das Verorten); unlocated: Fotos ohne bekannten Ort - Anlass für „Fotos verorten“
            return new { id = a.Id, a.Name, a.Type, a.Start, durationS = a.DurationS, distanceM = a.DistanceM, kcal = a.Kcal, track = track.Select(p => p[..2]), trackError, photos,
                unlocated = photos.Count(p => p.Lat == null) };
        });

        // Fotos ohne Ort anhand der Strecke verorten: Vorschau (offset: Versatz der Kamera-Uhr in Sekunden), Schreiben im Hintergrund, Stand
        api.MapGet("/activities/{id:long}/geotag", async (long id, double? offset, HttpContext http, ServerConfig config, IHttpClientFactory f, CancellationToken ct) =>
        {
            var space = http.Space();
            var a = CoachGarmin.ActivityById(space.Db, id) ?? throw new KeyNotFoundException("Aktivität nicht gefunden.");
            var (track, trackError) = await TimedTrackAsync(space, f, id, ct);
            return await CoachGeotag.PreviewAsync(space, config, a, track, trackError, Math.Clamp(offset ?? 0, -86400, 86400), ct);
        });
        api.MapPost("/activities/{id:long}/geotag", async (long id, GeotagBody body, HttpContext http, ServerConfig config, IHttpClientFactory f, CancellationToken ct) =>
        {
            var space = http.Space();
            var a = CoachGarmin.ActivityById(space.Db, id) ?? throw new KeyNotFoundException("Aktivität nicht gefunden.");
            var (track, _) = await TimedTrackAsync(space, f, id, ct);
            // Stellen neu berechnen - es zählt, was der Server sieht, nicht was die Oberfläche schickt
            var preview = await CoachGeotag.PreviewAsync(space, config, a, track, null, Math.Clamp(body.Offset, -86400, 86400), ct);
            var want = (body.Paths ?? []).ToHashSet(StringComparer.Ordinal);
            var items = preview.Items.Where(x => x.State == "ok" && want.Contains(x.Path)).ToList();
            if (items.Count == 0) throw new ArgumentException("Keine Fotos zum Verorten.");
            if (!CoachGeotag.Start(space, config, id, items)) return Results.Conflict(new { error = "Für diese Aktivität werden gerade Fotos verortet." });
            return Results.Ok(new { started = items.Count });
        });
        api.MapGet("/activities/{id:long}/geotag/state", (long id, HttpContext http) =>
            CoachGeotag.StateOf(http.Space(), id) is { } s
                ? Results.Ok(new { running = s.Running, total = s.Total, done = s.Done, failed = s.Failed.ToList() })
                : Results.Ok(new { running = false, total = 0, done = 0, failed = new List<CoachGeotag.Failure>() }));

        // Häufig: am öftesten eingetragen, gleich zu welcher Mahlzeit
        api.MapGet("/foods/frequent", (HttpContext http, CoachFoods foods) =>
        {
            var space = http.Space();
            return WithImages(CoachSuggest.Frequent(space.Db, CoachStore.Today(space))
                .Select(r => new Hit(r.Food, r.Name, r.Brand, Source(r.Food), r.Kcal * 100 / Math.Max(r.Amount, 1), r.Amount, r.Portion, true)), foods);
        });

        // Foto vom Teller: die KI des Servers (wenn für einen freigeschaltet) nennt Lebensmittel und Mengen, danach wie beim Beschreiben
        api.MapGet("/photo", (HttpContext http, ServerConfig config, UserStore users) => new { available = DmOffice.Chat.PukkiAi.Allowed(config, users.ById(http.Space().User.Id)) });
        api.MapPut("/photo", async (HttpContext http, ServerConfig config, UserStore users, CoachFoods foods, ILogger<CoachFoods> log, CancellationToken ct) =>
        {
            var space = http.Space();
            if (!DmOffice.Chat.PukkiAi.Allowed(config, users.ById(space.User.Id))) throw new UnauthorizedAccessException("Für dich ist keine KI freigeschaltet.");
            using var ms = new MemoryStream();
            await http.Request.Body.CopyToAsync(ms, ct);
            if (ms.Length == 0 || ms.Length > 15 * 1024 * 1024) throw new ArgumentException("Bitte ein Foto (höchstens 15 MB) schicken.");
            // Verkleinert (1024 px reichen zum Erkennen) - weniger zu übertragen; gespeichert wird das Foto nicht
            var small = DmOffice.Dav.Thumbs.FromBytes(ms.ToArray(), 1024) ?? throw new ArgumentException("Das Foto ließ sich nicht lesen.");
            var text = await DmOffice.Chat.PukkiAi.See(config, PhotoPrompt, small, "image/webp", log, ct);
            if (text == null) throw new InvalidOperationException("Die KI konnte das Foto gerade nicht auswerten. Bei einem eigenen KI-Dienst braucht es ein Modell, das Bilder versteht.");
            if (text.Contains("KEIN ESSEN", StringComparison.OrdinalIgnoreCase)) return Results.Ok(new { text = "", items = new List<CoachSuggest.Proposal>() });
            if (text.Length > 500) text = text[..500];
            return Results.Ok(new { text, items = CoachSuggest.Parse(space, foods, text, CoachStore.Today(space)) });
        });

        // Passt es zu Allergien und Unverträglichkeiten im Profil? (Open-Food-Facts-Allergene, sonst Wörter in Name und Zutaten)
        api.MapGet("/foods/{id}/check", (string id, HttpContext http, CoachFoods foods) =>
        {
            var space = http.Space();
            var f = Resolve(space, foods, id) ?? throw new KeyNotFoundException("Lebensmittel nicht gefunden.");
            var warnings = CoachPlan.Check(f.Name, f.Info?.Ingredients, f.Info?.Allergens ?? [], CoachStore.Profile(space.Db));
            return new { warnings, ingredients = f.Info?.Ingredients };
        });
        api.MapGet("/foods/{id}", (string id, HttpContext http, CoachFoods foods) => Resolve(http.Space(), foods, id) ?? throw new KeyNotFoundException("Lebensmittel nicht gefunden."));

        // Laut Essensplan: was zu dieser Mahlzeit im Essensplan steht (mit Rezept: als Lebensmittel mit Nährwerten je Portion)
        api.MapGet("/planned", (string? date, string meal, HttpContext http, CoachFoods foods) =>
            CoachRecipes.PlannedFor(http.Space(), foods, ParseDay(http.Space(), date), meal));

        // Freitext („2 Brötchen mit Käse und Kaffee“) → Vorschläge je Posten zum Bestätigen
        api.MapPost("/parse", (ParseBody body, HttpContext http, CoachFoods foods) =>
        {
            var space = http.Space();
            if (string.IsNullOrWhiteSpace(body.Text) || body.Text.Length > 500) throw new ArgumentException("Bitte kurz beschreiben, was du gegessen hast (höchstens 500 Zeichen).");
            return CoachSuggest.Parse(space, foods, body.Text, CoachStore.Today(space));
        });
        api.MapPut("/foodmap", (MapBody body, HttpContext http, CoachFoods foods) =>
        {
            var space = http.Space();
            if (Resolve(space, foods, body.Food) == null) throw new KeyNotFoundException("Lebensmittel nicht gefunden.");
            CoachSuggest.Remember(space.Db, body.Name, body.Food);
            return Results.NoContent();
        });

        // Vorlagen und Kopieren
        api.MapGet("/templates", (HttpContext http) => CoachSuggest.Templates(http.Space().Db));
        api.MapPost("/templates", (TemplateBody body, HttpContext http) =>
            CoachSuggest.SaveTemplate(http.Space().Db, body.Name, ParseDay(http.Space(), body.Date), body.Meal ?? "breakfast"));
        api.MapPost("/templates/{id}/apply", (string id, ApplyBody body, HttpContext http) =>
            new { added = CoachSuggest.ApplyTemplate(http.Space().Db, id, ParseDay(http.Space(), body.Date), body.Meal) });
        api.MapDelete("/templates/{id}", (string id, HttpContext http) => { CoachSuggest.DeleteTemplate(http.Space().Db, id); return Results.NoContent(); });
        api.MapPost("/diary/copy", (CopyBody body, HttpContext http) =>
        {
            var s = http.Space();
            return new { added = CoachSuggest.Copy(s.Db, ParseDay(s, body.From), ParseDay(s, body.To), body.Meal == null ? null : CoachStore.Meal(body.Meal),
                body.ToMeal == null ? null : CoachStore.Meal(body.ToMeal), body.Ids) };
        });

        // Rezepte: Nährwerte aus den Zutaten, Portionen einstellbar
        // Rezepte als Karten: Bild (erstes Bild der Notiz), Ordner, kcal je Portion (gemerkt, bis sich die Notiz ändert)
        api.MapGet("/recipes", (HttpContext http, CoachFoods foods) =>
        {
            var space = http.Space();
            var notes = DmOffice.Notes.NotesDb.Of(space);
            var folders = space.Db.Query("SELECT sync_id, parent, name, sort_order FROM note_folders",
                r => new DmOffice.Notes.NoteFolder { SyncId = r.GetString(0), Parent = r.IsDBNull(1) ? null : r.GetString(1), Name = r.GetString(2), SortOrder = r.GetDouble(3) });
            var profile = CoachStore.Profile(space.Db);
            return DmOffice.Notes.Recipes.All(notes, folders).Select(e =>
            {
                var note = notes.BySyncId(e.SyncId);
                var image = note == null ? null : FirstBlob.Match(note.Html) is { Success: true } m ? m.Groups[1].Value : null;
                var key = space.User.Id + "|" + e.SyncId + "|" + note?.Modified.Ticks;
                var (kcal, servings, protein) = RecipeCards.GetOrAdd(key, _ => CoachRecipes.Compute(space, foods, e.SyncId) is { } r && r.Weight > 0
                    ? (Math.Round(r.PerServing.Kcal), r.Servings, Math.Round(r.PerServing.Protein, 1)) : ((double?)null, (int?)null, (double?)null));
                // Kalorien je Portion laut Rezept vor dem Wert aus den Zutaten; ohne Angabe schlägt der Essensplan das Rezept nicht vor
                var given = note == null ? null : DmOffice.Notes.Recipes.Kcal(note.Plain);
                if (given != null) kcal = given;
                // Ernährungsformen und ob es zum eigenen Profil passt (Allergien, Unverträglichkeiten, Abneigungen, Ernährungsform)
                var candidate = new CoachPlan.Candidate(e.SyncId, e.Title, note == null ? [] : DmOffice.Notes.Recipes.Ingredients(note));
                var diets = CoachPlan.Diets(candidate, kcal, protein);
                var excluded = CoachPlan.Excluded(candidate, profile);
                return (object)new { e.SyncId, e.Title, e.Folder, image, kcal, kcalGiven = given != null, servings, protein, diets, excluded, @base = false, meal = (string?)null };
            })
            // Basisrezepte aus der Auslieferung (je Ernährungsform und Mahlzeit zehn)
            .Concat(BaseRecipes.All.Select(b => (object)new
            {
                syncId = b.Id, b.Title, folder = BaseRecipes.Folder(b), image = b.Image, kcal = (double?)b.PerServing.Kcal, kcalGiven = true, servings = (int?)1,
                protein = (double?)b.PerServing.Protein, diets = BaseRecipes.Diets(b), excluded = CoachPlan.Excluded(BaseRecipes.Candidate(b), profile), @base = true, b.Meal,
            })).ToList();
        });
        // Basisrezept als eigene Rezept-Notiz übernehmen
        api.MapPost("/recipes/{syncId}/adopt", (string syncId, HttpContext http, DmOffice.Notes.NotesSync sync) =>
        {
            var b = BaseRecipes.ById(syncId) ?? throw new KeyNotFoundException("Rezept nicht gefunden.");
            var space = http.Space();
            var notes = DmOffice.Notes.NotesDb.Of(space);
            var id = BaseRecipes.Adopt(notes, b, null);
            sync.Kick(space.User.Id, TimeSpan.FromSeconds(10));
            return Results.Ok(new { id, syncId = notes.GetNote(id)?.SyncId });
        });
        // Foto eines Basisrezepts (gehört zur Auslieferung, ändert sich nur mit einer neuen Version)
        api.MapGet("/recipes/{syncId}/image", (string syncId, HttpContext http) =>
        {
            if (BaseRecipes.Image(syncId) is not { } bytes) return Results.NotFound();
            http.Response.Headers.CacheControl = "private, max-age=86400";
            return Results.Bytes(bytes, "image/webp");
        });
        api.MapGet("/recipes/{syncId}", (string syncId, HttpContext http, CoachFoods foods) =>
            CoachRecipes.Compute(http.Space(), foods, syncId) ?? throw new KeyNotFoundException("Rezept nicht gefunden."));
        api.MapPut("/recipes/{syncId}/servings", (string syncId, ServingsBody body, HttpContext http, CoachFoods foods) =>
        {
            if (body.Servings is < 1 or > 50) throw new ArgumentException("Portionen: 1 bis 50.");
            http.Space().Db.Set(CoachRecipes.ServingsKey(syncId), body.Servings.ToString(CultureInfo.InvariantCulture));
            return CoachRecipes.Compute(http.Space(), foods, syncId) ?? throw new KeyNotFoundException("Rezept nicht gefunden.");
        });

        // Barcode: eigenes des Haushalts, sonst Open Food Facts (gemerkt); unbekannt: 404 mit unknown, damit die App „anlegen“ anbietet
        api.MapGet("/foods/barcode/{code}", async (string code, CoachFoods foods, IHttpClientFactory http, CancellationToken ct) =>
        {
            var food = await OpenFoodFacts.LookupAsync(foods, http, code, ct);
            return food != null ? Results.Ok(food) : Results.Json(new { unknown = true, barcode = code, error = "Dieses Produkt ist noch nicht bekannt." }, statusCode: 404);
        });

        api.MapPost("/foods", (CoachFoods.OwnInput body, HttpContext http, CoachFoods foods) => foods.SaveOwn(null, body, http.Space().User.Id));
        api.MapPut("/foods/{id}", (string id, CoachFoods.OwnInput body, HttpContext http, CoachFoods foods) =>
            foods.SaveOwn(OwnId(id), body, http.Space().User.Id));
        api.MapDelete("/foods/{id}", (string id, CoachFoods foods) => { foods.DeleteOwn(OwnId(id)); return Results.NoContent(); });

        // ---------------- Tagebuch ----------------

        api.MapPost("/diary", (DiaryBody body, HttpContext http, CoachFoods foods) =>
        {
            var space = http.Space();
            var day = ParseDay(space, body.Date);
            if (body.Quick is { } quick) return CoachStore.AddQuick(space.Db, day, body.Meal ?? "snack", body.Name, quick);
            var food = Resolve(space, foods, body.Food ?? "") ?? throw new KeyNotFoundException("Lebensmittel nicht gefunden.");
            if (!string.IsNullOrWhiteSpace(body.Remember)) CoachSuggest.Remember(space.Db, body.Remember, food.Id);
            // Ohne Menge (schnelles „+“ in der Suche): die übliche Portion, sonst 100 g
            var (amount, portion) = body.Amount is double a ? (a, body.Portion) : Usual(food);
            return CoachStore.Add(space.Db, day, body.Meal ?? "snack", food, amount, portion);
        });
        api.MapPut("/diary/{id:long}", (long id, DiaryChange body, HttpContext http) => CoachStore.Update(http.Space().Db, id, body.Amount, body.Meal, body.Portion));
        api.MapDelete("/diary/{id:long}", (long id, HttpContext http) => { CoachStore.Delete(http.Space().Db, id); return Results.NoContent(); });

        // ---------------- Wasser, Körper, Aktivität ----------------

        api.MapPost("/water", (WaterBody body, HttpContext http) => { var s = http.Space(); return CoachStore.AddWater(s.Db, ParseDay(s, body.Date), body.Ml); });
        api.MapDelete("/water/{id:long}", (long id, HttpContext http) => { CoachStore.DeleteWater(http.Space().Db, id); return Results.NoContent(); });

        api.MapPut("/body", (BodyBody body, HttpContext http) =>
        {
            var s = http.Space();
            CoachStore.SetWeight(s.Db, ParseDay(s, body.Date), body.Weight, body.Fat);
            return Results.NoContent();
        });

        // Von der App (Health Connect) bzw. später vom Garmin-Abruf: Tageswerte, auch rückwirkend
        api.MapPost("/activity", (ActivityBody body, HttpContext http) =>
        {
            var s = http.Space();
            var source = string.IsNullOrWhiteSpace(body.Source) ? "app" : body.Source.Trim()[..Math.Min(body.Source.Trim().Length, 40)];
            var today = CoachStore.Today(s);
            foreach (var d in (body.Days ?? []).Take(400))
            {
                var day = ParseDay(s, d.Date);
                if (day > today.AddDays(1)) continue;
                CoachStore.SetActivity(s.Db, new CoachStore.Activity(CoachStore.Day(day), d.Steps is >= 0 and < 200000 ? d.Steps : null,
                    d.ActiveKcal is >= 0 and < 20000 ? d.ActiveKcal : null, d.Distance is >= 0 ? d.Distance : null,
                    d.SleepMin is >= 0 and <= 1440 ? d.SleepMin : null, d.RestingHr is >= 20 and <= 250 ? d.RestingHr : null, source));
                if (d.Weight is >= 20 and <= 400 || d.BodyFat is >= 2 and <= 75)
                    CoachStore.SetWeight(s.Db, day, d.Weight is >= 20 and <= 400 ? d.Weight : null, d.BodyFat is >= 2 and <= 75 ? d.BodyFat : null, source);
            }
            return Results.NoContent();
        });

        // Garmin Personal API: eigener Schlüssel, Abholen, Verbindung testen (zuerst nur Admins)
        object GarminState(UserSpace s, ServerConfig config)
        {
            var g = CoachGarmin.Settings(s.Db);
            return new
            {
                allowed = CoachGarmin.Allowed(s, config), connected = !string.IsNullOrWhiteSpace(g.Key), expires = g.Expires,
                lastSync = g.LastSync is long l ? DateTimeOffset.FromUnixTimeSeconds(l) : (DateTimeOffset?)null, lastError = g.LastError,
                admin = s.User.IsAdmin, forAll = config.Current.CoachGarminForAll,
            };
        }
        api.MapGet("/garmin", (HttpContext http, ServerConfig config) => GarminState(http.Space(), config));
        api.MapPut("/garmin", async (GarminBody body, HttpContext http, ServerConfig config, IHttpClientFactory f, CancellationToken ct) =>
        {
            var s = http.Space();
            if (!CoachGarmin.Allowed(s, config)) throw new UnauthorizedAccessException("Die Garmin-Verbindung hat der Admin für dich noch nicht freigeschaltet.");
            var key = (body.Key ?? "").Trim();
            if (key.Length is < 10 or > 4000) throw new ArgumentException("Bitte den Schlüssel aus Garmin Connect vollständig einfügen.");
            // Gleich prüfen: nimmt Garmin ihn an?
            await CoachGarmin.TokenAsync(f.CreateClient(), key, ct);
            var days = body.ValidDays is 30 or 90 or 180 or 365 ? body.ValidDays.Value : 365;
            CoachGarmin.Save(s.Db, new GarminSettings { Key = key, Expires = CoachStore.Today(s).AddDays(days).ToString("yyyy-MM-dd") });
            // Die letzten 30 Tage gleich im Hintergrund holen
            var chat = http.RequestServices.GetRequiredService<DmOffice.Chat.ChatStore>();
            _ = Task.Run(() => CoachGarmin.RunAsync(s, chat, f.CreateClient(), config, CancellationToken.None));
            return GarminState(s, config);
        });
        api.MapDelete("/garmin", (HttpContext http, ServerConfig config) =>
        {
            CoachGarmin.Save(http.Space().Db, new GarminSettings());
            return GarminState(http.Space(), config);
        });
        // Verbindung testen: die letzten drei Tage holen und eintragen, zeigen, was erkannt wurde
        api.MapPost("/garmin/sync", async (HttpContext http, ServerConfig config, IHttpClientFactory f, CancellationToken ct) =>
        {
            var s = http.Space();
            if (!CoachGarmin.Allowed(s, config)) throw new UnauthorizedAccessException("Die Garmin-Verbindung ist für dich nicht freigeschaltet.");
            var today = CoachStore.Today(s);
            try
            {
                var r = await CoachGarmin.SyncAsync(s, f.CreateClient(), today.AddDays(-2), today, ct);
                return Results.Ok(new { r.Days, r.Sources, values = r.Values.OrderByDescending(v => v.Day).ToList(), state = GarminState(s, config) });
            }
            catch (CoachGarmin.GarminException e)
            {
                var g = CoachGarmin.Settings(s.Db);
                g.LastError = e.Message;
                CoachGarmin.Save(s.Db, g);
                throw;
            }
        });
        // Rohantworten eines Tages (eigene Daten) - falls Garmin Felder anders nennt
        api.MapGet("/garmin/raw", async (string? date, HttpContext http, ServerConfig config, IHttpClientFactory f, CancellationToken ct) =>
        {
            var s = http.Space();
            if (!CoachGarmin.Allowed(s, config)) throw new UnauthorizedAccessException("Die Garmin-Verbindung ist für dich nicht freigeschaltet.");
            return await CoachGarmin.RawAsync(s, f.CreateClient(), ParseDay(s, date ?? CoachStore.Day(CoachStore.Today(s).AddDays(-1))), ct);
        });
        api.MapPut("/garmin/forall", (ForAllBody body, HttpContext http, ServerConfig config) =>
        {
            var s = http.Space();
            if (!http.User.IsInRole("admin")) throw new UnauthorizedAccessException("Das darf nur der Admin.");
            config.Update(c => c.CoachGarminForAll = body.On);
            return GarminState(s, config);
        });

        // Standort aus der App (wenn freigegeben) - nur für das Wetter bei Pukkis Bewegungs-Erinnerung, gilt sechs Stunden
        api.MapPut("/location", (LocationBody body, HttpContext http) =>
        {
            if (body.Lat is < -90 or > 90 || body.Lon is < -180 or > 180) throw new ArgumentException("Ungültiger Ort.");
            // Auf etwa einen Kilometer gerundet - fürs Wetter reicht das
            http.Space().Db.SetJson(CoachNudges.LocationKey, new CoachLocation { Lat = Math.Round(body.Lat, 2), Lon = Math.Round(body.Lon, 2), At = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
            return Results.NoContent();
        });
        api.MapDelete("/location", (HttpContext http) => { http.Space().Db.Set(CoachNudges.LocationKey, null); return Results.NoContent(); });

        // Statistik: je Tag Summen, Wasser, Schritte samt Ziel, Gewicht, Schlaf (höchstens ein Jahr)
        api.MapGet("/stats", (string? from, string? to, int? days, HttpContext http) =>
        {
            var s = http.Space();
            var end = to != null ? ParseDay(s, to) : CoachStore.Today(s);
            var start = from != null ? ParseDay(s, from) : end.AddDays(-Math.Clamp(days ?? 30, 1, 366) + 1);
            if (start > end || end.DayNumber - start.DayNumber > 366) throw new ArgumentException("Der Zeitraum passt nicht (höchstens ein Jahr).");
            var p = CoachStore.Profile(s.Db);
            return new { days = CoachStore.Stats(s.Db, start, end), targets = CoachTargetsCalc.Compute(p, CoachStore.Weight(s.Db, end), end) };
        });

        api.MapDelete("/data", (HttpContext http) => { CoachStore.DeleteAll(http.Space().Db); return Results.NoContent(); });
    }

    /// <summary>Quellenangaben (Lizenzpflicht): im Coach sichtbar.</summary>
    public static readonly string[] Sources = [Bls.Attribution, OpenFoodFacts.Attribution];

    public sealed record GeotagBody(double Offset, List<string>? Paths);

    /// <summary>Strecke mit Zeiten (eine alte, gemerkte ohne Zeiten wird noch einmal geholt); Fehler von Garmin als Text.</summary>
    private static async Task<(List<double[]> Track, string? Error)> TimedTrackAsync(UserSpace space, IHttpClientFactory f, long id, CancellationToken ct)
    {
        try { return (await CoachGarmin.TrackAsync(space, f.CreateClient(), id, ct, timed: true), null); }
        catch (Exception e) when (e is CoachGarmin.GarminException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        { return ([], e is CoachGarmin.GarminException ? e.Message : "Garmin ist gerade nicht erreichbar."); }
    }

    public sealed record Hit(string Id, string Name, string? Brand, string Source, double KcalPer100, double? Amount, string? Portion, bool Mine, string? Image = null, string? Nutri = null)
    {
        /// <summary>Mit der üblichen Portion (wie sie „+“ einträgt), damit die Liste gleich „1 Portion groß (150 g) · 324 kcal“ zeigen kann.</summary>
        public static Hit Of(Food f)
        {
            var (amount, portion) = Usual(f);
            return new(f.Id, f.Name, f.Brand, f.Source, Math.Round(f.Per100.Kcal), portion == null ? null : amount, portion == null ? null : WithAmount(portion, amount, f.Liquid),
                false, null, f.Info?.Nutri);
        }
    }

    /// <summary>Adresse des Produktbilds (relativ zum Server).</summary>
    public static string ImagePath(string id) => "/api/coach/foods/" + Uri.EscapeDataString(id) + "/image";

    private static List<Hit> WithImages(IEnumerable<Hit> hits, CoachFoods foods)
    {
        var ids = foods.ImageIds();
        return hits.Select(h => ids.Contains(h.Id) ? h with { Image = ImagePath(h.Id) } : h).ToList();
    }

    private static string Source(string id) => id.Split(':')[0];

    /// <summary>„1 Glas (200 ml)“, „1 Apfel (mittel, 150 g)“ - die Menge im Namen, wenn sie noch nicht drinsteht.</summary>
    public static string WithAmount(string label, double amount, bool liquid)
    {
        if (System.Text.RegularExpressions.Regex.IsMatch(label, @"\d\s*(g|ml)\b")) return label;
        var a = amount.ToString("0.#", System.Globalization.CultureInfo.GetCultureInfo("de-DE")) + (liquid ? " ml" : " g");
        return label.EndsWith(')') ? label[..^1] + ", " + a + ")" : label + " (" + a + ")";
    }

    /// <summary>Übliche Portion: die erste, die nicht „100 g“ ist (1 Stück, 1 Portion, 1 Packung …), sonst 100 g.</summary>
    public static (double Amount, string? Portion) Usual(Food f) =>
        f.Portions.FirstOrDefault(p => Math.Abs(p.Grams - 100) > 0.01 || !p.Label.StartsWith("100")) is { } p ? (p.Grams, p.Label) : (100, null);

    private static readonly System.Text.RegularExpressions.Regex FirstBlob = new(@"<img[^>]*?\ssrc\s*=\s*[""']blob:([^""']+)[""']", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (double? Kcal, int? Servings, double? Protein)> RecipeCards = new();

    private static string OwnId(string id) => id.StartsWith("own:") ? id[4..] : id;

    /// <summary>Wie <see cref="Resolve(CoachFoods, string)"/>, dazu Rezepte der Person („recipe:&lt;sync_id&gt;“).</summary>
    public static Food? Resolve(UserSpace space, CoachFoods foods, string id) =>
        id.StartsWith("recipe:", StringComparison.Ordinal) ? CoachRecipes.AsFood(space, foods, id[7..]) : Resolve(foods, id);

    public static Food? Resolve(CoachFoods foods, string id)
    {
        var colon = id.IndexOf(':');
        if (colon < 0) return null;
        var key = id[(colon + 1)..];
        return id[..colon] switch
        {
            "bls" => Bls.Get(key) is { } e ? Bls.ToFood(e) : null,
            "rw" => Richtwerte.Get(key) is { } rw ? Richtwerte.ToFood(rw) : null,
            "own" => foods.Own(key),
            "off" => foods.Cached(key)?.Food ?? foods.OwnByBarcode(key),
            _ => null,
        };
    }

    private static DateOnly ParseDay(UserSpace space, string? date) =>
        string.IsNullOrEmpty(date) ? CoachStore.Today(space)
        : DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d
        : throw new ArgumentException("Datum bitte als JJJJ-MM-TT.");

    private static void Validate(CoachProfile p)
    {
        if (p.Birth is { } b && (b.Year < 1900 || b > DateOnly.FromDateTime(DateTime.Today))) throw new ArgumentException("Das Geburtsdatum passt nicht.");
        if (p.Sex is not (null or "f" or "m" or "d")) throw new ArgumentException("Unbekanntes Geschlecht.");
        if (p.HeightCm is double h && (h < 50 || h > 250)) throw new ArgumentException("Die Größe passt nicht (50 bis 250 cm).");
        if (p.WeightKg is double w && (w < 20 || w > 400)) throw new ArgumentException("Das Gewicht passt nicht (20 bis 400 kg).");
        if (p.Goal is not ("keep" or "lose" or "gain" or "muscle")) throw new ArgumentException("Unbekanntes Ziel.");
        if (p.Diet is not ("dge" or "med" or "vegetarian" or "vegan" or "protein" or "lowcarb" or "keto")) throw new ArgumentException("Unbekannte Ernährungsform.");
        if (p.StepMode is not ("fixed" or "dynamic")) throw new ArgumentException("Unbekannte Art des Schrittziels.");
        p.StepGoal = Math.Clamp(p.StepGoal, 1000, 50000);
        p.Pal = Math.Clamp(p.Pal, 1.2, 2.4);
        p.Pace = Math.Clamp(p.Pace, 0.1, 1.0);
        p.Allergens = p.Allergens.Distinct().Take(20).ToList();
        p.Intolerances = p.Intolerances.Distinct().Take(10).ToList();
        p.Dislikes = p.Dislikes.Select(x => x.Trim()).Where(x => x.Length is > 0 and <= 60).Distinct().Take(50).ToList();
        p.WaterSizes = (p.WaterSizes ?? []).Where(x => x is >= 50 and <= 3000).Distinct().Take(4).ToList();
        if (p.WaterSizes.Count == 0) p.WaterSizes = [250, 500];
    }

    /// <summary>Alles für einen Tag. Budget = Ziel + Bewegung, die über die im PAL schon enthaltene hinausgeht (nichts doppelt).</summary>
    public static object Day(UserSpace space, DateOnly day)
    {
        var db = space.Db;
        var p = CoachStore.Profile(db);
        var targets = CoachTargetsCalc.Compute(p, CoachStore.Weight(db, day), day);
        var entries = CoachStore.Diary(db, day);
        var water = CoachStore.WaterOf(db, day);
        var watch = CoachStore.WatchWater(db, day);
        var act = CoachStore.ActivityOf(db, day);
        double bonus = 0;
        if (targets.Bmr is double bmr && act?.ActiveKcal is double active)
            bonus = Math.Max(0, Math.Round(active - bmr * (Math.Clamp(p.Pal, 1.2, 2.4) - 1.2)));
        var eaten = new
        {
            kcal = Math.Round(entries.Sum(e => e.Kcal)), protein = Math.Round(entries.Sum(e => e.Protein), 1),
            carbs = Math.Round(entries.Sum(e => e.Carbs), 1), fat = Math.Round(entries.Sum(e => e.Fat), 1),
            fiber = Math.Round(entries.Sum(e => e.Fiber ?? 0), 1),
        };
        return new
        {
            date = CoachStore.Day(day),
            targets, budget = targets.Kcal is double k ? k + bonus : (double?)null, activityBonus = bonus,
            // Für die Anzeige: die ganze aktive Energie der Uhr. Das Budget bleibt gleich - der Grundbedarf ohne Bewegung ist dann budget − movement
            movement = act?.ActiveKcal is double moved && moved > 0 ? Math.Round(moved) : bonus,
            eaten,
            meals = CoachStore.Meals.ToDictionary(m => m, m => entries.Where(e => e.Meal == m).ToList()),
            // ml: eigene Einträge und die von der Uhr (watch, Garmin) zusammen; entries nur die eigenen (zum Zurücknehmen)
            water = new { ml = water.Sum(w => w.Ml) + watch.Ml, watch = watch.Ml, goal = targets.WaterMl, entries = water, sizes = p.WaterSizes },
            steps = new { steps = act?.Steps, goal = CoachStore.StepGoal(db, day), mode = p.StepMode },
            activity = act,
            weight = CoachStore.Weight(db, day),
            hideNumbers = p.HideNumbers,
            // Weitere Werte der Uhr (Garmin) und der Schnitt der sieben Tage davor - für die Kacheln der Tagesansicht
            extra = Extra(CoachStore.ExtrasOf(db, day)),
            // Einzelne Aktivitäten von der Uhr (Garmin)
            activities = CoachGarmin.ActivitiesOf(db, day).Select(x => new { id = x.Id, x.Name, x.Type, x.Start, durationS = x.DurationS, distanceM = x.DistanceM, kcal = x.Kcal }),
            avg7 = Avg7(db, day),
            // Verläufe der Körperwerte (14 Tage bis heute) für die kleinen Linien und Balken der Körper-Kachel
            trend = Trend(db, day),
            fasting = Fasting(space, day),
        };
    }

    private static object Extra(Dictionary<string, double> x)
    {
        double? V(string k) => x.TryGetValue(k, out var v) ? v : null;
        return new { stress = V("stress"), bodyBatteryHigh = V("bodyBatteryHigh"), bodyBatteryLow = V("bodyBatteryLow"), hrv = V("hrv") };
    }

    /// <summary>Je Tag der letzten 14 (bis einschließlich day): Schlaf, Ruhepuls, HRV, Stress, Body Battery - fehlende Werte null.</summary>
    private static object Trend(UserDb db, DateOnly day) =>
        Enumerable.Range(0, 14).Select(i => day.AddDays(i - 13)).Select(d =>
        {
            var a = CoachStore.ActivityOf(db, d);
            var x = CoachStore.ExtrasOf(db, d);
            double? V(string k) => x.TryGetValue(k, out var v) ? Math.Round(v) : null;
            return new { day = CoachStore.Day(d), sleepMin = a?.SleepMin, restingHr = a?.RestingHr, hrv = V("hrv"), stress = V("stress"), bbHigh = V("bodyBatteryHigh"), bbLow = V("bodyBatteryLow") };
        }).ToList();

    private static object Avg7(UserDb db, DateOnly day)
    {
        var days = Enumerable.Range(1, 7).Select(i => day.AddDays(-i)).ToList();
        var acts = days.Select(d => CoachStore.ActivityOf(db, d)).Where(a => a != null).ToList();
        var hrv = days.Select(d => CoachStore.ExtrasOf(db, d).TryGetValue("hrv", out var v) ? v : (double?)null).OfType<double>().ToList();
        static double? Avg(IEnumerable<double> v) { var l = v.ToList(); return l.Count == 0 ? null : Math.Round(l.Average()); }
        return new
        {
            sleepMin = Avg(acts.Select(a => a!.SleepMin).OfType<int>().Select(v => (double)v)),
            restingHr = Avg(acts.Select(a => a!.RestingHr).OfType<int>().Select(v => (double)v)),
            hrv = Avg(hrv),
        };
    }

    /// <summary>Laufendes Fasten (nur für heute) bzw. ob der Tracker an ist.</summary>
    private static object? Fasting(UserSpace space, DateOnly day)
    {
        var st = CoachFasting.Settings(space.Db);
        if (!st.Enabled || day != CoachStore.Today(space)) return null;
        var cur = CoachFasting.Current(space.Db);
        return new { running = cur != null, start = cur?.Start, goal = cur?.Goal ?? CoachFasting.GoalHours(st), plan = st.Plan, eatFrom = st.EatFrom };
    }
}
