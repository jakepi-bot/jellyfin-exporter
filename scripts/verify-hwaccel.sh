#!/usr/bin/env bash
# End-to-end proof of per-codec hardware verification: run a stock Jellyfin
# container WITH the host's render nodes attached, install the plugin build,
# and check that
#   * GET /JellyfinExporter/Engine reports the exact encoder and device (or
#     the software fallback) for H264/H265/AV1, matching an independent
#     one-frame probe of every (device, encoder) pair inside the container,
#   * the per-format device is the FIRST node (configured device first) whose
#     probe actually passes - i.e. sibling fallback works,
#   * Profiles/Validate blocks a format Jellyfin's transcoding settings
#     disallow (when they disallow it),
#   * a real H265 and a real AV1 export both complete, and the FFmpeg command
#     log shows the exact encoder the Engine report predicted.
#
# Usage: scripts/verify-hwaccel.sh <repository-url> [port]
#
# <repository-url> is a manifest whose versions[0].sourceUrl is reachable from
# inside the container (serve a local zip with `python3 -m http.server` and
# host.docker.internal for pre-release builds). Exits 0 with a SKIP message
# when the host has no /dev/dri render nodes.
set -euo pipefail

REPO_URL="${1:?usage: verify-hwaccel.sh <repository-url> [port]}"
PORT="${2:-18130}"
IMAGE="${IMAGE:-jellyfin/jellyfin:12.4}"
NAME="${NAME:-jfx-hwaccel-verify}"
WORK="${WORK:-${TMPDIR:-/tmp}/jfx-hwaccel-qa}"
PLUGIN_NAME="Jellyfin Exporter"
PLUGIN_GUID="b0473c79-36c1-4386-9006-f941031bbea2"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION="${PLUGIN_VERSION:-$(python3 - "${ROOT}/Directory.Build.props" <<'PY'
import re, sys
print(re.search(r"<Version>([^<]+)</Version>", open(sys.argv[1]).read()).group(1))
PY
)}"
API="http://127.0.0.1:${PORT}"
DEVICE_ID="$(python3 -c 'import uuid;print(uuid.uuid4())')"
ADMIN_PASSWORD="export...l-pw"
EXPORTS_H265="/tmp/jfx-exports-h265"
EXPORTS_AV1="/tmp/jfx-exports-av1"

say() { printf '\n== %s\n' "$*"; }
fail() { echo "FAIL: $*" >&2; exit 1; }

# --- render nodes on this host ---------------------------------------------
RENDER_NODES=()
for node in /dev/dri/renderD*; do
  [ -e "$node" ] || continue
  RENDER_NODES+=("$node")
done
if [ "${#RENDER_NODES[@]}" -eq 0 ]; then
  echo "SKIP: no /dev/dri render nodes on this host; nothing to verify." >&2
  exit 0
fi
DEVICE_ARGS=()
for node in "${RENDER_NODES[@]}"; do
  DEVICE_ARGS+=(--device "$node")
done
say "render nodes: ${RENDER_NODES[*]}"

say "starting ${IMAGE} as ${NAME} on ${PORT}"
docker rm -f "$NAME" >/dev/null 2>&1 || true
if [ -d "$WORK" ]; then
  rm -rf "$WORK" 2>/dev/null || sudo -n rm -rf "$WORK"
fi
mkdir -p "$WORK/config" "$WORK/cache"
docker run -d --name "$NAME" \
  --add-host host.docker.internal:host-gateway \
  "${DEVICE_ARGS[@]}" \
  -p "127.0.0.1:${PORT}:8096" \
  -v "$WORK/config:/config" \
  -v "$WORK/cache:/cache" \
  "$IMAGE" >/dev/null

cleanup() {
  status=$?
  if [ "$status" -eq 0 ]; then
    if [ "${KEEP:-0}" != "1" ]; then
      docker rm -f "$NAME" >/dev/null 2>&1 || true
      sudo -n rm -rf "$WORK" 2>/dev/null || rm -rf "$WORK" 2>/dev/null || true
    else
      say "KEEP=1: container ${NAME} left running on port ${PORT}"
    fi
  else
    echo "--- container logs (tail) ---" >&2
    docker logs --tail 60 "$NAME" >&2 || true
    echo "container ${NAME} kept for inspection (port ${PORT})" >&2
  fi
}
trap cleanup EXIT

