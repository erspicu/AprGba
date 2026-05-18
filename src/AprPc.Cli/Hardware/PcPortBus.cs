// PcPortBus — host-side dispatch for 8086 IN/OUT instructions.
//
// Phase 28.IO replaces the old "IN returns 0xFF / OUT no-op" stubs
// with a real port routing table. The CPU's IN/OUT IR (X86InImm8Emitter
// et al.) calls into MemoryEmitters.CallPortRead8 / CallPortWrite8
// which are bound by PcSystemRunner to the static extern shims below.
// Those shims forward into the singleton _active PcPortBus instance,
// which dispatches by port number to one of the registered devices
// (Pic8259, Pit8253, Kbd8042, CMOS, speaker gate, etc.).
//
// Unknown ports return 0xFF on read (open-bus default for PC XT) and
// ignore writes. We log unhandled writes when --trace-io is set so
// new BIOS / DOS code paths can flag the device they're expecting.
//
// Threading: the emulator thread is the only caller of Read/Write.
// Device handlers may serialize their own state internally (Pic8259
// and friends already have a lock).

using System.IO;
using System.Text;

namespace AprPc.Cli.Hardware;

public sealed class PcPortBus
{
    /// <summary>Singleton active bus for the JIT extern shims.</summary>
    public static PcPortBus? Active;

    private readonly bool _traceIo;
    private readonly Pic8259 _pic;
    private readonly PcPit _pit;
    private readonly Fdc8272? _fdc;     // Phase 30 — optional, only present in --bios=PATH path
    private readonly Dma8237? _dma;     // Phase 30 — paired with _fdc

    // 8042 keyboard state — for now just port 60h / 64h shape;
    // PcKeyboard already maintains the BDA buffer.
    private byte _kbd60Data;
    private byte _kbd64Status = 0x14;   // bit 4 = no error, bit 2 = self-test passed

    // CMOS / RTC at 70h/71h. PCXT BIOS reads ~14 bytes. Real RTC has
    // 64 bytes; we just stub a small static table.
    private byte _cmosIndex;
    private readonly byte[] _cmos = new byte[64];

    // PC speaker gate / system control at port 61h.
    private byte _port61;

    // NMI mask at port 0xA0 (XT). Single byte.
    private byte _nmiMask;

    // Diagnostic / POST output at port 0x80 (used by some BIOSes as
    // a checkpoint indicator). We just store it.
    private byte _port80;

    // Phase 28.IO-kbd / Phase 30.x — proper edge-triggered IRQ 1 model
    // per Gemini consultation 2026-05-16. Real XT keyboard interface:
    //   1. Scancode arrives -> push into FIFO
    //   2. If IRQ 1 line is currently LOW, pop to port 0x60 buffer,
    //      pull line HIGH (= new low->high edge -> 8259A registers IRQ).
    //   3. BIOS INT 9 reads port 0x60, then ACKs via port 0x61 bit 7
    //      pulse (high then low).
    //   4. On bit 7 HIGH->LOW transition (= ack pulse end), check FIFO;
    //      if non-empty, pop next + re-pull line HIGH (= fresh edge,
    //      another INT 9 will fire). Empty FIFO -> line stays LOW.
    // This eliminates the "first scancode latches but rest stranded"
    // bug a simple "AssertIrq per InjectScancode" model has when the
    // PIC's pending bit is already set from a prior assert.
    private readonly Queue<byte> _kbd60Fifo = new(capacity: 8);
    private readonly object _kbd60Lock = new();
    private bool _kbd60IrqLine;  // true = line currently HIGH (= 8259A saw rising edge)
    private byte _port61Prev;    // previous port 0x61 value, for edge detection

    // 30.7c — video adapter the host wants reported via the PPI port
    // 0x62 DIP-switch bits ("mda" -> bits 4-5 = 11, "cga" -> bits 4-5
    // = 10 for 80x25 color). pcxtbios.bin uses this to choose between
    // MDA and CGA INT 10h init paths.
    private readonly string _video;

    // Number of floppy drives reported via Port 0x62 readback bits 2-3
    // ((count - 1) encoding). pcxtbios POST reads this and writes the
    // result into BDA[0x40:0x10] (equipment word) bits 6-7. FreeDOS then
    // consults the equipment word to decide whether B: is a real second
    // drive or a "phantom" sharing the single physical drive (= prompts
    // for diskette swap on each B: access).
    private readonly int _floppyCount;

