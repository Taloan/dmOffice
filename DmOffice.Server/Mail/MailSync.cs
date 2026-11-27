using System.Collections.Concurrent;
using DmOffice.Core;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
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
    // [feature:mailpush]
    internal static readonly TimeSpan QuickEveryPush = TimeSpan.FromMinutes(5);
    // [/feature:mailpush]

    private readonly ServerConfig? _config;

    public MailSync(Spaces spaces, ImapPool pool, ILogger<MailSync> log, ServerConfig? config = null)
    {
        _config = config;
        _spaces = spaces;
        _pool = pool;
        _log = log;
    }

    private static string Key(string user, string account) => user + "|" + account;

    public State StateOf(string user, string account) => _states.GetOrAdd(Key(user, account), _ => new State());

    /// <summary>Zaehlt hoch, sobald sich bei einem Benutzer etwas geaendert hat -
    /// die Oberflaeche fragt danach und laedt dann neu.</summary>
    public long Version(string user) => _versions.GetValueOrDefault(user);
    public void Touch(string user)
    {
        _versions.AddOrUpdate(user, 1, (_, v) => v + 1);
        // [feature:mailpush]
        if (_waiters.TryRemove(user, out var tcs)) tcs.TrySetResult();
        // [/feature:mailpush]
    }

    // [feature:mailpush]
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _waiters = new();

    /// <summary>Warten, bis sich die Version des Benutzers von <paramref name="since"/> unterscheidet (höchstens
    /// <paramref name="timeout"/>); zurück: die Version. Ungleich statt größer: nach einem Neustart des Servers beginnt sie wieder bei 0.</summary>
    public async Task<long> WaitAsync(string user, long since, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = _waiters.GetOrAdd(user, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        if (Version(user) != since) return Version(user);
        try { await tcs.Task.WaitAsync(timeout, ct); }
        catch (TimeoutException) { }
        return Version(user);
    }

    /// <summary>Hält eine IDLE-Verbindung (MailPush) den Posteingang dieses Kontos gerade im Blick? Dann genügt der seltenere Takt.</summary>
    internal Func<string, string, bool>? PushLive { get; set; }
    // [/feature:mailpush]

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
            foreach (var space in _spaces.Working())
            {
                // Deaktivierte Konten (Kennung mailaccountoff) ruhen: kein Abruf, keine Wiedervorlage, kein Entfernen nach Zeit
                foreach (var account in space.Settings.ActiveMailAccounts())
                {
                    if (string.IsNullOrEmpty(account.ImapHost)) continue;
                    var key = Key(space.User.Id, account.Id);
                    if (running.TryGetValue(key, out var t) && !t.IsCompleted) continue;
                    var state = StateOf(space.User.Id, account.Id);
                    bool full, quick;
                    string[] folders;
                    var quickEvery = QuickEvery;
                    // [feature:mailpush]
                    // Meldet der Mailserver Neues selbst (IDLE), ist der Minutentakt nur noch Sicherheitsnetz - Fälliges der
                    // Wiedervorlage und des Entfernens nach Zeit kommt trotzdem pünktlich
                    if (PushLive?.Invoke(space.User.Id, account.Id) == true && !DueWork(space, account)) quickEvery = QuickEveryPush;
                    // [/feature:mailpush]
                    lock (state)
                    {
                        full = state.KickFull || state.LastFull == null || DateTime.UtcNow - state.LastFull > FullEvery;
                        quick = full || state.LastQuick == null || DateTime.UtcNow - state.LastQuick > quickEvery || state.KickFolders.Count > 0;
                        folders = state.KickFolders.ToArray();
                        state.KickFolders.Clear();
                        state.KickFull = false;
                    }
                    if (!quick && (state.PendingBodies == 0 || state.Failed)) continue;
                    running[key] = Task.Run(() => RunAccount(space, account, state, full, folders, stop), stop);
                    // [feature:mailpush]
                    // Während des Durchgangs angestoßen (IDLE meldet Neues): gleich danach weiter, nicht erst nach 15 s
                    _ = running[key].ContinueWith(_ => { lock (state) if (state.KickFull || state.KickFolders.Count > 0) _wake.Release(); }, TaskScheduler.Default);
                    // [/feature:mailpush]
                }
            }
            try { await _wake.WaitAsync(TimeSpan.FromSeconds(15), stop); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Meldung fuer die Person: Verbindungs- und Anmeldefehler auf Deutsch statt des Ausnahmetexts der Bibliothek
    /// („Name or service not known“, „The operation has timed out.“); die Oberflaeche uebersetzt sie wie andere Meldungen.</summary>
    internal static string Readable(Exception ex) => ex switch
    {
        MailKit.Security.AuthenticationException => "Anmeldung fehlgeschlagen (Benutzer oder Passwort).",
        TimeoutException or System.Security.Authentication.AuthenticationException => "Keine Verbindung zum Server.",
        _ when ImapPool.IsConnectionProblem(ex) => "Keine Verbindung zum Server.",
        _ => ex.Message,
    };

    private async Task RunAccount(UserSpace space, MailAccount account, State state, bool full, string[] kicked, CancellationToken stop)
    {
        // Kontingent ausgeschoepft: nichts Neues abrufen, bis wieder Platz ist (Aufbewahrung, Papierkorb leeren, Versionen, Mails loeschen)
        if (UserStorage.Exceeded(space.Db))
        {
            state.Error = "Kontingent ausgeschöpft – neue Mails werden erst wieder abgerufen, wenn Platz frei ist.";
            return;
        }
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
                        folderError = Snooze.FolderName + ": " + Readable(ex);
                        _log.LogWarning("Mail-Sync {Account}, Erneut erinnern: {Error}", LogNames.Of(space, account), ex.Message);
                    }
                }
                // Entfernen nach Zeit (in den Papierkorb, vernichten) - derselbe Weg wie die Wiedervorlage
                if (Features.On(Features.MailExpiry) && MailExpiry.All(space.Db).Any(e => e.Account == account.Id && e.At <= DateTime.UtcNow))
                {
                    state.Phase = "Entfernen nach Zeit";
                    try
                    {
                        var touched = new HashSet<string>();
                        if (await MailExpiry.RunAsync(new MailExpiry.Imap(client), space, account, e => e.At <= DateTime.UtcNow, touched, stop) > 0)
                        {
                            lock (state) state.KickFolders.UnionWith(touched);
                            Touch(space.User.Id);
                        }
                    }
                    catch (Exception ex) when (Skippable(client, ex) || ex is InvalidOperationException)
                    {
                        folderError = "Entfernen nach Zeit: " + Readable(ex);
                        _log.LogWarning("Mail-Sync {Account}, Entfernen nach Zeit: {Error}", LogNames.Of(space, account), ex.Message);
                    }
                }

                List<FolderRow> folders;
                if (full || MailStore.Folders(space.Db, account.Id).Count == 0)
                {
                    state.Phase = "Ordner";
                    // Neue, umbenannte oder entfernte Ordner (z. B. im Webmail des Anbieters angelegt): Browser neu laden lassen
                    var before = string.Join('\n', MailStore.Folders(space.Db, account.Id).Select(f => f.Path));
                    await RefreshFolders(client, space, account, stop);
                    if (before != string.Join('\n', MailStore.Folders(space.Db, account.Id).Select(f => f.Path))) Touch(space.User.Id);
                }
                folders = MailStore.Folders(space.Db, account.Id).Where(f => f.Selectable && f.Special != "all").ToList();

                IEnumerable<FolderRow> todo = full
                    ? folders.OrderBy(f => Order(f.Special))
                    : folders.Where(f => f.Special == "inbox" || kicked.Contains(f.Path)
                        // [feature:junkrecheck]
                        // Junk so oft wie den Posteingang abrufen, damit die Nachprüfung gleich beim Eintreffen läuft
                        || (f.Special == "junk" && JunkRecheckOn(space))
                        // [/feature:junkrecheck]
                        );
                var done = new HashSet<string>();
                async Task SyncAll(IEnumerable<FolderRow> list)
                {
                    foreach (var folder in list)
                    {
                        stop.ThrowIfCancellationRequested();
                        state.Phase = folder.Name;
                        done.Add(folder.Path);
                        try { await SyncFolder(client, space, account, folder, state, stop); }
                        catch (FolderNotFoundException) { await RefreshFolders(client, space, account, stop); }
                        catch (Exception ex) when (Skippable(client, ex))
                        {
                            // Ein Ordner, der sich nicht oeffnen laesst (NO, NOPERM), haelt die anderen nicht auf
                            folderError = folder.Name + ": " + Readable(ex);
                            _log.LogWarning("Mail-Sync {Account}, ein Ordner: {Error}", LogNames.Of(space, account), ex.Message);
                        }
                    }
                }
                await SyncAll(todo.ToList());
                // [feature:mailprefetch]
                // Von Spamfilter oder Regeln gerade einsortiert (Junk, Zielordner): diese Ordner gleich mit abgleichen, statt bis zum
                // nächsten Durchgang zu warten - so erscheinen die Mails dort zusammen mit der Meldung, samt Inhalt
                if (PrefetchOn)
                {
                    string[] moved;
                    lock (state) moved = state.KickFolders.Where(p => !done.Contains(p) && folders.Any(f => f.Path == p)).ToArray();
                    if (moved.Length > 0)
                    {
                        lock (state) state.KickFolders.ExceptWith(moved);
                        await SyncAll(folders.Where(f => moved.Contains(f.Path)).ToList());
                    }
                }
                // [/feature:mailprefetch]

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
                catch (Exception ex) { _log.LogInformation("Termine aus Mails {Account}: {Error}", LogNames.Of(space, account), ex.Message); }
                // Journal nach dem Laden einarbeiten, damit es nicht waechst
                try { space.Db.Checkpoint(); } catch { /* beim naechsten Mal */ }
            }, stop);
            state.Error = folderError;
            state.Failed = false;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            state.Error = Readable(ex);
            state.Failed = true;
            state.LastQuick = DateTime.UtcNow;   // nicht im Takt von 15 s gegen einen kaputten Zugang laufen
            state.LastFull ??= DateTime.UtcNow;
            _log.LogWarning("Mail-Sync {Account}: {Error}", LogNames.Of(space, account), ex.Message);
        }
        finally
        {
            state.Running = false;
            state.Phase = null;
            _parallel.Release();
        }
    }

    private static string RulesSinceKey(MailAccount a) => $"mail.{a.Id}.rulesSince";

    // [feature:mailpush]
    /// <summary>Wiedervorlage oder Entfernen nach Zeit fällig - das erledigt nur der Durchgang im Takt, nicht die IDLE-Meldung.</summary>
    private static bool DueWork(UserSpace space, MailAccount account)
    {
        var now = DateTime.UtcNow;
        try
        {
            return Snooze.All(space.Db).Any(e => e.Account == account.Id && e.Until <= now)
                || (Features.On(Features.MailExpiry) && MailExpiry.All(space.Db).Any(e => e.Account == account.Id && e.At <= now));
        }
        catch { return true; }
    }
    // [/feature:mailpush]

    /// <summary>Fehler, der nur einen Ordner oder eine Mail betrifft - nicht Abbruch oder Verbindung.</summary>
    private static bool Skippable(ImapClient c, Exception ex) =>
        ex is not OperationCanceledException && !ImapPool.IsConnectionProblem(ex) && c.IsConnected;

    private static int Order(string? special) => special switch
    {
        "inbox" => 0, "sent" => 1, "drafts" => 2, null => 3, "archive" => 4, "junk" => 5, "trash" => 6, _ => 7
    };

    // ---------------- Ordner ----------------

    internal static async Task RefreshFolders(ImapClient c, UserSpace space, MailAccount account, CancellationToken ct)
    {
        var all = new List<IMailFolder>();
        foreach (var ns in c.PersonalNamespaces)
            all.AddRange(await c.GetFoldersAsync(ns, false, ct));
        if (!all.Any(f => f.FullName.Equals("INBOX", StringComparison.OrdinalIgnoreCase))) all.Insert(0, c.Inbox);

        var existing = all.Where(f => f.Exists).ToList();
        var specials = AssignSpecials(existing.Select(f => (f.FullName, f.Name, f.ParentFolder?.FullName, f.Attributes)).ToList(), account.FolderMap);
        var rows = existing
            .Select((f, i) => (f.FullName, f.Name, f.DirectorySeparator.ToString(), specials[i],
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

    /// <summary>
    /// Sonderordner fuer alle Ordner eines Kontos: Kennzeichnet der Server einen (SPECIAL-USE, z. B. Gmail "[Gmail]/Gesendet"),
    /// gilt nur dieser - gleichnamige Ordner ("Gesendete Elemente", von einem anderen Programm angelegt) bleiben normale
    /// Ordner mit ihrem Namen. Ohne Kennzeichnung entscheidet der Name, je Art nur einer (der erste Name der Liste gewinnt).
    /// </summary>
    /// <param name="map">Im Konto selbst festgelegte Zuordnung (MailAccount.FolderMap) - geht der Erkennung vor</param>
    internal static List<string?> AssignSpecials(IReadOnlyList<(string FullName, string Name, string? Parent, FolderAttributes Attributes)> folders,
        IReadOnlyDictionary<string, string>? map = null)
    {
        string? ByAttribute(FolderAttributes a) => Specials.FirstOrDefault(s => s.Attr != FolderAttributes.None && a.HasFlag(s.Attr)).Kind;
        (string Kind, int Rank)? ByName(string name, string? parent)
        {
            if (!string.IsNullOrEmpty(parent) && !parent.Equals("INBOX", StringComparison.OrdinalIgnoreCase)) return null;
            var n = name.ToLowerInvariant();
            foreach (var (kind, _, names) in Specials)
                if (Array.IndexOf(names, n) is var r and >= 0) return (kind, r);
            return null;
        }
        var marked = folders.Select(f => ByAttribute(f.Attributes)).Where(k => k != null).ToHashSet();
        var result = new List<string?>();
        var taken = new HashSet<string>();
        // Bester Namenstreffer je Art (ohne Kennzeichnung): kleinster Rang, bei Gleichstand der erste
        var best = folders.Select((f, i) => (i, hit: ByName(f.Name, f.Parent)))
            .Where(x => x.hit != null && !marked.Contains(x.hit.Value.Kind))
            .GroupBy(x => x.hit!.Value.Kind)
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.hit!.Value.Rank).ThenBy(x => x.i).First().i);
        for (int i = 0; i < folders.Count; i++)
        {
            var f = folders[i];
            if (f.FullName.Equals("INBOX", StringComparison.OrdinalIgnoreCase)) { result.Add("inbox"); continue; }
            if (ByAttribute(f.Attributes) is { } k) { result.Add(k); continue; }
            result.Add(ByName(f.Name, f.Parent) is { } h && best.TryGetValue(h.Kind, out var bi) && bi == i ? h.Kind : null);
        }
        // Selbst festgelegt: dieser Ordner (leer: keiner) statt des erkannten - der Posteingang bleibt Posteingang
        foreach (var (kind, path) in map ?? new Dictionary<string, string>())
        {
            if (!MailAccount.MappableFolders.Contains(kind)) continue;
            for (int i = 0; i < result.Count; i++) if (result[i] == kind) result[i] = null;
            var at = folders.ToList().FindIndex(f => f.FullName == path);
            if (path.Length > 0 && at >= 0 && result[at] != "inbox") result[at] = kind;
        }
        return result;
    }

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
        // [feature:junkrecheck]
        // Neues im Junk-Ordner, das der Mailanbieter dort abgelegt hat: vor dem Eintragen nach den eigenen Regeln nachprüfen,
        // damit zurückgeholte Mails dort nie erscheinen und die übrigen gleich als gelesen ankommen
        JunkChecked? junkChecked = null;
        if (row.Special == "junk" && !wiped && fresh.Count > 0 && JunkRecheckOn(space)
            && DateTime.TryParse(db.Get(RulesSinceKey(account)), null, System.Globalization.DateTimeStyles.RoundtripKind, out var junkSince))
        {
            try
            {
                junkChecked = await RecheckJunk(c, space, account, f, state, fresh, junkSince, ct);
                if (junkChecked.Back.Count > 0)
                {
                    fresh = fresh.Where(u => !junkChecked.Back.Contains(u.Id)).ToList();
                    server.RemoveAll(u => junkChecked.Back.Contains(u.Id));
                }
            }
            catch (Exception e) when (e is not OperationCanceledException and not IOException and not ImapProtocolException)
            { _log.LogWarning("Junk nachprüfen {Account}: {Error}", LogNames.Of(space, account), e.Message); }
        }
        // [/feature:junkrecheck]
        // Postfach der Catchall-Aliase: die Kopfzeilen mit dem eigentlichen Empfänger mitholen (CatchallAliases)
        var aliases = CatchallAliases.Load(db);
        var aliasOn = CatchallAliases.ActiveFor(aliases, account.Id);
        const MessageSummaryItems items = MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope |
            MessageSummaryItems.Flags | MessageSummaryItems.InternalDate | MessageSummaryItems.Size |
            MessageSummaryItems.BodyStructure | MessageSummaryItems.References;
        // [feature:mailprefetch]
        // Neue Mails samt Inhalt eintragen, bevor Liste, Zähler und Benachrichtigung sie melden - nicht beim ersten Abgleich
        // eines Ordners und nicht nach neuen UIDs (dann sind es viele alte Mails, deren Kopfzeilen zuerst kommen sollen)
        var prefetch = PrefetchOn && !wiped && row.Synced != null && fresh.Count <= PrefetchMaxCount;
        var prefetchUntil = DateTime.UtcNow + PrefetchBudget;
        Func<uint, (byte[] Raw, MimeMessage Message)?>? prefetchKnown = null;
        // [feature:junkrecheck]
        // In der Nachprüfung schon geholt: nicht ein zweites Mal vom Anbieter
        prefetchKnown = uid => junkChecked?.Stay.TryGetValue(uid, out var k) == true ? (k.Raw, k.Message) : null;
        // [/feature:junkrecheck]
        // [/feature:mailprefetch]
        foreach (var batch in fresh.Chunk(250))
        {
            ct.ThrowIfCancellationRequested();
            var summaries = aliasOn
                ? await f.FetchAsync(batch, new FetchRequest(items, CatchallAliases.FetchHeaders), ct)
                : await f.FetchAsync(batch, items, ct);
            // [feature:mailprefetch]
            if (prefetch)
            {
                await InsertPrefetched(f, space, account, row.Path, summaries, prefetchKnown, prefetchUntil, _log, ct);
                if (aliasOn) CatchallAliases.Record(db, aliases, summaries, account.Id);
                continue;
            }
            // [/feature:mailprefetch]
            MailStore.Insert(db, account.Id, row.Path, summaries);
            if (aliasOn) CatchallAliases.Record(db, aliases, summaries, account.Id);
            // Gerade von hier gesendet: den Inhalt hat der Server schon - gleich ablegen statt spaeter vom Anbieter zu holen
            foreach (var s in summaries)
                if (SentCache.Take(user, account.Id, row.Path, s.Envelope?.MessageId) is { } raw)
                    MailStore.StoreRaw(db, account.Id, row.Path, s.UniqueId.Id, raw, MimeMessage.Load(new MemoryStream(raw)));
        }

        // [feature:junkrecheck]
        if (junkChecked != null)
            foreach (var (uid, kept) in junkChecked.Stay)
            {
                MailStore.StoreRaw(db, account.Id, row.Path, uid, kept.Raw, kept.Message);
                if (kept.Score is double sc) MailStore.SetSpamScore(db, account.Id, row.Path, uid, sc);
            }
        // [/feature:junkrecheck]

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
            var graph = GraphCategories.Uses(account);
            await MailStore.ApplyFlagsAsync(db, account.Id, row.Path, changed
                // Fahne/gelesen gerade unterwegs zum Server: den alten Stand nicht zurueckschreiben
                .Where(s => s.Flags is not null && !MailPending.Flagging(user, account.Id, row.Path, s.UniqueId.Id))
                // Microsoft mit Graph: Kategorien kommen von dort (GraphCategories), IMAP kennt sie nicht
                .Select(s => (s.UniqueId.Id, s.Flags!.Value, graph ? null : (IEnumerable<string>?)s.Keywords)), ct);
        }

        // Zähler des Ordners und Meldung an Oberfläche und Handy (Version)
        async Task Announce()
        {
            var unread = (await f.SearchAsync(SearchQuery.NotSeen.And(SearchQuery.NotDeleted), ct)).Count;
            var localUnread = Convert.ToInt32(db.Scalar("SELECT COUNT(*) FROM mail WHERE account=$a AND folder=$f AND seen=0", ("$a", account.Id), ("$f", row.Path)));
            db.Set($"mail.{account.Id}.outside.{row.Path}", Math.Max(0, unread - localUnread).ToString());
            // Gesamtzahl ohne als geloescht markierte (die zeigt die Liste auch nicht) - sonst bleibt z. B. "1" an einem leeren Ordner stehen
            // Zahl wie die Liste: nur was im Synchronisierungszeitraum liegt (sonst "5" bei leerem Ordner)
            var total = server.Count;
            MailStore.SetFolderState(db, account.Id, row.Path, f.UidValidity, (long)f.HighestModSeq, unread, total);

            if (fresh.Count > 0 || removed.Count > 0 || known.Count > 0) Touch(space.User.Id);
        }

        // Neues im Posteingang: Spamfilter und Regeln
        async Task RulesForNew()
        {
            if (row.Special != "inbox" || !DateTime.TryParse(db.Get(RulesSinceKey(account)), null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var since)) return;
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

        // [feature:mailprefetch]
        // Erst Spamfilter und Regeln, dann melden: Zähler und Liste zeigen neue Mails dort, wo sie bleiben, und mit Inhalt.
        // Scheitert die Bearbeitung (Verbindung), wenigstens die Oberfläche auffrischen - die Mails sind ja eingetragen.
        if (PrefetchOn)
        {
            try { await RulesForNew(); }
            catch { if (fresh.Count > 0) Touch(space.User.Id); throw; }
            await Announce();
            return;
        }
        // [/feature:mailprefetch]
        await Announce();
        await RulesForNew();
    }

    // [feature:mailprefetch]
    // ---------------- Vorladen neuer Mails ----------------

    private static bool PrefetchOn => Features.On(Features.MailPrefetch);

    /// <summary>Höchstens so viele neue Mails je Ordner und Durchgang mit Inhalt vorab - mehr sind ein Nachholen (lange offline,
    /// Ordner verschoben), dann zuerst die Kopfzeilen wie bisher.</summary>
    internal const int PrefetchMaxCount = 50;

    /// <summary>So lange je Ordner und Durchgang höchstens vorladen; der Rest kommt wie bisher danach.</summary>
    internal static readonly TimeSpan PrefetchBudget = TimeSpan.FromSeconds(20);

    /// <summary>Größere Mails (meist große Anhänge) werden nicht abgewartet: gemeldet sofort, Inhalt wie bisher gleich danach
    /// im Hintergrund oder beim Öffnen.</summary>
    internal const long PrefetchMaxBytes = 10L * 1024 * 1024;

    /// <summary>Grenze für eine Mail: 10 MB, höchstens die Grenze des Kontos; bei „Schlank“ höchstens, was dort vorab geladen wird.</summary>
    internal static long PrefetchLimit(MailAccount account, DateTimeOffset now)
    {
        var limit = Math.Min(PrefetchMaxBytes, account.MaxCachedMb * 1024L * 1024L);
        return MailStore.SlimLimit.Of(account, now) is { } slim ? Math.Min(limit, slim.Big) : limit;
    }

    /// <summary>
    /// Neue Mails einzeln eintragen, jede gleich mit Inhalt (vom Anbieter, aus <paramref name="known"/> oder - gerade gesendet - aus
    /// <see cref="SentCache"/>). So steht keine neue Mail ohne Inhalt in der Liste; Öffnen geht ohne Abruf beim Anbieter. Im Speicher
    /// liegt immer nur eine Mail. Zu große, nach <paramref name="until"/> oder bei einem Fehler: nur die Kopfzeilen, der Inhalt kommt
    /// wie bisher danach. Gibt die Zahl der mit Inhalt eingetragenen zurück.
    /// </summary>
    internal static async Task<int> InsertPrefetched(IMailFolder f, UserSpace space, MailAccount account, string folder,
        IEnumerable<IMessageSummary> summaries, Func<uint, (byte[] Raw, MimeMessage Message)?>? known, DateTime until,
        ILogger? log, CancellationToken ct)
    {
        var db = space.Db;
        var max = PrefetchLimit(account, DateTimeOffset.UtcNow);
        int loaded = 0;
        foreach (var s in summaries)
        {
            ct.ThrowIfCancellationRequested();
            byte[]? raw = SentCache.Take(space.User.Id, account.Id, folder, s.Envelope?.MessageId);
            MimeMessage? message = raw == null ? null : MimeMessage.Load(new MemoryStream(raw));
            if (raw == null && known?.Invoke(s.UniqueId.Id) is { } k) (raw, message) = k;
            if (raw == null && s.Size is { } size && size <= max && DateTime.UtcNow < until)
            {
                try
                {
                    message = await f.GetMessageAsync(s.UniqueId, ct);
                    using var ms = new MemoryStream();
                    await message.WriteToAsync(ms, ct);
                    raw = ms.Length <= max ? ms.ToArray() : null;
                }
                // Gerade wieder weg (verschoben, gelöscht): nicht eintragen
                catch (MessageNotFoundException) { continue; }
                // Eine nicht lesbare Mail hält die anderen nicht auf - ihr Inhalt kommt später wie bisher
                catch (Exception ex) when (ex is not OperationCanceledException && !ImapPool.IsConnectionProblem(ex))
                {
                    log?.LogWarning("Mail-Sync {Account}: Inhalt {Uid} nicht vorab geladen: {Error}", LogNames.Of(space, account), s.UniqueId.Id, ex.Message);
                }
            }
            MailStore.Insert(db, account.Id, folder, [s]);
            if (raw != null && message != null)
            {
                MailStore.StoreRaw(db, account.Id, folder, s.UniqueId.Id, raw, message);
                loaded++;
            }
        }
        return loaded;
    }
    // [/feature:mailprefetch]

    // ---------------- Spamfilter und Regeln ----------------

    /// <summary>Wie lange bearbeitete Mails gemerkt werden. Aeltere Mails (nach Eingangsdatum)
    /// gelten ohnehin als bearbeitet - mit Abstand, damit nichts dazwischen faellt.</summary>
    private static readonly TimeSpan ProcessedKeep = TimeSpan.FromDays(90), ProcessedFloor = TimeSpan.FromDays(60);

    /// <summary>So oft wird eine Mail versucht, bei der Spamfilter oder Regeln scheitern (z. B. MOVE wegen Quota).</summary>
    private const int MaxRuleTries = 10;

    /// <summary>Seit wann bearbeitete Mails gemerkt werden. Vorher Liegendes im Posteingang ist schon
    /// bearbeitet (alte Version ohne Liste) und laeuft nicht noch einmal.</summary>
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
        if (rules.Count == 0 && !spamActive && !settings.Spam.Enabled && !learnKnown && settings.Spam.Lists.Count == 0 && !settings.FocusOn(account) && !settings.CategoriesOn(account)
            && !CatchallAliases.ActiveFor(db, account))
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
                    _log.LogWarning("Regeln/Spamfilter {Account}: Mail {Uid} nach {Tries} Versuchen aufgegeben: {Error}", LogNames.Of(space, account), uid.Id, tries, ex.Message);
                }
                else
                {
                    pending = true;
                    _log.LogWarning("Regeln/Spamfilter {Account}: Mail {Uid} fehlgeschlagen (Versuch {Tries}): {Error}", LogNames.Of(space, account), uid.Id, tries, ex.Message);
                }
            }
        }
        await inbox.OpenAsync(FolderAccess.ReadOnly, ct);
        return pending;
    }

    // [feature:junkrecheck]
    /// <summary>So alt darf eine neue Mail im Junk-Ordner höchstens sein, damit sie als „vom Mailanbieter abgelegt“ gilt. Ältere hat
    /// jemand von Hand dorthin verschoben (beim Verschieben bleibt das Eingangsdatum).</summary>
    private static readonly TimeSpan JunkRecheckAge = TimeSpan.FromDays(2);

    /// <summary>Nachprüfung: zurück in den Posteingang bei sicherem Absender oder Bewertung unter „unsicher“; ohne Bewertung bleibt sie in Junk.</summary>
    public static bool BackFromJunk(bool trusted, double? score, double hamCutoff) => trusted || score < hamCutoff;

    /// <summary>
    /// Kennung junkrecheck: Neue Mails, die der Mailanbieter gleich in Junk gelegt hat, nach den eigenen Regeln prüfen. Sicherer Absender
    /// (Adressbuch, selbst angeschrieben, Liste sicherer Absender) oder Bewertung unter „unsicher“: zurück in den Posteingang, dort laufen
    /// Spamfilter und Regeln wie bei jeder neuen Mail. Sonst bleibt sie in Junk (auf Wunsch als gelesen). Nicht nachgeprüft wird, was
    /// schon im Posteingang bearbeitet war (dmOffice oder jemand hat es nach Junk verschoben) oder älter ist.
    /// </summary>
    private sealed record JunkChecked(HashSet<uint> Back, Dictionary<uint, (byte[] Raw, MimeMessage Message, double? Score)> Stay);

    private static bool JunkRecheckOn(UserSpace space) => Features.On(Features.JunkRecheck) && space.Settings.Spam.Enabled;

    private async Task<JunkChecked> RecheckJunk(ImapClient c, UserSpace space, MailAccount account, IMailFolder junk, State state,
        List<UniqueId> fresh, DateTime since, CancellationToken ct)
    {
        var db = space.Db;
        var spam = space.Settings.Spam;
        var result = new JunkChecked(new HashSet<uint>(), new Dictionary<uint, (byte[], MimeMessage, double?)>());
        var floor = new[] { since, DateTime.UtcNow - JunkRecheckAge }.Max();
        // Nur die jüngsten: Ältere hat jemand von Hand hierher verschoben
        var heads = await junk.FetchAsync(fresh, MessageSummaryItems.UniqueId | MessageSummaryItems.InternalDate | MessageSummaryItems.Envelope, ct);
        var done = MailStore.Processed(db, account.Id);
        var open = heads.Where(h => (h.InternalDate ?? DateTimeOffset.MinValue).UtcDateTime >= floor)
            // Ohne Message-ID lässt sich nicht erkennen, ob sie schon im Posteingang war: Urteil des Anbieters gilt
            .Select(h => (h.UniqueId, Key: h.Envelope?.MessageId?.Trim().Trim('<', '>').Trim()))
            .Where(h => !string.IsNullOrEmpty(h.Key) && !done.ContainsKey("mid:" + h.Key))
            .ToList();
        if (open.Count == 0) return result;
        var (nspam, nham) = SpamFilter.Counts(db);
        bool spamActive = nspam >= spam.MinTrained && nham >= spam.MinTrained;
        bool markRead = Features.On(Features.JunkRead) && spam.JunkMarkRead;

        await junk.OpenAsync(FolderAccess.ReadWrite, ct);
        var settled = new List<string>();
        IMailFolder? inbox = null;
        foreach (var (uid, mid) in open)
        {
            MimeMessage m;
            try { m = await junk.GetMessageAsync(uid, ct); }
            catch (MessageNotFoundException) { continue; }
            var key = "mid:" + mid;
            // Hat dmOffice selbst nach Junk verschoben (aus einem anderen Ordner oder auf einem anderen Gerät)
            if (JunkLearn.WasMoved(db, m)) { settled.Add(key); continue; }
            var signals = SpamSignals.Of(m, [account.ImapHost, PhishCheck.DomainOf(account.Email)]);
            // Zurückholen nur bei bestätigter Absenderdomain: eine bloß bekannte Adresse kann gefälscht sein
            bool trusted = SpamSignals.Trusted(space, m, signals, verifiedOnly: true).Trusted;
            var score = SpamSignals.Judge(space, account, m, spamActive ? SpamFilter.Score(db, m) : null, verifiedOnly: true).Score;
            if (BackFromJunk(trusted, score, spam.HamCutoff))
            {
                // Zurück, ungelesen: nicht als bearbeitet merken, im Posteingang läuft sie wie jede neue Mail durch Spamfilter und Regeln
                inbox ??= await Special(c, space, account, "inbox", ct);
                if (inbox == null) break;
                await junk.MoveToAsync(uid, inbox, ct);
                result.Back.Add(uid.Id);
                _log.LogInformation("Junk nachprüfen {Account}: Mail zurück in den Posteingang ({Why})", LogNames.Of(space, account), trusted ? "sicherer Absender" : "Bewertung");
            }
            else
            {
                // Bleibt: gleich als gelesen ablegen, bevor sie in der Liste erscheint
                if (markRead) await ReadIfWanted(junk, uid, spam, ct);
                using var ms = new MemoryStream();
                await m.WriteToAsync(ms, ct);
                if (ms.Length <= account.MaxCachedMb * 1024L * 1024L) result.Stay[uid.Id] = (ms.ToArray(), m, score);
                settled.Add(key);
            }
        }
        if (settled.Count > 0) MailStore.SetProcessed(db, account.Id, settled.Distinct(), MailStore.StageDone);
        if (result.Back.Count > 0 && inbox != null) Kick(space.User.Id, account.Id, inbox.FullName);
        return result;
    }
    // [/feature:junkrecheck]

    /// <summary>Kennung junkread: Vor dem Verschieben nach Junk als gelesen markieren (wenn gewünscht).</summary>
    private static async Task ReadIfWanted(IMailFolder folder, UniqueId uid, SpamSettings spam, CancellationToken ct)
    {
        if (!Features.On(Features.JunkRead) || !spam.JunkMarkRead) return;
        try { await folder.AddFlagsAsync(uid, MessageFlags.Seen, true, ct); }
        catch (Exception e) when (e is not OperationCanceledException) { /* Zugabe: das Verschieben darf daran nicht scheitern */ }
    }

    private async Task ProcessOne(ImapClient c, UserSpace space, MailAccount account, IMailFolder inbox, UniqueId uid,
        string key, int stage, List<MailRule> rules, bool spamActive, CancellationToken ct)
    {
        var settings = space.Settings;
        var message = await Fetch(inbox, space, account, uid, ct);
        if (message == null) return;
        // Virenprüfung (wenn eingeschaltet): Mails mit Anhang einmal beim Eingang
        try { await MailVirus.CheckAsync(space, account, inbox.FullName, uid.Id, message, ct); }
        catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException) { _log.LogWarning("Virenscanner: {Error}", e.Message); }

        // Gesperrter Catchall-Alias (oder unbekannte bei „Junk“): gleich in Junk oder Papierkorb - vor allem anderen, auch vor
        // sicheren Absendern (die Adresse ist ja gerade deshalb gesperrt, weil sie weitergegeben wurde)
        if (CatchallAliases.Judge(space.Db, account, message) is { } alias)
        {
            if (message.MessageId is { } mid) CatchallAliases.Hit(space.Db, CatchallAliases.Load(space.Db), alias.Alias, mid,
                message.From.Mailboxes.FirstOrDefault()?.Address, message.From.Mailboxes.FirstOrDefault()?.Name, message.Date);
            if (await Special(c, space, account, alias.Action == "delete" ? "trash" : "junk", ct) is { } target)
            {
                if (alias.Action == "junk")
                {
                    MailStore.SetSpamScore(space.Db, account.Id, inbox.FullName, uid.Id, Math.Max(0.99, settings.Spam.SpamCutoff));
                    JunkLearn.MarkMoved(space.Db, message);
                }
                if (alias.Action == "junk") await ReadIfWanted(inbox, uid, settings.Spam, ct);
                await inbox.MoveToAsync(uid, target, ct);
                MailStore.Delete(space.Db, account.Id, inbox.FullName, new[] { uid.Id });
                lock (StateOf(space.User.Id, account.Id)) StateOf(space.User.Id, account.Id).KickFolders.Add(target.FullName);
                Touch(space.User.Id);
                return;
            }
        }

        // Sichere Absender (Adressbuch, selbst angeschrieben): nie Junk, auf Wunsch als gut lernen
        var spam = settings.Spam;
        // Nie mit nachweislich gefälschter Absenderadresse (DMARC gescheitert) - sonst käme Chef-Betrug mit der Adresse eines Kontakts
        // durch; dazu die selbst eingetragenen sicheren Absender, nur mit bestätigter Domain (SpamSignals.Trusted, SafeList)
        var signals = spam.Enabled ? SpamSignals.Of(message, [account.ImapHost, PhishCheck.DomainOf(account.Email)]) : null;
        bool known = signals is { DmarcFailed: false } && (spam.TrustKnown || spam.LearnKnown) && SafeSenders.IsKnown(space, message);
        bool trusted = signals != null && SpamSignals.Trusted(space, message, signals).Trusted;
        if (known && spam.LearnKnown) SpamFilter.Train(space.Db, message, spam: false);

        // Globale Spamlisten: Treffer gehen in die Bewertung ein, je Liste auf Wunsch gleich nach Junk
        if (spam.Enabled && spam.Lists.Count > 0 && !trusted)
        {
            List<string> hits = [];
            try { hits = await Blocklists.CheckAsync(space.Db, message, spam.Lists, _config?.SpamhausKey(), ct); }
            catch (Exception e) when (e is not OperationCanceledException) { _log.LogDebug(e, "Spamlisten nicht erreichbar"); }
            if (hits.Any(spam.ListsJunk.Contains) && await Special(c, space, account, "junk", ct) is { } listJunk)
            {
                JunkLearn.MarkMoved(space.Db, message);
                await ReadIfWanted(inbox, uid, spam, ct);
                await inbox.MoveToAsync(uid, listJunk, ct);
                MailStore.Delete(space.Db, account.Id, inbox.FullName, new[] { uid.Id });
                lock (StateOf(space.User.Id, account.Id)) StateOf(space.User.Id, account.Id).KickFolders.Add(listJunk.FullName);
                return;
            }
        }

        // Bewertet wird erst, wenn genug gelernt ist - vorher kaeme fuer jede Mail 50 % heraus; feste Regeln (Echtheit, Markenimitation)
        // greifen immer (SpamSignals)
        var judged = spam.Enabled ? SpamSignals.Judge(space, account, message, spamActive ? SpamFilter.Score(space.Db, message) : null).Score : null;
        if (judged is double score)
        {
            MailStore.SetSpamScore(space.Db, account.Id, inbox.FullName, uid.Id, score);
            if (score >= settings.Spam.SpamCutoff && !trusted && await Special(c, space, account, "junk", ct) is { } junk)
            {
                // Selbst verschoben: daraus nicht wieder lernen (JunkLearn)
                JunkLearn.MarkMoved(space.Db, message);
                await ReadIfWanted(inbox, uid, spam, ct);
                await inbox.MoveToAsync(uid, junk, ct);
                MailStore.Delete(space.Db, account.Id, inbox.FullName, new[] { uid.Id });
                lock (StateOf(space.User.Id, account.Id)) StateOf(space.User.Id, account.Id).KickFolders.Add(junk.FullName);
                return;
            }
        }

        // Prio-Posteingang: Relevant oder Sonstige (nach Inhalt, Absender, Gelerntem)
        if (settings.FocusOn(account))
            MailStore.SetFocus(space.Db, account.Id, inbox.FullName, new[] { uid.Id }, Focus.Classify(space, message));
        // Lernende Kategorien: Etikett (IMAP-Schlagwort) vor den Regeln - es bleibt, wenn eine Regel die Mail verschiebt
        if (settings.CategoriesOn(account) && GraphCategories.Uses(account) && SmartInbox.Decide(space, SmartInbox.Of(message, space.Zone)) is { Count: > 0 } outlook)
        {
            foreach (var l in outlook)
                try { await GraphCategories.SetAsync(space, account, inbox.FullName, [uid.Id], l, true, ct); }
                catch (Exception e) when (e is Pim.GraphException or HttpRequestException or MailKit.Security.AuthenticationException)
                {
                    // Microsoft nicht erreichbar: lokal trotzdem einordnen
                    MailStore.SetKeyword(space.Db, account.Id, inbox.FullName, [uid.Id], l, true);
                }
        }
        else if (settings.CategoriesOn(account) && SmartInbox.Decide(space, SmartInbox.Of(message, space.Zone)) is { Count: > 0 } labels
            && (inbox.PermanentFlags.HasFlag(MessageFlags.UserDefined) || labels.All(inbox.PermanentKeywords.Contains)))
        {
            var req = new StoreFlagsRequest(StoreAction.Add, MessageFlags.None) { Silent = true };
            foreach (var l in labels) req.Keywords.Add(l);
            await inbox.StoreAsync(new[] { uid }, req, ct);
            foreach (var l in labels) MailStore.SetKeyword(space.Db, account.Id, inbox.FullName, new[] { uid.Id }, l, true);
        }

        if (rules.Count > 0)
        {
            var outcome = Rules.Evaluate(rules, account.Id, message);
            if (outcome.Applied.Count > 0)
            {
                await Apply(c, space, account, inbox, uid, key, stage, message, outcome, ct);
                // Eine Regel hat die Mail woandershin gelegt: dabei bleibt es
                if (outcome.Delete || outcome.MoveTo != null && Local(account, outcome.MoveTo) != inbox.FullName) return;
            }
        }

        // Catchall-Aliase: in den Ordner des Alias, Unbekannte in den Ordner der Domain (CatchallFolders)
        var aliases = CatchallAliases.Load(space.Db);
        // [feature:catchalldomains]
        // „Auch im Posteingang anzeigen“: die Mail bleibt hier, „Catchall-Aliase“ zeigt sie über mail_alias_hit (ca:-Ansicht).
        // Gilt nur für neu eintreffende Mails - vorhandene werden weder verschoben noch zurückgeholt.
        if (CatchallAliases.ActiveFor(aliases, account.Id) && CatchallAliases.Of(aliases, message, account.Id) is { } here
            && CatchallAliases.SiteFor(aliases, here) is { Inbox: true })
        {
            if (message.MessageId is { } hid) CatchallAliases.Hit(space.Db, aliases, here, hid,
                message.From.Mailboxes.FirstOrDefault()?.Address, message.From.Mailboxes.FirstOrDefault()?.Name, message.Date);
            if (CatchallAliases.Get(space.Db, here) is { } hereAlias
                && CatchallAliases.ExpiryFor(hereAlias, account.Id, inbox.FullName, uid.Id, message, DateTime.UtcNow) is { } stay)
                await MailExpiry.ScheduleAsync(space.Db, [stay]);
            return;
        }
        // [/feature:catchalldomains]
        if (CatchallAliases.ActiveFor(aliases, account.Id) && CatchallAliases.Of(aliases, message, account.Id) is { } to
            && await CatchallFolders.TargetAsync(c, space.Db, to, ct) is { } aliasFolder && aliasFolder.FullName != inbox.FullName)
        {
            if (!MailStore.Folders(space.Db, account.Id).Any(f => f.Path == aliasFolder.FullName)) await RefreshFolders(c, space, account, ct);
            var moved = await inbox.MoveToAsync(uid, aliasFolder, ct);
            MailStore.Delete(space.Db, account.Id, inbox.FullName, new[] { uid.Id });
            // Entfernen nach Zeit, wie am Alias eingestellt (gefunden wird die Mail dann über die Message-ID)
            if (CatchallAliases.Get(space.Db, to) is { } toAlias
                && CatchallAliases.ExpiryFor(toAlias, account.Id, aliasFolder.FullName, moved?.Id ?? 0, message, DateTime.UtcNow) is { } expiry)
                await MailExpiry.ScheduleAsync(space.Db, [expiry]);
            lock (StateOf(space.User.Id, account.Id)) StateOf(space.User.Id, account.Id).KickFolders.Add(aliasFolder.FullName);
            Touch(space.User.Id);
        }
    }

    private async Task Apply(ImapClient c, UserSpace space, MailAccount account, IMailFolder inbox, UniqueId uid,
        string key, int stage, MimeMessage message, RuleOutcome o, CancellationToken ct)
    {
        // Weiterleiten und Kopieren nur einmal - auch wenn danach etwas scheitert und die Mail wiederholt wird
        if (stage < MailStore.StageForwarded && (o.ForwardTo.Count > 0 || o.RedirectTo.Count > 0))
        {
            // Kennung ruleforward: nur an bestätigte Ziele (eigene Adressen und Altbestand gelten), sonst nichts senden
            var settings = space.Settings;
            foreach (var (to, redirect) in o.ForwardTo.Select(t => (t, false)).Concat(o.RedirectTo.Select(t => (t, true))))
            {
                if (!ForwardTargets.Allowed(settings, to))
                {
                    _log.LogInformation("Regel: {To} wartet auf Bestätigung - nicht {What}", to, redirect ? "umgeleitet" : "weitergeleitet");
                    continue;
                }
                try
                {
                    using var smtp = await ImapPool.SmtpAsync(account, ct);
                    if (redirect) ForwardTargets.Note8Bit(space, account.Id, smtp.Capabilities.Contains(MailKit.Net.Smtp.SmtpCapability.EightBitMime));
                    if (redirect)
                        await smtp.SendAsync(MailCompose.Redirect(account, message, to), new MailboxAddress("", account.Email), [MailboxAddress.Parse(to.Trim())], ct);
                    else await smtp.SendAsync(MailCompose.Forward(account, message, to), ct);
                    await smtp.DisconnectAsync(true, ct);
                    await inbox.StoreAsync(new[] { uid }, new StoreFlagsRequest(StoreAction.Add, MessageFlags.None) { Silent = true, Keywords = { "$Forwarded" } }, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (redirect) _log.LogWarning("Regel: Umleiten an {To} fehlgeschlagen: {Error}", to, ex.Message);
                    else _log.LogWarning("Regel: Weiterleiten an {To} fehlgeschlagen: {Error}", to, ex.Message);
                }
            }
            if (key.Length > 0) MailStore.SetProcessed(space.Db, account.Id, new[] { key }, MailStore.StageForwarded);
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
                if (GraphCategories.Uses(account))
                    foreach (var k in o.Categories) await GraphCategories.SetAsync(space, account, inbox.FullName, [uid.Id], k, true, ct);
                else
                {
                    var req = new StoreFlagsRequest(StoreAction.Add, MessageFlags.None) { Silent = true };
                    foreach (var k in o.Categories) req.Keywords.Add(k);
                    await inbox.StoreAsync(new[] { uid }, req, ct);
                    foreach (var k in o.Categories) MailStore.SetKeyword(space.Db, account.Id, inbox.FullName, new[] { uid.Id }, k, true);
                }
            }
            catch (Exception ex) when (Skippable(c, ex) || ex is Pim.GraphException or InvalidOperationException) { _log.LogWarning("Regel: Kategorie setzen: {Error}", ex.Message); }
        }
        if (stage < MailStore.StageCopied && o.CopyTo.Count > 0)
        {
            foreach (var target in o.CopyTo)
            {
                // Kennung ruleforward: Ordner eines anderen eigenen Kontos („Konto|Ordner“) - dorthin per APPEND
                var read = false;
                // [feature:rulecopyread]
                read = o.CopyRead.Contains(target);
                // [/feature:rulecopyread]
                if (OtherAccount(space, account, target) is { } other)
                {
                    await AppendTo(space, account, inbox, uid, message, other.Account, other.Folder, ct, read);
                    continue;
                }
                // Kopie als gelesen: COPY übernimmt die Flags des Originals - kurz \Seen setzen und danach zurücknehmen,
                // wenn das Original vorher ungelesen war (und keine Regel es als gelesen markiert)
                var unseen = read && !o.MarkRead && !(await inbox.FetchAsync(new[] { uid }, MessageSummaryItems.Flags, ct))
                    .Any(x => x.Flags?.HasFlag(MessageFlags.Seen) == true);
                if (unseen) await inbox.StoreAsync(new[] { uid }, new StoreFlagsRequest(StoreAction.Add, MessageFlags.Seen) { Silent = true }, ct);
                try { await inbox.CopyToAsync(uid, await c.GetFolderAsync(Local(account, target), ct), ct); Kick(space.User.Id, account.Id, Local(account, target)); }
                catch (FolderNotFoundException) { }
                finally { if (unseen) await inbox.StoreAsync(new[] { uid }, new StoreFlagsRequest(StoreAction.Remove, MessageFlags.Seen) { Silent = true }, ct); }
            }
            if (key.Length > 0) MailStore.SetProcessed(space.Db, account.Id, new[] { key }, MailStore.StageCopied);
        }

        // Verschieben in ein anderes eigenes Konto: erst dort ablegen, dann hier entfernen - scheitert das Ablegen, bleibt die Mail
        if (!o.Delete && o.MoveTo != null && OtherAccount(space, account, o.MoveTo) is { } dest)
        {
            if (await AppendTo(space, account, inbox, uid, message, dest.Account, dest.Folder, ct))
            {
                if (c.Capabilities.Contains(MailKit.Net.Imap.ImapCapability.UidPlus))
                {
                    await inbox.StoreAsync(new[] { uid }, new StoreFlagsRequest(StoreAction.Add, MessageFlags.Deleted) { Silent = true }, ct);
                    await inbox.ExpungeAsync(new[] { uid }, ct);
                }
                else if (await Special(c, space, account, "trash", ct) is { } trash) await inbox.MoveToAsync(uid, trash, ct);
                MailStore.Delete(space.Db, account.Id, inbox.FullName, new[] { uid.Id });
                Kick(space.User.Id, account.Id, inbox.FullName);
            }
            // Entfernen nach Zeit gilt für Mails im eigenen Konto; im anderen findet sie die Kennung nicht
            Touch(space.User.Id);
            return;
        }

        string? moveTo = o.Delete ? (await Special(c, space, account, "trash", ct))?.FullName : o.MoveTo is { } mt ? Local(account, mt) : null;
        var (where, whereUid) = (inbox.FullName, uid.Id);
        if (moveTo != null && moveTo != inbox.FullName)
        {
            try
            {
                var moved = await inbox.MoveToAsync(uid, await c.GetFolderAsync(moveTo, ct), ct);
                MailStore.Delete(space.Db, account.Id, inbox.FullName, new[] { uid.Id });
                lock (StateOf(space.User.Id, account.Id)) StateOf(space.User.Id, account.Id).KickFolders.Add(moveTo);
                (where, whereUid) = (moveTo, moved?.Id ?? 0);
            }
            catch (FolderNotFoundException) { _log.LogWarning("Regel: Zielordner gibt es nicht"); }
        }
        // In den Papierkorb / vernichten nach X Tagen: gezaehlt ab dem Eingang, gefunden spaeter ueber die Message-ID
        var expiries = new List<MailExpiry.Entry>();
        if (o.TrashAfterDays is int td && !o.Delete)
            expiries.Add(new() { Account = account.Id, Folder = where, Uid = whereUid, MessageId = message.MessageId, At = DateTime.UtcNow.AddDays(td), Kind = MailExpiry.Trash, Subject = message.Subject ?? "" });
        if (o.DestroyAfterDays is int dd)
            expiries.Add(new() { Account = account.Id, Folder = where, Uid = whereUid, MessageId = message.MessageId, At = DateTime.UtcNow.AddDays(dd), Kind = MailExpiry.Destroy, Subject = message.Subject ?? "" });
        if (expiries.Count > 0) await MailExpiry.ScheduleAsync(space.Db, expiries);
        Touch(space.User.Id);
    }

    /// <summary>„Konto|Ordner“ eines anderen eigenen Kontos (Kennung ruleforward)? Sonst null - auch beim eigenen Konto.</summary>
    internal static (MailAccount Account, string Folder)? OtherAccount(UserSpace space, MailAccount account, string target)
    {
        if (!Features.On(Features.RuleForward)) return null;
        var bar = target.IndexOf('|');
        if (bar <= 0) return null;
        var id = target[..bar];
        if (id == account.Id) return null;
        return space.Settings.MailAccounts.FirstOrDefault(a => a.Id == id) is { } other ? (other, target[(bar + 1)..]) : null;
    }

    /// <summary>Ordner im eigenen Konto: „Konto|Ordner“ mit der eigenen Kennung wird zum Ordner.</summary>
    internal static string Local(MailAccount account, string target) =>
        target.StartsWith(account.Id + "|", StringComparison.Ordinal) ? target[(account.Id.Length + 1)..] : target;

    /// <summary>Mail in einen Ordner eines anderen eigenen Kontos legen (IMAP APPEND). False: hat nicht geklappt (steht im Log).</summary>
    private async Task<bool> AppendTo(UserSpace space, MailAccount account, IMailFolder inbox, UniqueId uid, MimeMessage message,
        MailAccount other, string folder, CancellationToken ct, bool read = false)
    {
        try
        {
            // Ganze Mail (bei „Jetzt ausführen“ liegen sonst nur die Kopfzeilen vor)
            var full = await Fetch(inbox, space, account, uid, ct) ?? message;
            await _pool.UseAsync(other, "rules", async c2 =>
            {
                var f = await c2.GetFolderAsync(folder, ct);
                await f.AppendAsync(new AppendRequest(full, read ? MessageFlags.Seen : MessageFlags.None) { InternalDate = full.Date }, ct);
            }, ct);
            Kick(space.User.Id, other.Id, folder);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning("Regel: Ablegen in {Account} › {Folder} fehlgeschlagen: {Error}", LogNames.Of(space, other), folder, ex.Message);
            return false;
        }
    }

    public sealed record RuleRun(int Checked, int Matched);

    /// <summary>Höchstens so viele Mails beim Einordnen des ganzen Posteingangs (die neuesten).</summary>
    public const int ReclassifyMax = 1000;

    /// <summary>
    /// „Neu einordnen“ im Posteingang: Mails, die schon da waren (etwa vor dem Anlegen des Kontos in dmOffice), laufen durch dieselbe
    /// Einordnung wie neu eintreffende - Junk-Erkennung (auch das Verschieben nach Junk), Relevant/Sonstige und lernende Kategorien.
    /// Regeln nicht (dafür gibt es „Jetzt ausführen“ an der Regel). Ohne Auswahl die neuesten <see cref="ReclassifyMax"/>.
    /// </summary>
    public async Task<int> Reclassify(UserSpace space, MailAccount account, IReadOnlyCollection<uint>? uids, ImapPool pool, CancellationToken ct)
    {
        var spam = space.Settings.Spam;
        var (nspam, nham) = SpamFilter.Counts(space.Db);
        bool spamActive = spam.Enabled && nspam >= spam.MinTrained && nham >= spam.MinTrained;
        int done = 0;
        // Eigene Verbindung - Öffnen und Verschieben in der Oberfläche warten nicht auf das Einordnen
        await pool.UseAsync(account, "classify", async c =>
        {
            var inbox = c.Inbox;
            await inbox.OpenAsync(FolderAccess.ReadWrite, ct);
            var list = uids?.Select(u => new UniqueId(u)).ToList()
                ?? (await inbox.SearchAsync(SearchQuery.All, ct)).OrderByDescending(u => u.Id).Take(ReclassifyMax).ToList();
            using var job = Activity.Start(space.User.Id, Activity.MailClassify, account.Email, list.Count);
            foreach (var uid in list)
            {
                ct.ThrowIfCancellationRequested();
                try { await ProcessOne(c, space, account, inbox, uid, "", MailStore.StageForwarded, [], spamActive, ct); }
                catch (Exception ex) when (Skippable(c, ex)) { _log.LogWarning("Neu einordnen {Account}: Mail {Uid}: {Error}", LogNames.Of(space, account), uid.Id, ex.Message); }
                job.Progress(++done);
            }
            Kick(space.User.Id, account.Id, inbox.FullName);
            return 0;
        }, ct);
        return done;
    }

    /// <summary>
    /// Knopf "Jetzt ausfuehren": die Regel auf die Mails im Posteingang anwenden, die schon da sind (auch wenn sie
    /// ausgeschaltet ist). Weitergeleitet wird dabei nicht - das waere auf einen Schlag eine Flut alter Mails.
    /// Mit Bedingungen auf den Text hoechstens die neuesten 500, sonst die neuesten 5000 (nur Kopfzeilen).
    /// </summary>
    public async Task<RuleRun> RunRule(UserSpace space, MailRule rule, ImapPool pool, CancellationToken ct)
    {
        var once = new MailRule { Name = rule.Name, Enabled = true, Match = rule.Match, Conditions = rule.Conditions, Actions = rule.Actions };
        bool needsBody = rule.Conditions.Any(c => c.Field is "body" or "any");
        int seen = 0, hit = 0;
        foreach (var account in space.Settings.ActiveMailAccounts().Where(a => rule.AccountId == null || a.Id == rule.AccountId))
        {
            await pool.UseAsync(account, ImapPool.Ui, async c =>
            {
                var inbox = c.Inbox;
                await inbox.OpenAsync(FolderAccess.ReadWrite, ct);
                var uids = (await inbox.SearchAsync(SearchQuery.All, ct)).OrderByDescending(u => u.Id).Take(needsBody ? 500 : 5000).ToList();
                if (uids.Count == 0) return;
                var summaries = await inbox.FetchAsync(uids, MessageSummaryItems.UniqueId | MessageSummaryItems.Headers, ct);
                foreach (var s in summaries)
                {
                    seen++;
                    MimeMessage? m;
                    if (needsBody) m = await Fetch(inbox, space, account, s.UniqueId, ct);
                    else
                    {
                        using var ms = new MemoryStream();
                        s.Headers.WriteTo(ms);
                        ms.Write("\r\n"u8);
                        ms.Position = 0;
                        m = MimeMessage.Load(ms, ct);
                    }
                    if (m == null) continue;
                    var o = Rules.Evaluate(new[] { once }, account.Id, m);
                    if (o.Applied.Count == 0) continue;
                    o.ForwardTo.Clear();
                    o.RedirectTo.Clear();
                    hit++;
                    await Apply(c, space, account, inbox, s.UniqueId, "", MailStore.StageForwarded, m, o, ct);
                }
                Kick(space.User.Id, account.Id, inbox.FullName);
            }, ct);
        }
        return new RuleRun(seen, hit);
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
                    _log.LogWarning("Mail-Sync {Account}, Inhalte eines Ordners: {Error}", LogNames.Of(space, account), ex.Message);
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
                        _log.LogWarning("Mail-Sync {Account}: Vorschau nicht geladen: {Error}", LogNames.Of(space, account), ex.Message);
                        break;
                    }
                }
                bool firstBatch = true;
                foreach (var batch in missing.Chunk(20))
                {
                    if (DateTime.UtcNow > until) return;
                    ct.ThrowIfCancellationRequested();
                    // Luecke fuer andere Schreiber (Oberflaeche, DMS): beim Nachladen Zehntausender Inhalte schrieb der Sync
                    // sonst fast ohne Pause, und wer warten musste, lief nach 30 s in "database is locked"
                    if (!firstBatch) await Task.Delay(UserDb.Breather, ct);
                    firstBatch = false;
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
                            _log.LogWarning("Mail-Sync {Account}: Inhalt {Uid} nicht geladen: {Error}", LogNames.Of(space, account), uid, ex.Message);
                        }
                    }
                }
            }
        }
        finally
        {
            // Auch nach einem Fehler, sonst bleibt der alte Stand stehen
            try { state.PendingBodies = MailStore.MissingCount(space.Db, account.Id, limit, slim); }
            catch (Exception ex) { _log.LogWarning("Mail-Sync {Account}: fehlende Inhalte zaehlen: {Error}", LogNames.Of(space, account), ex.Message); }
            if (any) Touch(space.User.Id);
        }
    }
}
