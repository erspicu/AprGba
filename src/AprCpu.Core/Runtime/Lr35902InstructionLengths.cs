namespace AprCpu.Core.Runtime;

/// <summary>
/// LR35902 (Game Boy DMG) instruction length table — first byte determines
/// total instruction length (1, 2, or 3 bytes).
///
/// Reference: Pan Docs § Game Boy CPU + Sharp LR35902 instruction reference.
/// 0xCB is a prefix opcode: total length is 2 bytes (0xCB + sub-opcode);
/// the sub-opcode itself is decoded against the CB instruction set, but the
/// detector still treats 0xCB as a 2-byte fetch unit.
///
/// <para><b>Why hardcoded</b>: this is a tightly-defined ISA invariant
/// (256 entries, derivable from any LR35902 reference) and the detector
/// needs to consult it on every block-detection PC step. Keeping it as a
/// static lookup avoids spec-traversal cost on the hot path. Future:
/// could be derived from spec metadata at compile time and cached the
/// same way (see roadmap MD/design/12-gb-block-jit-roadmap.md §4.1).</para>
///
/// <para>Undefined opcodes (0xD3, 0xDB, 0xDD, 0xE3, 0xE4, 0xEB, 0xEC,
/// 0xED, 0xF4, 0xFC, 0xFD) are listed as length=1 — the detector will
/// hit "decoder returned null" and end the block one instruction earlier
/// anyway, so the length is moot, but staying consistent with hardware
/// fault behaviour (CPU does NOT skip extra bytes for invalid opcodes).</para>
/// </summary>
public static class Lr35902InstructionLengths
{
    private static readonly byte[] _table = new byte[256]
    {
        // 0x00..0x0F
        1, 3, 1, 1, 1, 1, 2, 1,    1, 1, 1, 1, 1, 1, 2, 1,
        // 0x10..0x1F   (STOP=0x10 is documented as 2-byte: 10 00)
        2, 3, 1, 1, 1, 1, 2, 1,    2, 1, 1, 1, 1, 1, 2, 1,
        // 0x20..0x2F
        2, 3, 1, 1, 1, 1, 2, 1,    2, 1, 1, 1, 1, 1, 2, 1,
        // 0x30..0x3F
        2, 3, 1, 1, 1, 1, 2, 1,    2, 1, 1, 1, 1, 1, 2, 1,

        // 0x40..0x4F  (LD r,r' block — 1 byte each, 0x76=HALT also 1)
        1, 1, 1, 1, 1, 1, 1, 1,    1, 1, 1, 1, 1, 1, 1, 1,
        // 0x50..0x5F
        1, 1, 1, 1, 1, 1, 1, 1,    1, 1, 1, 1, 1, 1, 1, 1,
        // 0x60..0x6F
        1, 1, 1, 1, 1, 1, 1, 1,    1, 1, 1, 1, 1, 1, 1, 1,
        // 0x70..0x7F  (0x76=HALT here)
        1, 1, 1, 1, 1, 1, 1, 1,    1, 1, 1, 1, 1, 1, 1, 1,

        // 0x80..0x8F  (ALU A,r — 1 byte each)
        1, 1, 1, 1, 1, 1, 1, 1,    1, 1, 1, 1, 1, 1, 1, 1,
        // 0x90..0x9F
        1, 1, 1, 1, 1, 1, 1, 1,    1, 1, 1, 1, 1, 1, 1, 1,
        // 0xA0..0xAF
        1, 1, 1, 1, 1, 1, 1, 1,    1, 1, 1, 1, 1, 1, 1, 1,
        // 0xB0..0xBF
        1, 1, 1, 1, 1, 1, 1, 1,    1, 1, 1, 1, 1, 1, 1, 1,

        // 0xC0..0xCF
        // C0 RET NZ          C1 POP BC          C2 JP NZ,nn        C3 JP nn
        // C4 CALL NZ,nn      C5 PUSH BC         C6 ADD A,n         C7 RST 0
        // C8 RET Z           C9 RET             CA JP Z,nn         CB CB-prefix
        // CC CALL Z,nn       CD CALL nn         CE ADC A,n         CF RST 8
        1, 1, 3, 3, 3, 1, 2, 1,    1, 1, 3, 2, 3, 3, 2, 1,

        // 0xD0..0xDF
        // D0 RET NC          D1 POP DE          D2 JP NC,nn        D3 *undef*
        // D4 CALL NC,nn      D5 PUSH DE         D6 SUB A,n         D7 RST 10
        // D8 RET C           D9 RETI            DA JP C,nn         DB *undef*
        // DC CALL C,nn       DD *undef*         DE SBC A,n         DF RST 18
        1, 1, 3, 1, 3, 1, 2, 1,    1, 1, 3, 1, 3, 1, 2, 1,

        // 0xE0..0xEF
        // E0 LDH (n),A       E1 POP HL          E2 LD (C),A        E3 *undef*
        // E4 *undef*         E5 PUSH HL         E6 AND A,n         E7 RST 20
        // E8 ADD SP,e8       E9 JP HL           EA LD (nn),A       EB *undef*
        // EC *undef*         ED *undef*         EE XOR A,n         EF RST 28
        2, 1, 1, 1, 1, 1, 2, 1,    2, 1, 3, 1, 1, 1, 2, 1,

        // 0xF0..0xFF
        // F0 LDH A,(n)       F1 POP AF          F2 LD A,(C)        F3 DI
        // F4 *undef*         F5 PUSH AF         F6 OR A,n          F7 RST 30
        // F8 LD HL,SP+e8     F9 LD SP,HL        FA LD A,(nn)       FB EI
        // FC *undef*         FD *undef*         FE CP A,n          FF RST 38
        2, 1, 1, 1, 1, 1, 2, 1,    2, 1, 3, 1, 1, 1, 2, 1,
    };

    /// <summary>
    /// Total bytes consumed by the LR35902 instruction starting with the
    /// given opcode byte. Always returns 1, 2, or 3.
    /// </summary>
    public static int GetLength(byte opcode) => _table[opcode];
}