    public PcPortBus(Pic8259 pic, PcPit pit, bool traceIo = false,
        Fdc8272? fdc = null, Dma8237? dma = null, string video = "mda",
        int floppyCount = 1)
    {
        _pic     = pic ?? throw new ArgumentNullException(nameof(pic));
        _pit     = pit ?? throw new ArgumentNullException(nameof(pit));
        _fdc     = fdc;
        _dma     = dma;
        _traceIo = traceIo;
        _video   = video;
        _floppyCount = Math.Clamp(floppyCount, 1, 4);
        // CMOS default: a few legitimate-looking bytes so BIOS POST
        // doesn't fail equipment / mem-size checks.
        _cmos[0x10] = 0x40;   // floppy A: = 1.44 MB
        _cmos[0x14] = 0x21;   // equipment: floppy + 80x25 color
        _cmos[0x15] = 0x80;   // base mem low (640 KB = 0x280 KB)
        _cmos[0x16] = 0x02;   // base mem high
        _cmos[0x17] = 0x00;   // ext mem low
        _cmos[0x18] = 0x00;   // ext mem high
    }

    /// <summary>
    /// Host-side keystroke injection for real-BIOS mode. Pushes
    /// <paramref name="scancode"/> into the 8042 FIFO and asserts IRQ 1
    /// so the real BIOS INT 9 handler runs, reads port 0x60, and writes
    /// the (ASCII, scancode) pair into the BDA keyboard ring buffer
    /// itself. Counterpart to <c>PcKeyboard.Enqueue</c> which bypasses
    /// the 8042 + INT 9 path (only correct for HLE BIOS mode).
    /// </summary>
    public void InjectScancode(byte scancode)
    {
        int fifoBefore;
        bool fireIrq;
        lock (_kbd60Lock)
        {
            fifoBefore = _kbd60Fifo.Count;
            if (_kbd60Fifo.Count < 8)
                _kbd60Fifo.Enqueue(scancode);

            // Gemini Option C: only pull IRQ line HIGH (= fresh edge)
            // when the previous scancode was acknowledged via port 0x61.
            // If line is already HIGH, this scancode just waits in FIFO
            // until the BIOS ack pulse drains it (see WritePort61).
            fireIrq = !_kbd60IrqLine && _kbd60Fifo.Count > 0;
            if (fireIrq)
            {
                _kbd60Data = _kbd60Fifo.Dequeue();
                _kbd60IrqLine = true;
                _kbd64Status = (byte)(_kbd64Status | 0x01);  // OBF
            }
        }
        AprPc.Cli.Diagnostics.KbdTrace.Log(
            $"PcPortBus.InjectScancode scan=0x{scancode:X2} fifo_before={fifoBefore} " +
            $"irq_line={_kbd60IrqLine} {(fireIrq ? "-> AssertIrq(1)" : "-> queued, no edge")}");
        if (fireIrq) _pic.AssertIrq(1);
    }

    private byte Read62()
    {
        // Phase 30.7c — pcxtbios.bin (Sergey Kiselev VirtualXT educational
        // BIOS) reads port 0x62 twice (per pcxtbios.asm source), with an
        // OUT 0xAD to Port 0x61 in between:
        //
        //   in al, 62h            ; first read = memory in low 4 bits
        //   and al, 0Fh
        //   mov ah, al
        //   mov al, 10101101b     ; 0xAD
        //   out dx, al            ; dx = 0x61 (Port B)
        //   in al, 62h            ; second read = video+floppy in low 4 bits
        //
        // We use Port B BIT 2 as the bank selector. 0xAD has bit 2 = 1
        // so this matches BIOS's intent. (NOTE: Gemini consults
        // contradicted each other on bit 2 vs bit 3. Empirically bit 2
        // works for CGA via --video=cga -- user-verified at commit
        // 9dbb8fc. Don't change without re-testing CGA.)
        //
        //   bit 2 = 0 -> memory in low 4 bits (0x03 = 256KB-class)
        //   bit 2 = 1 -> video+floppy in low 4 bits:
        //                bits 0-1 = video (00=EGA, 01=CGA40, 10=CGA80, 11=MDA)
        //                bits 2-3 = floppy count - 1 (00 = 1 drive)
        // pcxtbios TURBO_ENABLED writes 0xA5 to port 0x61 very early in
        // POST (line 491 of pcxtbios.asm) which has bit 2 = 1 baked in
        // as the turbo-mode flag. Then its later OR 0x30 / AND 0xCF
        // mask sequence at lines 668-671 doesn't touch bit 2. So bit 2
        // is NOT the SW2 bank selector on pcxtbios — bit 3 is, per the
        // standard PC/XT 8255 PIA Port B convention. The toggle
        // diff between pcxtbios's pre-read state (0x85) and its
        // explicit OUT 0xAD before the second read is bits 3 and 5;
        // bit 3 matches the 8255 SW2 select line.
        bool selectVideoFloppy = (_port61 & 0x08) != 0;
        if (!selectVideoFloppy) return 0x03;
        byte videoBits  = _video == "mda" ? (byte)0x03 : (byte)0x02;
        byte floppyBits = (byte)(((_floppyCount - 1) & 0x03) << 2);
        return (byte)(videoBits | floppyBits);
    }

