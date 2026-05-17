namespace AprGb.Cli.Cpu;

/// <summary>
/// Phase 30.16 sprint 5.5 — opaque snapshot blob produced by
/// <see cref="JsonCpu.SnapshotState"/> and consumed by
/// <see cref="JsonCpu.LoadState"/>. Used by the Verified Block-JIT
/// framework to restore the CPU side of pre-block state before driving
/// the interpreter for the same number of instructions as the JIT
/// just ran. The bus memory is snapshotted separately by the harness.
/// </summary>
public sealed class GbCpuStateBlob
{
    public byte[] StateCopy = System.Array.Empty<byte>();
    public bool   Halted;
    public bool   Ime;
    public int    EiDelay;
    public bool   HaltSignal;
    public long   TotalInstructions;
    public int    BlockGeneration;
}
