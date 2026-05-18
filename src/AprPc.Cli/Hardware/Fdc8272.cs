// Fdc8272 — NEC μPD765A / Intel 8272A floppy disk controller, just
// enough for real PC/XT BIOS INT 19h to load a boot sector.
//
// Phase 30 — implemented after Gemini consultation 2026-05-16. The 6
// commands we handle cover the entire normal-boot path:
//   0x03 SPECIFY            (no result, no IRQ — pure config)
//   0x07 RECALIBRATE        (no result, IRQ 6 — seek to cyl 0)
//   0x08 SENSE INTERRUPT    (2-byte result ST0+PCN — clears IRQ 6)
//   0x0F SEEK               (no result, IRQ 6 — move to specified cyl)
//   0x0A READ ID            (7-byte result — fake from current seek)
//   0x06 READ DATA (+ MFM/MT/SK variant bits — 7-byte result + IRQ 6)
// + 0x04 SENSE DRIVE STATUS (1-byte result, no IRQ — drive status)
// Any other command returns the canonical 0x80 "Invalid Command"
// status in a 1-byte result phase and does NOT assert IRQ.
//
// State machine: Idle → Command → Execution → Result → Idle.
// MSR (0x3F4) bit layout per Intel manual:
//   bit 7 RQM   — controller ready for byte transfer
//   bit 6 DIO   — 0 = CPU writes to FDC, 1 = CPU reads from FDC
//   bit 5 NDM   — 0 = DMA mode (always 0 for PC/XT path)
//   bit 4 CB    — controller busy (command in progress)
//   bit 3-0     — per-drive seek-busy (not implemented; always 0)
//
// DMA: hooked to Dma8237. On READ DATA execute, we synchronously copy
// the requested sectors from DiskImage to RAM via DMA channel 2's
// programmed base/count/page, then update DMA state and assert IRQ 6.

using AprX86.Cli.Memory;

namespace AprPc.Cli.Hardware;

public sealed class Fdc8272
{
    private enum Phase { Idle, Command, Execution, Result }

    private readonly Dma8237   _dma;
    private readonly Pic8259   _pic;
    private readonly X86Memory _mem;
    private readonly bool      _trace;

    // Mounted floppy drives 0..3 (typically just A = 0).
    private readonly DiskImage?[] _drives = new DiskImage?[4];

    private Phase _phase = Phase.Idle;

    // Command FIFO — accumulates command bytes until we know the
    // command length, then transitions to execution.
    private readonly byte[] _cmdBuf = new byte[16];
    private int _cmdLen;             // bytes received so far
    private int _cmdExpected;        // total bytes expected for this command

    // Result FIFO — bytes produced by command, read by CPU one at a time.
    private readonly byte[] _resBuf = new byte[16];
    private int _resLen;             // total bytes in result
    private int _resPos;             // next byte CPU will read

    // Per-drive current cylinder (PCN — Present Cylinder Number).
    // Updated by SEEK / RECALIBRATE; used as the "current position"
    // for subsequent READ DATA + READ ID.
    private readonly int[] _pcn = new int[4];

    // ST0 cached after the last completion event (RECAL/SEEK/READ).
    // Returned to SENSE INTERRUPT STATUS.
    private byte _pendingSt0;
    private bool _interruptPending;

    // DOR shadow.
    private byte _dor;

    public Fdc8272(Dma8237 dma, Pic8259 pic, X86Memory mem, bool trace = false)
    {
        _dma = dma; _pic = pic; _mem = mem; _trace = trace;
    }

    public void AttachDrive(byte drive, DiskImage img)
    {
        if (drive >= 4) throw new ArgumentOutOfRangeException(nameof(drive));
        _drives[drive] = img;
    }

    /// <summary>Drive attached to slot N, or null. Used for swap + DSKCHG plumbing.</summary>
    public DiskImage? GetDrive(byte drive) => drive < 4 ? _drives[drive] : null;

    /// <summary>
    /// True if any drive currently asserts DSKCHG. Real 8272A wires
    /// DSKCHG per-drive on port 0x3F7 bit 7; the BIOS reads it via the
    /// currently-selected drive (DOR bits 0-1). Match that semantic.
    /// </summary>
    public bool ActiveDriveDiskChanged
    {
        get
        {
            byte sel = (byte)(_dor & 3);
            return _drives[sel]?.DiskChanged ?? false;
        }
    }

    public void Reset()
    {
        _phase = Phase.Idle; _cmdLen = 0; _cmdExpected = 0;
        _resLen = 0; _resPos = 0; _dor = 0;
        Array.Clear(_pcn, 0, _pcn.Length);
        _pendingSt0 = 0; _interruptPending = false;
    }

    // ---- Port handlers ----

