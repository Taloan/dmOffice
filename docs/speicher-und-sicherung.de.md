# Speicher und Sicherung
Deutsch · [English](speicher-und-sicherung.md)

dmOffice 2.0 legt eine zweite Kopie der Daten in zwei Teilen ab:

| Was | Wohin | Wie oft |
|---|---|---|
| Dateiinhalte | **Storage Extender** | laufend |
| Datenbanken (Mails, Kalender, Kontakte, Aufgaben, Notizen, Chat, Namen und Ordner der Dateien, Benutzer, Einstellungen) | **Core-Backup** | z. B. täglich |
| Externe Ordner | – | nicht gesichert, selbst sichern ([Externe Ordner](externe-ordner.de.md)) |
| Hauptschlüssel | – | nirgends gesichert, selbst aufbewahren |

**Der Storage Extender allein ist keine Sicherung.** Ohne Datenbanken und Hauptschlüssel lässt sich nichts zurückholen.
Zusammen mit dem Core-Backup ergibt er eine vollständige Sicherung außerhalb des Servers.

Beides wird unter **Verwaltung → Server** eingerichtet (Storage Extender und Backup-Strategie). Ein Neustart ist nicht nötig.

## Voraussetzung: Hauptschlüssel

**`DMOFFICE_MASTER_KEY`** in der Container-Vorlage setzen (maskierte Variable) und getrennt aufbewahren, z. B. im
Passwortmanager. Ohne ihn liegt der Hauptschlüssel als Datei im Datenverzeichnis, und das Core-Backup lässt sich nicht
einschalten; die Verwaltung zeigt dann eine Warnleiste. Wer den Schlüssel und Zugang zum Sicherungsziel hat, kann alles
lesen; ohne den Schlüssel öffnet niemand die Sicherung – auch dmOffice nicht.

## Storage Extender

### 1. Wo ist der zweite Ort?

**Art:**

- **Ordner auf dem Server** – z. B. eine USB-Platte, im Container unter `/external/<Name>` eingebunden. Der Ordner darf
  nicht im Datenverzeichnis liegen und sich nicht mit einem freigegebenen externen Ordner überschneiden. Ein Ordner auf
  derselben Platte wie dmOffice hilft nicht, wenn diese Platte ausfällt.
- **WebDAV** – z. B. Nextcloud oder Storage Box: Adresse, Benutzername, Passwort (am besten ein eigenes App-Passwort). Ein
  fehlender Zielordner wird angelegt.
- **S3** – z. B. Garage, MinIO oder Hetzner Object Storage: Adresse, Region, Bucket, Ordner im Bucket, Zugangsschlüssel,
  geheimer Schlüssel. Ein gespeicherter geheimer Schlüssel wird nie wieder angezeigt; Feld leer lassen, um ihn zu behalten.

**Speichern und testen** schreibt, liest und löscht eine Probe.

### 2. Lokalen Speicherpool einstellen

Der lokale Speicherpool ist der Teil des lokalen Speichervolumens, der zur Verfügung steht. Im Storage Extender liegen alle Dateien, im lokalen Speicherpool die zuletzt verwendeten. Passen alle Dateien in die Grenzen, liegen alle
auch auf dem Server, und fehlende holt dmOffice von selbst zurück. Sonst füllt dmOffice den freien Speicherplatz automatisch mit den neuesten Dateien auf, und eine Datei, die nur im Storage Extender liegt, wird beim Öffnen geholt;
große Dateien werden durchgereicht statt abgelegt. Vorschaubilder, Mails, Kalender, Kontakte, Notizen und die Datenbanken bleiben
immer lokal.

- **Auf dem lokalen Speichervolumen immer frei lassen** (Vorgabe 50 GB, höchstens ein Fünftel des Volumes): gilt für das ganze Volume des
  Datenverzeichnisses, also auch für andere Daten dort. Wird die Grenze erreicht, bereinigt dmOffice den lokalen Speicherpool und reicht geöffnete Dateien nur noch durch. Lässt es sich
  nicht weiter bereinigen, nimmt dmOffice keine neuen Dateien an.
- **Für den lokalen Speicherpool höchstens nutzen**: optionale Obergrenze. Sie zählt nur Dateien; Vorschaubilder bleiben immer lokal und belegen zusätzlich Platz auf dem Volume.

Die Grafik zeigt beim Verschieben der Regler, was geschieht: Rot sind Dateien, die bereinigt werden, gelb Dateien, die noch übertragen werden. Gespeichert wird erst mit **Übernehmen**. Den belegten und freien Platz im Storage Extender zeigt die Grafik nur bei einem Ordner als Ziel, S3 und WebDAV melden ihn nicht verlässlich.

Das Backup des Storage Extenders liegt in der Verantwortung des Administrators. Für ältere Stände der Dateien gibt es das Files-Backup.

### 3. Einschalten

