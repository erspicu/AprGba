// AprCpu.Core/Validation/VerifiedBlockJitRunner.cs
//
// Phase 30.15d sprint 5.2 — generic per-block lockstep harness.
// Drives a JIT-mode CPU as the "primary" execution, then for each
// block runs the same instruction count through an interp-mode CPU
// from an identical pre-block state, then 3-axis compares:
//   - CPU state at block exit (Snapshot via ISteppableCpu)
//   - Memory write trace (via IBlockTraceSink)
//   - Side-effect log (port writes / IRQ asserts)
//
// Reports the FIRST divergence (block linearPc + instr index within
// block + which axis differed) with enough context to localise the
// bug to a specific emitter. See MD/design/30.15d-verified-blockjit-
// framework-design.md §4-§5 for the full design.

using System.Text;

namespace AprCpu.Core.Validation;

/// <summary>
/// Outcome of a single verified-block check.
/// </summary>
public enum VerifiedBlockStatus
{
    /// <summary>Both CPUs produced identical state + trace; block verified.</summary>
    Ok,
    /// <summary>CPU snapshot differed between JIT and interp at block exit.</summary>
    CpuStateMismatch,
    /// <summary>Memory write trace differed in count, address, value, or order.</summary>
    MemWriteTraceMismatch,
    /// <summary>Side-effect log differed (port writes or IRQ asserts).</summary>
    SideEffectMismatch,
    /// <summary>Could not verify (e.g. trace buffer overflowed, interp threw).</summary>
    CouldNotVerify,
}

public sealed record VerifiedBlockResult(
    VerifiedBlockStatus Status,
    long BlockIndex,
    ulong BlockStartPc,
    int InstructionCount,
    string Detail,
    string? CpuStateA = null,
    string? CpuStateB = null,
    string? CpuStatePre = null);

/// <summary>
/// Driver for verified-block-JIT mode. Owns the JIT CPU and interp
/// CPU, plus the trace sinks. Step() runs one block on JIT, verifies
/// via interp, returns the result.
/// </summary>
public sealed class VerifiedBlockJitRunner
{
    private readonly IBlockBoundedSteppableCpu _jit;
    private readonly IBlockBoundedSteppableCpu _interp;
    private readonly RingBufferTraceSink _jitSink = new();
    private readonly RingBufferTraceSink _interpSink = new();
    private long _blockIndex;

    public VerifiedBlockJitRunner(
        IBlockBoundedSteppableCpu jit,
        IBlockBoundedSteppableCpu interp)
    {
        _jit = jit;
        _interp = interp;
    }

    /// <summary>
    /// Run one block on the JIT, then verify by re-running the same
    /// instruction count through the interp from the captured pre-state.
    /// </summary>
    public VerifiedBlockResult RunAndVerifyOneBlock()
    {
        // 1. Capture pre-block state from JIT (memory + CPU).
        var snapshot = _jit.SnapshotMemoryState();
        var cpuPre = _jit.Snapshot();

        // 2. Run JIT block with trace capture.
        _jitSink.Reset();
        var jitToken = _jit.BeginTrace(_jitSink);
        try { _jit.Step(); }
        finally { _jit.EndTrace(jitToken); }
        int blockInstrCount = _jit.LastBlockInstructionCount;
        var cpuPostJit = _jit.Snapshot();

        if (blockInstrCount <= 0)
        {
            // JIT bailed out (e.g. fell back to per-instr); no block to verify.
            return new VerifiedBlockResult(
                VerifiedBlockStatus.Ok, _blockIndex++,
                cpuPre.Pc, 0, "(no block executed — interp comparison skipped)");
        }

        // 3. Restore pre-block state to interp.
        _interp.LoadMemoryState(snapshot);

        // 4. Run interp the same number of architectural instructions
        //    with trace capture.
        _interpSink.Reset();
        var interpToken = _interp.BeginTrace(_interpSink);
        try
        {
            for (int i = 0; i < blockInstrCount; i++)
                _interp.StepOneArchitecturalInstruction();
        }
        catch (Exception ex)
        {
            _interp.EndTrace(interpToken);
            return new VerifiedBlockResult(
                VerifiedBlockStatus.CouldNotVerify, _blockIndex++,
                cpuPre.Pc, blockInstrCount,
                $"interp threw during verification: {ex.GetType().Name}: {ex.Message}");
        }
        _interp.EndTrace(interpToken);
        var cpuPostInterp = _interp.Snapshot();

        // 5. Axis 1: CPU state.
        var cpuDiff = CompareCpuStates(cpuPostJit, cpuPostInterp);
        if (cpuDiff is not null)
        {
            return new VerifiedBlockResult(
                VerifiedBlockStatus.CpuStateMismatch, _blockIndex++,
                cpuPre.Pc, blockInstrCount,
                $"CPU state diverged: {cpuDiff}",
                CpuStateA: FormatSnapshot(cpuPostJit),
                CpuStateB: FormatSnapshot(cpuPostInterp),
                CpuStatePre: FormatSnapshot(cpuPre));
        }

        // 6. Axis 2: memory write trace.
        var memDiff = CompareMemWriteTraces(_jitSink, _interpSink);
        if (memDiff is not null)
        {
            return new VerifiedBlockResult(
                VerifiedBlockStatus.MemWriteTraceMismatch, _blockIndex++,
                cpuPre.Pc, blockInstrCount,
                $"mem-write trace diverged: {memDiff}",
                CpuStateA: FormatSnapshot(cpuPostJit),
                CpuStateB: FormatSnapshot(cpuPostInterp),
                CpuStatePre: FormatSnapshot(cpuPre));
        }

        // 7. Axis 3: side effects (ports + IRQs).
        var sideDiff = CompareSideEffects(_jitSink, _interpSink);
        if (sideDiff is not null)
        {
            return new VerifiedBlockResult(
                VerifiedBlockStatus.SideEffectMismatch, _blockIndex++,
                cpuPre.Pc, blockInstrCount,
                $"side-effects diverged: {sideDiff}");
        }

        // All axes agree.
        return new VerifiedBlockResult(
            VerifiedBlockStatus.Ok, _blockIndex++,
            cpuPre.Pc, blockInstrCount,
            $"verified {blockInstrCount} instructions, {_jitSink.MemWriteCount} mem writes");
    }

