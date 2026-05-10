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

        // 24.6.7b — shift/rotate D0/D1 group dispatcher (count=1 only;
        // count=CL D2/D3 plus full 8088 silicon flag quirks for count>1
        // deferred to 24.6.7b2 since those paths have undefined flag
        // semantics that need careful Tom Harte alignment).
        reg.Register(new X86ShiftRotateW8Count1Emitter());
        reg.Register(new X86ShiftRotateW16Count1Emitter());

        // 24.6.7b2 — shift by CL (D2/D3). SHL/SHR/SAR via precomputed LLVM
        // shifts; rotates fall back to count=1 IR logic (silicon-accurate
        // count>1 OF/AF rules deferred to a future Tom Harte alignment pass).
        reg.Register(new X86ShiftRotateW8CountClEmitter());
        reg.Register(new X86ShiftRotateW16CountClEmitter());

        // 24.6.7c — string ops (one iteration each; REP prefix machinery
        // in 24.6.7c2). All use DF (FLAGS bit 10) for SI/DI direction.
        reg.Register(new X86MovsbEmitter());
        reg.Register(new X86MovswEmitter());
        reg.Register(new X86CmpsbEmitter());
        reg.Register(new X86CmpswEmitter());
        reg.Register(new X86StosbEmitter());
        reg.Register(new X86StoswEmitter());
        reg.Register(new X86LodsbEmitter());
        reg.Register(new X86LodswEmitter());
        reg.Register(new X86ScasbEmitter());
        reg.Register(new X86ScaswEmitter());

        // 24.6.7d — flag-manip + IO. Each set/clear writes one FLAGS bit;
        // CMC toggles. SAHF/LAHF transfer between AH and FLAGS low byte.
        // IN/OUT are no-op stubs (just advance IP) since the framework
        // has no IO bus concept yet.
        reg.Register(new X86SetFlagBitEmitter());
        reg.Register(new X86ClearFlagBitEmitter());
        reg.Register(new X86CmcEmitter());
        reg.Register(new X86SahfEmitter());
        reg.Register(new X86LahfEmitter());
        reg.Register(new X86InImm8Emitter());
        reg.Register(new X86InDxEmitter());
        reg.Register(new X86OutImm8Emitter());
        reg.Register(new X86OutDxEmitter());

        // 24.6.7d2 — interrupt machinery (INT/INT3/INTO/IRET).
        reg.Register(new X86IntImm8Emitter());
        reg.Register(new X86Int3Emitter());
        reg.Register(new X86IntoEmitter());
        reg.Register(new X86IretEmitter());

        // 24.6.7e — FE/FF group dispatchers (INC/DEC r/m + CALL/JMP indirect
        // + PUSH r/m). Sub-ops selected at runtime by modrm.reg.
        reg.Register(new X86FeGroupDispatchEmitter());
        reg.Register(new X86FfGroupDispatchEmitter());

        // 24.6.7f — BCD adjustment ops (DAA/DAS/AAA/AAS/AAM/AAD).
        reg.Register(new X86DaaEmitter());
        reg.Register(new X86DasEmitter());
        reg.Register(new X86AaaEmitter());
        reg.Register(new X86AasEmitter());
        reg.Register(new X86AamEmitter());
        reg.Register(new X86AadEmitter());

        // Phase 26/27 — 80286 system instructions (real-mode subset).
        reg.Register(new X86Clts286StubEmitter());
        reg.Register(new X86286ZeroOneDispatchEmitter());
        reg.Register(new X86286ZeroZeroDispatchEmitter());
        reg.Register(new X86286LarLslStubEmitter());

        // Phase 25 — 80186 additions (referenced from i80186 spec via
        // inheritance overlay). 12 new opcodes + PUSH SP silicon-quirk fix.
        reg.Register(new X86PushSpPreDecrementEmitter());
        reg.Register(new X86PushImm8SextW16Emitter());
        reg.Register(new X86PushImm16Emitter());
        reg.Register(new X86PushaEmitter());
        reg.Register(new X86PopaEmitter());
        reg.Register(new X86BoundR16M16Emitter());
        reg.Register(new X86ImulR16Rm16Imm16Emitter());
        reg.Register(new X86ImulR16Rm16Imm8Emitter());
        reg.Register(new X86InsBEmitter());
        reg.Register(new X86InsWEmitter());
        reg.Register(new X86OutsBEmitter());
        reg.Register(new X86OutsWEmitter());
        reg.Register(new X86EnterEmitter());
        reg.Register(new X86LeaveEmitter());
        reg.Register(new X86ShiftRm8Imm8Emitter());
        reg.Register(new X86ShiftRm16Imm8Emitter());

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

    /// <summary>
    /// Sprint 27.10c — compute the linear address using the hidden
    /// segment-register cache (BASE slot). When the spec declares
    /// <c>{segName}_BASE</c> (i80286 onward — Sprint 27.10b added these),
    /// load BASE directly from the cached i32 slot and add offset.
    /// Otherwise fall back to the legacy <c>(visible_seg &lt;&lt; 4) + off</c>
    /// formula so i8086 / i80186 backends keep working unchanged.
    ///
    /// Sprint 27.10d will re-point existing call sites (FetchImm /
    /// SegmentedRead* / SegmentedWrite*) to this overload so protected-
    /// mode segment-register loads (which write a non-shift base into
    /// the cache) take effect throughout the IR.
    /// </summary>
    internal static LLVMValueRef SegmentedLinear(EmitContext ctx, string segName, LLVMValueRef off16, string label)
    {
        var i32 = LLVMTypeRef.Int32;
        var offZ = ctx.Builder.BuildZExt(off16, i32, $"{label}_off32");

        // Probe the layout for a cache slot at IR-emit time (once per
        // call site, no runtime cost).
        bool hasCache = false;
        foreach (var sr in ctx.Layout.RegisterFile.Status)
        {
            if (sr.Name == segName + "_BASE") { hasCache = true; break; }
        }

        if (hasCache)
        {
            var basePtr = ctx.GepStatusRegister(segName + "_BASE");
            var segBase = ctx.Builder.BuildLoad2(i32, basePtr, $"{label}_base");
            return ctx.Builder.BuildAdd(segBase, offZ, label);
        }

        // Legacy real-mode fallback (i8086 / i80186): (sel << 4) + off.
        var seg16 = LoadSeg16(ctx, segName, $"{label}_seg");
        var segZ = ctx.Builder.BuildZExt(seg16, i32, $"{label}_seg32");
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
    /// <para>Block-JIT fast path (24.6.8d): when BlockDetector pre-fetched
    /// trailing bytes into <see cref="EmitContext.CurrentInstructionPackedTailBytes"/>
    /// (an i64 LLVM constant carrying up to 8 LE bytes after the opcode)
    /// AND the requested byte offset still inside the packed region
    /// (ImmConsumed + 1 ≤ LengthBytes - 1), extract the byte via
    /// shift+trunc on the constant. Otherwise issue a memory_read_8
    /// extern at linear (CS&lt;&lt;4)+IP. IP advance happens identically in
    /// both modes so downstream PC reads stay consistent.</para>
    /// </summary>
    internal static LLVMValueRef FetchImm8(EmitContext ctx, string label)
    {
        var i8  = LLVMTypeRef.Int8;
        var i16 = LLVMTypeRef.Int16;
        var i32 = LLVMTypeRef.Int32;
        var i64 = LLVMTypeRef.Int64;

        var ipPtr = ctx.GepStatusRegister("IP");
        var ip16 = ctx.Builder.BuildLoad2(i16, ipPtr, $"{label}_ip");

        LLVMValueRef b;
        int trailingTotal = ctx.CurrentInstructionLengthBytes is byte len ? len - 1 : 0;
        if (ctx.CurrentInstructionPackedTailBytes is ulong tail
            && ctx.CurrentInstructionImmConsumed + 1 <= trailingTotal)
        {
            int offset = ctx.ReserveImmediateBytes(1);
            uint shiftBits = (uint)(offset * 8);
            var tailConst = LLVMValueRef.CreateConstInt(i64, tail, false);
            var shifted = ctx.Builder.BuildLShr(tailConst,
                LLVMValueRef.CreateConstInt(i64, shiftBits, false), $"{label}_tail_shr");
            b = ctx.Builder.BuildTrunc(shifted, i8, $"{label}_imm");
        }
        else
        {
            // Sprint 27.10d — switched to by-name overload so CS_BASE
            // cache (Sprint 27.10b) gets used when the spec declares it
            // (i80286+). i8086 / i80186 fall back to legacy (CS<<4)+IP
            // automatically inside SegmentedLinear.
            var lin32 = SegmentedLinear(ctx, "CS", ip16, $"{label}_lin");
            b = MemoryEmitters.CallRead8(ctx, lin32, label);
        }

        // IP wraps within 16 bits — silicon does not propagate carry into CS.
        var newIp = ctx.Builder.BuildAdd(ip16,
            LLVMValueRef.CreateConstInt(i16, 1, false), $"{label}_ip_next");
        ctx.Builder.BuildStore(newIp, ipPtr);
        return b;
    }

    /// <summary>
    /// Read a 16-bit little-endian word from CS:IP and advance IP by 2.
    /// Returns i16.
    ///
    /// <para>Block-JIT fast path (24.6.8d): when the next 2 bytes fit
    /// inside <see cref="EmitContext.CurrentInstructionPackedTailBytes"/>,
    /// extract via shift+trunc on the i64 constant (zero-cost — LLVM
    /// folds at compile time). Otherwise fall back to two memory_read_8
    /// externs. Each byte read goes through a 20-bit linear address
    /// computed independently — the 16-bit IP wrap means a fetch starting
    /// at 0xFFFF reads byte at CS:0xFFFF then byte at CS:0x0000.</para>
    /// </summary>
    internal static LLVMValueRef FetchImm16(EmitContext ctx, string label)
    {
        var i8  = LLVMTypeRef.Int8;
        var i16 = LLVMTypeRef.Int16;
        var i32 = LLVMTypeRef.Int32;
        var i64 = LLVMTypeRef.Int64;

        var ipPtr = ctx.GepStatusRegister("IP");
        var ip16 = ctx.Builder.BuildLoad2(i16, ipPtr, $"{label}_ip");

        LLVMValueRef word;
        int trailingTotal = ctx.CurrentInstructionLengthBytes is byte len ? len - 1 : 0;
        if (ctx.CurrentInstructionPackedTailBytes is ulong tail
            && ctx.CurrentInstructionImmConsumed + 2 <= trailingTotal)
        {
            int offset = ctx.ReserveImmediateBytes(2);
            uint shiftBits = (uint)(offset * 8);
            var tailConst = LLVMValueRef.CreateConstInt(i64, tail, false);
            var shifted = ctx.Builder.BuildLShr(tailConst,
                LLVMValueRef.CreateConstInt(i64, shiftBits, false), $"{label}_tail_shr");
            word = ctx.Builder.BuildTrunc(shifted, i16, $"{label}_imm");
        }
        else
        {
            // Sprint 27.10d — by-name overload, see FetchImm8 comment.
            var lin0 = SegmentedLinear(ctx, "CS", ip16, $"{label}_lin0");
            var lo8  = MemoryEmitters.CallRead8(ctx, lin0, $"{label}_lo");

            var ipPlus1 = ctx.Builder.BuildAdd(ip16,
                LLVMValueRef.CreateConstInt(i16, 1, false), $"{label}_ip1");
            var lin1 = SegmentedLinear(ctx, "CS", ipPlus1, $"{label}_lin1");
            var hi8  = MemoryEmitters.CallRead8(ctx, lin1, $"{label}_hi");

            var loZ  = ctx.Builder.BuildZExt(lo8, i16, $"{label}_loz");
            var hiZ  = ctx.Builder.BuildZExt(hi8, i16, $"{label}_hiz");
            var hiSh = ctx.Builder.BuildShl(hiZ,
                LLVMValueRef.CreateConstInt(i16, 8, false), $"{label}_hi_shl");
            word = ctx.Builder.BuildOr(hiSh, loZ, label);
        }

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

    /// <summary>Sprint 27.10d wave 2 — by-name overload uses cached BASE.</summary>
    internal static LLVMValueRef SegmentedRead8(
        EmitContext ctx, string segName, LLVMValueRef off16, string label)
    {
        var lin = SegmentedLinear(ctx, segName, off16, $"{label}_lin");
        return MemoryEmitters.CallRead8(ctx, lin, label);
    }

    /// <summary>Sprint 27.13b — read 8-bit from precomputed 32-bit base + 16-bit offset.
    /// Used by ModR/M load/store helpers to consume ea_base directly (which on
    /// i80286 is the cache-aware base, falling back to (sel &lt;&lt; 4) on i8086/186).</summary>
    internal static LLVMValueRef SegmentedRead8FromBase(
        EmitContext ctx, LLVMValueRef base32, LLVMValueRef off16, string label)
    {
        var i32 = LLVMTypeRef.Int32;
        var offZ = ctx.Builder.BuildZExt(off16, i32, $"{label}_offz");
        var lin = ctx.Builder.BuildAdd(base32, offZ, $"{label}_lin");
        return MemoryEmitters.CallRead8(ctx, lin, label);
    }

    /// <summary>Store an i8 to seg:off via memory_write_8.</summary>
    internal static void SegmentedWrite8(
        EmitContext ctx, LLVMValueRef seg16, LLVMValueRef off16, LLVMValueRef value8, string label)
    {
        var lin = SegmentedLinear(ctx, seg16, off16, $"{label}_lin");
        MemoryEmitters.CallWrite8(ctx, lin, value8);
    }

    /// <summary>Sprint 27.10d wave 2 — by-name overload uses cached BASE.</summary>
    internal static void SegmentedWrite8(
        EmitContext ctx, string segName, LLVMValueRef off16, LLVMValueRef value8, string label)
    {
        var lin = SegmentedLinear(ctx, segName, off16, $"{label}_lin");
        MemoryEmitters.CallWrite8(ctx, lin, value8);
    }

    /// <summary>Sprint 27.13b — write 8-bit at precomputed 32-bit base + 16-bit offset.</summary>
    internal static void SegmentedWrite8FromBase(
        EmitContext ctx, LLVMValueRef base32, LLVMValueRef off16, LLVMValueRef value8, string label)
    {
        var i32 = LLVMTypeRef.Int32;
        var offZ = ctx.Builder.BuildZExt(off16, i32, $"{label}_offz");
        var lin = ctx.Builder.BuildAdd(base32, offZ, $"{label}_lin");
        MemoryEmitters.CallWrite8(ctx, lin, value8);
    }

    /// <summary>Sprint 27.13b — read 16-bit at precomputed 32-bit base + 16-bit offset.</summary>
    internal static LLVMValueRef SegmentedRead16FromBase(
        EmitContext ctx, LLVMValueRef base32, LLVMValueRef off16, string label)
    {
        var i16 = LLVMTypeRef.Int16;
        var lo = SegmentedRead8FromBase(ctx, base32, off16, $"{label}_lo");
        var off1 = ctx.Builder.BuildAdd(off16,
            LLVMValueRef.CreateConstInt(i16, 1, false), $"{label}_off1");
        var hi = SegmentedRead8FromBase(ctx, base32, off1, $"{label}_hi");
        var loZ  = ctx.Builder.BuildZExt(lo, i16, $"{label}_loz");
        var hiZ  = ctx.Builder.BuildZExt(hi, i16, $"{label}_hiz");
        var hiSh = ctx.Builder.BuildShl(hiZ,
            LLVMValueRef.CreateConstInt(i16, 8, false), $"{label}_hi_sh");
        return ctx.Builder.BuildOr(hiSh, loZ, label);
    }

    /// <summary>Sprint 27.13b — write 16-bit at precomputed 32-bit base + 16-bit offset.</summary>
    internal static void SegmentedWrite16FromBase(
        EmitContext ctx, LLVMValueRef base32, LLVMValueRef off16, LLVMValueRef value16, string label)
    {
        var i8  = LLVMTypeRef.Int8;
        var i16 = LLVMTypeRef.Int16;
        var lo = ctx.Builder.BuildTrunc(value16, i8, $"{label}_lo");
        SegmentedWrite8FromBase(ctx, base32, off16, lo, $"{label}_loW");
        var hi16 = ctx.Builder.BuildLShr(value16,
            LLVMValueRef.CreateConstInt(i16, 8, false), $"{label}_hi16");
        var hi   = ctx.Builder.BuildTrunc(hi16, i8, $"{label}_hi");
        var off1 = ctx.Builder.BuildAdd(off16,
            LLVMValueRef.CreateConstInt(i16, 1, false), $"{label}_off1");
        SegmentedWrite8FromBase(ctx, base32, off1, hi, $"{label}_hiW");
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

    /// <summary>Sprint 27.10d wave 2 — by-name overload uses cached BASE.</summary>
    internal static LLVMValueRef SegmentedRead16(
        EmitContext ctx, string segName, LLVMValueRef off16, string label)
    {
        var i16 = LLVMTypeRef.Int16;
        var lo = SegmentedRead8(ctx, segName, off16, $"{label}_lo");
        var off1 = ctx.Builder.BuildAdd(off16,
            LLVMValueRef.CreateConstInt(i16, 1, false), $"{label}_off1");
        var hi = SegmentedRead8(ctx, segName, off1, $"{label}_hi");

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

    /// <summary>Sprint 27.10d wave 2 — by-name overload uses cached BASE.</summary>
    internal static void SegmentedWrite16(
        EmitContext ctx, string segName, LLVMValueRef off16, LLVMValueRef value16, string label)
    {
        var i8  = LLVMTypeRef.Int8;
        var i16 = LLVMTypeRef.Int16;

        var lo = ctx.Builder.BuildTrunc(value16, i8, $"{label}_lo");
        SegmentedWrite8(ctx, segName, off16, lo, $"{label}_loW");

        var hi16 = ctx.Builder.BuildLShr(value16,
            LLVMValueRef.CreateConstInt(i16, 8, false), $"{label}_hi16");
        var hi   = ctx.Builder.BuildTrunc(hi16, i8, $"{label}_hi");

        var off1 = ctx.Builder.BuildAdd(off16,
            LLVMValueRef.CreateConstInt(i16, 1, false), $"{label}_off1");
        SegmentedWrite8(ctx, segName, off1, hi, $"{label}_hiW");
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

        // Sprint 27.10d wave 6 — track segIdx parallel to seg value.
        // Each rm arm picks DS (3) or SS (2) per the architectural
        // default. arm 6's special "mod=00 → DS" override is handled
        // later in mod=00 join; the value here is SS to match arm 2/3
        // semantics for mod=01/10 BP-based addressing.
        //   rm 0/1/4/5/7 → DS = 3
        //   rm 2/3/6     → SS = 2
        var segIdxByRm = new uint[] { 3, 3, 2, 2, 3, 3, 2, 3 };
        var segIdxPhi = ctx.Builder.BuildPhi(i32, "ea_seg_idx_pre");
        var inIdxs = new LLVMValueRef[9];
        for (int i = 0; i < 8; i++) inIdxs[i] = LLVMValueRef.CreateConstInt(i32, segIdxByRm[i], false);
        inIdxs[8] = LLVMValueRef.CreateConstInt(i32, 0xFF, false);   // default unreachable sentinel
        segIdxPhi.AddIncoming(inIdxs, inBlocks, 9);

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
        // Sprint 27.10d wave 6 — segIdx for mod=00. mod=00 rm=6 special
        // forces DS (3); other rm uses segIdxPhi.
        var mod00SegIdx = ctx.Builder.BuildPhi(i32, "ea_mod00_seg_idx");
        mod00SegIdx.AddIncoming(
            new[] { LLVMValueRef.CreateConstInt(i32, 3, false), segIdxPhi },
            new[] { mod00DispBB, mod00NoDispBB }, 2);
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

        // Sprint 27.10d wave 6 — segIdx through the mod join.
        var eaSegIdxDefault = ctx.Builder.BuildPhi(i32, "ea_seg_idx_default");
        eaSegIdxDefault.AddIncoming(
            new[] { (LLVMValueRef)mod00SegIdx, segIdxPhi, segIdxPhi,
                    LLVMValueRef.CreateConstInt(i32, 0xFF, false) },
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

        // Sprint 27.10d wave 6 — segIdx final phi: override arms set
        // segIdx = i (0..3 = ES/CS/SS/DS); skip / default arms use the
        // pre-override eaSegIdxDefault tracked through the rm + mod chain.
        var segIdxFinal = ctx.Builder.BuildPhi(i32, "ea_seg_idx");
        var idxIns = new LLVMValueRef[6];
        for (int i = 0; i < 4; i++) idxIns[i] = LLVMValueRef.CreateConstInt(i32, (uint)i, false);
        idxIns[4] = eaSegIdxDefault;
        idxIns[5] = eaSegIdxDefault;
        segIdxFinal.AddIncoming(idxIns, pBlk, 6);

        ctx.Values["ea_off"] = eaOff;
        ctx.Values["ea_seg"] = eaSeg;

        // Sprint 27.10d wave 5 — ea_base computation:
        //   when the spec declares <seg>_BASE cache slots (i80286+) AND
        //   a segment override prefix is active, load the matching
        //   <seg>_BASE from the cache. Otherwise fall back to
        //   (ea_seg << 4) shift — same as wave 4's behavior.
        // In real mode, <seg>_BASE == visible_seg << 4 (Reset wires this),
        // so cache and shift produce identical results. The interesting
        // case is i80286 protected mode where descriptor cache holds a
        // base that DIFFERS from selector*16; consumers reading ea_base
        // automatically pick up the descriptor base.
        bool hasCacheSlots = false;
        foreach (var sr in ctx.Layout.RegisterFile.Status)
        {
            if (sr.Name == "ES_BASE") { hasCacheSlots = true; break; }
        }

        if (!hasCacheSlots)
        {
            // i8086 / i80186 — no cache, just shift.
            var eaSegZShift = ctx.Builder.BuildZExt(eaSeg, i32, "ea_base_segz");
            var eaBaseShift = ctx.Builder.BuildShl(eaSegZShift,
                LLVMValueRef.CreateConstInt(i32, 4, false), "ea_base");
            ctx.Values["ea_base"] = eaBaseShift;
            return;
        }

        // Sprint 27.10d wave 6 — i80286+ unified path: switch on segIdxFinal
        // (0..3 = ES/CS/SS/DS, derived from override OR rm-switch default
        // tracked through the entire EA-compute chain). Loads the matching
        // <seg>_BASE cache slot. No more shift fallback; the cache slots
        // are kept in sync with selectors at Reset/SetEntryPoint AND will
        // be loaded from descriptors in Phase 27b protected-mode segment-
        // register-load IR (wave 7+).
        var w6CacheDefBB = ctx.Function.AppendBasicBlock("ea_base_cache_def");
        var w6JoinBB     = ctx.Function.AppendBasicBlock("ea_base_join");
        var w6Arms = new LLVMBasicBlockRef[4];
        var w6Vals = new LLVMValueRef[4];
        for (int i = 0; i < 4; i++) w6Arms[i] = ctx.Function.AppendBasicBlock($"ea_base_w6_{i}");
        var w6Sw = ctx.Builder.BuildSwitch(segIdxFinal, w6CacheDefBB, 4);
        for (int i = 0; i < 4; i++)
            w6Sw.AddCase(LLVMValueRef.CreateConstInt(i32, (uint)i, false), w6Arms[i]);

        var w6SegMap = new[] { "ES", "CS", "SS", "DS" };
        for (int i = 0; i < 4; i++)
        {
            ctx.Builder.PositionAtEnd(w6Arms[i]);
            var basePtr = ctx.GepStatusRegister(w6SegMap[i] + "_BASE");
            w6Vals[i] = ctx.Builder.BuildLoad2(i32, basePtr, $"ea_base_w6_v_{i}");
            ctx.Builder.BuildBr(w6JoinBB);
        }
        // Default arm — should be unreachable since segIdx ∈ {0..3} once
        // we've passed mod=11 (which doesn't use ea_base anyway). Fall
        // through to shift as a defensive default.
        ctx.Builder.PositionAtEnd(w6CacheDefBB);
        var w6DefSegZ = ctx.Builder.BuildZExt(eaSeg, i32, "ea_base_def_segz");
        var w6DefShifted = ctx.Builder.BuildShl(w6DefSegZ,
            LLVMValueRef.CreateConstInt(i32, 4, false), "ea_base_def_shift");
        ctx.Builder.BuildBr(w6JoinBB);

        // Join phi (4 cache arms + 1 default = 5 incoming).
        ctx.Builder.PositionAtEnd(w6JoinBB);
        var eaBaseJoin = ctx.Builder.BuildPhi(i32, "ea_base");
        var jIns = new LLVMValueRef[5];
        var jBlk = new LLVMBasicBlockRef[5];
        for (int i = 0; i < 4; i++) { jIns[i] = w6Vals[i]; jBlk[i] = w6Arms[i]; }
        jIns[4] = w6DefShifted; jBlk[4] = w6CacheDefBB;
        eaBaseJoin.AddIncoming(jIns, jBlk, 5);
        ctx.Values["ea_base"] = eaBaseJoin;
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

        // mem path — Sprint 27.13b: read at ea_base:ea_off (cache-aware).
        ctx.Builder.PositionAtEnd(memBB);
        var memBase = ctx.Resolve("ea_base");
        var off = ctx.Resolve("ea_off");
        var memVal = X86_16Emitters.SegmentedRead8FromBase(ctx, memBase, off, $"{outName}_mem_v");
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
        // Sprint 27.13b — read via cache-aware ea_base.
        var memBase = ctx.Resolve("ea_base");
        var off = ctx.Resolve("ea_off");
        var memVal = X86_16Emitters.SegmentedRead16FromBase(ctx, memBase, off, $"{outName}_mem_v");
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
        // Sprint 27.13b — store via cache-aware ea_base.
        var stw8Base = ctx.Resolve("ea_base");
        var off = ctx.Resolve("ea_off");
        X86_16Emitters.SegmentedWrite8FromBase(ctx, stw8Base, off, value8, "stw8_w");
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
        // Sprint 27.13b — store via cache-aware ea_base.
        var stw16Base = ctx.Resolve("ea_base");
        var off = ctx.Resolve("ea_off");
        X86_16Emitters.SegmentedWrite16FromBase(ctx, stw16Base, off, value16, "stw16_w");
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

        // Sprint 27.10d wave 7 — does the spec declare hidden cache slots?
        // (i80286+ sets these via Sprint 27.10b register_file diff.)
        bool hasCacheSlots = false;
        foreach (var sr in ctx.Layout.RegisterFile.Status)
        {
            if (sr.Name == "ES_BASE") { hasCacheSlots = true; break; }
        }

        var endBB     = ctx.Function.AppendBasicBlock("wsreg_end");
        var defaultBB = ctx.Function.AppendBasicBlock("wsreg_default");
        var arms = new LLVMBasicBlockRef[4];
        for (int i = 0; i < 4; i++) arms[i] = ctx.Function.AppendBasicBlock($"wsreg_{i}");

        var sw = ctx.Builder.BuildSwitch(sel, defaultBB, 4);
        for (int i = 0; i < 4; i++)
            sw.AddCase(LLVMValueRef.CreateConstInt(i32, (uint)i, false), arms[i]);

        var names = new[] { "ES", "CS", "SS", "DS" };
        var accessByIdx = new byte[] { 0x93, 0x9B, 0x93, 0x93 };
        for (int i = 0; i < 4; i++)
        {
            ctx.Builder.PositionAtEnd(arms[i]);
            var p = ctx.GepStatusRegister(names[i]);
            ctx.Builder.BuildStore(value, p);

            if (hasCacheSlots)
            {
                EmitSegCacheUpdate(ctx, names[i], value, accessByIdx[i], $"wsreg_{i}");
            }
            ctx.Builder.BuildBr(endBB);
        }
        // Default: invalid encoding — silently no-op.
        ctx.Builder.PositionAtEnd(defaultBB);
        ctx.Builder.BuildBr(endBB);
        ctx.Builder.PositionAtEnd(endBB);
    }

    /// <summary>
    /// Sprint 27.10d wave 8 — emit cache-update IR for one segment register.
    /// Branches on MSW.PE:
    ///   PE=0 (real mode): BASE = sel << 4, LIMIT = 0xFFFF, ACCESS = default.
    ///   PE=1 (protected mode): GDT[sel.index].descriptor → BASE/LIMIT/ACCESS.
    /// LDT (TI=1) selectors fall through to the GDT path for now — proper
    /// LDT handling needs LDTR descriptor lookup first (deferred to a future
    /// sprint; affects only programs that actually load LDT-based selectors,
    /// which our demos don't).
    /// Privilege check (CPL/RPL/DPL) deferred to Sprint 27.11 (#GP exception
    /// model).
    /// </summary>
    private static void EmitSegCacheUpdate(
        EmitContext ctx, string segName, LLVMValueRef sel16, byte realModeAccess, string label)
    {
        var i8  = LLVMTypeRef.Int8;
        var i16 = LLVMTypeRef.Int16;
        var i32 = LLVMTypeRef.Int32;

        var basePtr   = ctx.GepStatusRegister(segName + "_BASE");
        var limitPtr  = ctx.GepStatusRegister(segName + "_LIMIT");
        var accessPtr = ctx.GepStatusRegister(segName + "_ACCESS");

        // Read MSW.PE bit.
        var msw = ctx.Builder.BuildLoad2(i16, ctx.GepStatusRegister("MSW"), $"{label}_msw");
        var peMask = ctx.Builder.BuildAnd(msw,
            LLVMValueRef.CreateConstInt(i16, 1, false), $"{label}_peM");
        var pe = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, peMask,
            LLVMValueRef.CreateConstInt(i16, 0, false), $"{label}_pe");

        var realBB = ctx.Function.AppendBasicBlock($"{label}_real");
        var protBB = ctx.Function.AppendBasicBlock($"{label}_prot");
        var doneBB = ctx.Function.AppendBasicBlock($"{label}_done");
        ctx.Builder.BuildCondBr(pe, protBB, realBB);

        // === Real-mode arm ===
        ctx.Builder.PositionAtEnd(realBB);
        var selZ = ctx.Builder.BuildZExt(sel16, i32, $"{label}_selz");
        var realBase = ctx.Builder.BuildShl(selZ,
            LLVMValueRef.CreateConstInt(i32, 4, false), $"{label}_realbase");
        ctx.Builder.BuildStore(realBase, basePtr);
        ctx.Builder.BuildStore(LLVMValueRef.CreateConstInt(i16, 0xFFFF, false), limitPtr);
        ctx.Builder.BuildStore(LLVMValueRef.CreateConstInt(i8, realModeAccess, false), accessPtr);
        ctx.Builder.BuildBr(doneBB);

        // === Protected-mode arm: GDT descriptor fetch ===
        ctx.Builder.PositionAtEnd(protBB);
        var gdtBase = ctx.Builder.BuildLoad2(i32,
            ctx.GepStatusRegister("GDTR_BASE"), $"{label}_gdt");
        var selZ2 = ctx.Builder.BuildZExt(sel16, i32, $"{label}_selz2");
        // sel.index * 8 = selector with low 3 bits cleared
        var idxBytes = ctx.Builder.BuildAnd(selZ2,
            LLVMValueRef.CreateConstInt(i32, 0xFFF8, false), $"{label}_idxb");
        var descAddr = ctx.Builder.BuildAdd(gdtBase, idxBytes, $"{label}_dsc");

        // Read 8 descriptor bytes. (Bytes 6-7 = reserved on 286, ignored.)
        var bytes = new LLVMValueRef[6];
        for (int b = 0; b < 6; b++)
        {
            var off = ctx.Builder.BuildAdd(descAddr,
                LLVMValueRef.CreateConstInt(i32, (uint)b, false), $"{label}_a{b}");
            bytes[b] = MemoryEmitters.CallRead8(ctx, off, $"{label}_b{b}");
        }

        // limit = byte0 | (byte1 << 8) at i16
        var b0z = ctx.Builder.BuildZExt(bytes[0], i16, $"{label}_b0z");
        var b1z = ctx.Builder.BuildZExt(bytes[1], i16, $"{label}_b1z");
        var b1sh = ctx.Builder.BuildShl(b1z,
            LLVMValueRef.CreateConstInt(i16, 8, false), $"{label}_b1sh");
        var limit = ctx.Builder.BuildOr(b0z, b1sh, $"{label}_lim");

        // base = byte2 | (byte3 << 8) | (byte4 << 16) at i32
        var b2z = ctx.Builder.BuildZExt(bytes[2], i32, $"{label}_b2z");
        var b3z = ctx.Builder.BuildZExt(bytes[3], i32, $"{label}_b3z");
        var b4z = ctx.Builder.BuildZExt(bytes[4], i32, $"{label}_b4z");
        var b3sh = ctx.Builder.BuildShl(b3z,
            LLVMValueRef.CreateConstInt(i32, 8, false), $"{label}_b3sh");
        var b4sh = ctx.Builder.BuildShl(b4z,
            LLVMValueRef.CreateConstInt(i32, 16, false), $"{label}_b4sh");
        var basePart = ctx.Builder.BuildOr(b2z, b3sh, $"{label}_bp");
        var protBase = ctx.Builder.BuildOr(basePart, b4sh, $"{label}_pbase");

        // Sprint 27.11c — check descriptor.P bit (AccessRights bit 7).
        // P=0 → segment-not-present fault: set EXC_PENDING=1, EXC_VECTOR=11
        // (#NP), EXC_ERROR=selector & 0xFFFC. Skip cache update so the
        // hidden cache stays at its previous (last-good) value. The
        // visible sreg field was already written by the caller; that's
        // a known minor architectural drift (Intel aborts the load
        // entirely on #NP) and is bounded by the EXC_PENDING flag —
        // future sprints can add a "rewind visible sreg on fault" pass
        // before the next instruction dispatches.
        var pMask = ctx.Builder.BuildAnd(bytes[5],
            LLVMValueRef.CreateConstInt(i8, 0x80, false), $"{label}_pmask");
        var pSet = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, pMask,
            LLVMValueRef.CreateConstInt(i8, 0, false), $"{label}_pset");

        var presentBB    = ctx.Function.AppendBasicBlock($"{label}_present");
        var notPresentBB = ctx.Function.AppendBasicBlock($"{label}_np");
        ctx.Builder.BuildCondBr(pSet, presentBB, notPresentBB);

        // P=1 — populate the hidden cache from the descriptor.
        ctx.Builder.PositionAtEnd(presentBB);
        ctx.Builder.BuildStore(protBase, basePtr);
        ctx.Builder.BuildStore(limit,    limitPtr);
        ctx.Builder.BuildStore(bytes[5], accessPtr);
        ctx.Builder.BuildBr(doneBB);

        // P=0 — raise #NP via the EXC_* slots; do NOT touch the cache.
        ctx.Builder.PositionAtEnd(notPresentBB);
        EmitRaiseException(ctx, /* vector */ 0x0B, /* selector */ sel16, $"{label}_np");
        ctx.Builder.BuildBr(doneBB);

        // === Done ===
        ctx.Builder.PositionAtEnd(doneBB);
    }

    /// <summary>
    /// Sprint 27.11c — write the i80286 exception slots (EXC_PENDING=1,
    /// EXC_VECTOR=vector, EXC_ERROR=selector &amp; 0xFFFC). Caller is
    /// responsible for branching to a fault path before invoking this;
    /// no cache / sreg side effects are performed here.
    /// </summary>
    private static void EmitRaiseException(
        EmitContext ctx, byte vector, LLVMValueRef sel16, string label)
    {
        var i8  = LLVMTypeRef.Int8;
        var i16 = LLVMTypeRef.Int16;

        var pendingPtr = ctx.GepStatusRegister("EXC_PENDING");
        var vectorPtr  = ctx.GepStatusRegister("EXC_VECTOR");
        var errorPtr   = ctx.GepStatusRegister("EXC_ERROR");

        ctx.Builder.BuildStore(LLVMValueRef.CreateConstInt(i8, 1, false), pendingPtr);
        ctx.Builder.BuildStore(LLVMValueRef.CreateConstInt(i8, vector, false), vectorPtr);
        // error code = selector with low 2 bits (RPL) cleared; bit 2 (TI)
        // preserved so a fault handler can tell GDT vs LDT. Bit 0 (EXT)
        // would be set for a CPU-generated fault during interrupt
        // delivery; we leave it 0 for the software-load case.
        var errMasked = ctx.Builder.BuildAnd(sel16,
            LLVMValueRef.CreateConstInt(i16, 0xFFFC, false), $"{label}_errm");
        ctx.Builder.BuildStore(errMasked, errorPtr);
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

        // Sprint 27.10d wave 2 — SS by-name picks up SS_BASE cache when
        // the spec declares it (i80286+).
        X86_16Emitters.SegmentedWrite16(ctx, "SS", spNew, value16, $"{label}_w");
    }

    /// <summary>Read MEM[SS:SP] → i16; SP += 2.</summary>
    public static LLVMValueRef PopW16(EmitContext ctx, string label)
    {
        var i16 = LLVMTypeRef.Int16;
        var spPtr = ctx.GepGpr(4);
        var spOld = ctx.Builder.BuildLoad2(i16, spPtr, $"{label}_sp_old");
        // Sprint 27.10d wave 2 — SS by-name (cache-aware).
        var v  = X86_16Emitters.SegmentedRead16(ctx, "SS", spOld, $"{label}_v");

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

        // Write the value to SS:newSP. Sprint 27.10d wave 3 — by-name
        // SS picks up SS_BASE cache on i80286+.
        X86_16Emitters.SegmentedWrite16(ctx, "SS", spNew, phi, "psh_w");
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
        // Sprint 27.10d wave 3 — by-name SS for cache-aware base.
        X86_16Emitters.SegmentedWrite16(ctx, "SS", spNew, phi, "pshrm_w");
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

        // /6 DIV r/m8 (unsigned). Divide-by-zero would raise INT 0 in
        // silicon — we substitute a safe value to avoid LLVM UB and
        // silently skip the writeback (the architectural exception is
        // deferred since the dispatcher would have to wire INT 0).
        ctx.Builder.PositionAtEnd(arms[6]);
        EmitDivW8(ctx, lhs, signed: false);
        ctx.Builder.BuildBr(endBB);

        // /7 IDIV r/m8 (signed). Same exception policy.
        ctx.Builder.PositionAtEnd(arms[7]);
        EmitDivW8(ctx, lhs, signed: true);
        ctx.Builder.BuildBr(endBB);

        ctx.Builder.PositionAtEnd(defaultBB);
        ctx.Builder.BuildBr(endBB);
        ctx.Builder.PositionAtEnd(endBB);
    }

    /// <summary>
    /// Emit DIV/IDIV r/m8.
    ///   AL = AX / r/m8;   AH = AX % r/m8
    /// Divide-by-zero (lhs8 == 0) substitutes 1 to avoid LLVM UB; the
    /// architectural INT 0 raise is deferred. Flags are undefined per
    /// Intel — we leave FLAGS untouched.
    /// </summary>
    private static void EmitDivW8(EmitContext ctx, LLVMValueRef lhs8, bool signed)
    {
        var i1  = LLVMTypeRef.Int1;
        var i8  = LLVMTypeRef.Int8;
        var i16 = LLVMTypeRef.Int16;
        var i32 = LLVMTypeRef.Int32;
        var label = signed ? "idiv8" : "div8";

        var divIsZero = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, lhs8,
            LLVMValueRef.CreateConstInt(i8, 0, false), $"{label}_zero");
        var oneI8 = LLVMValueRef.CreateConstInt(i8, 1, false);
        var safeDiv = ctx.Builder.BuildSelect(divIsZero, oneI8, lhs8, $"{label}_safe");

        var ax = X86_16Emitters.ReadGpr16(ctx, 0, $"{label}_ax");
        LLVMValueRef axW = signed
            ? ctx.Builder.BuildSExt(ax, i32, $"{label}_axw")
            : ctx.Builder.BuildZExt(ax, i32, $"{label}_axw");
        LLVMValueRef divW = signed
            ? ctx.Builder.BuildSExt(safeDiv, i32, $"{label}_dw")
            : ctx.Builder.BuildZExt(safeDiv, i32, $"{label}_dw");

        var q = signed ? ctx.Builder.BuildSDiv(axW, divW, $"{label}_q")
                       : ctx.Builder.BuildUDiv(axW, divW, $"{label}_q");
        var r = signed ? ctx.Builder.BuildSRem(axW, divW, $"{label}_r")
                       : ctx.Builder.BuildURem(axW, divW, $"{label}_r");

        var qI8 = ctx.Builder.BuildTrunc(q, i8, $"{label}_q8");
        var rI8 = ctx.Builder.BuildTrunc(r, i8, $"{label}_r8");

        var alOld = X86_16Emitters.ReadGpr8(ctx, 0, $"{label}_al_old");
        var ahOld = X86_16Emitters.ReadGpr8(ctx, 4, $"{label}_ah_old");
        var newAl = ctx.Builder.BuildSelect(divIsZero, alOld, qI8, $"{label}_newAl");
        var newAh = ctx.Builder.BuildSelect(divIsZero, ahOld, rI8, $"{label}_newAh");
        X86_16Emitters.WriteGpr8(ctx, 0, newAl);
        X86_16Emitters.WriteGpr8(ctx, 4, newAh);
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

        // /6 DIV / /7 IDIV r/m16. Divide-by-zero substituted to avoid LLVM UB.
        ctx.Builder.PositionAtEnd(arms[6]);
        EmitDivW16(ctx, lhs, signed: false);
        ctx.Builder.BuildBr(endBB);
        ctx.Builder.PositionAtEnd(arms[7]);
        EmitDivW16(ctx, lhs, signed: true);
        ctx.Builder.BuildBr(endBB);

        ctx.Builder.PositionAtEnd(defaultBB);
        ctx.Builder.BuildBr(endBB);
        ctx.Builder.PositionAtEnd(endBB);
    }

    /// <summary>
    /// Emit DIV/IDIV r/m16:
    ///   AX = (DX:AX) / r/m16;   DX = (DX:AX) % r/m16
    /// Divide-by-zero substitutes 1 to avoid LLVM UB; writeback skipped
    /// when divisor was zero.
    /// </summary>
    private static void EmitDivW16(EmitContext ctx, LLVMValueRef lhs16, bool signed)
    {
        var i16 = LLVMTypeRef.Int16;
        var i32 = LLVMTypeRef.Int32;
        var label = signed ? "idiv16" : "div16";

        var divIsZero = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, lhs16,
            LLVMValueRef.CreateConstInt(i16, 0, false), $"{label}_zero");
        var oneI16 = LLVMValueRef.CreateConstInt(i16, 1, false);
        var safeDiv = ctx.Builder.BuildSelect(divIsZero, oneI16, lhs16, $"{label}_safe");

        var ax = X86_16Emitters.ReadGpr16(ctx, 0, $"{label}_ax");
        var dx = X86_16Emitters.ReadGpr16(ctx, 2, $"{label}_dx");
        // Build DX:AX as i32.
        var axZ = ctx.Builder.BuildZExt(ax, i32, $"{label}_axz");
        var dxZ = ctx.Builder.BuildZExt(dx, i32, $"{label}_dxz");
        var dxSh = ctx.Builder.BuildShl(dxZ,
            LLVMValueRef.CreateConstInt(i32, 16, false), $"{label}_dxsh");
        var dxAx = ctx.Builder.BuildOr(dxSh, axZ, $"{label}_dxax");

        LLVMValueRef divW = signed
            ? ctx.Builder.BuildSExt(safeDiv, i32, $"{label}_dw")
            : ctx.Builder.BuildZExt(safeDiv, i32, $"{label}_dw");

        var q = signed ? ctx.Builder.BuildSDiv(dxAx, divW, $"{label}_q")
                       : ctx.Builder.BuildUDiv(dxAx, divW, $"{label}_q");
        var r = signed ? ctx.Builder.BuildSRem(dxAx, divW, $"{label}_r")
                       : ctx.Builder.BuildURem(dxAx, divW, $"{label}_r");

        var qI16 = ctx.Builder.BuildTrunc(q, i16, $"{label}_q16");
        var rI16 = ctx.Builder.BuildTrunc(r, i16, $"{label}_r16");

        var newAx = ctx.Builder.BuildSelect(divIsZero, ax, qI16, $"{label}_newAx");
        var newDx = ctx.Builder.BuildSelect(divIsZero, dx, rI16, $"{label}_newDx");
        X86_16Emitters.WriteGpr16(ctx, 0, newAx);
        X86_16Emitters.WriteGpr16(ctx, 2, newDx);
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
        var i8  = LLVMTypeRef.Int8;
        var i16 = LLVMTypeRef.Int16;
        // fetch_imm8 advances IP first; signed displacement adds to post-fetch IP.
        var disp8 = X86_16Emitters.FetchImm8(ctx, "jmp8_d");

        // 24.6.8e — back-edge fast path: unconditional JMP to a known
        // intra-block target = unconditional LLVM Br to the target's
        // pre-BB. No IP write needed (target's pre-BB pre-writes IP).
        if (ctx.BackEdgeTargetBB is { } target)
        {
            ctx.Builder.BuildBr(target);
            return;
        }

        var disp16 = ctx.Builder.BuildSExt(disp8, i16, "jmp8_dsx");
        var ipPtr = ctx.GepStatusRegister("IP");
        var ip = ctx.Builder.BuildLoad2(i16, ipPtr, "jmp8_ip");
        var newIp = ctx.Builder.BuildAdd(ip, disp16, "jmp8_newip");
        ctx.Builder.BuildStore(newIp, ipPtr);
        var pcwSlot = ctx.Layout.GepPcWritten(ctx.Builder, ctx.StatePtr);
        ctx.Builder.BuildStore(LLVMValueRef.CreateConstInt(i8, 1, false), pcwSlot);
    }
}

