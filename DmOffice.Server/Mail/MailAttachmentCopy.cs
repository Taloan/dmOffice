using System.Security.Cryptography;
using System.Text;
using DmOffice.Core;
using DmOffice.Dav;

namespace DmOffice.Mail;

/// <summary>
/// Mail-Anhänge zum Ansehen im Office-Server: der Office-Server liest nur Dateien der Ablage (WOPI), darum eine Kopie im
/// Systemordner „sys-dmoffice-mail/&lt;Kennung&gt;/Name“. Dieselbe Mail und derselbe Anhang landen im selben Unterordner
/// (zweimal öffnen legt nichts doppelt an). Kopien, die älter als drei Tage sind, werden beim nächsten Mal endgültig gelöscht.
/// </summary>
public static class MailAttachmentCopy
{
    public const string Root = "sys-dmoffice-mail";
    public static readonly TimeSpan Keep = TimeSpan.FromDays(3);

    public static string Store(UserDb db, string key, string name, byte[] data)
    {
        Cleanup(db);
        var dir = Root + "/" + IdOf(key);
        var path = dir + "/" + name;
        if (FileStore.Get(db, path) is { IsDir: false } f && f.Size == data.Length) return path;
        FileStore.EnsureDir(db, dir);
        FileStore.Put(db, path, data, null);
        return path;
    }

    private static string IdOf(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..12].ToLowerInvariant();

    /// <summary>Die Kopien einer Mail sofort endgültig löschen (Vernichten nach Zeit). Schlüssel wie in MailApi „part/office“.</summary>
    public static void Forget(UserDb db, string account, string folder, uint uid, string? messageId, int maxParts = 64)
    {
        if (FileStore.Get(db, Root) is not { IsDir: true }) return;
        for (int i = 0; i < maxParts; i++)
            if (FileStore.Get(db, Root + "/" + IdOf($"{account}|{folder}|{uid}|{i}|{messageId}")) is { IsDir: true } d
                && FileStore.Delete(db, d.Path, out var trash) && trash > 0)
                FileStore.PurgeTrash(db, [trash]);
    }

    /// <summary>Alte Kopien endgültig löschen (nicht im Papierkorb aufheben).</summary>
    public static void Cleanup(UserDb db)
    {
        if (FileStore.Get(db, Root) is not { IsDir: true }) return;
        foreach (var d in FileStore.Children(db, Root).Where(c => c.IsDir && DateTime.UtcNow - c.Modified.ToUniversalTime() > Keep))
            if (FileStore.Delete(db, d.Path, out var trash) && trash > 0) FileStore.PurgeTrash(db, [trash]);
    }
}
