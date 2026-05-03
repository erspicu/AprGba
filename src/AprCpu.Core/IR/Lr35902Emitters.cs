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
        // SCF / CCF / CPL migrated to generic ops (Phase 5.8 Step 5.7.A):
        // SCF: 3× set_flag (was already migrated in 5.2)
        // CCF: toggle_flag(C) + 2× set_flag
        // CPL: read_reg_named(A) + mvn (now width-aware) + write_reg_named(A) + 2× set_flag

        // HALT / IME / EI-delayed lower to extern host calls; JsonCpu
        // tracks the actual flags in C# state. Names of the externs
        // are in HostHelpers below.
        reg.Register(new HostFlagOpEmitter("halt",                 Lr35902HostHelpers.HaltExtern));
        reg.Register(new HostFlagOpEmitter("lr35902_ime_delayed",  Lr35902HostHelpers.ArmImeDelayedExtern));
        // Phase 7 GB block-JIT P0.6 — generic `defer` micro-op:
        //   - Per-instr backend: V1 routes action_id=0 to the existing
        //     LR35902 IME-delayed extern (matches current EI semantics).
        //     Action IDs >0 throw NotSupportedException pending V2.
        //   - Block-JIT backend: BlockFunctionBuilder runs DeferLowering
        //     pre-pass that strips the defer wrapper and inlines the body
        //     into a later instruction's steps; the emitter never gets
        //     called in block-JIT mode.
        reg.Register(new DeferEmitterPerInstr());
        reg.Register(new HostImeEmitter());

        // STOP is treated like HALT (waits for IRQ). Real DMG also
        // disables LCD; we don't model that here.
        reg.Register(new HostFlagOpEmitter("stop", Lr35902HostHelpers.HaltExtern));

        // CB-prefix dispatch is host-runtime concern — the compiled
        // function for opcode 0xCB just signals via a no-op; the
        // executor intercepts and pivots to the CB instruction set.
        // CB-prefix dispatch is now a spec-side empty-step instruction
        // (Phase 5.8 Step 5.6). The runtime sees the 0xCB opcode, the
        // entry function does nothing, then the next instruction is
        // looked up via the CB decoder table. No emitter needed.

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
        // RLCA / RRCA / RLA / RRA migrated to generic shift kind=rlc/rrc/rl/rr
        // (Phase 5.8 Step 5.4.B). The non-CB variants chain a set_flag(Z=0)
        // afterwards because they always clear Z (vs CB-rotates which set
        // Z = result == 0).

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
        // inc_pair / dec_pair migrated to generic
        // read_reg_pair_named + add/sub + write_reg_pair_named
        // (Phase 5.8 Step 5.7.B). Used by LD (HL+),A / LD A,(HL+) etc.

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
        // INC r / DEC r / INC (HL) / DEC (HL) migrated to generic chain
        // (Phase 5.8 Step 5.7.B): lr35902_read_r8 (or load_byte for HL)
        // + add/sub + trunc + lr35902_write_r8 (or store_byte) +
        // update_zero + set_flag(N) + update_h_inc/dec.

        // CB-prefix (HL) variants — memory R-M-W siblings of cb_shift /
        // cb_bit / cb_res / cb_set. Spec splits the sss=110 case out of
        // the field-driven r-form so the memory ops only emit when needed.
        // CB-prefix (HL)-mem variants migrated to generic ops (Phase 5.8
        // Step 5.4): read_reg_pair_named + load_byte + bit_test/set/clear
        // or shift kind=... + (store_byte for SET/RES/shift).

        // Wave 3: 16-bit register-pair operations selected by 2-bit dd field.
        // dd: 00=BC, 01=DE, 10=HL, 11=SP per LR35902 encoding.
        reg.Register(new Lr35902WriteRrDdEmitter());
        // INC rr / DEC rr (dd field-dispatched 16-bit) migrated to spec-side
        // selector variants — each dd value gets its own named-pair add/sub
        // chain. (Phase 5.8 Step 5.7.B/C cleanup.)
        reg.Register(new Lr35902AddHlRrEmitter());

        // Wave 4: control flow. Direct PC writes (JP / JP cc / JR / JR cc /
        // RST) work today; CALL / RET / PUSH / POP also compile but their
        // memory-side effects are placeholder until the bus extern lands.
        // Control flow (JP / JR / CALL / RET / RST + cc variants) is fully
        // handled by generic ops in Phase 5.8 Step 5.3:
        //   branch / branch_cc / call / call_cc / ret / ret_cc / read_pc / sext
        // (see StackOps + Emitters). The lr35902_jp/jr/call/ret/rst emitters
        // were deleted alongside the spec migration that removed all uses.

        // PUSH/POP qq dispatch — SP arithmetic + (placeholder) memory.
        // PUSH qq / POP qq are handled by generic push_pair / pop_pair
        // (Phase 5.8 Step 5.1, see StackOps).

        // Wave 5: CB-prefix instructions. shift/rotate (RLC/RRC/RL/RR/
        // SLA/SRA/SWAP/SRL) all share the same scaffolding via the
        // shift_op string; BIT / RES / SET each get their own emitter.
        // CB-prefix r-form variants migrated to generic ops (Phase 5.8
        // Step 5.4): lr35902_read_r8 + bit_test/set/clear or shift +
        // lr35902_write_r8 (with set_flag chains for the BIT N=0/H=1).

        // Wave 5: stack arith + LDH IO ops. ADD SP,e8 / LD HL,SP+e8
        // need 8-bit signed immediate sign-extended to 16-bit, plus
        // funky H/C-from-low-byte flag rules.
        reg.Register(new Lr35902AddSpE8Emitter());
        reg.Register(new Lr35902LdHlSpE8Emitter());
        // LDH (n)/(C)/A IO ops migrated to generic chain (Phase 5.8 Step 5.5):
        // read_imm8/read_reg_named + or with const 0xFF00 + load_byte/store_byte.
        // Binary's auto-coerce (i8 → i32 zext) handles the page composition.
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

