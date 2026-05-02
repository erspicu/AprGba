using System.Text;

namespace AprGb.Cli.Memory;

/// <summary>
/// Game Boy DMG memory bus with MBC1 banking and serial-port capture.
/// MBC2/3/5 not modelled — sufficient for Blargg's cpu_instrs and
/// instr_timing test ROMs.
///
/// Serial output capture: Blargg's CPU test ROMs print pass/fail text
/// by writing the character to <c>FF01</c> then <c>0x81</c> to
/// <c>FF02</c> (start transfer, internal clock). We intercept the
/// FF02 write and append <c>FF01</c>'s last value to <see cref="SerialLog"/>,
/// then immediately complete the transfer (write 0x01 to FF02 and raise
/// the serial interrupt — though no IRQ handler is needed for cpu_instrs).
/// </summary>
public sealed class GbMemoryBus
{
    public byte[] Rom    { get; private set; } = Array.Empty<byte>();
    public byte[] ExtRam { get; }              = new byte[0x8000];     // up to 32 KB MBC1 RAM
    public byte[] Wram   { get; }              = new byte[0x2000];     // 8 KB
    public byte[] Vram   { get; }              = new byte[0x2000];     // 8 KB
    public byte[] Oam    { get; }              = new byte[0xA0];       // 160 B
    public byte[] Hram   { get; }              = new byte[0x7F];       // 127 B (FF80-FFFE)
    public byte[] Io     { get; }              = new byte[0x80];       // FF00-FF7F

    /// <summary>
    /// DMG / DMG-0 boot ROM (256 bytes). When <see cref="BiosEnabled"/>
    /// is true the bus shadows reads at 0x0000..0x00FF with these bytes
    /// instead of the cart. Real hardware exposes the boot ROM at power-on
    /// and remaps it out via a one-shot write to 0xFF50 once the BIOS
    /// finishes (typically ~256 t-cycles after the Nintendo logo scroll).
    /// </summary>
    public byte[] Bios { get; private set; } = Array.Empty<byte>();
    public bool   BiosEnabled { get; private set; }

    /// <summary>Load a boot ROM image (256 bytes for DMG; 2304 for CGB).</summary>
    public void LoadBios(byte[] biosBytes)
    {
        if (biosBytes is null || biosBytes.Length == 0)
            throw new ArgumentException("BIOS bytes cannot be null/empty.", nameof(biosBytes));
        Bios = biosBytes;
        BiosEnabled = true;
    }

    public byte InterruptEnable;     // 0xFFFF
    public byte InterruptFlag;       // 0xFF0F shadowed in Io but exposed for clarity

    /// <summary>Captured serial output — Blargg test ROMs use this for pass/fail text.</summary>
    public StringBuilder SerialLog { get; } = new();

    /// <summary>
    /// Optional PPU clock scheduler. When attached, <see cref="Tick"/>
    /// advances scanline + LY + STAT mode bits + raises VBlank/STAT IRQs
    /// at the right cycles. Detached (default) keeps the legacy fake-VBlank
    /// behaviour for tests / harnesses that don't want a ticking PPU.
    /// </summary>
    public GbScheduler? Scheduler { get; set; }

    // MBC1 state
    private int  _romBank   = 1;
    private int  _ramBank   = 0;
    private bool _ramEnable = false;
    private bool _modeRamBank = false;    // false = ROM banking mode, true = RAM banking mode

    // Timer state (DIV/TIMA accumulators in t-cycles).
    private int _divAccum;
    private int _timaAccum;

    /// <summary>
    /// Advance hardware timers by <paramref name="tCycles"/> t-cycles.
    /// Called by the CPU after each instruction (and during HALT).
    /// Implements DIV (always-on, 16384 Hz) + TIMA (TAC-gated) per Pan Docs.
    /// On TIMA overflow, reloads from TMA and raises IF bit 2 (Timer IRQ).
    /// Also forwards the tick to <see cref="Scheduler"/> if attached, so
    /// LY / STAT / VBlank-IRQ timing tracks real hardware.
    /// </summary>
    public void Tick(int tCycles)
    {
        // PPU first — gives BIOS-LLE timing the tightest coupling. Order
        // doesn't really matter for the output, just for who raises IF
        // first if both fire on the same call.
        Scheduler?.Tick(tCycles);

        // DIV: always increments at 16384 Hz (every 256 t-cycles).
        _divAccum += tCycles;
        while (_divAccum >= 256) { _divAccum -= 256; Io[0x04]++; }

        // TIMA: only when TAC bit 2 is set.
        var tac = Io[0x07];
        if ((tac & 0x04) == 0) { _timaAccum = 0; return; }

        int period = (tac & 0x03) switch
        {
            0 => 1024,    // 4096 Hz
            1 => 16,      // 262144 Hz
            2 => 64,      // 65536 Hz
            _ => 256,     // 16384 Hz (TAC=11)
        };

        _timaAccum += tCycles;
        while (_timaAccum >= period)
        {
            _timaAccum -= period;
            if (Io[0x05] == 0xFF)
            {
                Io[0x05] = Io[0x06];          // reload TMA
                InterruptFlag |= 0x04;        // raise Timer IRQ
            }
            else
            {
                Io[0x05]++;
            }
        }
    }

    public void LoadRom(byte[] romBytes) => Rom = romBytes;

