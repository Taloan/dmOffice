# Web-Push für die installierte App (vorbereitet, noch nicht aktiv)

Deutsch · [English](web-push.md)

Heute meldet dmOffice neue Mails und Terminerinnerungen nur, solange ein Fenster offen ist
(`web/src/shell/Notifier.tsx` fragt den Status ab und zeigt Desktop-Benachrichtigungen).
Mit Web-Push kämen die Hinweise auch bei geschlossenem Fenster – auf Android, Windows, macOS, Linux und
(ab iOS 16.4) auf dem iPhone, dort aber nur für die zum Home-Bildschirm hinzugefügte App.

Hinweis: Die **Android-App** bekommt bereits Push, aber auf anderem Weg: `DmOffice.Server/Push.cs` schickt über Firebase
Cloud Messaging ein inhaltsloses Signal („es gibt Neues“), die App holt die Daten selbst. Eingerichtet wird das mit einem
Dienstkonto-Schlüssel als `data/keys/fcm.json` oder über `DMOFFICE_FCM_CREDENTIALS`; ohne Schlüssel fragt die App wie
bisher alle 15 Minuten ab. Hier geht es um Web-Push für den **Browser/die installierte Web-App**, den es noch nicht gibt.

## Was schon da ist

- **Service Worker** (`web/sw/sw.js`): Ereignisse `push` und `notificationclick` sind fertig.
  Erwartet wird ein JSON `{ "title", "body", "url", "tag" }`; ein Klick holt ein offenes Fenster nach vorn
  oder öffnet `url` (z. B. `/#/mail`, `/#/calendar`). `tag` fasst Hinweise zusammen (z. B. eine Erinnerung je Termin).
- **Manifest, Symbole, HTTPS-Prüfung** wie für jede Installation.

## Was fehlt

1. **VAPID-Schlüssel** (P-256) beim ersten Start in `<daten>/keys/vapid.json` erzeugen
   (`ECDsa.Create(ECCurve.NamedCurves.nistP256)`, öffentlicher Schlüssel als unkomprimierter Punkt, Base64url).
   Abfrage für die Oberfläche: `GET /api/push/key`.
2. **Abos je Benutzer** in dessen DB: Tabelle
   `push_subscriptions (endpoint TEXT PRIMARY KEY, p256dh TEXT, auth TEXT, created TEXT, user_agent TEXT)`
   in `Core/UserDb.cs` (Migration wie bei den anderen Tabellen). Endpunkte `POST /api/push/subscribe` und
   `DELETE /api/push/subscribe` (Body: `PushSubscription.toJSON()`).
3. **Senden** (`Core/WebPush.cs`, ohne Fremdpaket möglich):
   - Nutzlast nach RFC 8291 verschlüsseln (`aes128gcm`): ECDH mit `p256dh` (`ECDiffieHellman`),
     HKDF (`System.Security.Cryptography.HKDF`) mit `auth`, dann `AesGcm`, Datensatz mit Padding-Trenner `0x02`.
   - VAPID-Kopf nach RFC 8292: JWT (ES256) mit `aud` = Ursprung des Endpunkts, `exp` ≤ 24 h, `sub` = `mailto:`-Adresse
     des Admins; Kopf `Authorization: vapid t=<jwt>, k=<öffentlicher Schlüssel>`, dazu `TTL` und `Urgency`.
   - Antwort `404`/`410` → Abo löschen.
4. **Auslöser auf dem Server**:
   - *Neue Mail*: `Mail/MailSync.cs` merkt beim Abgleich des Posteingangs neue UIDs – dort je Benutzer
     `WebPush.Send(user, { title: Absender, body: Betreff, url: "/#/mail", tag: "mail-<konto>" })`,
     nur wenn in den Einstellungen `notifyMail` an ist.
   - *Erinnerungen*: ein kleiner `BackgroundService`, der jede Minute je Benutzer die Termine der nächsten Stunde
     liest (`Pim/PimStore.cs`) und fällige Erinnerungen einmal schickt (gesendete Schlüssel `id|start` im `kv` merken).
   - Damit ein offenes Fenster nicht doppelt meldet: im Service Worker vor `showNotification` mit
     `clients.matchAll()` prüfen, ob ein sichtbares Fenster existiert, und dann nur `postMessage` schicken.
5. **Oberfläche**: unter *Einstellungen → Benachrichtigungen* ein Schalter „Auch bei geschlossenem Fenster“,
   der `registration.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey })` aufruft.
   Auf iOS nur in der installierten App anbieten (`display-mode: standalone`).

Umfang grob: 300–400 Zeilen C#, 60 Zeilen TypeScript. Testen lässt sich das lokal unter `http://127.0.0.1`
mit Chrome/Edge (Push-Dienst von Google bzw. Microsoft, daher braucht der Server Zugang ins Internet).
