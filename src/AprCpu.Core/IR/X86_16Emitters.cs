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

        // 24.6.5g — XCHG / LEA / LDS / LES.
        reg.Register(new X86XchgAxReg16FieldEmitter());
        reg.Register(new X86LeaEmitter());
        reg.Register(new X86LoadFarPointerEmitter());

        // 24.6.6 — ALU + 9-flag IR computation. Single emitter per width
        // takes `kind` (add/or/adc/sbb/and/sub/xor/cmp) as a JSON
        // parameter; lhs/rhs/out reference the value cache.
        reg.Register(new X86AluW8Emitter());
        reg.Register(new X86AluW16Emitter());

        // Named-GPR helpers for fixed-register operands (AL/AH/AX, etc.)
        // used by the AL/AX-immediate ALU forms (0x04/0x05/0x0C/0x0D/...).
        reg.Register(new X86ReadNamedGprEmitter());
        reg.Register(new X86WriteNamedGprEmitter());

        // 24.6.6c — INC/DEC r16 (40-4F): same ALU flag rules as ADD/SUB
        // but CF preserved (so a separate path with mask=0x08D4).
        reg.Register(new X86IncReg16FieldEmitter());
        reg.Register(new X86DecReg16FieldEmitter());

        // 24.6.6c — 0x80-0x83 ALU r/m, imm group dispatcher. modrm.reg
        // selects the ALU op at runtime (0=ADD..7=CMP); CMP skips the
        // writeback step.
        reg.Register(new X86AluGroupModrmImmW8Emitter());
        reg.Register(new X86AluGroupModrmImmW16Emitter());

        // 24.6.6c — fetch i8 from CS:IP, sign-extend to i16. Used by 0x83
        // (ALU r/m16, sign-extended imm8) so the operand fits into the
        // i16 ALU lane while preserving signed value.
        reg.Register(new X86FetchImm8SextW16Emitter());

        // 24.6.6d — TEST (84/85/A8/A9), NOT/NEG (F6 /2 /3, F7 /2 /3),
        // and the F6/F7 group dispatcher (24.6.6e adds MUL/IMUL too;
        // DIV/IDIV stays no-op pending silicon-quirk verification).
        reg.Register(new X86TestW8Emitter());
        reg.Register(new X86TestW16Emitter());
        reg.Register(new X86NotW8Emitter());
        reg.Register(new X86NotW16Emitter());
        reg.Register(new X86NegW8Emitter());
        reg.Register(new X86NegW16Emitter());
        reg.Register(new X86F6GroupDispatchEmitter());
        reg.Register(new X86F7GroupDispatchEmitter());

        // 24.6.6e — CBW/CWD (sign extend AL→AX, AX→DX:AX). MUL/IMUL are
        // wired into the F6/F7 dispatchers in this same sub-step (no
        // separate emitters — they live inside the dispatcher).
        reg.Register(new X86CbwEmitter());
        reg.Register(new X86CwdEmitter());

        // 24.6.7a — control flow basics: JMP rel8/16, Jcc, JCXZ, LOOP*,
        // CALL rel16, RET (near).
        reg.Register(new X86JmpRel8Emitter());
        reg.Register(new X86JmpRel16Emitter());
        reg.Register(new X86JccRel8Emitter());
        reg.Register(new X86JcxzRel8Emitter());
        reg.Register(new X86LoopEmitter());
        reg.Register(new X86CallRel16Emitter());
        reg.Register(new X86RetNearEmitter());
        reg.Register(new X86RetNearImm16Emitter());

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

// ============================================================================
// 24.6.5g — XCHG AX, r16 (90-97). Opcode encodes the OTHER register in
// low 3 bits. 0x90 = XCHG AX,AX semantically equals NOP and is shadowed
// by the smoke group's NOP entry (mask=0xFF beats the broader mask=0xF8
// here). 0x91-0x97 all swap AX with CX/DX/BX/SP/BP/SI/DI.
//
// JSON shape: { "op": "x86_xchg_ax_reg16_field", "field": "<name>" }
// ============================================================================

internal sealed class X86XchgAxReg16FieldEmitter : IMicroOpEmitter
{
    public string OpName => "x86_xchg_ax_reg16_field";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var fieldName = step.Raw.GetProperty("field").GetString()!;
        var i16 = LLVMTypeRef.Int16;
        var i32 = LLVMTypeRef.Int32;

        // Snapshot AX BEFORE writing anything, so the swap is atomic.
        var axOrig = X86_16Emitters.ReadGpr16(ctx, 0, "xchg_ax_orig");

        // Read the other reg via runtime field dispatch (8-arm switch).
        var sel = ctx.Resolve(fieldName);
        var endBB     = ctx.Function.AppendBasicBlock("xchg_other_end");
        var defaultBB = ctx.Function.AppendBasicBlock("xchg_other_default");
        var arms      = new LLVMBasicBlockRef[8];
        var armVals   = new LLVMValueRef[8];
        for (int i = 0; i < 8; i++) arms[i] = ctx.Function.AppendBasicBlock($"xchg_other_{i}");
        var sw = ctx.Builder.BuildSwitch(sel, defaultBB, 8);
        for (int i = 0; i < 8; i++)
            sw.AddCase(LLVMValueRef.CreateConstInt(i32, (uint)i, false), arms[i]);
        for (int i = 0; i < 8; i++)
        {
            ctx.Builder.PositionAtEnd(arms[i]);
            armVals[i] = X86_16Emitters.ReadGpr16(ctx, i, $"xchg_other_{i}_v");
            ctx.Builder.BuildBr(endBB);
        }
        ctx.Builder.PositionAtEnd(defaultBB);
        var defVal = LLVMValueRef.CreateConstInt(i16, 0, false);
        ctx.Builder.BuildBr(endBB);

        ctx.Builder.PositionAtEnd(endBB);
        var otherVal = ctx.Builder.BuildPhi(i16, "xchg_other_v");
        var pIns = new LLVMValueRef[9]; var pBlk = new LLVMBasicBlockRef[9];
        for (int i = 0; i < 8; i++) { pIns[i] = armVals[i]; pBlk[i] = arms[i]; }
        pIns[8] = defVal; pBlk[8] = defaultBB;
        otherVal.AddIncoming(pIns, pBlk, 9);

        // AX := otherVal (always — index 0)
        X86_16Emitters.WriteGpr16(ctx, 0, otherVal);

        // GPR[field] := axOrig — runtime switch on field again.
        var endBB2     = ctx.Function.AppendBasicBlock("xchg_writeback_end");
        var defaultBB2 = ctx.Function.AppendBasicBlock("xchg_writeback_default");
        var arms2 = new LLVMBasicBlockRef[8];
        for (int i = 0; i < 8; i++) arms2[i] = ctx.Function.AppendBasicBlock($"xchg_wb_{i}");
        var sw2 = ctx.Builder.BuildSwitch(sel, defaultBB2, 8);
        for (int i = 0; i < 8; i++)
            sw2.AddCase(LLVMValueRef.CreateConstInt(i32, (uint)i, false), arms2[i]);
        for (int i = 0; i < 8; i++)
        {
            ctx.Builder.PositionAtEnd(arms2[i]);
            X86_16Emitters.WriteGpr16(ctx, i, axOrig);
            ctx.Builder.BuildBr(endBB2);
        }
        ctx.Builder.PositionAtEnd(defaultBB2);
        ctx.Builder.BuildBr(endBB2);
        ctx.Builder.PositionAtEnd(endBB2);
    }
}

// ============================================================================
// 24.6.5g — LEA r16, m (0x8D). Computes the effective address from the
// ModR/M byte but does NOT load — writes the EA offset to GPR[modrm.reg].
// Spec convention is fetch_modrm + compute_ea + this emitter, which
// pulls ea_off out of the value cache.
//
// mod=11 LEA r,r is undefined per Intel; we emit the EA computation
// anyway (which produces 0 for the mod=11 path) and write that value.
//
// JSON shape: { "op": "x86_lea" }
// ============================================================================

internal sealed class X86LeaEmitter : IMicroOpEmitter
{
    public string OpName => "x86_lea";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i32 = LLVMTypeRef.Int32;
        var off = ctx.Resolve("ea_off");      // i16 from compute_ea
        var sel = ctx.Resolve("modrm_reg");

        var endBB     = ctx.Function.AppendBasicBlock("lea_end");
        var defaultBB = ctx.Function.AppendBasicBlock("lea_default");
        var arms = new LLVMBasicBlockRef[8];
        for (int i = 0; i < 8; i++) arms[i] = ctx.Function.AppendBasicBlock($"lea_{i}");
        var sw = ctx.Builder.BuildSwitch(sel, defaultBB, 8);
        for (int i = 0; i < 8; i++)
            sw.AddCase(LLVMValueRef.CreateConstInt(i32, (uint)i, false), arms[i]);

        for (int i = 0; i < 8; i++)
        {
            ctx.Builder.PositionAtEnd(arms[i]);
            X86_16Emitters.WriteGpr16(ctx, i, off);
            ctx.Builder.BuildBr(endBB);
        }
        ctx.Builder.PositionAtEnd(defaultBB);
        ctx.Builder.BuildBr(endBB);
        ctx.Builder.PositionAtEnd(endBB);
    }
}

// ============================================================================
// 24.6.5g — LDS / LES (C4/C5): load 32-bit far pointer.
//   mem[0..1] → GPR[modrm_reg] (16-bit offset)
//   mem[2..3] → DS or ES        (16-bit segment)
//
// Both reads share the SAME ea_seg (the segment computed by
// modrm_compute_ea — usually DS, possibly overridden). Offset wraps
// within the segment for the +2 high-half read.
//
// mod=11 forms are undefined per Intel (no memory operand); emitter
// silently uses the bogus EA from compute_ea, matching the broader
// no-trap policy.
//
// JSON shape: { "op": "x86_load_far_pointer", "dest_seg": "ES" | "DS" }
// ============================================================================

internal sealed class X86LoadFarPointerEmitter : IMicroOpEmitter
{
    public string OpName => "x86_load_far_pointer";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var destSeg = step.Raw.GetProperty("dest_seg").GetString()!;
        var i16 = LLVMTypeRef.Int16;
        var i32 = LLVMTypeRef.Int32;

        var seg = ctx.Resolve("ea_seg");
        var off = ctx.Resolve("ea_off");

        // Read offset half (16 bits at ea_seg:ea_off).
        var offsetHalf = X86_16Emitters.SegmentedRead16(ctx, seg, off, "lfp_off");

        // Read segment half (16 bits at ea_seg:ea_off+2).
        var off2 = ctx.Builder.BuildAdd(off,
            LLVMValueRef.CreateConstInt(i16, 2, false), "lfp_off2");
        var segHalf = X86_16Emitters.SegmentedRead16(ctx, seg, off2, "lfp_seg");

