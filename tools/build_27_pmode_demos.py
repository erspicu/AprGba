#!/usr/bin/env python3
"""
Phase 27b protected-mode demo ROM builder.

Compiles the .asm sources in ``test-roms/x86/src/27-pmode-*.asm`` with
NASM into 96-byte .com files in ``test-roms/x86/``. Each .asm uses the
shared macros in ``desc.inc`` (DESC for an 80286 segment descriptor,
GDTR_IMAGE for the LIDT/LGDT memory operand).

Demos and their expected outcome on the i80286 backend
(see ``MD/performance/202605110200-i80286-pmode-fault-model-complete.md``):

  27-pmode-entry        — happy path; BX=0xF1B8, no EXC.
  27-pmode-np           — descriptor.P=0     → #NP, error=0x0008.
  27-pmode-null-ss      — NULL selector → SS → #GP, error=0x0000.
  27-pmode-dpl-gp       — RPL=3 > DPL=0      → #GP, error=0x0008.
  27-pmode-ss-bad-type  — code desc → SS     → #GP, error=0x0008.

Pre-NASM versions of this script (commits before this one) hard-coded
each .com file as a Python ``bytes(...)`` literal, which the .asm
sources now replace one-for-one (verified byte-identical at the time
of conversion). Adding a new demo is now a matter of dropping a
``27-pmode-*.asm`` next to the existing ones.

Requires NASM on PATH (``winget install NASM.NASM``); falls back to the
default install path on Windows.
"""
from __future__ import annotations

import os
import shutil
import subprocess
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
SRC_DIR   = REPO_ROOT / "test-roms" / "x86" / "src"
OUT_DIR   = REPO_ROOT / "test-roms" / "x86"

# Hardcoded fallback for the Windows winget install location, since
# `winget install NASM.NASM` does not add NASM to PATH automatically.
WINDOWS_NASM_FALLBACK = Path(r"C:\Program Files\NASM\nasm.exe")


def find_nasm() -> str:
    on_path = shutil.which("nasm")
    if on_path:
        return on_path
    if os.name == "nt" and WINDOWS_NASM_FALLBACK.exists():
        return str(WINDOWS_NASM_FALLBACK)
    raise FileNotFoundError(
        "nasm not found on PATH. Install via `winget install NASM.NASM` "
        "(Windows) or your distro's package manager."
    )


def assemble(nasm: str, asm_path: Path, out_path: Path) -> int:
    cmd = [
        nasm,
        "-f", "bin",
        "-I", str(SRC_DIR) + os.sep,        # include path for desc.inc
        "-o", str(out_path),
        str(asm_path),
    ]
    proc = subprocess.run(cmd, capture_output=True, text=True)
    if proc.returncode != 0:
        sys.stderr.write(proc.stdout)
        sys.stderr.write(proc.stderr)
        return proc.returncode
    return out_path.stat().st_size


def main() -> int:
    nasm = find_nasm()
    print(f"using {nasm}")

    sources = sorted(SRC_DIR.glob("27-pmode-*.asm"))
    if not sources:
        sys.stderr.write(f"no sources found under {SRC_DIR}\n")
        return 2

    failed = 0
    for src in sources:
        out = OUT_DIR / (src.stem + ".com")
        size = assemble(nasm, src, out)
        if isinstance(size, int) and size >= 0 and out.exists():
            print(f"  wrote {size:>3} bytes -> {out.relative_to(REPO_ROOT)}")
        else:
            sys.stderr.write(f"  FAILED -> {out.relative_to(REPO_ROOT)}\n")
            failed += 1

    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
