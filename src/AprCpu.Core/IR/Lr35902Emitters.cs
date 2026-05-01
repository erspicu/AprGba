using AprCpu.Core.JsonSpec;
using LLVMSharp.Interop;

namespace AprCpu.Core.IR;

/// <summary>
/// LR35902 (Sharp SM83 / Game Boy CPU) — micro-op emitters for the
/// custom <c>lr35902_*</c> ops referenced by <c>spec/lr35902/groups/*.json</c>,
/// plus the named-register helpers (<c>read_reg_named</c> /
/// <c>write_reg_named</c>) that any 8-bit CPU with mixed GPR + status
/// register layout will want.
///
/// Registered into the EmitterRegistry by <see cref="SpecCompiler"/>
/// when <c>architecture.family</c> = <c>"Sharp-SM83"</c>.
///
/// Phase 4.5C: this is a growing file — emitters land in dependency
/// order. The build never fails on missing emitters; instead each
/// missing op surfaces as a diagnostic from the spec compiler, so
/// JsonCpu coverage can grow opcode-by-opcode while the rest stays
/// loadable.
/// </summary>
public static class Lr35902Emitters
{
    public static void RegisterAll(EmitterRegistry reg)
    {
        // Cross-architecture helpers (live here for now; promote to
        // StandardEmitters.cs if a second 8-bit CPU lands).
        reg.Register(new ReadRegNamedEmitter());
        reg.Register(new WriteRegNamedEmitter());

        // LR35902-specific control / flag ops with no operand fetch
        // and no memory access — easiest first cut.
        reg.Register(new ScfEmitter());
        reg.Register(new CcfEmitter());
        reg.Register(new CplEmitter());

        // Halt / stop are placeholders for now: they emit a flag toggle
        // on a future "Internal" status register. Until that register
        // lands in cpu.json, the emitter just no-ops so the function
        // body still verifies. JsonCpu will handle real halt detection.
        reg.Register(new SimpleNoOpEmitter("halt"));
        reg.Register(new SimpleNoOpEmitter("stop"));

        // IME control is also placeholder until the Internal status
        // register lands; JsonCpu will track these in host state.
        reg.Register(new SimpleNoOpEmitter("lr35902_ime"));
        reg.Register(new SimpleNoOpEmitter("lr35902_ime_delayed"));

        // CB-prefix dispatch is host-runtime concern — the compiled
        // function for opcode 0xCB just signals via a no-op; the
        // executor intercepts and pivots to the CB instruction set.
        reg.Register(new SimpleNoOpEmitter("lr35902_cb_dispatch"));

        // DAA — complex BCD adjust. Stubbed for now (modifies nothing);
        // a real emitter is a follow-up since it needs N/H/C-driven flow.
        reg.Register(new SimpleNoOpEmitter("lr35902_daa"));

        // Immediate-byte reads are stubbed to constant 0 until the
        // host-bus extern shim lands. This lets instructions whose
        // immediate is unused (STOP padding byte) compile cleanly.
        reg.Register(new ImmediatePlaceholderEmitter("read_imm8",  LLVMTypeRef.Int8));
        reg.Register(new ImmediatePlaceholderEmitter("read_imm16", LLVMTypeRef.Int16));

        // A-rotate ops in block 0 column 7 (RLCA/RRCA/RLA/RRA). Each
        // rotates A by 1 bit and sets C from the bit that fell out;
        // Z/N/H are always 0 (these are the block-0 versions, distinct
        // from the CB-prefix RLC/RRC/RL/RR which set Z from the result).
        reg.Register(new ARotateEmitter("lr35902_rlca", ARotateKind.Rlca));
        reg.Register(new ARotateEmitter("lr35902_rrca", ARotateKind.Rrca));
        reg.Register(new ARotateEmitter("lr35902_rla",  ARotateKind.Rla));
        reg.Register(new ARotateEmitter("lr35902_rra",  ARotateKind.Rra));
    }

    // ---------------- shared utilities ----------------

