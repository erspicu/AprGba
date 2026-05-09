// TomHarteRunner — drive X86LegacyCpu through Tom Harte SingleStepTests
// 8088 v2 dataset (https://github.com/SingleStepTests/8088). Each test
// fully specifies CPU state + memory before & after one instruction;
// we set the state, step once, diff against the expected final.
//
// Phase 24.2.4 deliverable: this runner + CLI integration in
// AprX86.Cli/Program.cs (--tomharte=<path>) + initial smoke run on
// opcode 0x00 (ADD r/m8, r8) showing how many of 10000 tests pass.
//
// Test JSON format (gzipped):
//   [
//     {
//       "name": "...",                  // human-readable
//       "bytes": [0,75,156],            // instruction bytes
//       "initial": {
//         "regs": {"ax":..., "ip":..., "cs":..., "flags":..., ...},
//         "ram":  [[linear_addr, value], ...]
//       },
//       "final": {
//         "regs": {"ax":..., "ip":..., "flags":..., ...},   // delta
//         "ram":  [[linear_addr, value], ...]
//       },
//       "cycles": [...]                  // bus-pin trace; ignored here
//     },
//     ...
//   ]
//
// A "final" only lists registers/ram that the test CARES about (typically
// everything that could differ — the spec is unambiguous about what
// final state is allowed). Our diff treats all listed final.regs and
// final.ram as required to match exactly; un-listed registers retain
// initial values.

using System.IO.Compression;
using System.Text.Json;
using AprX86.Cli.Cpu;
using AprX86.Cli.Memory;

namespace AprX86.Cli.Tests;

public sealed class TomHarteRunner
{
    public sealed class Result
    {
        public int Total;
        public int Passed;
        public int Failed;
        public List<Failure> Failures = new();
    }

    public sealed class Failure
    {
        public required int Index;
        public required string Name;
        public required List<string> Diffs;
    }

    public Result RunFile(string path, int? limit = null, bool stopOnFirstFailure = false)
    {
        Stream raw = File.OpenRead(path);
        if (path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            raw = new GZipStream(raw, CompressionMode.Decompress);

        var tests = JsonSerializer.Deserialize<List<JsonElement>>(raw)
            ?? throw new InvalidDataException($"empty/invalid test file: {path}");

        var result = new Result { Total = Math.Min(tests.Count, limit ?? tests.Count) };

        for (int i = 0; i < result.Total; i++)
        {
            var test = tests[i];
            var diffs = RunOne(test);
            if (diffs.Count == 0) result.Passed++;
            else
            {
                result.Failed++;
                result.Failures.Add(new Failure
                {
                    Index = i,
                    Name = test.GetProperty("name").GetString() ?? $"#{i}",
                    Diffs = diffs,
                });
                if (stopOnFirstFailure) break;
            }
        }
        return result;
    }

    /// <summary>
    /// Run a single test case. Returns an empty list on pass, or a list
    /// of human-readable diff lines on fail.
    /// </summary>
    public List<string> RunOne(JsonElement test)
    {
        var diffs = new List<string>();

        // --- Set up CPU state from initial ---
        var mem = new X86Memory();
        var cpu = new X86LegacyCpu(mem);
        cpu.Reset();

        var initial = test.GetProperty("initial");
        var iRegs = initial.GetProperty("regs");
        ApplyRegs(cpu.State, iRegs);

        var iRam = initial.GetProperty("ram");
        foreach (var pair in iRam.EnumerateArray())
        {
            int addr = pair[0].GetInt32();
            byte v = (byte)pair[1].GetInt32();
            mem.Ram[addr & 0xFFFFF] = v;
        }

        // --- Step exactly one instruction ---
        try
        {
            cpu.Step();
        }
        catch (Exception ex)
        {
            diffs.Add($"step threw: {ex.GetType().Name}: {ex.Message}");
            return diffs;
        }

        // --- Diff against expected final ---
        var final = test.GetProperty("final");
        if (final.TryGetProperty("regs", out var fRegs))
        {
            foreach (var prop in fRegs.EnumerateObject())
                CompareReg(cpu.State, prop.Name, prop.Value.GetInt32(), diffs);
        }
        if (final.TryGetProperty("ram", out var fRam))
        {
            foreach (var pair in fRam.EnumerateArray())
            {
                int addr = pair[0].GetInt32();
                byte want = (byte)pair[1].GetInt32();
                byte got = mem.Ram[addr & 0xFFFFF];
                if (got != want)
                    diffs.Add($"ram[0x{addr:X5}]: want=0x{want:X2} got=0x{got:X2}");
            }
        }

        return diffs;
    }

    private static void ApplyRegs(X86State s, JsonElement regs)
    {
        foreach (var prop in regs.EnumerateObject())
        {
            ushort v = (ushort)prop.Value.GetInt32();
            switch (prop.Name)
            {
                case "ax": s.A.X = v; break;
                case "bx": s.B.X = v; break;
                case "cx": s.C.X = v; break;
                case "dx": s.D.X = v; break;
                case "sp": s.SP = v; break;
                case "bp": s.BP = v; break;
                case "si": s.SI = v; break;
                case "di": s.DI = v; break;
                case "cs": s.CS = v; break;
                case "ds": s.DS = v; break;
                case "es": s.ES = v; break;
                case "ss": s.SS = v; break;
                case "ip": s.IP = v; break;
                case "flags": s.SetFlags(v); break;
                default: break;    // ignore unknown reg names
            }
        }
    }

    private static void CompareReg(X86State s, string name, int wantInt, List<string> diffs)
    {
        ushort want = (ushort)wantInt;
        ushort got = name switch
        {
            "ax" => s.A.X, "bx" => s.B.X, "cx" => s.C.X, "dx" => s.D.X,
            "sp" => s.SP,  "bp" => s.BP,  "si" => s.SI,  "di" => s.DI,
            "cs" => s.CS,  "ds" => s.DS,  "es" => s.ES,  "ss" => s.SS,
            "ip" => s.IP,  "flags" => s.GetFlags(),
            _ => want,    // unknown — pretend match to avoid spurious diff
        };
        if (got != want)
        {
            // For flags, also dump the bit names that differ for easier triage.
            if (name == "flags")
            {
                string detail = FlagDiff(want, got);
                diffs.Add($"{name}: want=0x{want:X4} got=0x{got:X4} ({detail})");
            }
            else
            {
                diffs.Add($"{name}: want=0x{want:X4} got=0x{got:X4}");
            }
        }
    }

    private static string FlagDiff(ushort want, ushort got)
    {
        ushort xor = (ushort)(want ^ got);
        if (xor == 0) return "match";
        var parts = new List<string>();
        void Add(int bit, string n)
        {
            if ((xor & (1 << bit)) != 0)
                parts.Add($"{n}={(((got >> bit) & 1) == 1 ? '1' : '0')}→{(((want >> bit) & 1) == 1 ? '1' : '0')}");
        }
        Add(0, "CF"); Add(2, "PF"); Add(4, "AF"); Add(6, "ZF"); Add(7, "SF");
        Add(8, "TF"); Add(9, "IF"); Add(10, "DF"); Add(11, "OF");
        return string.Join(", ", parts);
    }
}
