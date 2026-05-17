// AprNes.Cli/Validation/NesSteppableCpu.cs
//
// Phase 30.16 sprint 5.6 — IBlockBoundedSteppableCpu adapter for NES
// Ricoh 2A03 backend. Mirrors GbSteppableCpu (see
// src/AprGb.Cli/Validation/GbSteppableCpu.cs) and X86SteppableCpu.
//
// The NES bus has Wram/Vram/Oam/PaletteRam + cartridge SRAM + PPU/APU
// registers. SnapshotMemoryState clones every byte-array region so
// LoadMemoryState produces bit-identical environments across both envs.

using AprCpu.Core.Validation;
using AprNes.Cli.Cpu;
using AprNes.Cli.Memory;

namespace AprNes.Cli.Validation;

/// <summary>
/// Snapshot of NES 2A03 CPU architectural state at one point in time.
/// Pc is the program counter; registers flattened into a Dict for the
/// generic comparator.
/// </summary>
public sealed class NesCpuStateSnapshot : ICpuStateSnapshot
{
    public ulong Pc { get; }
    public IReadOnlyDictionary<string, ulong> Registers { get; }
    public long CycleCount { get; }

    public NesCpuStateSnapshot(NesJsonCpu cpu, long cycles)
    {
        Pc = ((INesCpuBackend)cpu).PC;
        CycleCount = cycles;
        var backend = (INesCpuBackend)cpu;
        Registers = new Dictionary<string, ulong>
        {
            ["A"]  = backend.A,
            ["X"]  = backend.X,
            ["Y"]  = backend.Y,
            ["SP"] = backend.SP,
            ["P"]  = (byte)(backend.P | 0x20),     // bit-5 always reads as 1 per 6502 spec
            ["PC"] = backend.PC,
        };
    }
}

public sealed class NesSteppableCpu : IBlockBoundedSteppableCpu
{
    public string Name { get; }

    private readonly NesJsonCpu     _cpu;
    private readonly NesMemoryBus   _bus;
    private long                    _steps;

    public NesJsonCpu Cpu => _cpu;
    public NesMemoryBus Bus => _bus;

    public NesSteppableCpu(string name, NesJsonCpu cpu, NesMemoryBus bus)
    {
        Name = name;
        _cpu = cpu;
        _bus = bus;
    }

    public ICpuStateSnapshot Snapshot()
    {
        _cpu.SetActiveForLockstep();
        return new NesCpuStateSnapshot(_cpu, _steps);
    }

    public void Step()
    {
        OnBeforeStep?.Invoke();
        _cpu.SetActiveForLockstep();
        _cpu.Step();
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

    public object BeginTrace(IBlockTraceSink sink)
    {
        var prior = NesJsonCpu.ActiveTraceSink;
        NesJsonCpu.ActiveTraceSink = sink;
        return prior!;
    }

    public void EndTrace(object token)
    {
        NesJsonCpu.ActiveTraceSink = token as IBlockTraceSink;
    }

    public Func<object?>?   AdditionalSnapshot { get; set; }
    public Action<object?>? AdditionalRestore  { get; set; }
    public Action?          OnBeforeStep       { get; set; }

    public object SnapshotMemoryState()
    {
        // V1: clone every NES bus memory region + CPU state buffer.
        // Wram + Vram + Oam + PaletteRam are byte[] fields exposed via
        // public getters on NesMemoryBus.
        var busBlob = new NesBusStateBlob
        {
            Wram       = (byte[])_bus.Wram.Clone(),
            Vram       = (byte[])_bus.Vram.Clone(),
            Oam        = (byte[])_bus.Oam.Clone(),
            PaletteRam = (byte[])_bus.PaletteRam.Clone(),
            CpuBus     = _bus.InternalCpuBus,
            OamDmaWritePtr = _bus.InternalOamDmaWritePtr,
            PendingStallCycles   = _bus.InternalPendingStallCycles,
            PendingCatchUpCycles = _bus.InternalPendingCatchUpCycles,
        };
        var cpuBlob = _cpu.SnapshotState();
        var hwBlob  = AdditionalSnapshot?.Invoke();
        return (busBlob, cpuBlob, hwBlob);
    }

    public void LoadMemoryState(object snapshot)
    {
        var (busBlob, cpuBlob, hwBlob) =
            ((NesBusStateBlob, NesCpuStateBlob, object?))snapshot;
        Array.Copy(busBlob.Wram,       _bus.Wram,       busBlob.Wram.Length);
        Array.Copy(busBlob.Vram,       _bus.Vram,       busBlob.Vram.Length);
        Array.Copy(busBlob.Oam,        _bus.Oam,        busBlob.Oam.Length);
        Array.Copy(busBlob.PaletteRam, _bus.PaletteRam, busBlob.PaletteRam.Length);
        _bus.InternalCpuBus              = busBlob.CpuBus;
        _bus.InternalOamDmaWritePtr      = busBlob.OamDmaWritePtr;
        _bus.InternalPendingStallCycles  = busBlob.PendingStallCycles;
        _bus.InternalPendingCatchUpCycles = busBlob.PendingCatchUpCycles;
        _cpu.LoadState(cpuBlob);
        AdditionalRestore?.Invoke(hwBlob);
    }
}

/// <summary>Snapshot blob for NesMemoryBus regions (Phase 30.16 sprint 5.6,
/// extended in Phase 30.17b to include internal bus state).</summary>
public sealed class NesBusStateBlob
{
    public byte[] Wram       = System.Array.Empty<byte>();
    public byte[] Vram       = System.Array.Empty<byte>();
    public byte[] Oam        = System.Array.Empty<byte>();
    public byte[] PaletteRam = System.Array.Empty<byte>();
    public byte   CpuBus;
    public byte   OamDmaWritePtr;
    public int    PendingStallCycles;
    public int    PendingCatchUpCycles;
}
