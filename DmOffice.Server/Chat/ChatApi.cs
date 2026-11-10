using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Notes;

namespace DmOffice.Chat;

/// <summary>
/// Web-API des Chats (/api/chat): Unterhaltungen, Nachrichten, Warten auf Neues und die Verweise auf Dateien und
/// Notizen. Ein Verweis zeigt immer den aktuellen Stand beim Besitzer; ist das Original weg, gibt es 404.
/// Wer in der Unterhaltung ist, darf genau die Dateien und Notizen lesen, auf die ihre Nachrichten zeigen.
/// </summary>
public static partial class ChatApi
{
    /// <summary>Ein Mitglied: Gruppe mit Titel; ein Mitglied ohne Titel: die Unterhaltung zu zweit.</summary>
    private sealed record NewRoom(List<string> Members, string? Title);
    /// <summary>Color: #RRGGBB oder "" fuer die Akzentfarbe (persoenlich).</summary>
    private sealed record RoomPatch(string? Title, bool? Muted, string? Color);
    private sealed record UsersBody(List<string> Users);
    private sealed record AdminBody(bool Admin);
    /// <summary>Verweis beim Senden: eigene Datei (fileId) oder eigene Notiz (noteId); Mode show|send, ohne: Vorgabe aus den Einstellungen.</summary>
    /// Termin/Aufgabe/Kontakt: collection + item (Uri); Ordner: fileId des Ordners.
    private sealed record RefInput(string Type, long? FileId, long? NoteId, string? Mode = null, long? Collection = null, string? Item = null);
    /// <summary>Eigenen Verweis aendern: Art umstellen und/oder zurueckziehen.</summary>
    private sealed record RefPatch(string? Mode, bool? Revoke);
    /// <summary>Weiterleiten in Unterhaltungen; Mode show|send fuer alle Verweise (ohne: wie das Original, wenn erlaubt); Text mitnehmen.</summary>
    private sealed record ForwardBody(List<long> Rooms, string? Mode, bool? WithText);
    private sealed record RightsBody(bool Expiring, bool LocationRequests);
    private sealed record PostBody(string? Text, long? ReplyTo, List<RefInput>? Refs, string? Zone = null, string? Lang = null);
    private sealed record EditBody(string Text);
    private sealed record RetentionBody(string Value);
    private sealed record ReactBody(string Emoji);
    private sealed record ReadBody(long UpTo);
    /// <summary>Datei: Ordner in der eigenen Ablage (Vorgabe "Chat"); Notiz: Notizordner (Vorgabe: Eingang).</summary>
    private sealed record SaveBody(string? Folder);

    /// <summary>Ordner in der Dateiablage fuer Bilder und Dateien, die direkt im Chat hochgeladen werden.</summary>
    public const string UploadFolder = "Chat";

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api/chat").RequireAuthorization();

        // ---------------- Stand und Warten ----------------

        api.MapGet("/status", (HttpContext http, ChatStore chat) =>
        {
            var space = http.Space();
            var me = space.User.Id;
            // me: eigene Kennung - die App erkennt daran ihre Nachrichten (auth/state ist mit App-Passwort kein sicherer Weg)
            // quiet: Ruhezeit - null (alles melden), "mentions" (nur Erwaehnungen), "all" (nichts)
            return new { seq = chat.Seq(me), unread = chat.Unread(me), me, quiet = ChatSettings.Of(space.Db).QuietNow(space), avatar = LookOf(me).Avatar, accent = LookOf(me).Accent, pukkiFigure = PukkiFigure(space.Db) };
        });

        // Long-Poll: kommt sofort zurueck, wenn es Neueres als since gibt, sonst spaetestens nach timeout Sekunden
        // busy: Stand von „denkt nach“ (Pukki fragt die KI) - wer ihn mitschickt, wird auch bei dessen Aenderung geweckt
        api.MapGet("/wait", async (long since, int? timeout, long? busy, HttpContext http, ChatStore chat, CancellationToken ct) =>
        {
            var me = Me(http);
            var seq = await chat.WaitAsync(me, since, TimeSpan.FromSeconds(Math.Clamp(timeout ?? 25, 1, 50)), ct, busy ?? -1);
            var b = chat.Busy(me);
            return new { seq, busy = b.Version, busyRooms = b.Rooms };
        });

        api.MapGet("/changes", (long? since, HttpContext http, ChatStore chat, UserStore users) =>
        {
            var me = Me(http);
            var c = chat.Changes(me, since ?? 0);
            // Zustellung: was dieses Gerät gerade abholt
            chat.MarkDelivered(me, c.Messages.Select(m => (m.Room, m.Id)).Concat(c.Rooms.Where(r => r.Last != null).Select(r => (r.Id, r.Last!.Id))));
            return new { c.Seq, rooms = c.Rooms.Select(r => RoomDto(r, me, users, chat)), messages = c.Messages.Select(m => MessageDto(m, users, chat)), c.More };
        });

        // Wen man anschreiben kann: alle anderen Benutzer des Servers
        api.MapGet("/people", (HttpContext http, UserStore users) =>
        {
            var me = Me(http);
            return users.All().Where(u => u.Id != me).OrderBy(u => u.DisplayName)
                .Select(u => { var look = LookOf(u.Id); return new { u.Id, u.Login, u.DisplayName, look.Avatar, look.Accent }; });
        });

        // ---------------- Unterhaltungen ----------------

        // Wohin Pukki Fragen weitergibt, die er nicht selbst beantworten kann (für den Hinweis im Chat)
        api.MapGet("/pukki/ai", (HttpContext http, ServerConfig config, UserStore users) =>
        {
            var on = PukkiAi.Allowed(config, users.ById(Me(http)));
            return new { on, service = on ? PukkiAi.ServiceName(config) : null };
        });

