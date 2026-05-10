using AprCpu.Core.JsonSpec;
using LLVMSharp.Interop;

namespace AprCpu.Core.IR;

/// <summary>
/// Intel 8086 / x86-16 family — micro-op emitters for the <c>x86_*</c>
/// ops referenced by <c>spec/x86-16/i8086/groups/*.json</c>.
///
/// Registered into <see cref="EmitterRegistry"/> by <see cref="SpecCompiler"/>
/// when <c>architecture.family</c> = <c>"x86-16"</c>.
///
/// 24.6.3 — scaffolding only. Just <c>x86_halt</c> is wired up so the
/// 24.6.2 smoke group (NOP + HLT) compiles. The static helpers
/// (FetchImm8/16, segmented memory, byte/word GPR access, ModR/M decode)
/// have stub implementations or comments-only declarations and will be
/// fleshed out as later sub-phases (24.6.5 MOV, 24.6.6 ALU, ...) need
/// them. New emitters land in dependency order: each adds its own
/// register call here, the JSON spec adds a step using the new op,
/// and unit tests verify per-instr behaviour against Tom Harte SST.
///
/// Conventions (mirroring <see cref="Mos6502Emitters"/>):
///   * Internal helpers operate on i8 / i16 LLVM values. Arithmetic
///     done at i32 width is widened back to i16 / i8 before storage.
///   * 8086 has no PC; it uses IP (16-bit, segmented through CS).
///     "Linear address" = (segment &lt;&lt; 4) + offset, computed as i32.
///   * Byte-half register access (AL/AH/CL/CH/DL/DH/BL/BH) is done by
///     loading the parent 16-bit GPR and shifting / masking. Aliases
///     in the spec are informational; the IR-level read/write helpers
///     are the source of truth.
/// </summary>
public static class X86_16Emitters
{
    public static void RegisterAll(EmitterRegistry reg)
    {
        // Bare-metal halt: write 1 to the HALTED status register. The host
        // runtime reads it after each step and breaks out of the per-block
        // loop until an external interrupt clears it.
        reg.Register(new X86HaltEmitter());

        // 24.6.5 — immediate fetch + field-dispatched register read/write.
        reg.Register(new X86FetchImm8Emitter());
        reg.Register(new X86FetchImm16Emitter());
        reg.Register(new X86WriteReg8FieldEmitter());
        reg.Register(new X86WriteReg16FieldEmitter());
        reg.Register(new X86ReadReg8FieldEmitter());
        reg.Register(new X86ReadReg16FieldEmitter());
        reg.Register(new X86FetchModRmEmitter());

        // 24.6.5c — memory ModR/M: effective-address computation + segmented load/store.
        reg.Register(new X86ModRmComputeEaEmitter());
        reg.Register(new X86ModRmLoad8Emitter());
        reg.Register(new X86ModRmLoad16Emitter());
        reg.Register(new X86ModRmStore8Emitter());
        reg.Register(new X86ModRmStore16Emitter());

        // 24.6.5e — moffs (direct disp16) MOV forms (A0-A3) + sreg field
        // emitters (8C/8E). C6/C7 (MOV r/m, imm) reuse existing fetch_imm
        // + modrm_store ops, no new emitter needed.
        reg.Register(new X86MovAccMoffsLoad8Emitter());
        reg.Register(new X86MovAccMoffsLoad16Emitter());
        reg.Register(new X86MovAccMoffsStore8Emitter());
        reg.Register(new X86MovAccMoffsStore16Emitter());
        reg.Register(new X86ReadSregFieldEmitter());
        reg.Register(new X86WriteSregFieldEmitter());

        // 24.6.5f — PUSH/POP family. SS:SP-relative stack ops with the
        // 8088 PUSH-SP quirk (decrement-then-read so PUSH SP pushes the
        // new SP value).
        reg.Register(new X86PushReg16FieldEmitter());
        reg.Register(new X86PopReg16FieldEmitter());
        reg.Register(new X86PushSegEmitter());
        reg.Register(new X86PopSegEmitter());
        reg.Register(new X86PushFlagsEmitter());
        reg.Register(new X86PopFlagsEmitter());
        reg.Register(new X86PushModRmW16Emitter());
        reg.Register(new X86PopModRmW16Emitter());

        // Future emitters land here in dependency order — see the file's
        // class-level comment.
    }

    // ---------------- shared helpers ----------------

    /// <summary>
    /// Compute a 20-bit linear address from segment+offset.
    ///   linear = ((seg as i32) &lt;&lt; 4) + (offset as i32)
    /// 8086 wraps to 20 bits in silicon; for emulation the upper 12 bits
    /// of the i32 result are preserved (callers truncate where needed).
    /// </summary>
    internal static LLVMValueRef SegmentedLinear(EmitContext ctx, LLVMValueRef seg16, LLVMValueRef off16, string label)
    {
        var i32 = LLVMTypeRef.Int32;
        var segZ = ctx.Builder.BuildZExt(seg16, i32, $"{label}_seg32");
        var offZ = ctx.Builder.BuildZExt(off16, i32, $"{label}_off32");
        var segShifted = ctx.Builder.BuildShl(segZ,
            LLVMValueRef.CreateConstInt(i32, 4, false), $"{label}_seg_sh4");
        return ctx.Builder.BuildAdd(segShifted, offZ, label);
    }

    /// <summary>Load IP (16-bit status register) → i16.</summary>
    internal static LLVMValueRef LoadIp16(EmitContext ctx, string label)
    {
        var ipPtr = ctx.GepStatusRegister("IP");
        return ctx.Builder.BuildLoad2(LLVMTypeRef.Int16, ipPtr, label);
    }

    /// <summary>Store i16 to IP.</summary>
    internal static void StoreIp16(EmitContext ctx, LLVMValueRef ip16)
    {
        var ipPtr = ctx.GepStatusRegister("IP");
        ctx.Builder.BuildStore(ip16, ipPtr);
    }

    /// <summary>Load named segment register (CS/DS/ES/SS) → i16.</summary>
    internal static LLVMValueRef LoadSeg16(EmitContext ctx, string segName, string label)
    {
        var p = ctx.GepStatusRegister(segName);
        return ctx.Builder.BuildLoad2(LLVMTypeRef.Int16, p, label);
    }

    /// <summary>
    /// Read a byte from CS:IP and advance IP by 1. Returns i8.
    ///
    /// Block-JIT fast path mirrors <see cref="Mos6502Emitters.FetchImm8"/> —
    /// when <c>ctx.CurrentInstructionBaseAddress</c> is set we extract the
    /// imm byte from the precomputed instruction word constant; otherwise
    /// we issue a memory_read_8 extern at linear (CS&lt;&lt;4)+IP.
    ///
    /// 24.6.3: only the per-instr extern path is implemented. Block-JIT
    /// extraction goes in 24.6.8 once the instruction-word constant
    /// layout for variable-length 8086 ops is decided.
    /// </summary>
    internal static LLVMValueRef FetchImm8(EmitContext ctx, string label)
    {
        var i8  = LLVMTypeRef.Int8;
        var i16 = LLVMTypeRef.Int16;
        var i32 = LLVMTypeRef.Int32;

        var ipPtr = ctx.GepStatusRegister("IP");
        var ip16 = ctx.Builder.BuildLoad2(i16, ipPtr, $"{label}_ip");

        var cs16  = LoadSeg16(ctx, "CS", $"{label}_cs");
        var lin32 = SegmentedLinear(ctx, cs16, ip16, $"{label}_lin");

        var b = MemoryEmitters.CallRead8(ctx, lin32, label);

        // IP wraps within 16 bits — silicon does not propagate carry into CS.
        var newIp = ctx.Builder.BuildAdd(ip16,
            LLVMValueRef.CreateConstInt(i16, 1, false), $"{label}_ip_next");
        ctx.Builder.BuildStore(newIp, ipPtr);
        return b;
    }

    /// <summary>
    /// Read a 16-bit little-endian word from CS:IP and advance IP by 2.
    /// Returns i16. Each byte read goes through a 20-bit linear address
    /// computed independently — the 16-bit IP wrap means a fetch starting
    /// at 0xFFFF reads byte at CS:0xFFFF then byte at CS:0x0000.
    /// </summary>
    internal static LLVMValueRef FetchImm16(EmitContext ctx, string label)
    {
        var i8  = LLVMTypeRef.Int8;
        var i16 = LLVMTypeRef.Int16;
        var i32 = LLVMTypeRef.Int32;

        var ipPtr = ctx.GepStatusRegister("IP");
        var ip16 = ctx.Builder.BuildLoad2(i16, ipPtr, $"{label}_ip");

        var cs16 = LoadSeg16(ctx, "CS", $"{label}_cs");

        var lin0 = SegmentedLinear(ctx, cs16, ip16, $"{label}_lin0");
        var lo8  = MemoryEmitters.CallRead8(ctx, lin0, $"{label}_lo");

        var ipPlus1 = ctx.Builder.BuildAdd(ip16,
            LLVMValueRef.CreateConstInt(i16, 1, false), $"{label}_ip1");
        var lin1 = SegmentedLinear(ctx, cs16, ipPlus1, $"{label}_lin1");
        var hi8  = MemoryEmitters.CallRead8(ctx, lin1, $"{label}_hi");

        var loZ  = ctx.Builder.BuildZExt(lo8, i16, $"{label}_loz");
        var hiZ  = ctx.Builder.BuildZExt(hi8, i16, $"{label}_hiz");
        var hiSh = ctx.Builder.BuildShl(hiZ,
            LLVMValueRef.CreateConstInt(i16, 8, false), $"{label}_hi_shl");
        var word = ctx.Builder.BuildOr(hiSh, loZ, label);

        var newIp = ctx.Builder.BuildAdd(ip16,
            LLVMValueRef.CreateConstInt(i16, 2, false), $"{label}_ip_next");
        ctx.Builder.BuildStore(newIp, ipPtr);
        return word;
    }

    /// <summary>
    /// Load a 16-bit GPR by encoded index (000=AX..111=DI). Returns i16.
    /// Maps directly to the spec's GPR order.
    /// </summary>
    internal static LLVMValueRef ReadGpr16(EmitContext ctx, int index, string label)
    {
        var ptr = ctx.GepGpr(index);
        return ctx.Builder.BuildLoad2(LLVMTypeRef.Int16, ptr, label);
    }

    /// <summary>Store i16 to a GPR by encoded index.</summary>
    internal static void WriteGpr16(EmitContext ctx, int index, LLVMValueRef value16)
    {
        var ptr = ctx.GepGpr(index);
        ctx.Builder.BuildStore(value16, ptr);
    }