        // Write offset to GPR[modrm_reg] via the existing field-dispatched
        // write helper inlined here (8-arm switch).
        var sel = ctx.Resolve("modrm_reg");
        var endBB     = ctx.Function.AppendBasicBlock("lfp_wgpr_end");
        var defaultBB = ctx.Function.AppendBasicBlock("lfp_wgpr_default");
        var arms = new LLVMBasicBlockRef[8];
        for (int i = 0; i < 8; i++) arms[i] = ctx.Function.AppendBasicBlock($"lfp_wgpr_{i}");
        var sw = ctx.Builder.BuildSwitch(sel, defaultBB, 8);
        for (int i = 0; i < 8; i++)
            sw.AddCase(LLVMValueRef.CreateConstInt(i32, (uint)i, false), arms[i]);
        for (int i = 0; i < 8; i++)
        {
            ctx.Builder.PositionAtEnd(arms[i]);
            X86_16Emitters.WriteGpr16(ctx, i, offsetHalf);
            ctx.Builder.BuildBr(endBB);
        }
        ctx.Builder.PositionAtEnd(defaultBB);
        ctx.Builder.BuildBr(endBB);
        ctx.Builder.PositionAtEnd(endBB);

        // Write segment to the named destination (DS or ES).
        var p = ctx.GepStatusRegister(destSeg);
        ctx.Builder.BuildStore(segHalf, p);
    }
}

// ============================================================================
// 24.6.6 — ALU + 9-flag IR computation.
//
// Mirrors X86Alu.cs (the silicon-accurate hand-coded oracle in the legacy
// backend) at the LLVM IR level. Each ALU op emits:
//   1. The arithmetic/logical computation at i32 width (so carry/borrow
//      bits beyond the operand width are observable).
//   2. The flag rules for that op kind (CF/PF/AF/ZF/SF/OF — only those
//      affected; logical ops force CF/OF/AF=0, arithmetic ops compute all).
//   3. Truncation back to the operand width for the result.
//   4. FLAGS-register update via masked OR (preserve unaffected bits).
//
// FLAGS bit layout in the spec:
//   CF=0, PF=2, AF=4, ZF=6, SF=7, OF=11
// Mask of "ALU-affected" bits: 0x08D5 = (1<<0)|(1<<2)|(1<<4)|(1<<6)|(1<<7)|(1<<11)
//
// Parity flag uses LLVM's @llvm.ctpop intrinsic on the low byte. Even
// parity → PF=1, odd → PF=0. (8086 PF reflects only the low 8 bits of
// the result regardless of operand width.)
//
// JSON shape:
//   { "op": "x86_alu_w8" | "x86_alu_w16",
//     "kind": "add"|"or"|"adc"|"sbb"|"and"|"sub"|"xor"|"cmp",
//     "lhs":  "<cache name>",
//     "rhs":  "<cache name>",
//     "out":  "<cache name>" }
//
// CMP follows the SUB flag rules but does NOT cache its result (the
// caller's spec just omits the writeback step).
// ============================================================================

