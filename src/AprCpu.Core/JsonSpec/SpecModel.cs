using System.Text.Json;

namespace AprCpu.Core.JsonSpec;

// POCO model mirroring the JSON schema in spec/schema/cpu-spec.schema.json.
// Records are immutable post-load; SpecLoader is the only writer.

#region Top-level files

/// <summary>Loaded `cpu.json` (CPU-model file).</summary>
public sealed record CpuSpec(
    string SpecVersion,
    Architecture Architecture,
    IReadOnlyList<CpuVariant> Variants,
    RegisterFile RegisterFile,
    ProcessorModes? ProcessorModes,
    IReadOnlyList<ExceptionVector> ExceptionVectors,
    IReadOnlyList<InstructionSetRef> InstructionSets,
    InstructionSetDispatch? InstructionSetDispatch,
    MemoryModel? MemoryModel,
    IReadOnlyList<CustomMicroOp> CustomMicroOps,
    // N2.5 — declarative block-JIT policy hints. Optional; null defaults
    // to framework-baseline behaviour (cycles_per_spec_unit=4 m-cycle×4
    // GB/ARM convention, lazy PC, end-of-block IRQ check). See
    // MD/design/19-declarative-jit-policy.md.
    IsaMetadata? IsaMetadata = null,
    // 25.1 — child spec's diff against its parent's instruction sets.
    // Only meaningful when Architecture.Extends is non-null. Resolved at
    // load time via SpecLoader's recursive parent resolution + JSON Merge
    // Patch on overrides. Null when this is a base spec.
    InstructionSetDiff? InstructionSetDiff = null);

/// <summary>Loaded instruction-set file (e.g. `arm.json`, `thumb.json`).</summary>
public sealed record InstructionSetSpec(
    string SpecVersion,
    string Name,
    InstructionWidth WidthBits,
    int AlignmentBytes,
    int PcOffsetBytes,
    string EndianWithinWord,
    GlobalCondition? GlobalCondition,
    string DecodeStrategy,
    WidthDecision? WidthDecision,
    IReadOnlyList<EncodingGroup> EncodingGroups,
    InstructionSetExtends? Extends,
    IReadOnlyList<CustomMicroOp> CustomMicroOps);

#endregion

#region Architecture / variants

public sealed record Architecture(
    string Id,
    string Family,
    string? Extends,
    string Endianness,
    int WordSizeBits,
    // 25.1 — relative path to parent cpu.json. Required when Extends is
    // non-null. Resolved against the current spec file's directory.
    // Example: i80186/cpu.json sets ExtendsPath = "../i8086/cpu.json".
    // Null when Extends is null (base spec, no inheritance).
    string? ExtendsPath = null);

public sealed record CpuVariant(
    string Id,
    string? Core,
    IReadOnlyList<string> Features,
    string? Notes);

#endregion

#region Register file & modes

public sealed record RegisterFile(
    GeneralPurposeRegisters GeneralPurpose,
    IReadOnlyList<StatusRegister> Status,
    IReadOnlyList<RegisterPair> RegisterPairs,
    StackPointerRef? StackPointer = null);

/// <summary>
/// Where the stack pointer lives. <see cref="GprIndex"/> for arches
/// where SP is one of the GPRs (ARM R13, MIPS $sp); <see cref="StatusName"/>
/// for arches where SP is a separate addressable register (LR35902 SP,
/// 6502 S, M68k USP/SSP). Width comes from the underlying register.
/// Used by generic <c>push</c>/<c>pop</c>/<c>call</c>/<c>ret</c>
/// emitters so they don't need a per-arch C# implementation.
/// </summary>
public sealed record StackPointerRef(int? GprIndex, string? StatusName);

public sealed record GeneralPurposeRegisters(
    int Count,
    int WidthBits,
    IReadOnlyList<string> Names,
    IReadOnlyDictionary<string, string> Aliases,
    int? PcIndex);

