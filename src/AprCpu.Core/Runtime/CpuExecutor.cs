using System.Buffers.Binary;
using AprCpu.Core.Compilation;
using AprCpu.Core.Decoder;
using AprCpu.Core.IR;
using AprCpu.Core.JsonSpec;
using AprCpu.Core.Runtime.Gba;
using LLVMSharp.Interop;

namespace AprCpu.Core.Runtime;

/// <summary>
/// Phase 3.2 + 4.4 — fetch-decode-execute loop.
///
/// Walks PC, reads the instruction word from a host
/// <see cref="IMemoryBus"/>, decodes it via the appropriate
/// <see cref="DecoderTable"/> for the CPU's <b>current instruction set</b>,
/// and dispatches to the JIT'd function pointer.
///
/// Supports multi-instruction-set CPUs (ARM7TDMI = ARM + Thumb) via the
/// spec's <c>instruction_set_dispatch</c>: each <see cref="Step"/> reads
/// the selector (e.g. <c>CPSR.T</c>) and picks the matching set's
/// metadata (decoder, PC offset, instruction width). Mode switches
/// happen automatically on the next step after BX writes CPSR.T.
///
/// No code cache yet (Phase 7 work). Function pointers are cached by
/// disambiguated name across all instruction sets.
/// </summary>
public sealed unsafe class CpuExecutor
{
    private readonly HostRuntime _rt;
    private readonly IMemoryBus  _bus;
    // Phase 7 E.a: typed cache for the GBA bus so Step() can take a
    // fast path on instruction fetch (skip bus.ReadWord's interface
    // dispatch + region switch when PC is in cart ROM, the common case).
    // Null when the bus isn't GbaMemoryBus (host tests, future GB CpuExecutor
    // use, etc.) — fast path is gated by null check.
    private readonly GbaMemoryBus? _gbaBus;
    private readonly byte[]      _state;

    // Phase 7 B.f: permanent pin of the state buffer.
    // Pre-Phase-7 every Step did `fixed (byte* p = _state) fn(p, ...)`,
    // which pins/unpins the array each call (~50 ns × 84M instr/test).
    // Since the buffer is small (a few hundred bytes), a small fixed
    // sized GC root is acceptable. Pin once in ctor, free in finaliser.
    private readonly System.Runtime.InteropServices.GCHandle _stateHandle;
    private readonly byte* _statePtr;

    // Phase 7 F.x: keyed by InstructionDef reference (object identity)
    // rather than by formatted string name — skips the per-instruction
    // string interpolation (`$"Execute_{setName}_{format.Name}_{disambig}"`)
    // and dictionary string-hash on the hot dispatch path. The string is
    // only built on cache miss, which happens once per (opcode × selector)
    // combo at first encounter.
    //
    // ReferenceEqualityComparer ensures the key compares by identity,
    // not by (potentially overridden) Equals — InstructionDef is a record
    // with structural Equals, so without this we'd be doing a deep field
    // compare per lookup. Identity is correct because the spec loader
    // hands out the SAME InstructionDef instance for each opcode and the
    // decoder returns it via `decoded.Instruction`.
    private readonly Dictionary<JsonSpec.InstructionDef, IntPtr> _fnPtrByDef
        = new(System.Collections.Generic.ReferenceEqualityComparer.Instance);

    // Phase 7 A.6 — optional block-JIT mode. When _compileResult is set,
    // Step() takes the block path: cache lookup, miss → detect+compile,
    // hit → call block fn (handles N instructions atomically).
    // null → original per-instruction dispatch path.
    private SpecCompiler.CompileResult? _compileResult;
    private Dictionary<string, BlockCache>?    _blockCachesBySetName;
    private Dictionary<string, BlockDetector>? _blockDetectorsBySetName;

    /// <summary>
    /// Phase 7 A.6 — number of host instructions executed by the most
    /// recent <see cref="Step"/> call. Per-instruction mode always
    /// reports 1; block-JIT mode reports the size of the block executed.
    /// External cycle accounting (e.g. <see cref="Gba.GbaSystemRunner"/>)
    /// multiplies its per-instruction Tick by this to keep the GBA clock
    /// in sync when one Step represents many instructions.
    /// </summary>
    public int LastStepInstructionCount { get; private set; } = 1;

