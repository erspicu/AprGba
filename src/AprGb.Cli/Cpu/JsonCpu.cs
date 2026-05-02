using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AprCpu.Core.Compilation;
using AprCpu.Core.Decoder;
using AprCpu.Core.IR;
using AprCpu.Core.JsonSpec;
using AprCpu.Core.Runtime;
using AprGb.Cli.Memory;

namespace AprGb.Cli.Cpu;

/// <summary>
/// JSON-driven LR35902 backend. Loads <c>spec/lr35902/cpu.json</c>,
/// compiles it through <see cref="SpecCompiler"/> to LLVM IR, JITs the
/// module via <see cref="HostRuntime"/>, and dispatches each instruction
/// by reading the opcode byte from <see cref="GbMemoryBus"/> and calling
/// the matching emitted function.
///
/// Memory-bus externs (memory_read_8 / memory_write_8 / memory_write_16)
/// are bound via the inttoptr-in-initializer pattern that ARM uses for
/// host_swap_register_bank — see <see cref="HostRuntime.BindExtern"/>.
///
/// <para><b>Limitations (Phase 4.5C scope):</b></para>
/// <list type="bullet">
///   <item>Single live instance — the extern shims read from a static
///         bus field. A second JsonCpu would need a per-thread or
///         per-engine indirection. Acceptable for single-ROM smoke tests.</item>
///   <item>Cycle accounting parses the spec's <c>cycles.form</c> as the
///         first integer it sees ("3m_or_4m" → 3). Conditional cycles
///         and ALU side-effect cycles aren't tracked yet.</item>
///   <item>Interrupts not implemented — IME / EI / DI / vectoring all
///         lower to no-ops in the emitter pass; HALT also no-ops.</item>
///   <item>0xCB prefix opcode dispatches into the CB set inline; no
///         multi-set CpuExecutor needed.</item>
/// </list>
/// </summary>
public sealed unsafe class JsonCpu : ICpuBackend
{
    public string Name => "json-llvm";

    // Static bus reference used by the unmanaged extern shims. Last-call-wins
    // when multiple JsonCpu instances exist; today's call sites only ever
    // construct one at a time.
    private static GbMemoryBus? _activeBus;

    private readonly LoadedSpec     _spec;
    private readonly HostRuntime    _rt;
    private readonly DecoderTable   _mainDecoder;
    private readonly DecoderTable   _cbDecoder;
    // Phase 7 F.x: identity-keyed cache (InstructionDef reference →
    // fn pointer). The previous string-keyed cache forced
    // BuildFunctionKey to allocate a Dictionary AND format a string
    // PER INSTRUCTION — both gone on the hot path now.
    private readonly Dictionary<InstructionDef, IntPtr> _fnPtrByDef
        = new(System.Collections.Generic.ReferenceEqualityComparer.Instance);

    private byte[]    _state = Array.Empty<byte>();
    // Phase 7 B.f: permanent pin of the state buffer. Reset() re-allocates
    // _state across runs so we re-pin and free the previous handle. Pre-pin
    // every StepOne re-pinned via `fixed` (~50 ns × millions of instr).
    private System.Runtime.InteropServices.GCHandle _stateHandle;
    private byte* _statePtr;
    private GbMemoryBus _bus = null!;

    // Pre-cached field offsets for fast register access.
    private readonly int _aOff, _bOff, _cOff, _dOff, _eOff, _hOff, _lOff;
    private readonly int _fOff, _spOff, _pcOff;

    private bool _halted;
    private long _totalInstructions;
    private static bool _ime;             // shared with extern shims
    private static int  _eiDelay;         // counts down to IME=1 after EI
    private static bool _haltSignal;      // set by host_lr35902_halt extern

    public long InstructionsExecuted => _totalInstructions;

