using DmOffice.Core;
using MailKit;
using MailKit.Net.Imap;

namespace DmOffice.Mail;

/// <summary>
/// Entfernen nach Zeit: Eine Mail kommt zum gewaehlten Zeitpunkt in den Papierkorb (wiederherstellbar) oder wird
/// vernichtet (auf dem Mailserver \Deleted und sofort getilgt, in dmOffice Zwischenspeicher, Suchindex und
/// Anhang-Kopien weg). Gebaut wie die Wiedervorlage (<see cref="Snooze"/>): Zeitplan als Liste in der
/// Benutzerdatenbank, abgearbeitet vom Mail-Sync in derselben Phase - auch ohne offenes Geraet. Die Mail bleibt bis
/// dahin, wo sie ist; verschoben (anderer Ordner, neue UID) wird sie ueber die Message-ID wiedergefunden.
/// Kopien auf anderen Geraeten, beim Absender oder in Sicherungen des Mail-Hosters erreicht das nicht.
/// </summary>
public static class MailExpiry
{
    public const string Trash = "trash", Destroy = "destroy";

    public sealed class Entry
    {
        public string Account { get; set; } = "";
        /// <summary>Ordner und UID beim Einstellen - danach nur noch ein Hinweis, gesucht wird auch ueber die Message-ID.</summary>
        public string Folder { get; set; } = "";
        public uint Uid { get; set; }
        public string? MessageId { get; set; }
        public DateTime At { get; set; }
        /// <summary><see cref="Trash"/> oder <see cref="Destroy"/>.</summary>
        public string Kind { get; set; } = Trash;
        public string Subject { get; set; } = "";
    }

    private const string Key = "mail.expiries";

    /// <summary>Nicht mehr auffindbar (von Hand endgueltig geloescht): so lange nach dem Zeitpunkt noch suchen, dann vergessen.</summary>
    public static readonly TimeSpan GiveUp = TimeSpan.FromDays(2);

    public static List<Entry> All(UserDb db) => db.GetJson<List<Entry>>(Key);

    // Dieselbe Sperre wie die Wiedervorlage: Oberflaeche und Sync aendern die Liste gleichzeitig
    private static async Task UpdateAsync(UserDb db, Action<List<Entry>> change)
    {
        var gate = Snooze.Gate(db);
        await gate.WaitAsync();
        try
        {
            var list = All(db);
            change(list);
            db.SetJson(Key, list);
        }
        finally { gate.Release(); }
    }

    /// <summary>Dieselbe Mail: nach Message-ID, ohne sie nach Ordner und UID.</summary>
    private static bool SameMail(Entry a, Entry b) =>
        a.Account == b.Account && (a.MessageId != null || b.MessageId != null ? a.MessageId == b.MessageId : a.Folder == b.Folder && a.Uid == b.Uid);

    private static bool Same(Entry a, Entry b) => SameMail(a, b) && a.Kind == b.Kind && a.At == b.At;

    /// <summary>Einplanen. Je Mail und Art ein Eintrag (Papierkorb nach 7 Tagen und Vernichten nach 30 geht zusammen);
    /// ein neuer Zeitpunkt ersetzt den alten. Auch fuer andere Stellen, die beim Eingang einplanen (Regeln, Wegwerfadressen).</summary>
    public static Task ScheduleAsync(UserDb db, IEnumerable<Entry> entries)
    {
        // Release-Liste: in dieser Version nicht freigeschaltet - nichts einplanen (Regeln, Catchall-Aliase)
        if (!Features.On(Features.MailExpiry)) return Task.CompletedTask;
        var added = entries.Select(e => { e.At = e.At.ToUniversalTime(); return e; }).ToList();
        return UpdateAsync(db, list =>
        {
            foreach (var e in added)
            {
                list.RemoveAll(x => SameMail(x, e) && x.Kind == e.Kind);
                list.Add(e);
            }
        });
    }

    /// <summary>Mails eines Ordners einplanen - aus den Zeilen der Mailliste, ohne den Mailserver zu fragen.</summary>
    public static async Task<int> PlanAsync(UserDb db, string account, string folder, IEnumerable<uint> uids, DateTime at, string kind)
    {
        var entries = uids.Select(u => MailStore.Row(db, account, folder, u)).Where(r => r != null).Select(r => new Entry
        {
            Account = account, Folder = folder, Uid = r!.Uid, MessageId = r.MessageId, At = at, Kind = kind, Subject = r.Subject,
        }).ToList();
        await ScheduleAsync(db, entries);
        return entries.Count;
    }

