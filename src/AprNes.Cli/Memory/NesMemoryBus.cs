// NES memory bus — ported from erspicu/AprNes commit fcbbb23.
//
// Routes CPU bus accesses to:
// - $0000-$1FFF: 2KB internal WRAM (mirrored)
// - $2000-$3FFF: PPU registers ($2000-$2007 mirrored)
// - $4000-$4017: APU + IO registers (joypad, OAM DMA, frame counter)
// - $4020-$FFFF: cartridge (delegated to IMapper)
//
// PPU/APU register handling is stubbed — wire to NesPpu / NesApu when
// those are added. OAM DMA ($4014) timing is preserved (513-cycle stall).

using System.Runtime.CompilerServices;

namespace AprNes.Cli.Memory
{
    public sealed unsafe class NesMemoryBus
    {
        // 2KB internal CPU work RAM at $0000-$07FF (mirrored up to $1FFF).
        private readonly byte[] _wram = new byte[0x0800];

        // PPU-side memory exposed for diagnostics. The PPU module (NesPpu)
        // will own these once wired; for now they live here so save-state /
        // memory dump tooling has a stable place to look.
        //   - _vram      : 16KB nametable + palette region (matches source's `ppu_ram`)
        //   - _oam       : 256-byte sprite RAM (matches source's `spr_ram`)
        //   - _paletteRam: 32-byte palette ($3F00-$3F1F inside _vram is the
        //                  authoritative copy; this is a convenience alias)
        private readonly byte[] _vram = new byte[0x4000];
        private readonly byte[] _oam = new byte[0x100];
        private readonly byte[] _paletteRam = new byte[0x20];

        // CPU open-bus latch. Every CPU bus access updates this; reads from
        // unmapped addresses ($4018-$401F, write-only APU regs, ...) return it.
        private byte _cpubus;

        // OAM DMA write pointer (mirrors source's spr_ram_add). The CPU writes
        // $4014 -> we copy 256 bytes from $XX00 to OAM starting at this offset.
        private byte _oamDmaWritePtr;

        // Pending CPU stall cycles owed to the scheduler — currently only OAM
        // DMA contributes (513). Tick() drains this on the next dispatch.
        private int _pendingStallCycles;

        // Accumulated CPU cycles waiting to be flushed to the PPU/APU. The
        // framework calls Tick() per instruction; once PPU/APU are wired the
        // catch-up loop will consume this.
        private int _pendingCatchUpCycles;

        private IMapper _mapper = new NullMapper();

        public NesMemoryBus()
        {
        }

        public NesMemoryBus(IMapper mapper)
        {
            Reset(mapper);
        }

        /// <summary>
        /// Re-bind the bus to a freshly-loaded cartridge. Clears WRAM, OAM,
        /// VRAM, palette, and pending stall/catch-up state. The mapper itself
        /// must already have been Reset() with PRG/CHR data.
        /// </summary>
        public void Reset(IMapper mapper)
        {
            _mapper = mapper ?? new NullMapper();
            System.Array.Clear(_wram, 0, _wram.Length);
            System.Array.Clear(_vram, 0, _vram.Length);
            System.Array.Clear(_oam, 0, _oam.Length);
            System.Array.Clear(_paletteRam, 0, _paletteRam.Length);
            _cpubus = 0;
            _oamDmaWritePtr = 0;
            _pendingStallCycles = 0;
            _pendingCatchUpCycles = 0;
        }

        /// <summary>2KB internal WRAM. Exposed so the AprCpu IR pipeline can
        /// inline zero-page / stack writes directly without going through the
        /// switch dispatcher.</summary>
        public byte[] Wram => _wram;

        /// <summary>16KB PPU VRAM (nametables + palette). Diagnostic only.</summary>
        public byte[] Vram => _vram;

        /// <summary>256-byte OAM (sprite RAM). Diagnostic only.</summary>
        public byte[] Oam => _oam;

        /// <summary>32-byte palette mirror. Diagnostic only.</summary>
        public byte[] PaletteRam => _paletteRam;

        /// <summary>Pending cycles owed to the scheduler from OAM DMA stalls.
        /// The CPU loop should add this to the current instruction's cycle
        /// budget and clear it via <see cref="ConsumeStallCycles"/>.</summary>
        public int PendingStallCycles => _pendingStallCycles;

        /// <summary>Atomically read-and-clear the OAM DMA stall counter.</summary>
        public int ConsumeStallCycles()
        {
            int s = _pendingStallCycles;
            _pendingStallCycles = 0;
            return s;
        }

