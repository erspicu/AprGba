#!/usr/bin/env python3
"""
make_dos_hdd.py — pre-partition + pre-format a blank .img file as DOS-ready
single-partition FAT16 hard disk. Skips FreeDOS FDISK entirely.

Usage:
    python tools/make_dos_hdd.py --out=disks/c.img --size-mb=32

What it writes:
 - sector 0 (MBR): partition table with one primary FAT16 partition
   spanning sector 63 to end of disk; boot sector 55 AA magic.
 - sector 63 (FAT16 boot sector): VBR with BPB describing the partition,
   pointing to FAT + root dir; INT 18h fallback (no system files).
 - FAT 1 + FAT 2: all-zero + cluster 0 reserved + EOC marker for
   cluster 1.
 - root directory: empty.

After this, DOS / FreeDOS booted from a different floppy can:
  - `dir C:`        list (empty) root
  - `copy A:*.* C:` populate
  - `SYS C:`        make bootable (writes IO.SYS / MSDOS.SYS + boot loader)
  - reboot, with --hdd=path and no --floppy-a, boot from C:
"""

import argparse, os, struct, sys

SECTOR = 512


def build_mbr(total_sectors: int) -> bytes:
    """MBR with one primary FAT16 partition starting at sector 63."""
    mbr = bytearray(SECTOR)
    # Skip boot code (we don't need it — FreeDOS boot sector handles it
    # by `SYS C:` later). Leave first 446 bytes zero.
    # Partition entry at offset 446 (16 bytes):
    #   +0  active flag (0x80 = bootable)
    #   +1  start head
    #   +2  start sector (bits 0-5) + cyl bits 8-9 (bits 6-7)
    #   +3  start cyl low 8 bits
    #   +4  partition type (0x06 = FAT16 BIG, 0x0E = FAT16 LBA)
    #   +5  end head
    #   +6  end sector + cyl bits
    #   +7  end cyl low
    #   +8  start LBA (DWORD)
    #   +12 size in sectors (DWORD)
    start_lba = 63                    # canonical (CHS 0/1/1)
    size = total_sectors - start_lba
    # Encode CHS for start (head=1, sec=1, cyl=0)
    p0 = bytearray(16)
    p0[0] = 0x80                       # active
    p0[1] = 1                          # start head
    p0[2] = 0x01                       # start sec=1, cyl high bits=0
    p0[3] = 0                          # start cyl low
    p0[4] = 0x06                       # FAT16 BIG (32MB+)
    # End CHS — best-effort approximation (cyl wraps at 1024 anyway;
    # DOS prefers LBA fields for actual ops)
    p0[5] = 0xFE                       # end head 254
    p0[6] = 0x7F                       # end sec=63, cyl high 11:8 = 1
    p0[7] = 0xFF                       # end cyl low (1023)
    struct.pack_into('<I', p0, 8, start_lba)
    struct.pack_into('<I', p0, 12, size)
    mbr[446:446 + 16] = p0
    # Other 3 partition entries already zero
    mbr[510] = 0x55
    mbr[511] = 0xAA
    return bytes(mbr)


