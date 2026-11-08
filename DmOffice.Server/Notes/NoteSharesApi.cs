using DmOffice.Core;

namespace DmOffice.Notes;

/// <summary>
/// Einzelne Notiz teilen (Version 2): Zeigen, Senden (nur lesen) oder Teilen (bearbeiten) - nur in dmOffice,
/// beim Empfaenger unter „Mit mir geteilt“. Verschieben, Archivieren und Loeschen bleiben beim Besitzer.
/// </summary>
public static class NoteSharesApi
{
    public record ShareBody(List<string> Members, List<string>? ReadOnly, List<string>? ShowOnly = null);

    /// <summary>Eine einzeln mit mir geteilte Notiz in der Liste.</summary>
    public record SharedNoteDto(long Id, int Share, string Title, string Snippet, string Color, DateTime Modified, string Owner,
        bool CanWrite, bool ShowOnly, string? Image, int Attachments);

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api/notes").RequireAuthorization();

        // Mit wem eine eigene Notiz geteilt ist
        api.MapGet("/{id:long}/shares", (long id, HttpContext http, NoteShareStore shares, UserStore users) =>
        {
            var (space, note) = Own(http, id);
            return shares.Of(space.User.Id, note.SyncId) is { } s ? Members(s, users) : [];
        });

        // Eine eigene Notiz teilen; leere Mitglieder: Freigabe aufheben
        api.MapPut("/{id:long}/shares", (long id, ShareBody body, HttpContext http, NoteShareStore shares, UserStore users) =>
        {
            var (space, note) = Own(http, id);
            var members = body.Members.Where(m => m != space.User.Id && users.ById(m) != null).ToList();
            shares.Set(space.User.Id, note.SyncId, members, body.ReadOnly, body.ShowOnly);
            return Results.Ok(new { members = shares.Of(space.User.Id, note.SyncId) is { } s ? Members(s, users) : [] });
        });

        // Einzeln mit mir geteilte Notizen
        api.MapGet("/single-shared", (HttpContext http) =>
            SharedSingleNotes.WithMe(http)
                .Select(x => new SharedNoteDto(x.Scope.Wire(x.Note.Id), x.Scope.Share!.Id - NoteShareStore.WireBase, x.Note.DisplayTitle, x.Note.Snippet,
                    x.Note.Color, x.Note.Modified, x.Scope.OwnerName, x.Scope.CanWrite, x.Scope.ShowOnly, x.Note.Images.FirstOrDefault(), x.Note.Attachments.Count))
                .OrderByDescending(x => x.Modified).ToList());

        // Einzeln geteilte Notiz nicht mehr anzeigen (der Besitzer behaelt sie)
        api.MapPost("/single-shared/{id:int}/leave", (int id, HttpContext http, NoteShareStore shares) =>
        {
            shares.Leave(id, http.Space().User.Id);
            return Results.Ok();
        });
    }

    /// <summary>Eigene Notiz (nicht im Papierkorb), sonst 404 bzw. 403 fuer geteilte.</summary>
    private static (UserSpace Space, NoteRow Note) Own(HttpContext http, long id)
    {
        if (id < 0) throw new UnauthorizedAccessException("Teilen kann nur der Besitzer der Notiz.");
        var space = http.Space();
        var note = NotesDb.Of(space).GetNote(id);
        if (note == null || note.Deleted != null) throw new KeyNotFoundException("Notiz nicht gefunden.");
        return (space, note);
    }

    private static List<object> Members(NoteShare s, UserStore users) =>
        s.Members.Select(m => users.ById(m)).OfType<UserRecord>()
            .Select(u => (object)new { id = u.Id, name = u.DisplayName, readOnly = s.ReadOnly.Contains(u.Id), level = s.LevelOf(u.Id) }).ToList();
}
