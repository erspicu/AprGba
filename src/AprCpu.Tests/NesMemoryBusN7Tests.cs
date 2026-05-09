// N7 query API tests for NesMemoryBus.
//
// Verifies that spec-declared values (allowed_widths, wait_states, host-
// pointer-eligible regions) flow correctly from spec/machines/nes-ntsc.json
// through to runtime query results. These query APIs aren't enforced on
// the hot dispatch path — they're consumable hooks for the block-JIT
// fastmem path, debug width-checking modes, and the upcoming GBA bus.

using AprNes.Cli.Memory;
using Xunit;

namespace AprCpu.Tests;

public class NesMemoryBusN7Tests
{
    [Fact]
    public void TryGetHostPointer_Wram_ReturnsArrayAndCorrectOffset()
    {
        var bus = new NesMemoryBus();
        // Mid-WRAM mirrored slot — addr $0801 should resolve to _wram[0x001].
        Assert.True(bus.TryGetHostPointer(0x0801, out var arr, out var off));
        Assert.NotNull(arr);
        Assert.Same(bus.Wram, arr);
        Assert.Equal(0x001, off);
    }

    [Fact]
    public void TryGetHostPointer_Wram_AppliesMirror()
    {
        var bus = new NesMemoryBus();
        // $1FFF is the topmost WRAM mirror; mirror_mask 0x07FF brings
        // it down to offset 0x7FF.
        Assert.True(bus.TryGetHostPointer(0x1FFF, out _, out var off));
        Assert.Equal(0x7FF, off);
    }

    [Fact]
    public void TryGetHostPointer_PpuApuMapper_ReturnsFalse()
    {
        var bus = new NesMemoryBus();
        // PPU registers ($2000-$3FFF) — IO, side effects. Not host-pointer eligible.
        Assert.False(bus.TryGetHostPointer(0x2000, out _, out _));
        Assert.False(bus.TryGetHostPointer(0x3FFF, out _, out _));
        // APU/IO ($4000-$401F) — same reason.
        Assert.False(bus.TryGetHostPointer(0x4014, out _, out _));
        // Mapper-controlled cart_prg ($4020-$FFFF) — mapper bank-switches.
        Assert.False(bus.TryGetHostPointer(0x8000, out _, out _));
        Assert.False(bus.TryGetHostPointer(0xFFFC, out _, out _));
    }

    [Fact]
    public void IsAccessWidthAllowed_Wram_8bitOnly()
    {
        var bus = new NesMemoryBus();
        // NES spec declares "allowed_widths": [8] for every region.
        Assert.True (bus.IsAccessWidthAllowed(0x0042, widthBits: 8));
        Assert.False(bus.IsAccessWidthAllowed(0x0042, widthBits: 16));
        Assert.False(bus.IsAccessWidthAllowed(0x0042, widthBits: 32));
    }

    [Fact]
    public void IsAccessWidthAllowed_PpuApuCart_AllRespect8BitOnly()
    {
        var bus = new NesMemoryBus();
        Assert.True (bus.IsAccessWidthAllowed(0x2000, 8));
        Assert.False(bus.IsAccessWidthAllowed(0x2000, 16));
        Assert.True (bus.IsAccessWidthAllowed(0x4014, 8));
        Assert.False(bus.IsAccessWidthAllowed(0x4014, 32));
        Assert.True (bus.IsAccessWidthAllowed(0x8000, 8));
        Assert.False(bus.IsAccessWidthAllowed(0x8000, 16));
    }

    [Fact]
    public void GetWaitStates_Nes_ReturnsZeroPair()
    {
        var bus = new NesMemoryBus();
        var (seq, nonseq) = bus.GetWaitStates(0x8000);
        Assert.Equal(0, seq);
        Assert.Equal(0, nonseq);
    }
}
