# Memory spec v2 — handler registry + offset semantics + page-table dispatch

> **Status (last updated 2026-05-09)**: N4 series fully ✅ complete (N4.0-N4.6 + follow-ups
> N7/N8 + N3.3-finally). This doc describes schema v2 + dispatch v2, incorporating
> the 7 critique points from the Gemini consultation (record at
> `tools/knowledgebase/message/20260509_181033.txt`).
>
> **Goals achieved**: framework declarative ratio pushed from ~70% (N3) to ~85%
> (exceeding the original 80% target); perf recovered the N3.2 7% regression,
> leaving only ~3% (legacy) / on par (per-instruction) / +5% ahead (block-JIT)
> vs. the N1 baseline. The schema also paves the way for a 4th CPU
> (GBA/ARM).
>
> **N5/N7/N8 follow-ups**: items mentioned in N4 closeout §3 — fastmem TryGetHostPointer,
> allowed_widths runtime enforcement, and ARM 2-level page table — have all shipped
> (N7 query API, N10 GBA debug enforcement, N8 GBA 1-level page table; ARM 2-level
> remains future work since the N8 1-level approach already covers the GBA target).

---

## 1. v1 → v2 schema diff (overview)

| Field | v1 (N2/N3) | v2 (N4) | Reason |
|---|---|---|---|
| `side_effects: ["ppu", ...]` | implicit routing: `[0]` decides dispatch | **changed to explicit `handler: "ppu"`**; `observers` carries metadata | side_effects[0] magic is a hidden semantic rule (#6) |
| `mirror_mask: 0x2007` | applied to absolute addr `(addr & mask) \| start` | applied to offset `(addr - start) & mask` | brittle — only works for power-of-2 aligned regions (#2) |
| `fastmem_eligible: bool` | JIT optimization hint baked into spec | **removed**; JIT goes through `bus.TryGetHostPointer(addr)` query | spec should not dictate JIT optimization (#5) |
| `forces_end_of_block: bool` | JIT-specific concept | **changed to `volatile: bool`**; the CPU JIT decides whether a volatile region ends a block | a JIT concept is not a hardware fact (#7) |
| (none) | — | **`handler: "ppu"`** (string) | explicit routing key |
| (none) | — | **`allowed_widths: [8, 16, 32]`** | GBA SRAM 16-bit truncation, VRAM 8-bit mirror — critical (#7) |
| (none) | — | **`readable: bool` / `writable: bool`** | replaces `fastmem_eligible`; explicit ROM read-only |
| (none) | — | **`wait_states: { seq, nonseq }`** (optional) | future-proof GBA cart timing |
| machine root: (none) | — | **`unmapped_behavior: zero\|ignore\|fault\|last_bus_value`** | ARM/m68k have gaps; NES does not (#3) |

**Backwards compat**: N4 still parses v1 fields; the existing machine specs (nes-ntsc / gba / gb-dmg)
have all been upgraded to v2 fields in N4.5, but parsing v1 still works — the build is not broken.
Removal of v1 fields is left for the future (no forcing function; all current specs are v2).

---

## 2. v2 schema example (NES NTSC)

```json
{
    "$schema": "../schema/machine-spec.schema.json",
    "name": "nes-ntsc",
    "cpu": "Ricoh2A03",
    "spec_version": "2.0",
    "unmapped_behavior": "zero",
    "memory_regions": [
        {
            "name": "wram",
            "addr_start": "0x0000",
            "addr_end_exclusive": "0x2000",
            "type": "ram",
            "handler": "wram",
            "mirror_mask": "0x07FF",
            "readable": true,
            "writable": true,
            "allowed_widths": [8],
            "smc_notify": true
        },
        {
            "name": "ppu_io",
            "addr_start": "0x2000",
            "addr_end_exclusive": "0x4000",
            "type": "io",
            "handler": "ppu",
            "mirror_mask": "0x0007",
            "allowed_widths": [8],
            "volatile": true
        },
        {
            "name": "apu_io",
            "addr_start": "0x4000",
            "addr_end_exclusive": "0x4020",
            "type": "io",
            "handler": "apu",
            "allowed_widths": [8],
            "volatile": true,
            "observers": ["oam_dma", "joypad"]
        },
        {
            "name": "cart_prg",
            "addr_start": "0x4020",
            "addr_end_exclusive": "0x10000",
            "type": "io",
            "handler": "mapper",
            "allowed_widths": [8],
            "volatile": true
        }
    ],
    "interrupt_vectors": { "nmi": "0xFFFA", "reset": "0xFFFC", "irq": "0xFFFE" }
}
```

Note that `mirror_mask: "0x0007"` is cleaner than v1's `"0x2007"` (only the low 3 bits are kept,
no longer mixed with region-start bits) — because v2 applies the mask to an offset, not to the absolute address.

---

## 3. Dispatch rewrite (v2)

### 3.1 Handler registry

```csharp
// Components register at startup
busBuilder.RegisterHandler("wram",   _wramRead,   _wramWrite);
busBuilder.RegisterHandler("ppu",    ppu.Read,    ppu.Write);    // Note: receives offset, not absolute addr
busBuilder.RegisterHandler("apu",    apu.Read,    apu.Write);
busBuilder.RegisterHandler("mapper", mapper.CpuRead, mapper.CpuWrite);

// While parsing MachineSpec, each region's handler string is looked up in the registry to bind the delegate
busBuilder.Build();   // After build, every entry in the region table has (delegate, mirror_mask, ...)
```

Adding a new subsystem: add a region to the spec with `handler: "<new_name>"`, and the subsystem class calls `RegisterHandler("<new_name>", ...)` at startup. **The bus core code does not need to change at all**.

### 3.2 Offset-based mirror + handlers receive offset

```csharp
// Handler signature changed to (offset, value) — components no longer care about absolute addr
public delegate byte ReadHandler(int offset);
public delegate void WriteHandler(int offset, byte value);

// Bus dispatch
ref var page = ref _pageTable[addr >> _pageShift];
int offset = (addr - page.RegionStart) & (page.MirrorMask | someAllOnesMask);
return page.Reader(offset);
```

NesPpu changes: it used to take `WriteRegister(0x2000-0x2007 abs addr)`, now it takes `WriteRegister(0-7 offset)`.

NesPpu / NesApu's existing register handlers (PPUCTRL/PPUMASK/...) are cleaner once switched to offsets — no more `addr & 0x7` clutter.

### 3.3 Page-table O(1) lookup

```csharp
private struct PageEntry {
    public ReadHandler Reader;
    public WriteHandler Writer;
    public ushort RegionStart;
    public ushort MirrorMask;
    public bool SmcNotify;
    public bool Volatile;
}

private PageEntry[] _pageTable; // 64 entries for 16-bit addr space (1KB pages)

[MethodImpl(MethodImplOptions.AggressiveInlining)]
public byte ReadByte(ushort addr) {
    ref var p = ref _pageTable[addr >> 10];
    int offset = (addr - p.RegionStart) & p.MirrorMask;
    return p.Reader(offset);
}
```

NES 16-bit / 1KB-page → 64 entries × ~32 bytes = **~2KB memory**.
ARM 32-bit / 4KB-page → 1M entries × 32 bytes = 32MB (too big) → needs a 2-level page table (top 8 bits → 256 entries; bottom 12 bits → 4096 entries per sub-table; on average 4-8 sub-tables in use → ~128KB). **N4 only does NES; the 2-level for ARM is left for the future**.

Build cost: at spec load time, walk the regions and set entries for each page. One-shot ~10μs.

Hot-path cost: 1 array index + 1 add + 1 and + 1 indirect call ≈ ~5-10ns. Faster than v1's linear scan + switch, even faster than the hardcoded if-else (no branches; the indirect call to a hot delegate can be inlined by the JIT).

### 3.4 fastmem moved to a query API

```csharp
// Spec no longer has a fastmem_eligible field
public bool TryGetHostPointer(ushort addr, out byte[] hostArray, out int offset) {
    ref var p = ref _pageTable[addr >> 10];
    if (p.HostArray is { } arr) {
        hostArray = arr;
        offset = (addr - p.RegionStart) & p.MirrorMask;
        return true;
    }
    hostArray = null!;
    offset = 0;
    return false;
}
```

A block-JIT emitter that wants to inline a GEP-store queries `bus.TryGetHostPointer(addr)`, gets a host pointer + offset, and emits an inline store. On miss it falls back to an extern call. **The spec has no idea what fastmem is**.

---

## 4. Out of N4 scope (follow-up status)

| Original "future" item | Actual follow-up | Status |
|---|---|---|
| ARM 32-bit 2-level page table (Gemini #4) | **N8** GBA goes through a 1-level page table (256 entries × 16 MB pages, sufficient for GBA) | ✅ 1-level done; 2-level only when there is a real sub-region need |
| `wait_states` runtime enforcement (Gemini #7-add) | **N7** GetWaitStates query API + **N10** schema declared | partial — query API is in place; NES has no wait states; GBA does not enforce timing yet |
| fastmem `TryGetHostPointer` query API (§3.4) | **N7** API shipped + **N11** 6502 inline GEP-load (opt-in) | ✅ done; perf-neutral on 6502, waiting for other CPUs to benefit |
| spec format `extra_when_taken` / `extra_when_page_cross` (dynamic cycle penalties) | **N9** added top-level field and annotated branches | ✅ done (per-mode page-cross left for the future) |
| `allowed_widths` runtime enforcement | **N10** GbaMemoryBus debug-mode flag | ✅ done (debug-mode does not affect release perf) |

---

## 5. Migration plan (N4.0 - N4.6) ✅ all complete

| Step | Content | Commit |
|---|---|---|
| **N4.0** | This doc lands | (init commit of this doc) |
| **N4.1** | Schema v2 fields + parser; nes-ntsc.json upgraded to v2 fields | `914cda0` |
| **N4.2** | Handler registry; ClassifyRegion uses the v2 explicit Handler | `81103d8` |
| **N4.3** | Page-table O(1) dispatch (32-byte pages, 2048 entries) replaces linear scan | `61066a2` |
| **N4.4** | offset semantics — handlers receive a region-local offset | `101ae98` |
| **N4.5** | spec/machines/gba.json + gb-dmg.json upgraded to v2 fields | `abad579` |
| **N4.6** | 3-run perf bench + perf doc + N3.4 invalidations | `4367154` |

One commit per step, push, then the next. **5-minute timeout cap on tests**.

Detailed perf data: `MD/performance/202605091900-n4-memory-spec-v2.md`

---

## 6. References

- Gemini consultation (2026-05-09): `tools/knowledgebase/message/20260509_181033.txt`
- Earlier Gemini consultation (2026-05-09, earlier the same day): `tools/knowledgebase/message/20260509_163828.txt`
- Doc #19 — declarative JIT policy + CPU/Machine split
- Doc #21 — spec-driven runtime (incl. N3.2/N3.3 conclusions)
