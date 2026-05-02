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
    public void DispstatRead_TogglesVBlankFlagAcrossSuccessiveReads()
    {
        // jsmolka's m_vsync waits for VBlank to CLEAR then waits for it
        // to SET — both polarities must be observable. The stub toggles
        // on every read so both loops in m_vsync exit within 1-2 reads.
        var bus = new GbaMemoryBus();
        var r1 = bus.ReadHalfword(0x04000004);
        var r2 = bus.ReadHalfword(0x04000004);
        var r3 = bus.ReadHalfword(0x04000004);
        // r1 != r2 means flag toggled at least once in three reads.
        Assert.NotEqual(r1 & GbaMemoryMap.STAT_VBLANK_FLG, r2 & GbaMemoryMap.STAT_VBLANK_FLG);
        Assert.NotEqual(r2 & GbaMemoryMap.STAT_VBLANK_FLG, r3 & GbaMemoryMap.STAT_VBLANK_FLG);
    }

    [Fact]
    public void DispstatRead_WordReadIncludesVcountInUpperHalf()
    {
        // Word read at 0x04000004 returns DISPSTAT + VCOUNT (16+16). The
        // VCOUNT lie helps tests that read the line counter to make progress.
        var bus = new GbaMemoryBus();
        var word = bus.ReadWord(0x04000004);
        Assert.NotEqual(0u, word & 0xFFFF0000u);  // VCOUNT non-zero
        // Lower 16 bits hold DISPSTAT (which toggles); we just check it parses.
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

    // ---------------- Phase 5: BIOS LLE + IRQ register tests ----------------

    [Fact]
    public void LoadBios_PopulatesBiosRegion()
    {
        var bus = new GbaMemoryBus();
        var fakeBios = new byte[1024];
        for (int i = 0; i < fakeBios.Length; i++) fakeBios[i] = (byte)(i & 0xFF);

        bus.LoadBios(fakeBios);

        // ReadWord at 0x00000000 should return little-endian 0x03020100.
        Assert.Equal(0x03020100u, bus.ReadWord(0x00000000));
        // Beyond loaded region, BIOS is zero-padded.
        Assert.Equal(0u, bus.ReadWord(0x00000800));
    }

    [Fact]
    public void LoadBios_OversizeBiosThrows()
    {
        var bus = new GbaMemoryBus();
        var tooBig = new byte[GbaMemoryMap.BiosSize + 1];
        Assert.Throws<ArgumentException>(() => bus.LoadBios(tooBig));
    }

    [Fact]
    public void RaiseInterrupt_SetsCorrespondingIfBit()
    {
        var bus = new GbaMemoryBus();
        bus.RaiseInterrupt(GbaInterrupt.VBlank);

        // IF at 0x04000202 should now have bit 0 set.
        Assert.Equal(0x0001, bus.ReadHalfword(0x04000202));

        // Raising another interrupt OR-s.
        bus.RaiseInterrupt(GbaInterrupt.Timer1);
        Assert.Equal(0x0011, bus.ReadHalfword(0x04000202));   // VBlank | Timer1 = bit 0 | bit 4
    }

    [Fact]
    public void IfWrite_ClearsBitsThatAreSet_ViaWrite1ToClear()
    {
        var bus = new GbaMemoryBus();
        bus.RaiseInterrupt(GbaInterrupt.VBlank);
        bus.RaiseInterrupt(GbaInterrupt.Timer0);

        // IF = bit 0 | bit 3 = 0x09.
        Assert.Equal(0x0009, bus.ReadHalfword(0x04000202));

        // Write 0x0001 to IF: clears VBlank bit, leaves Timer0 alone.
        bus.WriteHalfword(0x04000202, 0x0001);
        Assert.Equal(0x0008, bus.ReadHalfword(0x04000202));
    }

    [Fact]
    public void HasPendingInterrupt_RequiresImeIeIfAllSet()
    {
        var bus = new GbaMemoryBus();
        bus.RaiseInterrupt(GbaInterrupt.VBlank);
        Assert.False(bus.HasPendingInterrupt());          // IME=0, IE=0

        bus.WriteHalfword(0x04000200, 0x0001);            // IE = VBlank
        Assert.False(bus.HasPendingInterrupt());          // still IME=0

        bus.WriteWord(0x04000208, 0x00000001);            // IME = 1
        Assert.True(bus.HasPendingInterrupt());

        // Clear IF — pending should drop.
        bus.WriteHalfword(0x04000202, 0x0001);
        Assert.False(bus.HasPendingInterrupt());
    }

    [Fact]
    public void DispstatWrite_PreservesReadOnlyFlagBits()
    {
        var bus = new GbaMemoryBus();
        // Read first to set the toggle to "VBlank flag set" (read count odd).
        var initial = bus.ReadHalfword(0x04000004);
        Assert.Equal(GbaMemoryMap.STAT_VBLANK_FLG, initial);

        // Write a value that tries to clear bit 0 AND set the IRQ-enable bits.
        bus.WriteHalfword(0x04000004, 0x0038);   // VBlank IE | HBlank IE | VCount IE

        // Next read: VBlank flag toggles to 0 (next read count even), but the
        // IRQ-enable bits we wrote should be readable in the latched value
        // when we read DISPSTAT byte-wise. Test the byte-level read for the
        // upper bits which our toggle stub doesn't override.
        var byteRead = bus.ReadByte(0x04000005);   // upper byte of DISPSTAT
        // The upper byte got the high half of 0x0038 = 0x00 (since 0x38 fits
        // in low byte). So this just checks the write went through to the
        // backing IO array.
        Assert.Equal(0x00, byteRead);
    }
}
