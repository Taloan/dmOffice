using System.Collections.Concurrent;
using DmOffice.Core;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MimeKit;

namespace DmOffice.Mail;

/// <summary>
/// Haelt die Mails jedes Benutzers in seiner Datenbank aktuell - wie Outlook
/// mit einer OST-Datei: Posteingang jede Minute, alle Ordner alle zehn
/// Minuten, auf Anstoss sofort. Ein Durchgang je Ordner:
///
///   1. UIDVALIDITY anders: Ordner hier leeren, neu holen.
///   2. UIDs im Zeitraum (alles oder N Monate) gegen die hiesigen: Neues holen
///      (Kopfzeilen), Fehlendes hier loeschen.
///   3. Flags: mit CONDSTORE nur Geaendertes, sonst alle.
///   4. Danach im Hintergrund die vollstaendigen Mails samt Anhaengen,
///      neueste zuerst, bis zur Groessengrenze des Kontos. Im Speichermodus
///      "Schlank" nur die juengsten (MailStore.SlimLimit), fuer die uebrigen
///      nur die Vorschau; ihr Inhalt kommt beim Oeffnen.
///
/// Neue Mails im Posteingang laufen durch Spamfilter und Regeln - nur die,
/// die nach dem ersten vollstaendigen Sync ankommen; sonst wuerde das
/// Einrichten eines Kontos alte Mails weiterleiten.
/// </summary>
public sealed class MailSync : BackgroundService
{
    public sealed class State
    {
        public bool Running { get; set; }
        public string? Phase { get; set; }
        public DateTime? LastQuick { get; set; }
        public DateTime? LastFull { get; set; }
        public string? Error { get; set; }
        public int PendingBodies { get; set; }
        internal bool KickFull;
        internal HashSet<string> KickFolders { get; } = new();
        /// <summary>Unbearbeitete Mails im Posteingang moeglich (nach dem Start, nach Fehlern) -
        /// dann laufen Spamfilter und Regeln auch ohne neue Mails.</summary>
        internal bool RulesPending = true;
        /// <summary>Letzter Durchgang mit Fehler - dann nicht alle 15 s wegen fehlender Inhalte erneut.</summary>
        internal bool Failed;
    }

    private readonly Spaces _spaces;
    private readonly ImapPool _pool;
    private readonly ILogger<MailSync> _log;
    private readonly ConcurrentDictionary<string, State> _states = new();
    private readonly ConcurrentDictionary<string, long> _versions = new();
    private readonly SemaphoreSlim _parallel = new(4);
    private readonly SemaphoreSlim _wake = new(0);

    private static readonly TimeSpan QuickEvery = TimeSpan.FromMinutes(1), FullEvery = TimeSpan.FromMinutes(10);

    public MailSync(Spaces spaces, ImapPool pool, ILogger<MailSync> log)
    {
        _spaces = spaces;
        _pool = pool;
        _log = log;
    }

    private static string Key(string user, string account) => user + "|" + account;

    public State StateOf(string user, string account) => _states.GetOrAdd(Key(user, account), _ => new State());

    /// <summary>Zaehlt hoch, sobald sich bei einem Benutzer etwas geaendert hat -
    /// die Oberflaeche fragt danach und laedt dann neu.</summary>
    public long Version(string user) => _versions.GetValueOrDefault(user);
    public void Touch(string user) => _versions.AddOrUpdate(user, 1, (_, v) => v + 1);

    /// <summary>Sofort synchronisieren - ein Ordner, oder alles.</summary>
    public void Kick(string user, string account, string? folder = null)
    {
        var s = StateOf(user, account);
        lock (s)
        {
            if (folder == null) s.KickFull = true;
            else s.KickFolders.Add(folder);
        }
        _wake.Release();
    }

