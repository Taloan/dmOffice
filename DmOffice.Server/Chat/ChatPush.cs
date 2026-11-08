using DmOffice.Core;

namespace DmOffice.Chat;

/// <summary>
/// Neue Chat-Nachricht: sofort Bescheid an die Empfaenger.
/// <list type="bullet">
/// <item>Handy-App: FCM-Datennachricht t=chat mit der Kennung der Unterhaltung, nie mit Inhalt - die App holt die
/// Nachricht ueber den verschluesselten Umschlag und zeigt die Benachrichtigung selbst.</item>
/// <item>Browser (Web Push, 2.0): Absender und Anfang des Textes - Ende-zu-Ende verschluesselt zum Browser, der
/// Push-Dienst sieht nichts.</item>
/// </list>
/// Mehrere Nachrichten kurz hintereinander ergeben ein Signal. Ruhezeiten (Chat-Komfort): in der Ruhezeit nichts
/// (ausser fuer Erwaehnungen, wenn so eingestellt) - danach eines, falls noch etwas ungelesen ist.
/// </summary>
public sealed class ChatPush(ChatStore chat, Spaces spaces, UserStore users, Fcm fcm, WebPush web, ILogger<ChatPush> log) : BackgroundService
{
    private readonly object _lock = new();
    private readonly Dictionary<string, (long Room, bool Mention, long Message)> _pending = new();
    private readonly Dictionary<string, long> _deferred = new();
    private readonly SemaphoreSlim _wake = new(0);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        chat.Posted += OnPosted;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                // Spaetestens jede Minute: ist eine Ruhezeit vorbei?
                if (await _wake.WaitAsync(TimeSpan.FromMinutes(1), stop))
                {
                    // Kurz sammeln: schnelle Folgen von Nachrichten sind ein Signal
                    await Task.Delay(TimeSpan.FromMilliseconds(400), stop);
                    while (_wake.CurrentCount > 0) await _wake.WaitAsync(stop);
                }
                Dictionary<string, (long Room, bool Mention, long Message)> batch;
                lock (_lock)
                {
                    batch = new(_pending);
                    _pending.Clear();
                }
                var withWeb = batch.Count > 0 || _deferred.Count > 0 ? web.Users() : [];
                foreach (var (user, p) in batch)
                {
                    try
                    {
                        var space = spaces.Get(user);
                        var quiet = ChatSettings.Of(space.Db).QuietNow(space);
                        if (quiet == "all" || (quiet == "mentions" && !p.Mention)) { _deferred[user] = p.Room; continue; }
                        await Signal(space, p.Room, p.Message, withWeb.Contains(user), stop);
                    }
                    catch (Exception ex) when (!stop.IsCancellationRequested) { log.LogWarning("Chat-Push an {User}: {Error}", user, ex.Message); }
                }
                foreach (var (user, room) in _deferred.ToList())
                {
                    try
                    {
                        var space = spaces.Get(user);
                        if (ChatSettings.Of(space.Db).QuietNow(space) != null) continue;
                        _deferred.Remove(user);
                        if (chat.Unread(user) > 0) await Signal(space, room, 0, withWeb.Contains(user), stop);
                    }
                    catch (Exception ex) when (!stop.IsCancellationRequested) { _deferred.Remove(user); log.LogWarning("Chat-Push an {User}: {Error}", user, ex.Message); }
                }
            }
        }
        catch (OperationCanceledException) { }
        finally { chat.Posted -= OnPosted; }
    }

    private void OnPosted(ChatPosted p)
    {
        lock (_lock)
            foreach (var u in p.Recipients.Where(u => u != Pukki.Id))
                _pending[u] = (p.Room, (_pending.TryGetValue(u, out var old) && old.Mention) || (p.Mentioned?.Contains(u) ?? false), p.Message);
        _wake.Release();
    }

    /// <summary>message: die neueste Nachricht (0: nach der Ruhezeit - nur „neue Nachrichten“).</summary>
    private async Task Signal(UserSpace space, long room, long message, bool toWeb, CancellationToken ct)
    {
        if (fcm.Configured)
            foreach (var tok in Push.Tokens(space.Db))
            {
                var result = await fcm.Send(tok.Value, new() { ["t"] = "chat", ["room"] = room.ToString() }, ct);
                if (result == Fcm.Result.Unregistered) Push.Remove(space.Db, tok.Value);
            }
        if (!toWeb) return;
        var me = space.User.Id;
        var r = chat.Room(me, room);
        var title = r.Kind == ChatStore.Direct ? Name(r.Members.FirstOrDefault(m => m.Id != me)?.Id) : r.Title ?? "Chat";
        var body = "Neue Nachrichten";
        if (message > 0)
        {
            var m = chat.Message(me, message);
            var text = m.Text ?? string.Join(", ", m.Refs.Select(x => x.Type == ChatRef.TypeLocation ? (x.LiveUntil != null ? "📍 Live-Standort" : "📍 Standort") : x.Mime?.StartsWith("audio/") == true ? "🎤 Sprachnachricht" : "📎 " + x.Name));
            if (r.Kind == ChatStore.Group) title = $"{Name(m.Sender)} · {title}";
            body = text;
        }
        await web.Send(me, title, body, $"/#/chat/{room}", $"chat-{room}", urgent: true, ct);
    }

    private string Name(string? id) => id == Pukki.Id ? Pukki.Name : id != null && users.ById(id) is { } u ? u.DisplayName : "Chat";
}
