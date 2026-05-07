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

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

string? romPath = null;
bool infoMode = false;
foreach (var arg in args)
{
    if      (arg == "--info")            infoMode = true;
    else if (arg.StartsWith("--rom="))   romPath = arg.Substring("--rom=".Length);
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
}

return 0;

static void PrintUsage()
{
    Console.Error.WriteLine("usage: apr-nes [--info] [--rom=<path.nes>]");
    Console.Error.WriteLine();
    Console.Error.WriteLine("N0 milestone — spec validation + ROM header parse only.");
    Console.Error.WriteLine("Full CPU execution will be wired in N1+.");
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
