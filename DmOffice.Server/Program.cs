using DmOffice;
using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Mail;
using DmOffice.Notes;
using DmOffice.Pim;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;

// Kommandozeilenwerkzeug: einzelne Dateien aus Core-Stand und Files-Backup holen - startet keinen Server und keine Hintergrunddienste
if (args.Length > 0 && args[0] == RestoreTool.Command) Environment.Exit(await RestoreTool.RunAsync(args[1..], Console.Out, Console.Error, CancellationToken.None));
// [feature:cfgseal]
// Einstellungsdateien nach einer gewollten Änderung von Hand neu versiegeln (Server gestoppt)
if (args.Length > 0 && args[0] == ConfigSeal.Command)
{
    var sealPaths = new DataPaths(Environment.GetEnvironmentVariable("DMOFFICE_DATA")
                                  ?? (OperatingSystem.IsWindows() ? Path.Combine(AppContext.BaseDirectory, "data") : "/data"));
    try { Environment.Exit(ConfigSeal.Run(new Vault(sealPaths, Environment.GetEnvironmentVariable("DMOFFICE_MASTER_KEY"), readOnly: true), sealPaths, Console.Out)); }
    catch (InvalidOperationException e) { Console.Error.WriteLine(e.Message); Environment.Exit(1); }
}
// [/feature:cfgseal]

var builder = WebApplication.CreateBuilder(args);
// Werkzeuge (OCR, PDF) laufen unter demselben Benutzer: Speicher und Umgebung des Servers für sie sperren
var processSealed = DmOffice.Core.Features.On(DmOffice.Core.Features.ToolIsolation) && DmOffice.Core.ProcessGuard.Seal();

var dataDir = Environment.GetEnvironmentVariable("DMOFFICE_DATA")
              ?? (OperatingSystem.IsWindows() ? Path.Combine(AppContext.BaseDirectory, "data") : "/data");
var paths = new DataPaths(dataDir, Environment.GetEnvironmentVariable("DMOFFICE_BLOBS"));
// Systemprotokoll auf Dauer: je Tag eine Datei unter <Daten>/logs (vor allem anderen, damit auch Startfehler drinstehen)
if (DmOffice.Core.Features.On(DmOffice.Core.Features.SyslogFile)) DmOffice.Core.SystemLogFile.Start(paths.Root);
// Hintergrundarbeit: wann sie zuletzt fertig wurde, über einen Neustart hinweg
DmOffice.Core.Activity.Init(paths.Root);
DmOffice.Core.JobControl.Init(paths.Root);
// Zwischendateien der Werkzeuge unter dem Datenverzeichnis; Reste eines abgebrochenen Laufs weg
DmOffice.Core.WorkDir.Init(paths.Root);
// Aus einer S3-Sicherung wiederhergestellt (Ersteinrichtung): jetzt an seinen Platz, bevor etwas die Dateien oeffnet
var restoredFiles = S3Backup.ApplyPending(paths);
// [feature:masterkeyreq]
// Ohne eigenen Hauptschlüssel startet eine neue Installation nicht (bestehende mit Schlüsseldatei laufen mit Warnung weiter).
// Ausgenommen der Demo-Server: nur Beispieldaten, jede Nacht neu angelegt
if (DmOffice.Core.Features.On(DmOffice.Core.Features.MasterKeyRequired) && !DmOffice.Core.Demo.Enabled
    && Vault.StartRefused(paths, Environment.GetEnvironmentVariable("DMOFFICE_MASTER_KEY")) is { } refused)
{
    Console.Error.WriteLine("crit: DmOffice.Core.Vault[0]");
    Console.Error.WriteLine("      " + refused);
    DmOffice.Core.SystemLogFile.Current?.Write(DateTime.UtcNow, "Critical", "Vault", refused, (string?)null);
    DmOffice.Core.SystemLogFile.Stop();
    Environment.Exit(78);
}
// [/feature:masterkeyreq]
var vault = new Vault(paths, Environment.GetEnvironmentVariable("DMOFFICE_MASTER_KEY"));
// [feature:cfgseal]
// Benutzerliste und Einstellungen so, wie dmOffice sie geschrieben hat? Vor dem Laden - sonst würde eine fremde Änderung mitversiegelt
var configSeal = DmOffice.Core.Features.On(DmOffice.Core.Features.ConfigSeal) ? ConfigSeal.Check(paths, vault) : null;
if (configSeal?.State == ConfigSeal.State.Refused)
{
    Console.Error.WriteLine("crit: DmOffice.Core.ConfigSeal[0]");
    Console.Error.WriteLine("      " + configSeal.Message);
    DmOffice.Core.SystemLogFile.Current?.Write(DateTime.UtcNow, "Critical", "ConfigSeal", configSeal.Message!, (string?)null);
    DmOffice.Core.SystemLogFile.Stop();
    Environment.Exit(78);
}
// [/feature:cfgseal]