    /// <summary>Zuruecknehmen (beide Arten) fuer diese Mails, gefunden nach Ordner und UID oder Message-ID.</summary>
    public static async Task<int> CancelAsync(UserDb db, string account, string folder, IEnumerable<uint> uids)
    {
        var ids = uids.ToHashSet();
        var mids = ids.Select(u => MailStore.Row(db, account, folder, u)?.MessageId).Where(m => m != null).ToHashSet();
        int n = 0;
        await UpdateAsync(db, list => n = list.RemoveAll(e => e.Account == account
            && ((e.MessageId != null && mids.Contains(e.MessageId)) || (e.Folder == folder && ids.Contains(e.Uid)))));
        return n;
    }

    // ---------------- Ausfuehren ----------------

    /// <summary>Der IMAP-Teil hinter einer schmalen Schnittstelle - im Sync <see cref="Imap"/>, in den Tests ein nachgebauter Server.</summary>
    public interface IServer
    {
        /// <summary>Server kann UID EXPUNGE (UIDPLUS).</summary>
        bool UidPlus { get; }
        /// <summary>Die Mail in einem Ordner: alle Treffer zur Message-ID, ohne sie die UID, wenn es sie gibt.</summary>
        Task<List<uint>> FindAsync(string folder, uint uid, string? messageId, CancellationToken ct);
        /// <summary>Verschieben; liefert die neuen UIDs, soweit der Server sie nennt.</summary>
        Task<List<uint>> MoveAsync(string folder, IList<uint> uids, string target, CancellationToken ct);
        Task<List<uint>> DeletedAsync(string folder, CancellationToken ct);
        Task SetDeletedAsync(string folder, IList<uint> uids, bool on, CancellationToken ct);
        /// <summary>Mit <paramref name="uids"/> UID EXPUNGE, sonst EXPUNGE (alles mit \Deleted).</summary>
        Task ExpungeAsync(string folder, IList<uint>? uids, CancellationToken ct);
    }

    /// <summary>Faellige (oder bestimmte) Eintraege eines Kontos ausfuehren. Gibt zurueck, wie viele Mails entfernt wurden;
    /// in <paramref name="touched"/> die Ordner, die neu abgeglichen werden sollten.</summary>
    public static async Task<int> RunAsync(IServer server, UserSpace space, MailAccount account, Func<Entry, bool> which,
        ICollection<string> touched, CancellationToken ct)
    {
        var db = space.Db;
        var due = All(db).Where(e => e.Account == account.Id && which(e)).OrderBy(e => e.At).ToList();
        if (due.Count == 0) return 0;
        var folders = MailStore.Folders(db, account.Id);
        var trash = folders.FirstOrDefault(f => f.Special == "trash")?.Path;
        // Gmail: Loeschen in einem Ordner nimmt nur das Label weg - endgueltig erst ueber den Papierkorb
        bool viaTrash = folders.Any(f => f.Special == "all");
        var settled = new List<Entry>();
        int count = 0;
        try
        {
            foreach (var e in due)
            {
                ct.ThrowIfCancellationRequested();
                var found = await LocateAsync(server, db, folders, e, trash, ct);
                if (found.Count == 0)
                {
                    // Nicht (mehr) da: von Hand geloescht oder noch nicht abgeglichen - eine Weile weiter suchen
                    if (DateTime.UtcNow - e.At > GiveUp) { settled.Add(e); if (e.Kind == Destroy) await WipeAsync(space, account, e, []); }
                    continue;
                }
                if (e.Kind == Trash)
                {
                    if (trash == null) { settled.Add(e); continue; }
                    foreach (var (folder, uids) in found.Where(f => f.Folder != trash))
                    {
                        await server.MoveAsync(folder, uids, trash, ct);
                        MailStore.Delete(db, account.Id, folder, uids);
                        touched.Add(folder);
                        touched.Add(trash);
                    }
                }
                else
                {
                    foreach (var (folder, uids) in found)
                    {
                        var (f, u) = (folder, uids);
                        if (viaTrash && trash != null && folder != trash)
                        {
                            var moved = await server.MoveAsync(folder, uids, trash, ct);
                            (f, u) = (trash, moved.Count == uids.Count && moved.All(x => x != 0) ? moved
                                : e.MessageId != null ? await server.FindAsync(trash, 0, e.MessageId, ct) : []);
                        }
                        await ExpungeOnlyAsync(server, f, u, ct);
                        touched.Add(folder);
                    }
                    await WipeAsync(space, account, e, found);
                }
                settled.Add(e);
                count++;
            }
        }
        finally
        {
            // Erledigtes austragen - frisch gelesen, was die Oberflaeche inzwischen eingetragen hat, bleibt
            if (settled.Count > 0) await UpdateAsync(db, list => list.RemoveAll(x => settled.Any(d => Same(d, x))));
        }
        return count;
    }

