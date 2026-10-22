# Geteilte Kalender
Deutsch · [English](geteilte-kalender.md)

Ein Kalender – z. B. „Familie“ oder „Essensplan“ – kann mit anderen Benutzern desselben Servers geteilt werden:
zum **Lesen** oder zum **Bearbeiten**, vom Besitzer vergeben und jederzeit änderbar oder entziehbar.

## Bestandsaufnahme

| Was | Wie es heute gespeichert ist |
|---|---|
| Benutzer | `data/users.json`: Login, Anzeigename, Passwort-Hash, mit dem Hauptschlüssel **eingepackter** Datenbankschlüssel (`UserRecord.WrappedKey`) |
| Daten eines Benutzers | eine SQLCipher-Datenbank je Benutzer (`UserDb`), Schlüssel = 32 Zufallsbytes, eingepackt mit dem Hauptschlüssel (`DMOFFICE_MASTER_KEY`, siehe `Vault`) |
| Kalender | Tabelle `collections` (`kind='calendar'`, `uri`, `name`, `color`, `components`, `synctoken`) in der Datenbank des **Besitzers**; Kalender und Aufgabenlisten sind dasselbe (`VEVENT,VTODO`) |
| Termine | Tabelle `objects`: je Termin der Text der `.ics`-Datei, dazu `etag`, `first_start`/`last_end` (Bereichsabfragen) und `changes` (Sync-Token, RFC 6578) |
| CalDAV | `Dav/DavServer.cs`: `/dav/calendars/<login>/<kalender>/<objekt>.ics`, immer in der Datenbank des angemeldeten Benutzers |
| Weboberfläche | `/api/pim/*`; `web/src/calendar` |
| Android-App | gleicht ausschließlich über CalDAV ab (`DavClient`, `SyncEngine`, Sync-Token) – sie hat keine eigene Kalenderlogik gegenüber dem Server |
| Freigaben | `data/shares.json` (`ShareStore`): gab es schon für **Aufgabenlisten** (Besitzer, Sammlungs-Id, Mitglieder) |

## Die offene Frage: Verschlüsselung

*Wie kommt ein zweiter Benutzer an die verschlüsselten Daten, ohne dass der Besitzer angemeldet ist?*

Die Benutzerdatenbanken sind **serverseitig** verschlüsselt (Schutz der Daten im Ruhezustand: kopierte Dateien, Backups,
fremder Zugriff auf das Datenverzeichnis). Der Server kann jede Datenbank öffnen, weil er den Hauptschlüssel aus der
Umgebung kennt und damit alle Datenbankschlüssel auspackt – auch für die Hintergrunddienste (Mail-Sync, Regeln),
die ohne Anmeldung laufen. Das Passwort eines Benutzers schützt die Daten **nicht** kryptografisch; es steuert nur den
Zugang über den Server.

Daraus folgt: Ein Teilen **bricht keine Verschlüsselung** auf. Der Kalender bleibt in der Datenbank des Besitzers, und der
Server liest und schreibt dort im Auftrag des Empfängers – nach Prüfung der Freigabe bei **jeder** Anfrage. Es gibt weder
Kopien noch weitergegebene Schlüssel; Widerruf wirkt deshalb sofort. Das ist dieselbe Architektur, die schon die geteilten
Aufgabenlisten tragen (`SharedLists.Resolve`).

Die Grenze, die das mitbringt, sollte man kennen: Wer den Server und den Hauptschlüssel hat (die Verwaltung), kommt an alle
Daten – mit oder ohne Freigabe. Eine Ende-zu-Ende-Verschlüsselung pro Benutzer, bei der auch die Verwaltung nicht lesen
kann, wäre mit gemeinsamen Kalendern nur über Schlüsselaustausch zwischen den Benutzern (Kalenderschlüssel, mit dem
öffentlichen Schlüssel jedes Mitglieds verpackt) zu haben und passt nicht zum heutigen Modell (Server-Hintergrunddienste,
CalDAV mit App-Passwort). Das ist **keine** Entscheidung dieser Änderung.

## Umsetzung

**Gemeinsame Quelle:** Die Sammlung bleibt beim Besitzer. Ein Empfänger sieht sie unter einer eigenen Adresse
`shared-<Freigabe-Id>` (Web: negative Id `-<Freigabe-Id>`, wie bei Aufgabenlisten), ihre Termine werden direkt aus der
Datenbank des Besitzers gelesen und geschrieben. ETag und Sync-Token sind die der Sammlung selbst – ein Termin, den
Cleo ändert, hat für Anna und Ben sofort einen neuen ETag, und jeder Sync-Client holt ihn mit der nächsten Abfrage.

