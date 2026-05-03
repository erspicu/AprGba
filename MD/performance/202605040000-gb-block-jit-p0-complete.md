# GB block-JIT P0 — 完工 (Blargg cpu_instrs ALL 11 PASS)

> **Phase**: 7 GB block-JIT P0 完工 milestone
> **Date**: 2026-05-04
> **Result**: 整個 P0 tier ship。Blargg `cpu_instrs.gb` (master ROM) 在
> block-JIT mode 全 11 子測試 PASS（含 BIOS LLE 啟動）。

---

## 1. P0 全 step 列表 + 結果

| Step | Subject | Commit | Result |
|---|---|---|---|
| **P0.1** | Variable-width `BlockDetector` + LR35902 length oracle | `3024100` | ✅ |
| **P0.2** | 0xCB prefix as 2-byte atomic instruction | `0cb93a8` | ✅ |
| **P0.3** | Immediate baking via instruction_word packing | `7a8305a` | ✅ |
| **P0.4** | GB CLI `--block-jit` + Strategy 2 PC fixes | `adddade` | ✅ |
| **P0.5** | HALT/STOP block boundary | `a10a718` | ✅ |
| **P0.5b** | EI delay band-aid (deprecated by P0.6) | `6a86005` | ✅ |
| **P0.5c** | `Lr35902Alu8Emitter.FetchImmediate` baking + `--diff-bjit` lockstep harness | `d760b08` | ✅ |
| **P0.6** | Generic `defer` micro-op + AST pre-pass | `ca248e8` | ✅ |
| **P0.6** step 3 | Cross-block defer body emit at block exit + per-instance JsonCpu state + Io[] diff check | `794ba73` | ✅ |
| **P0.7** step 1+2 | Bus sync extern (Write*WithSync) + Lr35902StoreByte sync exit | `2a1de15` | ✅ |
| **P0.7** step 3 | Generic `sync` micro-op + EI defer integration + cycle-deduct fix | `674316f` | ✅ |
| **P0.7b** | Conditional branch taken-cycle accounting | `34f9f4b` `d7314a8` | ✅ |

---

## 2. 完工 milestone — Blargg cpu_instrs PASSED

`apr-gb --rom=test-roms/gb-test-roms-master/cpu_instrs/cpu_instrs.gb
--cpu=json-llvm --block-jit --frames=10000`：

```
cpu_instrs

01:ok  02:ok  03:ok  04:ok  05:ok  06:ok  07:ok  08:ok  09:ok  10:ok  11:ok

Passed all tests
```

11 subtests:
- 01-special, 02-interrupts, 03-op_sp_hl, 04-op_r_imm, 05-op_rp,
  06-ld_r_r, 07-jr_jp_call_ret_rst, 08-misc_instrs, 09-op_r_r,
  10-bit_ops, 11-op_a_hl

**含 BIOS LLE 開機亦 PASS**（`--bios=BIOS/gb_bios.bin`）。

---

## 3. Perf 數字（3-run avg, MIPS）

### GB Blargg cpu_instrs.gb (master ROM, 10000 frames)

| Backend | MIPS | 跟 baseline 比 |
|---|---|---|
| `--cpu=legacy` (no block-jit) | 55.49 | reference high |
| `--cpu=legacy --block-jit` (no-op for legacy) | 55.42 | (legacy ignores --block-jit) |
| `--cpu=json-llvm` (per-instr) | 9.04 | baseline |
| `--cpu=json-llvm --block-jit` | **22.64** | **+150%** vs per-instr |

**Block-JIT 是 per-instr 的 ~2.5× 速度**。距 legacy interpreter 的 55 MIPS
還有 2.4× 差距 — 預期 P1 (block-local state caching, detector cross
unconditional B) 把這個差距收進 50% 內。

### GBA loop100 (1200 frames, no BIOS)

| combo | per-instr | block-JIT | bjit/pi ratio |
|---|---|---|---|
| arm pi   | 8.13 | — | — |
| arm bjit | — | 8.70 | +7% |
| thumb pi | 8.23 | — | — |
| thumb bjit | — | 10.13 | +23% |

**GBA bjit 比 baseline (P0.7 前) 跌 11-16%** — P0.7b 加的 taken-branch
cycle deduct 影響到 GBA pre-exit BB。User 接受 trade-off「正確性優先、
效能再慢慢修正」。Perf followup 留 P1 階段一起處理。

---

## 4. 比較：P0 起點 → P0 完工

