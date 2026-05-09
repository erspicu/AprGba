using AprCpu.Core.JsonSpec;
using Xunit;

namespace AprCpu.Tests;

public class SpecLoaderTests
{
    private static string SpecRoot => TestPaths.SpecRoot;

    [Fact]
    public void Loads_Arm7tdmi_CpuJson_AndAllReferencedSets()
    {
        var loaded = SpecLoader.LoadCpuSpec(Path.Combine(SpecRoot, "arm7tdmi", "cpu.json"));

        Assert.Equal("ARMv4T",   loaded.Cpu.Architecture.Id);
        Assert.Equal("ARM",      loaded.Cpu.Architecture.Family);
        Assert.Equal("little",   loaded.Cpu.Architecture.Endianness);
        Assert.Equal(32,         loaded.Cpu.Architecture.WordSizeBits);

        var v = Assert.Single(loaded.Cpu.Variants);
        Assert.Equal("ARM7TDMI", v.Id);
        Assert.Contains("T", v.Features);

        Assert.Equal(16, loaded.Cpu.RegisterFile.GeneralPurpose.Count);
        Assert.Equal("R13", loaded.Cpu.RegisterFile.GeneralPurpose.Aliases["SP"]);
        Assert.Equal(15, loaded.Cpu.RegisterFile.GeneralPurpose.PcIndex);

        Assert.NotNull(loaded.Cpu.ProcessorModes);
        Assert.Equal(7, loaded.Cpu.ProcessorModes!.Modes.Count);

        Assert.Equal(7, loaded.Cpu.ExceptionVectors.Count);
        Assert.Equal(0x18u, loaded.Cpu.ExceptionVectors.Single(v => v.Name == "IRQ").Address);

        Assert.Equal(2, loaded.InstructionSets.Count);
        Assert.True(loaded.InstructionSets.ContainsKey("ARM"));
        Assert.True(loaded.InstructionSets.ContainsKey("Thumb"));

        var arm = loaded.InstructionSets["ARM"];
        Assert.Equal(32, arm.WidthBits.Fixed);
        Assert.Equal(8,  arm.PcOffsetBytes);
        Assert.NotNull(arm.GlobalCondition);
        Assert.True(arm.GlobalCondition!.Table.Count >= 14);

        var thumb = loaded.InstructionSets["Thumb"];
        Assert.Equal(16, thumb.WidthBits.Fixed);
        Assert.Equal(4,  thumb.PcOffsetBytes);
    }

