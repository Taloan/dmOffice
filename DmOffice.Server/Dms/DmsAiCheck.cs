using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using DmOffice.Core;

namespace DmOffice.Dms;

/// <summary>
/// KI-Prüfung im DMS (Kennung dmsaicheck): Zuordnungen prüfen (gehören die Dokumente eines Eintrags wirklich dorthin?) und Dokumente
/// ohne Zuordnung einordnen - nur in vorhandene Einträge oder auch mit neuen Vorschlägen. Nichts geschieht automatisch: die Ergebnisse
/// landen als Vorschläge in einer Liste; übernommen oder verworfen wird von Hand (einzeln oder alle markierten), auf Wunsch gleich mit
/// „Nach Vorgabe ablegen“. Übernommene Änderungen zählen als geprüft (wie eine Änderung von Hand); verworfene Vorschläge kommen bei
/// späteren Läufen nicht wieder. Nur der offene Bereich - der Tresor geht nie an die KI. Läuft im Hintergrund, höchstens 200 Dokumente
/// je Lauf, in Paketen zu acht; an die KI gehen Titel, Angaben und ein Auszug des Texts (1500 Zeichen).
/// </summary>
public static class DmsAiCheck
{
    public const string Check = "check", Assign = "assign";
    public const string Existing = "existing", AllowNew = "new";
    public const string Fits = "fits", Doubt = "doubt", Wrong = "wrong";
    public const int MaxDocs = 200, Batch = 8, TextChars = 1500;

    public sealed record Run(long Id, string Kind, string Field, string? Entry, string Mode, string State, int Total, int Done, DateTime Created, string? Error);
    public sealed record Suggestion(long Id, long Run, long Doc, string Field, string? Current, string? Verdict, string? ProposalEntry, string? ProposalNew,
        string? ProposalParent, int Confidence, string? Reason, string State);

    private static readonly ConcurrentDictionary<long, CancellationTokenSource> Running = new();

    public static void Ensure(UserDb db)
    {
        Dms.Ensure(db);
        db.Exec(@"CREATE TABLE IF NOT EXISTS dms_ai_runs (id INTEGER PRIMARY KEY, kind TEXT NOT NULL, field TEXT NOT NULL, entry TEXT NULL, mode TEXT NOT NULL,
            state TEXT NOT NULL, total INTEGER NOT NULL DEFAULT 0, done INTEGER NOT NULL DEFAULT 0, created INTEGER NOT NULL, error TEXT NULL);
            CREATE TABLE IF NOT EXISTS dms_ai_sugg (id INTEGER PRIMARY KEY, run INTEGER NOT NULL, doc INTEGER NOT NULL, field TEXT NOT NULL, current TEXT NULL,
            verdict TEXT NULL, entry TEXT NULL, new_name TEXT NULL, parent TEXT NULL, confidence INTEGER NOT NULL DEFAULT 0, reason TEXT NULL, state TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS dms_ai_sugg_run ON dms_ai_sugg(run);
            CREATE INDEX IF NOT EXISTS dms_ai_sugg_doc ON dms_ai_sugg(doc, field);");
    }

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public static List<Run> Runs(UserDb db)
    {
        Ensure(db);
        return db.Query("SELECT id, kind, field, entry, mode, state, total, done, created, error FROM dms_ai_runs ORDER BY id DESC LIMIT 30",
            r => new Run(r.GetInt64(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4), r.GetString(5), r.GetInt32(6), r.GetInt32(7),
                DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(8)).UtcDateTime, r.IsDBNull(9) ? null : r.GetString(9)));
    }

    /// <summary>Offene Vorschläge (ohne „passt“ bei Prüfläufen - die brauchen keine Entscheidung), neueste Läufe zuerst.</summary>
    public static List<Suggestion> Open(UserDb db, long? run = null)
    {
        Ensure(db);
        return db.Query("SELECT id, run, doc, field, current, verdict, entry, new_name, parent, confidence, reason, state FROM dms_ai_sugg " +
            "WHERE state = 'open' AND COALESCE(verdict, '') <> 'fits'" + (run != null ? " AND run = $r" : "") + " ORDER BY run DESC, confidence DESC, id",
            Read, ("$r", run));
    }

