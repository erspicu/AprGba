using System.Text.Json;
using AprCpu.Core.JsonSpec;
using LLVMSharp.Interop;

namespace AprCpu.Core.IR;

/// <summary>
/// Built-in CPU-agnostic micro-op emitters: register I/O, arithmetic /
/// logical / shift, simple bitwise extras (mvn, bic), control flow
/// (if, branch, branch_link). ARM-specific emitters (carry-aware ALU,
/// CPSR flag updates, PSR access, BX-style indirect branch with T-bit)
/// live in <see cref="ArmEmitters"/> and are registered separately
/// based on the spec's <c>architecture.family</c>.
/// </summary>
public static class StandardEmitters
{
    /// <summary>Register every generic emitter into <paramref name="reg"/>.</summary>
    public static void RegisterAll(EmitterRegistry reg)
    {
        // Register access
        reg.Register(new ReadReg());
        reg.Register(new WriteReg());
        reg.Register(new ReadRegShiftByReg());

        // Arithmetic / logical
        reg.Register(new Binary("add", (b, l, r, n) => b.BuildAdd(l, r, n)));
        reg.Register(new Binary("sub", (b, l, r, n) => b.BuildSub(l, r, n)));
        reg.Register(new Binary("and", (b, l, r, n) => b.BuildAnd(l, r, n)));
        reg.Register(new Binary("or",  (b, l, r, n) => b.BuildOr (l, r, n)));
        reg.Register(new Binary("xor", (b, l, r, n) => b.BuildXor(l, r, n)));
        reg.Register(new Binary("shl", (b, l, r, n) => b.BuildShl (l, r, n)));
        reg.Register(new Binary("lsr", (b, l, r, n) => b.BuildLShr(l, r, n)));
        reg.Register(new Binary("asr", (b, l, r, n) => b.BuildAShr(l, r, n)));
        reg.Register(new Binary("rsb", (b, l, r, n) => b.BuildSub(r, l, n)));
        // ROR: rotate-right 32-bit. count is masked to 5 bits; count==0
        // returns the value unchanged (matches ARM/most ISA conventions).
        reg.Register(new Binary("ror", (b, l, r, n) =>
        {
            var i32 = LLVMTypeRef.Int32;
            var c5    = b.BuildAnd(r, LLVMValueRef.CreateConstInt(i32, 31, false), $"{n}_c5");
            var zero  = LLVMValueRef.CreateConstInt(i32, 0, false);
            var isZero = b.BuildICmp(LLVMIntPredicate.LLVMIntEQ, c5, zero, $"{n}_zc");
            var safeC = b.BuildSelect(isZero, LLVMValueRef.CreateConstInt(i32, 1, false), c5, $"{n}_sc");
            var lo = b.BuildLShr(l, safeC, $"{n}_lo");
            var inv = b.BuildSub(LLVMValueRef.CreateConstInt(i32, 32, false), safeC, $"{n}_inv");
            var hi = b.BuildShl(l, inv, $"{n}_hi");
            var rot = b.BuildOr(lo, hi, $"{n}_r");
            return b.BuildSelect(isZero, l, rot, n);
        }));
        reg.Register(new BicEmitter());   // a AND NOT b
        reg.Register(new MvnEmitter());   // NOT a

        // Multiply: 32-bit and 32x32->64 variants.
        reg.Register(new Binary("mul", (b, l, r, n) => b.BuildMul(l, r, n)));
        reg.Register(new MulLongEmitter("umul64", signed: false));
        reg.Register(new MulLongEmitter("smul64", signed: true));
        reg.Register(new AddI64Emitter());
        reg.Register(new ReadRegPairU64Emitter());
        reg.Register(new WriteRegPairEmitter());

        // Memory access (architecture-agnostic; lowers to host-bound externs)
        MemoryEmitters.RegisterAll(reg);
        BlockTransferEmitters.RegisterAll(reg);
        StackOps.RegisterAll(reg);
        FlagOps.RegisterAll(reg);
        BitOps.RegisterAll(reg);

        // Control flow (generic — branch_indirect is in ArmEmitters)
        reg.Register(new IfStep());
        reg.Register(new SelectStep());
        reg.Register(new Branch("branch",      BranchKind.Plain));
        reg.Register(new Branch("branch_link", BranchKind.Link));
        reg.Register(new BranchCc());
        reg.Register(new ReadPc());
        reg.Register(new SextEmitter());
        reg.Register(new TruncEmitter());
    }

    // ---------------- helpers ----------------

    /// <summary>Resolve "in"[i] entry — string name → value.</summary>
    internal static LLVMValueRef ResolveInput(EmitContext ctx, JsonElement step, int index, string defaultName = "in")
    {
        var arr = step.GetProperty(defaultName);
        var item = arr[index];
        return item.ValueKind switch
        {
            JsonValueKind.String => ctx.Resolve(item.GetString()!),
            JsonValueKind.Object when item.TryGetProperty("const", out var c) =>
                ctx.ConstU32(c.ValueKind switch
                {
                    JsonValueKind.Number => (uint)c.GetInt64(),
                    JsonValueKind.String when c.GetString()!.StartsWith("0x") =>
                        Convert.ToUInt32(c.GetString()!.Substring(2), 16),
                    _ => throw new InvalidOperationException("'const' value must be a number or 0x-hex string")
                }),
            _ => throw new InvalidOperationException(
                $"Step input[{index}] must be a name string or {{\"const\": <n>}} object; got {item.ValueKind}")
        };
    }

    internal static string GetOut(JsonElement step) =>
        step.GetProperty("out").GetString()!;
}

// ---------------- register access ----------------

internal sealed class ReadReg : IMicroOpEmitter
{
    public string OpName => "read_reg";
    public unsafe void Emit(EmitContext ctx, MicroOpStep step)
    {
        var indexExpr = step.Raw.GetProperty("index");
        var outName   = StandardEmitters.GetOut(step.Raw);

        // Phase 7 A.6.1 Strategy 2 — when the resolved index is statically
        // known to be PC, return the pipeline PC constant instead of
        // loading GPR[15]. This is what makes block-JIT correct without
        // the executor's per-step pre-set R15 write: GPR[15] in memory
        // is only ever touched by REAL branches.
        if (TryGetStaticPcReadConstant(ctx, indexExpr, pcOffsetAddend: 0u, out var pcConst))
        {
            ctx.Values[outName] = pcConst;
            return;
        }

        var ptr = ResolveRegPtr(ctx, indexExpr);
        var v   = ctx.Builder.BuildLoad2(ctx.Layout.GprType, ptr, outName);
        ctx.Values[outName] = v;
    }