**Storage Extender einschalten.** Der erste Abgleich startet 30 Sekunden nach dem Serverstart, danach spätestens alle
10 Minuten und sofort nach einer Änderung. Der Zustand zeigt „Zuletzt abgeglichen“ und wie viele Dateien im externen
Speicher liegen.

### Verhalten

- **Was übertragen wird:** die Dateiinhalte, bereits mit einem Schlüssel je Datei verschlüsselt, unter Zufallsnamen –
  auch frühere Versionen, Papierkorb und Vorschaubilder. Der Anbieter sieht weder Inhalte noch Dateinamen, nur Anzahl,
  Größe und Zeitpunkte der Objekte. Namen, Ordner und Schlüssel stehen in den Datenbanken (Core-Backup).
- **Löschen:** Was in dmOffice endgültig verschwindet, bleibt im Storage Extender noch 7 Tage; mit eingeschaltetem
  Core-Backup mindestens (Anzahl Stände + 1) × Abstand. Scheitert das
  Core-Backup oder ist es überfällig, löscht dmOffice am Ziel nichts.
- **Ziel nicht erreichbar:** Dateien auf dem Server lassen sich weiter öffnen, der Abgleich holt später auf und zeigt einen Fehler. Dateien, die
  nur im Storage Extender liegen, lassen sich erst wieder öffnen, wenn er erreichbar ist.
- **Sperren:** Ausschalten, Art ändern oder Zugangsdaten löschen geht nicht, solange Dateien nur im Storage Extender
  liegen. Passen sie in die Grenzen des Caches, holt dmOffice sie von selbst zurück. Eine neue
  Adresse wird nur angenommen, wenn dort alles zu finden ist.
- **Ein Ordner je Server:** Speichert ein anderer dmOffice-Server an denselben Ort, löscht dmOffice dort nichts.

## Files-Backup

Legt eine eigene Kopie der Dateien an und läuft vor jedem Core-Backup. Es überträgt nur Fehlendes und setzt das Core-Backup
voraus. Jeder Core-Stand, der jünger ist als die Haltedauer, lässt sich vollständig wiederherstellen, auch wenn er weggesichert
wurde.

- **Ziel:** **Ziel aus Storage Extender übernehmen** (dort muss es eingeschaltet sein) oder **Anderes Ziel**. Mit dem Ziel
  aus dem Storage Extender liegt die Kopie dort im eigenen Ordner `files-backup` neben `backups`. Die Dateien belegen dann
  zweimal Speicherplatz. Geht dieser Speicher verloren, betrifft das beide. Dagegen hilft ein anderes Ziel.
- **Haltedauer:** mindestens (Anzahl Stände + 1) × Abstand, oder unbegrenzt.
- **Löschen:** Ein Inhalt, den keine Datenbank mehr kennt, wird nach der Haltedauer aus der Kopie gelöscht. Das ist unabhängig
  von der Frist im Storage Extender.
- **Gelöschte Benutzer:** Ihre Dateien verschwinden sofort vom Server und direkt danach aus dem Storage Extender und der Kopie,
  auch wenn ein Ziel erst später erreichbar ist. Ältere Core-Backups stellen die Dateien dieses Kontos dann nicht mehr her.
  Core-Backups bleiben als Ganzes bestehen, bis neuere sie ersetzen, und enthalten die Daten bis dahin verschlüsselt. Ein Vermerk
  am Ziel sorgt dafür, dass ein eingespieltes älteres Core-Backup das Konto wieder entfernt. Ein woandershin weggesichertes
  Core-Backup, das ohne dieses Ziel eingespielt wird, kennt den Vermerk nicht.

Wiederherstellen und einzelne Dateien zurückholen: [Wiederherstellen](wiederherstellen.de.md).

## Core-Backup

**Verwaltung → Server → Backup-Strategie** enthält die Übersicht, das Core-Backup und das Files-Backup. Das Core-Backup
sichert alles außer den Dateiinhalten, jeder Stand ist vollständig.

1. **Ziel** – **Ziel aus Storage Extender übernehmen** oder **Anderes Ziel** (Ordner, WebDAV oder S3 wie oben,
   **Speichern und testen**). Mit eigenem Ziel läuft das Core-Backup auch ohne Storage Extender; es enthält dann nur die
   Namen der Dateien, nicht die Dateien.
2. **Core-Backup einschalten** – nur mit `DMOFFICE_MASTER_KEY` und eingerichtetem Ziel.
3. **Wie oft:** stündlich, alle 3, 6 oder 12 Stunden, täglich, wöchentlich, monatlich, mit Uhrzeit. **Stände aufbewahren:**
   die Anzahl, die dmOffice am Ziel behält. **Jetzt exportieren** startet sofort einen Lauf.
4. **Wiederherstellungsschlüssel erstellen** – enthält Ort und Zugangsdaten des Sicherungsziels, mit dem Hauptschlüssel
   verschlüsselt. Als Datei oder im Passwortmanager neben `DMOFFICE_MASTER_KEY` ablegen. Ändern sich die Zugangsdaten
   später, fordert die Verwaltung zum Neuerstellen auf.

