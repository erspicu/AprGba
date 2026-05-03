using System.Text.Json;
using AprCpu.Core.JsonSpec;
using LLVMSharp.Interop;

namespace AprCpu.Core.IR;

/// <summary>
/// Generic block (multi-register) load/store emitters. Cover ARM's
/// LDM/STM family but the operation pattern (iterate a 16-bit register
/// list, load/store each at incrementing addresses, with pre/post and
/// up/down addressing variants + optional writeback) applies to many
/// architectures.
///
/// Step shape:
/// <code>
///   { "op": "block_load",
///     "list_field": &lt;name&gt;,    // 16-bit register-list field
///     "base_field": &lt;name&gt;,    // 4-bit Rn field
///     "p_field":    &lt;name&gt;,    // pre/post bit
///     "u_field":    &lt;name&gt;,    // up/down bit
///     "w_field":    &lt;name&gt; }   // writeback bit
///
///   { "op": "block_store", ...same shape... }
/// </code>
///
/// Algorithm:
///   count       = popcount(list)
///   total_bytes = count * 4
///   start_addr  = U ? base + (P ? 4 : 0)
///                   : base - total + (P ? 0 : 4)
///   final_addr  = U ? base + total : base - total
///
///   Then iterate i = 0..15 low-to-high; for each set bit, do
///   load/store at running address, then advance by 4. After the loop,
///   write final_addr back to Rn if W=1.
///
/// Edge cases NOT modelled in this first iteration (deferred to a later
/// pass that aligns with the banked-register work in 2.5.7):
///   - Rn included in the load list with W=1 (LDM-with-Rn-in-list
///     timing of when Rn is updated)
///   - LDM with PC in list (special PC update + optional T-bit
///     transition when S=1)
///   - S bit set: forces user-mode register banks regardless of
///     current mode
/// </summary>
public static class BlockTransferEmitters
{
    public static void RegisterAll(EmitterRegistry reg)
    {
        reg.Register(new BlockLoadEmitter());
        reg.Register(new BlockStoreEmitter());
    }

    /// <summary>
    /// Lazy declaration of the LLVM ctpop intrinsic.
    /// </summary>
    internal static LLVMValueRef GetCtpopI32(LLVMModuleRef module)
    {
        var name = "llvm.ctpop.i32";
        var existing = module.GetNamedFunction(name);
        if (existing.Handle != IntPtr.Zero) return existing;
        var fnType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Int32, new[] { LLVMTypeRef.Int32 });
        return module.AddFunction(name, fnType);
    }

    /// <summary>
    /// Global pointer slots for the host-bound user-mode register access
    /// functions. Used by block_load/block_store when the S-bit is set
    /// (LDM/STM with the <c>^</c> suffix).
    /// </summary>
    internal static (LLVMValueRef Slot, LLVMTypeRef FnType, LLVMTypeRef PtrType)
        GetUserRegReadSlot(LLVMModuleRef module, LLVMTypeRef statePtrType)
    {
        const string name = "host_user_reg_read";
        var fnType  = LLVMTypeRef.CreateFunction(LLVMTypeRef.Int32, new[] { statePtrType, LLVMTypeRef.Int32 });
        var ptrType = LLVMTypeRef.CreatePointer(fnType, 0);
        var existing = module.GetNamedGlobal(name);
        if (existing.Handle != IntPtr.Zero) return (existing, fnType, ptrType);
        var slot = module.AddGlobal(ptrType, name);
        slot.Linkage = LLVMLinkage.LLVMExternalLinkage;
        return (slot, fnType, ptrType);
    }

    internal static (LLVMValueRef Slot, LLVMTypeRef FnType, LLVMTypeRef PtrType)
        GetUserRegWriteSlot(LLVMModuleRef module, LLVMTypeRef statePtrType)
    {
        const string name = "host_user_reg_write";
        var fnType  = LLVMTypeRef.CreateFunction(module.Context.VoidType,
            new[] { statePtrType, LLVMTypeRef.Int32, LLVMTypeRef.Int32 });
        var ptrType = LLVMTypeRef.CreatePointer(fnType, 0);
        var existing = module.GetNamedGlobal(name);
        if (existing.Handle != IntPtr.Zero) return (existing, fnType, ptrType);
        var slot = module.AddGlobal(ptrType, name);
        slot.Linkage = LLVMLinkage.LLVMExternalLinkage;
        return (slot, fnType, ptrType);
    }
}

