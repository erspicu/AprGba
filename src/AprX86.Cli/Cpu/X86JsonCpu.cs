// JSON-spec-driven Intel 8086 backend.
//
// 24.6.4 — per-instruction skeleton. Mirrors NesJsonCpu: loads
// spec/cpu/x86-16/i8086/cpu.json, runs SpecCompiler over it, wires the LLVM
// module through HostRuntime, JIT-compiles, and dispatches one opcode
// per Step() call.
//
// Coverage at this sub-step is intentionally minimal — only the
// 24.6.2 smoke group (NOP + HLT) is wired. Step() works for opcodes
// the smoke group covers; everything else returns "no decode" (gives
// the caller a chance to fall through to legacy or extend the spec).
//
// Real coverage (MOV / ALU / control flow / shift / string ops / misc)
// expands in 24.6.5..24.6.7.
//
// Two future modes (per the doc #24 plan):
//   1. Per-instruction (this file, default)
//   2. Block-JIT — added in 24.6.8 once the per-instr path is stable
//      Tom Harte across all opcodes.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AprCpu.Core.Compilation;
using AprCpu.Core.Decoder;
using AprCpu.Core.IR;
using AprCpu.Core.JsonSpec;
using AprCpu.Core.Runtime;
using AprX86.Cli.Memory;
using LLVMSharp.Interop;

namespace AprX86.Cli.Cpu;

public sealed unsafe class X86JsonCpu : IX86CpuBackend
{
    public string BackendName => _blockJitEnabled ? "json-block-llvm" : "json-llvm";

    // Static memory reference used by the unmanaged extern shims. Last-call-
    // wins when multiple X86JsonCpu instances exist; the harness only ever
    // constructs one at a time.
    private static X86Memory? _activeMem;
    // Phase 30.15 — static back-reference for SMC notify. The JIT-emitted
    // memory-write extern + DMA / FDC direct VRAM writers go through this
    // to tell BlockCache that compiled translations at the affected
    // addresses may be stale. Without this, real-BIOS + block-JIT diverges
    // when FreeDOS kernel loads via FDC DMA: the first time CPU enters
    // 1FE0:???? the JIT cached the all-zeros translation, and later code
    // execution at the same PC re-uses that stale block.
    private static X86JsonCpu? _activeCpu;

    /// <summary>
    /// Phase 30.15 — external SMC notify hook. Called by FDC / DMA paths
    /// in AprPc.Cli that write directly to <c>X86Memory.Ram</c> (i.e.,
    /// bypass the <c>MemWrite8</c> extern). For each byte that hits the
    /// shared RAM, invoke this so any block-JIT cached translation
    /// covering that address is invalidated and re-compiled on the next
    /// dispatch. No-op when block-JIT is disabled.
    /// </summary>
    public static void NotifyExternalMemoryWrite(uint addr)
        => _activeCpu?._blockCache?.NotifyMemoryWrite(addr);

    /// <summary>
    /// Phase 30.15b — install this CPU + its memory as the singleton
    /// "active" pair seen by the unmanaged extern shims (MemRead8 /
    /// MemWrite8 / etc.). Required for the lockstep harness which
    /// alternates Step() calls between two CPU instances; without
    /// re-pointing the singletons each iteration, the wrong memory
    /// would back the JIT-emitted load/store calls and writes would
    /// land on the wrong instance.
    /// </summary>
    public void SetActiveForLockstep()
    {
        _activeMem = _mem;
        _activeCpu = this;
    }

    /// <summary>
    /// Phase 30.15d sprint 5.3 — active trace sink for the Verified
    /// Block-JIT framework. When non-null, MemWrite8 / PortWrite8 also
    /// append to the sink so the framework can compare JIT-vs-interp
    /// at block boundaries. Null = no overhead.
    /// </summary>
    public static AprCpu.Core.Validation.IBlockTraceSink? ActiveTraceSink { get; set; }

    /// <summary>
    /// Phase 30.15d sprint 5.3 — most-recent block size (architectural
    /// instructions executed). For per-instr mode this is always 1.
    /// For block-JIT this is the block.Instructions.Count of the
    /// most-recently-run block. -1 if no block has ever run.
    /// </summary>
    public int LastBlockInstructionCount { get; private set; } = -1;

    /// <summary>
    /// Phase 30.15d sprint 5.3 — force a single per-instruction step,
    /// bypassing block-JIT regardless of <see cref="_blockJitEnabled"/>.
    /// Used by VerifiedBlockJitRunner to drive the interp side N times
    /// to mirror the JIT's block.
    /// </summary>
    public int StepOnePerInstr()
    {
        _activeMem = _mem;
        _activeCpu = this;
        if (Halted) { LastBlockInstructionCount = 0; return 0; }
        var rc = StepOne();
        LastBlockInstructionCount = 1;   // per-instr = 1 arch instruction
        return rc;
    }

    private readonly X86Memory                  _mem;
    private readonly LoadedSpec                  _spec;
    private readonly HostRuntime                 _rt;
    private readonly DecoderTable                _mainDecoder;
    private readonly SpecCompiler.CompileResult  _compileResult;

    // 24.6.8 — block-JIT (optional, opt-in via ctor flag).
    private readonly bool                        _blockJitEnabled;
    private readonly BlockDetector?              _blockDetector;
    private readonly BlockCache?                 _blockCache;
    private int                                  _blockGeneration;

    // Identity-keyed function-pointer cache (InstructionDef → fn ptr).
    private readonly Dictionary<InstructionDef, IntPtr> _fnPtrByDef
        = new(System.Collections.Generic.ReferenceEqualityComparer.Instance);

    private byte[]   _state = Array.Empty<byte>();
    private GCHandle _stateHandle;
    private byte*    _statePtr;

    // Pre-cached field offsets — order matches cpu.json's GPR list
    // (AX/CX/DX/BX/SP/BP/SI/DI in ModR/M order) and the status section.
    private readonly int _axOff, _cxOff, _dxOff, _bxOff;
    private readonly int _spOff, _bpOff, _siOff, _diOff;
    private readonly int _flagsOff, _ipOff, _esOff, _csOff, _ssOff, _dsOff, _haltedOff, _segOverrideOff;
    private readonly int _cyclesLeftOff;

