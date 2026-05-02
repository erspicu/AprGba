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

    public LLVMValueRef Build(InstructionSetSpec set, EncodingFormat format, InstructionDef def, string? mnemonicSuffix = null)
    {
        var name = $"Execute_{set.Name}_{format.Name}_{mnemonicSuffix ?? def.Mnemonic}";
        var paramTypes = new[] { Layout.PointerType, LLVMTypeRef.Int32 };
        var fnType = LLVMTypeRef.CreateFunction(Module.Context.VoidType, paramTypes);
        var fn = Module.AddFunction(name, fnType);

        // Suppress Windows MCJIT relocation crashes by stopping LLVM from
        // emitting the COFF supplementary sections (.pdata SEH unwind +
        // .rdata jump tables) that trigger
        // "IMAGE_REL_AMD64_ADDR32NB requires an ordered section layout".
        // Per Gemini analysis 2026-05-02:
        //   - i8/i16 register pressure on x64 forces extra spills →
        //     larger frames → SEH .pdata. nounwind suppresses it.
        //   - Dense 8-bit opcode-dispatch switches become jump tables
        //     in .rdata. no-jump-tables forces branch trees.
        // The SAME pattern works for ARM today (uniform i32, no
        // dispatch switches), so adding these attributes universally
        // is safe — no behavioural change, smaller emitted code, and
        // no SEH frames to walk through (we never throw across the
        // JIT boundary anyway).
        fn.AddAttributeAtIndex(LLVMAttributeIndex.LLVMAttributeFunctionIndex,
            CreateStringAttribute(Module.Context, "no-jump-tables", "true"));
        fn.AddAttributeAtIndex(LLVMAttributeIndex.LLVMAttributeFunctionIndex,
            CreateEnumAttribute(Module.Context, "nounwind"));

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

        // Phase 7 C.b: drain shadow status regs (CPSR, F, ...) back to
        // their real state slots before returning. CpsrHelpers shadows
        // are alloca-based; H.a's mem2reg pass lifts the alloca to SSA
        // so the multiple bit-write store sequences inside the body
        // collapse into one value chain + this drain's single store.
        if (!IsCurrentBlockTerminated(ctx.Builder))
        {
            CpsrHelpers.DrainAllShadows(ctx);
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
