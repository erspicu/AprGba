using AprCpu.Core.Runtime;

namespace AprCpu.Core.Runtime;

/// <summary>
/// Intel 8086 instruction length oracle — walks the bus from a given PC
/// and returns the total byte count of the next instruction (1-15).
///
/// 24.6.8a — implements the bus-aware oracle signature
/// <c>Func&lt;IMemoryBus, uint, int&gt;</c> needed by BlockDetector for CISC
/// CPUs whose length depends on bytes AFTER the opcode (ModR/M + displacement).
///
/// Walk order (per Intel iAPX 86,88 §encoding):
///   1. Optional prefixes (segment override 0x26/2E/36/3E, REP 0xF2/F3, LOCK 0xF0)
///   2. Opcode byte
///   3. Optional ModR/M byte (most opcodes)
///      - mod=00 / r/m=110 → 2 disp bytes
///      - mod=01           → 1 disp byte
///      - mod=10           → 2 disp bytes
///      - mod=11 / others  → 0 disp bytes
///   4. Optional immediate (0/1/2 bytes per opcode)
///
/// The opcode → "has ModR/M?" + "imm width" tables below cover the
/// 24.6.x JSON-driven coverage (~217 unique opcodes). Opcodes outside the
/// covered set return length=1 (defensive — gives the BlockDetector a
/// chance to end the block on the unknown opcode rather than walking
/// past it incorrectly).
/// </summary>
public static class X86_16InstructionLengths
{
    /// <summary>
    /// Bus-aware length oracle. Reads up to 15 bytes from <paramref name="bus"/>
    /// starting at <paramref name="pc"/>, returns the total instruction length.
    /// Caps at 15 (the 8086 architectural maximum).
    /// </summary>
    public static int GetLength(IMemoryBus bus, uint pc)
    {
        uint cur = pc;

        // Phase 1: consume prefixes.
        while (cur < pc + 15)
        {
            byte b = bus.ReadByte(cur);
            if (b is 0x26 or 0x2E or 0x36 or 0x3E or 0xF0 or 0xF2 or 0xF3)
            {
                cur++;
                continue;
            }
            break;
        }

        if (cur >= pc + 15) return 15;
        byte opcode = bus.ReadByte(cur);
        cur++;

        // F6/F7 group: length depends on modrm.reg.
        // /0=TEST (with imm), /1=alias TEST (with imm), /2=NOT, /3=NEG,
        // /4=MUL, /5=IMUL, /6=DIV, /7=IDIV.
        if (opcode is 0xF6 or 0xF7 && cur < pc + 15)
        {
            byte modrm = bus.ReadByte(cur);
            cur++;
            int dispBytes = ModRmDispBytes(modrm);
            cur += (uint)dispBytes;
            int reg = (modrm >> 3) & 7;
            if (reg <= 1)
            {
                // TEST has an imm — 1 byte for F6, 2 bytes for F7.
                cur += (uint)(opcode == 0xF6 ? 1 : 2);
            }
            // /2-/7: no imm.
            var totalF67 = (int)(cur - pc);
            return totalF67 > 15 ? 15 : totalF67;
        }

        // Phase 2: opcode → ModR/M? + imm width
        var (hasModrm, immBytes) = OpcodeShape(opcode);

        // Phase 3: ModR/M + displacement
        if (hasModrm && cur < pc + 15)
        {
            byte modrm = bus.ReadByte(cur);
            cur++;
            int dispBytes = ModRmDispBytes(modrm);
            cur += (uint)dispBytes;
        }

        // Phase 4: immediate
        cur += (uint)immBytes;

        var total = (int)(cur - pc);
        return total > 15 ? 15 : total;
    }

