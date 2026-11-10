# Nextcloud-Clients mit dmOffice (Machbarkeitsprobe)

Deutsch · [English](nextcloud-clients.md)

dmOffice spricht die Schnittstelle, die die offiziellen Nextcloud-Clients erwarten. Damit lässt sich die
Dateiablage mit der Nextcloud-Desktop-App (Windows, macOS, Linux) als echter lokaler Ordner abgleichen, ohne
Syncthing und ohne eigenen Sync-Client. Die Daten bleiben, wo sie sind: in der verschlüsselten Datenbank des
Benutzers (`FileStore`; die Inhalte liegen als verschlüsselte Blob-Dateien daneben, `Core/BlobStore.cs`), dieselbe Ablage wie unter `/dav/files/<login>/` und für notizi.

Code: `DmOffice.Server/Dav/NextcloudApi.cs` (Erkennung, OCS, Anmeldung) und `DavServer.cs` (WebDAV unter
`/remote.php`). Tests: `DmOffice.Tests/NextcloudClientTests.cs`.

## Was es gibt

| Adresse | Zweck |
| --- | --- |
| `GET /status.php` | Erkennung: `installed`, `version` (gemeldet wird 30.0.5), `productname` „dmOffice“ |
| `GET /ocs/v1.php/cloud/capabilities`, `/ocs/v2.php/…` | Fähigkeiten, ohne Anmeldung lesbar, nur das Nötige |
| `GET /ocs/v1.php/cloud/user`, `/ocs/v2.php/…` | Benutzer (`id` = Anmeldename, Anzeigename, Speicher) |
| `POST /index.php/login/v2`, `…/poll`, `…/flow/<token>` | Login Flow v2 (Desktop): Anmeldung im Browser, gibt ein App-Passwort aus |
| `GET /index.php/login/flow` | Login Flow v1 (ältere Handy-Apps): Weiterleitung auf `nc://login/…` |
| `/remote.php/dav/files/<login>/…`, `/remote.php/webdav/…` | WebDAV auf die Dateiablage |

- Anmeldung mit App-Passwort (Basic), wie bei `/dav`. Das Login-Passwort gilt nicht.
- Die Anmeldeseite des Flows nimmt die Cookie-Sitzung der Weboberfläche oder fragt Anmeldename, Passwort und
  ggf. den Zwei-Faktor-Code selbst ab (mit derselben Bremse gegen Durchprobieren). Das neue App-Passwort heißt
  nach dem Client („Nextcloud Desktop“) und lässt sich in den Einstellungen widerrufen.
- PROPFIND liefert zusätzlich `oc:id`, `oc:fileid`, `oc:permissions`, `oc:size` (Ordner: alles darunter),
  `oc:owner-id`, `oc:share-types`, `nc:has-preview`, `nc:is-encrypted`. Vorschaubilder gibt es unter
  `/index.php/core/preview` und `/index.php/apps/files/api/v1/thumbnail/…`.
- PUT, MKCOL, MOVE, COPY und GET antworten mit `OC-FileId` und `OC-ETag`; `X-OC-Mtime` wird übernommen
  (`X-OC-MTime: accepted`), die Änderungszeit kommt also auf allen Geräten gleich an.
- Jede Datei hat eine feste Nummer (Schema 11: Spalte `files.fileid`, Zähler `file_ids`). Sie bleibt beim
  Verschieben und Überschreiben und wird nie wiederverwendet. Daran erkennt der Client Verschiebungen.
- Ändert sich etwas, bekommen alle Ordner darüber bis zur Wurzel einen neuen ETag (`FileStore.Touch`). Der
  Client steigt nur in Ordner mit neuem ETag hinab.
- Hochladen in Stücken nach altem Verfahren (`OC-Chunked`) wird mit 501 abgelehnt, statt Stücke als Dateien abzulegen.

## Getestet mit dem echten Client

`nextcloudcmd` 35.0.0 (Kommandozeile, dieselbe Sync-Bibliothek wie die Desktop-App) gegen einen lokalen
Testserver auf `http://127.0.0.1`, zwei lokale Ordner als zwei Geräte („PC“ und „Handy“):

- Anmeldung: Login Flow v2 vollständig mit curl wie der Client (Start, Abfrage 404, Freigabe, Abfrage mit
  App-Passwort). Danach `capabilities` und `cloud/user` mit dem App-Passwort.
- Erstabgleich PC → Server: Ordner, leerer Ordner, Umlaute und `&` im Namen, 300-KB-Datei. Alles richtig.
- Zweites Gerät lädt alles identisch herunter (Prüfsummen gleich, Änderungszeit erhalten).
- Ändern, Umbenennen (als MOVE), Löschen, neue Datei am PC → kommen am zweiten Gerät an.
- Ordner auf dem zweiten Gerät verschieben → am PC als Verschiebung, ohne neuen Download.
- Leerlauf: nur PROPFINDs, keine Übertragung. Bei späteren Läufen steigt der Client nur in Ordner mit neuem ETag hinab.
- Konflikt (gleiche Datei auf beiden Geräten geändert): übliche „conflicted copy“, nichts geht verloren.
- Datei über `/dav/files/…` geschrieben (wie notizi) → kommt beim Nextcloud-Client an.
- 25 MB in einem PUT: geht. Der Client 35 schickt ohne `chunking`-Fähigkeit die ganze Datei auf einmal.
- `http://` wird von `nextcloudcmd` ohne Rückfrage angenommen.

**Nicht selbst getestet:** der Einrichtungsassistent der Desktop-App (die App lief auf dem Rechner bereits mit einem
Konto, eine zweite Instanz hätte nur die laufende angesprochen), die Android- und die iOS-App.

