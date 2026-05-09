using AprCpu.Core.JsonSpec;
using LLVMSharp.Interop;

namespace AprCpu.Core.IR;

/// <summary>
/// MOS 6502 / Ricoh 2A03 — micro-op emitters for the
/// <c>mos_*</c> ops referenced by <c>spec/2a03/groups/*.json</c>, plus
/// the <c>read_imm8</c> / <c>read_imm16</c> per-instr fetchers
/// (analogous to the LR35902 ones but reading from the 6502's PC, which
/// is a 16-bit status register).
///
/// Registered into <see cref="EmitterRegistry"/> by <see cref="SpecCompiler"/>
/// when <c>architecture.family</c> = <c>"MOS6502"</c>. The 2A03 spec
/// only ever uses these in per-instr (interpreter) mode — there is no
/// block-JIT backend for 6502 yet, so all emitters take the
/// <c>ctx.CurrentInstructionBaseAddress is null</c> per-instr path.
///
/// Generic ops the 6502 spec also uses (<c>read_reg_named</c>,
/// <c>write_reg_named</c>, <c>add</c>/<c>sub</c>/<c>and</c>/...,
/// <c>push8</c>/<c>pop8</c>, <c>set_flag</c>, <c>update_zero</c> /
/// <c>update_sign</c>, etc.) are registered by <see cref="StandardEmitters"/>
/// and <see cref="Lr35902Emitters"/> respectively.
/// </summary>
public static class Mos6502Emitters
{
    public static void RegisterAll(EmitterRegistry reg)
    {
        // The named-register helpers (read_reg_named/write_reg_named) live
        // inside Lr35902Emitters today but are CPU-agnostic — they look up
        // the name in the spec's GPR table or status registers. The 2A03
        // spec uses them for A/X/Y/SP/PC/P. Register them here too so the
        // 6502 emitter set is self-contained.
        reg.Register(new ReadRegNamedEmitter());
        reg.Register(new WriteRegNamedEmitter());

        // Immediate fetch via PC. PC is a 16-bit status register; the
        // bus extern is the same memory_read_8 used by every other CPU.
        reg.Register(new Mos6502ReadImm8Emitter());
        reg.Register(new Mos6502ReadImm16Emitter());

        // Generic byte load/store with i32 address — shared with LR35902
        // but registered here since SpecCompiler only registers the
        // Lr35902 bundle for Sharp-SM83. The op shapes match
        // <see cref="Lr35902LoadByteEmitter"/> /
        // <see cref="Lr35902StoreByteEmitter"/>.
        reg.Register(new Mos6502LoadByteEmitter());
        reg.Register(new Mos6502StoreByteEmitter());

        // Addressing-mode operand resolvers (8-way runtime field
        // dispatch on the bbb sub-field, mirror of Lr35902ReadR8).
        reg.Register(new MosLoadOperandCc01());
        reg.Register(new MosStoreOperandCc01());
        reg.Register(new MosLoadOperandCc10());
        reg.Register(new MosStoreOperandCc10());
        reg.Register(new MosLoadOperandCc00());
        reg.Register(new MosStoreOperandCc00());
        reg.Register(new MosAddrCc01());

        // ALU helpers that touch multiple flags at once.
        reg.Register(new MosAdc());
        reg.Register(new MosSbc());
        reg.Register(new MosCompare());
        reg.Register(new MosBit());

        // RMW combo on cc=10 — load + shift/inc/dec + store-back.
        // Combining is the simplest way to keep PC and effective-address
        // consistent (a load+store split would re-fetch imm bytes from
        // PC twice, advancing it past the next instruction).
        reg.Register(new MosRmwCc10());

        // Control-flow primitives (the rest go through generic
        // branch/call/ret + push8/pop8 chains).
        reg.Register(new MosJmpIndirect());
        reg.Register(new MosBranchRel());
        reg.Register(new MosJsr());
        reg.Register(new MosRts());
        reg.Register(new MosRti());
        reg.Register(new MosBrk());
        reg.Register(new MosKil());

        // cc=11 unofficial RMW combos (SLO/RLA/SRE/RRA/SAX/LAX/DCP/ISC).
        // Same combined-emitter rationale as mos_rmw_cc10: lets us compute
        // the effective address once, then read+modify+write without
        // re-advancing PC.
        reg.Register(new MosUnofficialCc11());

        // Tiny per-op emitters for the irregular #imm unofficials that
        // don't fit the cc=11 broad template (ANC/ALR/ARR/AXS/XAA/LAS).
        reg.Register(new MosAnc());
        reg.Register(new MosAlr());
        reg.Register(new MosArr());
        reg.Register(new MosAxs());
        reg.Register(new MosXaa());
        reg.Register(new MosLas());

        // Magic-store unstable opcodes that blargg cpu_test5 still exercises
        // for the no-page-cross case (well-defined behaviour). The full
        // page-cross + dummy-read semantics are matched against LegacyCpu.
        reg.Register(new MosShy());
        reg.Register(new MosShx());
    }

    // ---------------- shared helpers ----------------

    /// <summary>
    /// Read a byte from the bus by 32-bit address. Identical wrapper to
    /// <see cref="MemoryEmitters.CallRead8"/>; kept as a local alias so
    /// the 6502 emitters read like a coherent block.
    /// </summary>
    internal static LLVMValueRef BusRead8(EmitContext ctx, LLVMValueRef addrI32, string label)
        => MemoryEmitters.CallRead8(ctx, addrI32, label);

    internal static void BusWrite8(EmitContext ctx, LLVMValueRef addrI32, LLVMValueRef value8)
        => MemoryEmitters.CallWrite8(ctx, addrI32, value8);

    /// <summary>Get the i32 PC value (loaded from the 16-bit status reg).</summary>
    internal static LLVMValueRef LoadPc16(EmitContext ctx, string label)
    {
        var pcPtr = ctx.GepStatusRegister("PC");
        return ctx.Builder.BuildLoad2(LLVMTypeRef.Int16, pcPtr, label);
    }

    internal static void StorePc16(EmitContext ctx, LLVMValueRef pc16)
    {
        var pcPtr = ctx.GepStatusRegister("PC");
        ctx.Builder.BuildStore(pc16, pcPtr);
    }

    /// <summary>Zero-page read at byte address: load i8 from $00..$FF.</summary>
    internal static LLVMValueRef ZpRead(EmitContext ctx, LLVMValueRef zpAddr8, string label)
    {
        var i32 = LLVMTypeRef.Int32;
        var addr32 = ctx.Builder.BuildZExt(zpAddr8, i32, $"{label}_z32");
        return BusRead8(ctx, addr32, label);
    }

    /// <summary>Zero-page write at byte address: store i8 to $00..$FF.</summary>
    internal static void ZpWrite(EmitContext ctx, LLVMValueRef zpAddr8, LLVMValueRef value8)
    {
        var i32 = LLVMTypeRef.Int32;
        var addr32 = ctx.Builder.BuildZExt(zpAddr8, i32, "zp_w_z32");
        BusWrite8(ctx, addr32, value8);
    }

    /// <summary>
    /// Read a byte from PC and advance PC by 1. Returns the i8 value.
    ///
    /// <para>N2.4 — block-JIT fast path: when <c>ctx.CurrentInstructionBaseAddress</c>
    /// is set (block-JIT mode), the instruction word constant carries
    /// every byte at compile time. Extract imm8 directly via shift+trunc
    /// over <c>ctx.Instruction</c>, skipping the bus extern call. PC alloca
    /// IS still advanced by 1 — downstream emitter steps inside the same
    /// instruction (e.g. <c>mos_branch_rel</c>'s post-FetchImm8 PC read for
    /// target = PC + sext(off)) depend on the advance. mem2reg + LLVM
    /// constant-folding collapse the load+add+store chain to SSA-register
    /// arithmetic, so the in-block PC bookkeeping is essentially free.</para>
    /// </summary>
    internal static LLVMValueRef FetchImm8(EmitContext ctx, string label)
    {
        var i8 = LLVMTypeRef.Int8;
        var i16 = LLVMTypeRef.Int16;
        var i32 = LLVMTypeRef.Int32;
        var pcPtr = ctx.GepStatusRegister("PC");
        var pc16 = ctx.Builder.BuildLoad2(i16, pcPtr, $"{label}_pc");

        LLVMValueRef b;
        // Block-JIT fast path — extract from instruction word constant
        // instead of calling memory_read_8 extern.
        if (ctx.CurrentInstructionBaseAddress is not null)
        {
            // 6502 layout: opcode at byte 0, imm8 at byte 1.
            var shifted = ctx.Builder.BuildLShr(ctx.Instruction,
                LLVMValueRef.CreateConstInt(i32, 8, false), $"{label}_instr_shr8");
            b = ctx.Builder.BuildTrunc(shifted, i8, $"{label}_imm");
        }
        else
        {
            // Per-instr fallback: bus.ReadByte at PC.
            var pc32 = ctx.Builder.BuildZExt(pc16, i32, $"{label}_pc32");
            b = BusRead8(ctx, pc32, label);
        }

        // PC advance is shared between both modes — see doc comment above.
        var newPc = ctx.Builder.BuildAdd(pc16,
            LLVMValueRef.CreateConstInt(i16, 1, false), $"{label}_pc_next");
        ctx.Builder.BuildStore(newPc, pcPtr);
        return b;
    }

    /// <summary>
    /// Fetch i16 little-endian from PC, advance PC by 2. Block-JIT fast
    /// path mirrors <see cref="FetchImm8"/> — extract the 16-bit imm
    /// directly from <c>ctx.Instruction</c>, skipping two bus.ReadByte
    /// extern calls.
    /// </summary>
    internal static LLVMValueRef FetchImm16(EmitContext ctx, string label)
    {
        var i16 = LLVMTypeRef.Int16;
        var i32 = LLVMTypeRef.Int32;
        var pcPtr = ctx.GepStatusRegister("PC");
        var pc16 = ctx.Builder.BuildLoad2(i16, pcPtr, $"{label}_pc");

        LLVMValueRef word;
        if (ctx.CurrentInstructionBaseAddress is not null)
        {
            // 6502 layout: opcode at byte 0, imm16 little-endian at bytes 1-2.
            var shifted = ctx.Builder.BuildLShr(ctx.Instruction,
                LLVMValueRef.CreateConstInt(i32, 8, false), $"{label}_instr_shr8");
            word = ctx.Builder.BuildTrunc(shifted, i16, $"{label}_imm");
        }
        else
        {
            var pc32Lo = ctx.Builder.BuildZExt(pc16, i32, $"{label}_pcz");
            var lo8 = BusRead8(ctx, pc32Lo, $"{label}_lo");
            var pcPlus1 = ctx.Builder.BuildAdd(pc16,
                LLVMValueRef.CreateConstInt(i16, 1, false), $"{label}_pc1");
            var pc32Hi = ctx.Builder.BuildZExt(pcPlus1, i32, $"{label}_pc1z");
            var hi8 = BusRead8(ctx, pc32Hi, $"{label}_hi");
            var loZ = ctx.Builder.BuildZExt(lo8, i16, $"{label}_loz");
            var hiZ = ctx.Builder.BuildZExt(hi8, i16, $"{label}_hiz");
            var hiSh = ctx.Builder.BuildShl(hiZ,
                LLVMValueRef.CreateConstInt(i16, 8, false), $"{label}_hi_shl");
            word = ctx.Builder.BuildOr(hiSh, loZ, label);
        }

        var newPc = ctx.Builder.BuildAdd(pc16,
            LLVMValueRef.CreateConstInt(i16, 2, false), $"{label}_pc_next");
        ctx.Builder.BuildStore(newPc, pcPtr);
        return word;
    }

    /// <summary>
    /// Compute the 16-bit zero-page pointer for indexed-indirect (zp,X)
    /// or indirect-indexed (zp),Y modes. Reads two consecutive zero-page
    /// bytes with high-byte address wrapping inside zero page (the
    /// canonical 6502 quirk: ptr+1 wraps modulo 256 within $00..$FF).
    /// Output is i16.
    /// </summary>
    internal static LLVMValueRef ReadZpPointer(EmitContext ctx, LLVMValueRef zpBase8, string label)
    {
        var i8 = LLVMTypeRef.Int8;
        var i16 = LLVMTypeRef.Int16;
        var hiAddr = ctx.Builder.BuildAdd(zpBase8,
            LLVMValueRef.CreateConstInt(i8, 1, false), $"{label}_hi_zp");
        var lo = ZpRead(ctx, zpBase8, $"{label}_p_lo");
        var hi = ZpRead(ctx, hiAddr,  $"{label}_p_hi");
        var loZ = ctx.Builder.BuildZExt(lo, i16, $"{label}_p_loz");
        var hiZ = ctx.Builder.BuildZExt(hi, i16, $"{label}_p_hiz");
        var hiSh = ctx.Builder.BuildShl(hiZ,
            LLVMValueRef.CreateConstInt(i16, 8, false), $"{label}_p_hi_shl");
        return ctx.Builder.BuildOr(hiSh, loZ, label);
    }

    /// <summary>Read GPR by symbolic name (A/X/Y) — returns i8.</summary>
    internal static LLVMValueRef ReadGpr(EmitContext ctx, string name, string label)
    {
        var (ptr, _) = Lr35902Emitters.LocateRegister(ctx, name);
        return ctx.Builder.BuildLoad2(LLVMTypeRef.Int8, ptr, label);
    }

    /// <summary>Write GPR by symbolic name (A/X/Y) — value must be i8.</summary>
    internal static void WriteGpr(EmitContext ctx, string name, LLVMValueRef v8)
    {
        var (ptr, _) = Lr35902Emitters.LocateRegister(ctx, name);
        ctx.Builder.BuildStore(v8, ptr);
    }

    // ============================================================================
    // ADDRESSING-MODE OPERAND RESOLVERS
    //
    // Each emitter chains LLVM `select` instructions over the 8 possible
    // bbb (or 5/6 in narrower groups) values and lets dead-code elimination
    // collapse the unused branches. The same shape mirror that
    // Lr35902ReadR8Emitter uses for its sss/ddd field dispatch.
    // ============================================================================

