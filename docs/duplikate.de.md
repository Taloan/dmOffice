# Prüfsummen und „Duplikate finden“ (Konzept)

Deutsch · [English](duplikate.md)

Stand: Die Prüfsummen gibt es. „Duplikate finden“ ist noch **nicht gebaut**; hier steht, wie es aufsetzt.

## Was es schon gibt

- Jede Datei hat in ihrer Zeile die **SHA-256 des Klartexts** (Spalte `sha256`, 64 Zeichen hex, klein). Dasselbe gilt
  für frühere Versionen (`file_versions`) und den Papierkorb (`file_trash_items`). Schema-Version 18 (`Core/UserDb.cs`).
- Gefüllt wird sie beim Schreiben, ohne zweites Lesen: Der `BlobWriter` rechnet beim Verschlüsseln mit, `FileStore.Commit`
  trägt sie ein. Das gilt für Web-Upload, WebDAV-PUT, Speichern im Editor, Office (WOPI/OnlyOffice), den notizi-Abgleich
  und Uploads über einen Freigabelink. Kopieren, Verschieben, Papierkorb, Wiederherstellen und Versionen nehmen sie mit.
- Was vor Version 18 schon da war, rechnet `FileHashJob` im Hintergrund nach (`Dav/FileHashes.cs`): kurz nach dem Start,
  dann stündlich. Man kann ihn unterbrechen, er macht danach weiter. Er erscheint unter Aktivität als „Prüfsummen der
  Dateien berechnen“. Inhalte, die noch in der Datenbank liegen, bekommen ihre Prüfsumme beim Auslagern (`MigrateBlobs`).
  Fehlt ein Blob oder passt seine Länge nicht, bleibt die Spalte leer: lieber unbekannt als falsch.
- Index `files_sha256 ON files(sha256) WHERE sha256 IS NOT NULL`.
- `POST /api/files/hashes` mit `{ items: [{ sha256, size }] }` (höchstens 5000) antwortet mit
  `{ found: [{ sha256, size, path }], pending }`. Gezählt werden nur sichtbare Dateien, also nicht Papierkorb, Versionen
  oder der notizi-Ordner. `pending` ist die Zahl der Dateien ohne Prüfsumme, deren Inhalt dort also noch fehlen kann.
  Die Android-App nutzt das für „schon gesichert“ und für die Gegenprobe vor „Speicherplatz freigeben“.
- Im Detailbereich der Weboberfläche steht die Prüfsumme klein darunter. Ein Klick kopiert sie.

## Duplikate finden: so ginge es

**Abfrage.** Der Index trägt sie, ohne einen Inhalt zu lesen:

```sql
SELECT sha256, size, COUNT(*) AS n, SUM(size) - size AS spare
FROM files
WHERE is_dir = 0 AND sha256 IS NOT NULL AND size > 0
GROUP BY sha256, size
HAVING COUNT(*) > 1
ORDER BY spare DESC;
```

Danach folgen je Gruppe die Pfade (`SELECT path, modified FROM files WHERE sha256=$h AND size=$s`). Der notizi-Ordner
wird herausgefiltert (`FileStore.InRoot`), wie in `FileHashes.Lookup`.

**Endpunkt.** `GET /api/files/duplicates?under=<ordner>` liefert Gruppen mit `sha256`, `size`, `count`, `spare`
(was frei würde, wenn eine Kopie bleibt) und `paths`, seitenweise. Die Gruppierung kostet bei vielen Dateien etwas.
Die Antwort könnte deshalb 15 s zwischengespeichert werden, wie die Zähler in `ActivityApi.Cached`.

**Oberfläche.** Ein Eintrag unter Dateien → „Duplikate“, mit Gruppen, je Gruppe den Pfaden samt Vorschau, Ordner und
Datum und dem Vorschlag, welche Kopie bleibt (die älteste oder die mit dem kürzesten Pfad). „Die anderen löschen“
schiebt sie in den Papierkorb, also wie jedes Löschen und rückgängig zu machen. Den Speicher gibt erst das Leeren des
Papierkorbs frei.

**Wichtig.**
- Der Speicher wird **nicht automatisch dedupliziert**. Kopien innerhalb des Servers teilen sich den Blob schon jetzt
  (`MoveOrCopy`). Zwei unabhängige Uploads desselben Fotos sind zwei Blobs mit verschiedenen Schlüsseln, und das soll
  so bleiben: Jeder Blob hat seinen eigenen Schlüssel, und gemeinsame Blobs würden verraten, dass zwei Inhalte gleich sind.
- Dateien ohne Prüfsumme (`pending`) erscheinen erst, wenn der Hintergrundjob fertig ist. Die Oberfläche sollte das sagen.
- Leere Dateien (`size = 0`) haben alle dieselbe Prüfsumme und gehören nicht in die Liste.
