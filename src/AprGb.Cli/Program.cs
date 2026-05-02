using AprGb.Cli;
using AprGb.Cli.Cpu;
using AprGb.Cli.Diff;
using AprGb.Cli.Memory;
using AprGb.Cli.Video;

// apr-gb [--cpu=legacy|json-llvm] --rom=<path> [--cycles=N | --frames=N]
//        [--screenshot=out.ppm] [--info]

var opts = ParseArgs(args);
if (opts is null) { PrintUsage(); return 1; }

if (opts.DiffMaxSteps > 0 && opts.RomPath is not null)
{
    Console.WriteLine($"apr-gb — diff mode: legacy vs json-llvm");
    Console.WriteLine($"  ROM:        {opts.RomPath}");
    Console.WriteLine($"  max steps:  {opts.DiffMaxSteps}");
    var report = CpuDiff.Run(opts.RomPath, opts.DiffMaxSteps);
    if (report is null)
    {
        Console.WriteLine($"  no divergence in {opts.DiffMaxSteps} steps. Backends agree.");
        return 0;
    }
    Console.WriteLine(CpuDiff.Format(report));
    return 1;
}

if (opts.Bench && opts.RomPath is not null)
{
    return RunBench(opts);
}

Console.WriteLine($"apr-gb — Game Boy harness (DMG only; legacy + json-llvm backends)");
Console.WriteLine($"  ROM:        {opts.RomPath}");
Console.WriteLine($"  backend:    {opts.Backend}");
Console.WriteLine($"  cycles:     {opts.Cycles}");
Console.WriteLine($"  screenshot: {opts.ScreenshotPath ?? "(none)"}");

var rom = RomLoader.Load(opts.RomPath);
Console.WriteLine($"  loaded:     {rom.Length} bytes — {RomLoader.DescribeHeader(rom)}");

if (opts.InfoOnly) return 0;

var bus = new GbMemoryBus();
bus.LoadRom(rom);

ICpuBackend cpu = opts.Backend switch
{
    "legacy"    => new LegacyCpu(),
    "json-llvm" => new JsonCpu(),
    _           => throw new ArgumentException($"Unknown backend '{opts.Backend}'"),
};
cpu.Reset(bus);
Console.WriteLine($"  initial PC=0x{cpu.ReadReg16(GbReg16.PC):X4} SP=0x{cpu.ReadReg16(GbReg16.SP):X4}");

var consumed = cpu.RunCycles(opts.Cycles);
Console.WriteLine($"  ran {consumed} cycles. final PC=0x{cpu.ReadReg16(GbReg16.PC):X4} halted={cpu.IsHalted}");

if (bus.SerialLog.Length > 0)
{
    Console.WriteLine($"--- serial output ({bus.SerialLog.Length} bytes) ---");
    Console.WriteLine(bus.SerialLog.ToString());
    Console.WriteLine("--- end serial ---");
}

if (opts.ScreenshotPath is not null)
{
    var ppu = new GbPpu();
    ppu.RenderFrame(bus);
    Directory.CreateDirectory(Path.GetDirectoryName(opts.ScreenshotPath) ?? ".");
    if (opts.ScreenshotPath.EndsWith(".ppm", StringComparison.OrdinalIgnoreCase))
        PpmWriter.SavePpm(ppu.Framebuffer, GbPpu.Width, GbPpu.Height, opts.ScreenshotPath);
    else
        PngWriter.SavePng(ppu.Framebuffer, GbPpu.Width, GbPpu.Height, opts.ScreenshotPath);
    Console.WriteLine($"  screenshot: {opts.ScreenshotPath} ({GbPpu.Width}×{GbPpu.Height})");
}

return 0;

