#!/usr/bin/env python3
"""Prepare the deterministic Hugging Face cache used by with-models release assets."""

from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import tempfile
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class ModelFile:
    repo_id: str
    revision: str
    filename: str
    size: int
    sha256: str

    @property
    def repo_cache_name(self) -> str:
        return "models--" + self.repo_id.replace("/", "--")

    @property
    def hub_relative_path(self) -> Path:
        return Path(self.repo_cache_name) / "snapshots" / self.revision / self.filename


MODEL_FILES = (
    ModelFile(
        "kyutai/pocket-tts-without-voice-cloning",
        "d29db7978e464fb90cb3359ee0c69a273b9142cc",
        "languages/english/model.safetensors",
        219_029_196,
        "be9c6b4876d3f30740a8225dfcaa2e43dc4aeb753c15272735bee16bbb4abb0a",
    ),
    ModelFile(
        "kyutai/pocket-tts-without-voice-cloning",
        "d29db7978e464fb90cb3359ee0c69a273b9142cc",
        "languages/english/tokenizer.model",
        59_339,
        "d461765ae179566678c93091c5fa6f2984c31bbe990bf1aa62d92c64d91bc3f6",
    ),
    ModelFile(
        "kyutai/pocket-tts-without-voice-cloning",
        "e041936c75475d350b405bc870bcf7c22da4e9e6",
        "languages/english/embeddings/eve.safetensors",
        6_538_488,
        "ea9c2faf862a6c9d2cb61910fdf02842ae56940382cc8c1000fdb1b43269692b",
    ),
    ModelFile(
        "kyutai/pocket-tts-without-voice-cloning",
        "e041936c75475d350b405bc870bcf7c22da4e9e6",
        "languages/english/embeddings/george.safetensors",
        6_243_576,
        "0c1c6c57c55a98d81254b33728150c7776f40647fe95258d1a6c1a02780b5d02",
    ),
    ModelFile(
        "kyutai/pocket-tts-without-voice-cloning",
        "e041936c75475d350b405bc870bcf7c22da4e9e6",
        "languages/english/embeddings/paul.safetensors",
        6_980_856,
        "ed7a019168f94dfe77009f1b0de59387abc6fbb0db954d38ce722ecb77da61aa",
    ),
    ModelFile(
        "kyutai/pocket-tts-without-voice-cloning",
        "e041936c75475d350b405bc870bcf7c22da4e9e6",
        "languages/english/embeddings/vera.safetensors",
        6_735_096,
        "4bf50ddd957b5d218b264fdcf18efbbc7384d12da3eca98ca19b9e8dd6976acc",
    ),
    ModelFile(
        "Systran/faster-whisper-base.en",
        "3d3d5dee26484f91867d81cb899cfcf72b96be6c",
        "config.json",
        2_227,
        "f3bc3821e9fc76a27bae538e11ae5b677dcdd352b4600429ce7951d398569aeb",
    ),
    ModelFile(
        "Systran/faster-whisper-base.en",
        "3d3d5dee26484f91867d81cb899cfcf72b96be6c",
        "model.bin",
        145_216_508,
        "2a166925539a16005f14ff328359f9b9adb9dc4fb631bb3b227526862e93e2ef",
    ),
    ModelFile(
        "Systran/faster-whisper-base.en",
        "3d3d5dee26484f91867d81cb899cfcf72b96be6c",
        "tokenizer.json",
        2_128_466,
        "929c5252409436dce1b38a75d1abbcb5e132d170d8e324e4e04ed915fa2d22df",
    ),
    ModelFile(
        "Systran/faster-whisper-base.en",
        "3d3d5dee26484f91867d81cb899cfcf72b96be6c",
        "vocabulary.txt",
        422_309,
        "ff77588746d3a2595d32ab5b69ffd7b95ce2441ac57533cb66fc3eb575a115cf",
    ),
)

WHISPER_REPO_CACHE = "models--Systran--faster-whisper-base.en"
WHISPER_REVISION = "3d3d5dee26484f91867d81cb899cfcf72b96be6c"


def repo_root() -> Path:
    return Path(__file__).resolve().parents[1]


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def locate_hub(source_cache: Path) -> Path:
    candidates = (
        source_cache / "huggingface" / "hub",
        source_cache / "hub",
        source_cache,
    )
    for candidate in candidates:
        if (candidate / MODEL_FILES[0].repo_cache_name).is_dir():
            return candidate
    raise RuntimeError(f"Could not find a Hugging Face hub cache under {source_cache}")


def verify_file(path: Path, spec: ModelFile) -> None:
    if not path.is_file():
        raise RuntimeError(f"Missing model file: {path}")
    actual_size = path.stat().st_size
    if actual_size != spec.size:
        raise RuntimeError(f"Wrong size for {path}: expected {spec.size}, got {actual_size}")
    actual_hash = sha256(path)
    if actual_hash != spec.sha256:
        raise RuntimeError(f"Wrong SHA-256 for {path}: expected {spec.sha256}, got {actual_hash}")


