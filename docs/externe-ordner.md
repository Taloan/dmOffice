# External folders
[Deutsch](externe-ordner.de.md) · English

External folders show files that live **outside** dmOffice's own storage: a folder on the server (a mounted share or USB
disk) or a user's own WebDAV storage (Nextcloud, Storage Box, NAS). dmOffice reads and writes there directly; nothing is
copied into its own storage and nothing is encrypted.

## Folders on the server (administrator)

1. **Mount the folder in the container** under `/external/<name>`, e.g. the Unraid share `/mnt/user/music` → `/external/music`.
   Only this step needs a container restart.
2. **Admin → Server mounts → Local server mounts → Add another folder:**
   - **Name** – as users see it.
   - **Path in the container** – the full path; the folder icon lists the folders mounted in the container.
   - **Read only** – uploading, renaming and deleting are refused.
   - **Visible to:** – all users, or selected users.
   - **Shown in dmOffice:** – everything (including folders added later) or only selected folders at the top level.
3. **Save.** dmOffice checks the path: it must exist and be a full path. The data directory, anything inside or
   above it, the top level of a disk, and the folder used as Storage Extender cannot be shared.

### Search in server mounts

After saving, **Configure indexing** defines per mount what search finds:

- **Nothing, provide only** – files are visible and can be opened.
- **Files and folders** (default for new mounts) – name, folder, file type, size and date.
- **Contents too** – also the text in **text files** (TXT, CSV, Markdown, Log, JSON, XML, YAML, HTML, EML, VCF, ICS), **documents** (PDF, Word, Excel, PowerPoint) and **images and scans** (text recognition, takes much longer). You can also choose top-level folders and a size limit.

Search stays up to date like this:

- **Files changed through dmOffice** are found immediately, and an opened folder is updated right away.
- **Files changed outside dmOffice** are found after the next check: daily at 03:00 (default), every 6 hours, hourly, weekly (Sundays at 03:00) or manually only. dmOffice compares modification time and size. A restart only interrupts a check, it continues afterwards. Processing also runs during a check.
- **The results name the status** per mount: when it was last checked and how many files are not yet processed. That keeps “Nothing found” a reliable statement.

Technical: one index for all users (`data/extindex.db`, encrypted). A mount that several users see is read only once; only users who may see it get hits. Symlinks are not followed; recycle bins and NAS preview folders (`#recycle`, `@eaDir`, `.Trash`, …) are skipped. The index is not part of the core backup and can be rebuilt. Activity shows progress under **Search in server mounts**.

## Own WebDAV folders (users)

The administrator enables them in **Admin → Allow for users → User mounts**:

- **Users may add their own WebDAV folders (Nextcloud, Storage Box, NAS …)** – takes effect immediately.
- **… also with addresses in the home network** – off by default. While it is off, addresses in the home network and the
  server itself are refused, checked on every connection. Only enable it if all users are trustworthy.

Users then add a folder in **Files → External folders → Add WebDAV folder**: name, address (Nextcloud:
`https://cloud.example.org/remote.php/dav/files/<name>/`), user name and password – ideally an app password just for
dmOffice. The credentials are stored in the user's encrypted database. Right-click a WebDAV folder to choose which folders
appear in dmOffice or to remove it; the files on the other server stay as they are.

## Using external folders

- **Files → External folders**: browse, download, upload, create folders, rename, delete; views as in the file storage
  (details/tiles with thumbnails, sorting).
- **Gallery folders** can use external folders as their source. For server folders, image data and previews are cached once
  for all users (encrypted); for own WebDAV folders per user.
- **Settings → Files → External folders** shows the free space per folder (disk of the server folder, WebDAV quota where
  the server reports it).

## Limits

- **No recycle bin, no previous versions, no sharing links.** Deleting is final.
- **Not backed up** – neither by the Storage Extender nor by the core backup. The core backup only records which folders
  were shared. Back up external folders yourself.
- **Not counted** towards the user's quota.
- **Web only.** Not available via WebDAV/Nextcloud clients or in the Android app.
- **Uploads** go in a single request and are limited to 512 MB per request (no chunked upload as in the own file storage).

## See also

- [Storage and backup](speicher-und-sicherung.md) – Storage Extender and core backup
