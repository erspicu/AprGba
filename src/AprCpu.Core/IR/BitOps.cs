using AprCpu.Core.JsonSpec;
using LLVMSharp.Interop;

namespace AprCpu.Core.IR;

/// <summary>
/// Cross-arch L1 bit-manipulation primitives — operate on a value at a
/// chosen bit position. The bit index can be either a numeric constant
/// (<c>"bit": 3</c>) or a runtime field name (<c>"bit_field": "bbb"</c>)
/// so the same op covers both fixed-bit and field-dispatched encodings.
///
/// Registered ops:
/// <list type="bullet">
///   <item><c>bit_test { in, bit | bit_field, reg, flag }</c> — sets the
///         named status-register flag from <c>!(in &gt;&gt; b &amp; 1)</c>.
///         Other flag effects (N/H/C side-effects in LR35902 BIT) are
///         not folded in — the spec chains <c>set_flag</c> for those so
///         each step has one job.</item>
///   <item><c>bit_set   { in, bit | bit_field, out }</c> — out = in | (1&lt;&lt;b)</item>
///   <item><c>bit_clear { in, bit | bit_field, out }</c> — out = in &amp; ~(1&lt;&lt;b)</item>
/// </list>
/// All three preserve the input value's width (typically i8 for byte
/// registers; the mask is built at the same width via trunc/zext).
/// </summary>
internal static class BitOps
{
    public static void RegisterAll(EmitterRegistry reg)
    {
        reg.Register(new BitTest());
        reg.Register(new BitSet());
        reg.Register(new BitClear());
        reg.Register(new ShiftOp());
    }

    // ---------------- helpers ----------------

    /// <summary>
    /// Resolve the bit index — either a constant from <c>"bit"</c> or a
    /// runtime value from <c>"bit_field"</c> — coerced to the same int
    /// type as <paramref name="hostType"/> for use as a shift amount.
    /// </summary>
    internal static LLVMValueRef ResolveBitIndex(EmitContext ctx, MicroOpStep step, LLVMTypeRef hostType, string label)
    {
        if (step.Raw.TryGetProperty("bit", out var bEl))
            return LLVMValueRef.CreateConstInt(hostType, (uint)bEl.GetInt32(), false);

        if (step.Raw.TryGetProperty("bit_field", out var fEl))
        {
            var fieldName = fEl.GetString()!;
            var fieldVal  = ctx.Resolve(fieldName);
            return StackOps.CoerceToType(ctx, fieldVal, hostType, $"{label}_bit");
        }

        throw new InvalidOperationException(
            $"{step.Raw.GetProperty("op").GetString()} requires `bit: N` or `bit_field: name`");
    }

    private sealed class BitTest : IMicroOpEmitter
    {
        public string OpName => "bit_test";
        public void Emit(EmitContext ctx, MicroOpStep step)
        {
            var inName = step.Raw.GetProperty("in").GetString()!;
            var v = ctx.Resolve(inName);
            var bit = ResolveBitIndex(ctx, step, v.TypeOf, $"bit_test_{inName}");
            var one = LLVMValueRef.CreateConstInt(v.TypeOf, 1, false);

            var shifted = ctx.Builder.BuildLShr(v, bit, $"bit_test_{inName}_shr");
            var bitVal  = ctx.Builder.BuildAnd(shifted, one, $"bit_test_{inName}_b");
            var zero    = LLVMValueRef.CreateConstInt(v.TypeOf, 0, false);
            var isZero  = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, bitVal, zero, $"bit_test_{inName}_eqz");

