// Phase 24.2.1 — ModR/M decoder + EA computation tests.
//
// 8086 ModR/M is the single highest-leverage piece of the decoder
// because every memory-operand instruction goes through it. Bug here
// = bug in 200+ opcodes. Test every (mod × r/m) combo + the disp16
// direct-address special case + default-segment selection (BP-based
// modes default to SS, not DS) + register-encoding ordering.

using AprX86.Cli.Cpu;
using Xunit;

namespace AprCpu.Tests;

public class X86ModRmTests
{
    // ---------------- ModRmFields decoding ----------------

    [Fact]
    public void ModRmFields_DecodeSplitsThreeFields()
    {
        // Byte 0xC1 = 1100_0001 → mod=11 reg=000 r/m=001
        var f = ModRmFields.Decode(0xC1);
        Assert.Equal(0b11, f.Mod);
        Assert.Equal(0b000, f.Reg);
        Assert.Equal(0b001, f.RM);
        Assert.True(f.IsRegister);
        Assert.Equal(0, f.DisplacementBytes);
    }

    [Fact]
    public void ModRmFields_Decode_AllMods_Disp8And16()
    {
        // mod=00 r/m=000 [BX+SI] — no disp
        Assert.Equal(0, ModRmFields.Decode(0b00_000_000).DisplacementBytes);
        // mod=00 r/m=110 — disp16 direct (special case, 2 bytes despite mod=00)
        Assert.Equal(2, ModRmFields.Decode(0b00_000_110).DisplacementBytes);
        // mod=01 — disp8
        Assert.Equal(1, ModRmFields.Decode(0b01_000_000).DisplacementBytes);
        Assert.Equal(1, ModRmFields.Decode(0b01_000_110).DisplacementBytes);
        // mod=10 — disp16
        Assert.Equal(2, ModRmFields.Decode(0b10_000_000).DisplacementBytes);
        // mod=11 — register direct, no disp
        Assert.Equal(0, ModRmFields.Decode(0b11_000_000).DisplacementBytes);
    }

    // ---------------- Effective Address computation ----------------

    private static X86State StateWithRegs(
        ushort bx = 0, ushort si = 0, ushort di = 0, ushort bp = 0)
    {
        var s = new X86State();
        s.B.X = bx;
        s.SI = si;
        s.DI = di;
        s.BP = bp;
        return s;
    }

    [Fact]
    public void EA_BX_SI_NoDisp_DefaultsDS()
    {
        var s = StateWithRegs(bx: 0x1000, si: 0x0050);
        var f = ModRmFields.Decode(0b00_000_000);    // mod=00, r/m=000 — [BX+SI]
        var ea = ModRm.ComputeEffectiveAddress(f, 0, s);
        Assert.Equal(0x1050, ea.Offset);
        Assert.Equal(SegReg.DS, ea.DefaultSegment);
    }

    [Fact]
    public void EA_BP_SI_NoDisp_DefaultsSS()
    {
        // BP-based modes default to SS, not DS — classic 8086 corner.
        var s = StateWithRegs(bp: 0x2000, si: 0x0080);
        var f = ModRmFields.Decode(0b00_000_010);    // mod=00, r/m=010 — [BP+SI]
        var ea = ModRm.ComputeEffectiveAddress(f, 0, s);
        Assert.Equal(0x2080, ea.Offset);
        Assert.Equal(SegReg.SS, ea.DefaultSegment);
    }

    [Fact]
    public void EA_BP_DI_NoDisp_DefaultsSS()
    {
        var s = StateWithRegs(bp: 0x3000, di: 0x0010);
        var f = ModRmFields.Decode(0b00_000_011);    // [BP+DI]
        var ea = ModRm.ComputeEffectiveAddress(f, 0, s);
        Assert.Equal(0x3010, ea.Offset);
        Assert.Equal(SegReg.SS, ea.DefaultSegment);
    }