    /// <summary>
    /// Look up a register name in either the GPR file or the status-register
    /// list and return a (pointer, element-LLVM-type) pair. Throws if the
    /// name isn't declared anywhere — the spec author either typo'd it or
    /// forgot to declare it in cpu.json.
    /// </summary>
    internal static (LLVMValueRef Ptr, LLVMTypeRef ElemType) LocateRegister(EmitContext ctx, string name)
    {
        var gpr = ctx.Layout.RegisterFile.GeneralPurpose;
        for (int i = 0; i < gpr.Count; i++)
        {
            if (string.Equals(gpr.Names[i], name, StringComparison.Ordinal))
                return (ctx.Layout.GepGpr(ctx.Builder, ctx.StatePtr, i), ctx.Layout.GprType);
        }
        // Status register fallback (F / SP / PC for LR35902).
        foreach (var status in ctx.Layout.RegisterFile.Status)
        {
            if (status.BankedPerMode.Count != 0) continue;
            if (string.Equals(status.Name, name, StringComparison.Ordinal))
            {
                var ptr = ctx.Layout.GepStatusRegister(ctx.Builder, ctx.StatePtr, status.Name);
                var elem = LlvmIntTypeForBits(status.WidthBits);
                return (ptr, elem);
            }
        }
        throw new InvalidOperationException(
            $"read_reg_named/write_reg_named: register '{name}' not declared in spec " +
            $"(GPRs: [{string.Join(",", gpr.Names)}], status: [{string.Join(",", ctx.Layout.RegisterFile.Status.Select(s => s.Name))}]).");
    }

    private static LLVMTypeRef LlvmIntTypeForBits(int bits) => bits switch
    {
        8  => LLVMTypeRef.Int8,
        16 => LLVMTypeRef.Int16,
        32 => LLVMTypeRef.Int32,
        64 => LLVMTypeRef.Int64,
        _  => throw new NotSupportedException($"Unsupported register width {bits}-bit.")
    };
}

// ---------------- named-register I/O ----------------

internal sealed class ReadRegNamedEmitter : IMicroOpEmitter
{
    public string OpName => "read_reg_named";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var name = step.Raw.GetProperty("name").GetString()!;
        var outName = StandardEmitters.GetOut(step.Raw);

        var (ptr, elem) = Lr35902Emitters.LocateRegister(ctx, name);
        var v = ctx.Builder.BuildLoad2(elem, ptr, $"{name.ToLowerInvariant()}_v");
        ctx.Values[outName] = v;
    }
}

internal sealed class WriteRegNamedEmitter : IMicroOpEmitter
{
    public string OpName => "write_reg_named";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var name = step.Raw.GetProperty("name").GetString()!;
        var valueName = step.Raw.GetProperty("value").GetString()!;
        var value = ctx.Resolve(valueName);

        var (ptr, elem) = Lr35902Emitters.LocateRegister(ctx, name);

        // Truncate/extend the value to match the destination width if
        // necessary (e.g. a 16-bit immediate written to an 8-bit register
        // would otherwise type-mismatch in BuildStore).
        var valueType = value.TypeOf;
        if (!valueType.Equals(elem))
        {
            value = AdjustWidth(ctx, value, valueType, elem, $"{name.ToLowerInvariant()}_resized");
        }
        ctx.Builder.BuildStore(value, ptr);
    }

    private static LLVMValueRef AdjustWidth(EmitContext ctx, LLVMValueRef value, LLVMTypeRef from, LLVMTypeRef to, string label)
    {
        var fromBits = from.IntWidth;
        var toBits = to.IntWidth;
        if (toBits < fromBits) return ctx.Builder.BuildTrunc(value, to, label);
        if (toBits > fromBits) return ctx.Builder.BuildZExt(value, to, label);
        return value;
    }
}

// ---------------- F-register flag ops ----------------

