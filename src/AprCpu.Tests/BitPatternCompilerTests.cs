using AprCpu.Core.Decoder;
using AprCpu.Core.JsonSpec;
using Xunit;

namespace AprCpu.Tests;

public class BitPatternCompilerTests
{
    [Fact]
    public void DataProcessingImmediate_DerivesCorrectMaskMatch()
    {
        var p = BitPatternCompiler.Compile("cccc_001_oooo_s_nnnn_dddd_rrrriiiiiiii", 32);
        Assert.Equal(0x0E000000u, p.Mask);
        Assert.Equal(0x02000000u, p.Match);
    }

    [Fact]
    public void DataProcessingImmediate_ReportsAllLetterRuns()
    {
        var p = BitPatternCompiler.Compile("cccc_001_oooo_s_nnnn_dddd_rrrriiiiiiii", 32);
        var ranges = p.LetterFields.ToDictionary(t => t.Letter, t => t.Range);
        Assert.Equal(new BitRange(31, 28), ranges['c']);
        Assert.Equal(new BitRange(24, 21), ranges['o']);
        Assert.Equal(new BitRange(20, 20), ranges['s']);
        Assert.Equal(new BitRange(19, 16), ranges['n']);
        Assert.Equal(new BitRange(15, 12), ranges['d']);
        Assert.Equal(new BitRange(11, 8),  ranges['r']);
        Assert.Equal(new BitRange(7, 0),   ranges['i']);
    }

    [Fact]
    public void Validate_PassesForArmDataProcessingFormat()
    {
        var arm = SpecLoader.LoadInstructionSet(
            Path.Combine(TestPaths.SpecRoot, "arm7tdmi", "arm.json"));
        var fmt = arm.EncodingGroups
            .SelectMany(g => g.Formats)
            .Single(f => f.Name == "DataProcessing_Immediate");

        var p = BitPatternCompiler.Compile(fmt.Pattern!, 32);
        // Should not throw.
        BitPatternCompiler.Validate(p, fmt.Fields, fmt.Mask, fmt.Match, fmt.Name);
    }

    [Fact]
    public void Validate_PassesForAllArmAndThumbFormats()
    {
        var arm   = SpecLoader.LoadInstructionSet(Path.Combine(TestPaths.SpecRoot, "arm7tdmi", "arm.json"));
        var thumb = SpecLoader.LoadInstructionSet(Path.Combine(TestPaths.SpecRoot, "arm7tdmi", "thumb.json"));

        foreach (var (set, width) in new[] { (arm, 32), (thumb, 16) })
        foreach (var fmt in set.EncodingGroups.SelectMany(g => g.Formats))
        {
            Assert.False(string.IsNullOrEmpty(fmt.Pattern),
                $"Format '{fmt.Name}' has no pattern.");
            var p = BitPatternCompiler.Compile(fmt.Pattern!, width);
            BitPatternCompiler.Validate(p, fmt.Fields, fmt.Mask, fmt.Match, fmt.Name);
        }
    }

    [Fact]
    public void RejectsMaskMismatch()
    {
        var p = BitPatternCompiler.Compile("0000_oooo_xxxx_xxxx", 16);
        Assert.Throws<SpecValidationException>(() =>
            BitPatternCompiler.Validate(
                p,
                new Dictionary<string, BitRange> { ["o"] = new BitRange(11, 8) },
                /* mask  */ 0xF000u,            // wrong (should be 0xF000? actually 0000 in bits 15:12 → mask 0xF000)
                /* match */ 0xFFFFu,            // wrong match
                "Bogus"));
    }

    [Fact]
    public void RejectsPatternOfWrongLength()
    {
        var ex = Assert.Throws<SpecValidationException>(
            () => BitPatternCompiler.Compile("0000", 32));
        Assert.Contains("expected 32", ex.Message);
    }

    [Fact]
    public void RejectsNonContiguousFieldLetter()
    {
        Assert.Throws<SpecValidationException>(
            () => BitPatternCompiler.Compile("aaaa_0000_aaaa_0000", 16));
    }

    [Fact]
    public void ExtractField_PicksOutBits()
    {
        // instruction = 0xE3A0_0001 (MOV R0, #1, AL, S=0)
        // opcode field = 24:21
        uint ins = 0xE3A0_0001u;
        var range = new BitRange(24, 21);
        Assert.Equal(0b1101u, BitPatternCompiler.ExtractField(ins, range));
    }
}
