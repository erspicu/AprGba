using AprCpu.Core.Decoder;
using AprCpu.Core.JsonSpec;
using AprCpu.Core.Runtime;
using Xunit;

namespace AprCpu.Tests;

/// <summary>
/// Phase 7 A.1 — verify BlockDetector stops at the right boundaries.
/// </summary>
public class BlockDetectorTests
{
    private static (BlockDetector det, FakeBus bus) BuildArm()
    {
        var setSpec = SpecLoader.LoadInstructionSet(
            Path.Combine(TestPaths.SpecRoot, "arm7tdmi", "arm.json"));
        var decoder = new DecoderTable(setSpec);
        return (new BlockDetector(setSpec, decoder), new FakeBus());
    }

    /// <summary>Three plain MOVs followed by an unconditional branch — block ends at the branch.</summary>
    [Fact]
    public void StopsAtBranch()
    {
        var (det, bus) = BuildArm();
        // MOV R0,#1 / MOV R1,#2 / MOV R2,#3 / B 0  (all AL cond)
        bus.WriteWord(0x0000, 0xE3A00001u);  // MOV R0,#1
        bus.WriteWord(0x0004, 0xE3A01002u);  // MOV R1,#2
        bus.WriteWord(0x0008, 0xE3A02003u);  // MOV R2,#3
        bus.WriteWord(0x000C, 0xEAFFFFFEu);  // B .   (B to self, branch_offset = -2)

        var blk = det.Detect(bus, 0x0000u);

        Assert.Equal(4, blk.Instructions.Count);
        Assert.Equal(BlockEndReason.WritesPc, blk.EndReason);
        Assert.Equal(0x0000u, blk.StartPc);
        Assert.Equal(0x0010u, blk.EndPc);
        Assert.Equal("MOV", blk.Instructions[0].Decoded.Instruction.Mnemonic);
        Assert.Equal("B",   blk.Instructions[3].Decoded.Instruction.Mnemonic);
    }

    /// <summary>
    /// ALU instructions with <c>writes_pc: "conditional_via_rd"</c> are
    /// NOT block-end (they only write PC if Rd=R15, which is rare).
    /// Block continues past MOVS PC, LR.
    /// </summary>
    [Fact]
    public void DoesNotStopAtConditionalPcWriter()
    {
        var (det, bus) = BuildArm();
        // ADD R0, R0, #1
        bus.WriteWord(0x0000, 0xE2800001u);
        // MOVS PC, LR  (E1B0_F00E — Rd=15 IS PC but spec marks as
        // conditional_via_rd, not always)
        bus.WriteWord(0x0004, 0xE1B0F00Eu);
        // MOV R2, #3 — should ALSO be in the block
        bus.WriteWord(0x0008, 0xE3A02003u);
        // B .  — definite block end
        bus.WriteWord(0x000C, 0xEAFFFFFEu);

        var blk = det.Detect(bus, 0x0000u);

        Assert.Equal(4, blk.Instructions.Count);
        Assert.Equal(BlockEndReason.WritesPc, blk.EndReason);
    }

    /// <summary>
    /// Long straight-line code — block honours the per-block cap.
    /// </summary>
    [Fact]
    public void RespectsMaxInstructionsCap()
    {
        var (det, bus) = BuildArm();
        // 100 plain MOVs back-to-back
        for (uint i = 0; i < 100; i++)
            bus.WriteWord(i * 4, 0xE3A00001u);  // MOV R0,#1

        var blk = det.Detect(bus, 0x0000u, maxInstructions: 16);

        Assert.Equal(16, blk.Instructions.Count);
        Assert.Equal(BlockEndReason.Capped, blk.EndReason);
        Assert.Equal(0x0040u, blk.EndPc);   // 16 × 4 bytes
    }

    // Note: a "stops at undecodable" test was considered but removed —
    // the ARM spec covers the entire 4-bit cond × 4-bit opcode space
    // (including NV cond + reserved-bit-pattern combos that map to
    // architecturally-undefined). It's hard to construct an opcode
    // word the spec actually returns null for. The Undecodable end
    // reason is still emitted correctly when it triggers (verified
    // via SWI/UND on Phase 4.x ROMs).

    /// <summary>StartPc not at 0 — make sure offsets are computed correctly.</summary>
    [Fact]
    public void StartPcNonZero()
    {
        var (det, bus) = BuildArm();
        bus.WriteWord(0x1000, 0xE3A00001u);  // MOV R0,#1
        bus.WriteWord(0x1004, 0xEAFFFFFEu);  // B .

        var blk = det.Detect(bus, 0x1000u);

        Assert.Equal(2, blk.Instructions.Count);
        Assert.Equal(0x1000u, blk.StartPc);
        Assert.Equal(0x1008u, blk.EndPc);
        Assert.Equal(0x1000u, blk.Instructions[0].Pc);
        Assert.Equal(0x1004u, blk.Instructions[1].Pc);
    }

    // -------------------------------------------------------------------
    // Phase 7 GB block-JIT P0.1 — variable-width detector tests
    // -------------------------------------------------------------------

    private static (BlockDetector det, FakeBus bus) BuildLr35902Main()
    {
        var setSpec = SpecLoader.LoadInstructionSet(
            Path.Combine(TestPaths.SpecRoot, "lr35902", "main.json"));
        var decoder = new DecoderTable(setSpec);
        return (
            new BlockDetector(setSpec, decoder, Lr35902InstructionLengths.GetLength),
            new FakeBus());
    }

