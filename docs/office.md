# Office in the browser (Collabora, ONLYOFFICE)

[Deutsch](office.de.md) · English

dmOffice can edit Word, Excel and PowerPoint files (DOCX, XLSX, PPTX, plus ODT, ODS, ODP, RTF, CSV) from Files
directly in the browser. This needs a separate office server. There are two ways to connect one:

- **WOPI** (Collabora Online/CODE, or ONLYOFFICE with WOPI switched on): dmOffice acts as the WOPI host, and the
  office server fetches and saves the file through `/wopi/files/…`. Recommended: **Collabora CODE**, which also
  works on phones.
- **ONLYOFFICE Docs API** (api.js + callback, with JWT): for an existing ONLYOFFICE Document Server, with no changes
  to its container. dmOffice detects this on its own: if the address has no WOPI discovery but does serve
  `web-apps/apps/api/documents/api.js`, it uses this route.

Code: `DmOffice.Server/Office/` (discovery, tokens, WOPI endpoints, `OnlyOffice.cs`, empty templates), UI in
`web/src/files/Office.tsx`. Tests: `DmOffice.Tests/WopiTests.cs` (a fake Collabora with proof key) and
`OnlyOfficeTests.cs` (a fake Document Server with JWT).

## Which office servers exist (as of 20 Oct 2026)

| | Collabora Online / CODE | ONLYOFFICE Docs Community | Euro-Office |
| --- | --- | --- | --- |
| In Nextcloud | "Nextcloud Office" (app richdocuments) | app "ONLYOFFICE" | Nextcloud app "Euro-Office" (from Nextcloud 33); selectable under "Office Suite" in AIO |
| Origin | Collabora, based on LibreOffice | Ascensio System (ONLYOFFICE) | Fork of ONLYOFFICE (March 2026) by Nextcloud, IONOS, Proton and others; first stable release 9 June 2026 |
| License | MPL 2.0 (CODE) | AGPL v3 (with additional terms on logo/attribution) | AGPL v3 (the additional terms were removed – ONLYOFFICE disputes this with the projects) |
| Editing in a phone browser | yes, dedicated phone UI | **no** – "Mobile web editors" are an Enterprise Edition feature; the Community Edition opens documents view-only on phones (no change found for 9.x) | not documented; based on the ONLYOFFICE code |
| Limits | none by default; the old 10 documents / 20 connections now apply only in "Home Mode". Collabora does not recommend CODE for businesses (no support, rolling development version) | up to 9.3: 20 simultaneous connections; **from 9.4 (May 2026) the limit is gone** | none known |
| Docker image | `collabora/code` (Unraid: search the Community Apps for "Collabora", otherwise add it by hand) | `onlyoffice/documentserver` | `nextcloud/aio-eurooffice` (AIO), the project's own image |
| WOPI | yes, it is Collabora's native interface (with proof keys) | yes, since 6.4, **off** by default (`wopi.enable` in `local.json`, probably `WOPI_ENABLED=true` in the container); Nextcloud otherwise uses the Docs API | Nextcloud speaks the ONLYOFFICE Docs API; WOPI probably as with ONLYOFFICE, not checked |

Conclusion for dmOffice: **Collabora CODE** – free, no limit, edits on phones, speaks WOPI out of the box. An
ONLYOFFICE server that is already running works too (Docs API with JWT, see below), but as far as we could find, the
free edition does not edit in a phone browser.

Sources (retrieved 20 Oct 2026):
- Collabora forum, "Does Collabora CODE 26.04.2.4 have any user or concurrent connection limits": https://forum.collaboraonline.com/t/does-collabora-code-26-04-2-4-have-any-user-or-concurrent-connection-limits/5023
- Collabora FAQ (CODE not for business use): https://www.collaboraonline.com/faqs/
- Linuxiac, 20 May 2026, "ONLYOFFICE Docs 9.4 Removes Community Connection Limit": https://linuxiac.com/onlyoffice-docs-9-4-removes-community-connection-limit/
- ONLYOFFICE, editions compared: https://www.onlyoffice.com/compare-editions; forum "Mobile web docs editing …": https://community.onlyoffice.com/t/mobile-web-docs-editing-stopped-working-with-docs-home-server-license/11352
- ONLYOFFICE API, WOPI: https://api.onlyoffice.com/docs/docs-api/using-wopi/overview/ and https://api.onlyoffice.com/docs/Docs%20API/Using%20WOPI/WOPI%20discovery
- Euro-Office: https://en.wikipedia.org/wiki/Euro-Office; gHacks, 6 Apr 2026: https://www.ghacks.net/2026/04/06/nextcloud-ionos-and-proton-launch-euro-office-an-onlyoffice-fork-for-european-users/; heise on the license dispute: https://www.heise.de/en/news/Euro-Office-OnlyOffice-accuses-of-license-violations-11241334.html
- Nextcloud AIO with Euro-Office: https://github.com/nextcloud/all-in-one, https://hub.docker.com/r/nextcloud/aio-eurooffice, https://blog.wirelessmoves.com/2026/08/moving-to-euro-office-part-1-nextcloud-aio.html