    /// <summary>
    /// Load an 8-bit byte-half GPR by encoded byte-reg index
    /// (000=AL, 001=CL, 010=DL, 011=BL, 100=AH, 101=CH, 110=DH, 111=BH).
    ///
    /// Decoded as: parent GPR = byteIdx &amp; 3 (AX/CX/DX/BX in that order),
    /// upper-half flag = byteIdx &gt;= 4. Returns i8.
    /// </summary>
    internal static LLVMValueRef ReadGpr8(EmitContext ctx, int byteIdx, string label)
    {
        var i8  = LLVMTypeRef.Int8;
        var i16 = LLVMTypeRef.Int16;
        var parent = byteIdx & 3;
        var word = ReadGpr16(ctx, parent, $"{label}_p");
        if (byteIdx < 4)
        {
            return ctx.Builder.BuildTrunc(word, i8, label);
        }
        var shifted = ctx.Builder.BuildLShr(word,
            LLVMValueRef.CreateConstInt(i16, 8, false), $"{label}_hi_sh");
        return ctx.Builder.BuildTrunc(shifted, i8, label);
    }

    /// <summary>
    /// Store an i8 to a byte-half GPR. Preserves the other half of the
    /// parent 16-bit register: AL write does not touch AH and vice versa.
    /// </summary>
    internal static void WriteGpr8(EmitContext ctx, int byteIdx, LLVMValueRef value8)
    {
        var i16 = LLVMTypeRef.Int16;
        var parent = byteIdx & 3;
        var ptr = ctx.GepGpr(parent);
        var current = ctx.Builder.BuildLoad2(i16, ptr, $"r8w_{byteIdx}_cur");
        var valZ    = ctx.Builder.BuildZExt(value8, i16, $"r8w_{byteIdx}_z");

        LLVMValueRef merged;
        if (byteIdx < 4)
        {
            // Low half write — keep upper byte: (cur & 0xFF00) | val
            var keepHi = ctx.Builder.BuildAnd(current,
                LLVMValueRef.CreateConstInt(i16, 0xFF00, false), $"r8w_{byteIdx}_keepHi");
            merged = ctx.Builder.BuildOr(keepHi, valZ, $"r8w_{byteIdx}_merged");
        }
        else
        {
            // High half write — keep lower byte: (cur & 0x00FF) | (val << 8)
            var keepLo = ctx.Builder.BuildAnd(current,
                LLVMValueRef.CreateConstInt(i16, 0x00FF, false), $"r8w_{byteIdx}_keepLo");
            var valSh  = ctx.Builder.BuildShl(valZ,
                LLVMValueRef.CreateConstInt(i16, 8, false), $"r8w_{byteIdx}_sh");
            merged = ctx.Builder.BuildOr(keepLo, valSh, $"r8w_{byteIdx}_merged");
        }
        ctx.Builder.BuildStore(merged, ptr);
    }

    /// <summary>
    /// Load a byte from a segment:offset pair. Computes the 20-bit linear
    /// address and issues a memory_read_8 extern. Returns i8.
    /// </summary>
    internal static LLVMValueRef SegmentedRead8(
        EmitContext ctx, LLVMValueRef seg16, LLVMValueRef off16, string label)
    {
        var lin = SegmentedLinear(ctx, seg16, off16, $"{label}_lin");
        return MemoryEmitters.CallRead8(ctx, lin, label);
    }

    /// <summary>Store an i8 to seg:off via memory_write_8.</summary>
    internal static void SegmentedWrite8(
        EmitContext ctx, LLVMValueRef seg16, LLVMValueRef off16, LLVMValueRef value8, string label)
    {
        var lin = SegmentedLinear(ctx, seg16, off16, $"{label}_lin");
        MemoryEmitters.CallWrite8(ctx, lin, value8);
    }

    /// <summary>
    /// Load a 16-bit little-endian word from seg:off. Two single-byte reads;
    /// the high-byte address is (off+1) computed at i16 width so the offset
    /// wraps within the segment if the low byte was at offset 0xFFFF.
    /// </summary>
    internal static LLVMValueRef SegmentedRead16(
        EmitContext ctx, LLVMValueRef seg16, LLVMValueRef off16, string label)
    {
        var i16 = LLVMTypeRef.Int16;
        var lo = SegmentedRead8(ctx, seg16, off16, $"{label}_lo");
        var off1 = ctx.Builder.BuildAdd(off16,
            LLVMValueRef.CreateConstInt(i16, 1, false), $"{label}_off1");
        var hi = SegmentedRead8(ctx, seg16, off1, $"{label}_hi");

        var loZ  = ctx.Builder.BuildZExt(lo, i16, $"{label}_loz");
        var hiZ  = ctx.Builder.BuildZExt(hi, i16, $"{label}_hiz");
        var hiSh = ctx.Builder.BuildShl(hiZ,
            LLVMValueRef.CreateConstInt(i16, 8, false), $"{label}_hi_sh");
        return ctx.Builder.BuildOr(hiSh, loZ, label);
    }

    /// <summary>
    /// Store an i16 little-endian word to seg:off. Same offset-wrap rule
    /// as <see cref="SegmentedRead16"/>: high byte goes to (off+1) at i16
    /// width.
    /// </summary>
    internal static void SegmentedWrite16(
        EmitContext ctx, LLVMValueRef seg16, LLVMValueRef off16, LLVMValueRef value16, string label)
    {
        var i8  = LLVMTypeRef.Int8;
        var i16 = LLVMTypeRef.Int16;

        var lo = ctx.Builder.BuildTrunc(value16, i8, $"{label}_lo");
        SegmentedWrite8(ctx, seg16, off16, lo, $"{label}_loW");

        var hi16 = ctx.Builder.BuildLShr(value16,
            LLVMValueRef.CreateConstInt(i16, 8, false), $"{label}_hi16");
        var hi   = ctx.Builder.BuildTrunc(hi16, i8, $"{label}_hi");

        var off1 = ctx.Builder.BuildAdd(off16,
            LLVMValueRef.CreateConstInt(i16, 1, false), $"{label}_off1");
        SegmentedWrite8(ctx, seg16, off1, hi, $"{label}_hiW");
    }

    /// <summary>
    /// Resolve a segment register subject to 24.6.5d's SEG_OVERRIDE
    /// state slot: if the override is active (slot != 0xFF), pick the
    /// segment named by the override id (0=ES, 1=CS, 2=SS, 3=DS);
    /// otherwise load <paramref name="defaultSegName"/>.
    ///
    /// Used by moffs forms (A0-A3) and any other emitter that needs an
    /// override-aware segment without going through the full ModR/M
    /// machinery. The EA emitter (modrm_compute_ea) inlines the same
    /// logic for its tail filter.
    /// </summary>
    internal static LLVMValueRef LoadDefaultOrOverrideSegment(
        EmitContext ctx, string defaultSegName, string label)
    {
        var i8  = LLVMTypeRef.Int8;
        var i16 = LLVMTypeRef.Int16;
        var i32 = LLVMTypeRef.Int32;

        var defaultSeg = LoadSeg16(ctx, defaultSegName, $"{label}_def");

        var ovrPtr = ctx.GepStatusRegister("SEG_OVERRIDE");
        var ovr8 = ctx.Builder.BuildLoad2(i8, ovrPtr, $"{label}_ovr8");
        var ovr32 = ctx.Builder.BuildZExt(ovr8, i32, $"{label}_ovr32");
        var noOverride = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, ovr32,
            LLVMValueRef.CreateConstInt(i32, 0xFF, false), $"{label}_no_ovr");

        var applyBB = ctx.Function.AppendBasicBlock($"{label}_apply");
        var skipBB  = ctx.Function.AppendBasicBlock($"{label}_skip");
        var endBB   = ctx.Function.AppendBasicBlock($"{label}_end");
        ctx.Builder.BuildCondBr(noOverride, skipBB, applyBB);

        // Apply path: 4-arm switch on override id.
        ctx.Builder.PositionAtEnd(applyBB);
        var defBB = ctx.Function.AppendBasicBlock($"{label}_def_arm");
        var arms = new LLVMBasicBlockRef[4];
        var vals = new LLVMValueRef[4];
        for (int i = 0; i < 4; i++) arms[i] = ctx.Function.AppendBasicBlock($"{label}_arm_{i}");
        var sw = ctx.Builder.BuildSwitch(ovr32, defBB, 4);
        for (int i = 0; i < 4; i++)
            sw.AddCase(LLVMValueRef.CreateConstInt(i32, (uint)i, false), arms[i]);
        var names = new[] { "ES", "CS", "SS", "DS" };
        for (int i = 0; i < 4; i++)
        {
            ctx.Builder.PositionAtEnd(arms[i]);
            vals[i] = LoadSeg16(ctx, names[i], $"{label}_v_{i}");
            ctx.Builder.BuildBr(endBB);
        }
        ctx.Builder.PositionAtEnd(defBB);
        ctx.Builder.BuildBr(endBB);

        ctx.Builder.PositionAtEnd(skipBB);
        ctx.Builder.BuildBr(endBB);

        ctx.Builder.PositionAtEnd(endBB);
        var phi = ctx.Builder.BuildPhi(i16, label);
        var pIns = new LLVMValueRef[6];
        var pBlk = new LLVMBasicBlockRef[6];
        for (int i = 0; i < 4; i++) { pIns[i] = vals[i]; pBlk[i] = arms[i]; }
        pIns[4] = defaultSeg; pBlk[4] = defBB;
        pIns[5] = defaultSeg; pBlk[5] = skipBB;
        phi.AddIncoming(pIns, pBlk, 6);
        return phi;
    }
}

// ============================================================================
// x86_halt — write 1 to the HALTED status register so the host runtime
// breaks out of its per-block loop. The IP is left pointing PAST the HLT
// opcode (silicon behaviour: HLT increments IP first, then halts; the
// resume instruction is the byte after HLT). 24.6.2 cross-checks the
// HALTED slot exists in cpu.json (added in 24.6.3 as a width=8 status reg).
// ============================================================================

internal sealed class X86HaltEmitter : IMicroOpEmitter
{
    public string OpName => "x86_halt";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i8 = LLVMTypeRef.Int8;
        var ptr = ctx.GepStatusRegister("HALTED");
        ctx.Builder.BuildStore(LLVMValueRef.CreateConstInt(i8, 1, false), ptr);
    }
}

// ============================================================================
// x86_fetch_imm8 — read one byte at CS:IP, advance IP by 1, store the i8 in
// step.out so subsequent steps can reference it via ctx.Resolve(name).
//
// JSON shape: { "op": "x86_fetch_imm8", "out": "<name>" }
// ============================================================================

internal sealed class X86FetchImm8Emitter : IMicroOpEmitter
{
    public string OpName => "x86_fetch_imm8";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var outName = step.Raw.GetProperty("out").GetString()!;
        var v = X86_16Emitters.FetchImm8(ctx, outName);
        ctx.Values[outName] = v;
    }
}

// ============================================================================
// x86_fetch_imm16 — read two little-endian bytes at CS:IP, advance IP by 2,
// store the i16 in step.out.
//
// JSON shape: { "op": "x86_fetch_imm16", "out": "<name>" }
// ============================================================================

internal sealed class X86FetchImm16Emitter : IMicroOpEmitter
{
    public string OpName => "x86_fetch_imm16";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var outName = step.Raw.GetProperty("out").GetString()!;
        var v = X86_16Emitters.FetchImm16(ctx, outName);
        ctx.Values[outName] = v;
    }
}

