# Restoring

[Deutsch](wiederherstellen.de.md)

dmOffice 2.0 has two ways: bring the whole server back onto an empty dmOffice, or fetch individual files with the `restore-tool`. Neither overwrites a running server.

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

## Fetch individual files

`restore-tool` reads a core backup and the files backup without starting a server. No background services run, only the target is contacted, and nothing is changed there. The tool reads backups from version 2.0 on. The vault is not included, it has its own key.

The tool is part of the dmOffice image. The master key comes from the environment, so it does not appear on the command line:

```bash
export DMOFFICE_MASTER_KEY='…'
docker run --rm -e DMOFFICE_MASTER_KEY -v /mnt/backup:/backup dmoffice:v2 restore-tool users --stand /backup/20261104-030000_abc.dmb
```

| Command | Does |
|---|---|
| `users --stand <file>` | lists the users in the backup |
| `ls --stand <file> --user <login> [--path <folder>]` | shows folders and files |
| `ls … --trash` | shows the recycle bin |
| `ls … --versions <file>` | shows earlier versions with their number |
| `find --stand <file> --user <login> <text>` | searches names, including the recycle bin |
| `get --stand <file> --user <login> --out <folder> <path>` | copies a file or folder, decrypted, into the target folder |
| `get … --trash <path>` | fetches the newest entry of this path from the recycle bin |
| `get … --version <no> <path>` | fetches an earlier version |

By default the tool searches according to the backup's settings: first in the files backup, then in the configured storage. If the files are in a folder, `--files <folder>` selects it and `--prefix <prefix>` the folder inside it. A copy of the folder `files-backup` that was stored elsewhere is selected with `--files <folder> --prefix ""`.

Existing files in the target folder are not overwritten. If content is missing at the target, the tool names the file and exits with code 3.

### Examples

```bash
docker run --rm -e DMOFFICE_MASTER_KEY -v /mnt/backup:/backup dmoffice:v2 restore-tool find --stand /backup/20261104-030000_abc.dmb --user anna contract
```

```bash
docker run --rm -e DMOFFICE_MASTER_KEY -v /mnt/backup:/backup -v /mnt/out:/out dmoffice:v2 restore-tool get --stand /backup/20261104-030000_abc.dmb --user anna --out /out --trash Contracts/lease.pdf
```

The file then belongs to the user the tool runs as in the container (`DMOFFICE_UID`, `DMOFFICE_GID`). That user must be able to write to the folder.
