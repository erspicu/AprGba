using AprCpu.Core.Compilation;
using AprCpu.Core.Decoder;
using AprCpu.Core.JsonSpec;
using Xunit;

namespace AprCpu.Tests;

/// <summary>
/// 2A03 (NES Ricoh 6502 variant) spec validation. N0 milestone — verifies
/// that the bit-pattern-grouped spec correctly decodes all 256 8-bit
/// opcodes to a non-null instruction definition with the expected
/// mnemonic. This validates the spec's structural correctness without
/// requiring full execution wiring.
///
/// Spec design philosophy: instead of enumerating 256 opcode entries,
/// the 6502 'aaa bbb cc' encoding is compressed into ~30 format groups
/// across 8 group files (alu-cc01, rmw-cc10, ctrl-cc00, branches,
/// transfer-and-flags, stack-and-jump, unique-opcodes, unofficial).
/// This test verifies the compression doesn't lose decode coverage.
/// </summary>
public class Ricoh2A03SpecTests
{
    private static string CpuJsonPath =>
        Path.Combine(TestPaths.CpuSpecRoot, "2a03", "cpu.json");

    [Fact]
    public void LoadsCpuSpec_Architecture_IsRicoh2A03()
    {
        var loaded = SpecLoader.LoadCpuSpec(CpuJsonPath);
        Assert.Equal("Ricoh2A03", loaded.Cpu.Architecture.Id);
        Assert.Equal("MOS6502", loaded.Cpu.Architecture.Family);
        Assert.Equal(8, loaded.Cpu.Architecture.WordSizeBits);
        Assert.Equal("little", loaded.Cpu.Architecture.Endianness);
    }

    [Fact]
    public void LoadsCpuSpec_RegisterFile_HasThreeGprs_AXY()
    {
        var loaded = SpecLoader.LoadCpuSpec(CpuJsonPath);
        var rf = loaded.Cpu.RegisterFile;
        Assert.Equal(3, rf.GeneralPurpose.Count);
        Assert.Equal(8, rf.GeneralPurpose.WidthBits);
        Assert.Equal(new[] { "A", "X", "Y" }, rf.GeneralPurpose.Names);
    }

    [Fact]
    public void LoadsCpuSpec_StatusRegisters_PSpPc_AllDeclared()
    {
        var loaded = SpecLoader.LoadCpuSpec(CpuJsonPath);
        var status = loaded.Cpu.RegisterFile.Status;
        Assert.Contains(status, s => s.Name == "P");
        Assert.Contains(status, s => s.Name == "SP");
        Assert.Contains(status, s => s.Name == "PC");

        var p = status.First(s => s.Name == "P");
        Assert.Equal(8, p.WidthBits);
        // 6502 status flags: NV-BDIZC (bit 5 unused = U)
        foreach (var flag in new[] { "C", "Z", "I", "D", "B", "U", "V", "N" })
            Assert.True(p.Fields.ContainsKey(flag), $"P.{flag} flag missing");
    }

    [Fact]
    public void LoadsCpuSpec_ExceptionVectors_NmiResetIrq()
    {
        var loaded = SpecLoader.LoadCpuSpec(CpuJsonPath);
        var vectors = loaded.Cpu.ExceptionVectors;
        Assert.Contains(vectors, v => v.Name == "NMI"   && v.Address == 0xFFFAu);
        Assert.Contains(vectors, v => v.Address == 0xFFFCu); // RESET
        Assert.Contains(vectors, v => v.Name == "IRQ"   && v.Address == 0xFFFEu);
    }

    [Fact]
    public void DecoderTable_Constructs_NoErrors()
    {
        var compiled = SpecCompiler.Compile(CpuJsonPath);
        Assert.True(
            compiled.DecoderTables.ContainsKey("Main"),
            "diagnostics: " + string.Join(" | ", compiled.Diagnostics));
        Assert.NotEmpty(compiled.DecoderTables["Main"].Formats);
    }

