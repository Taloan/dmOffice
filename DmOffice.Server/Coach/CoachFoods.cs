using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using DmOffice.Core;
using Microsoft.Data.Sqlite;

namespace DmOffice.Coach;

/// <summary>
/// Lebensmittel, die allen auf dem Server gehören (data/coach.db, verschlüsselt, im Core-Backup): selbst angelegte des Haushalts
/// und der Zwischenspeicher von Open Food Facts (nur Barcode → Produkt, ohne Bezug zu Personen). Das Tagebuch selbst bleibt
/// in der Benutzerdatenbank und ist privat.
/// </summary>
public sealed class CoachFoods
{
    private readonly string _connectionString;
    private readonly object _write = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    static CoachFoods() => SQLitePCL.Batteries_V2.Init();

    public CoachFoods(DataPaths paths, Vault vault) : this(Path.Combine(paths.Root, "coach.db"), LoadKey(paths, vault)) { }

    internal CoachFoods(string path, byte[] key)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Password = "x'" + Convert.ToHexString(key) + "'", DefaultTimeout = 30 }.ToString();
        using var conn = Open();
        UserDb.Exec(conn, @"CREATE TABLE IF NOT EXISTS own_foods (id TEXT PRIMARY KEY, name TEXT NOT NULL, brand TEXT NULL, barcode TEXT NULL,
                per100 TEXT NOT NULL, portions TEXT NOT NULL, liquid INTEGER NOT NULL DEFAULT 0, created_by TEXT NOT NULL, created INTEGER NOT NULL, updated INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS own_foods_barcode ON own_foods(barcode);
            CREATE TABLE IF NOT EXISTS off_products (code TEXT PRIMARY KEY, food TEXT NULL, fetched INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS food_images (id TEXT PRIMARY KEY, url TEXT NULL, data BLOB NULL, fetched INTEGER NOT NULL);");
    }

    private static byte[] LoadKey(DataPaths paths, Vault vault)
    {
        Directory.CreateDirectory(paths.Keys);
        var file = Path.Combine(paths.Keys, "coach.key");
        if (!File.Exists(file))
        {
            var fresh = RandomNumberGenerator.GetBytes(32);
            File.WriteAllText(file, vault.Wrap(fresh));
            return fresh;
        }
        var wrapped = File.ReadAllText(file).Trim();
        try { return vault.Unwrap(wrapped); }
        catch (CryptographicException) when (vault.Previous != null)
        {
            var key = Vault.Unwrap(vault.Previous, wrapped);
            File.WriteAllText(file, vault.Wrap(key));
            return key;
        }
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=30000; PRAGMA synchronous=NORMAL;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    /// <summary>Konsistente, gleich verschlüsselte Kopie (Core-Backup).</summary>
    public void SnapshotTo(string file) => S3Backup.Vacuum(_connectionString, file);

    private static long Unix() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    // ---------------- Eigene Lebensmittel des Haushalts ----------------

    public sealed record OwnInput(string? Name, string? Brand, string? Barcode, Nutrients? Per100, List<Portion>? Portions, bool Liquid);

    public Food SaveOwn(string? id, OwnInput input, string user)
    {
        var name = (input.Name ?? "").Trim();
        if (name.Length == 0) throw new ArgumentException("Bitte einen Namen eingeben.");
        if (name.Length > 120) throw new ArgumentException("Der Name ist zu lang (höchstens 120 Zeichen).");
        var per = input.Per100 ?? throw new ArgumentException("Bitte die Nährwerte je 100 g angeben.");
        if (per.Kcal < 0 || per.Kcal > 950 || per.Protein < 0 || per.Fat < 0 || per.Carbs < 0 || per.Protein + per.Fat + per.Carbs > 101)
            throw new ArgumentException("Die Nährwerte je 100 g passen nicht (Energie höchstens 950 kcal, Eiweiß, Fett und Kohlenhydrate zusammen höchstens 100 g).");
        var barcode = Barcode(input.Barcode);
        var portions = (input.Portions ?? []).Where(p => p.Grams > 0 && p.Grams <= 5000 && !string.IsNullOrWhiteSpace(p.Label))
            .Select(p => new Portion(p.Label.Trim(), Math.Round(p.Grams, 1))).Take(10).ToList();
        lock (_write)
        {
            using var conn = Open();
            id ??= "o" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
            var exists = UserDb.Query(conn, "SELECT 1 FROM own_foods WHERE id=$i", r => 1, ("$i", id)).Count > 0;
            if (exists)
                UserDb.Exec(conn, "UPDATE own_foods SET name=$n, brand=$b, barcode=$c, per100=$p, portions=$o, liquid=$l, updated=$t WHERE id=$i",
                    ("$i", id), ("$n", name), ("$b", Clean(input.Brand)), ("$c", barcode), ("$p", JsonSerializer.Serialize(per, Json)),
                    ("$o", JsonSerializer.Serialize(portions, Json)), ("$l", input.Liquid ? 1 : 0), ("$t", Unix()));
            else
                UserDb.Exec(conn, "INSERT INTO own_foods(id, name, brand, barcode, per100, portions, liquid, created_by, created, updated) VALUES($i,$n,$b,$c,$p,$o,$l,$u,$t,$t)",
                    ("$i", id), ("$n", name), ("$b", Clean(input.Brand)), ("$c", barcode), ("$p", JsonSerializer.Serialize(per, Json)),
                    ("$o", JsonSerializer.Serialize(portions, Json)), ("$l", input.Liquid ? 1 : 0), ("$u", user), ("$t", Unix()));
        }
        return Own(id) ?? throw new InvalidOperationException("Speichern fehlgeschlagen.");
    }

    public void DeleteOwn(string id)
    {
        lock (_write)
        {
            using var conn = Open();
            UserDb.Exec(conn, "DELETE FROM own_foods WHERE id=$i", ("$i", id));
            UserDb.Exec(conn, "DELETE FROM food_images WHERE id=$i", ("$i", "own:" + id));
        }
    }

    public Food? Own(string id)
    {
        using var conn = Open();
        return UserDb.Query(conn, "SELECT id, name, brand, barcode, per100, portions, liquid FROM own_foods WHERE id=$i", ReadOwn, ("$i", id)).FirstOrDefault();
    }

    public Food? OwnByBarcode(string code)
    {
        using var conn = Open();
        return UserDb.Query(conn, "SELECT id, name, brand, barcode, per100, portions, liquid FROM own_foods WHERE barcode=$c ORDER BY updated DESC LIMIT 1", ReadOwn, ("$c", code)).FirstOrDefault();
    }

    public List<Food> SearchOwn(string q, int limit = 20)
    {
        var query = Bls.Words(Bls.Fold(q));
        if (query.Length == 0) return [];
        using var conn = Open();
        return UserDb.Query(conn, "SELECT id, name, brand, barcode, per100, portions, liquid FROM own_foods", ReadOwn)
            .Select(f => (f, s: Bls.Score(query, Bls.Fold(f.Name + " " + f.Brand), Bls.Words(Bls.Fold(f.Name + " " + f.Brand)))))
            .Where(x => x.s > 0).OrderByDescending(x => x.s).Take(limit).Select(x => x.f).ToList();
    }

    private static Food ReadOwn(SqliteDataReader r)
    {
        var per = JsonSerializer.Deserialize<Nutrients>(r.GetString(4), Json) ?? Nutrients.Zero;
        var portions = JsonSerializer.Deserialize<List<Portion>>(r.GetString(5), Json) ?? [];
        var liquid = r.GetInt64(6) != 0;
        if (portions.All(p => p.Grams != 100)) portions.Add(new Portion(liquid ? "100 ml" : "100 g", 100));
        return new Food("own:" + r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), "own", per, portions, liquid, r.IsDBNull(3) ? null : r.GetString(3));
    }

    // ---------------- Open Food Facts ----------------

    /// <summary>Gespeichertes Produkt zum Barcode: (gefunden, Produkt, wann geholt). Null: noch nie gefragt.</summary>
    public (Food? Food, long Fetched)? Cached(string code)
    {
        using var conn = Open();
        return UserDb.Query(conn, "SELECT food, fetched FROM off_products WHERE code=$c", r =>
            ((Food?)(r.IsDBNull(0) ? null : JsonSerializer.Deserialize<Food>(r.GetString(0), Json)), r.GetInt64(1)), ("$c", code)).Cast<(Food?, long)?>().FirstOrDefault();
    }

    public void Remember(string code, Food? food)
    {
        lock (_write)
        {
            using var conn = Open();
            UserDb.Exec(conn, "INSERT INTO off_products(code, food, fetched) VALUES($c,$f,$t) ON CONFLICT(code) DO UPDATE SET food=$f, fetched=$t",
                ("$c", code), ("$f", food == null ? null : JsonSerializer.Serialize(food, Json)), ("$t", Unix()));
        }
    }

    /// <summary>Schon geholte Produkte nach Namen (die Suche bei Open Food Facts selbst ist nicht fürs Tippen gedacht).</summary>
    public List<Food> SearchCached(string q, int limit = 15)
    {
        var query = Bls.Words(Bls.Fold(q));
        if (query.Length == 0) return [];
        using var conn = Open();
        return UserDb.Query(conn, "SELECT food FROM off_products WHERE food IS NOT NULL", r => JsonSerializer.Deserialize<Food>(r.GetString(0), Json)!)
            .Select(f => (f, s: Bls.Score(query, Bls.Fold(f.Name + " " + f.Brand), Bls.Words(Bls.Fold(f.Name + " " + f.Brand)))))
            .Where(x => x.s > 0).OrderByDescending(x => x.s).Take(limit).Select(x => x.f).ToList();
    }

    // ---------------- Bilder (klein, WebP) ----------------

    /// <summary>Bild-Adresse bei Open Food Facts merken; eine neue Adresse verwirft das zwischengespeicherte Bild.</summary>
    public void SetImageUrl(string id, string url)
    {
        lock (_write)
        {
            using var conn = Open();
            UserDb.Exec(conn, @"INSERT INTO food_images(id, url, data, fetched) VALUES($i,$u,NULL,0)
                ON CONFLICT(id) DO UPDATE SET data = CASE WHEN url IS $u THEN data ELSE NULL END, url=$u", ("$i", id), ("$u", url));
        }
    }

    /// <summary>Verkleinertes Bild ablegen (geholt oder hochgeladen); null löscht es.</summary>
    public void SetImage(string id, byte[]? data)
    {
        lock (_write)
        {
            using var conn = Open();
            if (data == null) UserDb.Exec(conn, "DELETE FROM food_images WHERE id=$i", ("$i", id));
            else UserDb.Exec(conn, "INSERT INTO food_images(id, url, data, fetched) VALUES($i,NULL,$d,$t) ON CONFLICT(id) DO UPDATE SET data=$d, fetched=$t",
                ("$i", id), ("$d", data), ("$t", Unix()));
        }
    }

    public (string? Url, byte[]? Data)? Image(string id)
    {
        using var conn = Open();
        return UserDb.Query(conn, "SELECT url, data FROM food_images WHERE id=$i", r =>
            ((string?)(r.IsDBNull(0) ? null : r.GetString(0)), (byte[]?)(r.IsDBNull(1) ? null : (byte[])r.GetValue(1))), ("$i", id))
            .Cast<(string?, byte[]?)?>().FirstOrDefault();
    }

    /// <summary>Lebensmittel mit Bild (für die Trefferliste).</summary>
    public HashSet<string> ImageIds()
    {
        using var conn = Open();
        return UserDb.Query(conn, "SELECT id FROM food_images WHERE url IS NOT NULL OR data IS NOT NULL", r => r.GetString(0)).ToHashSet();
    }

    /// <summary>Barcode prüfen: nur Ziffern, 8 bis 14 Stellen (EAN-8, UPC, EAN-13, GTIN-14).</summary>
    public static string? Barcode(string? code)
    {
        var c = Regex.Replace(code ?? "", @"\s", "");
        if (c.Length == 0) return null;
        if (!Regex.IsMatch(c, @"^\d{8,14}$")) throw new ArgumentException("Ein Barcode hat 8 bis 14 Ziffern.");
        return c;
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Length > 120 ? s.Trim()[..120] : s.Trim();
}

/// <summary>
/// Open Food Facts (ODbL): Produkt zum Barcode - nur der Barcode geht hinaus, kein Bezug zur Person. Ergebnisse (auch „unbekannt“)
/// liegen in coach.db: gefundene 30 Tage, unbekannte einen Tag. Höchstens 15 Abrufe pro Minute (Regel von Open Food Facts).
/// </summary>
public static class OpenFoodFacts
{
    public const string Attribution = "Enthält Daten von Open Food Facts (openfoodfacts.org), lizenziert unter der Open Database License (ODbL)";
    public static string UserAgent { get; set; } = "dmOffice/2.0 (self-hosted; https://dmoffice.de)";
    /// <summary>Für Tests: Abruf ersetzen (Barcode → JSON von Open Food Facts oder null).</summary>
    public static Func<string, CancellationToken, Task<string?>>? Fetch { get; set; }

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Queue<DateTime> Recent = new();

    public static async Task<Food?> LookupAsync(CoachFoods store, IHttpClientFactory http, string code, CancellationToken ct)
    {
        code = CoachFoods.Barcode(code) ?? throw new ArgumentException("Bitte einen Barcode angeben.");
        if (store.OwnByBarcode(code) is { } own) return own;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (store.Cached(code) is { } c && now - c.Fetched < (c.Food != null ? 30 * 86400 : 86400)) return c.Food;
        string? json;
        try { json = await Get(http, code, ct); }
        catch (HttpRequestException) { return store.Cached(code)?.Food; }   // offline: das Gemerkte, auch wenn alt
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return store.Cached(code)?.Food; }
        var food = json == null ? null : Parse(code, json);
        store.Remember(code, food);
        if (food != null && json != null && ImageUrl(json) is { } url) store.SetImageUrl(food.Id, url);
        return food;
    }

    // ---------------- Suche nach Namen ----------------

    /// <summary>Für Tests: Suche ersetzen (Suchbegriff → JSON von Open Food Facts).</summary>
    public static Func<string, CancellationToken, Task<string?>>? SearchFetch { get; set; }
    private static readonly Queue<DateTime> RecentSearches = new();
    private static readonly Dictionary<string, (DateTime At, List<Food> Foods)> SearchCache = new();

    /// <summary>
    /// Markenprodukte nach Namen (Produkte in Deutschland). Open Food Facts erlaubt höchstens 10 Suchen pro Minute - darüber
    /// gibt es null (der Benutzer tippt einfach weiter). Gefundene Produkte werden wie per Barcode gemerkt; Ergebnisse je Begriff eine Stunde.
    /// </summary>
    public static async Task<List<Food>?> SearchAsync(CoachFoods store, IHttpClientFactory factory, string q, CancellationToken ct)
    {
        var term = Regex.Replace(q.Trim().ToLowerInvariant(), @"\s+", " ");
        if (term.Length < 3) return [];
        if (term.Length > 80) term = term[..80];
        lock (SearchCache)
        {
            if (SearchCache.TryGetValue(term, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromHours(1)) return hit.Foods;
            while (RecentSearches.Count > 0 && DateTime.UtcNow - RecentSearches.Peek() > TimeSpan.FromMinutes(1)) RecentSearches.Dequeue();
            if (RecentSearches.Count >= 10) return null;
            RecentSearches.Enqueue(DateTime.UtcNow);
        }
        string? json;
        try { json = SearchFetch != null ? await SearchFetch(term, ct) : await Search(factory, term, ct); }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return null; }
        var foods = new List<Food>();
        if (json != null)
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("products", out var products) && products.ValueKind == JsonValueKind.Array)
                foreach (var p in products.EnumerateArray())
                {
                    var code = p.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                    if (code == null || !Regex.IsMatch(code, @"^\d{8,14}$") || ParseProduct(code, p) is not { } food) continue;
                    store.Remember(code, food);
                    if (ImageUrlOf(p) is { } url) store.SetImageUrl(food.Id, url);
                    foods.Add(food);
                }
        }
        lock (SearchCache)
        {
            if (SearchCache.Count > 500) SearchCache.Clear();
            SearchCache[term] = (DateTime.UtcNow, foods);
        }
        return foods;
    }

    private const string Fields = "code,product_name,product_name_de,generic_name_de,brands,quantity,serving_size,serving_quantity,nutriments,nutrition_data_per,product_quantity,product_quantity_unit,image_front_small_url,image_small_url,nutriscore_grade,ecoscore_grade,environmental_score_grade,nova_group,ingredients_analysis_tags,labels_tags,ingredients_text_de,ingredients_text,allergens_tags,traces_tags";

    private static async Task<string?> Search(IHttpClientFactory factory, string term, CancellationToken ct)
    {
        var http = factory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(10);
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://world.openfoodfacts.org/cgi/search.pl?action=process&json=1&search_simple=1&page_size=20&lc=de"
            + "&tagtype_0=countries&tag_contains_0=contains&tag_0=germany&fields=" + Fields + "&search_terms=" + Uri.EscapeDataString(term));
        req.Headers.UserAgent.ParseAdd(UserAgent);
        using var res = await http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsStringAsync(ct);
    }

    // ---------------- Bilder ----------------

    /// <summary>Kleines Produktbild (Vorderseite) aus der Antwort zum Barcode.</summary>
    public static string? ImageUrl(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("product", out var p) ? ImageUrlOf(p) : null;
    }

    private static string? ImageUrlOf(JsonElement p)
    {
        foreach (var n in new[] { "image_front_small_url", "image_small_url" })
            if (p.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String && AllowedImage(v.GetString()) is { } u) return u;
        return null;
    }

    /// <summary>Nur Bilder von Open Food Facts selbst (https), nie beliebige Adressen.</summary>
    public static string? AllowedImage(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == "https" && u.Host == "images.openfoodfacts.org" ? u.ToString() : null;

    /// <summary>Für Tests: Bildabruf ersetzen.</summary>
    public static Func<string, CancellationToken, Task<byte[]?>>? ImageFetch { get; set; }

    /// <summary>Bild holen und auf die kleine Größe von Open Food Facts verkleinern (200 px, WebP).</summary>
    public static async Task<byte[]?> FetchImageAsync(IHttpClientFactory factory, string url, CancellationToken ct)
    {
        if (AllowedImage(url) == null) return null;
        byte[]? bytes;
        if (ImageFetch != null) bytes = await ImageFetch(url, ct);
        else
        {
            var http = factory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd(UserAgent);
            using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!res.IsSuccessStatusCode || res.Content.Headers.ContentLength > 5_000_000) return null;
            bytes = await res.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length > 5_000_000) return null;
        }
        return bytes == null ? null : DmOffice.Dav.Thumbs.FromBytes(bytes, ImageSize);
    }

    /// <summary>Größe der Produktbilder (wie „small“ bei Open Food Facts).</summary>
    public const int ImageSize = 200;

    private static async Task<string?> Get(IHttpClientFactory factory, string code, CancellationToken ct)
    {
        if (Fetch != null) return await Fetch(code, ct);
        await Gate.WaitAsync(ct);
        try
        {
            // Höchstens 15 in der Minute: sonst kurz warten
            while (Recent.Count > 0 && DateTime.UtcNow - Recent.Peek() > TimeSpan.FromMinutes(1)) Recent.Dequeue();
            if (Recent.Count >= 15) await Task.Delay(TimeSpan.FromMinutes(1) - (DateTime.UtcNow - Recent.Peek()), ct);
            Recent.Enqueue(DateTime.UtcNow);
        }
        finally { Gate.Release(); }
        var http = factory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(10);
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://world.openfoodfacts.org/api/v2/product/{code}.json?fields={Fields}");
        req.Headers.UserAgent.ParseAdd(UserAgent);
        using var res = await http.SendAsync(req, ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsStringAsync(ct);
    }

    /// <summary>Antwort von Open Food Facts in ein Lebensmittel; ohne Energieangabe: null (nicht zu gebrauchen).</summary>
    public static Food? Parse(string code, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.Number && st.GetInt32() == 0) return null;
        if (!root.TryGetProperty("product", out var p)) return null;
        return ParseProduct(code, p);
    }

    /// <summary>Ein Produkt (aus der Antwort zum Barcode oder aus der Suche).</summary>
    public static Food? ParseProduct(string code, JsonElement p)
    {
        string? S(string n) => p.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String && v.GetString()!.Trim().Length > 0 ? v.GetString()!.Trim() : null;
        double? D(JsonElement e, string n) => e.TryGetProperty(n, out var v)
            ? v.ValueKind == JsonValueKind.Number ? v.GetDouble() : v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null
            : null;
        if (!p.TryGetProperty("nutriments", out var n)) return null;
        var kcal = D(n, "energy-kcal_100g") ?? (D(n, "energy_100g") is double kj ? kj / 4.184 : null);
        if (kcal == null) return null;
        var per = new Nutrients(Math.Round(kcal.Value, 1), D(n, "proteins_100g") ?? 0, D(n, "fat_100g") ?? 0, D(n, "carbohydrates_100g") ?? 0,
            D(n, "fiber_100g"), D(n, "sugars_100g"), D(n, "salt_100g"), D(n, "saturated-fat_100g"));
        var name = S("product_name_de") ?? S("product_name") ?? S("generic_name_de") ?? code;
        var brand = S("brands")?.Split(',')[0].Trim();
        var unit = (S("product_quantity_unit") ?? "").ToLowerInvariant();
        var liquid = (S("nutrition_data_per") ?? "").Contains("ml") || unit is "ml" or "l" || Regex.IsMatch(S("quantity") ?? "", @"\d\s*(ml|l|cl)\b", RegexOptions.IgnoreCase);
        var portions = new List<Portion>();
        if (D(p, "serving_quantity") is double sq && sq > 0 && sq <= 5000)
            portions.Add(new Portion("1 Portion (" + (S("serving_size") ?? sq.ToString("0.#", CultureInfo.InvariantCulture) + (liquid ? " ml" : " g")) + ")", sq));
        if (D(p, "product_quantity") is double pq && pq > 0 && pq <= 5000 && portions.All(x => Math.Abs(x.Grams - pq) > 0.5))
            portions.Add(new Portion("1 Packung (" + (S("quantity") ?? pq.ToString("0.#", CultureInfo.InvariantCulture) + (liquid ? " ml" : " g")) + ")", pq));
        portions.AddRange(Portions.For(name, liquid).Where(x => x.Grams != 100));
        portions.Add(new Portion(liquid ? "100 ml" : "100 g", 100));
        return new Food("off:" + code, name, brand, "off", per, portions.DistinctBy(x => x.Label).Take(6).ToList(), liquid, code, S("quantity"), Info(p));
    }

    private static readonly Dictionary<string, string> OffAllergen = new()
    {
        ["en:gluten"] = "gluten", ["en:crustaceans"] = "crustaceans", ["en:eggs"] = "eggs", ["en:fish"] = "fish", ["en:peanuts"] = "peanuts",
        ["en:soybeans"] = "soy", ["en:milk"] = "milk", ["en:nuts"] = "nuts", ["en:celery"] = "celery", ["en:mustard"] = "mustard",
        ["en:sesame-seeds"] = "sesame", ["en:sulphur-dioxide-and-sulphites"] = "sulphites", ["en:lupin"] = "lupin", ["en:molluscs"] = "molluscs",
    };

    /// <summary>Nutri-Score, Eco-Score, NOVA und Merkmale aus dem Produkt; null, wenn nichts davon bekannt ist.</summary>
    private static OffInfo? Info(JsonElement p)
    {
        string? Grade(string n) => p.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: 1 } g && "abcde".Contains(g) ? g : null;
        HashSet<string> Tags(string n) => p.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToHashSet() : [];
        int? nova = p.TryGetProperty("nova_group", out var nv) && nv.ValueKind == JsonValueKind.Number && nv.GetInt32() is >= 1 and <= 4 ? nv.GetInt32() : null;
        var ing = Tags("ingredients_analysis_tags");
        var labels = Tags("labels_tags");
        var flags = new List<string>();
        if (ing.Contains("en:palm-oil")) flags.Add("palm-oil");
        else if (ing.Contains("en:palm-oil-free")) flags.Add("palm-oil-free");
        if (ing.Contains("en:vegan")) flags.Add("vegan");
        else if (ing.Contains("en:vegetarian")) flags.Add("vegetarian");
        if (labels.Contains("en:no-gluten")) flags.Add("gluten-free");
        if (labels.Contains("en:no-lactose")) flags.Add("lactose-free");
        if (labels.Contains("en:organic") || labels.Contains("en:eu-organic")) flags.Add("organic");
        var nutri = Grade("nutriscore_grade");
        var eco = Grade("environmental_score_grade") ?? Grade("ecoscore_grade");
        string? Text(string n) => p.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String && v.GetString()!.Trim() is { Length: > 0 } x ? (x.Length > 2000 ? x[..2000] : x) : null;
        var ingredients = Text("ingredients_text_de") ?? Text("ingredients_text");
        // Allergene in den Schlüsseln des Profils (en:milk → milk, en:soybeans → soy …)
        var allergens = Tags("allergens_tags").Select(a => OffAllergen.TryGetValue(a, out var k) ? k : null).OfType<string>().Distinct().ToList();
        return nutri == null && eco == null && nova == null && flags.Count == 0 && ingredients == null && allergens.Count == 0
            ? null : new OffInfo(nutri, eco, nova, flags, ingredients, allergens);
    }
}