    // Sprint 27.11b — i80286 exception slots. -1 sentinel when the loaded
    // spec doesn't declare them (i8086 / i80186); State getter then
    // reports 0 / 0 / 0.
    private readonly int _excPendingOff, _excVectorOff, _excErrorOff;

    public X86Memory Memory => _mem;

    /// <summary>
    /// 25.5 — variant string selects which spec directory to load.
    /// "i8086" (default): spec/cpu/x86-16/i8086/cpu.json (base spec).
    /// "i80186" / "i80188": spec/cpu/x86-16/i80186/cpu.json (extends i8086;
    /// loaded via inheritance resolution).
    /// </summary>
    public string Variant { get; }

    public X86JsonCpu(X86Memory memory, bool enableBlockJit = false, string variant = "i8086",
        IReadOnlyList<string>? extensionPaths = null)
    {
        _mem = memory ?? throw new ArgumentNullException(nameof(memory));
        _blockJitEnabled = enableBlockJit;
        Variant = variant;

        var specPath = LocateSpec(variant);
        // N29.1 — when the consumer (AprPc.Cli) loaded a machine spec
        // declaring extensions (e.g. x87 8087), pass them through so the
        // merged compile result includes their opcode groups. Empty /
        // null behaves like a bare CPU socket — same path as before 29.1.
        var compileResult = extensionPaths is null || extensionPaths.Count == 0
            ? SpecCompiler.Compile(specPath)
            : SpecCompiler.Compile(specPath, extensionPaths);
        if (compileResult.Diagnostics.Count != 0)
        {
            var bad = new List<string>();
            foreach (var d in compileResult.Diagnostics)
                if (!d.StartsWith("[warn]")) bad.Add(d);
            if (bad.Count != 0)
            {
                throw new InvalidOperationException(
                    "X86JsonCpu: spec compilation produced errors:\n  " +
                    string.Join("\n  ", bad));
            }
        }
        _compileResult = compileResult;

        _spec = extensionPaths is null || extensionPaths.Count == 0
            ? SpecLoader.LoadCpuSpec(specPath)
            : SpecLoader.LoadCpuSpecWithExtensions(specPath, extensionPaths);
        if (!compileResult.DecoderTables.TryGetValue("Main", out var mainDecoder))
        {
            throw new InvalidOperationException(
                "X86JsonCpu: spec must declare a 'Main' instruction set.");
        }
        _mainDecoder = mainDecoder;

        _rt = HostRuntime.Build(compileResult.Module,
            new CpuStateLayout(
                compileResult.Module.Context,
                _spec.Cpu.RegisterFile,
                _spec.Cpu.ProcessorModes,
                _spec.Cpu.ExceptionVectors));

        _rt.BindExtern(MemoryEmitters.ExternFunctionNames.Read8,
            (IntPtr)(delegate* unmanaged[Cdecl]<uint, byte>)&MemRead8);
        _rt.BindExtern(MemoryEmitters.ExternFunctionNames.Write8,
            (IntPtr)(delegate* unmanaged[Cdecl]<uint, byte, void>)&MemWrite8);
        // Phase 28.IO — port I/O dispatch (delegates to AprPc.Cli.Hardware.PcPortBus.Active).
        _rt.BindExtern(MemoryEmitters.ExternFunctionNames.PortRead8,
            (IntPtr)(delegate* unmanaged[Cdecl]<ushort, byte>)&PortRead8);
        _rt.BindExtern(MemoryEmitters.ExternFunctionNames.PortRead16,
            (IntPtr)(delegate* unmanaged[Cdecl]<ushort, ushort>)&PortRead16);
        _rt.BindExtern(MemoryEmitters.ExternFunctionNames.PortWrite8,
            (IntPtr)(delegate* unmanaged[Cdecl]<ushort, byte, void>)&PortWrite8);
        _rt.BindExtern(MemoryEmitters.ExternFunctionNames.PortWrite16,
            (IntPtr)(delegate* unmanaged[Cdecl]<ushort, ushort, void>)&PortWrite16);

        // Phase 29.7 — FPU transcendentals via C# Math.* externs. Bound
        // unconditionally; if no FPU extension is loaded the slot exists
        // but is never called (no D9 F0-F3 in the spec).
        _rt.BindExtern(MemoryEmitters.ExternFunctionNames.FpuTan,
            (IntPtr)(delegate* unmanaged[Cdecl]<double, double>)&FpuTan);
        _rt.BindExtern(MemoryEmitters.ExternFunctionNames.FpuAtan2,
            (IntPtr)(delegate* unmanaged[Cdecl]<double, double, double>)&FpuAtan2);
        _rt.BindExtern(MemoryEmitters.ExternFunctionNames.FpuLog2,
            (IntPtr)(delegate* unmanaged[Cdecl]<double, double>)&FpuLog2);
        _rt.BindExtern(MemoryEmitters.ExternFunctionNames.FpuExp2M1,
            (IntPtr)(delegate* unmanaged[Cdecl]<double, double>)&FpuExp2M1);

        _rt.Compile();

        // GPR offsets in ModR/M order (cpu.json declares them so).
        _axOff = (int)_rt.GprOffset(0);
        _cxOff = (int)_rt.GprOffset(1);
        _dxOff = (int)_rt.GprOffset(2);
        _bxOff = (int)_rt.GprOffset(3);
        _spOff = (int)_rt.GprOffset(4);
        _bpOff = (int)_rt.GprOffset(5);
        _siOff = (int)_rt.GprOffset(6);
        _diOff = (int)_rt.GprOffset(7);

        _flagsOff       = (int)_rt.StatusOffset("FLAGS");
        _ipOff          = (int)_rt.StatusOffset("IP");
        _esOff          = (int)_rt.StatusOffset("ES");
        _csOff          = (int)_rt.StatusOffset("CS");
        _ssOff          = (int)_rt.StatusOffset("SS");
        _dsOff          = (int)_rt.StatusOffset("DS");
        _haltedOff      = (int)_rt.StatusOffset("HALTED");
        _segOverrideOff = (int)_rt.StatusOffset("SEG_OVERRIDE");
        _cyclesLeftOff  = (int)_rt.CyclesLeftOffset;

        // Sprint 27.11b — exception state slots (i80286 only). -1 when the
        // loaded spec doesn't declare them (i8086 / i80186 path).
        _excPendingOff = TryStatusOffset("EXC_PENDING");
        _excVectorOff  = TryStatusOffset("EXC_VECTOR");
        _excErrorOff   = TryStatusOffset("EXC_ERROR");

        _state = new byte[(int)_rt.StateSizeBytes];
        _stateHandle = GCHandle.Alloc(_state, GCHandleType.Pinned);
        _statePtr    = (byte*)_stateHandle.AddrOfPinnedObject();

        // Initialize SEG_OVERRIDE to 0xFF (no override). The Reset() path
        // zeroes the buffer, so seed the default here to keep the EA
        // emitter's "no override unless seen" invariant.
        _state[_segOverrideOff] = 0xFF;

        // 24.6.8 — block-JIT setup (opt-in).
        if (_blockJitEnabled)
        {
            var mainSetSpec = _spec.InstructionSets["Main"];

            // 26.2b — i80286 onward: 0x0F is a two-byte-opcode escape
            // prefix; second byte dispatches through a TwoByteEsc set.
            // i8086 / i80186 don't have this set and ignore the wiring.
            Dictionary<byte, DecoderTable>? prefixSubDecoders = null;
            if (compileResult.DecoderTables.TryGetValue("TwoByteEsc", out var escDec))
            {
                prefixSubDecoders = new Dictionary<byte, DecoderTable> { { 0x0F, escDec } };
            }

            _blockDetector = new BlockDetector(
                mainSetSpec,
                _mainDecoder,
                busLengthOracle: X86_16InstructionLengths.GetLength,
                prefixSubDecoders: prefixSubDecoders);
            _blockCache = new BlockCache();
        }
    }

