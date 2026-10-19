using System.Security.Claims;
using DmOffice.Core;
using DmOffice.Mail;

namespace DmOffice;

public static class AdminApi
{
    public record NewUser(string Login, string DisplayName, string Password, bool IsAdmin);
    public record EditUser(string? DisplayName, string? Password, bool? IsAdmin, long? FileQuotaMb = null, bool DefaultFileQuota = false, bool? FilesOff = null);
    public record ServerBody(long DefaultFileQuotaMb);

    /// <summary>Belegter Datei-Speicher; null, wenn die Datenbank gerade nicht zu oeffnen ist.</summary>
    private static long? FilesUsed(Spaces spaces, string id)
    {
        try { return DmOffice.Dav.FileStore.Usage(spaces.Get(id).Db); }
        catch { return null; }
    }

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api/admin").RequireAuthorization("admin");

        api.MapPut("/settings", (ServerBody body, ServerConfig config) =>
        {
            config.Update(s => s.DefaultFileQuotaMb = Math.Max(0, body.DefaultFileQuotaMb));
            return Results.Ok();
        });

        api.MapGet("/status", (Vault vault, DataPaths paths, ServerConfig config) => new
        {
            defaultFileQuotaMb = config.Current.DefaultFileQuotaMb,
            keyInDataDir = vault.KeyInDataDir,
            dataDir = paths.Root,
            version = SettingsApi.Version()
        });

        api.MapGet("/users", (UserStore users, DataPaths paths, Spaces spaces, ServerConfig config) => users.All().Select(u => new
        {
            u.Id, u.Login, u.DisplayName, u.IsAdmin, u.Created, u.TwoFactor,
            size = File.Exists(paths.UserDb(u.Id)) ? new FileInfo(paths.UserDb(u.Id)).Length : 0,
            fileQuotaMb = u.FileQuotaMb,
            filesOff = u.FilesOff,
            fileQuota = config.FileQuotaBytes(u),
            files = FilesUsed(spaces, u.Id)
        }));

        api.MapPost("/users", (NewUser body, UserStore users) =>
        {
            var u = users.Create(body.Login, body.DisplayName, body.Password, body.IsAdmin);
            return Results.Ok(new { u.Id });
        });

        api.MapPut("/users/{id}", (string id, EditUser body, UserStore users, HttpContext http) =>
        {
            if (body.IsAdmin == false && id == http.User.FindFirstValue(ClaimTypes.NameIdentifier))
                return Results.BadRequest(new { error = "Sich selbst die Verwaltung entziehen geht nicht." });
            if (body.Password is { Length: > 0 and < 8 }) return Results.BadRequest(new { error = "Passwort: mindestens 8 Zeichen." });
            users.Update(id, u =>
            {
                if (!string.IsNullOrWhiteSpace(body.DisplayName)) u.DisplayName = body.DisplayName.Trim();
                if (!string.IsNullOrEmpty(body.Password)) { u.PasswordHash = Passwords.Hash(body.Password); u.NewStamp(); }
                if (body.IsAdmin is bool a && a != u.IsAdmin) { u.IsAdmin = a; u.NewStamp(); }
                if (body.DefaultFileQuota) u.FileQuotaMb = null;
                else if (body.FileQuotaMb is long q) u.FileQuotaMb = Math.Max(0, q);
                if (body.FilesOff is bool off) u.FilesOff = off;
            });
            return Results.Ok();
        });

        // Handy verloren und keine Wiederherstellungscodes: der Admin schaltet 2FA ab
        api.MapPost("/users/{id}/reset-2fa", (string id, UserStore users) =>
        {
            users.Update(id, u => { u.TotpSecret = null; u.TotpPending = null; u.RecoveryCodes.Clear(); u.NewStamp(); });
            return Results.Ok();
        });

        api.MapDelete("/users/{id}", (string id, UserStore users, Spaces spaces, DataPaths paths, MailSync sync, DmOffice.Pim.ShareStore shares, HttpContext http) =>
        {
            if (id == http.User.FindFirstValue(ClaimTypes.NameIdentifier))
                return Results.BadRequest(new { error = "Das eigene Konto lässt sich hier nicht löschen." });
            shares.Forget(id);
            sync.Forget(id);
            spaces.Forget(id);
            users.Delete(id);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var f in new[] { "", "-wal", "-shm" })
                if (File.Exists(paths.UserDb(id) + f)) File.Delete(paths.UserDb(id) + f);
            return Results.Ok();
        });
    }
}
