using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using DmOffice.Core;

namespace DmOffice.Chat;

/// <summary>
/// In wessen Sprache und nach wessen Uhr Pukki gerade antwortet. Der Browser schickt mit jeder Nachricht Zeitzone und Sprache
/// des Geräts mit: Kalender und Termin-Dialog rechnen im Browser nach dem Gerät, also muss „morgen 15 Uhr“ an Pukki dasselbe
/// meinen. Hintergrundnachrichten (Tagesüberblick, Erinnerungen) haben kein Gerät: Sie folgen dem Konto.
/// </summary>
public static partial class Pukki
{
    /// <summary>Kennzeichen einer Antwort, die von der KI stammt (statt von Pukki selbst) - steht in der Spalte für die Stimmung.</summary>
    public const string AiMood = "ki";

    private static readonly AsyncLocal<bool> Speaks = new();
    private static readonly ConcurrentDictionary<string, (string? Zone, string? Lang)> Devices = new();

    /// <summary>Antwortet Pukki gerade auf Englisch?</summary>
    internal static bool SpeaksEnglish => Speaks.Value;

    /// <summary>Text in der Sprache dieser Antwort.</summary>
    internal static string L(string de, string en) => SpeaksEnglish ? en : de;

    /// <summary>Sprache und Zeitzone des Geräts, von dem die letzte Nachricht kam. Ohne Angabe (andere Programme): die des Kontos.</summary>
    public static void FromDevice(string user, string? zone, string? lang) => Devices[user] = (zone, lang);

    /// <summary>Spricht diese Person Englisch? Angabe des Geräts, sonst die Einstellung im Konto.</summary>
    public static bool WantsEnglish(UserSpace space, string? lang = null)
    {
        if (lang is "en" or "de") return lang == "en";
        try
        {
            if (space.Db.Get("ui.prefs") is not { } json) return false;
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("language", out var l) && l.ValueKind == JsonValueKind.String && l.GetString() == "en";
        }
        catch (JsonException) { return false; }
    }

    private sealed class Scope(bool before, IDisposable? zone) : IDisposable
    {
        public void Dispose() { Speaks.Value = before; zone?.Dispose(); }
    }

    /// <summary>Antwort auf eine Nachricht: Sprache und Zeitzone des Geräts gelten bis zum Ende des Blocks.</summary>
    public static IDisposable ForDevice(UserSpace space)
    {
        var device = Devices.TryGetValue(space.User.Id, out var d) ? d : default;
        var before = Speaks.Value;
        Speaks.Value = WantsEnglish(space, device.Lang);
        return new Scope(before, UserSpace.UseZone(device.Zone is { Length: > 0 } z ? Dav.Ics.Zone(z) : null));
    }

    /// <summary>Nachricht ohne Gerät (Tagesüberblick, Erinnerung, Start-Kachel): die Sprache des Kontos, die Uhr des Kontos.</summary>
    public static IDisposable ForAccount(UserSpace space)
    {
        var before = Speaks.Value;
        Speaks.Value = WantsEnglish(space);
        return new Scope(before, null);
    }

    /// <summary>Nur für Tests: Pukki auf Englisch.</summary>
    internal static IDisposable InEnglish()
    {
        var before = Speaks.Value;
        Speaks.Value = true;
        return new Scope(before, null);
    }

    private const string HelpEn =
        "I'm Pukki. Here's what I can do:\n" +
        "• appointment tomorrow 3pm dentist – I add it to your calendar\n" +
        "• task buy milk (with a day if you like: task Friday taxes) – goes to your tasks\n" +
        "• note holiday ideas – I create a note\n" +
        "• Questions like “What's on tomorrow?”\n" +
        "If something isn't right, just write “no”.\n" +
        "Many of my answers are still in German. I understand German best.\n" +
        "In the morning I send you the daily overview and remind you of events – set this up under Settings → Chat.";

    /// <summary>Tag für Sätze in der Sprache der Antwort: heute, morgen, sonst der Wochentag mit Datum.</summary>
    internal static string DayWords(DateOnly day, DateOnly today)
    {
        if (!SpeaksEnglish) return PukkiParse.DayText(day, today);
        return day == today ? "today" : day == today.AddDays(1) ? "tomorrow" : day == today.AddDays(2) ? "the day after tomorrow"
            : day == today.AddDays(-1) ? "yesterday"
            : day.ToString(day.Year == today.Year ? "dddd, d MMMM" : "dddd, d MMMM yyyy", CultureInfo.GetCultureInfo("en-GB"));
    }

    /// <summary>Wochentag und Datum ausgeschrieben, in der Sprache der Antwort.</summary>
    internal static string DateWords(DateOnly day, string de, string en) =>
        SpeaksEnglish ? day.ToString(en, CultureInfo.GetCultureInfo("en-GB")) : day.ToString(de, De);
}