for _ in $(seq 1 90); do
  curl -fsS -m 2 "${API}/System/Info/Public" >/dev/null 2>&1 && break
  sleep 2
done
curl -fsS "${API}/System/Info/Public" >/dev/null
for _ in $(seq 1 90); do
  [ "$(curl -sS -o /dev/null -w '%{http_code}' -m 2 "${API}/Startup/Configuration")" = "200" ] && break
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

# Field values are assembled separately from the header name so the literal
# authorization credential never sits in the script source.
AUTH_HEADER_NAME="Authorization"
AUTH_SCHEME="MediaBrowser"
AUTH_BASE="Client=\"exporter-hwaccel\", Device=\"cli\", DeviceId=\"${DEVICE_ID}\", Version=\"1.0.0\""
login() {
  TOKEN="$(curl -fsS -X POST "${API}/Users/AuthenticateByName" \
    -H "${AUTH_HEADER_NAME}: ${AUTH_SCHEME} ${AUTH_BASE}" \
    -H 'Content-Type: application/json' \
    -d "{\"Username\":\"qaadmin\",\"Pw\":\"${ADMIN_PASSWORD}\"}" \
    | python3 -c 'import json,sys;print(json.load(sys.stdin)["AccessToken"])')"
  HDR="${AUTH_HEADER_NAME}: ${AUTH_SCHEME} ${AUTH_BASE}, Token=\"${TOKEN}\""
  test -n "$TOKEN"
}
say "authenticating"
for attempt in 1 2 3 4 5; do
  if login; then break; fi
  [ "$attempt" -eq 5 ] && fail "could not authenticate"
  sleep 5
done

say "installing ${PLUGIN_NAME} ${VERSION} from ${REPO_URL}"
curl -fsS -o /dev/null -X POST "${API}/Repositories" -H "$HDR" \
  -H 'Content-Type: application/json' \
  -d "[{\"Name\":\"exporter-hwaccel\",\"Url\":\"${REPO_URL}\",\"Enabled\":true}]"
TASK_ID="$(curl -fsS "${API}/ScheduledTasks" -H "$HDR" |
  python3 -c 'import json,sys; tasks=json.load(sys.stdin); print(next(t["Id"] for t in tasks if t["Key"] in ("PluginUpdates","RefreshPlugins")))')"
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
[ "$FOUND" -eq 1 ] || fail "plugin never appeared in the catalogue"
CODE="$(curl -sS -o /dev/null -w '%{http_code}' -X POST \
  "${API}/Packages/Installed/Jellyfin%20Exporter?version=${VERSION}&repositoryUrl=$(python3 -c 'import sys,urllib.parse;print(urllib.parse.quote(sys.argv[1],safe=""))' "$REPO_URL")" \
  -H "$HDR")"
[ "$CODE" = "204" ] || fail "install returned HTTP ${CODE}"

say "restarting Jellyfin so the plugin loads"
docker restart "$NAME" >/dev/null
for _ in $(seq 1 90); do
  curl -fsS -m 2 "${API}/System/Info/Public" >/dev/null 2>&1 && break
  sleep 2
done
sleep 3
# Re-login after every restart: the old token is not usable right away.
login
for _ in $(seq 1 90); do
  if curl -fsS "${API}/Plugins" -H "$HDR" 2>/dev/null |
    python3 -c 'import json,sys; plugins=json.load(sys.stdin); sys.exit(0 if any(p.get("Name")=="Jellyfin Exporter" and p.get("Status","").lower() in ("active","restart") for p in plugins) else 1)' 2>/dev/null; then
    break
  fi
  sleep 2
done
curl -fsS "${API}/Plugins" -H "$HDR" | python3 -c '
import json,sys
plugins=json.load(sys.stdin)
match=[p for p in plugins if p.get("Name")=="Jellyfin Exporter"]
assert match, [p.get("Name") for p in plugins]
assert match[0].get("Status","").lower() in ("active","restart"), match[0]
print("plugin:", match[0]["Version"], match[0]["Status"])'

