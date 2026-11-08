using DmOffice.Core;
using DmOffice.Gallery;

namespace DmOffice.Chat;

/// <summary>
/// Alben im Chat (Version 2): nur Zeigen. Der Empfänger sieht das Album im Chat - Liste, Vorschaubilder, Bilder im Browser -,
/// solange der Absender es noch sehen darf und den Verweis nicht zurückzieht oder er abläuft. Herunterladen geht nicht.
/// </summary>
public static partial class ChatApi
{
    /// <summary>Für Resolve (ohne Dienste): beim Start gesetzt.</summary>
    internal static AlbumStore? Albums;

    private static ChatRef AlbumRef(UserSpace space, long? id)
    {
        var album = (id is { } a ? Albums?.Get(a) : null) ?? throw new KeyNotFoundException("Album nicht gefunden.");
        if (!album.Sees(space.User.Id)) throw new KeyNotFoundException("Album nicht gefunden.");
        return new ChatRef(ChatRef.TypeAlbum, space.User.Id, album.Name, FileId: album.Id);
    }

    /// <summary>Das gezeigte Album, solange der Absender es noch sehen darf.</summary>
    private static Album OpenAlbum(HttpContext http, ChatStore chat, AlbumStore store, long message, int i)
    {
        var r = RefAt(http, chat, message, i);
        if (r.Type != ChatRef.TypeAlbum || r.FileId is not { } id) throw new KeyNotFoundException();
        return store.Get(id) is { } a && a.Sees(r.Owner) ? a : throw new KeyNotFoundException("Nicht mehr verfügbar.");
    }

    private static void MapAlbums(RouteGroupBuilder api)
    {
        api.MapGet("/messages/{id:long}/refs/{i:int}/album", (long id, int i, HttpContext http, ChatStore chat, AlbumStore store, Spaces spaces, ServerConfig config, UserStore users) =>
        {
            var a = OpenAlbum(http, chat, store, id, i);
            return new
            {
                name = a.Name, kind = a.Kind,
                items = AlbumApi.ItemsOf(a, spaces, config, store).Select(p => new
                {
                    key = p.Key, name = p.Name, mime = p.Mime, size = p.Size, modified = p.Modified, etag = p.Etag, taken = p.Meta?.Taken,
                    ownerName = users.ById(p.Owner)?.DisplayName,
                }),
            };
        });

        api.MapGet("/messages/{id:long}/refs/{i:int}/album/thumb", async (long id, int i, string key, int? size, HttpContext http, ChatStore chat, AlbumStore store, Spaces spaces, ServerConfig config) =>
        {
            var a = OpenAlbum(http, chat, store, id, i);
            return await AlbumApi.Thumb(http, AlbumApi.PhotoOf(a, key, spaces, config, store), spaces, config, size);
        });

        // Nur ansehen: im Browser (Bilder, Videos), nie als Download
        api.MapGet("/messages/{id:long}/refs/{i:int}/album/file", async (long id, int i, string key, HttpContext http, ChatStore chat, AlbumStore store, Spaces spaces, ServerConfig config) =>
        {
            var a = OpenAlbum(http, chat, store, id, i);
            return await AlbumApi.File(http, AlbumApi.PhotoOf(a, key, spaces, config, store), spaces, config, inline: true, download: false);
        });
    }
}
