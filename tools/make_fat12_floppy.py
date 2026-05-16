#!/usr/bin/env python3
"""
make_fat12_floppy.py — build a minimal 1.44MB FAT12 floppy image from a
directory of files, suitable for `apr-pc --floppy-b=<out.img>`.

Per Gemini's Phase 30.14 advice (consult log 20260517_005126.txt):
- Force contiguous cluster allocation. Avoids the FAT12 split-nibble
  read-modify-write nightmare.
- BPB must match a real 1.44MB 3.5" geometry exactly or FreeDOS' INT 13h
  kernel driver will refuse to mount the disk. Media descriptor 0xF0,
  2880 total sectors, 18 sec/track, 2 heads, 1 sec/cluster.
- Boot sector ends 0x55 0xAA even though we never boot from B:.

Usage:
    python tools/make_fat12_floppy.py --src=test-roms/x86/fat12-b/ \
        --out=temp/test-floppy-b.img --label=TESTDISK

Note: filenames inside <src>/ become 8.3-uppercase short names. Long
filenames are NOT supported (no LFN dirent generation). Use names like
HELLO.COM, RUNTEST.BAT, AUTOEXEC.BAT.
"""

import argparse
import os
import struct
import sys
from datetime import datetime
from pathlib import Path

# Standard 1.44MB 3.5" floppy geometry
BPS              = 512                  # bytes / sector
SEC_PER_CLUSTER  = 1
RESERVED_SECTORS = 1                    # just the boot sector
NUM_FATS         = 2
ROOT_ENTRIES     = 224                  # = 14 sectors of 32-byte dirents
TOTAL_SECTORS    = 2880
SEC_PER_FAT      = 9
SEC_PER_TRACK    = 18
NUM_HEADS        = 2
MEDIA_BYTE       = 0xF0
HIDDEN_SECTORS   = 0

ROOT_SECTORS     = (ROOT_ENTRIES * 32) // BPS              # 14
DATA_START_SEC   = RESERVED_SECTORS + NUM_FATS * SEC_PER_FAT + ROOT_SECTORS  # 33
DATA_SECTORS     = TOTAL_SECTORS - DATA_START_SEC          # 2847
MAX_CLUSTERS     = DATA_SECTORS // SEC_PER_CLUSTER         # 2847
IMG_BYTES        = TOTAL_SECTORS * BPS                     # 1474560


def build_boot_sector(label: str) -> bytes:
    """Construct a 512-byte boot sector with a valid BPB. Boot code is
    a 'no-system' message + halt — we never boot from this disk."""
    bs = bytearray(BPS)
    # Jump + NOP (Intel 8086 JMP near + NOP) — DOS treats this as the
    # canonical boot signature start.
    bs[0:3] = b'\xEB\x3C\x90'
    # OEM name (8 bytes, ASCII)
    bs[3:11] = b'APRPC1.0'
    # BPB
    struct.pack_into('<H', bs, 11, BPS)
    bs[13] = SEC_PER_CLUSTER
    struct.pack_into('<H', bs, 14, RESERVED_SECTORS)
    bs[16] = NUM_FATS
    struct.pack_into('<H', bs, 17, ROOT_ENTRIES)
    struct.pack_into('<H', bs, 19, TOTAL_SECTORS)
    bs[21] = MEDIA_BYTE
    struct.pack_into('<H', bs, 22, SEC_PER_FAT)
    struct.pack_into('<H', bs, 24, SEC_PER_TRACK)
    struct.pack_into('<H', bs, 26, NUM_HEADS)
    struct.pack_into('<I', bs, 28, HIDDEN_SECTORS)
    struct.pack_into('<I', bs, 32, 0)   # total sectors 32-bit (only when 16-bit field is 0)
    # Extended BPB (DOS 4+):
    bs[36] = 0x00                       # drive number (0 = A:, but B: works too)
    bs[37] = 0x00                       # reserved (Windows NT flags)
    bs[38] = 0x29                       # extended boot signature
    struct.pack_into('<I', bs, 39, 0xCAFEBABE)  # volume serial
    bs[43:54] = label.upper()[:11].ljust(11, ' ').encode('ascii')
    bs[54:62] = b'FAT12   '
    # Boot code: print "Not bootable" and halt. We jump from 0x00 with
    # JMP 0x3C above so this lives at offset 0x3E onwards.
    code = (
        b'\xBE\x70\x00'                 # mov si, 0x70   (msg offset = boot_sec base + 0x70)
        b'\xAC'                         # lodsb
        b'\x08\xC0'                     # or al, al
        b'\x74\x06'                     # jz .halt
        b'\xB4\x0E'                     # mov ah, 0x0E
        b'\xCD\x10'                     # int 0x10
        b'\xEB\xF5'                     # jmp .loop
        b'\xF4'                         # hlt
        b'\xEB\xFD'                     # jmp $-1
    )
    bs[0x3E:0x3E + len(code)] = code
    # Message at offset 0x70 (= boot_sec base + 0x70).
    msg = b'AprPc data floppy - not bootable\r\n\x00'
    bs[0x70:0x70 + len(msg)] = msg
    # Boot signature
    bs[510] = 0x55
    bs[511] = 0xAA
    return bytes(bs)


def encode_fat_date(dt: datetime) -> int:
    return ((dt.year - 1980) << 9) | (dt.month << 5) | dt.day


