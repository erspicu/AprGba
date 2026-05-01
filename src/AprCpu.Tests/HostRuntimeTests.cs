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
