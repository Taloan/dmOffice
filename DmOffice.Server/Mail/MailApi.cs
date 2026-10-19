using System.Text.Json;
using DmOffice.Core;
using MailKit;
using MailKit.Net.Imap;
using MimeKit;

namespace DmOffice.Mail;

/// <summary>
/// Liste, Lesen und Suche aus der eigenen Datenbank; Aenderungen (Flags,
/// Verschieben, Loeschen, Senden) gehen sofort zum Server und werden hier
/// gleich nachgezogen, damit die Oberflaeche nicht auf den Sync wartet.
/// </summary>
public static class MailApi
{
    public record AttachmentDto(int Index, string Name, string Mime, long Size, bool Inline);

    public record MessageDto(string Account, uint Uid, string Folder, string Subject, Addr? From, List<Addr> To, List<Addr> Cc,
        List<Addr> Bcc, List<Addr> ReplyTo, DateTimeOffset Date, string Html, bool HasRemoteImages, string Text,
        List<AttachmentDto> Attachments, string? MessageId, string? InReplyTo, List<string> References,
        bool Seen, bool Flagged, double? Spam, bool Cached, bool ImagesAllowed, bool ImagesTrusted, Unsubscribe.Info? Unsubscribe = null);

    public record UidsBody(string Folder, List<uint> Uids);
    public record FlagsBody(string Folder, List<uint> Uids, bool? Seen, bool? Flagged);
    public record MoveBody(string Folder, List<uint> Uids, string Target);
    public record FolderBody(string Path, string? NewName);
    public record SnoozeBody(string Folder, List<uint> Uids, DateTime Until);
    public record ToNotiziBody(string Folder, uint Uid, bool Attachments = true);
    public record FollowUpBody(string Folder, List<uint> Uids, string? Due, bool Done = false);
    public record UidBody(string Folder, uint Uid);
    public record FocusBody(string Folder, List<uint> Uids, bool Focused, bool Always = false);
    public record CleanupBody(string Special, int Days);
    public record QueueTimeBody(string? SendAt);
    public record InviteBody(string Folder, uint Uid, string Response, bool Send = true);
    public record CategoryBody(string Folder, List<uint> Uids, string Key, bool On);

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api/mail").RequireAuthorization();

        api.MapGet("/accounts", (HttpContext http) =>
            http.Space().Settings.MailAccounts.Select(a => new { a.Id, a.Name, a.Email, a.DisplayName, a.Signature, a.Language, a.TrashDays, a.JunkDays }));

        api.MapGet("/status", (HttpContext http, MailSync sync) =>
        {
            var space = http.Space();
            return new
            {
                version = sync.Version(space.User.Id),
                outbox = MailQueue.Count(space.Db),
                accounts = space.Settings.MailAccounts.ToDictionary(a => a.Id, a =>
                {
                    var s = sync.StateOf(space.User.Id, a.Id);
                    var inbox = MailStore.Folders(space.Db, a.Id).FirstOrDefault(f => f.Special == "inbox");
                    return new { s.Running, s.Phase, s.LastFull, s.LastQuick, s.Error, s.PendingBodies, unread = inbox?.Unread ?? 0 };
                })
            };
        });

        api.MapPost("/sync", (HttpContext http, MailSync sync) =>
        {
            var space = http.Space();
            foreach (var a in space.Settings.MailAccounts) sync.Kick(space.User.Id, a.Id);
            return Results.Ok();
        });

        api.MapGet("/{acc}/folders", (string acc, HttpContext http) =>
        {
            var db = http.Space().Db;
            var list = MailStore.Folders(db, acc).Select(f => new { f.Path, f.Name, f.Delimiter, f.Special, f.Selectable, f.Unread, f.Total, f.Synced }).ToList();
            if (list.Count == 0) return list;
            // "Markiert": alle gekennzeichneten Mails aus allen Ordnern
            var (total, unread) = MailStore.FlaggedCounts(db, acc);
            list.Add(new { Path = MailStore.FlaggedFolder, Name = "Markiert", Delimiter = "/", Special = (string?)"flagged", Selectable = true, Unread = unread, Total = total, Synced = (string?)null });
            return list;
        });

        api.MapGet("/{acc}/messages", (string acc, string folder, int? page, int? size, string? filter, HttpContext http) =>
        {
            int n = Math.Clamp(size ?? 100, 1, 500), p = Math.Max(0, page ?? 0);
            var space = http.Space();
            // Suchordner und Kategorie-Ordner: ueber alle Konten
            if (MailStore.Virtual(space.Settings, folder) is { } criteria)
            {
                var (vt, _, vi) = MailStore.Query(space.Db, criteria, p, n, filter);
                return Results.Ok(new { total = vt, page = p, size = n, items = vi });
            }
            // Prio-Posteingang: noch nicht Eingeordnetes erst einordnen
            if (filter is "focused" or "other") Focus.Backfill(space, acc, folder);
            var (total, items) = MailStore.List(space.Db, acc, folder, p, n, filter);
            return Results.Ok(new { total, page = p, size = n, items });
        });

        // Zaehler fuer Suchordner und Kategorien im Ordnerbereich
        api.MapGet("/virtual-counts", (HttpContext http) =>
        {
            var space = http.Space();
            var result = new Dictionary<string, object>();
            foreach (var f in space.Settings.SearchFolders)
            {
                var (t, u, _) = MailStore.Query(space.Db, f.Criteria, 0, 0);
                result[MailStore.SearchPrefix + f.Id] = new { total = t, unread = u };
            }
            foreach (var c in Settings.Categories.Of(space.Settings))
            {
                var (t, u, _) = MailStore.Query(space.Db, new MailCriteria { Categories = new() { c.Key } }, 0, 0);
                result[MailStore.CategoryPrefix + c.Key] = new { total = t, unread = u };
            }
            return result;
        });

        // Vorschau beim Einrichten eines Suchordners
        api.MapPost("/query", (MailCriteria body, HttpContext http) =>
        {
            var (t, u, items) = MailStore.Query(http.Space().Db, body, 0, 20);
            return new { total = t, unread = u, items };
        });

        api.MapGet("/search", (string q, string? acc, string? folder, bool? deleted, bool? relevance, HttpContext http) =>
            MailStore.Search(http.Space().Db, q, acc, folder, 300, deleted ?? true, relevance ?? false));

