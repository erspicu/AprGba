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

    // DISPSTAT.VBLANK_FLG must oscillate, not stay set, otherwise jsmolka's
    // m_vsync macro deadlocks. The macro waits for the flag to CLEAR, then
    // for it to SET. We toggle on every read; both loops exit after 1-2 reads.
    private int _dispstatReadCount;

    /// <summary>
    /// Install a minimal "BIOS" that just returns from every exception
    /// vector via <c>MOVS PC, LR</c> (encoding 0xE1B0F00E).
    ///
    /// Used by Phase 4.x test runs that want SWI / Undefined / etc. to
    /// be no-ops rather than full HLE. Phase 5+ prefers <see cref="LoadBios"/>
    /// (real LLE BIOS file) for verification credibility.
    ///
    /// Without this, a ROM that issues SWI sees PC jump to 0x00000008
    /// in our zeroed BIOS region, decodes 0x00000000 as AND with cond=EQ,
    /// and wanders unpredictably.
    /// </summary>
    public void InstallMinimalBiosStubs()
    {
        // MOVS PC, LR : 1110_0001_1011_0000_1111_0000_0000_1110 = 0xE1B0F00E
        const uint MovsPcLr = 0xE1B0F00Eu;
        // Vectors at 0x00,04,08,0C,10,(reserved 14),18,1C.
        foreach (var vecOff in new uint[] { 0x00, 0x04, 0x08, 0x0C, 0x10, 0x18, 0x1C })
            BinaryPrimitives.WriteUInt32LittleEndian(Bios.AsSpan((int)vecOff, 4), MovsPcLr);
    }

    /// <summary>
    /// Phase 5: load a real GBA BIOS image into the BIOS region (LLE).
    /// Caller is expected to have read the file into bytes (the canonical
    /// GBA BIOS is exactly 16 KB; smaller blobs are zero-padded).
    /// Replaces any prior <see cref="InstallMinimalBiosStubs"/> call.
    /// </summary>
    public void LoadBios(byte[] biosBytes)
    {
        if (biosBytes is null || biosBytes.Length == 0)
            throw new ArgumentException("BIOS bytes cannot be null/empty.", nameof(biosBytes));
        if (biosBytes.Length > Bios.Length)
            throw new ArgumentException(
                $"BIOS size {biosBytes.Length} exceeds GBA BIOS region ({Bios.Length}).",
                nameof(biosBytes));
        Array.Clear(Bios, 0, Bios.Length);
        Array.Copy(biosBytes, 0, Bios, 0, biosBytes.Length);
    }

    /// <summary>
    /// Phase 5: raise an interrupt — sets the corresponding bit in IF.
    /// PPU/Timer/DMA/keypad call this when their condition fires; the
    /// CPU dispatches via the IRQ vector (0x18) when IME==1 &amp; IE&amp;IF != 0.
    /// </summary>
    public void RaiseInterrupt(GbaInterrupt kind)
    {
        var bit = (ushort)(1 << (int)kind);
        var current = BinaryPrimitives.ReadUInt16LittleEndian(Io.AsSpan((int)GbaMemoryMap.IF_Off, 2));
        BinaryPrimitives.WriteUInt16LittleEndian(Io.AsSpan((int)GbaMemoryMap.IF_Off, 2), (ushort)(current | bit));
    }

    /// <summary>True iff IME is set AND any IE&amp;IF bit is pending.</summary>
    public bool HasPendingInterrupt()
    {
        var ime = BinaryPrimitives.ReadUInt32LittleEndian(Io.AsSpan((int)GbaMemoryMap.IME_Off, 4)) & 1u;
        if (ime == 0) return false;
        var ie = BinaryPrimitives.ReadUInt16LittleEndian(Io.AsSpan((int)GbaMemoryMap.IE_Off, 2));
        var iff = BinaryPrimitives.ReadUInt16LittleEndian(Io.AsSpan((int)GbaMemoryMap.IF_Off, 2));
        return (ie & iff & 0x3FFF) != 0;
    }

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
            case Region.Io:      WriteIoByte((uint)off, value); break;
            case Region.Palette: Palette[off] = value; break;
            case Region.Vram:    Vram[off]    = value; break;
            case Region.Oam:     Oam[off]     = value; break;
            // Rom & unmapped: silent no-op
        }
    }

    public void WriteHalfword(uint addr, ushort value)
    {
        var (region, off) = Locate(addr);
        if (region == Region.Io) { WriteIoHalfword((uint)off, value); return; }
        var bytes = region switch
        {
            Region.Bios    => Bios,
            Region.Ewram   => Ewram,
            Region.Iwram   => Iwram,
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
        if (region == Region.Io)
        {
            WriteIoHalfword((uint)off,       (ushort)(value        & 0xFFFF));
            WriteIoHalfword((uint)(off + 2), (ushort)((value >> 16) & 0xFFFF));
            return;
        }
        var bytes = region switch
        {
            Region.Bios    => Bios,
            Region.Ewram   => Ewram,
            Region.Iwram   => Iwram,
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
        if (off == GbaMemoryMap.DISPSTAT_Off)     return (byte)(NextDispstat() & 0xFF);
        if (off == GbaMemoryMap.DISPSTAT_Off + 1) return (byte)((NextDispstat() >> 8) & 0xFF);
        return Io[off];
    }

    private ushort ReadIoHalfword(uint off)
    {
        if (off == GbaMemoryMap.DISPSTAT_Off) return NextDispstat();
        return BinaryPrimitives.ReadUInt16LittleEndian(Io.AsSpan((int)off, 2));
    }

    private uint ReadIoWord(uint off)
    {
        if (off == GbaMemoryMap.DISPSTAT_Off)
        {
            // VCOUNT in upper 16 bits — arbitrary line number kept stable.
            return ((uint)0x9F << 16) | NextDispstat();
        }
        return BinaryPrimitives.ReadUInt32LittleEndian(Io.AsSpan((int)off, 4));
    }

    /// <summary>
    /// Pseudo-VBlank toggle: alternates VBLANK_FLG between 1 and 0 on
    /// successive reads, so m_vsync (wait-for-clear, then wait-for-set)
    /// completes in 1-2 reads per loop instead of deadlocking.
    /// </summary>
    private ushort NextDispstat()
    {
        _dispstatReadCount++;
        return (_dispstatReadCount & 1) == 1 ? GbaMemoryMap.STAT_VBLANK_FLG : (ushort)0;
    }

    // ---------------- IO write helpers ----------------

    private void WriteIoByte(uint off, byte value)
    {
        // IF (0x202..0x203): "write 1 to clear" semantics.
        if (off == GbaMemoryMap.IF_Off || off == GbaMemoryMap.IF_Off + 1)
        {
            Io[off] = (byte)(Io[off] & ~value);
            return;
        }
        Io[off] = value;
    }

    private void WriteIoHalfword(uint off, ushort value)
    {
        if (off == GbaMemoryMap.IF_Off)
        {
            // Write 1 to clear: new IF = old & ~value.
            var old = BinaryPrimitives.ReadUInt16LittleEndian(Io.AsSpan((int)off, 2));
            BinaryPrimitives.WriteUInt16LittleEndian(Io.AsSpan((int)off, 2), (ushort)(old & ~value));
            return;
        }
        if (off == GbaMemoryMap.DISPSTAT_Off)
        {
            // Bits 0..2 (VBlank/HBlank/VCount FLAGS) are read-only — only
            // bits 3..15 (IRQ enables + VCount target) are writable.
            var old = BinaryPrimitives.ReadUInt16LittleEndian(Io.AsSpan((int)off, 2));
            const ushort writableMask = 0xFFF8;
            var combined = (ushort)((old & ~writableMask) | (value & writableMask));
            BinaryPrimitives.WriteUInt16LittleEndian(Io.AsSpan((int)off, 2), combined);
            return;
        }
        BinaryPrimitives.WriteUInt16LittleEndian(Io.AsSpan((int)off, 2), value);
    }
}
