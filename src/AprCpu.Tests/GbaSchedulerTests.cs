using AprCpu.Core.Runtime.Gba;
using Xunit;

namespace AprCpu.Tests;

/// <summary>
/// Phase 5: GbaScheduler ticks scanlines + fires VBlank/HBlank/VCount
/// IRQs at the right cycle boundaries; updates VCOUNT register.
/// </summary>
public class GbaSchedulerTests
{
    [Fact]
    public void Tick_AdvancesCycleAndScanlineCounters()
    {
        var bus = new GbaMemoryBus();
        var s = new GbaScheduler(bus);

        // 1232 cycles = exactly one scanline.
        s.Tick(GbaScheduler.CyclesPerScanline);
        Assert.Equal(1, s.Scanline);
        Assert.Equal(0, s.CycleInScanline);
        Assert.Equal(1, bus.Io[(int)GbaMemoryMap.VCOUNT_Off]);
    }

    [Fact]
    public void Tick_VBlankFiresAtScanline160()
    {
        var bus = new GbaMemoryBus();
        // Enable VBlank IRQ in DISPSTAT.
        bus.WriteHalfword(0x04000004, GbaMemoryMap.STAT_VBLANK_IE);

        var s = new GbaScheduler(bus);
        s.Tick(GbaScheduler.VisibleScanlines * GbaScheduler.CyclesPerScanline);

        Assert.Equal(GbaScheduler.VisibleScanlines, s.Scanline);
        Assert.True(s.InVBlank);

        // IF should have VBlank bit set.
        Assert.Equal(0x0001, bus.ReadHalfword(0x04000202) & 0x0001);

        // DISPSTAT VBlank flag should be set.
        Assert.NotEqual(0, bus.Io[(int)GbaMemoryMap.DISPSTAT_Off] & GbaMemoryMap.STAT_VBLANK_FLG);
    }

    [Fact]
    public void Tick_HBlankFiresInVisibleScanlineWhenEnabled()
    {
        var bus = new GbaMemoryBus();
        bus.WriteHalfword(0x04000004, GbaMemoryMap.STAT_HBLANK_IE);
        var s = new GbaScheduler(bus);

        // Tick past HDraw → enter HBlank.
        s.Tick(GbaScheduler.CyclesHDraw + 1);
        Assert.True(s.InHBlank);
        Assert.Equal(0x0002, bus.ReadHalfword(0x04000202) & 0x0002);   // IF.HBlank bit
    }

    [Fact]
    public void Tick_OneFrame_WrapsAndIncrementsFrameCount()
    {
        var bus = new GbaMemoryBus();
        var s = new GbaScheduler(bus);
        Assert.Equal(0, s.FrameCount);

        s.Tick(GbaScheduler.CyclesPerFrame);

        Assert.Equal(1, s.FrameCount);
        Assert.Equal(0, s.Scanline);
        Assert.False(s.InVBlank);   // VBlank cleared on wrap to scanline 0
    }

    [Fact]
    public void Tick_VCountMatch_FiresIrqOnTargetLine()
    {
        var bus = new GbaMemoryBus();
        // DISPSTAT: target line 5 (in upper byte) + VCOUNT_IE (bit 5).
        ushort dispstat = (ushort)(GbaMemoryMap.STAT_VCOUNT_IE | (5 << 8));
        bus.WriteHalfword(0x04000004, dispstat);

        var s = new GbaScheduler(bus);
        s.Tick(5 * GbaScheduler.CyclesPerScanline);

        // IF.VCount bit should be set.
        Assert.Equal(0x0004, bus.ReadHalfword(0x04000202) & 0x0004);
    }

    [Fact]
    public void Tick_VBlankWithNoIeEnabled_DoesNotRaiseIrq()
    {
        var bus = new GbaMemoryBus();
        // DISPSTAT IRQ-enable bits all clear.
        var s = new GbaScheduler(bus);
        s.Tick(GbaScheduler.VisibleScanlines * GbaScheduler.CyclesPerScanline);

        // VBlank flag still set, but IF.VBlank should NOT be raised.
        Assert.Equal(0, bus.ReadHalfword(0x04000202) & 0x0001);
    }
}
