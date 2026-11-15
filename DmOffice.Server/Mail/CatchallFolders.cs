using DmOffice.Core;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;

namespace DmOffice.Mail;

/// <summary>
/// Echte IMAP-Ordner für Catchall-Aliase im Postfach der Domain - so sehen andere Mailprogramme dieselbe Ordnung: ein Ordner je
/// Domain („example.com“, dort landen Mails an unbekannte Adressen) und darunter einer je angelegtem Alias („example.com/shop“).
/// Unbekannte bekommen keinen eigenen Ordner, sonst legte Spam an erfundene Adressen Ordner an. Das Trennzeichen des Servers und
/// die IMAP-Platzhalter (* und %) kommen in Namen nicht vor. Die Pfade merkt CatchallAliases (mail_alias.folder, kv).
/// </summary>
public static class CatchallFolders
{
    /// <summary>Ordnername ohne Trennzeichen des Servers und ohne IMAP-Platzhalter („example.com“ bei Trennzeichen „.“ → „example_com“).</summary>
    public static string Safe(string name, char delimiter) =>
        new(name.Select(ch => ch == delimiter || ch is '*' or '%' or '/' or '\\' ? '_' : ch).ToArray());

    private static async Task<IMailFolder?> Existing(ImapClient c, string? path, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try { return await c.GetFolderAsync(path, ct); } catch (FolderNotFoundException) { return null; }
    }

    private static async Task<IMailFolder> Child(IMailFolder parent, string name, CancellationToken ct)
    {
        foreach (var f in await parent.GetSubfoldersAsync(false, ct))
            if (f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return f;
        return await parent.CreateAsync(name, true, ct) ?? throw new InvalidOperationException($"Ordner „{name}“ ließ sich nicht anlegen.");
    }

    /// <summary>Ordner der Domain, bei Bedarf angelegt (oberste Ebene des persönlichen Namensraums).</summary>
    public static async Task<IMailFolder> DomainAsync(ImapClient c, UserDb db, string domain, CancellationToken ct)
    {
        if (await Existing(c, CatchallAliases.DomainFolder(db, domain), ct) is { } known) return known;
        var ns = c.PersonalNamespaces[0];
        var root = c.GetFolder(ns) ?? throw new InvalidOperationException("Kein Namensraum für Ordner.");
        var folder = await Child(root, Safe(domain, ns.DirectorySeparator), ct);
        CatchallAliases.SetDomainFolder(db, domain, folder.FullName);
        return folder;
    }

    /// <summary>Ordner eines angelegten Alias unter dem Ordner seiner Domain, bei Bedarf angelegt.</summary>
    public static async Task<IMailFolder> AliasAsync(ImapClient c, UserDb db, CatchallAliases.Alias a, CancellationToken ct)
    {
        if (await Existing(c, a.Folder, ct) is { } known) return known;
        var parent = await DomainAsync(c, db, CatchallAliases.DomainOf(a.Address), ct);
        var folder = await Child(parent, Safe(CatchallAliases.LocalOf(a.Address), parent.DirectorySeparator), ct);
        CatchallAliases.SetFolder(db, a.Address, folder.FullName);
        return folder;
    }

    /// <summary>Ordner für eine neue Mail an diese Adresse: der des Alias, bei unbekannten der Domain. Gesperrte nicht (die gehen nach
    /// Junk oder in den Papierkorb, CatchallAliases.Judge).</summary>
    public static async Task<IMailFolder?> TargetAsync(ImapClient c, UserDb db, string address, CancellationToken ct)
    {
        var a = CatchallAliases.Get(db, address);
        if (a?.Blocked != null) return null;
        return a != null ? await AliasAsync(c, db, a, ct) : await DomainAsync(c, db, CatchallAliases.DomainOf(address), ct);
    }

    /// <summary>
    /// Nach dem Anlegen oder Übernehmen eines Alias: seinen Ordner anlegen und die Mails an ihn aus Posteingang und Domain-Ordner
    /// dorthin verschieben. Gibt die Zahl verschobener Mails zurück. Ohne IMAP-Zugang (Demo, Tests) nichts.
    /// </summary>
    public static async Task<int> CollectAsync(UserSpace space, MailAccount account, string address, ImapPool pool, MailSync sync, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(account.ImapHost) || CatchallAliases.Get(space.Db, address) is not { } a) return 0;
        var db = space.Db;
        return await pool.UseAsync(account, ImapPool.Ui, async c =>
        {
            var target = await AliasAsync(c, db, a, ct);
            int moved = 0;
            foreach (var path in new[] { "INBOX", CatchallAliases.DomainFolder(db, CatchallAliases.DomainOf(a.Address)) }.OfType<string>().Distinct())
            {
                if (path == target.FullName) continue;
                var uids = db.Query(@"SELECT m.uid FROM mail m JOIN mail_alias_hit h ON h.message_id=m.message_id
                                      WHERE m.account=$a AND m.folder=$f AND h.alias=$x", r => (uint)r.GetInt64(0),
                    ("$a", account.Id), ("$f", path), ("$x", a.Address));
                if (uids.Count == 0) continue;
                var source = await c.GetFolderAsync(path, ct);
                await source.OpenAsync(FolderAccess.ReadWrite, ct);
                await source.MoveToAsync(uids.Select(u => new UniqueId(u)).ToList(), target, ct);
                MailStore.Delete(db, account.Id, path, uids);
                moved += uids.Count;
            }
            await MailSync.RefreshFolders(c, space, account, ct);
            sync.Kick(space.User.Id, account.Id, target.FullName);
            sync.Touch(space.User.Id);
            return moved;
        }, ct);
    }

    /// <summary>
    /// Vor dem Löschen eines Alias: alle Mails aus seinem Ordner in den Ordner der Domain verschieben, dann den Ordner des Alias auf
    /// dem Server löschen. Gibt die Zahl verschobener Mails zurück. Ohne IMAP-Zugang oder ohne Ordner nichts.
    /// </summary>
    public static async Task<int> RemoveAsync(UserSpace space, MailAccount account, string address, ImapPool pool, MailSync sync, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(account.ImapHost) || CatchallAliases.Get(space.Db, address) is not { Folder: { } path } a) return 0;
        var db = space.Db;
        return await pool.UseAsync(account, ImapPool.Ui, async c =>
        {
            if (await Existing(c, path, ct) is not { } folder) return 0;
            var target = await DomainAsync(c, db, CatchallAliases.DomainOf(a.Address), ct);
            await folder.OpenAsync(FolderAccess.ReadWrite, ct);
            var uids = await folder.SearchAsync(SearchQuery.All, ct);
            if (uids.Count > 0) await folder.MoveToAsync(uids, target, ct);
            // Manche Server löschen keinen ausgewählten Ordner
            await folder.CloseAsync(false, ct);
            await folder.DeleteAsync(ct);
            MailStore.WipeFolder(db, account.Id, path);
            await MailSync.RefreshFolders(c, space, account, ct);
            sync.Kick(space.User.Id, account.Id, target.FullName);
            sync.Touch(space.User.Id);
            return uids.Count;
        }, ct);
    }
}
