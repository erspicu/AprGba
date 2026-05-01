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
    IReadOnlyList<CustomMicroOp> CustomMicroOps);

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
    int WordSizeBits);

public sealed record CpuVariant(
    string Id,
    string? Core,
    IReadOnlyList<string> Features,
    string? Notes);

#endregion

#region Register file & modes

public sealed record RegisterFile(
    GeneralPurposeRegisters GeneralPurpose,
    IReadOnlyList<StatusRegister> Status);

public sealed record GeneralPurposeRegisters(
    int Count,
    int WidthBits,
    IReadOnlyList<string> Names,
    IReadOnlyDictionary<string, string> Aliases,
    int? PcIndex);

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
    IReadOnlyList<MicroOpStep> Steps);

public sealed record InstructionSelector(string Field, string Value)
{
    /// <summary>Decode the JSON value (binary string or integer) into a uint.</summary>
    public uint NumericValue =>
        Value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? Convert.ToUInt32(Value.Substring(2), 16)
        : Value.All(c => c is '0' or '1')
            ? Convert.ToUInt32(Value, 2)
        : uint.Parse(Value);
}

public sealed record Cycles(
    string? Form,
    IReadOnlyList<string> FormAlt,
    string? ExtraWhenDestPc,
    string? ExtraWhenLoadPc,
    string? ComputedAt);

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
