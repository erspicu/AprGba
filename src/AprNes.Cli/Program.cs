// AprNes CLI harness — Ricoh 2A03 (NES NTSC main CPU) validation.
//
// N0 milestone: minimal entry point. Currently supports:
//   --info: print spec load summary + decoder coverage report
//   --rom=<path>: load a .nes ROM (header parse only for now)
//
// Full execution path (LegacyCpu oracle / JsonCpu via spec / lockstep diff)
// will be wired in N1+. The N0 deliverable is "spec decodes all 256
// opcodes; LegacyCpu compiles standalone".

using System;
using System.IO;
using System.Linq;
using AprCpu.Core.JsonSpec;
using AprCpu.Core.IR;
using AprCpu.Core.Compilation;
using AprNes.Cli;
using AprNes.Cli.Cpu;
using AprNes.Cli.Memory;
using AprNes.Cli.Video;

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

string? romPath = null;
string? screenshotPath = null;
bool runMode = false;
bool nestestMode = false;
ushort? startPc = null;
long maxCycles = 30_000_000L;     // ~9k instr × ~5 cyc × big margin
ushort? expectPc = null;
foreach (var arg in args)
{
    if      (arg == "--info")            { /* default — still prints info */ }
    else if (arg == "--run")             runMode = true;
    else if (arg == "--nestest")         { runMode = true; nestestMode = true; }
    else if (arg.StartsWith("--rom="))        romPath = arg.Substring("--rom=".Length);
    else if (arg.StartsWith("--screenshot=")) { screenshotPath = arg.Substring("--screenshot=".Length); runMode = true; }
    else if (arg.StartsWith("--start-pc=")) startPc = (ushort)Convert.ToUInt32(arg.Substring("--start-pc=".Length).TrimStart('$').TrimStart('0').TrimStart('x'), 16);
    else if (arg.StartsWith("--max-cycles=")) maxCycles = long.Parse(arg.Substring("--max-cycles=".Length));
    else if (arg.StartsWith("--expect-pc=")) expectPc = (ushort)Convert.ToUInt32(arg.Substring("--expect-pc=".Length).TrimStart('$').TrimStart('0').TrimStart('x'), 16);
    else { Console.Error.WriteLine($"unknown arg: {arg}"); PrintUsage(); return 2; }
}

// Load 2A03 spec — locate spec/2a03/cpu.json relative to repo root.
var specPath = LocateSpec();
Console.WriteLine($"AprNes — Ricoh 2A03 spec validation");
Console.WriteLine($"  spec:    {specPath}");

var loaded = SpecLoader.LoadCpuSpec(specPath);
Console.WriteLine($"  arch:    {loaded.Cpu.Architecture.Id} ({loaded.Cpu.Architecture.Family}) — {loaded.Cpu.Architecture.WordSizeBits}-bit, {loaded.Cpu.Architecture.Endianness}");
Console.WriteLine($"  GPRs:    {loaded.Cpu.RegisterFile.GeneralPurpose.Count} × {loaded.Cpu.RegisterFile.GeneralPurpose.WidthBits}-bit ({string.Join(",", loaded.Cpu.RegisterFile.GeneralPurpose.Names)})");
Console.WriteLine($"  status:  {loaded.Cpu.RegisterFile.Status.Count} status reg(s)");
Console.WriteLine($"  vectors: {loaded.Cpu.ExceptionVectors.Count}");
Console.WriteLine($"  isets:   {loaded.InstructionSets.Count}");

// Compile decoder + report 256-opcode coverage.
var compiled = SpecCompiler.Compile(specPath);
if (compiled.Diagnostics.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("  diagnostics:");
    foreach (var d in compiled.Diagnostics) Console.WriteLine($"    {d}");
}
if (!compiled.DecoderTables.TryGetValue("Main", out var mainDecoder))
{
    Console.Error.WriteLine("error: spec missing Main instruction set decoder");
    return 3;
}

int decoded = 0, undecoded = 0;
var byMnemonic = new System.Collections.Generic.Dictionary<string, int>();
for (int op = 0; op < 256; op++)
{
    var d = mainDecoder.Decode((uint)op);
    if (d is null) { undecoded++; continue; }
    decoded++;
    var name = d.Instruction.Mnemonic ?? "?";
    byMnemonic[name] = byMnemonic.GetValueOrDefault(name, 0) + 1;
}
Console.WriteLine();
Console.WriteLine($"  decode:  {decoded}/256 opcodes resolved ({undecoded} undecoded)");
if (undecoded > 0)
{
    Console.WriteLine("  undecoded opcodes:");
    for (int op = 0; op < 256; op++)
        if (mainDecoder.Decode((uint)op) is null) Console.WriteLine($"    0x{op:X2}");
}
Console.WriteLine();
Console.WriteLine("  mnemonic coverage:");
foreach (var (mnem, count) in byMnemonic.OrderBy(kv => kv.Key))
    Console.WriteLine($"    {mnem,-6} {count}");

