// X86Alu — pure 8086 ALU primitives with flag computation.
//
// Phase 24.2.3 deliverable: every arithmetic / logical operation that
// the ALU groups (0x00-0x3D + group 0x80-0x83) need, factored into
// stateless static helpers that take/produce flag updates via X86State.
//
// Flag rules summary (8086, see Intel iAPX 86,88 Programmer's Manual):
//
//   Op       | CF                      | OF                        | AF          | SF/ZF/PF
//   ---------+-------------------------+---------------------------+-------------+----------
//   ADD/ADC  | carry out of MSB        | sign(a)==sign(b)≠sign(r)  | nibble carry| from result
//   SUB/SBB  | borrow into MSB         | sign(a)≠sign(b) AND        | nibble borr | from result
//   /CMP     |                         | sign(a)≠sign(r)           |             |
//   AND/OR   | 0                       | 0                         | undefined   | from result
//   /XOR     |                         |                           | (we set 0)  |
//   /TEST    |                         |                           |             |
//
// SF: high bit of result.
// ZF: result == 0.
// PF: low-byte parity (even = 1).
//
// "AF undefined" for logical ops — we set it to 0 for determinism;
// Tom Harte SST tests sometimes flag this, but per Intel docs the
// expected value is undefined so any consistent behaviour passes.

namespace AprX86.Cli.Cpu;

public enum AluOp : byte
{
    Add = 0,    // 0x00-0x05 / 0x80-0x83 reg=000
    Or  = 1,    // 0x08-0x0D / 0x80-0x83 reg=001
    Adc = 2,    // 0x10-0x15 / 0x80-0x83 reg=010
    Sbb = 3,    // 0x18-0x1D / 0x80-0x83 reg=011
    And = 4,    // 0x20-0x25 / 0x80-0x83 reg=100
    Sub = 5,    // 0x28-0x2D / 0x80-0x83 reg=101
    Xor = 6,    // 0x30-0x35 / 0x80-0x83 reg=110
    Cmp = 7,    // 0x38-0x3D / 0x80-0x83 reg=111  (= SUB but no result store)
}

public static class X86Alu
{
    /// <summary>True if <paramref name="op"/> doesn't write the result back
    /// (currently only CMP). Caller checks this before the writeback step.</summary>
    public static bool IsCompareOnly(AluOp op) => op == AluOp.Cmp;

    /// <summary>
    /// Compute the 8-bit ALU result. Flag bits in <paramref name="state"/>
    /// are updated to reflect the operation (per 8086 docs).
    /// </summary>
    public static byte Execute8(AluOp op, byte a, byte b, X86State state)
    {
        // For AF/OF, the formula `((a ^ b ^ result) & 0x10)` and
        // `((a ^ result) & (b ^ result) & 0x80)` already accounts for the
        // CF carry/borrow folded into `result`. We must pass the ORIGINAL
        // `b`, NOT `b + cfIn` (which would overflow at b=0xFF + CF=1).
        int cfIn = state.FlagC ? 1 : 0;
        int result;
        switch (op)
        {
            case AluOp.Add: result = a + b;            SetArithFlags8(a, b, result, isSub: false, state); return (byte)result;
            case AluOp.Adc: result = a + b + cfIn;     SetArithFlags8(a, b, result, isSub: false, state); return (byte)result;
            case AluOp.Sub:
            case AluOp.Cmp: result = a - b;            SetArithFlags8(a, b, result, isSub: true,  state); return (byte)result;
            case AluOp.Sbb: result = a - b - cfIn;     SetArithFlags8(a, b, result, isSub: true,  state); return (byte)result;
            case AluOp.And: result = a & b;            SetLogicFlags8((byte)result, state); return (byte)result;
            case AluOp.Or:  result = a | b;            SetLogicFlags8((byte)result, state); return (byte)result;
            case AluOp.Xor: result = a ^ b;            SetLogicFlags8((byte)result, state); return (byte)result;
            default: throw new ArgumentOutOfRangeException(nameof(op));
        }
    }

    /// <summary>16-bit form. Identical structure to <see cref="Execute8"/> but
    /// flag widths cover 16 bits.</summary>
    public static ushort Execute16(AluOp op, ushort a, ushort b, X86State state)
    {
        int cfIn = state.FlagC ? 1 : 0;
        int result;
        switch (op)
        {
            case AluOp.Add: result = a + b;            SetArithFlags16(a, b, result, isSub: false, state); return (ushort)result;
            case AluOp.Adc: result = a + b + cfIn;     SetArithFlags16(a, b, result, isSub: false, state); return (ushort)result;
            case AluOp.Sub:
            case AluOp.Cmp: result = a - b;            SetArithFlags16(a, b, result, isSub: true,  state); return (ushort)result;
            case AluOp.Sbb: result = a - b - cfIn;     SetArithFlags16(a, b, result, isSub: true,  state); return (ushort)result;
            case AluOp.And: result = a & b;            SetLogicFlags16((ushort)result, state); return (ushort)result;
            case AluOp.Or:  result = a | b;            SetLogicFlags16((ushort)result, state); return (ushort)result;
            case AluOp.Xor: result = a ^ b;            SetLogicFlags16((ushort)result, state); return (ushort)result;
            default: throw new ArgumentOutOfRangeException(nameof(op));
        }
    }

