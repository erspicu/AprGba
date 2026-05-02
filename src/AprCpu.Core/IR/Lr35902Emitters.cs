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

        // HALT / IME / EI-delayed lower to extern host calls; JsonCpu
        // tracks the actual flags in C# state. Names of the externs
        // are in HostHelpers below.
        reg.Register(new HostFlagOpEmitter("halt",                 Lr35902HostHelpers.HaltExtern));
        reg.Register(new HostFlagOpEmitter("lr35902_ime_delayed",  Lr35902HostHelpers.ArmImeDelayedExtern));
        reg.Register(new HostImeEmitter());

        // STOP is treated like HALT (waits for IRQ). Real DMG also
        // disables LCD; we don't model that here.
        reg.Register(new HostFlagOpEmitter("stop", Lr35902HostHelpers.HaltExtern));

        // CB-prefix dispatch is host-runtime concern — the compiled
        // function for opcode 0xCB just signals via a no-op; the
        // executor intercepts and pivots to the CB instruction set.
        reg.Register(new SimpleNoOpEmitter("lr35902_cb_dispatch"));

        // DAA — full BCD adjust per LR35902 spec.
        reg.Register(new Lr35902DaaEmitter());

        // Immediate-byte reads: fetch from PC, advance PC by N. Lower
        // to memory_read_8 calls (read_imm16 issues two of them, low
        // byte first per LR35902 little-endian).
        reg.Register(new Lr35902ReadImm8Emitter());
        reg.Register(new Lr35902ReadImm16Emitter());

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

        // Memory-bus extern calls. Lower to memory_read_8 / memory_write_8 /
        // memory_write_16 calls into the host runtime (JsonCpu binds these
        // to GbMemoryBus shims). Address is widened to i32 to match the
        // existing extern signature shared with ARM.
        reg.Register(new Lr35902LoadByteEmitter());
        reg.Register(new Lr35902StoreByteEmitter());
        reg.Register(new Lr35902StoreWordEmitter());

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
        reg.Register(new Lr35902IncDecHlMemEmitter("lr35902_inc_hl_mem", isInc: true));
        reg.Register(new Lr35902IncDecHlMemEmitter("lr35902_dec_hl_mem", isInc: false));

        // CB-prefix (HL) variants — memory R-M-W siblings of cb_shift /
        // cb_bit / cb_res / cb_set. Spec splits the sss=110 case out of
        // the field-driven r-form so the memory ops only emit when needed.
        reg.Register(new Lr35902CbShiftHlMemEmitter());
        reg.Register(new Lr35902CbBitHlMemEmitter());
        reg.Register(new Lr35902CbResSetHlMemEmitter());

        // Wave 3: 16-bit register-pair operations selected by 2-bit dd field.
        // dd: 00=BC, 01=DE, 10=HL, 11=SP per LR35902 encoding.
        reg.Register(new Lr35902WriteRrDdEmitter());
        reg.Register(new Lr35902IncDecRrDdEmitter("lr35902_inc_rr_dd", isInc: true));
        reg.Register(new Lr35902IncDecRrDdEmitter("lr35902_dec_rr_dd", isInc: false));
        reg.Register(new Lr35902AddHlRrEmitter());

        // Wave 4: control flow. Direct PC writes (JP / JP cc / JR / JR cc /
        // RST) work today; CALL / RET / PUSH / POP also compile but their
        // memory-side effects are placeholder until the bus extern lands.
        reg.Register(new Lr35902JpEmitter());
        reg.Register(new Lr35902JpCcEmitter());
        reg.Register(new Lr35902JrEmitter());
        reg.Register(new Lr35902JrCcEmitter());
        reg.Register(new Lr35902RstEmitter());
        reg.Register(new Lr35902CallEmitter());
        reg.Register(new Lr35902CallCcEmitter());
        reg.Register(new Lr35902RetEmitter(handleReti: false));
        reg.Register(new Lr35902RetCcEmitter());

        // PUSH/POP qq dispatch — SP arithmetic + (placeholder) memory.
        reg.Register(new Lr35902PushQqEmitter());
        reg.Register(new Lr35902PopQqEmitter());

        // Wave 5: CB-prefix instructions. shift/rotate (RLC/RRC/RL/RR/
        // SLA/SRA/SWAP/SRL) all share the same scaffolding via the
        // shift_op string; BIT / RES / SET each get their own emitter.
        reg.Register(new Lr35902CbShiftEmitter());
        reg.Register(new Lr35902CbBitEmitter());
        reg.Register(new Lr35902CbResEmitter());
        reg.Register(new Lr35902CbSetEmitter());

        // Wave 5: stack arith + LDH IO ops. ADD SP,e8 / LD HL,SP+e8
        // need 8-bit signed immediate sign-extended to 16-bit, plus
        // funky H/C-from-low-byte flag rules.
        reg.Register(new Lr35902AddSpE8Emitter());
        reg.Register(new Lr35902LdHlSpE8Emitter());
        reg.Register(new Lr35902LdhIoLoadEmitter());
        reg.Register(new Lr35902LdhIoStoreEmitter());
    }

    /// <summary>
    /// LR35902 condition codes (cc field, 2 bits) → boolean predicate
    /// against the F register. Returns an i1 LLVM value.
    /// </summary>
    internal static LLVMValueRef EvalCondition(EmitContext ctx, string cond)
    {
        var fPtr = ctx.Layout.GepStatusRegister(ctx.Builder, ctx.StatePtr, "F");
        var f = ctx.Builder.BuildLoad2(LLVMTypeRef.Int8, fPtr, "f_cc");
        var zSh = ctx.Builder.BuildLShr(f, LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, 7, false), "z_sh");
        var zBit = ctx.Builder.BuildAnd(zSh, LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, 1, false), "z_b");
        var cSh = ctx.Builder.BuildLShr(f, LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, 4, false), "c_sh");
        var cBit = ctx.Builder.BuildAnd(cSh, LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, 1, false), "c_b");

        var zero = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, 0, false);
        var one  = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, 1, false);

        return cond switch
        {
            "NZ" => ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, zBit, zero, "is_nz"),
            "Z"  => ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, zBit, one,  "is_z"),
            "NC" => ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, cBit, zero, "is_nc"),
            "C"  => ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, cBit, one,  "is_c"),
            _    => throw new InvalidOperationException($"Unknown cc '{cond}' (expected NZ/Z/NC/C).")
        };
    }

    /// <summary>Get a pointer + element type for the PC status register (i16).</summary>
    internal static (LLVMValueRef Ptr, LLVMTypeRef ElemType) PcPointer(EmitContext ctx)
    {
        var ptr = ctx.Layout.GepStatusRegister(ctx.Builder, ctx.StatePtr, "PC");
        return (ptr, LLVMTypeRef.Int16);
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
            Lr35902Alu8Source.HlMemory  => LoadHlIndirect(ctx),
            Lr35902Alu8Source.Immediate => FetchImmediate(ctx),
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

    /// <summary>
    /// Load i8 from memory[HL]. Used by ALU op A,(HL) variants.
    /// </summary>
    private static LLVMValueRef LoadHlIndirect(EmitContext ctx)
    {
        var hlPair = Lr35902Emitters.LocateRegisterPair(ctx, "HL")!;
        var hl16 = Lr35902Emitters.ComposePairValue(ctx, hlPair, "alu_hl");
        var hl32 = ctx.Builder.BuildZExt(hl16, LLVMTypeRef.Int32, "alu_hl32");
        return Lr35902MemoryHelpers.CallRead8(ctx, hl32, "alu_hl_byte");
    }

    /// <summary>
    /// Fetch immediate i8 at PC, advance PC by 1. Used by ALU op A,n.
    /// (Equivalent to a synthesised read_imm8 inline — the spec's
    /// alu-imm8 group has no separate read_imm8 step, so the source
    /// fetch lives here.)
    /// </summary>
    private static LLVMValueRef FetchImmediate(EmitContext ctx)
    {
        var pcPtr = ctx.Layout.GepStatusRegister(ctx.Builder, ctx.StatePtr, "PC");
        var pc16 = ctx.Builder.BuildLoad2(LLVMTypeRef.Int16, pcPtr, "alu_pc");
        var pc32 = ctx.Builder.BuildZExt(pc16, LLVMTypeRef.Int32, "alu_pc32");
        var imm = Lr35902MemoryHelpers.CallRead8(ctx, pc32, "alu_imm");
        var newPc = ctx.Builder.BuildAdd(pc16,
            LLVMValueRef.CreateConstInt(LLVMTypeRef.Int16, 1, false), "alu_pc_after");
        ctx.Builder.BuildStore(newPc, pcPtr);
        return imm;
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

// ---------------- control flow (wave 4) ----------------

/// <summary>
/// lr35902_jp — unconditional PC ← address. Address comes from a
/// previous step (read_imm16 or read_reg_pair_named "HL").
/// </summary>
internal sealed class Lr35902JpEmitter : IMicroOpEmitter
{
    public string OpName => "lr35902_jp";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var addrName = step.Raw.GetProperty("address").GetString()!;
        var addr = NormaliseToI16(ctx, ctx.Resolve(addrName), $"{addrName}_jp");
        var (pcPtr, _) = Lr35902Emitters.PcPointer(ctx);
        ctx.Builder.BuildStore(addr, pcPtr);
    }

    internal static LLVMValueRef NormaliseToI16(EmitContext ctx, LLVMValueRef v, string label)
    {
        var w = v.TypeOf.IntWidth;
        if (w == 16) return v;
        if (w < 16)  return ctx.Builder.BuildZExt(v, LLVMTypeRef.Int16, $"{label}_z16");
        return ctx.Builder.BuildTrunc(v, LLVMTypeRef.Int16, $"{label}_t16");
    }
}

/// <summary>
/// lr35902_jp_cc — PC ← address if condition holds, else PC unchanged.
/// Implemented via select to keep the emitted IR a single basic block.
/// </summary>
internal sealed class Lr35902JpCcEmitter : IMicroOpEmitter
{
    public string OpName => "lr35902_jp_cc";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var addrName = step.Raw.GetProperty("address").GetString()!;
        var cond = step.Raw.GetProperty("cond").GetString()!;
        var addr = Lr35902JpEmitter.NormaliseToI16(ctx, ctx.Resolve(addrName), $"{addrName}_jpcc");
        var (pcPtr, _) = Lr35902Emitters.PcPointer(ctx);
        var curPc = ctx.Builder.BuildLoad2(LLVMTypeRef.Int16, pcPtr, "pc_cur");
        var pred = Lr35902Emitters.EvalCondition(ctx, cond);
        var chosen = ctx.Builder.BuildSelect(pred, addr, curPc, $"pc_jpcc_{cond.ToLowerInvariant()}");
        ctx.Builder.BuildStore(chosen, pcPtr);
    }
}

