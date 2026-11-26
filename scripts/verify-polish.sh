#!/usr/bin/env bash
# Proves the two 0.5.1.0 fixes end to end on a stock Jellyfin container:
#
#   * "no artifacts at all": a failed or cancelled export leaves the destination
#     tree exactly as it was, with no empty folder skeleton, while a completed
#     export keeps its layout and the (n) de-duplication naming;
#   * a rejected configuration save names its reason: the endpoint's 400 body is
#     Jellyfin's own fixed text, so the plugin's Validate route carries the
#     message and the settings page shows it in the browser.
#
# Usage:
#   scripts/verify-polish.sh [port]
#   RUN_UI=0 scripts/verify-polish.sh [port]    # skip the browser phase
#
# Cases, all through the plugin's own API on a stock Jellyfin container:
#   * forced FFmpeg failure (profile directory on a 2 MB tmpfs): the job fails
#     with the FFmpeg error and /exports-small holds no directory and no file;
#   * cancel mid-export: the job is cancelled and its destination directory is
#     gone again;
#   * a completed export keeps Movies/{Title} ({Year})/{Title} ({Year}).mkv, and
#     a second export of the same item lands as ... (1).mkv;
#   * no *.partial file anywhere under the outputs.
set -euo pipefail

PORT="${1:-18110}"
MANIFEST_PORT="${MANIFEST_PORT:-18111}"
IMAGE="${IMAGE:-jellyfin/jellyfin:12.4}"
NAME="${NAME:-jfx-cleanup-qa}"
WORK="${WORK:-/tmp/jfx-cleanup-qa-run}"
PLUGIN_NAME="Jellyfin Exporter"
PLUGIN_GUID="b0473c79-36c1-4386-9006-f941031bbea2"
API="http://127.0.0.1:${PORT}"
ADMIN_PASSWORD="cleanup-verify-pw"
DEVICE_ID="$(python3 -c 'import uuid;print(uuid.uuid4())')"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION="$(python3 - "${ROOT}/Directory.Build.props" <<'PY'
import re, sys
print(re.search(r"<Version>([^<]+)</Version>", open(sys.argv[1]).read()).group(1))
PY
)"

say() { printf '\n== %s\n' "$*"; }
fail() { printf 'FAIL: %s\n' "$*" >&2; docker logs --tail 80 "$NAME" >&2 || true; exit 1; }

cleanup() {
  if [ -n "${SERVER_PID:-}" ]; then kill "$SERVER_PID" 2>/dev/null || true; fi
}
trap cleanup EXIT

wait_for_api() {
  local ok=0
  for _ in $(seq 1 180); do
    if curl -fsS -m 3 "${API}/System/Info/Public" >/dev/null 2>&1; then
      ok=$((ok + 1))
      if [ "$ok" -ge 2 ]; then
        sleep 3
        return 0
      fi
    else
      ok=0
    fi
    sleep 2
  done
  fail "Jellyfin did not come back on ${API}"
}

login() {
  local token=""
  for _ in $(seq 1 30); do
    token="$(curl -sS -m 5 -X POST "${API}/Users/AuthenticateByName" \
      -H "Authorization: MediaBrowser Client=\"cleanup-verify\", Device=\"cli\", DeviceId=\"${DEVICE_ID}\", Version=\"1.0.0\"" \
      -H 'Content-Type: application/json' \
      -d "{\"Username\":\"qaadmin\",\"Pw\":\"${ADMIN_PASSWORD}\"}" 2>/dev/null |
      python3 -c 'import json,sys
try:
    print(json.load(sys.stdin).get("AccessToken") or "")
except Exception:
    print("")')"
    if [ -n "$token" ]; then
      TOKEN="$token"
      HDR="Authorization: MediaBrowser Client=\"cleanup-verify\", Device=\"cli\", DeviceId=\"${DEVICE_ID}\", Version=\"1.0.0\", Token=\"${token}\""
      return 0
    fi
    sleep 2
  done
  fail "could not sign in as qaadmin on ${API}"
}

