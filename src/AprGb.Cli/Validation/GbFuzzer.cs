// AprGb.Cli/Validation/GbFuzzer.cs
//
// Phase 30.18 sprint 5.7b — GB differential fuzzer. Mirrors NesFuzzer
// (src/AprNes.Cli/Validation/NesFuzzer.cs). Generates a 32KB random
// GB cart ROM per iteration with a sane reset path:
//   $0100-$0102: NOP / JP $0150
//   $0150+:     random bytes (the fuzzed code)
//
// The interrupt vectors at $0040/$0048/$0050/$0058/$0060 are filled with
// RETI (0xD9) so any IRQ-related divergence is bounded.

using AprCpu.Core.Validation;
using AprGb.Cli.Cpu;
using AprGb.Cli.Memory;

namespace AprGb.Cli.Validation;

public static class GbFuzzer
{
    public static int Run(int iterations, int blocksPerIter, int? seed = null)
    {
        var rngSeed = seed ?? Environment.TickCount;
        // Phase 30.18 — match GbVerifyBlocks: disable inline RAM fast-path
        // so all WRAM/HRAM writes go through MemWrite8 extern (which calls
        // ActiveTraceSink for the verifier). The Lr35902Emitters fall-through
        // now properly invokes EmitWriteByteWithSync to preserve sync-exit
        // semantics, so this env var is safe.
        Environment.SetEnvironmentVariable("APR_GB_NO_INLINE_RAM", "1");
        Console.WriteLine("apr-gb fuzz (random cart ROM → per-block JIT-vs-interp diff)");
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
            var rom = GenerateRandomCart(iterRng);

            var (cpuJit,    busJit)    = BuildEnv(rom);
            var (cpuInterp, busInterp) = BuildEnv(rom);

            var jitStepper    = new GbSteppableCpu("JIT",    cpuJit,    busJit);
            var interpStepper = new GbSteppableCpu("INTERP", cpuInterp, busInterp);
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
                    // Skip blocks where pre-PC is outside cart ROM (avoid
                    // executing from VRAM/WRAM/IO open-bus, analogous to
                    // NesFuzzer's $8000+ constraint).
                    ushort curPc = cpuJit.ReadReg16(GbReg16.PC);
                    if (curPc >= 0x8000) break;
                    var r = runner.RunAndVerifyOneBlock();
                    iterBlocks++;
                    iterInstrs += Math.Max(r.InstructionCount, 0);
                    if (r.Status != VerifiedBlockStatus.Ok)
                    {
                        divergences++;
                        Console.WriteLine($"  iter {iter}/seed {iterSeed}: divergence at block #{r.BlockIndex}, pc=0x{r.BlockStartPc:X4}");
                        Console.WriteLine($"    status: {r.Status}");
                        Console.WriteLine($"    detail: {r.Detail}");
                        if (r.CpuStatePre is not null)
                            Console.WriteLine($"    pre:    {r.CpuStatePre}");
                        if (r.CpuStateA is not null)
                        {
                            Console.WriteLine($"    JIT:    {r.CpuStateA}");
                            Console.WriteLine($"    INTERP: {r.CpuStateB}");
                        }
                        Console.WriteLine($"    cart bytes @ pc=0x{r.BlockStartPc:X4}..+32:");
                        var hex = new System.Text.StringBuilder("      ");
                        for (int i = 0; i < 32; i++)
                        {
                            var bb = busJit.ReadByte((ushort)((uint)r.BlockStartPc + (uint)i));
                            hex.Append($"{bb:X2} ");
                            if ((i & 7) == 7) hex.Append(" ");
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

            if ((iter + 1) % 100 == 0 || iter + 1 == iterations)
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

    private static byte[] GenerateRandomCart(Random rng)
    {
        var rom = new byte[0x8000];   // 32KB single bank
        rng.NextBytes(rom);
        // Entry @ $0100: NOP; JP $0150 (= 0x00 0xC3 0x50 0x01)
        rom[0x0100] = 0x00;          // NOP
        rom[0x0101] = 0xC3;          // JP nn
        rom[0x0102] = 0x50;
        rom[0x0103] = 0x01;
        // Nintendo logo bytes ($0104-$0133) are checked by real BIOS, but
        // we don't run a BIOS here so they can stay random.
        // IRQ vectors $0040/$0048/$0050/$0058/$0060: RETI to bound any IRQ.
        foreach (var addr in new[] { 0x40, 0x48, 0x50, 0x58, 0x60 }) rom[addr] = 0xD9;
        return rom;
    }

    private static (JsonCpu Cpu, GbMemoryBus Bus) BuildEnv(byte[] rom)
    {
        var bus = new GbMemoryBus();
        bus.LoadRom(rom);
        var cpu = new JsonCpu(enableBlockJit: true);
        cpu.Reset(bus);
        return (cpu, bus);
    }
}
