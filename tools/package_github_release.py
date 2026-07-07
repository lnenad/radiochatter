#!/usr/bin/env python3
"""Build GitHub release assets for RadioChatter."""

from __future__ import annotations

import argparse
import shutil
import subprocess
import zipfile
from pathlib import Path
from xml.etree import ElementTree


PACKAGE_NAME = "RadioChatter"


def repo_root() -> Path:
    return Path(__file__).resolve().parents[1]


def read_version(root: Path) -> str:
    project = root / "src" / "RadioChatter" / "RadioChatter.csproj"
    tree = ElementTree.parse(project)
    version = tree.findtext(".//Version")
    if not version:
        raise RuntimeError(f"Could not find <Version> in {project}")
    return version.strip()


def normalize_version(version: str) -> str:
    version = version.strip()
    if version.startswith("v"):
        version = version[1:]
    if not version:
        raise RuntimeError("Version cannot be empty")
    return version


def run_build(root: Path, configuration: str, game_dir: str | None) -> None:
    project = root / "src" / "RadioChatter" / "RadioChatter.csproj"
    command = ["dotnet", "build", str(project), "-c", configuration]
    if game_dir:
        command.append(f"-p:GameDir={game_dir}")
    subprocess.run(command, cwd=root, check=True)


def copy_sidecar(root: Path, target: Path) -> None:
    sidecar = root / "sidecar"
    target.mkdir(parents=True, exist_ok=True)
    for name in ("server.py", "requirements.txt", "voices.json", "run_sidecar.bat", "run_sidecar.sh"):
        shutil.copy2(sidecar / name, target / name)


def stage_payload_from_build(root: Path, stage: Path, configuration: str) -> None:
    if stage.exists():
        shutil.rmtree(stage)
    stage.mkdir(parents=True)

    dll = root / "src" / "RadioChatter" / "bin" / configuration / "RadioChatter.dll"
    if not dll.exists():
        raise RuntimeError(f"Missing build output: {dll}")

    shutil.copy2(dll, stage / "RadioChatter.dll")
    copy_sidecar(root, stage / "sidecar")


def stage_payload_from_source(source: Path, stage: Path) -> None:
    source = source.resolve()
    if not (source / "RadioChatter.dll").exists():
        raise RuntimeError(f"Payload source is missing RadioChatter.dll: {source}")
    if not (source / "sidecar").is_dir():
        raise RuntimeError(f"Payload source is missing sidecar/: {source}")

    if stage.exists():
        shutil.rmtree(stage)
    shutil.copytree(source, stage)


def make_linux_zip(root: Path, payload: Path, output_zip: Path) -> None:
    if output_zip.exists():
        output_zip.unlink()

    with zipfile.ZipFile(output_zip, "w", zipfile.ZIP_DEFLATED) as archive:
        archive.write(root / "installer" / "linux" / "install-radiochatter.sh", "install-radiochatter.sh")
        archive.write(root / "README.md", "README.md")
        for path in sorted(payload.rglob("*")):
            if path.is_file():
                archive.write(path, path.relative_to(payload).as_posix())


def find_iscc(explicit: str | None) -> str | None:
    if explicit:
        return explicit
    for name in ("ISCC.exe", "iscc"):
        found = shutil.which(name)
        if found:
            return found
    candidates = [
        Path("C:/Program Files (x86)/Inno Setup 6/ISCC.exe"),
        Path("C:/Program Files/Inno Setup 6/ISCC.exe"),
    ]
    for candidate in candidates:
        if candidate.exists():
            return str(candidate)
    return None


def compile_windows_installer(root: Path, payload: Path, version: str, iscc: str | None) -> bool:
    compiler = find_iscc(iscc)
    if not compiler:
        print("Inno Setup compiler not found; skipped Windows installer .exe.")
        print("Install Inno Setup 6 and run this script again, or compile installer/windows/RadioChatterInstaller.iss manually.")
        return False

    script = root / "installer" / "windows" / "RadioChatterInstaller.iss"
    subprocess.run(
        [
            compiler,
            f"/DAppVersion={version}",
            f"/DPayloadDir={payload}",
            str(script),
        ],
        cwd=root,
        check=True,
    )
    return True


def main() -> int:
    root = repo_root()

    parser = argparse.ArgumentParser()
    parser.add_argument("--configuration", default="Release")
    parser.add_argument("--version", default=None, help="Package version. Defaults to <Version> from the csproj.")
    parser.add_argument("--game-dir", default=None)
    parser.add_argument("--skip-build", action="store_true")
    parser.add_argument("--payload-source", default=None, help="Use a prebuilt payload directory containing RadioChatter.dll and sidecar/.")
    parser.add_argument("--output-dir", default=str(root / "dist"))
    parser.add_argument("--iscc", default=None, help="Path to Inno Setup ISCC.exe")
    parser.add_argument("--skip-windows-installer", action="store_true")
    args = parser.parse_args()
    version = normalize_version(args.version) if args.version else read_version(root)

    if args.payload_source and not args.skip_build:
        raise SystemExit("--payload-source requires --skip-build")

    if not args.skip_build:
        run_build(root, args.configuration, args.game_dir)

    output_dir = Path(args.output_dir).resolve()
    payload = output_dir / "github" / "payload"
    if args.payload_source:
        stage_payload_from_source(Path(args.payload_source), payload)
    else:
        stage_payload_from_build(root, payload, args.configuration)

    linux_zip = output_dir / f"{PACKAGE_NAME}-{version}-linux.zip"
    make_linux_zip(root, payload, linux_zip)
    print(f"Wrote {linux_zip}")

    if not args.skip_windows_installer:
        compile_windows_installer(root, payload, version, args.iscc)

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
