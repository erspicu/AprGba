# Performance Baseline — Phase 4.5 完工後 (2026-05-02)

> ⚠ **SUPERSEDED by [`MD/note/loop100-bench-2026-05.md`](/MD/note/loop100-bench-2026-05.md)** — 該檔是
> Phase 5.7 完工後（含 GB scheduler、PPU 完整化、BIOS open-bus、
> 5 個 ARM bug fix 等正確性改動之後）的正式 baseline，方法統一為
> 「跑 1200 frames stress-test ROM (loop100)」可重現。
>
> 本檔保留作為 Phase 4.5 階段的歷史快照，**Phase 7 block-JIT 對照
> 基準請以 `loop100-bench-2026-05.md` 的數字為準**。
>
> ---
>
> 原文（Phase 4.5 完工後 baseline）：
>
> 紀錄 Phase 4.5C 收完、還沒做任何效能優化（block-JIT / 跨指令 register
> caching / spec-driven dispatch 都還沒）的 baseline 數字。之後做完 Phase 7
> block-JIT 後可以拿這份對照看加速幅度。

---

## 1. 量測方式

### apr-gb（GB / DMG）

```
apr-gb --bench --rom=<rom.gb> [--cycles=N]
```

跑 LegacyCpu + JsonCpu 兩個 backend，分別用 `Stopwatch` 包 `RunCycles(N)`，
報告 wall-clock 時間 + 累積 `InstructionsExecuted`，算 MIPS。設定時間
（spec 編譯 + MCJIT JIT engine）另外列出，**不計入 MIPS**。

### aprcpu（GBA / ARM7TDMI）

```
aprcpu --bench-rom <rom.gba> [--steps N]
```

GBA 端只有 JSON-LLVM 一個 backend（沒有手寫 ARM 對照組），所以只報
單一個數字。Boot 流程跟 `GbaRomExecutionTests.BootGba` 一樣（minimal
BIOS stub + 三個 extern binding + multi-set CpuExecutor）。一個 warm-up
step 後才開計時。

### 環境

- Windows 11 x64
- .NET 10 Release build
- LLVMSharp.Interop 20.x，MCJIT，OptLevel=0
- 都加了 `nounwind` + `no-jump-tables` function attribute（避開 Windows
  COFF section-ordering 的 relocation crash，見
  [`MD/note/framework-emitter-architecture.md`](/MD/note/framework-emitter-architecture.md)）

---

## 2. 原始數據

### GB (LR35902)

ROM: `test-roms/blargg-cpu/cpu_instrs.gb` — Blargg master 套件
（11 個 cpu_instrs subtest 串起來，含 MBC1 banking）

執行配額: 400,000,000 t-cycles

```
setup time: legacy=0 ms, json-llvm=313 ms (incl. spec compile + MCJIT)

legacy   :  0.637 s,  35,611,151 instr →  55.91 MIPS
json-llvm: 10.666 s,  36,097,325 instr →   3.38 MIPS
ratio    : 16.52x slower (json-llvm vs legacy)
```

### GBA (ARM7TDMI)

ROM 1: `test-roms/gba-tests/arm/arm.gba` — jsmolka ARM mode 套件
ROM 2: `test-roms/gba-tests/thumb/thumb.gba` — jsmolka Thumb mode 套件

執行配額: 50,000,000 instructions（ROM 跑完後在 halt loop `B .` 自旋）

```
arm.gba    setup: 328 ms, run: 11.331 s, 50M instr → 4.41 MIPS
thumb.gba  setup: 200 ms, run: 11.681 s, 50M instr → 4.28 MIPS
```

---

## 3. 跟實機 CPU 比

### 實機 IPS 推估

| 平台 | 主時脈 | 平均 cycles/instr | 實機 IPS |
|---|---|---|---|
| GB DMG (LR35902) | 4.194 MHz T-cycles (1.048 MHz M-cycles) | ~2 M-cycles | **~0.5 MIPS** |
| GBA (ARM mode)   | 16.78 MHz cycles | ~1.5 cycles | **~10 MIPS** |
| GBA (Thumb mode) | 16.78 MHz cycles | ~1.2 cycles | **~12 MIPS** |

（實際 IPS 會視 memory wait state、PSR 操作、interrupt 等浮動。這些是
混合 ALU + memory 的典型遊戲碼概估值。）

### json-llvm vs 實機

| 平台 | 實機 IPS | json-llvm IPS | json-llvm 相對速度 |
|---|---|---|---|
| **GB DMG** | ~0.5 | 3.4 | **~7x real-time** ✅ |
| **GBA (ARM)** | ~10 | 4.4 | **~40-50% real-time** ❌ |
| **GBA (Thumb)** | ~12 | 4.3 | **~30-40% real-time** ❌ |

### Legacy 對照（hand-written 上限參考）

| 平台 | Legacy IPS | 相對實機 |
|---|---|---|
| GB DMG | 55.91 | **~110x real-time** |

Legacy GB 是 1900 行 `switch (opcode)` 的 hand-written interpreter，C# JIT
能高度 inline + register allocate + branch prediction friendly，所以快很多。
這是「同樣硬體上 hand-tuned interpreter 能達到的速度上限」的參考點。

---

## 4. 結論

### GB 端 — production-ready ✅

JsonCpu 3.4 MIPS 對 0.5 MIPS 實機，**有 ~7x 餘裕**。即使加上：

- DMG PPU 渲染（每 frame ~70K cycles）
- APU 音效合成
- 各種 IO + DMA

跑 GB 遊戲到全速 60 fps 完全沒問題。框架在 8-bit ISA 上「JSON 能直接拿來用」
的論點成立。

