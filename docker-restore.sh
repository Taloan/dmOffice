#!/bin/sh
# Dateien aus dem Core-Backup holen, im laufenden Container: docker exec dmoffice restore ls anna:/Verträge
# Liest Einstellungen und Schlüssel des Servers nur, startet keine Dienste. Läuft als derselbe Benutzer wie dmOffice,
# damit geholte Dateien ihm gehören (Zielordner muss für ihn beschreibbar sein). Anleitung: docs/wiederherstellen.de.md
set -e
APP_UID="${DMOFFICE_UID:-1654}"
APP_GID="${DMOFFICE_GID:-1654}"
if [ "$(id -u)" = "0" ] && [ "${DMOFFICE_RUN_AS_ROOT:-0}" != "1" ]; then
  exec setpriv --reuid="$APP_UID" --regid="$APP_GID" --init-groups --no-new-privs dotnet /app/DmOffice.Server.dll restore-tool "$@"
fi
exec dotnet /app/DmOffice.Server.dll restore-tool "$@"
