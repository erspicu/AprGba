// HleBios — High-Level Emulation of the IBM PC BIOS.
//
// Mechanism (Phase 28.2):
//   For each supported INT vector v, IVT[v] = F000:00v. We don't put
//   any actual opcodes at those addresses; the emulator thread sees
//   CS:IP land there (because the CPU's INT v instruction pushed
//   FLAGS+CS+IP and jumped to IVT[v]), calls HleBios.Dispatch(v) which
//   runs the C# handler, and then HleBios simulates the IRET (pops
//   FLAGS+CS+IP from the stack so execution resumes at the
//   instruction after the original INT v).
//
// Phase 28.2 implements just INT 10h (video). Subsequent sub-phases
// add INT 13h / 16h / 19h / 1Ah / ...
//
// We deliberately mirror Intel's "BIOS INT call calling convention":
// the handler reads registers from the CPU state, writes outputs back
// to the same state, and the IRET preserves whatever the handler put
// there (Intel doesn't push GPRs).

using AprPc.Cli.Memory;
using AprX86.Cli.Cpu;
using AprX86.Cli.Memory;

namespace AprPc.Cli.Bios;

public sealed class HleBios
{
    /// <summary>
    /// HLE trap segment. Any CS:IP landing in F000:0000-F000:00FF on a
    /// vector we own is treated as a BIOS call. The low byte of IP
    /// names the vector.
    /// </summary>
    public const ushort HleTrapSegment = 0xF000;

    private readonly X86JsonCpu _cpu;
    private readonly PcMemoryBus _bus;
    private readonly bool _traceInt;

    // Bitset of which vectors this HLE implementation handles. Used
    // both to populate IVT entries and to decide whether a CS:IP land
    // at F000:00xx is ours.
    private readonly bool[] _owned = new bool[256];

    public HleBios(X86JsonCpu cpu, PcMemoryBus bus, bool traceInt = false)
    {
        _cpu      = cpu ?? throw new ArgumentNullException(nameof(cpu));
        _bus      = bus ?? throw new ArgumentNullException(nameof(bus));
        _traceInt = traceInt;
    }

    /// <summary>
    /// Install IVT entries for every supported vector. Call once after
    /// PcMemoryBus.Reset() has zeroed the IVT region.
    /// </summary>
    public void Install()
    {
        InstallVector(0x10, "video");
        // 28.3 / 28.4 / 28.5 / 28.6 add 0x16 / 0x1A / 0x13 / 0x19 here.
    }

    private void InstallVector(byte vector, string label)
    {
        // IVT[v] is 4 bytes at physical 0x0000 + v*4: low IP, high IP, low CS, high CS.
        int slot = vector * 4;
        _bus.WriteWord16(slot,     vector);              // IP = vector (low byte populated, high byte = 0)
        _bus.WriteWord16(slot + 2, HleTrapSegment);      // CS = 0xF000
        _owned[vector] = true;
        if (_traceInt)
            Console.Error.WriteLine($"  [HLE] installed INT {vector:X2}h ({label}) -> {HleTrapSegment:X4}:{vector:X4}");
    }

    /// <summary>
    /// Decide whether the CPU is currently parked in an HLE trap. The
    /// emulator thread calls this before each Step() — if true, it
    /// dispatches the handler and skips the underlying Step.
    /// </summary>
    public bool IsTrapped(ushort cs, ushort ip)
        => cs == HleTrapSegment && ip <= 0xFF && _owned[ip];

    /// <summary>
    /// Run the C# handler for the given vector and simulate IRET so
    /// execution returns to the instruction after the original INT v.
    /// </summary>
    public void Dispatch(byte vector)
    {
        var state = _cpu.State;
        if (_traceInt)
            Console.Error.WriteLine(
                $"  [HLE] INT {vector:X2}h AH={state.A.H:X2} AL={state.A.L:X2} BX={state.B.X:X4} CX={state.C.X:X4} DX={state.D.X:X4}");

        switch (vector)
        {
            case 0x10: Int10(state); break;
            default:
                // Unhandled — just IRET, no side effect.
                break;
        }

        SimulateIret(state);
        _cpu.LoadState(state);
    }

