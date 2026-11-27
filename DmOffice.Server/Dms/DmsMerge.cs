using DmOffice.Core;
using Microsoft.Data.Sqlite;

namespace DmOffice.Dms;

/// <summary>
/// Einträge einer Liste zusammenführen (Kennung dmsmerge): Korrespondenten, Dokumenttypen, Tags oder Rollen gehen in einem
/// Ziel auf. Die Dokumente der Quellen bekommen das Ziel, das Gelernte (Wörter und trainierte Dokumente) wird zum Ziel addiert,
/// Unterbegriffe rücken unter das Ziel, Vorgaben von Quellen und Mail-Quellen zeigen danach auf das Ziel, die Quellen werden
/// gelöscht. Je Datenbank in einer Transaktion: erst der Tresor (falls offen), dann die Benutzerdatenbank mit den Listen. Ist der
/// Tresor zu, holt er das Umhängen beim nächsten Öffnen nach (<see cref="CatchUp"/>). Die gelöschten Quellen bleiben in
/// dms_merged vermerkt: Nimmt man eine Änderung über die Versionen (dmshistory) zurück, entsteht der Eintrag wieder (<see cref="Revive"/>).
/// </summary>
public static class DmsMerge
{
    /// <summary>Für den geschlossenen Tresor nachzuholen (bewusst nicht unter „dms.“: dort ersetzt das Umhängen Kennungen in Vorgaben).</summary>
    public const string PendingKey = "dmsmerge.tresor";

    public sealed record Pending(string Target, List<string> Sources, string Change);

    public sealed record Plan(DmsEntry Target, List<DmsEntry> Sources);

    /// <summary>Ergebnis: Dokumente im offenen Bereich und im Tresor (null: Tresor zu bzw. nicht vorhanden), Tresor später.</summary>
    public sealed record Result(int Docs, int? TresorDocs, bool TresorLater, string Change);