    public JsonCpu()
    {
        var specPath = LocateSpec();
        var compileResult = SpecCompiler.Compile(specPath);
        if (compileResult.Diagnostics.Count != 0)
        {
            throw new InvalidOperationException(
                "JsonCpu: spec compilation produced diagnostics:\n  " +
                string.Join("\n  ", compileResult.Diagnostics));
        }

        _spec = SpecLoader.LoadCpuSpec(specPath);
        if (!compileResult.DecoderTables.TryGetValue("Main", out var mainDecoder) ||
            !compileResult.DecoderTables.TryGetValue("CB",   out var cbDecoder))
        {
            throw new InvalidOperationException("JsonCpu: spec must declare Main and CB instruction sets.");
        }
        _mainDecoder = mainDecoder;
        _cbDecoder   = cbDecoder;

        _rt = HostRuntime.Build(compileResult.Module,
            new CpuStateLayout(
                compileResult.Module.Context,
                _spec.Cpu.RegisterFile,
                _spec.Cpu.ProcessorModes,
                _spec.Cpu.ExceptionVectors));

        // Bind the three memory-bus externs. Trampolines are static C#
        // methods marked [UnmanagedCallersOnly] so they can be called
        // directly from JIT-compiled code.
        _rt.BindExtern(MemoryEmitters.ExternFunctionNames.Read8,
            (IntPtr)(delegate* unmanaged[Cdecl]<uint, byte>)&MemRead8);
        _rt.BindExtern(MemoryEmitters.ExternFunctionNames.Write8,
            (IntPtr)(delegate* unmanaged[Cdecl]<uint, byte, void>)&MemWrite8);
        _rt.BindExtern(MemoryEmitters.ExternFunctionNames.Write16,
            (IntPtr)(delegate* unmanaged[Cdecl]<uint, ushort, void>)&MemWrite16);

        // HALT / IME externs (declared by Lr35902HostHelpers; bound only
        // when the spec actually contains those ops). BindExtern throws
        // if the global isn't declared, so guard each one.
        TryBindNoArg(Lr35902HostHelpers.HaltExtern,
            (IntPtr)(delegate* unmanaged[Cdecl]<void>)&HostHalt);
        TryBindNoArg(Lr35902HostHelpers.ArmImeDelayedExtern,
            (IntPtr)(delegate* unmanaged[Cdecl]<void>)&HostArmImeDelayed);
        TryBindI8(Lr35902HostHelpers.SetImeExtern,
            (IntPtr)(delegate* unmanaged[Cdecl]<byte, void>)&HostSetIme);

        _rt.Compile();

        // Cache field offsets — order matches cpu.json's GPR list
        // (A, B, C, D, E, H, L) and the status section (F, SP, PC).
        _aOff = (int)_rt.GprOffset(0);
        _bOff = (int)_rt.GprOffset(1);
        _cOff = (int)_rt.GprOffset(2);
        _dOff = (int)_rt.GprOffset(3);
        _eOff = (int)_rt.GprOffset(4);
        _hOff = (int)_rt.GprOffset(5);
        _lOff = (int)_rt.GprOffset(6);
        _fOff = (int)_rt.StatusOffset("F");
        _spOff = (int)_rt.StatusOffset("SP");
        _pcOff = (int)_rt.StatusOffset("PC");
    }

    private static string LocateSpec()
    {
        // Walk up from CWD looking for spec/lr35902/cpu.json. The Cli is
        // typically run from repo root or its bin/ directory.
        var dir = AppContext.BaseDirectory;
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
        {
            var probe = Path.Combine(d.FullName, "spec", "lr35902", "cpu.json");
            if (File.Exists(probe)) return probe;
        }
        // Fallback: relative to CWD.
        var cwdProbe = Path.Combine(Environment.CurrentDirectory, "spec", "lr35902", "cpu.json");
        if (File.Exists(cwdProbe)) return cwdProbe;
        throw new FileNotFoundException(
            "JsonCpu: cannot locate spec/lr35902/cpu.json. Run from repo root.");
    }

    public bool IsHalted => _halted;

    public void Reset(GbMemoryBus bus)
    {
        _bus = bus;
        _activeBus = bus;
        // Free previous pin (Reset can be called multiple times).
        if (_stateHandle.IsAllocated) _stateHandle.Free();
        _state = new byte[(int)_rt.StateSizeBytes];
        _stateHandle = System.Runtime.InteropServices.GCHandle.Alloc(
            _state, System.Runtime.InteropServices.GCHandleType.Pinned);
        _statePtr = (byte*)_stateHandle.AddrOfPinnedObject();
        _halted = false;
        _totalInstructions = 0;
        _ime = false;
        _eiDelay = 0;
        _haltSignal = false;

        if (bus.BiosEnabled)
        {
            // Cold start with boot ROM — all registers zero, PC at 0.
            WriteI8(_aOff, 0); WriteI8(_bOff, 0); WriteI8(_cOff, 0);
            WriteI8(_dOff, 0); WriteI8(_eOff, 0);
            WriteI8(_hOff, 0); WriteI8(_lOff, 0);
            WriteI8(_fOff, 0);
            WriteI16(_spOff, 0);
            WriteI16(_pcOff, 0);
        }
        else
        {
            // Post-BIOS DMG state per Pan Docs.
            WriteI8(_aOff, 0x01); WriteI8(_bOff, 0x00); WriteI8(_cOff, 0x13);
            WriteI8(_dOff, 0x00); WriteI8(_eOff, 0xD8);
            WriteI8(_hOff, 0x01); WriteI8(_lOff, 0x4D);
            WriteI8(_fOff, 0xB0);                // Z=1 N=0 H=1 C=1
            WriteI16(_spOff, 0xFFFE);
            WriteI16(_pcOff, 0x0100);
        }
    }