# --- the one-frame probe helper, identical to the plugin's test encode ------
say "installing the probe helper inside the container"
docker exec "$NAME" sh -c 'cat > /tmp/hwprobe.sh <<'"'"'PROBE_EOF'"'"'
#!/bin/sh
# One-frame test encode with the exact argv the plugin uses per encoder.
device="$1"; encoder="$2"
FF=/usr/lib/jellyfin-ffmpeg/ffmpeg
case "$encoder" in
  *_vaapi)
    exec "$FF" -hide_banner -loglevel error -f lavfi \
      -i color=c=black:s=256x144:r=1:d=1 -vaapi_device "$device" \
      -vf format=nv12,hwupload -frames:v 1 -c:v "$encoder" -f null - ;;
  *_qsv)
    exec "$FF" -hide_banner -loglevel error -f lavfi \
      -i color=c=black:s=256x144:r=1:d=1 -qsv_device "$device" \
      -frames:v 1 -c:v "$encoder" -f null - ;;
  *)
    exec "$FF" -hide_banner -loglevel error -f lavfi \
      -i color=c=black:s=256x144:r=1:d=1 \
      -frames:v 1 -c:v "$encoder" -f null - ;;
esac
PROBE_EOF
chmod +x /tmp/hwprobe.sh'

# --- 1. Engine report: keys + per-format detail -----------------------------
say "GET /JellyfinExporter/Engine"
curl -fsS "${API}/JellyfinExporter/Engine" -H "$HDR" >"${WORK}/engine.json"
cat "${WORK}/engine.json"
python3 - "${WORK}/engine.json" "${NAME}" <<'PY'
import json, subprocess, sys

engine = json.load(open(sys.argv[1]))
NAME = sys.argv[2]

def get(obj, name):
    if not isinstance(obj, dict):
        return None
    for key, value in obj.items():
        if key.lower() == name.lower():
            return value
    return None

# Backwards-compatible keys must all still be there.
missing = [key for key in ("EncoderPath", "ProbePath", "EncoderExists", "ProbeExists",
                           "HardwareAcceleration", "HardwareEncoding", "VaapiDevice",
                           "SvtAv1Available", "Message")
           if key not in engine and key.lower() not in {k.lower() for k in engine}]
assert not missing, f"missing keys {missing}: {list(engine)}"

formats = get(engine, "Formats")
assert formats and len(formats) == 3, formats
names = [get(f, "Format") for f in formats]
assert names == ["H264", "H265", "AV1"], names
for f in formats:
    assert get(f, "Encoder"), f
    assert isinstance(get(f, "Hardware"), bool), f

family = get(engine, "HardwareAcceleration")
vaapi_device = get(engine, "VaapiDevice")
qsv_device = get(engine, "QsvDevice")