    /// <summary>
    /// Phase 7 A.6.1 — actual cycles consumed by the most recent Step.
    /// Set from the cycles_left downcount delta (block-JIT) or from
    /// cyclesPerInstr × LastStepInstructionCount (per-instr fallback).
    /// Used by GbaSystemRunner to feed the scheduler accurate cycle counts.
    /// </summary>
    public int LastStepCycles { get; private set; } = 4;

    /// <summary>
    /// Phase 7 A.6.1 — predictive-downcounting budget for block-JIT.
    /// Host loads cycles-until-next-event before calling Step(); block IR
    /// decrements it per instruction and exits when it hits zero. Read
    /// after Step() to compute actually-consumed cycles. Per-instr mode
    /// ignores this (always charges cyclesPerInstr per Step).
    /// </summary>
    public int CyclesLeft
    {
        get => System.Runtime.InteropServices.Marshal.ReadInt32((IntPtr)(_statePtr + _cyclesLeftOffset));
        set => System.Runtime.InteropServices.Marshal.WriteInt32((IntPtr)(_statePtr + _cyclesLeftOffset), value);
    }

    /// <summary>Total number of distinct blocks compiled into the JIT
    /// (cache misses) — block-JIT mode only.</summary>
    public long BlocksCompiled { get; private set; }

    /// <summary>Total number of block-fn invocations (cache hits +
    /// misses) — block-JIT mode only.</summary>
    public long BlocksExecuted { get; private set; }

    private readonly int   _pcRegIndex;
    // Phase 7 B.e: state-buffer field offsets cached at construction.
    // Pre-cache, every Step() called _rt.PcWrittenOffset and _rt.GprOffset(_pcRegIndex)
    // — both cascade into LLVM.OffsetOfElement P/Invoke. Per-instruction
    // overhead from those PInvokes was ~5× per Step (one for PcWritten,
    // one for pre-set R15, one for post-read R15, one for fall-through PC
    // update + the conditional path). Now the offsets are int fields,
    // looked up once at construction.
    private readonly int   _pcGprOffset;
    private readonly int   _pcWrittenOffset;
    private readonly int   _cyclesLeftOffset;
    private readonly ModeInfo _defaultMode;
    private readonly Dictionary<uint, ModeInfo>? _modesBySelectorValue;
    private readonly Func<byte[], uint>?         _readSelector;

    private readonly struct ModeInfo
    {
        public readonly InstructionSetSpec Set;
        public readonly DecoderTable       Decoder;
        public readonly uint               PcOffsetBytes;
        public readonly uint               InstrSizeBytes;
        public ModeInfo(InstructionSetSpec set, DecoderTable decoder)
        {
            Set = set;
            Decoder = decoder;
            if (!set.WidthBits.Fixed.HasValue)
                throw new NotSupportedException(
                    $"Instruction set '{set.Name}' has variable width — not supported by CpuExecutor yet.");
            InstrSizeBytes = (uint)(set.WidthBits.Fixed.Value / 8);
            PcOffsetBytes  = (uint)set.PcOffsetBytes;
        }
    }

    /// <summary>Single-set constructor — for tests / chips with no mode switching.</summary>
    public CpuExecutor(
        HostRuntime rt,
        InstructionSetSpec instructionSet,
        DecoderTable decoder,
        IMemoryBus bus)
    {
        _rt = rt;
        _bus = bus;
        _gbaBus = bus as GbaMemoryBus;
        _defaultMode = new ModeInfo(instructionSet, decoder);
        _pcRegIndex = ResolvePcIndex(rt);
        _state = new byte[(int)_rt.StateSizeBytes];
        _stateHandle = System.Runtime.InteropServices.GCHandle.Alloc(
            _state, System.Runtime.InteropServices.GCHandleType.Pinned);
        _statePtr = (byte*)_stateHandle.AddrOfPinnedObject();
        _pcGprOffset = (int)_rt.GprOffset(_pcRegIndex);
        _pcWrittenOffset = (int)_rt.PcWrittenOffset;
        _cyclesLeftOffset = (int)_rt.CyclesLeftOffset;
    }

