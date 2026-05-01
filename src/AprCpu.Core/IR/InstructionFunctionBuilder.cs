using AprCpu.Core.JsonSpec;
using LLVMSharp.Interop;

namespace AprCpu.Core.IR;

/// <summary>
/// Compiles one <see cref="InstructionDef"/> into an LLVM function.
///
/// Function signature: <c>void Execute_&lt;Format&gt;_&lt;Mnemonic&gt;(CpuState* state, i32 instruction)</c>
/// </summary>
public sealed unsafe class InstructionFunctionBuilder
{
    public LLVMModuleRef        Module    { get; }
    public CpuStateLayout            Layout            { get; }
    public EmitterRegistry           Registry          { get; }
    public OperandResolverRegistry   ResolverRegistry  { get; }

    public InstructionFunctionBuilder(
        LLVMModuleRef module,
        CpuStateLayout layout,
        EmitterRegistry registry,
        OperandResolverRegistry resolverRegistry)
    {
        Module           = module;
        Layout           = layout;
        Registry         = registry;
        ResolverRegistry = resolverRegistry;
    }

    public LLVMValueRef Build(InstructionSetSpec set, EncodingFormat format, InstructionDef def)
    {
        var name = $"Execute_{set.Name}_{format.Name}_{def.Mnemonic}";
        var paramTypes = new[] { Layout.PointerType, LLVMTypeRef.Int32 };
        var fnType = LLVMTypeRef.CreateFunction(Module.Context.VoidType, paramTypes);
        var fn = Module.AddFunction(name, fnType);

        var entry = fn.AppendBasicBlock("entry");
        var builder = Module.Context.CreateBuilder();
        builder.PositionAtEnd(entry);

        var statePtr        = fn.GetParam(0);
        var instructionWord = fn.GetParam(1);
        statePtr.Name        = "state";
        instructionWord.Name = "ins";

        var ctx = new EmitContext(Module, builder, fn, statePtr, instructionWord, Layout, set, format, def);

        // 1. Apply operand resolvers (pre-compute named outputs)
        ResolverRegistry.Apply(ctx);

        // 2. Conditional gate (if applicable)
        LLVMBasicBlockRef? endBlock = null;
        if (set.GlobalCondition is not null && !def.Unconditional)
        {
            endBlock = fn.AppendBasicBlock("ret");
            EmitConditionGate(ctx, set.GlobalCondition, endBlock.Value);
        }

        // 3. Run steps with the registry on the thread-local stack so that
        //    nested control-flow (if/then/else) can recurse.
        using (EmitterContextHolder.Push(Registry))
        {
            foreach (var step in def.Steps)
            {
                Registry.EmitStep(ctx, step);
            }
        }

        if (!IsCurrentBlockTerminated(ctx.Builder))
        {
            if (endBlock.HasValue) ctx.Builder.BuildBr(endBlock.Value);
            else                   ctx.Builder.BuildRetVoid();
        }

        if (endBlock.HasValue)
        {
            ctx.Builder.PositionAtEnd(endBlock.Value);
            ctx.Builder.BuildRetVoid();
        }

        return fn;
    }

    private static void EmitConditionGate(EmitContext ctx, GlobalCondition gc, LLVMBasicBlockRef skipBlock)
    {
        // Extract cond field
        var cond = ctx.ExtractField(gc.Field, "cond");
        // For Phase 2 first iteration, only support EQ table semantics:
        // `1110` (AL) → always; everything else evaluated against CPSR flags.
        var alConst = ctx.ConstU32(0b1110);
        var isAlways = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, cond, alConst, "is_always");

        var execBlock = ctx.Function.AppendBasicBlock("exec");
        var checkBlock = ctx.Function.AppendBasicBlock("cond_check");

        ctx.Builder.BuildCondBr(isAlways, execBlock, checkBlock);

        // The non-AL path: for now only supports EQ/NE; otherwise we conservatively
        // fall through to execute. A complete cond table lands in R4 (2.6).
        ctx.Builder.PositionAtEnd(checkBlock);
        var zBitIndex = ctx.Layout.GetStatusFlagBitIndex("CPSR", "Z");
        var cpsr = ctx.Builder.BuildLoad2(LLVMTypeRef.Int32,
            ctx.Layout.GepStatusRegister(ctx.Builder, ctx.StatePtr, "CPSR"), "cpsr");
        var zBit = ctx.Builder.BuildAnd(
            ctx.Builder.BuildLShr(cpsr, ctx.ConstU32((uint)zBitIndex), "cpsr_z_shr"),
            ctx.ConstU32(1), "cpsr_z");

        var isEq = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, cond, ctx.ConstU32(0b0000), "is_eq");
        var isNe = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, cond, ctx.ConstU32(0b0001), "is_ne");
        var zSet  = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, zBit, ctx.ConstU32(0), "z_set");
        var zClr  = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, zBit, ctx.ConstU32(0), "z_clr");

        var eqOk = ctx.Builder.BuildAnd(isEq, zSet, "eq_ok");
        var neOk = ctx.Builder.BuildAnd(isNe, zClr, "ne_ok");
        var anyOk = ctx.Builder.BuildOr(eqOk, neOk, "cond_ok_partial");

        // For unsupported codes (anything other than AL/EQ/NE), default to false.
        ctx.Builder.BuildCondBr(anyOk, execBlock, skipBlock);

        ctx.Builder.PositionAtEnd(execBlock);
    }

    private static bool IsCurrentBlockTerminated(LLVMBuilderRef builder)
        => builder.InsertBlock.Terminator.Handle != IntPtr.Zero;
}