/// <summary>
/// lr35902_jr — PC ← PC + signed_e8. The offset comes from a previous
/// read_imm8 step (currently a placeholder that returns 0, so JR
/// effectively no-ops at this stage; the IR shape is correct).
/// </summary>
internal sealed class Lr35902JrEmitter : IMicroOpEmitter
{
    public string OpName => "lr35902_jr";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var offsetName = step.Raw.GetProperty("offset").GetString()!;
        var off8 = ctx.Resolve(offsetName);
        // Sign-extend i8 → i16
        var off8AsI8 = off8.TypeOf.IntWidth == 8 ? off8
            : ctx.Builder.BuildTrunc(off8, LLVMTypeRef.Int8, $"{offsetName}_t8");
        var off16 = ctx.Builder.BuildSExt(off8AsI8, LLVMTypeRef.Int16, $"{offsetName}_sx16");

        var (pcPtr, _) = Lr35902Emitters.PcPointer(ctx);
        var pc = ctx.Builder.BuildLoad2(LLVMTypeRef.Int16, pcPtr, "pc_jr_cur");
        var newPc = ctx.Builder.BuildAdd(pc, off16, "pc_jr_new");
        ctx.Builder.BuildStore(newPc, pcPtr);
    }
}

/// <summary>
/// lr35902_jr_cc — JR cc, e8. Same shape as JR but only writes back
/// the new PC when the condition holds.
/// </summary>
internal sealed class Lr35902JrCcEmitter : IMicroOpEmitter
{
    public string OpName => "lr35902_jr_cc";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var offsetName = step.Raw.GetProperty("offset").GetString()!;
        var cond = step.Raw.GetProperty("cond").GetString()!;
        var off8 = ctx.Resolve(offsetName);
        var off8AsI8 = off8.TypeOf.IntWidth == 8 ? off8
            : ctx.Builder.BuildTrunc(off8, LLVMTypeRef.Int8, $"{offsetName}_t8");
        var off16 = ctx.Builder.BuildSExt(off8AsI8, LLVMTypeRef.Int16, $"{offsetName}_sx16");

        var (pcPtr, _) = Lr35902Emitters.PcPointer(ctx);
        var pc = ctx.Builder.BuildLoad2(LLVMTypeRef.Int16, pcPtr, "pc_jrcc_cur");
        var newPc = ctx.Builder.BuildAdd(pc, off16, "pc_jrcc_new");
        var pred = Lr35902Emitters.EvalCondition(ctx, cond);
        var chosen = ctx.Builder.BuildSelect(pred, newPc, pc, $"pc_jrcc_{cond.ToLowerInvariant()}");
        ctx.Builder.BuildStore(chosen, pcPtr);
    }
}

/// <summary>
/// lr35902_rst — PC ← ttt × 8 (where ttt is 3-bit field). Should
/// also push the return address; that part is placeholder-stubbed
/// until the bus extern lands.
/// </summary>
internal sealed class Lr35902RstEmitter : IMicroOpEmitter
{
    public string OpName => "lr35902_rst";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var fieldName = step.Raw.GetProperty("field").GetString()!;
        var field32 = ctx.Resolve(fieldName);
        var field16 = ctx.Builder.BuildTrunc(field32, LLVMTypeRef.Int16, $"{fieldName}_t16");
        var addr = ctx.Builder.BuildShl(field16, LLVMValueRef.CreateConstInt(LLVMTypeRef.Int16, 3, false), "rst_addr");

        // Push current PC then jump.
        var (pcPtr, _) = Lr35902Emitters.PcPointer(ctx);
        var curPc = ctx.Builder.BuildLoad2(LLVMTypeRef.Int16, pcPtr, "pc_rst_cur");
        var sp = Lr35902RetEmitter.LoadSp(ctx, "rst_sp");
        Lr35902RetEmitter.WriteWordAtSpMinus2(ctx, sp, curPc, "rst_push");
        Lr35902CallEmitter.DecrementSp(ctx, 2);

        ctx.Builder.BuildStore(addr, pcPtr);
    }
}

/// <summary>
/// lr35902_call — Push current return PC, then PC ← address.
/// The push half is currently a SP-decrement-only stub since the bus
/// extern isn't wired; the PC update is real.
/// </summary>
internal sealed class Lr35902CallEmitter : IMicroOpEmitter
{
    public string OpName => "lr35902_call";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var addrName = step.Raw.GetProperty("address").GetString()!;
        var addr = Lr35902JpEmitter.NormaliseToI16(ctx, ctx.Resolve(addrName), $"{addrName}_call");

        // Push current PC (which is the return address — read_imm16 has
        // already advanced PC past the operand bytes).
        var (pcPtr, _) = Lr35902Emitters.PcPointer(ctx);
        var curPc = ctx.Builder.BuildLoad2(LLVMTypeRef.Int16, pcPtr, "pc_call_cur");
        var sp = Lr35902RetEmitter.LoadSp(ctx, "call_sp");
        Lr35902RetEmitter.WriteWordAtSpMinus2(ctx, sp, curPc, "call_push");
        DecrementSp(ctx, 2);

        ctx.Builder.BuildStore(addr, pcPtr);
    }

    internal static void DecrementSp(EmitContext ctx, ushort by)
    {
        var spPtr = ctx.Layout.GepStatusRegister(ctx.Builder, ctx.StatePtr, "SP");
        var sp = ctx.Builder.BuildLoad2(LLVMTypeRef.Int16, spPtr, "sp_pre");
        var newSp = ctx.Builder.BuildSub(sp, LLVMValueRef.CreateConstInt(LLVMTypeRef.Int16, by, false), $"sp_minus_{by}");
        ctx.Builder.BuildStore(newSp, spPtr);
    }

    internal static void IncrementSp(EmitContext ctx, ushort by)
    {
        var spPtr = ctx.Layout.GepStatusRegister(ctx.Builder, ctx.StatePtr, "SP");
        var sp = ctx.Builder.BuildLoad2(LLVMTypeRef.Int16, spPtr, "sp_pre");
        var newSp = ctx.Builder.BuildAdd(sp, LLVMValueRef.CreateConstInt(LLVMTypeRef.Int16, by, false), $"sp_plus_{by}");
        ctx.Builder.BuildStore(newSp, spPtr);
    }
}

/// <summary>lr35902_call_cc — conditional CALL via select.</summary>
internal sealed class Lr35902CallCcEmitter : IMicroOpEmitter
{
    public string OpName => "lr35902_call_cc";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var addrName = step.Raw.GetProperty("address").GetString()!;
        var cond = step.Raw.GetProperty("cond").GetString()!;
        var addr = Lr35902JpEmitter.NormaliseToI16(ctx, ctx.Resolve(addrName), $"{addrName}_callcc");
        var pred = Lr35902Emitters.EvalCondition(ctx, cond);

        // Push current PC unconditionally to the stack location SP-2..SP-1
        // (only effective when SP is also decremented). On condition-false
        // we leave SP alone, so the bytes we wrote sit harmlessly above SP.
        var (pcPtr, _) = Lr35902Emitters.PcPointer(ctx);
        var curPc = ctx.Builder.BuildLoad2(LLVMTypeRef.Int16, pcPtr, "pc_callcc_cur");
        var sp = Lr35902RetEmitter.LoadSp(ctx, "callcc_sp");
        Lr35902RetEmitter.WriteWordAtSpMinus2(ctx, sp, curPc, "callcc_push");

