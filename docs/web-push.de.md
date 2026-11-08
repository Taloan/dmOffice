# Web-Push im Browser und in der installierten App (ab 2.0)

Deutsch · [English](web-push.md)

Ab dmOffice 2.0 kommen Chat-Nachrichten, neue Mails und Terminerinnerungen als Benachrichtigung, auch wenn kein
dmOffice-Fenster offen ist – auf Android, Windows, macOS, Linux und (ab iOS 16.4) auf dem iPhone, dort nur in der zum
Home-Bildschirm hinzugefügten App.

Einschalten: **Einstellungen → Benachrichtigungen → Auch bei geschlossenem Fenster**. Das Abo gilt je Browser; „Probe“
schickt eine Test-Benachrichtigung. Die Schalter „E-Mail“ und „Kalender“ darüber gelten auch für Web-Push; für den Chat
gelten Stummschalten, @Erwähnungen und die Ruhezeiten (Einstellungen → Chat).

Voraussetzung: dmOffice läuft über **HTTPS** (Reverse-Proxy mit Zertifikat) und der Server erreicht das Internet – die
Benachrichtigungen gehen über den Push-Dienst des Browsers (Google, Mozilla, Microsoft, Apple).

Hinweis: Die **Android-App** bekommt Push auf eigenem Weg: `DmOffice.Server/Push.cs` schickt über Firebase Cloud
Messaging ein inhaltsloses Signal, die App holt die Daten selbst (Schlüssel `data/keys/fcm.json` oder
`DMOFFICE_FCM_CREDENTIALS`).

## Wie es gebaut ist

- **Schlüssel:** VAPID (P-256) entsteht beim ersten Start in `data/keys/vapid.json`; der private Teil ist mit dem
  Hauptschlüssel eingepackt.
- **Abos:** in einer eigenen, verschlüsselten `data/push.db` (Schlüssel `data/keys/push.key`) – die
  Benutzerdatenbanken bleiben unverändert. Abos, die der Push-Dienst nicht mehr kennt (404/410), fallen weg; wird ein
  Benutzer gelöscht, auch seine Abos.
- **Senden** (`DmOffice.Server/Core/WebPush.cs`, ohne fremdes Paket): Inhalt verschlüsselt nach RFC 8291 (`aes128gcm`)
  – der Push-Dienst sieht weder Absender noch Text; Absenderkennung nach RFC 8292 (VAPID, ES256). Gesendet wird nur an
  bekannte Push-Dienste über HTTPS – der Server ruft keine beliebigen Adressen auf, die ein Browser ihm nennt.
- **Auslöser:** Chat sofort (`Chat/ChatPush.cs`, zusammen mit dem Signal an die Handy-App); neue ungelesene Mail im
  Posteingang und fällige Erinnerungen alle 30 Sekunden (`Core/WebPushWatcher.cs`).
- **Service Worker** (`web/sw/sw.js`): zeigt die Benachrichtigung; ist dmOffice gerade sichtbar offen, meldet sich die
  Seite selbst und es gibt keine zweite (außer in Safari, das zu jedem Push eine Benachrichtigung verlangt). Ein Klick
  holt ein offenes Fenster nach vorn oder öffnet die passende Seite.
- **Schnittstelle** (nur mit Anmeldung im Browser, nicht mit App-Passwort): `GET /api/push/web/key`,
  `POST /api/push/web/subscribe` (Body: `PushSubscription.toJSON()`), `POST /api/push/web/unsubscribe`,
  `POST /api/push/web/test`.