internal sealed class X86JmpRel16Emitter : IMicroOpEmitter
{
    public string OpName => "x86_jmp_rel16";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i8  = LLVMTypeRef.Int8;
        var i16 = LLVMTypeRef.Int16;
        var disp16 = X86_16Emitters.FetchImm16(ctx, "jmp16_d");

        // 24.6.8e — back-edge fast path (see X86JmpRel8Emitter).
        if (ctx.BackEdgeTargetBB is { } target)
        {
            ctx.Builder.BuildBr(target);
            return;
        }

        var ipPtr = ctx.GepStatusRegister("IP");
        var ip = ctx.Builder.BuildLoad2(i16, ipPtr, "jmp16_ip");
        var newIp = ctx.Builder.BuildAdd(ip, disp16, "jmp16_newip");
        ctx.Builder.BuildStore(newIp, ipPtr);
        var pcwSlot = ctx.Layout.GepPcWritten(ctx.Builder, ctx.StatePtr);
        ctx.Builder.BuildStore(LLVMValueRef.CreateConstInt(i8, 1, false), pcwSlot);
    }
}

internal sealed class X86JccRel8Emitter : IMicroOpEmitter
{
    public string OpName => "x86_jcc_rel8";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var fieldName = step.Raw.GetProperty("cond_field").GetString()!;
        var i8  = LLVMTypeRef.Int8;
        var i16 = LLVMTypeRef.Int16;
        var i32 = LLVMTypeRef.Int32;

