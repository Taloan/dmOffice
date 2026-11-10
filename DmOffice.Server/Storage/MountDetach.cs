using DmOffice.Audio;
using DmOffice.Core;
using DmOffice.Dms;
using DmOffice.Gallery;

namespace DmOffice.Storage;

/// <summary>
/// Ein externer Ordner wird nicht mehr eingebunden: alles, was ihn nutzt, abschalten statt löschen - Galerie- und Audiothek-Ordner
/// (Überwachen aus), Importprofile, DMS-Quellen (auch über ein Ablageprofil mit Ziel darin) und die Klartext-Sicherung.
/// Jeweils mit Hinweis, damit sichtbar ist, warum. Gelöscht wird nichts.
/// </summary>
public static class MountDetach
{
    public const string Reason = "Externer Ordner nicht mehr eingebunden.";

    /// <summary>Verwendungen abschalten, die noch an sind; gibt ihre Zahl zurück (schon Abgeschaltetes zählt nicht).</summary>
    public static int Run(UserDb db, string mount)
    {
        int n = 0;

        var gallery = GalleryFolders.Of(db);
        var g = gallery.Where(s => s.Mount == mount && s.Error != Reason).ToList();
        foreach (var s in g) { s.Watch = false; s.Proxies = false; s.Error = Reason; }
        if (g.Count > 0) { GalleryFolders.Save(db, gallery); n += g.Count; }

        var audio = AudioFolders.Of(db);
        var a = audio.Where(s => s.Mount == mount && s.Error != Reason).ToList();
        foreach (var s in a) { s.Watch = false; s.Error = Reason; }
        if (a.Count > 0) { AudioFolders.Save(db, audio); n += a.Count; }

        foreach (var p in ImportProfiles.Of(db).Where(p => p.Active && (p.Mount == mount || p.Sources.Any(x => x.Mount == mount))))
        {
            ImportProfiles.Update(db, p.Id, x => { x.Active = false; x.Error = Reason; });
            n++;
        }

        // DMS: Quellen im Ordner und Quellen, deren Ablageprofil dorthin ablegt (ohne Profil gilt das Standardprofil)
        var profiles = db.GetJson<List<DmsProfile>>(DmsFiling.Key);
        var lost = profiles.Where(p => p.Mount == mount).ToList();
        var standard = profiles.FirstOrDefault(p => p.Default)?.Id;
        foreach (var s in DmsSources.Of(db).Where(s => s.Active))
        {
            var profile = lost.FirstOrDefault(p => p.Id == (s.Profile ?? standard));
            if (s.Mount != mount && profile == null) continue;
            var why = s.Mount == mount ? Reason : $"Ablageprofil „{profile!.Name}“: {Reason}";
            DmsSources.Update(db, s.Id, x => { x.Active = false; x.Error = why; });
            n++;
        }

        var backup = PlainBackup.Settings(db);
        if (backup.Enabled && backup.Target == PlainBackup.External && backup.Mount == mount)
        {
            backup.Enabled = false;
            PlainBackup.Save(db, backup);
            var state = PlainBackup.State(db);
            state.LastError = Reason;
            PlainBackup.Save(db, state);
            n++;
        }
        return n;
    }

    /// <summary>
    /// Verwendungen von Ordnern, die es gar nicht mehr gibt (weder in der Verwaltung noch bei der Person) - etwa entfernt, bevor es
    /// <see cref="Run"/> gab. Nur nicht sichtbare (Administrator hat WebDAV für Anwender ausgeschaltet) bleiben unberührt.
    /// </summary>
    public static int Orphans(UserSpace space, ServerConfig config)
    {
        var known = config.Current.ExternalFolders.Select(f => f.Id).Concat(ExternalFolders.Own(space).Select(m => m.Id)).ToHashSet();
        var db = space.Db;
        var used = GalleryFolders.Of(db).Select(s => s.Mount)
            .Concat(AudioFolders.Of(db).Select(s => s.Mount))
            .Concat(ImportProfiles.Of(db).SelectMany(p => p.Sources.Select(x => x.Mount).Append(p.Mount)))
            .Concat(DmsSources.Of(db).Select(s => s.Mount))
            .Concat(db.GetJson<List<DmsProfile>>(DmsFiling.Key).Select(p => p.Mount ?? ""))
            .Append(PlainBackup.Settings(db).Mount ?? "")
            .Where(m => m.Length > 0 && !known.Contains(m)).Distinct();
        return used.Sum(m => Run(db, m));
    }
}
