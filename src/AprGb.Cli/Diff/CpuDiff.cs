using AprGb.Cli.Cpu;
using AprGb.Cli.Memory;

namespace AprGb.Cli.Diff;

/// <summary>
/// Lockstep diff harness — runs LegacyCpu and JsonCpu against the same
/// ROM with separate buses, comparing register state after each
/// instruction. Reports the first divergence (PC + opcode + which
/// register first differs) so behavioural bugs in JsonCpu's emitters
/// can be hunted by inspection.
///
/// Convention: each call to <c>backend.RunCycles(1)</c> runs at least
/// one instruction. Both backends are advanced by one step per
/// iteration; if their cycle accounting differs slightly that's fine —
/// we compare register snapshots, not consumed-cycle totals.
/// </summary>
public static class CpuDiff
{
    public sealed record Snapshot(
        ushort PC, ushort SP,
        byte A, byte F, byte B, byte C, byte D, byte E, byte H, byte L,
        byte Opcode);

    public sealed record DivergenceReport(
        long Step,
        Snapshot Legacy,
        Snapshot Json,
        string FirstDifferingField,
        ushort PreStepPc,
        byte PreStepOpcode);

    public static DivergenceReport? Run(string romPath, long maxSteps, bool verbose = false)
    {
        var rom = RomLoader.Load(romPath);

        var busL = new GbMemoryBus(); busL.LoadRom(rom);
        var busJ = new GbMemoryBus(); busJ.LoadRom(rom);

        var legacy = new LegacyCpu(); legacy.Reset(busL);
        var json   = new JsonCpu();   json.Reset(busJ);

        for (long i = 0; i < maxSteps; i++)
        {
            // Snapshot the about-to-execute opcode at each side's current PC.
            var preL = TakeSnapshot(legacy, busL);
            var preJ = TakeSnapshot(json,   busJ);

            if (verbose && i % 1000 == 0)
                Console.WriteLine($"step {i,6}: PC={preL.PC:X4} op={preL.Opcode:X2}");

            // Step each backend by at least one instruction.
            legacy.RunCycles(1);
            json  .RunCycles(1);

            var postL = TakeSnapshot(legacy, busL);
            var postJ = TakeSnapshot(json,   busJ);

            var diff = FirstDifference(postL, postJ);
            if (diff is not null)
            {
                return new DivergenceReport(
                    Step: i,
                    Legacy: postL,
                    Json:   postJ,
                    FirstDifferingField: diff,
                    PreStepPc: preL.PC,
                    PreStepOpcode: preL.Opcode);
            }
        }
        return null;
    }

    private static Snapshot TakeSnapshot(ICpuBackend cpu, GbMemoryBus bus)
    {
        var pc = cpu.ReadReg16(GbReg16.PC);
        return new Snapshot(
            PC: pc,
            SP: cpu.ReadReg16(GbReg16.SP),
            A: cpu.ReadReg8(GbReg8.A),
            F: cpu.ReadReg8(GbReg8.F),
            B: cpu.ReadReg8(GbReg8.B),
            C: cpu.ReadReg8(GbReg8.C),
            D: cpu.ReadReg8(GbReg8.D),
            E: cpu.ReadReg8(GbReg8.E),
            H: cpu.ReadReg8(GbReg8.H),
            L: cpu.ReadReg8(GbReg8.L),
            Opcode: bus.ReadByte(pc));
    }

    private static string? FirstDifference(Snapshot a, Snapshot b)
    {
        if (a.PC != b.PC) return "PC";
        if (a.SP != b.SP) return "SP";
        if (a.A  != b.A)  return "A";
        if (a.F  != b.F)  return "F";
        if (a.B  != b.B)  return "B";
        if (a.C  != b.C)  return "C";
        if (a.D  != b.D)  return "D";
        if (a.E  != b.E)  return "E";
        if (a.H  != b.H)  return "H";
        if (a.L  != b.L)  return "L";
        return null;
    }

    public static string Format(DivergenceReport r)
    {
        var w = new System.Text.StringBuilder();
        w.AppendLine($"DIVERGENCE at step {r.Step}");
        w.AppendLine($"  pre-step PC = 0x{r.PreStepPc:X4}, opcode = 0x{r.PreStepOpcode:X2}");
        w.AppendLine($"  first differing field: {r.FirstDifferingField}");
        w.AppendLine($"  legacy: PC={r.Legacy.PC:X4} SP={r.Legacy.SP:X4} " +
                     $"A={r.Legacy.A:X2} F={r.Legacy.F:X2} B={r.Legacy.B:X2} C={r.Legacy.C:X2} " +
                     $"D={r.Legacy.D:X2} E={r.Legacy.E:X2} H={r.Legacy.H:X2} L={r.Legacy.L:X2}");
        w.AppendLine($"  json:   PC={r.Json.PC:X4} SP={r.Json.SP:X4} " +
                     $"A={r.Json.A:X2} F={r.Json.F:X2} B={r.Json.B:X2} C={r.Json.C:X2} " +
                     $"D={r.Json.D:X2} E={r.Json.E:X2} H={r.Json.H:X2} L={r.Json.L:X2}");
        return w.ToString();
    }
}