// ============================================================================
// x86_write_reg8_field — runtime-dispatched 8-bit register write keyed off
// a 3-bit instruction-word field. Used by encodings like MOV r8, imm8
// (0xB0-0xB7) where the destination byte register is encoded in the low
// 3 bits of the opcode.
//
// Encoding: 000=AL 001=CL 010=DL 011=BL 100=AH 101=CH 110=DH 111=BH.
// Maps onto X86_16Emitters.WriteGpr8 by byte index — parent GPR =
// (idx & 3), upper-half flag = (idx &gt;= 4). The runtime switch produces
// 8 small basic blocks, one per byte register; LLVM's optimizer may
// collapse these in subsequent block-JIT passes.
//
// JSON shape:
//   { "op": "x86_write_reg8_field", "field": "<name>", "in": ["<value>"] }
// ============================================================================

internal sealed class X86WriteReg8FieldEmitter : IMicroOpEmitter
{
    public string OpName => "x86_write_reg8_field";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var fieldName = step.Raw.GetProperty("field").GetString()!;
        var inArr     = step.Raw.GetProperty("in");
        var valueName = inArr[0].GetString()!;

        var i8  = LLVMTypeRef.Int8;
        var i32 = LLVMTypeRef.Int32;

        var sel   = ctx.Resolve(fieldName);     // i32, 0..7
        var value = ctx.Resolve(valueName);     // i8

        var endBB     = ctx.Function.AppendBasicBlock("wreg8_end");
        var defaultBB = ctx.Function.AppendBasicBlock("wreg8_default");
        var arms = new LLVMBasicBlockRef[8];
        for (int i = 0; i < 8; i++) arms[i] = ctx.Function.AppendBasicBlock($"wreg8_{i}");

        var sw = ctx.Builder.BuildSwitch(sel, defaultBB, 8);
        for (int i = 0; i < 8; i++)
            sw.AddCase(LLVMValueRef.CreateConstInt(i32, (uint)i, false), arms[i]);

        for (int i = 0; i < 8; i++)
        {
            ctx.Builder.PositionAtEnd(arms[i]);
            X86_16Emitters.WriteGpr8(ctx, i, value);
            ctx.Builder.BuildBr(endBB);
        }

        // Default: undefined 3-bit value (cannot happen with a 2:0 field) —
        // fall through silently.
        ctx.Builder.PositionAtEnd(defaultBB);
        ctx.Builder.BuildBr(endBB);

        ctx.Builder.PositionAtEnd(endBB);
    }
}

// ============================================================================
// x86_write_reg16_field — runtime-dispatched 16-bit register write keyed off
// a 3-bit instruction-word field. Used by MOV r16, imm16 (0xB8-0xBF).
//
// Encoding: 000=AX 001=CX 010=DX 011=BX 100=SP 101=BP 110=SI 111=DI.
// Maps directly to spec GPR index — no byte-half splitting. Same switch
// structure as x86_write_reg8_field.
//
// JSON shape:
//   { "op": "x86_write_reg16_field", "field": "<name>", "in": ["<value>"] }
// ============================================================================

internal sealed class X86WriteReg16FieldEmitter : IMicroOpEmitter
{
    public string OpName => "x86_write_reg16_field";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var fieldName = step.Raw.GetProperty("field").GetString()!;
        var inArr     = step.Raw.GetProperty("in");
        var valueName = inArr[0].GetString()!;

        var i32 = LLVMTypeRef.Int32;

        var sel   = ctx.Resolve(fieldName);     // i32, 0..7
        var value = ctx.Resolve(valueName);     // i16

        var endBB     = ctx.Function.AppendBasicBlock("wreg16_end");
        var defaultBB = ctx.Function.AppendBasicBlock("wreg16_default");
        var arms = new LLVMBasicBlockRef[8];
        for (int i = 0; i < 8; i++) arms[i] = ctx.Function.AppendBasicBlock($"wreg16_{i}");

        var sw = ctx.Builder.BuildSwitch(sel, defaultBB, 8);
        for (int i = 0; i < 8; i++)
            sw.AddCase(LLVMValueRef.CreateConstInt(i32, (uint)i, false), arms[i]);

        for (int i = 0; i < 8; i++)
        {
            ctx.Builder.PositionAtEnd(arms[i]);
            X86_16Emitters.WriteGpr16(ctx, i, value);
            ctx.Builder.BuildBr(endBB);
        }

        ctx.Builder.PositionAtEnd(defaultBB);
        ctx.Builder.BuildBr(endBB);

        ctx.Builder.PositionAtEnd(endBB);
    }
}

// ============================================================================
// x86_read_reg8_field — runtime-dispatched 8-bit read keyed off a 3-bit
// instruction-word field or a cached value (e.g. modrm_reg from the
// previous ModR/M decode step). Caches the result in step.out.
//
// JSON shape:
//   { "op": "x86_read_reg8_field", "field": "<name>", "out": "<name>" }
// ============================================================================

internal sealed class X86ReadReg8FieldEmitter : IMicroOpEmitter
{
    public string OpName => "x86_read_reg8_field";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var fieldName = step.Raw.GetProperty("field").GetString()!;
        var outName   = step.Raw.GetProperty("out").GetString()!;

        var i8  = LLVMTypeRef.Int8;
        var i32 = LLVMTypeRef.Int32;

        var sel = ctx.Resolve(fieldName);

        // Result is selected via a switch + phi — each arm produces its
        // own i8 value and they merge into a single SSA value at the join.
        var endBB     = ctx.Function.AppendBasicBlock($"rreg8_{outName}_end");
        var defaultBB = ctx.Function.AppendBasicBlock($"rreg8_{outName}_default");
        var arms      = new LLVMBasicBlockRef[8];
        var armVals   = new LLVMValueRef[8];
        for (int i = 0; i < 8; i++) arms[i] = ctx.Function.AppendBasicBlock($"rreg8_{outName}_{i}");

        var sw = ctx.Builder.BuildSwitch(sel, defaultBB, 8);
        for (int i = 0; i < 8; i++)
            sw.AddCase(LLVMValueRef.CreateConstInt(i32, (uint)i, false), arms[i]);

        for (int i = 0; i < 8; i++)
        {
            ctx.Builder.PositionAtEnd(arms[i]);
            armVals[i] = X86_16Emitters.ReadGpr8(ctx, i, $"rreg8_{outName}_{i}_v");
            ctx.Builder.BuildBr(endBB);
        }

        ctx.Builder.PositionAtEnd(defaultBB);
        var defVal = LLVMValueRef.CreateConstInt(i8, 0, false);
        ctx.Builder.BuildBr(endBB);

        ctx.Builder.PositionAtEnd(endBB);
        var phi = ctx.Builder.BuildPhi(i8, outName);
        var incVals   = new LLVMValueRef[9];
        var incBlocks = new LLVMBasicBlockRef[9];
        for (int i = 0; i < 8; i++) { incVals[i] = armVals[i]; incBlocks[i] = arms[i]; }
        incVals[8] = defVal; incBlocks[8] = defaultBB;
        phi.AddIncoming(incVals, incBlocks, 9);

        ctx.Values[outName] = phi;
    }
}

// ============================================================================
// x86_read_reg16_field — runtime-dispatched 16-bit read keyed off a 3-bit
// instruction-word field or a cached value. Caches the result in step.out.
//
// JSON shape:
//   { "op": "x86_read_reg16_field", "field": "<name>", "out": "<name>" }
// ============================================================================

internal sealed class X86ReadReg16FieldEmitter : IMicroOpEmitter
{
    public string OpName => "x86_read_reg16_field";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var fieldName = step.Raw.GetProperty("field").GetString()!;
        var outName   = step.Raw.GetProperty("out").GetString()!;

        var i16 = LLVMTypeRef.Int16;
        var i32 = LLVMTypeRef.Int32;

        var sel = ctx.Resolve(fieldName);

        var endBB     = ctx.Function.AppendBasicBlock($"rreg16_{outName}_end");
        var defaultBB = ctx.Function.AppendBasicBlock($"rreg16_{outName}_default");
        var arms      = new LLVMBasicBlockRef[8];
        var armVals   = new LLVMValueRef[8];
        for (int i = 0; i < 8; i++) arms[i] = ctx.Function.AppendBasicBlock($"rreg16_{outName}_{i}");

        var sw = ctx.Builder.BuildSwitch(sel, defaultBB, 8);
        for (int i = 0; i < 8; i++)
            sw.AddCase(LLVMValueRef.CreateConstInt(i32, (uint)i, false), arms[i]);

        for (int i = 0; i < 8; i++)
        {
            ctx.Builder.PositionAtEnd(arms[i]);
            armVals[i] = X86_16Emitters.ReadGpr16(ctx, i, $"rreg16_{outName}_{i}_v");
            ctx.Builder.BuildBr(endBB);
        }

        ctx.Builder.PositionAtEnd(defaultBB);
        var defVal = LLVMValueRef.CreateConstInt(i16, 0, false);
        ctx.Builder.BuildBr(endBB);

        ctx.Builder.PositionAtEnd(endBB);
        var phi = ctx.Builder.BuildPhi(i16, outName);
        var incVals   = new LLVMValueRef[9];
        var incBlocks = new LLVMBasicBlockRef[9];
        for (int i = 0; i < 8; i++) { incVals[i] = armVals[i]; incBlocks[i] = arms[i]; }
        incVals[8] = defVal; incBlocks[8] = defaultBB;
        phi.AddIncoming(incVals, incBlocks, 9);

        ctx.Values[outName] = phi;
    }
}

// ============================================================================
// x86_fetch_modrm — read the ModR/M byte at CS:IP, advance IP by 1, and
// stash three sub-fields in the value cache:
//   modrm_mod  i32  bits 7:6  (00=mem-no-disp, 01=mem-disp8, 10=mem-disp16, 11=reg)
//   modrm_reg  i32  bits 5:3  (3-bit register field — usually the "other" operand)
//   modrm_rm   i32  bits 2:0  (3-bit r/m field — register or memory specifier)
//
// Subsequent steps look these up via ctx.Resolve("modrm_reg") etc.
//
// 24.6.5b — only the mod=11 (register-direct) case is exercised. mod=00/01/10
// memory paths land in 24.6.5c with effective-address computation.
//
// JSON shape: { "op": "x86_fetch_modrm" }   — no args; outputs are fixed.
// ============================================================================

internal sealed class X86FetchModRmEmitter : IMicroOpEmitter
{
    public string OpName => "x86_fetch_modrm";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i8  = LLVMTypeRef.Int8;
        var i32 = LLVMTypeRef.Int32;

        // Fetch the byte and zero-extend to i32 for field-extraction shifts.
        var byteVal = X86_16Emitters.FetchImm8(ctx, "modrm_byte");
        var asI32   = ctx.Builder.BuildZExt(byteVal, i32, "modrm_z");

        // mod = (byte >> 6) & 3
        var modShift = ctx.Builder.BuildLShr(asI32,
            LLVMValueRef.CreateConstInt(i32, 6, false), "modrm_mod_sh");
        var mod = ctx.Builder.BuildAnd(modShift,
            LLVMValueRef.CreateConstInt(i32, 3, false), "modrm_mod");