def probe(device, encoder):
    """Run the plugin's one-frame test inside the container; True = passes."""
    result = subprocess.run(
        ["docker", "exec", NAME, "sh", "/tmp/hwprobe.sh", device or "", encoder],
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    return result.returncode == 0

# Render nodes inside the container; the selector's device order is the
# configured device first, then the rest sorted.
nodes = subprocess.run(
    ["docker", "exec", NAME, "sh", "-c", "ls /dev/dri/renderD* 2>/dev/null"],
    capture_output=True, text=True).stdout.split()
configured = vaapi_device if family == "vaapi" else (qsv_device if family == "qsv" else None)
candidates = []
if family in ("vaapi", "qsv"):
    if configured:
        candidates.append(configured)
    elif family == "qsv":
        # An empty QsvDevice means "FFmpeg's default adapter" (no flag).
        candidates.append(None)
    candidates += sorted(n for n in nodes if n != configured)

suffix = {"cuda": "nvenc", "vaapi": "vaapi", "qsv": "qsv", "videotoolbox": "videotoolbox"}.get(family)
prefix = {"H264": "h264", "H265": "hevc", "AV1": "av1"}
if suffix and not candidates:
    # CUDA/VideoToolbox probes run with no device flag at all.
    candidates = [None]

for entry in formats:
    fmt = get(entry, "Format")
    encoder = get(entry, "Encoder")
    hardware = get(entry, "Hardware")
    device = get(entry, "Device")

    if hardware:
        # The exact encoder must really work on the reported device.
        assert probe(device, encoder), f"{fmt}: probe failed for reported {encoder} on {device}"
        if suffix in ("vaapi", "qsv"):
            expected = next((n for n in candidates if probe(n, encoder)), None)
            assert expected == device, (
                f"{fmt}: first passing device is {expected}, report says {device}")
            print(f"OK {fmt}: {encoder} on {device} (first passing node)")
        else:
            assert device is None, f"{fmt}: {family} should not carry a device: {entry}"
            print(f"OK {fmt}: {encoder} ({family})")
    else:
        assert not device, f"{fmt}: software decision carries a device: {entry}"
        if suffix:
            hw_name = f"{prefix[fmt]}_{suffix}"
            passing = [n for n in candidates if probe(n, hw_name)]
            assert not passing, f"{fmt}: report says software but {hw_name} passes on {passing}"
        print(f"OK {fmt}: {encoder} (software) - no configured device can encode it")

message = get(engine, "Message")
if all(get(f, "Hardware") for f in formats):
    assert message == "Hardware encoding is available and will be used.", message
elif not any(get(f, "Hardware") for f in formats):
    assert "software encoding" in message, message
else:
    assert "will use software encoding" in message, message
    print("OK partial-hardware message:", message)

print("OK Engine report verified against in-container probes")
PY

# --- 2. Profiles/Validate honours Jellyfin's allowed codecs -----------------
say "checking Profiles/Validate against Jellyfin's allowed codecs"
ENCODING_CODE="$(curl -sS -o "${WORK}/encoding.json" -w '%{http_code}' \
  "${API}/System/Configuration/encoding" -H "$HDR")"
if [ "$ENCODING_CODE" = "200" ]; then
  python3 - "${WORK}/encoding.json" <<'PY'
import json, sys
options = json.load(open(sys.argv[1]))
def get(name):
    for key, value in options.items():
        if key.lower() == name.lower():
            return value
    return None
print(f"AllowHevcEncoding={get('AllowHevcEncoding')} AllowAv1Encoding={get('AllowAv1Encoding')} "
      f"EnableHardwareEncoding={get('EnableHardwareEncoding')} preset={get('EncoderPreset')}")
PY
  python3 - "${WORK}/encoding.json" "${API}" "${HDR}" <<'PY'
import json, subprocess, sys

options = json.load(open(sys.argv[1]))
API = sys.argv[2]
header = sys.argv[3]

def get(name):
    for key, value in options.items():
        if key.lower() == name.lower():
            return value
    return None

def validate(profile):
    payload = json.dumps({
        "Profiles": [profile],
        "MaxConcurrentExports": 1,
        "JobHistoryLimit": 200,
        "NotifyOnCompletion": True,
        "NotifyOnFailure": True,
    })
    result = subprocess.run(
        ["curl", "-fsS", "-X", "POST", f"{API}/JellyfinExporter/Profiles/Validate",
         "-H", header, "-H", "Content-Type: application/json", "-d", payload],
        capture_output=True, text=True, check=True)
    body = json.loads(result.stdout)
    def pick(obj, name):
        for key, value in obj.items():
            if key.lower() == name.lower():
                return value
        return None
    return pick(body, "Valid"), pick(body, "Issues") or []

base = {
    "Id": "11111111-1111-1111-1111-111111111111",
    "Name": "Codec gate",
    "Resolution": "Resolution480p",
    "AudioChannels": "Stereo",
    "OutputDirectory": "/tmp/jfx-exports-gate",
    "Enabled": True,
    "UseJellyfinTranscodingSettings": True,
}

for fmt, allow_key in (("H265", "AllowHevcEncoding"), ("AV1", "AllowAv1Encoding")):
    allowed = get(allow_key)
    valid, issues = validate({**base, "Format": fmt})
    if allowed:
        assert valid, (fmt, issues)
        print(f"OK {fmt}: allowed by Jellyfin, Validate accepts it")
    else:
        assert not valid, (fmt, "expected the save to be blocked")
        blocked = [i for i in issues
                   if (i.get("Field") or i.get("field")) == "Format"]
        assert blocked, issues
        message = (blocked[0].get("Message") or blocked[0].get("message"))
        assert "Use Jellyfin's transcoding settings" in message, message
        print(f"OK {fmt}: blocked by Jellyfin's {allow_key} -> {message}")

# Opting out of Jellyfin's settings must bypass the codec gate.
valid, issues = validate({**base, "Format": "AV1", "UseJellyfinTranscodingSettings": False})
assert valid, issues
print("OK AV1 with the toggle off is accepted regardless of AllowAv1Encoding")
PY
else
  echo "note: GET /System/Configuration/encoding answered ${ENCODING_CODE}; codec-gate check skipped"
fi

# --- 3. real exports: H265 and AV1 both have to finish ----------------------
say "creating a test library and exporting H265 + AV1"
docker exec "$NAME" sh -c 'mkdir -p "/media/QA" && /usr/lib/jellyfin-ffmpeg/ffmpeg -y -hide_banner -loglevel error \
  -f lavfi -i testsrc2=duration=4:size=640x480:rate=15 \
  -f lavfi -i sine=frequency=440:duration=4 \
  -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest \
  "/media/QA/Test Movie (2020).mkv"'
curl -fsS -o /dev/null -X POST "${API}/Library/VirtualFolders?name=QA&collectionType=movies" \
  -H "$HDR" -H 'Content-Type: application/json' \
  -d '{"LibraryOptions":{"PathInfos":[{"Path":"/media/QA"}],"Enabled":true,"EnableRealtimeMonitor":true,"EnableInternetProviders":false,"TypeOptions":[{"Type":"Movie","MetadataFetchers":[],"MetadataFetcherOrder":[]}]}}'
REFRESH_ID="$(curl -fsS "${API}/ScheduledTasks" -H "$HDR" |
  python3 -c 'import json,sys; tasks=json.load(sys.stdin); print(next(t["Id"] for t in tasks if t["Key"]=="RefreshLibrary"))')"
