#!/usr/bin/env bash
# Wie make-blog.sh (eigene Datenbank, erfundene Beispieldaten), aber ohne Web-Screenshots:
# der Server laeuft danach weiter (z. B. fuer Android-Screenshots, siehe android-shots.ps1).
#   PORT=5097 tools/docs/serve-blog.sh      (Standard 5097; Daten in tools/docs/.data-serve)
# Das Passwort von alex steht danach in .data-serve/pass.txt, die PID in .data-serve/server.pid.
set -euo pipefail
cd "$(dirname "$0")"
ROOT="$(cd ../.. && pwd)"
PORT="${PORT:-5097}"
DATA="$PWD/.data-serve"
BASE="http://127.0.0.1:$PORT"
PASS="Doku-$(date +%s)-$RANDOM"

if curl -s -o /dev/null "$BASE/api/auth/state"; then echo "Port $PORT ist belegt"; exit 1; fi
[ -d node_modules ] || npm install --no-audit --no-fund
rm -rf "$DATA"
dotnet build "$ROOT/DmOffice.Server" -v q >/dev/null
dotnet build "$ROOT/DmOffice.Tests" -v q >/dev/null

start() {
  DMOFFICE_DATA="$DATA" ASPNETCORE_URLS="$BASE" nohup dotnet run --project "$ROOT/DmOffice.Server" --no-build >"$DATA.log" 2>&1 &
  SERVER=$!
  for _ in $(seq 1 60); do curl -sf "$BASE/api/auth/state" >/dev/null && return; sleep 1; done
  echo "Server startet nicht, siehe $DATA.log"; exit 1
}
stop() { kill "$SERVER" 2>/dev/null || true; wait "$SERVER" 2>/dev/null || true; sleep 1; }

start
curl -sf -X POST "$BASE/api/auth/setup" -H 'Content-Type: application/json' \
  -d "{\"login\":\"alex\",\"displayName\":\"Alex Berger\",\"password\":\"$PASS\"}" >/dev/null
stop
DMOFFICE_DOCS_BLOG=1 DMOFFICE_DOCS_SEED="$DATA" dotnet test "$ROOT/DmOffice.Tests" --no-build --filter "FullyQualifiedName~DocsSeed" -v q >/dev/null
start
node seed.mjs "$BASE" "$PASS"
node blog-seed.mjs "$BASE" "$PASS"
echo "$PASS" > "$DATA/pass.txt"
echo "$SERVER" > "$DATA/server.pid"
echo "Server laeuft auf $BASE (PID $SERVER), Passwort in $DATA/pass.txt"
