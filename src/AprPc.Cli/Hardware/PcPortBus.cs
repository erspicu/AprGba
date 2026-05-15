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

    public PcPortBus(Pic8259 pic, PcPit pit, bool traceIo = false)
    {
        _pic     = pic ?? throw new ArgumentNullException(nameof(pic));
        _pit     = pit ?? throw new ArgumentNullException(nameof(pit));
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

            // 8042 keyboard
            0x60 => _kbd60Data,
            0x64 => _kbd64Status,

            // Speaker gate / system control
            0x61 => _port61,

            // POST diagnostic
            0x80 => _port80,

            // NMI mask (PC XT)
            0xA0 => _nmiMask,

            // CMOS data
            0x71 => _cmos[_cmosIndex & 0x3F],

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

            // Speaker gate / sys ctrl
            case 0x61:
                _port61 = value;
                _pit.SpeakerGate = (value & 0x02) != 0;
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

            // Default — silently ignore (DMA, slave PIC, MPU-401, etc.).
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
