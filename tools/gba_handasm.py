#!/usr/bin/env python3
"""
gba_handasm.py — minimal hand-encoded GBA cart builder, zero toolchain.

Encodes a small subset of ARMv4T (the bits needed for tiny demos /
PPU stress tests) by hand from the ARM Architecture Reference Manual,
then writes a complete .gba cart with a valid header skeleton (the
Nintendo logo + checksum are filled in at runtime by `RomPatcher.cs`
when the cart loads under `apr-gba --bios=...`).

Why have this when we already have fasmarm? Three reasons:

  1. Educational — every byte is traceable to an ARM ARM section
  2. Zero install — pure stdlib Python, ships with the repo
  3. Useful for tiny experiments where firing up a real assembler
     is overkill (e.g. "make a 16-byte cart that just sets DISPCNT
     and halts")

Usage as a library:

    from gba_handasm import (
        Cart, mov_imm, orr_imm, strh, str_post_inc, subs_imm,
        bne_back, b_self, lsl_into, b_imm,
    )
    cart = Cart(title="REDDEMO", game_id="DEMO")
    cart.code(...)           # list of 32-bit ARM instructions
    cart.write("out.gba")

Usage as a CLI:

    python tools/gba_handasm.py temp/red_demo.gba
        → Builds the included sample (red mode-3 framebuffer).
"""

from __future__ import annotations
import struct
import sys
from typing import Iterable

# ---------------------------------------------------------------------------
# ARM imm12 (modified immediate) encoding
# ---------------------------------------------------------------------------
#
# Per ARM ARM A5.2.4: a data-processing immediate is encoded as
# imm12 = (rot << 8) | imm8, where the actual operand value is
# imm8 ROR (rot * 2) — only values that can be expressed this way are
# legal. find_imm12() brute-forces the (imm8, rot) pair, returning the
# encoded imm12 or None if the value can't fit.

def find_imm12(value: int) -> int | None:
    """Encode a constant as ARM imm12, or return None if not representable."""
    value &= 0xFFFFFFFF
    for rot in range(16):
        rotation = (rot * 2) & 31
        for imm8 in range(256):
            v = ((imm8 >> rotation) | (imm8 << (32 - rotation))) & 0xFFFFFFFF if rotation \
                else imm8
            if v == value:
                return (rot << 8) | imm8
    return None


def imm12(value: int) -> int:
    """Like find_imm12 but raise ValueError for unencodable values."""
    enc = find_imm12(value)
    if enc is None:
        raise ValueError(f"0x{value:08X} cannot be encoded as ARM imm8 ROR 2*rot")
    return enc


# ---------------------------------------------------------------------------
# Instruction emitters — return a single 32-bit ARM word
# ---------------------------------------------------------------------------
#
# Naming: snake_case, follows ARM ARM mnemonic. All emitters default
# cond = AL (always); add a `cond=` kwarg to override (e.g. cond=0x1
# for NE, 0xA for GE — see ARM ARM A8.3 condition code table).

AL = 0xE
NE = 0x1


def mov_imm(rd: int, value: int, cond: int = AL) -> int:
    """MOV Rd, #value (cccc 0011 1010 0000 Rd imm12)"""
    return (cond << 28) | (0x3A << 20) | (rd << 12) | imm12(value)


def orr_imm(rd: int, rn: int, value: int, cond: int = AL) -> int:
    """ORR Rd, Rn, #value (cccc 0011 1000 Rn Rd imm12)"""
    return (cond << 28) | (0x38 << 20) | (rn << 16) | (rd << 12) | imm12(value)


def lsl_into(rd: int, rn: int, rm: int, shift: int, cond: int = AL) -> int:
    """ORR Rd, Rn, Rm, LSL #shift  — used to OR two halves of a constant.
    For shift=16 this is the canonical "build 32-bit constant from two 16-bit
    halves" idiom: `r1 |= r1 << 16` doubles the low 16 bits into the high half."""
    if not (0 <= shift < 32):
        raise ValueError(f"shift {shift} out of range")
    # Register-shifted form: cccc 0001 1000 Rn Rd shift_imm5 000 Rm
    return (cond << 28) | (0x18 << 20) | (rn << 16) | (rd << 12) \
         | (shift << 7) | (0 << 5) | rm   # 00 = LSL


def strh_post0(rt: int, rn: int, cond: int = AL) -> int:
    """STRH Rt, [Rn]  — half-word store, no offset, no writeback.
    cccc 0001 1100 Rn Rt 0000 1011 0000."""
    return (cond << 28) | (0x1C << 20) | (rn << 16) | (rt << 12) | 0x000000B0


def str_post_inc(rt: int, rn: int, inc: int, cond: int = AL) -> int:
    """STR Rt, [Rn], #inc  — post-increment word store.
    cccc 010 P=0 U=1 0 W=0 0 Rn Rt imm12."""
    if not (0 <= inc < 0x1000):
        raise ValueError(f"post-inc {inc} out of imm12 range")
    return (cond << 28) | (0x48 << 20) | (rn << 16) | (rt << 12) | (inc & 0xFFF)


def subs_imm(rd: int, rn: int, value: int, cond: int = AL) -> int:
    """SUBS Rd, Rn, #value — sets flags. cccc 0010 0101 Rn Rd imm12."""
    return (cond << 28) | (0x25 << 20) | (rn << 16) | (rd << 12) | imm12(value)


