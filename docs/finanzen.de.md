# Finanzen

Deutsch · [English](finanzen.md)

Finanzen ist für Kleinunternehmer nach § 19 UStG gedacht, die ihren Gewinn mit einer Einnahmenüberschussrechnung ermitteln. Der Bereich erfasst Einnahmen und Ausgaben, schreibt Angebote und Rechnungen und verknüpft Belege aus dem DMS. Er ist keine Finanzbuchhaltung und ersetzt keine steuerliche Beratung.

Finanzen ist in dieser Version nicht freigeschaltet. Auf einem Testserver lässt es sich mit `DMOFFICE_FEATURES=+finance` einschalten, danach über die Verwaltung unter Module.

## Einnahmen und Ausgaben

Ein Eintrag zählt zu dem Tag, an dem das Geld geflossen ist. Zu jedem Eintrag lässt sich ein Beleg aus dem DMS wählen, auch aus dem Tresor. Das Dokument wird dabei fixiert. Betrag, Datum, Titel und Korrespondent des Dokuments kommen in die Felder, die noch leer sind; das Datum nur, solange es nicht geändert wurde. Für einen Beleg im Tresor muss der Tresor beim Verknüpfen und beim Öffnen offen sein. Der Eintrag selbst und die Jahresübersicht bleiben auch bei geschlossenem Tresor nutzbar.

Ein Eintrag lässt sich ändern oder löschen, solange sein Jahr offen ist; das Protokoll hält das fest. **Korrigieren** legt einen Gegeneintrag mit dem umgekehrten Betrag an. Einträge, die aus einer Zahlung entstanden sind, lassen sich nur so korrigieren.

**Jahr abschließen** geht unter Übersicht für ein vergangenes Jahr und lässt sich nicht rückgängig machen. Danach sind nur noch Gegeneinträge in einem offenen Jahr möglich.

## Angebote, Aufträge und Rechnungen

Ein Beleg beginnt als Entwurf und bekommt erst beim **Ausstellen** seine Nummer. Entwürfe verbrauchen keine Nummer. Nach dem Ausstellen lässt er sich nicht mehr ändern oder löschen, auch die Datenbank lehnt das ab. Das PDF bleibt in Finanzen, eine fixierte Kopie kommt nach dem Standardprofil ins DMS. Zeigt das Profil auf den geschlossenen Tresor, erscheint die Kopie beim nächsten Öffnen dort, aber noch nicht fixiert.

- **Angebot oder Auftrag ändern:** Neue Version erzeugt einen Entwurf aus dem Beleg.
- **Auftrag aus einem Angebot:** Auftrag daraus erzeugt einen Entwurf mit denselben Positionen, der sich überarbeiten lässt. Ausgestellt wird der Auftrag als Auftragsbestätigung mit eigener Nummer.
- **Rechnung aus einem Angebot oder Auftrag:** Rechnung daraus oder unter Rechnungen „Aus Angebot oder Auftrag“ erzeugt einen Entwurf mit denselben Positionen.
- **Abschlagsrechnung:** beim ausgestellten Auftrag, als Prozent der Auftragssumme oder fester Betrag. Alle Abschläge zusammen dürfen die Auftragssumme nicht übersteigen.
- **Schlussrechnung:** enthält die Positionen des Auftrags und zieht jede ausgestellte Abschlagsrechnung mit Nummer und Datum ab. Offene Entwürfe von Abschlagsrechnungen müssen vorher ausgestellt oder gelöscht sein. Danach gibt es keine weiteren Abschläge zu diesem Auftrag; ein abgezogener Abschlag lässt sich erst stornieren, wenn die Schlussrechnung storniert ist.
- **Leistungen:** unter Leistungen vorab erfasst, im Beleg mit „Leistung hinzufügen“ übernommen und dort anpassbar. Spätere Änderungen an einer Leistung berühren vorhandene Belege nicht.
- **Zahlung erfassen:** legt den Eintrag bei den Einnahmen an. Mehrere Zahlungen zu einer Rechnung sind möglich.
- **Stornieren:** erzeugt eine Stornorechnung mit eigener Nummer. Die Rechnung bleibt erhalten. Ist eine Zahlung erfasst, muss zuerst dieser Eintrag korrigiert werden.

Auf jedem Beleg steht der Hinweis auf die Befreiung nach § 19 UStG. Die Texte sind eine Vorlage und keine Rechtsberatung. Belege gibt es nur auf Deutsch.

