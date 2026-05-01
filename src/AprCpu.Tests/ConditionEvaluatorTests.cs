using AprCpu.Core.Compilation;
using AprCpu.Core.JsonSpec;
using Xunit;

namespace AprCpu.Tests;

/// <summary>
/// R4: Verify that ConditionEvaluator handles the full 14-entry ARM cond
/// table by inspecting the emitted IR for the expected sub-expressions.
/// We don't yet JIT-execute the cond gate (Phase 3 territory); these tests
/// look at IR shape to confirm every cond is wired up.
/// </summary>
public class ConditionEvaluatorTests
{
    private static string CpuJson => Path.Combine(TestPaths.SpecRoot, "arm7tdmi", "cpu.json");

    [Fact]
    public void EmittedIR_ReferencesEachStandardCondMnemonic()
    {
        var result = SpecCompiler.Compile(CpuJson);
        var ir = result.Module.PrintToString();

        // ConditionEvaluator emits per-mnemonic chain selects with names
        // "chain_eq", "chain_ne", "chain_cs", ... — one per cond mnemonic
        // present in the spec's global_condition.table. ARM aliases like
        // HS/LO map to the same evaluator branch as CS/CC; only the
        // mnemonic actually listed in the table appears in the IR.
        var inSpecTable = new[]
        {
            "eq", "ne", "cs", "cc",
            "mi", "pl", "vs", "vc", "hi", "ls",
            "ge", "lt", "gt", "le", "al", "nv",
        };
        foreach (var mnemonic in inSpecTable)
        {
            Assert.Contains($"chain_{mnemonic}", ir);
        }
    }

    [Fact]
    public void EmittedIR_NoLongerHasHardcodedAlOnlyPath()
    {
        // The pre-R4 path emitted "is_always" and "cond_check" basic
        // blocks; with R4 those names should have disappeared in favour of
        // the unified "exec" + cond evaluator.
        var result = SpecCompiler.Compile(CpuJson);
        var ir = result.Module.PrintToString();
        Assert.DoesNotContain("is_always",  ir);
        Assert.DoesNotContain("cond_check", ir);
    }

    [Fact]
    public void EmittedIR_HasExpectedCondBuildingBlocks()
    {
        var result = SpecCompiler.Compile(CpuJson);
        var ir = result.Module.PrintToString();

        // Compound cond expressions use named temporaries
        Assert.Contains("cond_hi", ir);  // C AND NOT Z
        Assert.Contains("cond_ls", ir);  // NOT C OR Z
        Assert.Contains("cond_gt", ir);  // NOT Z AND (N == V)
        Assert.Contains("cond_le", ir);  // Z OR (N != V)
        Assert.Contains("n_eq_v",  ir);
        Assert.Contains("n_ne_v",  ir);
    }
}
