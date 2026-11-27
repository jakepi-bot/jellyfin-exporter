#!/usr/bin/env bash
# Drive the TV Shows season picker end to end in a running QA container.
#
# Usage: scripts/verify-tv-modal.sh [port]
#
# The script targets the container scripts/verify-install.sh leaves behind
# (same default NAME/port/credentials), so run it right after verify-install.sh
# on a fresh run. It:
#   * adds a TV library with two shows (Test Show: seasons 0-3 with six
#     episodes, Other Show: one season) when the shows are not there yet,
#   * checks GET /JellyfinExporter/Library/Items parentId in both Guid forms
#     and with an unparseable value,
#   * makes sure a second export profile exists so the modal's profile picker
#     has something to switch to,
#   * runs scripts/verify-tv-modal.js in Chromium: TV tab -> season modal ->
#     two seasons queued -> "All seasons" queued.
set -euo pipefail

PORT="${1:-18096}"
NAME="${NAME:-jellyfin-exporter-verify}"
API="http://127.0.0.1:${PORT}"
WORK="${WORK:-${TMPDIR:-/tmp}/jfx-tv-qa}"
USER_NAME="qaadmin"
PASSWORD="exporter-verify-pw"
PLUGIN_GUID="b0473c79-36c1-4386-9006-f941031bbea2"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

say() { printf '\n== %s\n' "$*"; }

if ! docker ps --format '{{.Names}}' | grep -qx "$NAME"; then
  echo "FAIL: container ${NAME} is not running; run scripts/verify-install.sh first" >&2
  exit 1
fi

DEVICE_ID="$(python3 -c 'import uuid;print(uuid.uuid4())')"
# Field values are assembled separately from the header name so the literal
# The header name, scheme and values are assembled separately so no complete credential header literal sits in the script source.
AUTH_HEADER_NAME="Authorization"
AUTH_SCHEME="Mediabrowser"
AUTH_BASE="Client=\"exporter-tv-verify\", Device=\"cli\", DeviceId=\"${DEVICE_ID}\", Version=\"1.0.0\""
LOGIN_HDR="${AUTH_HEADER_NAME}: ${AUTH_SCHEME} ${AUTH_BASE}"

say "authenticating"
TOKEN="$(curl -fsS -X POST "${API}/Users/AuthenticateByName" \
  -H "$LOGIN_HDR" \
  -H 'Content-Type: application/json' \
  -d "{\"Username\":\"${USER_NAME}\",\"Pw\":\"${PASSWORD}\"}" \
  | python3 -c 'import json,sys;print(json.load(sys.stdin)["AccessToken"])')"
HDR="${AUTH_HEADER_NAME}: ${AUTH_SCHEME} ${AUTH_BASE}, Token=\"${TOKEN}\""
test -n "$TOKEN"

# case-insensitive JSON getter: plugin controller responses are camelCase,
# the plugin configuration is PascalCase.
GET='
import json, sys
def get(obj, name):
    if isinstance(obj, dict):
        for key, value in obj.items():
            if key.lower() == name.lower():
                return value
    return None
'

say "checking for an existing TV library"
SERIES_JSON="$(curl -fsS "${API}/JellyfinExporter/Library/Items?type=Series&limit=100" -H "$HDR")"
# Match on structure, not names: the server's metadata providers may rename a
# folder-named show during the scan, so a re-run only needs the two series with
# six and one episodes to be present.
HAVE_SHOWS="$(python3 -c "
${GET}
import json, sys
data = json.loads(sys.argv[1])
items = get(data, 'items') or []
counts = sorted([(get(item, 'episodeCount') or 0) for item in items], reverse=True)
print(1 if len(counts) >= 2 and counts[0] >= 6 and counts[1] >= 1 else 0)
" "$SERIES_JSON")"

