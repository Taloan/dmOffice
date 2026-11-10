# Nextcloud clients with dmOffice (feasibility trial)

[Deutsch](nextcloud-clients.de.md) · English

dmOffice speaks the interface the official Nextcloud clients expect. This lets you sync Files with the Nextcloud
desktop app (Windows, macOS, Linux) as a real local folder, without Syncthing and without a sync client of our own.
The data stays where it is: in the user's encrypted database (`FileStore`; the contents sit next to it as encrypted
blob files, `Core/BlobStore.cs`), the same storage as under `/dav/files/<login>/` and for notizi.

Code: `DmOffice.Server/Dav/NextcloudApi.cs` (detection, OCS, sign-in) and `DavServer.cs` (WebDAV under
`/remote.php`). Tests: `DmOffice.Tests/NextcloudClientTests.cs`.

## What is there

| Address | Purpose |
| --- | --- |
| `GET /status.php` | Detection: `installed`, `version` (reports 30.0.5), `productname` "dmOffice" |
| `GET /ocs/v1.php/cloud/capabilities`, `/ocs/v2.php/…` | Capabilities, readable without sign-in, only what is needed |
| `GET /ocs/v1.php/cloud/user`, `/ocs/v2.php/…` | User (`id` = login name, display name, storage) |
| `POST /index.php/login/v2`, `…/poll`, `…/flow/<token>` | Login Flow v2 (desktop): sign in in the browser, issues an app password |
| `GET /index.php/login/flow` | Login Flow v1 (older phone apps): redirect to `nc://login/…` |
| `/remote.php/dav/files/<login>/…`, `/remote.php/webdav/…` | WebDAV onto Files |

- Sign-in with an app password (Basic), as with `/dav`. The login password is not accepted.
- The flow's sign-in page accepts the web UI's cookie session or asks for login name, password and, if set, the
  two-factor code itself (with the same brake against guessing). The new app password is named after the client
  ("Nextcloud Desktop") and can be revoked in the settings.
- PROPFIND additionally returns `oc:id`, `oc:fileid`, `oc:permissions`, `oc:size` (for folders: everything below),
  `oc:owner-id`, `oc:share-types`, `nc:has-preview`, `nc:is-encrypted`. Thumbnails are served at
  `/index.php/core/preview` and `/index.php/apps/files/api/v1/thumbnail/…`.
- PUT, MKCOL, MOVE, COPY and GET answer with `OC-FileId` and `OC-ETag`; `X-OC-Mtime` is honored
  (`X-OC-MTime: accepted`), so the modification time arrives identically on all devices.
- Every file has a fixed number (schema 11: column `files.fileid`, counter `file_ids`). It survives moves and
  overwrites and is never reused. The client uses it to recognize moves.
- When something changes, all folders above it up to the root get a new ETag (`FileStore.Touch`). The client only
  descends into folders with a new ETag.
- Uploading in chunks the old way (`OC-Chunked`) is rejected with 501 instead of storing the chunks as files.

## Tested with the real client

`nextcloudcmd` 35.0.0 (command line, the same sync library as the desktop app) against a local test server at
`http://127.0.0.1`, with two local folders standing in for two devices ("PC" and "phone"):

- Sign-in: Login Flow v2 completely with curl, as the client does it (start, poll 404, grant, poll with app
  password). Then `capabilities` and `cloud/user` with the app password.
- Initial sync PC → server: folder, empty folder, umlauts and `&` in names, a 300 KB file. All correct.
- The second device downloads everything identically (checksums match, modification time preserved).
- Changing, renaming (as MOVE), deleting, a new file on the PC → all arrive on the second device.
- Moving a folder on the second device → arrives on the PC as a move, with no new download.
- Idle: only PROPFINDs, no transfer. On later runs the client only descends into folders with a new ETag.
- Conflict (same file changed on both devices): the usual "conflicted copy", nothing is lost.
- A file written through `/dav/files/…` (as notizi does) → arrives at the Nextcloud client.
- 25 MB in one PUT: works. Without a `chunking` capability, client 35 sends the whole file at once.
- `nextcloudcmd` accepts `http://` without asking.

