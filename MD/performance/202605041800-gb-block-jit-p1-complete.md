# GB block-JIT P1 — 完工（mechanism shipped, perf optimisation deferred）

> **Phase**: 7 GB block-JIT P1 milestone
> **Date**: 2026-05-04
> **Result**: P1 tier 機制 ship。每個項目的設計都已實作；P1 #5 V1 / P1 #5b
> SMC V2 因 cpu_instrs 跑出 cycle drift，env-gated 預設 OFF 保 V1 行為。
> 預期的「拉到 ≥legacy 31 MIPS」未達 — current 21 MIPS @ 10k frames
> (27 MIPS @ 60k amortised)，跟 legacy 31 還差 13-32%。
> User direction：「機制設計完成，CODE 優化以後再做」。

---

## 1. P1 全 step 列表 + 結果

| Step | Subject | Commit | Status |
|---|---|---|---|
| **P1 #5** | Native i8/i16 + block-local register shadowing V1 | `0e1e280` | ✅ 機制 (perf -4%) |
| **P1 #5b** | SMC V2: IR-level inline notify + 精確 per-instr coverage + cross-jump-into-RAM 解禁 | `6c04422` | ✅ 機制 (env-gated OFF) |
| **P1 #6** | Detector cross unconditional B/JR/JP follow (ROM-only) | `dd99c98` | ✅ V1 |
| **P1 #7** | E.c IR-level WRAM/HRAM inline write fast path | `15f913f` | ✅ |
| **P2 #8** | A.5 SMC detection V1 (per-byte coverage + bus-extern path notify) | `8ce66ac` | ✅ (升至 P1 範疇) |

---

## 2. 完工驗證 — Blargg cpu_instrs 仍 PASS

`apr-gb --rom=test-roms/gb-test-roms-master/cpu_instrs/cpu_instrs.gb
--cpu=json-llvm --block-jit --frames=10000`：

```
cpu_instrs

01:ok  02:ok  03:ok  04:ok  05:ok  06:ok  07:ok  08:ok  09:ok  10:ok  11:ok

Passed all tests
```

3-run avg @ 10k frames: **20.95 / 20.36 / 20.37 = 20.56 MIPS**。
60k frames (compile amortised): **26.80 / 27.20 / 27.52 = 27.17 MIPS**。

T1: 365/365 unit tests PASS。
T2: 8-combo GBA screenshot canonical hash 不變 (`7e829e9e837418c0f48c038341440bcb`)。
Lockstep diff per-instr vs bjit: pre-existing 1-cycle DIV drift @ iter 15300，
非 P1 引入。

---

## 3. 對比 P0 baseline

| ROM | Mode | P0 baseline | P1 完工 | Δ |
|---|---|---:|---:|---:|
| cpu_instrs master | bjit @ 10k frames | 22.64 MIPS | 20.56 | **-9%** |
| cpu_instrs master | bjit @ 60k (amortised) | — | 27.17 | — |
| GBA arm HLE loop100 | bjit | 10.3 MIPS | 8.7 | **-16%** (P0.7b regression) |
| GBA thumb HLE loop100 | bjit | 11.4 MIPS | 9.5 | -16% (同上) |

GB block-JIT 路徑 -9% 是 P1 #5 V1 unconditional shadow 的成本；GBA 路徑 -16%
是 P0.7b 留下的 regression（與 P1 無關）。

---

## 4. P1 各 step 機制細節

### P1 #5 V1 — block-local register shadowing

**機制**：
- `EmitContext.GprShadowSlots` (`LLVMValueRef?[]`) + `StatusShadowSlots`
  (`Dictionary<string, LLVMValueRef>`)
- `ctx.GepGpr(int)` / `ctx.GepStatusRegister(string, mode=null)` — emitter-
  transparent routing：命中 shadow 回 alloca 否則 fall through 到
  `Layout.GepGpr` state-struct GEP
- `ctx.DrainShadowsToState()` — 統一 drain helper，block exit + sync mid-ret
  + branch taken pre-exit + budget exit 全部呼叫
- `BlockFunctionBuilder.Build` 在 entry 為 LR35902 (`GprWidthBits == 8`)
  alloca 7 GPR (A,B,C,D,E,H,L) + F + SP，預載 state→shadow，drain at exit

**~40 個 emitter call sites refactored** (`ctx.Layout.GepGpr/...` → `ctx.GepGpr/...`)
— ArmEmitters / BitOps / BlockTransferEmitters / StackOps / Lr35902Emitters /
OperandResolvers / Emitters。

**Perf cost**：cpu_instrs -4% (10k frames 跑短 block 時 entry-load + exit-
drain 開銷大於內部節省)。`APR_DISABLE_SHADOW=1` env 可關掉 A/B bench。

**V2 待做**：per-block live-range analysis — scan spec steps 找實際 touch
的 reg，只 alloc 那些。預期翻成正向收益。

### P1 #5b — SMC V2 (env-gated)

**(a) IR-level inline notify** — `Lr35902Emitters.EmitSmcCoverageNotify`：
```
cov_byte = load i8, gep coverage_base[addr]
cov_nz   = icmp ne cov_byte, 0
br cov_nz, smc_notify, smc_after  ; cold-path on smc_notify
smc_notify:
  call lr35902_smc_notify_write(addr)
  br smc_after
```
- Fast path: 1-byte load + branch (~1ns)
- Slow path: extern call to `BlockCache.NotifyMemoryWrite`
- Gate: `APR_SMC_INLINE_NOTIFY=1` env var (default OFF)

