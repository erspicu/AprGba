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

        // BX-style indirect branch (T-bit selects ARM/Thumb)
        reg.Register(new BranchIndirectArm());

        // Exception entry (vectors). Phase 2.5.5 first-iteration stub.
        reg.Register(new RaiseExceptionEmitter());

        // Stub / placeholder
        reg.Register(new RestoreCpsrFromSpsr());
    }
}

// ---------------- ARM-only flag updaters ----------------

internal sealed class UpdateNz : IMicroOpEmitter
{
    public string OpName => "update_nz";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var valueName = step.Raw.GetProperty("value").GetString()!;
        var value = ctx.Resolve(valueName);

        var nBool = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntSLT, value, ctx.ConstU32(0), "n_test");
        var zBool = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ,  value, ctx.ConstU32(0), "z_test");

        CpsrHelpers.SetStatusFlag(ctx, "CPSR", "N", nBool);
        CpsrHelpers.SetStatusFlag(ctx, "CPSR", "Z", zBool);
    }
}

internal sealed class UpdateCAdd : IMicroOpEmitter
{
    public string OpName => "update_c_add";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var a = StandardEmitters.ResolveInput(ctx, step.Raw, 0);
        var b = StandardEmitters.ResolveInput(ctx, step.Raw, 1);
        var sum = ctx.Builder.BuildAdd(a, b, "c_add_sum");
        var cBool = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntULT, sum, a, "c_add_test");
        CpsrHelpers.SetStatusFlag(ctx, "CPSR", "C", cBool);
    }
}

internal sealed class UpdateCSub : IMicroOpEmitter
{
    public string OpName => "update_c_sub";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var a = StandardEmitters.ResolveInput(ctx, step.Raw, 0);
        var b = StandardEmitters.ResolveInput(ctx, step.Raw, 1);
        var cBool = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntUGE, a, b, "c_sub_test");
        CpsrHelpers.SetStatusFlag(ctx, "CPSR", "C", cBool);
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
        var aXor = ctx.Builder.BuildXor(a, res, "v_add_axor");
        var bXor = ctx.Builder.BuildXor(b, res, "v_add_bxor");
        var both = ctx.Builder.BuildAnd(aXor, bXor, "v_add_and");
        var top  = ctx.Builder.BuildLShr(both, ctx.ConstU32(31), "v_add_top");
        CpsrHelpers.SetStatusFlagFromI32Lsb(ctx, "CPSR", "V", top);
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
        var ab   = ctx.Builder.BuildXor(a, b,   "v_sub_ab");
        var ares = ctx.Builder.BuildXor(a, res, "v_sub_ares");
        var both = ctx.Builder.BuildAnd(ab, ares, "v_sub_and");
        var top  = ctx.Builder.BuildLShr(both, ctx.ConstU32(31), "v_sub_top");
        CpsrHelpers.SetStatusFlagFromI32Lsb(ctx, "CPSR", "V", top);
    }
}

internal sealed class UpdateCShifter : IMicroOpEmitter
{
    public string OpName => "update_c_shifter";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var v = StandardEmitters.ResolveInput(ctx, step.Raw, 0);
        CpsrHelpers.SetStatusFlagFromI32Lsb(ctx, "CPSR", "C", v);
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

        var i64 = LLVMTypeRef.Int64;
        var aL = ctx.Builder.BuildZExt(a,   i64, "a_l");
        var bL = ctx.Builder.BuildZExt(b,   i64, "b_l");
        var cL = ctx.Builder.BuildZExt(cin, i64, "c_l");
        var sum = ctx.Builder.BuildAdd(ctx.Builder.BuildAdd(aL, bL, "ab_l"), cL, "abc_l");
        var hi  = ctx.Builder.BuildLShr(sum, LLVMValueRef.CreateConstInt(i64, 32, false), "carry_hi");
        var hi32 = ctx.Builder.BuildTrunc(hi, LLVMTypeRef.Int32, "carry_hi_i32");
        CpsrHelpers.SetStatusFlagFromI32Lsb(ctx, "CPSR", "C", hi32);
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
        var notCin = ctx.Builder.BuildXor(cin, ctx.ConstU32(1), "not_cin");
        var bPlus  = ctx.Builder.BuildAdd(b, notCin, "b_plus");
        var cBool  = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntUGE, a, bPlus, "c_subc_test");
        CpsrHelpers.SetStatusFlag(ctx, "CPSR", "C", cBool);
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
        var target = which == "SPSR" ? "CPSR" : (which ?? "CPSR");
        var ptr = ctx.Layout.GepStatusRegister(ctx.Builder, ctx.StatePtr, target);

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

        var alignMask = ctx.ConstU32(0xFFFFFFFEu);
        var aligned   = ctx.Builder.BuildAnd(target, alignMask, "bx_aligned");

        var pcSlot = ctx.Layout.GepGpr(ctx.Builder, ctx.StatePtr, 15);
        ctx.Builder.BuildStore(aligned, pcSlot);
    }
}

internal sealed class RestoreCpsrFromSpsr : IMicroOpEmitter
{
    public string OpName => "restore_cpsr_from_spsr";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        // Stub: real semantics need mode-aware SPSR selection. Lands in
        // Phase 2.5.7 (banked register swap).
        var cpsrPtr = ctx.Layout.GepStatusRegister(ctx.Builder, ctx.StatePtr, "CPSR");
        var _ = ctx.Builder.BuildLoad2(LLVMTypeRef.Int32, cpsrPtr, "cpsr_stub_load");
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
        uint? address = null;
        foreach (var v in ctx.Layout.ExceptionVectors)
        {
            if (string.Equals(v.Name, vectorName, StringComparison.Ordinal))
            {
                address = v.Address;
                break;
            }
        }
        if (address is null)
        {
            throw new InvalidOperationException(
                $"raise_exception: unknown vector '{vectorName}'. Declared vectors: " +
                string.Join(", ", ctx.Layout.ExceptionVectors.Select(v => v.Name)));
        }

        // Phase 2.5.5b first-iteration stub: jump to vector address.
        // 2.5.7 will add: SPSR save, mode switch, LR save, banked register swap.
        var pcSlot = ctx.Layout.GepGpr(ctx.Builder, ctx.StatePtr, 15);
        ctx.Builder.BuildStore(ctx.ConstU32(address.Value), pcSlot);
    }
}

// ---------------- shared ARM helper ----------------

/// <summary>Reads CPSR.C as an i32 0/1 value for use as a carry-in operand.</summary>
internal static class CarryReader
{
    public static LLVMValueRef ReadCarryIn(EmitContext ctx)
        => CpsrHelpers.ReadStatusFlag(ctx, "CPSR", "C");
}
