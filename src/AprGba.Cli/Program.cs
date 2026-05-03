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
if (opts.DisableBiosOpenBus) bus.DisableBiosOpenBus = true;
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
var (cpu, swap, rt, disposables) = BootCpu(bus, opts.BlockJit);
var runner = new GbaSystemRunner(cpu, bus, swap);
setupSw.Stop();
Console.WriteLine($"  setup time: {setupSw.Elapsed.TotalMilliseconds:F0} ms (incl. spec compile + ORC LLJIT)");
if (opts.BlockJit) Console.WriteLine("  block-jit:  ON (Phase 7 A.6 path — block detection + cache + on-demand compile)");

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
else if (opts.TraceFramesPath is not null)
{
    RunWithFrameTrace(runner, cpu, bus, opts.Cycles, opts.TraceFramesPath);
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
    var instr      = cpu.InstructionsExecuted;
    var mips       = hostSecs > 0 ? (instr / 1_000_000.0) / hostSecs : 0;
    Console.WriteLine($"  ran {opts.Cycles:N0} cycles in {hostSecs:F3} s host time " +
                      $"({emSecs:F3} GBA-emulated s, {realtimeX:F1}× real-time)");
    Console.WriteLine($"  instructions: {instr:N0}  →  {mips:F2} MIPS");
    if (cpu.BlocksExecuted > 0)
    {
        var avgBlock = (double)instr / cpu.BlocksExecuted;
        Console.WriteLine($"  blocks: {cpu.BlocksExecuted:N0} executed, {cpu.BlocksCompiled:N0} compiled (cache miss rate {(double)cpu.BlocksCompiled / cpu.BlocksExecuted:P2}, avg {avgBlock:F1} instr/block)");
    }
}
Console.WriteLine($"  final PC = 0x{cpu.Pc:X8}, R0..R15 = {DumpRegs(cpu)}");
Console.WriteLine($"  IRQs delivered: {runner.IrqsDelivered}, frames: {runner.Scheduler.FrameCount}");
Console.WriteLine($"  CPSR=0x{cpu.ReadStatus("CPSR"):X8}");
Console.WriteLine($"  DISPCNT=0x{bus.ReadHalfword(0x04000000):X4} DISPSTAT=0x{bus.ReadHalfword(0x04000004):X4} VCOUNT=0x{bus.ReadHalfword(0x04000006):X4}");
Console.WriteLine($"  IE=0x{bus.ReadHalfword(0x04000200):X4} IF=0x{bus.ReadHalfword(0x04000202):X4} IME=0x{bus.ReadWord(0x04000208):X8}");
Console.WriteLine($"  HALTCNT writes: {bus.HaltCntWriteCount}, currently halted: {bus.CpuHalted}");
{
    ushort bg2cnt = bus.ReadHalfword(0x04000008), bg3cnt = bus.ReadHalfword(0x0400000A);
    short bg3pa = (short)bus.ReadHalfword(0x04000030), bg3pb = (short)bus.ReadHalfword(0x04000032);
    short bg3pc = (short)bus.ReadHalfword(0x04000034), bg3pd = (short)bus.ReadHalfword(0x04000036);
    uint bg3x = bus.ReadWord(0x04000038), bg3y = bus.ReadWord(0x0400003C);
    ushort pal0 = (ushort)(bus.Palette[0] | (bus.Palette[1] << 8));
    ushort bg2cntFix = bus.ReadHalfword(0x0400000C), bg3cntFix = bus.ReadHalfword(0x0400000E);
    Console.WriteLine($"  BG0CNT=0x{bg2cnt:X4} BG1CNT=0x{bg3cnt:X4} BG2CNT=0x{bg2cntFix:X4} BG3CNT=0x{bg3cntFix:X4}");
    Console.WriteLine($"  BG3 PA={bg3pa} PB={bg3pb} PC={bg3pc} PD={bg3pd} X=0x{bg3x:X8} Y=0x{bg3y:X8}");
    Console.WriteLine($"  BG3 char-base={(bg3cnt>>2)&3}*0x4000  screen-base={(bg3cnt>>8)&0x1F}*0x800  size={(bg3cnt>>14)&3}");
    Console.WriteLine($"  Palette[0]=0x{pal0:X4}  BLDCNT=0x{bus.ReadHalfword(0x04000050):X4} BLDALPHA=0x{bus.ReadHalfword(0x04000052):X4} BLDY=0x{bus.ReadHalfword(0x04000054):X4}");
    Console.WriteLine($"  WIN0H=0x{bus.ReadHalfword(0x04000040):X4} WIN1H=0x{bus.ReadHalfword(0x04000042):X4} WIN0V=0x{bus.ReadHalfword(0x04000044):X4} WIN1V=0x{bus.ReadHalfword(0x04000046):X4}");
    Console.WriteLine($"  WININ=0x{bus.ReadHalfword(0x04000048):X4} WINOUT=0x{bus.ReadHalfword(0x0400004A):X4}");
    int activeObjs = 0, mode1Objs = 0, mode2Objs = 0;
    for (int i = 0; i < 128; i++) {
        ushort a0 = (ushort)(bus.Oam[i*8] | (bus.Oam[i*8+1] << 8));
        bool affine = (a0 & 0x100) != 0;
        if (!affine && (a0 & 0x200) != 0) continue;     // disabled
        activeObjs++;
        int objMode = (a0 >> 10) & 3;
        if (objMode == 1) mode1Objs++;
        if (objMode == 2) mode2Objs++;
    }
    Console.WriteLine($"  active sprites: {activeObjs} (semi-trans/mode1={mode1Objs}, obj-window/mode2={mode2Objs})");

    // Dump first 16 active sprites with non-trivial details for diagnosis
    Console.WriteLine("  sprite details (first 30 active w/ tile):");
    int dumped = 0;
    for (int i = 0; i < 128 && dumped < 30; i++) {
        ushort a0 = (ushort)(bus.Oam[i*8] | (bus.Oam[i*8+1] << 8));
        ushort a1 = (ushort)(bus.Oam[i*8+2] | (bus.Oam[i*8+3] << 8));
        ushort a2 = (ushort)(bus.Oam[i*8+4] | (bus.Oam[i*8+5] << 8));
        bool affine = (a0 & 0x100) != 0;
        if (!affine && (a0 & 0x200) != 0) continue;
        int objMode = (a0 >> 10) & 3;
        if (objMode == 2) continue;
        int tileBase = a2 & 0x3FF;
        // (no tile filter — show all)
        int yOrigin = a0 & 0xFF;
        int xOrigin = a1 & 0x1FF;
        int sxO = xOrigin >= 256 ? xOrigin - 512 : xOrigin;
        int syO = yOrigin >= 160 ? yOrigin - 256 : yOrigin;
        int affineIdx = affine ? (a1 >> 9) & 0x1F : 0;
        int pa = 0, pb = 0, pc = 0, pd = 0;
        if (affine) {
            int matBase = affineIdx * 32;
            pa = (short)(bus.Oam[matBase + 0x06] | (bus.Oam[matBase + 0x07] << 8));
            pb = (short)(bus.Oam[matBase + 0x0E] | (bus.Oam[matBase + 0x0F] << 8));
            pc = (short)(bus.Oam[matBase + 0x16] | (bus.Oam[matBase + 0x17] << 8));
            pd = (short)(bus.Oam[matBase + 0x1E] | (bus.Oam[matBase + 0x1F] << 8));
        }
        int shp = (a0 >> 14) & 3;
        int sz  = (a1 >> 14) & 3;
        Console.WriteLine($"    OBJ[{i,3}]: x={sxO,4} y={syO,4} tile={tileBase,4} sh={shp} sz={sz} aff={(affine?"Y":"N")} mode={objMode} mat[{affineIdx,2}]={pa,4},{pb,4},{pc,4},{pd,4}");
        dumped++;
    }
}