        api.MapGet("/rooms", (string? lang, HttpContext http, ChatStore chat, UserStore users) =>
        {
            var me = Me(http);
            // Pukki (Stufe 5): die Unterhaltung mit ihm gibt es von Anfang an
            Pukki.Ensure(chat, me, Pukki.WantsEnglish(http.Space(), lang));
            var rooms = chat.Rooms(me);
            chat.MarkDelivered(me, rooms.Where(r => r.Last != null).Select(r => (r.Id, r.Last!.Id)));
            return rooms.Select(r => RoomDto(r, me, users, chat));
        });

        api.MapGet("/rooms/{id:long}", (long id, HttpContext http, ChatStore chat, UserStore users) =>
        {
            var me = Me(http);
            var room = chat.Room(me, id);
            if (room.Last != null) chat.MarkDelivered(me, [(room.Id, room.Last.Id)]);
            return RoomDto(room, me, users, chat);
        });

        api.MapPost("/rooms", (NewRoom body, HttpContext http, ChatStore chat, UserStore users) =>
        {
            var me = Me(http);
            var members = (body.Members ?? new()).Where(m => m != me).Distinct().ToList();
            foreach (var m in members)
                if (users.ById(m) == null) throw new ArgumentException("Unbekannter Benutzer.");
            long id;
            if (members.Count == 1 && string.IsNullOrWhiteSpace(body.Title)) id = chat.OpenDirect(me, members[0]);
            else if (members.Count == 0) throw new ArgumentException("Wen möchtest du anschreiben?");
            else id = chat.CreateGroup(me, body.Title ?? "", members);
            return RoomDto(chat.Room(me, id), me, users, chat);
        });

        api.MapPut("/rooms/{id:long}", (long id, RoomPatch body, HttpContext http, ChatStore chat, UserStore users) =>
        {
            var me = Me(http);
            if (body.Title != null) chat.Rename(me, id, body.Title);
            if (body.Muted is { } muted) chat.Mute(me, id, muted);
            if (body.Color != null) chat.SetColor(me, id, body.Color.Length == 0 ? null : body.Color);
            return RoomDto(chat.Room(me, id), me, users, chat);
        });

        api.MapPost("/rooms/{id:long}/members", (long id, UsersBody body, HttpContext http, ChatStore chat, UserStore users) =>
        {
            var me = Me(http);
            foreach (var u in body.Users ?? new())
                if (users.ById(u) == null) throw new ArgumentException("Unbekannter Benutzer.");
            chat.AddMembers(me, id, body.Users ?? new());
            GroupSpaces.Sync(http.RequestServices, id);
            return RoomDto(chat.Room(me, id), me, users, chat);
        });

        api.MapDelete("/rooms/{id:long}/members/{user}", (long id, string user, HttpContext http, ChatStore chat) =>
        {
            chat.RemoveMember(Me(http), id, user);
            GroupSpaces.Left(http.RequestServices, id, user);
            return Results.Ok();
        });

        api.MapPut("/rooms/{id:long}/admins/{user}", (long id, string user, AdminBody body, HttpContext http, ChatStore chat) =>
        {
            chat.SetAdmin(Me(http), id, user, body.Admin);
            return Results.Ok();
        });

        api.MapPost("/rooms/{id:long}/leave", (long id, HttpContext http, ChatStore chat) =>
        {
            var me = Me(http);
            chat.RemoveMember(me, id, me);
            GroupSpaces.Left(http.RequestServices, id, me);
            return Results.Ok();
        });

        api.MapPost("/rooms/{id:long}/read", (long id, ReadBody body, HttpContext http, ChatStore chat) =>
        {
            chat.MarkRead(Me(http), id, body.UpTo);
            return Results.Ok();
        });

        // ---------------- Nachrichten ----------------

        api.MapGet("/rooms/{id:long}/messages", (long id, long? before, int? limit, HttpContext http, ChatStore chat, UserStore users) =>
        {
            var me = Me(http);
            var list = chat.Messages(me, id, before, limit ?? 50);
            if (list.Count > 0) chat.MarkDelivered(me, [(id, list.Max(m => m.Id))]);
            return list.Select(m => MessageDto(m, users, chat));
        });

        // Suche im Chat (Chat-Komfort): Treffer mit ihrer Unterhaltung
        api.MapGet("/search", (string q, long? room, HttpContext http, ChatStore chat, UserStore users) =>
        {
            var me = Me(http);
            var titles = chat.Rooms(me).ToDictionary(r => r.Id, r => TitleOf(r, me, users));
            return chat.Search(me, q, room).Where(m => titles.ContainsKey(m.Room))
                .Select(m => new { room = m.Room, roomTitle = titles[m.Room], message = MessageDto(m, users, chat) });
        });

        api.MapPost("/rooms/{id:long}/messages", (long id, PostBody body, HttpContext http, ChatStore chat, UserStore users) =>
        {
            var space = http.Space();
            var settings = ChatSettings.Of(space.Db);
            var rights = chat.Rights(space.User.Id);
            // Alben: immer nur zeigen
            var refs = (body.Refs ?? new()).Select(r => WithMode(Resolve(space, r),
                r.Type == ChatRef.TypeAlbum ? ChatRef.Show : r.Mode ?? (r.Type is ChatRef.TypeFile or ChatRef.TypeFolder ? settings.FilesMode : settings.ItemsMode), settings, rights)).ToList();
            Pukki.FromDevice(space.User.Id, body.Zone, body.Lang);
            var msg = chat.Post(space.User.Id, id, body.Text, body.ReplyTo, refs, MentionsOf(body.Text, id, chat, users));
            return MessageDto(chat.Message(space.User.Id, msg), users, chat);
        });

