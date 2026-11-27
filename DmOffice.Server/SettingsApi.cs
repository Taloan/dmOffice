using DmOffice.Core;
using DmOffice.Mail;
using DmOffice.Notes;

namespace DmOffice;

public static class SettingsApi
{
    static readonly object UiPrefsLock = new();
    public record DiscoverBody(string Email);
    public record General(string? Theme, string? TimeZone, int? MarkReadDelay, string? OpenOnFolder, long? InviteCalendar = null, bool? SmartCalendar = null,
        string? AttachSaveTarget = null, string? AttachSaveFolder = null, bool? AttachSaveAsk = null);
    public record NewAppPassword(string Name, string? Password = null, string? Code = null);
    /// <summary>Kennung ruleforward: Ziel und (beim Bestätigen im Regeln-Dialog) der Code aus der Bestätigungsmail.</summary>
    public record ForwardTargetBody(string? Address, string? Code = null);
    public record RenameAppPassword(string? Name);
    public record NotiziConnect(string? Password = null, string? Code = null);
    public record TrustedEntry(string Entry);
    public record OAuthPoll(string Flow);

    public sealed class CompactJob
    {
        public bool Running { get; set; }
        public DateTime? Started { get; set; }
        public DateTime? Finished { get; set; }
        public long Before { get; set; }
        public long After { get; set; }
        public string? Error { get; set; }
        /// <summary>Gesetzt, wenn der freie Platz nicht reicht: so viel braucht das Verdichten, so viel ist frei, so viel fehlt. Dann lief nichts.</summary>
        public long Need { get; set; }
        public long? Free { get; set; }
        public long Missing { get; set; }
    }

