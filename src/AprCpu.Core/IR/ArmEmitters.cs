using System.Text.Json;
using AprCpu.Core.JsonSpec;
using LLVMSharp.Interop;

namespace AprCpu.Core.IR;

/// <summary>
/// ARM-specific micro-op emitters. Registered separately from
/// <see cref="StandardEmitters"/> so non-ARM CPU specs need not pay for
/// (or be coupled to) ARM CPSR semantics, banked PSR, T-bit handling
/// in indirect branches, etc.
///
/// SpecCompiler decides whether to call <see cref="RegisterAll"/> based
/// on the spec's <c>architecture.family</c>.
/// </summary>
public static class ArmEmitters
{
    public static void RegisterAll(EmitterRegistry reg)
    {
        // Carry-aware arithmetic (read CPSR.C as carry-in)
        reg.Register(new AdcEmitter());
        reg.Register(new SbcEmitter());
        reg.Register(new RscEmitter());

        // Flag updates (target CPSR by name)
        reg.Register(new UpdateNz());
        reg.Register(new UpdateNz64());
        reg.Register(new UpdateCAdd());
        reg.Register(new UpdateCSub());
        reg.Register(new UpdateVAdd());
        reg.Register(new UpdateVSub());
        reg.Register(new UpdateCShifter());
        reg.Register(new UpdateCAddCarry());
        reg.Register(new UpdateCSubCarry());

        // By-register shift with carry-out + ARM7TDMI edge cases (Thumb F4)
        reg.Register(new LslWithCarryEmitter());
        reg.Register(new LsrWithCarryEmitter());
        reg.Register(new AsrWithCarryEmitter());
        reg.Register(new RorWithCarryEmitter());

        // PSR access
        reg.Register(new ReadPsr());
        reg.Register(new WritePsr());

        // BX-style indirect branch (T-bit selects ARM/Thumb)
        reg.Register(new BranchIndirectArm());

        // Exception entry (vectors). Phase 2.5.5 first-iteration stub.
        reg.Register(new RaiseExceptionEmitter());

        // Per-instruction condition gate (Thumb F16 conditional branch).
        reg.Register(new IfArmCondEmitter());

        // Stub / placeholder
        reg.Register(new RestoreCpsrFromSpsr());
    }
}

// ---------------- ARM-only flag updaters ----------------

/// <summary>
/// Sets the "negative" and "zero" flags in a status register based on
/// a value. Generic across CPUs that share the N+Z pair convention
/// (ARM CPSR.N+CPSR.Z, M68k CCR.N+CCR.Z, x86 EFLAGS.SF+EFLAGS.ZF).
/// Spec specifies which status register and which flag names; defaults
/// keep ARM7TDMI working unchanged ("CPSR" / "N" / "Z").
/// </summary>
internal sealed class UpdateNz : IMicroOpEmitter
{
    public string OpName => "update_nz";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var valueName = step.Raw.GetProperty("value").GetString()!;
        var value = ctx.Resolve(valueName);
        var reg   = step.Raw.TryGetProperty("reg",   out var r) ? r.GetString()! : "CPSR";
        var nFlag = step.Raw.TryGetProperty("n_flag", out var n) ? n.GetString()! : "N";
        var zFlag = step.Raw.TryGetProperty("z_flag", out var z) ? z.GetString()! : "Z";

        var nBool = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntSLT, value, ctx.ConstU32(0), "n_test");
        var zBool = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ,  value, ctx.ConstU32(0), "z_test");

        CpsrHelpers.SetStatusFlag(ctx, reg, nFlag, nBool);
        CpsrHelpers.SetStatusFlag(ctx, reg, zFlag, zBool);
    }
}

/// <summary>
/// 64-bit variant for UMULLS / SMULLS / UMLALS / SMLALS. Per ARM ARM
/// A4.1.124, the long-multiply S-bit form sets N to bit 63 of the
/// 64-bit result and Z to (result == 0); C and V are UNPREDICTABLE
/// on ARMv4 (we leave them untouched, which mGBA / no$gba both do).
/// </summary>
internal sealed class UpdateNz64 : IMicroOpEmitter
{
    public string OpName => "update_nz_64";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var valueName = step.Raw.GetProperty("value").GetString()!;
        var value = ctx.Resolve(valueName);
        var reg   = step.Raw.TryGetProperty("reg",   out var r) ? r.GetString()! : "CPSR";
        var nFlag = step.Raw.TryGetProperty("n_flag", out var n) ? n.GetString()! : "N";
        var zFlag = step.Raw.TryGetProperty("z_flag", out var z) ? z.GetString()! : "Z";

        var i64Zero = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, 0, false);
        var nBool = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntSLT, value, i64Zero, "n64_test");
        var zBool = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ,  value, i64Zero, "z64_test");

        CpsrHelpers.SetStatusFlag(ctx, reg, nFlag, nBool);
        CpsrHelpers.SetStatusFlag(ctx, reg, zFlag, zBool);
    }
}

internal sealed class UpdateCAdd : IMicroOpEmitter
{
    public string OpName => "update_c_add";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var a = StandardEmitters.ResolveInput(ctx, step.Raw, 0);
        var b = StandardEmitters.ResolveInput(ctx, step.Raw, 1);
        var reg  = step.Raw.TryGetProperty("reg",  out var r) ? r.GetString()! : "CPSR";
        var flag = step.Raw.TryGetProperty("flag", out var f) ? f.GetString()! : "C";
        var sum = ctx.Builder.BuildAdd(a, b, "c_add_sum");
        var cBool = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntULT, sum, a, "c_add_test");
        CpsrHelpers.SetStatusFlag(ctx, reg, flag, cBool);
    }
}

