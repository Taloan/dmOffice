# dmOffice – Handbuch

dmOffice ist ein Büro im Browser: E-Mail, Kalender, Kontakte, Aufgaben, Notizen und Dateien in einer Oberfläche,
die sich wie gängige Office-Programme bedient. Dazu kommt ein eigener CalDAV-, CardDAV- und WebDAV-Dienst, sodass Handy, Tablet und
Thunderbird ihre Daten direkt von dmOffice holen.

Die Bilder zeigen ein erfundenes Beispielkonto („Alex Berger“). Sie werden mit `tools/docs/make.sh` aus einer
leeren Datenbank neu erzeugt.

**Inhalt**

1. [Installation und erster Start](#1-installation-und-erster-start)
2. [E-Mail](#2-e-mail)
3. [Kalender](#3-kalender)
4. [Kontakte](#4-kontakte)
5. [Aufgaben](#5-aufgaben)
6. [Notizen](#6-notizen)
7. [Dateien](#7-dateien)
8. [Handy, Tablet und Thunderbird verbinden](#8-handy-tablet-und-thunderbird-verbinden)
9. [Einstellungen](#9-einstellungen)
10. [Sicherheit](#10-sicherheit)
11. [Verwaltung (Admin)](#11-verwaltung-admin)
12. [Sicherung und Umzug](#12-sicherung-und-umzug)
13. [Tastenkürzel](#13-tastenkürzel)
14. [Häufige Fragen](#14-häufige-fragen)

---

## 1. Installation und erster Start

dmOffice läuft als Docker-Container, gedacht für Unraid, geht aber auf jedem Docker-Host.

**Unraid**

1. Unter *Apps* nach **dmOffice** suchen und installieren. Ist die App dort noch nicht gelistet, siehe
   [README](../README.md#unraid).
2. In der Vorlage **DMOFFICE_MASTER_KEY** setzen – eine lange Zufallsfolge, z. B. aus `openssl rand -hex 32`.
   Diesen Schlüssel gut aufheben (Passwortmanager): **Ohne ihn lassen sich die Daten nicht mehr öffnen.**
3. Port wählen und *Apply*.

**Docker Compose**

```yaml
services:
  dmoffice:
    image: ghcr.io/taloan/dmoffice:latest
    ports: ["8080:8080"]
    volumes: ["./data:/data"]
    environment:
      DMOFFICE_MASTER_KEY: "<lange Zufallsfolge>"
      TZ: Europe/Berlin
    restart: unless-stopped
```

**Erster Aufruf:** `http://<server>:8080/` öffnen. Beim allerersten Aufruf legt dmOffice das Verwaltungskonto an.
Danach meldet man sich mit Anmeldename und Passwort an.

![Anmeldung](img/de/anmeldung.png)

Für den Zugriff von unterwegs und für Handys gehört dmOffice hinter einen Reverse-Proxy mit HTTPS
(Nginx Proxy Manager, SWAG, Traefik). Einige Browserfunktionen (Zwischenablage, Desktop-Benachrichtigungen)
gibt es nur über HTTPS.

---

## 2. E-Mail

![Posteingang](img/de/mail.png)

Links stehen die **Favoriten** und die Ordner jedes Kontos, in der Mitte die Nachrichtenliste, rechts der
Lesebereich und – wenn eingeschaltet – die **Aufgabenleiste** mit Kalender, Personen und Aufgaben.

### Konto einrichten

*Einstellungen* → *Konto* → *E-Mail-Konten* → *Konto hinzufügen*. Für bekannte Anbieter füllt dmOffice die
Server aus, sonst IMAP- und SMTP-Server eintragen. Mit *Verbindung testen* prüfen.

![E-Mail-Konten](img/de/einstellungen-konten.png)

Je Konto lässt sich festlegen:

| Einstellung | Bedeutung |
|---|---|
| **Zeitraum** | *Alles* oder nur die letzten Monate liegen offline in dmOffice und sind durchsuchbar. |
| **Mails bis zu dieser Größe vollständig speichern** | Kleinere Mails kommen samt Anhängen in die Datenbank, größere werden beim Öffnen geholt. |
| **Gesendetes ablegen** | Bei Gmail aus – Gmail legt die Kopie selbst ab. |
| **Sprache der Mails** | Deutsch oder English: bestimmt *AW:/Re:*, *WG:/Fwd:*, den Zitatkopf und die Rechtschreibprüfung beim Antworten. |
| **Signatur** | Wird beim Verfassen unter den Text gesetzt. |

### Lesen, sortieren, ablegen

- **Ziehen** von Mails auf einen Ordner verschiebt sie, auf *Markiert* kennzeichnet sie.
- **Rechtsklick** auf eine oder mehrere Mails öffnet das Kontextmenü mit allen Aktionen.
- **Bilder aus dem Netz** sind blockiert, weil sie dem Absender verraten, dass die Mail gelesen wurde.
  Über *Bilder anzeigen ▾* lassen sie sich einmalig oder für den Absender bzw. die ganze Domain dauerhaft erlauben.
- **Suche** oben in der Liste: einfache Wörter oder gezielt `von:`, `an:`, `betreff:`. Mit *Alle Konten* über
  alle Konten hinweg.
- **Filter** über der Liste: Ungelesen, Gekennzeichnet, Mit Anhang oder eine Kategorie.

### Kategorien

![Kategorisieren](img/de/mail-kategorien.png)

Kategorien sind farbige Schlagworte für Mails. dmOffice speichert sie als **IMAP-Schlagwort** auf dem
Mailserver – sie sind also auch in Thunderbird oder auf einem zweiten dmOffice zu sehen, wenn dort dasselbe
Schlagwort verwendet wird.

- *Kategorisieren ▾* im Menüband oder im Rechtsklick-Menü.
- Verwalten unter *Einstellungen* → *E-Mail* → *Kategorien*: Name, Farbe, Reihenfolge. Umbenennen ändert nur die
  Anzeige, die Mails behalten ihre Kategorie.
- Gmail nimmt keine eigenen Schlagworte an; dort meldet dmOffice das.

![Kategorien verwalten](img/de/einstellungen-kategorien.png)

### Wiedervorlage

![Wiedervorlage](img/de/mail-wiedervorlage.png)

Eine Mail für später zurücklegen: *Wiedervorlage* → *In 1 Stunde*, *Heute Abend*, *Morgen*, *Nächste Woche* …
oder ein eigenes Datum. Die Mail verschwindet in den Ordner *Wiedervorlage* und taucht zur gewählten Zeit
**ungelesen oben im Posteingang** wieder auf. Mit *Wiedervorlage aufheben* kommt sie sofort zurück.

### Schreiben

![Neue E-Mail](img/de/mail-verfassen.png)

- *Neue E-Mail*, *Antworten*, *Allen antworten*, *Weiterleiten* oder *Als Anlage weiterleiten*.
- Empfänger werden beim Tippen aus Kontakten und früheren Adressen vorgeschlagen. Ein früherer Vorschlag lässt sich
  mit **×** vergessen; die Liste verwalten unter *Einstellungen* → *Personen*.
- Entwürfe werden automatisch gespeichert. **Strg+Enter** sendet.
- Ist im Text von einem Anhang die Rede, aber keiner dabei, fragt dmOffice nach.

### Mehr aus einer Mail machen

- **Termin aus E-Mail** – legt einen Termin mit Betreff und Text der Mail an.
- **Als Notiz speichern** – übernimmt die Mail in die Notizen. Oben in der Notiz steht ein Link *Mail in dmOffice
  öffnen*, der die Mail wieder aufruft – auch wenn sie inzwischen in einen anderen Ordner verschoben wurde.

### Regeln

*Einstellungen* → *E-Mail* → *Regeln*. Eine Regel prüft neue Mails im Posteingang (Absender, Empfänger, Betreff,
Text, Kopfzeile) und kann verschieben, kopieren, weiterleiten, als gelesen markieren, kennzeichnen, eine Kategorie
zuweisen oder löschen. Jede Mail durchläuft die Regeln **einmal** – auch wenn sie später wieder im Posteingang
landet.

![Regeln](img/de/einstellungen-regeln.png)

### Junk-E-Mail

Der Junk-Filter lernt mit einem statistischen Verfahren aus dem, was Sie als *Junk* oder *Kein Junk* markieren. Er
bewertet und verschiebt erst, wenn er je 10 Mails beider Sorten gelernt hat; bis dahin bleibt alles im Posteingang.
Schneller geht es mit *Aus Ordnern lernen*. Einstellungen und Schwellen unter
*Einstellungen* → *E-Mail* → *Junk-E-Mail*.

![Junk-E-Mail](img/de/einstellungen-junk.png)

### Hell und dunkel

Oben rechts der Mond schaltet zwischen hell, dunkel und Systemeinstellung um.

![Dunkles Design](img/de/mail-dunkel.png)

---

## 3. Kalender

![Kalender](img/de/kalender.png)

- Ansichten: Tag, Arbeitswoche, Woche, Monat, Liste.
- Mehrere farbige Kalender, einzeln ein- und ausblendbar.
- **Serien**: täglich, werktags, wöchentlich, zweiwöchentlich, monatlich, jährlich. Beim Ändern fragt dmOffice,
  ob nur dieser Termin oder die ganze Serie gemeint ist. Verschiebt man die ganze Serie, wandern abgesagte und
  geänderte Einzeltermine mit.
- **Erinnerungen** erscheinen als Hinweis im Browser (Benachrichtigungen erlauben) und auf allen verbundenen Geräten.
- **Import/Export** als `.ics` über das Menü des Kalenders – so ziehen Kalender aus Nextcloud oder Google um.
- Wird ein Termin gleichzeitig am Handy geändert, warnt dmOffice beim Speichern, statt die Änderung zu überschreiben.

---

## 4. Kontakte

![Kontakte](img/de/kontakte.png)

Adressbücher mit Fotos, mehreren Telefonnummern, Adressen und Geburtstagen. Felder, die dmOffice nicht selbst
bearbeitet (z. B. Postfach oder zusätzliche Telefontypen vom iPhone), bleiben beim Speichern erhalten.
Import und Export als `.vcf`.

---

## 5. Aufgaben

![Aufgaben](img/de/aufgaben.png)

*Mein Tag*, *Wichtig*, *Geplant* und eigene Listen. Aufgaben sind VTODO-Einträge in einem Kalender und erscheinen
damit in jeder Aufgaben-App, die CalDAV kann (z. B. jtx Board, OpenTasks, Erinnerungen am iPhone).

---

## 6. Notizen

![Notizen](img/de/notizen.png)

Notizen mit Farbe, Ordnern, Stern für *Wichtig*, Kästchen zum Abhaken, Listen, Codeblöcken und Anhängen.
Bilder aus der Zwischenablage oder per Ziehen landen direkt im Text, andere Dateien als Anhang. Links öffnen
mit Strg+Klick.

**Mit notizi:** dmOffice ist ein weiteres Gerät für die Notizen aus **notizi**. Der Abgleich läuft über den
Ende-zu-Ende-verschlüsselten Abgleichordner von notizi, der im WebDAV von dmOffice liegt. Einrichten unter
*Einstellungen* → *Notizen*: E2EE-Passwort eintragen und die Datenbank wählen.

![notizi-Abgleich](img/de/einstellungen-notizi.png)

Tabellen und Bilder aus notizi werden angezeigt, aber nur am PC in notizi bearbeitet – sie sind in dmOffice
gesperrt. Ändert ein anderes Gerät eine Notiz, während sie in dmOffice offen ist, warnt dmOffice und bietet
*Als Kopie speichern* an.

Ohne notizi funktionieren die Notizen genauso, nur ohne Abgleich.

---

## 7. Dateien

![Dateien](img/de/dateien.png)

Eine eigene Dateiablage – im Browser und per WebDAV (Windows-Explorer, Finder, Dateimanager am Handy).
Hochladen per Knopf oder Ziehen, auch ganze Ordner. Unten steht, wie viel belegt ist; hat der Admin eine
Grenze gesetzt, zeigt ein Balken die Auslastung.

---

## 8. Handy, Tablet und Thunderbird verbinden

![Geräte & Sync](img/de/einstellungen-geraete.png)

Unter *Einstellungen* → *Konto* → *Geräte & Sync* für **jedes Gerät ein eigenes App-Passwort** erstellen. Das
Login-Passwort gilt für diese Programme bewusst nicht: Ein App-Passwort lässt sich einzeln widerrufen, wenn ein
Gerät verloren geht.

| Programm | So geht's |
|---|---|
| **Android (DAVx⁵)** | *Mit URL und Benutzername anmelden*, URL `https://<host>/dav/`, Anmeldename, App-Passwort. |
| **iPhone/iPad** | *Einstellungen* → *Kalender* → *Accounts* → *Account hinzufügen* → *Andere* → *CalDAV-Account*; Server: nur der Hostname. Für Kontakte genauso mit *CardDAV-Account*. |
| **Thunderbird** | Neuer Kalender → *Im Netzwerk* → Adresse aus *Geräte & Sync*. Adressbuch: *CardDAV-Adressbuch hinzufügen*. |
| **Windows-Explorer** | *Netzlaufwerk verbinden* → `https://<host>/dav/files/<login>/` (nur über HTTPS). |
| **notizi** | *Einstellungen* → *Sync* → *WebDAV*, Adresse `https://<host>/dav/files/<login>/notizisync/`. |

---

## 9. Einstellungen

Die Einstellungen sind aufgebaut wie in gängigen Office-Programmen: links der Bereich, in der Mitte die Seite, rechts der Inhalt.
Oben links gibt es eine Suche über alle Seiten.

| Seite | Inhalt |
|---|---|
| **Sprache und Zeit** | Sprache der Oberfläche (Deutsch/English, gilt für das Konto auf allen Geräten), Datums- und Zeitformat, Zeitzone |
| **Darstellung** | Hell/dunkel, Akzentfarbe, Textgröße |
| **Benachrichtigungen** | Hinweise für neue Mails und Erinnerungen, auch als Desktop-Benachrichtigung |
| **Nachrichtenliste** | Abstand, Vorschautext, Absenderbilder |
| **Vorschaufenster** | Lesebereich rechts, unten oder aus; wann eine Mail als gelesen gilt |
| **Ordnerbereich** | Favoriten, Sortierung |
| **Verfassen und Antworten** | Schriftart und -größe, Standard *Antworten* oder *Allen antworten* |
| **Speicher & Statistik** | Größe der Ordner und der Datenbank, Datenbank verdichten |

![Sprache und Zeit](img/de/einstellungen-sprache.png)

![Speicher & Statistik](img/de/einstellungen-speicher.png)

---

## 10. Sicherheit

![Sicherheit](img/de/einstellungen-sicherheit.png)

- **Verschlüsselt:** Jeder Benutzer hat eine eigene, verschlüsselte Datenbank (SQLCipher) mit eigenem Schlüssel.
  Wer nur das Datenverzeichnis kopiert, kann nichts lesen.
- **Zwei-Faktor-Anmeldung (TOTP):** *Einstellungen* → *Sicherheit* → *Einrichten*, QR-Code mit einer
  Authenticator-App scannen (Aegis, 2FAS, Google/Microsoft Authenticator …). Die **Wiederherstellungscodes**
  ausdrucken oder im Passwortmanager ablegen – jeder gilt einmal.
- **Passwort ändern** meldet alle anderen Sitzungen ab.
- Nach mehreren falschen Versuchen wartet dmOffice, bevor es weitere Anmeldungen annimmt – je Adresse und je
  Anmeldename.

---

## 11. Verwaltung (Admin)

![Verwaltung](img/de/verwaltung.png)

Nur für Benutzer mit Verwaltungsrecht:

- Benutzer anlegen, Passwort setzen, Verwaltungsrecht geben oder nehmen, löschen (mit Bestätigung durch den
  Anmeldenamen).
- **2FA zurücksetzen**, wenn jemand Handy und Wiederherstellungscodes verloren hat.
- **Speicher für Dateien:** eine Vorgabe für alle (0 = unbegrenzt) und je Benutzer ein eigener Wert (Klick auf
  die Spalte *Dateien*; leer lassen = Vorgabe). Die Grenze gilt für WebDAV, die Dateien-Ansicht und den
  notizi-Abgleich.
- Ein Hinweis erscheint, solange der Hauptschlüssel als Datei im Datenverzeichnis liegt statt in
  `DMOFFICE_MASTER_KEY`.

---

## 12. Sicherung und Umzug

- **Sichern:** das Datenverzeichnis (`/data`, bei Unraid `appdata/dmoffice`) **und** den Hauptschlüssel. Am
  sichersten bei gestopptem Container; im laufenden Betrieb vorher *Einstellungen* → *Speicher & Statistik* →
  *Datenbank verdichten* hilft, die Zwischendateien klein zu halten.
- **Umziehen:** Datenverzeichnis auf den neuen Host kopieren, Container mit demselben `DMOFFICE_MASTER_KEY` starten.
- **Von Nextcloud:** Kalender als `.ics`, Adressbücher als `.vcf` exportieren und in dmOffice importieren;
  notizi auf allen Geräten auf die neue WebDAV-Adresse umstellen.

---

## 13. Tastenkürzel

| Taste | Wirkung |
|---|---|
| Strg+N | Neue E-Mail |
| Strg+R / Strg+Umschalt+R | Antworten / Allen antworten |
| Strg+Umschalt+F | Weiterleiten |
| Strg+Q / Strg+U | Als gelesen / ungelesen markieren |
| Einfg | Kennzeichnen |
| Entf | Löschen |
| ↑ / ↓ | Vorige / nächste Mail |
| Strg+Enter | Senden (beim Verfassen) |
| Strg+S | Notiz sofort speichern |

---

## 14. Häufige Fragen

**Kopieren in die Zwischenablage oder Desktop-Benachrichtigungen gehen nicht.**
Browser erlauben das nur über HTTPS. dmOffice über den Reverse-Proxy mit Zertifikat aufrufen.

**Das Handy meldet „Anmeldung fehlgeschlagen“.**
Für Geräte gilt nur ein **App-Passwort**, nicht das Login-Passwort.

**Die Datenbank ist sehr groß.**
*Einstellungen* → *Speicher & Statistik* zeigt, welche Ordner wie viel belegen. Den Zeitraum der Synchronisation
je Konto verkürzen und danach *Datenbank verdichten*.

**Ich habe den Hauptschlüssel verloren.**
Dann lassen sich die Datenbanken nicht mehr öffnen. Mails liegen weiter auf dem Mailserver; Kalender, Kontakte,
Notizen und Dateien nur noch auf den verbundenen Geräten.
