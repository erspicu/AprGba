// Dma8237 — Intel 8237A DMA controller (channel 2 only for now).
//
// Phase 30 — real PC/XT BIOS programs the DMA controller to set up
// floppy disk transfer (channel 2 = FDC). For minimum-viable boot we
// only need channel 2's address + count + mode + mask + page registers,
// plus the flip-flop that toggles between LO/HI byte writes.
//
// Per Gemini consultation 2026-05-16: the synchronous-burst cheat
// (do the entire transfer in one go when FDC enters execution phase)
// is what 90% of emulators ship with. Cycle-accurate interleaved
// byte-by-byte DMA is unnecessary for BIOS sector load.
//
// Reference: Intel 8237A DMA Controller Data Sheet (Oct 1986).

namespace AprPc.Cli.Hardware;

public sealed class Dma8237
{
    // Channel 2 state (FDC). Other channels are stubs.
    public ushort Ch2Base  { get; set; }   // current address
    public ushort Ch2Count { get; set; }   // word count (transfer = N+1 bytes)
    public byte   Ch2Page  { get; set; }   // high 4 bits of 24-bit physical addr
    public byte   Ch2Mode  { get; set; }   // mode register (write direction, auto-init, etc.)
    public byte   MaskReg  { get; set; } = 0x0F;  // all 4 channels masked at reset

    // Single internal flip-flop: 0 = next read/write hits LO byte, 1 = HI byte.
    // Cleared by writing any value to 0x0C. Used for 0x04, 0x05 (and other
    // channels' base/count regs).
    public bool FlipFlop { get; set; }

    // Status register (read via 0x08). Bit 0..3 = ch0..3 TC reached.
    public byte Status   { get; set; }

    private readonly bool _trace;

    public Dma8237(bool trace = false) { _trace = trace; }

    public void Reset()
    {
        Ch2Base = 0; Ch2Count = 0; Ch2Page = 0; Ch2Mode = 0;
        MaskReg = 0x0F; FlipFlop = false; Status = 0;
    }

    // ---- Port handlers ----
    public byte Read8(ushort port)
    {
        return port switch
        {
            // 0x04 / 0x05 — read of channel 2 base / count. Flip-flop
            // tracks which byte. After each access the flip-flop toggles.
            0x04 => ReadFlipFlopByte(Ch2Base),
            0x05 => ReadFlipFlopByte(Ch2Count),
            0x08 => Status,
            _    => 0xFF,
        };
    }

    public void Write8(ushort port, byte value)
    {
        switch (port)
        {
            case 0x04: Ch2Base  = WriteFlipFlopByte(Ch2Base,  value); break;
            case 0x05: Ch2Count = WriteFlipFlopByte(Ch2Count, value); break;
            case 0x0A: MaskReg  = value; break;    // mask register write
            case 0x0B: Ch2Mode  = (value & 0x03) == 0x02 ? value : Ch2Mode;  // only honor ch2 programming
                       break;
            case 0x0C: FlipFlop = false; break;    // clear flip-flop
            case 0x0D: Reset(); break;             // master reset
            case 0x81: Ch2Page  = value; break;    // ch2 page register (only one we wire)
            default: break;
        }
    }

    private byte ReadFlipFlopByte(ushort src)
    {
        byte b = FlipFlop ? (byte)(src >> 8) : (byte)(src & 0xFF);
        FlipFlop = !FlipFlop;
        return b;
    }

    private ushort WriteFlipFlopByte(ushort current, byte v)
    {
        ushort result = FlipFlop
            ? (ushort)((current & 0x00FF) | (v << 8))
            : (ushort)((current & 0xFF00) | v);
        FlipFlop = !FlipFlop;
        return result;
    }

    /// <summary>
    /// Phase 30 — compute the 20-bit physical base address for the
    /// currently-programmed channel 2 transfer. Real silicon assembles
    /// `(page &lt;&lt; 16) | base`; the page register supplies bits 16-23
    /// (we use only bits 16-19 since 8086 has 20-bit address bus).
    /// </summary>
    public int Channel2PhysicalAddress => (Ch2Page << 16) | Ch2Base;

    /// <summary>
    /// Phase 30 — burst transfer count (bytes). DMA count is N-1 per
    /// Intel convention; caller wants the true byte count.
    /// </summary>
    public int Channel2TransferBytes => Ch2Count + 1;

    /// <summary>
    /// Phase 30 — called by Fdc8272 after a burst transfer completes.
    /// Updates DMA controller's view of state so subsequent BIOS reads
    /// of the count register return 0xFFFF (TC reached).
    /// </summary>
    public void OnChannel2BurstComplete(int bytesTransferred)
    {
        Ch2Base = (ushort)((Ch2Base + bytesTransferred) & 0xFFFF);
        Ch2Count = 0xFFFF;
        Status |= 0x04;  // ch2 TC bit
    }
}
