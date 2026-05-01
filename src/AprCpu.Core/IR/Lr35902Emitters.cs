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

        // Wave 2: 16-bit register-pair I/O. Composes/decomposes the
        // four named pairs (AF/BC/DE/HL) from their two 8-bit halves;
        // SP/PC are loaded/stored directly as i16 status registers.
        reg.Register(new ReadRegPairNamedEmitter());
        reg.Register(new WriteRegPairNamedEmitter());

        // Wave 2: field-driven 8-bit register access. The 3-bit sss/ddd
        // field maps via the LR35902 lookup (000=B…101=L, 110=(HL),
        // 111=A). The (HL) variant requires the memory-bus extern,
        // which isn't wired yet — placeholder load/stores 0.
        reg.Register(new Lr35902ReadR8Emitter());
        reg.Register(new Lr35902WriteR8Emitter());

        // Memory-bus extern placeholders (load_byte / store_byte /
        // store_word). Until JsonCpu wires the GB bus, these compile
        // to no-ops (load returns 0). Real impl follows the ARM
        // host_swap_register_bank inttoptr-in-initializer pattern.
        reg.Register(new ImmediatePlaceholderEmitter("load_byte", LLVMTypeRef.Int8));
        reg.Register(new SimpleNoOpEmitter("store_byte"));
        reg.Register(new SimpleNoOpEmitter("store_word"));

        // 16-bit pair INC/DEC by name (used in (HL+) / (HL-) variants).
        reg.Register(new Lr35902IncDecPairEmitter("lr35902_inc_pair", isInc: true));
        reg.Register(new Lr35902IncDecPairEmitter("lr35902_dec_pair", isInc: false));

        // Wave 3: 8-bit ALU on A. All three source variants share the
        // same operation table (ADD/ADC/SUB/SBC/AND/XOR/OR/CP) but
        // differ in how they obtain the second operand:
        //   _r8   — field-driven 3-bit source (sss), with (HL) placeholder
        //   _hl   — memory[HL] (placeholder until bus extern lands)
        //   _imm8 — fetched immediate byte (placeholder constant 0)
        reg.Register(new Lr35902Alu8Emitter("lr35902_alu_a_r8",   Lr35902Alu8Source.RegField));
        reg.Register(new Lr35902Alu8Emitter("lr35902_alu_a_hl",   Lr35902Alu8Source.HlMemory));
        reg.Register(new Lr35902Alu8Emitter("lr35902_alu_a_imm8", Lr35902Alu8Source.Immediate));

        // Wave 3: 8-bit INC / DEC selected by 3-bit ddd field.
        reg.Register(new Lr35902IncDecR8Emitter("lr35902_inc_r8", isInc: true));
        reg.Register(new Lr35902IncDecR8Emitter("lr35902_dec_r8", isInc: false));

        // Wave 3: 16-bit register-pair operations selected by 2-bit dd field.
        // dd: 00=BC, 01=DE, 10=HL, 11=SP per LR35902 encoding.
        reg.Register(new Lr35902WriteRrDdEmitter());
        reg.Register(new Lr35902IncDecRrDdEmitter("lr35902_inc_rr_dd", isInc: true));
        reg.Register(new Lr35902IncDecRrDdEmitter("lr35902_dec_rr_dd", isInc: false));
        reg.Register(new Lr35902AddHlRrEmitter());
    }

    /// <summary>
    /// LR35902 2-bit dd field (used in 16-bit register-pair instructions)
    /// → pair name. dd: 00=BC, 01=DE, 10=HL, 11=SP.
    /// </summary>
    internal static string Dd2BitToRrName(int dd) => dd switch
    {
        0 => "BC",
        1 => "DE",
        2 => "HL",
        3 => "SP",
        _ => throw new ArgumentOutOfRangeException(nameof(dd))
    };

    /// <summary>
    /// Helper: write the F register from individual flag i8 values
    /// (each 0 or 1). Shifts each into its bit position and ORs them.
    /// </summary>
    internal static void StoreFlags(EmitContext ctx, LLVMValueRef z, LLVMValueRef n, LLVMValueRef h, LLVMValueRef c)
    {
        var fPtr = ctx.Layout.GepStatusRegister(ctx.Builder, ctx.StatePtr, "F");
        var i8 = LLVMTypeRef.Int8;
        var zSh = ctx.Builder.BuildShl(z, LLVMValueRef.CreateConstInt(i8, 7, false), "z_sh");
        var nSh = ctx.Builder.BuildShl(n, LLVMValueRef.CreateConstInt(i8, 6, false), "n_sh");
        var hSh = ctx.Builder.BuildShl(h, LLVMValueRef.CreateConstInt(i8, 5, false), "h_sh");
        var cSh = ctx.Builder.BuildShl(c, LLVMValueRef.CreateConstInt(i8, 4, false), "c_sh");
        var f1  = ctx.Builder.BuildOr(zSh, nSh, "f_zn");
        var f2  = ctx.Builder.BuildOr(hSh, cSh, "f_hc");
        var fNew = ctx.Builder.BuildOr(f1, f2, "f_new");
        ctx.Builder.BuildStore(fNew, fPtr);
    }

    /// <summary>
    /// Helper: read the C flag as i8 0/1.
    /// </summary>
    internal static LLVMValueRef ReadCarry(EmitContext ctx)
    {
        var fPtr = ctx.Layout.GepStatusRegister(ctx.Builder, ctx.StatePtr, "F");
        var f = ctx.Builder.BuildLoad2(LLVMTypeRef.Int8, fPtr, "f_for_c");
        var sh = ctx.Builder.BuildLShr(f, LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, 4, false), "c_sh");
        return ctx.Builder.BuildAnd(sh, LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, 1, false), "c_in");
    }

    /// <summary>
    /// Look up a register-pair definition by name in the spec's
    /// register_pairs table. Returns null if no such pair exists
    /// (the name might be a 16-bit status register like SP/PC).
    /// </summary>
    internal static RegisterPair? LocateRegisterPair(EmitContext ctx, string name)
    {
        foreach (var p in ctx.Layout.RegisterFile.RegisterPairs)
        {
            if (string.Equals(p.Name, name, StringComparison.Ordinal))
                return p;
        }
        return null;
    }

    /// <summary>
    /// Compose an i16 from a pair's two 8-bit halves.
    /// </summary>
    internal static LLVMValueRef ComposePairValue(EmitContext ctx, RegisterPair pair, string outName)
    {
        var (hiPtr, _) = LocateRegister(ctx, pair.High);
        var (loPtr, _) = LocateRegister(ctx, pair.Low);
        var hi8 = ctx.Builder.BuildLoad2(LLVMTypeRef.Int8, hiPtr, $"{pair.Name}_hi");
        var lo8 = ctx.Builder.BuildLoad2(LLVMTypeRef.Int8, loPtr, $"{pair.Name}_lo");
        var hi16 = ctx.Builder.BuildZExt(hi8, LLVMTypeRef.Int16, $"{pair.Name}_hi16");
        var lo16 = ctx.Builder.BuildZExt(lo8, LLVMTypeRef.Int16, $"{pair.Name}_lo16");
        var hiSh = ctx.Builder.BuildShl(hi16, LLVMValueRef.CreateConstInt(LLVMTypeRef.Int16, 8, false), $"{pair.Name}_hi_shl");
        return ctx.Builder.BuildOr(hiSh, lo16, outName);
    }

    /// <summary>
    /// Decompose an i16 into the pair's two 8-bit halves and store back.
    /// For AF, the F low nibble is force-zeroed per LR35902 spec.
    /// </summary>
    internal static void DecomposePairValue(EmitContext ctx, RegisterPair pair, LLVMValueRef value16)
    {
        var (hiPtr, _) = LocateRegister(ctx, pair.High);
        var (loPtr, _) = LocateRegister(ctx, pair.Low);
        var hi8 = ctx.Builder.BuildTrunc(
                      ctx.Builder.BuildLShr(value16,
                          LLVMValueRef.CreateConstInt(LLVMTypeRef.Int16, 8, false),
                          $"{pair.Name}_hi_shr"),
                      LLVMTypeRef.Int8, $"{pair.Name}_hi8");
        var lo8 = ctx.Builder.BuildTrunc(value16, LLVMTypeRef.Int8, $"{pair.Name}_lo8");

        // AF special: F low nibble is always zero on LR35902 (the unused
        // flag bits are unwritable). Mask before storing into the F slot.
        if (string.Equals(pair.Name, "AF", StringComparison.Ordinal) &&
            string.Equals(pair.Low, "F", StringComparison.Ordinal))
        {
            lo8 = ctx.Builder.BuildAnd(lo8,
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, 0xF0, false),
                "f_masked");
        }
        ctx.Builder.BuildStore(hi8, hiPtr);
        ctx.Builder.BuildStore(lo8, loPtr);
    }

    /// <summary>
    /// LR35902 sss/ddd 3-bit-field → GPR-index lookup table.
    /// Returns -1 for value 6 (the (HL) memory case) so callers can
    /// branch on that. Indices match cpu.json's GPR ordering
    /// (A=0, B=1, C=2, D=3, E=4, H=5, L=6).
    /// </summary>
    internal static int Sss3BitToGprIndex(int field) => field switch
    {
        0 => 1,    // B
        1 => 2,    // C
        2 => 3,    // D
        3 => 4,    // E
        4 => 5,    // H
        5 => 6,    // L
        6 => -1,   // (HL) — caller handles via memory access
        7 => 0,    // A
        _ => throw new ArgumentOutOfRangeException(nameof(field))
    };

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