        // reg = (byte >> 3) & 7
        var regShift = ctx.Builder.BuildLShr(asI32,
            LLVMValueRef.CreateConstInt(i32, 3, false), "modrm_reg_sh");
        var regV = ctx.Builder.BuildAnd(regShift,
            LLVMValueRef.CreateConstInt(i32, 7, false), "modrm_reg");

        // rm = byte & 7
        var rm = ctx.Builder.BuildAnd(asI32,
            LLVMValueRef.CreateConstInt(i32, 7, false), "modrm_rm");

        ctx.Values["modrm_mod"] = mod;
        ctx.Values["modrm_reg"] = regV;
        ctx.Values["modrm_rm"]  = rm;
    }
}

// ============================================================================
// x86_modrm_compute_ea — compute the effective address (seg, off) for the
// memory operand encoded by the most recent x86_fetch_modrm. The 8086 EA
// table:
//
//   r/m | mod=00         | mod=01           | mod=10            | default seg
//   ----+----------------+------------------+-------------------+-------------
//   000 | BX+SI          | BX+SI+disp8      | BX+SI+disp16      | DS
//   001 | BX+DI          | BX+DI+disp8      | BX+DI+disp16      | DS
//   010 | BP+SI          | BP+SI+disp8      | BP+SI+disp16      | SS
//   011 | BP+DI          | BP+DI+disp8      | BP+DI+disp16      | SS
//   100 | SI             | SI+disp8         | SI+disp16         | DS
//   101 | DI             | DI+disp8         | DI+disp16         | DS
//   110 | direct disp16  | BP+disp8         | BP+disp16         | DS / SS / SS
//   111 | BX             | BX+disp8         | BX+disp16         | DS
//
// For mod=11 (register-direct) this emitter still runs but the produced
// values are unused — load/store emitters branch on mod and skip the
// memory path. This keeps the IR straight-line and avoids a conditional
// IP-advance that would muddle the variable-length fetch bookkeeping
// (per Intel: ModR/M's mod=11 path consumes zero displacement bytes,
// which we honour by feeding the 4-arm mod switch with the no-disp
// case for mod=11).
//
// Stashes in cache:
//   ea_off  i16  — within-segment 16-bit offset
//   ea_seg  i16  — default segment register value (DS or SS)
//
// Segment override prefixes (0x26/0x2E/0x36/0x3E) are not yet supported;
// 24.6.5d will revisit. For now ea_seg always takes the architectural
// default for the chosen r/m.
//
// JSON shape: { "op": "x86_modrm_compute_ea" }
// ============================================================================

internal sealed class X86ModRmComputeEaEmitter : IMicroOpEmitter
{
    public string OpName => "x86_modrm_compute_ea";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i8  = LLVMTypeRef.Int8;
        var i16 = LLVMTypeRef.Int16;
        var i32 = LLVMTypeRef.Int32;

        var mod = ctx.Resolve("modrm_mod");
        var rm  = ctx.Resolve("modrm_rm");

        // Phase 1 — produce (base, default_seg_name) from the rm switch.
        // Default seg is encoded as a constant string at IR build time;
        // each rm arm loads either DS or SS and feeds the join phi.
        var endRmBB = ctx.Function.AppendBasicBlock("ea_rm_end");
        var rmDefaultBB = ctx.Function.AppendBasicBlock("ea_rm_default");
        var rmArms = new LLVMBasicBlockRef[8];
        var rmBaseVals = new LLVMValueRef[8];
        var rmSegVals  = new LLVMValueRef[8];
        for (int i = 0; i < 8; i++) rmArms[i] = ctx.Function.AppendBasicBlock($"ea_rm_{i}");

        var sw = ctx.Builder.BuildSwitch(rm, rmDefaultBB, 8);
        for (int i = 0; i < 8; i++)
            sw.AddCase(LLVMValueRef.CreateConstInt(i32, (uint)i, false), rmArms[i]);

        // rm=000: BX + SI         seg=DS
        ctx.Builder.PositionAtEnd(rmArms[0]);
        rmBaseVals[0] = ctx.Builder.BuildAdd(
            X86_16Emitters.ReadGpr16(ctx, 3, "ea_bx_0"),
            X86_16Emitters.ReadGpr16(ctx, 6, "ea_si_0"),
            "ea_base_0");
        rmSegVals[0] = X86_16Emitters.LoadSeg16(ctx, "DS", "ea_seg_0");
        ctx.Builder.BuildBr(endRmBB);

        // rm=001: BX + DI         seg=DS
        ctx.Builder.PositionAtEnd(rmArms[1]);
        rmBaseVals[1] = ctx.Builder.BuildAdd(
            X86_16Emitters.ReadGpr16(ctx, 3, "ea_bx_1"),
            X86_16Emitters.ReadGpr16(ctx, 7, "ea_di_1"),
            "ea_base_1");
        rmSegVals[1] = X86_16Emitters.LoadSeg16(ctx, "DS", "ea_seg_1");
        ctx.Builder.BuildBr(endRmBB);

        // rm=010: BP + SI         seg=SS
        ctx.Builder.PositionAtEnd(rmArms[2]);
        rmBaseVals[2] = ctx.Builder.BuildAdd(
            X86_16Emitters.ReadGpr16(ctx, 5, "ea_bp_2"),
            X86_16Emitters.ReadGpr16(ctx, 6, "ea_si_2"),
            "ea_base_2");
        rmSegVals[2] = X86_16Emitters.LoadSeg16(ctx, "SS", "ea_seg_2");
        ctx.Builder.BuildBr(endRmBB);

        // rm=011: BP + DI         seg=SS
        ctx.Builder.PositionAtEnd(rmArms[3]);
        rmBaseVals[3] = ctx.Builder.BuildAdd(
            X86_16Emitters.ReadGpr16(ctx, 5, "ea_bp_3"),
            X86_16Emitters.ReadGpr16(ctx, 7, "ea_di_3"),
            "ea_base_3");
        rmSegVals[3] = X86_16Emitters.LoadSeg16(ctx, "SS", "ea_seg_3");
        ctx.Builder.BuildBr(endRmBB);

        // rm=100: SI              seg=DS
        ctx.Builder.PositionAtEnd(rmArms[4]);
        rmBaseVals[4] = X86_16Emitters.ReadGpr16(ctx, 6, "ea_base_4");
        rmSegVals[4]  = X86_16Emitters.LoadSeg16(ctx, "DS", "ea_seg_4");
        ctx.Builder.BuildBr(endRmBB);

        // rm=101: DI              seg=DS
        ctx.Builder.PositionAtEnd(rmArms[5]);
        rmBaseVals[5] = X86_16Emitters.ReadGpr16(ctx, 7, "ea_base_5");
        rmSegVals[5]  = X86_16Emitters.LoadSeg16(ctx, "DS", "ea_seg_5");
        ctx.Builder.BuildBr(endRmBB);

        // rm=110: SPECIAL — for mod=00 it's direct disp16 (we'll override
        // base in the disp phase below). For mod=01/10 it's BP+disp.
        // To keep the join shape uniform, base here is BP (used for mod=01/10)
        // and seg defaults to SS. The mod=00 case rewrites both fields after
        // fetching the disp16.
        ctx.Builder.PositionAtEnd(rmArms[6]);
        rmBaseVals[6] = X86_16Emitters.ReadGpr16(ctx, 5, "ea_base_6_bp");
        rmSegVals[6]  = X86_16Emitters.LoadSeg16(ctx, "SS", "ea_seg_6_ss");
        ctx.Builder.BuildBr(endRmBB);

        // rm=111: BX              seg=DS
        ctx.Builder.PositionAtEnd(rmArms[7]);
        rmBaseVals[7] = X86_16Emitters.ReadGpr16(ctx, 3, "ea_base_7");
        rmSegVals[7]  = X86_16Emitters.LoadSeg16(ctx, "DS", "ea_seg_7");
        ctx.Builder.BuildBr(endRmBB);

        // Default arm — should never fire (rm is 3 bits) but join-shape requires it.
        ctx.Builder.PositionAtEnd(rmDefaultBB);
        var zeroI16 = LLVMValueRef.CreateConstInt(i16, 0, false);
        ctx.Builder.BuildBr(endRmBB);

        // Phase 1 join: phi-merge base and seg from 9 incoming blocks (8 + default).
        ctx.Builder.PositionAtEnd(endRmBB);
        var basePhi = ctx.Builder.BuildPhi(i16, "ea_base");
        var segPhi  = ctx.Builder.BuildPhi(i16, "ea_seg_pre");
        var inBlocks = new LLVMBasicBlockRef[9];
        var inBases  = new LLVMValueRef[9];
        var inSegs   = new LLVMValueRef[9];
        for (int i = 0; i < 8; i++) { inBlocks[i] = rmArms[i]; inBases[i] = rmBaseVals[i]; inSegs[i] = rmSegVals[i]; }
        inBlocks[8] = rmDefaultBB; inBases[8] = zeroI16; inSegs[8] = zeroI16;
        basePhi.AddIncoming(inBases, inBlocks, 9);
        segPhi.AddIncoming(inSegs,  inBlocks, 9);

        // Phase 2 — switch on mod to produce final ea_off (and possibly
        // override seg/base for the mod=00 rm=110 special case).
        var endModBB = ctx.Function.AppendBasicBlock("ea_mod_end");
        var mod00BB  = ctx.Function.AppendBasicBlock("ea_mod_00");
        var mod01BB  = ctx.Function.AppendBasicBlock("ea_mod_01");
        var mod10BB  = ctx.Function.AppendBasicBlock("ea_mod_10");
        var mod11BB  = ctx.Function.AppendBasicBlock("ea_mod_11");
        var modSw = ctx.Builder.BuildSwitch(mod, mod11BB, 4);
        modSw.AddCase(LLVMValueRef.CreateConstInt(i32, 0, false), mod00BB);
        modSw.AddCase(LLVMValueRef.CreateConstInt(i32, 1, false), mod01BB);
        modSw.AddCase(LLVMValueRef.CreateConstInt(i32, 2, false), mod10BB);
        modSw.AddCase(LLVMValueRef.CreateConstInt(i32, 3, false), mod11BB);