internal static class X86AluHelpers
{
    /// <summary>
    /// Emit the parity check for the low 8 bits of <paramref name="value"/>:
    /// PF = 1 when the byte has an even number of set bits, 0 otherwise.
    /// Uses @llvm.ctpop.i8 intrinsic via a declared extern. Returns i1.
    /// </summary>
    private static LLVMValueRef BuildParityEven(EmitContext ctx, LLVMValueRef byteVal, string label)
    {
        var i8  = LLVMTypeRef.Int8;
        var i32 = LLVMTypeRef.Int32;

        // Declare i8 @llvm.ctpop.i8(i8) on first reference.
        var ctpopName = "llvm.ctpop.i8";
        var fn = ctx.Module.GetNamedFunction(ctpopName);
        if (fn.Handle == IntPtr.Zero)
        {
            var fnType = LLVMTypeRef.CreateFunction(i8, new[] { i8 }, false);
            fn = ctx.Module.AddFunction(ctpopName, fnType);
        }
        var fnType2 = LLVMTypeRef.CreateFunction(i8, new[] { i8 }, false);
        var pop = ctx.Builder.BuildCall2(fnType2, fn, new[] { byteVal }, $"{label}_pop");

        var lsb = ctx.Builder.BuildAnd(pop,
            LLVMValueRef.CreateConstInt(i8, 1, false), $"{label}_pop_lsb");
        // Even = (lsb == 0)
        return ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, lsb,
            LLVMValueRef.CreateConstInt(i8, 0, false), $"{label}_pf");
    }

    /// <summary>
    /// Pack 6 individual i1 flags into the low bits of a 16-bit value at
    /// the canonical FLAGS positions (CF/PF/AF/ZF/SF/OF). Returns i16.
    /// </summary>
    private static LLVMValueRef BuildPackedFlags(
        EmitContext ctx,
        LLVMValueRef cf, LLVMValueRef pf, LLVMValueRef af,
        LLVMValueRef zf, LLVMValueRef sf, LLVMValueRef of,
        string label)
    {
        var i16 = LLVMTypeRef.Int16;
        LLVMValueRef ZxShl(LLVMValueRef bit, int pos, string n)
        {
            var z = ctx.Builder.BuildZExt(bit, i16, $"{label}_{n}_z");
            if (pos == 0) return z;
            return ctx.Builder.BuildShl(z,
                LLVMValueRef.CreateConstInt(i16, (ulong)pos, false), $"{label}_{n}_sh");
        }
        var bcf = ZxShl(cf, 0, "cf");
        var bpf = ZxShl(pf, 2, "pf");
        var baf = ZxShl(af, 4, "af");
        var bzf = ZxShl(zf, 6, "zf");
        var bsf = ZxShl(sf, 7, "sf");
        var bof = ZxShl(of, 11, "of");

        var t1 = ctx.Builder.BuildOr(bcf, bpf, $"{label}_t1");
        var t2 = ctx.Builder.BuildOr(t1, baf, $"{label}_t2");
        var t3 = ctx.Builder.BuildOr(t2, bzf, $"{label}_t3");
        var t4 = ctx.Builder.BuildOr(t3, bsf, $"{label}_t4");
        return ctx.Builder.BuildOr(t4, bof, $"{label}_packed");
    }

    /// <summary>
    /// Merge new flag bits at the 6 ALU-affected positions into FLAGS,
    /// preserving the other bits (TF/IF/DF, reserved bits, etc.).
    /// Mask of affected bits = 0x08D5.
    /// </summary>
    private static void StoreAluFlags(EmitContext ctx, LLVMValueRef packed, string label)
    {
        var i16 = LLVMTypeRef.Int16;
        var fPtr = ctx.GepStatusRegister("FLAGS");
        var current = ctx.Builder.BuildLoad2(i16, fPtr, $"{label}_flags_cur");
        var mask = LLVMValueRef.CreateConstInt(i16, 0x08D5, false);
        var notMask = LLVMValueRef.CreateConstInt(i16, (ulong)(unchecked((ushort)~0x08D5)), false);
        var keep = ctx.Builder.BuildAnd(current, notMask, $"{label}_flags_keep");
        var maskedNew = ctx.Builder.BuildAnd(packed, mask, $"{label}_flags_new_masked");
        var merged = ctx.Builder.BuildOr(keep, maskedNew, $"{label}_flags_merged");
        ctx.Builder.BuildStore(merged, fPtr);
    }

    /// <summary>
    /// Emit the 8-bit ALU op of <paramref name="kind"/> on
    /// (<paramref name="lhs"/>, <paramref name="rhs"/>). Returns the i8 result.
    /// Updates the FLAGS register's CF/PF/AF/ZF/SF/OF bits per the kind's rules.
    /// </summary>
    public static LLVMValueRef BuildAluW8(
        EmitContext ctx, string kind, LLVMValueRef lhs8, LLVMValueRef rhs8, string label)
    {
        var i1  = LLVMTypeRef.Int1;
        var i8  = LLVMTypeRef.Int8;
        var i32 = LLVMTypeRef.Int32;

        bool isLogical = kind is "and" or "or" or "xor";
        bool isAdd     = kind is "add" or "adc";
        bool isSub     = kind is "sub" or "sbb" or "cmp";
        bool useCarry  = kind is "adc" or "sbb";

        // Widen to i32 for carry detection.
        var aZ = ctx.Builder.BuildZExt(lhs8, i32, $"{label}_a32");
        var bZ = ctx.Builder.BuildZExt(rhs8, i32, $"{label}_b32");

        // Carry-in for ADC/SBB (read CF from FLAGS).
        LLVMValueRef cfIn32;
        if (useCarry)
        {
            var fPtr = ctx.GepStatusRegister("FLAGS");
            var f16  = ctx.Builder.BuildLoad2(LLVMTypeRef.Int16, fPtr, $"{label}_f16");
            var f32  = ctx.Builder.BuildZExt(f16, i32, $"{label}_f32");
            cfIn32 = ctx.Builder.BuildAnd(f32,
                LLVMValueRef.CreateConstInt(i32, 1, false), $"{label}_cfin");
        }
        else
        {
            cfIn32 = LLVMValueRef.CreateConstInt(i32, 0, false);
        }

        // Compute the raw result at i32 width.
        LLVMValueRef raw32 = kind switch
        {
            "add" => ctx.Builder.BuildAdd(aZ, bZ, $"{label}_raw"),
            "adc" => ctx.Builder.BuildAdd(ctx.Builder.BuildAdd(aZ, bZ, $"{label}_ab"), cfIn32, $"{label}_raw"),
            "sub" => ctx.Builder.BuildSub(aZ, bZ, $"{label}_raw"),
            "sbb" => ctx.Builder.BuildSub(ctx.Builder.BuildSub(aZ, bZ, $"{label}_ab"), cfIn32, $"{label}_raw"),
            "cmp" => ctx.Builder.BuildSub(aZ, bZ, $"{label}_raw"),
            "and" => ctx.Builder.BuildAnd(aZ, bZ, $"{label}_raw"),
            "or"  => ctx.Builder.BuildOr (aZ, bZ, $"{label}_raw"),
            "xor" => ctx.Builder.BuildXor(aZ, bZ, $"{label}_raw"),
            _ => throw new InvalidOperationException($"unknown ALU kind '{kind}'"),
        };

        // Truncate to i8 result.
        var r8  = ctx.Builder.BuildTrunc(raw32, i8, $"{label}_r8");
        var r32 = ctx.Builder.BuildZExt(r8, i32, $"{label}_r32");

        // -------- flag computation --------
        LLVMValueRef cf, of, af;

        if (isLogical)
        {
            // Logical ops force CF/OF/AF=0. Use an explicit i1 false
            // constant — earlier "(0==0)" pattern accidentally gave TRUE.
            var i1false = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, 0, false);
            cf = i1false;
            of = i1false;
            af = i1false;
        }
        else if (isSub)
        {
            // CF = (raw32 & 0x100) != 0 — borrow into MSB
            var c100 = ctx.Builder.BuildAnd(raw32,
                LLVMValueRef.CreateConstInt(i32, 0x100, false), $"{label}_cmask");
            cf = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, c100,
                LLVMValueRef.CreateConstInt(i32, 0, false), $"{label}_cf");
            // OF = ((a^b) & (a^r)) & 0x80
            var xab = ctx.Builder.BuildXor(aZ, bZ, $"{label}_xab");
            var xar = ctx.Builder.BuildXor(aZ, r32, $"{label}_xar");
            var both = ctx.Builder.BuildAnd(xab, xar, $"{label}_xboth");
            var omask = ctx.Builder.BuildAnd(both,
                LLVMValueRef.CreateConstInt(i32, 0x80, false), $"{label}_omask");
            of = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, omask,
                LLVMValueRef.CreateConstInt(i32, 0, false), $"{label}_of");
            // AF = ((a^b^r) & 0x10) != 0
            var afXor = ctx.Builder.BuildXor(ctx.Builder.BuildXor(aZ, bZ, $"{label}_afx1"), r32, $"{label}_afx2");
            var afMask = ctx.Builder.BuildAnd(afXor,
                LLVMValueRef.CreateConstInt(i32, 0x10, false), $"{label}_afmask");
            af = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, afMask,
                LLVMValueRef.CreateConstInt(i32, 0, false), $"{label}_af");
        }
        else // add/adc
        {
            var c100 = ctx.Builder.BuildAnd(raw32,
                LLVMValueRef.CreateConstInt(i32, 0x100, false), $"{label}_cmask");
            cf = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, c100,
                LLVMValueRef.CreateConstInt(i32, 0, false), $"{label}_cf");
            // OF = ((a^r) & (b^r)) & 0x80
            var xar = ctx.Builder.BuildXor(aZ, r32, $"{label}_xar");
            var xbr = ctx.Builder.BuildXor(bZ, r32, $"{label}_xbr");
            var both = ctx.Builder.BuildAnd(xar, xbr, $"{label}_xboth");
            var omask = ctx.Builder.BuildAnd(both,
                LLVMValueRef.CreateConstInt(i32, 0x80, false), $"{label}_omask");
            of = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, omask,
                LLVMValueRef.CreateConstInt(i32, 0, false), $"{label}_of");
            // AF — same XOR formula as sub
            var afXor = ctx.Builder.BuildXor(ctx.Builder.BuildXor(aZ, bZ, $"{label}_afx1"), r32, $"{label}_afx2");
            var afMask = ctx.Builder.BuildAnd(afXor,
                LLVMValueRef.CreateConstInt(i32, 0x10, false), $"{label}_afmask");
            af = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, afMask,
                LLVMValueRef.CreateConstInt(i32, 0, false), $"{label}_af");
        }

        // SF = high bit of result
        var sfMask = ctx.Builder.BuildAnd(r8,
            LLVMValueRef.CreateConstInt(i8, 0x80, false), $"{label}_sfmask");
        var sf = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, sfMask,
            LLVMValueRef.CreateConstInt(i8, 0, false), $"{label}_sf");
        // ZF = result == 0
        var zf = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, r8,
            LLVMValueRef.CreateConstInt(i8, 0, false), $"{label}_zf");
        // PF = parity-even of low byte
        var pf = BuildParityEven(ctx, r8, label);

        var packed = BuildPackedFlags(ctx, cf, pf, af, zf, sf, of, label);
        StoreAluFlags(ctx, packed, label);

        return r8;
    }

    /// <summary>16-bit counterpart of <see cref="BuildAluW8"/>.</summary>
    public static LLVMValueRef BuildAluW16(
        EmitContext ctx, string kind, LLVMValueRef lhs16, LLVMValueRef rhs16, string label)
    {
        var i1  = LLVMTypeRef.Int1;
        var i8  = LLVMTypeRef.Int8;
        var i16 = LLVMTypeRef.Int16;
        var i32 = LLVMTypeRef.Int32;

        bool isLogical = kind is "and" or "or" or "xor";
        bool isAdd     = kind is "add" or "adc";
        bool isSub     = kind is "sub" or "sbb" or "cmp";
        bool useCarry  = kind is "adc" or "sbb";

        var aZ = ctx.Builder.BuildZExt(lhs16, i32, $"{label}_a32");
        var bZ = ctx.Builder.BuildZExt(rhs16, i32, $"{label}_b32");

        LLVMValueRef cfIn32;
        if (useCarry)
        {
            var fPtr = ctx.GepStatusRegister("FLAGS");
            var f16  = ctx.Builder.BuildLoad2(i16, fPtr, $"{label}_f16");
            var f32  = ctx.Builder.BuildZExt(f16, i32, $"{label}_f32");
            cfIn32 = ctx.Builder.BuildAnd(f32,
                LLVMValueRef.CreateConstInt(i32, 1, false), $"{label}_cfin");
        }
        else
        {
            cfIn32 = LLVMValueRef.CreateConstInt(i32, 0, false);
        }

        LLVMValueRef raw32 = kind switch
        {
            "add" => ctx.Builder.BuildAdd(aZ, bZ, $"{label}_raw"),
            "adc" => ctx.Builder.BuildAdd(ctx.Builder.BuildAdd(aZ, bZ, $"{label}_ab"), cfIn32, $"{label}_raw"),
            "sub" => ctx.Builder.BuildSub(aZ, bZ, $"{label}_raw"),
            "sbb" => ctx.Builder.BuildSub(ctx.Builder.BuildSub(aZ, bZ, $"{label}_ab"), cfIn32, $"{label}_raw"),
            "cmp" => ctx.Builder.BuildSub(aZ, bZ, $"{label}_raw"),
            "and" => ctx.Builder.BuildAnd(aZ, bZ, $"{label}_raw"),
            "or"  => ctx.Builder.BuildOr (aZ, bZ, $"{label}_raw"),
            "xor" => ctx.Builder.BuildXor(aZ, bZ, $"{label}_raw"),
            _ => throw new InvalidOperationException($"unknown ALU kind '{kind}'"),
        };

        var r16 = ctx.Builder.BuildTrunc(raw32, i16, $"{label}_r16");
        var r32 = ctx.Builder.BuildZExt(r16, i32, $"{label}_r32");

        LLVMValueRef cf, of, af;

        if (isLogical)
        {
            // Logical ops force CF/OF/AF=0 — same fix as BuildAluW8.
            var i1false = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, 0, false);
            cf = i1false;
            of = i1false;
            af = i1false;
        }
        else if (isSub)
        {
            var c10000 = ctx.Builder.BuildAnd(raw32,
                LLVMValueRef.CreateConstInt(i32, 0x10000, false), $"{label}_cmask");
            cf = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, c10000,
                LLVMValueRef.CreateConstInt(i32, 0, false), $"{label}_cf");
            var xab = ctx.Builder.BuildXor(aZ, bZ, $"{label}_xab");
            var xar = ctx.Builder.BuildXor(aZ, r32, $"{label}_xar");
            var both = ctx.Builder.BuildAnd(xab, xar, $"{label}_xboth");
            var omask = ctx.Builder.BuildAnd(both,
                LLVMValueRef.CreateConstInt(i32, 0x8000, false), $"{label}_omask");
            of = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, omask,
                LLVMValueRef.CreateConstInt(i32, 0, false), $"{label}_of");
            var afXor = ctx.Builder.BuildXor(ctx.Builder.BuildXor(aZ, bZ, $"{label}_afx1"), r32, $"{label}_afx2");
            var afMask = ctx.Builder.BuildAnd(afXor,
                LLVMValueRef.CreateConstInt(i32, 0x10, false), $"{label}_afmask");
            af = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, afMask,
                LLVMValueRef.CreateConstInt(i32, 0, false), $"{label}_af");
        }
        else
        {
            var c10000 = ctx.Builder.BuildAnd(raw32,
                LLVMValueRef.CreateConstInt(i32, 0x10000, false), $"{label}_cmask");
            cf = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, c10000,
                LLVMValueRef.CreateConstInt(i32, 0, false), $"{label}_cf");
            var xar = ctx.Builder.BuildXor(aZ, r32, $"{label}_xar");
            var xbr = ctx.Builder.BuildXor(bZ, r32, $"{label}_xbr");
            var both = ctx.Builder.BuildAnd(xar, xbr, $"{label}_xboth");
            var omask = ctx.Builder.BuildAnd(both,
                LLVMValueRef.CreateConstInt(i32, 0x8000, false), $"{label}_omask");
            of = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, omask,
                LLVMValueRef.CreateConstInt(i32, 0, false), $"{label}_of");
            var afXor = ctx.Builder.BuildXor(ctx.Builder.BuildXor(aZ, bZ, $"{label}_afx1"), r32, $"{label}_afx2");
            var afMask = ctx.Builder.BuildAnd(afXor,
                LLVMValueRef.CreateConstInt(i32, 0x10, false), $"{label}_afmask");
            af = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, afMask,
                LLVMValueRef.CreateConstInt(i32, 0, false), $"{label}_af");
        }

        var sfMask = ctx.Builder.BuildAnd(r16,
            LLVMValueRef.CreateConstInt(i16, 0x8000, false), $"{label}_sfmask");
        var sf = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, sfMask,
            LLVMValueRef.CreateConstInt(i16, 0, false), $"{label}_sf");
        var zf = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, r16,
            LLVMValueRef.CreateConstInt(i16, 0, false), $"{label}_zf");
        // PF reflects only the low byte regardless of width.
        var rLowByte = ctx.Builder.BuildTrunc(r16, i8, $"{label}_rlow");
        var pf = BuildParityEven(ctx, rLowByte, label);

        var packed = BuildPackedFlags(ctx, cf, pf, af, zf, sf, of, label);
        StoreAluFlags(ctx, packed, label);

        return r16;
    }
}

internal sealed class X86AluW8Emitter : IMicroOpEmitter
{
    public string OpName => "x86_alu_w8";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var kind = step.Raw.GetProperty("kind").GetString()!;
        var lhsName = step.Raw.GetProperty("lhs").GetString()!;
        var rhsName = step.Raw.GetProperty("rhs").GetString()!;
        var lhs = ctx.Resolve(lhsName);
        var rhs = ctx.Resolve(rhsName);
        var r = X86AluHelpers.BuildAluW8(ctx, kind, lhs, rhs, $"alu8_{kind}");
        if (step.Raw.TryGetProperty("out", out var outProp))
        {
            ctx.Values[outProp.GetString()!] = r;
        }
    }
}

internal sealed class X86AluW16Emitter : IMicroOpEmitter
{
    public string OpName => "x86_alu_w16";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var kind = step.Raw.GetProperty("kind").GetString()!;
        var lhsName = step.Raw.GetProperty("lhs").GetString()!;
        var rhsName = step.Raw.GetProperty("rhs").GetString()!;
        var lhs = ctx.Resolve(lhsName);
        var rhs = ctx.Resolve(rhsName);
        var r = X86AluHelpers.BuildAluW16(ctx, kind, lhs, rhs, $"alu16_{kind}");
        if (step.Raw.TryGetProperty("out", out var outProp))
        {
            ctx.Values[outProp.GetString()!] = r;
        }
    }
}

