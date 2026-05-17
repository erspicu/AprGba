// AprGba.Cli/Validation/GbaSteppableCpu.cs
//
// Phase 30.16 sprint 5.6b — IBlockBoundedSteppableCpu adapter for the
// ARM7TDMI/GBA backend driven by CpuExecutor. Mirrors X86 / GB / NES
// adapters. Wraps one CpuExecutor + its GbaMemoryBus so
// VerifiedBlockJitRunner can drive two of them in parallel.
//
// SnapshotMemoryState clones every GBA memory region (Iwram, Ewram,
// Vram, Palette, Oam, Io) + CPU state buffer. ROM is read-only so
// shared between envs (no clone needed for snapshot purposes).

using AprCpu.Core.Runtime;
using AprCpu.Core.Runtime.Gba;
using AprCpu.Core.Validation;

namespace AprGba.Cli.Validation;

public sealed class GbaCpuStateSnapshot : ICpuStateSnapshot
{
    public ulong Pc { get; }
    public IReadOnlyDictionary<string, ulong> Registers { get; }
    public long CycleCount { get; }

    public GbaCpuStateSnapshot(CpuExecutor cpu, long cycles)
    {
        Pc = cpu.Pc;
        CycleCount = cycles;
        var regs = new Dictionary<string, ulong>();
        for (int r = 0; r < 16; r++)
            regs[$"R{r}"] = cpu.ReadGpr(r);
        regs["CPSR"] = cpu.ReadStatus("CPSR");
        Registers = regs;
    }
}

public sealed class GbaSteppableCpu : IBlockBoundedSteppableCpu
{
    public string Name { get; }

    private readonly CpuExecutor   _cpu;
    private readonly GbaMemoryBus  _bus;
    private long                   _steps;

    public CpuExecutor Cpu => _cpu;
    public GbaMemoryBus Bus => _bus;

    public GbaSteppableCpu(string name, CpuExecutor cpu, GbaMemoryBus bus)
    {
        Name = name;
        _cpu = cpu;
        _bus = bus;
    }

    public ICpuStateSnapshot Snapshot()
    {
        return new GbaCpuStateSnapshot(_cpu, _steps);
    }

    public void Step()
    {
        OnBeforeStep?.Invoke();
        _cpu.Step();
        _steps++;
    }

    public byte ReadByteFromBus(ulong addr) => _bus.ReadByte((uint)(addr & 0xFFFFFFFF));

    public int LastBlockInstructionCount => _cpu.LastStepInstructionCount;

    public void StepOneArchitecturalInstruction()
    {
        OnBeforeStep?.Invoke();
        _cpu.StepOnePerInstr();
        _steps++;
    }

    public object BeginTrace(IBlockTraceSink sink)
    {
        var prior = MemoryBusBindings.ActiveTraceSink;
        MemoryBusBindings.ActiveTraceSink = sink;
        return prior!;
    }

    public void EndTrace(object token)
    {
        MemoryBusBindings.ActiveTraceSink = token as IBlockTraceSink;
    }

    public Func<object?>?   AdditionalSnapshot { get; set; }
    public Action<object?>? AdditionalRestore  { get; set; }
    public Action?          OnBeforeStep       { get; set; }

    public object SnapshotMemoryState()
    {
        // V1: clone every writable GBA bus memory region + CPU state buffer.
        // ROM is read-only — share by reference between envs.
        var busBlob = new GbaBusStateBlob
        {
            Iwram   = (byte[])_bus.Iwram.Clone(),
            Ewram   = (byte[])_bus.Ewram.Clone(),
            Vram    = (byte[])_bus.Vram.Clone(),
            Palette = (byte[])_bus.Palette.Clone(),
            Oam     = (byte[])_bus.Oam.Clone(),
            Io      = (byte[])_bus.Io.Clone(),
        };
        var cpuState = _cpu.SnapshotState();
        var hwBlob   = AdditionalSnapshot?.Invoke();
        return (busBlob, cpuState, hwBlob);
    }

    public void LoadMemoryState(object snapshot)
    {
        var (busBlob, cpuState, hwBlob) =
            ((GbaBusStateBlob, byte[], object?))snapshot;
        Array.Copy(busBlob.Iwram,   _bus.Iwram,   busBlob.Iwram.Length);
        Array.Copy(busBlob.Ewram,   _bus.Ewram,   busBlob.Ewram.Length);
        Array.Copy(busBlob.Vram,    _bus.Vram,    busBlob.Vram.Length);
        Array.Copy(busBlob.Palette, _bus.Palette, busBlob.Palette.Length);
        Array.Copy(busBlob.Oam,     _bus.Oam,     busBlob.Oam.Length);
        Array.Copy(busBlob.Io,      _bus.Io,      busBlob.Io.Length);
        _cpu.LoadState(cpuState);
        AdditionalRestore?.Invoke(hwBlob);
    }
}

/// <summary>Snapshot blob for GbaMemoryBus writable regions (Phase 30.16 sprint 5.6b).</summary>
public sealed class GbaBusStateBlob
{
    public byte[] Iwram   = System.Array.Empty<byte>();
    public byte[] Ewram   = System.Array.Empty<byte>();
    public byte[] Vram    = System.Array.Empty<byte>();
    public byte[] Palette = System.Array.Empty<byte>();
    public byte[] Oam     = System.Array.Empty<byte>();
    public byte[] Io      = System.Array.Empty<byte>();
}