## How it works in dmOffice

- **Admin → Office in the browser**: enter the office server's address and choose "Check and save". dmOffice reads
  `<address>/hosting/discovery` and says clearly what is wrong (unreachable, certificate, redirect, no WOPI).
  Empty = off; without an office server, Files behaves as before.
- **Files**: office files open in the editor on double-click (or tap). The context menu has "Edit in browser", and
  "New → Word document / Excel workbook / PowerPoint presentation" creates an empty file and opens it. The editor
  fills the page; "Back" (top left, Collabora's close button, or the phone's back button) returns to Files. CSV still
  opens in the text editor; the context menu offers Collabora for it.
- **Direct link**: `https://<dmOffice>/#/files/:office/<path>` opens the document in the editor.
- **Phone app**: opens office files in the editor itself, see [below](#phone-app).
- **Public links**: for "view only" links, office files are shown in the office server's viewer (counted as one
  download). Editing through links is not possible.
- **Saving** goes through Files: previous versions, trash, full-text search, thumbnails and the storage limit apply
  as usual. The notizi sync folder stays locked (also for tokens that were already issued).

### Security

- `/wopi/files/{id}` knows no sign-in cookie, only the `access_token`: signed and encrypted with Data Protection,
  per user and file, valid for 10 hours (Collabora does not renew it during a session). A new password or "sign
  out everywhere" invalidates it immediately; for public links it ends with the link.
- The token is passed to the iframe by POST, so it never appears in an address or in the history.
- **Proof keys**: if the office server publishes keys in its discovery (Collabora does), dmOffice checks
  `X-WOPI-Proof`/`X-WOPI-ProofOld` and the timestamp (at most 20 minutes old). Without a matching signature: 500.
- **Conflicts**: locks (Lock/Unlock/RefreshLock/GetLock, 30 minutes) are held in the server's memory. When saving,
  Collabora sends which state it loaded (`X-COOL-WOPI-Timestamp`); if the file has changed in the meantime (WebDAV,
  phone, Nextcloud client), dmOffice answers 409/1010 and Collabora asks whether to overwrite or reload.
- "Save as" and export (PutRelativeFile) create the new file in the same folder, never in the notizi folder.

## Setup on Unraid with Collabora CODE

In the examples, dmOffice runs at `https://office.example.com` behind a reverse proxy (Nginx Proxy Manager or similar).

**1. A subdomain of its own for Collabora** (recommended), e.g. `https://collabora.example.com`, pointing at the
same proxy in DNS.

**2. Start the container** – search the Community Apps for "Collabora" (CODE template), or use Docker → Add Container:

| Field | Value |
| --- | --- |
| Repository | `collabora/code` |
| Port | container 9980 → host, e.g. 9980 |
| Variable `aliasgroup1` | `https://office.example.com:443` (the address of **dmOffice** – only this WOPI host may open files) |
| Variable `server_name` | `collabora.example.com` (how Collabora introduces itself to the browser) |
| Variable `extra_params` | `--o:ssl.enable=false --o:ssl.termination=true` (the proxy handles TLS) |
| Variable `dictionaries` | `de_DE en_GB en_US` (optional) |
| Variable `username` / `password` | for the admin console at `/browser/dist/admin/admin.html` (optional) |
| Extra Parameters | `--cap-add MKNOD` (optional, faster opening) |

With plain Docker:

