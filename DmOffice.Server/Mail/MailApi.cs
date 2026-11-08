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
        bool Seen, bool Flagged, double? Spam, bool Cached, bool ImagesAllowed, bool ImagesTrusted, Unsubscribe.Info? Unsubscribe = null,
        bool Junk = false, List<PhishCheck.Finding>? Phish = null,
        /** Kopfzeile List-Id (Newsletter, Mailinglisten) - fuer "Regel erstellen" */
        string? ListId = null,
        /** An diesen Catchall-Alias gegangen (CatchallAliases) - und ob die Antwort sie als Absender nehmen darf */
        string? Alias = null, bool AliasReply = false, string? AliasState = null,
        /** OpenPGP: "mime"/"inline" (signiert), "key" (Schlüssel dabei) - dann holt die Oberfläche /signature */
        string? Pgp = null);

    public record UidsBody(string Folder, List<uint> Uids);
    public record TrustBody(string Token, string Scope);
    public record FlagsBody(string Folder, List<uint> Uids, bool? Seen, bool? Flagged);
    public record MoveBody(string Folder, List<uint> Uids, string Target);
    public record FolderBody(string Path, string? NewName);
    public record ReclassifyBody(string Folder, List<uint>? Uids);
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
            http.Space().Settings.MailAccounts.Select(a => new { a.Id, a.Name, a.Email, a.DisplayName, a.Signature, a.Language, a.TrashDays, a.JunkDays, a.Storage }));

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

        api.MapGet("/{acc}/messages", (string acc, string folder, int? page, int? size, string? filter, string? sort, HttpContext http) =>
        {
            int n = Math.Clamp(size ?? 100, 1, 500), p = Math.Max(0, page ?? 0);
            var space = http.Space();
            // Alle Posteingaenge (App und Web): die Posteingaenge aller Konten zusammen, ohne Relevant/Sonstige -
            // die Konten koennen das unterschiedlich eingestellt haben
            if (folder == MailStore.UnifiedFolder)
            {
                var inboxes = space.Settings.MailAccounts.SelectMany(a => MailStore.Folders(space.Db, a.Id).Where(f => f.Special == "inbox").Select(f => a.Id + "|" + f.Path)).ToList();
                var (ut, uu, ui) = inboxes.Count == 0 ? (0, 0, new List<MailRow>()) : MailStore.Query(space.Db, new MailCriteria { Folders = inboxes }, p, n, filter is "focused" or "other" ? null : filter, sort);
                return Results.Ok(new { total = ut, unread = uu, page = p, size = n, items = CatchallAliases.Tag(space.Db, ui) });
            }
            // Suchordner und Kategorie-Ordner: ueber alle Konten
            if (MailStore.Virtual(space.Settings, folder) is { } criteria)
            {
                var (vt, _, vi) = MailStore.Query(space.Db, criteria, p, n, filter, sort);
                return Results.Ok(new { total = vt, page = p, size = n, items = CatchallAliases.Tag(space.Db, vi) });
            }
            // Prio-Posteingang: noch nicht Eingeordnetes erst einordnen
            if (filter is "focused" or "other") Focus.Backfill(space, acc, folder);
            var (total, items) = MailStore.List(space.Db, acc, folder, p, n, filter, sort);
            return Results.Ok(new { total, page = p, size = n, items = CatchallAliases.Tag(space.Db, items) });
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

        // Speichermodus "Schlank": den Text der Mails, deren Inhalt hier nicht liegt, beim Anbieter durchsuchen
        // (IMAP SEARCH). Die Oberflaeche zeigt zuerst die eigenen Treffer und haengt diese danach an.
        api.MapGet("/search/remote", async (string q, string? acc, string? folder, bool? deleted, HttpContext http, ImapPool pool, CancellationToken ct) =>
        {
            var space = http.Space();
            if (MailStore.ImapQuery(q) is not { } query) return Results.Ok(Array.Empty<MailRow>());
            var accounts = space.Settings.MailAccounts
                .Where(a => a.Slim && !string.IsNullOrEmpty(a.ImapHost) && (string.IsNullOrEmpty(acc) || a.Id == acc)).ToList();
            var until = DateTime.UtcNow + RemoteSearchBudget;
            var found = await Task.WhenAll(accounts.Select(a => SearchRemote(space, a, string.IsNullOrEmpty(folder) ? null : folder,
                deleted ?? true, query, until, pool, ct)));
            return Results.Ok(found.SelectMany(x => x).OrderByDescending(m => m.Date).Take(300));
        });

        api.MapGet("/{acc}/message", async (string acc, string folder, uint uid, bool? images, bool? markSeen,
            HttpContext http, ImapPool pool, MailOutbox outbox, CancellationToken ct) =>
        {
            var space = http.Space();
            var account = space.Account(acc);
            var row = MailStore.Row(space.Db, acc, folder, uid) ?? throw new KeyNotFoundException("Mail nicht gefunden.");
            var message = await Load(space, account, folder, uid, pool, ct);
            if (account.Slim) MailStore.Opened(space.Db, acc, folder, uid);
            // Sichere Absender: Bilder ohne Nachfrage
            var from = message.From.Mailboxes.FirstOrDefault()?.Address?.ToLowerInvariant() ?? "";
            var trusted = from.Length > 0 && space.Settings.TrustedImageSenders.Any(t =>
                t.StartsWith('@') ? from.EndsWith(t, StringComparison.OrdinalIgnoreCase) : from.Equals(t, StringComparison.OrdinalIgnoreCase));
            // Junk (Ordner oder Spamwert ueber der Grenze): alles gesperrt, auch fuer sichere Absender - nur von Hand freigeben
            var special = MailStore.Folder(space.Db, acc, folder)?.Special;
            var junk = special == "junk" || row.Spam >= space.Settings.Spam.SpamCutoff;
            if (trusted && !junk) images = true;
            if (markSeen == true && !row.Seen) SetFlags(space, account, outbox, folder, new() { uid }, seen: true, flagged: null);
            // Phishing-Merkmale (Warnbalken), nicht fuer eigene Mails
            var phish = special is "sent" or "drafts" ? null : PhishCheck.Check(space, account, message);
            // Virenfund beim Eingang: zuerst und deutlich
            if (MailVirus.Of(space.Db, MailVirus.KeyOf(message, acc, folder, uid)) is { } virus)
                (phish ??= []).Insert(0, new PhishCheck.Finding("virus", true, Detail: virus));
            var aliases = CatchallAliases.Load(space.Db);
            var alias = CatchallAliases.ActiveFor(aliases, acc) ? CatchallAliases.Of(aliases, message) : null;
            return Results.Ok(Detail(acc, folder, uid, message, images == true, row with { Seen = row.Seen || markSeen == true }, trusted && !junk, junk)
                with { Phish = phish, Alias = alias, AliasReply = alias != null && CatchallAliases.CanSendAs(space.Db, account, alias),
                    AliasState = alias != null ? CatchallAliases.StateOf(space.Db, alias) : null });
        });

        api.MapGet("/{acc}/part", async (string acc, string folder, uint uid, int index, bool? download,
            HttpContext http, ImapPool pool, CancellationToken ct) =>
        {
            var space = http.Space();
            var message = await Load(space, space.Account(acc), folder, uid, pool, ct);
            var parts = MailCompose.Parts(message);
            if (index < 0 || index >= parts.Count) return Results.NotFound();
            if (MailVirus.Of(space.Db, MailVirus.KeyOf(message, acc, folder, uid)) is { } virus)
                return Results.Json(new { error = $"Diese Mail enthält Schadsoftware ({virus}) – ihre Anhänge sind gesperrt." }, statusCode: 403);
            var part = parts[index];
            var name = part.FileName ?? part.ContentType.Name ?? $"anhang-{index}";
            var mime = ViewableMime(part.ContentType.MimeType, name);
            // Zum Ansehen in dmOffice nur harmlose Arten inline (Positivliste): Rasterbilder, PDF, Audio, Video - SVG, HTML und
            // alles andere als Download, damit kein Skript aus einer Mail unter dieser Adresse laeuft. nosniff gegen MIME-Raten.
            bool inline = download != true && mime != null;
            http.Response.Headers.XContentTypeOptions = "nosniff";
            // PDF ohne sandbox: sonst blockiert der Browser seinen eigenen PDF-Betrachter (wie bei den Dateien)
            if (inline && mime != "application/pdf") http.Response.Headers.ContentSecurityPolicy = "sandbox; default-src 'none'; media-src 'self'";
            return Results.File(MailCompose.Decode(part), inline ? mime! : "application/octet-stream", inline ? null : name, enableRangeProcessing: true);
        });

        // Anhang im Office-Server ansehen (Word, Excel, PowerPoint ...): als Kopie in den Systemordner der Dateiablage
        // („sys-dmoffice-mail“, je Mail und Anhang ein Unterordner, nach drei Tagen wieder weg), dann wie eine Datei oeffnen
        api.MapPost("/{acc}/part/office", async (string acc, PartBody body, HttpContext http, ImapPool pool, CancellationToken ct) =>
        {
            var space = http.Space();
            if (DmOffice.Dav.FileStore.FilesOff(space.Db)) return Results.Json(new { error = "Die Dateiablage ist für dieses Konto abgeschaltet." }, statusCode: 403);
            var message = await Load(space, space.Account(acc), body.Folder, body.Uid, pool, ct);
            var parts = MailCompose.Parts(message);
            if (body.Index < 0 || body.Index >= parts.Count) return Results.NotFound();
            var part = parts[body.Index];
            var name = string.Concat((part.FileName ?? part.ContentType.Name ?? $"anhang-{body.Index}").Split(Path.GetInvalidFileNameChars())).Trim();
            if (name.Length == 0) name = $"anhang-{body.Index}";
            return Results.Ok(new { path = MailAttachmentCopy.Store(space.Db, $"{acc}|{body.Folder}|{body.Uid}|{body.Index}|{message.MessageId}", name, MailCompose.Decode(part)) });
        });

        // Warum hat der Spamfilter so eingestuft? Die stärksten Merkmale je Richtung, Treffer der Spamlisten, wie viel gelernt ist
        api.MapGet("/{acc}/spam-why", async (string acc, string folder, uint uid, HttpContext http, ImapPool pool, CancellationToken ct) =>
        {
            var space = http.Space();
            var message = await Load(space, space.Account(acc), folder, uid, pool, ct);
            var account = space.Account(acc);
            var (score, junk, good, lists, nspam, nham) = SpamFilter.Explain(space.Db, message);
            // Feste Regeln obendrauf (SpamSignals): bestätigte Absender, Markenimitation, gefälschte Absenderadresse
            var (final, rule, sig) = SpamSignals.Judge(space, account, message, nspam > 0 && nham > 0 ? score : null);
            var (known, knownRule) = SpamSignals.Trusted(space, message, sig);
            return Results.Ok(new
            {
                // Alle Prüfungen, nicht nur die Wörter: Absenderbestätigung, bekannter Absender, Verteiler, Markenimitation,
                // Meldungen anderer, Erkennungsregeln, Urteil des eigenen Mailservers
                checks = new
                {
                    dmarc = sig.Dmarc, dkim = sig.DkimDomains, known = known ? knownRule : null, list = sig.List,
                    obfuscated = sig.Obfuscated, randomId = sig.RandomId, forwarded = sig.Forwarded?.Service, freemailOrg = sig.FreemailOrg, notToMe = sig.NotToMe,
                    server = SpamHeaders.Tokens(message).ToList(),
                },
                score = Math.Round(score, 2), junk, good, nspam, nham,
                final = final is double f ? Math.Round(f, 2) : (double?)null, rule, verified = sig.VerifiedDomain,
                alias = rule is "alias-blocked" or "alias-unknown" ? CatchallAliases.Of(CatchallAliases.Load(space.Db), message) : null, shared = sig.SharedReports, custom = sig.StrongestRule?.Name, rules = sig.Rules.Select(x => new { id = x.Id, name = x.Name }),
                lists = lists.Select(id => new { id, name = Blocklists.All.FirstOrDefault(l => l.Id == id)?.Name ?? id }),
            });
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
            if (!space.Settings.FocusOn(space.Account(acc))) return Results.Ok(new { enabled = false, focused = 0, other = 0 });
            Focus.Backfill(space, acc, folder);
            var (focused, other) = MailStore.FocusUnread(space.Db, acc, folder);
            var (focusedTotal, otherTotal) = MailStore.FocusTotal(space.Db, acc, folder);
            return Results.Ok(new { enabled = true, focused, other, focusedTotal, otherTotal });
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
            var account = space.Account(acc);
            // Aus Junk heraus (nicht in den Papierkorb) lernt "kein Junk", in den Junk-Ordner hinein lernt "Junk" -
            // wie die Knoepfe, damit Ziehen und Verschieben genauso wirken
            var from = MailStore.Folder(space.Db, acc, body.Folder)?.Special;
            var to = MailStore.Folder(space.Db, acc, body.Target)?.Special;
            bool? spam = from == "junk" && to is not ("junk" or "trash") ? false : to == "junk" && from != "junk" ? true : null;
            // Komfort: Archivieren heißt „das war in Ordnung“ - als „Kein Junk“ lernen (abschaltbar unter Junk-E-Mail)
            if (spam == null && to == "archive" && from is not ("junk" or "archive") && space.Settings.Spam.Enabled && space.Settings.Spam.LearnArchive)
            {
                spam = false;
                foreach (var uid in body.Uids) MailStore.SetSpamScore(space.Db, acc, body.Folder, uid, 0);
            }
            // Aus Junk in den Papierkorb gezogen: wie Löschen im Junk-Ordner als Junk lernen
            if (from == "junk" && to == "trash") await LearnDeletedJunk(space, account, body.Folder, body.Uids, pool, ct);
            if (spam is bool isSpam)
                foreach (var uid in body.Uids)
                    try
                    {
                        var m = await Load(space, account, body.Folder, uid, pool, ct);
                        SpamFilter.Train(space.Db, m, spam: isSpam);
                        // Gemeinsames Junk-Wissen: melden bzw. die eigene Meldung zurücknehmen
                        if (isSpam) JunkPrints.Shared?.Report(space, m); else JunkPrints.Shared?.Withdraw(space, m);
                    }
                    catch (Exception) { /* Lernen ist Zugabe - das Verschieben selbst darf daran nicht scheitern */ }
            Move(space, account, outbox, sync, body.Folder, body.Uids, body.Target);
            return Results.Ok();
        });

        // Neu einordnen (nur Posteingang): Junk-Erkennung, Relevant/Sonstige, Kategorien für Mails, die schon da waren - im Hintergrund,
        // der Fortschritt steht unter „Aktivität“. Ohne Auswahl die neuesten Mails des Posteingangs.
        api.MapPost("/{acc}/reclassify", (string acc, ReclassifyBody body, HttpContext http, MailSync sync, ImapPool pool, ILogger<MailSync> log) =>
        {
            var space = http.Space();
            var account = space.Account(acc);
            if (MailStore.Folder(space.Db, acc, body.Folder)?.Special != "inbox") return Results.BadRequest(new { error = "Neu einordnen geht nur im Posteingang." });
            var count = body.Uids?.Count ?? Math.Min(MailSync.ReclassifyMax, MailStore.Uids(space.Db, acc, body.Folder).Count);
            _ = Task.Run(async () =>
            {
                try { await sync.Reclassify(space, account, body.Uids, pool, CancellationToken.None); }
                catch (Exception ex) { log.LogWarning("Neu einordnen {Account}: {Error}", LogNames.Of(space, account), ex.Message); }
            });
            return Results.Ok(new { count });
        });

        api.MapPost("/{acc}/delete", async (string acc, UidsBody body, HttpContext http, ImapPool pool, MailSync sync, MailOutbox outbox, CancellationToken ct) =>
        {
            var space = http.Space();
            var account = space.Account(acc);
            var trash = MailStore.Folders(space.Db, acc).FirstOrDefault(f => f.Special == "trash");
            // Im Junk-Ordner gelöscht heißt „das ist Müll“: als Junk lernen
            if (MailStore.Folder(space.Db, acc, body.Folder)?.Special == "junk") await LearnDeletedJunk(space, account, body.Folder, body.Uids, pool, ct);
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
            {
                var m = await Load(space, account, body.Folder, uid, pool, ct);
                SpamFilter.Train(space.Db, m, spam: true);
                // Gemeinsames Junk-Wissen (wenn eingeschaltet): Fingerabdruck melden - schützt die anderen im Haushalt
                JunkPrints.Shared?.Report(space, m);
            }
            var junk = MailStore.Folders(space.Db, acc).FirstOrDefault(f => f.Special == "junk")
                ?? throw new InvalidOperationException("Kein Junk-Ordner gefunden.");
            if (junk.Path != body.Folder) Move(space, account, outbox, sync, body.Folder, body.Uids, junk.Path);
            return Results.Ok();
        });

        api.MapPost("/{acc}/notjunk", async (string acc, UidsBody body, HttpContext http, ImapPool pool, MailSync sync, MailOutbox outbox, CancellationToken ct) =>
        {
            var space = http.Space();
            var account = space.Account(acc);
            var inJunk = MailStore.Folder(space.Db, acc, body.Folder)?.Special == "junk";
            // Erst den Wert, dann lernen: Lernen lädt die Mails (dauert) - eine Liste, die solange neu lädt, zeigte sonst weiter „unsicher“
            if (!inJunk) foreach (var uid in body.Uids) MailStore.SetSpamScore(space.Db, acc, body.Folder, uid, 0);
            foreach (var uid in body.Uids)
            {
                var m = await Load(space, account, body.Folder, uid, pool, ct);
                SpamFilter.Train(space.Db, m, spam: false);
                JunkPrints.Shared?.Withdraw(space, m);
            }
            if (inJunk)
            {
                var inbox = MailStore.Folders(space.Db, acc).First(f => f.Special == "inbox");
                Move(space, account, outbox, sync, body.Folder, body.Uids, inbox.Path);
            }
            return Results.Ok();
        });

        // Sichere Absender nach „Kein Junk“: was sich eintragen ließe (Adresse, Domain, verifiziert, Freemail) und eintragen
        api.MapGet("/{acc}/trust", async (string acc, string folder, uint uid, HttpContext http, ImapPool pool, CancellationToken ct) =>
        {
            var space = http.Space();
            var account = space.Account(acc);
            var m = await Load(space, account, folder, uid, pool, ct);
            var check = SafeList.Inspect(space.Db, m, SpamSignals.Of(m, [account.ImapHost, PhishCheck.DomainOf(account.Email)]));
            return new { check.Address, check.Domain, check.Verified, check.Freemail, check.AddressListed, check.DomainListed, token = SafeList.Remember(space.User.Id, check) };
        });
        api.MapPost("/trust", (TrustBody body, HttpContext http) =>
        {
            var space = http.Space();
            var check = SafeList.Recall(space.User.Id, body.Token) ?? throw new ArgumentException("Das ist schon zu lange her – bitte die Mail noch einmal öffnen.");
            return Results.Ok(new { entry = SafeList.Add(space.Db, check, body.Scope) });
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
            var account = space.Account(acc);
            // Microsoft-Konten mit Graph: Outlook-Kategorien statt IMAP-Schlagworte (setzt auch lokal)
            if (GraphCategories.Uses(account)) await GraphCategories.SetAsync(space, account, body.Folder, body.Uids, key, body.On, ct);
            else await pool.UseAsync(account, ImapPool.Ui, async c =>
            {
                var f = await c.GetFolderAsync(body.Folder, ct);
                await f.OpenAsync(FolderAccess.ReadWrite, ct);
                // Nimmt der Server keine eigenen Schlagworte an (z. B. Gmail), meldet er das hier
                if (!f.PermanentFlags.HasFlag(MessageFlags.UserDefined) && !f.PermanentKeywords.Contains(key))
                    throw new InvalidOperationException(account.Auth == MailAuth.Microsoft ? GraphCategories.NotConnected
                        : "Dieser Mailserver speichert keine Kategorien (eigene IMAP-Schlagworte).");
                var req = new StoreFlagsRequest(body.On ? StoreAction.Add : StoreAction.Remove, MessageFlags.None) { Silent = true };
                req.Keywords.Add(key);
                await f.StoreAsync(body.Uids.Select(u => new UniqueId(u)).ToList(), req, ct);
            }, ct);
            if (!GraphCategories.Uses(account)) MailStore.SetKeyword(space.Db, acc, body.Folder, body.Uids, key, body.On);
            // Lernende Kategorie: von Hand gesetzt = gehört dazu, entfernt = gehört nicht dazu
            if (Settings.Categories.Of(space.Settings).Any(c => c.Key == key && c.Smart))
                foreach (var uid in body.Uids)
                    if (SmartInbox.OfStored(space, acc, body.Folder, uid) is { } doc)
                        SmartInbox.Train(space.Db, doc, key, body.On);
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
            // Als Catchall-Alias senden: nur, wenn das eingeschaltet und die Adresse bekannt ist
            if (!string.IsNullOrEmpty(draft.From) && !CatchallAliases.CanSendAs(space.Db, account, draft.From))
                return Results.BadRequest(new { error = "Als diese Adresse kann aus diesem Postfach nicht gesendet werden." });
            var source = SourceOf(space, account, draft);
            // Anhaenge als Freigabelink: Links anlegen und als Abschnitt unter den Text
            try { draft.Html += await MailLinks.CreateAsync(http, space, account, draft, files, ct); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (Dav.QuotaExceededException ex) { return Results.Json(new { error = ex.Message }, statusCode: 507); }
            MimeMessage message;
            // Ungueltige Adresse: 400 mit Meldung, wie die anderen Eingabefehler
            try
            {
                message = MailCompose.Build(account, draft, await Attachments(space, source, draft, files, pool, ct, http.RequestServices.GetRequiredService<ServerConfig>()));
                OpenPgpMail.Outgoing(space.Db, account, message, draft.Sign ?? OpenPgpKeys.SignsByDefault(space.Db, account.Id));
            }
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
            if (!string.IsNullOrEmpty(draft.From) && !CatchallAliases.CanSendAs(space.Db, account, draft.From)) draft.From = null;
            var source = SourceOf(space, account, draft);
            MimeMessage message;
            try { message = MailCompose.Build(account, draft, await Attachments(space, source, draft, files, pool, ct, http.RequestServices.GetRequiredService<ServerConfig>())); }
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

    /// <summary>So lange sucht /search/remote hoechstens - neue Ordner werden danach nicht mehr begonnen.</summary>
    private static readonly TimeSpan RemoteSearchBudget = TimeSpan.FromSeconds(8);

    /// <summary>IMAP SEARCH in den Ordnern eines Kontos, in denen Inhalte fehlen; nur Treffer ohne Inhalt hier
    /// (die anderen findet schon die eigene Suche). Fehler beim Anbieter: einfach keine weiteren Treffer.</summary>
    private static async Task<List<MailRow>> SearchRemote(UserSpace space, MailAccount account, string? folder, bool withDeleted,
        MailKit.Search.SearchQuery query, DateTime until, ImapPool pool, CancellationToken ct)
    {
        var missing = space.Db.Query("SELECT DISTINCT folder FROM mail WHERE account=$a AND cached=0", r => r.GetString(0), ("$a", account.Id)).ToHashSet();
        var folders = MailStore.Folders(space.Db, account.Id)
            .Where(f => f.Selectable && f.Special != "all" && missing.Contains(f.Path)
                        && (folder != null ? f.Path == folder : withDeleted || f.Special is not ("trash" or "junk")))
            .OrderBy(f => f.Special switch { "inbox" => 0, "sent" => 1, "archive" => 2, null => 3, _ => 4 }).ToList();
        var hits = new List<MailRow>();
        if (folders.Count == 0) return hits;
        try
        {
            await pool.UseAsync(account, ImapPool.Ui, async c =>
            {
                foreach (var row in folders)
                {
                    if (DateTime.UtcNow > until) break;
                    var f = await c.GetFolderAsync(row.Path, ct);
                    await f.OpenAsync(FolderAccess.ReadOnly, ct);
                    var uids = await f.SearchAsync(query, ct);
                    if (uids.Count > 0) hits.AddRange(MailStore.NotCached(space.Db, account.Id, row.Path, uids.Select(u => u.Id)));
                }
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Anbieter nicht erreichbar oder kein SEARCH fuer diesen Ordner: bleibt bei den eigenen Treffern
        }
        return hits;
    }

    /// <summary>Aus der Datenbank; fehlt der Inhalt noch (Sync nicht so weit, zu gross), vom Server.</summary>
    public record PartBody(string Folder, uint Uid, int Index);

    /// <summary>Art zum Ansehen in dmOffice (Positivliste), aus dem MIME-Typ oder - bei „application/octet-stream“ - der Endung; sonst null.</summary>
    internal static string? ViewableMime(string mime, string name)
    {
        var m = mime.ToLowerInvariant();
        if (m == "application/octet-stream" || m.Length == 0)
            m = Path.GetExtension(name).ToLowerInvariant() switch
            {
                ".pdf" => "application/pdf", ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png", ".gif" => "image/gif", ".webp" => "image/webp",
                ".mp3" => "audio/mpeg", ".m4a" => "audio/mp4", ".ogg" or ".opus" => "audio/ogg", ".wav" => "audio/wav", ".mp4" or ".m4v" => "video/mp4", ".webm" => "video/webm",
                _ => m,
            };
        return m is "image/png" or "image/jpeg" or "image/gif" or "image/webp" or "image/bmp" or "application/pdf"
            or "audio/mpeg" or "audio/mp4" or "audio/ogg" or "audio/wav" or "audio/x-wav" or "audio/webm" or "video/mp4" or "video/webm" or "video/quicktime" ? m : null;
    }

    internal static async Task<MimeMessage> Load(UserSpace space, MailAccount account, string folder, uint uid, ImapPool pool, CancellationToken ct)
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
            // Antwort des Mailservers festhalten: "250 ... queued as ..." heisst angenommen - mehr kann der Absender nicht wissen
            smtp.MessageSent += (_, e) => log?.LogInformation("Senden {Account}: Mailserver hat angenommen ({Count} Empfänger): {Response}",
                LogNames.Of(space, account), message.To.Mailboxes.Count() + message.Cc.Mailboxes.Count() + message.Bcc.Mailboxes.Count(), e.Response);
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
                log?.LogWarning("Senden {Account}: {What} fehlgeschlagen: {Error}", LogNames.Of(space, account), what, ex.Message);
            }
        }

        if (account.SaveSentCopy)
            await After("Kopie in Gesendet", () => pool.UseAsync(account, ImapPool.Ui, async c =>
            {
                if (await MailSync.Special(c, space, account, "sent", CancellationToken.None) is not { } sent) return;
                message.Bcc.Clear();
                await sent.AppendAsync(new AppendRequest(message, MessageFlags.Seen), CancellationToken.None);
                // Inhalt merken: kommt die Kopie beim Abgleich an, liegt sie gleich vollstaendig hier
                SentCache.Remember(space.User.Id, account.Id, sent.FullName, message, account.MaxCachedMb * 1024L * 1024L);
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
    internal static void SetFlags(UserSpace space, MailAccount account, MailOutbox outbox, string folder, List<uint> uids,
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

    /// <summary>Im Junk-Ordner gelöschte Mails als Junk lernen (JunkLearn.LearnDeleted). Lernen ist Zugabe - das Löschen darf
    /// daran nicht scheitern.</summary>
    private static async Task LearnDeletedJunk(UserSpace space, MailAccount account, string folder, List<uint> uids, ImapPool pool, CancellationToken ct)
    {
        foreach (var uid in uids)
            try { JunkLearn.LearnDeleted(space, await Load(space, account, folder, uid, pool, ct)); }
            catch (Exception) { }
    }

    /// <summary>Verschieben: sofort aus dem Ordner in der Datenbank, zum IMAP-Server im Hintergrund;
    /// danach holt der Sync die Mail im Zielordner.</summary>
    internal static void Move(UserSpace space, MailAccount account, MailOutbox outbox, MailSync sync,
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
        MailCompose.Draft d, List<IFormFile> files, ImapPool pool, CancellationToken ct, ServerConfig? config = null)
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
        // Aus der Dateiablage: nur eigene Dateien, bei abgeschalteter Dateiablage nur der notizi-Ordner
        long fromFiles = 0;
        foreach (var raw in d.FilePaths.Distinct())
        {
            // Foto aus einem Galerie-Ordner (externer Ordner, „ext:Quelle/Pfad“): vom externen Ordner gelesen
            if (raw.StartsWith("ext:") && config != null)
            {
                var (src, rel) = DmOffice.Gallery.GalleryFolders.Resolve(space, config, raw[4..]);
                var (driver, _) = DmOffice.Storage.ExternalFolders.Open(space, config, src.Mount);
                using (driver)
                {
                    await using var es = await driver.OpenReadAsync(rel, ct);
                    using var ems = new MemoryStream();
                    await es.CopyToAsync(ems, ct);
                    fromFiles += ems.Length;
                    if (fromFiles > MaxFilesAttached) throw new ArgumentException($"Zusammen höchstens {MaxFilesAttached / 1024 / 1024} MB aus der Dateiablage - für Größeres einen Link teilen.");
                    var ename = rel[(rel.LastIndexOf('/') + 1)..];
                    result.Add((ename, Dav.DavServer.Mime(ename), ems.ToArray()));
                }
                continue;
            }
            var path = Dav.FileStore.Normalize(raw);
            if (space.User.FilesOff && !Dav.FileStore.InRoot(path, space.Settings.Notizi.Root)) throw new ArgumentException("Die Dateiablage ist abgeschaltet.");
            var entry = Dav.FileStore.Get(space.Db, path);
            if (entry == null || entry.IsDir) throw new ArgumentException($"Datei nicht gefunden: {raw}");
            fromFiles += entry.Size;
            if (fromFiles > MaxFilesAttached) throw new ArgumentException($"Zusammen höchstens {MaxFilesAttached / 1024 / 1024} MB aus der Dateiablage - für Größeres einen Link teilen.");
            using var stream = Dav.FileStore.OpenRead(space.Db, path);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct);
            result.Add((entry.Name, entry.Mime ?? Dav.DavServer.Mime(entry.Name), ms.ToArray()));
        }
        return result;
    }

    /// <summary>Hoechstens so viel aus der Dateiablage je Mail (die meisten Anbieter nehmen nicht mehr an).</summary>
    private const long MaxFilesAttached = 40L * 1024 * 1024;

    private static MessageDto Detail(string acc, string folder, uint uid, MimeMessage m, bool images, MailRow row, bool trusted = false, bool junk = false)
    {
        var parts = MailCompose.Parts(m);
        string PartUrl(int i) => $"/api/mail/{Uri.EscapeDataString(acc)}/part?folder={Uri.EscapeDataString(folder)}&uid={uid}&index={i}";

        var cids = new HashSet<int>();
        var rawHtml = MailCompose.InlineSequence(m, parts, cids) ?? m.HtmlBody ?? (m.TextBody is { } t ? MailHtml.FromText(t) : "");
        var sanitized = MailHtml.Sanitize(rawHtml, images, cid =>
        {
            // Bilder aus InlineSequence (ohne eigene Content-ID): Nummer des Teils
            if (cid.StartsWith(MailCompose.SequencePart, StringComparison.Ordinal) && int.TryParse(cid[MailCompose.SequencePart.Length..], out var n) && n >= 0 && n < parts.Count)
                return PartUrl(n);
            var i = parts.FindIndex(p => p.ContentId != null && p.ContentId.Trim('<', '>') == cid);
            if (i < 0) return null;
            cids.Add(i);
            return PartUrl(i);
        }, junk);

        var attachments = parts.Select((p, i) => new AttachmentDto(i,
                p.FileName ?? p.ContentType.Name ?? (p.ContentType.MimeType == "message/rfc822" ? "Nachricht.eml" : $"Teil {i + 1}"),
                p.ContentType.MimeType, Size(p), cids.Contains(i)))
            .Where(a => !a.Inline && a.Mime != "application/pgp-signature")
            .ToList();

        return new MessageDto(acc, uid, folder, m.Subject ?? "", Addrs(m.From).FirstOrDefault(), Addrs(m.To), Addrs(m.Cc),
            Addrs(m.Bcc), Addrs(m.ReplyTo), m.Date, sanitized.Html, sanitized.HasRemoteImages, MailStore.BodyText(m),
            attachments, m.MessageId, m.InReplyTo, m.References.ToList(), row.Seen, row.Flagged, row.Spam, row.Cached, images, trusted,
            Unsubscribe.Of(m), junk, ListId: m.Headers[HeaderId.ListId], Pgp: Features.On(Features.Pgp) ? OpenPgpMail.Hint(m) : null);
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
