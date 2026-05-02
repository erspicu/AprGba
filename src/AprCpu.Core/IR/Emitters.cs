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

        // Control flow (generic — branch_indirect is in ArmEmitters)
        reg.Register(new IfStep());
        reg.Register(new SelectStep());
        reg.Register(new Branch("branch",      BranchKind.Plain));
        reg.Register(new Branch("branch_link", BranchKind.Link));
        reg.Register(new BranchCc());
        reg.Register(new ReadPc());
        reg.Register(new SextEmitter());
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
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var indexExpr = step.Raw.GetProperty("index");
        var outName   = StandardEmitters.GetOut(step.Raw);

        var ptr = ResolveRegPtr(ctx, indexExpr);
        var v   = ctx.Builder.BuildLoad2(ctx.Layout.GprType, ptr, outName);
        ctx.Values[outName] = v;
    }

    internal static LLVMValueRef ResolveRegPtr(EmitContext ctx, JsonElement indexExpr)
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
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var indexExpr = step.Raw.GetProperty("index");
        var valueName = step.Raw.GetProperty("value").GetString()!;
        var value     = ctx.Resolve(valueName);

        var ptr = ReadReg.ResolveRegPtr(ctx, indexExpr);
        ctx.Builder.BuildStore(value, ptr);
    }

    internal static void MarkPcWritten(EmitContext ctx)
    {
        var slot = ctx.Layout.GepPcWritten(ctx.Builder, ctx.StatePtr);
        ctx.Builder.BuildStore(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, 1, false), slot);
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
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var indexExpr = step.Raw.GetProperty("index");
        var outName   = StandardEmitters.GetOut(step.Raw);

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

    public static void SetStatusFlagFromI32Lsb(EmitContext ctx, string register, string flag, LLVMValueRef i32Value)
        => SetStatusFlagAtFromI32Lsb(ctx, register, ctx.Layout.GetStatusFlagBitIndex(register, flag), i32Value, flag.ToLowerInvariant());

    /// <summary>Convenience: read CPSR (or any status reg) bit as i32 0/1.</summary>
    public static LLVMValueRef ReadStatusFlag(EmitContext ctx, string register, string flag)
    {
        var bitIndex = ctx.Layout.GetStatusFlagBitIndex(register, flag);
        var ptr = ctx.Layout.GepStatusRegister(ctx.Builder, ctx.StatePtr, register);
        var v   = ctx.Builder.BuildLoad2(LLVMTypeRef.Int32, ptr, $"{register.ToLowerInvariant()}_for_{flag.ToLowerInvariant()}");
        var sh  = ctx.Builder.BuildLShr(v, ctx.ConstU32((uint)bitIndex), $"{flag.ToLowerInvariant()}_shr");
        return ctx.Builder.BuildAnd(sh, ctx.ConstU32(1), $"{flag.ToLowerInvariant()}_bit");
    }

    private static void SetStatusFlagAt(EmitContext ctx, string register, int bitIndex, LLVMValueRef boolValue, string label)
    {
        var ptr  = ctx.Layout.GepStatusRegister(ctx.Builder, ctx.StatePtr, register);
        var prev = ctx.Builder.BuildLoad2(LLVMTypeRef.Int32, ptr, $"{register.ToLowerInvariant()}_old");

        var asI32   = ctx.Builder.BuildZExt(boolValue, LLVMTypeRef.Int32, $"{label}_asi32");
        var shifted = ctx.Builder.BuildShl (asI32, ctx.ConstU32((uint)bitIndex), $"{label}_shifted");

        var clearMask = ctx.ConstU32(~(1u << bitIndex));
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
        var res = ctx.Builder.BuildXor(v, ctx.ConstU32(0xFFFFFFFFu), StandardEmitters.GetOut(step.Raw));
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
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var cond = CondExpr.Parse(step.Raw.GetProperty("cond"));
        var condVal = cond.Lower(ctx);

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
            var r15Ptr = ctx.Layout.GepGpr(ctx.Builder, ctx.StatePtr, 15);
            var r15    = ctx.Builder.BuildLoad2(LLVMTypeRef.Int32, r15Ptr, "r15");
            var width  = ctx.InstructionSet.WidthBits.Fixed!.Value / 8;
            var nextPc = ctx.Builder.BuildAdd(
                r15, ctx.ConstU32((uint)(width - ctx.InstructionSet.PcOffsetBytes)), "next_pc");
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

        var (pcPtr, pcType) = StackOps.LocateProgramCounter(ctx);
        var curPc   = ctx.Builder.BuildLoad2(pcType, pcPtr, "branch_cc_pc_cur");
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
        var (pcPtr, pcType) = StackOps.LocateProgramCounter(ctx);
        ctx.Values[outName] = ctx.Builder.BuildLoad2(pcType, pcPtr, outName);
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
