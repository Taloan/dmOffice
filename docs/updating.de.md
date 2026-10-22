# dmOffice aktualisieren

*[English version](updating.md)*

dmOffice bringt seine Daten bei einer neuen Version selbst auf den neuen Stand – du musst nichts von Hand ausführen. Weil eine
neue Version die Datenbanken (und die Dateiinhalte) umbauen kann, hat dmOffice ein Sicherheitsnetz, das **ohne vorheriges
Kopieren des Datenordners** und **ohne Platz für eine volle Kopie** auskommt: Es prüft den freien Platz, legt selbst eine
Sicherung an, wenn Platz da ist, fragt vor großen Umbauten und hinterlässt nie eine halb fertige Änderung.

Trotzdem gilt: Ab und zu den Datenordner zu kopieren ist die beste Versicherung. Das Sicherheitsnetz schützt die Datenbanken,
nicht vor einer kaputten Platte oder einem gelöschten Container.

## So aktualisierst du

1. Neues Image holen (Unraid: *Nach Updates suchen* → *Update anwenden*; Docker: `docker compose pull && docker compose up -d`).
2. dmOffice öffnen. Gibt es nichts Besonderes, ist alles schon auf dem neuen Stand.
3. Wartet etwas (zu wenig Platz oder ein großer Umbau), zeigt die Oberfläche statt deiner Mails **Verwaltung → Updates**.
   Normale Benutzer sehen einen kurzen Hinweis; nichts geht verloren.

**Verwaltung → Updates** kannst du jederzeit öffnen: Was steht an oder läuft, wie viel Platz es braucht und wie viel frei ist,
die Sicherungen und die Schalter *Jetzt starten*, *Pausieren*, *Erneut prüfen* und *Sicherung ansehen*.

## Was dmOffice beim Start tut

Für jede Benutzerdatenbank, die älter ist als die neue Version:

1. **Platzprüfung.** dmOffice schätzt den zusätzlichen Platz, den der Umbau braucht (die Änderungen landen zuerst im
   Journal), plus 256 MB Reserve. Ist im Datenordner weniger frei, wird **nichts verändert**: Der Server läuft weiter, andere
   Benutzer sind nicht betroffen, und die Seite nennt genau, wie viel fehlt. Platz schaffen (Dateien löschen, größeren
   Datenträger einbinden) und *Erneut prüfen* – alle zehn Minuten versucht es dmOffice auch selbst wieder.
2. **Sicherung.** Reicht der Platz, schreibt dmOffice zuerst eine konsistente, verschlüsselte Kopie der Datenbank (SQLite
   `VACUUM INTO`, gleicher Schlüssel) nach `<data>/pre-update/<Datum>-<Version>/users/<id>.db`, dazu `users.json`,
   `server.json`, `shares.json`, die Hauptschlüssel-Datei (falls der Schlüssel im Datenordner liegt) und `keys/`. Die Sicherung
   entsteht nur, wenn sie neben dem Umbau noch Platz findet. Passt sie nicht, baut dmOffice **nicht** um, bis du Platz schaffst
   oder ausdrücklich *Ohne Sicherung fortfahren* wählst (und die Warnung bestätigst).
3. **Große Umbauten warten auf dich.** Kleine Schritte laufen automatisch. Ein Umbau, der mehr als 1 GB Platz braucht (oder
   mehrere Minuten dauert), startet nicht still beim ersten Start: Die Seite zeigt Zeit und Platz und wartet auf *Jetzt starten*.
   Der Server läuft währenddessen; nur der wartende Benutzer kommt noch nicht an seine Daten.
4. **Jeder Schritt gilt ganz oder gar nicht.** Jeder Schemaschritt läuft samt Versionsnummer in einer Transaktion. Wird er
   unterbrochen (Container gestoppt, Strom weg, Platte voll), wird er vollständig zurückgerollt und beginnt beim nächsten Start
   von vorn. Nichts ist halb umgebaut.

## Dateiinhalte aus der Datenbank auslagern (Schema 16)

Vor Schema 16 lag jede Datei in der Benutzerdatenbank. Jetzt ist jede Datei eine eigene verschlüsselte Datei (`blobs/`, oder
der Ordner aus `DMOFFICE_BLOBS`). Nach dem Schemaschritt lagert dmOffice die alten Inhalte im Hintergrund aus:

- Zuerst prüft es, ob am **Blob-Ort** Platz für alle noch in der Datenbank liegenden Inhalte ist (plus Reserve). Wenn nicht,
  wird nichts ausgelagert, alles läuft wie bisher aus der Datenbank weiter, und die Seite nennt, wie viel fehlt.
