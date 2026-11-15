# Catchall-Aliase

Deutsch · [English](catchall-aliase.md)

Jedem Shop, Verein oder Forum eine eigene E-Mail-Adresse geben, wie bei SimpleLogin – ohne Fremddienst. Dafür braucht es
eine eigene Domain, deren Hoster alle Adressen annimmt (Catch-all) und an ein Postfach weiterleitet, das dmOffice schon
abholt. dmOffice ist kein Mailserver: Es ordnet nur, was in diesem Postfach ankommt. Jede angelegte Adresse ist ein **Alias**.

## Einrichten

1. Beim Hoster der Domain ein **Catch-all** für die Domain (z. B. `example.com`) einrichten, das alles an dein Postfach
   (z. B. `ich@example.net`) weiterleitet. Ohne Catch-all kommen neue Aliase nicht an.
2. In dmOffice: *Einstellungen* → *E-Mail* → *Catchall-Aliase*. Domain eintragen (mehrere mit Komma getrennt) und das
   **Postfach wählen, in dem die Mails ankommen**.
3. Festlegen, was mit **unbekannten Adressen** passiert, also Mails an eine Adresse der Domain, die kein Alias ist:
   - *Annehmen, in den Ordner der Domain* (Vorgabe).
   - *Gleich in Junk*: Nur deine Aliase kommen durch.
4. Festlegen, was **Sperren** heißt: *In Junk verschieben* oder *Löschen (in den Papierkorb)*.

## Ordner

dmOffice legt die Mails in echte IMAP-Ordner des Postfachs, damit andere Mailprogramme dieselbe Ordnung sehen:

- je Domain ein Ordner mit ihrem Namen (`example.com`) – dort landen Mails an unbekannte Adressen;
- darunter je Alias ein Ordner (`example.com/shop`).

Unbekannte Adressen bekommen keinen eigenen Ordner, sonst legte Spam an erfundene Adressen Ordner an. Nutzt der Server `.`
als Trennzeichen, wird der Punkt im Namen zu `_` (`example_com`); `*` und `%` kommen nie vor.

In dmOffice stehen diese Ordner nicht in der normalen Ordnerliste des Postfachs, sondern in einem eigenen Abschnitt
**Catchall-Aliase** in der Mail-Seitenleiste (wie die Kategorien): je Domain ein Eintrag mit der Zahl der Mails an Adressen
ohne Alias, darunter je Alias einer mit Notiz in Klammern und Zähler, gesperrte mit Schloss; über der Mailliste steht die
Adresse des Alias mit Notiz bzw. die Domain. Rechtsklick (Web) oder langes Drücken (App) auf einen Alias: Sperren/Entsperren,
Notiz ändern, Adresse kopieren, Löschen.

Einsortiert wird beim Einstufen neuer Mails, nach Spamfilter und Regeln: Was der Spamfilter nach Junk schickt oder eine Regel
woandershin legt, bleibt dort.

## Neuer Alias

- In der Mail-Ansicht: *Catchall-Alias* im Menüband (Web) oder das **@**-Symbol oben in der Mailliste (Android-App),
  dort **+**.
- Namen selbst wählen oder den zufälligen nehmen (lesbar, z. B. `fuchs-7k2`: Wort, Bindestrich, Ziffer-Buchstabe-Ziffer,
  ohne leicht verwechselbare Zeichen). Erlaubt sind `a–z`, Ziffern, `.`, `-` und `_`.
- Eine Notiz **wofür** dazuschreiben („Gewinnspiel Baumarkt“). Nach dem Anlegen liegt der Alias in der Zwischenablage, sein
  Ordner entsteht im Postfach.

## Alias aus einer Mail anlegen

Eine Mail an eine Adresse ohne Alias zeigt in Liste und Lese-Ansicht „An unbekannte Adresse x@example.com · **Als Alias
anlegen**“; dasselbe steht im Kontextmenü der Mail (Web) und im Menü (App). Auch *Einstellungen* → *E-Mail* → *Catchall-Aliase*
listet diese Adressen. dmOffice fragt die Notiz ab (vorbelegt mit dem Absendernamen), legt Alias und Ordner an und verschiebt
**alle** Mails an diese Adresse aus dem Domain-Ordner und dem Posteingang dorthin.

## An welche Adresse eine Mail ging