/// <summary>
/// Shared helpers used by both block_load and block_store. Step shape:
/// <code>
///   list:        either "list_field": &lt;name&gt; or "list": &lt;value-name&gt;
///   base:        either "base_field": &lt;name&gt; (runtime field, masked to 4 bits)
///                or     "base_index": &lt;int 0..15&gt; (compile-time register choice)
///   p / u / w:   either "_field": &lt;name&gt; or "_value": 0|1 / direct number value
/// </code>
/// The constant forms are useful for instructions whose addressing
/// behaviour is fixed (e.g. Thumb PUSH = STMDB SP! always).
/// </summary>
internal static class BlockTransferImpl
{
    public static (LLVMValueRef startAddr, LLVMValueRef finalAddr, LLVMValueRef basePtr)
        ComputeAddressing(EmitContext ctx, MicroOpStep step, out LLVMValueRef list, out LLVMValueRef wBit)
    {
        // List: either prebuilt value or instruction field
        var rawList = step.Raw.TryGetProperty("list", out var listEl)
            ? ctx.Resolve(listEl.GetString()!)
            : ctx.Resolve(step.Raw.GetProperty("list_field").GetString()!);

        // P / U / W: each may be a runtime field or a literal 0/1
        var p   = ResolveBitOrField(ctx, step.Raw, "p", "p_field");
        var u   = ResolveBitOrField(ctx, step.Raw, "u", "u_field");
        wBit    = ResolveBitOrField(ctx, step.Raw, "w", "w_field");

        // Base: either compile-time register index or runtime field
        LLVMValueRef basePtr;
        if (step.Raw.TryGetProperty("base_index", out var biEl))
        {
            int idx = biEl.GetInt32();
            basePtr = ctx.Layout.GepGpr(ctx.Builder, ctx.StatePtr, idx);
        }
        else
        {
            var baseFieldName = step.Raw.GetProperty("base_field").GetString()!;
            var rnIdx = ctx.Builder.BuildAnd(ctx.Resolve(baseFieldName), ctx.ConstU32(0xF), "blkt_rn_idx");
            basePtr = ctx.Layout.GepGprDynamic(ctx.Builder, ctx.StatePtr, rnIdx);
        }
        var baseVal = ctx.Builder.BuildLoad2(LLVMTypeRef.Int32, basePtr, "blkt_base");

        // ARM7TDMI empty-rlist quirk: when the register list is empty,
        // the instruction behaves as if {R15} were in the list (so PC is
        // loaded/stored), but the writeback advances by 0x40 (16 registers
        // worth) instead of 4. Tested by jsmolka tests 513-515.
        var listIsZero = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, rawList, ctx.ConstU32(0), "blkt_empty");
        list = ctx.Builder.BuildSelect(listIsZero, ctx.ConstU32(1u << 15), rawList, "blkt_list_eff");

