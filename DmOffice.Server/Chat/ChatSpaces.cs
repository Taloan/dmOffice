using DmOffice.Core;
using DmOffice.Dav;
using DmOffice.Notes;
using DmOffice.Pim;

namespace DmOffice.Chat;

/// <summary>
/// Gruppenraeume (Stufe 4): Eine Gruppe bekommt auf Wunsch einen Kalender, eine Liste, einen Ordner oder einen
/// Notizordner. Er liegt bei dem, der ihn einrichtet (dessen Kontingent), und ist mit allen anderen Mitgliedern
/// geteilt (Teilen = bearbeiten). Wer dazukommt, bekommt Zugriff; wer geht, verliert ihn. Geht der Besitzer selbst,
/// endet nur die Freigabe - der Raum bleibt sein Eigentum.
/// </summary>
public static class GroupSpaces
{
    /// <summary>Wie ein Gruppenraum fuer den Betrachter aussieht. Target: wohin die Oberflaeche springt
    /// (Kalender/Liste: Sammlungs-Id, fuer Mitglieder negativ; Ordner: Pfad; Notizordner: sync_id).</summary>
    public sealed record SpaceDto(string Kind, string Name, string Owner, string OwnerName, bool Mine, string? Target);

    public record NewSpace(string Kind);

    /// <summary>Freigaben aller Raeume einer Gruppe an die aktuellen Mitglieder angleichen.</summary>
    public static void Sync(IServiceProvider services, long room)
    {
        var chat = services.GetRequiredService<ChatStore>();
        var members = chat.MembersOf(room);
        foreach (var s in chat.SpacesOf(room))
        {
            if (!members.Contains(s.Owner)) { Unshare(services, s); chat.RemoveSpace(room, s.Kind); continue; }
            Share(services, s, members.Where(m => m != s.Owner).ToList());
        }
    }

    /// <summary>Jemand ist gegangen oder wurde entfernt: seine eigenen Raeume enden fuer die Gruppe, die anderen verlieren ihn.</summary>
    public static void Left(IServiceProvider services, long room, string user)
    {
        var chat = services.GetRequiredService<ChatStore>();
        foreach (var s in chat.SpacesOf(room).Where(s => s.Owner == user))
        {
            Unshare(services, s);
            chat.RemoveSpace(room, s.Kind);
        }
        Sync(services, room);
    }

    private static void Share(IServiceProvider services, ChatSpace s, List<string> members)
    {
        switch (s.Kind)
        {
            case ChatSpace.Calendar or ChatSpace.List: services.GetRequiredService<ShareStore>().Set(s.Owner, long.Parse(s.Ref), members); break;
            case ChatSpace.Folder: services.GetRequiredService<FolderShareStore>().Set(s.Owner, long.Parse(s.Ref), members); break;
            case ChatSpace.Notes: services.GetRequiredService<ShareStore>().SetFolder(s.Owner, s.Ref, members); break;
        }
    }

    private static void Unshare(IServiceProvider services, ChatSpace s) => Share(services, s, new());

    /// <summary>Raum anlegen: beim Einrichtenden, benannt nach der Gruppe, dann mit allen geteilt.</summary>
    public static void Create(HttpContext http, long room, string kind)
    {
        var services = http.RequestServices;
        var chat = services.GetRequiredService<ChatStore>();
        var me = http.Space();
        if (!ChatSpace.Kinds.Contains(kind)) throw new ArgumentException("Unbekannte Art.");
        chat.RequireSpaceAdmin(me.User.Id, room);
        if (chat.SpacesOf(room).Any(s => s.Kind == kind)) throw new InvalidOperationException("Diese Gruppe hat schon so einen Raum.");
        var title = chat.Room(me.User.Id, room).Title is { Length: > 0 } t ? t : "Gruppe";
        var db = me.Db;
        string reference;
        switch (kind)
        {
            case ChatSpace.Calendar or ChatSpace.List:
                var c = PimStore.CreateCollection(db, PimStore.Calendar, Guid.NewGuid().ToString("N")[..12], title, null, kind == ChatSpace.Calendar ? "VEVENT" : "VTODO");
                reference = c.Id.ToString();
                break;
            case ChatSpace.Folder:
                // In „Gruppen“, damit die eigene Ablage aufgeraeumt bleibt
                if (FileStore.Get(db, "Gruppen") == null) FileStore.Mkcol(db, "Gruppen");
                var path = FileStore.FreeName(db, "Gruppen/" + string.Concat(title.Select(ch => "/\\:*?\"<>|".Contains(ch) ? '_' : ch)).Trim());
                FileStore.Mkcol(db, path);
                reference = (FileStore.Get(db, path) ?? throw new InvalidOperationException("Ordner nicht angelegt.")).FileId.ToString();
                break;
            default:
                reference = Guid.NewGuid().ToString("N");
                var now = DateTime.UtcNow.ToString("o");
                db.Exec("INSERT INTO note_folders(sync_id,sync_rev,parent,name,sort_order,created,modified) VALUES($s,1,NULL,$n,(SELECT COALESCE(MAX(sort_order),0)+1 FROM note_folders),$m,$m)",
                    ("$s", reference), ("$n", title), ("$m", now));
                services.GetRequiredService<NotesSync>().Kick(me.User.Id, TimeSpan.FromSeconds(5));
                break;
        }
        chat.AddSpace(me.User.Id, room, kind, reference, title);
        Sync(services, room);
    }

