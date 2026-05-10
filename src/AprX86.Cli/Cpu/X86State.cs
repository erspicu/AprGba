// X86State — register file + flags for 8086 real mode.
//
// Phase 24.1: bare data type. CPU execution (X86LegacyCpu, X86JsonCpu)
// will operate on this; the framework spec-driven path will route via
// CpuStateLayout once spec/cpu/x86-16/i8086/cpu.json lands (phase 24.4).
//
// Layout intent:
//   AX/BX/CX/DX  — split into AL/AH/BL/BH/CL/CH/DL/DH halves via
//                  StructLayout(Explicit) (mirrors Apr86's approach)
//   SP/BP/SI/DI  — 16-bit
//   CS/DS/SS/ES  — segment registers (16-bit)
//   IP           — instruction pointer (16-bit)
//   FLAGS        — 16-bit packed (only 9 bits used for 8086 flags)

using System.Runtime.InteropServices;

namespace AprX86.Cli.Cpu;

[StructLayout(LayoutKind.Explicit, Size = 2)]
public struct RegWord
{
    [FieldOffset(0)] public ushort X;   // word view (AX / BX / ...)
    [FieldOffset(0)] public byte L;     // low  byte (AL / BL / ...)
    [FieldOffset(1)] public byte H;     // high byte (AH / BH / ...)
}

public sealed class X86State
{
    // General-purpose registers (with byte-half aliasing).
    public RegWord A;       // AX / AH:AL
    public RegWord B;       // BX / BH:BL
    public RegWord C;       // CX / CH:CL
    public RegWord D;       // DX / DH:DL

    // Address-related 16-bit registers.
    public ushort SP;       // stack pointer
    public ushort BP;       // base pointer
    public ushort SI;       // source index
    public ushort DI;       // destination index

    // Segment registers.
    public ushort CS;       // code segment
    public ushort DS;       // data segment
    public ushort SS;       // stack segment
    public ushort ES;       // extra segment

    public ushort IP;       // instruction pointer

    // Flags — 9 architectural bits on 8086 (FLAGS register; bits 1, 3, 5,
    // 12-15 are reserved/undefined). Stored as discrete bools for clarity;
    // packed via GetFlags()/SetFlags() when the architectural register is
    // read/written.
    public bool FlagC;      // bit 0  — carry
    public bool FlagP;      // bit 2  — parity (even-parity of low byte)
    public bool FlagA;      // bit 4  — auxiliary carry (BCD)
    public bool FlagZ;      // bit 6  — zero
    public bool FlagS;      // bit 7  — sign
    public bool FlagT;      // bit 8  — trap (single-step)
    public bool FlagI;      // bit 9  — interrupt enable
    public bool FlagD;      // bit 10 — direction (string ops)
    public bool FlagO;      // bit 11 — overflow

    // Sprint 27.11b — i80286 exception state. Mirrors EXC_PENDING /
    // EXC_VECTOR / EXC_ERROR slots from the i80286 register file. For
    // i8086 / i80186 these are always 0 (the spec doesn't declare the
    // slots, so the backend reads them as 0). Subsequent sprints
    // (27.11c+) will start populating these from PE=1 fault paths.
    public byte   ExcPending;   // 0 = no fault, 1 = exception raised
    public byte   ExcVector;    // 80286 fault number (#GP=13, #NP=11, ...)
    public ushort ExcError;     // selector + EXT/IDT/TI for #TS/#NP/#SS/#GP

    /// <summary>
    /// Reset to architectural power-on / RESET state of an 8086:
    /// CS=0xFFFF, IP=0x0000, all other regs / flags zero. After RESET the
    /// first fetch is at FFFF:0000 = physical 0xFFFF0, the canonical BIOS
    /// reset vector.
    ///
    /// Test ROM loaders (phase 24.3+) typically override this with
    /// CS:IP = 0:0x100 (CP/M-style .com convention) so they can skip BIOS.
    /// </summary>
    public void Reset()
    {
        A.X = B.X = C.X = D.X = 0;
        SP = BP = SI = DI = 0;
        CS = 0xFFFF;
        DS = SS = ES = 0;
        IP = 0;
        FlagC = FlagP = FlagA = FlagZ = FlagS = false;
        FlagT = FlagI = FlagD = FlagO = false;
        ExcPending = ExcVector = 0;
        ExcError = 0;
    }

