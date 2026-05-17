// AprGb.Cli/Validation/GbSteppableCpu.cs
//
// Phase 30.16 sprint 5.5 — IBlockBoundedSteppableCpu adapter for GB
// LR35902 backend. Mirrors the X86 adapter pattern (see
// src/AprX86.Cli/Validation/X86LockstepAdapter.cs). Wraps a single
// JsonCpu + its GbMemoryBus so VerifiedBlockJitRunner can drive
// two of them in parallel (one JIT, one interp) and 3-axis compare
// per-block output.
//
// The GB bus has many small memory regions (Rom, Wram, Vram, Oam, Hram,
// Io, InterruptEnable, InterruptFlag) — SnapshotMemoryState clones
// all of them so LoadMemoryState produces bit-identical environments
// across both envs.

using AprCpu.Core.Validation;
using AprGb.Cli.Cpu;
using AprGb.Cli.Memory;

namespace AprGb.Cli.Validation;

/// <summary>
/// Snapshot of an LR35902 JsonCpu's architectural state at one point
/// in time. PC is the program counter; other registers flattened into
/// a Dict for the generic comparator.
/// </summary>
public sealed class GbCpuStateSnapshot : ICpuStateSnapshot
{
    public ulong Pc { get; }
    public IReadOnlyDictionary<string, ulong> Registers { get; }
    public long CycleCount { get; }

    public GbCpuStateSnapshot(JsonCpu cpu, long cycles)
    {
        Pc = cpu.ReadReg16(GbReg16.PC);
        CycleCount = cycles;
        Registers = new Dictionary<string, ulong>
        {
            ["A"] = cpu.ReadReg8(GbReg8.A),
            ["F"] = cpu.ReadReg8(GbReg8.F),
            ["B"] = cpu.ReadReg8(GbReg8.B),
            ["C"] = cpu.ReadReg8(GbReg8.C),
            ["D"] = cpu.ReadReg8(GbReg8.D),
            ["E"] = cpu.ReadReg8(GbReg8.E),
            ["H"] = cpu.ReadReg8(GbReg8.H),
            ["L"] = cpu.ReadReg8(GbReg8.L),
            ["SP"] = cpu.ReadReg16(GbReg16.SP),
            ["PC"] = cpu.ReadReg16(GbReg16.PC),
            ["HALT"] = cpu.IsHalted ? 1u : 0u,
        };
    }
}

/// <summary>
/// IBlockBoundedSteppableCpu adapter for GB LR35902 JsonCpu. Wraps one
/// CPU + its memory bus so the verifier can drive two of them in
/// parallel.
/// </summary>
public sealed class GbSteppableCpu : IBlockBoundedSteppableCpu
{
    public string Name { get; }

    private readonly JsonCpu       _cpu;
    private readonly GbMemoryBus   _bus;
    private long                   _steps;

    public JsonCpu Cpu => _cpu;
    public GbMemoryBus Bus => _bus;

    public GbSteppableCpu(string name, JsonCpu cpu, GbMemoryBus bus)
    {
        Name = name;
        _cpu = cpu;
        _bus = bus;
    }

    public ICpuStateSnapshot Snapshot()
    {
        _cpu.SetActiveForLockstep();
        return new GbCpuStateSnapshot(_cpu, _steps);
    }

    public void Step()
    {
        OnBeforeStep?.Invoke();
        _cpu.SetActiveForLockstep();
        // RunCycles(1) runs at least 1 cycle worth = 1 block in block-JIT
        // mode, 1 instruction in per-instr mode. The "actual N" comes from
        // LastBlockInstructionCount, which JsonCpu's StepBlock now sets
        // from the LastInstrIndex slot (sprint 5.5 prep).
        _cpu.RunCycles(1);
        _steps++;
    }

    public byte ReadByteFromBus(ulong addr) => _bus.ReadByte((ushort)(addr & 0xFFFF));

    public int LastBlockInstructionCount => _cpu.LastBlockInstructionCount;

    public void StepOneArchitecturalInstruction()
    {
        OnBeforeStep?.Invoke();
        _cpu.SetActiveForLockstep();
        _cpu.StepOnePerInstr();
        _steps++;
    }

    /// <summary>
    /// Phase 30.18y — block-boundary IRQ poll. JIT-side already does
    /// this implicitly via RunCycles → CheckInterrupts; for INTERP-side
    /// the verifier calls this explicitly after the N×StepOnePerInstr
    /// loop so both backends see IRQ delivery at the same cadence.
    ///
    /// Phase 30.18ab — also mirror JIT.RunCycles' HALT-spin behavior:
    /// when INTERP ended in HALT with no pending IRQ but JIT might
    /// have ticked the bus (causing timer/PPU overflow to set IF),
    /// also tick INTERP's bus by 4 cycles and re-check pending. Mirrors
    /// JIT's one HALT-spin iteration for `RunCycles(1)`. This is the
    /// cpu_instrs.gb block #280289+ case.
    /// </summary>
    public void PollPendingIrqsAtBlockBoundary()
    {
        _cpu.SetActiveForLockstep();
        // If HALTed with no IRQ pending, mirror JIT's HALT-spin tick.
        if (_cpu.IsHalted)
        {
            var pendingPre = (byte)(_bus.InterruptEnable & _bus.InterruptFlag & 0x1F);
            if (pendingPre == 0)
            {
                _bus.Tick(4);
            }
        }
        _cpu.CheckInterruptsAtBlockBoundary();
    }

