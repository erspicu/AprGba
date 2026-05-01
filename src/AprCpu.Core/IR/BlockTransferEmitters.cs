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
}

/// <summary>
/// Shared helpers used by both block_load and block_store.
/// </summary>
internal static class BlockTransferImpl
{
    public static (LLVMValueRef startAddr, LLVMValueRef finalAddr, LLVMValueRef rnIdx)
        ComputeAddressing(EmitContext ctx, MicroOpStep step, out LLVMValueRef list, out LLVMValueRef wBit)
    {
        var listFieldName = step.Raw.GetProperty("list_field").GetString()!;
        var baseFieldName = step.Raw.GetProperty("base_field").GetString()!;
        var pFieldName    = step.Raw.GetProperty("p_field").GetString()!;
        var uFieldName    = step.Raw.GetProperty("u_field").GetString()!;
        var wFieldName    = step.Raw.GetProperty("w_field").GetString()!;

        list = ctx.Resolve(listFieldName);     // i32 value (low 16 bits = register list)
        var p   = ctx.Resolve(pFieldName);
        var u   = ctx.Resolve(uFieldName);
        wBit    = ctx.Resolve(wFieldName);
        var rnIdx = ctx.Builder.BuildAnd(ctx.Resolve(baseFieldName), ctx.ConstU32(0xF), "blkt_rn_idx");

        var basePtr = ctx.Layout.GepGprDynamic(ctx.Builder, ctx.StatePtr, rnIdx);
        var baseVal = ctx.Builder.BuildLoad2(LLVMTypeRef.Int32, basePtr, "blkt_base");

        // Popcount-based total byte count.
        var ctpop = BlockTransferEmitters.GetCtpopI32(ctx.Module);
        var ctpopType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Int32, new[] { LLVMTypeRef.Int32 });
        var count = ctx.Builder.BuildCall2(ctpopType, ctpop, new[] { list }, "blkt_count");
        var total = ctx.Builder.BuildShl(count, ctx.ConstU32(2), "blkt_total_bytes");

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

        return (startAddr, finalAddr, rnIdx);
    }

    /// <summary>
    /// Emit the writeback step shared by load and store paths:
    /// <c>if (w_bit) R[rn] = final_addr</c>.
    /// </summary>
    public static void EmitWriteback(EmitContext ctx, LLVMValueRef wBit, LLVMValueRef rnIdx, LLVMValueRef finalAddr)
    {
        var wIs1 = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, wBit, ctx.ConstU32(0), "blkt_w_is1");
        var wThen = ctx.Function.AppendBasicBlock("blkt_wb_then");
        var wEnd  = ctx.Function.AppendBasicBlock("blkt_wb_end");
        ctx.Builder.BuildCondBr(wIs1, wThen, wEnd);
        ctx.Builder.PositionAtEnd(wThen);
        var rnPtr = ctx.Layout.GepGprDynamic(ctx.Builder, ctx.StatePtr, rnIdx);
        ctx.Builder.BuildStore(finalAddr, rnPtr);
        ctx.Builder.BuildBr(wEnd);
        ctx.Builder.PositionAtEnd(wEnd);
    }
}

internal sealed class BlockLoadEmitter : IMicroOpEmitter
{
    public string OpName => "block_load";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var (startAddr, finalAddr, rnIdx) = BlockTransferImpl.ComputeAddressing(ctx, step, out var list, out var wBit);

        // Running address slot.
        var addrSlot = ctx.Builder.BuildAlloca(LLVMTypeRef.Int32, "blkl_addr");
        ctx.Builder.BuildStore(startAddr, addrSlot);

        var read32Fn = MemoryEmitters.GetOrDeclareMemoryFunction(
            ctx.Module, MemoryEmitters.ExternFunctionNames.Read32,
            LLVMTypeRef.Int32, LLVMTypeRef.Int32);
        var read32Type = LLVMTypeRef.CreateFunction(LLVMTypeRef.Int32, new[] { LLVMTypeRef.Int32 });

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
            var loaded  = ctx.Builder.BuildCall2(read32Type, read32Fn, new[] { curAddr }, $"blkl_v{i}");
            var rPtr    = ctx.Layout.GepGpr(ctx.Builder, ctx.StatePtr, i);
            ctx.Builder.BuildStore(loaded, rPtr);
            var nextAddr = ctx.Builder.BuildAdd(curAddr, ctx.ConstU32(4), $"blkl_a{i}_next");
            ctx.Builder.BuildStore(nextAddr, addrSlot);

            ctx.Builder.BuildBr(endBB);
            ctx.Builder.PositionAtEnd(endBB);
        }

        BlockTransferImpl.EmitWriteback(ctx, wBit, rnIdx, finalAddr);
    }
}

internal sealed class BlockStoreEmitter : IMicroOpEmitter
{
    public string OpName => "block_store";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var (startAddr, finalAddr, rnIdx) = BlockTransferImpl.ComputeAddressing(ctx, step, out var list, out var wBit);

        var addrSlot = ctx.Builder.BuildAlloca(LLVMTypeRef.Int32, "blks_addr");
        ctx.Builder.BuildStore(startAddr, addrSlot);

        var write32Fn = MemoryEmitters.GetOrDeclareMemoryFunction(
            ctx.Module, MemoryEmitters.ExternFunctionNames.Write32,
            ctx.Module.Context.VoidType, LLVMTypeRef.Int32, LLVMTypeRef.Int32);
        var write32Type = LLVMTypeRef.CreateFunction(
            ctx.Module.Context.VoidType, new[] { LLVMTypeRef.Int32, LLVMTypeRef.Int32 });

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
            var rPtr    = ctx.Layout.GepGpr(ctx.Builder, ctx.StatePtr, i);
            var rVal    = ctx.Builder.BuildLoad2(LLVMTypeRef.Int32, rPtr, $"blks_r{i}");
            ctx.Builder.BuildCall2(write32Type, write32Fn, new[] { curAddr, rVal }, "");
            var nextAddr = ctx.Builder.BuildAdd(curAddr, ctx.ConstU32(4), $"blks_a{i}_next");
            ctx.Builder.BuildStore(nextAddr, addrSlot);

            ctx.Builder.BuildBr(endBB);
            ctx.Builder.PositionAtEnd(endBB);
        }

        BlockTransferImpl.EmitWriteback(ctx, wBit, rnIdx, finalAddr);
    }
}
