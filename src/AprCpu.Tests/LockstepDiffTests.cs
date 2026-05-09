// N5 — generic lockstep diff toolkit smoke test.
//
// Runs BoundCpu (LegacyCpu oracle) and NesJsonCpu (per-instr JSON-spec
// backend) in lockstep over the first ~500 instructions of nestest.nes.
// Asserts that the toolkit reports NoDiff (or HaltedOnA at PC=0xC66E),
// proving the abstraction works end-to-end with two different
// implementations of the same architectural state.

using AprCpu.Core.Validation;
using AprNes.Cli;
using AprNes.Cli.Cpu;
using AprNes.Cli.Memory;
using Xunit;

namespace AprCpu.Tests;

public class LockstepDiffTests
{
    private static string NestestRomPath =>
        Path.Combine(TestPaths.RepoRoot, "test-roms", "nes-test", "nestest.nes");

    [Fact]
    public void Lockstep_LegacyVsJsonPerInstr_NoDivergence_FirstFewHundredInstr()
    {
        Assert.True(File.Exists(NestestRomPath));
        var rom = NesRomLoader.Load(NestestRomPath);

        // Two independent buses + mappers — lockstep needs separate state.
        var mLeg = new Mapper000(); mLeg.Reset(rom.PrgRom, rom.ChrRom);
        var busLeg = new NesMemoryBus(); busLeg.Reset(mLeg);
        var cpuLeg = new BoundCpu(busLeg);
        cpuLeg.InitForNestest();

        var mJit = new Mapper000(); mJit.Reset(rom.PrgRom, rom.ChrRom);
        var busJit = new NesMemoryBus(); busJit.Reset(mJit);
        var cpuJit = new NesJsonCpu(busJit);
        cpuJit.InitForNestest();

        // Adapt to the framework-level interface.
        var a = new NesLockstepAdapter(cpuLeg, busLeg, "legacy");
        var b = new NesLockstepAdapter(cpuJit, busJit, "json");

        // 500 instructions cover the early "Begin tests" preamble well
        // past any vector setup. nestest's pass-marker PC=$C66E sits at
        // the very end (~9000 instr) — we don't need to reach it here,
        // a clean 500-instr lockstep already proves the toolkit works.
        var result = LockstepDiff.Run(
            a, b,
            maxSteps: 500,
            haltConditionA: s => s.Pc == 0xC66E);

        Assert.True(
            result.Status == LockstepStatus.NoDiff ||
            result.Status == LockstepStatus.HaltedOnA,
            $"unexpected lockstep status {result.Status}\n{result.FormatReport()}");

        // Sanity: confirmed final A & B are register-by-register identical.
        foreach (var key in new[] { "A", "X", "Y", "SP", "P" })
        {
            Assert.Equal(result.SnapshotA.Registers[key], result.SnapshotB.Registers[key]);
        }
        Assert.Equal(result.SnapshotA.Pc, result.SnapshotB.Pc);
    }

    [Fact]
    public void Lockstep_DivergenceIsDetected_WithSyntheticMismatch()
    {
        // Verify that LockstepDiff actually catches divergence — not just
        // a no-op pass-through. Construct two synthetic CPUs that diverge
        // on the 3rd step.
        var a = new ToyCpu(diverge: false);
        var b = new ToyCpu(diverge: true);

        var result = LockstepDiff.Run(a, b, maxSteps: 100);

        Assert.Equal(LockstepStatus.Diverged, result.Status);
        Assert.Equal(3, result.StepsExecuted);
        Assert.Contains("R0", result.DivergedFields);
    }

    private sealed class ToyCpu : ISteppableCpu
    {
        private readonly bool _diverge;
        private ulong _pc;
        private ulong _r0;
        private long _step;

        public ToyCpu(bool diverge) { _diverge = diverge; }
        public string Name => _diverge ? "B" : "A";

        public ICpuStateSnapshot Snapshot()
            => new Snap(_pc, _r0);

        public void Step()
        {
            _pc += 4;
            _r0 += 1;
            _step++;
            // After 3 steps, B starts ticking R0 differently.
            if (_diverge && _step >= 3) _r0 += 100;
        }

        private sealed record Snap(ulong Pc, ulong R0Val) : ICpuStateSnapshot
        {
            public IReadOnlyDictionary<string, ulong> Registers { get; } =
                new Dictionary<string, ulong> { ["R0"] = R0Val };
            public long CycleCount => -1;
        }
    }
}