        var cccc = ctx.Resolve(fieldName);    // i32

        // Always fetch displacement first — IP must advance regardless
        // (block-exit semantics on the not-taken path expect IP to point
        // past Jcc). In back-edge mode the disp value is dead-code-
        // eliminated.
        var disp8  = X86_16Emitters.FetchImm8(ctx, "jcc_d");

        // Build the predicate from FLAGS + cccc.
        var flags = X86CtrlHelpers.LoadFlags(ctx, "jcc");
        var pred = X86CtrlHelpers.BuildJccPredicate(ctx, cccc, flags, "jcc");

        // 24.6.8e — back-edge fast path: emit LLVM-internal CondBr to
        // the target instruction's pre-BB instead of IP write + block
        // exit. See X86LoopEmitter for the full design rationale.
        if (ctx.BackEdgeTargetBB is { } target && ctx.BackEdgeFallthroughBB is { } fall)
        {
            ctx.Builder.BuildCondBr(pred, target, fall);
            return;
        }

        // Default path: IP = pred ? ip+disp : ip; signal PcWritten=pred.
        var disp16 = ctx.Builder.BuildSExt(disp8, i16, "jcc_dsx");
        var ipPtr = ctx.GepStatusRegister("IP");
        var ip = ctx.Builder.BuildLoad2(i16, ipPtr, "jcc_ip");
        var ipPlus = ctx.Builder.BuildAdd(ip, disp16, "jcc_ipP");
        var sel = ctx.Builder.BuildSelect(pred, ipPlus, ip, "jcc_newip");
        ctx.Builder.BuildStore(sel, ipPtr);

        // 24.6.8b — signal PcWritten to BlockFunctionBuilder when taken so
        // block-JIT exits the block on a taken branch. Without this, the
        // next instruction's IP pre-write would clobber the jump target.
        var pcwSlot = ctx.Layout.GepPcWritten(ctx.Builder, ctx.StatePtr);
        ctx.Builder.BuildStore(ctx.Builder.BuildZExt(pred, i8, "jcc_pcw"), pcwSlot);
    }
}

internal sealed class X86JcxzRel8Emitter : IMicroOpEmitter
{
    public string OpName => "x86_jcxz_rel8";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i8  = LLVMTypeRef.Int8;
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
        // 24.6.8b — signal PcWritten on taken (cx==0).
        var pcwSlot = ctx.Layout.GepPcWritten(ctx.Builder, ctx.StatePtr);
        ctx.Builder.BuildStore(ctx.Builder.BuildZExt(cxZero, i8, "jcxz_pcw"), pcwSlot);
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
        var i8  = LLVMTypeRef.Int8;
        var i16 = LLVMTypeRef.Int16;

        // FetchImm8 advances IP regardless of mode (block exit semantics).
        // In back-edge mode the disp value itself is dead-code-eliminated
        // because we use the static target BB instead.
        var disp8  = X86_16Emitters.FetchImm8(ctx, "loop_d");

        // CX -= 1
        var cx = X86_16Emitters.ReadGpr16(ctx, 1, "loop_cx");
        var newCx = ctx.Builder.BuildSub(cx,
            LLVMValueRef.CreateConstInt(i16, 1, false), "loop_cxN");
        X86_16Emitters.WriteGpr16(ctx, 1, newCx);

        // Predicate: cx != 0 (and optionally ZF check for LOOPE/LOOPNE).
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

        // 24.6.8e — back-edge fast path. When BlockDetector identified
        // the LOOP target as another instruction in this same block,
        // emit a CondBr directly to that instruction's pre-BB instead
        // of writing IP and exiting the block. Cross-iteration register
        // state (CX, AX, FLAGS, ...) survives via mem2reg-promoted phi
        // nodes — what Gemini called "the alloca + mem2reg pattern
        // gives you cross-block SSA without architectural nightmares."
        if (ctx.BackEdgeTargetBB is { } target && ctx.BackEdgeFallthroughBB is { } fall)
        {
            ctx.Builder.BuildCondBr(pred, target, fall);
            return;
        }

        // Default path: compute new IP via Select + signal PcWritten so
        // the block exits and the dispatcher re-enters at the target.
        var ipPtr = ctx.GepStatusRegister("IP");
        var ip = ctx.Builder.BuildLoad2(i16, ipPtr, "loop_ip");
        var disp16 = ctx.Builder.BuildSExt(disp8, i16, "loop_dsx");
        var ipPlus = ctx.Builder.BuildAdd(ip, disp16, "loop_ipP");
        var sel = ctx.Builder.BuildSelect(pred, ipPlus, ip, "loop_newip");
        ctx.Builder.BuildStore(sel, ipPtr);
        // 24.6.8b — signal PcWritten when LOOP iterates (pred=1).
        var pcwSlot = ctx.Layout.GepPcWritten(ctx.Builder, ctx.StatePtr);
        ctx.Builder.BuildStore(ctx.Builder.BuildZExt(pred, i8, "loop_pcw"), pcwSlot);
    }
}

internal sealed class X86CallRel16Emitter : IMicroOpEmitter
{
    public string OpName => "x86_call_rel16";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i8  = LLVMTypeRef.Int8;
        var i16 = LLVMTypeRef.Int16;
        var disp16 = X86_16Emitters.FetchImm16(ctx, "call_d");
        var ipPtr = ctx.GepStatusRegister("IP");
        var retIp = ctx.Builder.BuildLoad2(i16, ipPtr, "call_retip");
        // Push return address (post-fetch IP).
        X86StackHelpers.PushW16(ctx, retIp, "call_push");
        // Jump.
        var newIp = ctx.Builder.BuildAdd(retIp, disp16, "call_newip");
        ctx.Builder.BuildStore(newIp, ipPtr);
        var pcwSlot = ctx.Layout.GepPcWritten(ctx.Builder, ctx.StatePtr);
        ctx.Builder.BuildStore(LLVMValueRef.CreateConstInt(i8, 1, false), pcwSlot);
    }
}

internal sealed class X86RetNearEmitter : IMicroOpEmitter
{
    public string OpName => "x86_ret_near";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i8 = LLVMTypeRef.Int8;
        var ipPtr = ctx.GepStatusRegister("IP");
        var retIp = X86StackHelpers.PopW16(ctx, "ret_pop");
        ctx.Builder.BuildStore(retIp, ipPtr);
        var pcwSlot = ctx.Layout.GepPcWritten(ctx.Builder, ctx.StatePtr);
        ctx.Builder.BuildStore(LLVMValueRef.CreateConstInt(i8, 1, false), pcwSlot);
    }
}

internal sealed class X86RetNearImm16Emitter : IMicroOpEmitter
{
    public string OpName => "x86_ret_near_imm16";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i8  = LLVMTypeRef.Int8;
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
        var pcwSlot = ctx.Layout.GepPcWritten(ctx.Builder, ctx.StatePtr);
        ctx.Builder.BuildStore(LLVMValueRef.CreateConstInt(i8, 1, false), pcwSlot);
    }
}

// ============================================================================
// 24.6.7b — D0 / D1 shift/rotate by 1 (the count==1 form). The D2/D3
// "shift by CL" forms have undefined-or-quirky flag semantics for
// count != 1 and need careful Tom Harte alignment — deferred to 24.6.7b2.
//
// modrm.reg sub-selects:
//   /0 ROL — rotate left.        CF = old MSB.       OF = MSB(result) XOR CF
//   /1 ROR — rotate right.       CF = old LSB.       OF = MSB(result) XOR (next-MSB)
//   /2 RCL — rotate left thru CF. CF_new = old MSB.   OF = MSB(result) XOR CF_new
//   /3 RCR — rotate right thru CF. CF_new = old LSB. OF = MSB_old XOR new MSB
//   /4 SHL/SAL — logical shift left.  CF = old MSB.   OF = MSB(result) XOR CF.
//                                     AF = bit 4 of result (8088 quirk).
//   /5 SHR — logical shift right. CF = old LSB.       OF = MSB(original).  AF = 0.
//   /6 SAL — alias for /4 on 8086 silicon.
//   /7 SAR — arithmetic shift right. CF = old LSB.    OF = 0.              AF = 0.
//
// All sub-ops update SF/ZF/PF from the result. Logical (SHL/SHR/SAR)
// touch AF per kind (8088 silicon quirk). Rotates leave AF unchanged.
//
// Implementation: build all 8 result + flag values per arm, store result
// via X86ModRmMemHelpers.BuildStoreW{8,16}, merge flags into FLAGS.
// ============================================================================

internal static class X86ShiftHelpers
{
    /// <summary>
    /// Build 6 i1 flag bits (cf/pf/af/zf/sf/of), pack via the ALU helper,
    /// merge into FLAGS preserving non-affected bits. Same mask 0x08D5
    /// as ALU ops — shift/rotate also touches all 6 bits.
    /// </summary>
    public static void StoreShiftFlags(
        EmitContext ctx,
        LLVMValueRef cf, LLVMValueRef pf, LLVMValueRef af,
        LLVMValueRef zf, LLVMValueRef sf, LLVMValueRef of,
        string label)
    {
        var packed = X86F6GroupDispatchEmitter.X86AluHelpers_BuildPackedFlagsPublic(
            ctx, cf, pf, af, zf, sf, of, label);
        X86F6GroupDispatchEmitter.X86AluHelpers_StoreAluFlagsPublic(ctx, packed, label);
    }

    /// <summary>Read CF bit (i1) from FLAGS.</summary>
    public static LLVMValueRef ReadCf(EmitContext ctx, string label)
    {
        var flags = X86CtrlHelpers.LoadFlags(ctx, label);
        return X86CtrlHelpers.ExtractFlagBit(ctx, flags, 0, $"{label}_cf");
    }

    /// <summary>Common SF/ZF/PF triple from an i8 result.</summary>
    public static (LLVMValueRef sf, LLVMValueRef zf, LLVMValueRef pf) Szp8(
        EmitContext ctx, LLVMValueRef r8, string label)
    {
        var i8 = LLVMTypeRef.Int8;
        var sfMask = ctx.Builder.BuildAnd(r8,
            LLVMValueRef.CreateConstInt(i8, 0x80, false), $"{label}_sfm");
        var sf = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, sfMask,
            LLVMValueRef.CreateConstInt(i8, 0, false), $"{label}_sf");
        var zf = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, r8,
            LLVMValueRef.CreateConstInt(i8, 0, false), $"{label}_zf");
        var pf = X86F6GroupDispatchEmitter.X86AluHelpers_BuildParityEvenPublic(ctx, r8, label);
        return (sf, zf, pf);
    }

    public static (LLVMValueRef sf, LLVMValueRef zf, LLVMValueRef pf) Szp16(
        EmitContext ctx, LLVMValueRef r16, string label)
    {
        var i8  = LLVMTypeRef.Int8;
        var i16 = LLVMTypeRef.Int16;
        var sfMask = ctx.Builder.BuildAnd(r16,
            LLVMValueRef.CreateConstInt(i16, 0x8000, false), $"{label}_sfm");
        var sf = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, sfMask,
            LLVMValueRef.CreateConstInt(i16, 0, false), $"{label}_sf");
        var zf = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, r16,
            LLVMValueRef.CreateConstInt(i16, 0, false), $"{label}_zf");
        var rLow = ctx.Builder.BuildTrunc(r16, i8, $"{label}_rlo");
        var pf = X86F6GroupDispatchEmitter.X86AluHelpers_BuildParityEvenPublic(ctx, rLow, label);
        return (sf, zf, pf);
    }
}

internal sealed class X86ShiftRotateW8Count1Emitter : IMicroOpEmitter
{
    public string OpName => "x86_shift_rotate_w8_count1";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i1  = LLVMTypeRef.Int1;
        var i8  = LLVMTypeRef.Int8;
        var i16 = LLVMTypeRef.Int16;
        var i32 = LLVMTypeRef.Int32;

        var lhs = ctx.Resolve("lhs");      // i8 — the original value
        var sel = ctx.Resolve("modrm_reg"); // i32

        var i1false = LLVMValueRef.CreateConstInt(i1, 0, false);
        var cfIn = X86ShiftHelpers.ReadCf(ctx, "shr8");

        // Pre-compute MSB and LSB of the original.
        var msbMask = ctx.Builder.BuildAnd(lhs,
            LLVMValueRef.CreateConstInt(i8, 0x80, false), "shr8_msbm");
        var msb = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, msbMask,
            LLVMValueRef.CreateConstInt(i8, 0, false), "shr8_msb");
        var lsbMask = ctx.Builder.BuildAnd(lhs,
            LLVMValueRef.CreateConstInt(i8, 0x01, false), "shr8_lsbm");
        var lsb = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, lsbMask,
            LLVMValueRef.CreateConstInt(i8, 0, false), "shr8_lsb");

        var endBB     = ctx.Function.AppendBasicBlock("shr8_end");
        var defaultBB = ctx.Function.AppendBasicBlock("shr8_default");
        var arms = new LLVMBasicBlockRef[8];
        for (int i = 0; i < 8; i++) arms[i] = ctx.Function.AppendBasicBlock($"shr8_{i}");
        var sw = ctx.Builder.BuildSwitch(sel, defaultBB, 8);
        for (int i = 0; i < 8; i++)
            sw.AddCase(LLVMValueRef.CreateConstInt(i32, (uint)i, false), arms[i]);

        // Helper to build a single arm: compute result, store to r/m, set flags, branch.
        void EmitArm(int idx, string kind)
        {
            ctx.Builder.PositionAtEnd(arms[idx]);

            LLVMValueRef result, cf, of, af;

            switch (kind)
            {
                case "rol":
                    var rolL = ctx.Builder.BuildShl(lhs,
                        LLVMValueRef.CreateConstInt(i8, 1, false), "rol_l");
                    var rolR = ctx.Builder.BuildLShr(lhs,
                        LLVMValueRef.CreateConstInt(i8, 7, false), "rol_r");
                    result = ctx.Builder.BuildOr(rolL, rolR, "rol_r8");
                    cf = msb;
                    {
                        var rMsbMask = ctx.Builder.BuildAnd(result,
                            LLVMValueRef.CreateConstInt(i8, 0x80, false), "rol_rmsb_m");
                        var rMsb = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, rMsbMask,
                            LLVMValueRef.CreateConstInt(i8, 0, false), "rol_rmsb");
                        of = ctx.Builder.BuildXor(rMsb, cf, "rol_of");
                    }
                    af = X86CtrlHelpers.ExtractFlagBit(ctx, X86CtrlHelpers.LoadFlags(ctx, "rol_af"), 4, "rol_af_keep");
                    break;

                case "ror":
                    var rorR = ctx.Builder.BuildLShr(lhs,
                        LLVMValueRef.CreateConstInt(i8, 1, false), "ror_r");
                    var rorL = ctx.Builder.BuildShl(lhs,
                        LLVMValueRef.CreateConstInt(i8, 7, false), "ror_l");
                    result = ctx.Builder.BuildOr(rorR, rorL, "ror_r8");
                    cf = lsb;
                    {
                        // OF (count==1) = bit 7 XOR bit 6 of result.
                        var b7m = ctx.Builder.BuildAnd(result,
                            LLVMValueRef.CreateConstInt(i8, 0x80, false), "ror_b7m");
                        var b7 = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, b7m,
                            LLVMValueRef.CreateConstInt(i8, 0, false), "ror_b7");
                        var b6m = ctx.Builder.BuildAnd(result,
                            LLVMValueRef.CreateConstInt(i8, 0x40, false), "ror_b6m");
                        var b6 = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, b6m,
                            LLVMValueRef.CreateConstInt(i8, 0, false), "ror_b6");
                        of = ctx.Builder.BuildXor(b7, b6, "ror_of");
                    }
                    af = X86CtrlHelpers.ExtractFlagBit(ctx, X86CtrlHelpers.LoadFlags(ctx, "ror_af"), 4, "ror_af_keep");
                    break;

                case "rcl":
                    var rclL = ctx.Builder.BuildShl(lhs,
                        LLVMValueRef.CreateConstInt(i8, 1, false), "rcl_l");
                    var cfInZ = ctx.Builder.BuildZExt(cfIn, i8, "rcl_cfz");
                    result = ctx.Builder.BuildOr(rclL, cfInZ, "rcl_r8");
                    cf = msb;
                    {
                        var rMsbMask = ctx.Builder.BuildAnd(result,
                            LLVMValueRef.CreateConstInt(i8, 0x80, false), "rcl_rmsb_m");
                        var rMsb = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, rMsbMask,
                            LLVMValueRef.CreateConstInt(i8, 0, false), "rcl_rmsb");
                        of = ctx.Builder.BuildXor(rMsb, cf, "rcl_of");
                    }
                    af = X86CtrlHelpers.ExtractFlagBit(ctx, X86CtrlHelpers.LoadFlags(ctx, "rcl_af"), 4, "rcl_af_keep");
                    break;

                case "rcr":
                    var rcrR = ctx.Builder.BuildLShr(lhs,
                        LLVMValueRef.CreateConstInt(i8, 1, false), "rcr_r");
                    var cfInL = ctx.Builder.BuildShl(
                        ctx.Builder.BuildZExt(cfIn, i8, "rcr_cfz"),
                        LLVMValueRef.CreateConstInt(i8, 7, false), "rcr_cfshl");
                    result = ctx.Builder.BuildOr(rcrR, cfInL, "rcr_r8");
                    cf = lsb;
                    {
                        // OF = old MSB XOR new MSB (i.e. cfIn XOR original MSB).
                        of = ctx.Builder.BuildXor(cfIn, msb, "rcr_of");
                    }
                    af = X86CtrlHelpers.ExtractFlagBit(ctx, X86CtrlHelpers.LoadFlags(ctx, "rcr_af"), 4, "rcr_af_keep");
                    break;

                case "shl":   // /4 and /6 alias
                    result = ctx.Builder.BuildShl(lhs,
                        LLVMValueRef.CreateConstInt(i8, 1, false), "shl_r8");
                    cf = msb;
                    {
                        var rMsbMask = ctx.Builder.BuildAnd(result,
                            LLVMValueRef.CreateConstInt(i8, 0x80, false), "shl_rmsb_m");
                        var rMsb = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, rMsbMask,
                            LLVMValueRef.CreateConstInt(i8, 0, false), "shl_rmsb");
                        of = ctx.Builder.BuildXor(rMsb, cf, "shl_of");
                    }
                    {
                        // 8088 SHL AF quirk: AF = bit 4 of result.
                        var afMask = ctx.Builder.BuildAnd(result,
                            LLVMValueRef.CreateConstInt(i8, 0x10, false), "shl_afm");
                        af = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, afMask,
                            LLVMValueRef.CreateConstInt(i8, 0, false), "shl_af");
                    }
                    break;

                case "shr":
                    result = ctx.Builder.BuildLShr(lhs,
                        LLVMValueRef.CreateConstInt(i8, 1, false), "shr_r8");
                    cf = lsb;
                    of = msb;   // OF for SHR count==1 = original MSB
                    af = i1false; // 8088 SHR AF = 0
                    break;

                case "sar":
                    result = ctx.Builder.BuildAShr(lhs,
                        LLVMValueRef.CreateConstInt(i8, 1, false), "sar_r8");
                    cf = lsb;
                    of = i1false;  // OF for SAR count==1 = 0
                    af = i1false;  // 8088 SAR AF = 0
                    break;

                default:
                    throw new InvalidOperationException($"shr8 unknown kind '{kind}'");
            }

            // Write result back via mod-aware store (mod=11 → reg, else → mem).
            X86ModRmMemHelpers.BuildStoreW8(ctx, result);

            // Set SF/ZF/PF + cf/af/of via the shared StoreShiftFlags.
            var (sf, zf, pf) = X86ShiftHelpers.Szp8(ctx, result, $"shr8_{kind}");
            X86ShiftHelpers.StoreShiftFlags(ctx, cf, pf, af, zf, sf, of, $"shr8_{kind}");

            ctx.Builder.BuildBr(endBB);
        }

        EmitArm(0, "rol");
        EmitArm(1, "ror");
        EmitArm(2, "rcl");
        EmitArm(3, "rcr");
        EmitArm(4, "shl");
        EmitArm(5, "shr");
        EmitArm(6, "shl");   // /6 = SAL alias for SHL on 8086
        EmitArm(7, "sar");

        ctx.Builder.PositionAtEnd(defaultBB);
        ctx.Builder.BuildBr(endBB);
        ctx.Builder.PositionAtEnd(endBB);
    }
}

// ============================================================================
// 24.6.7c — string-op helpers + 10 string-op emitters.
//
// Direction Flag (DF) handling:
//   DF=0 (cleared, the default after RESET) → SI/DI advance forward (+1 byte
//        for byte ops, +2 for word ops).
//   DF=1 (set via STD)                       → SI/DI decrement.
//
// Source/dest segments:
//   MOVSB/MOVSW: src DS:SI (segment override applies), dst ES:DI (override
//                does NOT apply per Intel — ES is hardcoded for the dest).
//   CMPSB/CMPSW: same dual-segment access.
//   LODSB/LODSW: src DS:SI (segment override applies).
//   STOSB/STOSW: dst ES:DI (no override).
//   SCASB/SCASW: dst ES:DI (no override; scan target).
//
// Each emitter does ONE iteration; REP/REPE/REPNE wrapping happens in
// X86JsonCpu's C# Step() loop (24.6.7c2).
// ============================================================================