    /// <summary>
    /// Free the pinned GC handle on destruction. The state buffer is
    /// pinned for the lifetime of the executor (Phase 7 B.f); a small
    /// fixed-size GC root is acceptable for a long-lived runtime object.
    /// </summary>
    ~CpuExecutor()
    {
        if (_stateHandle.IsAllocated) _stateHandle.Free();
    }

    /// <summary>
    /// Multi-set constructor — for ARM/Thumb-style chips. The
    /// <paramref name="dispatch"/> tells us which selector to read
    /// (e.g. <c>"CPSR.T"</c>) and which selector value picks which
    /// instruction set name.
    /// </summary>
    public CpuExecutor(
        HostRuntime rt,
        IReadOnlyDictionary<string, (InstructionSetSpec Set, DecoderTable Decoder)> setsByName,
        InstructionSetDispatch dispatch,
        IMemoryBus bus)
    {
        _rt  = rt;
        _bus = bus;
        _gbaBus = bus as GbaMemoryBus;
        _pcRegIndex = ResolvePcIndex(rt);
        _state = new byte[(int)_rt.StateSizeBytes];
        _stateHandle = System.Runtime.InteropServices.GCHandle.Alloc(
            _state, System.Runtime.InteropServices.GCHandleType.Pinned);
        _statePtr = (byte*)_stateHandle.AddrOfPinnedObject();
        _pcGprOffset = (int)_rt.GprOffset(_pcRegIndex);
        _pcWrittenOffset = (int)_rt.PcWrittenOffset;
        _cyclesLeftOffset = (int)_rt.CyclesLeftOffset;

        // Build value→ModeInfo map from selector_values.
        _modesBySelectorValue = new Dictionary<uint, ModeInfo>();
        foreach (var (k, setName) in dispatch.SelectorValues)
        {
            if (!setsByName.TryGetValue(setName, out var pair))
                throw new InvalidOperationException(
                    $"instruction_set_dispatch.selector_values references unknown set '{setName}'.");
            _modesBySelectorValue[ParseSelectorKey(k)] = new ModeInfo(pair.Set, pair.Decoder);
        }

        // Selector parser for the most common form: "CPSR.T" → bit 5 of CPSR.T (= bit 5).
        // For now hardcode CPSR.<flag>; generalise when a non-ARM spec needs it.
        _readSelector = BuildSelectorReader(dispatch.Selector);

        // Sentinel default — used only by accessors that need _something_
        // before any Step has run.
        _defaultMode = _modesBySelectorValue.Values.First();
    }

    private static int ResolvePcIndex(HostRuntime rt)
        => rt.Layout.RegisterFile.GeneralPurpose.PcIndex
           ?? throw new InvalidOperationException(
               "register_file.general_purpose.pc_index must be declared in spec.");

    private static uint ParseSelectorKey(string s) => s switch
    {
        "0" => 0u,
        "1" => 1u,
        _ when s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
              => Convert.ToUInt32(s.Substring(2), 16),
        _   => uint.Parse(s),
    };

    private Func<byte[], uint>? BuildSelectorReader(string selector)
    {
        // Forms supported: "CPSR.T", "CPSR.M[0]", or generic "<status>.<flag>".
        var parts = selector.Split('.');
        if (parts.Length != 2)
            throw new NotSupportedException($"Unsupported selector form '{selector}'.");
        var statusName = parts[0];
        var flagName   = parts[1];
        var bitIdx = _rt.Layout.GetStatusFlagBitIndex(statusName, flagName);
        var statusOff = _rt.StatusOffset(statusName);
        return state =>
        {
            var v = BinaryPrimitives.ReadUInt32LittleEndian(state.AsSpan((int)statusOff, 4));
            return (v >> bitIdx) & 1u;
        };
    }

    /// <summary>Backing CPU state buffer (mirrors the LLVM struct layout).</summary>
    public Span<byte> State => _state;

