#!/usr/bin/env bash
# Bilder fuer Blog-Artikel (docs/img/blog) - eigene, leere Datenbank, erfundene Beispieldaten.
#   tools/docs/make-blog.sh   (Oberflaeche deutsch; docs/img/de und docs/img/en bleiben unberuehrt)
#   ONLY=m-vip,m-leer tools/docs/make-blog.sh   nur diese Bilder neu schreiben (die Schritte laufen trotzdem alle)
# Braucht: dotnet, node, Edge oder Chrome (BROWSER=/pfad/zu/msedge.exe), gebaute Oberflaeche (web: npm run build).
set -euo pipefail
cd "$(dirname "$0")"
ROOT="$(cd ../.. && pwd)"
DATA="$PWD/.data"
PORT=5098
BASE="http://127.0.0.1:$PORT"
BROWSER="${BROWSER:-/c/Program Files (x86)/Microsoft/Edge/Application/msedge.exe}"
PASS="Doku-$(date +%s)-$RANDOM"

[ -d node_modules ] || npm install --no-audit --no-fund
rm -rf "$DATA"
dotnet build "$ROOT/DmOffice.Server" -v q >/dev/null
dotnet build "$ROOT/DmOffice.Tests" -v q >/dev/null

start() {
  DMOFFICE_DATA="$DATA" ASPNETCORE_URLS="$BASE" dotnet run --project "$ROOT/DmOffice.Server" --no-build >"$DATA.log" 2>&1 &
  SERVER=$!
  for _ in $(seq 1 60); do curl -sf "$BASE/api/auth/state" >/dev/null && return; sleep 1; done
  echo "Server startet nicht, siehe $DATA.log"; exit 1
}
stop() { kill "$SERVER" 2>/dev/null || true; wait "$SERVER" 2>/dev/null || true; sleep 1; }
trap stop EXIT

start
curl -sf -X POST "$BASE/api/auth/setup" -H 'Content-Type: application/json' \
  -d "{\"login\":\"alex\",\"displayName\":\"Alex Berger\",\"password\":\"$PASS\"}" >/dev/null
stop
# Mails ohne IMAP-Server direkt in die Datenbank (Server dabei aus)
DMOFFICE_DOCS_BLOG=1 DMOFFICE_DOCS_SEED="$DATA" dotnet test "$ROOT/DmOffice.Tests" --no-build --filter "FullyQualifiedName~DocsSeed" -v q >/dev/null
start
node seed.mjs "$BASE" "$PASS"
node blog-seed.mjs "$BASE" "$PASS"
node blog-shots.mjs "$BASE" "$PASS" "$BROWSER" ${ONLY:-}
echo "Bilder in docs/img/blog"