    /// <summary>
    /// Compute the i32 effective address for the cc=01 ALU group's bbb
    /// modes, using the spec field name (typically "bbb"). Modes:
    /// <list type="bullet">
    ///   <item>000=(zp,X) — fetch zp byte; pointer = ZP[(zp+X) &amp; 0xFF],
    ///         high byte at ZP[(zp+X+1) &amp; 0xFF]</item>
    ///   <item>001=zp     — fetch zp byte → addr = zp</item>
    ///   <item>011=abs    — fetch imm16 → addr</item>
    ///   <item>100=(zp),Y — fetch zp byte; pointer = ZP[zp]/ZP[(zp+1)&amp;0xFF]; addr = ptr + Y</item>
    ///   <item>101=zp,X   — addr = (zp + X) &amp; 0xFF</item>
    ///   <item>110=abs,Y  — addr = imm16 + Y</item>
    ///   <item>111=abs,X  — addr = imm16 + X</item>
    ///   <item>010=#imm   — illegal here (caller must not request this)</item>
    /// </list>
    /// Page-cross dummy reads are NOT modelled (cycle accounting lives in C#).
    /// </summary>
    internal static LLVMValueRef ComputeEffectiveAddrCc01(EmitContext ctx, int bbb, string label)
    {
        var i8 = LLVMTypeRef.Int8;
        var i16 = LLVMTypeRef.Int16;
        var i32 = LLVMTypeRef.Int32;
        switch (bbb)
        {
            case 0: // (zp,X)
            {
                var zp = FetchImm8(ctx, $"{label}_zp");
                var x  = ReadGpr(ctx, "X", $"{label}_x");
                var sum8 = ctx.Builder.BuildAdd(zp, x, $"{label}_zpx"); // wraps in i8
                var ptr16 = ReadZpPointer(ctx, sum8, $"{label}_p");
                return ctx.Builder.BuildZExt(ptr16, i32, $"{label}_addr");
            }
            case 1: // zp
            {
                var zp = FetchImm8(ctx, $"{label}_zp");
                return ctx.Builder.BuildZExt(zp, i32, $"{label}_addr");
            }
            case 3: // abs
            {
                var w = FetchImm16(ctx, $"{label}_abs");
                return ctx.Builder.BuildZExt(w, i32, $"{label}_addr");
            }
            case 4: // (zp),Y
            {
                var zp = FetchImm8(ctx, $"{label}_zp");
                var y  = ReadGpr(ctx, "Y", $"{label}_y");
                var ptr16 = ReadZpPointer(ctx, zp, $"{label}_p");
                var yZ = ctx.Builder.BuildZExt(y, i16, $"{label}_yz16");
                var sum16 = ctx.Builder.BuildAdd(ptr16, yZ, $"{label}_addr16");
                return ctx.Builder.BuildZExt(sum16, i32, $"{label}_addr");
            }
            case 5: // zp,X
            {
                var zp = FetchImm8(ctx, $"{label}_zp");
                var x  = ReadGpr(ctx, "X", $"{label}_x");
                var sum8 = ctx.Builder.BuildAdd(zp, x, $"{label}_zpx");
                return ctx.Builder.BuildZExt(sum8, i32, $"{label}_addr");
            }
            case 6: // abs,Y
            {
                var w = FetchImm16(ctx, $"{label}_abs");
                var y = ReadGpr(ctx, "Y", $"{label}_y");
                var yZ = ctx.Builder.BuildZExt(y, i16, $"{label}_yz16");
                var sum16 = ctx.Builder.BuildAdd(w, yZ, $"{label}_addr16");
                return ctx.Builder.BuildZExt(sum16, i32, $"{label}_addr");
            }
            case 7: // abs,X
            {
                var w = FetchImm16(ctx, $"{label}_abs");
                var x = ReadGpr(ctx, "X", $"{label}_x");
                var xZ = ctx.Builder.BuildZExt(x, i16, $"{label}_xz16");
                var sum16 = ctx.Builder.BuildAdd(w, xZ, $"{label}_addr16");
                return ctx.Builder.BuildZExt(sum16, i32, $"{label}_addr");
            }
            default:
                // bbb=010 #imm has no effective address — return 0 as a
                // placeholder; callers that legitimately request this
                // for cc=01 are buggy.
                return LLVMValueRef.CreateConstInt(i32, 0, false);
        }
    }

    /// <summary>
    /// Compute the i32 effective address for cc=10 / cc=00 (used by the
    /// memory-mode RMW + STX/STY/LDY etc.). The bbb modes are slightly
    /// different from cc=01:
    /// <list type="bullet">
    ///   <item>001=zp</item>
    ///   <item>011=abs</item>
    ///   <item>101=zp,X (or zp,Y if swap_xy)</item>
    ///   <item>111=abs,X (or abs,Y if swap_xy)</item>
    /// </list>
    /// All other bbb values for memory access are illegal/unstructured;
    /// caller checks before calling. swap_xy is for STX/LDX which use Y
    /// instead of X for indexing.
    /// </summary>
    internal static LLVMValueRef ComputeEffectiveAddrMem(EmitContext ctx, int bbb, bool swapXy, string label)
    {
        var i16 = LLVMTypeRef.Int16;
        var i32 = LLVMTypeRef.Int32;
        var idxReg = swapXy ? "Y" : "X";
        switch (bbb)
        {
            case 1: // zp
            {
                var zp = FetchImm8(ctx, $"{label}_zp");
                return ctx.Builder.BuildZExt(zp, i32, $"{label}_addr");
            }
            case 3: // abs
            {
                var w = FetchImm16(ctx, $"{label}_abs");
                return ctx.Builder.BuildZExt(w, i32, $"{label}_addr");
            }
            case 5: // zp,X (or zp,Y)
            {
                var zp = FetchImm8(ctx, $"{label}_zp");
                var idx = ReadGpr(ctx, idxReg, $"{label}_idx");
                var sum8 = ctx.Builder.BuildAdd(zp, idx, $"{label}_zpx");
                return ctx.Builder.BuildZExt(sum8, i32, $"{label}_addr");
            }
            case 7: // abs,X (or abs,Y)
            {
                var w = FetchImm16(ctx, $"{label}_abs");
                var idx = ReadGpr(ctx, idxReg, $"{label}_idx");
                var idxZ = ctx.Builder.BuildZExt(idx, i16, $"{label}_idxz");
                var sum16 = ctx.Builder.BuildAdd(w, idxZ, $"{label}_addr16");
                return ctx.Builder.BuildZExt(sum16, i32, $"{label}_addr");
            }
            default:
                return LLVMValueRef.CreateConstInt(i32, 0, false);
        }
    }

    /// <summary>
    /// Read flag bit from P as i32 (0 or 1). Convenience wrapper around
    /// CpsrHelpers.
    /// </summary>
    internal static LLVMValueRef ReadFlag(EmitContext ctx, string flag)
        => CpsrHelpers.ReadStatusFlag(ctx, "P", flag);
}

// ============================================================================
// Per-instr immediate fetchers
// ============================================================================

internal sealed class Mos6502ReadImm8Emitter : IMicroOpEmitter
{
    public string OpName => "read_imm8";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var outName = StandardEmitters.GetOut(step.Raw);
        var b = Mos6502Emitters.FetchImm8(ctx, outName);
        ctx.Values[outName] = b;
    }
}

internal sealed class Mos6502ReadImm16Emitter : IMicroOpEmitter
{
    public string OpName => "read_imm16";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var outName = StandardEmitters.GetOut(step.Raw);
        var w = Mos6502Emitters.FetchImm16(ctx, outName);
        ctx.Values[outName] = w;
    }
}

// ============================================================================
// Generic byte memory access (matches Lr35902 op shape)
// ============================================================================

internal sealed class Mos6502LoadByteEmitter : IMicroOpEmitter
{
    public string OpName => "load_byte";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var addrName = step.Raw.GetProperty("address").GetString()!;
        var outName  = StandardEmitters.GetOut(step.Raw);
        var addr = ctx.Resolve(addrName);
        var addr32 = AddrToI32(ctx, addr, addrName);
        ctx.Values[outName] = Mos6502Emitters.BusRead8(ctx, addr32, outName);
    }

    private static LLVMValueRef AddrToI32(EmitContext ctx, LLVMValueRef addr, string label)
    {
        var w = addr.TypeOf.IntWidth;
        if (w == 32) return addr;
        if (w < 32) return ctx.Builder.BuildZExt(addr, LLVMTypeRef.Int32, $"{label}_z32");
        return ctx.Builder.BuildTrunc(addr, LLVMTypeRef.Int32, $"{label}_t32");
    }
}

internal sealed class Mos6502StoreByteEmitter : IMicroOpEmitter
{
    public string OpName => "store_byte";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var addrName  = step.Raw.GetProperty("address").GetString()!;
        var valueName = step.Raw.GetProperty("value").GetString()!;
        var addr = ctx.Resolve(addrName);
        var raw  = ctx.Resolve(valueName);
        var addr32 = AddrToI32(ctx, addr, addrName);
        var v8 = raw.TypeOf.IntWidth == 8 ? raw
            : raw.TypeOf.IntWidth < 8
                ? ctx.Builder.BuildZExt(raw, LLVMTypeRef.Int8, $"{valueName}_z8")
                : ctx.Builder.BuildTrunc(raw, LLVMTypeRef.Int8, $"{valueName}_t8");
        Mos6502Emitters.BusWrite8(ctx, addr32, v8);
    }

    private static LLVMValueRef AddrToI32(EmitContext ctx, LLVMValueRef addr, string label)
    {
        var w = addr.TypeOf.IntWidth;
        if (w == 32) return addr;
        if (w < 32) return ctx.Builder.BuildZExt(addr, LLVMTypeRef.Int32, $"{label}_z32");
        return ctx.Builder.BuildTrunc(addr, LLVMTypeRef.Int32, $"{label}_t32");
    }
}

// ============================================================================
// cc=01 ALU operand load — out: i8
// ============================================================================

internal sealed class MosLoadOperandCc01 : IMicroOpEmitter
{
    public string OpName => "mos_load_operand_cc01";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var fieldName = step.Raw.GetProperty("field").GetString()!;
        var outName   = StandardEmitters.GetOut(step.Raw);
        var i8 = LLVMTypeRef.Int8;
        var i32 = LLVMTypeRef.Int32;

        var field = ctx.Resolve(fieldName);

        // Build a value for each bbb path. For #imm we fetch one byte;
        // the others go via ComputeEffectiveAddrCc01 + bus read.
        // NOTE: every path side-effects (PC advance + bus reads), and
        // dead-arm DCE on side-effecting code is unsafe. So we lower to
        // a switch over basic blocks — exactly one arm runs.
        var endBB = ctx.Function.AppendBasicBlock("op01_end");
        var defaultBB = ctx.Function.AppendBasicBlock("op01_default");
        var arms = new LLVMBasicBlockRef[8];
        for (int i = 0; i < 8; i++) arms[i] = ctx.Function.AppendBasicBlock($"op01_b{i}");

        var sw = ctx.Builder.BuildSwitch(field, defaultBB, 8);
        for (int i = 0; i < 8; i++)
            sw.AddCase(LLVMValueRef.CreateConstInt(i32, (uint)i, false), arms[i]);

        // Per-arm IR + collect (value, fromBlock) pairs to phi at end.
        var incomingVals   = new LLVMValueRef[9];
        var incomingBlocks = new LLVMBasicBlockRef[9];

        for (int bbb = 0; bbb < 8; bbb++)
        {
            ctx.Builder.PositionAtEnd(arms[bbb]);
            LLVMValueRef v;
            if (bbb == 2)
            {
                // #imm
                v = Mos6502Emitters.FetchImm8(ctx, $"op01_imm");
            }
            else
            {
                var addr32 = Mos6502Emitters.ComputeEffectiveAddrCc01(ctx, bbb, $"op01_b{bbb}");
                v = Mos6502Emitters.BusRead8(ctx, addr32, $"op01_b{bbb}_v");
            }
            incomingVals[bbb]   = v;
            incomingBlocks[bbb] = ctx.Builder.InsertBlock;
            ctx.Builder.BuildBr(endBB);
        }

        // default arm — unreachable in well-formed input but must terminate.
        ctx.Builder.PositionAtEnd(defaultBB);
        incomingVals[8] = LLVMValueRef.CreateConstInt(i8, 0, false);
        incomingBlocks[8] = defaultBB;
        ctx.Builder.BuildBr(endBB);

        // end block — phi the value.
        ctx.Builder.PositionAtEnd(endBB);
        var phi = ctx.Builder.BuildPhi(i8, outName);
        phi.AddIncoming(incomingVals, incomingBlocks, (uint)incomingVals.Length);
        ctx.Values[outName] = phi;
    }
}

// ============================================================================
// cc=01 ALU operand store (STA + unofficial store-back ops)
// ============================================================================

internal sealed class MosStoreOperandCc01 : IMicroOpEmitter
{
    public string OpName => "mos_store_operand_cc01";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var fieldName = step.Raw.GetProperty("field").GetString()!;
        var valueName = step.Raw.GetProperty("value").GetString()!;
        var i32 = LLVMTypeRef.Int32;

        var field = ctx.Resolve(fieldName);
        var raw   = ctx.Resolve(valueName);
        var v8 = raw.TypeOf.IntWidth == 8 ? raw
            : raw.TypeOf.IntWidth < 8
                ? ctx.Builder.BuildZExt(raw, LLVMTypeRef.Int8, $"{valueName}_z8")
                : ctx.Builder.BuildTrunc(raw, LLVMTypeRef.Int8, $"{valueName}_t8");

        var endBB = ctx.Function.AppendBasicBlock("st01_end");
        var defaultBB = ctx.Function.AppendBasicBlock("st01_default");
        var arms = new LLVMBasicBlockRef[8];
        for (int i = 0; i < 8; i++) arms[i] = ctx.Function.AppendBasicBlock($"st01_b{i}");

        var sw = ctx.Builder.BuildSwitch(field, defaultBB, 8);
        for (int i = 0; i < 8; i++)
            sw.AddCase(LLVMValueRef.CreateConstInt(i32, (uint)i, false), arms[i]);

        for (int bbb = 0; bbb < 8; bbb++)
        {
            ctx.Builder.PositionAtEnd(arms[bbb]);
            if (bbb != 2) // skip #imm (illegal STA #imm — handled as NOP)
            {
                var addr32 = Mos6502Emitters.ComputeEffectiveAddrCc01(ctx, bbb, $"st01_b{bbb}");
                Mos6502Emitters.BusWrite8(ctx, addr32, v8);
            }
            ctx.Builder.BuildBr(endBB);
        }

        ctx.Builder.PositionAtEnd(defaultBB);
        ctx.Builder.BuildBr(endBB);

        ctx.Builder.PositionAtEnd(endBB);
    }
}

// ============================================================================
// cc=01 effective-address producer (RMW unofficial ops + STA storeback)
// ============================================================================

