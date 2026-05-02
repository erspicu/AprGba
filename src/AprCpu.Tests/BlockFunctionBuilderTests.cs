using System.Buffers.Binary;
using AprCpu.Core.Compilation;
using AprCpu.Core.Decoder;
using AprCpu.Core.IR;
using AprCpu.Core.JsonSpec;
using AprCpu.Core.Runtime;
using LLVMSharp.Interop;
using Xunit;

namespace AprCpu.Tests;

/// <summary>
/// Phase 7 A.2 — end-to-end test: build a Block, emit IR for the whole
/// block via BlockFunctionBuilder, JIT, run it, verify state matches
/// what running each instruction individually would produce.
/// </summary>
public class BlockFunctionBuilderTests
{
    private static string CpuJson => Path.Combine(TestPaths.SpecRoot, "arm7tdmi", "cpu.json");

    /// <summary>
    /// Three plain MOVs (no branch) — block runs all three, state ends
    /// with R0=1, R1=2, R2=3 and PC advanced past the last instruction.
    /// </summary>
    [Fact]
    public unsafe void Block_ThreeMovs_AllExecuteAndPcAdvances()
    {
        var loaded = SpecLoader.LoadCpuSpec(CpuJson);
        var armSet = loaded.InstructionSets["ARM"];
        var compileResult = SpecCompiler.Compile(loaded);

        // Build a block: MOV R0,#1 / MOV R1,#2 / MOV R2,#3 (then a B to
        // make the block stop — we won't actually take that branch since
        // we set PC to just before it).
        var bus = new FlatBus(0x1000);
        bus.WriteWord(0x100, 0xE3A00001u);  // MOV R0,#1
        bus.WriteWord(0x104, 0xE3A01002u);  // MOV R1,#2
        bus.WriteWord(0x108, 0xE3A02003u);  // MOV R2,#3
        bus.WriteWord(0x10C, 0xEAFFFFFEu);  // B .  (block boundary)

        var detector = new BlockDetector(armSet, compileResult.DecoderTables["ARM"]);
        var block = detector.Detect(bus, 0x100u);
        Assert.Equal(4, block.Instructions.Count);

        // Build the block function into the same module.
        var bfb = new BlockFunctionBuilder(
            compileResult.Module, compileResult.Layout,
            compileResult.EmitterRegistry, compileResult.ResolverRegistry);
        bfb.Build(armSet, block);

        // Bind the JIT (no externs needed for plain MOVs).
        using var rt = HostRuntime.Build(compileResult.Module, compileResult.Layout);
        rt.Compile();

        var fnName = BlockFunctionBuilder.BlockFunctionName("ARM", 0x100u);
        var fnPtr = rt.GetFunctionPointer(fnName);
        var fn = (delegate* unmanaged[Cdecl]<byte*, void>)fnPtr;

        Span<byte> state = stackalloc byte[(int)rt.StateSizeBytes];
        state.Clear();
        // Pre-set CPSR to AL-mode user (so cond gate passes). User-mode = 0x10.
        WriteI32(state, rt.StatusOffset("CPSR"), 0x10u);

        fixed (byte* p = state)
            fn(p);

        // R0..R2 set by the three MOVs.
        Assert.Equal(1u, ReadI32(state, rt.GprOffset(0)));
        Assert.Equal(2u, ReadI32(state, rt.GprOffset(1)));
        Assert.Equal(3u, ReadI32(state, rt.GprOffset(2)));
        // R15 (PC) should be the branch target written by the final B
        // instruction (B .  → branch to the B itself, PC=0x10C). Branch
        // target encoding = (signed_offset_24 << 2) + PC + 8 (pipeline).
        // Imm24 = 0xFFFFFE → signed = -2; (-2 << 2) = -8. PC at exec
        // time = 0x10C + 8 = 0x114; target = 0x114 - 8 = 0x10C.
        Assert.Equal(0x10Cu, ReadI32(state, rt.GprOffset(15)));
    }

