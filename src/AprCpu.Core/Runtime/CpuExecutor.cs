using System.Buffers.Binary;
using AprCpu.Core.Decoder;
using AprCpu.Core.IR;
using AprCpu.Core.JsonSpec;

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
    private readonly byte[]      _state;
    private readonly Dictionary<string, IntPtr> _fnPtrCache = new(StringComparer.Ordinal);

    private readonly int   _pcRegIndex;
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
        _defaultMode = new ModeInfo(instructionSet, decoder);
        _pcRegIndex = ResolvePcIndex(rt);
        _state = new byte[(int)_rt.StateSizeBytes];
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
        _pcRegIndex = ResolvePcIndex(rt);
        _state = new byte[(int)_rt.StateSizeBytes];

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
        get => ReadGpr(_pcRegIndex);
        set => WriteGpr(_pcRegIndex, value);
    }

    public uint ReadGpr(int regIndex)
        => BinaryPrimitives.ReadUInt32LittleEndian(_state.AsSpan((int)_rt.GprOffset(regIndex), 4));

    public void WriteGpr(int regIndex, uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(_state.AsSpan((int)_rt.GprOffset(regIndex), 4), value);

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

    /// <summary>Run exactly one instruction.</summary>
    public DecodedInstruction Step()
    {
        var mode = CurrentMode();
        var pc = Pc;

        // Pre-set R15 to PC + pc_offset_bytes so the IR's "read R15"
        // returns the correct pipeline-offset value mid-execution.
        var pcReadValue = pc + mode.PcOffsetBytes;
        WriteGpr(_pcRegIndex, pcReadValue);

        // Clear the "PC was written" sticky flag. Branch / BX / LDM-PC /
        // ALU-with-Rd=PC emitters set this to 1; we read it back below.
        // This is the authoritative branch-detect signal — the historical
        // (postR15 != pcReadValue) check fails when the branch target
        // equals pre-set R15, which is exactly the Thumb BCond +0
        // compiler idiom (skip-next-instruction). It's still kept as a
        // belt-and-suspenders fallback for any PC writer not yet
        // instrumented.
        _state[(int)_rt.PcWrittenOffset] = 0;

        // Snapshot the dispatch selector (e.g. CPSR.T) so we can detect
        // mode switches even when the new PC value happens to equal the
        // pre-set pcReadValue. (Real case: BX target == current+offset.)
        uint preSelector = _readSelector?.Invoke(_state) ?? 0;

        // Fetch.
        uint instructionWord = mode.InstrSizeBytes switch
        {
            4 => _bus.ReadWord(pc),
            2 => _bus.ReadHalfword(pc),
            _ => throw new NotSupportedException($"instruction size {mode.InstrSizeBytes} unsupported.")
        };

        var decoded = mode.Decoder.Decode(instructionWord)
            ?? throw new InvalidOperationException(
                $"Undecodable instruction 0x{instructionWord:X8} at PC=0x{pc:X8} ({mode.Set.Name}).");

        var fnPtr = ResolveFunctionPointer(decoded, mode.Set.Name);
        var fn = (delegate* unmanaged[Cdecl]<byte*, uint, void>)fnPtr;
        fixed (byte* p = _state)
            fn(p, instructionWord);

        // Did the instruction write PC or switch modes?
        var postR15 = ReadGpr(_pcRegIndex);
        uint postSelector = _readSelector?.Invoke(_state) ?? 0;
        bool flagged   = _state[(int)_rt.PcWrittenOffset] != 0;
        bool branched  = flagged
                       || postR15 != pcReadValue
                       || postSelector != preSelector;
        if (!branched)
            WriteGpr(_pcRegIndex, pc + mode.InstrSizeBytes);
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

        if (_fnPtrCache.TryGetValue(fnName, out var p)) return p;
        p = _rt.GetFunctionPointer(fnName);
        _fnPtrCache[fnName] = p;
        return p;
    }
}