**Rechte** (`shares.json`, `Share.Members` + `Share.ReadOnly`; ältere Dateien ohne `ReadOnly` gelten als „Bearbeiten“):

| | Besitzer | Bearbeiten | Lesen | ohne Freigabe |
|---|---|---|---|---|
| Termine sehen / exportieren | ✔ | ✔ | ✔ | – (404) |
| Termine anlegen, ändern, löschen, verschieben | ✔ | ✔ | – (403) | – (404) |
| Name, Farbe, Import, Löschen des Kalenders | ✔ | – (403) | – (403) | – |
| Teilen und Rechte ändern | ✔ | – | – | – |
| Nicht mehr anzeigen (aussteigen) | – | ✔ | ✔ | – |

Der Zugriff wird bei jeder Web-/CalDAV-Anfrage gegen `shares.json` geprüft. Wird der Besitzer oder der Kalender gelöscht,
räumt `ShareStore.Forget` die Freigaben auf.

**Weboberfläche:** Rechtsklick/Menü am eigenen Kalender → „Teilen …“ (Dialog mit Lesen/Bearbeiten je Person; dasselbe für
Aufgabenlisten). Geteilte Kalender erscheinen in der Seitenleiste als „Name (von Besitzer)“ in der Farbe des Besitzers,
nur lesbare ohne Anlegen/Ziehen/Löschen, mit „Nicht mehr anzeigen“.

**CalDAV:** Im Kalender-Verzeichnis des Empfängers erscheint `shared-<id>` mit Anzeigename „Name (von Besitzer)“, Farbe,
Sync-Token, und `current-user-privilege-set` ohne Schreibrecht, wenn nur lesend geteilt. Schreibversuche auf lesende
Kalender bekommen 403; `DELETE` auf die Sammlung heißt „aussteigen“, `PROPPATCH` (Name/Farbe) ist dem Besitzer vorbehalten.
`MOVE`/`COPY` zwischen eigenem und geteiltem Kalender schreibt zuerst ins Ziel, löscht dann die Quelle.

**Android-App:** Braucht keine eigene Freigabelogik – der Abgleich entdeckt geteilte Kalender über CalDAV, mit Name und Farbe.
Ein Schreibversuch auf einen nur lesbaren Kalender (403) wird in `SyncEngine` verworfen und stellt den Serverstand wieder her,
statt den ganzen Abgleich abzubrechen. Offen: die App zeigt Lese-Kalender noch im Editor als Ziel an (siehe unten).

Bekannte Einschränkung: Zeiten ohne Zeitzone („schwebend“) und Erinnerungen werden in der Zeitzone des **Besitzers**
gelesen/geschrieben.

## Kalenderarten

Jeder Kalender hat eine **Art** (`CalendarKinds.cs`, Weboberfläche `web/src/calendar/kinds.ts`): `normal` (Vorgabe) oder
`meal` (Essensplan). Die Art entscheidet, in welcher Ansicht ein Kalender erscheint – nicht sein Name.

- **Speicherung:** beim Besitzer in der Schlüssel/Wert-Tabelle (`pim.calkind.<id>`), nicht im Kalender. CalDAV, DAVx⁵, iOS und
  die Android-App sehen weiter einen gewöhnlichen Kalender mit gewöhnlichen Terminen. Keine Schema-Migration. Die App kennt die
  Art nicht (nur Web); soll sie sie später nutzen, liefert `GET /api/pim/collections/calendars` sie je Kalender als `calendarKind`.
- **Geteilte Kalender:** die Art gehört dem Besitzer (`PUT /api/pim/collections/{id}` mit `calendarKind`, für Empfänger 403) und
  gilt für Empfänger mit; sie steht auch in deren Kalenderliste. Lesend geteilte Essensplan-Kalender sind nicht beschreibbar.
- **Kalenderliste links:** gruppiert nach Art, je Gruppe eine einklappbare Überschrift („Meine Kalender“, „Essensplan“ ...; leere
  Gruppen außer der normalen fehlen). Die Haken einer Gruppe gelten für deren Ansicht: Tag/Woche/Monat/Liste zeigen nur Kalender
  der Art `normal`, der Essensplan genau die eingeschalteten Kalender seiner Gruppe; die jeweils anderen sind dort ausgeblendet.
- **Register:** `web/src/calendar/kinds.tsx` (`CALENDAR_KINDS`) beschreibt je Art Namen, Gruppentitel, Symbol und – wenn sie
  eine hat – Menüband-Knopf und Planungsansicht (Komponente mit `PlanProps`). Das Menüband und die Liste entstehen daraus.