    /// <summary>
    /// Phase 7 A.6.1 Strategy 2 — try to resolve the register index to
    /// the PC index AT JIT TIME, and if so produce a constant LLVM value
    /// equal to <c>PipelinePcConstant + pcOffsetAddend</c>. Returns false
    /// when (a) we're in per-instr mode (no pipeline constant set), or
    /// (b) the index can't be statically determined to be PC.
    /// <paramref name="pcOffsetAddend"/> is for ARM ARM A5.1.5's "PC reads
    /// as +12 in shift-by-register form": pass +4 to get +12 instead of +8.
    /// </summary>
    internal static unsafe bool TryGetStaticPcReadConstant(
        EmitContext ctx, JsonElement indexExpr, uint pcOffsetAddend, out LLVMValueRef pcConst)
    {
        pcConst = default;
        if (ctx.PipelinePcConstant is not uint pipelineValue) return false;
        // Phase 7 A.6.1 — if the current instruction has already written PC
        // (e.g., block_load with bit 15 in list, or a prior write_reg(15)),
        // a subsequent read_reg(15) within the same instruction must see
        // the just-written value from memory, NOT the static pipeline
        // constant. Otherwise patterns like Thumb POP {PC}'s alignment
        // step (read_reg 15; and 0xFFFFFFFE; write_reg 15) overwrite the
        // popped PC with the pipeline constant. Caused BIOS LLE BJIT
        // divergence at first POP {R4,R5,PC}.
        if (ctx.PcWriteEmittedInCurrentInstruction) return false;
        var pcIdx = ctx.Layout.RegisterFile.GeneralPurpose.PcIndex;
        if (pcIdx is not int pc) return false;

        // Literal index.
        if (indexExpr.ValueKind == JsonValueKind.Number)
        {
            if (indexExpr.GetInt32() != pc) return false;
            pcConst = ctx.ConstU32(pipelineValue + pcOffsetAddend);
            return true;
        }
        if (indexExpr.ValueKind != JsonValueKind.String) return false;

        var name = indexExpr.GetString()!;
        var maskBits = NextPowerOfTwoMinusOne(ctx.Layout.GprCount);

        // Path A: name is a format field → static decode of the constant
        // instruction word.
        if (ctx.Format.Fields.TryGetValue(name, out var range))
        {
            var instrConst = LLVM.IsAConstantInt(ctx.Instruction);
            if (instrConst == null) return false;
            var instrWord = (uint)LLVM.ConstIntGetZExtValue(instrConst);
            var idxValue = ((instrWord >> range.Low) & range.LowMask) & maskBits;
            if (idxValue != (uint)pc) return false;
            pcConst = ctx.ConstU32(pipelineValue + pcOffsetAddend);
            return true;
        }

        // Path B: name is a step output (e.g. Thumb f5's eff_rs computed
        // via shl+or of constant fields). If LLVM IRBuilder constant-
        // folded the result, ctx.Values[name] is a ConstantInt — extract
        // and compare. Without this, dynamic-but-effectively-constant
        // index reads of R15 fall through to the runtime ICmp+Select
        // fallback (correct but slow).
        if (ctx.Values.TryGetValue(name, out var resolvedVal))
        {
            var valConst = LLVM.IsAConstantInt(resolvedVal);
            if (valConst != null)
            {
                var idxValue = ((uint)LLVM.ConstIntGetZExtValue(valConst)) & maskBits;
                if (idxValue == (uint)pc)
                {
                    pcConst = ctx.ConstU32(pipelineValue + pcOffsetAddend);
                    return true;
                }
                // Constant but NOT PC — return false; caller emits the
                // normal load path. Skip the runtime fallback ICmp+Select
                // since we know statically it's not PC (will be caught
                // by IRBuilder's constant folding of the runtime cmp anyway,
                // but making it explicit avoids wasted IR).
            }
        }
        return false;
    }

    internal static unsafe LLVMValueRef ResolveRegPtr(EmitContext ctx, JsonElement indexExpr)
    {
        // index can be a string (field name) or integer (literal 0..N-1).
        if (indexExpr.ValueKind == JsonValueKind.Number)
        {
            var lit = indexExpr.GetInt32();
            return ctx.Layout.GepGpr(ctx.Builder, ctx.StatePtr, lit);
        }
        if (indexExpr.ValueKind == JsonValueKind.String)
        {
            var name = indexExpr.GetString()!;
            // Field-name → runtime extraction → dynamic GEP. Mask is sized
            // to the GPR count, rounded up to a power of 2 (so a 16-GPR
            // ARM file uses 0xF, an 8-entry encoding field on a 7-GPR file
            // also uses 0x7 — value 6 is "(HL) memory" which the LR35902-
            // specific emitter handles).
            var idx = ctx.Resolve(name);
            var maskBits = NextPowerOfTwoMinusOne(ctx.Layout.GprCount);
            var masked = ctx.Builder.BuildAnd(idx, ctx.ConstU32(maskBits), $"{name}_idx");
            // Phase 7 A.6.1 — when the masked index is a JIT-time constant
            // (typical in block-JIT where instruction word is baked in),
            // produce a struct-typed GEP so LLVM's alias analysis sees the
            // pointer with the same {struct}-element type as fixed-index
            // GepGpr writes. Otherwise mismatched GEP types (i32-array
            // vs struct-field) cause LLVM to assume no aliasing → codegen
            // can use a stale register value across a memory write to the
            // same byte address. Observed in Thumb POP {R3}; BX R3 path:
            // POP wrote R3 via struct GEP, BX read R3 via array GEP, BX
            // got the pre-POP register value (0) instead of the popped
            // 0x895 — eventually triggered UND in BIOS LLE.
            var maskedConst = LLVM.IsAConstantInt(masked);
            if (maskedConst != null)
            {
                int litIdx = (int)LLVM.ConstIntGetSExtValue(maskedConst);
                if (litIdx >= 0 && litIdx < ctx.Layout.GprCount)
                    return ctx.Layout.GepGpr(ctx.Builder, ctx.StatePtr, litIdx);
            }
            return ctx.Layout.GepGprDynamic(ctx.Builder, ctx.StatePtr, masked);
        }
        throw new InvalidOperationException("read_reg/write_reg 'index' must be string or number.");
    }

