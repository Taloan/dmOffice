# Connect notizi to dmOffice (QR code / link)

[Deutsch](notizi-verbinden.de.md) · English

dmOffice creates a dedicated access for notizi: an app password with the scope `notizi` that sees **only the sync
folder**. In dmOffice: *Settings → Notes → notizi → "Connect notizi"*. dmOffice then shows a QR code, a "Copy link"
button and the three values for typing in by hand.

## Link

```
notizi://webdav?url=<address>&user=<login name>&password=<app password>
```

- UTF-8, all values URL-encoded.
- `url` is already the WebDAV root for notizi, e.g. `https://office.example.com/dav/files/<login>/notizisync/`
  (with a trailing slash).
- `user` is the dmOffice login name, `password` the app password. dmOffice shows it only once.

## What notizi needs for this

1. In the sync dialog (*Settings → Sync → Connection → WebDAV*), "Scan QR code/link": paste the link, or read the QR
   code from an image or the clipboard; on Android later with the camera. The three fields are filled from it; the
   E2EE password remains a separate entry.
2. Optionally register `notizi://` as a URL scheme so that clicking the link opens the dialog.

## Behavior on the server

- With the notizi access, anything outside the folder returns `403`, for example a PROPFIND on `/dav/files/<login>/`
  or on calendars and address books. The web API is blocked as well. So notizi cannot search or create above its root.
- Once such an access has signed in for the first time, **all other** app passwords no longer see the folder over
  WebDAV (`404`): Explorer, DAVx⁵, and also an older app password that notizi used so far. Merely showing the QR code
  therefore locks nothing out yet. After that, switch notizi over right away and revoke the old app password under
  *Devices & sync*.
- Each "Connect notizi" creates another access (one per device); those already issued stay valid and can be revoked
  individually under *Devices & sync*.
- Nextcloud clients (`/remote.php/…`) never see the folder. In the web UI's Files it is likewise invisible and
  locked. Notes in dmOffice (web and app) still read and write it.