// SCF / CCF / CPL emitters deleted in Phase 5.8 Step 5.7.A — see RegisterAll comment.

// ---------------- placeholder no-op emitters ----------------

/// <summary>
// SimpleNoOpEmitter deleted in Phase 5.8 Step 5.6 — its sole user
// (lr35902_cb_dispatch) was removed in favour of an empty step list
// in spec/lr35902/groups/block3-cb-prefix.json.

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

// A-rotate (RLCA/RRCA/RLA/RRA) deleted in Phase 5.8 Step 5.4.B cleanup —
// migrated to generic shift kind=rlc/rrc/rl/rr + set_flag(Z=0).

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

// ============================================================================
// L3 ARCHITECTURAL INTRINSICS — operand resolvers + ALU compound ops
//
// Everything from here through Lr35902LdHlSpE8Emitter is LR35902-specific
// because it encodes one of two arch-bound things:
//
//   (1) Operand resolvers: the sss / ddd / dd field → register table
//       mappings. LR35902's table (B=0, C=1, D=2, E=3, H=4, L=5, (HL)=6,
//       A=7) is part of the ISA encoding and doesn't generalise to ARM
//       (4-bit GPR field, 16 registers) or RISC-V (5-bit, 32 registers).
//       Generalising would mean a "operand resolver registry" in the
//       spec schema — left as future work pending a third CPU port.
//
//       Affects: lr35902_read_r8, lr35902_write_r8, lr35902_write_rr_dd
//
//   (2) Compound ALU + flag rules: 8-bit and 16-bit arithmetic with
//       LR35902-specific flag derivation (e.g., ADD HL,rr's H flag from
//       bit 11→12 carry, ADD SP,e8's H/C from low-byte unsigned add).
//       The math is generic but the flag-bit positions and rules are
//       arch-specific. Splitting into 10+ generic ops per ALU op would
//       inflate the spec significantly without obvious payoff before a
//       third CPU validates the abstraction.
//
//       Affects: lr35902_alu_a (the 3 source-mode emitters share the
//       same Lr35902Alu8Emitter class), lr35902_add_hl_rr,
//       lr35902_add_sp_e8, lr35902_ld_hl_sp_e8.
//
// Plus the bus-level helpers further down (load_byte / store_byte /
// store_word / read_imm8 / read_imm16) which use a generic-looking name
// but currently live in the LR35902 file because they wrap the LR35902
// memory-bus extern. After Step 5.8 these should move to a shared
// MemoryEmitters file.
// ============================================================================