    private static uint NextPowerOfTwoMinusOne(int count)
    {
        if (count <= 1) return 0;
        uint v = 1;
        while (v < (uint)count) v <<= 1;
        return v - 1;
    }
}

internal sealed class WriteReg : IMicroOpEmitter
{
    public string OpName => "write_reg";
    public unsafe void Emit(EmitContext ctx, MicroOpStep step)
    {
        var indexExpr = step.Raw.GetProperty("index");
        var valueName = step.Raw.GetProperty("value").GetString()!;
        var value     = ctx.Resolve(valueName);

        var ptr = ReadReg.ResolveRegPtr(ctx, indexExpr);
        ctx.Builder.BuildStore(value, ptr);

        // Phase 7 A.6.1 — static-analysis PC-write detection. When the
        // instruction word is a baked-in constant (block-JIT mode), we
        // can extract the Rd field at JIT time and unconditionally mark
        // PcWritten=1 if Rd resolves to PC. mGBA's dynarec uses the same
        // pattern (special "DPPC" emitter for data-processing→PC).
        // Per-instr mode keeps the legacy backup check
        // (executor's `postR15 != pcReadValue`) so we only emit the mark
        // when needed for correctness.
        StaticallyMarkPcWrittenIfNeeded(ctx, indexExpr);
    }

    internal static void MarkPcWritten(EmitContext ctx)
    {
        // Phase 7 A.6.1 — track that this instruction has touched PC, so
        // any subsequent read_reg(15) within the same instruction reads
        // the just-written value from memory instead of short-circuiting
        // to the static pipeline constant.
        ctx.PcWriteEmittedInCurrentInstruction = true;
        var slot = ctx.Layout.GepPcWritten(ctx.Builder, ctx.StatePtr);
        ctx.Builder.BuildStore(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, 1, false), slot);
    }

    /// <summary>
    /// Decode the register index AT JIT TIME (not at runtime). When the
    /// instruction word is a constant in IR (block-JIT bakes it as
    /// <c>ConstU32(bi.InstructionWord)</c>), we can extract the Rd field
    /// from the spec format's bit range and check against PC index. If
    /// Rd resolves to PC, emit an unconditional <see cref="MarkPcWritten"/>
    /// — the block exit logic will then correctly route to block_exit
    /// instead of advance.
    /// </summary>
    internal static unsafe void StaticallyMarkPcWrittenIfNeeded(EmitContext ctx, System.Text.Json.JsonElement indexExpr)
    {
        var pcIdx = ctx.Layout.RegisterFile.GeneralPurpose.PcIndex;
        if (pcIdx is not int pc) return;   // CPUs without GPR-resident PC (LR35902) — N/A

        // Literal index: trivial check.
        if (indexExpr.ValueKind == System.Text.Json.JsonValueKind.Number)
        {
            if (indexExpr.GetInt32() == pc) MarkPcWritten(ctx);
            return;
        }
        if (indexExpr.ValueKind != System.Text.Json.JsonValueKind.String) return;

        var name = indexExpr.GetString()!;
        var maskBits = NextPowerOfTwoMinusOne(ctx.Layout.GprCount);

        // Path A: name is a format field — static decode of constant
        // instruction word.
        if (ctx.Format.Fields.TryGetValue(name, out var range))
        {
            var instrConst = LLVM.IsAConstantInt(ctx.Instruction);
            if (instrConst == null) return;
            var instrWord = (uint)LLVM.ConstIntGetZExtValue(instrConst);
            var idxValue = ((instrWord >> range.Low) & range.LowMask) & maskBits;
            if (idxValue == (uint)pc) MarkPcWritten(ctx);
            return;
        }

        // Path B: name is a step output (e.g. Thumb f5 hi-reg ops'
        // eff_rd computed via shl+or of constant fields). LLVM IRBuilder
        // const-folds shl/or chains, so the resolved Value can be a
        // ConstantInt — check that. Without this, Thumb f5 ADD/MOV with
        // dest=R15 (e.g. `MOV PC, Rs` for indirect branch) silently
        // drops the PC write under Strategy 2 because no marker fires.
        if (ctx.Values.TryGetValue(name, out var resolvedVal))
        {
            var valConst = LLVM.IsAConstantInt(resolvedVal);
            if (valConst != null)
            {
                var idxValue = ((uint)LLVM.ConstIntGetZExtValue(valConst)) & maskBits;
                if (idxValue == (uint)pc) MarkPcWritten(ctx);
            }
        }
    }

    private static uint NextPowerOfTwoMinusOne(int count)
    {
        if (count <= 1) return 0;
        uint v = 1;
        while (v < (uint)count) v <<= 1;
        return v - 1;
    }
}