internal sealed class MosAddrCc01 : IMicroOpEmitter
{
    public string OpName => "mos_addr_cc01";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var fieldName = step.Raw.GetProperty("field").GetString()!;
        var outName   = StandardEmitters.GetOut(step.Raw);
        var i32 = LLVMTypeRef.Int32;

        var field = ctx.Resolve(fieldName);

        var endBB = ctx.Function.AppendBasicBlock("ad01_end");
        var defaultBB = ctx.Function.AppendBasicBlock("ad01_default");
        var arms = new LLVMBasicBlockRef[8];
        for (int i = 0; i < 8; i++) arms[i] = ctx.Function.AppendBasicBlock($"ad01_b{i}");

        var sw = ctx.Builder.BuildSwitch(field, defaultBB, 8);
        for (int i = 0; i < 8; i++)
            sw.AddCase(LLVMValueRef.CreateConstInt(i32, (uint)i, false), arms[i]);

        var incomingVals   = new LLVMValueRef[9];
        var incomingBlocks = new LLVMBasicBlockRef[9];
        for (int bbb = 0; bbb < 8; bbb++)
        {
            ctx.Builder.PositionAtEnd(arms[bbb]);
            LLVMValueRef v;
            if (bbb == 2)
            {
                // #imm has no addr — should not be reached for cc=01 RMW
                v = LLVMValueRef.CreateConstInt(i32, 0, false);
            }
            else
            {
                v = Mos6502Emitters.ComputeEffectiveAddrCc01(ctx, bbb, $"ad01_b{bbb}");
            }
            incomingVals[bbb]   = v;
            incomingBlocks[bbb] = ctx.Builder.InsertBlock;
            ctx.Builder.BuildBr(endBB);
        }
        ctx.Builder.PositionAtEnd(defaultBB);
        incomingVals[8] = LLVMValueRef.CreateConstInt(i32, 0, false);
        incomingBlocks[8] = defaultBB;
        ctx.Builder.BuildBr(endBB);

        ctx.Builder.PositionAtEnd(endBB);
        var phi = ctx.Builder.BuildPhi(i32, outName);
        phi.AddIncoming(incomingVals, incomingBlocks, (uint)incomingVals.Length);
        ctx.Values[outName] = phi;
    }
}

// ============================================================================
// cc=10 operand load (RMW + LDX) — out: i8
//
// bbb mapping for cc=10 (per 6502 encoding):
//   000 = #imm  (LDX only)
//   001 = zp
//   010 = A     (accumulator addressing — operates on the A register itself)
//   011 = abs
//   101 = zp,X (or zp,Y if swap_xy=true)
//   111 = abs,X (or abs,Y if swap_xy=true)
//
// Other bbb values are illegal for the cc=10 opcodes.
// ============================================================================

internal sealed class MosLoadOperandCc10 : IMicroOpEmitter
{
    public string OpName => "mos_load_operand_cc10";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var fieldName = step.Raw.GetProperty("field").GetString()!;
        var outName   = StandardEmitters.GetOut(step.Raw);
        bool swapXy = step.Raw.TryGetProperty("swap_xy", out var sx) && sx.GetBoolean();
        var i8 = LLVMTypeRef.Int8;
        var i32 = LLVMTypeRef.Int32;

        var field = ctx.Resolve(fieldName);

        var endBB = ctx.Function.AppendBasicBlock("op10_end");
        var defaultBB = ctx.Function.AppendBasicBlock("op10_default");
        var arms = new LLVMBasicBlockRef[8];
        for (int i = 0; i < 8; i++) arms[i] = ctx.Function.AppendBasicBlock($"op10_b{i}");

        var sw = ctx.Builder.BuildSwitch(field, defaultBB, 8);
        for (int i = 0; i < 8; i++)
            sw.AddCase(LLVMValueRef.CreateConstInt(i32, (uint)i, false), arms[i]);

        var incomingVals   = new LLVMValueRef[9];
        var incomingBlocks = new LLVMBasicBlockRef[9];

        for (int bbb = 0; bbb < 8; bbb++)
        {
            ctx.Builder.PositionAtEnd(arms[bbb]);
            LLVMValueRef v;
            switch (bbb)
            {
                case 0: // #imm
                    v = Mos6502Emitters.FetchImm8(ctx, "op10_imm");
                    break;
                case 2: // accumulator
                    v = Mos6502Emitters.ReadGpr(ctx, "A", "op10_a");
                    break;
                case 1: // zp
                case 3: // abs
                case 5: // zp,X(/Y)
                case 7: // abs,X(/Y)
                {
                    var addr32 = Mos6502Emitters.ComputeEffectiveAddrMem(ctx, bbb, swapXy, $"op10_b{bbb}");
                    v = Mos6502Emitters.BusRead8(ctx, addr32, $"op10_b{bbb}_v");
                    break;
                }
                default:
                    v = LLVMValueRef.CreateConstInt(i8, 0, false);
                    break;
            }
            incomingVals[bbb]   = v;
            incomingBlocks[bbb] = ctx.Builder.InsertBlock;
            ctx.Builder.BuildBr(endBB);
        }

        ctx.Builder.PositionAtEnd(defaultBB);
        incomingVals[8] = LLVMValueRef.CreateConstInt(i8, 0, false);
        incomingBlocks[8] = defaultBB;
        ctx.Builder.BuildBr(endBB);

        ctx.Builder.PositionAtEnd(endBB);
        var phi = ctx.Builder.BuildPhi(i8, outName);
        phi.AddIncoming(incomingVals, incomingBlocks, (uint)incomingVals.Length);
        ctx.Values[outName] = phi;
    }
}

internal sealed class MosStoreOperandCc10 : IMicroOpEmitter
{
    public string OpName => "mos_store_operand_cc10";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var fieldName = step.Raw.GetProperty("field").GetString()!;
        var valueName = step.Raw.GetProperty("value").GetString()!;
        bool swapXy = step.Raw.TryGetProperty("swap_xy", out var sx) && sx.GetBoolean();
        var i32 = LLVMTypeRef.Int32;

        var field = ctx.Resolve(fieldName);
        var raw   = ctx.Resolve(valueName);
        var v8 = raw.TypeOf.IntWidth == 8 ? raw
            : raw.TypeOf.IntWidth < 8
                ? ctx.Builder.BuildZExt(raw, LLVMTypeRef.Int8, $"{valueName}_z8")
                : ctx.Builder.BuildTrunc(raw, LLVMTypeRef.Int8, $"{valueName}_t8");

        var endBB = ctx.Function.AppendBasicBlock("st10_end");
        var defaultBB = ctx.Function.AppendBasicBlock("st10_default");
        var arms = new LLVMBasicBlockRef[8];
        for (int i = 0; i < 8; i++) arms[i] = ctx.Function.AppendBasicBlock($"st10_b{i}");

        var sw = ctx.Builder.BuildSwitch(field, defaultBB, 8);
        for (int i = 0; i < 8; i++)
            sw.AddCase(LLVMValueRef.CreateConstInt(i32, (uint)i, false), arms[i]);

        for (int bbb = 0; bbb < 8; bbb++)
        {
            ctx.Builder.PositionAtEnd(arms[bbb]);
            switch (bbb)
            {
                case 0: // #imm — no-op for store (illegal)
                    break;
                case 2: // accumulator — store to A
                    Mos6502Emitters.WriteGpr(ctx, "A", v8);
                    break;
                case 1: case 3: case 5: case 7:
                {
                    var addr32 = Mos6502Emitters.ComputeEffectiveAddrMem(ctx, bbb, swapXy, $"st10_b{bbb}");
                    Mos6502Emitters.BusWrite8(ctx, addr32, v8);
                    break;
                }
                default:
                    break;
            }
            ctx.Builder.BuildBr(endBB);
        }
        ctx.Builder.PositionAtEnd(defaultBB);
        ctx.Builder.BuildBr(endBB);

        ctx.Builder.PositionAtEnd(endBB);
    }
}

// ============================================================================
// cc=00 operand load/store — used by BIT/STY/LDY/CPX/CPY
//
// bbb mapping for cc=00:
//   000 = #imm
//   001 = zp
//   011 = abs
//   101 = zp,X
//   111 = abs,X
// ============================================================================

internal sealed class MosLoadOperandCc00 : IMicroOpEmitter
{
    public string OpName => "mos_load_operand_cc00";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var fieldName = step.Raw.GetProperty("field").GetString()!;
        var outName   = StandardEmitters.GetOut(step.Raw);
        var i8 = LLVMTypeRef.Int8;
        var i32 = LLVMTypeRef.Int32;

        var field = ctx.Resolve(fieldName);

        var endBB = ctx.Function.AppendBasicBlock("op00_end");
        var defaultBB = ctx.Function.AppendBasicBlock("op00_default");
        var arms = new LLVMBasicBlockRef[8];
        for (int i = 0; i < 8; i++) arms[i] = ctx.Function.AppendBasicBlock($"op00_b{i}");

        var sw = ctx.Builder.BuildSwitch(field, defaultBB, 8);
        for (int i = 0; i < 8; i++)
            sw.AddCase(LLVMValueRef.CreateConstInt(i32, (uint)i, false), arms[i]);

        var incomingVals   = new LLVMValueRef[9];
        var incomingBlocks = new LLVMBasicBlockRef[9];

        for (int bbb = 0; bbb < 8; bbb++)
        {
            ctx.Builder.PositionAtEnd(arms[bbb]);
            LLVMValueRef v;
            if (bbb == 0)
            {
                v = Mos6502Emitters.FetchImm8(ctx, "op00_imm");
            }
            else if (bbb == 1 || bbb == 3 || bbb == 5 || bbb == 7)
            {
                var addr32 = Mos6502Emitters.ComputeEffectiveAddrMem(ctx, bbb, swapXy: false, $"op00_b{bbb}");
                v = Mos6502Emitters.BusRead8(ctx, addr32, $"op00_b{bbb}_v");
            }
            else
            {
                v = LLVMValueRef.CreateConstInt(i8, 0, false);
            }
            incomingVals[bbb]   = v;
            incomingBlocks[bbb] = ctx.Builder.InsertBlock;
            ctx.Builder.BuildBr(endBB);
        }
        ctx.Builder.PositionAtEnd(defaultBB);
        incomingVals[8] = LLVMValueRef.CreateConstInt(i8, 0, false);
        incomingBlocks[8] = defaultBB;
        ctx.Builder.BuildBr(endBB);

        ctx.Builder.PositionAtEnd(endBB);
        var phi = ctx.Builder.BuildPhi(i8, outName);
        phi.AddIncoming(incomingVals, incomingBlocks, (uint)incomingVals.Length);
        ctx.Values[outName] = phi;
    }
}

internal sealed class MosStoreOperandCc00 : IMicroOpEmitter
{
    public string OpName => "mos_store_operand_cc00";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var fieldName = step.Raw.GetProperty("field").GetString()!;
        var valueName = step.Raw.GetProperty("value").GetString()!;
        var i32 = LLVMTypeRef.Int32;

        var field = ctx.Resolve(fieldName);
        var raw   = ctx.Resolve(valueName);
        var v8 = raw.TypeOf.IntWidth == 8 ? raw
            : raw.TypeOf.IntWidth < 8
                ? ctx.Builder.BuildZExt(raw, LLVMTypeRef.Int8, $"{valueName}_z8")
                : ctx.Builder.BuildTrunc(raw, LLVMTypeRef.Int8, $"{valueName}_t8");

        var endBB = ctx.Function.AppendBasicBlock("st00_end");
        var defaultBB = ctx.Function.AppendBasicBlock("st00_default");
        var arms = new LLVMBasicBlockRef[8];
        for (int i = 0; i < 8; i++) arms[i] = ctx.Function.AppendBasicBlock($"st00_b{i}");

        var sw = ctx.Builder.BuildSwitch(field, defaultBB, 8);
        for (int i = 0; i < 8; i++)
            sw.AddCase(LLVMValueRef.CreateConstInt(i32, (uint)i, false), arms[i]);

        for (int bbb = 0; bbb < 8; bbb++)
        {
            ctx.Builder.PositionAtEnd(arms[bbb]);
            if (bbb == 1 || bbb == 3 || bbb == 5 || bbb == 7)
            {
                var addr32 = Mos6502Emitters.ComputeEffectiveAddrMem(ctx, bbb, swapXy: false, $"st00_b{bbb}");
                Mos6502Emitters.BusWrite8(ctx, addr32, v8);
            }
            // bbb=0 (#imm) is a no-op; other bbb values are illegal/no-op.
            ctx.Builder.BuildBr(endBB);
        }
        ctx.Builder.PositionAtEnd(defaultBB);
        ctx.Builder.BuildBr(endBB);

        ctx.Builder.PositionAtEnd(endBB);
    }
}

// ============================================================================
// ALU compound ops — ADC/SBC/CMP/BIT
// ============================================================================

/// <summary>
/// 6502 ADC: A = A + rhs + C (binary mode — Ricoh 2A03 disables BCD).
/// Updates N, V, Z, C in P.
/// </summary>
internal sealed class MosAdc : IMicroOpEmitter
{
    public string OpName => "mos_adc";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var rhsName = step.Raw.GetProperty("rhs").GetString()!;
        var i8  = LLVMTypeRef.Int8;
        var i32 = LLVMTypeRef.Int32;

        var rhsRaw = ctx.Resolve(rhsName);
        var rhs8 = rhsRaw.TypeOf.IntWidth == 8 ? rhsRaw
            : rhsRaw.TypeOf.IntWidth < 8
                ? ctx.Builder.BuildZExt(rhsRaw, i8, $"{rhsName}_z8")
                : ctx.Builder.BuildTrunc(rhsRaw, i8, $"{rhsName}_t8");

        var aPtr = Lr35902Emitters.LocateRegister(ctx, "A").Ptr;
        var aOld = ctx.Builder.BuildLoad2(i8, aPtr, "adc_a");

        var cIn = Mos6502Emitters.ReadFlag(ctx, "C"); // i32 0/1

        var aZ = ctx.Builder.BuildZExt(aOld, i32, "adc_a_z");
        var rZ = ctx.Builder.BuildZExt(rhs8, i32, "adc_r_z");
        var sumNoC = ctx.Builder.BuildAdd(aZ, rZ, "adc_a_plus_r");
        var sum = ctx.Builder.BuildAdd(sumNoC, cIn, "adc_sum");
        var sum8 = ctx.Builder.BuildTrunc(sum, i8, "adc_sum8");