builder.Services.AddSingleton(paths);
builder.Services.AddSingleton(vault);
builder.Services.AddSingleton<UserStore>();
builder.Services.AddSingleton<ServerConfig>();
builder.Services.AddSingleton<UpdateCenter>();
// Datenaenderungen beim Start (Platzpruefung, Sicherung, Freigabe) und alte Sicherungen wegraeumen
builder.Services.AddHostedService<UpdateRunner>();
builder.Services.AddSingleton<DmOffice.Pim.ShareStore>();
// [feature:gatekeeper]
builder.Services.AddSingleton<DmOffice.GateKeeper.GateKeeperStore>();
builder.Services.AddSingleton<DmOffice.GateKeeper.GateKeeperLists>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DmOffice.GateKeeper.GateKeeperLists>());
builder.Services.AddSingleton<DmOffice.GateKeeper.GateKeeperStats>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DmOffice.GateKeeper.GateKeeperStats>());
builder.Services.AddHttpClient("gatekeeper").ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { UseCookies = false, AutomaticDecompression = System.Net.DecompressionMethods.All });
// [/feature:gatekeeper]
builder.Services.AddSingleton<LinkIndex>();
builder.Services.AddSingleton<DmOffice.Mail.ForwardConfirmIndex>();
builder.Services.AddSingleton<FolderShareStore>();
// Notfallzugang (Version 2): Pakete, Anfragen, Fristen
builder.Services.AddSingleton<DmOffice.Emergency.EmergencyStore>();
builder.Services.AddSingleton<DmOffice.Emergency.EmergencyContent>();
builder.Services.AddSingleton<DmOffice.Core.SecurityCenter>();
builder.Services.AddHostedService<DmOffice.Core.SecurityHousekeeping>();
builder.Services.AddSingleton<DmOffice.Gallery.AlbumStore>();
builder.Services.AddSingleton<DmOffice.Emergency.EmergencyService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DmOffice.Emergency.EmergencyService>());
// Galerie-Ordner (Version 2): Fotos und Videos aus externen Ordnern einlesen
builder.Services.AddHostedService<DmOffice.Gallery.GalleryScanJob>();
builder.Services.AddHostedService<DmOffice.Core.TresorSweep>();
builder.Services.AddHostedService<DmOffice.Gallery.ImportProfileJob>();
builder.Services.AddHostedService<DmOffice.Audio.AudioScanJob>();
// Große Vorschauen der Galerie-Ordner (Zwischenspeicher unter data/cache/gallery, verschlüsselt, nicht in der Sicherung)
builder.Services.AddSingleton<DmOffice.Gallery.GalleryProxies>();
builder.Services.AddSingleton<DmOffice.Gallery.GalleryShared>();
builder.Services.AddSingleton<DmOffice.Audio.AudioShared>();
builder.Services.AddHostedService<DmOffice.Gallery.GalleryProxyJob>();
builder.Services.AddHostedService<DmOffice.Gallery.GalleryMediumJob>(); // [feature:galmedium]
builder.Services.AddHostedService<FilesCleanup>();
// Lernende Kategorien: alte Smart Inbox übernehmen, übernommene Zuordnungen als Schlagwort zum Mailserver
builder.Services.AddHostedService<DmOffice.Mail.LabelJob>();
// [feature:journal]
builder.Services.AddHostedService<DmOffice.Journal.JournalMediaMove>();
// [/feature:journal]
builder.Services.AddHostedService<DmOffice.Dav.FileMetaJob>();
builder.Services.AddHostedService<DmOffice.Dav.FileCategoryJob>();
builder.Services.AddHostedService<DmOffice.Dms.DmsJob>();
// Alte Dateiinhalte aus den Datenbanken in den BlobStore (einmalig, unterbrechbar)
builder.Services.AddHostedService<BlobMigration>();
// Kopie der Dateiinhalte in einen S3-Speicher (Version 2) - als Singleton, die Verwaltung zeigt den Stand
builder.Services.AddSingleton<S3Mirror>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<S3Mirror>());
// Files-Backup: haelt die Dateiinhalte fuer die Haltedauer vor, laeuft vor jedem Core-Backup
builder.Services.AddSingleton<FilesBackup>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<FilesBackup>());
builder.Services.AddSingleton<ErasedUsersJob>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ErasedUsersJob>());
// Naechtliche Sicherung der Datenbanken nach S3 (Version 2)
builder.Services.AddSingleton<S3Backup>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<S3Backup>());
// Datensicherung: lesbare Kopie an ein WebDAV-Ziel je Person (docs/datensicherung-konzept.md)
builder.Services.AddHostedService<DmOffice.Storage.PlainBackupService>();
// Naechtliche Pruefung aller Datenbanken (integrity_check, kaputte Indizes neu aufbauen)
builder.Services.AddSingleton<DbHealth>();
builder.Services.AddSingleton<DbRescue>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DbHealth>());
builder.Services.AddHostedService<ThumbAhead>();
builder.Services.AddSingleton<DmOffice.Storage.ExternalIndex>();
builder.Services.AddSingleton<DmOffice.Storage.ExternalIndexer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DmOffice.Storage.ExternalIndexer>());
builder.Services.AddHostedService<DmOffice.Dav.PreviewAhead>();
builder.Services.AddHostedService<FileHashJob>();
builder.Services.AddSingleton<E2e.ServerKey>();
builder.Services.AddSingleton<Fcm>();
builder.Services.AddHostedService<Push.Watcher>();
builder.Services.AddHttpClient("fcm");
// Web Push (2.0): Abos in data/push.db, Mail/Erinnerungen ueber den Watcher, Chat ueber ChatPush
builder.Services.AddHttpClient("webpush").ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
builder.Services.AddSingleton<DmOffice.Core.WebPush>();
builder.Services.AddHostedService<DmOffice.Core.WebPushWatcher>();
// Chat (Version 2): eigene Datenbank data/chat.db, Signal an die Handys bei neuen Nachrichten
builder.Services.AddSingleton<DmOffice.Chat.ChatStore>();
builder.Services.AddHostedService<DmOffice.Chat.ChatPush>();
builder.Services.AddHostedService<DmOffice.Chat.PukkiService>();
builder.Services.AddHostedService<DmOffice.Chat.ChatRetention>();
builder.Services.AddHostedService<DmOffice.Dav.UploadJanitor>();
// Einzeln geteilte Notizen (Version 2): data/noteshares.json
builder.Services.AddSingleton<DmOffice.Notes.NoteShareStore>();
// Newsletter abmelden (One-Click): keine Umleitungen - sie koennten ins eigene Netz fuehren
builder.Services.AddHttpClient("unsub").ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, ConnectCallback = DmOffice.Mail.Unsubscribe.ConnectPublicAsync });
builder.Services.AddHttpClient("oidc", c => { c.Timeout = TimeSpan.FromSeconds(15); c.MaxResponseContentBufferSize = 1024 * 1024; }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
// Autoconfig: nur öffentliche Adressen, verbunden mit der geprüften Adresse
builder.Services.AddHttpClient("discover").ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, ConnectCallback = DmOffice.Mail.Unsubscribe.ConnectPublicAsync });
builder.Services.AddHttpClient("e2e").ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
// Office-Server (Collabora/ONLYOFFICE): Discovery lesen - keine Umleitungen, kurze Wartezeit
builder.Services.AddHttpClient("office", c => c.Timeout = TimeSpan.FromSeconds(15)).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
builder.Services.AddHttpClient("office-download", c => c.Timeout = TimeSpan.FromMinutes(5)).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
builder.Services.AddSingleton<DmOffice.Office.OfficeDiscovery>();
builder.Services.AddSingleton<Spaces>();
builder.Services.AddSingleton<ImapPool>();
// Private Microsoft-Konten (outlook.com, hotmail, live): OAuth2, Vorgabe ist die App-Registrierung des Projekts
var microsoftOAuth = new MicrosoftOAuth(MicrosoftOAuth.ClientIdFrom(Environment.GetEnvironmentVariable("DMOFFICE_MS_CLIENT_ID")), new HttpClient { Timeout = TimeSpan.FromSeconds(30) });
builder.Services.AddSingleton(microsoftOAuth);
ImapPool.OAuth = microsoftOAuth;
DmOffice.Storage.ExternalFolders.OAuth = microsoftOAuth;
builder.Services.AddSingleton<MailSync>();
builder.Services.AddSingleton<DmOffice.Mail.MailOutbox>();
builder.Services.AddHostedService<DmOffice.Mail.MailQueueSender>();
builder.Services.AddHostedService<DmOffice.Mail.MailCleanup>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MailSync>());
// [feature:mailpush]
// Neue Mails per IMAP IDLE gleich melden (Mail/MailPush.cs)
builder.Services.AddSingleton<DmOffice.Mail.MailPush>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DmOffice.Mail.MailPush>());
// [/feature:mailpush]
builder.Services.AddSingleton<NotesSync>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<NotesSync>());
builder.Services.AddHostedService<DmOffice.Pim.SubscriptionSync>();
builder.Services.AddHostedService<DmOffice.Pim.CalendarRemindTasks>();
builder.Services.AddSingleton<DmOffice.Pim.GraphSync>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DmOffice.Pim.GraphSync>());
// Volltextsuche in Anhaengen und Dateien (Search/): Warteschlange im Hintergrund
builder.Services.AddSingleton<DmOffice.Search.TextIndexer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DmOffice.Search.TextIndexer>());
builder.Services.AddSingleton<DmOffice.Mail.JunkPrints>();
builder.Services.AddHostedService<DmOffice.Mail.SpamMaintenance>();
builder.Services.AddMemoryCache(o => o.SizeLimit = 300);

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(paths.Keys))
    .SetApplicationName("dmOffice");
