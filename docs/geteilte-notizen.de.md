# Geteilte Notizordner
Deutsch · [English](geteilte-notizen.md)

Ein Notizordner – z. B. „80 Rezepte“ – lässt sich samt Unterordnern mit anderen Benutzern desselben Servers teilen:
zum **Lesen** oder zum **Bearbeiten**, vom Besitzer vergeben und jederzeit änderbar oder entziehbar. Gleiches Modell wie
bei Kalendern und Aufgabenlisten (siehe [geteilte-kalender.de.md](geteilte-kalender.de.md), dort auch die Antwort auf die
Verschlüsselungsfrage): Der Inhalt **bleibt in der Datenbank des Besitzers**, der Server liest und schreibt dort für den
Empfänger und prüft die Freigabe bei **jeder** Anfrage. Keine Kopien, kein Schlüsselaustausch, Widerruf wirkt sofort.

## Ablauf

- **Freigabe:** `data/shares.json`, dieselbe Datei wie für Kalender. Ein Eintrag mit `Folder` (= `sync_id` des Ordners in der
  Datenbank des Besitzers) statt `Collection` ist eine Notizordner-Freigabe; die Kalender-Funktionen
  (`ShareStore.Of/Set/Forget`, `SharedLists`) überspringen solche Einträge. Zuständig für Notizen: `ShareStore.OfFolder/SetFolder/ForgetFolder`.
- **Bereich einer Freigabe:** der Ordner und alle Unterordner darunter (`NotesDb.FolderTree`), bei jeder Anfrage frisch aus der
  Datenbank des Besitzers berechnet. Neue Unterordner sind also sofort mit geteilt; verschiebt der Besitzer eine Notiz heraus,
  ist sie für den Empfänger weg.
- **Zugriff:** `Notes/SharedNotes.cs`. `NoteScope` sagt, in welcher Datenbank gearbeitet wird und ob geschrieben werden darf.
  Ordner-Ids (GUID) sind eindeutig, daran erkennt der Server, ob ein Ordner eigen oder geteilt ist. Notizen eines
  geteilten Ordners tragen für den Empfänger eine **negative Kennung** `-(Freigabe-Id · 2³² + Id)`; so kollidieren sie nie mit
  eigenen Notizen, und die vorhandenen Endpunkte (`/api/notes/{id}/…`) funktionieren mit beiden unverändert.
  Archivierte oder gelöschte Notizen sieht der Empfänger nicht mehr.

## Rechte

| | Besitzer | Bearbeiten | Lesen | ohne Freigabe |
|---|---|---|---|---|
| Ordner, Unterordner, Notizen, Bilder, Anhänge sehen; Suche | ✔ | ✔ | ✔ | – (404, leere Liste) |
| Notizen anlegen, ändern, Titel/Farbe/„wichtig“, Anhänge, in den Papierkorb | ✔ | ✔ | – (403) | – |
| Notiz innerhalb des geteilten Ordners (und seiner Unterordner) verschieben | ✔ | ✔ | – | – |
| Unterordner anlegen | ✔ | ✔ | – | – |
| Eigene Notiz in den geteilten Ordner verschieben (wird dorthin kopiert, das Original verschwindet bei ihr) | ✔ | ✔ | – (403) | – |
| Aus dem Ordner herausverschieben, archivieren, endgültig löschen, Ordner umbenennen/verschieben/löschen | ✔ | – (403) | – | – |
| Teilen und Rechte ändern | ✔ | – | – | – |
| Nicht mehr anzeigen (aussteigen) | – | ✔ | ✔ | – |

## Abgleich des Besitzers (notizi)

Schreibt ein Empfänger, landet die Änderung in der Datenbank des Besitzers wie eine eigene: `sync_rev` steigt (`SaveNote`,
`SetColor`, …), der Abgleich des **Besitzers** wird angestoßen und schickt sie wie gewohnt in dessen verschlüsselten Abgleichordner
zu notizi und den anderen Geräten. Der Empfänger hat keinen eigenen Abgleich dafür. Bilder, die ein Bearbeiter einfügt
(`POST /api/notes/images?note=<Kennung>`), werden mit dem Schlüssel des **Besitzers** benannt und in dessen Datenbank abgelegt.