curl -fsS -o /dev/null -X POST "${API}/ScheduledTasks/Running/${REFRESH_ID}" -H "$HDR"
ITEM_ID=""
for _ in $(seq 1 60); do
  ITEM_ID="$(curl -fsS "${API}/JellyfinExporter/Library/Items?type=Movies&limit=5" -H "$HDR" |
    python3 -c '
import json, sys
d = json.load(sys.stdin)
items = next((v for k, v in d.items() if k.lower() == "items"), None) or []
if items:
    print(next(v for k, v in items[0].items() if k.lower() == "id"))
' 2>/dev/null || true)"
  [ -n "$ITEM_ID" ] && break
  sleep 2
done
[ -n "$ITEM_ID" ] || fail "the QA movie never showed up in the library"

# Profiles opt OUT of Jellyfin's transcoding settings here so the allowed-
# codec gate (tested above) cannot mask the encoder-selection check; encoder
# selection itself is independent of the toggle.
curl -fsS "${API}/Plugins/${PLUGIN_GUID}/Configuration" -H "$HDR" >"${WORK}/config-before.json"
python3 - "${WORK}/config-before.json" "${WORK}/config-qa.json" <<'PY'
import json, sys, uuid

def key_of(obj, name):
    for key in obj:
        if key.lower() == name.lower():
            return key
    return name

config = json.load(open(sys.argv[1]))
profiles_key = key_of(config, "Profiles")
config[profiles_key] = [
    {
        "Id": str(uuid.uuid4()),
        "Name": "QA H265 sibling",
        "Format": "H265",
        "Resolution": "Resolution480p",
        "AudioChannels": "Stereo",
        "OutputDirectory": "/tmp/jfx-exports-h265",
        "Enabled": True,
        "UseJellyfinTranscodingSettings": False,
    },
    {
        "Id": str(uuid.uuid4()),
        "Name": "QA AV1 fallback",
        "Format": "AV1",
        "Resolution": "Resolution480p",
        "AudioChannels": "Stereo",
        "OutputDirectory": "/tmp/jfx-exports-av1",
        "Enabled": True,
        "UseJellyfinTranscodingSettings": False,
    },
]
config[key_of(config, "DefaultProfileId")] = config[profiles_key][0]["Id"]
json.dump(config, open(sys.argv[2], "w"))
PY
curl -fsS -o /dev/null -X POST "${API}/Plugins/${PLUGIN_GUID}/Configuration" \
  -H "$HDR" -H 'Content-Type: application/json' -d @"${WORK}/config-qa.json"
