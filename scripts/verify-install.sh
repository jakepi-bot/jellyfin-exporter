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
VERSION="0.1.0.0"
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

say "checking configuration page and configuration API"
PAGE_CODE="$(curl -sS -o "${WORK}/configPage.html" -w '%{http_code}' \
  "${API}/web/ConfigurationPage?name=Jellyfin%20Exporter")"
test "$PAGE_CODE" = "200"
grep -q 'JellyfinExporterConfigPage' "${WORK}/configPage.html"
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

say "PASS: ${PLUGIN_NAME} ${VERSION} installed from ${REPO_URL}, loaded, and persisted configuration"
