# Storage and backup
[Deutsch](speicher-und-sicherung.de.md) · English

dmOffice 2.0 keeps a second copy of the data in two parts:

| What | Where | How often |
|---|---|---|
| File contents | **Storage Extender** | continuously |
| Databases (mail, calendars, contacts, tasks, notes, journal, chat, file names and folders, users, settings) | **Core backup** | e.g. daily |
| External folders | – | not backed up, back them up yourself ([External folders](externe-ordner.md)) |
| Master key | – | not backed up anywhere, keep it yourself |

**The Storage Extender alone is not a backup.** Without the databases and the master key nothing can be restored. Together
with the core backup it is a complete backup outside the server.

Both are set up in **Admin → Server** (Storage Extender and Backup strategy). No restart is needed.

## Prerequisite: master key

Set **`DMOFFICE_MASTER_KEY`** in the container template (masked variable) and keep it separately, e.g. in a password
manager. Without it the master key lies as a file in the data directory, and the core backup cannot be switched on. The
administration shows a warning bar in that case. Whoever has the key and access to the backup target can read everything;
without the key, nobody can open the backup – dmOffice included.

## Storage Extender

### 1. Where is the second place?

**Type:**

- **Folder on the server** – e.g. a USB disk, mounted in the container under `/external/<name>`. The folder must not be
  inside the data directory and must not overlap a shared external folder. A folder on the same disk as dmOffice does not
  help if that disk fails.
- **WebDAV** – e.g. Nextcloud or a Storage Box: address, user name, password (ideally an app password just for this). A
  missing target folder is created.
- **S3** – e.g. Garage, MinIO or Hetzner Object Storage: address, region, bucket, folder in bucket, access key, secret key.
  A stored secret key is never shown again; leave the field empty to keep it.

**Save and test** writes, reads and deletes a sample.

### 2. Set up the local storage pool

The local storage pool is the part of the local storage volume that is available. The Storage Extender holds all files, the local storage pool the most recently used. If all files fit within the limits, all of them are on
the server as well, and dmOffice fetches missing ones back by itself. Otherwise dmOffice automatically fills the free storage space with the newest files, and a file that is only in the Storage Extender is fetched on opening; large
files are passed through instead of stored. Thumbnails, mail, calendars, contacts, notes and the databases always stay local.

- **Always keep free on the local storage volume** (default 50 GB, at most a fifth of the volume): applies to the whole volume of the data
  directory, including other data there. When the limit is reached, dmOffice cleans the local storage pool and only passes opened files through. If nothing can be evicted,
  dmOffice accepts no new files.
- **Use at most this much for the local storage pool**: optional upper limit. It counts files only; thumbnails always stay local and take additional space on the volume.

The graphic shows what happens as the sliders move: red are files that will be cleaned, yellow are files still to be transferred. Nothing is saved until **Apply**. The used and free space of the Storage Extender is only shown for a folder target, S3 and WebDAV do not report it reliably.

Backing up the Storage Extender is the administrator's responsibility. For earlier states of the files there is the files backup.

### 3. Switch on

**Switch on Storage Extender.** The first sync starts 30 seconds after the server start, then at the latest every
10 minutes, and right after a change. The state shows "Last synced" and how many files are in the Storage Extender.

### Behaviour

- **What is transferred:** the file contents, already encrypted with a key per file, under random names – including
  previous versions, recycle bin and thumbnails. The provider sees neither contents nor file names, only number, size
  and time of the objects. Names, folders and keys are in the databases (core backup).
- **Deleting:** what disappears for good in dmOffice stays in the Storage Extender for another 7 days; with the core
  backup on, at least (number of backups + 1) × interval. While
  the core backup fails or is overdue, dmOffice deletes nothing at the target.
- **Target unreachable:** files on the server can still be opened, the sync catches up later and shows an error. Files only in the
  Storage Extender cannot be opened until it is reachable again.
- **Locks:** switching off, changing the type or deleting the credentials is refused while files exist only in the
  Storage Extender. If they fit within the cache limits, dmOffice fetches them back by itself.
  A new address is only accepted if everything can be found there.
- **One folder per server:** if another dmOffice server stores to the same place, dmOffice deletes nothing there.

## Files backup

Creates a separate copy of the files and runs before every core backup. It transfers only what is missing and requires
the core backup. Every core backup younger than the retention period can be restored completely, even if it was copied
elsewhere.

- **Target:** **Use the target from Storage Extender** (it must be turned on there) or **Other target**. With the target
  from Storage Extender, the copy is stored there in its own folder `files-backup` next to `backups`. The files then take up
  storage twice. If this storage is lost, both are affected. Another target protects against that.
