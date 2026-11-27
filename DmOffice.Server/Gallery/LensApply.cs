using System.Threading.Channels;
using DmOffice.Core;
using DmOffice.Dav;

namespace DmOffice.Gallery;

/// <summary>
/// Objektivkorrektur auf die Bibliothek anwenden (Kennung lenscorr): Fotos der eigenen Ablage und der Galerie-Ordner, auf die eine Regel
/// passt, bekommen den Teil „Objektivkorrektur“ in ihre Bearbeitung (nicht-destruktiv wie die übrigen Einstellungen); vorhandene
/// Bearbeitungen bleiben, ebenso ein Foto, bei dem die Korrektur schon eingetragen oder ausgeschaltet ist. Danach entstehen die
/// Vorschaubilder neu - im Hintergrund, mit Fortschritt in der Aktivität („lens-apply“).
/// Neue Fotos bekommen die Korrektur gleich beim Einlesen, wenn ihre Besitzerin „Neue Fotos gleich korrigieren“ an hat (<see cref="AutoNewFor"/>).
/// </summary>
public static class LensApply
{
    /// <summary>Stand eines Fotos: Korrektur noch nicht eingetragen, schon eingetragen, vom Benutzer ausgeschaltet.</summary>
    public enum State { Todo, Done, Off }

    /// <summary>Ein Foto mit passender Regel. Id wie in der Galerie (eigene Ablage: Pfad, Galerie-Ordner: „ext:…“), Key der Bearbeitung.</summary>
    public sealed record Photo(string Id, string Key, string Rule, State State);

    private static Spaces? _spaces;
    private static ServerConfig? _config;
    private static ILogger? _log;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> Running = new();

    /// <summary>Stand der Korrektur aus der gespeicherten Bearbeitung.</summary>
    public static State StateOf(PhotoEdits.Edit? edit) => edit?.Lens == null ? State.Todo : edit.Lens.Off ? State.Off : State.Done;

    /// <summary>Bearbeitung mit eingeschalteter Korrektur (Profil über die Regeln, Vorgaben der Regler); alles andere bleibt.</summary>
    public static PhotoEdits.Edit WithLens(PhotoEdits.Edit? before) =>
        before == null ? new PhotoEdits.Edit(0, null, null, "hand", 0) { Lens = new LensSettings() } : before with { Lens = new LensSettings() };

    private static bool IsImage(GalleryTimeline.Entry e) => GalleryFolders.KindOf(e.Name) == "image";

    /// <summary>Statt einer Regel: Profil automatisch nach dem Objektivnamen.</summary>
    public const string FromLibrary = "library"; // [feature:lenslib]

    /// <summary>Eigene Fotos (keine aus geteilten Ordnern anderer) mit passender Regel; <paramref name="rule"/>: nur die, für die diese Regel gilt.</summary>
    public static List<Photo> Candidates(IEnumerable<GalleryTimeline.Entry> entries, Func<string, PhotoEdits.Edit?> edit, string? rule = null, string? user = null)
    {
        var list = new List<Photo>();
        foreach (var e in entries)
        {
            if (e.Owner != null || e.Meta == null || !IsImage(e)) continue;
            if (e.Id.StartsWith("tresor:", StringComparison.Ordinal)) continue;
            var ruleId = LensProfiles.Match(e.Meta, user)?.Id;
            if (ruleId == null && LensProfiles.Library(e.Meta, user) != null) ruleId = FromLibrary; // [feature:lenslib]
            if (ruleId == null || rule != null && ruleId != rule) continue;
            var key = e.Id.StartsWith("ext:", StringComparison.Ordinal) ? PhotoEdits.KeyOfExt(e.Id[4..]) : e.FileId > 0 ? "f:" + e.FileId : null;
            if (key == null) continue;
            list.Add(new Photo(e.Id, key, ruleId, StateOf(edit(key))));
        }
        return list;
    }

    /// <summary>Aufnahmedaten der Bibliothek, zusammengefasst: Kamera, Objektiv, Brennweite (0,1 mm) → Anzahl (Vorschläge im Regeldialog).</summary>
    public static List<object> Library(IEnumerable<GalleryTimeline.Entry> entries) =>
        [.. entries.Where(e => e.Owner == null && e.Meta != null && IsImage(e) && (e.Meta.Camera != null || e.Meta.Lens != null))
            .GroupBy(e => (Camera: e.Meta!.Camera?.Trim(), Lens: e.Meta.Lens?.Trim(), Focal: e.Meta.Focal is { } f ? Math.Round(f, 1) : (double?)null))
            .Select(g => (object)new { camera = g.Key.Camera, lens = g.Key.Lens, focal = g.Key.Focal, n = g.Count() })];

    // ---------------- Neue Fotos ----------------