            var reg  = step.Raw.GetProperty("reg").GetString()!;
            var flag = step.Raw.GetProperty("flag").GetString()!;
            CpsrHelpers.SetStatusFlag(ctx, reg, flag, isZero);
        }
    }

    private sealed class BitSet : IMicroOpEmitter
    {
        public string OpName => "bit_set";
        public void Emit(EmitContext ctx, MicroOpStep step)
        {
            var inName  = step.Raw.GetProperty("in").GetString()!;
            var outName = StandardEmitters.GetOut(step.Raw);
            var v = ctx.Resolve(inName);
            var bit = ResolveBitIndex(ctx, step, v.TypeOf, $"bit_set_{inName}");
            var one = LLVMValueRef.CreateConstInt(v.TypeOf, 1, false);
            var mask = ctx.Builder.BuildShl(one, bit, $"bit_set_{inName}_mask");
            ctx.Values[outName] = ctx.Builder.BuildOr(v, mask, outName);
        }
    }

    private sealed class BitClear : IMicroOpEmitter
    {
        public string OpName => "bit_clear";
        public void Emit(EmitContext ctx, MicroOpStep step)
        {
            var inName  = step.Raw.GetProperty("in").GetString()!;
            var outName = StandardEmitters.GetOut(step.Raw);
            var v = ctx.Resolve(inName);
            var bit = ResolveBitIndex(ctx, step, v.TypeOf, $"bit_clear_{inName}");
            var one = LLVMValueRef.CreateConstInt(v.TypeOf, 1, false);
            var mask = ctx.Builder.BuildShl(one, bit, $"bit_clear_{inName}_mask");
            var allOnes = LLVMValueRef.CreateConstInt(v.TypeOf, ulong.MaxValue, false);
            var notMask = ctx.Builder.BuildXor(mask, allOnes, $"bit_clear_{inName}_notmask");
            ctx.Values[outName] = ctx.Builder.BuildAnd(v, notMask, outName);
        }
    }

    /// <summary>
    /// shift { kind, in, out, reg?, flag_z?, flag_n?, flag_h?, flag_c? }
    /// — single-bit shift / rotate of an i8 value.
    ///
    /// kind:
    ///   "rlc"  — rotate left circular        : C ← bit7, bit0 ← old bit7
    ///   "rrc"  — rotate right circular       : C ← bit0, bit7 ← old bit0
    ///   "rl"   — rotate left through C       : C ← bit7, bit0 ← old C
    ///   "rr"   — rotate right through C      : C ← bit0, bit7 ← old C
    ///   "sla"  — shift left arithmetic       : C ← bit7, bit0 ← 0
    ///   "sra"  — shift right arithmetic      : C ← bit0, bit7 unchanged
    ///   "srl"  — shift right logical         : C ← bit0, bit7 ← 0
    ///   "swap" — nibble swap (lo↔hi)         : C ← 0
    ///
    /// Flag updates only fire for the optional flag_* names that are
    /// present. Z (when present) is set from <c>result == 0</c>; N/H
    /// (when present) are cleared; C (when present) is the shift-out
    /// bit. Reading the carry-in for RL/RR uses the same <c>reg</c> +
    /// <c>flag_c</c> names (so the op reads C, performs the shift, then
    /// writes C back — a single basic block, no aliasing concerns since
    /// we hold the carry in an SSA value across the shift).
    /// </summary>
    private sealed class ShiftOp : IMicroOpEmitter
    {
        public string OpName => "shift";
        public void Emit(EmitContext ctx, MicroOpStep step)
        {
            var inName  = step.Raw.GetProperty("in").GetString()!;
            var outName = StandardEmitters.GetOut(step.Raw);
            var kind    = step.Raw.GetProperty("kind").GetString()!;
            var v = ctx.Resolve(inName);
            if (v.TypeOf.IntWidth != 8)
                throw new NotSupportedException($"shift currently supports i8 only; got i{v.TypeOf.IntWidth}");
            var i8 = LLVMTypeRef.Int8;

            var topBit = ctx.Builder.BuildAnd(
                            ctx.Builder.BuildLShr(v, LLVMValueRef.CreateConstInt(i8, 7, false), $"{outName}_top_shr"),
                            LLVMValueRef.CreateConstInt(i8, 1, false), $"{outName}_top_bit");
            var lowBit = ctx.Builder.BuildAnd(v, LLVMValueRef.CreateConstInt(i8, 1, false), $"{outName}_low_bit");

            // Read the input carry as i8 0/1 — only needed for RL/RR.
            // Keeping this outside the kind switch costs a load when not
            // used, but the optimiser drops it.
            LLVMValueRef cIn = LLVMValueRef.CreateConstInt(i8, 0, false);
            if ((kind == "rl" || kind == "rr") && step.Raw.TryGetProperty("reg", out var regEl)
                && step.Raw.TryGetProperty("flag_c", out var fcEl))
            {
                var regName = regEl.GetString()!;
                var flagC   = fcEl.GetString()!;
                var bitIdx  = ctx.Layout.GetStatusFlagBitIndex(regName, flagC);
                var statusPtr = ctx.GepStatusRegister(regName);
                var statusVal = ctx.Builder.BuildLoad2(i8, statusPtr, $"{outName}_f_in");
                cIn = ctx.Builder.BuildAnd(
                          ctx.Builder.BuildLShr(statusVal,
                              LLVMValueRef.CreateConstInt(i8, (uint)bitIdx, false), $"{outName}_c_shr"),
                          LLVMValueRef.CreateConstInt(i8, 1, false), $"{outName}_c_in");
            }

            LLVMValueRef result;
            LLVMValueRef cOut;
            switch (kind)
            {
                case "rlc":
                    cOut   = topBit;
                    result = ctx.Builder.BuildOr(
                                 ctx.Builder.BuildShl(v, LLVMValueRef.CreateConstInt(i8, 1, false), $"{outName}_shl"),
                                 cOut, outName);
                    break;
                case "rrc":
                    cOut   = lowBit;
                    result = ctx.Builder.BuildOr(
                                 ctx.Builder.BuildLShr(v, LLVMValueRef.CreateConstInt(i8, 1, false), $"{outName}_shr"),
                                 ctx.Builder.BuildShl(cOut, LLVMValueRef.CreateConstInt(i8, 7, false), $"{outName}_top_in"),
                                 outName);
                    break;
                case "rl":
                    cOut   = topBit;
                    result = ctx.Builder.BuildOr(
                                 ctx.Builder.BuildShl(v, LLVMValueRef.CreateConstInt(i8, 1, false), $"{outName}_shl"),
                                 cIn, outName);
                    break;
                case "rr":
                    cOut   = lowBit;
                    result = ctx.Builder.BuildOr(
                                 ctx.Builder.BuildLShr(v, LLVMValueRef.CreateConstInt(i8, 1, false), $"{outName}_shr"),
                                 ctx.Builder.BuildShl(cIn, LLVMValueRef.CreateConstInt(i8, 7, false), $"{outName}_top_in"),
                                 outName);
                    break;
                case "sla":
                    cOut   = topBit;
                    result = ctx.Builder.BuildShl(v, LLVMValueRef.CreateConstInt(i8, 1, false), outName);
                    break;
                case "sra":
                    cOut   = lowBit;
                    // Arithmetic right shift: keep bit 7 in place. LLVM AShr
                    // does this naturally because i8 is signed-interpretable.
                    result = ctx.Builder.BuildAShr(v, LLVMValueRef.CreateConstInt(i8, 1, false), outName);
                    break;
                case "srl":
                    cOut   = lowBit;
                    result = ctx.Builder.BuildLShr(v, LLVMValueRef.CreateConstInt(i8, 1, false), outName);
                    break;
                case "swap":
                    cOut   = LLVMValueRef.CreateConstInt(i8, 0, false);
                    var swapLo = ctx.Builder.BuildAnd(v,
                                    LLVMValueRef.CreateConstInt(i8, 0x0F, false), "swap_lo");
                    var swapHi = ctx.Builder.BuildAnd(v,
                                    LLVMValueRef.CreateConstInt(i8, 0xF0u, false), "swap_hi");
                    var hiInLo = ctx.Builder.BuildLShr(swapHi,
                                    LLVMValueRef.CreateConstInt(i8, 4, false), $"{outName}_hi_to_lo");
                    var loInHi = ctx.Builder.BuildShl(swapLo,
                                    LLVMValueRef.CreateConstInt(i8, 4, false), $"{outName}_lo_to_hi");
                    result = ctx.Builder.BuildOr(loInHi, hiInLo, outName);
                    break;
                default:
                    throw new NotSupportedException($"shift kind '{kind}' unsupported (expected rlc|rrc|rl|rr|sla|sra|srl|swap)");
            }
            ctx.Values[outName] = result;

            // Optional flag updates.
            if (step.Raw.TryGetProperty("flag_z", out var fz))
            {
                var reg  = step.Raw.GetProperty("reg").GetString()!;
                var name = fz.GetString()!;
                var zero = LLVMValueRef.CreateConstInt(i8, 0, false);
                var isZero = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, result, zero, $"{outName}_z");
                CpsrHelpers.SetStatusFlag(ctx, reg, name, isZero);
            }
            if (step.Raw.TryGetProperty("flag_n", out var fn))
            {
                var reg  = step.Raw.GetProperty("reg").GetString()!;
                var name = fn.GetString()!;
                CpsrHelpers.SetStatusFlag(ctx, reg, name, LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, 0, false));
            }
            if (step.Raw.TryGetProperty("flag_h", out var fh))
            {
                var reg  = step.Raw.GetProperty("reg").GetString()!;
                var name = fh.GetString()!;
                CpsrHelpers.SetStatusFlag(ctx, reg, name, LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, 0, false));
            }
            if (step.Raw.TryGetProperty("flag_c", out var fc))
            {
                var reg  = step.Raw.GetProperty("reg").GetString()!;
                var name = fc.GetString()!;
                var cBool = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE,
                               cOut, LLVMValueRef.CreateConstInt(i8, 0, false), $"{outName}_c_b");
                CpsrHelpers.SetStatusFlag(ctx, reg, name, cBool);
            }
        }
    }
}
