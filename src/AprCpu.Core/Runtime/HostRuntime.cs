using System.Runtime.InteropServices;
using AprCpu.Core.IR;
using LLVMSharp.Interop;

namespace AprCpu.Core.Runtime;

/// <summary>
/// Phase 7 A.3 (2026-05-02) — host runtime upgraded from MCJIT to ORC
/// LLJIT. Same external surface (Build / BindExtern / Compile /
/// GetFunctionPointer / field offsets) plus a new <see cref="AddModule"/>
/// entry point for block-JIT cache-miss path (Phase 7 A.4) where each
/// freshly-detected hot block compiles into its own module and gets
/// linked into the live JIT post-Compile.
///
/// <para><b>Why ORC over MCJIT:</b> MCJIT finalizes the entire module on
/// <c>Compile()</c> and provides no API to add another module afterwards.
/// Block-JIT requires lazy module addition for every cache miss, so MCJIT
/// is a structural blocker. ORC LLJIT supports
/// <c>OrcLLJITAddLLVMIRModule</c> repeatedly against the same JITDylib
/// and resolves cross-module symbols transparently.</para>
///
/// <para><b>Extern binding mechanism preserved verbatim:</b></para>
///
/// Memory-bus and bank-swap externs are NOT declared as function
/// declarations — that path historically didn't work because MCJIT on
/// Windows x64 silently fell back to Small code model regardless of
/// <c>LLVMCodeModelLarge</c>, and any heap-allocated host slot more than
/// 2GB from the JIT'd code page produced a crash on the RIP-relative
/// load that fetched the function pointer. ORC LLJIT may not exhibit
/// this exact bug, but the inttoptr-global pattern is engine-agnostic
/// and known to work, so we keep it.
///
/// Each extern is an LLVM global variable of pointer type whose
/// initializer is a constant <c>inttoptr</c> of the trampoline's 64-bit
/// address. The JIT places the global in <c>.rdata</c> adjacent to
/// <c>.text</c>, so the RIP-relative load succeeds. The loaded value is
/// the trampoline's full 64-bit address; <c>call</c> through a register
/// has no distance limit.
///
/// Implications:
/// - <see cref="BindExtern"/> must be called BEFORE <see cref="Compile"/>
///   (which seals the initial module and adds it to the LLJIT).
/// - Re-binding the same extern after Compile is not supported — the
///   address is baked into the JIT-compiled image.
///
/// Typical usage:
/// <code>
///   var rt = HostRuntime.Build(module, layout);
///   rt.BindExtern("memory_read_32", trampolinePtr);
///   ...
///   rt.Compile();
///   var fnPtr = rt.GetFunctionPointer("Execute_...");
/// </code>
/// </summary>
public sealed unsafe class HostRuntime : IDisposable
{
    private static bool _jitInitialized;
    private static readonly object _initLock = new();

    private readonly LLVMModuleRef _initialModule;
    private LLVMOrcOpaqueLLJIT*               _lljit;
    private LLVMOrcOpaqueJITDylib*            _mainJD;
    private LLVMOrcOpaqueThreadSafeContext*   _tsCtx;
    private LLVMTargetDataRef                 _targetData;
    // Phase 7 H.a-instcombine fix (2026-05-03): the LLJIT-derived data
    // layout string. Stored so AddModule() can apply it to per-block
    // modules before they hit RunOptimizationPipeline. Without this,
    // instcombine uses a default datalayout that disagrees with our
    // OffsetOfElement-derived field offsets (in particular, struct
    // alignment/padding around i64+i8+i32 mix), producing GEPs whose
    // byte-offsets are wrong by 4 bytes — breaks BlockFunctionBuilder
    // tests where pc_written / cycles_left / R-register accesses end
    // up pointing at the wrong slot.
    private string? _dataLayoutStr;
    private bool _finalized;
    private bool _disposed;

    // Phase 7 A.4 — track every BindExtern call so AddModule (called for
    // block-JIT modules post-Compile) can replay the same trampoline
    // bindings into the new module's extern global slots. Without this,
    // a block module that uses memory_read_*, bank_swap, etc. would only
    // see uninitialized globals → trap stub at runtime.
    private readonly Dictionary<string, IntPtr> _externBindings
        = new(StringComparer.Ordinal);