        var chosen = ctx.Builder.BuildSelect(pred, addr, curPc, $"pc_callcc_{cond.ToLowerInvariant()}");
        ctx.Builder.BuildStore(chosen, pcPtr);

        var newSp = ctx.Builder.BuildSub(sp, LLVMValueRef.CreateConstInt(LLVMTypeRef.Int16, 2, false), "sp_callcc_dec");
        var spChosen = ctx.Builder.BuildSelect(pred, newSp, sp, "sp_callcc_chosen");
        var spPtr = ctx.Layout.GepStatusRegister(ctx.Builder, ctx.StatePtr, "SP");
        ctx.Builder.BuildStore(spChosen, spPtr);
    }
}

/// <summary>
/// lr35902_ret — pop PC from stack. Memory pop is placeholder (PC
/// stays unchanged); SP gets +2.
/// </summary>
internal sealed class Lr35902RetEmitter : IMicroOpEmitter
{
    public string OpName => "lr35902_ret";
    private readonly bool _handleReti;

    public Lr35902RetEmitter(bool handleReti) { _handleReti = handleReti; }

    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var (pcPtr, _) = Lr35902Emitters.PcPointer(ctx);
        var sp = LoadSp(ctx, "ret_sp");
        var newPc = ReadWordAtSp(ctx, sp, "ret_target");
        ctx.Builder.BuildStore(newPc, pcPtr);
        Lr35902CallEmitter.IncrementSp(ctx, 2);
    }

    internal static LLVMValueRef LoadSp(EmitContext ctx, string label)
    {
        var spPtr = ctx.Layout.GepStatusRegister(ctx.Builder, ctx.StatePtr, "SP");
        return ctx.Builder.BuildLoad2(LLVMTypeRef.Int16, spPtr, label);
    }

    /// <summary>Compose i16 from memory[sp..sp+1] little-endian.</summary>
    internal static LLVMValueRef ReadWordAtSp(EmitContext ctx, LLVMValueRef sp16, string label)
    {
        var i16 = LLVMTypeRef.Int16;
        var i32 = LLVMTypeRef.Int32;
        var sp32Lo = ctx.Builder.BuildZExt(sp16, i32, $"{label}_sp32lo");
        var lo = Lr35902MemoryHelpers.CallRead8(ctx, sp32Lo, $"{label}_lo");
        var spPlus1 = ctx.Builder.BuildAdd(sp16,
            LLVMValueRef.CreateConstInt(i16, 1, false), $"{label}_sp_p1");
        var sp32Hi = ctx.Builder.BuildZExt(spPlus1, i32, $"{label}_sp32hi");
        var hi = Lr35902MemoryHelpers.CallRead8(ctx, sp32Hi, $"{label}_hi");

        var loZ = ctx.Builder.BuildZExt(lo, i16, $"{label}_loz");
        var hiZ = ctx.Builder.BuildZExt(hi, i16, $"{label}_hiz");
        var hiSh = ctx.Builder.BuildShl(hiZ,
            LLVMValueRef.CreateConstInt(i16, 8, false), $"{label}_hi_shl");
        return ctx.Builder.BuildOr(hiSh, loZ, label);
    }

    /// <summary>Push i16 to memory[sp-2..sp-1] little-endian.</summary>
    internal static void WriteWordAtSpMinus2(EmitContext ctx, LLVMValueRef sp16, LLVMValueRef value16, string label)
    {
        var i16 = LLVMTypeRef.Int16;
        var i32 = LLVMTypeRef.Int32;
        var spM2 = ctx.Builder.BuildSub(sp16,
            LLVMValueRef.CreateConstInt(i16, 2, false), $"{label}_sp_m2");
        var spM1 = ctx.Builder.BuildSub(sp16,
            LLVMValueRef.CreateConstInt(i16, 1, false), $"{label}_sp_m1");

        var loByte = ctx.Builder.BuildTrunc(value16, LLVMTypeRef.Int8, $"{label}_lo");
        var hiByte = ctx.Builder.BuildTrunc(
            ctx.Builder.BuildLShr(value16,
                LLVMValueRef.CreateConstInt(i16, 8, false), $"{label}_hi_shr"),
            LLVMTypeRef.Int8, $"{label}_hi");

        Lr35902MemoryHelpers.CallWrite8(ctx,
            ctx.Builder.BuildZExt(spM2, i32, $"{label}_addrlo"), loByte);
        Lr35902MemoryHelpers.CallWrite8(ctx,
            ctx.Builder.BuildZExt(spM1, i32, $"{label}_addrhi"), hiByte);
    }
}

/// <summary>lr35902_ret_cc — conditional RET. PC ← pop only when cond holds.</summary>
internal sealed class Lr35902RetCcEmitter : IMicroOpEmitter
{
    public string OpName => "lr35902_ret_cc";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var cond = step.Raw.GetProperty("cond").GetString()!;
        var pred = Lr35902Emitters.EvalCondition(ctx, cond);

        // Always read the would-be popped PC (the load is harmless when
        // we don't actually return); select between it and the current
        // PC based on the condition.
        var (pcPtr, _) = Lr35902Emitters.PcPointer(ctx);
        var curPc = ctx.Builder.BuildLoad2(LLVMTypeRef.Int16, pcPtr, "pc_retcc_cur");
        var sp = Lr35902RetEmitter.LoadSp(ctx, "retcc_sp");
        var poppedPc = Lr35902RetEmitter.ReadWordAtSp(ctx, sp, "retcc_target");
        var newPc = ctx.Builder.BuildSelect(pred, poppedPc, curPc, $"pc_retcc_{cond.ToLowerInvariant()}");
        ctx.Builder.BuildStore(newPc, pcPtr);

        // SP += 2 only when the return is taken.
        var spPlus2 = ctx.Builder.BuildAdd(sp, LLVMValueRef.CreateConstInt(LLVMTypeRef.Int16, 2, false), "sp_retcc_inc");
        var chosenSp = ctx.Builder.BuildSelect(pred, spPlus2, sp, $"sp_retcc_{cond.ToLowerInvariant()}");
        var spPtr = ctx.Layout.GepStatusRegister(ctx.Builder, ctx.StatePtr, "SP");
        ctx.Builder.BuildStore(chosenSp, spPtr);
    }
}

/// <summary>
/// lr35902_push_qq — PUSH rr. Decrements SP by 2 and (placeholder)
/// stores the named pair to memory[SP..SP+1]. qq dispatch:
/// 00=BC, 01=DE, 10=HL, 11=AF.
/// </summary>
internal sealed class Lr35902PushQqEmitter : IMicroOpEmitter
{
    public string OpName => "lr35902_push_qq";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var fieldName = step.Raw.GetProperty("field").GetString()!;
        var field32 = ctx.Resolve(fieldName);
        var i32 = LLVMTypeRef.Int32;
        var i16 = LLVMTypeRef.Int16;

        // Compose all four pair values, select on qq, then push.
        var values = new LLVMValueRef[4];
        var qqNames = new[] { "BC", "DE", "HL", "AF" };
        for (int qq = 0; qq < 4; qq++)
        {
            var pair = Lr35902Emitters.LocateRegisterPair(ctx, qqNames[qq]);
            values[qq] = pair is not null
                ? Lr35902Emitters.ComposePairValue(ctx, pair, $"push_{qqNames[qq]}")
                : LLVMValueRef.CreateConstInt(i16, 0, false);
        }
        var picked = values[3];
        for (int qq = 2; qq >= 0; qq--)
        {
            var cmp = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, field32,
                LLVMValueRef.CreateConstInt(i32, (uint)qq, false), $"push_is_qq{qq}");
            picked = ctx.Builder.BuildSelect(cmp, values[qq], picked, $"push_pick_qq{qq}");
        }

        // Write picked to memory[SP-2..SP-1] then SP -= 2.
        var sp = Lr35902RetEmitter.LoadSp(ctx, "push_sp");
        Lr35902RetEmitter.WriteWordAtSpMinus2(ctx, sp, picked, "push");
        Lr35902CallEmitter.DecrementSp(ctx, 2);
    }
}

/// <summary>
/// lr35902_pop_qq — POP rr. Increments SP by 2 and (placeholder)
/// loads the named pair from memory[SP-2..SP-1]. POP AF must mask
/// F's low nibble to 0 — handled by DecomposePairValue.
/// </summary>
internal sealed class Lr35902PopQqEmitter : IMicroOpEmitter
{
    public string OpName => "lr35902_pop_qq";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var fieldName = step.Raw.GetProperty("field").GetString()!;
        var field32 = ctx.Resolve(fieldName);
        var i32 = LLVMTypeRef.Int32;

        // Read the popped value once.
        var sp = Lr35902RetEmitter.LoadSp(ctx, "pop_sp");
        var popped = Lr35902RetEmitter.ReadWordAtSp(ctx, sp, "pop_value");

