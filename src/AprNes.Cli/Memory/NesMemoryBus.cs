// NES memory bus — ported from erspicu/AprNes commit fcbbb23.
//
// Routes CPU bus accesses to:
// - $0000-$1FFF: 2KB internal WRAM (mirrored)
// - $2000-$3FFF: PPU registers ($2000-$2007 mirrored)
// - $4000-$4017: APU + IO registers (joypad, OAM DMA, frame counter)
// - $4020-$FFFF: cartridge (delegated to IMapper)
//
// N3.2 — region boundaries are now driven by spec/machines/nes-ntsc.json
// (loaded into MachineSpec at construction). Side-effect handlers
// (PPU/APU/mapper) stay C# per design doc #21 §2.2 — those are
// procedure-heavy and don't reduce well to declarative form. The
// migration replaces the hardcoded if-else chain with a sorted region
// table + RegionKind dispatch; correctness preserved, declarativity for
// memory layout up.
//
// PPU/APU register handling is stubbed — wire to NesPpu / NesApu when
// those are added. OAM DMA ($4014) timing is preserved (513-cycle stall).

using System.IO;
using System.Runtime.CompilerServices;
using AprCpu.Core.JsonSpec;

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

        // N1.B' B'.5b — block-JIT SMC notification hook. Fired on every
        // CPU bus write so the JsonCpu's BlockCache can invalidate any
        // cached block whose coverage range includes the address.
        // Default null = no-op (per-instr / legacy backends don't need it).
        // NesJsonCpu wires this when block-JIT is enabled.
        public Action<uint>? SmcWriteHook { get; set; }

        // Accumulated CPU cycles waiting to be flushed to the PPU/APU. The
        // framework calls Tick() per instruction; once PPU/APU are wired the
        // catch-up loop will consume this.
        private int _pendingCatchUpCycles;

        private IMapper _mapper = new NullMapper();

        // N3.2 — sorted region dispatch table built from MachineSpec.
        // Each region maps an addr range to a RegionKind that the
        // ReadByte/WriteByte hot path switches on. Region boundaries +
        // mirror masks come from spec; side-effect handlers stay C#
        // (PPU/APU/mapper logic doesn't reduce to declarative form).
        private RegionEntry[] _regions = Array.Empty<RegionEntry>();
        private MachineSpec? _machineSpec;

        private struct RegionEntry
        {
            public int Start;            // CPU bus addr — int so end can hit 0x10000
            public int End;              // exclusive (0x10000 for cart_prg ending at top)
            public ushort MirrorMask;   // 0 = no mirror
            public RegionKind Kind;
            public bool SmcNotify;
        }

        private enum RegionKind : byte
        {
            Unmapped,
            Wram,
            PpuIo,
            ApuIo,
            CartIo,    // mapper.CpuRead / CpuWrite
        }

        public NesMemoryBus()
        {
            BuildRegionTableFromSpec();
        }

        public NesMemoryBus(IMapper mapper)
        {
            BuildRegionTableFromSpec();
            Reset(mapper);
        }

        /// <summary>
        /// N3.2 — try loading <c>spec/machines/nes-ntsc.json</c> and build
        /// the region dispatch table from it. Falls back to the canonical
        /// hardcoded NES layout if the spec file is absent (so existing
        /// test harnesses + test ROMs without a machine spec keep working).
        /// </summary>
        private void BuildRegionTableFromSpec()
        {
            _machineSpec = TryLoadMachineSpec("nes-ntsc");
            if (_machineSpec is null)
            {
                // Fallback to canonical NES layout — same shape the spec
                // would have produced; used when no machine spec is found.
                _regions = new[]
                {
                    new RegionEntry { Start = 0x0000, End = 0x2000,  MirrorMask = 0x07FF, Kind = RegionKind.Wram,   SmcNotify = true  },
                    new RegionEntry { Start = 0x2000, End = 0x4000,  MirrorMask = 0x0007, Kind = RegionKind.PpuIo,  SmcNotify = false },
                    new RegionEntry { Start = 0x4000, End = 0x4020,  MirrorMask = 0x0000, Kind = RegionKind.ApuIo,  SmcNotify = false },
                    new RegionEntry { Start = 0x4020, End = 0x10000, MirrorMask = 0x0000, Kind = RegionKind.CartIo, SmcNotify = false },
                };
                return;
            }

            var list = new List<RegionEntry>();
            foreach (var r in _machineSpec.MemoryRegions)
            {
                list.Add(new RegionEntry
                {
                    Start = (int)r.AddrStart,
                    End = (int)Math.Min(r.AddrEndExclusive, 0x10000u),
                    MirrorMask = (ushort)(r.MirrorMask ?? 0u),
                    Kind = ClassifyRegion(r),
                    SmcNotify = r.SmcNotify,
                });
            }
            // Sort by Start so ReadByte/WriteByte can binary-search (or just
            // linear-scan since N is small).
            list.Sort((a, b) => a.Start.CompareTo(b.Start));
            _regions = list.ToArray();
        }

        private static RegionKind ClassifyRegion(MemoryRegion r)
        {
            // Map MachineSpec metadata to dispatch kind. Order matters —
            // side_effects[0] is the primary subsystem; we route based on
            // the first matching name.
            foreach (var s in r.SideEffects)
            {
                if (s == "ppu") return RegionKind.PpuIo;
                if (s == "apu" || s == "oam_dma" || s == "joypad") return RegionKind.ApuIo;
                if (s == "mapper") return RegionKind.CartIo;
            }
            // No side_effects → infer from type. ram → Wram (NES has only one
            // RAM region in the CPU bus); rom → CartIo (mapper handles ROM
            // via PRG bank lookup).
            return r.Kind switch
            {
                MemoryRegionKind.Ram => RegionKind.Wram,
                MemoryRegionKind.Rom => RegionKind.CartIo,
                _ => RegionKind.Unmapped,
            };
        }

        private static MachineSpec? TryLoadMachineSpec(string name)
        {
            var dir = AppContext.BaseDirectory;
            for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
            {
                var probe = Path.Combine(d.FullName, "spec", "machines", $"{name}.json");
                if (File.Exists(probe)) return MachineSpecLoader.LoadFromFile(probe);
            }
            var cwdProbe = Path.Combine(Environment.CurrentDirectory, "spec", "machines", $"{name}.json");
            return File.Exists(cwdProbe) ? MachineSpecLoader.LoadFromFile(cwdProbe) : null;
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
            // N3.2 — sorted region table dispatch; linear scan is fine for
            // ~4-11 regions and keeps tight branch-predictor-friendly code.
            // mirror_mask=0 means no mirror; use raw addr for handler call.
            // mirror_mask>0 means handler sees `(addr & mirror_mask) | start`.
            byte val = 0;
            for (int i = 0; i < _regions.Length; i++)
            {
                ref var r = ref _regions[i];
                if (addr < r.End)
                {
                    if (addr < r.Start) break;   // sorted; gap = unmapped
                    val = r.Kind switch
                    {
                        RegionKind.Wram   => _wram[(addr - r.Start) & (r.MirrorMask != 0 ? r.MirrorMask : (ushort)0xFFFF)],
                        RegionKind.PpuIo  => ReadPpu((ushort)(r.Start | (addr & (r.MirrorMask != 0 ? r.MirrorMask : (ushort)0x0007)))),
                        RegionKind.ApuIo  => ReadApu(addr),
                        RegionKind.CartIo => _mapper.CpuRead(addr),
                        _                 => _cpubus,
                    };
                    break;
                }
            }
            _cpubus = val;
            return val;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteByte(ushort addr, byte value)
        {
            _cpubus = value;
            // N1.B' SMC notify — BlockCache.NotifyMemoryWrite is a 1-byte
            // coverage-counter check + early-return; cheap on hot path.
            // Only fires invalidation for addresses where a cached block
            // actually has coverage. Routes RAM-resident SMC (blargg's
            // instr_template self-rewrite) to JIT cache invalidation.
            SmcWriteHook?.Invoke(addr);

            for (int i = 0; i < _regions.Length; i++)
            {
                ref var r = ref _regions[i];
                if (addr < r.End)
                {
                    if (addr < r.Start) break;
                    switch (r.Kind)
                    {
                        case RegionKind.Wram:
                            _wram[(addr - r.Start) & (r.MirrorMask != 0 ? r.MirrorMask : (ushort)0xFFFF)] = value;
                            break;
                        case RegionKind.PpuIo:
                            WritePpu((ushort)(r.Start | (addr & (r.MirrorMask != 0 ? r.MirrorMask : (ushort)0x0007))), value);
                            break;
                        case RegionKind.ApuIo:
                            WriteApu(addr, value);
                            break;
                        case RegionKind.CartIo:
                            _mapper.CpuWrite(addr, value);
                            break;
                    }
                    break;
                }
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
            // SMC notify — same rationale as WriteByte. zp writes are common
            // (stack pushes route to $0100|SP via WriteByte though, not here)
            // but block-JIT-cached blocks rarely live in zp, so the coverage
            // counter early-returns ~always.
            SmcWriteHook?.Invoke(addr);
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
        private Func<bool>? _ppuConsumeNmiHook;

        public void BindPpu(
            Func<ushort, byte> readReg,
            Action<ushort, byte> writeReg,
            Action<int>? tick = null,
            Func<bool>? consumeNmi = null)
        {
            _ppuReadHook  = readReg;
            _ppuWriteHook = writeReg;
            _ppuTickHook  = tick;
            _ppuConsumeNmiHook = consumeNmi;
        }

        /// <summary>Atomically read-and-clear PPU NMI line. Returns true if
        /// VBlank NMI is pending and should be serviced before the next
        /// instruction.</summary>
        public bool ConsumePpuNmi() => _ppuConsumeNmiHook?.Invoke() ?? false;

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
            public Action<int>? MirroringChanged { get; set; }
            public Action<uint, uint>? PrgBankSwitched { get; set; }
        }
    }
}
