# Fotos in der Galerie: Angaben, Ort, Bearbeiten

**Deutsch** · [English](galerie-fotos.md)

Stand: Version 2. Gilt für Fotos in der eigenen Ablage und in Galerie-Ordnern (externe Ordner, die in der Galerie stehen).

## Wo die Angaben landen

dmOffice schreibt Angaben dorthin, wo andere Programme (Lightroom, darktable, digiKam, Windows) sie lesen, ohne exiftool:

- **JPEG:** in die Datei selbst. Ersetzt werden nur die Metadaten-Abschnitte (EXIF, XMP), die Bilddaten bleiben Byte für
  Byte gleich. Der Ort kommt ins EXIF-GPS, Titel, Bildunterschrift, Bewertung und Stichwörter ins XMP.
- **RAW:** nie in die Datei. Änderungen kommen in die XMP-Datei daneben: eine vorhandene `foto.arw.xmp` oder `foto.xmp`,
  sonst eine neue `foto.xmp`. Angaben anderer Programme darin (Lightroom-Entwicklung, darktable-Verlauf, hierarchische
  Stichwörter …) bleiben erhalten.
- Nach dem Schreiben liest dmOffice das Ergebnis zurück und prüft es (Bilddaten gleich, Aufnahmezeit und Kamera wie
  vorher, Ort wie gewollt). Schlägt die Prüfung fehl, bleibt die Datei unverändert.

Galerie-Ordner müssen dafür beschreibbar eingebunden sein. Für RAW-Fotos in einem Galerie-Ordner müssen dort
„XMP-Dateien“ eingeschaltet sein (*Galerie* → *Galerie-Ordner*), sonst läse die Galerie das Geschriebene nicht.

Beim Lesen geht eine XMP-Datei den Angaben im Foto vor, und XMP geht IPTC vor.

## Titel, Bildunterschrift, Bewertung, Stichwörter

Ein oder mehrere Fotos markieren → im Menüband *Angaben* oder im Kontextmenü *Titel, Stichwörter, Bewertung …* (App:
Auswahl → Menü). Bei mehreren Fotos:

- Felder, die man nicht anfasst, bleiben bei jedem Foto, wie sie sind. Ein Feld mit unterschiedlichen Werten zeigt
  „Unterschiedlich“.
- Stichwörter zeigen, wie viele der Fotos sie haben („Berge (3/5)“). Hinzugefügte bekommen alle markierten Fotos,
  entfernte verlieren alle. Entfernen nimmt sie auch aus Lightrooms hierarchischen Stichwörtern, damit sie nicht
  zurückkommen.

Die Suche in der Galerie findet Titel, Bildunterschriften und Stichwörter.

## Ort

- **Von Hand:** Fotos markieren → *Atlas* → *Ort für n markierte Fotos setzen* → auf die Karte tippen. Ein vorhandener Ort
  wird nach Rückfrage ersetzt.
- **Aus XMP-Dateien:** Ein Ort, den Lightroom in `foto.xmp` geschrieben hat, erscheint im Atlas und im Detailbereich.

Sind Fotos markiert, zeigt der Atlas nur diese.

## Photo-Engine

RAW-Entwicklung und Bearbeitungsvorschläge rechnet die Photo-Engine. Sie ist in dmOffice enthalten, läuft nur auf deinem
Server und startet erst beim ersten Auftrag; nach zehn Minuten ohne Auftrag beendet dmOffice sie wieder. Unter *Verwaltung* → *Module*
lässt sie sich ausschalten. Auf einem anderen Rechner: dort `photo-engine/` starten und dmOffice `PHOTO_ENGINE_URL` geben (optional
`PHOTO_ENGINE_TOKEN`). Eine Vollauflösung braucht je nach Foto 0,3 bis 1 GB Arbeitsspeicher, ein Speicherlimit des Containers sollte das einplanen.

## Bearbeiten, ohne das Original zu ändern

Der Foto-Editor merkt sich Drehen, Ausschnitt und Regler als Einstellungen in der Datenbank. Die Originaldatei bleibt
unverändert. Vorschaubilder und Betrachter zeigen die bearbeitete Fassung, Kacheln und Betrachter kennzeichnen sie als
„bearbeitet“ (*Original* gedrückt halten zum Vergleich). *Als JPG exportieren* legt eine Kopie an, *Bearbeitung entfernen*
holt das Original zurück. *Automatisch verbessern* merkt sich ebenfalls nur Einstellungen.