        // Bild oder Datei direkt aus dem Chat (Handy, Einfuegen, Ziehen): landet in der eigenen Ablage unter "Chat",
        // die Nachricht zeigt darauf. Inhalt roh im Koerper (kein Formular - das laege sonst unverschluesselt in /tmp).
        // mode: show|send, ohne: Vorgabe "vom Geraet" aus den Einstellungen.
        api.MapPost("/rooms/{id:long}/upload", async (long id, string name, string? text, string? mode, HttpRequest request, HttpContext http,
            ChatStore chat, UserStore users, CancellationToken ct) =>
        {
            var space = http.Space();
            chat.Room(space.User.Id, id);
            if (FileStore.FilesOff(space.Db)) throw new InvalidOperationException("Die Dateiablage ist für dich abgeschaltet.");
            var settings = ChatSettings.Of(space.Db);
            var file = SafeName(name);
            var mime = request.ContentType is { Length: > 0 } ct0 && !ct0.StartsWith("application/octet-stream") ? ct0.Split(';')[0].Trim() : DavServer.Mime(file);
            var f = await StoreUpload(space.Db, file, mime, request.Body, ct);
            var msg = chat.Post(space.User.Id, id, text, null, new[] { WithMode(FileRef(space, f), mode ?? settings.DeviceMode, settings, chat.Rights(space.User.Id)) },
                MentionsOf(text, id, chat, users));
            return MessageDto(chat.Message(space.User.Id, msg), users, chat);
        });

        // In Stuecken hochgeladen (/api/files/uploads, Version 2): fertig zusammensetzen und als Anhang verschicken
        api.MapPost("/uploads/{upload}/send", async (string upload, long room, string? text, string? mode, HttpContext http, ChatStore chat, UserStore users, CancellationToken ct) =>
        {
            var space = http.Space();
            chat.Room(space.User.Id, room);
            if (FileStore.FilesOff(space.Db)) throw new InvalidOperationException("Die Dateiablage ist für dich abgeschaltet.");
            var session = FilesApi.RequireComplete(space.Db, upload);
            var settings = ChatSettings.Of(space.Db);
            var file = SafeName(session.Name);
            var mime = session.Mime is { Length: > 0 } m && !m.StartsWith("application/octet-stream") ? m.Split(';')[0].Trim() : DavServer.Mime(file);
            FileEntry f;
            await using (var body = FilesApi.OpenSession(space.Db, session)) f = await StoreUpload(space.Db, file, mime, body, ct);
            FilesApi.DropSession(space.Db, session);
            var msg = chat.Post(space.User.Id, room, text, null, new[] { WithMode(FileRef(space, f), mode ?? settings.DeviceMode, settings, chat.Rights(space.User.Id)) },
                MentionsOf(text, room, chat, users));
            return MessageDto(chat.Message(space.User.Id, msg), users, chat);
        });

        // Anhang vorab hochladen (App: erst sammeln, dann alles mit Text in einer Nachricht): landet wie beim direkten Senden unter
        // „Chat“ in der eigenen Ablage; die Nachricht folgt mit den Verweisen (POST …/messages). mode: Vorgabe „vom Gerät“.
        api.MapPost("/rooms/{id:long}/stage", async (long id, string name, HttpRequest request, HttpContext http, ChatStore chat, UserStore users, CancellationToken ct) =>
        {
            var space = http.Space();
            chat.Room(space.User.Id, id);
            if (FileStore.FilesOff(space.Db)) throw new InvalidOperationException("Die Dateiablage ist für dich abgeschaltet.");
            var file = SafeName(name);
            var mime = request.ContentType is { Length: > 0 } ct0 && !ct0.StartsWith("application/octet-stream") ? ct0.Split(';')[0].Trim() : DavServer.Mime(file);
            var f = await StoreUpload(space.Db, file, mime, request.Body, ct);
            return Results.Ok(new { fileId = f.FileId, mode = ChatSettings.Of(space.Db).DeviceMode });
        });

        // Dasselbe für in Stücken Hochgeladenes (große Videos)
        api.MapPost("/uploads/{upload}/stage", async (string upload, long room, HttpContext http, ChatStore chat, UserStore users, CancellationToken ct) =>
        {
            var space = http.Space();
            chat.Room(space.User.Id, room);
            if (FileStore.FilesOff(space.Db)) throw new InvalidOperationException("Die Dateiablage ist für dich abgeschaltet.");
            var session = FilesApi.RequireComplete(space.Db, upload);
            var file = SafeName(session.Name);
            var mime = session.Mime is { Length: > 0 } m && !m.StartsWith("application/octet-stream") ? m.Split(';')[0].Trim() : DavServer.Mime(file);
            FileEntry f;
            await using (var body = FilesApi.OpenSession(space.Db, session)) f = await StoreUpload(space.Db, file, mime, body, ct);
            FilesApi.DropSession(space.Db, session);
            return Results.Ok(new { fileId = f.FileId, mode = ChatSettings.Of(space.Db).DeviceMode });
        });