def b_imm(target: int, here: int, cond: int = AL) -> int:
    """B target  — `here` is the address of the B itself. ARM B encodes
    imm24 = (target - here - 8) >> 2; condition lives in the top nibble."""
    delta = (target - here - 8) >> 2
    if not (-0x800000 <= delta < 0x800000):
        raise ValueError(f"branch out of range: {target} from {here}")
    return (cond << 28) | (0xA << 24) | (delta & 0xFFFFFF)


def bne_back(steps_back: int) -> int:
    """BNE to an instruction `steps_back` positions BEFORE this BNE.
    Counts only the gap, NOT the BNE itself: a 2-instruction loop body
    (e.g. `str; subs; bne back2`) uses bne_back(2) — branch back 2 from
    the BNE lands on the str.
    Math: ARM B target = pc + 8 + (imm24 << 2). To target an instruction
    `steps_back` words behind PC, imm24 = -steps_back - 2."""
    delta = -steps_back - 2
    return (NE << 28) | (0xA << 24) | (delta & 0xFFFFFF)


def b_self() -> int:
    """B .  — infinite loop branching to itself (canonical ARM halt)."""
    return (AL << 28) | (0xA << 24) | 0xFFFFFE


# ---------------------------------------------------------------------------
# GBA cart skeleton
# ---------------------------------------------------------------------------
#
# Layout per GBATEK "GBA Cartridge Header":
#   0x000..0x003   B start    (jump to code, typically 0xC0)
#   0x004..0x09F   Nintendo logo (156 bytes — RomPatcher fills at runtime)
#   0x0A0..0x0AB   game title (12 ASCII bytes, padded with NUL)
#   0x0AC..0x0AF   game ID (4 ASCII bytes)
#   0x0B0..0x0B1   maker code (2 ASCII bytes)
#   0x0B2          fixed: 0x96
#   0x0B3..0x0BC   reserved + version
#   0x0BD          header checksum (RomPatcher fills at runtime)
#   0x0BE..0x0BF   reserved
#   0x0C0+         cart entry code

CODE_OFFSET = 0xC0


class Cart:
    def __init__(self, title: str = "DEMO", game_id: str = "DEMO", maker: str = "01"):
        self.title    = title.encode("ascii")[:12].ljust(12, b"\0")
        self.game_id  = game_id.encode("ascii")[:4].ljust(4, b"\0")
        self.maker    = maker.encode("ascii")[:2].ljust(2, b"\0")
        self._code: list[int] = []

    def code(self, instructions: Iterable[int]) -> "Cart":
        """Append a list of pre-encoded 32-bit ARM instructions."""
        self._code.extend(instructions)
        return self

    def build(self) -> bytes:
        # Round size up to a power-of-two (smallest commercial cart was 256B
        # but we round to 256 minimum just to keep dump tools happy).
        body_len = max(0x100, CODE_OFFSET + len(self._code) * 4)
        rom = bytearray(body_len)
        # Entry: B from cart start to CODE_OFFSET, computed at runtime cart base 0.
        struct.pack_into("<I", rom, 0x000, b_imm(CODE_OFFSET, 0))
        # Header text fields.
        rom[0x0A0:0x0AC] = self.title
        rom[0x0AC:0x0B0] = self.game_id
        rom[0x0B0:0x0B2] = self.maker
        rom[0x0B2]       = 0x96
        # Code.
        for i, instr in enumerate(self._code):
            struct.pack_into("<I", rom, CODE_OFFSET + i * 4, instr)
        return bytes(rom)

    def write(self, path: str) -> None:
        with open(path, "wb") as f:
            f.write(self.build())


# ---------------------------------------------------------------------------
# Sample: red mode-3 framebuffer
# ---------------------------------------------------------------------------
#
# Pseudo-asm:
#     mov  r0, #0x04000000        ; IO base
#     mov  r1, #0x400
#     orr  r1, r1, #3              ; DISPCNT = 0x0403 (mode 3 + BG2)
#     strh r1, [r0]
#     mov  r0, #0x06000000        ; VRAM
#     mov  r1, #0x1F               ; red lo
#     orr  r1, r1, r1, lsl #16     ; r1 = 0x001F001F (two RGB555 red pixels)
#     mov  r2, #0x4B00             ; 240*160/2 = 19200 word writes
# .fill:
#     str  r1, [r0], #4            ; post-inc word
#     subs r2, r2, #1
#     bne  .fill
# .halt:
#     b    .halt

def red_demo() -> Cart:
    return Cart(title="REDDEMO", game_id="DEMO").code([
        mov_imm(0, 0x04000000),
        mov_imm(1, 0x400),
        orr_imm(1, 1, 3),
        strh_post0(1, 0),
        mov_imm(0, 0x06000000),
        mov_imm(1, 0x1F),
        lsl_into(1, 1, 1, 16),
        mov_imm(2, 0x4B00),
        str_post_inc(1, 0, 4),
        subs_imm(2, 2, 1),
        bne_back(2),                    # branch back to str (2 positions before BNE)
        b_self(),
    ])


def main() -> int:
    out = sys.argv[1] if len(sys.argv) > 1 else "temp/red_demo.gba"
    cart = red_demo()
    cart.write(out)
    rom_size = len(cart.build())
    print(f"Wrote {rom_size} bytes hand-assembled GBA Mode 3 red-screen demo to {out}")
    print(f"Code starts at 0x{CODE_OFFSET:X}, {len(cart._code)} ARM instructions")
    return 0


if __name__ == "__main__":
    sys.exit(main())