    [Fact]
    public void EA_SIonly_DefaultsDS()
    {
        var s = StateWithRegs(si: 0x4444);
        var f = ModRmFields.Decode(0b00_000_100);    // [SI]
        var ea = ModRm.ComputeEffectiveAddress(f, 0, s);
        Assert.Equal(0x4444, ea.Offset);
        Assert.Equal(SegReg.DS, ea.DefaultSegment);
    }

    [Fact]
    public void EA_DIonly_DefaultsDS()
    {
        var s = StateWithRegs(di: 0x5555);
        var f = ModRmFields.Decode(0b00_000_101);    // [DI]
        var ea = ModRm.ComputeEffectiveAddress(f, 0, s);
        Assert.Equal(0x5555, ea.Offset);
        Assert.Equal(SegReg.DS, ea.DefaultSegment);
    }

    [Fact]
    public void EA_BPonly_Mod01_DefaultsSS()
    {
        // [BP] is only available with mod=01 or mod=10 (mod=00 r/m=110 is disp16).
        var s = StateWithRegs(bp: 0x6000);
        var f = ModRmFields.Decode(0b01_000_110);    // mod=01, r/m=110 — [BP+disp8]
        // disp8 = -2 sign-extended to ushort = 0xFFFE
        var ea = ModRm.ComputeEffectiveAddress(f, /*disp=*/0xFFFE, s);
        Assert.Equal(0x5FFE, ea.Offset);
        Assert.Equal(SegReg.SS, ea.DefaultSegment);
    }

    [Fact]
    public void EA_BXonly_DefaultsDS()
    {
        var s = StateWithRegs(bx: 0x7777);
        var f = ModRmFields.Decode(0b00_000_111);    // [BX]
        var ea = ModRm.ComputeEffectiveAddress(f, 0, s);
        Assert.Equal(0x7777, ea.Offset);
        Assert.Equal(SegReg.DS, ea.DefaultSegment);
    }

    [Fact]
    public void EA_DirectAddress_Mod00_RM110()
    {
        // The special case: mod=00, r/m=110 means disp16 (direct address),
        // NOT [BP]. Default segment is DS (not SS like other r/m=110 forms).
        var s = StateWithRegs(bp: 0xDEAD);            // BP intentionally non-zero
        var f = ModRmFields.Decode(0b00_000_110);
        var ea = ModRm.ComputeEffectiveAddress(f, /*disp=*/0x1234, s);
        Assert.Equal(0x1234, ea.Offset);              // BP NOT used
        Assert.Equal(SegReg.DS, ea.DefaultSegment);   // direct addressing → DS
    }

    [Fact]
    public void EA_Disp8_SignExtended()
    {
        var s = StateWithRegs(bx: 0x1000);
        var f = ModRmFields.Decode(0b01_000_111);    // mod=01, r/m=111 — [BX+disp8]
        // disp8 = -1 should sign-extend to 0xFFFF, so offset = 0x1000 + 0xFFFF = 0x0FFF (wraps).
        var ea = ModRm.ComputeEffectiveAddress(f, /*disp=*/0xFFFF, s);
        Assert.Equal(0x0FFF, ea.Offset);
    }

    [Fact]
    public void EA_Disp16_FullRange()
    {
        var s = StateWithRegs(bx: 0x0010);
        var f = ModRmFields.Decode(0b10_000_111);    // mod=10, r/m=111 — [BX+disp16]
        var ea = ModRm.ComputeEffectiveAddress(f, 0xFFF0, s);
        Assert.Equal(0x0000, ea.Offset);              // 0x10 + 0xFFF0 = 0x10000 → wraps to 0x0000
    }

    [Fact]
    public void EA_RegisterMode_ThrowsIfMisused()
    {
        var s = new X86State();
        var f = ModRmFields.Decode(0b11_000_001);
        Assert.Throws<InvalidOperationException>(
            () => ModRm.ComputeEffectiveAddress(f, 0, s));
    }

    // ---------------- Register-by-encoding accessors ----------------

