using AprCpu.Core.Compilation;
using Xunit;

namespace AprCpu.Tests;

public class SpecCompilerTests
{
    private static string CpuJson => Path.Combine(TestPaths.SpecRoot, "arm7tdmi", "cpu.json");

    [Fact]
    public void Compile_ProducesFunctionsForArmAndThumbInstructions()
    {
        var result = SpecCompiler.Compile(CpuJson);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(2, result.DecoderTables.Count);

        // Phase 2.5.2a: full set of 16 ARM Data Processing Immediate ALU ops
        var allArmAlu = new[]
        {
            "AND", "EOR", "SUB", "RSB", "ADD", "ADC", "SBC", "RSC",
            "TST", "TEQ", "CMP", "CMN", "ORR", "MOV", "BIC", "MVN"
        };
        foreach (var mnemonic in allArmAlu)
        {
            Assert.Contains($"ARM.DataProcessing_Immediate.{mnemonic}", result.Functions.Keys);
        }

        Assert.Contains("ARM.Branch_BL.B",                result.Functions.Keys);
        Assert.Contains("ARM.Branch_BL.BL",               result.Functions.Keys);
        Assert.Contains("ARM.Branch_Exchange.BX",         result.Functions.Keys);
        Assert.Contains("Thumb.Thumb_F1_MoveShiftedRegister.LSL", result.Functions.Keys);
        Assert.Contains("Thumb.Thumb_F3_ImmediateOps.ADD",        result.Functions.Keys);
        Assert.Contains("Thumb.Thumb_F18_B.B",                    result.Functions.Keys);
    }

    [Fact]
    public void Compile_EmittedIRContainsExpectedShapeForArmAdd()
    {
        var result = SpecCompiler.Compile(CpuJson);
        var ir = result.Module.PrintToString();

        Assert.Contains("define void @Execute_ARM_DataProcessing_Immediate_ADD",  ir);
        Assert.Contains("lshr i32",  ir);  // field extraction
        Assert.Contains("add i32",   ir);  // arithmetic
        Assert.Contains("store i32", ir);  // write_reg
    }

    [Fact]
    public void Compile_EmittedIRForThumbLslContainsShl()
    {
        var result = SpecCompiler.Compile(CpuJson);
        var ir = result.Module.PrintToString();

        Assert.Contains("define void @Execute_Thumb_Thumb_F1_MoveShiftedRegister_LSL", ir);
        Assert.Contains("shl i32", ir);
    }

    [Fact]
    public void Compile_EmittedIRForSwiContainsExceptionEntrySequence()
    {
        // 2.5.7b: SWI must save CPSR -> SPSR_Supervisor, save next-PC -> banked
        // R14_Supervisor, switch CPSR.M to Supervisor (10011 = 0x13), set CPSR.I,
        // call host_swap_register_bank, and store the SWI vector address (0x8) to PC.
        var result = SpecCompiler.Compile(CpuJson);
        var ir = result.Module.PrintToString();

        // Phase 3.1.b: extern is now an indirect call through a global ptr
        // (MCJIT can't reliably bind function decls in LLVM 20).
        Assert.Contains("@host_swap_register_bank = external global ptr", ir);
        // The SWI emit body must load the swap-fn slot, call through it, and store the vector address.
        var swiFnIdx = ir.IndexOf("@Execute_ARM_SWI_SWI", StringComparison.Ordinal);
        Assert.True(swiFnIdx >= 0, "SWI function should be emitted");
        var nextDefine = ir.IndexOf("\ndefine ", swiFnIdx + 1, StringComparison.Ordinal);
        var swiBody = nextDefine > 0 ? ir.Substring(swiFnIdx, nextDefine - swiFnIdx) : ir.Substring(swiFnIdx);

        Assert.Contains("load ptr, ptr @host_swap_register_bank", swiBody);
        Assert.Contains("call void %swap_fn(", swiBody);
        Assert.Contains("store i32 8,", swiBody);          // PC := 0x8 (SoftwareInterrupt vector)
        Assert.Contains("cpsr_with_new_mode", swiBody);    // mode bits replaced
        Assert.Contains("cpsr_disable_i", swiBody);        // I bit set per spec disable=["I"]
    }

