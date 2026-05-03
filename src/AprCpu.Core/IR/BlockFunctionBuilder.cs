using AprCpu.Core.JsonSpec;
using AprCpu.Core.Runtime;
using LLVMSharp.Interop;

namespace AprCpu.Core.IR;

/// <summary>
/// Phase 7 A.2 — compiles a <see cref="Block"/> (multiple consecutive
/// instructions) into a single LLVM function. Per-instruction cond
/// gates become internal basic blocks instead of separate functions;
/// instruction words are baked-in i32 constants instead of function
/// parameters; LLVM's optimizer (mem2reg / GVN / DSE) gets to see the
/// whole block at once and can eliminate redundant flag updates,
/// dead PC writes, etc.
///
/// Function signature: <c>void ExecuteBlock_&lt;set&gt;_pc&lt;startPc&gt;(CpuState* state)</c>
///
/// Per-instruction internal layout:
/// <code>
/// instr_K_pre:
///   ; Pre-set R15 = const(pc_K + pc_offset) so IR's "read R15" sees pipeline value
///   ; Clear PcWritten flag
///   ; Cond gate (if applicable + instr is not unconditional):
///   br cond, instr_K_exec, instr_K_post
/// instr_K_exec:
///   ; Run all spec steps for this instruction (Instruction const = pc_K's word)
///   br instr_K_post
/// instr_K_post:
///   ; Read PcWritten flag — if set, PC was written, exit block
///   br pc_written, block_exit, instr_K_advance
/// instr_K_advance:
///   ; PC := const(pc_K + instrSize) so next instr's R15 read is correct
///   br instr_K+1_pre   (or block_exit if last)
/// </code>
///
/// At <c>block_exit</c>: <see cref="CpsrHelpers.DrainAllShadows"/> + ret void.
/// </summary>
public sealed unsafe class BlockFunctionBuilder
{
    public LLVMModuleRef        Module    { get; }
    public CpuStateLayout       Layout    { get; }
    public EmitterRegistry      Registry  { get; }
    public OperandResolverRegistry ResolverRegistry { get; }

    public BlockFunctionBuilder(
        LLVMModuleRef module,
        CpuStateLayout layout,
        EmitterRegistry registry,
        OperandResolverRegistry resolverRegistry)
    {
        Module = module;
        Layout = layout;
        Registry = registry;
        ResolverRegistry = resolverRegistry;
    }

