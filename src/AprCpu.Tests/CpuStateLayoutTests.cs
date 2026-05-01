using AprCpu.Core.IR;
using AprCpu.Core.JsonSpec;
using LLVMSharp.Interop;
using Xunit;

namespace AprCpu.Tests;

/// <summary>
/// R1: verify CpuStateLayout is fully driven by the spec's RegisterFile +
/// ProcessorModes (not hardcoded ARM7TDMI). These tests construct a
/// minimal "fake CPU" spec and check that the layout adapts.
/// </summary>
public class CpuStateLayoutTests
{
    [Fact]
    public unsafe void Layout_AdaptsToCustomGprCount()
    {
        var spec = MakeMinimalRegisterFile(gprCount: 8, gprWidthBits: 32);
        var ctx = LLVMContextRef.Create();
        var layout = new CpuStateLayout(ctx, spec, processorModes: null);

        Assert.Equal(8,  layout.GprCount);
        Assert.Equal(32, layout.GprWidthBits);

        // GPR 7 is valid; GPR 8 throws.
        var dummyBuilder = ctx.CreateBuilder();
        var fnType = LLVMTypeRef.CreateFunction(ctx.VoidType, new[] { layout.PointerType });
        using var module = ctx.CreateModuleWithName("t");
        var fn = module.AddFunction("fn", fnType);
        var entry = fn.AppendBasicBlock("e");
        dummyBuilder.PositionAtEnd(entry);

        // No throw for valid index.
        layout.GepGpr(dummyBuilder, fn.GetParam(0), 7);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => layout.GepGpr(dummyBuilder, fn.GetParam(0), 8));
    }

    [Fact]
    public void Layout_LooksUpStatusRegistersByName()
    {
        var spec = MakeMinimalRegisterFile(
            gprCount: 4,
            gprWidthBits: 32,
            statusRegisters: new[]
            {
                MakeStatus("FLAGS", widthBits: 8, fields: new() { ["N"] = "7", ["Z"] = "6", ["C"] = "0" }),
            });
        var ctx = LLVMContextRef.Create();
        var layout = new CpuStateLayout(ctx, spec, null);

        var status = layout.GetStatusRegisterDef("FLAGS");
        Assert.Equal("FLAGS", status.Name);
        Assert.Equal(8, status.WidthBits);

        Assert.Equal(7, layout.GetStatusFlagBitIndex("FLAGS", "N"));
        Assert.Equal(6, layout.GetStatusFlagBitIndex("FLAGS", "Z"));
        Assert.Equal(0, layout.GetStatusFlagBitIndex("FLAGS", "C"));
    }

    [Fact]
    public void Layout_ThrowsOnMissingStatusRegister()
    {
        var spec = MakeMinimalRegisterFile(gprCount: 4, gprWidthBits: 32);
        var ctx = LLVMContextRef.Create();
        var layout = new CpuStateLayout(ctx, spec, null);

        Assert.Throws<InvalidOperationException>(
            () => layout.GetStatusRegisterDef("NONEXISTENT"));
    }

    [Fact]
    public void Layout_ThrowsOnMultiBitFlag()
    {
        var spec = MakeMinimalRegisterFile(
            gprCount: 4, gprWidthBits: 32,
            statusRegisters: new[]
            {
                MakeStatus("CPSR", widthBits: 32, fields: new() { ["MODE"] = "4:0" }),
            });
        var ctx = LLVMContextRef.Create();
        var layout = new CpuStateLayout(ctx, spec, null);

        var ex = Assert.Throws<InvalidOperationException>(
            () => layout.GetStatusFlagBitIndex("CPSR", "MODE"));
        Assert.Contains("5 bits wide; expected 1", ex.Message);
    }

    [Fact]
    public void Layout_ConstructsForArm7Tdmi_WithExpectedFlagPositions()
    {
        var loaded = SpecLoader.LoadCpuSpec(
            Path.Combine(TestPaths.SpecRoot, "arm7tdmi", "cpu.json"));
        var ctx = LLVMContextRef.Create();
        var layout = new CpuStateLayout(ctx, loaded.Cpu.RegisterFile, loaded.Cpu.ProcessorModes);

        // Sanity: ARM CPSR flags resolve to canonical positions
        Assert.Equal(31, layout.GetStatusFlagBitIndex("CPSR", "N"));
        Assert.Equal(30, layout.GetStatusFlagBitIndex("CPSR", "Z"));
        Assert.Equal(29, layout.GetStatusFlagBitIndex("CPSR", "C"));
        Assert.Equal(28, layout.GetStatusFlagBitIndex("CPSR", "V"));
        Assert.Equal(7,  layout.GetStatusFlagBitIndex("CPSR", "I"));
        Assert.Equal(6,  layout.GetStatusFlagBitIndex("CPSR", "F"));
        Assert.Equal(5,  layout.GetStatusFlagBitIndex("CPSR", "T"));

        Assert.Equal(16, layout.GprCount);

        // Banked GPR groups are present.
        // FIQ: R8..R14 (7 entries)
        var dummyBuilder = ctx.CreateBuilder();
        using var module = ctx.CreateModuleWithName("t");
        var fnType = LLVMTypeRef.CreateFunction(ctx.VoidType, new[] { layout.PointerType });
        var fn = module.AddFunction("fn", fnType);
        var entry = fn.AppendBasicBlock("e");
        dummyBuilder.PositionAtEnd(entry);

        // Should not throw.
        layout.GepBankedGpr(dummyBuilder, fn.GetParam(0), "FIQ", 6);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => layout.GepBankedGpr(dummyBuilder, fn.GetParam(0), "FIQ", 7));
    }

    // ---------------- helpers ----------------

    private static RegisterFile MakeMinimalRegisterFile(
        int gprCount, int gprWidthBits,
        StatusRegister[]? statusRegisters = null)
    {
        var names = Enumerable.Range(0, gprCount).Select(i => $"R{i}").ToList();
        var gp = new GeneralPurposeRegisters(
            Count:      gprCount,
            WidthBits:  gprWidthBits,
            Names:      names,
            Aliases:    new Dictionary<string, string>(),
            PcIndex:    null);
        return new RegisterFile(gp, statusRegisters ?? Array.Empty<StatusRegister>());
    }

    private static StatusRegister MakeStatus(string name, int widthBits, Dictionary<string, string> fields)
    {
        var bitFields = fields.ToDictionary(kv => kv.Key, kv => BitRange.Parse(kv.Value));
        return new StatusRegister(
            Name:           name,
            WidthBits:      widthBits,
            Fields:         bitFields,
            BankedPerMode:  Array.Empty<string>());
    }
}
