namespace AprCpu.Core.Runtime;

/// <summary>
/// MOS 6502 / Ricoh 2A03 instruction length table — first byte determines
/// total instruction length (1, 2, or 3 bytes).
///
/// Reference: official 6502 opcode matrix + nesdev.org documentation
/// for the NES's 2A03 unofficial opcode set. The table covers all 256
/// first-byte values; unofficial opcodes (KIL/SLO/RLA/SRE/RRA/SAX/LAX/
/// DCP/ISC/ANC/ALR/ARR/AXS/XAA/LAS/SHY/SHX) match the published lengths
/// per <c>nesdev.org/wiki/CPU_unofficial_opcodes</c>.
///
/// Used by <see cref="BlockDetector"/> on the NES block-JIT path —
/// because the 6502 is variable-width (1/2/3 bytes per instruction)
/// the detector needs an opcode → length oracle to walk the next PC
/// during sequential block crawl.
/// </summary>
public static class Mos6502InstructionLengths
{
    private static readonly byte[] _table = new byte[256]
    {
        // 0x00: BRK (1, but +1 padding byte conventionally — counted as 1 here)
        // Length table follows the official "instruction byte count" semantic.
        // 0x00..0x0F:  BRK PHP BPL CLC JSR PLP BMI SEC RTI PHA BVC CLI RTS PLA BVS SEI ...
        //              actual: 00=BRK(1) 01=ORA(zp,X)(2) 02=KIL(1) 03=SLO(zp,X)(2)
        //                      04=NOP zp(2) 05=ORA zp(2) 06=ASL zp(2) 07=SLO zp(2)
        //                      08=PHP(1) 09=ORA #imm(2) 0A=ASL A(1) 0B=ANC #imm(2)
        //                      0C=NOP abs(3) 0D=ORA abs(3) 0E=ASL abs(3) 0F=SLO abs(3)
        1, 2, 1, 2, 2, 2, 2, 2,    1, 2, 1, 2, 3, 3, 3, 3,
        // 0x10..0x1F:  10=BPL(2) 11=ORA(zp),Y(2) 12=KIL(1) 13=SLO(zp),Y(2)
        //              14=NOP zp,X(2) 15=ORA zp,X(2) 16=ASL zp,X(2) 17=SLO zp,X(2)
        //              18=CLC(1) 19=ORA abs,Y(3) 1A=NOP(1) 1B=SLO abs,Y(3)
        //              1C=NOP abs,X(3) 1D=ORA abs,X(3) 1E=ASL abs,X(3) 1F=SLO abs,X(3)
        2, 2, 1, 2, 2, 2, 2, 2,    1, 3, 1, 3, 3, 3, 3, 3,
        // 0x20..0x2F:  20=JSR abs(3) 21=AND(zp,X)(2) 22=KIL(1) 23=RLA(zp,X)(2)
        //              24=BIT zp(2) 25=AND zp(2) 26=ROL zp(2) 27=RLA zp(2)
        //              28=PLP(1) 29=AND #imm(2) 2A=ROL A(1) 2B=ANC #imm(2)
        //              2C=BIT abs(3) 2D=AND abs(3) 2E=ROL abs(3) 2F=RLA abs(3)
        3, 2, 1, 2, 2, 2, 2, 2,    1, 2, 1, 2, 3, 3, 3, 3,
        // 0x30..0x3F:  30=BMI(2) 31=AND(zp),Y(2) 32=KIL(1) 33=RLA(zp),Y(2)
        //              34=NOP zp,X(2) 35=AND zp,X(2) 36=ROL zp,X(2) 37=RLA zp,X(2)
        //              38=SEC(1) 39=AND abs,Y(3) 3A=NOP(1) 3B=RLA abs,Y(3)
        //              3C=NOP abs,X(3) 3D=AND abs,X(3) 3E=ROL abs,X(3) 3F=RLA abs,X(3)
        2, 2, 1, 2, 2, 2, 2, 2,    1, 3, 1, 3, 3, 3, 3, 3,

        // 0x40..0x4F:  40=RTI(1) 41=EOR(zp,X)(2) 42=KIL(1) 43=SRE(zp,X)(2)
        //              44=NOP zp(2) 45=EOR zp(2) 46=LSR zp(2) 47=SRE zp(2)
        //              48=PHA(1) 49=EOR #imm(2) 4A=LSR A(1) 4B=ALR #imm(2)
        //              4C=JMP abs(3) 4D=EOR abs(3) 4E=LSR abs(3) 4F=SRE abs(3)
        1, 2, 1, 2, 2, 2, 2, 2,    1, 2, 1, 2, 3, 3, 3, 3,
        // 0x50..0x5F:  50=BVC(2) 51=EOR(zp),Y(2) 52=KIL(1) 53=SRE(zp),Y(2)
        //              54=NOP zp,X(2) 55=EOR zp,X(2) 56=LSR zp,X(2) 57=SRE zp,X(2)
        //              58=CLI(1) 59=EOR abs,Y(3) 5A=NOP(1) 5B=SRE abs,Y(3)
        //              5C=NOP abs,X(3) 5D=EOR abs,X(3) 5E=LSR abs,X(3) 5F=SRE abs,X(3)
        2, 2, 1, 2, 2, 2, 2, 2,    1, 3, 1, 3, 3, 3, 3, 3,
        // 0x60..0x6F:  60=RTS(1) 61=ADC(zp,X)(2) 62=KIL(1) 63=RRA(zp,X)(2)
        //              64=NOP zp(2) 65=ADC zp(2) 66=ROR zp(2) 67=RRA zp(2)
        //              68=PLA(1) 69=ADC #imm(2) 6A=ROR A(1) 6B=ARR #imm(2)
        //              6C=JMP (abs)(3) 6D=ADC abs(3) 6E=ROR abs(3) 6F=RRA abs(3)
        1, 2, 1, 2, 2, 2, 2, 2,    1, 2, 1, 2, 3, 3, 3, 3,
        // 0x70..0x7F:  70=BVS(2) 71=ADC(zp),Y(2) 72=KIL(1) 73=RRA(zp),Y(2)
        //              74=NOP zp,X(2) 75=ADC zp,X(2) 76=ROR zp,X(2) 77=RRA zp,X(2)
        //              78=SEI(1) 79=ADC abs,Y(3) 7A=NOP(1) 7B=RRA abs,Y(3)
        //              7C=NOP abs,X(3) 7D=ADC abs,X(3) 7E=ROR abs,X(3) 7F=RRA abs,X(3)
        2, 2, 1, 2, 2, 2, 2, 2,    1, 3, 1, 3, 3, 3, 3, 3,

        // 0x80..0x8F:  80=NOP #imm(2) 81=STA(zp,X)(2) 82=NOP #imm(2) 83=SAX(zp,X)(2)
        //              84=STY zp(2) 85=STA zp(2) 86=STX zp(2) 87=SAX zp(2)
        //              88=DEY(1) 89=NOP #imm(2) 8A=TXA(1) 8B=XAA #imm(2)
        //              8C=STY abs(3) 8D=STA abs(3) 8E=STX abs(3) 8F=SAX abs(3)
        2, 2, 2, 2, 2, 2, 2, 2,    1, 2, 1, 2, 3, 3, 3, 3,
        // 0x90..0x9F:  90=BCC(2) 91=STA(zp),Y(2) 92=KIL(1) 93=AHX(zp),Y(2)
        //              94=STY zp,X(2) 95=STA zp,X(2) 96=STX zp,Y(2) 97=SAX zp,Y(2)
        //              98=TYA(1) 99=STA abs,Y(3) 9A=TXS(1) 9B=TAS abs,Y(3)
        //              9C=SHY abs,X(3) 9D=STA abs,X(3) 9E=SHX abs,Y(3) 9F=AHX abs,Y(3)
        2, 2, 1, 2, 2, 2, 2, 2,    1, 3, 1, 3, 3, 3, 3, 3,
        // 0xA0..0xAF:  A0=LDY #imm(2) A1=LDA(zp,X)(2) A2=LDX #imm(2) A3=LAX(zp,X)(2)
        //              A4=LDY zp(2) A5=LDA zp(2) A6=LDX zp(2) A7=LAX zp(2)
        //              A8=TAY(1) A9=LDA #imm(2) AA=TAX(1) AB=LAX #imm(2)
        //              AC=LDY abs(3) AD=LDA abs(3) AE=LDX abs(3) AF=LAX abs(3)
        2, 2, 2, 2, 2, 2, 2, 2,    1, 2, 1, 2, 3, 3, 3, 3,
        // 0xB0..0xBF:  B0=BCS(2) B1=LDA(zp),Y(2) B2=KIL(1) B3=LAX(zp),Y(2)
        //              B4=LDY zp,X(2) B5=LDA zp,X(2) B6=LDX zp,Y(2) B7=LAX zp,Y(2)
        //              B8=CLV(1) B9=LDA abs,Y(3) BA=TSX(1) BB=LAS abs,Y(3)
        //              BC=LDY abs,X(3) BD=LDA abs,X(3) BE=LDX abs,Y(3) BF=LAX abs,Y(3)
        2, 2, 1, 2, 2, 2, 2, 2,    1, 3, 1, 3, 3, 3, 3, 3,

        // 0xC0..0xCF:  C0=CPY #imm(2) C1=CMP(zp,X)(2) C2=NOP #imm(2) C3=DCP(zp,X)(2)
        //              C4=CPY zp(2) C5=CMP zp(2) C6=DEC zp(2) C7=DCP zp(2)
        //              C8=INY(1) C9=CMP #imm(2) CA=DEX(1) CB=AXS #imm(2)
        //              CC=CPY abs(3) CD=CMP abs(3) CE=DEC abs(3) CF=DCP abs(3)
        2, 2, 2, 2, 2, 2, 2, 2,    1, 2, 1, 2, 3, 3, 3, 3,
        // 0xD0..0xDF:  D0=BNE(2) D1=CMP(zp),Y(2) D2=KIL(1) D3=DCP(zp),Y(2)
        //              D4=NOP zp,X(2) D5=CMP zp,X(2) D6=DEC zp,X(2) D7=DCP zp,X(2)
        //              D8=CLD(1) D9=CMP abs,Y(3) DA=NOP(1) DB=DCP abs,Y(3)
        //              DC=NOP abs,X(3) DD=CMP abs,X(3) DE=DEC abs,X(3) DF=DCP abs,X(3)
        2, 2, 1, 2, 2, 2, 2, 2,    1, 3, 1, 3, 3, 3, 3, 3,
        // 0xE0..0xEF:  E0=CPX #imm(2) E1=SBC(zp,X)(2) E2=NOP #imm(2) E3=ISC(zp,X)(2)
        //              E4=CPX zp(2) E5=SBC zp(2) E6=INC zp(2) E7=ISC zp(2)
        //              E8=INX(1) E9=SBC #imm(2) EA=NOP(1) EB=SBC #imm(2) (unofficial dup)
        //              EC=CPX abs(3) ED=SBC abs(3) EE=INC abs(3) EF=ISC abs(3)
        2, 2, 2, 2, 2, 2, 2, 2,    1, 2, 1, 2, 3, 3, 3, 3,
        // 0xF0..0xFF:  F0=BEQ(2) F1=SBC(zp),Y(2) F2=KIL(1) F3=ISC(zp),Y(2)
        //              F4=NOP zp,X(2) F5=SBC zp,X(2) F6=INC zp,X(2) F7=ISC zp,X(2)
        //              F8=SED(1) F9=SBC abs,Y(3) FA=NOP(1) FB=ISC abs,Y(3)
        //              FC=NOP abs,X(3) FD=SBC abs,X(3) FE=INC abs,X(3) FF=ISC abs,X(3)
        2, 2, 1, 2, 2, 2, 2, 2,    1, 3, 1, 3, 3, 3, 3, 3,
    };

    /// <summary>
    /// Total bytes consumed by the MOS 6502 instruction starting with
    /// the given opcode byte. Always returns 1, 2, or 3.
    /// </summary>
    public static int GetLength(byte opcode) => _table[opcode];
}
