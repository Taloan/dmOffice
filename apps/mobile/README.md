# dmOffice for Android

[Deutsch](README.de.md) · **English**

Calendar, tasks, mail, contacts, notes and files from dmOffice on your phone, with home screen widgets. Native Android app: Kotlin, Jetpack Compose, Glance widgets, Room, WorkManager, OkHttp. It speaks CalDAV with the dmOffice server (`/dav/calendars/<login>/`), uses the server’s web API for the rest, and signs in with an app password.

## What the app does

- **Setup by QR code**: in dmOffice under Settings → Account → Devices & sync → “Pair phone app”. The code contains `dmoffice://pair?v=1&url=…&user=…&token=…&name=…`. Alternatively set it up by hand with address, username and app password. If the code is scanned with the camera app, dmOffice opens directly (deep link `dmoffice://pair`).
- **Agenda** (the next 90 days), **day**, **week**, **month**.
- **Events**: create and edit, all-day or timed, location, notes, reminder, simple recurrence (daily, weekly, monthly, yearly). Recurring series from the server are expanded (FREQ, INTERVAL, COUNT, UNTIL, BYDAY, BYMONTHDAY, BYMONTH, EXDATE, RECURRENCE-ID exceptions). Changes apply to the whole series.
- **Tasks** with due date (date, optional time), priority, notes and ticking off; quick entry at the top of the task list. Due tasks also show up in the agenda and calendar.
- **Date and time from text** (German and English): “Dentist tomorrow afternoon”, “Report next Wednesday quarter past 3”, “Christmas party 24.12. 2pm”, “Parcel in 3 days”, “Workshop Friday from 9 to 11”. Quick entry applies this directly (with a preview below the field); in the task and event editors a chip “Recognised: … – tap to apply” appears under the title, sets date/time and shortens the title. Hours 1–6 without a suffix count as afternoon (“at 3” = 15:00); times of day: early 8, morning 10, noon 12, afternoon 14, evening 19, night 22.
- **Widgets**: “dmOffice Agenda”, “dmOffice Tasks” (tick off directly in the widget) and “dmOffice Month”. Settings per widget, details in the “Widgets” section below.
- **Reminders** as local notifications (VALARM), with snooze and “Done” for tasks.
- **Sync**: `sync-collection` (RFC 6578) per calendar with tokens, ETags, `If-Match` on write, `If-None-Match: *` on create. Offline cache in Room; local changes are uploaded on the next sync. On `412` the server wins and your own version is kept as “(conflict copy)”. Background sync every 15 minutes plus immediately after each change.
- **Mail** through dmOffice’s web API (“Mail” tab): accounts and folders, inbox with cache (readable offline), reading in a WebView with HTML sanitised on the server (external images on request), opening attachments, reply, reply all, forward, sending through the server (the account’s SMTP, copy in Sent), flagging, unread, trash. Notifications for new mail can be set per account.
- **Contacts** (More → Contacts): list with search, card with call, SMS, mail, map; cached locally.
- **Notes** (More → Notes): list with search, reading; editing paragraph by paragraph with version check (locked blocks and notes with tables/images stay unchanged).
- **Files** (Files tab): browse, search, list or tiles, preview (pictures, PDF, text, video), open in other apps, rename, delete, create folders, upload, previous versions, share links (view, upload, upload only; expiry and password). **Available offline** keeps a copy of marked files or folders on the phone and keeps it in sync. Office documents open in the editor of your office server (see [docs/office.md](../../docs/office.md)).
- **Auto upload**: rules that upload new photos, videos or other files from a folder on the phone to a folder in your dmOffice files.
  - Sources: camera, screenshots, WhatsApp media, any media folder or a folder chosen by hand.
  - Target folder in dmOffice, optionally with subfolders by year, month or day, or keeping the source’s subfolders.
  - File types: photos and videos, photos only, videos only, or all files.
  - Same name but different file: keep both (adds a number), skip, or replace (the old one is kept as a previous version).
  - After uploading: keep on the device, or delete from the device (asks for confirmation each time; Android only allows deleting without asking with “All files access”).
  - When: Wi-Fi only, also when roaming, only while charging. Start date, or also upload existing files.
  - New photos and videos are detected within seconds, even when the app is closed; as a safety net it also checks every 15 minutes. The app remembers what it has uploaded. A file whose contents are already anywhere in dmOffice (same SHA-256, also from another device) is not transferred again. An upload queue with history shows progress; failed uploads can be retried.