    private byte Dequeue60()
    {
        byte result;
        int fifoCountAfter;
        lock (_kbd60Lock)
        {
            // BIOS read of 0x60 doesn't advance our FIFO; that happens
            // on the port 0x61 ack pulse. Just return the latched byte.
            result = _kbd60Data;
            fifoCountAfter = _kbd60Fifo.Count;
        }
        AprPc.Cli.Diagnostics.KbdTrace.Log(
            $"PcPortBus.Read(0x60) -> 0x{result:X2} (fifo_remaining={fifoCountAfter}, irq_line={_kbd60IrqLine})");
        return result;
    }

    /// <summary>
    /// Hook for port 0x61 writes -- the XT keyboard ack pulse. The
    /// IBM XT BIOS INT 9 ISR ack sequence is:
    ///   IN  AL, 61h
    ///   OR  AL, 80h    ; set bit 7
    ///   OUT 61h, AL    ; pulse high
    ///   AND AL, 7Fh    ; clear bit 7
    ///   OUT 61h, AL    ; pulse low
    /// Our IRQ 1 line stays HIGH after the BIOS-readable scancode is
    /// posted; the bit 7 LOW->HIGH transition de-asserts the line; the
    /// HIGH->LOW transition checks for next scancode and posts it if so.
    /// Without this, a FIFO with multiple scancodes (auto-repeat / fast
    /// typing / KeyDown+KeyPress double-fire) strands every entry past
    /// the first because no new rising edge ever hits the 8259A.
    /// </summary>
    private void OnPort61Edge(byte oldVal, byte newVal)
    {
        bool oldBit7 = (oldVal & 0x80) != 0;
        bool newBit7 = (newVal & 0x80) != 0;
        if (!oldBit7 && newBit7)
        {
            // ACK pulse rising edge -- BIOS acknowledged the scancode.
            // De-assert IRQ 1 line.
            lock (_kbd60Lock) { _kbd60IrqLine = false; }
        }
        else if (oldBit7 && !newBit7)
        {
            // ACK pulse falling edge -- check FIFO and post next.
            bool fireIrq;
            lock (_kbd60Lock)
            {
                fireIrq = _kbd60Fifo.Count > 0;
                if (fireIrq)
                {
                    _kbd60Data = _kbd60Fifo.Dequeue();
                    _kbd60IrqLine = true;
                    _kbd64Status = (byte)(_kbd64Status | 0x01);
                }
                else
                {
                    _kbd64Status = (byte)(_kbd64Status & ~0x01);  // clear OBF
                }
            }
            if (fireIrq)
            {
                AprPc.Cli.Diagnostics.KbdTrace.Log(
                    $"PcPortBus.OnPort61Edge ack_done, FIFO has more -> AssertIrq(1) scan=0x{_kbd60Data:X2}");
                _pic.AssertIrq(1);
            }
        }
    }

    // Per-port read counter — exposes pathological "this port read 50000
    // times in 1 second" loops (UIP polling, retrace polling, etc.).
    // Snapshotted to KbdTrace from PcSystemRunner's IRQ-rate watcher.
    public readonly long[] PortReadCounts = new long[0x400];