    /// <summary>„Neue Fotos gleich korrigieren“ dieser Person: eigene Wahl, sonst die Vorgabe (<see cref="LensProfiles.AutoNewDefault"/>).</summary>
    public static bool AutoNewFor(UserSpace space) => space.Settings.LensAutoNew ?? LensProfiles.AutoNewDefault;

    /// <summary>Kommt für dieses Foto der Person die Korrektur beim Einlesen in Frage (Schalter an, Regel passt, Einstellung der Person an)?</summary>
    public static bool Wanted(UserDb db, FileMeta.Row? meta) =>
        Wanted(meta, () => _spaces == null ? LensProfiles.AutoNewDefault : AutoNewFor(_spaces.Get(db.UserId)), db.UserId);

    /// <param name="autoNew">Einstellung der Person - erst gefragt, wenn eine Regel passt (die Einstellungen zu lesen kostet mehr).</param>
    public static bool Wanted(FileMeta.Row? meta, Func<bool> autoNew, string? user = null) =>
        Features.On(Features.LensCorr) && meta != null && (meta.Camera != null || meta.Lens != null)
        && LensProfiles.Auto(meta, user) != null && autoNew();

    /// <summary>Neues Foto in der eigenen Ablage (Aufnahmedaten zum ersten Mal gelesen).</summary>
    public static void ImportedOwn(UserDb db, string path)
    {
        if (FileStore.Get(db, path) is not { IsDir: false } f || f.FileId <= 0) return;
        if (!Add(db, PhotoEdits.KeyOf(f))) return;
        Later(ct => Thumbs.Get(db, f, Thumbs.Small, ct));
    }

    /// <summary>Neues Foto in einem Galerie-Ordner (Aufnahmedaten zum ersten Mal gelesen).</summary>
    public static void ImportedExt(UserDb db, string source, string rel)
    {
        if (!Add(db, PhotoEdits.KeyOfExt(source + "/" + rel))) return;
        var user = db.UserId;
        Later(ct => _spaces == null || _config == null ? Task.FromResult<byte[]?>(null) : GalleryFolders.ThumbAsync(_spaces.Get(user), _config, source + "/" + rel, Thumbs.Small, ct));
    }

    private static bool Add(UserDb db, string key)
    {
        try
        {
            var before = PhotoEdits.Get(db, key);
            if (StateOf(before) != State.Todo) return false;
            PhotoEdits.Set(db, key, WithLens(before));
            return true;
        }
        catch (Microsoft.Data.Sqlite.SqliteException e) { _log?.LogWarning("Objektivkorrektur beim Einlesen: {Error}", e.Message); return false; }
    }

    // Vorschaubilder neuer Fotos nacheinander vorab rechnen (die Engine ist teuer; die Galerie soll sie nicht beim Anzeigen rechnen)
    private static readonly Channel<Func<CancellationToken, Task<byte[]?>>> Queue =
        Channel.CreateBounded<Func<CancellationToken, Task<byte[]?>>>(new BoundedChannelOptions(5000) { FullMode = BoundedChannelFullMode.DropWrite });

    private static void Later(Func<CancellationToken, Task<byte[]?>> render) => Queue.Writer.TryWrite(render);