    private static string LocateSpec(string variant = "i8086")
    {
        // 25.5 — i8088 is ISA-identical to i8086 (cycle-only difference,
        // not modeled at the spec level). i80188 is ISA-identical to
        // i80186. Fold both into the single base / inheritance spec.
        string subdir = variant switch
        {
            "i8086" or "i8088"            => "i8086",
            "i80186" or "i80188"          => "i80186",
            "i80286"                      => "i80286",
            _ => throw new ArgumentException(
                $"X86JsonCpu: unknown variant '{variant}'. Valid: i8086 | i8088 | i80186 | i80188 | i80286.",
                nameof(variant)),
        };

        var dir = AppContext.BaseDirectory;
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
        {
            var probe = Path.Combine(d.FullName, "spec", "cpu", "x86-16", subdir, "cpu.json");
            if (File.Exists(probe)) return probe;
        }
        var cwdProbe = Path.Combine(Environment.CurrentDirectory, "spec", "cpu", "x86-16", subdir, "cpu.json");
        if (File.Exists(cwdProbe)) return cwdProbe;
        throw new FileNotFoundException(
            $"X86JsonCpu: cannot locate spec/cpu/x86-16/{subdir}/cpu.json. Run from repo root.");
    }

    // --- IX86CpuBackend surface -------------------------------------------

    public void Reset()
    {
        // 8086 RESET: CS=0xFFFF, IP=0, all GPRs/segs/flags=0.
        Array.Clear(_state, 0, _state.Length);
        WriteU16(_csOff, 0xFFFF);
        // FLAGS, IP, others already cleared.
        _state[_segOverrideOff] = 0xFF;     // no segment override at reset

        // Sprint 27.1 — i80286 only: MSW resets to 0xFFF0 (top 4 bits
        // stuck set per Intel 80286 PRM). i8086 / i80186 don't have the
        // MSW status register so the offset query returns -1 sentinel
        // (or the spec layout simply lacks the slot).
        try
        {
            var mswOff = (int)_rt.StatusOffset("MSW");
            WriteU16(mswOff, 0xFFF0);
        }
        catch (KeyNotFoundException) { /* spec doesn't declare MSW (i8086/i80186) — no-op */ }
        catch (ArgumentException)    { /* same */ }
        catch (InvalidOperationException) { /* CpuStateLayout throws this for unknown status reg */ }

        // Sprint 27.10b — i80286: initialize hidden segment-register
        // caches from visible selector values (real-mode shift-and-add).
        // After Reset(): CS=0xFFFF means CS_BASE = 0xFFFF0; ES/SS/DS=0
        // means BASE=0. All limits = 0xFFFF; access rights = real-mode
        // defaults (code/data writable).
        InitSegmentCache("ES", 0x0000, 0xFFFF, 0x93);
        InitSegmentCache("CS", 0xFFFF, 0xFFFF, 0x9B);
        InitSegmentCache("SS", 0x0000, 0xFFFF, 0x93);
        InitSegmentCache("DS", 0x0000, 0xFFFF, 0x93);

        _activeMem = _mem;
        _activeCpu = this;
    }

    /// <summary>
    /// Sprint 27.10b — write the hidden cache slots for a single
    /// segment register, using the real-mode (selector &lt;&lt; 4) base.
    /// No-op when the loaded spec doesn't declare these slots
    /// (i8086 / i80186).
    /// </summary>
    private void InitSegmentCache(string segName, ushort sel, ushort limit, byte access)
    {
        try
        {
            int baseOff = (int)_rt.StatusOffset(segName + "_BASE");
            uint baseAddr = (uint)sel << 4;
            _state[baseOff + 0] = (byte)(baseAddr & 0xFF);
            _state[baseOff + 1] = (byte)((baseAddr >> 8) & 0xFF);
            _state[baseOff + 2] = (byte)((baseAddr >> 16) & 0xFF);
            _state[baseOff + 3] = (byte)((baseAddr >> 24) & 0xFF);

            int limOff = (int)_rt.StatusOffset(segName + "_LIMIT");
            _state[limOff + 0] = (byte)(limit & 0xFF);
            _state[limOff + 1] = (byte)((limit >> 8) & 0xFF);

            int accOff = (int)_rt.StatusOffset(segName + "_ACCESS");
            _state[accOff] = access;
        }
        catch (KeyNotFoundException) { /* i8086 / i80186: no cache slots */ }
        catch (ArgumentException)    { /* same */ }
        catch (InvalidOperationException) { /* CpuStateLayout throws this */ }
    }

