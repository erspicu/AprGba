using AprCpu.Core.Decoder;

namespace AprCpu.Core.Runtime;

/// <summary>
/// Phase 7 A.1 — a "basic block" of consecutive instructions detected by
/// <see cref="BlockDetector"/>. The block contains every decoded instruction
/// from <see cref="StartPc"/> up to (and including) the first one that
/// terminates control flow within the block (writes PC, switches instruction
/// set, changes mode, or hits the per-block instruction-count cap).
///
/// A block is the unit of JIT compilation in Phase 7 A.2+. Multiple
/// per-instruction LLVM functions get merged into a single per-block
/// function, letting LLVM optimize across instruction boundaries
/// (CSE, DSE, register caching, dead-flag elimination).
/// </summary>
public sealed class Block
{
    /// <summary>PC value at the entry point of the block.</summary>
    public uint StartPc { get; }

    /// <summary>
    /// PC value just past the block's last instruction. For a block
    /// whose terminator instruction writes PC at runtime (branch / BX /
    /// LDM-PC / ALU-Rd=PC), this is the fall-through address — useful
    /// only when the runtime branch isn't taken or the spec marks
    /// <c>writes_pc: "conditional"</c>.
    /// </summary>
    public uint EndPc { get; }

    /// <summary>
    /// The instruction-set name this block was detected against (e.g.
    /// "ARM", "Thumb", "Main", "CB"). All instructions in the block
    /// share this set — instruction-set switches are a hard block
    /// boundary so the next block detect picks up under the new set.
    /// </summary>
    public string InstructionSetName { get; }

    /// <summary>
    /// Width in bytes of one instruction in this block (4 for ARM, 2
    /// for Thumb, 1 for LR35902 main, 1 for LR35902 CB). Cached from
    /// the spec so per-step PC-advance arithmetic is cheap.
    /// </summary>
    public uint InstrSizeBytes { get; }

    /// <summary>The decoded instructions, in PC order.</summary>
    public IReadOnlyList<DecodedBlockInstruction> Instructions { get; }

    /// <summary>
    /// Why the block ended. Useful for debugging + for the JIT to know
    /// whether to emit a trailing dispatcher-return (any reason except
    /// <see cref="BlockEndReason.Capped"/>) or to fall through directly
    /// into the next instruction's PC.
    /// </summary>
    public BlockEndReason EndReason { get; }

    public Block(
        uint startPc,
        uint endPc,
        string instructionSetName,
        uint instrSizeBytes,
        IReadOnlyList<DecodedBlockInstruction> instructions,
        BlockEndReason endReason)
    {
        if (instructions.Count == 0)
            throw new ArgumentException("Block must contain at least one instruction.", nameof(instructions));
        StartPc            = startPc;
        EndPc              = endPc;
        InstructionSetName = instructionSetName;
        InstrSizeBytes     = instrSizeBytes;
        Instructions       = instructions;
        EndReason          = endReason;
    }

    public override string ToString()
        => $"Block(set={InstructionSetName}, pc=0x{StartPc:X8}..0x{EndPc:X8}, " +
           $"{Instructions.Count} instr, end={EndReason})";
}

