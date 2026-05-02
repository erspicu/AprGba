using AprCpu.Core.JsonSpec;
using LLVMSharp.Interop;

namespace AprCpu.Core.IR;

/// <summary>
/// Spec-driven L1 generic flag-update micro-ops shared across all CPUs.
/// They share the same { reg, flag } / { reg, flag, in } shape as the
/// existing ARM-style update_nz / update_c_add etc, but cover patterns
/// the ARM-flavoured ops don't:
///
/// <list type="bullet">
///   <item><c>set_flag { reg, flag, value: 0|1 }</c> — write a fixed bit.
///         Replaces hand-rolled SCF/CCF/CPL flag-mask logic in the
///         LR35902 emitters.</item>
///   <item><c>update_h_add { in: [a, b], reg, flag, boundary?: 4 }</c> —
///         half-carry on add: H = (((a &amp; mask) + (b &amp; mask)) &gt; mask),
///         where mask = (1 &lt;&lt; boundary) - 1. GB DMG default
///         boundary=4 (bit 3→4 carry on 8-bit ops); other architectures
///         with auxiliary-carry style flags (x86 AF) supply their own.</item>
///   <item><c>update_h_sub { in: [a, b], reg, flag, boundary?: 4 }</c> —
///         half-borrow: H = ((a &amp; mask) &lt; (b &amp; mask)).</item>
/// </list>
///
/// Registered into the cross-arch registry by <see cref="StandardEmitters"/>
/// so any spec (ARM / GB / future RISC-V / 6502) can use them.
/// </summary>
internal static class FlagOps
{
    public static void RegisterAll(EmitterRegistry reg)
    {
        reg.Register(new SetFlag());
        reg.Register(new ToggleFlag());
        reg.Register(new UpdateHAdd());
        reg.Register(new UpdateHSub());
        reg.Register(new UpdateZero());
        reg.Register(new UpdateHInc());
        reg.Register(new UpdateHDec());
    }

