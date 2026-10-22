using DmOffice.Core;
using DmOffice.Mail;
using DmOffice.Notes;

namespace DmOffice;

public static class SettingsApi
{
    public record DiscoverBody(string Email);
    public record General(string? Theme, string? TimeZone, int? MarkReadDelay, string? OpenOnFolder, long? InviteCalendar = null, bool? SmartCalendar = null);
    public record NewAppPassword(string Name);
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
    private static object Masked(MailAccount a) => new
    {
        a.Id, a.Name, a.Email, a.DisplayName, a.ImapHost, a.ImapPort, a.ImapSecurity, a.ImapUser,
        hasImapPassword = !string.IsNullOrEmpty(a.ImapPassword),
        a.Auth, hasOAuth = !string.IsNullOrEmpty(a.OAuthRefreshToken),
        a.SmtpHost, a.SmtpPort, a.SmtpSecurity, a.SmtpUser,
        hasSmtpPassword = !string.IsNullOrEmpty(a.SmtpPassword),
        a.Signature, a.AcceptAnyCertificate, a.SaveSentCopy, a.SyncMonths, a.MaxCachedMb, a.Storage, a.SlimDays, a.SlimMb, a.Language, a.TrashDays, a.JunkDays, a.FocusedInbox
    };

    /// <summary>Zeigt der Host auf den eigenen Container (Loopback, Link-Local, unbestimmt)? Dann kein Verbindungsversuch -
    /// das Heimnetz (selbst gehostete Mail) bleibt erlaubt. Auch Namen werden aufgeloest: ein DNS-Name, der auf
    /// 127.0.0.1 zeigt, ist ebenso tabu.</summary>
    private static async Task<bool> LoopbackHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        var h = host.Trim().Trim('[', ']');
        if (h.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        static bool Bad(System.Net.IPAddress ip)
        {
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            return System.Net.IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.Equals(System.Net.IPAddress.Any) || ip.Equals(System.Net.IPAddress.IPv6Any)
                   || ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && ip.GetAddressBytes() is [169, 254, ..];
        }
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

    /// <summary>Fehlertext, wenn das Ziel geaendert wurde, aber Passwort bzw. neue Microsoft-Anmeldung fehlen; sonst null.</summary>
    private static string? MissingSecretFor(MailAccount existing, MailAccount body)
    {
        if (!TargetChanged(existing, body)) return null;
        if (body.Auth == MailAuth.Microsoft)
            return string.IsNullOrEmpty(body.OAuthFlow) ? "Bei geändertem Server bitte erneut bei Microsoft anmelden." : null;
        return string.IsNullOrEmpty(body.ImapPassword) || string.IsNullOrEmpty(body.SmtpPassword)
            ? "Bei geändertem Server bitte das Passwort erneut eingeben." : null;
    }

    /// <summary>Leeres Passwort beim Speichern heisst: das bisherige behalten. Das Microsoft-Token kommt
    /// nie aus der Anfrage, nur aus einer abgeschlossenen Anmeldung (Claim).</summary>

    internal static void Merge(MailAccount target, MailAccount body)
    {
        var imap = string.IsNullOrEmpty(body.ImapPassword) ? target.ImapPassword : body.ImapPassword;
        var smtp = string.IsNullOrEmpty(body.SmtpPassword) ? target.SmtpPassword : body.SmtpPassword;
        target.Name = body.Name.Trim(); target.Email = body.Email.Trim(); target.DisplayName = body.DisplayName.Trim();
        target.ImapHost = body.ImapHost.Trim(); target.ImapPort = body.ImapPort; target.ImapSecurity = body.ImapSecurity;
        target.ImapUser = body.ImapUser.Trim(); target.ImapPassword = imap;
        target.Auth = body.Auth;
        if (target.Auth != MailAuth.Microsoft) target.OAuthRefreshToken = null;
        target.SmtpHost = body.SmtpHost.Trim(); target.SmtpPort = body.SmtpPort; target.SmtpSecurity = body.SmtpSecurity;
        target.SmtpUser = body.SmtpUser.Trim(); target.SmtpPassword = smtp;
        target.Signature = body.Signature; target.AcceptAnyCertificate = body.AcceptAnyCertificate;
        target.SaveSentCopy = body.SaveSentCopy;
        target.Language = body.Language == "en" ? "en" : "de";
        target.SyncMonths = Math.Max(0, body.SyncMonths);
        target.MaxCachedMb = Math.Clamp(body.MaxCachedMb, 1, 500);
        target.Storage = body.Storage == MailAccount.StorageSlim ? MailAccount.StorageSlim : MailAccount.StorageFull;
        target.SlimDays = Math.Clamp(body.SlimDays, 7, 365);
        target.SlimMb = Math.Clamp(body.SlimMb, 20, 10_000);
        target.TrashDays = Math.Clamp(body.TrashDays, 0, 3650);
        target.JunkDays = Math.Clamp(body.JunkDays, 0, 3650);
        target.FocusedInbox = body.FocusedInbox;
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
                s.Theme, s.TimeZone, s.MarkReadDelay, s.OpenOnFolder, s.InviteCalendar, s.SmartCalendar,
                // Domains, bei denen der Assistent "Mit Microsoft anmelden" anbietet - null: nicht eingerichtet
                microsoftDomains = http.RequestServices.GetRequiredService<MicrosoftOAuth>().Enabled ? MicrosoftOAuth.Domains.Order() : null,
                accounts = s.MailAccounts.Select(Masked),
                user = new { space.User.Login, space.User.DisplayName, space.User.IsAdmin }
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
            });
            return Results.Ok();
        });

        // ---------------- Mailkonten ----------------

        api.MapPost("/accounts", async (MailAccount body, HttpContext http, MailSync sync, MicrosoftOAuth oauth) =>
        {
            var a = new MailAccount();
            var space = http.Space();
            if (await LoopbackHost(body.ImapHost) || await LoopbackHost(body.SmtpHost)) return Results.BadRequest(new { error = "Dieser Server ist nicht erlaubt." });
            Merge(a, body);
            Claim(a, oauth.Grant(space.User.Id, body.OAuthFlow));
            if (a.Auth == MailAuth.Microsoft && string.IsNullOrEmpty(a.OAuthRefreshToken))
                return Results.BadRequest(new { error = "Erst bei Microsoft anmelden." });
            space.UpdateSettings(s => s.MailAccounts.Add(a));
            oauth.Forget(body.OAuthFlow);
            sync.Kick(space.User.Id, a.Id);
            return Results.Ok(new { a.Id });
        });

        api.MapPut("/accounts/{id}", async (string id, MailAccount body, HttpContext http, MailSync sync, MicrosoftOAuth oauth, ImapPool pool, ILogger<MailCleanup> log) =>
        {
            var space = http.Space();
            var existing = space.Settings.MailAccounts.FirstOrDefault(a => a.Id == id) ?? throw new KeyNotFoundException("Konto nicht gefunden.");
            // Neuer Server: gespeichertes Passwort bzw. Microsoft-Token nicht mitnehmen (wie beim Test)
            if (MissingSecretFor(existing, body) is { } missing) return Results.BadRequest(new { error = missing });
            if (await LoopbackHost(body.ImapHost) || await LoopbackHost(body.SmtpHost)) return Results.BadRequest(new { error = "Dieser Server ist nicht erlaubt." });
            var token = oauth.Grant(space.User.Id, body.OAuthFlow);
            space.UpdateSettings(s =>
            {
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

        api.MapDelete("/accounts/{id}", (string id, HttpContext http, MailSync sync) =>
        {
            var space = http.Space();
            space.UpdateSettings(s => s.MailAccounts.RemoveAll(a => a.Id == id));
            MailStore.ForgetAccount(space.Db, id);
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
            var result = new Dictionary<string, string>();
            try
            {
                using var imap = new MailKit.Net.Imap.ImapClient { Timeout = 20_000 };
                if (a.AcceptAnyCertificate) imap.ServerCertificateValidationCallback = (_, _, _, _) => true;
                await imap.ConnectAsync(a.ImapHost, a.ImapPort, ImapPool.Options(a.ImapSecurity), ct);
                await ImapPool.LoginAsync(imap, a, a.ImapUser, a.ImapPassword, ct);
                await imap.DisconnectAsync(true, ct);
                result["imap"] = "ok";
            }
            // Keine Server-Einzelheiten als Fehlertext (Port-Scan-Orakel) - nur Anmeldung vs. Verbindung
            catch (MailKit.Security.AuthenticationException) { result["imap"] = "Anmeldung fehlgeschlagen (Benutzer oder Passwort)."; }
            catch (Exception) { result["imap"] = "Keine Verbindung zum Server."; }
            try
            {
                using var smtp = await ImapPool.SmtpAsync(a, ct);
                await smtp.DisconnectAsync(true, ct);
                result["smtp"] = "ok";
            }
            catch (MailKit.Security.AuthenticationException) { result["smtp"] = "Anmeldung fehlgeschlagen (Benutzer oder Passwort)."; }
            catch (Exception) { result["smtp"] = "Keine Verbindung zum Server."; }
            return Results.Ok(result);
        });

        // ---------------- Oberflaeche (Favoriten, Ordnerfarben, Reihenfolge ...) ----------------
        // Ein JSON-Dokument, das nur die Oberflaeche versteht - auf jedem Geraet gleich.

        api.MapGet("/ui", (HttpContext http) =>
            Results.Content(http.Space().Db.Get("ui.prefs") ?? "{}", "application/json"));

        api.MapPut("/ui", async (HttpRequest request, HttpContext http) =>
        {
            using var reader = new StreamReader(request.Body);
            var text = await reader.ReadToEndAsync();
            if (text.Length > 200_000) return Results.BadRequest(new { error = "Zu gross." });
            using (System.Text.Json.JsonDocument.Parse(text)) { }   // nur gueltiges JSON ablegen
            http.Space().Db.Set("ui.prefs", text);
            return Results.Ok();
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
        api.MapGet("/categories", (HttpContext http) => Settings.Categories.Of(http.Space().Settings));
        api.MapPut("/categories", (List<MailCategory> list, HttpContext http) =>
        {
            var clean = new List<MailCategory>();
            foreach (var c in list)
            {
                var name = c.Name.Trim();
                if (name.Length == 0) continue;
                var key = Settings.Categories.ValidKey(c.Key) ? c.Key : Settings.Categories.KeyFor(name, clean.Select(x => x.Key));
                if (clean.Any(x => x.Key == key)) key = Settings.Categories.KeyFor(name, clean.Select(x => x.Key));
                clean.Add(new MailCategory { Key = key, Name = name, Color = System.Text.RegularExpressions.Regex.IsMatch(c.Color, "^#[0-9a-fA-F]{6}$") ? c.Color : "#0F6CBD" });
            }
            http.Space().UpdateSettings(s => s.Categories = clean);
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
        api.MapGet("/storage", (HttpContext http, DataPaths paths, UpdateCenter update) =>
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
                // freier Platz auf dem Datentraeger des Servers (fehlt, wenn er sich nicht ermitteln laesst)
                diskFree = update.FreeBytes(paths.Root),
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
        api.MapPut("/rules", (List<MailRule> rules, HttpContext http) =>
        {
            foreach (var r in rules)
                foreach (var c in r.Conditions.Where(c => c.Op == "regex"))
                    try { _ = new System.Text.RegularExpressions.Regex(c.Value); }
                    catch (ArgumentException) { return Results.BadRequest(new { error = $"Ungültiger regulärer Ausdruck in Regel: „{r.Name}“" }); }
            http.Space().UpdateSettings(s => s.Rules = rules);
            return Results.Ok();
        });

        // ---------------- Spamfilter ----------------

        api.MapGet("/spam", (HttpContext http) =>
        {
            var space = http.Space();
            var (spam, ham) = SpamFilter.Counts(space.Db);
            return new { settings = space.Settings.Spam, trainedSpam = spam, trainedHam = ham };
        });
        api.MapPut("/spam", (SpamSettings body, HttpContext http) =>
        {
            body.SpamCutoff = Math.Clamp(body.SpamCutoff, 0.5, 0.999);
            body.HamCutoff = Math.Clamp(body.HamCutoff, 0.01, body.SpamCutoff);
            http.Space().UpdateSettings(s => s.Spam = body);
            return Results.Ok();
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
        api.MapPost("/app-passwords", (NewAppPassword body, HttpContext http) =>
            Results.Ok(new { password = AppPasswords.Create(http.Space().Db, body.Name) }));
        api.MapDelete("/app-passwords/{id}", (string id, HttpContext http) =>
        {
            AppPasswords.Delete(http.Space().Db, id);
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
        api.MapPost("/notizi/connect", (HttpContext http) =>
        {
            var space = http.Space();
            var origin = $"{http.Request.Scheme}://{http.Request.Host}";
            var url = $"{origin}/dav/files/{Uri.EscapeDataString(space.User.Login)}/{space.Settings.Notizi.Root.Trim('/')}/";
            // Jeder Aufruf: ein weiterer Zugang (ein Geraet je QR-Code); bestehende bleiben gueltig, entfernen unter "Geraete & Sync"
            var password = AppPasswords.Create(space.Db, "notizi " + DateTime.Now.ToString("dd.MM. HH:mm"), AppPasswords.ScopeNotizi);
            var link = "notizi://webdav?url=" + Uri.EscapeDataString(url) + "&user=" + Uri.EscapeDataString(space.User.Login)
                + "&password=" + Uri.EscapeDataString(password);
            return Results.Ok(new { url, login = space.User.Login, password, link });
        });

        api.MapPut("/notizi", (NotiziSettings body, HttpContext http, NotesSync notes) =>
        {
            var space = http.Space();
            space.UpdateSettings(s =>
            {
                var root = string.IsNullOrWhiteSpace(body.Root) ? "notizisync" : body.Root.Trim().Trim('/');
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
