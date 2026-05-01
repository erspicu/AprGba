using System.Text.Json;
using AprCpu.Core.JsonSpec;
using LLVMSharp.Interop;

namespace AprCpu.Core.IR;

/// <summary>
/// Helpers + the initial set of micro-op emitters. Just enough to cover
/// ARM7TDMI Data Processing Immediate, Branch and BX, plus the Thumb
/// shift-immediate / immediate-ALU formats.
/// </summary>
public static class StandardEmitters
{
    /// <summary>Register every emitter in this initial set into <paramref name="reg"/>.</summary>
    public static void RegisterAll(EmitterRegistry reg)
    {
        // Register access
        reg.Register(new ReadReg());
        reg.Register(new WriteReg());

        // Arithmetic / logical
        reg.Register(new Binary("add", (b, l, r, n) => b.BuildAdd(l, r, n)));
        reg.Register(new Binary("sub", (b, l, r, n) => b.BuildSub(l, r, n)));
        reg.Register(new Binary("and", (b, l, r, n) => b.BuildAnd(l, r, n)));
        reg.Register(new Binary("or",  (b, l, r, n) => b.BuildOr (l, r, n)));
        reg.Register(new Binary("xor", (b, l, r, n) => b.BuildXor(l, r, n)));
        reg.Register(new Binary("shl", (b, l, r, n) => b.BuildShl (l, r, n)));
        reg.Register(new Binary("lsr", (b, l, r, n) => b.BuildLShr(l, r, n)));
        reg.Register(new Binary("asr", (b, l, r, n) => b.BuildAShr(l, r, n)));
        // Reverse subtract: result = b - a (operands swapped)
        reg.Register(new Binary("rsb", (b, l, r, n) => b.BuildSub(r, l, n)));
        // BIC: a AND NOT b
        reg.Register(new BicEmitter());
        // Unary
        reg.Register(new MvnEmitter());
        // Carry-aware arithmetic (read CPSR.C as carry-in)
        reg.Register(new AdcEmitter());
        reg.Register(new SbcEmitter());
        reg.Register(new RscEmitter());

        // Flag updates (write into CPSR)
        reg.Register(new UpdateNz());
        reg.Register(new UpdateCAdd());
        reg.Register(new UpdateCSub());
        reg.Register(new UpdateVAdd());
        reg.Register(new UpdateVSub());
        reg.Register(new UpdateCShifter());
        reg.Register(new UpdateCAddCarry());
        reg.Register(new UpdateCSubCarry());

        // PSR access
        reg.Register(new ReadPsr());
        reg.Register(new WritePsr());

        // Control flow
        reg.Register(new IfStep());
        reg.Register(new Branch("branch",          BranchKind.Plain));
        reg.Register(new Branch("branch_link",     BranchKind.Link));
        reg.Register(new Branch("branch_indirect", BranchKind.Indirect));

        // Stub / placeholder
        reg.Register(new RestoreCpsrFromSpsr());
    }

    // ---------------- helpers ----------------

    /// <summary>Resolve "in"[i] entry — string name → value.</summary>
    internal static LLVMValueRef ResolveInput(EmitContext ctx, JsonElement step, int index, string defaultName = "in")
    {
        var arr = step.GetProperty(defaultName);
        var name = arr[index].GetString()!;
        return ctx.Resolve(name);
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
        var v   = ctx.Builder.BuildLoad2(LLVMTypeRef.Int32, ptr, outName);
        ctx.Values[outName] = v;
    }