        // Weiterleiten: dieselben Verweise (Besitzer bleibt) in andere Unterhaltungen. Gesendetes darf jeder weiterleiten,
        // als Zeigen oder Senden; Gezeigtes nur weiterzeigen, wenn der Besitzer es erlaubt. Eigenes ist frei.
        api.MapPost("/messages/{id:long}/forward", (long id, ForwardBody body, HttpContext http, ChatStore chat, Spaces spaces, UserStore users) =>
        {
            var space = http.Space();
            var me = space.User.Id;
            var src = chat.Message(me, id);
            if (src.Deleted || src.Kind != ChatStore.KindText) throw new InvalidOperationException("Diese Nachricht lässt sich nicht weiterleiten.");
            if (body.Mode is not (null or ChatRef.Show or ChatRef.Send)) throw new ArgumentException("Ungültige Art.");
            var settings = ChatSettings.Of(space.Db);
            var rights = chat.Rights(me);
            var refs = new List<ChatRef>();
            for (var i = 0; i < src.Refs.Count; i++)
            {
                var r = src.Refs[i];
                if (r.Type == ChatRef.TypeLocation && r.Owner != me) throw new UnauthorizedAccessException("Den Standort eines anderen kannst du nicht weiterleiten.");
                if (r.Owner == me && r.RootMessage == null)
                {
                    // Eigenes: wie neu verschickt
                    refs.Add(WithMode(r with { Revoked = false, LiveUntil = null }, body.Mode ?? r.EffectiveMode, settings, rights));
                    continue;
                }
                if (chat.RefState(r) != "ok") throw new InvalidOperationException("Etwas davon ist abgelaufen oder zurückgezogen.");
                var mode = body.Mode ?? (r.EffectiveMode == ChatRef.Show ? ChatRef.Show : ChatRef.Send);
                if (r.EffectiveMode == ChatRef.Show)
                {
                    if (mode != ChatRef.Show) throw new UnauthorizedAccessException("Gezeigtes lässt sich nur weiterzeigen.");
                    if (!OwnerSettings(spaces, r.Owner).Reshow) throw new UnauthorizedAccessException("Der Absender erlaubt nicht, das weiterzuzeigen.");
                }
                var forwarded = r with { Mode = mode, Revoked = false, RootMessage = r.RootMessage ?? id, RootIndex = r.RootMessage != null ? r.RootIndex : i };
                refs.Add(WithMode(forwarded, mode, settings, rights, keepExpiry: true));
            }
            var text = body.WithText != false ? src.Text : null;
            if (text == null && refs.Count == 0) throw new ArgumentException("Die Nachricht ist leer.");
            var result = new List<object>();
            foreach (var room in (body.Rooms ?? new()).Distinct())
            {
                var msg = chat.Post(me, room, text, null, refs);
                result.Add(MessageDto(chat.Message(me, msg), users, chat));
            }
            return result;
        });

        // Eigenen Verweis aendern: Art (Zeigen <-> Senden) oder zurueckziehen - wirkt auch in Weiterleitungen
        api.MapPut("/messages/{id:long}/refs/{i:int}", (long id, int i, RefPatch body, HttpContext http, ChatStore chat, UserStore users) =>
        {
            var space = http.Space();
            var me = space.User.Id;
            if (body.Mode is not (null or ChatRef.Show or ChatRef.Send)) throw new ArgumentException("Ungültige Art.");
            var created = chat.Message(me, id).Created;
            var settings = ChatSettings.Of(space.Db);
            var rights = chat.Rights(me);
            chat.UpdateRef(me, id, i, r =>
            {
                if (body.Mode == ChatRef.Send && r.Type == ChatRef.TypeLocation) throw new ArgumentException("Einen Standort kannst du nur zeigen.");
                if (body.Revoke == true) r = r with { Revoked = true };
                else if (body.Revoke == false) r = r with { Revoked = false };
                // Sichtbarkeitsdauer zaehlt ab dem Sendedatum
                if (body.Mode != null && !(r.Type == ChatRef.TypeAlbum && body.Mode != ChatRef.Show)) r = r with { Mode = body.Mode, Expires = body.Mode == ChatRef.Show ? settings.ExpiresFrom(created, rights) : null };
                return r;
            });
            return MessageDto(chat.Message(me, id), users, chat);
        });

        // ---------------- Einstellungen und Rechte ----------------

        api.MapGet("/settings", (HttpContext http, ChatStore chat) =>
        {
            var space = http.Space();
            var rights = chat.Rights(space.User.Id);
            return new { settings = ChatSettings.Of(space.Db), rights = new { rights.Expiring, rights.LocationRequests } };
        });

        api.MapPut("/settings", (ChatSettings body, HttpContext http, ChatStore chat) =>
        {
            var space = http.Space();
            // Ohne Erlaubnis fuer ablaufende Inhalte bleibt Gezeigtes dauerhaft
            if (!chat.Rights(space.User.Id).Expiring) body.ShowFor = "forever";
            ChatSettings.Save(space.Db, body);
            // Lesebestätigungen gelten für die anderen - darum auch in chat.db
            if (chat.Receipts(space.User.Id) != body.ReadReceipts) chat.SetReceipts(space.User.Id, body.ReadReceipts);
            return ChatSettings.Of(space.Db);
        });

        // Verwaltung: Chat-Rechte je Benutzer
        var admin = app.MapGroup("/api/chat/admin").RequireAuthorization("admin");
        admin.MapGet("/rights", (UserStore users, ChatStore chat) =>
            users.All().OrderBy(u => u.DisplayName).Select(u =>
            {
                var r = chat.Rights(u.Id);
                return new { u.Id, u.Login, u.DisplayName, r.Expiring, r.LocationRequests };
            }));
        // Aufbewahrung serverweit (Chat-Alltag): forever | 5y | 2y | 1y | 6m | 3m
        admin.MapGet("/retention", (ChatStore chat) => new { value = ChatRetention.Of(chat) });
        admin.MapPut("/retention", (RetentionBody body, ChatStore chat) =>
        {
            ChatRetention.Set(chat, body.Value);
            return new { value = body.Value, deleted = ChatRetention.Run(chat, DateTime.UtcNow) };
        });
        admin.MapPut("/rights/{user}", (string user, RightsBody body, UserStore users, ChatStore chat) =>
        {
            if (users.ById(user) == null) throw new KeyNotFoundException("Unbekannter Benutzer.");
            chat.SetRights(new ChatRights(user, body.Expiring, body.LocationRequests));
            return Results.Ok();
        });

