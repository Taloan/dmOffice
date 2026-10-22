using DmOffice;
using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Mail;
using DmOffice.Notes;
using DmOffice.Pim;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

var dataDir = Environment.GetEnvironmentVariable("DMOFFICE_DATA")
              ?? (OperatingSystem.IsWindows() ? Path.Combine(AppContext.BaseDirectory, "data") : "/data");
var paths = new DataPaths(dataDir, Environment.GetEnvironmentVariable("DMOFFICE_BLOBS"));
var vault = new Vault(paths, Environment.GetEnvironmentVariable("DMOFFICE_MASTER_KEY"));

builder.Services.AddSingleton(paths);
builder.Services.AddSingleton(vault);
builder.Services.AddSingleton<UserStore>();
builder.Services.AddSingleton<ServerConfig>();
builder.Services.AddSingleton<UpdateCenter>();
// Datenaenderungen beim Start (Platzpruefung, Sicherung, Freigabe) und alte Sicherungen wegraeumen
builder.Services.AddHostedService<UpdateRunner>();
builder.Services.AddSingleton<DmOffice.Pim.ShareStore>();
builder.Services.AddSingleton<LinkIndex>();
builder.Services.AddSingleton<FolderShareStore>();
builder.Services.AddHostedService<FilesCleanup>();
// Alte Dateiinhalte aus den Datenbanken in den BlobStore (einmalig, unterbrechbar)
builder.Services.AddHostedService<BlobMigration>();
builder.Services.AddHostedService<FileHashJob>();
builder.Services.AddSingleton<E2e.ServerKey>();
builder.Services.AddSingleton<Fcm>();
builder.Services.AddHostedService<Push.Watcher>();
builder.Services.AddHttpClient("fcm");
// Newsletter abmelden (One-Click): keine Umleitungen - sie koennten ins eigene Netz fuehren
builder.Services.AddHttpClient("unsub").ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
builder.Services.AddHttpClient("discover").ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
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
builder.Services.AddSingleton<MailSync>();
builder.Services.AddSingleton<DmOffice.Mail.MailOutbox>();
builder.Services.AddHostedService<DmOffice.Mail.MailQueueSender>();
builder.Services.AddHostedService<DmOffice.Mail.MailCleanup>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MailSync>());
builder.Services.AddSingleton<NotesSync>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<NotesSync>());
builder.Services.AddHostedService<DmOffice.Pim.SubscriptionSync>();
// Volltextsuche in Anhaengen und Dateien (Search/): Warteschlange im Hintergrund
builder.Services.AddSingleton<DmOffice.Search.TextIndexer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DmOffice.Search.TextIndexer>());
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

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "dmoffice";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
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
    var trusted = Environment.GetEnvironmentVariable("DMOFFICE_TRUSTED_PROXIES");
    var nets = string.IsNullOrWhiteSpace(trusted)
        ? new[] { "127.0.0.0/8", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "::1/128", "fc00::/7", "fe80::/10" }
        : trusted.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    foreach (var n in nets) o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(n));
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
});
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

var app = builder.Build();
Auth.Log = app.Logger;

// Noch kein Benutzer: Einmal-Code fuer die Ersteinrichtung ausgeben (verhindert, dass der erste Fremde Admin wird)
if (app.Services.GetRequiredService<UserStore>().Empty) Auth.EnsureSetupCode(app.Logger);
// Hauptschluessel liegt als Datei neben den Daten: dann schuetzt die Verschluesselung nur einzelne Datenbanken,
// nicht das ganze Volume. Deutlich ins Log schreiben.
if (vault.KeyInDataDir)
    app.Logger.LogWarning("DMOFFICE_MASTER_KEY ist nicht gesetzt - der Hauptschlüssel liegt als Datei neben den Daten ({File}). " +
        "Wer das Datenverzeichnis oder eine Sicherung kopiert, kann damit alles entschlüsseln. Besser den Schlüssel als Umgebungsvariable setzen.", vault.KeyFile);

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
}

app.UseForwardedHeaders();
// Zum Vergleich in der Handy-App (Koppeln ohne QR-Code): der Weg ueber das Server-Log fuehrt an keinem Proxy vorbei
app.Logger.LogInformation("Fingerabdruck des Serverschlüssels (verschlüsselter Umschlag der App): {Fingerprint}", app.Services.GetRequiredService<E2e.ServerKey>().Fingerprint);
if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DMOFFICE_TRUSTED_PROXIES")))
    app.Logger.LogWarning("DMOFFICE_TRUSTED_PROXIES ist nicht gesetzt: X-Forwarded-*-Kopfzeilen gelten aus allen privaten Netzen. " +
        "Hinter einem Reverse-Proxy dessen Adresse eintragen, z. B. DMOFFICE_TRUSTED_PROXIES=172.17.0.1/32.");
