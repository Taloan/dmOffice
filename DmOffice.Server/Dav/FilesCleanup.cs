using DmOffice.Core;

namespace DmOffice.Dav;

/// <summary>Stuendlich: Papierkorb der Dateien nach den eingestellten Tagen leeren, alte Versionen und Verwaistes
/// (Vorschaubilder, Favoriten, Links endgueltig geloeschter Dateien) wegraeumen, den Link-Index abgleichen.</summary>
public sealed class FilesCleanup(Spaces spaces, LinkIndex links, ILogger<FilesCleanup> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(2), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            foreach (var space in spaces.All())
            {
                using var activity = Activity.Start(space.User.Id, Activity.FilesCleanup);
                try
                {
                    var n = FileRetention.Apply(space.Db, DateTime.UtcNow) + FileStore.Expire(space.Db, 0, DateTime.UtcNow);
                    if (n > 0) log.LogInformation("Dateien {User}: {Count} alte Einträge aus Papierkorb/Versionen gelöscht", space.User.Login, n);
                    links.Prune(space.User.Id, Links.Ids(space.Db));
                }
                catch (Exception ex) { activity.Fail(ex.Message); log.LogWarning("Dateien aufräumen {User}: {Error}", space.User.Login, ex.Message); }
            }
            try { await Task.Delay(TimeSpan.FromHours(1), ct); } catch (OperationCanceledException) { return; }
        }
    }
}