// ============================================================================
// x86_read_named_gpr / x86_write_named_gpr — fixed-register access by
// name (AL/AH/CL/CH/DL/DH/BL/BH/AX/CX/DX/BX/SP/BP/SI/DI). Used by
// instructions like ADD AL, imm8 where the operand register is hardcoded
// in the opcode rather than encoded in a ModR/M field.
//
// JSON shape:
//   { "op": "x86_read_named_gpr",  "name": "AL", "out": "<name>" }
//   { "op": "x86_write_named_gpr", "name": "AX", "in":  ["<name>"] }
// ============================================================================

internal sealed class X86ReadNamedGprEmitter : IMicroOpEmitter
{
    public string OpName => "x86_read_named_gpr";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var name    = step.Raw.GetProperty("name").GetString()!;
        var outName = step.Raw.GetProperty("out").GetString()!;
        ctx.Values[outName] = ReadNamedGpr(ctx, name, outName);
    }

    internal static LLVMValueRef ReadNamedGpr(EmitContext ctx, string name, string label)
    {
        // Map name to (parent index, half) — same encoding as ModR/M sreg/byte.
        // 16-bit: 0=AX 1=CX 2=DX 3=BX 4=SP 5=BP 6=SI 7=DI
        // 8-bit:  byteIdx 0..7 = AL CL DL BL AH CH DH BH
        switch (name)
        {
            case "AL": return X86_16Emitters.ReadGpr8(ctx, 0, label);
            case "CL": return X86_16Emitters.ReadGpr8(ctx, 1, label);
            case "DL": return X86_16Emitters.ReadGpr8(ctx, 2, label);
            case "BL": return X86_16Emitters.ReadGpr8(ctx, 3, label);
            case "AH": return X86_16Emitters.ReadGpr8(ctx, 4, label);
            case "CH": return X86_16Emitters.ReadGpr8(ctx, 5, label);
            case "DH": return X86_16Emitters.ReadGpr8(ctx, 6, label);
            case "BH": return X86_16Emitters.ReadGpr8(ctx, 7, label);
            case "AX": return X86_16Emitters.ReadGpr16(ctx, 0, label);
            case "CX": return X86_16Emitters.ReadGpr16(ctx, 1, label);
            case "DX": return X86_16Emitters.ReadGpr16(ctx, 2, label);
            case "BX": return X86_16Emitters.ReadGpr16(ctx, 3, label);
            case "SP": return X86_16Emitters.ReadGpr16(ctx, 4, label);
            case "BP": return X86_16Emitters.ReadGpr16(ctx, 5, label);
            case "SI": return X86_16Emitters.ReadGpr16(ctx, 6, label);
            case "DI": return X86_16Emitters.ReadGpr16(ctx, 7, label);
            default:
                throw new InvalidOperationException($"x86_read_named_gpr: unknown register '{name}'");
        }
    }
}

internal sealed class X86WriteNamedGprEmitter : IMicroOpEmitter
{
    public string OpName => "x86_write_named_gpr";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var name    = step.Raw.GetProperty("name").GetString()!;
        var inArr   = step.Raw.GetProperty("in");
        var valName = inArr[0].GetString()!;
        var v = ctx.Resolve(valName);
        WriteNamedGpr(ctx, name, v);
    }

    internal static void WriteNamedGpr(EmitContext ctx, string name, LLVMValueRef value)
    {
        switch (name)
        {
            case "AL": X86_16Emitters.WriteGpr8(ctx, 0, value); return;
            case "CL": X86_16Emitters.WriteGpr8(ctx, 1, value); return;
            case "DL": X86_16Emitters.WriteGpr8(ctx, 2, value); return;
            case "BL": X86_16Emitters.WriteGpr8(ctx, 3, value); return;
            case "AH": X86_16Emitters.WriteGpr8(ctx, 4, value); return;
            case "CH": X86_16Emitters.WriteGpr8(ctx, 5, value); return;
            case "DH": X86_16Emitters.WriteGpr8(ctx, 6, value); return;
            case "BH": X86_16Emitters.WriteGpr8(ctx, 7, value); return;
            case "AX": X86_16Emitters.WriteGpr16(ctx, 0, value); return;
            case "CX": X86_16Emitters.WriteGpr16(ctx, 1, value); return;
            case "DX": X86_16Emitters.WriteGpr16(ctx, 2, value); return;
            case "BX": X86_16Emitters.WriteGpr16(ctx, 3, value); return;
            case "SP": X86_16Emitters.WriteGpr16(ctx, 4, value); return;
            case "BP": X86_16Emitters.WriteGpr16(ctx, 5, value); return;
            case "SI": X86_16Emitters.WriteGpr16(ctx, 6, value); return;
            case "DI": X86_16Emitters.WriteGpr16(ctx, 7, value); return;
            default:
                throw new InvalidOperationException($"x86_write_named_gpr: unknown register '{name}'");
        }
    }
}

// ============================================================================
// 24.6.6c — INC/DEC r16 (0x40-0x47 INC, 0x48-0x4F DEC).
//
// Same flag rules as ADD/SUB(r,1) EXCEPT CF is preserved. Affected mask
// is 0x08D4 (bits 2/4/6/7/11 — PF/AF/ZF/SF/OF) instead of the full ALU
// 0x08D5. We compute via the existing ALU helper but then post-mask
// FLAGS to restore the original CF.
// ============================================================================

internal static class X86IncDecHelpers
{
    /// <summary>
    /// Read CF from FLAGS into i1, run BuildAluW16(add or sub, x, 1),
    /// then restore CF in the resulting FLAGS storage. Returns the new
    /// 16-bit value.
    /// </summary>
    public static LLVMValueRef BuildIncDecW16(
        EmitContext ctx, string kind, LLVMValueRef x16, string label)
    {
        var i16 = LLVMTypeRef.Int16;
        var fPtr = ctx.GepStatusRegister("FLAGS");
        var fOld = ctx.Builder.BuildLoad2(i16, fPtr, $"{label}_f_old");

        var one16 = LLVMValueRef.CreateConstInt(i16, 1, false);
        var r16 = X86AluHelpers.BuildAluW16(ctx, kind, x16, one16, $"{label}_alu");

        // Re-read FLAGS (BuildAluW16 just stored the new value), then
        // splice the old CF back in: new = (new & ~1) | (old & 1).
        var fNew = ctx.Builder.BuildLoad2(i16, fPtr, $"{label}_f_new");
        var newKeep = ctx.Builder.BuildAnd(fNew,
            LLVMValueRef.CreateConstInt(i16, 0xFFFE, false), $"{label}_f_keep");
        var oldCf = ctx.Builder.BuildAnd(fOld,
            LLVMValueRef.CreateConstInt(i16, 0x0001, false), $"{label}_f_oldcf");
        var merged = ctx.Builder.BuildOr(newKeep, oldCf, $"{label}_f_merged");
        ctx.Builder.BuildStore(merged, fPtr);
        return r16;
    }
}

internal sealed class X86IncReg16FieldEmitter : IMicroOpEmitter
{
    public string OpName => "x86_inc_reg16_field";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var fieldName = step.Raw.GetProperty("field").GetString()!;
        var i16 = LLVMTypeRef.Int16;
        var i32 = LLVMTypeRef.Int32;

        var sel = ctx.Resolve(fieldName);
        var endBB     = ctx.Function.AppendBasicBlock("inc_end");
        var defaultBB = ctx.Function.AppendBasicBlock("inc_default");
        var arms = new LLVMBasicBlockRef[8];
        for (int i = 0; i < 8; i++) arms[i] = ctx.Function.AppendBasicBlock($"inc_{i}");
        var sw = ctx.Builder.BuildSwitch(sel, defaultBB, 8);
        for (int i = 0; i < 8; i++)
            sw.AddCase(LLVMValueRef.CreateConstInt(i32, (uint)i, false), arms[i]);

        for (int i = 0; i < 8; i++)
        {
            ctx.Builder.PositionAtEnd(arms[i]);
            var r = X86_16Emitters.ReadGpr16(ctx, i, $"inc_{i}_v");
            var nr = X86IncDecHelpers.BuildIncDecW16(ctx, "add", r, $"inc_{i}");
            X86_16Emitters.WriteGpr16(ctx, i, nr);
            ctx.Builder.BuildBr(endBB);
        }
        ctx.Builder.PositionAtEnd(defaultBB);
        ctx.Builder.BuildBr(endBB);
        ctx.Builder.PositionAtEnd(endBB);
    }
}

internal sealed class X86DecReg16FieldEmitter : IMicroOpEmitter
{
    public string OpName => "x86_dec_reg16_field";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var fieldName = step.Raw.GetProperty("field").GetString()!;
        var i32 = LLVMTypeRef.Int32;

        var sel = ctx.Resolve(fieldName);
        var endBB     = ctx.Function.AppendBasicBlock("dec_end");
        var defaultBB = ctx.Function.AppendBasicBlock("dec_default");
        var arms = new LLVMBasicBlockRef[8];
        for (int i = 0; i < 8; i++) arms[i] = ctx.Function.AppendBasicBlock($"dec_{i}");
        var sw = ctx.Builder.BuildSwitch(sel, defaultBB, 8);
        for (int i = 0; i < 8; i++)
            sw.AddCase(LLVMValueRef.CreateConstInt(i32, (uint)i, false), arms[i]);

        for (int i = 0; i < 8; i++)
        {
            ctx.Builder.PositionAtEnd(arms[i]);
            var r = X86_16Emitters.ReadGpr16(ctx, i, $"dec_{i}_v");
            var nr = X86IncDecHelpers.BuildIncDecW16(ctx, "sub", r, $"dec_{i}");
            X86_16Emitters.WriteGpr16(ctx, i, nr);
            ctx.Builder.BuildBr(endBB);
        }
        ctx.Builder.PositionAtEnd(defaultBB);
        ctx.Builder.BuildBr(endBB);
        ctx.Builder.PositionAtEnd(endBB);
    }
}

// ============================================================================
// 24.6.6c — 0x80-0x83 ALU r/m, imm group dispatcher. The opcode byte
// selects width (8/16) and immediate width (8 or sign-extended 8 → 16);
// modrm.reg selects the ALU op (0=ADD..7=CMP). All in one composite
// emitter so the conditional writeback (skip for CMP) can be handled
// inside an 8-arm runtime switch:
//
//   switch (modrm_reg) {
//     case 0: result = ADD(lhs, rhs); modrm_store(result); break;
//     case 1: result = OR (lhs, rhs); modrm_store(result); break;
//     ...
//     case 7: result = CMP(lhs, rhs);            // no writeback
//   }
//
// JSON shape:
//   { "op": "x86_alu_group_modrm_imm_w8" }   — 0x80, 0x82
//   { "op": "x86_alu_group_modrm_imm_w16" }  — 0x81 (regular), 0x83 (sext)
//
// Spec convention: the lhs is preloaded by modrm_load_w{8,16} → "lhs"
// and the rhs by fetch_imm{8,16} or sext-imm8 → "rhs" prior to this op.
// ============================================================================

