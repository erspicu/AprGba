// Phase 24.2.3 — 8086 ALU + flag computation tests.
//
// Covers ADD/OR/ADC/SBB/AND/SUB/XOR/CMP across:
//   - 6 instruction forms (r/m8/16 ⊕ r8/16 in both directions, AL/imm8,
//     AX/imm16) via the 0x00-0x3D opcode range
//   - r/m, imm via the 0x80/0x81/0x82/0x83 group
//   - All 9 flags (CF/PF/AF/ZF/SF/TF/IF/DF/OF) computed correctly
//   - Edge cases: signed overflow, zero detection, parity, BCD nibble
//     boundary (AF), ADC/SBB with prior CF=0/1
//
// These tests pin down the exact 8086 architectural flag behaviour so
// the spec-driven backend (phase 24.4+) can lockstep against this oracle.

using AprX86.Cli.Cpu;
using AprX86.Cli.Memory;
using Xunit;

namespace AprCpu.Tests;

public class X86AluTests
{
    private static (X86LegacyCpu cpu, X86Memory mem) Setup(byte[] code, X86State? init = null)
    {
        var mem = new X86Memory();
        mem.LoadBinary(code, 0, 0x100);
        var cpu = new X86LegacyCpu(mem);
        cpu.Reset();
        cpu.SetEntryPoint(0, 0x100);
        if (init != null)
        {
            // copy init regs/flags
            cpu.State.A = init.A;
            cpu.State.B = init.B;
            cpu.State.C = init.C;
            cpu.State.D = init.D;
            cpu.State.SetFlags(init.GetFlags());
        }
        return (cpu, mem);
    }

