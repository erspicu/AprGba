// AprGba.Cli/Validation/GbaVerifyBlocks.cs
//
// Phase 30.16 sprint 5.6b — CLI entry point for `apr-gba --verify-blocks`.
// Wires the generic VerifiedBlockJitRunner against TWO independent GBA
// envs (each = HostRuntime + CpuExecutor + GbaMemoryBus). Each env has
// its own JIT module, own bus, own memory regions; only ROM bytes are
// shared (read-only).

using AprCpu.Core.Compilation;
using AprCpu.Core.Decoder;
using AprCpu.Core.IR;
using AprCpu.Core.JsonSpec;
using AprCpu.Core.Runtime;
using AprCpu.Core.Runtime.Gba;
using AprCpu.Core.Validation;

namespace AprGba.Cli.Validation;

public static class GbaVerifyBlocks
{
    public static int Run(string romPath, string? biosPath, long maxBlocks)
    {
        Console.WriteLine("apr-gba verify-blocks (per-block JIT-vs-interp diff)");
        Console.WriteLine($"  ROM:    {romPath}");
        Console.WriteLine($"  BIOS:   {biosPath ?? "(none — minimal vector stubs)"}");
        Console.WriteLine($"  blocks: up to {maxBlocks:N0}");

        var romBytes  = File.ReadAllBytes(romPath);
        var biosBytes = biosPath is not null ? File.ReadAllBytes(biosPath) : null;

        var (cpuJit,    busJit)    = BuildEnv(romBytes, biosBytes);
        var (cpuInterp, busInterp) = BuildEnv(romBytes, biosBytes);

        var jitStepper    = new GbaSteppableCpu("JIT",    cpuJit,    busJit);
        var interpStepper = new GbaSteppableCpu("INTERP", cpuInterp, busInterp);

        // Per-stepper env activation switches the global MemoryBusBindings
        // singletons so JIT-emitted Read/Write trampolines hit the right
        // env's memory.
        jitStepper.OnBeforeStep    = () => MemoryBusBindings.SetActive(busJit);
        interpStepper.OnBeforeStep = () => MemoryBusBindings.SetActive(busInterp);

        MemoryBusBindings.SetActive(busJit);
        var runner = new VerifiedBlockJitRunner(jitStepper, interpStepper);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        long verifiedBlocks = 0;
        long verifiedInstrs = 0;

        for (long b = 0; b < maxBlocks; b++)
        {
            MemoryBusBindings.SetActive(busJit);
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
                Console.WriteLine($"  diverged at block: #{r.BlockIndex}, pc=0x{r.BlockStartPc:X8}, " +
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

    private static (CpuExecutor Cpu, GbaMemoryBus Bus) BuildEnv(byte[] romBytes, byte[]? biosBytes)
    {
        var bus = new GbaMemoryBus();
        if (biosBytes is not null) bus.LoadBios(biosBytes);
        else                       bus.InstallMinimalBiosStubs();
        bus.LoadRom(romBytes);

        // Build the JIT runtime + bindings for this env. Same pattern as
        // Program.cs BootCpu, replicated locally so each env has its own
        // HostRuntime + CpuExecutor.
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