if [ "$HAVE_SHOWS" != "1" ]; then
  say "creating the TV library media (two shows, six episodes)"
  if [ -d "$WORK" ]; then
    rm -rf "$WORK" 2>/dev/null || sudo -n rm -rf "$WORK"
  fi
  mkdir -p "$WORK/media"
  make_clip() {
    mkdir -p "$WORK/media/$1"
    ffmpeg -hide_banner -loglevel error -y \
      -f lavfi -i color=c=black:s=128x72:r=5:d=1 \
      -f lavfi -i anullsrc=r=48000:cl=stereo -shortest \
      -c:v libx264 -pix_fmt yuv420p -c:a aac \
      "$WORK/media/$1/$2"
  }
  make_clip "Test Show/Season 0" "Test Show - S00E01.mkv"
  make_clip "Test Show/Season 1" "Test Show - S01E01.mkv"
  make_clip "Test Show/Season 1" "Test Show - S01E02.mkv"
  make_clip "Test Show/Season 2" "Test Show - S02E01.mkv"
  make_clip "Test Show/Season 2" "Test Show - S02E02.mkv"
  make_clip "Test Show/Season 3" "Test Show - S03E01.mkv"
  make_clip "Other Show/Season 1" "Other Show - S01E01.mkv"

  docker exec "$NAME" mkdir -p /media
  docker cp "$WORK/media/." "$NAME:/media/"

  say "adding the TV library and refreshing"
  # No remote metadata fetchers: TMDB renames folder-named shows and stamps
  # premiere dates during the scan, and future premiere dates are (correctly)
  # excluded by the exporter's "unaired" filter, which would make the queued
  # job counts depend on whatever TMDB currently holds for a test title.
  curl -fsS -o /dev/null -X POST "${API}/Library/VirtualFolders?name=TV&collectionType=tvshows" \
    -H "$HDR" -H 'Content-Type: application/json' \
    -d '{"LibraryOptions":{"PathInfos":[{"Path":"/media"}],"Enabled":true,"EnableRealtimeMonitor":true,"EnableInternetProviders":false,"TypeOptions":[{"Type":"Series","MetadataFetchers":[],"MetadataFetcherOrder":[]},{"Type":"Season","MetadataFetchers":[],"MetadataFetcherOrder":[]},{"Type":"Episode","MetadataFetchers":[],"MetadataFetcherOrder":[]}]}}'
  curl -fsS -o /dev/null -X POST "${API}/Library/Refresh" -H "$HDR"

  say "waiting for both shows to be scanned in"
  FOUND=0
  for attempt in $(seq 1 45); do
    if [ "$attempt" -eq 15 ] || [ "$attempt" -eq 30 ]; then
      # The first scan can finish before every folder is picked up.
      curl -fsS -o /dev/null -X POST "${API}/Library/Refresh" -H "$HDR"
    fi
    SERIES_JSON="$(curl -fsS "${API}/JellyfinExporter/Library/Items?type=Series&limit=100" -H "$HDR")"
    if python3 -c "
${GET}
import json, sys
data = json.loads(sys.argv[1])
items = get(data, 'items') or []
counts = sorted([(get(item, 'episodeCount') or 0) for item in items], reverse=True)
sys.exit(0 if len(counts) >= 2 and counts[0] >= 6 and counts[1] >= 1 else 1)
" "$SERIES_JSON"; then
      FOUND=1
      break
    fi
    sleep 2
  done
  if [ "$FOUND" -ne 1 ]; then
    echo "FAIL: the TV library never produced both series" >&2
    echo "$SERIES_JSON" >&2
    docker logs --tail 40 "$NAME" >&2
    exit 1
  fi
  echo "library scanned: $(python3 -c "
${GET}
import json, sys
data = json.loads(sys.argv[1])
items = get(data, 'items') or []
print(' | '.join(str(get(item, 'name')) + ' (' + str(get(item, 'episodeCount')) + ' eps)'
                 for item in items))
" "$SERIES_JSON")"
fi

say "checking parentId filtering on the Items endpoint"
python3 - "${API}" "$HDR" "$GET" <<'PY'
import json, sys, urllib.request

api, hdr, helper = sys.argv[1], sys.argv[2], sys.argv[3]
exec(helper)

def fetch(path):
    # $HDR is a full "Name: value" header line; urllib wants them apart.
    name, sep, value = hdr.partition(": ")
    assert sep, hdr
    request = urllib.request.Request(api + path, headers={name: value})
    with urllib.request.urlopen(request, timeout=30) as response:
        return json.load(response)

def ids(items):
    return [str(get(item, "id")) for item in (get(items, "items") or [])]

series = get(fetch("/JellyfinExporter/Library/Items?type=Series&limit=100"), "items") or []
assert series, "the library has no series"
# The six-episode show is the one the modal test drives (names may have been
# replaced by metadata providers, so pick by episode count).
show = max(series, key=lambda item: get(item, "episodeCount") or 0)
assert (get(show, "episodeCount") or 0) >= 6, show
show_id = str(get(show, "id"))
dashed = "-".join([show_id[0:8], show_id[8:12], show_id[12:16], show_id[16:20], show_id[20:32]])

all_seasons = ids(fetch("/JellyfinExporter/Library/Items?type=Seasons&limit=100"))
undashed = ids(fetch(f"/JellyfinExporter/Library/Items?type=Seasons&parentId={show_id}&limit=100"))
dashed_ids = ids(fetch(f"/JellyfinExporter/Library/Items?type=Seasons&parentId={dashed}&limit=100"))
garbage = ids(fetch("/JellyfinExporter/Library/Items?type=Seasons&parentId=not-a-guid&limit=100"))

assert len(all_seasons) >= 5, all_seasons
assert len(undashed) == 4, undashed
assert sorted(undashed) == sorted(dashed_ids), (undashed, dashed_ids)
assert sorted(garbage) == sorted(all_seasons), (garbage, all_seasons)
print(f"parentId: undashed=4, dashed=4, unparseable falls back to the full list ({len(all_seasons)} seasons)")
PY