    /// <summary>
    /// Platz, den das Verdichten braucht: VACUUM schreibt die Datenbank noch einmal (im Journal, bis es zurueckkopiert ist),
    /// dazu die Reserve. Reicht der Platz nicht, startet es gar nicht - mitten drin abzubrechen wuerde nichts zerstoeren
    /// (SQLite rollt zurueck), liesse aber eine halbe Kopie und eine volle Platte zurueck.
    /// </summary>
    public static (long Need, long? Free, long Missing) CompactSpace(UserSpace space, DataPaths paths, UpdateCenter update)
    {
        var (file, wal, _) = space.Db.SpaceInfo();
        var need = file + wal + update.Reserve;
        var free = update.FreeBytes(Path.GetDirectoryName(paths.UserDb(space.User.Id))!);
        return (need, free, free is { } f ? Math.Max(0, need - f) : 0);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CompactJob> Compactions = new();

    /// <summary>Versionsnummer aus dem Image (DMOFFICE_VERSION), sonst aus der Assembly.</summary>
    public static string Version() =>
        Environment.GetEnvironmentVariable("DMOFFICE_VERSION") is { Length: > 0 } v ? v
        : "dev-" + (typeof(SettingsApi).Assembly.GetName().Version?.ToString(3) ?? "0");

    /// <summary>Name der Version des Zweigs ("1.5", "2.0") aus release.txt neben der Assembly; leer, wenn die Datei fehlt.</summary>
    public static string Release() => ReleaseText.Value;

    // Einmal lesen: /api/health fragt ohne Anmeldung und alle 15 s - nicht jedes Mal die Platte
    private static readonly Lazy<string> ReleaseText = new(() =>
    {
        try { return File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "release.txt")).Trim(); }
        catch (IOException) { return ""; }
    });

    /// <summary>Konto ohne Passwoerter - die Oberflaeche sieht nur, ob eines gesetzt ist.</summary>
    private static object Masked(MailAccount a, bool isDefault, UserSpace space) => new
    {
        a.Id, isDefault, a.Name, a.Email, a.DisplayName, a.ImapHost, a.ImapPort, a.ImapSecurity, a.ImapUser,
        hasImapPassword = !string.IsNullOrEmpty(a.ImapPassword),
        a.Auth, hasOAuth = !string.IsNullOrEmpty(a.OAuthRefreshToken),
        hasGraph = !string.IsNullOrEmpty(a.GraphRefreshToken), a.GraphCalendars, a.GraphContacts, a.GraphTasks,
        a.SmtpHost, a.SmtpPort, a.SmtpSecurity, a.SmtpUser,
        hasSmtpPassword = !string.IsNullOrEmpty(a.SmtpPassword),
        a.Signature, a.AcceptAnyCertificate, a.SaveSentCopy, a.SyncMonths, a.MaxCachedMb, a.Storage, a.SlimDays, a.SlimMb, a.Language, a.TrashDays, a.JunkDays, a.FocusedInbox, a.FolderMap, a.InUnified,
        // [feature:mailaccountoff]
        disabled = a.Off, offNotes = a.Off ? MailAccountOff.Notes(space, a) : [],
        // [/feature:mailaccountoff]
    };


    /// <summary>Zeigt der Host auf den eigenen Container (Loopback, Link-Local, unbestimmt)? Dann kein Verbindungsversuch -
    /// das Heimnetz (selbst gehostete Mail) bleibt erlaubt. Auch Namen werden aufgeloest: ein DNS-Name, der auf
    /// 127.0.0.1 zeigt, ist ebenso tabu.</summary>
    private static async Task<bool> LoopbackHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        var h = host.Trim().Trim('[', ']');
        if (h.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        // Dieselbe Sperre wie für eigene WebDAV-Ordner mit Heimnetz: der Server selbst, link-lokal (Metadaten-Dienste),
        // Multicast, auch in IPv6 verpackt (NAT64, 6to4)
        static bool Bad(System.Net.IPAddress ip) => DmOffice.Storage.ExternalFolders.NeverAllowed(ip);
        if (System.Net.IPAddress.TryParse(h, out var lit)) return Bad(lit);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            return (await System.Net.Dns.GetHostAddressesAsync(h, cts.Token)).Any(Bad);
        }
        catch (Exception) { return false; }   // nicht aufloesbar: die Verbindung scheitert ohnehin
    }

    /// <summary>Zeigt das Konto auf einen anderen Server oder Benutzer als gespeichert? Dann darf das gespeicherte
    /// Passwort bzw. Microsoft-Token nicht mitgehen - sonst liesse es sich an einen frei gewaehlten Host schicken.</summary>
    private static bool TargetChanged(MailAccount existing, MailAccount body) =>
        existing.ImapHost != body.ImapHost.Trim() || existing.ImapPort != body.ImapPort
        || existing.ImapSecurity != body.ImapSecurity || existing.ImapUser != body.ImapUser.Trim()
        || existing.SmtpHost != body.SmtpHost.Trim() || existing.SmtpPort != body.SmtpPort
        || existing.SmtpSecurity != body.SmtpSecurity || existing.SmtpUser != body.SmtpUser.Trim()
        || existing.AcceptAnyCertificate != body.AcceptAnyCertificate && body.AcceptAnyCertificate;

    /// <summary>Fehlertext bei unvollstaendigem Konto (Adresse, Server, Ports); sonst null. Bei Microsoft-Anmeldung kommen die
    /// Server aus der Anmeldung, Adresse und Ports gelten trotzdem.</summary>
    internal static string? Invalid(MailAccount body)
    {
        var email = body.Email?.Trim() ?? "";
        if (email.Length < 3 || email.IndexOf('@') is <= 0 || email.EndsWith('@') || email.Any(char.IsWhiteSpace))
            return "Bitte eine gültige E-Mail-Adresse eintragen.";
        if (body.Auth != MailAuth.Microsoft && (string.IsNullOrWhiteSpace(body.ImapHost) || string.IsNullOrWhiteSpace(body.SmtpHost)))
            return "Bitte den Server für Posteingang und Postausgang eintragen.";
        if (body.ImapPort is < 1 or > 65535 || body.SmtpPort is < 1 or > 65535)
            return "Der Port muss zwischen 1 und 65535 liegen.";
        return null;
    }

    /// <summary>Fehlertext, wenn das Ziel geaendert wurde, aber Passwort bzw. neue Microsoft-Anmeldung fehlen; sonst null.</summary>
    private static string? MissingSecretFor(MailAccount existing, MailAccount body)
    {
        if (!TargetChanged(existing, body)) return null;
        if (body.Auth == MailAuth.Microsoft)
            return string.IsNullOrEmpty(body.OAuthFlow) ? "Bei geändertem Server bitte erneut bei Microsoft anmelden." : null;
        // Leeres SMTP-Passwort heisst „wie IMAP“ (Merge) - verlangt wird nur das IMAP-Passwort
        return string.IsNullOrEmpty(body.ImapPassword) ? "Bei geändertem Server bitte das Passwort erneut eingeben." : null;
    }

    /// <summary>Leeres Passwort beim Speichern heisst: das bisherige behalten. Das Microsoft-Token kommt
    /// nie aus der Anfrage, nur aus einer abgeschlossenen Anmeldung (Claim).</summary>

    internal static void Merge(MailAccount target, MailAccount body)
    {
        var imap = string.IsNullOrEmpty(body.ImapPassword) ? target.ImapPassword : body.ImapPassword;
        // Leeres SMTP-Passwort: bei gleichem Ziel das bisherige, bei neuem Ziel keines - dann gilt das IMAP-Passwort
        // (wie der Feldhinweis „leer: wie IMAP“ sagt); das alte ginge sonst an einen anderen Server
        var smtp = !string.IsNullOrEmpty(body.SmtpPassword) ? body.SmtpPassword : TargetChanged(target, body) ? null : target.SmtpPassword;
        target.Name = body.Name.Trim(); target.Email = body.Email.Trim(); target.DisplayName = body.DisplayName.Trim();
        target.ImapHost = body.ImapHost.Trim(); target.ImapPort = body.ImapPort; target.ImapSecurity = body.ImapSecurity;
        target.ImapUser = body.ImapUser.Trim(); target.ImapPassword = imap;
        target.Auth = body.Auth;
        if (target.Auth != MailAuth.Microsoft) { target.OAuthRefreshToken = null; target.GraphRefreshToken = null; }
        target.SmtpHost = body.SmtpHost.Trim(); target.SmtpPort = body.SmtpPort; target.SmtpSecurity = body.SmtpSecurity;
        target.SmtpUser = body.SmtpUser.Trim(); target.SmtpPassword = smtp;
        target.Signature = body.Signature; target.AcceptAnyCertificate = body.AcceptAnyCertificate;
        target.SaveSentCopy = body.SaveSentCopy;
        target.InUnified = body.InUnified;
        target.Language = body.Language == "en" ? "en" : "de";
        target.SyncMonths = Math.Max(0, body.SyncMonths);
        target.MaxCachedMb = Math.Clamp(body.MaxCachedMb, 1, 500);
        target.Storage = body.Storage == MailAccount.StorageSlim ? MailAccount.StorageSlim : MailAccount.StorageFull;
        target.SlimDays = Math.Clamp(body.SlimDays, 7, 365);
        target.SlimMb = Math.Clamp(body.SlimMb, 20, 10_000);
        target.TrashDays = Math.Clamp(body.TrashDays, 0, 3650);
        target.JunkDays = Math.Clamp(body.JunkDays, 0, 3650);
        target.FocusedInbox = body.FocusedInbox;
        // Ordnerzuordnung: fehlt sie in der Anfrage (ältere Oberfläche), bleibt die gespeicherte
        if (body.FolderMap != null)
        {
            var map = body.FolderMap.Where(kv => MailAccount.MappableFolders.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value.Trim());
            target.FolderMap = map.Count > 0 ? map : null;
        }
        if (string.IsNullOrWhiteSpace(target.Name)) target.Name = target.Email;
    }

    /// <summary>Token einer abgeschlossenen Microsoft-Anmeldung ins Konto uebernehmen.</summary>
    internal static void Claim(MailAccount target, string? refreshToken)
    {
        if (target.Auth == MailAuth.Microsoft && !string.IsNullOrEmpty(refreshToken)) target.OAuthRefreshToken = refreshToken;
    }

    /// <summary>Datenbank im Hintergrund verdichten - laeuft schon eine, bleibt es bei der.
    /// Auch der Aufraeumer fuer "Schlank" (MailCleanup.Slim) startet sie hierueber.</summary>
    public static CompactJob? CompactOf(string userId) => Compactions.GetValueOrDefault(userId);

    public static CompactJob StartCompact(UserSpace space, ILogger log, DataPaths? paths = null, UpdateCenter? update = null)
    {
        var job = Compactions.GetOrAdd(space.User.Id, _ => new CompactJob());
        lock (job)
        {
            if (job.Running) return job;
            if (paths != null && update != null)
            {
                var (need, free, missing) = CompactSpace(space, paths, update);
                job.Need = need; job.Free = free; job.Missing = missing;
                if (missing > 0)
                {
                    job.Error = "Zu wenig freier Platz zum Verdichten.";
                    log.LogWarning("Verdichten {User}: nicht gestartet, es fehlen etwa {Missing} MB freier Platz", space.User.Login, missing / 1024 / 1024);
                    return job;
                }
            }
            job.Running = true; job.Error = null; job.Started = DateTime.UtcNow; job.Finished = null;
            var before = space.Db.SpaceInfo();
            job.Before = before.File + before.Wal;
        }
        _ = Task.Run(() =>
        {
            using var activity = Activity.Start(space.User.Id, Activity.Compact);
            try { space.Db.Compact(); }
            catch (Exception ex) { job.Error = ex.Message; activity.Fail(ex.Message); log.LogWarning("Verdichten: {Error}", ex.Message); }
            var after = space.Db.SpaceInfo();
            lock (job) { job.After = after.File + after.Wal; job.Finished = DateTime.UtcNow; job.Running = false; }
        });
        return job;
    }

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api/settings").RequireAuthorization();

        api.MapGet("/", (HttpContext http) =>
        {
            var space = http.Space();
            var s = space.Settings;
            return new
            {
                s.Theme, s.TimeZone, s.MarkReadDelay, s.OpenOnFolder, s.InviteCalendar, s.SmartCalendar, s.AttachSaveTarget, s.AttachSaveFolder, s.AttachSaveAsk,
                // Domains, bei denen der Assistent "Mit Microsoft anmelden" anbietet - null: nicht eingerichtet
                microsoftDomains = http.RequestServices.GetRequiredService<MicrosoftOAuth>().Enabled ? MicrosoftOAuth.Domains.Order() : null,
                accounts = s.MailAccounts.Select(a => Masked(a, s.IsDefault(a), space)),
                user = new { space.User.Login, space.User.DisplayName, space.User.IsAdmin },
            };
        });

        api.MapPut("/general", (General body, HttpContext http) =>
        {
            if (body.TimeZone != null && Dav.Ics.Zone(body.TimeZone) == null) return Results.BadRequest(new { error = "Unbekannte Zeitzone." });
            http.Space().UpdateSettings(s =>
            {
                if (body.Theme is "system" or "light" or "dark") s.Theme = body.Theme;
                if (body.TimeZone != null) s.TimeZone = body.TimeZone;
                if (body.MarkReadDelay is int d) s.MarkReadDelay = Math.Clamp(d, -1, 60);
                if (body.OpenOnFolder is "none" or "first" or "unread" or "last") s.OpenOnFolder = body.OpenOnFolder;
                if (body.InviteCalendar is long ic) s.InviteCalendar = ic > 0 ? ic : null;
                if (body.SmartCalendar is bool sc) s.SmartCalendar = sc;
                if (body.AttachSaveTarget is "ablage" or "browser") s.AttachSaveTarget = body.AttachSaveTarget;
                if (body.AttachSaveFolder != null) s.AttachSaveFolder = DmOffice.Dav.FileStore.Normalize(body.AttachSaveFolder) is { Length: > 0 } f ? f : null;
                if (body.AttachSaveAsk is bool ask) s.AttachSaveAsk = ask;
            });
            return Results.Ok();
        });

        // ---------------- Mailkonten ----------------

        api.MapPost("/accounts", async (MailAccount body, HttpContext http, MailSync sync, MicrosoftOAuth oauth) =>
        {
            var a = new MailAccount();
            var space = http.Space();
            if (Invalid(body) is { } invalid) return Results.BadRequest(new { error = invalid });
            if (await LoopbackHost(body.ImapHost) || await LoopbackHost(body.SmtpHost)) return Results.BadRequest(new { error = "Dieser Server ist nicht erlaubt." });
            Merge(a, body);
            Claim(a, oauth.Grant(space.User.Id, body.OAuthFlow));
            if (a.Auth == MailAuth.Microsoft && string.IsNullOrEmpty(a.OAuthRefreshToken))
                return Results.BadRequest(new { error = "Erst bei Microsoft anmelden." });
            space.UpdateSettings(s => { DmOffice.Mail.ForwardTargets.Remember(s); s.MailAccounts.Add(a); });
            oauth.Forget(body.OAuthFlow);
            sync.Kick(space.User.Id, a.Id);
            return Results.Ok(new { a.Id });
        });

        api.MapPut("/accounts/{id}", async (string id, MailAccount body, HttpContext http, MailSync sync, MicrosoftOAuth oauth, ImapPool pool, ILogger<MailCleanup> log) =>
        {
            var space = http.Space();
            var existing = space.Settings.MailAccounts.FirstOrDefault(a => a.Id == id) ?? throw new KeyNotFoundException("Konto nicht gefunden.");
            if (Invalid(body) is { } invalid) return Results.BadRequest(new { error = invalid });
            // Neuer Server: gespeichertes Passwort bzw. Microsoft-Token nicht mitnehmen (wie beim Test)
            if (MissingSecretFor(existing, body) is { } missing) return Results.BadRequest(new { error = missing });
            if (await LoopbackHost(body.ImapHost) || await LoopbackHost(body.SmtpHost)) return Results.BadRequest(new { error = "Dieser Server ist nicht erlaubt." });
            var token = oauth.Grant(space.User.Id, body.OAuthFlow);
            space.UpdateSettings(s =>
            {
                DmOffice.Mail.ForwardTargets.Remember(s);
                var a = s.MailAccounts.First(a => a.Id == id);
                Merge(a, body);
                Claim(a, token);
            });
            oauth.Forget(body.OAuthFlow);
            // Neu angemeldet: offene Verbindungen mit dem alten Token schliessen
            if (token != null) pool.Close(id);
            sync.Kick(space.User.Id, id);
            // Schlank: gleich aufraeumen, damit der Platz frei wird - nicht erst beim naechsten stuendlichen Lauf
            if (space.Settings.MailAccounts.FirstOrDefault(a => a.Id == id) is { Slim: true })
                _ = Task.Run(() =>
                {
                    try { if (MailCleanup.Slim(space, log).Count > 0) sync.Touch(space.User.Id); }
                    catch (Exception ex) { log.LogWarning("Schlank aufraeumen: {Error}", ex.Message); }
                });
            return Results.Ok();
        });

        // Microsoft-Konten: Anmeldung per Geraetecode (microsoft.com/devicelogin)
        api.MapPost("/accounts/microsoft/start", async (HttpContext http, MicrosoftOAuth oauth, CancellationToken ct) =>
            oauth.Enabled ? Results.Ok(await oauth.StartAsync(http.Space().User.Id, ct)) : Results.NotFound());
        api.MapPost("/accounts/microsoft/poll", async (OAuthPoll body, HttpContext http, MicrosoftOAuth oauth, CancellationToken ct) =>
            oauth.Enabled ? Results.Ok(new { status = await oauth.PollAsync(http.Space().User.Id, body.Flow, ct) }) : Results.NotFound());

        // [feature:mailaccountoff]
        MailAccountOff.Map(api);
        // [/feature:mailaccountoff]

        // Standard-Mailkonto festlegen (Features.DefaultAccount): genau eines ist Standard
        api.MapPut("/accounts/{id}/default", (string id, HttpContext http) =>
        {
            if (!Features.On(Features.DefaultAccount)) return Results.NotFound();
            var space = http.Space();
            if (space.Settings.MailAccounts.All(a => a.Id != id)) return Results.NotFound();
            space.UpdateSettings(s => s.DefaultMailAccount = id);
            return Results.Ok();
        });

        api.MapDelete("/accounts/{id}", (string id, HttpContext http, MailSync sync) =>
        {
            var space = http.Space();
            space.UpdateSettings(s => s.MailAccounts.RemoveAll(a => a.Id == id));
            MailStore.ForgetAccount(space.Db, id);
            DmOffice.Pim.Graph.UnlinkAccount(space.Db, id);
            return Results.Ok();
        });

        // Einrichtungsassistent: Server, Ports und Verschluesselung aus der Mailadresse herausfinden
        api.MapPost("/accounts/discover", async (DiscoverBody body, IHttpClientFactory factory, MicrosoftOAuth oauth, CancellationToken ct) =>
        {
            var email = body.Email.Trim();
            var r = await DmOffice.Mail.MailDiscovery.DiscoverAsync(factory.CreateClient("discover"), email, ct);
            return r == null ? Results.Ok(new { found = false })
                : Results.Ok(new { found = true, settings = r, auth = DmOffice.Mail.MailDiscovery.AuthFor(email, r, oauth.Enabled) });
        });

        api.MapPost("/accounts/test", async (MailAccount body, HttpContext http, MicrosoftOAuth oauth, CancellationToken ct) =>
        {
            var space = http.Space();
            var a = new MailAccount { Id = body.Id };
            if (space.Settings.MailAccounts.FirstOrDefault(x => x.Id == body.Id) is { } existing)
            {
                Merge(a, existing);
                a.OAuthRefreshToken = existing.OAuthRefreshToken;
                // Das gespeicherte Passwort bzw. Microsoft-Token nicht an einen anderen Server schicken
                if (MissingSecretFor(existing, body) is { } missing) return Results.BadRequest(new { error = missing });
            }
            Merge(a, body);
            Claim(a, oauth.Grant(space.User.Id, body.OAuthFlow));
            // Nur der eigene Container ist tabu (Loopback) - das Heimnetz ist bei selbst gehosteter Mail gewollt
            if (await LoopbackHost(a.ImapHost) || await LoopbackHost(a.SmtpHost)) return Results.BadRequest(new { error = "Dieser Server ist nicht erlaubt." });
            // IMAP und SMTP gleichzeitig, beide zusammen höchstens 15 s (ein nicht erreichbarer Server hielt den Test sonst 80 s fest)
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(15));
            var lt = limit.Token;
            async Task<string> Imap()
            {
                try
                {
                    using var imap = new MailKit.Net.Imap.ImapClient { Timeout = 15_000 };
                    if (a.AcceptAnyCertificate) imap.ServerCertificateValidationCallback = (_, _, _, _) => true;
                    await ImapPool.ConnectCheckedAsync(imap, a, a.ImapHost, a.ImapPort, a.ImapSecurity, lt);
                    await ImapPool.LoginAsync(imap, a, a.ImapUser, a.ImapPassword, lt);
                    await imap.DisconnectAsync(true, lt);
                    return "ok";
                }
                // Keine Server-Einzelheiten als Fehlertext (Port-Scan-Orakel) - nur Anmeldung vs. Verbindung
                catch (MailKit.Security.AuthenticationException) { return "Anmeldung fehlgeschlagen (Benutzer oder Passwort)."; }
                catch (Exception) when (!ct.IsCancellationRequested) { return "Keine Verbindung zum Server."; }
            }
            async Task<string> Smtp()
            {
                try
                {
                    using var smtp = await ImapPool.SmtpAsync(a, lt);
                    await smtp.DisconnectAsync(true, lt);
                    return "ok";
                }
                catch (MailKit.Security.AuthenticationException) { return "Anmeldung fehlgeschlagen (Benutzer oder Passwort)."; }
                catch (Exception) when (!ct.IsCancellationRequested) { return "Keine Verbindung zum Server."; }
            }
            var imapTask = Imap();
            var smtpTask = Smtp();
            var result = new Dictionary<string, string> { ["imap"] = await imapTask, ["smtp"] = await smtpTask };
            return Results.Ok(result);
        });

        // ---------------- Oberflaeche (Favoriten, Ordnerfarben, Reihenfolge ...) ----------------
        // Ein JSON-Dokument, das nur die Oberflaeche versteht - auf jedem Geraet gleich.

        // Sicherheitsprotokoll des Kontos (Tresor Stufe 1): Änderungen durch die Verwaltung und eigene
        api.MapGet("/security-log", (HttpContext http) => SecurityNotice.Of(http.Space().Db));

        api.MapGet("/ui", (HttpContext http) =>
            Results.Content(http.Space().Db.Get("ui.prefs") ?? "{}", "application/json"));

        api.MapPut("/ui", async (HttpRequest request, HttpContext http) =>
        {
            using var reader = new StreamReader(request.Body);
            var text = await reader.ReadToEndAsync();
            if (text.Length > 200_000) return Results.BadRequest(new { error = "Zu gross." });
            // nur ein gueltiges JSON-Objekt ablegen - Chat, Benachrichtigungen und Sprache lesen daraus einzelne Felder
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(text);
                if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return Results.BadRequest(new { error = "Ungültige Einstellungen." });
            }
            catch (System.Text.Json.JsonException) { return Results.BadRequest(new { error = "Ungültige Einstellungen." }); }
            http.Space().Db.Set("ui.prefs", text);
            return Results.Ok();
        });

        // Nur die geaenderten Felder zusammenfuehren (null loescht ein Feld). Ein alter Stand
        // aus einem anderen Tab oder vor dem ersten Laden ueberschreibt so nichts anderes.
        api.MapMethods("/ui", new[] { "PATCH" }, async (HttpRequest request, HttpContext http) =>
        {
            using var reader = new StreamReader(request.Body);
            var text = await reader.ReadToEndAsync();
            if (text.Length > 200_000) return Results.BadRequest(new { error = "Zu gross." });
            System.Text.Json.Nodes.JsonObject patch;
            try { patch = System.Text.Json.Nodes.JsonNode.Parse(text) as System.Text.Json.Nodes.JsonObject ?? throw new System.Text.Json.JsonException(); }
            catch (Exception) { return Results.BadRequest(new { error = "Ungültige Einstellungen." }); }
            var db = http.Space().Db;
            lock (UiPrefsLock)
            {
                System.Text.Json.Nodes.JsonObject current;
                try { current = System.Text.Json.Nodes.JsonNode.Parse(db.Get("ui.prefs") ?? "{}") as System.Text.Json.Nodes.JsonObject ?? new(); }
                catch (Exception) { current = new(); }
                foreach (var (k, v) in patch.ToList())
                {
                    if (v is null) current.Remove(k);
                    else current[k] = v.DeepClone();
                }
                var json = current.ToJsonString();
                if (json.Length > 200_000) return Results.BadRequest(new { error = "Zu gross." });
                db.Set("ui.prefs", json);
                return Results.Content(json, "application/json");
            }
        });

        // ---------------- Suchordner ----------------
        api.MapGet("/search-folders", (HttpContext http) => http.Space().Settings.SearchFolders);
        api.MapPut("/search-folders", (List<SearchFolder> list, HttpContext http) =>
        {
            var clean = list.Where(f => !string.IsNullOrWhiteSpace(f.Name)).Select(f => new SearchFolder
            {
                Id = string.IsNullOrWhiteSpace(f.Id) || f.Id.Length > 20 || !f.Id.All(char.IsLetterOrDigit) ? Guid.NewGuid().ToString("N")[..10] : f.Id,
                Name = f.Name.Trim(),
                Color = System.Text.RegularExpressions.Regex.IsMatch(f.Color ?? "", "^#[0-9a-fA-F]{6}$") ? f.Color! : "#0F6CBD",
                Criteria = f.Criteria ?? new(),
            }).ToList();
            http.Space().UpdateSettings(s => s.SearchFolders = clean);
            return Results.Ok(clean);
        });

        // ---------------- Kategorien ----------------
        api.MapGet("/categories", (HttpContext http) => { DmOffice.Mail.SmartInbox.Migrate(http.Space()); return Settings.Categories.Of(http.Space().Settings); });
        api.MapPut("/categories", (List<MailCategory> list, HttpContext http) =>
        {
            var clean = new List<MailCategory>();
            foreach (var c in list)
            {
                var name = c.Name.Trim();
                if (name.Length == 0) continue;
                var key = Settings.Categories.ValidKey(c.Key) ? c.Key : Settings.Categories.KeyFor(name, clean.Select(x => x.Key));
                if (clean.Any(x => x.Key == key)) key = Settings.Categories.KeyFor(name, clean.Select(x => x.Key));
                var preset = DmOffice.Mail.SmartInbox.BuiltIn.Any(b => b.Preset == c.Preset) ? c.Preset : null;
                clean.Add(new MailCategory { Key = key, Name = name, Color = System.Text.RegularExpressions.Regex.IsMatch(c.Color, "^#[0-9a-fA-F]{6}$") ? c.Color : "#0F6CBD", Smart = c.Smart, Preset = preset, Quiet = c.Quiet });
            }
            var space = http.Space();
            // Gelöschte Kategorien: Gelerntes dazu vergessen (die Schlagwörter an den Mails bleiben)
            foreach (var gone in Settings.Categories.Of(space.Settings).Select(c => c.Key).Where(k => !clean.Any(c => c.Key == k)))
                DmOffice.Mail.SmartInbox.Drop(space.Db, gone);
            space.UpdateSettings(s => s.Categories = clean);
            return Results.Ok(clean);
        });

        // ---------------- Sichere Absender fuer Bilder ----------------

        api.MapGet("/trusted-images", (HttpContext http) => http.Space().Settings.TrustedImageSenders);
        api.MapPost("/trusted-images", (TrustedEntry body, HttpContext http) =>
        {
            var e = body.Entry.Trim().ToLowerInvariant();
            if (e.Length < 3 || !e.Contains('@') || e.Contains(' ')) return Results.BadRequest(new { error = "Adresse oder @domain angeben." });
            http.Space().UpdateSettings(s => { if (!s.TrustedImageSenders.Contains(e)) s.TrustedImageSenders.Add(e); });
            return Results.Ok();
        });
        api.MapPost("/trusted-images/remove", (TrustedEntry body, HttpContext http) =>
        {
            http.Space().UpdateSettings(s => s.TrustedImageSenders.Remove(body.Entry.Trim().ToLowerInvariant()));
            return Results.Ok();
        });

        // ---------------- Speicher und Statistik ----------------

        // Kurz fuer die Fusszeile im Ordnerbereich: belegter Platz, Datei-Grenze, Version
        api.MapGet("/storage", (HttpContext http, DataPaths paths, UpdateCenter update, ServerConfig config) =>
        {
            var space = http.Space();
            var dbPath = paths.UserDb(space.User.Id);
            long FileSize(string f) => File.Exists(f) ? new FileInfo(f).Length : 0;
            return new
            {
                database = FileSize(dbPath) + FileSize(dbPath + "-wal"),
                files = Dav.FileStore.UsageTotal(space.Db),
                used = UserStorage.Used(space.Db),
                fileQuota = Dav.FileStore.Quota(space.Db),
                // freier Platz auf dem Datentraeger des Servers (fehlt, wenn er sich nicht ermitteln laesst). Mit Storage Extender
                // (Storage Extender als eigentliche Ablage) ist die Platte nur Zwischenspeicher - dann gilt allein das Kontingent
                diskFree = S3Mirror.CacheMode(config.Current) ? null : update.FreeBytes(paths.Root),
                version = Version()
            };
        });

        api.MapGet("/stats", (HttpContext http, DataPaths paths) =>
        {
            var space = http.Space();
            var db = space.Db;
            long FileSize(string f) => File.Exists(f) ? new FileInfo(f).Length : 0;
            var dbPath = paths.UserDb(space.User.Id);
            long Count(string sql) => Convert.ToInt64(db.Scalar(sql) ?? 0L);

            var accounts = space.Settings.MailAccounts.Select(a => new
            {
                a.Id, a.Name, a.Email, a.SyncMonths, a.Storage, a.SlimDays, a.SlimMb,
                // Schlank: Groesse (laut Server) der Mails, deren Inhalt nur beim Anbieter liegt
                serverOnly = a.Slim ? Convert.ToInt64(db.Scalar("SELECT COALESCE(SUM(size),0) FROM mail WHERE account=$a AND cached=0", ("$a", a.Id)) ?? 0L) : 0L,
                folders = db.Query(@"SELECT f.path, f.name, f.special, f.total, f.unread,
                        COUNT(m.id), COALESCE(SUM(m.size),0), COALESCE(SUM((SELECT length(raw) FROM mail_raw r WHERE r.id = m.id)),0),
                        COALESCE(SUM(m.cached),0), COALESCE(SUM(m.has_att),0), MIN(m.date), MAX(m.date)
                    FROM mail_folders f LEFT JOIN mail m ON m.account=f.account AND m.folder=f.path
                    WHERE f.account=$a GROUP BY f.path ORDER BY COALESCE(SUM(m.size),0) DESC",
                    r => new
                    {
                        path = r.GetString(0), name = r.GetString(1), special = r.IsDBNull(2) ? null : r.GetString(2),
                        serverTotal = r.GetInt32(3), unread = r.GetInt32(4), local = r.GetInt64(5),
                        size = r.GetInt64(6), stored = r.GetInt64(7), cached = r.GetInt64(8), withAttachments = r.GetInt64(9),
                        oldest = r.IsDBNull(10) ? (DateTimeOffset?)null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(10)),
                        newest = r.IsDBNull(11) ? (DateTimeOffset?)null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(11)),
                    }, ("$a", a.Id))
            }).ToList();

            var (file, wal, free) = db.SpaceInfo();
            var cs = CompactSpace(space, paths, http.RequestServices.GetRequiredService<UpdateCenter>());
            return new
            {
                database = FileSize(dbPath) + FileSize(dbPath + "-wal"),
                file, wal, free,
                mails = Count("SELECT COUNT(*) FROM mail"),
                mailBytes = Count("SELECT COALESCE(SUM(length(raw)),0) FROM mail_raw"),
                events = Count("SELECT COUNT(*) FROM objects WHERE component='VEVENT'"),
                tasks = Count("SELECT COUNT(*) FROM objects WHERE component='VTODO'"),
                contacts = Count("SELECT COUNT(*) FROM objects WHERE component='VCARD'"),
                notes = Count("SELECT COUNT(*) FROM notes WHERE deleted IS NULL"),
                noteBlobs = Count("SELECT COALESCE(SUM(length(data)),0) FROM note_blobs"),
                files = Count("SELECT COUNT(*) FROM files WHERE is_dir=0"),
                fileBytes = Count("SELECT COALESCE(SUM(size),0) FROM files"),
                // Alte Inhalte noch in der Datenbank (BlobMigration) - danach lohnt das Verdichten
                fileContentsPending = Dav.FileStore.PendingBlobs(db),
                // Platz fuers Verdichten: so viel braucht es voruebergehend, so viel ist frei
                compactNeed = cs.Need,
                diskFree = cs.Free,
                appPasswords = Count("SELECT COUNT(*) FROM app_passwords"),
                accounts
            };
        });

        // Datenbank verdichten - im Hintergrund, denn bei mehreren GB dauert es laenger als ein
        // Reverse-Proxy auf eine Antwort wartet. Die Oberflaeche fragt den Stand ab.
        api.MapPost("/compact", (HttpContext http, ILogger<UserSpace> log, DataPaths paths, UpdateCenter update) =>
        {
            var job = StartCompact(http.Space(), log, paths, update);
            return job.Missing > 0 && !job.Running ? Results.Json(job, statusCode: 409) : Results.Ok(job);
        });
        api.MapGet("/compact", (HttpContext http) =>
            Compactions.TryGetValue(http.Space().User.Id, out var job) ? Results.Ok(job) : Results.Ok(new CompactJob()));

        // ---------------- Regeln ----------------

        api.MapGet("/rules", (HttpContext http) => http.Space().Settings.Rules);
        // Regeln speichern: als Liste oder { rules, password, code } - neue Weiterleitungsziele nur mit dem Anmelde-Passwort, und gemeldet
        api.MapPut("/rules", async (HttpContext http, UserStore users) =>
        {
            var web = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
            List<MailRule> rules;
            string? password = null, code = null;
            try
            {
                var el = await http.Request.ReadFromJsonAsync<System.Text.Json.JsonElement>(http.RequestAborted);
                if (el.ValueKind == System.Text.Json.JsonValueKind.Array) rules = System.Text.Json.JsonSerializer.Deserialize<List<MailRule>>(el, web) ?? [];
                else if (el.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    rules = el.TryGetProperty("rules", out var rs) ? System.Text.Json.JsonSerializer.Deserialize<List<MailRule>>(rs, web) ?? [] : [];
                    if (el.TryGetProperty("password", out var pw) && pw.ValueKind == System.Text.Json.JsonValueKind.String) password = pw.GetString();
                    if (el.TryGetProperty("code", out var cd) && cd.ValueKind == System.Text.Json.JsonValueKind.String) code = cd.GetString();
                }
                else return Results.BadRequest(new { error = "Ungültige Regel." });
            }
            catch (System.Text.Json.JsonException) { return Results.BadRequest(new { error = "Ungültige Regel." }); }
            // Felder mit null (statt Text oder Liste) nicht speichern - Abruf und Weiterleiten lesen sie ungeprüft
            if (rules.Any(r => r is null || r.Id is null || r.Name is null || r.Match is null || r.Conditions is null || r.Actions is null
                || r.Conditions.Any(c => c is null || c.Field is null || c.Op is null || c.Value is null)
                || r.Actions.Any(a => a is null || a.Type is null || a.Value is null)))
                return Results.BadRequest(new { error = "Ungültige Regel." });
            // Zieladressen und Tage prüfen, bevor etwas gilt: sonst wirkt die Aktion still nie (oder es gehen Bestätigungen an Unsinn)
            if (rules.Select(DmOffice.Mail.Rules.Problem).FirstOrDefault(p => p != null) is { } problem) return Results.BadRequest(new { error = problem });
            static HashSet<string> Targets(IEnumerable<MailRule> list) => DmOffice.Mail.ForwardTargets.Of(list);
            var added = Targets(rules).Except(Targets(http.Space().Settings.Rules)).ToList();
            if (added.Count > 0)
            {
                if (users.ById(http.Space().User.Id) is not { } u) return Results.NotFound();
                // Mit Zwei-Faktor auch der Code: wer nur das Passwort kennt, soll sich keine Mails umleiten können
                if (Auth.Confirm(users, u, password, code, needCode: true, http) is { } refused)
                    return Results.Json(new { error = u.TwoFactor ? "Neue Weiterleitungen nur mit Passwort und Code." : "Neue Weiterleitungen nur mit dem Passwort.", confirm = true }, statusCode: 403);
            }
            foreach (var r in rules)
                foreach (var c in r.Conditions.Where(c => c.Op == "regex"))
                    try { _ = new System.Text.RegularExpressions.Regex(c.Value); }
                    catch (ArgumentException) { return Results.BadRequest(new { error = $"Ungültiger regulärer Ausdruck in Regel: „{r.Name}“" }); }
            // Kennung ruleforward: neue Ziele warten auf Bestätigung durch den Empfänger, weggefallene vergessen
            List<string> pending = [], dropped = [];
            http.Space().UpdateSettings(s => { (pending, dropped) = DmOffice.Mail.ForwardTargets.Update(s, rules); s.Rules = rules; });
            var confirmIndex = http.RequestServices.GetRequiredService<DmOffice.Mail.ForwardConfirmIndex>();
            foreach (var h in dropped) confirmIndex.Remove(h);
            if (pending.Count > 0)
            {
                var space = http.Space();
                var origin = PublicAddress.Base(http);
                var log = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("DmOffice.Mail.ForwardTargets");
                // Im Hintergrund: Speichern wartet nicht auf den Mailserver
                _ = Task.Run(async () => { foreach (var a in pending) await DmOffice.Mail.ForwardTargets.SendAsync(space, confirmIndex, a, origin, log); });
            }
            if (added.Count > 0) SecurityNotice.Important(http.RequestServices, http.Space().User.Id, "Mail-Weiterleitung an " + string.Join(", ", added));
            return Results.Ok();
        });

        // Kennung ruleforward: Ziele von Weiterleiten/Umleiten mit Stand der Bestätigung
        api.MapGet("/forward-targets", (HttpContext http) =>
        {
            var space = http.Space();
            DmOffice.Mail.ForwardTargets.Ensure(space);
            var s = space.Settings;
            var now = DateTime.UtcNow;
            // Konten mit Umleiten-Regeln: 8BITMIME einmal im Hintergrund abfragen, die Oberfläche lädt kurz danach erneut
            var redirectAccounts = s.Rules.Where(r => r.Actions.Any(a => a.Type == "redirect"))
                .SelectMany(r => r.AccountId is { } id ? [id] : s.MailAccounts.Select(a => a.Id)).Distinct().ToList();
            foreach (var id in redirectAccounts.Where(id => s.Smtp8BitMime?.ContainsKey(id) != true))
                if (s.MailAccounts.FirstOrDefault(a => a.Id == id) is { } acc) DmOffice.Mail.ForwardTargets.Probe8Bit(space, acc);
            return Results.Ok(new
            {
                own = DmOffice.Mail.ForwardTargets.Own(s),
                no8bit = redirectAccounts.Where(id => s.Smtp8BitMime?.TryGetValue(id, out var ok) == true && !ok),
                targets = (s.ForwardTargets ?? []).Select(t => new
                {
                    address = t.Address, confirmed = t.Confirmed, sentAt = t.SentAt, expired = !t.Confirmed && t.Expires is { } e && e <= now,
                    error = t.Error, locked = !t.Confirmed && t.CodeTries >= DmOffice.Mail.ForwardTargets.MaxCodeTries,
                }),
            });
        });
        api.MapPost("/forward-targets/resend", async (ForwardTargetBody body, HttpContext http) =>
        {
            var space = http.Space();
            var a = DmOffice.Mail.ForwardTargets.Norm(body.Address ?? "");
            if (space.Settings.ForwardTargets?.FirstOrDefault(t => t.Address == a) is not { Confirmed: false } t)
                return Results.NotFound(new { error = "Dieses Ziel wartet nicht auf Bestätigung." });
            if (t.SentAt is { } sent && DateTime.UtcNow - sent < DmOffice.Mail.ForwardTargets.ResendPause)
                return Results.Json(new { error = "Gerade erst gesendet. Bitte eine Minute warten." }, statusCode: 429);
            var log = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("DmOffice.Mail.ForwardTargets");
            await DmOffice.Mail.ForwardTargets.SendAsync(space, http.RequestServices.GetRequiredService<DmOffice.Mail.ForwardConfirmIndex>(), a,
                PublicAddress.Base(http), log, http.RequestAborted);
            var error = space.Settings.ForwardTargets?.FirstOrDefault(x => x.Address == a)?.Error;
            return error == null ? Results.Ok() : Results.Json(new { error = "Bestätigungsmail nicht gesendet: " + error }, statusCode: 502);
        });
        api.MapPost("/forward-targets/confirm", (ForwardTargetBody body, HttpContext http) =>
        {
            var r = DmOffice.Mail.ForwardTargets.ConfirmCode(http.Space(), http.RequestServices.GetRequiredService<DmOffice.Mail.ForwardConfirmIndex>(),
                body.Address ?? "", body.Code ?? "", DateTime.UtcNow);
            return r switch
            {
                DmOffice.Mail.ForwardTargets.Result.Confirmed => Results.Ok(),
                DmOffice.Mail.ForwardTargets.Result.Expired => Results.BadRequest(new { error = "Der Code ist abgelaufen. Bitte die Bestätigung erneut senden." }),
                DmOffice.Mail.ForwardTargets.Result.TooManyTries => Results.BadRequest(new { error = "Zu viele falsche Codes. Bitte die Bestätigung erneut senden." }),
                DmOffice.Mail.ForwardTargets.Result.TooManyToday => Results.BadRequest(new { error = "Heute zu viele falsche Codes. Bitte den Link aus der Bestätigungsmail nutzen oder morgen erneut versuchen." }),
                DmOffice.Mail.ForwardTargets.Result.WrongCode => Results.BadRequest(new { error = "Der Code stimmt nicht." }),
                _ => Results.NotFound(new { error = "Dieses Ziel wartet nicht auf Bestätigung." }),
            };
        });

        // Jetzt ausfuehren: gespeicherte Regel auf die Mails im Posteingang anwenden (ohne Weiterleiten)
        api.MapPost("/rules/{id}/run", async (string id, HttpContext http, MailSync sync, ImapPool pool, CancellationToken ct) =>
        {
            var space = http.Space();
            var rule = space.Settings.Rules.FirstOrDefault(r => r.Id == id) ?? throw new KeyNotFoundException("Regel nicht gefunden - erst speichern.");
            if (rule.Conditions.Count == 0) return Results.BadRequest(new { error = "Die Regel hat keine Bedingung." });
            var r = await sync.RunRule(space, rule, pool, ct);
            sync.Touch(space.User.Id);
            return Results.Ok(new { @checked = r.Checked, matched = r.Matched });
        });

        // ---------------- Spamfilter ----------------

        api.MapGet("/spam", (HttpContext http) =>
        {
            var space = http.Space();
            var (spam, ham) = SpamFilter.Counts(space.Db);
            return new
            {
                settings = space.Settings.Spam, trainedSpam = spam, trainedHam = ham,
                lists = Blocklists.All.Select(l => new { id = l.Id, name = l.Name, kind = l.Kind == Blocklists.Kind.Ip ? "ip" : "domain", spamhaus = l.Dqs != null }),
                spamhausKey = !string.IsNullOrEmpty(http.RequestServices.GetRequiredService<ServerConfig>().SpamhausKey()),
                // Gemeinsames Junk-Wissen: nur wählbar, wenn der Admin es eingeschaltet hat
                sharedJunk = http.RequestServices.GetRequiredService<ServerConfig>().Current.SharedJunk,
                safe = SafeList.Of(space.Db),
            };
        });
        api.MapPut("/spam", (SpamSettings body, HttpContext http) =>
        {
            body.SpamCutoff = Math.Clamp(body.SpamCutoff, 0.5, 0.999);
            body.HamCutoff = Math.Clamp(body.HamCutoff, 0.01, body.SpamCutoff);
            body.Lists = (body.Lists ?? []).Where(id => Blocklists.All.Any(l => l.Id == id)).Distinct().ToList();
            body.ListsJunk = (body.ListsJunk ?? []).Where(body.Lists.Contains).Distinct().ToList();
            http.Space().UpdateSettings(s => s.Spam = body);
            return Results.Ok();
        });
        // Selbst eingetragene sichere Absender (eintragen nach „Kein Junk“ in der Mailansicht, /api/mail/{acc}/trust)
        api.MapDelete("/spam/safe", (string entry, HttpContext http) =>
        {
            SafeList.Remove(http.Space().Db, entry);
            return Results.Ok(SafeList.Of(http.Space().Db));
        });
        api.MapPost("/spam/train-folders", (HttpContext http) =>
        {
            var n = SpamFilter.TrainFromFolders(http.Space());
            return Results.Ok(n);
        });
        api.MapPost("/spam/reset", (HttpContext http) =>
        {
            SpamFilter.Reset(http.Space().Db);
            return Results.Ok();
        });

        // ---------------- App-Passwoerter und DAV ----------------

        api.MapGet("/app-passwords", (HttpContext http) => AppPasswords.List(http.Space().Db));
        // Neues App-Passwort: ein dauerhafter Zugang ohne Zwei-Faktor - nur mit Passwort (und Code) bestätigt, und gemeldet
        api.MapPost("/app-passwords", (NewAppPassword body, HttpContext http, UserStore users) =>
        {
            var space = http.Space();
            if (users.ById(space.User.Id) is not { } u) return Results.NotFound();
            if (Auth.Confirm(users, u, body.Password, body.Code, needCode: true, http) is { } refused) return refused;
            var password = AppPasswords.Create(space.Db, body.Name);
            SecurityNotice.Important(http.RequestServices, u.Id, $"neues App-Passwort „{body.Name}“");
            return Results.Ok(new { password });
        });
        api.MapPut("/app-passwords/{id}", (string id, RenameAppPassword body, HttpContext http) =>
        {
            AppPasswords.Rename(http.Space().Db, id, body.Name);
            return Results.Ok();
        });
        api.MapDelete("/app-passwords/{id}", (string id, HttpContext http) =>
        {
            AppPasswords.Delete(http.Space().Db, id);
            // Links, die das Gerät angelegt hat, enden mit
            foreach (var link in DmOffice.Dav.Links.DeleteForDevice(http.Space().Db, id)) http.RequestServices.GetRequiredService<DmOffice.Dav.LinkIndex>().Remove(http.Space().User.Id, link);
            return Results.Ok();
        });

        api.MapGet("/dav", (HttpContext http, E2e.ServerKey e2e) =>
        {
            var login = http.Space().User.Login;
            var origin = $"{http.Request.Scheme}://{http.Request.Host}";
            return new
            {
                serverKey = e2e.Public,
                serverKeyFingerprint = e2e.Fingerprint,
                server = origin + "/dav/",
                caldav = $"{origin}/dav/calendars/{login}/",
                carddav = $"{origin}/dav/addressbooks/{login}/",
                webdav = $"{origin}/dav/files/{login}/",
                notizi = $"{origin}/dav/files/{login}/{http.Space().Settings.Notizi.Root}/",
                login
            };
        });

        // ---------------- notizi ----------------

        api.MapGet("/notizi", async (HttpContext http, CancellationToken ct) =>
        {
            var space = http.Space();
            var n = space.Settings.Notizi;
            return new
            {
                n.Enabled, n.Root, n.DbFolder,
                hasPassword = !string.IsNullOrEmpty(n.E2eePassword),
                status = NotesSync.Status(space),
                databases = string.IsNullOrEmpty(n.E2eePassword) ? null : await NotesSync.ScanAsync(space, ct)
            };
        });
        // notizi verbinden: eigenes App-Passwort, das nur den Abgleichordner sieht, dazu ein Link fuer den QR-Code.
        // Ab dann sehen alle anderen Zugaenge (Explorer, Nextcloud, DAVx5 ...) den Ordner nicht mehr.
        // Wie jedes App-Passwort ein dauerhafter Zugang ohne Zwei-Faktor: nur mit Passwort (und Code) bestätigt.
        api.MapPost("/notizi/connect", (NotiziConnect body, HttpContext http, UserStore users) =>
        {
            var space = http.Space();
            if (users.ById(space.User.Id) is not { } u) return Results.NotFound();
            if (Auth.Confirm(users, u, body.Password, body.Code, needCode: true, http) is { } refused) return refused;
            var origin = $"{http.Request.Scheme}://{http.Request.Host}";
            var url = $"{origin}/dav/files/{Uri.EscapeDataString(space.User.Login)}/{space.Settings.Notizi.Root.Trim('/')}/";
            // Jeder Aufruf: ein weiterer Zugang (ein Geraet je QR-Code); bestehende bleiben gueltig, entfernen unter "Geraete & Sync"
            var password = AppPasswords.Create(space.Db, "notizi " + DateTime.Now.ToString("dd.MM. HH:mm"), AppPasswords.ScopeNotizi);
            SecurityNotice.Important(http.RequestServices, space.User.Id, "neuer Zugang für notizi");
            var link = "notizi://webdav?url=" + Uri.EscapeDataString(url) + "&user=" + Uri.EscapeDataString(space.User.Login)
                + "&password=" + Uri.EscapeDataString(password);
            return Results.Ok(new { url, login = space.User.Login, password, link });
        });

        api.MapPut("/notizi", (NotiziSettings body, HttpContext http, NotesSync notes) =>
        {
            var space = http.Space();
            string root;
            try { root = string.IsNullOrWhiteSpace(body.Root) ? "notizisync" : DmOffice.Dav.FileStore.Normalize(body.Root.Trim()); }
            catch (ArgumentException) { return Results.BadRequest(new { error = "Dieser Name geht für den Abgleichordner nicht." }); }
            if (root.Length == 0 || DmOffice.Dav.FileStore.InRoot(root, DmOffice.Dav.FileStore.SystemVault) || DmOffice.Dav.FileStore.InRoot(root, DmOffice.Dav.FileStore.OldSystemVault)
                || DmOffice.Dav.FileStore.InRoot(root, DmOffice.Dav.FileStore.ReservedRoot) || DmOffice.Dav.FileStore.Get(space.Db, root) is { IsDir: false })
                return Results.BadRequest(new { error = "Dieser Name geht für den Abgleichordner nicht." });
            space.UpdateSettings(s =>
            {
                var changedPlace = root != s.Notizi.Root || body.DbFolder != s.Notizi.DbFolder;
                s.Notizi.Enabled = body.Enabled;
                s.Notizi.Root = root;
                if (!string.IsNullOrEmpty(body.E2eePassword)) s.Notizi.E2eePassword = body.E2eePassword;
                if (changedPlace)
                {
                    s.Notizi.DbFolder = body.DbFolder.Trim().Trim('/');
                    // Die Notizen hier bleiben bis zum naechsten Abgleich - der raeumt sie weg, wenn sie
                    // zu einer anderen Datenbank gehoeren (nie mitnehmen, NotesSync.RunAsync)
                    s.Notizi.SwitchDb();
                }
            });
            notes.Kick(space.User.Id);
            return Results.Ok();
        });
    }
}
