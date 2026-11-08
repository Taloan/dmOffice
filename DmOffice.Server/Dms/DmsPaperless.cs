using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using DmOffice.Core;

namespace DmOffice.Dms;

/// <summary>
/// Übernahme aus paperless-ngx (docs/dms-konzept.md, „Übernahme aus paperless-ngx“): ein Export von <c>document_exporter</c>
/// (Originale, Archivfassungen, manifest.json) aus der eigenen Ablage oder einem externen Ordner. Die Listen kommen mit Namen,
/// Farbe und Zuweisung; Korrespondent, Dokumenttyp und Tags der Dokumente bestimmt das DMS selbst - aus dem, was es aus den
/// anderen Dokumenten des Exports lernt (Kreuzvalidierung, nichts davon landet im Gelernten) -, die Angaben aus paperless
/// bleiben zum Vergleich. Gelernt wird erst aus dem, was im Posteingang bestätigt wird.
/// </summary>
public static class DmsPaperless
{
    public const string MapKey = "dms.paperless.map";

    /// <summary>Ein Eintrag einer paperless-Liste. Key: „correspondent:12“; Existing: gleichnamiger Eintrag im DMS.</summary>
    public sealed record PlEntry(string Key, string Kind, int Pk, string Name, int? Parent, string? Color, string Match, string? MatchText,
        bool CaseSensitive, bool InboxTag, string? Note, string? Existing = null, string? Mapped = null);

    public sealed record PlDoc(int Pk, string Title, string Content, string? Created, DateTime? Added, int? Correspondent, int? Doctype, List<int> Tags,
        string? Original, string? Archive, string OriginalName, string? Mime);

    public sealed record Export(List<PlEntry> Entries, List<PlDoc> Docs, int Missing);

    // ---------------- Einlesen ----------------

