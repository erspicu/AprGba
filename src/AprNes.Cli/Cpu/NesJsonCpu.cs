// JSON-spec-driven Ricoh 2A03 backend.
//
// Loads spec/2a03/cpu.json, runs SpecCompiler over it (emits 117 LLVM
// functions covering all 256 opcodes), wires the LLVM module through
// HostRuntime, JIT-compiles, and dispatches one instruction per Step()
// call by reading the opcode byte from NesMemoryBus and invoking the
// matching native function pointer.
//
// Two modes — selected at construction time via enableBlockJit:
//
// 1. Per-instruction (default) — Step() handles one opcode. NMI handled
//    in C# at the start of each Step(). Cycle accounting reuses the
//    LegacyCpu 256-byte cycle_table rather than the spec's per-mnemonic
//    "Nm" form, so PPU NMI scheduling matches LegacyCpu byte-for-byte.
//
// 2. Block-JIT (enableBlockJit=true) — BlockDetector walks PC + builds a
//    Block (multiple instructions until next writes_pc:always boundary),
//    BlockFunctionBuilder emits one LLVM function per block, ORC LLJIT
//    AddModule wires it. State access goes through the framework's
//    alloca + mem2reg machinery (#18 design doc), so Mos6502Emitters.cs
//    is unchanged. Cycle accounting via IR-level cycles_left budget:
//    set initial = sentinel large value, IR decrements per executed
//    instruction (using BFB's per-instr cycle decrement with
//    CyclesPerSpecUnit=1 to match 6502's raw-cycle spec form), read
//    residual after block exit, consumed = initial - residual. This
//    correctly bills cycles only for instructions that actually ran
//    (taken-branch early exits don't get charged for the unrun tail).

using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AprCpu.Core.Compilation;
using AprCpu.Core.Decoder;
using AprCpu.Core.IR;
using AprCpu.Core.JsonSpec;
using AprCpu.Core.Runtime;
using AprNes.Cli.Memory;
using LLVMSharp.Interop;

namespace AprNes.Cli.Cpu;

public sealed unsafe class NesJsonCpu : INesCpuBackend
{
    public string BackendName => _blockJitEnabled ? "json-block-llvm" : "json-llvm";

    // Static bus reference used by the unmanaged extern shims. Last-call-
    // wins when multiple NesJsonCpu instances exist; the harness only ever
    // constructs one at a time.
    private static NesMemoryBus? _activeBus;

    private readonly NesMemoryBus _bus;
    private readonly LoadedSpec    _spec;
    private readonly HostRuntime   _rt;
    private readonly DecoderTable  _mainDecoder;
    private readonly SpecCompiler.CompileResult _compileResult;

    // Block-JIT (optional, opt-in via ctor flag).
    private readonly bool          _blockJitEnabled;
    private readonly BlockDetector? _blockDetector;
    private readonly BlockCache?    _blockCache;
    private int _blockGeneration;

    // Identity-keyed function-pointer cache (InstructionDef → fn ptr).
    // Reference equality is fine because DecoderTable.Decode returns the
    // exact same InstructionDef instance for the same opcode.
    private readonly Dictionary<InstructionDef, IntPtr> _fnPtrByDef
        = new(System.Collections.Generic.ReferenceEqualityComparer.Instance);

    private byte[] _state = Array.Empty<byte>();
    private GCHandle _stateHandle;
    private byte* _statePtr;

    // N11 — pin WRAM so the JIT'd code can GEP-load directly from it
    // via the mos6502_wram_base extern (set in the constructor). Bus's
    // _wram[] is a fixed-size readonly array, so pinning once here is
    // safe for the lifetime of this NesJsonCpu instance.
    private GCHandle _wramHandle;

    // Pre-cached field offsets — order matches cpu.json's GPR list
    // (A, X, Y) and the status section (P, SP, PC).
    private readonly int _aOff, _xOff, _yOff;
    private readonly int _pOff, _spOff, _pcOff;
    // Block-JIT-only — cycles_left budget slot + PcWritten flag slot.
    private readonly int _cyclesLeftOff, _pcWrittenOff;