if (opts.ScreenshotPath is not null)
{
    var ppu = new GbaPpu();
    if (opts.DisableObj) ppu.DisableObj = true;
    if (opts.DisableBg)  ppu.DisableBg  = true;
    if (opts.OnlyObjIndex >= 0) ppu.OnlyObjIndex = opts.OnlyObjIndex;
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

/// <summary>
/// Per-frame state trace — at every frame boundary dump PC, CPSR, key
/// IO regs, and selected GPRs. Designed to compare per-instr vs
/// block-JIT execution paths for divergence detection. The same
/// emulated cycle budget produces the same number of frame samples
/// regardless of execution mode.
/// </summary>
static void RunWithFrameTrace(GbaSystemRunner runner, CpuExecutor cpu, GbaMemoryBus bus,
                               long cycleBudget, string outPath)
{
    using var fs = new StreamWriter(outPath) { AutoFlush = true };
    fs.WriteLine("frame\tPC\tCPSR\tIE\tIF\tIME\tDISPCNT\tHALTCNT_writes\tIRQs\tR0\tR13\tR14");
    // Phase 7 A.6.1 — use runner.RunCycles in 1-frame chunks so block-JIT's
    // predictive cycle budget + MMIO catch-up logic stays consistent. The
    // previous hand-rolled loop bypassed runner's CyclesLeft setup, causing
    // block IR to budget-exit on every instruction and stale-_budgetAtStep
    // miscounts via the MMIO sync handler.
    long lastFrame = runner.Scheduler.FrameCount;
    long consumed = 0;
    long perFrame = AprCpu.Core.Runtime.Gba.GbaScheduler.CyclesPerFrame;
    // Sample initial state.
    fs.WriteLine($"{lastFrame}\t0x{cpu.Pc:X8}\t0x{cpu.ReadStatus("CPSR"):X8}\t0x{bus.ReadHalfword(0x04000200):X4}\t0x{bus.ReadHalfword(0x04000202):X4}\t0x{bus.ReadWord(0x04000208):X4}\t0x{bus.ReadHalfword(0x04000000):X4}\t{bus.HaltCntWriteCount}\t{runner.IrqsDelivered}\t0x{cpu.ReadGpr(0):X8}\t0x{cpu.ReadGpr(13):X8}\t0x{cpu.ReadGpr(14):X8}");
    while (consumed < cycleBudget)
    {
        var chunk = Math.Min(perFrame, cycleBudget - consumed);
        consumed += runner.RunCycles(chunk);
        if (runner.Scheduler.FrameCount != lastFrame)
        {
            lastFrame = runner.Scheduler.FrameCount;
            fs.WriteLine($"{lastFrame}\t0x{cpu.Pc:X8}\t0x{cpu.ReadStatus("CPSR"):X8}\t0x{bus.ReadHalfword(0x04000200):X4}\t0x{bus.ReadHalfword(0x04000202):X4}\t0x{bus.ReadWord(0x04000208):X4}\t0x{bus.ReadHalfword(0x04000000):X4}\t{bus.HaltCntWriteCount}\t{runner.IrqsDelivered}\t0x{cpu.ReadGpr(0):X8}\t0x{cpu.ReadGpr(13):X8}\t0x{cpu.ReadGpr(14):X8}");
        }
    }
    fs.Flush();
}

static (CpuExecutor Cpu, Arm7tdmiBankSwapHandler Swap, HostRuntime Rt, IDisposable[] Bindings)
    BootCpu(GbaMemoryBus bus, bool enableBlockJit = false)
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
    if (enableBlockJit)
        exec.EnableBlockJit(compileResult);
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
        else if (arg.StartsWith("--trace-frames=")) opts.TraceFramesPath = arg.Substring("--trace-frames=".Length);
        else if (arg == "--info")                 opts.InfoOnly = true;
        else if (arg == "--no-bios-openbus")      opts.DisableBiosOpenBus = true;
        else if (arg == "--no-obj")               opts.DisableObj = true;
        else if (arg == "--no-bg")                opts.DisableBg  = true;
        else if (arg.StartsWith("--only-obj="))   opts.OnlyObjIndex = int.Parse(arg.Substring("--only-obj=".Length));
        else if (arg == "--block-jit")            opts.BlockJit = true;
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
    public string?  TraceFramesPath;
    public bool     InfoOnly;
    public bool     DisableBiosOpenBus;
    public bool     DisableObj;
    public bool     DisableBg;
    public int      OnlyObjIndex = -1;
    public bool     BlockJit;
}
