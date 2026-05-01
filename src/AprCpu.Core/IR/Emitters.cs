using System.Text.Json;
using AprCpu.Core.JsonSpec;
using LLVMSharp.Interop;

namespace AprCpu.Core.IR;

/// <summary>
/// Built-in CPU-agnostic micro-op emitters: register I/O, arithmetic /
/// logical / shift, simple bitwise extras (mvn, bic), control flow
/// (if, branch, branch_link). ARM-specific emitters (carry-aware ALU,
/// CPSR flag updates, PSR access, BX-style indirect branch with T-bit)
/// live in <see cref="ArmEmitters"/> and are registered separately
/// based on the spec's <c>architecture.family</c>.
/// </summary>
public static class StandardEmitters
{
    /// <summary>Register every generic emitter into <paramref name="reg"/>.</summary>
    public static void RegisterAll(EmitterRegistry reg)
    {
        // Register access
        reg.Register(new ReadReg());
        reg.Register(new WriteReg());

        // Arithmetic / logical
        reg.Register(new Binary("add", (b, l, r, n) => b.BuildAdd(l, r, n)));
        reg.Register(new Binary("sub", (b, l, r, n) => b.BuildSub(l, r, n)));
        reg.Register(new Binary("and", (b, l, r, n) => b.BuildAnd(l, r, n)));
        reg.Register(new Binary("or",  (b, l, r, n) => b.BuildOr (l, r, n)));
        reg.Register(new Binary("xor", (b, l, r, n) => b.BuildXor(l, r, n)));
        reg.Register(new Binary("shl", (b, l, r, n) => b.BuildShl (l, r, n)));
        reg.Register(new Binary("lsr", (b, l, r, n) => b.BuildLShr(l, r, n)));
        reg.Register(new Binary("asr", (b, l, r, n) => b.BuildAShr(l, r, n)));
        reg.Register(new Binary("rsb", (b, l, r, n) => b.BuildSub(r, l, n)));
        reg.Register(new BicEmitter());   // a AND NOT b
        reg.Register(new MvnEmitter());   // NOT a

        // Control flow (generic — branch_indirect is in ArmEmitters)
        reg.Register(new IfStep());
        reg.Register(new Branch("branch",      BranchKind.Plain));
        reg.Register(new Branch("branch_link", BranchKind.Link));
    }

    // ---------------- helpers ----------------

    /// <summary>Resolve "in"[i] entry — string name → value.</summary>
    internal static LLVMValueRef ResolveInput(EmitContext ctx, JsonElement step, int index, string defaultName = "in")
    {
        var arr = step.GetProperty(defaultName);
        var name = arr[index].GetString()!;
        return ctx.Resolve(name);
    }

    internal static string GetOut(JsonElement step) =>
        step.GetProperty("out").GetString()!;
}

// ---------------- register access ----------------

internal sealed class ReadReg : IMicroOpEmitter
{
    public string OpName => "read_reg";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var indexExpr = step.Raw.GetProperty("index");
        var outName   = StandardEmitters.GetOut(step.Raw);

        var ptr = ResolveRegPtr(ctx, indexExpr);
        var v   = ctx.Builder.BuildLoad2(LLVMTypeRef.Int32, ptr, outName);
        ctx.Values[outName] = v;
    }

    internal static LLVMValueRef ResolveRegPtr(EmitContext ctx, JsonElement indexExpr)
    {
        // index can be a string (field name) or integer (literal 0..15).
        if (indexExpr.ValueKind == JsonValueKind.Number)
        {
            var lit = indexExpr.GetInt32();
            return ctx.Layout.GepGpr(ctx.Builder, ctx.StatePtr, lit);
        }
        if (indexExpr.ValueKind == JsonValueKind.String)
        {
            var name = indexExpr.GetString()!;
            // Field-name → runtime extraction → dynamic GEP
            var idx = ctx.Resolve(name);
            var masked = ctx.Builder.BuildAnd(idx, ctx.ConstU32(0xF), $"{name}_idx");
            return ctx.Layout.GepGprDynamic(ctx.Builder, ctx.StatePtr, masked);
        }
        throw new InvalidOperationException("read_reg/write_reg 'index' must be string or number.");
    }
}

internal sealed class WriteReg : IMicroOpEmitter
{
    public string OpName => "write_reg";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var indexExpr = step.Raw.GetProperty("index");
        var valueName = step.Raw.GetProperty("value").GetString()!;
        var value     = ctx.Resolve(valueName);

        var ptr = ReadReg.ResolveRegPtr(ctx, indexExpr);
        ctx.Builder.BuildStore(value, ptr);
    }
}

// ---------------- generic binary op ----------------

internal sealed class Binary : IMicroOpEmitter
{
    public string OpName { get; }
    private readonly Func<LLVMBuilderRef, LLVMValueRef, LLVMValueRef, string, LLVMValueRef> _build;

