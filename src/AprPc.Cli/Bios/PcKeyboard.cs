// PcKeyboard — BIOS-level keyboard buffer management.
//
// Phase 28.3 keeps this entirely BIOS-buffer-level (HLE). Port 60h /
// 64h real emulation and INT 9 (IRQ 1) delivery wait for Phase 28.7;
// for now the host puts (ascii, scancode) pairs straight into the BDA
// ring buffer at 0040:001E-003D, and INT 16h HLE drains from there.
//
// BDA layout:
//   0040:001A (phys 0x041A): buffer head pointer (next read offset)
//   0040:001C (phys 0x041C): buffer tail pointer (next write offset)
//   0040:001E (phys 0x041E): buffer start (16 entries × 2 bytes)
//   0040:003E (phys 0x043E): buffer end exclusive
//   0040:0080 (phys 0x0480): buffer start offset (0x001E)
//   0040:0082 (phys 0x0482): buffer end offset   (0x003E)
//   0040:0017 (phys 0x0417): shift-flags byte
//
// Head/tail offsets are *relative to segment 0x0040*, so the BDA value
// "0x001E" means "the byte at physical 0x0041E".
//
// Empty: head == tail. Full: (tail + 2) wrapping == head. Wrap from
// 0x003C back to 0x001E.

using AprPc.Cli.Memory;

namespace AprPc.Cli.Bios;

public sealed class PcKeyboard
{
    public const int BdaHead       = 0x0041A;
    public const int BdaTail       = 0x0041C;
    public const int BdaBufferLo   = 0x0041E;
    public const int BdaBufferHi   = 0x0043E;
    public const int BdaShiftFlags = 0x00417;

    public const ushort BufferStartOffset = 0x001E;
    public const ushort BufferEndOffset   = 0x003E;

    private readonly PcMemoryBus _bus;
    private readonly object _lock = new();

    public PcKeyboard(PcMemoryBus bus)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
    }

    /// <summary>Initialise BDA head/tail/range. Call after PcMemoryBus.Reset().</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _bus.WriteWord16(BdaHead, BufferStartOffset);
            _bus.WriteWord16(BdaTail, BufferStartOffset);
            _bus.WriteWord16(0x00480, BufferStartOffset);
            _bus.WriteWord16(0x00482, BufferEndOffset);
            _bus.WriteByte (BdaShiftFlags, 0);
        }
    }

    /// <summary>Enqueue an (ASCII, scancode) pair. Drops the keystroke if the buffer is full.</summary>
    public bool Enqueue(byte ascii, byte scancode)
    {
        lock (_lock)
        {
            ushort head = _bus.ReadWord16(BdaHead);
            ushort tail = _bus.ReadWord16(BdaTail);
            ushort next = Advance(tail);
            if (next == head) return false;   // full
            int physWrite = 0x00400 + tail;
            _bus.WriteByte(physWrite,     ascii);
            _bus.WriteByte(physWrite + 1, scancode);
            _bus.WriteWord16(BdaTail, next);
            return true;
        }
    }

    /// <summary>True iff the ring is empty (no keystrokes pending).</summary>
    public bool IsEmpty
    {
        get
        {
            lock (_lock)
                return _bus.ReadWord16(BdaHead) == _bus.ReadWord16(BdaTail);
        }
    }

    /// <summary>
    /// Read (without removing) the next pending keystroke. Returns
    /// false if buffer is empty.
    /// </summary>
    public bool TryPeek(out byte ascii, out byte scancode)
    {
        lock (_lock)
        {
            ushort head = _bus.ReadWord16(BdaHead);
            ushort tail = _bus.ReadWord16(BdaTail);
            if (head == tail) { ascii = 0; scancode = 0; return false; }
            int physRead = 0x00400 + head;
            ascii    = _bus.ReadByte(physRead);
            scancode = _bus.ReadByte(physRead + 1);
            return true;
        }
    }

    /// <summary>
    /// Remove and return the next pending keystroke. Returns false if
    /// buffer is empty (caller should block-wait + try again).
    /// </summary>
    public bool TryDequeue(out byte ascii, out byte scancode)
    {
        lock (_lock)
        {
            if (!TryPeek(out ascii, out scancode)) return false;
            ushort head = _bus.ReadWord16(BdaHead);
            _bus.WriteWord16(BdaHead, Advance(head));
            return true;
        }
    }

    public byte ShiftFlags
    {
        get { lock (_lock) return _bus.ReadByte(BdaShiftFlags); }
        set { lock (_lock) _bus.WriteByte(BdaShiftFlags, value); }
    }

    private static ushort Advance(ushort offset)
    {
        ushort next = (ushort)(offset + 2);
        if (next >= BufferEndOffset) next = BufferStartOffset;
        return next;
    }
}
