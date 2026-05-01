using System.Buffers.Binary;
using AprCpu.Core.Compilation;
using AprCpu.Core.IR;
using AprCpu.Core.JsonSpec;
using AprCpu.Core.Runtime;
using Xunit;

namespace AprCpu.Tests;

/// <summary>
/// Phase 3.1.a: end-to-end JIT smoke test. Compile the full ARM7TDMI
/// spec, JIT one instruction, run it against a host-supplied byte
/// buffer, assert post-state.
///
/// These tests don't bind any externs (memory_read/write,
/// host_swap_register_bank) — they exercise only instructions whose
/// emit body touches GPR/CPSR slots and nothing else.
/// </summary>
public class HostRuntimeTests
{
    private static string CpuJson => Path.Combine(TestPaths.SpecRoot, "arm7tdmi", "cpu.json");

    [Fact]
    public unsafe void ArmAddImmediate_WritesExpectedSum()
    {
        // ARM "ADD R0, R1, #5" (cond=AL, S=0, imm form)
        //   cccc 001 oooo s nnnn dddd rrrr iiiiiiii
        //   1110 001 0100 0 0001 0000 0000 00000101
        //   = 0xE2810005
        const uint instruction = 0xE2810005;

        var result = SpecCompiler.Compile(CpuJson);
        using var rt = HostRuntime.Create(result.Module, GetLayout(result));

        var fnPtr = rt.GetFunctionPointer("Execute_ARM_DataProcessing_Immediate_ADD");
        var fn = (delegate* unmanaged[Cdecl]<byte*, uint, void>)fnPtr;

        Span<byte> state = stackalloc byte[(int)rt.StateSizeBytes];
        state.Clear();
        WriteI32(state, rt.GprOffset(1), 10u);  // R1 = 10

        fixed (byte* p = state)
            fn(p, instruction);

        var r0 = ReadI32(state, rt.GprOffset(0));
        Assert.Equal(15u, r0);
    }

    [Fact]
    public unsafe void ArmMovImmediate_WritesValue()
    {
        // ARM "MOV R3, #0xAB" (cond=AL, S=0, imm form)
        //   cccc 001 1101 0 0000 dddd rrrr iiiiiiii   (opcode MOV = 1101)
        //   1110 001 1101 0 0000 0011 0000 10101011
        //   = 0xE3A030AB
        const uint instruction = 0xE3A030AB;

        var result = SpecCompiler.Compile(CpuJson);
        using var rt = HostRuntime.Create(result.Module, GetLayout(result));

        var fnPtr = rt.GetFunctionPointer("Execute_ARM_DataProcessing_Immediate_MOV");
        var fn = (delegate* unmanaged[Cdecl]<byte*, uint, void>)fnPtr;

        Span<byte> state = stackalloc byte[(int)rt.StateSizeBytes];
        state.Clear();

        fixed (byte* p = state)
            fn(p, instruction);

        var r3 = ReadI32(state, rt.GprOffset(3));
        Assert.Equal(0xABu, r3);
    }

    [Fact]
    public unsafe void ArmLdrImmediate_LoadsWordFromMemoryBus()
    {
        // ARM "LDR R0, [R1, #4]" pre-index, add, no writeback, word
        //   cccc 010 P U 0 W 1 nnnn dddd iiiiiiiiiiii
        //   1110 010 1 1 0 0 1 0001 0000 000000000100
        //   = 0xE5910004
        const uint instruction = 0xE5910004;

        var result = SpecCompiler.Compile(CpuJson);
        using var rt = HostRuntime.Build(result.Module, GetLayout(result));

        var bus = new FlatMemoryBus(0x100);
        bus.WriteWord(0x14, 0xCAFEF00Du);  // R1=0x10, +4 = 0x14
        using var _ = MemoryBusBindings.Install(rt, bus);
        rt.Compile();

        var fnPtr = rt.GetFunctionPointer("Execute_ARM_SDT_Imm_LDR_LDR");
        var fn = (delegate* unmanaged[Cdecl]<byte*, uint, void>)fnPtr;

        Span<byte> state = stackalloc byte[(int)rt.StateSizeBytes];
        state.Clear();
        WriteI32(state, rt.GprOffset(1), 0x10u);

        fixed (byte* p = state)
            fn(p, instruction);

        Assert.Equal(0xCAFEF00Du, ReadI32(state, rt.GprOffset(0)));
    }