// ---------------- field-driven r8 access (L3 — operand resolver) ----------------

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

// inc_pair / dec_pair (named-pair) deleted in Phase 5.8 Step 5.7.B cleanup —
// migrated to generic read_reg_pair_named + add/sub + write_reg_pair_named.

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
    ///
    /// Phase 7 GB block-JIT P0.5c — block-JIT mode bakes the immediate
    /// from the instruction-word constant (set by BlockDetector P0.1:
    /// 2-byte instr packs opcode | (imm8 &lt;&lt; 8)) without going through
    /// the bus or touching real PC. Same fix as
    /// <see cref="Lr35902ReadImm8Emitter"/> in P0.3 — applied here
    /// independently because this emitter has its own inline fetch
    /// rather than going through the read_imm8 micro-op. Without this
    /// fix, ALU A,n reads from PC=block_start_pc (Strategy 2 — real PC
    /// slot isn't updated per-instr), getting a wrong byte and
    /// computing wrong flags. CpuDiff caught this as F-register
    /// divergence on `CP A,n` in Blargg 01-special "JR negative".
    /// </summary>
    private static LLVMValueRef FetchImmediate(EmitContext ctx)
    {
        if (ctx.CurrentInstructionBaseAddress is not null)
        {
            // Block-JIT mode: extract imm8 from instruction word constant.
            var shifted = ctx.Builder.BuildLShr(ctx.Instruction,
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 8, false), "alu_imm_shr8");
            return ctx.Builder.BuildTrunc(shifted, LLVMTypeRef.Int8, "alu_imm");
        }

        // Per-instr fallback: walk PC + bus.ReadByte (legacy JsonCpu path).
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

// 8-bit INC r / DEC r emitters (lr35902_inc_r8 / dec_r8) deleted in Phase 5.8 Step 5.7.B
// cleanup — migrated to generic lr35902_read_r8 + add/sub + trunc + lr35902_write_r8 +
// update_zero + set_flag(N) + update_h_inc/dec.

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

// Lr35902IncDecRrDdEmitter deleted in Phase 5.8 Step 5.7.B/C cleanup —
// migrated to per-dd selector variants in spec/lr35902/groups/block0-alu-rr.json
// using the named-pair read+add/sub+write chain.

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

// ---------------- control flow lowered to generic ops (Phase 5.8 Step 5.3) ----------------
// ----------------   see StackOps + Emitters: branch / branch_cc / call / call_cc / ret / ret_cc / read_pc / sext

// ---------------- CB-prefix wave 5 ----------------