// ---------------- 16-bit register pair I/O ----------------

/// <summary>
/// read_reg_pair_named — load a 16-bit value from a named register pair.
/// For declared pairs (AF/BC/DE/HL): composes (high &lt;&lt; 8) | low from
/// the two 8-bit halves. For 16-bit status registers (SP/PC): loads
/// directly. Output is always i16.
/// </summary>
internal sealed class ReadRegPairNamedEmitter : IMicroOpEmitter
{
    public string OpName => "read_reg_pair_named";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var name    = step.Raw.GetProperty("name").GetString()!;
        var outName = StandardEmitters.GetOut(step.Raw);

        var pair = Lr35902Emitters.LocateRegisterPair(ctx, name);
        if (pair is not null)
        {
            ctx.Values[outName] = Lr35902Emitters.ComposePairValue(ctx, pair, outName);
            return;
        }

        // Fallback: 16-bit status register (SP / PC).
        var (ptr, elem) = Lr35902Emitters.LocateRegister(ctx, name);
        if (elem.IntWidth != 16)
            throw new InvalidOperationException(
                $"read_reg_pair_named '{name}': resolved register is {elem.IntWidth}-bit; expected 16 (declared as a register_pair or 16-bit status reg).");
        ctx.Values[outName] = ctx.Builder.BuildLoad2(elem, ptr, $"{name.ToLowerInvariant()}_v");
    }
}