        // Popcount-based total byte count, with empty-list → 0x40 override.
        var ctpop = BlockTransferEmitters.GetCtpopI32(ctx.Module);
        var ctpopType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Int32, new[] { LLVMTypeRef.Int32 });
        var count = ctx.Builder.BuildCall2(ctpopType, ctpop, new[] { rawList }, "blkt_count");
        var totalNormal = ctx.Builder.BuildShl(count, ctx.ConstU32(2), "blkt_total_normal");
        var total = ctx.Builder.BuildSelect(listIsZero, ctx.ConstU32(0x40), totalNormal, "blkt_total_bytes");

        // P==1 -> pAdj = 4, P==0 -> pAdj = 0  (and conversely for the
        // decrement branch where it picks 0 vs 4).
        var pIs1     = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, p, ctx.ConstU32(0), "p_is1");
        var pAdjInc  = ctx.Builder.BuildSelect(pIs1, ctx.ConstU32(4), ctx.ConstU32(0), "p_adj_inc");
        var pAdjDec  = ctx.Builder.BuildSelect(pIs1, ctx.ConstU32(0), ctx.ConstU32(4), "p_adj_dec");

        // Increment branch:  start = base + pAdjInc
        // Decrement branch:  start = base - total + pAdjDec
        var incStart = ctx.Builder.BuildAdd(baseVal, pAdjInc, "blkt_inc_start");
        var baseMinusTotal = ctx.Builder.BuildSub(baseVal, total, "blkt_base_minus_total");
        var decStart = ctx.Builder.BuildAdd(baseMinusTotal, pAdjDec, "blkt_dec_start");

        var uIs1 = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, u, ctx.ConstU32(0), "u_is1");
        var startAddr = ctx.Builder.BuildSelect(uIs1, incStart, decStart, "blkt_start");

        // Final address (for writeback): U ? base + total : base - total
        var incFinal = ctx.Builder.BuildAdd(baseVal, total, "blkt_inc_final");
        var decFinal = ctx.Builder.BuildSub(baseVal, total, "blkt_dec_final");
        var finalAddr = ctx.Builder.BuildSelect(uIs1, incFinal, decFinal, "blkt_final");

        return (startAddr, finalAddr, basePtr);
    }

    private static LLVMValueRef ResolveBitOrField(EmitContext ctx, JsonElement step, string constName, string fieldName)
    {
        if (step.TryGetProperty(constName, out var cEl))
        {
            return cEl.ValueKind switch
            {
                JsonValueKind.Number => ctx.ConstU32((uint)cEl.GetInt64()),
                JsonValueKind.String => ctx.Resolve(cEl.GetString()!),
                _ => throw new InvalidOperationException(
                    $"block_transfer '{constName}': must be number or string name; got {cEl.ValueKind}"),
            };
        }
        return ctx.Resolve(step.GetProperty(fieldName).GetString()!);
    }

    /// <summary>
    /// Emit the writeback step shared by load and store paths:
    /// <c>if (w_bit) *basePtr = final_addr</c>.
    /// </summary>
    public static void EmitWriteback(EmitContext ctx, LLVMValueRef wBit, LLVMValueRef basePtr, LLVMValueRef finalAddr)
    {
        var wIs1 = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, wBit, ctx.ConstU32(0), "blkt_w_is1");
        var wThen = ctx.Function.AppendBasicBlock("blkt_wb_then");
        var wEnd  = ctx.Function.AppendBasicBlock("blkt_wb_end");
        ctx.Builder.BuildCondBr(wIs1, wThen, wEnd);
        ctx.Builder.PositionAtEnd(wThen);
        ctx.Builder.BuildStore(finalAddr, basePtr);
        ctx.Builder.BuildBr(wEnd);
        ctx.Builder.PositionAtEnd(wEnd);
    }
}

internal sealed class BlockLoadEmitter : IMicroOpEmitter
{
    public string OpName => "block_load";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var (startAddr, finalAddr, basePtr) = BlockTransferImpl.ComputeAddressing(ctx, step, out var list, out var wBit);

        // ARM7TDMI: when Rn appears in the load list, the loaded value
        // wins over the writeback. Achieve this by performing the
        // writeback BEFORE the loads — any subsequent load to Rn
        // overrides what writeback wrote. (For STM the writeback stays
        // AFTER the stores; see BlockStoreEmitter.)
        BlockTransferImpl.EmitWriteback(ctx, wBit, basePtr, finalAddr);

        // Running address slot.
        var addrSlot = ctx.Builder.BuildAlloca(LLVMTypeRef.Int32, "blkl_addr");
        ctx.Builder.BuildStore(startAddr, addrSlot);

        var (read32Slot, read32Type, read32PtrType) = MemoryEmitters.GetOrDeclareMemoryFunctionPointer(
            ctx.Module, MemoryEmitters.ExternFunctionNames.Read32,
            LLVMTypeRef.Int32, LLVMTypeRef.Int32);

