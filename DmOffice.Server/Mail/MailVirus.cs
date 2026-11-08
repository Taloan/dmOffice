using DmOffice.Core;
using MimeKit;

namespace DmOffice.Mail;

/// <summary>
/// Virenfunde in Mails (VirusScan): beim Eingang im Posteingang geprüft, gemerkt über die Message-ID (bleibt beim Verschieben
/// gültig, IMAP-Schlagworte überschreibt der Abgleich). Die Mail zeigt dann einen Warnbalken, ihre Anhänge lassen sich nicht öffnen.
/// </summary>
public static class MailVirus
{
    private static void Ensure(UserDb db) =>
        db.Exec("CREATE TABLE IF NOT EXISTS mail_virus(message_id TEXT PRIMARY KEY, name TEXT NOT NULL, at TEXT NOT NULL)");

    public static string KeyOf(MimeMessage m, string account, string folder, uint uid) =>
        string.IsNullOrEmpty(m.MessageId) ? $"{account}|{folder}|{uid}" : m.MessageId;

    public static void Mark(UserDb db, string key, string name)
    {
        Ensure(db);
        db.Exec("INSERT OR REPLACE INTO mail_virus(message_id, name, at) VALUES($k, $n, $t)", ("$k", key), ("$n", name), ("$t", DateTime.UtcNow.ToString("o")));
    }

    public static string? Of(UserDb db, string key)
    {
        Ensure(db);
        return db.Scalar("SELECT name FROM mail_virus WHERE message_id = $k", ("$k", key)) as string;
    }

    /// <summary>Neue Mail mit Anhang prüfen (nur wenn eingeschaltet); bei einem Fund merken. Fehler des Scanners halten nichts auf.</summary>
    public static async Task CheckAsync(UserSpace space, MailAccount account, string folder, uint uid, MimeMessage message, CancellationToken ct)
    {
        if (!VirusScan.Enabled || !message.Attachments.Any()) return;
        using var ms = new MemoryStream();
        await message.WriteToAsync(ms, ct);
        ms.Position = 0;
        var where = $"Mail {account.Email}: {message.Subject}";
        if (await VirusScan.VirusInAsync(ms, ms.Length, space.User.Id, where, ct) is { } name)
            Mark(space.Db, KeyOf(message, account.Id, folder, uid), name);
    }
}