        // Z, N from sum8.
        var z = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, sum8,
            LLVMValueRef.CreateConstInt(i8, 0, false), "adc_z");
        var nMask = ctx.Builder.BuildAnd(sum8,
            LLVMValueRef.CreateConstInt(i8, 0x80, false), "adc_n_mask");
        var n = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, nMask,
            LLVMValueRef.CreateConstInt(i8, 0, false), "adc_n");

        // C from bit 8 of i32 sum.
        var cOutMask = ctx.Builder.BuildAnd(sum, ctx.ConstU32(0x100), "adc_c_mask");
        var c = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, cOutMask,
            ctx.ConstU32(0), "adc_c");

        // V: signed overflow = ((A ^ sum) & (rhs ^ sum) & 0x80) != 0
        var sum8Z = ctx.Builder.BuildZExt(sum8, i32, "adc_sum8_z");
        var aXorS = ctx.Builder.BuildXor(aZ, sum8Z, "adc_axs");
        var rXorS = ctx.Builder.BuildXor(rZ, sum8Z, "adc_rxs");
        var both  = ctx.Builder.BuildAnd(aXorS, rXorS, "adc_v_both");
        var bothMask = ctx.Builder.BuildAnd(both, ctx.ConstU32(0x80), "adc_v_mask");
        var v = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, bothMask,
            ctx.ConstU32(0), "adc_v");

        ctx.Builder.BuildStore(sum8, aPtr);
        CpsrHelpers.SetStatusFlag(ctx, "P", "Z", z);
        CpsrHelpers.SetStatusFlag(ctx, "P", "N", n);
        CpsrHelpers.SetStatusFlag(ctx, "P", "C", c);
        CpsrHelpers.SetStatusFlag(ctx, "P", "V", v);
    }
}

/// <summary>
/// 6502 SBC: A = A + (~rhs) + C (binary mode). Same flag updates as ADC,
/// just feed the inverted rhs.
/// </summary>
internal sealed class MosSbc : IMicroOpEmitter
{
    public string OpName => "mos_sbc";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var rhsName = step.Raw.GetProperty("rhs").GetString()!;
        var i8  = LLVMTypeRef.Int8;
        var i32 = LLVMTypeRef.Int32;

        var rhsRaw = ctx.Resolve(rhsName);
        var rhs8 = rhsRaw.TypeOf.IntWidth == 8 ? rhsRaw
            : rhsRaw.TypeOf.IntWidth < 8
                ? ctx.Builder.BuildZExt(rhsRaw, i8, $"{rhsName}_z8")
                : ctx.Builder.BuildTrunc(rhsRaw, i8, $"{rhsName}_t8");

        // Invert rhs at i8 width.
        var inv = ctx.Builder.BuildXor(rhs8,
            LLVMValueRef.CreateConstInt(i8, 0xFF, false), "sbc_inv");

        var aPtr = Lr35902Emitters.LocateRegister(ctx, "A").Ptr;
        var aOld = ctx.Builder.BuildLoad2(i8, aPtr, "sbc_a");
        var cIn = Mos6502Emitters.ReadFlag(ctx, "C");

        var aZ = ctx.Builder.BuildZExt(aOld, i32, "sbc_a_z");
        var iZ = ctx.Builder.BuildZExt(inv, i32, "sbc_i_z");
        var sumNoC = ctx.Builder.BuildAdd(aZ, iZ, "sbc_sum_pre");
        var sum = ctx.Builder.BuildAdd(sumNoC, cIn, "sbc_sum");
        var sum8 = ctx.Builder.BuildTrunc(sum, i8, "sbc_sum8");

        var z = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, sum8,
            LLVMValueRef.CreateConstInt(i8, 0, false), "sbc_z");
        var nMask = ctx.Builder.BuildAnd(sum8,
            LLVMValueRef.CreateConstInt(i8, 0x80, false), "sbc_n_mask");
        var n = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, nMask,
            LLVMValueRef.CreateConstInt(i8, 0, false), "sbc_n");
        var cOutMask = ctx.Builder.BuildAnd(sum, ctx.ConstU32(0x100), "sbc_c_mask");
        var c = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, cOutMask,
            ctx.ConstU32(0), "sbc_c");

        // V: ((A ^ sum) & (inv ^ sum) & 0x80) != 0
        var sum8Z = ctx.Builder.BuildZExt(sum8, i32, "sbc_sum8_z");
        var aXorS = ctx.Builder.BuildXor(aZ, sum8Z, "sbc_axs");
        var iXorS = ctx.Builder.BuildXor(iZ, sum8Z, "sbc_ixs");
        var both  = ctx.Builder.BuildAnd(aXorS, iXorS, "sbc_v_both");
        var bothMask = ctx.Builder.BuildAnd(both, ctx.ConstU32(0x80), "sbc_v_mask");
        var v = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, bothMask,
            ctx.ConstU32(0), "sbc_v");

        ctx.Builder.BuildStore(sum8, aPtr);
        CpsrHelpers.SetStatusFlag(ctx, "P", "Z", z);
        CpsrHelpers.SetStatusFlag(ctx, "P", "N", n);
        CpsrHelpers.SetStatusFlag(ctx, "P", "C", c);
        CpsrHelpers.SetStatusFlag(ctx, "P", "V", v);
    }
}

/// <summary>
/// CMP / CPX / CPY: NZC from (lhs - rhs); no destination write.
/// C = (lhs &gt;= rhs).
/// </summary>
internal sealed class MosCompare : IMicroOpEmitter
{
    public string OpName => "mos_compare";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var lhsName = step.Raw.GetProperty("lhs").GetString()!;
        var rhsName = step.Raw.GetProperty("rhs").GetString()!;
        var i8 = LLVMTypeRef.Int8;

        LLVMValueRef Coerce8(LLVMValueRef v, string label)
        {
            if (v.TypeOf.IntWidth == 8) return v;
            if (v.TypeOf.IntWidth < 8) return ctx.Builder.BuildZExt(v, i8, $"{label}_z8");
            return ctx.Builder.BuildTrunc(v, i8, $"{label}_t8");
        }

        var lhs = Coerce8(ctx.Resolve(lhsName), lhsName);
        var rhs = Coerce8(ctx.Resolve(rhsName), rhsName);

        // C = lhs >= rhs (unsigned).
        var c = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntUGE, lhs, rhs, "cmp_c");
        var diff = ctx.Builder.BuildSub(lhs, rhs, "cmp_diff");
        // Z = diff == 0.
        var z = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, diff,
            LLVMValueRef.CreateConstInt(i8, 0, false), "cmp_z");
        // N = bit 7 of diff.
        var nMask = ctx.Builder.BuildAnd(diff,
            LLVMValueRef.CreateConstInt(i8, 0x80, false), "cmp_n_mask");
        var n = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, nMask,
            LLVMValueRef.CreateConstInt(i8, 0, false), "cmp_n");

        CpsrHelpers.SetStatusFlag(ctx, "P", "Z", z);
        CpsrHelpers.SetStatusFlag(ctx, "P", "N", n);
        CpsrHelpers.SetStatusFlag(ctx, "P", "C", c);
    }
}

/// <summary>
/// BIT: N=rhs.7, V=rhs.6, Z=(A &amp; rhs)==0. Doesn't modify A.
/// </summary>
internal sealed class MosBit : IMicroOpEmitter
{
    public string OpName => "mos_bit";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var rhsName = step.Raw.GetProperty("rhs").GetString()!;
        var i8 = LLVMTypeRef.Int8;

        var rhsRaw = ctx.Resolve(rhsName);
        var rhs = rhsRaw.TypeOf.IntWidth == 8 ? rhsRaw
            : rhsRaw.TypeOf.IntWidth < 8
                ? ctx.Builder.BuildZExt(rhsRaw, i8, $"{rhsName}_z8")
                : ctx.Builder.BuildTrunc(rhsRaw, i8, $"{rhsName}_t8");

        var nMask = ctx.Builder.BuildAnd(rhs,
            LLVMValueRef.CreateConstInt(i8, 0x80, false), "bit_n_mask");
        var n = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, nMask,
            LLVMValueRef.CreateConstInt(i8, 0, false), "bit_n");

        var vMask = ctx.Builder.BuildAnd(rhs,
            LLVMValueRef.CreateConstInt(i8, 0x40, false), "bit_v_mask");
        var v = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, vMask,
            LLVMValueRef.CreateConstInt(i8, 0, false), "bit_v");

        var a = Mos6502Emitters.ReadGpr(ctx, "A", "bit_a");
        var andVal = ctx.Builder.BuildAnd(a, rhs, "bit_and");
        var z = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, andVal,
            LLVMValueRef.CreateConstInt(i8, 0, false), "bit_z");

        CpsrHelpers.SetStatusFlag(ctx, "P", "N", n);
        CpsrHelpers.SetStatusFlag(ctx, "P", "V", v);
        CpsrHelpers.SetStatusFlag(ctx, "P", "Z", z);
    }
}

// ============================================================================
// Control flow
// ============================================================================

/// <summary>
/// JMP indirect: read 16-bit pointer at $nnnn, jump to *ptr. Reproduces
/// the famous 6502 page-boundary bug: when ptr_low == 0xFF, ptr_high
/// is read from <c>(ptr &amp; 0xFF00)</c> not ptr+1.
///
/// Output: i16 PC target. Caller is responsible for writing PC.
/// </summary>
internal sealed class MosJmpIndirect : IMicroOpEmitter
{
    public string OpName => "mos_jmp_indirect";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var outName = StandardEmitters.GetOut(step.Raw);
        var i8 = LLVMTypeRef.Int8;
        var i16 = LLVMTypeRef.Int16;
        var i32 = LLVMTypeRef.Int32;

        var ptr16 = Mos6502Emitters.FetchImm16(ctx, "jmpi_ptr");
        var ptr32 = ctx.Builder.BuildZExt(ptr16, i32, "jmpi_ptr32");
        var lo = Mos6502Emitters.BusRead8(ctx, ptr32, "jmpi_lo");

        // ptr_high = (ptr & 0xFF00) | ((ptr + 1) & 0x00FF)  — page-bug.
        var hiAddr16 = ctx.Builder.BuildOr(
            ctx.Builder.BuildAnd(ptr16,
                LLVMValueRef.CreateConstInt(i16, 0xFF00, false), "jmpi_hi_pg"),
            ctx.Builder.BuildAnd(
                ctx.Builder.BuildAdd(ptr16,
                    LLVMValueRef.CreateConstInt(i16, 1, false), "jmpi_ptr1"),
                LLVMValueRef.CreateConstInt(i16, 0x00FF, false), "jmpi_hi_off"),
            "jmpi_hi_addr");
        var hiAddr32 = ctx.Builder.BuildZExt(hiAddr16, i32, "jmpi_hi32");
        var hi = Mos6502Emitters.BusRead8(ctx, hiAddr32, "jmpi_hi");

        var loZ = ctx.Builder.BuildZExt(lo, i16, "jmpi_loz");
        var hiZ = ctx.Builder.BuildZExt(hi, i16, "jmpi_hiz");
        var hiSh = ctx.Builder.BuildShl(hiZ,
            LLVMValueRef.CreateConstInt(i16, 8, false), "jmpi_hi_shl");
        var target = ctx.Builder.BuildOr(hiSh, loZ, outName);
        ctx.Values[outName] = target;
    }
}

/// <summary>
/// Conditional branch with relative i8 offset. Reads imm8, sign-extends
/// to i16, and if the cond holds: PC = PC + sext(off). The PC-advance for
/// the imm8 fetch is automatic (FetchImm8 advances by 1). Cycle-cost
/// adjustment for the taken / page-cross variants is done at the C# layer.
///
/// JSON shape: <c>{ "cond_reg":"P", "cond_flag":"N", "cond_value":1 }</c>.
/// </summary>
internal sealed class MosBranchRel : IMicroOpEmitter
{
    public string OpName => "mos_branch_rel";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i8 = LLVMTypeRef.Int8;
        var i16 = LLVMTypeRef.Int16;

        var reg     = step.Raw.GetProperty("cond_reg").GetString()!;
        var flag    = step.Raw.GetProperty("cond_flag").GetString()!;
        int wantBit = step.Raw.GetProperty("cond_value").GetInt32() & 1;

        var off8 = Mos6502Emitters.FetchImm8(ctx, "br_off"); // i8
        // Read flag, compare to wanted value.
        var flagBit = CpsrHelpers.ReadStatusFlag(ctx, reg, flag); // i32 0/1
        var wantI = ctx.ConstU32((uint)wantBit);
        var taken = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, flagBit, wantI, "br_taken");

        // Compute target PC = PC + sext(off).
        var pcPtr = ctx.GepStatusRegister("PC");
        var pc16  = ctx.Builder.BuildLoad2(i16, pcPtr, "br_pc");
        var off16 = ctx.Builder.BuildSExt(off8, i16, "br_off16");
        var target = ctx.Builder.BuildAdd(pc16, off16, "br_target");

        // Select between target and current PC.
        var newPc = ctx.Builder.BuildSelect(taken, target, pc16, "br_new_pc");
        ctx.Builder.BuildStore(newPc, pcPtr);

        // Mark PC-written when taken so the executor knows to honor the
        // branch (for block-JIT pipelines that gate on PcWritten).
        var flagSlot = ctx.Layout.GepPcWritten(ctx.Builder, ctx.StatePtr);
        var oldFlag = ctx.Builder.BuildLoad2(i8, flagSlot, "br_pcw_old");
        var pcw = ctx.Builder.BuildSelect(taken,
            LLVMValueRef.CreateConstInt(i8, 1, false), oldFlag, "br_pcw_new");
        ctx.Builder.BuildStore(pcw, flagSlot);
    }
}

/// <summary>
/// JSR abs: push (PC-1) high then low, then PC = imm16. PC at fetch-time
/// points one before the next instruction (the canonical 6502 quirk).
/// SP = $0100 + SP register (8-bit logical SP, post-decrement).
/// </summary>
internal sealed class MosJsr : IMicroOpEmitter
{
    public string OpName => "mos_jsr";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i8 = LLVMTypeRef.Int8;
        var i16 = LLVMTypeRef.Int16;
        var i32 = LLVMTypeRef.Int32;

        // Read low byte of target first (advances PC by 1). Then PC-1
        // is "PC after low fetch" - 1, which equals "address of high
        // byte". 6502 behaviour: PC pushed = address-of-high-byte.
        // Equivalently: pushed PC = (PC of JSR) + 2 = PC-after-low.
        // Easier path: fetch low + high (FetchImm16), compute PC-1,
        // push high then low, then PC = target.
        var target = Mos6502Emitters.FetchImm16(ctx, "jsr_t"); // PC now = JSR_addr+3
        var pcPtr  = ctx.GepStatusRegister("PC");
        var pcAfter = ctx.Builder.BuildLoad2(i16, pcPtr, "jsr_pc_after");
        var pcMinus1 = ctx.Builder.BuildSub(pcAfter,
            LLVMValueRef.CreateConstInt(i16, 1, false), "jsr_pcm1");

