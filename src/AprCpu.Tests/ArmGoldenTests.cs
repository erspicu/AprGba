using System.Buffers.Binary;
using AprCpu.Core.Compilation;
using AprCpu.Core.Decoder;
using AprCpu.Core.IR;
using AprCpu.Core.JsonSpec;
using AprCpu.Core.Runtime;
using Xunit;

namespace AprCpu.Tests;

/// <summary>
/// Phase 3.3 — golden tests against the ARM Architecture Reference Manual
/// (ARMv4). Each test sets up a known pre-state, runs ONE instruction
/// through the JIT-compiled spec, and asserts every relevant CPSR flag
/// + register value matches what the ARM ARM specifies.
///
/// Coverage focus is on places where the spec / emitters could
/// silently get the semantics wrong:
/// - Flag computation (N/Z/C/V add vs sub)
/// - Carry-in plumbing (ADC)
/// - Barrel shifter and shifter_carry_out
/// - PSR transfer (MRS/MSR)
/// - Block transfer with writeback (LDMIA!)
/// - Multiply
/// </summary>
public class ArmGoldenTests
{
    private const uint UserModeEnc = 0x10;

    private static string CpuJson => Path.Combine(TestPaths.SpecRoot, "arm7tdmi", "cpu.json");

    private sealed class Setup : IDisposable
    {
        public CpuExecutor Exec { get; }
        public FlatMemoryBus Bus { get; }
        public HostRuntime Rt { get; }
        private readonly IDisposable _busBinding;
        private readonly IDisposable _swapBinding;

        public Setup(CpuExecutor exec, FlatMemoryBus bus, HostRuntime rt,
                     IDisposable busBinding, IDisposable swapBinding)
        { Exec = exec; Bus = bus; Rt = rt; _busBinding = busBinding; _swapBinding = swapBinding; }

        public void Dispose()
        { _busBinding.Dispose(); _swapBinding.Dispose(); Rt.Dispose(); }
    }

    private static Setup BuildOne(uint instruction)
    {
        var compileResult = SpecCompiler.Compile(CpuJson);
        var loaded = SpecLoader.LoadCpuSpec(CpuJson);
        var armSet = loaded.InstructionSets["ARM"];
        var layout = new CpuStateLayout(
            compileResult.Module.Context,
            loaded.Cpu.RegisterFile,
            loaded.Cpu.ProcessorModes,
            loaded.Cpu.ExceptionVectors);

        var rt = HostRuntime.Build(compileResult.Module, layout);
        var bus = new FlatMemoryBus(0x1000);
        bus.WriteWord(0, instruction);

        var busBinding  = MemoryBusBindings.Install(rt, bus);
        var swapBinding = BankSwapBindings.Install(rt, new Arm7tdmiBankSwapHandler(rt));
        rt.Compile();

        var exec = new CpuExecutor(rt, armSet, new DecoderTable(armSet), bus);
        exec.Pc = 0;
        exec.WriteStatus("CPSR", UserModeEnc);   // clean User-mode CPSR (no flags)
        return new Setup(exec, bus, rt, busBinding, swapBinding);
    }

    private static (uint N, uint Z, uint C, uint V) FlagsOf(uint cpsr) =>
        ((cpsr >> 31) & 1, (cpsr >> 30) & 1, (cpsr >> 29) & 1, (cpsr >> 28) & 1);

    // ---------------------------------------------------------------
    // Flag computation: N/Z/C/V on add and sub
    // ---------------------------------------------------------------

    [Fact]
    public void ADDS_DoublingFFFFFFFF_NSetCSetVClear()
    {
        // ADDS R0, R0, R0  with R0 = 0xFFFFFFFF
        // expect: R0 = 0xFFFFFFFE, N=1, Z=0, C=1 (unsigned overflow), V=0 (no signed overflow: -1 + -1 = -2 fits)
        using var s = BuildOne(0xE0900000);
        s.Exec.WriteGpr(0, 0xFFFFFFFFu);
        s.Exec.Step();
        Assert.Equal(0xFFFFFFFEu, s.Exec.ReadGpr(0));
        var (n, z, c, v) = FlagsOf(s.Exec.ReadStatus("CPSR"));
        Assert.Equal(1u, n);
        Assert.Equal(0u, z);
        Assert.Equal(1u, c);
        Assert.Equal(0u, v);
    }

    [Fact]
    public void SUBS_ZeroMinusOne_BorrowAndNegative()
    {
        // SUBS R0, R0, #1  with R0 = 0
        // expect: R0 = 0xFFFFFFFF, N=1, Z=0, C=0 (borrow occurred), V=0
        using var s = BuildOne(0xE2500001);
        s.Exec.WriteGpr(0, 0u);
        s.Exec.Step();
        Assert.Equal(0xFFFFFFFFu, s.Exec.ReadGpr(0));
        var (n, z, c, v) = FlagsOf(s.Exec.ReadStatus("CPSR"));
        Assert.Equal(1u, n);
        Assert.Equal(0u, z);
        Assert.Equal(0u, c);   // ARM C-after-SUB = NOT borrow → 0 means borrow
        Assert.Equal(0u, v);
    }

