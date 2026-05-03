using AprGb.Cli;
using AprGb.Cli.Cpu;
using AprGb.Cli.Diff;
using AprGb.Cli.Memory;
using AprGb.Cli.Video;

// apr-gb --rom=<path> [--bios=<dmg.bin>] [--cpu=legacy|json-llvm]
//        [--cycles=N | --frames=N | --seconds=N]
//        [--screenshot=out.png] [--info]
//
// Run-length units (later wins; pick one):
//   --cycles=N    raw t-cycles (DMG: 70224 = 1 frame)
//   --frames=N    DMG frames
//   --seconds=N   DMG-emulated wall-time seconds; converted via the
//                 4,194,304 t-cycles/sec DMG clock.

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
Console.WriteLine($"  BIOS:       {opts.BiosPath ?? "(none — booting straight to cart entry @ 0x0100)"}");
Console.WriteLine($"  backend:    {opts.Backend}");
{
    var emFrames = opts.Cycles / (double)GbTiming.TCyclesPerFrame;
    var emSecs   = opts.Cycles / (double)GbTiming.CpuClockHz;
    Console.WriteLine($"  budget:     {opts.Cycles:N0} t-cycles ≈ {emFrames:F1} frames ≈ {emSecs:F3} DMG seconds");
}
Console.WriteLine($"  screenshot: {opts.ScreenshotPath ?? "(none)"}");

var rom = RomLoader.Load(opts.RomPath);
Console.WriteLine($"  loaded:     {rom.Length} bytes — {RomLoader.DescribeHeader(rom)}");

if (opts.InfoOnly) return 0;

var bus = new GbMemoryBus();
if (opts.BiosPath is not null)
{
    var biosBytes = File.ReadAllBytes(opts.BiosPath);
    bus.LoadBios(biosBytes);
    Console.WriteLine($"  BIOS loaded:  {biosBytes.Length} bytes (mapped over 0x0000..0x00FF until cart writes 0xFF50)");
    // Real DMG PPU clock — required for the BIOS animation to take its
    // full ~2.5s instead of completing in zero LY-polling iterations.
    bus.Scheduler = new GbScheduler(bus);
}
bus.LoadRom(rom);

ICpuBackend cpu = opts.Backend switch
{
    "legacy"    => new LegacyCpu(),
    "json-llvm" => new JsonCpu(enableBlockJit: opts.BlockJit),
    _           => throw new ArgumentException($"Unknown backend '{opts.Backend}'"),
};
if (opts.BlockJit && opts.Backend != "json-llvm")
    Console.WriteLine($"  warning: --block-jit only affects --cpu=json-llvm (current: {opts.Backend})");
if (opts.BlockJit)
    Console.WriteLine($"  block-jit:   ON (Phase 7 GB block-JIT P0 path — variable-width detector + CB-prefix atomic + imm baking)");
cpu.Reset(bus);
Console.WriteLine($"  initial PC=0x{cpu.ReadReg16(GbReg16.PC):X4} SP=0x{cpu.ReadReg16(GbReg16.SP):X4}");