/// <summary>
/// Variant of <c>read_reg</c> for the ARM data-processing shift-by-
/// register form, where ARM ARM A5.1.5 specifies that PC reads as
/// <c>address + 12</c> instead of the usual <c>+8</c> (the register
/// shift takes one extra pipeline cycle). For non-PC indices behaves
/// exactly like <c>read_reg</c>.
/// </summary>
internal sealed class ReadRegShiftByReg : IMicroOpEmitter
{
    public string OpName => "read_reg_shift_by_reg";
    public unsafe void Emit(EmitContext ctx, MicroOpStep step)
    {
        var indexExpr = step.Raw.GetProperty("index");
        var outName   = StandardEmitters.GetOut(step.Raw);

        // Phase 7 A.6.1 Strategy 2 — static PC redirection with the +4
        // shift-by-reg adjustment (ARM ARM A5.1.5: PC reads as +12 in
        // this form, vs +8 in normal data-processing).
        if (ReadReg.TryGetStaticPcReadConstant(ctx, indexExpr, pcOffsetAddend: 4u, out var pcConst))
        {
            ctx.Values[outName] = pcConst;
            return;
        }

        // Resolve index (literal or runtime field).
        var raw = ctx.Builder.BuildLoad2(ctx.Layout.GprType,
                       ReadReg.ResolveRegPtr(ctx, indexExpr), $"{outName}_raw");

        var pcIndex = ctx.Layout.RegisterFile.GeneralPurpose.PcIndex;
        if (pcIndex is int pcIdx)
        {
            // Determine "is this PC?" — for literal index this is a
            // compile-time constant, but the IRBuilder folds trivially.
            LLVMValueRef isPc;
            if (indexExpr.ValueKind == System.Text.Json.JsonValueKind.Number)
            {
                isPc = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1,
                          indexExpr.GetInt32() == pcIdx ? 1u : 0u, false);
            }
            else
            {
                var idxName = indexExpr.GetString()!;
                var idx     = ctx.Resolve(idxName);
                // For ARM (16 GPRs), index is 4-bit so mask is 0xF.
                var masked  = ctx.Builder.BuildAnd(idx, ctx.ConstU32(0xF), $"{idxName}_pc_chk");
                isPc = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, masked,
                          ctx.ConstU32((uint)pcIdx), "is_pc");
            }
            var plus4 = ctx.Builder.BuildAdd(raw, ctx.ConstU32(4), $"{outName}_plus4");
            ctx.Values[outName] = ctx.Builder.BuildSelect(isPc, plus4, raw, outName);
        }
        else
        {
            ctx.Values[outName] = raw;
        }
    }
}

// ---------------- generic binary op ----------------

internal sealed class Binary : IMicroOpEmitter
{
    public string OpName { get; }
    private readonly Func<LLVMBuilderRef, LLVMValueRef, LLVMValueRef, string, LLVMValueRef> _build;

    public Binary(string opName,
                  Func<LLVMBuilderRef, LLVMValueRef, LLVMValueRef, string, LLVMValueRef> build)
    {
        OpName = opName;
        _build = build;
    }

    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var lhs = StandardEmitters.ResolveInput(ctx, step.Raw, 0);
        var rhs = StandardEmitters.ResolveInput(ctx, step.Raw, 1);
        // Auto-coerce widths via zero-extension when one input is narrower.
        // Pre-Step 5.5 this was a hard error — required for LR35902 IO
        // address composition where i8 offsets get OR'd with i32 page
        // bases (e.g. LDH offset | 0xFF00). Safe for add/sub/and/or/xor:
        // result mod 2^N is unchanged. ARM specs already use uniform i32
        // operands so this is a no-op for them.
        if (lhs.TypeOf != rhs.TypeOf)
        {
            var lw = lhs.TypeOf.IntWidth;
            var rw = rhs.TypeOf.IntWidth;
            if (lw < rw) lhs = ctx.Builder.BuildZExt(lhs, rhs.TypeOf, "binop_lhs_zx");
            else         rhs = ctx.Builder.BuildZExt(rhs, lhs.TypeOf, "binop_rhs_zx");
        }
        var outName = StandardEmitters.GetOut(step.Raw);
        var result  = _build(ctx.Builder, lhs, rhs, outName);
        ctx.Values[outName] = result;
    }
}

// ---------------- flag updates ----------------

/// <summary>Common helpers for writing CPSR bits.</summary>
internal static class CpsrHelpers
{
    /// <summary>
    /// Set a single bit by spec-defined flag name (e.g. ("CPSR","N")).
    /// Looks up the bit position via <see cref="CpuStateLayout.GetStatusFlagBitIndex"/>
    /// so no ARM-specific constants are baked in.
    /// </summary>
    public static void SetStatusFlag(EmitContext ctx, string register, string flag, LLVMValueRef boolValue)
        => SetStatusFlagAt(ctx, register, ctx.Layout.GetStatusFlagBitIndex(register, flag), boolValue, flag.ToLowerInvariant());

    /// <summary>
    /// Phase 7 A.2 (recovery branch) — no-op stub. C.b retry adds real
    /// alloca-shadow draining; until then there are no shadow allocas to
    /// flush, so this is just a placeholder so BlockFunctionBuilder
    /// compiles cleanly.
    /// </summary>
    public static void DrainAllShadows(EmitContext ctx) { /* no-op until C.b */ }

    public static void SetStatusFlagFromI32Lsb(EmitContext ctx, string register, string flag, LLVMValueRef i32Value)
        => SetStatusFlagAtFromI32Lsb(ctx, register, ctx.Layout.GetStatusFlagBitIndex(register, flag), i32Value, flag.ToLowerInvariant());

    /// <summary>Convenience: read CPSR (or any status reg) bit as i32 0/1.</summary>
    public static LLVMValueRef ReadStatusFlag(EmitContext ctx, string register, string flag)
    {
        var bitIndex = ctx.Layout.GetStatusFlagBitIndex(register, flag);
        var ptr = ctx.Layout.GepStatusRegister(ctx.Builder, ctx.StatePtr, register);
        // Phase 7 C.a: load at the status register's actual width.
        // Pre-7.C.a always loaded i32; for LR35902 F (i8) that read 4 bytes
        // spanning F + adjacent SP/PC fields. Width-correct loads/stores
        // let LLVM combine consecutive flag updates into a single store
        // (no aliasing-to-other-fields concerns).
        var (statusType, _) = StatusTypeAndAllOnes(ctx, register);
        var v   = ctx.Builder.BuildLoad2(statusType, ptr, $"{register.ToLowerInvariant()}_for_{flag.ToLowerInvariant()}");
        var sh  = ctx.Builder.BuildLShr(v, LLVMValueRef.CreateConstInt(statusType, (uint)bitIndex, false), $"{flag.ToLowerInvariant()}_shr");
        var bit = ctx.Builder.BuildAnd(sh, LLVMValueRef.CreateConstInt(statusType, 1, false), $"{flag.ToLowerInvariant()}_bit");
        // Callers expect i32 (existing emitters do arithmetic with these
        // as i32). Widen if needed.
        return statusType == LLVMTypeRef.Int32
            ? bit
            : ctx.Builder.BuildZExt(bit, LLVMTypeRef.Int32, $"{flag.ToLowerInvariant()}_z32");
    }