internal sealed class X86AluGroupModrmImmW8Emitter : IMicroOpEmitter
{
    public string OpName => "x86_alu_group_modrm_imm_w8";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i32 = LLVMTypeRef.Int32;
        var lhs = ctx.Resolve("lhs");
        var rhs = ctx.Resolve("rhs");
        var sel = ctx.Resolve("modrm_reg");

        var endBB     = ctx.Function.AppendBasicBlock("alug8_end");
        var defaultBB = ctx.Function.AppendBasicBlock("alug8_default");
        var arms = new LLVMBasicBlockRef[8];
        for (int i = 0; i < 8; i++) arms[i] = ctx.Function.AppendBasicBlock($"alug8_{i}");
        var sw = ctx.Builder.BuildSwitch(sel, defaultBB, 8);
        for (int i = 0; i < 8; i++)
            sw.AddCase(LLVMValueRef.CreateConstInt(i32, (uint)i, false), arms[i]);

        var kinds = new[] { "add", "or", "adc", "sbb", "and", "sub", "xor", "cmp" };
        for (int i = 0; i < 8; i++)
        {
            ctx.Builder.PositionAtEnd(arms[i]);
            var r = X86AluHelpers.BuildAluW8(ctx, kinds[i], lhs, rhs, $"alug8_{kinds[i]}");
            if (kinds[i] != "cmp")
            {
                X86ModRmMemHelpers.BuildStoreW8(ctx, r);
            }
            ctx.Builder.BuildBr(endBB);
        }
        ctx.Builder.PositionAtEnd(defaultBB);
        ctx.Builder.BuildBr(endBB);
        ctx.Builder.PositionAtEnd(endBB);
    }
}

internal sealed class X86AluGroupModrmImmW16Emitter : IMicroOpEmitter
{
    public string OpName => "x86_alu_group_modrm_imm_w16";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i32 = LLVMTypeRef.Int32;
        var lhs = ctx.Resolve("lhs");
        var rhs = ctx.Resolve("rhs");
        var sel = ctx.Resolve("modrm_reg");

        var endBB     = ctx.Function.AppendBasicBlock("alug16_end");
        var defaultBB = ctx.Function.AppendBasicBlock("alug16_default");
        var arms = new LLVMBasicBlockRef[8];
        for (int i = 0; i < 8; i++) arms[i] = ctx.Function.AppendBasicBlock($"alug16_{i}");
        var sw = ctx.Builder.BuildSwitch(sel, defaultBB, 8);
        for (int i = 0; i < 8; i++)
            sw.AddCase(LLVMValueRef.CreateConstInt(i32, (uint)i, false), arms[i]);

        var kinds = new[] { "add", "or", "adc", "sbb", "and", "sub", "xor", "cmp" };
        for (int i = 0; i < 8; i++)
        {
            ctx.Builder.PositionAtEnd(arms[i]);
            var r = X86AluHelpers.BuildAluW16(ctx, kinds[i], lhs, rhs, $"alug16_{kinds[i]}");
            if (kinds[i] != "cmp")
            {
                X86ModRmMemHelpers.BuildStoreW16(ctx, r);
            }
            ctx.Builder.BuildBr(endBB);
        }
        ctx.Builder.PositionAtEnd(defaultBB);
        ctx.Builder.BuildBr(endBB);
        ctx.Builder.PositionAtEnd(endBB);
    }
}

internal sealed class X86FetchImm8SextW16Emitter : IMicroOpEmitter
{
    public string OpName => "x86_fetch_imm8_sext_w16";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var outName = step.Raw.GetProperty("out").GetString()!;
        var i16 = LLVMTypeRef.Int16;
        var b = X86_16Emitters.FetchImm8(ctx, outName);
        ctx.Values[outName] = ctx.Builder.BuildSExt(b, i16, $"{outName}_sext");
    }
}

// ============================================================================
// 24.6.6d — TEST / NOT / NEG (8/16-bit).
//
// TEST is "AND for flags only" — set flags per AND rules (CF=0, OF=0,
// AF=0, PF/ZF/SF from result), no writeback. Direct opcodes 0x84/0x85
// (r/m,r) and 0xA8/0xA9 (AL/AX,imm) plus the F6 /0 / F7 /0 group entries.
//
// NOT is bitwise inversion. Per Intel: does NOT touch any flag. Spec
// composes fetch_modrm + compute_ea + load + not + store.
//
// NEG is arithmetic negation (0 - operand). Flag rules:
//   CF = 1 if operand was non-zero, else 0
//   OF = 1 if operand was 0x80 (or 0x8000) — signed overflow at MIN
//   AF/PF/ZF/SF — per result via standard rules
// Same writeback path as ADD/SUB.
//
// JSON shape:
//   { "op": "x86_test_w8", "lhs": "<n>", "rhs": "<n>" }   — no out, no writeback
//   { "op": "x86_not_w8",  "in": ["<n>"], "out": "<n>" }
//   { "op": "x86_neg_w8",  "in": ["<n>"], "out": "<n>" }
//   (and w16 counterparts)
// ============================================================================

internal sealed class X86TestW8Emitter : IMicroOpEmitter
{
    public string OpName => "x86_test_w8";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var lhs = ctx.Resolve(step.Raw.GetProperty("lhs").GetString()!);
        var rhs = ctx.Resolve(step.Raw.GetProperty("rhs").GetString()!);
        // AND rules apply for flags; result discarded.
        X86AluHelpers.BuildAluW8(ctx, "and", lhs, rhs, "test8");
    }
}

internal sealed class X86TestW16Emitter : IMicroOpEmitter
{
    public string OpName => "x86_test_w16";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var lhs = ctx.Resolve(step.Raw.GetProperty("lhs").GetString()!);
        var rhs = ctx.Resolve(step.Raw.GetProperty("rhs").GetString()!);
        X86AluHelpers.BuildAluW16(ctx, "and", lhs, rhs, "test16");
    }
}

internal sealed class X86NotW8Emitter : IMicroOpEmitter
{
    public string OpName => "x86_not_w8";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var v = ctx.Resolve(step.Raw.GetProperty("in")[0].GetString()!);
        var i8 = LLVMTypeRef.Int8;
        var nv = ctx.Builder.BuildXor(v,
            LLVMValueRef.CreateConstInt(i8, 0xFF, false), "not8_v");
        if (step.Raw.TryGetProperty("out", out var op))
            ctx.Values[op.GetString()!] = nv;
    }
}

internal sealed class X86NotW16Emitter : IMicroOpEmitter
{
    public string OpName => "x86_not_w16";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var v = ctx.Resolve(step.Raw.GetProperty("in")[0].GetString()!);
        var i16 = LLVMTypeRef.Int16;
        var nv = ctx.Builder.BuildXor(v,
            LLVMValueRef.CreateConstInt(i16, 0xFFFF, false), "not16_v");
        if (step.Raw.TryGetProperty("out", out var op))
            ctx.Values[op.GetString()!] = nv;
    }
}

internal sealed class X86NegW8Emitter : IMicroOpEmitter
{
    public string OpName => "x86_neg_w8";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var v = ctx.Resolve(step.Raw.GetProperty("in")[0].GetString()!);
        var i8 = LLVMTypeRef.Int8;
        // NEG = SUB 0, v — uses the standard sub flag rules naturally:
        //   CF = (raw & 0x100) != 0 = (0 - v < 0) = (v != 0) ✓
        //   OF = (((0^v) & (0^r)) & 0x80) != 0 = ((v & r) & 0x80) != 0
        //        which equals 1 iff v=0x80 (only case where 0-v wraps to itself)
        var zero = LLVMValueRef.CreateConstInt(i8, 0, false);
        var nv = X86AluHelpers.BuildAluW8(ctx, "sub", zero, v, "neg8");
        if (step.Raw.TryGetProperty("out", out var op))
            ctx.Values[op.GetString()!] = nv;
    }
}

internal sealed class X86NegW16Emitter : IMicroOpEmitter
{
    public string OpName => "x86_neg_w16";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var v = ctx.Resolve(step.Raw.GetProperty("in")[0].GetString()!);
        var i16 = LLVMTypeRef.Int16;
        var zero = LLVMValueRef.CreateConstInt(i16, 0, false);
        var nv = X86AluHelpers.BuildAluW16(ctx, "sub", zero, v, "neg16");
        if (step.Raw.TryGetProperty("out", out var op))
            ctx.Values[op.GetString()!] = nv;
    }
}

// ============================================================================
// 24.6.6d — F6 / F7 group dispatchers. modrm.reg sub-selects the op:
//   /0 = TEST r/m, imm
//   /1 = (alias for TEST on 8086, same as /0 — silicon decodes both)
//   /2 = NOT r/m
//   /3 = NEG r/m
//   /4 = MUL r/m  (AX = AL * r/m8 ; DX:AX = AX * r/m16)   — 24.6.6e
//   /5 = IMUL r/m (signed multiply)                        — 24.6.6e
//   /6 = DIV r/m  (unsigned divide)                        — 24.6.6e
//   /7 = IDIV r/m (signed divide)                          — 24.6.6e
//
// /4-/7 land in 24.6.6e; this dispatcher silently no-ops them for now.
//
// Spec convention: the lhs (operand) is preloaded by modrm_load_w{8,16}
// → "lhs". For /0 TEST the imm is also preloaded by fetch_imm{8,16}
// → "rhs". /2 NOT and /3 NEG don't need rhs.
// ============================================================================

internal sealed class X86F6GroupDispatchEmitter : IMicroOpEmitter
{
    public string OpName => "x86_f6_group_dispatch";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i8  = LLVMTypeRef.Int8;
        var i32 = LLVMTypeRef.Int32;
        var lhs = ctx.Resolve("lhs");
        var sel = ctx.Resolve("modrm_reg");

        var endBB     = ctx.Function.AppendBasicBlock("f6g_end");
        var defaultBB = ctx.Function.AppendBasicBlock("f6g_default");
        var arms = new LLVMBasicBlockRef[8];
        for (int i = 0; i < 8; i++) arms[i] = ctx.Function.AppendBasicBlock($"f6g_{i}");
        var sw = ctx.Builder.BuildSwitch(sel, defaultBB, 8);
        for (int i = 0; i < 8; i++)
            sw.AddCase(LLVMValueRef.CreateConstInt(i32, (uint)i, false), arms[i]);

        // /0 TEST r/m8, imm8 — fetch imm INSIDE this arm so non-TEST sub-ops
        // don't accidentally advance IP. (Pre-fetching in spec was wrong:
        // NOT/NEG/MUL/etc. don't have an imm operand.)
        ctx.Builder.PositionAtEnd(arms[0]);
        var rhs0 = X86_16Emitters.FetchImm8(ctx, "f6g_imm0");
        X86AluHelpers.BuildAluW8(ctx, "and", lhs, rhs0, "f6g_test");
        ctx.Builder.BuildBr(endBB);

