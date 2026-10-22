# dmOffice für Android

**Deutsch** · [English](README.md)

Kalender, Aufgaben, Mail, Kontakte, Notizen und Dateien aus dmOffice auf dem Handy, mit Widgets für den Startbildschirm.
Native Android-App: Kotlin, Jetpack Compose, Glance-Widgets, Room, WorkManager, OkHttp.
Die App spricht CalDAV mit dem dmOffice-Server (`/dav/calendars/<login>/`), nutzt für den Rest die Web-API des Servers und meldet sich mit einem App-Passwort an.

## Was die App kann

- **Einrichtung per QR-Code**: in dmOffice unter Einstellungen → Konto → Geräte & Sync →
  „Handy-App koppeln“. Der Code enthält `dmoffice://pair?v=1&url=…&user=…&token=…&name=…`.
  Alternativ von Hand mit Adresse, Anmeldename und App-Passwort. Wird der Code mit der
  Kamera-App gescannt, öffnet sich dmOffice direkt (Deep Link `dmoffice://pair`).
- **Agenda** (die nächsten 90 Tage), **Tag**, **Woche**, **Monat**.
- **Termine** anlegen und bearbeiten: ganztägig oder mit Uhrzeit, Ort, Notizen, Erinnerung,
  einfache Wiederholung (täglich, wöchentlich, monatlich, jährlich). Serien vom Server werden
  ausgefaltet (FREQ, INTERVAL, COUNT, UNTIL, BYDAY, BYMONTHDAY, BYMONTH, EXDATE,
  RECURRENCE-ID-Ausnahmen). Änderungen gelten für die ganze Serie.
- **Aufgaben** mit Fälligkeit (Datum, optional Uhrzeit), Priorität, Notizen und Abhaken;
  Schnell-Eingabe oben in der Aufgabenliste. Fällige Aufgaben erscheinen auch in Agenda und Kalender.
- **Datum und Uhrzeit aus dem Text** (Deutsch und Englisch): „Zahnarzt morgen Nachmittag“,
  „Bericht nächsten Mittwoch Viertel nach 3“, „Weihnachtsfeier 24.12. 14 Uhr“, „Paket in 3 Tagen“,
  „Workshop Freitag von 9 bis 11“, „Dentist tomorrow 3pm“. Die Schnell-Eingabe übernimmt das
  direkt (mit Vorschau unter dem Feld); in den Editoren für Aufgabe und Termin erscheint unter dem
  Titel ein Chip „Erkannt: … – antippen zum Übernehmen“, der Datum/Zeit setzt und den Titel kürzt.
  Stunden 1–6 ohne Zusatz gelten als nachmittags („um 3“ = 15:00), Tageszeiten: früh 8, vormittags 10,
  mittags 12, nachmittags 14, abends 19, nachts 22 Uhr.
- **Widgets**: „dmOffice Agenda“, „dmOffice Aufgaben“ (Abhaken direkt im Widget) und
  „dmOffice Monat“. Einstellungen je Widget, Details im Abschnitt „Widgets“ unten.
- **Erinnerungen** als lokale Benachrichtigungen (VALARM), mit Schlummern und „Erledigt“ bei Aufgaben.
- **Synchronisierung**: `sync-collection` (RFC 6578) je Kalender mit Token, ETags, `If-Match`
  beim Schreiben, `If-None-Match: *` beim Anlegen. Offline-Cache in Room; lokale Änderungen
  werden beim nächsten Sync hochgeladen. Bei `412` gewinnt der Server, die eigene Version
  bleibt als „(Konfliktkopie)“ erhalten. Hintergrund-Sync alle 15 Minuten plus sofort nach
  jeder Änderung.
- **E-Mail** über die Web-API von dmOffice (Reiter „Mail“): Konten und Ordner, Posteingang mit
  Cache (offline lesbar), Lesen im WebView mit serverseitig bereinigtem HTML (externe Bilder auf
  Wunsch), Anhänge öffnen, Antworten, Allen antworten, Weiterleiten, Senden über den Server
  (SMTP des Kontos, Kopie in Gesendet), Markieren, Ungelesen, Papierkorb. Benachrichtigungen bei neuer Post lassen sich je Konto einstellen.