Beim Abholen ordnet dmOffice jede Mail des Postfachs ihrer Adresse zu – auch nach einer Weiterleitung. Geprüft wird in dieser
Reihenfolge: `Delivered-To`, `X-Original-To`, `Envelope-To`, `X-Envelope-To`, `X-Forwarded-To`, dann `An` und `Cc`, zuletzt
`Received: … for <…>`. Die erste Adresse einer der Domains gilt. Hoster schreiben den ursprünglichen Empfänger in
unterschiedliche Kopfzeilen; die Umschlag-Kopfzeilen kommen zuerst, weil in `An` bei Verteilern die Liste und bei Bcc gar
nichts steht.

Die Mailliste und die Lese-Ansicht zeigen das klein, mit Knopf: „über shop-9a4@example.com · Sperren“.

## Übersicht je Alias

*Einstellungen* → *E-Mail* → *Catchall-Aliase* zeigt jeden Alias mit Notiz, wann er angelegt wurde, wie viele Mails kamen
und wann die letzte. Ein Klick zeigt die häufigsten Absender – so sieht man, wer einen Alias weitergegeben hat. Die
Android-App zeigt dieselbe Liste.

## Sperren

Ein Klick auf **Sperren** sperrt einen Alias, ein weiterer gibt ihn wieder frei – in den Einstellungen, an der Mail oder in
der Seitenleiste. Das Menü in den Einstellungen bietet unabhängig von der Vorgabe *Sperren – in Junk* und *Sperren – löschen*.

Gesperrt heißt: **Neue** Mails an diesen Alias gehen beim Eingang in Junk oder in den Papierkorb – vor allem anderen, auch bei
Absendern aus dem Adressbuch (der Alias ist ja gerade deshalb gesperrt, weil er weitergegeben wurde). „Warum?“ an so einer
Mail nennt den Grund („ging an den gesperrten Catchall-Alias …“). Der Ordner des Alias mit den älteren Mails bleibt und trägt
ein Schloss.

## Entfernen nach Zeit je Alias

Im Menü des Alias (Einstellungen, Android-App): *Entfernen nach Zeit …* – *In den Papierkorb nach …* oder *Vernichten nach …*
einer Zahl von Tagen. Das gilt für **neue** Mails an diesen Alias ab ihrem Eingang, mit demselben Entfernen nach Zeit wie an
einer einzelnen Mail: Die Mail trägt eine Sanduhr, und bis dahin lässt es sich dort zurücknehmen. Vernichten löscht auch auf
dem Mailserver, ohne Papierkorb; Kopien anderswo (andere Geräte, beim Absender, Sicherungen des Anbieters) erreicht dmOffice
nicht.

## Antworten mit dem Alias

Wahlweise, Vorgabe aus: *Antworten mit dem Alias als Absender*. Beim Antworten auf eine Mail, die über einen Alias kam, ist
dann dieser Alias der Absender; ein Schalter im Verfassen-Fenster nimmt das für die einzelne Mail zurück. Die Message-ID trägt
dann ebenfalls die Domain des Alias, nicht die des Postfachs.

Nur einschalten, wenn der **SMTP-Server des Postfachs diesen Absender erlaubt** und **SPF und DKIM der Domain für diesen
Server eingerichtet** sind. Sonst lehnt der Server die Mail ab oder sie landet beim Empfänger im Spam. dmOffice sendet über
den SMTP-Server des Postfachs und kann das selbst nicht prüfen.

## Hinweise

- Die Einstellungen gelten je Benutzer. Domain und Postfach stellt man in der Weboberfläche ein; die App kann anlegen,
  sperren, übernehmen und kopieren.
- *Löschen …* (im Kontextmenü des Alias in der Seitenleiste, in den Einstellungen und in der App) verschiebt die Mails im
  Ordner des Alias in den Ordner der Domain, löscht den Ordner des Alias auf dem Mailserver und dann den Alias samt
  Übersicht. Neue Mails an die Adresse landen im Ordner der Domain (oder in Junk, wie eingestellt). Klappt das Verschieben
  nicht, bleibt der Alias stehen.
- Für die Zuordnung merkt sich dmOffice je Mail nur Message-ID, Adresse, Absender und Zeit (Tabelle `mail_alias_hit` in der
  Benutzerdatenbank).