    public void SetEntryPoint(ushort segment, ushort offset)
    {
        WriteU16(_csOff, segment);
        WriteU16(_ipOff, offset);
        // Sprint 27.10b — keep hidden CS cache in sync with the visible
        // selector. SegmentedLinear in Sprint 27.10c will read CS_BASE.
        InitSegmentCache("CS", segment, 0xFFFF, 0x9B);
        _activeMem = _mem;
        _activeCpu = this;
    }

    public bool Halted => _state[_haltedOff] != 0;

    /// <summary>
    /// Clear the HALTED flag. Called by the dispatch loop when an
    /// IRQ wakes the CPU from HLT — real silicon does this automatically
    /// as part of the interrupt-acknowledge sequence; in our model the
    /// host loop must explicitly clear before delivering the vector
    /// (otherwise we stay parked).
    /// </summary>
    public void ClearHalted() { _state[_haltedOff] = 0; }

    /// <summary>
    /// Sprint 27.11b — try to look up a status-register offset; return -1
    /// when the loaded spec doesn't declare it. Used for i80286-only
    /// slots (EXC_PENDING/VECTOR/ERROR, MSW, etc.) that are absent on
    /// older variants.
    /// </summary>
    private int TryStatusOffset(string name)
    {
        try { return (int)_rt.StatusOffset(name); }
        catch (KeyNotFoundException)      { return -1; }
        catch (ArgumentException)         { return -1; }
        catch (InvalidOperationException) { return -1; }
    }

    // Phase 29.3b — FPU state accessors. Return null when the loaded spec
    // doesn't declare the FPU register file (no i8087 extension on the
    // machine spec). Use these for unit-test verification of FPU semantics
    // and for `apr-pc --verbose` / debug dumps. The 8 ST(i) slots store
    // f64-as-i64; bitcast to double for human reading.
    public uint? TryReadFpuTop()
    {
        var off = TryStatusOffset("FPU_TOP");
        if (off < 0) return null;
        return BinaryPrimitives.ReadUInt32LittleEndian(_state.AsSpan(off, 4));
    }
    public ushort? TryReadFpuCw()
    {
        var off = TryStatusOffset("FPU_CW");
        if (off < 0) return null;
        return BinaryPrimitives.ReadUInt16LittleEndian(_state.AsSpan(off, 2));
    }
    public ushort? TryReadFpuSw()
    {
        var off = TryStatusOffset("FPU_SW");
        if (off < 0) return null;
        return BinaryPrimitives.ReadUInt16LittleEndian(_state.AsSpan(off, 2));
    }
    public ushort? TryReadFpuTags()
    {
        var off = TryStatusOffset("FPU_TAGS");
        if (off < 0) return null;
        return BinaryPrimitives.ReadUInt16LittleEndian(_state.AsSpan(off, 2));
    }
    /// <summary>
    /// Read the physical-slot ST(i) (0-7) as a double. Returns null when
    /// the spec doesn't declare the slot. Note: this reads the PHYSICAL
    /// slot index, not the logical ST(i) — callers that want logical
    /// ST(0) should do `TryReadFpuPhysicalSt(TryReadFpuTop() ?? 0)`.
    /// </summary>
    public double? TryReadFpuPhysicalSt(int physicalIndex)
    {
        if ((uint)physicalIndex >= 8) return null;
        var off = TryStatusOffset($"FPU_ST{physicalIndex}");
        if (off < 0) return null;
        var i64 = BinaryPrimitives.ReadInt64LittleEndian(_state.AsSpan(off, 8));
        return BitConverter.Int64BitsToDouble(i64);
    }

    public X86State State
    {
        get
        {
            // Synthesize an X86State snapshot from the pinned spec buffer.
            // Used by tests / lockstep diff. Allocations are cheap relative
            // to the cost of the test itself.
            var s = new X86State
            {
                A = { X = ReadU16(_axOff) },
                C = { X = ReadU16(_cxOff) },
                D = { X = ReadU16(_dxOff) },
                B = { X = ReadU16(_bxOff) },
                SP = ReadU16(_spOff),
                BP = ReadU16(_bpOff),
                SI = ReadU16(_siOff),
                DI = ReadU16(_diOff),
                ES = ReadU16(_esOff),
                CS = ReadU16(_csOff),
                SS = ReadU16(_ssOff),
                DS = ReadU16(_dsOff),
                IP = ReadU16(_ipOff),
            };
            s.SetFlags(ReadU16(_flagsOff));
            // Sprint 27.11b — mirror exception slots when the spec
            // declares them (i80286+). Older variants leave them at 0.
            if (_excPendingOff >= 0) s.ExcPending = _state[_excPendingOff];
            if (_excVectorOff  >= 0) s.ExcVector  = _state[_excVectorOff];
            if (_excErrorOff   >= 0) s.ExcError   = ReadU16(_excErrorOff);
            return s;
        }
    }

    /// <summary>
    /// Mirror an externally-built X86State onto the spec buffer. Used by
    /// Tom Harte SST runner to install initial state before each Step().
    /// </summary>
    public void LoadState(X86State s)
    {
        WriteU16(_axOff, s.A.X);
        WriteU16(_cxOff, s.C.X);
        WriteU16(_dxOff, s.D.X);
        WriteU16(_bxOff, s.B.X);
        WriteU16(_spOff, s.SP);
        WriteU16(_bpOff, s.BP);
        WriteU16(_siOff, s.SI);
        WriteU16(_diOff, s.DI);
        WriteU16(_esOff, s.ES);
        WriteU16(_csOff, s.CS);
        WriteU16(_ssOff, s.SS);
        WriteU16(_dsOff, s.DS);
        WriteU16(_ipOff, s.IP);
        WriteU16(_flagsOff, s.GetFlags());
        _state[_haltedOff] = 0;
        _activeMem = _mem;
        _activeCpu = this;
    }

