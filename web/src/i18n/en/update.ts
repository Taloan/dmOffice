// Englische Texte: Update-Sicherheit und Migrations-Assistent (Verwaltung → Updates)
export const update: Record<string, string> = {
  'unter einer Minute': 'under a minute',
  'etwa {n} Stunden': 'about {n} hours',
  'startet gleich': 'starting shortly',
  'wartet auf „Jetzt starten“': 'waiting for “Start now”',
  'zu wenig Platz': 'not enough space',
  'Platz für die Sicherung fehlt': 'no space for the backup',
  'fehlgeschlagen': 'failed',
  'pausiert': 'paused',
  'aktuell': 'up to date',
  'Gestartet': 'Started',
  'Wird geprüft': 'Checking',
  'Sicherung „{name}“ ({size}) endgültig löschen?': 'Delete backup “{name}” ({size}) permanently?',
  'Updates und Datenänderungen': 'Updates and data changes',
  'Bei einer neuen Version bringt dmOffice die Daten selbst auf den neuen Stand. Vorher prüft es den freien Platz und sichert die Datenbanken; reicht der Platz nicht, wird nichts verändert, und hier steht, was fehlt. Große Umbauten starten erst nach „Jetzt starten“.':
    'With a new version, dmOffice brings the data up to date by itself. Beforehand it checks the free space and backs up the databases; if there is not enough space, nothing is changed and this page says what is missing. Large conversions only start after “Start now”.',
  'Version {v}, Datenstand {n}': 'Version {v}, data schema {n}',
  'Frei im Datenordner: {size}': 'Free in the data folder: {size}',
  'Frei bei den Dateiinhalten: {size}': 'Free for file contents: {size}',
  'Reserve: {size}': 'Reserve: {size}',
  'Alle Daten sind auf dem neuesten Stand, es steht nichts an.': 'All data is up to date, nothing is pending.',
  'Datenstand {from} → {to}': 'Data schema {from} → {to}',
  'schreibt viele Daten um': 'rewrites a lot of data',
  'Datenbank: {size}': 'Database: {size}',
  'Platz für den Umbau: {size}': 'Space for the conversion: {size}',
  'Sicherung vorhanden': 'Backup exists',
  'Sicherung: {size}': 'Backup: {size}',
  'ohne Sicherung': 'without backup',
  'Frei: {size}': 'Free: {size}',
  'Dauer: {eta}': 'Duration: {eta}',
  'Es fehlen etwa {missing} freier Platz. Bis genug da ist, wird nichts verändert – die Daten bleiben unberührt, dieser Benutzer kommt nur noch nicht an seine Daten. Platz schaffen (Dateien löschen, größeren Datenträger einbinden) und „Erneut prüfen“.':
    'About {missing} of free space is missing. Nothing is changed until there is enough – the data stays untouched, this user just cannot reach their data yet. Free up space (delete files, attach a bigger disk) and press “Check again”.',
  'Für die Sicherung vor dem Umbau fehlen etwa {missing}. Entweder Platz schaffen und „Erneut prüfen“ – oder ausdrücklich ohne Sicherung fortfahren (dann hilft bei einem Fehler nur deine eigene Sicherung des Datenordners).':
    'About {missing} is missing for the backup before the conversion. Either free up space and press “Check again” – or explicitly continue without a backup (then only your own copy of the data folder helps if something goes wrong).',
  'Dieser Umbau ist groß ({eta}, {size} Platz) und startet nicht von selbst. Der Server läuft währenddessen weiter, dieser Benutzer wartet. Am besten vorher den ganzen Datenordner kopieren.':
    'This conversion is large ({eta}, {size} of space) and does not start by itself. The server keeps running meanwhile; this user waits. Best copy the whole data folder first.',
  'Die Datenänderung ist gescheitert; die Daten sind unverändert (jeder Schritt gilt ganz oder gar nicht).':
    'The data change failed; the data is unchanged (every step is all or nothing).',
  'Sicherung wird angelegt …': 'Creating backup …',
  'Dateiinhalte aus der Datenbank in eigene Dateien auslagern': 'Moving file contents out of the database into separate files',
  '{n} Einträge, {size}': '{n} entries, {size}',
  'Es fehlen etwa {missing} Platz für die Dateiinhalte. Nichts wird angefasst, bis genug frei ist; alles bleibt wie bisher in der Datenbank und funktioniert weiter.':
    'About {missing} of space is missing for the file contents. Nothing is touched until enough is free; everything stays in the database as before and keeps working.',
  'Das Auslagern ist groß und startet nicht von selbst. Es löscht nichts: Die alten Inhalte bleiben in der Datenbank, bis du unter Einstellungen → Statistik verdichtest. Dafür braucht es dann noch einmal so viel freien Platz.':
    'Moving is large and does not start by itself. It deletes nothing: the old contents stay in the database until you compact it under Settings → Statistics. That needs as much free space again.',
  'Jetzt starten': 'Start now',
  'Ohne Sicherung fortfahren': 'Continue without backup',
  'Fortsetzen': 'Resume',
  'Pausieren': 'Pause',
  'Erneut prüfen': 'Check again',
  'Pausiert: Die Auslagerung hält nach der laufenden Portion an und macht mit „Fortsetzen“ weiter.':
    'Paused: moving stops after the current batch and continues with “Resume”.',
  'Sicherungen vor Datenänderungen': 'Backups before data changes',
  'Vor jedem Umbau legt dmOffice von jeder Benutzerdatenbank eine verschlüsselte Kopie an (samt Benutzerliste, Einstellungen, Freigaben und Schlüsseln) – aber nur, wenn der Platz reicht. Die Dateiinhalte (Blob-Dateien) werden nicht mitgesichert: Das Auslagern löscht nichts, die alten Inhalte bleiben in der Datenbank – und damit in dieser Sicherung –, bis du ausdrücklich verdichtest. Die Sicherungen zählen zum belegten Platz: {size}.':
    'Before every conversion dmOffice makes an encrypted copy of each user database (with the user list, settings, shares and keys) – but only if there is enough space. The file contents (blob files) are not backed up: moving deletes nothing, the old contents stay in the database – and so in this backup – until you explicitly compact. Backups count towards the used space: {size}.',
  'Aufbewahren': 'Keep',
  'nie löschen': 'never delete',
  'Danach räumt dmOffice sie selbst weg.': 'After that dmOffice removes them by itself.',
  'Keine Sicherungen vorhanden.': 'No backups.',
  'Sicherung': 'Backup',
  'Sicherung ansehen': 'View backup',
  'Sicherung {name}': 'Backup {name}',
  'Ordner: {path}': 'Folder: {path}',
  'Angelegt mit Version {v} am {date}, {size}.': 'Created with version {v} on {date}, {size}.',
  '{login}: Datenbank im Stand {schema} ({size})': '{login}: database at schema {schema} ({size})',
  'users.json, server.json, shares.json, Hauptschlüssel und keys/ (soweit vorhanden)': 'users.json, server.json, shares.json, master key and keys/ (where present)',
  'Zurückgehen': 'Going back',
  'dmOffice (den Container) stoppen und die Image-Version festnageln, die zu dieser Sicherung passt, z. B. ghcr.io/taloan/dmoffice:{v}.':
    'Stop dmOffice (the container) and pin the image version that matches this backup, e.g. ghcr.io/taloan/dmoffice:{v}.',
  'Aus dem Sicherungsordner users/*.db nach {dir}/users/ kopieren; die gleichnamigen Dateien mit -wal und -shm dort vorher löschen.':
    'Copy users/*.db from the backup folder to {dir}/users/; first delete the files of the same name ending in -wal and -shm there.',
  'users.json, server.json, shares.json (und master.key, keys/) aus dem Sicherungsordner in {dir} zurückkopieren.':
    'Copy users.json, server.json, shares.json (and master.key, keys/) from the backup folder back to {dir}.',
  'Container starten. Änderungen seit der Sicherung gehen dabei verloren; Blob-Dateien müssen nicht zurück (überzählige räumt dmOffice selbst weg).':
    'Start the container. Changes since the backup are lost; blob files do not need to go back (dmOffice removes surplus ones itself).',
  'Ohne Sicherung fortfahren?': 'Continue without backup?',
  'Für die Sicherung reicht der Platz nicht. dmOffice baut die Datenbank trotzdem um – jeder Schritt ist für sich sicher (ganz oder gar nicht), aber wenn etwas Unerwartetes schiefgeht, gibt es keine Kopie von vorher. Hast du den Datenordner selbst gesichert?':
    'There is not enough space for the backup. dmOffice will convert the database anyway – every step is safe on its own (all or nothing), but if something unexpected goes wrong there is no copy from before. Have you backed up the data folder yourself?',
  'Die Daten werden auf den neuen Stand gebracht': 'The data is being brought up to date',
  'dmOffice wurde aktualisiert und bringt deine Daten gerade auf den neuen Stand. Das dauert oder wartet auf die Verwaltung (Platz, Freigabe). Nichts geht verloren – bitte später wieder öffnen.':
    'dmOffice was updated and is bringing your data up to date. This takes a while or waits for the administrator (space, approval). Nothing is lost – please open again later.',
  'Eine Datenänderung wartet auf Platz oder deine Freigabe.': 'A data change is waiting for space or your approval.',
  'Zu wenig freier Platz zum Verdichten: es fehlen etwa {missing} (gebraucht {need}, frei {free}). Es wurde nichts verändert.':
    'Not enough free space to compact: about {missing} is missing (needed {need}, free {free}). Nothing was changed.',
  'Das Verdichten schreibt die Datenbank vorübergehend noch einmal und braucht dafür etwa {need} freien Platz (frei: {free}).':
    'Compacting temporarily writes the database a second time and needs about {need} of free space (free: {free}).',
  'Das reicht nicht – erst Platz schaffen.': 'That is not enough – free up space first.',
  'Updates': 'Updates',
  // Schritte der Datenänderung
  'Grundstruktur': 'Basic structure',
  'Spamfilter bereinigen': 'Clean up spam filter',
  'Mailinhalte in eigene Tabelle': 'Mail contents into their own table',
  'Mail-Schlagwörter': 'Mail keywords',
  'Spamfilter neu bewerten': 'Re-rate spam filter',
  'Geräteschlüssel der App': 'App device keys',
  'Browser-Geräte': 'Browser devices',
  'Push-Tokens': 'Push tokens',
  'Prio-Posteingang': 'Priority inbox',
  'Speichermodus Schlank': 'Slim storage mode',
  'Dateinummern für Nextcloud-Clients': 'File numbers for Nextcloud clients',
  'Bereich der App-Passwörter': 'Scope of app passwords',
  'Papierkorb, Versionen und Links der Dateiablage': 'Trash, versions and links of the file store',
  'Volltextsuche': 'Full-text search',
  'Abrufgrenze der Freigabelinks': 'Download limit of share links',
  'Dateiinhalte als eigene Dateien': 'File contents as separate files',
  'Schlagwörter der Notizen': 'Note tags',
  'Prüfsummen der Dateien': 'File checksums',
  // Meldungen des Servers
  'Ohne Sicherung fortfahren muss ausdrücklich bestätigt werden.': 'Continuing without a backup must be confirmed explicitly.',
  'Sicherung nicht gefunden.': 'Backup not found.',
  'Die Datenänderung ist groß und wartet auf „Jetzt starten“ in der Verwaltung.': 'The data change is large and waits for “Start now” in the administration.',
  'Zu wenig freier Platz zum Verdichten.': 'Not enough free space to compact.',
}
