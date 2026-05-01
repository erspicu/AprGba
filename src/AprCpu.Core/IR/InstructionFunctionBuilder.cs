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
        // R4: cond evaluation is fully data-driven by the spec's
        // global_condition.table; ConditionEvaluator covers the 14 standard
        // ARM cond codes plus AL / NV. Mnemonics not in the recognised set
        // default to "false" (never execute), matching ARM's "NV reserved"
        // semantics.
        var shouldExecute = ConditionEvaluator.EmitCheck(ctx, gc);
        var execBlock = ctx.Function.AppendBasicBlock("exec");
        ctx.Builder.BuildCondBr(shouldExecute, execBlock, skipBlock);
        ctx.Builder.PositionAtEnd(execBlock);
    }

    private static bool IsCurrentBlockTerminated(LLVMBuilderRef builder)
        => builder.InsertBlock.Terminator.Handle != IntPtr.Zero;
}
