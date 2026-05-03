using AprCpu.Core.JsonSpec;
using LLVMSharp.Interop;

namespace AprCpu.Core.IR;

/// <summary>
/// Generic memory-access emitters: <c>load</c> and <c>store</c>. Both
/// lower to calls into externally declared C-callable functions
/// (<c>memory_read_8/16/32</c>, <c>memory_write_8/16/32</c>) that the
/// host runtime binds to a real memory bus at JIT execution time
/// (Phase 3 work).
///
/// Step shape:
/// <code>
///   { "op": "load",  "addr": &lt;value-name&gt;, "size": 8|16|32,
///     "signed": true|false, "out": &lt;name&gt; }
///   { "op": "store", "addr": &lt;value-name&gt;, "value": &lt;value-name&gt;,
///     "size": 8|16|32 }
/// </code>
///
/// Address and value references are names in <see cref="EmitContext.Values"/>
/// (or fields auto-resolved from the instruction word). The size is
/// taken from the JSON; `signed` defaults to false (zero-extend on load
/// to i32). All memory results are unified as i32.
///
/// ARM-specific quirks like rotated unaligned reads are NOT modelled
/// here — they belong in the host-side memory bus implementation. This
/// emitter is intentionally architecture-agnostic.
/// </summary>
public static class MemoryEmitters
{
    public static void RegisterAll(EmitterRegistry reg)
    {
        reg.Register(new LoadEmitter());
        reg.Register(new StoreEmitter());
    }

    /// <summary>
    /// Names for the externally bound memory-bus functions. Kept centralised
    /// so the host knows what to bind.
    /// </summary>
    public static class ExternFunctionNames
    {
        public const string Read8   = "memory_read_8";
        public const string Read16  = "memory_read_16";
        public const string Read32  = "memory_read_32";
        public const string Write8  = "memory_write_8";
        public const string Write16 = "memory_write_16";
        public const string Write32 = "memory_write_32";

        // Phase 7 GB block-JIT P0.7 — sync-flag write variants. Return
        // i8: 1 = sync needed (IRQ state may have changed; JIT'd block
        // should exit early), 0 = no sync needed (continue executing).
        // Block-JIT memory emitter uses these; per-instr backend uses
        // the void variants above (outer loop checks IRQ between every
        // instruction regardless). Bus shims bind both. See
        // MD/design/14-irq-sync-fastslow.md.
        public const string Write8WithSync  = "memory_write_8_sync";
        public const string Write16WithSync = "memory_write_16_sync";
        public const string Write32WithSync = "memory_write_32_sync";

        // Phase 7 GB block-JIT P1 #7 — RAM region base pointer constants
        // for IR-level inline memory access. Bound by the host (JsonCpu
        // pins its byte arrays + binds via inttoptr-global pattern).
        // When block-JIT memory emitters detect addr ∈ RAM region, they
        // can do `store/load` directly via GEP from these bases, skipping
        // the bus extern call entirely. Per-CPU base names; not used by
        // ARM/GBA path.
        public const string Lr35902WramBase = "lr35902_wram_base";  // 0xC000..0xDFFF
        public const string Lr35902HramBase = "lr35902_hram_base";  // 0xFF80..0xFFFE

        // Phase 7 GB block-JIT P1 #5b SMC V2 — base of the per-byte coverage
        // counter array (i8[0x10000] for LR35902). After every IR-level
        // inline RAM store, JIT'd code does:
        //     cov_byte = load i8, [coverage_base + addr]
        //     if (cov_byte != 0) call notify_extern(addr)
        // so SMC sees writes that bypass the bus extern (P1 #7 inline path).
        // Without this, RAM writes via the inline fast-path miss the bus
        // shim's NotifyMemoryWrite call, leaving stale block code resident
        // for execution after a RAM-code overwrite. Bound by JsonCpu.Reset
        // to the BlockCache's pinned coverage byte array.
        public const string Lr35902SmcCoverageBase = "lr35902_smc_coverage_base";

        // The slow-path notify function called when the inline coverage
        // check fires. Signature: void(uint32 addr). Trampolines to
        // BlockCache.NotifyMemoryWrite which scans cached blocks and
        // invalidates any whose precise per-instr coverage covers addr.
        public const string Lr35902SmcNotifyWrite = "lr35902_smc_notify_write";
    }

