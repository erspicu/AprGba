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
        reg.Register(new UpdateHAdd());
        reg.Register(new UpdateHSub());
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
}