jobs() {
  # jobs [status-filter] -> STATUS|PERCENT|NAME|ID|PATH-OR-ERROR
  curl -fsS -m 15 "${API}/JellyfinExporter/Jobs${1:+?status=$1}" -H "$HDR" |
    python3 -c '
import json, sys
for job in json.load(sys.stdin)["Jobs"]:
    print("{}|{}|{}|{}|{}".format(job["Status"], job["ProgressPercent"], job["ItemName"], job["Id"],
                                 job.get("OutputPath") or job.get("ErrorMessage") or ""))
'
}

jobs_json() { curl -fsS -m 15 "${API}/JellyfinExporter/Jobs${1:+?status=$1}" -H "$HDR"; }

wait_for_no_active() {
  for _ in $(seq 1 240); do
    if [ -z "$(jobs active)" ]; then return 0; fi
    sleep 2
  done
  fail "an export never finished"
}

# tree <container-path> -> every directory and file below it, one per line
tree() { docker exec "$NAME" find "$1" -mindepth 1 2>/dev/null | sort || true; }

assert_empty_tree() {
  # assert_empty_tree <container-path> <label>
  local listing
  listing="$(tree "$1")"
  if [ -n "$listing" ]; then
    printf 'leftover entries under %s:\n%s\n' "$1" "$listing" >&2
    fail "$2 left artifacts behind"
  fi
  echo "$2 left no artifacts under $1"
}

say "building and packaging ${VERSION}"
export PATH="$HOME/.dotnet:$PATH"
(cd "$ROOT" && dotnet build -c Release >/dev/null)
(cd "$ROOT" && python3 scripts/package.py >/dev/null)
ZIP="${ROOT}/artifacts/jellyfin-exporter_${VERSION}.zip"
MD5="$(md5sum "$ZIP" | cut -d' ' -f1)"
test -f "$ZIP"

say "starting ${IMAGE} as ${NAME} on ${PORT}"
docker rm -f "$NAME" >/dev/null 2>&1 || true
if [ -d "$WORK" ]; then
  rm -rf "$WORK" 2>/dev/null || sudo -n rm -rf "$WORK"
fi
mkdir -p "$WORK/config" "$WORK/cache" "$WORK/exports" "$WORK/exports2" "$WORK/media/Movies/Test Movie (2020)"
# 120 s of 720p: long enough to cancel mid-encode, large enough to run the
# 2 MB tmpfs out of space.
ffmpeg -hide_banner -loglevel error -y \
  -f lavfi -i "testsrc2=size=1280x720:rate=30:duration=120" \
  -f lavfi -i "sine=frequency=440:duration=120" \
  -c:v libx264 -preset ultrafast -pix_fmt yuv420p -c:a aac -shortest \
  "$WORK/media/Movies/Test Movie (2020)/Test Movie (2020).mkv"

python3 -m http.server "$MANIFEST_PORT" --bind 0.0.0.0 --directory "$(dirname "$ZIP")" \
  >"${WORK}/httpd.log" 2>&1 &
SERVER_PID=$!
sleep 1
cat >"$(dirname "$ZIP")/cleanup-verify-manifest.json" <<EOF
[
  {
    "guid": "${PLUGIN_GUID}",
    "name": "${PLUGIN_NAME}",
    "description": "Export movies, shows and seasons with FFmpeg profiles.",
    "overview": "Export media with FFmpeg profiles.",
    "owner": "jakepi-bot",
    "category": "General",
    "imageUrl": "",
    "versions": [
      {
        "version": "${VERSION}",
        "changelog": "cleanup verification build",
        "targetAbi": "12.4.0.0",
        "sourceUrl": "http://host.docker.internal:${MANIFEST_PORT}/$(basename "$ZIP")",
        "checksum": "${MD5}",
        "timestamp": "2026-01-01T00:00:00Z"
      }
    ]
  }
]
EOF