    // ---------------- Public byte-level helpers (used by StackOps etc) ----------------

    public static LLVMValueRef CallRead8(EmitContext ctx, LLVMValueRef addrI32, string outLabel)
    {
        var (slot, fnType, ptrType) = GetOrDeclareMemoryFunctionPointer(
            ctx.Module, ExternFunctionNames.Read8, LLVMTypeRef.Int8, LLVMTypeRef.Int32);
        var fn = ctx.Builder.BuildLoad2(ptrType, slot, $"{outLabel}_fn");
        return ctx.Builder.BuildCall2(fnType, fn, new[] { addrI32 }, outLabel);
    }

    public static void CallWrite8(EmitContext ctx, LLVMValueRef addrI32, LLVMValueRef valueI8)
    {
        var (slot, fnType, ptrType) = GetOrDeclareMemoryFunctionPointer(
            ctx.Module, ExternFunctionNames.Write8,
            ctx.Module.Context.VoidType, LLVMTypeRef.Int32, LLVMTypeRef.Int8);
        var fn = ctx.Builder.BuildLoad2(ptrType, slot, "w8_fn");
        ctx.Builder.BuildCall2(fnType, fn, new[] { addrI32, valueI8 }, "");
    }

    /// <summary>
    /// Phase 7 GB block-JIT P0.7 — write 8-bit + receive sync flag (i8:
    /// 1 = IRQ state may have changed, JIT'd block should exit early;
    /// 0 = no sync needed). Block-JIT memory emitter uses this so it can
    /// branch on the result. Per-instr backends should keep using
    /// <see cref="CallWrite8"/> — there's no benefit since per-instr
    /// already checks IRQ between every instruction.
    /// </summary>
    public static LLVMValueRef CallWrite8WithSync(EmitContext ctx, LLVMValueRef addrI32, LLVMValueRef valueI8, string outLabel = "w8_sync")
    {
        var (slot, fnType, ptrType) = GetOrDeclareMemoryFunctionPointer(
            ctx.Module, ExternFunctionNames.Write8WithSync,
            LLVMTypeRef.Int8, LLVMTypeRef.Int32, LLVMTypeRef.Int8);
        var fn = ctx.Builder.BuildLoad2(ptrType, slot, "w8s_fn");
        return ctx.Builder.BuildCall2(fnType, fn, new[] { addrI32, valueI8 }, outLabel);
    }

    /// <summary>
    /// Phase 7 GB block-JIT P1 #7 — declare/lookup a RAM-region base
    /// pointer global. Host binds via inttoptr-global pattern (same
    /// machinery as bus extern function pointers). Returns the global
    /// slot ref + i8* type; caller does BuildLoad2 to get the actual
    /// byte pointer at runtime, then GEPs from there with (addr - base_addr).
    /// </summary>
    public static (LLVMValueRef Slot, LLVMTypeRef PtrType) GetOrDeclareRamBasePointer(LLVMModuleRef module, string name)
    {
        var ptrType = LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);
        var existing = module.GetNamedGlobal(name);
        if (existing.Handle != IntPtr.Zero) return (existing, ptrType);
        var slot = module.AddGlobal(ptrType, name);
        slot.Linkage = LLVMLinkage.LLVMExternalLinkage;
        return (slot, ptrType);
    }

    /// <summary>
    /// Look up (or declare on first use) the global pointer slot holding
    /// the address of an external memory-bus function. The host binds
    /// each name to a real native function pointer at JIT time via
    /// <c>AddGlobalMapping</c> on the global.
    ///
    /// Background: MCJIT in LLVM 20 cannot reliably bind <c>declare</c>'d
    /// extern functions via AddGlobalMapping (the call site stays
    /// unresolved and crashes with an access violation). The standard
    /// workaround is to indirect through a global function pointer that
    /// LLVM CAN bind. Each call site does <c>load</c> + <c>call %loaded</c>.
    /// </summary>
    public static (LLVMValueRef Slot, LLVMTypeRef FnType, LLVMTypeRef PtrType)
        GetOrDeclareMemoryFunctionPointer(
            LLVMModuleRef module,
            string name,
            LLVMTypeRef returnType,
            params LLVMTypeRef[] paramTypes)
    {
        var fnType  = LLVMTypeRef.CreateFunction(returnType, paramTypes);
        var ptrType = LLVMTypeRef.CreatePointer(fnType, 0);

        var existing = module.GetNamedGlobal(name);
        if (existing.Handle != IntPtr.Zero)
            return (existing, fnType, ptrType);

        var slot = module.AddGlobal(ptrType, name);
        slot.Linkage = LLVMLinkage.LLVMExternalLinkage;
        return (slot, fnType, ptrType);
    }
}