    // ---------- INT 10h: video ----------

    private void Int10(AprX86.Cli.Cpu.X86State state)
    {
        switch (state.A.H)
        {
            case 0x00: Int10_SetVideoMode(state, state.A.L); break;
            case 0x02: Int10_SetCursorPos(state, state.B.H, state.D.L, state.D.H); break;
            case 0x03: Int10_GetCursorPos(state, state.B.H); break;
            case 0x06: Int10_ScrollUp(state); break;
            case 0x09: Int10_WriteCharAttr(state, state.A.L, state.B.L, state.C.X); break;
            case 0x0E: Int10_Teletype(state, state.A.L, state.B.L); break;
            case 0x0F: Int10_GetVideoMode(state); break;
            default:
                if (_traceInt)
                    Console.Error.WriteLine($"  [HLE] INT 10h AH={state.A.H:X2} not implemented; no-op");
                break;
        }
    }

    /// <summary>AH=00 — set video mode. We only support mode 3 (80×25 color text).</summary>
    private void Int10_SetVideoMode(AprX86.Cli.Cpu.X86State state, byte mode)
    {
        // BDA 0040:0049 stores current video mode.
        _bus.WriteByte(0x00449, mode);
        // BDA 0040:004A stores screen width in columns (80).
        _bus.WriteWord16(0x0044A, 80);
        // BDA 0040:004C stores video buffer size (4000 bytes for mode 3).
        _bus.WriteWord16(0x0044C, 80 * 25 * 2);
        // BDA 0040:0050-005F = cursor positions per video page (8 pages).
        for (int p = 0; p < 8; p++) _bus.WriteWord16(0x00450 + p * 2, 0);
        // BDA 0040:0062 = active video page = 0.
        _bus.WriteByte(0x00462, 0);

        // Clear the framebuffer for mode 3 (write space + light-grey-on-black attribute).
        if (mode == 0x03)
        {
            for (int i = 0; i < 80 * 25; i++)
            {
                _bus.WriteByte(0xB8000 + i * 2,     (byte)' ');
                _bus.WriteByte(0xB8000 + i * 2 + 1, 0x07);
            }
        }
    }

    /// <summary>AH=02 — set cursor position. BH = page, DH = row, DL = col.</summary>
    private void Int10_SetCursorPos(AprX86.Cli.Cpu.X86State state, byte page, byte col, byte row)
    {
        int slot = 0x00450 + (page & 7) * 2;
        _bus.WriteByte(slot,     col);
        _bus.WriteByte(slot + 1, row);
    }

    /// <summary>AH=03 — get cursor position. Returns DH=row, DL=col, CH/CL=cursor shape.</summary>
    private void Int10_GetCursorPos(AprX86.Cli.Cpu.X86State state, byte page)
    {
        int slot = 0x00450 + (page & 7) * 2;
        state.D.L = _bus.ReadByte(slot);
        state.D.H = _bus.ReadByte(slot + 1);
        state.C.H = 0x06;        // cursor start scan line
        state.C.L = 0x07;        // cursor end scan line
    }

    /// <summary>AH=06 — scroll up. AL = lines (0 = clear whole window). BH = attr for new lines.</summary>
    private void Int10_ScrollUp(AprX86.Cli.Cpu.X86State state)
    {
        // CH/CL = top-left (row/col); DH/DL = bottom-right.
        byte rowTop = state.C.H, colLeft  = state.C.L;
        byte rowBot = state.D.H, colRight = state.D.L;
        byte lines  = state.A.L;
        byte attr   = state.B.H;
        if (rowTop > rowBot || colLeft > colRight) return;

        if (lines == 0 || lines > (rowBot - rowTop + 1))
        {
            // Clear the window entirely with `attr`.
            for (int r = rowTop; r <= rowBot; r++)
            for (int c = colLeft; c <= colRight; c++)
            {
                int off = 0xB8000 + (r * 80 + c) * 2;
                _bus.WriteByte(off,     (byte)' ');
                _bus.WriteByte(off + 1, attr);
            }
            return;
        }

        for (int r = rowTop; r <= rowBot - lines; r++)
        for (int c = colLeft; c <= colRight; c++)
        {
            int dstOff = 0xB8000 + (r * 80 + c) * 2;
            int srcOff = 0xB8000 + ((r + lines) * 80 + c) * 2;
            _bus.WriteByte(dstOff,     _bus.ReadByte(srcOff));
            _bus.WriteByte(dstOff + 1, _bus.ReadByte(srcOff + 1));
        }
        for (int r = rowBot - lines + 1; r <= rowBot; r++)
        for (int c = colLeft; c <= colRight; c++)
        {
            int off = 0xB8000 + (r * 80 + c) * 2;
            _bus.WriteByte(off,     (byte)' ');
            _bus.WriteByte(off + 1, attr);
        }
    }