/// <summary>
/// write_reg_pair_named — store a 16-bit value to a named register pair.
/// Mirror of read_reg_pair_named: splits an i16 into high/low halves
/// for declared pairs, or stores directly for 16-bit status registers.
/// </summary>
internal sealed class WriteRegPairNamedEmitter : IMicroOpEmitter
{
    public string OpName => "write_reg_pair_named";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var name      = step.Raw.GetProperty("name").GetString()!;
        var valueName = step.Raw.GetProperty("value").GetString()!;
        var raw       = ctx.Resolve(valueName);

        // Normalise the source value to i16 if needed.
        var rawType = raw.TypeOf;
        LLVMValueRef value16;
        if (rawType.IntWidth == 16) value16 = raw;
        else if (rawType.IntWidth < 16) value16 = ctx.Builder.BuildZExt(raw, LLVMTypeRef.Int16, $"{valueName}_z16");
        else                            value16 = ctx.Builder.BuildTrunc(raw, LLVMTypeRef.Int16, $"{valueName}_t16");

        var pair = Lr35902Emitters.LocateRegisterPair(ctx, name);
        if (pair is not null)
        {
            Lr35902Emitters.DecomposePairValue(ctx, pair, value16);
            return;
        }

        var (ptr, elem) = Lr35902Emitters.LocateRegister(ctx, name);
        if (elem.IntWidth != 16)
            throw new InvalidOperationException(
                $"write_reg_pair_named '{name}': resolved register is {elem.IntWidth}-bit; expected 16.");
        ctx.Builder.BuildStore(value16, ptr);
    }
}

// ---------------- field-driven r8 access ----------------

/// <summary>
/// lr35902_read_r8 — produce an i8 value selected by a 3-bit sss/ddd
/// field. Implements the LR35902 lookup B/C/D/E/H/L/(HL)/A.
///
/// Approach: chain of LLVM <c>select</c> instructions, since the field
/// value is runtime-known (extracted from the instruction word). The
/// (HL) variant currently loads constant 0 — needs the memory-bus
/// extern to be wired before it's correct.
/// </summary>
internal sealed class Lr35902ReadR8Emitter : IMicroOpEmitter
{
    public string OpName => "lr35902_read_r8";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var fieldName = step.Raw.GetProperty("field").GetString()!;
        var outName   = StandardEmitters.GetOut(step.Raw);
        var i8 = LLVMTypeRef.Int8;

        var field32 = ctx.Resolve(fieldName);     // i32, 0..7
        // Load all 7 GPRs upfront — the dead loads will be DCE'd by LLVM.
        // (HL) memory case is a placeholder constant 0.
        var values = new LLVMValueRef[8];
        for (int sss = 0; sss < 8; sss++)
        {
            int gprIdx = Lr35902Emitters.Sss3BitToGprIndex(sss);
            if (gprIdx < 0)
            {
                values[sss] = LLVMValueRef.CreateConstInt(i8, 0, false);
                continue;
            }
            var ptr = ctx.Layout.GepGpr(ctx.Builder, ctx.StatePtr, gprIdx);
            values[sss] = ctx.Builder.BuildLoad2(i8, ptr, $"r8_{Lr35902GprName(gprIdx)}");
        }

