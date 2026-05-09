# Memory spec v2 — handler registry + offset semantics + page-table dispatch

> **Status (2026-05-09 末更新)**：N4 系列全部 ✅ 完成（N4.0-N4.6 + 後續
> N7/N8 follow-ups + N3.3-finally）。本 doc 描述 schema v2 + dispatch v2，
> 吸收 Gemini 諮詢的 7 點 critique（紀錄
> `tools/knowledgebase/message/20260509_181033.txt`）。
>
> **目標達成**：framework declarative ratio 從 N3 的 ~70% 推到 ~85%
> （超過原本 80% 目標）；perf 救回 N3.2 7% 退步、距 N1 baseline 只剩
> ~3%（legacy）/ 持平（per-instr）/ 反超 5%（block-JIT）。schema 為
> GBA/ARM 加第 4 個 CPU 鋪好路。
>
> **N5/N7/N8 follow-up**：N4 closeout §3 提的 fastmem TryGetHostPointer
> + allowed_widths runtime enforce + ARM 2-level page table 都已 ship
> （N7 query API、N10 GBA debug enforce、N8 GBA 1-level page table；
> ARM 2-level 留 future，N8 1-level 已涵蓋 GBA target）。

---

## 1. v1 → v2 schema diff（總覽）

| Field | v1 (N2/N3) | v2 (N4) | 原因 |
|---|---|---|---|
| `side_effects: ["ppu", ...]` | 隱含 routing：`[0]` 決定 dispatch | **改 `handler: "ppu"` 顯式**；`observers` 收 metadata | side_effects[0] magic 是 hidden semantic rule (#6) |
| `mirror_mask: 0x2007` | 套絕對 addr `(addr & mask) \| start` | 套 offset `(addr - start) & mask` | brittle — 只對 power-of-2 aligned regions work (#2) |
| `fastmem_eligible: bool` | JIT 優化 hint 寫進 spec | **刪除**；JIT 走 `bus.TryGetHostPointer(addr)` query | spec 不該 dictate JIT optimization (#5) |
| `forces_end_of_block: bool` | JIT-specific concept | **改 `volatile: bool`**；CPU JIT 自行決定 volatile 要不要結束 block | JIT 概念不是硬體事實 (#7) |
| (none) | — | **`handler: "ppu"`** (string) | explicit routing key |
| (none) | — | **`allowed_widths: [8, 16, 32]`** | GBA SRAM 16-bit 截斷、VRAM 8-bit mirror — critical (#7) |
| (none) | — | **`readable: bool` / `writable: bool`** | 取代 `fastmem_eligible`、明確 ROM read-only |
| (none) | — | **`wait_states: { seq, nonseq }`** (optional) | future-proof GBA cart timing |
| machine root: (none) | — | **`unmapped_behavior: zero\|ignore\|fault\|last_bus_value`** | ARM/m68k 有 gaps；NES 沒 (#3) |

**Backwards compat**：N4 解析 v1 fields；existing machine specs（nes-ntsc / gba / gb-dmg）N4.5 已逐一升級到 v2，但解析 v1 仍 work — 不破 build。v1 fields 移除留 future（無強制驅動力，目前所有 spec 都 v2）。

---

## 2. v2 schema example（NES NTSC）

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

注意 `mirror_mask: "0x0007"` 比 v1 的 `"0x2007"` 乾淨（只保留低 3 bit、不再混 region start bits）— 因為 v2 套 offset 不套絕對 addr。

---

## 3. Dispatch 重寫（v2）

### 3.1 Handler registry

```csharp
// Components 開機時 register
busBuilder.RegisterHandler("wram",   _wramRead,   _wramWrite);
busBuilder.RegisterHandler("ppu",    ppu.Read,    ppu.Write);    // 注意：收 offset，不收 abs addr
busBuilder.RegisterHandler("apu",    apu.Read,    apu.Write);
busBuilder.RegisterHandler("mapper", mapper.CpuRead, mapper.CpuWrite);

// MachineSpec 解析時，每 region 的 handler 字串查 registry 取對應 delegate
busBuilder.Build();   // 完成後 region table 內每個 entry 有 (delegate, mirror_mask, ...)
```

新 subsystem 加進去：spec 加一個 region with `handler: "<new_name>"`，subsystem class 在開機時 `RegisterHandler("<new_name>", ...)`。**Bus core code 完全不需改動**。

### 3.2 Offset-based mirror + handlers receive offset

```csharp
// Handler 簽名改成 (offset, value) — components 不再 care 絕對 addr
public delegate byte ReadHandler(int offset);
public delegate void WriteHandler(int offset, byte value);

// Bus dispatch
ref var page = ref _pageTable[addr >> _pageShift];
int offset = (addr - page.RegionStart) & (page.MirrorMask | someAllOnesMask);
return page.Reader(offset);
```

NesPpu 改：以前接 `WriteRegister(0x2000-0x2007 abs addr)`，現在接 `WriteRegister(0-7 offset)`。

NesPpu / NesApu 既有的 register handlers（PPUCTRL/PPUMASK/...）切換到 offset 後變 cleaner — 不再需要 `addr & 0x7` 的混亂。

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

NES 16-bit / 1KB-page → 64 entries × ~32 bytes = **~2KB 記憶體**。
ARM 32-bit / 4KB-page → 1M entries × 32 bytes = 32MB（太大）→ 需要 2-level page table（top 8 bits → 256 entries; bottom 12 bits → per-table 4096 entries; 平均使用 4-8 sub-tables → ~128KB）。**N4 只做 NES，2-level for ARM 留 future**。

Build cost：spec 載入時 walk regions、為每 page 設 entry。一次性 ~10μs。

Hot-path cost：1 array index + 1 add + 1 and + 1 indirect call ≈ ~5-10ns。比 v1 linear scan + switch 快、比 hardcoded if-else 還快（無分支、indirect call 對 hot delegate JIT 可 inline）。

### 3.4 fastmem 改 query API

```csharp
// Spec 不再有 fastmem_eligible field
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

JIT block-JIT emitter wants to inline GEP-store: query `bus.TryGetHostPointer(addr)`、收 host pointer + offset、emit inline store。沒命中 fallback 走 extern call。**spec 完全不知道 fastmem 是什麼**。

---

## 4. 不在 N4 範圍（後續處理狀態）

| 原本「未來」項目 | 後續實際做了 | 狀態 |
|---|---|---|
| ARM 32-bit 2-level page table（Gemini #4） | **N8** GBA 走 1-level page table（256 entries × 16 MB pages 對 GBA 已足夠） | ✅ 1-level 完成；2-level 真有 sub-region 需求才做 |
| `wait_states` runtime 強制（Gemini #7-add） | **N7** GetWaitStates query API + N10 schema declared | 部分 — query API 在；NES 沒 wait state、GBA 還沒實際 enforce timing |
| fastmem `TryGetHostPointer` query API（§3.4） | **N7** API 上線 + **N11** 6502 inline GEP-load (opt-in) | ✅ 完成；6502 perf-neutral，等其他 CPU 受惠 |
| spec format `extra_when_taken` / `extra_when_page_cross`（dynamic cycle penalties） | **N9** 加 top-level field 並 annotate branches | ✅ 完成（per-mode page-cross 留 future） |
| `allowed_widths` runtime enforce | **N10** GbaMemoryBus debug-mode flag | ✅ 完成（debug-mode 不影響 release perf） |

---

## 5. Migration plan（N4.0 - N4.6） ✅ 全部完工

| Step | 內容 | Commit |
|---|---|---|
| **N4.0** | 本 doc 落地 | (本 doc init commit) |
| **N4.1** | Schema v2 fields + parser；nes-ntsc.json 升級 v2 fields | `914cda0` |
| **N4.2** | Handler registry；ClassifyRegion 改用 v2 explicit Handler | `81103d8` |
| **N4.3** | Page-table O(1) dispatch（32-byte pages, 2048 entries）替換 linear scan | `61066a2` |
| **N4.4** | offset semantics — handlers 接 region-local offset | `101ae98` |
| **N4.5** | spec/machines/gba.json + gb-dmg.json 升級 v2 fields | `abad579` |
| **N4.6** | 3-run perf bench + perf doc + N3.4 invalidations | `4367154` |

每 step 一個 commit、push 後再下一步。**5 分鐘 timeout cap on tests**。

詳細 perf 數據：`MD/performance/202605091900-n4-memory-spec-v2.md`

---

## 6. Reference

- Gemini 諮詢 (2026-05-09)：`tools/knowledgebase/message/20260509_181033.txt`
- 之前 Gemini 諮詢 (2026-05-09 早些時候)：`tools/knowledgebase/message/20260509_163828.txt`
- Doc #19 — declarative JIT policy + CPU/Machine split
- Doc #21 — spec-driven runtime（含 N3.2/N3.3 結論）