    private const string MergedSchema = @"CREATE TABLE IF NOT EXISTS dms_merged (id TEXT PRIMARY KEY, kind TEXT NOT NULL, parent TEXT NULL, name TEXT NOT NULL,
    color TEXT NULL, match TEXT NOT NULL, match_text TEXT NULL, case_sensitive INTEGER NOT NULL, target TEXT NOT NULL, at INTEGER NOT NULL)";

    /// <summary>Prüft Ziel und Quellen: alle aus derselben Liste, mindestens eine Quelle.</summary>
    public static Plan Check(UserDb db, string? target, IEnumerable<string>? sources)
    {
        var all = Dms.Entries(db);
        var t = all.FirstOrDefault(e => e.Id == target) ?? throw new KeyNotFoundException("Eintrag nicht gefunden.");
        var src = (sources ?? []).Distinct().Where(s => s != t.Id)
            .Select(s => all.FirstOrDefault(e => e.Id == s) ?? throw new KeyNotFoundException("Eintrag nicht gefunden.")).ToList();
        if (src.Count == 0) throw new ArgumentException("Bitte mindestens zwei Einträge wählen.");
        if (src.Any(s => s.Kind != t.Kind)) throw new ArgumentException("Zusammenführen geht nur innerhalb einer Liste.");
        return new Plan(t, src);
    }

    private static (string Sql, (string, object?)[] Args) In(IReadOnlyList<string> ids, string prefix = "$s")
    {
        var names = ids.Select((_, i) => prefix + i).ToList();
        return ("(" + string.Join(",", names) + ")", ids.Select((x, i) => (names[i], (object?)x)).ToArray());
    }

    /// <summary>Dokumente des Bereichs, die eine der Quellen tragen (ohne wartende; deren Vorgaben ändert <see cref="Apply"/> mit).</summary>
    public static List<long> Affected(DmsStore db, IReadOnlyList<string> sources)
    {
        Dms.Ensure(db);
        var (inSql, args) = In(sources);
        return db.Query($@"SELECT id FROM dms_docs WHERE state <> 'queued' AND (correspondent IN {inSql} OR doctype IN {inSql} OR role IN {inSql})
            UNION SELECT g.doc FROM dms_doc_tags g JOIN dms_docs d ON d.id = g.doc WHERE d.state <> 'queued' AND g.tag IN {inSql}", r => r.GetInt64(0), args);
    }

    /// <summary>Dokumente und Gelerntes eines Bereichs umhängen (eine Transaktion); bei der Benutzerdatenbank auch Listen, Rollen und
    /// Vorgaben (lists = true). Gibt die Dokumente zurück, deren Ablage sich ändern kann.</summary>
    internal static List<long> Apply(DmsStore db, string target, IReadOnlyList<string> sources, string change, Plan? lists)
    {
        Dms.Ensure(db);
        var docs = Affected(db, sources);
        var history = Features.On(Features.DmsHistory) ? DmsHistory.Snapshot(db, docs) : null;
        var (inSql, args) = In(sources);
        Array.Resize(ref args, args.Length + 1);
        args[^1] = ("$t", target);

        // Gelerntes je Dokument: trägt ein Dokument Quelle und Ziel (oder zwei Quellen), zählte es nach dem Addieren doppelt.
        // Behalten wird eine Zeile (die des Ziels, sonst positiv vor negativ), die übrigen werden von den Wortzählern abgezogen.
        var trained = db.Query($"SELECT doc, entry, label FROM dms_trained WHERE entry IN {inSql} OR entry = $t",
            r => (Doc: r.GetInt64(0), Entry: r.GetString(1), Label: (int)r.GetInt64(2)), args).GroupBy(x => x.Doc).ToList();
        var keep = new Dictionary<long, int>();
        var extra = new List<(HashSet<string> Tokens, int Label)>();
        foreach (var g in trained)
        {
            var src = g.Where(x => x.Entry != target).ToList();
            if (src.Count == 0) continue;
            var own = g.FirstOrDefault(x => x.Entry == target);
            var label = own.Entry != null ? own.Label : src.Any(x => x.Label == 1) ? 1 : -1;
            keep[g.Key] = label;
            var dropped = own.Entry != null ? src : src.Except([src.First(x => x.Label == label)]).ToList();
            if (dropped.Count == 0) continue;
            var tokens = DmsAssign.Tokens(db, g.Key);
            foreach (var d in dropped) extra.Add((tokens, d.Label));
        }
        // Vorgaben wartender Dokumente (JSON mit Kennungen): Kennung ersetzen
        var presets = db.Query($"SELECT id, presets FROM dms_docs WHERE state = 'queued' AND presets IS NOT NULL", r => (r.GetInt64(0), r.GetString(1)))
            .Where(x => sources.Any(s => x.Item2.Contains('"' + s + '"'))).ToList();
        // Vergleichswerte aus paperless: sortierte Liste mit Komma
        var plTags = db.Query("SELECT id, pl_tags FROM dms_docs WHERE pl = 1 AND pl_tags IS NOT NULL AND pl_tags <> ''", r => (r.GetInt64(0), r.GetString(1)))
            .Where(x => x.Item2.Split(',').Any(sources.Contains)).ToList();

        using (var conn = db.Open())
        using (var tx = conn.BeginTransaction())
        {
            void X(string sql) => UserDb.Exec(conn, sql, args);
            // Wörter: Zähler der Quellen zum Ziel addieren, doppelt gezählte Dokumente abziehen
            X($@"INSERT INTO dms_tokens(token, entry, pos, neg) SELECT token, $t, SUM(pos), SUM(neg) FROM dms_tokens WHERE entry IN {inSql} GROUP BY token
                 ON CONFLICT(token, entry) DO UPDATE SET pos = pos + excluded.pos, neg = neg + excluded.neg");
            foreach (var (tokens, label) in extra)
                foreach (var tok in tokens)
                    UserDb.Exec(conn, label == 1 ? "UPDATE dms_tokens SET pos = MAX(0, pos - 1) WHERE token = $k AND entry = $t" : "UPDATE dms_tokens SET neg = MAX(0, neg - 1) WHERE token = $k AND entry = $t",
                        ("$k", tok), ("$t", target));
            X($"DELETE FROM dms_tokens WHERE entry IN {inSql}");
            X($"DELETE FROM dms_trained WHERE entry IN {inSql}");
            foreach (var (doc, label) in keep)
                UserDb.Exec(conn, "INSERT INTO dms_trained(doc, entry, label) VALUES($d, $t, $l) ON CONFLICT(doc, entry) DO UPDATE SET label = $l", ("$d", doc), ("$t", target), ("$l", label));

            // Angaben der Dokumente (Herkunft „automatisch“ bleibt, wie sie war)
            foreach (var col in new[] { "correspondent", "doctype", "role", "pl_correspondent", "pl_doctype" })
                X($"UPDATE dms_docs SET {col} = $t WHERE {col} IN {inSql}");
            // Tags: unbestätigt nur, wenn alle beteiligten unbestätigt waren; von Hand entfernt bleibt entfernt, solange das Ziel fehlt
            X($@"INSERT INTO dms_doc_tags(doc, tag, auto) SELECT doc, $t, MIN(auto) FROM dms_doc_tags WHERE tag IN {inSql} GROUP BY doc
                 ON CONFLICT(doc, tag) DO UPDATE SET auto = MIN(auto, excluded.auto)");
            X($"DELETE FROM dms_doc_tags WHERE tag IN {inSql}");
            X($"INSERT OR IGNORE INTO dms_doc_tags_off(doc, tag) SELECT doc, $t FROM dms_doc_tags_off WHERE tag IN {inSql} AND doc NOT IN (SELECT doc FROM dms_doc_tags WHERE tag = $t)");
            X($"DELETE FROM dms_doc_tags_off WHERE tag IN {inSql}");
            foreach (var (id, json) in presets)
                UserDb.Exec(conn, "UPDATE dms_docs SET presets = $p WHERE id = $d", ("$p", Replace(json, sources, target)), ("$d", id));
            foreach (var (id, list) in plTags)
                UserDb.Exec(conn, "UPDATE dms_docs SET pl_tags = $p WHERE id = $d",
                    ("$p", string.Join(",", list.Split(',').Select(x => sources.Contains(x) ? target : x).Distinct().OrderBy(x => x, StringComparer.Ordinal))), ("$d", id));

            if (lists != null) MergeLists(conn, db, lists, args, inSql);
            tx.Commit();
        }
        if (history != null) DmsHistory.Record(db, history, change);
        return docs;
    }

    private static string Replace(string json, IEnumerable<string> sources, string target) =>
        sources.Aggregate(json, (j, s) => j.Replace('"' + s + '"', '"' + target + '"'));

    /// <summary>Listen, Rollen und Vorgaben (nur Benutzerdatenbank, in derselben Transaktion).</summary>
    private static void MergeLists(SqliteConnection conn, DmsStore db, Plan p, (string, object?)[] args, string inSql)
    {
        var all = Dms.Entries(db.User);
        var t = p.Target;
        var sources = p.Sources.Select(s => s.Id).ToHashSet();
        UserDb.Exec(conn, MergedSchema);
        var now = Dms.Unix();
        foreach (var s in p.Sources)
            UserDb.Exec(conn, @"INSERT INTO dms_merged(id, kind, parent, name, color, match, match_text, case_sensitive, target, at) VALUES($i,$k,$p,$n,$c,$m,$x,$s,$t,$a)
                ON CONFLICT(id) DO UPDATE SET name = $n, parent = $p, target = $t, at = $a",
                ("$i", s.Id), ("$k", s.Kind), ("$p", s.Parent), ("$n", s.Name), ("$c", s.Color), ("$m", s.Match), ("$x", s.MatchText), ("$s", s.CaseSensitive ? 1 : 0),
                ("$t", t.Id), ("$a", now));
        // Liegt das Ziel unter einer Quelle, rückt es an deren Stelle (sonst hinge es unter sich selbst)
        var byId = all.ToDictionary(e => e.Id);
        var parent = t.Parent;
        while (parent != null && sources.Contains(parent)) parent = byId.GetValueOrDefault(parent)?.Parent;
        if (parent != t.Parent) UserDb.Exec(conn, "UPDATE dms_lists SET parent = $p WHERE id = $t", ("$p", parent), ("$t", t.Id));
        UserDb.Exec(conn, $"UPDATE dms_lists SET parent = $t WHERE parent IN {inSql} AND id <> $t", args);
        // Zuweisung: gleiche Art aus Wörtern bzw. Absendern - die Wörter vereinen (sonst legte etwa die nächste Mail den Absender neu an)
        if (t.Match is Dms.Any or Dms.Sender && p.Sources.Where(s => s.Match == t.Match && s.MatchText is { Length: > 0 }).ToList() is { Count: > 0 } same)
        {
            var words = new[] { t.MatchText ?? "" }.Concat(same.Select(s => s.MatchText!))
                .SelectMany(m => m.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            UserDb.Exec(conn, "UPDATE dms_lists SET match_text = $m WHERE id = $t", ("$m", string.Join(" ", words)), ("$t", t.Id));
        }
        // Rollen: zu jeder Rolle genau ein geltendes Dokument - das zuletzt zugeordnete gilt, die anderen sind frühere Versionen
        var holders = UserDb.Query(conn, $"SELECT role, tresor, doc, since FROM dms_roles WHERE role IN {inSql} OR role = $t",
            r => (Tresor: r.GetInt64(1) != 0, Doc: r.GetInt64(2), Since: r.GetInt64(3)), args);
        if (holders.Count > 0)
        {
            var win = holders.OrderByDescending(h => h.Since).First();
            UserDb.Exec(conn, $"DELETE FROM dms_roles WHERE role IN {inSql} OR role = $t", args);
            UserDb.Exec(conn, "INSERT INTO dms_roles(role, tresor, doc, since) VALUES($t, $r, $d, $n)", ("$t", t.Id), ("$r", win.Tresor ? 1 : 0), ("$d", win.Doc), ("$n", win.Since));
            foreach (var h in holders.Where(h => h != win)) db.ReplacedHolders.Add((h.Tresor, h.Doc, t.Name));
        }
        // Vorgaben von Quellen (überwachte Ordner) und Mail-Quellen
        foreach (var key in new[] { DmsSources.Key, DmsMailSources.Key })
            foreach (var s in sources)
                UserDb.Exec(conn, "UPDATE kv SET value = replace(value, $f, $r) WHERE key = $k", ("$f", '"' + s + '"'), ("$r", '"' + t.Id + '"'), ("$k", key));
        UserDb.Exec(conn, $"DELETE FROM dms_lists WHERE id IN {inSql}", args);
    }

    /// <summary>
    /// Zusammenführen: Tresor (offen) zuerst, dann die Benutzerdatenbank samt Listen; ein geschlossener Tresor holt es beim
    /// nächsten Öffnen nach. Gibt die Bereiche mit ihren Dokumenten zurück, deren Ablage sich ändern kann.
    /// </summary>
    public static (Result Result, List<(DmsStore Store, List<long> Docs)> Refile) Run(UserDb user, TresorDb? tresor, bool tresorExists, Plan p, string change)
    {
        var sources = p.Sources.Select(s => s.Id).ToList();
        var refile = new List<(DmsStore, List<long>)>();
        int? tresorDocs = null;
        if (tresor != null)
        {
            var ts = DmsStore.ForTresor(user, tresor);
            var d = Apply(ts, p.Target.Id, sources, change, null);
            tresorDocs = d.Count;
            refile.Add((ts, d));
        }
        DmsStore us = user;
        var docs = Apply(us, p.Target.Id, sources, change, p);
        refile.Add((us, docs));
        var later = tresor == null && tresorExists;
        if (later)
        {
            var pending = user.GetJson<List<Pending>>(PendingKey);
            pending.Add(new Pending(p.Target.Id, sources, change));
            user.SetJson(PendingKey, pending);
        }
        // Abgelöste Rolleninhaber: im Bereich, in dem sie liegen, neu ablegen (Rolle aus dem Namen)
        foreach (var (inTresor, doc, _) in us.ReplacedHolders)
        {
            var store = inTresor ? refile.Select(r => r.Item1).FirstOrDefault(s => s.Tresor) : us;
            var list = refile.FirstOrDefault(r => r.Item1 == store).Item2;
            if (list != null && !list.Contains(doc)) list.Add(doc);
        }
        return (new Result(docs.Count, tresorDocs, later, change), refile);
    }

    /// <summary>Geschlossener Tresor beim Zusammenführen: beim nächsten Zugriff auf den offenen Tresor nachholen. Gibt die Dokumente zurück, deren Ablage sich ändern kann.</summary>
    public static List<long> CatchUp(DmsStore tresor)
    {
        if (!tresor.Tresor) return [];
        var pending = tresor.User.GetJson<List<Pending>>(PendingKey);
        if (pending.Count == 0) return [];
        // Erst austragen: schlägt ein Lauf fehl, bleibt der Rest nicht ewig hängen
        tresor.User.SetJson<List<Pending>>(PendingKey, []);
        var docs = new List<long>();
        foreach (var p in pending)
            foreach (var d in Apply(tresor, p.Target, p.Sources, p.Change, null))
                if (!docs.Contains(d)) docs.Add(d);
        return docs;
    }

    /// <summary>Vor dem Wiederherstellen einer Version: zusammengeführte (gelöschte) Einträge, auf die sie verweist, wieder anlegen.</summary>
    public static void Revive(UserDb db, DmsHistory.Meta m)
    {
        var ids = new[] { m.Correspondent, m.Doctype }.Concat(m.Tags).Where(x => !string.IsNullOrEmpty(x)).Select(x => x!).ToList();
        if (ids.Count == 0) return;
        if (db.Scalar("SELECT 1 FROM sqlite_master WHERE name = 'dms_merged'") == null) return;
        var have = Dms.Entries(db).Select(e => e.Id).ToHashSet();
        foreach (var id in ids.Where(x => !have.Contains(x)))
        {
            var row = db.Query("SELECT kind, parent, name, color, match, match_text, case_sensitive FROM dms_merged WHERE id = $i",
                r => (Kind: r.GetString(0), Parent: r.IsDBNull(1) ? null : r.GetString(1), Name: r.GetString(2), Color: r.IsDBNull(3) ? null : r.GetString(3),
                    Match: r.GetString(4), Text: r.IsDBNull(5) ? null : r.GetString(5), Cs: r.GetInt64(6)), ("$i", id)).FirstOrDefault();
            if (row.Name == null) continue;
            db.Exec("INSERT OR IGNORE INTO dms_lists(id, kind, parent, name, color, match, match_text, case_sensitive) VALUES($i,$k,$p,$n,$c,$m,$t,$s)",
                ("$i", id), ("$k", row.Kind), ("$p", row.Parent != null && have.Contains(row.Parent) ? row.Parent : null), ("$n", row.Name), ("$c", row.Color),
                ("$m", row.Match), ("$t", row.Text), ("$s", row.Cs));
            have.Add(id);
        }
    }
}