// Neue Schluessel des Schluesselrings (Cookies, WOPI-Tokens) mit dem Hauptschluessel einpacken - eine Kopie
// von keys/ allein reicht dann nicht mehr, um Anmelde-Cookies zu faelschen. Bestehende, unverpackte
// Schluessel bleiben lesbar und laufen regulaer aus.
builder.Services.Configure<Microsoft.AspNetCore.DataProtection.KeyManagement.KeyManagementOptions>(o =>
    o.XmlEncryptor = new VaultXmlEncryptor(vault));

// Anfragen nicht auf Information protokollieren: Pfad und Query enthalten Tokens (Freigabelinks, WOPI, Login-Flow)
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
// Systemprotokoll für die Verwaltung (Warnungen und Fehler seit dem Start)
builder.Logging.AddProvider(new DmOffice.Core.SystemLog());
// Ausgehende Anfragen ebenso: die Adressen enthalten Web-Push-Endpunkte, Abmelde-Tokens aus Newslettern, WOPI-Zugänge
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "dmoffice";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        // Secure, sobald die Anfrage über HTTPS kommt (auch hinter dem Proxy); „Always“ sperrte Installationen im Heimnetz ohne HTTPS aus
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.SlidingExpiration = true;
        o.ExpireTimeSpan = TimeSpan.FromDays(30);
        o.Events.OnValidatePrincipal = Auth.Validate;
        o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
    })
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, AppPasswordHandler>(Auth.AppPasswordScheme, null);

builder.Services.AddAuthorizationBuilder()
    // Web-API: Cookie (Browser) oder App-Passwort (Handy-App, eingeschraenkt - siehe Auth.AppPasswordAllowed)
    .SetDefaultPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder(CookieAuthenticationDefaults.AuthenticationScheme, Auth.AppPasswordScheme)
        .RequireAuthenticatedUser().Build())
    .AddPolicy("admin", p => p.AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme, Auth.AppPasswordScheme).RequireRole("admin"))
    .AddPolicy("dav", p => p.AddAuthenticationSchemes(Auth.AppPasswordScheme).RequireAuthenticatedUser());

builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 512L * 1024 * 1024);
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.MultipartBodyLengthLimit = 512L * 1024 * 1024);
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    // Nur Proxys im eigenen Netz glauben (Nginx Proxy Manager, Docker-Bruecke) - sonst
    // koennte jeder mit erfundenem X-Forwarded-For die Bremse beim Anmelden umgehen.
    // Andere Netze: DMOFFICE_TRUSTED_PROXIES="203.0.113.0/24,2001:db8::/32"
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
    foreach (var n in DmOffice.Core.ProxyTrust.Networks(Environment.GetEnvironmentVariable("DMOFFICE_TRUSTED_PROXIES"),
        DmOffice.Core.Features.On(DmOffice.Core.Features.ProxyTrust))) o.KnownIPNetworks.Add(n);
});
// Oeffentliche Links (/api/public): je Adresse gebremst - Ordner mit vielen Vorschaubildern gehen, Durchprobieren nicht
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("public", ctx => System.Threading.RateLimiting.RateLimitPartition.GetTokenBucketLimiter(
        Auth.IpKey(ctx.Connection.RemoteIpAddress),
        _ => new System.Threading.RateLimiting.TokenBucketRateLimiterOptions
        {
            TokenLimit = 300, TokensPerPeriod = 60, ReplenishmentPeriod = TimeSpan.FromSeconds(10), QueueLimit = 0, AutoReplenishment = true,
        }));
    // KI-Aufrufe auf Knopfdruck: je Benutzer gebremst - ein paar Seiten nacheinander gehen, Dauerfeuer nicht
    o.AddPolicy("ai", ctx => System.Threading.RateLimiting.RateLimitPartition.GetTokenBucketLimiter(
        ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? Auth.IpKey(ctx.Connection.RemoteIpAddress),
        _ => new System.Threading.RateLimiting.TokenBucketRateLimiterOptions
        {
            TokenLimit = 10, TokensPerPeriod = 5, ReplenishmentPeriod = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true,
        }));
});
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
// Große Listen (Galerie mit zehntausenden Fotos, Bildangaben, Suche) komprimiert - 30 MB JSON werden so ein paar MB.
// Nur dort (siehe UseWhen unten): Downloads, Vorschaubilder und Streams bleiben unverändert.
builder.Services.AddResponseCompression(o =>
{
    o.EnableForHttps = true;
    o.MimeTypes = ["application/json"];
    o.Providers.Add<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProvider>();
    o.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();
});
builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProviderOptions>(o => o.Level = System.IO.Compression.CompressionLevel.Fastest);
builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProviderOptions>(o => o.Level = System.IO.Compression.CompressionLevel.Fastest);

// Plugins (Kennung plugins): Ordner lesen, Siegel prüfen, Server-Teile laden und ihre Dienste anmelden - vor Build()
builder.Services.AddSingleton<DmOffice.Sdk.IUserDataAccessor, DmOffice.Plugins.UserDataAccessor>();
builder.Services.AddSingleton<DmOffice.Plugins.ServerDataStore>();
builder.Services.AddSingleton<DmOffice.Sdk.IServerDataAccessor>(sp => sp.GetRequiredService<DmOffice.Plugins.ServerDataStore>());
builder.Services.AddSingleton<DmOffice.Sdk.IPeople, DmOffice.Plugins.People>();
builder.Services.AddSingleton<DmOffice.Sdk.IMailOut, DmOffice.Plugins.MailOut>();
builder.Services.AddSingleton<DmOffice.Sdk.ICoreFeatures, DmOffice.Plugins.CoreFeatures>();
builder.Services.AddSingleton<DmOffice.Sdk.IPublicAddress, DmOffice.Plugins.PluginPublicAddress>();
DmOffice.Plugins.PluginHost.Discover(builder.Services);

