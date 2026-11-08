using System.Text;
using DmOffice.Core;

namespace DmOffice.Mail;

/// <summary>OpenPGP (docs/openpgp-konzept.md): Signatur einer Mail, eigene Schlüssel je Konto, bekannte Schlüssel anderer,
/// Einstellungen. Unter /api/mail/pgp/…, die Signatur einer Mail unter /api/mail/{acc}/signature.</summary>
public static class OpenPgpApi
{
    public record AccountBody(string Id, bool? Sign, bool? AttachKey, bool? Autocrypt);
    public record SettingsBody(bool? Wkd, List<AccountBody>? Accounts);
    public record ImportBody(string Key, string? Passphrase);
    public record KeyBody(string Key);

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api/mail").RequireAuthorization();

        // Signatur prüfen; Schlüssel aus der Mail übernehmen und - eingeschaltet, nicht bei Junk - unbekannte per WKD holen
        api.MapGet("/{acc}/signature", async (string acc, string folder, uint uid, bool? lookup, HttpContext http, ImapPool pool, CancellationToken ct) =>
        {
            var space = http.Space();
            var account = space.Account(acc);
            var row = MailStore.Row(space.Db, acc, folder, uid) ?? throw new KeyNotFoundException("Mail nicht gefunden.");
            var message = await MailApi.Load(space, account, folder, uid, pool, ct);
            var special = MailStore.Folder(space.Db, acc, folder)?.Special;
            var junk = special == "junk" || row.Spam >= space.Settings.Spam.SpamCutoff;
            var added = !junk && OpenPgpMail.Harvest(space.Db, message);
            var status = OpenPgpMail.Verify(space.Db, message);
            var tried = false;
            var from = message.From.Mailboxes.FirstOrDefault()?.Address?.Trim().ToLowerInvariant();
            // WKD fragt den Mailanbieter des Absenders - das verrät ihm, dass die Mail geöffnet wurde. Daher nur für
            // signierte Mails mit unbekanntem Schlüssel, nie bei Junk, höchstens einmal am Tag je Adresse (außer von Hand)
            if (status?.State == "unknown" && !junk && !string.IsNullOrEmpty(from)
                && (lookup == true || (OpenPgpKeys.GetSettings(space.Db).Wkd && OpenPgpKeys.WkdDue(space.Db, from))))
            {
                tried = true;
                var rings = await OpenPgpWkd.FetchAsync(from, ct);
                foreach (var r in rings) added |= OpenPgpKeys.Store(space.Db, r, OpenPgpKeys.Wkd);
                OpenPgpKeys.WkdChecked(space.Db, from, rings.Count > 0);
                if (rings.Count > 0) status = OpenPgpMail.Verify(space.Db, message);
            }
            return Results.Ok(new { signature = status is null ? null : status with { KeyAdded = added, WkdTried = tried }, keyAdded = added });
        });

        api.MapGet("/pgp/settings", (HttpContext http) => Results.Ok(State(http.Space())));

        api.MapPut("/pgp/settings", (SettingsBody body, HttpContext http) =>
        {
            var space = http.Space();
            var s = OpenPgpKeys.GetSettings(space.Db);
            if (body.Wkd is bool w) s.Wkd = w;
            var ids = space.Settings.MailAccounts.Select(a => a.Id).ToHashSet();
            foreach (var a in body.Accounts ?? [])
            {
                if (!ids.Contains(a.Id)) continue;
                if (!s.Accounts.TryGetValue(a.Id, out var o)) s.Accounts[a.Id] = o = new OpenPgpKeys.AccountSettings();
                if (a.Sign is bool sign) o.Sign = sign;
                if (a.AttachKey is bool k) o.AttachKey = k;
                if (a.Autocrypt is bool c) o.Autocrypt = c;
            }
            OpenPgpKeys.SaveSettings(space.Db, s);
            return Results.Ok(State(space));
        });

