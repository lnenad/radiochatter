#!/usr/bin/env sh
set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
GAME_DIR="${GAME_DIR:-}"
ASSUME_YES=0
SKIP_SIDECAR_INSTALL=0

usage() {
    echo "Usage: sh install-radiochatter.sh [--game-dir PATH] [--yes] [--skip-sidecar-install]" >&2
}

while [ "$#" -gt 0 ]; do
    case "$1" in
        -g|--game-dir)
            [ "$#" -ge 2 ] || { usage; exit 2; }
            GAME_DIR="$2"
            shift 2
            ;;
        -y|--yes)
            ASSUME_YES=1
            shift
            ;;
        --skip-sidecar-install)
            SKIP_SIDECAR_INSTALL=1
            shift
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            usage
            exit 2
            ;;
    esac
done

find_game_dir() {
    for candidate in \
        "${HOME:-}/.steam/steam/steamapps/common/Nuclear Option" \
        "${HOME:-}/.local/share/Steam/steamapps/common/Nuclear Option"
    do
        if [ -d "$candidate" ]; then
            printf '%s\n' "$candidate"
            return 0
        fi
    done
    return 1
}

if [ -z "$GAME_DIR" ]; then
    GAME_DIR=$(find_game_dir || true)
fi

if [ -z "$GAME_DIR" ]; then
    if [ -t 0 ]; then
        printf 'Nuclear Option game directory: '
        IFS= read -r GAME_DIR
    else
        echo "Could not find Nuclear Option. Pass --game-dir PATH." >&2
        exit 2
    fi
fi

if [ ! -d "$GAME_DIR" ]; then
    echo "Game directory does not exist: $GAME_DIR" >&2
    exit 1
fi

if [ ! -d "$GAME_DIR/BepInEx" ]; then
    echo "BepInEx was not found in: $GAME_DIR" >&2
    echo "Install BepInEx 5 for Nuclear Option first, then rerun this installer." >&2
    exit 1
fi

DLL_SRC="$SCRIPT_DIR/RadioChatter.dll"
SIDECAR_SRC="$SCRIPT_DIR/sidecar"
if [ ! -f "$DLL_SRC" ] || [ ! -d "$SIDECAR_SRC" ]; then
    echo "Installer payload is incomplete. Expected RadioChatter.dll and sidecar/ next to this script." >&2
    exit 1
fi

PLUGIN_DIR="$GAME_DIR/BepInEx/plugins/RadioChatter"
SIDECAR_DIR="$PLUGIN_DIR/sidecar"

mkdir -p "$SIDECAR_DIR"
cp "$DLL_SRC" "$PLUGIN_DIR/RadioChatter.dll"
for name in server.py requirements.txt voices.json run_sidecar.bat run_sidecar.sh; do
    if [ -f "$SIDECAR_SRC/$name" ]; then
        cp "$SIDECAR_SRC/$name" "$SIDECAR_DIR/$name"
    fi
done
chmod +x "$SIDECAR_DIR/run_sidecar.sh" 2>/dev/null || true

echo "Installed RadioChatter to $PLUGIN_DIR"

if [ "$SKIP_SIDECAR_INSTALL" -eq 0 ]; then
    SHOULD_INSTALL="$ASSUME_YES"
    if [ "$SHOULD_INSTALL" -eq 0 ] && [ -t 0 ]; then
        printf 'Prepare Pocket TTS sidecar now? This needs Python 3.10+ and internet. [Y/n] '
        IFS= read -r answer
        case "$answer" in
            n|N|no|NO|No) SHOULD_INSTALL=0 ;;
            *) SHOULD_INSTALL=1 ;;
        esac
    fi

    if [ "$SHOULD_INSTALL" -eq 1 ]; then
        (cd "$SIDECAR_DIR" && sh ./run_sidecar.sh --install-only)
    else
        echo "Skipped sidecar dependency setup. It will run on first auto-start, or you can run sidecar/run_sidecar.sh manually."
    fi
fi

echo "Done."
