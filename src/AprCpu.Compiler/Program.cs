using AprCpu.Core;
using AprCpu.Core.Compilation;
using AprCpu.Core.Decoder;
using AprCpu.Core.IR;
using AprCpu.Core.JsonSpec;
using AprCpu.Core.Runtime;
using AprCpu.Core.Runtime.Gba;

// aprcpu - AprCpu CLI
//
// Modes:
//   aprcpu --spec <cpu.json> --output <out.ll>
//       Load a CPU spec, compile every instruction in every set into one
//       LLVM module, dump it as textual IR.
//
//   aprcpu --spike --output <out.ll>
//       Phase-0 spike: emit a trivial int add(int,int) function and JIT-run
//       it. Useful for verifying the LLVM toolchain is wired up.
//
//   aprcpu --jit-only
//       Phase-0 spike with no IR file output, JIT-runs add(3,4).
//
//   aprcpu --help

string? specPath   = null;
string? outputPath = null;
string? benchRom   = null;
long    benchSteps = 0;
bool spike   = false;
bool jitOnly = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--spec" when i + 1 < args.Length:
            specPath = args[++i];
            break;
        case "--output" when i + 1 < args.Length:
            outputPath = args[++i];
            break;
        case "--spike":
            spike = true;
            break;
        case "--jit-only":
            spike = true;
            jitOnly = true;
            break;
        case "--bench-rom" when i + 1 < args.Length:
            benchRom = args[++i];
            break;
        case "--steps" when i + 1 < args.Length:
            benchSteps = long.Parse(args[++i]);
            break;
        case "-h":
        case "--help":
            PrintUsage();
            return 0;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            PrintUsage();
            return 1;
    }
}

if (benchRom is not null)
{
    if (benchSteps <= 0) benchSteps = 50_000_000;
    return RunBench(benchRom, benchSteps);
}

if (spike)
{
    if (!jitOnly)
    {
        if (outputPath is null)
        {
            Console.Error.WriteLine("--spike requires --output <path> (or use --jit-only).");
            return 1;
        }
        EnsureDirForFile(outputPath);
        JitSpike.DumpAddIRToFile(outputPath);
        Console.WriteLine($"[aprcpu] wrote spike IR -> {Path.GetFullPath(outputPath)}");
    }
    int result = JitSpike.JitAndRunAdd(3, 4);
    Console.WriteLine($"[aprcpu] JIT add(3, 4) = {result}");
    return result == 7 ? 0 : 2;
}

if (specPath is null || outputPath is null)
{
    PrintUsage();
    return 1;
}

if (!File.Exists(specPath))
{
    Console.Error.WriteLine($"[aprcpu] spec not found: {specPath}");
    return 1;
}

EnsureDirForFile(outputPath);
Console.WriteLine($"[aprcpu] compiling {Path.GetFileName(specPath)} ...");

var compileResult = SpecCompiler.CompileToFile(specPath, outputPath);

Console.WriteLine($"[aprcpu] emitted {compileResult.Functions.Count} function(s):");
foreach (var key in compileResult.Functions.Keys.OrderBy(k => k))
{
    Console.WriteLine($"          - {key}");
}

if (compileResult.Diagnostics.Count > 0)
{
    Console.Error.WriteLine($"[aprcpu] diagnostics ({compileResult.Diagnostics.Count}):");
    foreach (var diag in compileResult.Diagnostics)
        Console.Error.WriteLine($"  {diag}");
    Console.Error.WriteLine($"[aprcpu] WROTE WITH ERRORS -> {Path.GetFullPath(outputPath)}");
    return 3;
}

Console.WriteLine($"[aprcpu] wrote -> {Path.GetFullPath(outputPath)}");
return 0;

static void PrintUsage()
{
    Console.WriteLine("aprcpu - AprCpu CLI");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  aprcpu --spec <cpu.json> --output <out.ll>");
    Console.WriteLine("        Compile a CPU spec to LLVM IR.");
    Console.WriteLine("  aprcpu --spike --output <out.ll>");
    Console.WriteLine("        Run the Phase-0 add(int,int) spike, write its IR, JIT-run it.");
    Console.WriteLine("  aprcpu --jit-only");
    Console.WriteLine("        Phase-0 spike, JIT only (no IR file).");
}

static void EnsureDirForFile(string path)
{
    var dir = Path.GetDirectoryName(Path.GetFullPath(path));
    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
}

