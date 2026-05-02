#!/usr/bin/env bash
# Build the three "loop100" stress-test ROMs:
#   GBA: arm-loop100.gba    (jsmolka arm.asm + 100x loop wrapper)
#   GBA: thumb-loop100.gba  (jsmolka thumb.asm + 100x loop wrapper)
#   GB:  09-loop100.gb       (Blargg cpu_instrs sub-test 09 + 100x loop)
#
# Each ROM runs its underlying test framework, then displays an
# extra "x N" line under the result. After 100 successful iterations
# it halts. Used as soak / state-leak / MIPS measurement tests.
#
# Requires:
#   tools/fasmarm/fasmarm.exe       (GBA assembler)
#   tools/wla-dx/wla-gb.exe + wlalink.exe   (GB assembler + linker)
set -euo pipefail
cd "$(dirname "$0")/.."

FASMARM="tools/fasmarm/fasmarm.exe"
WLA_GB="tools/wla-dx/wla-gb.exe"
WLALINK="tools/wla-dx/wlalink.exe"

for tool in "$FASMARM" "$WLA_GB" "$WLALINK"; do
    if [ ! -x "$tool" ]; then
        echo "ERROR: $tool not found. See tools/build_jsmolka.sh and tools/build_blargg.sh for setup." >&2
        exit 1
    fi
done

# --- GBA: arm-loop100.gba ---
echo "==> Building GBA arm-loop100.gba"
(cd test-roms/gba-tests/arm && "../../../$FASMARM" arm-loop100.asm arm-loop100.gba)

# --- GBA: thumb-loop100.gba ---
echo "==> Building GBA thumb-loop100.gba"
(cd test-roms/gba-tests/thumb && "../../../$FASMARM" thumb-loop100.asm thumb-loop100.gba)

# --- GB: 09-loop100.gb (Blargg sub-test 09 op r,r) ---
echo "==> Building GB 09-loop100.gb"
LOOP_DIR="test-roms/gb-test-roms-master/cpu_instrs/loop100"
(cd "$LOOP_DIR" \
    && "../../../../$WLA_GB" -o 09-loop100.o 09-loop100.s \
    && "../../../../$WLALINK" -r linkfile 09-loop100.gb)

echo
echo "Built:"
ls -la test-roms/gba-tests/arm/arm-loop100.gba \
       test-roms/gba-tests/thumb/thumb-loop100.gba \
       "$LOOP_DIR/09-loop100.gb"