    public byte Read8(ushort port)
    {
        if (port < 0x400) PortReadCounts[port]++;
        byte v = port switch
        {
            // PIC master at 0x20 / 0x21
            0x20 => 0,                  // ISR/IRR read — return 0 (nothing in service)
            0x21 => _pic.GetImr(),

            // PIT 8253 at 0x40-0x43 — return zero for the latched count
            // reads for now (real BIOS does these during cal; we don't
            // emulate channel count latching yet, but returning 0 is
            // safer than 0xFF — BIOS won't see "no PIT").
            0x40 or 0x41 or 0x42 => 0,
            0x43 => 0,                  // control word is write-only

            // 8042 keyboard. Port 0x60 is the data port (scancode from
            // the keyboard). When the BIOS INT 9 ISR reads it, we pop
            // the next scancode off the host-side FIFO. Returns 0 when
            // FIFO is empty (real silicon undefined — works for FreeDOS).
            0x60 => Dequeue60(),
            0x64 => _kbd64Status,

            // Speaker gate / system control
            0x61 => _port61,

            // POST diagnostic
            0x80 => _port80,

            // NMI mask (PC XT)
            0xA0 => _nmiMask,

            // CMOS data
            0x71 => _cmos[_cmosIndex & 0x3F],

            // Phase 30 — 8272 FDC at 0x3F0-0x3F7.
            //   0x3F2 DOR (read-back of last write)
            //   0x3F4 MSR (read only)
            //   0x3F5 FIFO data (read = result phase byte)
            0x3F2 when _fdc is not null => _fdc.ReadDor(),
            0x3F4 when _fdc is not null => _fdc.ReadMsr(),
            0x3F5 when _fdc is not null => _fdc.ReadFifo(),

            // Phase 30 — 8237 DMA at 0x00-0x0F + 0x81-0x8F.
            0x04 when _dma is not null => _dma.Read8(0x04),
            0x05 when _dma is not null => _dma.Read8(0x05),
            0x08 when _dma is not null => _dma.Read8(0x08),
            0x81 when _dma is not null => _dma.Ch2Page,

            // MDA / CGA video status registers (0x3BA / 0x3DA).
            // Real silicon: bit 0 = horizontal retrace (cycles every
            // ~70 μs at 15.7 kHz), bit 3 = vertical retrace (every
            // ~16.7 ms at 60 Hz). BIOS POST + DOS games poll these to
            // sync to retrace boundaries before writing the
            // framebuffer (avoids tearing). Without time-varying bits
            // the polling loop spins forever.
            //
            // Phase 29-supp — implement via a per-read counter. Each
            // call increments _videoStatusCount and returns alternating
            // bit 0 + bit 3 patterns so the BIOS sees both edges within
            // a few reads. Not cycle-accurate but breaks the spin loop
            // and lets the BIOS POST advance past video init.
            0x3BA or 0x3DA => ReadVideoStatus(),

            // Phase 30.7a — UART (8250) and LPT (parallel printer) status
            // stubs per Gemini consultation. FreeDOS COMMAND.COM print
            // path appears to mirror STDOUT to COM1 or LPT1 and waits
            // for "transmit ready" forever without these. Returning
            // "always ready" makes DOS think the byte was accepted
            // instantly so the next char keeps flowing.
            //
            //   COM1 (0x3F8-0x3FF):
            //     0x3FD LSR: bit 5 = TBE (transmit buffer empty)
            //                bit 6 = TSE (transmit shift empty)
            //                bit 0 = data ready (we report none).
            //     0x3FE MSR: bit 4 = CTS, bit 5 = DSR (ready).
            //   LPT1 (0x378-0x37F):
            //     0x379 status: bit 7 = busy active-low (= 1 means NOT busy)
            //                   bit 6 = ack (no ack pending)
            //                   bit 4 = select (printer online)
            //                   bit 3 = error active-low (= 1 means no error)
            0x3FD or 0x2FD => 0x60,   // COM1/COM2 LSR = TBE | TSE, no data ready
            0x3FE or 0x2FE => 0x30,   // COM1/COM2 MSR = CTS | DSR
            0x379          => 0xD8,   // LPT1 status = not-busy + no-ack + online + no-error
            0x279          => 0xD8,   // LPT2 status mirror

            // FDC Digital Input Register (port 0x3F7). Bit 7 = DSKCHG
            // (disk change line). Bits 6-0 undriven on real hardware
            // (= float HIGH, read as 1).
            //
            // Phase 32.1 — route bit 7 to the FDC's active-drive
            // DSKCHG state. DiskImage.Swap() sets DiskChanged=true;
            // FDC clears it on the next SEEK / RECALIBRATE for that
            // drive. Steady-state (no swap) returns 0x7F = bit 7 low
            // = "no disk change", matching the original hard-coded
            // value that fixed the FreeCom-`ver`-90s-per-char bug
            // (Gemini 2026-05-16 consultation, tools/knowledgebase/
            // message/20260516_152244.txt).
            //
            // CRITICAL: without this bit, DOS caches FAT sectors of
            // the previous disk and writes them onto the new disk —
            // permanent FS corruption on disk swap. Per Gemini
            // 2026-05-18 consult (knowledgebase/message/20260518_184350.txt).
            0x3F7 or 0x377 => (byte)((_fdc?.ActiveDriveDiskChanged == true ? 0x80 : 0x00) | 0x7F),

            // 8255 PPI Port C (0x62) — DIP switch readback. pcxtbios.bin
            // (Sergey Kiselev/VirtualXT XT BIOS) uses an UNUSUAL pattern
            // that differs from IBM original PC layout:
            //
            //   in al, 62h                ; first read -- memory size in low 4 bits
            //   and al, 0Fh
            //   mov ah, al                ;   save
            //   mov al, 10101101b
            //   out dx, al                ; toggle PPI selector
            //   in al, 62h                ; second read -- video+floppy in low 4 bits
            //   mov cl, 4
            //   shl al, cl                ;   shift to high nibble
            //   or al, ah                 ; combine -> equipment flag
            //
            // So both reads use LOW 4 bits. The OUT-to-PPI-control between
            // them toggles which DIP bank is exposed. The pre-OUT state is
            // memory bits; post-OUT state is video+floppy. Per BIOS source:
            //   Pre-OUT  low 4: memory (we report 0x03 = 256KB-class)
            //   Post-OUT low 4: bits 0-1 = video, bits 2-3 = floppy-count
            //     video: 00 = EGA/VGA, 01 = CGA 40x25, 10 = CGA 80x25,
            //            11 = MDA 80x25 monochrome
            //     floppy: 00 = 1 drive
            //
            // Bit 3 of _port61 is the standard PC/XT 8255 PIA Port B SW2
            // select line. pcxtbios writes 0xAD before the 2nd 0x62 read
            // (bit 3 = 1, selects video+floppy); 0x85 before the 1st
            // (bit 3 = 0, selects memory). Bit 2 is the pcxtbios TURBO
            // flag (sticky from boot per asm line 491) and is NOT the
            // selector — earlier code that used bit 2 worked accidentally
            // when TURBO_ENABLED happened to leave it clear; with VBIOS
            // loaded the timing exposed the bug. See Phase 30.14c.
            //
            // Configurable via --video=mda|cga CLI flag (default mda).
            // Tested mda -> BIOS picks MDA, sets BDA[0x49]=0x07, writes
            // to 0xB0000. Tested cga -> BIOS picks CGA, sets BDA[0x49]
            // based on CRTC probe, writes to 0xB8000.
            0x62 => Read62(),

            // Default — open bus
            _ => 0xFF,
        };
        if (_traceIo && port != 0x40 && port != 0x21)   // skip the noisy ones
            Console.Error.WriteLine($"  [IO] IN  port=0x{port:X3} → 0x{v:X2}");
        return v;
    }

