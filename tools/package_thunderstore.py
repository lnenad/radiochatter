#!/usr/bin/env python3
"""Build a Thunderstore/r2modman package for RadioChatter."""

from __future__ import annotations

import argparse
import json
import shutil
import struct
import subprocess
import zipfile
import zlib
from pathlib import Path
from xml.etree import ElementTree


PACKAGE_NAME = "RadioChatter"
BEPINEX_DEPENDENCY = "BepInEx-BepInExPack-5.4.2305"
DESCRIPTION = "Dynamic Pocket TTS radio chatter for Nuclear Option."
WEBSITE_URL = "https://github.com/lnenad/radiochatter"


def repo_root() -> Path:
    return Path(__file__).resolve().parents[1]


def read_version(root: Path) -> str:
    project = root / "src" / "RadioChatter" / "RadioChatter.csproj"
    tree = ElementTree.parse(project)
    version = tree.findtext(".//Version")
    if not version:
        raise RuntimeError(f"Could not find <Version> in {project}")
    return version.strip()


def run_build(root: Path, configuration: str, game_dir: str | None) -> None:
    project = root / "src" / "RadioChatter" / "RadioChatter.csproj"
    command = ["dotnet", "build", str(project), "-c", configuration]
    if game_dir:
        command.append(f"-p:GameDir={game_dir}")
    subprocess.run(command, cwd=root, check=True)


def write_icon(path: Path) -> None:
    width = height = 256
    rows = []
    for y in range(height):
        row = bytearray()
        for x in range(width):
            dx = x - 128
            dy = y - 128
            dist2 = dx * dx + dy * dy
            r = 16 + y // 16
            g = 24 + x // 18
            b = 34 + (x + y) // 32
            a = 255

            if 34 * 34 <= dist2 <= 43 * 43:
                r, g, b = 238, 188, 74
            elif 58 * 58 <= dist2 <= 64 * 64 and x >= 128:
                r, g, b = 66, 190, 219
            elif 82 * 82 <= dist2 <= 88 * 88 and x >= 128:
                r, g, b = 66, 190, 219
            elif 106 * 106 <= dist2 <= 112 * 112 and x >= 128:
                r, g, b = 66, 190, 219
            elif 90 <= x <= 112 and 70 <= y <= 186:
                r, g, b = 238, 188, 74
            elif 112 <= x <= 136 and 91 <= y <= 165:
                r, g, b = 238, 188, 74
            elif 72 <= x <= 154 and 184 <= y <= 196:
                r, g, b = 238, 188, 74
            elif 106 <= x <= 120 and 196 <= y <= 220:
                r, g, b = 238, 188, 74

            row.extend((r, g, b, a))
        rows.append(b"\x00" + bytes(row))

    def chunk(kind: bytes, data: bytes) -> bytes:
        body = kind + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)

    png = (
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
        + chunk(b"IDAT", zlib.compress(b"".join(rows), 9))
        + chunk(b"IEND", b"")
    )
    path.write_bytes(png)


def copy_sidecar(root: Path, target: Path, include_launchers: bool) -> None:
    sidecar = root / "sidecar"
    target.mkdir(parents=True, exist_ok=True)
    names = ["server.py", "requirements.txt", "voices.json"]
    if include_launchers:
        names.extend(["run_sidecar.bat", "run_sidecar.sh"])

    for name in names:
        source = sidecar / name
        if source.exists():
            shutil.copy2(source, target / name)


def stage_package(
    root: Path,
    stage: Path,
    configuration: str,
    version: str,
    website_url: str,
    include_launchers: bool,
) -> None:
    if stage.exists():
        shutil.rmtree(stage)
    stage.mkdir(parents=True)

    manifest = {
        "name": PACKAGE_NAME,
        "version_number": version,
        "website_url": website_url,
        "description": DESCRIPTION,
        "dependencies": [BEPINEX_DEPENDENCY],
    }
    (stage / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    shutil.copy2(root / "packaging" / "thunderstore" / "README.md", stage / "README.md")
    write_icon(stage / "icon.png")

    plugin_dir = stage / "plugins" / "RadioChatter"
    plugin_dir.mkdir(parents=True)
    dll = root / "src" / "RadioChatter" / "bin" / configuration / "RadioChatter.dll"
    if not dll.exists():
        raise RuntimeError(f"Missing build output: {dll}")
    shutil.copy2(dll, plugin_dir / "RadioChatter.dll")
    copy_sidecar(root, plugin_dir / "sidecar", include_launchers)


def zip_stage(stage: Path, output_zip: Path) -> None:
    if output_zip.exists():
        output_zip.unlink()
    with zipfile.ZipFile(output_zip, "w", zipfile.ZIP_DEFLATED) as archive:
        for path in sorted(stage.rglob("*")):
            if path.is_file():
                archive.write(path, path.relative_to(stage).as_posix())


def main() -> int:
    root = repo_root()
    version = read_version(root)

    parser = argparse.ArgumentParser()
    parser.add_argument("--configuration", default="Release")
    parser.add_argument("--game-dir", default=None)
    parser.add_argument("--skip-build", action="store_true")
    parser.add_argument("--output-dir", default=str(root / "dist"))
    parser.add_argument("--website-url", default=WEBSITE_URL)
    parser.add_argument(
        "--include-launchers",
        action="store_true",
        help="Include run_sidecar.bat/.sh. Avoid this for Thunderstore uploads; the launchers can install code from PyPI.",
    )
    args = parser.parse_args()

    if not args.skip_build:
        run_build(root, args.configuration, args.game_dir)

    output_dir = Path(args.output_dir).resolve()
    stage = output_dir / "thunderstore" / PACKAGE_NAME
    output_zip = output_dir / f"{PACKAGE_NAME}-{version}.zip"

    stage_package(root, stage, args.configuration, version, args.website_url, args.include_launchers)
    zip_stage(stage, output_zip)
    print(f"Wrote {output_zip}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