- **Kontakte** (Mehr → Kontakte): Liste mit Suche, Karte mit Anrufen, SMS, Mail, Karte; lokal gecacht.
- **Notizen** (Mehr → Notizen): Liste mit Suche, Lesen; Bearbeiten absatzweise mit Versionsprüfung
  (gesperrte Blöcke und Notizen mit Tabellen/Bildern bleiben unverändert).
- **Dateien** (Reiter Dateien): durchsuchen, Liste oder Kacheln, Vorschau (Bilder, PDF, Text, Video), in anderen Apps öffnen, umbenennen, löschen, Ordner anlegen, hochladen, frühere Versionen, Freigabelinks (ansehen, hochladen, nur hochladen; Ablaufdatum und Passwort). **Offline verfügbar** hält eine Kopie markierter Dateien oder Ordner auf dem Handy und aktuell. Office-Dokumente öffnen im Editor des Office-Servers (siehe [docs/office.de.md](../../docs/office.de.md)).
- **Automatisches Hochladen** (Optionen): Regeln laden neue Fotos, Videos oder andere Dateien aus einem Ordner des Handys in einen Ordner der dmOffice-Dateien.
  - Quellen: Kamera, Screenshots, WhatsApp-Medien, ein beliebiger Medienordner oder ein von Hand gewählter Ordner.
  - Zielordner in dmOffice, wahlweise mit Unterordnern nach Jahr, Monat oder Tag oder mit den Unterordnern der Quelle.
  - Dateitypen: Fotos und Videos, nur Fotos, nur Videos oder alle Dateien.
  - Gleicher Name, aber andere Datei: beide behalten (mit Nummer), überspringen oder ersetzen (die alte bleibt als frühere Version).
  - Danach: auf dem Gerät behalten oder vom Gerät löschen (fragt jedes Mal nach; ohne Nachfrage erlaubt Android das nur mit „Zugriff auf alle Dateien“).
  - Wann: nur WLAN, auch im Roaming, nur beim Laden. Startdatum oder auch vorhandene Dateien hochladen.
  - Neue Fotos und Videos werden binnen Sekunden bemerkt, auch bei geschlossener App; als Sicherheitsnetz wird alle 15 Minuten geprüft. Die App merkt sich, was sie hochgeladen hat. Eine Datei, deren Inhalt schon irgendwo in dmOffice liegt (gleiche SHA-256, auch von einem anderen Gerät), wird nicht noch einmal übertragen. Eine Warteschlange mit Verlauf zeigt den Fortschritt; fehlgeschlagene Uploads lassen sich wiederholen.
- **Speicherplatz freigeben** (Optionen): zeigt Fotos und Videos, die der Server per Prüfsumme als gesichert bestätigt hat (`POST /api/files/hashes`) und die sich seitdem auf dem Gerät nicht geändert haben, und löscht sie auf Wunsch. Direkt vor dem Löschen fragt die App den Server noch einmal. Die letzten 30 Tage bleiben standardmäßig auf dem Gerät (0, 7, 30, 90 oder 365 Tage zur Wahl). In dmOffice bleiben die Dateien; jedes Gerät gibt nur seine eigenen frei.
- **Updates**: Die App sucht beim Start und einmal am Tag bei GitHub nach neuen Versionen (Optionen → „Nach Updates suchen“), zeigt die Neuerungen und installiert die APK, nachdem sie die Signatur mit demselben Schlüssel geprüft hat. Android fragt einmal, ob Installationen aus dieser Quelle erlaubt sind. Entwicklerbuilds sind anders signiert und aktualisieren sich so nicht.
- **Android-Kalender und -Kontakte** (Optionen): auf Wunsch werden Kalender und Kontakte in die
  Android-Datenbanken gespiegelt (CalendarContract als lokales Konto „dmOffice“, nur lesbar;
  Kontakte als Gerätekontakte). Samsung-Kalender, Telefon, WhatsApp usw. sehen sie dann.
  Einbahnstraße dmOffice → Android, die Kopien liegen dort unverschlüsselt; abschaltbar, beim
  Ausschalten oder Entfernen des Kontos werden sie gelöscht.