        var qqNames = new[] { "BC", "DE", "HL", "AF" };
        for (int qq = 0; qq < 4; qq++)
        {
            var pair = Lr35902Emitters.LocateRegisterPair(ctx, qqNames[qq]);
            if (pair is null) continue;

            var current = Lr35902Emitters.ComposePairValue(ctx, pair, $"pop_{qqNames[qq]}_cur");
            var cmp = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, field32,
                LLVMValueRef.CreateConstInt(i32, (uint)qq, false), $"pop_is_qq{qq}");
            var chosen = ctx.Builder.BuildSelect(cmp, popped, current, $"pop_pick_qq{qq}");
            Lr35902Emitters.DecomposePairValue(ctx, pair, chosen);
        }

        Lr35902CallEmitter.IncrementSp(ctx, 2);
    }
}

// ---------------- CB-prefix wave 5 ----------------

/// <summary>
/// CB shift/rotate ops: RLC/RRC/RL/RR/SLA/SRA/SWAP/SRL on a register
/// selected by 3-bit sss field. Z set from result, N=H=0, C from the
/// bit that fell out (or 0 for SWAP which has C=0).
/// </summary>
internal sealed class Lr35902CbShiftEmitter : IMicroOpEmitter
{
    public string OpName => "lr35902_cb_shift";

    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var shiftOp   = step.Raw.GetProperty("shift_op").GetString()!;
        var fieldName = step.Raw.GetProperty("field").GetString()!;
        var i8 = LLVMTypeRef.Int8;

        // Read source value via field-driven select chain (same shape
        // as lr35902_read_r8). (HL) variant reads placeholder 0.
        var field32 = ctx.Resolve(fieldName);
        var srcs = new LLVMValueRef[8];
        for (int sss = 0; sss < 8; sss++)
        {
            int gprIdx = Lr35902Emitters.Sss3BitToGprIndex(sss);
            srcs[sss] = gprIdx < 0
                ? LLVMValueRef.CreateConstInt(i8, 0, false)
                : ctx.Builder.BuildLoad2(i8, ctx.Layout.GepGpr(ctx.Builder, ctx.StatePtr, gprIdx), $"cb_src_{gprIdx}");
        }
        var src = srcs[7];
        for (int sss = 6; sss >= 0; sss--)
        {
            var cmp = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, field32,
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (uint)sss, false), $"cb_is_sss{sss}");
            src = ctx.Builder.BuildSelect(cmp, srcs[sss], src, $"cb_src_sel{sss}");
        }

        var cIn = Lr35902Emitters.ReadCarry(ctx);

        // Compute new value + new C bit.
        var (result, cOut) = ComputeShift(ctx, shiftOp, src, cIn);

        // Write back into the selected GPR via merge stores (skip (HL)).
        for (int sss = 0; sss < 8; sss++)
        {
            int gprIdx = Lr35902Emitters.Sss3BitToGprIndex(sss);
            if (gprIdx < 0) continue;
            var ptr = ctx.Layout.GepGpr(ctx.Builder, ctx.StatePtr, gprIdx);
            var prev = ctx.Builder.BuildLoad2(i8, ptr, $"cb_w_prev_{gprIdx}");
            var cmp = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, field32,
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (uint)sss, false), $"cb_w_is{sss}");
            var merged = ctx.Builder.BuildSelect(cmp, result, prev, $"cb_w_merge_{gprIdx}");
            ctx.Builder.BuildStore(merged, ptr);
        }

        // Z = (result == 0)
        var z = ctx.Builder.BuildZExt(
                    ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, result,
                        LLVMValueRef.CreateConstInt(i8, 0, false), "cb_z_cmp"),
                    i8, "cb_z");
        var n = LLVMValueRef.CreateConstInt(i8, 0, false);
        var h = LLVMValueRef.CreateConstInt(i8, 0, false);
        Lr35902Emitters.StoreFlags(ctx, z, n, h, cOut);
    }

    private static (LLVMValueRef result, LLVMValueRef cOut) ComputeShift(
        EmitContext ctx, string op, LLVMValueRef src, LLVMValueRef cIn)
    {
        var i8 = LLVMTypeRef.Int8;
        var one  = LLVMValueRef.CreateConstInt(i8, 1, false);
        var seven = LLVMValueRef.CreateConstInt(i8, 7, false);
        var top = ctx.Builder.BuildAnd(
                    ctx.Builder.BuildLShr(src, seven, "cb_top_sh"),
                    one, "cb_top");
        var bot = ctx.Builder.BuildAnd(src, one, "cb_bot");

        switch (op)
        {
            case "rlc":
            {
                var r = ctx.Builder.BuildOr(
                            ctx.Builder.BuildShl(src, one, "rlc_shl"),
                            top, "rlc_or");
                return (r, top);
            }
            case "rrc":
            {
                var r = ctx.Builder.BuildOr(
                            ctx.Builder.BuildLShr(src, one, "rrc_shr"),
                            ctx.Builder.BuildShl(bot, seven, "rrc_top"),
                            "rrc_or");
                return (r, bot);
            }
            case "rl":
            {
                var r = ctx.Builder.BuildOr(
                            ctx.Builder.BuildShl(src, one, "rl_shl"),
                            cIn, "rl_or");
                return (r, top);
            }
            case "rr":
            {
                var r = ctx.Builder.BuildOr(
                            ctx.Builder.BuildLShr(src, one, "rr_shr"),
                            ctx.Builder.BuildShl(cIn, seven, "rr_cin_top"),
                            "rr_or");
                return (r, bot);
            }
            case "sla":
            {
                var r = ctx.Builder.BuildShl(src, one, "sla_shl");
                return (r, top);
            }
            case "sra":
            {
                // Arithmetic shift right preserves bit 7.
                var r = ctx.Builder.BuildAShr(src, one, "sra_ashr");
                return (r, bot);
            }
            case "srl":
            {
                var r = ctx.Builder.BuildLShr(src, one, "srl_shr");
                return (r, bot);
            }
            case "swap":
            {
                var four = LLVMValueRef.CreateConstInt(i8, 4, false);
                var hi = ctx.Builder.BuildShl(src, four, "swap_hi");
                var lo = ctx.Builder.BuildLShr(src, four, "swap_lo");
                var r  = ctx.Builder.BuildOr(hi, lo, "swap_or");
                return (r, LLVMValueRef.CreateConstInt(i8, 0, false));
            }
            default:
                throw new InvalidOperationException($"lr35902_cb_shift: unknown shift_op '{op}'.");
        }
    }
}

/// <summary>
/// CB BIT b,r — Z ← !(r >> b & 1), N=0, H=1, C unchanged.
/// b comes from the bbb 3-bit field; r from sss.
/// </summary>
internal sealed class Lr35902CbBitEmitter : IMicroOpEmitter
{
    public string OpName => "lr35902_cb_bit";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var bitFieldName = step.Raw.GetProperty("bit_field").GetString()!;
        var srcFieldName = step.Raw.GetProperty("src_field").GetString()!;
        var i8 = LLVMTypeRef.Int8;
        var i32 = LLVMTypeRef.Int32;

        // Read source value via select chain.
        var srcField = ctx.Resolve(srcFieldName);
        var srcs = new LLVMValueRef[8];
        for (int sss = 0; sss < 8; sss++)
        {
            int gprIdx = Lr35902Emitters.Sss3BitToGprIndex(sss);
            srcs[sss] = gprIdx < 0
                ? LLVMValueRef.CreateConstInt(i8, 0, false)
                : ctx.Builder.BuildLoad2(i8, ctx.Layout.GepGpr(ctx.Builder, ctx.StatePtr, gprIdx), $"bit_src_{gprIdx}");
        }
        var src = srcs[7];
        for (int sss = 6; sss >= 0; sss--)
        {
            var cmp = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, srcField,
                LLVMValueRef.CreateConstInt(i32, (uint)sss, false), $"bit_is_sss{sss}");
            src = ctx.Builder.BuildSelect(cmp, srcs[sss], src, $"bit_src_sel{sss}");
        }

        // shifted = src >> bbb_field (use i8-truncated bbb).
        var bitField = ctx.Resolve(bitFieldName);
        var bbb8 = ctx.Builder.BuildTrunc(bitField, i8, "bbb_t8");
        var shifted = ctx.Builder.BuildLShr(src, bbb8, "bit_shr");
        var bit = ctx.Builder.BuildAnd(shifted, LLVMValueRef.CreateConstInt(i8, 1, false), "bit_pick");

        // Z = (bit == 0)
        var z = ctx.Builder.BuildZExt(
                    ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, bit,
                        LLVMValueRef.CreateConstInt(i8, 0, false), "bit_z_cmp"),
                    i8, "bit_z_i8");

        // N=0, H=1, C preserved.
        var n = LLVMValueRef.CreateConstInt(i8, 0, false);
        var h = LLVMValueRef.CreateConstInt(i8, 1, false);
        var c = Lr35902Emitters.ReadCarry(ctx);
        Lr35902Emitters.StoreFlags(ctx, z, n, h, c);
    }
}

