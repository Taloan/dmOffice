# Shared note folders
[Deutsch](geteilte-notizen.de.md) · English

A note folder – e.g. "80 Recipes" – can be shared, together with its subfolders, with other users on the same server, either for
**reading** or for **editing**. The owner grants the access and can change or withdraw it at any time. The model is the same as for
calendars and task lists (see [geteilte-kalender.md](geteilte-kalender.md), which also answers the encryption question): the content
**stays in the owner's database**, and the server reads and writes there for the recipient, checking the share on **every**
request. No copies, no key exchange, and revoking takes effect immediately.

## How it works

- **Share:** `data/shares.json`, the same file as for calendars. An entry with `Folder` (= the folder's `sync_id` in the owner's
  database) instead of `Collection` is a note folder share; the calendar functions (`ShareStore.Of/Set/Forget`, `SharedLists`)
  skip such entries. Notes are handled by `ShareStore.OfFolder/SetFolder/ForgetFolder`.
- **Scope of a share:** the folder and all subfolders below it (`NotesDb.FolderTree`), computed fresh from the owner's database on
  every request. New subfolders are therefore shared immediately; if the owner moves a note out, it disappears for the recipient.
- **Access:** `Notes/SharedNotes.cs`. `NoteScope` says which database is being worked on and whether writing is allowed. Folder ids
  (GUIDs) are unique, which is how the server tells whether a folder is the user's own or a shared one. For the recipient, notes
  in a shared folder carry a **negative identifier** `-(share id · 2³² + id)`; this way they never collide with the recipient's
  own notes, and the existing endpoints (`/api/notes/{id}/…`) work unchanged with both. The recipient no longer sees archived or
  deleted notes.

## Permissions

| | Owner | Edit | Read | no share |
|---|---|---|---|---|
| See folders, subfolders, notes, images, attachments; search | ✔ | ✔ | ✔ | – (404, empty list) |
| Create and change notes, title/color/"important", attachments, move to the recycle bin | ✔ | ✔ | – (403) | – |
| Move a note within the shared folder (and its subfolders) | ✔ | ✔ | – | – |
| Create subfolders | ✔ | ✔ | – | – |
| Move one of your own notes into the shared folder (it is copied there, the original disappears for the mover) | ✔ | ✔ | – (403) | – |
| Move out of the folder, archive, delete permanently, rename/move/delete the folder | ✔ | – (403) | – | – |
| Share and change permissions | ✔ | – | – | – |
| Stop showing (leave) | – | ✔ | ✔ | – |

## The owner's sync (notizi)

When a recipient writes, the change lands in the owner's database like one of the owner's own: `sync_rev` increases (`SaveNote`,
`SetColor`, …), the **owner's** sync is triggered and sends it as usual into the owner's encrypted sync folder, to notizi and the
other devices. The recipient has no sync of their own for it. Images that an editor inserts (`POST /api/notes/images?note=<identifier>`)
are named with the **owner's** key and stored in the owner's database.

## Images and attachments

`/api/notes/blob/<name>` (also with `?thumb=`) serves your own blobs as before; otherwise only if the name occurs in an active note
of a folder shared with you (`NotesDb.BlobUsedIn`: images, attachments, text). If the note is moved out or the share is withdrawn,
the link is dead immediately.

## Moving your own note into a shared folder

With edit permission, one of your own notes can be put into a shared folder with "Move to" (menu, editor, drag and drop).
Technically this is a copy between two databases (`SharedNotes.MoveIntoShared`): a new `sync_id` with the owner, images and
attachments renamed under the owner's key, alarm and sources are left behind; the original is deleted with a tombstone on the
mover's side, so it also disappears from their notizi. Afterwards the note belongs to the owner of the folder (only the owner can
take it out again).

## Concurrent editing

The version check remains: `PUT …/content` with `version` – if the state is stale, the server answers 409. Checking and saving now
happen under one lock per database so that two users cannot overwrite each other. The web app polls a note opened from a shared
folder every 15 seconds: if nothing is unsaved, it loads the new version; otherwise "The note was changed by someone else in the
meantime." appears with "Save as copy" (a new note in the same folder) and "Discard changes".

## Web app

Right-click an own folder → "Share…" (the same dialog as for calendars; a person icon on the folder shows that it is shared).
Recipients see "Name (from owner)" together with its subfolders under "Shared with me"; with Read, "New note" is disabled and the
menu is limited to "Open". Full-text search includes shared folders (hits are marked "from owner").

## Android app and notizi

Shared notes do **not** appear there – and never enter a recipient's sync (notizi) either: `NotesDb.Entries()` reads only the
user's own `notes` table; shared notes live exclusively with the owner (test: `SharedNotesTests`). notizi also has no read-only
concept yet, so a recipient would not be allowed to get any of it there. The app's notes come exclusively from the owner's sync
folder, and notizi belongs to a different project. The owner's own changes and the changes of recipients (which land in the owner's
database) go to the app by sync unchanged. Should the app show shared folders later, it needs: `GET /api/notes/shared` (shares
with subfolders and `canWrite`), `GET /api/notes?folder=<id>` for the list, the negative identifier for notes,
`PUT /api/notes/{id}/content` with `version`, and handling of 403 (read only) and 409 (stale) – plus a folder group of its own,
"Shared with me", without sync into Room.

## Open decisions

1. **Who saved last?** A note has no editor field; the conflict message says "someone else". A field `modified_by` would need a
   schema change (and notizi does not know it). Recommendation: do not build it.
2. **Archive/recycle bin for recipients:** an editor who deletes a note sends it to the owner's recycle bin; only the owner can
   restore it. Recommendation: leave it that way.
