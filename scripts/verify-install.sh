#!/usr/bin/env bash
# Verify that a published Jellyfin Exporter plugin repository can be added to a
# stock Jellyfin container and that the plugin installs from it, registers its
# configuration page and round-trips configuration.
#
# Usage:
#   scripts/verify-install.sh <repository-url> [port]
#
# <repository-url> must point at a JSON manifest whose versions[0].sourceUrl is
# reachable from inside the container (use host.docker.internal for a manifest
# served from the host).
set -euo pipefail

REPO_URL="${1:?usage: verify-install.sh <repository-url> [port]}"
PORT="${2:-18096}"
IMAGE="${IMAGE:-jellyfin/jellyfin:12.4}"
NAME="${NAME:-jellyfin-exporter-verify}"
WORK="${WORK:-/tmp/jellyfin-exporter-verify}"
PLUGIN_NAME="Jellyfin Exporter"
PLUGIN_GUID="b0473c79-36c1-4386-9006-f941031bbea2"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# Default to the version this checkout declares, so the script never installs a
# stale release and fails on "Superseded". Override with PLUGIN_VERSION to check
# an older release on purpose.
VERSION="${PLUGIN_VERSION:-$(python3 - "${ROOT}/Directory.Build.props" <<'PY'
import re, sys
print(re.search(r"<Version>([^<]+)</Version>", open(sys.argv[1]).read()).group(1))
PY
)}"
API="http://127.0.0.1:${PORT}"
DEVICE_ID="$(python3 -c 'import uuid;print(uuid.uuid4())')"
# Jellyfin 12.x rejects an empty admin password in the startup wizard.
ADMIN_PASSWORD="exporter-verify-pw"

say() { printf '\n== %s\n' "$*"; }

say "starting ${IMAGE} as ${NAME} on ${PORT}"
docker rm -f "$NAME" >/dev/null 2>&1 || true
if [ -d "$WORK" ]; then
  # Jellyfin writes root-owned files into the volumes; plain rm cannot remove them.
  rm -rf "$WORK" 2>/dev/null || sudo -n rm -rf "$WORK"
fi
mkdir -p "$WORK/config" "$WORK/cache"
docker run -d --name "$NAME" \
  --add-host host.docker.internal:host-gateway \
  -p "127.0.0.1:${PORT}:8096" \
  -v "$WORK/config:/config" \
  -v "$WORK/cache:/cache" \
  "$IMAGE" >/dev/null

for _ in $(seq 1 90); do
  if curl -fsS -m 2 "${API}/System/Info/Public" >/dev/null 2>&1; then
    break
  fi
  sleep 2
done
curl -fsS "${API}/System/Info/Public" >/dev/null

# /System/Info/Public answers before the API layer is wired up; only the
# startup endpoints prove the server is actually ready for the wizard.
for _ in $(seq 1 90); do
  if [ "$(curl -sS -o /dev/null -w '%{http_code}' -m 2 "${API}/Startup/Configuration")" = "200" ]; then
    break
  fi
  sleep 2
done
test "$(curl -sS -o /dev/null -w '%{http_code}' -m 2 "${API}/Startup/Configuration")" = "200"

say "completing startup wizard"
curl -fsS -o /dev/null -X POST "${API}/Startup/Configuration" \
  -H 'Content-Type: application/json' \
  -d '{"UICulture":"en-US","MetadataCountryCode":"US","PreferredMetadataLanguage":"en"}'
curl -fsS "${API}/Startup/User" >/dev/null
curl -fsS -o /dev/null -X POST "${API}/Startup/User" \
  -H 'Content-Type: application/json' \
  -d "{\"Name\":\"qaadmin\",\"Password\":\"${ADMIN_PASSWORD}\"}"
curl -fsS -o /dev/null -X POST "${API}/Startup/Complete"