    public byte ReadMsr()   // port 0x3F4
    {
        // Always RQM=1 in our impl (no inter-byte busy windows).
        // CB+DIO depend on phase.
        byte msr = 0x80;
        if (_phase != Phase.Idle) msr |= 0x10;       // CB
        if (_phase == Phase.Result) msr |= 0x40;     // DIO (host reads)
        return msr;
    }

    public byte ReadFifo()  // port 0x3F5
    {
        if (_phase != Phase.Result || _resPos >= _resLen) return 0xFF;
        byte b = _resBuf[_resPos++];
        // Phase 30-supp (per Gemini Q5) — when BIOS starts reading the
        // result phase, the FDC drops its IRQ 6 line. Pic8259's
        // edge-triggered pending bit was already cleared by
        // DequeueNextVector when the original ISR fired, so there's
        // nothing to deassert here — just mark our local pending
        // flag clean so future SENSE INTERRUPT doesn't think there's
        // still an interrupt to report. (The previous code's spurious
        // AssertIrq(6) re-fired the line, causing a second ISR call
        // during result-phase processing.)
        if (_resPos == 1 && _interruptPending)
        {
            _interruptPending = false;
        }
        if (_resPos >= _resLen)
        {
            _phase = Phase.Idle;
            _resLen = 0; _resPos = 0;
        }
        return b;
    }

    public void WriteFifo(byte v)  // port 0x3F5 (write)
    {
        // Start of new command — figure out length from opcode.
        if (_phase == Phase.Idle)
        {
            _cmdLen = 0;
            _cmdExpected = ExpectedCommandLength(v);
            _phase = Phase.Command;
        }

        if (_cmdLen < _cmdBuf.Length) _cmdBuf[_cmdLen++] = v;
        if (_cmdLen >= _cmdExpected)
        {
            AprPc.Cli.Diagnostics.KbdTrace.Log(
                $"FDC.Exec opcode=0x{_cmdBuf[0]:X2} op&0x1F=0x{_cmdBuf[0] & 0x1F:X2} " +
                $"bytes=[{string.Join(" ", _cmdBuf.Take(_cmdLen).Select(b => b.ToString("X2")))}]");
            ExecuteCommand();
        }
    }

    public void WriteDor(byte v)   // port 0x3F2
    {
        bool wasReset = (_dor & 0x04) == 0;
        bool nowReset = (v   & 0x04) == 0;
        // Phase 30.x speed hack — force all motor-on bits (4-7) to 1
        // regardless of what BIOS asked. Real IBM XT BIOS INT 13h does
        // a 500ms wall-clock stall on each disk read where the motor
        // bit is OFF, waiting for physical 300 RPM spin-up. We have no
        // physical motor; reads complete instantly. Keeping all motor
        // bits forced-on tricks the BIOS into thinking the motor is
        // already spinning, skipping the stall. Net effect on the user:
        // dir/ver/etc respond in milliseconds instead of multiple
        // minutes (840 reads * 500ms = 7 minutes saved per `dir`).
        // Confirmed via Gemini 2026-05-16 consultation.
        v = (byte)(v | 0xF0);
        _dor = v;
        if (wasReset && !nowReset)
        {
            // 0→1 transition on nRESET: assert IRQ 6 per Intel spec.
            _pendingSt0 = 0xC0;  // abnormal termination + poll mode = "FDC reset"
            _interruptPending = true;
            _pic.AssertIrq(6);
            _phase = Phase.Idle;
        }
    }
    public byte ReadDor() => _dor;

    // ---- Internal: command length table ----

    private static int ExpectedCommandLength(byte opcode)
    {
        // Mask out MT (0x80), MFM (0x40), SK (0x20) bits — only the low
        // 5 bits identify the command.
        byte op = (byte)(opcode & 0x1F);
        return op switch
        {
            0x03 => 3,   // SPECIFY: cmd + SRT/HUT + HLT/ND
            0x04 => 2,   // SENSE DRIVE STATUS: cmd + HDS/DS
            0x07 => 2,   // RECALIBRATE: cmd + DS
            0x08 => 1,   // SENSE INTERRUPT STATUS: cmd
            0x0A => 2,   // READ ID: cmd + HDS/DS
            0x0F => 3,   // SEEK: cmd + HDS/DS + NCN
            0x06 => 9,   // READ DATA (and variants 0x46/0x66/0xE6)
            _    => 1,   // unknown: invalid command result
        };
    }

    // ---- Command execution dispatch ----