/// <summary>
/// Two 8-bit GPRs that can also be addressed as a single 16-bit value
/// (LR35902/Z80-style: BC, DE, HL, AF). The pair name is read/written
/// via <c>read_reg_pair</c>/<c>write_reg_pair</c> micro-ops; <c>High</c>
/// supplies the upper 8 bits and <c>Low</c> the lower 8 bits.
/// </summary>
public sealed record RegisterPair(string Name, string High, string Low);

public sealed record StatusRegister(
    string Name,
    int WidthBits,
    IReadOnlyDictionary<string, BitRange> Fields,
    IReadOnlyList<string> BankedPerMode);

public sealed record ProcessorModes(
    IReadOnlyList<ProcessorMode> Modes,
    IReadOnlyDictionary<string, IReadOnlyList<string>> BankedRegisters);

public sealed record ProcessorMode(
    string Id,
    string? Encoding,
    bool Privileged);

public sealed record ExceptionVector(
    string Name,
    uint Address,
    string? EnterMode,
    IReadOnlyList<string> DisableFlags);

#endregion

#region Memory + dispatch

public sealed record InstructionSetRef(
    string Name,
    string File,
    InstructionSetExtends? Extends);

public sealed record InstructionSetExtends(
    string Spec,
    string Set);

/// <summary>
/// 25.1 — declarative diff that a child CPU spec applies to its parent's
/// instruction sets. Per RFC 7386 (JSON Merge Patch) at the per-instruction
/// level — child overrides only declare the fields that change, the rest
/// inherits from parent.
/// </summary>
public sealed record InstructionSetDiff(
    /// <summary>
    /// Per instruction-set name (e.g. "Main"), the diff to apply.
    /// </summary>
    IReadOnlyDictionary<string, PerSetDiff> PerSet);

/// <summary>
/// Diff for one named instruction set inside an <see cref="InstructionSetDiff"/>.
/// </summary>
public sealed record PerSetDiff(
    /// <summary>
    /// New instructions to add. Each must carry a unique ID not present in
    /// the parent's set. The element shape is the raw <c>instructions[]</c>
    /// JSON used elsewhere — same parser path applied at merge time.
    /// </summary>
    IReadOnlyList<JsonElement> AdditionsRaw,
    /// <summary>
    /// Map of instruction ID → partial-instruction patch. The ID must
    /// exist in the parent; the patch is applied via JSON Merge Patch
    /// (RFC 7386). Throws if ID not found in parent.
    /// </summary>
    IReadOnlyDictionary<string, JsonElement> Overrides,
    /// <summary>
    /// IDs to remove from the parent's set. Each must exist in the parent.
    /// </summary>
    IReadOnlyList<string> Removals);

public sealed record InstructionSetDispatch(
    string Selector,
    IReadOnlyDictionary<string, string> SelectorValues,
    IReadOnlyList<string> SwitchVia,
    string? TransitionRule);

public sealed record MemoryModel(
    string DefaultEndianness,
    AlignmentPolicy? AlignmentPolicy);

public sealed record AlignmentPolicy(
    string LoadUnaligned,
    string StoreUnaligned);

#endregion

#region Instruction-set internals

/// <summary>Discriminated representation of <c>width_bits</c> (integer or "variable").</summary>
public readonly record struct InstructionWidth(int? Fixed, bool IsVariable)
{
    public static InstructionWidth OfFixed(int bits) => new(bits, false);
    public static InstructionWidth Variable() => new(null, true);
    public override string ToString() => IsVariable ? "variable" : Fixed!.Value.ToString();
}

public sealed record GlobalCondition(
    BitRange Field,
    IReadOnlyDictionary<string, string> Table,
    string AppliesTo);

public sealed record WidthDecision(
    int FirstUnitBits,
    WidthDecisionRule Rule);

public sealed record WidthDecisionRule(
    string Field,
    IReadOnlyList<string> LongWhenIn,
    int LongTotalBits);

