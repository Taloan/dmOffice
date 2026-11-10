# Audiothek

[English](audiothek.md) · Deutsch

Stand: Version 2, Stufen 1 bis 3 (Web mit Playlists, Android-App) ([Konzept](audiothek-konzept.md)).

Die Audiothek spielt den eigenen Musikbestand aus externen Ordnern ab. dmOffice liest die Dateien nur: Es benennt nichts um und
schreibt keine Tags.

## Einschalten

Die Audiothek ist aus, bis die Verwaltung sie einschaltet: *Verwaltung* → *Dienste* → *Audiothek*. Dann haben sie alle
Benutzer mit Dateiablage. Ausgeschaltet bleiben eingelesene Titel und Hörverlauf erhalten.

## Audiothek-Ordner

*Audiothek* → *Audiothek-Ordner*: einen externen Ordner wählen (Serverordner der Verwaltung oder eigener WebDAV-Ordner, siehe
[Externe Ordner](externe-ordner.de.md)) und darin den Ordner, in dem die Musik liegt. Unterordner gehören dazu; versteckte Ordner
(`.name`, `@eaDir`, `#recycle`) nicht.

- **Dateiarten:** MP3, M4A, AAC, FLAC, OGG und Opus. Der Browser spielt sie direkt; nichts wird umgewandelt.
- **Einlesen:** erst die Liste (Titel aus dem Dateinamen, Album aus dem Ordnernamen), dann Titel, Interpret, Album, Titel- und
  CD-Nummer, Jahr, Genre, Dauer und Cover aus den Dateien. Ohne eingebettetes Cover nimmt dmOffice `cover`, `folder`, `front`,
  `album` oder `albumart` (JPG oder PNG) aus dem Ordner.
- **Überwachen:** alle 15 Minuten neu einlesen; neue Titel erscheinen, gelöschte verschwinden. Ohne *Überwachen* nur auf Knopfdruck.
- **Nicht erreichbar:** Ist der externe Ordner nicht erreichbar, bleiben seine Titel in der Liste, ausgegraut mit rotem
  Stoppschild, und lassen sich nicht abspielen.

## Ansichten und Suche

- **Titel, Alben, Interpreten, Genres** im Bereich links; ein Klick auf Album, Interpret oder Genre zeigt die Titel dazu.
- **Suche** findet jedes Wort in Titel, Interpret, Album, Genre und Jahr („beatles 1969“).
- Eine Titelliste sortiert ein Klick auf die Spaltenüberschrift. Doppelklick spielt die Liste ab diesem Titel (Handy: Tippen).
  Strg-Klick und Umschalt-Klick wählen mehrere Titel, Strg+A alle. Das Kontextmenü bietet *Als Nächstes abspielen*,
  *Zur Warteschlange hinzufügen*, *Zur Playlist hinzufügen*, *Album öffnen* und *Interpret öffnen*.

## Player

Die Player-Leiste unten bleibt in allen Bereichen stehen und spielt weiter, wenn man zu Mail oder Kalender wechselt. Sie hat
Zufall, vorheriger/nächster Titel, Wiederholen (aus, alle, diesen Titel), Position und Lautstärke. Medientasten und der
Sperrbildschirm der installierten Web-App funktionieren über die Media Session des Browsers.

Die **Warteschlange** (*Audiothek* → *Warteschlange* oder Klick auf die Player-Leiste) zeigt, was als Nächstes kommt: Klick
springt zum Titel, Ziehen ändert die Reihenfolge, × nimmt ihn heraus. Die Warteschlange bleibt im Browser auch nach dem Neuladen.

## Playlists

**Eigene Playlists** stehen links unter *Playlists*; *+* oder *Neue Playlist* im Menüband legt eine an.

- Titel kommen dazu über *Zur Playlist hinzufügen* im Kontextmenü (auch für eine Auswahl), über den Knopf *Zur Playlist* bei
  Album, Interpret oder Genre oder per Ziehen gewählter Titel auf eine Playlist links. *Als Playlist speichern* in der
  Warteschlange speichert, was dort steht.
- In einer geöffneten Playlist ändert Ziehen die Reihenfolge; *Aus der Playlist entfernen* (Kontextmenü) oder die Entf-Taste
  nimmt Titel heraus. Derselbe Titel darf mehrmals vorkommen. *Umbenennen* und *Löschen* stehen neben *Abspielen*; Löschen
  lässt die Musikdateien unberührt.
- Wie der Hörverlauf hängen die Einträge an externem Ordner und Pfad: Nimmt man einen Audiothek-Ordner heraus, verschwinden
  seine Titel aus der Playlist und kommen zurück, wenn man ihn wieder aufnimmt.

**Auto-Playlists** entstehen aus dem Hörverlauf, je höchstens 100 Titel:

| Auto-Playlist | Inhalt |
|---|---|
| Zuletzt hinzugefügt | neueste Dateien zuerst (Dateidatum) |
| Zuletzt gehört | zuletzt gehörte zuerst |
| Am meisten gehört | meistgehörte zuerst |
| Selten gehört | schon gehört, aber nicht in den letzten 90 Tagen, am seltensten gehörte zuerst |
| Nie gehört | nie als gehört gezählt, in zufälliger Reihenfolge |

Listen je Genre stehen unter *Genres*.

## Android-App

Die App zeigt die Audiothek als eigenen Bereich (*Audiothek* in der Leiste unten oder unter *Mehr*), wenn der Server sie
anbietet. Drei Ansichten: **Playlists** (Auto-Playlists und eigene), **Alben** und **Titel**; das Suchfeld durchsucht alle
Titel. Tippen auf einen Titel spielt die Liste ab dort; Album und Playlist haben *Abspielen* und *Zufällig*.

- Sie spielt im Hintergrund weiter, mit Benachrichtigung, auf dem Sperrbildschirm und mit Bluetooth-Tasten.
- Der laufende Titel steht in allen Bereichen über der Leiste unten; Tippen öffnet den Player mit Position, Zufall,
  Wiederholen und Warteschlange (Tippen springt hin, × nimmt heraus).
- Was man hört, zählt in den Hörverlauf; ohne Netz meldet die App es später nach.
- Playlists legt man im Web an und bearbeitet sie dort.
- Widget **dmOffice Musik** für den Startbildschirm: Cover, Titel, Interpret, zurück, Abspielen/Pause und weiter; Tippen öffnet die Audiothek.

## Hörverlauf

Als gehört zählt ein Titel nach der Hälfte seiner Dauer, höchstens nach 4 Minuten; früher abgebrochen zählt er als übersprungen.
Die Spalte *Gehört* zeigt die Anzahl, der Tooltip das letzte Mal. Der Hörverlauf liegt je Benutzer und bleibt, wenn man einen
Audiothek-Ordner herausnimmt und wieder aufnimmt.