    public uint Pc
    {
        // Phase 7 B.e: PC accessor uses cached _pcGprOffset, skipping
        // the per-call _rt.GprOffset → LLVM.OffsetOfElement P/Invoke.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get => BinaryPrimitives.ReadUInt32LittleEndian(_state.AsSpan(_pcGprOffset, 4));
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        set => BinaryPrimitives.WriteUInt32LittleEndian(_state.AsSpan(_pcGprOffset, 4), value);
    }

    public uint ReadGpr(int regIndex)
        => BinaryPrimitives.ReadUInt32LittleEndian(_state.AsSpan((int)_rt.GprOffset(regIndex), 4));

    public void WriteGpr(int regIndex, uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(_state.AsSpan((int)_rt.GprOffset(regIndex), 4), value);

    // Phase 7 B.e: PC-specific fast paths used by Step()'s hot loop —
    // bypass the regIndex parameter entirely so we don't pay the
    // _rt.GprOffset PInvoke per call.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private uint ReadPc()
        => BinaryPrimitives.ReadUInt32LittleEndian(_state.AsSpan(_pcGprOffset, 4));
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private void WritePc(uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(_state.AsSpan(_pcGprOffset, 4), value);

    public uint ReadStatus(string name, string? mode = null)
        => BinaryPrimitives.ReadUInt32LittleEndian(_state.AsSpan((int)_rt.StatusOffset(name, mode), 4));

    public void WriteStatus(string name, uint value, string? mode = null)
        => BinaryPrimitives.WriteUInt32LittleEndian(_state.AsSpan((int)_rt.StatusOffset(name, mode), 4), value);

    private ModeInfo CurrentMode()
    {
        if (_readSelector is null) return _defaultMode;
        var sel = _readSelector(_state);
        if (!_modesBySelectorValue!.TryGetValue(sel, out var m))
            throw new InvalidOperationException(
                $"Selector value 0x{sel:X} has no mapped instruction set in dispatch table.");
        return m;
    }

    /// <summary>Total number of instructions executed since construction.</summary>
    public long InstructionsExecuted { get; private set; }

    /// <summary>
    /// Phase 7 A.6 — opt this executor into block-JIT mode. After this
    /// call, every <see cref="Step"/> executes a whole block (cache
    /// hit) or detects + compiles + caches + executes a fresh block
    /// (cache miss). <see cref="LastStepInstructionCount"/> reports
    /// the size of the block that just ran.
    ///
    /// <paramref name="compileResult"/> must be the same one used to
    /// build the <see cref="HostRuntime"/> — its EmitterRegistry /
    /// ResolverRegistry / Layout are reused to compile per-block IR
    /// into fresh modules that get added to the live JIT via
    /// <see cref="HostRuntime.AddModule"/>.
    /// </summary>
    public void EnableBlockJit(SpecCompiler.CompileResult compileResult)
    {
        _compileResult = compileResult;
        _blockCachesBySetName    = new Dictionary<string, BlockCache>(StringComparer.Ordinal);
        _blockDetectorsBySetName = new Dictionary<string, BlockDetector>(StringComparer.Ordinal);

        // Pre-populate one cache + detector per known instruction set.
        if (_modesBySelectorValue is not null)
        {
            foreach (var mode in _modesBySelectorValue.Values)
            {
                if (_blockCachesBySetName.ContainsKey(mode.Set.Name)) continue;
                _blockCachesBySetName[mode.Set.Name]    = new BlockCache();
                _blockDetectorsBySetName[mode.Set.Name] = new BlockDetector(mode.Set, mode.Decoder);
            }
        }
        else
        {
            _blockCachesBySetName[_defaultMode.Set.Name]    = new BlockCache();
            _blockDetectorsBySetName[_defaultMode.Set.Name] = new BlockDetector(_defaultMode.Set, _defaultMode.Decoder);
        }
    }

    /// <summary>
    /// Run exactly one instruction (per-instr mode) or one block
    /// (block-JIT mode, see <see cref="EnableBlockJit"/>). In block
    /// mode the returned <see cref="DecodedInstruction"/> is a default
    /// value — callers wanting per-instruction info should not enable
    /// block-JIT.
    /// </summary>
    public DecodedInstruction Step()
    {
        if (_compileResult is not null)
        {
            StepBlock();
            return default!;   // block mode: no single decoded-instr to return
        }

        LastStepInstructionCount = 1;
        LastStepCycles           = 4;   // 1S cycle approximation
        InstructionsExecuted++;
        var mode = CurrentMode();
        var pc = ReadPc();   // Phase 7 B.e: fast cached-offset accessor

        // Pre-set R15 to PC + pc_offset_bytes so the IR's "read R15"
        // returns the correct pipeline-offset value mid-execution.
        var pcReadValue = pc + mode.PcOffsetBytes;
        WritePc(pcReadValue);

        // Clear the "PC was written" sticky flag. Branch / BX / LDM-PC /
        // ALU-with-Rd=PC emitters set this to 1; we read it back below.
        // This is the authoritative branch-detect signal — the historical
        // (postR15 != pcReadValue) check fails when the branch target
        // equals pre-set R15, which is exactly the Thumb BCond +0
        // compiler idiom (skip-next-instruction). It's still kept as a
        // belt-and-suspenders fallback for any PC writer not yet
        // instrumented.
        _state[_pcWrittenOffset] = 0;   // Phase 7 B.e: cached offset

        // Snapshot the dispatch selector (e.g. CPSR.T) so we can detect
        // mode switches even when the new PC value happens to equal the
        // pre-set pcReadValue. (Real case: BX target == current+offset.)
        uint preSelector = _readSelector?.Invoke(_state) ?? 0;

        // Tell the bus where the CPU is about to execute BEFORE we fetch
        // — the fetch itself is a BIOS read when pc < 0x4000, and the
        // bus's open-bus check needs to see the new pc, not the previous
        // step's. Without this the very first fetch after a SWI / IRQ /
        // BX into BIOS would return the stale sticky value and the CPU
        // would decode garbage at the vector address.
        _bus.NotifyExecutingPc(pc);

        // Fetch. Phase 7 E.a: fast path when bus is GbaMemoryBus and PC is
        // in cart ROM (0x08000000+). Bypasses interface dispatch + bus.Locate
        // + region switch — direct array index. The vast majority of GBA
        // execution lives in cart ROM, so this catches >99% of fetches.
        // Falls through to the regular bus.ReadWord/ReadHalfword for BIOS
        // (rare, but happens during boot) and any other region (unusual).
        uint instructionWord;
        var gbaBus = _gbaBus;
        if (gbaBus is not null
            && pc >= GbaMemoryMap.RomBase
            && pc < GbaMemoryMap.RomBase + (uint)gbaBus.Rom.Length)
        {
            int off = (int)(pc - GbaMemoryMap.RomBase);
            var rom = gbaBus.Rom;
            instructionWord = mode.InstrSizeBytes switch
            {
                4 when off + 3 < rom.Length =>
                    BinaryPrimitives.ReadUInt32LittleEndian(rom.AsSpan(off, 4)),
                2 when off + 1 < rom.Length =>
                    BinaryPrimitives.ReadUInt16LittleEndian(rom.AsSpan(off, 2)),
                _ => 0u  // address landed past rom end; bus would also return 0
            };
        }
        else
        {
            instructionWord = mode.InstrSizeBytes switch
            {
                4 => _bus.ReadWord(pc),
                2 => _bus.ReadHalfword(pc),
                _ => throw new NotSupportedException($"instruction size {mode.InstrSizeBytes} unsupported.")
            };
        }

        // Tell the bus what we just fetched, so the BIOS sticky value
        // can be updated when this fetch came from BIOS.
        _bus.NotifyInstructionFetch(pc, instructionWord, mode.InstrSizeBytes);

        var decoded = mode.Decoder.Decode(instructionWord)
            ?? throw new InvalidOperationException(
                $"Undecodable instruction 0x{instructionWord:X8} at PC=0x{pc:X8} ({mode.Set.Name}).");

        var fnPtr = ResolveFunctionPointer(decoded, mode.Set.Name);
        var fn = (delegate* unmanaged[Cdecl]<byte*, uint, void>)fnPtr;
        // Phase 7 B.f: use cached pinned pointer instead of per-step fixed.
        fn(_statePtr, instructionWord);

        // Did the instruction write PC or switch modes?
        var postR15 = ReadPc();   // Phase 7 B.e: fast cached-offset accessor
        uint postSelector = _readSelector?.Invoke(_state) ?? 0;
        bool flagged   = _state[_pcWrittenOffset] != 0;
        bool branched  = flagged
                       || postR15 != pcReadValue
                       || postSelector != preSelector;
        if (!branched)
            WritePc(pc + mode.InstrSizeBytes);
        // Else: branch / exception / ALU-to-PC / mode switch wrote new PC.

        return decoded;
    }

    public int Run(int maxSteps)
    {
        int n = 0;
        while (n < maxSteps) { Step(); n++; }
        return n;
    }

    public (int Executed, bool Halted) RunUntilHalt(int maxSteps)
    {
        uint lastPc = uint.MaxValue;
        int n = 0;
        while (n < maxSteps)
        {
            var pc = Pc;
            if (pc == lastPc) return (n, true);
            Step();
            lastPc = pc;
            n++;
        }
        return (n, false);
    }

    private IntPtr ResolveFunctionPointer(DecodedInstruction decoded, string setName)
    {
        // Hot path: identity-keyed cache hit, zero allocation.
        if (_fnPtrByDef.TryGetValue(decoded.Instruction, out var cached)) return cached;

        // Cold path (first call per opcode×selector): resolve the IR
        // function name via spec metadata + disambiguation, then cache.
        var p = ResolveFunctionPointerSlow(decoded, setName);
        _fnPtrByDef[decoded.Instruction] = p;
        return p;
    }

    private IntPtr ResolveFunctionPointerSlow(DecodedInstruction decoded, string setName)
    {
        var format = decoded.Format;
        var def    = decoded.Instruction;

        var ambiguous = false;
        for (int i = 0, hits = 0; i < format.Instructions.Count; i++)
            if (format.Instructions[i].Mnemonic == def.Mnemonic && ++hits > 1)
            { ambiguous = true; break; }

        var disambig = ambiguous && def.Selector is not null
            ? $"{def.Mnemonic}_{def.Selector.Value}"
            : def.Mnemonic;
        var fnName = $"Execute_{setName}_{format.Name}_{disambig}";
        return _rt.GetFunctionPointer(fnName);
    }

    // ---------------------------------------------------------------- Phase 7 A.6 ----
    // Block-JIT dispatch — taken when EnableBlockJit has been called.

    private void StepBlock()
    {
        var mode = CurrentMode();
        var pc = ReadPc();

        // Notify bus before any potential extern call from inside the
        // block (memory_read for ROM access, etc). The block IR doesn't
        // do its own NotifyExecutingPc because that would mean an extern
        // call per block start; we do it once here.
        _bus.NotifyExecutingPc(pc);

        var cache = _blockCachesBySetName![mode.Set.Name];
        if (!cache.TryGet(pc, out var entry))
        {
            entry = CompileBlockAtPc(pc, mode);
            cache.Add(pc, entry);
        }

        // Phase 7 A.6.1 Strategy 2 — block fn now NEVER pre-sets PC nor
        // advances PC at the end. Pipeline-PC reads inside the block
        // resolve to constants (bi.Pc + offset), and GPR[15] in memory
        // is only written by REAL branches (which also set PcWritten=1).
        // After block fn returns, if PcWritten=0 then no branch happened
        // and we advance PC by total block size; if PcWritten=1 the
        // branch already wrote PC to the target, leave it.
        _state[_pcWrittenOffset] = 0;
        // Phase 7 A.6.1 — predictive downcounting. Snapshot cycles_left
        // before block runs to compute cycles actually consumed afterwards.
        // Block IR decrements cycles_left per instruction and exits early
        // when it hits zero (writing the next-instruction PC + PcWritten=1
        // so the post-block "advance PC" path doesn't overshoot).
        int cyclesBudgetBefore = CyclesLeft;
        var fn = (delegate* unmanaged[Cdecl]<byte*, void>)entry.Fn;
        fn(_statePtr);
        int cyclesConsumed = cyclesBudgetBefore - CyclesLeft;
        if (_state[_pcWrittenOffset] == 0)
        {
            // No branch fired AND budget didn't exhaust — straight-line
            // block executed fully. Advance PC by N×size. (Budget exit
            // path sets PcWritten=1 + writes next-instr PC itself.)
            var totalBytes = (uint)entry.InstructionCount * mode.InstrSizeBytes;
            WritePc(pc + totalBytes);
        }

        // Phase 7 A.6.1 — actual cycle count comes from the budget delta.
        // Convert back to instruction count for callers that still want
        // it (Scheduler.Tick uses cycles directly via LastStepCycles).
        const int instrCycleCost = 4;
        int actualInstrCount = cyclesConsumed > 0 ? cyclesConsumed / instrCycleCost : entry.InstructionCount;
        if (actualInstrCount > entry.InstructionCount) actualInstrCount = entry.InstructionCount;
        LastStepInstructionCount = actualInstrCount;
        LastStepCycles           = cyclesConsumed > 0 ? cyclesConsumed : entry.InstructionCount * instrCycleCost;
        InstructionsExecuted    += actualInstrCount;
        BlocksExecuted++;
    }

    private CachedBlock CompileBlockAtPc(uint pc, ModeInfo mode)
    {
        // Detect — read instruction words from bus until a boundary.
        var detector = _blockDetectorsBySetName![mode.Set.Name];
        var block = detector.Detect(_bus, pc, maxInstructions: 64);
        if (block.Instructions.Count == 0)
        {
            throw new InvalidOperationException(
                $"BlockJit: detector found no instructions at PC=0x{pc:X8} (set {mode.Set.Name})." +
                " Likely the bus returned undecodable bytes — fall back to per-instr Step is not yet wired up.");
        }

        // Build block IR into a fresh module, hand to JIT, look up fn ptr.
        var moduleName = $"AprCpu_BlockJit_{mode.Set.Name}_pc{pc:X8}";
        var module = LLVMModuleRef.CreateWithName(moduleName);
        var bfb = new BlockFunctionBuilder(
            module, _compileResult!.Layout,
            _compileResult.EmitterRegistry, _compileResult.ResolverRegistry);
        bfb.Build(mode.Set, block);

        _rt.AddModule(module);
        var fnName = BlockFunctionBuilder.BlockFunctionName(mode.Set.Name, pc);
        var fnPtr = _rt.GetFunctionPointer(fnName);

        BlocksCompiled++;
        // Fixed-width sets: total bytes = N × instr_size. Variable-width
        // (LR35902) callers compute via per-instr LengthBytes sum (see
        // JsonCpu.CompileBlockAtPc in AprGb.Cli).
        int totalBytes = block.Instructions.Count * (int)mode.InstrSizeBytes;
        // P1 #6 — fall-through PC = last instr's PC + length. For
        // sequential blocks equals StartPc + totalBytes; for cross-jump
        // blocks differs (last instr can be in a different PC range).
        var lastBi = block.Instructions[block.Instructions.Count - 1];
        uint nextPc = lastBi.Pc + lastBi.LengthBytes;
        // P1 SMC V2 — convex hull + precise per-instr (pc, length) arrays.
        // For sequential blocks the precise arrays equal the convex hull;
        // for cross-jump blocks (P1 #6) the precise arrays correctly skip
        // gaps so a data write between source and target portions doesn't
        // over-invalidate.
        uint covStart = uint.MaxValue, covEnd = 0;
        int n = block.Instructions.Count;
        var instrPcs = new uint[n];
        var instrLens = new byte[n];
        for (int i = 0; i < n; i++)
        {
            var bi = block.Instructions[i];
            instrPcs[i] = bi.Pc;
            instrLens[i] = bi.LengthBytes;
            if (bi.Pc < covStart) covStart = bi.Pc;
            uint instrEnd = bi.Pc + bi.LengthBytes;
            if (instrEnd > covEnd) covEnd = instrEnd;
        }
        return new CachedBlock(fnPtr, n, totalBytes, nextPc, covStart, covEnd,
            coverageInstrPcs: instrPcs, coverageInstrLens: instrLens);
    }
}
