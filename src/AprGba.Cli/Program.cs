using AprCpu.Core.Compilation;
using AprCpu.Core.Decoder;
using AprCpu.Core.IR;
using AprCpu.Core.JsonSpec;
using AprCpu.Core.Runtime;
using AprCpu.Core.Runtime.Gba;
using AprGba.Cli.Video;

// apr-gba — headless GBA test-ROM runner + screenshot
//
// Modes:
//   apr-gba --rom=<path.gba> [--bios=<path.bin>] [--cycles=N | --frames=N]
//           [--screenshot=out.png] [--info]
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
Console.WriteLine($"  cycles:     {opts.Cycles:N0}");
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
runner.RunCycles(opts.Cycles);
runSw.Stop();
Console.WriteLine($"  ran {opts.Cycles:N0} cycles in {runSw.Elapsed.TotalSeconds:F3} s");
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
        else if (arg.StartsWith("--screenshot=")) opts.ScreenshotPath = arg.Substring("--screenshot=".Length);
        else if (arg == "--info")                 opts.InfoOnly = true;
        else                                      return null;
    }
    return opts.RomPath is null ? null : opts;
}

static void PrintUsage()
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  apr-gba --rom=<path.gba> [--bios=<path.bin>]");
    Console.Error.WriteLine("          [--cycles=N | --frames=N]");
    Console.Error.WriteLine("          [--screenshot=out.png] [--info]");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Defaults: --cycles=280896 (one GBA frame).");
    Console.Error.WriteLine("Without --bios: minimal vector stubs are installed (MOVS PC, LR).");
}

internal sealed class Options
{
    public string?  RomPath;
    public string?  BiosPath;
    public long     Cycles  = GbaScheduler.CyclesPerFrame;
    public string?  ScreenshotPath;
    public bool     InfoOnly;
}