- **Sicherheit**: lokale Datenbank mit SQLCipher verschlüsselt (Schlüssel im Keystore-geschützten
  Speicher), optionale App-Sperre per Fingerabdruck, Gesicht oder Geräte-PIN. Der Server nimmt das
  App-Passwort für die Web-API nur für Mail, Kalender, Kontakte, Notizen und Dateien an, nie für
  Verwaltung oder Anmeldeeinstellungen.
- **Verschlüsselter Umschlag** (zusätzlich zu TLS, Server `E2e.cs`, App `api/E2e.kt`): beim Koppeln
  holt die App den öffentlichen Serverschlüssel (aus dem QR-Code, Parameter `spk`, sonst von
  `/api/e2e/key`), erzeugt ein eigenes P-256-Schlüsselpaar und registriert es mit dem App-Passwort
  (`/api/e2e/register`). Aus ECDH und HKDF-SHA256 entsteht ein AES-256-GCM-Schlüssel; danach geht jede
  Anfrage (Web-API und CalDAV) als Chiffrat an `/api/e2e/{user}/{device}` und die Antwort kommt
  genauso zurück. Proxy, Netz und TLS-Endpunkt sehen nur Chiffrat, entschlüsselt wird erst in
  dmOffice. Zähler gegen Wiederholung, Widerruf des App-Passworts löscht den Geräteschlüssel. Ein
  Server ohne `/api/e2e/key` wird mit TLS allein bedient; der Zustand steht in den Optionen unter
  „Sicherheit“.
- Hell und Dunkel nach System (Material You), Deutsch und Englisch nach Systemsprache.

## Bauen

Voraussetzungen: JDK 17 und das Android SDK (Plattform 36). Android Studio ist nicht nötig.

```bash
# JDK, z. B. unter Windows
winget install Microsoft.OpenJDK.17

# SDK-Pfad eintragen (einmalig), Vorwärts-Schrägstriche auch unter Windows
echo "sdk.dir=C:/Users/<name>/AppData/Local/Android/Sdk" > apps/mobile/local.properties

cd apps/mobile
./gradlew :app:assembleDebug          # -> app/build/outputs/apk/debug/app-debug.apk
./gradlew :app:testDebugUnitTest      # Parser, Serienregeln, DAV-XML
```

Unter Windows `gradlew.bat` statt `./gradlew`; `JAVA_HOME` muss auf das JDK 17 zeigen.
Der erste Build lädt Gradle-Abhängigkeiten und dauert einige Minuten.

Die Debug-Variante heißt `at.dimes.dmoffice.debug` und kann neben einer Release-Version
installiert sein.

Release-APKs baut und signiert `.github/workflows/android.yml` für jedes Versions-Tag (`v1.2.3`) und hängt sie als `dmOffice-1.2.3.apk` und `dmOffice.apk` an den GitHub-Release. Der Signaturschlüssel liegt in den Repository-Geheimnissen, nie im Repository; immer denselben Schlüssel nehmen, weil Android ein Update nur mit derselben Signatur installiert.

## Auf dem Handy installieren und gegen eine lokale Instanz testen

```bash
adb install -r app/build/outputs/apk/debug/app-debug.apk

# dmOffice lokal auf Port 5099 laufen lassen und den Port aufs Handy durchreichen
adb reverse tcp:5099 tcp:5099
```

Dann in der App „Von Hand einrichten“: Adresse `http://127.0.0.1:5099`, Anmeldename und ein
App-Passwort aus den dmOffice-Einstellungen. Klartext-HTTP ist nur für `127.0.0.1`,
`localhost` und `10.0.2.2` (Emulator) erlaubt (`res/xml/network_security_config.xml`);
produktiv braucht es HTTPS.

Ohne Tippen geht die Kopplung auch per Intent:

```bash
adb shell am start -a android.intent.action.VIEW -d "'dmoffice://pair?v=1&url=http%3A%2F%2F127.0.0.1%3A5099&user=<login>&token=<app-passwort>&name=<name>'"
```

## Bedienung

- **Agenda**: nach Tagen gruppiert, Haken bei fälligen Aufgaben. Plus-Knopf: neuer Termin.
- **Kalender**: oben zwischen Tag, Woche und Monat umschalten, Pfeile blättern, das
  Kalender-Symbol springt zu heute. In der Monatsansicht einen Tag antippen, darunter
  erscheint seine Liste. Der Plus-Knopf legt den Termin am gewählten Tag an.