docker run -d --name "$NAME" \
  --add-host host.docker.internal:host-gateway \
  -p "127.0.0.1:${PORT}:8096" \
  -v "$WORK/config:/config" \
  -v "$WORK/cache:/cache" \
  -v "$WORK/media:/media:ro" \
  -v "$WORK/exports:/exports" \
  -v "$WORK/exports2:/exports2" \
  --tmpfs /exports-small:size=2m \
  "$IMAGE" >/dev/null

for _ in $(seq 1 90); do
  if [ "$(curl -sS -o /dev/null -w '%{http_code}' -m 2 "${API}/Startup/Configuration")" = "200" ]; then break; fi
  sleep 2
done
test "$(curl -sS -o /dev/null -w '%{http_code}' -m 2 "${API}/Startup/Configuration")" = "200"

say "completing the startup wizard and signing in"
curl -fsS -o /dev/null -X POST "${API}/Startup/Configuration" -H 'Content-Type: application/json' \
  -d '{"UICulture":"en-US","MetadataCountryCode":"US","PreferredMetadataLanguage":"en"}'
curl -fsS "${API}/Startup/User" >/dev/null
curl -fsS -o /dev/null -X POST "${API}/Startup/User" -H 'Content-Type: application/json' \
  -d "{\"Name\":\"qaadmin\",\"Password\":\"${ADMIN_PASSWORD}\"}"
curl -fsS -o /dev/null -X POST "${API}/Startup/Complete"

login
USER_ID="$(curl -fsS -m 10 "${API}/Users/Me" -H "$HDR" | python3 -c 'import json,sys;print(json.load(sys.stdin)["Id"])')"

say "adding the Movies library"
curl -fsS -o /dev/null -X POST "${API}/Library/VirtualFolders?name=Movies&collectionType=movies&refreshLibrary=true" \
  -H "$HDR" -H 'Content-Type: application/json' \
  -d '{"LibraryOptions":{"PathInfos":[{"Path":"/media/Movies"}],"Enabled":true,"EnableRealtimeMonitor":true}}'
curl -fsS -o /dev/null -X POST "${API}/Library/Refresh" -H "$HDR"
ITEM_ID=""
for _ in $(seq 1 60); do
  ITEM_ID="$(curl -fsS "${API}/Items?Recursive=true&IncludeItemTypes=Movie&userId=${USER_ID}" -H "$HDR" |
    python3 -c 'import json,sys;i=json.load(sys.stdin)["Items"];print(i[0]["Id"] if i else "")')"
  [ -n "$ITEM_ID" ] && break
  sleep 2
done
[ -n "$ITEM_ID" ] || fail "no movie was scanned into the library"
say "library item ${ITEM_ID}"

say "installing ${PLUGIN_NAME} ${VERSION} from a local manifest"
REPO_URL="http://host.docker.internal:${MANIFEST_PORT}/cleanup-verify-manifest.json"
curl -fsS -o /dev/null -X POST "${API}/Repositories" -H "$HDR" -H 'Content-Type: application/json' \
  -d "[{\"Name\":\"cleanup-verify\",\"Url\":\"${REPO_URL}\",\"Enabled\":true}]"
TASK_ID="$(curl -fsS "${API}/ScheduledTasks" -H "$HDR" |
  python3 -c 'import json,sys;tasks=json.load(sys.stdin);print(next(t["Id"] for t in tasks if t["Key"] in ("PluginUpdates","RefreshPlugins")))')"
curl -fsS -o /dev/null -X POST "${API}/ScheduledTasks/Running/${TASK_ID}" -H "$HDR"
for _ in $(seq 1 30); do
  if curl -fsS "${API}/Packages" -H "$HDR" |
    python3 -c 'import json,sys;sys.exit(0 if any(p["name"]=="'"${PLUGIN_NAME}"'" for p in json.load(sys.stdin)) else 1)'; then break; fi
  sleep 2