        // Build the chain right-to-left:  values[0] if field==0 else (values[1] if field==1 else (...))
        var current = values[7];
        for (int sss = 6; sss >= 0; sss--)
        {
            var cmp = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ,
                field32,
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (uint)sss, false),
                $"is_sss{sss}");
            current = ctx.Builder.BuildSelect(cmp, values[sss], current, $"r8_sel{sss}");
        }
        ctx.Values[outName] = current;
    }

    private static string Lr35902GprName(int gprIdx) => gprIdx switch
    {
        0 => "a", 1 => "b", 2 => "c", 3 => "d",
        4 => "e", 5 => "h", 6 => "l",
        _ => $"r{gprIdx}"
    };
}

/// <summary>
/// lr35902_write_r8 — store a value to the GPR (or HL-pointed memory)
/// selected by a 3-bit ddd field. Emits a chain of conditional stores;
/// the (HL) case is currently a no-op until the memory bus is wired.
/// </summary>
internal sealed class Lr35902WriteR8Emitter : IMicroOpEmitter
{
    public string OpName => "lr35902_write_r8";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var fieldName = step.Raw.GetProperty("field").GetString()!;
        var valueName = step.Raw.GetProperty("value").GetString()!;

        var field32 = ctx.Resolve(fieldName);
        var raw     = ctx.Resolve(valueName);
        // Normalise to i8.
        var i8 = LLVMTypeRef.Int8;
        if (raw.TypeOf.IntWidth != 8)
        {
            raw = raw.TypeOf.IntWidth < 8
                ? ctx.Builder.BuildZExt(raw, i8, $"{valueName}_z8")
                : ctx.Builder.BuildTrunc(raw, i8, $"{valueName}_t8");
        }

        // For each ddd value 0..7: read current GPR (or 0), select-merge with
        // the new value when ddd matches, store back. (HL) case skipped.
        for (int ddd = 0; ddd < 8; ddd++)
        {
            int gprIdx = Lr35902Emitters.Sss3BitToGprIndex(ddd);
            if (gprIdx < 0) continue;     // (HL) — placeholder, no store

            var ptr = ctx.Layout.GepGpr(ctx.Builder, ctx.StatePtr, gprIdx);
            var prev = ctx.Builder.BuildLoad2(i8, ptr, $"r8_w_old_{gprIdx}");
            var cmp = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ,
                field32,
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (uint)ddd, false),
                $"is_ddd{ddd}");
            var merged = ctx.Builder.BuildSelect(cmp, raw, prev, $"r8_w_merge_{gprIdx}");
            ctx.Builder.BuildStore(merged, ptr);
        }
    }
}

/// <summary>
/// lr35902_inc_pair / lr35902_dec_pair — adjust a named register pair
/// by ±1 in place. Used by LD (HL+),A and LD (HL-),A variants.
/// </summary>
internal sealed class Lr35902IncDecPairEmitter : IMicroOpEmitter
{
    public string OpName { get; }
    private readonly bool _isInc;

    public Lr35902IncDecPairEmitter(string opName, bool isInc)
    {
        OpName = opName;
        _isInc = isInc;
    }

    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var name = step.Raw.GetProperty("name").GetString()!;
        var pair = Lr35902Emitters.LocateRegisterPair(ctx, name);

        LLVMValueRef cur;
        if (pair is not null)
        {
            cur = Lr35902Emitters.ComposePairValue(ctx, pair, $"{name.ToLowerInvariant()}_cur");
        }
        else
        {
            var (ptr, elem) = Lr35902Emitters.LocateRegister(ctx, name);
            if (elem.IntWidth != 16)
                throw new InvalidOperationException(
                    $"lr35902_inc/dec_pair '{name}': resolved register is {elem.IntWidth}-bit; expected 16.");
            cur = ctx.Builder.BuildLoad2(elem, ptr, $"{name.ToLowerInvariant()}_cur");
        }

        var one = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int16, 1, false);
        var next = _isInc
            ? ctx.Builder.BuildAdd(cur, one, $"{name.ToLowerInvariant()}_inc")
            : ctx.Builder.BuildSub(cur, one, $"{name.ToLowerInvariant()}_dec");

        if (pair is not null)
        {
            Lr35902Emitters.DecomposePairValue(ctx, pair, next);
        }
        else
        {
            var (ptr, _) = Lr35902Emitters.LocateRegister(ctx, name);
            ctx.Builder.BuildStore(next, ptr);
        }
    }
}

// ---------------- 8-bit ALU on A ----------------

internal enum Lr35902Alu8Source { RegField, HlMemory, Immediate }

/// <summary>
/// 8-bit ALU on A — implements ADD/ADC/SUB/SBC/AND/XOR/OR/CP. Read
/// the second operand using the source-mode (sss field, memory[HL],
/// or immediate byte), perform the op, write A back (except CP) and
/// update Z/N/H/C per LR35902 rules.
/// </summary>
internal sealed class Lr35902Alu8Emitter : IMicroOpEmitter
{
    public string OpName { get; }
    private readonly Lr35902Alu8Source _source;