    public object BeginTrace(IBlockTraceSink sink)
    {
        var prior = JsonCpu.ActiveTraceSink;
        JsonCpu.ActiveTraceSink = sink;
        return prior!;
    }

    public void EndTrace(object token)
    {
        JsonCpu.ActiveTraceSink = token as IBlockTraceSink;
    }

    public Func<object?>?    AdditionalSnapshot { get; set; }
    public Action<object?>?  AdditionalRestore  { get; set; }
    public Action?           OnBeforeStep       { get; set; }

    public object SnapshotMemoryState()
    {
        // V1: clone every GB bus memory region + CPU state buffer.
        // Bus memory is the big chunks (Wram/Vram/Oam/Hram/Io/ExtRam);
        // plus the small fields (InterruptEnable, InterruptFlag,
        // BiosEnabled, scheduler state). CPU state buffer covers
        // GPRs/F/SP/PC/cycle counter via JsonCpu.SnapshotState.
        var busBlob = new GbBusStateBlob
        {
            Wram = (byte[])_bus.Wram.Clone(),
            Vram = (byte[])_bus.Vram.Clone(),
            Oam  = (byte[])_bus.Oam.Clone(),
            Hram = (byte[])_bus.Hram.Clone(),
            Io   = (byte[])_bus.Io.Clone(),
            ExtRam = (byte[])_bus.ExtRam.Clone(),
            InterruptEnable = _bus.InterruptEnable,
            InterruptFlag   = _bus.InterruptFlag,
            // Phase 30.18aa — also snapshot timer accumulators + MBC state.
            // Without this JIT and INTERP have independent timer state →
            // timer-overflow timing differs → divergent IRQ delivery.
            DivAccum     = _bus.DivAccumSnapshot,
            TimaAccum    = _bus.TimaAccumSnapshot,
            RomBank      = _bus.RomBankSnapshot,
            RamBank      = _bus.RamBankSnapshot,
            RamEnable    = _bus.RamEnableSnapshot,
            ModeRamBank  = _bus.ModeRamBankSnapshot,
        };
        var cpuBlob = _cpu.SnapshotState();
        var hwBlob  = AdditionalSnapshot?.Invoke();
        return (busBlob, cpuBlob, hwBlob);
    }

    public void LoadMemoryState(object snapshot)
    {
        var (busBlob, cpuBlob, hwBlob) =
            ((GbBusStateBlob, GbCpuStateBlob, object?))snapshot;
        Array.Copy(busBlob.Wram, _bus.Wram, busBlob.Wram.Length);
        Array.Copy(busBlob.Vram, _bus.Vram, busBlob.Vram.Length);
        Array.Copy(busBlob.Oam,  _bus.Oam,  busBlob.Oam.Length);
        Array.Copy(busBlob.Hram, _bus.Hram, busBlob.Hram.Length);
        Array.Copy(busBlob.Io,   _bus.Io,   busBlob.Io.Length);
        Array.Copy(busBlob.ExtRam, _bus.ExtRam, busBlob.ExtRam.Length);
        _bus.InterruptEnable = busBlob.InterruptEnable;
        _bus.InterruptFlag   = busBlob.InterruptFlag;
        // Phase 30.18aa — restore timer + MBC state for cadence parity.
        _bus.DivAccumSnapshot     = busBlob.DivAccum;
        _bus.TimaAccumSnapshot    = busBlob.TimaAccum;
        _bus.RomBankSnapshot      = busBlob.RomBank;
        _bus.RamBankSnapshot      = busBlob.RamBank;
        _bus.RamEnableSnapshot    = busBlob.RamEnable;
        _bus.ModeRamBankSnapshot  = busBlob.ModeRamBank;
        _cpu.LoadState(cpuBlob);
        AdditionalRestore?.Invoke(hwBlob);
    }
}

/// <summary>Snapshot blob for the GB memory bus (Phase 30.16 sprint 5.5,
/// extended in Phase 30.18aa with timer + MBC state).</summary>
public sealed class GbBusStateBlob
{
    public byte[] Wram = System.Array.Empty<byte>();
    public byte[] Vram = System.Array.Empty<byte>();
    public byte[] Oam  = System.Array.Empty<byte>();
    public byte[] Hram = System.Array.Empty<byte>();
    public byte[] Io   = System.Array.Empty<byte>();
    public byte[] ExtRam = System.Array.Empty<byte>();
    public byte InterruptEnable;
    public byte InterruptFlag;
    // Phase 30.18aa — timer + MBC state needed for cadence parity
    // between JIT and INTERP backends.
    public int  DivAccum;
    public int  TimaAccum;
    public int  RomBank;
    public int  RamBank;
    public bool RamEnable;
    public bool ModeRamBank;
}
