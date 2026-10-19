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
var paths = new DataPaths(dataDir);
var vault = new Vault(paths, Environment.GetEnvironmentVariable("DMOFFICE_MASTER_KEY"));

builder.Services.AddSingleton(paths);
builder.Services.AddSingleton(vault);
builder.Services.AddSingleton<UserStore>();
builder.Services.AddSingleton<ServerConfig>();
builder.Services.AddSingleton<DmOffice.Pim.ShareStore>();
builder.Services.AddSingleton<E2e.ServerKey>();
builder.Services.AddSingleton<Fcm>();
builder.Services.AddHostedService<Push.Watcher>();
builder.Services.AddHttpClient("fcm");
// Newsletter abmelden (One-Click): keine Umleitungen - sie koennten ins eigene Netz fuehren
builder.Services.AddHttpClient("unsub").ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
builder.Services.AddHttpClient("discover").ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
builder.Services.AddHttpClient("e2e").ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
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
builder.Services.AddMemoryCache(o => o.SizeLimit = 300);

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(paths.Keys))
    .SetApplicationName("dmOffice");

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
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

var app = builder.Build();

// Datei-Speicher je Benutzer begrenzen (Verwaltung: Vorgabe und je Benutzer)
{
    var users = app.Services.GetRequiredService<UserStore>();
    var config = app.Services.GetRequiredService<ServerConfig>();
    DmOffice.Dav.FileStore.QuotaOf = id => config.FileQuotaBytes(users.ById(id));
    DmOffice.Dav.FileStore.FilesOffOf = id => users.ById(id)?.FilesOff == true;
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
app.UseAuthorization();

// Fehler aus IMAP/SMTP und Ablage als lesbare Meldung statt 500 ohne Text
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (Exception ex) when (!ctx.Response.HasStarted && ctx.Request.Path.StartsWithSegments("/api"))
    {
        var status = ex switch
        {
            BadHttpRequestException b => b.StatusCode,
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
        await ctx.Response.WriteAsJsonAsync(new { error = message });
    }
});

Auth.Map(app);
E2e.Map(app);
Push.Map(app);
AdminApi.Map(app);
SettingsApi.Map(app);
MailApi.Map(app);
PimApi.Map(app);
NotesApi.Map(app);
FilesApi.Map(app);
DavServer.Map(app);

app.MapGet("/api/health", () => Results.Ok(new { ok = true, version = DmOffice.SettingsApi.Version() }));

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
    if (ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path.StartsWithSegments("/dav") || ctx.Request.Path.StartsWithSegments("/assets")
        || ctx.Request.Path.Value is "/sw.js" or "/manifest.webmanifest")
    {
        ctx.Response.StatusCode = 404;
        return;
    }
    var index = Path.Combine(app.Environment.WebRootPath ?? "wwwroot", "index.html");
    if (!File.Exists(index)) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsync("Oberfläche nicht gebaut (web/: npm run build)."); return; }
    ctx.Response.ContentType = "text/html; charset=utf-8";
    ctx.Response.Headers.CacheControl = "no-cache";
    await ctx.Response.SendFileAsync(index);
});

app.Run();
