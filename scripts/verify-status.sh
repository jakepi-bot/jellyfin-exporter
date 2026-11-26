#!/usr/bin/env bash
# End-to-end check of the export status page, the persisted job history and the
# completion/failure notifications, against a stock Jellyfin container.
#
# Usage:
#   scripts/verify-status.sh [port]
#
# It builds and packages the plugin from this checkout, serves the zip to the
# container through a local manifest, adds a real (tiny) movie to a library,
# exports it, and then proves the acceptance criteria:
#   * the job history survives a server restart (real restart, real file);
#   * progress is reported while FFmpeg runs and the job lands in history with a
#     valid output path;
#   * a completed export raises its notification ("Export completed: ... -> path");
#   * a forced FFmpeg failure (output folder on a full disk) raises the failure
#     notification with the FFmpeg error;
#   * a job that was running when the server stopped comes back as failed
#     instead of staying in the running state.
#
# The browser-level checks (live progress bar, banner, activity page) live in
# scripts/verify-status-ui.js and run against the same container.
set -euo pipefail

PORT="${1:-18102}"
MANIFEST_PORT="${MANIFEST_PORT:-18103}"
IMAGE="${IMAGE:-jellyfin/jellyfin:12.4}"
NAME="${NAME:-jfx-status-qa}"
WORK="${WORK:-/tmp/jfx-status-qa-run}"
PLUGIN_NAME="Jellyfin Exporter"
PLUGIN_GUID="b0473c79-36c1-4386-9006-f941031bbea2"
# BasePlugin.DataFolderPath for this plugin inside the container.
PLUGIN_DATA_DIR="/config/plugins/Jellyfin.Plugin.Exporter"
API="http://127.0.0.1:${PORT}"
ADMIN_PASSWORD="status-verify-pw"
DEVICE_ID="$(python3 -c 'import uuid;print(uuid.uuid4())')"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION="$(python3 - "${ROOT}/Directory.Build.props" <<'PY'
import re, sys
print(re.search(r"<Version>([^<]+)</Version>", open(sys.argv[1]).read()).group(1))
PY
)"

say() { printf '\n== %s\n' "$*"; }
fail() { printf 'FAIL: %s\n' "$*" >&2; docker logs --tail 60 "$NAME" >&2 || true; exit 1; }

cleanup() {
  if [ -n "${SERVER_PID:-}" ]; then kill "$SERVER_PID" 2>/dev/null || true; fi
}
trap cleanup EXIT

