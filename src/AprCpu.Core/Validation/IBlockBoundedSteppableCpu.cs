// AprCpu.Core/Validation/IBlockBoundedSteppableCpu.cs
//
// Phase 30.15d sprint 5.1 — interfaces for the Verified Block-JIT
// framework feature (per MD/design/30.15d-verified-blockjit-framework-
// design.md). Extends the existing ISteppableCpu so CPUs that opt
// into per-block verification can have the framework drive a
// JIT-mode "primary" pass and an interp-mode "verifier" pass on the
// same initial state, then 3-axis compare at block exit:
//   - CPU state (already in ISteppableCpu via Snapshot)
//   - Memory write trace (new — captured via IBlockTraceSink)
//   - Side-effect log (new — port writes, IRQ asserts)
//
// CPUs that don't opt in continue to work through ISteppableCpu
// (lockstep-only). The verifier framework refuses to run on those.

namespace AprCpu.Core.Validation;

/// <summary>
/// Sink that a JIT-backed CPU writes per-block side effects into so
/// the verifier framework can diff against an interpreter run.
/// All record methods are no-op on a null sink — callers MAY null-check
/// for hot paths but the canonical pattern is to leave a single static
/// nullable field that the JIT shim reads.
/// </summary>
public interface IBlockTraceSink
{
    /// <summary>
    /// Record a guest memory write. Called from the JIT-emitted
    /// memory_write extern (and from the interp's bus write path).
    /// </summary>
    /// <param name="instrIndexWithinBlock">0-based index of the
    ///     instruction inside the currently-executing block, so the
    ///     verifier can pinpoint which architectural instruction's
    ///     emit produced the divergent write.</param>
    /// <param name="linearAddr">Linear (physical) address of the write.</param>
    /// <param name="value">Stored value (zero-extended to u64 for u8/u16/u32 writes).</param>
    /// <param name="sizeBytes">1, 2, or 4.</param>
    void RecordMemWrite(int instrIndexWithinBlock, uint linearAddr, ulong value, byte sizeBytes);

    /// <summary>
    /// Record a port write (x86 OUT family). Optional — backends without
    /// port I/O leave this unimplemented.
    /// </summary>
    void RecordPortWrite(int instrIndexWithinBlock, ushort port, ushort value, byte sizeBytes) { }

    /// <summary>
    /// Record a host-side IRQ assertion that the executing instruction
    /// or HW chip caused. Used by side-effect axis comparison.
    /// </summary>
    void RecordIrqAssert(int instrIndexWithinBlock, byte vector) { }
}

/// <summary>
/// Opt-in interface for CPUs that can be driven block-by-block under
/// the verifier framework. The framework asks for snapshots of CPU
/// state + memory at block boundaries, drives the JIT primary pass,
/// captures its trace, restores the snapshot, drives the interp
/// verifier the same number of instructions, captures ITS trace, and
/// finally diffs.
/// </summary>
public interface IBlockBoundedSteppableCpu : ISteppableCpu
{
    /// <summary>
    /// Architectural-instruction count of the most-recent block
    /// execution. -1 if the last call was not a block (e.g. fell back
    /// to per-instr or no block has run yet).
    /// </summary>
    int LastBlockInstructionCount { get; }

    /// <summary>
    /// Force the next Step() to execute exactly one architectural
    /// instruction (no block compilation / no JIT cache). Used by the
    /// verifier when stepping the interpreter side to mirror the JIT
    /// block's instruction count.
    /// </summary>
    void StepOneArchitecturalInstruction();

    /// <summary>
    /// Install <paramref name="sink"/> as the active trace target. All
    /// subsequent memory writes / port writes / IRQ asserts from this
    /// CPU's Step() are recorded until <see cref="EndTrace"/>. Returns
    /// an opaque token to pass back to EndTrace (lets the framework
    /// nest scopes if needed).
    /// </summary>
    object BeginTrace(IBlockTraceSink sink);

    /// <summary>Stop recording — pairs with <see cref="BeginTrace"/>.</summary>
    void EndTrace(object token);