- **Altbestand:** beim ersten Zugriff nach dem Update (einmal je Benutzer, Marke `pim.calkind.migrated`) bekommt jeder Kalender
  ohne Art, der mindestens einen Termin mit der Kategorie Frühstück/Mittagessen/Abendessen enthält, die Art `meal`.
- **Weitere Arten** (denkbar: Dienstplan, Abwesenheiten, Fahrdienst): Namen in `CalendarKinds.All` und `CALENDAR_KINDS`
  mit Gruppe, Symbol und Planungsansicht aufnehmen (die Ansicht zeigt die eingeschalteten Kalender dieser Art zusammen). Die Einträge bleiben gewöhnliche Termine mit
  eigener Kategorie bzw. `X-DM-…`-Eigenschaft, damit andere Kalenderprogramme sie lesbar zeigen.

## Essensplan (umgesetzt)

Kalenderansicht → Menüband „Essensplan“: eine Woche mit Frühstück, Mittagessen und Abendessen je Tag, aus **allen
eingeschalteten Kalendern der Art „Essensplan“** (siehe Kalenderarten), jeder Eintrag in der Farbe seines Kalenders – am
besten ein geteilter, den die Familie gemeinsam bearbeitet. Die linke Liste zeigt im Plan nur diese Kalender und schaltet
ein/aus, welche im Plan stehen. Beim Eintragen fragt der Dialog bei mehreren beschreibbaren Essensplan-Kalendern, wohin
(Vorgabe: zuletzt benutzt, sonst der erste); „Zutaten auf die Einkaufsliste“ nimmt die Rezepte aller sichtbaren. Ohne
Essensplan-Kalender zeigt die Ansicht einen Hinweis mit „Essensplan-Kalender anlegen“; der Kalendername spielt keine Rolle.

- Ein Eintrag ist ein gewöhnlicher Termin (Mahlzeit als Kategorie `Frühstück`/`Mittagessen`/`Abendessen`, Uhrzeit 8/12/18 Uhr
  nur für andere Kalenderprogramme). **Freitext:** der Titel. **Rezept:** zusätzlich `X-DM-RECIPE` = `sync_id` der Notiz;
  der Titel bleibt der Rezeptname, andere CalDAV-Programme zeigen so einen lesbaren Termin.
- **Rezepte tragen in notizi den Tag `recipe`** (Vorlage „Rezept“, Rezept-Grabber; Vergleich ohne Groß/klein). Der Tag kommt im
  Abgleichformat als `tags` am Notizelement (notizi `docs/konzept-sync.md`, Abschnitt 4) und liegt in `notes.tags`
  (Migration 17, NULL = Notiz von einem Gerät vor den Tags). Fehlt das Feld im Element, bleiben vorhandene Tags unberührt; eine leere
  Liste löscht sie; beim Zurückschreiben geht `tags` so mit, wie gelesen. Ältere, ungetaggte Rezepte erkennt `Notes/Recipes.cs` weiter
  an der Gestalt der Vorlage („Zutaten“/„Zubereitung“, „Rezept von …“, Eckdatenzeile). Der Ordnername spielt keine Rolle
  (`GET /api/notes/recipes`). Die Auswahl im Dialog sucht darin; das Buch-Symbol am Eintrag öffnet die Notiz.
  Die mitgelieferte Kernbibliothek `vendor/Notizi.Core` hat dafür die Tag-Zeilen aus dem Arbeitsstand von notizi (siehe `HERKUNFT.txt`)
  und ist beim Erscheinen von notizi 1.5.1 durch den veröffentlichten Stand zu ersetzen.
- Ohne Schreibrecht (nur lesend geteilt) bleibt der Plan ansehbar. Beim Zurückkehren ins Fenster wird neu geladen.

Offen für später: Zutaten eines Wochenplans auf die Einkaufsliste setzen; Rezepte aus geteilten Notizordnern (heute nur
die eigenen Notizen).

## Offene Entscheidungen

1. **Verschlüsselung** – siehe oben: Empfehlung ist, es bei „Server öffnet die Datenbank des Besitzers“ zu belassen
   (gleiches Modell wie die geteilten Aufgabenlisten, kein zusätzlicher Schlüsselaustausch).
2. **Schreibrecht „Termine nur anlegen“** (ohne fremde zu ändern) gibt es nicht; Empfehlung: nicht bauen, die Familie
   braucht es nicht.
3. **Android-Editor:** Lese-Kalender dort als Ziel ausblenden – dazu muss `calendars.readOnly` in Room (Schema-Migration,
   Auslesen von `current-user-privilege-set`). Empfehlung: als eigener kleiner Schritt mit der App-Version.