PROFILES="$(curl -fsS "${API}/JellyfinExporter/Library/Profiles" -H "$HDR")"
profile_id() {
  printf '%s' "$PROFILES" | python3 -c '
import json, sys
def pick(obj, name):
    return next((v for k, v in obj.items() if k.lower() == name.lower()), None)
data = json.load(sys.stdin)
profiles = pick(data, "Profiles") or []
wanted = sys.argv[1]
print(next(pick(p, "Id") for p in profiles if pick(p, "Name") == wanted))
' "$1"
}
H265_ID="$(profile_id "QA H265 sibling")"
AV1_ID="$(profile_id "QA AV1 fallback")"

for PROFILE_ID in "$H265_ID" "$AV1_ID"; do
  curl -fsS -o /dev/null -X POST "${API}/JellyfinExporter/Library/Export" \
    -H "$HDR" -H 'Content-Type: application/json' \
    -d "{\"itemIds\":[\"${ITEM_ID}\"],\"profileId\":\"${PROFILE_ID}\"}"
done

say "waiting for both exports to finish"
JOBS_JSON=""
TERMINAL=0
for _ in $(seq 1 90); do
  JOBS_JSON="$(curl -fsS "${API}/JellyfinExporter/Jobs?limit=20" -H "$HDR")"
  if printf '%s' "$JOBS_JSON" | python3 -c '
import json, sys
def pick(obj, name):
    return next((v for k, v in obj.items() if k.lower() == name.lower()), None)
jobs = pick(json.load(sys.stdin), "Jobs") or []
fmts = [j for j in jobs if pick(j, "Format") in ("H265", "AV1")]
done = all(pick(j, "Status") in ("Completed", "Failed", "Cancelled") for j in fmts)
sys.exit(0 if len(fmts) >= 2 and done else 1)'; then
    TERMINAL=1
    break
  fi
  sleep 2
done
[ "$TERMINAL" -eq 1 ] || fail "exports did not reach a terminal state: ${JOBS_JSON}"
printf '%s' "$JOBS_JSON" | python3 -c '
import json, sys
def pick(obj, name):
    return next((v for k, v in obj.items() if k.lower() == name.lower()), None)
jobs = pick(json.load(sys.stdin), "Jobs") or []
failed = False
for job in jobs:
    fmt = pick(job, "Format")
    if fmt in ("H265", "AV1"):
        status = pick(job, "Status")
        path = pick(job, "OutputPath")
        print(f"{fmt}: {status} -> {path}")
        if status != "Completed":
            print("  error:", pick(job, "ErrorMessage"), file=sys.stderr)
            failed = True
sys.exit(1 if failed else 0)'
for dir in "$EXPORTS_H265" "$EXPORTS_AV1"; do
  docker exec "$NAME" sh -c "find $dir -name '*.mkv' -size +0c | grep -q ." \
    || fail "no non-empty export under $dir"
done
say "both exports produced a file"

# The engine log names the encoder of every job: it must be exactly what the
# Engine report predicted for that format.
say "checking the encoder each export actually ran"
curl -fsS "${API}/JellyfinExporter/Engine" -H "$HDR" >"${WORK}/engine-after.json"
# Only the per-job engine lines ("Export <id> of '<item>': <encoder> ..."),
# never the detection log line that also names encoders.
docker logs "$NAME" 2>&1 | grep -E "Export [0-9a-f-]+ of '" >"${WORK}/job-logs.txt" || true
cat "${WORK}/job-logs.txt"
python3 - "${WORK}/engine-after.json" "${WORK}/job-logs.txt" <<'PY'
import json, sys
engine = json.load(open(sys.argv[1]))
logs = open(sys.argv[2]).read()

def get(obj, name):
    for key, value in obj.items():
        if key.lower() == name.lower():
            return value
    return None

expected = {get(f, "Format"): get(f, "Encoder") for f in get(engine, "Formats")}
for fmt in ("H265", "AV1"):
    marker = f": {expected[fmt]} "
    assert marker in logs, (
        f"no job log line with the predicted encoder for {fmt} "
        f"(expected '{marker.strip()}' in:\n{logs})")
    print(f"OK {fmt} export ran {expected[fmt]}")
PY

say "PASS: per-codec hardware selection verified end to end on ${RENDER_NODES[*]}"