    private static void SetStatusFlagAt(EmitContext ctx, string register, int bitIndex, LLVMValueRef boolValue, string label)
    {
        var ptr  = ctx.Layout.GepStatusRegister(ctx.Builder, ctx.StatePtr, register);
        // Phase 7 C.a: width-correct load+store (was always i32).
        var (statusType, allOnes) = StatusTypeAndAllOnes(ctx, register);
        var prev = ctx.Builder.BuildLoad2(statusType, ptr, $"{register.ToLowerInvariant()}_old");

        var asI    = ctx.Builder.BuildZExt(boolValue, statusType, $"{label}_asi");
        var shifted = ctx.Builder.BuildShl(asI, LLVMValueRef.CreateConstInt(statusType, (uint)bitIndex, false), $"{label}_shifted");

        // ~(1 << bitIndex) at the right width so LLVM sees the constant
        // as the real mask, not an i32 with extra high bits set.
        var clearMask = LLVMValueRef.CreateConstInt(statusType, allOnes ^ (1UL << bitIndex), false);
        var cleared   = ctx.Builder.BuildAnd(prev, clearMask, $"{register.ToLowerInvariant()}_clear_{label}");
        var newV      = ctx.Builder.BuildOr (cleared, shifted, $"{register.ToLowerInvariant()}_set_{label}");
        ctx.Builder.BuildStore(newV, ptr);
    }

    private static void SetStatusFlagAtFromI32Lsb(EmitContext ctx, string register, int bitIndex, LLVMValueRef i32Value, string label)
    {
        var lowBit = ctx.Builder.BuildAnd(i32Value, ctx.ConstU32(1), $"{label}_lsb");
        var asBool = ctx.Builder.BuildTrunc(lowBit, LLVMTypeRef.Int1, $"{label}_b");
        SetStatusFlagAt(ctx, register, bitIndex, asBool, label);
    }

    /// <summary>
    /// Phase 7 C.a — pick the LLVM int type matching the status register's
    /// declared width plus an all-ones mask of that width (for inverted
    /// bit-clear masks).
    /// </summary>
    private static (LLVMTypeRef Type, ulong AllOnes) StatusTypeAndAllOnes(EmitContext ctx, string register)
    {
        var def = ctx.Layout.GetStatusRegisterDef(register);
        return def.WidthBits switch
        {
            8  => (LLVMTypeRef.Int8,  0xFFUL),
            16 => (LLVMTypeRef.Int16, 0xFFFFUL),
            32 => (LLVMTypeRef.Int32, 0xFFFFFFFFUL),
            _  => throw new NotSupportedException($"status register '{register}' width {def.WidthBits}-bit unsupported")
        };
    }


}

// (UpdateNz / UpdateC*/UpdateV*/Update*Carry / Adc/Sbc/Rsc / ReadPsr /
// WritePsr / RestoreCpsrFromSpsr / CarryReader were moved to ArmEmitters.cs
// in R5.)



internal sealed class MvnEmitter : IMicroOpEmitter
{
    public string OpName => "mvn";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var v = StandardEmitters.ResolveInput(ctx, step.Raw, 0);
        // Use all-ones at the input value's native width — pre-Step 5.7.A
        // this was hardcoded to i32, which broke for i8 inputs (LR35902 CPL
        // would produce a wrong-width XOR result).
        var allOnes = LLVMValueRef.CreateConstInt(v.TypeOf, ulong.MaxValue, false);
        var res = ctx.Builder.BuildXor(v, allOnes, StandardEmitters.GetOut(step.Raw));
        ctx.Values[StandardEmitters.GetOut(step.Raw)] = res;
    }
}

internal sealed class BicEmitter : IMicroOpEmitter
{
    public string OpName => "bic";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var a = StandardEmitters.ResolveInput(ctx, step.Raw, 0);
        var b = StandardEmitters.ResolveInput(ctx, step.Raw, 1);
        var notB = ctx.Builder.BuildXor(b, ctx.ConstU32(0xFFFFFFFFu), "bic_notb");
        var res = ctx.Builder.BuildAnd(a, notB, StandardEmitters.GetOut(step.Raw));
        ctx.Values[StandardEmitters.GetOut(step.Raw)] = res;
    }
}

// ---------------- 64-bit multiply-long support ----------------

/// <summary>
/// 32x32 → 64 widening multiply. Inputs are zero/sign-extended to i64
/// then multiplied; the i64 result is cached under the step's `out` name.
/// </summary>
internal sealed class MulLongEmitter : IMicroOpEmitter
{
    public string OpName { get; }
    private readonly bool _signed;

    public MulLongEmitter(string opName, bool signed)
    {
        OpName  = opName;
        _signed = signed;
    }

    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var a = StandardEmitters.ResolveInput(ctx, step.Raw, 0);
        var b = StandardEmitters.ResolveInput(ctx, step.Raw, 1);
        var i64 = LLVMTypeRef.Int64;
        var aL = _signed
            ? ctx.Builder.BuildSExt(a, i64, $"{OpName}_a_l")
            : ctx.Builder.BuildZExt(a, i64, $"{OpName}_a_l");
        var bL = _signed
            ? ctx.Builder.BuildSExt(b, i64, $"{OpName}_b_l")
            : ctx.Builder.BuildZExt(b, i64, $"{OpName}_b_l");
        var outName = StandardEmitters.GetOut(step.Raw);
        var res = ctx.Builder.BuildMul(aL, bL, outName);
        ctx.Values[outName] = res;
    }
}

/// <summary>64-bit add (i64 in, i64 out). Used by accumulating multiplies.</summary>
internal sealed class AddI64Emitter : IMicroOpEmitter
{
    public string OpName => "add_i64";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var a = StandardEmitters.ResolveInput(ctx, step.Raw, 0);
        var b = StandardEmitters.ResolveInput(ctx, step.Raw, 1);
        var outName = StandardEmitters.GetOut(step.Raw);
        var res = ctx.Builder.BuildAdd(a, b, outName);
        ctx.Values[outName] = res;
    }
}