    /// <summary>manifest.json (und bei --split-manifest die einzelnen *-manifest.json) lesen.</summary>
    public static async Task<Export> ReadAsync(UserSpace space, IDmsSources sources, string? mount, string folder, CancellationToken ct)
    {
        var (files, _) = await sources.ListAsync(space, mount, folder, ct);
        var names = files.ToDictionary(f => Rel(folder, f.Path), f => f, StringComparer.Ordinal);
        var manifests = names.Keys.Where(k => k == "manifest.json" || k.EndsWith("-manifest.json", StringComparison.Ordinal)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        if (!manifests.Contains("manifest.json")) throw new ArgumentException("In diesem Ordner liegt kein Export von paperless-ngx (manifest.json fehlt).");
        if (names.ContainsKey("metadata.json"))
        {
            await using var m = await sources.OpenReadAsync(space, mount, Storage.StoragePath.Join(Storage.StoragePath.Clean(folder), "metadata.json"), ct);
            using var meta = await ParseAsync(m, "metadata.json", ct);
            if (meta.RootElement.ValueKind == JsonValueKind.Object && (meta.RootElement.TryGetProperty("encryption", out _) || meta.RootElement.TryGetProperty("crypto", out _)))
                throw new ArgumentException("Verschlüsselte Exporte von paperless-ngx werden nicht unterstützt.");
        }
        var entries = new List<PlEntry>();
        var docs = new List<PlDoc>();
        int missing = 0;
        foreach (var name in manifests)
        {
            await using var s = await sources.OpenReadAsync(space, mount, Storage.StoragePath.Join(Storage.StoragePath.Clean(folder), name), ct);
            using var json = await ParseAsync(s, name, ct);
            if (json.RootElement.ValueKind != JsonValueKind.Array) continue;
            foreach (var o in json.RootElement.EnumerateArray())
            {
                if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty("model", out var model) || !o.TryGetProperty("fields", out var f) || f.ValueKind != JsonValueKind.Object) continue;
                var pk = Int(o, "pk") ?? 0;
                var kind = Str(o, "model") switch
                {
                    "documents.correspondent" => Dms.Correspondent,
                    "documents.documenttype" => Dms.Doctype,
                    "documents.tag" => Dms.Tag,
                    "documents.document" => "doc",
                    _ => null,
                };
                if (kind == null) continue;
                if (kind == "doc")
                {
                    var file = Str(o, "__exported_file_name__");
                    var archive = Str(o, "__exported_archive_name__");
                    if (file?.EndsWith(".gpg", StringComparison.OrdinalIgnoreCase) == true)
                        throw new ArgumentException("Verschlüsselte Exporte von paperless-ngx werden nicht unterstützt.");
                    if (file == null || !names.ContainsKey(file)) { missing++; file = null; }
                    if (archive != null && !names.ContainsKey(archive)) archive = null;
                    var tags = f.TryGetProperty("tags", out var tg) && tg.ValueKind == JsonValueKind.Array
                        ? tg.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out _)).Select(x => x.GetInt32()).ToList() : [];
                    var created = Str(f, "created") is { Length: >= 10 } c ? c[..10] : null;
                    DateTime? added = Str(f, "added") is { } a && DateTimeOffset.TryParse(a, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ad) ? ad.UtcDateTime : null;
                    docs.Add(new PlDoc(pk, Str(f, "title") ?? "", Str(f, "content") ?? "", created, added, Int(f, "correspondent"), Int(f, "document_type"), tags,
                        file, archive, Str(f, "original_filename") ?? Path.GetFileName(file ?? "Dokument"), Str(f, "mime_type")));
                    continue;
                }
                var algo = Int(f, "matching_algorithm") ?? 0;
                var text = Str(f, "match");
                var (match, note) = algo switch
                {
                    1 => (Dms.Any, (string?)null),
                    2 => (Dms.All, null),
                    3 => (Dms.Exact, null),
                    4 => (Dms.Regex, null),
                    5 => (Dms.Any, "Unscharf wird zu „Ein Wort“."),
                    6 => (Dms.Auto, null),
                    _ => (Dms.None, null),
                };
                if (match is Dms.Any or Dms.All or Dms.Exact or Dms.Regex && string.IsNullOrWhiteSpace(text)) (match, note) = (Dms.None, null);
                if (match == Dms.Regex)
                    try { _ = new System.Text.RegularExpressions.Regex(text!, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1)); }
                    catch (ArgumentException) { (match, note) = (Dms.None, "Der reguläre Ausdruck ist ungültig."); }
                var inbox = kind == Dms.Tag && f.TryGetProperty("is_inbox_tag", out var ib) && ib.ValueKind == JsonValueKind.True;
                entries.Add(new PlEntry($"{kind}:{pk}", kind, pk, (Str(f, "name") ?? "").Trim(), Int(f, "parent") ?? Int(f, "tn_parent"),
                    kind == Dms.Tag ? Str(f, "color") : null, match, match is Dms.Auto or Dms.None ? null : text,
                    !(f.TryGetProperty("is_insensitive", out var ins) && ins.ValueKind == JsonValueKind.True), inbox,
                    inbox ? "Inbox-Tag – im DMS übernimmt das die Inbox." : note));
            }
        }
        // Schon vorhandene Einträge (gleicher Name und Oberbegriff) und frühere Zuordnung
        var have = Dms.Entries(space.Db);
        var map = space.Db.GetJson<Dictionary<string, string>>(MapKey);
        entries = entries.Where(e => e.Name.Length > 0).Select(e => e with
        {
            Existing = have.FirstOrDefault(h => h.Kind == e.Kind && h.Name.Equals(e.Name, StringComparison.CurrentCultureIgnoreCase))?.Id,
            Mapped = map.TryGetValue(e.Key, out var id) && have.Any(h => h.Id == id) ? id : null,
        }).OrderBy(e => Array.IndexOf(Dms.Kinds, e.Kind)).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        return new Export(entries, docs.OrderBy(d => d.Added ?? DateTime.MinValue).ToList(), missing);
    }

    private static async Task<JsonDocument> ParseAsync(Stream data, string name, CancellationToken ct)
    {
        try { return await JsonDocument.ParseAsync(data, cancellationToken: ct); }
        catch (JsonException) { throw new ArgumentException($"„{name}“ lässt sich nicht lesen. Der Export ist beschädigt."); }
    }

    private static string Rel(string folder, string path)
    {
        var f = Storage.StoragePath.Clean(folder);
        return f.Length > 0 && path.StartsWith(f + "/", StringComparison.Ordinal) ? path[(f.Length + 1)..] : path;
    }

    private static string? Str(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int? Int(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    // ---------------- Listen ----------------

    /// <summary>
    /// Listen übernehmen: je Eintrag einen im DMS anlegen oder den gleichnamigen nehmen (Oberbegriffe zuerst). Posteingang-Tags und
    /// abgewählte Einträge (skip) nicht. Gibt die Zuordnung paperless → DMS zurück (auch gespeichert).
    /// </summary>
    public static Dictionary<string, string> ApplyLists(UserSpace space, Export export, ICollection<string> skip)
    {
        var db = space.Db;
        var map = db.GetJson<Dictionary<string, string>>(MapKey);
        var byKey = export.Entries.ToDictionary(e => e.Key);
        // Einträge, die sich im Kreis als Oberbegriff nennen, stehen dort ohne weiteren Oberbegriff, statt sich zu wiederholen
        var open = new HashSet<string>();
        string? Ensure(PlEntry e, int depth)
        {
            if (map.TryGetValue(e.Key, out var known) && Dms.Entries(db, e.Kind).Any(x => x.Id == known)) return known;
            if (skip.Contains(e.Key) || e.InboxTag || depth > 20 || !open.Add(e.Key)) return null;
            string? parent = e.Parent is { } p && byKey.TryGetValue($"{e.Kind}:{p}", out var pe) ? Ensure(pe, depth + 1) : null;
            var existing = Dms.Entries(db, e.Kind).FirstOrDefault(x => x.Parent == parent && x.Name.Equals(e.Name, StringComparison.CurrentCultureIgnoreCase));
            var id = existing?.Id ?? Dms.Create(db, e.Kind, e.Name.Length > 120 ? e.Name[..120] : e.Name, parent, e.Color, e.Match, e.MatchText, e.CaseSensitive).Id;
            map[e.Key] = id;
            open.Remove(e.Key);
            return id;
        }
        foreach (var e in export.Entries) Ensure(e, 0);
        db.SetJson(MapKey, map);
        return map;
    }

    // ---------------- Lernen im Speicher ----------------

    /// <summary>Gezählte Merkmale wie dms_tokens/dms_trained, nur im Speicher (für Probelauf und Vorschläge).</summary>
    private sealed class Model
    {
        public readonly Dictionary<string, Dictionary<string, (int Pos, int Neg)>> ByToken = new();
        public readonly Dictionary<string, (int Pos, int Neg)> Trained = new();

        public void Add(HashSet<string> tokens, IEnumerable<string> entries)
        {
            foreach (var e in entries.Distinct())
            {
                Trained[e] = Trained.TryGetValue(e, out var t) ? (t.Pos + 1, t.Neg) : (1, 0);
                foreach (var tok in tokens)
                {
                    if (!ByToken.TryGetValue(tok, out var per)) ByToken[tok] = per = new();
                    per[e] = per.TryGetValue(e, out var c) ? (c.Pos + 1, c.Neg) : (1, 0);
                }
            }
        }
    }

    /// <summary>Angaben eines Dokuments im DMS-Kennungen (über die Zuordnung).</summary>
    private sealed record Labels(string? Correspondent, string? Doctype, List<string> Tags);

    private static Labels LabelsOf(PlDoc d, Dictionary<string, string> map) => new(
        d.Correspondent is { } c && map.TryGetValue($"{Dms.Correspondent}:{c}", out var ci) ? ci : null,
        d.Doctype is { } t && map.TryGetValue($"{Dms.Doctype}:{t}", out var ti) ? ti : null,
        d.Tags.Select(g => map.TryGetValue($"{Dms.Tag}:{g}", out var gi) ? gi : null).OfType<string>().Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList());

    /// <summary>Gelernt wird wie beim Bestätigen: der Eintrag und seine Oberbegriffe gehören dazu (nur Einträge mit „Automatisch“).</summary>
    private static IEnumerable<string> Positive(List<DmsEntry> all, Labels l)
    {
        var byId = all.ToDictionary(e => e.Id);
        foreach (var e in new[] { l.Correspondent, l.Doctype }.Concat(l.Tags).OfType<string>())
            foreach (var x in Dms.Above(all, e).Prepend(e))
                if (byId.TryGetValue(x, out var en) && en.Match == Dms.Auto) yield return x;
    }

    private static HashSet<string> TokensOf(PlDoc d) => DmsAssign.Tokens(d.Content, d.OriginalName, null);

    /// <summary>Was DMS für ein Dokument vorschlägt, gelernt aus <paramref name="model"/> (Regeln wie sonst auch).</summary>
    private static Labels Propose(List<DmsEntry> all, Model model, PlDoc d, HashSet<string> tokens)
    {
        var text = d.Content + "\n" + Path.GetFileNameWithoutExtension(d.OriginalName);
        var r = DmsAssign.DecideWith(all, text, null, null, DmsAssign.Probabilities(all, tokens, model.ByToken, model.Trained));
        return new Labels(r.Correspondent, r.Doctype, r.Tags.OrderBy(x => x, StringComparer.Ordinal).ToList());
    }

    /// <summary>Vorschläge für alle Dokumente: in fünf Teilen, jeder aus den anderen vier gelernt.</summary>
    private static Dictionary<int, Labels> CrossProposals(List<DmsEntry> all, List<PlDoc> docs, Dictionary<string, string> map, int folds = 5)
    {
        var tokens = docs.ToDictionary(d => d.Pk, TokensOf);
        var labels = docs.ToDictionary(d => d.Pk, d => LabelsOf(d, map));
        var result = new Dictionary<int, Labels>();
        for (var k = 0; k < folds; k++)
        {
            var model = new Model();
            for (var i = 0; i < docs.Count; i++)
                if (i % folds != k) model.Add(tokens[docs[i].Pk], Positive(all, labels[docs[i].Pk]));
            for (var i = k; i < docs.Count; i += folds) result[docs[i].Pk] = Propose(all, model, docs[i], tokens[docs[i].Pk]);
        }
        return result;
    }

    // ---------------- Probelauf ----------------

    public sealed record ListStat(string Kind, int Same, int Different, int Empty, int Total);
    public sealed record EntryStat(string Kind, string Id, string Name, int Same, int Different, int Empty, int Total);
    public sealed record Difference(int Pk, string Title, string Kind, string? Paperless, string? Dms);
    public sealed record Trial(int Learned, int Tested, List<ListStat> Lists, List<EntryStat> Entries, List<Difference> Differences);

    /// <summary>
    /// Probelauf, ohne etwas abzulegen oder zu lernen: Das DMS lernt aus einem zufälligen Teil (<paramref name="share"/>) der Dokumente mit
    /// den Angaben aus paperless und ordnet den Rest ein. Ergebnis je Liste und je Eintrag: gleich, anders, leer.
    /// </summary>
    public static Trial RunTrial(UserSpace space, Export export, double share, int seed = 42)
    {
        var db = space.Db;
        var map = db.GetJson<Dictionary<string, string>>(MapKey);
        var all = Dms.Entries(db);
        var byId = all.ToDictionary(e => e.Id);
        var docs = export.Docs.Where(d => d.Content.Length > 0).ToList();
        var rnd = new Random(seed);
        var shuffled = docs.OrderBy(_ => rnd.Next()).ToList();
        var learnCount = (int)Math.Round(shuffled.Count * Math.Clamp(share, 0.1, 0.95));
        var model = new Model();
        foreach (var d in shuffled.Take(learnCount)) model.Add(TokensOf(d), Positive(all, LabelsOf(d, map)));

        var lists = Dms.Kinds.ToDictionary(k => k, _ => (Same: 0, Different: 0, Empty: 0, Total: 0));
        var entries = new Dictionary<(string Kind, string Id), (int Same, int Different, int Empty, int Total)>();
        var diffs = new List<Difference>();
        string? Name(string? id) => id != null && byId.TryGetValue(id, out var e) ? string.Join(" › ", Dms.PathOf(all, e.Id)) : null;
        void Count(string kind, string? pl, string? dms, PlDoc d)
        {
            if (pl == null && dms == null) return;
            var outcome = pl == dms ? 0 : dms == null ? 2 : 1;
            var l = lists[kind];
            lists[kind] = (l.Same + (outcome == 0 ? 1 : 0), l.Different + (outcome == 1 ? 1 : 0), l.Empty + (outcome == 2 ? 1 : 0), l.Total + 1);
            foreach (var id in new[] { pl, dms }.OfType<string>().Distinct())
            {
                // Je Eintrag: Dokumente, die ihn in paperless oder im DMS haben
                var key = (kind, id);
                var c = entries.GetValueOrDefault(key);
                entries[key] = (c.Same + (outcome == 0 ? 1 : 0), c.Different + (outcome == 1 ? 1 : 0), c.Empty + (outcome == 2 ? 1 : 0), c.Total + 1);
            }
            if (outcome != 0 && diffs.Count < 500) diffs.Add(new Difference(d.Pk, d.Title, kind, Name(pl), Name(dms)));
        }
        foreach (var d in shuffled.Skip(learnCount))
        {
            var pl = LabelsOf(d, map);
            var mine = Propose(all, model, d, TokensOf(d));
            Count(Dms.Correspondent, pl.Correspondent, mine.Correspondent, d);
            Count(Dms.Doctype, pl.Doctype, mine.Doctype, d);
            foreach (var t in pl.Tags.Union(mine.Tags))
                Count(Dms.Tag, pl.Tags.Contains(t) ? t : null, mine.Tags.Contains(t) ? t : null, d);
        }
        return new Trial(learnCount, shuffled.Count - learnCount,
            lists.Select(x => new ListStat(x.Key, x.Value.Same, x.Value.Different, x.Value.Empty, x.Value.Total)).ToList(),
            entries.Select(x => new EntryStat(x.Key.Kind, x.Key.Id, Name(x.Key.Id) ?? "?", x.Value.Same, x.Value.Different, x.Value.Empty, x.Value.Total))
                .OrderBy(x => Array.IndexOf(Dms.Kinds, x.Kind)).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            diffs);
    }

    // ---------------- Übernahme ----------------

    public sealed record Progress(int Total, int Done, int Duplicates, int Errors, bool Running, string? Error, string? Last);
    private static readonly ConcurrentDictionary<string, Progress> Runs = new();

    public static Progress? StatusOf(string userId) => Runs.TryGetValue(userId, out var p) ? p : null;

    /// <summary>Übernahme starten (im Hintergrund). Läuft schon eine: nichts.</summary>
    public static bool Start(UserSpace space, DmsHost host, string? mount, string folder, string? profile, CancellationToken stopping)
    {
        if (Runs.TryGetValue(space.User.Id, out var p) && p.Running) return false;
        Runs[space.User.Id] = new Progress(0, 0, 0, 0, true, null, null);
        _ = Task.Run(async () =>
        {
            try { await RunImportAsync(space, host, mount, folder, profile, stopping); }
            catch (Exception e) { Runs[space.User.Id] = (StatusOf(space.User.Id) ?? new(0, 0, 0, 0, false, null, null)) with { Running = false, Error = e.Message }; }
        }, CancellationToken.None);
        return true;
    }

    public static async Task RunImportAsync(UserSpace space, DmsHost host, string? mount, string folder, string? profile, CancellationToken ct)
    {
        var db = space.Db;
        var export = await ReadAsync(space, host, mount, folder, ct);
        var map = db.GetJson<Dictionary<string, string>>(MapKey);
        var all = Dms.Entries(db);
        var docs = export.Docs.Where(d => d.Original != null).ToList();
        var proposals = CrossProposals(all, docs, map);
        int done = 0, dup = 0, errors = 0;
        void Report(string? last, bool running = true) => Runs[space.User.Id] = new Progress(docs.Count, done, dup, errors, running, null, last);
        Report(null);
        var root = Storage.StoragePath.Clean(folder);
        foreach (var d in docs)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var pl = LabelsOf(d, map);
                var mine = proposals.GetValueOrDefault(d.Pk) ?? new Labels(null, null, []);
                var given = new DmsImport.Given(d.Title.Length > 0 ? d.Title : Path.GetFileNameWithoutExtension(d.OriginalName), d.Created, d.Added, d.Content,
                    mine.Correspondent, mine.Doctype, mine.Tags, pl.Correspondent, pl.Doctype, pl.Tags);
                Stream? archive = null;
                if (d.Archive != null) archive = await host.OpenReadAsync(space, mount, Storage.StoragePath.Join(root, d.Archive), ct);
                DmsImport.Result r;
                try
                {
                    await using var original = await host.OpenReadAsync(space, mount, Storage.StoragePath.Join(root, d.Original!), ct);
                    r = await DmsImport.ImportAsync(space, original, d.OriginalName, d.Mime,
                        new DmsImport.Info("paperless", $"paperless:{d.Pk}", Profile: profile, Given: given, Archive: archive), ct);
                }
                finally { if (archive != null) await archive.DisposeAsync(); }
                if (r.Duplicate) dup++; else done++;
            }
            catch (Exception e) when (e is IOException or ArgumentException or KeyNotFoundException or UnauthorizedAccessException or HttpRequestException or JsonException or Dav.QuotaExceededException)
            {
                errors++;
            }
            Report(d.Title);
        }
        Report(null, false);
        DmsJob.Kick();
    }

    // ---------------- Nach der Übernahme ----------------

    /// <summary>Im Posteingang alles bestätigen, was das DMS genauso eingeordnet hat wie paperless (lernt mit). Gibt die Zahl zurück.</summary>
    public static List<long> SameIds(UserDb db)
    {
        Dms.Ensure(db);
        return db.Query($"SELECT d.id FROM dms_docs d WHERE d.pl = 1 AND d.inbox = 1 AND d.state = 'done' AND NOT {Dms.PlDiffers}", r => r.GetInt64(0));
    }

    /// <summary>Die Angaben aus paperless übernehmen (als bestätigt).</summary>
    public static Dms.Change TakePaperless(DmsDoc d) => d.Paperless is not { } p
        ? new Dms.Change(Confirm: true)
        : new Dms.Change(Correspondent: p.Correspondent ?? "", Doctype: p.Doctype ?? "", Tags: p.Tags, Confirm: true);

    /// <summary>Angaben aus paperless verwerfen (nach der Prüfung).</summary>
    public static int Discard(UserDb db)
    {
        Dms.Ensure(db);
        return db.Exec("UPDATE dms_docs SET pl = 0, pl_correspondent = NULL, pl_doctype = NULL, pl_tags = NULL WHERE pl = 1");
    }
}