internal sealed class UpdateCSub : IMicroOpEmitter
{
    public string OpName => "update_c_sub";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var a = StandardEmitters.ResolveInput(ctx, step.Raw, 0);
        var b = StandardEmitters.ResolveInput(ctx, step.Raw, 1);
        var reg  = step.Raw.TryGetProperty("reg",  out var r) ? r.GetString()! : "CPSR";
        var flag = step.Raw.TryGetProperty("flag", out var f) ? f.GetString()! : "C";
        var cBool = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntUGE, a, b, "c_sub_test");
        CpsrHelpers.SetStatusFlag(ctx, reg, flag, cBool);
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
        var reg  = step.Raw.TryGetProperty("reg",  out var r) ? r.GetString()! : "CPSR";
        var flag = step.Raw.TryGetProperty("flag", out var f) ? f.GetString()! : "V";
        var aXor = ctx.Builder.BuildXor(a, res, "v_add_axor");
        var bXor = ctx.Builder.BuildXor(b, res, "v_add_bxor");
        var both = ctx.Builder.BuildAnd(aXor, bXor, "v_add_and");
        var top  = ctx.Builder.BuildLShr(both, ctx.ConstU32(31), "v_add_top");
        CpsrHelpers.SetStatusFlagFromI32Lsb(ctx, reg, flag, top);
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
        var reg  = step.Raw.TryGetProperty("reg",  out var r) ? r.GetString()! : "CPSR";
        var flag = step.Raw.TryGetProperty("flag", out var f) ? f.GetString()! : "V";
        var ab   = ctx.Builder.BuildXor(a, b,   "v_sub_ab");
        var ares = ctx.Builder.BuildXor(a, res, "v_sub_ares");
        var both = ctx.Builder.BuildAnd(ab, ares, "v_sub_and");
        var top  = ctx.Builder.BuildLShr(both, ctx.ConstU32(31), "v_sub_top");
        CpsrHelpers.SetStatusFlagFromI32Lsb(ctx, reg, flag, top);
    }
}

/// <summary>
/// Thumb F4-style by-register shift with full ARM7TDMI carry-out
/// semantics. Inputs: <c>in: [value, amount]</c>. Output:
/// <c>out: &lt;result-name&gt;</c>. Updates CPSR.C as a side effect.
/// Handles: amount==0 (preserve C, return value); 1..31 (normal);
/// 32 (LSL/LSR result=0, carry=bit0/bit31); &gt;32 (LSL/LSR result=0,
/// carry=0); ASR amount&gt;=32 (sign-fill, carry=bit31); ROR amount mod 32.
/// Used by Thumb F4 ALU shift opcodes.
/// </summary>
internal abstract class ShiftWithCarryEmitter : IMicroOpEmitter
{
    public abstract string OpName { get; }
    protected abstract (LLVMValueRef value, LLVMValueRef carry) Compute(
        EmitContext ctx, LLVMValueRef value, LLVMValueRef amountFull,
        LLVMValueRef amountIsZero);

    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var value  = StandardEmitters.ResolveInput(ctx, step.Raw, 0);
        var amount = StandardEmitters.ResolveInput(ctx, step.Raw, 1);
        var outName = StandardEmitters.GetOut(step.Raw);

        var amountFull = ctx.Builder.BuildAnd(amount, ctx.ConstU32(0xFF), "shf_amt_full");
        var amountIsZero = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, amountFull, ctx.ConstU32(0), "shf_amt_eq0");

        var (result, carry) = Compute(ctx, value, amountFull, amountIsZero);
        ctx.Values[outName] = result;
        CpsrHelpers.SetStatusFlagFromI32Lsb(ctx, "CPSR", "C", carry);
    }
}

internal sealed class LslWithCarryEmitter : ShiftWithCarryEmitter
{
    public override string OpName => "lsl_with_c";
    protected override (LLVMValueRef value, LLVMValueRef carry) Compute(
        EmitContext ctx, LLVMValueRef value, LLVMValueRef amountFull, LLVMValueRef amountIsZero)
    {
        var amount31 = ctx.Builder.BuildAnd(amountFull, ctx.ConstU32(31), "lsl_amt31");
        var safeAmt  = ctx.Builder.BuildSelect(amountIsZero, ctx.ConstU32(1), amount31, "lsl_safe_amt");
        var shifted  = ctx.Builder.BuildShl(value, safeAmt, "lsl_shifted");
        var carryShr = ctx.Builder.BuildSub(ctx.ConstU32(32), safeAmt, "lsl_carry_shr");
        var carryRaw = ctx.Builder.BuildLShr(value, carryShr, "lsl_carry_pre");
        var carryNorm = ctx.Builder.BuildAnd(carryRaw, ctx.ConstU32(1), "lsl_carry_norm");

        var amountEQ32 = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, amountFull, ctx.ConstU32(32), "lsl_amt_eq32");
        var amountGT32 = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntUGT, amountFull, ctx.ConstU32(32), "lsl_amt_gt32");
        var amountGE32 = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntUGE, amountFull, ctx.ConstU32(32), "lsl_amt_ge32");

        var bit0 = ctx.Builder.BuildAnd(value, ctx.ConstU32(1), "lsl_bit0");
        var carryForGE32 = ctx.Builder.BuildSelect(amountGT32, ctx.ConstU32(0), bit0, "lsl_carry_ge32");
        var carryForNZ = ctx.Builder.BuildSelect(amountGE32, carryForGE32, carryNorm, "lsl_carry_nz");

        var resultForGE32 = ctx.ConstU32(0);
        var resultForNZ   = ctx.Builder.BuildSelect(amountGE32, resultForGE32, shifted, "lsl_result_nz");

        var cpsrC = CarryReader.ReadCarryIn(ctx);
        var result = ctx.Builder.BuildSelect(amountIsZero, value, resultForNZ, "lsl_result");
        var carry  = ctx.Builder.BuildSelect(amountIsZero, cpsrC, carryForNZ,  "lsl_carry");
        return (result, carry);
    }
}

