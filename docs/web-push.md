# Web Push in the browser and the installed app (from 2.0)

[Deutsch](web-push.de.md) · English

From dmOffice 2.0, chat messages, new mail and event reminders arrive as notifications even when no dmOffice window is
open – on Android, Windows, macOS, Linux and (from iOS 16.4) on the iPhone, where it works only in the app added to the
Home Screen.

Turn it on under **Settings → Notifications → Even when the window is closed**. The subscription applies per browser;
"Test" sends a test notification. The "Email" and "Calendar" switches above also apply to Web Push; for chat, muting,
@mentions and quiet hours apply (Settings → Chat).

Requirements: dmOffice runs over **HTTPS** (reverse proxy with a certificate) and the server can reach the internet –
notifications go through the browser's push service (Google, Mozilla, Microsoft, Apple).

Note: the **Android app** receives push its own way: `DmOffice.Server/Push.cs` sends a content-free signal through
Firebase Cloud Messaging and the app fetches the data itself (key `data/keys/fcm.json` or `DMOFFICE_FCM_CREDENTIALS`).

## How it is built

- **Keys:** VAPID (P-256) is created on first start in `data/keys/vapid.json`; the private part is wrapped with the
  master key.
- **Subscriptions:** in a separate, encrypted `data/push.db` (key `data/keys/push.key`) – the user databases stay
  unchanged. Subscriptions the push service no longer knows (404/410) are dropped; deleting a user drops theirs too.
- **Sending** (`DmOffice.Server/Core/WebPush.cs`, no third-party package): payload encrypted per RFC 8291 (`aes128gcm`)
  – the push service sees neither sender nor text; sender identification per RFC 8292 (VAPID, ES256). Only known push
  services over HTTPS are contacted – the server never calls arbitrary addresses a browser hands it.
- **Triggers:** chat immediately (`Chat/ChatPush.cs`, together with the signal to the phone app); new unread mail in the
  inbox and due reminders every 30 seconds (`Core/WebPushWatcher.cs`).
- **Service worker** (`web/sw/sw.js`): shows the notification; if dmOffice is visibly open, the page notifies by itself
  and there is no second one (except in Safari, which requires a notification for every push). A click brings an open
  window to the front or opens the matching page.
- **API** (browser login only, not app passwords): `GET /api/push/web/key`, `POST /api/push/web/subscribe` (body:
  `PushSubscription.toJSON()`), `POST /api/push/web/unsubscribe`, `POST /api/push/web/test`.