        // Eigener Schlüssel je Konto: erzeugen, importieren, entfernen, öffentlichen Teil exportieren
        api.MapPost("/pgp/{acc}/generate", (string acc, HttpContext http) =>
        {
            var space = http.Space();
            var account = space.Account(acc);
            if (string.IsNullOrWhiteSpace(account.Email)) return Results.BadRequest(new { error = "Das Konto hat keine Mailadresse." });
            OpenPgpKeys.Generate(space.Db, account.Id, account.DisplayName, account.Email);
            return Results.Ok(State(space));
        });

        api.MapPost("/pgp/{acc}/import", (string acc, ImportBody body, HttpContext http) =>
        {
            var space = http.Space();
            var account = space.Account(acc);
            var (_, warning) = OpenPgpKeys.ImportOwn(space.Db, account.Id, account.Email, Encoding.UTF8.GetBytes(body.Key ?? ""), body.Passphrase);
            return Results.Ok(new { state = State(space), warning });
        });

        api.MapDelete("/pgp/{acc}/key", (string acc, HttpContext http) =>
        {
            var space = http.Space();
            OpenPgpKeys.RemoveOwn(space.Db, space.Account(acc).Id);
            return Results.Ok(State(space));
        });

        api.MapGet("/pgp/{acc}/public", (string acc, HttpContext http) =>
        {
            var space = http.Space();
            var account = space.Account(acc);
            var own = OpenPgpKeys.GetOwn(space.Db, account.Id) ?? throw new KeyNotFoundException("Für dieses Konto gibt es keinen Schlüssel.");
            var name = $"{account.Email.Replace('@', '_')}-0x{own.Fingerprint[^16..]}.asc";
            return Results.File(Encoding.ASCII.GetBytes(OpenPgpKeys.Armor(own.Public)), "application/pgp-keys", name);
        });

        // Bekannte Schlüssel anderer
        api.MapGet("/pgp/keys", (HttpContext http) => Results.Ok(OpenPgpKeys.List(http.Space().Db)));

        api.MapPost("/pgp/keys", (KeyBody body, HttpContext http) =>
            Results.Ok(new { imported = OpenPgpKeys.ImportPublic(http.Space().Db, Encoding.UTF8.GetBytes(body.Key ?? "")) }));

        api.MapPost("/pgp/keys/{fingerprint}/confirm", (string fingerprint, HttpContext http) =>
        {
            OpenPgpKeys.Confirm(http.Space().Db, fingerprint);
            return Results.Ok();
        });

        api.MapDelete("/pgp/keys/{fingerprint}", (string fingerprint, HttpContext http) =>
        {
            OpenPgpKeys.Delete(http.Space().Db, fingerprint);
            return Results.Ok();
        });

        api.MapGet("/pgp/keys/{fingerprint}/export", (string fingerprint, HttpContext http) =>
        {
            var key = OpenPgpKeys.ByFingerprint(http.Space().Db, fingerprint) ?? throw new KeyNotFoundException("Diesen Schlüssel gibt es nicht.");
            return Results.File(Encoding.ASCII.GetBytes(OpenPgpKeys.Armor(key.Ring)), "application/pgp-keys", $"0x{fingerprint.ToUpperInvariant()[^16..]}.asc");
        });
    }

    private static object State(UserSpace space)
    {
        var s = OpenPgpKeys.GetSettings(space.Db);
        return new
        {
            wkd = s.Wkd,
            accounts = space.Settings.MailAccounts.Select(a =>
            {
                var o = s.Accounts.TryGetValue(a.Id, out var x) ? x : new OpenPgpKeys.AccountSettings();
                var own = OpenPgpKeys.GetOwn(space.Db, a.Id);
                var info = own == null ? null : OpenPgpKeys.Info(own.Public, OpenPgpKeys.Own, true, own.Created);
                return new
                {
                    id = a.Id, name = a.Name, email = a.Email,
                    key = info == null ? null : new
                    {
                        fingerprint = info.Fingerprint, userIds = info.UserIds, algorithm = info.Algorithm, created = info.Created,
                        expires = info.Expires, matches = info.Emails.Contains(a.Email.Trim().ToLowerInvariant()),
                    },
                    sign = o.Sign && own != null, attachKey = o.AttachKey, autocrypt = o.Autocrypt,
                };
            }),
        };
    }
}
