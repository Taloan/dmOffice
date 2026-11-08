using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using DmOffice.Chat;
using DmOffice.Coach;
using DmOffice.Core;

namespace DmOffice.Notes;

/// <summary>
/// Rezepte im Internet suchen: Suchbegriff an DuckDuckGo (ohne Konto, ohne Bezug zur Person), dazu - wenn eine KI mit Websuche
/// eingerichtet und freigegeben ist - deren Treffer. Von jeder Trefferseite liest der Server das schema.org-Rezept (wie beim
/// Import per Link); Seiten ohne Rezept fallen weg. Nährwerte je Portion grob über die Zutaten (BLS), Bilder über den Server.
/// Übernommen wird mit dem Import per Link (Notiz mit Tag „recipe“).
/// </summary>
public static class RecipeSearch
{
    public sealed record Hit(string Url, string Name, string Source, string? Image, string Yield, string Time, List<string> Ingredients,
        int Servings, double? Kcal, double? Protein, double? Fat, double? Carbs, int Unmatched);

    /// <summary>Für Tests: Suche bzw. Seitenabruf ersetzen.</summary>
    public static Func<string, CancellationToken, Task<string>>? SearchFetch { get; set; }
    public static Func<string, CancellationToken, Task<string>>? PageFetch { get; set; }

    // Bilder, die eine Suche gezeigt hat - nur die holt der Server (kein offener Bild-Proxy); eine Stunde gültig
    private static readonly ConcurrentDictionary<string, DateTime> Images = new();
    private static readonly Regex Uddg = new(@"[?&]uddg=([^&""']+)", RegexOptions.Compiled);

    /// <summary>Adressen von Trefferseiten aus der HTML-Antwort von DuckDuckGo (Links laufen dort über /l/?uddg=…).</summary>
    public static List<string> ParseDuckDuckGo(string html)
    {
        var urls = new List<string>();
        foreach (Match m in Regex.Matches(html, @"class=""result__a""[^>]*href=""([^""]+)""|href=""([^""]+)""[^>]*class=""result__a"""))
        {
            var href = WebUtility.HtmlDecode(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
            var u = Uddg.Match(href) is { Success: true } x ? Uri.UnescapeDataString(x.Groups[1].Value) : href;
            if (u.StartsWith("//")) u = "https:" + u;
            if (Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && !uri.Host.EndsWith("duckduckgo.com")) urls.Add(uri.ToString());
        }
        return urls.Distinct().ToList();
    }

    private static async Task<List<string>> WebAsync(string q, CancellationToken ct)
    {
        var term = q + " Rezept";
        var html = SearchFetch != null ? await SearchFetch(term, ct)
            : await RecipeImport.PageAsync(new Uri("https://html.duckduckgo.com/html/?kl=de-de&q=" + Uri.EscapeDataString(term)), ct);
        return ParseDuckDuckGo(html);
    }

    private static async Task<List<string>> AiAsync(ServerConfig config, string q, ILogger log, CancellationToken ct)
    {
        var text = await PukkiAi.Write(config,
            "Du suchst im Internet Rezeptseiten. Antworte nur mit den Adressen der Seiten, je Zeile eine, ohne weiteren Text. " +
            "Nur Seiten mit einem einzelnen Rezept (keine Übersichten), möglichst deutschsprachig.",
            $"Finde bis zu 8 Rezepte für: {q}", log, ct);
        return text == null ? [] : Regex.Matches(text, @"https?://[^\s<>()""\]]+").Select(m => m.Value.TrimEnd('.', ',')).Distinct().ToList();
    }

    public static async Task<(List<Hit> Hits, bool Ai)> SearchAsync(UserSpace space, CoachFoods foods, ServerConfig config, bool ai, string q, ILogger log, CancellationToken ct)
    {
        q = q.Trim();
        if (q.Length < 2) return ([], false);
        if (q.Length > 100) q = q[..100];
        var useAi = ai && PukkiAi.CanSearch(config);
        var lists = await Task.WhenAll(
            Safe(() => WebAsync(q, ct)),
            useAi ? Safe(() => AiAsync(config, q, log, ct)) : Task.FromResult(new List<string>()));
        // KI-Treffer zuerst, dann die Websuche; doppelte Seiten nur einmal
        var urls = lists[1].Concat(lists[0]).DistinctBy(u => u.TrimEnd('/')).Take(12).ToList();
        using var gate = new SemaphoreSlim(4);
        var pages = await Task.WhenAll(urls.Select(async u =>
        {
            await gate.WaitAsync(ct);
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(10));
                var uri = new Uri(u);
                var html = PageFetch != null ? await PageFetch(u, cts.Token) : await RecipeImport.PageAsync(uri, cts.Token);
                return RecipeImport.FromHtml(html, uri) is { } r ? (Url: u, Recipe: r) : default;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or ArgumentException or UriFormatException) { return default; }
            finally { gate.Release(); }
        }));
        var hits = new List<Hit>();
        foreach (var (url, r) in pages.Where(p => p.Recipe != null))
        {
            var lines = r.Ingredients.SelectMany(s => s.Items).ToList();
            var (servings, _) = CoachRecipes.Servings("Portionen: " + Regex.Match(r.Yield, @"\d+").Value);
            var (per, unmatched) = CoachRecipes.Estimate(space, foods, lines, servings);
            if (r.Image != null) Images[r.Image] = DateTime.UtcNow.AddHours(1);
            hits.Add(new Hit(url, r.Name, new Uri(url).Host.Replace("www.", ""), r.Image, r.Yield, r.TotalTime.Length > 0 ? r.TotalTime : r.CookTime, lines, servings,
                (double?)Recipes.Kcal(r.Calories + " kcal je Portion") ?? (per.Kcal > 0 ? per.Kcal : null), per.Kcal > 0 ? per.Protein : null, per.Kcal > 0 ? per.Fat : null, per.Kcal > 0 ? per.Carbs : null, unmatched));
        }
        foreach (var (k, until) in Images) if (until < DateTime.UtcNow) Images.TryRemove(k, out _);
        return (hits, useAi);
    }

    private static async Task<List<string>> Safe(Func<Task<List<string>>> f)
    {
        try { return await f(); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException) { return []; }
    }

    /// <summary>Vorschaubild eines Treffers (nur Bilder aus einer Suche der letzten Stunde), klein als WebP.</summary>
    public static async Task<byte[]?> ImageAsync(string url, CancellationToken ct)
    {
        if (!Images.TryGetValue(url, out var until) || until < DateTime.UtcNow) return null;
        var data = await RecipeImport.ImageAsync(url, ct);
        return data == null ? null : DmOffice.Dav.Thumbs.FromBytes(data, 320);
    }
}
