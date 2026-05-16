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

    public PcPortBus(Pic8259 pic, PcPit pit, bool traceIo = false,
        Fdc8272? fdc = null, Dma8237? dma = null)
    {
        _pic     = pic ?? throw new ArgumentNullException(nameof(pic));
        _pit     = pit ?? throw new ArgumentNullException(nameof(pit));
        _fdc     = fdc;
        _dma     = dma;
        _traceIo = traceIo;
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

    public byte Read8(ushort port)
    {
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
}
