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

        // Per-instruction condition gate (Thumb F16 conditional branch).
        reg.Register(new IfArmCondEmitter());

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
        var swapFn = HostHelpers.GetSwapRegisterBankFn(ctx.Module, ctx.Layout.PointerType);
        var swapType = LLVMTypeRef.CreateFunction(
            ctx.Module.Context.VoidType,
            new[] { ctx.Layout.PointerType, LLVMTypeRef.Int32, LLVMTypeRef.Int32 });
        ctx.Builder.BuildCall2(swapType, swapFn,
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
        const uint modeMask = 0x1F;
        var clearedMode = ctx.Builder.BuildAnd(oldCpsr, ctx.ConstU32(~modeMask), "cpsr_no_mode");
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
        var swapFn = HostHelpers.GetSwapRegisterBankFn(ctx.Module, ctx.Layout.PointerType);
        var swapType = LLVMTypeRef.CreateFunction(
            ctx.Module.Context.VoidType,
            new[] { ctx.Layout.PointerType, LLVMTypeRef.Int32, LLVMTypeRef.Int32 });
        ctx.Builder.BuildCall2(swapType, swapFn,
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

    public static LLVMValueRef GetSwapRegisterBankFn(LLVMModuleRef module, LLVMTypeRef statePtrType)
    {
        var existing = module.GetNamedFunction(SwapRegisterBank);
        if (existing.Handle != IntPtr.Zero) return existing;
        var fnType = LLVMTypeRef.CreateFunction(
            module.Context.VoidType,
            new[] { statePtrType, LLVMTypeRef.Int32, LLVMTypeRef.Int32 });
        return module.AddFunction(SwapRegisterBank, fnType);
    }
}

// ---------------- shared ARM helper ----------------

/// <summary>Reads CPSR.C as an i32 0/1 value for use as a carry-in operand.</summary>
internal static class CarryReader
{
    public static LLVMValueRef ReadCarryIn(EmitContext ctx)
        => CpsrHelpers.ReadStatusFlag(ctx, "CPSR", "C");
}