        // --- CPU bus dispatch ----------------------------------------------

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte ReadByte(ushort addr)
        {
            byte val;
            if (addr < 0x2000)
            {
                // $0000-$1FFF: 2KB WRAM mirrored every 0x800.
                val = _wram[addr & 0x07FF];
            }
            else if (addr < 0x4000)
            {
                // $2000-$3FFF: PPU registers, mirrored every 8 bytes.
                val = ReadPpu((ushort)(0x2000 | (addr & 0x0007)));
            }
            else if (addr < 0x4020)
            {
                // $4000-$401F: APU + IO. $4014 is OAM DMA (write-only),
                // $4016/$4017 are joypad reads, the rest are stubbed.
                val = ReadApu(addr);
            }
            else
            {
                // $4020-$FFFF: cartridge.
                val = _mapper.CpuRead(addr);
            }
            _cpubus = val;
            return val;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteByte(ushort addr, byte value)
        {
            _cpubus = value;
            if (addr < 0x2000)
            {
                _wram[addr & 0x07FF] = value;
            }
            else if (addr < 0x4000)
            {
                WritePpu((ushort)(0x2000 | (addr & 0x0007)), value);
            }
            else if (addr < 0x4020)
            {
                WriteApu(addr, value);
            }
            else
            {
                _mapper.CpuWrite(addr, value);
            }
        }

        // --- Zero-page fast paths ------------------------------------------
        // The framework's IR-level fast path knows that zero-page accesses
        // ($0000-$00FF) and stack accesses ($0100-$01FF) always land in WRAM,
        // so it can skip the dispatcher entirely. These helpers exist so that
        // hand-rolled host code can do the same.

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte ReadZeroPage(byte addr)
        {
            byte val = _wram[addr];
            _cpubus = val;
            return val;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteZeroPage(byte addr, byte value)
        {
            _cpubus = value;
            _wram[addr] = value;
        }

        // --- Scheduler ------------------------------------------------------

        /// <summary>
        /// Account for <paramref name="cpuCycles"/> CPU cycles having elapsed.
        /// The PPU runs at 3x and the APU at 1x; once those modules are wired
        /// this will drive their catch-up loops. For now we only accumulate.
        /// </summary>
        public void Tick(int cpuCycles)
        {
            _pendingCatchUpCycles += cpuCycles;
            // Drain into the PPU hook if attached. NES NTSC: 3 PPU dots
            // per CPU cycle. PPU implementation is a separate component
            // (AprNes.Cli.Video.NesPpu); bus binds via delegate to avoid
            // a Memory→Video namespace dependency.
            _ppuTickHook?.Invoke(cpuCycles);
        }

        // --- PPU register hooks --------------------------------------------
        // Bus dispatches $2000-$2007 reads/writes through caller-supplied
        // delegates. Default stubs return open bus / drop. AprNes.Cli
        // attaches NesPpu via BindPpu() at startup. This decoupling keeps
        // the bus oblivious to the rendering side and lets tests run with
        // a no-op PPU when only CPU correctness is being verified
        // (e.g. NestestOracleTests).

        private Func<ushort, byte>? _ppuReadHook;
        private Action<ushort, byte>? _ppuWriteHook;
        private Action<int>? _ppuTickHook;

        public void BindPpu(
            Func<ushort, byte> readReg,
            Action<ushort, byte> writeReg,
            Action<int>? tick = null)
        {
            _ppuReadHook  = readReg;
            _ppuWriteHook = writeReg;
            _ppuTickHook  = tick;
        }

        private byte ReadPpu(ushort addr)
        {
            if (_ppuReadHook is { } hook) return hook(addr);
            // Fallback: open-bus / write-only-register defaults.
            return addr switch
            {
                0x2002 => 0,      // PPUSTATUS — would be VBL flag etc.
                0x2004 => 0,      // OAMDATA
                0x2007 => 0,      // PPUDATA buffered read
                _      => _cpubus // write-only registers return last bus value
            };
        }

        private void WritePpu(ushort addr, byte value)
        {
            _ppuWriteHook?.Invoke(addr, value);
            // No-op fallback when no PPU bound; _cpubus already updated.
            _ = addr; _ = value;
        }

        // --- APU + IO register stubs ---------------------------------------

        private byte ReadApu(ushort addr)
        {
            switch (addr)
            {
                case 0x4015: return 0;        // TODO: APU status
                case 0x4016: return 0;        // TODO: joypad 1
                case 0x4017: return 0;        // TODO: joypad 2 / frame counter status
                default:     return _cpubus;  // open bus on write-only / unmapped
            }
        }

        private void WriteApu(ushort addr, byte value)
        {
            if (addr == 0x4014)
            {
                OamDma(value);
                return;
            }
            // TODO: wire APU — $4000-$4013 channel regs, $4015 enables,
            // $4016 joypad strobe, $4017 frame counter mode.
            _ = value;
        }

        // --- OAM DMA --------------------------------------------------------
        // $4014 write: copy 256 bytes from CPU $XX00-$XXFF into OAM, starting
        // at the current OAMADDR (_oamDmaWritePtr, which wraps mod-256). Real
        // hardware halts the CPU for 513 cycles (1 dummy + 256 read + 256
        // write); we surface that via _pendingStallCycles.
        //
        // fixex 2017.01.16 pass sprite_ram test
        private void OamDma(byte page)
        {
            int oamAddress = page << 8;
            for (int i = 0; i < 256; i++)
            {
                _oam[_oamDmaWritePtr++] = ReadByte((ushort)(oamAddress++));
            }
            // OAM DMA: 1 dummy cycle (halt) + 256 × 2 (read/write) = 513 cycles
            // On real NES, odd-cycle start adds 1 more (514), but 513 is the base.
            _pendingStallCycles += 513;
        }

        // --- Null mapper (placeholder when no cartridge is loaded) ----------

        private sealed class NullMapper : IMapper
        {
            public void Reset(byte[] prgRom, byte[] chrRom) { }
            public byte CpuRead(ushort addr) => 0;
            public void CpuWrite(ushort addr, byte value) { }
            public byte PpuRead(ushort addr) => 0;
            public void PpuWrite(ushort addr, byte value) { }
        }
    }
}