    [Fact]
    public void Compile_EmittedIRForSubsContainsRestoreCpsrSwitch()
    {
        // 2.5.7c: restore_cpsr_from_spsr lowers to a switch over CPSR.M[4:0]
        // with one case per banked-SPSR mode (FIQ/IRQ/Supervisor/Abort/Undefined),
        // a PHI to merge the chosen SPSR (or oldCpsr in default), and a swap call.
        var result = SpecCompiler.Compile(CpuJson);
        var ir = result.Module.PrintToString();

        // restore is wired into ALU bodies via the if-S-and-Rd=PC path —
        // SUBS Register-shifted is one such function.
        var fnIdx = ir.IndexOf("@Execute_ARM_DataProcessing_Immediate_SUB", StringComparison.Ordinal);
        Assert.True(fnIdx >= 0, "SUB function should be emitted");
        var nextDefine = ir.IndexOf("\ndefine ", fnIdx + 1, StringComparison.Ordinal);
        var body = nextDefine > 0 ? ir.Substring(fnIdx, nextDefine - fnIdx) : ir.Substring(fnIdx);

        Assert.Contains("restore_cpsr_merge", body);
        Assert.Contains("restore_cpsr_default", body);
        Assert.Contains("restore_cpsr_from_spsr_supervisor", body);
        Assert.Contains("restore_cpsr_from_spsr_fiq", body);
        Assert.Contains("phi i32", body);
        Assert.Contains("call void %swap_fn(", body);
    }

    [Fact]
    public void CompileToFile_WritesValidLlFile()
    {
        var temp = Path.Combine(TestPaths.RepoRoot, "temp", "spec_compiler_test.ll");
        if (File.Exists(temp)) File.Delete(temp);

        var result = SpecCompiler.CompileToFile(CpuJson, temp);

        Assert.True(File.Exists(temp));
        var content = File.ReadAllText(temp);
        Assert.Contains("define void @Execute_ARM_DataProcessing_Immediate_ADD", content);
        Assert.Empty(result.Diagnostics);
    }

    // ---------------- LR35902 (Phase 4.5C) ----------------

    private static string Lr35902CpuJson => Path.Combine(TestPaths.SpecRoot, "lr35902", "cpu.json");

    /// <summary>
    /// Baseline: the spec loads, the module builds, and the trivial "no-step"
    /// instructions (NOP and HALT-like) emit even before LR35902 emitters
    /// land. Functions whose steps reference unimplemented micro-ops are
    /// captured as diagnostics, not exceptions.
    /// </summary>
    [Fact]
    public void Compile_Lr35902_LoadsAndEmitsAtLeastNop()
    {
        var result = SpecCompiler.Compile(Lr35902CpuJson);

        Assert.Equal(2, result.DecoderTables.Count);
        Assert.True(result.DecoderTables.ContainsKey("Main"));
        Assert.True(result.DecoderTables.ContainsKey("CB"));

        // NOP has empty steps[], so it compiles to bare entry+ret without
        // any emitter being needed.
        Assert.Contains("Main.Nop.NOP", result.Functions.Keys);

        // The IR text should declare the function.
        var ir = result.Module.PrintToString();
        Assert.Contains("define void @Execute_Main_Nop_NOP", ir);
    }