    internal static LLVMValueRef ResolveRegPtr(EmitContext ctx, JsonElement indexExpr)
    {
        // index can be a string (field name) or integer (literal 0..15).
        if (indexExpr.ValueKind == JsonValueKind.Number)
        {
            var lit = indexExpr.GetInt32();
            return ctx.Layout.GepGpr(ctx.Builder, ctx.StatePtr, lit);
        }
        if (indexExpr.ValueKind == JsonValueKind.String)
        {
            var name = indexExpr.GetString()!;
            // Field-name → runtime extraction → dynamic GEP
            var idx = ctx.Resolve(name);
            var masked = ctx.Builder.BuildAnd(idx, ctx.ConstU32(0xF), $"{name}_idx");
            return ctx.Layout.GepGprDynamic(ctx.Builder, ctx.StatePtr, masked);
        }
        throw new InvalidOperationException("read_reg/write_reg 'index' must be string or number.");
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
    public static void SetCpsrBit(EmitContext ctx, int bitIndex, LLVMValueRef boolValue, string label)
    {
        // Load CPSR
        var cpsrPtr = ctx.Layout.GepCpsr(ctx.Builder, ctx.StatePtr);
        var cpsr    = ctx.Builder.BuildLoad2(LLVMTypeRef.Int32, cpsrPtr, "cpsr_old");

        // bit_value (i32) = zext(i1 boolValue) << bitIndex
        var asI32   = ctx.Builder.BuildZExt(boolValue, LLVMTypeRef.Int32, $"{label}_asi32");
        var shifted = ctx.Builder.BuildShl (asI32, ctx.ConstU32((uint)bitIndex), $"{label}_shifted");

        // mask out the old bit
        var clearMask = ctx.ConstU32(~(1u << bitIndex));
        var cleared = ctx.Builder.BuildAnd(cpsr, clearMask, $"cpsr_clear_{label}");
        var newCpsr = ctx.Builder.BuildOr (cleared, shifted, $"cpsr_set_{label}");
        ctx.Builder.BuildStore(newCpsr, cpsrPtr);
    }

    public static void SetCpsrBitFromI32Lsb(EmitContext ctx, int bitIndex, LLVMValueRef i32Value, string label)
    {
        // Treat low bit of i32Value as the bit to write.
        var lowBit = ctx.Builder.BuildAnd(i32Value, ctx.ConstU32(1), $"{label}_lsb");
        var asBool = ctx.Builder.BuildTrunc(lowBit, LLVMTypeRef.Int1, $"{label}_b");
        SetCpsrBit(ctx, bitIndex, asBool, label);
    }
}

internal sealed class UpdateNz : IMicroOpEmitter
{
    public string OpName => "update_nz";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var valueName = step.Raw.GetProperty("value").GetString()!;
        var value = ctx.Resolve(valueName);

        // N = value bit 31
        var nBool = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntSLT, value, ctx.ConstU32(0), "n_test");
        // Z = value == 0
        var zBool = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, value, ctx.ConstU32(0), "z_test");

        CpsrHelpers.SetCpsrBit(ctx, CpuStateLayout.CpsrBit_N, nBool, "n");
        CpsrHelpers.SetCpsrBit(ctx, CpuStateLayout.CpsrBit_Z, zBool, "z");
    }
}

internal sealed class UpdateCAdd : IMicroOpEmitter
{
    public string OpName => "update_c_add";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var a = StandardEmitters.ResolveInput(ctx, step.Raw, 0);
        var b = StandardEmitters.ResolveInput(ctx, step.Raw, 1);

        // Carry-out for unsigned add = (a + b) > MAX, equivalently (a + b) < a (wrap).
        var sum = ctx.Builder.BuildAdd(a, b, "c_add_sum");
        var cBool = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntULT, sum, a, "c_add_test");
        CpsrHelpers.SetCpsrBit(ctx, CpuStateLayout.CpsrBit_C, cBool, "c");
    }
}

internal sealed class UpdateCSub : IMicroOpEmitter
{
    public string OpName => "update_c_sub";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var a = StandardEmitters.ResolveInput(ctx, step.Raw, 0);
        var b = StandardEmitters.ResolveInput(ctx, step.Raw, 1);
        // ARM C for SUB = NOT borrow = (a >= b)
        var cBool = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntUGE, a, b, "c_sub_test");
        CpsrHelpers.SetCpsrBit(ctx, CpuStateLayout.CpsrBit_C, cBool, "c");
    }
}

