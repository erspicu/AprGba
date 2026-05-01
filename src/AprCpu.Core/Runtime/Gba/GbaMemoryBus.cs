using System.Buffers.Binary;

namespace AprCpu.Core.Runtime.Gba;

/// <summary>
/// Phase 4.1 — GBA memory bus. Dispatches reads/writes by region:
/// BIOS / EWRAM / IWRAM / IO (with stub) / Palette / VRAM / OAM / ROM.
///
/// IO read for DISPSTAT (0x04000004) returns <c>VBLANK_FLG=1</c>
/// permanently so jsmolka's <c>m_vsync</c> macro doesn't deadlock
/// (we don't have a real PPU; "always in VBlank" is the cheapest lie
/// that lets headless test ROMs progress). All other IO reads return 0;
/// IO writes are absorbed (logged at debug level if instrumented).
///
/// Read-only enforcement is OFF for Phase 4 — write-to-ROM silently
/// no-ops (matches GBA hardware: GamePak ROM is in fact read-only but
/// stray writes don't fault). BIOS region accepts writes too (we don't
/// model the privileged/non-privileged read protection yet).
///
/// No mirror handling: 0x0A/0x0C ROM mirrors and the various
/// wait-state aliases all hit the same backing array. Phase 5 work.
/// </summary>
public sealed class GbaMemoryBus : IMemoryBus
{
    public byte[] Bios     { get; } = new byte[GbaMemoryMap.BiosSize];
    public byte[] Ewram    { get; } = new byte[GbaMemoryMap.EwramSize];
    public byte[] Iwram    { get; } = new byte[GbaMemoryMap.IwramSize];
    public byte[] Io       { get; } = new byte[GbaMemoryMap.IoSize];
    public byte[] Palette  { get; } = new byte[GbaMemoryMap.PaletteSize];
    public byte[] Vram     { get; } = new byte[GbaMemoryMap.VramSize];
    public byte[] Oam      { get; } = new byte[GbaMemoryMap.OamSize];
    public byte[] Rom      { get; private set; } = Array.Empty<byte>();

    /// <summary>
    /// Load a .gba ROM image into the GamePak ROM region. Caller is
    /// expected to have already read the file into bytes.
    /// </summary>
    public void LoadRom(byte[] romBytes)
    {
        if (romBytes.Length > GbaMemoryMap.RomMaxSize)
            throw new ArgumentException(
                $"ROM size {romBytes.Length} bytes exceeds GBA ROM max ({GbaMemoryMap.RomMaxSize}).",
                nameof(romBytes));
        Rom = romBytes;
    }

    public byte ReadByte(uint addr)
    {
        var (region, off) = Locate(addr);
        return region switch
        {
            Region.Bios    => Bios[off],
            Region.Ewram   => Ewram[off],
            Region.Iwram   => Iwram[off],
            Region.Io      => ReadIoByte((uint)off),
            Region.Palette => Palette[off],
            Region.Vram    => Vram[off],
            Region.Oam     => Oam[off],
            Region.Rom     => off < Rom.Length ? Rom[off] : (byte)0,
            _              => 0,    // OpenBus stub
        };
    }

    public ushort ReadHalfword(uint addr)
    {
        var (region, off) = Locate(addr);
        return region switch
        {
            Region.Bios    => BinaryPrimitives.ReadUInt16LittleEndian(Bios.AsSpan(off, 2)),
            Region.Ewram   => BinaryPrimitives.ReadUInt16LittleEndian(Ewram.AsSpan(off, 2)),
            Region.Iwram   => BinaryPrimitives.ReadUInt16LittleEndian(Iwram.AsSpan(off, 2)),
            Region.Io      => ReadIoHalfword((uint)off),
            Region.Palette => BinaryPrimitives.ReadUInt16LittleEndian(Palette.AsSpan(off, 2)),
            Region.Vram    => BinaryPrimitives.ReadUInt16LittleEndian(Vram.AsSpan(off, 2)),
            Region.Oam     => BinaryPrimitives.ReadUInt16LittleEndian(Oam.AsSpan(off, 2)),
            Region.Rom     => off + 1 < Rom.Length
                                  ? BinaryPrimitives.ReadUInt16LittleEndian(Rom.AsSpan(off, 2))
                                  : (ushort)0,
            _              => 0,
        };
    }

    public uint ReadWord(uint addr)
    {
        var (region, off) = Locate(addr);
        return region switch
        {
            Region.Bios    => BinaryPrimitives.ReadUInt32LittleEndian(Bios.AsSpan(off, 4)),
            Region.Ewram   => BinaryPrimitives.ReadUInt32LittleEndian(Ewram.AsSpan(off, 4)),
            Region.Iwram   => BinaryPrimitives.ReadUInt32LittleEndian(Iwram.AsSpan(off, 4)),
            Region.Io      => ReadIoWord((uint)off),
            Region.Palette => BinaryPrimitives.ReadUInt32LittleEndian(Palette.AsSpan(off, 4)),
            Region.Vram    => BinaryPrimitives.ReadUInt32LittleEndian(Vram.AsSpan(off, 4)),
            Region.Oam     => BinaryPrimitives.ReadUInt32LittleEndian(Oam.AsSpan(off, 4)),
            Region.Rom     => off + 3 < Rom.Length
                                  ? BinaryPrimitives.ReadUInt32LittleEndian(Rom.AsSpan(off, 4))
                                  : 0u,
            _              => 0u,
        };
    }

