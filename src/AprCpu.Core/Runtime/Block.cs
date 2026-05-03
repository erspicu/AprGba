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
    bool IsFollowedBranch = false);

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