done
ENC_REPO="$(python3 -c 'import sys,urllib.parse;print(urllib.parse.quote(sys.argv[1],safe=""))' "$REPO_URL")"
CODE="$(curl -sS -o /dev/null -w '%{http_code}' -X POST \
  "${API}/Packages/Installed/Jellyfin%20Exporter?version=${VERSION}&repositoryUrl=${ENC_REPO}" -H "$HDR")"
[ "$CODE" = "204" ] || fail "plugin install returned HTTP ${CODE}"

say "restarting so the plugin loads"
docker restart "$NAME" >/dev/null
wait_for_api
login
for attempt in $(seq 1 120); do
  CODE="$(curl -sS -o "${WORK}/plugins.json" -w '%{http_code}' -m 10 "${API}/Plugins" -H "$HDR" 2>/dev/null || true)"
  if python3 -c 'import json,sys;json.load(open(sys.argv[1]))' "${WORK}/plugins.json" >/dev/null 2>&1; then break; fi
  [ $((attempt % 10)) -eq 0 ] && echo "   /Plugins attempt ${attempt}: HTTP ${CODE}"
  sleep 3
done
python3 - "${WORK}/plugins.json" "${PLUGIN_NAME}" "${VERSION}" <<'PY'
import json, sys
plugins = json.load(open(sys.argv[1]))
match = [p for p in plugins if p["Name"] == sys.argv[2]]
assert match, f"{sys.argv[2]} is not loaded"
assert match[0]["Version"] == sys.argv[3], match[0]["Version"]
assert match[0]["Status"].lower() == "active", match[0]["Status"]
print("plugin active", match[0]["Version"])
PY

say "configuring three profiles: tmpfs (fails), slow (cancelled), normal"
P_FAIL="$(python3 -c 'import uuid;print(uuid.uuid4())')"
P_CANCEL="$(python3 -c 'import uuid;print(uuid.uuid4())')"
P_OK="$(python3 -c 'import uuid;print(uuid.uuid4())')"
python3 - "$P_FAIL" "$P_CANCEL" "$P_OK" >"${WORK}/config.json" <<'PY'
import json, sys
fail, cancel, ok = sys.argv[1:4]
print(json.dumps({
    "Profiles": [
        {"Id": fail, "Name": "Tmpfs 1080p", "Format": "H264",
         "Resolution": "Resolution1080p", "AudioChannels": "Stereo",
         "OutputDirectory": "/exports-small", "Enabled": True},
        {"Id": cancel, "Name": "Cancel 1080p", "Format": "H264",
         "Resolution": "Resolution1080p", "AudioChannels": "Stereo",
         "OutputDirectory": "/exports2", "Enabled": True},
        {"Id": ok, "Name": "Cleanup 480p", "Format": "H264",
         "Resolution": "Resolution480p", "AudioChannels": "Stereo",
         "OutputDirectory": "/exports", "Enabled": True},
    ],
    "DefaultProfileId": ok,
    "MaxConcurrentExports": 1,
    "JobHistoryLimit": 50,
    "NotifyOnCompletion": False,
    "NotifyOnFailure": False,
}))
PY
curl -fsS -o /dev/null -X POST "${API}/Plugins/${PLUGIN_GUID}/Configuration" -H "$HDR" \
  -H 'Content-Type: application/json' --data-binary @"${WORK}/config.json"

export_item() {
  # export_item <profile-id> -> prints the queued job count
  curl -fsS -m 30 -X POST "${API}/JellyfinExporter/Library/Export" -H "$HDR" \
    -H 'Content-Type: application/json' \
    -d "{\"itemIds\":[\"${ITEM_ID}\"],\"profileId\":\"$1\"}" |
    python3 -c 'import json,sys;p=json.load(sys.stdin);print(p.get("Queued", p))'
}

