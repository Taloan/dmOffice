#!/bin/sh
# Start von dmOffice im Container: nicht als root. Läuft der Container als root (Vorgabe von Docker), werden die Daten einmalig
# dem Benutzer „app“ (UID 1654, im .NET-Image vorhanden) bzw. DMOFFICE_UID übergeben und dmOffice ohne Root-Rechte gestartet. So kann eine Lücke in
# einem Werkzeug, das fremde Dateien liest (Texterkennung, PDF), weder Programmdateien austauschen noch Systemdateien lesen.
# Eigene Kennung, etwa wie auf Unraid üblich 99/100 (nobody/users), damit dmOffice eingebundene Freigaben beschreiben darf:
# DMOFFICE_UID / DMOFFICE_GID. Ausnahme für Einbindungen, die nur root beschreiben darf: DMOFFICE_RUN_AS_ROOT=1 (nicht empfohlen).
set -e
APP_UID="${DMOFFICE_UID:-1654}"
APP_GID="${DMOFFICE_GID:-1654}"
DATA="${DMOFFICE_DATA:-/data}"

if [ "$(id -u)" = "0" ] && [ "${DMOFFICE_RUN_AS_ROOT:-0}" != "1" ]; then
  mkdir -p "$DATA"
  # Nur, wenn die Daten noch root gehören (erster Start nach dem Umstieg) - sonst bei jedem Start alle Dateien
  if [ "$(stat -c %u "$DATA")" != "$APP_UID" ]; then
    echo "dmOffice: übergebe $DATA an den Benutzer app ($APP_UID) - einmalig, kann bei vielen Dateien etwas dauern"
    chown -R "$APP_UID:$APP_GID" "$DATA"
  fi
  exec setpriv --reuid="$APP_UID" --regid="$APP_GID" --init-groups --no-new-privs dotnet /app/DmOffice.Server.dll "$@"
fi

exec dotnet /app/DmOffice.Server.dll "$@"
