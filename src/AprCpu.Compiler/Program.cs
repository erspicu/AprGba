using AprCpu.Core;

// Phase 0 spike CLI: emits the trivial add(int, int) LLVM module to a .ll
// file and runs add(3, 4) through the JIT to verify the toolchain.
//
// Usage:
//   aprcpu --output <path>        Write IR to <path>, then JIT-run add(3,4)
//   aprcpu --emit-only <path>     Write IR only, no JIT
//   aprcpu --jit-only             JIT-run add(3,4) only

string? outputPath = null;
bool jitOnly = false;
bool emitOnly = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--output" when i + 1 < args.Length:
            outputPath = args[++i];
            break;
        case "--emit-only" when i + 1 < args.Length:
            outputPath = args[++i];
            emitOnly = true;
            break;
        case "--jit-only":
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

if (outputPath is null && !jitOnly)
{
    PrintUsage();
    return 1;
}

if (outputPath is not null)
{
    JitSpike.DumpAddIRToFile(outputPath);
    Console.WriteLine($"[aprcpu] wrote IR -> {Path.GetFullPath(outputPath)}");
}

if (!emitOnly)
{
    int result = JitSpike.JitAndRunAdd(3, 4);
    Console.WriteLine($"[aprcpu] JIT add(3, 4) = {result}");
    if (result != 7)
    {
        Console.Error.WriteLine($"[aprcpu] FAIL: expected 7, got {result}");
        return 2;
    }
    Console.WriteLine("[aprcpu] OK");
}

return 0;

static void PrintUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  aprcpu --output <path>      Emit IR + JIT-run add(3,4)");
    Console.WriteLine("  aprcpu --emit-only <path>   Emit IR only");
    Console.WriteLine("  aprcpu --jit-only           JIT-run add(3,4) only");
}