/// <summary>
/// One instruction in a <see cref="Block"/> — the runtime PC, raw
/// instruction word, byte length, and the spec/decode result it produced.
///
/// <para><b>LengthBytes</b> distinguishes per-instruction byte width for
/// variable-width sets (LR35902 = 1/2/3 bytes per opcode). Fixed-width
/// sets (ARM=4, Thumb=2) always have <see cref="LengthBytes"/> equal to
/// the parent <see cref="Block.InstrSizeBytes"/>; for variable-width
/// sets, <see cref="Block.InstrSizeBytes"/> is 0 (sentinel) and the
/// per-instruction value here is authoritative.</para>
///
/// <para><b>InstructionWord</b> packs the entire instruction's bytes
/// little-endian into a uint: 1-byte → opcode in LSB; 2-byte → opcode |
/// (imm8 &lt;&lt; 8); 3-byte → opcode | (imm16 &lt;&lt; 8). For CB-prefix
/// opcodes the packing is opcode (0xCB) | (sub_opcode &lt;&lt; 8). The
/// emitter for <c>read_imm8</c>/<c>read_imm16</c> can statically extract
/// the immediate via shift+mask in block-JIT mode without going through
/// the bus (Strategy 2 extension — see roadmap §4.3).</para>
/// </summary>
public sealed record DecodedBlockInstruction(
    uint Pc,
    uint InstructionWord,
    DecodedInstruction Decoded,
    byte LengthBytes,
    bool IsFollowedBranch = false,
    // N2.1 — pre-extracted immediate. For variable-width ISAs (LR35902,
    // MOS6502) BlockDetector mechanically reads the operand bytes after
    // the opcode and packs them little-endian into Immediate; emitters
    // can consume this as a compile-time constant (skipping the
    // BuildLShr+BuildTrunc dance over InstructionWord). For fixed-width
    // ISAs (ARM/Thumb) the imm is encoded in bit fields inside the
    // instruction word — leave null and let arch-specific emitters
    // extract via the existing pattern. Length-1 instructions (no
    // operand) also leave this null.
    uint? Immediate = null,
    // 24.6.8d — pre-fetched trailing bytes (after the opcode), little-
    // endian packed into a ulong. CISC ISAs (Intel 8086+) populate this
    // in BlockDetector when total instruction length ≤ 9 bytes; emitters
    // for FetchImm8 / FetchImm16 / ModR/M-byte / disp consume it as an
    // i64 LLVM constant via shift+trunc. Each in-instruction fetch
    // advances ctx.CurrentInstructionImmConsumed by its byte count, so
    // shift offset = ImmConsumed * 8. Null means either fixed-width
    // ISA, length 1 (no trailing bytes), or length > 9 (rare; emitters
    // fall back to the bus path). RISC variable-width ISAs (LR35902,
    // 6502) leave this null and use Immediate / InstructionWord directly.
    ulong? PackedTailBytes = null,
    // 24.6.8e — intra-block back-edge target. When this instruction is a
    // branch (LOOP/Jcc/JMP rel8/rel16) whose statically-computed target
    // PC matches another instruction inside the same block, BlockDetector
    // records that target instruction's index here. BlockFunctionBuilder
    // then directs the branch emitter to emit an LLVM CondBr / Br to
    // preBBs[BackEdgeTargetIndex] instead of writing IP + setting
    // PcWritten. This turns a tight inner loop (e.g. add/add/loop) into
    // an LLVM-native loop within the block function — alloca + mem2reg
    // give cross-iteration register SSA via phi nodes for free, removing
    // the dispatcher round-trip per iteration. Per Gemini's 2026-05-10
    // guidance: this is "intra-function CFG", NOT linear unrolling
    // (which would (a) blow up LLVM compile time with O(N²) regalloc
    // and (b) miscompile the iteration end condition). Block still
    // formally ends at this branch — execution exits the LLVM function
    // when the loop predicate fails, restoring the dispatcher's
    // schedule-and-redispatch flow.
    int? BackEdgeTargetIndex = null);

/// <summary>Why <see cref="BlockDetector"/> stopped collecting instructions.</summary>
public enum BlockEndReason
{
    /// <summary>Hit an instruction with <c>writes_pc</c> declared (branch / BX / call / ret / etc.).</summary>
    WritesPc,
    /// <summary>Instruction-set switch (e.g. LR35902 CB-prefix opcode).</summary>
    SwitchesInstructionSet,
    /// <summary>Mode change (CPSR.M write on ARM).</summary>
    ChangesMode,
    /// <summary>Decoder couldn't recognise the instruction word — emit dispatcher-return so runtime can throw.</summary>
    Undecodable,
    /// <summary>Per-block instruction cap reached without hitting a natural boundary.</summary>
    Capped,
}