internal sealed class LoadEmitter : IMicroOpEmitter
{
    public string OpName => "load";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var addrName = step.Raw.GetProperty("addr").GetString()!;
        var size     = step.Raw.GetProperty("size").GetInt32();
        var signed   = step.Raw.TryGetProperty("signed", out var s) && s.GetBoolean();
        var outName  = step.Raw.GetProperty("out").GetString()!;

        var addr = ctx.Resolve(addrName);

        var (returnType, fnName) = size switch
        {
            8  => (LLVMTypeRef.Int8,  MemoryEmitters.ExternFunctionNames.Read8),
            16 => (LLVMTypeRef.Int16, MemoryEmitters.ExternFunctionNames.Read16),
            32 => (LLVMTypeRef.Int32, MemoryEmitters.ExternFunctionNames.Read32),
            _ => throw new NotSupportedException($"load size {size} not supported (use 8/16/32).")
        };

        var (slot, fnType, ptrType) = MemoryEmitters.GetOrDeclareMemoryFunctionPointer(
            ctx.Module, fnName, returnType, LLVMTypeRef.Int32);
        var loadedFn = ctx.Builder.BuildLoad2(ptrType, slot, $"{fnName}_fn");
        var raw = ctx.Builder.BuildCall2(fnType, loadedFn, new[] { addr }, $"{outName}_raw");

        // Promote to i32. For sub-word loads we must respect signed flag.
        LLVMValueRef result;
        if (size == 32)
        {
            result = raw;
        }
        else if (signed)
        {
            result = ctx.Builder.BuildSExt(raw, LLVMTypeRef.Int32, outName);
        }
        else
        {
            result = ctx.Builder.BuildZExt(raw, LLVMTypeRef.Int32, outName);
        }

        // Ensure the named result is exactly `outName` even when no extension
        // was needed (i32 case).
        if (size == 32 && raw.Name != outName) result.Name = outName;

        ctx.Values[outName] = result;
    }
}

internal sealed class StoreEmitter : IMicroOpEmitter
{
    public string OpName => "store";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var addrName  = step.Raw.GetProperty("addr").GetString()!;
        var valueName = step.Raw.GetProperty("value").GetString()!;
        var size      = step.Raw.GetProperty("size").GetInt32();

        var addr  = ctx.Resolve(addrName);
        var value = ctx.Resolve(valueName);

        var (paramType, fnName) = size switch
        {
            8  => (LLVMTypeRef.Int8,  MemoryEmitters.ExternFunctionNames.Write8),
            16 => (LLVMTypeRef.Int16, MemoryEmitters.ExternFunctionNames.Write16),
            32 => (LLVMTypeRef.Int32, MemoryEmitters.ExternFunctionNames.Write32),
            _ => throw new NotSupportedException($"store size {size} not supported (use 8/16/32).")
        };

        // Truncate i32 source to required width.
        var truncated = size == 32
            ? value
            : ctx.Builder.BuildTrunc(value, paramType, $"{valueName}_trunc");

        var (slot, fnType, ptrType) = MemoryEmitters.GetOrDeclareMemoryFunctionPointer(
            ctx.Module, fnName, ctx.Module.Context.VoidType, LLVMTypeRef.Int32, paramType);
        var loadedFn = ctx.Builder.BuildLoad2(ptrType, slot, $"{fnName}_fn");

        ctx.Builder.BuildCall2(fnType, loadedFn, new[] { addr, truncated }, "");
    }
}
