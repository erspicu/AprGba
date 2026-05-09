namespace AprNes.Cli.Memory
{
    /// <summary>
    /// Mapper 0 (NROM) — ported from erspicu/AprNes commit fcbbb23.
    ///
    /// The simplest cartridge layout: no banking at all.
    ///   - 16KB or 32KB PRG-ROM mapped at $8000-$FFFF (16KB mirrors $C000-$FFFF).
    ///   - 8KB CHR-ROM (or CHR-RAM if the iNES header says CHR count = 0)
    ///     mapped at PPU $0000-$1FFF.
    ///   - Optional 8KB PRG-RAM at $6000-$7FFF (we always provide it).
    /// </summary>
    public sealed class Mapper000 : IMapper
    {
        // PRG-ROM: either 16KB (mirrored) or 32KB. After Reset() this is always
        // exactly 32KB so $8000-$FFFF can index directly.
        private byte[] _prgRom = System.Array.Empty<byte>();

        // CHR-ROM if present, otherwise CHR-RAM (8KB writable).
        private byte[] _chr = System.Array.Empty<byte>();
        private bool _chrIsRam;

        // 8KB cartridge PRG-RAM at $6000-$7FFF.
        private readonly byte[] _prgRam = new byte[0x2000];

        //NROM ok!
        public void Reset(byte[] prgRom, byte[] chrRom)
        {
            // Normalise PRG to 32KB by mirroring 16KB carts, so CpuRead can
            // index `addr - 0x8000` without a branch.
            if (prgRom.Length == 0x4000)
            {
                _prgRom = new byte[0x8000];
                System.Buffer.BlockCopy(prgRom, 0, _prgRom, 0x0000, 0x4000);
                System.Buffer.BlockCopy(prgRom, 0, _prgRom, 0x4000, 0x4000);
            }
            else
            {
                _prgRom = prgRom;
            }

            if (chrRom == null || chrRom.Length == 0)
            {
                _chr = new byte[0x2000];
                _chrIsRam = true;
            }
            else
            {
                _chr = chrRom;
                _chrIsRam = false;
            }

            System.Array.Clear(_prgRam, 0, _prgRam.Length);
        }

        public byte CpuRead(ushort addr)
        {
            if (addr >= 0x8000)
            {
                return _prgRom[addr - 0x8000];
            }
            if (addr >= 0x6000)
            {
                return _prgRam[addr - 0x6000];
            }
            // $4020-$5FFF: expansion ROM area — open bus on NROM.
            return 0;
        }

        public void CpuWrite(ushort addr, byte value)
        {
            if (addr >= 0x8000)
            {
                // PRG-ROM is read-only on NROM.
                return;
            }
            if (addr >= 0x6000)
            {
                _prgRam[addr - 0x6000] = value;
            }
            // $4020-$5FFF: expansion area — silently ignored.
        }

        public byte PpuRead(ushort addr)
        {
            return _chr[addr & 0x1FFF];
        }

        public void PpuWrite(ushort addr, byte value)
        {
            if (_chrIsRam)
            {
                _chr[addr & 0x1FFF] = value;
            }
            // CHR-ROM games silently drop the write.
        }

        // NROM has fixed mirroring (set from iNES header at startup); never
        // changes at runtime. The interface property is here for protocol
        // conformance — Mapper001 (MMC1) actually uses it.
        public Action<int>? MirroringChanged { get; set; }

        // NROM has no PRG bank switching — bank-switch callback never fires.
        public Action<uint, uint>? PrgBankSwitched { get; set; }
    }
}
