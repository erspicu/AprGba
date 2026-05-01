using AprCpu.Core.JsonSpec;
using LLVMSharp.Interop;

namespace AprCpu.Core.IR;

/// <summary>
/// LLVM struct layout describing the per-CPU register file passed to every
/// emitted instruction function. Built dynamically from the spec's
/// <see cref="RegisterFile"/> + <see cref="ProcessorModes"/> so the same
/// runtime can target ARM, MIPS, 6502, ... by changing only the JSON.
///
/// Field layout (in struct order):
/// <list type="number">
///   <item>General-purpose registers: <c>count</c> entries of <c>i&lt;width&gt;</c>
///         (currently i32 only; future arches with non-32-bit GPRs are
///         supported by the spec's <c>width_bits</c> field).</item>
///   <item>Status registers, in <see cref="RegisterFile.Status"/> order
///         (CPSR, then SPSR or whatever the architecture defines).</item>
///   <item>Banked GPR groups, in <see cref="ProcessorModes.BankedRegisters"/>
///         order. Each banked group is laid as a flat run of i32 cells.</item>
///   <item>Emulator-internal suffix (always present, not in spec):
///         <c>cycle_counter</c> (i64), <c>pending_exceptions</c> (i32).</item>
/// </list>
///
/// All field offsets are pre-computed in the constructor and exposed via
/// strongly typed accessors (<see cref="GepGpr"/>, <see cref="GepStatusRegister"/>,
/// <see cref="GepBankedGpr"/>, <see cref="GepCycleCounter"/>).
/// </summary>
public sealed unsafe class CpuStateLayout
{
    public LLVMTypeRef    StructType  { get; }
    public LLVMTypeRef    PointerType { get; }
    public LLVMContextRef Context     { get; }

    public RegisterFile               RegisterFile      { get; }
    public ProcessorModes?            ProcessorModes    { get; }
    public IReadOnlyList<ExceptionVector> ExceptionVectors { get; }

    /// <summary>GPR count (from <c>register_file.general_purpose.count</c>).</summary>
    public int GprCount { get; }

    /// <summary>GPR width in bits (from <c>register_file.general_purpose.width_bits</c>).</summary>
    public int GprWidthBits { get; }

    /// <summary>LLVM type used for GPR cells (currently i32 / i64 / i16 / i8).</summary>
    public LLVMTypeRef GprType { get; }

    // Field-index map. GPRs occupy [0, GprCount); status registers and
    // banked groups follow in declaration order.
    private readonly Dictionary<string, int> _statusFieldIndex
        = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _bankedGroupFirstIndex
        = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _bankedGroupSize
        = new(StringComparer.Ordinal);

    public int CycleCounterFieldIndex { get; }
    public int PendingExceptionsFieldIndex { get; }

    public CpuStateLayout(
        LLVMContextRef context,
        RegisterFile registerFile,
        ProcessorModes? processorModes,
        IReadOnlyList<ExceptionVector>? exceptionVectors = null)
    {
        Context          = context;
        RegisterFile     = registerFile;
        ProcessorModes   = processorModes;
        ExceptionVectors = exceptionVectors ?? Array.Empty<ExceptionVector>();

        GprCount     = registerFile.GeneralPurpose.Count;
        GprWidthBits = registerFile.GeneralPurpose.WidthBits;
        GprType      = LlvmIntTypeForBits(GprWidthBits);

        var elements = new List<LLVMTypeRef>(64);

        // 1. GPRs
        for (int i = 0; i < GprCount; i++)
            elements.Add(GprType);

        // 2. Status registers
        foreach (var status in registerFile.Status)
        {
            _statusFieldIndex[status.Name] = elements.Count;
            elements.Add(LlvmIntTypeForBits(status.WidthBits));
        }

        // 3. Banked GPR groups (one cell per banked register listed in spec)
        if (processorModes is not null)
        {
            foreach (var (mode, banked) in processorModes.BankedRegisters)
            {
                if (banked.Count == 0) continue;
                _bankedGroupFirstIndex[mode] = elements.Count;
                _bankedGroupSize[mode]       = banked.Count;
                for (int i = 0; i < banked.Count; i++)
                    elements.Add(GprType);
            }
        }

        // 4. Emulator suffix
        CycleCounterFieldIndex      = elements.Count;
        elements.Add(LLVMTypeRef.Int64);
        PendingExceptionsFieldIndex = elements.Count;
        elements.Add(LLVMTypeRef.Int32);

        StructType  = LLVMTypeRef.CreateStruct(elements.ToArray(), Packed: false);
        PointerType = LLVMTypeRef.CreatePointer(StructType, 0);
    }

    /// <summary>GEP into a fixed GPR slot (compile-time known index).</summary>
    public LLVMValueRef GepGpr(LLVMBuilderRef builder, LLVMValueRef statePtr, int regIndex)
    {
        if (regIndex < 0 || regIndex >= GprCount)
            throw new ArgumentOutOfRangeException(nameof(regIndex),
                $"GPR index {regIndex} out of range [0, {GprCount}).");
        return BuildGep(builder, statePtr, regIndex, $"r{regIndex}_ptr");
    }

