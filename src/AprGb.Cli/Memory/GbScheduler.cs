namespace AprGb.Cli.Memory;

/// <summary>
/// DMG PPU clock scheduler. Mirrors <c>GbaScheduler</c>'s shape but for
/// the simpler Game Boy timing model:
///
/// <list type="bullet">
///   <item>One scanline = 456 t-cycles (4 t-cycles per dot × 114 dots)</item>
///   <item>One frame = 154 scanlines × 456 = 70 224 t-cycles
///         (144 visible + 10 VBlank lines)</item>
///   <item>Per-scanline modes (visible only):
///         <para>Mode 2 (OAM scan, 80 cyc) → Mode 3 (drawing, ~172 cyc) → Mode 0 (HBlank, ~204 cyc)</para>
///         <para>VBlank scanlines (144..153) are Mode 1 throughout.</para></item>
///   <item>Updates <c>LY</c> (0xFF44), <c>STAT</c> mode bits (0xFF41 lo
///         2 bits) + LY=LYC coincidence flag (STAT bit 2) every cycle.</item>
///   <item>Raises VBlank IRQ (IF bit 0) when crossing into scanline 144;
///         raises STAT IRQ (IF bit 1) on enabled HBlank/VBlank/OAM/LYC events.</item>
/// </list>
///
/// Without this scheduler, <see cref="GbMemoryBus.ReadIo"/> used to hand
/// out a hardcoded <c>LY = 0x90</c> (fake permanent VBlank) so polling
/// loops wouldn't deadlock — but that made the DMG boot ROM finish its
/// 2.5-second logo-scroll animation in ≈ 0 cycles, since every
/// "wait for VBlank" loop iteration completed instantly. With real LY
/// ticking, the animation runs at correct timing.
/// </summary>
public sealed class GbScheduler
{
    private readonly GbMemoryBus _bus;

    public const int TCyclesPerScanline = 456;
    public const int Mode2OamCycles     = 80;
    public const int Mode3DrawCycles    = 172;
    public const int Mode0HBlankCycles  = 204;        // 80 + 172 + 204 = 456 ✓
    public const int VisibleScanlines   = 144;
    public const int VBlankScanlines    = 10;
    public const int TotalScanlines     = 154;
    public const int TCyclesPerFrame    = TCyclesPerScanline * TotalScanlines;     // 70224

    private int _cycleInScanline;
    private int _scanline;
    private int _mode;             // 0 HBlank, 1 VBlank, 2 OAM, 3 Drawing

    public int  Scanline        => _scanline;
    public int  CycleInScanline => _cycleInScanline;
    public int  Mode            => _mode;
    public long FrameCount      { get; private set; }

    public GbScheduler(GbMemoryBus bus)
    {
        _bus = bus;
        WriteLy(0);
        SetMode(2);       // boot starts at scanline 0 / OAM scan
        UpdateLycCoincidence();
    }

    /// <summary>Advance the scheduler by <paramref name="tCycles"/> t-cycles.</summary>
    public void Tick(int tCycles)
    {
        if (tCycles <= 0) return;
        int remaining = tCycles;
        while (remaining > 0)
        {
            int space = TCyclesPerScanline - _cycleInScanline;
            int step  = System.Math.Min(remaining, space);
            _cycleInScanline += step;
            remaining -= step;

            // Update PPU mode within a visible scanline as we cross OAM →
            // Drawing → HBlank boundaries. VBlank lines stay in mode 1
            // for their entire 456-cycle duration.
            if (_scanline < VisibleScanlines)
            {
                int newMode;
                if (_cycleInScanline < Mode2OamCycles)              newMode = 2;
                else if (_cycleInScanline < Mode2OamCycles + Mode3DrawCycles) newMode = 3;
                else                                                 newMode = 0;
                if (newMode != _mode)
                {
                    SetMode(newMode);
                    var stat = ReadStat();
                    if      (newMode == 0 && (stat & 0x08) != 0) RaiseStat();   // HBlank STAT IRQ
                    else if (newMode == 2 && (stat & 0x20) != 0) RaiseStat();   // OAM STAT IRQ
                }
            }

            // Scanline rollover.
            if (_cycleInScanline >= TCyclesPerScanline)
            {
                _cycleInScanline = 0;
                AdvanceScanline();
            }
        }
    }

    private void AdvanceScanline()
    {
        _scanline++;
        if (_scanline >= TotalScanlines)
        {
            _scanline = 0;
            FrameCount++;
        }
        WriteLy((byte)_scanline);

        if (_scanline == VisibleScanlines)
        {
            // VBlank entry — always raise the dedicated VBlank IRQ (IF bit 0),
            // plus the STAT VBlank IRQ if its enable bit is set.
            SetMode(1);
            _bus.InterruptFlag |= 0x01;                             // IF.VBlank
            if ((ReadStat() & 0x10) != 0) RaiseStat();             // STAT VBlank IRQ enable
        }
        else if (_scanline == 0)
        {
            // Wrap back to first visible scanline → re-enter OAM mode.
            SetMode(2);
            if ((ReadStat() & 0x20) != 0) RaiseStat();             // STAT OAM IRQ enable
        }
        else if (_scanline < VisibleScanlines)
        {
            // Each new visible scanline starts in OAM mode.
            SetMode(2);
            if ((ReadStat() & 0x20) != 0) RaiseStat();
        }

        UpdateLycCoincidence();
    }

    private void UpdateLycCoincidence()
    {
        var stat = ReadStat();
        var lyc  = _bus.Io[0x45];
        bool match = _scanline == lyc;
        byte newStat = match ? (byte)(stat | 0x04) : (byte)(stat & ~0x04);
        WriteStat(newStat);
        if (match && (stat & 0x40) != 0) RaiseStat();              // LYC=LY STAT IRQ enable
    }

    private void SetMode(int mode)
    {
        _mode = mode;
        var stat = ReadStat();
        byte newStat = (byte)((stat & ~0x03) | (mode & 0x03));
        WriteStat(newStat);
    }

    private byte ReadStat()                  => _bus.Io[0x41];
    private void WriteStat(byte v)           => _bus.Io[0x41] = v;
    private void WriteLy(byte v)             => _bus.Io[0x44] = v;
    private void RaiseStat()                 => _bus.InterruptFlag |= 0x02;     // IF.LCD-STAT
}
