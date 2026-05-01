using System.Buffers.Binary;
using AprCpu.Core.Decoder;
using AprCpu.Core.JsonSpec;

namespace AprCpu.Core.Runtime;

/// <summary>
/// Phase 3.2 — fetch-decode-execute loop. Walks PC, reads the
/// instruction word from a host <see cref="IMemoryBus"/>, decodes it
/// via <see cref="DecoderTable"/>, and dispatches to the JIT'd function
/// pointer for that instruction.
///
/// No code cache yet (Phase 7 work). Function pointers ARE cached by
/// disambiguated name, so the second hit on the same instruction skips
/// MCJIT's name-resolution overhead.
///
/// PC convention (matches what the IR emitters expect):
/// - State.R15 (or whatever <c>register_file.general_purpose.pc_index</c>
///   declares) holds <b>"address of currently-executing instruction +
///   pc_offset_bytes"</b> while the JIT'd code is running.
/// - Between Step() calls, R15 holds <b>"address of next instruction to
///   execute"</b>.
/// - Step() handles the +/- pc_offset_bytes adjustment around the JIT
///   call, and detects PC writes by comparing post-execution R15 against
///   the pre-set "current + offset" value.
/// </summary>
public sealed unsafe class CpuExecutor
{
    private readonly HostRuntime          _rt;
    private readonly InstructionSetSpec   _set;
    private readonly DecoderTable         _decoder;
    private readonly IMemoryBus           _bus;
    private readonly byte[]               _state;
    private readonly Dictionary<string, IntPtr> _fnPtrCache = new(StringComparer.Ordinal);

    private readonly int  _pcRegIndex;
    private readonly uint _pcOffsetBytes;
    private readonly uint _instrSizeBytes;
    private readonly ulong _pcSlotOffset;

    public CpuExecutor(
        HostRuntime rt,
        InstructionSetSpec instructionSet,
        DecoderTable decoder,
        IMemoryBus bus)
    {
        _rt = rt;
        _set = instructionSet;
        _decoder = decoder;
        _bus = bus;

        if (!_set.WidthBits.Fixed.HasValue)
            throw new NotSupportedException(
                $"Instruction set '{_set.Name}' has variable width — Phase 3.2 only supports fixed-width sets.");
        _instrSizeBytes  = (uint)(_set.WidthBits.Fixed.Value / 8);
        _pcOffsetBytes   = (uint)_set.PcOffsetBytes;

        _pcRegIndex = _rt.Layout.RegisterFile.GeneralPurpose.PcIndex
            ?? throw new InvalidOperationException(
                "register_file.general_purpose.pc_index must be declared in spec for the executor to know which GPR is PC.");

        _state = new byte[(int)_rt.StateSizeBytes];
        _pcSlotOffset = _rt.GprOffset(_pcRegIndex);
    }

    /// <summary>Backing CPU state buffer (mirrors the LLVM struct layout).</summary>
    public Span<byte> State => _state;

    /// <summary>
    /// Address of the next instruction to fetch. Reading/writing this
    /// goes through the PC register slot.
    /// </summary>
    public uint Pc
    {
        get => ReadGpr(_pcRegIndex);
        set => WriteGpr(_pcRegIndex, value);
    }

    /// <summary>Read a GPR from the host-side state buffer.</summary>
    public uint ReadGpr(int regIndex)
        => BinaryPrimitives.ReadUInt32LittleEndian(_state.AsSpan((int)_rt.GprOffset(regIndex), 4));

    /// <summary>Write a GPR to the host-side state buffer.</summary>
    public void WriteGpr(int regIndex, uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(_state.AsSpan((int)_rt.GprOffset(regIndex), 4), value);

    public uint ReadStatus(string name, string? mode = null)
        => BinaryPrimitives.ReadUInt32LittleEndian(_state.AsSpan((int)_rt.StatusOffset(name, mode), 4));

    public void WriteStatus(string name, uint value, string? mode = null)
        => BinaryPrimitives.WriteUInt32LittleEndian(_state.AsSpan((int)_rt.StatusOffset(name, mode), 4), value);

    /// <summary>
    /// Run exactly one instruction. Returns the decoded instruction
    /// metadata (useful for tracing / tests). Throws if the bus returns
    /// an undecodable word at the current PC.
    /// </summary>
    public DecodedInstruction Step()
    {
        var pc = Pc;

        // Pre-set R15 to PC + pc_offset_bytes so the IR's "read R15"
        // returns the correct pipeline-offset value mid-execution.
        var pcReadValue = pc + _pcOffsetBytes;
        WriteGpr(_pcRegIndex, pcReadValue);

        // Fetch.
        uint instructionWord = _instrSizeBytes switch
        {
            4 => _bus.ReadWord(pc),
            2 => _bus.ReadHalfword(pc),
            _ => throw new NotSupportedException($"instruction size {_instrSizeBytes} unsupported.")
        };

        // Decode.
        var decoded = _decoder.Decode(instructionWord)
            ?? throw new InvalidOperationException(
                $"Undecodable instruction 0x{instructionWord:X8} at PC=0x{pc:X8} ({_set.Name}).");

        // Look up (or cache) the JIT'd function pointer.
        var fnPtr = ResolveFunctionPointer(decoded);
        var fn = (delegate* unmanaged[Cdecl]<byte*, uint, void>)fnPtr;

        // Invoke.
        fixed (byte* p = _state)
            fn(p, instructionWord);

        // Determine next PC.
        var postR15 = ReadGpr(_pcRegIndex);
        if (postR15 == pcReadValue)
        {
            // Instruction did not write PC — advance.
            WriteGpr(_pcRegIndex, pc + _instrSizeBytes);
        }
        // Else: instruction wrote PC (branch / exception / ALU-to-PC) — postR15
        // already holds the next instruction address; leave it.

        return decoded;
    }

    /// <summary>
    /// Run up to <paramref name="maxSteps"/> instructions. Returns the
    /// number actually executed.
    /// </summary>
    public int Run(int maxSteps)
    {
        int n = 0;
        while (n < maxSteps)
        {
            Step();
            n++;
        }
        return n;
    }

    private IntPtr ResolveFunctionPointer(DecodedInstruction decoded)
    {
        var format = decoded.Format;
        var def    = decoded.Instruction;

        // Mirror SpecCompiler's name-disambiguation: if multiple instructions
        // in this format share a mnemonic, suffix the one with the selector value.
        var ambiguous = false;
        for (int i = 0, hits = 0; i < format.Instructions.Count; i++)
            if (format.Instructions[i].Mnemonic == def.Mnemonic && ++hits > 1)
            { ambiguous = true; break; }

        var disambig = ambiguous && def.Selector is not null
            ? $"{def.Mnemonic}_{def.Selector.Value}"
            : def.Mnemonic;
        var fnName = $"Execute_{_set.Name}_{format.Name}_{disambig}";

        if (_fnPtrCache.TryGetValue(fnName, out var p)) return p;
        p = _rt.GetFunctionPointer(fnName);
        _fnPtrCache[fnName] = p;
        return p;
    }
}
