# Wiederherstellen

[English](restore.md)

dmOffice kennt zwei Wege: den ganzen Server auf ein leeres dmOffice zurückholen oder einzelne Dateien mit dem Befehl `restore` herausholen. Der laufende Server wird in beiden Fällen nicht überschrieben.

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

## Beschädigte Datenbank

Jede Person hat eine eigene Datenbank, dazu kommen Chat, Alben und Push. Die nächtliche Prüfung (**Verwaltung → System → Datenbanken**) testet jede einzeln. Kaputte Indizes baut dmOffice selbst neu auf. Bei einem anderen Befund stehen neben der betroffenen Datenbank zwei Knöpfe:

- **Reparieren versuchen:** dmOffice übernimmt alles Lesbare in eine neue Datenbank und prüft sie. Oft fehlen danach nur einzelne Einträge.
- **Aus Sicherung ersetzen:** nur diese Datenbank aus einem gewählten Core-Stand. Änderungen darin seit dem Stand gehen verloren. Bei einer Person bleiben Dateien, die danach hochgeladen wurden, erhalten: Sie liegen ohne Namen im Ordner „Nach Wiederherstellung gefunden“.

Beide Wege betreffen nur die beschädigte Datenbank, alle anderen bleiben unverändert. Die bisherige Datei bleibt als Kopie neben der neuen liegen (`<name>.beschaedigt-<Zeit>`).

## Einzelne Dateien zurückholen

Mit `restore` holst du Dateien, Ordner, gelöschte Dateien und frühere Versionen aus dem Core-Backup, ohne etwas am laufenden Server zu ändern. Pfade schreibst du wie bei rclone: `<benutzer>:/<pfad>`.

**Der Server läuft:** im Container des Servers aufrufen. Ziel, Zugangsdaten und Hauptschlüssel kennt er schon, genommen wird der neueste Stand.

```bash
docker exec dmoffice restore ls anna:/Verträge
docker exec dmoffice restore copy anna:/Verträge/mietvertrag.pdf /external/restore
```

**Der Server ist weg:** mit dem Wiederherstellungsschlüssel (**Verwaltung → Server → Backup-Strategie**) und dem Hauptschlüssel. Das Werkzeug sucht die Stände selbst am Ziel.

```bash
export DMOFFICE_MASTER_KEY='…'
docker run --rm -e DMOFFICE_MASTER_KEY -v /mnt/restore:/restore dmoffice:v2 restore-tool ls --key /restore/schluessel.txt anna:/Verträge
```

| Befehl | Tut |
|---|---|
| `stands` | nennt die Stände am Ziel |
| `users` | nennt die Benutzer im Stand |
| `ls anna:/<ordner>` | zeigt Ordner und Dateien |
| `ls --trash anna:` | zeigt den Papierkorb |
| `ls --versions anna:/<datei>` | zeigt frühere Versionen mit ihrer Nummer |
| `find anna: <text>` | sucht nach Namen, auch im Papierkorb |
| `copy anna:/<pfad> <zielordner>` | kopiert Datei oder Ordner entschlüsselt in den Zielordner |
| `copy --trash anna:/<pfad> <zielordner>` | holt den jüngsten Eintrag dieses Pfads aus dem Papierkorb |
| `copy --version <nr> anna:/<datei> <zielordner>` | holt eine frühere Version |

**Welcher Stand:** ohne Angabe der neueste. `--at 2026-11-04` nimmt den letzten Stand bis zu diesem Tag, `--at 2026-11-04T03:00` bis zu dieser Uhrzeit. Eine einzelne Stand-Datei wählst du mit `--stand <datei.dmb>`.

**Woher die Dateien kommen:** wie im Stand eingestellt, zuerst aus dem Files-Backup, dann aus dem Storage Extender. Liegen sie in einem Ordner, wählt `--files <ordner>` diesen, `--prefix <präfix>` den Ordner darin.

Der Zielordner muss im Container eingebunden und für dmOffice beschreibbar sein, etwa `/external/restore`. Vorhandene Dateien werden nicht überschrieben. Fehlt ein Inhalt am Ziel, nennt das Werkzeug die Datei und endet mit dem Rückgabewert 3. Der Tresor ist nicht enthalten, er hat einen eigenen Schlüssel. Das Werkzeug liest Stände ab Version 2.0.

Die bisherige Schreibweise (`--user anna --path …`, `get … --out …`) gilt weiter.