/// <summary>
/// CB RES b,r — r ← r &amp; ~(1 &lt;&lt; b). Flags unchanged.
/// </summary>
internal sealed class Lr35902CbResEmitter : IMicroOpEmitter
{
    public string OpName => "lr35902_cb_res";
    public void Emit(EmitContext ctx, MicroOpStep step) =>
        EmitBitwise(ctx, step, isSet: false);

    internal static void EmitBitwise(EmitContext ctx, MicroOpStep step, bool isSet)
    {
        var bitFieldName = step.Raw.GetProperty("bit_field").GetString()!;
        var srcFieldName = step.Raw.GetProperty("src_field").GetString()!;
        var i8 = LLVMTypeRef.Int8;
        var i32 = LLVMTypeRef.Int32;

        var bitField = ctx.Resolve(bitFieldName);
        var srcField = ctx.Resolve(srcFieldName);
        var bbb8 = ctx.Builder.BuildTrunc(bitField, i8, "bbb_t8");
        var mask = ctx.Builder.BuildShl(LLVMValueRef.CreateConstInt(i8, 1, false), bbb8, "bbb_mask");

        // For each sss 0..7, read prev, apply op, conditionally store back.
        for (int sss = 0; sss < 8; sss++)
        {
            int gprIdx = Lr35902Emitters.Sss3BitToGprIndex(sss);
            if (gprIdx < 0) continue;
            var ptr = ctx.Layout.GepGpr(ctx.Builder, ctx.StatePtr, gprIdx);
            var prev = ctx.Builder.BuildLoad2(i8, ptr, $"rs_prev_{gprIdx}");
            var modified = isSet
                ? ctx.Builder.BuildOr(prev, mask, $"set_or_{gprIdx}")
                : ctx.Builder.BuildAnd(prev,
                    ctx.Builder.BuildXor(mask, LLVMValueRef.CreateConstInt(i8, 0xFF, false), $"res_notmask_{gprIdx}"),
                    $"res_and_{gprIdx}");
            var cmp = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, srcField,
                LLVMValueRef.CreateConstInt(i32, (uint)sss, false), $"rs_is{sss}");
            var chosen = ctx.Builder.BuildSelect(cmp, modified, prev, $"rs_merge_{gprIdx}");
            ctx.Builder.BuildStore(chosen, ptr);
        }
        // Flags unchanged for RES/SET.
    }
}

/// <summary>CB SET b,r — r ← r | (1 &lt;&lt; b). Flags unchanged.</summary>
internal sealed class Lr35902CbSetEmitter : IMicroOpEmitter
{
    public string OpName => "lr35902_cb_set";
    public void Emit(EmitContext ctx, MicroOpStep step) =>
        Lr35902CbResEmitter.EmitBitwise(ctx, step, isSet: true);
}

// ---------------- stack arith (wave 5) ----------------

/// <summary>
/// ADD SP, e8 — SP ← SP + signed-extended 8-bit. Z=N=0; H from bit 3
/// of low-byte add; C from bit 7 (carry-out of low byte). The flag
/// rules use UNSIGNED arithmetic on the low bytes regardless of e8 sign.
/// </summary>
internal sealed class Lr35902AddSpE8Emitter : IMicroOpEmitter
{
    public string OpName => "lr35902_add_sp_e8";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var offsetName = step.Raw.GetProperty("offset").GetString()!;
        var i8 = LLVMTypeRef.Int8;
        var i16 = LLVMTypeRef.Int16;
        var i32 = LLVMTypeRef.Int32;

        var off8 = ctx.Resolve(offsetName);
        var off8AsI8 = off8.TypeOf.IntWidth == 8 ? off8
            : ctx.Builder.BuildTrunc(off8, i8, $"{offsetName}_t8");
        var off16 = ctx.Builder.BuildSExt(off8AsI8, i16, $"{offsetName}_sx16");

        var (spPtr, _) = (ctx.Layout.GepStatusRegister(ctx.Builder, ctx.StatePtr, "SP"), i16);
        var sp = ctx.Builder.BuildLoad2(i16, spPtr, "sp_pre");
        var newSp = ctx.Builder.BuildAdd(sp, off16, "sp_plus_e8");
        ctx.Builder.BuildStore(newSp, spPtr);

        // H/C derived from low-byte unsigned add of SP_lo + e8_unsigned.
        var spLo = ctx.Builder.BuildAnd(
                       ctx.Builder.BuildZExt(sp, i32, "sp_z32"),
                       ctx.ConstU32(0xFF), "sp_lo");
        var e8Lo = ctx.Builder.BuildAnd(
                       ctx.Builder.BuildZExt(off8AsI8, i32, "e8_z32"),
                       ctx.ConstU32(0xFF), "e8_lo");
        var loSum = ctx.Builder.BuildAdd(spLo, e8Lo, "lo_sum");
        var c = ctx.Builder.BuildZExt(
                    ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntUGT, loSum, ctx.ConstU32(0xFF), "c_cmp"),
                    i8, "c_i8");
        // H = ((SP & 0xF) + (e8 & 0xF)) > 0xF
        var spLoNib = ctx.Builder.BuildAnd(spLo, ctx.ConstU32(0xF), "sp_lo_nib");
        var e8LoNib = ctx.Builder.BuildAnd(e8Lo, ctx.ConstU32(0xF), "e8_lo_nib");
        var nibSum = ctx.Builder.BuildAdd(spLoNib, e8LoNib, "nib_sum");
        var h = ctx.Builder.BuildZExt(
                    ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntUGT, nibSum, ctx.ConstU32(0xF), "h_cmp"),
                    i8, "h_i8");

        var zero = LLVMValueRef.CreateConstInt(i8, 0, false);
        Lr35902Emitters.StoreFlags(ctx, zero, zero, h, c);
    }
}

// (Lr35902LdhIoLoadPlaceholder removed — replaced by the real
//  Lr35902LdhIoLoadEmitter at the end of this file.)

/// <summary>
/// LD HL, SP+e8 — HL ← SP + signed-extended 8-bit. Same flag rules as
/// ADD SP,e8 (Z=N=0; H/C from low-byte unsigned add).
/// </summary>
internal sealed class Lr35902LdHlSpE8Emitter : IMicroOpEmitter
{
    public string OpName => "lr35902_ld_hl_sp_e8";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var offsetName = step.Raw.GetProperty("offset").GetString()!;
        var i8 = LLVMTypeRef.Int8;
        var i16 = LLVMTypeRef.Int16;
        var i32 = LLVMTypeRef.Int32;

        var off8 = ctx.Resolve(offsetName);
        var off8AsI8 = off8.TypeOf.IntWidth == 8 ? off8
            : ctx.Builder.BuildTrunc(off8, i8, $"{offsetName}_t8");
        var off16 = ctx.Builder.BuildSExt(off8AsI8, i16, $"{offsetName}_sx16");

        var spPtr = ctx.Layout.GepStatusRegister(ctx.Builder, ctx.StatePtr, "SP");
        var sp = ctx.Builder.BuildLoad2(i16, spPtr, "sp_for_hl");
        var newHl = ctx.Builder.BuildAdd(sp, off16, "hl_from_sp");

        // Decompose into HL pair.
        var hlPair = Lr35902Emitters.LocateRegisterPair(ctx, "HL")!;
        Lr35902Emitters.DecomposePairValue(ctx, hlPair, newHl);

        // Same H/C derivation as ADD SP,e8.
        var spLo = ctx.Builder.BuildAnd(
                       ctx.Builder.BuildZExt(sp, i32, "sp_z32"),
                       ctx.ConstU32(0xFF), "sp_lo");
        var e8Lo = ctx.Builder.BuildAnd(
                       ctx.Builder.BuildZExt(off8AsI8, i32, "e8_z32"),
                       ctx.ConstU32(0xFF), "e8_lo");
        var loSum = ctx.Builder.BuildAdd(spLo, e8Lo, "lo_sum");
        var c = ctx.Builder.BuildZExt(
                    ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntUGT, loSum, ctx.ConstU32(0xFF), "c_cmp"),
                    i8, "c_i8");
        var spLoNib = ctx.Builder.BuildAnd(spLo, ctx.ConstU32(0xF), "sp_lo_nib");
        var e8LoNib = ctx.Builder.BuildAnd(e8Lo, ctx.ConstU32(0xF), "e8_lo_nib");
        var nibSum = ctx.Builder.BuildAdd(spLoNib, e8LoNib, "nib_sum");
        var h = ctx.Builder.BuildZExt(
                    ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntUGT, nibSum, ctx.ConstU32(0xF), "h_cmp"),
                    i8, "h_i8");

        var zero = LLVMValueRef.CreateConstInt(i8, 0, false);
        Lr35902Emitters.StoreFlags(ctx, zero, zero, h, c);
    }
}

// ---------------- memory-bus extern calls ----------------