## Vorlagen

Unter Vorlagen lässt sich das Aussehen der Belege selbst gestalten. Eine Vorlage besteht aus Kopfzeile und Fußzeile, die auf jeder Seite stehen, und dem Inhalt. Bausteine sind Text, Bild, Empfänger, Angabenliste, Positionen, Gesamtbetrag, Hinweis nach § 19 UStG, Linie, Abstand, Seitenumbruch und bis zu vier Spalten nebeneinander. Die Reihenfolge lässt sich per Ziehen oder mit den Pfeilen ändern. Die Vorschau zeigt das Ergebnis genauso, wie es im PDF umbricht, auch über mehrere Seiten.

In Texten stehen Platzhalter in geschweiften Klammern, etwa `{firma}`, `{nummer}` oder `{seite}`. Text in eckigen Klammern entfällt, wenn ein Platzhalter darin leer ist: `[USt-IdNr.: {ust_id}]`.

Empfänger, Positionen mit den Spalten Leistung, Menge und Betrag, Gesamtbetrag und der Hinweis nach § 19 UStG sind Pflicht, ohne sie lässt sich eine Vorlage nicht speichern. Diese Prüfung betrifft nur den Aufbau: Dass ein gestalteter Beleg alle gesetzlichen Angaben trägt, bleibt deine Verantwortung, zum Beispiel Name, Anschrift und Steuernummer im eigenen Text.

Änderungen an einer Vorlage gelten für neue Belege. Ein ausgestellter Beleg behält sein PDF.

## Kunden

Ein Kunde hat einen Namen, eine Anschrift und eine Kundennummer, die dmOffice auf Wunsch selbst vergibt. Die Kundennummer steht fest im Beleg. Änderungen am Kunden gelten nur für neue Belege.

## Nummern

Muster und nächste Nummer stehen unter Einstellungen. Das Jahr im Muster kommt aus dem Belegdatum, jedes Jahr beginnt bei 1. Beim Umstieg aus einem anderen Programm lässt sich die nächste Nummer setzen. Kleiner werden darf sie nicht.

Nach dem Einspielen eines Core-Backups lässt sich nichts ausstellen, bis die nächsten Nummern bestätigt sind. Ein älterer Stand kennt später vergebene Nummern nicht. Bitte die Nummern vorher mit den letzten Belegen vergleichen, zum Beispiel im DMS oder in der Datensicherung.

## Fixiert im DMS

Ein fixiertes Dokument trägt einen blauen Rahmen, im Tresor außen zusätzlich den grünen. Sein Inhalt lässt sich nicht ändern, das Dokument nicht löschen. Das gilt auch für Ordner darüber und für WebDAV, Nextcloud-Clients und Office. Angaben wie Titel und Tags bleiben änderbar, Verschieben und Umbenennen bleiben möglich.

Fixieren geht von Hand oder automatisch beim Verknüpfen in Finanzen. Die Fixierung bleibt, bis sie bewusst aufgehoben wird. Solange Finanzen auf das Dokument verweist, lässt sie sich nicht aufheben.

Grenzen:

- **Externe Ordner:** Die Sperre gilt nur innerhalb von dmOffice. Außerhalb lassen sich die Dateien ändern.
- **Tresor:** Die Sperre gilt nur bei offenem Tresor und innerhalb von dmOffice. Die verschlüsselten Dateien lassen sich außerhalb von dmOffice verändern.
- **Konto löschen:** Mit dem Konto werden auch fixierte Dokumente und ausgestellte Belege gelöscht.
- **Tresor löschen:** Auch fixierte Dokumente im Tresor werden gelöscht. Verweise aus Finanzen führen danach ins Leere.

Fixiert ist kein Versprechen einer revisionssicheren Ablage.

## Sichern und Exportieren

- **Core-Backup:** enthält Finanzen vollständig.
- **Datenexport:** legt unter `Finanzen/` die ausgestellten Belege als PDF ab, dazu Einträge je Jahr, Belegliste, Kunden und Protokoll als CSV.
- **Übersicht:** Jahresübersicht als PDF und Einträge als CSV. Die Jahresübersicht ist keine amtliche Anlage EÜR.

## Noch nicht enthalten

Zuordnung zur Anlage EÜR, Export für WISO Steuer, Anlagegüter und andere Sonderfälle, E-Rechnung, App.
