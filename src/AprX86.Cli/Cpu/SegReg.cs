// 8086 segment-register encoding for SREG fields in opcodes that
// take a segment register (PUSH/POP/MOV with sreg, segment override
// prefix). Order matches the architectural 2-bit encoding.
//
// Note that the order DOES NOT match the field order in X86State —
// don't pun a SegReg cast directly to X86State field index. Use
// X86State.GetSeg / SetSeg accessors instead.

namespace AprX86.Cli.Cpu;

public enum SegReg : byte
{
    ES = 0,   // sreg encoding 00
    CS = 1,   // sreg encoding 01
    SS = 2,   // sreg encoding 10
    DS = 3,   // sreg encoding 11
}
