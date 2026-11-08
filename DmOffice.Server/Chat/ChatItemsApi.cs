using System.Text.RegularExpressions;
using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Pim;

namespace DmOffice.Chat;

/// <summary>
/// Stufe 3: weitere Verweise im Chat - Termin, Aufgabe, Kontakt (Objekt in einer Sammlung des Besitzers) und Ordner der
/// Ablage. Zeigen: ansehen; Senden: "Bei mir speichern" legt eine eigene Kopie an (Termin in einen Kalender, Aufgabe in
/// eine Liste, Kontakt in ein Adressbuch; aus einem Ordner einzelne Dateien). Teilen kommt ueber die Freigaben.
/// </summary>
public static partial class ChatApi
{
    private sealed record FolderSaveBody(string Path, string? Folder);

    private static readonly Regex UidLine = new(@"(?m)^UID[:;][^\r\n]*", RegexOptions.Compiled);

    /// <summary>Verweis auf ein eigenes Objekt (Termin, Aufgabe, Kontakt) - Sammlung und Objekt muessen dem Absender gehoeren.</summary>
    private static ChatRef ItemRef(UserSpace space, string type, long? collection, string? item)
    {
        var coll = (collection is { } cid ? PimStore.Find(space.Db, cid) : null) ?? throw new KeyNotFoundException("Sammlung nicht gefunden.");
        var wantKind = type == ChatRef.TypeContact ? PimStore.AddressBook : PimStore.Calendar;
        if (coll.Kind != wantKind) throw new ArgumentException("Falsche Sammlung.");
        var obj = (item == null ? null : PimStore.Object(space.Db, coll.Id, item)) ?? throw new KeyNotFoundException("Nicht gefunden.");
        return new ChatRef(type, space.User.Id, ItemTitle(obj, type) ?? "?", Collection: coll.Id, Item: obj.Uri);
    }

    private static string? ItemTitle(DavObject obj, string type)
    {
        if (type == ChatRef.TypeContact) return ContactModel.Read(obj, false)?.DisplayName;
        var c = FirstComponent(obj, type == ChatRef.TypeEvent ? "VEVENT" : "VTODO");
        return c?.Text("SUMMARY");
    }

    private static Component? FirstComponent(DavObject obj, string name)
    {
        try
        {
            var all = Ics.Parse(obj.Data).First(r => r.Name == "VCALENDAR").Of(name).ToList();
            return all.FirstOrDefault(e => e.Get("RECURRENCE-ID") == null) ?? all.FirstOrDefault();
        }
        catch { return null; }
    }

    private static (UserSpace Owner, DavObject Obj) OpenItem(HttpContext http, ChatStore chat, Spaces spaces, long message, int i)
    {
        var r = RefAt(http, chat, message, i);
        if (r.Type is not (ChatRef.TypeEvent or ChatRef.TypeTask or ChatRef.TypeContact) || r.Collection is not { } cid || r.Item == null) throw new KeyNotFoundException();
        var owner = OwnerOf(spaces, r);
        var obj = PimStore.Object(owner.Db, cid, r.Item) ?? throw new KeyNotFoundException("Nicht mehr verfügbar.");
        return (owner, obj);
    }

    /// <summary>Ordner eines Verweises und eine Datei bzw. ein Unterordner darin (rel: Pfad relativ zum Ordner).</summary>
    private static (UserSpace Owner, FileEntry Folder, string Full) OpenFolder(HttpContext http, ChatStore chat, Spaces spaces, long message, int i, string? rel)
    {
        var r = RefAt(http, chat, message, i);
        if (r.Type != ChatRef.TypeFolder || r.FileId is not { } fid) throw new KeyNotFoundException();
        var owner = OwnerOf(spaces, r);
        var dir = FileStore.ById(owner.Db, fid);
        if (dir == null || !dir.IsDir) throw new KeyNotFoundException("Nicht mehr verfügbar.");
        var sub = FileStore.Normalize(rel ?? "");
        var full = sub.Length == 0 ? dir.Path : dir.Path.Length == 0 ? sub : dir.Path + "/" + sub;
        // Systemordner (notizi, Tagebuch, Tresor) gehen nie mit, auch wenn ein Elternordner verschickt wurde
        if (FileStore.IsSystem(owner.Db, full)) throw new KeyNotFoundException("Nicht mehr verfügbar.");
        return (owner, dir, full);
    }

