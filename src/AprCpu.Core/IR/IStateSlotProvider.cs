using LLVMSharp.Interop;

namespace AprCpu.Core.IR;

/// <summary>
/// N1.B' — abstraction over "where does the IR get the pointer for this
/// state slot?". Two production implementations:
///
/// <list type="bullet">
///   <item><see cref="StateBufferProvider"/> — per-instr default; returns
///         a GEP into the state-struct pointer (live memory).</item>
///   <item><see cref="AllocaSlotProvider"/> — block-JIT mode; returns a
///         pointer to a function-entry alloca. <see cref="BlockFunctionBuilder"/>
///         pre-loads each alloca from the state buffer at block prologue
///         and writes back at block exit (and at any mid-block sync
///         point). LLVM's mem2reg pass promotes those alloca + load/store
///         chains to SSA values, so emitter-issued stores/loads to the
///         "PC slot" become register-level operations that LLVM can CSE
///         across instructions and skip altogether for dead values.</item>
/// </list>
///
/// Spec emitters call <see cref="EmitContext.GepGpr"/> /
/// <see cref="EmitContext.GepStatusRegister"/>; those route through the
/// active provider. Per-instr code is unchanged because StateBufferProvider
/// emits exactly the same GEP that <see cref="CpuStateLayout.GepGpr"/>
/// always emitted.
///
/// See <c>MD/design/18-block-jit-state-abstraction.md</c>.
/// </summary>
public interface IStateSlotProvider
{
    /// <summary>Pointer to the GPR-<paramref name="index"/> slot.</summary>
    LLVMValueRef GprPtr(int index);

    /// <summary>
    /// Pointer to a named status register slot. <paramref name="mode"/>
    /// is non-null only for banked status registers (ARM SPSR_FIQ etc.).
    /// </summary>
    LLVMValueRef StatusRegPtr(string name, string? mode = null);

    /// <summary>
    /// Pointer to a banked GPR slot. Used by ARM mode-banked register
    /// access; not relevant for NES/GB.
    /// </summary>
    LLVMValueRef BankedGprPtr(string mode, int idxInGroup);

    /// <summary>
    /// Emit IR that flushes any pending in-register slot values back to
    /// the state buffer. <see cref="StateBufferProvider"/> is a no-op
    /// (writes already went to live memory). <see cref="AllocaSlotProvider"/>
    /// loads each tracked alloca and stores it to the corresponding state
    /// slot. Called by <see cref="BlockFunctionBuilder"/> at block exit
    /// and by sync-emitter mid-block ret paths.
    /// </summary>
    void SyncToState();
}

/// <summary>
/// Default per-instr provider — every slot access is a direct GEP into
/// the state-struct pointer carried by <see cref="EmitContext.StatePtr"/>.
/// IR shape is identical to the pre-N1.B' framework: per-instruction
/// functions in the cached module, and any block-JIT module that opts out
/// of the alloca path, all see the same load/store-via-GEP IR they did
/// before this refactor.
/// </summary>
public sealed class StateBufferProvider : IStateSlotProvider
{
    private readonly LLVMBuilderRef _builder;
    private readonly LLVMValueRef _statePtr;
    private readonly CpuStateLayout _layout;

    public StateBufferProvider(LLVMBuilderRef builder, LLVMValueRef statePtr, CpuStateLayout layout)
    {
        _builder = builder;
        _statePtr = statePtr;
        _layout = layout;
    }

    public LLVMValueRef GprPtr(int index)
        => _layout.GepGpr(_builder, _statePtr, index);

    public LLVMValueRef StatusRegPtr(string name, string? mode = null)
        => _layout.GepStatusRegister(_builder, _statePtr, name, mode);

    public LLVMValueRef BankedGprPtr(string mode, int idxInGroup)
        => _layout.GepBankedGpr(_builder, _statePtr, mode, idxInGroup);

    public void SyncToState() { /* nothing to do — writes hit live state already. */ }
}

/// <summary>
/// Block-JIT provider — every slot access returns a pointer to an
/// <c>alloca</c> registered at function entry. Mem2reg promotes the
/// alloca + load/store chains to pure SSA values that the optimiser can
/// freely CSE / fold across instruction boundaries. At block exit (and
/// at any mid-block sync point), <see cref="SyncToState"/> emits the
/// alloca → state-buffer write-back so the host C# loop and any
/// subsequent block invocation see the latest values.
///
/// Slots not registered (e.g. emulator-internal cycle counter) fall
/// through to direct state-buffer GEP via the <paramref name="fallback"/>
/// provider passed to the constructor — typically a
/// <see cref="StateBufferProvider"/>. This lets emitters access the
/// non-shadowable state fields (PcWritten flag, cycles_left budget,
/// pending exceptions) without per-CPU configuration.
/// </summary>
public sealed class AllocaSlotProvider : IStateSlotProvider
{
    private readonly LLVMBuilderRef _builder;
    private readonly LLVMValueRef _statePtr;
    private readonly CpuStateLayout _layout;
    private readonly IStateSlotProvider _fallback;

    // Slot maps. Keyed by the slot's identifier within its category:
    //   GPR        — by GPR index
    //   Status     — by (name, mode)
    //   BankedGpr  — by (mode, idxInGroup)
    private readonly Dictionary<int, LLVMValueRef> _gprSlots = new();
    private readonly Dictionary<(string Name, string? Mode), LLVMValueRef> _statusSlots
        = new();
    private readonly Dictionary<(string Mode, int Idx), LLVMValueRef> _bankedSlots
        = new();

