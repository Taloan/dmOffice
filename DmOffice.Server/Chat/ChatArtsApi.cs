namespace DmOffice.Chat;

public static partial class ChatApi
{
    /// <summary>Arten von Terminen (Stufe 5, Schritt 2): Liste für Dialoge und ein Vorschlag zu einem Titel.</summary>
    private static void MapArts(RouteGroupBuilder api)
    {
        api.MapGet("/arts", () => PukkiArts.All.Select(a => new { a.Key, a.De, a.En, a.Icon }));

        api.MapGet("/arts/suggest", (string? title, string? description) =>
            PukkiArts.Classify(title ?? "", description) is { } a ? Results.Ok(new { a.Key, a.De, a.En, a.Icon }) : Results.Ok(new { key = (string?)null }));
    }

    /// <summary>
    /// Verweis auf ein eigenes Element (Datei, Ordner, Notiz, Termin, Aufgabe, Kontakt) - für Pukkis Suchantworten
    /// (Stufe 5, Schritt 3); derselbe Weg wie „Im Chat …“.
    /// </summary>
    internal static ChatRef RefFor(Core.UserSpace space, string type, long? fileId = null, long? noteId = null, long? collection = null, string? item = null) =>
        Resolve(space, new RefInput(type, fileId, noteId, null, collection, item));
}
