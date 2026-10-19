# dmOffice in Unraid Community Applications (CA) aufnehmen

Diese Schritte brauchen die eigenen Konten (GitHub, Unraid-Forum) und sind deshalb von Hand zu erledigen.
Stand der Regeln: <https://ca.unraid.net/submit/help> (gilt vor älteren Forenbeiträgen).

## Voraussetzungen (im Repo erledigt)

- Öffentliches GitHub-Repo `Taloan/dmOffice` mit OSI-Lizenz (`LICENSE`, AGPL-3.0).
- `ca_profile.xml` im Wurzelverzeichnis mit nicht leerem `<Profile>`.
- Eine Vorlage je App: `unraid/dmoffice.xml` mit `<Repository>ghcr.io/taloan/dmoffice:latest</Repository>`,
  `<TemplateURL>` auf genau diese Datei (raw), `<Icon>` auf `unraid/dmoffice.png`.
- Das Image wird von `.github/workflows/docker.yml` gebaut.

## 1. Image öffentlich prüfen

1. GitHub → Profil → *Packages* → `dmoffice` → *Package settings* → *Change visibility* → **Public**.
2. Ohne Anmeldung testen (z. B. auf dem Unraid-Server):

   ```bash
   docker pull ghcr.io/taloan/dmoffice:latest
   ```

3. Die Vorlage einmal selbst installieren: `unraid/dmoffice.xml` nach
   `/boot/config/plugins/dockerMan/templates-user/my-dmoffice.xml` kopieren, *Docker* → *Add Container* →
   *Template* `dmOffice`, starten, Web-Oberfläche öffnen, Verwaltungskonto anlegen.

## 2. Support-Thread im Unraid-Forum

Optional: Laut <https://ca.unraid.net/submit/help> ist ein Forum-Thread keine Pflicht. `<Support>` zeigt bis dahin
auf die GitHub-Issues.

Neue Themen in **Application Support → Docker Containers**
(<https://forums.unraid.net/forum/47-docker-containers/>) dürfen nur „Community Developers“ anlegen. Fehlt dort der
Knopf *Start new topic*, gilt laut angehefteter Regel
(<https://forums.unraid.net/topic/40696-why-cant-i-post-new-topics-in-here/>):

1. Auf <https://forums.unraid.net> anmelden.
2. Den Moderatoren (Squid, notfalls SpencerJ) eine Privatnachricht schicken: Link zum Repo
   `https://github.com/Taloan/dmOffice` und Bitte um Freischaltung als Community Developer bzw. um den Thread.
   Alternativ den Thread in einem offenen Bereich anlegen und um Verschiebung nach *Docker Containers* bitten.
3. Nach der Freischaltung in **Docker Containers** einen neuen Beitrag erstellen, Titel z. B. **„[Support] dmOffice – self-hosted Outlook-like mail, calendar, contacts with
   CalDAV/CardDAV/WebDAV“**. Auf Englisch schreiben, das ist das Publikum von CA.
4. Inhalt (Vorschlag):
   - Kurzbeschreibung (wie `<Overview>` im Template), ein bis zwei Bildschirmfotos.
   - Links: Projekt <https://github.com/Taloan/dmOffice>, Image `ghcr.io/taloan/dmoffice`, Template
     <https://raw.githubusercontent.com/Taloan/dmOffice/master/unraid/dmoffice.xml>.
   - Einrichtung: `DMOFFICE_MASTER_KEY` setzen und sichern, erster Aufruf legt das Verwaltungskonto an,
     HTTPS über Reverse-Proxy für Handys/DAV.
   - Hinweis, dass Fehler gern auch als GitHub-Issue gemeldet werden können.
5. Die Adresse des Threads (`https://forums.unraid.net/topic/<nummer>-…/`) notieren und eintragen:
   - `unraid/dmoffice.xml`: `<Support>` auf den Thread setzen (bisher GitHub-Issues).
   - `ca_profile.xml`: `<Forum>https://forums.unraid.net/topic/…</Forum>` ergänzen.
   - Committen und nach GitHub pushen.

## 3. Bei CA einreichen

1. <https://ca.unraid.net/submit> öffnen (Anmeldung mit GitHub) → **Submit new** (<https://ca.unraid.net/submit/new>).
2. Repository-URL angeben: `https://github.com/Taloan/dmOffice`.
3. **Validate** ausführen, gemeldete Fehler in den XML-Dateien beheben, pushen, erneut prüfen.
4. **Scan** ausführen: Es muss genau eine App `dmOffice` gefunden werden.
5. Einreichen. Docker-Apps werden automatisch oder von den Moderatoren geprüft; die Aufnahme kann einige Tage dauern.
6. Danach in Unraid unter *Apps* nach `dmOffice` suchen.

## Pflichten danach

- Fragen im Support-Thread beantworten.
- Das Image mit neuen Unraid-Versionen lauffähig halten.
- Bei Ende der Pflege die CA-Moderation informieren (oder `<Deprecated>true</Deprecated>` ins Template).

## Neue Version veröffentlichen

```bash
git tag v1.0.0
```

```bash
git push github v1.0.0
```

Der Workflow baut dann `:1.0.0`, `:1.0` und `:latest`. Jeder Push auf `master` aktualisiert `:latest`.

Änderungen nur unter `apps/` oder `docs/` bauen kein neues Image.

## Android-App veröffentlichen

`.github/workflows/android.yml` baut bei einem Tag `android-v…` die APK, signiert sie und hängt sie an einen
GitHub-Release. Einmalig einen Signaturschlüssel anlegen und gut aufheben (ohne ihn gibt es keine Updates mehr):

```bash
keytool -genkeypair -v -keystore dmoffice.jks -alias dmoffice -keyalg RSA -keysize 4096 -validity 10000
```

Im Repo unter *Settings → Secrets and variables → Actions* anlegen: `ANDROID_KEYSTORE_BASE64` (Inhalt von
`base64 -w0 dmoffice.jks`), `ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS` (`dmoffice`), `ANDROID_KEY_PASSWORD`.
Die Datei `dmoffice.jks` nie ins Repo legen.

```bash
git tag android-v0.1.0
```

```bash
git push github android-v0.1.0
```