    private static async Task WorkAsync(CancellationToken ct)
    {
        await foreach (var render in Queue.Reader.ReadAllAsync(ct))
        {
            try { await render(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e) when (e is not OutOfMemoryException) { _log?.LogDebug("Vorschaubild mit Objektivkorrektur: {Error}", e.Message); }
        }
    }

    // ---------------- Auf die Bibliothek anwenden ----------------

    public record ApplyBody(string? Rule, string? Lang);

    /// <summary>Vorschaubild neu (eigene Ablage oder Galerie-Ordner) - rechnet die große und die kleine Fassung und merkt sie.</summary>
    private static Task<byte[]?> RenderAsync(UserSpace space, ServerConfig config, Photo p, CancellationToken ct)
    {
        if (p.Id.StartsWith("ext:", StringComparison.Ordinal)) return GalleryFolders.ThumbAsync(space, config, p.Id[4..], Thumbs.Small, ct);
        return FileStore.ById(space.Db, long.Parse(p.Key[2..], System.Globalization.CultureInfo.InvariantCulture)) is { IsDir: false } f
            ? Thumbs.Get(space.Db, f, Thumbs.Small, ct) : Task.FromResult<byte[]?>(null);
    }

    public static string Summary(int changed, int total, int failed, bool stopped, string? lang)
    {
        var en = lang == "en";
        var text = en ? (stopped ? $"Cancelled: lens correction added to {changed} of {total} photos" : changed == 1 ? "Lens correction added to 1 photo" : $"Lens correction added to {changed} photos")
            : (stopped ? $"Abgebrochen: Objektivkorrektur bei {changed} von {total} Fotos eingetragen" : changed == 1 ? "Objektivkorrektur bei 1 Foto eingetragen" : $"Objektivkorrektur bei {changed} Fotos eingetragen");
        if (failed > 0) text += en ? $", {failed} previews not renewed" : $", {failed} Vorschaubilder nicht erneuert";
        return text;
    }

    internal static async Task<(int Changed, int Failed, bool Stopped)> RunAsync(UserSpace space, ServerConfig config, List<Photo> todo, Activity.Job? job, CancellationToken ct)
    {
        var db = space.Db;
        var changed = 0;
        var failed = 0;
        var done = 0;
        var stopped = false;
        var write = new Lock();
        try
        {
            // Zwei zugleich, wie die bearbeiteten Vorschaubilder (PhotoEdits) - die Engine rechnet sie
            await Parallel.ForEachAsync(todo, new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = ct }, async (p, token) =>
            {
                await JobControl.CheckAsync(space.User.Id, Activity.LensApply, token);
                var added = false;
                lock (write)
                {
                    // Inzwischen im Editor geändert? Dann bleibt es so
                    var before = PhotoEdits.Get(db, p.Key);
                    if (StateOf(before) == State.Todo) { PhotoEdits.Set(db, p.Key, WithLens(before)); added = true; }
                }
                if (added)
                {
                    Interlocked.Increment(ref changed);
                    try { if (await RenderAsync(space, config, p, token) == null && PhotoEngine.Configured) Interlocked.Increment(ref failed); }
                    catch (Exception e) when (e is IOException or KeyNotFoundException or UnauthorizedAccessException or InvalidOperationException or HttpRequestException or ArgumentException)
                    { Interlocked.Increment(ref failed); }
                }
                job?.Progress(Interlocked.Increment(ref done));
            });
        }
        catch (JobControl.Stopped) { stopped = true; }
        return (changed, failed, stopped);
    }

    public static void Map(RouteGroupBuilder api, WebApplication app)
    {
        _spaces = app.Services.GetRequiredService<Spaces>();
        _config = app.Services.GetRequiredService<ServerConfig>();
        _log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DmOffice.Gallery.LensApply");
        var stop = app.Lifetime.ApplicationStopping;
        _ = Task.Run(() => WorkAsync(stop));

        // Vorschläge für den Regeldialog: Kameras, Objektive und Brennweiten der eigenen Fotos mit Anzahl
        api.MapGet("/library", async (HttpContext http, ServerConfig config) => Results.Ok(new { items = Library(await GalleryTimeline.AllAsync(http, config)) }));

        // Vorab: wie viele Fotos je Regel betroffen sind
        api.MapGet("/apply", async (string? rule, HttpContext http, ServerConfig config) =>
        {
            var space = http.Space();
            var edits = PhotoEdits.All(space.Db);
            var list = Candidates(await GalleryTimeline.AllAsync(http, config), k => edits.GetValueOrDefault(k), rule, http.Space().User.Id);
            return Results.Ok(new
            {
                rules = list.GroupBy(p => p.Rule).Select(g => new
                {
                    id = g.Key, todo = g.Count(p => p.State == State.Todo), done = g.Count(p => p.State == State.Done), off = g.Count(p => p.State == State.Off),
                }),
                todo = list.Count(p => p.State == State.Todo),
                running = Running.ContainsKey(space.User.Id),
                engine = PhotoEngine.Configured,
            });
        });

        // Anwenden: im Hintergrund, Fortschritt in der Aktivität
        api.MapPost("/apply", async (ApplyBody body, HttpContext http, ServerConfig config) =>
        {
            var space = http.Space();
            var edits = PhotoEdits.All(space.Db);
            var todo = Candidates(await GalleryTimeline.AllAsync(http, config), k => edits.GetValueOrDefault(k), string.IsNullOrEmpty(body.Rule) ? null : body.Rule, http.Space().User.Id)
                .Where(p => p.State == State.Todo).ToList();
            if (todo.Count == 0) return Results.Ok(new { started = false, count = 0 });
            if (!Running.TryAdd(space.User.Id, true)) return Results.Conflict(new { error = "Die Objektivkorrektur wird gerade schon eingetragen." });
            _ = Task.Run(async () =>
            {
                using var control = JobControl.Run(space.User.Id, Activity.LensApply);
                using var job = Activity.Start(space.User.Id, Activity.LensApply, null, todo.Count);
                try
                {
                    var (changed, failed, stopped) = await RunAsync(space, config, todo, job, CancellationToken.None);
                    job.Finish(Summary(changed, todo.Count, failed, stopped, body.Lang));
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    _log?.LogWarning("Objektivkorrektur anwenden für {User}: {Error}", space.User.Login, e.Message);
                    job.Fail(e.Message);
                }
                finally { Running.TryRemove(space.User.Id, out _); }
            });
            return Results.Accepted(value: new { started = true, count = todo.Count });
        });
    }
}