| 階段 | GB block-JIT MIPS | Blargg cpu_instrs |
|---|---|---|
| P0 起點 (沒 block-JIT) | N/A | per-instr 9 MIPS, 全過 |
| P0.4 (block-JIT 跑通) | 10.5 | 部分 subtest 過 |
| P0.5c (CP A,n bug 修) | 10.6 | **01-special PASS** |
| P0.7 step 3 (sync micro-op) | ~20 | 01 + 02 PASS |
| **P0.7b 完工** | **22.64** | **全 11 子測試 PASS** |

從「block-JIT 跑得起來但 Blargg 半數 fail」到「**完整 cpu_instrs PASS
+ +150% perf vs per-instr**」。

---

## 5. 架構勝利

P0 期間累積的 generic framework 機制（不限 GB / LR35902）：

| 機制 | 用途 | 通用性 |
|---|---|---|
| Variable-width `BlockDetector` + length oracle | 任何 1/2/3-byte ISA | LR35902 + 未來 6502/Z80/x86 |
| Prefix sub-decoder dispatch（0xCB-style） | atomic prefix+sub-opcode 編譯 | LR35902 + Z80 (DD/FD/ED) + x86 (REX/VEX) |
| Strategy 2 PC reads (`PipelinePcConstant`) | block 內 PC 讀化為 const | ARM / Thumb / LR35902 / 任何 spec |
| `defer` micro-op + AST pre-pass | 1-instr-delayed effects (EI/STI/...) | 任何 CPU 的延遲生效指令 |
| `sync` micro-op | 強制 block 退出 + 重新 check IRQ | 任何 IRQ-mutator instruction |
| Bus sync flag (Write*WithSync extern) | MMIO write 後 IRQ-state-changed → block exit | 任何 CPU 的 MMIO map |
| Conditional branch cycle deduct | taken vs not-taken cycle 差異 | 任何有 conditional branches 的 ISA |
| Lockstep diff harness (`--diff-bjit`) | per-instr vs block-JIT 對拍 debug 工具 | 任何 spec |

**P0 不只把 GB block-JIT 推到能跑 Blargg 全綠，更建立了一套通用 framework
機制讓未來新 CPU 加 block-JIT 不用碰 BlockDetector / BlockFunctionBuilder
等核心 C# code，只改 spec 即可。**

---

## 6. 設計 doc + Gemini 諮詢紀錄

| Doc | 內容 |
|---|---|
| `MD/design/12-gb-block-jit-roadmap.md` | P0/P1/P2/P3/P4 完整 priority 排序 |
| `MD/design/13-defer-microop.md` | `defer` 機制 design |
| `MD/design/14-irq-sync-fastslow.md` | Hybrid IRQ sync 機制 design |
| `tools/knowledgebase/message/20260503_202431.txt` | Gemini 諮詢: variable-width + prefix + narrow-int |
| `tools/knowledgebase/message/20260503_220938.txt` | Gemini 諮詢: generic delayed-effect mechanism |
| `tools/knowledgebase/message/20260503_224732.txt` | Gemini 諮詢: IRQ delivery granularity |

---

## 7. 還沒解的 / 後續工作

### 7.1 GBA bjit perf regression (~11-16%)

P0.7b 的 pre-exit BB 影響 ARM block-JIT 的 hot-path layout。Followup：
- 改 IR 結構讓 LLVM 更好優化
- 或把 cycle accounting 進一步移到 host loop（trade-off 需評估）
- 留 P1 收尾時一起處理

### 7.2 lockstep diff 還有少量 drift

`--diff-bjit=N` 在大量 step 後仍會偵測 PPU/scanline 細節 timing 的偏差。
不影響任何 Blargg cpu_instrs 子測試 — pass rate 全綠。Mooneye 等更嚴格
的 cycle-precise 測試套件可能會撞到。Followup 視需要再做。

### 7.3 Per-instr backend `_eiDelay` 路徑保留

V1 strategy: per-instr 仍走舊 `lr35902_arm_ime_delayed` extern；只有
block-JIT 用新 `defer` 機制。V2 統一兩 path（per-instr 也走 generic
defer pending-action runtime mechanism）— 工作量小但目前沒迫切需求。

---

## 8. 進入 P1

P0 完工後，roadmap 下個階段：

- **P1 #5** Native i8/i16 + block-local state caching — 所有 GPR 在 block
  入口 load 進 LLVM SSA，出口 store 回 state buffer
- **P1 #6** Detector 跨 unconditional B/JR/JP — block 平均長度從 1.0-1.1
  拉到 5-10，預期 bjit 速度大幅提升
- **P1 #7** E.c IR-level memory region inline check — JIT 內 inline
  region check 跳過 extern call

預期 P1 完工後 GB block-JIT MIPS 從 22.64 拉到 40-60（接近 legacy 55）。
GBA bjit 也應該一起受益。