/// <summary>
/// Helpers shared by the LR35902 memory ops — all of them lower to the
/// same memory_read_8 / memory_write_8 / memory_write_16 externs that
/// ARM uses, with i32 addresses.
/// </summary>
internal static class Lr35902MemoryHelpers
{
    public static LLVMValueRef CallRead8(EmitContext ctx, LLVMValueRef addrI32, string outLabel)
    {
        var (slot, fnType, ptrType) = MemoryEmitters.GetOrDeclareMemoryFunctionPointer(
            ctx.Module, MemoryEmitters.ExternFunctionNames.Read8, LLVMTypeRef.Int8, LLVMTypeRef.Int32);
        var fn = ctx.Builder.BuildLoad2(ptrType, slot, $"{outLabel}_fn");
        return ctx.Builder.BuildCall2(fnType, fn, new[] { addrI32 }, outLabel);
    }

    public static void CallWrite8(EmitContext ctx, LLVMValueRef addrI32, LLVMValueRef valueI8)
    {
        var (slot, fnType, ptrType) = MemoryEmitters.GetOrDeclareMemoryFunctionPointer(
            ctx.Module, MemoryEmitters.ExternFunctionNames.Write8,
            ctx.Module.Context.VoidType, LLVMTypeRef.Int32, LLVMTypeRef.Int8);
        var fn = ctx.Builder.BuildLoad2(ptrType, slot, "w8_fn");
        ctx.Builder.BuildCall2(fnType, fn, new[] { addrI32, valueI8 }, "");
    }

    public static void CallWrite16(EmitContext ctx, LLVMValueRef addrI32, LLVMValueRef valueI16)
    {
        var (slot, fnType, ptrType) = MemoryEmitters.GetOrDeclareMemoryFunctionPointer(
            ctx.Module, MemoryEmitters.ExternFunctionNames.Write16,
            ctx.Module.Context.VoidType, LLVMTypeRef.Int32, LLVMTypeRef.Int16);
        var fn = ctx.Builder.BuildLoad2(ptrType, slot, "w16_fn");
        ctx.Builder.BuildCall2(fnType, fn, new[] { addrI32, valueI16 }, "");
    }

    public static LLVMValueRef AddrToI32(EmitContext ctx, LLVMValueRef addr, string label)
    {
        var w = addr.TypeOf.IntWidth;
        if (w == 32) return addr;
        if (w < 32)  return ctx.Builder.BuildZExt(addr, LLVMTypeRef.Int32, $"{label}_z32");
        return ctx.Builder.BuildTrunc(addr, LLVMTypeRef.Int32, $"{label}_t32");
    }
}

internal sealed class Lr35902LoadByteEmitter : IMicroOpEmitter
{
    public string OpName => "load_byte";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var addrName = step.Raw.GetProperty("address").GetString()!;
        var outName  = StandardEmitters.GetOut(step.Raw);
        var addr32 = Lr35902MemoryHelpers.AddrToI32(ctx, ctx.Resolve(addrName), addrName);
        ctx.Values[outName] = Lr35902MemoryHelpers.CallRead8(ctx, addr32, outName);
    }
}

internal sealed class Lr35902StoreByteEmitter : IMicroOpEmitter
{
    public string OpName => "store_byte";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var addrName  = step.Raw.GetProperty("address").GetString()!;
        var valueName = step.Raw.GetProperty("value").GetString()!;
        var addr32 = Lr35902MemoryHelpers.AddrToI32(ctx, ctx.Resolve(addrName), addrName);
        var raw    = ctx.Resolve(valueName);
        var v8 = raw.TypeOf.IntWidth == 8 ? raw
            : raw.TypeOf.IntWidth < 8
                ? ctx.Builder.BuildZExt(raw, LLVMTypeRef.Int8, $"{valueName}_z8")
                : ctx.Builder.BuildTrunc(raw, LLVMTypeRef.Int8, $"{valueName}_t8");
        Lr35902MemoryHelpers.CallWrite8(ctx, addr32, v8);
    }
}

internal sealed class Lr35902StoreWordEmitter : IMicroOpEmitter
{
    public string OpName => "store_word";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var addrName  = step.Raw.GetProperty("address").GetString()!;
        var valueName = step.Raw.GetProperty("value").GetString()!;
        var addr32 = Lr35902MemoryHelpers.AddrToI32(ctx, ctx.Resolve(addrName), addrName);
        var raw    = ctx.Resolve(valueName);
        var v16 = raw.TypeOf.IntWidth == 16 ? raw
            : raw.TypeOf.IntWidth < 16
                ? ctx.Builder.BuildZExt(raw, LLVMTypeRef.Int16, $"{valueName}_z16")
                : ctx.Builder.BuildTrunc(raw, LLVMTypeRef.Int16, $"{valueName}_t16");
        Lr35902MemoryHelpers.CallWrite16(ctx, addr32, v16);
    }
}

internal sealed class Lr35902ReadImm8Emitter : IMicroOpEmitter
{
    public string OpName => "read_imm8";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var outName = StandardEmitters.GetOut(step.Raw);
        var pcPtr = ctx.Layout.GepStatusRegister(ctx.Builder, ctx.StatePtr, "PC");
        var pc16 = ctx.Builder.BuildLoad2(LLVMTypeRef.Int16, pcPtr, "pc_for_imm8");
        var pc32 = ctx.Builder.BuildZExt(pc16, LLVMTypeRef.Int32, "pc_z32");
        var byteV = Lr35902MemoryHelpers.CallRead8(ctx, pc32, outName);
        var newPc = ctx.Builder.BuildAdd(pc16,
            LLVMValueRef.CreateConstInt(LLVMTypeRef.Int16, 1, false), "pc_after_imm8");
        ctx.Builder.BuildStore(newPc, pcPtr);
        ctx.Values[outName] = byteV;
    }
}

internal sealed class Lr35902ReadImm16Emitter : IMicroOpEmitter
{
    public string OpName => "read_imm16";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var outName = StandardEmitters.GetOut(step.Raw);
        var i16 = LLVMTypeRef.Int16;
        var i32 = LLVMTypeRef.Int32;

        var pcPtr = ctx.Layout.GepStatusRegister(ctx.Builder, ctx.StatePtr, "PC");
        var pc16 = ctx.Builder.BuildLoad2(i16, pcPtr, "pc_for_imm16");
        var pc32Lo = ctx.Builder.BuildZExt(pc16, i32, "pc_z32");
        var lo8 = Lr35902MemoryHelpers.CallRead8(ctx, pc32Lo, $"{outName}_lo");

        var pcPlus1 = ctx.Builder.BuildAdd(pc16,
            LLVMValueRef.CreateConstInt(i16, 1, false), "pc_plus1");
        var pc32Hi = ctx.Builder.BuildZExt(pcPlus1, i32, "pc_plus1_z32");
        var hi8 = Lr35902MemoryHelpers.CallRead8(ctx, pc32Hi, $"{outName}_hi");

        var loZ = ctx.Builder.BuildZExt(lo8, i16, $"{outName}_loz");
        var hiZ = ctx.Builder.BuildZExt(hi8, i16, $"{outName}_hiz");
        var hiSh = ctx.Builder.BuildShl(hiZ,
            LLVMValueRef.CreateConstInt(i16, 8, false), $"{outName}_hi_shl");
        var word = ctx.Builder.BuildOr(hiSh, loZ, outName);

        var newPc = ctx.Builder.BuildAdd(pc16,
            LLVMValueRef.CreateConstInt(i16, 2, false), "pc_after_imm16");
        ctx.Builder.BuildStore(newPc, pcPtr);

        ctx.Values[outName] = word;
    }
}

internal sealed class Lr35902LdhIoLoadEmitter : IMicroOpEmitter
{
    public string OpName => "lr35902_ldh_io_load";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var offsetName = step.Raw.GetProperty("offset").GetString()!;
        var outName = StandardEmitters.GetOut(step.Raw);
        var off = ctx.Resolve(offsetName);
        var off32 = Lr35902MemoryHelpers.AddrToI32(ctx, off, offsetName);
        var addr = ctx.Builder.BuildOr(off32, ctx.ConstU32(0xFF00), $"{outName}_addr");
        ctx.Values[outName] = Lr35902MemoryHelpers.CallRead8(ctx, addr, outName);
    }
}

internal sealed class Lr35902LdhIoStoreEmitter : IMicroOpEmitter
{
    public string OpName => "lr35902_ldh_io_store";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var offsetName = step.Raw.GetProperty("offset").GetString()!;
        var valueName  = step.Raw.GetProperty("value").GetString()!;
        var off = ctx.Resolve(offsetName);
        var off32 = Lr35902MemoryHelpers.AddrToI32(ctx, off, offsetName);
        var addr = ctx.Builder.BuildOr(off32, ctx.ConstU32(0xFF00), "ldh_addr");