public sealed record EncodingGroup(
    string Name,
    string? AppliesWhen,
    IReadOnlyList<EncodingFormat> Formats);

public sealed record EncodingFormat(
    string Name,
    string? Comment,
    string? Pattern,
    IReadOnlyDictionary<string, BitRange> Fields,
    uint Mask,
    uint Match,
    IReadOnlyDictionary<string, OperandResolver> Operands,
    IReadOnlyList<InstructionDef> Instructions);

public sealed record OperandResolver(
    string Kind,
    IReadOnlyList<string> Outputs,
    JsonElement Raw); // Kind-specific extra fields preserved here.

public sealed record InstructionDef(
    InstructionSelector? Selector,
    string Mnemonic,
    string? Since,
    string? Until,
    string? RequiresFeature,
    bool Unconditional,
    string? WritesPc,
    IReadOnlyList<string> WritesMemory,
    bool ChangesMode,
    bool SwitchesInstructionSet,
    bool RequiresIoBarrier,
    IReadOnlyList<string> Quirks,
    string? ManualRef,
    Cycles? Cycles,
    IReadOnlyList<MicroOpStep> Steps,
    // 25.1 — stable ID for spec inheritance / override. Optional during
    // bootstrap (existing 8086 spec retrofitted in sprint 25.2). Once
    // retrofitted, IDs must be unique within an instruction set, and
    // `instruction_set_diff.overrides` / `removals` reference instructions
    // by ID. Convention: <MNEMONIC>_<OPERAND_SHAPE> (see
    // MD/design/25.2-instruction-id-conventions.md).
    string? Id = null,
    // 25.1.4 — provenance: which CPU spec originally defined this
    // instruction (set on first load) and which CPU spec last overrode
    // it (set when an inheritance override applies). Both null on the
    // base spec; `OverriddenBy` is set even on a no-op override (for
    // audit). Currently informational; spec dump tools surface this.
    string? OriginCpu = null,
    string? OverriddenBy = null);

