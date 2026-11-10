using DmOffice.Core;

namespace DmOffice.Chat;

/// <summary>
/// Standort-Anfrage (Stufe 7): Wem die Verwaltung es erlaubt, der fragt im Gespraech zu zweit nach dem Standort - aber
/// nur, wenn der andere das in seinen Einstellungen zulaesst. Der Gefragte entscheidet jedes Mal selbst: "Standort
/// schicken" holt die Position einmal auf seinem Geraet und schickt sie als Nachricht (kein laufendes Verfolgen),
/// "Nicht schicken" antwortet kurz. Der Standort ist ein Verweis der Art "location" mit Breite, Laenge und Genauigkeit
/// (in den Verweisen der Nachricht, kein neues Schema). Er ist gezeigt, nicht gesendet: mit der Ablauf-Einstellung des
/// Gefragten, danach sehen andere keine Koordinaten mehr. Weiterleiten darf ihn nur, wem er gehoert.
/// Live-Teilen (Minutes 1-30): dieselbe Nachricht bekommt bis LiveUntil neue Positionen (PUT .../location/{msg}), statt
/// neue Nachrichten zu erzeugen; danach bleibt sie als "letzter Standort". Spaeter als 30 Minuten nimmt der Server keine
/// Position mehr an, Beenden geht jederzeit (.../stop). Weitergeleitet wird nur die Position, nicht das Live-Teilen.
/// </summary>
public static partial class ChatApi
{
    public const string LocYes = "loc-yes", LocNo = "loc-no";

    public const int MaxLiveMinutes = 30;

    private sealed record LocationBody(double Lat, double Lon, double? Accuracy, long? Request, int? Minutes = null);
    private sealed record LocationUpdate(double Lat, double Lon, double? Accuracy);

    private static void CheckPosition(double lat, double lon)
    {
        if (!double.IsFinite(lat) || !double.IsFinite(lon) || lat is < -90 or > 90 || lon is < -180 or > 180)
            throw new ArgumentException("Ungültige Position.");
    }

    private static double? AccuracyOf(double? accuracy) => accuracy is > 0 and < 1e7 ? Math.Round(accuracy.Value) : null;

    /// <summary>Darf me den anderen in diesem Raum nach dem Standort fragen? (Recht von der Verwaltung + Einstellung des anderen)</summary>
    internal static bool MayAskLocation(ChatStore chat, ChatRoom room, string me)
    {
        if (room.Kind != ChatStore.Direct || !chat.Rights(me).LocationRequests) return false;
        var other = room.Members.FirstOrDefault(m => m.Id != me)?.Id;
        if (other == null || other == Pukki.Id || _lookSpaces == null) return false;
        try { return ChatSettings.Of(_lookSpaces.Get(other).Db).LocationRequests; }
        catch (KeyNotFoundException) { return false; }
    }

