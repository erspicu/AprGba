using AprCpu.Core.JsonSpec;
using LLVMSharp.Interop;

namespace AprCpu.Core.IR;

/// <summary>
/// Generic stack-related micro-ops driven by the CPU spec's
/// <see cref="RegisterFile.StackPointer"/> declaration. Replaces
/// per-CPU push/pop/call/ret implementations that all do the same
/// "decrement SP, write value at [SP]" / "read at [SP], increment SP"
/// pattern.
///
/// SP is located via spec metadata:
/// <list type="bullet">
///   <item><c>stack_pointer: "SP"</c> — separate status register
///         (LR35902, 6502, M68k USP)</item>
///   <item><c>stack_pointer: { "gpr_index": 13 }</c> — one of the
///         GPRs (ARM R13, MIPS $sp)</item>
/// </list>
///
/// Registered ops:
/// <list type="bullet">
///   <item><c>push_pair { name }</c> — read 16-bit register pair, push to stack</item>
///   <item><c>pop_pair  { name, low_clear_mask?: int }</c> — pop, write to pair;
///         optional <c>low_clear_mask</c> AND-clears low N bits before write
///         (LR35902 POP AF zeros F's low nibble — pass 0xFFF0)</item>
///   <item><c>call { target }</c> — push current PC, branch to target</item>
///   <item><c>ret  </c> — pop into PC</item>
/// </list>
/// Word size is fixed per spec: 16-bit for LR35902 (SP is 16-bit
/// status register), 32-bit for ARM (SP is 32-bit GPR).
/// </summary>
internal static class StackOps
{
    public static void RegisterAll(EmitterRegistry reg)
    {
        reg.Register(new PushPair());
        reg.Register(new PopPair());
        reg.Register(new Call());
        reg.Register(new Ret());
        reg.Register(new CallCc());
        reg.Register(new RetCc());
    }

    // ---------------- helpers (shared across the four ops) ----------------

    /// <summary>Returns (sp_pointer, sp_type, word_bytes) — sp_type is i16 / i32.</summary>
    internal static (LLVMValueRef Ptr, LLVMTypeRef Type, int WordBytes) LocateStackPointer(EmitContext ctx)
    {
        var spRef = ctx.Layout.RegisterFile.StackPointer
            ?? throw new InvalidOperationException(
                "StackOps requires register_file.stack_pointer in the CPU spec.");

        if (spRef.GprIndex is int gprIdx)
        {
            var ptr = ctx.Layout.GepGpr(ctx.Builder, ctx.StatePtr, gprIdx);
            var bits = ctx.Layout.GprWidthBits;
            return (ptr, ctx.Layout.GprType, bits / 8);
        }

        if (spRef.StatusName is string statusName)
        {
            var ptr  = ctx.Layout.GepStatusRegister(ctx.Builder, ctx.StatePtr, statusName);
            var def  = ctx.Layout.GetStatusRegisterDef(statusName);
            var type = def.WidthBits switch
            {
                16 => LLVMTypeRef.Int16,
                32 => LLVMTypeRef.Int32,
                _  => throw new NotSupportedException($"stack pointer width {def.WidthBits}-bit unsupported.")
            };
            return (ptr, type, def.WidthBits / 8);
        }

        throw new InvalidOperationException("StackPointerRef has neither GprIndex nor StatusName set.");
    }

    /// <summary>Returns (pc_pointer, pc_type) — falls back to status reg "PC" when no pc_index in GPR file.</summary>
    internal static (LLVMValueRef Ptr, LLVMTypeRef Type) LocateProgramCounter(EmitContext ctx)
    {
        if (ctx.Layout.RegisterFile.GeneralPurpose.PcIndex is int pcIdx)
            return (ctx.Layout.GepGpr(ctx.Builder, ctx.StatePtr, pcIdx), ctx.Layout.GprType);

        var ptr = ctx.Layout.GepStatusRegister(ctx.Builder, ctx.StatePtr, "PC");
        var def = ctx.Layout.GetStatusRegisterDef("PC");
        var type = def.WidthBits switch
        {
            16 => LLVMTypeRef.Int16,
            32 => LLVMTypeRef.Int32,
            _  => throw new NotSupportedException($"PC width {def.WidthBits}-bit unsupported.")
        };
        return (ptr, type);
    }

