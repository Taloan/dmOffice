# Geteilte Ordner der Dateiablage
Deutsch · [English](geteilte-ordner.md)

Ein Ordner der Dateiablage lässt sich mit anderen Benutzern desselben Servers teilen – zum **Lesen** oder zum **Bearbeiten**, vom
Besitzer vergeben und jederzeit änderbar oder entziehbar. Gleiches Modell wie bei Kalendern, Aufgabenlisten und Notizordnern
(siehe [geteilte-kalender.de.md](geteilte-kalender.de.md), [geteilte-notizen.de.md](geteilte-notizen.de.md)): Der Inhalt **bleibt in der
Ablage des Besitzers**, der Server liest und schreibt dort für den Empfänger und prüft die Freigabe bei **jeder** Anfrage.
Keine Kopien, kein Schlüsselaustausch, Widerruf wirkt sofort. Geteilt werden nur **Ordner**; einzelne Dateien teilt man weiter per Link.

## Ablauf

- **Freigabe:** `data/file-shares.json` (`FolderShareStore`, `Dav/FileShares.cs`), eigene Datei neben `shares.json`.
  Ein Eintrag: Besitzer, `fileid` des Ordners (bleibt beim Umbenennen und Verschieben), Mitglieder, davon Nur-Lesen.
  Landet der Ordner im Papierkorb, ruht die Freigabe und kommt mit dem Zurückholen wieder.
- **Teilen-Dialog** (Dateien): oben „Mit Personen teilen“ (derselbe Baustein wie bei Kalendern: `usePeople`/`PeopleList`
  in `calendar/ShareDialog.tsx`), darunter „Link“ wie bisher (Ablauf, Abrufe, Passwort). Bei Dateien nur der Link.
- **Beim Empfänger** gibt es in der Wurzel einen virtuellen Ordner **„Mit mir geteilt“** (nur, solange jemand etwas teilt), darin die
  Ordner als „Name (von Besitzer)“. Er erscheint in `GET /api/files` als Ordner, im WebDAV/Nextcloud-Wurzelverzeichnis und in der
  Weboberfläche zusätzlich links in der Navigation. Ein eigener Eintrag gleichen Namens wird verdeckt, solange der Bereich existiert.
- **Auflösung:** `SharedFiles.Resolve(http, pfad)` macht aus dem Pfad des Benutzers einen `FileLoc` (Ablage, Pfad dort, geteilter
  Ordner, Schreibrecht). Alle Endpunkte (`/api/files/*`, `/dav`, `/remote.php`, Vorschau, Inhaltssuche, Office) gehen darüber.
  Ohne Freigabe: 404, wie bei einem Pfad, den es nicht gibt.
- **Datei-IDs** (Nextcloud `oc:fileid`, Vorschau `?fileId=`): beim Empfänger `Freigabe · 2^40 + fileid beim Besitzer`. Kollidiert nie mit
  eigenen, und `SharedFiles.ById` prüft die Freigabe und dass die Datei im geteilten Ordner liegt.

## Rechte

| | Besitzer | Bearbeiten | Lesen | ohne Freigabe |
|---|---|---|---|---|
| Ansehen, Herunterladen, Vorschau, Suche (Name und Inhalt), ZIP | ✔ | ✔ | ✔ | 404 |
| Hochladen, Ordner/Datei anlegen, Text speichern, Office bearbeiten, frühere Version zurückholen | ✔ | ✔ | 403 | – |
| Umbenennen, verschieben (innerhalb des geteilten Ordners), löschen | ✔ | ✔ | 403 | – |
| Den geteilten Ordner selbst umbenennen, verschieben, löschen | ✔ | 403 | 403 | – |
| Heraus in die eigene Ablage „verschieben“ | – | **kopiert**, Original bleibt | kopiert | – |
| Eigenes in den geteilten Ordner „verschieben“ | – | kopiert zum Besitzer, bei mir weg | 403 | – |
| Teilen, Links, Favoriten | ✔ | 403 | 403 | – |
| „Mit mir geteilt“ ausblenden (aussteigen) | – | `POST /api/files/shared/leave` | ✔ | – |

