using System.Collections.Concurrent;
using System.Text.Json;
using DmOffice.Core;
using MimeKit;

namespace DmOffice.Mail;

/// <summary>
/// Sichere Absender fuer den Spamfilter: wer im Adressbuch steht oder wem man selbst geschrieben hat
/// (Empfaenger in den Gesendet-Ordnern und beim Senden aus dmOffice gemerkt). Die eigenen Adressen
/// zaehlen nicht - Spam mit gefaelschtem eigenem Absender ist haeufig.
/// </summary>
public static class SafeSenders
{
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(10);
    private static readonly ConcurrentDictionary<UserDb, (DateTime At, HashSet<string> Set)> Cache = new();

    public static HashSet<string> Known(UserSpace space)
    {
        if (Cache.TryGetValue(space.Db, out var c) && DateTime.UtcNow - c.At < Fresh) return c.Set;
        var set = Pim.ContactStore.KnownAddresses(space);
        foreach (var r in Pim.ContactStore.Recents(space)) set.Add(r.Address.ToLowerInvariant());
        foreach (var json in space.Db.Query(@"SELECT m.recipients FROM mail m
                JOIN mail_folders f ON f.account = m.account AND f.path = m.folder
                WHERE f.special = 'sent' ORDER BY m.date DESC LIMIT 5000", r => r.GetString(0)))
            foreach (var a in Addresses(json)) set.Add(a);
        foreach (var own in space.Settings.MailAccounts.Select(a => a.Email.ToLowerInvariant())) set.Remove(own);
        Cache[space.Db] = (DateTime.UtcNow, set);
        return set;
    }

    /// <summary>Absender (From) steht im Adressbuch oder bekam schon Post von uns.</summary>
    public static bool IsKnown(UserSpace space, MimeMessage m)
    {
        var from = m.From.Mailboxes.FirstOrDefault()?.Address?.ToLowerInvariant();
        return !string.IsNullOrEmpty(from) && Known(space).Contains(from);
    }

    /// <summary>Nach Aenderungen am Adressbuch oder an den Einstellungen sofort neu lesen.</summary>
    public static void Forget(UserDb db) => Cache.TryRemove(db, out _);

    /// <summary>Adressen aus der Spalte "recipients": {"to":[{"address":…}],"cc":[…]}.</summary>
    private static IEnumerable<string> Addresses(string json)
    {
        if (!json.StartsWith('{')) yield break;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); } catch (JsonException) { yield break; }
        using (doc)
            foreach (var list in doc.RootElement.EnumerateObject())
                if (list.Value.ValueKind == JsonValueKind.Array)
                    foreach (var a in list.Value.EnumerateArray())
                        if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty("address", out var v) && v.GetString() is { Length: > 0 } s)
                            yield return s.ToLowerInvariant();
    }
}
