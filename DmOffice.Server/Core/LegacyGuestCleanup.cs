using System.Text.Json;
using DmOffice.Chat;

namespace DmOffice.Core;

/// <summary>
/// Fruehere Versionen kannten Gastkonten: guests.json neben users.json, dazu Schluessel "guests.*" in chat.db. Beim Start
/// einmal alles davon entfernen, jedes Konto so wie beim Loeschen eines Benutzers. Scheitert ein Konto, bleibt die Datei
/// fuer den naechsten Start stehen; der Server startet in jedem Fall.
/// </summary>
public static class LegacyGuestCleanup
{
    private const string KvPrefix = "guests.";

    private sealed class Entry
    {
        public string? Id { get; set; }
    }

    public static void Run(IServiceProvider services, ILogger log)
    {
        var paths = services.GetRequiredService<DataPaths>();
        var file = Path.Combine(paths.Root, "guests.json");
        if (!File.Exists(file)) return;
        try
        {
            var entries = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(file)) ?? new();
            var users = services.GetRequiredService<UserStore>();
            int removed = 0, failed = 0;
            foreach (var id in entries.Select(e => e.Id).OfType<string>().Distinct())
            {
                // Nur gueltige Kennungen, und nie ein Konto aus users.json
                if (id.Length != 32 || !id.All(Uri.IsHexDigit) || users.ById(id) != null) continue;
                try
                {
                    AdminApi.Erase(services, id, systemVault: false);
                    // Belegte Dateien bleiben beim Löschen liegen: dann beim nächsten Start noch einmal
                    if (File.Exists(paths.UserDb(id))) failed++; else removed++;
                }
                catch (Exception e) { failed++; log.LogWarning("Gastkonto entfernen: {Error}", e.GetType().Name); }
            }
            if (failed > 0)
            {
                log.LogWarning("{Failed} von {Count} Gastkonten nicht entfernt - nächster Versuch beim nächsten Start", failed, removed + failed);
                return;
            }
            var chat = services.GetRequiredService<ChatStore>();
            foreach (var key in new[] { "guest-yes", "guest-no" }) chat.ClearActionsWith(key);
            foreach (var (key, _) in chat.KvLike(KvPrefix)) chat.DeleteKv(key);
            File.Delete(file);
            if (File.Exists(file + ".tmp")) File.Delete(file + ".tmp");
            log.LogInformation("{Count} Gastkonten entfernt", removed);
        }
        catch (Exception e) { log.LogWarning("Gastkonten aufräumen: {Error}", e.GetType().Name); }
    }
}