    private void TryBindNoArg(string name, IntPtr fn)
    {
        try { _rt.BindExtern(name, fn); }
        catch (InvalidOperationException) { /* spec didn't declare it; skip */ }
    }

    private void TryBindI8(string name, IntPtr fn)
    {
        try { _rt.BindExtern(name, fn); }
        catch (InvalidOperationException) { }
    }

    public ushort ReadReg16(GbReg16 reg) => reg switch
    {
        GbReg16.AF => (ushort)((ReadI8(_aOff) << 8) | ReadI8(_fOff)),
        GbReg16.BC => (ushort)((ReadI8(_bOff) << 8) | ReadI8(_cOff)),
        GbReg16.DE => (ushort)((ReadI8(_dOff) << 8) | ReadI8(_eOff)),
        GbReg16.HL => (ushort)((ReadI8(_hOff) << 8) | ReadI8(_lOff)),
        GbReg16.SP => ReadI16(_spOff),
        GbReg16.PC => ReadI16(_pcOff),
        _          => 0
    };

    public byte ReadReg8(GbReg8 reg) => reg switch
    {
        GbReg8.A => ReadI8(_aOff), GbReg8.F => ReadI8(_fOff),
        GbReg8.B => ReadI8(_bOff), GbReg8.C => ReadI8(_cOff),
        GbReg8.D => ReadI8(_dOff), GbReg8.E => ReadI8(_eOff),
        GbReg8.H => ReadI8(_hOff), GbReg8.L => ReadI8(_lOff),
        _        => 0
    };

    public long RunCycles(long targetCycles)
    {
        long consumed = 0;
        _activeBus = _bus;     // ensure shims see the right bus
        while (consumed < targetCycles)
        {
            // Wake from HALT if any enabled IRQ is pending (whether or
            // not IME is set — HALT exits on IE & IF != 0).
            if (_halted)
            {
                var pending = (byte)(_bus.InterruptEnable & _bus.InterruptFlag & 0x1F);
                if (pending != 0) _halted = false;
                else { _bus.Tick(4); consumed += 4; CheckInterrupts(out var srvHalt); consumed += srvHalt; continue; }
            }

            var stepCycles = StepOne();
            _totalInstructions++;
            _bus.Tick((int)stepCycles);
            consumed += stepCycles;

            // Pick up "halt signalled by emitter this step".
            if (_haltSignal) { _halted = true; _haltSignal = false; }

            // Tick the EI delay and apply IME scheduling.
            if (_eiDelay > 0 && --_eiDelay == 0) _ime = true;

            CheckInterrupts(out var serviceCycles);
            consumed += serviceCycles;
        }
        return consumed;
    }

    /// <summary>
    /// Mirrors LegacyCpu.CheckInterrupts: priority-ordered IRQ vectoring
    /// with auto-clear of the IF bit and IME=0 on entry. Wakes from HALT
    /// regardless of IME. Reports the cycles charged for the IRQ entry.
    /// </summary>
    private void CheckInterrupts(out long serviceCycles)
    {
        serviceCycles = 0;
        var pending = (byte)(_bus.InterruptEnable & _bus.InterruptFlag & 0x1F);
        if (pending == 0) return;
        if (_halted) _halted = false;
        if (!_ime) return;

        ushort vector;
        byte mask;
        if      ((pending & 0x01) != 0) { vector = 0x40; mask = 0x01; }   // VBlank
        else if ((pending & 0x02) != 0) { vector = 0x48; mask = 0x02; }   // STAT
        else if ((pending & 0x04) != 0) { vector = 0x50; mask = 0x04; }   // Timer
        else if ((pending & 0x08) != 0) { vector = 0x58; mask = 0x08; }   // Serial
        else                            { vector = 0x60; mask = 0x10; }   // Joypad

        _ime = false;
        _bus.InterruptFlag &= (byte)~mask;

        // Push current PC: SP -= 2; mem[SP] = pc_lo; mem[SP+1] = pc_hi.
        var pc = ReadI16(_pcOff);
        var sp = (ushort)(ReadI16(_spOff) - 2);
        WriteI16(_spOff, sp);
        _bus.WriteByte(sp,                (byte)(pc & 0xFF));
        _bus.WriteByte((ushort)(sp + 1),  (byte)(pc >> 8));
        WriteI16(_pcOff, vector);

        _bus.Tick(20);
        serviceCycles = 20;
    }