/// <summary>
/// Reads two GPRs and combines them as <c>(hi &lt;&lt; 32) | lo</c>,
/// returning an i64 named value. Used by accumulating long multiplies
/// (UMLAL/SMLAL) to fold the existing RdHi:RdLo into the multiplier.
///
/// Step shape:
/// <code>
///   { "op": "read_reg_pair_u64",
///     "lo_index": "rd_lo" | 0..15,
///     "hi_index": "rd_hi" | 0..15,
///     "out": &lt;name&gt; }
/// </code>
/// </summary>
internal sealed class ReadRegPairU64Emitter : IMicroOpEmitter
{
    public string OpName => "read_reg_pair_u64";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var loIdxExpr = step.Raw.GetProperty("lo_index");
        var hiIdxExpr = step.Raw.GetProperty("hi_index");
        var outName   = StandardEmitters.GetOut(step.Raw);

        var loPtr = ReadReg.ResolveRegPtr(ctx, loIdxExpr);
        var hiPtr = ReadReg.ResolveRegPtr(ctx, hiIdxExpr);
        var lo32  = ctx.Builder.BuildLoad2(LLVMTypeRef.Int32, loPtr, $"{outName}_lo32");
        var hi32  = ctx.Builder.BuildLoad2(LLVMTypeRef.Int32, hiPtr, $"{outName}_hi32");

        var i64 = LLVMTypeRef.Int64;
        var loZ = ctx.Builder.BuildZExt(lo32, i64, $"{outName}_lo64");
        var hiZ = ctx.Builder.BuildZExt(hi32, i64, $"{outName}_hi64");
        var hiShift = ctx.Builder.BuildShl(hiZ,
            LLVMValueRef.CreateConstInt(i64, 32, false), $"{outName}_hi_shifted");
        var combined = ctx.Builder.BuildOr(loZ, hiShift, outName);
        ctx.Values[outName] = combined;
    }
}

/// <summary>
/// Splits an i64 value into low/high 32-bit halves and writes each to a
/// GPR. Step shape:
/// <code>
///   { "op": "write_reg_pair",
///     "lo_index": "rd_lo" | 0..15,
///     "hi_index": "rd_hi" | 0..15,
///     "value":    &lt;i64-name&gt; }
/// </code>
/// </summary>
internal sealed class WriteRegPairEmitter : IMicroOpEmitter
{
    public string OpName => "write_reg_pair";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var loIdxExpr = step.Raw.GetProperty("lo_index");
        var hiIdxExpr = step.Raw.GetProperty("hi_index");
        var valueName = step.Raw.GetProperty("value").GetString()!;
        var v64 = ctx.Resolve(valueName);

        var i64 = LLVMTypeRef.Int64;
        var lo32 = ctx.Builder.BuildTrunc(v64, LLVMTypeRef.Int32, $"{valueName}_lo");
        var hi64 = ctx.Builder.BuildLShr(v64,
            LLVMValueRef.CreateConstInt(i64, 32, false), $"{valueName}_hi64");
        var hi32 = ctx.Builder.BuildTrunc(hi64, LLVMTypeRef.Int32, $"{valueName}_hi");

        var loPtr = ReadReg.ResolveRegPtr(ctx, loIdxExpr);
        var hiPtr = ReadReg.ResolveRegPtr(ctx, hiIdxExpr);
        ctx.Builder.BuildStore(lo32, loPtr);
        ctx.Builder.BuildStore(hi32, hiPtr);
    }
}

// ---------------- control flow ----------------

internal sealed class IfStep : IMicroOpEmitter
{
    public string OpName => "if";
    public unsafe void Emit(EmitContext ctx, MicroOpStep step)
    {
        var cond = CondExpr.Parse(step.Raw.GetProperty("cond"));
        var condVal = cond.Lower(ctx);

        // Phase 7 H.a-instcombine fix — when cond is a JIT-time constant
        // (typical in block-JIT where instruction word is baked in and
        // field extraction const-folds), emit ONLY the taken branch's
        // body inline. This avoids generating dead basic blocks that
        // contain switch instructions on (later-poisoned) operands —
        // such patterns trigger an LLVM instcombine UB-propagation bug
        // where poison input to the switch in the dead branch taints
        // the live path's reachability analysis. Observed symptom:
        // BlockFunctionBuilderTests had R-register stores eliminated.
        var condConst = LLVM.IsAConstantInt(condVal);
        if (condConst != null)
        {
            ulong v = LLVM.ConstIntGetZExtValue(condConst);
            if (v != 0)
            {
                // cond is statically TRUE — emit then body inline,
                // skip else entirely.
                EmitNestedSteps(ctx, step.Raw.GetProperty("then"));
            }
            else if (step.Raw.TryGetProperty("else", out var elseEl))
            {
                // cond is statically FALSE with else branch — emit else inline.
                EmitNestedSteps(ctx, elseEl);
            }
            // else cond=false with no else → emit nothing
            return;
        }

        var thenBlock = ctx.AppendBlock("if_then");
        var elseBlock = step.Raw.TryGetProperty("else", out _)
            ? ctx.AppendBlock("if_else") : (LLVMBasicBlockRef?)null;
        var endBlock  = ctx.AppendBlock("if_end");

        ctx.Builder.BuildCondBr(condVal, thenBlock, elseBlock ?? endBlock);

        // then arm
        ctx.Builder.PositionAtEnd(thenBlock);
        EmitNestedSteps(ctx, step.Raw.GetProperty("then"));
        if (!IsBlockTerminated(ctx)) ctx.Builder.BuildBr(endBlock);

        // else arm (if any)
        if (elseBlock.HasValue)
        {
            ctx.Builder.PositionAtEnd(elseBlock.Value);
            EmitNestedSteps(ctx, step.Raw.GetProperty("else"));
            if (!IsBlockTerminated(ctx)) ctx.Builder.BuildBr(endBlock);
        }

        // continuation
        ctx.Builder.PositionAtEnd(endBlock);
    }

    private static void EmitNestedSteps(EmitContext ctx, JsonElement steps)
    {
        // We need the registry. Stash a reference on EmitContext via a module-static.
        // Since we don't currently route registry through EmitContext, the
        // InstructionFunctionBuilder sets EmitContext.Values["__registry__"] is overkill.
        // Instead we keep registry on a thread-local set up by InstructionFunctionBuilder.
        var registry = EmitterContextHolder.CurrentRegistry
            ?? throw new InvalidOperationException("No active EmitterRegistry on this thread.");
        foreach (var stepEl in steps.EnumerateArray())
        {
            var op = stepEl.GetProperty("op").GetString()!;
            registry.EmitStep(ctx, new MicroOpStep(op, stepEl));
        }
    }