```bash
docker run -d --name collabora -p 9980:9980 -e "aliasgroup1=https://office.example.com:443" -e "server_name=collabora.example.com" -e "extra_params=--o:ssl.enable=false --o:ssl.termination=true" --cap-add MKNOD --restart unless-stopped collabora/code
```

**3. Proxy host** `collabora.example.com` → `http://<unraid-ip>:9980`, **websockets on**, SSL certificate as for dmOffice.

**4. dmOffice → Admin → Office in the browser**: address `https://collabora.example.com`, "Check and save".
Green means: "Connected to Collabora Online … Requests are verified with proof keys." Then in Files choose
"New → Word document" – the document opens in the editor.

### If the containers cannot reach each other through the public address

dmOffice reads the discovery and Collabora calls dmOffice (WOPI) – both through the addresses you entered. If you
cannot get in through the public address from inside your home network (no hairpin NAT):

- Office server address in dmOffice: `http://<unraid-ip>:9980` (the browser still gets
  `https://collabora.example.com`, which Collabora reports through `server_name`).
- "dmOffice address for the office server": `http://<unraid-ip>:8080`; in Collabora then
  `aliasgroup1=http://<unraid-ip>:8080`, and add `--o:net.frame_ancestors=https://office.example.com` to `extra_params`
  (otherwise the browser refuses to embed the editor).

### Same domain instead of a subdomain

This works too: in the proxy for `office.example.com`, send the paths `/browser`, `/hosting`, `/cool` (websocket) and
`/lool` to `http://<unraid-ip>:9980`, and everything else to dmOffice. dmOffice does not use these paths. In
Collabora set `server_name=office.example.com`, and in dmOffice use `https://office.example.com` as the office
address. A subdomain is simpler and avoids surprises on updates.

### Tested with Collabora CODE 26.04

CODE 26.04 as a container on Unraid, behind a reverse proxy with its own subdomain. Collabora does its own TLS on
port 9980 with its own certificate, so choose the scheme **https** in the proxy; with `http` you get 502 Bad Gateway.

- The discovery was read through the public address; the addresses in the editor look like `https://<collabora-host>/browser/…`.
- Collabora calls dmOffice by WOPI (`CheckFileInfo` 200) – the office server → dmOffice direction works.
- This container publishes **no proof keys**; dmOffice then checks only the token (Admin shows this).
- Embedding: Collabora allows it only for `frame-ancestors <Collabora-host>:* <WOPI-host>:*`; a source without a
  scheme does not match an http page. With dmOffice served over https it matches. For a test with dmOffice over http,
  the container additionally had `--o:net.frame_ancestors=http://<dmoffice-host>:<port>` in `extra_params`.
- **Editing and saving work**: opened an Excel workbook, entered text and a number, Ctrl+S – Collabora calls
  CheckFileInfo, GetFile, Lock, PutFile, Unlock (all 200); the new content is in Files, the old one as a previous
  version. The empty templates (Word A4, Excel "Sheet1") open cleanly, and so does the direct link `#/files/:office/…`.
- **Open issue**: Collabora sent the page no PostMessage messages (`App_LoadingStatus`, `UI_Close`). Consequence: the X
  at the top right in Collabora does not close the editor (greyed out), and the loading indicator disappears only
  after 15 seconds. "Back" in dmOffice's bar closes it as intended. `PostMessageOrigin` is correct (checked); the cause
  is not yet found – check again once it runs over https.

### Home network only, without a reverse proxy?

Possible, but not recommended:

- The browser has to accept Collabora's certificate (`https://<unraid-ip>:9980`, "Dummy Authority") – separately on
  every device, which is awkward on a phone. dmOffice itself checks the certificate when reading the discovery and
  rejects a self-signed one (a "do not verify certificate" switch is deliberately not built in).
- Alternatively run Collabora without TLS (`--o:ssl.enable=false`) and open dmOffice over http in the home network
  too – then everything is unencrypted, and `aliasgroup1`/`frame_ancestors` must name dmOffice's http address.
- Either way it does not work on the go (phone on mobile data), and an https page (`office.example.com`) may not
  embed an editor from `http://<unraid-ip>…`.

With a subdomain of its own behind the proxy, both work – at home and away – without certificate warnings.

### Troubleshooting