- **Free up space**: lists photos and videos that the server has confirmed as backed up by checksum (`POST /api/files/hashes`) and that have not changed on the device since, and deletes them on request. Right before deleting, the app asks the server once more. By default the last 30 days stay on the device (0, 7, 30, 90 or 365 days to choose). In dmOffice the files stay; each device frees up only its own files.
- **Android calendar and contacts** (Options): optionally mirrors calendars and contacts into the Android databases (CalendarContract as a local account “dmOffice”, read-only; contacts as device contacts). Samsung Calendar, Phone, WhatsApp and so on then see them. One-way street dmOffice → Android; the copies are stored unencrypted there; can be turned off, and are deleted when turned off or when the account is removed.
- **Security**: local database encrypted with SQLCipher (key in Keystore-protected storage), optional app lock with fingerprint, face or device PIN. The server accepts the app password for the web API only for mail, calendar, contacts, notes and files, never for administration or sign-in settings.
- **Encrypted envelope** (in addition to TLS, server `E2e.cs`, app `api/E2e.kt`): when pairing, the app fetches the server’s public key (from the QR code, parameter `spk`, otherwise from `/api/e2e/key`), generates its own P-256 key pair and registers it with the app password (`/api/e2e/register`). ECDH and HKDF-SHA256 yield an AES-256-GCM key; from then on every request (web API and CalDAV) goes as ciphertext to `/api/e2e/{user}/{device}` and the reply comes back the same way. Proxy, network and TLS endpoint only see ciphertext; decryption happens inside dmOffice. A counter protects against replay; revoking the app password deletes the device key. A server without `/api/e2e/key` is served with TLS alone; the state is shown in Options under “Security”.
- **Updates**: the app checks GitHub for new releases at start and once a day (Options → “Check for updates”), shows what is new and installs the APK after verifying that it is signed with the same key. Android asks once to allow installing from this source. Developer builds are signed differently and do not update this way.
- Light and dark following the system (Material You), German and English following the system language.

## Building

Requirements: JDK 17 and the Android SDK (platform 36). Android Studio is not needed.

```bash
# JDK, e.g. on Windows
winget install Microsoft.OpenJDK.17

# set the SDK path (once), forward slashes also on Windows
echo "sdk.dir=C:/Users/<name>/AppData/Local/Android/Sdk" > apps/mobile/local.properties

cd apps/mobile
./gradlew :app:assembleDebug          # -> app/build/outputs/apk/debug/app-debug.apk
./gradlew :app:testDebugUnitTest      # parser, recurrence rules, DAV XML
```

On Windows use `gradlew.bat` instead of `./gradlew`; `JAVA_HOME` must point to JDK 17. The first build downloads Gradle dependencies and takes a few minutes.

The debug variant is called `at.dimes.dmoffice.debug` and can be installed next to a release version.

Release APKs are built and signed by `.github/workflows/android.yml` for every version tag (`v1.2.3`) and attached to the GitHub release as `dmOffice-1.2.3.apk` and `dmOffice.apk`. The signing key lives in repository secrets, never in the repository; always use the same key, because Android only installs an update with the same signature.

## Installing on the phone and testing against a local instance

```bash
adb install -r app/build/outputs/apk/debug/app-debug.apk

# run dmOffice locally on port 5099 and pass the port through to the phone
adb reverse tcp:5099 tcp:5099
```

Then choose “Set up by hand” in the app: address `http://127.0.0.1:5099`, username and an app password from the dmOffice settings. Plain HTTP is only allowed for `127.0.0.1`, `localhost` and `10.0.2.2` (emulator) (`res/xml/network_security_config.xml`); production needs HTTPS.

Pairing also works without typing, by intent:

```bash
adb shell am start -a android.intent.action.VIEW -d "'dmoffice://pair?v=1&url=http%3A%2F%2F127.0.0.1%3A5099&user=<login>&token=<app-password>&name=<name>'"
```

## Using the app

- **Agenda**: grouped by day, tick boxes for due tasks. Plus button: new event.
- **Calendar**: switch between day, week and month at the top, arrows page through, the calendar icon jumps to today. In the month view tap a day and its list appears below. The plus button creates the event on the selected day.
- **Tasks**: quick entry at the top (title only, goes into the first task calendar), groups overdue / today / later / no date. Completed ones can be shown in the options.
- **Files**: Tap a file to open it, long-press to select; the three-dot menu has rename, delete, versions, share and “Available offline”. Upload button: files, photos, new folder, new Office document.
- **Options**: account, sync now, last sync and errors, show and hide calendars, default reminder, auto upload and free up space, updates, remove account.
- **Widgets**: long-press the home screen → Widgets → dmOffice. All widgets can be resized. Tapping an entry opens it in the app; the tick in the tasks widget completes the task immediately and triggers a sync.

## Widgets

Three widgets, each with its own settings (cog in the title row or when adding), stored per widget instance in `data/WidgetPrefs.kt`:

