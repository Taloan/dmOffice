using System.Globalization;
using DmOffice.Chat;
using DmOffice.Core;
using DmOffice.Dav;

namespace DmOffice.Emergency;

/// <summary>
/// Notfallzugang (Version 2): gewaehrt nach Ablauf der Frist, erinnert alle, die widersprechen duerfen, taeglich - ueber
/// Pukki im Chat, also auch per Push aufs Handy - und sagt dem Anfragenden, wie es ausgegangen ist.
/// </summary>
public sealed class EmergencyService(IServiceProvider services, EmergencyStore store, ILogger<EmergencyService> log) : BackgroundService
{
    public const string DenyKey = "emergency-deny";
    private static readonly TimeSpan RemindEvery = TimeSpan.FromHours(20);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Release-Liste: in dieser Version nicht freigeschaltet - nichts im Hintergrund
        if (!DmOffice.Core.Features.On(DmOffice.Core.Features.Emergency)) return;
        try { await Task.Delay(TimeSpan.FromSeconds(20), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try { Tick(DateTime.UtcNow); }
            catch (Exception e) { log.LogWarning(e, "Notfallzugang: Durchgang fehlgeschlagen"); }
            try { await Task.Delay(TimeSpan.FromMinutes(1), ct); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Faellige Anfragen gewaehren, an offene taeglich erinnern.</summary>
    public void Tick(DateTime now)
    {
        foreach (var r in store.OpenRequests())
        {
            if (store.Package(r.Package) is not { } p) continue;
            if (r.Due <= now)
            {
                var granted = store.Grant(r.Id);
                log.LogInformation("Notfallzugang: Frist abgelaufen, {Requester} hat Zugriff", Name(r.Requester));
                Granted(p, granted);
            }
            else if (r.LastReminder == null || now - r.LastReminder > RemindEvery)
            {
                Remind(p, r, first: r.LastReminder == null);
                store.Reminded(r.Id);
            }
        }
    }

    // ---------------- Nachrichten ----------------

    /// <summary>Neue Anfrage: gleich alle benachrichtigen, die widersprechen duerfen, und den Anfragenden.</summary>
    public void Asked(EmergencyPackage p, EmergencyRequest r)
    {
        Remind(p, r, first: true);
        store.Reminded(r.Id);
        Tell(r.Requester, $"Deine Anfrage für den Notfallzugang „{p.Name}“ von {Name(p.Owner)} ist eingegangen. Wenn bis {When(r.Requester, r.Due)} "
            + "niemand bestätigt, dass kein Notfall vorliegt, bekommst du Zugriff – ich sage dir Bescheid.");
    }

    private void Remind(EmergencyPackage p, EmergencyRequest r, bool first)
    {
        foreach (var v in store.Vetoers(p).Where(v => v != r.Requester))
        {
            var whose = v == p.Owner ? "deinen Notfallzugang" : $"den Notfallzugang von {Name(p.Owner)}";
            var text = (first ? "⚠️ " : "⚠️ Erinnerung: ") + $"{Name(r.Requester)} hat {whose} „{p.Name}“ angefordert. "
                + $"Wenn niemand bis {When(v, r.Due)} bestätigt, dass kein Notfall vorliegt, bekommt {Name(r.Requester)} Zugriff auf {Content(p)}.";
            Tell(v, text, [new ChatAction(DenyKey, "Kein Notfall", r.Id)]);
        }
    }

    public void Denied(EmergencyPackage p, EmergencyRequest r)
    {
        var by = Name(r.DecidedBy ?? p.Owner);
        Tell(r.Requester, $"Alles gut, es gibt keinen Notfall – {by} hat das für „{p.Name}“ bestätigt.");
        foreach (var v in store.Vetoers(p).Where(v => v != r.Requester && v != r.DecidedBy))
            Tell(v, $"{by} hat bestätigt, dass kein Notfall vorliegt – die Anfrage von {Name(r.Requester)} für „{p.Name}“ ist erledigt.");
    }

    private void Granted(EmergencyPackage p, EmergencyRequest r)
    {
        var how = new List<string>();
        if (p.Folders.Count > 0) how.Add("die Ordner stehen in der Dateiablage unter „Mit mir geteilt“");
        if (p.HasText || p.Files.Count > 0) how.Add("Notfalltext und Dateien öffnest du mit dem Notfallcode unter Einstellungen → Notfallzugang");
        if (p.Account) how.Add("das Konto öffnest du unter Einstellungen → Notfallzugang");
        if (r.MadeAdmin) how.Add("du hast jetzt Verwaltungsrechte");
        Tell(r.Requester, $"Die Frist für den Notfallzugang „{p.Name}“ von {Name(p.Owner)} ist abgelaufen, ohne dass jemand widersprochen hat. "
            + $"Du hast jetzt Zugriff auf {Content(p)}" + (how.Count > 0 ? " – " + string.Join("; ", how) : "") + ".");
        foreach (var v in store.Vetoers(p).Where(v => v != r.Requester))
            Tell(v, $"Die Frist ist abgelaufen: {Name(r.Requester)} hat jetzt Zugriff auf „{p.Name}“ ({Content(p)}). "
                + "Zurücknehmen geht unter Einstellungen → Notfallzugang.");
    }

    public void Revoked(EmergencyPackage p, EmergencyRequest r) =>
        Tell(r.Requester, $"{Name(r.DecidedBy ?? p.Owner)} hat den Notfallzugang „{p.Name}“ zurückgenommen.");

    public void Entered(EmergencyPackage p, EmergencyRequest r)
    {
        foreach (var v in store.Vetoers(p).Where(v => v != r.Requester))
            Tell(v, $"{Name(r.Requester)} hat über den Notfallzugang „{p.Name}“ das Konto von {(v == p.Owner ? "dir" : Name(p.Owner))} geöffnet.");
    }

    /// <summary>Knopf „Kein Notfall“ unter Pukkis Nachricht.</summary>
    public static string DenyFromChat(IServiceProvider services, string user, string requestId)
    {
        var store = services.GetRequiredService<EmergencyStore>();
        try
        {
            var r = store.Deny(requestId, user);
            if (store.Package(r.Package) is { } p) services.GetRequiredService<EmergencyService>().Denied(p, r);
            return "Danke – ich habe Bescheid gegeben, dass kein Notfall vorliegt.";
        }
        catch (Exception e) when (e is InvalidOperationException or KeyNotFoundException or UnauthorizedAccessException) { return e.Message; }
    }

    // ---------------- Hilfen ----------------

    /// <summary>Was das Paket gewaehrt, lesbar: „die Ordner „Verträge“, „Versicherungen“ (lesen), das ganze Konto, die Verwaltung“.</summary>
    public string Content(EmergencyPackage p)
    {
        var parts = new List<string>();
        if (p.Folders.Count > 0)
        {
            var db = services.GetRequiredService<Spaces>().Get(p.Owner).Db;
            var names = p.Folders.Select(f => FileStore.ById(db, f)?.Name).Where(n => !string.IsNullOrEmpty(n)).Select(n => $"„{n}“").ToList();
            parts.Add((names.Count == 1 ? "den Ordner " : "die Ordner ") + string.Join(", ", names) + " (lesen)");
        }
        if (p.HasText) parts.Add("den Notfalltext");
        if (p.Files.Count > 0) parts.Add(p.Files.Count == 1 ? "eine Datei" : $"{p.Files.Count} Dateien");
        if (p.Account) parts.Add("das ganze Konto");
        if (p.Admin) parts.Add("Verwaltungsrechte");
        return parts.Count == 0 ? "nichts" : string.Join(", ", parts);
    }

    public string Name(string userId) =>
        services.GetRequiredService<UserStore>().ById(userId) is { } u ? (string.IsNullOrWhiteSpace(u.DisplayName) ? u.Login : u.DisplayName) : "jemand";

    private string When(string user, DateTime utc)
    {
        try { return TimeZoneInfo.ConvertTimeFromUtc(utc, services.GetRequiredService<Spaces>().Get(user).Zone).ToString("dd.MM.yyyy, HH:mm", CultureInfo.InvariantCulture) + " Uhr"; }
        catch (Exception) { return utc.ToString("dd.MM.yyyy, HH:mm", CultureInfo.InvariantCulture) + " UTC"; }
    }

    /// <summary>Ueber Pukki - auch wenn der Benutzer die Unterhaltung ausgeblendet hat: ein Notfall geht vor.</summary>
    private void Tell(string user, string text, IReadOnlyList<ChatAction>? actions = null)
    {
        try
        {
            var chat = services.GetRequiredService<ChatStore>();
            var room = Pukki.RoomOf(chat, user) ?? chat.OpenDirect(user, Pukki.Id);
            chat.Post(Pukki.Id, room, text, actions: actions);
        }
        catch (Exception e) { log.LogWarning(e, "Notfallzugang: Nachricht an {User} nicht zugestellt", user); }
    }
}