        // mod=00: disp = 0, EXCEPT rm=110 means direct disp16 (no base, seg=DS).
        ctx.Builder.PositionAtEnd(mod00BB);
        var rmIs6 = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, rm,
            LLVMValueRef.CreateConstInt(i32, 6, false), "ea_rm_is_6");
        var mod00DispBB    = ctx.Function.AppendBasicBlock("ea_mod00_disp16");
        var mod00NoDispBB  = ctx.Function.AppendBasicBlock("ea_mod00_nodisp");
        var mod00JoinBB    = ctx.Function.AppendBasicBlock("ea_mod00_join");
        ctx.Builder.BuildCondBr(rmIs6, mod00DispBB, mod00NoDispBB);

        // mod=00 rm=6: ea_off = fetch_imm16; seg = DS
        ctx.Builder.PositionAtEnd(mod00DispBB);
        var directDisp = X86_16Emitters.FetchImm16(ctx, "ea_disp16_direct");
        var dsSeg00 = X86_16Emitters.LoadSeg16(ctx, "DS", "ea_seg_direct_ds");
        ctx.Builder.BuildBr(mod00JoinBB);

        // mod=00 other rm: ea_off = base; seg = segPhi (already loaded)
        ctx.Builder.PositionAtEnd(mod00NoDispBB);
        ctx.Builder.BuildBr(mod00JoinBB);

        ctx.Builder.PositionAtEnd(mod00JoinBB);
        var mod00Off = ctx.Builder.BuildPhi(i16, "ea_mod00_off");
        var mod00Seg = ctx.Builder.BuildPhi(i16, "ea_mod00_seg");
        mod00Off.AddIncoming(new[] { directDisp, basePhi }, new[] { mod00DispBB, mod00NoDispBB }, 2);
        mod00Seg.AddIncoming(new[] { dsSeg00,    segPhi   }, new[] { mod00DispBB, mod00NoDispBB }, 2);
        ctx.Builder.BuildBr(endModBB);

        // mod=01: disp = sext(fetch_imm8); ea_off = base + disp; seg = segPhi.
        // For rm=110, base was BP and seg was SS — that's correct here.
        ctx.Builder.PositionAtEnd(mod01BB);
        var disp8raw = X86_16Emitters.FetchImm8(ctx, "ea_disp8");
        var disp8sx  = ctx.Builder.BuildSExt(disp8raw, i16, "ea_disp8_sx");
        var mod01Off = ctx.Builder.BuildAdd(basePhi, disp8sx, "ea_mod01_off");
        ctx.Builder.BuildBr(endModBB);

        // mod=10: disp = fetch_imm16; ea_off = base + disp; seg = segPhi.
        ctx.Builder.PositionAtEnd(mod10BB);
        var disp16 = X86_16Emitters.FetchImm16(ctx, "ea_disp16");
        var mod10Off = ctx.Builder.BuildAdd(basePhi, disp16, "ea_mod10_off");
        ctx.Builder.BuildBr(endModBB);

        // mod=11 (register-direct): EA values are unused; emit zero
        // placeholders that fold into the join phi. Load/store emitters
        // detect mod=11 at runtime and bypass the EA path.
        ctx.Builder.PositionAtEnd(mod11BB);
        ctx.Builder.BuildBr(endModBB);

        // Final join — ea_off + ea_seg merge from the 4 mod arms.
        ctx.Builder.PositionAtEnd(endModBB);
        var eaOff = ctx.Builder.BuildPhi(i16, "ea_off");
        var eaSegDefault = ctx.Builder.BuildPhi(i16, "ea_seg_default");
        eaOff.AddIncoming(
            new[] { (LLVMValueRef)mod00Off, mod01Off, mod10Off, zeroI16 },
            new[] { mod00JoinBB, mod01BB, mod10BB, mod11BB },
            4);
        eaSegDefault.AddIncoming(
            new[] { (LLVMValueRef)mod00Seg, segPhi, segPhi, zeroI16 },
            new[] { mod00JoinBB, mod01BB, mod10BB, mod11BB },
            4);

        // Phase 3 — apply segment override prefix (24.6.5d). The C# Step()
        // dispatcher writes SEG_OVERRIDE to a non-0xFF value (0=ES, 1=CS,
        // 2=SS, 3=DS — matches sreg ModR/M encoding) when one of the four
        // 0x26/0x2E/0x36/0x3E bytes precedes the real opcode, then clears
        // it after the instruction completes. If active, the override
        // segment replaces the architectural default; otherwise the
        // default flows through unchanged.
        var ovrPtr = ctx.GepStatusRegister("SEG_OVERRIDE");
        var ovr8 = ctx.Builder.BuildLoad2(i8, ovrPtr, "seg_ovr8");
        var ovr32 = ctx.Builder.BuildZExt(ovr8, i32, "seg_ovr32");
        var noOverride = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, ovr32,
            LLVMValueRef.CreateConstInt(i32, 0xFF, false), "no_seg_ovr");

        var ovrApplyBB = ctx.Function.AppendBasicBlock("seg_ovr_apply");
        var ovrSkipBB  = ctx.Function.AppendBasicBlock("seg_ovr_skip");
        var ovrEndBB   = ctx.Function.AppendBasicBlock("seg_ovr_end");
        ctx.Builder.BuildCondBr(noOverride, ovrSkipBB, ovrApplyBB);

        // Apply: 4-arm switch on override value (0..3 = ES/CS/SS/DS).
        ctx.Builder.PositionAtEnd(ovrApplyBB);
        var ovrDefBB = ctx.Function.AppendBasicBlock("seg_ovr_default");
        var ovrArms  = new LLVMBasicBlockRef[4];
        var ovrVals  = new LLVMValueRef[4];
        for (int i = 0; i < 4; i++) ovrArms[i] = ctx.Function.AppendBasicBlock($"seg_ovr_{i}");
        var ovrSw = ctx.Builder.BuildSwitch(ovr32, ovrDefBB, 4);
        for (int i = 0; i < 4; i++)
            ovrSw.AddCase(LLVMValueRef.CreateConstInt(i32, (uint)i, false), ovrArms[i]);

        var segNames = new[] { "ES", "CS", "SS", "DS" };
        for (int i = 0; i < 4; i++)
        {
            ctx.Builder.PositionAtEnd(ovrArms[i]);
            ovrVals[i] = X86_16Emitters.LoadSeg16(ctx, segNames[i], $"seg_ovr_v_{i}");
            ctx.Builder.BuildBr(ovrEndBB);
        }
        // Default arm — unreachable in practice (override is 0..3 or 0xFF;
        // 0xFF was filtered above). Feed back the default to keep IR sane.
        ctx.Builder.PositionAtEnd(ovrDefBB);
        ctx.Builder.BuildBr(ovrEndBB);

        // Skip path — pass default through.
        ctx.Builder.PositionAtEnd(ovrSkipBB);
        ctx.Builder.BuildBr(ovrEndBB);

        // Final phi.
        ctx.Builder.PositionAtEnd(ovrEndBB);
        var eaSeg = ctx.Builder.BuildPhi(i16, "ea_seg");
        var pIns = new LLVMValueRef[6];
        var pBlk = new LLVMBasicBlockRef[6];
        for (int i = 0; i < 4; i++) { pIns[i] = ovrVals[i]; pBlk[i] = ovrArms[i]; }
        pIns[4] = eaSegDefault; pBlk[4] = ovrDefBB;
        pIns[5] = eaSegDefault; pBlk[5] = ovrSkipBB;
        eaSeg.AddIncoming(pIns, pBlk, 6);

        ctx.Values["ea_off"] = eaOff;
        ctx.Values["ea_seg"] = eaSeg;
    }
}

// ============================================================================
// Helper for the four x86_modrm_load_w{8,16} / x86_modrm_store_w{8,16}
// emitters. Splits the runtime path on mod==11: register-direct on the
// "true" branch, segmented memory access via cached ea_seg/ea_off on
// the "false" branch.
// ============================================================================

internal static class X86ModRmMemHelpers
{
    public static LLVMValueRef BuildLoadW8(EmitContext ctx, string outName)
    {
        var i8  = LLVMTypeRef.Int8;
        var i32 = LLVMTypeRef.Int32;
        var mod = ctx.Resolve("modrm_mod");
        var rm  = ctx.Resolve("modrm_rm");
        var isReg = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, mod,
            LLVMValueRef.CreateConstInt(i32, 3, false), $"{outName}_isreg");

        var regBB = ctx.Function.AppendBasicBlock($"{outName}_reg");
        var memBB = ctx.Function.AppendBasicBlock($"{outName}_mem");
        var endBB = ctx.Function.AppendBasicBlock($"{outName}_end");
        ctx.Builder.BuildCondBr(isReg, regBB, memBB);

        // reg path — runtime switch on rm to read the byte register.
        ctx.Builder.PositionAtEnd(regBB);
        var regVal = X86ReadByteByField(ctx, rm, $"{outName}_reg_v");
        var regBlock = ctx.Builder.InsertBlock;
        ctx.Builder.BuildBr(endBB);

        // mem path — segmented read at ea_seg:ea_off.
        ctx.Builder.PositionAtEnd(memBB);
        var seg = ctx.Resolve("ea_seg");
        var off = ctx.Resolve("ea_off");
        var memVal = X86_16Emitters.SegmentedRead8(ctx, seg, off, $"{outName}_mem_v");
        var memBlock = ctx.Builder.InsertBlock;
        ctx.Builder.BuildBr(endBB);

