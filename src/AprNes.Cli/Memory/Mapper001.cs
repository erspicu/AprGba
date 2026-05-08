// Mapper 1 (MMC1) — ported from erspicu/AprNes commit fcbbb23
// (OldProject/AprNes/AprNes/NesCore/Mapper/Mapper001.cs).
//
// MMC1 has a 5-bit serial shift register fed one bit at a time by writes
// to $8000-$FFFF. Bit 7 of any write resets the shift register and forces
// PRG mode 3 (fix last bank at $C000). After 5 writes (LSB first), the
// assembled value is latched into one of four register banks selected by
// bits 13-14 of the destination address:
//
//   $8000-$9FFF: Control  (mirroring, PRG bankmode, CHR bankmode)
//   $A000-$BFFF: CHR bank 0
//   $C000-$DFFF: CHR bank 1
//   $E000-$FFFF: PRG bank
//
// PRG bankmode:
//   0 / 1 — switch full 32KB at $8000 (low bit of bank-select ignored)
//   2     — fix first 16KB at $8000, switch 16KB at $C000
//   3     — fix last 16KB at $C000, switch 16KB at $8000  (power-on default)
//
// CHR bankmode:
//   0 — switch 8KB at PPU $0000 (CHR bank 0 select; CHR bank 1 ignored)
//   1 — switch two separate 4KB banks ($0000 + $1000)
//
// Mirroring:
//   0 — one-screen, lower bank
//   1 — one-screen, upper bank
//   2 — vertical
//   3 — horizontal  (default after power-on for blargg's H-mirroring carts)
//
// One-screen modes are degraded to the closest H/V approximation since the
// NesPpu fold table only handles H/V. For most test ROMs (which use H or
// V) this is exact; for games that rely on one-screen (e.g. some MMC1
// titles like Final Fantasy split-screen status bar) there will be visual
// artifacts. Listed as a known limitation in the per-method comments.

using System;

namespace AprNes.Cli.Memory
{
    public sealed class Mapper001 : IMapper
    {
        // PRG-ROM as raw bank-aware buffer; CpuRead computes the right offset.
        private byte[] _prgRom = Array.Empty<byte>();
        private int _prgBanks16k;        // 16KB-bank count (PRG_ROM_count in source)

        // CHR-ROM if present, else CHR-RAM (8KB writable).
        private byte[] _chr = Array.Empty<byte>();
        private bool _chrIsRam;
        private int _chrBanks8k;         // CHR_ROM_count in source

        // Cartridge PRG-RAM at $6000-$7FFF (8KB; some MMC1 boards have more,
        // not modeled here).
        private readonly byte[] _prgRam = new byte[0x2000];

        // MMC1 register state.
        private int _shiftCount;         // 0..4
        private int _shiftReg;           // assembled bits LSB-first
        private int _ctrl = 0x0C;        // control register; power-on = PRG mode 3 (fix last)
        private int _chr0Select;
        private int _chr1Select;
        private int _prgSelect;

        // Decoded ctrl fields:
        private int Mirroring => _ctrl & 0x03;        // 0=oneL, 1=oneU, 2=V, 3=H
        private int PrgMode   => (_ctrl >> 2) & 0x03; // 0/1/2/3
        private int ChrMode   => (_ctrl >> 4) & 0x01; // 0=8KB / 1=4KB×2

        public Action<int>? MirroringChanged { get; set; }

        public void Reset(byte[] prgRom, byte[] chrRom)
        {
            _prgRom = prgRom;
            _prgBanks16k = prgRom.Length / 0x4000;

            if (chrRom == null || chrRom.Length == 0)
            {
                _chr = new byte[0x2000];
                _chrIsRam = true;
                _chrBanks8k = 0;
            }
            else
            {
                _chr = chrRom;
                _chrIsRam = false;
                _chrBanks8k = chrRom.Length / 0x2000;
            }

            // Power-on state per nesdev: control = $0C (PRG mode 3, CHR mode 0,
            // mirroring undefined but games always set it). Source matches:
            // PRG_Bankmode = 3 default, last bank fixed at $C000.
            _shiftCount = 0;
            _shiftReg = 0;
            _ctrl = 0x0C;
            _chr0Select = 0;
            _chr1Select = 0;
            _prgSelect = _prgBanks16k - 2;   // matches source: PRG_Bankselect = _PRG_ROM_count - 2

            Array.Clear(_prgRam, 0, _prgRam.Length);
        }