- "not reachable": check the address from inside the dmOffice container (`docker exec dmoffice wget -qO- https://collabora.example.com/hosting/discovery`).
- Empty editor, browser console says "violates … frame-ancestors": dmOffice is served over http or at an address
  Collabora does not know – open dmOffice over https, or adjust `aliasgroup1`/`net.frame_ancestors`.
- 502 Bad Gateway at the proxy: Collabora speaks https on 9980 – forward with scheme https, or switch Collabora to
  http with `--o:ssl.enable=false --o:ssl.termination=true`.
- Editor stays empty or "Unauthorized WOPI host": `aliasgroup1` does not match the dmOffice address (scheme, host, port).
- "Proof key check failed" in the dmOffice log: Collabora calls dmOffice at a different address than the WOPI address
  entered – set the field "dmOffice address for the office server".
- Saving fails with "Storage full": the user's storage limit (Admin).

## Existing ONLYOFFICE Document Server (Docs API)

Why this exists alongside WOPI: an installed ONLYOFFICE speaks its Docs API without any changes, whereas WOPI would
first have to be switched on in the container. Nextcloud and Euro-Office use the same route.

**Flow.** The UI loads `api.js` from the Document Server; dmOffice supplies the configuration, signed with the JWT
secret (HS256). The Document Server fetches the file at `<dmOffice>/onlyoffice/file?t=…` and reports the state to
`<dmOffice>/onlyoffice/callback?t=…`. `t` is a separate, short-lived token per user and file (as with WOPI); the
callback must also be signed with the JWT secret. On status 2 (ready) or 6 (intermediate save), dmOffice downloads
the result – only from the configured Document Server, whatever host the message names – and saves it through Files.
If the file was changed elsewhere in the meantime, nothing is overwritten; "Name (conflict).docx" is created next
to it instead.

### Setup

Example: ONLYOFFICE as a container on Unraid (port 8900), reachable at `https://onlyoffice.example.com`.

1. **Reverse proxy**: proxy host `onlyoffice.example.com` → `http://<unraid-ip>:8900`, **websockets on**, SSL
   certificate (Let's Encrypt), allow large uploads (Nginx Proxy Manager → Advanced: `client_max_body_size 512m;`
   plus `proxy_read_timeout 3600s;`). Pass the host on (`X-Forwarded-Host`, `X-Forwarded-Proto` – NPM does this),
   otherwise the Document Server builds `http://` addresses and the browser blocks them.
   *Check: `https://onlyoffice.example.com/healthcheck` answers `true` and `api.js` loads.*
2. **HTTPS is required**: if dmOffice runs over https, an editor from `http://<unraid-ip>:8900` would be blocked as
   mixed content and is unreachable on the go. dmOffice warns when you enter an `http://` address.
3. **dmOffice → Admin → Office in the browser**:
   - Office server address: `https://onlyoffice.example.com`
   - JWT secret: the value of `JWT_SECRET` in the ONLYOFFICE container (Unraid: edit the container → variable
     `JWT_SECRET`; if none was set, the image generates one on first start – then find it inside the container in
     `/etc/onlyoffice/documentserver/local.json` under `services.CoAuthoring.secret.inbox.string`). It is stored
     encrypted with the master key and never shown again.
   - JWT header only if `JWT_HEADER` is set on the container (default `Authorization`).
   - "Check and save": dmOffice asks `CommandService.ashx` for the version with a signed request. Without a secret,
     or with a wrong one, you get a clear message ("requires a JWT secret" / "does not match").
4. **Return path**: the Document Server must be able to reach dmOffice's address (to fetch the file and for the
   callback). If that does not work from inside the home network (no hairpin NAT), enter `http://<unraid-ip>:8080`
   under "dmOffice address for the office server". ONLYOFFICE then needs the container setting
   `ALLOW_PRIVATE_IP_ADDRESS=true`, because otherwise it does not load files from private addresses.

Nothing else needs to change on the container (JWT is already on there).

### What has been verified and what has not

- Against a real ONLYOFFICE server (`api.js` 10.0.0, web apps 9.3.5): detection as Docs API, the message "requires a
  JWT secret", and "does not match" with an invented secret; `api.js` loads in a dmOffice page, the editor starts and
  rejects an unsigned configuration ("The document security token is not correct").