- **Retention:** at least (number of backups + 1) × interval, or unlimited.
- **Deleting:** content no database refers to any more is deleted from the copy after the retention period, regardless of
  the period in the Storage Extender.
- **Deleted users:** their files disappear from the server immediately and right after that from the Storage Extender and the copy,
  even if a target only becomes reachable later. Older core backups can then no longer restore this account's files.
  Core backups remain as a whole until newer ones replace them and contain the data in encrypted form until then. A note at the
  target makes sure a restored older core backup removes the account again. A core backup copied elsewhere and restored without
  this target does not know the note.

Restoring and fetching individual files: [Restoring](wiederherstellen.md).

## Core backup

**Admin → Server → Backup strategy** contains the overview, the core backup and the files backup. The core backup covers
everything except the file contents; every backup is complete.

1. **Target** – **Use the target from Storage Extender** or **Other target** (folder, WebDAV or S3 as above, **Save and
   test**). With a target of its own the core backup also runs without Storage Extender; it then only contains the names
   of the files, not the files.
2. **Switch on core backup** – only possible with `DMOFFICE_MASTER_KEY` and a configured target.
3. **How often:** hourly, every 3, 6 or 12 hours, daily, weekly, monthly, with a time. **Keep backups:** the number dmOffice
   keeps at the target. **Back up now** runs one immediately.
4. **Create recovery key** – contains the place and credentials of the backup target, encrypted with the master key. Save
   it as a file or in the password manager next to `DMOFFICE_MASTER_KEY`. If the credentials change later, the
   administration asks to create it again.

### Content

- **Included:** all user databases, chat, user list, server settings, key ring; packed as one file and encrypted again
  with a key derived from the master key.
- **Not included:** file contents (in the Storage Extender), the master key, the container template (environment
  variables, mounted folders), files in external folders (only which folders were shared), the search index of server folders.
- Before each backup dmOffice waits (at most 30 minutes) for a sync of the Storage Extender, so the backup only refers to
  contents that are already there.
- A backup is a snapshot: changes to mail, calendars etc. after it are not included. Files uploaded after the last backup
  are recovered without their names into the folder "Nach Wiederherstellung gefunden".

### Restore

On a new, empty dmOffice with the **same** `DMOFFICE_MASTER_KEY` and the same mounted folders:

1. On the first-start page: **Restore from the core backup instead …**
2. **Setup code** (from the server log), **Recovery key (optional)** → **Read in**. Without a recovery key, enter the
   place and credentials by hand (**Without recovery key: enter access data …**).
3. **Search core backups**, choose a backup, **Restore this backup**.
4. The server restarts and then fetches the file contents from the Storage Extender. The container needs a restart policy.

## Data export

In addition, each person can place their data **decrypted and in open formats** at a server target of their own, for example via
WebDAV on pCloud (`https://ewebdav.pcloud.com/…` for EU accounts, otherwise `https://webdav.pcloud.com/…`). In an emergency
the data can be opened there without dmOffice. A data export cannot be restored. There is no download to the browser; to have everything on your own
computer, sync the target there.

- **Set up:** **Settings → Account → Data export** – target (WebDAV address with user name and password, or an own
  external folder that may be written to, with a subfolder, or OneDrive with a Microsoft sign-in and a folder, default
  “Datensicherung”), **Test connection**, areas and folders, time. It can only be
  switched on after confirming that the data will be readable at the target.
- **Contents:** files byte for byte as originals in their folders, calendars and tasks as `.ics`, contacts as `.vcf`, notes
  and journal as `.md`, DMS as a list (`.csv`, `.json`), Coach as `.csv`, albums as `.json`. Every area and single
  folders can be excluded. Not included: e-mail (stays with the mail provider), external folders, chat. A vault in
  Cryptomator format in the file storage goes to the target encrypted along with the files.
- **Schedule:** daily at the chosen time and with **Export now**; only changes are transferred. For several people the data
  export runs one after another. Status, last run and last error are shown in the settings; if the data export fails several times in a
  row, Pukki sends a message.
- **Write-only:** dmOffice never reads anything back from the target.
- **Folder names:** in the interface language of the first run (German: `Dateien`, `Tagebuch` …, English: `Files`, `Journal` …), fixed after that. Journal media are stored per year under `Media/` next to the entries.
- **`.trash`:** deleted items – and, if chosen, older versions of changed files – go to the hidden folder `.trash` at the
  target with the same folder structure and are removed after the retention period (7, 30, 90 days or never).
- A `LIESMICH.txt` at the target explains the structure and which programs to use.
- **Admin → Allow for users → User data export:** allow or block it and see the status per person – without targets or
  credentials.
## See also

- [External folders](externe-ordner.md)
- [Updating](updating.md)
