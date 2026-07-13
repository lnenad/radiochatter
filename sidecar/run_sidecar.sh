#!/usr/bin/env sh
set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
cd "$SCRIPT_DIR"

INSTALL_ONLY=0
if [ "${1:-}" = "--install-only" ]; then
    INSTALL_ONLY=1
fi

if [ -z "${HF_HOME:-}" ]; then
    HF_HOME="$SCRIPT_DIR/cache/huggingface"
fi
if [ -z "${TORCH_HOME:-}" ]; then
    TORCH_HOME="$SCRIPT_DIR/cache/torch"
fi
if [ -z "${HF_HUB_DISABLE_SYMLINKS_WARNING:-}" ]; then
    HF_HUB_DISABLE_SYMLINKS_WARNING=1
fi
if [ -z "${HF_HUB_ETAG_TIMEOUT:-}" ]; then
    HF_HUB_ETAG_TIMEOUT=5
fi
if [ -z "${HF_HUB_DOWNLOAD_TIMEOUT:-}" ]; then
    HF_HUB_DOWNLOAD_TIMEOUT=15
fi
if [ -z "${UV_CACHE_DIR:-}" ]; then
    UV_CACHE_DIR="$SCRIPT_DIR/cache/uv"
fi
if [ -f "$SCRIPT_DIR/cache/MODEL_BUNDLE.json" ]; then
    HF_HOME="$SCRIPT_DIR/cache/huggingface"
    HF_HUB_OFFLINE=1
    echo "Using bundled RadioChatter voice models; Hugging Face is offline."
fi
export HF_HOME TORCH_HOME HF_HUB_OFFLINE HF_HUB_DISABLE_SYMLINKS_WARNING HF_HUB_ETAG_TIMEOUT HF_HUB_DOWNLOAD_TIMEOUT UV_CACHE_DIR

if [ -z "${PYTHON_EXE:-}" ] && [ -x "$SCRIPT_DIR/.venv/bin/python" ]; then
    PYTHON_EXE="$SCRIPT_DIR/.venv/bin/python"
fi

if [ -z "${PYTHON_EXE:-}" ]; then
    for candidate in "$SCRIPT_DIR"/../.venv-sidecar*/bin/python; do
        if [ -x "$candidate" ]; then
            PYTHON_EXE="$candidate"
            break
        fi
    done
fi

if [ -z "${PYTHON_EXE:-}" ]; then
    if command -v python3 >/dev/null 2>&1; then
        PY_BOOTSTRAP=python3
    elif command -v python >/dev/null 2>&1; then
        PY_BOOTSTRAP=python
    else
        PY_BOOTSTRAP=""
    fi

    if [ -n "$PY_BOOTSTRAP" ]; then
        echo "Creating RadioChatter sidecar environment in $SCRIPT_DIR/.venv..."
        "$PY_BOOTSTRAP" -m venv "$SCRIPT_DIR/.venv"
    else
        # No system Python: bootstrap a private standalone CPython with uv so the
        # mod works on machines without Python installed.
        UV_EXE="$SCRIPT_DIR/uv/uv"
        if [ ! -x "$UV_EXE" ]; then
            case "$(uname -s)/$(uname -m)" in
                Linux/x86_64)  UV_TARGET="uv-x86_64-unknown-linux-gnu" ;;
                Linux/aarch64) UV_TARGET="uv-aarch64-unknown-linux-gnu" ;;
                Darwin/arm64)  UV_TARGET="uv-aarch64-apple-darwin" ;;
                Darwin/x86_64) UV_TARGET="uv-x86_64-apple-darwin" ;;
                *)
                    echo "RadioChatter sidecar needs Python 3.10+ (no uv build for $(uname -s)/$(uname -m))." >&2
                    exit 1
                    ;;
            esac

            echo "No system Python found. Downloading uv to bootstrap a private Python 3.12..."
            mkdir -p "$SCRIPT_DIR/uv"
            UV_URL="https://github.com/astral-sh/uv/releases/latest/download/$UV_TARGET.tar.gz"
            if command -v curl >/dev/null 2>&1; then
                curl -L --fail --silent --show-error -o "$SCRIPT_DIR/uv/uv.tar.gz" "$UV_URL"
            elif command -v wget >/dev/null 2>&1; then
                wget -q -O "$SCRIPT_DIR/uv/uv.tar.gz" "$UV_URL"
            else
                echo "RadioChatter sidecar needs Python 3.10+, or curl/wget so it can download uv." >&2
                exit 1
            fi
            tar -xzf "$SCRIPT_DIR/uv/uv.tar.gz" -C "$SCRIPT_DIR/uv" --strip-components=1
            rm -f "$SCRIPT_DIR/uv/uv.tar.gz"
        fi

        if [ ! -x "$UV_EXE" ]; then
            echo "Failed to download uv. Install Python 3.10+ manually, then try again." >&2
            exit 1
        fi

        if [ -z "${UV_PYTHON_INSTALL_DIR:-}" ]; then
            UV_PYTHON_INSTALL_DIR="$SCRIPT_DIR/uv/python"
            export UV_PYTHON_INSTALL_DIR
        fi
        echo "Creating RadioChatter sidecar environment in $SCRIPT_DIR/.venv (uv-managed Python 3.12)..."
        "$UV_EXE" venv --seed --python 3.12 "$SCRIPT_DIR/.venv"
    fi

    PYTHON_EXE="$SCRIPT_DIR/.venv/bin/python"
    "$PYTHON_EXE" -m pip install --upgrade pip
    "$PYTHON_EXE" -m pip install -r requirements.txt
    if [ "$INSTALL_ONLY" -eq 1 ]; then
        echo "RadioChatter sidecar environment is ready."
        exit 0
    fi
fi

if [ "$INSTALL_ONLY" -eq 1 ]; then
    "$PYTHON_EXE" -m pip install --upgrade pip
    "$PYTHON_EXE" -m pip install -r requirements.txt
    echo "RadioChatter sidecar environment is ready."
    exit 0
fi

# A pre-existing venv may predate or differ from the versions paired with the bundled cache.
if ! "$PYTHON_EXE" -c "import importlib.metadata as m, importlib.util, sys; sys.exit(0 if importlib.util.find_spec('pocket_tts') and importlib.util.find_spec('numpy') and importlib.util.find_spec('faster_whisper') and m.version('pocket-tts') == '2.1.0' and m.version('faster-whisper') == '1.2.1' else 1)" >/dev/null 2>&1; then
    echo "RadioChatter sidecar dependencies are missing or outdated; installing now..."
    "$PYTHON_EXE" -m pip install --upgrade pip
    "$PYTHON_EXE" -m pip install -r requirements.txt
fi

exec "$PYTHON_EXE" server.py --host 127.0.0.1 --port 5075 --voices voices.json --language english