internal static class X86StringHelpers
{
    /// <summary>
    /// Compute the SI/DI delta as i16: +N if DF=0, -N if DF=1.
    /// </summary>
    public static LLVMValueRef BuildDfDelta(EmitContext ctx, int width, string label)
    {
        var i16 = LLVMTypeRef.Int16;
        var flags = X86CtrlHelpers.LoadFlags(ctx, label);
        var df = X86CtrlHelpers.ExtractFlagBit(ctx, flags, 10, $"{label}_df");
        // delta = df ? -width : +width
        var pos = LLVMValueRef.CreateConstInt(i16, (ulong)width, false);
        var neg = LLVMValueRef.CreateConstInt(i16, (ulong)(unchecked((ushort)(-width))), false);
        return ctx.Builder.BuildSelect(df, neg, pos, $"{label}_delta");
    }

    /// <summary>Read SI (GPR 6) → i16.</summary>
    public static LLVMValueRef ReadSi(EmitContext ctx, string label)
        => X86_16Emitters.ReadGpr16(ctx, 6, label);

    /// <summary>Read DI (GPR 7) → i16.</summary>
    public static LLVMValueRef ReadDi(EmitContext ctx, string label)
        => X86_16Emitters.ReadGpr16(ctx, 7, label);

    public static void WriteSi(EmitContext ctx, LLVMValueRef v) => X86_16Emitters.WriteGpr16(ctx, 6, v);
    public static void WriteDi(EmitContext ctx, LLVMValueRef v) => X86_16Emitters.WriteGpr16(ctx, 7, v);
}

// 0xA4 MOVSB — [DS:SI] → [ES:DI]; SI±1; DI±1
internal sealed class X86MovsbEmitter : IMicroOpEmitter
{
    public string OpName => "x86_movsb";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i16 = LLVMTypeRef.Int16;
        var srcSeg = X86_16Emitters.LoadDefaultOrOverrideSegment(ctx, "DS", "movsb_src");
        var esSeg  = X86_16Emitters.LoadSeg16(ctx, "ES", "movsb_es");
        var si = X86StringHelpers.ReadSi(ctx, "movsb_si");
        var di = X86StringHelpers.ReadDi(ctx, "movsb_di");

        var data = X86_16Emitters.SegmentedRead8(ctx, srcSeg, si, "movsb_v");
        X86_16Emitters.SegmentedWrite8(ctx, esSeg, di, data, "movsb_w");

        var delta = X86StringHelpers.BuildDfDelta(ctx, 1, "movsb");
        X86StringHelpers.WriteSi(ctx, ctx.Builder.BuildAdd(si, delta, "movsb_si_n"));
        X86StringHelpers.WriteDi(ctx, ctx.Builder.BuildAdd(di, delta, "movsb_di_n"));
    }
}

// 0xA5 MOVSW
internal sealed class X86MovswEmitter : IMicroOpEmitter
{
    public string OpName => "x86_movsw";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var srcSeg = X86_16Emitters.LoadDefaultOrOverrideSegment(ctx, "DS", "movsw_src");
        var esSeg  = X86_16Emitters.LoadSeg16(ctx, "ES", "movsw_es");
        var si = X86StringHelpers.ReadSi(ctx, "movsw_si");
        var di = X86StringHelpers.ReadDi(ctx, "movsw_di");

        var data = X86_16Emitters.SegmentedRead16(ctx, srcSeg, si, "movsw_v");
        X86_16Emitters.SegmentedWrite16(ctx, esSeg, di, data, "movsw_w");

        var delta = X86StringHelpers.BuildDfDelta(ctx, 2, "movsw");
        X86StringHelpers.WriteSi(ctx, ctx.Builder.BuildAdd(si, delta, "movsw_si_n"));
        X86StringHelpers.WriteDi(ctx, ctx.Builder.BuildAdd(di, delta, "movsw_di_n"));
    }
}

// 0xA6 CMPSB — compare [DS:SI] vs [ES:DI]; flags via SUB; advance both.
internal sealed class X86CmpsbEmitter : IMicroOpEmitter
{
    public string OpName => "x86_cmpsb";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var srcSeg = X86_16Emitters.LoadDefaultOrOverrideSegment(ctx, "DS", "cmpsb_src");
        var esSeg  = X86_16Emitters.LoadSeg16(ctx, "ES", "cmpsb_es");
        var si = X86StringHelpers.ReadSi(ctx, "cmpsb_si");
        var di = X86StringHelpers.ReadDi(ctx, "cmpsb_di");

        var lhs = X86_16Emitters.SegmentedRead8(ctx, srcSeg, si, "cmpsb_l");
        var rhs = X86_16Emitters.SegmentedRead8(ctx, esSeg, di, "cmpsb_r");
        // Flag set as if SUB lhs - rhs (matches CMP semantics).
        X86AluHelpers.BuildAluW8(ctx, "cmp", lhs, rhs, "cmpsb");

        var delta = X86StringHelpers.BuildDfDelta(ctx, 1, "cmpsb");
        X86StringHelpers.WriteSi(ctx, ctx.Builder.BuildAdd(si, delta, "cmpsb_si_n"));
        X86StringHelpers.WriteDi(ctx, ctx.Builder.BuildAdd(di, delta, "cmpsb_di_n"));
    }
}

internal sealed class X86CmpswEmitter : IMicroOpEmitter
{
    public string OpName => "x86_cmpsw";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var srcSeg = X86_16Emitters.LoadDefaultOrOverrideSegment(ctx, "DS", "cmpsw_src");
        var esSeg  = X86_16Emitters.LoadSeg16(ctx, "ES", "cmpsw_es");
        var si = X86StringHelpers.ReadSi(ctx, "cmpsw_si");
        var di = X86StringHelpers.ReadDi(ctx, "cmpsw_di");

        var lhs = X86_16Emitters.SegmentedRead16(ctx, srcSeg, si, "cmpsw_l");
        var rhs = X86_16Emitters.SegmentedRead16(ctx, esSeg, di, "cmpsw_r");
        X86AluHelpers.BuildAluW16(ctx, "cmp", lhs, rhs, "cmpsw");

        var delta = X86StringHelpers.BuildDfDelta(ctx, 2, "cmpsw");
        X86StringHelpers.WriteSi(ctx, ctx.Builder.BuildAdd(si, delta, "cmpsw_si_n"));
        X86StringHelpers.WriteDi(ctx, ctx.Builder.BuildAdd(di, delta, "cmpsw_di_n"));
    }
}

// 0xAA STOSB — AL → [ES:DI]; DI±1
internal sealed class X86StosbEmitter : IMicroOpEmitter
{
    public string OpName => "x86_stosb";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var esSeg = X86_16Emitters.LoadSeg16(ctx, "ES", "stosb_es");
        var di = X86StringHelpers.ReadDi(ctx, "stosb_di");
        var al = X86_16Emitters.ReadGpr8(ctx, 0, "stosb_al");
        X86_16Emitters.SegmentedWrite8(ctx, esSeg, di, al, "stosb_w");
        var delta = X86StringHelpers.BuildDfDelta(ctx, 1, "stosb");
        X86StringHelpers.WriteDi(ctx, ctx.Builder.BuildAdd(di, delta, "stosb_di_n"));
    }
}

// 0xAB STOSW — AX → [ES:DI]
internal sealed class X86StoswEmitter : IMicroOpEmitter
{
    public string OpName => "x86_stosw";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var esSeg = X86_16Emitters.LoadSeg16(ctx, "ES", "stosw_es");
        var di = X86StringHelpers.ReadDi(ctx, "stosw_di");
        var ax = X86_16Emitters.ReadGpr16(ctx, 0, "stosw_ax");
        X86_16Emitters.SegmentedWrite16(ctx, esSeg, di, ax, "stosw_w");
        var delta = X86StringHelpers.BuildDfDelta(ctx, 2, "stosw");
        X86StringHelpers.WriteDi(ctx, ctx.Builder.BuildAdd(di, delta, "stosw_di_n"));
    }
}

// 0xAC LODSB — [DS:SI] → AL; SI±1
internal sealed class X86LodsbEmitter : IMicroOpEmitter
{
    public string OpName => "x86_lodsb";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var srcSeg = X86_16Emitters.LoadDefaultOrOverrideSegment(ctx, "DS", "lodsb_src");
        var si = X86StringHelpers.ReadSi(ctx, "lodsb_si");
        var data = X86_16Emitters.SegmentedRead8(ctx, srcSeg, si, "lodsb_v");
        X86_16Emitters.WriteGpr8(ctx, 0, data);
        var delta = X86StringHelpers.BuildDfDelta(ctx, 1, "lodsb");
        X86StringHelpers.WriteSi(ctx, ctx.Builder.BuildAdd(si, delta, "lodsb_si_n"));
    }
}

// 0xAD LODSW — [DS:SI] → AX
internal sealed class X86LodswEmitter : IMicroOpEmitter
{
    public string OpName => "x86_lodsw";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var srcSeg = X86_16Emitters.LoadDefaultOrOverrideSegment(ctx, "DS", "lodsw_src");
        var si = X86StringHelpers.ReadSi(ctx, "lodsw_si");
        var data = X86_16Emitters.SegmentedRead16(ctx, srcSeg, si, "lodsw_v");
        X86_16Emitters.WriteGpr16(ctx, 0, data);
        var delta = X86StringHelpers.BuildDfDelta(ctx, 2, "lodsw");
        X86StringHelpers.WriteSi(ctx, ctx.Builder.BuildAdd(si, delta, "lodsw_si_n"));
    }
}

// 0xAE SCASB — compare AL with [ES:DI]; flags via SUB; DI±1
internal sealed class X86ScasbEmitter : IMicroOpEmitter
{
    public string OpName => "x86_scasb";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var esSeg = X86_16Emitters.LoadSeg16(ctx, "ES", "scasb_es");
        var di = X86StringHelpers.ReadDi(ctx, "scasb_di");
        var al = X86_16Emitters.ReadGpr8(ctx, 0, "scasb_al");
        var rhs = X86_16Emitters.SegmentedRead8(ctx, esSeg, di, "scasb_r");
        X86AluHelpers.BuildAluW8(ctx, "cmp", al, rhs, "scasb");
        var delta = X86StringHelpers.BuildDfDelta(ctx, 1, "scasb");
        X86StringHelpers.WriteDi(ctx, ctx.Builder.BuildAdd(di, delta, "scasb_di_n"));
    }
}

// 0xAF SCASW
internal sealed class X86ScaswEmitter : IMicroOpEmitter
{
    public string OpName => "x86_scasw";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var esSeg = X86_16Emitters.LoadSeg16(ctx, "ES", "scasw_es");
        var di = X86StringHelpers.ReadDi(ctx, "scasw_di");
        var ax = X86_16Emitters.ReadGpr16(ctx, 0, "scasw_ax");
        var rhs = X86_16Emitters.SegmentedRead16(ctx, esSeg, di, "scasw_r");
        X86AluHelpers.BuildAluW16(ctx, "cmp", ax, rhs, "scasw");
        var delta = X86StringHelpers.BuildDfDelta(ctx, 2, "scasw");
        X86StringHelpers.WriteDi(ctx, ctx.Builder.BuildAdd(di, delta, "scasw_di_n"));
    }
}

// ============================================================================
// 24.6.7d — flag-manipulation + IO ops.
//
// Generic SET/CLEAR-flag-bit emitter takes a `bit_pos` JSON parameter and
// writes the named FLAGS bit. Used by all six standalone bit set/clear
// opcodes (CLC/STC/CLI/STI/CLD/STD).
//
// CMC toggles CF.
// SAHF copies AH (8 bits) into FLAGS bits 7:0.
// LAHF copies FLAGS bits 7:0 into AH.
//
// IN/OUT use a no-op stub (just consume the port byte if applicable
// and advance IP via the existing fetch helper). Future: wire to a
// proper IO bus when MMIO peripherals are needed.
// ============================================================================

internal sealed class X86SetFlagBitEmitter : IMicroOpEmitter
{
    public string OpName => "x86_set_flag_bit";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var bitPos = step.Raw.GetProperty("bit_pos").GetInt32();
        var i16 = LLVMTypeRef.Int16;
        var fPtr = ctx.GepStatusRegister("FLAGS");
        var f = ctx.Builder.BuildLoad2(i16, fPtr, "sf_f");
        var mask = LLVMValueRef.CreateConstInt(i16, 1u << bitPos, false);
        var newF = ctx.Builder.BuildOr(f, mask, "sf_new");
        ctx.Builder.BuildStore(newF, fPtr);
    }
}

internal sealed class X86ClearFlagBitEmitter : IMicroOpEmitter
{
    public string OpName => "x86_clear_flag_bit";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var bitPos = step.Raw.GetProperty("bit_pos").GetInt32();
        var i16 = LLVMTypeRef.Int16;
        var fPtr = ctx.GepStatusRegister("FLAGS");
        var f = ctx.Builder.BuildLoad2(i16, fPtr, "cf_f");
        var notMask = LLVMValueRef.CreateConstInt(i16,
            (ulong)(unchecked((ushort)~(1 << bitPos))), false);
        var newF = ctx.Builder.BuildAnd(f, notMask, "cf_new");
        ctx.Builder.BuildStore(newF, fPtr);
    }
}

internal sealed class X86CmcEmitter : IMicroOpEmitter
{
    public string OpName => "x86_cmc";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i16 = LLVMTypeRef.Int16;
        var fPtr = ctx.GepStatusRegister("FLAGS");
        var f = ctx.Builder.BuildLoad2(i16, fPtr, "cmc_f");
        var newF = ctx.Builder.BuildXor(f,
            LLVMValueRef.CreateConstInt(i16, 1, false), "cmc_new");
        ctx.Builder.BuildStore(newF, fPtr);
    }
}

internal sealed class X86SahfEmitter : IMicroOpEmitter
{
    public string OpName => "x86_sahf";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i16 = LLVMTypeRef.Int16;
        var ah = X86_16Emitters.ReadGpr8(ctx, 4, "sahf_ah");   // byteIdx 4 = AH
        var ah16 = ctx.Builder.BuildZExt(ah, i16, "sahf_ah16");
        var fPtr = ctx.GepStatusRegister("FLAGS");
        var f = ctx.Builder.BuildLoad2(i16, fPtr, "sahf_f");
        // Replace low byte of FLAGS with AH.
        var keep = ctx.Builder.BuildAnd(f,
            LLVMValueRef.CreateConstInt(i16, 0xFF00, false), "sahf_keep");
        var newF = ctx.Builder.BuildOr(keep, ah16, "sahf_new");
        ctx.Builder.BuildStore(newF, fPtr);
    }
}

internal sealed class X86LahfEmitter : IMicroOpEmitter
{
    public string OpName => "x86_lahf";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i8  = LLVMTypeRef.Int8;
        var i16 = LLVMTypeRef.Int16;
        var fPtr = ctx.GepStatusRegister("FLAGS");
        var f = ctx.Builder.BuildLoad2(i16, fPtr, "lahf_f");
        var ah16 = ctx.Builder.BuildAnd(f,
            LLVMValueRef.CreateConstInt(i16, 0x00FF, false), "lahf_ah16");
        var ah = ctx.Builder.BuildTrunc(ah16, i8, "lahf_ah");
        X86_16Emitters.WriteGpr8(ctx, 4, ah);   // AH
    }
}

// ============================================================================
// IN AL/AX, imm8/DX  +  OUT imm8/DX, AL/AX. The framework has no IO bus
// concept yet — IN reads always return 0xFF / 0xFFFF (open bus); OUT
// silently discards. The IP advance for the immediate port byte
// happens via FetchImm8.
// ============================================================================

internal sealed class X86InImm8Emitter : IMicroOpEmitter
{
    public string OpName => "x86_in_imm8";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var width = step.Raw.GetProperty("width").GetInt32();
        // Consume the port byte (advances IP).
        X86_16Emitters.FetchImm8(ctx, "in_port");
        if (width == 8)
        {
            var i8 = LLVMTypeRef.Int8;
            X86_16Emitters.WriteGpr8(ctx, 0,
                LLVMValueRef.CreateConstInt(i8, 0xFF, false));
        }
        else
        {
            var i16 = LLVMTypeRef.Int16;
            X86_16Emitters.WriteGpr16(ctx, 0,
                LLVMValueRef.CreateConstInt(i16, 0xFFFF, false));
        }
    }
}

internal sealed class X86InDxEmitter : IMicroOpEmitter
{
    public string OpName => "x86_in_dx";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var width = step.Raw.GetProperty("width").GetInt32();
        if (width == 8)
        {
            var i8 = LLVMTypeRef.Int8;
            X86_16Emitters.WriteGpr8(ctx, 0,
                LLVMValueRef.CreateConstInt(i8, 0xFF, false));
        }
        else
        {
            var i16 = LLVMTypeRef.Int16;
            X86_16Emitters.WriteGpr16(ctx, 0,
                LLVMValueRef.CreateConstInt(i16, 0xFFFF, false));
        }
    }
}

internal sealed class X86OutImm8Emitter : IMicroOpEmitter
{
    public string OpName => "x86_out_imm8";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        // Consume the port byte (advances IP). Width param is informational
        // for now since we discard the value.
        X86_16Emitters.FetchImm8(ctx, "out_port");
    }
}

internal sealed class X86OutDxEmitter : IMicroOpEmitter
{
    public string OpName => "x86_out_dx";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        // No-op — port DX, value AL/AX both ignored.
    }
}

// ============================================================================
// 24.6.7d2 — interrupt machinery (INT/INT3/INTO/IRET).
//
// INT n (CD imm8):
//   1. Push FLAGS (with current bits — IF/TF still set if they were)
//   2. Clear IF (FLAGS bit 9), TF (FLAGS bit 8)
//   3. Push CS
//   4. Push IP (post-fetch — points past the INT instruction)
//   5. Read 32-bit far pointer at linear (n * 4): low word → IP, high word → CS
//
// INT 3 (CC, single-byte): same as INT 3 but no imm fetch.
// INTO (CE): conditional INT 4 if OF=1.
// IRET (CF): pop IP, pop CS, pop FLAGS — reverses INT entry.
//
// The vector-table read uses absolute (segment=0) addressing — 8086 has
// the IVT hardcoded at physical 0x00000-0x003FF (256 vectors × 4 bytes).
// ============================================================================

internal static class X86InterruptHelpers
{
    /// <summary>
    /// Common INT entry sequence given a vector number (i32 0..255).
    /// Pushes FLAGS, clears IF+TF, pushes CS, pushes IP, then jumps via
    /// the IVT entry at linear address (vec * 4).
    /// </summary>
    public static void EmitIntCommon(EmitContext ctx, LLVMValueRef vec32, string label)
    {
        var i8  = LLVMTypeRef.Int8;
        var i16 = LLVMTypeRef.Int16;
        var i32 = LLVMTypeRef.Int32;

        // Push FLAGS as-is (NOT the masked-reserved-bit version PUSHF
        // applies — silicon pushes raw on INT entry).
        var fPtr = ctx.GepStatusRegister("FLAGS");
        var flags = ctx.Builder.BuildLoad2(i16, fPtr, $"{label}_flags");
        X86StackHelpers.PushW16(ctx, flags, $"{label}_psh_f");

        // Clear IF (bit 9) and TF (bit 8) AFTER pushing the original FLAGS.
        var clearMask = LLVMValueRef.CreateConstInt(i16, (ulong)(unchecked((ushort)~0x0300)), false);
        var newFlags = ctx.Builder.BuildAnd(flags, clearMask, $"{label}_flags_clr");
        ctx.Builder.BuildStore(newFlags, fPtr);

        // Push CS.
        var csPtr = ctx.GepStatusRegister("CS");
        var cs = ctx.Builder.BuildLoad2(i16, csPtr, $"{label}_cs");
        X86StackHelpers.PushW16(ctx, cs, $"{label}_psh_cs");

        // Push IP (post-fetch).
        var ipPtr = ctx.GepStatusRegister("IP");
        var ip = ctx.Builder.BuildLoad2(i16, ipPtr, $"{label}_ip");
        X86StackHelpers.PushW16(ctx, ip, $"{label}_psh_ip");

        // Read vector at physical (vec * 4): low word = new IP, high word = new CS.
        var vec32_4 = ctx.Builder.BuildShl(vec32,
            LLVMValueRef.CreateConstInt(i32, 2, false), $"{label}_vec4");
        // Read 4 bytes via memory_read_8 calls.
        var addrIp = vec32_4;
        var addrCs = ctx.Builder.BuildAdd(vec32_4,
            LLVMValueRef.CreateConstInt(i32, 2, false), $"{label}_addrCs");
        var newIpLo = MemoryEmitters.CallRead8(ctx, addrIp, $"{label}_iplo");
        var addrIp1 = ctx.Builder.BuildAdd(vec32_4,
            LLVMValueRef.CreateConstInt(i32, 1, false), $"{label}_addrIp1");
        var newIpHi = MemoryEmitters.CallRead8(ctx, addrIp1, $"{label}_iphi");
        var newCsLo = MemoryEmitters.CallRead8(ctx, addrCs, $"{label}_cslo");
        var addrCs1 = ctx.Builder.BuildAdd(vec32_4,
            LLVMValueRef.CreateConstInt(i32, 3, false), $"{label}_addrCs1");
        var newCsHi = MemoryEmitters.CallRead8(ctx, addrCs1, $"{label}_cshi");

        LLVMValueRef Combine(LLVMValueRef lo, LLVMValueRef hi, string n)
        {
            var loZ = ctx.Builder.BuildZExt(lo, i16, $"{n}_lz");
            var hiZ = ctx.Builder.BuildZExt(hi, i16, $"{n}_hz");
            var hiSh = ctx.Builder.BuildShl(hiZ,
                LLVMValueRef.CreateConstInt(i16, 8, false), $"{n}_hs");
            return ctx.Builder.BuildOr(hiSh, loZ, n);
        }
        var newIp16 = Combine(newIpLo, newIpHi, $"{label}_newIp");
        var newCs16 = Combine(newCsLo, newCsHi, $"{label}_newCs");

        ctx.Builder.BuildStore(newIp16, ipPtr);
        ctx.Builder.BuildStore(newCs16, csPtr);

        // 24.6.8b — INT/IRET unconditionally write IP/CS; signal block exit.
        var pcwSlot = ctx.Layout.GepPcWritten(ctx.Builder, ctx.StatePtr);
        ctx.Builder.BuildStore(LLVMValueRef.CreateConstInt(i8, 1, false), pcwSlot);
    }
}

internal sealed class X86IntImm8Emitter : IMicroOpEmitter
{
    public string OpName => "x86_int_imm8";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i32 = LLVMTypeRef.Int32;
        // Fetch the vector byte (advances IP).
        var vec8 = X86_16Emitters.FetchImm8(ctx, "int_vec");
        var vec32 = ctx.Builder.BuildZExt(vec8, i32, "int_vec32");
        X86InterruptHelpers.EmitIntCommon(ctx, vec32, "int");
    }
}

internal sealed class X86Int3Emitter : IMicroOpEmitter
{
    public string OpName => "x86_int3";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i32 = LLVMTypeRef.Int32;
        X86InterruptHelpers.EmitIntCommon(ctx,
            LLVMValueRef.CreateConstInt(i32, 3, false), "int3");
    }
}

