using AprCpu.Core.Compilation;
using AprCpu.Core.Decoder;
using AprCpu.Core.IR;
using AprCpu.Core.JsonSpec;
using AprCpu.Core.Runtime;
using AprCpu.Core.Runtime.Gba;
using AprGba.Cli;
using AprGba.Cli.Video;

// apr-gba — headless GBA test-ROM runner + screenshot
//
// Modes:
//   apr-gba --rom=<path.gba> [--bios=<path.bin>]
//           [--cycles=N | --frames=N | --seconds=N]
//           [--screenshot=out.png] [--info]
//
// Run-length units (pick one; later wins):
//   --cycles=N    raw CPU cycles (one frame ≈ 280896 cycles)
//   --frames=N    GBA scanline frames (228 lines × 1232 cycles each)
//   --seconds=N   floating-point GBA wall-time seconds; converted via
//                 the canonical 16,777,216 cycles/sec = 59.7275 fps.
//                 e.g. --seconds=30 ≈ 1791.8 frames ≈ 503M cycles.
//
// Defaults:
//   --cycles = 280896 (one GBA frame)
//   --bios omitted → install minimal BIOS stubs (MOVS PC, LR at every vector)
//
// Per Phase 5 scope: screenshot supports DISPCNT mode 3 / 4 only. Mode 0
// (tile-based BG used by jsmolka) lands in Phase 8.

var opts = ParseArgs(args);
if (opts is null) { PrintUsage(); return 1; }

Console.WriteLine("apr-gba — GBA harness (json-llvm CPU + headless screenshot)");
Console.WriteLine($"  ROM:        {opts.RomPath}");
Console.WriteLine($"  BIOS:       {opts.BiosPath ?? "(none — using minimal vector stubs)"}");
{
    var emFrames = opts.Cycles / (double)GbaScheduler.CyclesPerFrame;
    var emSecs   = opts.Cycles / (double)GbaTiming.CpuClockHz;
    Console.WriteLine($"  budget:     {opts.Cycles:N0} cycles ≈ {emFrames:F1} frames ≈ {emSecs:F3} GBA seconds");
}
Console.WriteLine($"  screenshot: {opts.ScreenshotPath ?? "(none)"}");

var rom = File.ReadAllBytes(opts.RomPath!);
Console.WriteLine($"  loaded:     {rom.Length:N0} bytes");

if (opts.InfoOnly)
{
    DumpRomHeader(rom);
    return 0;
}

var bus = new GbaMemoryBus();
if (opts.BiosPath is not null)
{
    var bios = File.ReadAllBytes(opts.BiosPath);
    bus.LoadBios(bios);

    // Real BIOS verifies the cart's Nintendo logo at 0x004..0x09F and
    // header checksum at 0x0BD. Without these matching the BIOS's
    // built-in canonical sequence, BIOS halts forever instead of
    // jumping to ROM @ 0x08000000. Patch the cart in-place using the
    // logo bytes the BIOS itself contains.
    if (RomPatcher.EnsureValidLogoAndChecksum(rom, bios))
        Console.WriteLine($"  [logo-patch] applied Nintendo-logo + header-checksum fixup to cart in memory");
}
else
{
    bus.InstallMinimalBiosStubs();
}
bus.LoadRom(rom);

var setupSw = System.Diagnostics.Stopwatch.StartNew();
var (cpu, swap, rt, disposables) = BootCpu(bus);
var runner = new GbaSystemRunner(cpu, bus, swap);
setupSw.Stop();
Console.WriteLine($"  setup time: {setupSw.Elapsed.TotalMilliseconds:F0} ms (incl. spec compile + MCJIT)");

// LLE boot (real BIOS file loaded): start in Supervisor mode with IRQs +
// FIQs disabled (CPSR=0xD3) and PC at 0x00000000. The BIOS does its own
// startup, sets up SP for each mode, eventually switches to System mode
// and enables IRQs before jumping to ROM @ 0x08000000.
//
// HLE boot (no BIOS file): jump straight to ROM with the post-BIOS state
// the BIOS would have left — System mode, IRQs enabled, SP at IWRAM top.
if (opts.BiosPath is not null)
{
    cpu.WriteStatus("CPSR", 0xD3u);              // SVC mode + I=1 + F=1
    cpu.Pc = 0x00000000u;
}
else
{
    cpu.WriteStatus("CPSR", 0x1Fu);              // System mode, IRQs enabled
    cpu.WriteGpr(13, 0x03007F00u);               // User/System SP at IWRAM top
    cpu.Pc = 0x08000000u;                        // ROM entry
}

