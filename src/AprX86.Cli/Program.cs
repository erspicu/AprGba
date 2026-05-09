// AprX86 CLI harness — Intel x86-16 (8086 base) validation.
//
// Phase 24.1 deliverable: minimum viable harness that can load a raw
// .com binary at 0:0x100 (CP/M convention), step the CPU stub, detect
// HLT, and report final state. Real opcodes (phase 24.2) will let
// hello-cga.com produce the first screenshot in phase 24.3.

using System;
using System.IO;
using AprX86.Cli.Cpu;
using AprX86.Cli.Memory;
using AprX86.Cli.Tests;

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

string? romPath = null;
string? tomHartePath = null;
int? tomHarteLimit = null;
bool tomHarteStopOnFail = false;
int tomHarteMaxFailures = 10;
ushort entrySeg = 0x0000;
ushort entryOff = 0x0100;     // CP/M .com convention
long maxCycles = 5_000_000L;
string backend = "legacy";
bool verbose = false;

foreach (var arg in args)
{
    if      (arg.StartsWith("--rom="))               romPath = arg.Substring("--rom=".Length);
    else if (arg.StartsWith("--tomharte="))          tomHartePath = arg.Substring("--tomharte=".Length);
    else if (arg.StartsWith("--tomharte-limit="))    tomHarteLimit = int.Parse(arg.Substring("--tomharte-limit=".Length));
    else if (arg == "--tomharte-stop-on-fail")       tomHarteStopOnFail = true;
    else if (arg.StartsWith("--tomharte-max-failures=")) tomHarteMaxFailures = int.Parse(arg.Substring("--tomharte-max-failures=".Length));
    else if (arg.StartsWith("--entry-seg="))         entrySeg = ParseHex16(arg.Substring("--entry-seg=".Length));
    else if (arg.StartsWith("--entry-off="))         entryOff = ParseHex16(arg.Substring("--entry-off=".Length));
    else if (arg.StartsWith("--max-cycles="))        maxCycles = long.Parse(arg.Substring("--max-cycles=".Length));
    else if (arg.StartsWith("--backend="))           backend  = arg.Substring("--backend=".Length);
    else if (arg == "--verbose" || arg == "-v")      verbose = true;
    else { Console.Error.WriteLine($"unknown arg: {arg}"); PrintUsage(); return 2; }
}

// ============================================================
// Tom Harte SST mode: load + run one opcode's worth of tests.
// ============================================================
if (tomHartePath != null)
{
    if (!File.Exists(tomHartePath))
    {
        Console.Error.WriteLine($"error: Tom Harte test file not found: {tomHartePath}");
        return 3;
    }
    Console.WriteLine($"AprX86 — Tom Harte SST runner");
    Console.WriteLine($"  test file:    {tomHartePath}");
    if (tomHarteLimit.HasValue)
        Console.WriteLine($"  limit:        {tomHarteLimit.Value}");
    Console.WriteLine();

    var runner = new TomHarteRunner();
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var result = runner.RunFile(tomHartePath, tomHarteLimit, tomHarteStopOnFail);
    sw.Stop();

    Console.WriteLine($"  total:        {result.Total}");
    Console.WriteLine($"  passed:       {result.Passed}  ({100.0 * result.Passed / Math.Max(result.Total, 1):F1}%)");
    Console.WriteLine($"  failed:       {result.Failed}");
    Console.WriteLine($"  elapsed:      {sw.Elapsed.TotalSeconds:F2}s");

    if (result.Failed > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"  first {Math.Min(tomHarteMaxFailures, result.Failures.Count)} failure(s):");
        foreach (var f in result.Failures.Take(tomHarteMaxFailures))
        {
            Console.WriteLine($"    [{f.Index,5}] {f.Name}");
            foreach (var d in f.Diffs.Take(6))
                Console.WriteLine($"      {d}");
        }
    }
    return result.Failed == 0 ? 0 : 6;
}