- **dmOffice Agenda**: the next days as a list with day headers.
- **dmOffice Tasks**: open tasks with a tick to complete them.
- **dmOffice Month**: month grid, below it optionally the list of the selected day. Tapping a day switches the list, arrows page through the month, tapping the month name jumps back to today. Month and selected day are kept per widget.

| Area | Options |
|---|---|
| Content | Events and tasks / events only / tasks only; period in days; multi-day events (on every day, first and last day only, only on the current day); maximum number of entries; only tasks with a due date; completed tasks |
| Month grid | Agenda widget: “show month grid on top” (the list then starts at the selected day); Month widget: list of the selected day on/off; days with entries as dots or small bars in the calendar colours (up to three per day) |
| Appearance | Row layout compact (one line: time and title) / normal (title, below it time and place) / detailed (title, time, place, calendar name); calendar colour as bar, dot or none; time to the left of or below the title; day header relative (Today/Tomorrow/weekday) or always as a date; font size; background opacity; title row; place |

Multi-day events follow the same rule (`MultiDayRule`) in all widgets, including the month grid. The implementation is in `widget/Widgets.kt` (shared building blocks, agenda, tasks, grid) and `widget/MonthWidget.kt`.

## Push through Firebase (optional)

Without push the app asks for news every 15 minutes. With push, dmOffice sends a content-free signal through Google Firebase Cloud Messaging on every change (mail, events, tasks, contacts, notes), and the app syncs immediately. Only “there is news” travels through Google; the app fetches the data itself through dmOffice’s encrypted envelope.

One-time setup:

1. **Firebase project**: <https://console.firebase.google.com> → add project (Google Analytics is not needed).
2. **Android apps**: in the project create two Android apps with the package names `at.dimes.dmoffice` (release) and `at.dimes.dmoffice.debug` (debug). Then download `google-services.json` under Project settings → General; it contains both apps.
3. **For the app**: put `google-services.json` in `apps/mobile/app/` (it is in `.gitignore`) and rebuild the app. Without the file the app builds just the same, only without push.
4. **For the server**: Project settings → Service accounts → “Generate new private key”. Put the JSON file as `keys/fcm.json` into dmOffice’s data folder (on Unraid under appdata, next to `keys/master.key`) or give the path in the environment variable `DMOFFICE_FCM_CREDENTIALS`, then restart the container. The key is secret and does not belong in the repository.

The server log then no longer says “Push: kein FCM-Schluessel”. The app registers its token at start; it is tied to the app password and goes away when that is revoked.

Technique: `DmOffice.Server/Push.cs` checks every 10 seconds, for users with a signed-in phone, a fingerprint made of mail version, calendar sync tokens and notes state, and sends a signal at most every 15 seconds (FCM HTTP v1, high-priority data message). In the app `sync/PushService.kt` then starts the sync.

## Code layout

| Folder | Contents |
|---|---|
| `data/NaturalDate.kt` | Date/time from text (de/en), `data/WidgetPrefs.kt` settings per widget |
| `api/` | Web API client (app password, JSON), the encrypted envelope and data formats as in web/src/api.ts |
| `mail/` | Mail repository with cache; `data/ContactsRepo.kt`, `data/NotesRepo.kt` likewise |
| `files/` | File repository, offline copies, thumbnails, document provider, Office editor glue |
| `upload/` | Auto upload (scanner, queue, rules) and the logic for “free up space” |
| `update/` | Update check, download and signature verification |
| `sync/ProviderSync.kt` | Mirror into CalendarContract and ContactsContract |
| `sync/PushService.kt` | Push through Firebase: register token, signal starts the sync |
| `ical/` | iCalendar reader/writer (round trip of unknown properties) and RRULE expansion |
| `dav/` | CalDAV client (OkHttp) and a namespace-aware multistatus parser (XmlPullParser) |
| `data/` | Room database, account (encrypted), model/field derivation from ICS, repository |
| `sync/` | Sync engine (upload, sync-collection, conflicts) and WorkManager workers |
| `notify/` | Reminders via AlarmManager, mail notifications, notifications, boot receiver |
| `widget/` | Glance widgets for agenda, tasks and month, settings activity |
| `ui/` | Compose interface: setup, agenda, calendar, tasks, files, editors, options |

The multistatus parser compares namespace and local name, never the prefix. This makes the app independent of whether the server writes `d:`, `D:` or no prefix at all.

## Still open

- Recurring series can only be edited as a whole (no single-instance exceptions).
- No time-grid view for day and week; they are lists.
- No subtasks, no templates, no year view.
- Widgets: no week view, no time grid, no free colour choice for text and background.
- Mail: plain text only when writing.
- Contacts read-only; notes without formatting, attachments or folders.
- The Android mirror has no tasks and no single exceptions of series.
