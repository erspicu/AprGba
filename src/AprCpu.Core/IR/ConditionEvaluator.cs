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
            return EmitCheckOnCondValue(ctx, cond, gc.Table);
        }

        /// <summary>
        /// Standard ARMv4 condition mnemonic table (used as default when the
        /// caller doesn't pass an explicit table — typical for Thumb F16
        /// conditional branches whose cond field is per-instruction rather
        /// than a global gate).
        /// </summary>
        public static readonly IReadOnlyDictionary<string, string> StandardArmCondTable
            = new Dictionary<string, string>
            {
                ["0000"] = "EQ", ["0001"] = "NE",
                ["0010"] = "CS", ["0011"] = "CC",
                ["0100"] = "MI", ["0101"] = "PL",
                ["0110"] = "VS", ["0111"] = "VC",
                ["1000"] = "HI", ["1001"] = "LS",
                ["1010"] = "GE", ["1011"] = "LT",
                ["1100"] = "GT", ["1101"] = "LE",
                ["1110"] = "AL", ["1111"] = "NV",
            };

        /// <summary>
        /// Same as <see cref="EmitCheck"/> but takes the cond value as an
        /// already-resolved i32 (typically a step-output or a field
        /// extraction the caller did itself). Useful for instructions like
        /// Thumb F16 that carry their own per-instruction cond field
        /// rather than going through the instruction-set-wide cond gate.
        /// </summary>
        public static unsafe LLVMValueRef EmitCheckOnCondValue(
            EmitContext ctx,
            LLVMValueRef cond,
            IReadOnlyDictionary<string, string>? table = null)
        {
            table ??= StandardArmCondTable;

            // Phase 7 A.6.1 Golden Fix (Gemini-suggested, mGBA/Dolphin/
            // Ryujinx pattern) — when cond is a compile-time constant
            // (block-JIT mode bakes the instruction word as a constant),
            // emit ONLY the IR for the specific matching mnemonic. Avoids
            // the 14-deep select chain that FastISel may codegen incorrectly
            // (observed: BLT branch fired despite N==V==0 due to chain
            // anomaly). Per-instr mode (cond is a runtime function param)
            // falls through to the runtime select chain below.
            var condConst = LLVM.IsAConstantInt(cond);
            if (condConst != null)
            {
                var condVal = (uint)LLVM.ConstIntGetZExtValue(condConst);
                foreach (var (codeStr, mnemonic) in table)
                {
                    uint codeVal;
                    try { codeVal = Convert.ToUInt32(codeStr, 2); }
                    catch { continue; }
                    if (codeVal != condVal) continue;
                    return EmitMnemonicCheck(ctx, mnemonic);
                }
                // Cond value not in table → never execute (matches the
                // default of the runtime chain).
                return ctx.ConstBool(false);
            }

            // Runtime cond — generate the full chain (per-instr fallback).
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
            foreach (var (codeStr, mnemonic) in table)
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

        /// <summary>
        /// Phase 7 A.6.1 Golden Fix — emit ONLY the IR needed for the
        /// specific known-at-JIT-time cond mnemonic. Reads the minimum
        /// CPSR flags required (e.g. AL emits no reads at all, EQ only
        /// reads Z, LT only reads N+V). No select chain.
        /// </summary>
        private static LLVMValueRef EmitMnemonicCheck(EmitContext ctx, string mnemonic)
        {
            switch (mnemonic.ToUpperInvariant())
            {
                case "AL": return ctx.ConstBool(true);
                case "NV": return ctx.ConstBool(false);

                case "EQ": return ToI1(ctx, CpsrHelpers.ReadStatusFlag(ctx, "CPSR", "Z"), "eq_z");
                case "NE": return NotI1(ctx, ToI1(ctx, CpsrHelpers.ReadStatusFlag(ctx, "CPSR", "Z"), "ne_z"), "ne_notz");

                case "CS": case "HS":
                    return ToI1(ctx, CpsrHelpers.ReadStatusFlag(ctx, "CPSR", "C"), "cs_c");
                case "CC": case "LO":
                    return NotI1(ctx, ToI1(ctx, CpsrHelpers.ReadStatusFlag(ctx, "CPSR", "C"), "cc_c"), "cc_notc");

                case "MI": return ToI1(ctx, CpsrHelpers.ReadStatusFlag(ctx, "CPSR", "N"), "mi_n");
                case "PL": return NotI1(ctx, ToI1(ctx, CpsrHelpers.ReadStatusFlag(ctx, "CPSR", "N"), "pl_n"), "pl_notn");

                case "VS": return ToI1(ctx, CpsrHelpers.ReadStatusFlag(ctx, "CPSR", "V"), "vs_v");
                case "VC": return NotI1(ctx, ToI1(ctx, CpsrHelpers.ReadStatusFlag(ctx, "CPSR", "V"), "vc_v"), "vc_notv");

                case "HI":
                {
                    var c = ToI1(ctx, CpsrHelpers.ReadStatusFlag(ctx, "CPSR", "C"), "hi_c");
                    var nz = NotI1(ctx, ToI1(ctx, CpsrHelpers.ReadStatusFlag(ctx, "CPSR", "Z"), "hi_z"), "hi_notz");
                    return ctx.Builder.BuildAnd(c, nz, "hi");
                }
                case "LS":
                {
                    var notC = NotI1(ctx, ToI1(ctx, CpsrHelpers.ReadStatusFlag(ctx, "CPSR", "C"), "ls_c"), "ls_notc");
                    var z    = ToI1(ctx, CpsrHelpers.ReadStatusFlag(ctx, "CPSR", "Z"), "ls_z");
                    return ctx.Builder.BuildOr(notC, z, "ls");
                }

                case "GE":
                {
                    var n = CpsrHelpers.ReadStatusFlag(ctx, "CPSR", "N");
                    var v = CpsrHelpers.ReadStatusFlag(ctx, "CPSR", "V");
                    return ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, n, v, "ge_neqv");
                }
                case "LT":
                {
                    var n = CpsrHelpers.ReadStatusFlag(ctx, "CPSR", "N");
                    var v = CpsrHelpers.ReadStatusFlag(ctx, "CPSR", "V");
                    return ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, n, v, "lt_nnev");
                }
                case "GT":
                {
                    var notZ = NotI1(ctx, ToI1(ctx, CpsrHelpers.ReadStatusFlag(ctx, "CPSR", "Z"), "gt_z"), "gt_notz");
                    var n = CpsrHelpers.ReadStatusFlag(ctx, "CPSR", "N");
                    var v = CpsrHelpers.ReadStatusFlag(ctx, "CPSR", "V");
                    var neq = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, n, v, "gt_neqv");
                    return ctx.Builder.BuildAnd(notZ, neq, "gt");
                }
                case "LE":
                {
                    var z = ToI1(ctx, CpsrHelpers.ReadStatusFlag(ctx, "CPSR", "Z"), "le_z");
                    var n = CpsrHelpers.ReadStatusFlag(ctx, "CPSR", "N");
                    var v = CpsrHelpers.ReadStatusFlag(ctx, "CPSR", "V");
                    var nne = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, n, v, "le_nnev");
                    return ctx.Builder.BuildOr(z, nne, "le");
                }
            }
            return ctx.ConstBool(false);   // unknown mnemonic → never execute
        }

        private static LLVMValueRef ToI1(EmitContext ctx, LLVMValueRef i32val, string name)
            => ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, i32val, ctx.ConstU32(0), name);

        private static LLVMValueRef NotI1(EmitContext ctx, LLVMValueRef i1val, string name)
            => ctx.Builder.BuildXor(i1val, ctx.ConstBool(true), name);
    }
}