    // ---------------- Flag computation ----------------

    private static void SetArithFlags8(byte a, byte b, int rawResult, bool isSub, X86State s)
    {
        byte r = (byte)rawResult;
        s.FlagC = isSub
            ? (rawResult < 0)              // borrow into MSB
            : ((rawResult & 0x100) != 0);  // carry out of MSB
        s.FlagS = (r & 0x80) != 0;
        s.FlagZ = r == 0;
        s.FlagP = ParityEven(r);
        // AF = carry/borrow out of bit 3 (nibble boundary). For both add
        // and sub the formula is the same XOR pattern.
        s.FlagA = ((a ^ b ^ r) & 0x10) != 0;
        // OF: overflow detection.
        if (isSub)
        {
            // operands differ in sign AND result has different sign than a
            s.FlagO = (((a ^ b) & (a ^ r)) & 0x80) != 0;
        }
        else
        {
            // operands same sign AND result has different sign
            s.FlagO = (((a ^ r) & (b ^ r)) & 0x80) != 0;
        }
    }

    private static void SetArithFlags16(ushort a, ushort b, int rawResult, bool isSub, X86State s)
    {
        ushort r = (ushort)rawResult;
        s.FlagC = isSub
            ? (rawResult < 0)
            : ((rawResult & 0x10000) != 0);
        s.FlagS = (r & 0x8000) != 0;
        s.FlagZ = r == 0;
        s.FlagP = ParityEven((byte)r);    // PF still derived from low byte only
        s.FlagA = ((a ^ b ^ r) & 0x10) != 0;
        if (isSub)
        {
            s.FlagO = (((a ^ b) & (a ^ r)) & 0x8000) != 0;
        }
        else
        {
            s.FlagO = (((a ^ r) & (b ^ r)) & 0x8000) != 0;
        }
    }

    private static void SetLogicFlags8(byte r, X86State s)
    {
        s.FlagC = false;
        s.FlagO = false;
        s.FlagS = (r & 0x80) != 0;
        s.FlagZ = r == 0;
        s.FlagP = ParityEven(r);
        s.FlagA = false;     // officially undefined; we pick 0 for determinism
    }

    private static void SetLogicFlags16(ushort r, X86State s)
    {
        s.FlagC = false;
        s.FlagO = false;
        s.FlagS = (r & 0x8000) != 0;
        s.FlagZ = r == 0;
        s.FlagP = ParityEven((byte)r);
        s.FlagA = false;
    }

    /// <summary>
    /// 8086 PF flag: 1 if the low byte of the result has even number
    /// of 1-bits. Use a precomputed table for hot path.
    /// </summary>
    private static readonly byte[] s_parityTable = BuildParityTable();

    private static byte[] BuildParityTable()
    {
        var t = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            int v = i, c = 0;
            for (int j = 0; j < 8; j++) { c += v & 1; v >>= 1; }
            t[i] = (byte)((c & 1) == 0 ? 1 : 0);
        }
        return t;
    }

    public static bool ParityEven(byte v) => s_parityTable[v] != 0;

    // ---------------- INC / DEC ----------------
    //
    // Quirk: INC/DEC do NOT affect CF (carry flag) — the original CF is
    // preserved. All other arithmetic flags (OF/SF/ZF/AF/PF) are updated
    // per the result. This is one of the rare 8086 instructions that
    // touches some flags but not CF.

    public static byte Inc8(byte a, X86State s)
    {
        bool savedCf = s.FlagC;
        byte r = X86Alu.Execute8(AluOp.Add, a, 1, s);
        s.FlagC = savedCf;
        return r;
    }

    public static byte Dec8(byte a, X86State s)
    {
        bool savedCf = s.FlagC;
        byte r = X86Alu.Execute8(AluOp.Sub, a, 1, s);
        s.FlagC = savedCf;
        return r;
    }

    public static ushort Inc16(ushort a, X86State s)
    {
        bool savedCf = s.FlagC;
        ushort r = X86Alu.Execute16(AluOp.Add, a, 1, s);
        s.FlagC = savedCf;
        return r;
    }

    public static ushort Dec16(ushort a, X86State s)
    {
        bool savedCf = s.FlagC;
        ushort r = X86Alu.Execute16(AluOp.Sub, a, 1, s);
        s.FlagC = savedCf;
        return r;
    }
}