        api.MapPut("/messages/{id:long}", (long id, EditBody body, HttpContext http, ChatStore chat, UserStore users) =>
        {
            var me = Me(http);
            chat.Edit(me, id, body.Text, MentionsOf(body.Text, chat.Message(me, id).Room, chat, users));
            return MessageDto(chat.Message(me, id), users, chat);
        });

        // Knopf unter Pukkis Antwort (Rückgängig, Auswahl): nur in der eigenen Unterhaltung mit Pukki; dazu die Standort-Anfrage
        api.MapPost("/messages/{id:long}/actions/{key}", (long id, string key, HttpContext http, ChatStore chat, IServiceProvider services) =>
        {
            var space = http.Space();
            var m = chat.Message(space.User.Id, id);
            // Standort-Anfrage: "Nicht schicken" (Schicken geht über /rooms/{id}/location mit der Position)
            if (key == LocNo) { DeclineLocation(chat, space.User.Id, m); return Results.Ok(); }
            if (m.Sender != Pukki.Id || chat.DirectRoom(space.User.Id, Pukki.Id) != m.Room)
                throw new UnauthorizedAccessException("Das geht nur bei Pukkis Antworten an dich.");
            // Interne Termineinladung: zusagen, mit Vorbehalt, absagen
            if (key is Pim.InternalInvites.Accept or Pim.InternalInvites.Tentative or Pim.InternalInvites.Decline)
            {
                Pim.InternalInvites.Respond(services, space, m, key);
                return Results.Ok();
            }
            using (Pukki.ForDevice(space)) Pukki.Act(services, chat, space, m, key);
            return Results.Ok();
        });

        api.MapDelete("/messages/{id:long}", (long id, HttpContext http, ChatStore chat) =>
        {
            chat.Delete(Me(http), id);
            return Results.Ok();
        });

        api.MapPost("/messages/{id:long}/reactions", (long id, ReactBody body, HttpContext http, ChatStore chat, UserStore users) =>
        {
            var me = Me(http);
            chat.React(me, id, body.Emoji);
            return MessageDto(chat.Message(me, id), users, chat);
        });

        // ---------------- Verweise ----------------

        MapItems(api);
        Albums = app.Services.GetService<DmOffice.Gallery.AlbumStore>();
        MapAlbums(api);
        MapSpaces(api);
        MapArts(api);
        MapExport(api);
        MapLocation(api);
        MapAvatars(api, app.Services.GetRequiredService<Spaces>());

        // Eigene Datei: in welchen Unterhaltungen sie verschickt wurde (Details in der Dateiablage)
        api.MapGet("/shared", (long fileId, HttpContext http, ChatStore chat, UserStore users) =>
        {
            var me = Me(http);
            return chat.SharedFile(me, fileId).Select(x => new
            {
                x.Room, x.Kind,
                title = x.Kind == ChatStore.Direct ? (x.Members.Count > 0 ? NameOf(users, x.Members[0]) : NameOf(users, "")) : x.Title,
                people = x.Members.Select(m => NameOf(users, m)), x.Last, x.Count,
            });
        });

        api.MapGet("/messages/{id:long}/refs/{i:int}/file", (long id, int i, bool? inline, HttpContext http, ChatStore chat, Spaces spaces) =>
        {
            // Gezeigtes nur anzeigen, nicht herunterladen (der Besitzer darf alles)
            var r = RefAt(http, chat, id, i);
            if (inline != true && r.EffectiveMode == ChatRef.Show && r.Owner != Me(http)) throw new UnauthorizedAccessException("Nur gezeigt – herunterladen ist nicht erlaubt.");
            var (owner, f) = OpenFile(http, chat, spaces, id, i);
            // Auch mit inline nur, was der Browser anzeigt - sonst kaeme die Datei zum Speichern heraus
            if (r.EffectiveMode == ChatRef.Show && r.Owner != Me(http) && !FilesApi.Viewable(f)) throw new UnauthorizedAccessException("Nur gezeigt – diese Datei lässt sich hier nicht ansehen.");
            return FilesApi.Send(http, owner.Db, f, inline == true);
        });

        api.MapGet("/messages/{id:long}/refs/{i:int}/thumb", async (long id, int i, int? size, string? v, HttpContext http, ChatStore chat, Spaces spaces) =>
        {
            var (owner, f) = OpenFile(http, chat, spaces, id, i);
            return await FilesApi.SendThumb(http, owner.Db, f, size, v);
        });

        api.MapGet("/messages/{id:long}/refs/{i:int}/note", (long id, int i, HttpContext http, ChatStore chat, Spaces spaces, UserStore users) =>
        {
            var (_, r, note) = OpenNote(http, chat, spaces, id, i);
            return new
            {
                note.SyncId, title = note.DisplayTitle, note.Html, note.Color, note.Modified, note.Created, note.Images,
                attachments = note.Attachments.Select(a => new { a.Name, a.Mime, a.Size, a.Blob }),
                owner = r.Owner, ownerName = NameOf(users, r.Owner),
            };
        });

        // Bilder und Anhaenge der Notiz - nur die, die in ihr vorkommen
        api.MapGet("/messages/{id:long}/refs/{i:int}/blob/{name}", (long id, int i, string name, bool? download, string? file, HttpContext http, ChatStore chat, Spaces spaces) =>
        {
            var (owner, r, note) = OpenNote(http, chat, spaces, id, i);
            if (!note.Images.Contains(name) && note.Attachments.All(a => a.Blob != name)) throw new KeyNotFoundException();
            var data = NotesDb.Of(owner).GetBlob(name) ?? throw new KeyNotFoundException();
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            var mime = Sniff(data);
            // Gezeigte Notiz: Bilder ansehen ja, Anhaenge herunterladen nein (wie in NotesApi)
            if ((download == true || mime == null) && r.EffectiveMode == ChatRef.Show && r.Owner != Me(http))
                throw new UnauthorizedAccessException("Nur gezeigt – herunterladen ist nicht erlaubt.");
            if (download == true || mime == null) return Results.File(data, "application/octet-stream", file ?? name);
            return Results.File(data, mime);
        });