        var sBit = step.Raw.TryGetProperty("s_field", out var sFE)
            ? ctx.Resolve(sFE.GetString()!)
            : ctx.ConstU32(0);
        var sIsSet = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, sBit, ctx.ConstU32(0), "blkl_s_is1");
        var (userWriteSlot, userWriteType, userWritePtrType) =
            BlockTransferEmitters.GetUserRegWriteSlot(ctx.Module, ctx.Layout.PointerType);

        for (int i = 0; i < 16; i++)
        {
            var bit = ctx.Builder.BuildAnd(
                ctx.Builder.BuildLShr(list, ctx.ConstU32((uint)i), $"blkl_shr_{i}"),
                ctx.ConstU32(1), $"blkl_bit_{i}");
            var isSet = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, bit, ctx.ConstU32(0), $"blkl_set_{i}");

            var thenBB = ctx.Function.AppendBasicBlock($"blkl_{i}_then");
            var endBB  = ctx.Function.AppendBasicBlock($"blkl_{i}_end");
            ctx.Builder.BuildCondBr(isSet, thenBB, endBB);
            ctx.Builder.PositionAtEnd(thenBB);

            var curAddr = ctx.Builder.BuildLoad2(LLVMTypeRef.Int32, addrSlot, $"blkl_a{i}");
            var read32Fn = ctx.Builder.BuildLoad2(read32PtrType, read32Slot, $"blkl_read32_{i}");
            var loaded  = ctx.Builder.BuildCall2(read32Type, read32Fn, new[] { curAddr }, $"blkl_v{i}");

            // Conditional write: if S=1 → user-mode reg write, else visible.
            var userWriteBB    = ctx.Function.AppendBasicBlock($"blkl_{i}_user");
            var visibleWriteBB = ctx.Function.AppendBasicBlock($"blkl_{i}_vis");
            var afterWriteBB   = ctx.Function.AppendBasicBlock($"blkl_{i}_afterw");
            ctx.Builder.BuildCondBr(sIsSet, userWriteBB, visibleWriteBB);

            ctx.Builder.PositionAtEnd(userWriteBB);
            var userWriteFn = ctx.Builder.BuildLoad2(userWritePtrType, userWriteSlot, $"blkl_uwrite_{i}");
            ctx.Builder.BuildCall2(userWriteType, userWriteFn,
                new[] { ctx.StatePtr, ctx.ConstU32((uint)i), loaded }, "");
            // Phase 7 A.6.1 Strategy 2 — same MarkPcWritten as visible
            // path: if i==15, the user-mode write also writes PC (LDM ^
            // exception-return form). MUST go through WriteReg.MarkPcWritten
            // (not direct store) so ctx.PcWriteEmittedInCurrentInstruction
            // is set — required for the subsequent read_reg(15) in spec
            // patterns like Thumb POP {PC}'s alignment step to read from
            // memory instead of short-circuiting to the pipeline constant.
            if (i == 15) WriteReg.MarkPcWritten(ctx);
            ctx.Builder.BuildBr(afterWriteBB);

            ctx.Builder.PositionAtEnd(visibleWriteBB);
            var rPtr = ctx.Layout.GepGpr(ctx.Builder, ctx.StatePtr, i);
            ctx.Builder.BuildStore(loaded, rPtr);
            // Phase 7 A.6.1 Strategy 2 — when LDM loads into PC (R15)
            // it's an indirect branch. Mark PcWritten so block-JIT's
            // post-instr check exits the block; otherwise executor's
            // "no branch → advance PC by N×size" path overwrites the
            // loaded target. Affects ARM `LDM ... {..., PC}` and
            // Thumb `POP {..., PC}` (delegated to block_load). MUST go
            // through WriteReg.MarkPcWritten (not direct store) — see
            // userWriteBB comment.
            if (i == 15) WriteReg.MarkPcWritten(ctx);
            ctx.Builder.BuildBr(afterWriteBB);

            ctx.Builder.PositionAtEnd(afterWriteBB);
            var nextAddr = ctx.Builder.BuildAdd(curAddr, ctx.ConstU32(4), $"blkl_a{i}_next");
            ctx.Builder.BuildStore(nextAddr, addrSlot);

            ctx.Builder.BuildBr(endBB);
            ctx.Builder.PositionAtEnd(endBB);
        }
    }
}

internal sealed class BlockStoreEmitter : IMicroOpEmitter
{
    public string OpName => "block_store";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var (startAddr, finalAddr, basePtr) = BlockTransferImpl.ComputeAddressing(ctx, step, out var list, out var wBit);

        var addrSlot = ctx.Builder.BuildAlloca(LLVMTypeRef.Int32, "blks_addr");
        ctx.Builder.BuildStore(startAddr, addrSlot);

        var (write32Slot, write32Type, write32PtrType) = MemoryEmitters.GetOrDeclareMemoryFunctionPointer(
            ctx.Module, MemoryEmitters.ExternFunctionNames.Write32,
            ctx.Module.Context.VoidType, LLVMTypeRef.Int32, LLVMTypeRef.Int32);