static Options? ParseArgs(string[] args)
{
    var opts = new Options();
    foreach (var arg in args)
    {
        if      (arg.StartsWith("--cpu="))        opts.Backend = arg.Substring("--cpu=".Length);
        else if (arg.StartsWith("--rom="))        opts.RomPath = arg.Substring("--rom=".Length);
        else if (arg.StartsWith("--cycles="))     opts.Cycles  = long.Parse(arg.Substring("--cycles=".Length));
        else if (arg.StartsWith("--frames="))     opts.Cycles  = long.Parse(arg.Substring("--frames=".Length)) * 70224;
        else if (arg.StartsWith("--screenshot=")) opts.ScreenshotPath = arg.Substring("--screenshot=".Length);
        else if (arg == "--info")                 opts.InfoOnly = true;
        else if (arg.StartsWith("--diff="))       opts.DiffMaxSteps = long.Parse(arg.Substring("--diff=".Length));
        else if (arg == "--bench")                opts.Bench = true;
        else                                      return null;
    }
    return opts.RomPath is null ? null : opts;
}

static void PrintUsage()
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  apr-gb --rom=<path.gb|.zip> [--cpu=legacy|json-llvm] [--cycles=N | --frames=N]");
    Console.Error.WriteLine("         [--screenshot=out.ppm] [--info]");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Defaults: --cpu=legacy --cycles=70224 (one frame).");
}

static int RunBench(Options opts)
{
    Console.WriteLine($"apr-gb — bench mode: legacy vs json-llvm");
    Console.WriteLine($"  ROM:        {opts.RomPath}");
    Console.WriteLine($"  cycles:     {opts.Cycles:N0}");
    Console.WriteLine();

    var rom = RomLoader.Load(opts.RomPath!);

    // Construct + Reset + warm-up step for each, then time the timed
    // window separately. Construction cost (LLVM module compile + JIT
    // engine creation for JsonCpu) is reported but excluded from MIPS.
    var (legSetupMs, legacy, busL) = SetupBackend(rom, () => new LegacyCpu());
    var (jitSetupMs, json,   busJ) = SetupBackend(rom, () => new JsonCpu());

    Console.WriteLine($"  setup time: legacy={legSetupMs:F0} ms, json-llvm={jitSetupMs:F0} ms (incl. spec compile + MCJIT)");
    Console.WriteLine();

    var legResult = TimeRun("legacy",    legacy, opts.Cycles);
    var jitResult = TimeRun("json-llvm", json,   opts.Cycles);

    Console.WriteLine();
    var ratio = jitResult.Mips / legResult.Mips;
    var label = ratio >= 1.0 ? $"{ratio:F2}x faster" : $"{1.0 / ratio:F2}x slower";
    Console.WriteLine($"  json-llvm vs legacy: {label} ({jitResult.Mips:F2} / {legResult.Mips:F2})");
    return 0;
}

static (double SetupMs, ICpuBackend Cpu, GbMemoryBus Bus) SetupBackend(byte[] rom, Func<ICpuBackend> ctor)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var bus = new GbMemoryBus(); bus.LoadRom(rom);
    var cpu = ctor();
    cpu.Reset(bus);
    sw.Stop();
    return (sw.Elapsed.TotalMilliseconds, cpu, bus);
}

static BenchResult TimeRun(string label, ICpuBackend cpu, long targetCycles)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    cpu.RunCycles(targetCycles);
    sw.Stop();
    var seconds = sw.Elapsed.TotalSeconds;
    var instr = cpu.InstructionsExecuted;
    var mips = (instr / 1_000_000.0) / seconds;
    Console.WriteLine($"  {label,-9}: {seconds,7:F3} s, {instr,15:N0} instr → {mips,8:F2} MIPS");
    return new BenchResult(seconds, instr, mips);
}

internal readonly record struct BenchResult(double WallSeconds, long Instructions, double Mips);

internal sealed class Options
{
    public string?  RomPath;
    public string   Backend = "legacy";
    public long     Cycles  = 70224;     // one DMG frame = 70224 machine cycles
    public string?  ScreenshotPath;
    public bool     InfoOnly;
    public long     DiffMaxSteps;        // when > 0, run lockstep diff harness instead
    public bool     Bench;               // when true, run both backends and report MIPS
}
