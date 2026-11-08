# Externe Ordner
Deutsch · [English](externe-ordner.md)

Externe Ordner zeigen Dateien, die **außerhalb** der eigenen Ablage von dmOffice liegen: einen Ordner auf dem Server
(eingebundene Freigabe oder USB-Platte) oder einen eigenen WebDAV-Speicher eines Benutzers (Nextcloud, Storage Box, NAS).
dmOffice liest und schreibt dort direkt; nichts wird in die eigene Ablage kopiert, nichts verschlüsselt.

## Ordner auf dem Server (Administrator)

1. **Ordner im Container einbinden** unter `/external/<Name>`, z. B. die Unraid-Freigabe `/mnt/user/music` → `/external/music`.
   Nur dieser Schritt braucht einen Neustart des Containers.
2. **Verwaltung → Server-Mounts → Lokale Server-Mounts → Weiteren Ordner hinzufügen:**
   - **Name** – so sehen ihn die Benutzer.
   - **Pfad im Container** – der volle Pfad; das Ordner-Symbol listet die im Container eingebundenen Ordner.
   - **Nur lesen** – Hochladen, Umbenennen und Löschen werden abgelehnt.
   - **Sichtbar für:** – alle Benutzer oder ausgewählte.
   - **In dmOffice erscheinen:** – alles (auch später hinzukommende Ordner) oder nur ausgewählte Ordner der obersten Ebene.
3. **Speichern.** dmOffice prüft den Pfad: Er muss existieren und vollständig sein. Das Datenverzeichnis, alles
   darin oder darüber, die oberste Ebene einer Platte und der Ordner des Storage Extenders lassen sich nicht freigeben.

### Suche in Server-Mounts

Nach dem Speichern legt **Indexierung konfigurieren** je Mount fest, was die Suche findet:

- **Nichts, nur bereitstellen** – die Dateien sind sichtbar und lassen sich öffnen.
- **Dateien und Ordner** (Vorgabe bei neuen Mounts) – Name, Ordner, Dateityp, Größe und Datum.
- **Auch Inhalte** – zusätzlich der Text in **Textdateien** (TXT, CSV, Markdown, Log, JSON, XML, YAML, HTML, EML, VCF, ICS), **Dokumenten** (PDF, Word, Excel, PowerPoint) und **Bildern und Scans** (Texterkennung, dauert deutlich länger). Dazu lassen sich Ordner der obersten Ebene und eine Größengrenze wählen.

Auf dem Stand bleibt die Suche so:

- **Über dmOffice geänderte Dateien** findet sie sofort, und ein geöffneter Ordner wird gleich aktualisiert.
- **Außerhalb von dmOffice geänderte Dateien** findet sie nach der nächsten Prüfung: täglich um 03:00 (Vorgabe), alle 6 Stunden, stündlich, wöchentlich (sonntags um 03:00) oder nur von Hand. dmOffice vergleicht dabei Änderungsdatum und Größe. Neustarts unterbrechen eine Prüfung nur, sie läuft danach weiter. Das Auswerten läuft auch während einer Prüfung.
- **Die Trefferliste nennt den Stand** je Mount: wann zuletzt geprüft wurde und wie viele Dateien noch nicht ausgewertet sind. So bleibt „Nichts gefunden“ eine verlässliche Aussage.

Technik: ein Index für alle Benutzer (`data/extindex.db`, verschlüsselt). Ein Mount, den mehrere sehen, wird nur einmal gelesen; Treffer bekommt nur, wer ihn sehen darf. Verknüpfungen (Symlinks) werden nicht verfolgt; Papierkörbe und NAS-Vorschauordner (`#recycle`, `@eaDir`, `.Trash`, …) übersprungen. Der Index gehört nicht zum Core-Backup, er lässt sich neu aufbauen. Fortschritt zeigt die Aktivität unter **Suche in Server-Mounts**.

## Eigene WebDAV-Ordner (Benutzer)

Der Administrator schaltet sie unter **Verwaltung → Für Benutzer freigeben → Benutzer-Mounts** frei:

- **Benutzer dürfen eigene WebDAV-Ordner einbinden (Nextcloud, Storage Box, NAS …)** – wirkt sofort.
- **… auch mit Adressen im Heimnetz** – vorgegeben aus. Solange es aus ist, werden Adressen im Heimnetz und der Server
  selbst abgelehnt, geprüft bei jeder Verbindung. Nur einschalten, wenn allen Benutzern zu trauen ist.

Benutzer binden dann unter **Dateien → Externe Ordner → WebDAV-Ordner einbinden** einen Ordner ein: Name, Adresse (Nextcloud:
`https://cloud.example.org/remote.php/dav/files/<name>/`), Benutzername und Passwort – am besten ein eigenes App-Passwort
nur für dmOffice. Die Zugangsdaten liegen in der verschlüsselten Datenbank des Benutzers. Per Rechtsklick auf einen
WebDAV-Ordner wählt man, welche Ordner in dmOffice erscheinen, oder entfernt ihn; die Dateien auf dem anderen Server bleiben.

## Benutzung

- **Dateien → Externe Ordner**: blättern, herunterladen, hochladen, Ordner anlegen, umbenennen, löschen; Ansichten wie in
  der Dateiablage (Details/Kacheln mit Vorschaubildern, Sortierung).
- **Galerie-Ordner** können externe Ordner als Quelle nutzen. Bei Serverordnern werden Bildangaben und Vorschauen einmal für
  alle Benutzer zwischengespeichert (verschlüsselt), bei eigenen WebDAV-Ordnern je Benutzer.
- **Einstellungen → Dateien → Externe Ordner** zeigt den freien Platz je Ordner (Platte des Serverordners, WebDAV-Kontingent,
  sofern der Server es meldet).

## Grenzen

- **Kein Papierkorb, keine früheren Versionen, keine Freigabelinks.** Löschen ist endgültig.
- **Nicht gesichert** – weder vom Storage Extender noch vom Core-Backup. Das Core-Backup hält nur fest, welche Ordner
  freigegeben waren. Externe Ordner selbst sichern.
- **Zählen nicht** zum Kontingent des Benutzers.
- **Nur im Web.** Nicht über WebDAV-/Nextcloud-Clients und nicht in der Android-App.
- **Hochladen** geht in einer einzigen Anfrage, höchstens 512 MB je Anfrage (kein Hochladen in Stücken wie in der eigenen Ablage).

## Siehe auch

- [Speicher und Sicherung](speicher-und-sicherung.de.md) – Storage Extender und Core-Backup
