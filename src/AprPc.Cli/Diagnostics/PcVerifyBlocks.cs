// AprPc.Cli/Diagnostics/PcVerifyBlocks.cs
//
// Phase 30.15d sprint 5.4 — CLI entry point for `apr-pc --verify-blocks`.
// Wires the generic VerifiedBlockJitRunner (AprCpu.Core/Validation/)
// against TWO PcLockstepEnv instances (one JIT, one interp), each with
// its own bus / memory / HW chips, both loaded with the same BIOS +
// floppy. Then runs RunAndVerifyOneBlock until:
//   - --max-cycles blocks verified
//   - First divergence reported (= bug found)
//   - JIT halts

using AprCpu.Core.Validation;
using AprPc.Cli.Hardware;
using AprPc.Cli.Memory;
using AprX86.Cli.Validation;

namespace AprPc.Cli.Diagnostics;

public static class PcVerifyBlocks
{
    public static int Run(PcOptions options)
    {
        Console.WriteLine("apr-pc verify-blocks (per-block JIT-vs-interp diff)");
        Console.WriteLine($"  cpu       = {options.Cpu}");
        Console.WriteLine($"  bios      = {options.BiosPath ?? "(none)"}");
        Console.WriteLine($"  video-bios= {options.VideoBiosPath ?? "(none)"}");
        Console.WriteLine($"  floppy A  = {options.FloppyAPath ?? "(none)"}");

        // JIT side = full block-JIT (max block size from APR_X86_BLOCK_MAX
        // env or default 64). Interp side = per-instr (no JIT cache).
        // Per design doc §4.2 the verifier framework drives both with the
        // generic ISteppableCpu interface; the JIT side reports block
        // size via LastBlockInstructionCount and the interp side steps
        // exactly N times via StepOneArchitecturalInstruction().
        var envJit    = PcLockstep.BuildEnvForVerify("JIT",    blockJit: true,  options);
        var envInterp = PcLockstep.BuildEnvForVerify("INTERP", blockJit: false, options);

        // Identical disk mount.
        if (options.FloppyAPath is { } fa)
        {
            var size = new FileInfo(fa).Length;
            if (size >= 64 * 1024)
            {
                envJit.Fdc?.AttachDrive(0x00, DiskImage.LoadFloppy(fa));
                envInterp.Fdc?.AttachDrive(0x00, DiskImage.LoadFloppy(fa));
                Console.WriteLine($"  floppy mounted ({size / 1024} KB) into both envs");
            }
        }

        // Disable PIT to avoid non-deterministic IRQ divergences (same
        // rationale as PcLockstep — verifier wants deterministic execution).
        envJit.Pit.StopForLockstep();
        envInterp.Pit.StopForLockstep();
        Console.WriteLine("  PIT timer + IRQ: fully stopped (deterministic)");

        var jitStepper = new X86SteppableCpu("JIT", envJit.Cpu, envJit.Bus.Memory);
        var interpStepper = new X86SteppableCpu("INTERP", envInterp.Cpu, envInterp.Bus.Memory);

        // Activate JIT env once at start so JIT-emitted code routes correctly.
        envJit.Activate();
        var runner = new VerifiedBlockJitRunner(jitStepper, interpStepper);

        long maxBlocks = options.MaxCycles ?? 100_000;
        Console.WriteLine($"  running verified-block up to {maxBlocks:N0} blocks...");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        long verifiedBlocks = 0;
        long verifiedInstrs = 0;
        for (long b = 0; b < maxBlocks; b++)
        {
            // Activate JIT env before running — VerifiedBlockJitRunner calls
            // Step() on the JIT stepper first, which calls _cpu.Step(); we
            // need JIT env's port handlers + active memory at that point.
            envJit.Activate();
            VerifiedBlockResult r;
            try
            {
                r = runner.RunAndVerifyOneBlock();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ERROR at block {b}: {ex.GetType().Name}: {ex.Message}");
                sw.Stop();
                Console.WriteLine($"  elapsed:  {sw.Elapsed}");
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
                Console.WriteLine($"  diverged at block: #{r.BlockIndex}, pc=0x{r.BlockStartPc:X5}, " +
                    $"instrs in block={r.InstructionCount}");
                Console.WriteLine($"  detail:            {r.Detail}");
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