- Große Mengen (über 1 GB) warten auf *Jetzt starten*; *Pausieren* hält nach der laufenden Portion an, *Fortsetzen* macht weiter.
- Das Auslagern **löscht nichts, was du noch brauchen könntest**: Die alten Inhalte werden Zeile für Zeile in der Datenbank
  geleert, der Platz kommt aber erst zurück, wenn jemand *Datenbank verdichten* drückt (Einstellungen → Statistik). Bis dahin
  behält die Datenbankdatei ihre Größe, und die Sicherung vor dem Update enthält die alten Inhalte noch. Darum gehören die
  Blob-Dateien selbst **nicht** zur Sicherung.
- Es lässt sich jederzeit unterbrechen (Neustart, gestoppter Container); der nächste Start macht dort weiter.
- Danach rechnet dmOffice im Hintergrund die Prüfsummen (SHA-256, Schema 18) nach; das braucht fast keinen Platz.

## Datenbank verdichten

*Datenbank verdichten* schreibt die Datenbank noch einmal und braucht daher freien Platz in Höhe der Datenbank plus Reserve.
dmOffice prüft das vorher und zeigt, was fehlt, statt mittendrin zu scheitern. Verdichten sperrt die Datenbank für die Dauer
(bei mehreren GB Minuten).

## Sicherungen: wo, wie lange, wie groß

- Ordner: `<data>/pre-update/<Datum>-<Version>/` – die Verwaltung listet sie mit Datum, Benutzern und Größe und zählt sie zum
  belegten Platz. *Sicherung ansehen* zeigt den Ordner und den Weg zurück.
- dmOffice löscht Sicherungen nach **14 Tagen** selbst (einstellbar unter *Aufbewahren*, oder *nie löschen*), solange kein Umbau wartet.
- Die Sicherungen enthalten dieselben Geheimnisse wie der Datenordner (die Datenbanken sind verschlüsselt, aber `users.json`
  und – ohne `DMOFFICE_MASTER_KEY` – `master.key` liegen dabei). Schütze sie wie den Datenordner.

## Zurückgehen

1. Container stoppen und die Image-Version festnageln, die die Sicherung angelegt hat (etwa `ghcr.io/taloan/dmoffice:1.1.0`
   statt `latest`).
2. `pre-update/<Sicherung>/users/*.db` nach `<data>/users/` kopieren (die gleichnamigen `*.db-wal`- und `*.db-shm`-Dateien vorher löschen).
3. `users.json`, `server.json`, `shares.json` (und `master.key`, `keys/`) aus der Sicherung in den Datenordner zurückkopieren.
4. Container starten. Änderungen seit der Sicherung gehen verloren. Blob-Dateien müssen nicht zurück; überzählige werden aufgeräumt.

## Was sich in welcher Version geändert hat

| Release | Schema | Datenänderung | Aufwand |
|---|---|---|---|
| 1.0 | 9 | Prio-Posteingang | – |
| 1.1 | 10 | Speichermodus „Schlank“ für Mail | klein |
| nächste | 11 | Feste Dateinummern für Nextcloud-Clients | schreibt jede Dateizeile samt Inhalt neu, solange der noch in der Datenbank liegt: braucht etwa die Größe der Dateiinhalte an zusätzlichem Platz, bei GB Minuten |
| nächste | 12–15 | Bereich der App-Passwörter, Papierkorb und Versionen der Dateiablage, Volltextsuche, Abrufgrenze der Links | klein |
| nächste | 16 | Dateiinhalte als eigene verschlüsselte Dateien | Schemaschritt klein; das Auslagern danach braucht die Größe der Dateiinhalte am Blob-Ort; die Datenbank schrumpft erst mit *Verdichten* |
| nächste | 17 | Schlagwörter der Notizen (Rezepte) | klein |
| nächste | 18 | SHA-256-Prüfsumme je Datei | klein; die Berechnung im Hintergrund liest jede Datei einmal |

Die Aktualisierung von 1.0 oder 1.1 führt die Schritte 11 bis 18 in einem Zug aus; ein automatischer Test deckt das mit
Datenbanken ab, die der echte Code von 1.0 und 1.1 angelegt hat.

## Hinweise für besondere Aufbauten

- `DMOFFICE_BLOBS` (eigenes Volume für Dateiinhalte): Die Platzprüfung fürs Auslagern schaut auf dieses Volume, die für den
  Datenbankumbau und die Sicherung auf den Datenordner.
- Netzwerk-Dateisysteme melden manchmal falschen freien Platz. `DMOFFICE_ASSUME_FREE_MB` ersetzt die Messung durch eine feste
  Zahl in Megabyte (auch praktisch, um die Prüfungen auszuprobieren).