    public int Step()
    {
        _activeMem = _mem;
        _activeCpu = this;

        if (Halted) return 0;

        // 24.6.8 — block-JIT dispatch. Falls back to per-instr StepOne()
        // when the next byte is a prefix (0x26/2E/36/3E/F0/F2/F3) or when
        // BlockDetector decides not to compile a block at this PC.
        if (_blockJitEnabled)
        {
            ushort cs0 = ReadU16(_csOff);
            ushort ip0 = ReadU16(_ipOff);
            int linear0 = ((cs0 << 4) + ip0) & 0xFFFFF;
            byte first0 = _mem.ReadByte(linear0);
            bool isPrefix = first0 is 0x26 or 0x2E or 0x36 or 0x3E or 0xF0 or 0xF2 or 0xF3;
            if (!isPrefix)
            {
                int rc = StepBlock();
                if (rc >= 0) return rc;
                // -1 = block-JIT path bailed out (decoder returned null at
                // this PC, or other unhandled case); fall through to
                // per-instr Step() which has the broader fallback path.
            }
        }

        // Phase 30.15d sprint 5.4c — fall-through to per-instr (prefix
        // case OR block-JIT compile bailed). Must explicitly set
        // LastBlockInstructionCount=1; otherwise the slot holds STALE
        // value from a prior block (e.g. 59 from previous block-JIT
        // call), and the verifier framework would drive interp for the
        // wrong number of instructions.
        var rc1 = StepOne();
        LastBlockInstructionCount = 1;
        return rc1;
    }

    /// <summary>
    /// 24.6.8 — block-JIT step. Detect (or hit cache for) the block at the
    /// current CS:IP linear address, invoke the JIT'd block function, and
    /// return cycles consumed (currently 1 — cycle accounting deferred).
    /// </summary>
    private int StepBlock()
    {
        ushort cs = ReadU16(_csOff);
        ushort ip = ReadU16(_ipOff);
        uint linearPc = (uint)(((cs << 4) + ip) & 0xFFFFF);

        // Phase 30.15c-B verification — APR_X86_NO_BLOCK_CACHE=1 forces
        // a fresh compile on EVERY block entry, bypassing the cache. Used
        // to test the "stale packed-tail / SMC missed invalidation"
        // hypothesis: if disabling the cache makes a previously-divergent
        // workload behave correctly under block-JIT, the bug is in cache
        // invalidation / packed-tail freshness, not in the IR emitter.
        bool noCache = Environment.GetEnvironmentVariable("APR_X86_NO_BLOCK_CACHE") == "1";
        // APR_X86_TRACE_COMPILE=ADDR (hex) — log every fresh compile at
        // or near ADDR (±0x20 bytes). Lets us see when a particular
        // block was FIRST compiled vs when the divergent execution
        // ran, to test the "stale packed-tail" hypothesis.
        int traceAddr = Environment.GetEnvironmentVariable("APR_X86_TRACE_COMPILE") is string sa
                        && int.TryParse(sa, System.Globalization.NumberStyles.HexNumber, null, out var v)
                        ? v : -1;
        CachedBlock entry;
        if (noCache || !_blockCache!.TryGet(linearPc, out entry))
        {
            try
            {
                entry = CompileBlockAtLinearPc(linearPc);
                if (!noCache) _blockCache!.Add(linearPc, entry);
                if (traceAddr >= 0
                    && Math.Abs((long)linearPc - traceAddr) <= 0x40)
                {
                    Console.Error.WriteLine(
                        $"  [COMPILE] linearPc=0x{linearPc:X5} bytes: " +
                        $"{_mem.ReadByte((int)linearPc):X2} " +
                        $"{_mem.ReadByte((int)(linearPc + 1)):X2} " +
                        $"{_mem.ReadByte((int)(linearPc + 2)):X2} " +
                        $"{_mem.ReadByte((int)(linearPc + 3)):X2} " +
                        $"{_mem.ReadByte((int)(linearPc + 4)):X2}");
                }
            }
            catch (InvalidOperationException)
            {
                // Block compile failed (undecodable at startPc, or
                // BlockDetector found 0 instructions). Bail to per-instr.
                return -1;
            }
            catch (BlockDetector.UndecodableFirstInstructionException)
            {
                // Phase 30.18o — first byte at startPc is undecodable AND
                // not safe-NOP-fallback (x86 0x00=ADD). Bail to per-instr;
                // the per-instr StepOne will treat the byte as [UNK] and
                // advance past it. Without this catch the fuzzer reports
                // these blocks as SKIPPED (uncaught ArgumentException) and
                // never gets to verify the post-byte block.
                return -1;
            }
        }

        // Initialize the IR-level cycle budget. BlockFunctionBuilder's
        // post-instruction "deduct + check exhausted" logic uses this
        // slot; without setting it the residual zero forces budget exit
        // after the very first instruction (block-JIT degenerates to
        // per-instr). Set high enough that a full 64-instruction block
        // worst-case (each instr cycles.form max ~83 for AAM) won't
        // exhaust it; budget is a counter, not a real cycle quota.
        const int budgetInit = 1 << 24;
        Marshal.WriteInt32((IntPtr)(_statePtr + _cyclesLeftOff), budgetInit);
        // Clear PcWritten — block exits set it to 1 when control transfers,
        // and we use the non-zero value as "block exited via PC change"
        // signal. Stale value from prior call would mis-signal.
        _statePtr[_rt.PcWrittenOffset] = 0;

        // Phase 30.15d sprint 5.4c — clear LastInstrIndex slot, JIT
        // block writes (i+1) at start of each instr's preBB, so on
        // return the slot contains the 1-based count of instructions
        // actually entered (correct even on mid-block early Jcc/RET
        // exits, unlike entry.InstructionCount which is the COMPILE-
        // TIME block size). Required by verifier framework.
        var lastIdxOff = (int)_rt.LastInstrIndexOffset;
        Marshal.WriteInt32((IntPtr)(_statePtr + lastIdxOff), 0);
        var fn = (delegate* unmanaged[Cdecl]<byte*, void>)entry.Fn;
        fn(_statePtr);
        int actualCount = Marshal.ReadInt32((IntPtr)(_statePtr + lastIdxOff));
        if (Environment.GetEnvironmentVariable("APR_X86_TRACE_BLOCKLEN") is not null)
            Console.Error.WriteLine($"[BLOCKLEN] pc=0x{linearPc:X5} detected={entry.InstructionCount} actual={actualCount}");
        LastBlockInstructionCount = actualCount > 0 ? actualCount : entry.InstructionCount;
        return 1;
    }