    private sealed class SetFlag : IMicroOpEmitter
    {
        public string OpName => "set_flag";
        public void Emit(EmitContext ctx, MicroOpStep step)
        {
            var reg  = step.Raw.GetProperty("reg").GetString()!;
            var flag = step.Raw.GetProperty("flag").GetString()!;
            var v    = step.Raw.GetProperty("value").GetInt32();
            var b    = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, (uint)(v & 1), false);
            CpsrHelpers.SetStatusFlag(ctx, reg, flag, b);
        }
    }

    /// <summary>
    /// toggle_flag { reg, flag } — read the named flag bit, XOR with 1,
    /// write it back. Used by LR35902 CCF (toggle carry); maps to
    /// x86 CMC and any "flip flag" instruction in other ISAs.
    /// </summary>
    private sealed class ToggleFlag : IMicroOpEmitter
    {
        public string OpName => "toggle_flag";
        public void Emit(EmitContext ctx, MicroOpStep step)
        {
            var reg  = step.Raw.GetProperty("reg").GetString()!;
            var flag = step.Raw.GetProperty("flag").GetString()!;
            // Read flag as i32 0/1, XOR with 1, write back as i1.
            var oldBit = CpsrHelpers.ReadStatusFlag(ctx, reg, flag);
            var newBit = ctx.Builder.BuildXor(oldBit,
                            LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 1, false),
                            $"toggle_{flag.ToLowerInvariant()}");
            // Truncate to i1 for SetStatusFlag.
            var newI1 = ctx.Builder.BuildTrunc(newBit, LLVMTypeRef.Int1, $"toggle_{flag.ToLowerInvariant()}_b");
            CpsrHelpers.SetStatusFlag(ctx, reg, flag, newI1);
        }
    }

    private sealed class UpdateHAdd : IMicroOpEmitter
    {
        public string OpName => "update_h_add";
        public void Emit(EmitContext ctx, MicroOpStep step)
        {
            var a = StandardEmitters.ResolveInput(ctx, step.Raw, 0);
            var b = StandardEmitters.ResolveInput(ctx, step.Raw, 1);
            var reg  = step.Raw.GetProperty("reg").GetString()!;
            var flag = step.Raw.GetProperty("flag").GetString()!;
            var boundary = step.Raw.TryGetProperty("boundary", out var bd) ? bd.GetInt32() : 4;
            uint mask = (1u << boundary) - 1;
            var aLow = ctx.Builder.BuildAnd(a, ctx.ConstU32(mask), "h_add_a_low");
            var bLow = ctx.Builder.BuildAnd(b, ctx.ConstU32(mask), "h_add_b_low");
            var sum  = ctx.Builder.BuildAdd(aLow, bLow, "h_add_sum");
            var hBool = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntUGT,
                           sum, ctx.ConstU32(mask), "h_add_test");
            CpsrHelpers.SetStatusFlag(ctx, reg, flag, hBool);
        }
    }

    private sealed class UpdateHSub : IMicroOpEmitter
    {
        public string OpName => "update_h_sub";
        public void Emit(EmitContext ctx, MicroOpStep step)
        {
            var a = StandardEmitters.ResolveInput(ctx, step.Raw, 0);
            var b = StandardEmitters.ResolveInput(ctx, step.Raw, 1);
            var reg  = step.Raw.GetProperty("reg").GetString()!;
            var flag = step.Raw.GetProperty("flag").GetString()!;
            var boundary = step.Raw.TryGetProperty("boundary", out var bd) ? bd.GetInt32() : 4;
            uint mask = (1u << boundary) - 1;
            var aLow = ctx.Builder.BuildAnd(a, ctx.ConstU32(mask), "h_sub_a_low");
            var bLow = ctx.Builder.BuildAnd(b, ctx.ConstU32(mask), "h_sub_b_low");
            var hBool = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntULT,
                           aLow, bLow, "h_sub_test");
            CpsrHelpers.SetStatusFlag(ctx, reg, flag, hBool);
        }
    }

    /// <summary>
    /// update_zero { in, reg, flag } — Z = (in == 0). Compares the input
    /// value to zero at its native width; the spec is responsible for
    /// truncating beforehand if it cares about a specific byte/word range.
    /// </summary>
    private sealed class UpdateZero : IMicroOpEmitter
    {
        public string OpName => "update_zero";
        public void Emit(EmitContext ctx, MicroOpStep step)
        {
            var inName = step.Raw.GetProperty("in").GetString()!;
            var v = ctx.Resolve(inName);
            var zero = LLVMValueRef.CreateConstInt(v.TypeOf, 0, false);
            var isZero = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, v, zero, $"upd_z_{inName}");
            var reg  = step.Raw.GetProperty("reg").GetString()!;
            var flag = step.Raw.GetProperty("flag").GetString()!;
            CpsrHelpers.SetStatusFlag(ctx, reg, flag, isZero);
        }
    }

    /// <summary>
    /// update_h_inc { in, reg, flag, boundary?: 4 } — H from increment:
    /// H = ((in &amp; mask) == 0). For LR35902 INC r the new value's low
    /// nibble being 0 means a carry crossed bit 3 during +1.
    /// </summary>
    private sealed class UpdateHInc : IMicroOpEmitter
    {
        public string OpName => "update_h_inc";
        public void Emit(EmitContext ctx, MicroOpStep step)
        {
            var inName = step.Raw.GetProperty("in").GetString()!;
            var v = ctx.Resolve(inName);
            var boundary = step.Raw.TryGetProperty("boundary", out var bd) ? bd.GetInt32() : 4;
            ulong mask = (1UL << boundary) - 1;
            var maskC = LLVMValueRef.CreateConstInt(v.TypeOf, mask, false);
            var lowNib = ctx.Builder.BuildAnd(v, maskC, $"h_inc_{inName}_low");
            var zero = LLVMValueRef.CreateConstInt(v.TypeOf, 0, false);
            var hBool = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, lowNib, zero, $"h_inc_{inName}");
            var reg  = step.Raw.GetProperty("reg").GetString()!;
            var flag = step.Raw.GetProperty("flag").GetString()!;
            CpsrHelpers.SetStatusFlag(ctx, reg, flag, hBool);
        }
    }

    /// <summary>
    /// update_h_dec { in, reg, flag, boundary?: 4 } — H from decrement:
    /// H = ((in &amp; mask) == mask). For LR35902 DEC r the new value's
    /// low nibble being 0xF means a borrow crossed bit 4 during −1.
    /// </summary>
    private sealed class UpdateHDec : IMicroOpEmitter
    {
        public string OpName => "update_h_dec";
        public void Emit(EmitContext ctx, MicroOpStep step)
        {
            var inName = step.Raw.GetProperty("in").GetString()!;
            var v = ctx.Resolve(inName);
            var boundary = step.Raw.TryGetProperty("boundary", out var bd) ? bd.GetInt32() : 4;
            ulong mask = (1UL << boundary) - 1;
            var maskC = LLVMValueRef.CreateConstInt(v.TypeOf, mask, false);
            var lowNib = ctx.Builder.BuildAnd(v, maskC, $"h_dec_{inName}_low");
            var hBool = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, lowNib, maskC, $"h_dec_{inName}");
            var reg  = step.Raw.GetProperty("reg").GetString()!;
            var flag = step.Raw.GetProperty("flag").GetString()!;
            CpsrHelpers.SetStatusFlag(ctx, reg, flag, hBool);
        }
    }
}