    public void WriteByte(uint addr, byte value)
    {
        var (region, off) = Locate(addr);
        switch (region)
        {
            case Region.Bios:    Bios[off]    = value; break;
            case Region.Ewram:   Ewram[off]   = value; break;
            case Region.Iwram:   Iwram[off]   = value; break;
            case Region.Io:      Io[off]      = value; break;
            case Region.Palette: Palette[off] = value; break;
            case Region.Vram:    Vram[off]    = value; break;
            case Region.Oam:     Oam[off]     = value; break;
            // Rom & unmapped: silent no-op
        }
    }

    public void WriteHalfword(uint addr, ushort value)
    {
        var (region, off) = Locate(addr);
        var bytes = region switch
        {
            Region.Bios    => Bios,
            Region.Ewram   => Ewram,
            Region.Iwram   => Iwram,
            Region.Io      => Io,
            Region.Palette => Palette,
            Region.Vram    => Vram,
            Region.Oam     => Oam,
            _              => null,
        };
        if (bytes is not null) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(off, 2), value);
    }

    public void WriteWord(uint addr, uint value)
    {
        var (region, off) = Locate(addr);
        var bytes = region switch
        {
            Region.Bios    => Bios,
            Region.Ewram   => Ewram,
            Region.Iwram   => Iwram,
            Region.Io      => Io,
            Region.Palette => Palette,
            Region.Vram    => Vram,
            Region.Oam     => Oam,
            _              => null,
        };
        if (bytes is not null) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(off, 4), value);
    }

    // ---------------- region locator ----------------

    private enum Region { Unmapped, Bios, Ewram, Iwram, Io, Palette, Vram, Oam, Rom }

    private static (Region region, int offset) Locate(uint addr)
    {
        var page = addr >> 24;
        return page switch
        {
            0x00 when addr < GbaMemoryMap.BiosBase + GbaMemoryMap.BiosSize
                => (Region.Bios, (int)(addr - GbaMemoryMap.BiosBase)),
            0x02 => (Region.Ewram, (int)((addr - GbaMemoryMap.EwramBase) % GbaMemoryMap.EwramSize)),
            0x03 => (Region.Iwram, (int)((addr - GbaMemoryMap.IwramBase) % GbaMemoryMap.IwramSize)),
            0x04 when addr < GbaMemoryMap.IoBase + GbaMemoryMap.IoSize
                => (Region.Io, (int)(addr - GbaMemoryMap.IoBase)),
            0x05 => (Region.Palette, (int)((addr - GbaMemoryMap.PaletteBase) % GbaMemoryMap.PaletteSize)),
            0x06 => (Region.Vram, (int)((addr - GbaMemoryMap.VramBase) % GbaMemoryMap.VramSize)),
            0x07 => (Region.Oam, (int)((addr - GbaMemoryMap.OamBase) % GbaMemoryMap.OamSize)),
            0x08 or 0x09 or 0x0A or 0x0B or 0x0C or 0x0D
                => (Region.Rom, (int)((addr - GbaMemoryMap.RomBase) & (GbaMemoryMap.RomMaxSize - 1))),
            _   => (Region.Unmapped, 0),
        };
    }

    // ---------------- IO stub ----------------

    private byte ReadIoByte(uint off)
    {
        // For DISPSTAT we want VBLANK_FLG forever — return the proper bit
        // when the half/byte read hits offset 0x004 or 0x005.
        if (off == GbaMemoryMap.DISPSTAT_Off)     return (byte)(GbaMemoryMap.STAT_VBLANK_FLG & 0xFF);
        if (off == GbaMemoryMap.DISPSTAT_Off + 1) return (byte)((GbaMemoryMap.STAT_VBLANK_FLG >> 8) & 0xFF);
        return Io[off];
    }

    private ushort ReadIoHalfword(uint off)
    {
        if (off == GbaMemoryMap.DISPSTAT_Off) return GbaMemoryMap.STAT_VBLANK_FLG;
        return BinaryPrimitives.ReadUInt16LittleEndian(Io.AsSpan((int)off, 2));
    }

    private uint ReadIoWord(uint off)
    {
        // DISPSTAT (16-bit) sits in lower half of word at 0x04; upper half is VCOUNT (16-bit).
        if (off == GbaMemoryMap.DISPSTAT_Off)
        {
            // VCOUNT = 0x9F (line 159 = end of VBlank window). Arbitrary
            // value just to keep tests that read VCOUNT happy.
            return ((uint)0x9F << 16) | GbaMemoryMap.STAT_VBLANK_FLG;
        }
        return BinaryPrimitives.ReadUInt32LittleEndian(Io.AsSpan((int)off, 4));
    }
}