    private CachedBlock CompileBlockAtLinearPc(uint linearPc)
    {
        int maxInstr = BlockDetector.DefaultMaxInstructions;
        var maxEnv = Environment.GetEnvironmentVariable("APR_X86_BLOCK_MAX");
        if (maxEnv is not null && int.TryParse(maxEnv, out var m) && m > 0) maxInstr = m;

        var busAdapter = new MemoryBusAdapter(_mem);
        var block = _blockDetector!.Detect(busAdapter, linearPc, maxInstructions: maxInstr);
        if (block.Instructions.Count == 0)
        {
            throw new InvalidOperationException(
                $"X86JsonCpu: BlockDetector found no instructions at linearPc=0x{linearPc:X5}.");
        }

        var generation = ++_blockGeneration;
        var moduleName = $"AprX86_BlockJit_pc{linearPc:X5}_g{generation}";
        var module = LLVMModuleRef.CreateWithName(moduleName);
        var bfb = new BlockFunctionBuilder(
            module, _compileResult.Layout,
            _compileResult.EmitterRegistry, _compileResult.ResolverRegistry)
        {
            CyclesPerSpecUnit = _spec.Cpu.IsaMetadata?.CyclesPerSpecUnit ?? 1
        };
        var mainSetSpec = _spec.InstructionSets["Main"];
        bfb.Build(mainSetSpec, block, generation);

        // Phase 30.15c-B — dump LLVM IR for blocks matching APR_X86_IR_DUMP=ADDR
        // (within ±0x40 bytes). Used to spot bugs in the emitted IR for a
        // specific block where lockstep showed divergence.
        if (Environment.GetEnvironmentVariable("APR_X86_IR_DUMP") is string irAddr
            && int.TryParse(irAddr, System.Globalization.NumberStyles.HexNumber, null, out var irA)
            && Math.Abs((long)linearPc - irA) <= 0x40)
        {
            try
            {
                var irText = module.PrintToString();
                var path = $"temp/ir-dump-pc{linearPc:X5}-g{generation}.ll";
                System.IO.Directory.CreateDirectory("temp");
                System.IO.File.WriteAllText(path, irText);
                Console.Error.WriteLine($"  [IR-DUMP] linearPc=0x{linearPc:X5} → {path}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  [IR-DUMP] failed: {ex.Message}");
            }
        }
        _rt.AddModule(module);
        var fnName = BlockFunctionBuilder.BlockFunctionName("Main", linearPc, generation);
        var fnPtr = _rt.GetFunctionPointer(fnName);

        // Coverage range for cache invalidation (currently unused on x86 —
        // no SMC notification path yet; left for future symmetry with NES).
        int totalBytes = 0;
        uint covStart = uint.MaxValue, covEnd = 0;
        var n = block.Instructions.Count;
        var instrPcs  = new uint[n];
        var instrLens = new byte[n];
        for (int i = 0; i < n; i++)
        {
            var bi = block.Instructions[i];
            totalBytes += bi.LengthBytes;
            instrPcs[i] = bi.Pc;
            instrLens[i] = bi.LengthBytes;
            if (bi.Pc < covStart) covStart = bi.Pc;
            uint instrEnd = bi.Pc + bi.LengthBytes;
            if (instrEnd > covEnd) covEnd = instrEnd;
        }
        var lastBi = block.Instructions[n - 1];
        uint nextPcAfterLastInstr = (uint)((lastBi.Pc + lastBi.LengthBytes) & 0xFFFFFu);

        return new CachedBlock(fnPtr, n, totalBytes, nextPcAfterLastInstr,
            covStart, covEnd, instrPcs, instrLens);
    }

    /// <summary>
    /// IMemoryBus adapter so BlockDetector can read instruction bytes from
    /// X86Memory during cache-miss block detection. Addresses are linear
    /// (post (CS&lt;&lt;4)+IP), masked to 20 bits per 8086 architectural
    /// bus width.
    /// </summary>
    private sealed class MemoryBusAdapter : IMemoryBus
    {
        private readonly X86Memory _mem;
        public MemoryBusAdapter(X86Memory mem) => _mem = mem;
        public byte   ReadByte    (uint addr) => _mem.ReadByte((int)(addr & 0xFFFFFu));
        public ushort ReadHalfword(uint addr) => (ushort)(
              _mem.ReadByte((int)(addr & 0xFFFFFu))
            | (_mem.ReadByte((int)((addr + 1) & 0xFFFFFu)) << 8));
        public uint ReadWord(uint addr) => (uint)(
              _mem.ReadByte((int)(addr & 0xFFFFFu))
            | (_mem.ReadByte((int)((addr + 1) & 0xFFFFFu)) << 8)
            | (_mem.ReadByte((int)((addr + 2) & 0xFFFFFu)) << 16)
            | (_mem.ReadByte((int)((addr + 3) & 0xFFFFFu)) << 24));
        public void WriteByte    (uint addr, byte v)   => _mem.WriteByte((int)(addr & 0xFFFFFu), v);
        public void WriteHalfword(uint addr, ushort v)
        {
            _mem.WriteByte((int)(addr & 0xFFFFFu),       (byte)v);
            _mem.WriteByte((int)((addr + 1) & 0xFFFFFu), (byte)(v >> 8));
        }
        public void WriteWord(uint addr, uint v)
        {
            _mem.WriteByte((int)(addr & 0xFFFFFu),       (byte)v);
            _mem.WriteByte((int)((addr + 1) & 0xFFFFFu), (byte)(v >> 8));
            _mem.WriteByte((int)((addr + 2) & 0xFFFFFu), (byte)(v >> 16));
            _mem.WriteByte((int)((addr + 3) & 0xFFFFFu), (byte)(v >> 24));
        }
    }