/// <summary>
/// SCF — set carry flag. C ← 1, N ← 0, H ← 0, Z preserved.
/// </summary>
internal sealed class ScfEmitter : IMicroOpEmitter
{
    public string OpName => "lr35902_scf";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        WriteFlagsScfCcfStyle(ctx, carryValue: ctx.Builder.BuildZExt(
            LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, 1, false),
            LLVMTypeRef.Int8, "scf_c"));
    }

    /// <summary>
    /// Common: clear N and H, set C to <paramref name="carryValue"/> (i8 0/1),
    /// preserve Z. Implements both SCF (carryValue=1) and the C-write half
    /// of CCF (carryValue=!oldC).
    /// </summary>
    internal static void WriteFlagsScfCcfStyle(EmitContext ctx, LLVMValueRef carryValue)
    {
        var fPtr = ctx.Layout.GepStatusRegister(ctx.Builder, ctx.StatePtr, "F");
        var fOld = ctx.Builder.BuildLoad2(LLVMTypeRef.Int8, fPtr, "f_old");

        // Mask: keep Z (bit 7), clear N (6), H (5), and the existing C (4).
        var preserveMask = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, 0x80, false);
        var preserved    = ctx.Builder.BuildAnd(fOld, preserveMask, "f_preserve_z");

        // Stamp new C in bit 4.
        var cMask = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, 0x10, false);
        var cBit  = ctx.Builder.BuildAnd(carryValue, LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, 1, false), "c_bit");
        var cShifted = ctx.Builder.BuildShl(cBit, LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, 4, false), "c_shifted");
        // (extra mask is defensive — cBit already 0/1, so cShifted is 0 or 0x10)
        cShifted = ctx.Builder.BuildAnd(cShifted, cMask, "c_in_place");

        var fNew = ctx.Builder.BuildOr(preserved, cShifted, "f_new");
        ctx.Builder.BuildStore(fNew, fPtr);
    }
}

/// <summary>CCF — toggle carry. C ← !C, N ← 0, H ← 0, Z preserved.</summary>
internal sealed class CcfEmitter : IMicroOpEmitter
{
    public string OpName => "lr35902_ccf";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var fPtr = ctx.Layout.GepStatusRegister(ctx.Builder, ctx.StatePtr, "F");
        var fOld = ctx.Builder.BuildLoad2(LLVMTypeRef.Int8, fPtr, "f_old");

        // Extract existing C (bit 4) into bit 0 of an i8.
        var cShr = ctx.Builder.BuildLShr(fOld, LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, 4, false), "c_shr");
        var cBit = ctx.Builder.BuildAnd(cShr, LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, 1, false), "c_old");
        // Toggle.
        var cNew = ctx.Builder.BuildXor(cBit, LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, 1, false), "c_new");

        ScfEmitter.WriteFlagsScfCcfStyle(ctx, cNew);
    }
}

/// <summary>
/// CPL — A ← ~A, N ← 1, H ← 1, Z and C preserved.
/// </summary>
internal sealed class CplEmitter : IMicroOpEmitter
{
    public string OpName => "lr35902_cpl";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        // 1. A ← ~A
        var (aPtr, _) = Lr35902Emitters.LocateRegister(ctx, "A");
        var aOld = ctx.Builder.BuildLoad2(LLVMTypeRef.Int8, aPtr, "a_old");
        var aNew = ctx.Builder.BuildXor(aOld, LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, 0xFF, false), "a_inv");
        ctx.Builder.BuildStore(aNew, aPtr);

        // 2. F: set N=H=1, preserve Z and C.
        var fPtr = ctx.Layout.GepStatusRegister(ctx.Builder, ctx.StatePtr, "F");
        var fOld = ctx.Builder.BuildLoad2(LLVMTypeRef.Int8, fPtr, "f_old");
        // Preserve Z (bit 7) and C (bit 4): mask = 0x90.
        var preserveMask = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, 0x90, false);
        var preserved    = ctx.Builder.BuildAnd(fOld, preserveMask, "f_preserve_zc");
        // Set N (bit 6) and H (bit 5): or with 0x60.
        var setMask      = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, 0x60, false);
        var fNew         = ctx.Builder.BuildOr(preserved, setMask, "f_new");
        ctx.Builder.BuildStore(fNew, fPtr);
    }
}

// ---------------- placeholder no-op emitters ----------------

/// <summary>
/// Emits an empty body for ops whose semantics depend on host runtime
/// state we haven't introduced yet (HALT, STOP) — keeps the spec
/// compilable end-to-end so we can grow coverage incrementally without
/// breaking module verification.
/// </summary>
internal sealed class SimpleNoOpEmitter : IMicroOpEmitter
{
    public string OpName { get; }
    public SimpleNoOpEmitter(string opName) { OpName = opName; }
    public void Emit(EmitContext ctx, MicroOpStep step) { /* deliberately empty */ }
}