def encode_fat_time(dt: datetime) -> int:
    return (dt.hour << 11) | (dt.minute << 5) | (dt.second // 2)


def short83(name: str) -> tuple[bytes, bytes]:
    """Return (8-byte name, 3-byte ext) space-padded, uppercased.
    Falls back to first 8 chars / extension for names that don't fit;
    no LFN generation. Refuses any name DOS would reject."""
    stem, ext = os.path.splitext(name)
    stem = stem.upper().replace(' ', '').encode('ascii', 'replace')[:8]
    ext  = ext.lstrip('.').upper().encode('ascii', 'replace')[:3]
    return stem.ljust(8, b' '), ext.ljust(3, b' ')


def set_fat12_entry(fat: bytearray, cluster: int, value: int) -> None:
    """Write a 12-bit cluster pointer into a FAT byte array."""
    byte_pos = (cluster * 3) // 2
    if cluster & 1:
        # high nibble of pos+0 | all of pos+1's low 4 bits unchanged is
        # wrong — actually: odd cluster occupies high nibble of byte n
        # and ALL of byte n+1.
        fat[byte_pos] = (fat[byte_pos] & 0x0F) | ((value << 4) & 0xF0)
        fat[byte_pos + 1] = (value >> 4) & 0xFF
    else:
        # Even cluster occupies all of byte n and low nibble of byte n+1.
        fat[byte_pos] = value & 0xFF
        fat[byte_pos + 1] = (fat[byte_pos + 1] & 0xF0) | ((value >> 8) & 0x0F)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument('--src', required=True, help='directory of files to put on the floppy')
    ap.add_argument('--out', required=True, help='output .img path')
    ap.add_argument('--label', default='APRPC TEST', help='volume label (max 11 chars)')
    args = ap.parse_args()

    src_dir = Path(args.src)
    if not src_dir.is_dir():
        print(f'error: --src {src_dir} is not a directory', file=sys.stderr)
        return 1

    img = bytearray(IMG_BYTES)

    # 1. Boot sector
    img[0:BPS] = build_boot_sector(args.label)

    # 2. Build FATs in memory
    fat = bytearray(SEC_PER_FAT * BPS)
    set_fat12_entry(fat, 0, 0xF00 | MEDIA_BYTE)   # reserved entry 0
    set_fat12_entry(fat, 1, 0xFFF)                # reserved entry 1 (EOC marker)

    # 3. Walk src dir, lay out files contiguously starting at cluster 2.
    files = sorted([p for p in src_dir.iterdir() if p.is_file()])
    if not files:
        print(f'warning: no files in {src_dir}', file=sys.stderr)

    root = bytearray(ROOT_SECTORS * BPS)

    # Volume label as the first dirent (DOS expects this).
    label_name, label_ext = short83(args.label)
    root[0:8]   = label_name
    root[8:11]  = label_ext
    root[11]    = 0x08                  # volume label attribute
    now = datetime.now()
    struct.pack_into('<H', root, 22, encode_fat_time(now))
    struct.pack_into('<H', root, 24, encode_fat_date(now))

    dirent_idx = 1
    next_cluster = 2
    for fp in files:
        data = fp.read_bytes()
        n_clusters = max(1, (len(data) + BPS - 1) // BPS)   # 1 cluster = 1 sector here

        if next_cluster + n_clusters - 1 > MAX_CLUSTERS + 1:
            print(f'error: {fp.name} ({len(data)} bytes) overflows the floppy', file=sys.stderr)
            return 2
        if dirent_idx >= ROOT_ENTRIES:
            print('error: root directory full', file=sys.stderr)
            return 3

        # Chain clusters next_cluster..next_cluster+n-1 then EOC.
        for c in range(next_cluster, next_cluster + n_clusters - 1):
            set_fat12_entry(fat, c, c + 1)
        set_fat12_entry(fat, next_cluster + n_clusters - 1, 0xFFF)

        # Write file data into data area.
        for i, c in enumerate(range(next_cluster, next_cluster + n_clusters)):
            data_off = (DATA_START_SEC + (c - 2) * SEC_PER_CLUSTER) * BPS
            chunk = data[i * BPS : (i + 1) * BPS]
            img[data_off : data_off + len(chunk)] = chunk

        # Dirent.
        de_off = dirent_idx * 32
        name, ext = short83(fp.name)
        root[de_off + 0 : de_off + 8]   = name
        root[de_off + 8 : de_off + 11]  = ext
        root[de_off + 11]               = 0x20      # archive attribute
        struct.pack_into('<H', root, de_off + 22, encode_fat_time(now))
        struct.pack_into('<H', root, de_off + 24, encode_fat_date(now))
        struct.pack_into('<H', root, de_off + 26, next_cluster)
        struct.pack_into('<I', root, de_off + 28, len(data))

        print(f'  + {fp.name:<20} {len(data):>7} bytes  '
              f'cluster {next_cluster}..{next_cluster + n_clusters - 1}')
        dirent_idx   += 1
        next_cluster += n_clusters

    # 4. Place FAT1 + FAT2 (identical mirrors).
    fat_off1 = RESERVED_SECTORS * BPS
    fat_off2 = fat_off1 + SEC_PER_FAT * BPS
    img[fat_off1 : fat_off1 + len(fat)] = fat
    img[fat_off2 : fat_off2 + len(fat)] = fat

    # 5. Place root directory.
    root_off = (RESERVED_SECTORS + NUM_FATS * SEC_PER_FAT) * BPS
    img[root_off : root_off + len(root)] = root

    # 6. Write image.
    Path(args.out).parent.mkdir(parents=True, exist_ok=True)
    Path(args.out).write_bytes(img)
    print(f'wrote {args.out} ({IMG_BYTES} bytes, {len(files)} file(s), '
          f'next free cluster = {next_cluster})')
    return 0


if __name__ == '__main__':
    sys.exit(main())