**Not tested by us:** the desktop app's setup wizard (the app was already running on the machine with an account,
and a second instance would only have talked to the running one), and the Android and iOS apps.

### Test it yourself (desktop app)

1. Start dmOffice with this version (locally or behind the proxy; in that case pass `/status.php`, `/ocs/`,
   `/index.php/` and `/remote.php/` through and set the `X-Forwarded-Proto` and `X-Forwarded-Host` headers).
2. In the Nextcloud app: add account → server address `https://office.example.com` (locally `http://localhost:5000`
   works too; the app then warns about the unencrypted connection).
3. The browser opens dmOffice's "Allow access?" page → sign in or confirm.
4. In the app choose a local folder, optionally sync only selected folders. Virtual files work too (placeholders
   from PROPFIND, download on demand through GET) but are untested.

## Open points for a full build-out

**Storage:** File contents no longer sit as BLOBs in the database; they are individual encrypted files next to the
user database (64 KB blocks, AES-GCM, a key of its own per file; `Core/BlobStore.cs`), read and written as a stream.
What remains is the limit of 512 MB per request (Kestrel). Deduplicating independent uploads is deliberately not
built in (see `duplikate.md`).

**Chunked upload (not in the trial):** Chunking v2 under `/remote.php/dav/uploads/<login>/<transfer-id>/`: MKCOL for
the transfer, PUT per chunk (`OC-Total-Length`, `Destination`), finally `MOVE …/.file` onto the target; store chunks
encrypted in the meantime and clean up after expiry; capability `dav.chunking: "1.0"`. Only this makes it possible to
resume interrupted large uploads. Optionally `dav.bulkupload` for many small files.

**Shares (not in the trial):** OCS sharing API (`/ocs/v2.php/apps/files_sharing/api/v1/shares`), public links
(`/s/<token>`), shares to other dmOffice users (mounted as a folder with `oc:permissions` "S"/"M" and rights of their
own), notifying the other side through ETags.

**End-to-end encryption (not in the trial):** the `end_to_end_encryption` API (the user's public/private keys,
metadata per folder, locking a folder with `e2e-token` during upload), capability `end-to-end-encryption`. The
server then stores only what the client sends encrypted; the web UI and search no longer see such folders.

**Further points:**

- Trash and versions: deleting through a client moves the file to dmOffice's trash, from where it can be restored in
  the web UI. The client-side trash (`/remote.php/dav/trashbin/<login>/`, capability `files.undelete`) and the version
  list (`versioning`) are still missing.
- Checksums: dmOffice stores a SHA-256 per file (see `duplikate.md`). Still open: evaluate `OC-Checksum` on upload,
  return it as `oc:checksums` and in the GET header, and report the `checksums` capability. The client would then
  verify every download.
- Instant notification (`notify_push`); today the client polls the root about every 30 seconds.
- Avatars, notifications, activity, user status and search (WebDAV `SEARCH`, needed for the phone apps' photo view)
  answer 404. The desktop client does without them; whether the phone apps manage all the important things without
  them is unverified.
- Android and iOS: Login Flow v1 and v2 are there, but not tested on devices.
- Diarium: on the PC the Nextcloud desktop app syncs the folder. On Android, Diarium's built-in WebDAV sync is the
  preferred way. There Diarium talks directly to the dmOffice storage (`https://<server>/dav/files/<login>/<folder>/`
  or `https://<server>/remote.php/webdav/<folder>/`, login name and app password). The Nextcloud app is not needed
  on the phone for this. PC and phone see the same files because both addresses point at the same storage. Still to
  test: Diarium on the phone against dmOffice, and Diarium on the phone together with the desktop app on the PC on
  the same folder.
- Reported version 30.0.5: new enough for current clients; later clients may warn about old versions. The number
  should move along with the Nextcloud versions.
- Running sign-ins (Login Flow) live in memory only; after a restart you have to start over in the client.
- The "Allow access?" page is a plain HTML page of its own, not part of the web UI.
- An app password from the flow also applies to calendars and contacts, like any other app password.
- Manual: add a section "Syncing files with the Nextcloud app" and notes for the reverse proxy.
