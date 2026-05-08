// JSON-spec-driven Ricoh 2A03 backend.
//
// Loads spec/2a03/cpu.json, runs SpecCompiler over it (emits 117 LLVM
// functions covering all 256 opcodes), wires the LLVM module through
// HostRuntime, JIT-compiles, and dispatches one instruction per Step()
// call by reading the opcode byte from NesMemoryBus and invoking the
// matching native function pointer.
//
// This is the per-instr (no block-JIT) backend. NMI handling is done in
// C# at the start of each Step() — JIT'd code never sees the NMI line.
//
// Cycle accounting reuses the LegacyCpu cycle_table (256-byte per-opcode
// table) rather than the spec's coarse per-mnemonic "Nm" form, so that
// PPU NMI scheduling matches LegacyCpu byte-for-byte.

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

namespace AprNes.Cli.Cpu;

public sealed unsafe class NesJsonCpu : INesCpuBackend
{
    public string BackendName => "json-llvm";

    // Static bus reference used by the unmanaged extern shims. Last-call-
    // wins when multiple NesJsonCpu instances exist; the harness only ever
    // constructs one at a time.
    private static NesMemoryBus? _activeBus;

    private readonly NesMemoryBus _bus;
    private readonly LoadedSpec    _spec;
    private readonly HostRuntime   _rt;
    private readonly DecoderTable  _mainDecoder;

    // Identity-keyed function-pointer cache (InstructionDef → fn ptr).
    // Reference equality is fine because DecoderTable.Decode returns the
    // exact same InstructionDef instance for the same opcode.
    private readonly Dictionary<InstructionDef, IntPtr> _fnPtrByDef
        = new(System.Collections.Generic.ReferenceEqualityComparer.Instance);

    private byte[] _state = Array.Empty<byte>();
    private GCHandle _stateHandle;
    private byte* _statePtr;

    // Pre-cached field offsets — order matches cpu.json's GPR list
    // (A, X, Y) and the status section (P, SP, PC).
    private readonly int _aOff, _xOff, _yOff;
    private readonly int _pOff, _spOff, _pcOff;

    // Per-opcode cycle table mirroring LegacyCpu.cycle_tableData. Used
    // for cycle accounting since the 2A03 spec only carries per-mnemonic
    // coarse cycle forms.
    private static readonly byte[] s_cycleTable =
    {
        7,6,2,8,3,3,5,5,3,2,2,2,4,4,6,6,
        2,5,2,8,4,4,6,6,2,4,2,7,4,4,7,7,
        6,6,2,8,3,3,5,5,4,2,2,2,4,4,6,6,
        2,5,2,8,4,4,6,6,2,4,2,7,4,4,7,7,
        6,6,2,8,3,3,5,5,3,2,2,2,3,4,6,6,
        2,5,2,8,4,4,6,6,2,4,2,7,4,4,7,7,
        6,6,2,8,3,3,5,5,4,2,2,2,5,4,6,6,
        2,5,2,8,4,4,6,6,2,4,2,7,4,4,7,7,
        2,6,2,6,3,3,3,3,2,2,2,2,4,4,4,4,
        2,6,2,6,4,4,4,4,2,5,2,5,5,5,5,5,
        2,6,2,6,3,3,3,3,2,2,2,2,4,4,4,4,
        2,5,2,5,4,4,4,4,2,4,2,4,4,4,4,4,
        2,6,2,8,3,3,5,5,2,2,2,2,4,4,6,6,
        2,5,2,8,4,4,6,6,2,4,2,7,4,4,7,7,
        2,6,2,8,3,3,5,5,2,2,2,2,4,4,6,6,
        2,5,2,8,4,4,6,6,2,4,2,7,4,4,7,7
    };

    // Bookkeeping cycle count carried over from interrupt entry — mirrors
    // LegacyCpu.Interrupt_cycle. Folded into the cycle count on the next
    // Step() call so the harness sees the +7 reset / NMI prologue.
    private int _interruptCycle;

    public NesJsonCpu(NesMemoryBus bus)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));

        var specPath = LocateSpec();
        var compileResult = SpecCompiler.Compile(specPath);
        if (compileResult.Diagnostics.Count != 0)
        {
            throw new InvalidOperationException(
                "NesJsonCpu: spec compilation produced diagnostics:\n  " +
                string.Join("\n  ", compileResult.Diagnostics));
        }

        _spec = SpecLoader.LoadCpuSpec(specPath);
        if (!compileResult.DecoderTables.TryGetValue("Main", out var mainDecoder))
        {
            throw new InvalidOperationException(
                "NesJsonCpu: spec must declare a 'Main' instruction set.");
        }
        _mainDecoder = mainDecoder;

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

        _rt.Compile();

        // Register-field offsets — order matches cpu.json's GPR list
        // (A, X, Y) and status registers (P, SP, PC).
        _aOff  = (int)_rt.GprOffset(0);
        _xOff  = (int)_rt.GprOffset(1);
        _yOff  = (int)_rt.GprOffset(2);
        _pOff  = (int)_rt.StatusOffset("P");
        _spOff = (int)_rt.StatusOffset("SP");
        _pcOff = (int)_rt.StatusOffset("PC");

        // Allocate + pin the state buffer. Permanently pinned for the
        // lifetime of this NesJsonCpu — we never reallocate.
        _state = new byte[(int)_rt.StateSizeBytes];
        _stateHandle = GCHandle.Alloc(_state, GCHandleType.Pinned);
        _statePtr    = (byte*)_stateHandle.AddrOfPinnedObject();
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
        ushort lo = _bus.ReadByte(0xFFFC);
        ushort hi = _bus.ReadByte(0xFFFD);
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

        // Service pending PPU VBlank NMI before fetching the next opcode —
        // matches BoundCpu.Step ordering. Without this, vblank-wait loops
        // (BIT $2002 / BPL ...) spin forever.
        if (_bus.ConsumePpuNmi())
        {
            NmiInterrupt();
        }

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

        // Cycle accounting via the 256-byte per-opcode table (matches
        // LegacyCpu byte-for-byte). The spec carries per-mnemonic forms
        // only ("3m" for ADC regardless of addressing mode), which would
        // perturb PPU NMI delivery vs the oracle.
        int cycles = s_cycleTable[opcode] + _interruptCycle;
        _interruptCycle = 0;

        int dmaStall = _bus.ConsumeStallCycles();
        cycles += dmaStall;
        _bus.Tick(cycles);
        return cycles;
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

        ushort lo = _bus.ReadByte(0xFFFA);
        ushort hi = _bus.ReadByte(0xFFFB);
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