    private static Suggestion Read(Microsoft.Data.Sqlite.SqliteDataReader r) => new(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3),
        r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7),
        r.IsDBNull(8) ? null : r.GetString(8), r.GetInt32(9), r.IsDBNull(10) ? null : r.GetString(10), r.GetString(11));

    private static List<Suggestion> ByIds(UserDb db, IEnumerable<long> ids)
    {
        Ensure(db);
        var list = new List<Suggestion>();
        foreach (var id in ids.Distinct())
            list.AddRange(db.Query("SELECT id, run, doc, field, current, verdict, entry, new_name, parent, confidence, reason, state FROM dms_ai_sugg WHERE id = $i", Read, ("$i", id)));
        return list;
    }

    /// <summary>Die Dokumente eines Laufs: bei „prüfen“ die mit dem Eintrag, bei „einordnen“ die ohne Zuordnung im Feld.</summary>
    public static List<DmsDoc> Candidates(UserDb db, string kind, string field, string? entry)
    {
        var f = kind == Check
            ? field switch
            {
                Dms.Correspondent => new Dms.Filter(Correspondent: entry),
                Dms.Doctype => new Dms.Filter(Doctype: entry),
                _ => new Dms.Filter(Tags: [entry!]),
            }
            : field switch
            {
                Dms.Correspondent => new Dms.Filter(NoCorrespondent: true),
                Dms.Doctype => new Dms.Filter(NoDoctype: true),
                _ => new Dms.Filter(NoTags: true),
            };
        return Dms.Docs(db, f, 0, MaxDocs).Items.Where(d => d.State == "done").ToList();
    }

    /// <summary>Lauf anlegen und im Hintergrund starten. Gibt die Nummer des Laufs zurück.</summary>
    public static long Start(UserSpace space, IDmsAi ai, string kind, string field, string? entry, string mode)
    {
        var db = space.Db;
        Ensure(db);
        if (kind is not (Check or Assign)) throw new ArgumentException("Unbekannte Art.");
        if (field is not (Dms.Correspondent or Dms.Doctype or Dms.Tag)) throw new ArgumentException("Nur Korrespondent, Dokumenttyp oder Tags.");
        if (kind == Check && Dms.Entries(db).All(e => e.Id != entry || e.Kind != field)) throw new ArgumentException("Diesen Eintrag gibt es nicht.");
        if (!ai.Allowed(space)) throw new ArgumentException("Die KI ist nicht eingerichtet oder für dich nicht freigegeben.");
        if (Runs(db).Any(r => r.State == "running")) throw new ArgumentException("Es läuft schon eine KI-Prüfung. Bitte warten oder abbrechen.");
        var docs = Candidates(db, kind, field, entry);
        if (docs.Count == 0) throw new ArgumentException("Keine Dokumente zum Prüfen.");
        db.Exec("INSERT INTO dms_ai_runs(kind, field, entry, mode, state, total, created) VALUES($k, $f, $e, $m, 'running', $t, $c)",
            ("$k", kind), ("$f", field), ("$e", entry), ("$m", mode == AllowNew && kind == Assign ? AllowNew : Existing), ("$t", docs.Count), ("$c", Now));
        var id = Convert.ToInt64(db.Scalar("SELECT last_insert_rowid()"));
        var cts = new CancellationTokenSource();
        Running[id] = cts;
        _ = Task.Run(async () =>
        {
            try { await ProcessAsync(space, ai, id, kind, field, entry, mode == AllowNew && kind == Assign, docs, cts.Token); db.Exec("UPDATE dms_ai_runs SET state = 'done' WHERE id = $i AND state = 'running'", ("$i", id)); }
            catch (OperationCanceledException) { db.Exec("UPDATE dms_ai_runs SET state = 'cancelled' WHERE id = $i", ("$i", id)); }
            catch (Exception e) { db.Exec("UPDATE dms_ai_runs SET state = 'failed', error = $e WHERE id = $i", ("$e", e.Message), ("$i", id)); }
            finally { Running.TryRemove(id, out _); }
        });
        return id;
    }

    public static void Cancel(long run)
    {
        if (Running.TryGetValue(run, out var cts)) cts.Cancel();
    }

    private const string CheckSystem =
        "Du prüfst Zuordnungen in einem Dokumentenarchiv. Du bekommst einen Eintrag (Korrespondent, Dokumenttyp oder Tag) und Dokumente, " +
        "denen er zugeordnet ist, mit Titel, Angaben und einem Textauszug. Beurteile je Dokument, ob der Eintrag passt: \"fits\" (passt), " +
        "\"doubt\" (zweifelhaft) oder \"wrong\" (passt nicht). Passt er nicht, nenne unter \"better\" die Nummer eines besser passenden " +
        "vorhandenen Eintrags derselben Liste oder null. Sei zurückhaltend: \"wrong\" nur, wenn klar erkennbar. Antworte nur mit einem " +
        "JSON-Array: [{\"doc\": Dokumentnummer, \"verdict\": \"fits|doubt|wrong\", \"better\": Nummer oder null, \"confidence\": 0-100, " +
        "\"reason\": \"kurzer Grund auf Deutsch\"}].";

    private const string AssignSystem =
        "Du ordnest Dokumente in einem Dokumentenarchiv ein. Du bekommst eine Liste (Korrespondenten = wer hat geschrieben, Dokumenttypen = " +
        "was ist es, Tags = Themen) mit Nummern und Dokumente ohne Eintrag in dieser Liste. Wähle je Dokument den passenden vorhandenen " +
        "Eintrag über seine Nummer, oder null, wenn keiner passt - nichts erzwingen. {NEU}Antworte nur mit einem JSON-Array: " +
        "[{\"doc\": Dokumentnummer, \"entry\": Nummer oder null, \"new\": {NEUFELD}, \"parent\": Nummer oder null, \"confidence\": 0-100, " +
        "\"reason\": \"kurzer Grund auf Deutsch\"}].";

    private static async Task ProcessAsync(UserSpace space, IDmsAi ai, long run, string kind, string field, string? entry, bool allowNew, List<DmsDoc> docs, CancellationToken ct)
    {
        var db = space.Db;
        var all = Dms.Entries(db);
        var list = all.Where(e => e.Kind == field).ToList();
        var numbered = list.Select((e, i) => (No: i + 1, Entry: e)).ToList();
        var listText = string.Join("\n", numbered.Select(x => $"{x.No}: {string.Join(" › ", Dms.PathOf(all, x.Entry.Id))}"));
        string Name(string? id) => id == null ? "" : string.Join(" › ", Dms.PathOf(all, id));
        var dropped = db.Query("SELECT doc, COALESCE(entry, '') || '|' || COALESCE(new_name, '') FROM dms_ai_sugg WHERE state = 'dropped' AND field = $f",
            r => (r.GetInt64(0), r.GetString(1)), ("$f", field)).ToHashSet();
        var system = kind == Check ? CheckSystem
            : AssignSystem.Replace("{NEU}", allowNew
                ? "Passt kein vorhandener Eintrag, darfst du unter \"new\" einen kurzen neuen Namen vorschlagen und unter \"parent\" die Nummer des passenden Oberbegriffs (oder null). "
                : "Neue Einträge sind nicht erlaubt; \"new\" ist immer null. ").Replace("{NEUFELD}", allowNew ? "\"Name\" oder null" : "null");
        var label = field == Dms.Correspondent ? "Korrespondenten" : field == Dms.Doctype ? "Dokumenttypen" : "Tags";
        var done = 0;
        foreach (var chunk in docs.Chunk(Batch))
        {
            ct.ThrowIfCancellationRequested();
            var sb = new StringBuilder();
            sb.Append($"Liste {label}:\n{listText}\n\n");
            if (kind == Check) sb.Append($"Zu prüfender Eintrag: {Name(entry)} (Nummer {numbered.FirstOrDefault(x => x.Entry.Id == entry).No})\n\n");
            foreach (var d in chunk)
            {
                var text = (Dms.Text(db, d.Id) ?? "").Replace("\r", "");
                if (text.Length > TextChars) text = text[..TextChars];
                sb.Append($"--- Dokument {d.Id}\nTitel: {d.Title}\nDatei: {d.OriginalName}\nDatum: {d.DocDate}\n")
                  .Append($"Korrespondent: {Name(d.Correspondent)}\nDokumenttyp: {Name(d.Doctype)}\nTags: {string.Join(", ", d.Tags.Select(t => Name(t.Id)))}\n")
                  .Append($"Text:\n{text}\n\n");
            }
            var answer = await ai.WriteAsync(system, sb.ToString(), ct);
            foreach (var item in Parse(answer))
            {
                if (chunk.FirstOrDefault(d => d.Id == item.Doc) is not { } d) continue;
                string? Pick(int? n) => n is int x && numbered.FirstOrDefault(y => y.No == x) is { Entry: { } e } ? e.Id : null;
                if (kind == Check)
                {
                    var verdict = item.Verdict is Fits or Doubt or Wrong ? item.Verdict : Doubt;
                    var better = Pick(item.Better);
                    if (better == entry) better = null;
                    if (dropped.Contains((d.Id, (better ?? "") + "|"))) continue;
                    Insert(db, run, d.Id, field, entry, verdict, better, null, null, item.Confidence, item.Reason);
                }
                else
                {
                    var target = Pick(item.Entry);
                    var newName = allowNew && target == null && item.New is { Length: > 0 and <= 120 } nn ? nn.Trim() : null;
                    if (target == null && newName == null) continue;
                    if (dropped.Contains((d.Id, (target ?? "") + "|" + (newName ?? "")))) continue;
                    Insert(db, run, d.Id, field, null, null, target, newName, newName != null ? Pick(item.Parent) : null, item.Confidence, item.Reason);
                }
            }
            done += chunk.Length;
            db.Exec("UPDATE dms_ai_runs SET done = $d WHERE id = $i", ("$d", done), ("$i", run));
        }
    }

    private static void Insert(UserDb db, long run, long doc, string field, string? current, string? verdict, string? entry, string? newName, string? parent, int confidence, string? reason) =>
        db.Exec("INSERT INTO dms_ai_sugg(run, doc, field, current, verdict, entry, new_name, parent, confidence, reason, state) VALUES($r, $d, $f, $c, $v, $e, $n, $p, $co, $re, 'open')",
            ("$r", run), ("$d", doc), ("$f", field), ("$c", current), ("$v", verdict), ("$e", entry), ("$n", newName), ("$p", parent),
            ("$co", Math.Clamp(confidence, 0, 100)), ("$re", reason is { Length: > 300 } ? reason[..300] : reason));

    internal sealed record Item(long Doc, string? Verdict, int? Better, int? Entry, string? New, int? Parent, int Confidence, string? Reason);

    /// <summary>Antwort der KI lesen (JSON-Array, auch mit Text drumherum); Unlesbares fällt weg.</summary>
    internal static List<Item> Parse(string? answer)
    {
        var list = new List<Item>();
        if (answer == null) return list;
        var start = answer.IndexOf('[');
        var end = answer.LastIndexOf(']');
        if (start < 0 || end <= start) return list;
        try
        {
            using var doc = JsonDocument.Parse(answer[start..(end + 1)]);
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                int? Num(string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
                string? Str(string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s && s != "null" ? s : null;
                if (!(e.TryGetProperty("doc", out var dv) && dv.ValueKind == JsonValueKind.Number && dv.TryGetInt64(out var docId))) continue;
                list.Add(new Item(docId, Str("verdict"), Num("better"), Num("entry"), Str("new"), Num("parent"), Num("confidence") ?? 50, Str("reason")));
            }
        }
        catch (JsonException) { }
        return list;
    }

    /// <summary>Vorschlag vor dem Übernehmen ändern: anderer vorhandener Eintrag, neuer Name (mit Oberbegriff) oder „keiner“ (bei Prüfung: nur entfernen).</summary>
    public static void Edit(UserDb db, long id, string? entry, string? newName, string? parent)
    {
        Ensure(db);
        db.Exec("UPDATE dms_ai_sugg SET entry = $e, new_name = $n, parent = $p WHERE id = $i AND state = 'open'",
            ("$e", string.IsNullOrEmpty(entry) ? null : entry), ("$n", string.IsNullOrWhiteSpace(newName) ? null : newName.Trim()), ("$p", string.IsNullOrEmpty(parent) ? null : parent), ("$i", id));
    }

    public static int Drop(UserDb db, IEnumerable<long> ids)
    {
        Ensure(db);
        var n = 0;
        foreach (var id in ids.Distinct()) { db.Exec("UPDATE dms_ai_sugg SET state = 'dropped' WHERE id = $i AND state = 'open'", ("$i", id)); n++; }
        return n;
    }

    /// <summary>
    /// Übernehmen: Prüfung „passt nicht“ entfernt den Eintrag (und setzt den besseren, wenn vorgeschlagen); „zweifelhaft“ ebenso, wenn
    /// übernommen. Einordnen setzt den Eintrag, legt einen neuen erst jetzt an (gleiche Namen nur einmal). Danach wie eine Änderung von
    /// Hand (Ablage folgt dem Profil); mit <paramref name="refile"/> zusätzlich „Nach Vorgabe ablegen“. Gibt die geänderten Dokumente zurück.
    /// </summary>
    public static List<long> Apply(UserSpace space, IEnumerable<long> ids, bool refile, Action<long> refileOne)
    {
        var db = space.Db;
        var created = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var changed = new List<long>();
        foreach (var s in ByIds(db, ids).Where(x => x.State == "open"))
        {
            var doc = Dms.ByIds(db, [s.Doc]).FirstOrDefault();
            if (doc == null) { db.Exec("UPDATE dms_ai_sugg SET state = 'dropped' WHERE id = $i", ("$i", s.Id)); continue; }
            var target = s.ProposalEntry;
            if (target == null && s.ProposalNew != null)
            {
                var key = s.ProposalNew + "|" + s.ProposalParent;
                if (!created.TryGetValue(key, out var made))
                {
                    var existing = Dms.Entries(db, s.Field).FirstOrDefault(e => e.Parent == s.ProposalParent && string.Equals(e.Name, s.ProposalNew, StringComparison.OrdinalIgnoreCase));
                    made = existing?.Id ?? Dms.Create(db, s.Field, s.ProposalNew, s.ProposalParent, null, s.Field == Dms.Role ? Dms.None : Dms.Auto, null, false).Id;
                    created[key] = made;
                }
                target = made;
            }
            DmsApi.DocBody body;
            if (s.Verdict != null)
            {
                if (s.Verdict == Fits) { db.Exec("UPDATE dms_ai_sugg SET state = 'taken' WHERE id = $i", ("$i", s.Id)); continue; }
                body = s.Field == Dms.Tag
                    ? new DmsApi.DocBody(null, null, null, null, null, target != null ? [target] : null, s.Current != null ? [s.Current] : null, null)
                    : s.Field == Dms.Correspondent ? new DmsApi.DocBody(null, null, target ?? "", null, null, null, null, null)
                    : new DmsApi.DocBody(null, null, null, target ?? "", null, null, null, null);
            }
            else
            {
                if (target == null) continue;
                body = s.Field == Dms.Tag ? new DmsApi.DocBody(null, null, null, null, null, [target], null, null)
                    : s.Field == Dms.Correspondent ? new DmsApi.DocBody(null, null, target, null, null, null, null, null)
                    : new DmsApi.DocBody(null, null, null, target, null, null, null, null);
            }
            DmsApi.Change(space, [s.Doc], body);
            db.Exec("UPDATE dms_ai_sugg SET state = 'taken' WHERE id = $i", ("$i", s.Id));
            changed.Add(s.Doc);
        }
        if (refile) foreach (var d in changed.Distinct()) refileOne(d);
        return changed.Distinct().ToList();
    }
}
