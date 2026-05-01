using AprCpu.Core.IR;
using LLVMSharp.Interop;

namespace AprCpu.Core.Runtime;

/// <summary>
/// Phase 3.1.a/b — host runtime that JIT-compiles a spec module via MCJIT
/// and exposes raw function pointers + struct field offsets keyed off
/// <see cref="CpuStateLayout"/>. Host code passes a byte buffer (sized by
/// <see cref="StateSizeBytes"/>) instead of a hardcoded ARM-specific
/// C# struct, so the same runtime drives ARM7TDMI today and GB LR35902
/// in Phase 4.5.
///
/// MCJIT is used because LLVMSharp.Interop's MCJIT bindings are stable
/// across LLVM 17/18/20. ORC LLJIT can come later if needed.
///
/// <para><b>Extern binding mechanism (the painful bit):</b></para>
///
/// Memory-bus and bank-swap externs are NOT declared as function
/// declarations — that path doesn't work because MCJIT on Windows x64
/// silently falls back to Small code model regardless of
/// <c>LLVMCodeModelLarge</c>, and any heap-allocated host slot more than
/// 2GB from the JIT'd code page produces a crash on the RIP-relative
/// load that fetches the function pointer.
///
/// Instead, each extern is an LLVM global variable of pointer type whose
/// <b>initializer is a constant <c>inttoptr</c> of the trampoline's
/// 64-bit address</b>. MCJIT then places the global in <c>.rdata</c>
/// adjacent to <c>.text</c>, so the RIP-relative load succeeds. The
/// loaded value is the trampoline's full 64-bit address; <c>call</c>
/// through a register has no distance limit.
///
/// Implications:
/// - <see cref="BindExtern"/> must be called BEFORE <see cref="Finalize"/>
///   (which creates the MCJIT engine and locks the module).
/// - Re-binding the same extern after Finalize is not supported — the
///   address is baked into the JIT-compiled image.
///
/// Typical usage:
/// <code>
///   var rt = HostRuntime.Build(module, layout);
///   rt.BindExtern("memory_read_32", trampolinePtr);
///   ...
///   rt.Finalize();
///   var fnPtr = rt.GetFunctionPointer("Execute_...");
/// </code>
/// </summary>
public sealed unsafe class HostRuntime : IDisposable
{
    private static bool _jitInitialized;
    private static readonly object _initLock = new();

    private readonly LLVMModuleRef _module;
    private LLVMExecutionEngineRef _engine;
    private LLVMTargetDataRef       _targetData;
    private bool _finalized;
    private bool _disposed;

    public CpuStateLayout Layout { get; }

    /// <summary>Total byte size of the CPU-state struct.</summary>
    public ulong StateSizeBytes { get; private set; }

    private HostRuntime(LLVMModuleRef module, CpuStateLayout layout)
    {
        _module = module;
        Layout  = layout;
    }

    /// <summary>
    /// Start a new host runtime. Externs must be bound via
    /// <see cref="BindExtern"/> before <see cref="Finalize"/> is called.
    /// </summary>
    public static HostRuntime Build(LLVMModuleRef module, CpuStateLayout layout)
        => new(module, layout);

    /// <summary>
    /// Convenience: build, finalize, and return — for tests that don't
    /// need any externs (pure GPR/CPSR instructions).
    /// </summary>
    public static HostRuntime Create(LLVMModuleRef module, CpuStateLayout layout)
    {
        var rt = Build(module, layout);
        rt.Finalize();
        return rt;
    }

    /// <summary>
    /// Bake the trampoline address for an extern symbol into the IR as
    /// the initializer of the corresponding global pointer. Must be
    /// called BEFORE <see cref="Finalize"/>.
    /// </summary>
    public void BindExtern(string symbolName, IntPtr nativeFn)
    {
        if (_finalized)
            throw new InvalidOperationException(
                $"BindExtern('{symbolName}'): runtime already finalized — externs must be bound first.");

        var globalSlot = _module.GetNamedGlobal(symbolName);
        if (globalSlot.Handle == IntPtr.Zero)
            throw new InvalidOperationException(
                $"Extern '{symbolName}' not declared in module — nothing to bind. " +
                "Declare it as an external global pointer variable.");

        // Set the initializer to inttoptr(addr) so MCJIT places the global
        // in .rdata adjacent to .text. Switch linkage to Internal so the
        // JIT linker doesn't try to satisfy the symbol externally.
        var i64 = LLVMTypeRef.Int64;
        var addrConst = LLVMValueRef.CreateConstInt(i64, (ulong)nativeFn.ToInt64(), false);
        var ptrType = LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);   // generic ptr
        var ptrConst = LLVMValueRef.CreateConstIntToPtr(addrConst, ptrType);

        globalSlot.Initializer = ptrConst;
        globalSlot.Linkage = LLVMLinkage.LLVMInternalLinkage;
    }

    /// <summary>
    /// Lock the module, create the MCJIT engine, and become ready for
    /// <see cref="GetFunctionPointer"/>. After this, <see cref="BindExtern"/>
    /// will throw.
    /// </summary>
    public void Finalize()
    {
        if (_finalized) return;
        EnsureJitInitialized();

        var options = new LLVMMCJITCompilerOptions { OptLevel = 0 };
        _engine = _module.CreateMCJITCompiler(ref options);
        _targetData = LLVM.GetExecutionEngineTargetData(_engine);
        StateSizeBytes = LLVM.SizeOfTypeInBits(_targetData, Layout.StructType) / 8;
        _finalized = true;
    }

    /// <summary>Look up the native entry point of a JIT'd function by its IR name.</summary>
    public IntPtr GetFunctionPointer(string functionName)
    {
        EnsureFinalized();
        var addr = _engine.GetFunctionAddress(functionName);
        if (addr == 0)
            throw new InvalidOperationException(
                $"MCJIT could not resolve function '{functionName}'.");
        return (IntPtr)addr;
    }

    /// <summary>Byte offset of a struct field within the CPU-state buffer.</summary>
    public ulong GetFieldOffsetBytes(int fieldIndex)
    {
        EnsureFinalized();
        return LLVM.OffsetOfElement(_targetData, Layout.StructType, (uint)fieldIndex);
    }

    public ulong GprOffset(int regIndex) => GetFieldOffsetBytes(Layout.GprFieldIndex(regIndex));
    public ulong StatusOffset(string name, string? mode = null) => GetFieldOffsetBytes(Layout.StatusFieldIndex(name, mode));
    public ulong BankedGprOffset(string mode, int idxInGroup) => GetFieldOffsetBytes(Layout.BankedGprFieldIndex(mode, idxInGroup));
    public ulong CycleCounterOffset => GetFieldOffsetBytes(Layout.CycleCounterFieldIndex);

    private void EnsureFinalized()
    {
        if (!_finalized)
            throw new InvalidOperationException(
                "HostRuntime: call Finalize() (or Create) before using JIT-dependent operations.");
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
        if (_finalized) _engine.Dispose();
    }
}
