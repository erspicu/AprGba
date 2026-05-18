// DiskImage — flat .img file backing the floppy / hard-disk emulation
// for INT 13h HLE.
//
// Layout assumptions:
//   - Sector size is 512 bytes (universal for floppy + most HDD .img
//     files used by FreeDOS / DOSBox / 86Box).
//   - CHS geometry is determined from the file size for floppies
//     (1.44 MB / 720 KB / 360 KB) or derived from --geometry CLI flag
//     for HDDs (deferred — 28.5 ships the floppy path; the same class
//     handles HDD with explicit geometry once 28.8 needs it).
//
// Threading: PcSystemRunner constructs the image on the main thread
// before Resume(); INT 13h reads happen on the emulator thread.
// File-backed writes go through a single lock — typical DOS load
// throughput (~70 KB/s on a 1.44 MB floppy) is far below the lock
// overhead so this is fine.

namespace AprPc.Cli.Hardware;

public enum DiskKind { Floppy, HardDisk }

public sealed class DiskImage
{
    public const int SectorSize = 512;

    private byte[] _bytes;
    private string _path;
    private readonly object _lock = new();
    public DiskKind Kind { get; }
    public int Cylinders { get; private set; }
    public int Heads     { get; private set; }
    public int Sectors   { get; private set; }
    public int TotalSectors => Cylinders * Heads * Sectors;
    public bool ReadOnly  { get; }

    /// <summary>
    /// Path of the currently-loaded backing image. Updated on Swap().
    /// </summary>
    public string Path => _path;

    /// <summary>
    /// DSKCHG signal — set true on Swap(), cleared by FDC on the next
    /// SEEK / RECALIBRATE command per real-hardware semantics. Real
    /// 8272A drives this pin low when the door is opened; PC BIOS /
    /// MS-DOS reads it via port 0x3F7 bit 7.
    ///
    /// CRITICAL: if this is not asserted on disk swap, DOS aggressively
    /// caches FAT sectors and will write disk 1's cached FAT onto disk 2
    /// — permanent filesystem corruption. See Gemini consult
    /// 2026-05-18 (knowledgebase/message/20260518_184350.txt) and the
    /// Phase 32.1 storage plan.
    /// </summary>
    public bool DiskChanged { get; set; }

    private DiskImage(string path, byte[] bytes, DiskKind kind, int c, int h, int s, bool readOnly)
    {
        _path     = path;
        _bytes    = bytes;
        Kind      = kind;
        Cylinders = c;
        Heads     = h;
        Sectors   = s;
        ReadOnly  = readOnly;
    }

    /// <summary>
    /// Load a flat .img file as a floppy disk. Geometry is derived
    /// from the file size:
    ///   360 KB:  40 cyl × 2 heads × 9 sec  (5.25" DS/DD)
    ///   720 KB:  80 cyl × 2 heads × 9 sec  (3.5" DS/DD)
    ///  1.44 MB:  80 cyl × 2 heads × 18 sec (3.5" DS/HD)
    /// Anything else throws — explicit geometry needed.
    /// </summary>
    public static DiskImage LoadFloppy(string path, bool readOnly = false)
    {
        var bytes = File.ReadAllBytes(path);
        (int c, int h, int s) = bytes.Length switch
        {
              360 * 1024 => (40, 2,  9),
              720 * 1024 => (80, 2,  9),
            1440 * 1024 => (80, 2, 18),
            _ => throw new InvalidDataException(
                $"floppy image {path}: size {bytes.Length} bytes does not match a standard floppy geometry (360K / 720K / 1.44M)")
        };
        return new DiskImage(path, bytes, DiskKind.Floppy, c, h, s, readOnly);
    }

    /// <summary>
    /// Load a flat .img file as a hard disk. Geometry is auto-detected
    /// from file size against a table of common DOS-era HDD sizes;
    /// arbitrary sizes fall back to S=63 H=16 C=ceil(sectors/(16*63)).
    /// Sub-504 MB images use traditional CHS; for &gt; 504 MB we still
    /// report 1024×16×63 via INT 13h AH=08 so legacy callers see
    /// "max CHS", and the guest must use LBA extensions (AH=42/43) to
    /// reach the rest. Phase 32.2c stubs AH=41h CF=1 / AH=01h so
    /// FreeDOS LBA-probe doesn't hang.
    /// </summary>
    public static DiskImage LoadHardDisk(string path, bool readOnly = false)
    {
        var bytes = File.ReadAllBytes(path);
        (int c, int h, int s) = DetectHardDiskGeometry(bytes.Length);
        return new DiskImage(path, bytes, DiskKind.HardDisk, c, h, s, readOnly);
    }

