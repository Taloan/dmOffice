using DmOffice.Core;

namespace DmOffice.Chat;

/// <summary>
/// Persoenliche Chat-Einstellungen (Einstellungen -> Chat), als JSON im kv der Benutzerdatenbank ("chat.settings") -
/// kein neues Schema. Arten: show (Zeigen) | send (Senden).
/// </summary>
public sealed class ChatSettings
{
    public const string Key = "chat.settings";

    /// <summary>Vorgabe-Art fuer Fotos/Videos/Dateien vom Geraet.</summary>
    public string DeviceMode { get; set; } = ChatRef.Send;
    /// <summary>Vorgabe-Art fuer Dateien aus der Ablage.</summary>
    public string FilesMode { get; set; } = ChatRef.Show;
    /// <summary>Vorgabe-Art fuer Notizen (spaeter auch Termine, Aufgaben, Kontakte).</summary>
    public string ItemsMode { get; set; } = ChatRef.Show;
    /// <summary>"Bei mir speichern": Ordner fuer Dateien - fest der Ordner „Chat“ (frueher einstellbar; Gespeichertes wird ignoriert).
    /// Beim Speichern selbst laesst sich jedes Mal ein anderes Ziel waehlen.</summary>
    public string SaveFolder { get => ChatApi.UploadFolder; set { } }
    /// <summary>"Bei mir speichern": Notizordner (sync_id), null = ohne Ordner.</summary>
    public string? NoteFolder { get; set; }
    /// <summary>Andere duerfen, was ich zeige, weiterzeigen.</summary>
    public bool Reshow { get; set; }
    /// <summary>Wie lange Gezeigtes sichtbar bleibt: forever | 1y | 6m | 1m | 1w | 24h (ab Sendedatum).</summary>
    public string ShowFor { get; set; } = "forever";
    /// <summary>Andere duerfen mich nach meinem Standort fragen (Standort-Anfrage, ChatLocation.cs).</summary>
    public bool LocationRequests { get; set; }
    /// <summary>Pukki (Stufe 5): morgens den Tagesueberblick schicken, um PukkiTime (HH:mm, Ortszeit).</summary>
    public bool PukkiDaily { get; set; } = true;
    public string PukkiTime { get; set; } = "07:00";
    /// <summary>Pukki erinnert an Termine mit Erinnerung (als Chat-Nachricht).</summary>
    public bool PukkiReminders { get; set; } = true;
    /// <summary>Lesebestätigungen: andere sehen, ob ich ihre Nachricht gelesen habe (die Zustellung sehen sie immer).</summary>
    public bool ReadReceipts { get; set; } = true;
    /// <summary>Ruhezeiten (Chat-Komfort): von QuietFrom bis QuietTo (HH:mm, Ortszeit, auch ueber Mitternacht) keine Benachrichtigungen.</summary>
    public bool Quiet { get; set; }
    public string QuietFrom { get; set; } = "22:00";
    public string QuietTo { get; set; } = "07:00";
    /// <summary>In der Ruhezeit trotzdem melden, wenn mich jemand erwaehnt.</summary>
    public bool QuietMentions { get; set; }

    public static readonly string[] Durations = ["forever", "1y", "6m", "1m", "1w", "24h"];

    public static ChatSettings Of(UserDb db) => db.GetJson<ChatSettings>(Key);

    public static void Save(UserDb db, ChatSettings s)
    {
        if (s.DeviceMode is not (ChatRef.Show or ChatRef.Send) || s.FilesMode is not (ChatRef.Show or ChatRef.Send) || s.ItemsMode is not (ChatRef.Show or ChatRef.Send))
            throw new ArgumentException("Ungültige Art.");
        if (!Durations.Contains(s.ShowFor)) throw new ArgumentException("Ungültige Dauer.");
        if (!TimeOnly.TryParse(s.PukkiTime ?? "", System.Globalization.CultureInfo.InvariantCulture, out var at)) throw new ArgumentException("Ungültige Uhrzeit.");
        s.PukkiTime = at.ToString("HH:mm");
        if (!TimeOnly.TryParse(s.QuietFrom ?? "", System.Globalization.CultureInfo.InvariantCulture, out var from)
            || !TimeOnly.TryParse(s.QuietTo ?? "", System.Globalization.CultureInfo.InvariantCulture, out var to)) throw new ArgumentException("Ungültige Uhrzeit.");
        s.QuietFrom = from.ToString("HH:mm");
        s.QuietTo = to.ToString("HH:mm");
        db.SetJson(Key, s);
    }

    /// <summary>Ruhezeit gerade (Ortszeit <paramref name="local"/>)? Gleiche Zeiten: keine.</summary>
    public bool InQuiet(DateTime local)
    {
        if (!Quiet || !TimeOnly.TryParse(QuietFrom, System.Globalization.CultureInfo.InvariantCulture, out var from)
            || !TimeOnly.TryParse(QuietTo, System.Globalization.CultureInfo.InvariantCulture, out var to) || from == to) return false;
        var t = TimeOnly.FromDateTime(local);
        return from < to ? t >= from && t < to : t >= from || t < to;
    }

    /// <summary>Was jetzt gemeldet wird: null = alles, "mentions" = nur Erwaehnungen, "all" = nichts (Ruhezeit).</summary>
    public string? QuietNow(UserSpace space) => !InQuiet(Pukki.Now(space)) ? null : QuietMentions ? "mentions" : "all";

    /// <summary>Ablauf fuer etwas, das jetzt gezeigt wird - null: dauerhaft (auch, wenn die Verwaltung Ablauf nicht erlaubt).</summary>
    public DateTime? ExpiresFrom(DateTime now, ChatRights rights) => !rights.Expiring ? null : ShowFor switch
    {
        "1y" => now.AddYears(1),
        "6m" => now.AddMonths(6),
        "1m" => now.AddMonths(1),
        "1w" => now.AddDays(7),
        "24h" => now.AddHours(24),
        _ => null,
    };
}