    [Fact]
    public void ADDS_PositiveOverflow_VSet()
    {
        // ADDS R0, R0, #1  with R0 = 0x7FFFFFFF (max positive signed)
        // expect: R0 = 0x80000000, N=1, Z=0, C=0, V=1
        using var s = BuildOne(0xE2900001);
        s.Exec.WriteGpr(0, 0x7FFFFFFFu);
        s.Exec.Step();
        Assert.Equal(0x80000000u, s.Exec.ReadGpr(0));
        var (n, z, c, v) = FlagsOf(s.Exec.ReadStatus("CPSR"));
        Assert.Equal(1u, n);
        Assert.Equal(0u, z);
        Assert.Equal(0u, c);
        Assert.Equal(1u, v);
    }

    // ---------------------------------------------------------------
    // CMP — flags only, no Rd write
    // ---------------------------------------------------------------

    [Fact]
    public void CMP_EqualOperands_ZSet()
    {
        // CMP R0, R1  with R0 = R1 = 0x12345678
        // CMP = SUBS but discards result. Expect Z=1, N=0, C=1 (no borrow), V=0
        using var s = BuildOne(0xE1500001);
        s.Exec.WriteGpr(0, 0x12345678u);
        s.Exec.WriteGpr(1, 0x12345678u);
        s.Exec.Step();
        // R0 unchanged
        Assert.Equal(0x12345678u, s.Exec.ReadGpr(0));
        var (n, z, c, v) = FlagsOf(s.Exec.ReadStatus("CPSR"));
        Assert.Equal(0u, n);
        Assert.Equal(1u, z);
        Assert.Equal(1u, c);
        Assert.Equal(0u, v);
    }

    // ---------------------------------------------------------------
    // Barrel shifter and shifter_carry_out
    // ---------------------------------------------------------------

    [Fact]
    public void MOV_LSL_Immediate_NoFlagUpdate()
    {
        // MOV R0, R1, LSL #4   with R1 = 0x10  → R0 = 0x100, no flags touched
        using var s = BuildOne(0xE1A00201);
        s.Exec.WriteGpr(1, 0x10u);
        s.Exec.WriteStatus("CPSR", UserModeEnc | (1u << 30));   // pre-set Z so we can verify it stayed
        s.Exec.Step();
        Assert.Equal(0x100u, s.Exec.ReadGpr(0));
        // Z still set (S-bit was 0 → no flag update)
        Assert.Equal(1u, (s.Exec.ReadStatus("CPSR") >> 30) & 1);
    }

    [Fact]
    public void MOVS_LSL_OneBitOffMSB_CarryFromShiftedOut()
    {
        // MOVS R0, R0, LSL #1  with R0 = 0x80000000
        // → R0 = 0, Z=1, N=0, C=1 (the bit shifted out = 1)
        using var s = BuildOne(0xE1B00080);
        s.Exec.WriteGpr(0, 0x80000000u);
        s.Exec.Step();
        Assert.Equal(0u, s.Exec.ReadGpr(0));
        var (n, z, c, _) = FlagsOf(s.Exec.ReadStatus("CPSR"));
        Assert.Equal(0u, n);
        Assert.Equal(1u, z);
        Assert.Equal(1u, c);
    }

    // ---------------------------------------------------------------
    // Multiply
    // ---------------------------------------------------------------

    [Fact]
    public void MUL_SmallOperands_GivesProduct()
    {
        // MUL R0, R1, R2   with R1=3, R2=5   → R0 = 15
        using var s = BuildOne(0xE0000291);
        s.Exec.WriteGpr(1, 3u);
        s.Exec.WriteGpr(2, 5u);
        s.Exec.Step();
        Assert.Equal(15u, s.Exec.ReadGpr(0));
    }

    // ---------------------------------------------------------------
    // Block transfer — LDMIA with writeback
    // ---------------------------------------------------------------

    [Fact]
    public void LDMIA_Writeback_LoadsThreeRegistersAndUpdatesBase()
    {
        // LDMIA R0!, {R1, R2, R3}
        // P=0 U=1 S=0 W=1 L=1, Rn=R0, list bits 1,2,3 set = 0x000E
        // = 1110 100 0 1 0 1 1 0000 0000 0000 0000 1110 = 0xE8B0000E
        using var s = BuildOne(0xE8B0000Eu);
        s.Bus.WriteWord(0x100, 0x11111111u);
        s.Bus.WriteWord(0x104, 0x22222222u);
        s.Bus.WriteWord(0x108, 0x33333333u);
        s.Exec.WriteGpr(0, 0x100u);
        s.Exec.Step();
        Assert.Equal(0x11111111u, s.Exec.ReadGpr(1));
        Assert.Equal(0x22222222u, s.Exec.ReadGpr(2));
        Assert.Equal(0x33333333u, s.Exec.ReadGpr(3));
        Assert.Equal(0x10Cu,      s.Exec.ReadGpr(0));   // base writeback to next address
    }

    // ---------------------------------------------------------------
    // PSR transfer
    // ---------------------------------------------------------------

    [Fact]
    public void MRS_R0_CPSR_CopiesCpsrIntoR0()
    {
        // MRS R0, CPSR   pattern: cccc 00010 P 001111 dddd 000000000000  with P=0
        //   1110 0001 0000 1111 0000 0000 0000 0000 = 0xE10F0000
        using var s = BuildOne(0xE10F0000);
        var cpsrValue = UserModeEnc | (1u << 31) | (1u << 29);  // N=1, C=1
        s.Exec.WriteStatus("CPSR", cpsrValue);
        s.Exec.Step();
        Assert.Equal(cpsrValue, s.Exec.ReadGpr(0));
    }
}
