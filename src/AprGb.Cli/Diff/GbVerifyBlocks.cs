// AprGb.Cli/Diff/GbVerifyBlocks.cs
//
// Phase 30.16 sprint 5.5b — CLI entry point for `apr-gb --verify-blocks`.
// Wires the generic VerifiedBlockJitRunner against TWO independent
// GB env (each = JsonCpu + GbMemoryBus with same ROM + BIOS loaded),
// then runs RunAndVerifyOneBlock until:
//   - --verify-blocks=N blocks verified
//   - First divergence reported (= bug found)

using AprCpu.Core.Validation;
using AprGb.Cli.Cpu;
using AprGb.Cli.Memory;
using AprGb.Cli.Validation;

namespace AprGb.Cli.Diff;

public static class GbVerifyBlocks
{
    /// <summary>
    /// Build a fresh env (CPU + bus) with the same ROM + optional BIOS.
    /// Each env owns its own memory; both start in the same reset state.
    /// </summary>
    private static (JsonCpu Cpu, GbMemoryBus Bus) BuildEnv(byte[] rom, byte[]? bios)
    {
        var bus = new GbMemoryBus();
        if (bios is not null && bios.Length > 0) bus.LoadBios(bios);
        bus.LoadRom(rom);
        var cpu = new JsonCpu(enableBlockJit: true);   // JIT side defaults; verifier still drives interp side via StepOnePerInstr
        cpu.Reset(bus);
        return (cpu, bus);
    }

    /// <summary>
    /// Run the per-block JIT-vs-interp verifier. Returns:
    ///   0 = NoDiff up to maxBlocks
    ///   5 = divergence reported
    ///   2 = exception during verification
    /// </summary>
    public static int Run(string romPath, string? biosPath, long maxBlocks)
    {
        Console.WriteLine("apr-gb verify-blocks (per-block JIT-vs-interp diff)");
        Console.WriteLine($"  ROM:    {romPath}");
        Console.WriteLine($"  BIOS:   {biosPath ?? "(none — boot straight to cart @ 0x0100)"}");
        Console.WriteLine($"  blocks: up to {maxBlocks:N0}");

        // Phase 30.16 sprint 5.5 — gate off the GB block-JIT inline
        // RAM fast path so every write goes through MemWrite8 extern
        // and the verifier's ActiveTraceSink sees all of it. Without
        // this, JIT inline-writes WRAM/HRAM directly via baked pointers
        // and the trace count diverges from INTERP's per-byte path.
        Environment.SetEnvironmentVariable("APR_GB_NO_INLINE_RAM", "1");
        // Phase 30.18i — disable cross-jump-follow (see GbFuzzer note).
        Environment.SetEnvironmentVariable("APR_NO_CROSS_JUMP_FOLLOW", "1");

        var rom = RomLoader.Load(romPath);
        var bios = biosPath is not null ? File.ReadAllBytes(biosPath) : null;

        // Build two completely independent envs. Each JsonCpu has its
        // own state buffer; each GbMemoryBus has its own Wram/Vram/etc.
        // ROM bytes are read-only and shared, but reads from each env
        // route through that env's own bus instance.
        var (cpuJit,    busJit)    = BuildEnv(rom, bios);
        var (cpuInterp, busInterp) = BuildEnv(rom, bios);

        // For interp side: construct JsonCpu with enableBlockJit=true
        // too, but Step the interp side via StepOnePerInstr() which
        // forces per-instr regardless. Both envs see the same code, so
        // they'd compile identical blocks anyway if the JIT path were
        // taken — but per-instr keeps the verifier honest.

        var jitStepper    = new GbSteppableCpu("JIT",    cpuJit,    busJit);
        var interpStepper = new GbSteppableCpu("INTERP", cpuInterp, busInterp);

        // Phase 30.15d sprint 5.4d analogue — each stepper activates
        // its own env's static bus before stepping.
        jitStepper.OnBeforeStep    = () => cpuJit.SetActiveForLockstep();
        interpStepper.OnBeforeStep = () => cpuInterp.SetActiveForLockstep();

        // Activate JIT env once at start so first compile goes there.
        cpuJit.SetActiveForLockstep();

        var runner = new VerifiedBlockJitRunner(jitStepper, interpStepper);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long verifiedBlocks = 0;
        long verifiedInstrs = 0;

        for (long b = 0; b < maxBlocks; b++)
        {
            cpuJit.SetActiveForLockstep();
            VerifiedBlockResult r;
            try
            {
                r = runner.RunAndVerifyOneBlock();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ERROR at block {b}: {ex.GetType().Name}: {ex.Message}");
                sw.Stop();
                Console.WriteLine($"  elapsed: {sw.Elapsed}");
                return 2;
            }
            verifiedBlocks++;
            verifiedInstrs += Math.Max(r.InstructionCount, 0);

            if (r.Status != VerifiedBlockStatus.Ok)
            {
                sw.Stop();
                Console.WriteLine();
                Console.WriteLine($"  elapsed:           {sw.Elapsed}");
                Console.WriteLine($"  verified blocks:   {verifiedBlocks - 1} OK before divergence");
                Console.WriteLine($"  verified instrs:   {verifiedInstrs - r.InstructionCount}");
                Console.WriteLine($"  status:            {r.Status}");
                Console.WriteLine($"  diverged at block: #{r.BlockIndex}, pc=0x{r.BlockStartPc:X4}, " +
                    $"instrs in block={r.InstructionCount}");
                Console.WriteLine($"  detail:            {r.Detail}");
                if (r.CpuStatePre is not null)
                    Console.WriteLine($"  pre-block:         {r.CpuStatePre}");
                if (r.CpuStateA is not null)
                {
                    Console.WriteLine($"  JIT post-block:    {r.CpuStateA}");
                    Console.WriteLine($"  INTERP post-block: {r.CpuStateB}");
                }
                return 5;
            }
        }

        sw.Stop();
        Console.WriteLine();
        Console.WriteLine($"  elapsed:         {sw.Elapsed}");
        Console.WriteLine($"  verified blocks: {verifiedBlocks}");
        Console.WriteLine($"  verified instrs: {verifiedInstrs}");
        Console.WriteLine($"  status:          NoDiff");
        return 0;
    }
}
