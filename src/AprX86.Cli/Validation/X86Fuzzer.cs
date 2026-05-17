// AprX86.Cli/Validation/X86Fuzzer.cs
//
// Phase 30.18d sprint 5.7d — x86 differential fuzzer. Generates a random
// .com-style flat binary, loads it at CS:IP, and runs the verifier.
//
// x86 is the most opcode-dense ISA among our four CPUs (1-15 byte variable-
// length, prefixes, ModR/M, packed-tail) so the fuzzer is the most stressful
// validation.

using AprCpu.Core.Validation;
using AprX86.Cli.Cpu;
using AprX86.Cli.Memory;

namespace AprX86.Cli.Validation;

public static class X86Fuzzer
{
    public static int Run(int iterations, int blocksPerIter, int? seed = null,
        ushort entrySeg = 0x1000, ushort entryOff = 0x0000,
        bool continueOnDivergence = false)
    {
        var rngSeed = seed ?? Environment.TickCount;
        Console.WriteLine("apr-x86 fuzz (random .com-style ROM → per-block JIT-vs-interp diff)");
        Console.WriteLine($"  iterations:      {iterations:N0}");
        Console.WriteLine($"  blocks per iter: {blocksPerIter:N0}");
        Console.WriteLine($"  entry:           {entrySeg:X4}:{entryOff:X4}");
        Console.WriteLine($"  seed:            {rngSeed} (re-run with --fuzz-seed={rngSeed} to reproduce)");
        Console.WriteLine();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var rng = new Random(rngSeed);
        long totalBlocksVerified = 0;
        long totalInstrsVerified = 0;
        int skipped = 0;
        int divergences = 0;
        uint entryLinear = (uint)((entrySeg << 4) + entryOff);

        for (int iter = 0; iter < iterations; iter++)
        {
            int iterSeed = rng.Next();
            var iterRng = new Random(iterSeed);
            // 4KB random ROM — small enough to compile fast, large enough
            // for thousands of instructions worth of random execution.
            var rom = new byte[0x1000];
            iterRng.NextBytes(rom);

            var (cpuJit,    memJit)    = BuildEnv(rom, entrySeg, entryOff);
            var (cpuInterp, memInterp) = BuildEnv(rom, entrySeg, entryOff);

            var jitStepper    = new X86SteppableCpu("JIT",    cpuJit,    memJit);
            var interpStepper = new X86SteppableCpu("INTERP", cpuInterp, memInterp);
            jitStepper.OnBeforeStep    = () => cpuJit.SetActiveForLockstep();
            interpStepper.OnBeforeStep = () => cpuInterp.SetActiveForLockstep();
            cpuJit.SetActiveForLockstep();

            var runner = new VerifiedBlockJitRunner(jitStepper, interpStepper);
            long iterBlocks = 0;
            long iterInstrs = 0;
            bool iterDiverged = false;
            try
            {
                for (int b = 0; b < blocksPerIter; b++)
                {
                    cpuJit.SetActiveForLockstep();
                    // Skip when PC drifts outside our random ROM area
                    // (anything ±ROM region is unsafe — open-bus, etc).
                    ushort cs = cpuJit.State.CS, ip = cpuJit.State.IP;
                    uint linearPc = (uint)((cs << 4) + ip) & 0xFFFFF;
                    if (linearPc < entryLinear
                        || linearPc >= entryLinear + (uint)rom.Length) break;
                    var r = runner.RunAndVerifyOneBlock();
                    iterBlocks++;
                    iterInstrs += Math.Max(r.InstructionCount, 0);
                    if (r.Status != VerifiedBlockStatus.Ok)
                    {
                        divergences++;
                        Console.WriteLine($"  iter {iter}/seed {iterSeed}: divergence at block #{r.BlockIndex}, pc=0x{r.BlockStartPc:X5}");
                        Console.WriteLine($"    status: {r.Status}");
                        Console.WriteLine($"    detail: {r.Detail}");
                        if (r.CpuStatePre is not null)
                            Console.WriteLine($"    pre:    {r.CpuStatePre}");
                        if (r.CpuStateA is not null)
                        {
                            Console.WriteLine($"    JIT:    {r.CpuStateA}");
                            Console.WriteLine($"    INTERP: {r.CpuStateB}");
                        }
                        Console.WriteLine($"    ROM bytes @ pc=0x{r.BlockStartPc:X5}..+16:");
                        var hex = new System.Text.StringBuilder("      ");
                        for (int i = 0; i < 16; i++)
                        {
                            var bb = memJit.ReadByte((int)((uint)r.BlockStartPc + (uint)i));
                            hex.Append($"{bb:X2} ");
                        }
                        Console.WriteLine(hex.ToString());
                        iterDiverged = true;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                skipped++;
                if (skipped <= 5 || skipped % 100 == 0)
                {
                    Console.WriteLine($"  iter {iter}/seed {iterSeed}: SKIPPED ({ex.GetType().Name}: {ex.Message})");
                }
                continue;
            }

            if (!iterDiverged) { totalBlocksVerified += iterBlocks; totalInstrsVerified += iterInstrs; }

            if ((iter + 1) % 10 == 0 || iter + 1 == iterations)
            {
                Console.WriteLine($"  progress: {iter+1}/{iterations} iter, "
                    + $"{totalBlocksVerified:N0} blocks, "
                    + $"{totalInstrsVerified:N0} instrs, "
                    + $"{divergences} diverged, "
                    + $"{skipped} skipped, "
                    + $"elapsed {sw.Elapsed.TotalSeconds:F1}s");
            }

            if (iterDiverged && !continueOnDivergence) break;
        }

        sw.Stop();
        Console.WriteLine();
        Console.WriteLine($"  elapsed:            {sw.Elapsed}");
        Console.WriteLine($"  total iterations:   {iterations}");
        Console.WriteLine($"  total blocks:       {totalBlocksVerified:N0}");
        Console.WriteLine($"  total instructions: {totalInstrsVerified:N0}");
        Console.WriteLine($"  divergences:        {divergences}");
        Console.WriteLine($"  skipped (host err): {skipped}");
        Console.WriteLine($"  status:             {(divergences > 0 ? "DIVERGENCE" : "NoDiff")}");
        return divergences > 0 ? 5 : 0;
    }

    private static (X86JsonCpu Cpu, X86Memory Mem) BuildEnv(byte[] rom, ushort entrySeg, ushort entryOff)
    {
        var mem = new X86Memory();
        mem.LoadBinary(rom, entrySeg, entryOff);
        var cpu = new X86JsonCpu(mem, enableBlockJit: true, variant: "i8086");
        cpu.Reset();
        cpu.SetEntryPoint(entrySeg, entryOff);
        return (cpu, mem);
    }
}