def build_fat16_vbr(total_part_sectors: int) -> bytes:
    """
    FAT16 volume boot record with BPB describing the partition.
    Uses 1 reserved sector, 2 FATs of dynamic size, 512 root entries.
    Cluster size auto-sized for the partition.
    """
    vbr = bytearray(SECTOR)
    # JMP short + NOP
    vbr[0] = 0xEB; vbr[1] = 0x3C; vbr[2] = 0x90
    # OEM = "MSDOS5.0" (8 bytes)
    vbr[3:11] = b'MSDOS5.0'
    # BPB
    # Pick cluster size: for FAT16 we want < 65525 clusters.
    # Try cluster sizes 4, 8, 16, 32, 64 sectors. Pick smallest that fits.
    spc = None
    for try_spc in (4, 8, 16, 32, 64):
        # Approximate cluster count after reserving 1 boot + 2*FAT + root dir
        # FAT size: 65536 entries * 2 bytes = 131072 bytes = 256 sectors. Each FAT.
        reserved = 1
        root_ents = 512
        root_sectors = (root_ents * 32 + SECTOR - 1) // SECTOR
        # Estimate FAT sectors (over-estimate then refine):
        approx_clusters = (total_part_sectors - reserved - root_sectors) // try_spc
        fat_bytes = (approx_clusters + 2) * 2
        fat_sectors = (fat_bytes + SECTOR - 1) // SECTOR
        usable_data = total_part_sectors - reserved - 2 * fat_sectors - root_sectors
        clusters = usable_data // try_spc
        if 4085 <= clusters < 65525:
            spc = try_spc
            break
    if spc is None:
        # Fallback: largest cluster
        spc = 64
    reserved = 1
    root_ents = 512
    root_sectors = (root_ents * 32 + SECTOR - 1) // SECTOR
    # Recompute FAT size precisely now that spc is fixed
    fat_sectors = ((total_part_sectors - reserved - root_sectors) // spc * 2 + SECTOR - 1) // SECTOR
    # Ensure FATs don't eat all the disk
    if fat_sectors == 0:
        fat_sectors = 1

    struct.pack_into('<H', vbr, 11, SECTOR)              # bytes/sector
    vbr[13] = spc                                          # sectors/cluster
    struct.pack_into('<H', vbr, 14, reserved)            # reserved sectors
    vbr[16] = 2                                            # FATs
    struct.pack_into('<H', vbr, 17, root_ents)           # root entries
    # total_sectors_16: use 0 if > 65535, then put in total_sectors_32
    if total_part_sectors <= 0xFFFF:
        struct.pack_into('<H', vbr, 19, total_part_sectors)
        struct.pack_into('<I', vbr, 32, 0)
    else:
        struct.pack_into('<H', vbr, 19, 0)
        struct.pack_into('<I', vbr, 32, total_part_sectors)
    vbr[21] = 0xF8                                         # media descriptor (fixed disk)
    struct.pack_into('<H', vbr, 22, fat_sectors)         # sectors per FAT
    struct.pack_into('<H', vbr, 24, 63)                  # sectors per track
    struct.pack_into('<H', vbr, 26, 255)                 # heads
    struct.pack_into('<I', vbr, 28, 63)                  # hidden sectors (= start LBA of partition)
    vbr[36] = 0x80                                         # drive number (HDD)
    vbr[38] = 0x29                                         # extended boot sig
    struct.pack_into('<I', vbr, 39, 0x12345678)          # volume serial
    vbr[43:54] = b'DOS-HDD    '                            # volume label (11 chars)
    vbr[54:62] = b'FAT16   '                                # FAT type
    # Boot code: just `int 0x18` + halt. DOS uses SYS C: to install
    # a real bootloader later.
    boot_code = bytes([
        0xFA,                          # CLI
        0xB4, 0x00,                    # MOV AH, 0
        0xCD, 0x18,                    # INT 18h (no system, fall back)
        0xF4,                          # HLT
        0xEB, 0xFD,                    # JMP -3 (loop if INT 18h returns)
    ])
    vbr[62:62 + len(boot_code)] = boot_code
    vbr[510] = 0x55; vbr[511] = 0xAA
    return bytes(vbr), reserved, fat_sectors, root_sectors, spc


def build_empty_fat(fat_sectors: int) -> bytes:
    fat = bytearray(fat_sectors * SECTOR)
    # Cluster 0 = media descriptor + 0xFF padding
    fat[0] = 0xF8; fat[1] = 0xFF
    # Cluster 1 = EOC marker
    fat[2] = 0xFF; fat[3] = 0xFF
    return bytes(fat)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--out', required=True, help='output .img path')
    ap.add_argument('--size-mb', type=int, default=32, help='total disk size in MB')
    args = ap.parse_args()

    total_sectors = args.size_mb * 1024 * 1024 // SECTOR
    part_start = 63
    part_sectors = total_sectors - part_start
    vbr, reserved, fat_sectors, root_sectors, spc = build_fat16_vbr(part_sectors)
    mbr = build_mbr(total_sectors)
    fat = build_empty_fat(fat_sectors)

    os.makedirs(os.path.dirname(os.path.abspath(args.out)) or '.', exist_ok=True)
    with open(args.out, 'wb') as f:
        # Write whole disk as sparse zero-filled
        f.seek(total_sectors * SECTOR - 1)
        f.write(b'\x00')
        f.seek(0)
        # MBR
        f.write(mbr)
        # FAT16 VBR at partition start
        f.seek(part_start * SECTOR)
        f.write(vbr)
        # FAT 1
        f.seek((part_start + reserved) * SECTOR)
        f.write(fat)
        # FAT 2 (same as FAT 1)
        f.seek((part_start + reserved + fat_sectors) * SECTOR)
        f.write(fat)
        # root directory is zero-filled (already by sparse)
    print(f'wrote {args.out} ({total_sectors} sectors / {args.size_mb} MB)')
    print(f'  partition 0: type 0x06 FAT16, start LBA {part_start}, '
          f'size {part_sectors} sectors ({part_sectors * SECTOR // (1024 * 1024)} MB)')
    print(f'  cluster size: {spc} sectors ({spc * SECTOR} bytes)')
    print(f'  FAT: {fat_sectors} sectors each, 2 FATs')
    print(f'  root dir: {root_sectors} sectors, {512} entries')
    print(f'  boot code: INT 18h fallback (use SYS C: to install real bootloader)')


if __name__ == '__main__':
    main()
