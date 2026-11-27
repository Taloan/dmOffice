using System.Globalization;
using System.Text.Json;
using DmOffice.Core;

namespace DmOffice.Dms;

/// <summary>
/// Eigene Felder (Kennung dmsfields, Vorbild „benutzerdefinierte Felder“ in paperless): Felder mit Name und Art (Text, Zahl, Datum,
/// Betrag, Link), auf Wunsch nur bei bestimmten Dokumenttypen vorgeschlagen - etwa „Steuernummer“ beim Steuerbescheid. Die Felder
/// gelten für das ganze DMS (Tabelle in der Benutzerdatenbank, wie die Listen); die Werte stehen am Dokument (Spalte fields als JSON,
/// im Tresor in dessen Datenbank). Gesucht wird mit der Volltextsuche, sortiert über die Spalte „f:Kennung“.
/// </summary>
public static class DmsFields
{
    public const string Text = "text", Number = "number", Date = "date", Money = "money", Url = "url";
    private static readonly string[] Types = [Text, Number, Date, Money, Url];
    public const int MaxFields = 50, MaxText = 500;

    /// <param name="Doctypes">Kennungen der Dokumenttypen, bei denen das Feld gleich angeboten wird; leer: nur über „Feld hinzufügen“</param>
    public sealed record Field(string Id, string Name, string Type, List<string> Doctypes, int Sort);

    public sealed record Input(string? Name, string? Type, List<string>? Doctypes);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static void Ensure(UserDb db) =>
        db.Exec("CREATE TABLE IF NOT EXISTS dms_fields (id TEXT PRIMARY KEY, name TEXT NOT NULL, type TEXT NOT NULL, doctypes TEXT NULL, sort INTEGER NOT NULL DEFAULT 0)");

    public static List<Field> All(UserDb db)
    {
        Ensure(db);
        return db.Query("SELECT id, name, type, doctypes, sort FROM dms_fields ORDER BY sort, name COLLATE NOCASE", r => new Field(r.GetString(0), r.GetString(1), r.GetString(2),
            r.IsDBNull(3) ? [] : JsonSerializer.Deserialize<List<string>>(r.GetString(3), Json) ?? [], (int)r.GetInt64(4)));
    }

    public static Field Save(UserDb db, string? id, Input b)
    {
        Ensure(db);
        var name = (b.Name ?? "").Trim();
        if (name.Length == 0) throw new ArgumentException("Bitte einen Namen angeben.");
        if (name.Length > 80) throw new ArgumentException("Der Name ist zu lang (höchstens 80 Zeichen).");
        var type = b.Type ?? Text;
        if (!Types.Contains(type)) throw new ArgumentException("Unbekannte Art.");
        var all = All(db);
        if (all.Any(f => f.Id != id && string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException($"Ein Feld „{name}“ gibt es schon.");
        var doctypes = (b.Doctypes ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().Take(50).ToList();
        var json = doctypes.Count == 0 ? null : JsonSerializer.Serialize(doctypes, Json);
        if (id == null)
        {
            if (all.Count >= MaxFields) throw new ArgumentException($"Höchstens {MaxFields} eigene Felder.");
            id = "f" + Guid.NewGuid().ToString("N")[..10];
            db.Exec("INSERT INTO dms_fields(id, name, type, doctypes, sort) VALUES($i, $n, $t, $d, $s)", ("$i", id), ("$n", name), ("$t", type), ("$d", json),
                ("$s", all.Count == 0 ? 0 : all.Max(f => f.Sort) + 1));
        }
        else
        {
            var old = all.FirstOrDefault(f => f.Id == id) ?? throw new KeyNotFoundException("Feld nicht gefunden.");
            // Art wechseln geht nur, solange die Werte dazu passen: sonst bliebe Unlesbares stehen. Darum hier nicht erlaubt.
            if (old.Type != type) throw new ArgumentException("Die Art eines Felds lässt sich nicht ändern. Bitte ein neues Feld anlegen.");
            db.Exec("UPDATE dms_fields SET name = $n, doctypes = $d WHERE id = $i", ("$n", name), ("$d", json), ("$i", id));
        }
        return All(db).First(f => f.Id == id);
    }

    /// <summary>Feld löschen: die Werte an den Dokumenten bleiben stehen, bis das Dokument das nächste Mal geändert wird (dann fallen sie weg).</summary>
    public static void Delete(UserDb db, string id)
    {
        Ensure(db);
        db.Exec("DELETE FROM dms_fields WHERE id = $i", ("$i", id));
    }

    public static void Order(UserDb db, List<string> ids)
    {
        Ensure(db);
        for (var i = 0; i < ids.Count; i++) db.Exec("UPDATE dms_fields SET sort = $s WHERE id = $i", ("$s", i), ("$i", ids[i]));
    }

    /// <summary>Wert prüfen und vereinheitlichen: Zahl mit Punkt, Datum yyyy-MM-dd, Betrag in Cent, Link mit http(s); leer heißt entfernen (null).</summary>
    public static string? Normalize(Field f, string? value)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 0) return null;
        switch (f.Type)
        {
            case Number:
                var n = v.Replace(" ", "");
                if (!decimal.TryParse(n, NumberStyles.Number, CultureInfo.InvariantCulture, out var num) && !decimal.TryParse(n, NumberStyles.Number, CultureInfo.GetCultureInfo("de-DE"), out num))
                    throw new ArgumentException($"„{f.Name}“: keine Zahl.");
                return num.ToString(CultureInfo.InvariantCulture);
            case Date:
                if (!DateOnly.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) || d.Year < 1000)
                    throw new ArgumentException($"„{f.Name}“: kein gültiges Datum.");
                return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            case Money:
                if (!long.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var cents) || Math.Abs(cents) >= 100_000_000_000)
                    throw new ArgumentException($"„{f.Name}“: kein gültiger Betrag.");
                return cents.ToString(CultureInfo.InvariantCulture);
            case Url:
                if (!v.Contains("://")) v = "https://" + v;
                if (!Uri.TryCreate(v, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https") || v.Length > MaxText)
                    throw new ArgumentException($"„{f.Name}“: keine gültige Adresse.");
                return v;
            default:
                return v.Length > MaxText ? v[..MaxText] : v;
        }
    }

    public static Dictionary<string, string> Parse(string? json) =>
        string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<Dictionary<string, string>>(json, Json) ?? [];

    /// <summary>Neue Werte in die bisherigen einarbeiten: nur bekannte Felder, leer entfernt; Werte gelöschter Felder fallen weg.</summary>
    public static string? Merge(List<Field> defs, string? current, Dictionary<string, string?> change)
    {
        var byId = defs.ToDictionary(f => f.Id);
        var values = Parse(current).Where(kv => byId.ContainsKey(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        foreach (var (id, value) in change)
        {
            if (!byId.TryGetValue(id, out var f)) throw new KeyNotFoundException("Feld nicht gefunden.");
            if (Normalize(f, value) is { } v) values[id] = v; else values.Remove(id);
        }
        return values.Count == 0 ? null : JsonSerializer.Serialize(values, Json);
    }
}
