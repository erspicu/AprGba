// AprCpu.Core/Validation/LockstepDiff.cs
//
// N5 — generic lockstep diff toolkit. Run two ISteppableCpu implementations
// in lockstep, one architectural instruction at a time, and report the
// first divergence in any architectural register (or PC, or cycle count).
//
// Originally NES-specific (in AprNes.Cli/Program.cs); abstracted to be
// reusable across CPU architectures so future CPUs (R3000A, R4300i, ...)
// + GB-DMG / GBA backends can opt into the same correctness scaffolding.
//
// This is a *correctness validation tool*, not a perf path — straightforward
// snapshot-and-compare, no clever fastpath. Suitable for harnesses that
// step a few thousand instructions and assert no divergence.

using System.Text;

namespace AprCpu.Core.Validation;

/// <summary>
/// Snapshot of an architectural CPU state at a single point in time.
/// Implementations are expected to be cheap to allocate (used per-step in
/// lockstep) and to populate <see cref="Registers"/> with the architectural
/// state that should match between two implementations.
/// </summary>
public interface ICpuStateSnapshot
{
    /// <summary>Primary program counter / instruction pointer.</summary>
    ulong Pc { get; }

    /// <summary>
    /// Architectural registers + status flags. Keys are register names
    /// (e.g. "A", "X", "Y", "P", "SP" for 6502; "R0".."R15", "CPSR" for
    /// ARM). Values are zero-extended into ulong.
    /// </summary>
    IReadOnlyDictionary<string, ulong> Registers { get; }

    /// <summary>Total cycles executed so far. -1 if not tracked.</summary>
    long CycleCount { get; }
}

/// <summary>
/// A CPU implementation that can be driven one instruction at a time and
/// produce snapshots of its architectural state.
/// </summary>
public interface ISteppableCpu
{
    /// <summary>Display name (e.g. "legacy 2A03", "json-block").</summary>
    string Name { get; }

    /// <summary>Snapshot the current architectural state.</summary>
    ICpuStateSnapshot Snapshot();

    /// <summary>Execute one architectural instruction.</summary>
    void Step();

    /// <summary>
    /// Read a byte from the CPU's bus. Used for opcode trail capture; not
    /// performance-critical. Returns 0 if the implementation can't peek
    /// at its bus.
    /// </summary>
    byte ReadByteFromBus(ulong addr) => 0;
}

/// <summary>One captured (instruction-index, snapshot) pair for the trail.</summary>
public sealed record LockstepTrailEntry(
    long InstructionIndex,
    ICpuStateSnapshot State,
    byte OpcodeAtPc);

/// <summary>Outcome category for a lockstep run.</summary>
public enum LockstepStatus
{
    /// <summary>Both CPUs ran the full max-steps budget with no divergence.</summary>
    NoDiff,
    /// <summary>Architectural state diverged.</summary>
    Diverged,
    /// <summary>Halt condition matched on side A.</summary>
    HaltedOnA,
    /// <summary>An exception was thrown during stepping.</summary>
    Faulted,
}

/// <summary>
/// Result of a lockstep run.
///
/// On <see cref="LockstepStatus.Diverged"/>, <see cref="DivergedFields"/>
/// names the architectural fields that disagreed (e.g. "P", "PC"), and
/// <see cref="Trail"/> contains the last few (matched) snapshots so the
/// caller can show what led up to the divergence.
/// </summary>
public sealed record LockstepResult(
    LockstepStatus Status,
    long StepsExecuted,
    ICpuStateSnapshot SnapshotA,
    ICpuStateSnapshot SnapshotB,
    IReadOnlyList<LockstepTrailEntry> Trail,
    IReadOnlyList<string> DivergedFields,
    string? FaultMessage = null)
{
    public string FormatReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"  status:  {Status}");
        sb.AppendLine($"  steps:   {StepsExecuted}");
        if (Status == LockstepStatus.Diverged)
        {
            sb.AppendLine($"  trail (last {Trail.Count} matched instructions):");
            foreach (var t in Trail)
                sb.AppendLine($"    i={t.InstructionIndex,5} {FormatSnapshot(t.State)} op=0x{t.OpcodeAtPc:X2}");
            sb.AppendLine($"  divergence in: {string.Join(", ", DivergedFields)}");
            sb.AppendLine($"    A: {FormatSnapshot(SnapshotA)}");
            sb.AppendLine($"    B: {FormatSnapshot(SnapshotB)}");
        }
        if (FaultMessage is not null)
            sb.AppendLine($"  fault:   {FaultMessage}");
        return sb.ToString();
    }

    private static string FormatSnapshot(ICpuStateSnapshot s)
    {
        var sb = new StringBuilder();
        sb.Append($"PC=0x{s.Pc:X4}");
        foreach (var (name, val) in s.Registers)
            sb.Append($" {name}=0x{val:X2}");
        if (s.CycleCount >= 0) sb.Append($" cycles={s.CycleCount}");
        return sb.ToString();
    }
}