### Selbst testen (Desktop-App)

1. dmOffice mit diesem Stand starten (lokal oder hinter dem Proxy; dann `/status.php`, `/ocs/`, `/index.php/` und
   `/remote.php/` mit durchreichen, die Header `X-Forwarded-Proto` und `X-Forwarded-Host` setzen).
2. In der Nextcloud-App: Konto hinzufügen → Serveradresse `https://office.example.com` (lokal geht auch
   `http://localhost:5000`, die App warnt dann vor der unverschlüsselten Verbindung).
3. Der Browser öffnet die dmOffice-Seite „Zugriff erlauben?“ → anmelden bzw. bestätigen.
4. In der App einen lokalen Ordner wählen, ggf. nur einzelne Ordner abgleichen. Virtuelle Dateien gehen auch
   (Platzhalter aus PROPFIND, Herunterladen bei Bedarf über GET), sind aber ungetestet.

## Offene Punkte für den vollen Ausbau

**Speicher:** Die Inhalte liegen nicht mehr als BLOB in der Datenbank, sondern als einzelne verschlüsselte Dateien neben der
Benutzerdatenbank (Blöcke zu 64 KB, AES-GCM, eigener Schlüssel je Datei; `Core/BlobStore.cs`). Lesen und Schreiben laufen
als Strom. Offen bleibt die Grenze von 512 MB je Anfrage (Kestrel). Eine Deduplizierung über getrennte Uploads hinweg ist
bewusst nicht eingebaut (siehe `duplikate.de.md`).

**Hochladen in Stücken (nicht in der Probe):** Chunking v2 unter `/remote.php/dav/uploads/<login>/<transfer-id>/`:
MKCOL für den Vorgang, PUT je Stück (`OC-Total-Length`, `Destination`), zum Schluss `MOVE …/.file` auf das Ziel;
Stücke verschlüsselt zwischenlagern und nach Ablauf aufräumen; Fähigkeit `dav.chunking: "1.0"`. Erst damit lassen
sich abgebrochene große Uploads fortsetzen. Optional `dav.bulkupload` für viele kleine Dateien.

**Freigaben (nicht in der Probe):** OCS-Freigabe-API (`/ocs/v2.php/apps/files_sharing/api/v1/shares`), öffentliche
Links (`/s/<token>`), Freigaben an andere dmOffice-Benutzer (eingebunden als Ordner mit `oc:permissions` „S“/„M“
und eigenen Rechten), Benachrichtigung der Gegenseite über ETags.

**Ende-zu-Ende-Verschlüsselung (nicht in der Probe):** API `end_to_end_encryption` (öffentliche/private Schlüssel
des Benutzers, Metadaten je Ordner, Sperren eines Ordners mit `e2e-token` beim Hochladen), Fähigkeit
`end-to-end-encryption`. Der Server speichert dann nur, was der Client verschlüsselt schickt; Weboberfläche und
Suche sehen solche Ordner nicht mehr.

**Weitere Punkte:**

- Papierkorb und Versionen: Ein Löschen über den Client landet im Papierkorb von dmOffice und lässt sich dort in der
  Weboberfläche wiederherstellen. Der Papierkorb des Clients (`/remote.php/dav/trashbin/<login>/`, Fähigkeit
  `files.undelete`) und die Versionsliste (`versioning`) fehlen noch.
- Prüfsummen: dmOffice speichert je Datei die SHA-256 (siehe `duplikate.de.md`). Noch offen: `OC-Checksum` beim Hochladen
  auswerten, als `oc:checksums` und im Kopf beim GET zurückgeben und die Fähigkeit `checksums` melden. Dann prüft der
  Client jeden Download.
- Sofortige Benachrichtigung (`notify_push`); heute fragt der Client etwa alle 30 Sekunden die Wurzel ab.
- Avatare, Benachrichtigungen, Aktivität, Benutzerstatus und die
  Suche (WebDAV `SEARCH`, braucht die Fotoansicht der Handy-Apps) antworten mit 404. Der Desktop kommt ohne aus; ob
  die Handy-Apps alles Wichtige ohne sie können, ist ungeprüft.
- Android und iOS: Login Flow v1 und v2 sind da, aber nicht auf Geräten getestet.
- Diarium: Am PC gleicht die Nextcloud-Desktop-App den Ordner ab. Auf Android ist der eingebaute
  WebDAV-Abgleich von Diarium der bevorzugte Weg. Diarium spricht dort direkt mit der dmOffice-Ablage
  (`https://<server>/dav/files/<login>/<ordner>/` oder `https://<server>/remote.php/webdav/<ordner>/`, Anmeldename
  und App-Passwort). Die Nextcloud-App ist am Handy dafür nicht nötig. PC und Handy sehen dieselben Dateien, weil
  beide Adressen auf dieselbe Ablage zeigen. Noch zu testen: Diarium am Handy gegen dmOffice, und Diarium am
  Handy und die Desktop-App am PC zusammen auf demselben Ordner.
- Gemeldete Version 30.0.5: neu genug für heutige Clients; spätere Clients warnen eventuell bei alten Versionen.
  Die Zahl sollte mit den Nextcloud-Versionen wandern.
- Laufende Anmeldungen (Login Flow) liegen nur im Speicher; nach einem Neustart muss man im Client neu anfangen.
- Die Seite „Zugriff erlauben?“ ist eine schlichte eigene HTML-Seite, nicht Teil der Weboberfläche.
- Ein App-Passwort aus dem Flow gilt wie jedes andere auch für Kalender und Kontakte.
- Handbuch: Abschnitt „Dateien mit der Nextcloud-App abgleichen“ und Hinweise für den Reverse-Proxy ergänzen.