    public Lr35902Alu8Emitter(string opName, Lr35902Alu8Source source)
    {
        OpName = opName;
        _source = source;
    }

    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var aluOp = step.Raw.GetProperty("alu_op").GetString()!;
        var i8 = LLVMTypeRef.Int8;
        var i32 = LLVMTypeRef.Int32;

        // Load A.
        var (aPtr, _) = Lr35902Emitters.LocateRegister(ctx, "A");
        var a = ctx.Builder.BuildLoad2(i8, aPtr, "a_old");

        // Load source.
        LLVMValueRef src = _source switch
        {
            Lr35902Alu8Source.RegField  => ResolveR8FromField(ctx, step),
            Lr35902Alu8Source.HlMemory  => LLVMValueRef.CreateConstInt(i8, 0, false),  // placeholder (memory bus not wired)
            Lr35902Alu8Source.Immediate => LLVMValueRef.CreateConstInt(i8, 0, false),  // placeholder (read_imm8 stubbed)
            _ => throw new InvalidOperationException()
        };

        // C-in for ADC/SBC.
        var cIn = (aluOp is "adc" or "sbc")
            ? Lr35902Emitters.ReadCarry(ctx)
            : LLVMValueRef.CreateConstInt(i8, 0, false);

        // Widen to i32 for arithmetic + flag derivation.
        var aZ = ctx.Builder.BuildZExt(a,   i32, "a_z");
        var sZ = ctx.Builder.BuildZExt(src, i32, "s_z");
        var cZ = ctx.Builder.BuildZExt(cIn, i32, "c_z");

        LLVMValueRef result32, h, c, n;
        switch (aluOp)
        {
            case "add":
            case "adc":
            {
                result32 = ctx.Builder.BuildAdd(ctx.Builder.BuildAdd(aZ, sZ, "a_s"), cZ, "a_s_c");
                // H = ((A & 0xF) + (s & 0xF) + cin) > 0xF
                var aLo = ctx.Builder.BuildAnd(aZ, ctx.ConstU32(0xF), "a_lo");
                var sLo = ctx.Builder.BuildAnd(sZ, ctx.ConstU32(0xF), "s_lo");
                var loSum = ctx.Builder.BuildAdd(ctx.Builder.BuildAdd(aLo, sLo, "lo_sum"), cZ, "lo_sum_c");
                h = BoolToI8(ctx, ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntUGT, loSum, ctx.ConstU32(0xF), "h_cmp"));
                c = BoolToI8(ctx, ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntUGT, result32, ctx.ConstU32(0xFF), "c_cmp"));
                n = LLVMValueRef.CreateConstInt(i8, 0, false);
                break;
            }
            case "sub":
            case "sbc":
            case "cp":
            {
                var subC = (aluOp == "sbc") ? cZ : ctx.ConstU32(0);
                var sPlusC = ctx.Builder.BuildAdd(sZ, subC, "s_plus_c");
                result32 = ctx.Builder.BuildSub(aZ, sPlusC, "a_minus_s");
                // H borrow: ((A & 0xF) < ((s & 0xF) + cin))
                var aLo = ctx.Builder.BuildAnd(aZ, ctx.ConstU32(0xF), "a_lo");
                var sLo = ctx.Builder.BuildAnd(sZ, ctx.ConstU32(0xF), "s_lo");
                var sLoPlusC = ctx.Builder.BuildAdd(sLo, subC, "s_lo_plus_c");
                h = BoolToI8(ctx, ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntULT, aLo, sLoPlusC, "h_cmp"));
                c = BoolToI8(ctx, ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntULT, aZ, sPlusC, "c_cmp"));
                n = LLVMValueRef.CreateConstInt(i8, 1, false);
                break;
            }
            case "and":
            {
                result32 = ctx.Builder.BuildAnd(aZ, sZ, "a_and_s");
                n = LLVMValueRef.CreateConstInt(i8, 0, false);
                h = LLVMValueRef.CreateConstInt(i8, 1, false);
                c = LLVMValueRef.CreateConstInt(i8, 0, false);
                break;
            }
            case "xor":
            {
                result32 = ctx.Builder.BuildXor(aZ, sZ, "a_xor_s");
                n = LLVMValueRef.CreateConstInt(i8, 0, false);
                h = LLVMValueRef.CreateConstInt(i8, 0, false);
                c = LLVMValueRef.CreateConstInt(i8, 0, false);
                break;
            }
            case "or":
            {
                result32 = ctx.Builder.BuildOr(aZ, sZ, "a_or_s");
                n = LLVMValueRef.CreateConstInt(i8, 0, false);
                h = LLVMValueRef.CreateConstInt(i8, 0, false);
                c = LLVMValueRef.CreateConstInt(i8, 0, false);
                break;
            }
            default:
                throw new InvalidOperationException($"lr35902_alu_a_*: unknown alu_op '{aluOp}'.");
        }

        var result8 = ctx.Builder.BuildTrunc(result32, i8, "alu_result8");

        // Z = (result8 == 0)
        var z = BoolToI8(ctx, ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, result8,
            LLVMValueRef.CreateConstInt(i8, 0, false), "z_cmp"));

        // Write A unless this is CP (compare-only).
        if (aluOp != "cp") ctx.Builder.BuildStore(result8, aPtr);

        // Write F.
        Lr35902Emitters.StoreFlags(ctx, z, n, h, c);
    }

    private LLVMValueRef ResolveR8FromField(EmitContext ctx, MicroOpStep step)
    {
        var i8 = LLVMTypeRef.Int8;
        var fieldName = step.Raw.GetProperty("field").GetString()!;
        var field32 = ctx.Resolve(fieldName);

        var values = new LLVMValueRef[8];
        for (int sss = 0; sss < 8; sss++)
        {
            int gprIdx = Lr35902Emitters.Sss3BitToGprIndex(sss);
            if (gprIdx < 0)
            {
                values[sss] = LLVMValueRef.CreateConstInt(i8, 0, false);   // (HL) placeholder
                continue;
            }
            var ptr = ctx.Layout.GepGpr(ctx.Builder, ctx.StatePtr, gprIdx);
            values[sss] = ctx.Builder.BuildLoad2(i8, ptr, $"alu_src_{gprIdx}");
        }
        var current = values[7];
        for (int sss = 6; sss >= 0; sss--)
        {
            var cmp = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, field32,
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (uint)sss, false), $"alu_is_sss{sss}");
            current = ctx.Builder.BuildSelect(cmp, values[sss], current, $"alu_src_sel{sss}");
        }
        return current;
    }

    private static LLVMValueRef BoolToI8(EmitContext ctx, LLVMValueRef i1)
        => ctx.Builder.BuildZExt(i1, LLVMTypeRef.Int8, "to_i8");
}

