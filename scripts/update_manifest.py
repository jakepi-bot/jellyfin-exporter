#!/usr/bin/env python3
"""Add (or replace) a version entry in the Jellyfin plugin repository manifest.

The manifest is the file Jellyfin fetches from
Jellyfin > Dashboard > Plugins > Repositories. The release workflow runs this
after uploading the zip so the catalogue advertises the exact bytes that were
published, including their MD5 checksum.
"""

from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
MANIFEST = ROOT / "manifest.json"
META = ROOT / "src" / "Jellyfin.Plugin.Exporter" / "meta.json"
PROPS = ROOT / "Directory.Build.props"
RELEASE_ZIP_URL = "https://github.com/jakepi-bot/jellyfin-exporter/releases/download/v{version}/jellyfin-exporter_{version}.zip"


def md5(path: Path) -> str:
    digest = hashlib.md5(usedforsecurity=False)
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def prop(name: str, default: str | None = None) -> str:
    text = PROPS.read_text(encoding="utf-8")
    match = re.search(rf"<{name}>([^<]+)</{name}>", text)
    if match:
        return match.group(1).strip()
    if default is not None:
        return default
    raise SystemExit(f"Directory.Build.props has no <{name}> element")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--version", default=None, help="plugin version (default: Directory.Build.props)")
    parser.add_argument("--zip", required=True, help="path to the published plugin zip")
    parser.add_argument("--changelog", default=None)
    parser.add_argument("--target-abi", default=None)
    parser.add_argument(
        "--source-url",
        default=None,
        help="direct .zip URL (default: the GitHub release asset for this version)",
    )
    parser.add_argument("--timestamp", default=None, help="ISO-8601 UTC timestamp (default: now)")
    parser.add_argument("--check", action="store_true", help="verify the manifest checksum instead of updating")
    args = parser.parse_args()

    zip_path = Path(args.zip).resolve()
    if not zip_path.is_file():
        raise SystemExit(f"zip not found: {zip_path}")
    checksum = md5(zip_path)
    version = args.version or prop("Version")
    target_abi = args.target_abi or prop("JellyfinTargetAbi", "12.4.0.0")
    meta = json.loads(META.read_text(encoding="utf-8"))
    packages = json.loads(MANIFEST.read_text(encoding="utf-8"))
    package = next(p for p in packages if p["guid"] == meta["guid"])

    if args.check:
        for entry in package["versions"]:
            if entry["version"] == version:
                ok = entry.get("checksum", "").lower() == checksum.lower()
                print(f"{'OK' if ok else 'MISMATCH'} {version} {checksum}")
                return 0 if ok else 1
        print(f"MISSING {version} (checksum {checksum})")
        return 1

    source_url = args.source_url or RELEASE_ZIP_URL.format(version=version)
    entry = {
        "version": version,
        "changelog": args.changelog or meta.get("changelog", ""),
        "targetAbi": target_abi,
        "sourceUrl": source_url,
        "checksum": checksum,
        "timestamp": args.timestamp
        or dt.datetime.now(dt.timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z"),
    }

    versions = [v for v in package["versions"] if v["version"] != version]
    versions.insert(0, entry)
    package["versions"] = versions
    package["name"] = meta.get("name", package.get("name"))
    package["description"] = meta.get("description", package.get("description"))
    package["overview"] = meta.get("overview", package.get("overview"))
    package["owner"] = meta.get("owner", package.get("owner"))
    package["category"] = meta.get("category", package.get("category"))

    MANIFEST.write_text(json.dumps(packages, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(json.dumps({"manifest": str(MANIFEST), "version": version, "checksum": checksum}, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