    public Binary(string opName,
                  Func<LLVMBuilderRef, LLVMValueRef, LLVMValueRef, string, LLVMValueRef> build)
    {
        OpName = opName;
        _build = build;
    }

    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var lhs = StandardEmitters.ResolveInput(ctx, step.Raw, 0);
        var rhs = StandardEmitters.ResolveInput(ctx, step.Raw, 1);
        var outName = StandardEmitters.GetOut(step.Raw);
        var result  = _build(ctx.Builder, lhs, rhs, outName);
        ctx.Values[outName] = result;
    }
}

// ---------------- flag updates ----------------

/// <summary>Common helpers for writing CPSR bits.</summary>
internal static class CpsrHelpers
{
    /// <summary>
    /// Set a single bit by spec-defined flag name (e.g. ("CPSR","N")).
    /// Looks up the bit position via <see cref="CpuStateLayout.GetStatusFlagBitIndex"/>
    /// so no ARM-specific constants are baked in.
    /// </summary>
    public static void SetStatusFlag(EmitContext ctx, string register, string flag, LLVMValueRef boolValue)
        => SetStatusFlagAt(ctx, register, ctx.Layout.GetStatusFlagBitIndex(register, flag), boolValue, flag.ToLowerInvariant());

    public static void SetStatusFlagFromI32Lsb(EmitContext ctx, string register, string flag, LLVMValueRef i32Value)
        => SetStatusFlagAtFromI32Lsb(ctx, register, ctx.Layout.GetStatusFlagBitIndex(register, flag), i32Value, flag.ToLowerInvariant());

    /// <summary>Convenience: read CPSR (or any status reg) bit as i32 0/1.</summary>
    public static LLVMValueRef ReadStatusFlag(EmitContext ctx, string register, string flag)
    {
        var bitIndex = ctx.Layout.GetStatusFlagBitIndex(register, flag);
        var ptr = ctx.Layout.GepStatusRegister(ctx.Builder, ctx.StatePtr, register);
        var v   = ctx.Builder.BuildLoad2(LLVMTypeRef.Int32, ptr, $"{register.ToLowerInvariant()}_for_{flag.ToLowerInvariant()}");
        var sh  = ctx.Builder.BuildLShr(v, ctx.ConstU32((uint)bitIndex), $"{flag.ToLowerInvariant()}_shr");
        return ctx.Builder.BuildAnd(sh, ctx.ConstU32(1), $"{flag.ToLowerInvariant()}_bit");
    }

    private static void SetStatusFlagAt(EmitContext ctx, string register, int bitIndex, LLVMValueRef boolValue, string label)
    {
        var ptr  = ctx.Layout.GepStatusRegister(ctx.Builder, ctx.StatePtr, register);
        var prev = ctx.Builder.BuildLoad2(LLVMTypeRef.Int32, ptr, $"{register.ToLowerInvariant()}_old");

        var asI32   = ctx.Builder.BuildZExt(boolValue, LLVMTypeRef.Int32, $"{label}_asi32");
        var shifted = ctx.Builder.BuildShl (asI32, ctx.ConstU32((uint)bitIndex), $"{label}_shifted");

        var clearMask = ctx.ConstU32(~(1u << bitIndex));
        var cleared   = ctx.Builder.BuildAnd(prev, clearMask, $"{register.ToLowerInvariant()}_clear_{label}");
        var newV      = ctx.Builder.BuildOr (cleared, shifted, $"{register.ToLowerInvariant()}_set_{label}");
        ctx.Builder.BuildStore(newV, ptr);
    }

    private static void SetStatusFlagAtFromI32Lsb(EmitContext ctx, string register, int bitIndex, LLVMValueRef i32Value, string label)
    {
        var lowBit = ctx.Builder.BuildAnd(i32Value, ctx.ConstU32(1), $"{label}_lsb");
        var asBool = ctx.Builder.BuildTrunc(lowBit, LLVMTypeRef.Int1, $"{label}_b");
        SetStatusFlagAt(ctx, register, bitIndex, asBool, label);
    }


}

// (UpdateNz / UpdateC*/UpdateV*/Update*Carry / Adc/Sbc/Rsc / ReadPsr /
// WritePsr / RestoreCpsrFromSpsr / CarryReader were moved to ArmEmitters.cs
// in R5.)



internal sealed class MvnEmitter : IMicroOpEmitter
{
    public string OpName => "mvn";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var v = StandardEmitters.ResolveInput(ctx, step.Raw, 0);
        var res = ctx.Builder.BuildXor(v, ctx.ConstU32(0xFFFFFFFFu), StandardEmitters.GetOut(step.Raw));
        ctx.Values[StandardEmitters.GetOut(step.Raw)] = res;
    }
}

internal sealed class BicEmitter : IMicroOpEmitter
{
    public string OpName => "bic";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var a = StandardEmitters.ResolveInput(ctx, step.Raw, 0);
        var b = StandardEmitters.ResolveInput(ctx, step.Raw, 1);
        var notB = ctx.Builder.BuildXor(b, ctx.ConstU32(0xFFFFFFFFu), "bic_notb");
        var res = ctx.Builder.BuildAnd(a, notB, StandardEmitters.GetOut(step.Raw));
        ctx.Values[StandardEmitters.GetOut(step.Raw)] = res;
    }
}