- Not against the real server: opening, saving and the callback with the correct secret. The flow is verified with a
  rebuilt Document Server and a test key of our own (`OnlyOfficeTests`). First real test: enter the secret, "New →
  Word document", type something, close the editor; after a few seconds the new state is in Files.
- Phone: according to all sources, the Community Edition does not edit in a phone browser (view only). For phones,
  Collabora is the right choice.

### ONLYOFFICE over WOPI (alternative)

dmOffice also reads ONLYOFFICE's WOPI discovery (placeholders such as `<ui=UI_LLCC&>` are replaced). For that, WOPI
would have to be switched on in the container (`"wopi": {"enable": true}` in `local.json`, probably
`WOPI_ENABLED=true` in the image). Not verified; for an existing server the Docs API is the simpler route.

## Limits and ideas

- Locks live in memory: after a dmOffice restart they count as free (Collabora locks again on the next save).
- No renaming from within the editor (`SupportsRename` off) and no editing through public links.
- Phone app: see below.

## Phone app

The Android app opens DOCX, XLSX, PPTX, ODT, ODS and ODP from Files in the office server's editor (a page of its
own with a WebView, `ui/OfficeActivity.kt`) as soon as an office server is configured:

- **Tapping** opens the editor; the three-dot menu has "Edit in Office" (or "View in Office"); "Open in app" stays
  for other apps. Tapping a CSV still opens it in an app. Without an office server (`/api/office/config` returns
  `enabled: false`) everything works as before.
- **Upload button → Word document / Excel workbook / PowerPoint presentation** creates an empty file through
  `/api/office/new` and opens it.
- The session goes through `/api/office/open` with the app password (inside the envelope, like everything else).
  WOPI: a form POST with `access_token` straight into the WebView (no embedding, so no `frame-ancestors` problem).
  Docs API: a small page with `api.js` and the signed configuration, loaded with dmOffice's address as its origin;
  "Close" in the editor (`onRequestClose`) also closes it in the app.
- The **back button** or the arrow at the top closes the editor and reloads the folder list – and once more after
  15 seconds, because ONLYOFFICE saves only after closing. Rotating the phone or opening the keyboard does not
  reload the editor.
- Three-dot menu in the editor: "Reload" and (ONLYOFFICE only) "Phone layout", see below.

### Editing on a phone with ONLYOFFICE / Euro-Office – what can be said (20 Oct 2026)

Not verified against a real server – that requires the JWT secret. What could be read from the code of an
ONLYOFFICE server (web apps in the path `9.3.5-…`):

- `api.js` does **not** detect the phone itself. Which UI appears is decided only by the configuration's `type`
  field: `mobile` → phone editor, anything else the normal ("desktop") editor. dmOffice does not set `type`, so
  phones get the normal editor too, and it does not redirect on a phone.
- In the phone editor, editing depends on the license (`isEdit = canLicense && …`); according to ONLYOFFICE the
  "Mobile web editors" are part of the Enterprise Edition, and the Community Edition (and so probably Euro-Office)
  only displays there.
- Conclusion: in the app, **editing should work, but in the desktop UI** – complete, yet cramped on a narrow screen
  (holding the phone in landscape helps). "Phone layout" in the menu brings up the phone UI (`type: mobile`,
  outside the signed part) – with the free edition probably view only there.
- Whether the Document Server accepts a configuration without `type` in the token when `type: mobile` is set
  outside it has not been verified either (the token does not contain `type`; according to the docs the outer value
  then applies).

For comfortable editing on a phone, **Collabora CODE** remains the better choice (dedicated phone UI, free).

### Tested (emulator, Android 15, own test server, fake office servers)

- WOPI (fake Collabora): tapping opens, token by POST, CheckFileInfo/GetFile, typing with the keyboard, saving with
  Lock/PutFile/Unlock (old version kept), back button returns to the list, new size/time visible, "New → Word
  document" creates and opens.
- Docs API (fake ONLYOFFICE with JWT): configuration signed, origin = dmOffice address, file fetched, "Save and
  close" closes in the app, callback saves, the list shows the new state after 15 s; "Phone layout" sets
  `type: mobile`; rotating does not reload.
- Without an office server: tapping opens the app chooser as before, no office entries in the New menu.
- Not tested: a real Collabora/ONLYOFFICE/Euro-Office on a phone, inserting an image (file picker), "Download as".