    public CpuStateLayout Layout { get; }

    /// <summary>Total byte size of the CPU-state struct.</summary>
    public ulong StateSizeBytes { get; private set; }

    private HostRuntime(LLVMModuleRef module, CpuStateLayout layout)
    {
        _initialModule = module;
        Layout  = layout;
    }

    /// <summary>
    /// Start a new host runtime. Externs must be bound via
    /// <see cref="BindExtern"/> before <see cref="Compile"/> is called.
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
        rt.Compile();
        return rt;
    }

    /// <summary>
    /// Bake the trampoline address for an extern symbol into the IR as
    /// the initializer of the corresponding global pointer. Must be
    /// called BEFORE <see cref="Compile"/>.
    /// </summary>
    public void BindExtern(string symbolName, IntPtr nativeFn)
    {
        if (_finalized)
            throw new InvalidOperationException(
                $"BindExtern('{symbolName}'): runtime already finalized — externs must be bound first.");

        var globalSlot = _initialModule.GetNamedGlobal(symbolName);
        if (globalSlot.Handle == IntPtr.Zero)
            throw new InvalidOperationException(
                $"Extern '{symbolName}' not declared in module — nothing to bind. " +
                "Declare it as an external global pointer variable.");

        BindExternInModule(_initialModule, symbolName, nativeFn);
        // Phase 7 A.4 — remember binding so block modules added later
        // (via AddModule) can replay the same trampoline address.
        _externBindings[symbolName] = nativeFn;
    }

    /// <summary>
    /// Inner helper — write the inttoptr-global binding into a specific
    /// module. Used by both <see cref="BindExtern"/> (initial module)
    /// and <see cref="AddModule"/> (block-JIT modules, which need the
    /// same bindings replayed into their own private global slots).
    /// </summary>
    private static void BindExternInModule(LLVMModuleRef module, string symbolName, IntPtr nativeFn)
    {
        var slot = module.GetNamedGlobal(symbolName);
        if (slot.Handle == IntPtr.Zero) return;   // module doesn't reference this extern

        var i64 = LLVMTypeRef.Int64;
        var addrConst = LLVMValueRef.CreateConstInt(i64, (ulong)nativeFn.ToInt64(), false);
        var ptrType = LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);
        var ptrConst = LLVMValueRef.CreateConstIntToPtr(addrConst, ptrType);

        slot.Initializer = ptrConst;
        slot.Linkage = LLVMLinkage.LLVMInternalLinkage;
    }

    /// <summary>
    /// Lock the initial module, create the ORC LLJIT engine, run the
    /// IR-level optimization pipeline, hand the module to the JIT, and
    /// become ready for <see cref="GetFunctionPointer"/>. After this,
    /// <see cref="BindExtern"/> will throw, but <see cref="AddModule"/>
    /// remains available for block-JIT cache-miss compilation.
    /// </summary>
    public void Compile()
    {
        if (_finalized) return;
        EnsureJitInitialized();

        // Auto-bind any extern global pointer the IR declared but no
        // caller wired up. Point it at a trap stub so calls fail loudly
        // instead of dereferencing NULL.
        BindUnboundExternsToTrap(_initialModule);

        // --- Build the LLJIT engine FIRST so we can pull its datalayout
        // and apply it to the module BEFORE optimisation runs. Required
        // for Phase 7 H.a-instcombine fix (see _dataLayoutStr field
        // comment). Module ownership is still transferred at AddModuleToJit
        // call below, so creating the engine early is safe.
        var jitBuilder = LLVM.OrcCreateLLJITBuilder();
        LLVMOrcOpaqueLLJIT* lljitOut;
        var createErr = LLVM.OrcCreateLLJIT(&lljitOut, jitBuilder);
        ThrowIfError(createErr, "OrcCreateLLJIT");
        _lljit = lljitOut;
        _mainJD = LLVM.OrcLLJITGetMainJITDylib(_lljit);

        // Compute StateSizeBytes/field offsets against the host's data
        // layout (LLJIT-derived) — lengths are independent of the module's
        // own LLVMContext so we can use Layout.StructType directly.
        var dlStrPtr = LLVM.OrcLLJITGetDataLayoutStr(_lljit);
        _dataLayoutStr = MarshalUtf8(dlStrPtr) ?? string.Empty;
        _targetData    = LLVMTargetDataRef.FromStringRepresentation(_dataLayoutStr);
        StateSizeBytes = LLVM.SizeOfTypeInBits(_targetData, Layout.StructType) / 8;

        // Phase 7 H.a-instcombine fix — propagate the datalayout into the
        // module so passes (instcombine especially) see the same struct
        // layout that our OffsetOfElement-derived field offsets use.
        SetModuleDataLayoutString(_initialModule, _dataLayoutStr);

        // Phase 7 H.a (re-enabled 2026-05-03): explicit LLVM new pass
        // manager pipeline (mem2reg/instcombine/gvn/dse/simplifycfg).
        // Originally disabled on recovery branch when instcombine
        // miscompiled BIOS LLE path. Subsequent investigation traced
        // the symptom to our Strategy 2 read_reg(15)/PC handling bugs
        // (commits 260cbb0 + 0fa2153) which generated IR patterns that
        // instcombine optimised into broken code. With those fixed
        // upstream the pipeline can re-enable safely. ~50ms one-shot
        // compile cost per spec module — acceptable.
        RunOptimizationPipeline(_initialModule);

        // Hand the module to the JIT. ORC takes ownership of both the
        // module and its TSM wrapper on success — never touch the
        // LLVMModuleRef after this.
        _tsCtx = LLVM.OrcCreateNewThreadSafeContext();
        AddModuleToJit(_initialModule);

        _finalized = true;
    }

    /// <summary>
    /// Phase 7 H.a-instcombine fix — assign the LLJIT-derived data layout
    /// string to a module before optimisation. Must agree with the layout
    /// used by <see cref="LLVM.OffsetOfElement"/> calls used to derive
    /// field byte-offsets host-side, otherwise instcombine simplifies
    /// struct GEPs into raw byte GEPs using the WRONG offsets.
    /// </summary>
    private static void SetModuleDataLayoutString(LLVMModuleRef module, string dlStr)
    {
        if (string.IsNullOrEmpty(dlStr)) return;
        var bytes = System.Text.Encoding.ASCII.GetBytes(dlStr + "\0");
        fixed (byte* p = bytes) LLVM.SetDataLayout(module, (sbyte*)p);
    }

    /// <summary>
    /// Phase 7 A.3 — add another LLVM module into the live JIT after
    /// <see cref="Compile"/> has run. Used by the block-JIT cache-miss
    /// path (A.4): compile a hot block into a fresh module, hand it to
    /// the JIT, then look up its block function via
    /// <see cref="GetFunctionPointer"/>.
    ///
    /// The added module must use unique LLVM symbol names — collisions
    /// against the initial module (or earlier-added modules) will fail
    /// at lookup time. Block functions named via
    /// <c>BlockFunctionBuilder.BlockFunctionName</c> (which embeds the
    /// block start PC) are unique by construction.
    /// </summary>
    public void AddModule(LLVMModuleRef module)
    {
        EnsureFinalized();
        // Replay all known extern bindings into this module's matching
        // global slots. The block IR will have declared each extern it
        // references via GetOrDeclareExtern (external-linkage global ptr);
        // here we bake the same trampoline address as the initial module.
        foreach (var (name, addr) in _externBindings)
            BindExternInModule(module, name, addr);
        // Anything still unbound in this module (extern declared but not
        // in our binding map — shouldn't happen for our emitters) falls
        // back to the trap stub.
        BindUnboundExternsToTrap(module);
        // Phase 7 H.a-instcombine fix — apply same datalayout to per-block
        // modules so instcombine sees the same struct layout the host
        // computed offsets against. Skipped silently if Compile() never
        // recorded one (won't happen — AddModule requires Compile first).
        if (_dataLayoutStr is not null)
            SetModuleDataLayoutString(module, _dataLayoutStr);
        // Phase 7 H.a — apply same IR-level pipeline to per-block JIT
        // modules so they get the same alloca→SSA + DSE benefits.
        RunOptimizationPipeline(module);
        AddModuleToJit(module);
    }

    private void AddModuleToJit(LLVMModuleRef module)
    {
        var tsm = LLVM.OrcCreateNewThreadSafeModule(module, _tsCtx);
        var err = LLVM.OrcLLJITAddLLVMIRModule(_lljit, _mainJD, tsm);
        ThrowIfError(err, "OrcLLJITAddLLVMIRModule");
    }

    /// <summary>
    /// Phase 7 H.a — run the explicit LLVM IR-level optimisation pipeline
    /// on a module via the new pass manager API (LLVM.RunPasses).
    ///
    /// Pass list (curated, in order):
    ///   mem2reg      — promote alloca → SSA registers + PHI nodes
    ///   instcombine  — peephole-style instruction combining
    ///   gvn          — global value numbering (CSE)
    ///   dse          — dead store elimination
    ///   simplifycfg  — basic block merging + branch simplification
    /// </summary>
    /// <summary>DEBUG: when set (or APR_IR_BEFORE/APR_IR_AFTER env vars set),
    /// dumps IR before+after RunPasses to two files.</summary>
    public static string? DebugIrDumpBeforePath;
    public static string? DebugIrDumpAfterPath;

    private void RunOptimizationPipeline(LLVMModuleRef module)
    {
        var beforePath = DebugIrDumpBeforePath ?? Environment.GetEnvironmentVariable("APR_IR_BEFORE");
        var afterPath  = DebugIrDumpAfterPath  ?? Environment.GetEnvironmentVariable("APR_IR_AFTER");
        if (beforePath is not null)
            System.IO.File.WriteAllText(beforePath, module.PrintToString());
        // instcombine&lt;no-verify-fixpoint&gt; instead of plain instcombine —
        // some emitter-generated IR (notably ARM Branch_Exchange BX with
        // its select-chain alignment logic) doesn't reach instcombine's
        // expected fixpoint in 1 iteration; the verify is a sanity check
        // that's safe to skip for our use case.
        // BISECT mode: APR_PASSES env can override
        var passesEnv = Environment.GetEnvironmentVariable("APR_PASSES");
        // Phase 7 H.a-instcombine (2026-05-03): instcombine added back to
        // default pipeline now that the underlying root cause (missing
        // module datalayout → instcombine using wrong struct field
        // offsets) is fixed in Compile()/AddModule(). Use the
        // <no-verify-fixpoint> variant because our IR (ARM v4T spec with
        // restore_cpsr_from_spsr's switch-then-PHI alignment chain)
        // doesn't reach instcombine's expected fixpoint in 1 iteration —
        // the verify is a sanity check that's safe to skip for our use.
        var passes = passesEnv ?? "mem2reg,instcombine<no-verify-fixpoint>,gvn,dse,simplifycfg";

        var optionsHandle = LLVMPassBuilderOptionsRef.Create();
        try
        {
            var passesBytes = System.Text.Encoding.ASCII.GetBytes(passes + "\0");
            fixed (byte* passesPtr = passesBytes)
            {
                var err = LLVM.RunPasses(module, (sbyte*)passesPtr, default(LLVMTargetMachineRef), optionsHandle);
                if (err != null)
                {
                    var msg = ExtractAndDispose(err);
                    throw new InvalidOperationException(
                        $"HostRuntime: LLVM RunPasses('{passes}') failed: {msg}");
                }
            }
            if (afterPath is not null)
                System.IO.File.WriteAllText(afterPath, module.PrintToString());
        }
        finally
        {
            optionsHandle.Dispose();
        }
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly(
        CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static void TrapStub() => throw new InvalidOperationException(
        "HostRuntime: an extern was invoked but no handler was installed before Compile().");

    private static void BindUnboundExternsToTrap(LLVMModuleRef module)
    {
        var trapAddr = (IntPtr)(delegate* unmanaged[Cdecl]<void>)&TrapStub;
        var g = module.FirstGlobal;
        while (g.Handle != IntPtr.Zero)
        {
            // Only consider external globals that we declared as ptr-typed
            // function slots (no initializer set yet → linkage stays External).
            if (g.Linkage == LLVMLinkage.LLVMExternalLinkage && g.Initializer.Handle == IntPtr.Zero)
            {
                var i64 = LLVMTypeRef.Int64;
                var addrConst = LLVMValueRef.CreateConstInt(i64, (ulong)trapAddr.ToInt64(), false);
                var ptrType = LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);
                var ptrConst = LLVMValueRef.CreateConstIntToPtr(addrConst, ptrType);
                g.Initializer = ptrConst;
                g.Linkage = LLVMLinkage.LLVMInternalLinkage;
            }
            g = g.NextGlobal;
        }
    }

    /// <summary>Look up the native entry point of a JIT'd function by its IR name.</summary>
    public IntPtr GetFunctionPointer(string functionName)
    {
        EnsureFinalized();
        var nameBytes = System.Text.Encoding.ASCII.GetBytes(functionName + "\0");
        fixed (byte* p = nameBytes)
        {
            ulong addr;
            var err = LLVM.OrcLLJITLookup(_lljit, &addr, (sbyte*)p);
            if (err != null)
            {
                var msg = ExtractAndDispose(err);
                throw new InvalidOperationException(
                    $"ORC LLJIT could not resolve function '{functionName}': {msg}");
            }
            if (addr == 0)
                throw new InvalidOperationException(
                    $"ORC LLJIT returned NULL address for function '{functionName}'.");
            return (IntPtr)addr;
        }
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
    public ulong PcWrittenOffset    => GetFieldOffsetBytes(Layout.PcWrittenFieldIndex);
    public ulong CyclesLeftOffset   => GetFieldOffsetBytes(Layout.CyclesLeftFieldIndex);

    private void EnsureFinalized()
    {
        if (!_finalized)
            throw new InvalidOperationException(
                "HostRuntime: call Compile() (or Create) before using JIT-dependent operations.");
    }

    private static void EnsureJitInitialized()
    {
        if (_jitInitialized) return;
        lock (_initLock)
        {
            if (_jitInitialized) return;
            // ORC LLJIT needs targets registered (codegen) but does NOT
            // need LinkInMCJIT — the LLJITBuilder selects an appropriate
            // object linking layer (RuntimeDyld or JITLink) on its own.
            LLVM.InitializeAllTargetInfos();
            LLVM.InitializeAllTargets();
            LLVM.InitializeAllTargetMCs();
            LLVM.InitializeAllAsmPrinters();
            LLVM.InitializeAllAsmParsers();
            _jitInitialized = true;
        }
    }

    private static void ThrowIfError(LLVMOpaqueError* err, string ctx)
    {
        if (err == null) return;
        var msg = ExtractAndDispose(err);
        throw new InvalidOperationException($"HostRuntime: {ctx} failed: {msg}");
    }

    private static string ExtractAndDispose(LLVMOpaqueError* err)
    {
        var msgPtr = LLVM.GetErrorMessage(err);
        var msg = Marshal.PtrToStringUTF8((IntPtr)msgPtr) ?? "(unknown)";
        LLVM.DisposeErrorMessage(msgPtr);
        return msg;
    }

    private static string? MarshalUtf8(sbyte* p)
        => p == null ? null : Marshal.PtrToStringUTF8((IntPtr)p);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Order matters: dispose LLJIT first (it owns the modules added
        // to it; tearing it down will release them); then dispose the
        // shared TSC; finally the target data.
        if (_lljit != null)
        {
            // OrcDisposeLLJIT returns an error code we drop in Dispose.
            LLVM.OrcDisposeLLJIT(_lljit);
            _lljit = null;
        }
        if (_tsCtx != null)
        {
            LLVM.OrcDisposeThreadSafeContext(_tsCtx);
            _tsCtx = null;
        }
        if (_targetData.Handle != IntPtr.Zero)
        {
            LLVM.DisposeTargetData(_targetData);
        }
    }
}
