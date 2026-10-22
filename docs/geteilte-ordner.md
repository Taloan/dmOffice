# Shared file folders
[Deutsch](geteilte-ordner.de.md) · English

A folder in the file storage can be shared with other users on the same server, either for **reading** or for **editing**. The
owner grants the access and can change or withdraw it at any time. The model is the same as for calendars, task lists and note
folders (see [geteilte-kalender.md](geteilte-kalender.md), [geteilte-notizen.md](geteilte-notizen.md)): the content **stays in the
owner's storage**, and the server reads and writes there for the recipient, checking the share on **every** request. No copies, no
key exchange, and revoking takes effect immediately. Only **folders** are shared; single files are still shared with a link.

## How it works

- **Share:** `data/file-shares.json` (`FolderShareStore`, `Dav/FileShares.cs`), a file of its own next to `shares.json`.
  An entry holds the owner, the folder's `fileid` (which survives renaming and moving), the members, and which of them are
  read-only. If the folder lands in the recycle bin, the share rests and comes back when the folder is restored.
- **Share dialog** (Files): at the top "Share with people" (the same building block as for calendars: `usePeople`/`PeopleList`
  in `calendar/ShareDialog.tsx`), below it "Link" as before (expiry, downloads, password). For single files there is only the link.
- **On the recipient's side**, the root contains a virtual folder **"Shared with me"** (only while someone is sharing something),
  holding the folders as "Name (from owner)". It appears in `GET /api/files` as a folder, in the WebDAV/Nextcloud root directory,
  and in the web app additionally in the navigation on the left. An own entry of the same name is hidden while the area exists.
- **Resolution:** `SharedFiles.Resolve(http, path)` turns the user's path into a `FileLoc` (storage, path within it, shared folder,
  write permission). All endpoints (`/api/files/*`, `/dav`, `/remote.php`, preview, content search, Office) go through it.
  Without a share: 404, as for a path that does not exist.
- **File ids** (Nextcloud `oc:fileid`, preview `?fileId=`): for the recipient `share · 2^40 + fileid at the owner`. This never
  collides with their own ids, and `SharedFiles.ById` checks the share and that the file lies within the shared folder.

## Permissions

| | Owner | Edit | Read | no share |
|---|---|---|---|---|
| View, download, preview, search (name and content), ZIP | ✔ | ✔ | ✔ | 404 |
| Upload, create folder/file, save text, edit in Office, restore an earlier version | ✔ | ✔ | 403 | – |
| Rename, move (within the shared folder), delete | ✔ | ✔ | 403 | – |
| Rename, move, delete the shared folder itself | ✔ | 403 | 403 | – |
| "Move" out into your own storage | – | **copies**, original stays | copies | – |
| "Move" something of your own into the shared folder | – | copied to the owner, gone on my side | 403 | – |
| Share, links, favorites | ✔ | 403 | 403 | – |
| Hide "Shared with me" entry (leave) | – | `POST /api/files/shared/leave` | ✔ | – |

Subfolders inherit the share, and new subfolders are included at once. The protected notizi folder cannot be shared, nor can a
folder that contains it (400); its content is invisible to recipients, just as it is to the owner in the web app.

## Recycle bin, versions, storage space

Deleted items land in the **owner's** recycle bin, and so do earlier versions; only the owner can restore them. A recipient's upload
checks and uses the **owner's** quota (message "There is no space left with the owner", 507). A copy into your own storage counts
against the recipient. "Recently used" stays per user.

## WebDAV and Nextcloud clients

`/dav/files/<login>/`, `/remote.php/dav/files/<login>/` and `/remote.php/webdav/` show "Shared with me" in the root, with the
folders inside. `oc:permissions` follows the permission (`SG` read only, `SGDNVW`/`SGDNVCK` edit, `SGCK` for the shared folder
itself), and `oc:owner-id` is the owner. The root changes its ETag when something changes in a shared folder, so that the client
descends into it. COPY/MOVE between your own storage and a shared folder write across two storages (`SharedFiles.CopyAcross`).

## Office

When a file in a shared folder is opened, the server issues a token for the **owner's** file (`WopiGrant.V` = member). WOPI and
ONLYOFFICE check the share again on **every** request (`SharedFiles.Allows`); only someone with Edit may write. A revocation thus
ends running sessions as well. Locks (`WopiLocks`) apply per file of the owner, so also between the owner and a member.

## Owner: overview

Files and folders show a person icon when shared. "Shared by me" contains "Shared with people" (`GET /api/files/shares`) and
"Shared by link". The details pane shows the members with their permissions.

## Android app

The app fetches the file list via `GET /api/files?path=`, `download`, `thumb`, `upload`, `move`, `delete`. "Shared with me" shows up
there unchanged as a folder, and preview and download work via the path. **Still needed** to round this off: the app does not know
`canWrite` (in `GET /api/files` and `/api/files/shared`) – without edit permission the user only sees the server's error message
when trying (403, "Read only …"); creating/deleting/renaming should be hidden there. The sorting of the root and the handling of
403/507 errors have not been checked in the app. Automatic upload targets the user's own folders and is unaffected.

## Checksums and duplicates

`POST /api/files/hashes` (the app's checksum lookup: "is this content already backed up?") reads only the user's own storage
(`http.Space().Db`) and does not know "Shared with me" – a shared folder therefore does not count as a backup of one's own. This
should stay that way (do not resolve through `SharedFiles`).

## Open decisions

1. **Sharing single files internally:** not for now (use a link). A later extension: allow `FolderShare.Folder` for files as well.
2. **Restoring deleted items by an editor:** only the owner sees the recycle bin; the web app offers the person who deleted
   something no "Undo" for it. Recommendation: leave it that way.
3. **Showing who changed something last:** does not exist (files have no editor field). Recommendation: do not build it.