        ctx.Builder.PositionAtEnd(endBB);
        var phi = ctx.Builder.BuildPhi(i8, outName);
        phi.AddIncoming(new[] { regVal, memVal }, new[] { regBlock, memBlock }, 2);
        return phi;
    }

    public static LLVMValueRef BuildLoadW16(EmitContext ctx, string outName)
    {
        var i16 = LLVMTypeRef.Int16;
        var i32 = LLVMTypeRef.Int32;
        var mod = ctx.Resolve("modrm_mod");
        var rm  = ctx.Resolve("modrm_rm");
        var isReg = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, mod,
            LLVMValueRef.CreateConstInt(i32, 3, false), $"{outName}_isreg");

        var regBB = ctx.Function.AppendBasicBlock($"{outName}_reg");
        var memBB = ctx.Function.AppendBasicBlock($"{outName}_mem");
        var endBB = ctx.Function.AppendBasicBlock($"{outName}_end");
        ctx.Builder.BuildCondBr(isReg, regBB, memBB);

        ctx.Builder.PositionAtEnd(regBB);
        var regVal = X86ReadWordByField(ctx, rm, $"{outName}_reg_v");
        var regBlock = ctx.Builder.InsertBlock;
        ctx.Builder.BuildBr(endBB);

        ctx.Builder.PositionAtEnd(memBB);
        var seg = ctx.Resolve("ea_seg");
        var off = ctx.Resolve("ea_off");
        var memVal = X86_16Emitters.SegmentedRead16(ctx, seg, off, $"{outName}_mem_v");
        var memBlock = ctx.Builder.InsertBlock;
        ctx.Builder.BuildBr(endBB);

        ctx.Builder.PositionAtEnd(endBB);
        var phi = ctx.Builder.BuildPhi(i16, outName);
        phi.AddIncoming(new[] { regVal, memVal }, new[] { regBlock, memBlock }, 2);
        return phi;
    }

    public static void BuildStoreW8(EmitContext ctx, LLVMValueRef value8)
    {
        var i32 = LLVMTypeRef.Int32;
        var mod = ctx.Resolve("modrm_mod");
        var rm  = ctx.Resolve("modrm_rm");
        var isReg = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, mod,
            LLVMValueRef.CreateConstInt(i32, 3, false), "stw8_isreg");

        var regBB = ctx.Function.AppendBasicBlock("stw8_reg");
        var memBB = ctx.Function.AppendBasicBlock("stw8_mem");
        var endBB = ctx.Function.AppendBasicBlock("stw8_end");
        ctx.Builder.BuildCondBr(isReg, regBB, memBB);

        ctx.Builder.PositionAtEnd(regBB);
        X86WriteByteByField(ctx, rm, value8);
        ctx.Builder.BuildBr(endBB);

        ctx.Builder.PositionAtEnd(memBB);
        var seg = ctx.Resolve("ea_seg");
        var off = ctx.Resolve("ea_off");
        X86_16Emitters.SegmentedWrite8(ctx, seg, off, value8, "stw8_w");
        ctx.Builder.BuildBr(endBB);

        ctx.Builder.PositionAtEnd(endBB);
    }

    public static void BuildStoreW16(EmitContext ctx, LLVMValueRef value16)
    {
        var i32 = LLVMTypeRef.Int32;
        var mod = ctx.Resolve("modrm_mod");
        var rm  = ctx.Resolve("modrm_rm");
        var isReg = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, mod,
            LLVMValueRef.CreateConstInt(i32, 3, false), "stw16_isreg");

        var regBB = ctx.Function.AppendBasicBlock("stw16_reg");
        var memBB = ctx.Function.AppendBasicBlock("stw16_mem");
        var endBB = ctx.Function.AppendBasicBlock("stw16_end");
        ctx.Builder.BuildCondBr(isReg, regBB, memBB);

        ctx.Builder.PositionAtEnd(regBB);
        X86WriteWordByField(ctx, rm, value16);
        ctx.Builder.BuildBr(endBB);

        ctx.Builder.PositionAtEnd(memBB);
        var seg = ctx.Resolve("ea_seg");
        var off = ctx.Resolve("ea_off");
        X86_16Emitters.SegmentedWrite16(ctx, seg, off, value16, "stw16_w");
        ctx.Builder.BuildBr(endBB);

        ctx.Builder.PositionAtEnd(endBB);
    }

    private static LLVMValueRef X86ReadByteByField(EmitContext ctx, LLVMValueRef sel, string label)
    {
        var i8  = LLVMTypeRef.Int8;
        var i32 = LLVMTypeRef.Int32;
        var endBB     = ctx.Function.AppendBasicBlock($"{label}_end");
        var defaultBB = ctx.Function.AppendBasicBlock($"{label}_default");
        var arms = new LLVMBasicBlockRef[8];
        var armVals = new LLVMValueRef[8];
        for (int i = 0; i < 8; i++) arms[i] = ctx.Function.AppendBasicBlock($"{label}_{i}");
        var sw = ctx.Builder.BuildSwitch(sel, defaultBB, 8);
        for (int i = 0; i < 8; i++)
            sw.AddCase(LLVMValueRef.CreateConstInt(i32, (uint)i, false), arms[i]);
        for (int i = 0; i < 8; i++)
        {
            ctx.Builder.PositionAtEnd(arms[i]);
            armVals[i] = X86_16Emitters.ReadGpr8(ctx, i, $"{label}_{i}_v");
            ctx.Builder.BuildBr(endBB);
        }
        ctx.Builder.PositionAtEnd(defaultBB);
        var defVal = LLVMValueRef.CreateConstInt(i8, 0, false);
        ctx.Builder.BuildBr(endBB);

        ctx.Builder.PositionAtEnd(endBB);
        var phi = ctx.Builder.BuildPhi(i8, label);
        var inVals   = new LLVMValueRef[9];
        var inBlocks = new LLVMBasicBlockRef[9];
        for (int i = 0; i < 8; i++) { inVals[i] = armVals[i]; inBlocks[i] = arms[i]; }
        inVals[8] = defVal; inBlocks[8] = defaultBB;
        phi.AddIncoming(inVals, inBlocks, 9);
        return phi;
    }

    private static LLVMValueRef X86ReadWordByField(EmitContext ctx, LLVMValueRef sel, string label)
    {
        var i16 = LLVMTypeRef.Int16;
        var i32 = LLVMTypeRef.Int32;
        var endBB     = ctx.Function.AppendBasicBlock($"{label}_end");
        var defaultBB = ctx.Function.AppendBasicBlock($"{label}_default");
        var arms = new LLVMBasicBlockRef[8];
        var armVals = new LLVMValueRef[8];
        for (int i = 0; i < 8; i++) arms[i] = ctx.Function.AppendBasicBlock($"{label}_{i}");
        var sw = ctx.Builder.BuildSwitch(sel, defaultBB, 8);
        for (int i = 0; i < 8; i++)
            sw.AddCase(LLVMValueRef.CreateConstInt(i32, (uint)i, false), arms[i]);
        for (int i = 0; i < 8; i++)
        {
            ctx.Builder.PositionAtEnd(arms[i]);
            armVals[i] = X86_16Emitters.ReadGpr16(ctx, i, $"{label}_{i}_v");
            ctx.Builder.BuildBr(endBB);
        }
        ctx.Builder.PositionAtEnd(defaultBB);
        var defVal = LLVMValueRef.CreateConstInt(i16, 0, false);
        ctx.Builder.BuildBr(endBB);

        ctx.Builder.PositionAtEnd(endBB);
        var phi = ctx.Builder.BuildPhi(i16, label);
        var inVals   = new LLVMValueRef[9];
        var inBlocks = new LLVMBasicBlockRef[9];
        for (int i = 0; i < 8; i++) { inVals[i] = armVals[i]; inBlocks[i] = arms[i]; }
        inVals[8] = defVal; inBlocks[8] = defaultBB;
        phi.AddIncoming(inVals, inBlocks, 9);
        return phi;
    }

    private static void X86WriteByteByField(EmitContext ctx, LLVMValueRef sel, LLVMValueRef value8)
    {
        var i32 = LLVMTypeRef.Int32;
        var endBB     = ctx.Function.AppendBasicBlock("wbf8_end");
        var defaultBB = ctx.Function.AppendBasicBlock("wbf8_default");
        var arms = new LLVMBasicBlockRef[8];
        for (int i = 0; i < 8; i++) arms[i] = ctx.Function.AppendBasicBlock($"wbf8_{i}");
        var sw = ctx.Builder.BuildSwitch(sel, defaultBB, 8);
        for (int i = 0; i < 8; i++)
            sw.AddCase(LLVMValueRef.CreateConstInt(i32, (uint)i, false), arms[i]);
        for (int i = 0; i < 8; i++)
        {
            ctx.Builder.PositionAtEnd(arms[i]);
            X86_16Emitters.WriteGpr8(ctx, i, value8);
            ctx.Builder.BuildBr(endBB);
        }
        ctx.Builder.PositionAtEnd(defaultBB);
        ctx.Builder.BuildBr(endBB);
        ctx.Builder.PositionAtEnd(endBB);
    }

    private static void X86WriteWordByField(EmitContext ctx, LLVMValueRef sel, LLVMValueRef value16)
    {
        var i32 = LLVMTypeRef.Int32;
        var endBB     = ctx.Function.AppendBasicBlock("wbf16_end");
        var defaultBB = ctx.Function.AppendBasicBlock("wbf16_default");
        var arms = new LLVMBasicBlockRef[8];
        for (int i = 0; i < 8; i++) arms[i] = ctx.Function.AppendBasicBlock($"wbf16_{i}");
        var sw = ctx.Builder.BuildSwitch(sel, defaultBB, 8);
        for (int i = 0; i < 8; i++)
            sw.AddCase(LLVMValueRef.CreateConstInt(i32, (uint)i, false), arms[i]);
        for (int i = 0; i < 8; i++)
        {
            ctx.Builder.PositionAtEnd(arms[i]);
            X86_16Emitters.WriteGpr16(ctx, i, value16);
            ctx.Builder.BuildBr(endBB);
        }
        ctx.Builder.PositionAtEnd(defaultBB);
        ctx.Builder.BuildBr(endBB);
        ctx.Builder.PositionAtEnd(endBB);
    }
}

// ============================================================================
// x86_modrm_load_w8 / x86_modrm_load_w16 — read source operand based on
// modrm_mod (reg if mod=11, else memory at ea_seg:ea_off). Cache the
// result in step.out.
//
// JSON shape: { "op": "x86_modrm_load_w8", "out": "<name>" }
// ============================================================================

internal sealed class X86ModRmLoad8Emitter : IMicroOpEmitter
{
    public string OpName => "x86_modrm_load_w8";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var outName = step.Raw.GetProperty("out").GetString()!;
        ctx.Values[outName] = X86ModRmMemHelpers.BuildLoadW8(ctx, outName);
    }
}

internal sealed class X86ModRmLoad16Emitter : IMicroOpEmitter
{
    public string OpName => "x86_modrm_load_w16";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var outName = step.Raw.GetProperty("out").GetString()!;
        ctx.Values[outName] = X86ModRmMemHelpers.BuildLoadW16(ctx, outName);
    }
}

// ============================================================================
// x86_modrm_store_w8 / x86_modrm_store_w16 — write destination operand
// based on modrm_mod (reg if mod=11, else memory at ea_seg:ea_off).
//
// JSON shape: { "op": "x86_modrm_store_w8", "in": ["<value>"] }
// ============================================================================

internal sealed class X86ModRmStore8Emitter : IMicroOpEmitter
{
    public string OpName => "x86_modrm_store_w8";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var inArr = step.Raw.GetProperty("in");
        var valueName = inArr[0].GetString()!;
        var v = ctx.Resolve(valueName);
        X86ModRmMemHelpers.BuildStoreW8(ctx, v);
    }
}

internal sealed class X86ModRmStore16Emitter : IMicroOpEmitter
{
    public string OpName => "x86_modrm_store_w16";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var inArr = step.Raw.GetProperty("in");
        var valueName = inArr[0].GetString()!;
        var v = ctx.Resolve(valueName);
        X86ModRmMemHelpers.BuildStoreW16(ctx, v);
    }
}

// ============================================================================
// 24.6.5e — MOV moffs (A0-A3): direct disp16 to/from accumulator (AL/AX).
//
// Encoding: opcode + 16-bit little-endian displacement; default segment
// is DS, but a 24.6.5d segment override prefix can redirect it. No
// ModR/M byte involved — this is the "fast path" for absolute-address
// memory access that compilers used to emit a lot for global variables
// before more flexible addressing modes became common.
//
//   0xA0   MOV AL, moffs8     (read  byte from [seg:disp16] into AL)
//   0xA1   MOV AX, moffs16    (read  word from [seg:disp16] into AX)
//   0xA2   MOV moffs8, AL     (write AL into [seg:disp16])
//   0xA3   MOV moffs16, AX    (write AX into [seg:disp16])
//
// The "8/16" suffix in moffs8/moffs16 refers to the WIDTH of the data
// being moved, NOT the displacement — the displacement is always 16-bit.
//
// JSON shape (no args): { "op": "x86_mov_acc_moffs_load_w8" }, etc.
// ============================================================================

internal sealed class X86MovAccMoffsLoad8Emitter : IMicroOpEmitter
{
    public string OpName => "x86_mov_acc_moffs_load_w8";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var disp = X86_16Emitters.FetchImm16(ctx, "moffs_disp");
        var seg  = X86_16Emitters.LoadDefaultOrOverrideSegment(ctx, "DS", "moffs_seg");
        var data = X86_16Emitters.SegmentedRead8(ctx, seg, disp, "moffs_v");
        X86_16Emitters.WriteGpr8(ctx, 0, data);   // AL = byte 0 of GPR[0] (AX)
    }
}

