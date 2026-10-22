# Updating dmOffice

*[Deutsche Fassung](updating.de.md)*

dmOffice brings its data up to date by itself when you install a new version – you do not have to run anything by hand.
Because a new version can reorganise the databases (and the file contents), dmOffice has a safety net that works **without
you copying the data folder first** and **without enough free space for a full copy**: it checks the free space, makes its
own backup when there is room, asks before big conversions and never leaves a half-done change behind.

Still: a copy of the data folder from time to time is the best insurance. The safety net below covers the databases, not a
broken disk or a deleted container.

## How to update

1. Pull the new image (Unraid: *Check for updates* → *Apply update*; Docker: `docker compose pull && docker compose up -d`).
2. Open dmOffice. If there is nothing special, it is already up to date.
3. If something is waiting (not enough space, or a big conversion), the web interface shows **Administration → Updates**
   instead of your mail. Normal users see a short notice and nothing is lost.

You can also open **Administration → Updates** at any time to see what is pending or running, how much space it needs and
how much is free, the backups, and the buttons *Start now*, *Pause*, *Check again* and *View backup*.

## What dmOffice does at the start

For every user database that is older than the new version:

1. **Space check.** dmOffice estimates the extra space the conversion needs (the database changes are written to a journal
   first) plus a reserve of 256 MB. If the free space in the data folder is smaller, **nothing is changed**: the server keeps
   running, the other users are not affected, and the page tells you exactly how much is missing. Free some space (delete files,
   attach a bigger disk) and press *Check again* – it also retries by itself every ten minutes.
2. **Backup.** If there is room, dmOffice first writes a consistent, encrypted copy of the database (SQLite `VACUUM INTO`, same
   key) to `<data>/pre-update/<date>-<version>/users/<id>.db`, together with `users.json`, `server.json`, `shares.json`, the
   master key file (if the key is stored in the data folder) and `keys/`. A backup is only made when it fits next to the
   conversion. If it does not fit, dmOffice does **not** convert until you either make room or explicitly press
   *Continue without backup* (and confirm the warning).
3. **Big conversions wait for you.** Small steps run automatically. A conversion that needs more than 1 GB of space (or takes
   several minutes) does not start silently at the first start: the page shows the time and space estimate and waits for
   *Start now*. Meanwhile the server runs; only the waiting user cannot reach their data yet.
4. **Every step is all or nothing.** Each schema step runs in one transaction together with the version number. If it is
   interrupted (container stopped, power loss, disk full) it is rolled back completely and starts again from that step at the
   next start. Nothing is half converted.

## Moving the file contents out of the database (schema 16)

Before schemas 16 and later every file lived inside the user database. Now each file is stored as its own encrypted file (`blobs/`, or
the folder set with `DMOFFICE_BLOBS`). After the schema step, dmOffice moves the old contents in the background:

- It first checks that the **blob location** has room for all contents still in the database (plus the reserve). If not, nothing
  is moved, everything keeps working as before from the database, and the page says how much is missing.
- Large amounts (over 1 GB) wait for *Start now*; *Pause* stops after the current batch, *Resume* continues.
- Moving **deletes nothing you could still need**: the old contents are emptied row by row in the database, but the space is only
  given back when someone presses *Compact database* (Settings → Statistics). Until then the database file keeps its size, and
  the pre-update backup still holds the old contents. For that reason the blob files themselves are **not** part of the backup.
- It can be interrupted at any time (restart, stopped container); the next start continues where it stopped.
- Afterwards the checksums (SHA-256, schema 18) are calculated in the background; this needs almost no space.

## Compacting the database

*Compact database* writes the database once more, so it needs free space of about the database size plus the reserve. dmOffice
checks this first and shows how much is missing instead of starting and failing in the middle. Compacting locks the database
for its duration (minutes for several GB).

## Backups: where, how long, how big

- Folder: `<data>/pre-update/<date>-<version>/` – the Administration page lists them with date, users and size and counts them
  as used space. *View backup* shows the folder and how to go back.
- dmOffice removes backups by itself after **14 days** (adjustable under *Keep*, or *never delete*), as long as no conversion is
  waiting.
- The backups hold the same secrets as the data folder (the databases are encrypted, but `users.json` and, without
  `DMOFFICE_MASTER_KEY`, `master.key` are in there). Protect them like the data folder.

## Going back

1. Stop the container and pin the image of the version that created the backup (for example `ghcr.io/taloan/dmoffice:1.1.0`
   instead of `latest`).
2. Copy `pre-update/<backup>/users/*.db` to `<data>/users/` (delete the `*.db-wal` and `*.db-shm` files of the same name first).
3. Copy `users.json`, `server.json`, `shares.json` (and `master.key`, `keys/`) from the backup back to the data folder.
4. Start the container. Changes since the backup are lost. Blob files do not have to go back; surplus ones are cleaned up.

## What changed in which version

| Release | Schema | Data change | Cost |
|---|---|---|---|
| 1.0 | 9 | Priority inbox | – |
| 1.1 | 10 | "Slim" storage mode for mail | small |
| next | 11 | Fixed file numbers for Nextcloud clients | rewrites every file row including contents still in the database: needs about the size of the file contents in extra space, takes minutes for GB |
| next | 12–15 | App password scope, trash and versions of the file store, full-text search, link limits | small |
| next | 16 | File contents as separate encrypted files | schema step small; then the background move needs the size of the file contents at the blob location; the database only shrinks after *Compact* |
| next | 17 | Tags of notes (recipes) | small |
| next | 18 | SHA-256 checksum per file | small; background calculation reads every file once |

Updating from 1.0 or 1.1 runs steps 11 to 18 in one go; this is covered by an automated test with databases created by the real
1.0 and 1.1 code.

## Notes for special setups

- `DMOFFICE_BLOBS` (own volume for file contents): the space check for moving looks at that volume, the one for the database
  conversion and the backup at the data folder.
- Network file systems sometimes report wrong free space. `DMOFFICE_ASSUME_FREE_MB` replaces the measurement by a fixed number
  of megabytes (also handy to try the checks out).