internal sealed class LsrWithCarryEmitter : ShiftWithCarryEmitter
{
    public override string OpName => "lsr_with_c";
    protected override (LLVMValueRef value, LLVMValueRef carry) Compute(
        EmitContext ctx, LLVMValueRef value, LLVMValueRef amountFull, LLVMValueRef amountIsZero)
    {
        var amount31 = ctx.Builder.BuildAnd(amountFull, ctx.ConstU32(31), "lsr_amt31");
        var safeAmt  = ctx.Builder.BuildSelect(amountIsZero, ctx.ConstU32(1), amount31, "lsr_safe_amt");
        var shifted  = ctx.Builder.BuildLShr(value, safeAmt, "lsr_shifted");
        var carryShr = ctx.Builder.BuildSub(safeAmt, ctx.ConstU32(1), "lsr_carry_shr");
        var carryRaw = ctx.Builder.BuildLShr(value, carryShr, "lsr_carry_pre");
        var carryNorm = ctx.Builder.BuildAnd(carryRaw, ctx.ConstU32(1), "lsr_carry_norm");

        var amountGT32 = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntUGT, amountFull, ctx.ConstU32(32), "lsr_amt_gt32");
        var amountGE32 = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntUGE, amountFull, ctx.ConstU32(32), "lsr_amt_ge32");

        var bit31 = ctx.Builder.BuildLShr(value, ctx.ConstU32(31), "lsr_bit31");
        var carryForGE32 = ctx.Builder.BuildSelect(amountGT32, ctx.ConstU32(0), bit31, "lsr_carry_ge32");
        var carryForNZ = ctx.Builder.BuildSelect(amountGE32, carryForGE32, carryNorm, "lsr_carry_nz");

        var resultForNZ = ctx.Builder.BuildSelect(amountGE32, ctx.ConstU32(0), shifted, "lsr_result_nz");

        var cpsrC = CarryReader.ReadCarryIn(ctx);
        var result = ctx.Builder.BuildSelect(amountIsZero, value, resultForNZ, "lsr_result");
        var carry  = ctx.Builder.BuildSelect(amountIsZero, cpsrC, carryForNZ,  "lsr_carry");
        return (result, carry);
    }
}

internal sealed class AsrWithCarryEmitter : ShiftWithCarryEmitter
{
    public override string OpName => "asr_with_c";
    protected override (LLVMValueRef value, LLVMValueRef carry) Compute(
        EmitContext ctx, LLVMValueRef value, LLVMValueRef amountFull, LLVMValueRef amountIsZero)
    {
        // ARM7TDMI ASR: amount in [1,31] → normal; amount >= 32 → sign-fill (0 or 0xFFFFFFFF), carry = bit 31.
        var amount31 = ctx.Builder.BuildAnd(amountFull, ctx.ConstU32(31), "asr_amt31");
        var safeAmt  = ctx.Builder.BuildSelect(amountIsZero, ctx.ConstU32(1), amount31, "asr_safe_amt");
        var shifted  = ctx.Builder.BuildAShr(value, safeAmt, "asr_shifted");
        var carryShr = ctx.Builder.BuildSub(safeAmt, ctx.ConstU32(1), "asr_carry_shr");
        var carryRaw = ctx.Builder.BuildLShr(value, carryShr, "asr_carry_pre");
        var carryNorm = ctx.Builder.BuildAnd(carryRaw, ctx.ConstU32(1), "asr_carry_norm");

        var amountGE32 = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntUGE, amountFull, ctx.ConstU32(32), "asr_amt_ge32");
        var bit31 = ctx.Builder.BuildLShr(value, ctx.ConstU32(31), "asr_bit31");
        var signFill = ctx.Builder.BuildAShr(value, ctx.ConstU32(31), "asr_sign_fill");

        var resultForNZ = ctx.Builder.BuildSelect(amountGE32, signFill, shifted, "asr_result_nz");
        var carryForNZ  = ctx.Builder.BuildSelect(amountGE32, bit31, carryNorm, "asr_carry_nz");

        var cpsrC = CarryReader.ReadCarryIn(ctx);
        var result = ctx.Builder.BuildSelect(amountIsZero, value, resultForNZ, "asr_result");
        var carry  = ctx.Builder.BuildSelect(amountIsZero, cpsrC, carryForNZ,  "asr_carry");
        return (result, carry);
    }
}

internal sealed class RorWithCarryEmitter : ShiftWithCarryEmitter
{
    public override string OpName => "ror_with_c";
    protected override (LLVMValueRef value, LLVMValueRef carry) Compute(
        EmitContext ctx, LLVMValueRef value, LLVMValueRef amountFull, LLVMValueRef amountIsZero)
    {
        // ROR by register uses amount mod 32. If amount % 32 == 0 (and amount != 0):
        // result = value, carry = bit 31 of value.
        var amountMod32 = ctx.Builder.BuildAnd(amountFull, ctx.ConstU32(31), "ror_amt_mod32");
        var modIsZero = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, amountMod32, ctx.ConstU32(0), "ror_mod_eq0");