    /// <summary>Variable-width detector advances PC per first-byte length lookup, not by fixed stride.</summary>
    [Fact]
    public void Lr35902_VariableWidthDetect_AdvancesByLengthTable()
    {
        var (det, bus) = BuildLr35902Main();
        // LD A,#0x42 (2 bytes: 3E 42) / LD B,A (1 byte: 47) / NOP (1 byte: 00) / JP nn (3 bytes: C3 04 80)
        bus.WriteByte(0x0000, 0x3E); bus.WriteByte(0x0001, 0x42);          // LD A,0x42
        bus.WriteByte(0x0002, 0x47);                                       // LD B,A
        bus.WriteByte(0x0003, 0x00);                                       // NOP
        bus.WriteByte(0x0004, 0xC3); bus.WriteByte(0x0005, 0x04); bus.WriteByte(0x0006, 0x80); // JP 0x8004

        var blk = det.Detect(bus, 0x0000u);

        Assert.Equal(4, blk.Instructions.Count);
        Assert.Equal(0x0000u, blk.Instructions[0].Pc); Assert.Equal((byte)2, blk.Instructions[0].LengthBytes);
        Assert.Equal(0x0002u, blk.Instructions[1].Pc); Assert.Equal((byte)1, blk.Instructions[1].LengthBytes);
        Assert.Equal(0x0003u, blk.Instructions[2].Pc); Assert.Equal((byte)1, blk.Instructions[2].LengthBytes);
        Assert.Equal(0x0004u, blk.Instructions[3].Pc); Assert.Equal((byte)3, blk.Instructions[3].LengthBytes);
        Assert.Equal(BlockEndReason.WritesPc, blk.EndReason);     // JP nn ends the block
        Assert.Equal(0x0007u, blk.EndPc);                         // 4 instructions, total 7 bytes
        Assert.True(det.IsVariableWidth);
        Assert.Equal(0u, det.InstrSizeBytes);                     // sentinel for variable-width
    }

    /// <summary>InstructionWord packs all bytes LE so emitter can extract imm via shift+mask.</summary>
    [Fact]
    public void Lr35902_VariableWidthDetect_PacksMultiByteInstructionWordLittleEndian()
    {
        var (det, bus) = BuildLr35902Main();
        bus.WriteByte(0x0000, 0x3E); bus.WriteByte(0x0001, 0x42);          // LD A,0x42 (2 bytes)
        bus.WriteByte(0x0002, 0xC3); bus.WriteByte(0x0003, 0xCD); bus.WriteByte(0x0004, 0xAB); // JP 0xABCD (3 bytes)

        var blk = det.Detect(bus, 0x0000u);

        // 2-byte LD A,n: opcode 0x3E in LSB, imm 0x42 in next byte → 0x0000_423E
        Assert.Equal(0x0000_423Eu, blk.Instructions[0].InstructionWord);
        // 3-byte JP nn: opcode 0xC3 in LSB, imm16 = 0xABCD packed LE → 0x00AB_CDC3
        Assert.Equal(0x00AB_CDC3u, blk.Instructions[1].InstructionWord);
    }

    /// <summary>
    /// Without lengthOracle, LR35902 main set falls back to 1-byte fixed
    /// (driven by spec's width_bits=8 fetch unit); detector won't throw
    /// but will misread multi-byte instructions. Documented baseline; the
    /// CpuExecutor wiring (P0.4) is responsible for always passing the
    /// oracle for LR35902 sets.
    /// </summary>
    [Fact]
    public void Lr35902_CtorWithoutLengthOracle_TreatedAsOneByteFixed()
    {
        var setSpec = SpecLoader.LoadInstructionSet(
            Path.Combine(TestPaths.SpecRoot, "lr35902", "main.json"));
        var decoder = new DecoderTable(setSpec);
        var det = new BlockDetector(setSpec, decoder);   // no oracle
        Assert.False(det.IsVariableWidth);
        Assert.Equal(1u, det.InstrSizeBytes);
    }

    // Minimal in-memory bus for tests — implements just the read/write
    // helpers the BlockDetector calls (ReadWord for ARM 4-byte fetch).
    // Backed by a flat 64KB byte array.
    private sealed class FakeBus : IMemoryBus
    {
        private readonly byte[] _mem = new byte[0x10000];

        public void WriteWord(uint addr, uint v)
        {
            _mem[addr]     = (byte)(v & 0xFF);
            _mem[addr + 1] = (byte)((v >> 8)  & 0xFF);
            _mem[addr + 2] = (byte)((v >> 16) & 0xFF);
            _mem[addr + 3] = (byte)((v >> 24) & 0xFF);
        }

        public void WriteByte(uint addr, byte v) => _mem[addr] = v;

        public byte   ReadByte    (uint addr) => _mem[addr];
        public ushort ReadHalfword(uint addr) =>
            (ushort)(_mem[addr] | (_mem[addr + 1] << 8));
        public uint   ReadWord    (uint addr) =>
            (uint)(_mem[addr] | (_mem[addr + 1] << 8) | (_mem[addr + 2] << 16) | (_mem[addr + 3] << 24));

        // Write side not used by detector — provide minimal stubs.
        void IMemoryBus.WriteHalfword(uint addr, ushort value)
        {
            _mem[addr] = (byte)value; _mem[addr + 1] = (byte)(value >> 8);
        }
        void IMemoryBus.WriteWord(uint addr, uint value) => WriteWord(addr, value);
    }
}
