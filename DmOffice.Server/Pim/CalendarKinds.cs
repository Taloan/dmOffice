using System.Text.RegularExpressions;
using DmOffice.Core;

namespace DmOffice.Pim;

/// <summary>
/// Die Art eines Kalenders: "normal" oder eine besondere mit eigener Planungsansicht in der Weboberflaeche
/// (heute "meal" = Essensplan; denkbar spaeter Dienstplan, Abwesenheiten, Fahrdienst). Die Art ist nur ein
/// dmOffice-Merkmal: Sie steht beim Besitzer in der Schluessel/Wert-Tabelle (wie Lists.Meta), nicht im Kalender selbst
/// - CalDAV, DAVx5, iOS und die App sehen weiter einen gewoehnlichen Kalender mit gewoehnlichen Terminen (und das
/// Sync-Token bleibt, wenn die Art wechselt). Empfaenger eines geteilten Kalenders bekommen die Art des Besitzers mit.
/// Eine neue Art: Namen in <see cref="All"/> aufnehmen, in der Oberflaeche anzeigen und ihre Ansicht bauen.
/// </summary>
public static class CalendarKinds
{
    public const string Normal = "normal", Meal = "meal";

    /// <summary>Alle Arten, die der Server kennt; die erste ist die Vorgabe.</summary>
    public static readonly string[] All = { Normal, Meal };

    private const string Done = "pim.calkind.migrated";
    private static string Key(long id) => "pim.calkind." + id;

    /// <summary>Die Art eines Kalenders; ohne Eintrag (oder mit einer unbekannten Art) "normal".</summary>
    public static string Of(UserDb db, long id)
    {
        Migrate(db);
        var k = db.Get(Key(id));
        return k != null && All.Contains(k) ? k : Normal;
    }

    /// <summary>Art setzen; unbekannte Arten werden zu "normal". "normal" wird als eigener Wert gemerkt (die Wahl ist getroffen).</summary>
    public static string Set(UserDb db, long id, string? kind)
    {
        Migrate(db);
        var k = kind != null && All.Contains(kind) ? kind : Normal;
        db.Set(Key(id), k);
        return k;
    }

    public static void Remove(UserDb db, long id) => db.Set(Key(id), null);

    /// <summary>"CATEGORIES:...Mittagessen..." - die Kategorien, mit denen der Essensplan seine Termine kennzeichnet.</summary>
    private static readonly Regex MealCategory = new(@"^CATEGORIES[^:\r\n]*:[^\r\n]*(Frühstück|Mittagessen|Abendessen)", RegexOptions.Multiline | RegexOptions.IgnoreCase);

    /// <summary>
    /// Einmalig je Benutzer: Jeder Kalender mit mindestens einem Mahlzeit-Termin und ohne gewaehlte Art wird zum Essensplan
    /// (so lief der Essensplan vor den Kalenderarten). Danach entscheidet allein die Wahl des Besitzers.
    /// </summary>
    public static void Migrate(UserDb db)
    {
        if (db.Get(Done) != null) return;
        foreach (var c in PimStore.Collections(db, PimStore.Calendar))
        {
            if (db.Get(Key(c.Id)) != null || !c.Components.Contains("VEVENT")) continue;
            var found = db.Query("SELECT data FROM objects WHERE collection=$c AND component='VEVENT' AND data LIKE '%CATEGORIES%'", r => r.GetString(0), ("$c", c.Id))
                .Any(d => MealCategory.IsMatch(d));
            if (found) db.Set(Key(c.Id), Meal);
        }
        db.Set(Done, "1");
    }
}