var runSw = System.Diagnostics.Stopwatch.StartNew();
if (opts.TraceBiosPath is not null)
{
    RunWithBiosTrace(runner, cpu, bus, opts.Cycles, opts.TraceBiosPath);
}
else
{
    runner.RunCycles(opts.Cycles);
}
runSw.Stop();
{
    var hostSecs   = runSw.Elapsed.TotalSeconds;
    var emSecs     = opts.Cycles / (double)GbaTiming.CpuClockHz;
    var realtimeX  = hostSecs > 0 ? emSecs / hostSecs : 0;
    Console.WriteLine($"  ran {opts.Cycles:N0} cycles in {hostSecs:F3} s host time " +
                      $"({emSecs:F3} GBA-emulated s, {realtimeX:F1}× real-time)");
}
Console.WriteLine($"  final PC = 0x{cpu.Pc:X8}, R0..R15 = {DumpRegs(cpu)}");
Console.WriteLine($"  IRQs delivered: {runner.IrqsDelivered}, frames: {runner.Scheduler.FrameCount}");
Console.WriteLine($"  CPSR=0x{cpu.ReadStatus("CPSR"):X8}");
Console.WriteLine($"  DISPCNT=0x{bus.ReadHalfword(0x04000000):X4} DISPSTAT=0x{bus.ReadHalfword(0x04000004):X4} VCOUNT=0x{bus.ReadHalfword(0x04000006):X4}");
Console.WriteLine($"  IE=0x{bus.ReadHalfword(0x04000200):X4} IF=0x{bus.ReadHalfword(0x04000202):X4} IME=0x{bus.ReadWord(0x04000208):X8}");
Console.WriteLine($"  HALTCNT writes: {bus.HaltCntWriteCount}, currently halted: {bus.CpuHalted}");

if (opts.ScreenshotPath is not null)
{
    var ppu = new GbaPpu();
    ppu.RenderFrame(bus);
    Directory.CreateDirectory(Path.GetDirectoryName(opts.ScreenshotPath) ?? ".");
    PngWriter.SaveRgbPng(ppu.Framebuffer, GbaPpu.Width, GbaPpu.Height, opts.ScreenshotPath);
    Console.WriteLine($"  screenshot: {opts.ScreenshotPath} ({GbaPpu.Width}×{GbaPpu.Height})");
}

foreach (var d in disposables) d.Dispose();
rt.Dispose();
return 0;

/// <summary>
/// Run the system with a per-step PC log restricted to BIOS-region PCs
/// (0x00000000..0x00003FFF). Emits a tab-separated trace file:
///   step  pc  cpsr  ie  if  ime  haltcnt_writes
/// Useful for finding where real-BIOS LLE diverges from expected
/// behaviour without flooding the console with millions of lines.
/// Throttled: writes one line every 100 BIOS-region steps so a 200M
/// cycle run produces a few thousand lines, not 50M.
/// </summary>
static void RunWithBiosTrace(GbaSystemRunner runner, CpuExecutor cpu, GbaMemoryBus bus,
                              long cycleBudget, string outPath)
{
    using var fs = new StreamWriter(outPath);
    fs.WriteLine("step\tPC\tCPSR\tIE\tIF\tIME\tHALTCNT_writes\tR0\tR14");
    long step = 0;
    long throttle = 0;
    while (runner.Scheduler.FrameCount * (long)GbaScheduler.CyclesPerFrame +
           runner.Scheduler.CycleInScanline +
           runner.Scheduler.Scanline * (long)GbaScheduler.CyclesPerScanline < cycleBudget)
    {
        if (bus.CpuHalted)
        {
            runner.Scheduler.Tick(4);
            if (runner.HasAnyPendingIrqPublic()) bus.CpuHalted = false;
            runner.DeliverIrqIfPending();
            continue;
        }

        var pc = cpu.Pc;
        cpu.Step();
        runner.Scheduler.Tick(4);
        runner.DeliverIrqIfPending();
        step++;

        if (pc < 0x00004000)
        {
            throttle++;
            if (throttle % 100 == 0)
            {
                var ime = bus.ReadWord(0x04000208);
                fs.WriteLine($"{step}\t0x{pc:X4}\t0x{cpu.ReadStatus("CPSR"):X8}\t0x{bus.ReadHalfword(0x04000200):X4}\t0x{bus.ReadHalfword(0x04000202):X4}\t0x{ime:X4}\t{bus.HaltCntWriteCount}\t0x{cpu.ReadGpr(0):X8}\t0x{cpu.ReadGpr(14):X8}");
            }
        }
    }
    fs.Flush();
}

static (CpuExecutor Cpu, Arm7tdmiBankSwapHandler Swap, HostRuntime Rt, IDisposable[] Bindings)
    BootCpu(GbaMemoryBus bus)
{
    var cpuJson = LocateArm7tdmiSpec();
    var compileResult = SpecCompiler.Compile(cpuJson);
    var loaded = SpecLoader.LoadCpuSpec(cpuJson);
    var layout = new CpuStateLayout(
        compileResult.Module.Context,
        loaded.Cpu.RegisterFile,
        loaded.Cpu.ProcessorModes,
        loaded.Cpu.ExceptionVectors);

    var rt = HostRuntime.Build(compileResult.Module, layout);
    var swap = new Arm7tdmiBankSwapHandler(rt);
    var bindings = new IDisposable[]
    {
        MemoryBusBindings.Install(rt, bus),
        BankSwapBindings.Install(rt, swap),
        UserModeRegBindings.Install(rt, swap),
    };
    rt.Compile();

    var setsByName = new Dictionary<string, (InstructionSetSpec, DecoderTable)>(StringComparer.Ordinal);
    foreach (var (name, set) in loaded.InstructionSets)
        setsByName[name] = (set, new DecoderTable(set));
    var dispatch = loaded.Cpu.InstructionSetDispatch
        ?? throw new InvalidOperationException("CPU spec missing instruction_set_dispatch");
    var exec = new CpuExecutor(rt, setsByName, dispatch, bus);
    return (exec, swap, rt, bindings);
}

