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
    public void DispstatRead_ReturnsBackingArrayBytes()
    {
        // Phase 5: the old "toggle VBLANK_FLG on every read" hack was
        // removed — GbaScheduler now maintains the real flag bits as the
        // emulated frame advances. A bare bus (no scheduler) just returns
        // whatever's in the backing array (zeros at construction).
        var bus = new GbaMemoryBus();
        Assert.Equal((ushort)0, bus.ReadHalfword(0x04000004));
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

    // ---------------- Phase 5: DMA channel tests ----------------

    [Fact]
    public void Dma_ImmediateMode_CopiesHalfwordsFromEwramToVram()
    {
        var bus = new GbaMemoryBus();
        // Source: 8 halfwords in EWRAM @ 0x02000000 = 0x0001..0x0008
        for (int i = 0; i < 8; i++)
            bus.WriteHalfword((uint)(0x02000000 + i * 2), (ushort)(i + 1));

        // Configure DMA0: SAD=0x02000000, DAD=0x06000000 (VRAM),
        // CNT_L=8 halfwords, CNT_H=enable | timing=immediate | width=16-bit |
        //                          src=inc | dst=inc.
        bus.WriteWord(0x040000B0, 0x02000000);   // SAD
        bus.WriteWord(0x040000B4, 0x06000000);   // DAD
        bus.WriteHalfword(0x040000B8, 8);         // CNT_L
        bus.WriteHalfword(0x040000BA, GbaMemoryMap.DMA_ENABLE);  // immediate, 16-bit, src+dst inc

        // Verify VRAM now holds the 8 halfwords.
        for (int i = 0; i < 8; i++)
            Assert.Equal((ushort)(i + 1), bus.ReadHalfword((uint)(0x06000000 + i * 2)));

        // Channel disabled itself after transfer.
        Assert.Equal(0, bus.ReadHalfword(0x040000BA) & GbaMemoryMap.DMA_ENABLE);
    }

    [Fact]
    public void Dma_32BitTransfer_CopiesWords()
    {
        var bus = new GbaMemoryBus();
        bus.WriteWord(0x02000000, 0xCAFEF00D);
        bus.WriteWord(0x02000004, 0xDEADBEEF);

        bus.WriteWord(0x040000B0, 0x02000000);
        bus.WriteWord(0x040000B4, 0x06000000);
        bus.WriteHalfword(0x040000B8, 2);
        bus.WriteHalfword(0x040000BA,
            (ushort)(GbaMemoryMap.DMA_ENABLE | GbaMemoryMap.DMA_WIDTH_32));

        Assert.Equal(0xCAFEF00Du, bus.ReadWord(0x06000000));
        Assert.Equal(0xDEADBEEFu, bus.ReadWord(0x06000004));
    }

    [Fact]
    public void Dma_FixedSource_RepeatsTheSameValue()
    {
        var bus = new GbaMemoryBus();
        bus.WriteHalfword(0x02000000, 0xABCD);

        bus.WriteWord(0x040000B0, 0x02000000);
        bus.WriteWord(0x040000B4, 0x06000000);
        bus.WriteHalfword(0x040000B8, 4);
        bus.WriteHalfword(0x040000BA,
            (ushort)(GbaMemoryMap.DMA_ENABLE | GbaMemoryMap.DMA_SRC_FIXED));

        // All 4 destinations should hold the same value (source didn't advance).
        for (int i = 0; i < 4; i++)
            Assert.Equal((ushort)0xABCD, bus.ReadHalfword((uint)(0x06000000 + i * 2)));
    }

    [Fact]
    public void Dma_IrqOnEnd_RaisesDma0Flag()
    {
        var bus = new GbaMemoryBus();
        bus.WriteWord(0x040000B0, 0x02000000);
        bus.WriteWord(0x040000B4, 0x06000000);
        bus.WriteHalfword(0x040000B8, 1);
        bus.WriteHalfword(0x040000BA,
            (ushort)(GbaMemoryMap.DMA_ENABLE | GbaMemoryMap.DMA_IRQ_ON_END));

        // IF bit 8 (Dma0) should be set.
        Assert.Equal(0x0100, bus.ReadHalfword(0x04000202));
    }

    [Fact]
    public void Dma_VBlankTiming_DoesNotFireImmediately()
    {
        var bus = new GbaMemoryBus();
        bus.WriteHalfword(0x02000000, 0x1234);

        bus.WriteWord(0x040000B0, 0x02000000);
        bus.WriteWord(0x040000B4, 0x06000000);
        bus.WriteHalfword(0x040000B8, 1);
        bus.WriteHalfword(0x040000BA,
            (ushort)(GbaMemoryMap.DMA_ENABLE | GbaMemoryMap.DMA_TIMING_VBLANK));

        // Nothing copied yet — VRAM still 0.
        Assert.Equal((ushort)0, bus.ReadHalfword(0x06000000));

        // Trigger VBlank: DMA fires.
        bus.Dma.TriggerOnVBlank();
        Assert.Equal((ushort)0x1234, bus.ReadHalfword(0x06000000));
    }

    [Fact]
    public void DispstatWrite_PreservesReadOnlyFlagBits()
    {
        var bus = new GbaMemoryBus();

        // Pre-set bit 0 (VBlank flag) directly in the IO backing array
        // so we can verify "read-only" semantics — only scheduler / hardware
        // should be able to set this bit, but a write should NOT clear it.
        bus.Io[(int)GbaMemoryMap.DISPSTAT_Off] = (byte)GbaMemoryMap.STAT_VBLANK_FLG;

        // Write a value that tries to clear bit 0 AND set the IRQ-enable bits.
        bus.WriteHalfword(0x04000004, 0x0038);   // VBlank IE | HBlank IE | VCount IE

        // Read back: bit 0 still set (read-only via WriteIoHalfword's mask),
        // bits 3..5 (IRQ enables) now set from our write.
        var post = bus.ReadHalfword(0x04000004);
        Assert.Equal(GbaMemoryMap.STAT_VBLANK_FLG, (ushort)(post & GbaMemoryMap.STAT_VBLANK_FLG));
        Assert.Equal((ushort)0x0038, (ushort)(post & 0x0038));
    }
}