        // Push high then low. Use 6502 push convention via the spec's
        // SP status register: write at $0100|SP, then post-decrement SP.
        var spPtr = ctx.GepStatusRegister("SP");
        var hi8 = ctx.Builder.BuildTrunc(
            ctx.Builder.BuildLShr(pcMinus1,
                LLVMValueRef.CreateConstInt(i16, 8, false), "jsr_hi_sh"),
            i8, "jsr_hi8");
        var lo8 = ctx.Builder.BuildTrunc(pcMinus1, i8, "jsr_lo8");

        // Push high.
        var sp1 = ctx.Builder.BuildLoad2(i8, spPtr, "jsr_sp1");
        var sp1Z = ctx.Builder.BuildZExt(sp1, i32, "jsr_sp1z");
        var addr1 = ctx.Builder.BuildOr(sp1Z, ctx.ConstU32(0x100), "jsr_addr1");
        Mos6502Emitters.BusWrite8(ctx, addr1, hi8);
        var spDec1 = ctx.Builder.BuildSub(sp1,
            LLVMValueRef.CreateConstInt(i8, 1, false), "jsr_sp1d");
        ctx.Builder.BuildStore(spDec1, spPtr);

        // Push low.
        var sp2 = ctx.Builder.BuildLoad2(i8, spPtr, "jsr_sp2");
        var sp2Z = ctx.Builder.BuildZExt(sp2, i32, "jsr_sp2z");
        var addr2 = ctx.Builder.BuildOr(sp2Z, ctx.ConstU32(0x100), "jsr_addr2");
        Mos6502Emitters.BusWrite8(ctx, addr2, lo8);
        var spDec2 = ctx.Builder.BuildSub(sp2,
            LLVMValueRef.CreateConstInt(i8, 1, false), "jsr_sp2d");
        ctx.Builder.BuildStore(spDec2, spPtr);

        // PC = target.
        ctx.Builder.BuildStore(target, pcPtr);

        // Mark PC-written.
        var pcwSlot = ctx.Layout.GepPcWritten(ctx.Builder, ctx.StatePtr);
        ctx.Builder.BuildStore(LLVMValueRef.CreateConstInt(i8, 1, false), pcwSlot);
    }
}

/// <summary>
/// RTS: pop low, pop high, PC = ((high &lt;&lt; 8) | low) + 1. Pre-increment SP
/// then read at $0100|SP, twice.
/// </summary>
internal sealed class MosRts : IMicroOpEmitter
{
    public string OpName => "mos_rts";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i8 = LLVMTypeRef.Int8;
        var i16 = LLVMTypeRef.Int16;
        var i32 = LLVMTypeRef.Int32;
        var spPtr = ctx.GepStatusRegister("SP");
        var pcPtr = ctx.GepStatusRegister("PC");

        // Pop low.
        var sp1 = ctx.Builder.BuildLoad2(i8, spPtr, "rts_sp1");
        var sp1Inc = ctx.Builder.BuildAdd(sp1,
            LLVMValueRef.CreateConstInt(i8, 1, false), "rts_sp1i");
        ctx.Builder.BuildStore(sp1Inc, spPtr);
        var sp1Z = ctx.Builder.BuildZExt(sp1Inc, i32, "rts_sp1z");
        var addr1 = ctx.Builder.BuildOr(sp1Z, ctx.ConstU32(0x100), "rts_addr1");
        var lo = Mos6502Emitters.BusRead8(ctx, addr1, "rts_lo");

        // Pop high.
        var sp2 = ctx.Builder.BuildLoad2(i8, spPtr, "rts_sp2");
        var sp2Inc = ctx.Builder.BuildAdd(sp2,
            LLVMValueRef.CreateConstInt(i8, 1, false), "rts_sp2i");
        ctx.Builder.BuildStore(sp2Inc, spPtr);
        var sp2Z = ctx.Builder.BuildZExt(sp2Inc, i32, "rts_sp2z");
        var addr2 = ctx.Builder.BuildOr(sp2Z, ctx.ConstU32(0x100), "rts_addr2");
        var hi = Mos6502Emitters.BusRead8(ctx, addr2, "rts_hi");

        var loZ = ctx.Builder.BuildZExt(lo, i16, "rts_loz");
        var hiZ = ctx.Builder.BuildZExt(hi, i16, "rts_hiz");
        var hiSh = ctx.Builder.BuildShl(hiZ,
            LLVMValueRef.CreateConstInt(i16, 8, false), "rts_hi_shl");
        var target = ctx.Builder.BuildOr(hiSh, loZ, "rts_t");
        var newPc = ctx.Builder.BuildAdd(target,
            LLVMValueRef.CreateConstInt(i16, 1, false), "rts_pc");
        ctx.Builder.BuildStore(newPc, pcPtr);

        var pcwSlot = ctx.Layout.GepPcWritten(ctx.Builder, ctx.StatePtr);
        ctx.Builder.BuildStore(LLVMValueRef.CreateConstInt(i8, 1, false), pcwSlot);
    }
}

/// <summary>
/// RTI: pop P (force U=1, B=0 per emulator convention; NES SetFlag in
/// LegacyCpu writes only NVDIZC bits — bits 4 and 5 are ignored on pull),
/// pop low, pop high, PC = (high &lt;&lt; 8) | low (no +1 like RTS).
/// </summary>
internal sealed class MosRti : IMicroOpEmitter
{
    public string OpName => "mos_rti";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i8 = LLVMTypeRef.Int8;
        var i16 = LLVMTypeRef.Int16;
        var i32 = LLVMTypeRef.Int32;
        var spPtr = ctx.GepStatusRegister("SP");
        var pcPtr = ctx.GepStatusRegister("PC");
        var pPtr  = ctx.GepStatusRegister("P");

        // Pop P: pre-increment SP, read at $0100|SP, write to P with
        // bits 4 (B) cleared and bit 5 (U) set. The 6502 PLP/RTI keep
        // those two bits 'soft' — most emulators just OR 0x20, AND ~0x10.
        var sp1 = ctx.Builder.BuildLoad2(i8, spPtr, "rti_sp1");
        var sp1Inc = ctx.Builder.BuildAdd(sp1,
            LLVMValueRef.CreateConstInt(i8, 1, false), "rti_sp1i");
        ctx.Builder.BuildStore(sp1Inc, spPtr);
        var sp1Z = ctx.Builder.BuildZExt(sp1Inc, i32, "rti_sp1z");
        var addr1 = ctx.Builder.BuildOr(sp1Z, ctx.ConstU32(0x100), "rti_addr1");
        var pulled = Mos6502Emitters.BusRead8(ctx, addr1, "rti_p");
        var pMasked = ctx.Builder.BuildAnd(pulled,
            LLVMValueRef.CreateConstInt(i8, 0xEF, false), "rti_p_b0");
        var pNew = ctx.Builder.BuildOr(pMasked,
            LLVMValueRef.CreateConstInt(i8, 0x20, false), "rti_p_u1");
        ctx.Builder.BuildStore(pNew, pPtr);

        // Pop PC low.
        var sp2 = ctx.Builder.BuildLoad2(i8, spPtr, "rti_sp2");
        var sp2Inc = ctx.Builder.BuildAdd(sp2,
            LLVMValueRef.CreateConstInt(i8, 1, false), "rti_sp2i");
        ctx.Builder.BuildStore(sp2Inc, spPtr);
        var sp2Z = ctx.Builder.BuildZExt(sp2Inc, i32, "rti_sp2z");
        var addr2 = ctx.Builder.BuildOr(sp2Z, ctx.ConstU32(0x100), "rti_addr2");
        var lo = Mos6502Emitters.BusRead8(ctx, addr2, "rti_lo");

        // Pop PC high.
        var sp3 = ctx.Builder.BuildLoad2(i8, spPtr, "rti_sp3");
        var sp3Inc = ctx.Builder.BuildAdd(sp3,
            LLVMValueRef.CreateConstInt(i8, 1, false), "rti_sp3i");
        ctx.Builder.BuildStore(sp3Inc, spPtr);
        var sp3Z = ctx.Builder.BuildZExt(sp3Inc, i32, "rti_sp3z");
        var addr3 = ctx.Builder.BuildOr(sp3Z, ctx.ConstU32(0x100), "rti_addr3");
        var hi = Mos6502Emitters.BusRead8(ctx, addr3, "rti_hi");

        var loZ = ctx.Builder.BuildZExt(lo, i16, "rti_loz");
        var hiZ = ctx.Builder.BuildZExt(hi, i16, "rti_hiz");
        var hiSh = ctx.Builder.BuildShl(hiZ,
            LLVMValueRef.CreateConstInt(i16, 8, false), "rti_hi_shl");
        var newPc = ctx.Builder.BuildOr(hiSh, loZ, "rti_pc");
        ctx.Builder.BuildStore(newPc, pcPtr);

        var pcwSlot = ctx.Layout.GepPcWritten(ctx.Builder, ctx.StatePtr);
        ctx.Builder.BuildStore(LLVMValueRef.CreateConstInt(i8, 1, false), pcwSlot);
    }
}

/// <summary>
/// BRK: push (PC+1) high, low; push P with B=1, U=1 forced; set I=1;
/// PC = read16($FFFE/$FFFF) (IRQ vector, shared with /IRQ — B flag in the
/// pushed P distinguishes BRK from hardware IRQ).
/// </summary>
internal sealed class MosBrk : IMicroOpEmitter
{
    public string OpName => "mos_brk";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i8 = LLVMTypeRef.Int8;
        var i16 = LLVMTypeRef.Int16;
        var i32 = LLVMTypeRef.Int32;
        var pcPtr = ctx.GepStatusRegister("PC");
        var spPtr = ctx.GepStatusRegister("SP");
        var pPtr  = ctx.GepStatusRegister("P");

        // PC at this point already advanced past the BRK opcode (the
        // executor's per-instr fetch did opcode++ and steps run after).
        // BRK pushes PC+1 (canonical 6502 — there is a dummy PC++ to
        // skip the signature byte).
        var pc = ctx.Builder.BuildLoad2(i16, pcPtr, "brk_pc");
        var pcPlus1 = ctx.Builder.BuildAdd(pc,
            LLVMValueRef.CreateConstInt(i16, 1, false), "brk_pcp1");

        var hi8 = ctx.Builder.BuildTrunc(
            ctx.Builder.BuildLShr(pcPlus1,
                LLVMValueRef.CreateConstInt(i16, 8, false), "brk_hi_sh"),
            i8, "brk_hi8");
        var lo8 = ctx.Builder.BuildTrunc(pcPlus1, i8, "brk_lo8");

        // Push high.
        var sp1 = ctx.Builder.BuildLoad2(i8, spPtr, "brk_sp1");
        var sp1Z = ctx.Builder.BuildZExt(sp1, i32, "brk_sp1z");
        var a1 = ctx.Builder.BuildOr(sp1Z, ctx.ConstU32(0x100), "brk_a1");
        Mos6502Emitters.BusWrite8(ctx, a1, hi8);
        ctx.Builder.BuildStore(
            ctx.Builder.BuildSub(sp1, LLVMValueRef.CreateConstInt(i8, 1, false), "brk_sp1d"),
            spPtr);

        // Push low.
        var sp2 = ctx.Builder.BuildLoad2(i8, spPtr, "brk_sp2");
        var sp2Z = ctx.Builder.BuildZExt(sp2, i32, "brk_sp2z");
        var a2 = ctx.Builder.BuildOr(sp2Z, ctx.ConstU32(0x100), "brk_a2");
        Mos6502Emitters.BusWrite8(ctx, a2, lo8);
        ctx.Builder.BuildStore(
            ctx.Builder.BuildSub(sp2, LLVMValueRef.CreateConstInt(i8, 1, false), "brk_sp2d"),
            spPtr);

        // Push P with B=1 U=1 forced.
        var p = ctx.Builder.BuildLoad2(i8, pPtr, "brk_p");
        var pSt = ctx.Builder.BuildOr(p,
            LLVMValueRef.CreateConstInt(i8, 0x30, false), "brk_p_or");
        var sp3 = ctx.Builder.BuildLoad2(i8, spPtr, "brk_sp3");
        var sp3Z = ctx.Builder.BuildZExt(sp3, i32, "brk_sp3z");
        var a3 = ctx.Builder.BuildOr(sp3Z, ctx.ConstU32(0x100), "brk_a3");
        Mos6502Emitters.BusWrite8(ctx, a3, pSt);
        ctx.Builder.BuildStore(
            ctx.Builder.BuildSub(sp3, LLVMValueRef.CreateConstInt(i8, 1, false), "brk_sp3d"),
            spPtr);

        // Set I in P.
        CpsrHelpers.SetStatusFlag(ctx, "P", "I",
            LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, 1, false));

        // PC = read16($FFFE).
        var lo = Mos6502Emitters.BusRead8(ctx, ctx.ConstU32(0xFFFE), "brk_vlo");
        var hi = Mos6502Emitters.BusRead8(ctx, ctx.ConstU32(0xFFFF), "brk_vhi");
        var loZ = ctx.Builder.BuildZExt(lo, i16, "brk_vloz");
        var hiZ = ctx.Builder.BuildZExt(hi, i16, "brk_vhiz");
        var hiSh = ctx.Builder.BuildShl(hiZ,
            LLVMValueRef.CreateConstInt(i16, 8, false), "brk_vhi_shl");
        var v = ctx.Builder.BuildOr(hiSh, loZ, "brk_vec");
        ctx.Builder.BuildStore(v, pcPtr);

        var pcwSlot = ctx.Layout.GepPcWritten(ctx.Builder, ctx.StatePtr);
        ctx.Builder.BuildStore(LLVMValueRef.CreateConstInt(i8, 1, false), pcwSlot);
    }
}

/// <summary>
/// KIL / STP / JAM — undocumented 6502 halt opcode. Real hardware locks
/// the CPU until reset. nestest doesn't execute these and blargg
/// cpu_test5 doesn't either, so we treat them as a no-op (the simplest
/// correct behaviour for non-test ROMs that accidentally land here).
/// Documenting a real halt would require either an infinite loop in IR
/// or a host extern; not worth the complexity.
/// </summary>
internal sealed class MosKil : IMicroOpEmitter
{
    public string OpName => "mos_kil";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        // intentional no-op
    }
}

