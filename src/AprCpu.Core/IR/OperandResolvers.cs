using AprCpu.Core.JsonSpec;
using LLVMSharp.Interop;

namespace AprCpu.Core.IR;

/// <summary>
/// Built-in operand resolvers. <see cref="RegisterAll"/> populates an
/// <see cref="OperandResolverRegistry"/> with the kinds shipped in the
/// box; new architectures may also call
/// <c>OperandResolverRegistry.Register(...)</c> with their own
/// <see cref="IOperandResolver"/> implementations.
///
/// (R5 will split the ARM-specific kinds out of this file.)
/// </summary>
public static class StandardOperandResolvers
{
    public static void RegisterAll(OperandResolverRegistry reg)
    {
        reg.Register(new RegisterDirectResolver());
        reg.Register(new PcRelativeOffsetResolver());
        reg.Register(new ImmediateRotatedResolver());
        reg.Register(new ShiftedRegisterByImmediateResolver());
        reg.Register(new ShiftedRegisterByRegisterResolver());
    }
}

internal sealed class ImmediateRotatedResolver : IOperandResolver
{
    public string Kind => "immediate_rotated";
    public void Resolve(EmitContext ctx, string name, OperandResolver resolver)
        => OperandResolverImpl.EmitImmediateRotated(ctx, name, resolver);
}

internal sealed class PcRelativeOffsetResolver : IOperandResolver
{
    public string Kind => "pc_relative_offset";
    public void Resolve(EmitContext ctx, string name, OperandResolver resolver)
        => OperandResolverImpl.EmitPcRelativeOffset(ctx, name, resolver);
}

internal sealed class RegisterDirectResolver : IOperandResolver
{
    public string Kind => "register_direct";
    public void Resolve(EmitContext ctx, string name, OperandResolver resolver)
        => OperandResolverImpl.EmitRegisterDirect(ctx, name, resolver);
}

internal sealed class ShiftedRegisterByImmediateResolver : IOperandResolver
{
    public string Kind => "shifted_register_by_immediate";
    public void Resolve(EmitContext ctx, string name, OperandResolver resolver)
        => OperandResolverImpl.EmitShiftedRegisterByImmediate(ctx, name, resolver);
}

internal sealed class ShiftedRegisterByRegisterResolver : IOperandResolver
{
    public string Kind => "shifted_register_by_register";
    public void Resolve(EmitContext ctx, string name, OperandResolver resolver)
        => OperandResolverImpl.EmitShiftedRegisterByRegister(ctx, name, resolver);
}

/// <summary>
/// Concrete IR-emit logic for each built-in resolver kind. Held in one
/// internal static class so the (long) helpers can share private methods
/// without exposing them publicly.
/// </summary>
internal static class OperandResolverImpl
{

    /// <summary>
    /// ARM "immediate, rotated" operand:
    ///   value = ROR(zext(imm8), rotate*2)
    ///   shifter_carry_out = (rotate == 0) ? CPSR.C : value[31]
    /// We emit the simple rotation here; carry-out is computed but kept
    /// minimal (uses bit 31 of the rotated value when rotate != 0, else
    /// re-uses the existing CPSR.C, modelled as an i32 0/1).
    /// </summary>
    internal static void EmitImmediateRotated(EmitContext ctx, string name, OperandResolver _)
    {
        var imm8   = ctx.Resolve("imm8");
        var rotate = ctx.Resolve("rotate");

        // amount = rotate * 2  (already 0..30, so 5 bits result fits in i32)
        var amount = ctx.Builder.BuildShl(rotate, ctx.ConstU32(1), "rot_amt");

        // value = (imm8 >> amount) | (imm8 << (32 - amount))
        var thirtyTwo = ctx.ConstU32(32);
        var leftShift = ctx.Builder.BuildSub(thirtyTwo, amount, "rot_lhs");

        // Avoid undefined-behaviour at amount==0: when amount==0, simulate a
        // pure copy by selecting `imm8`; otherwise compute the rotated value.
        var zero = ctx.ConstU32(0);
        var amtIsZero = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, amount, zero, "amt_is_zero");