    /// <summary>
    /// Per-instruction Step (the original implementation, renamed). Always
    /// the slow path — handles segment override + REP prefixes, single
    /// instruction execution. Block-JIT falls back here for prefixed
    /// instructions and other paths it can't handle.
    /// </summary>
    private int StepOne()
    {
        // Prefix dispatch — consumes 0x26/0x2E/0x36/0x3E (segment override,
        // 24.6.5d) and 0xF2/0xF3 (REP/REPE/REPNE, 24.6.7c2). All prefixes
        // are single-byte; we accumulate any combination until we hit a
        // real opcode. Defensive cap of 15 bytes (= 8086 max instruction
        // length) guards against malformed input.
        ushort cs = ReadU16(_csOff);
        ushort ip = ReadU16(_ipOff);
        int prefixesConsumed = 0;
        bool repe = false;     // 0xF3 seen
        bool repne = false;    // 0xF2 seen
        byte opcode;
        while (true)
        {
            int linear = ((cs << 4) + ip) & 0xFFFFF;
            opcode = _mem.ReadByte(linear);
            byte? overrideId = opcode switch
            {
                0x26 => (byte?)0,   // ES
                0x2E => (byte?)1,   // CS
                0x36 => (byte?)2,   // SS
                0x3E => (byte?)3,   // DS
                _    => null,
            };
            if (overrideId is not null)
            {
                _state[_segOverrideOff] = overrideId.Value;
            }
            else if (opcode == 0xF3)
            {
                repe = true;
                repne = false;     // last-prefix-wins
            }
            else if (opcode == 0xF2)
            {
                repne = true;
                repe = false;
            }
            else
            {
                break;
            }
            ip = (ushort)(ip + 1);
            prefixesConsumed++;
            if (prefixesConsumed > 15)
            {
                _state[_segOverrideOff] = 0xFF;
                WriteU16(_ipOff, ip);
                return -1;
            }
        }

        // ip now points at the real opcode byte (post-prefixes). Persist
        // it before invoking the function so emitters that read CS:IP
        // (FetchImm8/16 / fetch_modrm) see the correct value.
        WriteU16(_ipOff, (ushort)(ip + 1));

        var decoded = _mainDecoder.Decode(opcode);
        if (decoded is null)
        {
            // Unknown opcode -- rewind to pre-prefix IP and return -1.
            // **Beware: dispatch loop typically just calls Step() again,
            // so this is an infinite hot loop on the offending byte.**
            // Log once per (linearPc, opcode) so missing opcodes are
            // visible instead of silently hanging. The first time we
            // saw this pattern was pcxtbios.bin INT 9 ISR's XLAT (0xD7);
            // it took a Gemini round-trip to find -- this log prevents
            // the next one needing that.
            uint key = ((uint)(((cs << 4) + (ip - prefixesConsumed)) & 0xFFFFF))
                       | ((uint)opcode << 24);
            if (_unknownOpcodeLogged.Add(key))
            {
                var msg = $"X86 unknown opcode 0x{opcode:X2} at {cs:X4}:{(ushort)(ip - prefixesConsumed):X4} " +
                          $"(linear 0x{((cs << 4) + (ip - prefixesConsumed)) & 0xFFFFF:X5}, " +
                          $"prefixes={prefixesConsumed}) -- CPU will loop here until implemented";
                Console.Error.WriteLine($"  [UNK] {msg}");
                OnUnknownOpcode?.Invoke(cs, (ushort)(ip - prefixesConsumed), opcode);
            }
            WriteU16(_ipOff, (ushort)(ip - prefixesConsumed));
            _state[_segOverrideOff] = 0xFF;
            return -1;
        }

        var fnPtr = ResolveFunctionPointer(_mainDecoder.Name, decoded);
        var fn = (delegate* unmanaged[Cdecl]<byte*, uint, void>)fnPtr;

        // 24.6.7c2 — REP prefix dispatch. F2/F3 are only meaningful before
        // string ops (A4-A7, AA-AF). For non-string ops they're silently
        // ignored. For string ops:
        //   MOVS/STOS/LODS (A4/A5/AA/AB/AC/AD): both F2 and F3 act as REP
        //                                       (count down only, ignore ZF)
        //   CMPS/SCAS (A6/A7/AE/AF):
        //     F3 → REPE  (repeat while ZF=1, abort when ZF=0 OR CX=0)
        //     F2 → REPNE (repeat while ZF=0, abort when ZF=1 OR CX=0)
        //
        // CX==0 at entry means zero iterations (the whole instruction
        // becomes a no-op except for the IP advance + override clears).
        bool isStringOp = (opcode >= 0xA4 && opcode <= 0xAF) && opcode != 0xA8 && opcode != 0xA9;
        bool isCmpOrScas = opcode == 0xA6 || opcode == 0xA7 || opcode == 0xAE || opcode == 0xAF;
        bool repActive = (repe || repne) && isStringOp;

        if (repActive)
        {
            // Defensive iteration cap — well-behaved code keeps CX small;
            // pathological inputs could wedge the loop.
            int maxIters = 0x20000;
            while (maxIters-- > 0)
            {
                ushort cx = ReadU16(_cxOff);
                if (cx == 0) break;
                fn(_statePtr, opcode);
                cx = (ushort)(cx - 1);
                WriteU16(_cxOff, cx);
                if (cx == 0) break;
                if (isCmpOrScas)
                {
                    // ZF lives at FLAGS bit 6
                    bool zf = ((ReadU16(_flagsOff) >> 6) & 1) != 0;
                    if (repe && !zf) break;
                    if (repne && zf) break;
                }
                // Re-fetch IP after each iteration — string-op IR doesn't
                // touch IP, so we must reset it back to the same opcode for
                // the JIT'd function to operate on the correct state. But
                // string-op IR ALSO does NOT advance IP (post-fetch IP was
                // stored after the prefix-consumption). So nothing to do.
            }
        }
        else
        {
            fn(_statePtr, opcode);
        }

        // Override is per-instruction: clear after execution so the next
        // Step() starts with a clean default-segment policy.
        _state[_segOverrideOff] = 0xFF;

        return 1;
    }

