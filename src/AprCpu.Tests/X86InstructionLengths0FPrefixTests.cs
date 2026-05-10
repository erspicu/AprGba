using AprCpu.Core.Runtime;
using Xunit;

namespace AprCpu.Tests;

/// <summary>
/// Sprint 26.2 — 0x0F escape-prefix awareness in the X86 length oracle.
/// Verifies BlockDetector doesn't truncate or mis-walk the instruction
/// stream when an 80286 0F-prefixed instruction shows up.
///
/// Decoder dispatch (which set of formats to match against) is sprint
/// 26.3 work; this test only covers length detection.
/// </summary>
public class X86InstructionLengths0FPrefixTests
{
    private static FlatMemoryBus BusFromBytes(params byte[] data)
    {
        var bus = new FlatMemoryBus(0x10000);
        for (int i = 0; i < data.Length; i++) bus.WriteByte((uint)i, data[i]);
        return bus;
    }

    [Fact]
    public void Clts_0F_06_Has_Length_2()
    {
        // 0F 06 CLTS — no operand.
        var bus = BusFromBytes(0x0F, 0x06);
        Assert.Equal(2, X86_16InstructionLengths.GetLength(bus, 0));
    }

    [Fact]
    public void Lmsw_Reg_0F_01_F0_Has_Length_3()
    {
        // 0F 01 F0 — LMSW AX (ModR/M 11 110 000 = mod=11 reg-reg with reg=AX,
        // sub-op 110=LMSW). mod=11 → no displacement bytes. Total 3.
        var bus = BusFromBytes(0x0F, 0x01, 0xF0);
        Assert.Equal(3, X86_16InstructionLengths.GetLength(bus, 0));
    }

    [Fact]
    public void Lmsw_Mem_Disp16_0F_01_36_XX_XX_Has_Length_5()
    {
        // 0F 01 36 XX XX — LMSW [disp16] (mod=00 r/m=110 = direct disp16).
        // Total: 0F + 01 + 36 + 2 disp = 5.
        var bus = BusFromBytes(0x0F, 0x01, 0x36, 0x12, 0x34);
        Assert.Equal(5, X86_16InstructionLengths.GetLength(bus, 0));
    }

    [Fact]
    public void Lgdt_Mod_01_Disp8_Has_Length_4()
    {
        // 0F 01 50 XX — LGDT [BX+SI+disp8] (mod=01 reg=010 r/m=000).
        // Total: 0F + 01 + 50 + 1 disp = 4.
        var bus = BusFromBytes(0x0F, 0x01, 0x50, 0x42);
        Assert.Equal(4, X86_16InstructionLengths.GetLength(bus, 0));
    }

    [Fact]
    public void Lgdt_Mod_10_Disp16_Has_Length_5()
    {
        // 0F 01 90 XX XX — LGDT [BX+SI+disp16] (mod=10 reg=010 r/m=000).
        // Total: 5.
        var bus = BusFromBytes(0x0F, 0x01, 0x90, 0x12, 0x34);
        Assert.Equal(5, X86_16InstructionLengths.GetLength(bus, 0));
    }

    [Fact]
    public void Lar_0F_02_Has_Length_3_With_RegRegModRm()
    {
        // 0F 02 C0 — LAR AX, AX (mod=11 reg=000 r/m=000).
        var bus = BusFromBytes(0x0F, 0x02, 0xC0);
        Assert.Equal(3, X86_16InstructionLengths.GetLength(bus, 0));
    }

    [Fact]
    public void Lsl_0F_03_Has_Length_3_With_RegRegModRm()
    {
        var bus = BusFromBytes(0x0F, 0x03, 0xC0);
        Assert.Equal(3, X86_16InstructionLengths.GetLength(bus, 0));
    }

    [Fact]
    public void Unknown_0F_XX_Returns_Length_2_Defensive()
    {
        // 0F 99 — undefined. Length oracle returns 2 (defensive); decoder
        // will fail to match and BlockDetector ends the block.
        var bus = BusFromBytes(0x0F, 0x99);
        Assert.Equal(2, X86_16InstructionLengths.GetLength(bus, 0));
    }

    [Fact]
    public void Plain_0F_Standalone_Returns_3_When_End_Of_Bus()
    {
        // 0F at end of memory (no second byte readable) — bus returns 0
        // for the absent second byte, which IS a valid 0F-prefix opcode
        // (0F 00 = LLDT/SLDT/LTR/STR/VERR/VERW group). The third byte
        // (also 0) is interpreted as ModR/M with mod=00 r/m=000 = no
        // displacement. Total length = 3. This is the right behavior for
        // a real 0F 00 00 byte sequence; "defensive 2" only kicks in for
        // unknown 0F XX where XX is NOT in the 0/1/2/3 group.
        var bus = BusFromBytes(0x0F);
        Assert.Equal(3, X86_16InstructionLengths.GetLength(bus, 0));
    }
}