    [Fact]
    public void Throws_When_CpuJson_NotFound()
    {
        var ex = Assert.Throws<SpecValidationException>(
            () => SpecLoader.LoadCpuSpec(Path.Combine(SpecRoot, "does", "not", "exist.json")));
        Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ArmJson_DataProcessingImmediate_HasFourInstructions()
    {
        var arm = SpecLoader.LoadInstructionSet(Path.Combine(SpecRoot, "arm7tdmi", "arm.json"));

        var fmt = arm.EncodingGroups
            .SelectMany(g => g.Formats)
            .Single(f => f.Name == "DataProcessing_Immediate");

        Assert.Equal(0x0E000000u, fmt.Mask);
        Assert.Equal(0x02000000u, fmt.Match);
        Assert.Equal(7, fmt.Fields.Count);
        Assert.Equal(new BitRange(31, 28), fmt.Fields["cond"]);
        Assert.Equal(new BitRange(24, 21), fmt.Fields["opcode"]);

        var mnemonics = fmt.Instructions.Select(i => i.Mnemonic).ToList();
        Assert.Contains("MOV", mnemonics);
        Assert.Contains("ADD", mnemonics);
        Assert.Contains("SUB", mnemonics);
        Assert.Contains("CMP", mnemonics);
    }

    [Fact]
    public void ThumbJson_F1_DefinesShiftedRegisterMoves()
    {
        var thumb = SpecLoader.LoadInstructionSet(Path.Combine(SpecRoot, "arm7tdmi", "thumb.json"));

        var fmt = thumb.EncodingGroups
            .SelectMany(g => g.Formats)
            .Single(f => f.Name == "Thumb_F1_MoveShiftedRegister");

        Assert.Equal(0xE000u, fmt.Mask);
        Assert.Equal(0x0000u, fmt.Match);

        var mnemonics = fmt.Instructions.Select(i => i.Mnemonic).ToList();
        Assert.Contains("LSL", mnemonics);
        Assert.Contains("LSR", mnemonics);
        Assert.Contains("ASR", mnemonics);
    }

    [Fact]
    public void Loads_Lr35902_CpuJson_WithRegisterPairs()
    {
        var loaded = SpecLoader.LoadCpuSpec(Path.Combine(SpecRoot, "lr35902", "cpu.json"));

        Assert.Equal("LR35902",     loaded.Cpu.Architecture.Id);
        Assert.Equal("Sharp-SM83",  loaded.Cpu.Architecture.Family);
        Assert.Equal("little",      loaded.Cpu.Architecture.Endianness);
        Assert.Equal(8,             loaded.Cpu.Architecture.WordSizeBits);

        var gpr = loaded.Cpu.RegisterFile.GeneralPurpose;
        Assert.Equal(7,  gpr.Count);
        Assert.Equal(8,  gpr.WidthBits);
        Assert.Equal(new[] { "A", "B", "C", "D", "E", "H", "L" }, gpr.Names);

        var pairs = loaded.Cpu.RegisterFile.RegisterPairs;
        Assert.Equal(4, pairs.Count);
        var bc = pairs.Single(p => p.Name == "BC");
        Assert.Equal("B", bc.High);
        Assert.Equal("C", bc.Low);
        var af = pairs.Single(p => p.Name == "AF");
        Assert.Equal("A", af.High);
        Assert.Equal("F", af.Low);

        // F is the flag byte (8-bit), SP/PC are 16-bit specials.
        var status = loaded.Cpu.RegisterFile.Status;
        Assert.Equal(3, status.Count);
        var f = status.Single(s => s.Name == "F");
        Assert.Equal(8, f.WidthBits);
        Assert.Equal(new BitRange(7, 7), f.Fields["Z"]);
        Assert.Equal(new BitRange(4, 4), f.Fields["C"]);
        Assert.Equal(16, status.Single(s => s.Name == "SP").WidthBits);
        Assert.Equal(16, status.Single(s => s.Name == "PC").WidthBits);

        Assert.Equal(5, loaded.Cpu.ExceptionVectors.Count);
        Assert.Equal(0x50u, loaded.Cpu.ExceptionVectors.Single(v => v.Name == "Timer").Address);

        Assert.Equal(2, loaded.InstructionSets.Count);
        Assert.True(loaded.InstructionSets.ContainsKey("Main"));
        Assert.True(loaded.InstructionSets.ContainsKey("CB"));

        var main = loaded.InstructionSets["Main"];
        Assert.Equal(8, main.WidthBits.Fixed);

        var cb = loaded.InstructionSets["CB"];
        Assert.Equal(8, cb.WidthBits.Fixed);

        // Block 1 (LD r,r' + HALT) loaded.
        var formats = main.EncodingGroups.SelectMany(g => g.Formats).ToList();
        Assert.Contains(formats, f => f.Name == "Halt");
        Assert.Contains(formats, f => f.Name == "LdReg_Reg");
        Assert.Contains(formats, f => f.Name == "LdHlInd_Reg");
        Assert.Contains(formats, f => f.Name == "LdReg_HlInd");
    }

    /// <summary>
    /// 24.6.1 — 8086 spec scaffolding. Validates that the cpu.json + main.json
    /// pair parses with the expected register file (8 GPRs in ModR/M order),
    /// segment registers as status registers, and the 9-field FLAGS layout.
    /// No groups are populated yet (24.6.2 onwards); this just proves the
    /// framework's SpecLoader accepts the new architecture.
    /// </summary>
    [Fact]
    public void Loads_Intel8086_CpuJson_With9FlagsAndSegmentRegisters()
    {
        var loaded = SpecLoader.LoadCpuSpec(Path.Combine(SpecRoot, "x86-16", "i8086", "cpu.json"));

        Assert.Equal("Intel8086", loaded.Cpu.Architecture.Id);
        Assert.Equal("x86-16",    loaded.Cpu.Architecture.Family);
        Assert.Equal("little",    loaded.Cpu.Architecture.Endianness);
        Assert.Equal(16,          loaded.Cpu.Architecture.WordSizeBits);

        // 8 GPRs in ModR/M reg-field order: 000=AX..111=DI.
        var gpr = loaded.Cpu.RegisterFile.GeneralPurpose;
        Assert.Equal(8,  gpr.Count);
        Assert.Equal(16, gpr.WidthBits);
        Assert.Equal(new[] { "AX", "CX", "DX", "BX", "SP", "BP", "SI", "DI" }, gpr.Names);

        // 8-bit byte-half aliases (AL/AH/CL/CH/DL/DH/BL/BH) carried as informational strings.
        Assert.Equal(8, gpr.Aliases.Count);
        Assert.Equal("AX[7:0]",  gpr.Aliases["AL"]);
        Assert.Equal("AX[15:8]", gpr.Aliases["AH"]);

        // FLAGS register has all 9 architectural 8086 flags at the canonical bit positions.
        var status = loaded.Cpu.RegisterFile.Status;
        var flags  = status.Single(s => s.Name == "FLAGS");
        Assert.Equal(16, flags.WidthBits);
        Assert.Equal(new BitRange(0,  0),  flags.Fields["CF"]);
        Assert.Equal(new BitRange(2,  2),  flags.Fields["PF"]);
        Assert.Equal(new BitRange(4,  4),  flags.Fields["AF"]);
        Assert.Equal(new BitRange(6,  6),  flags.Fields["ZF"]);
        Assert.Equal(new BitRange(7,  7),  flags.Fields["SF"]);
        Assert.Equal(new BitRange(8,  8),  flags.Fields["TF"]);
        Assert.Equal(new BitRange(9,  9),  flags.Fields["IF"]);
        Assert.Equal(new BitRange(10, 10), flags.Fields["DF"]);
        Assert.Equal(new BitRange(11, 11), flags.Fields["OF"]);

        // 4 segment registers + IP all live as 16-bit status registers.
        Assert.Equal(16, status.Single(s => s.Name == "IP").WidthBits);
        foreach (var seg in new[] { "ES", "CS", "SS", "DS" })
            Assert.Equal(16, status.Single(s => s.Name == seg).WidthBits);

        // Variable-length (1-15 byte) main set, no encodings yet.
        var main = loaded.InstructionSets["Main"];
        Assert.True(main.WidthBits.IsVariable);
        Assert.Equal(1, main.AlignmentBytes);
        Assert.Empty(main.EncodingGroups);
    }
}
