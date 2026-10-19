using System.Collections.Concurrent;
using DmOffice.Core;
using MailKit;
using MailKit.Net.Imap;
using MimeKit;

namespace DmOffice.Mail;

/// <summary>
/// Wiedervorlage (in Outlook "Erneut erinnern"): Die Mail wandert auf dem Mailserver in den
/// Ordner "Wiedervorlage" und kommt zur gewaehlten Zeit ungelesen in den Posteingang zurueck.
/// Der Zeitplan liegt in der Benutzerdatenbank und wird vom Mail-Sync abgearbeitet - auch ohne
/// offenen Browser.
/// </summary>
public static class Snooze
{
    public const string FolderName = "Wiedervorlage";

    public sealed class Entry
    {
        public string Account { get; set; } = "";
        /// <summary>UID im Ordner "Erneut erinnern" (aendert sich beim Verschieben).</summary>
        public uint Uid { get; set; }
        /// <summary>Zum Wiederfinden, wenn der Server keine UID nennt (ohne UIDPLUS).</summary>
        public string? MessageId { get; set; }
        public DateTime Until { get; set; }
        public string ReturnTo { get; set; } = "INBOX";
        public string Subject { get; set; } = "";
    }

    private const string Key = "mail.snoozes";

    public static List<Entry> All(UserDb db) => db.GetJson<List<Entry>>(Key);
    private static void Save(UserDb db, List<Entry> list) => db.SetJson(Key, list);

    // Oberflaeche und Sync aendern die Liste gleichzeitig - jedes Lesen-Aendern-Schreiben unter
    // dieser Sperre, je Benutzer. Die IMAP-Arbeit laeuft ausserhalb; danach frisch lesen und einarbeiten.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new();

    private static async Task UpdateAsync(UserDb db, Action<List<Entry>> change)
    {
        var gate = Locks.GetOrAdd(db.UserId, _ => new SemaphoreSlim(1, 1));
        // Ohne Abbruch: die IMAP-Arbeit ist schon geschehen, der Eintrag muss dazu passen
        await gate.WaitAsync();
        try
        {
            var list = All(db);
            change(list);
            Save(db, list);
        }
        finally { gate.Release(); }
    }

    private static bool Same(Entry a, Entry b) =>
        a.Account == b.Account && a.Uid == b.Uid && a.MessageId == b.MessageId && a.Until == b.Until && a.ReturnTo == b.ReturnTo;

    /// <summary>Den Ordner finden oder anlegen.</summary>
    public static async Task<IMailFolder> FolderAsync(ImapClient c, UserSpace space, MailAccount account, CancellationToken ct)
    {
        var row = MailStore.Folders(space.Db, account.Id).FirstOrDefault(f => f.Special == "snoozed");
        if (row != null)
            try { return await c.GetFolderAsync(row.Path, ct); } catch (FolderNotFoundException) { }
        var root = c.GetFolder(c.PersonalNamespaces[0]) ?? throw new InvalidOperationException("Kein Namensraum fuer Ordner.");
        try { return await root.GetSubfolderAsync(FolderName, ct); }
        catch (FolderNotFoundException) { }
        return await root.CreateAsync(FolderName, true, ct) ?? throw new InvalidOperationException("Ordner liess sich nicht anlegen.");
    }

    /// <summary>Mails zuruecklegen bis <paramref name="until"/> (UTC).</summary>
    public static async Task AddAsync(ImapClient c, UserSpace space, MailAccount account, string folder, IList<uint> uids, DateTime until, CancellationToken ct)
    {
        var target = await FolderAsync(c, space, account, ct);
        var source = await c.GetFolderAsync(folder, ct);
        await source.OpenAsync(FolderAccess.ReadWrite, ct);
        var ids = uids.Select(u => new UniqueId(u)).ToList();
        var info = await source.FetchAsync(ids, MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope, ct);
        var map = await source.MoveToAsync(ids, target, ct);

        var inbox = MailStore.Folders(space.Db, account.Id).FirstOrDefault(f => f.Special == "inbox")?.Path ?? "INBOX";
        var added = new List<Entry>();
        foreach (var s in info)
        {
            uint newUid = 0;
            if (map != null && map.TryGetValue(s.UniqueId, out var moved)) newUid = moved.Id;
            added.Add(new Entry
            {
                Account = account.Id, Uid = newUid, MessageId = s.Envelope?.MessageId, Until = until.ToUniversalTime(),
                // Wiedervorlage kommt immer im Posteingang an - auch aus dem Archiv (wie Outlook)
                ReturnTo = inbox,
                Subject = s.Envelope?.Subject ?? "",
            });
        }
        await UpdateAsync(space.Db, list =>
        {
            foreach (var e in added)
            {
                list.RemoveAll(x => x.Account == account.Id && x.MessageId != null && x.MessageId == e.MessageId);
                list.Add(e);
            }
        });
        MailStore.Delete(space.Db, account.Id, folder, uids);
    }

    /// <summary>Faellige Mails (oder auf Wunsch bestimmte sofort) zurueckholen. Gibt zurueck, wie viele.</summary>
    public static async Task<int> ReturnAsync(ImapClient c, UserSpace space, MailAccount account, Func<Entry, bool> which, CancellationToken ct)
    {
        var list = All(space.Db);
        var due = list.Where(e => e.Account == account.Id && which(e)).ToList();
        if (due.Count == 0) return 0;

        var folder = await FolderAsync(c, space, account, ct);
        await folder.OpenAsync(FolderAccess.ReadWrite, ct);
        int count = 0;
        // Immer in den Posteingang, auch bei aelteren Eintraegen mit anderem Ziel
        foreach (var group in due.GroupBy(_ => "INBOX"))
        {
            IMailFolder dest = c.Inbox;

            var uids = new List<UniqueId>();
            foreach (var e in group)
            {
                // UID pruefen; fehlt sie (Server ohne UIDPLUS, oder verschoben), ueber die Message-ID suchen
                UniqueId? uid = null;
                if (e.Uid != 0)
                {
                    var found = await folder.SearchAsync(MailKit.Search.SearchQuery.Uids(new[] { new UniqueId(e.Uid) }), ct);
                    if (found.Count > 0) uid = found[0];
                }
                if (uid == null && e.MessageId != null)
                {
                    var found = await folder.SearchAsync(MailKit.Search.SearchQuery.HeaderContains("Message-ID", e.MessageId), ct);
                    if (found.Count > 0) uid = found[0];
                }
                if (uid is UniqueId u) uids.Add(u);
            }
            if (uids.Count > 0)
            {
                // Ungelesen zurueck, wie in Outlook - damit sie auffaellt
                await folder.StoreAsync(uids, new StoreFlagsRequest(StoreAction.Remove, MessageFlags.Seen) { Silent = true }, ct);
                await folder.MoveToAsync(uids, dest, ct);
                MailStore.Delete(space.Db, account.Id, folder.FullName, uids.Select(x => x.Id));
                count += uids.Count;
            }
        }
        // Erledigt - auch Eintraege, deren Mail inzwischen von Hand verschoben oder geloescht wurde.
        // Frisch gelesen: was die andere Seite inzwischen eingetragen hat, bleibt.
        await UpdateAsync(space.Db, current => current.RemoveAll(e => due.Any(d => Same(d, e))));
        return count;
    }
}