### Inhalt

- **Enthalten:** alle Benutzerdatenbanken, Chat, Benutzerliste, Server-Einstellungen, Schlüsselring; als eine Datei
  gepackt und mit einem aus dem Hauptschlüssel abgeleiteten Schlüssel noch einmal verschlüsselt.
- **Nicht enthalten:** Dateiinhalte (im Storage Extender), der Hauptschlüssel, die Container-Vorlage (Umgebungsvariablen,
  eingebundene Ordner), Dateien in externen Ordnern (nur, welche Ordner freigegeben waren), der Suchindex der Serverordner.
- Vor jedem Lauf wartet dmOffice (höchstens 30 Minuten) auf einen Abgleich des Storage Extenders, damit die Sicherung nur
  Inhalte kennt, die dort schon liegen.
- Ein Stand ist eine Momentaufnahme: Änderungen an Mails, Kalendern usw. danach fehlen. Dateien, die nach dem letzten Stand
  hochgeladen wurden, kommen ohne Namen in den Ordner „Nach Wiederherstellung gefunden“.

### Wiederherstellen

Auf einem neuen, leeren dmOffice mit **demselben** `DMOFFICE_MASTER_KEY` und denselben eingebundenen Ordnern:

1. Auf der Seite für den ersten Start: **Stattdessen aus dem Core-Backup wiederherstellen …**
2. **Einrichtungscode** (aus dem Server-Protokoll), **Wiederherstellungsschlüssel (optional)** → **Einlesen**. Ohne
   Wiederherstellungsschlüssel Ort und Zugangsdaten von Hand eintragen (**Ohne Wiederherstellungsschlüssel: Zugangsdaten eingeben …**).
3. **Core-Backups suchen**, Stand wählen, **Diesen Stand wiederherstellen**.
4. Der Server startet neu und holt danach die Dateiinhalte aus dem Storage Extender. Der Container braucht dafür eine
   Neustart-Richtlinie.

## Datenexport

Zusätzlich legt jede Person auf Wunsch ihre Daten **entschlüsselt und in offenen Formaten** an ein eigenes Ziel auf einem Server, etwa per
WebDAV nach pCloud (`https://ewebdav.pcloud.com/…` für Konten in der EU, sonst `https://webdav.pcloud.com/…`). Im Notfall
lassen sich die Daten dort ohne dmOffice öffnen. Zurückspielen lässt sich ein Datenexport nicht. Ein Herunterladen in den Browser gibt es nicht; wer alles auf dem eigenen
Rechner haben will, synchronisiert das Ziel dorthin.

- **Einrichten:** **Einstellungen → Konto → Datenexport** – Ziel (WebDAV-Adresse mit Benutzername und Passwort oder
  ein eigener externer Ordner, in den geschrieben werden darf, mit Unterordner, oder OneDrive mit Anmeldung bei Microsoft
  und einem Ordner, Vorgabe „Datensicherung“), **Verbindung testen**, Bereiche und Ordner,
  Uhrzeit. Einschalten erst nach der Bestätigung, dass die Daten im Ziel lesbar liegen.
- **Inhalt:** Dateien bytegenau als Originale in ihrer Ordnerstruktur, Kalender und Aufgaben als `.ics`, Kontakte als
  `.vcf`, Notizen als `.md`, DMS als Liste (`.csv`, `.json`), Alben als `.json`. Jeder
  Bereich und einzelne Ordner lassen sich abwählen. Nicht enthalten: E-Mails (liegen beim Mailanbieter), externe Ordner,
  Chat. Ein Tresor im Cryptomator-Format in der Dateiablage kommt mit den Dateien verschlüsselt ins Ziel.
- **Ablauf:** täglich zur gewählten Uhrzeit und mit **Jetzt exportieren**; übertragen wird nur Geändertes. Bei mehreren Personen
  läuft der Datenexport nacheinander. Zustand, letzter Lauf und letzter Fehler stehen in den Einstellungen; schlägt der
  Datenexport mehrmals nacheinander fehl, gibt Pukki Bescheid.
- **Nur schreiben:** dmOffice liest aus dem Ziel nichts zurück.
- **Ordnernamen:** in der Oberflächensprache beim ersten Lauf (deutsch: `Dateien` …, englisch: `Files` …), danach fest.
- **`.trash`:** Gelöschtes – und auf Wunsch ältere Versionen geänderter Dateien – kommt in den versteckten Ordner `.trash`
  im Ziel, mit gleicher Ordnerstruktur, und wird nach der Frist (7, 30, 90 Tage oder nie) entfernt.
- Im Ziel erklärt eine `LIESMICH.txt` Aufbau und Programme.
- **Verwaltung → Für Benutzer freigeben → Benutzer-Datenexport:** freischalten oder sperren und den Stand je Person sehen – ohne Ziel und
  Zugangsdaten.
## Siehe auch

- [Externe Ordner](externe-ordner.de.md)
- [Aktualisieren](updating.de.md)
