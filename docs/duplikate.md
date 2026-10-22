# Checksums and "Find duplicates" (concept)

[Deutsch](duplikate.de.md) · English

Status: the checksums exist. "Find duplicates" is **not built yet**; this document describes how it would build on them.

## What exists

- Every file has the **SHA-256 of its plaintext** in its row (column `sha256`, 64 hex characters, lowercase). The same
  goes for previous versions (`file_versions`) and the trash (`file_trash_items`). Schema version 18 (`Core/UserDb.cs`).
- It is filled in while writing, without a second read: the `BlobWriter` computes it while encrypting, and
  `FileStore.Commit` records it. This covers web upload, WebDAV PUT, saving in the editor, Office (WOPI/OnlyOffice),
  the notizi sync and uploads through a share link. Copying, moving, trash, restoring and versions carry it along.
- Whatever existed before version 18 is computed afterwards by `FileHashJob` in the background (`Dav/FileHashes.cs`):
  shortly after start, then hourly. It can be interrupted and resumes afterwards. It shows up under Activity as
  "Computing file checksums". Contents still held in the database get their checksum when they are moved out
  (`MigrateBlobs`). If a blob is missing or its length does not match, the column stays empty: better unknown than wrong.
- Index `files_sha256 ON files(sha256) WHERE sha256 IS NOT NULL`.
- `POST /api/files/hashes` with `{ items: [{ sha256, size }] }` (at most 5000) answers with
  `{ found: [{ sha256, size, path }], pending }`. Only visible files are counted, so not trash, versions or the notizi
  folder. `pending` is the number of files without a checksum, whose content may therefore still be missing there.
  The Android app uses this for "already backed up" and for the cross-check before "Free up space".
- In the web UI's detail pane the checksum is shown in small type below. A click copies it.

## Find duplicates: how it would work

**Query.** The index supports it without reading any content:

```sql
SELECT sha256, size, COUNT(*) AS n, SUM(size) - size AS spare
FROM files
WHERE is_dir = 0 AND sha256 IS NOT NULL AND size > 0
GROUP BY sha256, size
HAVING COUNT(*) > 1
ORDER BY spare DESC;
```

Then, per group, the paths follow (`SELECT path, modified FROM files WHERE sha256=$h AND size=$s`). The notizi folder
is filtered out (`FileStore.InRoot`), as in `FileHashes.Lookup`.

**Endpoint.** `GET /api/files/duplicates?under=<folder>` returns groups with `sha256`, `size`, `count`, `spare`
(what would be freed if one copy stays) and `paths`, page by page. Grouping costs something with many files, so the
response could be cached for 15 s, like the counters in `ActivityApi.Cached`.

**UI.** An entry under Files → "Duplicates", with groups; per group the paths with preview, folder and date, and a
suggestion which copy to keep (the oldest, or the one with the shortest path). "Delete the others" moves them to the
trash, so like any deletion it can be undone. The storage is freed only when the trash is emptied.

**Important.**
- Storage is **not deduplicated automatically**. Copies within the server already share the blob (`MoveOrCopy`). Two
  independent uploads of the same photo are two blobs with different keys, and that is meant to stay that way: each
  blob has its own key, and shared blobs would reveal that two contents are identical.
- Files without a checksum (`pending`) appear only once the background job has finished. The UI should say so.
- Empty files (`size = 0`) all have the same checksum and do not belong in the list.