        var lo = ctx.Builder.BuildLShr(imm8, amount, "rot_lo");
        var hi = ctx.Builder.BuildShl(imm8, leftShift, "rot_hi");
        var combined = ctx.Builder.BuildOr(lo, hi, "rot_or");

        var value = ctx.Builder.BuildSelect(amtIsZero, imm8, combined, "op2_value");
        ctx.Values["op2_value"] = value;

        // shifter_carry_out: top bit of value when amount != 0, else 0 (placeholder).
        var top = ctx.Builder.BuildLShr(value, ctx.ConstU32(31), "op2_top");
        var topMask = ctx.Builder.BuildAnd(top, ctx.ConstU32(1), "op2_top_bit");
        var carryOut = ctx.Builder.BuildSelect(amtIsZero, zero, topMask, "shifter_carry_out");
        ctx.Values["shifter_carry_out"] = carryOut;
    }

    /// <summary>
    /// PC-relative branch target. Auto-detects the offset field by name
    /// ("offset24" for ARM B/BL, "offset11" for Thumb F18). Shift amount
    /// is implied by the field width (24 → &lt;&lt;2, 11 → &lt;&lt;1) so the
    /// simple JSON form (no extra attrs) keeps working for both, while
    /// callers can override via the resolver's <c>field</c> / <c>shift</c>
    /// attributes if needed.
    /// </summary>
    internal static void EmitPcRelativeOffset(EmitContext ctx, string _, OperandResolver resolver)
    {
        var raw = resolver.Raw;
        string fieldName;
        int shiftAmt;
        int fieldWidth;

        if (raw.TryGetProperty("field", out var fEl))
        {
            fieldName = fEl.GetString()!;
            if (!ctx.Format.Fields.TryGetValue(fieldName, out var range))
                throw new InvalidOperationException(
                    $"pc_relative_offset: declared field '{fieldName}' missing from format '{ctx.Format.Name}'.");
            fieldWidth = range.Width;
        }
        else
        {
            // Auto-detect: pick a single field that looks like an offset.
            var candidates = ctx.Format.Fields
                .Where(kv => kv.Key.StartsWith("offset", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (candidates.Count != 1)
                throw new InvalidOperationException(
                    $"pc_relative_offset in format '{ctx.Format.Name}' needs an explicit 'field' attribute when multiple offset-like fields exist.");
            fieldName  = candidates[0].Key;
            fieldWidth = candidates[0].Value.Width;
        }

        if (raw.TryGetProperty("shift", out var sEl)) shiftAmt = sEl.GetInt32();
        else                                          shiftAmt = ctx.InstructionSet.WidthBits.Fixed == 16 ? 1 : 2;

        var off = ctx.Resolve(fieldName);

        // Sign-extend the n-bit field to 32 bits via (off << (32-n)) >>arith (32-n).
        var pad = ctx.ConstU32((uint)(32 - fieldWidth));
        var shl  = ctx.Builder.BuildShl (off, pad, "off_shl");
        var sext = ctx.Builder.BuildAShr(shl, pad, "off_sext");
        var scaled = shiftAmt == 0
            ? sext
            : ctx.Builder.BuildShl(sext, ctx.ConstU32((uint)shiftAmt), "off_scaled");

        // Convention: state.R15 already contains "current_instruction_addr +
        // pc_offset_bytes" (the pipeline-adjusted value the host loop pre-sets
        // before executing each instruction). Do NOT add pc_offset_bytes again —
        // doing so would double-count and put the branch target 8 bytes too far
        // (caught by Phase 3.2 CpuExecutor branch test). Same convention is used
        // by raise_exception (subtracts pc_offset−instr_size to get next-PC) and
        // by BL_HI (read_reg 15 → raw_pc → add scaled_offset directly).
        //
        // Phase 7 A.6.1 Strategy 2 — in block-JIT mode use the pipeline
        // PC constant directly. Per-instr mode reads GPR[15] (which the
        // executor pre-set).
        LLVMValueRef r15;
        if (ctx.PipelinePcConstant is uint pipelineValue)
        {
            r15 = ctx.ConstU32(pipelineValue);
        }
        else
        {
            var r15Ptr = ctx.Layout.GepGpr(ctx.Builder, ctx.StatePtr, 15);
            r15        = ctx.Builder.BuildLoad2(LLVMTypeRef.Int32, r15Ptr, "r15");
        }
        var addr   = ctx.Builder.BuildAdd(r15, scaled, "address");
        ctx.Values["address"] = addr;
    }

    internal static void EmitRegisterDirect(EmitContext ctx, string name, OperandResolver _)
    {
        // For now this is a no-op: callers can resolve the register directly
        // via read_reg with the underlying field name.
        var idx   = ctx.Resolve(name);
        var maskd = ctx.Builder.BuildAnd(idx, ctx.ConstU32(0xF), $"{name}_idx_masked");
        var ptr   = ctx.Layout.GepGprDynamic(ctx.Builder, ctx.StatePtr, maskd);
        var raw   = ctx.Builder.BuildLoad2(LLVMTypeRef.Int32, ptr, $"{name}_raw");
        // Phase 7 A.6.1 Strategy 2 — block-JIT does NOT pre-set GPR[15],
        // so direct memory reads for PC return stale data. Override with
        // pipeline constant via runtime select when index resolves to 15.
        LLVMValueRef v;
        if (ctx.PipelinePcConstant is uint pipelineValue)
        {
            var pcConst = ctx.ConstU32(pipelineValue);
            var isPc    = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, maskd, ctx.ConstU32(15), $"{name}_is_pc");
            v = ctx.Builder.BuildSelect(isPc, pcConst, raw, $"{name}_value");
        }
        else
        {
            v = raw;
        }
        ctx.Values[$"{name}_value"] = v;
    }

    /// <summary>
    /// ARM Barrel Shifter — register operand shifted by a 5-bit immediate.
    /// Outputs <c>op2_value</c> and <c>shifter_carry_out</c>.
    /// Handles all four shift types (LSL/LSR/ASR/ROR) and the ARM-specific
    /// amount-zero edge cases:
    ///   <list type="bullet">
    ///     <item>LSL #0   → value = Rm, carry = CPSR.C  (no shift)</item>
    ///     <item>LSR #0   → encoded as LSR #32: value = 0, carry = Rm[31]</item>
    ///     <item>ASR #0   → encoded as ASR #32: value = sign-fill, carry = Rm[31]</item>
    ///     <item>ROR #0   → encoded as RRX:    value = (C&lt;&lt;31)|(Rm&gt;&gt;1), carry = Rm[0]</item>
    ///   </list>
    /// Implementation strategy: compute both the "amount > 0" and the
    /// "amount == 0" path for each shift type, select with i1 mux on
    /// <c>amount == 0</c>, then a final 4-way mux on <c>shift_type</c>.
    /// LLVM optimisation collapses unreached branches at use sites.
    /// </summary>
    internal static void EmitShiftedRegisterByImmediate(EmitContext ctx, string name, OperandResolver _)
    {
        var rm        = LoadRegisterByFieldIndex(ctx, "rm", "rm_val");
        var shiftType = ctx.Resolve("shift_type");
        var amount    = ctx.Resolve("shift_amount");
        var cpsrC    = CarryReader.ReadCarryIn(ctx);

        var (lslV, lslC) = EmitLslShift(ctx, rm, amount, cpsrC);
        var (lsrV, lsrC) = EmitLsrShift(ctx, rm, amount);
        var (asrV, asrC) = EmitAsrShift(ctx, rm, amount);
        var (rorV, rorC) = EmitRorShift(ctx, rm, amount, cpsrC);

        // Final mux on shift_type: 00 LSL, 01 LSR, 10 ASR, 11 ROR
        var st0 = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, shiftType, ctx.ConstU32(0), "st_lsl");
        var st1 = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, shiftType, ctx.ConstU32(1), "st_lsr");
        var st2 = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, shiftType, ctx.ConstU32(2), "st_asr");

        var v_lsr_or_asr = ctx.Builder.BuildSelect(st2, asrV, rorV, "v_asr_ror");
        var v_inner      = ctx.Builder.BuildSelect(st1, lsrV, v_lsr_or_asr, "v_lsr_inner");
        var value        = ctx.Builder.BuildSelect(st0, lslV, v_inner,      "op2_value");

        var c_lsr_or_asr = ctx.Builder.BuildSelect(st2, asrC, rorC, "c_asr_ror");
        var c_inner      = ctx.Builder.BuildSelect(st1, lsrC, c_lsr_or_asr, "c_lsr_inner");
        var carry        = ctx.Builder.BuildSelect(st0, lslC, c_inner,      "shifter_carry_out");

        ctx.Values["op2_value"] = value;
        ctx.Values["shifter_carry_out"] = carry;
    }

    /// <summary>
    /// ARM Barrel Shifter — register operand shifted by another register's
    /// low byte. Like the immediate version but the shift amount comes from
    /// Rs[7:0]. Per ARM ARM, when the byte is 0, the operand is unchanged
    /// (carry-out = CPSR.C); when ≥ 32, behaviour depends on shift type
    /// (LSL/LSR ≥ 32 → 0, ASR ≥ 32 → sign-fill, ROR uses amount mod 32).
    /// </summary>
    internal static void EmitShiftedRegisterByRegister(EmitContext ctx, string name, OperandResolver _)
    {
        // ARM ARM A5.1.5: in shift-by-register form, PC reads as
        // address + 12 (one extra cycle for register shift). The same
        // adjustment must be applied to Rn reads in this format — see
        // the read_reg_shift_by_reg op below.
        var rm        = LoadRegisterPcAdjustedShiftByReg(ctx, "rm", "rm_val");
        var rs        = LoadRegisterByFieldIndex(ctx, "rs", "rs_val");
        var shiftType = ctx.Resolve("shift_type");
        var cpsrC     = CarryReader.ReadCarryIn(ctx);

        // Use only low 8 bits of Rs.
        var amountFull = ctx.Builder.BuildAnd(rs, ctx.ConstU32(0xFF), "shift_amount_full");
        // For LSL/LSR/ASR with amount >= 32, the shift saturates the result;
        // we use ARM behaviour: clamp internally at 32.  Simplest correct path:
        // - For shift count == 0:  value = rm, carry = CPSR.C  (all types)
        // - For 1 <= count <= 31:  normal shift
        // - For LSL count >= 32:  value = 0, carry = (count==32 ? rm[0] : 0)
        // - For LSR count >= 32:  value = 0, carry = (count==32 ? rm[31] : 0)
        // - For ASR count >= 32:  value = sign-fill, carry = rm[31]
        // - For ROR: amount mod 32; if mod-32 == 0 then carry = rm[31] (special)
        //
        // First-pass implementation: handle count==0 specially, else clamp
        // count to min(31, count) for LSL/LSR/ASR. RRX (the immediate-form
        // amount-0 case) does NOT apply here — by-register shift simply
        // returns Rm when Rs[7:0]==0.

        var zero  = ctx.ConstU32(0);
        var amountIsZero = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, amountFull, zero, "amt_eq_0");

        // Clamp amount to 31 for the normal-path computations to avoid
        // LLVM's undefined-behavior on shifts >= bit width.
        var thirtyOne = ctx.ConstU32(31);
        var clampedSel = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntUGT, amountFull, thirtyOne, "amt_gt_31");
        var amountClamped = ctx.Builder.BuildSelect(clampedSel, thirtyOne, amountFull, "amt_clamped");

        // Predict whether result becomes saturated (count >= 32).
        var amountGE32 = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntUGE, amountFull, ctx.ConstU32(32), "amt_ge_32");

        var (lslV, lslC) = EmitLslShiftByReg(ctx, rm, amountClamped, amountFull, amountGE32);
        var (lsrV, lsrC) = EmitLsrShiftByReg(ctx, rm, amountClamped, amountFull, amountGE32);
        var (asrV, asrC) = EmitAsrShiftByReg(ctx, rm, amountClamped, amountGE32);
        var (rorV, rorC) = EmitRorShiftByReg(ctx, rm, amountFull);

        // Pick by shift_type
        var st0 = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, shiftType, ctx.ConstU32(0), "st_lsl");
        var st1 = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, shiftType, ctx.ConstU32(1), "st_lsr");
        var st2 = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, shiftType, ctx.ConstU32(2), "st_asr");

        var v_lsr_or_asr = ctx.Builder.BuildSelect(st2, asrV, rorV, "v_asr_ror");
        var v_inner      = ctx.Builder.BuildSelect(st1, lsrV, v_lsr_or_asr, "v_lsr_inner");
        var v_typed      = ctx.Builder.BuildSelect(st0, lslV, v_inner,      "v_typed");

        var c_lsr_or_asr = ctx.Builder.BuildSelect(st2, asrC, rorC, "c_asr_ror");
        var c_inner      = ctx.Builder.BuildSelect(st1, lsrC, c_lsr_or_asr, "c_lsr_inner");
        var c_typed      = ctx.Builder.BuildSelect(st0, lslC, c_inner,      "c_typed");

        // Override with (rm, CPSR.C) when amount is zero, regardless of type.
        var value = ctx.Builder.BuildSelect(amountIsZero, rm,    v_typed, "op2_value");
        var carry = ctx.Builder.BuildSelect(amountIsZero, cpsrC, c_typed, "shifter_carry_out");

        ctx.Values["op2_value"] = value;
        ctx.Values["shifter_carry_out"] = carry;
    }

    // ----- helpers for shifted-register paths -----

    private static LLVMValueRef LoadRegisterByFieldIndex(EmitContext ctx, string fieldName, string outName)
    {
        var idx    = ctx.Resolve(fieldName);
        var masked = ctx.Builder.BuildAnd(idx, ctx.ConstU32(0xF), $"{fieldName}_idx_masked");
        var ptr    = ctx.Layout.GepGprDynamic(ctx.Builder, ctx.StatePtr, masked);
        var raw    = ctx.Builder.BuildLoad2(LLVMTypeRef.Int32, ptr, $"{outName}_raw");
        // Phase 7 A.6.1 Strategy 2 — block-JIT does NOT pre-set GPR[15]
        // each instruction (it stays as last branch target), so a runtime
        // read of GPR[15] returns stale data. Override with the pipeline
        // PC constant via a runtime select when index resolves to 15.
        if (ctx.PipelinePcConstant is uint pipelineValue)
        {
            var pcConst = ctx.ConstU32(pipelineValue);
            var isPc    = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, masked, ctx.ConstU32(15), $"{fieldName}_is_pc");
            return ctx.Builder.BuildSelect(isPc, pcConst, raw, outName);
        }
        return raw;
    }

    /// <summary>
    /// Same as <see cref="LoadRegisterByFieldIndex"/> but adds 4 to the
    /// loaded value when the index resolves to PC. Used in the data-
    /// processing shift-by-register form where ARM ARM A5.1.5 specifies
    /// that PC reads as <c>address + 12</c> rather than the usual
    /// <c>+8</c> (one extra cycle for the register shift).
    /// </summary>
    private static LLVMValueRef LoadRegisterPcAdjustedShiftByReg(EmitContext ctx, string fieldName, string outName)
    {
        var idx    = ctx.Resolve(fieldName);
        var masked = ctx.Builder.BuildAnd(idx, ctx.ConstU32(0xF), $"{fieldName}_idx_masked");
        var ptr    = ctx.Layout.GepGprDynamic(ctx.Builder, ctx.StatePtr, masked);
        var raw    = ctx.Builder.BuildLoad2(LLVMTypeRef.Int32, ptr, $"{outName}_raw");
        var isPc   = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, masked, ctx.ConstU32(15), $"{fieldName}_is_pc");
        // Phase 7 A.6.1 Strategy 2 — same stale-PC issue as
        // LoadRegisterByFieldIndex. When index resolves to PC, replace
        // raw memory load with pipeline constant + 4 (A5.1.5 PC+12).
        if (ctx.PipelinePcConstant is uint pipelineValue)
        {
            var pcConstPlus4 = ctx.ConstU32(pipelineValue + 4);
            return ctx.Builder.BuildSelect(isPc, pcConstPlus4, raw, outName);
        }
        var plus4  = ctx.Builder.BuildAdd(raw, ctx.ConstU32(4), $"{outName}_plus4");
        return ctx.Builder.BuildSelect(isPc, plus4, raw, outName);
    }

    private static (LLVMValueRef value, LLVMValueRef carry) EmitLslShift(
        EmitContext ctx, LLVMValueRef rm, LLVMValueRef amount, LLVMValueRef cpsrC)
    {
        var zero = ctx.ConstU32(0);
        var amountIsZero = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, amount, zero, "lsl_amt_zero");
        // Normal: value = rm << amt, carry = (rm >> (32-amt)) & 1
        var clampedAmt = ctx.Builder.BuildSelect(amountIsZero, ctx.ConstU32(1), amount, "lsl_amt_safe");
        var normalValue = ctx.Builder.BuildShl(rm, clampedAmt, "lsl_norm_v");
        var carryShift = ctx.Builder.BuildSub(ctx.ConstU32(32), clampedAmt, "lsl_carry_shr");
        var carryShifted = ctx.Builder.BuildLShr(rm, carryShift, "lsl_carry_pre");
        var normalCarry = ctx.Builder.BuildAnd(carryShifted, ctx.ConstU32(1), "lsl_norm_c");
        // amount == 0: pass-through
        var value = ctx.Builder.BuildSelect(amountIsZero, rm, normalValue, "lsl_v");
        var carry = ctx.Builder.BuildSelect(amountIsZero, cpsrC, normalCarry, "lsl_c");
        return (value, carry);
    }

    private static (LLVMValueRef value, LLVMValueRef carry) EmitLsrShift(
        EmitContext ctx, LLVMValueRef rm, LLVMValueRef amount)
    {
        var zero = ctx.ConstU32(0);
        var amountIsZero = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, amount, zero, "lsr_amt_zero");
        // amount == 0  → treat as LSR #32: value = 0, carry = rm[31]
        var carryAt0 = ctx.Builder.BuildLShr(rm, ctx.ConstU32(31), "lsr_carry_zero");
        // amount > 0
        var clampedAmt = ctx.Builder.BuildSelect(amountIsZero, ctx.ConstU32(1), amount, "lsr_amt_safe");
        var normalValue = ctx.Builder.BuildLShr(rm, clampedAmt, "lsr_norm_v");
        var carryShift = ctx.Builder.BuildSub(clampedAmt, ctx.ConstU32(1), "lsr_carry_shr");
        var carryShifted = ctx.Builder.BuildLShr(rm, carryShift, "lsr_carry_pre");
        var normalCarry = ctx.Builder.BuildAnd(carryShifted, ctx.ConstU32(1), "lsr_norm_c");
        var value = ctx.Builder.BuildSelect(amountIsZero, zero, normalValue, "lsr_v");
        var carry = ctx.Builder.BuildSelect(amountIsZero, carryAt0, normalCarry, "lsr_c");
        return (value, carry);
    }

    private static (LLVMValueRef value, LLVMValueRef carry) EmitAsrShift(
        EmitContext ctx, LLVMValueRef rm, LLVMValueRef amount)
    {
        var zero = ctx.ConstU32(0);
        var amountIsZero = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, amount, zero, "asr_amt_zero");
        // amount == 0 → ASR #32: value = sign-fill, carry = rm[31]
        var signFill = ctx.Builder.BuildAShr(rm, ctx.ConstU32(31), "asr_sign_fill");
        var carryAt0 = ctx.Builder.BuildLShr(rm, ctx.ConstU32(31), "asr_carry_zero");
        // amount > 0
        var clampedAmt = ctx.Builder.BuildSelect(amountIsZero, ctx.ConstU32(1), amount, "asr_amt_safe");
        var normalValue = ctx.Builder.BuildAShr(rm, clampedAmt, "asr_norm_v");
        var carryShift = ctx.Builder.BuildSub(clampedAmt, ctx.ConstU32(1), "asr_carry_shr");
        var carryShifted = ctx.Builder.BuildLShr(rm, carryShift, "asr_carry_pre");
        var normalCarry = ctx.Builder.BuildAnd(carryShifted, ctx.ConstU32(1), "asr_norm_c");
        var value = ctx.Builder.BuildSelect(amountIsZero, signFill, normalValue, "asr_v");
        var carry = ctx.Builder.BuildSelect(amountIsZero, carryAt0, normalCarry, "asr_c");
        return (value, carry);
    }

    private static (LLVMValueRef value, LLVMValueRef carry) EmitRorShift(
        EmitContext ctx, LLVMValueRef rm, LLVMValueRef amount, LLVMValueRef cpsrC)
    {
        var zero = ctx.ConstU32(0);
        var amountIsZero = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, amount, zero, "ror_amt_zero");
        // amount == 0 → RRX:  value = (C<<31)|(rm>>1), carry = rm[0]
        var cBit = ctx.Builder.BuildAnd(cpsrC, ctx.ConstU32(1), "rrx_c_bit");
        var cIn31 = ctx.Builder.BuildShl(cBit, ctx.ConstU32(31), "rrx_c_in_31");
        var rmShr1 = ctx.Builder.BuildLShr(rm, ctx.ConstU32(1), "rrx_rm_shr1");
        var rrxVal = ctx.Builder.BuildOr(cIn31, rmShr1, "rrx_value");
        var rrxCarry = ctx.Builder.BuildAnd(rm, ctx.ConstU32(1), "rrx_carry");
        // amount > 0 → ROR(rm, amount)
        var clampedAmt = ctx.Builder.BuildSelect(amountIsZero, ctx.ConstU32(1), amount, "ror_amt_safe");
        var rotL = ctx.Builder.BuildLShr(rm, clampedAmt, "ror_lo");
        var leftShift = ctx.Builder.BuildSub(ctx.ConstU32(32), clampedAmt, "ror_left_amt");
        var rotH = ctx.Builder.BuildShl(rm, leftShift, "ror_hi");
        var normalValue = ctx.Builder.BuildOr(rotL, rotH, "ror_norm_v");
        var carryShift = ctx.Builder.BuildSub(clampedAmt, ctx.ConstU32(1), "ror_carry_shr");
        var carryShifted = ctx.Builder.BuildLShr(rm, carryShift, "ror_carry_pre");
        var normalCarry = ctx.Builder.BuildAnd(carryShifted, ctx.ConstU32(1), "ror_norm_c");
        var value = ctx.Builder.BuildSelect(amountIsZero, rrxVal,   normalValue, "ror_v");
        var carry = ctx.Builder.BuildSelect(amountIsZero, rrxCarry, normalCarry, "ror_c");
        return (value, carry);
    }

    private static (LLVMValueRef value, LLVMValueRef carry) EmitLslShiftByReg(
        EmitContext ctx, LLVMValueRef rm, LLVMValueRef amountClamped, LLVMValueRef amountFull, LLVMValueRef ge32)
    {
        // Normal LSL by 1..31, plus saturation when ge32.
        // Per ARM ARM A5.1.7 (Data-processing operands - shift by register):
        //   count == 32:  result = 0, carry = Rm[0]
        //   count >  32:  result = 0, carry = 0
        var v = ctx.Builder.BuildShl(rm, amountClamped, "lslr_v");
        var carryShr = ctx.Builder.BuildSub(ctx.ConstU32(32), amountClamped, "lslr_carry_shr");
        var carryRaw = ctx.Builder.BuildLShr(rm, carryShr, "lslr_carry_raw");
        var c = ctx.Builder.BuildAnd(carryRaw, ctx.ConstU32(1), "lslr_c");
        var amountIs32 = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, amountFull, ctx.ConstU32(32), "lslr_amt_eq_32");
        var rmBit0     = ctx.Builder.BuildAnd(rm, ctx.ConstU32(1), "lslr_rm_bit0");
        var ge32Carry  = ctx.Builder.BuildSelect(amountIs32, rmBit0, ctx.ConstU32(0), "lslr_ge32_c");
        var v2 = ctx.Builder.BuildSelect(ge32, ctx.ConstU32(0), v, "lslr_v_sat");
        var c2 = ctx.Builder.BuildSelect(ge32, ge32Carry, c, "lslr_c_sat");
        return (v2, c2);
    }

    private static (LLVMValueRef value, LLVMValueRef carry) EmitLsrShiftByReg(
        EmitContext ctx, LLVMValueRef rm, LLVMValueRef amountClamped, LLVMValueRef amountFull, LLVMValueRef ge32)
    {
        // Normal LSR by 1..31, plus saturation when ge32.
        // Per ARM ARM A5.1.7:
        //   count == 32:  result = 0, carry = Rm[31]
        //   count >  32:  result = 0, carry = 0
        var v = ctx.Builder.BuildLShr(rm, amountClamped, "lsrr_v");
        var carryShr = ctx.Builder.BuildSub(amountClamped, ctx.ConstU32(1), "lsrr_carry_shr");
        var carryRaw = ctx.Builder.BuildLShr(rm, carryShr, "lsrr_carry_raw");
        var c = ctx.Builder.BuildAnd(carryRaw, ctx.ConstU32(1), "lsrr_c");
        var amountIs32 = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, amountFull, ctx.ConstU32(32), "lsrr_amt_eq_32");
        var rmBit31    = ctx.Builder.BuildLShr(rm, ctx.ConstU32(31), "lsrr_rm_bit31");
        var ge32Carry  = ctx.Builder.BuildSelect(amountIs32, rmBit31, ctx.ConstU32(0), "lsrr_ge32_c");
        var v2 = ctx.Builder.BuildSelect(ge32, ctx.ConstU32(0), v, "lsrr_v_sat");
        var c2 = ctx.Builder.BuildSelect(ge32, ge32Carry, c, "lsrr_c_sat");
        return (v2, c2);
    }

    private static (LLVMValueRef value, LLVMValueRef carry) EmitAsrShiftByReg(
        EmitContext ctx, LLVMValueRef rm, LLVMValueRef amountClamped, LLVMValueRef ge32)
    {
        var v = ctx.Builder.BuildAShr(rm, amountClamped, "asrr_v");
        var carryShr = ctx.Builder.BuildSub(amountClamped, ctx.ConstU32(1), "asrr_carry_shr");
        var carryRaw = ctx.Builder.BuildLShr(rm, carryShr, "asrr_carry_raw");
        var c = ctx.Builder.BuildAnd(carryRaw, ctx.ConstU32(1), "asrr_c");
        var signFill = ctx.Builder.BuildAShr(rm, ctx.ConstU32(31), "asrr_sign_fill");
        var msb      = ctx.Builder.BuildLShr(rm, ctx.ConstU32(31), "asrr_msb");
        var v2 = ctx.Builder.BuildSelect(ge32, signFill, v, "asrr_v_sat");
        var c2 = ctx.Builder.BuildSelect(ge32, msb,      c, "asrr_c_sat");
        return (v2, c2);
    }

    private static (LLVMValueRef value, LLVMValueRef carry) EmitRorShiftByReg(
        EmitContext ctx, LLVMValueRef rm, LLVMValueRef amountFull)
    {
        // ROR by Rs uses amount mod 32 for the rotation.
        var amountMod = ctx.Builder.BuildAnd(amountFull, ctx.ConstU32(31), "rorr_mod");
        var amountIsZeroMod = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, amountMod, ctx.ConstU32(0), "rorr_mod_zero");
        // For mod==0: when the original amount was 0, value=rm + carry=CPSR.C is
        // handled by the outer override; when amount was >0 (e.g. 32, 64, …),
        // value=rm, carry=rm[31].
        var clampedAmt = ctx.Builder.BuildSelect(amountIsZeroMod, ctx.ConstU32(1), amountMod, "rorr_amt_safe");
        var lo = ctx.Builder.BuildLShr(rm, clampedAmt, "rorr_lo");
        var leftShift = ctx.Builder.BuildSub(ctx.ConstU32(32), clampedAmt, "rorr_left_amt");
        var hi = ctx.Builder.BuildShl(rm, leftShift, "rorr_hi");
        var rotated = ctx.Builder.BuildOr(lo, hi, "rorr_rotated");
        var carryShr = ctx.Builder.BuildSub(clampedAmt, ctx.ConstU32(1), "rorr_carry_shr");
        var carryRaw = ctx.Builder.BuildLShr(rm, carryShr, "rorr_carry_raw");
        var c = ctx.Builder.BuildAnd(carryRaw, ctx.ConstU32(1), "rorr_c");
        var msb = ctx.Builder.BuildLShr(rm, ctx.ConstU32(31), "rorr_msb");
        var value = ctx.Builder.BuildSelect(amountIsZeroMod, rm,  rotated, "rorr_v");
        var carry = ctx.Builder.BuildSelect(amountIsZeroMod, msb, c,       "rorr_c2");
        return (value, carry);
    }
}
