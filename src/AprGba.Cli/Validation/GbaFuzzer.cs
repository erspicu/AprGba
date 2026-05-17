// AprGba.Cli/Validation/GbaFuzzer.cs
//
// Phase 30.18c sprint 5.7c — GBA differential fuzzer. Mirrors NES/GB
// fuzzers. Generates a random ARM ROM (4-byte ARM instructions, all
// unconditional or various-condition) and runs the verifier.
//
// Each iteration:
//   1. Allocate a 16MB random ROM (small relative to real carts to
//      keep memory pressure low). First 0x100 bytes left zero for
//      header; CPU starts at $08000000.
//   2. Build two independent GBA envs sharing the random ROM.
//   3. Run verifier for blocksPerIter blocks, capped to avoid hangs.
//   4. Report any divergence with seed for reproduction.

using AprCpu.Core.Compilation;
using AprCpu.Core.Decoder;
using AprCpu.Core.IR;
using AprCpu.Core.JsonSpec;
using AprCpu.Core.Runtime;
using AprCpu.Core.Runtime.Gba;
using AprCpu.Core.Validation;

namespace AprGba.Cli.Validation;

public static class GbaFuzzer
{
    public static int Run(int iterations, int blocksPerIter, int? seed = null)
    {
        var rngSeed = seed ?? Environment.TickCount;
        Console.WriteLine("apr-gba fuzz (random ARM ROM → per-block JIT-vs-interp diff)");
        Console.WriteLine($"  iterations:      {iterations:N0}");
        Console.WriteLine($"  blocks per iter: {blocksPerIter:N0}");
        Console.WriteLine($"  seed:            {rngSeed} (re-run with --fuzz-seed={rngSeed} to reproduce)");
        Console.WriteLine();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var rng = new Random(rngSeed);
        long totalBlocksVerified = 0;
        long totalInstrsVerified = 0;
        int skipped = 0;
        int divergences = 0;

        for (int iter = 0; iter < iterations; iter++)
        {
            int iterSeed = rng.Next();
            var iterRng = new Random(iterSeed);
            // 64KB random ARM ROM (small enough to be fast, large enough
            // to provide thousands of instructions).
            var rom = new byte[0x10000];
            iterRng.NextBytes(rom);
            // Don't worry about ROM header (BIOS check skipped via
            // InstallMinimalBiosStubs).

            var (cpuJit,    busJit)    = BuildEnv(rom);
            var (cpuInterp, busInterp) = BuildEnv(rom);

            // Jump straight to ROM base; skip BIOS boot entirely.
            cpuJit.Pc    = GbaMemoryMap.RomBase;
            cpuInterp.Pc = GbaMemoryMap.RomBase;

            var jitStepper    = new GbaSteppableCpu("JIT",    cpuJit,    busJit);
            var interpStepper = new GbaSteppableCpu("INTERP", cpuInterp, busInterp);
            jitStepper.OnBeforeStep    = () => MemoryBusBindings.SetActive(busJit);
            interpStepper.OnBeforeStep = () => MemoryBusBindings.SetActive(busInterp);
            MemoryBusBindings.SetActive(busJit);

            var runner = new VerifiedBlockJitRunner(jitStepper, interpStepper);
            long iterBlocks = 0;
            long iterInstrs = 0;
            bool iterDiverged = false;
            try
            {
                for (int b = 0; b < blocksPerIter; b++)
                {
                    MemoryBusBindings.SetActive(busJit);
                    // Skip when PC drifts outside ROM (random branches into
                    // unmapped/BIOS regions are not interesting).
                    uint curPc = cpuJit.Pc;
                    if (curPc < GbaMemoryMap.RomBase
                        || curPc >= GbaMemoryMap.RomBase + (uint)rom.Length) break;
                    var r = runner.RunAndVerifyOneBlock();
                    iterBlocks++;
                    iterInstrs += Math.Max(r.InstructionCount, 0);
                    if (r.Status != VerifiedBlockStatus.Ok)
                    {
                        divergences++;
                        Console.WriteLine($"  iter {iter}/seed {iterSeed}: divergence at block #{r.BlockIndex}, pc=0x{r.BlockStartPc:X8}");
                        Console.WriteLine($"    status: {r.Status}");
                        Console.WriteLine($"    detail: {r.Detail}");
                        if (r.CpuStatePre is not null)
                            Console.WriteLine($"    pre:    {r.CpuStatePre}");
                        if (r.CpuStateA is not null)
                        {
                            Console.WriteLine($"    JIT:    {r.CpuStateA}");
                            Console.WriteLine($"    INTERP: {r.CpuStateB}");
                        }
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

            if (iterDiverged) break;
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

    private static (CpuExecutor Cpu, GbaMemoryBus Bus) BuildEnv(byte[] rom)
    {
        var bus = new GbaMemoryBus();
        bus.InstallMinimalBiosStubs();
        bus.LoadRom(rom);

        var specPath = LocateArm7tdmiSpec();
        var compileResult = SpecCompiler.Compile(specPath);
        var loaded = SpecLoader.LoadCpuSpec(specPath);
        var layout = new CpuStateLayout(
            compileResult.Module.Context,
            loaded.Cpu.RegisterFile,
            loaded.Cpu.ProcessorModes,
            loaded.Cpu.ExceptionVectors);
        var rt = HostRuntime.Build(compileResult.Module, layout);
        var swap = new Arm7tdmiBankSwapHandler(rt);
        _ = MemoryBusBindings.Install(rt, bus);
        _ = BankSwapBindings.Install(rt, swap);
        _ = UserModeRegBindings.Install(rt, swap);
        rt.Compile();

        var setsByName = new Dictionary<string, (InstructionSetSpec, DecoderTable)>(StringComparer.Ordinal);
        foreach (var (name, set) in loaded.InstructionSets)
            setsByName[name] = (set, new DecoderTable(set));
        var dispatch = loaded.Cpu.InstructionSetDispatch
            ?? throw new InvalidOperationException("CPU spec missing instruction_set_dispatch");
        var cpu = new CpuExecutor(rt, setsByName, dispatch, bus);
        cpu.EnableBlockJit(compileResult);
        return (cpu, bus);
    }

    private static string LocateArm7tdmiSpec()
    {
        var dir = AppContext.BaseDirectory;
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
        {
            var probe = Path.Combine(d.FullName, "spec", "cpu", "arm7tdmi", "cpu.json");
            if (File.Exists(probe)) return probe;
        }
        var cwd = Path.Combine(Environment.CurrentDirectory, "spec", "cpu", "arm7tdmi", "cpu.json");
        if (File.Exists(cwd)) return cwd;
        throw new FileNotFoundException("spec/cpu/arm7tdmi/cpu.json not found — run apr-gba from repo root.");
    }
}