    [Fact]
    public unsafe void ArmStrImmediate_StoresWordToMemoryBus()
    {
        // ARM "STR R2, [R3, #8]" pre-index, add, no writeback, word
        //   1110 010 1 1 0 0 0 0011 0010 000000001000
        //   = 0xE5832008
        const uint instruction = 0xE5832008;

        var result = SpecCompiler.Compile(CpuJson);
        using var rt = HostRuntime.Build(result.Module, GetLayout(result));

        var bus = new FlatMemoryBus(0x100);
        using var _ = MemoryBusBindings.Install(rt, bus);
        rt.Compile();

        var fnPtr = rt.GetFunctionPointer("Execute_ARM_SDT_Imm_STR_STR");
        var fn = (delegate* unmanaged[Cdecl]<byte*, uint, void>)fnPtr;

        Span<byte> state = stackalloc byte[(int)rt.StateSizeBytes];
        state.Clear();
        WriteI32(state, rt.GprOffset(2), 0x12345678u);
        WriteI32(state, rt.GprOffset(3), 0x20u);

        fixed (byte* p = state)
            fn(p, instruction);

        Assert.Equal(0x12345678u, bus.ReadWord(0x28u));
    }

    [Fact]
    public unsafe void ArmStrbImmediate_OnlyWritesOneByte()
    {
        // ARM "STRB R0, [R1, #1]" — byte write should not touch neighbors
        //   cccc 010 P U 1 W 0 nnnn dddd iiiiiiiiiiii
        //   1110 010 1 1 1 0 0 0001 0000 000000000001
        //   = 0xE5C10001
        const uint instruction = 0xE5C10001;

        var result = SpecCompiler.Compile(CpuJson);
        using var rt = HostRuntime.Build(result.Module, GetLayout(result));

        var bus = new FlatMemoryBus(0x100);
        bus.WriteWord(0x10, 0xAABBCCDDu);  // pre-fill so we can detect spillover
        using var _ = MemoryBusBindings.Install(rt, bus);
        rt.Compile();

        var fnPtr = rt.GetFunctionPointer("Execute_ARM_SDT_Imm_STRB_STRB");
        var fn = (delegate* unmanaged[Cdecl]<byte*, uint, void>)fnPtr;

        Span<byte> state = stackalloc byte[(int)rt.StateSizeBytes];
        state.Clear();
        WriteI32(state, rt.GprOffset(0), 0x42u);   // STRB writes only low byte
        WriteI32(state, rt.GprOffset(1), 0x10u);

        fixed (byte* p = state)
            fn(p, instruction);

        // Address 0x11 should be 0x42; the rest of the word at 0x10 untouched
        Assert.Equal((byte)0xDD, bus.ReadByte(0x10));  // unchanged
        Assert.Equal((byte)0x42, bus.ReadByte(0x11));  // written
        Assert.Equal((byte)0xBB, bus.ReadByte(0x12));  // unchanged
        Assert.Equal((byte)0xAA, bus.ReadByte(0x13));  // unchanged
    }