        // Normal rotation: value rotated right by amountMod32 (when mod != 0).
        var safeAmt = ctx.Builder.BuildSelect(modIsZero, ctx.ConstU32(1), amountMod32, "ror_safe_amt");
        var rightPart = ctx.Builder.BuildLShr(value, safeAmt, "ror_right");
        var leftShift = ctx.Builder.BuildSub(ctx.ConstU32(32), safeAmt, "ror_left_shift");
        var leftPart  = ctx.Builder.BuildShl(value, leftShift, "ror_left");
        var rotated   = ctx.Builder.BuildOr(rightPart, leftPart, "ror_rotated");

        var carryShr = ctx.Builder.BuildSub(safeAmt, ctx.ConstU32(1), "ror_carry_shr");
        var carryRaw = ctx.Builder.BuildLShr(value, carryShr, "ror_carry_pre");
        var carryNorm = ctx.Builder.BuildAnd(carryRaw, ctx.ConstU32(1), "ror_carry_norm");

        // amount mod 32 == 0 (amount != 0): result = value, carry = bit31
        var bit31 = ctx.Builder.BuildLShr(value, ctx.ConstU32(31), "ror_bit31");
        var resultForNZ = ctx.Builder.BuildSelect(modIsZero, value, rotated, "ror_result_nz");
        var carryForNZ  = ctx.Builder.BuildSelect(modIsZero, bit31, carryNorm, "ror_carry_nz");

        var cpsrC = CarryReader.ReadCarryIn(ctx);
        var result = ctx.Builder.BuildSelect(amountIsZero, value, resultForNZ, "ror_result");
        var carry  = ctx.Builder.BuildSelect(amountIsZero, cpsrC, carryForNZ,  "ror_carry");
        return (result, carry);
    }
}

internal sealed class UpdateCShifter : IMicroOpEmitter
{
    public string OpName => "update_c_shifter";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var v = StandardEmitters.ResolveInput(ctx, step.Raw, 0);
        var reg  = step.Raw.TryGetProperty("reg",  out var r) ? r.GetString()! : "CPSR";
        var flag = step.Raw.TryGetProperty("flag", out var f) ? f.GetString()! : "C";
        CpsrHelpers.SetStatusFlagFromI32Lsb(ctx, reg, flag, v);
    }
}

internal sealed class UpdateCAddCarry : IMicroOpEmitter
{
    public string OpName => "update_c_add_carry";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var a   = StandardEmitters.ResolveInput(ctx, step.Raw, 0);
        var b   = StandardEmitters.ResolveInput(ctx, step.Raw, 1);
        var cin = StandardEmitters.ResolveInput(ctx, step.Raw, 2);
        var reg  = step.Raw.TryGetProperty("reg",  out var r) ? r.GetString()! : "CPSR";
        var flag = step.Raw.TryGetProperty("flag", out var f) ? f.GetString()! : "C";

        var i64 = LLVMTypeRef.Int64;
        var aL = ctx.Builder.BuildZExt(a,   i64, "a_l");
        var bL = ctx.Builder.BuildZExt(b,   i64, "b_l");
        var cL = ctx.Builder.BuildZExt(cin, i64, "c_l");
        var sum = ctx.Builder.BuildAdd(ctx.Builder.BuildAdd(aL, bL, "ab_l"), cL, "abc_l");
        var hi  = ctx.Builder.BuildLShr(sum, LLVMValueRef.CreateConstInt(i64, 32, false), "carry_hi");
        var hi32 = ctx.Builder.BuildTrunc(hi, LLVMTypeRef.Int32, "carry_hi_i32");
        CpsrHelpers.SetStatusFlagFromI32Lsb(ctx, reg, flag, hi32);
    }
}

internal sealed class UpdateCSubCarry : IMicroOpEmitter
{
    public string OpName => "update_c_sub_carry";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var a   = StandardEmitters.ResolveInput(ctx, step.Raw, 0);
        var b   = StandardEmitters.ResolveInput(ctx, step.Raw, 1);
        var cin = StandardEmitters.ResolveInput(ctx, step.Raw, 2);
        var reg  = step.Raw.TryGetProperty("reg",  out var r) ? r.GetString()! : "CPSR";
        var flag = step.Raw.TryGetProperty("flag", out var f) ? f.GetString()! : "C";
        var notCin = ctx.Builder.BuildXor(cin, ctx.ConstU32(1), "not_cin");
        var bPlus  = ctx.Builder.BuildAdd(b, notCin, "b_plus");
        var cBool  = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntUGE, a, bPlus, "c_subc_test");
        CpsrHelpers.SetStatusFlag(ctx, reg, flag, cBool);
    }
}

// ---------------- ARM carry-aware arithmetic ----------------

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

// ---------------- ARM PSR access ----------------

internal sealed class ReadPsr : IMicroOpEmitter
{
    public string OpName => "read_psr";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var which = step.Raw.TryGetProperty("which", out var w) ? w.GetString() : "CPSR";
        var outName = StandardEmitters.GetOut(step.Raw);
        // SPSR is mode-banked; banked-PSR support lands later. Route SPSR
        // reads through CPSR slot for now.
        var target = which == "SPSR" ? "CPSR" : (which ?? "CPSR");
        var ptr = ctx.Layout.GepStatusRegister(ctx.Builder, ctx.StatePtr, target);
        var v = ctx.Builder.BuildLoad2(LLVMTypeRef.Int32, ptr, outName);
        ctx.Values[outName] = v;
    }
}