    private void ExecuteCommand()
    {
        byte opcode = _cmdBuf[0];
        byte op = (byte)(opcode & 0x1F);
        _resPos = 0; _resLen = 0;

        switch (op)
        {
            case 0x03: ExecSpecify();          break;
            case 0x04: ExecSenseDriveStatus(); break;
            case 0x07: ExecRecalibrate();      break;
            case 0x08: ExecSenseInterrupt();   break;
            case 0x0A: ExecReadId(opcode);     break;
            case 0x0F: ExecSeek();             break;
            case 0x06: ExecReadData(opcode);   break;
            default:   ExecInvalid();          break;
        }
    }

    private void ExecSpecify()
    {
        // No result phase, no IRQ. Just go back to idle.
        _phase = Phase.Idle;
        _cmdLen = 0;
    }

    private void ExecSenseDriveStatus()
    {
        byte hds_ds = _cmdBuf[1];
        byte drive = (byte)(hds_ds & 3);
        byte hds   = (byte)((hds_ds >> 2) & 1);

        // ST3 layout:
        //   b0-1 DS  (drive select bits we received)
        //   b2   HDS (head select)
        //   b3   TWO_SIDE = 1 (we always claim two-sided media)
        //   b4   TRK0 = 1 if drive is at cyl 0
        //   b5   READY = 1 if drive has media (we report 1 when DiskImage attached)
        //   b6   WP = 0 (not write-protected)
        //   b7   FAULT = 0
        byte st3 = (byte)((hds << 2) | (drive & 3) | 0x08);  // TWO_SIDE always
        if (_drives[drive] is not null) st3 |= 0x20;          // READY
        if (_pcn[drive] == 0)            st3 |= 0x10;          // TRK0
        _resBuf[0] = st3;
        _resLen = 1;
        _phase = Phase.Result;
    }

    private void ExecRecalibrate()
    {
        byte drive = (byte)(_cmdBuf[1] & 3);
        _pcn[drive] = 0;
        // Phase 32.1 — clear DSKCHG on SEEK/RECAL per real 8272A.
        if (_drives[drive] is { } d) d.DiskChanged = false;
        // No result phase. ST0 reflects the seek-end + drive.
        _pendingSt0 = (byte)(0x20 | drive);  // SE (Seek End) bit
        _interruptPending = true;
        _pic.AssertIrq(6);
        _phase = Phase.Idle;
        _cmdLen = 0;
    }

    private void ExecSenseInterrupt()
    {
        if (_interruptPending)
        {
            _resBuf[0] = _pendingSt0;
            _resBuf[1] = (byte)_pcn[_pendingSt0 & 3];
            _resLen = 2;
            _interruptPending = false;
            _phase = Phase.Result;
        }
        else
        {
            // No pending interrupt — invalid SENSE INT. Return ST0=0x80.
            _resBuf[0] = 0x80;
            _resLen = 1;
            _phase = Phase.Result;
        }
    }

    private void ExecSeek()
    {
        byte drive = (byte)(_cmdBuf[1] & 3);
        byte ncn   = _cmdBuf[2];
        _pcn[drive] = ncn;
        // Phase 32.1 — clear DSKCHG on SEEK/RECAL per real 8272A.
        if (_drives[drive] is { } d) d.DiskChanged = false;
        _pendingSt0 = (byte)(0x20 | drive);
        _interruptPending = true;
        _pic.AssertIrq(6);
        _phase = Phase.Idle;
        _cmdLen = 0;
    }

    private void ExecReadId(byte opcode)
    {
        byte hds_ds = _cmdBuf[1];
        byte drive  = (byte)(hds_ds & 3);
        byte hds    = (byte)((hds_ds >> 2) & 1);
        // Fake: return the CHS of "current seeked position".
        _resBuf[0] = (byte)((hds << 2) | drive);  // ST0
        _resBuf[1] = 0;                            // ST1
        _resBuf[2] = 0;                            // ST2
        _resBuf[3] = (byte)_pcn[drive];            // Cyl
        _resBuf[4] = hds;                          // Head
        _resBuf[5] = 1;                            // Sector (1-based)
        _resBuf[6] = 2;                            // N = 2 (512 bytes)
        _resLen = 7;
        _pendingSt0 = _resBuf[0];
        _interruptPending = true;
        _pic.AssertIrq(6);
        _phase = Phase.Result;
    }