/// <summary>
/// Bench mode: load arm.gba / thumb.gba etc. through the JSON-LLVM
/// JIT (the only ARM7TDMI backend; there's no hand-written legacy ARM
/// to compare against, unlike the GB side). Reports MIPS so the
/// number can be cross-referenced against apr-gb's --bench output.
///
/// Boot logic mirrors AprCpu.Tests/GbaRomExecutionTests.BootGba:
/// minimal BIOS stubs, MCJIT, three extern bindings (memory bus +
/// bank swap + user-mode reg), CPSR=System, SP=top of IWRAM,
/// PC=ROM entry.
/// </summary>
static int RunBench(string romPath, long steps)
{
    var cpuJson = LocateArm7tdmiSpec();
    if (cpuJson is null)
    {
        Console.Error.WriteLine("[aprcpu] cannot find spec/arm7tdmi/cpu.json. Run from repo root.");
        return 1;
    }
    var rom = File.ReadAllBytes(romPath);

    Console.WriteLine($"aprcpu — bench mode: arm7tdmi json-llvm");
    Console.WriteLine($"  ROM:        {romPath} ({rom.Length:N0} bytes)");
    Console.WriteLine($"  steps:      {steps:N0}");

    var setupSw = System.Diagnostics.Stopwatch.StartNew();

    var bus = new GbaMemoryBus();
    bus.InstallMinimalBiosStubs();
    bus.LoadRom(rom);

    var compileResult = SpecCompiler.Compile(cpuJson);
    var loaded = SpecLoader.LoadCpuSpec(cpuJson);
    var layout = new CpuStateLayout(
        compileResult.Module.Context,
        loaded.Cpu.RegisterFile,
        loaded.Cpu.ProcessorModes,
        loaded.Cpu.ExceptionVectors);

    var rt = HostRuntime.Build(compileResult.Module, layout);
    var swapHandler  = new Arm7tdmiBankSwapHandler(rt);
    using var busBinding   = MemoryBusBindings.Install(rt, bus);
    using var swapBinding  = BankSwapBindings.Install(rt, swapHandler);
    using var userBinding  = UserModeRegBindings.Install(rt, swapHandler);
    rt.Compile();

    var setsByName = new Dictionary<string, (InstructionSetSpec, DecoderTable)>(StringComparer.Ordinal);
    foreach (var (name, set) in loaded.InstructionSets)
        setsByName[name] = (set, new DecoderTable(set));
    var dispatch = loaded.Cpu.InstructionSetDispatch
        ?? throw new InvalidOperationException("CPU spec missing instruction_set_dispatch");
    var exec = new CpuExecutor(rt, setsByName, dispatch, bus);

    exec.WriteStatus("CPSR", 0x1Fu);
    exec.WriteGpr(13, 0x03007F00u);
    exec.Pc = 0x08000000u;

    setupSw.Stop();
    Console.WriteLine($"  setup time: {setupSw.Elapsed.TotalMilliseconds:F0} ms (incl. spec compile + MCJIT)");
    Console.WriteLine();

    // Warm one step to amortise first-call JIT linking.
    exec.Step();

    var runSw = System.Diagnostics.Stopwatch.StartNew();
    long executed = 1;
    for (long i = 1; i < steps; i++)
    {
        try { exec.Step(); executed++; }
        catch (Exception ex)
        {
            // Some ROMs branch to undefined opcodes after halt loop;
            // that's fine, just report what we got.
            Console.Error.WriteLine($"  (stopped at step {i}: {ex.Message.Substring(0, Math.Min(80, ex.Message.Length))})");
            break;
        }
    }
    runSw.Stop();

    var seconds = runSw.Elapsed.TotalSeconds;
    var mips = (executed / 1_000_000.0) / seconds;
    Console.WriteLine($"  json-llvm:  {seconds,7:F3} s, {executed,15:N0} instr → {mips,8:F2} MIPS");

    rt.Dispose();
    return 0;
}

static string? LocateArm7tdmiSpec()
{
    var dir = AppContext.BaseDirectory;
    for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
    {
        var probe = Path.Combine(d.FullName, "spec", "arm7tdmi", "cpu.json");
        if (File.Exists(probe)) return probe;
    }
    var cwd = Path.Combine(Environment.CurrentDirectory, "spec", "arm7tdmi", "cpu.json");
    return File.Exists(cwd) ? cwd : null;
}