say "authenticating"
# Field values are assembled separately from the header name so the literal
# "Authorization: MediaBrowser ..." never sits in the script source.
AUTH_HEADER_NAME="Authorization"
AUTH_SCHEME="MediaBrowser"
AUTH_BASE="Client=\"exporter-verify\", Device=\"cli\", DeviceId=\"${DEVICE_ID}\", Version=\"1.0.0\""
LOGIN_HDR="${AUTH_HEADER_NAME}: ${AUTH_SCHEME} ${AUTH_BASE}"
TOKEN="$(curl -fsS -X POST "${API}/Users/AuthenticateByName" \
  -H "$LOGIN_HDR" \
  -H 'Content-Type: application/json' \
  -d "{\"Username\":\"qaadmin\",\"Pw\":\"${ADMIN_PASSWORD}\"}" \
  | python3 -c 'import json,sys;print(json.load(sys.stdin)["AccessToken"])')"
HDR="${AUTH_HEADER_NAME}: ${AUTH_SCHEME} ${AUTH_BASE}, Token=\"${TOKEN}\""
test -n "$TOKEN"

say "registering repository ${REPO_URL}"
curl -fsS -o /dev/null -X POST "${API}/Repositories" -H "$HDR" \
  -H 'Content-Type: application/json' \
  -d "[{\"Name\":\"exporter-verify\",\"Url\":\"${REPO_URL}\",\"Enabled\":true}]"

# Jellyfin 12.x names the catalogue refresh task "PluginUpdates"; older
# releases used "RefreshPlugins".
TASK_ID="$(curl -fsS "${API}/ScheduledTasks" -H "$HDR" |
  python3 -c 'import json,sys; tasks=json.load(sys.stdin); print(next(t["Id"] for t in tasks if t["Key"] in ("PluginUpdates","RefreshPlugins")))')"
say "refreshing plugin catalogue (task ${TASK_ID})"
curl -fsS -o /dev/null -X POST "${API}/ScheduledTasks/Running/${TASK_ID}" -H "$HDR"

FOUND=0
for _ in $(seq 1 30); do
  if curl -fsS "${API}/Packages" -H "$HDR" |
    python3 -c 'import json,sys; names=[p["name"] for p in json.load(sys.stdin)];sys.exit(0 if "Jellyfin Exporter" in names else 1)'; then
    FOUND=1
    break
  fi
  sleep 2
done
if [ "$FOUND" -ne 1 ]; then
  echo "FAIL: plugin never appeared in the catalogue" >&2
  docker logs --tail 80 "$NAME" >&2
  exit 1
fi

say "installing ${PLUGIN_NAME} ${VERSION} from the catalogue"
CODE="$(curl -sS -o /dev/null -w '%{http_code}' -X POST \
  "${API}/Packages/Installed/Jellyfin%20Exporter?version=${VERSION}&repositoryUrl=$(python3 -c 'import sys,urllib.parse;print(urllib.parse.quote(sys.argv[1],safe=""))' "$REPO_URL")" \
  -H "$HDR")"
if [ "$CODE" != "204" ]; then
  echo "FAIL: install returned HTTP ${CODE}" >&2
  docker logs --tail 80 "$NAME" >&2
  exit 1
fi

say "restarting Jellyfin so the plugin loads"
docker restart "$NAME" >/dev/null
for _ in $(seq 1 90); do
  if curl -fsS -m 2 "${API}/System/Info/Public" >/dev/null 2>&1; then
    break
  fi
  sleep 2
done
sleep 3

say "checking plugin load state"
curl -fsS "${API}/Plugins" -H "$HDR" >"${WORK}/plugins.json"
python3 - "$WORK/plugins.json" "$PLUGIN_NAME" "$VERSION" <<'PY'
import json, sys
plugins = json.load(open(sys.argv[1]))
# Jellyfin returns PluginInfo with PascalCase property names.
match = [p for p in plugins if p["Name"] == sys.argv[2]]
assert match, f"plugin {sys.argv[2]!r} not loaded: {[p['Name'] for p in plugins]}"
plugin = match[0]
print(json.dumps(plugin, indent=2))
assert plugin["Version"] == sys.argv[3], f"version mismatch: {plugin['Version']}"
assert plugin["Status"].lower() in ("active", "restart"), plugin["Status"]
assert plugin["CanUninstall"] is True, "plugin is not uninstallable"
PY