    [Fact]
    public void Reg8_AllEightEncodings()
    {
        var s = new X86State();
        // Set each via specific architectural register, read back via index.
        s.A.L = 0x10; s.C.L = 0x11; s.D.L = 0x12; s.B.L = 0x13;
        s.A.H = 0x20; s.C.H = 0x21; s.D.H = 0x22; s.B.H = 0x23;
        Assert.Equal(0x10, s.GetReg8(0));   // AL
        Assert.Equal(0x11, s.GetReg8(1));   // CL
        Assert.Equal(0x12, s.GetReg8(2));   // DL
        Assert.Equal(0x13, s.GetReg8(3));   // BL
        Assert.Equal(0x20, s.GetReg8(4));   // AH
        Assert.Equal(0x21, s.GetReg8(5));   // CH
        Assert.Equal(0x22, s.GetReg8(6));   // DH
        Assert.Equal(0x23, s.GetReg8(7));   // BH
    }

    [Fact]
    public void Reg8_SetByIndex_RoundTrips()
    {
        var s = new X86State();
        for (int i = 0; i < 8; i++)
            s.SetReg8(i, (byte)(0x40 + i));
        for (int i = 0; i < 8; i++)
            Assert.Equal((byte)(0x40 + i), s.GetReg8(i));
    }

    [Fact]
    public void Reg16_AllEightEncodings()
    {
        var s = new X86State();
        s.A.X = 0x1010; s.C.X = 0x1111; s.D.X = 0x1212; s.B.X = 0x1313;
        s.SP = 0x2020; s.BP = 0x2121; s.SI = 0x2222; s.DI = 0x2323;
        Assert.Equal(0x1010, s.GetReg16(0));  // AX
        Assert.Equal(0x1111, s.GetReg16(1));  // CX
        Assert.Equal(0x1212, s.GetReg16(2));  // DX
        Assert.Equal(0x1313, s.GetReg16(3));  // BX
        Assert.Equal(0x2020, s.GetReg16(4));  // SP
        Assert.Equal(0x2121, s.GetReg16(5));  // BP
        Assert.Equal(0x2222, s.GetReg16(6));  // SI
        Assert.Equal(0x2323, s.GetReg16(7));  // DI
    }

    [Fact]
    public void SegReg_RoundTrip()
    {
        var s = new X86State();
        s.SetSeg(SegReg.ES, 0x1000);
        s.SetSeg(SegReg.CS, 0x2000);
        s.SetSeg(SegReg.SS, 0x3000);
        s.SetSeg(SegReg.DS, 0x4000);
        Assert.Equal(0x1000, s.GetSeg(SegReg.ES));
        Assert.Equal(0x2000, s.GetSeg(SegReg.CS));
        Assert.Equal(0x3000, s.GetSeg(SegReg.SS));
        Assert.Equal(0x4000, s.GetSeg(SegReg.DS));
    }

    // ---------------- Brute-force coverage matrix ----------------

    [Fact]
    public void EA_AllNonRegisterModRmCombos_ReturnSensibleSegment()
    {
        // For every mod ∈ {00, 01, 10}, r/m ∈ 000..111, ensure the
        // default segment is SS iff r/m is BP-based (010, 011, 110-when-not-direct).
        var s = StateWithRegs(bx: 1, si: 2, di: 3, bp: 4);
        for (int mod = 0; mod <= 2; mod++)
        for (int rm = 0; rm <= 7; rm++)
        {
            byte modrm = (byte)((mod << 6) | rm);
            var f = ModRmFields.Decode(modrm);
            ushort disp = (ushort)(f.DisplacementBytes == 1 ? 0xAB :
                                   f.DisplacementBytes == 2 ? 0xABCD : 0);
            var ea = ModRm.ComputeEffectiveAddress(f, disp, s);

            bool isDirectDisp16 = (mod == 0 && rm == 0b110);
            bool isBpForm = (rm == 0b010 || rm == 0b011 || rm == 0b110) && !isDirectDisp16;
            SegReg expected = isBpForm ? SegReg.SS : SegReg.DS;
            Assert.Equal(expected, ea.DefaultSegment);
        }
    }
}
