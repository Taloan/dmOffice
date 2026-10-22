using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using DmOffice.Core;
using DmOffice.Dav;
using Notizi.Sync;

namespace DmOffice.Notes;

/// <summary>
/// Rezepte aus den anderen Notiz-Datenbanken unter der Wurzel des Abgleichs (nicht nur aus der gerade geladenen).
/// Gelesen wird nur, was das E2EE-Passwort des Benutzers oeffnet - die anderen bleiben zu. Es wird nichts in die
/// gerade aktive Datenbank uebernommen und nichts geschrieben; die Staende liegen im Speicher und werden nur neu
/// gelesen, wenn sich in der Datenbank etwas geaendert hat.
/// </summary>
public static class RemoteRecipes
{
    private sealed record Recipe(string SyncId, string Title, string? Folder, List<string> Ingredients);
    private sealed record Loaded(string Stamp, List<Recipe> Recipes);
    private sealed record Catalog(DateTime At, string Root, string Active, List<CatalogEntry> Dbs);

    private static readonly ConcurrentDictionary<string, Loaded> Cache = new();
    private static readonly ConcurrentDictionary<string, Catalog> Catalogs = new();
    private static readonly TimeSpan CatalogTtl = TimeSpan.FromMinutes(10);

    public sealed record Ingredients(string SyncId, string Title, List<string> Items);

    /// <summary>Rezepte der anderen Datenbanken; leer ohne Abgleich oder E2EE-Passwort. Fehler einer Datenbank lassen die anderen stehen.</summary>
    public static async Task<List<Recipes.Entry>> AllAsync(UserSpace space, string? q, CancellationToken ct)
    {
        var result = new List<Recipes.Entry>();
        foreach (var (db, loaded) in await LoadAllAsync(space, ct))
            foreach (var r in loaded.Recipes)
                if (string.IsNullOrWhiteSpace(q) || r.Title.Contains(q.Trim(), StringComparison.OrdinalIgnoreCase))
                    result.Add(new Recipes.Entry(0, r.SyncId, r.Title, r.Folder, db.Title));
        return result;
    }

    /// <summary>Zutaten der Rezepte mit diesen Kennungen aus den anderen Datenbanken.</summary>
    public static async Task<List<Ingredients>> IngredientsAsync(UserSpace space, IReadOnlySet<string> wanted, CancellationToken ct)
    {
        var result = new List<Ingredients>();
        if (wanted.Count == 0) return result;
        foreach (var (_, loaded) in await LoadAllAsync(space, ct))
            foreach (var r in loaded.Recipes.Where(r => wanted.Contains(r.SyncId)))
                if (result.All(x => x.SyncId != r.SyncId)) result.Add(new Ingredients(r.SyncId, r.Title, r.Ingredients));
        return result;
    }

    private static async Task<List<(CatalogEntry Db, Loaded Loaded)>> LoadAllAsync(UserSpace space, CancellationToken ct)
    {
        var found = new List<(CatalogEntry, Loaded)>();
        var n = space.Settings.Notizi;
        if (!n.Enabled || string.IsNullOrEmpty(n.E2eePassword)) return found;
        try
        {
            foreach (var db in await DatabasesAsync(space, ct))
            {
                if (db.Folder == n.DbFolder || !db.PasswordFits) continue;   // die aktive kommt aus dem lokalen Stand
                try
                {
                    var store = await SyncStore.OpenAsync(new DbTransport(space.Db, n.Root + "/" + db.Folder), n.E2eePassword, ct);
                    if (store == null) continue;
                    found.Add((db, await ReadAsync(space.User.Id + "|" + db.Folder, store, ct)));
                }
                catch (Exception) when (!ct.IsCancellationRequested) { /* diese Datenbank ist gerade nicht lesbar - die anderen zaehlen weiter */ }
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested) { /* kein Abgleichordner o. Ae. */ }
        return found;
    }

    private static async Task<List<CatalogEntry>> DatabasesAsync(UserSpace space, CancellationToken ct)
    {
        var n = space.Settings.Notizi;
        var key = space.User.Id;
        if (Catalogs.TryGetValue(key, out var c) && DateTime.UtcNow - c.At < CatalogTtl && c.Root == n.Root && c.Active == n.DbFolder) return c.Dbs;
        if (FileStore.Get(space.Db, FileStore.Normalize(n.Root)) == null) return new();
        var list = await SyncCatalog.ScanAsync(new DbTransport(space.Db, n.Root), folder => new DbTransport(space.Db, n.Root + "/" + folder), n.E2eePassword ?? "", ct);
        Catalogs[key] = new Catalog(DateTime.UtcNow, n.Root, n.DbFolder, list);
        return list;
    }

    private static async Task<Loaded> ReadAsync(string key, SyncStore store, CancellationToken ct)
    {
        var tags = await store.ListItemsAsync(null, ct);
        var stamp = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', tags.OrderBy(t => t.Key, StringComparer.Ordinal).Select(t => t.Key + ":" + t.Value)))));
        if (Cache.TryGetValue(key, out var hit) && hit.Stamp == stamp) return hit;

        var items = new ConcurrentBag<SyncItem>();
        using var gate = new SemaphoreSlim(8);
        await Task.WhenAll(tags.Keys.Select(async id =>
        {
            await gate.WaitAsync(ct);
            try { if (await store.ReadItemAsync(id, ct) is { } item) items.Add(item); }
            catch (Exception) when (!ct.IsCancellationRequested) { /* eine kaputte Datei lassen wir aus */ }
            finally { gate.Release(); }
        }));

        var folders = items.OfType<FolderItem>().Select(f => new NoteFolder { SyncId = f.Id, Parent = f.Parent, Name = f.Name, SortOrder = f.SortOrder });
        var paths = Recipes.FolderPaths(folders);
        var recipes = new List<Recipe>();
        foreach (var note in items.OfType<NoteItem>().Where(x => x.Deleted == null && x.Archived == null && Recipes.IsRecipe(x.Tags, x.Plain ?? "")))
        {
            var row = new NoteRow { SyncId = note.Id, Folder = note.Folder, Title = note.Title ?? "", CustomTitle = note.CustomTitle, Html = note.Html ?? "", Plain = note.Plain ?? "", Tags = note.Tags };
            recipes.Add(new Recipe(note.Id, row.DisplayTitle, note.Folder != null && paths.TryGetValue(note.Folder, out var p) ? p : null, Recipes.Ingredients(row)));
        }
        var loaded = new Loaded(stamp, recipes.OrderBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase).ToList());
        Cache[key] = loaded;
        return loaded;
    }
}