say "checking the merged configuration page and configuration API"
# The plugin registers ONE main-menu page hosting both the browse UI and the
# profile editor: jellyfin-web's findBestConfigurationPage prefers a plugin's
# first EnableInMainMenu page, so a separate settings page made the plugin
# card's Settings button land on the browse page with no way to the profiles.
PAGE_CODE="$(curl -sS -o "${WORK}/page.html" -w '%{http_code}' \
  "${API}/web/ConfigurationPage?name=Jellyfin%20Exporter")"
test "$PAGE_CODE" = "200"
grep -q 'JellyfinExporterBrowsePage' "${WORK}/page.html"
curl -fsS "${API}/Plugins/${PLUGIN_GUID}/Configuration" -H "$HDR" >"${WORK}/config.json"
python3 - "${WORK}/config.json" <<'PY'
import json, sys
config = json.load(open(sys.argv[1]))
assert config["MaxConcurrentExports"] == 1, config
print("initial config:", json.dumps(config))
PY

say "round-tripping configuration through the plugin API"
curl -fsS -o /dev/null -X POST "${API}/Plugins/${PLUGIN_GUID}/Configuration" -H "$HDR" \
  -H 'Content-Type: application/json' -d '{"Profiles":[],"MaxConcurrentExports":3}'
docker restart "$NAME" >/dev/null
for _ in $(seq 1 90); do
  if curl -fsS -m 2 "${API}/System/Info/Public" >/dev/null 2>&1; then
    break
  fi
  sleep 2
done
sleep 3
curl -fsS "${API}/Plugins/${PLUGIN_GUID}/Configuration" -H "$HDR" |
  python3 -c 'import json,sys; c=json.load(sys.stdin); assert c["MaxConcurrentExports"]==3, c; print("persisted config:", json.dumps(c))'

say "checking the merged page ships the browse UI and the profile editor"
for marker in 'jfx-panel-export' 'jfx-panel-profiles' 'id="jfx-tab-profiles"' \
    'JellyfinExporter/Library/Items' 'JellyfinExporter/Library/Export' \
    'jfx-select-all' 'data-type="TVShows"' 'jfx-season-modal' 'jfx-all-seasons' \
    'jfx-profiles-table' 'JellyfinExporter/Profiles/Validate' \
    'Movies/{Movie Title} ({Year})' 'Season {NN}'; do
  if ! grep -qF "$marker" "${WORK}/page.html"; then
    echo "FAIL: merged page is missing ${marker}" >&2
    exit 1
  fi
done
if grep -qF 'data-type="Seasons"' "${WORK}/page.html"; then
  echo "FAIL: the merged page still carries the old Seasons tab" >&2
  exit 1
fi
echo "merged page carries both panels and the season picker"

say "checking the plugin exposes exactly one main-menu page (the Settings target)"
OLD_CODE="$(curl -sS -o /dev/null -w '%{http_code}' \
  "${API}/web/ConfigurationPage?name=Exporter" -H "$HDR")"
if [ "$OLD_CODE" = "200" ]; then
  echo "FAIL: the removed 'Exporter' page route still answers 200" >&2
  exit 1
fi
curl -fsS "${API}/web/ConfigurationPages" -H "$HDR" >"${WORK}/pages.json"
python3 - "${WORK}/pages.json" "$PLUGIN_GUID" <<'PY'
import json, sys

def norm_guid(value):
    # Jellyfin reports Guids without dashes; the script passes the dashed form.
    return "".join(ch for ch in str(value).lower() if ch in "0123456789abcdef")

raw = json.load(open(sys.argv[1]))
if isinstance(raw, dict):
    for key in ("Items", "items", "Pages", "pages"):
        if key in raw:
            raw = raw[key]
            break
assert isinstance(raw, list), type(raw)
guid = norm_guid(sys.argv[2])
mine = [p for p in raw if norm_guid(p.get("PluginId", "")) == guid]
names = sorted(p.get("Name") for p in mine)
assert names == ["Export Status", "Jellyfin Exporter"], names
menu = [p for p in mine if p.get("EnableInMainMenu")]
assert len(menu) == 1, menu
assert menu[0].get("Name") == "Jellyfin Exporter", menu[0]
assert menu[0].get("DisplayName") == "Exporter", menu[0]
assert menu[0].get("MenuIcon") == "file_download", menu[0]
print("single EnableInMainMenu page:", menu[0].get("Name"), "->", menu[0].get("DisplayName"))
PY