    // N3.3 (finally) — per-opcode cycle table now derived from spec at
    // construction. Walks 256 opcodes through the decoder; for each, looks
    // up cycles.table (per-(mnemonic, addressing-mode) granularity) or
    // falls back to cycles.form. The result is byte-identical to the
    // LegacyCpu oracle that was hardcoded here pre-N3.3, but now the spec
    // is the source of truth.
    //
    // Built per-instance because the CpuSpec instance lives on the JIT
    // pipeline; static caching across NesJsonCpu instances would need a
    // (CpuSpec, Decoder) keyed cache and isn't worth the complexity for
    // typical usage (one CPU per emulator).
    private readonly byte[] _specCycleTable;

    private static byte[] BuildSpecCycleTable(
        AprCpu.Core.Decoder.DecoderTable decoder,
        int cyclesPerSpecUnit)
    {
        var table = new byte[256];
        for (int op = 0; op < 256; op++)
        {
            var d = decoder.Decode((uint)op);
            if (d?.Instruction.Cycles is not { } cyc)
            {
                throw new InvalidOperationException(
                    $"NesJsonCpu: spec missing cycles for opcode 0x{op:X2}");
            }

            int? cycles = null;
            if (cyc.Table is { } ct &&
                ct.Resolve(d.Format, (uint)op) is int tableValue)
            {
                cycles = tableValue;
            }
            else if (!string.IsNullOrEmpty(cyc.Form))
            {
                int n = 0;
                foreach (var ch in cyc.Form)
                {
                    if (ch >= '0' && ch <= '9') { n = n * 10 + (ch - '0'); continue; }
                    if (n > 0) break;
                }
                if (n > 0) cycles = n * cyclesPerSpecUnit;
            }

            if (cycles is not int v)
                throw new InvalidOperationException(
                    $"NesJsonCpu: opcode 0x{op:X2} has cycles spec but neither table nor form is resolvable");
            table[op] = (byte)v;
        }
        return table;
    }

    // Bookkeeping cycle count carried over from interrupt entry — mirrors
    // LegacyCpu.Interrupt_cycle. Folded into the cycle count on the next
    // Step() call so the harness sees the +7 reset / NMI prologue.
    private int _interruptCycle;

    // N3.1 — interrupt vectors loaded from spec/machines/nes-ntsc.json.
    // Defaults match the canonical 6502 layout if MachineSpec lookup fails
    // (so the per-instr backend keeps working without a machine spec).
    private readonly ushort _nmiVector;
    private readonly ushort _resetVector;
    private readonly ushort _irqVector;

    public NesJsonCpu(NesMemoryBus bus, bool enableBlockJit = false)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _blockJitEnabled = enableBlockJit;

        var specPath = LocateSpec();
        var compileResult = SpecCompiler.Compile(specPath);
        if (compileResult.Diagnostics.Count != 0)
        {
            throw new InvalidOperationException(
                "NesJsonCpu: spec compilation produced diagnostics:\n  " +
                string.Join("\n  ", compileResult.Diagnostics));
        }
        _compileResult = compileResult;

        _spec = SpecLoader.LoadCpuSpec(specPath);
        if (!compileResult.DecoderTables.TryGetValue("Main", out var mainDecoder))
        {
            throw new InvalidOperationException(
                "NesJsonCpu: spec must declare a 'Main' instruction set.");
        }
        _mainDecoder = mainDecoder;

        // N3.3 (finally) — derive 256-byte cycle table from spec.
        int cyclesPerSpecUnit = _spec.Cpu.IsaMetadata?.CyclesPerSpecUnit ?? 1;
        _specCycleTable = BuildSpecCycleTable(_mainDecoder, cyclesPerSpecUnit);

        _rt = HostRuntime.Build(compileResult.Module,
            new CpuStateLayout(
                compileResult.Module.Context,
                _spec.Cpu.RegisterFile,
                _spec.Cpu.ProcessorModes,
                _spec.Cpu.ExceptionVectors));

