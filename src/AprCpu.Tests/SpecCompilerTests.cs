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
}