    /// <summary>Wo liegt die Mail jetzt? Erst im Ordner beim Einstellen (nach UID oder Message-ID), dann ueberall, wo der Abgleich
    /// dieselbe Message-ID kennt (verschoben, Wiedervorlage, Papierkorb). Papierkorb: der erste Fundort; Vernichten: alle.</summary>
    private static async Task<List<(string Folder, List<uint> Uids)>> LocateAsync(IServer server, UserDb db, List<FolderRow> folders,
        Entry e, string? trash, CancellationToken ct)
    {
        var known = folders.Where(f => f.Selectable).Select(f => f.Path).ToHashSet();
        var candidates = new List<string> { e.Folder };
        if (e.MessageId != null)
        {
            candidates.AddRange(db.Query("SELECT DISTINCT folder FROM mail WHERE account=$a AND message_id=$m", r => r.GetString(0),
                ("$a", e.Account), ("$m", e.MessageId)));
            candidates.AddRange(folders.Where(f => f.Special is "snoozed" or "trash" or "archive").Select(f => f.Path));
        }
        var result = new List<(string, List<uint>)>();
        foreach (var folder in candidates.Distinct().Where(known.Contains))
        {
            // Ohne Message-ID nur der urspruengliche Platz
            if (folder != e.Folder && e.MessageId == null) continue;
            List<uint> uids;
            try { uids = await server.FindAsync(folder, folder == e.Folder ? e.Uid : 0, e.MessageId, ct); }
            catch (FolderNotFoundException) { continue; }
            if (uids.Count == 0) continue;
            if (e.Kind == Trash)
                // Schon im Papierkorb: nichts zu tun (leere Liste fuer den Papierkorb zaehlt als erledigt)
                return [(folder, folder == trash ? [] : uids)];
            result.Add((folder, uids));
        }
        return result;
    }

    /// <summary>Genau diese Mails tilgen. Ohne UIDPLUS loescht EXPUNGE alles mit \Deleted - auch, was ein anderes Programm nur
    /// markiert hat. Deren Markierung daher kurz wegnehmen, pruefen, dass nur noch die eigenen markiert sind, danach zuruecksetzen.
    /// Kommt in der Zwischenzeit doch eine fremde dazu, bleibt die eigene markiert und der naechste Durchgang tilgt sie.</summary>
    internal static async Task ExpungeOnlyAsync(IServer server, string folder, IList<uint> uids, CancellationToken ct)
    {
        if (uids.Count == 0) return;
        await server.SetDeletedAsync(folder, uids, true, ct);
        if (server.UidPlus)
        {
            await server.ExpungeAsync(folder, uids, ct);
            return;
        }
        var mine = uids.ToHashSet();
        var others = (await server.DeletedAsync(folder, ct)).Where(u => !mine.Contains(u)).ToList();
        if (others.Count > 0) await server.SetDeletedAsync(folder, others, false, ct);
        try
        {
            var marked = await server.DeletedAsync(folder, ct);
            if (marked.All(mine.Contains)) await server.ExpungeAsync(folder, null, ct);
            else throw new InvalidOperationException("Andere Mails im Ordner sind zum Löschen markiert – erneut beim nächsten Abgleich.");
        }
        finally
        {
            if (others.Count > 0) await server.SetDeletedAsync(folder, others, true, ct);
        }
    }