// ============================================================================
// cc=10 read-modify-write (ASL / LSR / ROL / ROR / INC / DEC) — combined
// load + transform + store-back so the effective address (which depends
// on PC-advancing imm fetches) is computed once.
//
// JSON shape: <c>{ "op": "mos_rmw_cc10", "field": "bbb", "kind":
//                  "asl"|"lsr"|"rol"|"ror"|"inc"|"dec" }</c>
//
// bbb=010 (accumulator) is honoured for the shift/rotate kinds (asl/lsr/
// rol/ror); INC/DEC only apply to memory modes (1/3/5/7) so the
// accumulator path is treated as no-op for them.
//
// All variants update Z and N from the result. ASL/LSR/ROL/ROR update
// C from the bit shifted out. INC/DEC do not touch C.
// ============================================================================

internal sealed class MosRmwCc10 : IMicroOpEmitter
{
    public string OpName => "mos_rmw_cc10";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var fieldName = step.Raw.GetProperty("field").GetString()!;
        var kind      = step.Raw.GetProperty("kind").GetString()!;
        var i8 = LLVMTypeRef.Int8;
        var i32 = LLVMTypeRef.Int32;

        var field = ctx.Resolve(fieldName);

        // The "load → transform → store-back" sequence goes inside each
        // bbb arm to keep PC effects local to the chosen mode.
        var endBB = ctx.Function.AppendBasicBlock("rmw_end");
        var defaultBB = ctx.Function.AppendBasicBlock("rmw_default");
        var arms = new LLVMBasicBlockRef[8];
        for (int i = 0; i < 8; i++) arms[i] = ctx.Function.AppendBasicBlock($"rmw_b{i}");

        var sw = ctx.Builder.BuildSwitch(field, defaultBB, 8);
        for (int i = 0; i < 8; i++)
            sw.AddCase(LLVMValueRef.CreateConstInt(i32, (uint)i, false), arms[i]);

        for (int bbb = 0; bbb < 8; bbb++)
        {
            ctx.Builder.PositionAtEnd(arms[bbb]);
            EmitArm(ctx, bbb, kind);
            ctx.Builder.BuildBr(endBB);
        }
        ctx.Builder.PositionAtEnd(defaultBB);
        ctx.Builder.BuildBr(endBB);

        ctx.Builder.PositionAtEnd(endBB);
    }

    private static void EmitArm(EmitContext ctx, int bbb, string kind)
    {
        var i8 = LLVMTypeRef.Int8;
        var i32 = LLVMTypeRef.Int32;

        // Decide if this arm is a no-op:
        //   - bbb=0/2/4/6 (illegal modes for cc=10) — no-op
        //   - INC/DEC with bbb=2 (accumulator) — no-op (INC/DEC have no acc form)
        bool isAccumulator = bbb == 2;
        bool isMemoryMode  = bbb == 1 || bbb == 3 || bbb == 5 || bbb == 7;
        bool isShiftKind   = kind is "asl" or "lsr" or "rol" or "ror";
        bool isIncDecKind  = kind is "inc" or "dec";

        if (!isAccumulator && !isMemoryMode) return;
        if (isAccumulator && isIncDecKind) return;

        // Load source: A or memory[addr].
        LLVMValueRef src;
        LLVMValueRef? addr32 = null;
        if (isAccumulator)
        {
            src = Mos6502Emitters.ReadGpr(ctx, "A", $"rmw_b{bbb}_a");
        }
        else
        {
            addr32 = Mos6502Emitters.ComputeEffectiveAddrMem(ctx, bbb, swapXy: false, $"rmw_b{bbb}");
            src = Mos6502Emitters.BusRead8(ctx, addr32.Value, $"rmw_b{bbb}_v");
        }

        // Transform.
        LLVMValueRef result;
        bool updatesC = false;
        LLVMValueRef cBit = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, 0, false);
        switch (kind)
        {
            case "asl":
            {
                var top = ctx.Builder.BuildAnd(
                    ctx.Builder.BuildLShr(src,
                        LLVMValueRef.CreateConstInt(i8, 7, false), $"rmw_b{bbb}_top_sh"),
                    LLVMValueRef.CreateConstInt(i8, 1, false), $"rmw_b{bbb}_top");
                result = ctx.Builder.BuildShl(src,
                    LLVMValueRef.CreateConstInt(i8, 1, false), $"rmw_b{bbb}_asl");
                cBit = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, top,
                    LLVMValueRef.CreateConstInt(i8, 0, false), $"rmw_b{bbb}_c");
                updatesC = true;
                break;
            }
            case "lsr":
            {
                var low = ctx.Builder.BuildAnd(src,
                    LLVMValueRef.CreateConstInt(i8, 1, false), $"rmw_b{bbb}_low");
                result = ctx.Builder.BuildLShr(src,
                    LLVMValueRef.CreateConstInt(i8, 1, false), $"rmw_b{bbb}_lsr");
                cBit = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, low,
                    LLVMValueRef.CreateConstInt(i8, 0, false), $"rmw_b{bbb}_c");
                updatesC = true;
                break;
            }
            case "rol":
            {
                var top = ctx.Builder.BuildAnd(
                    ctx.Builder.BuildLShr(src,
                        LLVMValueRef.CreateConstInt(i8, 7, false), $"rmw_b{bbb}_top_sh"),
                    LLVMValueRef.CreateConstInt(i8, 1, false), $"rmw_b{bbb}_top");
                // Old C in i8 0/1.
                var cIn32 = Mos6502Emitters.ReadFlag(ctx, "C");
                var cIn8 = ctx.Builder.BuildTrunc(cIn32, i8, $"rmw_b{bbb}_cin");
                var shl = ctx.Builder.BuildShl(src,
                    LLVMValueRef.CreateConstInt(i8, 1, false), $"rmw_b{bbb}_rol_sh");
                result = ctx.Builder.BuildOr(shl, cIn8, $"rmw_b{bbb}_rol");
                cBit = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, top,
                    LLVMValueRef.CreateConstInt(i8, 0, false), $"rmw_b{bbb}_c");
                updatesC = true;
                break;
            }
            case "ror":
            {
                var low = ctx.Builder.BuildAnd(src,
                    LLVMValueRef.CreateConstInt(i8, 1, false), $"rmw_b{bbb}_low");
                var cIn32 = Mos6502Emitters.ReadFlag(ctx, "C");
                var cIn8 = ctx.Builder.BuildTrunc(cIn32, i8, $"rmw_b{bbb}_cin");
                var cInTop = ctx.Builder.BuildShl(cIn8,
                    LLVMValueRef.CreateConstInt(i8, 7, false), $"rmw_b{bbb}_cin_top");
                var shr = ctx.Builder.BuildLShr(src,
                    LLVMValueRef.CreateConstInt(i8, 1, false), $"rmw_b{bbb}_ror_sh");
                result = ctx.Builder.BuildOr(shr, cInTop, $"rmw_b{bbb}_ror");
                cBit = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, low,
                    LLVMValueRef.CreateConstInt(i8, 0, false), $"rmw_b{bbb}_c");
                updatesC = true;
                break;
            }
            case "inc":
            {
                result = ctx.Builder.BuildAdd(src,
                    LLVMValueRef.CreateConstInt(i8, 1, false), $"rmw_b{bbb}_inc");
                break;
            }
            case "dec":
            {
                result = ctx.Builder.BuildSub(src,
                    LLVMValueRef.CreateConstInt(i8, 1, false), $"rmw_b{bbb}_dec");
                break;
            }
            default:
                throw new NotSupportedException($"mos_rmw_cc10: unknown kind '{kind}'");
        }

        // Store back.
        if (isAccumulator)
        {
            Mos6502Emitters.WriteGpr(ctx, "A", result);
        }
        else
        {
            Mos6502Emitters.BusWrite8(ctx, addr32!.Value, result);
        }

        // Update Z, N from result.
        var z = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, result,
            LLVMValueRef.CreateConstInt(i8, 0, false), $"rmw_b{bbb}_z");
        var nMask = ctx.Builder.BuildAnd(result,
            LLVMValueRef.CreateConstInt(i8, 0x80, false), $"rmw_b{bbb}_n_mask");
        var n = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, nMask,
            LLVMValueRef.CreateConstInt(i8, 0, false), $"rmw_b{bbb}_n");
        CpsrHelpers.SetStatusFlag(ctx, "P", "Z", z);
        CpsrHelpers.SetStatusFlag(ctx, "P", "N", n);
        if (updatesC)
            CpsrHelpers.SetStatusFlag(ctx, "P", "C", cBit);
    }
}

// ============================================================================
// cc=11 unofficial RMW + ALU combos (SLO/RLA/SRE/RRA/SAX/LAX/DCP/ISC)
//
// JSON shape: <c>{ "op": "mos_unofficial_cc11", "field": "bbb", "kind":
//                  "slo"|"rla"|"sre"|"rra"|"sax"|"lax"|"dcp"|"isc" }</c>
//
// Per-kind semantics:
//   SLO: ASL mem; A |= mem(new); set NZ from A; set C from old bit 7
//   RLA: ROL mem; A &= mem(new); set NZ from A; set C from old bit 7
//   SRE: LSR mem; A ^= mem(new); set NZ from A; set C from old bit 0
//   RRA: ROR mem; A += mem(new) + C  (full ADC after ROR)
//   SAX: store (A & X) to addr; no flag changes; bbb=010 (#imm) is no-op
//   LAX: load mem; A = X = mem; set NZ; bbb=010 (#imm) does FetchImm8
//   DCP: DEC mem; CMP A vs mem(new); set NZC
//   ISC: INC mem; SBC A by mem(new); set NVZC
//
// bbb path mapping mirrors mos_load_operand_cc01 (cc=01-style):
//   000=(zp,X), 001=zp, 011=abs, 100=(zp),Y, 101=zp,X, 110=abs,Y, 111=abs,X.
// bbb=010 (#imm) is intercepted by the mask-0xFF unofficial entries above
// for most kinds; LAX is the exception and treats bbb=010 as #imm.
// ============================================================================

internal sealed class MosUnofficialCc11 : IMicroOpEmitter
{
    public string OpName => "mos_unofficial_cc11";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var fieldName = step.Raw.GetProperty("field").GetString()!;
        var kind      = step.Raw.GetProperty("kind").GetString()!;
        var i32 = LLVMTypeRef.Int32;

        var field = ctx.Resolve(fieldName);

        var endBB = ctx.Function.AppendBasicBlock("u11_end");
        var defaultBB = ctx.Function.AppendBasicBlock("u11_default");
        var arms = new LLVMBasicBlockRef[8];
        for (int i = 0; i < 8; i++) arms[i] = ctx.Function.AppendBasicBlock($"u11_b{i}");

        var sw = ctx.Builder.BuildSwitch(field, defaultBB, 8);
        for (int i = 0; i < 8; i++)
            sw.AddCase(LLVMValueRef.CreateConstInt(i32, (uint)i, false), arms[i]);

        for (int bbb = 0; bbb < 8; bbb++)
        {
            ctx.Builder.PositionAtEnd(arms[bbb]);
            EmitArm(ctx, bbb, kind);
            ctx.Builder.BuildBr(endBB);
        }
        ctx.Builder.PositionAtEnd(defaultBB);
        ctx.Builder.BuildBr(endBB);