        // Bind the two memory-bus externs the 2A03 IR uses (no Read16/
        // Write16 — Mos6502Emitters only ever calls 8-bit accessors).
        _rt.BindExtern(MemoryEmitters.ExternFunctionNames.Read8,
            (IntPtr)(delegate* unmanaged[Cdecl]<uint, byte>)&MemRead8);
        _rt.BindExtern(MemoryEmitters.ExternFunctionNames.Write8,
            (IntPtr)(delegate* unmanaged[Cdecl]<uint, byte, void>)&MemWrite8);

        // N11 — bind WRAM base pointer so Mos6502Emitters.BusRead8's
        // inline fastmem path (addr < 0x2000 → GEP-load from _wram[]) can
        // resolve the host array address at JIT time.
        _wramHandle = GCHandle.Alloc(_bus.Wram, GCHandleType.Pinned);
        _rt.BindExtern(MemoryEmitters.ExternFunctionNames.Mos6502WramBase,
            _wramHandle.AddrOfPinnedObject());

        _rt.Compile();

        // Register-field offsets — order matches cpu.json's GPR list
        // (A, X, Y) and status registers (P, SP, PC).
        _aOff  = (int)_rt.GprOffset(0);
        _xOff  = (int)_rt.GprOffset(1);
        _yOff  = (int)_rt.GprOffset(2);
        _pOff  = (int)_rt.StatusOffset("P");
        _spOff = (int)_rt.StatusOffset("SP");
        _pcOff = (int)_rt.StatusOffset("PC");
        _cyclesLeftOff = (int)_rt.CyclesLeftOffset;
        _pcWrittenOff  = (int)_rt.PcWrittenOffset;

        // Allocate + pin the state buffer. Permanently pinned for the
        // lifetime of this NesJsonCpu — we never reallocate.
        _state = new byte[(int)_rt.StateSizeBytes];
        _stateHandle = GCHandle.Alloc(_state, GCHandleType.Pinned);
        _statePtr    = (byte*)_stateHandle.AddrOfPinnedObject();

        // Block-JIT setup — only if requested.
        if (_blockJitEnabled)
        {
            var mainSetSpec = _spec.InstructionSets["Main"];
            _blockDetector = new BlockDetector(
                mainSetSpec,
                _mainDecoder,
                lengthOracle: Mos6502InstructionLengths.GetLength,
                prefixSubDecoders: null);   // 6502 has no prefix bytes
            _blockCache = new BlockCache();
        }