    /// <summary>
    /// Decrement SP by N bytes and write a word value to [SP]. Word
    /// is little-endian (matches all our current target CPUs).
    /// </summary>
    internal static void PushWord(EmitContext ctx, LLVMValueRef value, int wordBytes, string label)
    {
        var (spPtr, spType, _) = LocateStackPointer(ctx);
        var sp = ctx.Builder.BuildLoad2(spType, spPtr, $"{label}_sp_pre");
        var newSp = ctx.Builder.BuildSub(sp,
            LLVMValueRef.CreateConstInt(spType, (uint)wordBytes, false), $"{label}_sp_dec");
        ctx.Builder.BuildStore(newSp, spPtr);

        // Write `wordBytes` bytes little-endian at [newSp].
        var i32 = LLVMTypeRef.Int32;
        for (int b = 0; b < wordBytes; b++)
        {
            var addr16 = ctx.Builder.BuildAdd(newSp,
                LLVMValueRef.CreateConstInt(spType, (uint)b, false), $"{label}_a{b}");
            var addr32 = spType == i32 ? addr16 : ctx.Builder.BuildZExt(addr16, i32, $"{label}_a{b}_z");
            var byteVal = ctx.Builder.BuildTrunc(
                ctx.Builder.BuildLShr(value,
                    LLVMValueRef.CreateConstInt(value.TypeOf, (uint)(b * 8), false),
                    $"{label}_v{b}_shr"),
                LLVMTypeRef.Int8, $"{label}_v{b}");
            MemoryEmitters.CallWrite8(ctx, addr32, byteVal);
        }
    }

    /// <summary>
    /// Read a word from [SP] little-endian, then increment SP by N.
    /// Returns the popped value typed to <paramref name="resultType"/>
    /// (defaults to SP's own type when null).
    /// </summary>
    internal static LLVMValueRef PopWord(EmitContext ctx, int wordBytes, string label, LLVMTypeRef? resultType = null)
    {
        var (spPtr, spType, _) = LocateStackPointer(ctx);
        var sp = ctx.Builder.BuildLoad2(spType, spPtr, $"{label}_sp_pre");

        // Read `wordBytes` bytes little-endian, compose into wordType.
        var rt = resultType ?? spType;
        var i32 = LLVMTypeRef.Int32;
        LLVMValueRef accum = LLVMValueRef.CreateConstInt(rt, 0, false);
        for (int b = 0; b < wordBytes; b++)
        {
            var addr16 = ctx.Builder.BuildAdd(sp,
                LLVMValueRef.CreateConstInt(spType, (uint)b, false), $"{label}_a{b}");
            var addr32 = spType == i32 ? addr16 : ctx.Builder.BuildZExt(addr16, i32, $"{label}_a{b}_z");
            var byteVal = MemoryEmitters.CallRead8(ctx, addr32, $"{label}_b{b}");
            var byteExt = ctx.Builder.BuildZExt(byteVal, rt, $"{label}_b{b}_z");
            var byteShl = ctx.Builder.BuildShl(byteExt,
                LLVMValueRef.CreateConstInt(rt, (uint)(b * 8), false), $"{label}_b{b}_shl");
            accum = ctx.Builder.BuildOr(accum, byteShl, $"{label}_acc{b}");
        }

        var newSp = ctx.Builder.BuildAdd(sp,
            LLVMValueRef.CreateConstInt(spType, (uint)wordBytes, false), $"{label}_sp_inc");
        ctx.Builder.BuildStore(newSp, spPtr);

        return accum;
    }

    /// <summary>
    /// Look up a register pair definition by name. Throws if the spec
    /// doesn't declare this pair in <c>register_file.register_pairs</c>.
    /// </summary>
    internal static RegisterPair LookupPair(EmitContext ctx, string name)
    {
        foreach (var p in ctx.Layout.RegisterFile.RegisterPairs)
            if (string.Equals(p.Name, name, StringComparison.Ordinal)) return p;
        throw new InvalidOperationException(
            $"register pair '{name}' not declared in register_file.register_pairs. " +
            $"Available: {string.Join(", ", ctx.Layout.RegisterFile.RegisterPairs.Select(p => p.Name))}");
    }