    /// <summary>Alles, was dmOffice von der vernichteten Mail hat: Zeilen in allen Ordnern (mit Inhalt und Suchindex), Text der
    /// Anhaenge, Anhang-Kopien fuer den Office-Server, Nachverfolgung, Virenbefund, Wiedervorlage.</summary>
    private static async Task WipeAsync(UserSpace space, MailAccount account, Entry e, List<(string Folder, List<uint> Uids)> found)
    {
        var db = space.Db;
        var places = found.SelectMany(f => f.Uids.Select(u => (f.Folder, Uid: u))).ToList();
        if (e.MessageId != null)
            places.AddRange(db.Query("SELECT folder, uid FROM mail WHERE account=$a AND message_id=$m",
                r => (r.GetString(0), (uint)r.GetInt64(1)), ("$a", account.Id), ("$m", e.MessageId)));
        places.Add((e.Folder, e.Uid));
        foreach (var g in places.Distinct().GroupBy(p => p.Folder))
        {
            MailStore.Delete(db, account.Id, g.Key, g.Select(p => p.Uid));
            foreach (var p in g) MailAttachmentCopy.Forget(db, account.Id, p.Folder, p.Uid, e.MessageId);
        }
        if (e.MessageId == null) return;
        // Ohne Wartezeit (sonst bleibt der Text der Anhaenge noch drei Tage fuer einen Umzug stehen)
        Try(() => db.Exec("DELETE FROM text_docs WHERE kind='mail' AND account=$a AND msgid=$m", ("$a", account.Id), ("$m", e.MessageId)));
        Try(() => db.Exec("DELETE FROM mail_followup WHERE account=$a AND message_id=$m", ("$a", account.Id), ("$m", e.MessageId)));
        Try(() => db.Exec("DELETE FROM mail_processed WHERE account=$a AND key=$m", ("$a", account.Id), ("$m", e.MessageId)));
        Try(() => db.Exec("DELETE FROM mail_virus WHERE message_id=$m", ("$m", e.MessageId)));
        await Snooze.ForgetAsync(db, account.Id, e.MessageId);

        // Tabellen, die es je nach Stand der Datenbank noch nicht gibt
        static void Try(Action a) { try { a(); } catch (Microsoft.Data.Sqlite.SqliteException) { } }
    }

    /// <summary>Die echte Seite fuer den Sync: ein offener Ordner zur Zeit, zum Schreiben.</summary>
    public sealed class Imap(ImapClient client) : IServer
    {
        private IMailFolder? _open;

        public bool UidPlus => client.Capabilities.HasFlag(ImapCapabilities.UidPlus);

        private async Task<IMailFolder> OpenAsync(string path, CancellationToken ct)
        {
            if (_open?.FullName == path && _open.IsOpen && _open.Access == FolderAccess.ReadWrite) return _open;
            var f = await client.GetFolderAsync(path, ct);
            await f.OpenAsync(FolderAccess.ReadWrite, ct);
            return _open = f;
        }

        private static List<UniqueId> Ids(IEnumerable<uint> uids) => uids.Select(u => new UniqueId(u)).ToList();

        public async Task<List<uint>> FindAsync(string folder, uint uid, string? messageId, CancellationToken ct)
        {
            var f = await OpenAsync(folder, ct);
            if (messageId != null)
            {
                // HEADER sucht Teilstrings - nur genau passende nehmen
                var hits = await f.SearchAsync(MailKit.Search.SearchQuery.HeaderContains("Message-ID", messageId), ct);
                if (hits.Count == 0) return [];
                var exact = await f.FetchAsync(hits, MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope, ct);
                return exact.Where(s => s.Envelope?.MessageId == messageId).Select(s => s.UniqueId.Id).ToList();
            }
            if (uid == 0) return [];
            return (await f.SearchAsync(MailKit.Search.SearchQuery.Uids(Ids([uid])), ct)).Select(u => u.Id).ToList();
        }

        public async Task<List<uint>> MoveAsync(string folder, IList<uint> uids, string target, CancellationToken ct)
        {
            var f = await OpenAsync(folder, ct);
            var map = await f.MoveToAsync(Ids(uids), await client.GetFolderAsync(target, ct), ct);
            return uids.Select(u => map != null && map.TryGetValue(new UniqueId(u), out var n) ? n.Id : 0u).ToList();
        }

        public async Task<List<uint>> DeletedAsync(string folder, CancellationToken ct) =>
            (await (await OpenAsync(folder, ct)).SearchAsync(MailKit.Search.SearchQuery.Deleted, ct)).Select(u => u.Id).ToList();

        public async Task SetDeletedAsync(string folder, IList<uint> uids, bool on, CancellationToken ct) =>
            await (await OpenAsync(folder, ct)).StoreAsync(Ids(uids),
                new StoreFlagsRequest(on ? StoreAction.Add : StoreAction.Remove, MessageFlags.Deleted) { Silent = true }, ct);

        public async Task ExpungeAsync(string folder, IList<uint>? uids, CancellationToken ct)
        {
            var f = await OpenAsync(folder, ct);
            if (uids != null) await f.ExpungeAsync(Ids(uids), ct);
            else await f.ExpungeAsync(ct);
        }
    }
}