    /// <summary>AH=09 — write char + attr at cursor, replicated CX times.</summary>
    private void Int10_WriteCharAttr(AprX86.Cli.Cpu.X86State state, byte ch, byte attr, ushort count)
    {
        byte page = state.B.H;
        int slot = 0x00450 + (page & 7) * 2;
        byte col = _bus.ReadByte(slot);
        byte row = _bus.ReadByte(slot + 1);
        for (int i = 0; i < count; i++)
        {
            int c = col + i;
            int r = row + c / 80;
            c %= 80;
            if (r >= 25) break;
            int off = 0xB8000 + (r * 80 + c) * 2;
            _bus.WriteByte(off,     ch);
            _bus.WriteByte(off + 1, attr);
        }
    }

    /// <summary>AH=0E — teletype: write AL with default attr, advance cursor, handle BS/LF/CR/BEL.</summary>
    private void Int10_Teletype(AprX86.Cli.Cpu.X86State state, byte ch, byte attr)
    {
        byte page = state.B.H;
        int slot = 0x00450 + (page & 7) * 2;
        byte col = _bus.ReadByte(slot);
        byte row = _bus.ReadByte(slot + 1);

        switch (ch)
        {
            case 0x07: /* BEL — ignored for now */ break;
            case 0x08: /* BS  */ if (col > 0) col--; break;
            case 0x0A: /* LF  */ row++; break;
            case 0x0D: /* CR  */ col = 0; break;
            default:
                int off = 0xB8000 + (row * 80 + col) * 2;
                _bus.WriteByte(off,     ch);
                _bus.WriteByte(off + 1, 0x07);   // standard light-grey-on-black; Phase 28's INT 10h AH=0E ignores BL by default
                col++;
                if (col >= 80) { col = 0; row++; }
                break;
        }
        if (row >= 25)
        {
            // Scroll the entire window up one line; cursor stays on last row.
            state.A.L = 1; state.B.H = 0x07;
            state.C.H = 0; state.C.L = 0;
            state.D.H = 24; state.D.L = 79;
            Int10_ScrollUp(state);
            row = 24;
        }
        _bus.WriteByte(slot,     col);
        _bus.WriteByte(slot + 1, row);
    }

    /// <summary>AH=0F — get current video mode. AL = mode, AH = columns, BH = active page.</summary>
    private void Int10_GetVideoMode(AprX86.Cli.Cpu.X86State state)
    {
        state.A.L = _bus.ReadByte(0x00449);
        state.A.H = (byte)_bus.ReadWord16(0x0044A);
        state.B.H = _bus.ReadByte(0x00462);
    }

    // ---------- IRET simulation ----------

    private void SimulateIret(AprX86.Cli.Cpu.X86State state)
    {
        // Pop in order: IP, CS, FLAGS (reverse of INT push order).
        ushort ip    = ReadStackWord(state, 0);
        ushort cs    = ReadStackWord(state, 2);
        ushort flags = ReadStackWord(state, 4);
        state.SP = (ushort)(state.SP + 6);
        state.CS = cs;
        state.IP = ip;
        state.SetFlags(flags);
    }

    private ushort ReadStackWord(AprX86.Cli.Cpu.X86State state, ushort spAddend)
    {
        int addr = X86Memory.LinearAddr(state.SS, (ushort)(state.SP + spAddend));
        return _bus.ReadWord16(addr);
    }
}