var app = builder.Build();
Auth.Log = app.Logger;
Tresor.Paths = paths;
// Tresor-Datenbank im Cryptomator-Tresor: nach 30 Minuten Ruhe zurückschreiben (Prüfung jede Minute), beim Herunterfahren sofort
var tresorFlush = new System.Threading.Timer(_ => Tresor.FlushAll(force: false), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
app.Lifetime.ApplicationStopping.Register(() => { tresorFlush.Dispose(); Tresor.FlushAll(force: true); });
// Systemprotokoll: was noch im Puffer steht, in die Datei
app.Lifetime.ApplicationStopped.Register(DmOffice.Core.SystemLogFile.Stop);
// System-Tresor (Cryptomator) liegt in der Dateiablage der Person
{ var tresorSpaces = app.Services.GetRequiredService<Spaces>(); Tresor.UserDbOf = id => tresorSpaces.Get(id).Db; }
// Office im Tresor: Gespeichertes bei geschlossenem Tresor beim Öffnen übernehmen
DmOffice.Office.TresorOffice.Register();

// Demo-Server (play.dmoffice.de): leerer Server bekommt Verwaltung und Demo-Konto mit Beispieldaten
if (Demo.Enabled) Demo.Seed(app.Services, app.Logger);
// Noch kein Benutzer: Einmal-Code fuer die Ersteinrichtung ausgeben (verhindert, dass der erste Fremde Admin wird)
if (app.Services.GetRequiredService<UserStore>().Empty) Auth.EnsureSetupCode(app.Logger);
// Hauptschluessel liegt als Datei neben den Daten: dann schuetzt die Verschluesselung nur einzelne Datenbanken,
// nicht das ganze Volume. Deutlich ins Log schreiben.
// Zwischenspeicher-Betrieb: verdraengte Inhalte beim Oeffnen aus S3 holen
{
    var mirror = app.Services.GetRequiredService<S3Mirror>();
    BlobStore.Fetch = (blobs, id) => mirror.FetchOne(blobs, id);
    BlobStore.Written = mirror.Wrote;
    DmOffice.Gallery.GalleryFolders.Proxies = app.Services.GetRequiredService<DmOffice.Gallery.GalleryProxies>();
    // [feature:galmedium]
    // Mittlere Vorschauen (1280 px) der Galerie-Ordner ohne große Vorschauen: data/cache/gallery-medium
    DmOffice.Gallery.GalleryFolders.Medium = new DmOffice.Gallery.GalleryProxies(Path.Combine(app.Services.GetRequiredService<DataPaths>().Root, "cache", "gallery-medium"),
        app.Services.GetRequiredService<Vault>().Key);
    // [/feature:galmedium]
    // Bildangaben und kleine Vorschaubilder der Server-Ordner: einmal für alle
    DmOffice.Gallery.GalleryFolders.Shared = app.Services.GetRequiredService<DmOffice.Gallery.GalleryShared>();
    // Tags und Cover der Audiothek auf Server-Ordnern: einmal für alle
    DmOffice.Audio.AudioFolders.Shared = app.Services.GetRequiredService<DmOffice.Audio.AudioShared>();
    // Kleine Vorschaubilder der externen Ordner: eigener Zwischenspeicher unter data/cache/external
    // Virenprüfung (aus, bis der Admin sie unter Verwaltung → Sicherheit einschaltet)
    // Gemeinsames Junk-Wissen (Fingerabdrücke) für Spamfilter und Mailabruf
    DmOffice.Mail.JunkPrints.Shared = app.Services.GetRequiredService<DmOffice.Mail.JunkPrints>();
    DmOffice.Mail.SpamRules.Config = app.Services.GetRequiredService<ServerConfig>();
    DmOffice.Core.VirusScan.Config = app.Services.GetRequiredService<ServerConfig>();
    DmOffice.Core.VirusScan.DataRoot = app.Services.GetRequiredService<DataPaths>().Root;
    DmOffice.Core.VirusScan.Log = app.Logger;
    DmOffice.Storage.ExternalFolders.Thumbs = new DmOffice.Gallery.GalleryProxies(Path.Combine(app.Services.GetRequiredService<DataPaths>().Root, "cache", "external"),
        app.Services.GetRequiredService<Vault>().Key);
    DmOffice.Dav.FileStore.CacheRoom = mirror.CacheRoom;
    // Suche in Server-Mounts: Änderungen über dmOffice und geöffnete Ordner gleich nachziehen
    { var mountIndexer = app.Services.GetRequiredService<DmOffice.Storage.ExternalIndexer>();
      DmOffice.Storage.ExternalFolders.IndexChanged = mountIndexer.Changed;
      DmOffice.Storage.ExternalFolders.IndexOpened = mountIndexer.Opened; }
    // Eingebaute Photo-Engine: startet beim ersten Auftrag, in der Verwaltung abschaltbar
    { var serverConfig = app.Services.GetRequiredService<ServerConfig>();
      DmOffice.Dav.PhotoEngine.BuiltInOff = () => serverConfig.Current.PhotoEngineOff;
      DmOffice.Dav.PhotoEngine.Startup(app.Logger); }
    // Bearbeitete Fotos (Entwicklungseinstellungen): ihre Vorschaubilder unter data/cache/edited
    DmOffice.Dav.PhotoEdits.Cache = new DmOffice.Gallery.GalleryProxies(Path.Combine(app.Services.GetRequiredService<DataPaths>().Root, "cache", "edited"),
        app.Services.GetRequiredService<Vault>().Key);
    // Foto-Editor: Arbeitskopien der Originale aus Galerie-Ordnern und Schreiben im Hintergrund
    { var spaces = app.Services.GetRequiredService<Spaces>();
      DmOffice.Dav.PhotoWork.Init(app.Services.GetRequiredService<DataPaths>().Root, app.Services.GetRequiredService<Vault>().Key, app.Services.GetRequiredService<ServerConfig>(),
        id => { try { return spaces.Get(id); } catch (Exception) { return null; } }, app.Logger); }
}
// [feature:cfgseal]
if (configSeal?.State == ConfigSeal.State.SealedNow)
    app.Logger.LogWarning("{Files}: noch ohne Siegel ({Seal}) - jetzt so versiegelt, wie sie sind. Erwartet beim ersten Start dieser Version " +
        "und nach dem Zurücklegen einer älteren Sicherung; sonst Benutzerliste und Verwaltungsrechte prüfen.", string.Join(", ", configSeal.Files), ConfigSeal.FileName);
// [/feature:cfgseal]
if (restoredFiles > 0)
    app.Logger.LogWarning("Stand aus der S3-Sicherung eingespielt ({Count} Dateien) - die Dateiinhalte kommen jetzt im Hintergrund aus S3.", restoredFiles);
if (vault.Migrated > 0)
    app.Logger.LogInformation("Hauptschlüssel jetzt aus DMOFFICE_MASTER_KEY: {Count} Werte neu eingepackt. Die alte Schlüsseldatei bleibt liegen, bis sie in der Verwaltung gelöscht wird.", vault.Migrated);
if (vault.MigrationError != null)
    app.Logger.LogError("Umstieg auf DMOFFICE_MASTER_KEY unvollständig ({Error}) - die Schlüsseldatei bleibt liegen, beide Schlüssel gelten weiter.", vault.MigrationError);
if (Vault.LooksWeak(Environment.GetEnvironmentVariable("DMOFFICE_MASTER_KEY")))
    app.Logger.LogWarning("DMOFFICE_MASTER_KEY ist kurz oder einfach gebaut und mit einer Kopie des Datenverzeichnisses leichter zu erraten. " +
        "Für neue Installationen eine lange Zufallsfolge nehmen, z. B. aus „openssl rand -hex 32“. Bestehende Daten brauchen den bisherigen Wert.");
if (vault.KeyInDataDir)
    app.Logger.LogWarning("DMOFFICE_MASTER_KEY ist nicht gesetzt - der Hauptschlüssel liegt als Datei neben den Daten ({File}). " +
        "Wer das Datenverzeichnis oder eine Sicherung kopiert, kann damit alles entschlüsseln. Besser den Schlüssel als Umgebungsvariable setzen.", vault.KeyFile);

// Chat: frueher verlassene Unterhaltungen zu zweit wieder als ausgeblendet fuehren (einmalig)
{
    var users = app.Services.GetRequiredService<UserStore>();
    try { app.Services.GetRequiredService<DmOffice.Chat.ChatStore>().RestoreHidden(id => id == DmOffice.Chat.Pukki.Id || users.ById(id) != null); }
    catch (Exception ex) { app.Logger.LogWarning("Chat ausgeblendete Unterhaltungen: {Error}", ex.Message); }
}

// Datei-Speicher je Benutzer begrenzen (Verwaltung: Vorgabe und je Benutzer)
{
    var users = app.Services.GetRequiredService<UserStore>();
    var config = app.Services.GetRequiredService<ServerConfig>();
    DmOffice.Dav.FileStore.QuotaOf = id => config.FileQuotaBytes(users.ById(id));
    DmOffice.Dav.FileStore.FilesOffOf = id => users.ById(id)?.FilesOff == true;
    DmOffice.Dav.FileRetention.LimitsOf = () => config.Current.FileLimits;
}

// Microsoft tauscht das Refresh-Token beim Erneuern aus - das neue ins Konto schreiben
{
    var spaces = app.Services.GetRequiredService<Spaces>();
    microsoftOAuth.TokenRotated = (accountId, previous, fresh) =>
    {
        try { MicrosoftOAuth.Persist(spaces.All(), accountId, previous, fresh); }
        catch (Exception ex) { app.Logger.LogWarning("Microsoft-Token speichern: {Error}", ex.Message); }
    };
    microsoftOAuth.GraphTokenRotated = (accountId, previous, fresh) =>
    {
        try { MicrosoftOAuth.Persist(spaces.All(), accountId, previous, fresh, graph: true); }
        catch (Exception ex) { app.Logger.LogWarning("Microsoft-Token (Graph) speichern: {Error}", ex.Message); }
    };
    // OneDrive der Datensicherung: nur ersetzen, wenn dort noch das bisherige Token steht
    microsoftOAuth.FilesTokenRotated = (userId, previous, fresh) =>
    {
        try
        {
            var db = spaces.Get(userId).Db;
            var s = DmOffice.Storage.PlainBackup.Settings(db);
            if (s.OneDriveToken != previous) return;
            s.OneDriveToken = fresh;
            DmOffice.Storage.PlainBackup.Save(db, s);
        }
        catch (Exception ex) { app.Logger.LogWarning("Microsoft-Token (OneDrive) speichern: {Error}", ex.Message); }
    };
}

if (OperatingSystem.IsLinux() && DmOffice.Core.Features.On(DmOffice.Core.Features.ToolIsolation))
    app.Logger.Log(processSealed ? LogLevel.Information : LogLevel.Warning, processSealed
        ? "Speicher des Servers gegen gestartete Werkzeuge gesperrt."
        : "Speicher des Servers ließ sich nicht gegen gestartete Werkzeuge sperren.");
// [feature:proxyconfirm]
// Ohne DMOFFICE_TRUSTED_PROXIES: X-Forwarded-* nur von Proxys, die in der Verwaltung bestätigt (oder nach dem Update übernommen) sind
var proxyConfirm = DmOffice.Core.Features.On(DmOffice.Core.Features.ProxyConfirm) && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DMOFFICE_TRUSTED_PROXIES"));
if (proxyConfirm)
{
    var confirm = DmOffice.Core.ProxyConfirm.Shared = new DmOffice.Core.ProxyConfirm(Path.Combine(app.Services.GetRequiredService<DataPaths>().Root, "proxies.json"),
        DmOffice.Core.ProxyTrust.Networks(null, DmOffice.Core.Features.On(DmOffice.Core.Features.ProxyTrust)));
    var any = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost, ForwardLimit = 1 };
    any.KnownIPNetworks.Clear();
    any.KnownProxies.Clear();
    any.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("0.0.0.0/0"));
    any.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("::/0"));
    app.UseWhen(ctx => DmOffice.Core.ProxyConfirm.Forwarded(ctx.Request) && ctx.Connection.RemoteIpAddress is { } from
        && confirm.Trusted(from, ctx.Request.Headers["X-Forwarded-Host"].FirstOrDefault()), b => b.UseForwardedHeaders(any));
}
else app.UseForwardedHeaders();
// [feature:proxyconfirm:else]
// app.UseForwardedHeaders();
// [/feature:proxyconfirm]
// Eigene Rechnernamen öffentlicher Plugin-Seiten (etwa hilfe.beispiel.de → Website in einer Sprache): Weg vor dem Routing umschreiben
DmOffice.Plugins.PluginHost.UseHosts(app);
app.UseRouting();
DmOffice.Core.SystemLog.Use(app);
// X-Forwarded-For blieb stehen: der Absender gilt nicht als Proxy (DMOFFICE_TRUSTED_PROXIES passt nicht) - einmal je Adresse melden
var untrustedProxies = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>();
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Headers.ContainsKey("X-Forwarded-For") && ctx.Connection.RemoteIpAddress is { } from
        && untrustedProxies.Count < 50 && untrustedProxies.TryAdd(from.ToString(), 0))
        // [feature:proxyconfirm]
        if (proxyConfirm) app.Logger.LogWarning("X-Forwarded-For von {Address} nicht übernommen: kein bestätigter Proxy (Verwaltung → Sicherheit → Reverse-Proxys).", from);
        else
        // [/feature:proxyconfirm]
        app.Logger.LogWarning("X-Forwarded-For von {Address} nicht übernommen: die Adresse steht nicht in DMOFFICE_TRUSTED_PROXIES.", from);
    await next();
});
// Komprimieren nur für die großen Listen der Galerie, Dateiablage und Audiothek (keine Geheimnisse darin, nur Namen und Bildangaben)
// (Die Liste der Galerie-Ordner packt GalleryList selbst, einmal je Stand)
app.UseWhen(ctx => ctx.Request.Path.StartsWithSegments("/api/files/meta") || ctx.Request.Path.StartsWithSegments("/api/files/search") || ctx.Request.Path.StartsWithSegments("/api/gallery/timeline") || ctx.Request.Path.StartsWithSegments("/api/audio/tracks")
    || ctx.Request.Path.Equals("/api/files") || ctx.Request.Path.Equals("/api/files/"),
    b => b.UseResponseCompression());