internal sealed class X86MovAccMoffsLoad16Emitter : IMicroOpEmitter
{
    public string OpName => "x86_mov_acc_moffs_load_w16";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var disp = X86_16Emitters.FetchImm16(ctx, "moffs_disp");
        var seg  = X86_16Emitters.LoadDefaultOrOverrideSegment(ctx, "DS", "moffs_seg");
        var data = X86_16Emitters.SegmentedRead16(ctx, seg, disp, "moffs_v");
        X86_16Emitters.WriteGpr16(ctx, 0, data);  // AX = GPR[0]
    }
}

internal sealed class X86MovAccMoffsStore8Emitter : IMicroOpEmitter
{
    public string OpName => "x86_mov_acc_moffs_store_w8";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var disp = X86_16Emitters.FetchImm16(ctx, "moffs_disp");
        var seg  = X86_16Emitters.LoadDefaultOrOverrideSegment(ctx, "DS", "moffs_seg");
        var data = X86_16Emitters.ReadGpr8(ctx, 0, "moffs_al");
        X86_16Emitters.SegmentedWrite8(ctx, seg, disp, data, "moffs_w");
    }
}

internal sealed class X86MovAccMoffsStore16Emitter : IMicroOpEmitter
{
    public string OpName => "x86_mov_acc_moffs_store_w16";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var disp = X86_16Emitters.FetchImm16(ctx, "moffs_disp");
        var seg  = X86_16Emitters.LoadDefaultOrOverrideSegment(ctx, "DS", "moffs_seg");
        var data = X86_16Emitters.ReadGpr16(ctx, 0, "moffs_ax");
        X86_16Emitters.SegmentedWrite16(ctx, seg, disp, data, "moffs_w");
    }
}

// ============================================================================
// 24.6.5e — MOV sreg (8C/8E): segment register read/write via ModR/M.
//
// The 3-bit ModR/M reg field holds a sreg index in these forms (only
// values 0..3 are valid — ES/CS/SS/DS — though silicon also accepts
// 4..7 with implementation-defined behaviour; we treat them as no-op).
//
//   0x8C  MOV r/m16, sreg     reg → r/m16
//   0x8E  MOV sreg, r/m16     r/m16 → reg
//
// Writing CS via 0x8E reg=001 has undefined silicon behaviour (different
// chips behave differently — some reload CS, some ignore). We treat it
// as a normal segment write to keep behaviour deterministic; software
// that uses this is malformed.
//
// JSON shape:
//   x86_read_sreg_field   field=<name>  out=<name>      → i16
//   x86_write_sreg_field  field=<name>  in=[<name>]
// ============================================================================

internal sealed class X86ReadSregFieldEmitter : IMicroOpEmitter
{
    public string OpName => "x86_read_sreg_field";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var fieldName = step.Raw.GetProperty("field").GetString()!;
        var outName   = step.Raw.GetProperty("out").GetString()!;

        var i16 = LLVMTypeRef.Int16;
        var i32 = LLVMTypeRef.Int32;
        var sel = ctx.Resolve(fieldName);

        var endBB     = ctx.Function.AppendBasicBlock($"rsreg_{outName}_end");
        var defaultBB = ctx.Function.AppendBasicBlock($"rsreg_{outName}_default");
        var arms      = new LLVMBasicBlockRef[4];
        var armVals   = new LLVMValueRef[4];
        for (int i = 0; i < 4; i++) arms[i] = ctx.Function.AppendBasicBlock($"rsreg_{outName}_{i}");
        var sw = ctx.Builder.BuildSwitch(sel, defaultBB, 4);
        for (int i = 0; i < 4; i++)
            sw.AddCase(LLVMValueRef.CreateConstInt(i32, (uint)i, false), arms[i]);

        var names = new[] { "ES", "CS", "SS", "DS" };
        for (int i = 0; i < 4; i++)
        {
            ctx.Builder.PositionAtEnd(arms[i]);
            armVals[i] = X86_16Emitters.LoadSeg16(ctx, names[i], $"rsreg_{outName}_{i}_v");
            ctx.Builder.BuildBr(endBB);
        }
        // Default arm: invalid sreg encoding (4..7). Return 0 — will not
        // happen with well-formed software.
        ctx.Builder.PositionAtEnd(defaultBB);
        var defVal = LLVMValueRef.CreateConstInt(i16, 0, false);
        ctx.Builder.BuildBr(endBB);

        ctx.Builder.PositionAtEnd(endBB);
        var phi = ctx.Builder.BuildPhi(i16, outName);
        var inVals   = new LLVMValueRef[5];
        var inBlocks = new LLVMBasicBlockRef[5];
        for (int i = 0; i < 4; i++) { inVals[i] = armVals[i]; inBlocks[i] = arms[i]; }
        inVals[4] = defVal; inBlocks[4] = defaultBB;
        phi.AddIncoming(inVals, inBlocks, 5);

        ctx.Values[outName] = phi;
    }
}

internal sealed class X86WriteSregFieldEmitter : IMicroOpEmitter
{
    public string OpName => "x86_write_sreg_field";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var fieldName = step.Raw.GetProperty("field").GetString()!;
        var inArr     = step.Raw.GetProperty("in");
        var valueName = inArr[0].GetString()!;

        var i32 = LLVMTypeRef.Int32;
        var sel   = ctx.Resolve(fieldName);
        var value = ctx.Resolve(valueName);

        var endBB     = ctx.Function.AppendBasicBlock("wsreg_end");
        var defaultBB = ctx.Function.AppendBasicBlock("wsreg_default");
        var arms = new LLVMBasicBlockRef[4];
        for (int i = 0; i < 4; i++) arms[i] = ctx.Function.AppendBasicBlock($"wsreg_{i}");

        var sw = ctx.Builder.BuildSwitch(sel, defaultBB, 4);
        for (int i = 0; i < 4; i++)
            sw.AddCase(LLVMValueRef.CreateConstInt(i32, (uint)i, false), arms[i]);

        var names = new[] { "ES", "CS", "SS", "DS" };
        for (int i = 0; i < 4; i++)
        {
            ctx.Builder.PositionAtEnd(arms[i]);
            var p = ctx.GepStatusRegister(names[i]);
            ctx.Builder.BuildStore(value, p);
            ctx.Builder.BuildBr(endBB);
        }
        // Default: invalid encoding — silently no-op.
        ctx.Builder.PositionAtEnd(defaultBB);
        ctx.Builder.BuildBr(endBB);
        ctx.Builder.PositionAtEnd(endBB);
    }
}

// ============================================================================
// 24.6.5f — PUSH / POP family. All flavors share two SS:SP primitives:
//
//   PushW16(value):
//       SP -= 2
//       MEM[SS:SP] = value
//
//   PopW16() → value:
//       value = MEM[SS:SP]
//       SP += 2
//
// 8088 PUSH-SP quirk: silicon decrements SP BEFORE reading the operand
// register, so PUSH SP pushes the NEW (decremented) SP. We implement
// this naturally by ordering "decrement SP first, then read GPR" in
// every reg-source emitter — for fields other than SP the read is
// independent of SP, so the order doesn't matter; for SP itself the
// reader sees the post-decrement value, matching silicon. POP has no
// analogous quirk (POP SP just overwrites SP with the popped value).
// ============================================================================

internal static class X86StackHelpers
{
    /// <summary>SP -= 2; MEM[SS:SP] = value16.</summary>
    public static void PushW16(EmitContext ctx, LLVMValueRef value16, string label)
    {
        var i16 = LLVMTypeRef.Int16;
        var spPtr = ctx.GepGpr(4);   // SP is GPR index 4 in ModR/M order
        var spOld = ctx.Builder.BuildLoad2(i16, spPtr, $"{label}_sp_old");
        var spNew = ctx.Builder.BuildSub(spOld,
            LLVMValueRef.CreateConstInt(i16, 2, false), $"{label}_sp_new");
        ctx.Builder.BuildStore(spNew, spPtr);

        var ss = X86_16Emitters.LoadSeg16(ctx, "SS", $"{label}_ss");
        X86_16Emitters.SegmentedWrite16(ctx, ss, spNew, value16, $"{label}_w");
    }

    /// <summary>Read MEM[SS:SP] → i16; SP += 2.</summary>
    public static LLVMValueRef PopW16(EmitContext ctx, string label)
    {
        var i16 = LLVMTypeRef.Int16;
        var spPtr = ctx.GepGpr(4);
        var spOld = ctx.Builder.BuildLoad2(i16, spPtr, $"{label}_sp_old");
        var ss = X86_16Emitters.LoadSeg16(ctx, "SS", $"{label}_ss");
        var v  = X86_16Emitters.SegmentedRead16(ctx, ss, spOld, $"{label}_v");

        var spNew = ctx.Builder.BuildAdd(spOld,
            LLVMValueRef.CreateConstInt(i16, 2, false), $"{label}_sp_new");
        ctx.Builder.BuildStore(spNew, spPtr);
        return v;
    }
}

// ============================================================================
// x86_push_reg16_field — 0x50-0x57 PUSH r16. The 3-bit reg field encoded
// in the opcode's low bits selects the source GPR (000=AX..111=DI). The
// SP decrement happens BEFORE the GPR read so PUSH SP captures the new
// (decremented) SP value, matching 8088 silicon.
//
// JSON shape: { "op": "x86_push_reg16_field", "field": "<name>" }
// ============================================================================

internal sealed class X86PushReg16FieldEmitter : IMicroOpEmitter
{
    public string OpName => "x86_push_reg16_field";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var fieldName = step.Raw.GetProperty("field").GetString()!;
        var i16 = LLVMTypeRef.Int16;
        var i32 = LLVMTypeRef.Int32;

        // Decrement SP first (8088 PUSH-SP quirk).
        var spPtr = ctx.GepGpr(4);
        var spOld = ctx.Builder.BuildLoad2(i16, spPtr, "psh_sp_old");
        var spNew = ctx.Builder.BuildSub(spOld,
            LLVMValueRef.CreateConstInt(i16, 2, false), "psh_sp_new");
        ctx.Builder.BuildStore(spNew, spPtr);

        // Now read GPR by field — for field=SP this gets the post-decrement value.
        var sel = ctx.Resolve(fieldName);
        var endBB     = ctx.Function.AppendBasicBlock("psh_end");
        var defaultBB = ctx.Function.AppendBasicBlock("psh_default");
        var arms      = new LLVMBasicBlockRef[8];
        var armVals   = new LLVMValueRef[8];
        for (int i = 0; i < 8; i++) arms[i] = ctx.Function.AppendBasicBlock($"psh_{i}");
        var sw = ctx.Builder.BuildSwitch(sel, defaultBB, 8);
        for (int i = 0; i < 8; i++)
            sw.AddCase(LLVMValueRef.CreateConstInt(i32, (uint)i, false), arms[i]);

