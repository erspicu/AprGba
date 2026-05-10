// JSON-spec-driven Intel 8086 backend.
//
// 24.6.4 — per-instruction skeleton. Mirrors NesJsonCpu: loads
// spec/x86-16/i8086/cpu.json, runs SpecCompiler over it, wires the LLVM
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

    public X86Memory Memory => _mem;

    /// <summary>
    /// 25.5 — variant string selects which spec directory to load.
    /// "i8086" (default): spec/x86-16/i8086/cpu.json (base spec).
    /// "i80186" / "i80188": spec/x86-16/i80186/cpu.json (extends i8086;
    /// loaded via inheritance resolution).
    /// </summary>
    public string Variant { get; }

    public X86JsonCpu(X86Memory memory, bool enableBlockJit = false, string variant = "i8086")
    {
        _mem = memory ?? throw new ArgumentNullException(nameof(memory));
        _blockJitEnabled = enableBlockJit;
        Variant = variant;

        var specPath = LocateSpec(variant);
        var compileResult = SpecCompiler.Compile(specPath);
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

        _spec = SpecLoader.LoadCpuSpec(specPath);
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
            var probe = Path.Combine(d.FullName, "spec", "x86-16", subdir, "cpu.json");
            if (File.Exists(probe)) return probe;
        }
        var cwdProbe = Path.Combine(Environment.CurrentDirectory, "spec", "x86-16", subdir, "cpu.json");
        if (File.Exists(cwdProbe)) return cwdProbe;
        throw new FileNotFoundException(
            $"X86JsonCpu: cannot locate spec/x86-16/{subdir}/cpu.json. Run from repo root.");
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
    }

    public bool Halted => _state[_haltedOff] != 0;

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
    }

    public int Step()
    {
        _activeMem = _mem;

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

        return StepOne();
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

        if (!_blockCache!.TryGet(linearPc, out var entry))
        {
            try
            {
                entry = CompileBlockAtLinearPc(linearPc);
                _blockCache.Add(linearPc, entry);
            }
            catch (InvalidOperationException)
            {
                // Block compile failed (undecodable at startPc, or
                // BlockDetector found 0 instructions). Bail to per-instr.
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

        var fn = (delegate* unmanaged[Cdecl]<byte*, void>)entry.Fn;
        fn(_statePtr);
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

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static byte MemRead8(uint addr)
        => _activeMem is not null ? _activeMem.ReadByte((int)(addr & 0xFFFFF)) : (byte)0xFF;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void MemWrite8(uint addr, byte value)
    {
        if (_activeMem is null) return;
        _activeMem.WriteByte((int)(addr & 0xFFFFF), value);
    }
}