say "before the failure case, /exports-small and /exports2 are empty"
assert_empty_tree /exports-small "precondition"
assert_empty_tree /exports2 "precondition"

# --- Case 1: forced FFmpeg failure (no space on a 2 MB tmpfs).
say "case 1: export into a 2 MB tmpfs so FFmpeg fails"
export_item "$P_FAIL" >/dev/null
wait_for_no_active
FAILED="$(jobs failed | head -1)"
printf '%s\n' "$FAILED"
printf '%s' "$FAILED" | grep -q 'FFmpeg exited with code' || fail "the tmpfs export did not fail with an FFmpeg error: ${FAILED}"
assert_empty_tree /exports-small "the failed export"
# The failed job must still be reported as failed, with its error, in the API.
jobs failed | grep -q '^Failed|' || fail "the failed job is missing from the job list"

# --- Case 2: cancel mid-export.
say "case 2: cancel an export while FFmpeg is running"
export_item "$P_CANCEL" >/dev/null
CANCEL_JOB=""
for _ in $(seq 1 60); do
  CANCEL_JOB="$(jobs active | awk -F'|' '$1=="Running"{print $4; exit}')"
  [ -n "$CANCEL_JOB" ] && break
  sleep 1
done
[ -n "$CANCEL_JOB" ] || fail "no export became active to cancel"
# Give FFmpeg a moment to create the destination folder and the partial file.
for _ in $(seq 1 60); do
  if docker exec "$NAME" find /exports2 -name '*.partial' | grep -q .; then break; fi
  sleep 1
done
docker exec "$NAME" find /exports2 -mindepth 1 | sed 's/^/   before cancel: /'
curl -fsS -o /dev/null -X POST "${API}/JellyfinExporter/Jobs/${CANCEL_JOB}/cancel" -H "$HDR"
for _ in $(seq 1 60); do
  if jobs cancelled | grep -q '^Cancelled|'; then break; fi
  sleep 1
done
jobs cancelled | head -2
jobs cancelled | grep -q '^Cancelled|' || fail "the cancelled job never reported Cancelled"
assert_empty_tree /exports2 "the cancelled export"

# --- Case 3: a completed export keeps its layout and de-duplicates.
say "case 3: a completed export keeps its folders, and a second copy gets (1)"
export_item "$P_OK" >/dev/null
wait_for_no_active
export_item "$P_OK" >/dev/null
wait_for_no_active
jobs completed | head -4
PATHS="$(jobs completed | awk -F'|' '{print $5}')"
FIRST="$(printf '%s\n' "$PATHS" | grep -v ' (1)\.mkv$' | head -1)"
DEDUP="$(printf '%s\n' "$PATHS" | grep ' (1)\.mkv$' | head -1)"
[ -n "$FIRST" ] || fail "the completed export has no output path: ${PATHS}"
[ -n "$DEDUP" ] || fail "the second export was not de-duplicated: ${PATHS}"
printf 'first:     %s\nsecond:    %s\n' "$FIRST" "$DEDUP"
docker exec "$NAME" test -f "$FIRST" || fail "the completed output file is missing: ${FIRST}"
docker exec "$NAME" test -f "$DEDUP" || fail "the de-duplicated output file is missing: ${DEDUP}"
printf '%s' "$FIRST" | grep -qE '/exports/Movies/Test Movie \(2020\)/Test Movie \(2020\)\.mkv$' ||
  fail "the completed export does not follow the documented layout: ${FIRST}"
docker exec "$NAME" test -d "$(dirname "$FIRST")" || fail "the movie folder is missing: $(dirname "$FIRST")"

say "no partial files anywhere"
PARTIALS="$(docker exec "$NAME" find /exports /exports2 /exports-small -name '*.partial' 2>/dev/null || true)"
[ -z "$PARTIALS" ] || fail "partial files were left behind: ${PARTIALS}"