/// <summary>
/// Run two <see cref="ISteppableCpu"/> instances in lockstep and stop at
/// the first divergence. The two CPUs are expected to be initialised to
/// the same starting state by the caller — this toolkit doesn't help with
/// setup.
/// </summary>
public static class LockstepDiff
{
    /// <summary>
    /// Run lockstep until divergence, halt condition, or step budget exhausted.
    /// </summary>
    /// <param name="a">First CPU (often the oracle / reference).</param>
    /// <param name="b">Second CPU (often the implementation under test).</param>
    /// <param name="maxSteps">Max architectural instructions to run.</param>
    /// <param name="haltConditionA">Optional predicate over A's snapshot
    ///     that, when true, ends the run early. E.g. <c>s =&gt; s.Pc == 0xC66E</c>
    ///     for nestest's halt loop.</param>
    /// <param name="trailDepth">Last N matched snapshots kept for divergence
    ///     context.</param>
    /// <param name="ignoreFields">Field names to skip when comparing (e.g.
    ///     for the legacy NES backend P bit-5 forced-on quirk).</param>
    /// <param name="cycleSensitive">If true, CycleCount divergence is
    ///     treated as a divergence. Off by default since not all backends
    ///     track cycles identically.</param>
    public static LockstepResult Run(
        ISteppableCpu a,
        ISteppableCpu b,
        long maxSteps,
        Func<ICpuStateSnapshot, bool>? haltConditionA = null,
        int trailDepth = 6,
        IReadOnlySet<string>? ignoreFields = null,
        bool cycleSensitive = false)
    {
        var trail = new LockstepTrailEntry[trailDepth];
        int trailHead = 0;
        long matchedSteps = 0;

        try
        {
            for (long i = 0; i < maxSteps; i++)
            {
                var sa = a.Snapshot();
                var sb = b.Snapshot();

                var diverged = CompareSnapshots(sa, sb, ignoreFields, cycleSensitive);
                if (diverged.Count > 0)
                {
                    return new LockstepResult(
                        LockstepStatus.Diverged,
                        StepsExecuted: i,
                        SnapshotA: sa,
                        SnapshotB: sb,
                        Trail: SnapshotTrail(trail, trailHead),
                        DivergedFields: diverged);
                }

                if (haltConditionA is not null && haltConditionA(sa))
                {
                    return new LockstepResult(
                        LockstepStatus.HaltedOnA,
                        StepsExecuted: i,
                        SnapshotA: sa,
                        SnapshotB: sb,
                        Trail: SnapshotTrail(trail, trailHead),
                        DivergedFields: System.Array.Empty<string>());
                }

                trail[trailHead] = new LockstepTrailEntry(i, sa, a.ReadByteFromBus(sa.Pc));
                trailHead = (trailHead + 1) % trailDepth;
                matchedSteps = i + 1;

                a.Step();
                b.Step();
            }
        }
        catch (System.Exception ex)
        {
            var sa = a.Snapshot();
            var sb = b.Snapshot();
            return new LockstepResult(
                LockstepStatus.Faulted,
                StepsExecuted: matchedSteps,
                SnapshotA: sa,
                SnapshotB: sb,
                Trail: SnapshotTrail(trail, trailHead),
                DivergedFields: System.Array.Empty<string>(),
                FaultMessage: $"{ex.GetType().Name}: {ex.Message}");
        }

        var finalA = a.Snapshot();
        var finalB = b.Snapshot();
        return new LockstepResult(
            LockstepStatus.NoDiff,
            StepsExecuted: maxSteps,
            SnapshotA: finalA,
            SnapshotB: finalB,
            Trail: SnapshotTrail(trail, trailHead),
            DivergedFields: System.Array.Empty<string>());
    }

    private static List<string> CompareSnapshots(
        ICpuStateSnapshot a,
        ICpuStateSnapshot b,
        IReadOnlySet<string>? ignoreFields,
        bool cycleSensitive)
    {
        var diffs = new List<string>();
        if (a.Pc != b.Pc) diffs.Add("PC");

        // Union of register keys — if either side declares a key, compare it.
        var keys = new HashSet<string>(a.Registers.Keys);
        foreach (var k in b.Registers.Keys) keys.Add(k);
        foreach (var key in keys)
        {
            if (ignoreFields is not null && ignoreFields.Contains(key)) continue;
            a.Registers.TryGetValue(key, out var va);
            b.Registers.TryGetValue(key, out var vb);
            if (va != vb) diffs.Add(key);
        }

        if (cycleSensitive && a.CycleCount != b.CycleCount)
            diffs.Add("cycles");

        return diffs;
    }

    private static IReadOnlyList<LockstepTrailEntry> SnapshotTrail(
        LockstepTrailEntry[] buf, int head)
    {
        var list = new List<LockstepTrailEntry>(buf.Length);
        for (int k = 0; k < buf.Length; k++)
        {
            var t = buf[(head + k) % buf.Length];
            if (t is null) continue;
            list.Add(t);
        }
        return list;
    }
}
