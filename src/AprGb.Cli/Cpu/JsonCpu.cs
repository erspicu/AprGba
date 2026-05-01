using AprGb.Cli.Memory;

namespace AprGb.Cli.Cpu;

/// <summary>
/// Game Boy CPU driven by the JSON-driven LLVM JIT framework
/// (<see cref="AprCpu.Core"/>). **Skeleton only** in this iteration —
/// the actual <c>spec/lr35902/*.json</c> needs to be written first.
/// Once the spec lands, this backend will compile it through
/// <c>SpecCompiler</c>, JIT it via <c>HostRuntime</c>, and dispatch
/// instructions through <c>CpuExecutor</c> exactly the way the
/// existing GBA tests do.
/// </summary>
public sealed class JsonCpu : ICpuBackend
{
    public string Name => "json-llvm";

    public void Reset(GbMemoryBus bus)
        => throw new NotImplementedException(
            "JsonCpu backend is a placeholder. Phase 4.5: write spec/lr35902/*.json + wire to AprCpu.Core.");

    public long RunCycles(long targetCycles) =>
        throw new NotImplementedException();

    public ushort ReadReg16(GbReg16 reg) =>
        throw new NotImplementedException();

    public byte ReadReg8(GbReg8 reg) =>
        throw new NotImplementedException();

    public bool IsHalted => false;
}
