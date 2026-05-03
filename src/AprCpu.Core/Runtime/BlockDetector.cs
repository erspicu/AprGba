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
    private readonly uint _instrSizeBytes;          // 0 sentinel for variable-width
    private readonly Func<byte, int>? _lengthOracle;// non-null only for variable-width sets

    /// <summary>
    /// Construct a detector for a fixed-width or variable-width instruction
    /// set. When the set is variable-width (LR35902 main + CB), supply a
    /// <paramref name="lengthOracle"/> mapping first-byte → instruction
    /// length in bytes (1, 2, or 3 for LR35902). For LR35902 the canonical
    /// oracle is <see cref="Lr35902InstructionLengths.GetLength"/>.
    ///
    /// Phase 7 GB block-JIT P0.1 — see MD/design/12-gb-block-jit-roadmap.md.
    /// </summary>
    public BlockDetector(InstructionSetSpec setSpec, DecoderTable decoder, Func<byte, int>? lengthOracle = null)
    {
        if (lengthOracle is not null)
        {
            // Variable-width path: explicit per-opcode length lookup. This
            // wins over the spec's `width_bits` field because for sets like
            // LR35902 main, `width_bits: 8` is the FETCH UNIT (1 byte) not
            // the INSTRUCTION LENGTH (1-3 bytes). The oracle is
            // authoritative for total instruction length.
            _instrSizeBytes = 0;        // sentinel
            _lengthOracle   = lengthOracle;
        }
        else if (setSpec.WidthBits.Fixed.HasValue)
        {
            // Fixed-width fast path (ARM=32, Thumb=16, LR35902 CB=8 — all
            // every-instruction-same-width sets).
            _instrSizeBytes = (uint)(setSpec.WidthBits.Fixed.Value / 8);
            _lengthOracle   = null;
        }
        else
        {
            // Variable spec without oracle: refuse. We can't infer length
            // at runtime safely (per Gemini's 2026-05-03 analysis: operand
            // inference creates edge-case bugs).
            throw new ArgumentException(
                $"Variable-width instruction set '{setSpec.Name}' requires a lengthOracle " +
                "(opcode → byte length). For LR35902 use Lr35902InstructionLengths.GetLength.",
                nameof(lengthOracle));
        }
        _setSpec = setSpec;
        _decoder = decoder;
    }

    /// <summary>The instruction-set this detector is bound to.</summary>
    public string SetName => _setSpec.Name;

    /// <summary>
    /// Bytes per instruction in this set when fixed-width; 0 if variable-width
    /// (consult <see cref="DecodedBlockInstruction.LengthBytes"/> per-instruction
    /// instead).
    /// </summary>
    public uint InstrSizeBytes => _instrSizeBytes;

    /// <summary>True if instruction length depends on opcode (LR35902).</summary>
    public bool IsVariableWidth => _lengthOracle is not null;

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
            // 1. Determine this instruction's byte length.
            //    Fixed-width: constant from spec.
            //    Variable-width: peek first byte → length oracle.
            uint thisLength;
            uint word;
            if (_lengthOracle is null)
            {
                // Fixed-width fast path (ARM / Thumb): single bus read of
                // the full instruction word.
                thisLength = _instrSizeBytes;
                word       = ReadFixedWidthWord(bus, pc, _instrSizeBytes);
            }
            else
            {
                // Variable-width sequential crawl (LR35902): fetch first
                // byte, look up length, then pack the full instruction
                // bytes into an LE-packed uint for the emitter (Strategy 2
                // immediate baking, see roadmap §4.3).
                byte first = bus.ReadByte(pc);
                int  lenInt = _lengthOracle(first);
                if (lenInt is < 1 or > 4)
                    throw new InvalidOperationException(
                        $"BlockDetector lengthOracle returned {lenInt} for opcode 0x{first:X2} in set '{_setSpec.Name}'; expected 1..4.");
                thisLength = (uint)lenInt;
                word       = PackVariableWidthBytes(bus, pc, thisLength);
            }

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

            instrs.Add(new DecodedBlockInstruction(pc, word, decoded, (byte)thisLength));
            pc += thisLength;

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

    private static uint ReadFixedWidthWord(IMemoryBus bus, uint pc, uint sizeBytes) =>
        sizeBytes switch
        {
            4 => bus.ReadWord(pc),
            2 => bus.ReadHalfword(pc),
            1 => bus.ReadByte(pc),
            _ => throw new NotSupportedException($"instruction size {sizeBytes}-byte unsupported by BlockDetector.")
        };

    /// <summary>
    /// Pack the consecutive bytes of a variable-width instruction into a
    /// little-endian uint: byte at PC in LSB, byte at PC+1 in next byte
    /// position, etc. 1-byte → just the opcode; 2-byte → opcode | (op2 &lt;&lt; 8);
    /// 3-byte → opcode | (op2 &lt;&lt; 8) | (op3 &lt;&lt; 16). Caller can extract
    /// imm8/imm16 via shift+mask without needing extra bus reads at IR
    /// emission time (Strategy 2 immediate baking).
    /// </summary>
    private static uint PackVariableWidthBytes(IMemoryBus bus, uint pc, uint lengthBytes)
    {
        uint word = 0;
        for (uint i = 0; i < lengthBytes; i++)
            word |= (uint)bus.ReadByte(pc + i) << (int)(i * 8);
        return word;
    }
}