// ============================================================
// ROM mode: load + run a flat .com binary.
// ============================================================
if (romPath is null)
{
    Console.Error.WriteLine("error: --rom=<path> or --tomharte=<path> is required");
    PrintUsage();
    return 2;
}

if (!File.Exists(romPath))
{
    Console.Error.WriteLine($"error: ROM not found: {romPath}");
    return 3;
}

// --- Setup ---
var mem = new X86Memory();
var rom = File.ReadAllBytes(romPath);
mem.LoadBinary(rom, entrySeg, entryOff);

IX86CpuBackend cpu = backend switch
{
    "legacy" => new X86LegacyCpu(mem),
    _        => throw new NotSupportedException($"backend '{backend}' not yet supported (phase 24.1 only ships legacy stub)"),
};
cpu.Reset();
cpu.SetEntryPoint(entrySeg, entryOff);

Console.WriteLine($"AprX86 — Intel x86-16 harness (phase 24.1 stub)");
Console.WriteLine($"  rom:        {romPath} ({rom.Length} bytes)");
Console.WriteLine($"  entry:      {entrySeg:X4}:{entryOff:X4}");
Console.WriteLine($"  backend:    {cpu.BackendName}");
Console.WriteLine($"  max-cycles: {maxCycles:N0}");
Console.WriteLine();

// --- Run ---
long instrCount = 0, cycles = 0;
try
{
    while (!cpu.Halted && cycles < maxCycles)
    {
        if (verbose)
        {
            var s = cpu.State;
            Console.WriteLine($"  CS:IP={s.CS:X4}:{s.IP:X4} AX={s.A.X:X4} BX={s.B.X:X4} CX={s.C.X:X4} DX={s.D.X:X4} SP={s.SP:X4}");
        }
        cycles += cpu.Step();
        instrCount++;
    }
}
catch (NotImplementedException ex)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"  STOP: {ex.Message}");
    Console.Error.WriteLine($"  ran {instrCount:N0} instr / {cycles:N0} cycles before hitting unimplemented opcode");
    return 4;
}

Console.WriteLine();
Console.WriteLine($"  ran {instrCount:N0} instr in {cycles:N0} cycles");
Console.WriteLine($"  final state:");
{
    var s = cpu.State;
    Console.WriteLine($"    CS:IP={s.CS:X4}:{s.IP:X4} AX={s.A.X:X4} BX={s.B.X:X4} CX={s.C.X:X4} DX={s.D.X:X4}");
    Console.WriteLine($"    SP={s.SP:X4} BP={s.BP:X4} SI={s.SI:X4} DI={s.DI:X4}");
    Console.WriteLine($"    DS={s.DS:X4} ES={s.ES:X4} SS={s.SS:X4}");
    Console.WriteLine($"    FLAGS={s.GetFlags():X4}  C={(s.FlagC?1:0)} P={(s.FlagP?1:0)} A={(s.FlagA?1:0)} Z={(s.FlagZ?1:0)} S={(s.FlagS?1:0)} O={(s.FlagO?1:0)} D={(s.FlagD?1:0)} I={(s.FlagI?1:0)}");
}
Console.WriteLine($"  halted: {cpu.Halted}");

return cpu.Halted ? 0 : 5;

static ushort ParseHex16(string s)
    => (ushort)Convert.ToUInt16(s.TrimStart('$', '0', 'x', 'X'), 16);

static void PrintUsage()
{
    Console.Error.WriteLine("usage:");
    Console.Error.WriteLine("  apr-x86 --rom=<path> [--entry-seg=<hex>] [--entry-off=<hex>]");
    Console.Error.WriteLine("          [--max-cycles=N] [--backend=legacy] [--verbose]");
    Console.Error.WriteLine();
    Console.Error.WriteLine("  apr-x86 --tomharte=<.json[.gz]> [--tomharte-limit=N]");
    Console.Error.WriteLine("          [--tomharte-stop-on-fail] [--tomharte-max-failures=N]");
}