        // /1 alias of /0 on 8086 — also has imm operand.
        ctx.Builder.PositionAtEnd(arms[1]);
        var rhs1 = X86_16Emitters.FetchImm8(ctx, "f6g_imm1");
        X86AluHelpers.BuildAluW8(ctx, "and", lhs, rhs1, "f6g_test1");
        ctx.Builder.BuildBr(endBB);

        // /2 NOT r/m8 — bitwise inversion + writeback. No flag update.
        ctx.Builder.PositionAtEnd(arms[2]);
        var notV = ctx.Builder.BuildXor(lhs,
            LLVMValueRef.CreateConstInt(i8, 0xFF, false), "f6g_not");
        X86ModRmMemHelpers.BuildStoreW8(ctx, notV);
        ctx.Builder.BuildBr(endBB);

        // /3 NEG r/m8 — 0 - lhs via SUB rules + writeback.
        ctx.Builder.PositionAtEnd(arms[3]);
        var zero = LLVMValueRef.CreateConstInt(i8, 0, false);
        var negV = X86AluHelpers.BuildAluW8(ctx, "sub", zero, lhs, "f6g_neg");
        X86ModRmMemHelpers.BuildStoreW8(ctx, negV);
        ctx.Builder.BuildBr(endBB);

        // /4 MUL r/m8: AX = AL * lhs (unsigned). 8088 flag rules:
        //   CF = OF = (AH != 0)
        //   SF/ZF/PF reflect AH (the high byte of the result), NOT the
        //   full AX — the silicon "high byte quirk" caught in 24.4.4.
        ctx.Builder.PositionAtEnd(arms[4]);
        EmitMulW8(ctx, lhs, signed: false);
        ctx.Builder.BuildBr(endBB);

        // /5 IMUL r/m8 (signed multiply). Same flag shape, signed widening.
        ctx.Builder.PositionAtEnd(arms[5]);
        EmitMulW8(ctx, lhs, signed: true);
        ctx.Builder.BuildBr(endBB);

        // /6 DIV / /7 IDIV deferred — divide-by-zero exception + silicon-
        // quirky flag behaviour need careful Tom Harte alignment in 24.6.7.
        for (int i = 6; i <= 7; i++)
        {
            ctx.Builder.PositionAtEnd(arms[i]);
            ctx.Builder.BuildBr(endBB);
        }

        ctx.Builder.PositionAtEnd(defaultBB);
        ctx.Builder.BuildBr(endBB);
        ctx.Builder.PositionAtEnd(endBB);
    }

    /// <summary>
    /// Emit MUL or IMUL r/m8 IR at the current builder position.
    ///   product (i16) = AL (s/zext to i16) * lhs (s/zext to i16)
    ///   AX := product
    ///   AH (high byte) → drives CF/OF + SF/ZF/PF (8088 high-byte quirk)
    /// </summary>
    private static void EmitMulW8(EmitContext ctx, LLVMValueRef lhs8, bool signed)
    {
        var i1  = LLVMTypeRef.Int1;
        var i8  = LLVMTypeRef.Int8;
        var i16 = LLVMTypeRef.Int16;
        var label = signed ? "imul8" : "mul8";

        var al = X86_16Emitters.ReadGpr8(ctx, 0, $"{label}_al");
        LLVMValueRef alW = signed
            ? ctx.Builder.BuildSExt(al, i16, $"{label}_al_w")
            : ctx.Builder.BuildZExt(al, i16, $"{label}_al_w");
        LLVMValueRef rhsW = signed
            ? ctx.Builder.BuildSExt(lhs8, i16, $"{label}_b_w")
            : ctx.Builder.BuildZExt(lhs8, i16, $"{label}_b_w");
        var prod = ctx.Builder.BuildMul(alW, rhsW, $"{label}_prod");

        // Store full product to AX.
        X86_16Emitters.WriteGpr16(ctx, 0, prod);

        // High byte = (prod >> 8) & 0xFF
        var ahShift = ctx.Builder.BuildLShr(prod,
            LLVMValueRef.CreateConstInt(i16, 8, false), $"{label}_ah_sh");
        var ah = ctx.Builder.BuildTrunc(ahShift, i8, $"{label}_ah");

        // CF = OF = (ah != 0)
        var ahNonZero = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, ah,
            LLVMValueRef.CreateConstInt(i8, 0, false), $"{label}_ah_nz");

        // SF/ZF/PF from AH (8088 high-byte quirk).
        var sfMask = ctx.Builder.BuildAnd(ah,
            LLVMValueRef.CreateConstInt(i8, 0x80, false), $"{label}_sf_mask");
        var sf = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, sfMask,
            LLVMValueRef.CreateConstInt(i8, 0, false), $"{label}_sf");
        var zf = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, ah,
            LLVMValueRef.CreateConstInt(i8, 0, false), $"{label}_zf");
        // Parity from AH (low byte of FLAGS-relevant value, but the spec says
        // the high byte of the product per Tom Harte SST analysis).
        var pf = X86AluHelpers_BuildParityEvenPublic(ctx, ah, label);

        // AF undefined — set to 0 for determinism.
        var i1false = LLVMValueRef.CreateConstInt(i1, 0, false);
        var packed = X86AluHelpers_BuildPackedFlagsPublic(ctx, ahNonZero, pf, i1false, zf, sf, ahNonZero, label);
        X86AluHelpers_StoreAluFlagsPublic(ctx, packed, label);
    }

    // Bridge to the private helpers in X86AluHelpers — internal-friendly
    // wrappers so EmitMulW8/16 can reach them.
    internal static LLVMValueRef X86AluHelpers_BuildParityEvenPublic(EmitContext ctx, LLVMValueRef byteVal, string label)
    {
        var i8  = LLVMTypeRef.Int8;
        var i32 = LLVMTypeRef.Int32;
        var ctpopName = "llvm.ctpop.i8";
        var fn = ctx.Module.GetNamedFunction(ctpopName);
        if (fn.Handle == IntPtr.Zero)
        {
            var fnType = LLVMTypeRef.CreateFunction(i8, new[] { i8 }, false);
            fn = ctx.Module.AddFunction(ctpopName, fnType);
        }
        var fnType2 = LLVMTypeRef.CreateFunction(i8, new[] { i8 }, false);
        var pop = ctx.Builder.BuildCall2(fnType2, fn, new[] { byteVal }, $"{label}_pop");
        var lsb = ctx.Builder.BuildAnd(pop,
            LLVMValueRef.CreateConstInt(i8, 1, false), $"{label}_pop_lsb");
        return ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, lsb,
            LLVMValueRef.CreateConstInt(i8, 0, false), $"{label}_pf");
    }

    internal static LLVMValueRef X86AluHelpers_BuildPackedFlagsPublic(
        EmitContext ctx,
        LLVMValueRef cf, LLVMValueRef pf, LLVMValueRef af,
        LLVMValueRef zf, LLVMValueRef sf, LLVMValueRef of,
        string label)
    {
        var i16 = LLVMTypeRef.Int16;
        LLVMValueRef ZxShl(LLVMValueRef bit, int pos, string n)
        {
            var z = ctx.Builder.BuildZExt(bit, i16, $"{label}_{n}_z");
            if (pos == 0) return z;
            return ctx.Builder.BuildShl(z,
                LLVMValueRef.CreateConstInt(i16, (ulong)pos, false), $"{label}_{n}_sh");
        }
        var bcf = ZxShl(cf, 0, "cf");
        var bpf = ZxShl(pf, 2, "pf");
        var baf = ZxShl(af, 4, "af");
        var bzf = ZxShl(zf, 6, "zf");
        var bsf = ZxShl(sf, 7, "sf");
        var bof = ZxShl(of, 11, "of");
        var t1 = ctx.Builder.BuildOr(bcf, bpf, $"{label}_t1");
        var t2 = ctx.Builder.BuildOr(t1, baf, $"{label}_t2");
        var t3 = ctx.Builder.BuildOr(t2, bzf, $"{label}_t3");
        var t4 = ctx.Builder.BuildOr(t3, bsf, $"{label}_t4");
        return ctx.Builder.BuildOr(t4, bof, $"{label}_packed");
    }

    internal static void X86AluHelpers_StoreAluFlagsPublic(EmitContext ctx, LLVMValueRef packed, string label)
    {
        var i16 = LLVMTypeRef.Int16;
        var fPtr = ctx.GepStatusRegister("FLAGS");
        var current = ctx.Builder.BuildLoad2(i16, fPtr, $"{label}_flags_cur");
        var notMask = LLVMValueRef.CreateConstInt(i16, (ulong)(unchecked((ushort)~0x08D5)), false);
        var keep = ctx.Builder.BuildAnd(current, notMask, $"{label}_flags_keep");
        var maskedNew = ctx.Builder.BuildAnd(packed,
            LLVMValueRef.CreateConstInt(i16, 0x08D5, false), $"{label}_flags_new_masked");
        var merged = ctx.Builder.BuildOr(keep, maskedNew, $"{label}_flags_merged");
        ctx.Builder.BuildStore(merged, fPtr);
    }
}

internal sealed class X86F7GroupDispatchEmitter : IMicroOpEmitter
{
    public string OpName => "x86_f7_group_dispatch";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i16 = LLVMTypeRef.Int16;
        var i32 = LLVMTypeRef.Int32;
        var lhs = ctx.Resolve("lhs");
        var sel = ctx.Resolve("modrm_reg");

        var endBB     = ctx.Function.AppendBasicBlock("f7g_end");
        var defaultBB = ctx.Function.AppendBasicBlock("f7g_default");
        var arms = new LLVMBasicBlockRef[8];
        for (int i = 0; i < 8; i++) arms[i] = ctx.Function.AppendBasicBlock($"f7g_{i}");
        var sw = ctx.Builder.BuildSwitch(sel, defaultBB, 8);
        for (int i = 0; i < 8; i++)
            sw.AddCase(LLVMValueRef.CreateConstInt(i32, (uint)i, false), arms[i]);

        ctx.Builder.PositionAtEnd(arms[0]);
        var rhs0 = X86_16Emitters.FetchImm16(ctx, "f7g_imm0");
        X86AluHelpers.BuildAluW16(ctx, "and", lhs, rhs0, "f7g_test");
        ctx.Builder.BuildBr(endBB);

        ctx.Builder.PositionAtEnd(arms[1]);
        var rhs1 = X86_16Emitters.FetchImm16(ctx, "f7g_imm1");
        X86AluHelpers.BuildAluW16(ctx, "and", lhs, rhs1, "f7g_test1");
        ctx.Builder.BuildBr(endBB);

        ctx.Builder.PositionAtEnd(arms[2]);
        var notV = ctx.Builder.BuildXor(lhs,
            LLVMValueRef.CreateConstInt(i16, 0xFFFF, false), "f7g_not");
        X86ModRmMemHelpers.BuildStoreW16(ctx, notV);
        ctx.Builder.BuildBr(endBB);

