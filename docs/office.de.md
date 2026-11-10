# Office im Browser (Collabora, ONLYOFFICE)

Deutsch · [English](office.md)

dmOffice kann Word-, Excel- und PowerPoint-Dateien (DOCX, XLSX, PPTX, auch ODT, ODS, ODP, RTF, CSV) aus der
Dateiablage direkt im Browser bearbeiten. Dafür braucht es einen eigenen Office-Server. Zwei Wege:

- **WOPI** (Collabora Online/CODE, ONLYOFFICE mit eingeschaltetem WOPI): dmOffice ist der WOPI-Host, der
  Office-Server holt und speichert die Datei über `/wopi/files/…`. Empfohlen: **Collabora CODE**, auch auf dem Handy.
- **ONLYOFFICE Docs-API** (api.js + Callback, mit JWT): für einen vorhandenen ONLYOFFICE Document Server, ohne
  am Container etwas umzustellen. dmOffice erkennt das selbst: Gibt es unter der Adresse keine WOPI-Discovery, aber
  `web-apps/apps/api/documents/api.js`, nimmt es diesen Weg.

Code: `DmOffice.Server/Office/` (Discovery, Tokens, WOPI-Endpunkte, `OnlyOffice.cs`, leere Vorlagen), Oberfläche
`web/src/files/Office.tsx`. Tests: `DmOffice.Tests/WopiTests.cs` (gefälschtes Collabora mit Proof-Key) und
`OnlyOfficeTests.cs` (gefälschter Document Server mit JWT).

## Welche Office-Server es gibt (Stand 20.10.2026)

| | Collabora Online / CODE | ONLYOFFICE Docs Community | Euro-Office |
| --- | --- | --- | --- |
| In Nextcloud | „Nextcloud Office“ (App richdocuments) | App „ONLYOFFICE“ | Nextcloud-App „Euro-Office“ (ab Nextcloud 33); in AIO wählbar unter „Office Suite“ |
| Herkunft | Collabora, auf LibreOffice-Basis | Ascensio System (ONLYOFFICE) | Fork von ONLYOFFICE (März 2026) von Nextcloud, IONOS, Proton u. a.; erste stabile Version 9. Juni 2026 |
| Lizenz | MPL 2.0 (CODE) | AGPL v3 (mit Zusatzbedingungen zu Logo/Namensnennung) | AGPL v3 (die Zusatzbedingungen wurden gestrichen – darüber streitet ONLYOFFICE mit den Projekten) |
| Bearbeiten im Handy-Browser | ja, eigene Handy-Oberfläche | **nein** – die „Mobile web editors“ sind ein Merkmal der Enterprise Edition; die Community Edition öffnet auf dem Handy nur zum Ansehen (für 9.x keine Änderung gefunden) | nicht belegt; Grundlage ist der ONLYOFFICE-Code |
| Grenzen | in der Grundeinstellung keine; die alten 10 Dokumente / 20 Verbindungen gelten nur noch im „Home Mode“. Collabora empfiehlt CODE nicht für Firmen (kein Support, laufende Entwicklerversion) | bis 9.3: 20 gleichzeitige Verbindungen; **ab 9.4 (Mai 2026) entfällt die Grenze** | keine bekannt |
| Docker-Image | `collabora/code` (Unraid: in den Community Apps nach „Collabora“ suchen, sonst von Hand anlegen) | `onlyoffice/documentserver` | `nextcloud/aio-eurooffice` (AIO), eigenes Image des Projekts |
| WOPI | ja, das ist die Schnittstelle von Collabora (mit Proof-Keys) | ja, seit 6.4, **aus** in der Grundeinstellung (`wopi.enable` in `local.json`, im Container vermutlich `WOPI_ENABLED=true`); Nextcloud nutzt sonst die eigene Docs-API | Nextcloud spricht die ONLYOFFICE-Docs-API; WOPI vermutlich wie bei ONLYOFFICE, nicht geprüft |

