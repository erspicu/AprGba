// Phase 24.2.2 — MOV opcode coverage tests.
//
// Drive X86LegacyCpu through every MOV form and verify the final
// register / memory state. These tests don't yet hit Tom Harte SST
// (that's phase 24.2.4) — they're focused per-opcode checks that fail
// fast if a specific MOV variant has a bug.

using AprX86.Cli.Cpu;
using AprX86.Cli.Memory;
using Xunit;

namespace AprCpu.Tests;

public class X86MovTests
{
    private static (X86LegacyCpu cpu, X86Memory mem) Setup(byte[] code)
    {
        var mem = new X86Memory();
        mem.LoadBinary(code, 0, 0x100);
        var cpu = new X86LegacyCpu(mem);
        cpu.Reset();
        cpu.SetEntryPoint(0, 0x100);
        return (cpu, mem);
    }

    private static void RunUntilHalt(X86LegacyCpu cpu, int maxSteps = 100)
    {
        for (int i = 0; i < maxSteps && !cpu.Halted; i++)
            cpu.Step();
        Assert.True(cpu.Halted, "CPU did not halt within step budget");
    }

    [Fact]
    public void MovImm8_AllEightRegisters()
    {
        // MOV AL/CL/DL/BL/AH/CH/DH/BH = 0x10..0x17 ; HLT
        var rom = new byte[]
        {
            0xB0, 0x10,    // MOV AL, 0x10
            0xB1, 0x11,    // MOV CL, 0x11
            0xB2, 0x12,    // MOV DL, 0x12
            0xB3, 0x13,    // MOV BL, 0x13
            0xB4, 0x14,    // MOV AH, 0x14
            0xB5, 0x15,    // MOV CH, 0x15
            0xB6, 0x16,    // MOV DH, 0x16
            0xB7, 0x17,    // MOV BH, 0x17
            0xF4,          // HLT
        };
        var (cpu, _) = Setup(rom);
        RunUntilHalt(cpu);

        for (int i = 0; i < 8; i++)
            Assert.Equal((byte)(0x10 + i), cpu.State.GetReg8(i));
    }

    [Fact]
    public void MovImm16_AllEightRegisters()
    {
        var rom = new byte[]
        {
            0xB8, 0x00, 0xA0,        // MOV AX, 0xA000
            0xB9, 0x11, 0xA1,        // MOV CX, 0xA111
            0xBA, 0x22, 0xA2,        // MOV DX, 0xA222
            0xBB, 0x33, 0xA3,        // MOV BX, 0xA333
            0xBC, 0x44, 0xA4,        // MOV SP, 0xA444
            0xBD, 0x55, 0xA5,        // MOV BP, 0xA555
            0xBE, 0x66, 0xA6,        // MOV SI, 0xA666
            0xBF, 0x77, 0xA7,        // MOV DI, 0xA777
            0xF4,
        };
        var (cpu, _) = Setup(rom);
        RunUntilHalt(cpu);

        for (int i = 0; i < 8; i++)
            Assert.Equal((ushort)(0xA000 | (i * 0x111)), cpu.State.GetReg16(i));
    }

    [Fact]
    public void MovRegReg16_RmFromReg()
    {
        // MOV AX, 0x1234 ; MOV BX, AX (89 C3 = mod=11 reg=000=AX r/m=011=BX)
        var rom = new byte[] { 0xB8, 0x34, 0x12, 0x89, 0xC3, 0xF4 };
        var (cpu, _) = Setup(rom);
        RunUntilHalt(cpu);
        Assert.Equal(0x1234, cpu.State.A.X);
        Assert.Equal(0x1234, cpu.State.B.X);
    }

    [Fact]
    public void MovRegReg16_RegFromRm()
    {
        // MOV AX, 0xCAFE ; MOV BX, AX via 8B opcode
        // (8B C3 = MOV r16, r/m16 ; mod=11 reg=000=AX r/m=011=BX → AX ← BX)
        // We need: AX=0xCAFE → MOV BX, ... then read back. Easier: MOV BX, AX (89 C3)
        var rom = new byte[] { 0xB8, 0xFE, 0xCA, 0x8B, 0xD8, 0xF4 };
        // 8B D8 = MOV r16, r/m16 ; modrm = 11_011_000 = 0xD8 (mod=11 reg=011=BX r/m=000=AX) → BX ← AX
        var (cpu, _) = Setup(rom);
        RunUntilHalt(cpu);
        Assert.Equal(0xCAFE, cpu.State.A.X);
        Assert.Equal(0xCAFE, cpu.State.B.X);
    }

    [Fact]
    public void MovRegReg8()
    {
        // MOV AH, 0xAB ; MOV DL, AH via 88 /r (88 E2 = mod=11 reg=100=AH r/m=010=DL)
        var rom = new byte[] { 0xB4, 0xAB, 0x88, 0xE2, 0xF4 };
        var (cpu, _) = Setup(rom);
        RunUntilHalt(cpu);
        Assert.Equal(0xAB, cpu.State.A.H);
        Assert.Equal(0xAB, cpu.State.D.L);
    }