### GBA 端 — 框架對、效能不夠 ⚠️

11/11 ARM + Thumb 測試全綠，**正確性沒問題**。但 4.4 MIPS 對 10+ MIPS
實機 = **目前約 40% real-time**。意思是 60 fps 的遊戲會掉到 ~24 fps。

更悲觀的細節：

1. 我們測的 jsmolka ROM 多數 step 在 halt-loop `B .`（最便宜的指令）。
   實際遊戲碼會有：
   - 更多 memory access（多次 `memory_read_8` / `memory_write_*` extern call）
   - 更多 PSR 操作 + condition evaluation
   - 更多 mode switch / banked register 處理
   - 偶爾 interrupt
   實際遊戲 MIPS 可能掉到 **2-3 MIPS**
2. 還沒算 PPU 渲染（GBA 一條 scanline 就要算 128 個 sprite × 16-bit 像素）
3. 還沒算 APU、DMA 等

**結論**：GBA 上 instruction-level JIT 不夠快。要 Phase 7 block-JIT。

---

## 5. 為什麼 json-llvm 比 Legacy 慢這麼多

每條指令的 dispatch overhead：

```
JsonCpu.StepOne:
  bus.ReadByte(pc)          — virtual call into bus
  WriteI16(pcOff, pc+1)     — state buffer write
  decoder.Decode(opcode)    — dictionary lookup → entry walk
  BuildFunctionKey(...)     — string concatenation per call
  _fnPtrCache[key]          — dictionary lookup
  fn(state*, instructionWord) — indirect call into JIT'd code
  CyclesFor(def)            — iterate cycles.form string
  ConditionalBranchExtraCycles(...) — switch-case
```

每條指令還會在 JIT'd function 裡：

- 載入 `state` 指標
- 對 register / PC / F 各做一次 load + store
- 多數 ALU 還要 call `memory_read_8` 之類的 extern shim（C# delegate
  trampoline，每次 ~30-100 ns）

而 LegacyCpu 是：

```
case 0x47: _b = _a; break;  // 一條 mov，C# JIT 直接 inline
```

`_a`/`_b` 是 instance field，C# JIT register-allocate 進 CPU register。

**差距大致來源**：
- ~5x：JIT'd function 裡每條指令重新 load/store state buffer，沒有
  跨指令 register caching
- ~3x：每條指令過 dispatch chain（decode + key + fn pointer lookup
  + indirect call）
- ~2x：memory access 走 extern shim 而不是直接 inline

加起來大致 ~30x 上限，實測 16x 是因為部分 instruction 已經很 dispatch-heavy，
壓縮了相對差距。

---

## 6. 加速路線圖

按優先順序（CP 值高到低）：

### A. Block JIT (Phase 7) — 最大效益 ★★★★★

把連續 N 條指令 fuse 成一個 LLVM function。例如一段沒有 branch 的
linear code:

```
MOV R0, #1
ADD R1, R0, #2
STR R1, [R2]
B .next
```

目前是 4 次 dispatch + 4 次 indirect call。block-JIT 變成 1 次
dispatch + 1 次 indirect call，內部全 inline。

**預期加速**：5-10x（block 越長越好），對 GBA 接近 native interpreter
速度。

### B. State buffer → register caching ★★★★

讓 JIT'd function 在 entry 處把常用 register（PC、SP、A、F、HL...）
load 進 LLVM virtual register，exit 時才 store 回 state buffer。
LLVM 會 register-allocate 進 CPU 暫存器，省掉中間 load/store。

**預期加速**：2-3x。配合 block-JIT 效果更好（block 內所有指令共享
loaded register）。

### C. Memory bus extern inlining ★★★

memory_read_8 等是 C# 端的 trampoline。每次 call 過 cdecl boundary，
~30-100 ns。如果改成在 IR 內直接做 mapped-memory pointer arithmetic
（spec 的 memory map 寫死在 IR 裡），能省掉這層。

**預期加速**：1.5-2x（多 memory access 的程式更明顯）。

### D. Decode table specialization ★★

目前 DecoderTable 是「對 opcode 跑一遍 mask/match」。可以預先 precompute
一張 256-entry / 65536-entry 表。對 8-bit GB 直接是 256 個 entry，
ARM 16-bit Thumb 是 65536，ARM 32-bit 太大不適合（用 hash table）。

**預期加速**：1.2-1.5x。

### E. C# host loop hot-path tightening ★★

`StepOne` 裡的 `BuildFunctionKey` 用 string concat、`_fnPtrCache` 是
generic Dictionary。可以改成預先 cache 在 DecodedInstruction 上的
`IntPtr` field，省一次 dictionary lookup。

**預期加速**：1.1-1.2x。

### F. ORC LLJIT 取代 MCJIT ★

不直接加速 dispatch，但解掉 Windows COFF section ordering 的歷史
包袱，並支援 lazy compile / re-jit。對 block-JIT 後的 dynamic
recompilation 是必要前置。

**預期加速**：~0（純維護性投資，但 Phase 7 需要）。

---

## 7. Baseline 摘要（單行版）

當前（2026-05-02，Phase 4.5C 完工）：

- **GB JsonCpu = 3.4 MIPS（~7x real-time）→ production-ready**
- **GBA JsonCpu = 4.4 MIPS（~40% real-time）→ 等 Phase 7 block-JIT**
- **參考：Legacy GB = 56 MIPS，hand-written 上限大概在這附近**

之後加速完做 ROM 對拍時，數字應該往實機方向移動：GB 維持「遠超實機」，
GBA 至少要超過實機 1.5-2x（留 margin 給 PPU/APU/DMA 等其他工作）。
