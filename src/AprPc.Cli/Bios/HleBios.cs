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

using AprPc.Cli.Hardware;
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
    private readonly PcKeyboard _kbd;
    private readonly PcPit _pit;
    private readonly bool _traceInt;

    // Bitset of which vectors this HLE implementation handles. Used
    // both to populate IVT entries and to decide whether a CS:IP land
    // at F000:00xx is ours.
    private readonly bool[] _owned = new bool[256];

    // Phase 28.5 — attached disks by drive number (0=A, 1=B, 0x80=C, 0x81=D).
    private readonly Dictionary<byte, DiskImage> _disks = new();
    // Last INT 13h status code (returned by AH=01).
    private byte _diskLastStatus;

    public HleBios(X86JsonCpu cpu, PcMemoryBus bus, PcKeyboard kbd, PcPit pit, bool traceInt = false)
    {
        _cpu      = cpu ?? throw new ArgumentNullException(nameof(cpu));
        _bus      = bus ?? throw new ArgumentNullException(nameof(bus));
        _kbd      = kbd ?? throw new ArgumentNullException(nameof(kbd));
        _pit      = pit ?? throw new ArgumentNullException(nameof(pit));
        _traceInt = traceInt;
    }

    /// <summary>Phase 28.5 — attach a disk image to a drive number.</summary>
    public void AttachDisk(byte drive, DiskImage img)
    {
        _disks[drive] = img;
        if (_traceInt)
            Console.Error.WriteLine($"  [HLE] attached drive {drive:X2}h ({img.Kind}) " +
                $"geom={img.Cylinders}x{img.Heads}x{img.Sectors} ({img.TotalSectors * 512 / 1024} KB)");
    }

    /// <summary>
    /// Install IVT entries for every supported vector. Call once after
    /// PcMemoryBus.Reset() has zeroed the IVT region.
    /// </summary>
    public void Install()
    {
        // Phase 28.8c — pre-install default IRET trap for ALL 256
        // IVT slots. Real-mode INT to any unset vector would land at
        // IVT[v]=0:0000 and send CPU walking through IVT bytes as
        // code (FreeDOS hit this with stray INT 12h / 1Bh / etc.
        // before reaching its own IVT writes). User code that
        // installs its own handler overrides our F000:00xx entry
        // since the user IVT write replaces our pointer. Specific
        // vectors (0x10/0x13/0x16/0x19/0x1A) get real HLE bodies
        // below; unhandled cases in Dispatch's switch fall to a
        // no-op + SimulateIret.
        for (int v = 0; v < 256; v++) InstallVector((byte)v, $"default_{v:X2}");
        // Re-announce explicit handlers (the install call is a no-op
        // since the slot was already written, but tracing improves).
        if (_traceInt)
        {
            Console.Error.WriteLine($"  [HLE] handlers: INT 10h video / 13h disk / 16h keyboard / 19h bootstrap / 1Ah time");
            Console.Error.WriteLine($"  [HLE] (other 251 vectors get a default no-op IRET trap)");
        }
    }

    private void InstallVector(byte vector, string label)
    {
        // IVT[v] is 4 bytes at physical 0x0000 + v*4: low IP, high IP, low CS, high CS.
        int slot = vector * 4;
        _bus.WriteWord16(slot,     vector);              // IP = vector (low byte populated, high byte = 0)
        _bus.WriteWord16(slot + 2, HleTrapSegment);      // CS = 0xF000
        _owned[vector] = true;
        // Trace only for explicitly-handled vectors (cuts log noise from
        // the Phase 28.8c "install all 256 defaults" loop).
        if (_traceInt && !label.StartsWith("default_"))
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

        bool skipIret = false;
        switch (vector)
        {
            case 0x10: Int10(state); break;
            case 0x13: Int13(state); break;
            case 0x16: Int16(state); break;
            case 0x19: Int19(state); skipIret = true; break;
            case 0x1A: Int1A(state); break;
            default:
                // Unhandled — just IRET, no side effect.
                break;
        }

        if (!skipIret) SimulateIret(state);
        _cpu.LoadState(state);
    }

    /// <summary>
    /// Returns true if INT 16h AH=00 is currently blocked waiting on
    /// an empty keyboard buffer. The emulator thread uses this to
    /// park the CPU instead of busy-spinning the dispatch loop.
    /// </summary>
    public bool IsBlockedOnKeyboard(ushort cs, ushort ip)
    {
        if (!IsTrapped(cs, ip) || (byte)ip != 0x16) return false;
        return _cpu.State.A.H == 0x00 && _kbd.IsEmpty;
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

    // ---------- INT 13h: disk ----------

    // Status codes per Intel BIOS doc (RBIL Table 00234).
    private const byte DiskOk            = 0x00;
    private const byte DiskBadCmd        = 0x01;
    private const byte DiskNoMedia       = 0x06;
    private const byte DiskSectorNotFound = 0x04;
    private const byte DiskWriteProtect  = 0x03;

    private void Int13(AprX86.Cli.Cpu.X86State state)
    {
        switch (state.A.H)
        {
            case 0x00: Int13_Reset(state); break;
            case 0x01: Int13_LastStatus(state); break;
            case 0x02: Int13_Read(state); break;
            case 0x03: Int13_Write(state); break;
            case 0x04: Int13_Verify(state); break;
            case 0x08: Int13_GetDriveParams(state); break;
            case 0x15: Int13_GetDiskType(state); break;
            default:
                if (_traceInt)
                    Console.Error.WriteLine($"  [HLE] INT 13h AH={state.A.H:X2} not implemented; failing");
                Int13Fail(state, DiskBadCmd);
                break;
        }
    }

    private void Int13Ok(AprX86.Cli.Cpu.X86State state, byte ret = DiskOk)
    {
        state.A.H = ret;
        state.FlagC = false;
        _diskLastStatus = ret;
    }

    private void Int13Fail(AprX86.Cli.Cpu.X86State state, byte status)
    {
        state.A.H = status;
        state.FlagC = true;
        _diskLastStatus = status;
    }

    /// <summary>AH=00 — reset disk system. DL = drive. We just clear last-status.</summary>
    private void Int13_Reset(AprX86.Cli.Cpu.X86State state)
    {
        Int13Ok(state);
    }

    /// <summary>AH=01 — return last status in AH. DL = drive (currently ignored — single global last-status).</summary>
    private void Int13_LastStatus(AprX86.Cli.Cpu.X86State state)
    {
        state.A.H = _diskLastStatus;
        state.FlagC = _diskLastStatus != DiskOk;
    }

    /// <summary>AH=02 — read sectors. AL = count, CH/CL = cyl/sec, DH/DL = head/drive, ES:BX = buffer.</summary>
    private void Int13_Read(AprX86.Cli.Cpu.X86State state)
    {
        if (!_disks.TryGetValue(state.D.L, out var disk))
        {
            Int13Fail(state, DiskNoMedia);
            state.A.L = 0;
            return;
        }
        int cyl = state.C.H | ((state.C.L & 0xC0) << 2);   // 10-bit cylinder
        int sec = state.C.L & 0x3F;                         // 6-bit sector (1-indexed)
        int head = state.D.H;
        int count = state.A.L;
        int lba = disk.ChsToLba(cyl, head, sec);
        if (lba < 0)
        {
            Int13Fail(state, DiskSectorNotFound);
            state.A.L = 0;
            return;
        }
        var buf = new byte[count * DiskImage.SectorSize];
        int got = disk.ReadSectors(lba, count, buf);
        int physBase = X86Memory.LinearAddr(state.ES, state.B.X);
        for (int i = 0; i < got * DiskImage.SectorSize; i++)
            _bus.WriteByte(physBase + i, buf[i]);

        state.A.L = (byte)got;
        if (got == count) Int13Ok(state);
        else              Int13Fail(state, DiskSectorNotFound);
    }

    /// <summary>AH=03 — write sectors. Same register layout as AH=02.</summary>
    private void Int13_Write(AprX86.Cli.Cpu.X86State state)
    {
        if (!_disks.TryGetValue(state.D.L, out var disk))
        {
            Int13Fail(state, DiskNoMedia);
            state.A.L = 0;
            return;
        }
        if (disk.ReadOnly)
        {
            Int13Fail(state, DiskWriteProtect);
            state.A.L = 0;
            return;
        }
        int cyl = state.C.H | ((state.C.L & 0xC0) << 2);
        int sec = state.C.L & 0x3F;
        int head = state.D.H;
        int count = state.A.L;
        int lba = disk.ChsToLba(cyl, head, sec);
        if (lba < 0)
        {
            Int13Fail(state, DiskSectorNotFound);
            state.A.L = 0;
            return;
        }
        var buf = new byte[count * DiskImage.SectorSize];
        int physBase = X86Memory.LinearAddr(state.ES, state.B.X);
        for (int i = 0; i < buf.Length; i++)
            buf[i] = _bus.ReadByte(physBase + i);

        int wrote = disk.WriteSectors(lba, count, buf);
        state.A.L = (byte)wrote;
        if (wrote == count) Int13Ok(state);
        else                Int13Fail(state, DiskSectorNotFound);
    }

    /// <summary>AH=04 — verify sectors. We just check the LBA range is valid.</summary>
    private void Int13_Verify(AprX86.Cli.Cpu.X86State state)
    {
        if (!_disks.TryGetValue(state.D.L, out var disk))
        {
            Int13Fail(state, DiskNoMedia);
            return;
        }
        int cyl = state.C.H | ((state.C.L & 0xC0) << 2);
        int sec = state.C.L & 0x3F;
        int head = state.D.H;
        int lba = disk.ChsToLba(cyl, head, sec);
        if (lba < 0) Int13Fail(state, DiskSectorNotFound);
        else         Int13Ok(state);
    }

    /// <summary>AH=08 — get drive parameters. CH/CL = max cyl/sec, DH = max head, DL = drive count.</summary>
    private void Int13_GetDriveParams(AprX86.Cli.Cpu.X86State state)
    {
        if (!_disks.TryGetValue(state.D.L, out var disk))
        {
            Int13Fail(state, DiskNoMedia);
            return;
        }
        int maxCyl = disk.Cylinders - 1;
        int maxHead = disk.Heads - 1;
        int sectorsPerTrack = disk.Sectors;
        state.C.H = (byte)(maxCyl & 0xFF);
        state.C.L = (byte)((sectorsPerTrack & 0x3F) | ((maxCyl >> 2) & 0xC0));
        state.D.H = (byte)maxHead;
        // DL = number of drives of the same type attached.
        int sameKind = 0;
        foreach (var d in _disks.Values)
            if (d.Kind == disk.Kind) sameKind++;
        state.D.L = (byte)sameKind;
        // BL on AT-class = drive type (4 = 1.44 MB).
        state.B.L = disk.Kind == DiskKind.Floppy ? (byte)4 : (byte)0;
        // ES:DI = pointer to drive parameter table — not provided.
        state.ES = 0; state.DI = 0;
        Int13Ok(state);
    }

    /// <summary>AH=15 — get disk type. AH on return: 0=no disk, 1=floppy no diskchg, 2=floppy w/diskchg, 3=fixed.</summary>
    private void Int13_GetDiskType(AprX86.Cli.Cpu.X86State state)
    {
        if (!_disks.TryGetValue(state.D.L, out var disk))
        {
            state.A.H = 0; state.FlagC = false; return;
        }
        if (disk.Kind == DiskKind.Floppy)
        {
            state.A.H = 0x02;
        }
        else
        {
            state.A.H = 0x03;
            // CX:DX = sector count (32-bit) for fixed disks.
            uint total = (uint)disk.TotalSectors;
            state.C.X = (ushort)((total >> 16) & 0xFFFF);
            state.D.X = (ushort)(total & 0xFFFF);
        }
        state.FlagC = false;
    }

    // ---------- INT 19h: bootstrap loader ----------

    /// <summary>
    /// INT 19h — the BIOS bootstrap. Try drive 0 (A:) first, then
    /// drive 0x80 (C:). For each: read sector 0 (CHS=0/0/1) into
    /// 0000:7C00; if the last two bytes are the 55 AA boot magic,
    /// set CS=0, IP=0x7C00, DL=drive, and **skip IRET** (caller in
    /// Dispatch sees the skipIret flag and leaves the stack alone
    /// so the CPU resumes execution at the boot sector).
    ///
    /// If no bootable disk is found, the routine falls back to
    /// setting CS:IP=F000:FFF2 (= the HLT instruction right after
    /// the INT 19h opcode at FFFF:0000); HLT halts the CPU.
    /// </summary>
    private void Int19(AprX86.Cli.Cpu.X86State state)
    {
        byte[] candidates = new byte[] { 0x00, 0x80 };
        foreach (byte drive in candidates)
        {
            if (!_disks.TryGetValue(drive, out var disk)) continue;
            var boot = new byte[DiskImage.SectorSize];
            int got = disk.ReadSectors(0, 1, boot);
            if (got != 1) continue;
            // Boot magic check at offset 510/511.
            if (boot[510] != 0x55 || boot[511] != 0xAA)
            {
                if (_traceInt)
                    Console.Error.WriteLine($"  [HLE] INT 19h: drive {drive:X2}h sector 0 lacks 55 AA magic; skipping");
                continue;
            }
            // Copy to 0000:7C00 (physical 0x07C00).
            for (int i = 0; i < boot.Length; i++)
                _bus.WriteByte(0x07C00 + i, boot[i]);

            state.CS = 0x0000;
            state.IP = 0x7C00;
            state.D.L = drive;
            // Conventional: clear DH; preserve other GPRs / segs at
            // their reset-zero state. The boot sector is responsible
            // for setting up its own DS/ES/SS/SP.
            state.D.H = 0;
            if (_traceInt)
                Console.Error.WriteLine($"  [HLE] INT 19h booted from drive {drive:X2}h (sector 0 -> 0000:7C00)");
            return;
        }
        // No bootable disk — fall through to HLT at FFFF:0002.
        state.CS = 0xFFFF;
        state.IP = 0x0002;
        if (_traceInt)
            Console.Error.WriteLine("  [HLE] INT 19h: no bootable disk; halting");
    }

    // ---------- INT 16h: keyboard ----------

    private void Int16(AprX86.Cli.Cpu.X86State state)
    {
        switch (state.A.H)
        {
            case 0x00: Int16_ReadChar(state); break;
            case 0x01: Int16_PeekChar(state); break;
            case 0x02: Int16_GetShiftFlags(state); break;
            default:
                if (_traceInt)
                    Console.Error.WriteLine($"  [HLE] INT 16h AH={state.A.H:X2} not implemented; no-op");
                break;
        }
    }

    /// <summary>
    /// AH=00 — block-wait for a keystroke; return AL=ascii, AH=scancode.
    /// Caller (PcSystemRunner) gates the dispatch so we never actually
    /// block here: it only invokes Dispatch(0x16) when the buffer is
    /// non-empty. We do one final defensive check to keep the contract
    /// simple — if the buffer is somehow empty, return (0, 0).
    /// </summary>
    private void Int16_ReadChar(AprX86.Cli.Cpu.X86State state)
    {
        if (_kbd.TryDequeue(out byte ascii, out byte scancode))
        {
            state.A.L = ascii;
            state.A.H = scancode;
        }
        else
        {
            state.A.L = 0;
            state.A.H = 0;
        }
    }

    /// <summary>
    /// AH=01 — peek. If a keystroke is available: ZF=0, AL=ascii,
    /// AH=scancode. Otherwise: ZF=1.
    /// </summary>
    private void Int16_PeekChar(AprX86.Cli.Cpu.X86State state)
    {
        if (_kbd.TryPeek(out byte ascii, out byte scancode))
        {
            state.A.L = ascii;
            state.A.H = scancode;
            state.FlagZ = false;
        }
        else
        {
            state.FlagZ = true;
        }
    }

    /// <summary>AH=02 — read BDA shift-flags byte into AL.</summary>
    private void Int16_GetShiftFlags(AprX86.Cli.Cpu.X86State state)
    {
        state.A.L = _kbd.ShiftFlags;
    }

    // ---------- INT 1Ah: time ----------

    private void Int1A(AprX86.Cli.Cpu.X86State state)
    {
        switch (state.A.H)
        {
            case 0x00: Int1A_GetTicks(state); break;
            // AH=01 set ticks, AH=02-07 RTC services etc. — defer.
            default:
                if (_traceInt)
                    Console.Error.WriteLine($"  [HLE] INT 1Ah AH={state.A.H:X2} not implemented; no-op");
                break;
        }
    }

    /// <summary>
    /// AH=00 — get tick count since midnight.
    ///   CX = high word of tick count
    ///   DX = low  word of tick count
    ///   AL = midnight-rollover flag (also cleared after read, per Intel)
    /// </summary>
    private void Int1A_GetTicks(AprX86.Cli.Cpu.X86State state)
    {
        uint ticks  = _pit.Ticks;
        state.C.X   = (ushort)((ticks >> 16) & 0xFFFF);
        state.D.X   = (ushort)(ticks & 0xFFFF);
        state.A.L   = _pit.MidnightRolled;
        if (_pit.MidnightRolled != 0) _pit.MidnightRolled = 0;
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