internal sealed class UpdateVAdd : IMicroOpEmitter
{
    public string OpName => "update_v_add";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var a   = StandardEmitters.ResolveInput(ctx, step.Raw, 0);
        var b   = StandardEmitters.ResolveInput(ctx, step.Raw, 1);
        var res = StandardEmitters.ResolveInput(ctx, step.Raw, 2);
        // V = ((a XOR res) AND (b XOR res))[31]
        var aXor = ctx.Builder.BuildXor(a, res, "v_add_axor");
        var bXor = ctx.Builder.BuildXor(b, res, "v_add_bxor");
        var both = ctx.Builder.BuildAnd(aXor, bXor, "v_add_and");
        var top  = ctx.Builder.BuildLShr(both, ctx.ConstU32(31), "v_add_top");
        CpsrHelpers.SetCpsrBitFromI32Lsb(ctx, CpuStateLayout.CpsrBit_V, top, "v");
    }
}

internal sealed class UpdateVSub : IMicroOpEmitter
{
    public string OpName => "update_v_sub";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var a   = StandardEmitters.ResolveInput(ctx, step.Raw, 0);
        var b   = StandardEmitters.ResolveInput(ctx, step.Raw, 1);
        var res = StandardEmitters.ResolveInput(ctx, step.Raw, 2);
        // V = ((a XOR b) AND (a XOR res))[31]
        var ab   = ctx.Builder.BuildXor(a, b,   "v_sub_ab");
        var ares = ctx.Builder.BuildXor(a, res, "v_sub_ares");
        var both = ctx.Builder.BuildAnd(ab, ares, "v_sub_and");
        var top  = ctx.Builder.BuildLShr(both, ctx.ConstU32(31), "v_sub_top");
        CpsrHelpers.SetCpsrBitFromI32Lsb(ctx, CpuStateLayout.CpsrBit_V, top, "v");
    }
}

internal sealed class UpdateCShifter : IMicroOpEmitter
{
    public string OpName => "update_c_shifter";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        // step.in[0] = name of pre-computed shifter_carry_out (i32 0/1)
        var v = StandardEmitters.ResolveInput(ctx, step.Raw, 0);
        CpsrHelpers.SetCpsrBitFromI32Lsb(ctx, CpuStateLayout.CpsrBit_C, v, "c_shf");
    }
}

internal sealed class UpdateCAddCarry : IMicroOpEmitter
{
    public string OpName => "update_c_add_carry";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var a   = StandardEmitters.ResolveInput(ctx, step.Raw, 0);
        var b   = StandardEmitters.ResolveInput(ctx, step.Raw, 1);
        var cin = StandardEmitters.ResolveInput(ctx, step.Raw, 2); // i32 0/1

        // Carry-out = (a + b + cin) wraps past UInt32.MaxValue
        // Compute via 64-bit add and check bit 32.
        var i64 = LLVMTypeRef.Int64;
        var aL = ctx.Builder.BuildZExt(a,   i64, "a_l");
        var bL = ctx.Builder.BuildZExt(b,   i64, "b_l");
        var cL = ctx.Builder.BuildZExt(cin, i64, "c_l");
        var sum = ctx.Builder.BuildAdd(ctx.Builder.BuildAdd(aL, bL, "ab_l"), cL, "abc_l");
        var hi  = ctx.Builder.BuildLShr(sum, LLVMValueRef.CreateConstInt(i64, 32, false), "carry_hi");
        var hi32 = ctx.Builder.BuildTrunc(hi, LLVMTypeRef.Int32, "carry_hi_i32");
        CpsrHelpers.SetCpsrBitFromI32Lsb(ctx, CpuStateLayout.CpsrBit_C, hi32, "c_addc");
    }
}

internal sealed class UpdateCSubCarry : IMicroOpEmitter
{
    public string OpName => "update_c_sub_carry";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        // ARM SBC: result = a - b - !cin, C-out = NOT borrow
        // Equivalently, C-out = (a >= b + !cin)
        var a   = StandardEmitters.ResolveInput(ctx, step.Raw, 0);
        var b   = StandardEmitters.ResolveInput(ctx, step.Raw, 1);
        var cin = StandardEmitters.ResolveInput(ctx, step.Raw, 2);

        var notCin = ctx.Builder.BuildXor(cin, ctx.ConstU32(1), "not_cin");
        var bPlus  = ctx.Builder.BuildAdd(b, notCin, "b_plus");
        var cBool  = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntUGE, a, bPlus, "c_subc_test");
        CpsrHelpers.SetCpsrBit(ctx, CpuStateLayout.CpsrBit_C, cBool, "c_subc");
    }
}