    private static bool IsBlockTerminated(EmitContext ctx)
    {
        var bb = ctx.Builder.InsertBlock;
        return bb.Terminator.Handle != IntPtr.Zero;
    }
}

/// <summary>
/// Pure-SSA conditional value selection — needed when a value defined
/// in two branches of an <c>if</c> would otherwise be used past the
/// branch (which violates LLVM SSA dominance). Lowers to a single
/// <c>select i1</c> instruction.
///
/// Step shape:
/// <code>
///   { "op": "select",
///     "cond":     { "field": "u_bit", "eq": 1 } | { "var": "x", "eq": 0 },
///     "if_true":  &lt;value-name&gt;,
///     "if_false": &lt;value-name&gt;,
///     "out":      &lt;name&gt; }
/// </code>
/// </summary>
internal sealed class SelectStep : IMicroOpEmitter
{
    public string OpName => "select";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var cond     = CondExpr.Parse(step.Raw.GetProperty("cond")).Lower(ctx);
        var trueVal  = ResolveValue(ctx, step.Raw, "if_true");
        var falseVal = ResolveValue(ctx, step.Raw, "if_false");
        var outName  = step.Raw.GetProperty("out").GetString()!;
        var result   = ctx.Builder.BuildSelect(cond, trueVal, falseVal, outName);
        ctx.Values[outName] = result;
    }

    // Accept either { "if_true": "name" } or { "if_true": { "const": 32 } }.
    private static LLVMValueRef ResolveValue(EmitContext ctx, JsonElement raw, string property)
    {
        var el = raw.GetProperty(property);
        if (el.ValueKind == JsonValueKind.String)
            return ctx.Resolve(el.GetString()!);
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("const", out var c))
        {
            uint v = c.ValueKind switch
            {
                JsonValueKind.Number => (uint)c.GetInt64(),
                JsonValueKind.String when c.GetString()!.StartsWith("0x") =>
                    Convert.ToUInt32(c.GetString()!.Substring(2), 16),
                _ => throw new InvalidOperationException("'const' must be a number or 0x-hex string")
            };
            return ctx.ConstU32(v);
        }
        throw new InvalidOperationException(
            $"select '{property}' must be a value name or {{\"const\": <n>}}; got {el.ValueKind}");
    }
}

/// <summary>
/// Generic branch kinds. ARM-specific BX (T-bit + alignment) lives as
/// <c>BranchIndirectArm</c> in <see cref="ArmEmitters"/>.
/// </summary>
internal enum BranchKind { Plain, Link }

internal sealed class Branch : IMicroOpEmitter
{
    public string OpName { get; }
    private readonly BranchKind _kind;

    public Branch(string opName, BranchKind kind)
    {
        OpName = opName;
        _kind  = kind;
    }

    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var targetName = step.Raw.GetProperty("target").GetString()!;
        var target = ctx.Resolve(targetName);

        // Locate PC via spec metadata so this op works for any CPU
        // (ARM R15 GPR, LR35902 PC status reg, future RISC-V x0+pc, ...).
        var (pcPtr, pcType) = StackOps.LocateProgramCounter(ctx);

        if (_kind == BranchKind.Link)
        {
            // ARM-style link: read R15 (= pc + offset), write back-adjusted
            // to R14. Other CPUs that want a "link register" semantics
            // (none we currently support) need a different op.
            //
            // Phase 7 A.6.1 Strategy 2 — in block-JIT mode use the
            // pipeline PC constant directly instead of loading GPR[15]
            // (which is no longer pre-set per instruction). Per-instr
            // mode keeps the GPR[15] load (executor's pre-set R15 is
            // valid). The arithmetic is the same: nextPc = r15 + (width
            // - pcOffsetBytes), which for ARM = (pc + 8) + (4 - 8) =
            // pc + 4 = next-instruction address.
            var width  = ctx.InstructionSet.WidthBits.Fixed!.Value / 8;
            var addend = (uint)(width - ctx.InstructionSet.PcOffsetBytes);
            LLVMValueRef nextPc;
            if (ctx.PipelinePcConstant is uint pipelineValue)
            {
                nextPc = ctx.ConstU32(pipelineValue + addend);
            }
            else
            {
                var r15Ptr = ctx.Layout.GepGpr(ctx.Builder, ctx.StatePtr, 15);
                var r15    = ctx.Builder.BuildLoad2(LLVMTypeRef.Int32, r15Ptr, "r15");
                nextPc = ctx.Builder.BuildAdd(r15, ctx.ConstU32(addend), "next_pc");
            }
            var lrPtr  = ctx.Layout.GepGpr(ctx.Builder, ctx.StatePtr, 14);
            ctx.Builder.BuildStore(nextPc, lrPtr);
        }

        // Coerce target to PC type (i32 for ARM, i16 for LR35902, etc.).
        var coerced = StackOps.CoerceToType(ctx, target, pcType, "branch_target");
        ctx.Builder.BuildStore(coerced, pcPtr);

        // Mark "branch taken" so the executor's post-step PC-advance logic
        // can distinguish "branch to target == pre-set R15" (e.g. Thumb
        // BCond +0) from "no branch happened". See PcWrittenFieldIndex.
        // Harmless for non-ARM CPUs (the byte slot exists but the executor
        // for those CPUs doesn't gate on it).
        var flagSlot = ctx.Layout.GepPcWritten(ctx.Builder, ctx.StatePtr);
        ctx.Builder.BuildStore(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, 1, false), flagSlot);
    }
}

/// <summary>
/// branch_cc — conditional branch. Reads a flag bit from a status
/// register, compares to expected value, and writes target to PC iff
/// condition holds. Uses LLVM <c>select</c> so the IR stays a single
/// basic block (matches existing LR35902 jp_cc / jr_cc shape).
///
/// Step shape:
/// <code>
/// {
///   "op": "branch_cc",
///   "target": "&lt;varname&gt;",
///   "cond": { "reg": "F", "flag": "Z", "value": 1 }
/// }
/// </code>
///
/// The cond <c>value</c> is the bit value that triggers the branch:
/// 1 = branch when flag set, 0 = branch when flag clear.
/// </summary>
internal sealed class BranchCc : IMicroOpEmitter
{
    public string OpName => "branch_cc";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var targetName = step.Raw.GetProperty("target").GetString()!;
        var target = ctx.Resolve(targetName);
        var pred   = StackOps.ResolveFlagCond(ctx, step.Raw.GetProperty("cond"));