    /// <summary>
    /// Mirrors LegacyCpu.CheckInterrupts: priority-ordered IRQ vectoring
    /// with auto-clear of the IF bit and IME=0 on entry. Wakes from HALT
    /// regardless of IME.
    /// </summary>
    /// <summary>Execute exactly one instruction. Returns the t-cycles charged.</summary>
    private long StepOne()
    {
        ushort pc = ReadI16(_pcOff);
        byte opcode = _bus.ReadByte(pc);
        // Advance PC past the opcode byte. Multi-byte instructions
        // fetch their trailing operands via the read_imm8/imm16 emitters,
        // which themselves bump PC.
        WriteI16(_pcOff, (ushort)(pc + 1));

        DecoderTable decoder;
        DecodedInstruction? decoded;
        uint instructionWord;

        if (opcode == 0xCB)
        {
            // Read the CB byte and decode in the CB set.
            byte cbOpcode = _bus.ReadByte(ReadI16(_pcOff));
            WriteI16(_pcOff, (ushort)(ReadI16(_pcOff) + 1));
            decoder = _cbDecoder;
            decoded = decoder.Decode(cbOpcode);
            instructionWord = cbOpcode;
        }
        else
        {
            decoder = _mainDecoder;
            decoded = decoder.Decode(opcode);
            instructionWord = opcode;
        }

        if (decoded is null)
        {
            // Undefined opcode — match LegacyCpu behavior: skip silently.
            return 4;
        }

        var fnPtr = ResolveFunctionPointer(decoder.Name, decoded);
        var fn = (delegate* unmanaged[Cdecl]<byte*, uint, void>)fnPtr;

        // For conditional control-flow ops, snapshot the "would-be
        // fall-through PC" so we can detect taken-branch and charge the
        // extra cycles LegacyCpu adds in its switch (see Step.cs).
        ushort fallThroughPc = ComputeFallThroughPc(opcode, pc);

        // Phase 7 B.f: cached pinned pointer instead of per-step `fixed`.
        fn(_statePtr, instructionWord);

        long cycles = CyclesFor(decoded.Instruction);
        cycles += ConditionalBranchExtraCycles(opcode, fallThroughPc, ReadI16(_pcOff));
        return cycles;
    }

    /// <summary>
    /// For conditional control-flow opcodes, return the PC the CPU would
    /// have if the branch were NOT taken (i.e. fall-through). Returns
    /// 0xFFFF for non-conditional opcodes (a sentinel never produced by
    /// fall-through which lives in 0x0000-0xFFFE).
    /// </summary>
    private static ushort ComputeFallThroughPc(byte opcode, ushort prePc) => opcode switch
    {
        0x20 or 0x28 or 0x30 or 0x38 => (ushort)(prePc + 2),                  // JR cc, e8
        0xC0 or 0xC8 or 0xD0 or 0xD8 => (ushort)(prePc + 1),                  // RET cc
        0xC2 or 0xCA or 0xD2 or 0xDA => (ushort)(prePc + 3),                  // JP cc, nn
        0xC4 or 0xCC or 0xD4 or 0xDC => (ushort)(prePc + 3),                  // CALL cc, nn
        _ => (ushort)0xFFFF
    };

