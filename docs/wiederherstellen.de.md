# Wiederherstellen

[English](wiederherstellen.md)

dmOffice 2.0 kennt zwei Wege: den ganzen Server auf ein leeres dmOffice zurückholen oder einzelne Dateien mit dem Werkzeug `restore-tool` herausholen. Der laufende Server wird in beiden Fällen nicht überschrieben.

Eingerichtet werden die Backups unter **Verwaltung → Server → Backup-Strategie**.

## Server wiederherstellen

Bereithalten:

- `DMOFFICE_MASTER_KEY` des alten Servers (zwingend),
- den Wiederherstellungsschlüssel oder die Zugangsdaten zum Ziel des Core-Backups.

Ablauf:

1. dmOffice mit leerem Datenordner starten und in der Container-Vorlage denselben `DMOFFICE_MASTER_KEY` eintragen.
2. Auf der Anmeldeseite **Aus dem Core-Backup wiederherstellen** wählen.
3. Wiederherstellungsschlüssel einlesen oder das Ziel von Hand eintragen, **Core-Backups suchen**, Stand wählen.
4. dmOffice nennt vorab, wie viele Dateien des Stands am Ziel fehlen. Danach startet der Server neu.
5. Die Dateien holt dmOffice selbst, vom eingerichteten Speicher und aus dem Files-Backup.

Dateien, die nach dem gewählten Stand an den eingerichteten Speicher kamen, legt dmOffice ohne Namen im Ordner „Nach Wiederherstellung gefunden“ ab.

Jeder Core-Stand, der jünger ist als die Haltedauer des Files-Backups, lässt sich vollständig wiederherstellen.

### Weggesicherter Stand

Ein Stand, der nicht mehr am Ziel liegt, wird über ein Ordner-Ziel eingelesen:

1. Datei `<Zeit>_<Kennung>.dmb` unverändert in `backups/` eines Ordners legen, den der Container sieht, etwa `/external/restore/backups/`.
2. Bei der Wiederherstellung **Ordner auf dem Server** wählen und `/external/restore` eintragen. Wurde das Core-Backup mit einem Präfix gesichert, gehört der Stand unter `<Präfix>/backups/`.

Die Zugangsdaten zu den Dateien stehen in den Einstellungen des Stands.

## Einzelne Dateien zurückholen

`restore-tool` liest einen Core-Stand und das Files-Backup, ohne einen Server zu starten. Es laufen keine Hintergrunddienste, nur das Ziel wird kontaktiert, und dort wird nichts verändert. Das Werkzeug arbeitet ab Ständen der Version 2.0. Der Tresor ist nicht enthalten, er hat einen eigenen Schlüssel.

Das Werkzeug liegt im Image von dmOffice. Der Hauptschlüssel kommt aus der Umgebung, damit er nicht in der Befehlszeile steht:

```bash
export DMOFFICE_MASTER_KEY='…'
docker run --rm -e DMOFFICE_MASTER_KEY -v /mnt/backup:/backup dmoffice:v2 restore-tool users --stand /backup/20261104-030000_abc.dmb
```

| Befehl | Tut |
|---|---|
| `users --stand <datei>` | nennt die Benutzer im Stand |
| `ls --stand <datei> --user <login> [--path <ordner>]` | zeigt Ordner und Dateien |
| `ls … --trash` | zeigt den Papierkorb |
| `ls … --versions <datei>` | zeigt frühere Versionen mit ihrer Nummer |
| `find --stand <datei> --user <login> <text>` | sucht nach Namen, auch im Papierkorb |
| `get --stand <datei> --user <login> --out <ordner> <pfad>` | kopiert Datei oder Ordner entschlüsselt in den Zielordner |
| `get … --trash <pfad>` | holt den jüngsten Eintrag dieses Pfads aus dem Papierkorb |
| `get … --version <nr> <pfad>` | holt eine frühere Version |

Ohne weitere Angabe sucht das Werkzeug nach den Einstellungen des Stands: zuerst im Files-Backup, dann im eingerichteten Speicher. Liegen die Dateien in einem Ordner, wählt `--files <ordner>` diesen, `--prefix <präfix>` den Ordner darin. Eine weggesicherte Kopie des Ordners `files-backup` wählst du mit `--files <ordner> --prefix ""`.

Vorhandene Dateien im Zielordner werden nicht überschrieben. Fehlt ein Inhalt am Ziel, nennt das Werkzeug die Datei und endet mit dem Rückgabewert 3.

### Beispiele

```bash
docker run --rm -e DMOFFICE_MASTER_KEY -v /mnt/backup:/backup dmoffice:v2 restore-tool find --stand /backup/20261104-030000_abc.dmb --user anna vertrag
```

```bash
docker run --rm -e DMOFFICE_MASTER_KEY -v /mnt/backup:/backup -v /mnt/out:/out dmoffice:v2 restore-tool get --stand /backup/20261104-030000_abc.dmb --user anna --out /out --trash Vertraege/mietvertrag.pdf
```

Die Datei gehört danach dem Benutzer, unter dem das Werkzeug im Container läuft (`DMOFFICE_UID`, `DMOFFICE_GID`). Der Ordner muss für ihn beschreibbar sein.