// ---------------- 8-bit INC/DEC ----------------

/// <summary>
/// lr35902_inc_r8 / lr35902_dec_r8 — INC r / DEC r selected by 3-bit
/// ddd field. Updates Z/N/H; C preserved. (HL) variant uses memory
/// (placeholder until bus extern lands).
/// </summary>
internal sealed class Lr35902IncDecR8Emitter : IMicroOpEmitter
{
    public string OpName { get; }
    private readonly bool _isInc;

    public Lr35902IncDecR8Emitter(string opName, bool isInc)
    {
        OpName = opName;
        _isInc = isInc;
    }

    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var fieldName = step.Raw.GetProperty("field").GetString()!;
        var field32 = ctx.Resolve(fieldName);
        var i8 = LLVMTypeRef.Int8;
        var i32 = LLVMTypeRef.Int32;
        var one = LLVMValueRef.CreateConstInt(i8, 1, false);

        // For each ddd 0..7, compute current → next, conditionally store and
        // update F. (HL) skipped for now (placeholder).
        for (int ddd = 0; ddd < 8; ddd++)
        {
            int gprIdx = Lr35902Emitters.Sss3BitToGprIndex(ddd);
            if (gprIdx < 0) continue;

            var ptr  = ctx.Layout.GepGpr(ctx.Builder, ctx.StatePtr, gprIdx);
            var prev = ctx.Builder.BuildLoad2(i8, ptr, $"r8_idx{ddd}_prev");
            var next = _isInc
                ? ctx.Builder.BuildAdd(prev, one, $"r8_idx{ddd}_inc")
                : ctx.Builder.BuildSub(prev, one, $"r8_idx{ddd}_dec");

            var cmp = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, field32,
                LLVMValueRef.CreateConstInt(i32, (uint)ddd, false), $"is_ddd{ddd}");
            var stored = ctx.Builder.BuildSelect(cmp, next, prev, $"r8_idx{ddd}_st");
            ctx.Builder.BuildStore(stored, ptr);
        }

        // Compute flag candidates from a single representative ddd value via
        // the same select chain — but for simplicity here we just stamp Z/N/H
        // based on the ddd-selected result. Use field-driven select to pick
        // which stored byte to use for Z computation.
        // (Approach: re-derive next-value via select chain from the just-stored slots.)
        var values = new LLVMValueRef[8];
        for (int ddd = 0; ddd < 8; ddd++)
        {
            int gprIdx = Lr35902Emitters.Sss3BitToGprIndex(ddd);
            if (gprIdx < 0)
            {
                values[ddd] = LLVMValueRef.CreateConstInt(i8, 0, false);
                continue;
            }
            var ptr = ctx.Layout.GepGpr(ctx.Builder, ctx.StatePtr, gprIdx);
            values[ddd] = ctx.Builder.BuildLoad2(i8, ptr, $"r8_for_flags_{ddd}");
        }
        var newVal = values[7];
        for (int ddd = 6; ddd >= 0; ddd--)
        {
            var cmp = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, field32,
                LLVMValueRef.CreateConstInt(i32, (uint)ddd, false), $"f_is_ddd{ddd}");
            newVal = ctx.Builder.BuildSelect(cmp, values[ddd], newVal, $"f_sel{ddd}");
        }

        var z = ctx.Builder.BuildZExt(
                    ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, newVal,
                        LLVMValueRef.CreateConstInt(i8, 0, false), "z_cmp"),
                    i8, "z_i8");
        var n = LLVMValueRef.CreateConstInt(i8, _isInc ? 0u : 1u, false);
        // H for INC r: low nibble of newVal == 0 (i.e. carry from bit 3).
        // H for DEC r: low nibble of OLD value == 0 (i.e. borrow from bit 4).
        // We use newVal-derived H here; old-value would need additional book-keeping.
        // For INC: H = (newVal & 0xF) == 0
        // For DEC: H = (newVal & 0xF) == 0xF
        var lowNibble = ctx.Builder.BuildAnd(newVal, LLVMValueRef.CreateConstInt(i8, 0xF, false), "low_nib");
        var hCheck = _isInc
            ? LLVMValueRef.CreateConstInt(i8, 0x0, false)
            : LLVMValueRef.CreateConstInt(i8, 0xF, false);
        var h = ctx.Builder.BuildZExt(
                    ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, lowNibble, hCheck, "h_cmp"),
                    i8, "h_i8");

        // C preserved: read existing C and pass through.
        var cPreserved = Lr35902Emitters.ReadCarry(ctx);
        Lr35902Emitters.StoreFlags(ctx, z, n, h, cPreserved);
    }
}