    // Type metadata for sync write-back. Mirrors the slot maps so we
    // know what LLVMTypeRef to load/store with.
    private readonly Dictionary<int, LLVMTypeRef> _gprTypes = new();
    private readonly Dictionary<(string Name, string? Mode), LLVMTypeRef> _statusTypes
        = new();
    private readonly Dictionary<(string Mode, int Idx), LLVMTypeRef> _bankedTypes
        = new();

    public AllocaSlotProvider(
        LLVMBuilderRef builder,
        LLVMValueRef statePtr,
        CpuStateLayout layout,
        IStateSlotProvider fallback)
    {
        _builder = builder;
        _statePtr = statePtr;
        _layout = layout;
        _fallback = fallback;
    }

    /// <summary>
    /// Allocate, pre-load, and register an alloca for GPR <paramref name="index"/>.
    /// Should be called from the function's entry block before any in-block
    /// code emits (mem2reg requires entry-block-only allocas to promote).
    /// </summary>
    public void RegisterGpr(int index)
    {
        var statePtr = _layout.GepGpr(_builder, _statePtr, index);
        var alloca = _builder.BuildAlloca(_layout.GprType, $"r{index}_local");
        var initial = _builder.BuildLoad2(_layout.GprType, statePtr, $"r{index}_init");
        _builder.BuildStore(initial, alloca);
        _gprSlots[index] = alloca;
        _gprTypes[index] = _layout.GprType;
    }

    /// <summary>
    /// Allocate, pre-load, and register an alloca for the named status
    /// register slot. <paramref name="mode"/> is non-null only for banked
    /// status registers.
    /// </summary>
    public void RegisterStatus(string name, string? mode = null)
    {
        var def = _layout.GetStatusRegisterDef(name);
        var t = def.WidthBits switch
        {
            8 => LLVMTypeRef.Int8,
            16 => LLVMTypeRef.Int16,
            32 => LLVMTypeRef.Int32,
            _ => throw new NotSupportedException(
                $"AllocaSlotProvider: status reg {name} width {def.WidthBits} unsupported.")
        };
        var statePtr = _layout.GepStatusRegister(_builder, _statePtr, name, mode);
        var label = mode is null ? name.ToLowerInvariant() : $"{name.ToLowerInvariant()}_{mode.ToLowerInvariant()}";
        var alloca = _builder.BuildAlloca(t, $"{label}_local");
        var initial = _builder.BuildLoad2(t, statePtr, $"{label}_init");
        _builder.BuildStore(initial, alloca);
        _statusSlots[(name, mode)] = alloca;
        _statusTypes[(name, mode)] = t;
    }

    public LLVMValueRef GprPtr(int index)
        => _gprSlots.TryGetValue(index, out var s) ? s : _fallback.GprPtr(index);

    public LLVMValueRef StatusRegPtr(string name, string? mode = null)
        => _statusSlots.TryGetValue((name, mode), out var s)
            ? s
            : _fallback.StatusRegPtr(name, mode);

    public LLVMValueRef BankedGprPtr(string mode, int idxInGroup)
        => _bankedSlots.TryGetValue((mode, idxInGroup), out var s)
            ? s
            : _fallback.BankedGprPtr(mode, idxInGroup);

    /// <summary>
    /// True when the given slot is alloca-backed (i.e. registered). Used
    /// by <see cref="BlockFunctionBuilder"/>'s budget-exit PC writer to
    /// route the explicit "next-PC" store through the alloca rather than
    /// directly into the state buffer (otherwise the alloca's stale value
    /// would be drained over the explicit write at block exit).
    /// </summary>
    public bool HasGpr(int index) => _gprSlots.ContainsKey(index);

    /// <summary>True when the given status reg slot is alloca-backed.</summary>
    public bool HasStatus(string name, string? mode = null)
        => _statusSlots.ContainsKey((name, mode));

    public void SyncToState()
    {
        // Drain in stable order for IR-diff stability:
        //   GPRs by index ascending, then status by (name, mode) ascending.
        foreach (var idx in _gprSlots.Keys.OrderBy(k => k))
        {
            var alloca = _gprSlots[idx];
            var t = _gprTypes[idx];
            var statePtr = _layout.GepGpr(_builder, _statePtr, idx);
            var v = _builder.BuildLoad2(t, alloca, $"r{idx}_drain");
            _builder.BuildStore(v, statePtr);
        }
        foreach (var key in _statusSlots.Keys
            .OrderBy(k => k.Name, StringComparer.Ordinal)
            .ThenBy(k => k.Mode ?? string.Empty, StringComparer.Ordinal))
        {
            var alloca = _statusSlots[key];
            var t = _statusTypes[key];
            var statePtr = _layout.GepStatusRegister(_builder, _statePtr, key.Name, key.Mode);
            var label = key.Mode is null
                ? key.Name.ToLowerInvariant()
                : $"{key.Name.ToLowerInvariant()}_{key.Mode.ToLowerInvariant()}";
            var v = _builder.BuildLoad2(t, alloca, $"{label}_drain");
            _builder.BuildStore(v, statePtr);
        }
    }
}
