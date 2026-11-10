namespace DmOffice.Chat;

public static partial class Pukki
{
    /// <summary>
    /// Für den Morgenüberblick: Zeitumstellung letzte Nacht oder heute Nacht (nach der Zeitzone des Benutzers - zurück heißt
    /// Winterzeit, vor Sommerzeit). Null: keine.
    /// </summary>
    internal static string? ClockChange(TimeZoneInfo zone, DateOnly today)
    {
        TimeSpan Offset(DateOnly d) => zone.GetUtcOffset(TimeZoneInfo.ConvertTimeToUtc(d.ToDateTime(new TimeOnly(12, 0)), zone));
        var yesterday = Offset(today.AddDays(-1));
        var now = Offset(today);
        var tomorrow = Offset(today.AddDays(1));
        if (now != yesterday)
            return now < yesterday
                ? "Heute Nacht wurde auf Winterzeit umgestellt – die Uhr ging eine Stunde zurück. Eine Stunde länger geschlafen 😴"
                : "Heute Nacht wurde auf Sommerzeit umgestellt – die Uhr ging eine Stunde vor. Eine Stunde weniger Schlaf ☕";
        if (tomorrow != now)
            return tomorrow < now
                ? "Heute Nacht wird auf Winterzeit umgestellt: Die Uhr geht eine Stunde zurück."
                : "Heute Nacht wird auf Sommerzeit umgestellt: Die Uhr geht eine Stunde vor – eine Stunde weniger Schlaf.";
        return null;
    }
}
