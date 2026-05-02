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
}
