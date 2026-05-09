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