// Zum Vergleich in der Handy-App (Koppeln ohne QR-Code): der Weg ueber das Server-Log fuehrt an keinem Proxy vorbei
app.Logger.LogInformation("Fingerabdruck des Serverschlüssels (verschlüsselter Umschlag der App): {Fingerprint}", app.Services.GetRequiredService<E2e.ServerKey>().Fingerprint);
// [feature:proxyconfirm]
if (proxyConfirm)
    app.Logger.LogInformation("Reverse-Proxys: X-Forwarded-*-Kopfzeilen gelten von dieser Maschine und von den Proxys, die unter Verwaltung → Sicherheit bestätigt sind.");
else
// [/feature:proxyconfirm]
if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DMOFFICE_TRUSTED_PROXIES")))
    app.Logger.LogWarning((DmOffice.Core.Features.On(DmOffice.Core.Features.ProxyTrust)
        ? "DMOFFICE_TRUSTED_PROXIES ist nicht gesetzt: X-Forwarded-*-Kopfzeilen gelten nur von dieser Maschine und aus den eigenen Netzen des Containers. "
        : "DMOFFICE_TRUSTED_PROXIES ist nicht gesetzt: X-Forwarded-*-Kopfzeilen gelten aus allen privaten Netzen. ") +
        "Hinter einem Reverse-Proxy dessen Adresse eintragen, z. B. DMOFFICE_TRUSTED_PROXIES=172.17.0.1/32.");