    /// <summary>GEP into the named status register (e.g. "CPSR", "SPSR").</summary>
    public LLVMValueRef GepStatusRegister(LLVMBuilderRef builder, LLVMValueRef statePtr, string name)
    {
        if (!_statusFieldIndex.TryGetValue(name, out var idx))
            throw new InvalidOperationException(
                $"Status register '{name}' is not declared in register_file.status.");
        return BuildGep(builder, statePtr, idx, $"{name.ToLowerInvariant()}_ptr");
    }

    /// <summary>GEP into a banked GPR slot for a given processor mode.</summary>
    public LLVMValueRef GepBankedGpr(LLVMBuilderRef builder, LLVMValueRef statePtr, string mode, int idxInGroup)
    {
        if (!_bankedGroupFirstIndex.TryGetValue(mode, out var first))
            throw new InvalidOperationException(
                $"Mode '{mode}' has no banked GPR group declared in processor_modes.banked_registers.");
        var size = _bankedGroupSize[mode];
        if (idxInGroup < 0 || idxInGroup >= size)
            throw new ArgumentOutOfRangeException(nameof(idxInGroup),
                $"Banked GPR index {idxInGroup} out of range [0, {size}) for mode '{mode}'.");
        return BuildGep(builder, statePtr, first + idxInGroup, $"r_{mode.ToLowerInvariant()}_{idxInGroup}_ptr");
    }

    /// <summary>GEP into the cycle counter slot (i64).</summary>
    public LLVMValueRef GepCycleCounter(LLVMBuilderRef builder, LLVMValueRef statePtr)
        => BuildGep(builder, statePtr, CycleCounterFieldIndex, "cycle_ptr");

    /// <summary>GEP into the pending-exceptions bitmask slot (i32).</summary>
    public LLVMValueRef GepPendingExceptions(LLVMBuilderRef builder, LLVMValueRef statePtr)
        => BuildGep(builder, statePtr, PendingExceptionsFieldIndex, "pending_exc_ptr");

    /// <summary>
    /// GEP into a GPR slot by a runtime-computed index. Bitcasts the state
    /// pointer to a flat i32 pointer; valid only when GPRs are first in the
    /// struct and stored as i32, which the constructor enforces (other widths
    /// would need a per-element GEP with the right element type).
    /// </summary>
    public LLVMValueRef GepGprDynamic(LLVMBuilderRef builder, LLVMValueRef statePtr, LLVMValueRef regIdx)
    {
        if (GprWidthBits != 32)
            throw new NotSupportedException(
                $"GepGprDynamic only supports 32-bit GPRs (got {GprWidthBits}-bit).");
        var i32Ptr = LLVMTypeRef.CreatePointer(LLVMTypeRef.Int32, 0);
        var asI32Ptr = builder.BuildBitCast(statePtr, i32Ptr, "state_as_i32p");
        return builder.BuildGEP2(LLVMTypeRef.Int32, asI32Ptr, new[] { regIdx }, "rdyn_ptr");
    }

    /// <summary>Lookup the spec definition of a status register (e.g. CPSR field positions).</summary>
    public StatusRegister GetStatusRegisterDef(string name)
    {
        foreach (var s in RegisterFile.Status)
            if (s.Name == name) return s;
        throw new InvalidOperationException($"Status register '{name}' not declared in spec.");
    }

    /// <summary>
    /// Convenience accessor for the bit position of a flag inside a status
    /// register. Used by R2 (flag emitters) to avoid hardcoding CPSR.N=31 etc.
    /// </summary>
    public int GetStatusFlagBitIndex(string statusRegister, string flagName)
    {
        var status = GetStatusRegisterDef(statusRegister);
        if (!status.Fields.TryGetValue(flagName, out var range))
            throw new InvalidOperationException(
                $"Status register '{statusRegister}' has no flag '{flagName}' (declared fields: {string.Join(",", status.Fields.Keys)}).");
        if (range.Width != 1)
            throw new InvalidOperationException(
                $"Flag '{flagName}' on '{statusRegister}' is {range.Width} bits wide; expected 1.");
        return range.Low;
    }

    // ----- internal helpers -----

    private LLVMValueRef BuildGep(LLVMBuilderRef builder, LLVMValueRef statePtr, int fieldIndex, string name)
    {
        var i32 = LLVMTypeRef.Int32;
        var indices = new[]
        {
            LLVMValueRef.CreateConstInt(i32, 0,                       SignExtend: false),
            LLVMValueRef.CreateConstInt(i32, (ulong)(uint)fieldIndex, SignExtend: false),
        };
        return builder.BuildGEP2(StructType, statePtr, indices, name);
    }

    private static LLVMTypeRef LlvmIntTypeForBits(int bits) => bits switch
    {
        8  => LLVMTypeRef.Int8,
        16 => LLVMTypeRef.Int16,
        32 => LLVMTypeRef.Int32,
        64 => LLVMTypeRef.Int64,
        _  => throw new NotSupportedException($"Unsupported register width {bits}-bit."),
    };

}
