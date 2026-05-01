using AprCpu.Core.Decoder;
using AprCpu.Core.JsonSpec;
using Xunit;

namespace AprCpu.Tests;

public class DecoderTableTests
{
    private static InstructionSetSpec LoadArm() =>
        SpecLoader.LoadInstructionSet(Path.Combine(TestPaths.SpecRoot, "arm7tdmi", "arm.json"));

    private static InstructionSetSpec LoadThumb() =>
        SpecLoader.LoadInstructionSet(Path.Combine(TestPaths.SpecRoot, "arm7tdmi", "thumb.json"));

    [Fact]
    public void Construct_ValidatesAllPatterns()
    {
        var t = new DecoderTable(LoadArm());
        Assert.NotEmpty(t.Formats);
    }

    /// <summary>
    /// MOV R0, #1     (AL cond, no S, opcode=1101, rd=0, imm8=1, rotate=0)
    /// Expected encoding: 1110 001 1101 0 0000 0000 0000 0000 0001 = 0xE3A00001
    /// </summary>
    [Fact]
    public void Decode_MovImmediate()
    {
        var t = new DecoderTable(LoadArm());
        var d = t.Decode(0xE3A0_0001u);
        Assert.NotNull(d);
        Assert.Equal("DataProcessing_Immediate", d!.Format.Name);
        Assert.Equal("MOV", d.Instruction.Mnemonic);
    }

    /// <summary>ADD R1, R2, #3 → opcode=0100, rn=2, rd=1, imm=3 → 0xE2821003</summary>
    [Fact]
    public void Decode_AddImmediate()
    {
        var t = new DecoderTable(LoadArm());
        var d = t.Decode(0xE282_1003u);
        Assert.NotNull(d);
        Assert.Equal("ADD", d!.Instruction.Mnemonic);
    }

    /// <summary>SUB R0, R0, #1 → opcode=0010, rd=0, rn=0, imm=1 → 0xE2400001</summary>
    [Fact]
    public void Decode_SubImmediate()
    {
        var t = new DecoderTable(LoadArm());
        var d = t.Decode(0xE240_0001u);
        Assert.NotNull(d);
        Assert.Equal("SUB", d!.Instruction.Mnemonic);
    }

    /// <summary>CMP R0, #0 → opcode=1010, S=1, rn=0, imm=0 → 0xE3500000</summary>
    [Fact]
    public void Decode_CmpImmediate()
    {
        var t = new DecoderTable(LoadArm());
        var d = t.Decode(0xE350_0000u);
        Assert.NotNull(d);
        Assert.Equal("CMP", d!.Instruction.Mnemonic);
    }

    /// <summary>BX R0 → 0xE12FFF10</summary>
    [Fact]
    public void Decode_BxRegister()
    {
        var t = new DecoderTable(LoadArm());
        var d = t.Decode(0xE12F_FF10u);
        Assert.NotNull(d);
        Assert.Equal("BX", d!.Instruction.Mnemonic);
    }

    /// <summary>B label (offset=0) → 0xEA000000</summary>
    [Fact]
    public void Decode_BranchUnconditional()
    {
        var t = new DecoderTable(LoadArm());
        var d = t.Decode(0xEA00_0000u);
        Assert.NotNull(d);
        Assert.Equal("B", d!.Instruction.Mnemonic);
    }

    /// <summary>BL label → 0xEB000000</summary>
    [Fact]
    public void Decode_BranchLink()
    {
        var t = new DecoderTable(LoadArm());
        var d = t.Decode(0xEB00_0000u);
        Assert.NotNull(d);
        Assert.Equal("BL", d!.Instruction.Mnemonic);
    }

    /// <summary>Thumb LSL R0, R1, #2 → 0x0088 (op=00 LSL, imm5=2, rs=1, rd=0)</summary>
    [Fact]
    public void Decode_Thumb_Lsl()
    {
        var t = new DecoderTable(LoadThumb());
        var d = t.Decode(0x0088u);
        Assert.NotNull(d);
        Assert.Equal("LSL", d!.Instruction.Mnemonic);
    }

    /// <summary>Thumb LSR R0, R1, #2 → 0x0888</summary>
    [Fact]
    public void Decode_Thumb_Lsr()
    {
        var t = new DecoderTable(LoadThumb());
        var d = t.Decode(0x0888u);
        Assert.NotNull(d);
        Assert.Equal("LSR", d!.Instruction.Mnemonic);
    }

    /// <summary>Thumb MOV R0, #5 → 0x2005 (op=00 MOV, rd=0, imm8=5)</summary>
    [Fact]
    public void Decode_Thumb_MovImmediate()
    {
        var t = new DecoderTable(LoadThumb());
        var d = t.Decode(0x2005u);
        Assert.NotNull(d);
        Assert.Equal("MOV", d!.Instruction.Mnemonic);
    }

    /// <summary>Thumb B (unconditional) offset 0 → 0xE000</summary>
    [Fact]
    public void Decode_Thumb_BranchUnconditional()
    {
        var t = new DecoderTable(LoadThumb());
        var d = t.Decode(0xE000u);
        Assert.NotNull(d);
        Assert.Equal("B", d!.Instruction.Mnemonic);
    }

    [Fact]
    public void Decode_ReturnsNullForUndefinedEncoding()
    {
        var t = new DecoderTable(LoadArm());
        // Undefined / unallocated space.
        var d = t.Decode(0xF000_0000u);
        Assert.Null(d);
    }
}
