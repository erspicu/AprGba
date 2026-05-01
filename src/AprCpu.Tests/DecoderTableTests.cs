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

    /// <summary>Thumb F2 ADD R0, R1, R2 (reg form) → 0x1888</summary>
    [Fact]
    public void Decode_Thumb_F2_AddReg()
    {
        var t = new DecoderTable(LoadThumb());
        var d = t.Decode(0x1888u);
        Assert.NotNull(d);
        Assert.Equal("Thumb_F2_AddSub", d!.Format.Name);
        Assert.Equal("ADD", d.Instruction.Mnemonic);
    }

    /// <summary>Thumb F2 ADD R0, R1, #5 (imm3 form) → 0x1D48</summary>
    [Fact]
    public void Decode_Thumb_F2_AddImm3()
    {
        var t = new DecoderTable(LoadThumb());
        var d = t.Decode(0x1D48u);
        Assert.NotNull(d);
        Assert.Equal("Thumb_F2_AddSub", d!.Format.Name);
        Assert.Equal("ADD", d.Instruction.Mnemonic);
    }

    /// <summary>Thumb F2 SUB R0, R1, R2 (reg) → 0x1A88</summary>
    [Fact]
    public void Decode_Thumb_F2_SubReg()
    {
        var t = new DecoderTable(LoadThumb());
        var d = t.Decode(0x1A88u);
        Assert.NotNull(d);
        Assert.Equal("SUB", d!.Instruction.Mnemonic);
    }

    /// <summary>Thumb F4 AND R0, R1 → 0x4008</summary>
    [Fact]
    public void Decode_Thumb_F4_And()
    {
        var t = new DecoderTable(LoadThumb());
        var d = t.Decode(0x4008u);
        Assert.NotNull(d);
        Assert.Equal("Thumb_F4_AluOps", d!.Format.Name);
        Assert.Equal("AND", d.Instruction.Mnemonic);
    }

    /// <summary>Thumb F4 NEG R0, R1 → 0x4248</summary>
    [Fact]
    public void Decode_Thumb_F4_Neg()
    {
        var t = new DecoderTable(LoadThumb());
        var d = t.Decode(0x4248u);
        Assert.NotNull(d);
        Assert.Equal("NEG", d!.Instruction.Mnemonic);
    }

    /// <summary>Thumb F4 MUL R0, R1 → 0x4348</summary>
    [Fact]
    public void Decode_Thumb_F4_Mul()
    {
        var t = new DecoderTable(LoadThumb());
        var d = t.Decode(0x4348u);
        Assert.NotNull(d);
        Assert.Equal("MUL", d!.Instruction.Mnemonic);
    }

    /// <summary>Thumb F5 BX R8 (Hi reg, H2=1) → 0x4740</summary>
    [Fact]
    public void Decode_Thumb_F5_BxHiReg()
    {
        var t = new DecoderTable(LoadThumb());
        var d = t.Decode(0x4740u);
        Assert.NotNull(d);
        Assert.Equal("Thumb_F5_HiRegOps", d!.Format.Name);
        Assert.Equal("BX", d.Instruction.Mnemonic);
    }

    /// <summary>Thumb F5 ADD R0, R8 (H2=1) → 0x4440</summary>
    [Fact]
    public void Decode_Thumb_F5_AddHi()
    {
        var t = new DecoderTable(LoadThumb());
        var d = t.Decode(0x4440u);
        Assert.NotNull(d);
        Assert.Equal("Thumb_F5_HiRegOps", d!.Format.Name);
        Assert.Equal("ADD", d.Instruction.Mnemonic);
    }

    // Note: prior "Decode_ReturnsNullForUndefinedEncoding" test was removed
    // in 2.5.5a because the spec's encoding-space coverage is approaching
    // 100% (Coprocessor stubs in 2.5.5b will close the remaining gap), and
    // any reserved/UND encoding becomes a defined Undefined-trap format.

    /// <summary>RegImmShift form: MOV R0, R1, LSL #2 → 0xE1A00101.</summary>
    [Fact]
    public void Decode_RegImmShift_Mov()
    {
        var t = new DecoderTable(LoadArm());
        var d = t.Decode(0xE1A0_0101u);
        Assert.NotNull(d);
        Assert.Equal("DataProcessing_RegImmShift", d!.Format.Name);
        Assert.Equal("MOV", d.Instruction.Mnemonic);
    }

    /// <summary>RegImmShift form: ADD R0, R1, R2, LSL #3 → 0xE0810182.</summary>
    [Fact]
    public void Decode_RegImmShift_AddWithShift()
    {
        var t = new DecoderTable(LoadArm());
        var d = t.Decode(0xE081_0182u);
        Assert.NotNull(d);
        Assert.Equal("DataProcessing_RegImmShift", d!.Format.Name);
        Assert.Equal("ADD", d.Instruction.Mnemonic);
    }

    /// <summary>STR R0, [R1, #4]  pre-indexed, no writeback → 0xE5810004</summary>
    [Fact]
    public void Decode_SDT_Imm_STR()
    {
        var t = new DecoderTable(LoadArm());
        var d = t.Decode(0xE581_0004u);
        Assert.NotNull(d);
        Assert.Equal("SDT_Imm_STR", d!.Format.Name);
        Assert.Equal("STR", d.Instruction.Mnemonic);
    }

    /// <summary>LDR R0, [R1, #4]  pre-indexed, no writeback → 0xE5910004</summary>
    [Fact]
    public void Decode_SDT_Imm_LDR()
    {
        var t = new DecoderTable(LoadArm());
        var d = t.Decode(0xE591_0004u);
        Assert.NotNull(d);
        Assert.Equal("SDT_Imm_LDR", d!.Format.Name);
        Assert.Equal("LDR", d.Instruction.Mnemonic);
    }

    /// <summary>STRB R0, [R1, #4] → 0xE5C10004</summary>
    [Fact]
    public void Decode_SDT_Imm_STRB()
    {
        var t = new DecoderTable(LoadArm());
        var d = t.Decode(0xE5C1_0004u);
        Assert.NotNull(d);
        Assert.Equal("SDT_Imm_STRB", d!.Format.Name);
        Assert.Equal("STRB", d.Instruction.Mnemonic);
    }

    /// <summary>LDRB R0, [R1, #4] → 0xE5D10004</summary>
    [Fact]
    public void Decode_SDT_Imm_LDRB()
    {
        var t = new DecoderTable(LoadArm());
        var d = t.Decode(0xE5D1_0004u);
        Assert.NotNull(d);
        Assert.Equal("SDT_Imm_LDRB", d!.Format.Name);
        Assert.Equal("LDRB", d.Instruction.Mnemonic);
    }

    /// <summary>STR R0, [R1, R2]   reg offset, pre, no shift, no writeback → 0xE7810002</summary>
    [Fact]
    public void Decode_SDT_Reg_STR()
    {
        var t = new DecoderTable(LoadArm());
        var d = t.Decode(0xE781_0002u);
        Assert.NotNull(d);
        Assert.Equal("SDT_Reg_STR", d!.Format.Name);
        Assert.Equal("STR", d.Instruction.Mnemonic);
    }

    /// <summary>LDR R0, [R1, R2, LSL #2] → 0xE7910102</summary>
    [Fact]
    public void Decode_SDT_Reg_LDR()
    {
        var t = new DecoderTable(LoadArm());
        var d = t.Decode(0xE791_0102u);
        Assert.NotNull(d);
        Assert.Equal("SDT_Reg_LDR", d!.Format.Name);
        Assert.Equal("LDR", d.Instruction.Mnemonic);
    }

    /// <summary>STRB R0, [R1, R2] → 0xE7C10002</summary>
    [Fact]
    public void Decode_SDT_Reg_STRB()
    {
        var t = new DecoderTable(LoadArm());
        var d = t.Decode(0xE7C1_0002u);
        Assert.NotNull(d);
        Assert.Equal("SDT_Reg_STRB", d!.Format.Name);
        Assert.Equal("STRB", d.Instruction.Mnemonic);
    }

    /// <summary>LDRB R0, [R1, R2] → 0xE7D10002</summary>
    [Fact]
    public void Decode_SDT_Reg_LDRB()
    {
        var t = new DecoderTable(LoadArm());
        var d = t.Decode(0xE7D1_0002u);
        Assert.NotNull(d);
        Assert.Equal("SDT_Reg_LDRB", d!.Format.Name);
        Assert.Equal("LDRB", d.Instruction.Mnemonic);
    }

    /// <summary>STRH R0, [R1, #4]  → 0xE1C100B4</summary>
    [Fact]
    public void Decode_HSDT_Imm_STRH()
    {
        var t = new DecoderTable(LoadArm());
        var d = t.Decode(0xE1C1_00B4u);
        Assert.NotNull(d);
        Assert.Equal("HSDT_Imm_STRH", d!.Format.Name);
        Assert.Equal("STRH", d.Instruction.Mnemonic);
    }

    /// <summary>LDRH R0, [R1, #4]  → 0xE1D100B4</summary>
    [Fact]
    public void Decode_HSDT_Imm_LDRH()
    {
        var t = new DecoderTable(LoadArm());
        var d = t.Decode(0xE1D1_00B4u);
        Assert.NotNull(d);
        Assert.Equal("HSDT_Imm_LDRH", d!.Format.Name);
        Assert.Equal("LDRH", d.Instruction.Mnemonic);
    }

    /// <summary>LDRSB R0, [R1, #4] → 0xE1D100D4</summary>
    [Fact]
    public void Decode_HSDT_Imm_LDRSB()
    {
        var t = new DecoderTable(LoadArm());
        var d = t.Decode(0xE1D1_00D4u);
        Assert.NotNull(d);
        Assert.Equal("HSDT_Imm_LDRSB", d!.Format.Name);
        Assert.Equal("LDRSB", d.Instruction.Mnemonic);
    }

    /// <summary>LDRSH R0, [R1, #4] → 0xE1D100F4</summary>
    [Fact]
    public void Decode_HSDT_Imm_LDRSH()
    {
        var t = new DecoderTable(LoadArm());
        var d = t.Decode(0xE1D1_00F4u);
        Assert.NotNull(d);
        Assert.Equal("HSDT_Imm_LDRSH", d!.Format.Name);
        Assert.Equal("LDRSH", d.Instruction.Mnemonic);
    }

    /// <summary>STRH R0, [R1, R2] → 0xE18100B2</summary>
    [Fact]
    public void Decode_HSDT_Reg_STRH()
    {
        var t = new DecoderTable(LoadArm());
        var d = t.Decode(0xE181_00B2u);
        Assert.NotNull(d);
        Assert.Equal("HSDT_Reg_STRH", d!.Format.Name);
        Assert.Equal("STRH", d.Instruction.Mnemonic);
    }

    /// <summary>LDRH R0, [R1, R2] → 0xE19100B2</summary>
    [Fact]
    public void Decode_HSDT_Reg_LDRH()
    {
        var t = new DecoderTable(LoadArm());
        var d = t.Decode(0xE191_00B2u);
        Assert.NotNull(d);
        Assert.Equal("HSDT_Reg_LDRH", d!.Format.Name);
        Assert.Equal("LDRH", d.Instruction.Mnemonic);
    }

    /// <summary>LDRSB R0, [R1, R2] → 0xE19100D2</summary>
    [Fact]
    public void Decode_HSDT_Reg_LDRSB()
    {
        var t = new DecoderTable(LoadArm());
        var d = t.Decode(0xE191_00D2u);
        Assert.NotNull(d);
        Assert.Equal("HSDT_Reg_LDRSB", d!.Format.Name);
        Assert.Equal("LDRSB", d.Instruction.Mnemonic);
    }

    /// <summary>LDRSH R0, [R1, R2] → 0xE19100F2</summary>
    [Fact]
    public void Decode_HSDT_Reg_LDRSH()
    {
        var t = new DecoderTable(LoadArm());
        var d = t.Decode(0xE191_00F2u);
        Assert.NotNull(d);
        Assert.Equal("HSDT_Reg_LDRSH", d!.Format.Name);
        Assert.Equal("LDRSH", d.Instruction.Mnemonic);
    }

    /// <summary>SWP R0, R1, [R2]  → 0xE1020091</summary>
    [Fact]
    public void Decode_SWP_Word()
    {
        var t = new DecoderTable(LoadArm());
        var d = t.Decode(0xE102_0091u);
        Assert.NotNull(d);
        Assert.Equal("SWP_Word", d!.Format.Name);
        Assert.Equal("SWP", d.Instruction.Mnemonic);
    }

    /// <summary>SWPB R0, R1, [R2] → 0xE1420091</summary>
    [Fact]
    public void Decode_SWP_Byte()
    {
        var t = new DecoderTable(LoadArm());
        var d = t.Decode(0xE142_0091u);
        Assert.NotNull(d);
        Assert.Equal("SWP_Byte", d!.Format.Name);
        Assert.Equal("SWPB", d.Instruction.Mnemonic);
    }

    /// <summary>MUL R0, R1, R2  Rd=0, Rm=1, Rs=2 → 0xE0000291</summary>
    [Fact]
    public void Decode_MUL()
    {
        var t = new DecoderTable(LoadArm());
        var d = t.Decode(0xE000_0291u);
        Assert.NotNull(d);
        Assert.Equal("Multiply_32", d!.Format.Name);
        Assert.Equal("MUL", d.Instruction.Mnemonic);
    }

    /// <summary>MLA R0, R1, R2, R3 → 0xE0203291  (A=1, Rd=0, Rn=3, Rs=2, Rm=1)</summary>
    [Fact]
    public void Decode_MLA()
    {
        var t = new DecoderTable(LoadArm());
        var d = t.Decode(0xE020_3291u);
        Assert.NotNull(d);
        Assert.Equal("Multiply_32", d!.Format.Name);
        Assert.Equal("MLA", d.Instruction.Mnemonic);
    }

    /// <summary>UMULL R0, R1, R2, R3  RdLo=0, RdHi=1, Rm=2, Rs=3 → 0xE0810392</summary>
    [Fact]
    public void Decode_UMULL()
    {
        var t = new DecoderTable(LoadArm());
        var d = t.Decode(0xE081_0392u);
        Assert.NotNull(d);
        Assert.Equal("MultiplyLong", d!.Format.Name);
        Assert.Equal("UMULL", d.Instruction.Mnemonic);
    }

    /// <summary>UMLAL R0, R1, R2, R3 → 0xE0A10392</summary>
    [Fact]
    public void Decode_UMLAL()
    {
        var t = new DecoderTable(LoadArm());
        var d = t.Decode(0xE0A1_0392u);
        Assert.NotNull(d);
        Assert.Equal("MultiplyLong", d!.Format.Name);
        Assert.Equal("UMLAL", d.Instruction.Mnemonic);
    }

    /// <summary>SMULL R0, R1, R2, R3 → 0xE0C10392</summary>
    [Fact]
    public void Decode_SMULL()
    {
        var t = new DecoderTable(LoadArm());
        var d = t.Decode(0xE0C1_0392u);
        Assert.NotNull(d);
        Assert.Equal("MultiplyLong", d!.Format.Name);
        Assert.Equal("SMULL", d.Instruction.Mnemonic);
    }

    /// <summary>SMLAL R0, R1, R2, R3 → 0xE0E10392</summary>
    [Fact]
    public void Decode_SMLAL()
    {
        var t = new DecoderTable(LoadArm());
        var d = t.Decode(0xE0E1_0392u);
        Assert.NotNull(d);
        Assert.Equal("MultiplyLong", d!.Format.Name);
        Assert.Equal("SMLAL", d.Instruction.Mnemonic);
    }

    /// <summary>LDMIA R0, {R1,R2} → 0xE8900006</summary>
    [Fact]
    public void Decode_LDM_IA()
    {
        var t = new DecoderTable(LoadArm());
        var d = t.Decode(0xE890_0006u);
        Assert.NotNull(d);
        Assert.Equal("BDT_LDM", d!.Format.Name);
        Assert.Equal("LDM", d.Instruction.Mnemonic);
    }

    /// <summary>LDMIB R0, {R1} → 0xE9900002 (P=1)</summary>
    [Fact]
    public void Decode_LDM_IB()
    {
        var t = new DecoderTable(LoadArm());
        var d = t.Decode(0xE990_0002u);
        Assert.NotNull(d);
        Assert.Equal("BDT_LDM", d!.Format.Name);
        Assert.Equal("LDM", d.Instruction.Mnemonic);
    }

    /// <summary>STMIA R0!, {R1,R2} → 0xE8A00006 (W=1)</summary>
    [Fact]
    public void Decode_STM_IA_Writeback()
    {
        var t = new DecoderTable(LoadArm());
        var d = t.Decode(0xE8A0_0006u);
        Assert.NotNull(d);
        Assert.Equal("BDT_STM", d!.Format.Name);
        Assert.Equal("STM", d.Instruction.Mnemonic);
    }

    /// <summary>STMDB R13!, {R0,R14}  push → 0xE92D4001  (P=1, U=0, W=1)</summary>
    [Fact]
    public void Decode_STMDB_Push()
    {
        var t = new DecoderTable(LoadArm());
        var d = t.Decode(0xE92D_4001u);
        Assert.NotNull(d);
        Assert.Equal("BDT_STM", d!.Format.Name);
        Assert.Equal("STM", d.Instruction.Mnemonic);
    }

    /// <summary>SWI #0x123456 → 0xEF123456</summary>
    [Fact]
    public void Decode_SWI()
    {
        var t = new DecoderTable(LoadArm());
        var d = t.Decode(0xEF12_3456u);
        Assert.NotNull(d);
        Assert.Equal("SWI", d!.Format.Name);
        Assert.Equal("SWI", d.Instruction.Mnemonic);
    }

    /// <summary>CDP encoding (any) → traps to UND. 0xEE000000.</summary>
    [Fact]
    public void Decode_CDP_StubsToUndefined()
    {
        var t = new DecoderTable(LoadArm());
        var d = t.Decode(0xEE00_0000u);
        Assert.NotNull(d);
        Assert.Equal("Coprocessor_CDP", d!.Format.Name);
        Assert.Equal("CDP", d.Instruction.Mnemonic);
    }

    /// <summary>MCR/MRC encoding (bit 4 = 1) → 0xEE000010.</summary>
    [Fact]
    public void Decode_MCR_MRC_StubsToUndefined()
    {
        var t = new DecoderTable(LoadArm());
        var d = t.Decode(0xEE00_0010u);
        Assert.NotNull(d);
        Assert.Equal("Coprocessor_MCR_MRC", d!.Format.Name);
        Assert.Equal("MCR_MRC", d.Instruction.Mnemonic);
    }

    /// <summary>LDC/STC encoding (bits 27:25 = 110) → 0xEC000000.</summary>
    [Fact]
    public void Decode_LDC_STC_StubsToUndefined()
    {
        var t = new DecoderTable(LoadArm());
        var d = t.Decode(0xEC00_0000u);
        Assert.NotNull(d);
        Assert.Equal("Coprocessor_LDC_STC", d!.Format.Name);
        Assert.Equal("LDC_STC", d.Instruction.Mnemonic);
    }

    /// <summary>"Always undefined" reserved encoding (bits 27:25 = 011, bit 4 = 1) → 0xE6000010.</summary>
    [Fact]
    public void Decode_ArchitecturallyUndefined()
    {
        var t = new DecoderTable(LoadArm());
        var d = t.Decode(0xE600_0010u);
        Assert.NotNull(d);
        Assert.Equal("Undefined_Reserved_011_x4_1", d!.Format.Name);
        Assert.Equal("UND", d.Instruction.Mnemonic);
    }

    /// <summary>RegRegShift form: ADD R0, R1, R2, LSL R3 → 0xE0810312.</summary>
    [Fact]
    public void Decode_RegRegShift_AddByRegister()
    {
        var t = new DecoderTable(LoadArm());
        var d = t.Decode(0xE081_0312u);
        Assert.NotNull(d);
        Assert.Equal("DataProcessing_RegRegShift", d!.Format.Name);
        Assert.Equal("ADD", d.Instruction.Mnemonic);
    }

    /// <summary>MRS R0, CPSR → 0xE10F_0000.</summary>
    [Fact]
    public void Decode_MrsCpsr()
    {
        var t = new DecoderTable(LoadArm());
        var d = t.Decode(0xE10F_0000u);
        Assert.NotNull(d);
        Assert.Equal("PSR_MRS", d!.Format.Name);
        Assert.Equal("MRS", d.Instruction.Mnemonic);
    }

    /// <summary>MSR CPSR_fc, R0 → 0xE12F_F000  (mask=1111).</summary>
    [Fact]
    public void Decode_MsrReg()
    {
        var t = new DecoderTable(LoadArm());
        var d = t.Decode(0xE12F_F000u);
        Assert.NotNull(d);
        Assert.Equal("PSR_MSR_Reg", d!.Format.Name);
        Assert.Equal("MSR", d.Instruction.Mnemonic);
    }

    /// <summary>MSR CPSR_f, #0x80 → 0xE32F_F080.</summary>
    [Fact]
    public void Decode_MsrImm()
    {
        var t = new DecoderTable(LoadArm());
        var d = t.Decode(0xE32F_F080u);
        Assert.NotNull(d);
        Assert.Equal("PSR_MSR_Imm", d!.Format.Name);
        Assert.Equal("MSR", d.Instruction.Mnemonic);
    }

    /// <summary>Decoder coverage for all 16 ARM Data Processing Immediate ALU ops.</summary>
    [Theory]
    [InlineData(0xE20000FFu, "AND")]   // AND R0, R0, #0xFF
    [InlineData(0xE2211001u, "EOR")]   // EOR R1, R1, #1
    [InlineData(0xE26AA000u, "RSB")]   // RSB R10, R10, #0
    [InlineData(0xE282_1003u, "ADD")]
    [InlineData(0xE2A88001u, "ADC")]   // ADC R8, R8, #1
    [InlineData(0xE2C99000u, "SBC")]   // SBC R9, R9, #0
    [InlineData(0xE2EBB000u, "RSC")]   // RSC R11, R11, #0
    [InlineData(0xE3150001u, "TST")]   // TST R5, #1
    [InlineData(0xE3360000u, "TEQ")]   // TEQ R6, #0
    [InlineData(0xE350_0000u, "CMP")]
    [InlineData(0xE3770005u, "CMN")]   // CMN R7, #5
    [InlineData(0xE3822080u, "ORR")]   // ORR R2, R2, #0x80
    [InlineData(0xE3A0_0001u, "MOV")]
    [InlineData(0xE3C3300Fu, "BIC")]   // BIC R3, R3, #0xF
    [InlineData(0xE3E04000u, "MVN")]   // MVN R4, #0
    [InlineData(0xE2400001u, "SUB")]
    public void Decode_AllArmDataProcessingImmediate(uint encoding, string expectedMnemonic)
    {
        var t = new DecoderTable(LoadArm());
        var d = t.Decode(encoding);
        Assert.NotNull(d);
        Assert.Equal("DataProcessing_Immediate", d!.Format.Name);
        Assert.Equal(expectedMnemonic, d.Instruction.Mnemonic);
    }
}