        // S-bit (user-mode register access) — optional. Default 0 = visible regs.
        var sBit = step.Raw.TryGetProperty("s_field", out var sFE)
            ? ctx.Resolve(sFE.GetString()!)
            : ctx.ConstU32(0);
        var sIsSet = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, sBit, ctx.ConstU32(0), "blks_s_is1");
        var (userReadSlot, userReadType, userReadPtrType) =
            BlockTransferEmitters.GetUserRegReadSlot(ctx.Module, ctx.Layout.PointerType);

        // Resolve Rn so we can detect the ARM7TDMI "Rn in list but not
        // first" quirk: if Rn is the lowest set bit in the list the
        // ORIGINAL value goes to memory; otherwise the post-writeback
        // value goes to memory.
        var rnIdx = step.Raw.TryGetProperty("base_index", out var rbiEl)
            ? ctx.ConstU32((uint)rbiEl.GetInt32())
            : ctx.Builder.BuildAnd(
                ctx.Resolve(step.Raw.GetProperty("base_field").GetString()!),
                ctx.ConstU32(0xF), "blks_rn_idx");

        for (int i = 0; i < 16; i++)
        {
            var bit = ctx.Builder.BuildAnd(
                ctx.Builder.BuildLShr(list, ctx.ConstU32((uint)i), $"blks_shr_{i}"),
                ctx.ConstU32(1), $"blks_bit_{i}");
            var isSet = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, bit, ctx.ConstU32(0), $"blks_set_{i}");

            var thenBB = ctx.Function.AppendBasicBlock($"blks_{i}_then");
            var endBB  = ctx.Function.AppendBasicBlock($"blks_{i}_end");
            ctx.Builder.BuildCondBr(isSet, thenBB, endBB);
            ctx.Builder.PositionAtEnd(thenBB);

            var curAddr = ctx.Builder.BuildLoad2(LLVMTypeRef.Int32, addrSlot, $"blks_a{i}");
            // Visible R[i] read.
            // Phase 7 A.6.1 Strategy 2 — for i=15 (PC), block-JIT must NOT
            // load GPR[15] from memory because Strategy 2 doesn't pre-set
            // it (memory holds the prior block's branch target). Use the
            // pipeline PC constant directly. Per-instr falls back to the
            // memory load (executor's pre-set R15 is valid there).
            LLVMValueRef visibleVal;
            if (i == 15 && ctx.PipelinePcConstant is uint pipelineValue)
            {
                visibleVal = ctx.ConstU32(pipelineValue);
            }
            else
            {
                var rPtr   = ctx.Layout.GepGpr(ctx.Builder, ctx.StatePtr, i);
                visibleVal = ctx.Builder.BuildLoad2(LLVMTypeRef.Int32, rPtr, $"blks_r{i}");
            }
            // User-mode R[i] read via host extern (only meaningful when S=1).
            var userReadFn  = ctx.Builder.BuildLoad2(userReadPtrType, userReadSlot, $"blks_uread_{i}");
            var userVal     = ctx.Builder.BuildCall2(userReadType, userReadFn,
                new[] { ctx.StatePtr, ctx.ConstU32((uint)i) }, $"blks_user{i}");
            var chosen      = ctx.Builder.BuildSelect(sIsSet, userVal, visibleVal, $"blks_chosen{i}");
            // ARM7TDMI quirk: when R15 is in the store list, the stored
            // value is R15 read + instruction_size (= current_pc + 2 ×
            // instr_size). For ARM that's R15 + 4 (= current_pc + 12);
            // for Thumb it's R15 + 2 (= current_pc + 6).
            uint instrSize = (uint)(ctx.InstructionSet.WidthBits.Fixed!.Value / 8);
            var chosenAdj   = i == 15
                ? ctx.Builder.BuildAdd(chosen, ctx.ConstU32(instrSize), $"blks_r{i}_pc")
                : chosen;

            // ARM7TDMI quirk: if i == Rn and Rn is NOT the first register
            // in the list, store the post-writeback value instead. For
            // i=0 this can never trigger (R0 is always first if in list).
            LLVMValueRef storeVal = chosenAdj;
            if (i > 0)
            {
                var iEqRn = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, rnIdx, ctx.ConstU32((uint)i), $"blks_i_eq_rn_{i}");
                // Any lower bits set in list?  list & ((1<<i)-1) != 0 means i is NOT first.
                var lowMask = ctx.ConstU32((1u << i) - 1u);
                var lowBits = ctx.Builder.BuildAnd(list, lowMask, $"blks_lowbits_{i}");
                var hasLower = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, lowBits, ctx.ConstU32(0), $"blks_haslower_{i}");
                var useWB = ctx.Builder.BuildAnd(iEqRn, hasLower, $"blks_useWB_{i}");
                storeVal = ctx.Builder.BuildSelect(useWB, finalAddr, chosenAdj, $"blks_storeval_{i}");
            }

            var write32Fn = ctx.Builder.BuildLoad2(write32PtrType, write32Slot, $"blks_write32_{i}");
            ctx.Builder.BuildCall2(write32Type, write32Fn, new[] { curAddr, storeVal }, "");
            var nextAddr = ctx.Builder.BuildAdd(curAddr, ctx.ConstU32(4), $"blks_a{i}_next");
            ctx.Builder.BuildStore(nextAddr, addrSlot);

            ctx.Builder.BuildBr(endBB);
            ctx.Builder.PositionAtEnd(endBB);
        }

        BlockTransferImpl.EmitWriteback(ctx, wBit, basePtr, finalAddr);
    }
}