    /// <summary>
    /// Core N0 verification: every opcode 0x00-0xFF resolves to a
    /// non-null DecodedInstruction. This is the structural correctness
    /// check that the bit-pattern groups + selectors + mask/match
    /// priority correctly cover the entire 8-bit opcode space.
    /// </summary>
    [Fact]
    public void Decoder_Resolves_All_256_Opcodes()
    {
        var compiled = SpecCompiler.Compile(CpuJsonPath);
        var main = compiled.DecoderTables["Main"];

        var undecoded = new List<int>();
        for (int op = 0; op < 256; op++)
        {
            var decoded = main.Decode((uint)op);
            if (decoded is null) undecoded.Add(op);
        }
        Assert.True(undecoded.Count == 0,
            "undecoded opcodes: " + string.Join(", ", undecoded.Select(o => $"0x{o:X2}")));
    }

    /// <summary>
    /// Spot-check specific opcodes to verify decode correctness — not just
    /// "any" instruction, but the RIGHT one per the 6502 reference.
    /// Covers each opcode class (cc=00/01/10/11), branch family, and
    /// individual unique opcodes (BRK, JSR, NOP, etc.).
    /// </summary>
    [Theory]
    [InlineData(0x00, "BRK")]    // unique mask 0xFF
    [InlineData(0xEA, "NOP")]    // unique mask 0xFF
    [InlineData(0x18, "CLC")]    // transfer-and-flags
    [InlineData(0x38, "SEC")]
    [InlineData(0xAA, "TAX")]
    [InlineData(0xCA, "DEX")]
    [InlineData(0x48, "PHA")]    // stack-and-jump
    [InlineData(0x60, "RTS")]
    [InlineData(0x4C, "JMP")]
    [InlineData(0x6C, "JMP_IND")]
    [InlineData(0x10, "BPL")]    // branches
    [InlineData(0xF0, "BEQ")]
    [InlineData(0x69, "ADC")]    // cc=01 ALU class
    [InlineData(0x29, "AND")]
    [InlineData(0xA9, "LDA")]
    [InlineData(0x85, "STA")]    // STA zp
    [InlineData(0xC9, "CMP")]
    [InlineData(0xE9, "SBC")]
    [InlineData(0x0A, "ASL")]    // cc=10 RMW (accumulator)
    [InlineData(0xA2, "LDX")]
    [InlineData(0x86, "STX")]
    [InlineData(0xCE, "DEC")]    // DEC abs
    [InlineData(0xEE, "INC")]
    [InlineData(0x24, "BIT")]    // cc=00 ctrl
    [InlineData(0xA0, "LDY")]
    [InlineData(0x84, "STY")]
    [InlineData(0xC0, "CPY")]
    [InlineData(0xE0, "CPX")]
    // Unofficial opcodes
    [InlineData(0x02, "KIL")]    // KIL/STP/JAM
    [InlineData(0xF2, "KIL")]
    [InlineData(0x1A, "NOP")]    // implied unofficial NOP
    [InlineData(0x80, "NOP")]    // imm unofficial NOP
    [InlineData(0x89, "NOP")]    // STA-#imm = unofficial NOP
    [InlineData(0x04, "NOP")]    // zp NOP
    [InlineData(0x0C, "NOP")]    // abs NOP
    [InlineData(0x07, "SLO")]    // cc=11 unofficial
    [InlineData(0x27, "RLA")]
    [InlineData(0x47, "SRE")]
    [InlineData(0x67, "RRA")]
    [InlineData(0x87, "SAX")]
    [InlineData(0xA7, "LAX")]
    [InlineData(0xC7, "DCP")]
    [InlineData(0xE7, "ISC")]
    // Irregular unofficial (mask 0xFF shadows of cc=11)
    [InlineData(0x0B, "ANC")]
    [InlineData(0x4B, "ALR")]
    [InlineData(0x6B, "ARR")]
    [InlineData(0x8B, "XAA")]
    [InlineData(0x9B, "TAS")]
    [InlineData(0xCB, "AXS")]
    [InlineData(0xEB, "SBC")]    // SBC #imm duplicate
    public void Decoder_ResolvesOpcode_ToExpectedMnemonic(int opcode, string expectedMnemonic)
    {
        var compiled = SpecCompiler.Compile(CpuJsonPath);
        var main = compiled.DecoderTables["Main"];
        var decoded = main.Decode((uint)opcode);
        Assert.NotNull(decoded);
        Assert.Equal(expectedMnemonic, decoded!.Instruction.Mnemonic);
    }
}