        var raw = ctx.Resolve(valueName);
        var v8 = raw.TypeOf.IntWidth == 8 ? raw
            : raw.TypeOf.IntWidth < 8
                ? ctx.Builder.BuildZExt(raw, LLVMTypeRef.Int8, $"{valueName}_z8")
                : ctx.Builder.BuildTrunc(raw, LLVMTypeRef.Int8, $"{valueName}_t8");
        Lr35902MemoryHelpers.CallWrite8(ctx, addr, v8);
    }
}

// ---------------- host-side flag/IME externs ----------------

/// <summary>
/// Names of the LR35902-specific externs JsonCpu must bind. These
/// signal CPU-level flag changes (HALT, IME) that the host runtime
/// tracks outside the LLVM struct, since they affect dispatch decisions
/// (skip step on HALT, gate IRQ servicing on IME).
/// </summary>
public static class Lr35902HostHelpers
{
    public const string HaltExtern           = "host_lr35902_halt";
    public const string SetImeExtern         = "host_lr35902_set_ime";
    public const string ArmImeDelayedExtern  = "host_lr35902_arm_ime_delayed";

    public static (LLVMValueRef Slot, LLVMTypeRef FnType, LLVMTypeRef PtrType)
        GetVoidNoArgSlot(LLVMModuleRef module, string name)
    {
        var fnType  = LLVMTypeRef.CreateFunction(module.Context.VoidType, Array.Empty<LLVMTypeRef>());
        var ptrType = LLVMTypeRef.CreatePointer(fnType, 0);
        var existing = module.GetNamedGlobal(name);
        if (existing.Handle != IntPtr.Zero) return (existing, fnType, ptrType);
        var slot = module.AddGlobal(ptrType, name);
        slot.Linkage = LLVMLinkage.LLVMExternalLinkage;
        return (slot, fnType, ptrType);
    }

    public static (LLVMValueRef Slot, LLVMTypeRef FnType, LLVMTypeRef PtrType)
        GetVoidI8Slot(LLVMModuleRef module, string name)
    {
        var fnType  = LLVMTypeRef.CreateFunction(module.Context.VoidType, new[] { LLVMTypeRef.Int8 });
        var ptrType = LLVMTypeRef.CreatePointer(fnType, 0);
        var existing = module.GetNamedGlobal(name);
        if (existing.Handle != IntPtr.Zero) return (existing, fnType, ptrType);
        var slot = module.AddGlobal(ptrType, name);
        slot.Linkage = LLVMLinkage.LLVMExternalLinkage;
        return (slot, fnType, ptrType);
    }
}

internal sealed class HostFlagOpEmitter : IMicroOpEmitter
{
    public string OpName { get; }
    private readonly string _externName;

    public HostFlagOpEmitter(string opName, string externName)
    {
        OpName = opName;
        _externName = externName;
    }

    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var (slot, fnType, ptrType) = Lr35902HostHelpers.GetVoidNoArgSlot(ctx.Module, _externName);
        var fn = ctx.Builder.BuildLoad2(ptrType, slot, _externName + "_fn");
        ctx.Builder.BuildCall2(fnType, fn, Array.Empty<LLVMValueRef>(), "");
    }
}

internal sealed class HostImeEmitter : IMicroOpEmitter
{
    public string OpName => "lr35902_ime";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var v = (byte)step.Raw.GetProperty("value").GetInt32();
        var (slot, fnType, ptrType) = Lr35902HostHelpers.GetVoidI8Slot(ctx.Module, Lr35902HostHelpers.SetImeExtern);
        var fn = ctx.Builder.BuildLoad2(ptrType, slot, "set_ime_fn");
        var arg = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, v, false);
        ctx.Builder.BuildCall2(fnType, fn, new[] { arg }, "");
    }
}

// ---------------- DAA (BCD adjust) ----------------

internal sealed class Lr35902DaaEmitter : IMicroOpEmitter
{
    public string OpName => "lr35902_daa";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i8 = LLVMTypeRef.Int8;
        var i32 = LLVMTypeRef.Int32;

        var (aPtr, _) = Lr35902Emitters.LocateRegister(ctx, "A");
        var aOld = ctx.Builder.BuildLoad2(i8, aPtr, "a_pre_daa");

        var fPtr = ctx.Layout.GepStatusRegister(ctx.Builder, ctx.StatePtr, "F");
        var f = ctx.Builder.BuildLoad2(i8, fPtr, "f_pre_daa");
        var nBit = ctx.Builder.BuildAnd(
                       ctx.Builder.BuildLShr(f, LLVMValueRef.CreateConstInt(i8, 6, false), "f_n_sh"),
                       LLVMValueRef.CreateConstInt(i8, 1, false), "n_in");
        var hBit = ctx.Builder.BuildAnd(
                       ctx.Builder.BuildLShr(f, LLVMValueRef.CreateConstInt(i8, 5, false), "f_h_sh"),
                       LLVMValueRef.CreateConstInt(i8, 1, false), "h_in");
        var cBit = ctx.Builder.BuildAnd(
                       ctx.Builder.BuildLShr(f, LLVMValueRef.CreateConstInt(i8, 4, false), "f_c_sh"),
                       LLVMValueRef.CreateConstInt(i8, 1, false), "c_in");

        var aZ = ctx.Builder.BuildZExt(aOld, i32, "a_z");

        var nIsZero = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, nBit,
            LLVMValueRef.CreateConstInt(i8, 0, false), "n_eq_0");
        var cIsSet  = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, cBit,
            LLVMValueRef.CreateConstInt(i8, 1, false), "c_eq_1");
        var hIsSet  = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, hBit,
            LLVMValueRef.CreateConstInt(i8, 1, false), "h_eq_1");

        var aGt99 = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntUGT, aZ, ctx.ConstU32(0x99), "a_gt_99");
        var aLoGt9 = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntUGT,
            ctx.Builder.BuildAnd(aZ, ctx.ConstU32(0xF), "a_lo"),
            ctx.ConstU32(0x9), "alo_gt_9");

        var addNeed60 = ctx.Builder.BuildOr(cIsSet, aGt99,  "add_need60");
        var addNeed06 = ctx.Builder.BuildOr(hIsSet, aLoGt9, "add_need06");

        var add60 = ctx.Builder.BuildSelect(addNeed60, ctx.ConstU32(0x60), ctx.ConstU32(0), "add60");
        var add06 = ctx.Builder.BuildSelect(addNeed06, ctx.ConstU32(0x06), ctx.ConstU32(0), "add06");
        var sub60 = ctx.Builder.BuildSelect(cIsSet,    ctx.ConstU32(0x60), ctx.ConstU32(0), "sub60");
        var sub06 = ctx.Builder.BuildSelect(hIsSet,    ctx.ConstU32(0x06), ctx.ConstU32(0), "sub06");

        var addAddend = ctx.Builder.BuildOr(add60, add06, "add_addend");
        var subAddend = ctx.Builder.BuildOr(sub60, sub06, "sub_addend");

        var aAdded = ctx.Builder.BuildAdd(aZ, addAddend, "a_after_add");
        var aSubed = ctx.Builder.BuildSub(aZ, subAddend, "a_after_sub");

        var newA32 = ctx.Builder.BuildSelect(nIsZero, aAdded, aSubed, "a_after_daa32");
        var newA   = ctx.Builder.BuildTrunc(newA32, i8, "a_after_daa");
        ctx.Builder.BuildStore(newA, aPtr);

        var cAdd = ctx.Builder.BuildZExt(addNeed60, i8, "c_add_i8");
        var cKeep = cBit;
        var cOut = ctx.Builder.BuildSelect(nIsZero, cAdd, cKeep, "c_after_daa");

        var z = ctx.Builder.BuildZExt(
                    ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, newA,
                        LLVMValueRef.CreateConstInt(i8, 0, false), "daa_z_cmp"),
                    i8, "daa_z");
        var h = LLVMValueRef.CreateConstInt(i8, 0, false);
        Lr35902Emitters.StoreFlags(ctx, z, nBit, h, cOut);
    }
}

/// <summary>
/// INC/DEC (HL) memory R-M-W. Reads memory[HL], adjusts ±1, writes
/// back, updates Z/N/H per LR35902 (C preserved).
/// </summary>
internal sealed class Lr35902IncDecHlMemEmitter : IMicroOpEmitter
{
    public string OpName { get; }
    private readonly bool _isInc;

    public Lr35902IncDecHlMemEmitter(string opName, bool isInc)
    {
        OpName = opName;
        _isInc = isInc;
    }

    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i8 = LLVMTypeRef.Int8;
        var i32 = LLVMTypeRef.Int32;

        // Compute (HL) address.
        var hlPair = Lr35902Emitters.LocateRegisterPair(ctx, "HL")!;
        var hl16 = Lr35902Emitters.ComposePairValue(ctx, hlPair, "incdec_hl");
        var hl32 = ctx.Builder.BuildZExt(hl16, i32, "incdec_hl32");