    /// <summary>
    /// (hasModrm, immBytes) for each 8086 opcode in our JSON-driven coverage.
    /// Opcodes not listed default to (false, 0) — length 1.
    ///
    /// Sourced from Intel iAPX 86,88 manual + spec/x86-16/i8086/groups/*.
    /// Ordering: data-transfer, ALU, control flow, shift/rotate, string,
    /// flag-manip, IO, INT/IRET, FE/FF group, BCD.
    /// </summary>
    private static (bool hasModrm, int immBytes) OpcodeShape(byte opcode)
    {
        switch (opcode)
        {
            // ---------- ALU r/m,r and r,r/m forms (00-3D) ----------
            case 0x00: case 0x01: case 0x02: case 0x03:    return (true, 0); // ADD
            case 0x04: return (false, 1);                                    // ADD AL,imm8
            case 0x05: return (false, 2);                                    // ADD AX,imm16
            case 0x08: case 0x09: case 0x0A: case 0x0B:    return (true, 0); // OR
            case 0x0C: return (false, 1);
            case 0x0D: return (false, 2);
            case 0x10: case 0x11: case 0x12: case 0x13:    return (true, 0); // ADC
            case 0x14: return (false, 1);
            case 0x15: return (false, 2);
            case 0x18: case 0x19: case 0x1A: case 0x1B:    return (true, 0); // SBB
            case 0x1C: return (false, 1);
            case 0x1D: return (false, 2);
            case 0x20: case 0x21: case 0x22: case 0x23:    return (true, 0); // AND
            case 0x24: return (false, 1);
            case 0x25: return (false, 2);
            case 0x28: case 0x29: case 0x2A: case 0x2B:    return (true, 0); // SUB
            case 0x2C: return (false, 1);
            case 0x2D: return (false, 2);
            case 0x30: case 0x31: case 0x32: case 0x33:    return (true, 0); // XOR
            case 0x34: return (false, 1);
            case 0x35: return (false, 2);
            case 0x38: case 0x39: case 0x3A: case 0x3B:    return (true, 0); // CMP
            case 0x3C: return (false, 1);
            case 0x3D: return (false, 2);

            // ---------- BCD adjust ops (no operand) ----------
            case 0x27: case 0x2F: case 0x37: case 0x3F:    return (false, 0); // DAA/DAS/AAA/AAS

            // ---------- INC/DEC r16 ----------
            case 0x40: case 0x41: case 0x42: case 0x43:
            case 0x44: case 0x45: case 0x46: case 0x47:    return (false, 0); // INC
            case 0x48: case 0x49: case 0x4A: case 0x4B:
            case 0x4C: case 0x4D: case 0x4E: case 0x4F:    return (false, 0); // DEC

            // ---------- PUSH/POP r16 ----------
            case 0x50: case 0x51: case 0x52: case 0x53:
            case 0x54: case 0x55: case 0x56: case 0x57:    return (false, 0); // PUSH
            case 0x58: case 0x59: case 0x5A: case 0x5B:
            case 0x5C: case 0x5D: case 0x5E: case 0x5F:    return (false, 0); // POP

            // ---------- PUSH/POP sreg ----------
            case 0x06: case 0x0E: case 0x16: case 0x1E:    return (false, 0);
            case 0x07: case 0x17: case 0x1F:               return (false, 0);

            // ---------- Jcc rel8 ----------
            case 0x70: case 0x71: case 0x72: case 0x73:
            case 0x74: case 0x75: case 0x76: case 0x77:
            case 0x78: case 0x79: case 0x7A: case 0x7B:
            case 0x7C: case 0x7D: case 0x7E: case 0x7F:    return (false, 1);

            // ---------- 0x80-0x83 ALU r/m, imm group ----------
            case 0x80: case 0x82:                          return (true, 1); // r/m8, imm8
            case 0x81:                                     return (true, 2); // r/m16, imm16
            case 0x83:                                     return (true, 1); // r/m16, sext-imm8

            // ---------- TEST/XCHG r/m,r ----------
            case 0x84: case 0x85: case 0x86: case 0x87:    return (true, 0);

            // ---------- MOV r/m,r and r,r/m ----------
            case 0x88: case 0x89: case 0x8A: case 0x8B:    return (true, 0);

            // ---------- MOV r/m,sreg / sreg,r/m ----------
            case 0x8C: case 0x8E:                          return (true, 0);

            // ---------- LEA r16, m ----------
            case 0x8D:                                     return (true, 0);

            // ---------- POP r/m16 ----------
            case 0x8F:                                     return (true, 0);

            // ---------- XCHG AX,r16 (90-97) ----------
            case 0x90: case 0x91: case 0x92: case 0x93:
            case 0x94: case 0x95: case 0x96: case 0x97:    return (false, 0);

            // ---------- CBW/CWD ----------
            case 0x98: case 0x99:                          return (false, 0);

            // ---------- PUSHF/POPF/SAHF/LAHF ----------
            case 0x9C: case 0x9D: case 0x9E: case 0x9F:    return (false, 0);

            // ---------- MOV moffs (A0-A3) ----------
            case 0xA0: case 0xA1: case 0xA2: case 0xA3:    return (false, 2);

            // ---------- TEST AL/AX, imm ----------
            case 0xA8:                                     return (false, 1);
            case 0xA9:                                     return (false, 2);

            // ---------- String ops (no operand bytes; REP prefix may precede) ----------
            case 0xA4: case 0xA5: case 0xA6: case 0xA7:    return (false, 0); // MOVS/CMPS
            case 0xAA: case 0xAB: case 0xAC: case 0xAD:    return (false, 0); // STOS/LODS
            case 0xAE: case 0xAF:                          return (false, 0); // SCAS

            // ---------- MOV r, imm ----------
            case 0xB0: case 0xB1: case 0xB2: case 0xB3:
            case 0xB4: case 0xB5: case 0xB6: case 0xB7:    return (false, 1); // MOV r8, imm8
            case 0xB8: case 0xB9: case 0xBA: case 0xBB:
            case 0xBC: case 0xBD: case 0xBE: case 0xBF:    return (false, 2); // MOV r16, imm16

            // ---------- RET imm16 / RET ----------
            case 0xC2:                                     return (false, 2);
            case 0xC3:                                     return (false, 0);

            // ---------- LES/LDS r16, m32 ----------
            case 0xC4: case 0xC5:                          return (true, 0);

            // ---------- MOV r/m, imm ----------
            case 0xC6:                                     return (true, 1);
            case 0xC7:                                     return (true, 2);

            // ---------- INT 3 / INT imm8 / INTO / IRET ----------
            case 0xCC: case 0xCE: case 0xCF:               return (false, 0);
            case 0xCD:                                     return (false, 1);

            // ---------- Shift/rotate D0-D3 ----------
            case 0xD0: case 0xD1: case 0xD2: case 0xD3:    return (true, 0);

            // ---------- AAM/AAD imm8 ----------
            case 0xD4: case 0xD5:                          return (false, 1);

            // ---------- LOOP family / JCXZ ----------
            case 0xE0: case 0xE1: case 0xE2: case 0xE3:    return (false, 1);

            // ---------- IN/OUT imm8 ----------
            case 0xE4: case 0xE5: case 0xE6: case 0xE7:    return (false, 1);

            // ---------- CALL rel16 / JMP rel16 / JMP rel8 ----------
            case 0xE8: case 0xE9:                          return (false, 2);
            case 0xEB:                                     return (false, 1);

            // ---------- IN/OUT DX ----------
            case 0xEC: case 0xED: case 0xEE: case 0xEF:    return (false, 0);

            // ---------- HLT / NOP / flag-manip ----------
            // (NOP 0x90 covered by XCHG AX,AX above.)
            case 0xF4:                                     return (false, 0); // HLT
            case 0xF5:                                     return (false, 0); // CMC
            case 0xF8: case 0xF9:                          return (false, 0); // CLC/STC
            case 0xFA: case 0xFB:                          return (false, 0); // CLI/STI
            case 0xFC: case 0xFD:                          return (false, 0); // CLD/STD

            // F6/F7 group is handled specially in GetLength (peeks modrm.reg
            // because length depends on sub-op).

            // ---------- FE/FF group ----------
            case 0xFE: case 0xFF:                          return (true, 0);

            default:
                // Unknown opcode — return length 1 so BlockDetector can
                // end the block on the undecodable opcode at the next
                // dispatch.
                return (false, 0);
        }
    }

    /// <summary>
    /// Displacement bytes implied by a ModR/M byte:
    ///   mod=00 / r/m=110 → 2 (direct disp16)
    ///   mod=01           → 1 (sign-extended disp8)
    ///   mod=10           → 2 (disp16)
    ///   mod=11 / others  → 0
    /// </summary>
    private static int ModRmDispBytes(byte modrm)
    {
        int mod = (modrm >> 6) & 3;
        int rm  = modrm & 7;
        return mod switch
        {
            0 => rm == 6 ? 2 : 0,
            1 => 1,
            2 => 2,
            _ => 0,   // mod=11 register-direct
        };
    }
}