        for (int i = 0; i < 8; i++)
        {
            ctx.Builder.PositionAtEnd(arms[i]);
            armVals[i] = X86_16Emitters.ReadGpr16(ctx, i, $"psh_{i}_v");
            ctx.Builder.BuildBr(endBB);
        }
        ctx.Builder.PositionAtEnd(defaultBB);
        var defVal = LLVMValueRef.CreateConstInt(i16, 0, false);
        ctx.Builder.BuildBr(endBB);

        ctx.Builder.PositionAtEnd(endBB);
        var phi = ctx.Builder.BuildPhi(i16, "psh_value");
        var inVals   = new LLVMValueRef[9];
        var inBlocks = new LLVMBasicBlockRef[9];
        for (int i = 0; i < 8; i++) { inVals[i] = armVals[i]; inBlocks[i] = arms[i]; }
        inVals[8] = defVal; inBlocks[8] = defaultBB;
        phi.AddIncoming(inVals, inBlocks, 9);

        // Write the value to SS:newSP.
        var ss = X86_16Emitters.LoadSeg16(ctx, "SS", "psh_ss");
        X86_16Emitters.SegmentedWrite16(ctx, ss, spNew, phi, "psh_w");
    }
}

// ============================================================================
// x86_pop_reg16_field — 0x58-0x5F POP r16. POP has no quirk — read at
// SS:SP first, increment SP, write to GPR. POP SP overwrites SP with
// the popped value (the SP+=2 happens, then the GPR write replaces it).
//
// JSON shape: { "op": "x86_pop_reg16_field", "field": "<name>" }
// ============================================================================

internal sealed class X86PopReg16FieldEmitter : IMicroOpEmitter
{
    public string OpName => "x86_pop_reg16_field";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var fieldName = step.Raw.GetProperty("field").GetString()!;
        var i16 = LLVMTypeRef.Int16;
        var i32 = LLVMTypeRef.Int32;

        var v = X86StackHelpers.PopW16(ctx, "pop");

        var sel = ctx.Resolve(fieldName);
        var endBB     = ctx.Function.AppendBasicBlock("pop_end");
        var defaultBB = ctx.Function.AppendBasicBlock("pop_default");
        var arms = new LLVMBasicBlockRef[8];
        for (int i = 0; i < 8; i++) arms[i] = ctx.Function.AppendBasicBlock($"pop_{i}");
        var sw = ctx.Builder.BuildSwitch(sel, defaultBB, 8);
        for (int i = 0; i < 8; i++)
            sw.AddCase(LLVMValueRef.CreateConstInt(i32, (uint)i, false), arms[i]);

        for (int i = 0; i < 8; i++)
        {
            ctx.Builder.PositionAtEnd(arms[i]);
            X86_16Emitters.WriteGpr16(ctx, i, v);
            ctx.Builder.BuildBr(endBB);
        }
        ctx.Builder.PositionAtEnd(defaultBB);
        ctx.Builder.BuildBr(endBB);
        ctx.Builder.PositionAtEnd(endBB);
    }
}

// ============================================================================
// x86_push_seg / x86_pop_seg — PUSH/POP segment register (0x06/0x0E/0x16/0x1E
// for PUSH; 0x07/0x17/0x1F for POP — there is no POP CS at 0x0F, that
// byte is reserved for the 80286+ 2-byte opcode prefix).
//
// JSON shape: { "op": "x86_push_seg", "seg": "ES" }
//             { "op": "x86_pop_seg",  "seg": "ES" }
// ============================================================================

internal sealed class X86PushSegEmitter : IMicroOpEmitter
{
    public string OpName => "x86_push_seg";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var segName = step.Raw.GetProperty("seg").GetString()!;
        var v = X86_16Emitters.LoadSeg16(ctx, segName, $"psh_{segName}");
        X86StackHelpers.PushW16(ctx, v, $"psh_{segName}");
    }
}

internal sealed class X86PopSegEmitter : IMicroOpEmitter
{
    public string OpName => "x86_pop_seg";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var segName = step.Raw.GetProperty("seg").GetString()!;
        var v = X86StackHelpers.PopW16(ctx, $"pop_{segName}");
        var p = ctx.GepStatusRegister(segName);
        ctx.Builder.BuildStore(v, p);
    }
}

// ============================================================================
// x86_push_flags / x86_pop_flags — PUSHF (0x9C) / POPF (0x9D).
//
// 8086 reserved-bit policy (per Intel iAPX 86,88 manual):
//   bits 1, 12-15: forced to 1 on PUSHF
//   bits 3, 5:     forced to 0 on PUSHF
//   bits 0, 2, 4, 6, 7, 8, 9, 10, 11: the 9 architectural flags
//
// PUSHF mask: pushed = (FLAGS & 0x0FD7) | 0xF002
//   0x0FD7 = real flag bits (CF/PF/AF/ZF/SF/TF/IF/DF/OF) preserved
//   0xF002 = bit 1 + bits 12-15 forced to 1
//
// POPF: write the popped value as-is into FLAGS storage. Reserved bits
// in our representation are read-mostly (only PUSHF/PUSHFD observe them).
// Tom Harte SST coverage in 24.6.7 will surface any silicon-quirk
// mismatches; this minimal mask is correct for non-reserved-bit
// programs (i.e. all real software).
// ============================================================================

internal sealed class X86PushFlagsEmitter : IMicroOpEmitter
{
    public string OpName => "x86_push_flags";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i16 = LLVMTypeRef.Int16;
        var fPtr = ctx.GepStatusRegister("FLAGS");
        var raw = ctx.Builder.BuildLoad2(i16, fPtr, "psh_flags_raw");
        var keep = ctx.Builder.BuildAnd(raw,
            LLVMValueRef.CreateConstInt(i16, 0x0FD7, false), "psh_flags_keep");
        var masked = ctx.Builder.BuildOr(keep,
            LLVMValueRef.CreateConstInt(i16, 0xF002, false), "psh_flags_masked");
        X86StackHelpers.PushW16(ctx, masked, "psh_flags");
    }
}

internal sealed class X86PopFlagsEmitter : IMicroOpEmitter
{
    public string OpName => "x86_pop_flags";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var v = X86StackHelpers.PopW16(ctx, "pop_flags");
        var fPtr = ctx.GepStatusRegister("FLAGS");
        ctx.Builder.BuildStore(v, fPtr);
    }
}

// ============================================================================
// x86_push_modrm_w16 / x86_pop_modrm_w16 — PUSH r/m16 (0xFF /6) and
// POP r/m16 (0x8F /0). Both follow the existing fetch_modrm +
// modrm_compute_ea pattern, then route through the appropriate
// stack helper depending on mod=11 vs memory.
//
// PUSH r/m16:
//   if mod=11: SP-=2; write GPR[rm] (post-decrement value if rm=SP)
//   else:      load value from EA; SP-=2; write value to SS:SP
//
// POP r/m16:
//   read at SS:SP; SP+=2; write to GPR[rm] OR memory (overwrites SP if rm=SP)
// ============================================================================

internal sealed class X86PushModRmW16Emitter : IMicroOpEmitter
{
    public string OpName => "x86_push_modrm_w16";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i16 = LLVMTypeRef.Int16;
        var i32 = LLVMTypeRef.Int32;
        var mod = ctx.Resolve("modrm_mod");
        var rm  = ctx.Resolve("modrm_rm");

        var isReg = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, mod,
            LLVMValueRef.CreateConstInt(i32, 3, false), "pshrm_isreg");

        var regBB = ctx.Function.AppendBasicBlock("pshrm_reg");
        var memBB = ctx.Function.AppendBasicBlock("pshrm_mem");
        var endBB = ctx.Function.AppendBasicBlock("pshrm_end");
        ctx.Builder.BuildCondBr(isReg, regBB, memBB);

        // Reg path: same as push_reg16_field — decrement SP, then read GPR.
        ctx.Builder.PositionAtEnd(regBB);
        var spPtr = ctx.GepGpr(4);
        var spOld = ctx.Builder.BuildLoad2(i16, spPtr, "pshrm_sp_old");
        var spNew = ctx.Builder.BuildSub(spOld,
            LLVMValueRef.CreateConstInt(i16, 2, false), "pshrm_sp_new");
        ctx.Builder.BuildStore(spNew, spPtr);
        // Switch on rm to pick the GPR (after decrement, so SP gives new value).
        var armEnd     = ctx.Function.AppendBasicBlock("pshrm_arm_end");
        var armDef     = ctx.Function.AppendBasicBlock("pshrm_arm_def");
        var armBlocks  = new LLVMBasicBlockRef[8];
        var armVals    = new LLVMValueRef[8];
        for (int i = 0; i < 8; i++) armBlocks[i] = ctx.Function.AppendBasicBlock($"pshrm_arm_{i}");
        var sw = ctx.Builder.BuildSwitch(rm, armDef, 8);
        for (int i = 0; i < 8; i++)
            sw.AddCase(LLVMValueRef.CreateConstInt(i32, (uint)i, false), armBlocks[i]);
        for (int i = 0; i < 8; i++)
        {
            ctx.Builder.PositionAtEnd(armBlocks[i]);
            armVals[i] = X86_16Emitters.ReadGpr16(ctx, i, $"pshrm_arm_{i}_v");
            ctx.Builder.BuildBr(armEnd);
        }
        ctx.Builder.PositionAtEnd(armDef);
        var defVal = LLVMValueRef.CreateConstInt(i16, 0, false);
        ctx.Builder.BuildBr(armEnd);
        ctx.Builder.PositionAtEnd(armEnd);
        var phi = ctx.Builder.BuildPhi(i16, "pshrm_v");
        var pIns = new LLVMValueRef[9]; var pBlk = new LLVMBasicBlockRef[9];
        for (int i = 0; i < 8; i++) { pIns[i] = armVals[i]; pBlk[i] = armBlocks[i]; }
        pIns[8] = defVal; pBlk[8] = armDef;
        phi.AddIncoming(pIns, pBlk, 9);
        var ssReg = X86_16Emitters.LoadSeg16(ctx, "SS", "pshrm_ss");
        X86_16Emitters.SegmentedWrite16(ctx, ssReg, spNew, phi, "pshrm_w");
        ctx.Builder.BuildBr(endBB);

        // Mem path: load operand value from EA first, then push.
        ctx.Builder.PositionAtEnd(memBB);
        var seg = ctx.Resolve("ea_seg");
        var off = ctx.Resolve("ea_off");
        var memVal = X86_16Emitters.SegmentedRead16(ctx, seg, off, "pshrm_mem_v");
        X86StackHelpers.PushW16(ctx, memVal, "pshrm_mem");
        ctx.Builder.BuildBr(endBB);

        ctx.Builder.PositionAtEnd(endBB);
    }
}

internal sealed class X86PopModRmW16Emitter : IMicroOpEmitter
{
    public string OpName => "x86_pop_modrm_w16";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        // Pop the value first (read + SP+=2), then write to dest. For
        // mod=11 r/m=SP this overwrites SP with the popped value (not
        // SP+2) — that's the architectural semantic.
        var v = X86StackHelpers.PopW16(ctx, "poprm");
        X86ModRmMemHelpers.BuildStoreW16(ctx, v);
    }
}
