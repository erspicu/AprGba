#!/usr/bin/env bash
# Rebuild jsmolka GBA test ROMs from .asm sources using fasmarm.
#
# Setup:
#   1. Download fasmarm: http://arm.flatassembler.net/FASMARM_win32.ZIP
#   2. Extract fasmarm.exe to tools/fasmarm/ (gitignored)
#
# Usage:
#   tools/build_jsmolka.sh           # rebuild arm.gba + thumb.gba
#   tools/build_jsmolka.sh arm       # just arm.gba
#   tools/build_jsmolka.sh thumb     # just thumb.gba
set -euo pipefail
cd "$(dirname "$0")/.."

FASMARM="tools/fasmarm/fasmarm.exe"
if [ ! -x "$FASMARM" ]; then
    echo "ERROR: $FASMARM not found." >&2
    echo "Download FASMARM_win32.ZIP from http://arm.flatassembler.net/" >&2
    echo "and extract fasmarm.exe to tools/fasmarm/." >&2
    exit 1
fi

build() {
    local name="$1"
    local src="test-roms/gba-tests/$name/$name.asm"
    local out="test-roms/gba-tests/$name/$name.gba"
    echo "==> Building $out from $src"
    (cd "test-roms/gba-tests/$name" && "../../../$FASMARM" "$name.asm" "$name.gba")
}

case "${1:-all}" in
    arm)   build arm ;;
    thumb) build thumb ;;
    bios)  build bios ;;
    all)   build arm; build thumb; build bios ;;
    *)     echo "Unknown target: $1" >&2; exit 1 ;;
esac
