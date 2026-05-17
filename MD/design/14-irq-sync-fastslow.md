# IRQ 傳遞粒度 — Hybrid Fast-Path / Slow-Path Sync

> **狀態**：設計文件（2026-05-03）。實作追蹤為 Phase 7 GB block-JIT P0.7
> （看 [`MD/design/12-gb-block-jit-roadmap.md`](/MD/design/12-gb-block-jit-roadmap.md)）。
>
> **起源**：Gemini consult
> ([`tools/knowledgebase/message/20260503_224732.txt`](/tools/knowledgebase/message/20260503_224732.txt))
> 關於業界在 block-JIT 內 match per-instruction IRQ delivery 粒度的 pattern、
> 不犧牲 throughput。
>
> **目標**：block-JIT mode 下 per-instruction IRQ delivery 粒度，common
> case（無 IRQ trigger 的 RAM access）下 **近零 perf cost**。Generic 跨
> CPU spec — framework 跟 JSON spec 無 per-CPU MMIO 知識。

---

## 1. 問題陳述

目前狀態（P0.6 出貨）：
- Per-instr backend 每 instruction 後檢查 IRQ pending
- Block-JIT backend 只在整 block 之後檢查 IRQ pending（~5-30 instruction/block）
- IRQ-timing test ROM（Blargg 02-interrupts）偵測 divergence 並 fail

具體例：Blargg 02-interrupts subtest 2 set up STAT IRQ、跑
`LDH (FF41), A` 寫到 STAT register、期待下個 instruction 邊界 IRQ vector。
Per-instr 傳得對。Block-JIT 晚 ~10-30 個 instruction 才傳（當前 block 結束後）。
Test 偵測 divergence → fail。

這是 GB block-JIT match per-instr 行為的最後一個大正確性 gap。

---

## 2. 為什麼 naive solution 不 work

| Approach | Cost | Gemini 評價 |
|---|---|---|
| (1) 每 instruction 後 inline IRQ check | ~5-15% MIPS hit | IR 膨脹、L1 instruction cache 壓力、擋住 LLVM cross-instr optimization |
| (3) Static MMIO-aware block exit | 低成本 | **anti-pattern**：JSON spec 不該知道 `0xFF0F` 跟 IRQ 相關；不通用 |
| (5) Profile-guided adaptive | 高複雜度 | 對穩定的 emulator pattern overkill |
| (2) Cycle-budget 主動 exit | 0 cost | 只對可預測 event（timer/scanline）work；軟體觸發（直接 set IF 的 MMIO write）失敗 |

實際解組合 **(2) cycle-budget for 可預測** + **(3) 的 generic variant
for 不可預測** — 但 DYNAMIC 解（spec 不知道 MMIO）、不 static。

---

## 3. 架構：Hybrid Fast-Path/Slow-Path Sync

把 IRQ delivery 拆兩類分別處理：

### 3.1 可預測 IRQ（timer / scanline）— Phase 1a 已 cover

Scheduler 在每個 RunCycles call 之前算「離下個 pending event 多少 cycle」
→ 載到 `cycles_left` budget → block IR per instr decrement（Phase 1a）
→ block 在對的 cycle 邊界 early exit。

**Cost**：0（已實作）。

### 3.2 不可預測 IRQ（MMIO write）— P0.7 新

只在可能 mutate IRQ state 的 instruction 後檢查 IRQ state：
- Memory write（MMIO 可能 set IF 或 IE）
- 特殊 CPU instruction（EI/DI/MSR）

**關鍵 insight**：JSON spec 不需要知道哪些 address 跟 IRQ 相關。
C# bus 知道。讓 bus.Write return 「需要 sync」flag；true 就 JIT'd
block 提早 exit。

#### 3.2.1 Bus extern 簽名改

之前：
```csharp
[UnmanagedCallersOnly]
public static void BusWrite(uint addr, byte value);
```

之後：
```csharp
[UnmanagedCallersOnly]
public static byte BusWrite(uint addr, byte value);   // 需要 IRQ sync 回 1、否則 0
```

Bus 實作知道每 CPU 的 IRQ-relevant address 範圍（GB：0xFF0F IF、
0xFFFF IE、加上 device-specific 像 0xFF41 STAT）。寫一個這些 register
MAY 改 IRQ state 時、return 1。

#### 3.2.2 Memory write emitter — fast/slow split + sync check

