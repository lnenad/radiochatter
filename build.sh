#!/usr/bin/env sh
set -eu

CONFIGURATION="${CONFIGURATION:-Release}"
GAME_DIR="${GAME_DIR:-}"
HOME_DIR="${HOME:-}"

usage() {
    echo "Usage: sh ./build.sh [--configuration Release|Debug] [--game-dir PATH]" >&2
}

while [ "$#" -gt 0 ]; do
    case "$1" in
        -c|--configuration)
            [ "$#" -ge 2 ] || { usage; exit 2; }
            CONFIGURATION="$2"
            shift 2
            ;;
        -g|--game-dir)
            [ "$#" -ge 2 ] || { usage; exit 2; }
            GAME_DIR="$2"
            shift 2
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            if [ -n "$GAME_DIR" ]; then
                usage
                exit 2
            fi
            GAME_DIR="$1"
            shift
            ;;
    esac
done

if [ -z "$GAME_DIR" ] && [ -n "$HOME_DIR" ]; then
    for candidate in \
        "$HOME_DIR/.steam/steam/steamapps/common/Nuclear Option" \
        "$HOME_DIR/.local/share/Steam/steamapps/common/Nuclear Option"
    do
        if [ -d "$candidate" ]; then
            GAME_DIR="$candidate"
            break
        fi
    done
fi

if [ -z "$GAME_DIR" ]; then
    if [ -z "$HOME_DIR" ]; then
        echo "GAME_DIR is not set and HOME is unavailable; pass --game-dir PATH." >&2
        exit 2
    fi
    GAME_DIR="$HOME_DIR/.steam/steam/steamapps/common/Nuclear Option"
fi

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
PROJECT="$SCRIPT_DIR/src/RadioChatter/RadioChatter.csproj"

dotnet build "$PROJECT" -c "$CONFIGURATION" "-p:GameDir=$GAME_DIR"

OUT_DLL="$SCRIPT_DIR/src/RadioChatter/bin/$CONFIGURATION/RadioChatter.dll"
PLUGIN_DIR="$GAME_DIR/BepInEx/plugins/RadioChatter"
SIDECAR_SRC="$SCRIPT_DIR/sidecar"
SIDECAR_DIR="$PLUGIN_DIR/sidecar"

mkdir -p "$PLUGIN_DIR"
cp "$OUT_DLL" "$PLUGIN_DIR/"

mkdir -p "$SIDECAR_DIR"
for name in server.py requirements.txt voices.json run_sidecar.bat run_sidecar.sh; do
    if [ -f "$SIDECAR_SRC/$name" ]; then
        cp "$SIDECAR_SRC/$name" "$SIDECAR_DIR/"
    fi
done

if [ -f "$SIDECAR_DIR/run_sidecar.sh" ]; then
    chmod +x "$SIDECAR_DIR/run_sidecar.sh" 2>/dev/null || true
fi

echo "Deployed $OUT_DLL -> $PLUGIN_DIR"
echo "Deployed sidecar files -> $SIDECAR_DIR"