    private static string Iso(DateTime? t) => t == null ? "" : DateTime.SpecifyKind(t.Value, DateTimeKind.Utc).ToString("o");

    private static void MapItems(RouteGroupBuilder api)
    {
        // Termin, Aufgabe, Kontakt ansehen (aktueller Stand beim Besitzer)
        api.MapGet("/messages/{id:long}/refs/{i:int}/item", (long id, int i, HttpContext http, ChatStore chat, Spaces spaces, UserStore users) =>
        {
            var (owner, obj) = OpenItem(http, chat, spaces, id, i);
            var r = RefAt(http, chat, id, i);
            var zone = owner.Zone;
            if (r.Type == ChatRef.TypeContact)
                return Results.Ok(new { type = r.Type, ownerName = NameOf(users, r.Owner), contact = ContactModel.Read(obj, false) });
            var c = FirstComponent(obj, r.Type == ChatRef.TypeEvent ? "VEVENT" : "VTODO") ?? throw new KeyNotFoundException("Nicht lesbar.");
            if (r.Type == ChatRef.TypeEvent)
            {
                var start = Ics.Time(c.Get("DTSTART"), zone, out var allDay);
                var end = Ics.Time(c.Get("DTEND"), zone, out _);
                return Results.Ok(new
                {
                    type = r.Type, ownerName = NameOf(users, r.Owner), title = c.Text("SUMMARY") ?? "", location = c.Text("LOCATION"),
                    description = c.Text("DESCRIPTION"), start = Iso(start), end = Iso(end), allDay, recurring = c.Get("RRULE") != null,
                });
            }
            var due = Ics.Time(c.Get("DUE"), zone, out var dueAllDay);
            var done = c.Get("COMPLETED") != null || string.Equals(c.Text("STATUS"), "COMPLETED", StringComparison.OrdinalIgnoreCase);
            return Results.Ok(new
            {
                type = r.Type, ownerName = NameOf(users, r.Owner), title = c.Text("SUMMARY") ?? "", description = c.Text("DESCRIPTION"),
                due = due == null ? null : Iso(due), dueAllDay, done,
            });
        });

        // Ordner ansehen: Inhalt einer Ebene (path relativ zum Ordner)
        api.MapGet("/messages/{id:long}/refs/{i:int}/folder", (long id, int i, string? path, HttpContext http, ChatStore chat, Spaces spaces) =>
        {
            var (owner, dir, full) = OpenFolder(http, chat, spaces, id, i, path);
            if (FileStore.Get(owner.Db, full) is not { IsDir: true }) throw new KeyNotFoundException("Nicht mehr verfügbar.");
            var prefix = dir.Path.Length == 0 ? "" : dir.Path + "/";
            var system = FileStore.SystemFilter(owner.Db);
            return FileStore.Children(owner.Db, full).Where(f => !system(f.Path)).Select(f => new
            {
                path = f.Path[prefix.Length..], f.Name, f.IsDir, f.Size, mime = f.Mime is { Length: > 0 } m ? m : DavServer.Mime(f.Path), f.Etag,
            });
        });

        // Datei aus dem Ordner: anzeigen; herunterladen nur bei Senden (Gezeigtes bleibt in dmOffice)
        api.MapGet("/messages/{id:long}/refs/{i:int}/folder/file", (long id, int i, string path, bool? inline, HttpContext http, ChatStore chat, Spaces spaces) =>
        {
            var r = RefAt(http, chat, id, i);
            if (inline != true && r.EffectiveMode == ChatRef.Show && r.Owner != Me(http)) throw new UnauthorizedAccessException("Nur gezeigt – herunterladen ist nicht erlaubt.");
            var (owner, _, full) = OpenFolder(http, chat, spaces, id, i, path);
            var f = FileStore.Get(owner.Db, full);
            if (f == null || f.IsDir) throw new KeyNotFoundException("Nicht mehr verfügbar.");
            if (r.EffectiveMode == ChatRef.Show && r.Owner != Me(http) && !FilesApi.Viewable(f)) throw new UnauthorizedAccessException("Nur gezeigt – diese Datei lässt sich hier nicht ansehen.");
            return FilesApi.Send(http, owner.Db, f, inline == true);
        });

        api.MapGet("/messages/{id:long}/refs/{i:int}/folder/thumb", async (long id, int i, string path, int? size, string? v, HttpContext http, ChatStore chat, Spaces spaces) =>
        {
            var (owner, _, full) = OpenFolder(http, chat, spaces, id, i, path);
            var f = FileStore.Get(owner.Db, full);
            if (f == null || f.IsDir) throw new KeyNotFoundException();
            return await FilesApi.SendThumb(http, owner.Db, f, size, v);
        });

        // Aus einem gesendeten Ordner einzelne Dateien bei mir speichern
        api.MapPost("/messages/{id:long}/refs/{i:int}/folder/save", async (long id, int i, FolderSaveBody body, HttpContext http, ChatStore chat, Spaces spaces, CancellationToken ct) =>
        {
            var me = http.Space();
            var r = RefAt(http, chat, id, i);
            if (r.EffectiveMode == ChatRef.Show && r.Owner != me.User.Id) throw new UnauthorizedAccessException("Nur gezeigt – speichern ist nicht erlaubt.");
            if (FileStore.FilesOff(me.Db)) throw new InvalidOperationException("Die Dateiablage ist für dich abgeschaltet.");
            var (owner, _, full) = OpenFolder(http, chat, spaces, id, i, body.Path);
            var f = FileStore.Get(owner.Db, full);
            if (f == null || f.IsDir) throw new KeyNotFoundException("Nicht mehr verfügbar.");
            var dir = FileStore.Normalize(body.Folder ?? ChatSettings.Of(me.Db).SaveFolder);
            FileStore.EnsureDir(me.Db, dir);
            var target = FileStore.FreeName(me.Db, (dir.Length == 0 ? "" : dir + "/") + f.Name);
            await using (var data = FileStore.OpenRead(owner.Db, f.Path))
                await FileStore.PutAsync(me.Db, target, data, f.Mime, ifNoneMatch: "*", ct: ct);
            return Results.Ok(new { type = ChatRef.TypeFile, path = target });
        });
    }

