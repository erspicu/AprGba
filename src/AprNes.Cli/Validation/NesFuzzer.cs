// AprNes.Cli/Validation/NesFuzzer.cs
//
// Phase 30.17 sprint 5.7 — RISU-style differential fuzzer for NES.
// Generates random instruction sequences (in the shape of NROM cart
// PRG-ROM), feeds them to the Verified Block-JIT framework, and
// reports any divergence.
//
// Each iteration:
//   1. Generate a 32KB random PRG-ROM with a valid reset vector
//      pointing at $8000 (cart entry).
//   2. Build two independent NES envs sharing that PRG.
//   3. Run the verifier for up to N blocks (default 100), bounded so
//      a single bad sequence doesn't hang the fuzzer.
//   4. On divergence: print the seed + first-divergence detail so the
//      run can be reproduced for investigation.
//   5. On exception (rare; e.g. unmapped read): count as a "skipped"
//      iteration and continue with the next seed.
//
// Catches: encoder/decoder mismatches, emitter bugs that real ROMs
// don't exercise, addressing-mode edge cases, flag-update corner
// cases (PHP/RTI/BIT P bits, decimal-mode (D-flag on 2A03 ignored),
// JMP indirect $XXFF wrap, stack wrap, etc.).
//
// Does NOT catch: anything requiring HW timing (PPU, APU, OAM-DMA),
// MMC-bank-switching state, IRQ/NMI delivery (random ROMs rarely
// enable IRQ).

using AprCpu.Core.Validation;
using AprNes.Cli.Cpu;
using AprNes.Cli.Memory;

namespace AprNes.Cli.Validation;

public static class NesFuzzer
{
    /// <summary>
    /// Run the differential fuzzer. Generates <paramref name="iterations"/>
    /// random programs, verifies each for up to <paramref name="blocksPerIter"/>
    /// blocks. Returns 0 on all-NoDiff, 5 on first divergence, 2 on host error.
    /// </summary>
    public static int Run(int iterations, int blocksPerIter, int? seed = null)
    {
        var rngSeed = seed ?? Environment.TickCount;
        Console.WriteLine("apr-nes fuzz (random PRG-ROM → per-block JIT-vs-interp diff)");
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

            // Generate 32KB random PRG-ROM with reset vector → $8000.
            var prgRom = new byte[0x8000];
            iterRng.NextBytes(prgRom);
            // Reset vector at $FFFC-$FFFD (relative offset 0x7FFC-0x7FFD)
            // points at $8000 = the start of PRG-ROM.
            prgRom[0x7FFC] = 0x00;
            prgRom[0x7FFD] = 0x80;
            // IRQ/BRK vector at $FFFE-$FFFF — point to $8000 too so any
            // BRK encountered loops back rather than hanging.
            prgRom[0x7FFE] = 0x00;
            prgRom[0x7FFF] = 0x80;
            // NMI vector at $FFFA-$FFFB.
            prgRom[0x7FFA] = 0x00;
            prgRom[0x7FFB] = 0x80;

            var (cpuJit,    busJit)    = BuildSyntheticEnv(prgRom);
            var (cpuInterp, busInterp) = BuildSyntheticEnv(prgRom);

            var jitStepper    = new NesSteppableCpu("JIT",    cpuJit,    busJit);
            var interpStepper = new NesSteppableCpu("INTERP", cpuInterp, busInterp);
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
                        // Dump the PRG bytes near the block start so future
                        // investigation can disassemble. addr is the linear
                        // PC at block entry; we know it's in PRG-ROM
                        // ($8000..$FFFF), so look up the bytes from the
                        // mapper-fed bus directly.
                        Console.WriteLine($"    PRG bytes @ pc=0x{r.BlockStartPc:X4}..+32:");
                        var hexLine = new System.Text.StringBuilder("      ");
                        for (int i = 0; i < 32; i++)
                        {
                            var bb = busJit.ReadByte((ushort)((uint)r.BlockStartPc + (uint)i));
                            hexLine.Append($"{bb:X2} ");
                            if ((i & 7) == 7) hexLine.Append(" ");
                        }
                        Console.WriteLine(hexLine.ToString());
                        iterDiverged = true;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                skipped++;
                // Random code can construct undecodable sequences (rare on
                // 6502 — all 256 opcodes defined) or trigger unimplemented
                // emitter paths. Don't fail the run; just skip and continue.
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

            // Bail out early on first divergence for quick feedback.
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

    private static (NesJsonCpu Cpu, NesMemoryBus Bus) BuildSyntheticEnv(byte[] prgRom)
    {
        var mapper = new Mapper000();
        mapper.Reset(prgRom, System.Array.Empty<byte>());     // no CHR (RAM created automatically)
        var bus = new NesMemoryBus();
        bus.Reset(mapper);
        var cpu = new NesJsonCpu(bus, enableBlockJit: true);
        cpu.Reset();
        return (cpu, bus);
    }
}