// ---------------- additional ALU emitters ----------------

/// <summary>
/// Reads CPSR.C as an i32 (0 or 1) for use as a carry-in operand.
/// </summary>
internal static class CarryReader
{
    public static LLVMValueRef ReadCarryIn(EmitContext ctx)
    {
        var cpsrPtr = ctx.Layout.GepCpsr(ctx.Builder, ctx.StatePtr);
        var cpsr    = ctx.Builder.BuildLoad2(LLVMTypeRef.Int32, cpsrPtr, "cpsr_for_cin");
        var shifted = ctx.Builder.BuildLShr(cpsr, ctx.ConstU32(CpuStateLayout.CpsrBit_C), "cin_shr");
        return ctx.Builder.BuildAnd(shifted, ctx.ConstU32(1), "cin");
    }
}

internal sealed class AdcEmitter : IMicroOpEmitter
{
    public string OpName => "adc";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var a = StandardEmitters.ResolveInput(ctx, step.Raw, 0);
        var b = StandardEmitters.ResolveInput(ctx, step.Raw, 1);
        var cin = CarryReader.ReadCarryIn(ctx);
        var ab = ctx.Builder.BuildAdd(a, b, "adc_ab");
        var res = ctx.Builder.BuildAdd(ab, cin, StandardEmitters.GetOut(step.Raw));
        ctx.Values[StandardEmitters.GetOut(step.Raw)] = res;
        // Cache the carry-in for any subsequent flag update.
        ctx.Values["__carry_in__"] = cin;
    }
}

internal sealed class SbcEmitter : IMicroOpEmitter
{
    public string OpName => "sbc";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var a = StandardEmitters.ResolveInput(ctx, step.Raw, 0);
        var b = StandardEmitters.ResolveInput(ctx, step.Raw, 1);
        var cin = CarryReader.ReadCarryIn(ctx);
        var notCin = ctx.Builder.BuildXor(cin, ctx.ConstU32(1), "sbc_notcin");
        var ab = ctx.Builder.BuildSub(a, b, "sbc_ab");
        var res = ctx.Builder.BuildSub(ab, notCin, StandardEmitters.GetOut(step.Raw));
        ctx.Values[StandardEmitters.GetOut(step.Raw)] = res;
        ctx.Values["__carry_in__"] = cin;
    }
}

internal sealed class RscEmitter : IMicroOpEmitter
{
    public string OpName => "rsc";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        // RSC: result = b - a - !cin (operands swapped relative to SBC)
        var a = StandardEmitters.ResolveInput(ctx, step.Raw, 0);
        var b = StandardEmitters.ResolveInput(ctx, step.Raw, 1);
        var cin = CarryReader.ReadCarryIn(ctx);
        var notCin = ctx.Builder.BuildXor(cin, ctx.ConstU32(1), "rsc_notcin");
        var ba = ctx.Builder.BuildSub(b, a, "rsc_ba");
        var res = ctx.Builder.BuildSub(ba, notCin, StandardEmitters.GetOut(step.Raw));
        ctx.Values[StandardEmitters.GetOut(step.Raw)] = res;
        ctx.Values["__carry_in__"] = cin;
    }
}

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

// ---------------- PSR access ----------------

internal sealed class ReadPsr : IMicroOpEmitter
{
    public string OpName => "read_psr";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        // Phase 2.5 first iteration: only CPSR is supported; SPSR will be
        // handled once banked-register swap lands in 2.5.7.
        var which = step.Raw.TryGetProperty("which", out var w) ? w.GetString() : "CPSR";
        var outName = StandardEmitters.GetOut(step.Raw);
        if (which != "CPSR")
        {
            // Stub: just read CPSR for now.
        }
        var ptr = ctx.Layout.GepCpsr(ctx.Builder, ctx.StatePtr);
        var v = ctx.Builder.BuildLoad2(LLVMTypeRef.Int32, ptr, outName);
        ctx.Values[outName] = v;
    }
}

