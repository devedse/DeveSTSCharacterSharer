#!/bin/sh
# Runs the real sts-character-sync.sh on Linux or macOS against a locally started server: first
# download, nothing changed, a character update, sorting the game's mod list, the game running and
# the script updating itself. Used by CI; locally: dotnet publish the app, then
#     sh tests/test-sync-script.sh <publish folder>
set -eu

APP=$(cd "$1" && pwd)
REPO=$(cd "$(dirname "$0")/.." && pwd)
FIXTURE="$REPO/tests/DeveSTSCharacterSharer.Tests/Fixtures/TheJester-1.0.7z"
SERVER=http://localhost:5281
WORK=$(mktemp -d "${TMPDIR:-/tmp}/sts-sync-test.XXXXXX")
MODS="$WORK/steamapps/common/Slay the Spire 2/mods"
SCRIPT="$MODS/sts-character-sync.sh"
GAME_HOME="$WORK/home"
if [ "$(uname)" = Darwin ]; then
    SAVES="$GAME_HOME/Library/Application Support/SlayTheSpire2/steam/76561190000000001"
else
    SAVES="$GAME_HOME/.local/share/SlayTheSpire2/steam/76561190000000001"
fi
SETTINGS="$SAVES/settings.save"
mkdir -p "$MODS" "$SAVES" "$WORK/data"

PUBLIC_URL=$SERVER ASPNETCORE_URLS=$SERVER DATA_DIR="$WORK/data" dotnet "$APP/DeveSTSCharacterSharer.dll" >"$WORK/server.log" 2>&1 &
SERVER_PID=$!
GAME_PID=
cleanup() {
    kill "$SERVER_PID" ${GAME_PID:+"$GAME_PID"} 2>/dev/null || true
    rm -rf "$WORK"
}
trap cleanup EXIT

fail() {
    echo "FAILED: $1"
    echo "--- server log"
    cat "$WORK/server.log"
    exit 1
}
check() {
    message=$1
    shift
    if "$@"; then echo "ok: $message"; else fail "$message"; fi
}
output_has() { printf '%s\n' "$OUTPUT" | grep -q -- "$1"; }
output_count() { printf '%s\n' "$OUTPUT" | grep -c "$@" || true; }
mod_ids() { grep -o '"id": "[^"]*"' "$SETTINGS" | sed 's/"id": "//; s/"$//' | tr '\n' ' '; }
no_leftovers() { [ ! -e "$MODS/TheJester.sync-old" ] && [ ! -e "$MODS/TheJester.sync-new" ]; }
jester_version() { sed 's/.*"version": *"\([^"]*\)".*/\1/' "$MODS/TheJester/TheJester.json"; }

# Run like a player would: from anywhere, with the game's settings in their home folder.
run_sync() {
    STATUS=0
    OUTPUT=$(cd "$WORK" && HOME="$GAME_HOME" XDG_DATA_HOME='' sh "$SCRIPT" "$@" 2>&1) || STATUS=$?
    printf '%s\n' "$OUTPUT"
}

upload() { curl -fsS -T "$1" "$SERVER/api/upload/$(basename "$1")" >/dev/null || fail "upload of $1"; }

# Packs a copy of TheJester as <name> <version>, the way a modder would zip a new build.
make_mod() {
    rm -rf "$WORK/build"
    mkdir -p "$WORK/build/$1"
    for ext in dll json pck; do
        cp "$MODS/TheJester/TheJester.$ext" "$WORK/build/$1/$1.$ext"
    done
    sed "s/TheJester/$1/g; s/1\.0\.0/$2/" "$MODS/TheJester/TheJester.json" > "$WORK/build/$1/$1.json"
    tar -czf "$WORK/$1-$2.tar.gz" -C "$WORK/build" "$1"
    echo "$WORK/$1-$2.tar.gz"
}

i=0
until curl -fs "$SERVER/api/manifest.txt" >/dev/null 2>&1; do
    i=$((i + 1))
    [ "$i" -lt 60 ] || fail "the server did not start"
    sleep 1
done

upload "$FIXTURE"
curl -fsS -o "$SCRIPT" "$SERVER/sts-character-sync.sh"
cat > "$SETTINGS" <<'EOF'
{
  "fps_limit": 60,
  "mod_settings": {
    "mod_list": [
      {
        "id": "Zeta",
        "is_enabled": true,
        "source": "workshop"
      },
      {
        "id": "TheJester",
        "is_enabled": false,
        "source": "mods_directory"
      },
      {
        "id": "STS2-RitsuLib",
        "is_enabled": true,
        "source": "workshop"
      },
      {
        "id": "BaseLib",
        "is_enabled": true,
        "source": "workshop"
      }
    ],
    "play_with_mods": true
  },
  "volume": 0.5
}
EOF
cp "$SETTINGS" "$WORK/original.save"

