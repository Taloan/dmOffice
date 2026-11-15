using DmOffice.Core;

namespace DmOffice.Mail;

/// <summary>Catchall-Aliase (CatchallAliases): Einstellungen, Liste, anlegen und übernehmen, Übersicht je Alias, sperren und entsperren.
/// Die Adresse steht im Body oder in der Abfrage, nicht im Pfad (dort käme sie den /{acc}/…-Routen in die Quere).</summary>
public static class CatchallAliasesApi
{
    public record CreateBody(string? Name, string? Domain, string? Note);
    /// <param name="Block">true: sperren (BlockAs, sonst wie eingestellt), false: entsperren, null: unverändert</param>
    /// <param name="ExpireKind">Entfernen nach Zeit: „trash“, „destroy“ oder „“ (aus); null: unverändert</param>
    public record UpdateBody(string Address, string? Note, bool? Block, string? BlockAs, string? ExpireKind = null, int? ExpireDays = null);

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api/mail/aliases").RequireAuthorization();

        api.MapGet("", (HttpContext http) => Results.Ok(State(http.Space())));

        api.MapPut("/config", (CatchallAliases.Config body, HttpContext http) =>
        {
            var space = http.Space();
            try { CatchallAliases.Save(space.Db, body, space.Settings.MailAccounts); }
            catch (ArgumentException e) { return Results.BadRequest(new { error = e.Message }); }
            return Results.Ok(State(space));
        });

        api.MapGet("/suggest", () => Results.Ok(new { name = CatchallAliases.RandomName() }));

        // Neuer Alias - oder eine unbekannte Adresse übernehmen: Ordner anlegen, ihre Mails aus Posteingang und Domain-Ordner dorthin
        api.MapPost("", async (CreateBody body, HttpContext http, ImapPool pool, MailSync sync, ILoggerFactory logs, CancellationToken ct) =>
        {
            var space = http.Space();
            var config = CatchallAliases.Load(space.Db);
            CatchallAliases.Alias a;
            try { a = CatchallAliases.Create(space.Db, config, body.Name, body.Domain, body.Note); }
            catch (ArgumentException e) { return Results.BadRequest(new { error = e.Message }); }
            int moved = 0;
            if (space.Settings.MailAccounts.FirstOrDefault(x => x.Id == config.Account) is { } account)
                try { moved = await CatchallFolders.CollectAsync(space, account, a.Address, pool, sync, ct); }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // Der Alias gilt trotzdem; der Ordner kommt mit der nächsten Mail
                    logs.CreateLogger("DmOffice.Mail.CatchallAliases").LogWarning("Ordner für {Alias}: {Error}", a.Address, e.Message);
                }
            return Results.Ok(Item(CatchallAliases.Get(space.Db, a.Address) ?? a, moved));
        });

        api.MapGet("/item", (string address, HttpContext http) =>
        {
            var db = http.Space().Db;
            var a = CatchallAliases.Get(db, address) ?? throw new KeyNotFoundException("Diesen Alias gibt es nicht.");
            return Results.Ok(new { alias = Item(a), senders = CatchallAliases.TopSenders(db, a.Address) });
        });

        api.MapPut("/item", (UpdateBody body, HttpContext http, MailSync sync) =>
        {
            var space = http.Space();
            var blockAs = body.Block == true ? body.BlockAs is "junk" or "delete" ? body.BlockAs : CatchallAliases.Load(space.Db).BlockAs : null;
            var a = CatchallAliases.Update(space.Db, body.Address, body.Note, blockAs, body.Block != null);
            if (body.ExpireKind != null)
                try { a = CatchallAliases.SetExpiry(space.Db, a.Address, body.ExpireKind, body.ExpireDays); }
                catch (ArgumentException e) { return Results.BadRequest(new { error = e.Message }); }
            sync.Touch(space.User.Id);
            return Results.Ok(Item(a));
        });

        // Löschen: Mails aus dem Ordner des Alias in den Ordner der Domain, Ordner des Alias weg, dann Eintrag und Übersicht.
        // Klappt das Verschieben nicht, bleibt der Alias stehen - sonst wären die Mails im Ordner nicht mehr zu sehen.
        api.MapDelete("/item", async (string address, HttpContext http, ImapPool pool, MailSync sync, CancellationToken ct) =>
        {
            var space = http.Space();
            if (CatchallAliases.Get(space.Db, address) is not { } a) return Results.NotFound();
            int moved = 0;
            if (space.Settings.MailAccounts.FirstOrDefault(x => x.Id == CatchallAliases.Load(space.Db).Account) is { } account)
                try { moved = await CatchallFolders.RemoveAsync(space, account, a.Address, pool, sync, ct); }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    return Results.BadRequest(new { error = $"Der Ordner von {a.Address} ließ sich nicht auflösen: {e.Message}" });
                }
            CatchallAliases.Delete(space.Db, a.Address);
            sync.Touch(space.User.Id);
            return Results.Ok(new { moved });
        });
    }

    private static object Item(CatchallAliases.Alias a, int? moved = null) => new
    {
        address = a.Address, note = a.Note, created = a.Created, blocked = a.Blocked, auto = a.Auto, count = a.Count, last = a.Last,
        folder = a.Folder, moved, expireKind = a.ExpireKind, expireDays = a.ExpireDays,
    };

    private static object State(UserSpace space)
    {
        var db = space.Db;
        var c = CatchallAliases.Load(db);
        return new
        {
            config = c,
            accounts = space.Settings.MailAccounts.Select(a => new { id = a.Id, name = a.Name, email = a.Email }),
            // Für die Seitenleiste: je Domain ihr IMAP-Ordner (Mails an unbekannte Adressen)
            domains = c.Domains.Select(d => new { domain = d, folder = CatchallAliases.DomainFolder(db, d) }),
            items = CatchallAliases.List(db).Select(a => Item(a)),
            unknown = CatchallAliases.Unknowns(db).Select(u => new { address = u.Address, count = u.Count, last = u.Last }),
        };
    }
}