public sealed record InstructionSelector(string Field, string Value)
{
    /// <summary>Decode the JSON value (binary string or integer) into a uint.
    /// Accepts: "0xFF" hex, "0b1010" binary-with-prefix, "1010" plain binary,
    /// or plain decimal.</summary>
    public uint NumericValue
    {
        get
        {
            if (Value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return Convert.ToUInt32(Value.Substring(2), 16);
            if (Value.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
                return Convert.ToUInt32(Value.Substring(2), 2);
            if (Value.Length > 0 && Value.All(c => c is '0' or '1'))
                return Convert.ToUInt32(Value, 2);
            return uint.Parse(Value);
        }
    }
}

public sealed record Cycles(
    string? Form,
    IReadOnlyList<string> FormAlt,
    string? ExtraWhenDestPc,
    string? ExtraWhenLoadPc,
    string? ComputedAt,
    // N3.3 — per-(mnemonic, addressing-mode) cycle granularity. The `Form`
    // field gives a coarse default; `Table` maps a decoder field's bit
    // pattern to an exact cycle count, breaking the 1D limitation that
    // motivated the original BLOCKED status of N3.3.
    CycleTable? Table = null,
    // N9 — dynamic cycle penalties encoded declaratively. Currently
    // runtime IR steps (mos_branch_rel, mos_load_operand_*) handle
    // these inline; spec metadata is a declarative source-of-truth so
    // future runtime can read penalty values from spec instead of
    // hardcoding them.
    int? ExtraWhenTaken = null,             // conditional branch taken — 6502 branches: +1
    int? ExtraWhenPageCross = null);        // load addressing-mode page boundary — 6502 read abs,X / abs,Y / (zp),Y: +1

/// <summary>
/// N3.3 — selector-driven cycle count map.
/// <c>Field</c> names a decoder field declared on the instruction's
/// <see cref="EncodingFormat"/> (e.g. "bbb" for the cc=01 6502 ALU
/// addressing mode selector). <c>Values</c> maps each bit pattern (as a
/// binary or hex string, matching the same convention as
/// <see cref="InstructionSelector.Value"/>) to its cycle count.
/// </summary>
public sealed record CycleTable(
    string Field,
    IReadOnlyDictionary<string, int> Values)
{
    /// <summary>
    /// Look up the cycle count for <paramref name="opcode"/> via the format's
    /// field bits. Returns null if the field isn't declared on the format,
    /// or if the extracted value isn't covered by <see cref="Values"/>.
    /// </summary>
    public int? Resolve(EncodingFormat format, uint opcode)
    {
        if (!format.Fields.TryGetValue(Field, out var range)) return null;
        int width = range.High - range.Low + 1;
        if (width <= 0 || width > 32) return null;
        uint mask = width == 32 ? 0xFFFFFFFFu : (1u << width) - 1u;
        uint value = (opcode >> range.Low) & mask;

        // Try binary-padded string first (matches selector convention),
        // then hex with "0x" prefix, then plain decimal.
        var binKey = System.Convert.ToString(value, 2).PadLeft(width, '0');
        if (Values.TryGetValue(binKey, out var bin)) return bin;
        var hexKey = "0x" + value.ToString("X");
        if (Values.TryGetValue(hexKey, out var hex)) return hex;
        var decKey = value.ToString();
        if (Values.TryGetValue(decKey, out var dec)) return dec;
        return null;
    }
}

#endregion

#region Micro-op step (kept open-ended)

/// <summary>
/// A single step within an instruction's `steps[]`. Op-specific arguments
/// are kept in <see cref="Raw"/>; emitters parse what they need.
/// </summary>
public sealed record MicroOpStep(
    string Op,
    JsonElement Raw);

#endregion

#region Custom micro-ops

public sealed record CustomMicroOp(
    string Name,
    IReadOnlyList<CustomMicroOpPort> Inputs,
    IReadOnlyList<CustomMicroOpPort> Outputs,
    string? Summary,
    string? ImplementationHint);

public sealed record CustomMicroOpPort(string Name, int? Width);

#endregion

#region BitRange helper

/// <summary>
/// Inclusive bit range high:low. Width = high - low + 1.
/// Used both for encoding-format field extraction and for status-register
/// flag positions.
/// </summary>
public readonly record struct BitRange(int High, int Low)
{
    public int Width => High - Low + 1;

    /// <summary>Mask of the field's width, lowered to bit 0 (e.g. 4 bits → 0x0F).</summary>
    public uint LowMask => Width >= 32 ? 0xFFFFFFFFu : (1u << Width) - 1u;

    /// <summary>Mask of the field at its in-instruction position.</summary>
    public uint InPlaceMask => LowMask << Low;

    public override string ToString() =>
        High == Low ? $"{High}" : $"{High}:{Low}";

    /// <summary>Parse "31:28" or "5".</summary>
    public static BitRange Parse(string s)
    {
        var trimmed = s.Trim();
        var colonIdx = trimmed.IndexOf(':');
        if (colonIdx < 0)
        {
            var single = int.Parse(trimmed);
            return new BitRange(single, single);
        }
        var hi = int.Parse(trimmed[..colonIdx]);
        var lo = int.Parse(trimmed[(colonIdx + 1)..]);
        if (hi < lo) throw new FormatException($"BitRange '{s}': high {hi} < low {lo}");
        return new BitRange(hi, lo);
    }
}

#endregion

#region IsaMetadata — declarative block-JIT policy

/// <summary>
/// N2.5 — block-JIT optimization hints from <c>cpu.json::isa_metadata</c>.
/// Read by HostRuntime / BlockFunctionBuilder to configure framework
/// behaviour without per-CPU host-class hardcoding. See
/// <c>MD/design/19-declarative-jit-policy.md</c>.
/// </summary>
public sealed record IsaMetadata(
    string? Endianness,
    int CyclesPerSpecUnit,
    string PcUpdatePolicy,
    string InterruptCheckPolicy);

#endregion