// Sicherheits-Kopfzeilen: fuer alle Antworten nosniff und Referrer-Policy; fuer HTML-Seiten zusaetzlich eine
// Anmeldung und Login-Flow (ohne Anmeldung erreichbar): kleine Anfragen - die 512 MB gelten für Uploads, nicht für ein Passwortfeld
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path;
    if ((path.StartsWithSegments("/api/auth") || path.StartsWithSegments("/index.php/login") || path.StartsWithSegments("/login") || path.StartsWithSegments("/p"))
        && ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
        size.MaxRequestBodySize = 64 * 1024;
    await next();
});

// Content-Security-Policy (nur eigene Skripte, kein Einbetten in fremde Seiten). Der Office-Server darf Skript
// (ONLYOFFICE api.js), Rahmen und Formularziel sein. Antworten mit eigener CSP (Downloads, DAV-Liste) bleiben.
app.Use(async (ctx, next) =>
{
    ctx.Response.OnStarting(() =>
    {
        var h = ctx.Response.Headers;
        h.XContentTypeOptions = "nosniff";
        // Über HTTPS (auch hinter dem Proxy, X-Forwarded-Proto): der Browser bleibt ein Jahr bei HTTPS. Ohne includeSubDomains -
        // andere Dienste unter derselben Domain könnten noch HTTP brauchen. Über HTTP (Heimnetz) nicht, sonst sperrte es aus.
        if (ctx.Request.IsHttps && !h.ContainsKey("Strict-Transport-Security")) h.StrictTransportSecurity = "max-age=31536000";
        if (!h.ContainsKey("Referrer-Policy")) h["Referrer-Policy"] = "same-origin";
        if (h.ContainsKey("Content-Security-Policy")) return Task.CompletedTask;
        var type = ctx.Response.ContentType ?? "";
        var html = type.StartsWith("text/html", StringComparison.OrdinalIgnoreCase);
        // Netz für die Schnittstelle: Antworten unter /api/ (außer HTML und PDF) als Dokument geöffnet ohne Skripte
        if (!html && ctx.Request.Path.StartsWithSegments("/api") && !type.StartsWith("application/pdf", StringComparison.OrdinalIgnoreCase))
        {
            h.ContentSecurityPolicy = DmOffice.Dav.FilesApi.SandboxCsp;
            return Task.CompletedTask;
        }
        if (!html) return Task.CompletedTask;
        var office = "";
        var officeUrl = ctx.RequestServices.GetService<ServerConfig>()?.Current.OfficeUrl;
        if (Uri.TryCreate(officeUrl, UriKind.Absolute, out var ou) && ou.Scheme is "http" or "https")
            office = " " + ou.GetLeftPart(UriPartial.Authority);
        h.ContentSecurityPolicy =
            "default-src 'self'; " +
            // Skripte des Office-Servers nur für die Docs-API (api.js); mit WOPI bleibt er in seinem Rahmen
            "script-src 'self'" + (ctx.RequestServices.GetService<DmOffice.Office.OfficeDiscovery>()?.NeedsScript == false ? "" : office) + "; " +
            "style-src 'self' 'unsafe-inline'; " +
            "img-src 'self' data: blob: https: http:; " +
            "media-src 'self' blob:; " +
            "font-src 'self' data:; " +
            "connect-src 'self'" + office + "; " +
            "frame-src 'self' blob:" + office + "; " +
            "worker-src 'self'; " +
            "form-action 'self'" + office + "; " +
            "frame-ancestors 'self'; base-uri 'self'; object-src 'none'";
        h.XFrameOptions = "SAMEORIGIN";
        return Task.CompletedTask;
    });
    await next();
});
// Web-Teile und Sprachdateien der Plugins (/plugins/<key>/web|lang/…)
DmOffice.Plugins.PluginApi.UseFiles(app);
app.UseDefaultFiles();
// Manifest mit protocol_handlers (mailto, webcal), solange die Kennung linkhandler an ist
DmOffice.Core.WebManifest.Use(app);
var contentTypes = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
contentTypes.Mappings[".webmanifest"] = "application/manifest+json";
// Vorkomprimierte Oberfläche (vite.config.ts legt .br/.gz daneben): nimmt der Browser br oder gzip an,
// wird die kleinere Datei ausgeliefert - Content-Type vom Original, Content-Encoding dazu.
var webFiles = app.Environment.WebRootFileProvider;
app.Use(async (ctx, next) =>
{
    var req = ctx.Request;
    if ((HttpMethods.IsGet(req.Method) || HttpMethods.IsHead(req.Method)) && req.Path.HasValue
        && !req.Path.StartsWithSegments("/api") && !req.Path.StartsWithSegments("/dav") && !req.Headers.ContainsKey("Range")
        && Path.GetExtension(req.Path.Value) is ".js" or ".css" or ".json" or ".svg" or ".html")
    {
        var accept = req.Headers.AcceptEncoding.ToString();
        var enc = accept.Contains("br") ? ".br" : accept.Contains("gzip") ? ".gz" : null;
        if (enc == ".br" && !webFiles.GetFileInfo(req.Path.Value + ".br").Exists) enc = accept.Contains("gzip") ? ".gz" : null;
        if (enc != null && webFiles.GetFileInfo(req.Path.Value + enc).Exists)
            req.Path = req.Path.Value + enc;
        // Auch die unkomprimierte Antwort hängt vom Accept-Encoding ab (für Zwischenspeicher)
        ctx.Response.Headers.Vary = "Accept-Encoding";
    }
    await next();
});
var staticTypes = new PrecompressedTypes(contentTypes);
app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = staticTypes,
    // Assets tragen einen Hash im Namen und aendern sich nie; index.html immer frisch, sonst zeigt ein Update auf alte Teilstuecke.
    // sw.js (Service Worker der installierbaren App) und das Manifest ebenso frisch - daran erkennt der Browser eine neue Version.
    OnPrepareResponse = ctx =>
    {
        var name = ctx.File.Name;
        if (name.EndsWith(".br") || name.EndsWith(".gz"))
        {
            ctx.Context.Response.Headers.ContentEncoding = name.EndsWith(".br") ? "br" : "gzip";
            name = name[..^3];
        }
        ctx.Context.Response.Headers.CacheControl =
            name.EndsWith(".html") || name is "sw.js" or "manifest.webmanifest" ? "no-cache"
            : ctx.Context.Request.Path.StartsWithSegments("/assets") ? "public, max-age=31536000, immutable" : "public, max-age=3600";
    }
});
DmOffice.Core.SlowRequests.Use(app);
app.UseAuthentication();
// CSRF von Nachbar-Seiten derselben Domain: aendernde Cookie-Anfragen nur von der eigenen Seite (Sec-Fetch-Site)
app.Use(async (ctx, next) =>
{
    if (Auth.CrossSiteCookieWrite(ctx)) { ctx.Response.StatusCode = 403; return; }
    await next();
});
// Security Center: Erlaubtes durch, Gesperrtes ab, Abtasten (unbekannter Pfad ohne Anmeldemerkmal, Scanner) sofort sperren.
// DMOFFICE_SECURITY=off schaltet es ab (Notbremse, falls eine Sperre die eigenen Zugänge trifft)
if (!string.Equals(Environment.GetEnvironmentVariable("DMOFFICE_SECURITY"), "off", StringComparison.OrdinalIgnoreCase))
{
    DmOffice.Core.SecurityCenter.Shared = app.Services.GetRequiredService<DmOffice.Core.SecurityCenter>();
    DmOffice.Core.SecurityCenter.Use(app);
}
else app.Logger.LogWarning("DMOFFICE_SECURITY=off: das Security Center sperrt nichts.");
// Ansteigende Bremse fuer nicht angemeldete Anfragen je Adresse (zwischen Anmeldung und Autorisierung, damit auch
// abgewiesene Anfragen zaehlen). DMOFFICE_ANON_LIMIT=off schaltet sie ab, eine Zahl setzt Anfragen je Minute.
if (AnonThrottle.FromEnvironment() is { } anonThrottle) AnonThrottle.Use(app, anonThrottle);
else app.Logger.LogWarning("DMOFFICE_ANON_LIMIT=off: Anfragen ohne Anmeldung werden nicht gebremst.");
// Release-Liste: Pfade nicht freigeschalteter Funktionen antworten mit 404, vor der Autorisierung auch ohne Anmeldung
DmOffice.Core.Features.Use(app);
app.UseAuthorization();
// Demo: Mailkonten, Versand, Freigabelinks, Einladungen, Passwort und Zwei-Faktor gesperrt
if (Demo.Enabled) app.Use(Demo.Guard);
// Geraete (App-Passwort) und Umschlag: tatsaechlichen Pfad noch einmal pruefen, /api/auth/* gesperrt
app.Use(async (ctx, next) =>
{
    if (!Auth.DeviceRequestAllowed(ctx)) { ctx.Response.StatusCode = 403; return; }
    await next();
});
// Notfallsitzung im Konto eines anderen: Sicherheit, Verwaltung und Notfallzugang bleiben gesperrt
DmOffice.Emergency.EmergencyApi.Use(app);
app.UseRateLimiter();
// [feature:gatekeeper]
// GateKeeper: Tunnel der App als WebSocket (/api/gatekeeper/tunnel)
app.UseWebSockets();
// [/feature:gatekeeper]
// PDF-Vorschau als Seitenbilder: Download-Adressen mit pdfpages / pdfpage
DmOffice.Dav.PdfPages.Use(app);