    /// <summary>
    /// Bei mir speichern fuer Termin, Aufgabe, Kontakt: eine eigene Kopie mit neuer UID in eine eigene Sammlung (Kennung in
    /// folder; ohne: der erste passende Kalender bzw. das erste Adressbuch).
    /// </summary>
    private static IResult SaveItemCopy(UserSpace me, UserSpace owner, DavObject obj, string type, string? folder)
    {
        PimStore.EnsureDefaults(me.Db);
        var kind = type == ChatRef.TypeContact ? PimStore.AddressBook : PimStore.Calendar;
        var component = type == ChatRef.TypeEvent ? "VEVENT" : type == ChatRef.TypeTask ? "VTODO" : null;
        Collection? target = long.TryParse(folder, out var tid) ? PimStore.Find(me.Db, tid) : null;
        if (target != null && target.Kind != kind) target = null;
        target ??= PimStore.Collections(me.Db, kind).FirstOrDefault(c => component == null || c.Components.Contains(component))
            ?? throw new InvalidOperationException(type == ChatRef.TypeContact ? "Kein Adressbuch vorhanden." : "Kein passender Kalender vorhanden.");
        if (component != null && !target.Components.Contains(component)) throw new ArgumentException("Diese Sammlung nimmt das nicht auf.");
        // Eigene Kopie: neue UID - sonst wuerden Kalender-Apps sie fuer dasselbe Objekt halten
        var data = UidLine.Replace(obj.Data, "UID:" + Guid.NewGuid().ToString("D").ToUpperInvariant());
        var uri = PimStore.NewUri(type == ChatRef.TypeContact ? ".vcf" : ".ics");
        PimStore.Put(me.Db, target, uri, data, ifNoneMatch: "*", zone: me.Zone);
        return Results.Ok(new { type, collection = target.Id, item = uri });
    }
}