Unterordner erben die Freigabe, neue Unterordner sind sofort dabei. Der geschützte notizi-Ordner lässt sich nicht teilen, auch nicht
ein Ordner, der ihn enthält (400); sein Inhalt ist für Empfänger unsichtbar wie für den Besitzer in der Weboberfläche.

## Papierkorb, Versionen, Speicherplatz

Gelöschtes landet im Papierkorb des **Besitzers**, ebenso frühere Versionen; zurückholen kann nur er. Ein Upload des Empfängers prüft und
verbraucht das Kontingent des **Besitzers** (Meldung „Beim Besitzer ist kein Platz mehr frei“, 507). Eine Kopie in die eigene Ablage zählt
beim Empfänger. „Zuletzt verwendet“ bleibt je Benutzer.

## WebDAV und Nextcloud-Clients

`/dav/files/<login>/`, `/remote.php/dav/files/<login>/` und `/remote.php/webdav/` zeigen in der Wurzel „Mit mir geteilt“, darin die Ordner.
`oc:permissions` folgt dem Recht (`SG` nur lesen, `SGDNVW`/`SGDNVCK` bearbeiten, `SGCK` für den geteilten Ordner selbst), `oc:owner-id` ist der Besitzer.
Die Wurzel ändert ihr ETag, wenn sich in einem geteilten Ordner etwas ändert – so steigt der Client hinab. COPY/MOVE zwischen eigener Ablage
und geteiltem Ordner schreiben quer über zwei Ablagen (`SharedFiles.CopyAcross`).

## Office

Beim Öffnen einer Datei in einem geteilten Ordner stellt der Server ein Token für die Datei des **Besitzers** aus (`WopiGrant.V` = Mitglied).
WOPI und ONLYOFFICE prüfen die Freigabe bei **jeder** Anfrage neu (`SharedFiles.Allows`); schreiben darf nur, wer Bearbeiten hat. Ein Widerruf
beendet so auch laufende Sitzungen. Sperren (`WopiLocks`) gelten je Datei des Besitzers, also auch zwischen Besitzer und Mitglied.

## Besitzer: Übersicht

Datei/Ordner mit Personensymbol, wenn geteilt. In „Von mir geteilt“ stehen „Mit Personen geteilt“ (`GET /api/files/shares`) und „Per Link geteilt“.
Der Detailbereich zeigt die Mitglieder mit ihren Rechten.

## Android-App

Die App holt die Dateiliste über `GET /api/files?path=`, `download`, `thumb`, `upload`, `move`, `delete`. „Mit mir geteilt“ erscheint dort ohne
Änderung als Ordner, Vorschau und Herunterladen funktionieren über den Pfad. **Noch nötig**, damit es rund ist: die App kennt `canWrite`
(in `GET /api/files` und `/api/files/shared`) nicht – ohne Bearbeitungsrecht sieht der Benutzer erst beim Versuch die Fehlermeldung des Servers (403,
„Nur lesbar …“); Anlegen/Löschen/Umbenennen sollten dort ausgeblendet werden. Die Sortierung der Wurzel und die Fehlerbehandlung bei 403/507
sind in der App nicht geprüft. Das automatische Hochladen zielt auf eigene Ordner und bleibt unberührt.

## Prüfsummen und Duplikate

`POST /api/files/hashes` (Prüfsummen-Abfrage der App: „Ist dieser Inhalt schon gesichert?“) liest nur die eigene Ablage des Benutzers
(`http.Space().Db`) und kennt „Mit mir geteilt“ nicht – ein geteilter Ordner zählt also nicht als eigene Sicherung. Das soll so bleiben
(nicht über `SharedFiles` auflösen).

## Offene Entscheidungen

1. **Einzelne Dateien intern teilen:** vorerst nicht (Link). Spätere Erweiterung: `FolderShare.Folder` auch für Dateien zulassen.
2. **Gelöschtes zurückholen durch Bearbeiter:** nur der Besitzer sieht den Papierkorb; die Weboberfläche bietet dem Löschenden dafür kein „Rückgängig“ an. Empfehlung: so lassen.
3. **Kennzeichnung, wer zuletzt geändert hat:** gibt es nicht (Dateien kennen keinen Bearbeiter). Empfehlung: nicht bauen.