internal sealed class WritePsr : IMicroOpEmitter
{
    public string OpName => "write_psr";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var valueName = step.Raw.GetProperty("value").GetString()!;
        var value = ctx.Resolve(valueName);
        var which = step.Raw.TryGetProperty("which", out var wEl) ? wEl.GetString() : "CPSR";
        var isCpsr = which == "CPSR" || which is null;

        // Capture old CPSR mode bits BEFORE write so we can detect mode
        // change after the masked store completes.
        var cpsrPtrEarly = isCpsr ? ctx.Layout.GepStatusRegister(ctx.Builder, ctx.StatePtr, "CPSR") : default;
        LLVMValueRef oldMode = default;
        if (isCpsr)
        {
            var oldCpsr = ctx.Builder.BuildLoad2(LLVMTypeRef.Int32, cpsrPtrEarly, "psr_old_for_modecheck");
            oldMode = ctx.Builder.BuildAnd(oldCpsr, ctx.ConstU32(0x1F), "psr_old_mode");
        }

        // Static mask form: { "mask": <constant> } — used by callers who
        // know at compile time which bits to update.
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

        // Runtime mask form: { "mask_from_psr_field": "field_name" } —
        // expands ARM's 4-bit MSR field selector (f/s/x/c) into a 32-bit
        // mask word at runtime, then applies it like the static path.
        // Bits: 19=f→0xFF000000, 18=s→0x00FF0000, 17=x→0x0000FF00, 16=c→0x000000FF.
        LLVMValueRef? runtimeMask = null;
        if (step.Raw.TryGetProperty("mask_from_psr_field", out var fEl))
        {
            var fieldName = fEl.GetString()!;
            var fieldVal  = ctx.Resolve(fieldName);   // 4-bit field, low 4 bits

            // mask = ((field & 8) ? 0xFF000000 : 0)
            //      | ((field & 4) ? 0x00FF0000 : 0)
            //      | ((field & 2) ? 0x0000FF00 : 0)
            //      | ((field & 1) ? 0x000000FF : 0)
            // Implemented branchlessly: for each bit, isolate, sign-extend
            // to 0/-1 worth of the byte, and OR in the byte mask.
            LLVMValueRef accum = ctx.ConstU32(0);
            (uint bitIdx, uint byteMask)[] entries = {
                (3, 0xFF000000u),   // f
                (2, 0x00FF0000u),   // s
                (1, 0x0000FF00u),   // x
                (0, 0x000000FFu),   // c
            };
            foreach (var (bitIdx, byteMask) in entries)
            {
                var bit  = ctx.Builder.BuildAnd(
                    ctx.Builder.BuildLShr(fieldVal, ctx.ConstU32(bitIdx), $"mskf_shr{bitIdx}"),
                    ctx.ConstU32(1), $"mskf_bit{bitIdx}");
                // Multiply bit (0 or 1) by byteMask: bit * byteMask works because
                // bit is 0 or 1 and we want 0 or byteMask.
                var contribution = ctx.Builder.BuildMul(bit, ctx.ConstU32(byteMask), $"mskf_c{bitIdx}");
                accum = ctx.Builder.BuildOr(accum, contribution, $"mskf_or{bitIdx}");
            }
            runtimeMask = accum;
        }

        var target = which == "SPSR" ? "CPSR" : (which ?? "CPSR");
        var ptr = ctx.Layout.GepStatusRegister(ctx.Builder, ctx.StatePtr, target);

        if (runtimeMask is { } rm)
        {
            var oldV = ctx.Builder.BuildLoad2(LLVMTypeRef.Int32, ptr, "psr_old");
            var notMask = ctx.Builder.BuildXor(rm, ctx.ConstU32(0xFFFFFFFFu), "psr_notmask");
            var keep = ctx.Builder.BuildAnd(oldV,  notMask, "psr_keep");
            var inV  = ctx.Builder.BuildAnd(value, rm, "psr_in");
            var newV = ctx.Builder.BuildOr (keep, inV, "psr_new");
            ctx.Builder.BuildStore(newV, ptr);
        }
        else if (maskBits == 0xFFFFFFFFu)
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

        // CPSR mode-change → invoke host_swap_register_bank so banked R8-R14
        // get re-shuffled. Only emit when writing CPSR (SPSR writes never
        // affect the visible register file). Comparison is runtime; if
        // oldMode == newMode the swap is a no-op inside the handler.
        if (isCpsr)
        {
            var newCpsr = ctx.Builder.BuildLoad2(LLVMTypeRef.Int32, ptr, "psr_new_for_modecheck");
            var newMode = ctx.Builder.BuildAnd(newCpsr, ctx.ConstU32(0x1F), "psr_new_mode");
            var (swapSlot, swapType, swapPtrType) = HostHelpers.GetSwapRegisterBankSlot(ctx.Module, ctx.Layout.PointerType);
            var loadedSwap = ctx.Builder.BuildLoad2(swapPtrType, swapSlot, "psr_swap_fn");
            ctx.Builder.BuildCall2(swapType, loadedSwap,
                new[] { ctx.StatePtr, oldMode, newMode }, "");
        }
    }
}

// ---------------- ARM-specific control flow ----------------

/// <summary>
/// BX-style indirect branch: target's bit 0 selects ARM (0) vs Thumb (1)
/// instruction set; aligned target written to PC (R15).
/// </summary>
internal sealed class BranchIndirectArm : IMicroOpEmitter
{
    public string OpName => "branch_indirect";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var targetName = step.Raw.GetProperty("target").GetString()!;
        var target = ctx.Resolve(targetName);