        ctx.Builder.PositionAtEnd(arms[3]);
        var zero = LLVMValueRef.CreateConstInt(i16, 0, false);
        var negV = X86AluHelpers.BuildAluW16(ctx, "sub", zero, lhs, "f7g_neg");
        X86ModRmMemHelpers.BuildStoreW16(ctx, negV);
        ctx.Builder.BuildBr(endBB);

        // /4 MUL r/m16: DX:AX = AX * lhs (unsigned). Flag rules per 8088:
        //   CF = OF = (DX != 0)
        //   SF/ZF/PF reflect DX (high half) per the Tom Harte high-byte
        //   quirk (24.4.4 reverse-engineered the exact rule).
        ctx.Builder.PositionAtEnd(arms[4]);
        EmitMulW16(ctx, lhs, signed: false);
        ctx.Builder.BuildBr(endBB);

        ctx.Builder.PositionAtEnd(arms[5]);
        EmitMulW16(ctx, lhs, signed: true);
        ctx.Builder.BuildBr(endBB);

        // /6 DIV / /7 IDIV deferred — divide-by-zero exception + silicon
        // quirks need careful Tom Harte alignment.
        for (int i = 6; i <= 7; i++)
        {
            ctx.Builder.PositionAtEnd(arms[i]);
            ctx.Builder.BuildBr(endBB);
        }

        ctx.Builder.PositionAtEnd(defaultBB);
        ctx.Builder.BuildBr(endBB);
        ctx.Builder.PositionAtEnd(endBB);
    }

    /// <summary>
    /// Emit MUL/IMUL r/m16 IR.
    ///   product i32 = AX (s/zext to i32) * lhs (s/zext to i32)
    ///   AX = product[15:0],  DX = product[31:16]
    ///   CF = OF = (DX != 0)
    ///   SF/ZF/PF reflect DX (high half) — 8088 high-half quirk.
    /// </summary>
    private static void EmitMulW16(EmitContext ctx, LLVMValueRef lhs16, bool signed)
    {
        var i1  = LLVMTypeRef.Int1;
        var i8  = LLVMTypeRef.Int8;
        var i16 = LLVMTypeRef.Int16;
        var i32 = LLVMTypeRef.Int32;
        var label = signed ? "imul16" : "mul16";

        var ax = X86_16Emitters.ReadGpr16(ctx, 0, $"{label}_ax");
        LLVMValueRef axW = signed
            ? ctx.Builder.BuildSExt(ax, i32, $"{label}_ax_w")
            : ctx.Builder.BuildZExt(ax, i32, $"{label}_ax_w");
        LLVMValueRef rhsW = signed
            ? ctx.Builder.BuildSExt(lhs16, i32, $"{label}_b_w")
            : ctx.Builder.BuildZExt(lhs16, i32, $"{label}_b_w");
        var prod = ctx.Builder.BuildMul(axW, rhsW, $"{label}_prod");

        var lo = ctx.Builder.BuildTrunc(prod, i16, $"{label}_lo");
        X86_16Emitters.WriteGpr16(ctx, 0, lo);   // AX
        var hiShift = ctx.Builder.BuildLShr(prod,
            LLVMValueRef.CreateConstInt(i32, 16, false), $"{label}_hi_sh");
        var hi = ctx.Builder.BuildTrunc(hiShift, i16, $"{label}_hi");
        X86_16Emitters.WriteGpr16(ctx, 2, hi);   // DX (GPR index 2)

        // CF = OF = (DX != 0)
        var dxNonZero = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, hi,
            LLVMValueRef.CreateConstInt(i16, 0, false), $"{label}_dx_nz");

        // SF from DX bit 15
        var sfMask = ctx.Builder.BuildAnd(hi,
            LLVMValueRef.CreateConstInt(i16, 0x8000, false), $"{label}_sf_mask");
        var sf = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, sfMask,
            LLVMValueRef.CreateConstInt(i16, 0, false), $"{label}_sf");
        // ZF from DX
        var zf = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, hi,
            LLVMValueRef.CreateConstInt(i16, 0, false), $"{label}_zf");
        // PF from low byte of DX
        var dxLow = ctx.Builder.BuildTrunc(hi, i8, $"{label}_dx_lo");
        var pf = X86F6GroupDispatchEmitter.X86AluHelpers_BuildParityEvenPublic(ctx, dxLow, label);

        var i1false = LLVMValueRef.CreateConstInt(i1, 0, false);
        var packed = X86F6GroupDispatchEmitter.X86AluHelpers_BuildPackedFlagsPublic(
            ctx, dxNonZero, pf, i1false, zf, sf, dxNonZero, label);
        X86F6GroupDispatchEmitter.X86AluHelpers_StoreAluFlagsPublic(ctx, packed, label);
    }
}

// ============================================================================
// 24.6.6e — CBW (0x98) sign-extends AL to AX, CWD (0x99) sign-extends AX
// to DX:AX. Both don't touch flags.
// ============================================================================

internal sealed class X86CbwEmitter : IMicroOpEmitter
{
    public string OpName => "x86_cbw";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i8  = LLVMTypeRef.Int8;
        var i16 = LLVMTypeRef.Int16;
        var al  = X86_16Emitters.ReadGpr8(ctx, 0, "cbw_al");
        var ax  = ctx.Builder.BuildSExt(al, i16, "cbw_ax");
        X86_16Emitters.WriteGpr16(ctx, 0, ax);
    }
}

internal sealed class X86CwdEmitter : IMicroOpEmitter
{
    public string OpName => "x86_cwd";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i16 = LLVMTypeRef.Int16;
        var i32 = LLVMTypeRef.Int32;
        var ax  = X86_16Emitters.ReadGpr16(ctx, 0, "cwd_ax");
        // DX = AX bit 15 ? 0xFFFF : 0x0000 = arithmetic shift right by 15.
        var sext32 = ctx.Builder.BuildSExt(ax, i32, "cwd_sx");
        var dxShift = ctx.Builder.BuildLShr(sext32,
            LLVMValueRef.CreateConstInt(i32, 16, false), "cwd_dxsh");
        var dx = ctx.Builder.BuildTrunc(dxShift, i16, "cwd_dx");
        X86_16Emitters.WriteGpr16(ctx, 2, dx);
    }
}

// ============================================================================
// 24.6.7a — control flow basics.
//
// All branch targets are computed relative to "post-fetch IP" — i.e. the
// IP value AFTER the displacement bytes have been consumed. So
// "fetch_imm{8,16}" advances IP correctly first, then we add the (sign-
// extended) displacement. IP wraps at 16 bits; CS untouched.
//
// Jcc condition encoding (8086):
//   cccc | mnemonic | predicate
//   0000 | JO       | OF=1
//   0001 | JNO      | OF=0
//   0010 | JB/JC    | CF=1
//   0011 | JNB/JNC  | CF=0
//   0100 | JZ/JE    | ZF=1
//   0101 | JNZ/JNE  | ZF=0
//   0110 | JBE/JNA  | CF|ZF
//   0111 | JNBE/JA  | !CF & !ZF
//   1000 | JS       | SF=1
//   1001 | JNS      | SF=0
//   1010 | JP/JPE   | PF=1
//   1011 | JNP/JPO  | PF=0
//   1100 | JL/JNGE  | SF^OF
//   1101 | JNL/JGE  | !(SF^OF)
//   1110 | JLE/JNG  | ZF | (SF^OF)
//   1111 | JNLE/JG  | !ZF & !(SF^OF)
// ============================================================================

internal static class X86CtrlHelpers
{
    /// <summary>Read FLAGS as i16.</summary>
    public static LLVMValueRef LoadFlags(EmitContext ctx, string label)
    {
        var i16 = LLVMTypeRef.Int16;
        var fPtr = ctx.GepStatusRegister("FLAGS");
        return ctx.Builder.BuildLoad2(i16, fPtr, $"{label}_flags");
    }

    /// <summary>Extract a single FLAGS bit as i1 by position.</summary>
    public static LLVMValueRef ExtractFlagBit(EmitContext ctx, LLVMValueRef flags16, int pos, string name)
    {
        var i16 = LLVMTypeRef.Int16;
        var i1  = LLVMTypeRef.Int1;
        var shifted = pos == 0 ? flags16 : ctx.Builder.BuildLShr(flags16,
            LLVMValueRef.CreateConstInt(i16, (ulong)pos, false), $"{name}_sh");
        var masked = ctx.Builder.BuildAnd(shifted,
            LLVMValueRef.CreateConstInt(i16, 1, false), $"{name}_mask");
        return ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, masked,
            LLVMValueRef.CreateConstInt(i16, 0, false), $"{name}_i1");
    }

    /// <summary>
    /// Build the i1 predicate for a 4-bit Jcc condition value (cccc field).
    /// Uses a runtime switch on cccc → arm-specific i1 expression.
    /// </summary>
    public static LLVMValueRef BuildJccPredicate(
        EmitContext ctx, LLVMValueRef cccc32, LLVMValueRef flags16, string label)
    {
        var i1  = LLVMTypeRef.Int1;
        var i32 = LLVMTypeRef.Int32;

        var cf = ExtractFlagBit(ctx, flags16, 0,  $"{label}_cf");
        var pf = ExtractFlagBit(ctx, flags16, 2,  $"{label}_pf");
        var zf = ExtractFlagBit(ctx, flags16, 6,  $"{label}_zf");
        var sf = ExtractFlagBit(ctx, flags16, 7,  $"{label}_sf");
        var of = ExtractFlagBit(ctx, flags16, 11, $"{label}_of");

        var notCf = ctx.Builder.BuildNot(cf, $"{label}_ncf");
        var notZf = ctx.Builder.BuildNot(zf, $"{label}_nzf");
        var notSf = ctx.Builder.BuildNot(sf, $"{label}_nsf");
        var notOf = ctx.Builder.BuildNot(of, $"{label}_nof");
        var notPf = ctx.Builder.BuildNot(pf, $"{label}_npf");
        var sfXorOf  = ctx.Builder.BuildXor(sf, of, $"{label}_sxo");
        var nsfXorOf = ctx.Builder.BuildNot(sfXorOf, $"{label}_nsxo");

        var endBB     = ctx.Function.AppendBasicBlock($"{label}_end");
        var defaultBB = ctx.Function.AppendBasicBlock($"{label}_default");
        var arms = new LLVMBasicBlockRef[16];
        var armVals = new LLVMValueRef[16];
        for (int i = 0; i < 16; i++) arms[i] = ctx.Function.AppendBasicBlock($"{label}_{i:X}");

        var sw = ctx.Builder.BuildSwitch(cccc32, defaultBB, 16);
        for (int i = 0; i < 16; i++)
            sw.AddCase(LLVMValueRef.CreateConstInt(i32, (uint)i, false), arms[i]);

        // Per-arm predicate IR. We POSITION at the arm and produce a value;
        // each arm just fall-throughs to endBB after producing its value.
        ctx.Builder.PositionAtEnd(arms[0x0]); armVals[0x0] = of;                                  ctx.Builder.BuildBr(endBB);
        ctx.Builder.PositionAtEnd(arms[0x1]); armVals[0x1] = notOf;                               ctx.Builder.BuildBr(endBB);
        ctx.Builder.PositionAtEnd(arms[0x2]); armVals[0x2] = cf;                                  ctx.Builder.BuildBr(endBB);
        ctx.Builder.PositionAtEnd(arms[0x3]); armVals[0x3] = notCf;                               ctx.Builder.BuildBr(endBB);
        ctx.Builder.PositionAtEnd(arms[0x4]); armVals[0x4] = zf;                                  ctx.Builder.BuildBr(endBB);
        ctx.Builder.PositionAtEnd(arms[0x5]); armVals[0x5] = notZf;                               ctx.Builder.BuildBr(endBB);
        ctx.Builder.PositionAtEnd(arms[0x6]); armVals[0x6] = ctx.Builder.BuildOr(cf, zf, $"{label}_cfOzf"); ctx.Builder.BuildBr(endBB);
        ctx.Builder.PositionAtEnd(arms[0x7]); armVals[0x7] = ctx.Builder.BuildAnd(notCf, notZf, $"{label}_ncfAnzf"); ctx.Builder.BuildBr(endBB);
        ctx.Builder.PositionAtEnd(arms[0x8]); armVals[0x8] = sf;                                  ctx.Builder.BuildBr(endBB);
        ctx.Builder.PositionAtEnd(arms[0x9]); armVals[0x9] = notSf;                               ctx.Builder.BuildBr(endBB);
        ctx.Builder.PositionAtEnd(arms[0xA]); armVals[0xA] = pf;                                  ctx.Builder.BuildBr(endBB);
        ctx.Builder.PositionAtEnd(arms[0xB]); armVals[0xB] = notPf;                               ctx.Builder.BuildBr(endBB);
        ctx.Builder.PositionAtEnd(arms[0xC]); armVals[0xC] = sfXorOf;                             ctx.Builder.BuildBr(endBB);
        ctx.Builder.PositionAtEnd(arms[0xD]); armVals[0xD] = nsfXorOf;                            ctx.Builder.BuildBr(endBB);
        ctx.Builder.PositionAtEnd(arms[0xE]); armVals[0xE] = ctx.Builder.BuildOr(zf, sfXorOf, $"{label}_zfOsxo"); ctx.Builder.BuildBr(endBB);
        ctx.Builder.PositionAtEnd(arms[0xF]); armVals[0xF] = ctx.Builder.BuildAnd(notZf, nsfXorOf, $"{label}_nzfAnsxo"); ctx.Builder.BuildBr(endBB);

        ctx.Builder.PositionAtEnd(defaultBB);
        var defVal = LLVMValueRef.CreateConstInt(i1, 0, false);
        ctx.Builder.BuildBr(endBB);

        ctx.Builder.PositionAtEnd(endBB);
        var phi = ctx.Builder.BuildPhi(i1, $"{label}_pred");
        var pIns = new LLVMValueRef[17];
        var pBlk = new LLVMBasicBlockRef[17];
        for (int i = 0; i < 16; i++) { pIns[i] = armVals[i]; pBlk[i] = arms[i]; }
        pIns[16] = defVal; pBlk[16] = defaultBB;
        phi.AddIncoming(pIns, pBlk, 17);
        return phi;
    }
}