- **Dateien**: Datei antippen öffnet sie, langes Drücken wählt aus; das Drei-Punkte-Menü bietet Umbenennen, Löschen, Versionen, Teilen und „Offline verfügbar“.
- **Aufgaben**: Schnell-Eingabe oben (nur Titel, kommt in den ersten Aufgaben-Kalender),
  Gruppen überfällig / heute / später / ohne Datum. Erledigte lassen sich in den
  Optionen einblenden.
- **Optionen**: Konto, Sync jetzt, letzte Synchronisierung und Fehler, Kalender
  ein- und ausblenden, Standard-Erinnerung, automatisches Hochladen und Speicherplatz freigeben, Updates, Konto entfernen.
- **Widgets**: Startbildschirm lange drücken → Widgets → dmOffice. Alle Widgets lassen sich
  in der Größe ändern. Ein Tipp auf einen Eintrag öffnet ihn in der App, der Haken im
  Aufgaben-Widget erledigt die Aufgabe sofort und stößt den Sync an.

## Widgets

Drei Widgets, jedes mit eigenen Einstellungen (Zahnrad in der Titelzeile oder beim Anlegen),
gespeichert je Widget-Instanz in `data/WidgetPrefs.kt`:

- **dmOffice Agenda**: die nächsten Tage als Liste mit Tagesköpfen.
- **dmOffice Aufgaben**: offene Aufgaben mit Haken zum Erledigen.
- **dmOffice Monat**: Monatsraster, darunter wahlweise die Liste des gewählten Tages.
  Tag antippen wechselt die Liste, Pfeile blättern den Monat, ein Tipp auf den Monatsnamen
  springt zurück zu heute. Monat und gewählter Tag bleiben je Widget erhalten.

Einstellungen:

| Bereich | Optionen |
|---|---|
| Inhalt | Termine und Aufgaben / nur Termine / nur Aufgaben; Zeitraum in Tagen; mehrtägige Termine (an jedem Tag, nur erster und letzter Tag, nur am laufenden Tag); Höchstzahl Einträge; nur Aufgaben mit Fälligkeit; erledigte Aufgaben |
| Monatsraster | Agenda-Widget: „Monatsraster oben einblenden“ (die Liste beginnt dann am gewählten Tag); Monats-Widget: Liste des gewählten Tages ein/aus; Tage mit Einträgen als Punkte oder kleine Balken in den Kalenderfarben (bis zu drei je Tag) |
| Darstellung | Zeilenlayout kompakt (eine Zeile: Zeit und Titel) / normal (Titel, darunter Zeit und Ort) / ausführlich (Titel, Zeit, Ort, Kalendername); Kalenderfarbe als Balken, Punkt oder gar nicht; Zeitangabe links neben oder unter dem Titel; Tageskopf relativ (Heute/Morgen/Wochentag) oder immer als Datum; Schriftgröße; Deckkraft des Hintergrunds; Titelzeile; Ort |

Mehrtägige Termine folgen in allen Widgets derselben Regel (`MultiDayRule`), auch im Monatsraster.
Die Umsetzung liegt in `widget/Widgets.kt` (gemeinsame Bausteine, Agenda, Aufgaben, Raster) und
`widget/MonthWidget.kt`.

## Push über Firebase (optional)

Ohne Push fragt die App alle 15 Minuten nach Neuem. Mit Push schickt dmOffice bei jeder Änderung
(Mail, Termine, Aufgaben, Kontakte, Notizen) ein inhaltsloses Signal über Google Firebase Cloud
Messaging, und die App gleicht sofort ab. Über Google läuft dabei nur „es gibt Neues“; die Daten
holt die App selbst über den verschlüsselten Umschlag von dmOffice.

Einmalig einrichten:

1. **Firebase-Projekt**: <https://console.firebase.google.com> → Projekt hinzufügen (Google
   Analytics ist nicht nötig).
2. **Android-Apps**: Im Projekt zwei Android-Apps anlegen, mit den Paketnamen
   `at.dimes.dmoffice` (Release) und `at.dimes.dmoffice.debug` (Debug). Danach unter
   Projekteinstellungen → Allgemein die Datei `google-services.json` herunterladen; sie enthält
   beide Apps.