    /// <summary>
    /// Snapshot full guest memory + CPU state into an opaque blob the
    /// framework can later restore. V1 implementations memcpy the
    /// guest RAM; future versions may use COW page tables for speed.
    /// </summary>
    object SnapshotMemoryState();

    /// <summary>Restore a snapshot produced by SnapshotMemoryState().</summary>
    void LoadMemoryState(object snapshot);
}

/// <summary>
/// Built-in in-memory trace sink with a fixed-size ring buffer.
/// Designed for verifier use (each block trace is short — usually
/// 1-10 entries). Overflow is recorded as a flag; the verifier reports
/// it as "trace truncated, divergence-detection partial".
/// </summary>
public sealed class RingBufferTraceSink : IBlockTraceSink
{
    public readonly struct MemWriteRecord
    {
        public readonly int InstrIndexWithinBlock;
        public readonly uint LinearAddr;
        public readonly ulong Value;
        public readonly byte SizeBytes;
        public MemWriteRecord(int i, uint a, ulong v, byte s)
        { InstrIndexWithinBlock = i; LinearAddr = a; Value = v; SizeBytes = s; }
        public override string ToString()
            => $"i{InstrIndexWithinBlock} addr=0x{LinearAddr:X5} val=0x{Value:X} sz={SizeBytes}";
    }
    public readonly struct PortWriteRecord
    {
        public readonly int InstrIndexWithinBlock;
        public readonly ushort Port;
        public readonly ushort Value;
        public readonly byte SizeBytes;
        public PortWriteRecord(int i, ushort p, ushort v, byte s)
        { InstrIndexWithinBlock = i; Port = p; Value = v; SizeBytes = s; }
    }
    public readonly struct IrqAssertRecord
    {
        public readonly int InstrIndexWithinBlock;
        public readonly byte Vector;
        public IrqAssertRecord(int i, byte v)
        { InstrIndexWithinBlock = i; Vector = v; }
    }

    private readonly MemWriteRecord[] _mem;
    private readonly PortWriteRecord[] _ports;
    private readonly IrqAssertRecord[] _irq;
    public int MemWriteCount { get; private set; }
    public int PortWriteCount { get; private set; }
    public int IrqAssertCount { get; private set; }
    public bool MemWriteTruncated { get; private set; }
    public bool PortWriteTruncated { get; private set; }
    public bool IrqAssertTruncated { get; private set; }

    public RingBufferTraceSink(int memCapacity = 256, int portCapacity = 32, int irqCapacity = 16)
    {
        _mem = new MemWriteRecord[memCapacity];
        _ports = new PortWriteRecord[portCapacity];
        _irq = new IrqAssertRecord[irqCapacity];
    }

    public void Reset()
    {
        MemWriteCount = PortWriteCount = IrqAssertCount = 0;
        MemWriteTruncated = PortWriteTruncated = IrqAssertTruncated = false;
    }

    public void RecordMemWrite(int idx, uint addr, ulong val, byte sz)
    {
        if (MemWriteCount < _mem.Length) _mem[MemWriteCount++] = new MemWriteRecord(idx, addr, val, sz);
        else MemWriteTruncated = true;
    }
    public void RecordPortWrite(int idx, ushort port, ushort val, byte sz)
    {
        if (PortWriteCount < _ports.Length) _ports[PortWriteCount++] = new PortWriteRecord(idx, port, val, sz);
        else PortWriteTruncated = true;
    }
    public void RecordIrqAssert(int idx, byte vec)
    {
        if (IrqAssertCount < _irq.Length) _irq[IrqAssertCount++] = new IrqAssertRecord(idx, vec);
        else IrqAssertTruncated = true;
    }

    public ReadOnlySpan<MemWriteRecord> MemWrites => _mem.AsSpan(0, MemWriteCount);
    public ReadOnlySpan<PortWriteRecord> PortWrites => _ports.AsSpan(0, PortWriteCount);
    public ReadOnlySpan<IrqAssertRecord> IrqAsserts => _irq.AsSpan(0, IrqAssertCount);
}