    // Phase 29-supp — MDA/CGA status register read counter. Each access
    // bumps the counter; we produce a 4-state cycle (00, 01, 09, 08) so
    // both bit 0 (HSYNC) and bit 3 (VSYNC) traverse all four edges
    // within 4 reads. BIOS polling code samples these to find both the
    // start and end of retrace pulses; a per-read counter is enough for
    // POST detection without needing wall-clock cycle accuracy.
    private long _videoStatusCount;
    private byte ReadVideoStatus()
    {
        _videoStatusCount++;
        var c = _videoStatusCount & 3;
        return c switch
        {
            0 => 0x00,   // neither retrace
            1 => 0x01,   // HSYNC only
            2 => 0x09,   // both
            _ => 0x08,   // VSYNC only
        };
    }

    public ushort Read16(ushort port)
    {
        // 16-bit IN — read low then high port byte. Common idiom for
        // DMA / video pairs. Implement as two 8-bit reads.
        byte lo = Read8(port);
        byte hi = Read8((ushort)(port + 1));
        return (ushort)(lo | (hi << 8));
    }

    public void Write8(ushort port, byte value)
    {
        if (_traceIo && port != 0x80 && port != 0x43)
            Console.Error.WriteLine($"  [IO] OUT port=0x{port:X3} ← 0x{value:X2}");

        switch (port)
        {
            // PIC master
            case 0x20:
                // OCW2 — typically EOI (0x20). We treat any write as
                // "ack the in-service IRQ" (our Pic8259 doesn't track
                // ISR explicitly; AssertIrq is edge-triggered).
                break;
            case 0x21:
                // OCW1 — IMR (interrupt mask). Bit set = masked.
                for (byte i = 0; i < 8; i++)
                    _pic.SetMask(i, (value & (1 << i)) != 0);
                break;

            // PIT 8253 — channel data writes update reload, control
            // word selects mode. We ignore for now since PcPit drives
            // its tick from the host wall clock; FreeDOS just reprograms
            // the PIT to default 18.2 Hz which we already provide.
            case 0x40: case 0x41: case 0x42: case 0x43:
                break;

            // 8042 keyboard data write (controller command response)
            case 0x60:
                _kbd60Data = value;
                break;
            // 8042 command port
            case 0x64:
                // Common command 0xFE = pulse output port low (system reset).
                // Ignore for now.
                break;

            // Speaker gate / sys ctrl + keyboard ACK (XT only — port 0x61 bit 7
            // pulse is what de-asserts the keyboard IRQ line and posts the
            // next scancode from our FIFO).
            case 0x61:
                {
                    byte old = _port61;
                    _port61 = value;
                    _pit.SpeakerGate = (value & 0x02) != 0;
                    OnPort61Edge(old, value);
                }
                break;

            // POST diagnostic
            case 0x80:
                _port80 = value;
                break;

            // Phase 30.14a — Bochs/QEMU "Port 0xE9 debug-out" hack.
            // Real PC/XT hardware leaves port 0xE9 unassigned. Both Bochs
            // and QEMU (and BIRGER's testdev) optionally repurpose it as a
            // host-side console: every OUT 0xE9, AL writes AL straight to
            // the host's stdout / a log file. Test programs can signal
            // results back to the test harness with a 2-instruction stub:
            //     mov al, '*' ; out 0xE9, al
            // Avoids screen-scraping the framebuffer for test pass/fail.
            case 0xE9:
                WritePortE9(value);
                break;

            // NMI mask
            case 0xA0:
                _nmiMask = value;
                break;

            // CMOS index / data
            case 0x70:
                _cmosIndex = (byte)(value & 0x7F);
                // High bit is NMI disable; ignore for now.
                break;
            case 0x71:
                _cmos[_cmosIndex & 0x3F] = value;
                break;

            // Phase 30 — 8272 FDC writes.
            case 0x3F2 when _fdc is not null: _fdc.WriteDor(value);   break;
            case 0x3F5 when _fdc is not null: _fdc.WriteFifo(value);  break;
            case 0x3F7:                                                break;  // FDC CCR (data rate); ignore

            // Phase 30 — 8237 DMA writes (channel 2 only + flip-flop).
            case 0x04 when _dma is not null:
            case 0x05 when _dma is not null:
            case 0x0A when _dma is not null:
            case 0x0B when _dma is not null:
            case 0x0C when _dma is not null:
            case 0x0D when _dma is not null:
            case 0x81 when _dma is not null:
                _dma.Write8(port, value);
                break;

            // Default — silently ignore (slave PIC, MPU-401, etc.).
            default:
                break;
        }
    }