    /// <summary>
    /// Build one block-level LLVM function. Returns the function value
    /// — caller can <see cref="HostRuntime.GetFunctionPointer"/> by name
    /// after Compile().
    /// </summary>
    public LLVMValueRef Build(InstructionSetSpec set, Block block)
    {
        if (block.Instructions.Count == 0)
            throw new ArgumentException("Cannot build a block with zero instructions.", nameof(block));

        var name = BlockFunctionName(set.Name, block.StartPc);
        var paramTypes = new[] { Layout.PointerType };
        var fnType = LLVMTypeRef.CreateFunction(Module.Context.VoidType, paramTypes);
        var fn = Module.AddFunction(name, fnType);

        // Same Windows/MCJIT-friendly attributes as InstructionFunctionBuilder.
        fn.AddAttributeAtIndex(LLVMAttributeIndex.LLVMAttributeFunctionIndex,
            CreateStringAttribute(Module.Context, "no-jump-tables", "true"));
        fn.AddAttributeAtIndex(LLVMAttributeIndex.LLVMAttributeFunctionIndex,
            CreateEnumAttribute(Module.Context, "nounwind"));

        var entry = fn.AppendBasicBlock("entry");
        var builder = Module.Context.CreateBuilder();
        builder.PositionAtEnd(entry);

        var statePtr = fn.GetParam(0);
        statePtr.Name = "state";

        // Block-wide EmitContext. Format/Def/Instruction get swapped per
        // instruction via BeginInstruction; StatusShadowAllocas + Values
        // dictionary are reset per instr (Values via BeginInstruction).
        // Instruction starts as undef i32; will be overwritten before
        // any step uses it.
        var firstInsDef = block.Instructions[0].Decoded;
        var ctx = new EmitContext(
            Module, builder, fn, statePtr,
            instructionWord: ConstU32(0),  // placeholder, BeginInstruction overrides
            Layout, set,
            firstInsDef.Format, firstInsDef.Instruction);

        // The block-exit BB is the only "ret void" point. Pre-create it
        // so per-instr post-blocks can branch to it.
        var blockExit = fn.AppendBasicBlock("block_exit");

        // Walk instructions, emitting per-instruction sub-graph.
        var pcOffsetBytes = (uint)set.PcOffsetBytes;
        var pcWrittenSlotInEntry = Layout.GepPcWritten(builder, statePtr);

        // For each instruction we'll create up to 5 BBs: pre, exec,
        // post, budget_check (predictive downcount), advance.
        // Pre-create them all so we can branch forward.
        var preBBs        = new LLVMBasicBlockRef[block.Instructions.Count];
        var execBBs       = new LLVMBasicBlockRef[block.Instructions.Count];
        var postBBs       = new LLVMBasicBlockRef[block.Instructions.Count];
        var budgetCheckBBs= new LLVMBasicBlockRef[block.Instructions.Count];
        var budgetExitBBs = new LLVMBasicBlockRef[block.Instructions.Count];
        var advanceBBs    = new LLVMBasicBlockRef[block.Instructions.Count];
        for (int i = 0; i < block.Instructions.Count; i++)
        {
            preBBs[i]         = fn.AppendBasicBlock($"i{i}_pre");
            execBBs[i]        = fn.AppendBasicBlock($"i{i}_exec");
            postBBs[i]        = fn.AppendBasicBlock($"i{i}_post");
            budgetCheckBBs[i] = fn.AppendBasicBlock($"i{i}_budget");
            budgetExitBBs[i]  = fn.AppendBasicBlock($"i{i}_budget_exit");
            advanceBBs[i]     = fn.AppendBasicBlock($"i{i}_advance");
        }

        // Phase 7 GB block-JIT P0.6 — defer pre-pass: lower the spec's
        // `defer` micro-ops into phantom-injected steps in the target
        // instruction's emit list. After this pass, the per-instruction
        // step lists may have INJECTED steps prepended (deferred bodies
        // from earlier instructions whose delay just expired) and any
        // own-defer wrappers stripped. The original instruction metadata
        // (Format, Cycles, Pc, etc.) is preserved.
        // See MD/design/13-defer-microop.md.
        var loweredInstrs = DeferLowering.Lower(block.Instructions, out var crossBlockPending);
        // crossBlockPending list: any defers whose delay extends past
        // block end. V1 logs to console as a known-unsupported edge
        // case (LR35902 EI is the only consumer for now and the
        // detector keeps blocks short enough to avoid this path in
        // practice). V2 will serialize to pending_deferred_flags slot
        // (P0.6 step 3 followup).
        if (crossBlockPending.Count > 0)
        {
            // Silently drop for V1 — same effect as legacy behaviour
            // pre-defer. Tracked as P0.6-step-3 followup. This path is
            // only reachable when EI / similar is in the LAST instruction
            // of a block, which BlockDetector currently caps at 64 instr
            // (so the action just gets lost; per-instr backend's own
            // _eiDelay extern still works as the canonical path).
        }

        // Branch from entry to first instruction's pre block.
        builder.PositionAtEnd(entry);
        builder.BuildBr(preBBs[0]);

        for (int i = 0; i < block.Instructions.Count; i++)
        {
            var bi = block.Instructions[i];
            var emittedSteps = loweredInstrs[i].EmittedSteps;
            // Phase 7 A.6.1 Strategy 2 — pass bi.Pc so emitters can
            // statically resolve "read R15" to a pipeline-PC constant
            // instead of loading from GPR[15] (which is no longer
            // pre-set per instruction).
            // Pass per-instr length ONLY for variable-width sets (LR35902).
            // For these, PipelinePcConstant becomes bi.Pc + bi.LengthBytes
            // — what per-instr-mode read_pc would see after read_imm8/16
            // bumped PC. For fixed-width sets (ARM/Thumb), pass null so
            // PipelinePcConstant uses spec.PcOffsetBytes (ARM's pc+8
            // pipeline quirk, which is NOT the same as instruction length).
            byte? len = (block.InstrSizeBytes == 0u && bi.LengthBytes > 0)
                ? bi.LengthBytes
                : (byte?)null;
            ctx.BeginInstruction(bi.Decoded.Format, bi.Decoded.Instruction, ConstU32(bi.InstructionWord), bi.Pc, len);

            // 1. Pre block: clear PcWritten, then cond gate.
            //
            // Phase 7 A.6.1 Strategy 2 — DROPPED the per-instruction PC
            // pre-write. Previously this wrote PC = bi.Pc + pcOffsetBytes
            // so emitter "read R15" sees the pipeline-offset value, but
            // it also meant GPR[15] was constantly being overwritten,
            // making it impossible to use "GPR[15] changed" as a branch
            // signal. Emitters now read pipeline-PC as a constant via
            // ctx.PipelinePcConstant, so GPR[15] in memory is ONLY
            // touched by real branches. The advance block likewise no
            // longer writes PC (the executor advances PC after block
            // exit when PcWritten=0).
            builder.PositionAtEnd(preBBs[i]);
            builder.BuildStore(
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, 0, false),
                Layout.GepPcWritten(builder, statePtr));

            // Cond gate: only if instr-set has global cond AND instr is
            // not marked unconditional. If no gate, fall straight to exec.
            if (set.GlobalCondition is not null && !bi.Decoded.Instruction.Unconditional)
            {
                var shouldExecute = ConditionEvaluator.EmitCheck(ctx, set.GlobalCondition);
                builder.BuildCondBr(shouldExecute, execBBs[i], postBBs[i]);
            }
            else
            {
                builder.BuildBr(execBBs[i]);
            }

            // 2. Exec block: run spec steps (post defer-lowering — see
            //    pre-pass above; emittedSteps may include injected
            //    bodies from earlier instructions' expired defers, and
            //    excludes any own defer wrappers which became part of
            //    later instructions' steps).
            builder.PositionAtEnd(execBBs[i]);
            ResolverRegistry.Apply(ctx);
            using (EmitterContextHolder.Push(Registry))
            {
                foreach (var step in emittedSteps)
                    Registry.EmitStep(ctx, step);
            }
            if (!IsTerminated(builder)) builder.BuildBr(postBBs[i]);

            // 3. Post block: did this instruction write PC? If so, exit
            //    block (control transferred). Otherwise go to budget check.
            builder.PositionAtEnd(postBBs[i]);
            var pcWrittenSlot = Layout.GepPcWritten(builder, statePtr);
            var pcWritten = builder.BuildLoad2(LLVMTypeRef.Int8, pcWrittenSlot, $"i{i}_pcw");
            var pcNotWritten = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ,
                pcWritten, LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, 0, false), $"i{i}_pcw_eq0");
            builder.BuildCondBr(pcNotWritten, budgetCheckBBs[i], blockExit);

            // 4. Budget check: deduct this instruction's cycle cost from
            //    cycles_left. If exhausted, write next-PC + mark PcWritten
            //    and exit; otherwise continue to advance. For the last
            //    instruction in the block, budget check is moot (we'd exit
            //    anyway), so just skip straight to advance.
            //
            // Phase 7 A.6.1 — predictive downcounting (Dolphin/mGBA pattern,
            // recommended by Gemini). cycles_left is loaded by the host
            // before each block call; the IR decrements per instruction.
            // Sub-block IRQ delivery + MMIO catch-up granularity without
            // losing the JIT throughput win.
            //
            // Phase 7 GB block-JIT P0.4 — per-instruction cycle cost is now
            // spec-driven (parsed from cycles.form: "Nm" → N×4 t-cycles).
            // For LR35902 this matters a lot because instructions vary
            // 4-24 t-cycles; the previous fixed 4 caused under-counting →
            // outer scheduler ticked too few cycles → IRQ delivery delayed.
            // For ARM most instructions are ~1 S-cycle = 4 t-cycles so the
            // old constant was already accurate; spec-driven keeps it so.
            int instrCycleCost = ParseCyclesForm(bi.Decoded.Instruction.Cycles?.Form);
            builder.PositionAtEnd(budgetCheckBBs[i]);
            if (i + 1 < block.Instructions.Count)
            {
                var cyclesPtr = Layout.GepCyclesLeft(builder, statePtr);
                var cyclesOld = builder.BuildLoad2(LLVMTypeRef.Int32, cyclesPtr, $"i{i}_cycles_old");
                var cyclesNew = builder.BuildSub(cyclesOld,
                    LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (ulong)instrCycleCost, false), $"i{i}_cycles_new");
                builder.BuildStore(cyclesNew, cyclesPtr);
                var exhausted = builder.BuildICmp(LLVMIntPredicate.LLVMIntSLE,
                    cyclesNew, LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0, false), $"i{i}_budget_done");
                builder.BuildCondBr(exhausted, budgetExitBBs[i], advanceBBs[i]);
            }
            else
            {
                // Last instruction: still deduct cycles but no need to check
                // (block ends regardless).
                var cyclesPtr = Layout.GepCyclesLeft(builder, statePtr);
                var cyclesOld = builder.BuildLoad2(LLVMTypeRef.Int32, cyclesPtr, $"i{i}_cycles_old");
                var cyclesNew = builder.BuildSub(cyclesOld,
                    LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (ulong)instrCycleCost, false), $"i{i}_cycles_new");
                builder.BuildStore(cyclesNew, cyclesPtr);
                builder.BuildBr(advanceBBs[i]);
            }

            // 4b. Budget exit: write the NEXT instruction's PC into the PC
            //     slot (so the host dispatches there next), mark PcWritten=1
            //     (so the host's "no branch → advance PC" path doesn't
            //     overwrite our exit PC), and jump to block_exit. Only the
            //     non-last instructions have a meaningful budget exit; for
            //     the last we still create the BB but make it an unreachable
            //     stub so LLVM verifier accepts the function.
            builder.PositionAtEnd(budgetExitBBs[i]);
            if (i + 1 < block.Instructions.Count)
            {
                uint nextPc = block.Instructions[i + 1].Pc;
                WritePcConst(builder, statePtr, nextPc);
                builder.BuildStore(
                    LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, 1, false),
                    Layout.GepPcWritten(builder, statePtr));
                builder.BuildBr(blockExit);
            }
            else
            {
                builder.BuildUnreachable();
            }

            // 5. Advance block: just branch to next instr's pre (or
            //    block_exit if last). NO PC write — executor advances
            //    PC by total block size when PcWritten=0 at exit.
            builder.PositionAtEnd(advanceBBs[i]);
            var next = (i + 1 < block.Instructions.Count) ? preBBs[i + 1] : blockExit;
            builder.BuildBr(next);
        }

        // 5. Block exit: drain shadow status registers, ret void.
        builder.PositionAtEnd(blockExit);
        CpsrHelpers.DrainAllShadows(ctx);
        builder.BuildRetVoid();

        return fn;
    }

    /// <summary>
    /// Canonical name for a block function, used for both
    /// <c>AddFunction</c> and post-Compile <c>GetFunctionPointer</c>.
    /// </summary>
    public static string BlockFunctionName(string setName, uint startPc)
        => $"ExecuteBlock_{setName}_pc{startPc:X8}";

    private LLVMValueRef ConstU32(uint v) =>
        LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, v, SignExtend: false);

    private void WritePcConst(LLVMBuilderRef builder, LLVMValueRef statePtr, uint pcValue)
    {
        // PC location is spec-driven (see StackOps.LocateProgramCounter).
        // For ARM PC is GPR[15] (i32); for LR35902 it's status reg "PC" (i16).
        var pcIdx = Layout.RegisterFile.GeneralPurpose.PcIndex;
        if (pcIdx is int idx)
        {
            // GPR-resident PC (ARM)
            var ptr = Layout.GepGpr(builder, statePtr, idx);
            builder.BuildStore(LLVMValueRef.CreateConstInt(Layout.GprType, pcValue, false), ptr);
        }
        else
        {
            // Status-reg PC (LR35902)
            var ptr = Layout.GepStatusRegister(builder, statePtr, "PC");
            var def = Layout.GetStatusRegisterDef("PC");
            var t = def.WidthBits switch
            {
                16 => LLVMTypeRef.Int16,
                32 => LLVMTypeRef.Int32,
                _ => throw new NotSupportedException($"PC width {def.WidthBits} unsupported")
            };
            builder.BuildStore(LLVMValueRef.CreateConstInt(t, pcValue, false), ptr);
        }
    }

    private static bool IsTerminated(LLVMBuilderRef builder)
        => builder.InsertBlock.Terminator.Handle != IntPtr.Zero;

    /// <summary>
    /// Phase 7 GB block-JIT P0.4 — parse spec's <c>cycles.form</c> string
    /// into integer t-cycle count for per-instruction budget decrement.
    /// Mirrors AprGb.Cli.JsonCpu.CyclesFor: extract first integer N, return
    /// N × 4 (m-cycle = 4 t-cycles). Default to 4 (= 1 m-cycle) if no form.
    /// </summary>
    private static int ParseCyclesForm(string? form)
    {
        if (string.IsNullOrEmpty(form)) return 4;
        int n = 0;
        foreach (var ch in form)
        {
            if (ch >= '0' && ch <= '9') { n = n * 10 + (ch - '0'); continue; }
            if (n > 0) break;
        }
        if (n == 0) n = 1;
        return n * 4;
    }

    private static unsafe LLVMAttributeRef CreateEnumAttribute(LLVMContextRef ctx, string name)
    {
        var kind = LLVM.GetEnumAttributeKindForName(
            (sbyte*)System.Runtime.InteropServices.Marshal.StringToHGlobalAnsi(name),
            (UIntPtr)name.Length);
        return ctx.CreateEnumAttribute(kind, 0);
    }

    private static unsafe LLVMAttributeRef CreateStringAttribute(LLVMContextRef ctx, string key, string value)
    {
        var keyBytes   = System.Text.Encoding.ASCII.GetBytes(key);
        var valueBytes = System.Text.Encoding.ASCII.GetBytes(value);
        fixed (byte* kp = keyBytes)
        fixed (byte* vp = valueBytes)
        {
            return LLVM.CreateStringAttribute(ctx,
                (sbyte*)kp, (uint)keyBytes.Length,
                (sbyte*)vp, (uint)valueBytes.Length);
        }
    }
}
