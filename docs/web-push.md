# Web Push for the installed app (prepared, not yet active)

[Deutsch](web-push.de.md) · English

Today dmOffice announces new mail and event reminders only while a window is open (`web/src/shell/Notifier.tsx`
polls the status and shows desktop notifications). With Web Push the notifications would also arrive when the window
is closed – on Android, Windows, macOS, Linux and (from iOS 16.4) on the iPhone, where it works only for the app added
to the Home Screen.

Note: the **Android app** already receives push, but by a different route: `DmOffice.Server/Push.cs` sends a
content-free signal ("there is something new") through Firebase Cloud Messaging, and the app fetches the data itself.
It is set up with a service-account key as `data/keys/fcm.json` or through `DMOFFICE_FCM_CREDENTIALS`; without a key
the app polls every 15 minutes as before. This document is about Web Push for the **browser / installed web app**,
which does not exist yet.

## What is already there

- **Service worker** (`web/sw/sw.js`): the `push` and `notificationclick` events are done. It expects JSON
  `{ "title", "body", "url", "tag" }`; a click brings an open window to the front or opens `url` (e.g. `/#/mail`,
  `/#/calendar`). `tag` groups notifications (e.g. one reminder per event).
- **Manifest, icons, HTTPS check** as for any installation.

## What is missing

1. **VAPID keys** (P-256), generated on first start in `<data>/keys/vapid.json`
   (`ECDsa.Create(ECCurve.NamedCurves.nistP256)`, public key as an uncompressed point, Base64url).
   Query for the UI: `GET /api/push/key`.
2. **Subscriptions per user** in their DB: table
   `push_subscriptions (endpoint TEXT PRIMARY KEY, p256dh TEXT, auth TEXT, created TEXT, user_agent TEXT)`
   in `Core/UserDb.cs` (migration like the other tables). Endpoints `POST /api/push/subscribe` and
   `DELETE /api/push/subscribe` (body: `PushSubscription.toJSON()`).
3. **Sending** (`Core/WebPush.cs`, possible without a third-party package):
   - Encrypt the payload per RFC 8291 (`aes128gcm`): ECDH with `p256dh` (`ECDiffieHellman`), HKDF
     (`System.Security.Cryptography.HKDF`) with `auth`, then `AesGcm`, record with padding delimiter `0x02`.
   - VAPID header per RFC 8292: a JWT (ES256) with `aud` = origin of the endpoint, `exp` ≤ 24 h, `sub` = the admin's
     `mailto:` address; header `Authorization: vapid t=<jwt>, k=<public key>`, plus `TTL` and `Urgency`.
   - Response `404`/`410` → delete the subscription.
4. **Triggers on the server**:
   - *New mail*: `Mail/MailSync.cs` notices new UIDs when it syncs the inbox – there, per user,
     `WebPush.Send(user, { title: sender, body: subject, url: "/#/mail", tag: "mail-<account>" })`,
     only if `notifyMail` is on in the settings.
   - *Reminders*: a small `BackgroundService` that every minute reads each user's events for the next hour
     (`Pim/PimStore.cs`) and sends due reminders once (remember sent keys `id|start` in `kv`).
   - So that an open window does not notify twice: in the service worker, before `showNotification`, check with
     `clients.matchAll()` whether a visible window exists, and in that case only send a `postMessage`.
5. **UI**: under *Settings → Notifications* a switch "Also when the window is closed" that calls
   `registration.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey })`.
   On iOS offer it only in the installed app (`display-mode: standalone`).

Rough size: 300–400 lines of C#, 60 lines of TypeScript. It can be tested locally at `http://127.0.0.1` with
Chrome/Edge (push service from Google or Microsoft, so the server needs internet access).