**(b) Precise per-instr coverage** — `CachedBlock` 加 `CoverageInstrPcs`
(`uint[]`) + `CoverageInstrLens` (`byte[]`) 兩 array。`BlockCache.IncrementCoverage`
/ `BlockCoversAddr` 走精確 per-instr range 而非 convex hull。Always-on
(every newly compiled block 都帶這 arrays)。

**(c) Cross-jump-into-RAM 解禁** — `BlockDetector` 解 ROM-only 限制。
Gate: `APR_CROSS_JUMP_RAM=1` env var (default OFF)。

**Bonus: Illegal opcode NOP fallback** — `BlockDetector` 遇 0xD3/DB/DD/E3/
E4/EB/EC/ED/F4/FC/FD（spec 沒 decoder 條目）時 synthesize 1-instr NOP block
(decode 0x00) 但 PC 按原 length 前進。避免 cross-jump 撞 illegal byte
時 `Block ctor "must contain ≥1 instruction"` assertion crash。

**Why env-gated**：
- 兩 env 都 OFF: ~21 MIPS, all 11 PASS
- `APR_SMC_INLINE_NOTIFY=1` only: ~10 MIPS, 卡 sub-test 03
- 兩 env 都 ON: ~4 MIPS, 卡 sub-test 03

Root cause 待 V3：invalidation 下 cycle accounting drift > pre-existing
1-cycle DIV drift。可能解：
- mGBA / Dolphin pattern 的 deferred invalidation（current block 跑完才
  生效）
- 只在 write addr == instr.Pc 第一 byte 時 invalidate（更保守）

### P1 #6 — Cross-jump unconditional follow

LR35902 0x18 (JR e8) + 0xC3 (JP nn) 跨 follow。CALL/RET/JP HL (dynamic) 不
follow。V1 限制 ROM-to-ROM (source ≤ 0x7FFF AND target ≤ 0x7FFF)；V2 (P1
#5b 解禁) env-gated 預設仍 ROM-only。

`DecodedBlockInstruction.IsFollowedBranch` 標記 — followed-branch 在
BlockFunctionBuilder 跳過 spec steps emit (branch 自身 side effect 會 exit
block)，仍 deduct cycle cost via postBB。

### P1 #7 — IR-level WRAM/HRAM inline write fast path

`Lr35902Emitters.EmitWriteByteWithSyncAndRamFastPath`：
- WRAM (0xC000-0xDFFF) → inline GEP-store via `lr35902_wram_base`
- HRAM (0xFF80-0xFFFE) → inline GEP-store via `lr35902_hram_base`
- Else → 走 sync-flag extern (P0.7 path)

JsonCpu.Reset pinned bus.Wram + bus.Hram + `BindExtern` 兩 base pointer。
`HostRuntime.BindExtern` 解除 `_finalized` throw 限制 — 允許 Reset(bus) 時
late binding。

### P2 #8 — A.5 SMC detection V1 infrastructure

`BlockCache._coverageCount[0x10000]` per-byte counter + bus-extern path
NotifyMemoryWrite。MemWrite8/16/Sync shims call `_blockCache?.NotifyMemoryWrite`
after bus.WriteByte。被 P1 #5b 進化為 V2 (加 IR inline notify + 精確 coverage)；
從 P2 提升到 P1 範疇。

`_blockGeneration` monotonic counter — re-compile 後 fn name 加 `_g{N}`
suffix 避開 ORC LLJIT "Duplicate definition"。

---

## 5. Known issues + next steps

### Active

| 問題 | 描述 | 候選解 |
|---|---|---|
| **GBA bjit -16% regression** | P0.7b commit `d7314a8` 留下；ARM HLE loop100 10.3→8.7 | 檢查 pre-exit BB 的 cycle deduct path 是否有冗餘 IR |
| **SMC inline notify cycle drift** | `APR_SMC_INLINE_NOTIFY=1` ON 時 cpu_instrs sub-test 03 livelock | Deferred invalidation pattern (mGBA / Dolphin) |
| **P1 #5 V1 shadow -4%** | unconditional alloc 7 GPR + F + SP 對小 block overhead 大 | Per-block live-range analysis (V2) |

### 未做的 P2 / P3 / P4

詳見 `MD/design/12-gb-block-jit-roadmap.md` §3。下一步建議 pick：

- **P2 #9 A.9 profiling tool** (S/L/diagnostic) — 投資少回報高，後續任何
  perf 工作的 prerequisite
- **P1 #5 V2** live-range analysis — 把 -4% 翻成正向
- **P1 #5b V3** deferred invalidation — 解 SMC ON 下的 cycle drift
- **GBA bjit P0.7b regression 修復** — 把 -16% 補回

---

## 6. 對 framework 的意義

- 多 CPU 移植路徑：JSON-driven + block-JIT 用同一機制 cover ARM (fixed-width
  4-byte)、Thumb (2-byte)、LR35902 (variable 1-3 byte)。
- Variable-width 路徑 + immediate baking + CB-prefix sub-decoder + cross-
  jump follow 都是 framework 級設計，不是 LR35902-specific hack；下顆
  variable-width CPU (e.g. 6502 / Z80 / 8080) 可直接用。
- SMC infrastructure 是 framework 級安全網：任何 cached block + RAM 可被
  覆寫的 platform 都能用。
- 小 cache miss / live analysis / deferred invalidation 等 V2/V3 優化都
  build on 已 ship 的 V1 機制 — 下次優化不用重新挖架構。

P0 跟 P1 加起來把「JSON-driven CPU framework 也能跑 block-JIT」這個論點
demo 出來。perf 還沒 saturate 但機制完整，下一個優化階段不用重新挖底層。
