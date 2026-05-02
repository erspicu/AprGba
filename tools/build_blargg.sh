#!/usr/bin/env bash
# Rebuild Blargg's GB CPU sub-test ROMs from .s sources using wla-dx.
#
# Setup:
#   1. Download wla-dx Win64 from
#      https://github.com/vhelin/wla-dx/releases/latest
#   2. Extract wla-gb.exe + wlalink.exe to tools/wla-dx/ (gitignored)
#
# Usage:
#   tools/build_blargg.sh                    # rebuild all 11 cpu_instrs sub-tests
#   tools/build_blargg.sh 01                 # just 01-special.gb
#   tools/build_blargg.sh 09                 # just 09-op r,r.gb
#
# Output goes to:
#   temp/blargg/<NN>-<name>.gb               (one .gb per sub-test)
#
# NOTE on "all-in-one" cpu_instrs.gb:
#   The 64 KB MBC1 multi-cart that Blargg ships at
#   test-roms/gb-test-roms-master/cpu_instrs/cpu_instrs.gb is built by
#   a multi-cart wrapper (build_multi.s) that is NOT included in the
#   public source release. Only build_rom.s (single .gb cart) and
#   build_gbs.s (Game Boy Sound music format) ship as build targets.
#   So this script can rebuild any of the 11 individual sub-tests,
#   but cannot regenerate the combined cpu_instrs.gb byte-identically.
#   Each rebuilt single-test ROM still runs through apr-gb correctly
#   and reports "Passed" via serial output.
set -euo pipefail
cd "$(dirname "$0")/.."

WLA_GB="tools/wla-dx/wla-gb.exe"
WLALINK="tools/wla-dx/wlalink.exe"

if [ ! -x "$WLA_GB" ] || [ ! -x "$WLALINK" ]; then
    echo "ERROR: wla-dx not found in tools/wla-dx/." >&2
    echo "Download wla_dx_v10.6_Win64.zip from" >&2
    echo "  https://github.com/vhelin/wla-dx/releases/latest" >&2
    echo "and extract wla-gb.exe + wlalink.exe to tools/wla-dx/." >&2
    exit 1
fi

SRC_DIR="test-roms/gb-test-roms-master/cpu_instrs/source"
OUT_DIR="temp/blargg"
mkdir -p "$OUT_DIR"

build() {
    local prefix="$1"
    # Find matching source: e.g. "01" → "01-special.s"
    local src
    src=$(cd "$SRC_DIR" && ls "${prefix}-"*.s 2>/dev/null | head -1)
    if [ -z "$src" ]; then
        echo "ERROR: no source matches $prefix-*.s in $SRC_DIR" >&2
        return 1
    fi
    # Sanitise filename for output: spaces, commas and parens
    # (e.g. "09-op r,r" → "09-op_r_r", "11-op a,(hl)" → "11-op_a_hl")
    # The .gb still runs identically; only the host filename changes,
    # sparing us shell-quoting headaches.
    local base
    base=$(basename "$src" .s | tr ' ,()' '____' | tr -s '_' | sed 's/_$//')
    local out_gb="$OUT_DIR/${base}.gb"
    local linkfile="$OUT_DIR/${prefix}.link"
    local obj="$OUT_DIR/${prefix}.o"

    echo "==> Building $out_gb from $SRC_DIR/$src"
    (cd "$SRC_DIR" && "../../../../$WLA_GB" -o "../../../../$obj" "$src")

    cat > "$linkfile" <<EOF
[objects]
$(basename "$obj")
EOF
    (cd "$OUT_DIR" && "../../$WLALINK" -r "$(basename "$linkfile")" "$(basename "$out_gb")")

    # Quick sanity check by serial output. 80M t-cycles ≈ 19 DMG-seconds —
    # enough for 09-op_r_r (the longest sub-test, exhaustive ALU r,r matrix).
    # Blargg sub-tests print "<test name>\n\n\nPassed\n" or "Failed #NN\n"
    # to the serial port; we grab the last non-blank line containing
    # Passed or Failed.
    local result
    result=$(dotnet run --project src/AprGb.Cli -c Release \
        -- --rom="$out_gb" --cycles=80000000 2>&1 | grep -aA10 "serial output" | grep -aE "Passed|Failed" | tail -1 | tr -d '\r')
    echo "  → result: ${result:-(no Passed/Failed in serial)}"
}

ALL=("01" "02" "03" "04" "05" "06" "07" "08" "09" "10" "11")

if [ $# -eq 0 ]; then
    for p in "${ALL[@]}"; do build "$p"; done
else
    build "$1"
fi