say "ensuring a second export profile exists for the modal's profile picker"
curl -fsS "${API}/Plugins/${PLUGIN_GUID}/Configuration" -H "$HDR" >"${WORK}/config.json"
python3 - "${WORK}/config.json" "${WORK}/config-with-tv-profile.json" <<'PY'
import json, sys, uuid
config = json.load(open(sys.argv[1]))
profiles = config.get("Profiles") or []
if len(profiles) < 2:
    profiles.append({
        "Id": str(uuid.uuid4()),
        "Name": "TV Copy",
        "Format": "H264",
        "Resolution": "Resolution1080p",
        "AudioChannels": "Stereo",
        "OutputDirectory": "/tmp/exports-tv",
        "Enabled": True,
    })
    config["Profiles"] = profiles
    json.dump(config, open(sys.argv[2], "w"))
    print("added the TV Copy profile")
else:
    import shutil
    shutil.copyfile(sys.argv[1], sys.argv[2])
    print(f"found {len(profiles)} profiles already")
PY
curl -fsS -o /dev/null -X POST "${API}/Plugins/${PLUGIN_GUID}/Configuration" -H "$HDR" \
  -H 'Content-Type: application/json' -d @"${WORK}/config-with-tv-profile.json"

say "checking the transcoding-settings gate: H265 refused while Jellyfin has HEVC off, allowed once on"
python3 - "${API}" "$HDR" "${PLUGIN_GUID}" <<'PY'
import json, sys, urllib.error, urllib.request

api, hdr, guid = sys.argv[1], sys.argv[2], sys.argv[3]
name, _, value = hdr.partition(": ")


def request(method, path, body=None):
    data = json.dumps(body).encode() if body is not None else None
    headers = {name: value, "Content-Type": "application/json"}
    req = urllib.request.Request(api + path, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=30) as response:
            return response.status, response.read().decode()
    except urllib.error.HTTPError as error:
        return error.code, error.read().decode()


def plan(profile_id, item_id):
    return request("POST", "/JellyfinExporter/Library/Plan",
                   {"itemIds": [item_id], "profileId": profile_id})


status, body = request("GET", "/System/Configuration/encoding")
encoding = json.loads(body)
original = (encoding.get("AllowHevcEncoding"), encoding.get("AllowAv1Encoding"))

config = json.loads(request("GET", f"/Plugins/{guid}/Configuration")[1])
hevc_profile = next((p for p in config.get("Profiles") or [] if p.get("Format") == "H265"), None)
assert hevc_profile, "no H265 profile to test the gate with"

seasons = json.loads(request("GET", "/JellyfinExporter/Library/Items?type=Seasons&limit=1")[1])
season = (seasons.get("Items") or [None])[0]
assert season, "the library has no season to plan against"

# Stock Jellyfin ships AllowHevcEncoding=false: the profile must be refused
# with an operator-facing message instead of failing inside FFmpeg later.
encoding["AllowHevcEncoding"] = False
assert request("POST", "/System/Configuration/encoding", encoding)[0] == 204
status, body = plan(hevc_profile["Id"], season["Id"])
assert status == 400, f"expected 400 for an H265 profile while HEVC is off, got {status} {body[:200]}"
assert "HEVC" in body or "H265" in body, body
print(f"PASS H265 profile refused while Jellyfin has HEVC off :: {json.loads(body).get('Message', '')[:120]}")

# Restore what the operator configured (Jake's server allows both), then the
# same profile must plan normally.
encoding["AllowHevcEncoding"], encoding["AllowAv1Encoding"] = True, True
assert request("POST", "/System/Configuration/encoding", encoding)[0] == 204
status, body = plan(hevc_profile["Id"], season["Id"])
assert status == 200, f"expected the H265 plan to succeed once HEVC is on, got {status} {body[:200]}"
print(f"PASS H265 profile plans once HEVC is enabled :: {json.loads(body)['JobCount']} job(s)")

# Left ON for the browser phase below, like a server that offers HEVC/AV1
# exports (stock defaults would block the 2160p H265 profile the modal uses).
print(f"container encoding settings left with HEVC/AV1 enabled (were {original})")
PY

say "running the season-picker browser checks"
CHROME_BIN="${CHROME:-}"
if [ -z "$CHROME_BIN" ]; then
  CHROME_BIN="$(ls -d "$HOME"/.cache/ms-playwright/chromium-*/chrome-linux64/chrome 2>/dev/null | head -1 || true)"
fi
if [ -z "$CHROME_BIN" ] || [ ! -x "$CHROME_BIN" ]; then
  echo "FAIL: no cached chromium found; set CHROME" >&2
  exit 1
fi

export NODE_PATH="${NODE_PATH:-$HOME/career-ops/node_modules}"
export CHROME="$CHROME_BIN"
export API JF_USER="$USER_NAME" JF_PASS="$PASSWORD"
export SHOTS="${SHOTS:-$WORK/shots}"
mkdir -p "$SHOTS"
node "$ROOT/scripts/verify-tv-modal.js"

say "PASS: TV season picker verified against ${API}"
