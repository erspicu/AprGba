using AprCpu.Core.IR;
using LLVMSharp.Interop;

namespace AprCpu.Core.Runtime;

/// <summary>
/// Phase 3.1.a — minimal host runtime that JIT-compiles a spec module
/// and exposes raw function pointers to the caller. The caller supplies
/// a byte buffer matching the LLVM struct layout of <see cref="CpuStateLayout"/>;
/// no architecture-specific C# struct is required (so the same host code
/// can drive ARM7TDMI, GB LR35902, etc.).
///
/// MCJIT is used because LLVMSharp.Interop's MCJIT bindings are stable
/// across LLVM 17/18/20, while the ORC C API surface has shifted between
/// versions. Tier-up to ORC LLJIT can come later if needed.
/// </summary>
public sealed unsafe class HostRuntime : IDisposable
{
    private static bool _jitInitialized;
    private static readonly object _initLock = new();

    private readonly LLVMExecutionEngineRef _engine;
    private readonly LLVMTargetDataRef       _targetData;
    private bool _disposed;

    public CpuStateLayout Layout { get; }

    /// <summary>Total byte size of the CPU-state struct.</summary>
    public ulong StateSizeBytes { get; }

    private HostRuntime(LLVMExecutionEngineRef engine, LLVMTargetDataRef targetData, CpuStateLayout layout)
    {
        _engine = engine;
        _targetData = targetData;
        Layout = layout;
        StateSizeBytes = LLVM.SizeOfTypeInBits(targetData, layout.StructType) / 8;
    }

    /// <summary>
    /// JIT-compile <paramref name="module"/> and bundle it with the
    /// <paramref name="layout"/> the caller used to emit it.
    /// The module is consumed by the JIT engine and must not be reused
    /// for further IR additions after this call.
    /// </summary>
    public static HostRuntime Create(LLVMModuleRef module, CpuStateLayout layout)
    {
        EnsureJitInitialized();
        var engine = module.CreateMCJITCompiler();
        var targetData = LLVM.GetExecutionEngineTargetData(engine);
        return new HostRuntime(engine, targetData, layout);
    }

    /// <summary>Look up the native entry point of a JIT'd function by its IR name.</summary>
    public IntPtr GetFunctionPointer(string functionName)
    {
        var addr = _engine.GetFunctionAddress(functionName);
        if (addr == 0)
            throw new InvalidOperationException(
                $"MCJIT could not resolve function '{functionName}'. " +
                "Either the name is wrong, or the function references an unbound extern.");
        return (IntPtr)addr;
    }

    /// <summary>Byte offset of a struct field within the CPU-state buffer.</summary>
    public ulong GetFieldOffsetBytes(int fieldIndex)
        => LLVM.OffsetOfElement(_targetData, Layout.StructType, (uint)fieldIndex);

    /// <summary>Byte offset of GPR <paramref name="regIndex"/>.</summary>
    public ulong GprOffset(int regIndex)
        => GetFieldOffsetBytes(Layout.GprFieldIndex(regIndex));

    /// <summary>Byte offset of a status register slot. <c>mode=null</c> for non-banked (CPSR).</summary>
    public ulong StatusOffset(string name, string? mode = null)
        => GetFieldOffsetBytes(Layout.StatusFieldIndex(name, mode));

    /// <summary>Byte offset of a banked GPR slot.</summary>
    public ulong BankedGprOffset(string mode, int idxInGroup)
        => GetFieldOffsetBytes(Layout.BankedGprFieldIndex(mode, idxInGroup));

    /// <summary>Byte offset of the cycle-counter (i64).</summary>
    public ulong CycleCounterOffset
        => GetFieldOffsetBytes(Layout.CycleCounterFieldIndex);

    /// <summary>
    /// Bind a native function pointer to an extern symbol declared in the
    /// IR (e.g. <c>memory_read_8</c>, <c>host_swap_register_bank</c>).
    /// The IR must have declared the symbol via <c>module.AddFunction(name, ...)</c>
    /// without a body. Call this BEFORE <see cref="GetFunctionPointer"/>
    /// so the JIT resolves the symbol on first use.
    /// </summary>
    public void BindExtern(string symbolName, IntPtr nativeFn)
    {
        var fnVal = _engine.FindFunction(symbolName);
        if (fnVal.Handle == IntPtr.Zero)
            throw new InvalidOperationException(
                $"Extern '{symbolName}' not declared in module — nothing to bind.");
        _engine.AddGlobalMapping(fnVal, nativeFn);
    }

    private static void EnsureJitInitialized()
    {
        if (_jitInitialized) return;
        lock (_initLock)
        {
            if (_jitInitialized) return;
            LLVM.InitializeAllTargetInfos();
            LLVM.InitializeAllTargets();
            LLVM.InitializeAllTargetMCs();
            LLVM.InitializeAllAsmPrinters();
            LLVM.InitializeAllAsmParsers();
            LLVM.LinkInMCJIT();
            _jitInitialized = true;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // MCJIT engine owns the module; disposing the engine frees both.
        _engine.Dispose();
    }
}