        // N3.1 — load interrupt vectors from spec/machines/nes-ntsc.json
        // when present. Defaults match canonical 6502 layout for backwards-
        // compat (test harnesses that don't drop a machine spec still work).
        var machineSpec = TryLoadMachineSpec("nes-ntsc");
        _nmiVector   = LookupVector(machineSpec, "nmi",   defaultAddr: 0xFFFA);
        _resetVector = LookupVector(machineSpec, "reset", defaultAddr: 0xFFFC);
        _irqVector   = LookupVector(machineSpec, "irq",   defaultAddr: 0xFFFE);
    }

    private static MachineSpec? TryLoadMachineSpec(string name)
    {
        var dir = AppContext.BaseDirectory;
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
        {
            var probe = Path.Combine(d.FullName, "spec", "machines", $"{name}.json");
            if (File.Exists(probe)) return MachineSpecLoader.LoadFromFile(probe);
        }
        var cwdProbe = Path.Combine(Environment.CurrentDirectory, "spec", "machines", $"{name}.json");
        return File.Exists(cwdProbe) ? MachineSpecLoader.LoadFromFile(cwdProbe) : null;
    }

    private static ushort LookupVector(MachineSpec? spec, string name, ushort defaultAddr)
    {
        if (spec is not null && spec.InterruptVectors.TryGetValue(name, out var addr))
            return (ushort)addr;
        return defaultAddr;
    }

    private static string LocateSpec()
    {
        var dir = AppContext.BaseDirectory;
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
        {
            var probe = Path.Combine(d.FullName, "spec", "2a03", "cpu.json");
            if (File.Exists(probe)) return probe;
        }
        var cwdProbe = Path.Combine(Environment.CurrentDirectory, "spec", "2a03", "cpu.json");
        if (File.Exists(cwdProbe)) return cwdProbe;
        throw new FileNotFoundException(
            "NesJsonCpu: cannot locate spec/2a03/cpu.json. Run from repo root.");
    }

    // --- INesCpuBackend surface -----------------------------------------

    public ushort PC => ReadU16(_pcOff);
    public byte   A  => _state[_aOff];
    public byte   X  => _state[_xOff];
    public byte   Y  => _state[_yOff];
    public byte   SP => _state[_spOff];
    public byte   P  => _state[_pOff];

    public void Reset()
    {
        // Mirror LegacyCpu.ResetInterrupt(): SP -= 3, PC := *(0xFFFC),
        // I := 1. Initial SP=0xFD comes from "0x00 - 3 = 0xFD" — caller
        // must ensure we start from a clean state buffer (already true,
        // _state is zeroed at construction).
        _activeBus = _bus;
        byte sp = (byte)(_state[_spOff] - 3);
        _state[_spOff] = sp;
        ushort lo = _bus.ReadByte(_resetVector);
        ushort hi = _bus.ReadByte((ushort)(_resetVector + 1));
        WriteU16(_pcOff, (ushort)((hi << 8) | lo));
        // Force I=1 in P, leave other bits.
        _state[_pOff] = (byte)(_state[_pOff] | 0x04);
        _interruptCycle = 7;
    }

    public void InitForNestest()
    {
        // PC=$C000, SP=$FD, P=$24 (I=1, U=1). Match BoundCpu.InitForNestest.
        _activeBus = _bus;
        SetRegisters(a: 0, x: 0, y: 0, sp: 0xFD, pc: 0xC000,
            flagN: 0, flagV: 0, flagD: 0, flagI: 1, flagZ: 0, flagC: 0);
        // 7-cycle prologue carried into the first Step() so the cycle
        // total matches nestest.log's CYC:7 starting line.
        _interruptCycle = 7;
    }

    public void SetRegisters(byte a, byte x, byte y, byte sp, ushort pc,
        byte flagN, byte flagV, byte flagD, byte flagI, byte flagZ, byte flagC)
    {
        _activeBus = _bus;
        _state[_aOff]  = a;
        _state[_xOff]  = x;
        _state[_yOff]  = y;
        _state[_spOff] = sp;
        WriteU16(_pcOff, pc);
        // Assemble P from individual bit args; bit 5 (U) is always 1.
        byte packed = (byte)(
            ((flagN & 1) << 7) |
            ((flagV & 1) << 6) |
            (1 << 5)           |   // U
            ((flagD & 1) << 3) |
            ((flagI & 1) << 2) |
            ((flagZ & 1) << 1) |
            (flagC & 1));
        _state[_pOff] = packed;
    }

    public int Step()
    {
        _activeBus = _bus;

        // Service pending PPU VBlank NMI before fetching the next opcode /
        // entering the next block — matches BoundCpu.Step ordering. Without
        // this, vblank-wait loops (BIT $2002 / BPL ...) spin forever.
        if (_bus.ConsumePpuNmi())
        {
            NmiInterrupt();
        }

        return _blockJitEnabled ? StepBlock() : StepOne();
    }

    private int StepOne()
    {
        // Fetch opcode (advances PC by 1 — multi-byte fetches handled
        // by the IR's read_imm8 / read_imm16 emitters which themselves
        // bump PC).
        ushort pc = ReadU16(_pcOff);
        byte opcode = _bus.ReadByte(pc);
        WriteU16(_pcOff, (ushort)(pc + 1));

        var decoded = _mainDecoder.Decode(opcode);
        if (decoded is null)
        {
            // Undefined opcode — match LegacyCpu's silent skip + 2 cycles.
            int undef = 2 + _interruptCycle;
            _interruptCycle = 0;
            var dmaUndef = _bus.ConsumeStallCycles();
            undef += dmaUndef;
            _bus.Tick(undef);
            return undef;
        }

        var fnPtr = ResolveFunctionPointer(_mainDecoder.Name, decoded);
        var fn = (delegate* unmanaged[Cdecl]<byte*, uint, void>)fnPtr;
        fn(_statePtr, opcode);

        // N3.3 (finally) — cycle accounting via the spec-derived 256-byte
        // table. Each opcode's count comes from cycles.table (multi-mode
        // groups: cc=01/cc=00/cc=10/cc=11) or cycles.form (unique opcodes).
        // Byte-identical to the prior hardcoded LegacyCpu mirror.
        int cycles = _specCycleTable[opcode] + _interruptCycle;
        _interruptCycle = 0;

        int dmaStall = _bus.ConsumeStallCycles();
        cycles += dmaStall;
        _bus.Tick(cycles);
        return cycles;
    }

    /// <summary>
    /// Block-JIT step: detect (or hit cache for) the block at current PC,
    /// invoke its compiled function, and bill the actual cycles consumed
    /// via the IR's cycles_left budget. Cycles are debited per-instruction
    /// inside the block (BlockFunctionBuilder's predictive-downcount
    /// machinery), so taken-branch early exits don't get charged for the
    /// instructions they didn't run — read residual = (init − cycles_left)
    /// after block exit to get the true count.
    /// </summary>
    private int StepBlock()
    {
        ushort pc = ReadU16(_pcOff);
        // APR_NES_NOCACHE — debug knob: bypass block cache, recompile
        // every Step. Useful for SMC-suspect bugs (if blargg passes only
        // with this, the cache is stale due to RAM-resident code rewrites).
        bool noCache = Environment.GetEnvironmentVariable("APR_NES_NOCACHE") is not null;
        if (noCache || !_blockCache!.TryGet(pc, out var entry))
        {
            entry = CompileBlockAtPc(pc);
            if (!noCache) _blockCache.Add(pc, entry);
        }

        // Initialise IR-level cycle budget. Set high enough that a full
        // 64-instruction block (max ~64 × 8 cyc = 512) won't exhaust it
        // mid-block; we're using cycles_left as a counter, not a real
        // budget. Sentinel = 1024.
        const int budgetInit = 1024;
        Marshal.WriteInt32((IntPtr)(_statePtr + _cyclesLeftOff), budgetInit);
        _statePtr[_pcWrittenOff] = 0;

        var fn = (delegate* unmanaged[Cdecl]<byte*, void>)entry.Fn;
        fn(_statePtr);

        int residual = Marshal.ReadInt32((IntPtr)(_statePtr + _cyclesLeftOff));
        int consumedIr = budgetInit - residual;
        if (consumedIr < 0) consumedIr = 0;   // defensive — shouldn't happen with budgetInit=1024

        // PcWritten=0 fall-through path: the IR-driven cycles_left walked
        // every instruction's deduct. PcWritten=1 (branch/JMP/RTS taken):
        // only the executed prefix paid, the unrun tail stayed unbilled.
        // Either way `consumedIr` is the right number.

        int cycles = consumedIr + _interruptCycle;
        _interruptCycle = 0;
        int dmaStall = _bus.ConsumeStallCycles();
        cycles += dmaStall;
        _bus.Tick(cycles);
        return cycles;
    }

    private CachedBlock CompileBlockAtPc(ushort pc)
    {
        // APR_NES_BLOCK_MAX env var: optional debug knob to limit block
        // size. Set to 1 to reduce block-JIT to per-instr-via-block-path
        // (each block contains exactly 1 instruction). Useful for
        // bisecting multi-instruction-block bugs vs single-instr emitter
        // bugs.
        int maxInstr = BlockDetector.DefaultMaxInstructions;
        var maxEnv = Environment.GetEnvironmentVariable("APR_NES_BLOCK_MAX");
        if (maxEnv is not null && int.TryParse(maxEnv, out var m) && m > 0) maxInstr = m;
        var block = _blockDetector!.Detect(new BusAdapter(_bus), pc, maxInstructions: maxInstr);
        if (block.Instructions.Count == 0)
        {
            throw new InvalidOperationException(
                $"NesJsonCpu: BlockDetector found no instructions at PC=0x{pc:X4}.");
        }

        var generation = ++_blockGeneration;
        var moduleName = $"AprNes_BlockJit_pc{pc:X4}_g{generation}";
        var module = LLVMModuleRef.CreateWithName(moduleName);
        var bfb = new BlockFunctionBuilder(
            module, _compileResult.Layout,
            _compileResult.EmitterRegistry, _compileResult.ResolverRegistry)
        {
            // N2.5 — read cycles_per_spec_unit from spec/2a03/cpu.json's
            // isa_metadata section instead of hardcoding 1. 6502 spec
            // uses raw CPU cycles in cycles.form ("3m" = 3 cycles), so
            // isa_metadata declares cycles_per_spec_unit=1; default 4
            // (GB/ARM m-cycle×4) is the fallback if spec omits the field.
            CyclesPerSpecUnit = _spec.Cpu.IsaMetadata?.CyclesPerSpecUnit ?? 4
        };
        var mainSetSpec = _spec.InstructionSets["Main"];
        bfb.Build(mainSetSpec, block, generation);

        _rt.AddModule(module);
        var fnName = BlockFunctionBuilder.BlockFunctionName("Main", pc, generation);
        var fnPtr = _rt.GetFunctionPointer(fnName);

        // Compute coverage range for SMC / bank-switch invalidation.
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
        uint nextPcAfterLastInstr = (uint)((lastBi.Pc + lastBi.LengthBytes) & 0xFFFFu);

        return new CachedBlock(fnPtr, n, totalBytes, nextPcAfterLastInstr,
            covStart, covEnd, instrPcs, instrLens);
    }

    /// <summary>
    /// Drop all cached blocks whose instruction bytes fall in [addrStart, addrEnd).
    /// Wired to <see cref="IMapper.PrgBankSwitched"/> so MMC1 PRG bank changes
    /// don't leave stale blocks pointing at obsolete code. Conservative
    /// implementation — clears the entire cache. PRG bank switches are
    /// infrequent in real games (per-frame at most), so the recompile cost
    /// is fine.
    /// </summary>
    public void InvalidateCachedBlocksInRange(uint addrStart, uint addrEnd)
    {
        if (!_blockJitEnabled || _blockCache is null) return;
        // We could be more surgical (only blocks overlapping [start,end)),
        // but Clear() is simpler + correct. blargg cpu_test5 hits this
        // path during MMC1 control-register init — only ~5 times total.
        _blockCache.Clear();
    }

    /// <summary>
    /// SMC notification — called from <see cref="NesMemoryBus.WriteByte"/>
    /// on every CPU bus write. Routes through BlockCache's per-byte
    /// coverage counter (1-byte read + branch on the fast path) so most
    /// writes are no-ops; only writes overlapping a cached block's PC
    /// range trigger invalidation.
    ///
    /// blargg cpu_test5 needs this — the test framework's instr_template
    /// is in RAM and gets rewritten between sub-tests. Without SMC notify,
    /// the cache returns stale block IR for the rewritten template's PC
    /// and runs the wrong opcode.
    /// </summary>
    public void NotifyBusWrite(uint addr)
    {
        _blockCache?.NotifyMemoryWrite(addr);
    }

    /// <summary>
    /// Minimal IMemoryBus adapter so BlockDetector can read instruction
    /// bytes from NesMemoryBus during cache-miss block detection. Only
    /// 8-bit reads are needed (BlockDetector calls ReadByte per step);
    /// other widths fall back to byte-by-byte composition.
    /// </summary>
    private sealed class BusAdapter : IMemoryBus
    {
        private readonly NesMemoryBus _bus;
        public BusAdapter(NesMemoryBus bus) => _bus = bus;
        public byte   ReadByte    (uint addr) => _bus.ReadByte((ushort)addr);
        public ushort ReadHalfword(uint addr) =>
            (ushort)(_bus.ReadByte((ushort)addr) | (_bus.ReadByte((ushort)(addr + 1)) << 8));
        public uint   ReadWord    (uint addr) => (uint)(
              _bus.ReadByte((ushort)addr)
            | (_bus.ReadByte((ushort)(addr + 1)) << 8)
            | (_bus.ReadByte((ushort)(addr + 2)) << 16)
            | (_bus.ReadByte((ushort)(addr + 3)) << 24));
        public void WriteByte    (uint addr, byte   v) => _bus.WriteByte((ushort)addr, v);
        public void WriteHalfword(uint addr, ushort v)
        {
            _bus.WriteByte((ushort)addr,        (byte)v);
            _bus.WriteByte((ushort)(addr + 1), (byte)(v >> 8));
        }
        public void WriteWord    (uint addr, uint   v)
        {
            _bus.WriteByte((ushort)addr,        (byte)v);
            _bus.WriteByte((ushort)(addr + 1), (byte)(v >> 8));
            _bus.WriteByte((ushort)(addr + 2), (byte)(v >> 16));
            _bus.WriteByte((ushort)(addr + 3), (byte)(v >> 24));
        }
    }

    // --- NMI ------------------------------------------------------------

    private void NmiInterrupt()
    {
        // Mirror LegacyCpu.NmiInterrupt: push PC.hi, PC.lo, P|0x20; set
        // I=1; PC = *(0xFFFA). Stack lives at $0100 | SP.
        ushort pcCur = ReadU16(_pcOff);
        byte sp = _state[_spOff];
        byte pushedFlags = (byte)(_state[_pOff] | 0x20);

        _bus.WriteByte((ushort)(0x100 | sp), (byte)(pcCur >> 8));   sp--;
        _bus.WriteByte((ushort)(0x100 | sp), (byte)(pcCur & 0xFF)); sp--;
        _bus.WriteByte((ushort)(0x100 | sp), pushedFlags);          sp--;
        _state[_spOff] = sp;

        ushort lo = _bus.ReadByte(_nmiVector);
        ushort hi = _bus.ReadByte((ushort)(_nmiVector + 1));
        WriteU16(_pcOff, (ushort)((hi << 8) | lo));
        _state[_pOff] = (byte)(_state[_pOff] | 0x04);   // I=1
        _interruptCycle = 7;
    }

    // --- Function-pointer resolution ------------------------------------

    private IntPtr ResolveFunctionPointer(string setName, DecodedInstruction decoded)
    {
        if (_fnPtrByDef.TryGetValue(decoded.Instruction, out var cached)) return cached;
        var p = ResolveFunctionPointerSlow(setName, decoded);
        _fnPtrByDef[decoded.Instruction] = p;
        return p;
    }

    private IntPtr ResolveFunctionPointerSlow(string setName, DecodedInstruction decoded)
    {
        var fmt = decoded.Format;
        var def = decoded.Instruction;

        // Mirror CpuExecutor.ResolveFunctionPointerSlow: when the format
        // has multiple instructions sharing a mnemonic, append the
        // selector value to the function name; otherwise just the mnemonic.
        var ambiguous = false;
        for (int i = 0, hits = 0; i < fmt.Instructions.Count; i++)
            if (fmt.Instructions[i].Mnemonic == def.Mnemonic && ++hits > 1)
            { ambiguous = true; break; }

        var disambig = ambiguous && def.Selector is not null
            ? $"{def.Mnemonic}_{def.Selector.Value}"
            : def.Mnemonic;
        var fnName = $"Execute_{setName}_{fmt.Name}_{disambig}";
        return _rt.GetFunctionPointer(fnName);
    }

    // --- State buffer accessors -----------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ushort ReadU16(int off)
        => BinaryPrimitives.ReadUInt16LittleEndian(_state.AsSpan(off, 2));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteU16(int off, ushort v)
        => BinaryPrimitives.WriteUInt16LittleEndian(_state.AsSpan(off, 2), v);

    // --- Extern shims (called from JIT'd IR) ----------------------------

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static byte MemRead8(uint addr)
        => _activeBus is not null ? _activeBus.ReadByte((ushort)addr) : (byte)0xFF;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void MemWrite8(uint addr, byte value)
    {
        if (_activeBus is null) return;
        _activeBus.WriteByte((ushort)addr, value);
    }
}
