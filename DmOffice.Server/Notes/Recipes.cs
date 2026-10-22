namespace DmOffice.Notes;

/// <summary>
/// Rezepte tragen in notizi den Tag "recipe" (Vorlage "Rezept", Rezept-Grabber der Browser-Erweiterung), der mit
/// dem Abgleich kommt (Notizen.tags). Aeltere Rezepte haben ihn nicht; sie erkennt man weiter an der gewohnten
/// Gestalt der Vorlage: die Eckdatenzeile ("Arbeitszeit ... Koch-/Backzeit ... Sammlung ..."), die Ueberschriften
/// "Zutaten" und "Zubereitung", "Rezept von ...". Wie der Ordner heisst, spielt keine Rolle. Der Essensplan
/// verweist ueber die sync_id auf die Notiz.
/// </summary>
public static class Recipes
{
    /// <param name="Id">Nummer der Notiz hier; 0 bei einem Rezept aus einer anderen Datenbank.</param>
    /// <param name="Db">Name der anderen Datenbank, sonst null (die gerade geladene).</param>
    public sealed record Entry(long Id, string SyncId, string Title, string? Folder, string? Db = null);

    public const string Tag = "recipe";

    /// <summary>Der Tag "recipe" (Gross-/Kleinschreibung egal), sonst die Gestalt der Vorlage.</summary>
    public static bool IsRecipe(IEnumerable<string>? tags, string plain) =>
        tags != null && tags.Any(t => string.Equals(t.Trim(), Tag, StringComparison.OrdinalIgnoreCase)) || IsRecipe(plain);

    /// <summary>Hat die Gestalt der Rezeptvorlage: Ueberschrift Zutaten/Zubereitung, "Rezept von ..." oder die Eckdatenzeile.</summary>
    public static bool IsRecipe(string plain)
    {
        foreach (var raw in plain.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Equals("Zutaten", StringComparison.OrdinalIgnoreCase) || line.Equals("Zubereitung", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Rezept von ", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return plain.Contains("Arbeitszeit", StringComparison.Ordinal) && plain.Contains("Koch-/Backzeit", StringComparison.Ordinal);
    }

    /// <summary>Namen der Ordner von der Wurzel bis zum Ordner, mit " / " verbunden.</summary>
    public static Dictionary<string, string> FolderPaths(IEnumerable<NoteFolder> folders)
    {
        var byId = folders.ToDictionary(f => f.SyncId);
        var result = new Dictionary<string, string>();
        foreach (var f in byId.Values)
        {
            var names = new List<string>();
            var cur = f;
            for (int i = 0; i < 20; i++)   // Schutz vor Schleifen
            {
                names.Insert(0, cur.Name);
                if (cur.Parent == null || !byId.TryGetValue(cur.Parent, out cur!)) break;
            }
            result[f.SyncId] = string.Join(" / ", names);
        }
        return result;
    }

    /// <summary>
    /// Zutaten eines Rezepts: in der Vorlage ein Kaestchen je Zutat (abgehakt oder nicht - fuer den Einkauf egal).
    /// Ohne Kaestchen die Zeilen unter der Ueberschrift "Zutaten" bis zur naechsten Ueberschrift.
    /// </summary>
    public static List<string> Ingredients(NoteRow note)
    {
        var boxes = NoteTasks.Of(note).Select(t => t.Text).Where(t => t.Length > 0).ToList();
        return boxes.Count > 0 ? boxes : IngredientsFromText(note.Plain);
    }

    private static readonly string[] EndHeadings = ["Zubereitung", "Anleitung", "Zubereitungsschritte", "Schritte", "Notizen", "Tipps", "Hinweise", "Preparation", "Directions", "Instructions", "Notes"];

    /// <summary>Zeilen zwischen "Zutaten" (bzw. "Ingredients") und der naechsten Ueberschrift, ohne Aufzaehlungszeichen.</summary>
    public static List<string> IngredientsFromText(string plain)
    {
        var result = new List<string>();
        bool inside = false;
        foreach (var raw in plain.Split('\n'))
        {
            var line = raw.Replace('￼', ' ').Trim();
            if (!inside)
            {
                if (line.TrimEnd(':').Equals("Zutaten", StringComparison.OrdinalIgnoreCase) || line.TrimEnd(':').Equals("Ingredients", StringComparison.OrdinalIgnoreCase)) inside = true;
                continue;
            }
            if (line.Length == 0) continue;
            if (EndHeadings.Any(h => line.TrimEnd(':').Equals(h, StringComparison.OrdinalIgnoreCase)) || line.StartsWith("Rezept von ", StringComparison.OrdinalIgnoreCase)) break;
            line = line.TrimStart('-', '–', '•', '*', '·', '☐', '☑', '☒', ' ').Trim();
            if (line.Length > 0 && line.Length <= 200) result.Add(line);
            if (result.Count >= 80) break;
        }
        return result;
    }

    public static List<Entry> All(NotesDb db, IEnumerable<NoteFolder> folders, string? q = null)
    {
        var paths = FolderPaths(folders);
        return db.GetAllActive()
            .Select(n => (Note: n, Path: n.Folder != null && paths.TryGetValue(n.Folder, out var p) ? p : null))
            .Where(x => IsRecipe(x.Note.Tags, x.Note.Plain))
            .Where(x => string.IsNullOrWhiteSpace(q) || x.Note.DisplayTitle.Contains(q.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Note.DisplayTitle, StringComparer.CurrentCultureIgnoreCase)
            .Select(x => new Entry(x.Note.Id, x.Note.SyncId, x.Note.DisplayTitle, x.Path))
            .ToList();
    }
}
