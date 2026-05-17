// AprX86.Cli/Validation/X86LockstepAdapter.cs
//
// Phase 30.15b — wire X86JsonCpu into AprCpu.Core's generic LockstepDiff
// harness so we can run two x86 CPUs side-by-side and find the first
// architectural-state divergence between per-instr and block-JIT backends.
//
// Design:
//   - X86CpuStateSnapshot captures all 8086 GPRs + segments + IP + FLAGS
//     into the ICpuStateSnapshot shape (PC + Registers dict).
//   - X86SteppableCpu wraps a single X86JsonCpu + its X86Memory. Step() swaps
//     the static _activeMem / _activeCpu before calling _cpu.Step() so that
//     LockstepDiff (which alternates A.Step() / B.Step() in a loop) drives
//     the right backend each time.
//   - Caller is responsible for: identical initial memory contents (deep
//     copy of BIOS + disk image into each adapter's own X86Memory), shared
//     PortBus or stubbed I/O, and disabling any non-deterministic IRQ
//     sources (PIT timer) before invoking LockstepDiff.Run.

using AprCpu.Core.Validation;
using AprX86.Cli.Cpu;
using AprX86.Cli.Memory;

namespace AprX86.Cli.Validation;

/// <summary>
/// Snapshot of an X86JsonCpu's architectural state at one point in time.
/// Fields are flattened into a Dict for the generic comparator. PC is
/// the linear address (CS << 4 | IP) so two CPUs with different CS but
/// same effective address compare as equal — usually irrelevant in real
/// mode but defensive.
/// </summary>
public sealed class X86CpuStateSnapshot : ICpuStateSnapshot
{
    public ulong Pc { get; }
    public IReadOnlyDictionary<string, ulong> Registers { get; }
    public long CycleCount { get; }

    public X86CpuStateSnapshot(X86State s, long cycles)
    {
        Pc = (ulong)(((s.CS << 4) + s.IP) & 0xFFFFF);
        CycleCount = cycles;
        Registers = new Dictionary<string, ulong>
        {
            ["AX"] = s.A.X,
            ["BX"] = s.B.X,
            ["CX"] = s.C.X,
            ["DX"] = s.D.X,
            ["SI"] = s.SI,
            ["DI"] = s.DI,
            ["BP"] = s.BP,
            ["SP"] = s.SP,
            ["CS"] = s.CS,
            ["DS"] = s.DS,
            ["SS"] = s.SS,
            ["ES"] = s.ES,
            ["IP"] = s.IP,
            ["FLAGS"] = s.GetFlags(),
        };
    }
}

/// <summary>
/// ISteppableCpu adapter for X86JsonCpu. Wraps one CPU + its memory so
/// LockstepDiff can drive two of them in parallel. Also implements
/// IBlockBoundedSteppableCpu (Phase 30.15d) so the VerifiedBlockJitRunner
/// can use it for per-block verification.
/// </summary>
public sealed class X86SteppableCpu : IBlockBoundedSteppableCpu
{
    public string Name { get; }

    private readonly X86JsonCpu _cpu;
    private readonly X86Memory _mem;
    private long _steps;

    public X86JsonCpu Cpu => _cpu;
    public X86Memory Memory => _mem;

    public X86SteppableCpu(string name, X86JsonCpu cpu, X86Memory mem)
    {
        Name = name;
        _cpu = cpu;
        _mem = mem;
    }

    public ICpuStateSnapshot Snapshot()
    {
        // SetActive() before reading state — some accessors touch _activeMem
        // for side-effect-free address translation. The Snapshot itself is
        // pure read though, so SetActive isn't strictly required here, but
        // doing it keeps the singleton in a consistent "this CPU is the live
        // one" state for any subsequent ReadByteFromBus call.
        _cpu.SetActiveForLockstep();
        return new X86CpuStateSnapshot(_cpu.State, _steps);
    }

    public void Step()
    {
        _cpu.SetActiveForLockstep();
        _cpu.Step();
        _steps++;
    }

    public byte ReadByteFromBus(ulong addr)
        => _mem.ReadByte((int)(addr & 0xFFFFF));

    // === IBlockBoundedSteppableCpu (Phase 30.15d sprint 5.3) ============

    public int LastBlockInstructionCount => _cpu.LastBlockInstructionCount;

    public void StepOneArchitecturalInstruction()
    {
        _cpu.SetActiveForLockstep();
        _cpu.StepOnePerInstr();
        _steps++;
    }

    public object BeginTrace(IBlockTraceSink sink)
    {
        var prior = X86JsonCpu.ActiveTraceSink;
        X86JsonCpu.ActiveTraceSink = sink;
        return prior!;   // token = previous sink (nullable, but we boxed)
    }

    public void EndTrace(object token)
    {
        X86JsonCpu.ActiveTraceSink = token as IBlockTraceSink;
    }

    public object SnapshotMemoryState()
    {
        // V1: full 1MB memcpy + CPU state clone.
        var ramCopy = (byte[])_mem.Ram.Clone();
        var cpuCopy = _cpu.State;  // X86State.Clone-equivalent via getter
        return (ramCopy, cpuCopy);
    }

    public void LoadMemoryState(object snapshot)
    {
        var (ramCopy, cpuCopy) = ((byte[], AprX86.Cli.Cpu.X86State))snapshot;
        Array.Copy(ramCopy, _mem.Ram, ramCopy.Length);
        _cpu.LoadState(cpuCopy);
    }
}
