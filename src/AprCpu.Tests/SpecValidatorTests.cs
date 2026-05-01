using AprCpu.Core.JsonSpec;
using Xunit;

namespace AprCpu.Tests;

public class SpecValidatorTests
{
    [Fact]
    public void ExistingArmAndThumbSpecs_PassValidationWithNoWarnings()
    {
        var arm   = SpecLoader.LoadInstructionSet(Path.Combine(TestPaths.SpecRoot, "arm7tdmi", "arm.json"));
        var thumb = SpecLoader.LoadInstructionSet(Path.Combine(TestPaths.SpecRoot, "arm7tdmi", "thumb.json"));

        Assert.Empty(SpecValidator.ValidateInstructionSet(arm));
        Assert.Empty(SpecValidator.ValidateInstructionSet(thumb));
    }

    [Fact]
    public void Detects_FieldOutOfBounds()
    {
        var fmt = MakeFormat(
            mask:  0x80000000u, match: 0x80000000u,
            fields: new() { ["bad"] = new BitRange(40, 32) },
            instructions: new[] { MakeInstruction("X", null) });
        var set = WrapSet("ARM", 32, fmt);

        var ex = Assert.Throws<SpecValidationException>(
            () => SpecValidator.ValidateInstructionSet(set));
        Assert.Contains("outside instruction width", ex.Message);
    }

    [Fact]
    public void Detects_OverlappingFields()
    {
        var fmt = MakeFormat(
            mask: 0u, match: 0u,
            fields: new()
            {
                ["a"] = new BitRange(15, 8),
                ["b"] = new BitRange(11, 4),  // overlaps a
            },
            instructions: new[] { MakeInstruction("X", null) });
        var set = WrapSet("ARM", 32, fmt);

        var ex = Assert.Throws<SpecValidationException>(
            () => SpecValidator.ValidateInstructionSet(set));
        Assert.Contains("overlap", ex.Message);
    }

    [Fact]
    public void Detects_SelectorValueWidthMismatch_Binary()
    {
        var fmt = MakeFormat(
            mask: 0u, match: 0u,
            fields: new() { ["opcode"] = new BitRange(24, 21) }, // 4 bits
            instructions: new[]
            {
                MakeInstruction("X",
                    new InstructionSelector("opcode", "10000")), // 5 bits — bad
            });
        var set = WrapSet("ARM", 32, fmt);

        var ex = Assert.Throws<SpecValidationException>(
            () => SpecValidator.ValidateInstructionSet(set));
        Assert.Contains("has 5 bits but field", ex.Message);
    }

    [Fact]
    public void Detects_SelectorValueOverflow_Numeric()
    {
        var fmt = MakeFormat(
            mask: 0u, match: 0u,
            fields: new() { ["op"] = new BitRange(7, 4) },     // 4 bits, max=15
            instructions: new[]
            {
                MakeInstruction("X",
                    new InstructionSelector("op", "16")),      // exceeds
            });
        var set = WrapSet("ARM", 32, fmt);

        var ex = Assert.Throws<SpecValidationException>(
            () => SpecValidator.ValidateInstructionSet(set));
        Assert.Contains("exceeds field", ex.Message);
    }

    [Fact]
    public void Detects_SelectorReferencesUnknownField()
    {
        var fmt = MakeFormat(
            mask: 0u, match: 0u,
            fields: new() { ["op"] = new BitRange(7, 4) },
            instructions: new[]
            {
                MakeInstruction("X",
                    new InstructionSelector("nonexistent", "0000")),
            });
        var set = WrapSet("ARM", 32, fmt);

        var ex = Assert.Throws<SpecValidationException>(
            () => SpecValidator.ValidateInstructionSet(set));
        Assert.Contains("unknown field", ex.Message);
    }

    [Fact]
    public void Warns_MultipleInstructionsButNoSelector()
    {
        var fmt = MakeFormat(
            mask: 0u, match: 0u,
            fields: new() { ["op"] = new BitRange(7, 4) },
            instructions: new[]
            {
                MakeInstruction("A", null),
                MakeInstruction("B", null),  // never matches
            });
        var set = WrapSet("ARM", 32, fmt);

        var warns = SpecValidator.ValidateInstructionSet(set);
        Assert.NotEmpty(warns);
        Assert.Contains("only the first selector-less one", warns[0]);
    }

    // ----- helpers -----

    private static EncodingFormat MakeFormat(
        uint mask, uint match,
        Dictionary<string, BitRange> fields,
        IEnumerable<InstructionDef> instructions)
    {
        return new EncodingFormat(
            Name:         "TestFormat",
            Comment:      null,
            Pattern:      null,    // bypass pattern compiler for these targeted tests
            Fields:       fields,
            Mask:         mask,
            Match:        match,
            Operands:     new Dictionary<string, OperandResolver>(),
            Instructions: instructions.ToList());
    }

    private static InstructionDef MakeInstruction(string mnemonic, InstructionSelector? selector)
    {
        return new InstructionDef(
            Selector:                selector,
            Mnemonic:                mnemonic,
            Since:                   null,
            Until:                   null,
            RequiresFeature:         null,
            Unconditional:           false,
            WritesPc:                "never",
            WritesMemory:            Array.Empty<string>(),
            ChangesMode:             false,
            SwitchesInstructionSet:  false,
            RequiresIoBarrier:       false,
            Quirks:                  Array.Empty<string>(),
            ManualRef:               null,
            Cycles:                  null,
            Steps:                   Array.Empty<MicroOpStep>());
    }

    private static InstructionSetSpec WrapSet(string name, int width, EncodingFormat fmt)
    {
        return new InstructionSetSpec(
            SpecVersion:        "1.0",
            Name:               name,
            WidthBits:          InstructionWidth.OfFixed(width),
            AlignmentBytes:     width / 8,
            PcOffsetBytes:      0,
            EndianWithinWord:   "little",
            GlobalCondition:    null,
            DecodeStrategy:     "mask_match_priority",
            WidthDecision:      null,
            EncodingGroups:     new[] { new EncodingGroup("g", null, new[] { fmt }) },
            Extends:            null,
            CustomMicroOps:     Array.Empty<CustomMicroOp>());
    }
}