3. **Für die App**: `google-services.json` nach `apps/mobile/app/` legen (steht in `.gitignore`)
   und die App neu bauen. Ohne die Datei baut die App genauso, nur ohne Push.
4. **Für den Server**: Projekteinstellungen → Dienstkonten → „Neuen privaten Schlüssel
   generieren“. Die JSON-Datei als `keys/fcm.json` in den Datenordner von dmOffice legen
   (auf dem Unraid unter appdata, neben `keys/master.key`) oder den Pfad in der
   Umgebungsvariable `DMOFFICE_FCM_CREDENTIALS` angeben, dann den Container neu starten.
   Der Schlüssel ist geheim, er gehört nicht ins Repo.

Im Server-Log steht danach nicht mehr „Push: kein FCM-Schluessel“. Die App meldet ihr Token beim
Start an; es hängt am App-Passwort und fällt mit dessen Widerruf weg.

Technik: `DmOffice.Server/Push.cs` prüft alle 10 Sekunden für Benutzer mit angemeldetem Handy einen
Fingerabdruck aus Mail-Version, Kalender-Synctokens und Notizen-Stand und schickt höchstens alle
15 Sekunden ein Signal (FCM HTTP v1, Datennachricht mit hoher Priorität). In der App startet
`sync/PushService.kt` daraufhin den Abgleich.

## Aufbau des Codes

| Ordner | Inhalt |
|---|---|
| `data/NaturalDate.kt` | Datum/Uhrzeit aus Text (de/en), `data/WidgetPrefs.kt` Einstellungen je Widget |
| `api/` | Web-API-Client (App-Passwort, JSON) und Datenformate wie web/src/api.ts |
| `mail/` | Mail-Repository mit Cache; `data/ContactsRepo.kt`, `data/NotesRepo.kt` ebenso |
| `files/` | Dateien, Offline-Kopien, Vorschaubilder, Dokumentenanbieter, Anbindung des Office-Editors |
| `upload/` | Automatisches Hochladen (Suche, Warteschlange, Regeln) und die Logik für „Speicherplatz freigeben“ |
| `update/` | Update-Suche, Download und Prüfung der Signatur |
| `sync/ProviderSync.kt` | Spiegel in CalendarContract und ContactsContract |
| `sync/PushService.kt` | Push über Firebase: Token anmelden, Signal startet den Abgleich |
| `ical/` | iCalendar-Leser/-Schreiber (Rundreise unbekannter Eigenschaften) und RRULE-Ausfaltung |
| `dav/` | CalDAV-Client (OkHttp) und namespace-bewusster Multistatus-Parser (XmlPullParser) |
| `data/` | Room-Datenbank, Konto (verschlüsselt), Modell/Feldableitung aus ICS, Repository |
| `sync/` | Sync-Engine (hochladen, sync-collection, Konflikte) und WorkManager-Worker |
| `notify/` | Erinnerungen über AlarmManager, Mail-Benachrichtigungen, Boot-Receiver |
| `widget/` | Glance-Widgets für Agenda, Aufgaben und Monat, Einstellungs-Activity |
| `ui/` | Compose-Oberfläche: Einrichtung, Agenda, Kalender, Aufgaben, Editoren, Optionen |

Der Multistatus-Parser vergleicht Namespace und lokalen Namen, nie das Präfix. Damit ist
die App unabhängig davon, ob der Server `d:`, `D:` oder gar kein Präfix schreibt.

## Noch offen

- Serien nur als Ganzes bearbeiten (keine Einzelinstanz-Ausnahmen anlegen).
- Keine Zeitraster-Ansicht für Tag und Woche; es sind Listen.
- Keine Unteraufgaben, keine Vorlagen, keine Jahresansicht.
- Widgets: keine Wochenansicht, kein Zeitraster, keine freie Farbwahl für Text und Hintergrund.
- Mail: beim Schreiben nur Text.
- Kontakte nur lesen, Notizen ohne Formatierung, Anhänge, Ordner.
- Android-Spiegel ohne Aufgaben und ohne Einzelausnahmen von Serien.