static string LocateArm7tdmiSpec()
{
    var dir = AppContext.BaseDirectory;
    for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
    {
        var probe = Path.Combine(d.FullName, "spec", "arm7tdmi", "cpu.json");
        if (File.Exists(probe)) return probe;
    }
    var cwd = Path.Combine(Environment.CurrentDirectory, "spec", "arm7tdmi", "cpu.json");
    if (File.Exists(cwd)) return cwd;
    throw new FileNotFoundException("spec/arm7tdmi/cpu.json not found — run apr-gba from repo root.");
}

static void DumpRomHeader(byte[] rom)
{
    if (rom.Length < 0xC0)
    {
        Console.WriteLine("  (header truncated, ROM smaller than 0xC0 bytes)");
        return;
    }
    var title  = System.Text.Encoding.ASCII.GetString(rom, 0xA0, 12).TrimEnd('\0');
    var gameId = System.Text.Encoding.ASCII.GetString(rom, 0xAC, 4);
    Console.WriteLine($"  title:      '{title}'");
    Console.WriteLine($"  game id:    '{gameId}'");
    Console.WriteLine($"  fixed=0x96: 0x{rom[0xB2]:X2}");
}

static string DumpRegs(CpuExecutor cpu)
{
    var sb = new System.Text.StringBuilder();
    for (int i = 0; i < 16; i++)
    {
        if (i > 0) sb.Append(' ');
        sb.Append($"R{i}=0x{cpu.ReadGpr(i):X8}");
    }
    return sb.ToString();
}

static Options? ParseArgs(string[] args)
{
    var opts = new Options();
    foreach (var arg in args)
    {
        if      (arg.StartsWith("--rom="))        opts.RomPath = arg.Substring("--rom=".Length);
        else if (arg.StartsWith("--bios="))       opts.BiosPath = arg.Substring("--bios=".Length);
        else if (arg.StartsWith("--cycles="))     opts.Cycles = long.Parse(arg.Substring("--cycles=".Length));
        else if (arg.StartsWith("--frames="))     opts.Cycles = long.Parse(arg.Substring("--frames=".Length)) * GbaScheduler.CyclesPerFrame;
        else if (arg.StartsWith("--seconds="))    opts.Cycles = (long)System.Math.Round(double.Parse(arg.Substring("--seconds=".Length), System.Globalization.CultureInfo.InvariantCulture) * GbaTiming.CpuClockHz);
        else if (arg.StartsWith("--screenshot=")) opts.ScreenshotPath = arg.Substring("--screenshot=".Length);
        else if (arg.StartsWith("--trace-bios=")) opts.TraceBiosPath = arg.Substring("--trace-bios=".Length);
        else if (arg == "--info")                 opts.InfoOnly = true;
        else                                      return null;
    }
    return opts.RomPath is null ? null : opts;
}

static void PrintUsage()
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  apr-gba --rom=<path.gba> [--bios=<path.bin>]");
    Console.Error.WriteLine("          [--cycles=N | --frames=N | --seconds=N]");
    Console.Error.WriteLine("          [--screenshot=out.png] [--info]");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Run-length units (later wins; pick one):");
    Console.Error.WriteLine("  --cycles=N    raw CPU cycles");
    Console.Error.WriteLine("  --frames=N    GBA frames (1 frame = 280896 cycles)");
    Console.Error.WriteLine("  --seconds=N   GBA-emulated wall-time seconds (16,777,216 cyc/s)");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Defaults: --cycles=280896 (= 1 frame ≈ 0.0167 GBA seconds).");
    Console.Error.WriteLine("Without --bios: minimal vector stubs are installed (MOVS PC, LR).");
}

/// <summary>
/// GBA timing constants. The CPU runs at 16.78 MHz (16,777,216 Hz),
/// rendering one full frame (228 scanlines × 1232 cycles each) in
/// 280,896 cycles = ~16.74 ms. That gives a real-hardware refresh of
/// 16,777,216 / 280,896 ≈ 59.7275 frames per second.
/// </summary>
internal static class GbaTiming
{
    public const long CpuClockHz = 16_777_216L;
    public const double FramesPerSecond = (double)CpuClockHz / GbaScheduler.CyclesPerFrame;
}

internal sealed class Options
{
    public string?  RomPath;
    public string?  BiosPath;
    public long     Cycles  = GbaScheduler.CyclesPerFrame;
    public string?  ScreenshotPath;
    public string?  TraceBiosPath;
    public bool     InfoOnly;
}