internal sealed class WritePsr : IMicroOpEmitter
{
    public string OpName => "write_psr";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        // For MSR. step.value = name of value to write; step.mask = field
        // mask (which CPSR bytes to update). Mask defaults to all bytes.
        var valueName = step.Raw.GetProperty("value").GetString()!;
        var value = ctx.Resolve(valueName);

        uint maskBits = 0xFFFFFFFFu;
        if (step.Raw.TryGetProperty("mask", out var mEl))
        {
            maskBits = mEl.ValueKind switch
            {
                JsonValueKind.Number => (uint)mEl.GetInt64(),
                JsonValueKind.String when mEl.GetString()!.StartsWith("0x") =>
                    Convert.ToUInt32(mEl.GetString()!.Substring(2), 16),
                _ => 0xFFFFFFFFu
            };
        }

        var which = step.Raw.TryGetProperty("which", out var w) ? w.GetString() : "CPSR";
        var ptr = ctx.Layout.GepCpsr(ctx.Builder, ctx.StatePtr);
        if (which != "CPSR")
        {
            // SPSR write — Phase 2.5 stub: still target CPSR slot.
        }

        if (maskBits == 0xFFFFFFFFu)
        {
            ctx.Builder.BuildStore(value, ptr);
        }
        else
        {
            var oldV = ctx.Builder.BuildLoad2(LLVMTypeRef.Int32, ptr, "psr_old");
            var keep = ctx.Builder.BuildAnd(oldV,  ctx.ConstU32(~maskBits), "psr_keep");
            var inV  = ctx.Builder.BuildAnd(value, ctx.ConstU32( maskBits), "psr_in");
            var newV = ctx.Builder.BuildOr (keep, inV, "psr_new");
            ctx.Builder.BuildStore(newV, ptr);
        }
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

internal enum BranchKind { Plain, Link, Indirect }

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

        if (_kind == BranchKind.Link)
        {
            // LR <- PC (next instruction) ; PC <- target
            var pcOff = ctx.ConstU32((uint)ctx.InstructionSet.PcOffsetBytes);
            var r15Ptr = ctx.Layout.GepGpr(ctx.Builder, ctx.StatePtr, 15);
            var r15    = ctx.Builder.BuildLoad2(LLVMTypeRef.Int32, r15Ptr, "r15");
            // Next-instruction PC = current R15 - pc_offset + instruction_size
            var width  = ctx.InstructionSet.WidthBits.Fixed!.Value / 8;
            var nextPc = ctx.Builder.BuildAdd(r15, ctx.ConstU32((uint)(width - ctx.InstructionSet.PcOffsetBytes)), "next_pc");
            var lrPtr  = ctx.Layout.GepGpr(ctx.Builder, ctx.StatePtr, 14);
            ctx.Builder.BuildStore(nextPc, lrPtr);
        }

        if (_kind == BranchKind.Indirect)
        {
            // BX semantics: target's bit 0 selects Thumb (CPSR.T <- bit 0); aligned target written to PC.
            var bit0 = ctx.Builder.BuildAnd(target, ctx.ConstU32(1), "bx_bit0");
            CpsrHelpers.SetCpsrBitFromI32Lsb(ctx, CpuStateLayout.CpsrBit_T, bit0, "t");

            var alignMask = ctx.ConstU32(0xFFFFFFFEu);
            var aligned   = ctx.Builder.BuildAnd(target, alignMask, "bx_aligned");
            target = aligned;
        }

        // Store target as new PC (R15)
        var pcSlot = ctx.Layout.GepGpr(ctx.Builder, ctx.StatePtr, 15);
        ctx.Builder.BuildStore(target, pcSlot);
    }
}

internal sealed class RestoreCpsrFromSpsr : IMicroOpEmitter
{
    public string OpName => "restore_cpsr_from_spsr";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        // Phase 2 stub: emit a no-op marker (extract+ignore CPSR). The
        // proper behaviour requires mode-aware SPSR selection; we will fill
        // it in once banked-register handling lands in a later phase.
        var cpsrPtr = ctx.Layout.GepCpsr(ctx.Builder, ctx.StatePtr);
        var _ = ctx.Builder.BuildLoad2(LLVMTypeRef.Int32, cpsrPtr, "cpsr_stub_load");
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