    private static string? CompareCpuStates(ICpuStateSnapshot a, ICpuStateSnapshot b)
    {
        if (a.Pc != b.Pc) return $"PC: A=0x{a.Pc:X} B=0x{b.Pc:X}";
        var keys = new HashSet<string>(a.Registers.Keys);
        foreach (var k in b.Registers.Keys) keys.Add(k);
        foreach (var key in keys)
        {
            a.Registers.TryGetValue(key, out var va);
            b.Registers.TryGetValue(key, out var vb);
            if (va != vb) return $"{key}: A=0x{va:X} B=0x{vb:X}";
        }
        return null;
    }

    private static string? CompareMemWriteTraces(RingBufferTraceSink a, RingBufferTraceSink b)
    {
        if (a.MemWriteTruncated || b.MemWriteTruncated)
            return $"trace truncated (A={a.MemWriteTruncated}, B={b.MemWriteTruncated}) — increase RingBuffer capacity";
        if (a.MemWriteCount != b.MemWriteCount)
            return $"count: A={a.MemWriteCount} B={b.MemWriteCount}";
        for (int i = 0; i < a.MemWriteCount; i++)
        {
            var ra = a.MemWrites[i];
            var rb = b.MemWrites[i];
            if (ra.LinearAddr != rb.LinearAddr
                || ra.Value != rb.Value
                || ra.SizeBytes != rb.SizeBytes)
            {
                return $"write[{i}]: A={ra} B={rb}";
            }
        }
        return null;
    }

    private static string? CompareSideEffects(RingBufferTraceSink a, RingBufferTraceSink b)
    {
        if (a.PortWriteCount != b.PortWriteCount)
            return $"port-write count: A={a.PortWriteCount} B={b.PortWriteCount}";
        if (a.IrqAssertCount != b.IrqAssertCount)
            return $"irq-assert count: A={a.IrqAssertCount} B={b.IrqAssertCount}";
        // Detailed compare for ports.
        for (int i = 0; i < a.PortWriteCount; i++)
        {
            var ra = a.PortWrites[i];
            var rb = b.PortWrites[i];
            if (ra.Port != rb.Port || ra.Value != rb.Value || ra.SizeBytes != rb.SizeBytes)
                return $"port-write[{i}]: A(port=0x{ra.Port:X3} val=0x{ra.Value:X} sz={ra.SizeBytes}) " +
                       $"B(port=0x{rb.Port:X3} val=0x{rb.Value:X} sz={rb.SizeBytes})";
        }
        for (int i = 0; i < a.IrqAssertCount; i++)
        {
            var ra = a.IrqAsserts[i];
            var rb = b.IrqAsserts[i];
            if (ra.Vector != rb.Vector)
                return $"irq-assert[{i}]: A=0x{ra.Vector:X2} B=0x{rb.Vector:X2}";
        }
        return null;
    }

    private static string FormatSnapshot(ICpuStateSnapshot s)
    {
        var sb = new StringBuilder();
        sb.Append($"PC=0x{s.Pc:X5}");
        foreach (var (n, v) in s.Registers) sb.Append($" {n}=0x{v:X4}");
        return sb.ToString();
    }
}