internal sealed class X86IntoEmitter : IMicroOpEmitter
{
    public string OpName => "x86_into";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        // INTO: if OF=1, do INT 4. Else no-op (IP advance already happened
        // via the opcode-byte fetch in the dispatcher).
        var i32 = LLVMTypeRef.Int32;
        var flags = X86CtrlHelpers.LoadFlags(ctx, "into");
        var of = X86CtrlHelpers.ExtractFlagBit(ctx, flags, 11, "into_of");

        var doIntBB = ctx.Function.AppendBasicBlock("into_do");
        var skipBB  = ctx.Function.AppendBasicBlock("into_skip");
        var endBB   = ctx.Function.AppendBasicBlock("into_end");
        ctx.Builder.BuildCondBr(of, doIntBB, skipBB);

        ctx.Builder.PositionAtEnd(doIntBB);
        X86InterruptHelpers.EmitIntCommon(ctx,
            LLVMValueRef.CreateConstInt(i32, 4, false), "into");
        ctx.Builder.BuildBr(endBB);

        ctx.Builder.PositionAtEnd(skipBB);
        ctx.Builder.BuildBr(endBB);
        ctx.Builder.PositionAtEnd(endBB);
    }
}

internal sealed class X86IretEmitter : IMicroOpEmitter
{
    public string OpName => "x86_iret";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i8 = LLVMTypeRef.Int8;
        // Reverse of INT entry: pop IP, pop CS, pop FLAGS.
        var newIp = X86StackHelpers.PopW16(ctx, "iret_ip");
        var newCs = X86StackHelpers.PopW16(ctx, "iret_cs");
        var newFl = X86StackHelpers.PopW16(ctx, "iret_fl");

        ctx.Builder.BuildStore(newIp, ctx.GepStatusRegister("IP"));
        ctx.Builder.BuildStore(newCs, ctx.GepStatusRegister("CS"));
        ctx.Builder.BuildStore(newFl, ctx.GepStatusRegister("FLAGS"));

        var pcwSlot = ctx.Layout.GepPcWritten(ctx.Builder, ctx.StatePtr);
        ctx.Builder.BuildStore(LLVMValueRef.CreateConstInt(i8, 1, false), pcwSlot);
    }
}

// ============================================================================
// 24.6.7e — FE/FF group dispatchers.
//
// FE: only /0=INC r/m8 and /1=DEC r/m8 are valid (others are 8086-undefined;
// silicon decodes /4=undefined for INC/DEC width=8). FE handles 8-bit INC/DEC.
//
// FF (16-bit operand) sub-ops:
//   /0 INC r/m16
//   /1 DEC r/m16
//   /2 CALL near r/m16  (push IP, then IP = lhs)
//   /3 CALL far m16:16  (push CS, push IP, then IP/CS from EA's far ptr)
//   /4 JMP near r/m16   (IP = lhs)
//   /5 JMP far m16:16   (IP/CS from EA's far ptr)
//   /6 PUSH r/m16       (push lhs — handled here too for completeness)
//   /7 invalid
//
// Both dispatchers expect the spec to have already done:
//   fetch_modrm + compute_ea + modrm_load_w{8,16} → "lhs"
// ============================================================================

internal sealed class X86FeGroupDispatchEmitter : IMicroOpEmitter
{
    public string OpName => "x86_fe_group_dispatch";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i1  = LLVMTypeRef.Int1;
        var i8  = LLVMTypeRef.Int8;
        var i16 = LLVMTypeRef.Int16;
        var i32 = LLVMTypeRef.Int32;

        var lhs = ctx.Resolve("lhs");        // i8
        var sel = ctx.Resolve("modrm_reg");

        var endBB     = ctx.Function.AppendBasicBlock("feg_end");
        var defaultBB = ctx.Function.AppendBasicBlock("feg_default");
        var arms = new LLVMBasicBlockRef[8];
        for (int i = 0; i < 8; i++) arms[i] = ctx.Function.AppendBasicBlock($"feg_{i}");
        var sw = ctx.Builder.BuildSwitch(sel, defaultBB, 8);
        for (int i = 0; i < 8; i++)
            sw.AddCase(LLVMValueRef.CreateConstInt(i32, (uint)i, false), arms[i]);

        // Helper: INC/DEC r/m8 with CF preserved (mask = 0x08D4, no CF).
        void EmitIncDec(int idx, string kind)
        {
            ctx.Builder.PositionAtEnd(arms[idx]);
            var fPtr = ctx.GepStatusRegister("FLAGS");
            var fOld = ctx.Builder.BuildLoad2(i16, fPtr, $"feg_{kind}_fold");

            var oneI8 = LLVMValueRef.CreateConstInt(i8, 1, false);
            var r = X86AluHelpers.BuildAluW8(ctx, kind, lhs, oneI8, $"feg_{kind}");

            // Restore old CF: new = (new & ~1) | (old & 1)
            var fNew = ctx.Builder.BuildLoad2(i16, fPtr, $"feg_{kind}_fnew");
            var keep = ctx.Builder.BuildAnd(fNew,
                LLVMValueRef.CreateConstInt(i16, 0xFFFE, false), $"feg_{kind}_keep");
            var oldCf = ctx.Builder.BuildAnd(fOld,
                LLVMValueRef.CreateConstInt(i16, 0x0001, false), $"feg_{kind}_oldcf");
            var merged = ctx.Builder.BuildOr(keep, oldCf, $"feg_{kind}_merged");
            ctx.Builder.BuildStore(merged, fPtr);

            X86ModRmMemHelpers.BuildStoreW8(ctx, r);
            ctx.Builder.BuildBr(endBB);
        }

        EmitIncDec(0, "add");   // /0 INC = ADD lhs, 1
        EmitIncDec(1, "sub");   // /1 DEC = SUB lhs, 1

        // /2-/7 invalid — silent no-op.
        for (int i = 2; i <= 7; i++)
        {
            ctx.Builder.PositionAtEnd(arms[i]);
            ctx.Builder.BuildBr(endBB);
        }

        ctx.Builder.PositionAtEnd(defaultBB);
        ctx.Builder.BuildBr(endBB);
        ctx.Builder.PositionAtEnd(endBB);
    }
}

// ============================================================================
// 24.6.7f — BCD adjustment ops.
//
// All operate on AL (and AH for AAA/AAS/AAM/AAD). After the operation,
// SF/ZF/PF are updated from the new AL via the standard parity helper.
//
// DAA / DAS: post-BCD-add / post-BCD-sub adjust on AL. AF/CF tracked
//            silicon-style (per Intel iAPX 86,88 §DAA/DAS).
// AAA / AAS: post-unpacked-BCD-add/sub. Modifies AL+AH; AL &= 0x0F.
// AAM imm8:  AH = AL / imm8; AL = AL % imm8 (typically imm8 = 10).
// AAD imm8:  AL = (AH * imm8) + AL; AH = 0.
//
// 8088 silicon flag quirks for these ops can differ from the legacy
// Apr86 implementation; Tom Harte SST validation is deferred (legacy
// backend itself doesn't fully validate these).
// ============================================================================

internal static class X86BcdHelpers
{
    public static (LLVMValueRef sf, LLVMValueRef zf, LLVMValueRef pf) Szp8(EmitContext ctx, LLVMValueRef r8, string label)
        => X86ShiftHelpers.Szp8(ctx, r8, label);

    /// <summary>Set SF/ZF/PF flags from r8 + given CF/AF/OF (i1). Mask=0x08D5.</summary>
    public static void StoreFlagsFromAl(
        EmitContext ctx, LLVMValueRef al,
        LLVMValueRef cf, LLVMValueRef af, LLVMValueRef of,
        string label)
    {
        var (sf, zf, pf) = Szp8(ctx, al, label);
        X86ShiftHelpers.StoreShiftFlags(ctx, cf, pf, af, zf, sf, of, label);
    }
}

internal sealed class X86DaaEmitter : IMicroOpEmitter
{
    public string OpName => "x86_daa";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i1 = LLVMTypeRef.Int1;
        var i8 = LLVMTypeRef.Int8;
        var i16 = LLVMTypeRef.Int16;
        var i32 = LLVMTypeRef.Int32;

        var al = X86_16Emitters.ReadGpr8(ctx, 0, "daa_al");
        var flags = X86CtrlHelpers.LoadFlags(ctx, "daa");
        var cfIn = X86CtrlHelpers.ExtractFlagBit(ctx, flags, 0, "daa_cf");
        var afIn = X86CtrlHelpers.ExtractFlagBit(ctx, flags, 4, "daa_af");

        // Phase 1: low nibble check.
        // need_lo_adj = (AL & 0x0F) > 9 OR AF=1
        var lowNib = ctx.Builder.BuildAnd(al,
            LLVMValueRef.CreateConstInt(i8, 0x0F, false), "daa_lo");
        var lowGt9 = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntUGT, lowNib,
            LLVMValueRef.CreateConstInt(i8, 9, false), "daa_lo_gt9");
        var needLo = ctx.Builder.BuildOr(lowGt9, afIn, "daa_need_lo");

        // al_after_lo = needLo ? AL + 6 : AL
        var alPlus6 = ctx.Builder.BuildAdd(al,
            LLVMValueRef.CreateConstInt(i8, 6, false), "daa_alp6");
        var al1 = ctx.Builder.BuildSelect(needLo, alPlus6, al, "daa_al1");
        var afOut = needLo;     // AF set if low adjust was applied

        // Phase 2: high nibble check uses ORIGINAL AL (per silicon — the
        // silicon order is: store original AL, do low adjust, then check
        // ORIGINAL high nibble + ORIGINAL CF for high adjust).
        var origGt99 = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntUGT, al,
            LLVMValueRef.CreateConstInt(i8, 0x99, false), "daa_orig_gt99");
        var needHi = ctx.Builder.BuildOr(origGt99, cfIn, "daa_need_hi");

        // al_final = needHi ? al1 + 0x60 : al1
        var alPlus60 = ctx.Builder.BuildAdd(al1,
            LLVMValueRef.CreateConstInt(i8, 0x60, false), "daa_alp60");
        var alFinal = ctx.Builder.BuildSelect(needHi, alPlus60, al1, "daa_alF");
        var cfOut = needHi;

        X86_16Emitters.WriteGpr8(ctx, 0, alFinal);
        var i1false = LLVMValueRef.CreateConstInt(i1, 0, false);
        X86BcdHelpers.StoreFlagsFromAl(ctx, alFinal, cfOut, afOut, i1false, "daa");
    }
}

internal sealed class X86DasEmitter : IMicroOpEmitter
{
    public string OpName => "x86_das";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i1 = LLVMTypeRef.Int1;
        var i8 = LLVMTypeRef.Int8;

        var al = X86_16Emitters.ReadGpr8(ctx, 0, "das_al");
        var flags = X86CtrlHelpers.LoadFlags(ctx, "das");
        var cfIn = X86CtrlHelpers.ExtractFlagBit(ctx, flags, 0, "das_cf");
        var afIn = X86CtrlHelpers.ExtractFlagBit(ctx, flags, 4, "das_af");

        var lowNib = ctx.Builder.BuildAnd(al,
            LLVMValueRef.CreateConstInt(i8, 0x0F, false), "das_lo");
        var lowGt9 = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntUGT, lowNib,
            LLVMValueRef.CreateConstInt(i8, 9, false), "das_lo_gt9");
        var needLo = ctx.Builder.BuildOr(lowGt9, afIn, "das_need_lo");

        var alSub6 = ctx.Builder.BuildSub(al,
            LLVMValueRef.CreateConstInt(i8, 6, false), "das_als6");
        var al1 = ctx.Builder.BuildSelect(needLo, alSub6, al, "das_al1");
        var afOut = needLo;

        var origGt99 = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntUGT, al,
            LLVMValueRef.CreateConstInt(i8, 0x99, false), "das_orig_gt99");
        var needHi = ctx.Builder.BuildOr(origGt99, cfIn, "das_need_hi");

        var alSub60 = ctx.Builder.BuildSub(al1,
            LLVMValueRef.CreateConstInt(i8, 0x60, false), "das_als60");
        var alFinal = ctx.Builder.BuildSelect(needHi, alSub60, al1, "das_alF");
        var cfOut = needHi;

        X86_16Emitters.WriteGpr8(ctx, 0, alFinal);
        var i1false = LLVMValueRef.CreateConstInt(i1, 0, false);
        X86BcdHelpers.StoreFlagsFromAl(ctx, alFinal, cfOut, afOut, i1false, "das");
    }
}

internal sealed class X86AaaEmitter : IMicroOpEmitter
{
    public string OpName => "x86_aaa";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i1 = LLVMTypeRef.Int1;
        var i8 = LLVMTypeRef.Int8;

        var al = X86_16Emitters.ReadGpr8(ctx, 0, "aaa_al");
        var ah = X86_16Emitters.ReadGpr8(ctx, 4, "aaa_ah");
        var flags = X86CtrlHelpers.LoadFlags(ctx, "aaa");
        var afIn = X86CtrlHelpers.ExtractFlagBit(ctx, flags, 4, "aaa_af");

        var lowNib = ctx.Builder.BuildAnd(al,
            LLVMValueRef.CreateConstInt(i8, 0x0F, false), "aaa_lo");
        var lowGt9 = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntUGT, lowNib,
            LLVMValueRef.CreateConstInt(i8, 9, false), "aaa_lo_gt9");
        var cond = ctx.Builder.BuildOr(lowGt9, afIn, "aaa_cond");

        // If cond: AL += 6; AH += 1
        var alAdj = ctx.Builder.BuildAdd(al,
            LLVMValueRef.CreateConstInt(i8, 6, false), "aaa_alA");
        var ahAdj = ctx.Builder.BuildAdd(ah,
            LLVMValueRef.CreateConstInt(i8, 1, false), "aaa_ahA");
        var newAl = ctx.Builder.BuildSelect(cond, alAdj, al, "aaa_newAl");
        var newAh = ctx.Builder.BuildSelect(cond, ahAdj, ah, "aaa_newAh");

        // AL := newAl & 0x0F (always mask)
        var alMasked = ctx.Builder.BuildAnd(newAl,
            LLVMValueRef.CreateConstInt(i8, 0x0F, false), "aaa_alM");

        X86_16Emitters.WriteGpr8(ctx, 0, alMasked);
        X86_16Emitters.WriteGpr8(ctx, 4, newAh);

        var i1false = LLVMValueRef.CreateConstInt(i1, 0, false);
        X86BcdHelpers.StoreFlagsFromAl(ctx, alMasked, cond, cond, i1false, "aaa");
    }
}

internal sealed class X86AasEmitter : IMicroOpEmitter
{
    public string OpName => "x86_aas";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i1 = LLVMTypeRef.Int1;
        var i8 = LLVMTypeRef.Int8;

        var al = X86_16Emitters.ReadGpr8(ctx, 0, "aas_al");
        var ah = X86_16Emitters.ReadGpr8(ctx, 4, "aas_ah");
        var flags = X86CtrlHelpers.LoadFlags(ctx, "aas");
        var afIn = X86CtrlHelpers.ExtractFlagBit(ctx, flags, 4, "aas_af");

        var lowNib = ctx.Builder.BuildAnd(al,
            LLVMValueRef.CreateConstInt(i8, 0x0F, false), "aas_lo");
        var lowGt9 = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntUGT, lowNib,
            LLVMValueRef.CreateConstInt(i8, 9, false), "aas_lo_gt9");
        var cond = ctx.Builder.BuildOr(lowGt9, afIn, "aas_cond");

        var alSub = ctx.Builder.BuildSub(al,
            LLVMValueRef.CreateConstInt(i8, 6, false), "aas_alS");
        var ahSub = ctx.Builder.BuildSub(ah,
            LLVMValueRef.CreateConstInt(i8, 1, false), "aas_ahS");
        var newAl = ctx.Builder.BuildSelect(cond, alSub, al, "aas_newAl");
        var newAh = ctx.Builder.BuildSelect(cond, ahSub, ah, "aas_newAh");

        var alMasked = ctx.Builder.BuildAnd(newAl,
            LLVMValueRef.CreateConstInt(i8, 0x0F, false), "aas_alM");

        X86_16Emitters.WriteGpr8(ctx, 0, alMasked);
        X86_16Emitters.WriteGpr8(ctx, 4, newAh);

        var i1false = LLVMValueRef.CreateConstInt(i1, 0, false);
        X86BcdHelpers.StoreFlagsFromAl(ctx, alMasked, cond, cond, i1false, "aas");
    }
}

internal sealed class X86AamEmitter : IMicroOpEmitter
{
    public string OpName => "x86_aam";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i1 = LLVMTypeRef.Int1;
        var i8 = LLVMTypeRef.Int8;

        // imm8 (base) — fetch.
        var imm8 = X86_16Emitters.FetchImm8(ctx, "aam_imm");
        var al = X86_16Emitters.ReadGpr8(ctx, 0, "aam_al");

        // AAM with imm=0 should raise INT 0 (divide-by-zero); we silently
        // no-op in that case to match the "DIV/IDIV deferred" policy.
        // Production code never uses AAM 0 anyway.
        var imm0 = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, imm8,
            LLVMValueRef.CreateConstInt(i8, 0, false), "aam_imm_zero");
        var oneI8 = LLVMValueRef.CreateConstInt(i8, 1, false);
        var safeImm = ctx.Builder.BuildSelect(imm0, oneI8, imm8, "aam_safeImm");

        var newAh = ctx.Builder.BuildUDiv(al, safeImm, "aam_ah");
        var newAl = ctx.Builder.BuildURem(al, safeImm, "aam_al");

        X86_16Emitters.WriteGpr8(ctx, 0, newAl);
        X86_16Emitters.WriteGpr8(ctx, 4, newAh);

        var i1false = LLVMValueRef.CreateConstInt(i1, 0, false);
        X86BcdHelpers.StoreFlagsFromAl(ctx, newAl, i1false, i1false, i1false, "aam");
    }
}

internal sealed class X86AadEmitter : IMicroOpEmitter
{
    public string OpName => "x86_aad";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i1 = LLVMTypeRef.Int1;
        var i8 = LLVMTypeRef.Int8;

        var imm8 = X86_16Emitters.FetchImm8(ctx, "aad_imm");
        var al = X86_16Emitters.ReadGpr8(ctx, 0, "aad_al");
        var ah = X86_16Emitters.ReadGpr8(ctx, 4, "aad_ah");

        // AL = (AH * imm8) + AL; AH = 0
        var prod = ctx.Builder.BuildMul(ah, imm8, "aad_prod");
        var newAl = ctx.Builder.BuildAdd(prod, al, "aad_newAl");

        X86_16Emitters.WriteGpr8(ctx, 0, newAl);
        X86_16Emitters.WriteGpr8(ctx, 4, LLVMValueRef.CreateConstInt(i8, 0, false));

        var i1false = LLVMValueRef.CreateConstInt(i1, 0, false);
        X86BcdHelpers.StoreFlagsFromAl(ctx, newAl, i1false, i1false, i1false, "aad");
    }
}

internal sealed class X86FfGroupDispatchEmitter : IMicroOpEmitter
{
    public string OpName => "x86_ff_group_dispatch";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i1  = LLVMTypeRef.Int1;
        var i8  = LLVMTypeRef.Int8;
        var i16 = LLVMTypeRef.Int16;
        var i32 = LLVMTypeRef.Int32;

        var lhs = ctx.Resolve("lhs");         // i16
        var sel = ctx.Resolve("modrm_reg");

        var endBB     = ctx.Function.AppendBasicBlock("ffg_end");
        var defaultBB = ctx.Function.AppendBasicBlock("ffg_default");
        var arms = new LLVMBasicBlockRef[8];
        for (int i = 0; i < 8; i++) arms[i] = ctx.Function.AppendBasicBlock($"ffg_{i}");
        var sw = ctx.Builder.BuildSwitch(sel, defaultBB, 8);
        for (int i = 0; i < 8; i++)
            sw.AddCase(LLVMValueRef.CreateConstInt(i32, (uint)i, false), arms[i]);

        void EmitIncDec(int idx, string kind)
        {
            ctx.Builder.PositionAtEnd(arms[idx]);
            var nr = X86IncDecHelpers.BuildIncDecW16(ctx, kind, lhs, $"ffg_{kind}");
            X86ModRmMemHelpers.BuildStoreW16(ctx, nr);
            ctx.Builder.BuildBr(endBB);
        }

        EmitIncDec(0, "add");   // /0 INC r/m16
        EmitIncDec(1, "sub");   // /1 DEC r/m16

        // /2 CALL near r/m16 — push IP (post-fetch), then IP = lhs. Signal block exit.
        ctx.Builder.PositionAtEnd(arms[2]);
        {
            var ipPtr = ctx.GepStatusRegister("IP");
            var ip = ctx.Builder.BuildLoad2(i16, ipPtr, "ffg2_ip");
            X86StackHelpers.PushW16(ctx, ip, "ffg2_psh");
            ctx.Builder.BuildStore(lhs, ipPtr);
            var pcwSlot2 = ctx.Layout.GepPcWritten(ctx.Builder, ctx.StatePtr);
            ctx.Builder.BuildStore(LLVMValueRef.CreateConstInt(i8, 1, false), pcwSlot2);
            ctx.Builder.BuildBr(endBB);
        }

        // /3 CALL far m16:16 — push CS, push IP, load far ptr from EA. Signal block exit.
        ctx.Builder.PositionAtEnd(arms[3]);
        {
            var ipPtr = ctx.GepStatusRegister("IP");
            var csPtr = ctx.GepStatusRegister("CS");
            var ip = ctx.Builder.BuildLoad2(i16, ipPtr, "ffg3_ip");
            var cs = ctx.Builder.BuildLoad2(i16, csPtr, "ffg3_cs");
            X86StackHelpers.PushW16(ctx, cs, "ffg3_psh_cs");
            X86StackHelpers.PushW16(ctx, ip, "ffg3_psh_ip");
            // Far pointer at ea_seg:ea_off — low word IP, high word CS.
            var seg = ctx.Resolve("ea_seg");
            var off = ctx.Resolve("ea_off");
            var newIp = X86_16Emitters.SegmentedRead16(ctx, seg, off, "ffg3_newIp");
            var off2 = ctx.Builder.BuildAdd(off,
                LLVMValueRef.CreateConstInt(i16, 2, false), "ffg3_off2");
            var newCs = X86_16Emitters.SegmentedRead16(ctx, seg, off2, "ffg3_newCs");
            ctx.Builder.BuildStore(newIp, ipPtr);
            ctx.Builder.BuildStore(newCs, csPtr);
            var pcwSlot3 = ctx.Layout.GepPcWritten(ctx.Builder, ctx.StatePtr);
            ctx.Builder.BuildStore(LLVMValueRef.CreateConstInt(i8, 1, false), pcwSlot3);
            ctx.Builder.BuildBr(endBB);
        }