echo
echo "=== first run downloads everything and sorts the mod list"
run_sync
check "exit code 0" [ "$STATUS" -eq 0 ]
check "TheJester is downloaded" output_has "downloading  TheJester"
check "TheJester/ is unpacked into the mods folder" [ -f "$MODS/TheJester/TheJester.pck" ]
HASH=$(curl -fsS "$SERVER/api/manifest.txt" | cut -d'|' -f1)
check "the marker holds the server hash" [ "$(cat "$MODS/TheJester/.character-sync")" = "$HASH" ]
check "nothing else is left in the mods folder" [ "$(find "$MODS" -mindepth 1 -maxdepth 1 | wc -l | tr -d ' ')" -eq 2 ]
check "an up to date script does not update itself" [ "$(output_count 'Updating this script')" -eq 0 ]
check "it says it sorted" output_has "Mod order: sorted"
check "libraries first, then the rest A-Z" [ "$(mod_ids)" = "BaseLib STS2-RitsuLib TheJester Zeta " ]
check "the rest of settings.save is untouched" grep -q '"volume": 0.5' "$SETTINGS"
check "TheJester stays disabled" grep -q '"is_enabled": false' "$SETTINGS"
check "the old order is kept as settings.save.bak" cmp -s "$SETTINGS.bak" "$WORK/original.save"

echo
echo "=== second run downloads nothing"
run_sync
check "exit code 0" [ "$STATUS" -eq 0 ]
check "TheJester is up to date" output_has "up to date   TheJester"
check "the mod list is already sorted" output_has "Mod order: already sorted"

echo
echo "=== a new character is downloaded and added to the mod list right away"
upload "$(make_mod Other 1.0.0)"
run_sync
check "exit code 0" [ "$STATUS" -eq 0 ]
check "Other is downloaded" output_has "downloading  Other"
check "Other is in the mod list before the game has seen it" [ "$(mod_ids)" = "BaseLib STS2-RitsuLib Other TheJester Zeta " ]
check "added enabled from the mods folder" [ "$(grep -A2 '"id": "Other"' "$SETTINGS" | tr -d ' \n')" = '"id":"Other","is_enabled":true,"source":"mods_directory"' ]

echo
echo "=== a new version replaces the old folder"
upload "$(make_mod TheJester 1.1.0)"
run_sync
check "exit code 0" [ "$STATUS" -eq 0 ]
check "the new version is downloaded" output_has "downloading  TheJester"
check "TheJester is now 1.1.0" [ "$(jester_version)" = "1.1.0" ]
check "no old folder is left behind" no_leftovers

echo
echo "=== nosort leaves the mod list alone"
cp "$WORK/original.save" "$SETTINGS"
run_sync nosort
check "exit code 0" [ "$STATUS" -eq 0 ]
check "nothing about the mod order" [ "$(output_count 'Mod order')" -eq 0 ]
check "settings.save is untouched" cmp -s "$SETTINGS" "$WORK/original.save"

echo
echo "=== the mod list isn't sorted while the game is running"
# Stand-in for the game: a process called SlayTheSpire2. A copy of sleep, or a script where sleep is a
# multi-call binary that picks the tool by its name (uutils on Ubuntu 26); Linux lists a script by its
# own name.
cp "$(command -v sleep)" "$WORK/SlayTheSpire2"
if ! "$WORK/SlayTheSpire2" 0 2>/dev/null; then
    printf '#!/bin/sh\nsleep 60\n' > "$WORK/SlayTheSpire2"
    chmod +x "$WORK/SlayTheSpire2"
fi
"$WORK/SlayTheSpire2" 60 &
GAME_PID=$!
sleep 1
run_sync
kill "$GAME_PID"
GAME_PID=
check "exit code 0" [ "$STATUS" -eq 0 ]
check "it says why" output_has "not sorted while Slay the Spire 2 is running"
check "settings.save is untouched" cmp -s "$SETTINGS" "$WORK/original.save"

echo
echo "=== an older script updates itself, then syncs with the new version"
cp "$SCRIPT" "$WORK/latest.sh"
# An "older version" of a different length: the running shell must not read on in the new file.
{ head -n 1 "$WORK/latest.sh"; printf '# an older version\n# with a few more lines\n# than the current one\n'; tail -n +2 "$WORK/latest.sh"; } > "$SCRIPT"
run_sync force
check "exit code 0" [ "$STATUS" -eq 0 ]
check "it updates itself exactly once" [ "$(output_count 'Updating this script')" -eq 1 ]
check "the script is now the latest version" cmp -s "$SCRIPT" "$WORK/latest.sh"
check "the sync ran exactly once, in the new version" [ "$(output_count 'Done:')" -eq 1 ]
check "arguments (force) are passed on to the new version" output_has "downloading  TheJester"
check "no broken lines were run" [ "$(output_count -e 'not found' -e 'Syntax error' -e 'unexpected')" -eq 0 ]
check "the mod list is sorted again" [ "$(mod_ids)" = "BaseLib STS2-RitsuLib Other TheJester Zeta " ]

echo
echo "All sync script checks passed."