    private static void MapLocation(RouteGroupBuilder api)
    {
        api.MapPost("/rooms/{id:long}/location-request", (long id, HttpContext http, ChatStore chat, UserStore users) =>
        {
            var me = Me(http);
            var room = chat.Room(me, id);
            if (!chat.Rights(me).LocationRequests) throw new UnauthorizedAccessException("Standort-Anfragen sind für dich in der Verwaltung nicht erlaubt.");
            if (!MayAskLocation(chat, room, me)) throw new UnauthorizedAccessException("Diese Person möchte nicht nach ihrem Standort gefragt werden.");
            var msg = chat.Post(me, id, "Wo bist du gerade? Schickst du mir deinen Standort?",
                actions: [new ChatAction(LocYes, "Standort schicken"), new ChatAction(LocNo, "Nicht schicken")]);
            return MessageDto(chat.Message(me, msg), users, chat);
        });

        // Den eigenen Standort schicken - als Antwort auf eine Anfrage (request) oder einfach so
        api.MapPost("/rooms/{id:long}/location", (long id, LocationBody body, HttpContext http, ChatStore chat, UserStore users) =>
        {
            var space = http.Space();
            var me = space.User.Id;
            chat.Room(me, id);
            CheckPosition(body.Lat, body.Lon);
            if (body.Minutes is < 0 or > MaxLiveMinutes) throw new ArgumentException("Live teilen geht für 1 bis 30 Minuten.");
            if (body.Request is { } request) TakeRequest(chat, me, id, request);
            var now = DateTime.UtcNow;
            DateTime? live = body.Minutes is int n and > 0 ? now.AddMinutes(n) : null;
            var r = WithMode(new ChatRef(ChatRef.TypeLocation, me, "Standort", Lat: Math.Round(body.Lat, 6), Lon: Math.Round(body.Lon, 6),
                Accuracy: AccuracyOf(body.Accuracy), LiveUntil: live, At: now), ChatRef.Show, ChatSettings.Of(space.Db), chat.Rights(me));
            // Kurze Sichtbarkeit (Einstellung) endet nicht mitten im Live-Teilen
            if (live != null && r.Expires is { } e && e < live) r = r with { Expires = live };
            var msg = chat.Post(me, id, null, body.Request, [r]);
            return MessageDto(chat.Message(me, msg), users, chat);
        });

        // Live-Teilen: neue Position in derselben Nachricht - nur der Absender, nur bis LiveUntil
        api.MapPut("/rooms/{id:long}/location/{msg:long}", (long id, long msg, LocationUpdate body, HttpContext http, ChatStore chat, UserStore users) =>
        {
            var me = Me(http);
            CheckPosition(body.Lat, body.Lon);
            var index = LiveIndex(chat, me, id, msg);
            chat.UpdateRef(me, msg, index, r => r.LiveUntil is { } until && until > DateTime.UtcNow && !r.Revoked
                ? r with { Lat = Math.Round(body.Lat, 6), Lon = Math.Round(body.Lon, 6), Accuracy = AccuracyOf(body.Accuracy), At = DateTime.UtcNow }
                : throw new InvalidOperationException("Das Live-Teilen ist beendet."));
            return MessageDto(chat.Message(me, msg), users, chat);
        });

        // Live-Teilen vorzeitig beenden: die letzte Position bleibt stehen
        api.MapPost("/rooms/{id:long}/location/{msg:long}/stop", (long id, long msg, HttpContext http, ChatStore chat, UserStore users) =>
        {
            var me = Me(http);
            var index = LiveIndex(chat, me, id, msg);
            var now = DateTime.UtcNow;
            chat.UpdateRef(me, msg, index, r => r.LiveUntil is { } until && until > now ? r with { LiveUntil = now } : r);
            return MessageDto(chat.Message(me, msg), users, chat);
        });
    }

    /// <summary>Der eigene Live-Standort in dieser Nachricht (Index des Verweises) - sonst KeyNotFound bzw. nicht erlaubt.</summary>
    private static int LiveIndex(ChatStore chat, string me, long room, long message)
    {
        var m = chat.Message(me, message);
        if (m.Room != room) throw new KeyNotFoundException("Nachricht nicht gefunden.");
        if (m.Sender != me) throw new UnauthorizedAccessException("Nur wer den Standort teilt, kann ihn ändern.");
        var index = m.Refs.FindIndex(r => r.Type == ChatRef.TypeLocation && r.Owner == me && r.RootMessage == null && r.LiveUntil != null);
        return index >= 0 ? index : throw new InvalidOperationException("Diese Nachricht ist kein Live-Standort.");
    }

    /// <summary>Knopf "Nicht schicken": Anfrage beantworten, kurz Bescheid geben.</summary>
    internal static void DeclineLocation(ChatStore chat, string me, ChatMessage request)
    {
        TakeRequest(chat, me, request.Room, request.Id);
        chat.Post(me, request.Room, "Ich schicke meinen Standort gerade nicht.", request.Id);
    }

    /// <summary>Offene Anfrage an mich? Dann die Knoepfe wegnehmen - sie ist beantwortet.</summary>
    private static void TakeRequest(ChatStore chat, string me, long room, long request)
    {
        var m = chat.Message(me, request);
        if (m.Room != room || m.Sender == me || m.Actions?.Any(a => a.Key == LocYes) != true)
            throw new InvalidOperationException("Diese Anfrage ist schon beantwortet.");
        chat.SetActions(request, null);
    }
}
