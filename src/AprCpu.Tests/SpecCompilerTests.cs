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

        Assert.Contains("ARM.DataProcessing_Immediate.MOV", result.Functions.Keys);
        Assert.Contains("ARM.DataProcessing_Immediate.ADD", result.Functions.Keys);
        Assert.Contains("ARM.DataProcessing_Immediate.SUB", result.Functions.Keys);
        Assert.Contains("ARM.DataProcessing_Immediate.CMP", result.Functions.Keys);
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
