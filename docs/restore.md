# Restoring

[Deutsch](restore.de.md)

dmOffice has two ways: bring the whole server back onto an empty dmOffice, or fetch individual files with the `restore` command. Neither overwrites a running server.

The backups are set up under **Admin → Server → Backup strategy**.

## Restore a server

Have ready:

- the `DMOFFICE_MASTER_KEY` of the old server (required),
- the recovery key or the credentials of the core backup target.

Steps:

1. Start dmOffice with an empty data folder and put the same `DMOFFICE_MASTER_KEY` into the container template.
2. On the sign-in page choose **Restore from core backup**.
3. Read in the recovery key or enter the target by hand, **Find core backups**, pick a backup.
4. dmOffice states beforehand how many files of the backup are missing at the target. The server then restarts.
5. dmOffice fetches the files itself, from the configured storage and from the files backup.

Files that reached the configured storage after the chosen backup end up without names in the folder "Nach Wiederherstellung gefunden".

Every core backup younger than the retention period of the files backup can be restored completely.

### A backup copied elsewhere

A backup that is no longer at the target is read through a folder target:

1. Put the file `<time>_<id>.dmb`, unchanged, into `backups/` of a folder the container can see, such as `/external/restore/backups/`.
2. When restoring, choose **Folder on the server** and enter `/external/restore`. If the core backup used a prefix, the backup goes under `<prefix>/backups/`.

The credentials for the files are in the settings of the backup.

## Damaged database

Every person has their own database, plus chat, albums and push. The nightly check (**Administration → System → Databases**) tests each one separately. dmOffice rebuilds broken indexes itself. For any other finding, two buttons appear next to the affected database:

- **Try to repair:** dmOffice copies everything readable into a new database and checks it. Often only single entries are missing afterwards.
- **Replace from backup:** only this database, from a chosen core backup. Changes in it since that backup are lost. For a person, files uploaded afterwards are kept: they are placed without names in the folder “Nach Wiederherstellung gefunden”.

Both only affect the damaged database, all others stay unchanged. The previous file is kept as a copy next to the new one (`<name>.beschaedigt-<time>`).

## Fetch individual files

`restore` fetches files, folders, deleted files and earlier versions from the core backup without changing anything on the running server. Paths are written as in rclone: `<user>:/<path>`.

**The server is running:** call it in the server's container. It already knows the target, credentials and master key, and uses the newest backup.

```bash
docker exec dmoffice restore ls anna:/Contracts
docker exec dmoffice restore copy anna:/Contracts/lease.pdf /external/restore
```

**The server is gone:** with the recovery key (**Administration → Server → Backup strategy**) and the master key. The tool finds the backups at the target by itself.

```bash
export DMOFFICE_MASTER_KEY='…'
docker run --rm -e DMOFFICE_MASTER_KEY -v /mnt/restore:/restore dmoffice:v2 restore-tool ls --key /restore/key.txt anna:/Contracts
```

| Command | Does |
|---|---|
| `stands` | lists the backups at the target |
| `users` | lists the users in the backup |
| `ls anna:/<folder>` | shows folders and files |
| `ls --trash anna:` | shows the trash |
| `ls --versions anna:/<file>` | shows earlier versions with their number |
| `find anna: <text>` | searches names, including the trash |
| `copy anna:/<path> <target folder>` | copies a file or folder, decrypted, into the target folder |
| `copy --trash anna:/<path> <target folder>` | fetches the latest trash entry of this path |
| `copy --version <no> anna:/<file> <target folder>` | fetches an earlier version |

**Which backup:** by default the newest. `--at 2026-11-04` takes the last backup up to that day, `--at 2026-11-04T03:00` up to that time. A single backup file is chosen with `--stand <file.dmb>`.

**Where the files come from:** as set in the backup, first the files backup, then the Storage Extender. If they are in a folder, `--files <folder>` selects it and `--prefix <prefix>` the folder inside.

The target folder must be mounted in the container and writable for dmOffice, for example `/external/restore`. Existing files are not overwritten. If content is missing at the target, the tool names the file and ends with exit code 3. The vault is not included, it has its own key. The tool reads backups from version 2.0 on.

The previous syntax (`--user anna --path …`, `get … --out …`) still works.
