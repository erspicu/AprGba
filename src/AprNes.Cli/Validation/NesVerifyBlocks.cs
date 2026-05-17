// AprNes.Cli/Validation/NesVerifyBlocks.cs
//
// Phase 30.16 sprint 5.6 — CLI entry point for `apr-nes --verify-blocks`.
// Wires the generic VerifiedBlockJitRunner against TWO independent NES
// envs (each = NesJsonCpu + NesMemoryBus with same ROM loaded).

using AprCpu.Core.Validation;
using AprNes.Cli.Cpu;
using AprNes.Cli.Memory;

namespace AprNes.Cli.Validation;

public static class NesVerifyBlocks
{
    /// <summary>
    /// Run the per-block JIT-vs-interp verifier on a NES ROM. Returns:
    ///   0 = NoDiff up to maxBlocks
    ///   5 = divergence reported
    ///   2 = exception during verification
    /// </summary>
    public static int Run(string romPath, long maxBlocks)
    {
        Console.WriteLine("apr-nes verify-blocks (per-block JIT-vs-interp diff)");
        Console.WriteLine($"  ROM:    {romPath}");
        Console.WriteLine($"  blocks: up to {maxBlocks:N0}");

        var (cpuJit,    busJit)    = BuildEnv(romPath);
        var (cpuInterp, busInterp) = BuildEnv(romPath);

        var jitStepper    = new NesSteppableCpu("JIT",    cpuJit,    busJit);
        var interpStepper = new NesSteppableCpu("INTERP", cpuInterp, busInterp);

        // Per-stepper env activation (sprint 5.4d analogue).
        jitStepper.OnBeforeStep    = () => cpuJit.SetActiveForLockstep();
        interpStepper.OnBeforeStep = () => cpuInterp.SetActiveForLockstep();

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

    private static (NesJsonCpu Cpu, NesMemoryBus Bus) BuildEnv(string romPath)
    {
        // Same pattern as Program.cs --diff path: parse iNES header, pick
        // mapper, attach to a fresh bus, construct JsonCpu with block-JIT
        // enabled (interp side calls StepOnePerInstr via the verifier's
        // StepOneArchitecturalInstruction).
        var rom = AprNes.Cli.NesRomLoader.Load(romPath);
        IMapper mapper = rom.MapperId switch
        {
            0 => new Mapper000(),
            1 => new Mapper001(),
            _ => throw new NotSupportedException(
                $"NesVerifyBlocks: mapper {rom.MapperId} not supported (have 0=NROM, 1=MMC1)")
        };
        mapper.Reset(rom.PrgRom, rom.ChrRom);
        var bus = new NesMemoryBus();
        bus.Reset(mapper);
        var cpu = new NesJsonCpu(bus, enableBlockJit: true);
        cpu.Reset();
        return (cpu, bus);
    }
}
