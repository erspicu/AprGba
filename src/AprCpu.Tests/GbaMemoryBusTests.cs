using AprCpu.Core.Runtime.Gba;
using Xunit;

namespace AprCpu.Tests;

/// <summary>
/// Phase 4.1 — verify GbaMemoryBus dispatches to the right region,
/// the IO stub answers DISPSTAT correctly so jsmolka's m_vsync can
/// progress, and ROM loading works end-to-end with a real .gba file.
/// </summary>
public class GbaMemoryBusTests
{
    [Fact]
    public void EwramReadWrite_RoundTrips()
    {
        var bus = new GbaMemoryBus();
        bus.WriteWord(0x02001234, 0xCAFEF00D);
        Assert.Equal(0xCAFEF00Du, bus.ReadWord(0x02001234));
    }

    [Fact]
    public void IwramReadWrite_RoundTrips()
    {
        var bus = new GbaMemoryBus();
        bus.WriteWord(0x03001000, 0xDEADBEEF);
        Assert.Equal(0xDEADBEEFu, bus.ReadWord(0x03001000));
    }

    [Fact]
    public void DispstatRead_AlwaysReportsVBlankFlag()
    {
        // jsmolka's m_vsync polls (DISPSTAT & VBLANK_FLG); if we never
        // report it, the test ROM deadlocks. The stub must always set bit 0.
        var bus = new GbaMemoryBus();
        var halfword = bus.ReadHalfword(0x04000004);
        Assert.Equal(GbaMemoryMap.STAT_VBLANK_FLG, (ushort)(halfword & GbaMemoryMap.STAT_VBLANK_FLG));
    }

    [Fact]
    public void DispstatRead_WordReadIncludesVcountInUpperHalf()
    {
        // Word read at 0x04000004 returns DISPSTAT + VCOUNT (16+16). The
        // VCOUNT lie helps tests that read the line counter to make progress.
        var bus = new GbaMemoryBus();
        var word = bus.ReadWord(0x04000004);
        Assert.Equal(GbaMemoryMap.STAT_VBLANK_FLG, (ushort)(word & 0xFFFFu));
        Assert.NotEqual(0u, word & 0xFFFF0000u);  // VCOUNT non-zero
    }

    [Fact]
    public void IoWrite_AbsorbsArbitraryRegisterWrites()
    {
        // Any IO write must not throw — most tests blast DISPCNT/IRQ regs
        // during init even though we don't model PPU/interrupts.
        var bus = new GbaMemoryBus();
        bus.WriteHalfword(0x04000000, 0x0403);  // DISPCNT
        bus.WriteHalfword(0x04000200, 0x0001);  // IE
        bus.WriteHalfword(0x04000208, 0x0001);  // IME
        // Read-back should return what was written (except for special-cased DISPSTAT)
        Assert.Equal((ushort)0x0403, bus.ReadHalfword(0x04000000));
    }

    [Fact]
    public void RomMirror_AliasedAcrossWaitStateRegions()
    {
        // 0x08/0x0A/0x0C are wait-state aliases of the same ROM.
        var bus = new GbaMemoryBus();
        bus.LoadRom(new byte[] { 0xEF, 0xBE, 0xAD, 0xDE });  // little-endian 0xDEADBEEF at offset 0
        Assert.Equal(0xDEADBEEFu, bus.ReadWord(0x08000000));
        Assert.Equal(0xDEADBEEFu, bus.ReadWord(0x0A000000));
        Assert.Equal(0xDEADBEEFu, bus.ReadWord(0x0C000000));
    }

    [Fact]
    public void LoadRom_RealJsmolkaArmGba_FirstWordIsBranchInstruction()
    {
        // GBA cartridge header: byte 0 of every game is the first instruction
        // of the entry point — typically a `B reset_handler` (cond=AL, opcode=B).
        // We don't disassemble it here; just sanity-check the file loaded
        // and the top nibble of the word is 0xE (cond=AL).
        var romPath = Path.Combine(TestPaths.TestRomsRoot, "gba-tests", "arm", "arm.gba");
        if (!File.Exists(romPath))
            throw new Xunit.Sdk.XunitException($"Test ROM missing: {romPath}");

        var bytes = File.ReadAllBytes(romPath);
        var bus = new GbaMemoryBus();
        bus.LoadRom(bytes);

        var firstInstr = bus.ReadWord(0x08000000);
        Assert.Equal(0xEu, firstInstr >> 28);  // cond = AL
        // Bits 27:25 should be 101 (Branch group). Loose check — header may vary.
        var topGroup = (firstInstr >> 25) & 0b111;
        Assert.Equal(0b101u, topGroup);
    }
}
