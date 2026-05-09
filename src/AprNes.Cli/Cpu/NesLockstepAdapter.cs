// Adapter that exposes an INesCpuBackend through the framework-level
// ISteppableCpu interface (AprCpu.Core/Validation/LockstepDiff.cs).
//
// N5 — lets any pair of NES CPU backends (BoundCpu / NesJsonCpu per-instr /
// NesJsonCpu block-JIT) be lockstep-tested via the generic toolkit instead
// of the hand-rolled loop in Program.cs.

using AprCpu.Core.Validation;
using AprNes.Cli.Memory;

namespace AprNes.Cli.Cpu;

public sealed class NesLockstepAdapter : ISteppableCpu
{
    private readonly INesCpuBackend _cpu;
    private readonly NesMemoryBus _bus;
    public string Name { get; }

    public NesLockstepAdapter(INesCpuBackend cpu, NesMemoryBus bus, string? name = null)
    {
        _cpu = cpu;
        _bus = bus;
        Name = name ?? cpu.BackendName;
    }

    public ICpuStateSnapshot Snapshot()
    {
        // BoundCpu (LegacyCpu) returns P with bit-5 (U) cleared — but the
        // 6502 architectural P register always reads with bit-5 set on
        // PHP / BRK. Force it on here so legacy and json backends compare
        // identically. (Same trick the existing diff loop in Program.cs
        // uses.)
        byte p = _cpu.P;
        if (_cpu is BoundCpu) p |= 0x20;

        return new NesSnapshot(
            Pc: _cpu.PC,
            A: _cpu.A,
            X: _cpu.X,
            Y: _cpu.Y,
            SP: _cpu.SP,
            P: p);
    }

    public void Step() => _cpu.Step();

    public byte ReadByteFromBus(ulong addr) => _bus.ReadByte((ushort)addr);

    private sealed class NesSnapshot : ICpuStateSnapshot
    {
        public ulong Pc { get; }
        public IReadOnlyDictionary<string, ulong> Registers { get; }
        public long CycleCount => -1;     // not tracked at this layer

        public NesSnapshot(ushort Pc, byte A, byte X, byte Y, byte SP, byte P)
        {
            this.Pc = Pc;
            Registers = new Dictionary<string, ulong>
            {
                ["A"]  = A,
                ["X"]  = X,
                ["Y"]  = Y,
                ["SP"] = SP,
                ["P"]  = P,
            };
        }
    }
}
