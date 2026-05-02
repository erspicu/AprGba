namespace AprCpu.Core.Runtime.Gba;

/// <summary>
/// Phase 5: GBA cycle scheduler. Tracks scanline + position-in-scanline,
/// updates VCOUNT in IO, fires VBlank and HBlank events when the scheduler
/// crosses the corresponding boundaries.
///
/// GBA timing constants (GBATEK):
/// <list type="bullet">
///   <item>1 dot = 4 CPU cycles</item>
///   <item>1 scanline = 308 dots = 1232 cycles
///         (240 dots HDraw + 68 dots HBlank)</item>
///   <item>1 frame = 228 scanlines = 280896 cycles
///         (160 visible scanlines + 68 VBlank scanlines)</item>
/// </list>
///
/// Owner pattern: a per-frame cycle budget (typical RunFrames(n) call)
/// pumps Tick(deltaCycles) from inside the CPU loop. Each Tick advances
/// _cycleInScanline + _scanline. Boundary crossings fire the host callbacks.
///
/// Scope (matches Phase 5 minimum):
/// <list type="bullet">
///   <item>VBlank entry (scanline 160) → bus.RaiseInterrupt(VBlank) +
///         dma.TriggerOnVBlank() if DISPSTAT.VBLANK_IE set</item>
///   <item>HBlank entry (within visible scanlines only) → similar for HBlank</item>
///   <item>VCOUNT register kept in sync</item>
///   <item>VCount-match IRQ (scanline == DISPSTAT[15:8]) when bit 5 enable set</item>
/// </list>
/// </summary>
public sealed class GbaScheduler
{
    private readonly GbaMemoryBus _bus;

    public const int CyclesPerDot      = 4;
    public const int DotsHDraw         = 240;
    public const int DotsHBlank        = 68;
    public const int CyclesPerScanline = (DotsHDraw + DotsHBlank) * CyclesPerDot;     // 1232
    public const int VisibleScanlines  = 160;
    public const int VBlankScanlines   = 68;
    public const int TotalScanlines    = VisibleScanlines + VBlankScanlines;          // 228
    public const int CyclesPerFrame    = CyclesPerScanline * TotalScanlines;          // 280896
    public const int CyclesHDraw       = DotsHDraw * CyclesPerDot;                    // 960

    private int _cycleInScanline;     // 0..1231
    private int _scanline;            // 0..227
    private bool _inHBlank;
    private bool _inVBlank;

    public int Scanline       => _scanline;
    public int CycleInScanline=> _cycleInScanline;
    public bool InHBlank      => _inHBlank;
    public bool InVBlank      => _inVBlank;
    public long FrameCount    { get; private set; }

    public GbaScheduler(GbaMemoryBus bus)
    {
        _bus = bus;
        WriteVcount(0);
    }

    /// <summary>
    /// Advance the scheduler by <paramref name="cycles"/> CPU cycles.
    /// Fires VBlank/HBlank/VCount-match events as boundaries are crossed.
    /// Caller is the CPU loop after each Step() (or batched).
    /// </summary>
    public void Tick(int cycles)
    {
        if (cycles <= 0) return;
        int remaining = cycles;
        while (remaining > 0)
        {
            int spaceInScanline = CyclesPerScanline - _cycleInScanline;
            int step = Math.Min(remaining, spaceInScanline);
            _cycleInScanline += step;
            remaining -= step;

            // HBlank entry inside a visible scanline (cycle reaches CyclesHDraw).
            if (!_inHBlank
                && _scanline < VisibleScanlines
                && _cycleInScanline >= CyclesHDraw)
            {
                EnterHBlank();
            }

            // Scanline rollover.
            if (_cycleInScanline >= CyclesPerScanline)
            {
                _cycleInScanline = 0;
                AdvanceScanline();
            }
        }
    }

    private void EnterHBlank()
    {
        _inHBlank = true;
        // Set DISPSTAT.HBLANK_FLG. (Read-back via DISPSTAT word IO,
        // not the toggle stub for VBLANK.)
        SetDispstatBit(GbaMemoryMap.STAT_HBLANK_FLG, true);

        // HBlank IRQ if DISPSTAT.HBLANK_IE set.
        if ((ReadDispstat() & GbaMemoryMap.STAT_HBLANK_IE) != 0)
            _bus.RaiseInterrupt(GbaInterrupt.HBlank);

        _bus.Dma.TriggerOnHBlank();
    }

    private void AdvanceScanline()
    {
        // Leaving the previous scanline → clear HBlank flag.
        _inHBlank = false;
        SetDispstatBit(GbaMemoryMap.STAT_HBLANK_FLG, false);

        _scanline++;
        if (_scanline >= TotalScanlines)
        {
            _scanline = 0;
            FrameCount++;
            _inVBlank = false;
            SetDispstatBit(GbaMemoryMap.STAT_VBLANK_FLG, false);
        }

        WriteVcount((byte)_scanline);

        // VBlank entry on scanline 160.
        if (_scanline == VisibleScanlines)
        {
            _inVBlank = true;
            SetDispstatBit(GbaMemoryMap.STAT_VBLANK_FLG, true);

            if ((ReadDispstat() & GbaMemoryMap.STAT_VBLANK_IE) != 0)
                _bus.RaiseInterrupt(GbaInterrupt.VBlank);

            _bus.Dma.TriggerOnVBlank();
        }

        // VCount-match IRQ (DISPSTAT[15:8] holds the target line).
        var dispstat = ReadDispstat();
        var target   = (dispstat >> 8) & 0xFF;
        var matched  = _scanline == target;
        SetDispstatBit(GbaMemoryMap.STAT_VCOUNT_FLG, matched);
        if (matched && (dispstat & GbaMemoryMap.STAT_VCOUNT_IE) != 0)
            _bus.RaiseInterrupt(GbaInterrupt.VCount);
    }

    private void WriteVcount(byte v)
    {
        _bus.Io[(int)GbaMemoryMap.VCOUNT_Off] = v;
    }

    private ushort ReadDispstat()
    {
        return (ushort)(_bus.Io[(int)GbaMemoryMap.DISPSTAT_Off]
                      | (_bus.Io[(int)GbaMemoryMap.DISPSTAT_Off + 1] << 8));
    }

    private void SetDispstatBit(ushort bit, bool value)
    {
        var cur = ReadDispstat();
        var next = value ? (ushort)(cur | bit) : (ushort)(cur & ~bit);
        _bus.Io[(int)GbaMemoryMap.DISPSTAT_Off]     = (byte)(next & 0xFF);
        _bus.Io[(int)GbaMemoryMap.DISPSTAT_Off + 1] = (byte)((next >> 8) & 0xFF);
    }
}
