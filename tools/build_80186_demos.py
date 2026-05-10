#!/usr/bin/env python3
"""Build 80186-only demo .com files for Phase 25 visual proof.

Demos exercise instructions that don't exist on i8086 (PUSHA/POPA/ENTER/
LEAVE), so they:
- run cleanly through apr-x86 --variant=i80186
- explode (NotImplementedException) through --variant=i8086

Output: test-roms/x86/25-pusha-popa.com, 25-enter-leave.com.
"""
from pathlib import Path

ROOT = Path(__file__).parent.parent
OUT_DIR = ROOT / "test-roms" / "x86"
OUT_DIR.mkdir(parents=True, exist_ok=True)


def write_demo(name: str, bytecode: bytes, comment: str):
    path = OUT_DIR / name
    path.write_bytes(bytecode)
    print(f"  wrote {path} ({len(bytecode)} bytes)  -- {comment}")


# ============================================================================
# 25-pusha-popa.com — PUSHA / POPA roundtrip.
#
# Assembly (CP/M-86 convention, .com loads at CS:0x0100):
#   mov bx, 0x1234     BB 34 12
#   mov cx, 0x5678     B9 78 56
#   mov dx, 0x9ABC     BA BC 9A
#   pusha              60        ; save AX,CX,DX,BX,orig-SP,BP,SI,DI
#   xor bx, bx         31 DB     ; clobber BX = 0
#   xor cx, cx         31 C9     ; clobber CX = 0
#   xor dx, dx         31 D2     ; clobber DX = 0
#   popa               61        ; restore — should give BX=1234,CX=5678,DX=9ABC
#   hlt                F4
#
# After HLT: BX=0x1234, CX=0x5678, DX=0x9ABC. If PUSHA were unimplemented
# (i8086 backend), the program would either throw or produce garbage.
# ============================================================================
write_demo(
    "25-pusha-popa.com",
    bytes([
        0xBB, 0x34, 0x12,      # mov bx, 0x1234
        0xB9, 0x78, 0x56,      # mov cx, 0x5678
        0xBA, 0xBC, 0x9A,      # mov dx, 0x9ABC
        0x60,                  # pusha
        0x31, 0xDB,            # xor bx, bx
        0x31, 0xC9,            # xor cx, cx
        0x31, 0xD2,            # xor dx, dx
        0x61,                  # popa
        0xF4,                  # hlt
    ]),
    "PUSHA -> clobber -> POPA — final BX=1234 CX=5678 DX=9ABC if PUSHA/POPA work"
)


# ============================================================================
# 25-enter-leave.com — ENTER imm16, imm8 + LEAVE round-trip.
#
# Assembly:
#   mov sp, 0x1000     BC 00 10  ; sane stack
#   mov bp, 0x2222     BD 22 22  ; sentinel BP
#   enter 8, 0         C8 08 00 00   ; alloc 8-byte local frame, nest=0
#                                    ; effect: push BP_old; BP = SP_after_push;
#                                    ;         SP -= 8
#   mov ax, bp         89 E8     ; AX = current BP (= SP at ENTER's start - 2)
#   leave              C9        ; SP = BP; pop BP — BP back to 0x2222
#   mov cx, bp         89 E9     ; CX = restored BP, should be 0x2222
#   hlt                F4
#
# After HLT: AX = 0x0FFE (= 0x1000 - 2 from BP push), BP = 0x2222 (restored),
# CX = 0x2222 (echo of restored BP), SP = 0x1000.
# ============================================================================
write_demo(
    "25-enter-leave.com",
    bytes([
        0xBC, 0x00, 0x10,      # mov sp, 0x1000
        0xBD, 0x22, 0x22,      # mov bp, 0x2222
        0xC8, 0x08, 0x00, 0x00,  # enter 8, 0
        0x89, 0xE8,            # mov ax, bp   (AX = BP after ENTER)
        0xC9,                  # leave
        0x89, 0xE9,            # mov cx, bp   (CX = restored BP)
        0xF4,                  # hlt
    ]),
    "ENTER 8,0 -> mov ax,bp -> LEAVE -> mov cx,bp — BP restored to 0x2222, AX=frame"
)