        // Phase 7 GB block-JIT P0.4 — block-JIT mode: use PipelinePcConstant
        // (= bi.Pc + length, the next-instr PC) as the not-taken value
        // instead of loading stale PC from memory. Without this, not-taken
        // case writes the BLOCK START PC back to memory, causing infinite
        // loops at the first conditional branch in the block.
        var (pcPtr, pcType) = StackOps.LocateProgramCounter(ctx);
        LLVMValueRef curPc;
        if (ctx.PipelinePcConstant is uint pipelineValue)
            curPc = LLVMValueRef.CreateConstInt(pcType, pipelineValue, false);
        else
            curPc = ctx.Builder.BuildLoad2(pcType, pcPtr, "branch_cc_pc_cur");
        var coerced = StackOps.CoerceToType(ctx, target, pcType, "branch_cc_target");
        var chosen  = ctx.Builder.BuildSelect(pred, coerced, curPc, "branch_cc_chosen");
        ctx.Builder.BuildStore(chosen, pcPtr);

        // Mark PC-written when condition holds. Use select to choose
        // between "1 (taken)" and "0 (not taken)" for the flag.
        var flagSlot  = ctx.Layout.GepPcWritten(ctx.Builder, ctx.StatePtr);
        var oldFlag   = ctx.Builder.BuildLoad2(LLVMTypeRef.Int8, flagSlot, "pc_w_old");
        var taken     = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, 1, false);
        var pcWritten = ctx.Builder.BuildSelect(pred, taken, oldFlag, "pc_w_new");
        ctx.Builder.BuildStore(pcWritten, flagSlot);
    }
}

/// <summary>
/// read_pc { out } — load the current PC value (i16 or i32 depending
/// on the spec). Used by PC-relative branches like LR35902 JR e8 to
/// compose targets without an arch-specific op.
/// </summary>
internal sealed class ReadPc : IMicroOpEmitter
{
    public string OpName => "read_pc";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var outName = StandardEmitters.GetOut(step.Raw);

        // Phase 7 A.6.1 Strategy 2 — block-JIT mode uses pipeline constant.
        if (ctx.PipelinePcConstant is uint pipelineValue)
        {
            // PC type is whatever LocateProgramCounter would return.
            // For ARM that's i32 (GPR-resident PC); for LR35902 i16 (status reg).
            var (_, pcType) = StackOps.LocateProgramCounter(ctx);
            ctx.Values[outName] = LLVMValueRef.CreateConstInt(pcType, pipelineValue, false);
            return;
        }

        var (pcPtr, pcTypeRt) = StackOps.LocateProgramCounter(ctx);
        ctx.Values[outName] = ctx.Builder.BuildLoad2(pcTypeRt, pcPtr, outName);
    }
}

/// <summary>
/// trunc { in, width, out } — narrow an integer value to a smaller
/// bit width via low-bits extraction. Width must be 8/16/32/64.
/// Pass-through if input is already at the target width; errors if
/// narrowing isn't possible (caller should sext / zext to widen).
/// Sister to <see cref="SextEmitter"/>.
/// </summary>
internal sealed class TruncEmitter : IMicroOpEmitter
{
    public string OpName => "trunc";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var inName  = step.Raw.GetProperty("in").GetString()!;
        var outName = StandardEmitters.GetOut(step.Raw);
        var width   = step.Raw.GetProperty("width").GetInt32();
        var v = ctx.Resolve(inName);
        var t = width switch
        {
            8  => LLVMTypeRef.Int8,
            16 => LLVMTypeRef.Int16,
            32 => LLVMTypeRef.Int32,
            64 => LLVMTypeRef.Int64,
            _  => throw new NotSupportedException($"trunc width {width} not supported")
        };
        var srcW = v.TypeOf.IntWidth;
        if (srcW == width)      ctx.Values[outName] = v;
        else if (srcW > width)  ctx.Values[outName] = ctx.Builder.BuildTrunc(v, t, outName);
        else throw new InvalidOperationException($"trunc cannot widen i{srcW} to i{width} — use sext or zext");
    }
}

/// <summary>
/// sext { in, width, out } — sign-extend (or truncate / passthrough)
/// to the requested bit width. Width must be 8/16/32/64.
/// </summary>
internal sealed class SextEmitter : IMicroOpEmitter
{
    public string OpName => "sext";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var inName  = step.Raw.GetProperty("in").GetString()!;
        var outName = StandardEmitters.GetOut(step.Raw);
        var width   = step.Raw.GetProperty("width").GetInt32();
        var v = ctx.Resolve(inName);
        var t = width switch
        {
            8  => LLVMTypeRef.Int8,
            16 => LLVMTypeRef.Int16,
            32 => LLVMTypeRef.Int32,
            64 => LLVMTypeRef.Int64,
            _  => throw new NotSupportedException($"sext width {width} not supported")
        };
        var srcW = v.TypeOf.IntWidth;
        LLVMValueRef result;
        if (srcW == width)      result = v;
        else if (srcW > width)  result = ctx.Builder.BuildTrunc(v, t, outName);
        else                    result = ctx.Builder.BuildSExt(v, t, outName);
        ctx.Values[outName] = result;
    }
}

/// <summary>
/// Thread-local holder that lets nested emitters (like <c>if</c>'s `then`/`else`
/// arms) recursively dispatch through the same registry without threading it
/// through every signature.
/// </summary>
internal static class EmitterContextHolder
{
    [ThreadStatic] private static EmitterRegistry? _current;
    public static EmitterRegistry? CurrentRegistry => _current;
    public static IDisposable Push(EmitterRegistry reg)
    {
        var prev = _current;
        _current = reg;
        return new Pop(prev);
    }
    private sealed class Pop : IDisposable
    {
        private readonly EmitterRegistry? _prev;
        public Pop(EmitterRegistry? prev) { _prev = prev; }
        public void Dispose() { _current = _prev; }
    }
}