// ---------------- control flow ----------------

internal sealed class IfStep : IMicroOpEmitter
{
    public string OpName => "if";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var cond = CondExpr.Parse(step.Raw.GetProperty("cond"));
        var condVal = cond.Lower(ctx);

        var thenBlock = ctx.AppendBlock("if_then");
        var elseBlock = step.Raw.TryGetProperty("else", out _)
            ? ctx.AppendBlock("if_else") : (LLVMBasicBlockRef?)null;
        var endBlock  = ctx.AppendBlock("if_end");

        ctx.Builder.BuildCondBr(condVal, thenBlock, elseBlock ?? endBlock);

        // then arm
        ctx.Builder.PositionAtEnd(thenBlock);
        EmitNestedSteps(ctx, step.Raw.GetProperty("then"));
        if (!IsBlockTerminated(ctx)) ctx.Builder.BuildBr(endBlock);

        // else arm (if any)
        if (elseBlock.HasValue)
        {
            ctx.Builder.PositionAtEnd(elseBlock.Value);
            EmitNestedSteps(ctx, step.Raw.GetProperty("else"));
            if (!IsBlockTerminated(ctx)) ctx.Builder.BuildBr(endBlock);
        }

        // continuation
        ctx.Builder.PositionAtEnd(endBlock);
    }

    private static void EmitNestedSteps(EmitContext ctx, JsonElement steps)
    {
        // We need the registry. Stash a reference on EmitContext via a module-static.
        // Since we don't currently route registry through EmitContext, the
        // InstructionFunctionBuilder sets EmitContext.Values["__registry__"] is overkill.
        // Instead we keep registry on a thread-local set up by InstructionFunctionBuilder.
        var registry = EmitterContextHolder.CurrentRegistry
            ?? throw new InvalidOperationException("No active EmitterRegistry on this thread.");
        foreach (var stepEl in steps.EnumerateArray())
        {
            var op = stepEl.GetProperty("op").GetString()!;
            registry.EmitStep(ctx, new MicroOpStep(op, stepEl));
        }
    }

    private static bool IsBlockTerminated(EmitContext ctx)
    {
        var bb = ctx.Builder.InsertBlock;
        return bb.Terminator.Handle != IntPtr.Zero;
    }
}

/// <summary>
/// Generic branch kinds. ARM-specific BX (T-bit + alignment) lives as
/// <c>BranchIndirectArm</c> in <see cref="ArmEmitters"/>.
/// </summary>
internal enum BranchKind { Plain, Link }

internal sealed class Branch : IMicroOpEmitter
{
    public string OpName { get; }
    private readonly BranchKind _kind;

    public Branch(string opName, BranchKind kind)
    {
        OpName = opName;
        _kind  = kind;
    }

    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var targetName = step.Raw.GetProperty("target").GetString()!;
        var target = ctx.Resolve(targetName);

        if (_kind == BranchKind.Link)
        {
            // LR <- PC (next instruction); PC <- target. Uses GPR-15 as PC
            // and GPR-14 as LR by ARM convention; non-ARM specs that want
            // a different "link register" should provide their own emitter.
            var r15Ptr = ctx.Layout.GepGpr(ctx.Builder, ctx.StatePtr, 15);
            var r15    = ctx.Builder.BuildLoad2(LLVMTypeRef.Int32, r15Ptr, "r15");
            var width  = ctx.InstructionSet.WidthBits.Fixed!.Value / 8;
            var nextPc = ctx.Builder.BuildAdd(
                r15, ctx.ConstU32((uint)(width - ctx.InstructionSet.PcOffsetBytes)), "next_pc");
            var lrPtr  = ctx.Layout.GepGpr(ctx.Builder, ctx.StatePtr, 14);
            ctx.Builder.BuildStore(nextPc, lrPtr);
        }

        // Store target as new PC (R15)
        var pcSlot = ctx.Layout.GepGpr(ctx.Builder, ctx.StatePtr, 15);
        ctx.Builder.BuildStore(target, pcSlot);
    }
}

/// <summary>
/// Thread-local holder that lets nested emitters (like <c>if</c>'s `then`/`else`
/// arms) recursively dispatch through the same registry without threading it
/// through every signature.
/// </summary>
internal static class EmitterContextHolder
{
    [ThreadStatic] private static EmitterRegistry? _current;
    public static EmitterRegistry? CurrentRegistry => _current;
    public static IDisposable Push(EmitterRegistry reg)
    {
        var prev = _current;
        _current = reg;
        return new Pop(prev);
    }
    private sealed class Pop : IDisposable
    {
        private readonly EmitterRegistry? _prev;
        public Pop(EmitterRegistry? prev) { _prev = prev; }
        public void Dispose() { _current = _prev; }
    }
}