        public byte CpuRead(ushort addr)
        {
            if (addr >= 0x8000)
            {
                int bank, off;
                switch (PrgMode)
                {
                    case 0: case 1:
                        // 32KB switch at $8000; ignore low bit of select.
                        bank = (_prgSelect & ~1) % Math.Max(1, _prgBanks16k);
                        off  = (addr - 0x8000) + (bank << 14);
                        return _prgRom[off];
                    case 2:
                        // Fix first 16KB at $8000; switch 16KB at $C000.
                        if (addr < 0xC000) return _prgRom[addr - 0x8000];
                        bank = _prgSelect % Math.Max(1, _prgBanks16k);
                        off  = (addr - 0xC000) + (bank << 14);
                        return _prgRom[off];
                    default: // mode 3
                        // Switch 16KB at $8000; fix last 16KB at $C000.
                        if (addr < 0xC000)
                        {
                            bank = _prgSelect % Math.Max(1, _prgBanks16k);
                            off  = (addr - 0x8000) + (bank << 14);
                            return _prgRom[off];
                        }
                        return _prgRom[(addr - 0xC000) + ((_prgBanks16k - 1) << 14)];
                }
            }
            if (addr >= 0x6000)
            {
                return _prgRam[addr - 0x6000];
            }
            return 0;
        }

        public void CpuWrite(ushort addr, byte value)
        {
            if (addr >= 0x8000)
            {
                // Reset bit (bit 7 set): clear shift state and force PRG
                // mode 3 (control |= $0C). Source's behavior is "set
                // PRG_Bankmode = 3" which matches setting ctrl bits 2-3.
                if ((value & 0x80) != 0)
                {
                    _shiftCount = 0;
                    _shiftReg = 0;
                    _ctrl |= 0x0C;
                    return;
                }

                // Shift in LSB of value.
                _shiftReg |= (value & 1) << _shiftCount;
                _shiftCount++;
                if (_shiftCount < 5) return;

                // 5-bit value assembled — latch into selected register.
                if (addr < 0xA000)
                {
                    int oldMirroring = Mirroring;
                    _ctrl = _shiftReg;
                    int newMirroring = Mirroring;
                    if (newMirroring != oldMirroring)
                    {
                        // Map MMC1 mirroring (0=oneL, 1=oneU, 2=V, 3=H) to
                        // OldProject encoding (0=H, 1=V, 2=oneL, 3=oneU)
                        // expected by NesPpu.SetMirroringMode.
                        int mode = newMirroring switch
                        {
                            0 => 2, // one-screen lower
                            1 => 3, // one-screen upper
                            2 => 1, // vertical
                            _ => 0  // horizontal
                        };
                        MirroringChanged?.Invoke(mode);
                    }
                }
                else if (addr < 0xC000) _chr0Select = _shiftReg;
                else if (addr < 0xE000) _chr1Select = _shiftReg;
                else                    _prgSelect  = _shiftReg & 0x0F;

                _shiftReg = 0;
                _shiftCount = 0;
            }
            else if (addr >= 0x6000)
            {
                _prgRam[addr - 0x6000] = value;
            }
        }

        public byte PpuRead(ushort addr)
        {
            int a = addr & 0x1FFF;
            if (_chrIsRam) return _chr[a];

            if (ChrMode == 1)
            {
                // 4KB × 2 mode.
                int banks4k = _chrBanks8k * 2;
                if (banks4k == 0) return 0;
                if (a < 0x1000) return _chr[a + ((_chr0Select % banks4k) << 12)];
                return _chr[(a - 0x1000) + ((_chr1Select % banks4k) << 12)];
            }
            // 8KB mode: low bit of select ignored.
            int banks8k = Math.Max(1, _chrBanks8k);
            return _chr[a + 0x2000 * ((_chr0Select >> 1) % banks8k)];
        }

        public void PpuWrite(ushort addr, byte value)
        {
            if (_chrIsRam) _chr[addr & 0x1FFF] = value;
        }
    }
}
