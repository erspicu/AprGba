using AprCpu.Core.JsonSpec;
using LLVMSharp.Interop;

namespace AprCpu.Core.IR;

/// <summary>
/// Lowers an <see cref="OperandResolver"/> into IR before any micro-op
/// step runs. Outputs are added to <see cref="EmitContext.Values"/> and
/// can then be referenced by name in subsequent steps.
/// </summary>
public static class OperandResolvers
{
    public static void Apply(EmitContext ctx)
    {
        foreach (var (name, resolver) in ctx.Format.Operands)
        {
            switch (resolver.Kind)
            {
                case "immediate_rotated":
                    EmitImmediateRotated(ctx, name, resolver);
                    break;
                case "pc_relative_offset":
                    EmitPcRelativeOffset(ctx, name, resolver);
                    break;
                case "register_direct":
                    EmitRegisterDirect(ctx, name, resolver);
                    break;
                default:
                    throw new NotSupportedException(
                        $"Operand resolver kind '{resolver.Kind}' is not yet implemented (operand '{name}' in format '{ctx.Format.Name}').");
            }
        }
    }

    /// <summary>
    /// ARM "immediate, rotated" operand:
    ///   value = ROR(zext(imm8), rotate*2)
    ///   shifter_carry_out = (rotate == 0) ? CPSR.C : value[31]
    /// We emit the simple rotation here; carry-out is computed but kept
    /// minimal (uses bit 31 of the rotated value when rotate != 0, else
    /// re-uses the existing CPSR.C, modelled as an i32 0/1).
    /// </summary>
    private static void EmitImmediateRotated(EmitContext ctx, string name, OperandResolver _)
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
    private static void EmitPcRelativeOffset(EmitContext ctx, string _, OperandResolver resolver)
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

        var r15Ptr = ctx.Layout.GepGpr(ctx.Builder, ctx.StatePtr, 15);
        var r15    = ctx.Builder.BuildLoad2(LLVMTypeRef.Int32, r15Ptr, "r15");
        var pcOff  = ctx.ConstU32((uint)ctx.InstructionSet.PcOffsetBytes);
        var pc     = ctx.Builder.BuildAdd(r15, pcOff, "pc_with_offset");
        var addr   = ctx.Builder.BuildAdd(pc, scaled, "address");
        ctx.Values["address"] = addr;
    }

    private static void EmitRegisterDirect(EmitContext ctx, string name, OperandResolver _)
    {
        // For now this is a no-op: callers can resolve the register directly
        // via read_reg with the underlying field name.
        var idx   = ctx.Resolve(name);
        var maskd = ctx.Builder.BuildAnd(idx, ctx.ConstU32(0xF), $"{name}_idx_masked");
        var ptr   = ctx.Layout.GepGprDynamic(ctx.Builder, ctx.StatePtr, maskd);
        var v     = ctx.Builder.BuildLoad2(LLVMTypeRef.Int32, ptr, $"{name}_value");
        ctx.Values[$"{name}_value"] = v;
    }
}