        ctx.Builder.PositionAtEnd(endBB);
    }

    private static void EmitArm(EmitContext ctx, int bbb, string kind)
    {
        var i8 = LLVMTypeRef.Int8;
        var i32 = LLVMTypeRef.Int32;

        // bbb=010 (#imm) special handling — only LAX is meaningful here
        // (intercepted by mask-0xFF entries for SLO/RLA/SRE/RRA/DCP/ISC/SAX,
        // but if it does fall through we just no-op to avoid PC corruption).
        if (bbb == 2)
        {
            if (kind == "lax")
            {
                var v = Mos6502Emitters.FetchImm8(ctx, "lax_imm");
                Mos6502Emitters.WriteGpr(ctx, "A", v);
                Mos6502Emitters.WriteGpr(ctx, "X", v);
                var z = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, v,
                    LLVMValueRef.CreateConstInt(i8, 0, false), "lax_z");
                var nM = ctx.Builder.BuildAnd(v,
                    LLVMValueRef.CreateConstInt(i8, 0x80, false), "lax_n_m");
                var n = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, nM,
                    LLVMValueRef.CreateConstInt(i8, 0, false), "lax_n");
                CpsrHelpers.SetStatusFlag(ctx, "P", "Z", z);
                CpsrHelpers.SetStatusFlag(ctx, "P", "N", n);
            }
            return;
        }

        // Compute effective address once. LAX and SAX are special: bbb=101
        // (zp,X slot) and bbb=111 (abs,X slot) actually use Y as the index
        // register on the real 6502 — this is the same swap_xy quirk that
        // STX/LDX use in the cc=10 family. Concretely:
        //   $B7 LAX zp,Y, $97 SAX zp,Y   (bbb=101)
        //   $BF LAX abs,Y                 (bbb=111)
        //   ($9F SAX abs,Y is intercepted by the AHX_9F mask-0xFF entry,
        //    so we never reach here with kind=sax bbb=111 in practice — but
        //    swap unconditionally for symmetry / future correctness.)
        bool useY = (kind == "lax" || kind == "sax") && (bbb == 5 || bbb == 7);
        var addr32 = useY
            ? ComputeCc11AddrSwapY(ctx, bbb, $"u11_b{bbb}")
            : Mos6502Emitters.ComputeEffectiveAddrCc01(ctx, bbb, $"u11_b{bbb}");

        switch (kind)
        {
            case "sax":
            {
                // A & X stored.
                var a = Mos6502Emitters.ReadGpr(ctx, "A", $"u11_b{bbb}_a");
                var x = Mos6502Emitters.ReadGpr(ctx, "X", $"u11_b{bbb}_x");
                var ax = ctx.Builder.BuildAnd(a, x, $"u11_b{bbb}_ax");
                Mos6502Emitters.BusWrite8(ctx, addr32, ax);
                break;
            }
            case "lax":
            {
                var v = Mos6502Emitters.BusRead8(ctx, addr32, $"u11_b{bbb}_v");
                Mos6502Emitters.WriteGpr(ctx, "A", v);
                Mos6502Emitters.WriteGpr(ctx, "X", v);
                var z = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, v,
                    LLVMValueRef.CreateConstInt(i8, 0, false), "lax_z");
                var nM = ctx.Builder.BuildAnd(v,
                    LLVMValueRef.CreateConstInt(i8, 0x80, false), "lax_n_m");
                var n = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, nM,
                    LLVMValueRef.CreateConstInt(i8, 0, false), "lax_n");
                CpsrHelpers.SetStatusFlag(ctx, "P", "Z", z);
                CpsrHelpers.SetStatusFlag(ctx, "P", "N", n);
                break;
            }
            case "slo":
            {
                var v = Mos6502Emitters.BusRead8(ctx, addr32, $"u11_b{bbb}_v");
                var top = ctx.Builder.BuildAnd(
                    ctx.Builder.BuildLShr(v,
                        LLVMValueRef.CreateConstInt(i8, 7, false), "slo_top_sh"),
                    LLVMValueRef.CreateConstInt(i8, 1, false), "slo_top");
                var shifted = ctx.Builder.BuildShl(v,
                    LLVMValueRef.CreateConstInt(i8, 1, false), "slo_shl");
                Mos6502Emitters.BusWrite8(ctx, addr32, shifted);
                var c = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, top,
                    LLVMValueRef.CreateConstInt(i8, 0, false), "slo_c");
                var a = Mos6502Emitters.ReadGpr(ctx, "A", "slo_a");
                var rA = ctx.Builder.BuildOr(a, shifted, "slo_or");
                Mos6502Emitters.WriteGpr(ctx, "A", rA);
                EmitNZ(ctx, rA, "slo");
                CpsrHelpers.SetStatusFlag(ctx, "P", "C", c);
                break;
            }
            case "rla":
            {
                var v = Mos6502Emitters.BusRead8(ctx, addr32, $"u11_b{bbb}_v");
                var top = ctx.Builder.BuildAnd(
                    ctx.Builder.BuildLShr(v,
                        LLVMValueRef.CreateConstInt(i8, 7, false), "rla_top_sh"),
                    LLVMValueRef.CreateConstInt(i8, 1, false), "rla_top");
                var cIn32 = Mos6502Emitters.ReadFlag(ctx, "C");
                var cIn8 = ctx.Builder.BuildTrunc(cIn32, i8, "rla_cin");
                var shifted = ctx.Builder.BuildOr(
                    ctx.Builder.BuildShl(v,
                        LLVMValueRef.CreateConstInt(i8, 1, false), "rla_shl"),
                    cIn8, "rla_rol");
                Mos6502Emitters.BusWrite8(ctx, addr32, shifted);
                var c = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, top,
                    LLVMValueRef.CreateConstInt(i8, 0, false), "rla_c");
                var a = Mos6502Emitters.ReadGpr(ctx, "A", "rla_a");
                var rA = ctx.Builder.BuildAnd(a, shifted, "rla_and");
                Mos6502Emitters.WriteGpr(ctx, "A", rA);
                EmitNZ(ctx, rA, "rla");
                CpsrHelpers.SetStatusFlag(ctx, "P", "C", c);
                break;
            }
            case "sre":
            {
                var v = Mos6502Emitters.BusRead8(ctx, addr32, $"u11_b{bbb}_v");
                var low = ctx.Builder.BuildAnd(v,
                    LLVMValueRef.CreateConstInt(i8, 1, false), "sre_low");
                var shifted = ctx.Builder.BuildLShr(v,
                    LLVMValueRef.CreateConstInt(i8, 1, false), "sre_lsr");
                Mos6502Emitters.BusWrite8(ctx, addr32, shifted);
                var c = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, low,
                    LLVMValueRef.CreateConstInt(i8, 0, false), "sre_c");
                var a = Mos6502Emitters.ReadGpr(ctx, "A", "sre_a");
                var rA = ctx.Builder.BuildXor(a, shifted, "sre_xor");
                Mos6502Emitters.WriteGpr(ctx, "A", rA);
                EmitNZ(ctx, rA, "sre");
                CpsrHelpers.SetStatusFlag(ctx, "P", "C", c);
                break;
            }
            case "rra":
            {
                // ROR mem, then ADC A,mem(new)
                var v = Mos6502Emitters.BusRead8(ctx, addr32, $"u11_b{bbb}_v");
                var low = ctx.Builder.BuildAnd(v,
                    LLVMValueRef.CreateConstInt(i8, 1, false), "rra_low");
                var cIn32 = Mos6502Emitters.ReadFlag(ctx, "C");
                var cIn8 = ctx.Builder.BuildTrunc(cIn32, i8, "rra_cin");
                var cInTop = ctx.Builder.BuildShl(cIn8,
                    LLVMValueRef.CreateConstInt(i8, 7, false), "rra_cin_top");
                var shr = ctx.Builder.BuildLShr(v,
                    LLVMValueRef.CreateConstInt(i8, 1, false), "rra_shr");
                var shifted = ctx.Builder.BuildOr(shr, cInTop, "rra_ror");
                Mos6502Emitters.BusWrite8(ctx, addr32, shifted);
                // First write C from rotate-out (low), so ADC's C-in is the
                // ROR's new carry.
                var newC = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, low,
                    LLVMValueRef.CreateConstInt(i8, 0, false), "rra_c_new");
                CpsrHelpers.SetStatusFlag(ctx, "P", "C", newC);
                // ADC A by shifted.
                EmitAdc(ctx, shifted);
                break;
            }
            case "dcp":
            {
                // DEC mem; CMP A vs mem(new).
                var v = Mos6502Emitters.BusRead8(ctx, addr32, $"u11_b{bbb}_v");
                var dec = ctx.Builder.BuildSub(v,
                    LLVMValueRef.CreateConstInt(i8, 1, false), "dcp_dec");
                Mos6502Emitters.BusWrite8(ctx, addr32, dec);
                var a = Mos6502Emitters.ReadGpr(ctx, "A", "dcp_a");
                var c = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntUGE, a, dec, "dcp_c");
                var diff = ctx.Builder.BuildSub(a, dec, "dcp_diff");
                var z = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, diff,
                    LLVMValueRef.CreateConstInt(i8, 0, false), "dcp_z");
                var nM = ctx.Builder.BuildAnd(diff,
                    LLVMValueRef.CreateConstInt(i8, 0x80, false), "dcp_n_m");
                var n = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, nM,
                    LLVMValueRef.CreateConstInt(i8, 0, false), "dcp_n");
                CpsrHelpers.SetStatusFlag(ctx, "P", "Z", z);
                CpsrHelpers.SetStatusFlag(ctx, "P", "N", n);
                CpsrHelpers.SetStatusFlag(ctx, "P", "C", c);
                break;
            }
            case "isc":
            {
                // INC mem; SBC A by mem(new).
                var v = Mos6502Emitters.BusRead8(ctx, addr32, $"u11_b{bbb}_v");
                var inc = ctx.Builder.BuildAdd(v,
                    LLVMValueRef.CreateConstInt(i8, 1, false), "isc_inc");
                Mos6502Emitters.BusWrite8(ctx, addr32, inc);
                EmitSbc(ctx, inc);
                break;
            }
            default:
                throw new NotSupportedException($"mos_unofficial_cc11: unknown kind '{kind}'");
        }
    }

    // For LAX/SAX with bbb=101 / bbb=111 — swap X for Y as the index register.
    // bbb=101 is zp + Y (8-bit wrap), bbb=111 is abs + Y (16-bit add).
    private static LLVMValueRef ComputeCc11AddrSwapY(EmitContext ctx, int bbb, string label)
    {
        var i16 = LLVMTypeRef.Int16;
        var i32 = LLVMTypeRef.Int32;
        switch (bbb)
        {
            case 5: // zp,Y
            {
                var zp = Mos6502Emitters.FetchImm8(ctx, $"{label}_zp");
                var y  = Mos6502Emitters.ReadGpr(ctx, "Y", $"{label}_y");
                var sum8 = ctx.Builder.BuildAdd(zp, y, $"{label}_zpy");
                return ctx.Builder.BuildZExt(sum8, i32, $"{label}_addr");
            }
            case 7: // abs,Y
            {
                var w = Mos6502Emitters.FetchImm16(ctx, $"{label}_abs");
                var y = Mos6502Emitters.ReadGpr(ctx, "Y", $"{label}_y");
                var yZ = ctx.Builder.BuildZExt(y, i16, $"{label}_yz16");
                var sum16 = ctx.Builder.BuildAdd(w, yZ, $"{label}_addr16");
                return ctx.Builder.BuildZExt(sum16, i32, $"{label}_addr");
            }
            default:
                // Fallback — should be unreachable; caller only uses bbb=5/7.
                return Mos6502Emitters.ComputeEffectiveAddrCc01(ctx, bbb, label);
        }
    }

    private static void EmitNZ(EmitContext ctx, LLVMValueRef v8, string label)
    {
        var i8 = LLVMTypeRef.Int8;
        var z = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, v8,
            LLVMValueRef.CreateConstInt(i8, 0, false), $"{label}_z");
        var nM = ctx.Builder.BuildAnd(v8,
            LLVMValueRef.CreateConstInt(i8, 0x80, false), $"{label}_n_m");
        var n = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, nM,
            LLVMValueRef.CreateConstInt(i8, 0, false), $"{label}_n");
        CpsrHelpers.SetStatusFlag(ctx, "P", "Z", z);
        CpsrHelpers.SetStatusFlag(ctx, "P", "N", n);
    }

    private static void EmitAdc(EmitContext ctx, LLVMValueRef rhs8)
    {
        var i8 = LLVMTypeRef.Int8;
        var i32 = LLVMTypeRef.Int32;
        var aPtr = Lr35902Emitters.LocateRegister(ctx, "A").Ptr;
        var aOld = ctx.Builder.BuildLoad2(i8, aPtr, "rra_adc_a");
        var cIn = Mos6502Emitters.ReadFlag(ctx, "C");
        var aZ = ctx.Builder.BuildZExt(aOld, i32, "rra_adc_az");
        var rZ = ctx.Builder.BuildZExt(rhs8, i32, "rra_adc_rz");
        var sum = ctx.Builder.BuildAdd(
            ctx.Builder.BuildAdd(aZ, rZ, "rra_adc_sum0"), cIn, "rra_adc_sum");
        var sum8 = ctx.Builder.BuildTrunc(sum, i8, "rra_adc_sum8");
        var z = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, sum8,
            LLVMValueRef.CreateConstInt(i8, 0, false), "rra_adc_z");
        var nM = ctx.Builder.BuildAnd(sum8,
            LLVMValueRef.CreateConstInt(i8, 0x80, false), "rra_adc_nm");
        var n = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, nM,
            LLVMValueRef.CreateConstInt(i8, 0, false), "rra_adc_n");
        var cMask = ctx.Builder.BuildAnd(sum, ctx.ConstU32(0x100), "rra_adc_cm");
        var c = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, cMask,
            ctx.ConstU32(0), "rra_adc_c");
        var sum8Z = ctx.Builder.BuildZExt(sum8, i32, "rra_adc_s8z");
        var aXorS = ctx.Builder.BuildXor(aZ, sum8Z, "rra_adc_axs");
        var rXorS = ctx.Builder.BuildXor(rZ, sum8Z, "rra_adc_rxs");
        var both = ctx.Builder.BuildAnd(aXorS, rXorS, "rra_adc_both");
        var bm = ctx.Builder.BuildAnd(both, ctx.ConstU32(0x80), "rra_adc_bm");
        var v = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, bm,
            ctx.ConstU32(0), "rra_adc_v");
        ctx.Builder.BuildStore(sum8, aPtr);
        CpsrHelpers.SetStatusFlag(ctx, "P", "Z", z);
        CpsrHelpers.SetStatusFlag(ctx, "P", "N", n);
        CpsrHelpers.SetStatusFlag(ctx, "P", "C", c);
        CpsrHelpers.SetStatusFlag(ctx, "P", "V", v);
    }

    private static void EmitSbc(EmitContext ctx, LLVMValueRef rhs8)
    {
        var i8 = LLVMTypeRef.Int8;
        var i32 = LLVMTypeRef.Int32;
        var inv = ctx.Builder.BuildXor(rhs8,
            LLVMValueRef.CreateConstInt(i8, 0xFF, false), "isc_inv");
        var aPtr = Lr35902Emitters.LocateRegister(ctx, "A").Ptr;
        var aOld = ctx.Builder.BuildLoad2(i8, aPtr, "isc_a");
        var cIn = Mos6502Emitters.ReadFlag(ctx, "C");
        var aZ = ctx.Builder.BuildZExt(aOld, i32, "isc_az");
        var iZ = ctx.Builder.BuildZExt(inv, i32, "isc_iz");
        var sum = ctx.Builder.BuildAdd(
            ctx.Builder.BuildAdd(aZ, iZ, "isc_sum0"), cIn, "isc_sum");
        var sum8 = ctx.Builder.BuildTrunc(sum, i8, "isc_sum8");
        var z = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, sum8,
            LLVMValueRef.CreateConstInt(i8, 0, false), "isc_z");
        var nM = ctx.Builder.BuildAnd(sum8,
            LLVMValueRef.CreateConstInt(i8, 0x80, false), "isc_nm");
        var n = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, nM,
            LLVMValueRef.CreateConstInt(i8, 0, false), "isc_n");
        var cMask = ctx.Builder.BuildAnd(sum, ctx.ConstU32(0x100), "isc_cm");
        var c = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, cMask,
            ctx.ConstU32(0), "isc_c");
        var sum8Z = ctx.Builder.BuildZExt(sum8, i32, "isc_s8z");
        var aXorS = ctx.Builder.BuildXor(aZ, sum8Z, "isc_axs");
        var iXorS = ctx.Builder.BuildXor(iZ, sum8Z, "isc_ixs");
        var both = ctx.Builder.BuildAnd(aXorS, iXorS, "isc_both");
        var bm = ctx.Builder.BuildAnd(both, ctx.ConstU32(0x80), "isc_bm");
        var v = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, bm,
            ctx.ConstU32(0), "isc_v");
        ctx.Builder.BuildStore(sum8, aPtr);
        CpsrHelpers.SetStatusFlag(ctx, "P", "Z", z);
        CpsrHelpers.SetStatusFlag(ctx, "P", "N", n);
        CpsrHelpers.SetStatusFlag(ctx, "P", "C", c);
        CpsrHelpers.SetStatusFlag(ctx, "P", "V", v);
    }
}

// ============================================================================
// Tiny per-op emitters for irregular #imm unofficial 6502 ops.
// Each fetches its #imm byte, runs a small computation, and updates flags.
// ============================================================================

