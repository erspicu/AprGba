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
        //
        // N4.3 — _regions is now build-time scaffolding only; hot-path
        // dispatch uses _pageTable[addr >> PageShift] for O(1) lookup.
        private RegionEntry[] _regions = Array.Empty<RegionEntry>();
        private MachineSpec? _machineSpec;

        // N4.3 — 32-byte page table for O(1) region dispatch. PageShift=5
        // is the largest page size that doesn't split any NES region:
        // smallest region (apu_io) is 32 bytes wide and starts at 0x4000,
        // cart_prg starts at 0x4020 — both 32-byte aligned. 0x10000/32 =
        // 2048 entries × 8 bytes = 16KB scratch, built once at boot.
        private const int PageShift = 5;
        private const int PageCount = 0x10000 >> PageShift;   // 2048

        private PageEntry[] _pageTable = new PageEntry[PageCount];

        private struct PageEntry
        {
            public RegionKind Kind;          // 1 byte (default Unmapped = 0)
            public bool SmcNotify;           // 1 byte
            public byte AllowedWidthsMask;   // 1 byte — N7: bit 0 = 8-bit,
                                              //   bit 1 = 16-bit, bit 2 = 32-bit;
                                              //   0 = unspecified (any width OK).
            public byte _pad;                // 1 byte explicit padding
            public ushort Start;             // 2 bytes — region start (always < 0x10000)
            public ushort MirrorMask;        // 2 bytes — 0 = no mirror
            // 8-byte struct (1+1+1+1+2+2); cache-line friendly.
        }

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

            // N4.3 — populate the page table from the now-finalised region
            // list. Done once at boot; hot path then dispatches in O(1).
            BuildPageTable();
        }

        /// <summary>
        /// N4.3 — populate <see cref="_pageTable"/> from <see cref="_regions"/>.
        /// For each 32-byte page, find the region whose [Start, End) covers
        /// the page's start address; copy that region's Kind / Start /
        /// MirrorMask / SmcNotify into the page entry. Pages with no
        /// covering region default to <see cref="RegionKind.Unmapped"/>.
        /// </summary>
        private void BuildPageTable()
        {
            // Reset to all-Unmapped (default for the struct).
            Array.Clear(_pageTable, 0, _pageTable.Length);

            // N7 — read allowed_widths from spec for IsAccessWidthAllowed
            // queries. NES is uniformly 8-bit so most NES specs declare
            // [8]; we still parse it generically so the same code can be
            // lifted for GBA / GB-DMG.
            for (int p = 0; p < PageCount; p++)
            {
                int pageAddr = p << PageShift;
                for (int i = 0; i < _regions.Length; i++)
                {
                    ref var r = ref _regions[i];
                    if (pageAddr >= r.Start && pageAddr < r.End)
                    {
                        // Default mask 0 = any width. If the spec declares
                        // allowed_widths, encode them: 8→bit0, 16→bit1, 32→bit2.
                        byte widthMask = 0;
                        var specRegion = FindSpecRegionByStart(r.Start);
                        if (specRegion?.AllowedWidths is { } widths)
                        {
                            foreach (var w in widths)
                            {
                                widthMask |= w switch
                                {
                                    8 => (byte)0x01,
                                    16 => (byte)0x02,
                                    32 => (byte)0x04,
                                    _ => (byte)0x00,
                                };
                            }
                        }
                        _pageTable[p] = new PageEntry
                        {
                            Kind = r.Kind,
                            SmcNotify = r.SmcNotify,
                            AllowedWidthsMask = widthMask,
                            Start = (ushort)r.Start,
                            MirrorMask = r.MirrorMask,
                        };
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// N7 — locate the original MachineSpec.MemoryRegion entry that
        /// corresponds to a runtime RegionEntry. Used at boot to look up
        /// v2 fields (allowed_widths, wait_states) that the runtime
        /// RegionEntry doesn't carry. O(N) over <see cref="_regions"/>; only
        /// called during table build, not on hot path.
        /// </summary>
        private MemoryRegion? FindSpecRegionByStart(int start)
        {
            if (_machineSpec is null) return null;
            foreach (var sr in _machineSpec.MemoryRegions)
            {
                if ((int)sr.AddrStart == start) return sr;
            }
            return null;
        }

        private static RegionKind ClassifyRegion(MemoryRegion r)
        {
            // N4.2 — prefer v2 explicit Handler field over v1 side_effects[0]
            // implicit-routing magic. Resolve via the bus's handler registry
            // (which currently maps known names to internal RegionKind enum
            // values; future N4.3 will switch to delegate-based dispatch).
            var routingKey = r.Handler ?? (r.SideEffects.Count > 0 ? r.SideEffects[0] : null);
            switch (routingKey)
            {
                case "wram":   return RegionKind.Wram;
                case "ppu":    return RegionKind.PpuIo;
                case "apu":
                case "oam_dma":
                case "joypad": return RegionKind.ApuIo;
                case "mapper": return RegionKind.CartIo;
            }
            // No handler / known routing → infer from type. ram → Wram (NES
            // has only one RAM region in the CPU bus); rom → CartIo (mapper
            // handles ROM via PRG bank lookup).
            return r.Kind switch
            {
                MemoryRegionKind.Ram => RegionKind.Wram,
                MemoryRegionKind.Rom => RegionKind.CartIo,
                _ => RegionKind.Unmapped,
            };
        }

        /// <summary>
        /// N4.2 — handler registry stub. Future N4.3 will use this to
        /// drive delegate-based dispatch; for now the bus internally
        /// routes via the RegionKind enum + switch. Components can call
        /// <see cref="RegisterHandler"/> at boot to declare their
        /// intent, but those registrations are only consulted by the
        /// upcoming page-table dispatch.
        /// </summary>
        private readonly Dictionary<string, (Func<ushort, byte> Reader, Action<ushort, byte> Writer)> _handlers
            = new(StringComparer.Ordinal);

        /// <summary>
        /// Register a named read/write handler pair. Components like NesPpu,
        /// NesApu, or a custom mapper subsystem call this at boot to declare
        /// the routing key referenced from <c>spec/machines/*.json</c>'s
        /// <c>handler</c> field.
        /// </summary>
        public void RegisterHandler(string name, Func<ushort, byte> reader, Action<ushort, byte> writer)
        {
            _handlers[name] = (reader, writer);
        }

        /// <summary>True if a handler with this name is registered.</summary>
        public bool HasHandler(string name) => _handlers.ContainsKey(name);

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

        // === Phase 30.17b — Verifier framework integration ====================
        //
        // Expose internal bus state (open-bus latch, OAM-DMA pointer, pending-
        // cycle counters) so the Verified Block-JIT framework can capture +
        // restore it between JIT-vs-interp passes. Without these, reads from
        // PPU/APU/unmapped regions return whichever env's _cpubus happens
        // to be — causing spurious divergences after block #1 when the two
        // envs' _cpubus values drift apart (each env updates its own latch
        // independently after the snapshot restores RAM only).

        public byte InternalCpuBus { get => _cpubus; set => _cpubus = value; }
        public byte InternalOamDmaWritePtr { get => _oamDmaWritePtr; set => _oamDmaWritePtr = value; }
        public int  InternalPendingStallCycles { get => _pendingStallCycles; set => _pendingStallCycles = value; }
        public int  InternalPendingCatchUpCycles { get => _pendingCatchUpCycles; set => _pendingCatchUpCycles = value; }

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

        // --- N7 query APIs (spec-declared values, not yet enforced on hot path) ----

        /// <summary>
        /// N7.1 — fastmem query: if <paramref name="addr"/> resolves to a
        /// host-array-backed region (currently only the 2KB internal WRAM),
        /// return the underlying byte[] + the offset within it. Block-JIT
        /// emitters can use this to inline a GEP-store and skip the bus
        /// dispatch entirely.
        ///
        /// Returns false for IO regions (PPU/APU registers — side effects)
        /// and for the cart_prg region (mapper bank-switching means no
        /// stable host pointer).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetHostPointer(ushort addr, out byte[]? hostArray, out int offset)
        {
            ref var p = ref _pageTable[addr >> PageShift];
            if (p.Kind == RegionKind.Wram)
            {
                offset = (addr - p.Start) & (p.MirrorMask != 0 ? p.MirrorMask : (ushort)0xFFFF);
                hostArray = _wram;
                return true;
            }
            hostArray = null;
            offset = 0;
            return false;
        }

        /// <summary>
        /// N7.2 — query whether <paramref name="widthBits"/> (8 / 16 / 32)
        /// is permitted at <paramref name="addr"/> per the spec's
        /// <c>allowed_widths</c>. NES regions all declare [8] so non-8-bit
        /// queries return false; if the spec doesn't declare allowed_widths
        /// for a region, returns true (no constraint).
        ///
        /// Not enforced on hot path — callers (tests, future width-checking
        /// debug mode, GBA halfword-write splitters) opt in.
        /// </summary>
        public bool IsAccessWidthAllowed(ushort addr, int widthBits)
        {
            ref var p = ref _pageTable[addr >> PageShift];
            if (p.AllowedWidthsMask == 0) return true;     // unspecified → any
            byte bit = widthBits switch
            {
                8  => 0x01,
                16 => 0x02,
                32 => 0x04,
                _  => 0x00,
            };
            return (p.AllowedWidthsMask & bit) != 0;
        }

        /// <summary>
        /// N7.3 — placeholder for spec-declared wait states. NES has none
        /// (returns 0/0). GBA's bus will override this when N8 lands —
        /// cart_rom + EWRAM declare wait_states in spec/machines/gba.json
        /// already (per N4.5).
        /// </summary>
        public (int seq, int nonseq) GetWaitStates(ushort addr)
        {
            _ = addr;
            return (0, 0);
        }

        // --- CPU bus dispatch ----------------------------------------------

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte ReadByte(ushort addr)
        {
            // N4.3 — O(1) page-table dispatch.
            // N4.4 — handlers receive an OFFSET (region-local addr), not the
            // absolute CPU bus addr. offset = (addr - region.Start) & mirror.
            // Mapper is the one exception — mappers everywhere assume an
            // absolute addr ($8000+, etc.) so we keep that convention.
            ref var p = ref _pageTable[addr >> PageShift];
            ushort offset = (ushort)((addr - p.Start) & (p.MirrorMask != 0 ? p.MirrorMask : (ushort)0xFFFF));
            byte val = p.Kind switch
            {
                RegionKind.Wram   => _wram[offset],
                RegionKind.PpuIo  => ReadPpu(offset),
                RegionKind.ApuIo  => ReadApu(offset),
                RegionKind.CartIo => _mapper.CpuRead(addr),     // mapper: absolute addr (convention)
                _                 => _cpubus,
            };
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

            ref var p = ref _pageTable[addr >> PageShift];
            ushort offset = (ushort)((addr - p.Start) & (p.MirrorMask != 0 ? p.MirrorMask : (ushort)0xFFFF));
            switch (p.Kind)
            {
                case RegionKind.Wram:
                    _wram[offset] = value;
                    break;
                case RegionKind.PpuIo:
                    WritePpu(offset, value);
                    break;
                case RegionKind.ApuIo:
                    WriteApu(offset, value);
                    break;
                case RegionKind.CartIo:
                    _mapper.CpuWrite(addr, value);              // mapper: absolute addr (convention)
                    break;
                // RegionKind.Unmapped: drop write (open-bus latch already updated above)
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

        // N4.4 — ReadPpu / WritePpu now take a region-local OFFSET (0-7 after
        // the bus's mirror_mask is applied). The bound NesPpu handles the
        // routing — its ReadRegister still applies `& 7` defensively (cheap).
        private byte ReadPpu(ushort offset)
        {
            if (_ppuReadHook is { } hook) return hook(offset);
            // Fallback: open-bus / write-only-register defaults — offsets
            // are PPU-register-local (0-7), not absolute $2000+ addrs.
            return offset switch
            {
                0x02 => 0,        // PPUSTATUS ($2002) — VBL flag etc.
                0x04 => 0,        // OAMDATA   ($2004)
                0x07 => 0,        // PPUDATA   ($2007) buffered read
                _    => _cpubus   // write-only registers return last bus value
            };
        }

        private void WritePpu(ushort offset, byte value)
        {
            _ppuWriteHook?.Invoke(offset, value);
            // No-op fallback when no PPU bound; _cpubus already updated.
            _ = offset; _ = value;
        }

        // --- APU + IO register stubs ---------------------------------------
        // N4.4 — handlers receive a region-local OFFSET, not absolute addr.
        // For apu_io ($4000-$401F, mirror_mask=0) offset = addr - 0x4000,
        // so $4014 → 0x14, $4015 → 0x15, $4016 → 0x16, $4017 → 0x17.

        private byte ReadApu(ushort offset)
        {
            switch (offset)
            {
                case 0x15: return 0;          // TODO: APU status ($4015)
                case 0x16: return 0;          // TODO: joypad 1 ($4016)
                case 0x17: return 0;          // TODO: joypad 2 / frame counter ($4017)
                default:   return _cpubus;    // open bus on write-only / unmapped
            }
        }

        private void WriteApu(ushort offset, byte value)
        {
            if (offset == 0x14)               // $4014 OAM DMA
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