say "the destination trees after all three cases"
docker exec "$NAME" sh -c 'for d in /exports /exports2 /exports-small; do echo "-- $d"; find "$d" -mindepth 1 | sort; done'

# --- Case 4: a rejected configuration save.
# Jellyfin's own configuration endpoint answers 400 with a fixed body, so this
# proves that the body is not controllable from the plugin and that the plugin's
# own Validate route (which the settings page now calls when a save fails)
# carries the reason.
say "case 4: an invalid profile is rejected by the configuration endpoint"
UNWRITABLE="/proc/jfx-export-nope"
python3 - "${WORK}/config.json" "${UNWRITABLE}" >"${WORK}/config-invalid.json" <<'PY'
import json, sys
config = json.load(open(sys.argv[1]))
config["Profiles"][0]["OutputDirectory"] = sys.argv[2]
json.dump(config, sys.stdout)
PY
CODE="$(curl -sS -o "${WORK}/reject-body.txt" -w '%{http_code}' -X POST \
  "${API}/Plugins/${PLUGIN_GUID}/Configuration" -H "$HDR" \
  -H 'Content-Type: application/json' --data-binary @"${WORK}/config-invalid.json")"
BODY="$(cat "${WORK}/reject-body.txt")"
printf 'HTTP %s, body: %s\n' "$CODE" "$BODY"
[ "$CODE" = "400" ] || fail "an invalid profile was not rejected (HTTP ${CODE})"

say "the stored configuration still has the valid profiles"
curl -fsS "${API}/Plugins/${PLUGIN_GUID}/Configuration" -H "$HDR" |
  python3 -c 'import json,sys;c=json.load(sys.stdin);dirs=[p.get("OutputDirectory") for p in (c.get("Profiles") or [])];assert dirs==["/exports-small","/exports2","/exports"], dirs;print("stored directories:", dirs)'

say "the plugin's Validate route reports the reason"
python3 - "${WORK}/config-invalid.json" >"${WORK}/validate-body.json" <<'PY'
import json, sys
config = json.load(open(sys.argv[1]))
json.dump({"Profiles": config["Profiles"], "MaxConcurrentExports": config["MaxConcurrentExports"],
           "JobHistoryLimit": config["JobHistoryLimit"], "DefaultProfileId": config["DefaultProfileId"]}, sys.stdout)
PY
curl -fsS -X POST "${API}/JellyfinExporter/Profiles/Validate" -H "$HDR" \
  -H 'Content-Type: application/json' --data-binary @"${WORK}/validate-body.json" >"${WORK}/validate-result.json"
cat "${WORK}/validate-result.json"
python3 - "${WORK}/validate-result.json" <<'PY'
import json, sys
result = json.load(open(sys.argv[1]))
assert result["Valid"] is False, result
issues = result["Issues"]
assert issues, result
assert any(i["Field"] == "OutputDirectory" for i in issues), issues
print("validator message:", issues[0]["Message"])
PY

say "the server log names the same reason"
LOG_LINE="$(docker logs "$NAME" 2>&1 | grep 'Export configuration rejected:' | tail -1 || true)"
[ -n "$LOG_LINE" ] || fail "the server log does not record the rejected configuration"
printf '%s\n' "$LOG_LINE"

if [ "${RUN_UI:-1}" = "1" ]; then
  say "browser: the settings page shows the validator message after a rejected save"
  NODE_PATH="${NODE_PATH:-$HOME/career-ops/node_modules}" \
  API="${API}" JF_USER="qaadmin" JF_PASS="${ADMIN_PASSWORD}" SHOTS="${WORK}" \
  UNWRITABLE="${UNWRITABLE}" BEFORE_PROFILES="3" \
    node "${ROOT}/scripts/verify-config-error-ui.js" ||
    fail "the browser-level configuration-error checks failed"
fi

say "PASS: failed and cancelled exports leave no artifacts, completions keep their layout, and a rejected save names its reason"