        api.MapGet("/{acc}/message", async (string acc, string folder, uint uid, bool? images, bool? markSeen,
            HttpContext http, ImapPool pool, MailOutbox outbox, CancellationToken ct) =>
        {
            var space = http.Space();
            var account = space.Account(acc);
            var row = MailStore.Row(space.Db, acc, folder, uid) ?? throw new KeyNotFoundException("Mail nicht gefunden.");
            var message = await Load(space, account, folder, uid, pool, ct);
            // Sichere Absender: Bilder ohne Nachfrage
            var from = message.From.Mailboxes.FirstOrDefault()?.Address?.ToLowerInvariant() ?? "";
            var trusted = from.Length > 0 && space.Settings.TrustedImageSenders.Any(t =>
                t.StartsWith('@') ? from.EndsWith(t, StringComparison.OrdinalIgnoreCase) : from.Equals(t, StringComparison.OrdinalIgnoreCase));
            if (trusted) images = true;
            if (markSeen == true && !row.Seen) SetFlags(space, account, outbox, folder, new() { uid }, seen: true, flagged: null);
            return Results.Ok(Detail(acc, folder, uid, message, images == true, row with { Seen = row.Seen || markSeen == true }, trusted));
        });

        api.MapGet("/{acc}/part", async (string acc, string folder, uint uid, int index, bool? download,
            HttpContext http, ImapPool pool, CancellationToken ct) =>
        {
            var space = http.Space();
            var message = await Load(space, space.Account(acc), folder, uid, pool, ct);
            var parts = MailCompose.Parts(message);
            if (index < 0 || index >= parts.Count) return Results.NotFound();
            var part = parts[index];
            var mime = part.ContentType.MimeType;
            // Nur Bilder inline - alles andere als Download, damit kein HTML aus einer Mail unter dieser Adresse laeuft
            bool inline = download != true && mime.StartsWith("image/") && mime != "image/svg+xml";
            var name = part.FileName ?? part.ContentType.Name ?? $"anhang-{index}";
            return Results.File(MailCompose.Decode(part), inline ? mime : "application/octet-stream", inline ? null : name);
        });

        api.MapGet("/{acc}/source", async (string acc, string folder, uint uid, HttpContext http, ImapPool pool, CancellationToken ct) =>
        {
            var space = http.Space();
            var message = await Load(space, space.Account(acc), folder, uid, pool, ct);
            var ms = new MemoryStream();
            await message.WriteToAsync(ms, ct);
            return Results.File(ms.ToArray(), "message/rfc822", $"mail-{uid}.eml");
        });

        api.MapPost("/{acc}/flags", async (string acc, FlagsBody body, HttpContext http, ImapPool pool, MailSync sync, MailOutbox outbox, CancellationToken ct) =>
        {
            var space = http.Space();
            SetFlags(space, space.Account(acc), outbox, body.Folder, body.Uids, body.Seen, body.Flagged);
            sync.Touch(space.User.Id);
            return Results.Ok();
        });

        // ---------------- Prio-Posteingang ----------------
        // Ungelesene je Reiter (Relevant/Sonstige)
        api.MapGet("/{acc}/focus", (string acc, string folder, HttpContext http) =>
        {
            var space = http.Space();
            if (!(space.Settings.FocusedInbox && space.Account(acc).FocusedInbox)) return Results.Ok(new { enabled = false, focused = 0, other = 0 });
            Focus.Backfill(space, acc, folder);
            var (focused, other) = MailStore.FocusUnread(space.Db, acc, folder);
            return Results.Ok(new { enabled = space.Settings.FocusedInbox, focused, other });
        });

        // Nach Relevant/Sonstige verschieben: lernt aus dem Inhalt; "always" merkt sich den Absender fest
        api.MapPost("/{acc}/focus", async (string acc, FocusBody body, HttpContext http, ImapPool pool, MailSync sync, CancellationToken ct) =>
        {
            var space = http.Space();
            var account = space.Account(acc);
            MailStore.SetFocus(space.Db, acc, body.Folder, body.Uids, body.Focused);
            foreach (var uid in body.Uids)
            {
                if (body.Always && MailStore.Row(space.Db, acc, body.Folder, uid)?.From?.Address is { Length: > 0 } from)
                    Focus.SetSender(space.Db, from, body.Focused);
                try { Focus.Train(space.Db, await Load(space, account, body.Folder, uid, pool, ct), body.Focused); }
                catch (KeyNotFoundException) { }
            }
            sync.Touch(space.User.Id);
            return Results.Ok();
        });

        // ---------------- Von Newslettern abmelden ----------------
        // One-Click (RFC 8058) erledigt der Server, sonst eine Mail an die mailto-Adresse; bleibt nur ein Link,
        // kommt er zurueck und der Browser bzw. die App oeffnet ihn.
        api.MapPost("/{acc}/unsubscribe", async (string acc, UidBody body, HttpContext http, ImapPool pool, IHttpClientFactory clients, CancellationToken ct) =>
        {
            var space = http.Space();
            var account = space.Account(acc);
            var message = await Load(space, account, body.Folder, body.Uid, pool, ct);
            var u = Unsubscribe.Of(message) ?? throw new InvalidOperationException("Diese Mail bietet keine Abmeldung an.");
            if (u.OneClick && u.Url != null)
                try { if (await Unsubscribe.OneClickAsync(clients.CreateClient("unsub"), u.Url, ct)) return Results.Ok(new { done = true, how = "oneclick" }); }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { }
            if (u.Mailto != null)
            {
                using var smtp = await ImapPool.SmtpAsync(account, ct);
                await smtp.SendAsync(Unsubscribe.MailFor(account, u.Mailto), ct);
                await smtp.DisconnectAsync(true, ct);
                return Results.Ok(new { done = true, how = "mail" });
            }
            return Results.Ok(new { done = false, url = u.Url });
        });

        // ---------------- E-Mail-Nachverfolgung ----------------
        api.MapGet("/followups", (HttpContext http) =>
            MailStore.FollowUps(http.Space().Db).Select(x => new { mail = x.Mail, due = x.Due }));

        // Kennzeichnen mit Faelligkeit (due: yyyy-MM-dd oder null = ohne Datum)
        api.MapPost("/{acc}/followup", async (string acc, FollowUpBody body, HttpContext http, ImapPool pool, MailSync sync, MailOutbox outbox, CancellationToken ct) =>
        {
            var space = http.Space();
            if (body.Due != null && !DateOnly.TryParse(body.Due, out _)) return Results.BadRequest(new { error = "Ungültiges Datum." });
            SetFlags(space, space.Account(acc), outbox, body.Folder, body.Uids, null, body.Done ? false : true);
            MailStore.SetFollowUpDue(space.Db, acc, body.Folder, body.Uids, body.Done ? null : body.Due);
            sync.Touch(space.User.Id);
            return Results.Ok();
        });