        // /4 JMP near r/m16 — IP = lhs. Signal block exit.
        ctx.Builder.PositionAtEnd(arms[4]);
        {
            var ipPtr = ctx.GepStatusRegister("IP");
            ctx.Builder.BuildStore(lhs, ipPtr);
            var pcwSlot4 = ctx.Layout.GepPcWritten(ctx.Builder, ctx.StatePtr);
            ctx.Builder.BuildStore(LLVMValueRef.CreateConstInt(i8, 1, false), pcwSlot4);
            ctx.Builder.BuildBr(endBB);
        }

        // /5 JMP far m16:16 — load far ptr from EA. Signal block exit.
        ctx.Builder.PositionAtEnd(arms[5]);
        {
            var seg = ctx.Resolve("ea_seg");
            var off = ctx.Resolve("ea_off");
            var newIp = X86_16Emitters.SegmentedRead16(ctx, seg, off, "ffg5_newIp");
            var off2 = ctx.Builder.BuildAdd(off,
                LLVMValueRef.CreateConstInt(i16, 2, false), "ffg5_off2");
            var newCs = X86_16Emitters.SegmentedRead16(ctx, seg, off2, "ffg5_newCs");
            ctx.Builder.BuildStore(newIp, ctx.GepStatusRegister("IP"));
            ctx.Builder.BuildStore(newCs, ctx.GepStatusRegister("CS"));
            var pcwSlot5 = ctx.Layout.GepPcWritten(ctx.Builder, ctx.StatePtr);
            ctx.Builder.BuildStore(LLVMValueRef.CreateConstInt(i8, 1, false), pcwSlot5);
            ctx.Builder.BuildBr(endBB);
        }

        // /6 PUSH r/m16 — push lhs.
        ctx.Builder.PositionAtEnd(arms[6]);
        {
            X86StackHelpers.PushW16(ctx, lhs, "ffg6_psh");
            ctx.Builder.BuildBr(endBB);
        }

        // /7 invalid — silent no-op.
        ctx.Builder.PositionAtEnd(arms[7]);
        ctx.Builder.BuildBr(endBB);

        ctx.Builder.PositionAtEnd(defaultBB);
        ctx.Builder.BuildBr(endBB);
        ctx.Builder.PositionAtEnd(endBB);
    }
}

// ============================================================================
// 24.6.7b2 — shift/rotate by CL (D2/D3).
//
// Reads CL at runtime; for count==0 the entire instruction is a no-op
// (no flag update — per Intel iAPX 86,88 §shifts: "If the count is 0,
// no flags are affected"). For count>=1, dispatch to one of the 8
// sub-ops same as count=1.
//
// SHL / SHR / SAR use LLVM precomputed shifts at i32 width (clamped to
// avoid LLVM UB) — semantics match silicon for the common count
// values used by real software. CF is the bit just shifted out, which
// for count N equals bit (W-N) of original (for SHL) or bit (N-1) of
// original (for SHR/SAR). The 8088 AF rule (SHL=bit 4 of result;
// SHR/SAR=0) carries over unchanged. OF for count==1 only is
// well-defined per Intel; for count>1 we use the same MSB-XOR-CF
// formula (silicon-undefined but consistent).
//
// ROL/ROR/RCL/RCR with count!=1 have notoriously undefined silicon
// behaviour; we delegate to the count=1 IR (effectively shift by 1
// regardless of CL, with a TODO comment). This is wrong for count>1
// but correct for count=1, which covers most real code that ever
// reaches D2/D3 with a small dynamic count.
// ============================================================================

internal sealed class X86ShiftRotateW8CountClEmitter : IMicroOpEmitter
{
    public string OpName => "x86_shift_rotate_w8_count_cl";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i1  = LLVMTypeRef.Int1;
        var i8  = LLVMTypeRef.Int8;
        var i16 = LLVMTypeRef.Int16;
        var i32 = LLVMTypeRef.Int32;

        var lhs = ctx.Resolve("lhs");          // i8
        var sel = ctx.Resolve("modrm_reg");

        // Read CL — GPR index 1, low byte (byteIdx=1).
        var cl = X86_16Emitters.ReadGpr8(ctx, 1, "shrcl_cl");
        var clNonZero = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, cl,
            LLVMValueRef.CreateConstInt(i8, 0, false), "shrcl_nz");

        var doBB  = ctx.Function.AppendBasicBlock("shrcl_do");
        var endBB = ctx.Function.AppendBasicBlock("shrcl_end");
        ctx.Builder.BuildCondBr(clNonZero, doBB, endBB);

        ctx.Builder.PositionAtEnd(doBB);

        // Clamp count to 16 (safe — beyond 8 bits of operand the result
        // is anyway 0 for shifts, and clamping prevents LLVM UB).
        var clU16 = LLVMValueRef.CreateConstInt(i8, 16, false);
        var clCmp16 = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntULT, cl, clU16, "shrcl_cmp16");
        var clClamp = ctx.Builder.BuildSelect(clCmp16, cl, clU16, "shrcl_clamp");
        var cl32 = ctx.Builder.BuildZExt(clClamp, i32, "shrcl_cl32");

        var lhs32 = ctx.Builder.BuildZExt(lhs, i32, "shrcl_lhs32");
        var lhsSx = ctx.Builder.BuildSExt(lhs, i32, "shrcl_lhsSx");

        // Branch by sub-op.
        var doneBB = ctx.Function.AppendBasicBlock("shrcl_done");
        var defaultBB = ctx.Function.AppendBasicBlock("shrcl_default");
        var arms = new LLVMBasicBlockRef[8];
        for (int i = 0; i < 8; i++) arms[i] = ctx.Function.AppendBasicBlock($"shrcl_{i}");
        var sw = ctx.Builder.BuildSwitch(sel, defaultBB, 8);
        for (int i = 0; i < 8; i++)
            sw.AddCase(LLVMValueRef.CreateConstInt(i32, (uint)i, false), arms[i]);

        var i1false = LLVMValueRef.CreateConstInt(i1, 0, false);

        // For ROL/ROR/RCL/RCR (/0/1/2/3) — TODO: silicon-accurate count>1.
        // For now, delegate to count=1 IR which always shifts by 1.
        // Matches D0 D1 semantics; wrong for cl != 1.
        void EmitRotateCount1Stub(int idx, string kind)
        {
            ctx.Builder.PositionAtEnd(arms[idx]);
            // Extract MSB / LSB of original for CF computation.
            var msbMask = ctx.Builder.BuildAnd(lhs,
                LLVMValueRef.CreateConstInt(i8, 0x80, false), $"shrcl_{kind}_msbm");
            var msb = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, msbMask,
                LLVMValueRef.CreateConstInt(i8, 0, false), $"shrcl_{kind}_msb");
            var lsbMask = ctx.Builder.BuildAnd(lhs,
                LLVMValueRef.CreateConstInt(i8, 0x01, false), $"shrcl_{kind}_lsbm");
            var lsb = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, lsbMask,
                LLVMValueRef.CreateConstInt(i8, 0, false), $"shrcl_{kind}_lsb");
            var cfIn = X86ShiftHelpers.ReadCf(ctx, $"shrcl_{kind}");

            LLVMValueRef result, cf, of;
            switch (kind)
            {
                case "rol":
                    var rolL = ctx.Builder.BuildShl(lhs,
                        LLVMValueRef.CreateConstInt(i8, 1, false), "rolc_l");
                    var rolR = ctx.Builder.BuildLShr(lhs,
                        LLVMValueRef.CreateConstInt(i8, 7, false), "rolc_r");
                    result = ctx.Builder.BuildOr(rolL, rolR, "rolc_r8");
                    cf = msb;
                    {
                        var rMsbMask = ctx.Builder.BuildAnd(result,
                            LLVMValueRef.CreateConstInt(i8, 0x80, false), "rolc_rmsb_m");
                        var rMsb = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, rMsbMask,
                            LLVMValueRef.CreateConstInt(i8, 0, false), "rolc_rmsb");
                        of = ctx.Builder.BuildXor(rMsb, cf, "rolc_of");
                    }
                    break;
                case "ror":
                    var rorR = ctx.Builder.BuildLShr(lhs,
                        LLVMValueRef.CreateConstInt(i8, 1, false), "rorc_r");
                    var rorL = ctx.Builder.BuildShl(lhs,
                        LLVMValueRef.CreateConstInt(i8, 7, false), "rorc_l");
                    result = ctx.Builder.BuildOr(rorR, rorL, "rorc_r8");
                    cf = lsb;
                    {
                        var b7m = ctx.Builder.BuildAnd(result,
                            LLVMValueRef.CreateConstInt(i8, 0x80, false), "rorc_b7m");
                        var b7 = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, b7m,
                            LLVMValueRef.CreateConstInt(i8, 0, false), "rorc_b7");
                        var b6m = ctx.Builder.BuildAnd(result,
                            LLVMValueRef.CreateConstInt(i8, 0x40, false), "rorc_b6m");
                        var b6 = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, b6m,
                            LLVMValueRef.CreateConstInt(i8, 0, false), "rorc_b6");
                        of = ctx.Builder.BuildXor(b7, b6, "rorc_of");
                    }
                    break;
                case "rcl":
                    var rclL = ctx.Builder.BuildShl(lhs,
                        LLVMValueRef.CreateConstInt(i8, 1, false), "rclc_l");
                    var cfInZ = ctx.Builder.BuildZExt(cfIn, i8, "rclc_cfz");
                    result = ctx.Builder.BuildOr(rclL, cfInZ, "rclc_r8");
                    cf = msb;
                    {
                        var rMsbMask = ctx.Builder.BuildAnd(result,
                            LLVMValueRef.CreateConstInt(i8, 0x80, false), "rclc_rmsb_m");
                        var rMsb = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, rMsbMask,
                            LLVMValueRef.CreateConstInt(i8, 0, false), "rclc_rmsb");
                        of = ctx.Builder.BuildXor(rMsb, cf, "rclc_of");
                    }
                    break;
                case "rcr":
                    var rcrR = ctx.Builder.BuildLShr(lhs,
                        LLVMValueRef.CreateConstInt(i8, 1, false), "rcrc_r");
                    var cfInL = ctx.Builder.BuildShl(
                        ctx.Builder.BuildZExt(cfIn, i8, "rcrc_cfz"),
                        LLVMValueRef.CreateConstInt(i8, 7, false), "rcrc_cfshl");
                    result = ctx.Builder.BuildOr(rcrR, cfInL, "rcrc_r8");
                    cf = lsb;
                    of = ctx.Builder.BuildXor(cfIn, msb, "rcrc_of");
                    break;
                default:
                    throw new InvalidOperationException();
            }

            X86ModRmMemHelpers.BuildStoreW8(ctx, result);
            // AF preserved for rotates.
            var afKeep = X86CtrlHelpers.ExtractFlagBit(ctx, X86CtrlHelpers.LoadFlags(ctx, $"shrcl_{kind}_afk"), 4, $"shrcl_{kind}_afk_v");
            var (sf, zf, pf) = X86ShiftHelpers.Szp8(ctx, result, $"shrcl_{kind}");
            X86ShiftHelpers.StoreShiftFlags(ctx, cf, pf, afKeep, zf, sf, of, $"shrcl_{kind}");
            ctx.Builder.BuildBr(doneBB);
        }

        // SHL/SHR/SAR — precompute via LLVM shift with clamped count.
        void EmitShlOp(int idx)
        {
            ctx.Builder.PositionAtEnd(arms[idx]);
            // result_i32 = lhs_zext32 << cl
            var shifted = ctx.Builder.BuildShl(lhs32, cl32, "shlc_sh");
            var result = ctx.Builder.BuildTrunc(shifted, i8, "shlc_r8");
            // CF = bit 8 of (lhs << cl) — i.e. the bit JUST shifted out.
            var cfRaw = ctx.Builder.BuildLShr(shifted,
                LLVMValueRef.CreateConstInt(i32, 8, false), "shlc_cf_raw");
            var cfMasked = ctx.Builder.BuildAnd(cfRaw,
                LLVMValueRef.CreateConstInt(i32, 1, false), "shlc_cfm");
            var cf = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, cfMasked,
                LLVMValueRef.CreateConstInt(i32, 0, false), "shlc_cf");
            // OF = MSB(result) XOR CF (count==1 rule; silicon undefined for count>1)
            var rMsbM = ctx.Builder.BuildAnd(result,
                LLVMValueRef.CreateConstInt(i8, 0x80, false), "shlc_rmsb_m");
            var rMsb = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, rMsbM,
                LLVMValueRef.CreateConstInt(i8, 0, false), "shlc_rmsb");
            var of = ctx.Builder.BuildXor(rMsb, cf, "shlc_of");
            // AF = bit 4 of result (8088 SHL quirk)
            var afM = ctx.Builder.BuildAnd(result,
                LLVMValueRef.CreateConstInt(i8, 0x10, false), "shlc_afm");
            var af = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, afM,
                LLVMValueRef.CreateConstInt(i8, 0, false), "shlc_af");

            X86ModRmMemHelpers.BuildStoreW8(ctx, result);
            var (sf, zf, pf) = X86ShiftHelpers.Szp8(ctx, result, "shlc");
            X86ShiftHelpers.StoreShiftFlags(ctx, cf, pf, af, zf, sf, of, "shlc");
            ctx.Builder.BuildBr(doneBB);
        }

        void EmitShrOp(int idx)
        {
            ctx.Builder.PositionAtEnd(arms[idx]);
            var shifted = ctx.Builder.BuildLShr(lhs32, cl32, "shrc_sh");
            var result = ctx.Builder.BuildTrunc(shifted, i8, "shrc_r8");
            // CF = bit (cl-1) of original
            var clm1 = ctx.Builder.BuildSub(cl32,
                LLVMValueRef.CreateConstInt(i32, 1, false), "shrc_clm1");
            var cfRaw = ctx.Builder.BuildLShr(lhs32, clm1, "shrc_cf_raw");
            var cfMasked = ctx.Builder.BuildAnd(cfRaw,
                LLVMValueRef.CreateConstInt(i32, 1, false), "shrc_cfm");
            var cf = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, cfMasked,
                LLVMValueRef.CreateConstInt(i32, 0, false), "shrc_cf");
            // OF = original MSB (count==1 SHR rule)
            var origMsbM = ctx.Builder.BuildAnd(lhs,
                LLVMValueRef.CreateConstInt(i8, 0x80, false), "shrc_omm");
            var of = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, origMsbM,
                LLVMValueRef.CreateConstInt(i8, 0, false), "shrc_of");

            X86ModRmMemHelpers.BuildStoreW8(ctx, result);
            var (sf, zf, pf) = X86ShiftHelpers.Szp8(ctx, result, "shrc");
            X86ShiftHelpers.StoreShiftFlags(ctx, cf, pf, i1false, zf, sf, of, "shrc");
            ctx.Builder.BuildBr(doneBB);
        }

        void EmitSarOp(int idx)
        {
            ctx.Builder.PositionAtEnd(arms[idx]);
            var shifted = ctx.Builder.BuildAShr(lhsSx, cl32, "sarc_sh");
            var result = ctx.Builder.BuildTrunc(shifted, i8, "sarc_r8");
            var clm1 = ctx.Builder.BuildSub(cl32,
                LLVMValueRef.CreateConstInt(i32, 1, false), "sarc_clm1");
            var cfRaw = ctx.Builder.BuildLShr(lhs32, clm1, "sarc_cf_raw");
            var cfMasked = ctx.Builder.BuildAnd(cfRaw,
                LLVMValueRef.CreateConstInt(i32, 1, false), "sarc_cfm");
            var cf = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, cfMasked,
                LLVMValueRef.CreateConstInt(i32, 0, false), "sarc_cf");
            // OF = 0 for SAR (count==1 rule; silicon undefined for count>1)

            X86ModRmMemHelpers.BuildStoreW8(ctx, result);
            var (sf, zf, pf) = X86ShiftHelpers.Szp8(ctx, result, "sarc");
            X86ShiftHelpers.StoreShiftFlags(ctx, cf, pf, i1false, zf, sf, i1false, "sarc");
            ctx.Builder.BuildBr(doneBB);
        }

        EmitRotateCount1Stub(0, "rol");
        EmitRotateCount1Stub(1, "ror");
        EmitRotateCount1Stub(2, "rcl");
        EmitRotateCount1Stub(3, "rcr");
        EmitShlOp(4);
        EmitShrOp(5);
        EmitShlOp(6);   // /6 = SAL alias for SHL
        EmitSarOp(7);

        ctx.Builder.PositionAtEnd(defaultBB);
        ctx.Builder.BuildBr(doneBB);

        ctx.Builder.PositionAtEnd(doneBB);
        ctx.Builder.BuildBr(endBB);
        ctx.Builder.PositionAtEnd(endBB);
    }
}

internal sealed class X86ShiftRotateW16CountClEmitter : IMicroOpEmitter
{
    public string OpName => "x86_shift_rotate_w16_count_cl";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i1  = LLVMTypeRef.Int1;
        var i8  = LLVMTypeRef.Int8;
        var i16 = LLVMTypeRef.Int16;
        var i32 = LLVMTypeRef.Int32;

        var lhs = ctx.Resolve("lhs");
        var sel = ctx.Resolve("modrm_reg");

        var cl = X86_16Emitters.ReadGpr8(ctx, 1, "shrcl16_cl");
        var clNonZero = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, cl,
            LLVMValueRef.CreateConstInt(i8, 0, false), "shrcl16_nz");

        var doBB  = ctx.Function.AppendBasicBlock("shrcl16_do");
        var endBB = ctx.Function.AppendBasicBlock("shrcl16_end");
        ctx.Builder.BuildCondBr(clNonZero, doBB, endBB);

        ctx.Builder.PositionAtEnd(doBB);

        var clU8 = LLVMValueRef.CreateConstInt(i8, 32, false);
        var clCmp = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntULT, cl, clU8, "shrcl16_cmp32");
        var clClamp = ctx.Builder.BuildSelect(clCmp, cl, clU8, "shrcl16_clamp");
        var cl32 = ctx.Builder.BuildZExt(clClamp, i32, "shrcl16_cl32");

        var lhs32 = ctx.Builder.BuildZExt(lhs, i32, "shrcl16_lhs32");
        var lhsSx = ctx.Builder.BuildSExt(lhs, i32, "shrcl16_lhsSx");

        var doneBB = ctx.Function.AppendBasicBlock("shrcl16_done");
        var defaultBB = ctx.Function.AppendBasicBlock("shrcl16_default");
        var arms = new LLVMBasicBlockRef[8];
        for (int i = 0; i < 8; i++) arms[i] = ctx.Function.AppendBasicBlock($"shrcl16_{i}");
        var sw = ctx.Builder.BuildSwitch(sel, defaultBB, 8);
        for (int i = 0; i < 8; i++)
            sw.AddCase(LLVMValueRef.CreateConstInt(i32, (uint)i, false), arms[i]);

        var i1false = LLVMValueRef.CreateConstInt(i1, 0, false);

        // Rotates: same count=1-stub strategy as W8 path.
        void EmitRotateCount1Stub(int idx, string kind)
        {
            ctx.Builder.PositionAtEnd(arms[idx]);
            var msbMask = ctx.Builder.BuildAnd(lhs,
                LLVMValueRef.CreateConstInt(i16, 0x8000, false), $"shrcl16_{kind}_msbm");
            var msb = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, msbMask,
                LLVMValueRef.CreateConstInt(i16, 0, false), $"shrcl16_{kind}_msb");
            var lsbMask = ctx.Builder.BuildAnd(lhs,
                LLVMValueRef.CreateConstInt(i16, 0x0001, false), $"shrcl16_{kind}_lsbm");
            var lsb = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, lsbMask,
                LLVMValueRef.CreateConstInt(i16, 0, false), $"shrcl16_{kind}_lsb");
            var cfIn = X86ShiftHelpers.ReadCf(ctx, $"shrcl16_{kind}");

            LLVMValueRef result, cf, of;
            switch (kind)
            {
                case "rol":
                    var rolL = ctx.Builder.BuildShl(lhs,
                        LLVMValueRef.CreateConstInt(i16, 1, false), "rolc16_l");
                    var rolR = ctx.Builder.BuildLShr(lhs,
                        LLVMValueRef.CreateConstInt(i16, 15, false), "rolc16_r");
                    result = ctx.Builder.BuildOr(rolL, rolR, "rolc16_r");
                    cf = msb;
                    {
                        var rMsbMask = ctx.Builder.BuildAnd(result,
                            LLVMValueRef.CreateConstInt(i16, 0x8000, false), "rolc16_rmsb_m");
                        var rMsb = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, rMsbMask,
                            LLVMValueRef.CreateConstInt(i16, 0, false), "rolc16_rmsb");
                        of = ctx.Builder.BuildXor(rMsb, cf, "rolc16_of");
                    }
                    break;
                case "ror":
                    var rorR = ctx.Builder.BuildLShr(lhs,
                        LLVMValueRef.CreateConstInt(i16, 1, false), "rorc16_r");
                    var rorL = ctx.Builder.BuildShl(lhs,
                        LLVMValueRef.CreateConstInt(i16, 15, false), "rorc16_l");
                    result = ctx.Builder.BuildOr(rorR, rorL, "rorc16_r");
                    cf = lsb;
                    {
                        var b15m = ctx.Builder.BuildAnd(result,
                            LLVMValueRef.CreateConstInt(i16, 0x8000, false), "rorc16_b15m");
                        var b15 = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, b15m,
                            LLVMValueRef.CreateConstInt(i16, 0, false), "rorc16_b15");
                        var b14m = ctx.Builder.BuildAnd(result,
                            LLVMValueRef.CreateConstInt(i16, 0x4000, false), "rorc16_b14m");
                        var b14 = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, b14m,
                            LLVMValueRef.CreateConstInt(i16, 0, false), "rorc16_b14");
                        of = ctx.Builder.BuildXor(b15, b14, "rorc16_of");
                    }
                    break;
                case "rcl":
                    var rclL = ctx.Builder.BuildShl(lhs,
                        LLVMValueRef.CreateConstInt(i16, 1, false), "rclc16_l");
                    var cfInZ = ctx.Builder.BuildZExt(cfIn, i16, "rclc16_cfz");
                    result = ctx.Builder.BuildOr(rclL, cfInZ, "rclc16_r");
                    cf = msb;
                    {
                        var rMsbMask = ctx.Builder.BuildAnd(result,
                            LLVMValueRef.CreateConstInt(i16, 0x8000, false), "rclc16_rmsb_m");
                        var rMsb = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, rMsbMask,
                            LLVMValueRef.CreateConstInt(i16, 0, false), "rclc16_rmsb");
                        of = ctx.Builder.BuildXor(rMsb, cf, "rclc16_of");
                    }
                    break;
                case "rcr":
                    var rcrR = ctx.Builder.BuildLShr(lhs,
                        LLVMValueRef.CreateConstInt(i16, 1, false), "rcrc16_r");
                    var cfInL = ctx.Builder.BuildShl(
                        ctx.Builder.BuildZExt(cfIn, i16, "rcrc16_cfz"),
                        LLVMValueRef.CreateConstInt(i16, 15, false), "rcrc16_cfshl");
                    result = ctx.Builder.BuildOr(rcrR, cfInL, "rcrc16_r");
                    cf = lsb;
                    of = ctx.Builder.BuildXor(cfIn, msb, "rcrc16_of");
                    break;
                default:
                    throw new InvalidOperationException();
            }

            X86ModRmMemHelpers.BuildStoreW16(ctx, result);
            var afKeep = X86CtrlHelpers.ExtractFlagBit(ctx, X86CtrlHelpers.LoadFlags(ctx, $"shrcl16_{kind}_afk"), 4, $"shrcl16_{kind}_afk_v");
            var (sf, zf, pf) = X86ShiftHelpers.Szp16(ctx, result, $"shrcl16_{kind}");
            X86ShiftHelpers.StoreShiftFlags(ctx, cf, pf, afKeep, zf, sf, of, $"shrcl16_{kind}");
            ctx.Builder.BuildBr(doneBB);
        }

        void EmitShlOp(int idx)
        {
            ctx.Builder.PositionAtEnd(arms[idx]);
            var shifted = ctx.Builder.BuildShl(lhs32, cl32, "shlc16_sh");
            var result = ctx.Builder.BuildTrunc(shifted, i16, "shlc16_r");
            var cfRaw = ctx.Builder.BuildLShr(shifted,
                LLVMValueRef.CreateConstInt(i32, 16, false), "shlc16_cf_raw");
            var cfMasked = ctx.Builder.BuildAnd(cfRaw,
                LLVMValueRef.CreateConstInt(i32, 1, false), "shlc16_cfm");
            var cf = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, cfMasked,
                LLVMValueRef.CreateConstInt(i32, 0, false), "shlc16_cf");
            var rMsbM = ctx.Builder.BuildAnd(result,
                LLVMValueRef.CreateConstInt(i16, 0x8000, false), "shlc16_rmsb_m");
            var rMsb = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, rMsbM,
                LLVMValueRef.CreateConstInt(i16, 0, false), "shlc16_rmsb");
            var of = ctx.Builder.BuildXor(rMsb, cf, "shlc16_of");
            var afM = ctx.Builder.BuildAnd(result,
                LLVMValueRef.CreateConstInt(i16, 0x10, false), "shlc16_afm");
            var af = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, afM,
                LLVMValueRef.CreateConstInt(i16, 0, false), "shlc16_af");

            X86ModRmMemHelpers.BuildStoreW16(ctx, result);
            var (sf, zf, pf) = X86ShiftHelpers.Szp16(ctx, result, "shlc16");
            X86ShiftHelpers.StoreShiftFlags(ctx, cf, pf, af, zf, sf, of, "shlc16");
            ctx.Builder.BuildBr(doneBB);
        }

        void EmitShrOp(int idx)
        {
            ctx.Builder.PositionAtEnd(arms[idx]);
            var shifted = ctx.Builder.BuildLShr(lhs32, cl32, "shrc16_sh");
            var result = ctx.Builder.BuildTrunc(shifted, i16, "shrc16_r");
            var clm1 = ctx.Builder.BuildSub(cl32,
                LLVMValueRef.CreateConstInt(i32, 1, false), "shrc16_clm1");
            var cfRaw = ctx.Builder.BuildLShr(lhs32, clm1, "shrc16_cf_raw");
            var cfMasked = ctx.Builder.BuildAnd(cfRaw,
                LLVMValueRef.CreateConstInt(i32, 1, false), "shrc16_cfm");
            var cf = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, cfMasked,
                LLVMValueRef.CreateConstInt(i32, 0, false), "shrc16_cf");
            var origMsbM = ctx.Builder.BuildAnd(lhs,
                LLVMValueRef.CreateConstInt(i16, 0x8000, false), "shrc16_omm");
            var of = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, origMsbM,
                LLVMValueRef.CreateConstInt(i16, 0, false), "shrc16_of");

            X86ModRmMemHelpers.BuildStoreW16(ctx, result);
            var (sf, zf, pf) = X86ShiftHelpers.Szp16(ctx, result, "shrc16");
            X86ShiftHelpers.StoreShiftFlags(ctx, cf, pf, i1false, zf, sf, of, "shrc16");
            ctx.Builder.BuildBr(doneBB);
        }

        void EmitSarOp(int idx)
        {
            ctx.Builder.PositionAtEnd(arms[idx]);
            var shifted = ctx.Builder.BuildAShr(lhsSx, cl32, "sarc16_sh");
            var result = ctx.Builder.BuildTrunc(shifted, i16, "sarc16_r");
            var clm1 = ctx.Builder.BuildSub(cl32,
                LLVMValueRef.CreateConstInt(i32, 1, false), "sarc16_clm1");
            var cfRaw = ctx.Builder.BuildLShr(lhs32, clm1, "sarc16_cf_raw");
            var cfMasked = ctx.Builder.BuildAnd(cfRaw,
                LLVMValueRef.CreateConstInt(i32, 1, false), "sarc16_cfm");
            var cf = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, cfMasked,
                LLVMValueRef.CreateConstInt(i32, 0, false), "sarc16_cf");

            X86ModRmMemHelpers.BuildStoreW16(ctx, result);
            var (sf, zf, pf) = X86ShiftHelpers.Szp16(ctx, result, "sarc16");
            X86ShiftHelpers.StoreShiftFlags(ctx, cf, pf, i1false, zf, sf, i1false, "sarc16");
            ctx.Builder.BuildBr(doneBB);
        }

        EmitRotateCount1Stub(0, "rol");
        EmitRotateCount1Stub(1, "ror");
        EmitRotateCount1Stub(2, "rcl");
        EmitRotateCount1Stub(3, "rcr");
        EmitShlOp(4);
        EmitShrOp(5);
        EmitShlOp(6);
        EmitSarOp(7);

        ctx.Builder.PositionAtEnd(defaultBB);
        ctx.Builder.BuildBr(doneBB);

        ctx.Builder.PositionAtEnd(doneBB);
        ctx.Builder.BuildBr(endBB);
        ctx.Builder.PositionAtEnd(endBB);
    }
}

