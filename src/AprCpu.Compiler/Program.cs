using AprCpu.Core;
using AprCpu.Core.Compilation;

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