    /// <summary>
    /// Pack the discrete flag bools into the architectural 16-bit FLAGS
    /// register layout (8086). Reserved bits 1/3/5 read as 1/0/0 per Intel
    /// docs; bit 15/14/13/12 read as 1 on 8086 (changed on 80286+).
    /// </summary>
    public ushort GetFlags()
    {
        ushort f = 0xF002;     // bits 15..12 + bit 1 forced (8086 idiom)
        if (FlagC) f |= 0x0001;
        if (FlagP) f |= 0x0004;
        if (FlagA) f |= 0x0010;
        if (FlagZ) f |= 0x0040;
        if (FlagS) f |= 0x0080;
        if (FlagT) f |= 0x0100;
        if (FlagI) f |= 0x0200;
        if (FlagD) f |= 0x0400;
        if (FlagO) f |= 0x0800;
        return f;
    }

    public void SetFlags(ushort f)
    {
        FlagC = (f & 0x0001) != 0;
        FlagP = (f & 0x0004) != 0;
        FlagA = (f & 0x0010) != 0;
        FlagZ = (f & 0x0040) != 0;
        FlagS = (f & 0x0080) != 0;
        FlagT = (f & 0x0100) != 0;
        FlagI = (f & 0x0200) != 0;
        FlagD = (f & 0x0400) != 0;
        FlagO = (f & 0x0800) != 0;
    }

    // --- Register-by-encoding accessors ---
    //
    // ModR/M reg / r/m fields use these encodings:
    //   8-bit (w=0):   000=AL 001=CL 010=DL 011=BL 100=AH 101=CH 110=DH 111=BH
    //   16-bit (w=1):  000=AX 001=CX 010=DX 011=BX 100=SP 101=BP 110=SI 111=DI
    //   sreg (2-bit):  00=ES  01=CS  10=SS  11=DS
    //
    // Note encoding ordering does NOT match field-declaration order
    // (architectural: A/C/D/B vs. structural: A/B/C/D). These methods
    // are the canonical way for emitters / decoders to read registers
    // by their architectural index.

    public byte GetReg8(int idx) => (idx & 7) switch
    {
        0 => A.L, 1 => C.L, 2 => D.L, 3 => B.L,
        4 => A.H, 5 => C.H, 6 => D.H, 7 => B.H,
        _ => 0,
    };

    public void SetReg8(int idx, byte value)
    {
        switch (idx & 7)
        {
            case 0: A.L = value; break;
            case 1: C.L = value; break;
            case 2: D.L = value; break;
            case 3: B.L = value; break;
            case 4: A.H = value; break;
            case 5: C.H = value; break;
            case 6: D.H = value; break;
            case 7: B.H = value; break;
        }
    }

    public ushort GetReg16(int idx) => (idx & 7) switch
    {
        0 => A.X, 1 => C.X, 2 => D.X, 3 => B.X,
        4 => SP,  5 => BP,  6 => SI,  7 => DI,
        _ => 0,
    };

    public void SetReg16(int idx, ushort value)
    {
        switch (idx & 7)
        {
            case 0: A.X = value; break;
            case 1: C.X = value; break;
            case 2: D.X = value; break;
            case 3: B.X = value; break;
            case 4: SP  = value; break;
            case 5: BP  = value; break;
            case 6: SI  = value; break;
            case 7: DI  = value; break;
        }
    }

    public ushort GetSeg(SegReg s) => s switch
    {
        SegReg.ES => ES,
        SegReg.CS => CS,
        SegReg.SS => SS,
        SegReg.DS => DS,
        _         => 0,
    };

    public void SetSeg(SegReg s, ushort value)
    {
        switch (s)
        {
            case SegReg.ES: ES = value; break;
            case SegReg.CS: CS = value; break;
            case SegReg.SS: SS = value; break;
            case SegReg.DS: DS = value; break;
        }
    }
}