wait_for_api() {
  # Requires two consecutive successes: right after a restart the API can answer
  # once and then drop connections while the server finishes starting.
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

# Signs in and (re)builds HDR. Called after every restart: the auth subsystem
# answers 401 with an empty body for a while after the API itself is up, and a
# fresh token removes any doubt about a token invalidated by the restart.
login() {
  local token=""
  for _ in $(seq 1 30); do
    token="$(curl -sS -m 5 -X POST "${API}/Users/AuthenticateByName" \
      -H "Authorization: MediaBrowser Client=\"status-verify\", Device=\"cli\", DeviceId=\"${DEVICE_ID}\", Version=\"1.0.0\"" \
      -H 'Content-Type: application/json' \
      -d "{\"Username\":\"qaadmin\",\"Pw\":\"${ADMIN_PASSWORD}\"}" 2>/dev/null |
      python3 -c 'import json,sys
try:
    print(json.load(sys.stdin).get("AccessToken") or "")
except Exception:
    print("")')"
    if [ -n "$token" ]; then
      TOKEN="$token"
      HDR="Authorization: MediaBrowser Client=\"status-verify\", Device=\"cli\", DeviceId=\"${DEVICE_ID}\", Version=\"1.0.0\", Token=\"${token}\""
      return 0
    fi
    sleep 2
  done
  fail "could not sign in as qaadmin on ${API}"
}

jobs() {
  # jobs [status-filter] -> one line per job: STATUS|PERCENT|NAME|PATH-OR-ERROR
  curl -fsS -m 15 "${API}/JellyfinExporter/Jobs${1:+?status=$1}" -H "$HDR" |
    python3 -c '
import json, sys
payload = json.load(sys.stdin)
for job in payload["Jobs"]:
    print("{}|{}|{}|{}".format(job["Status"], job["ProgressPercent"], job["ItemName"],
                               job.get("OutputPath") or job.get("ErrorMessage") or ""))
'
}

jobs_json() {
  curl -fsS -m 15 "${API}/JellyfinExporter/Jobs${1:+?status=$1}" -H "$HDR"
}

wait_for_no_active() {
  for _ in $(seq 1 180); do
    if [ "$(jobs active | wc -l)" = "0" ]; then return 0; fi
    sleep 2
  done
  fail "an export never finished"
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
mkdir -p "$WORK/config" "$WORK/cache" "$WORK/media/Movies/Test Movie (2020)" "$WORK/exports"
ffmpeg -hide_banner -loglevel error -y \
  -f lavfi -i "testsrc2=size=1280x720:rate=30:duration=120" \
  -f lavfi -i "sine=frequency=440:duration=120" \
  -c:v libx264 -preset ultrafast -pix_fmt yuv420p -c:a aac -shortest \
  "$WORK/media/Movies/Test Movie (2020)/Test Movie (2020).mkv"

# The zip is served from the host; /exports-ro is a read-only bind mount and
# /exports-small is a 2 MB tmpfs, both used to force genuine failures.
python3 -m http.server "$MANIFEST_PORT" --bind 0.0.0.0 --directory "$(dirname "$ZIP")" \
  >"${WORK}/httpd.log" 2>&1 &
SERVER_PID=$!
sleep 1
cat >"$(dirname "$ZIP")/status-verify-manifest.json" <<EOF
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
        "changelog": "status verification build",
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
  -v "$WORK/readonly:/exports-ro:ro" \
  --tmpfs /exports-small:size=2m \
  "$IMAGE" >/dev/null
mkdir -p "$WORK/readonly"

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
REPO_URL="http://host.docker.internal:${MANIFEST_PORT}/status-verify-manifest.json"
curl -fsS -o /dev/null -X POST "${API}/Repositories" -H "$HDR" -H 'Content-Type: application/json' \
  -d "[{\"Name\":\"status-verify\",\"Url\":\"${REPO_URL}\",\"Enabled\":true}]"
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
PLUGINS_JSON=""
PLUGINS_CODE=""
# A restarted Jellyfin answers /System/Info/Public before the plugin manager has
# finished loading, and /Plugins then returns an empty body for a while (and the
# first minutes after a start can drop connections outright). Poll generously.
for attempt in $(seq 1 120); do
  PLUGINS_CODE="$(curl -sS -o "${WORK}/plugins.json" -w '%{http_code}' -m 10 "${API}/Plugins" \
    -H "$HDR" 2>/dev/null || true)"
  PLUGINS_JSON="$(cat "${WORK}/plugins.json" 2>/dev/null || true)"
  if printf '%s' "$PLUGINS_JSON" |
    python3 -c 'import json,sys;print(len(json.load(sys.stdin)))' >/dev/null 2>&1; then break; fi
  [ $((attempt % 10)) -eq 0 ] && echo "   /Plugins attempt ${attempt}: HTTP ${PLUGINS_CODE}, $(printf '%s' "$PLUGINS_JSON" | wc -c) bytes"
  sleep 3
done
[ -s "${WORK}/plugins.json" ] || fail "/Plugins stayed empty for the whole poll (last HTTP ${PLUGINS_CODE})"
cp "${WORK}/plugins.json" "${WORK}/plugins-last.json"
cat >"${WORK}/check_1.py" <<'PYCHECK'
import json, sys
plugins = json.load(sys.stdin)
match = [p for p in plugins if p["Name"] == sys.argv[1]]
assert match, f"{sys.argv[1]} is not loaded"
assert match[0]["Version"] == sys.argv[2], match[0]["Version"]
assert match[0]["Status"].lower() == "active", match[0]["Status"]
print("plugin active", match[0]["Version"])
PYCHECK
printf '%s' "$PLUGINS_JSON" | python3 "${WORK}/check_1.py" "${PLUGIN_NAME}" "${VERSION}"

say "configuring a 480p profile"
PROFILE_ID="$(python3 -c 'import uuid;print(uuid.uuid4())')"
python3 - "$PROFILE_ID" >"${WORK}/config.json" <<'PY'
import json, sys
print(json.dumps({
    "Profiles": [{
        "Id": sys.argv[1], "Name": "QA 480p", "Format": "H264",
        "Resolution": "Resolution480p", "AudioChannels": "Original",
        "OutputDirectory": "/exports", "Enabled": True,
    }],
    "DefaultProfileId": sys.argv[1],
    "MaxConcurrentExports": 1,
    "JobHistoryLimit": 25,
    "NotifyOnCompletion": True,
    "NotifyOnFailure": True,
}))
PY
curl -fsS -o /dev/null -X POST "${API}/Plugins/${PLUGIN_GUID}/Configuration" -H "$HDR" \
  -H 'Content-Type: application/json' --data-binary @"${WORK}/config.json"

seed_job() {
  # seed_job <item-name> <output-directory> <error> [status]
  # Writes jobs.json into the stopped container's plugin data folder with docker
  # cp (the file is root-owned once Jellyfin has written it, so a plain host
  # write fails; docker cp goes through the daemon). The directory has to be
  # created while the container still runs, because exec needs a live container.
  docker exec "$NAME" mkdir -p "${PLUGIN_DATA_DIR}"
  docker stop "$NAME" >/dev/null
  python3 - "$PROFILE_ID" "$ITEM_ID" "$1" "$2" "$3" "${4:-Failed}" >"${WORK}/jobs.json" <<'PY'
import datetime, json, sys, uuid

profile, item, name, directory, error, status = sys.argv[1:7]
now = datetime.datetime.now(datetime.timezone.utc).isoformat()
print(json.dumps({"Version": 1, "SavedAt": now, "Jobs": [{
    # Jellyfin writes Guids without dashes; the store must read that form.
    "Id": uuid.uuid4().hex,
    "JellyfinItemId": item,
    "ItemName": name,
    "ItemType": "Movie",
    "ProfileId": profile,
    "ProfileName": "QA 480p",
    "Status": status,
    "ProgressPercent": 0,
    "OutputPath": None,
    "ErrorMessage": error,
    "CreatedAt": now,
    "StartedAt": now,
    "FinishedAt": now,
    "Format": "H264",
    "Resolution": "Resolution480p",
    "AudioChannels": "Original",
    "OutputDirectory": directory,
}]}, indent=2))
PY
  mkdir -p "${WORK}/seed" && cp "${WORK}/jobs.json" "${WORK}/seed/jobs.json"
  docker cp "${WORK}/seed/jobs.json" "$NAME:${PLUGIN_DATA_DIR}/jobs.json"
  docker start "$NAME" >/dev/null
  wait_for_api
  login
}

# A small finished history (two completed with real paths, two failed) so the
# status page has something to render when the browser checks run.
seed_finished_history() {
  docker exec "$NAME" mkdir -p "${PLUGIN_DATA_DIR}"
  docker stop "$NAME" >/dev/null
  python3 - "$PROFILE_ID" "$ITEM_ID" >"${WORK}/jobs-history.json" <<'PY'
import datetime, json, sys, uuid

profile, item = sys.argv[1:3]
now = datetime.datetime.now(datetime.timezone.utc)

def job(name, status, minutes_ago, path=None, error=None, percent=100):
    finished = now - datetime.timedelta(minutes=minutes_ago)
    return {
        "Id": uuid.uuid4().hex,
        "JellyfinItemId": item,
        "ItemName": name,
        "ItemType": "Movie",
        "ProfileId": profile,
        "ProfileName": "QA 480p",
        "Status": status,
        "ProgressPercent": percent,
        "OutputPath": path,
        "ErrorMessage": error,
        "CreatedAt": (finished - datetime.timedelta(minutes=4)).isoformat(),
        "StartedAt": (finished - datetime.timedelta(minutes=4)).isoformat(),
        "FinishedAt": finished.isoformat(),
        "Format": "H264",
        "Resolution": "Resolution480p",
        "AudioChannels": "Original",
        "OutputDirectory": "/exports",
    }

print(json.dumps({"Version": 1, "SavedAt": now.isoformat(), "Jobs": [
    job("Test Movie (2020)", "Completed", 2,
        path="/exports/Movies/Test Movie (2020)/Test Movie (2020).mkv"),
    job("Season One", "Completed", 9,
        path="/exports/TV/Example Show/Season 01/01 - Pilot.mkv"),
    job("Tiny disk", "Failed", 15,
        error="FFmpeg exited with code 228: No space left on device"),
    job("Corrupt source", "Failed", 22,
        error="FFmpeg exited with code 1: Invalid data found when processing input"),
]}, indent=2))
PY
  docker cp "${WORK}/jobs-history.json" "$NAME:${PLUGIN_DATA_DIR}/jobs.json"
  docker start "$NAME" >/dev/null
  wait_for_api
  login
}

say "seed a finished job on disk, restart, and read it back"
seed_job "Seeded failure" "/exports" "Seeded failure so the retry path can be exercised"
cat >"${WORK}/check_2.py" <<'PYCHECK'
import json, sys
payload = json.load(sys.stdin)
assert payload["HistoryLimit"] == 25, payload["HistoryLimit"]
assert payload["Counts"]["Failed"] == 1, payload["Counts"]
job = payload["Jobs"][0]
assert job["Status"] == "Failed", job
assert job["CanRetry"] is True, job
assert payload["HistoryFile"].endswith("jobs.json"), payload["HistoryFile"]
print("history restored:", json.dumps(payload["Counts"]))
PYCHECK
jobs_json | python3 "${WORK}/check_2.py"

say "retry it and watch the progress endpoint while FFmpeg runs"
JOB_ID="$(jobs_json | python3 -c 'import json,sys;print(json.load(sys.stdin)["Jobs"][0]["Id"])')"
curl -fsS -o /dev/null -X POST "${API}/JellyfinExporter/Jobs/${JOB_ID}/retry" -H "$HDR"
PROGRESS_SEEN=0
for _ in $(seq 1 90); do
  LINE="$(jobs active | head -1)"
  [ -z "$LINE" ] && break
  PERCENT="$(printf '%s' "$LINE" | cut -d'|' -f2)"
  if python3 -c "import sys;sys.exit(0 if float(sys.argv[1]) > 0 else 1)" "$PERCENT"; then PROGRESS_SEEN=1; fi
  sleep 1
done
wait_for_no_active
[ "$PROGRESS_SEEN" = "1" ] || fail "the API never reported progress above 0%"
echo "observed progress above 0% while the export ran"
jobs completed | grep -q '^Completed|100' || fail "the retried job did not complete"

OUTPUT_PATH="$(jobs completed | head -1 | cut -d'|' -f4)"
[ -n "$OUTPUT_PATH" ] || fail "the completed job has no output path"
docker exec "$NAME" test -f "$OUTPUT_PATH" || fail "the output file is missing in the container: ${OUTPUT_PATH}"
echo "output file exists: ${OUTPUT_PATH}"

say "checking the completion notification in the activity log"
activity() { curl -fsS "${API}/System/ActivityLog/Entries?limit=200" -H "$HDR"; }
cat >"${WORK}/check_3.py" <<'PYCHECK'
import json, sys
entries = json.load(sys.stdin)["Items"]
completed = [e for e in entries if e["Name"] == "Export completed"]
assert completed, [e["Name"] for e in entries]
match = [e for e in completed if e.get("ShortOverview", "").endswith(sys.argv[1])]
assert match, completed[0].get("ShortOverview")
entry = match[0]
assert "Export completed: " in entry["ShortOverview"], entry
assert "(QA 480p)" in entry["ShortOverview"], entry
print("notification:", entry["ShortOverview"])
PYCHECK
activity | python3 "${WORK}/check_3.py" "${OUTPUT_PATH}"

say "restarting to prove the finished history survives"
docker restart "$NAME" >/dev/null
wait_for_api
login
cat >"${WORK}/check_4.py" <<'PYCHECK'
import json, sys
payload = json.load(sys.stdin)
completed = [j for j in payload["Jobs"] if j["Status"] == "Completed"]
assert completed, payload["Jobs"]
assert completed[0]["OutputPath"], completed[0]
assert completed[0]["FinishedAt"], completed[0]
print("history after restart:", json.dumps(payload["Counts"]))
PYCHECK
jobs_json | python3 "${WORK}/check_4.py"

say "forcing a real FFmpeg failure (output folder on a full disk)"
seed_job "Tiny disk" "/exports-small" "Seeded to force an FFmpeg failure by running out of space"
JOB_ID="$(jobs_json | python3 -c 'import json,sys;print(json.load(sys.stdin)["Jobs"][0]["Id"])')"
curl -fsS -o /dev/null -X POST "${API}/JellyfinExporter/Jobs/${JOB_ID}/retry" -H "$HDR"
wait_for_no_active
FAILED_LINE="$(jobs failed | grep 'Tiny disk' | head -1)"
printf '%s\n' "$FAILED_LINE"
printf '%s' "$FAILED_LINE" | grep -q 'FFmpeg exited with code' || fail "no FFmpeg error was recorded: ${FAILED_LINE}"
cat >"${WORK}/check_5.py" <<'PYCHECK'
import json, sys
entries = json.load(sys.stdin)["Items"]
failed = [e for e in entries if e["Name"] == "Export failed"]
assert failed, [e["Name"] for e in entries]
entry = failed[0]
assert "Export failed: " in entry["ShortOverview"], entry
assert "FFmpeg exited with code" in entry["ShortOverview"], entry
assert entry["Severity"] in ("Error", 4, "error"), entry
print("notification:", entry["ShortOverview"][:160])
PYCHECK
activity | python3 "${WORK}/check_5.py"

say "a job that was running when the server stopped comes back failed"
seed_job "Interrupted" "/exports" "run in progress" "Running"
cat >"${WORK}/check_6.py" <<'PYCHECK'
import json, sys
payload = json.load(sys.stdin)
jobs = [j for j in payload["Jobs"] if j["ItemName"] == "Interrupted"]
assert jobs, payload["Jobs"]
job = jobs[0]
assert job["Status"] == "Failed", job
assert "restarted" in (job["ErrorMessage"] or ""), job
print("interrupted job:", job["ErrorMessage"][:80])
PYCHECK
jobs_json | python3 "${WORK}/check_6.py"

# --- A finished history for the browser checks, then the page itself.
say "seeding a finished history for the browser checks"
seed_finished_history
jobs_json | python3 -c 'import json,sys;p=json.load(sys.stdin);print("seeded history:", json.dumps(p["Counts"]))'

if [ "${RUN_UI:-1}" = "1" ]; then
  say "browser-level checks (status page, live progress bar, activity page)"
  NODE_PATH="${NODE_PATH:-$HOME/career-ops/node_modules}" \
  API="${API}" JF_USER="qaadmin" JF_PASS="${ADMIN_PASSWORD}" EXPECT_LIMIT="25" SHOTS="${WORK}" \
    node "${ROOT}/scripts/verify-status-ui.js" ||
    fail "the browser-level status page checks failed"
fi

say "PASS: status page, persisted history and notifications verified"
echo "Screenshots and the seeded history are in ${WORK}."