// Sicherheits-Kopfzeilen: fuer alle Antworten nosniff und Referrer-Policy; fuer HTML-Seiten zusaetzlich eine
// Content-Security-Policy (nur eigene Skripte, kein Einbetten in fremde Seiten). Der Office-Server darf Skript
// (ONLYOFFICE api.js), Rahmen und Formularziel sein. Antworten mit eigener CSP (Downloads, DAV-Liste) bleiben.
app.Use(async (ctx, next) =>
{
    ctx.Response.OnStarting(() =>
    {
        var h = ctx.Response.Headers;
        h.XContentTypeOptions = "nosniff";
        if (!h.ContainsKey("Referrer-Policy")) h["Referrer-Policy"] = "same-origin";
        if (h.ContainsKey("Content-Security-Policy")) return Task.CompletedTask;
        if (ctx.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) != true) return Task.CompletedTask;
        var office = "";
        var officeUrl = ctx.RequestServices.GetService<ServerConfig>()?.Current.OfficeUrl;
        if (Uri.TryCreate(officeUrl, UriKind.Absolute, out var ou) && ou.Scheme is "http" or "https")
            office = " " + ou.GetLeftPart(UriPartial.Authority);
        h.ContentSecurityPolicy =
            "default-src 'self'; " +
            "script-src 'self'" + office + "; " +
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
app.UseDefaultFiles();
var contentTypes = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
contentTypes.Mappings[".webmanifest"] = "application/manifest+json";
app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = contentTypes,
    // Assets tragen einen Hash im Namen und aendern sich nie; index.html immer frisch, sonst zeigt ein Update auf alte Teilstuecke.
    // sw.js (Service Worker der installierbaren App) und das Manifest ebenso frisch - daran erkennt der Browser eine neue Version.
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl =
        ctx.File.Name.EndsWith(".html") || ctx.File.Name is "sw.js" or "manifest.webmanifest" ? "no-cache"
        : ctx.Context.Request.Path.StartsWithSegments("/assets") ? "public, max-age=31536000, immutable" : "public, max-age=3600"
});
app.UseAuthentication();
// CSRF von Nachbar-Seiten derselben Domain: aendernde Cookie-Anfragen nur von der eigenen Seite (Sec-Fetch-Site)
app.Use(async (ctx, next) =>
{
    if (Auth.CrossSiteCookieWrite(ctx)) { ctx.Response.StatusCode = 403; return; }
    await next();
});
// Ansteigende Bremse fuer nicht angemeldete Anfragen je Adresse (zwischen Anmeldung und Autorisierung, damit auch
// abgewiesene Anfragen zaehlen). DMOFFICE_ANON_LIMIT=off schaltet sie ab, eine Zahl setzt Anfragen je Minute.
if (AnonThrottle.FromEnvironment() is { } anonThrottle) AnonThrottle.Use(app, anonThrottle);
else app.Logger.LogWarning("DMOFFICE_ANON_LIMIT=off: Anfragen ohne Anmeldung werden nicht gebremst.");
app.UseAuthorization();
// Geraete (App-Passwort) und Umschlag: tatsaechlichen Pfad noch einmal pruefen, /api/auth/* gesperrt
app.Use(async (ctx, next) =>
{
    if (!Auth.DeviceRequestAllowed(ctx)) { ctx.Response.StatusCode = 403; return; }
    await next();
});
app.UseRateLimiter();

// Fehler aus IMAP/SMTP und Ablage als lesbare Meldung statt 500 ohne Text
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (Exception ex) when (!ctx.Response.HasStarted && ctx.Request.Path.StartsWithSegments("/api"))
    {
        var status = ex switch
        {
            BadHttpRequestException b => b.StatusCode,
            UpdatePendingException => 503,
            KeyNotFoundException => 404,
            UnauthorizedAccessException => 403,
            ArgumentException or InvalidOperationException or FormatException => 400,
            MailKit.Security.AuthenticationException => 502,
            MailKit.ServiceNotConnectedException or System.Net.Sockets.SocketException or IOException => 502,
            _ => 500
        };
        if (status == 500) app.Logger.LogError(ex, "Fehler bei {Path}", ctx.Request.Path);
        ctx.Response.StatusCode = status;
        // Bei Lesefehlern steht das Eigentliche innen (welches Feld, welche Zeile)
        var message = ex is BadHttpRequestException && ex.InnerException != null ? ex.Message + " " + ex.InnerException.Message : ex.Message;
        // Keine Interna nach aussen: unerwartete Fehler nur allgemein (Einzelheiten im Log), und Ein-/Ausgabefehler
        // nicht mit Dateipfaden des Servers
        if (status == 500) message = "Interner Fehler. Einzelheiten stehen im Server-Protokoll.";
        else if (ex is IOException && (message.Contains('/') || message.Contains('\\')))
        {
            app.Logger.LogWarning("Ein-/Ausgabefehler bei {Path}: {Error}", ctx.Request.Path, ex.Message);
            message = "Speicher- oder Verbindungsfehler.";
        }
        if (ex is UpdatePendingException pending) await ctx.Response.WriteAsJsonAsync(new { error = message, updatePending = true, reason = pending.Reason });
        else await ctx.Response.WriteAsJsonAsync(new { error = message });
    }
});

Auth.Map(app);
E2e.Map(app);
Push.Map(app);
AdminApi.Map(app);
SettingsApi.Map(app);
ActivityApi.Map(app);
UpdateApi.Map(app);
MailApi.Map(app);
PimApi.Map(app);
NotesApi.Map(app);
FilesApi.Map(app);
Tickets.Map(app);
PublicApi.Map(app);
DmOffice.Office.WopiApi.Map(app);
DmOffice.Office.OnlyOffice.Map(app);
DmOffice.Search.TextIndexApi.Map(app);
DavServer.Map(app);
NextcloudApi.Map(app);

app.MapGet("/api/health", () => Results.Ok(new { ok = true, version = DmOffice.SettingsApi.Version(), release = DmOffice.SettingsApi.Release() }));

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
    var index = Path.Combine(app.Environment.WebRootPath ?? "wwwroot", "index.html");
    if (!File.Exists(index)) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsync("Oberfläche nicht gebaut (web/: npm run build)."); return; }
    ctx.Response.ContentType = "text/html; charset=utf-8";
    ctx.Response.Headers.CacheControl = "no-cache";
    // Oeffentlicher Link: das Token steht in der Adresse - nicht weitergeben, nicht in Suchmaschinen
    if (ctx.Request.Path.StartsWithSegments("/s"))
    {
        ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
        ctx.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
    }
    await ctx.Response.SendFileAsync(index);
});

app.Run();

// Fuer die Tests (WebApplicationFactory)
public partial class Program;