        var bit0 = ctx.Builder.BuildAnd(target, ctx.ConstU32(1), "bx_bit0");
        CpsrHelpers.SetStatusFlagFromI32Lsb(ctx, "CPSR", "T", bit0);

        // Alignment depends on which mode we're switching INTO:
        //   bit0 == 1 → Thumb (halfword align: clear bit 0)
        //   bit0 == 0 → ARM   (word align: clear bits 1:0)
        var bit0IsSet = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, bit0, ctx.ConstU32(0), "bx_thumb");
        var thumbAligned = ctx.Builder.BuildAnd(target, ctx.ConstU32(0xFFFFFFFEu), "bx_thumb_aligned");
        var armAligned   = ctx.Builder.BuildAnd(target, ctx.ConstU32(0xFFFFFFFCu), "bx_arm_aligned");
        var aligned = ctx.Builder.BuildSelect(bit0IsSet, thumbAligned, armAligned, "bx_aligned");

        var pcSlot = ctx.Layout.GepGpr(ctx.Builder, ctx.StatePtr, 15);
        ctx.Builder.BuildStore(aligned, pcSlot);
    }
}

/// <summary>
/// Per-instruction conditional gate. Wraps a then-block so it executes
/// only if the standard ARM condition (named by a 4-bit field in the
/// instruction) holds against current CPSR flags.
///
/// Step shape:
/// <code>
///   { "op": "if_arm_cond",
///     "cond_field": &lt;name&gt;,
///     "then": [ ...steps... ] }
/// </code>
///
/// Used by Thumb F16 (conditional branch) where each instruction
/// carries its own cond field rather than going through the
/// instruction-set-wide condition gate.
/// </summary>
internal sealed class IfArmCondEmitter : IMicroOpEmitter
{
    public string OpName => "if_arm_cond";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var condFieldName = step.Raw.GetProperty("cond_field").GetString()!;
        var thenSteps = step.Raw.GetProperty("then");

        var condVal = ctx.Resolve(condFieldName);
        var shouldExec = ConditionEvaluator.EmitCheckOnCondValue(ctx, condVal);

        var thenBB = ctx.Function.AppendBasicBlock("ifcond_then");
        var endBB  = ctx.Function.AppendBasicBlock("ifcond_end");
        ctx.Builder.BuildCondBr(shouldExec, thenBB, endBB);

        ctx.Builder.PositionAtEnd(thenBB);
        var registry = EmitterContextHolder.CurrentRegistry
            ?? throw new InvalidOperationException("No active EmitterRegistry on this thread.");
        foreach (var stepEl in thenSteps.EnumerateArray())
        {
            var op = stepEl.GetProperty("op").GetString()!;
            registry.EmitStep(ctx, new MicroOpStep(op, stepEl));
        }
        if (ctx.Builder.InsertBlock.Terminator.Handle == IntPtr.Zero)
            ctx.Builder.BuildBr(endBB);

        ctx.Builder.PositionAtEnd(endBB);
    }
}

/// <summary>
/// Lower the ARM <c>SPSR -&gt; CPSR</c> restore that happens at exception
/// return (e.g. <c>SUBS pc, lr, #4</c>, <c>LDM ... ^</c>). The current
/// CPSR.M[4:0] selects which banked SPSR slot is the source. We emit a
/// switch over CPSR.M with one arm per banked-SPSR mode declared in the
/// spec; modes with no SPSR slot (User/System) hit the default arm and
/// leave CPSR untouched (matches ARM ARM "UNPREDICTABLE in User mode").
///
/// After writing the new CPSR we call <c>host_swap_register_bank</c>
/// with (old_mode, new_mode) so the host can re-shuffle banked GPRs.
/// </summary>
internal sealed class RestoreCpsrFromSpsr : IMicroOpEmitter
{
    public string OpName => "restore_cpsr_from_spsr";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var modes = ctx.Layout.ProcessorModes
            ?? throw new InvalidOperationException(
                "restore_cpsr_from_spsr requires processor_modes in the CPU spec.");
        if (!ctx.Layout.IsStatusRegisterBanked("SPSR"))
            throw new InvalidOperationException(
                "restore_cpsr_from_spsr requires SPSR to be declared as banked_per_mode.");

        var cpsrPtr = ctx.Layout.GepStatusRegister(ctx.Builder, ctx.StatePtr, "CPSR");
        var oldCpsr = ctx.Builder.BuildLoad2(LLVMTypeRef.Int32, cpsrPtr, "old_cpsr_for_restore");
        const uint modeMask = 0x1F;
        var oldMode = ctx.Builder.BuildAnd(oldCpsr, ctx.ConstU32(modeMask), "restore_old_mode");

        // Build merge block where all arms join.
        var mergeBB = ctx.AppendBlock("restore_cpsr_merge");

        // Default arm: CPSR untouched, just branch to merge.
        var defaultBB = ctx.AppendBlock("restore_cpsr_default");
        var bankedModes = ctx.Layout.GetStatusBankedModes("SPSR");
        var sw = ctx.Builder.BuildSwitch(oldMode, defaultBB, (uint)bankedModes.Count);

        // PHI for the new CPSR value — defaults to oldCpsr in the no-SPSR arm.
        var armResults = new List<(LLVMValueRef value, LLVMBasicBlockRef block)>();