if (romPath is not null)
{
    Console.WriteLine();
    Console.WriteLine($"  rom:     {romPath}");
    if (!File.Exists(romPath)) { Console.Error.WriteLine($"  rom not found"); return 4; }
    var rom = NesRomLoader.Load(romPath);
    Console.WriteLine($"  PRG ROM: {rom.PrgRom.Length} bytes ({rom.PrgRom.Length / 16384} × 16KB banks)");
    Console.WriteLine($"  CHR ROM: {rom.ChrRom.Length} bytes ({rom.ChrRom.Length / 8192} × 8KB banks)");
    Console.WriteLine($"  mapper:  {rom.MapperId}");
    Console.WriteLine($"  mirror:  {(rom.Vertical ? "vertical" : "horizontal")}");

    if (runMode)
    {
        IMapper mapper = rom.MapperId switch
        {
            0 => new Mapper000(),
            1 => new Mapper001(),
            _ => throw new NotSupportedException(
                $"mapper {rom.MapperId} not yet supported (have 0=NROM, 1=MMC1)")
        };
        mapper.Reset(rom.PrgRom, rom.ChrRom);
        var bus = new NesMemoryBus();
        bus.Reset(mapper);
        var cpu = new BoundCpu(bus);

        // Always wire the PPU — even nestest writes to PPU regs during init
        // (resets PPUCTRL/PPUMASK), and a screenshot at end-of-run captures
        // whatever the cart drew (mostly empty for nestest, but valid PNG).
        var ppu = new NesPpu(bus.Vram, bus.Oam, bus.PaletteRam, mapper, rom.Vertical);
        bus.BindPpu(
            readReg:  addr => ppu.ReadRegister(addr),
            writeReg: (addr, val) => ppu.WriteRegister(addr, val),
            tick:     cpuCycles => ppu.Tick(cpuCycles),
            consumeNmi: () => ppu.ConsumeNmi());
        // MMC1 mirroring is dynamic — let the mapper push changes to the PPU.
        mapper.MirroringChanged = mode => ppu.SetMirroringMode(mode);
        ppu.Reset();

        if (nestestMode)
        {
            cpu.InitForNestest();
            expectPc ??= 0xC66E;
            Console.WriteLine();
            Console.WriteLine($"  mode:    nestest (PC=0xC000, expect end at 0x{expectPc.Value:X4})");
        }
        else
        {
            cpu.Reset();   // standard reset-vector fetch
            if (startPc is ushort sp) cpu.SetRegisters(0, 0, 0, 0xFD, sp, 0, 0, 0, 1, 0, 0);
            Console.WriteLine();
            Console.WriteLine($"  mode:    run (max {maxCycles:N0} cycles)");
            Console.WriteLine($"  initial PC=0x{cpu.PC:X4}");
        }

        long cyclesConsumed = 0;
        long instructions = 0;
        bool reachedExpect = false;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (cyclesConsumed < maxCycles)
        {
            cyclesConsumed += cpu.Step();
            instructions++;
            if (expectPc is ushort target && cpu.PC == target)
            {
                reachedExpect = true;
                break;
            }
        }
        sw.Stop();
        Console.WriteLine();
        Console.WriteLine($"  ran {instructions:N0} instr in {sw.Elapsed.TotalSeconds:F3}s ({cyclesConsumed:N0} cycles, {instructions / sw.Elapsed.TotalSeconds / 1_000_000:F2} MIPS)");
        Console.WriteLine($"  final  PC=0x{cpu.PC:X4} A=0x{cpu.A:X2} X=0x{cpu.X:X2} Y=0x{cpu.Y:X2} SP=0x{cpu.SP:X2} P=0x{cpu.P:X2}");

        // End-of-run screenshot: render whatever the cart drew to PPU
        // memory + write as 256×240 RGB PNG. Even for CPU-only test ROMs
        // like nestest the screenshot is meaningful — many test ROMs
        // write a result string to the BG nametable, and visual diff is
        // a useful sanity check.
        if (screenshotPath is not null)
        {
            ppu.RenderFrame();
            PngWriter.SavePng(ppu.Framebuffer, NesPpu.Width, NesPpu.Height, screenshotPath);
            Console.WriteLine($"  screenshot: {screenshotPath} (256×240 RGB)");
        }

        if (expectPc is ushort tgt)
        {
            Console.WriteLine($"  expect PC=0x{tgt:X4} → {(reachedExpect ? "REACHED" : "NOT reached (cycle budget exhausted)")}");
            if (nestestMode)
            {
                Console.WriteLine($"  nestest result codes: $02=0x{bus.ReadByte(0x0002):X2} $03=0x{bus.ReadByte(0x0003):X2}");
                Console.WriteLine($"    (00,00 = all official-opcode tests passed)");
            }
            return reachedExpect ? 0 : 6;
        }
    }
}

return 0;

static void PrintUsage()
{
    Console.Error.WriteLine("usage: apr-nes [--info] [--rom=<path.nes>] [--run|--nestest]");
    Console.Error.WriteLine("              [--start-pc=<hex>] [--max-cycles=N] [--expect-pc=<hex>]");
    Console.Error.WriteLine("              [--screenshot=<out.png>]");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Modes:");
    Console.Error.WriteLine("  (default)    — load spec, report 256-opcode decode coverage");
    Console.Error.WriteLine("  --rom=X      — additionally parse iNES header");
    Console.Error.WriteLine("  --run        — boot ROM via LegacyCpu (oracle), reset vector fetch");
    Console.Error.WriteLine("  --nestest    — like --run but jump to PC=0xC000 with nestest init state,");
    Console.Error.WriteLine("                   stop when PC=0xC66E (official-opcode pass marker)");
    Console.Error.WriteLine("  --screenshot — write 256×240 PNG of PPU framebuffer at end-of-run");
}

static string LocateSpec()
{
    var dir = AppContext.BaseDirectory;
    for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
    {
        var probe = Path.Combine(d.FullName, "spec", "2a03", "cpu.json");
        if (File.Exists(probe)) return probe;
    }
    var cwdProbe = Path.Combine(Environment.CurrentDirectory, "spec", "2a03", "cpu.json");
    if (File.Exists(cwdProbe)) return cwdProbe;
    throw new FileNotFoundException("Cannot locate spec/2a03/cpu.json. Run from repo root.");
}