// Fehler aus IMAP/SMTP und Ablage als lesbare Meldung statt 500 ohne Text
app.Use(async (ctx, next) =>
{
    try { await next(); }
    // Die Gegenseite hat aufgegeben (App geschlossen, Warten auf neue Chat-Nachrichten abgebrochen): kein Fehler, nichts zu melden
    catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { }
    catch (Exception ex) when (!ctx.Response.HasStarted && ctx.Request.Path.StartsWithSegments("/api"))
    {
        var status = ex switch
        {
            BadHttpRequestException b => b.StatusCode,
            UpdatePendingException or BlobUnavailableException => 503,
            BlobMissingException => 404,
            TresorClosedException => StatusCodes.Status423Locked,
            DmOffice.VaultLockedException => StatusCodes.Status423Locked,
            KeyNotFoundException => 404,
            UnauthorizedAccessException or DmOffice.Dav.DavForbiddenException => 403,
            DmOffice.Dav.DavConflictException => 409,
            DmOffice.Dav.QuotaExceededException => 507,
            ArgumentException or InvalidOperationException or FormatException => 400,
            MailKit.Security.AuthenticationException => 502,
            MailKit.ServiceNotConnectedException or System.Net.Sockets.SocketException or IOException => 502,
            TimeoutException => 504,
            _ => 500
        };
        if (status == 500) app.Logger.LogError(ex, "Fehler bei {Path}", ctx.Request.Path);
        ctx.Response.StatusCode = status;
        // Bei Lesefehlern steht das Eigentliche innen (welches Feld, welche Zeile)
        var message = ex is BadHttpRequestException && ex.InnerException != null ? ex.Message + " " + ex.InnerException.Message : ex.Message;
        // Keine Interna nach aussen: unerwartete Fehler nur allgemein (Einzelheiten im Log), und Ein-/Ausgabefehler
        // nicht mit Dateipfaden des Servers
        if (status == 500) message = "Interner Fehler. Einzelheiten stehen im Server-Protokoll.";
        // Mail- und andere Serververbindungen: kein englischer Rohtext („Name or service not known“)
        else if (ex is System.Net.Sockets.SocketException or MailKit.ServiceNotConnectedException or TimeoutException)
            message = "Keine Verbindung zum Server.";
        // Ohne eigenen Text (auch: gerade von anderer Stelle gelöscht oder verschoben) nur das, was für die Person zählt
        else if (ex is KeyNotFoundException && (message == new KeyNotFoundException().Message || message.StartsWith("The given key", StringComparison.Ordinal)))
            message = "Das gibt es nicht mehr. Bitte die Ansicht neu laden.";
        else if (ex is IOException and not BlobUnavailableException && (message.Contains('/') || message.Contains('\\')))
        {
            app.Logger.LogWarning("Ein-/Ausgabefehler bei {Path}: {Error}", ctx.Request.Path, ex.Message);
            message = "Speicher- oder Verbindungsfehler.";
        }
        if (ex is UpdatePendingException pending) await ctx.Response.WriteAsJsonAsync(new { error = message, updatePending = true, reason = pending.Reason });
        else if (ex is TresorClosedException) await ctx.Response.WriteAsJsonAsync(new { error = message, locked = Tresor.Locked });
        else if (ex is DmOffice.VaultLockedException) await ctx.Response.WriteAsJsonAsync(new { error = message, locked = "vault" });
        else if (ex is DmOffice.Storage.SftpHostKeyException hk) await ctx.Response.WriteAsJsonAsync(new { error = message, sftpHost = new { hk.Info.Fingerprint, hk.Info.KeyType, hk.Info.Known, hk.Info.State } }); // [feature:sftptrust]
        else await ctx.Response.WriteAsJsonAsync(new { error = message });
    }
});