    public void Forget(string user)
    {
        foreach (var k in _states.Keys.Where(k => k.StartsWith(user + "|"))) _states.TryRemove(k, out _);
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        await Task.Delay(TimeSpan.FromSeconds(3), stop);
        var running = new ConcurrentDictionary<string, Task>();
        while (!stop.IsCancellationRequested)
        {
            foreach (var space in _spaces.All())
            {
                foreach (var account in space.Settings.MailAccounts)
                {
                    if (string.IsNullOrEmpty(account.ImapHost)) continue;
                    var key = Key(space.User.Id, account.Id);
                    if (running.TryGetValue(key, out var t) && !t.IsCompleted) continue;
                    var state = StateOf(space.User.Id, account.Id);
                    bool full, quick;
                    string[] folders;
                    lock (state)
                    {
                        full = state.KickFull || state.LastFull == null || DateTime.UtcNow - state.LastFull > FullEvery;
                        quick = full || state.LastQuick == null || DateTime.UtcNow - state.LastQuick > QuickEvery || state.KickFolders.Count > 0;
                        folders = state.KickFolders.ToArray();
                        state.KickFolders.Clear();
                        state.KickFull = false;
                    }
                    if (!quick && (state.PendingBodies == 0 || state.Failed)) continue;
                    running[key] = Task.Run(() => RunAccount(space, account, state, full, folders, stop), stop);
                }
            }
            try { await _wake.WaitAsync(TimeSpan.FromSeconds(15), stop); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task RunAccount(UserSpace space, MailAccount account, State state, bool full, string[] kicked, CancellationToken stop)
    {
        await _parallel.WaitAsync(stop);
        state.Running = true;
        string? folderError = null;
        try
        {
            await _pool.UseAsync(account, ImapPool.Sync, async client =>
            {
                // Erneut erinnern: Faelliges zurueck in den Posteingang
                if (Snooze.All(space.Db).Any(e => e.Account == account.Id && e.Until <= DateTime.UtcNow))
                {
                    state.Phase = "Wiedervorlage";
                    try
                    {
                        if (await Snooze.ReturnAsync(client, space, account, e => e.Until <= DateTime.UtcNow, stop) > 0)
                        {
                            lock (state) state.KickFolders.Add("INBOX");
                            Touch(space.User.Id);
                        }
                    }
                    catch (Exception ex) when (Skippable(client, ex))
                    {
                        // Beim naechsten Durchgang erneut; der Sync selbst laeuft weiter
                        folderError = Snooze.FolderName + ": " + ex.Message;
                        _log.LogWarning("Mail-Sync {Account}, Erneut erinnern: {Error}", account.Email, ex.Message);
                    }
                }

                List<FolderRow> folders;
                if (full || MailStore.Folders(space.Db, account.Id).Count == 0)
                {
                    state.Phase = "Ordner";
                    await RefreshFolders(client, space, account, stop);
                }
                folders = MailStore.Folders(space.Db, account.Id).Where(f => f.Selectable && f.Special != "all").ToList();

                IEnumerable<FolderRow> todo = full
                    ? folders.OrderBy(f => Order(f.Special))
                    : folders.Where(f => f.Special == "inbox" || kicked.Contains(f.Path));
                foreach (var folder in todo)
                {
                    stop.ThrowIfCancellationRequested();
                    state.Phase = folder.Name;
                    try { await SyncFolder(client, space, account, folder, state, stop); }
                    catch (FolderNotFoundException) { await RefreshFolders(client, space, account, stop); }
                    catch (Exception ex) when (Skippable(client, ex))
                    {
                        // Ein Ordner, der sich nicht oeffnen laesst (NO, NOPERM), haelt die anderen nicht auf
                        folderError = folder.Name + ": " + ex.Message;
                        _log.LogWarning("Mail-Sync {Account}, Ordner {Folder}: {Error}", account.Email, folder.Path, ex.Message);
                    }
                }

                var now = DateTime.UtcNow;
                state.LastQuick = now;
                if (full)
                {
                    state.LastFull = now;
                    // Ab jetzt laufen Regeln und Spamfilter fuer Neues
                    if (space.Db.Get(RulesSinceKey(account)) == null) space.Db.Set(RulesSinceKey(account), now.ToString("o"));
                }

                state.Phase = "Inhalte";
                await DownloadBodies(client, space, account, folders, state, TimeSpan.FromSeconds(full ? 120 : 30), stop);
                // Pakete, Fluege, Hotels ... aus neuen Mails in den Kalender
                try { if (SmartMail.Scan(space, account) > 0) Touch(space.User.Id); }
                catch (Exception ex) { _log.LogInformation("Termine aus Mails {Account}: {Error}", account.Email, ex.Message); }
                // Journal nach dem Laden einarbeiten, damit es nicht waechst
                try { space.Db.Checkpoint(); } catch { /* beim naechsten Mal */ }
            }, stop);
            state.Error = folderError;
            state.Failed = false;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            state.Error = ex.Message;
            state.Failed = true;
            state.LastQuick = DateTime.UtcNow;   // nicht im Takt von 15 s gegen einen kaputten Zugang laufen
            state.LastFull ??= DateTime.UtcNow;
            _log.LogWarning("Mail-Sync {Account}: {Error}", account.Email, ex.Message);
        }
        finally
        {
            state.Running = false;
            state.Phase = null;
            _parallel.Release();
        }
    }

    private static string RulesSinceKey(MailAccount a) => $"mail.{a.Id}.rulesSince";

    /// <summary>Fehler, der nur einen Ordner oder eine Mail betrifft - nicht Abbruch oder Verbindung.</summary>
    private static bool Skippable(ImapClient c, Exception ex) =>
        ex is not OperationCanceledException && !ImapPool.IsConnectionProblem(ex) && c.IsConnected;

    private static int Order(string? special) => special switch
    {
        "inbox" => 0, "sent" => 1, "drafts" => 2, null => 3, "archive" => 4, "junk" => 5, "trash" => 6, _ => 7
    };

    // ---------------- Ordner ----------------

    private static async Task RefreshFolders(ImapClient c, UserSpace space, MailAccount account, CancellationToken ct)
    {
        var all = new List<IMailFolder>();
        foreach (var ns in c.PersonalNamespaces)
            all.AddRange(await c.GetFoldersAsync(ns, false, ct));
        if (!all.Any(f => f.FullName.Equals("INBOX", StringComparison.OrdinalIgnoreCase))) all.Insert(0, c.Inbox);

        var rows = all.Where(f => f.Exists)
            .Select(f => (f.FullName, f.Name, f.DirectorySeparator.ToString(), SpecialOf(f),
                !f.Attributes.HasFlag(FolderAttributes.NoSelect) && !f.Attributes.HasFlag(FolderAttributes.NonExistent)))
            .ToList();
        MailStore.ReplaceFolders(space.Db, account.Id, rows);
    }

    private static readonly (string Kind, FolderAttributes Attr, string[] Names)[] Specials =
    {
        ("drafts", FolderAttributes.Drafts, new[] { "drafts", "entwürfe", "entwurf", "draft" }),
        ("sent", FolderAttributes.Sent, new[] { "sent", "gesendet", "sent items", "sent messages", "gesendete objekte", "gesendete elemente" }),
        ("trash", FolderAttributes.Trash, new[] { "trash", "papierkorb", "deleted", "deleted items", "deleted messages", "gelöschte elemente", "gelöscht" }),
        ("junk", FolderAttributes.Junk, new[] { "junk", "spam", "junk-e-mail", "junk e-mail", "junk-mail" }),
        ("archive", FolderAttributes.Archive, new[] { "archive", "archiv" }),
        ("all", FolderAttributes.All, Array.Empty<string>()),
        // Kein IMAP-Attribut dafuer - nur am Namen erkannt (Outlook legt ihn genauso an)
        ("snoozed", FolderAttributes.None, new[] { "wiedervorlage", "erneut erinnern", "snoozed", "erneut erinnern aktiviert" }),
    };

    public static string? SpecialOf(IMailFolder f)
    {
        if (f.FullName.Equals("INBOX", StringComparison.OrdinalIgnoreCase)) return "inbox";
        foreach (var (kind, attr, _) in Specials)
            if (attr != FolderAttributes.None && f.Attributes.HasFlag(attr)) return kind;
        var parent = f.ParentFolder?.FullName ?? "";
        if (parent.Length > 0 && !parent.Equals("INBOX", StringComparison.OrdinalIgnoreCase)) return null;
        var name = f.Name.ToLowerInvariant();
        foreach (var (kind, _, names) in Specials)
            if (names.Contains(name)) return kind;
        return null;
    }

    public static async Task<IMailFolder?> Special(ImapClient c, UserSpace space, MailAccount account, string special, CancellationToken ct)
    {
        var row = MailStore.Folders(space.Db, account.Id).FirstOrDefault(f => f.Special == special);
        if (row != null)
            try { return await c.GetFolderAsync(row.Path, ct); } catch (FolderNotFoundException) { }
        return null;
    }

    // ---------------- Ein Ordner ----------------

    private async Task SyncFolder(ImapClient c, UserSpace space, MailAccount account, FolderRow row, State state, CancellationToken ct)
    {
        var db = space.Db;
        var f = await c.GetFolderAsync(row.Path, ct);
        await f.OpenAsync(FolderAccess.ReadOnly, ct);

        bool wiped = false;
        if (row.UidValidity != 0 && row.UidValidity != f.UidValidity)
        {
            MailStore.WipeFolder(db, account.Id, row.Path);
            row = row with { ModSeq = 0 };
            wiped = true;
        }

        // Mit \Deleted markierte (ohne UIDPLUS nicht sofort entfernt) gehoeren nicht mehr dazu
        SearchQuery window = account.SyncMonths > 0
            ? SearchQuery.DeliveredAfter(DateTime.Today.AddMonths(-account.SyncMonths)).And(SearchQuery.NotDeleted)
            : SearchQuery.NotDeleted;
        var server = (await f.SearchAsync(window, ct)).ToList();
        var serverSet = server.Select(u => u.Id).ToHashSet();
        var local = MailStore.Uids(db, account.Id, row.Path);

        var removed = local.Where(u => !serverSet.Contains(u)).ToList();
        if (removed.Count > 0) MailStore.Delete(db, account.Id, row.Path, removed);

        // Gerade verschoben/geloescht, aber noch auf dem Server: nicht wieder aufnehmen (MailOutbox)
        var user = space.User.Id;
        var fresh = server.Where(u => !local.Contains(u.Id) && !MailPending.Removing(user, account.Id, row.Path, u.Id))
            .OrderByDescending(u => u.Id).ToList();
        foreach (var batch in fresh.Chunk(250))
        {
            ct.ThrowIfCancellationRequested();
            var summaries = await f.FetchAsync(batch, MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope |
                MessageSummaryItems.Flags | MessageSummaryItems.InternalDate | MessageSummaryItems.Size |
                MessageSummaryItems.BodyStructure | MessageSummaryItems.References, ct);
            MailStore.Insert(db, account.Id, row.Path, summaries);
        }

        // Flags der schon bekannten
        var known = server.Where(u => local.Contains(u.Id)).ToList();
        if (known.Count > 0)
        {
            IList<IMessageSummary> changed;
            if (f.Supports(FolderFeature.ModSequences) && row.ModSeq > 0 && row.UidValidity == f.UidValidity)
                changed = await f.FetchAsync(known, new FetchRequest(MessageSummaryItems.UniqueId | MessageSummaryItems.Flags) { ChangedSince = (ulong)row.ModSeq }, ct);
            else
            {
                var all = new List<IMessageSummary>();
                foreach (var batch in known.Chunk(2000))
                    all.AddRange(await f.FetchAsync(batch, MessageSummaryItems.UniqueId | MessageSummaryItems.Flags, ct));
                changed = all;
            }
            foreach (var s in changed)
                // Fahne/gelesen gerade unterwegs zum Server: den alten Stand nicht zurueckschreiben
                if (s.Flags is MessageFlags fl && !MailPending.Flagging(user, account.Id, row.Path, s.UniqueId.Id))
                    MailStore.SetFlags(db, account.Id, row.Path, s.UniqueId.Id, fl, s.Keywords);
        }

        var unread = (await f.SearchAsync(SearchQuery.NotSeen.And(SearchQuery.NotDeleted), ct)).Count;
        var localUnread = Convert.ToInt32(db.Scalar("SELECT COUNT(*) FROM mail WHERE account=$a AND folder=$f AND seen=0", ("$a", account.Id), ("$f", row.Path)));
        db.Set($"mail.{account.Id}.outside.{row.Path}", Math.Max(0, unread - localUnread).ToString());
        // Gesamtzahl ohne als geloescht markierte (die zeigt die Liste auch nicht) - sonst bleibt z. B. "1" an einem leeren Ordner stehen
        // Zahl wie die Liste: nur was im Synchronisierungszeitraum liegt (sonst "5" bei leerem Ordner)
        var total = server.Count;
        MailStore.SetFolderState(db, account.Id, row.Path, f.UidValidity, (long)f.HighestModSeq, unread, total);

        if (fresh.Count > 0 || removed.Count > 0 || known.Count > 0) Touch(space.User.Id);

        // Neues im Posteingang: Spamfilter und Regeln
        if (row.Special == "inbox" && DateTime.TryParse(db.Get(RulesSinceKey(account)), null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var since))
        {
            if (wiped)
            {
                // Neue UIDs fuer alles - das sind keine neuen Mails: nicht auswerten, nur merken
                MailStore.SetProcessed(db, account.Id, MailStore.ProcessKeys(db, account.Id, row.Path).Values.Distinct(), MailStore.StageDone);
                state.RulesPending = false;
            }
            else if (fresh.Count > 0 || state.RulesPending)
            {
                // Bricht es ab (Verbindung), beim naechsten Mal erneut
                state.RulesPending = true;
                state.RulesPending = await ProcessNew(c, space, account, f, fresh.Select(u => u.Id).ToHashSet(), since, ct);
            }
        }
    }

    // ---------------- Spamfilter und Regeln ----------------

    /// <summary>Wie lange bearbeitete Mails gemerkt werden. Aeltere Mails (nach Eingangsdatum)
    /// gelten ohnehin als bearbeitet - mit Abstand, damit nichts dazwischen faellt.</summary>
    private static readonly TimeSpan ProcessedKeep = TimeSpan.FromDays(90), ProcessedFloor = TimeSpan.FromDays(60);

    /// <summary>So oft wird eine Mail versucht, bei der Spamfilter oder Regeln scheitern (z. B. MOVE wegen Quota).</summary>
    private const int MaxRuleTries = 10;

    /// <summary>Seit wann bearbeitete Mails gemerkt werden. Vorher Liegendes im Posteingang ist schon
    /// bearbeitet (alte Fassung ohne Liste) und laeuft nicht noch einmal.</summary>
    private static DateTime TrackedSince(UserDb db, MailAccount a)
    {
        var key = $"mail.{a.Id}.processedSince";
        if (DateTime.TryParse(db.Get(key), null, System.Globalization.DateTimeStyles.RoundtripKind, out var t)) return t;
        var now = DateTime.UtcNow;
        db.Set(key, now.ToString("o"));
        return now;
    }

    /// <summary>Unbearbeitete Mails im Posteingang durch Spamfilter und Regeln. Bearbeitet heisst: je Konto
    /// nach Message-ID gemerkt - kommt eine Mail zurueck (Erneut erinnern, Kein Junk, von Hand), laeuft
    /// nichts noch einmal. Gibt zurueck, ob Mails nach einem Fehler noch offen sind.</summary>
    private async Task<bool> ProcessNew(ImapClient c, UserSpace space, MailAccount account, IMailFolder inbox,
        HashSet<uint> fresh, DateTime since, CancellationToken ct)
    {
        var db = space.Db;
        var settings = space.Settings;
        var rules = settings.Rules.Where(r => r.Enabled && (r.AccountId == null || r.AccountId == account.Id)).ToList();
        var (nspam, nham) = SpamFilter.Counts(db);
        bool spamActive = settings.Spam.Enabled && nspam >= settings.Spam.MinTrained && nham >= settings.Spam.MinTrained;
        // Mails sicherer Absender lernen - auch schon, bevor genug gelernt ist, um zu verschieben
        bool learnKnown = settings.Spam.Enabled && settings.Spam.LearnKnown;

        MailStore.PruneProcessed(db, ProcessedKeep);
        var keys = MailStore.ProcessKeys(db, account.Id, inbox.FullName);
        var known = MailStore.Processed(db, account.Id);
        var open = keys.Where(k => !known.TryGetValue(k.Value, out var p) || p.Stage < MailStore.StageDone).ToList();
        if (open.Count == 0) return false;

        // Nichts zu tun: trotzdem merken, sonst liefen sie spaeter doch noch (Regel neu, Rueckkehr)
        if (rules.Count == 0 && !spamActive && !learnKnown && !(settings.FocusedInbox && account.FocusedInbox))
        {
            MailStore.SetProcessed(db, account.Id, open.Select(k => k.Value).Distinct(), MailStore.StageDone);
            return false;
        }

        // Neu Angekommenes ab dem ersten vollstaendigen Sync; Uebriggebliebenes (nach Fehler) erst ab
        // Beginn der Liste - nie aber Mails, die schon laenger da sind, als die Liste reicht
        var floor = DateTime.UtcNow - ProcessedFloor;
        var tracked = TrackedSince(db, account);
        var forOld = new[] { since, tracked, floor }.Max();
        var forFresh = since > floor ? since : floor;

        var dates = new List<IMessageSummary>();
        foreach (var batch in open.Select(k => new UniqueId(k.Key)).Chunk(1000))
            dates.AddRange(await inbox.FetchAsync(batch, MessageSummaryItems.UniqueId | MessageSummaryItems.InternalDate, ct));
        var settled = new List<string>();
        var todo = new List<UniqueId>();
        foreach (var s in dates)
        {
            if (!keys.TryGetValue(s.UniqueId.Id, out var key)) continue;
            var arrived = (s.InternalDate ?? DateTimeOffset.MinValue).UtcDateTime;
            if (arrived >= (fresh.Contains(s.UniqueId.Id) ? forFresh : forOld)) todo.Add(s.UniqueId);
            else settled.Add(key);
        }
        if (settled.Count > 0) MailStore.SetProcessed(db, account.Id, settled.Distinct(), MailStore.StageDone);
        if (todo.Count == 0) return false;

        bool pending = false;
        await inbox.OpenAsync(FolderAccess.ReadWrite, ct);
        foreach (var uid in todo)
        {
            var key = keys[uid.Id];
            // Nach einem Fehler steht hier, was schon geschehen ist (Weiterleitung, Kopie)
            var stage = known.TryGetValue(key, out var p) ? p.Stage : 0;
            try
            {
                await ProcessOne(c, space, account, inbox, uid, key, stage, rules, spamActive, ct);
                MailStore.SetProcessed(db, account.Id, new[] { key }, MailStore.StageDone);
            }
            catch (Exception ex) when (Skippable(c, ex))
            {
                // Eine Mail haelt die anderen nicht auf; erst nach Erfolg gilt sie als bearbeitet
                var tries = MailStore.ProcessFailed(db, account.Id, key);
                if (tries >= MaxRuleTries)
                {
                    MailStore.SetProcessed(db, account.Id, new[] { key }, MailStore.StageDone);
                    _log.LogWarning("Regeln/Spamfilter {Account}: Mail {Uid} nach {Tries} Versuchen aufgegeben: {Error}", account.Email, uid.Id, tries, ex.Message);
                }
                else
                {
                    pending = true;
                    _log.LogWarning("Regeln/Spamfilter {Account}: Mail {Uid} fehlgeschlagen (Versuch {Tries}): {Error}", account.Email, uid.Id, tries, ex.Message);
                }
            }
        }
        await inbox.OpenAsync(FolderAccess.ReadOnly, ct);
        return pending;
    }

    private async Task ProcessOne(ImapClient c, UserSpace space, MailAccount account, IMailFolder inbox, UniqueId uid,
        string key, int stage, List<MailRule> rules, bool spamActive, CancellationToken ct)
    {
        var settings = space.Settings;
        var message = await Fetch(inbox, space, account, uid, ct);
        if (message == null) return;

        // Sichere Absender (Adressbuch, selbst angeschrieben): nie Junk, auf Wunsch als gut lernen
        var spam = settings.Spam;
        bool known = spam.Enabled && (spam.TrustKnown || spam.LearnKnown) && SafeSenders.IsKnown(space, message);
        if (known && spam.LearnKnown) SpamFilter.Train(space.Db, message, spam: false);

        // Bewertet wird erst, wenn genug gelernt ist - vorher kaeme fuer jede Mail 50 % heraus
        if (spamActive)
        {
            var score = SpamFilter.Score(space.Db, message);
            MailStore.SetSpamScore(space.Db, account.Id, inbox.FullName, uid.Id, score);
            if (score >= settings.Spam.SpamCutoff && !(known && spam.TrustKnown) && await Special(c, space, account, "junk", ct) is { } junk)
            {
                await inbox.MoveToAsync(uid, junk, ct);
                MailStore.Delete(space.Db, account.Id, inbox.FullName, new[] { uid.Id });
                lock (StateOf(space.User.Id, account.Id)) StateOf(space.User.Id, account.Id).KickFolders.Add(junk.FullName);
                return;
            }
        }

        // Prio-Posteingang: Relevant oder Sonstige (nach Inhalt, Absender, Gelerntem)
        if (settings.FocusedInbox && account.FocusedInbox)
            MailStore.SetFocus(space.Db, account.Id, inbox.FullName, new[] { uid.Id }, Focus.Classify(space, message));

        if (rules.Count == 0) return;
        var outcome = Rules.Evaluate(rules, account.Id, message);
        if (outcome.Applied.Count == 0) return;
        await Apply(c, space, account, inbox, uid, key, stage, message, outcome, ct);
    }

    private async Task Apply(ImapClient c, UserSpace space, MailAccount account, IMailFolder inbox, UniqueId uid,
        string key, int stage, MimeMessage message, RuleOutcome o, CancellationToken ct)
    {
        // Weiterleiten und Kopieren nur einmal - auch wenn danach etwas scheitert und die Mail wiederholt wird
        if (stage < MailStore.StageForwarded && o.ForwardTo.Count > 0)
        {
            foreach (var to in o.ForwardTo)
            {
                try
                {
                    using var smtp = await ImapPool.SmtpAsync(account, ct);
                    await smtp.SendAsync(MailCompose.Forward(account, message, to), ct);
                    await smtp.DisconnectAsync(true, ct);
                    await inbox.StoreAsync(new[] { uid }, new StoreFlagsRequest(StoreAction.Add, MessageFlags.None) { Silent = true, Keywords = { "$Forwarded" } }, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning("Regel: Weiterleiten an {To} fehlgeschlagen: {Error}", to, ex.Message); }
            }
            MailStore.SetProcessed(space.Db, account.Id, new[] { key }, MailStore.StageForwarded);
        }
        if (o.MarkRead)
        {
            await inbox.StoreAsync(new[] { uid }, new StoreFlagsRequest(StoreAction.Add, MessageFlags.Seen) { Silent = true }, ct);
            MailStore.SetFlag(space.Db, account.Id, inbox.FullName, new[] { uid.Id }, "seen", true);
        }
        if (o.Flag)
        {
            await inbox.StoreAsync(new[] { uid }, new StoreFlagsRequest(StoreAction.Add, MessageFlags.Flagged) { Silent = true }, ct);
            MailStore.SetFlag(space.Db, account.Id, inbox.FullName, new[] { uid.Id }, "flagged", true);
        }
        if (o.Categories.Count > 0)
        {
            try
            {
                var req = new StoreFlagsRequest(StoreAction.Add, MessageFlags.None) { Silent = true };
                foreach (var k in o.Categories) req.Keywords.Add(k);
                await inbox.StoreAsync(new[] { uid }, req, ct);
                foreach (var k in o.Categories) MailStore.SetKeyword(space.Db, account.Id, inbox.FullName, new[] { uid.Id }, k, true);
            }
            catch (Exception ex) when (Skippable(c, ex)) { _log.LogWarning("Regel: Kategorie setzen: {Error}", ex.Message); }
        }
        if (stage < MailStore.StageCopied && o.CopyTo.Count > 0)
        {
            foreach (var target in o.CopyTo)
                try { await inbox.CopyToAsync(uid, await c.GetFolderAsync(target, ct), ct); Kick(space.User.Id, account.Id, target); }
                catch (FolderNotFoundException) { }
            MailStore.SetProcessed(space.Db, account.Id, new[] { key }, MailStore.StageCopied);
        }

        string? moveTo = o.Delete ? (await Special(c, space, account, "trash", ct))?.FullName : o.MoveTo;
        if (moveTo != null && moveTo != inbox.FullName)
        {
            try
            {
                await inbox.MoveToAsync(uid, await c.GetFolderAsync(moveTo, ct), ct);
                MailStore.Delete(space.Db, account.Id, inbox.FullName, new[] { uid.Id });
                lock (StateOf(space.User.Id, account.Id)) StateOf(space.User.Id, account.Id).KickFolders.Add(moveTo);
            }
            catch (FolderNotFoundException) { _log.LogWarning("Regel: Ordner {Folder} gibt es nicht", moveTo); }
        }
        Touch(space.User.Id);
    }

    // ---------------- Inhalte ----------------

    /// <summary>Holt eine Mail vom Server und legt sie ab (wenn nicht zu gross).</summary>
    public static async Task<MimeMessage?> Fetch(IMailFolder f, UserSpace space, MailAccount account, UniqueId uid, CancellationToken ct)
    {
        if (MailStore.Raw(space.Db, account.Id, f.FullName, uid.Id) is { } cached)
            return MimeMessage.Load(new MemoryStream(cached));
        MimeMessage message;
        try { message = await f.GetMessageAsync(uid, ct); }
        catch (MessageNotFoundException) { return null; }
        using var ms = new MemoryStream();
        await message.WriteToAsync(ms, ct);
        if (ms.Length <= account.MaxCachedMb * 1024L * 1024L)
            MailStore.StoreRaw(space.Db, account.Id, f.FullName, uid.Id, ms.ToArray(), message);
        return message;
    }

    private async Task DownloadBodies(ImapClient c, UserSpace space, MailAccount account, List<FolderRow> folders,
        State state, TimeSpan budget, CancellationToken ct)
    {
        var limit = account.MaxCachedMb * 1024L * 1024L;
        var slim = MailStore.SlimLimit.Of(account, DateTimeOffset.UtcNow);
        var slimMissing = slim == null ? null : MailStore.MissingBodies(space.Db, account.Id, limit, slim);
        var until = DateTime.UtcNow + budget;
        bool any = false;
        try
        {
            foreach (var row in folders.OrderBy(f => Order(f.Special)))
            {
                var missing = slimMissing?[row.Path].ToList() ?? MailStore.MissingBodies(space.Db, account.Id, row.Path, limit, 100_000);
                var previews = slim == null ? new List<uint>() : MailStore.MissingPreviews(space.Db, account.Id, row.Path, 100_000);
                if (missing.Count == 0 && previews.Count == 0) continue;
                IMailFolder f;
                try
                {
                    f = await c.GetFolderAsync(row.Path, ct);
                    await f.OpenAsync(FolderAccess.ReadOnly, ct);
                }
                catch (Exception ex) when (Skippable(c, ex))
                {
                    _log.LogWarning("Mail-Sync {Account}, Inhalte in {Folder}: {Error}", account.Email, row.Path, ex.Message);
                    continue;
                }
                // Schlank: fuer Mails ohne Inhalt hier nur die Vorschau (Liste und Suche) - wenige Bytes je Mail
                foreach (var batch in previews.Chunk(200))
                {
                    if (DateTime.UtcNow > until) return;
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var got = await f.FetchAsync(batch.Select(u => new UniqueId(u)).ToList(), MessageSummaryItems.UniqueId | MessageSummaryItems.PreviewText, ct);
                        MailStore.SetPreviews(space.Db, account.Id, row.Path, got.Select(x => (x.UniqueId.Id, x.PreviewText)));
                        any = true;
                    }
                    catch (Exception ex) when (Skippable(c, ex))
                    {
                        _log.LogWarning("Mail-Sync {Account}: Vorschau in {Folder} nicht geladen: {Error}", account.Email, row.Path, ex.Message);
                        break;
                    }
                }
                foreach (var batch in missing.Chunk(20))
                {
                    if (DateTime.UtcNow > until) return;
                    ct.ThrowIfCancellationRequested();
                    foreach (var (uid, _) in batch)
                    {
                        try
                        {
                            var message = await f.GetMessageAsync(new UniqueId(uid), ct);
                            using var ms = new MemoryStream();
                            await message.WriteToAsync(ms, ct);
                            MailStore.StoreRaw(space.Db, account.Id, row.Path, uid, ms.ToArray(), message);
                            any = true;
                        }
                        catch (MessageNotFoundException) { MailStore.Delete(space.Db, account.Id, row.Path, new[] { uid }); }
                        catch (Exception ex) when (Skippable(c, ex))
                        {
                            // Eine kaputte Mail (FETCH NO, nicht lesbar) haelt die anderen nicht auf - spaeter erneut
                            MailStore.BodyFailed(space.Db, account.Id, row.Path, uid);
                            _log.LogWarning("Mail-Sync {Account}: Inhalt {Folder}/{Uid} nicht geladen: {Error}", account.Email, row.Path, uid, ex.Message);
                        }
                    }
                }
            }
        }
        finally
        {
            // Auch nach einem Fehler, sonst bleibt der alte Stand stehen
            try { state.PendingBodies = MailStore.MissingCount(space.Db, account.Id, limit, slim); }
            catch (Exception ex) { _log.LogWarning("Mail-Sync {Account}: fehlende Inhalte zaehlen: {Error}", account.Email, ex.Message); }
            if (any) Touch(space.User.Id);
        }
    }
}