    [Fact]
    public void MovDirectAddr16_StoreAndLoad()
    {
        // MOV AX, 0xBEEF
        // MOV [0x500], AX                ; via opcode 0xA3 (MOV [disp16], AX)
        // MOV BX, [0x500]                ; via opcode 0x8B + ModR/M direct (8B 1E 00 05)
        var rom = new byte[]
        {
            0xB8, 0xEF, 0xBE,
            0xA3, 0x00, 0x05,
            0x8B, 0x1E, 0x00, 0x05,
            0xF4,
        };
        var (cpu, mem) = Setup(rom);
        RunUntilHalt(cpu);
        Assert.Equal(0xBEEF, cpu.State.A.X);
        Assert.Equal(0xBEEF, cpu.State.B.X);
        Assert.Equal((byte)0xEF, mem.ReadByte(0x500));    // little-endian
        Assert.Equal((byte)0xBE, mem.ReadByte(0x501));
    }

    [Fact]
    public void MovDirectAddr8_StoreAndLoad()
    {
        // MOV AL, 0x42 ; MOV [0x300], AL ; MOV BL, [0x300]
        // Opcodes: B0 42 ; A2 00 03 ; 8A 1E 00 03 (MOV r8, r/m8 via 0x8A)
        var rom = new byte[]
        {
            0xB0, 0x42,
            0xA2, 0x00, 0x03,
            0x8A, 0x1E, 0x00, 0x03,
            0xF4,
        };
        var (cpu, mem) = Setup(rom);
        RunUntilHalt(cpu);
        Assert.Equal(0x42, cpu.State.A.L);
        Assert.Equal(0x42, cpu.State.B.L);
        Assert.Equal((byte)0x42, mem.ReadByte(0x300));
    }

    [Fact]
    public void MovImmToMem8_C6()
    {
        // MOV byte [0x500], 0x77   via 0xC6 /0
        // C6 /0 = ModR/M reg=000 (the opcode extension) ; mod=00 r/m=110 disp16
        var rom = new byte[]
        {
            0xC6, 0x06, 0x00, 0x05, 0x77,
            0xF4,
        };
        var (cpu, mem) = Setup(rom);
        RunUntilHalt(cpu);
        Assert.Equal((byte)0x77, mem.ReadByte(0x500));
    }

    [Fact]
    public void MovImmToMem16_C7()
    {
        // MOV word [0x600], 0x9988
        var rom = new byte[]
        {
            0xC7, 0x06, 0x00, 0x06, 0x88, 0x99,
            0xF4,
        };
        var (cpu, mem) = Setup(rom);
        RunUntilHalt(cpu);
        Assert.Equal(0x9988, mem.ReadWord(0x600));
    }

    [Fact]
    public void MovSreg_Roundtrip()
    {
        // MOV BX, 0x1234 ; MOV ES, BX (8E /r reg=000=ES) ; MOV CX, ES (8C /r reg=000=ES)
        // 8E C3 = mod=11 reg=000=ES r/m=011=BX → ES ← BX
        // 8C C1 = mod=11 reg=000=ES r/m=001=CX → CX ← ES
        var rom = new byte[]
        {
            0xBB, 0x34, 0x12,
            0x8E, 0xC3,
            0x8C, 0xC1,
            0xF4,
        };
        var (cpu, _) = Setup(rom);
        RunUntilHalt(cpu);
        Assert.Equal(0x1234, cpu.State.B.X);
        Assert.Equal(0x1234, cpu.State.ES);
        Assert.Equal(0x1234, cpu.State.C.X);
    }

    [Fact]
    public void MovWithSegmentOverride()
    {
        // Set ES=0x1000, write via MOV [DS:0x100], AX, then read via ES override
        // (write to physical 0x00100; ES:0x100 reads from physical 0x10100, so we expect 0)
        // Inverse test: MOV ES=0x10 ; MOV byte [ES:0x500], 0x55
        //   physical = (0x10 << 4) + 0x500 = 0x100 + 0x500 = 0x600
        var rom = new byte[]
        {
            0xB8, 0x10, 0x00,             // MOV AX, 0x10
            0x8E, 0xC0,                   // MOV ES, AX  (mod=11 reg=000=ES r/m=000=AX)
            0x26, 0xC6, 0x06, 0x00, 0x05, 0x55,   // ES: MOV byte [0x500], 0x55  →  physical 0x600
            0xF4,
        };
        var (cpu, mem) = Setup(rom);
        RunUntilHalt(cpu);
        Assert.Equal(0x10, cpu.State.ES);
        Assert.Equal((byte)0x55, mem.ReadByte(0x600));
        Assert.Equal((byte)0x00, mem.ReadByte(0x500));   // proves DS path was NOT used
    }

    [Fact]
    public void MovComprehensiveSmokeRom()
    {
        // The big 64-byte coverage ROM: 12 instructions exercising every
        // major MOV form (imm8/imm16, r/r 8/16, r/m direct addr 8/16,
        // sreg, imm-to-mem). End state has been hand-verified.
        var rom = System.IO.File.ReadAllBytes(System.IO.Path.Combine(
            TestPaths.RepoRoot, "test-roms", "x86", "24.2.2-mov-coverage.com"));
        var (cpu, mem) = Setup(rom);
        RunUntilHalt(cpu, maxSteps: 50);

        // Hand-derived expected end state (matches the comments in the
        // ROM-generator script).
        Assert.Equal(0xAA42, cpu.State.A.X);   // AH=0xAA from MOV AH; AL=0x42 from MOV AL,[0x500]
        Assert.Equal(0x5678, cpu.State.B.X);
        Assert.Equal(0x1234, cpu.State.C.X);
        Assert.Equal(0xAA9A, cpu.State.D.X);
        Assert.Equal(0x1234, cpu.State.BP);
        Assert.Equal(0x5678, cpu.State.ES);
        Assert.Equal(0x1234, mem.ReadWord(0x300));
        Assert.Equal((byte)0x42, mem.ReadByte(0x500));
    }
}