    public byte ReadByte(ushort addr)
    {
        // Boot-ROM shadow: when BIOS is mapped, reads in 0x0000..0x00FF go
        // to the boot ROM. The cart's own 0x100 (entry point) and beyond
        // are visible normally — that lets the BIOS verify the cart's
        // Nintendo logo at 0x104..0x133 by reading the cart, not itself.
        if (BiosEnabled && addr < 0x100)         return Bios[addr];
        if (addr < 0x4000)                       return SafeRom(addr);
        if (addr < 0x8000)                       return SafeRom((addr - 0x4000) + _romBank * 0x4000);
        if (addr < 0xA000)                       return Vram[addr - 0x8000];
        if (addr < 0xC000)                       return _ramEnable ? ExtRam[(addr - 0xA000) + _ramBank * 0x2000] : (byte)0xFF;
        if (addr < 0xE000)                       return Wram[addr - 0xC000];
        if (addr < 0xFE00)                       return Wram[addr - 0xE000];     // echo
        if (addr < 0xFEA0)                       return Oam[addr - 0xFE00];
        if (addr < 0xFF00)                       return 0xFF;                    // unusable
        if (addr == 0xFFFF)                      return InterruptEnable;
        if (addr >= 0xFF80)                      return Hram[addr - 0xFF80];
        return ReadIo((byte)(addr - 0xFF00));
    }

    public void WriteByte(ushort addr, byte v)
    {
        if (addr < 0x2000) { _ramEnable = (v & 0x0F) == 0x0A; return; }
        if (addr < 0x4000)
        {
            // Lower 5 bits of ROM bank, with the MBC1 quirk: bank values
            // 0/0x20/0x40/0x60 alias to 1/0x21/0x41/0x61 (no bank 0 in
            // the upper window).
            var lo = v & 0x1F;
            if (lo == 0) lo = 1;
            _romBank = (_romBank & 0x60) | lo;
            return;
        }
        if (addr < 0x6000)
        {
            // Upper 2 bits — depending on mode, either ROM bank high or
            // RAM bank.
            var hi = v & 0x3;
            if (_modeRamBank) _ramBank = hi;
            else              _romBank = (_romBank & 0x1F) | (hi << 5);
            return;
        }
        if (addr < 0x8000) { _modeRamBank = (v & 1) != 0; return; }
        if (addr < 0xA000) { Vram[addr - 0x8000] = v; return; }
        if (addr < 0xC000) { if (_ramEnable) ExtRam[(addr - 0xA000) + _ramBank * 0x2000] = v; return; }
        if (addr < 0xE000) { Wram[addr - 0xC000] = v; return; }
        if (addr < 0xFE00) { Wram[addr - 0xE000] = v; return; }     // echo
        if (addr < 0xFEA0) { Oam[addr - 0xFE00] = v; return; }
        if (addr < 0xFF00) return;       // unusable
        if (addr == 0xFFFF) { InterruptEnable = v; return; }
        if (addr >= 0xFF80) { Hram[addr - 0xFF80] = v; return; }
        WriteIo((byte)(addr - 0xFF00), v);
    }

    private byte SafeRom(int idx) => idx < Rom.Length ? Rom[idx] : (byte)0xFF;

    private byte ReadIo(byte off)
    {
        return off switch
        {
            0x00 => 0xCF,                    // P1 — no buttons pressed
            0x04 => Io[0x04],                // DIV
            0x05 => Io[0x05],                // TIMA
            0x06 => Io[0x06],                // TMA
            0x07 => Io[0x07],                // TAC
            0x0F => InterruptFlag,           // IF
            0x40 => Io[0x40],                // LCDC
            0x41 => Io[0x41],                // STAT (mode + LYC coincidence maintained by GbScheduler)
            0x42 => Io[0x42],                // SCY
            0x43 => Io[0x43],                // SCX
            // LY: when a Scheduler is attached it walks 0..153 like real
            // hardware. Without one we fall back to the legacy fake
            // VBlank line so headless test harnesses without a scheduler
            // still see a non-stuck LY.
            0x44 => Scheduler is not null ? Io[0x44] : (byte)0x90,
            0x45 => Io[0x45],                // LYC
            0x47 => Io[0x47],                // BGP
            0x48 => Io[0x48],                // OBP0
            0x49 => Io[0x49],                // OBP1
            0x4A => Io[0x4A],                // WY
            0x4B => Io[0x4B],                // WX
            _    => Io[off]
        };
    }

    private void WriteIo(byte off, byte v)
    {
        switch (off)
        {
            case 0x01:                       // SB — serial data buffer
                Io[0x01] = v;
                break;

            case 0x02:                       // SC — serial control
                Io[0x02] = v;
                if ((v & 0x80) != 0)
                {
                    // Bit 7 set = "start transfer". Capture the byte and
                    // immediately complete; raise serial IRQ flag.
                    SerialLog.Append((char)Io[0x01]);
                    Io[0x02] = (byte)(v & 0x7F);
                    InterruptFlag |= 0x08;
                }
                break;

            case 0x04:                       // DIV — any write resets to 0
                Io[0x04] = 0;
                _divAccum = 0;
                break;

            case 0x07:                       // TAC — reset TIMA accumulator on TAC change
                Io[0x07] = (byte)(v & 0x07);
                _timaAccum = 0;
                break;

            case 0x0F:                       // IF
                InterruptFlag = (byte)(v & 0x1F);
                break;

            case 0x50:                       // BOOT — write any non-zero bit to remap BIOS out
                Io[0x50] = v;
                if ((v & 1) != 0) BiosEnabled = false;
                break;

            default:
                Io[off] = v;
                break;
        }
    }
}
