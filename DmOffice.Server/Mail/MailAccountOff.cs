using DmOffice.Core;

namespace DmOffice.Mail;

/// <summary>
/// Kennung mailaccountoff: ein Mailkonto deaktivieren statt löschen. Deaktiviert ruft dmOffice es nicht ab, sendet nichts
/// darüber und zeigt es im Mailbereich nicht an (UserSettings.ActiveMailAccounts); Mails, Ordner und Einstellungen bleiben.
/// Nach dem Aktivieren holt der Abgleich ab dem gespeicherten Stand nur Neues.
/// </summary>
public static class MailAccountOff
{
    public record ActiveBody(bool Active);

    /// <summary>Ein Hinweis: Art, Anzahl, Name (Ersatz-Standardkonto). Den Text setzt die Oberfläche zusammen (übersetzt).</summary>
    public sealed record Note(string Kind, int Count = 0, string? Name = null);

    /// <summary>Was am Konto hängt und während der Deaktivierung ruht - für die Hinweiszeile in den Einstellungen.</summary>
    public static List<Note> Notes(UserSpace space, MailAccount a)
    {
        var notes = new List<Note>();
        var s = space.Settings;
        if (Features.On(Features.DefaultAccount) && s.DefaultMailAccount == a.Id)
            notes.Add(s.DefaultAccount() is { } d ? new Note("default", Name: string.IsNullOrWhiteSpace(d.Name) ? d.Email : d.Name) : new Note("defaultNone"));
        if (MailQueue.CountFor(space.Db, a.Id) is var q and > 0) notes.Add(new Note("outbox", q));
        if (CatchallAliases.ActiveFor(space.Db, a)) notes.Add(new Note("catchall"));
        var rules = s.Rules.Count(r => r.Enabled && (r.AccountId == a.Id
            || r.Actions.Any(x => x.Type is "move" or "copy" && x.Value.StartsWith(a.Id + "|"))));
        if (rules > 0) notes.Add(new Note("rules", rules));
        if (a.GraphCalendars || a.GraphContacts || a.GraphTasks) notes.Add(new Note("graph"));
        var waiting = Snooze.All(space.Db).Count(e => e.Account == a.Id) + MailExpiry.All(space.Db).Count(e => e.Account == a.Id);
        if (waiting > 0) notes.Add(new Note("waiting", waiting));
        if (Features.On(Features.Dms) && DmOffice.Dms.DmsMailSources.Of(space.Db).Any(x => x.Account == a.Id)) notes.Add(new Note("dms"));
        return notes;
    }

    public static void Map(RouteGroupBuilder api)
    {
        // Ein Konto deaktivieren oder aktivieren
        api.MapPut("/accounts/{id}/active", (string id, ActiveBody body, HttpContext http, MailSync sync, ImapPool pool) =>
        {
            if (!Features.On(Features.MailAccountOff)) return Results.NotFound();
            var space = http.Space();
            if (space.Settings.MailAccounts.FirstOrDefault(a => a.Id == id) is not { } acc) return Results.NotFound();
            space.UpdateSettings(s => s.MailAccounts.First(a => a.Id == id).Disabled = !body.Active);
            if (body.Active) sync.Kick(space.User.Id, id);
            else pool.Close(id);
            // Mailbereich, Zähler und App laden die Konten neu
            sync.Touch(space.User.Id);
            var a = space.Settings.MailAccounts.First(x => x.Id == id);
            return Results.Ok(new { disabled = a.Disabled, offNotes = a.Disabled ? Notes(space, a) : [] });
        });
    }
}