/// <summary>
/// Stub for read_imm8 / read_imm16 until the host-bus extern is wired.
/// Produces a constant 0 of the correct width so dependent steps compile.
/// JsonCpu will replace this with a real fetch via the GB memory bus.
/// </summary>
internal sealed class ImmediatePlaceholderEmitter : IMicroOpEmitter
{
    public string OpName { get; }
    private readonly LLVMTypeRef _width;

    public ImmediatePlaceholderEmitter(string opName, LLVMTypeRef width)
    {
        OpName = opName;
        _width = width;
    }

    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var outName = StandardEmitters.GetOut(step.Raw);
        ctx.Values[outName] = LLVMValueRef.CreateConstInt(_width, 0, false);
    }
}

// ---------------- A-rotate (block 0 column 7) ----------------

internal enum ARotateKind { Rlca, Rrca, Rla, Rra }

/// <summary>
/// RLCA/RRCA/RLA/RRA — single-byte rotate of A. Z/N/H always cleared;
/// C set from the bit that rolled out. RLA/RRA use the existing C as
/// the input bit (rotate-through-carry); RLCA/RRCA do a circular
/// rotate where C just mirrors the bit that wraps.
/// </summary>
internal sealed class ARotateEmitter : IMicroOpEmitter
{
    public string OpName { get; }
    private readonly ARotateKind _kind;

    public ARotateEmitter(string opName, ARotateKind kind)
    {
        OpName = opName;
        _kind = kind;
    }

    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i8 = LLVMTypeRef.Int8;
        var (aPtr, _) = Lr35902Emitters.LocateRegister(ctx, "A");
        var aOld = ctx.Builder.BuildLoad2(i8, aPtr, "a_old");

        // Read existing C as i8 0/1.
        var fPtr = ctx.Layout.GepStatusRegister(ctx.Builder, ctx.StatePtr, "F");
        var fOld = ctx.Builder.BuildLoad2(i8, fPtr, "f_old");
        var cIn  = ctx.Builder.BuildAnd(
                      ctx.Builder.BuildLShr(fOld, ConstI8(4), "f_c_shr"),
                      ConstI8(1), "c_in");

        // Compute the new A and the C-out bit (i8, 0 or 1).
        LLVMValueRef aNew, cOut;
        switch (_kind)
        {
            case ARotateKind.Rlca:
                cOut = ctx.Builder.BuildAnd(
                           ctx.Builder.BuildLShr(aOld, ConstI8(7), "a_top_shr"),
                           ConstI8(1), "c_out");
                aNew = ctx.Builder.BuildOr(
                           ctx.Builder.BuildShl (aOld, ConstI8(1), "a_shl"),
                           cOut, "a_rlca");
                break;
            case ARotateKind.Rrca:
                cOut = ctx.Builder.BuildAnd(aOld, ConstI8(1), "c_out");
                aNew = ctx.Builder.BuildOr(
                           ctx.Builder.BuildLShr(aOld, ConstI8(1), "a_shr"),
                           ctx.Builder.BuildShl (cOut, ConstI8(7), "c_top"),
                           "a_rrca");
                break;
            case ARotateKind.Rla:
                cOut = ctx.Builder.BuildAnd(
                           ctx.Builder.BuildLShr(aOld, ConstI8(7), "a_top_shr"),
                           ConstI8(1), "c_out");
                aNew = ctx.Builder.BuildOr(
                           ctx.Builder.BuildShl(aOld, ConstI8(1), "a_shl"),
                           cIn, "a_rla");
                break;
            case ARotateKind.Rra:
                cOut = ctx.Builder.BuildAnd(aOld, ConstI8(1), "c_out");
                aNew = ctx.Builder.BuildOr(
                           ctx.Builder.BuildLShr(aOld, ConstI8(1), "a_shr"),
                           ctx.Builder.BuildShl (cIn, ConstI8(7), "c_top"),
                           "a_rra");
                break;
            default:
                throw new InvalidOperationException();
        }

        ctx.Builder.BuildStore(aNew, aPtr);

        // F: Z=N=H=0; C=cOut. Effectively F = cOut << 4 (everything else cleared).
        var cBit = ctx.Builder.BuildAnd(cOut, ConstI8(1), "c_bit");
        var cInPlace = ctx.Builder.BuildShl(cBit, ConstI8(4), "c_in_place");
        ctx.Builder.BuildStore(cInPlace, fPtr);
    }

    private static LLVMValueRef ConstI8(byte v) =>
        LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, v, false);
}
