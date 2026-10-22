# notizi mit dmOffice verbinden (QR-Code / Link)

Deutsch · [English](notizi-verbinden.md)

dmOffice legt für notizi einen eigenen Zugang an: ein App-Passwort mit Bereich `notizi`, das **nur den
Abgleichordner** sieht. In dmOffice: *Einstellungen → Notizen → notizi → „notizi verbinden“*. Danach zeigt dmOffice
einen QR-Code, einen Knopf „Link kopieren“ und die drei Werte zum Abschreiben.

## Link

```
notizi://webdav?url=<Adresse>&user=<Anmeldename>&password=<App-Passwort>
```

- UTF-8, alle Werte URL-kodiert.
- `url` ist schon die WebDAV-Wurzel für notizi, z. B. `https://office.example.com/dav/files/<login>/notizisync/`
  (mit abschließendem Schrägstrich).
- `user` ist der dmOffice-Anmeldename, `password` das App-Passwort. Es wird in dmOffice nur einmal angezeigt.

## Was notizi dafür braucht

1. Im Sync-Dialog (*Einstellungen → Sync → Verbindung → WebDAV*) „QR-Code/Link einlesen“: Link einfügen, QR aus
   einem Bild oder der Zwischenablage; auf Android später per Kamera. Die drei Felder werden daraus gefüllt, das
   E2EE-Passwort bleibt eine eigene Eingabe.
2. Optional `notizi://` als URL-Schema registrieren, damit ein Klick auf den Link den Dialog öffnet.

## Verhalten auf dem Server

- Mit dem notizi-Zugang gibt es außerhalb des Ordners `403`, etwa bei einem PROPFIND auf `/dav/files/<login>/`
  oder auf Kalender und Adressbücher. Auch die Web-API ist gesperrt. notizi darf also nicht oberhalb seiner Wurzel
  suchen oder anlegen.
- Sobald sich ein solcher Zugang zum ersten Mal angemeldet hat, sehen **alle anderen** App-Passwörter den Ordner über
  WebDAV nicht mehr (`404`): Explorer, DAVx⁵, auch ein älteres App-Passwort, mit dem notizi bisher verbunden war. Erst
  das Anzeigen des QR-Codes sperrt also noch nichts aus. Danach notizi gleich umstellen und das alte App-Passwort unter
  *Geräte & Sync* widerrufen.
- Jedes „notizi verbinden“ legt einen weiteren Zugang an (einen je Gerät); bereits ausgegebene bleiben gültig und
  lassen sich unter *Geräte & Sync* einzeln widerrufen.
- Nextcloud-Clients (`/remote.php/…`) sehen den Ordner nie. In der Dateiablage der Weboberfläche ist er ebenfalls
  unsichtbar und gesperrt. Die Notizen in dmOffice (Web und App) lesen und schreiben ihn weiterhin.
