#!/usr/bin/env python3
"""Build the Jellyfin Exporter plugin zip.

The zip contains everything Jellyfin needs for a manual install or a plugin
repository download: the plugin assemblies at the archive root, meta.json and
the legacy plugin.xml.

Version and targetAbi are read from Directory.Build.props (the single source of
truth) and stamped into the copies inside the zip. Nothing in the repository is
rewritten by this script.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / "src" / "Jellyfin.Plugin.Exporter"
PROJECT_FILE = PROJECT / "Jellyfin.Plugin.Exporter.csproj"
PROPS_FILE = ROOT / "Directory.Build.props"
DEFAULT_OUTPUT = ROOT / "artifacts"


def read_props() -> dict[str, str]:
    text = PROPS_FILE.read_text(encoding="utf-8")

    def value(name: str) -> str:
        match = re.search(rf"<{name}>([^<]+)</{name}>", text)
        if not match:
            raise SystemExit(f"Directory.Build.props has no <{name}> element")
        return match.group(1).strip()

    return {
        "version": value("Version"),
        "jellyfin": value("JellyfinVersion"),
        "target_abi": value("JellyfinTargetAbi"),
    }


def stamp_meta(meta: dict, props: dict[str, str]) -> dict:
    meta["version"] = props["version"]
    meta["targetAbi"] = props["target_abi"]
    return meta


def stamp_plugin_xml(text: str, props: dict[str, str]) -> str:
    text = re.sub(r"<version>[^<]*</version>", f"<version>{props['version']}</version>", text)
    text = re.sub(r"<targetAbi>[^<]*</targetAbi>", f"<targetAbi>{props['target_abi']}</targetAbi>", text)
    return text


def publish(dotnet: str, configuration: str, out_dir: Path) -> None:
    cmd = [
        dotnet,
        "publish",
        str(PROJECT_FILE),
        "-c",
        configuration,
        "-o",
        str(out_dir),
        "--nologo",
    ]
    print("+", " ".join(cmd))
    subprocess.run(cmd, cwd=ROOT, check=True)


def build_zip(stage: Path, out_zip: Path, meta: dict, props: dict[str, str], plugin_xml: str) -> None:
    out_zip.parent.mkdir(parents=True, exist_ok=True)
    if out_zip.exists():
        out_zip.unlink()

    assemblies = sorted(p for p in stage.iterdir() if p.suffix.lower() == ".dll")
    if not any(p.name == "Jellyfin.Plugin.Exporter.dll" for p in assemblies):
        raise SystemExit(f"publish output in {stage} has no Jellyfin.Plugin.Exporter.dll")

    with zipfile.ZipFile(out_zip, "w", compression=zipfile.ZIP_DEFLATED) as zf:
        for path in assemblies:
            zf.write(path, path.name)
        zf.writestr(
            "meta.json",
            json.dumps(stamp_meta(dict(meta), props), indent=2, ensure_ascii=False) + "\n",
        )
        zf.writestr("plugin.xml", stamp_plugin_xml(plugin_xml, props))


def md5(path: Path) -> str:
    digest = hashlib.md5(usedforsecurity=False)
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("-c", "--configuration", default="Release")
    parser.add_argument(
        "--dotnet",
        default=os.environ.get("DOTNET", "dotnet"),
        help="dotnet executable (default: $DOTNET or dotnet)",
    )
    parser.add_argument("--output-dir", default=str(DEFAULT_OUTPUT))
    parser.add_argument(
        "--skip-publish",
        action="store_true",
        help="reuse the last publish output under the output directory's publish/ folder",
    )
    args = parser.parse_args()

    props = read_props()
    meta = json.loads((PROJECT / "meta.json").read_text(encoding="utf-8"))
    plugin_xml = (PROJECT / "plugin.xml").read_text(encoding="utf-8")

    output_dir = Path(args.output_dir)
    stage = output_dir / "publish"
    if args.skip_publish:
        if not stage.is_dir():
            raise SystemExit(f"--skip-publish given but {stage} does not exist")
    else:
        if stage.exists():
            shutil.rmtree(stage)
        publish(args.dotnet, args.configuration, stage)

    out_zip = output_dir / f"jellyfin-exporter_{props['version']}.zip"
    build_zip(stage, out_zip, meta, props, plugin_xml)

    print(
        json.dumps(
            {
                "zip": str(out_zip),
                "version": props["version"],
                "targetAbi": props["target_abi"],
                "md5": md5(out_zip),
                "bytes": out_zip.stat().st_size,
            },
            indent=2,
        )
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
