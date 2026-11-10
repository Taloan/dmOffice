using System.Collections.Concurrent;
using System.Globalization;
using DmOffice.Core;
using DmOffice.Dav;

namespace DmOffice.Storage;

/// <summary>
/// Hintergrund der Datensicherung: einmal am Tag zur Uhrzeit, die jede Person selbst wählt, dazu „Jetzt sichern“. Immer nur
/// eine Person zur Zeit und nie gleichzeitig mit dem Core-Backup - der Server hat wenig Arbeitsspeicher. Scheitert die Sicherung
/// mehrmals nacheinander, sagt Pukki einmal Bescheid.
/// </summary>
public sealed class PlainBackupService(Spaces spaces, ServerConfig config, DataPaths paths, IServiceProvider services, ILogger<PlainBackupService> log) : BackgroundService
{
    private static readonly SemaphoreSlim Signal = new(0);
    private static readonly ConcurrentDictionary<string, bool> Wanted = new();
    private static readonly ConcurrentDictionary<string, PlainProgress> Running = new();
    /// <summary>Laufende Läufe zum Anhalten (Einstellungen → Datensicherung: „Anhalten“).</summary>
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> Stops = new();
    public const string Stopped = "Angehalten.";
    /// <summary>Nach so vielen Fehlschlägen nacheinander gibt Pukki Bescheid.</summary>
    public const int TellAfter = 3;

    public static void RunNow(string userId) { Wanted[userId] = true; if (Signal.CurrentCount == 0) Signal.Release(); }
    public static PlainProgress? Progress(string userId) => Running.GetValueOrDefault(userId);
    public static bool Queued(string userId) => Wanted.ContainsKey(userId);

    /// <summary>Laufenden oder wartenden Lauf anhalten; was schon übertragen ist, bleibt (der nächste Lauf macht weiter).</summary>
    public static bool Stop(string userId)
    {
        var queued = Wanted.TryRemove(userId, out _);
        if (Stops.TryGetValue(userId, out var cts)) { try { cts.Cancel(); } catch (ObjectDisposedException) { } return true; }
        return queued;
    }