// ---------------- 16-bit ops on dd-field pairs ----------------

/// <summary>
/// lr35902_write_rr_dd — LD rr,nn dispatch by 2-bit dd field.
/// dd: 00=BC, 01=DE, 10=HL, 11=SP. Value comes from imm16 step earlier.
/// </summary>
internal sealed class Lr35902WriteRrDdEmitter : IMicroOpEmitter
{
    public string OpName => "lr35902_write_rr_dd";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var fieldName = step.Raw.GetProperty("field").GetString()!;
        var valueName = step.Raw.GetProperty("value").GetString()!;
        var field32 = ctx.Resolve(fieldName);
        var raw = ctx.Resolve(valueName);

        // Normalise to i16.
        var i16 = LLVMTypeRef.Int16;
        LLVMValueRef value16 = raw.TypeOf.IntWidth == 16 ? raw
            : raw.TypeOf.IntWidth < 16 ? ctx.Builder.BuildZExt(raw, i16, $"{valueName}_z16")
                                        : ctx.Builder.BuildTrunc(raw, i16, $"{valueName}_t16");

        // For each dd value, conditionally write to the corresponding pair/reg.
        for (int dd = 0; dd < 4; dd++)
        {
            var name = Lr35902Emitters.Dd2BitToRrName(dd);
            var pair = Lr35902Emitters.LocateRegisterPair(ctx, name);

            var cmp = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, field32,
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (uint)dd, false), $"is_dd{dd}");

            if (pair is not null)
            {
                // Emulate conditional decompose by reading current, selecting,
                // then decomposing with the chosen value.
                var current = Lr35902Emitters.ComposePairValue(ctx, pair, $"{name}_cur");
                var chosen = ctx.Builder.BuildSelect(cmp, value16, current, $"{name}_chosen");
                Lr35902Emitters.DecomposePairValue(ctx, pair, chosen);
            }
            else
            {
                var (ptr, _) = Lr35902Emitters.LocateRegister(ctx, name);
                var prev = ctx.Builder.BuildLoad2(i16, ptr, $"{name}_prev");
                var chosen = ctx.Builder.BuildSelect(cmp, value16, prev, $"{name}_chosen");
                ctx.Builder.BuildStore(chosen, ptr);
            }
        }
    }
}

/// <summary>
/// lr35902_inc_rr_dd / lr35902_dec_rr_dd — 16-bit INC/DEC selected by
/// 2-bit dd field. Affects no flags (per LR35902 spec).
/// </summary>
internal sealed class Lr35902IncDecRrDdEmitter : IMicroOpEmitter
{
    public string OpName { get; }
    private readonly bool _isInc;

    public Lr35902IncDecRrDdEmitter(string opName, bool isInc)
    {
        OpName = opName;
        _isInc = isInc;
    }

    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var fieldName = step.Raw.GetProperty("field").GetString()!;
        var field32 = ctx.Resolve(fieldName);
        var i16 = LLVMTypeRef.Int16;
        var one = LLVMValueRef.CreateConstInt(i16, 1, false);

