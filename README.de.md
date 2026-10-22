# dmOffice

**Deutsch** · [English](README.md)

**Webseite: [dmoffice.de](https://dmoffice.de)** (deutsch) – Funktionen, Anleitungen und Neuigkeiten · **Quellcode und Fehlermeldungen:** hier auf GitHub

Ein selbst gehostetes Büro im Browser: E-Mail, Kalender, Kontakte, Aufgaben, Notizen und Dateien – mit einem eigenen Server dazu. Alles läuft in einem Container, mit einer verschlüsselten Datenbank je Benutzer.

| Bereich | Was es kann |
|---|---|
| **E-Mail** | IMAP/SMTP, beliebig viele Konten. Mit lokalem Speicher: je Konto *alles* oder *N Monate* zurück, vollständig samt Anhängen in der Datenbank. Volltextsuche (`von:`, `an:`, `betreff:`) über alle Ordner und Konten, Regeln (verschieben, kopieren, weiterleiten, markieren, löschen), Kategorien, Wiedervorlage und ein lernender Junk-Filter nach SpamBayes. Ziehen auf Ordner, gewohnte Tastenkürzel, Bilder aus dem Netz blockiert, Entwürfe |
| **Kalender** | Tag, Arbeitswoche, Woche, Monat, Liste; Serien (nur dieses / ganze Serie), Erinnerungen, mehrere farbige Kalender, Einladungen per Mail mit Antworten, Kalender-Abos (`webcal://`), Import/Export `.ics`. Kalender lassen sich mit anderen Benutzern teilen; ein Kalender der Art *Essensplan* bekommt eine Wochenplanung |
| **Kontakte** | Adressbücher, Fotos, Adressvorschläge beim Schreiben, Import/Export `.vcf` |
| **Aufgaben** | Mein Tag, Wichtig, Geplant, Listen – als VTODO, also auch in jeder Aufgaben-App am Handy. Listen lassen sich mit anderen Benutzern teilen |
| **Notizen** | Die Notizen aus **notizi**, über dessen Ende-zu-Ende-verschlüsselten Abgleichordner. dmOffice ist ein weiteres Gerät wie das Handy. Notizordner lassen sich mit anderen Benutzern teilen |
| **Dateien** | Eigene Dateiablage im Browser und per WebDAV: Vorschau, Volltextsuche (mit Texterkennung für Scans und Fotos), Papierkorb, frühere Versionen, Ordner für andere Benutzer teilen, Freigabelinks (ansehen, Einwurf-Ordner, Passwort, Ablaufdatum). Word-, Excel- und PowerPoint-Dateien lassen sich im Browser bearbeiten, mit einem eigenen Office-Server (Collabora Online oder ONLYOFFICE) |
| **Android-App** | Kalender (mit Essensplan und Verschieben per Ziehen), Aufgaben und Einkaufslisten, Mail, Kontakte, Notizen und Dateien auf dem Handy, mit Widgets, Offline-Dateien, Checklisten zum Abhaken ohne Verbindung, automatischem Hochladen von Fotos und „Speicherplatz freigeben“ |
| **Server** | Eigener **CalDAV**-, **CardDAV**- und **WebDAV**-Dienst mit **App-Passwörtern** – ein zusätzlicher Cloud-Server ist nicht nötig. Kompatibel mit DAVx⁵, Thunderbird, Apple Kalender/Kontakte, dem Windows-Explorer und den Nextcloud-Clients |
| **Sicherheit** | Mehrere Benutzer, je Benutzer eine **verschlüsselte Datenbank** (SQLCipher), jede gespeicherte Datei einzeln verschlüsselt, Zwei-Faktor-Anmeldung (TOTP) mit Wiederherstellungscodes |
| **Darstellung** | Hell, dunkel oder wie das System; Oberfläche deutsch und englisch; als PWA installierbar |

**Handbuch mit Bildern:** [Deutsch](docs/handbuch.md) · [English](docs/manual.md) · Mehr dazu und Neuigkeiten auf [dmoffice.de](https://dmoffice.de)

![dmOffice](docs/img/de/mail.png)

## Aufbau

```
DmOffice.Server/        ASP.NET Core (net10.0) – API, Hintergrunddienste, DAV-Server
  Core/                 Benutzer, Hauptschlüssel, verschlüsselte Benutzerdatenbank, 2FA
  Mail/                 IMAP-Sync, lokaler Speicher, Suche, Regeln, Spamfilter, Senden
  Pim/                  Kalender, Aufgaben, Kontakte (Speicher und Web-API), Freigaben, Abos
  Dav/                  CalDAV/CardDAV/WebDAV (RFC 4918, 4791, 6352, 6578), Dateien, Links
  Notes/                notizi-Abgleich (Notizi.Core) über das eigene WebDAV
  Office/               WOPI- und ONLYOFFICE-Anbindung zum Bearbeiten im Browser
  Search/               Volltextindex und Texterkennung (OCR)
web/                    React + Fluent UI 9, FullCalendar
apps/mobile/            Android-App (Kotlin, Jetpack Compose)
vendor/Notizi.Core/     Kopie aus notizi – tools/notizi-core-holen.ps1 aktualisiert sie
DmOffice.Tests/         Prüfungen, darunter notizi als zweites Gerät über HTTP
unraid/dmoffice.xml     Unraid-Vorlage (Community Applications), Icon dmoffice.png
ca_profile.xml          Profil für Community Applications
.github/workflows/      baut das Image nach ghcr.io/taloan/dmoffice und die Android-APK
.gitea/workflows/       baut das Image in die Registry einer eigenen Gitea
```

### Konfiguration

dmOffice wird über Umgebungsvariablen eingestellt. Nötig ist eigentlich nur der Hauptschlüssel.

| Variable | Bedeutung |
|---|---|
| `DMOFFICE_MASTER_KEY` | Hauptschlüssel, der die Datenbankschlüssel einpackt, z. B. aus `openssl rand -hex 32`. Siehe [Daten und Schlüssel](#daten-und-schlüssel) |
| `DMOFFICE_DATA` | Datenverzeichnis. Im Image `/data` (unter Windows `data` neben dem Programm) |
| `DMOFFICE_BLOBS` | Optional: Inhalte der Dateiablage hier statt in `<Daten>/blobs` (etwa ein eigenes Volume auf dem Array). Zusammen mit dem Datenverzeichnis sichern |
| `DMOFFICE_MS_CLIENT_ID` | Optional: Client-ID einer eigenen Microsoft-App-Registrierung für Outlook.com/Hotmail/Live; leer nimmt die eingebaute, `off` schaltet die Microsoft-Anmeldung ab. Siehe [Microsoft-Konten](#outlookcom-hotmail-und-live) |
| `DMOFFICE_TRUSTED_PROXIES` | Optional: Netze (CIDR, durch Komma getrennt), deren `X-Forwarded-*`-Kopfzeilen gelten, z. B. `203.0.113.0/24,2001:db8::/32`. Vorgabe: Loopback und private Netze |
| `DMOFFICE_ANON_LIMIT` | Optional: ansteigende Bremse für Anfragen **ohne** Anmeldung, je Adresse (IPv6 je /64). Vorgabe: 300 auf einmal, dann 240 je Minute. Wer darüber liegt, ist 30 s gesperrt, und jeder weitere Verstoß verdoppelt die Sperre (bis 1 Stunde). Angemeldete Anfragen werden nie gebremst. Eine Zahl setzt die Anfragen je Minute, `off` schaltet die Bremse ab |
| `DMOFFICE_INDEX_THREADS` | Optional: Parallelität von Volltextindex und Texterkennung (1–8). Vorgabe 1, ab vier Kernen 2 |
| `DMOFFICE_FCM_CREDENTIALS` | Optional: Pfad zum Firebase-Dienstkonto-Schlüssel für Push zur Android-App (Vorgabe `<Daten>/keys/fcm.json`). Siehe [apps/mobile/README.de.md](apps/mobile/README.de.md) |
| `DMOFFICE_DAV_LOG` | `1` protokolliert CalDAV-/CardDAV-Anfragen zur Fehlersuche (nicht die Dateiablage, keine Passwörter) |
| `TZ` | Zeitzone des Containers (Protokolle) |

Der Container hört auf Port **8080** (`ASPNETCORE_URLS`) und legt seine Daten im Volume `/data` ab. Die Versionsnummer in der Oberfläche setzt das Image (`DMOFFICE_VERSION`).

### Daten und Schlüssel

```
/data/users.json        Anmeldenamen, Passwort-Hashes, eingepackte Datenbankschlüssel, 2FA
/data/users/<id>.db     SQLCipher – alles eines Benutzers: Einstellungen, Mails mit Anhängen,
                        Kalender, Kontakte, Notizen, App-Passwörter (nur Hashes), Namen der Dateien
/data/blobs/<id>/       Inhalte der Dateiablage (Dateien, Versionen, Papierkorb, Vorschaubilder), jede Datei
                        einzeln mit AES-GCM verschlüsselt, Schlüssel aus dem der Datenbank; mit DMOFFICE_BLOBS
                        auch anderswo (etwa ein eigenes Volume auf dem Array)
/data/keys/             Schlüssel für das Anmelde-Cookie (und optional fcm.json)
/data/master.key        nur, wenn DMOFFICE_MASTER_KEY fehlt
/data/*.json            Servereinstellungen, Freigaben, Freigabelinks
```

Jede Benutzerdatenbank hat einen eigenen Zufallsschlüssel. Diese Schlüssel sind mit dem **Hauptschlüssel** eingepackt. Der kommt aus der Umgebungsvariable `DMOFFICE_MASTER_KEY`, in Unraid steht er maskiert in der Vorlage auf dem USB-Stick, nicht in `appdata`. Wer `appdata` kopiert, kann damit nichts lesen. Fehlt die Variable, legt dmOffice `master.key` im Datenverzeichnis an und warnt in der Verwaltung. Wird die Variable später gesetzt, packt der nächste Start alle Schlüssel um und löscht die Datei.

Ältere Installationen hatten die Dateiinhalte in der Datenbank. Nach dem Update lagert dmOffice sie im Hintergrund in `blobs/` aus (unterbrechbar, ein Neustart macht weiter). Der Platz in der Datenbank wird erst frei mit *Einstellungen* → *Speicher & Statistik* → *Datenbank verdichten* – das startet niemand von selbst. Vor jeder Datenänderung prüft dmOffice den freien Platz und sichert die Datenbanken selbst (`pre-update/`); reicht der Platz nicht, ändert es nichts und sagt in der Verwaltung, wie viel fehlt. Einzelheiten und der Weg zurück: [Aktualisieren](docs/updating.de.md). Ein Abbild des ganzen Datenverzeichnisses bleibt trotzdem die beste Versicherung.

**Hauptschlüssel verloren heißt Daten verloren.** Gut aufheben, etwa im Passwortmanager.

## Unraid

Das Image baut GitHub Actions bei jedem Push auf `master` nach `ghcr.io/taloan/dmoffice:latest` (ein Tag `v1.2.3` ergibt zusätzlich `:1.2.3` und `:1.2`).

1. **Vorlage** – über *Apps* (Community Applications) nach `dmOffice` suchen. Ohne Community Applications: [`dmoffice.xml`](https://github.com/Taloan/unraid-templates/blob/main/dmoffice.xml) aus [Taloan/unraid-templates](https://github.com/Taloan/unraid-templates) auf den USB-Stick nach `config/plugins/dockerMan/templates-user/my-dmoffice.xml` legen und *Docker* → *Add Container* → *Template* `dmOffice`.
2. **Installieren** – `DMOFFICE_MASTER_KEY` setzen (`openssl rand -hex 32`), Port wählen (Vorgabe 8080), *Apply*.
3. **Einrichten** – `http://<unraid>:8080/` öffnen: Der erste Aufruf legt das Verwaltungskonto an.

Mit reinem Docker leistet `compose.yaml` dasselbe: `DMOFFICE_MASTER_KEY` in eine `.env` schreiben und `docker compose up -d` ausführen.

### Image aus einer eigenen Gitea

`.gitea/workflows/docker.yml` baut dasselbe Image in die Container-Registry einer Gitea auf demselben Server; Docker zieht es über `127.0.0.1`, weil eine Registry ohne https nur über localhost angenommen wird.

1. **Repo** – in der Gitea anlegen, Actions einschalten (*Einstellungen* → *Actions*).
2. **Runner** – ein `act_runner` mit Label `ubuntu-latest:docker://docker:cli` und dem Docker-Socket des Hosts.
3. **Token** – im Repo unter *Einstellungen* → *Actions* → *Geheimnisse* `REGISTRY_TOKEN` anlegen (Zugangstoken mit `write:package`).
4. **Bauen** – Push auf `master` oder *Actions* → `docker` → *Ausführen*. Danach steht das Paket `dmoffice` unter *Pakete*.
5. **Vorlage** – wie oben, aber unter *Repository* `127.0.0.1:<gitea-port>/<besitzer>/dmoffice:latest` eintragen.

### Von außen erreichbar

Für Handys unterwegs und den Windows-Explorer braucht es HTTPS. Am einfachsten über den vorhandenen Reverse-Proxy (Nginx Proxy Manager, SWAG, Traefik) auf den Port von dmOffice. dmOffice wertet `X-Forwarded-Proto` und `X-Forwarded-Host` aus, die Adressen unter *Einstellungen* → *Geräte & Sync* stimmen dann. Für die automatische Erkennung leitet dmOffice `/.well-known/caldav` und `/.well-known/carddav` selbst weiter.

### Als PWA installieren

dmOffice ist eine installierbare Web-App (PWA): unter Windows, macOS, Linux, Android und iOS läuft es dann in einem eigenen Fenster mit eigenem Symbol. In dmOffice unter *Einstellungen* → *Allgemein* → *Als PWA installieren* (Chrome, Edge, Android), auf iPhone/iPad in Safari über *Teilen* → *Zum Home-Bildschirm*. Voraussetzung ist HTTPS (oder `localhost`). Statische Dateien liegen danach im Gerät, Post und Termine kommen immer frisch vom Server; ohne Verbindung erscheint ein Hinweis. Nach einem Update bietet die App „Neu laden“ an. Die Symbole zeichnet `python tools/pwa-icons.py`.

### Android-App

Die native Android-App (Android 8 und neuer) liegt als signierte APK bei den [GitHub-Releases](https://github.com/Taloan/dmOffice/releases) (`dmOffice.apk`) und aktualisiert sich von dort. In dmOffice zeigt *Einstellungen* → *Konto* → *Geräte & Sync* → *Handy-App koppeln* einen QR-Code, der die App einrichtet. Einzelheiten: [apps/mobile/README.de.md](apps/mobile/README.de.md).

## Outlook.com, Hotmail und Live

Private Microsoft-Konten (`outlook.com`, `hotmail.*`, `live.*`, `msn.com`) nehmen für IMAP und SMTP kein Passwort mehr an, nur OAuth2. dmOffice bringt dafür eine eigene App-Registrierung mit („dm Mailsuite“ – so heißt sie auf der Zustimmungsseite von Microsoft), es ist nichts einzurichten.

Wer lieber eine eigene Registrierung verwendet (etwa in einer Firma), legt sie im [Entra-Portal](https://entra.microsoft.com) unter *App-Registrierungen* an: Kontotypen *Alle Konten von Entra ID-Mandanten und persönliche Microsoft-Konten*, keine Umleitungs-URI, unter *Authentifizierung* **Öffentliche Clientflows zulassen** auf *Ja*. Der Name darf keine Microsoft-Marken wie „Office“ oder „Outlook“ enthalten. Die *Anwendungs-ID (Client)* dann als `DMOFFICE_MS_CLIENT_ID` setzen (Unraid: erweiterte Ansicht der Vorlage, Docker Compose: `.env`) und dmOffice neu starten. `DMOFFICE_MS_CLIENT_ID=off` schaltet die Microsoft-Anmeldung ganz ab.

Danach erkennt *Einstellungen* → *Konto* → *Konto hinzufügen* die Adresse und zeigt **Mit Microsoft anmelden**: Code kopieren, `microsoft.com/link` öffnen, Code eingeben, anmelden und den Zugriff erlauben. dmOffice wartet, prüft IMAP und SMTP und legt das Konto an. Das Refresh-Token liegt wie die Passwörter anderer Konten in der verschlüsselten Datenbank des Benutzers. Läuft es ab (etwa nach einer Passwortänderung bei Microsoft), im Konto auf **Neu anmelden** gehen.

## Geräte verbinden

In dmOffice unter *Einstellungen* → *Geräte & Sync* je Gerät ein **App-Passwort** erstellen. Dort stehen auch die Adressen.

| Programm | Einstellung |
|---|---|
| Android (DAVx⁵) | „Mit URL und Benutzername anmelden“, URL `https://<host>/dav/` |
| iPhone/iPad | Einstellungen → Kalender → Accounts → Andere → CalDAV; Server: nur der Hostname. Genauso CardDAV |
| Thunderbird | Kalender: CalDAV-Adresse; Adressbuch: CardDAV-Adresse |
| Windows-Explorer / Finder | Netzlaufwerk bzw. „Mit Server verbinden“: `https://<host>/dav/files/<login>/` |
| notizi | Einstellungen → Sync → Verbindung → WebDAV, Adresse `…/dav/files/<login>/notizisync/` |

Die Nextcloud-Clients für Desktop und Handy arbeiten gegen `/remote.php/dav/` und `/remote.php/webdav/`; siehe [docs/nextcloud-clients.de.md](docs/nextcloud-clients.de.md).

### Von Nextcloud umziehen

1. In Nextcloud jeden Kalender als `.ics` und jedes Adressbuch als `.vcf` herunterladen.
2. In dmOffice im Kalender bzw. in den Kontakten über das Menü der Sammlung *Importieren*.
3. notizi auf jedem Gerät auf die neue WebDAV-Adresse umstellen. Das erste Gerät legt den Abgleichordner neu an (oder vorher den Ordner `notizisync` aus Nextcloud unter *Dateien* hochladen). In dmOffice unter *Einstellungen* → *Notizen* das E2EE-Passwort eintragen und die Datenbank wählen.

## Weitere Dokumentation

| Thema | Deutsch | English |
|---|---|---|
| Handbuch | [docs/handbuch.md](docs/handbuch.md) | [docs/manual.md](docs/manual.md) |
| Office-Dateien im Browser bearbeiten (Collabora, ONLYOFFICE) | [docs/office.de.md](docs/office.de.md) | [docs/office.md](docs/office.md) |
| Kalender und Aufgabenlisten teilen | [docs/geteilte-kalender.de.md](docs/geteilte-kalender.de.md) | [docs/geteilte-kalender.md](docs/geteilte-kalender.md) |
| Notizordner teilen | [docs/geteilte-notizen.de.md](docs/geteilte-notizen.de.md) | [docs/geteilte-notizen.md](docs/geteilte-notizen.md) |
| Dateiordner teilen | [docs/geteilte-ordner.de.md](docs/geteilte-ordner.de.md) | [docs/geteilte-ordner.md](docs/geteilte-ordner.md) |
| Prüfsummen und Duplikate | [docs/duplikate.de.md](docs/duplikate.de.md) | [docs/duplikate.md](docs/duplikate.md) |
| Nextcloud-Clients | [docs/nextcloud-clients.de.md](docs/nextcloud-clients.de.md) | [docs/nextcloud-clients.md](docs/nextcloud-clients.md) |
| notizi verbinden | [docs/notizi-verbinden.de.md](docs/notizi-verbinden.de.md) | [docs/notizi-verbinden.md](docs/notizi-verbinden.md) |
| Web-Push | [docs/web-push.de.md](docs/web-push.de.md) | [docs/web-push.md](docs/web-push.md) |
| Android-App | [apps/mobile/README.de.md](apps/mobile/README.de.md) | [apps/mobile/README.md](apps/mobile/README.md) |

## Entwickeln

```bash
dotnet run --project DmOffice.Server
```

```bash
cd web && npm install && npm run dev
```

Vite läuft auf Port 5173 und leitet `/api` und `/dav` an den Server auf 5099 weiter (`dmOffice.code-workspace` setzt Port und Datenverzeichnis). Ohne Variable `DMOFFICE_DATA` liegen die Daten unter `bin/…/data`.

Prüfungen:

```bash
dotnet test
```

`NotiziInteropTests` laufen gegen einen laufenden Server, wenn `DMOFFICE_IT_URL`, `DMOFFICE_IT_USER`, `DMOFFICE_IT_TOKEN` und `DMOFFICE_IT_E2EE` gesetzt sind. `DevSeed` legt Beispielmails an (`DMOFFICE_SEED`, `DMOFFICE_SEED_USER`). Die Bilder der Doku erzeugt `tools/docs/make.sh [de|en]` aus einer leeren Datenbank mit erfundenen Beispieldaten (braucht Edge oder Chrome). Unter Windows muss der Datenpfad kurz sein: Natives SQLite scheitert an Pfaden über 260 Zeichen.

Mitmachen ist willkommen – siehe [CONTRIBUTING.md](CONTRIBUTING.md) (englisch).

## Grenzen

- Wiederholungen bearbeitet die Oberfläche als Täglich, Werktags, Wöchentlich, Zweiwöchentlich, Monatlich oder Jährlich. Andere Regeln aus anderen Programmen bleiben erhalten und werden angezeigt.
- Notizen: Bearbeitet werden Absätze und Listen. Tabellen, Bilder und Codeblöcke bleiben unverändert, wie auf dem Handy. notizi am PC nimmt eine Version sonst nicht an.
- vCard-Gruppen (`item1.EMAIL` mit `X-ABLabel` von Apple) verlieren beim Bearbeiten im Browser die Beschriftung. Der Wert bleibt.
- Sperren (WebDAV LOCK) werden bestätigt, aber nicht durchgesetzt. Schreiben ist ohnehin atomar.
- Office-Dokumente im Browser zu bearbeiten braucht einen eigenen Office-Server (Collabora Online oder ONLYOFFICE Docs); ohne ihn funktioniert die Dateiablage wie bisher.

## Lizenz

Copyright (C) 2026 Taloan

dmOffice ist freie Software: Sie steht unter der **GNU Affero General Public License, Version 3** ([LICENSE](LICENSE)). Sie dürfen dmOffice nutzen, verändern und weitergeben. Wer eine veränderte Version als Dienst im Netz anbietet, muss deren Quelltext den Nutzern ebenfalls unter der AGPL zugänglich machen. dmOffice wird ohne jede Gewährleistung bereitgestellt.

`vendor/Notizi.Core` stammt aus notizi desselben Autors und ist hier ebenfalls unter der AGPL-3.0 enthalten. Die fremden Bibliotheken (MIT, Apache-2.0, ISC, BSD) und ihre Lizenzen stehen in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md); alle sind mit der AGPL-3.0 vereinbar.