        ctx.Builder.PositionAtEnd(defaultBB);
        armResults.Add((oldCpsr, defaultBB));
        ctx.Builder.BuildBr(mergeBB);

        foreach (var modeId in bankedModes)
        {
            var modeEntry = modes.Modes.FirstOrDefault(m => m.Id == modeId);
            if (modeEntry?.Encoding is null)
                throw new InvalidOperationException(
                    $"Mode '{modeId}' is in SPSR.banked_per_mode but has no encoding in processor_modes.");
            uint modeEnc = Convert.ToUInt32(modeEntry.Encoding, 2);

            var armBB = ctx.AppendBlock($"restore_cpsr_from_spsr_{modeId.ToLowerInvariant()}");
            sw.AddCase(ctx.ConstU32(modeEnc), armBB);

            ctx.Builder.PositionAtEnd(armBB);
            var spsrPtr = ctx.Layout.GepStatusRegister(ctx.Builder, ctx.StatePtr, "SPSR", modeId);
            var spsrVal = ctx.Builder.BuildLoad2(LLVMTypeRef.Int32, spsrPtr, $"spsr_{modeId.ToLowerInvariant()}");
            armResults.Add((spsrVal, armBB));
            ctx.Builder.BuildBr(mergeBB);
        }

        // Merge: PHI new CPSR, store, swap if mode actually changed.
        ctx.Builder.PositionAtEnd(mergeBB);
        var phi = ctx.Builder.BuildPhi(LLVMTypeRef.Int32, "new_cpsr");
        phi.AddIncoming(
            armResults.Select(r => r.value).ToArray(),
            armResults.Select(r => r.block).ToArray(),
            (uint)armResults.Count);
        ctx.Builder.BuildStore(phi, cpsrPtr);

        var newMode = ctx.Builder.BuildAnd(phi, ctx.ConstU32(modeMask), "restore_new_mode");
        var (swapSlot, swapType, swapPtrType) = HostHelpers.GetSwapRegisterBankSlot(ctx.Module, ctx.Layout.PointerType);
        var loadedSwap = ctx.Builder.BuildLoad2(swapPtrType, swapSlot, "swap_fn");
        ctx.Builder.BuildCall2(swapType, loadedSwap,
            new[] { ctx.StatePtr, oldMode, newMode }, "");
    }
}

/// <summary>
/// Enter an exception vector. The full ARM exception-entry sequence
/// (save CPSR -&gt; SPSR_&lt;mode&gt;, switch CPSR.M, save next-PC -&gt; LR_&lt;mode&gt;,
/// jump to vector address) requires banked-register support which lands
/// in 2.5.7. For now we emit only the PC update — host code can detect
/// the PC change and complete the rest in software.
///
/// Step shape:
/// <code>{ "op": "raise_exception", "vector": "SoftwareInterrupt" }</code>
///
/// The vector name is looked up in the spec's
/// <see cref="CpuStateLayout.ExceptionVectors"/> table.
/// </summary>
internal sealed class RaiseExceptionEmitter : IMicroOpEmitter
{
    public string OpName => "raise_exception";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var vectorName = step.Raw.GetProperty("vector").GetString()!;

        // Look up the spec's vector entry.
        ExceptionVector? vector = null;
        foreach (var v in ctx.Layout.ExceptionVectors)
            if (string.Equals(v.Name, vectorName, StringComparison.Ordinal)) { vector = v; break; }
        if (vector is null)
        {
            throw new InvalidOperationException(
                $"raise_exception: unknown vector '{vectorName}'. Declared vectors: " +
                string.Join(", ", ctx.Layout.ExceptionVectors.Select(v => v.Name)));
        }

        var modes = ctx.Layout.ProcessorModes
            ?? throw new InvalidOperationException(
                "raise_exception requires processor_modes in the CPU spec.");
        if (vector.EnterMode is null)
            throw new InvalidOperationException(
                $"Vector '{vectorName}' has no enter_mode declared in spec.");
        var enterMode = vector.EnterMode;

        var modeEntry = modes.Modes.FirstOrDefault(m => m.Id == enterMode);
        if (modeEntry?.Encoding is null)
            throw new InvalidOperationException(
                $"Mode '{enterMode}' has no encoding declared in spec.");
        uint newModeEnc = Convert.ToUInt32(modeEntry.Encoding, 2);

        // 1. Read current CPSR.
        var cpsrPtr = ctx.Layout.GepStatusRegister(ctx.Builder, ctx.StatePtr, "CPSR");
        var oldCpsr = ctx.Builder.BuildLoad2(LLVMTypeRef.Int32, cpsrPtr, "old_cpsr");

        // 2. Save CPSR -> SPSR_<enterMode> (only if SPSR is banked AND
        //    the target mode has a banked SPSR slot).
        if (ctx.Layout.IsStatusRegisterBanked("SPSR") &&
            ctx.Layout.GetStatusBankedModes("SPSR").Contains(enterMode))
        {
            var spsrPtr = ctx.Layout.GepStatusRegister(ctx.Builder, ctx.StatePtr, "SPSR", enterMode);
            ctx.Builder.BuildStore(oldCpsr, spsrPtr);
        }

