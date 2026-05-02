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

internal sealed class Options
{
    public string?  RomPath;
    public string   Backend = "legacy";
    public long     Cycles  = 70224;     // one DMG frame = 70224 machine cycles
    public string?  ScreenshotPath;
    public bool     InfoOnly;
    public long     DiffMaxSteps;        // when > 0, run lockstep diff harness instead
}