    /// <summary>
    /// Locate a named register byte by its symbolic name. Looks first
    /// in <c>general_purpose.names</c> (e.g. ARM "R13", LR35902 "B"),
    /// then in <c>status</c> (LR35902 "F" — paired register pieces can
    /// span the GPR / status divide). Returns the underlying byte pointer.
    /// </summary>
    internal static LLVMValueRef GepNamedRegister(EmitContext ctx, string name)
    {
        var names = ctx.Layout.RegisterFile.GeneralPurpose.Names;
        for (int i = 0; i < names.Count; i++)
            if (string.Equals(names[i], name, StringComparison.Ordinal))
                return ctx.Layout.GepGpr(ctx.Builder, ctx.StatePtr, i);

        // Fall back to status register lookup (e.g. LR35902 F is a status reg).
        try
        {
            return ctx.Layout.GepStatusRegister(ctx.Builder, ctx.StatePtr, name);
        }
        catch (InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"register '{name}' not found in either GPR names or status registers.");
        }
    }

    /// <summary>Read a 16-bit register pair into i16 (little-endian: hi << 8 | lo).</summary>
    internal static LLVMValueRef ReadPair16(EmitContext ctx, RegisterPair pair, string label)
    {
        var loPtr = GepNamedRegister(ctx, pair.Low);
        var hiPtr = GepNamedRegister(ctx, pair.High);
        var i16 = LLVMTypeRef.Int16;
        var lo = ctx.Builder.BuildLoad2(LLVMTypeRef.Int8, loPtr, $"{label}_lo");
        var hi = ctx.Builder.BuildLoad2(LLVMTypeRef.Int8, hiPtr, $"{label}_hi");
        var loZ = ctx.Builder.BuildZExt(lo, i16, $"{label}_loz");
        var hiZ = ctx.Builder.BuildZExt(hi, i16, $"{label}_hiz");
        var hiShl = ctx.Builder.BuildShl(hiZ,
            LLVMValueRef.CreateConstInt(i16, 8, false), $"{label}_hi_shl");
        return ctx.Builder.BuildOr(hiShl, loZ, label);
    }

    /// <summary>Write a 16-bit i16 value into a register pair (high/low split).</summary>
    internal static void WritePair16(EmitContext ctx, RegisterPair pair, LLVMValueRef value16, string label)
    {
        var loPtr = GepNamedRegister(ctx, pair.Low);
        var hiPtr = GepNamedRegister(ctx, pair.High);
        var loByte = ctx.Builder.BuildTrunc(value16, LLVMTypeRef.Int8, $"{label}_lo");
        var hiShr = ctx.Builder.BuildLShr(value16,
            LLVMValueRef.CreateConstInt(LLVMTypeRef.Int16, 8, false), $"{label}_hi_shr");
        var hiByte = ctx.Builder.BuildTrunc(hiShr, LLVMTypeRef.Int8, $"{label}_hi");
        ctx.Builder.BuildStore(loByte, loPtr);
        ctx.Builder.BuildStore(hiByte, hiPtr);
    }

    // ---------------- ops ----------------

    /// <summary>push_pair { name } — read pair then push word.</summary>
    private sealed class PushPair : IMicroOpEmitter
    {
        public string OpName => "push_pair";
        public void Emit(EmitContext ctx, MicroOpStep step)
        {
            var name = step.Raw.GetProperty("name").GetString()!;
            var pair = LookupPair(ctx, name);
            var (_, _, wordBytes) = LocateStackPointer(ctx);
            if (wordBytes != 2)
                throw new NotSupportedException($"push_pair requires 16-bit SP; got {wordBytes * 8}-bit.");
            var value = ReadPair16(ctx, pair, $"push_{name}");
            PushWord(ctx, value, wordBytes, $"push_{name}");
        }
    }

    /// <summary>
    /// pop_pair { name, low_clear_mask?: int } — pop word into pair.
    /// low_clear_mask is AND'd to the popped value before writing
    /// (LR35902 POP AF needs 0xFFF0 to zero F's low nibble).
    /// </summary>
    private sealed class PopPair : IMicroOpEmitter
    {
        public string OpName => "pop_pair";
        public void Emit(EmitContext ctx, MicroOpStep step)
        {
            var name = step.Raw.GetProperty("name").GetString()!;
            var pair = LookupPair(ctx, name);
            var (_, _, wordBytes) = LocateStackPointer(ctx);
            if (wordBytes != 2)
                throw new NotSupportedException($"pop_pair requires 16-bit SP; got {wordBytes * 8}-bit.");
            var popped = PopWord(ctx, wordBytes, $"pop_{name}", LLVMTypeRef.Int16);

            if (step.Raw.TryGetProperty("low_clear_mask", out var lcm))
            {
                var mask = (ushort)lcm.GetUInt32();
                popped = ctx.Builder.BuildAnd(popped,
                    LLVMValueRef.CreateConstInt(LLVMTypeRef.Int16, mask, false),
                    $"pop_{name}_masked");
            }
            WritePair16(ctx, pair, popped, $"pop_{name}");
        }
    }

    /// <summary>
    /// call { target | target_const } — push current PC then branch.
    /// Target is either a previously-resolved varname (from read_imm16
    /// or label arithmetic) or a numeric literal (RST vectors etc).
    /// </summary>
    private sealed class Call : IMicroOpEmitter
    {
        public string OpName => "call";
        public void Emit(EmitContext ctx, MicroOpStep step)
        {
            // PC currently points at the next instruction (read_imm16 or
            // similar has already advanced it). Push it first.
            var (pcPtr, pcType) = LocateProgramCounter(ctx);
            var (_, _, wordBytes) = LocateStackPointer(ctx);
            var curPc = ctx.Builder.BuildLoad2(pcType, pcPtr, "call_pc_cur");
            PushWord(ctx, curPc, wordBytes, "call");

            var target = ResolveCallTarget(ctx, step, pcType);
            var coerced = CoerceToType(ctx, target, pcType, "call_target");
            ctx.Builder.BuildStore(coerced, pcPtr);
        }
    }

    /// <summary>
    /// Resolve target / target_const to an LLVM value. target_const is
    /// emitted at the PC's native width directly so no coercion is
    /// needed downstream; varname targets must come from a prior step.
    /// </summary>
    internal static LLVMValueRef ResolveCallTarget(EmitContext ctx, MicroOpStep step, LLVMTypeRef pcType)
    {
        if (step.Raw.TryGetProperty("target_const", out var tc))
            return LLVMValueRef.CreateConstInt(pcType, tc.GetUInt64(), false);
        var targetName = step.Raw.GetProperty("target").GetString()!;
        return ctx.Resolve(targetName);
    }

    /// <summary>ret — pop word into PC.</summary>
    private sealed class Ret : IMicroOpEmitter
    {
        public string OpName => "ret";
        public void Emit(EmitContext ctx, MicroOpStep step)
        {
            var (pcPtr, pcType) = LocateProgramCounter(ctx);
            var (_, _, wordBytes) = LocateStackPointer(ctx);
            var newPc = PopWord(ctx, wordBytes, "ret", pcType);
            ctx.Builder.BuildStore(newPc, pcPtr);
        }
    }

    /// <summary>
    /// call_cc { target, cond: { reg, flag, value: 0|1 } } — conditional
    /// CALL: push PC + branch only if cond holds. Side-effect-free when
    /// cond fails (no SP modification). Uses two basic blocks rather
    /// than the select trick because push-then-branch isn't a value op
    /// that can be conditionally selected.
    /// </summary>
    private sealed class CallCc : IMicroOpEmitter
    {
        public string OpName => "call_cc";
        public void Emit(EmitContext ctx, MicroOpStep step)
        {
            var pred = ResolveFlagCond(ctx, step.Raw.GetProperty("cond"));

            var thenBB = ctx.Function.AppendBasicBlock("call_cc_taken");
            var endBB  = ctx.Function.AppendBasicBlock("call_cc_end");
            ctx.Builder.BuildCondBr(pred, thenBB, endBB);

            ctx.Builder.PositionAtEnd(thenBB);
            var (pcPtr, pcType) = LocateProgramCounter(ctx);
            var (_, _, wordBytes) = LocateStackPointer(ctx);
            var curPc = ctx.Builder.BuildLoad2(pcType, pcPtr, "call_cc_pc_cur");
            PushWord(ctx, curPc, wordBytes, "call_cc");
            var target = ResolveCallTarget(ctx, step, pcType);
            var coerced = CoerceToType(ctx, target, pcType, "call_cc_target");
            ctx.Builder.BuildStore(coerced, pcPtr);
            // mark PC-written for the open-bus detector
            var flagSlot = ctx.Layout.GepPcWritten(ctx.Builder, ctx.StatePtr);
            ctx.Builder.BuildStore(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, 1, false), flagSlot);
            ctx.Builder.BuildBr(endBB);

            ctx.Builder.PositionAtEnd(endBB);
        }
    }

    /// <summary>
    /// ret_cc { cond: { reg, flag, value: 0|1 } } — conditional RET:
    /// pop into PC only if cond holds. SP unchanged when cond fails.
    /// </summary>
    private sealed class RetCc : IMicroOpEmitter
    {
        public string OpName => "ret_cc";
        public void Emit(EmitContext ctx, MicroOpStep step)
        {
            var pred = ResolveFlagCond(ctx, step.Raw.GetProperty("cond"));

            var thenBB = ctx.Function.AppendBasicBlock("ret_cc_taken");
            var endBB  = ctx.Function.AppendBasicBlock("ret_cc_end");
            ctx.Builder.BuildCondBr(pred, thenBB, endBB);

            ctx.Builder.PositionAtEnd(thenBB);
            var (pcPtr, pcType) = LocateProgramCounter(ctx);
            var (_, _, wordBytes) = LocateStackPointer(ctx);
            var newPc = PopWord(ctx, wordBytes, "ret_cc", pcType);
            ctx.Builder.BuildStore(newPc, pcPtr);
            var flagSlot = ctx.Layout.GepPcWritten(ctx.Builder, ctx.StatePtr);
            ctx.Builder.BuildStore(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, 1, false), flagSlot);
            ctx.Builder.BuildBr(endBB);

            ctx.Builder.PositionAtEnd(endBB);
        }
    }

    /// <summary>
    /// Resolve a `cond: { reg, flag, value }` JSON object to an i1
    /// predicate by reading the named status-register flag bit and
    /// comparing to the expected bit value (0 or 1). Shared by call_cc,
    /// ret_cc, and the BranchCc op in Emitters.cs.
    /// </summary>
    internal static LLVMValueRef ResolveFlagCond(EmitContext ctx, System.Text.Json.JsonElement condEl)
    {
        var reg  = condEl.GetProperty("reg").GetString()!;
        var flag = condEl.GetProperty("flag").GetString()!;
        var expectedVal = condEl.GetProperty("value").GetInt32();

        var bitIdx = ctx.Layout.GetStatusFlagBitIndex(reg, flag);
        var statusPtr = ctx.Layout.GepStatusRegister(ctx.Builder, ctx.StatePtr, reg);
        var statusDef = ctx.Layout.GetStatusRegisterDef(reg);
        var statusType = statusDef.WidthBits switch
        {
            8  => LLVMTypeRef.Int8,
            16 => LLVMTypeRef.Int16,
            32 => LLVMTypeRef.Int32,
            _  => throw new NotSupportedException($"status reg {reg} width {statusDef.WidthBits}-bit unsupported.")
        };
        var statusVal = ctx.Builder.BuildLoad2(statusType, statusPtr, $"{flag.ToLowerInvariant()}_load");
        var bitShift  = LLVMValueRef.CreateConstInt(statusType, (uint)bitIdx, false);
        var bit       = ctx.Builder.BuildAnd(
                            ctx.Builder.BuildLShr(statusVal, bitShift, $"{flag.ToLowerInvariant()}_shr"),
                            LLVMValueRef.CreateConstInt(statusType, 1, false),
                            $"{flag.ToLowerInvariant()}_bit");
        var expectedI = LLVMValueRef.CreateConstInt(statusType, (uint)(expectedVal & 1), false);
        return ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, bit, expectedI, $"cond_{flag.ToLowerInvariant()}");
    }

    /// <summary>
    /// Best-effort int-int type coercion: wider→narrower trunc,
    /// narrower→wider zext, same-width passthrough. Used wherever a
    /// generic op writes to a typed slot (PC, SP, register pair) but
    /// the source value comes from a different-width compute step.
    /// </summary>
    internal static LLVMValueRef CoerceToType(EmitContext ctx, LLVMValueRef value, LLVMTypeRef targetType, string label)
    {
        if (value.TypeOf == targetType) return value;
        var vBits = value.TypeOf.IntWidth;
        var tBits = targetType.IntWidth;
        if (vBits > tBits) return ctx.Builder.BuildTrunc(value, targetType, $"{label}_tr");
        return ctx.Builder.BuildZExt(value, targetType, $"{label}_zx");
    }
}