    /// <summary>
    /// First instruction's cond is "EQ" with Z flag clear → cond fails,
    /// so the first MOV is skipped; second MOV has AL cond, runs.
    /// Verifies the per-instruction cond gate works inside a block.
    /// </summary>
    [Fact]
    public unsafe void Block_FirstInstructionCondFails_SecondStillRuns()
    {
        var loaded = SpecLoader.LoadCpuSpec(CpuJson);
        var armSet = loaded.InstructionSets["ARM"];
        var compileResult = SpecCompiler.Compile(loaded);

        // MOVEQ R0, #0xAA  (cond=EQ → cccc=0000)
        // MOV   R1, #0xBB  (cond=AL)
        // B .              (boundary)
        var bus = new FlatBus(0x1000);
        bus.WriteWord(0x100, 0x03A000AAu);  // MOVEQ R0,#0xAA
        bus.WriteWord(0x104, 0xE3A010BBu);  // MOV   R1,#0xBB
        bus.WriteWord(0x108, 0xEAFFFFFEu);  // B .

        var detector = new BlockDetector(armSet, compileResult.DecoderTables["ARM"]);
        var block = detector.Detect(bus, 0x100u);
        Assert.Equal(3, block.Instructions.Count);

        var bfb = new BlockFunctionBuilder(
            compileResult.Module, compileResult.Layout,
            compileResult.EmitterRegistry, compileResult.ResolverRegistry);
        bfb.Build(armSet, block);

        using var rt = HostRuntime.Build(compileResult.Module, compileResult.Layout);
        rt.Compile();

        var fnPtr = rt.GetFunctionPointer(
            BlockFunctionBuilder.BlockFunctionName("ARM", 0x100u));
        var fn = (delegate* unmanaged[Cdecl]<byte*, void>)fnPtr;

        Span<byte> state = stackalloc byte[(int)rt.StateSizeBytes];
        state.Clear();
        // CPSR = User mode + Z=0 (so EQ fails, AL passes).
        WriteI32(state, rt.StatusOffset("CPSR"), 0x10u);

        fixed (byte* p = state)
            fn(p);

        Assert.Equal(0u,    ReadI32(state, rt.GprOffset(0)));   // MOVEQ skipped
        Assert.Equal(0xBBu, ReadI32(state, rt.GprOffset(1)));   // MOV ran
    }

    /// <summary>
    /// FlatBus = simple 64KB byte array implementing IMemoryBus, used by
    /// these tests to feed instructions into BlockDetector.
    /// </summary>
    private sealed class FlatBus : IMemoryBus
    {
        private readonly byte[] _mem;
        public FlatBus(int size) { _mem = new byte[size]; }
        public void WriteWord(uint addr, uint v)
        {
            _mem[addr]     = (byte)(v & 0xFF);
            _mem[addr + 1] = (byte)((v >> 8)  & 0xFF);
            _mem[addr + 2] = (byte)((v >> 16) & 0xFF);
            _mem[addr + 3] = (byte)((v >> 24) & 0xFF);
        }
        public byte   ReadByte    (uint addr) => _mem[addr];
        public ushort ReadHalfword(uint addr) =>
            (ushort)(_mem[addr] | (_mem[addr + 1] << 8));
        public uint   ReadWord    (uint addr) =>
            (uint)(_mem[addr] | (_mem[addr + 1] << 8) | (_mem[addr + 2] << 16) | (_mem[addr + 3] << 24));
        void IMemoryBus.WriteByte(uint addr, byte value) => _mem[addr] = value;
        void IMemoryBus.WriteHalfword(uint addr, ushort value)
        {
            _mem[addr] = (byte)value; _mem[addr + 1] = (byte)(value >> 8);
        }
        void IMemoryBus.WriteWord(uint addr, uint value) => WriteWord(addr, value);
    }

    private static uint ReadI32(Span<byte> s, ulong off)
        => BinaryPrimitives.ReadUInt32LittleEndian(s.Slice((int)off, 4));
    private static void WriteI32(Span<byte> s, ulong off, uint v)
        => BinaryPrimitives.WriteUInt32LittleEndian(s.Slice((int)off, 4), v);
}
