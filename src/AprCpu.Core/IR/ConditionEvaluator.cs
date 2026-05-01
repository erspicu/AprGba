using AprCpu.Core.JsonSpec;
using LLVMSharp.Interop;

namespace AprCpu.Core.IR
{
    /// <summary>
    /// Emits LLVM IR that evaluates the per-instruction condition (e.g. ARM
    /// cond codes EQ / NE / CS / ...) declared by the instruction-set
    /// spec's <see cref="GlobalCondition"/> block.
    ///
    /// The 14 standard ARM cond mnemonics are recognised; the spec's
    /// <c>global_condition.table</c> picks which encoding values map to
    /// which mnemonics, so an architecture with a different cond table
    /// (e.g. a permuted ordering, or a subset) can drive this through
    /// JSON alone. Mnemonics not in the recognised set fall through to
    /// "false" (treated as never-execute / undefined).
    /// </summary>
    public static class ConditionEvaluator
    {
        /// <summary>
        /// Emit IR computing an i1 "should the current instruction execute?".
        /// Reads CPSR N/Z/C/V via <see cref="CpsrHelpers.ReadStatusFlag"/>;
        /// composes the 14 standard ARM conds + AL/NV; routes the cond
        /// field to the appropriate result via a chain of selects.
        /// </summary>
        public static LLVMValueRef EmitCheck(EmitContext ctx, GlobalCondition gc)
        {
            var cond = ctx.ExtractField(gc.Field, "cond");

            // Read CPSR flags as i32 0/1 then convert to i1.
            var nI32 = CpsrHelpers.ReadStatusFlag(ctx, "CPSR", "N");
            var zI32 = CpsrHelpers.ReadStatusFlag(ctx, "CPSR", "Z");
            var cI32 = CpsrHelpers.ReadStatusFlag(ctx, "CPSR", "C");
            var vI32 = CpsrHelpers.ReadStatusFlag(ctx, "CPSR", "V");

            var nB = ToI1(ctx, nI32, "n_b");
            var zB = ToI1(ctx, zI32, "z_b");
            var cB = ToI1(ctx, cI32, "c_b");
            var vB = ToI1(ctx, vI32, "v_b");

            // ARM cond mnemonic -> i1 result.
            // CS == HS, CC == LO are intentional aliases (ARMv4 nomenclature).
            var notZ = NotI1(ctx, zB, "not_z");
            var notC = NotI1(ctx, cB, "not_c");
            var nEqV = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, nI32, vI32, "n_eq_v");
            var nNeV = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, nI32, vI32, "n_ne_v");

            var byMnemonic = new Dictionary<string, LLVMValueRef>(StringComparer.OrdinalIgnoreCase)
            {
                ["EQ"] = zB,
                ["NE"] = notZ,
                ["CS"] = cB,
                ["HS"] = cB,
                ["CC"] = notC,
                ["LO"] = notC,
                ["MI"] = nB,
                ["PL"] = NotI1(ctx, nB, "not_n"),
                ["VS"] = vB,
                ["VC"] = NotI1(ctx, vB, "not_v"),
                ["HI"] = ctx.Builder.BuildAnd(cB, notZ, "cond_hi"),
                ["LS"] = ctx.Builder.BuildOr (notC, zB, "cond_ls"),
                ["GE"] = nEqV,
                ["LT"] = nNeV,
                ["GT"] = ctx.Builder.BuildAnd(notZ, nEqV, "cond_gt"),
                ["LE"] = ctx.Builder.BuildOr (zB,   nNeV, "cond_le"),
                ["AL"] = ctx.ConstBool(true),
                ["NV"] = ctx.ConstBool(false),
            };

            // Default for unmatched cond values: false (never execute).
            var result = ctx.ConstBool(false);

            // Walk the spec's cond table; for each (binary code, mnemonic)
            // recognised, emit a select that picks this branch's result
            // when the cond field matches the code.
            foreach (var (codeStr, mnemonic) in gc.Table)
            {
                if (!byMnemonic.TryGetValue(mnemonic, out var condResult)) continue;

                uint codeVal;
                try { codeVal = Convert.ToUInt32(codeStr, 2); }
                catch { continue; }

                var match = ctx.Builder.BuildICmp(
                    LLVMIntPredicate.LLVMIntEQ, cond, ctx.ConstU32(codeVal),
                    $"is_{mnemonic.ToLowerInvariant()}");
                result = ctx.Builder.BuildSelect(
                    match, condResult, result,
                    $"chain_{mnemonic.ToLowerInvariant()}");
            }

            return result;
        }

        private static LLVMValueRef ToI1(EmitContext ctx, LLVMValueRef i32val, string name)
            => ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, i32val, ctx.ConstU32(0), name);

        private static LLVMValueRef NotI1(EmitContext ctx, LLVMValueRef i1val, string name)
            => ctx.Builder.BuildXor(i1val, ctx.ConstBool(true), name);
    }
}