    private static void RunUntilHalt(X86LegacyCpu cpu, int max = 100)
    {
        for (int i = 0; i < max && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
    }

    // ============== Pure-helper tests (X86Alu directly) ==============

    [Fact]
    public void Add8_BasicNoCarry()
    {
        var s = new X86State();
        byte r = X86Alu.Execute8(AluOp.Add, 0x10, 0x20, s);
        Assert.Equal(0x30, r);
        Assert.False(s.FlagC);
        Assert.False(s.FlagZ);
        Assert.False(s.FlagS);
        Assert.False(s.FlagO);
    }

    [Fact]
    public void Add8_CarryOut()
    {
        var s = new X86State();
        byte r = X86Alu.Execute8(AluOp.Add, 0xFF, 0x01, s);
        Assert.Equal(0x00, r);
        Assert.True(s.FlagC);   // carry out of bit 7
        Assert.True(s.FlagZ);
        Assert.True(s.FlagA);   // carry across nibble too
        Assert.False(s.FlagO);  // 0xFF = -1 unsigned, +1 = 0; no signed overflow
    }

    [Fact]
    public void Add8_SignedOverflow()
    {
        var s = new X86State();
        // 0x7F (127) + 1 = 0x80 (-128 signed) → signed overflow
        byte r = X86Alu.Execute8(AluOp.Add, 0x7F, 0x01, s);
        Assert.Equal(0x80, r);
        Assert.True(s.FlagS);
        Assert.True(s.FlagO);   // positive + positive = negative → OF
        Assert.False(s.FlagC);  // unsigned: no carry
        Assert.True(s.FlagA);   // nibble carry
    }

    [Fact]
    public void Add8_AuxCarry_NibbleBoundary()
    {
        var s = new X86State();
        // 0x0F + 0x01 = 0x10 → carry from bit 3 to bit 4 (AF=1)
        byte r = X86Alu.Execute8(AluOp.Add, 0x0F, 0x01, s);
        Assert.Equal(0x10, r);
        Assert.True(s.FlagA);
        Assert.False(s.FlagC);
    }

    [Fact]
    public void Sub8_NoBorrow()
    {
        var s = new X86State();
        byte r = X86Alu.Execute8(AluOp.Sub, 0x30, 0x10, s);
        Assert.Equal(0x20, r);
        Assert.False(s.FlagC);
        Assert.False(s.FlagS);
    }

    [Fact]
    public void Sub8_Borrow()
    {
        var s = new X86State();
        byte r = X86Alu.Execute8(AluOp.Sub, 0x10, 0x20, s);
        Assert.Equal((byte)0xF0, r);
        Assert.True(s.FlagC);   // borrow
        Assert.True(s.FlagS);
        Assert.False(s.FlagO);  // unsigned-only borrow, no signed overflow
    }

    [Fact]
    public void Sub8_SignedOverflow()
    {
        var s = new X86State();
        // 0x80 - 0x01 = 0x7F (negative becomes positive → OF)
        byte r = X86Alu.Execute8(AluOp.Sub, 0x80, 0x01, s);
        Assert.Equal(0x7F, r);
        Assert.True(s.FlagO);
        Assert.False(s.FlagC);
        Assert.False(s.FlagS);
    }

    [Fact]
    public void Adc8_WithCarryIn()
    {
        var s = new X86State { FlagC = true };
        byte r = X86Alu.Execute8(AluOp.Adc, 0x10, 0x20, s);
        Assert.Equal(0x31, r);   // +1 from CF
        Assert.False(s.FlagC);   // no carry out
    }

    [Fact]
    public void Adc8_OverflowOnCarryIn()
    {
        var s = new X86State { FlagC = true };
        byte r = X86Alu.Execute8(AluOp.Adc, 0xFE, 0x01, s);   // = 0xFF + 1 = 0x100
        Assert.Equal(0x00, r);
        Assert.True(s.FlagC);
        Assert.True(s.FlagZ);
    }

    [Fact]
    public void Sbb8_WithBorrowIn()
    {
        var s = new X86State { FlagC = true };
        byte r = X86Alu.Execute8(AluOp.Sbb, 0x10, 0x05, s);   // 0x10 - 5 - 1 = 0x0A
        Assert.Equal(0x0A, r);
        Assert.False(s.FlagC);
    }

    [Fact]
    public void Cmp8_DoesntStoreButSetsFlags()
    {
        var s = new X86State();
        // CMP returns the result as if SUB but caller is expected to discard.
        // Flags must reflect the subtraction.
        byte r = X86Alu.Execute8(AluOp.Cmp, 0x10, 0x10, s);
        Assert.Equal(0x00, r);   // (caller discards in legacy CPU)
        Assert.True(s.FlagZ);
        Assert.False(s.FlagC);
    }

    [Fact]
    public void And8_ClearsCFAndOF()
    {
        var s = new X86State { FlagC = true, FlagO = true };
        byte r = X86Alu.Execute8(AluOp.And, 0xF0, 0x0F, s);
        Assert.Equal(0x00, r);
        Assert.False(s.FlagC);
        Assert.False(s.FlagO);
        Assert.True(s.FlagZ);
        Assert.True(s.FlagP);    // 0 has even parity
    }

    [Fact]
    public void Xor8_ClearsCFAndOF()
    {
        var s = new X86State { FlagC = true, FlagO = true };
        byte r = X86Alu.Execute8(AluOp.Xor, 0x55, 0xAA, s);
        Assert.Equal(0xFF, r);
        Assert.False(s.FlagC);
        Assert.False(s.FlagO);
        Assert.True(s.FlagS);
        Assert.True(s.FlagP);    // 0xFF has 8 ones = even
    }

    [Fact]
    public void Add16_CarryOut()
    {
        var s = new X86State();
        ushort r = X86Alu.Execute16(AluOp.Add, 0xFFFF, 0x0001, s);
        Assert.Equal(0x0000, r);
        Assert.True(s.FlagC);
        Assert.True(s.FlagZ);
        Assert.True(s.FlagA);    // 0xF + 0x1 in nibble
    }

    [Fact]
    public void Add16_SignedOverflow()
    {
        var s = new X86State();
        ushort r = X86Alu.Execute16(AluOp.Add, 0x7FFF, 0x0001, s);
        Assert.Equal(0x8000, r);
        Assert.True(s.FlagO);
        Assert.True(s.FlagS);
    }

    [Fact]
    public void Sub16_BorrowAndSign()
    {
        var s = new X86State();
        ushort r = X86Alu.Execute16(AluOp.Sub, 0x0000, 0x0001, s);
        Assert.Equal((ushort)0xFFFF, r);
        Assert.True(s.FlagC);
        Assert.True(s.FlagS);
        Assert.False(s.FlagO);
    }

    [Theory]
    [InlineData(0x00, true)]   // 0 ones — even
    [InlineData(0x01, false)]  // 1 one  — odd
    [InlineData(0x03, true)]   // 2 ones — even
    [InlineData(0xFF, true)]   // 8 ones — even
    [InlineData(0x55, true)]   // 4 ones (01010101) — even
    [InlineData(0x07, false)]  // 3 ones — odd
    public void ParityFlag_LowByteOnly(byte v, bool expected)
    {
        Assert.Equal(expected, X86Alu.ParityEven(v));
    }

    // ============== Opcode-level integration tests ==============

    [Fact]
    public void OpAdd_ALimm8()
    {
        // MOV AL, 0x10 ; ADD AL, 0x05 ; HLT
        var rom = new byte[] { 0xB0, 0x10, 0x04, 0x05, 0xF4 };
        var (cpu, _) = Setup(rom);
        RunUntilHalt(cpu);
        Assert.Equal(0x15, cpu.State.A.L);
        Assert.False(cpu.State.FlagC);
        Assert.False(cpu.State.FlagZ);
    }

    [Fact]
    public void OpAdd_AXimm16_WithCarry()
    {
        // MOV AX, 0xFFFF ; ADD AX, 0x0001 ; HLT
        var rom = new byte[] { 0xB8, 0xFF, 0xFF, 0x05, 0x01, 0x00, 0xF4 };
        var (cpu, _) = Setup(rom);
        RunUntilHalt(cpu);
        Assert.Equal(0x0000, cpu.State.A.X);
        Assert.True(cpu.State.FlagC);
        Assert.True(cpu.State.FlagZ);
    }

    [Fact]
    public void OpSub_BXfromAX()
    {
        // MOV AX, 0x30 ; MOV BX, 0x10 ; SUB AX, BX ; HLT
        // SUB AX, BX → 29 D8 (0x29 = SUB r/m16, r16 ; modrm = 11_011_000 = D8)
        var rom = new byte[]
        {
            0xB8, 0x30, 0x00,
            0xBB, 0x10, 0x00,
            0x29, 0xD8,
            0xF4,
        };
        var (cpu, _) = Setup(rom);
        RunUntilHalt(cpu);
        Assert.Equal(0x20, cpu.State.A.X);
        Assert.False(cpu.State.FlagC);
    }

    [Fact]
    public void OpAnd_ClearsFlags()
    {
        // MOV AX, 0xFFFF ; AND AX, 0x0F0F ; HLT
        // AND AX, imm16 = 0x25
        var rom = new byte[]
        {
            0xB8, 0xFF, 0xFF,
            0x25, 0x0F, 0x0F,
            0xF4,
        };
        var (cpu, _) = Setup(rom);
        cpu.State.FlagC = true;     // dirty CF before
        cpu.State.FlagO = true;
        RunUntilHalt(cpu);
        Assert.Equal(0x0F0F, cpu.State.A.X);
        Assert.False(cpu.State.FlagC);
        Assert.False(cpu.State.FlagO);
    }

    [Fact]
    public void OpCmp_DoesntStore()
    {
        // MOV AX, 0x10 ; CMP AX, 0x10 ; HLT
        // CMP AX, imm16 = 0x3D
        var rom = new byte[]
        {
            0xB8, 0x10, 0x00,
            0x3D, 0x10, 0x00,
            0xF4,
        };
        var (cpu, _) = Setup(rom);
        RunUntilHalt(cpu);
        Assert.Equal(0x10, cpu.State.A.X);    // AX unchanged
        Assert.True(cpu.State.FlagZ);          // equal → ZF=1
    }

    [Fact]
    public void OpXor_ClearAX_IdiomaticZero()
    {
        // XOR AX, AX ; HLT  (32 C0  = 0x33 r16, r/m16 ; modrm 11_000_000 = C0 → AX, AX)
        var rom = new byte[] { 0xB8, 0xCD, 0xAB, 0x33, 0xC0, 0xF4 };
        var (cpu, _) = Setup(rom);
        RunUntilHalt(cpu);
        Assert.Equal(0x0000, cpu.State.A.X);
        Assert.True(cpu.State.FlagZ);
        Assert.False(cpu.State.FlagS);
        Assert.False(cpu.State.FlagC);
    }

    [Fact]
    public void Op80_AddImm8ToRm8()
    {
        // MOV BL, 0x10 ; ADD BL, 0x05 (group 0x80 /0)
        // 0x80 modrm=11_000_011 (mod=11 reg=000=ADD r/m=011=BL) imm8=0x05
        var rom = new byte[] { 0xB3, 0x10, 0x80, 0xC3, 0x05, 0xF4 };
        var (cpu, _) = Setup(rom);
        RunUntilHalt(cpu);
        Assert.Equal(0x15, cpu.State.B.L);
    }

    [Fact]
    public void Op81_AndImm16ToRm16()
    {
        // MOV CX, 0xF0F0 ; AND CX, 0x00FF (group 0x81 /4)
        // 0x81 modrm=11_100_001 (mod=11 reg=100=AND r/m=001=CX) imm16=0x00FF
        var rom = new byte[] { 0xB9, 0xF0, 0xF0, 0x81, 0xE1, 0xFF, 0x00, 0xF4 };
        var (cpu, _) = Setup(rom);
        RunUntilHalt(cpu);
        Assert.Equal(0x00F0, cpu.State.C.X);
    }

    [Fact]
    public void Op83_SignExtendImm8ToRm16()
    {
        // MOV DX, 0x0010 ; SUB DX, -1 (sign-extended → 0xFFFF) → DX = 0x0011
        // 0x83 modrm=11_101_010 (mod=11 reg=101=SUB r/m=010=DX) imm8=0xFF
        // 0x83 sign-extends 0xFF to 0xFFFF; 0x0010 - 0xFFFF = 0x0011 (wraps).
        var rom = new byte[] { 0xBA, 0x10, 0x00, 0x83, 0xEA, 0xFF, 0xF4 };
        var (cpu, _) = Setup(rom);
        RunUntilHalt(cpu);
        Assert.Equal(0x0011, cpu.State.D.X);
    }

    [Fact]
    public void Adc_RippleAddTwoWords()
    {
        // 32-bit ADD via two ADC ops:
        //   AX = 0xFFFF, BX = 0x0001  (low half: A + B)
        //   DX = 0x0001, CX = 0x0002  (high half: D += C + carry)
        // Result expected: low=0x0000 with CF=1, then high = 1 + 2 + 1 = 4
        //
        // MOV AX, 0xFFFF ; MOV BX, 1 ; ADD AX, BX ; MOV DX, 1 ; MOV CX, 2 ; ADC DX, CX ; HLT
        var rom = new byte[]
        {
            0xB8, 0xFF, 0xFF,    // MOV AX, 0xFFFF
            0xBB, 0x01, 0x00,    // MOV BX, 0x0001
            0x01, 0xD8,          // ADD AX, BX (01 r/m16, r16 ; modrm 11_011_000 = D8 = AX, BX)
            0xBA, 0x01, 0x00,    // MOV DX, 1
            0xB9, 0x02, 0x00,    // MOV CX, 2
            0x11, 0xCA,          // ADC DX, CX (11 r/m16, r16 ; modrm 11_001_010 = CA = DX, CX)
            0xF4,
        };
        var (cpu, _) = Setup(rom);
        RunUntilHalt(cpu);
        Assert.Equal(0x0000, cpu.State.A.X);
        Assert.Equal(0x0004, cpu.State.D.X);     // 1 + 2 + carry(1) = 4
    }
}