    // --- Function-pointer resolution --------------------------------------

    private IntPtr ResolveFunctionPointer(string setName, DecodedInstruction decoded)
    {
        if (_fnPtrByDef.TryGetValue(decoded.Instruction, out var cached)) return cached;

        var fmt = decoded.Format;
        var def = decoded.Instruction;
        var ambiguous = false;
        for (int i = 0, hits = 0; i < fmt.Instructions.Count; i++)
            if (fmt.Instructions[i].Mnemonic == def.Mnemonic && ++hits > 1)
            { ambiguous = true; break; }

        var disambig = ambiguous && def.Selector is not null
            ? $"{def.Mnemonic}_{def.Selector.Value}"
            : def.Mnemonic;
        var fnName = $"Execute_{setName}_{fmt.Name}_{disambig}";
        var p = _rt.GetFunctionPointer(fnName);
        _fnPtrByDef[def] = p;
        return p;
    }

    // --- State buffer accessors -------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ushort ReadU16(int off)
        => BinaryPrimitives.ReadUInt16LittleEndian(_state.AsSpan(off, 2));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteU16(int off, ushort v)
        => BinaryPrimitives.WriteUInt16LittleEndian(_state.AsSpan(off, 2), v);

    // --- Extern shims (called from JIT'd IR) ------------------------------

    // Phase 30 debug — read-watch (parallel to WriteWatch).
    public static uint ReadWatchLo;
    public static uint ReadWatchHi;
    private static int _readWatchCount;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static byte MemRead8(uint addr)
    {
        if (_activeMem is null) return 0xFF;
        uint a = addr & 0xFFFFF;
        byte v = _activeMem.ReadByte((int)a);
        if (ReadWatchHi > ReadWatchLo && a >= ReadWatchLo && a < ReadWatchHi)
        {
            if (_readWatchCount < 600)
            {
                Console.Error.WriteLine($"  [RW] read  0x{a:X5} → 0x{v:X2}");
                _readWatchCount++;
            }
        }
        return v;
    }

    // Phase 30 debug — write-watch range. When non-zero, every CPU write
    // landing in [_writeWatchLo, _writeWatchHi) gets logged to stderr.
    public static uint WriteWatchLo;
    public static uint WriteWatchHi;
    private static int _writeWatchCount;

    /// <summary>
    /// Optional callback fired on every write inside the WriteWatch range.
    /// Lets downstream tooling (AprPc kbd trace, etc.) route the data into
    /// its own log without AprX86.Cli having to know about it.
    /// </summary>
    public static Action<uint, byte>? OnWriteWatch;

    /// <summary>
    /// Optional callback fired once per unique (CS:IP, opcode) tuple that
    /// the main decoder can't decode. Downstream tooling can route this to
    /// a dedicated log; without a hook, only stderr gets the warning.
    /// </summary>
    public static Action<ushort, ushort, byte>? OnUnknownOpcode;

    private static readonly HashSet<uint> _unknownOpcodeLogged = new();

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void MemWrite8(uint addr, byte value)
    {
        if (_activeMem is null) return;
        uint a = addr & 0xFFFFF;
        _activeMem.WriteByte((int)a, value);
        // Phase 30.15 — SMC notify. Cheap (single counter read + branch
        // when no cached block covers the addr) and only fires the slow
        // scan path when JIT actually has a stale translation for this
        // byte. Without this, FreeDOS kernel relocation via REP MOVSW
        // leaves the JIT executing stale zero-translations at the new CS.
        _activeCpu?._blockCache?.NotifyMemoryWrite(a);
        // Phase 30.15d sprint 5.3 — record into verified-block trace
        // sink when active. instrIndexWithinBlock=0 placeholder; per-
        // instruction granularity needs IR-level counter (future).
        ActiveTraceSink?.RecordMemWrite(0, a, value, 1);
        if (WriteWatchHi > WriteWatchLo && a >= WriteWatchLo && a < WriteWatchHi)
        {
            // Note: stderr [WW] spam removed Phase 30.10 — the auto-
            // enabled BDA write watch in real-BIOS mode (PcSystemRunner)
            // generated thousands of lines per launch on terminal. The
            // OnWriteWatch callback still fires (downstream tooling
            // logs to temp/kbd-trace.log instead).
            OnWriteWatch?.Invoke(a, value);
        }
    }

    // Phase 28.IO — port I/O extern shims. Forward to delegate handlers
    // that downstream harnesses (AprPc.Cli) install at startup. Tom
    // Harte SST runners + plain .com test invocations leave the handlers
    // null and get open-bus reads / no-op writes (matches the pre-28.IO
    // behavior).
    public static Func<ushort, byte>?     PortRead8Handler;
    public static Func<ushort, ushort>?   PortRead16Handler;
    public static Action<ushort, byte>?   PortWrite8Handler;
    public static Action<ushort, ushort>? PortWrite16Handler;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static byte PortRead8(ushort port)
        => PortRead8Handler?.Invoke(port) ?? (byte)0xFF;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static ushort PortRead16(ushort port)
        => PortRead16Handler?.Invoke(port) ?? (ushort)0xFFFF;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void PortWrite8(ushort port, byte value)
        => PortWrite8Handler?.Invoke(port, value);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void PortWrite16(ushort port, ushort value)
        => PortWrite16Handler?.Invoke(port, value);

    // Phase 29.7 — FPU transcendental shims. Route to C# System.Math.*.
    // Bound by ctor unconditionally so the IR slots resolve even on Tom
    // Harte runs (which never call them).
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static double FpuTan(double x) => Math.Tan(x);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static double FpuAtan2(double y, double x) => Math.Atan2(y, x);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static double FpuLog2(double x) => Math.Log2(x);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static double FpuExp2M1(double x) => Math.Pow(2.0, x) - 1.0;
}