var runSw = System.Diagnostics.Stopwatch.StartNew();
var consumed = cpu.RunCycles(opts.Cycles);
runSw.Stop();
{
    var hostSecs = runSw.Elapsed.TotalSeconds;
    var emSecs   = consumed / (double)GbTiming.CpuClockHz;
    var ratio    = hostSecs > 0 ? emSecs / hostSecs : 0;
    var instr    = cpu.InstructionsExecuted;
    var mips     = hostSecs > 0 ? (instr / 1_000_000.0) / hostSecs : 0;
    Console.WriteLine($"  ran {consumed:N0} t-cycles in {hostSecs:F3} s host time " +
                      $"({emSecs:F3} DMG-emulated s, {ratio:F1}× real-time)");
    Console.WriteLine($"  instructions: {instr:N0}  →  {mips:F2} MIPS");
}
Console.WriteLine($"  final PC=0x{cpu.ReadReg16(GbReg16.PC):X4} halted={cpu.IsHalted}");
Console.WriteLine($"  LCDC=0x{bus.Io[0x40]:X2} STAT=0x{bus.Io[0x41]:X2} LY=0x{bus.Io[0x44]:X2} LYC=0x{bus.Io[0x45]:X2}");
Console.WriteLine($"  SCY={bus.Io[0x42]} SCX={bus.Io[0x43]} BGP=0x{bus.Io[0x47]:X2}  IF=0x{bus.InterruptFlag:X2} IE=0x{bus.InterruptEnable:X2}");
if (bus.Scheduler is not null)
    Console.WriteLine($"  scheduler: scanline={bus.Scheduler.Scanline} mode={bus.Scheduler.Mode} frames={bus.Scheduler.FrameCount}");

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
        else if (arg.StartsWith("--bios="))       opts.BiosPath = arg.Substring("--bios=".Length);
        else if (arg.StartsWith("--cycles="))     opts.Cycles  = long.Parse(arg.Substring("--cycles=".Length));
        else if (arg.StartsWith("--frames="))     opts.Cycles  = long.Parse(arg.Substring("--frames=".Length)) * GbTiming.TCyclesPerFrame;
        else if (arg.StartsWith("--seconds="))    opts.Cycles  = (long)System.Math.Round(double.Parse(arg.Substring("--seconds=".Length), System.Globalization.CultureInfo.InvariantCulture) * GbTiming.CpuClockHz);
        else if (arg.StartsWith("--screenshot=")) opts.ScreenshotPath = arg.Substring("--screenshot=".Length);
        else if (arg == "--info")                 opts.InfoOnly = true;
        else if (arg.StartsWith("--diff="))       opts.DiffMaxSteps = long.Parse(arg.Substring("--diff=".Length));
        else if (arg == "--bench")                opts.Bench = true;
        else if (arg == "--block-jit")            opts.BlockJit = true;
        else                                      return null;
    }
    return opts.RomPath is null ? null : opts;
}

static void PrintUsage()
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  apr-gb --rom=<path.gb|.zip> [--bios=<path.bin>] [--cpu=legacy|json-llvm]");
    Console.Error.WriteLine("         [--cycles=N | --frames=N | --seconds=N]");
    Console.Error.WriteLine("         [--screenshot=out.png] [--info]");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Run-length units (later wins; pick one):");
    Console.Error.WriteLine("  --cycles=N    raw t-cycles (DMG: 70224 = 1 frame)");
    Console.Error.WriteLine("  --frames=N    DMG frames");
    Console.Error.WriteLine("  --seconds=N   DMG-emulated wall-time seconds (4,194,304 cyc/s)");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Defaults: --cpu=legacy --cycles=70224 (= 1 frame ≈ 0.0167 DMG seconds).");
    Console.Error.WriteLine("Without --bios: post-BIOS state is set up directly (PC=0x0100, etc).");
    Console.Error.WriteLine("With    --bios: power-on cold start (all regs 0, PC=0); BIOS scrolls");
    Console.Error.WriteLine("              the Nintendo logo and hands off to cart at ~0.5s.");
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

    Console.WriteLine($"  setup time: legacy={legSetupMs:F0} ms, json-llvm={jitSetupMs:F0} ms (incl. spec compile + ORC LLJIT)");
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
    public string?  BiosPath;
    public string   Backend = "legacy";
    public long     Cycles  = 70224;     // one DMG frame = 70224 t-cycles
    public string?  ScreenshotPath;
    public bool     InfoOnly;
    public long     DiffMaxSteps;        // when > 0, run lockstep diff harness instead
    public bool     Bench;               // when true, run both backends and report MIPS
    public bool     BlockJit;            // --block-jit: enable Phase 7 GB block-JIT path on json-llvm
}

/// <summary>
/// DMG timing: CPU clock 4.194304 MHz; one frame = 154 scanlines × 456
/// t-cycles each = 70224 t-cycles ≈ 16.74 ms ≈ 59.7275 fps. Matches GBA
/// frame rate by coincidence (the frame-clock divider is identical).
/// </summary>
internal static class GbTiming
{
    public const long CpuClockHz       = 4_194_304L;
    public const long TCyclesPerFrame  = 70_224L;
    public const double FramesPerSecond = (double)CpuClockHz / TCyclesPerFrame;
}
