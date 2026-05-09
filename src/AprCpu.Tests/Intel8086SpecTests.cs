using AprCpu.Core.Compilation;
using AprCpu.Core.JsonSpec;
using Xunit;

namespace AprCpu.Tests;

/// <summary>
/// Intel 8086 spec validation — Phase 24.6 sub-step gate. Verifies that
/// the spec compiles end-to-end through SpecCompiler with the new
/// X86_16Emitters bundle, and that the smoke group's HLT instruction
/// produces a real LLVM function (not a "no emitter registered" failure).
///
/// As later sub-phases (24.6.5 MOV, 24.6.6 ALU, ...) land, this file
/// gains targeted assertions: per-group function counts, expected
/// emitter ops being registered, etc. The full Tom Harte SST sweep
/// runs separately under X86JsonCpu in 24.6.5+.
/// </summary>
public class Intel8086SpecTests
{
    private static string CpuJsonPath =>
        Path.Combine(TestPaths.SpecRoot, "x86-16", "i8086", "cpu.json");

    [Fact]
    public void LoadsCpuSpec_Architecture_IsIntel8086()
    {
        var loaded = SpecLoader.LoadCpuSpec(CpuJsonPath);
        Assert.Equal("Intel8086", loaded.Cpu.Architecture.Id);
        Assert.Equal("x86-16",    loaded.Cpu.Architecture.Family);
        Assert.Equal(16,          loaded.Cpu.Architecture.WordSizeBits);
        Assert.Equal("little",    loaded.Cpu.Architecture.Endianness);
    }

    [Fact]
    public void LoadsCpuSpec_RegisterFile_Has8GprsInModRmOrder()
    {
        var loaded = SpecLoader.LoadCpuSpec(CpuJsonPath);
        var gpr = loaded.Cpu.RegisterFile.GeneralPurpose;
        Assert.Equal(8,  gpr.Count);
        Assert.Equal(16, gpr.WidthBits);
        Assert.Equal(new[] { "AX", "CX", "DX", "BX", "SP", "BP", "SI", "DI" }, gpr.Names);
    }

    [Fact]
    public void LoadsCpuSpec_StatusRegisters_FlagsSegmentsIp_AllDeclared()
    {
        var loaded = SpecLoader.LoadCpuSpec(CpuJsonPath);
        var status = loaded.Cpu.RegisterFile.Status;

        // FLAGS, IP, ES, CS, SS, DS, HALTED — at least these must be present.
        foreach (var name in new[] { "FLAGS", "IP", "ES", "CS", "SS", "DS", "HALTED" })
        {
            Assert.Contains(status, s => s.Name == name);
        }

        // FLAGS layout — 9 architectural 8086 flags at canonical bit positions.
        var flags = status.First(s => s.Name == "FLAGS");
        Assert.Equal(16, flags.WidthBits);
        foreach (var f in new[] { "CF", "PF", "AF", "ZF", "SF", "TF", "IF", "DF", "OF" })
        {
            Assert.True(flags.Fields.ContainsKey(f), $"FLAGS.{f} flag missing");
        }
    }

    [Fact]
    public void Compile_SmokeGroup_NopAndHltFunctionsExist()
    {
        var compiled = SpecCompiler.Compile(CpuJsonPath);

        // Diagnostics should be clean — every step opcode in the smoke group
        // must have a registered emitter (NOP has no steps, HLT has x86_halt).
        var bad = compiled.Diagnostics
            .Where(d => !d.StartsWith("[warn]"))
            .ToList();
        Assert.True(bad.Count == 0, "compile diagnostics: " + string.Join(" | ", bad));

        Assert.True(compiled.DecoderTables.ContainsKey("Main"));
        Assert.True(compiled.Functions.ContainsKey("Main.Nop.NOP"),
            "expected NOP function; got: " + string.Join(",", compiled.Functions.Keys));
        Assert.True(compiled.Functions.ContainsKey("Main.Hlt.HLT"),
            "expected HLT function; got: " + string.Join(",", compiled.Functions.Keys));
    }

    [Fact]
    public void Compile_RegistersX86HaltEmitter()
    {
        var compiled = SpecCompiler.Compile(CpuJsonPath);
        Assert.Contains("x86_halt", compiled.EmitterRegistry.RegisteredOpNames);
    }
}
