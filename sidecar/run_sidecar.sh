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
export HF_HOME TORCH_HOME HF_HUB_DISABLE_SYMLINKS_WARNING HF_HUB_ETAG_TIMEOUT HF_HUB_DOWNLOAD_TIMEOUT

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
        echo "RadioChatter sidecar needs Python 3.10+ to install Pocket TTS dependencies." >&2
        echo "Install Python, or set PYTHON_EXE to an existing environment with sidecar/requirements.txt installed." >&2
        exit 1
    fi

    echo "Creating RadioChatter sidecar environment in $SCRIPT_DIR/.venv..."
    "$PY_BOOTSTRAP" -m venv "$SCRIPT_DIR/.venv"
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

exec "$PYTHON_EXE" server.py --host 127.0.0.1 --port 5075 --voices voices.json --language english
