#!/usr/bin/env python3
"""Build GitHub release assets for RadioChatter."""

from __future__ import annotations

import argparse
import shutil
import subprocess
import zipfile
from pathlib import Path
from xml.etree import ElementTree

from prepare_model_bundle import verify_bundle


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


def read_sidecar_manifest(root: Path) -> list[str]:
    """File list from sidecar/MANIFEST — the single source of truth shared with
    build.ps1 and build.sh."""
    manifest = root / "sidecar" / "MANIFEST"
    names = []
    for line in manifest.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if line and not line.startswith("#"):
            names.append(line)
    if not names:
        raise RuntimeError(f"{manifest} lists no files")
    return names


def copy_sidecar(root: Path, target: Path) -> None:
    sidecar = root / "sidecar"
    target.mkdir(parents=True, exist_ok=True)
    for name in read_sidecar_manifest(root):
        source = sidecar / name
        destination = target / name
        if source.suffix.lower() == ".bat":
            copy_batch_with_crlf(source, destination)
        else:
            shutil.copy2(source, destination)


def copy_batch_with_crlf(source: Path, destination: Path) -> None:
    """cmd.exe can fail to find labels in LF-only batch files after CALL/GOTO."""
    text = source.read_text(encoding="utf-8")
    destination.write_text(text.replace("\r\n", "\n").replace("\n", "\r\n"), encoding="utf-8", newline="")


def stage_payload_from_build(root: Path, stage: Path, configuration: str) -> None:
    if stage.exists():
        shutil.rmtree(stage)
    stage.mkdir(parents=True)

    dll = root / "src" / "RadioChatter" / "bin" / configuration / "RadioChatter.dll"
    if not dll.exists():
        raise RuntimeError(f"Missing build output: {dll}")

    shutil.copy2(dll, stage / "RadioChatter.dll")
    copy_sidecar(root, stage / "sidecar")


def stage_payload_from_source(root: Path, source: Path, stage: Path) -> None:
    """Stage the prebuilt DLL from the payload source, but always take the sidecar
    from sidecar/ in the repo — a committed payload copy of it could silently drift."""
    source = source.resolve()
    if not (source / "RadioChatter.dll").exists():
        raise RuntimeError(f"Payload source is missing RadioChatter.dll: {source}")

    if stage.exists():
        shutil.rmtree(stage)
    stage.mkdir(parents=True)
    shutil.copy2(source / "RadioChatter.dll", stage / "RadioChatter.dll")
    copy_sidecar(root, stage / "sidecar")


def stage_payload_with_models(payload: Path, model_cache: Path, stage: Path) -> None:
    model_cache = model_cache.resolve()
    verify_bundle(model_cache)

    if stage.exists():
        shutil.rmtree(stage)
    shutil.copytree(payload, stage)
    shutil.copytree(model_cache, stage / "sidecar" / "cache")
    normalize_windows_batch_files(stage)


def normalize_windows_batch_files(root: Path) -> None:
    for path in root.rglob("*.bat"):
        text = path.read_text(encoding="utf-8")
        path.write_text(text.replace("\r\n", "\n").replace("\n", "\r\n"), encoding="utf-8", newline="")


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


def compile_windows_installer(
    root: Path,
    payload: Path,
    version: str,
    iscc: str | None,
    bundled_models: bool = False,
) -> bool:
    compiler = find_iscc(iscc)
    if not compiler:
        print("Inno Setup compiler not found; skipped Windows installer .exe.")
        print("Install Inno Setup 6 and run this script again, or compile installer/windows/RadioChatterInstaller.iss manually.")
        return False

    script = root / "installer" / "windows" / "RadioChatterInstaller.iss"
    command = [
        compiler,
        f"/DAppVersion={version}",
        f"/DPayloadDir={payload}",
        f"/DOutputDir={payload.parent.parent}",
    ]
    if bundled_models:
        command.append("/DBundledModels=1")
    command.append(str(script))
    subprocess.run(command, cwd=root, check=True)
    return True


def main() -> int:
    root = repo_root()

    parser = argparse.ArgumentParser()
    parser.add_argument("--configuration", default="Release")
    parser.add_argument("--version", default=None, help="Package version. Defaults to <Version> from the csproj.")
    parser.add_argument("--game-dir", default=None)
    parser.add_argument("--skip-build", action="store_true")
    parser.add_argument("--payload-source", default=None, help="Use a prebuilt payload directory containing RadioChatter.dll and sidecar/.")
    parser.add_argument(
        "--model-cache",
        default=None,
        help="Verified cache from prepare_model_bundle.py; also builds separate with-models assets.",
    )
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
        stage_payload_from_source(root, Path(args.payload_source), payload)
    else:
        stage_payload_from_build(root, payload, args.configuration)

    linux_zip = output_dir / f"{PACKAGE_NAME}-{version}-linux.zip"
    make_linux_zip(root, payload, linux_zip)
    print(f"Wrote {linux_zip}")

    if not args.skip_windows_installer:
        compile_windows_installer(root, payload, version, args.iscc)

    if args.model_cache:
        full_payload = output_dir / "github" / "payload-with-models"
        stage_payload_with_models(payload, Path(args.model_cache), full_payload)

        full_linux_zip = output_dir / f"{PACKAGE_NAME}-{version}-linux-with-models.zip"
        make_linux_zip(root, full_payload, full_linux_zip)
        print(f"Wrote {full_linux_zip}")

        if not args.skip_windows_installer:
            compile_windows_installer(
                root, full_payload, version, args.iscc, bundled_models=True
            )

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