        // 3. Compute next-instruction PC.  Reading R15 returns
        //    current_addr + pc_offset_bytes; the next instruction is at
        //    current_addr + instruction_size_bytes; so next_pc = R15 -
        //    (pc_offset_bytes - instruction_size_bytes).
        var r15Ptr = ctx.Layout.GepGpr(ctx.Builder, ctx.StatePtr, 15);
        var r15    = ctx.Builder.BuildLoad2(LLVMTypeRef.Int32, r15Ptr, "raw_pc");
        var widthBytes  = ctx.InstructionSet.WidthBits.Fixed!.Value / 8;
        var pcDelta     = ctx.InstructionSet.PcOffsetBytes - widthBytes;
        var nextPc      = pcDelta == 0
            ? r15
            : ctx.Builder.BuildSub(r15, ctx.ConstU32((uint)pcDelta), "next_pc");

        // 4. Save next_pc -> R14_<enterMode> (banked LR slot).
        if (modes.BankedRegisters.TryGetValue(enterMode, out var bankedList))
        {
            var r14Idx = -1;
            for (int i = 0; i < bankedList.Count; i++)
                if (bankedList[i] == "R14") { r14Idx = i; break; }
            if (r14Idx >= 0)
            {
                var lrPtr = ctx.Layout.GepBankedGpr(ctx.Builder, ctx.StatePtr, enterMode, r14Idx);
                ctx.Builder.BuildStore(nextPc, lrPtr);
            }
        }

        // 5. Compute new CPSR: clear M[4:0], OR in new mode + disable bits.
        //    Per ARM ARM B1.8.5, every exception entry on ARMv4T also
        //    clears the T-bit (forces ARM state) since the vector table
        //    at 0x00..0x1C is ARM code. We mask T off as part of the
        //    initial bit-clear pass.
        const uint modeMask = 0x1F;
        uint clearMask = modeMask;
        bool hasTBit = false;
        try
        {
            int tBitIdx = ctx.Layout.GetStatusFlagBitIndex("CPSR", "T");
            clearMask |= 1u << tBitIdx;
            hasTBit = true;
        }
        catch (InvalidOperationException) { /* CPU has no Thumb state — fine */ }
        var clearedMode = ctx.Builder.BuildAnd(oldCpsr, ctx.ConstU32(~clearMask),
            hasTBit ? "cpsr_no_mode_no_t" : "cpsr_no_mode");
        var withNewMode = ctx.Builder.BuildOr(clearedMode, ctx.ConstU32(newModeEnc), "cpsr_with_new_mode");
        var newCpsr = withNewMode;
        foreach (var disableFlag in vector.DisableFlags)
        {
            int bitIdx = ctx.Layout.GetStatusFlagBitIndex("CPSR", disableFlag);
            uint bitMask = 1u << bitIdx;
            newCpsr = ctx.Builder.BuildOr(newCpsr, ctx.ConstU32(bitMask),
                $"cpsr_disable_{disableFlag.ToLowerInvariant()}");
        }
        ctx.Builder.BuildStore(newCpsr, cpsrPtr);

        // 6. Call host_swap_register_bank(state, old_mode, new_mode) so
        //    the host can re-shuffle banked GPRs into the visible R8-R14
        //    slots. Implemented host-side (Phase 3 work); we just emit the
        //    extern call here.
        var oldMode = ctx.Builder.BuildAnd(oldCpsr, ctx.ConstU32(modeMask), "old_mode");
        var (swapSlot, swapType, swapPtrType) = HostHelpers.GetSwapRegisterBankSlot(ctx.Module, ctx.Layout.PointerType);
        var loadedSwap = ctx.Builder.BuildLoad2(swapPtrType, swapSlot, "swap_fn");
        ctx.Builder.BuildCall2(swapType, loadedSwap,
            new[] { ctx.StatePtr, oldMode, ctx.ConstU32(newModeEnc) }, "");

        // 7. PC = vector address.
        ctx.Builder.BuildStore(ctx.ConstU32(vector.Address), r15Ptr);
    }
}

/// <summary>
/// Externs the host runtime is expected to bind at JIT/exec setup time
/// (Phase 3 work). Centralised so the binding side has a single
/// canonical name list.
/// </summary>
internal static class HostHelpers
{
    public const string SwapRegisterBank = "host_swap_register_bank";

    /// <summary>
    /// Returns (slot, fnType, ptrType) where <c>slot</c> is the global
    /// pointer variable holding the host's swap-bank function address.
    /// Indirect-call idiom (load slot → call) is used because MCJIT can't
    /// reliably bind extern function declarations via AddGlobalMapping in
    /// LLVM 20 — see MemoryEmitters.GetOrDeclareMemoryFunctionPointer.
    /// </summary>
    public static (LLVMValueRef Slot, LLVMTypeRef FnType, LLVMTypeRef PtrType)
        GetSwapRegisterBankSlot(LLVMModuleRef module, LLVMTypeRef statePtrType)
    {
        var fnType = LLVMTypeRef.CreateFunction(
            module.Context.VoidType,
            new[] { statePtrType, LLVMTypeRef.Int32, LLVMTypeRef.Int32 });
        var ptrType = LLVMTypeRef.CreatePointer(fnType, 0);

        var existing = module.GetNamedGlobal(SwapRegisterBank);
        if (existing.Handle != IntPtr.Zero) return (existing, fnType, ptrType);

        var slot = module.AddGlobal(ptrType, SwapRegisterBank);
        slot.Linkage = LLVMLinkage.LLVMExternalLinkage;
        return (slot, fnType, ptrType);
    }
}

// ---------------- shared ARM helper ----------------

/// <summary>Reads CPSR.C as an i32 0/1 value for use as a carry-in operand.</summary>
internal static class CarryReader
{
    public static LLVMValueRef ReadCarryIn(EmitContext ctx)
        => CpsrHelpers.ReadStatusFlag(ctx, "CPSR", "C");
}