        api.MapPost("/{acc}/move", async (string acc, MoveBody body, HttpContext http, ImapPool pool, MailSync sync, MailOutbox outbox, CancellationToken ct) =>
        {
            var space = http.Space();
            Move(space, space.Account(acc), outbox, sync, body.Folder, body.Uids, body.Target);
            return Results.Ok();
        });

        api.MapPost("/{acc}/delete", async (string acc, UidsBody body, HttpContext http, ImapPool pool, MailSync sync, MailOutbox outbox, CancellationToken ct) =>
        {
            var space = http.Space();
            var account = space.Account(acc);
            var trash = MailStore.Folders(space.Db, acc).FirstOrDefault(f => f.Special == "trash");
            if (trash != null && trash.Path != body.Folder)
            {
                Move(space, account, outbox, sync, body.Folder, body.Uids, trash.Path);
                return Results.Ok(new { moved = true });
            }
            // Schon im Papierkorb (oder es gibt keinen): endgueltig
            MailStore.Delete(space.Db, acc, body.Folder, body.Uids);
            sync.Touch(space.User.Id);
            outbox.Enqueue(space, account, body.Folder, body.Uids, removes: true, "expunge", async (c, ct) =>
            {
                var f = await c.GetFolderAsync(body.Folder, ct);
                await f.OpenAsync(FolderAccess.ReadWrite, ct);
                await ExpungeOnly(c, f, body.Uids.Select(u => new UniqueId(u)).ToList(), ct);
            });
            return Results.Ok(new { moved = false });
        });

        // Junk: verschieben und lernen. Kein Junk: zurueck in den Posteingang und als gut lernen.
        api.MapPost("/{acc}/junk", async (string acc, UidsBody body, HttpContext http, ImapPool pool, MailSync sync, MailOutbox outbox, CancellationToken ct) =>
        {
            var space = http.Space();
            var account = space.Account(acc);
            foreach (var uid in body.Uids)
                SpamFilter.Train(space.Db, await Load(space, account, body.Folder, uid, pool, ct), spam: true);
            var junk = MailStore.Folders(space.Db, acc).FirstOrDefault(f => f.Special == "junk")
                ?? throw new InvalidOperationException("Kein Junk-Ordner gefunden.");
            if (junk.Path != body.Folder) Move(space, account, outbox, sync, body.Folder, body.Uids, junk.Path);
            return Results.Ok();
        });

        api.MapPost("/{acc}/notjunk", async (string acc, UidsBody body, HttpContext http, ImapPool pool, MailSync sync, MailOutbox outbox, CancellationToken ct) =>
        {
            var space = http.Space();
            var account = space.Account(acc);
            foreach (var uid in body.Uids)
                SpamFilter.Train(space.Db, await Load(space, account, body.Folder, uid, pool, ct), spam: false);
            var inbox = MailStore.Folders(space.Db, acc).First(f => f.Special == "inbox");
            if (MailStore.Folder(space.Db, acc, body.Folder)?.Special == "junk")
                Move(space, account, outbox, sync, body.Folder, body.Uids, inbox.Path);
            else
                foreach (var uid in body.Uids) MailStore.SetSpamScore(space.Db, acc, body.Folder, uid, 0);
            return Results.Ok();
        });

        // ---------------- Termine aus Mails (Pakete, Reisen) ----------------
        api.MapGet("/{acc}/smart", async (string acc, string folder, uint uid, HttpContext http, ImapPool pool, CancellationToken ct) =>
        {
            var space = http.Space();
            var message = await Load(space, space.Account(acc), folder, uid, pool, ct);
            var items = SmartMail.Extract(message, space.Zone, DateTime.UtcNow);
            if (items.Count == 0) return Results.NoContent();
            return Results.Ok(items.Select(i => new { i.Kind, i.Title, i.Start, i.End, i.AllDay, i.Location, i.Details, inCalendar = SmartMail.InCalendar(space.Db, i) }));
        });

