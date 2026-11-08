using DmOffice.Core;
using DmOffice.Dms;
using MimeKit;

namespace DmOffice.Mail;

/// <summary>
/// Mail → DMS (docs/dms-konzept.md, Stufe 2): „Ins DMS ablegen“ für die ganze Mail (.eml als Original, PDF als Archivfassung) und
/// für Anhänge, dazu die Mailkonten für Mailordner als Quelle (<see cref="IDmsMail"/>). Die Mail ruft das DMS auf, nicht umgekehrt;
/// DMS bekommt nur die rohe Mail bzw. Datei und eine Herkunftsangabe (Konto + Message-ID).
/// </summary>
public static class MailToDms
{
    /// <summary>Whole: die ganze Mail; Attachments: alle echten Anhänge; Parts: einzelne Teile (Nummer wie in der Lese-Ansicht).</summary>
    public record ToDmsBody(string Folder, uint Uid, bool Whole, bool Attachments, List<int>? Parts, string? Profile);

    public static void Map(WebApplication app)
    {
        var pool = app.Services.GetRequiredService<ImapPool>();
        var sync = app.Services.GetRequiredService<MailSync>();
        var outbox = app.Services.GetRequiredService<MailOutbox>();
        DmsHost.Mail = new Bridge(pool, sync, outbox);

        app.MapPost("/api/mail/{acc}/to-dms", async (string acc, ToDmsBody body, HttpContext http, CancellationToken ct) =>
        {
            var space = http.Space();
            if (Dav.FileStore.FilesOff(space.Db)) return Results.Json(new { error = "Die Dateiablage ist für dieses Konto abgeschaltet." }, statusCode: 403);
            var account = space.Account(acc);
            var message = await MailApi.Load(space, account, body.Folder, body.Uid, pool, ct);
            if (MailVirus.Of(space.Db, MailVirus.KeyOf(message, acc, body.Folder, body.Uid)) is { } virus)
                return Results.Json(new { error = $"Diese Mail enthält Schadsoftware ({virus}) – sie lässt sich nicht ablegen." }, statusCode: 403);
            var from = message.From.Mailboxes.FirstOrDefault()?.Address;
            var reference = RefOf(acc, message, body.Folder, body.Uid);
            var date = message.Date == DateTimeOffset.MinValue ? (DateTime?)null : message.Date.UtcDateTime;
            var results = new List<object>();
            if (body.Whole)
            {
                using var ms = new MemoryStream();
                await message.WriteToAsync(ms, ct);
                ms.Position = 0;
                var name = DmsImport.SafeName(string.IsNullOrWhiteSpace(message.Subject) ? "Mail" : message.Subject) + ".eml";
                var r = await DmsImport.ImportAsync(space, ms, name, "message/rfc822", new DmsImport.Info("mail", reference, from, message.Subject, body.Profile, FileDate: date), ct);
                results.Add(new { name, id = r.Id, duplicate = r.Duplicate });
            }
            var parts = MailCompose.Parts(message);
            var wanted = (body.Parts ?? []).ToHashSet();
            if (body.Attachments) for (var i = 0; i < parts.Count; i++) if (parts[i].IsAttachment) wanted.Add(i);
            foreach (var index in wanted.Order())
            {
                if (index < 0 || index >= parts.Count) continue;
                var part = parts[index];
                var name = part.FileName ?? part.ContentType.Name ?? $"Anhang {index + 1}";
                var r = await DmsImport.ImportAsync(space, new MemoryStream(MailCompose.Decode(part)), name, part.ContentType.MimeType,
                    new DmsImport.Info("mail", reference, from, null, body.Profile, FileDate: date), ct);
                results.Add(new { name, id = r.Id, duplicate = r.Duplicate });
            }
            return Results.Ok(new { files = results });
        }).RequireAuthorization();
    }

    /// <summary>Herkunft einer Mail für das DMS: Konto und Message-ID (bleibt beim Verschieben), sonst Ordner und UID.</summary>
    public static string RefOf(string acc, MimeMessage m, string folder, uint uid) => acc + "|" + (m.MessageId ?? $"{folder}|{uid}");

    /// <summary>Die Mailkonten der Person für das DMS (Mailordner als Quelle).</summary>
    private sealed class Bridge(ImapPool pool, MailSync sync, MailOutbox outbox) : IDmsMail
    {
        public List<(string Id, string Name)> Accounts(UserSpace space) =>
            space.Settings.MailAccounts.Select(a => (a.Id, string.IsNullOrWhiteSpace(a.Name) ? a.Email : $"{a.Name} ({a.Email})")).ToList();

        public List<(string Path, string Name)> Folders(UserSpace space, string account) =>
            MailStore.Folders(space.Db, account).Where(f => f.Selectable).Select(f => (f.Path, f.Name)).ToList();

        public List<DmsMailItem> List(UserSpace space, string account, string folder, DateTimeOffset since)
        {
            var result = new List<DmsMailItem>();
            for (var page = 0; page < 200; page++)
            {
                var (_, items) = MailStore.List(space.Db, account, folder, page, 200, null);
                foreach (var m in items)
                {
                    if (m.Date < since) { result.Reverse(); return result; }
                    result.Add(new DmsMailItem(m.Folder, m.Uid, m.MessageId, m.Date));
                }
                if (items.Count < 200) break;
            }
            result.Reverse();
            return result;
        }

        public async Task<byte[]> RawAsync(UserSpace space, string account, string folder, uint uid, CancellationToken ct)
        {
            var m = await MailApi.Load(space, space.Account(account), folder, uid, pool, ct);
            using var ms = new MemoryStream();
            await m.WriteToAsync(ms, ct);
            return ms.ToArray();
        }

        public void After(UserSpace space, string account, string folder, uint uid, string action, string? target)
        {
            var acc = space.Account(account);
            switch (action)
            {
                case "seen": MailApi.SetFlags(space, acc, outbox, folder, [uid], true, null); break;
                case "flag": MailApi.SetFlags(space, acc, outbox, folder, [uid], null, true); break;
                case "move" when !string.IsNullOrEmpty(target) && target != folder: MailApi.Move(space, acc, outbox, sync, folder, [uid], target); break;
                case "delete":
                    if (MailStore.Folders(space.Db, account).FirstOrDefault(f => f.Special == "trash") is { } trash && trash.Path != folder)
                        MailApi.Move(space, acc, outbox, sync, folder, [uid], trash.Path);
                    break;
            }
            sync.Touch(space.User.Id);
        }
    }
}