    /// <summary>Raum aus der Gruppe nehmen (nur der Besitzer): die Freigabe endet, die Daten bleiben bei ihm.</summary>
    public static void Remove(HttpContext http, long room, string kind)
    {
        var services = http.RequestServices;
        var chat = services.GetRequiredService<ChatStore>();
        var me = http.Space().User.Id;
        chat.CheckMember(me, room);
        var s = chat.SpacesOf(room).FirstOrDefault(x => x.Kind == kind) ?? throw new KeyNotFoundException("Kein solcher Raum.");
        if (s.Owner != me) throw new UnauthorizedAccessException("Nur wer den Raum eingerichtet hat, kann ihn herausnehmen.");
        Unshare(services, s);
        chat.RemoveSpace(room, kind);
    }

    /// <summary>Die Raeume einer Gruppe, wie me sie sieht; was der Besitzer inzwischen geloescht hat, faellt heraus.</summary>
    public static List<SpaceDto> List(HttpContext http, long room)
    {
        var services = http.RequestServices;
        var chat = services.GetRequiredService<ChatStore>();
        var spaces = services.GetRequiredService<Spaces>();
        var users = services.GetRequiredService<UserStore>();
        var me = http.Space().User.Id;
        chat.CheckMember(me, room);
        var result = new List<SpaceDto>();
        foreach (var s in chat.SpacesOf(room))
        {
            UserSpace owner;
            try { owner = spaces.Get(s.Owner); } catch { continue; }
            var mine = s.Owner == me;
            string? name, target;
            switch (s.Kind)
            {
                case ChatSpace.Calendar or ChatSpace.List:
                    var id = long.Parse(s.Ref);
                    name = PimStore.Find(owner.Db, id)?.Name;
                    target = mine ? s.Ref : services.GetRequiredService<ShareStore>().Of(s.Owner, id) is { } sh ? (-(long)sh.Id).ToString() : null;
                    break;
                case ChatSpace.Folder:
                    var dir = FileStore.ById(owner.Db, long.Parse(s.Ref));
                    name = dir?.Name;
                    target = mine ? dir?.Path : SharedFiles.Mounts(services, me).FirstOrDefault(m => m.Owner.User.Id == s.Owner && m.Dir.FileId == dir?.FileId)?.Path;
                    break;
                default:
                    name = NotesDb.Of(owner).GetFolder(s.Ref)?.Name;
                    target = s.Ref;
                    break;
            }
            // Beim Besitzer geloescht: Raum austragen
            if (name == null) { Unshare(services, s); chat.RemoveSpace(room, s.Kind); continue; }
            result.Add(new SpaceDto(s.Kind, name, s.Owner, users.ById(s.Owner)?.DisplayName ?? "", mine, target));
        }
        return result;
    }
}

public static partial class ChatApi
{
    private static void MapSpaces(RouteGroupBuilder api)
    {
        api.MapGet("/rooms/{id:long}/spaces", (long id, HttpContext http) => GroupSpaces.List(http, id));

        api.MapPost("/rooms/{id:long}/spaces", (long id, GroupSpaces.NewSpace body, HttpContext http) =>
        {
            GroupSpaces.Create(http, id, body.Kind);
            return GroupSpaces.List(http, id);
        });

        api.MapDelete("/rooms/{id:long}/spaces/{kind}", (long id, string kind, HttpContext http) =>
        {
            GroupSpaces.Remove(http, id, kind);
            return Results.Ok();
        });
    }
}
