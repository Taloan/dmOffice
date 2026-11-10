using DmOffice.Core;
using DmOffice.Dav;

namespace DmOffice.Gallery;

/// <summary>
/// Angaben zu Fotos nur in der eigenen Datenbank (Einstellungen → Galerie: „Angaben in die Fotos schreiben“ aus): Sterne, Titel,
/// Beschreibung, Stichwörter, Markierung und Farbe. Die Dateien und Sidecars bleiben dann unverändert; beim Lesen geht ein Wert
/// von hier vor dem aus der Datei. Schlüssel wie in der Galerie: Pfad in der Ablage oder „ext:Quelle/Pfad“.
/// Gespeichert heißt: gesetzt - auch „entfernt“ (Sterne 0, Markierung 0, leerer Text, leere Farbe) überdeckt den Wert der Datei.
/// </summary>
public static class PhotoMarks
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<UserDb, object> Ensured = new();

    public static void Ensure(UserDb db)
    {
        if (Ensured.TryGetValue(db, out _)) return;
        db.Exec(@"CREATE TABLE IF NOT EXISTS photo_marks(key TEXT PRIMARY KEY, rating INTEGER, title TEXT, description TEXT, keywords TEXT,
            pick INTEGER, label TEXT) WITHOUT ROWID");
        Ensured.AddOrUpdate(db, true);
    }

    /// <summary>SQL-Ausdruck: Wert aus photo_marks (Alias pm), sonst der aus der Tabelle (Alias t); „entfernt“ gibt NULL.</summary>
    public static string Col(string t, string col) => col is "rating" or "pick"
        ? $"CASE WHEN pm.{col} IS NULL THEN {t}.{col} WHEN pm.{col} = 0 THEN NULL ELSE pm.{col} END"
        : $"CASE WHEN pm.{col} IS NULL THEN {t}.{col} WHEN pm.{col} = '' THEN NULL ELSE pm.{col} END";

    /// <summary>Angaben eintragen; was in den Feldern null ist, bleibt, wie es ist.</summary>
    public static void Set(UserDb db, string key, PhotoMeta.Fields f, string? currentKeywords)
    {
        Ensure(db);
        string? keywords = null;
        if (f.HasKeywords)
        {
            var old = db.Scalar("SELECT keywords FROM photo_marks WHERE key = $k", ("$k", key)) as string ?? currentKeywords ?? "";
            keywords = string.Join("\n", f.KeywordsFrom(old.Split('\n', StringSplitOptions.RemoveEmptyEntries)));
        }
        db.Exec(@"INSERT INTO photo_marks(key, rating, title, description, keywords, pick, label) VALUES($k, $r, $t, $d, $kw, $p, $l)
            ON CONFLICT(key) DO UPDATE SET rating = COALESCE($r, rating), title = COALESCE($t, title), description = COALESCE($d, description),
                keywords = COALESCE($kw, keywords), pick = COALESCE($p, pick), label = COALESCE($l, label)",
            ("$k", key), ("$r", f.Rating), ("$t", f.Title?.Trim()), ("$d", f.Description?.Trim()), ("$kw", keywords),
            ("$p", f.Pick is int p ? Math.Sign(p) : null), ("$l", f.Label));
        // Galerie und Liste neu: der Stand hat sich geändert, auch wenn keine Datei sich geändert hat
        GalleryTimeline.Ensure(db);
        db.Exec("UPDATE gallery_own_ver SET v = v + 1 WHERE id = 1");
        if (db.Scalar("SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'gallery_ext_ver'") != null) db.Exec("UPDATE gallery_ext_ver SET v = v + 1 WHERE id = 1");
    }
}