## Bilder und Anhänge

`/api/notes/blob/<Name>` (auch `?thumb=`) liefert eigene Blobs wie bisher; sonst nur, wenn der Name in einer aktiven Notiz eines
mit mir geteilten Ordners vorkommt (`NotesDb.BlobUsedIn`: Bilder, Anhänge, Text). Wird die Notiz herausverschoben oder die
Freigabe entzogen, ist der Link sofort tot.

## Eigene Notiz in einen geteilten Ordner

Mit Bearbeitungsrecht lässt sich eine eigene Notiz per „Verschieben nach“ (Menü, Editor, Ziehen) in einen geteilten Ordner legen.
Technisch eine Kopie zwischen zwei Datenbanken (`SharedNotes.MoveIntoShared`): neue `sync_id` beim Besitzer, Bilder und Anhänge
unter dem Schlüssel des Besitzers neu benannt, Alarm und Quellen bleiben zurück; das Original wird bei ihr mit Grabstein gelöscht,
verschwindet also auch aus ihrem notizi. Danach gehört die Notiz dem Besitzer des Ordners (nur er kann sie wieder herausnehmen).

## Gleichzeitiges Bearbeiten

Die Versionsprüfung bleibt: `PUT …/content` mit `version` – ist der Stand veraltet, antwortet der Server 409. Prüfen und
Speichern geschehen jetzt unter einer Sperre je Datenbank, damit zwei Benutzer sich nicht überschreiben. Die Weboberfläche
fragt eine geteilt geöffnete Notiz alle 15 Sekunden ab: ist nichts ungespeichert, lädt sie die neue Version; sonst erscheint
„Die Notiz wurde inzwischen von jemand anderem geändert“ mit „Als Kopie speichern“ (neue Notiz im selben Ordner) und „Änderungen verwerfen“.

## Weboberfläche

Rechtsklick auf einen eigenen Ordner → „Teilen …“ (derselbe Dialog wie bei Kalendern; ein Personensymbol am Ordner zeigt, dass er geteilt ist).
Empfänger sehen unter „Geteilt mit mir“ „Name (von Besitzer)“ samt Unterordnern; bei Lesen ist „Neue Notiz“ gesperrt und das Menü auf „Öffnen“ beschränkt. Die Volltextsuche durchsucht
geteilte Ordner mit (Treffer tragen „von Besitzer“).

## Android-App und notizi

Geteilte Notizen erscheinen dort **nicht** – und gehen auch nie in den Abgleich (notizi) eines Empfängers: `NotesDb.Entries()` liest nur die
eigene `notes`-Tabelle, geteilte Notizen liegen ausschließlich beim Besitzer (Test: `SharedNotesTests`). notizi kennt auch noch kein
Nur-Lesen, ein Empfänger dürfte dort also gar nichts davon bekommen. Die Notizen der App kommen ausschließlich aus dem Abgleichordner des Besitzers,
und notizi gehört einem anderen Projekt. Eigene Änderungen des Besitzers und die Änderungen von Empfängern (die in seiner Datenbank landen) gehen
unverändert per Abgleich dorthin. Soll die App später geteilte Ordner zeigen, braucht sie: `GET /api/notes/shared`
(Freigaben mit Unterordnern und `canWrite`), `GET /api/notes?folder=<id>` für die Liste, die negative Kennung für Notizen,
`PUT /api/notes/{id}/content` mit `version` und die Behandlung von 403 (nur lesen) und 409 (veraltet) – dazu eine eigene
Ordnergruppe „Geteilt mit mir“ ohne Abgleich in Room.

## Offene Entscheidungen

1. **Wer hat zuletzt gespeichert?** Die Notiz kennt keinen Bearbeiter; die Meldung bei Konflikt sagt „jemand anderes“. Ein Feld
   `modified_by` bräuchte eine Schemaänderung (und notizi kennt es nicht). Empfehlung: nicht bauen.
2. **Archiv/Papierkorb für Empfänger:** Ein Bearbeiter, der eine Notiz löscht, schickt sie in den Papierkorb des Besitzers;
   zurückholen kann nur der Besitzer. Empfehlung: so lassen.