// CB-prefix r-form bit/shift emitters deleted in Phase 5.8 Step 5.4 cleanup —
// migrated to generic bit_test / bit_set / bit_clear / shift ops (BitOps.cs).

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
        // Phase 7 GB block-JIT P0.7 — block-JIT mode uses sync-flag write
        // variant. If sync flag returns 1 (IRQ-relevant address written),
        // block exits early so outer loop can deliver the IRQ at the
        // exact instruction boundary. Per-instr mode skips the sync
        // check (outer loop already polls IRQ between instructions).
        if (ctx.CurrentInstructionBaseAddress is not null)
        {
            EmitWriteByteWithSync(ctx, addr32, v8);
            return;
        }
        Lr35902MemoryHelpers.CallWrite8(ctx, addr32, v8);
    }

    /// <summary>
    /// Emit IR sequence: call memory_write_8_sync(addr, value) → if
    /// returned i8 == 1, set PcWritten=1 + write next-instr PC + ret void
    /// (block exits). Else fall through to next instr.
    /// Cost: 1 i8-compare + 1 branch with llvm.expect cold-path hint.
    /// </summary>
    internal static void EmitWriteByteWithSync(EmitContext ctx, LLVMValueRef addr32, LLVMValueRef v8)
    {
        var sync = MemoryEmitters.CallWrite8WithSync(ctx, addr32, v8, "w8s");
        var syncBool = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ,
            sync, LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, 1, false), "w8s_eq1");
        // V1 simple form: branch on raw sync flag (no llvm.expect.i1 hint
        // yet — LLVMSharp doesn't trivially expose the intrinsic; the
        // backend's profile-driven layout still puts the unlikely path
        // at the bottom even without explicit hint).
        var fn       = ctx.Function;
        var contBB   = fn.AppendBasicBlock("after_sync");
        var exitBB   = fn.AppendBasicBlock("sync_exit_block");
        ctx.Builder.BuildCondBr(syncBool, exitBB, contBB);

        // sync_exit_block: deduct cycles + write next-PC + PcWritten=1 +
        // ret void. Cycle deduction is critical: BlockFunctionBuilder's
        // Phase 1a budget downcount happens AFTER exec BB, so by sync-
        // exiting from inside exec we'd skip the deduction. Without it
        // host scheduler sees 0 cycles consumed → PPU/timer don't advance
        // → state diverges from per-instr (which always ticks N cycles
        // per StepOne via outer loop).
        ctx.Builder.PositionAtEnd(exitBB);
        if (ctx.CurrentInstructionCycleCost > 0)
        {
            var cyclesPtr = ctx.Layout.GepCyclesLeft(ctx.Builder, ctx.StatePtr);
            var cyclesOld = ctx.Builder.BuildLoad2(LLVMTypeRef.Int32, cyclesPtr, "sync_cycles_old");
            var cyclesNew = ctx.Builder.BuildSub(cyclesOld,
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (ulong)ctx.CurrentInstructionCycleCost, false),
                "sync_cycles_new");
            ctx.Builder.BuildStore(cyclesNew, cyclesPtr);
        }
        if (ctx.PipelinePcConstant is uint nextPc)
        {
            var (pcPtr, pcType) = StackOps.LocateProgramCounter(ctx);
            var nextPcConst = LLVMValueRef.CreateConstInt(pcType, nextPc, false);
            ctx.Builder.BuildStore(nextPcConst, pcPtr);
            var pcwSlot = ctx.Layout.GepPcWritten(ctx.Builder, ctx.StatePtr);
            ctx.Builder.BuildStore(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, 1, false), pcwSlot);
        }
        ctx.Builder.BuildRetVoid();

        // Continue path resumes the next instruction.
        ctx.Builder.PositionAtEnd(contBB);
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

        // Phase 7 GB block-JIT P0.3 — block-JIT mode bakes the immediate
        // byte from the instruction-word constant (set by BlockDetector
        // P0.1: 2-byte instr packs opcode | (imm8 << 8)). No bus call,
        // no PC walk — the BlockFunctionBuilder advances PC for the whole
        // block via Strategy 2 baked PC constants.
        if (ctx.CurrentInstructionBaseAddress is not null)
        {
            // imm8 = (instruction_word >> 8) & 0xFF, then trunc to i8
            var shifted = ctx.Builder.BuildLShr(ctx.Instruction,
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 8, false), $"{outName}_shr8");
            var byteI8 = ctx.Builder.BuildTrunc(shifted, LLVMTypeRef.Int8, outName);
            ctx.Values[outName] = byteI8;
            return;
        }

        // Per-instr fallback: walk PC + bus.ReadByte (legacy JsonCpu path).
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

        // Phase 7 GB block-JIT P0.3 — block-JIT mode bakes the imm16 from
        // the instruction-word constant (3-byte instr packs opcode |
        // (imm_lo << 8) | (imm_hi << 16)). Extract bits 8..23 and trunc
        // to i16 — LLVM constant-folds the whole chain.
        if (ctx.CurrentInstructionBaseAddress is not null)
        {
            var shifted = ctx.Builder.BuildLShr(ctx.Instruction,
                LLVMValueRef.CreateConstInt(i32, 8, false), $"{outName}_shr8");
            var w16 = ctx.Builder.BuildTrunc(shifted, i16, outName);
            ctx.Values[outName] = w16;
            return;
        }

        // Per-instr fallback: walk PC + 2 bus.ReadByte calls.
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