LLVM IR pattern（per write）：
```llvm
mem_write:
  %is_ram = icmp ult i32 %addr, RAM_END           ; 便宜的 region check
  br i1 %is_ram, label %fast_path, label %slow_path
                                ; ↑ 99% 預測到 fast_path

fast_path:
  ; Inline GEP + store — region check 之外無額外 branch
  store ...
  br label %continue

slow_path:
  ; Phase 1b MMIO callback（MMIO 反正都要付）
  %sync = call i8 @BusWrite(i32 %addr, i8 %val)
  %sync_b = icmp eq i8 %sync, 1
  %sync_h = call i1 @llvm.expect.i1(i1 %sync_b, i1 false)  ; cold path
  br i1 %sync_h, label %exit_block_for_sync, label %continue

exit_block_for_sync:
  ; Serialize PC + cycles_consumed、return
  store next_pc, ptr %pc_slot
  store i8 1, ptr %pc_written_slot
  ret void

continue:
  ; Block 下個 instruction
```

**Cost 分析**：
- RAM write（絕大部分）：1 個 region-check branch、完美預測 → ~0 額外
  cost（branch predictor 飽和到 0 cycle）
- MMIO write（少）：既有 P/Invoke callback overhead 主導；新 sync-check
  是一個 i8 compare + branch + exit cleanup — ~2ns 額外、完全被 MMIO
  callback 淹沒
- 因 sync 而 block exit（極少 — 只在 MMIO 真的改 IRQ state 時）：
  一個 block 稍早結束、outer loop 送 IRQ、跟 per-instr 一樣

#### 3.2.3 特殊 CPU 指令 — `sync` micro-op + defer 整合

對不碰 memory 但改 IRQ state 的 instruction（EI / DI / ARM MSR 等）、
用新 generic micro-op：

```json
{ "op": "sync" }
```

`sync` 的 emitter 就 emit `ret void`（或 branch 到 block_exit BB）。
`sync` 之後、control 回到 outer loop、它重新檢查 IRQ。

LR35902 EI（用既有 P0.6 `defer` 機制）：
```json
{
  "mnemonic": "EI",
  "steps": [{
    "op": "defer", "delay_value": 1, "action_id": 0,
    "body": [
      { "op": "lr35902_ime", "value": 1 },
      { "op": "sync" }
    ]
  }]
}
```

P0.6 的 AST pre-pass 把這個 body 注到 EI+1 instruction 結尾之後、
產生的 block：
- 跑 EI+1 instruction 的正常 step
- Set IME=1
- `sync` → `ret void` → outer loop 檢查 IRQ

如果有 IRQ pending 而 IME 剛變 1、outer loop NOW 送它 — 完全 match
per-instr 的「EI+1 跟 EI+2 之間」點。

---

## 4. 通用性

機制完全 generic：

| CPU | IRQ-state mutator | sync 機制 |
|---|---|---|
| LR35902 | EI/DI、寫 IF (0xFF0F) / IE (0xFFFF) / STAT (0xFF41) 等 | EI/DI 用 spec 的 `sync` step；bus 追 MMIO addr |
| ARM7TDMI | MSR CPSR_c（set I/F bit）、寫 IF/IE register | MSR 用 `sync` step；bus 追 0x04000200/202/208 |
| RISC-V | CSRRW 到 mip/mie/mstatus、ECALL/EBREAK | CSRRW 用 `sync` step；CSR 是 register-based 不是 memory |
| MIPS R3000 | mtc0 到 Status/Cause register | mtc0 用 `sync` step |

Spec 描述 SEMANTIC（「這個 op 可能改 IRQ state、之後 sync」）。
Framework 只在 C# bus 實作層知道 per-CPU MMIO map。JSON spec 維持 generic。

---

## 5. 實作步驟（P0.7）

### 5.1 Step 1 — Bus extern 簽名改（~0.5 day）

- `MemoryEmitters.ExternFunctionNames`：加新 `Write8WithSync` extern
  variant（舊 `Write8` 留著、給遷移期間的 non-sync caller 用；之後移除）
- C# bus shim（GbMemoryBus、GbaMemoryBus）：實作新簽名、碰 IRQ-relevant
  address 的 MMIO write return sync flag

### 5.2 Step 2 — Memory write emitter fast/slow split（~1 day）

- `Lr35902StoreByteEmitter` + ARM 等價：emit fast-path region check
  + slow-path callback + sync check
- Block-JIT：sync exit path 存 `next_pc + PcWritten=1` 然後 `ret void`
  （類似 budget-exit path）
- Per-instr mode：sync 是 no-op（per-instr 已經每 instruction 之間檢查 IRQ）

### 5.3 Step 3 — `sync` micro-op（~0.5 day）

- 在 `Emitters.cs` 新 emitter `SyncEmitter`（generic、所有 CPU）
- Block-JIT emit：存 `next_pc`、mark PcWritten=1、branch 到 block_exit
- Per-instr emit：no-op（outer loop 反正檢查 IRQ）
- 加到 `EmitterRegistry.RegisterStandard`

