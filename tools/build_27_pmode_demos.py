#!/usr/bin/env python3
"""
Phase 27b protected-mode demo ROM builder.

Generates two .com files in test-roms/x86/:
  27-pmode-entry.com  — DS load with PRESENT descriptor (P=1, access=0x92)
                        Expected outcome on i80286 backend:
                          BX = 0xF1B8  (code at 0x100, first 2 bytes)
                          EXC_PENDING = 0
  27-pmode-np.com     — DS load with NOT-PRESENT descriptor (P=0, access=0x12)
                        Expected outcome on i80286 backend (Sprint 27.11c+):
                          EXC_PENDING = 1
                          EXC_VECTOR  = 0x0B  (#NP)
                          EXC_ERROR   = 0x0008 (selector & 0xFFFC)

The two demos share the same code prologue + GDT layout; only the
access-rights byte of GDT[1] differs. Re-running this script should
produce byte-identical output for entry.com (sanity / no-drift check).

COM-file layout assumption:
  loaded at CS:0100h (real-mode COM convention)
  IP starts at 0100h
  → file byte 0 == segment offset 0x100 == linear 0x100 (CS=0)
"""
import struct
from pathlib import Path

CODE_PROLOGUE = bytes([
    0xB8, 0xF1, 0xFF,           # mov ax, 0xFFF1   (PE | reserved bits)
    0x0F, 0x01, 0x16, 0x40, 0x01,  # lgdt [0x140]
    0x0F, 0x01, 0xF0,           # lmsw ax          (PE bit goes live)
    0xB8, 0x08, 0x00,           # mov ax, 0x0008   (selector for GDT[1])
    0x8E, 0xD8,                 # mov ds, ax       (descriptor fetch)
    0x8B, 0x1E, 0x00, 0x00,     # mov bx, [0x0000] (read DS:0)
    0xF4,                       # hlt
])
assert len(CODE_PROLOGUE) == 21

# Sprint 27.11d — NULL-SS demo. Same prologue shape (LGDT + LMSW to enter
# pmode), but then loads NULL into SS to trigger #GP.
CODE_NULL_SS = bytes([
    0xB8, 0xF1, 0xFF,           # mov ax, 0xFFF1
    0x0F, 0x01, 0x16, 0x40, 0x01,  # lgdt [0x140]
    0x0F, 0x01, 0xF0,           # lmsw ax
    0xB8, 0x00, 0x00,           # mov ax, 0x0000   (NULL selector)
    0x8E, 0xD0,                 # mov ss, ax       (#GP expected, PE=1)
    0xF4,                       # hlt
])
assert len(CODE_NULL_SS) == 17

# GDTR image: 6 bytes at file offset 0x40 (segment offset 0x140).
#   limit = 0x10 (room for 2 descriptors)
#   base  = 0x150 (segment offset where GDT lives)
GDTR_IMAGE = struct.pack("<HI", 0x0010, 0x00000150)[:6]
assert len(GDTR_IMAGE) == 6

# GDT entry [0] is always 8 zero bytes (NULL descriptor).
GDT_ENTRY_NULL = bytes(8)


def build_descriptor(limit: int, base: int, access: int) -> bytes:
    """80286 8-byte descriptor: limit/base/access/reserved=0/0."""
    return bytes([
        limit & 0xFF, (limit >> 8) & 0xFF,
        base & 0xFF, (base >> 8) & 0xFF, (base >> 16) & 0xFF,
        access & 0xFF,
        0x00, 0x00,  # 80286 reserved
    ])


def build_com(access_byte: int, code: bytes = CODE_PROLOGUE) -> bytes:
    # NOP-pad code to reach file offset 0x40 (segment offset 0x140) for GDTR image.
    pad1 = bytes([0x90] * (0x40 - len(code)))
    # 0x50 - (0x40 + 6) = 10 bytes of padding before GDT.
    pad2 = bytes([0x00] * (0x50 - (0x40 + len(GDTR_IMAGE))))
    gdt_entry_1 = build_descriptor(limit=0xFFFF, base=0x100, access=access_byte)
    return (
        code
        + pad1
        + GDTR_IMAGE
        + pad2
        + GDT_ENTRY_NULL
        + gdt_entry_1
    )


def main():
    out_dir = Path(__file__).resolve().parent.parent / "test-roms" / "x86"
    out_dir.mkdir(parents=True, exist_ok=True)

    entry   = build_com(access_byte=0x92)                     # P=1, S=1, writable data, DPL=0
    np      = build_com(access_byte=0x12)                     # P=0, S=1, writable data, DPL=0
    null_ss = build_com(access_byte=0x92, code=CODE_NULL_SS)  # GDT[1] unused (NULL load)

    (out_dir / "27-pmode-entry.com").write_bytes(entry)
    (out_dir / "27-pmode-np.com").write_bytes(np)
    (out_dir / "27-pmode-null-ss.com").write_bytes(null_ss)

    print(f"wrote {len(entry)} bytes -> 27-pmode-entry.com")
    print(f"wrote {len(np)} bytes -> 27-pmode-np.com")
    print(f"wrote {len(null_ss)} bytes -> 27-pmode-null-ss.com")


if __name__ == "__main__":
    main()