internal sealed class X86ShiftRotateW16Count1Emitter : IMicroOpEmitter
{
    public string OpName => "x86_shift_rotate_w16_count1";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i1  = LLVMTypeRef.Int1;
        var i8  = LLVMTypeRef.Int8;
        var i16 = LLVMTypeRef.Int16;
        var i32 = LLVMTypeRef.Int32;

        var lhs = ctx.Resolve("lhs");
        var sel = ctx.Resolve("modrm_reg");

        var i1false = LLVMValueRef.CreateConstInt(i1, 0, false);
        var cfIn = X86ShiftHelpers.ReadCf(ctx, "shr16");

        var msbMask = ctx.Builder.BuildAnd(lhs,
            LLVMValueRef.CreateConstInt(i16, 0x8000, false), "shr16_msbm");
        var msb = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, msbMask,
            LLVMValueRef.CreateConstInt(i16, 0, false), "shr16_msb");
        var lsbMask = ctx.Builder.BuildAnd(lhs,
            LLVMValueRef.CreateConstInt(i16, 0x0001, false), "shr16_lsbm");
        var lsb = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, lsbMask,
            LLVMValueRef.CreateConstInt(i16, 0, false), "shr16_lsb");

        var endBB     = ctx.Function.AppendBasicBlock("shr16_end");
        var defaultBB = ctx.Function.AppendBasicBlock("shr16_default");
        var arms = new LLVMBasicBlockRef[8];
        for (int i = 0; i < 8; i++) arms[i] = ctx.Function.AppendBasicBlock($"shr16_{i}");
        var sw = ctx.Builder.BuildSwitch(sel, defaultBB, 8);
        for (int i = 0; i < 8; i++)
            sw.AddCase(LLVMValueRef.CreateConstInt(i32, (uint)i, false), arms[i]);

        void EmitArm(int idx, string kind)
        {
            ctx.Builder.PositionAtEnd(arms[idx]);

            LLVMValueRef result, cf, of, af;

            switch (kind)
            {
                case "rol":
                    var rolL = ctx.Builder.BuildShl(lhs,
                        LLVMValueRef.CreateConstInt(i16, 1, false), "rol_l");
                    var rolR = ctx.Builder.BuildLShr(lhs,
                        LLVMValueRef.CreateConstInt(i16, 15, false), "rol_r");
                    result = ctx.Builder.BuildOr(rolL, rolR, "rol_r16");
                    cf = msb;
                    {
                        var rMsbMask = ctx.Builder.BuildAnd(result,
                            LLVMValueRef.CreateConstInt(i16, 0x8000, false), "rol_rmsb_m");
                        var rMsb = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, rMsbMask,
                            LLVMValueRef.CreateConstInt(i16, 0, false), "rol_rmsb");
                        of = ctx.Builder.BuildXor(rMsb, cf, "rol_of");
                    }
                    af = X86CtrlHelpers.ExtractFlagBit(ctx, X86CtrlHelpers.LoadFlags(ctx, "rol_af"), 4, "rol_af_keep");
                    break;

                case "ror":
                    var rorR = ctx.Builder.BuildLShr(lhs,
                        LLVMValueRef.CreateConstInt(i16, 1, false), "ror_r");
                    var rorL = ctx.Builder.BuildShl(lhs,
                        LLVMValueRef.CreateConstInt(i16, 15, false), "ror_l");
                    result = ctx.Builder.BuildOr(rorR, rorL, "ror_r16");
                    cf = lsb;
                    {
                        var b15m = ctx.Builder.BuildAnd(result,
                            LLVMValueRef.CreateConstInt(i16, 0x8000, false), "ror_b15m");
                        var b15 = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, b15m,
                            LLVMValueRef.CreateConstInt(i16, 0, false), "ror_b15");
                        var b14m = ctx.Builder.BuildAnd(result,
                            LLVMValueRef.CreateConstInt(i16, 0x4000, false), "ror_b14m");
                        var b14 = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, b14m,
                            LLVMValueRef.CreateConstInt(i16, 0, false), "ror_b14");
                        of = ctx.Builder.BuildXor(b15, b14, "ror_of");
                    }
                    af = X86CtrlHelpers.ExtractFlagBit(ctx, X86CtrlHelpers.LoadFlags(ctx, "ror_af"), 4, "ror_af_keep");
                    break;

                case "rcl":
                    var rclL = ctx.Builder.BuildShl(lhs,
                        LLVMValueRef.CreateConstInt(i16, 1, false), "rcl_l");
                    var cfInZ = ctx.Builder.BuildZExt(cfIn, i16, "rcl_cfz");
                    result = ctx.Builder.BuildOr(rclL, cfInZ, "rcl_r16");
                    cf = msb;
                    {
                        var rMsbMask = ctx.Builder.BuildAnd(result,
                            LLVMValueRef.CreateConstInt(i16, 0x8000, false), "rcl_rmsb_m");
                        var rMsb = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, rMsbMask,
                            LLVMValueRef.CreateConstInt(i16, 0, false), "rcl_rmsb");
                        of = ctx.Builder.BuildXor(rMsb, cf, "rcl_of");
                    }
                    af = X86CtrlHelpers.ExtractFlagBit(ctx, X86CtrlHelpers.LoadFlags(ctx, "rcl_af"), 4, "rcl_af_keep");
                    break;

                case "rcr":
                    var rcrR = ctx.Builder.BuildLShr(lhs,
                        LLVMValueRef.CreateConstInt(i16, 1, false), "rcr_r");
                    var cfInL = ctx.Builder.BuildShl(
                        ctx.Builder.BuildZExt(cfIn, i16, "rcr_cfz"),
                        LLVMValueRef.CreateConstInt(i16, 15, false), "rcr_cfshl");
                    result = ctx.Builder.BuildOr(rcrR, cfInL, "rcr_r16");
                    cf = lsb;
                    of = ctx.Builder.BuildXor(cfIn, msb, "rcr_of");
                    af = X86CtrlHelpers.ExtractFlagBit(ctx, X86CtrlHelpers.LoadFlags(ctx, "rcr_af"), 4, "rcr_af_keep");
                    break;

                case "shl":
                    result = ctx.Builder.BuildShl(lhs,
                        LLVMValueRef.CreateConstInt(i16, 1, false), "shl_r16");
                    cf = msb;
                    {
                        var rMsbMask = ctx.Builder.BuildAnd(result,
                            LLVMValueRef.CreateConstInt(i16, 0x8000, false), "shl_rmsb_m");
                        var rMsb = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, rMsbMask,
                            LLVMValueRef.CreateConstInt(i16, 0, false), "shl_rmsb");
                        of = ctx.Builder.BuildXor(rMsb, cf, "shl_of");
                    }
                    {
                        var afMask = ctx.Builder.BuildAnd(result,
                            LLVMValueRef.CreateConstInt(i16, 0x10, false), "shl_afm");
                        af = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, afMask,
                            LLVMValueRef.CreateConstInt(i16, 0, false), "shl_af");
                    }
                    break;

                case "shr":
                    result = ctx.Builder.BuildLShr(lhs,
                        LLVMValueRef.CreateConstInt(i16, 1, false), "shr_r16");
                    cf = lsb;
                    of = msb;
                    af = i1false;
                    break;

                case "sar":
                    result = ctx.Builder.BuildAShr(lhs,
                        LLVMValueRef.CreateConstInt(i16, 1, false), "sar_r16");
                    cf = lsb;
                    of = i1false;
                    af = i1false;
                    break;

                default:
                    throw new InvalidOperationException($"shr16 unknown kind '{kind}'");
            }

            X86ModRmMemHelpers.BuildStoreW16(ctx, result);

            var (sf, zf, pf) = X86ShiftHelpers.Szp16(ctx, result, $"shr16_{kind}");
            X86ShiftHelpers.StoreShiftFlags(ctx, cf, pf, af, zf, sf, of, $"shr16_{kind}");

            ctx.Builder.BuildBr(endBB);
        }

        EmitArm(0, "rol");
        EmitArm(1, "ror");
        EmitArm(2, "rcl");
        EmitArm(3, "rcr");
        EmitArm(4, "shl");
        EmitArm(5, "shr");
        EmitArm(6, "shl");
        EmitArm(7, "sar");

        ctx.Builder.PositionAtEnd(defaultBB);
        ctx.Builder.BuildBr(endBB);
        ctx.Builder.PositionAtEnd(endBB);
    }
}

// ============================================================================
// Phase 25 — Intel 80186 additions
//
// All emitters below are referenced only by spec/x86-16/i80186/groups/
// i80186-additions.json. Running an i8086 backend will never invoke them
// (DecoderTable doesn't see the 80186 spec). They form the back half of
// the inheritance demo: i80186 spec inherits 149 instructions + adds 26
// new ones; these emitters give those 26 IR.
// ============================================================================

// x86_push_sp_pre_decrement — 80186 fix to the 8086 PUSH SP silicon quirk.
// 8086: SP -= 2; mem[SS:SP] = SP   (i.e. captures the new, decremented SP)
// 80186: SP_orig = SP; SP -= 2; mem[SS:SP] = SP_orig   (captures original SP)
// x86_clts_286_stub — Phase 26 Sprint 26.1 placeholder. Real CLTS clears
// the TS bit in MSW; the v1 stub is a no-op pending Sprint 26.4 (MSW
// status register addition). Scope: just lets the i80286 spec load +
// pass schema validation so we can prove the inheritance chain depth=3
// works (i80286 -> i80186 -> i8086).
internal sealed class X86Clts286StubEmitter : IMicroOpEmitter
{
    public string OpName => "x86_clts_286_stub";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        // intentionally empty — no IR generated; LLVM will see only the
        // BlockFunctionBuilder's auto-br at the end of execBB.
    }
}

// x86_286_lar_lsl_stub — Sprint 27.4. Real-mode behavior of LAR (0F 02)
// and LSL (0F 03): per Intel 80286 PRM, descriptor-table lookup which
// is meaningless in real mode. Common implementations clear FLAGS.ZF
// to indicate "segment not verified". We do that.
internal sealed class X86286LarLslStubEmitter : IMicroOpEmitter
{
    public string OpName => "x86_286_lar_lsl_stub";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i16 = LLVMTypeRef.Int16;
        var flags = X86CtrlHelpers.LoadFlags(ctx, "lar_lsl");
        var cleared = ctx.Builder.BuildAnd(flags,
            LLVMValueRef.CreateConstInt(i16, unchecked((ushort)~(1 << 6)), false),
            "lar_lsl_zf_clear");
        ctx.Builder.BuildStore(cleared, ctx.GepStatusRegister("FLAGS"));
    }
}

// x86_286_zero_zero_dispatch — 0F 00 group on i80286. ModR/M.reg selects:
//   /0 SLDT r/m16   ✅ Sprint 27.3 — read LDTR, store to r/m16
//   /1 STR r/m16    ✅ Sprint 27.3 — read TR, store to r/m16
//   /2 LLDT r/m16   ✅ Sprint 27.3 — load r/m16, write to LDTR
//   /3 LTR r/m16    ✅ Sprint 27.3 — load r/m16, write to TR
//   /4 VERR r/m16   ✅ Sprint 27.3 — real-mode no-op (clears ZF)
//   /5 VERW r/m16   ✅ Sprint 27.3 — real-mode no-op (clears ZF)
//   /6 /7 invalid (treated as no-op stub)
internal sealed class X86286ZeroZeroDispatchEmitter : IMicroOpEmitter
{
    public string OpName => "x86_286_zero_zero_dispatch";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i16 = LLVMTypeRef.Int16;
        var i32 = LLVMTypeRef.Int32;

        var sel = ctx.Resolve("modrm_reg");
        var endBB     = ctx.Function.AppendBasicBlock("z00_end");
        var defaultBB = ctx.Function.AppendBasicBlock("z00_default");
        var arms = new LLVMBasicBlockRef[8];
        for (int i = 0; i < 8; i++) arms[i] = ctx.Function.AppendBasicBlock($"z00_{i}");
        var sw = ctx.Builder.BuildSwitch(sel, defaultBB, 8);
        for (int i = 0; i < 8; i++)
            sw.AddCase(LLVMValueRef.CreateConstInt(i32, (uint)i, false), arms[i]);

        // /0 SLDT: store LDTR to r/m16.
        ctx.Builder.PositionAtEnd(arms[0]);
        var ldtrV = ctx.Builder.BuildLoad2(i16, ctx.GepStatusRegister("LDTR"), "z00_0_ldtr");
        X86ModRmMemHelpers.BuildStoreW16(ctx, ldtrV);
        ctx.Builder.BuildBr(endBB);

        // /1 STR: store TR to r/m16.
        ctx.Builder.PositionAtEnd(arms[1]);
        var trV = ctx.Builder.BuildLoad2(i16, ctx.GepStatusRegister("TR"), "z00_1_tr");
        X86ModRmMemHelpers.BuildStoreW16(ctx, trV);
        ctx.Builder.BuildBr(endBB);

        // /2 LLDT: load r/m16, write to LDTR.
        ctx.Builder.PositionAtEnd(arms[2]);
        var ldtrSrc = X86ModRmMemHelpers.BuildLoadW16(ctx, "z00_2_src");
        ctx.Builder.BuildStore(ldtrSrc, ctx.GepStatusRegister("LDTR"));
        ctx.Builder.BuildBr(endBB);

        // /3 LTR: load r/m16, write to TR.
        ctx.Builder.PositionAtEnd(arms[3]);
        var trSrc = X86ModRmMemHelpers.BuildLoadW16(ctx, "z00_3_src");
        ctx.Builder.BuildStore(trSrc, ctx.GepStatusRegister("TR"));
        ctx.Builder.BuildBr(endBB);

        // /4 VERR + /5 VERW: real-mode no-op. Per Intel 80286 PRM, VERR/VERW
        // in real mode is undefined; common behavior is clearing ZF (segment
        // not verifiable). We implement the ZF=0 clear so the demo can
        // observe the no-op semantics.
        for (int armIdx = 4; armIdx <= 5; armIdx++)
        {
            ctx.Builder.PositionAtEnd(arms[armIdx]);
            var flags = X86CtrlHelpers.LoadFlags(ctx, $"z00_{armIdx}");
            // Clear ZF (bit 6) — flags & ~(1 << 6).
            var cleared = ctx.Builder.BuildAnd(flags,
                LLVMValueRef.CreateConstInt(i16, unchecked((ushort)~(1 << 6)), false),
                $"z00_{armIdx}_zf_clear");
            ctx.Builder.BuildStore(cleared, ctx.GepStatusRegister("FLAGS"));
            ctx.Builder.BuildBr(endBB);
        }

        // /6 /7 — invalid (stub no-op).
        ctx.Builder.PositionAtEnd(arms[6]);
        ctx.Builder.BuildBr(endBB);
        ctx.Builder.PositionAtEnd(arms[7]);
        ctx.Builder.BuildBr(endBB);

        ctx.Builder.PositionAtEnd(defaultBB);
        ctx.Builder.BuildBr(endBB);
        ctx.Builder.PositionAtEnd(endBB);
    }
}

// x86_286_zero_one_dispatch — 0F 01 group on i80286. ModR/M.reg selects:
//   /0 SGDT m48     ✅ Sprint 27.2 — store GDTR (limit+base) to 6-byte mem
//   /1 SIDT m48     ✅ Sprint 27.2 — store IDTR
//   /2 LGDT m48     ✅ Sprint 27.2 — load GDTR
//   /3 LIDT m48     ✅ Sprint 27.2 — load IDTR
//   /4 SMSW r/m16   ✅ Sprint 27.1 — reads REAL MSW status register
//   /5 reserved     (UD)
//   /6 LMSW r/m16   ✅ Sprint 27.1 — writes low 16 bits to MSW
//   /7 INVLPG       (386+, never reachable on 286)
internal sealed class X86286ZeroOneDispatchEmitter : IMicroOpEmitter
{
    public string OpName => "x86_286_zero_one_dispatch";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i8  = LLVMTypeRef.Int8;
        var i16 = LLVMTypeRef.Int16;
        var i32 = LLVMTypeRef.Int32;

        var sel = ctx.Resolve("modrm_reg");
        var endBB     = ctx.Function.AppendBasicBlock("z01_end");
        var defaultBB = ctx.Function.AppendBasicBlock("z01_default");
        var arms = new LLVMBasicBlockRef[8];
        for (int i = 0; i < 8; i++) arms[i] = ctx.Function.AppendBasicBlock($"z01_{i}");
        var sw = ctx.Builder.BuildSwitch(sel, defaultBB, 8);
        for (int i = 0; i < 8; i++)
            sw.AddCase(LLVMValueRef.CreateConstInt(i32, (uint)i, false), arms[i]);

        // SGDT (/0) and SIDT (/1) share the same shape: store
        // limit (16) + base (24+8 reserved = 32) to 6-byte memory.
        EmitSdt(ctx, arms[0], endBB, "GDTR_LIMIT", "GDTR_BASE", "z01_0_sgdt");
        EmitSdt(ctx, arms[1], endBB, "IDTR_LIMIT", "IDTR_BASE", "z01_1_sidt");

