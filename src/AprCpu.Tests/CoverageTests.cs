using System.Text.Json;
using AprCpu.Core.IR;
using AprCpu.Core.JsonSpec;
using Xunit;

namespace AprCpu.Tests;

/// <summary>
/// Phase 2.5.8 closeout: cross-check the spec against the emitter
/// catalogue, and assert ARM/Thumb mnemonic coverage hasn't regressed.
///
/// These tests are deliberately data-driven — they don't list specific
/// instructions, so adding new mnemonics to the spec automatically counts
/// toward coverage, and removing one is caught only if it drops below the
/// baseline.
/// </summary>
public class CoverageTests
{
    private static string CpuJson => Path.Combine(TestPaths.SpecRoot, "arm7tdmi", "cpu.json");

    [Fact]
    public void Spec_AllUsedOpsHaveRegisteredEmitter()
    {
        var (registered, used) = CollectOps();
        var missing = used.Except(registered).OrderBy(x => x).ToList();
        Assert.True(missing.Count == 0,
            "Spec uses micro-ops that no emitter handles: " + string.Join(", ", missing));
    }

    [Fact]
    public void Spec_HasNoUnusedEmitters()
    {
        // Whitelist: ops registered for completeness but not yet hit by any
        // ARM/Thumb instruction. Add new entries with a justification comment.
        var allowedUnused = new HashSet<string>(StringComparer.Ordinal)
        {
            // Generic L1 stack ops introduced by the Phase 5.8 emitter
            // refactor. They ARE used by the LR35902 spec (push_pair /
            // pop_pair via Block3_PushPop; call / ret via Block3_Call /
            // Block3_Ret) but CollectOps below only scans the ARM7TDMI
            // spec, so those usages aren't visible here. Drop these
            // entries once the scanner walks every CPU spec.
            "push_pair", "pop_pair", "call", "ret",

            // Step 5.2 — generic flag setters used by LR35902 SCF (set_flag)
            // and to-be-migrated GB ALU half-carry computations.
            // CollectOps only scans ARM7TDMI spec so these LR35902-side
            // usages are invisible.
            "set_flag", "update_h_add", "update_h_sub",

            // Step 5.3 — generic conditional branch + conditional
            // call/ret with inline cond, plus PC-arithmetic helpers
            // (read_pc / sext) for PC-relative jumps. All used by
            // LR35902 JP cc / JR (cc) / CALL cc / RET cc / RST after
            // the spec migration; invisible to this ARM7TDMI scan.
            "branch_cc", "call_cc", "ret_cc", "read_pc", "sext",

            // Step 5.4 — generic bit primitives. bit_test/set/clear
            // cover the 192 CB-prefix BIT/SET/RES opcodes; `shift`
            // (kind=rlc/rrc/rl/rr/sla/sra/swap/srl) covers the
            // remaining 64 CB-prefix opcodes plus the four
            // non-CB A-rotates (RLCA/RRCA/RLA/RRA).
            "bit_test", "bit_set", "bit_clear", "shift",

            // Step 5.7.A — generic flag-twiddle for LR35902 CCF
            // (toggle C). ARM has no direct equivalent so this stays
            // unused on the ARM7TDMI scan path.
            "toggle_flag",
        };

        var (registered, used) = CollectOps();
        var unused = registered.Except(used).Except(allowedUnused).OrderBy(x => x).ToList();
        Assert.True(unused.Count == 0,
            "Registered emitters never used by any spec instruction (likely dead code; " +
            "add to allowedUnused with justification if intentional): " +
            string.Join(", ", unused));
    }

    [Theory]
    // Baselines are floors, not exact counts — set just below current to
    // catch regressions while tolerating small consolidations. Current
    // (Phase 2.5 closeout): ARM 44, Thumb ~30.
    [InlineData("ARM",   42)]
    [InlineData("Thumb", 28)]
    public void Spec_MeetsMnemonicCoverageBaseline(string instructionSetName, int minDistinctMnemonics)
    {
        var loaded = SpecLoader.LoadCpuSpec(CpuJson);
        if (!loaded.InstructionSets.TryGetValue(instructionSetName, out var set))
            throw new Xunit.Sdk.XunitException(
                $"Instruction set '{instructionSetName}' not found in spec.");

        var distinct = set.EncodingGroups
            .SelectMany(g => g.Formats)
            .SelectMany(f => f.Instructions)
            .Select(i => i.Mnemonic)
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(distinct.Count >= minDistinctMnemonics,
            $"{instructionSetName} has only {distinct.Count} distinct mnemonics; " +
            $"baseline is {minDistinctMnemonics}. Mnemonics: {string.Join(", ", distinct.OrderBy(x => x))}");
    }

    // ---- helpers ----

    private static (HashSet<string> registered, HashSet<string> used) CollectOps()
    {
        var loaded = SpecLoader.LoadCpuSpec(CpuJson);

        // Mirror SpecCompiler's registry construction so we measure exactly
        // what the compiler will see at emit time.
        var registry = new EmitterRegistry();
        StandardEmitters.RegisterAll(registry);
        if (string.Equals(loaded.Cpu.Architecture.Family, "ARM", StringComparison.OrdinalIgnoreCase))
            ArmEmitters.RegisterAll(registry);
        var registered = new HashSet<string>(registry.RegisteredOpNames, StringComparer.Ordinal);

        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, set) in loaded.InstructionSets)
            foreach (var grp in set.EncodingGroups)
                foreach (var fmt in grp.Formats)
                    foreach (var ins in fmt.Instructions)
                        foreach (var step in ins.Steps)
                            CollectStepOps(step.Raw, used);

        return (registered, used);
    }

    private static void CollectStepOps(JsonElement step, HashSet<string> sink)
    {
        if (step.ValueKind != JsonValueKind.Object) return;
        if (step.TryGetProperty("op", out var opEl) && opEl.ValueKind == JsonValueKind.String)
            sink.Add(opEl.GetString()!);

        // Recurse into nested step arrays under "then" / "else" (used by
        // `if` and `if_arm_cond`).
        foreach (var nest in new[] { "then", "else" })
            if (step.TryGetProperty(nest, out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var inner in arr.EnumerateArray())
                    CollectStepOps(inner, sink);
    }
}