        api.MapPost("/{acc}/smart", async (string acc, UidBody body, HttpContext http, ImapPool pool, CancellationToken ct) =>
        {
            var space = http.Space();
            var message = await Load(space, space.Account(acc), body.Folder, body.Uid, pool, ct);
            var link = message.MessageId is { Length: > 0 } mid
                ? $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}/#/mail/open/{Uri.EscapeDataString(acc)}/{Uri.EscapeDataString(mid)}" : null;
            long? calendar = null;
            foreach (var item in SmartMail.Extract(message, space.Zone, DateTime.UtcNow)) calendar = SmartMail.Upsert(space, item, link);
            return Results.Ok(new { calendar });
        });

        // ---------------- Einladungen (iMIP) ----------------
        api.MapGet("/{acc}/invite", async (string acc, string folder, uint uid, HttpContext http, ImapPool pool, CancellationToken ct) =>
        {
            var space = http.Space();
            var message = await Load(space, space.Account(acc), folder, uid, pool, ct);
            return Invites.Read(space, message) is { } dto ? Results.Ok(dto) : Results.NoContent();
        });

        api.MapPost("/{acc}/invite", async (string acc, InviteBody body, HttpContext http, ImapPool pool, CancellationToken ct) =>
        {
            if (body.Response is not ("accept" or "tentative" or "decline" or "remove" or "apply"))
                return Results.BadRequest(new { error = "Unbekannte Antwort." });
            var space = http.Space();
            var account = space.Account(acc);
            var message = await Load(space, account, body.Folder, body.Uid, pool, ct);
            var sent = await Invites.RespondAsync(space, account, message, body.Response, body.Send, ct);
            return Results.Ok(new { sent, invite = Invites.Read(space, message) });
        });

        // ---------------- Kategorien (IMAP-Schlagworte) ----------------
        api.MapPost("/{acc}/category", async (string acc, CategoryBody body, HttpContext http, ImapPool pool, MailSync sync, CancellationToken ct) =>
        {
            var space = http.Space();
            var key = body.Key.Trim().ToLowerInvariant();
            if (!Settings.Categories.ValidKey(key)) return Results.BadRequest(new { error = "Ungültige Kategorie." });
            await pool.UseAsync(space.Account(acc), ImapPool.Ui, async c =>
            {
                var f = await c.GetFolderAsync(body.Folder, ct);
                await f.OpenAsync(FolderAccess.ReadWrite, ct);
                // Nimmt der Server keine eigenen Schlagworte an (z. B. Gmail), meldet er das hier
                if (!f.PermanentFlags.HasFlag(MessageFlags.UserDefined) && !f.PermanentKeywords.Contains(key))
                    throw new InvalidOperationException("Dieser Mailserver speichert keine Kategorien (eigene IMAP-Schlagworte).");
                var req = new StoreFlagsRequest(body.On ? StoreAction.Add : StoreAction.Remove, MessageFlags.None) { Silent = true };
                req.Keywords.Add(key);
                await f.StoreAsync(body.Uids.Select(u => new UniqueId(u)).ToList(), req, ct);
            }, ct);
            MailStore.SetKeyword(space.Db, acc, body.Folder, body.Uids, key, body.On);
            sync.Touch(space.User.Id);
            return Results.Ok();
        });

        // ---------------- Als Notiz speichern ----------------
        // Wie "An OneNote senden": eine Notiz mit Kopf, Text und Anhaengen - geht auch ohne notizi-Abgleich.
        // Der Text statt des HTML - Newsletter-Tabellen waeren in notizi am PC sonst gesperrte Bloecke.
        // Ruecklink aus einer Notiz: Mail per Message-ID finden (sie kann inzwischen verschoben sein)
        api.MapGet("/{acc}/find", (string acc, string messageId, HttpContext http) =>
        {
            var space = http.Space();
            space.Account(acc);
            return MailStore.ByMessageId(space.Db, acc, messageId) is { } row ? Results.Ok(row) : Results.NotFound(new { error = "Die Mail gibt es hier nicht mehr." });
        });

        api.MapPost("/{acc}/to-note", async (string acc, ToNotiziBody body, HttpContext http, ImapPool pool, Notes.NotesSync notes, CancellationToken ct) =>
        {
            var space = http.Space();
            var message = await Load(space, space.Account(acc), body.Folder, body.Uid, pool, ct);
            var db = Notes.NotesDb.Of(space);

            string E(string s) => System.Net.WebUtility.HtmlEncode(s);
            var head = new System.Text.StringBuilder();
            // Ruecklink: oeffnet die Mail in dmOffice, auch nachdem sie verschoben wurde
            string? link = message.MessageId is { Length: > 0 } mid0
                ? $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}/#/mail/open/{Uri.EscapeDataString(acc)}/{Uri.EscapeDataString(mid0)}"
                : null;
            if (link != null) head.Append("<p><a href=\"").Append(E(link)).Append("\">✉ Mail in dmOffice öffnen</a></p>");
            var subject = string.IsNullOrWhiteSpace(message.Subject) ? "(ohne Betreff)" : message.Subject.Trim();
            head.Append("<p>Von: ").Append(E(message.From.ToString())).Append("</p>");
            head.Append("<p>Datum: ").Append(E(message.Date.ToLocalTime().ToString("dd.MM.yyyy HH:mm"))).Append("</p>");
            if (message.To.Count > 0) head.Append("<p>An: ").Append(E(message.To.ToString())).Append("</p>");
            head.Append("<p></p>");
            var text = MailStore.BodyText(message);
            // Der Betreff ist der Titel der Notiz - im Text steht er nicht noch einmal
            var plain = new System.Text.StringBuilder();
            // Leerzeilen zusammenfassen, sonst stehen Newsletter mit Dutzenden Leerabsaetzen da
            bool blank = false;
            foreach (var line in text.Replace("\r", "").Split('\n'))
            {
                var t = line.TrimEnd();
                if (t.Length == 0) { if (!blank) head.Append("<p></p>"); blank = true; continue; }
                blank = false;
                // Textteile mancher Absender enthalten HTML-Entities ("&amp;") - einmal aufloesen
                head.Append("<p>").Append(E(System.Net.WebUtility.HtmlDecode(t))).Append("</p>");
                plain.Append(t).Append("\r\n");
            }
            var html = "<div data-notizi=\"1\">" + head.ToString().Replace("&nbsp;", "&#160;") + "</div>";

            var id = db.CreateNote(null);
            db.SaveNote(id, html, plain.ToString());
            db.SetCustomTitle(id, subject);
            db.SetSources(id, new List<Notizi.Sync.SourceRef> { new() { Title = subject, Url = link ?? (message.MessageId is { } mid ? "mid:" + mid : null), App = "dmOffice Mail" } });

            int attached = 0;
            if (body.Attachments)
            {
                foreach (var part in MailCompose.Parts(message).Where(p => p.IsAttachment))
                {
                    var data = MailCompose.Decode(part);
                    db.AddAttachment(id, part.FileName ?? "anhang", part.ContentType.MimeType, data, await Notes.NotesSync.BlobNameAsync(space, data, ct));
                    attached++;
                }
            }
            notes.Kick(space.User.Id, TimeSpan.FromSeconds(3));
            return Results.Ok(new { id, attached });
        });

        // ---------------- Erneut erinnern ----------------

        api.MapGet("/snoozes", (HttpContext http) =>
            Snooze.All(http.Space().Db).Select(e => new { e.Account, e.Uid, e.MessageId, e.Until, e.Subject }));

        api.MapPost("/{acc}/snooze", async (string acc, SnoozeBody body, HttpContext http, ImapPool pool, MailSync sync, CancellationToken ct) =>
        {
            var space = http.Space();
            var account = space.Account(acc);
            if (body.Until.ToUniversalTime() <= DateTime.UtcNow.AddMinutes(1)) return Results.BadRequest(new { error = "Der Zeitpunkt liegt in der Vergangenheit." });
            string? path = null;
            await pool.UseAsync(account, ImapPool.Ui, async c =>
            {
                await Snooze.AddAsync(c, space, account, body.Folder, body.Uids, body.Until, ct);
                path = (await Snooze.FolderAsync(c, space, account, ct)).FullName;
            }, ct);
            // Ordnerliste neu, falls er gerade angelegt wurde; der Ordner selbst bekommt die Mails
            sync.Kick(space.User.Id, acc);
            sync.Touch(space.User.Id);
            return Results.Ok(new { folder = path });
        });

        // Sofort zurueckholen (Erinnerung aufheben)
        api.MapPost("/{acc}/unsnooze", async (string acc, UidsBody body, HttpContext http, ImapPool pool, MailSync sync, CancellationToken ct) =>
        {
            var space = http.Space();
            var account = space.Account(acc);
            var ids = body.Uids.ToHashSet();
            var messageIds = MailStore.Folders(space.Db, acc).Any(f => f.Path == body.Folder)
                ? body.Uids.Select(u => MailStore.Row(space.Db, acc, body.Folder, u)?.MessageId).Where(x => x != null).ToHashSet()
                : new HashSet<string?>();
            var n = await pool.UseAsync(account, ImapPool.Ui, c =>
                Snooze.ReturnAsync(c, space, account, e => ids.Contains(e.Uid) || (e.MessageId != null && messageIds.Contains(e.MessageId)), ct), ct);
            sync.Kick(space.User.Id, acc, "INBOX");
            sync.Touch(space.User.Id);
            return Results.Ok(new { returned = n });
        });

        api.MapPost("/{acc}/folders", async (string acc, FolderBody body, HttpContext http, ImapPool pool, MailSync sync, CancellationToken ct) =>
        {
            var space = http.Space();
            var path = await pool.UseAsync(space.Account(acc), ImapPool.Ui, async c =>
            {
                // Path = Elternordner ("" = oberste Ebene), NewName = Name des neuen Ordners
                var parent = string.IsNullOrEmpty(body.Path) ? c.GetFolder(c.PersonalNamespaces[0]) : await c.GetFolderAsync(body.Path, ct);
                var created = await parent.CreateAsync(body.NewName ?? "Neuer Ordner", true, ct)
                    ?? throw new InvalidOperationException("Der Server hat den Ordner nicht angelegt.");
                return created.FullName;
            }, ct);
            sync.Kick(space.User.Id, acc);
            return Results.Ok(new { path });
        });

        // Alles im Ordner gelesen - auf dem Server alle Ungelesenen, nicht nur die im Sync-Zeitraum
        api.MapPost("/{acc}/folders/read", async (string acc, FolderBody body, HttpContext http, ImapPool pool, MailSync sync, CancellationToken ct) =>
        {
            var space = http.Space();
            await pool.UseAsync(space.Account(acc), ImapPool.Ui, async c =>
            {
                var f = await c.GetFolderAsync(body.Path, ct);
                await f.OpenAsync(FolderAccess.ReadWrite, ct);
                var unseen = await f.SearchAsync(MailKit.Search.SearchQuery.NotSeen, ct);
                foreach (var batch in unseen.Chunk(1000))
                    await f.StoreAsync(batch, new StoreFlagsRequest(StoreAction.Add, MessageFlags.Seen) { Silent = true }, ct);
            }, ct);
            MailStore.MarkFolderRead(space.Db, acc, body.Path);
            sync.Touch(space.User.Id);
            return Results.Ok();
        });

        // Leeren wie in Outlook: im Papierkorb und in Junk endgueltig, sonst alles in den Papierkorb
        api.MapPost("/{acc}/folders/empty", async (string acc, FolderBody body, HttpContext http, ImapPool pool, MailSync sync, CancellationToken ct) =>
        {
            var space = http.Space();
            var account = space.Account(acc);
            var row = MailStore.Folder(space.Db, acc, body.Path) ?? throw new KeyNotFoundException("Ordner nicht gefunden.");
            var trash = MailStore.Folders(space.Db, acc).FirstOrDefault(f => f.Special == "trash");
            bool permanent = row.Special is "trash" or "junk" || trash == null;
            var count = await pool.UseAsync(account, ImapPool.Ui, async c =>
            {
                var f = await c.GetFolderAsync(body.Path, ct);
                await f.OpenAsync(FolderAccess.ReadWrite, ct);
                var all = await f.SearchAsync(MailKit.Search.SearchQuery.All, ct);
                if (all.Count == 0) return 0;
                if (permanent)
                {
                    foreach (var batch in all.Chunk(1000))
                        await f.StoreAsync(batch, new StoreFlagsRequest(StoreAction.Add, MessageFlags.Deleted) { Silent = true }, ct);
                    await f.ExpungeAsync(ct);
                }
                else
                {
                    var target = await c.GetFolderAsync(trash!.Path, ct);
                    foreach (var batch in all.Chunk(1000)) await f.MoveToAsync(batch, target, ct);
                }
                return all.Count;
            }, ct);
            MailStore.WipeFolder(space.Db, acc, body.Path);
            MailStore.SetFolderState(space.Db, acc, body.Path, row.UidValidity, 0, 0, 0);
            if (!permanent) sync.Kick(space.User.Id, acc, trash!.Path);
            sync.Touch(space.User.Id);
            return Results.Ok(new { count, permanent });
        });

        // Papierkorb/Junk automatisch leeren: Tage einstellen und gleich einmal aufraeumen
        api.MapPost("/{acc}/folders/cleanup", async (string acc, CleanupBody body, HttpContext http, ImapPool pool, MailSync sync, CancellationToken ct) =>
        {
            var space = http.Space();
            if (body.Special is not ("trash" or "junk")) return Results.BadRequest(new { error = "Nur für Papierkorb und Junk." });
            var days = Math.Clamp(body.Days, 0, 3650);
            space.UpdateSettings(s =>
            {
                var a = s.MailAccounts.First(x => x.Id == acc);
                if (body.Special == "trash") a.TrashDays = days; else a.JunkDays = days;
            });
            var count = days > 0 ? await MailCleanup.RunAsync(space, space.Account(acc), pool, sync, ct) : 0;
            return Results.Ok(new { count });
        });

        api.MapPost("/{acc}/folders/rename", async (string acc, FolderBody body, HttpContext http, ImapPool pool, MailSync sync, CancellationToken ct) =>
        {
            var space = http.Space();
            await pool.UseAsync(space.Account(acc), ImapPool.Ui, async c =>
            {
                var folder = await c.GetFolderAsync(body.Path, ct);
                await folder.RenameAsync(folder.ParentFolder ?? c.GetFolder(c.PersonalNamespaces[0]), body.NewName ?? folder.Name, ct);
            }, ct);
            sync.Kick(space.User.Id, acc);
            return Results.Ok();
        });

        api.MapPost("/{acc}/folders/delete", async (string acc, FolderBody body, HttpContext http, ImapPool pool, MailSync sync, CancellationToken ct) =>
        {
            var space = http.Space();
            await pool.UseAsync(space.Account(acc), ImapPool.Ui, async c => await (await c.GetFolderAsync(body.Path, ct)).DeleteAsync(ct), ct);
            sync.Kick(space.User.Id, acc);
            return Results.Ok();
        });

        // Senden: in den Postausgang (verzoegert zum Zurueckrufen oder zu einer geplanten Zeit), oder sofort
        api.MapPost("/{acc}/send", async (string acc, HttpRequest request, HttpContext http, ImapPool pool, MailSync sync, CancellationToken ct) =>
        {
            var space = http.Space();
            var account = space.Account(acc);
            var (draft, files) = await ReadDraft(request, ct);
            var source = SourceOf(space, account, draft);
            MimeMessage message;
            // Ungueltige Adresse: 400 mit Meldung, wie die anderen Eingabefehler
            try { message = MailCompose.Build(account, draft, await Attachments(space, source, draft, files, pool, ct)); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            if (!message.To.Mailboxes.Any() && !message.Cc.Mailboxes.Any() && !message.Bcc.Mailboxes.Any())
                return Results.BadRequest(new { error = "Kein Empfänger." });

            DateTime? at = null;
            if (!string.IsNullOrEmpty(draft.SendAt))
            {
                if (!DateTime.TryParse(draft.SendAt, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var when))
                    return Results.BadRequest(new { error = "Ungültige Sendezeit." });
                at = when;
            }
            else if (draft.DelaySeconds is int d && d > 0) at = DateTime.UtcNow.AddSeconds(Math.Min(d, 30));
            if (at != null)
            {
                var id = MailQueue.Add(space.Db, account.Id, source.Id, draft, message, at.Value);
                sync.Touch(space.User.Id);
                return Results.Ok(new { queued = id, sendAt = at.Value.ToString("o") });
            }

            var log = http.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("DmOffice.Mail.Send");
            var warnings = await Deliver(space, account, source, draft, message, pool, sync, log, ct);
            sync.Touch(space.User.Id);
            return warnings.Count == 0
                ? Results.Ok()
                : Results.Ok(new { sent = true, warning = "Die Mail wurde gesendet. Nicht geklappt hat: " + string.Join("; ", warnings) });
        }).DisableAntiforgery();

        // ---------------- Postausgang ----------------
        api.MapGet("/queue", (HttpContext http) => MailQueue.List(http.Space().Db));

        // Zurueckrufen oder loeschen - nur solange sie noch nicht unterwegs ist
        api.MapPost("/queue/{id:long}/cancel", (long id, HttpContext http, MailSync sync) =>
        {
            var space = http.Space();
            if (!MailQueue.Cancel(space.Db, id)) return Results.Conflict(new { error = "Die Mail ist schon unterwegs." });
            sync.Touch(space.User.Id);
            return Results.Ok();
        });

        api.MapPost("/queue/{id:long}/time", (long id, QueueTimeBody body, HttpContext http, MailSync sync) =>
        {
            var space = http.Space();
            DateTime when = DateTime.UtcNow;
            if (!string.IsNullOrEmpty(body.SendAt) && !DateTime.TryParse(body.SendAt, null,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out when))
                return Results.BadRequest(new { error = "Ungültige Sendezeit." });
            if (!MailQueue.Reschedule(space.Db, id, when)) return Results.Conflict(new { error = "Die Mail ist schon unterwegs." });
            sync.Touch(space.User.Id);
            return Results.Ok();
        });

        // Nicht senden, sondern zurueck in die Entwuerfe (mit allen Anhaengen)
        api.MapPost("/queue/{id:long}/to-drafts", async (long id, HttpContext http, ImapPool pool, MailSync sync, CancellationToken ct) =>
        {
            var space = http.Space();
            var item = MailQueue.Take(space.Db, id) ?? throw new KeyNotFoundException("Die Mail ist schon unterwegs.");
            var account = space.Account(item.Account);
            try
            {
                await pool.UseAsync(account, ImapPool.Ui, async c =>
                {
                    var drafts = await MailSync.Special(c, space, account, "drafts", ct)
                        ?? throw new InvalidOperationException("Kein Entwürfe-Ordner gefunden.");
                    await drafts.AppendAsync(new AppendRequest(item.Message, MessageFlags.Draft | MessageFlags.Seen), ct);
                    sync.Kick(space.User.Id, account.Id, drafts.FullName);
                }, ct);
            }
            catch { MailQueue.Restore(space.Db, item); throw; }
            sync.Touch(space.User.Id);
            return Results.Ok();
        });

        api.MapPost("/{acc}/draft", async (string acc, HttpRequest request, HttpContext http, ImapPool pool, MailSync sync, CancellationToken ct) =>
        {
            var space = http.Space();
            var account = space.Account(acc);
            var (draft, files) = await ReadDraft(request, ct);
            var source = SourceOf(space, account, draft);
            MimeMessage message;
            try { message = MailCompose.Build(account, draft, await Attachments(space, source, draft, files, pool, ct)); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            var result = await pool.UseAsync(account, ImapPool.Ui, async c =>
            {
                var drafts = await MailSync.Special(c, space, account, "drafts", ct)
                    ?? throw new InvalidOperationException("Kein Entwürfe-Ordner gefunden.");
                var uid = await drafts.AppendAsync(new AppendRequest(message, MessageFlags.Draft | MessageFlags.Seen), ct);
                // Der alte Entwurf liegt im Quellkonto - im selben Konto gleich hier mit
                if (source.Id == account.Id) await RemoveDraft(c, space, account, draft.DraftUid, ct);
                sync.Kick(space.User.Id, acc, drafts.FullName);
                return new { uid = uid?.Id, folder = drafts.FullName };
            }, ct);
            if (source.Id != account.Id && draft.DraftUid != null)
                await pool.UseAsync(source, ImapPool.Ui, c => RemoveDraft(c, space, source, draft.DraftUid, ct), ct);
            return Results.Ok(result);
        }).DisableAntiforgery();
    }

    // ------------------------------------------------------------------

    /// <summary>Aus der Datenbank; fehlt der Inhalt noch (Sync nicht so weit, zu gross), vom Server.</summary>
    private static async Task<MimeMessage> Load(UserSpace space, MailAccount account, string folder, uint uid, ImapPool pool, CancellationToken ct)
    {
        if (MailStore.Raw(space.Db, account.Id, folder, uid) is { } raw) return MimeMessage.Load(new MemoryStream(raw));
        return await pool.UseAsync(account, ImapPool.Ui, async c =>
        {
            var f = await c.GetFolderAsync(folder, ct);
            await f.OpenAsync(FolderAccess.ReadOnly, ct);
            return await MailSync.Fetch(f, space, account, new UniqueId(uid), ct)
                   ?? throw new KeyNotFoundException("Die Mail gibt es auf dem Server nicht mehr.");
        }, ct);
    }

    /// <summary>Per SMTP senden, danach Kopie in Gesendet, Original markieren, Entwurf weg, Empfaenger merken.
    /// Ab dem SMTP-Versand ist die Mail draussen - was danach scheitert, kommt nur als Warnung zurueck.</summary>
    internal static async Task<List<string>> Deliver(UserSpace space, MailAccount account, MailAccount source, MailCompose.Draft draft,
        MimeMessage message, ImapPool pool, MailSync sync, ILogger? log, CancellationToken ct)
    {
        using (var smtp = await ImapPool.SmtpAsync(account, ct))
        {
            await smtp.SendAsync(message, ct);
            await smtp.DisconnectAsync(true, ct);
        }

        // Ab hier ist die Mail gesendet - scheitert der Rest, nur eine Warnung, kein Fehler
        // (sonst sendet ein erneuter Klick sie ein zweites Mal)
        var warnings = new List<string>();
        async Task After(string what, Func<Task> step)
        {
            try { await step(); }
            catch (Exception ex)
            {
                warnings.Add(what + ": " + ex.Message);
                log?.LogWarning("Senden {Account}: {What} fehlgeschlagen: {Error}", account.Email, what, ex.Message);
            }
        }

        if (account.SaveSentCopy)
            await After("Kopie in Gesendet", () => pool.UseAsync(account, ImapPool.Ui, async c =>
            {
                if (await MailSync.Special(c, space, account, "sent", CancellationToken.None) is not { } sent) return;
                message.Bcc.Clear();
                await sent.AppendAsync(new AppendRequest(message, MessageFlags.Seen), CancellationToken.None);
                sync.Kick(space.User.Id, account.Id, sent.FullName);
            }, CancellationToken.None));

        // Original und alter Entwurf liegen im Quellkonto
        if (draft.ReplyFolder is { } rf && draft.ReplyUid is uint ru)
            await After("Als beantwortet markieren", () => pool.UseAsync(source, ImapPool.Ui, async c =>
            {
                var f = await c.GetFolderAsync(rf, CancellationToken.None);
                await f.OpenAsync(FolderAccess.ReadWrite, CancellationToken.None);
                await f.StoreAsync(new[] { new UniqueId(ru) }, new StoreFlagsRequest(StoreAction.Add, MessageFlags.Answered) { Silent = true }, CancellationToken.None);
                MailStore.SetFlag(space.Db, source.Id, rf, new[] { ru }, "answered", true);
            }, CancellationToken.None));
        if (draft.ForwardFolder is { } ff && draft.ForwardUid is uint fu)
            await After("Als weitergeleitet markieren", () => pool.UseAsync(source, ImapPool.Ui, async c =>
            {
                var f = await c.GetFolderAsync(ff, CancellationToken.None);
                await f.OpenAsync(FolderAccess.ReadWrite, CancellationToken.None);
                await f.StoreAsync(new[] { new UniqueId(fu) }, new StoreFlagsRequest(StoreAction.Add, MessageFlags.None) { Silent = true, Keywords = { "$Forwarded" } }, CancellationToken.None);
                MailStore.SetFlag(space.Db, source.Id, ff, new[] { fu }, "forwarded", true);
            }, CancellationToken.None));
        if (draft.DraftUid != null)
            await After("Entwurf löschen", () => pool.UseAsync(source, ImapPool.Ui,
                c => RemoveDraft(c, space, source, draft.DraftUid, CancellationToken.None), CancellationToken.None));

        await After("Empfänger merken", () => { Pim.ContactStore.RememberRecipients(space, message); return Task.CompletedTask; });
        return warnings;
    }

    /// <summary>Fahne/gelesen: sofort in der Datenbank, zum IMAP-Server im Hintergrund.</summary>
    private static void SetFlags(UserSpace space, MailAccount account, MailOutbox outbox, string folder, List<uint> uids,
        bool? seen, bool? flagged)
    {
        if (seen is bool s2) MailStore.SetFlag(space.Db, account.Id, folder, uids, "seen", s2);
        if (flagged is bool f2) MailStore.SetFlag(space.Db, account.Id, folder, uids, "flagged", f2);
        outbox.Enqueue(space, account, folder, uids, removes: false, "flags", async (c, ct) =>
        {
            var f = await c.GetFolderAsync(folder, ct);
            await f.OpenAsync(FolderAccess.ReadWrite, ct);
            var ids = uids.Select(u => new UniqueId(u)).ToList();
            if (seen is bool s)
                await f.StoreAsync(ids, new StoreFlagsRequest(s ? StoreAction.Add : StoreAction.Remove, MessageFlags.Seen) { Silent = true }, ct);
            if (flagged is bool fl)
                await f.StoreAsync(ids, new StoreFlagsRequest(fl ? StoreAction.Add : StoreAction.Remove, MessageFlags.Flagged) { Silent = true }, ct);
        });
    }

    /// <summary>Verschieben: sofort aus dem Ordner in der Datenbank, zum IMAP-Server im Hintergrund;
    /// danach holt der Sync die Mail im Zielordner.</summary>
    private static void Move(UserSpace space, MailAccount account, MailOutbox outbox, MailSync sync,
        string folder, List<uint> uids, string target)
    {
        MailStore.Delete(space.Db, account.Id, folder, uids);
        sync.Touch(space.User.Id);
        outbox.Enqueue(space, account, folder, uids, removes: true, "move", async (c, ct) =>
        {
            var f = await c.GetFolderAsync(folder, ct);
            await f.OpenAsync(FolderAccess.ReadWrite, ct);
            await f.MoveToAsync(uids.Select(u => new UniqueId(u)).ToList(), await c.GetFolderAsync(target, ct), ct);
        }, after: () => sync.Kick(space.User.Id, account.Id, target));
    }

    private static async Task RemoveDraft(ImapClient c, UserSpace space, MailAccount account, uint? uid, CancellationToken ct)
    {
        if (uid is not uint du) return;
        if (await MailSync.Special(c, space, account, "drafts", ct) is not { } drafts) return;
        await drafts.OpenAsync(FolderAccess.ReadWrite, ct);
        await ExpungeOnly(c, drafts, new[] { new UniqueId(du) }, ct);
        MailStore.Delete(space.Db, account.Id, drafts.FullName, new[] { du });
    }

    /// <summary>Genau diese Mails endgueltig loeschen (Ordner zum Schreiben offen). Ohne UIDPLUS
    /// loescht EXPUNGE alles mit \Deleted - auch, was ein anderes Programm nur markiert hat. Deren
    /// Markierung daher kurz wegnehmen und danach wieder setzen.</summary>
    private static async Task ExpungeOnly(ImapClient c, IMailFolder f, IList<UniqueId> uids, CancellationToken ct)
    {
        if (uids.Count == 0) return;
        if (c.Capabilities.HasFlag(ImapCapabilities.UidPlus))
        {
            await f.StoreAsync(uids, new StoreFlagsRequest(StoreAction.Add, MessageFlags.Deleted) { Silent = true }, ct);
            await f.ExpungeAsync(uids, ct);
            return;
        }
        var mine = uids.Select(u => u.Id).ToHashSet();
        var others = (await f.SearchAsync(MailKit.Search.SearchQuery.Deleted, ct)).Where(u => !mine.Contains(u.Id)).ToList();
        if (others.Count > 0)
            await f.StoreAsync(others, new StoreFlagsRequest(StoreAction.Remove, MessageFlags.Deleted) { Silent = true }, ct);
        try
        {
            await f.StoreAsync(uids, new StoreFlagsRequest(StoreAction.Add, MessageFlags.Deleted) { Silent = true }, ct);
            await f.ExpungeAsync(ct);
        }
        finally
        {
            // UIDs bleiben beim Loeschen anderer Mails gleich. Auch bei Abbruch wieder setzen.
            if (others.Count > 0 && c.IsConnected)
                await f.StoreAsync(others, new StoreFlagsRequest(StoreAction.Add, MessageFlags.Deleted) { Silent = true }, CancellationToken.None);
        }
    }

    /// <summary>Konto der Original-Mail und des alten Entwurfs - ohne Angabe das Absenderkonto.</summary>
    private static MailAccount SourceOf(UserSpace space, MailAccount sender, MailCompose.Draft d) =>
        string.IsNullOrEmpty(d.SourceAccount) || d.SourceAccount == sender.Id ? sender : space.Account(d.SourceAccount);

    private static async Task<(MailCompose.Draft, List<IFormFile>)> ReadDraft(HttpRequest request, CancellationToken ct)
    {
        var form = await request.ReadFormAsync(ct);
        var draft = JsonSerializer.Deserialize<MailCompose.Draft>(form["json"].ToString(), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new ArgumentException("json fehlt");
        return (draft, form.Files.ToList());
    }

    private static async Task<List<(string, string, byte[])>> Attachments(UserSpace space, MailAccount account,
        MailCompose.Draft d, List<IFormFile> files, ImapPool pool, CancellationToken ct)
    {
        var result = new List<(string, string, byte[])>();
        foreach (var file in files)
        {
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            result.Add((file.FileName, file.ContentType ?? "application/octet-stream", ms.ToArray()));
        }
        if (d.ForwardFolder != null && d.ForwardUid is uint fa && d.ForwardAsAttachment)
        {
            var original = await Load(space, account, d.ForwardFolder, fa, pool, ct);
            using var ms = new MemoryStream();
            await original.WriteToAsync(ms, ct);
            var name = string.Concat((string.IsNullOrWhiteSpace(original.Subject) ? "Nachricht" : original.Subject).Split(Path.GetInvalidFileNameChars()));
            result.Add(((name.Length > 80 ? name[..80] : name) + ".eml", "message/rfc822", ms.ToArray()));
        }
        if (d.ForwardFolder != null && d.ForwardUid is uint fu && d.ForwardAttachments.Count > 0)
        {
            var original = await Load(space, account, d.ForwardFolder, fu, pool, ct);
            var parts = MailCompose.Parts(original);
            foreach (var i in d.ForwardAttachments.Where(i => i >= 0 && i < parts.Count))
                result.Add((parts[i].FileName ?? $"anhang-{i}", parts[i].ContentType.MimeType, MailCompose.Decode(parts[i])));
        }
        return result;
    }

    private static MessageDto Detail(string acc, string folder, uint uid, MimeMessage m, bool images, MailRow row, bool trusted = false)
    {
        var parts = MailCompose.Parts(m);
        string PartUrl(int i) => $"/api/mail/{Uri.EscapeDataString(acc)}/part?folder={Uri.EscapeDataString(folder)}&uid={uid}&index={i}";

        var cids = new HashSet<int>();
        var rawHtml = m.HtmlBody ?? (m.TextBody is { } t ? MailHtml.FromText(t) : "");
        var sanitized = MailHtml.Sanitize(rawHtml, images, cid =>
        {
            var i = parts.FindIndex(p => p.ContentId != null && p.ContentId.Trim('<', '>') == cid);
            if (i < 0) return null;
            cids.Add(i);
            return PartUrl(i);
        });

        var attachments = parts.Select((p, i) => new AttachmentDto(i,
                p.FileName ?? p.ContentType.Name ?? (p.ContentType.MimeType == "message/rfc822" ? "Nachricht.eml" : $"Teil {i + 1}"),
                p.ContentType.MimeType, Size(p), cids.Contains(i)))
            .Where(a => !a.Inline)
            .ToList();

        return new MessageDto(acc, uid, folder, m.Subject ?? "", Addrs(m.From).FirstOrDefault(), Addrs(m.To), Addrs(m.Cc),
            Addrs(m.Bcc), Addrs(m.ReplyTo), m.Date, sanitized.Html, sanitized.HasRemoteImages, MailStore.BodyText(m),
            attachments, m.MessageId, m.InReplyTo, m.References.ToList(), row.Seen, row.Flagged, row.Spam, row.Cached, images, trusted,
            Unsubscribe.Of(m));
    }

    private static long Size(MimePart p)
    {
        try
        {
            var stream = p.Content?.Stream;
            if (stream == null || !stream.CanSeek) return 0;
            return p.ContentTransferEncoding == ContentEncoding.Base64 ? stream.Length * 3 / 4 : stream.Length;
        }
        catch { return 0; }
    }

    private static List<Addr> Addrs(InternetAddressList? list) =>
        list?.Mailboxes.Select(a => new Addr(string.IsNullOrEmpty(a.Name) ? null : a.Name, a.Address)).ToList() ?? new();
}