        // Bei mir speichern: Datei in die eigene Ablage, Notiz in die eigenen Notizen - als eigene Kopie
        api.MapPost("/messages/{id:long}/refs/{i:int}/save", async (long id, int i, SaveBody body, HttpContext http, ChatStore chat, Spaces spaces,
            NotesSync notesSync, CancellationToken ct) =>
        {
            var me = http.Space();
            var r = RefAt(http, chat, id, i);
            // Gezeigtes bleibt im Chat - speichern nur bei Senden (eigenes immer)
            if (r.EffectiveMode == ChatRef.Show && r.Owner != me.User.Id) throw new UnauthorizedAccessException("Nur gezeigt – speichern ist nicht erlaubt.");
            var settings = ChatSettings.Of(me.Db);
            if (r.Type == ChatRef.TypeFile)
            {
                var (owner, f) = OpenFile(http, chat, spaces, id, i);
                if (FileStore.FilesOff(me.Db)) throw new InvalidOperationException("Die Dateiablage ist für dich abgeschaltet.");
                var dir = FileStore.Normalize(body.Folder ?? settings.SaveFolder);
                FileStore.EnsureDir(me.Db, dir);
                var target = FileStore.FreeName(me.Db, (dir.Length == 0 ? "" : dir + "/") + f.Name);
                await using (var data = FileStore.OpenRead(owner.Db, f.Path))
                    await FileStore.PutAsync(me.Db, target, data, f.Mime, ifNoneMatch: "*", ct: ct);
                return Results.Ok(new { type = r.Type, path = target });
            }
            else if (r.Type is ChatRef.TypeEvent or ChatRef.TypeTask or ChatRef.TypeContact)
            {
                var (itemOwner, obj) = OpenItem(http, chat, spaces, id, i);
                return SaveItemCopy(me, itemOwner, obj, r.Type, body.Folder);
            }
            else if (r.Type == ChatRef.TypeFolder) throw new ArgumentException("Aus einem Ordner einzelne Dateien speichern.");
            else
            {
                var (owner, _, note) = OpenNote(http, chat, spaces, id, i);
                var mine = NotesDb.Of(me);
                // Ohne Wahl: Ordner „Chat“ (wie bei Dateien) - gesucht beim Namen, damit es auch nach einem Wechsel des
                // Notizbuchs in notizi passt; fehlt er, wird er angelegt
                var folder = body.Folder == null ? ChatNoteFolder(me) : body.Folder.Length == 0 ? null : body.Folder;
                if (folder != null && mine.GetFolder(folder) == null) throw new KeyNotFoundException("Ordner nicht gefunden.");
                var src = NotesDb.Of(owner);
                // Bilder und Anhaenge heissen beim Empfaenger nach seinem Schluessel (wie beim Verschieben in geteilte Ordner)
                var renamed = new Dictionary<string, string>();
                foreach (var name in note.Images.Concat(note.Attachments.Select(a => a.Blob)).Distinct())
                    if (src.GetBlob(name) is { } data)
                    {
                        var now = await NotesSync.BlobNameAsync(me, data, ct);
                        mine.PutBlob(now, data);
                        if (now != name) renamed[name] = now;
                    }
                var newId = mine.ImportFrom(src, note.Id, folder, renamed);
                notesSync.Kick(me.User.Id);
                return Results.Ok(new { type = r.Type, noteId = newId });
            }
        });
    }

    // ---------------- Hilfen ----------------

    private static string Me(HttpContext http) => http.Space().User.Id;

    /// <summary>@Erwaehnungen: Mitglieder, die der Text mit Anzeigename, Vorname oder Anmeldename nennt.</summary>
    private static List<string> MentionsOf(string? text, long room, ChatStore chat, UserStore users) =>
        string.IsNullOrEmpty(text) || !text.Contains('@') ? [] : ChatMentions.Find(text, chat.MembersOf(room).Where(u => u != Pukki.Id).Select(u =>
        {
            var x = users.ById(u);
            return (u, new[] { x?.DisplayName, x?.DisplayName?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(), x?.Login });
        }));

    private static string NameOf(UserStore users, string id) => id == Pukki.Id ? Pukki.Name : users.ById(id)?.DisplayName ?? "Gelöschter Benutzer";

    /// <summary>Notizordner „Chat“ (oberste Ebene) für gespeicherte Notizen - fehlt er, wird er angelegt.</summary>
    private static string ChatNoteFolder(UserSpace me)
    {
        var id = me.Db.Query("SELECT sync_id FROM note_folders WHERE parent IS NULL AND name=$n ORDER BY sort_order LIMIT 1", r => r.GetString(0), ("$n", UploadFolder)).FirstOrDefault();
        if (id != null) return id;
        id = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow.ToString("o");
        me.Db.Exec("INSERT INTO note_folders(sync_id,sync_rev,parent,name,sort_order,created,modified) VALUES($s,1,NULL,$n,(SELECT COALESCE(MAX(sort_order),0)+1 FROM note_folders),$m,$m)",
            ("$s", id), ("$n", UploadFolder), ("$m", now));
        return id;
    }

    private static string SafeName(string? name)
    {
        var n = Path.GetFileName((name ?? "").Replace('\\', '/')).Trim();
        foreach (var c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
        if (n.Length == 0 || n is "." or "..") n = "Datei";
        return n.Length > 200 ? n[..200] : n;
    }

    /// <summary>
    /// Hochgeladenes ablegen - ausser es liegt schon dieselbe Datei in der eigenen Ablage (gleiche Pruefsumme, etwa dasselbe
    /// Foto an eine zweite Person oder vom automatischen Hochladen): dann zeigt die Nachricht auf die vorhandene, es gibt
    /// keine zweite Kopie. Vorrang hat eine im Ordner "Chat". Die Pruefsumme rechnet der BlobStore beim Schreiben mit.
    /// </summary>
    internal static async Task<FileEntry> StoreUpload(UserDb db, string name, string? mime, Stream body, CancellationToken ct)
    {
        var quota = FileStore.Quota(db);
        var room = quota <= 0 ? long.MaxValue : Math.Max(0, quota - FileStore.Usage(db));
        var blob = await db.Blobs.WriteAsync(body, room, () => new QuotaExceededException(quota), ct);
        var hex = FileStore.Hex(blob.Sha256);
        var same = db.Query("SELECT path FROM files WHERE sha256=$h AND is_dir=0 ORDER BY path LIKE $c DESC, path LIMIT 1",
            r => r.GetString(0), ("$h", hex), ("$c", UploadFolder + "/%")).FirstOrDefault();
        if (same != null && FileStore.Get(db, same) is { } existing)
        {
            if (blob.Id != null) db.Blobs.Delete(blob.Id);
            return existing;
        }
        FileStore.EnsureDir(db, UploadFolder);
        var path = FileStore.FreeName(db, UploadFolder + "/" + name);
        FileStore.PutBlob(db, path, blob, mime);
        return FileStore.Get(db, path) ?? throw new InvalidOperationException("Hochladen fehlgeschlagen.");
    }

    private static ChatRef FileRef(UserSpace space, FileEntry f) =>
        new(ChatRef.TypeFile, space.User.Id, f.Name, f.Mime is { Length: > 0 } m ? m : DavServer.Mime(f.Path), f.Size, FileId: f.FileId);

    /// <summary>Was der Absender anhaengt, muss ihm gehoeren.</summary>
    private static ChatRef Resolve(UserSpace space, RefInput r)
    {
        switch (r.Type)
        {
            case ChatRef.TypeFile:
                if (FileStore.FilesOff(space.Db)) throw new InvalidOperationException("Die Dateiablage ist für dich abgeschaltet.");
                var f = (r.FileId is { } fid ? FileStore.ById(space.Db, fid) : null) ?? throw new KeyNotFoundException("Datei nicht gefunden.");
                if (f.IsDir) throw new ArgumentException("Ordner mit type=folder verschicken.");
                if (FileStore.IsVaultOrSync(space.Db, f.Path)) throw new KeyNotFoundException("Datei nicht gefunden.");
                return FileRef(space, f);
            case ChatRef.TypeFolder:
                if (FileStore.FilesOff(space.Db)) throw new InvalidOperationException("Die Dateiablage ist für dich abgeschaltet.");
                var dir = (r.FileId is { } did ? FileStore.ById(space.Db, did) : null) ?? throw new KeyNotFoundException("Ordner nicht gefunden.");
                if (!dir.IsDir) throw new ArgumentException("Das ist kein Ordner.");
                if (FileStore.IsSystem(space.Db, dir.Path)) throw new KeyNotFoundException("Ordner nicht gefunden.");
                return new ChatRef(ChatRef.TypeFolder, space.User.Id, dir.Name.Length == 0 ? "Dateien" : dir.Name, FileId: dir.FileId);
            case ChatRef.TypeEvent or ChatRef.TypeTask or ChatRef.TypeContact:
                return ItemRef(space, r.Type, r.Collection, r.Item);
            case ChatRef.TypeAlbum:
                return AlbumRef(space, r.FileId);
            case ChatRef.TypeNote:
                if (r.NoteId is not > 0) throw new ArgumentException("Nur eigene Notizen lassen sich senden.");
                var n = NotesDb.Of(space).GetNote(r.NoteId.Value);
                if (n == null || n.Deleted != null) throw new KeyNotFoundException("Notiz nicht gefunden.");
                return new ChatRef(ChatRef.TypeNote, space.User.Id, n.DisplayTitle, Note: n.SyncId);
            default:
                throw new ArgumentException("Unbekannte Art von Anhang.");
        }
    }

    /// <summary>Art setzen: Zeigen bekommt die Sichtbarkeitsdauer aus den Einstellungen (sofern die Verwaltung Ablauf erlaubt).
    /// keepExpiry: beim Weiterleiten den frueheren Ablauf behalten, wenn er eher kommt.</summary>
    private static ChatRef WithMode(ChatRef r, string mode, ChatSettings settings, ChatRights rights, bool keepExpiry = false)
    {
        if (mode is not (ChatRef.Show or ChatRef.Send)) throw new ArgumentException("Ungültige Art.");
        if (r.Type == ChatRef.TypeAlbum) mode = ChatRef.Show;   // Alben nur zeigen
        DateTime? expires = mode == ChatRef.Show ? settings.ExpiresFrom(DateTime.UtcNow, rights) : null;
        if (keepExpiry && r.Expires is { } old && (expires == null || old < expires)) expires = old;
        return r with { Mode = mode, Expires = expires };
    }

    private static ChatSettings OwnerSettings(Spaces spaces, string owner)
    {
        try { return ChatSettings.Of(spaces.Get(owner).Db); }
        catch (KeyNotFoundException) { return new ChatSettings(); }
    }

    /// <summary>Verweis, den ich gerade nutzen darf: abgelaufen oder zurueckgezogen sieht nur noch der Besitzer.</summary>
    private static ChatRef RefAt(HttpContext http, ChatStore chat, long message, int i)
    {
        var me = Me(http);
        var (_, _, refs) = chat.RefsOf(me, message);
        if (i < 0 || i >= refs.Count) throw new KeyNotFoundException();
        var r = refs[i];
        if (r.Owner != me && chat.RefState(r) is var state && state != "ok")
            throw new KeyNotFoundException(state == "expired" ? "Abgelaufen." : "Zurückgezogen.");
        return r;
    }

    private static UserSpace OwnerOf(Spaces spaces, ChatRef r)
    {
        try { return spaces.Get(r.Owner); }
        catch (KeyNotFoundException) { throw new KeyNotFoundException("Nicht mehr verfügbar."); }
    }

    private static (UserSpace Owner, FileEntry File) OpenFile(HttpContext http, ChatStore chat, Spaces spaces, long message, int i)
    {
        var r = RefAt(http, chat, message, i);
        if (r.Type != ChatRef.TypeFile || r.FileId is not { } fid) throw new KeyNotFoundException();
        var owner = OwnerOf(spaces, r);
        var f = FileStore.ById(owner.Db, fid);
        if (f == null || f.IsDir) throw new KeyNotFoundException("Nicht mehr verfügbar.");
        return (owner, f);
    }

    private static (UserSpace Owner, ChatRef Ref, NoteRow Note) OpenNote(HttpContext http, ChatStore chat, Spaces spaces, long message, int i)
    {
        var r = RefAt(http, chat, message, i);
        if (r.Type != ChatRef.TypeNote || r.Note == null) throw new KeyNotFoundException();
        var owner = OwnerOf(spaces, r);
        var note = NotesDb.Of(owner).BySyncId(r.Note) ?? throw new KeyNotFoundException("Nicht mehr verfügbar.");
        return (owner, r, note);
    }

    private static string? Sniff(byte[] d) =>
        d.Length > 3 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47 ? "image/png"
        : d.Length > 2 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF ? "image/jpeg"
        : d.Length > 3 && d[0] == 0x47 && d[1] == 0x49 && d[2] == 0x46 && d[3] == 0x38 ? "image/gif"
        : d.Length > 11 && d[0] == 0x52 && d[1] == 0x49 && d[2] == 0x46 && d[3] == 0x46 && d[8] == 0x57 && d[9] == 0x45 && d[10] == 0x42 && d[11] == 0x50 ? "image/webp"
        : null;

    // ---------------- Ausgabe ----------------

    /// <summary>Titel einer Unterhaltung für me: zu zweit der Name des anderen, sonst der Gruppenname.</summary>
    private static string TitleOf(ChatRoom r, string me, UserStore users) =>
        r.Kind == ChatStore.Direct ? NameOf(users, r.Members.FirstOrDefault(m => m.Id != me)?.Id ?? "") : r.Title ?? "";

    private static object RoomDto(ChatRoom r, string me, UserStore users, ChatStore chat)
    {
        var mine = r.Members.FirstOrDefault(m => m.Id == me);
        var others = r.Members.Where(m => m.Id != me).ToList();
        return new
        {
            r.Id, r.Kind,
            title = TitleOf(r, me, users),
            r.Created, r.CreatedBy, r.Seq, r.Unread,
            r.Mentioned,
            muted = mine?.Muted ?? false, admin = mine?.Admin ?? false, lastRead = mine?.LastRead ?? 0, color = mine?.Color,
            // Standort-Anfrage: darf ich den anderen fragen? (nur zu zweit)
            askLocation = MayAskLocation(chat, r, me),
            // Lesestand nur, wenn das Mitglied Lesebestätigungen erlaubt; die Zustellung immer
            members = r.Members.Select(m => new
            {
                m.Id, name = NameOf(users, m.Id), login = m.Id == Pukki.Id ? Pukki.Id : users.ById(m.Id)?.Login, m.Admin,
                lastRead = m.Id == me || chat.Receipts(m.Id) ? m.LastRead : 0, delivered = m.Delivered, receipts = chat.Receipts(m.Id),
                // Profilbild (Version) und Akzentfarbe - ohne Bild zeigen Web und App den Anfangsbuchstaben in dieser Farbe
                avatar = LookOf(m.Id).Avatar, accent = LookOf(m.Id).Accent,
            }),
            last = r.Last == null ? null : MessageDto(r.Last, users, chat),
        };
    }

    private static object MessageDto(ChatMessage m, UserStore users, ChatStore chat) => new
    {
        m.Id, m.Room, m.Sender, senderName = NameOf(users, m.Sender), m.Created, m.Edited, m.Deleted, m.Kind, m.Text, m.ReplyTo,
        mentions = m.Mentions ?? [],
        // Pukkis Stimmung (Pose neben der Nachricht); ai: die Antwort stammt von der KI
        Mood = m.Mood == Pukki.AiMood ? null : m.Mood,
        ai = m.Mood == Pukki.AiMood,
        // Knöpfe (Pukki) - ihre Daten bleiben auf dem Server
        actions = (m.Actions ?? []).Select(a => new { a.Key, a.Label }),
        // mode: show|send; state: ok|expired|revoked (bei Weiterleitungen samt Original)
        refs = m.Refs.Select(r => (Ref: r, State: chat.RefState(r))).Select(x => new
        {
            x.Ref.Type, x.Ref.Name, x.Ref.Mime, x.Ref.Size, x.Ref.Owner, mode = x.Ref.EffectiveMode, x.Ref.Expires, state = x.State, forwarded = x.Ref.RootMessage != null,
            // Standort: Koordinaten nur, solange er gilt
            lat = x.State == "ok" ? x.Ref.Lat : null, lon = x.State == "ok" ? x.Ref.Lon : null, accuracy = x.State == "ok" ? x.Ref.Accuracy : null,
            // Live-Teilen: bis wann (danach "letzter Standort") und Zeit der letzten Position
            liveUntil = x.State == "ok" ? x.Ref.LiveUntil : null, at = x.State == "ok" ? x.Ref.At : null,
        }),
        reactions = m.Reactions.Select(x => new { x.Emoji, x.Users }),
        m.Seq,
    };
}