// LDH IO emitters deleted in Phase 5.8 Step 5.5 — see RegisterAll comment.

// ============================================================================
// L3 ARCHITECTURAL INTRINSICS (LR35902-only hardware quirks)
//
// Everything below this line stays LR35902-specific by design. These ops
// model behaviours that don't generalise across CPU families:
//
//   lr35902_ime / lr35902_ime_delayed  — IME master enable, with the
//     EI quirk: writes IME=1 only AFTER the next instruction completes.
//     Implemented via a host extern that maintains a per-CpuState delay
//     counter; the counter is then drained by the dispatcher loop.
//
//   halt / stop  — pause CPU until next IRQ. The exact wakeup conditions
//     differ per CPU (DMG halt-bug vs CGB; STOP+button on DMG). Modelled
//     as a host extern that sets a flag the dispatcher checks each step.
//
//   lr35902_daa  — BCD adjust of A. The 4×2 lookup-table semantics
//     (carry-in N/H/C bits → adjustment value + new C) are textbook
//     LR35902 (and Z80 / 8080) and don't have an obvious generic shape.
//
// These are the kind of "L3" ops the Phase 5.8 refactor doc identified
// as truly worth keeping arch-specific. Don't try to generalise them
// further unless a third CPU shows up that genuinely shares the quirk.
// ============================================================================


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

/// <summary>
/// Phase 7 GB block-JIT P0.6 — per-instr `defer` emitter.
///
/// In block-JIT mode, the BlockFunctionBuilder runs DeferLowering pre-pass
/// that strips defer wrappers and inlines the body into a later
/// instruction's emit list. So this emitter is ONLY hit in per-instr
/// (InstructionFunctionBuilder) mode.
///
/// V1: action_id=0 routes to the existing LR35902 IME-delayed extern
/// (host_lr35902_arm_ime_delayed) which sets the JsonCpu's _eiDelay
/// counter. This preserves current EI behaviour exactly. V2 generalises
/// to any action_id via a per-action runtime mechanism (state slot table
/// + outer-loop tick).
/// </summary>
internal sealed class DeferEmitterPerInstr : IMicroOpEmitter
{
    public string OpName => "defer";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var parsed = DeferStep.Parse(step);
        if (parsed.ActionId != 0)
            throw new NotSupportedException(
                $"defer.action_id={parsed.ActionId} not supported in V1 — only action_id=0 (LR35902 EI/IME-delayed). " +
                "V2 will add a generic per-action runtime mechanism.");
        if (parsed.DelayValue != 1)
            throw new NotSupportedException(
                $"defer.delay_value={parsed.DelayValue} for action_id=0 not supported in V1 — " +
                "current LR35902 IME extern hardcodes delay=1.");
        // Route to existing LR35902 IME-delayed extern. Body content is
        // not re-emitted in per-instr mode — the extern + JsonCpu's
        // RunCycles _eiDelay countdown owns the IME=1 effect.
        var (slot, fnType, ptrType) = Lr35902HostHelpers.GetVoidNoArgSlot(
            ctx.Module, Lr35902HostHelpers.ArmImeDelayedExtern);
        var fn = ctx.Builder.BuildLoad2(ptrType, slot, Lr35902HostHelpers.ArmImeDelayedExtern + "_fn");
        ctx.Builder.BuildCall2(fnType, fn, Array.Empty<LLVMValueRef>(), "");
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

// INC/DEC (HL)-mem emitter deleted in Phase 5.8 Step 5.7.B cleanup —
// migrated to generic read_reg_pair_named + load_byte + add/sub + trunc + store_byte
// + update_zero + set_flag(N) + update_h_inc/dec.

// CB-prefix (HL)-mem variants deleted in Phase 5.8 Step 5.4 cleanup —
// migrated to generic read_reg_pair_named + load_byte + bit_*/shift + store_byte chain.