    public void Write16(ushort port, ushort value)
    {
        Write8(port,                (byte)(value & 0xFF));
        Write8((ushort)(port + 1),  (byte)((value >> 8) & 0xFF));
    }

    // === Port 0xE9 debug-out hook (Phase 30.14a) ====================

    private static readonly object _e9Lock = new();
    private static readonly StringBuilder _e9LineBuf = new(256);
    private static StreamWriter? _e9Writer;
    private const string E9LogPath = "temp/port-e9.log";

    /// <summary>
    /// Toggle whether bytes emitted to port 0xE9 are mirrored to the host
    /// stdout in addition to the per-run log file. Defaults to true — the
    /// whole point of the hack is real-time visibility while the guest
    /// runs. Set false for headless / CI runs that want only the log.
    /// </summary>
    public static bool PortE9MirrorStdout { get; set; } = true;

    /// <summary>
    /// Reset the port-0xE9 log (truncate temp/port-e9.log + drop the line
    /// buffer). Call once per emulator launch from Program.cs so each
    /// session is self-contained.
    /// </summary>
    public static void ResetPortE9Log()
    {
        lock (_e9Lock)
        {
            _e9LineBuf.Clear();
            _e9Writer?.Dispose();
            var dir = Path.GetDirectoryName(E9LogPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            _e9Writer = new StreamWriter(E9LogPath, append: false)
            {
                AutoFlush = true,
            };
            _e9Writer.WriteLine(
                $"# port-0xE9 log opened {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} UTC");
        }
    }

    private static void WritePortE9(byte value)
    {
        // Flush per newline to keep the log readable while a test runs.
        // Non-printable bytes are escaped \xNN so binary signals are
        // visible without breaking the line format.
        char c = (char)value;
        string token = value switch
        {
            0x0A => "\n",
            0x0D => "",                    // collapse CRLF
            >= 0x20 and < 0x7F => c.ToString(),
            _    => $"\\x{value:X2}",
        };

        lock (_e9Lock)
        {
            if (token == "\n")
            {
                var line = _e9LineBuf.ToString();
                _e9LineBuf.Clear();
                _e9Writer?.WriteLine(line);
                if (PortE9MirrorStdout)
                    Console.Out.WriteLine($"[E9] {line}");
            }
            else
            {
                _e9LineBuf.Append(token);
                // Flush partial line if it gets long — caller may not
                // emit a newline (e.g. printing one char as test signal).
                if (_e9LineBuf.Length >= 240)
                {
                    var line = _e9LineBuf.ToString();
                    _e9LineBuf.Clear();
                    _e9Writer?.WriteLine(line + " [no-newline-flush]");
                    if (PortE9MirrorStdout)
                        Console.Out.WriteLine($"[E9] {line} [no-newline-flush]");
                }
            }
        }
    }

    /// <summary>
    /// Phase 30.15d sprint 5.4b — snapshot/restore for the verifier
    /// framework. Captures the small amount of in-PortBus state that
    /// evolves independently of guest memory (cycling counters, control
    /// register copies, kbd FIFO position) so the JIT-vs-interp diff
    /// doesn't see spurious divergences from one env's PortBus being
    /// "ahead" of the other's. Does NOT snapshot Pic / Pit / Fdc / Dma
    /// — those have their own state, snapshotted separately by the
    /// env wrapper.
    /// </summary>
    public PcPortBusSnapshot Snapshot() => new()
    {
        VideoStatusCount = _videoStatusCount,
        Port61 = _port61,
        Port80 = _port80,
        NmiMask = _nmiMask,
        CmosIndex = _cmosIndex,
        CmosCopy = (byte[])_cmos.Clone(),
        Kbd60Data = _kbd60Data,
        Kbd64Status = _kbd64Status,
        Kbd60IrqLine = _kbd60IrqLine,
    };

    public void RestoreSnapshot(PcPortBusSnapshot s)
    {
        _videoStatusCount = s.VideoStatusCount;
        _port61 = s.Port61;
        _port80 = s.Port80;
        _nmiMask = s.NmiMask;
        _cmosIndex = s.CmosIndex;
        Array.Copy(s.CmosCopy, _cmos, s.CmosCopy.Length);
        _kbd60Data = s.Kbd60Data;
        _kbd64Status = s.Kbd64Status;
        _kbd60IrqLine = s.Kbd60IrqLine;
    }
}

/// <summary>Snapshot blob for PcPortBus (Phase 30.15d sprint 5.4b).</summary>
public sealed class PcPortBusSnapshot
{
    public long VideoStatusCount;
    public byte Port61;
    public byte Port80;
    public byte NmiMask;
    public byte CmosIndex;
    public byte[] CmosCopy = System.Array.Empty<byte>();
    public byte Kbd60Data;
    public byte Kbd64Status;
    public bool Kbd60IrqLine;
}