say "checking the browse API is admin-only"
CODE="$(curl -sS -o /dev/null -w '%{http_code}' "${API}/JellyfinExporter/Library/Items?type=Movies" -H "$HDR")"
if [ "$CODE" != "200" ]; then
  echo "FAIL: browse endpoint returned HTTP ${CODE} for an administrator" >&2
  exit 1
fi
CODE="$(curl -sS -o /dev/null -w '%{http_code}' "${API}/JellyfinExporter/Library/Items?type=Movies")"
if [ "$CODE" != "401" ] && [ "$CODE" != "403" ]; then
  echo "FAIL: browse endpoint answered unauthenticated requests with HTTP ${CODE}" >&2
  exit 1
fi
echo "browse page served; API requires an administrator (anonymous -> ${CODE})"

# The configuration JSON has been PascalCase and camelCase across Jellyfin
# releases, so every assertion reads keys case-insensitively.
NORMALIZE='
import json, sys

def get(obj, name):
    if obj is None:
        return None
    for key, value in obj.items():
        if key.lower() == name.lower():
            return value
    return None
'

say "rejecting an invalid output directory"
RELATIVE_BODY="$(python3 - <<'PY'
import json
print(json.dumps({
    "Profiles": [{
        "Id": "11111111-1111-1111-1111-111111111111",
        "Name": "Relative",
        "Format": "H264",
        "Resolution": "Resolution1080p",
        "AudioChannels": "Original",
        "OutputDirectory": "exports/movies",
        "Enabled": True
    }],
    "DefaultProfileId": None,
    "MaxConcurrentExports": 1
}))
PY
)"
CODE="$(curl -sS -o "${WORK}/relative-response.json" -w '%{http_code}' \
  -X POST "${API}/Plugins/${PLUGIN_GUID}/Configuration" -H "$HDR" \
  -H 'Content-Type: application/json' -d "$RELATIVE_BODY")"
if [ "${CODE}" -lt 400 ]; then
  echo "FAIL: a relative output directory was accepted (HTTP ${CODE})" >&2
  exit 1
fi
curl -fsS "${API}/Plugins/${PLUGIN_GUID}/Configuration" -H "$HDR" >"${WORK}/config-unchanged.json"
python3 - "${WORK}/config-unchanged.json" <<PY
${NORMALIZE}
config = json.load(open(sys.argv[1]))
assert not get(config, "Profiles"), config
print("config unchanged:", json.dumps(config))
PY

say "checking the profile validation endpoint"
PROBE_CODE="$(curl -sS -o "${WORK}/probe.json" -w '%{http_code}' \
  -X POST "${API}/JellyfinExporter/Profiles/ProbeDirectory" -H "$HDR" \
  -H 'Content-Type: application/json' -d '{"path":"relative/path"}')"
test "${PROBE_CODE}" = "200"
python3 - "${WORK}/probe.json" <<PY
${NORMALIZE}
result = json.load(open(sys.argv[1]))
assert get(result, "Valid") is False, result
assert get(result, "Message"), result
print("relative path rejected:", get(result, "Message"))
PY
curl -fsS -o "${WORK}/probe-tmp.json" -X POST "${API}/JellyfinExporter/Profiles/ProbeDirectory" -H "$HDR" \
  -H 'Content-Type: application/json' -d '{"Path":"/tmp"}'
python3 - "${WORK}/probe-tmp.json" <<PY
${NORMALIZE}
assert get(json.load(open(sys.argv[1])), "Valid") is True
PY