    /// <summary>Die zuletzt fällige Uhrzeit (UTC) - heute, wenn schon vorbei, sonst gestern.</summary>
    public static DateTime LastSlot(string time, TimeZoneInfo zone, DateTime nowUtc)
    {
        var t = TimeOnly.TryParseExact(time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var v) ? v : new TimeOnly(3, 0);
        var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone);
        var slot = local.Date + t.ToTimeSpan();
        if (slot > local) slot = slot.AddDays(-1);
        // Zeitumstellung: eine Uhrzeit, die es an dem Tag nicht gibt, eine Stunde später
        if (zone.IsInvalidTime(slot)) slot = slot.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(slot, DateTimeKind.Unspecified), zone);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(60), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!config.Current.PlainBackupOff)
                    foreach (var space in spaces.All())
                    {
                        ct.ThrowIfCancellationRequested();
                        var s = Settings(space);
                        var wanted = Wanted.ContainsKey(space.User.Id);
                        if (!PlainBackup.Ready(s)) { Wanted.TryRemove(space.User.Id, out _); continue; }
                        var state = PlainBackup.State(space.Db);
                        var slot = LastSlot(s.Time, space.Zone, DateTime.UtcNow);
                        if (state.LastSlot == null && !wanted)
                        {
                            // Gerade eingeschaltet: erst zur nächsten Uhrzeit (oder „Jetzt sichern“)
                            state.LastSlot = slot;
                            PlainBackup.Save(space.Db, state);
                            continue;
                        }
                        if (!wanted && state.LastSlot >= slot) continue;
                        // Das Core-Backup zuerst fertig werden lassen
                        while (services.GetService<S3Backup>()?.Status.Running == true) await Task.Delay(TimeSpan.FromSeconds(30), ct);
                        await RunAsync(space, s, slot, ct);
                    }
                else Wanted.Clear();
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e) { log.LogWarning(e, "Datenexport: Durchgang fehlgeschlagen"); }
            try { await Signal.WaitAsync(TimeSpan.FromSeconds(60), ct); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task RunAsync(UserSpace space, PlainBackupSettings s, DateTime slot, CancellationToken ct)
    {
        var db = space.Db;
        var started = DateTime.UtcNow;
        Running[space.User.Id] = new PlainProgress(started, 0, 0, 0, null);
        Wanted.TryRemove(space.User.Id, out _);
        PlainBackup.Result result;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Stops[space.User.Id] = stop;
        try
        {
            using var d = Driver(config, space, s, services.GetService<DmOffice.Mail.MicrosoftOAuth>());
            // Erster Lauf: Sprache der Ordnernamen festlegen. Wer schon exportiert hat, behält die bisherigen (deutschen) Namen.
            if (s.Lang == null)
            {
                s.Lang = PlainBackup.State(db).LastRun != null || !DmOffice.Chat.Pukki.WantsEnglish(space) ? "de" : "en";
                var fresh = PlainBackup.Settings(db);
                fresh.Lang = s.Lang;
                PlainBackup.Save(db, fresh);
            }
            var items = PlainBackupAreas.Items(space, s, services);
            var tmp = Path.Combine(paths.Root, "tmp", "plainbackup", space.User.Id);
            result = await PlainBackup.RunAsync(db, s, d, items, tmp, () => PlainBackupReadMe.Text(space, s, started),
                p => Running[space.User.Id] = p, stop.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            // Von Hand angehalten: vermerken, aber nicht als Fehlschlag zählen
            var st = PlainBackup.State(db);
            st.LastRun = started;
            st.LastSlot = slot > (st.LastSlot ?? DateTime.MinValue) ? slot : st.LastSlot;
            st.LastError = Stopped;
            PlainBackup.Save(db, st);
            log.LogInformation("Datenexport {User}: angehalten", space.User.Login);
            return;
        }
        catch (Exception e) { result = new PlainBackup.Result(0, 0, 0, 0, 0, 0, 1, e.GetBaseException().Message); }
        finally { Running.TryRemove(space.User.Id, out _); Stops.TryRemove(new(space.User.Id, stop)); }

        var state = PlainBackup.State(db);
        state.LastRun = started;
        state.LastSlot = slot > (state.LastSlot ?? DateTime.MinValue) ? slot : state.LastSlot;
        state.Seconds = Math.Round((DateTime.UtcNow - started).TotalSeconds, 1);
        state.Files = result.Files; state.Bytes = result.Bytes; state.Written = result.Written; state.Moved = result.Moved;
        state.Trashed = result.Trashed; state.Purged = result.Purged; state.Failed = result.Failed;
        state.LastError = result.Error;
        if (result.Error == null) { state.LastOk = started; state.Fails = 0; state.Told = null; }
        else state.Fails++;
        PlainBackup.Save(db, state);
        if (result.Error != null) log.LogWarning("Datenexport {User}: {Error}", space.User.Login, result.Error);
        else log.LogInformation("Datenexport {User}: {Files} Dateien, {Written} übertragen, {Moved} verschoben, {Trashed} nach .trash",
            space.User.Login, result.Files, result.Written, result.Moved, result.Trashed);
        if (state.Fails >= TellAfter && state.Told == null) Tell(space, state);
    }

    /// <summary>Pukki sagt einmal Bescheid, bis die Sicherung wieder klappt.</summary>
    private void Tell(UserSpace space, PlainBackupState state)
    {
        try
        {
            if (services.GetService<DmOffice.Chat.ChatStore>() is not { } chat) return;
            var room = DmOffice.Chat.Pukki.RoomOf(chat, space.User.Id) ?? chat.OpenDirect(space.User.Id, DmOffice.Chat.Pukki.Id);
            chat.Post(DmOffice.Chat.Pukki.Id, room, $"Der Datenexport ist {state.Fails}-mal nacheinander fehlgeschlagen. Letzter Fehler: {state.LastError}\n" +
                "Ziel und Zugangsdaten stehen unter Einstellungen → Konto → Datenexport und Einstellungen → Dateien → Externe Ziele.");
            state.Told = DateTime.UtcNow;
            PlainBackup.Save(space.Db, state);
        }
        catch (Exception e) { log.LogWarning(e, "Datenexport: Hinweis über Pukki fehlgeschlagen"); }
    }

    /// <summary>Verbindung zu OneDrive: folgt Weiterleitungen (Herunterladen kommt von einer anderen Adresse, ohne Anmeldung).</summary>
    private static readonly HttpClient OneDriveHttp = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = TimeSpan.FromMinutes(30) };

    /// <summary>Treiber fürs Ziel: WebDAV mit denselben Grenzen wie eigene WebDAV-Ordner (Heimnetz nur, wenn die Verwaltung es
    /// erlaubt), ein eigener externer Ordner oder OneDrive.</summary>
    public static IStorageDriver Driver(ServerConfig config, UserSpace space, PlainBackupSettings s, DmOffice.Mail.MicrosoftOAuth? oauth)
    {
        if (s.Target == PlainBackup.OneDrive)
        {
            if (oauth is not { Enabled: true }) throw new InvalidOperationException("Die Anmeldung bei Microsoft ist auf diesem Server nicht eingerichtet.");
            if (!config.Current.ExternalOneDriveForUsers) throw new UnauthorizedAccessException("OneDrive ist auf diesem Server für Benutzer nicht freigegeben.");
            var userId = space.User.Id;
            var token = s.OneDriveToken;
            var drive = new OneDriveDriver(ct => oauth.FilesTokenAsync(userId, token, ct), OneDriveHttp);
            return new SubfolderDriver(drive, StoragePath.Clean(s.Folder).Length > 0 ? s.Folder : PlainBackup.OneDriveFolder);
        }
        if (s.Target != PlainBackup.External)
        {
            if (!config.Current.ExternalWebDavForUsers) throw new UnauthorizedAccessException("WebDAV ist auf diesem Server für Benutzer nicht freigegeben.");
            return new WebDavDriver(s.Url, s.User, s.Password, ExternalFolders.ClientFor(config)) { RememberDirs = true };
        }
        // Eigener externer Ordner: Zugangsdaten und Grenzen von dort, nur wenn er beschreibbar ist. Serverordner nie - sonst
        // schriebe jede Person die Platte des Servers voll
        if (!Targets(space, config).Any(m => m.Id == s.Mount)) throw new UnauthorizedAccessException("Dieser externe Ordner ist als Ziel nicht möglich.");
        var (d, readOnly) = ExternalFolders.Open(space, config, s.Mount ?? "");
        if (readOnly) { d.Dispose(); throw new UnauthorizedAccessException("Der externe Ordner ist nur zum Lesen eingebunden."); }
        if (d is WebDavDriver w) w.RememberDirs = true;
        return StoragePath.Clean(s.Folder).Length > 0 ? new SubfolderDriver(d, s.Folder) : d;
    }

    /// <summary>Als Ziel wählbar: eigene externe Ordner (WebDAV, OneDrive, pCloud, soweit freigegeben), beschreibbar - keine Serverordner.</summary>
    private static List<ExternalFolders.Mount> Targets(UserSpace space, ServerConfig config) =>
        ExternalFolders.TargetsOf(space, config).Concat(ExternalFolders.MountsOf(space, config).Where(m => !m.ReadOnly && m.Kind != "folder")).ToList();

    /// <summary>OneDrive direkt als Ziel: Anmeldung bei Microsoft eingerichtet und OneDrive für Benutzer freigegeben.</summary>
    private static bool OneDriveAllowed(ServerConfig config, DmOffice.Mail.MicrosoftOAuth? oauth) => oauth?.Enabled == true && config.Current.ExternalOneDriveForUsers;

    // ---------------- Schnittstelle ----------------

    public sealed record SaveBody(bool Enabled, string? Target, string? Mount, string? Folder, string? Url, string? User, string? Password, string? Time,
        int TrashDays, bool Versions, List<string>? Areas, List<string>? Skip, bool Confirm, string? AccountPassword = null, string? Code = null);
    public sealed record TestBody(string? Target, string? Mount, string? Folder, string? Url, string? User, string? Password);

    private static object View(UserSpace space, ServerConfig config, DmOffice.Mail.MicrosoftOAuth? oauth)
    {
        var s = Settings(space);
        var state = PlainBackup.State(space.Db);
        return new
        {
            allowed = !config.Current.PlainBackupOff,
            available = PlainBackupAreas.Available(space),
            settings = new
            {
                s.Enabled, s.Target, s.Mount, s.Folder, s.Url, s.User, hasPassword = !string.IsNullOrEmpty(s.Password), s.Time, s.TrashDays, s.Versions, s.Areas, s.Skip,
                oneDrive = string.IsNullOrEmpty(s.OneDriveToken) ? null : s.OneDriveAccount ?? "",
            },
            // OneDrive geht nur, wenn die Anmeldung bei Microsoft eingerichtet ist (DMOFFICE_MS_CLIENT_ID) und die Verwaltung OneDrive freigibt
            oneDrive = OneDriveAllowed(config, oauth),
            // WebDAV-Adresse nur, wenn die Verwaltung eigene WebDAV-Ordner für Benutzer freigibt (derselbe Schalter wie beim Einbinden)
            webDav = config.Current.ExternalWebDavForUsers,
            // Eigene externe Ordner, in die geschrieben werden darf - als Ziel wählbar (keine Serverordner)
            // Externe Ziele zur Auswahl; eine früher gewählte Einbindung aus Dateien bleibt wählbar, solange sie eingestellt ist
            mounts = ExternalFolders.TargetsOf(space, config).Concat(Targets(space, config).Where(m => m.Access != null && m.Id == s.Mount))
                .Select(m => new { m.Id, m.Name, m.Kind, m.Account }).ToList(),
            confirmed = s.Confirmed != null && s.Confirmed == PlainBackup.ConfirmKey(s),
            ready = PlainBackup.Ready(s),
            state,
            next = PlainBackup.Ready(s) ? LastSlot(s.Time, space.Zone, DateTime.UtcNow).AddDays(1) : (DateTime?)null,
            running = Progress(space.User.Id),
            queued = Queued(space.User.Id),
        };
    }

    /// <summary>Adresse eines externen Ziels geändert: Datensicherung dorthin neu bestätigen lassen (bis dahin läuft sie nicht).</summary>
    public static void TargetChanged(UserSpace space, string mount)
    {
        var s = Settings(space);
        if (s.Target != PlainBackup.External || s.Mount == null) return;
        var uses = s.Mount == mount || ExternalFolders.Own(space).Any(m => m.Id == s.Mount && m.Access == mount);
        if (!uses || s.Confirmed == null) return;
        s.Confirmed = null;
        PlainBackup.Save(space.Db, s);
    }

    /// <summary>Einstellungen der Person. Ein früher eingerichtetes Ziel (eigene WebDAV-Adresse oder OneDrive-Anmeldung) wird dabei ein
    /// externes Ziel unter Einstellungen → Externe Ziele - gewählt wird nur noch ein externes Ziel. Bestätigung und gemerkte Stempel bleiben.</summary>
    public static PlainBackupSettings Settings(UserSpace space)
    {
        var s = PlainBackup.Settings(space.Db);
        if (s.Target == PlainBackup.External) return s;
        lock (space.Db)
        {
            s = PlainBackup.Settings(space.Db);
            if (s.Target == PlainBackup.External) return s;
            var confirmed = s.Confirmed != null && s.Confirmed == PlainBackup.ConfirmKey(s);
            var previous = PlainBackup.TargetId(s);
            UserMount? m = null;
            var folder = "";
            if (s.Target == PlainBackup.OneDrive && !string.IsNullOrEmpty(s.OneDriveToken))
            {
                m = new UserMount { Name = "OneDrive", Kind = UserMount.OneDrive, RefreshToken = s.OneDriveToken, Account = s.OneDriveAccount, Item = "root" };
                folder = StoragePath.Clean(s.Folder) is { Length: > 0 } f ? f : PlainBackup.OneDriveFolder;
            }
            else if (s.Target == PlainBackup.WebDav && s.Url.Trim().Length > 0)
                m = new UserMount
                {
                    Name = Uri.TryCreate(s.Url.Trim(), UriKind.Absolute, out var u) ? u.Host : "WebDAV", Kind = UserMount.WebDav,
                    Url = s.Url.Trim(), User = s.User, Password = s.Password,
                };
            if (m != null)
            {
                var list = ExternalFolders.Own(space);
                list.Add(m);
                space.Db.SetJson(ExternalFolders.UserKey, list);
            }
            s.Target = PlainBackup.External;
            s.Mount = m?.Id;
            s.Folder = folder;
            s.Url = "";
            s.User = s.Password = s.OneDriveToken = s.OneDriveAccount = null;
            if (confirmed) s.Confirmed = PlainBackup.ConfirmKey(s);
            if (m != null) PlainBackup.Retarget(space.Db, previous, s);
            PlainBackup.Save(space.Db, s);
            return s;
        }
    }

    /// <summary>Ziel aus der Eingabe; Fehlermeldung, wenn es so nicht geht. Ohne neues Passwort gilt das gespeicherte, solange
    /// Adresse und Benutzer gleich bleiben.</summary>
    private static string? TargetFrom(UserSpace space, ServerConfig config, PlainBackupSettings into, PlainBackupSettings saved,
        string? target, string? mount, string? folder, string? url, string? user, string? password)
    {
        // Nur noch externe Ziele (Einstellungen → Externe Ziele); ohne Wahl bleibt das Ziel leer
        into.Target = PlainBackup.External;
        if (string.IsNullOrEmpty(mount)) { into.Mount = null; into.Folder = ""; return null; }
        if (into.Target == PlainBackup.OneDrive)
        {
            // Anmeldung bleibt, wie sie gespeichert ist (eigene Endpunkte zum Anmelden und Trennen)
            into.OneDriveToken = saved.OneDriveToken;
            into.OneDriveAccount = saved.OneDriveAccount;
            try { into.Folder = StoragePath.Clean(folder) is { Length: > 0 } f ? f : PlainBackup.OneDriveFolder; }
            catch (ArgumentException) { return "Ungültiger Ordner."; }
            return string.IsNullOrEmpty(into.OneDriveToken) ? "Bitte zuerst bei Microsoft anmelden." : null;
        }
        if (into.Target == PlainBackup.External)
        {
            var m = Targets(space, config).FirstOrDefault(x => x.Id == mount) ?? ExternalFolders.MountsOf(space, config).FirstOrDefault(x => x.Id == mount);
            if (m == null) return "Diesen externen Ordner gibt es nicht.";
            if (m.Kind == "folder") return "Serverordner sind als Ziel nicht möglich.";
            if (m.ReadOnly) return "Der externe Ordner ist nur zum Lesen eingebunden.";
            try { into.Folder = StoragePath.Clean(folder); }
            catch (ArgumentException) { return "Ungültiger Unterordner."; }
            into.Mount = m.Id;
            return null;
        }
        if (!config.Current.ExternalWebDavForUsers) return "WebDAV ist auf diesem Server für Benutzer nicht freigegeben.";
        var u = (url ?? "").Trim();
        if (u.Length > 0 && !(Uri.TryCreate(u, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https"))
            return "Die Adresse muss mit https:// (oder http://) beginnen.";
        var who = string.IsNullOrWhiteSpace(user) ? null : user.Trim();
        var same = u.TrimEnd('/') == saved.Url.TrimEnd('/') && who == saved.User;
        into.Password = !string.IsNullOrEmpty(password) ? password : same ? saved.Password : null;
        into.Url = u;
        into.User = who;
        return null;
    }

    private static DmOffice.Mail.MicrosoftOAuth? Ms(HttpContext http) => http.RequestServices.GetService<DmOffice.Mail.MicrosoftOAuth>();

    public sealed record FlowBody(string? Flow);

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api/plainbackup").RequireAuthorization();

        // OneDrive: Anmeldung per Gerätecode (abgefragt wird über /api/settings/accounts/microsoft/poll), dann verbinden
        api.MapPost("/onedrive/start", async (HttpContext http, ServerConfig config, CancellationToken ct) =>
        {
            var space = http.Space();
            if (Ms(http) is not { Enabled: true } oauth || !config.Current.ExternalOneDriveForUsers) return Results.NotFound();
            return Results.Ok(await oauth.StartAsync(space.User.Id, ct, DmOffice.Mail.MicrosoftOAuth.FilesScopes));
        });
        api.MapPost("/onedrive/connect", async (FlowBody body, HttpContext http, ServerConfig config, CancellationToken ct) =>
        {
            var space = http.Space();
            if (Ms(http) is not { Enabled: true } oauth || !config.Current.ExternalOneDriveForUsers) return Results.NotFound();
            if (oauth.FilesGrant(space.User.Id, body.Flow) is not { } token) return Results.BadRequest(new { error = "Bitte zuerst bei Microsoft anmelden." });
            string? account;
            try { account = await new OneDriveDriver(c => oauth.FilesTokenAsync(space.User.Id, token, c), OneDriveHttp).AccountAsync(ct); }
            catch (Exception e) when (e is not OperationCanceledException) { return Results.BadRequest(new { error = "Verbindung fehlgeschlagen: " + e.GetBaseException().Message }); }
            var s = Settings(space);
            s.OneDriveToken = token;
            s.OneDriveAccount = account;
            if (s.Target != PlainBackup.OneDrive && !s.Enabled) { s.Target = PlainBackup.OneDrive; if (StoragePath.Clean(s.Folder).Length == 0) s.Folder = PlainBackup.OneDriveFolder; }
            PlainBackup.Save(space.Db, s);
            oauth.Forget(body.Flow);
            return Results.Ok(View(space, config, oauth));
        });
        // Trennen: Anmeldung vergessen; bei Microsoft bleibt alles, was schon dort liegt
        api.MapDelete("/onedrive", (HttpContext http, ServerConfig config) =>
        {
            var space = http.Space();
            var s = Settings(space);
            s.OneDriveToken = null;
            s.OneDriveAccount = null;
            PlainBackup.Save(space.Db, s);
            return Results.Ok(View(space, config, Ms(http)));
        });

        api.MapGet("", (HttpContext http, ServerConfig config) =>
            Results.Ok(View(http.Space(), config, Ms(http))));

        api.MapPut("", (SaveBody body, HttpContext http, ServerConfig config) =>
        {
            var space = http.Space();
            if (config.Current.PlainBackupOff) return Results.Json(new { error = "Der Datenexport ist auf diesem Server nicht freigeschaltet." }, statusCode: 403);
            var s = Settings(space);
            var saved = Settings(space);
            if (TargetFrom(space, config, s, saved, body.Target, body.Mount, body.Folder, body.Url, body.User, body.Password) is { } error)
                return Results.BadRequest(new { error });
            var time = (body.Time ?? s.Time).Trim();
            if (!TimeOnly.TryParseExact(time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                return Results.BadRequest(new { error = "Die Uhrzeit bitte als HH:MM angeben." });
            if (!PlainBackup.TrashChoices.Contains(body.TrashDays)) return Results.BadRequest(new { error = "Unbekannte Frist." });
            s.Time = time;
            s.TrashDays = body.TrashDays;
            s.Versions = body.Versions;
            s.Areas = (body.Areas ?? []).Where(PlainBackup.AllAreas.Contains).Distinct().ToList();
            s.Skip = (body.Skip ?? []).Where(x => x.Contains(':') && x.Length < 2000).Distinct().Take(2000).ToList();
            // Neues Ziel bestätigen (dorthin gehen ab jetzt alle Daten lesbar): nur mit dem Anmelde-Passwort, und gemeldet
            var newTarget = body.Confirm && saved.Confirmed != PlainBackup.ConfirmKey(s);
            if (newTarget)
            {
                var users = http.RequestServices.GetRequiredService<UserStore>();
                if (users.ById(space.User.Id) is not { } u) return Results.NotFound();
                // Mit Zwei-Faktor auch der Code: wer nur das Passwort kennt, soll die Daten nicht anderswohin sichern lassen können
                if (Auth.Confirm(users, u, body.AccountPassword, body.Code, needCode: true) is { } refused) return refused;
            }
            if (body.Confirm) s.Confirmed = PlainBackup.ConfirmKey(s);
            // Neues Ziel: die Sprache der Ordnernamen gilt ab dem ersten Lauf dorthin neu
            if (newTarget) s.Lang = null;
            if (body.Enabled)
            {
                if (!PlainBackup.HasTarget(s)) return Results.BadRequest(new { error = "Bitte ein externes Ziel wählen." });
                if (s.Confirmed != PlainBackup.ConfirmKey(s)) return Results.BadRequest(new { error = "Bitte zuerst bestätigen, dass die Daten im Ziel lesbar liegen." });
            }
            s.Enabled = body.Enabled;
            PlainBackup.Save(space.Db, s);
            if (newTarget) SecurityNotice.Important(http.RequestServices, space.User.Id, "Ziel des Datenexports geändert");
            return Results.Ok(View(space, config, Ms(http)));
        });

        // Verbindungstest: Probe schreiben, lesen, löschen
        api.MapPost("/test", async (TestBody body, HttpContext http, ServerConfig config, CancellationToken ct) =>
        {
            var space = http.Space();
            var saved = Settings(space);
            var s = new PlainBackupSettings();
            if (TargetFrom(space, config, s, saved, body.Target, body.Mount, body.Folder, body.Url, body.User, body.Password) is { } error)
                return Results.BadRequest(new { error });
            try
            {
                using var d = Driver(config, space, s, Ms(http));
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(60));
                await PlainBackup.TestAsync(d, cts.Token);
                var free = await d.SpaceAsync(cts.Token).ContinueWith(t => t.IsCompletedSuccessfully ? t.Result?.Free : null, TaskScheduler.Default);
                return Results.Ok(new { ok = true, free });
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                var msg = e is OperationCanceledException ? "Keine Antwort." : e.GetBaseException().Message;
                return Results.BadRequest(new { error = "Verbindung fehlgeschlagen: " + msg });
            }
        });

        api.MapPost("/run", (HttpContext http, ServerConfig config) =>
        {
            var space = http.Space();
            if (config.Current.PlainBackupOff || !PlainBackup.Ready(Settings(space)))
                return Results.BadRequest(new { error = "Der Datenexport ist nicht eingeschaltet." });
            RunNow(space.User.Id);
            return Results.Ok(View(space, config, Ms(http)));
        });

        api.MapPost("/stop", (HttpContext http, ServerConfig config) =>
        {
            var space = http.Space();
            Stop(space.User.Id);
            return Results.Ok(View(space, config, Ms(http)));
        });

        // Zum Abwählen: Unterordner der Dateiablage bzw. die Sammlungen eines Bereichs
        api.MapGet("/choices", (string area, string? path, HttpContext http) =>
        {
            var space = http.Space();
            var db = space.Db;
            object list = area switch
            {
                // Ohne den reservierten Systemordner
                PlainBackup.Files => FileStore.FilesOff(db) ? [] : FileStore.Children(db, FileStore.Normalize(path ?? ""))
                    .Where(f => f.IsDir && !FileStore.InRoot(f.Path, FileStore.ReservedRoot))
                    .Select(f => new { id = f.Path, name = f.Name, folder = true }).ToList(),
                PlainBackup.Calendar or PlainBackup.Tasks => DmOffice.Pim.PimStore.Collections(db, DmOffice.Pim.PimStore.Calendar)
                    .Where(c => !DmOffice.Pim.PimStore.IsReserved(c) && db.Scalar("SELECT 1 FROM objects WHERE collection=$c AND component=$k LIMIT 1",
                        ("$c", c.Id), ("$k", area == PlainBackup.Tasks ? "VTODO" : "VEVENT")) != null)
                    .Select(c => new { id = c.Uri, name = c.Name, folder = false }).ToList(),
                PlainBackup.Contacts => DmOffice.Pim.PimStore.Collections(db, DmOffice.Pim.PimStore.AddressBook).Select(c => new { id = c.Uri, name = c.Name, folder = false }).ToList(),
                PlainBackup.Notes => db.Scalar("SELECT 1 FROM sqlite_master WHERE name='notes'") == null ? []
                    : DmOffice.Notes.NotesDb.Of(space).AllFolders().Where(f => f.Parent == (string.IsNullOrEmpty(path) ? null : path))
                        .Select(f => new { id = f.SyncId, name = f.Name, folder = true }).ToList(),
                _ => [],
            };
            return Results.Ok(list);
        });
    }

    public sealed record AdminBody(bool Allowed);

    /// <summary>Verwaltung: freischalten oder sperren, dazu der Stand je Person - ohne Ziel und Zugangsdaten.</summary>
    public static void MapAdmin(RouteGroupBuilder api)
    {
        api.MapGet("/plainbackup", (ServerConfig config, Spaces spaces) => new
        {
            allowed = !config.Current.PlainBackupOff,
            users = spaces.All().Select(sp =>
            {
                var s = Settings(sp);
                var st = PlainBackup.State(sp.Db);
                return new { sp.User.Login, sp.User.DisplayName, on = PlainBackup.Ready(s), st.LastRun, st.LastOk, st.LastError, st.Fails, running = Progress(sp.User.Id) != null };
            }).OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList(),
        });
        api.MapPut("/plainbackup", (AdminBody body, ServerConfig config) =>
        {
            config.Update(s => s.PlainBackupOff = !body.Allowed);
            return Results.Ok();
        });
    }
}