        for (int dd = 0; dd < 4; dd++)
        {
            var name = Lr35902Emitters.Dd2BitToRrName(dd);
            var pair = Lr35902Emitters.LocateRegisterPair(ctx, name);

            var cmp = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, field32,
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (uint)dd, false), $"is_dd{dd}");

            LLVMValueRef cur;
            if (pair is not null) cur = Lr35902Emitters.ComposePairValue(ctx, pair, $"{name}_cur");
            else
            {
                var (ptr, _) = Lr35902Emitters.LocateRegister(ctx, name);
                cur = ctx.Builder.BuildLoad2(i16, ptr, $"{name}_cur");
            }

            var next = _isInc
                ? ctx.Builder.BuildAdd(cur, one, $"{name}_next")
                : ctx.Builder.BuildSub(cur, one, $"{name}_next");
            var chosen = ctx.Builder.BuildSelect(cmp, next, cur, $"{name}_chosen");

            if (pair is not null) Lr35902Emitters.DecomposePairValue(ctx, pair, chosen);
            else
            {
                var (ptr, _) = Lr35902Emitters.LocateRegister(ctx, name);
                ctx.Builder.BuildStore(chosen, ptr);
            }
        }
    }
}

/// <summary>
/// lr35902_add_hl_rr — HL ← HL + rr_dd. N=0, H from bit 11→12 carry,
/// C from bit 15→16 carry, Z preserved.
/// </summary>
internal sealed class Lr35902AddHlRrEmitter : IMicroOpEmitter
{
    public string OpName => "lr35902_add_hl_rr";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var fieldName = step.Raw.GetProperty("field").GetString()!;
        var field32 = ctx.Resolve(fieldName);
        var i32 = LLVMTypeRef.Int32;
        var i16 = LLVMTypeRef.Int16;
        var i8 = LLVMTypeRef.Int8;

        // Read HL (i16).
        var hlPair = Lr35902Emitters.LocateRegisterPair(ctx, "HL")!;
        var hl = Lr35902Emitters.ComposePairValue(ctx, hlPair, "hl_cur");

        // Resolve rr_dd source (i16) via select chain.
        var srcs = new LLVMValueRef[4];
        for (int dd = 0; dd < 4; dd++)
        {
            var name = Lr35902Emitters.Dd2BitToRrName(dd);
            var pair = Lr35902Emitters.LocateRegisterPair(ctx, name);
            if (pair is not null) srcs[dd] = Lr35902Emitters.ComposePairValue(ctx, pair, $"src_{name}");
            else
            {
                var (ptr, _) = Lr35902Emitters.LocateRegister(ctx, name);
                srcs[dd] = ctx.Builder.BuildLoad2(i16, ptr, $"src_{name}");
            }
        }
        var rr = srcs[3];
        for (int dd = 2; dd >= 0; dd--)
        {
            var cmp = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, field32,
                LLVMValueRef.CreateConstInt(i32, (uint)dd, false), $"add_is_dd{dd}");
            rr = ctx.Builder.BuildSelect(cmp, srcs[dd], rr, $"add_src_sel{dd}");
        }

        // Add and derive flags via i32 widening.
        var hl32 = ctx.Builder.BuildZExt(hl, i32, "hl32");
        var rr32 = ctx.Builder.BuildZExt(rr, i32, "rr32");
        var sum32 = ctx.Builder.BuildAdd(hl32, rr32, "hl_plus_rr");

        // C from bit 16.
        var c = ctx.Builder.BuildZExt(
                    ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntUGT, sum32, ctx.ConstU32(0xFFFF), "c_cmp"),
                    i8, "c_i8");
        // H from bit 11→12: ((HL & 0x0FFF) + (rr & 0x0FFF)) > 0x0FFF
        var hlLow = ctx.Builder.BuildAnd(hl32, ctx.ConstU32(0x0FFF), "hl_low12");
        var rrLow = ctx.Builder.BuildAnd(rr32, ctx.ConstU32(0x0FFF), "rr_low12");
        var lowSum = ctx.Builder.BuildAdd(hlLow, rrLow, "low12_sum");
        var h = ctx.Builder.BuildZExt(
                    ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntUGT, lowSum, ctx.ConstU32(0x0FFF), "h_cmp"),
                    i8, "h_i8");

        // Store result back into HL.
        var newHl = ctx.Builder.BuildTrunc(sum32, i16, "new_hl");
        Lr35902Emitters.DecomposePairValue(ctx, hlPair, newHl);

        // Z preserved, N=0.
        var fPtr = ctx.Layout.GepStatusRegister(ctx.Builder, ctx.StatePtr, "F");
        var fOld = ctx.Builder.BuildLoad2(i8, fPtr, "f_old");
        var z = ctx.Builder.BuildAnd(
                    ctx.Builder.BuildLShr(fOld, LLVMValueRef.CreateConstInt(i8, 7, false), "z_sh"),
                    LLVMValueRef.CreateConstInt(i8, 1, false), "z_old");
        var n = LLVMValueRef.CreateConstInt(i8, 0, false);
        Lr35902Emitters.StoreFlags(ctx, z, n, h, c);
    }
}