    /// <summary>
    /// First emitter wave: the F-flag-only ops (SCF, CCF, CPL) compile, plus
    /// HALT/STOP which currently lower to no-op placeholders. Together with
    /// NOP these constitute the smallest set that exercises the full
    /// SpecCompiler path for an 8-bit CPU.
    /// </summary>
    [Fact]
    public void Compile_Lr35902_FlagOnlyOpsCompile()
    {
        var result = SpecCompiler.Compile(Lr35902CpuJson);

        Assert.Contains("Main.Scf.SCF", result.Functions.Keys);
        Assert.Contains("Main.Ccf.CCF", result.Functions.Keys);
        Assert.Contains("Main.Cpl.CPL", result.Functions.Keys);
        Assert.Contains("Main.Halt.HALT", result.Functions.Keys);
        Assert.Contains("Main.Stop.STOP", result.Functions.Keys);

        var ir = result.Module.PrintToString();
        Assert.Contains("define void @Execute_Main_Scf_SCF", ir);
        Assert.Contains("define void @Execute_Main_Ccf_CCF", ir);
        Assert.Contains("define void @Execute_Main_Cpl_CPL", ir);

        // CPL must read A, invert it, and write it back. The bit pattern
        // 0xFF appears as the XOR mask for the inversion.
        var cplFnIdx = ir.IndexOf("@Execute_Main_Cpl_CPL", StringComparison.Ordinal);
        Assert.True(cplFnIdx >= 0);
        var nextDefine = ir.IndexOf("\ndefine ", cplFnIdx + 1, StringComparison.Ordinal);
        var cplBody = nextDefine > 0 ? ir.Substring(cplFnIdx, nextDefine - cplFnIdx) : ir.Substring(cplFnIdx);
        Assert.Contains("xor i8", cplBody);     // A = ~A
        Assert.Contains("a_old",  cplBody);     // load A first
        Assert.Contains("f_new",  cplBody);     // F write
    }

    /// <summary>
    /// Block-0 col-7 A-rotates (RLCA/RRCA/RLA/RRA) compile and the IR
    /// contains the expected shl/lshr i8 sequence.
    /// </summary>
    [Fact]
    public void Compile_Lr35902_ARotatesCompile()
    {
        var result = SpecCompiler.Compile(Lr35902CpuJson);

        Assert.Contains("Main.Rlca.RLCA", result.Functions.Keys);
        Assert.Contains("Main.Rrca.RRCA", result.Functions.Keys);
        Assert.Contains("Main.Rla.RLA",   result.Functions.Keys);
        Assert.Contains("Main.Rra.RRA",   result.Functions.Keys);

        var ir = result.Module.PrintToString();
        // Each rotate body should have an i8 shift.
        var rlcaIdx = ir.IndexOf("@Execute_Main_Rlca_RLCA", StringComparison.Ordinal);
        Assert.True(rlcaIdx >= 0);
        var nextDef = ir.IndexOf("\ndefine ", rlcaIdx + 1, StringComparison.Ordinal);
        var rlcaBody = nextDef > 0 ? ir.Substring(rlcaIdx, nextDef - rlcaIdx) : ir.Substring(rlcaIdx);
        Assert.Contains("shl i8",  rlcaBody);
        Assert.Contains("lshr i8", rlcaBody);
    }

    /// <summary>
    /// Track JsonCpu compilation coverage: how many of LR35902's instructions
    /// produce a function vs. how many surface as "no emitter" diagnostics.
    /// As more emitters land, the diagnostic count drops; this test merely
    /// records the current threshold so a regression (an emitter accidentally
    /// removed) shows up.
    /// </summary>
    [Fact]
    public void Compile_Lr35902_CoverageBaseline()
    {
        var result = SpecCompiler.Compile(Lr35902CpuJson);

        // Every function listed here is one that successfully emitted IR.
        // Use this number as a baseline; assert it doesn't regress below
        // the current set of emitters (NOP + 5 flag/halt-style + every
        // CB-prefix instruction whose steps reference no extant emitter
        // would also count if added).
        var compiled = result.Functions.Count;
        var failed = result.Diagnostics.Count(d => d.Contains("emission failed"));

        // Print to test output so this test doubles as a coverage report.
        var byMnemonic = result.Functions.Keys
            .Select(k => k.Split('.').Last())
            .GroupBy(m => m)
            .OrderBy(g => g.Key)
            .ToList();
        var compiledMnemonics = string.Join(", ", byMnemonic.Select(g => $"{g.Key}×{g.Count()}"));
        var sampleFailures = string.Join(" || ", result.Diagnostics.Take(3));

        Assert.True(compiled >= 6,
            $"At least NOP+SCF+CCF+CPL+HALT+STOP should compile (got {compiled}). " +
            $"Compiled mnemonics: {compiledMnemonics}. " +
            $"Sample failures: {sampleFailures}");
        Assert.True(compiled + failed >= 100,
            $"Spec should account for ≥100 instructions total (compiled+failed got {compiled}+{failed} = {compiled + failed}).");
    }
}