Fazit für dmOffice: **Collabora CODE** – frei, ohne Grenze, bearbeitet auf dem Handy, spricht WOPI von Haus aus.
Ein schon laufender ONLYOFFICE geht ebenso (Docs-API mit JWT, siehe unten), bearbeitet in der freien Ausgabe
auf dem Handy aber nach allem, was sich finden ließ, nicht.

Quellen (abgerufen am 20.10.2026):
- Collabora-Forum, „Does Collabora CODE 26.04.2.4 have any user or concurrent connection limits“: https://forum.collaboraonline.com/t/does-collabora-code-26-04-2-4-have-any-user-or-concurrent-connection-limits/5023
- Collabora FAQ (CODE nicht für den Firmeneinsatz): https://www.collaboraonline.com/faqs/
- Linuxiac, 20.05.2026, „ONLYOFFICE Docs 9.4 Removes Community Connection Limit“: https://linuxiac.com/onlyoffice-docs-9-4-removes-community-connection-limit/
- ONLYOFFICE, Ausgaben im Vergleich: https://www.onlyoffice.com/compare-editions; Forum „Mobile web docs editing …“: https://community.onlyoffice.com/t/mobile-web-docs-editing-stopped-working-with-docs-home-server-license/11352
- ONLYOFFICE API, WOPI: https://api.onlyoffice.com/docs/docs-api/using-wopi/overview/ und https://api.onlyoffice.com/docs/Docs%20API/Using%20WOPI/WOPI%20discovery
- Euro-Office: https://en.wikipedia.org/wiki/Euro-Office; gHacks, 06.04.2026: https://www.ghacks.net/2026/04/06/nextcloud-ionos-and-proton-launch-euro-office-an-onlyoffice-fork-for-european-users/; heise zum Lizenzstreit: https://www.heise.de/en/news/Euro-Office-OnlyOffice-accuses-of-license-violations-11241334.html
- Nextcloud AIO mit Euro-Office: https://github.com/nextcloud/all-in-one, https://hub.docker.com/r/nextcloud/aio-eurooffice, https://blog.wirelessmoves.com/2026/08/moving-to-euro-office-part-1-nextcloud-aio.html

## Wie es in dmOffice funktioniert

- **Verwaltung → Office im Browser**: Adresse des Office-Servers eintragen, „Prüfen und speichern“. dmOffice liest
  `<Adresse>/hosting/discovery` und meldet klar, was nicht stimmt (nicht erreichbar, Zertifikat, Umleitung,
  kein WOPI). Leer = aus; ohne Office-Server bleibt die Dateiablage wie bisher.
- **Dateiablage**: Office-Dateien öffnen per Doppelklick (bzw. Tippen) im Editor, Kontextmenü „Im Browser
  bearbeiten“, „Neu → Word-Dokument / Excel-Arbeitsmappe / PowerPoint-Präsentation“ legt eine leere Datei an und
  öffnet sie. Der Editor füllt die Seite; „Zurück“ (oben links, der Schließen-Knopf von Collabora oder die
  Zurück-Taste am Handy) führt in die Ablage. CSV öffnet weiter im Texteditor, im Kontextmenü gibt es Collabora.