def copy_from_cache(source_cache: Path, output: Path) -> None:
    source_hub = locate_hub(source_cache)
    output_hub = output / "huggingface" / "hub"
    for spec in MODEL_FILES:
        source = source_hub / spec.hub_relative_path
        verify_file(source, spec)
        destination = output_hub / spec.hub_relative_path
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, destination)


def download_models(output: Path) -> None:
    try:
        from huggingface_hub import hf_hub_download
    except ImportError as exc:
        raise RuntimeError(
            "Downloading a model bundle requires huggingface-hub. "
            "Install it or pass --source-cache with an already warm RadioChatter cache."
        ) from exc

    output_hub = output / "huggingface" / "hub"
    with tempfile.TemporaryDirectory(prefix="radiochatter-models-") as temp:
        download_hub = Path(temp) / "hub"
        for spec in MODEL_FILES:
            print(f"Downloading {spec.repo_id}@{spec.revision}: {spec.filename}")
            source = Path(
                hf_hub_download(
                    repo_id=spec.repo_id,
                    filename=spec.filename,
                    revision=spec.revision,
                    cache_dir=download_hub,
                )
            )
            verify_file(source, spec)
            destination = output_hub / spec.hub_relative_path
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source, destination)


def write_metadata(output: Path) -> None:
    whisper_ref = output / "huggingface" / "hub" / WHISPER_REPO_CACHE / "refs" / "main"
    whisper_ref.parent.mkdir(parents=True, exist_ok=True)
    whisper_ref.write_text(WHISPER_REVISION, encoding="ascii", newline="")

    license_source = repo_root() / "installer" / "models" / "MODEL_LICENSES.txt"
    shutil.copy2(license_source, output / "MODEL_LICENSES.txt")

    files = []
    for spec in MODEL_FILES:
        relative = Path("huggingface") / "hub" / spec.hub_relative_path
        files.append(
            {
                "path": relative.as_posix(),
                "size": spec.size,
                "sha256": spec.sha256,
            }
        )

    manifest = {
        "format": 1,
        "description": "RadioChatter bundled model cache",
        "runtime_packages": {
            "pocket-tts": "2.1.0",
            "faster-whisper": "1.2.1",
        },
        "models": [
            {
                "repo_id": "kyutai/pocket-tts-without-voice-cloning",
                "revisions": [
                    "d29db7978e464fb90cb3359ee0c69a273b9142cc",
                    "e041936c75475d350b405bc870bcf7c22da4e9e6",
                ],
                "license": "CC-BY-4.0",
            },
            {
                "repo_id": "Systran/faster-whisper-base.en",
                "revisions": [WHISPER_REVISION],
                "license": "MIT",
            },
        ],
        "files": files,
        "total_model_bytes": sum(spec.size for spec in MODEL_FILES),
    }
    (output / "MODEL_BUNDLE.json").write_text(
        json.dumps(manifest, indent=2) + "\n", encoding="utf-8", newline="\n"
    )


def verify_bundle(output: Path) -> None:
    manifest_path = output / "MODEL_BUNDLE.json"
    if not manifest_path.is_file():
        raise RuntimeError(f"Missing model bundle manifest: {manifest_path}")

    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    if manifest.get("format") != 1:
        raise RuntimeError(f"Unsupported model bundle format in {manifest_path}")

    for spec in MODEL_FILES:
        verify_file(output / "huggingface" / "hub" / spec.hub_relative_path, spec)

    whisper_ref = output / "huggingface" / "hub" / WHISPER_REPO_CACHE / "refs" / "main"
    if whisper_ref.read_text(encoding="ascii") != WHISPER_REVISION:
        raise RuntimeError(f"Invalid faster-whisper main ref: {whisper_ref}")
    if not (output / "MODEL_LICENSES.txt").is_file():
        raise RuntimeError("Model bundle is missing MODEL_LICENSES.txt")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", default=str(repo_root() / "dist" / "model-cache"))
    parser.add_argument(
        "--source-cache",
        default=None,
        help="Copy from an existing RadioChatter/Hugging Face cache instead of downloading.",
    )
    parser.add_argument("--verify-only", action="store_true")
    args = parser.parse_args()

    output = Path(args.output).resolve()
    if args.verify_only:
        verify_bundle(output)
        print(f"Verified model bundle: {output}")
        return 0

    if output.exists():
        shutil.rmtree(output)
    output.mkdir(parents=True)

    if args.source_cache:
        copy_from_cache(Path(args.source_cache).resolve(), output)
    else:
        download_models(output)

    write_metadata(output)
    verify_bundle(output)
    total_mib = sum(spec.size for spec in MODEL_FILES) / (1024 * 1024)
    print(f"Prepared model bundle: {output} ({total_mib:.1f} MiB model data)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