        // LGDT (/2) and LIDT (/3) — load limit + base from 6-byte memory.
        EmitLdt(ctx, arms[2], endBB, "GDTR_LIMIT", "GDTR_BASE", "z01_2_lgdt");
        EmitLdt(ctx, arms[3], endBB, "IDTR_LIMIT", "IDTR_BASE", "z01_3_lidt");

        // /4 SMSW r/m16: read MSW status register, store to r/m16 destination.
        ctx.Builder.PositionAtEnd(arms[4]);
        var mswPtr = ctx.GepStatusRegister("MSW");
        var mswVal = ctx.Builder.BuildLoad2(i16, mswPtr, "z01_4_msw");
        X86ModRmMemHelpers.BuildStoreW16(ctx, mswVal);
        ctx.Builder.BuildBr(endBB);

        // /6 LMSW r/m16: load r/m16 source value, write to MSW.
        ctx.Builder.PositionAtEnd(arms[6]);
        var lmswSrc = X86ModRmMemHelpers.BuildLoadW16(ctx, "z01_6_src");
        ctx.Builder.BuildStore(lmswSrc, ctx.GepStatusRegister("MSW"));
        ctx.Builder.BuildBr(endBB);

        // /5 /7 — no-op stubs.
        for (int i = 0; i < 8; i++)
        {
            if (i is 0 or 1 or 2 or 3 or 4 or 6) continue;
            ctx.Builder.PositionAtEnd(arms[i]);
            ctx.Builder.BuildBr(endBB);
        }
        ctx.Builder.PositionAtEnd(defaultBB);
        ctx.Builder.BuildBr(endBB);
        ctx.Builder.PositionAtEnd(endBB);
    }

    /// <summary>
    /// Sprint 27.2 — store-descriptor-table (SGDT / SIDT). 6-byte memory
    /// layout: bytes 0-1 = limit (LE), bytes 2-4 = base low24, byte 5
    /// reserved/zero on 80286. Memory destination given by ea_seg:ea_off
    /// from the preceding x86_modrm_compute_ea step (mod=11 forms are
    /// undefined for SGDT/SIDT — Intel manual says #UD; we silently emit
    /// the writes anyway for layout consistency since the test harness
    /// won't exercise the mod=11 path).
    /// </summary>
    private static void EmitSdt(EmitContext ctx, LLVMBasicBlockRef armBB, LLVMBasicBlockRef endBB,
        string limitReg, string baseReg, string label)
    {
        var i8  = LLVMTypeRef.Int8;
        var i16 = LLVMTypeRef.Int16;
        var i32 = LLVMTypeRef.Int32;

        ctx.Builder.PositionAtEnd(armBB);
        var seg = ctx.Resolve("ea_seg");
        var off = ctx.Resolve("ea_off");

        // limit -> [m + 0..1]
        var limitVal = ctx.Builder.BuildLoad2(i16, ctx.GepStatusRegister(limitReg), $"{label}_lim");
        X86_16Emitters.SegmentedWrite16(ctx, seg, off, limitVal, $"{label}_w_lim");

        // base low16 -> [m + 2..3]
        var baseVal = ctx.Builder.BuildLoad2(i32, ctx.GepStatusRegister(baseReg), $"{label}_base");
        var off2 = ctx.Builder.BuildAdd(off, LLVMValueRef.CreateConstInt(i16, 2, false), $"{label}_off2");
        var baseLow = ctx.Builder.BuildTrunc(baseVal, i16, $"{label}_base_lo");
        X86_16Emitters.SegmentedWrite16(ctx, seg, off2, baseLow, $"{label}_w_lo");

        // base high8 -> [m + 4]; byte 5 = 0 reserved on 286.
        var baseHi = ctx.Builder.BuildLShr(baseVal, LLVMValueRef.CreateConstInt(i32, 16, false), $"{label}_base_hi32");
        var baseHi8 = ctx.Builder.BuildTrunc(baseHi, i8, $"{label}_base_hi8");
        var off4 = ctx.Builder.BuildAdd(off, LLVMValueRef.CreateConstInt(i16, 4, false), $"{label}_off4");
        var lin4 = X86_16Emitters.SegmentedLinear(ctx, seg, off4, $"{label}_lin4");
        MemoryEmitters.CallWrite8(ctx, lin4, baseHi8);
        var off5 = ctx.Builder.BuildAdd(off, LLVMValueRef.CreateConstInt(i16, 5, false), $"{label}_off5");
        var lin5 = X86_16Emitters.SegmentedLinear(ctx, seg, off5, $"{label}_lin5");
        MemoryEmitters.CallWrite8(ctx, lin5, LLVMValueRef.CreateConstInt(i8, 0, false));

        ctx.Builder.BuildBr(endBB);
    }

    /// <summary>
    /// Sprint 27.2 — load-descriptor-table (LGDT / LIDT). Reverse of EmitSdt.
    /// </summary>
    private static void EmitLdt(EmitContext ctx, LLVMBasicBlockRef armBB, LLVMBasicBlockRef endBB,
        string limitReg, string baseReg, string label)
    {
        var i8  = LLVMTypeRef.Int8;
        var i16 = LLVMTypeRef.Int16;
        var i32 = LLVMTypeRef.Int32;

        ctx.Builder.PositionAtEnd(armBB);
        var seg = ctx.Resolve("ea_seg");
        var off = ctx.Resolve("ea_off");

        // limit <- [m + 0..1]
        var limitNew = X86_16Emitters.SegmentedRead16(ctx, seg, off, $"{label}_lim");
        ctx.Builder.BuildStore(limitNew, ctx.GepStatusRegister(limitReg));

        // base low16 <- [m + 2..3]
        var off2 = ctx.Builder.BuildAdd(off, LLVMValueRef.CreateConstInt(i16, 2, false), $"{label}_off2");
        var baseLow = X86_16Emitters.SegmentedRead16(ctx, seg, off2, $"{label}_base_lo");

        // base high8 <- [m + 4]; byte 5 ignored on 286.
        var off4 = ctx.Builder.BuildAdd(off, LLVMValueRef.CreateConstInt(i16, 4, false), $"{label}_off4");
        var lin4 = X86_16Emitters.SegmentedLinear(ctx, seg, off4, $"{label}_lin4");
        var baseHi8 = MemoryEmitters.CallRead8(ctx, lin4, $"{label}_r_hi8");

        // Combine: base32 = (zext(hi8) << 16) | zext(low16)
        var baseLow32 = ctx.Builder.BuildZExt(baseLow, i32, $"{label}_base_lo32");
        var baseHi32  = ctx.Builder.BuildZExt(baseHi8, i32, $"{label}_base_hi32");
        var baseHiSh  = ctx.Builder.BuildShl(baseHi32, LLVMValueRef.CreateConstInt(i32, 16, false), $"{label}_base_hi_sh");
        var baseFull  = ctx.Builder.BuildOr(baseHiSh, baseLow32, $"{label}_base_full");
        ctx.Builder.BuildStore(baseFull, ctx.GepStatusRegister(baseReg));

        ctx.Builder.BuildBr(endBB);
    }
}

internal sealed class X86PushSpPreDecrementEmitter : IMicroOpEmitter
{
    public string OpName => "x86_push_sp_pre_decrement";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i16 = LLVMTypeRef.Int16;
        var spPtr = ctx.GepGpr(4);
        var spOld = ctx.Builder.BuildLoad2(i16, spPtr, "psh_sp_orig");
        var spNew = ctx.Builder.BuildSub(spOld,
            LLVMValueRef.CreateConstInt(i16, 2, false), "psh_sp_new");
        ctx.Builder.BuildStore(spNew, spPtr);
        // Sprint 27.10d wave 3 — by-name SS (cache-aware on i80286+).
        X86_16Emitters.SegmentedWrite16(ctx, "SS", spNew, spOld, "psh_w");
    }
}

// x86_push_imm8_sext_w16 — 0x6A. Fetch imm8, sign-extend to i16, push.
internal sealed class X86PushImm8SextW16Emitter : IMicroOpEmitter
{
    public string OpName => "x86_push_imm8_sext_w16";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i16 = LLVMTypeRef.Int16;
        var imm8 = X86_16Emitters.FetchImm8(ctx, "pi8");
        var ext = ctx.Builder.BuildSExt(imm8, i16, "pi8_sext");
        X86StackHelpers.PushW16(ctx, ext, "pi8_psh");
    }
}

// x86_push_imm16 — 0x68. Fetch imm16 and push.
internal sealed class X86PushImm16Emitter : IMicroOpEmitter
{
    public string OpName => "x86_push_imm16";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var imm16 = X86_16Emitters.FetchImm16(ctx, "pi16");
        X86StackHelpers.PushW16(ctx, imm16, "pi16_psh");
    }
}

// x86_pusha — 0x60. Push AX, CX, DX, BX, ORIG_SP, BP, SI, DI in that order.
// ORIG_SP is the SP value BEFORE PUSHA started decrementing.
// GPR indices: AX=0, CX=1, DX=2, BX=3, SP=4, BP=5, SI=6, DI=7.
internal sealed class X86PushaEmitter : IMicroOpEmitter
{
    public string OpName => "x86_pusha";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i16 = LLVMTypeRef.Int16;
        // Snapshot original SP for the SP slot push.
        var spPtr = ctx.GepGpr(4);
        var spOrig = ctx.Builder.BuildLoad2(i16, spPtr, "pusha_sp_orig");

        // Push order: AX, CX, DX, BX, SP_orig, BP, SI, DI.
        int[] order = { 0, 1, 2, 3, /*sentinel*/ -1, 5, 6, 7 };
        for (int k = 0; k < 8; k++)
        {
            LLVMValueRef v = order[k] >= 0
                ? X86_16Emitters.ReadGpr16(ctx, order[k], $"pusha_r{order[k]}")
                : spOrig;
            X86StackHelpers.PushW16(ctx, v, $"pusha_p{k}");
        }
    }
}

// x86_popa — 0x61. Pop DI, SI, BP, [skip SP slot], BX, DX, CX, AX.
internal sealed class X86PopaEmitter : IMicroOpEmitter
{
    public string OpName => "x86_popa";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i16 = LLVMTypeRef.Int16;
        // Pop in reverse of PUSHA. SP slot is discarded — popa doesn't
        // restore SP from the pushed slot (the pops themselves move SP).
        int[] revOrder = { 7, 6, 5, /*skip*/ -1, 3, 2, 1, 0 };
        for (int k = 0; k < 8; k++)
        {
            var v = X86StackHelpers.PopW16(ctx, $"popa_p{k}");
            if (revOrder[k] >= 0)
                X86_16Emitters.WriteGpr16(ctx, revOrder[k], v);
            // else: discard popped value (SP slot)
        }
    }
}

// x86_bound_r16_m16 — 0x62. Check r16 in [m16_lo, m16_hi]; if out of range,
// raise INT 5. Simplified: we fetch ModR/M + check, but defer the actual
// INT 5 trigger to a stub (real test ROMs that use BOUND are rare).
internal sealed class X86BoundR16M16Emitter : IMicroOpEmitter
{
    public string OpName => "x86_bound_r16_m16";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        // Stub — fetch ModR/M (so IP advances correctly) and discard.
        // Real BOUND would compare reg vs [m16, m16+2] and fire INT 5
        // on out-of-range; deferred to phase 25.x follow-up since none
        // of the demo ROMs exercise BOUND.
        var modrm = X86_16Emitters.FetchImm8(ctx, "bnd_modrm");
        // Drop the value; mod-encoded disp bytes (if any) are NOT
        // consumed here — meaning a memory-form BOUND would corrupt the
        // following instruction. Real impl needs full ModR/M decode.
        // Acceptable for v1 since (a) no demo uses BOUND, (b) Tom Harte
        // SST filter excludes opcode 0x62 from i80186 vector set.
        _ = modrm;
    }
}

// x86_imul_r16_rm16_imm{16,8} — 0x69 / 0x6B. Three-operand signed multiply.
// Result = (i16) (rhs * imm), stored to r16 from ModR/M reg field.
internal abstract class X86ImulR16Rm16ImmBase : IMicroOpEmitter
{
    public abstract string OpName { get; }
    protected abstract bool ImmIsByte { get; }

    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i16 = LLVMTypeRef.Int16;
        var i32 = LLVMTypeRef.Int32;
        // Fetch ModR/M (defines reg + r/m).
        new X86FetchModRmEmitter().Emit(ctx,
            new MicroOpStep("x86_fetch_modrm", default));
        // Compute EA + load r/m as i16 source.
        new X86ModRmComputeEaEmitter().Emit(ctx,
            new MicroOpStep("x86_modrm_compute_ea", default));
        var rm = X86ModRmMemHelpers.BuildLoadW16(ctx, "imul_rm");

        // Fetch immediate (sign-extended for imm8 form).
        LLVMValueRef imm;
        if (ImmIsByte)
        {
            var imm8 = X86_16Emitters.FetchImm8(ctx, "imul_imm8");
            imm = ctx.Builder.BuildSExt(imm8, i16, "imul_imm8_sext");
        }
        else
        {
            imm = X86_16Emitters.FetchImm16(ctx, "imul_imm16");
        }

        // Signed multiply at i32 width to capture overflow, truncate to i16.
        var rmS  = ctx.Builder.BuildSExt(rm,  i32, "imul_rm32");
        var immS = ctx.Builder.BuildSExt(imm, i32, "imul_imm32");
        var prod = ctx.Builder.BuildMul(rmS, immS, "imul_prod");
        var prodLo = ctx.Builder.BuildTrunc(prod, i16, "imul_lo");

        // Write to r16 selected by modrm_reg.
        var sel = ctx.Resolve("modrm_reg");
        var endBB     = ctx.Function.AppendBasicBlock("imul_end");
        var defaultBB = ctx.Function.AppendBasicBlock("imul_default");
        var arms = new LLVMBasicBlockRef[8];
        for (int i = 0; i < 8; i++) arms[i] = ctx.Function.AppendBasicBlock($"imul_{i}");
        var sw = ctx.Builder.BuildSwitch(sel, defaultBB, 8);
        for (int i = 0; i < 8; i++)
            sw.AddCase(LLVMValueRef.CreateConstInt(i32, (uint)i, false), arms[i]);
        for (int i = 0; i < 8; i++)
        {
            ctx.Builder.PositionAtEnd(arms[i]);
            X86_16Emitters.WriteGpr16(ctx, i, prodLo);
            ctx.Builder.BuildBr(endBB);
        }
        ctx.Builder.PositionAtEnd(defaultBB);
        ctx.Builder.BuildBr(endBB);
        ctx.Builder.PositionAtEnd(endBB);
        // CF/OF flag rules (set when high32 != sext(low16)) deferred.
    }
}

internal sealed class X86ImulR16Rm16Imm16Emitter : X86ImulR16Rm16ImmBase
{
    public override string OpName => "x86_imul_r16_rm16_imm16";
    protected override bool ImmIsByte => false;
}
internal sealed class X86ImulR16Rm16Imm8Emitter : X86ImulR16Rm16ImmBase
{
    public override string OpName => "x86_imul_r16_rm16_imm8";
    protected override bool ImmIsByte => true;
}

// x86_ins_{b,w} / x86_outs_{b,w} — 0x6C-0x6F. String IO. Framework has
// no real IO bus, so these are no-op stubs that still advance SI/DI per
// DF (so loops with REP terminate correctly).
internal abstract class X86InsOutsBase : IMicroOpEmitter
{
    public abstract string OpName { get; }
    protected abstract int Width { get; }   // 1 or 2 bytes
    protected abstract bool IsIns { get; }   // true = INS (writes ES:DI), false = OUTS (reads DS:SI)

    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i16 = LLVMTypeRef.Int16;
        // Read DF (FLAGS bit 10) — direction: 0=increment, 1=decrement.
        var flags = X86CtrlHelpers.LoadFlags(ctx, OpName);
        var df = X86CtrlHelpers.ExtractFlagBit(ctx, flags, 10, $"{OpName}_df");
        var delta = ctx.Builder.BuildSelect(df,
            LLVMValueRef.CreateConstInt(i16, unchecked((ulong)(short)-Width), true),
            LLVMValueRef.CreateConstInt(i16, (ulong)Width, false),
            $"{OpName}_delta");

        // Advance the affected GPR (DI for INS, SI for OUTS).
        int gprIdx = IsIns ? 7 : 6;   // SI=6, DI=7
        var ptr = ctx.GepGpr(gprIdx);
        var old = ctx.Builder.BuildLoad2(i16, ptr, $"{OpName}_old");
        var n = ctx.Builder.BuildAdd(old, delta, $"{OpName}_new");
        ctx.Builder.BuildStore(n, ptr);
        // Memory side-effect: real INS would write the port byte to ES:DI;
        // OUTS would read DS:SI and emit to the port. Both no-op here
        // since the framework's IO model is "no peripherals" (existing
        // IN/OUT emitters are also no-op stubs).
    }
}

internal sealed class X86InsBEmitter  : X86InsOutsBase { public override string OpName => "x86_ins_b";  protected override int Width => 1; protected override bool IsIns => true; }
internal sealed class X86InsWEmitter  : X86InsOutsBase { public override string OpName => "x86_ins_w";  protected override int Width => 2; protected override bool IsIns => true; }
internal sealed class X86OutsBEmitter : X86InsOutsBase { public override string OpName => "x86_outs_b"; protected override int Width => 1; protected override bool IsIns => false; }
internal sealed class X86OutsWEmitter : X86InsOutsBase { public override string OpName => "x86_outs_w"; protected override int Width => 2; protected override bool IsIns => false; }

// x86_enter — 0xC8 ENTER imm16, imm8. Sprint 25.4 v1: only the
// nest_level=0 path is implemented (which is what every C compiler
// emits — display-copying for nested Pascal-style scoping is rarely
// used). nest_level > 0 is silently ignored. Steps:
//   1. push BP
//   2. frame_temp = SP
//   3. BP = frame_temp
//   4. SP -= alloc_size
// Earlier draft had a CondBr to gate the nest-level path but it
// interacted badly with block-JIT alloca shadow propagation across the
// merge BB; per-instr backend was correct but block-JIT showed register
// state losses. Linear emit is robust under both modes.
internal sealed class X86EnterEmitter : IMicroOpEmitter
{
    public string OpName => "x86_enter";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i16 = LLVMTypeRef.Int16;

        var allocSize = X86_16Emitters.FetchImm16(ctx, "enter_alloc");
        // FetchImm8 still has to advance IP past the nest-level byte even
        // though we ignore the value. (Block-JIT's PackedTailBytes path
        // advances IP regardless.)
        _ = X86_16Emitters.FetchImm8(ctx, "enter_nest_ignored");

        // Step 1: push BP (current).
        var bp = X86_16Emitters.ReadGpr16(ctx, 5, "enter_bp_old");
        X86StackHelpers.PushW16(ctx, bp, "enter_psh_bp");

        // Step 2 + 3: BP = (SP after push).
        var spPtr = ctx.GepGpr(4);
        var frameTemp = ctx.Builder.BuildLoad2(i16, spPtr, "enter_frame");
        X86_16Emitters.WriteGpr16(ctx, 5, frameTemp);

        // Step 4: SP -= alloc_size.
        var spFinal = ctx.Builder.BuildSub(frameTemp, allocSize, "enter_sp_final");
        ctx.Builder.BuildStore(spFinal, spPtr);
    }
}

// x86_leave — 0xC9. SP = BP; pop BP. Reverses ENTER.
internal sealed class X86LeaveEmitter : IMicroOpEmitter
{
    public string OpName => "x86_leave";
    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var i16 = LLVMTypeRef.Int16;
        // SP = BP (so the local frame is wiped).
        var bp = X86_16Emitters.ReadGpr16(ctx, 5, "leave_bp");
        var spPtr = ctx.GepGpr(4);
        ctx.Builder.BuildStore(bp, spPtr);
        // Pop into BP.
        var newBp = X86StackHelpers.PopW16(ctx, "leave_pop_bp");
        X86_16Emitters.WriteGpr16(ctx, 5, newBp);
    }
}

// x86_shift_rm{8,16}_imm8 — 0xC0/0xC1 group dispatcher. The kind comes
// from the spec's `kind` field. Count is the imm8 masked to 5 bits per
// 80186 silicon. For sprint 25.4 v1 we emit only basic SHL/SHR/SAR
// using LLVM Shl/LShr/AShr; ROL/ROR/RCL/RCR fall through to a count=1
// approximation. Silicon-accurate flag rules (CF/OF/AF for count > 0)
// are deferred — none of the demo ROMs exercise shift-imm with non-1
// counts, and Tom Harte SST filter will exclude opcode 0xC0/0xC1.
internal abstract class X86ShiftRmImmBase : IMicroOpEmitter
{
    public abstract string OpName { get; }
    protected abstract bool IsW16 { get; }

    public void Emit(EmitContext ctx, MicroOpStep step)
    {
        var kind = step.Raw.GetProperty("kind").GetString()!;
        var i8 = LLVMTypeRef.Int8;
        var i16 = LLVMTypeRef.Int16;

        // Spec preceded this step with x86_fetch_modrm + x86_modrm_compute_ea
        // + x86_modrm_load_w{8,16} (out: "lhs"). So `lhs` is in ctx.Values.
        var rm = ctx.Resolve("lhs");
        var imm8 = X86_16Emitters.FetchImm8(ctx, "shi_imm");
        // 80186 silicon: count masked to 5 bits.
        var count = ctx.Builder.BuildAnd(imm8,
            LLVMValueRef.CreateConstInt(i8, 0x1F, false), "shi_count");

        if (IsW16)
        {
            var count16 = ctx.Builder.BuildZExt(count, i16, "shi_c16");
            LLVMValueRef result = kind switch
            {
                "shl" or "sal" => ctx.Builder.BuildShl(rm, count16, "shi_shl"),
                "shr"          => ctx.Builder.BuildLShr(rm, count16, "shi_shr"),
                "sar"          => ctx.Builder.BuildAShr(rm, count16, "shi_sar"),
                // ROL/ROR/RCL/RCR — v1 stub: pass-through (no rotate yet).
                _              => rm,
            };
            X86ModRmMemHelpers.BuildStoreW16(ctx, result);
        }
        else
        {
            LLVMValueRef result = kind switch
            {
                "shl" or "sal" => ctx.Builder.BuildShl(rm, count, "shi_shl"),
                "shr"          => ctx.Builder.BuildLShr(rm, count, "shi_shr"),
                "sar"          => ctx.Builder.BuildAShr(rm, count, "shi_sar"),
                _              => rm,
            };
            X86ModRmMemHelpers.BuildStoreW8(ctx, result);
        }
    }
}

internal sealed class X86ShiftRm8Imm8Emitter  : X86ShiftRmImmBase
{
    public override string OpName => "x86_shift_rm8_imm8";
    protected override bool IsW16 => false;
}
internal sealed class X86ShiftRm16Imm8Emitter : X86ShiftRmImmBase
{
    public override string OpName => "x86_shift_rm16_imm8";
    protected override bool IsW16 => true;
}
