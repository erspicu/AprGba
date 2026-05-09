// N10 — allowed_widths debug-mode enforcement on GbaMemoryBus.
//
// Verifies that turning on EnforceAllowedWidths makes the bus reject
// accesses whose width isn't in the spec's `allowed_widths` for that
// region. Default OFF preserves all current behaviour.
//
// gba.json (N4.5) declares:
//   bios:     [8, 16, 32]
//   ewram:    [8, 16, 32]
//   iwram:    [8, 16, 32]
//   io:       [16, 32]      ← 8-bit access disallowed
//   palette:  [16, 32]      ← 8-bit access disallowed
//   vram:     [16, 32]      ← 8-bit access disallowed
//   oam:      [16, 32]      ← 8-bit access disallowed
//   cart_rom: [8, 16, 32]

using AprCpu.Core.Runtime.Gba;
using Xunit;

namespace AprCpu.Tests;

public class GbaMemoryBusN10Tests
{
    [Fact]
    public void EnforceWidths_OffByDefault_8BitWritesToVramSucceed()
    {
        var bus = new GbaMemoryBus();
        // No exception — default behaviour preserved.
        bus.WriteByte(0x06000000, 0x42);
    }

    [Fact]
    public void EnforceWidths_On_8BitVramWriteThrows()
    {
        var bus = new GbaMemoryBus { EnforceAllowedWidths = true };
        var ex = Assert.Throws<InvalidOperationException>(
            () => bus.WriteByte(0x06000000, 0x42));
        Assert.Contains("8-bit", ex.Message);
        Assert.Contains("0x06000000", ex.Message);
    }

    [Fact]
    public void EnforceWidths_On_HalfwordVramWriteSucceeds()
    {
        var bus = new GbaMemoryBus { EnforceAllowedWidths = true };
        bus.WriteHalfword(0x06000000, 0xABCD);   // [16, 32] allowed
        Assert.Equal((ushort)0xABCD, bus.ReadHalfword(0x06000000));
    }

    [Fact]
    public void EnforceWidths_On_8BitPaletteWriteThrows()
    {
        var bus = new GbaMemoryBus { EnforceAllowedWidths = true };
        Assert.Throws<InvalidOperationException>(
            () => bus.WriteByte(0x05000000, 0x10));
    }

    [Fact]
    public void EnforceWidths_On_8BitOamReadThrows()
    {
        var bus = new GbaMemoryBus { EnforceAllowedWidths = true };
        Assert.Throws<InvalidOperationException>(
            () => bus.ReadByte(0x07000000));
    }

    [Fact]
    public void EnforceWidths_On_8BitIoReadThrows()
    {
        var bus = new GbaMemoryBus { EnforceAllowedWidths = true };
        Assert.Throws<InvalidOperationException>(
            () => bus.ReadByte(0x04000000));
    }

    [Fact]
    public void EnforceWidths_On_EwramAcceptsAllWidths()
    {
        var bus = new GbaMemoryBus { EnforceAllowedWidths = true };
        // EWRAM allows [8, 16, 32] — none should throw.
        bus.WriteByte    (0x02000000, 0x11);
        bus.WriteHalfword(0x02000004, 0x2233);
        bus.WriteWord    (0x02000008, 0x44556677);
        Assert.Equal((byte)0x11,        bus.ReadByte    (0x02000000));
        Assert.Equal((ushort)0x2233,    bus.ReadHalfword(0x02000004));
        Assert.Equal(0x44556677u,       bus.ReadWord    (0x02000008));
    }

    [Fact]
    public void EnforceWidths_On_CartRomAcceptsAllWidths()
    {
        var bus = new GbaMemoryBus { EnforceAllowedWidths = true };
        bus.LoadRom(new byte[] { 0x11, 0x22, 0x33, 0x44 });
        bus.ReadByte    (0x08000000);
        bus.ReadHalfword(0x08000000);
        bus.ReadWord    (0x08000000);
    }
}