### 5.4 Step 4 — 更新 LR35902 EI spec（~0.5 day）

- 在 EI 的 defer body 內 `lr35902_ime` 之後加 `{ "op": "sync" }`
- 應該也更新 DI（它把 IRQ state 改成 NO interrupt；不 sync 也安全因為
  IRQ 反正不會 fire、但對稱起見更乾淨）

### 5.5 Step 5 — GbMemoryBus IRQ-relevant address 追蹤（~0.5 day）

- 識別 GB IRQ-relevant address：0xFF0F (IF)、0xFFFF (IE)、
  0xFF41 (STAT — bit 3-6 控制 STAT IRQ source)
- 新 `WriteByte` overload（或擴）return sync flag
- 在 GbMemoryBus header 文件化

### 5.6 Step 6 — 驗證（~0.5 day）

- T1 unit test（無預期改變、但對 sync emitter 加 test）
- T2 GBA matrix（regression check）
- Lockstep diff Blargg 01-special（還是 0 divergence）
- Lockstep diff Blargg 02-interrupts（現在應該 0 或接近 0 divergence）
- Blargg 02-interrupts block-JIT：應該 PASS（之前「EI Failed #2」）
- Bench：量 perf 影響（預測 < 1% 損失、可能因為 branch prediction = 0）

**Total**：~3 day 工作。

---

## 6. Edge case / 踩坑

### 6.1 Budget downcount 中 sync exit

如果 sync exit 在 block 中間 fire、cycles_left budget 可能不等於
「cycles consumed = initial budget - exit 時 cycles_left」因為 budget
exit 跟 sync exit 是不同 path。確保兩條 path 都正確 update cycles_left
讓 outer loop 的 bus.Tick 拿到對的值。

### 6.2 Block 內多個 sync

如果 block 有多個 `sync` micro-op（例如一個 block 內多個 EI）、只 FIRST
個 fire — block 立刻 exit。Per-instr 行為：每個 instruction 的 IRQ check
都可能 fire 自己的 IRQ。用 sync exit、每個 block 最多結束一次。Outer
loop re-enter、從那裡跑下個 block、可以再 fire。

### 6.3 RAM region check size

LR35902、「RAM」= WRAM (0xC000-0xDFFF) + HRAM (0xFF80-0xFFFE)。MMIO
是 0xFF00-0xFF7F + 0xFFFF。Cart RAM 是 0xA000-0xBFFF（有時 MBC 控制）。
簡單起見 V1 只用 `addr < 0xFF00 || addr == 0xFFxx_specific_region` check；
V2 可以擴成 multi-range。

### 6.4 MMIO 不真改 IRQ state 時 sync flag 多丟

C# bus 可以保守（「任何 MMIO write return sync=1」）或精準（「只在實際
改 IF/IE/STAT bit 的 write return sync=1」）。保守簡單 + 便宜實作；
精準省 spurious block exit。先保守；profile 後緊收如需要。

### 6.5 Per-instr backend cost

Per-instr 不需要 fast/slow split 因為 outer loop 反正每 instruction
檢查 IRQ。Per-instr emitter 就 call bus write extern 並忽略 return value。
更乾淨：per-instr 用舊 `Write8`（無 sync return）；block-JIT 用新
`Write8WithSync`。透過 `ctx.CurrentInstructionBaseAddress`（既有 block-JIT
mode marker）在 emit time 偵測。

---

## 7. 為什麼這是高工程價值

成功的話、給：
1. **Block-JIT IRQ 正確性等同 per-instr** — Blargg 02-interrupts PASS、
   真實遊戲的 IRQ-sensitive code（DMA、sound timing）block-JIT 下正確 work
2. **Generic framework** — 對任何 CPU spec work；新 CPU 只需要 IRQ-mutator
   instruction 的 `sync` annotation + bus 追 IRQ-relevant address
3. **近零 perf cost** — RAM write 只付被預測好的 region-check branch
   （~0ns）；MMIO write 在既有 P/Invoke 之外只付一個 sync-check branch
   （~2ns）、被 MMIO callback 本身淹沒
4. **生產級 pattern** — QEMU TCG（`gen_io_start` + `cpu_loop_exit`）、
   Dynarmic（`halt_requested` flag）、DOSBox dynarec 用同樣架構
5. **解鎖未來工作** — 一旦 IRQ delivery per-instr 準確、block-JIT 可以
   當 ARM 跟 LR35902（跟未來 CPU）的預設 backend；per-instr 只當 debug
   fallback

這是決定 framework 的 block-JIT 是「只夠 screenshot test」還是「夠跑
任何商業 ROM」的架構決策。P0.7 是 gate。