    private void ExecReadData(byte opcode)
    {
        // Command bytes: cmd, hds_ds, C, H, R, N, EOT, GPL, DTL.
        byte hds_ds = _cmdBuf[1];
        byte drive  = (byte)(hds_ds & 3);
        byte hds    = (byte)((hds_ds >> 2) & 1);
        byte c   = _cmdBuf[2];
        byte h   = _cmdBuf[3];
        byte r   = _cmdBuf[4];     // starting sector (1-based)
        byte n   = _cmdBuf[5];     // sector size = 128 << n  (n=2 → 512)
        byte eot = _cmdBuf[6];     // last sector on track (multi-sector)

        int sectorSize = 128 << Math.Min(n, (byte)4);
        var disk = _drives[drive];
        if (disk is null)
        {
            // Drive not present — abnormal termination.
            _resBuf[0] = (byte)(0x40 | (hds << 2) | drive);  // IC = 01 (abnormal)
            _resBuf[1] = 0x01;  // missing address mark
            _resBuf[2] = 0;
            _resBuf[3] = c; _resBuf[4] = h; _resBuf[5] = r; _resBuf[6] = n;
            _resLen = 7;
            _interruptPending = true;
            _pic.AssertIrq(6);
            _phase = Phase.Result;
            return;
        }

        // DMA-burst transfer. Compute physical destination + length.
        int physAddr = _dma.Channel2PhysicalAddress;
        int dmaBytes = _dma.Channel2TransferBytes;

        // Compute starting LBA from CHS.
        int sectorsPerTrack = disk.Sectors;
        int headsPerCyl     = disk.Heads;
        int lba             = ((c * headsPerCyl) + h) * sectorsPerTrack + (r - 1);

        AprPc.Cli.Diagnostics.KbdTrace.Log(
            $"FDC.ExecReadData drv={drive} CHS={c}/{h}/{r} EOT={eot} N={n} secSize={sectorSize} " +
            $"-> LBA={lba} dmaPhys=0x{physAddr:X5} dmaBytes={dmaBytes}");

        // Read up to dmaBytes from disk image; clamp at end-of-image.
        int readBytes = Math.Min(dmaBytes, (disk.TotalSectors - lba) * 512);
        if (readBytes > 0)
        {
            int firstSec   = lba;
            int totalSecs  = (readBytes + 511) / 512;
            var buf = new byte[totalSecs * 512];
            disk.ReadSectors(firstSec, totalSecs, buf);
            for (int i = 0; i < readBytes && physAddr + i < _mem.Ram.Length; i++)
            {
                _mem.Ram[physAddr + i] = buf[i];
                // Phase 30.15 — SMC notify the block-JIT cache.
                // FDC DMA writes the FreeDOS boot sector and kernel
                // into RAM; without this notification, block-JIT keeps
                // executing the all-zeros translation it cached for those
                // addresses before the load happened, and CPU jumps land
                // in a stale zero-sled. Per-instr backend is unaffected
                // (no cache). Cheap when no block covers the addr —
                // BlockCache.NotifyMemoryWrite uses a per-byte coverage
                // counter for the fast path.
                AprX86.Cli.Cpu.X86JsonCpu.NotifyExternalMemoryWrite(
                    (uint)(physAddr + i));
            }
            _dma.OnChannel2BurstComplete(readBytes);
            if (_trace)
            {
                Console.Error.WriteLine($"  [FDC] READ drive={drive} C={c} H={h} R={r} N={n} EOT={eot} → LBA={lba} bytes={readBytes} → phys=0x{physAddr:X5}");
                Console.Error.WriteLine($"        first 16 bytes: {string.Join(" ", buf.Take(16).Select(b => b.ToString("X2")))}");
            }
        }

        // Compute final CHS for result phase (CHS of last + 1 sector
        // boundary, per Intel manual).
        int lastLba = lba + (readBytes / sectorSize) - 1;
        int finalC = lastLba / (headsPerCyl * sectorsPerTrack);
        int finalH = (lastLba / sectorsPerTrack) % headsPerCyl;
        int finalS = (lastLba % sectorsPerTrack) + 2;  // +1 (1-based) +1 (next sector)
        if (finalS > sectorsPerTrack) { finalS = 1; finalH ^= 1; if (finalH == 0) finalC++; }

        _resBuf[0] = (byte)((hds << 2) | drive);   // ST0: normal termination
        _resBuf[1] = 0;                             // ST1
        _resBuf[2] = 0;                             // ST2
        _resBuf[3] = (byte)finalC;
        _resBuf[4] = (byte)finalH;
        _resBuf[5] = (byte)finalS;
        _resBuf[6] = n;
        _resLen = 7;
        _pendingSt0 = _resBuf[0];
        _interruptPending = true;
        _pic.AssertIrq(6);
        _phase = Phase.Result;
    }

    private void ExecInvalid()
    {
        AprPc.Cli.Diagnostics.KbdTrace.Log(
            $"FDC.INVALID_COMMAND opcode=0x{_cmdBuf[0]:X2} bytes=[{string.Join(" ", _cmdBuf.Take(_cmdLen).Select(b => b.ToString("X2")))}]");
        _resBuf[0] = 0x80;  // ST0 = invalid command
        _resLen = 1;
        _phase = Phase.Result;
        // No IRQ for invalid commands.
    }
}
