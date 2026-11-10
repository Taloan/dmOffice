namespace DmOffice.Chat;

/// <summary>
/// Aufbewahrung (Chat-Alltag): Die Verwaltung legt serverweit fest, wie lange Chat-Nachrichten bleiben (Vorgabe: immer).
/// Ältere löscht dieser Dienst endgültig, samt Reaktionen. Dateien und Notizen, auf die sie zeigten, bleiben bei ihren
/// Besitzern (Ordner „Chat“) - die gehören den Leuten, nicht dem Chat.
/// </summary>
public sealed class ChatRetention(ChatStore chat, ILogger<ChatRetention> log) : BackgroundService
{
    public const string Key = "retention";
    public static readonly string[] Values = ["forever", "5y", "2y", "1y", "6m", "3m"];

    public static string Of(ChatStore chat) => chat.GetKv(Key) is { } v && Values.Contains(v) ? v : "forever";

    public static void Set(ChatStore chat, string value)
    {
        if (!Values.Contains(value)) throw new ArgumentException("Ungültige Aufbewahrung.");
        chat.SetKv(Key, value);
    }

    /// <summary>Was vor diesem Zeitpunkt geschrieben wurde, kommt weg - null: alles bleibt.</summary>
    public static DateTime? Cutoff(string value, DateTime nowUtc) => value switch
    {
        "5y" => nowUtc.AddYears(-5), "2y" => nowUtc.AddYears(-2), "1y" => nowUtc.AddYears(-1),
        "6m" => nowUtc.AddMonths(-6), "3m" => nowUtc.AddMonths(-3), _ => null,
    };

    /// <summary>Einmal aufräumen - gibt die Zahl der gelöschten Nachrichten zurück.</summary>
    public static int Run(ChatStore chat, DateTime nowUtc) => Cutoff(Of(chat), nowUtc) is { } cut ? chat.Purge(cut) : 0;

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), stop);
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    var n = Run(chat, DateTime.UtcNow);
                    if (n > 0) log.LogInformation("Chat-Aufbewahrung: {Count} alte Nachrichten gelöscht", n);
                }
                catch (Exception ex) { log.LogWarning("Chat-Aufbewahrung: {Error}", ex.Message); }
                await Task.Delay(TimeSpan.FromHours(6), stop);
            }
        }
        catch (OperationCanceledException) { }
    }
}