internal sealed class X86JmpRel8Emitter : IMicroOpEmitter
{
    public string OpName => "x86_jmp_rel8";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i16 = LLVMTypeRef.Int16;
        // fetch_imm8 advances IP first; signed displacement adds to post-fetch IP.
        var disp8 = X86_16Emitters.FetchImm8(ctx, "jmp8_d");
        var disp16 = ctx.Builder.BuildSExt(disp8, i16, "jmp8_dsx");
        var ipPtr = ctx.GepStatusRegister("IP");
        var ip = ctx.Builder.BuildLoad2(i16, ipPtr, "jmp8_ip");
        var newIp = ctx.Builder.BuildAdd(ip, disp16, "jmp8_newip");
        ctx.Builder.BuildStore(newIp, ipPtr);
    }
}

internal sealed class X86JmpRel16Emitter : IMicroOpEmitter
{
    public string OpName => "x86_jmp_rel16";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i16 = LLVMTypeRef.Int16;
        var disp16 = X86_16Emitters.FetchImm16(ctx, "jmp16_d");
        var ipPtr = ctx.GepStatusRegister("IP");
        var ip = ctx.Builder.BuildLoad2(i16, ipPtr, "jmp16_ip");
        var newIp = ctx.Builder.BuildAdd(ip, disp16, "jmp16_newip");
        ctx.Builder.BuildStore(newIp, ipPtr);
    }
}

internal sealed class X86JccRel8Emitter : IMicroOpEmitter
{
    public string OpName => "x86_jcc_rel8";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var fieldName = step.Raw.GetProperty("cond_field").GetString()!;
        var i16 = LLVMTypeRef.Int16;
        var i32 = LLVMTypeRef.Int32;

        var cccc = ctx.Resolve(fieldName);    // i32

        // Always fetch displacement first — IP must advance regardless.
        var disp8  = X86_16Emitters.FetchImm8(ctx, "jcc_d");
        var disp16 = ctx.Builder.BuildSExt(disp8, i16, "jcc_dsx");

        // Build the predicate from FLAGS + cccc.
        var flags = X86CtrlHelpers.LoadFlags(ctx, "jcc");
        var pred = X86CtrlHelpers.BuildJccPredicate(ctx, cccc, flags, "jcc");

        // If predicate true: IP += disp; else: no change.
        var ipPtr = ctx.GepStatusRegister("IP");
        var ip = ctx.Builder.BuildLoad2(i16, ipPtr, "jcc_ip");
        var ipPlus = ctx.Builder.BuildAdd(ip, disp16, "jcc_ipP");
        var sel = ctx.Builder.BuildSelect(pred, ipPlus, ip, "jcc_newip");
        ctx.Builder.BuildStore(sel, ipPtr);
    }
}

internal sealed class X86JcxzRel8Emitter : IMicroOpEmitter
{
    public string OpName => "x86_jcxz_rel8";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i16 = LLVMTypeRef.Int16;
        var disp8  = X86_16Emitters.FetchImm8(ctx, "jcxz_d");
        var disp16 = ctx.Builder.BuildSExt(disp8, i16, "jcxz_dsx");
        var cx = X86_16Emitters.ReadGpr16(ctx, 1, "jcxz_cx");
        var cxZero = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, cx,
            LLVMValueRef.CreateConstInt(i16, 0, false), "jcxz_isz");
        var ipPtr = ctx.GepStatusRegister("IP");
        var ip = ctx.Builder.BuildLoad2(i16, ipPtr, "jcxz_ip");
        var ipPlus = ctx.Builder.BuildAdd(ip, disp16, "jcxz_ipP");
        var sel = ctx.Builder.BuildSelect(cxZero, ipPlus, ip, "jcxz_newip");
        ctx.Builder.BuildStore(sel, ipPtr);
    }
}

internal sealed class X86LoopEmitter : IMicroOpEmitter
{
    public string OpName => "x86_loop";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        // kind: "loop" (no zf check), "loope" (require ZF=1), "loopne" (require ZF=0)
        var kind = step.Raw.GetProperty("kind").GetString()!;
        var i1  = LLVMTypeRef.Int1;
        var i16 = LLVMTypeRef.Int16;

        var disp8  = X86_16Emitters.FetchImm8(ctx, "loop_d");
        var disp16 = ctx.Builder.BuildSExt(disp8, i16, "loop_dsx");

        // CX -= 1
        var cx = X86_16Emitters.ReadGpr16(ctx, 1, "loop_cx");
        var newCx = ctx.Builder.BuildSub(cx,
            LLVMValueRef.CreateConstInt(i16, 1, false), "loop_cxN");
        X86_16Emitters.WriteGpr16(ctx, 1, newCx);

        // Predicate: cx != 0
        var cxNonZero = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, newCx,
            LLVMValueRef.CreateConstInt(i16, 0, false), "loop_cxnz");

        LLVMValueRef pred;
        if (kind == "loop")
        {
            pred = cxNonZero;
        }
        else
        {
            var flags = X86CtrlHelpers.LoadFlags(ctx, "loop");
            var zf = X86CtrlHelpers.ExtractFlagBit(ctx, flags, 6, "loop_zf");
            var zfReq = kind == "loope" ? zf : ctx.Builder.BuildNot(zf, "loop_nzf");
            pred = ctx.Builder.BuildAnd(cxNonZero, zfReq, "loop_pred");
        }

        var ipPtr = ctx.GepStatusRegister("IP");
        var ip = ctx.Builder.BuildLoad2(i16, ipPtr, "loop_ip");
        var ipPlus = ctx.Builder.BuildAdd(ip, disp16, "loop_ipP");
        var sel = ctx.Builder.BuildSelect(pred, ipPlus, ip, "loop_newip");
        ctx.Builder.BuildStore(sel, ipPtr);
    }
}

internal sealed class X86CallRel16Emitter : IMicroOpEmitter
{
    public string OpName => "x86_call_rel16";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i16 = LLVMTypeRef.Int16;
        var disp16 = X86_16Emitters.FetchImm16(ctx, "call_d");
        var ipPtr = ctx.GepStatusRegister("IP");
        var retIp = ctx.Builder.BuildLoad2(i16, ipPtr, "call_retip");
        // Push return address (post-fetch IP).
        X86StackHelpers.PushW16(ctx, retIp, "call_push");
        // Jump.
        var newIp = ctx.Builder.BuildAdd(retIp, disp16, "call_newip");
        ctx.Builder.BuildStore(newIp, ipPtr);
    }
}

internal sealed class X86RetNearEmitter : IMicroOpEmitter
{
    public string OpName => "x86_ret_near";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var ipPtr = ctx.GepStatusRegister("IP");
        var retIp = X86StackHelpers.PopW16(ctx, "ret_pop");
        ctx.Builder.BuildStore(retIp, ipPtr);
    }
}

internal sealed class X86RetNearImm16Emitter : IMicroOpEmitter
{
    public string OpName => "x86_ret_near_imm16";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i16 = LLVMTypeRef.Int16;
        // RET imm16 — pop IP, then SP += imm16. The imm16 is fetched from
        // CS:IP BEFORE the pop (so push args can be discarded post-return).
        var pop16 = X86_16Emitters.FetchImm16(ctx, "retn_d");
        var ipPtr = ctx.GepStatusRegister("IP");
        var retIp = X86StackHelpers.PopW16(ctx, "retn_pop");
        ctx.Builder.BuildStore(retIp, ipPtr);
        // SP += imm16 (post-pop adjustment to discard caller's pushed args)
        var spPtr = ctx.GepGpr(4);
        var sp = ctx.Builder.BuildLoad2(i16, spPtr, "retn_sp");
        var newSp = ctx.Builder.BuildAdd(sp, pop16, "retn_spN");
        ctx.Builder.BuildStore(newSp, spPtr);
    }
}