- **Direktlink**: `https://<dmOffice>/#/files/:office/<Pfad>` öffnet das Dokument im Editor.
- **Handy-App**: öffnet Office-Dateien selbst im Editor, siehe [unten](#handy-app).
- **Öffentliche Links**: bei „nur ansehen“ zeigt der Link Office-Dateien im Betrachter des Office-Servers
  (zählt als ein Abruf). Bearbeiten über Links gibt es nicht.
- **Speichern** geht über die Dateiablage: frühere Versionen, Papierkorb, Volltextsuche, Vorschaubilder und
  Speichergrenze greifen wie sonst. Der notizi-Abgleichordner bleibt gesperrt (auch für bereits ausgegebene Tokens).

### Sicherheit

- `/wopi/files/{id}` kennt kein Anmelde-Cookie, nur das `access_token`: mit Data Protection signiert und
  verschlüsselt, je Benutzer und Datei, gilt 10 Stunden (Collabora erneuert es während einer Sitzung nicht).
  Neues Passwort oder „überall abmelden“ macht es sofort ungültig; bei öffentlichen Links endet es mit dem Link.
- Das Token geht per POST in das iframe, steht also in keiner Adresse und keinem Verlauf.
- **Proof-Keys**: Veröffentlicht der Office-Server Schlüssel in der Discovery (Collabora tut das), prüft dmOffice
  `X-WOPI-Proof`/`X-WOPI-ProofOld` und den Zeitstempel (höchstens 20 Minuten alt). Ohne passende Signatur: 500.
- **Konflikte**: Sperren (Lock/Unlock/RefreshLock/GetLock, 30 Minuten) im Speicher des Servers. Collabora schickt
  beim Speichern mit, welchen Stand es geladen hat (`X-COOL-WOPI-Timestamp`); hat sich die Datei inzwischen
  anders geändert (WebDAV, Handy, Nextcloud-Client), antwortet dmOffice mit 409/1010 und Collabora fragt, ob
  überschrieben oder neu geladen werden soll.
- „Speichern unter“ und Export (PutRelativeFile) legen die neue Datei im selben Ordner an, nie im notizi-Ordner.

## Einrichtung auf Unraid mit Collabora CODE

dmOffice läuft z. B. unter `https://office.example.com` hinter einem Reverse Proxy (Nginx Proxy Manager o. ä.).

**1. Eigene Subdomain für Collabora** (empfohlen), etwa `https://collabora.example.com`, im DNS auf denselben Proxy.

**2. Container starten** – in den Community Apps nach „Collabora“ suchen (CODE-Vorlage) oder unter Docker →
Add Container:

| Feld | Wert |
| --- | --- |
| Repository | `collabora/code` |
| Port | Container 9980 → Host z. B. 9980 |
| Variable `aliasgroup1` | `https://office.example.com:443` (die Adresse von **dmOffice** – nur dieser WOPI-Host darf Dateien öffnen) |
| Variable `server_name` | `collabora.example.com` (so meldet sich Collabora gegenüber dem Browser) |
| Variable `extra_params` | `--o:ssl.enable=false --o:ssl.termination=true` (TLS macht der Proxy) |
| Variable `dictionaries` | `de_DE en_GB en_US` (optional) |
| Variable `username` / `password` | für die Admin-Konsole unter `/browser/dist/admin/admin.html` (optional) |
| Extra Parameters | `--cap-add MKNOD` (optional, schnelleres Öffnen) |

Mit Docker direkt:

```bash
docker run -d --name collabora -p 9980:9980 -e "aliasgroup1=https://office.example.com:443" -e "server_name=collabora.example.com" -e "extra_params=--o:ssl.enable=false --o:ssl.termination=true" --cap-add MKNOD --restart unless-stopped collabora/code
```

**3. Proxy-Host** `collabora.example.com` → `http://<unraid-ip>:9980`, **Websockets an**, SSL-Zertifikat wie für dmOffice.

**4. dmOffice → Verwaltung → Office im Browser**: Adresse `https://collabora.example.com`, „Prüfen und speichern“.
Grün: „Verbunden mit Collabora Online … Anfragen werden mit Proof-Keys geprüft.“ Danach in der Dateiablage
„Neu → Word-Dokument“ – das Dokument öffnet sich im Editor.

### Wenn sich die Container nicht über die öffentliche Adresse erreichen

dmOffice liest die Discovery, Collabora ruft dmOffice (WOPI) – beide über die eingetragenen Adressen. Kommt man aus
dem Heimnetz nicht über die öffentliche Adresse herein (kein „Hairpin-NAT“):

- Adresse des Office-Servers in dmOffice: `http://<unraid-ip>:9980` (der Browser bekommt trotzdem
  `https://collabora.example.com`, das meldet Collabora über `server_name`).
- „Adresse von dmOffice für den Office-Server“: `http://<unraid-ip>:8080`; in Collabora dann
  `aliasgroup1=http://<unraid-ip>:8080` und in `extra_params` zusätzlich `--o:net.frame_ancestors=https://office.example.com`
  (sonst verweigert der Browser das Einbetten).

### Gleiche Domain statt Subdomain

Geht auch: Im Proxy für `office.example.com` die Pfade `/browser`, `/hosting`, `/cool` (Websocket) und `/lool` an
`http://<unraid-ip>:9980` leiten, alles andere an dmOffice. dmOffice belegt diese Pfade nicht. In Collabora dann
`server_name=office.example.com`, in dmOffice als Office-Adresse `https://office.example.com`. Die Subdomain ist einfacher
und vermeidet Überraschungen bei Updates.

### Getestet mit Collabora CODE 26.04

CODE 26.04 als Container auf Unraid, dahinter ein Reverse Proxy mit eigener Subdomain. Collabora spricht auf 9980
selbst TLS mit eigenem Zertifikat, also im Proxy das Schema **https** wählen; mit `http` kommt 502 Bad Gateway.

- Discovery über die öffentliche Adresse gelesen, die Adressen im Editor lauten `https://<collabora-host>/browser/…`.
- Collabora ruft dmOffice per WOPI auf (`CheckFileInfo` 200) – der Weg Office-Server → dmOffice funktioniert.
- Dieser Container veröffentlicht **keine Proof-Keys**; dmOffice prüft dann nur das Token (Verwaltung zeigt das an).
- Einbetten: Collabora erlaubt es nur für `frame-ancestors <Collabora-Host>:* <WOPI-Host>:*`; eine Quelle ohne Schema passt
  nicht auf eine http-Seite. Mit dmOffice unter https passt es. Für einen Test mit dmOffice über http stand am Container
  zusätzlich `--o:net.frame_ancestors=http://<dmoffice-host>:<port>` in `extra_params`.
- **Bearbeiten und Speichern gehen**: Excel-Arbeitsmappe geöffnet, Text und Zahl eingetragen, Strg+S – Collabora ruft
  CheckFileInfo, GetFile, Lock, PutFile, Unlock (alle 200); der neue Inhalt steht in der Ablage, der alte als frühere
  Version. Die leeren Vorlagen (Word A4, Excel „Tabelle1“) öffnen sauber; der Direktlink `#/files/:office/…` auch.
- **Offen**: Collabora schickte der Seite keine PostMessage-Nachrichten (`App_LoadingStatus`, `UI_Close`). Folge:
  das X oben rechts in Collabora schließt nicht (ausgegraut), die Ladeanzeige verschwindet erst nach 15 Sekunden.
  „Zurück“ in der Leiste von dmOffice schließt wie gewünscht. `PostMessageOrigin` stimmt (geprüft); Ursache noch
  nicht gefunden – nach dem Einspielen unter https erneut prüfen.

### Nur im Heimnetz, ohne Reverse Proxy?

Geht, ist aber nicht zu empfehlen:

- Der Browser muss das Zertifikat von Collabora (`https://<unraid-ip>:9980`, „Dummy Authority“) akzeptieren –
  auf jedem Gerät einzeln, auf dem Handy umständlich. dmOffice selbst prüft das Zertifikat beim Lesen der Discovery
  und lehnt ein selbst signiertes ab (ein Schalter „Zertifikat nicht prüfen“ ist bewusst nicht eingebaut).
- Alternativ Collabora ohne TLS (`--o:ssl.enable=false`) und dmOffice ebenfalls über http im Heimnetz aufrufen –
  dann unverschlüsselt, und `aliasgroup1`/`frame_ancestors` müssen die http-Adresse von dmOffice nennen.
- In beiden Fällen geht es unterwegs (Handy im Mobilnetz) nicht, und eine https-Seite (`office.example.com`) darf keinen
  Editor von `http://<unraid-ip>…` einbetten.

Mit einer eigenen Subdomain hinter dem Proxy geht beides – zuhause und unterwegs – ohne Zertifikatswarnungen.

### Fehlersuche

- „nicht erreichbar“: Adresse vom dmOffice-Container aus prüfen (`docker exec dmoffice wget -qO- https://collabora.example.com/hosting/discovery`).
- Leerer Editor, in der Browser-Konsole „violates … frame-ancestors“: dmOffice läuft über http oder unter einer
  Adresse, die Collabora nicht kennt – dmOffice über https aufrufen bzw. `aliasgroup1`/`net.frame_ancestors` anpassen.
- 502 Bad Gateway am Proxy: Collabora spricht auf 9980 https – Weiterleitung mit Schema https, oder Collabora mit
  `--o:ssl.enable=false --o:ssl.termination=true` auf http umstellen.
- Editor bleibt leer oder „Unauthorized WOPI host“: `aliasgroup1` stimmt nicht mit der dmOffice-Adresse überein (Schema, Host, Port).
- „Proof-Key-Prüfung fehlgeschlagen“ im dmOffice-Log: Collabora ruft dmOffice unter einer anderen Adresse auf als
  der eingetragenen WOPI-Adresse – Feld „Adresse von dmOffice für den Office-Server“ setzen.
- Speichern scheitert mit „Speicher voll“: Speichergrenze des Benutzers (Verwaltung).

## Vorhandener ONLYOFFICE Document Server (Docs-API)

Warum zusätzlich zur WOPI-Schnittstelle: Ein installierter ONLYOFFICE spricht seine Docs-API ohne Umstellung, WOPI
müsste erst am Container eingeschaltet werden. Nextcloud und Euro-Office nutzen denselben Weg.

**Ablauf.** Die Oberfläche lädt `api.js` vom Document Server; dmOffice gibt die Konfiguration, mit dem
JWT-Geheimnis signiert (HS256). Der Document Server holt die Datei unter `<dmOffice>/onlyoffice/file?t=…` und
meldet den Stand an `<dmOffice>/onlyoffice/callback?t=…`. `t` ist ein eigenes, kurzlebiges Token je Benutzer und
Datei (wie bei WOPI); der Callback muss außerdem mit dem JWT-Geheimnis signiert sein. Bei Status 2 (fertig) oder 6
(Zwischenspeichern) lädt dmOffice das Ergebnis – nur vom eingetragenen Document Server, egal welcher Host in der
Meldung steht – und speichert es über die Dateiablage. Wurde die Datei inzwischen anderswo geändert, wird nicht
überschrieben, sondern „Name (Konflikt).docx“ daneben angelegt.

### Einrichtung

Beispiel: ONLYOFFICE als Container auf Unraid (Port 8900), erreichbar unter `https://onlyoffice.example.com`.

1. **Reverse Proxy**: Proxy-Host `onlyoffice.example.com` → `http://<unraid-ip>:8900`, **Websockets an**, SSL-Zertifikat
   (Let's Encrypt), große Uploads erlauben (Nginx Proxy Manager → Advanced: `client_max_body_size 512m;`, dazu
   `proxy_read_timeout 3600s;`). Den Host weitergeben (`X-Forwarded-Host`, `X-Forwarded-Proto` – NPM tut das),
   sonst baut der Document Server Adressen mit `http://` und der Browser blockiert sie.
   *Prüfen: `https://onlyoffice.example.com/healthcheck` antwortet `true`, `api.js` lädt.*
2. **HTTPS ist Pflicht**: Läuft dmOffice über https, würde ein Editor von `http://<unraid-ip>:8900` als gemischter
   Inhalt blockiert und ist unterwegs nicht erreichbar. dmOffice warnt beim Eintragen einer `http://`-Adresse.
3. **dmOffice → Verwaltung → Office im Browser**:
   - Adresse des Office-Servers: `https://onlyoffice.example.com`
   - JWT-Geheimnis: der Wert von `JWT_SECRET` des ONLYOFFICE-Containers (Unraid: Container bearbeiten → Variable
     `JWT_SECRET`; wurde keine gesetzt, erzeugt das Image eines beim ersten Start – dann im Container in
     `/etc/onlyoffice/documentserver/local.json` unter `services.CoAuthoring.secret.inbox.string`). Wird verschlüsselt
     mit dem Hauptschlüssel gespeichert und nie wieder angezeigt.
   - JWT-Kopfzeile nur, wenn am Container `JWT_HEADER` gesetzt ist (Vorgabe `Authorization`).
   - „Prüfen und speichern“: dmOffice fragt `CommandService.ashx` mit signierter Anfrage nach der Version. Ohne oder
     mit falschem Geheimnis kommt eine klare Meldung („verlangt ein JWT-Geheimnis“ / „passt nicht“).
4. **Rückweg**: Der Document Server muss die Adresse von dmOffice erreichen (Datei holen, Callback). Geht das im
   Heimnetz nicht (kein Hairpin-NAT), unter „Adresse von dmOffice für den Office-Server“ `http://<unraid-ip>:8080`
   eintragen. Die Container-Einstellung `ALLOW_PRIVATE_IP_ADDRESS=true` braucht ONLYOFFICE dann, weil es sonst keine
   Dateien von privaten Adressen lädt.

Am Container muss sonst nichts geändert werden (JWT ist dort schon an).

### Was geprüft ist und was nicht

- Gegen einen echten ONLYOFFICE-Server (`api.js` 10.0.0, Web-Apps 9.3.5): Erkennung als Docs-API, Meldung „verlangt
  ein JWT-Geheimnis“, mit erfundenem Geheimnis „passt nicht“; `api.js` lädt in einer dmOffice-Seite, der Editor startet
  und lehnt eine unsignierte Konfiguration ab („Sicherheitstoken des Dokuments ist nicht korrekt“).
- Nicht gegen den echten Server: Öffnen, Speichern und Callback mit dem richtigen Geheimnis. Der Ablauf ist mit einem
  nachgebauten Document Server und eigenem Testschlüssel geprüft (`OnlyOfficeTests`). Erster echter Test: Geheimnis
  eintragen, „Neu → Word-Dokument“, etwas tippen, Editor schließen; nach einigen Sekunden steht der neue Stand in der Ablage.
- Handy: Die Community Edition bearbeitet im Handy-Browser nach allen Quellen nicht (nur Ansehen). Für das Handy
  ist Collabora der richtige Weg.

### ONLYOFFICE über WOPI (Alternative)

dmOffice liest auch die WOPI-Discovery von ONLYOFFICE (Platzhalter wie `<ui=UI_LLCC&>` werden ersetzt). Dazu müsste
am Container WOPI eingeschaltet werden (`"wopi": {"enable": true}` in `local.json`, im Image vermutlich
`WOPI_ENABLED=true`). Nicht geprüft; die Docs-API ist für einen vorhandenen Server der einfachere Weg.

## Grenzen und Ideen

- Sperren liegen im Speicher: nach einem Neustart von dmOffice gelten sie als frei (Collabora sperrt beim nächsten Speichern neu).
- Kein Umbenennen aus dem Editor heraus (`SupportsRename` aus) und keine Bearbeitung über öffentliche Links.
- Handy-App: siehe unten.

## Handy-App

Die Android-App öffnet DOCX, XLSX, PPTX, ODT, ODS und ODP aus der Dateiablage im Editor des Office-Servers
(eigene Seite mit WebView, `ui/OfficeActivity.kt`), sobald ein Office-Server eingestellt ist:

- **Antippen** öffnet im Editor, das Drei-Punkte-Menü hat „In Office bearbeiten“ (bzw. „ansehen“); „In App öffnen“
  bleibt für fremde Apps. CSV öffnet beim Antippen weiter in einer App. Ohne Office-Server (`/api/office/config`
  liefert `enabled: false`) ist alles wie bisher.
- **Hochladen-Knopf → Word-Dokument / Excel-Arbeitsmappe / PowerPoint-Präsentation** legt über `/api/office/new` eine
  leere Datei an und öffnet sie.
- Sitzung über `/api/office/open` mit dem App-Passwort (im Umschlag wie alles andere). WOPI: Formular-POST mit
  `access_token` direkt in die WebView (keine Einbettung, also kein `frame-ancestors`-Problem). Docs-API: eine
  kleine Seite mit `api.js` und der signierten Konfiguration, geladen mit der Adresse von dmOffice als Herkunft;
  „Schließen“ im Editor (`onRequestClose`) schließt auch in der App.
- **Zurück-Taste** oder Pfeil oben schließt den Editor, die Ordnerliste lädt neu – und nach 15 Sekunden noch einmal,
  weil ONLYOFFICE erst nach dem Schließen speichert. Drehen und Tastatur laden den Editor nicht neu.
- Drei-Punkte-Menü im Editor: „Neu laden“ und (nur ONLYOFFICE) „Handy-Ansicht“, siehe unten.

### Bearbeiten auf dem Handy mit ONLYOFFICE / Euro-Office – was sich sagen lässt (20.10.2026)

Mit dem echten Server nicht geprüft – dafür braucht es das JWT-Geheimnis. Was sich aus dem Code eines
ONLYOFFICE-Servers (Web-Apps im Pfad `9.3.5-…`) ablesen ließ:

- `api.js` erkennt das Handy **nicht** selbst. Welche Oberfläche kommt, bestimmt allein das Feld `type` der
  Konfiguration: `mobile` → Handy-Editor, sonst der normale („Desktop“-)Editor. dmOffice setzt `type` nicht, also
  bekommt auch das Handy den normalen Editor. Der leitet auf dem Handy nicht um.
- Im Handy-Editor hängt Bearbeiten an der Lizenz (`isEdit = canLicense && …`); laut ONLYOFFICE sind die „Mobile web
  editors“ Teil der Enterprise Edition, die Community Edition (und damit vermutlich Euro-Office) zeigt dort nur an.
- Folgerung: In der App sollte **Bearbeiten gehen, aber in der Desktop-Oberfläche** – vollständig, jedoch eng auf
  einem schmalen Bildschirm (quer halten hilft). Mit „Handy-Ansicht“ im Menü kommt die Handy-Oberfläche (`type:
  mobile`, außerhalb des signierten Teils) – dort bei der freien Ausgabe voraussichtlich nur Ansehen.
- Ob der Document Server eine Konfiguration ohne `type` im Token annimmt, wenn außen `type: mobile` steht, ist
  ebenfalls nicht live geprüft (im Token steht `type` nicht; nach der Doku gilt dann der äußere Wert).

Für bequemes Bearbeiten auf dem Handy bleibt **Collabora CODE** die bessere Wahl (eigene Handy-Oberfläche, frei).

### Geprüft (Emulator, Android 15, eigener Testserver, gefälschte Office-Server)

- WOPI (Fake-Collabora): Antippen öffnet, Token per POST, CheckFileInfo/GetFile, Tippen mit Tastatur, Speichern mit
  Lock/PutFile/Unlock (alte Version bleibt), Zurück-Taste zurück in die Liste, neue Größe/Zeit sichtbar,
  „Neu → Word-Dokument“ legt an und öffnet.
- Docs-API (Fake-ONLYOFFICE mit JWT): Konfiguration signiert, Herkunft = dmOffice-Adresse, Datei geholt, „Speichern und
  schließen“ schließt in der App, Callback speichert, Liste zeigt den Stand nach 15 s; „Handy-Ansicht“ setzt
  `type: mobile`; Drehen lädt nicht neu.
- Ohne Office-Server: Antippen öffnet wie bisher die App-Auswahl, keine Office-Einträge im Neu-Menü.
- Nicht geprüft: echter Collabora/ONLYOFFICE/Euro-Office auf dem Handy, Bild einfügen (Dateiauswahl), „Herunterladen als“.