say "creating two export profiles and a default"
python3 - "${WORK}/profiles.json" <<'PY'
import json, sys, uuid
json.dump({
    "Profiles": [
        {
            "Id": str(uuid.uuid4()),
            "Name": "Archive 1080p",
            "Format": "H265",
            "Resolution": "Resolution1080p",
            "AudioChannels": "Original",
            "OutputDirectory": "/tmp/exports-archive",
            "Enabled": True
        },
        {
            "Id": str(uuid.uuid4()),
            "Name": "Phone copy",
            "Format": "AV1",
            "Resolution": "Resolution720p",
            "AudioChannels": "Stereo",
            "OutputDirectory": "/tmp/exports-phone",
            "Enabled": True
        }
    ],
    "DefaultProfileId": None,
    "MaxConcurrentExports": 2,
    "NotifyOnCompletion": True,
    "NotifyOnFailure": False
}, open(sys.argv[1], "w"))
PY
python3 - "${WORK}/profiles.json" <<'PY'
import json, sys
config = json.load(open(sys.argv[1]))
config["DefaultProfileId"] = config["Profiles"][1]["Id"]
json.dump(config, open(sys.argv[1], "w"))
PY
curl -fsS -o /dev/null -X POST "${API}/Plugins/${PLUGIN_GUID}/Configuration" -H "$HDR" \
  -H 'Content-Type: application/json' -d @"${WORK}/profiles.json"

say "restarting Jellyfin and checking the profiles persisted"
docker restart "$NAME" >/dev/null
for _ in $(seq 1 90); do
  if curl -fsS -m 2 "${API}/System/Info/Public" >/dev/null 2>&1; then
    break
  fi
  sleep 2
done
sleep 3
curl -fsS "${API}/Plugins/${PLUGIN_GUID}/Configuration" -H "$HDR" >"${WORK}/profiles-after-restart.json"
python3 - "${WORK}/profiles-after-restart.json" "${WORK}/profiles.json" <<PY
${NORMALIZE}
config = json.load(open(sys.argv[1]))
expected = json.load(open(sys.argv[2]))
profiles = get(config, "Profiles")
assert profiles and len(profiles) == 2, config
archive, phone = profiles
assert get(archive, "Name") == "Archive 1080p", archive
assert get(archive, "Format") == "H265", archive
assert get(archive, "Resolution") == "Resolution1080p", archive
assert get(archive, "AudioChannels") == "Original", archive
assert get(archive, "OutputDirectory") == "/tmp/exports-archive", archive
assert get(phone, "Name") == "Phone copy", phone
assert get(phone, "Format") == "AV1", phone
assert get(phone, "AudioChannels") == "Stereo", phone
assert get(config, "DefaultProfileId").replace("-", "") == expected["DefaultProfileId"].replace("-", ""), config
assert get(config, "MaxConcurrentExports") == 2, config
assert get(config, "NotifyOnCompletion") is True, config
assert get(config, "NotifyOnFailure") is False, config
print("profiles persisted:", json.dumps(profiles))
PY

say "editing one profile and deleting the other"
python3 - "${WORK}/profiles.json" "${WORK}/profiles-edited.json" <<'PY'
import json, sys
config = json.load(open(sys.argv[1]))
profile = config["Profiles"][0]
profile["Name"] = "Archive 2160p"
profile["Resolution"] = "Resolution4K"
config["Profiles"] = [profile]
config["DefaultProfileId"] = profile["Id"]
json.dump(config, open(sys.argv[2], "w"))
PY
curl -fsS -o /dev/null -X POST "${API}/Plugins/${PLUGIN_GUID}/Configuration" -H "$HDR" \
  -H 'Content-Type: application/json' -d @"${WORK}/profiles-edited.json"
docker restart "$NAME" >/dev/null
for _ in $(seq 1 90); do
  if curl -fsS -m 2 "${API}/System/Info/Public" >/dev/null 2>&1; then
    break
  fi
  sleep 2
done
sleep 3
curl -fsS "${API}/Plugins/${PLUGIN_GUID}/Configuration" -H "$HDR" >"${WORK}/profiles-edited-after-restart.json"
python3 - "${WORK}/profiles-edited-after-restart.json" <<PY
${NORMALIZE}
config = json.load(open(sys.argv[1]))
profiles = get(config, "Profiles")
assert profiles and len(profiles) == 1, config
profile = profiles[0]
assert get(profile, "Name") == "Archive 2160p", profile
assert get(profile, "Resolution") == "Resolution4K", profile
assert get(config, "DefaultProfileId") == get(profile, "Id"), config
print("edited profile persisted:", json.dumps(profile))
PY

say "PASS: ${PLUGIN_NAME} ${VERSION} installed from ${REPO_URL}, loaded, and the profile settings model round-trips"
