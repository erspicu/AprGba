using AprCpu.Core.Decoder;
using AprCpu.Core.JsonSpec;

namespace AprCpu.Core.Runtime;

/// <summary>
/// Phase 7 A.1 — walk memory from a start PC, decode instructions until
/// the first block-boundary is hit (writes PC / switches instruction set
/// / changes mode / undecodable / cap reached).
///
/// Pure-data class — no JIT side-effects, no host runtime mutation. The
/// caller (CpuExecutor in A.2+) is responsible for actually executing
/// the block via the JIT or interpreter.
///
/// Block boundary criteria for MVP (deliberately conservative — a block
/// can always be SHORTER than ideal without correctness loss):
/// <list type="bullet">
///   <item>Last instruction marks <c>writes_pc: "always"</c> in spec
///         (B / BL / BX / SWI / undefined / coprocessor) — definitely
///         transfers control after this instruction.</item>
///   <item>Last instruction marks <c>switches_instruction_set</c>
///         (e.g. LR35902 CB-prefix dispatch).</item>
///   <item>Last instruction marks <c>changes_mode</c> (CPSR.M write).</item>
///   <item>Decoder returns null (undecodable opcode — let runtime decide).</item>
///   <item>Per-block instruction cap (default 64) reached.</item>
/// </list>
///
/// <para>NOT block-end: <c>writes_pc: "conditional"</c> /
/// <c>"conditional_via_rd"</c> instructions. ARM's data-processing ops
/// declare this because they CAN write PC if Rd=R15, but in practice
/// most don't. Including them as boundaries would split blocks at every
/// MOV/ADD/etc — defeats the point of block-JIT. The runtime PcWritten
/// flag check in the executor (already present pre-block-JIT) catches
/// the rare case where one of these actually writes PC, and dispatches
/// to the new block.</para>
///
/// <para>Branches that always-fall-through under conditional execution
/// still end the block — the runtime cond gate inside the JIT'd block
/// handles the conditional skip; whether or not the branch is taken,
/// control must return to dispatcher to find the next block.</para>
/// </summary>
public sealed class BlockDetector
{
    /// <summary>
    /// Default upper bound on instructions per block. Trades JIT compile
    /// time + IR size against dispatch amortisation. 64 is a typical
    /// emulator block size — most basic blocks in real ROMs are 5-30
    /// instructions; the cap mostly affects huge straight-line code
    /// (unrolled loops, BIOS init).
    /// </summary>
    public const int DefaultMaxInstructions = 64;

    private readonly DecoderTable _decoder;
    private readonly InstructionSetSpec _setSpec;
    private readonly uint _instrSizeBytes;

    public BlockDetector(InstructionSetSpec setSpec, DecoderTable decoder)
    {
        if (!setSpec.WidthBits.Fixed.HasValue)
            throw new NotSupportedException(
                $"BlockDetector requires fixed-width instruction set; '{setSpec.Name}' is variable-width.");
        _setSpec        = setSpec;
        _decoder        = decoder;
        _instrSizeBytes = (uint)(setSpec.WidthBits.Fixed.Value / 8);
    }

    /// <summary>The instruction-set this detector is bound to.</summary>
    public string SetName => _setSpec.Name;

    /// <summary>Bytes per instruction in this set.</summary>
    public uint InstrSizeBytes => _instrSizeBytes;

    /// <summary>
    /// Walk memory starting at <paramref name="startPc"/>, decode instructions,
    /// and return a <see cref="Block"/> bounded by the first natural
    /// boundary (or <paramref name="maxInstructions"/>).
    /// </summary>
    public Block Detect(IMemoryBus bus, uint startPc, int maxInstructions = DefaultMaxInstructions)
    {
        if (maxInstructions <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxInstructions), "Must be positive.");

        var instrs = new List<DecodedBlockInstruction>();
        uint pc = startPc;
        BlockEndReason endReason = BlockEndReason.Capped;

        for (int i = 0; i < maxInstructions; i++)
        {
            uint word = ReadInstructionWord(bus, pc);
            var decoded = _decoder.Decode(word);
            if (decoded is null)
            {
                // Undecodable. If we've already collected something, stop
                // here so the executor can run what we have and then
                // hit-and-throw on the bad opcode next dispatch. If this
                // is the very first instruction, still produce a 1-instr
                // block of nothing — caller knows from EndReason to bail.
                if (instrs.Count > 0)
                {
                    endReason = BlockEndReason.Undecodable;
                    break;
                }
                // Synthesise an empty-decode block so callers can check
                // EndReason. We can't put a null DecodedInstruction in
                // the list, so just return early with a degenerate block.
                return new Block(startPc, pc, _setSpec.Name, _instrSizeBytes,
                    Array.Empty<DecodedBlockInstruction>(), BlockEndReason.Undecodable);
            }

            instrs.Add(new DecodedBlockInstruction(pc, word, decoded));
            pc += _instrSizeBytes;

            // Block-end checks on this instruction (POST-add — the boundary
            // instruction IS in the block; control transfer happens AS
            // it executes).
            var def = decoded.Instruction;
            // Only "always" — see class doc; "conditional" / "conditional_via_rd"
            // are too pessimistic to use as boundaries (would split every ALU op).
            if (def.WritesPc == "always")
            {
                endReason = BlockEndReason.WritesPc;
                break;
            }
            if (def.SwitchesInstructionSet)
            {
                endReason = BlockEndReason.SwitchesInstructionSet;
                break;
            }
            if (def.ChangesMode)
            {
                endReason = BlockEndReason.ChangesMode;
                break;
            }
        }

        return new Block(startPc, pc, _setSpec.Name, _instrSizeBytes, instrs, endReason);
    }

    private uint ReadInstructionWord(IMemoryBus bus, uint pc) =>
        _instrSizeBytes switch
        {
            4 => bus.ReadWord(pc),
            2 => bus.ReadHalfword(pc),
            1 => bus.ReadByte(pc),
            _ => throw new NotSupportedException($"instruction size {_instrSizeBytes}-byte unsupported by BlockDetector.")
        };
}