    /// <summary>
    /// Common DOS-era HDD sizes mapped to canonical CHS geometry. Order
    /// matters: largest match wins to handle slack space at end-of-image.
    /// Source: tabular geometry per ISA / IDE Reference disk archives +
    /// CompuServe DR-DOS HD database.
    /// </summary>
    internal static (int C, int H, int S) DetectHardDiskGeometry(long imageBytes)
    {
        // Standard table of (size in bytes, C, H, S).
        // 10 MB / 20 MB / 32 MB are XT-class; 40-504 MB are AT-class
        // with S=17 (early MFM/RLL) or S=63 (later IDE LBA-translated).
        (long Size, int C, int H, int S)[] table =
        {
            (10L  * 1024 * 1024, 306,  4, 17),   // ST-412 5 MB (rounded) / 10 MB DOS 2.x
            (20L  * 1024 * 1024, 615,  4, 17),   // 20 MB XT HDD
            (32L  * 1024 * 1024, 615,  6, 17),   // 32 MB DOS 3.3 max single partition
            (40L  * 1024 * 1024, 977,  5, 17),   // 40 MB AT
            (60L  * 1024 * 1024, 1024, 7, 17),
            (80L  * 1024 * 1024, 1024, 9, 17),
            (120L * 1024 * 1024, 977, 12, 17),
            (240L * 1024 * 1024, 1024, 13, 35),
            (504L * 1024 * 1024, 1024, 16, 63),  // INT 13h legacy CHS ceiling
        };
        foreach (var (sz, c, h, s) in table)
            if (imageBytes == sz) return (c, h, s);

        // Heuristic fallback for non-standard sizes:
        //   - Use S=63 H=16 (IDE LBA-translated common case).
        //   - C = ceil(total_sectors / (H * S)), capped at 1024.
        long sectors = imageBytes / SectorSize;
        const int H_ = 16, S_ = 63;
        int C_ = (int)Math.Min(1024L, (sectors + (H_ * S_) - 1) / (H_ * S_));
        if (C_ < 1) C_ = 1;
        return (C_, H_, S_);
    }

    /// <summary>
    /// Convert CHS to a zero-based linear block address. Returns -1
    /// when the (c, h, s) tuple is out of range for this geometry.
    /// </summary>
    public int ChsToLba(int c, int h, int s)
    {
        if ((uint)c >= (uint)Cylinders) return -1;
        if ((uint)h >= (uint)Heads)     return -1;
        if (s < 1 || s > Sectors)       return -1;       // 1-indexed!
        return (c * Heads + h) * Sectors + (s - 1);
    }

    /// <summary>
    /// Copy <paramref name="count"/> sectors starting at LBA
    /// <paramref name="lba"/> into <paramref name="dst"/>. Returns the
    /// number actually copied (may be less if the read runs off the
    /// end of the image).
    /// </summary>
    public int ReadSectors(int lba, int count, Span<byte> dst)
    {
        if (lba < 0 || dst.Length < count * SectorSize) return 0;
        lock (_lock)
        {
            int copied = 0;
            for (int i = 0; i < count; i++)
            {
                int off = (lba + i) * SectorSize;
                if (off + SectorSize > _bytes.Length) break;
                new ReadOnlySpan<byte>(_bytes, off, SectorSize)
                    .CopyTo(dst[(i * SectorSize)..]);
                copied++;
            }
            return copied;
        }
    }

    /// <summary>
    /// Mirror of ReadSectors. Returns the count actually written
    /// (0 if read-only).
    /// </summary>
    public int WriteSectors(int lba, int count, ReadOnlySpan<byte> src)
    {
        if (ReadOnly || lba < 0 || src.Length < count * SectorSize) return 0;
        lock (_lock)
        {
            int wrote = 0;
            for (int i = 0; i < count; i++)
            {
                int off = (lba + i) * SectorSize;
                if (off + SectorSize > _bytes.Length) break;
                src.Slice(i * SectorSize, SectorSize).CopyTo(new Span<byte>(_bytes, off, SectorSize));
                wrote++;
            }
            // Persist eagerly — DOS writes are infrequent and we want
            // the .img file to reflect a clean shutdown crash. For 28.5
            // we just rewrite the whole file; future optimisation can
            // batch by sector range.
            File.WriteAllBytes(_path, _bytes);
            return wrote;
        }
    }

    /// <summary>Sector 0 (the boot sector) as a 512-byte snapshot.</summary>
    public byte[] BootSector()
    {
        var b = new byte[SectorSize];
        ReadSectors(0, 1, b);
        return b;
    }

    /// <summary>
    /// Hot-swap the backing image to a new file. Only supported for
    /// floppy disks — HDD swap (eject) isn't a real-hardware concept.
    /// Re-derives geometry from the new file's size (a 360 KB disk can
    /// follow a 1.44 MB disk in the same slot — the BIOS reads the BPB
    /// to figure out the new geometry on its next access).
    ///
    /// Asserts DSKCHG so the FDC reports the change on port 0x3F7;
    /// FDC clears DSKCHG on the next SEEK / RECALIBRATE per real
    /// 8272A semantics.
    /// </summary>
    public void Swap(string newPath)
    {
        if (Kind != DiskKind.Floppy)
            throw new InvalidOperationException("Swap is only supported for floppy disks");
        var bytes = File.ReadAllBytes(newPath);
        (int c, int h, int s) = bytes.Length switch
        {
              360 * 1024 => (40, 2,  9),
              720 * 1024 => (80, 2,  9),
            1440 * 1024 => (80, 2, 18),
            _ => throw new InvalidDataException(
                $"floppy image {newPath}: size {bytes.Length} bytes does not match a standard floppy geometry (360K / 720K / 1.44M)")
        };
        lock (_lock)
        {
            _bytes    = bytes;
            _path     = newPath;
            Cylinders = c;
            Heads     = h;
            Sectors   = s;
            DiskChanged = true;
        }
    }
}
