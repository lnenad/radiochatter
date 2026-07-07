#!/usr/bin/env sh
set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
cd "$SCRIPT_DIR"

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

if [ -z "${PYTHON_EXE:-}" ]; then
    if [ -x "$SCRIPT_DIR/.venv/bin/python" ]; then
        PYTHON_EXE="$SCRIPT_DIR/.venv/bin/python"
    else
        for candidate in "$SCRIPT_DIR"/../.venv-sidecar*/bin/python; do
            if [ -x "$candidate" ]; then
                PYTHON_EXE="$candidate"
                break
            fi
        done
    fi
fi

if [ -z "${PYTHON_EXE:-}" ]; then
    if command -v python3 >/dev/null 2>&1; then
        PYTHON_EXE=python3
    else
        PYTHON_EXE=python
    fi
fi

exec "$PYTHON_EXE" server.py --host 127.0.0.1 --port 5075 --voices voices.json --language english
