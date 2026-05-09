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
    public string BackendName => "json-llvm";

    // Static memory reference used by the unmanaged extern shims. Last-call-
    // wins when multiple X86JsonCpu instances exist; the harness only ever
    // constructs one at a time.
    private static X86Memory? _activeMem;

    private readonly X86Memory                  _mem;
    private readonly LoadedSpec                  _spec;
    private readonly HostRuntime                 _rt;
    private readonly DecoderTable                _mainDecoder;
    private readonly SpecCompiler.CompileResult  _compileResult;

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
    private readonly int _flagsOff, _ipOff, _esOff, _csOff, _ssOff, _dsOff, _haltedOff;

    public X86Memory Memory => _mem;

    public X86JsonCpu(X86Memory memory)
    {
        _mem = memory ?? throw new ArgumentNullException(nameof(memory));

        var specPath = LocateSpec();
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

        _flagsOff  = (int)_rt.StatusOffset("FLAGS");
        _ipOff     = (int)_rt.StatusOffset("IP");
        _esOff     = (int)_rt.StatusOffset("ES");
        _csOff     = (int)_rt.StatusOffset("CS");
        _ssOff     = (int)_rt.StatusOffset("SS");
        _dsOff     = (int)_rt.StatusOffset("DS");
        _haltedOff = (int)_rt.StatusOffset("HALTED");

        _state = new byte[(int)_rt.StateSizeBytes];
        _stateHandle = GCHandle.Alloc(_state, GCHandleType.Pinned);
        _statePtr    = (byte*)_stateHandle.AddrOfPinnedObject();
    }

    private static string LocateSpec()
    {
        var dir = AppContext.BaseDirectory;
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
        {
            var probe = Path.Combine(d.FullName, "spec", "x86-16", "i8086", "cpu.json");
            if (File.Exists(probe)) return probe;
        }
        var cwdProbe = Path.Combine(Environment.CurrentDirectory, "spec", "x86-16", "i8086", "cpu.json");
        if (File.Exists(cwdProbe)) return cwdProbe;
        throw new FileNotFoundException(
            "X86JsonCpu: cannot locate spec/x86-16/i8086/cpu.json. Run from repo root.");
    }

    // --- IX86CpuBackend surface -------------------------------------------

    public void Reset()
    {
        // 8086 RESET: CS=0xFFFF, IP=0, all GPRs/segs/flags=0.
        Array.Clear(_state, 0, _state.Length);
        WriteU16(_csOff, 0xFFFF);
        // FLAGS, IP, others already cleared.
        _activeMem = _mem;
    }

    public void SetEntryPoint(ushort segment, ushort offset)
    {
        WriteU16(_csOff, segment);
        WriteU16(_ipOff, offset);
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

        // Fetch opcode at CS:IP.
        ushort cs = ReadU16(_csOff);
        ushort ip = ReadU16(_ipOff);
        int linear = ((cs << 4) + ip) & 0xFFFFF;
        byte opcode = _mem.ReadByte(linear);

        // Advance IP past the opcode byte. Multi-byte fetches (ModR/M /
        // displacement / immediate) advance IP further from inside the
        // emitted IR via x86_fetch_imm8/16.
        WriteU16(_ipOff, (ushort)(ip + 1));

        var decoded = _mainDecoder.Decode(opcode);
        if (decoded is null)
        {
            // 24.6.4 — unsupported opcode: revert IP and signal "not handled".
            // Returning -1 lets the test harness distinguish "json backend
            // doesn't cover this byte yet" from "it ran successfully".
            WriteU16(_ipOff, ip);
            return -1;
        }

        var fnPtr = ResolveFunctionPointer(_mainDecoder.Name, decoded);
        var fn = (delegate* unmanaged[Cdecl]<byte*, uint, void>)fnPtr;
        fn(_statePtr, opcode);

        // Cycle accounting deferred — 8088 cycle accuracy is not the
        // 24.6 goal. Return 1 for now so the caller has a non-zero
        // step count to drive its outer loop.
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