    [Fact]
    public unsafe void ArmSwi_FullExceptionEntrySequence()
    {
        // ARM "SWI #0x42" — cccc 1111 oooooooooooooooooooooooo
        //   1110 1111 0000 0000 0000 0000 0100 0010
        //   = 0xEF000042
        // Exception entry expectations (per spec exception_vectors[SoftwareInterrupt]):
        //   - SPSR_Supervisor := old CPSR
        //   - banked R14_Supervisor := next-PC = R15 - (pc_offset_bytes - instr_size) = R15 - 4
        //   - CPSR.M := Supervisor (10011 = 0x13)
        //   - CPSR.I (bit 7) := 1 (disable list per spec = ["I"])
        //   - host_swap_register_bank invoked (visible R13/R14 swap with SVC bank)
        //   - PC := 0x8 (SWI vector address)
        const uint instruction = 0xEF000042;
        const uint userModeEnc = 0b10000;       // 0x10
        const uint svcModeEnc  = 0b10011;       // 0x13
        const uint initialR15  = 0x100u;        // pretend we're executing at 0x100-pcoffset

        var result = SpecCompiler.Compile(CpuJson);
        var layout = GetLayout(result);
        using var rt = HostRuntime.Build(result.Module, layout);

        var swapHandler = new Arm7tdmiBankSwapHandler(rt);
        using var _ = BankSwapBindings.Install(rt, swapHandler);
        rt.Compile();

        var fnPtr = rt.GetFunctionPointer("Execute_ARM_SWI_SWI");
        var fn = (delegate* unmanaged[Cdecl]<byte*, uint, void>)fnPtr;

        Span<byte> state = stackalloc byte[(int)rt.StateSizeBytes];
        state.Clear();
        WriteI32(state, rt.GprOffset(15), initialR15);
        var initialCpsr = userModeEnc;          // User mode, no flags
        WriteI32(state, rt.StatusOffset("CPSR"), initialCpsr);
        // Pre-seed SVC bank R14 with a sentinel so we can detect the swap moved data
        WriteI32(state, rt.BankedGprOffset("Supervisor", 1), 0xDEADBEEFu);  // index 1 = R14 in svc bank

        fixed (byte* p = state)
            fn(p, instruction);

        // SPSR_Supervisor saved old CPSR
        Assert.Equal(initialCpsr, ReadI32(state, rt.StatusOffset("SPSR", "Supervisor")));

        // banked R14_Supervisor = next-PC = initialR15 - (8 - 4) = initialR15 - 4
        var expectedNextPc = initialR15 - 4u;
        // After swap, the visible R14 should now hold what was the SVC bank R14 (next_pc)
        Assert.Equal(expectedNextPc, ReadI32(state, rt.GprOffset(14)));

        // CPSR: mode bits → SVC, I bit set
        var newCpsr = ReadI32(state, rt.StatusOffset("CPSR"));
        Assert.Equal(svcModeEnc, newCpsr & 0x1Fu);
        Assert.Equal(1u << 7, newCpsr & (1u << 7));

        // PC = vector address
        Assert.Equal(0x8u, ReadI32(state, rt.GprOffset(15)));
    }

    [Fact]
    public unsafe void ArmAddImmediate_RespectsConditionGate()
    {
        // Same ADD as above but with cond=NE (0001) and Z flag set in CPSR.
        // Expected: R0 untouched (instruction skipped by cond gate).
        //   0001 001 0100 0 0001 0000 0000 00000101
        //   = 0x12810005
        const uint instruction = 0x12810005;

        var result = SpecCompiler.Compile(CpuJson);
        using var rt = HostRuntime.Create(result.Module, GetLayout(result));

        var fnPtr = rt.GetFunctionPointer("Execute_ARM_DataProcessing_Immediate_ADD");
        var fn = (delegate* unmanaged[Cdecl]<byte*, uint, void>)fnPtr;

        Span<byte> state = stackalloc byte[(int)rt.StateSizeBytes];
        state.Clear();
        WriteI32(state, rt.GprOffset(0), 0xDEADBEEFu);  // R0 sentinel
        WriteI32(state, rt.GprOffset(1), 10u);
        // CPSR.Z = bit 30 → set, so cond=NE (Z==0) is FALSE, instr should be skipped
        WriteI32(state, rt.StatusOffset("CPSR"), 1u << 30);

        fixed (byte* p = state)
            fn(p, instruction);

        var r0 = ReadI32(state, rt.GprOffset(0));
        Assert.Equal(0xDEADBEEFu, r0);
    }

    // ---- helpers ----

    private static CpuStateLayout GetLayout(SpecCompiler.CompileResult result)
    {
        // SpecCompiler doesn't expose its internal layout. Rebuild one from
        // the same spec — both will produce identical struct shapes because
        // the layout is fully determined by RegisterFile + ProcessorModes.
        var loaded = SpecLoader.LoadCpuSpec(CpuJson);
        return new CpuStateLayout(
            result.Module.Context,
            loaded.Cpu.RegisterFile,
            loaded.Cpu.ProcessorModes,
            loaded.Cpu.ExceptionVectors);
    }

    private static void WriteI32(Span<byte> buf, ulong offset, uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice((int)offset, 4), value);

    private static uint ReadI32(Span<byte> buf, ulong offset)
        => BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice((int)offset, 4));
}