        // Read, adjust, write back.
        var mem = Lr35902MemoryHelpers.CallRead8(ctx, hl32, "incdec_mem_old");
        var one = LLVMValueRef.CreateConstInt(i8, 1, false);
        var newVal = _isInc
            ? ctx.Builder.BuildAdd(mem, one, "incdec_mem_new")
            : ctx.Builder.BuildSub(mem, one, "incdec_mem_new");
        Lr35902MemoryHelpers.CallWrite8(ctx, hl32, newVal);

        // Z = (newVal == 0).
        var z = ctx.Builder.BuildZExt(
                    ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, newVal,
                        LLVMValueRef.CreateConstInt(i8, 0, false), "incdec_z_cmp"),
                    i8, "incdec_z");

        // H: INC → low nibble of newVal == 0 (carry from bit 3).
        //    DEC → low nibble of newVal == 0xF (borrow from bit 4).
        var lowNib = ctx.Builder.BuildAnd(newVal,
            LLVMValueRef.CreateConstInt(i8, 0xF, false), "incdec_lowm");
        var hCheck = _isInc
            ? LLVMValueRef.CreateConstInt(i8, 0x0, false)
            : LLVMValueRef.CreateConstInt(i8, 0xF, false);
        var h = ctx.Builder.BuildZExt(
                    ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, lowNib, hCheck, "incdec_h_cmp"),
                    i8, "incdec_h");

        var n = LLVMValueRef.CreateConstInt(i8, _isInc ? 0u : 1u, false);
        var c = Lr35902Emitters.ReadCarry(ctx);
        Lr35902Emitters.StoreFlags(ctx, z, n, h, c);
    }
}

// ---------------- CB-prefix (HL) memory variants ----------------

/// <summary>
/// CB shift/rotate on memory[HL]. Reads byte, shifts per shift_op,
/// writes back, sets Z/N/H/C the same way as the reg variant.
/// </summary>
internal sealed class Lr35902CbShiftHlMemEmitter : IMicroOpEmitter
{
    public string OpName => "lr35902_cb_shift_hl_mem";

    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var shiftOp = step.Raw.GetProperty("shift_op").GetString()!;
        var i8 = LLVMTypeRef.Int8;
        var i32 = LLVMTypeRef.Int32;

        var hlPair = Lr35902Emitters.LocateRegisterPair(ctx, "HL")!;
        var hl16 = Lr35902Emitters.ComposePairValue(ctx, hlPair, "cbhl");
        var hl32 = ctx.Builder.BuildZExt(hl16, i32, "cbhl32");

        var mem = Lr35902MemoryHelpers.CallRead8(ctx, hl32, "cb_mem_old");
        var cIn = Lr35902Emitters.ReadCarry(ctx);

        var (result, cOut) = ComputeShift(ctx, shiftOp, mem, cIn);
        Lr35902MemoryHelpers.CallWrite8(ctx, hl32, result);

        var z = ctx.Builder.BuildZExt(
                    ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, result,
                        LLVMValueRef.CreateConstInt(i8, 0, false), "cbhl_z_cmp"),
                    i8, "cbhl_z");
        var n = LLVMValueRef.CreateConstInt(i8, 0, false);
        var h = LLVMValueRef.CreateConstInt(i8, 0, false);
        Lr35902Emitters.StoreFlags(ctx, z, n, h, cOut);
    }

    /// <summary>Reuses the same arithmetic as the reg variant.</summary>
    private static (LLVMValueRef result, LLVMValueRef cOut) ComputeShift(
        EmitContext ctx, string op, LLVMValueRef src, LLVMValueRef cIn)
    {
        var i8 = LLVMTypeRef.Int8;
        var one = LLVMValueRef.CreateConstInt(i8, 1, false);
        var seven = LLVMValueRef.CreateConstInt(i8, 7, false);
        var top = ctx.Builder.BuildAnd(
                    ctx.Builder.BuildLShr(src, seven, "top_sh"),
                    one, "top");
        var bot = ctx.Builder.BuildAnd(src, one, "bot");

        return op switch
        {
            "rlc" => (
                ctx.Builder.BuildOr(ctx.Builder.BuildShl(src, one, "rlc_shl"), top, "rlc_or"),
                top),
            "rrc" => (
                ctx.Builder.BuildOr(
                    ctx.Builder.BuildLShr(src, one, "rrc_shr"),
                    ctx.Builder.BuildShl(bot, seven, "rrc_top"),
                    "rrc_or"),
                bot),
            "rl"  => (
                ctx.Builder.BuildOr(ctx.Builder.BuildShl(src, one, "rl_shl"), cIn, "rl_or"),
                top),
            "rr"  => (
                ctx.Builder.BuildOr(
                    ctx.Builder.BuildLShr(src, one, "rr_shr"),
                    ctx.Builder.BuildShl(cIn, seven, "rr_cin_top"),
                    "rr_or"),
                bot),
            "sla" => (ctx.Builder.BuildShl (src, one, "sla_shl"), top),
            "sra" => (ctx.Builder.BuildAShr(src, one, "sra_ashr"), bot),
            "srl" => (ctx.Builder.BuildLShr(src, one, "srl_shr"), bot),
            "swap" => (
                ctx.Builder.BuildOr(
                    ctx.Builder.BuildShl (src, LLVMValueRef.CreateConstInt(i8, 4, false), "swap_hi"),
                    ctx.Builder.BuildLShr(src, LLVMValueRef.CreateConstInt(i8, 4, false), "swap_lo"),
                    "swap_or"),
                LLVMValueRef.CreateConstInt(i8, 0, false)),
            _ => throw new InvalidOperationException($"cb_shift_hl_mem: unknown shift_op '{op}'.")
        };
    }
}

/// <summary>
/// BIT b, (HL) — Z = !(memory[HL] >> b & 1), N=0, H=1, C unchanged.
/// </summary>
internal sealed class Lr35902CbBitHlMemEmitter : IMicroOpEmitter
{
    public string OpName => "lr35902_cb_bit_hl_mem";

    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var bitFieldName = step.Raw.GetProperty("bit_field").GetString()!;
        var i8 = LLVMTypeRef.Int8;
        var i32 = LLVMTypeRef.Int32;

        var hlPair = Lr35902Emitters.LocateRegisterPair(ctx, "HL")!;
        var hl16 = Lr35902Emitters.ComposePairValue(ctx, hlPair, "bithl");
        var hl32 = ctx.Builder.BuildZExt(hl16, i32, "bithl32");
        var mem = Lr35902MemoryHelpers.CallRead8(ctx, hl32, "bithl_mem");

        var bitField = ctx.Resolve(bitFieldName);
        var bbb8 = ctx.Builder.BuildTrunc(bitField, i8, "bbb_t8");
        var bit = ctx.Builder.BuildAnd(
            ctx.Builder.BuildLShr(mem, bbb8, "bithl_shr"),
            LLVMValueRef.CreateConstInt(i8, 1, false), "bithl_pick");

        var z = ctx.Builder.BuildZExt(
                    ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, bit,
                        LLVMValueRef.CreateConstInt(i8, 0, false), "bithl_z_cmp"),
                    i8, "bithl_z");

        var n = LLVMValueRef.CreateConstInt(i8, 0, false);
        var h = LLVMValueRef.CreateConstInt(i8, 1, false);
        var c = Lr35902Emitters.ReadCarry(ctx);
        Lr35902Emitters.StoreFlags(ctx, z, n, h, c);
    }
}

/// <summary>
/// RES/SET b, (HL) — memory[HL] R-M-W setting / clearing bit b.
/// is_set distinguishes RES (false) from SET (true). Flags unchanged.
/// </summary>
internal sealed class Lr35902CbResSetHlMemEmitter : IMicroOpEmitter
{
    public string OpName => "lr35902_cb_resset_hl_mem";

    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var bitFieldName = step.Raw.GetProperty("bit_field").GetString()!;
        var isSet = step.Raw.GetProperty("is_set").GetBoolean();
        var i8 = LLVMTypeRef.Int8;
        var i32 = LLVMTypeRef.Int32;

        var hlPair = Lr35902Emitters.LocateRegisterPair(ctx, "HL")!;
        var hl16 = Lr35902Emitters.ComposePairValue(ctx, hlPair, "rshl");
        var hl32 = ctx.Builder.BuildZExt(hl16, i32, "rshl32");

        var bitField = ctx.Resolve(bitFieldName);
        var bbb8 = ctx.Builder.BuildTrunc(bitField, i8, "bbb_t8");
        var mask = ctx.Builder.BuildShl(LLVMValueRef.CreateConstInt(i8, 1, false), bbb8, "rshl_mask");

        var prev = Lr35902MemoryHelpers.CallRead8(ctx, hl32, "rshl_prev");
        LLVMValueRef result;
        if (isSet)
        {
            result = ctx.Builder.BuildOr(prev, mask, "rshl_set");
        }
        else
        {
            var notMask = ctx.Builder.BuildXor(mask,
                LLVMValueRef.CreateConstInt(i8, 0xFF, false), "rshl_notmask");
            result = ctx.Builder.BuildAnd(prev, notMask, "rshl_res");
        }
        Lr35902MemoryHelpers.CallWrite8(ctx, hl32, result);
    }
}
