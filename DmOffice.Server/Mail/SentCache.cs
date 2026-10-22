using System.Collections.Concurrent;
using MimeKit;

namespace DmOffice.Mail;

/// <summary>
/// Gerade gesendete Mails: Der Server hat sie beim Senden vollstaendig in der Hand. Kommt ihre Kopie in "Gesendet" beim
/// naechsten Abgleich an, wird ihr Inhalt gleich abgelegt - sonst muesste sie beim ersten Oeffnen vom Anbieter geholt werden.
/// Liegt nur kurz im Speicher.
/// </summary>
public static class SentCache
{
    private static readonly ConcurrentDictionary<string, (DateTime At, byte[] Raw)> Items = new();
    private static readonly TimeSpan Keep = TimeSpan.FromMinutes(30);

    private static string Key(string user, string account, string folder, string messageId) =>
        user + "|" + account + "|" + folder + "|" + messageId.Trim('<', '>');

    public static void Remember(string user, string account, string folder, MimeMessage message, long maxBytes)
    {
        if (string.IsNullOrEmpty(message.MessageId)) return;
        foreach (var (k, v) in Items.ToArray())
            if (DateTime.UtcNow - v.At > Keep) Items.TryRemove(k, out _);
        using var ms = new MemoryStream();
        message.WriteTo(ms);
        if (ms.Length > maxBytes) return;   // groesser als die Grenze des Kontos: wird ohnehin beim Oeffnen geholt
        Items[Key(user, account, folder, message.MessageId)] = (DateTime.UtcNow, ms.ToArray());
    }

    /// <summary>Inhalt der gerade gesendeten Mail mit dieser Message-ID (und weg aus dem Speicher), sonst null.</summary>
    public static byte[]? Take(string user, string account, string folder, string? messageId) =>
        messageId != null && Items.TryRemove(Key(user, account, folder, messageId), out var hit) && DateTime.UtcNow - hit.At <= Keep ? hit.Raw : null;
}