    /// <summary>
    /// Mirrors LegacyCpu's <c>_cycles += N</c> in conditional branch handlers:
    /// add the taken-branch overhead when PC after execution does NOT match
    /// the fall-through value. Returns 0 for non-conditional opcodes.
    /// </summary>
    private static long ConditionalBranchExtraCycles(byte opcode, ushort fallThroughPc, ushort actualPc)
    {
        if (fallThroughPc == 0xFFFF || actualPc == fallThroughPc) return 0;
        return opcode switch
        {
            0x20 or 0x28 or 0x30 or 0x38 => 4,    // JR cc taken: +1 m-cycle
            0xC0 or 0xC8 or 0xD0 or 0xD8 => 12,   // RET cc taken: +3 m-cycles
            0xC2 or 0xCA or 0xD2 or 0xDA => 4,    // JP cc taken: +1 m-cycle
            0xC4 or 0xCC or 0xD4 or 0xDC => 12,   // CALL cc taken: +3 m-cycles
            _ => 0
        };
    }

    private IntPtr ResolveFunctionPointer(string setName, DecodedInstruction decoded)
    {
        // Hot path: identity-keyed cache hit, zero allocation.
        if (_fnPtrByDef.TryGetValue(decoded.Instruction, out var cached)) return cached;

        // Cold path: rebuild the function key (matches SpecCompiler.Compile's
        // key construction) once, look up the IR function, cache by
        // InstructionDef reference. This was the per-instruction hot path
        // before Phase 7 F.x — Dictionary allocation + string format every
        // single instruction. Now amortised to once per opcode×selector.
        var p = ResolveFunctionPointerSlow(setName, decoded);
        _fnPtrByDef[decoded.Instruction] = p;
        return p;
    }

    private IntPtr ResolveFunctionPointerSlow(string setName, DecodedInstruction decoded)
    {
        var fmt = decoded.Format;
        var def = decoded.Instruction;

        // Local ambiguity check (avoids allocating a Dictionary by counting
        // up to 2 hits and stopping; cold path so it doesn't matter much).
        var ambiguous = false;
        for (int i = 0, hits = 0; i < fmt.Instructions.Count; i++)
            if (fmt.Instructions[i].Mnemonic == def.Mnemonic && ++hits > 1)
            { ambiguous = true; break; }

        var suffix = ambiguous && def.Selector is not null
            ? $".{def.Mnemonic}_{def.Selector.Value}"
            : $".{def.Mnemonic}";
        var key = $"{setName}.{fmt.Name}{suffix}";
        var name = "Execute_" + key.Replace('.', '_');
        return _rt.GetFunctionPointer(name);
    }

    private static long CyclesFor(InstructionDef def)
    {
        // cycles.form is something like "1m" / "2m" / "3m_or_4m" — pull the
        // first integer and multiply by 4 (m-cycle = 4 t-cycles).
        var form = def.Cycles?.Form;
        if (string.IsNullOrEmpty(form)) return 4;
        int n = 0;
        foreach (var ch in form)
        {
            if (ch >= '0' && ch <= '9') { n = n * 10 + (ch - '0'); continue; }
            if (n > 0) break;
        }
        if (n == 0) n = 1;
        return n * 4;
    }

    // ---------------- state buffer accessors ----------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte ReadI8(int off) => _state[off];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteI8(int off, byte v) => _state[off] = v;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ushort ReadI16(int off)
        => BinaryPrimitives.ReadUInt16LittleEndian(_state.AsSpan(off, 2));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteI16(int off, ushort v)
        => BinaryPrimitives.WriteUInt16LittleEndian(_state.AsSpan(off, 2), v);

    // ---------------- extern shims (called from JIT'd IR) ----------------

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static byte MemRead8(uint addr)
        => _activeBus is not null ? _activeBus.ReadByte((ushort)addr) : (byte)0xFF;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void MemWrite8(uint addr, byte value)
    {
        if (_activeBus is not null) _activeBus.WriteByte((ushort)addr, value);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void MemWrite16(uint addr, ushort value)
    {
        if (_activeBus is null) return;
        _activeBus.WriteByte((ushort)addr,        (byte)(value & 0xFF));
        _activeBus.WriteByte((ushort)(addr + 1),  (byte)(value >> 8));
    }

    // ---------------- HALT / IME shims (called from JIT'd IR) ----------------

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void HostHalt() => _haltSignal = true;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void HostSetIme(byte v) => _ime = v != 0;

    /// <summary>
    /// EI sets _eiDelay = 2 so RunCycles' --_eiDelay applies one
    /// instruction later (matching LegacyCpu's EI semantics — IME
    /// becomes effective AFTER the instruction following EI completes).
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void HostArmImeDelayed() => _eiDelay = 2;
}