/// <summary>
/// ANC #imm (0x0B / 0x2B): A = A &amp; imm; set NZ from A; C = N (mirror of bit 7).
/// </summary>
internal sealed class MosAnc : IMicroOpEmitter
{
    public string OpName => "mos_anc";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i8 = LLVMTypeRef.Int8;
        var imm = Mos6502Emitters.FetchImm8(ctx, "anc_imm");
        var a = Mos6502Emitters.ReadGpr(ctx, "A", "anc_a");
        var r = ctx.Builder.BuildAnd(a, imm, "anc_r");
        Mos6502Emitters.WriteGpr(ctx, "A", r);
        var z = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, r,
            LLVMValueRef.CreateConstInt(i8, 0, false), "anc_z");
        var nM = ctx.Builder.BuildAnd(r,
            LLVMValueRef.CreateConstInt(i8, 0x80, false), "anc_nm");
        var n = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, nM,
            LLVMValueRef.CreateConstInt(i8, 0, false), "anc_n");
        CpsrHelpers.SetStatusFlag(ctx, "P", "Z", z);
        CpsrHelpers.SetStatusFlag(ctx, "P", "N", n);
        // C mirrors N.
        CpsrHelpers.SetStatusFlag(ctx, "P", "C", n);
    }
}

/// <summary>
/// ALR #imm (0x4B): A = (A &amp; imm); C = bit 0; A = A &gt;&gt; 1; set NZ.
/// </summary>
internal sealed class MosAlr : IMicroOpEmitter
{
    public string OpName => "mos_alr";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i8 = LLVMTypeRef.Int8;
        var imm = Mos6502Emitters.FetchImm8(ctx, "alr_imm");
        var a = Mos6502Emitters.ReadGpr(ctx, "A", "alr_a");
        var anded = ctx.Builder.BuildAnd(a, imm, "alr_and");
        var lowBit = ctx.Builder.BuildAnd(anded,
            LLVMValueRef.CreateConstInt(i8, 1, false), "alr_low");
        var r = ctx.Builder.BuildLShr(anded,
            LLVMValueRef.CreateConstInt(i8, 1, false), "alr_lsr");
        Mos6502Emitters.WriteGpr(ctx, "A", r);
        var c = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, lowBit,
            LLVMValueRef.CreateConstInt(i8, 0, false), "alr_c");
        var z = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, r,
            LLVMValueRef.CreateConstInt(i8, 0, false), "alr_z");
        var nM = ctx.Builder.BuildAnd(r,
            LLVMValueRef.CreateConstInt(i8, 0x80, false), "alr_nm");
        var n = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, nM,
            LLVMValueRef.CreateConstInt(i8, 0, false), "alr_n");
        CpsrHelpers.SetStatusFlag(ctx, "P", "Z", z);
        CpsrHelpers.SetStatusFlag(ctx, "P", "N", n);
        CpsrHelpers.SetStatusFlag(ctx, "P", "C", c);
    }
}

/// <summary>
/// ARR #imm (0x6B): A = (A &amp; imm) ROR through C. Flag rules are
/// notoriously weird (binary mode: N=A.7, Z=A==0, C=A.6, V=A.5 ^ A.6).
/// </summary>
internal sealed class MosArr : IMicroOpEmitter
{
    public string OpName => "mos_arr";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i8 = LLVMTypeRef.Int8;
        var imm = Mos6502Emitters.FetchImm8(ctx, "arr_imm");
        var a = Mos6502Emitters.ReadGpr(ctx, "A", "arr_a");
        var anded = ctx.Builder.BuildAnd(a, imm, "arr_and");
        var cIn32 = Mos6502Emitters.ReadFlag(ctx, "C");
        var cIn8 = ctx.Builder.BuildTrunc(cIn32, i8, "arr_cin");
        var cInTop = ctx.Builder.BuildShl(cIn8,
            LLVMValueRef.CreateConstInt(i8, 7, false), "arr_cin_top");
        var shr = ctx.Builder.BuildLShr(anded,
            LLVMValueRef.CreateConstInt(i8, 1, false), "arr_shr");
        var r = ctx.Builder.BuildOr(shr, cInTop, "arr_r");
        Mos6502Emitters.WriteGpr(ctx, "A", r);

        var z = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, r,
            LLVMValueRef.CreateConstInt(i8, 0, false), "arr_z");
        var nM = ctx.Builder.BuildAnd(r,
            LLVMValueRef.CreateConstInt(i8, 0x80, false), "arr_nm");
        var n = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, nM,
            LLVMValueRef.CreateConstInt(i8, 0, false), "arr_n");

        var b6 = ctx.Builder.BuildAnd(r,
            LLVMValueRef.CreateConstInt(i8, 0x40, false), "arr_b6");
        var c = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, b6,
            LLVMValueRef.CreateConstInt(i8, 0, false), "arr_c");

        var b5 = ctx.Builder.BuildAnd(r,
            LLVMValueRef.CreateConstInt(i8, 0x20, false), "arr_b5");
        var b5c = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, b5,
            LLVMValueRef.CreateConstInt(i8, 0, false), "arr_b5c");
        var v_arr = ctx.Builder.BuildXor(b5c, c, "arr_v");

        CpsrHelpers.SetStatusFlag(ctx, "P", "Z", z);
        CpsrHelpers.SetStatusFlag(ctx, "P", "N", n);
        CpsrHelpers.SetStatusFlag(ctx, "P", "C", c);
        CpsrHelpers.SetStatusFlag(ctx, "P", "V", v_arr);
    }
}

/// <summary>
/// AXS #imm (0xCB): X = (A &amp; X) - imm, no carry-in; set NZC.
/// </summary>
internal sealed class MosAxs : IMicroOpEmitter
{
    public string OpName => "mos_axs";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i8 = LLVMTypeRef.Int8;
        var i32 = LLVMTypeRef.Int32;
        var imm = Mos6502Emitters.FetchImm8(ctx, "axs_imm");
        var a = Mos6502Emitters.ReadGpr(ctx, "A", "axs_a");
        var x = Mos6502Emitters.ReadGpr(ctx, "X", "axs_x");
        var ax = ctx.Builder.BuildAnd(a, x, "axs_ax");

        var axZ = ctx.Builder.BuildZExt(ax, i32, "axs_axz");
        var iZ = ctx.Builder.BuildZExt(imm, i32, "axs_iz");
        var diff32 = ctx.Builder.BuildSub(axZ, iZ, "axs_diff");
        var diff8 = ctx.Builder.BuildTrunc(diff32, i8, "axs_diff8");

        Mos6502Emitters.WriteGpr(ctx, "X", diff8);

        var c = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntUGE, ax, imm, "axs_c");
        var z = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, diff8,
            LLVMValueRef.CreateConstInt(i8, 0, false), "axs_z");
        var nM = ctx.Builder.BuildAnd(diff8,
            LLVMValueRef.CreateConstInt(i8, 0x80, false), "axs_nm");
        var n = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, nM,
            LLVMValueRef.CreateConstInt(i8, 0, false), "axs_n");
        CpsrHelpers.SetStatusFlag(ctx, "P", "Z", z);
        CpsrHelpers.SetStatusFlag(ctx, "P", "N", n);
        CpsrHelpers.SetStatusFlag(ctx, "P", "C", c);
    }
}

/// <summary>
/// XAA #imm (0x8B, UNSTABLE): real-hardware behaviour depends on
/// internal magic. Most emulators use the simplified A = X &amp; imm; set NZ.
/// </summary>
internal sealed class MosXaa : IMicroOpEmitter
{
    public string OpName => "mos_xaa";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i8 = LLVMTypeRef.Int8;
        var imm = Mos6502Emitters.FetchImm8(ctx, "xaa_imm");
        var x = Mos6502Emitters.ReadGpr(ctx, "X", "xaa_x");
        var r = ctx.Builder.BuildAnd(x, imm, "xaa_r");
        Mos6502Emitters.WriteGpr(ctx, "A", r);
        var z = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, r,
            LLVMValueRef.CreateConstInt(i8, 0, false), "xaa_z");
        var nM = ctx.Builder.BuildAnd(r,
            LLVMValueRef.CreateConstInt(i8, 0x80, false), "xaa_nm");
        var n = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, nM,
            LLVMValueRef.CreateConstInt(i8, 0, false), "xaa_n");
        CpsrHelpers.SetStatusFlag(ctx, "P", "Z", z);
        CpsrHelpers.SetStatusFlag(ctx, "P", "N", n);
    }
}

/// <summary>
/// LAS abs,Y (0xBB): A = X = SP = mem &amp; SP; set NZ. The abs,Y addressing
/// is hand-rolled because LAS isn't part of the cc=11 broad pattern.
/// </summary>
internal sealed class MosLas : IMicroOpEmitter
{
    public string OpName => "mos_las";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i8 = LLVMTypeRef.Int8;
        var i16 = LLVMTypeRef.Int16;
        var i32 = LLVMTypeRef.Int32;
        var w = Mos6502Emitters.FetchImm16(ctx, "las_abs");
        var y = Mos6502Emitters.ReadGpr(ctx, "Y", "las_y");
        var yZ = ctx.Builder.BuildZExt(y, i16, "las_yz");
        var addr16 = ctx.Builder.BuildAdd(w, yZ, "las_addr16");
        var addr32 = ctx.Builder.BuildZExt(addr16, i32, "las_addr32");
        var v = Mos6502Emitters.BusRead8(ctx, addr32, "las_v");

        var spPtr = ctx.GepStatusRegister("SP");
        var sp = ctx.Builder.BuildLoad2(i8, spPtr, "las_sp");
        var r = ctx.Builder.BuildAnd(v, sp, "las_r");
        Mos6502Emitters.WriteGpr(ctx, "A", r);
        Mos6502Emitters.WriteGpr(ctx, "X", r);
        ctx.Builder.BuildStore(r, spPtr);

        var z = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, r,
            LLVMValueRef.CreateConstInt(i8, 0, false), "las_z");
        var nM = ctx.Builder.BuildAnd(r,
            LLVMValueRef.CreateConstInt(i8, 0x80, false), "las_nm");
        var n = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, nM,
            LLVMValueRef.CreateConstInt(i8, 0, false), "las_n");
        CpsrHelpers.SetStatusFlag(ctx, "P", "Z", z);
        CpsrHelpers.SetStatusFlag(ctx, "P", "N", n);
    }
}

/// <summary>
/// Magic-store helper shared by SHY ($9C abs,X), SHX ($9E abs,Y).
/// Semantics (matches LegacyCpu oracle):
///   base16 = imm16; hi = base16 &gt;&gt; 8; lo = base16 &amp; 0xFF
///   value = src AND (hi + 1)              // src = Y for SHY, X for SHX
///   effLo = (lo + idx) &amp; 0xFF             // idx = X for SHY, Y for SHX
///   // page-cross: if (lo + idx) overflowed, magic injects value as new hi
///   effHi = (effLo &lt; idx) ? value : hi
///   M[(effHi &lt;&lt; 8) | effLo] = value
/// blargg cpu_test5 06-abs_xy exercises this — for the no-cross case the
/// store goes to the canonical base+idx address; for the cross case the
/// hi byte is replaced by the magic value (low byte still wrapped).
/// </summary>
internal static class MosMagicStore
{
    public static void EmitMagic(EmitContext ctx, string srcReg, string idxReg, string label)
    {
        var i8  = LLVMTypeRef.Int8;
        var i16 = LLVMTypeRef.Int16;
        var i32 = LLVMTypeRef.Int32;

        var base16 = Mos6502Emitters.FetchImm16(ctx, $"{label}_abs");
        // hi = (base >> 8) & 0xFF as i8
        var hi16 = ctx.Builder.BuildLShr(base16,
            LLVMValueRef.CreateConstInt(i16, 8, false), $"{label}_hi16");
        var hi8 = ctx.Builder.BuildTrunc(hi16, i8, $"{label}_hi8");
        // lo = base & 0xFF as i8
        var lo8 = ctx.Builder.BuildTrunc(base16, i8, $"{label}_lo8");

        // src register (Y for SHY, X for SHX)
        var src = Mos6502Emitters.ReadGpr(ctx, srcReg, $"{label}_src");
        // value = src AND (hi+1) — adds in i8 (wrap is fine, hi=0xFF→0x00 is canonical)
        var hiPlus1 = ctx.Builder.BuildAdd(hi8,
            LLVMValueRef.CreateConstInt(i8, 1, false), $"{label}_hi1");
        var value = ctx.Builder.BuildAnd(src, hiPlus1, $"{label}_val");

        // idx register (X for SHY, Y for SHX)
        var idx = Mos6502Emitters.ReadGpr(ctx, idxReg, $"{label}_idx");
        // effLo = (lo + idx) & 0xFF — i8 add wraps naturally
        var effLo = ctx.Builder.BuildAdd(lo8, idx, $"{label}_eff_lo");
        // page-cross detect: effLo < idx (unsigned) iff (lo+idx) >= 0x100
        var crossed = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntULT,
            effLo, idx, $"{label}_crossed");
        // effHi = crossed ? value : hi8
        var effHi = ctx.Builder.BuildSelect(crossed, value, hi8, $"{label}_eff_hi");

        // Combine effHi:effLo into i16 address.
        var effHi16 = ctx.Builder.BuildZExt(effHi, i16, $"{label}_ehi16");
        var effLo16 = ctx.Builder.BuildZExt(effLo, i16, $"{label}_elo16");
        var hiSh = ctx.Builder.BuildShl(effHi16,
            LLVMValueRef.CreateConstInt(i16, 8, false), $"{label}_hi_shl");
        var addr16 = ctx.Builder.BuildOr(hiSh, effLo16, $"{label}_addr16");
        var addr32 = ctx.Builder.BuildZExt(addr16, i32, $"{label}_addr32");

        Mos6502Emitters.BusWrite8(ctx, addr32, value);
    }
}

/// <summary>SHY abs,X ($9C): M[base + X] = Y AND (high+1) — magic store.</summary>
internal sealed class MosShy : IMicroOpEmitter
{
    public string OpName => "mos_shy";
    public void Emit(EmitContext ctx, MicroOpStep step)
        => MosMagicStore.EmitMagic(ctx, srcReg: "Y", idxReg: "X", label: "shy");
}

/// <summary>SHX abs,Y ($9E): M[base + Y] = X AND (high+1) — magic store.</summary>
internal sealed class MosShx : IMicroOpEmitter
{
    public string OpName => "mos_shx";
    public void Emit(EmitContext ctx, MicroOpStep step)
        => MosMagicStore.EmitMagic(ctx, srcReg: "X", idxReg: "Y", label: "shx");
}