Auth.Map(app);
Oidc.Map(app);
DmOffice.Storage.SftpApi.Map(app);
DmOffice.Storage.SftpDriver.KnownHosts = Path.Combine(paths.Root, "sftp-known-hosts.json");
DmOffice.Storage.SftpDriver.Log = app.Logger;
// Kalender-Abos: Heimnetz wie bei eigenen WebDAV-Ordnern nur mit Erlaubnis der Verwaltung
{ var subConfig = app.Services.GetRequiredService<ServerConfig>(); DmOffice.Pim.Subscriptions.HomeNet = () => subConfig.Current.ExternalWebDavPrivate; }
TresorApi.Map(app);
TresorFiles.Map(app);
DmOffice.Notes.TresorNotizi.Register();
E2e.Map(app);
Push.Map(app);
DmOffice.Core.WebPush.Map(app);
AdminApi.Map(app);
DmOffice.Core.SecurityCenterApi.Map(app);
SettingsApi.Map(app);
ActivityApi.Map(app);
AppDownload.Map(app);
UpdateApi.Map(app);
MailApi.Map(app);
DmOffice.Mail.MailExpiryApi.Map(app);
DmOffice.Mail.MailSnippets.Map(app); // [feature:mailsnippets]
DmOffice.Mail.MailSignatures.Map(app); // [feature:mailsignatures]
PimApi.Map(app);
DmOffice.Pim.GraphApi.Map(app);
NotesApi.Map(app);
FilesApi.Map(app);
FileCategoriesApi.Map(app);
DmOffice.Dms.DmsApi.Map(app);
DmOffice.Mail.MailToDms.Map(app);
DmOffice.Storage.ExternalFolders.Map(app);
DmOffice.Storage.PCloudFolders.Map(app);
DmOffice.Storage.PlainBackupService.Map(app);
Tickets.Map(app);
PublicApi.Map(app);
// Bestätigung von Weiterleitungszielen (Kennung ruleforward): öffentliche Seite mit Knopf
DmOffice.Mail.ForwardTargets.Map(app);
DmOffice.Office.WopiApi.Map(app);
DmOffice.Office.OnlyOffice.Map(app);
DmOffice.Search.TextIndexApi.Map(app);
// Plugins: Liste, Verwaltung, Routen der geladenen Plugins (404, solange eines aus ist)
DmOffice.Plugins.PluginApi.Map(app);
DmOffice.Plugins.PluginHost.Map(app);
DmOffice.Chat.ChatApi.Map(app);
DmOffice.Chat.PukkiLearnApi.Map(app);
DmOffice.Emergency.EmergencyApi.Map(app);
// Link einer Person ohne Konto (/n/…): anfragen und Passwort festlegen ohne Anmeldung
DmOffice.Emergency.EmergencyPage.Map(app);
DmOffice.Gallery.GalleryApi.Map(app);
DmOffice.Gallery.LensProfiles.Map(app, paths); // [feature:lenscorr]
DmOffice.Gallery.ImportProfilesApi.Map(app);
DmOffice.Audio.AudioApi.Map(app);
DmOffice.Gallery.AlbumApi.Map(app);
DmOffice.Gallery.AlbumLinksApi.Map(app);
DmOffice.Dashboard.DashboardApi.Map(app);
DmOffice.Mail.SmartInboxApi.Map(app);
DmOffice.Mail.CatchallAliasesApi.Map(app);
DmOffice.Mail.OpenPgpApi.Map(app);
DmOffice.Notes.NoteSharesApi.Map(app);
// [feature:journal]
DmOffice.Journal.JournalApi.Map(app);
DmOffice.Journal.JournalAi.Map(app);
// [/feature:journal]
DmOffice.GateKeeper.GateKeeperApi.Map(app); // [feature:gatekeeper]
DavServer.Map(app);
NextcloudApi.Map(app);

app.MapGet("/api/health", () => Results.Ok(new { ok = true, version = DmOffice.SettingsApi.Version(), release = DmOffice.Core.Features.Shown, releaseExtra = DmOffice.Core.Features.ShownExtra }));

// Service Worker auch ohne Dateiendung: Reverse-Proxys mit Asset-Cache (Nginx Proxy Manager
// "Cache Assets") halten *.js trotz no-cache bis zu einem Tag fest - dann kaeme kein Update an.
app.MapGet("/sw", (IWebHostEnvironment env, HttpContext http) =>
{
    var file = Path.Combine(env.WebRootPath ?? "wwwroot", "sw.js");
    if (!File.Exists(file)) return Results.NotFound();
    http.Response.Headers.CacheControl = "no-cache, no-store";
    return Results.File(file, "text/javascript; charset=utf-8");
});

// Oberflaeche: jede unbekannte Adresse ausserhalb von /api und /dav ist eine Seite der App
app.MapFallback(async ctx =>
{
    // Fehlende Dateien der Oberflaeche nicht mit index.html beantworten - sonst haelt der Browser
    // HTML fuer den Service Worker, das Manifest oder ein Skript und meldet einen falschen MIME-Typ
    if (ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path.StartsWithSegments("/dav") || ctx.Request.Path.StartsWithSegments("/assets") || ctx.Request.Path.StartsWithSegments("/wopi") || ctx.Request.Path.StartsWithSegments("/onlyoffice")
        || NextcloudApi.Reserved(ctx.Request.Path)
        || ctx.Request.Path.Value is "/sw.js" or "/manifest.webmanifest")
    {
        ctx.Response.StatusCode = 404;
        return;
    }
    // Nur echte Seiten der Oberfläche (alles andere läuft über #/… hinter „/“) - ein unbekannter Pfad ist „gibt es nicht“,
    // nicht die Startseite mit 200 (so sieht ein Abtasten keine scheinbaren Treffer). Angemeldet: wie bisher
    if (!DmOffice.Core.SecurityCenter.Page(ctx.Request.Path) && ctx.User.Identity?.IsAuthenticated != true)
    {
        ctx.Response.StatusCode = 404;
        return;
    }
    var index = Path.Combine(app.Environment.WebRootPath ?? "wwwroot", "index.html");
    if (!File.Exists(index)) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsync("Oberfläche nicht gebaut (web/: npm run build)."); return; }
    ctx.Response.ContentType = "text/html; charset=utf-8";
    ctx.Response.Headers.CacheControl = "no-cache";
    // Oeffentlicher Link: das Token steht in der Adresse - nicht weitergeben, nicht in Suchmaschinen
    if (ctx.Request.Path.StartsWithSegments("/s") || ctx.Request.Path.StartsWithSegments("/a"))
    {
        ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
        ctx.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
    }
    await ctx.Response.SendFileAsync(index);
});

// Gastkonten frueherer Versionen einmalig entfernen - bevor die Hintergrunddienste loslaufen
DmOffice.Core.LegacyGuestCleanup.Run(app.Services, app.Logger);

app.Run();

// Fuer die Tests (WebApplicationFactory)
public partial class Program;

/// <summary>Content-Type einer vorkomprimierten Datei (x.js.br, x.js.gz) ist der des Originals (x.js).</summary>
sealed class PrecompressedTypes(Microsoft.AspNetCore.StaticFiles.IContentTypeProvider inner) : Microsoft.AspNetCore.StaticFiles.IContentTypeProvider
{
    public bool TryGetContentType(string subpath, out string contentType) =>
        inner.TryGetContentType(subpath.EndsWith(".br") || subpath.EndsWith(".gz") ? subpath[..^3] : subpath, out contentType!);
}
