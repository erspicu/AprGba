# AprCpu framework vs 模擬器外殼 — Timing 責任分工

> **Status**: design + introduction (2026-05-05)
> **Scope**: 把「`AprCpu` 框架負責哪些 timing」跟「emulator 外殼 (AprGba /
> AprGb / 將來其他 host) 負責哪些 timing」明確劃線，描述兩邊互動的 contract
> 介面跟責任邊界。
>
> **目標讀者**：(a) 想接手把 AprGba/AprGb 推到完整 emulator 的人；(b) 想拿
> `AprCpu` 框架做新 emulator (例如 6502 / Z80 / 8080) 的人；(c) 想理解
> 「framework 邊界在哪」的 future me。
>
> **對照**：
> - [`MD/design/15-timing-and-framework-design.md`](/MD/design/15-timing-and-framework-design.md) — 講「我們**怎麼**把 timing 做進框架」（觀念與方法）
> - 這份 doc — 講「**誰負責什麼**」（責任分工與 contract 介面）
> - [`MD/design/16-emulator-completeness.md`](/MD/design/16-emulator-completeness.md) — 講「emulator 外殼還缺哪些 timing-related 子系統」（完成度盤點）

---

## 1. 為什麼要劃這條線？

`AprCpu` 是個 **CPU 模擬框架**，不是 **平台模擬器**。框架的工作是「正確、有
效率地執行 spec 描述的 CPU instruction stream」；平台模擬器的工作是「把
PPU、APU、Timer、DMA、Joypad、cartridge 等周邊圍著 CPU 跑出一台真實機器」。

這兩件事的 timing 責任是**互補但不重疊**的：

- 框架要管「**這條 instr 花了幾個 cycle**」、「**block 跑到哪該讓出**」、「**IRQ
  該何時被檢查**」
- 模擬器要管「**現在距離下個 PPU event 還剩幾 cycle**」、「**寫 0xFF40 LCDC 後
  下一條 instr 看到的 STAT 是什麼**」、「**Timer overflow 時 IF.bit-2 該怎麼設**」

劃清楚這條線的價值：

1. **新 CPU port 不用重做 emulator scaffolding**。換 ARM7TDMI 為 6502，框架
   端不用改；新 emulator 只要實作 6502 的 PPU/Timer/IO 跟同一套介面對話。
2. **新 emulator port 不用重做 CPU framework**。AprGba 已 ship、AprGb 已
   ship；新 platform (例如 NES / SMS / Z80 PC) 換掉 PPU/APU 但 CPU + 框架完
   全 reuse。
3. **Bug isolation**。Timing 有問題時可以先問「是 CPU 拍子錯（框架 bug）還
   是 HW tick 錯（emulator bug）」，而不是兩邊一起翻。
4. **避免 framework rot**。如果框架塞 PPU/APU specific 邏輯就會跟某個 platform
   耦合，第三顆 CPU port 時要把它撕回來。

---

## 2. 責任分工總表

| Timing 行為 | 誰負責 | 介面 |
|---|---|---|
| 每條 instr cycle cost | 框架 (從 spec `cycles.form` 解析) | spec JSON |
| Block-JIT 內 cycle deduct + budget exhaustion | 框架 (predictive downcounting in IR) | `state.cycles_left` |
| Per-instr cycle tick (per-instr backend) | 框架 (CpuExecutor 內) | `bus.Tick(N)` callback |
| **HW model 推進** (PPU dot / Timer counter / APU sample) | **emulator** | `Scheduler.Tick(N)` |
| **下個 HW event 還剩幾 cycle** | **emulator** (Scheduler 計算) | `Scheduler.CyclesUntilNextEvent` |
| HW event 觸發時的 IRQ source set (例：VBlank、Timer overflow) | emulator | `bus.RaiseInterrupt(source)` |
| IRQ pending bitmask 維護 | emulator (在 bus IF / IE) | bus 內部 |
| **IRQ delivery sequence** (CPU mode swap、PC=vector、SPSR save) | **框架** | `CpuExecutor.DeliverIrqIfPending()` |
| IRQ 該檢查的時機 (instruction boundary) | 框架 (per-instr / block-exit) | 框架自動 |
| Delayed-effect instruction (EI、STI) | 框架 (`defer` micro-op) | spec micro-op |
| Mid-block control yield (IRQ-relevant write) | 框架 (`sync` micro-op) | spec micro-op + bus extern return value |
| 寫 MMIO 時 HW state 推到「現在」 | **emulator** (bus 接 catch-up callback) | `bus.OnMmioWrite(addr, ...)` |
| Self-modifying code detection | 框架 (BlockCache coverage counter) | `bus.WriteByte` 走 framework shim |
| Pipeline-PC reads | 框架 (Strategy 2 baking) | spec `pc_offset_bytes` |
| Save state / battery RAM (.sav) | emulator | host file I/O |
| Joypad / keyboard input | emulator | host input event |
| Audio output streaming | emulator | host audio backend |
| **Frame timing** (60Hz wall-clock sync) | **emulator** (還沒做) | host vsync / sleep |

**簡單記法**：
- **框架管 CPU 內部** — 指令拍子、IRQ 處理流程、block-JIT 機制
- **Emulator 管 CPU 外部** — HW state 推進、HW event 觸發、host UI/IO

---

## 3. 介面契約（contract API）

兩邊靠以下幾個 contract 互動。**這些介面的形狀是 framework-level 設計，新
emulator port 應該實作這些**。

### 3.1 `IMemoryBus` interface — emulator → 框架的記憶體 + IRQ 入口

```csharp
public interface IMemoryBus
{
    byte   ReadByte(uint addr);
    void   WriteByte(uint addr, byte value);
    ushort ReadHalf(uint addr);
    void   WriteHalf(uint addr, ushort value);
    uint   ReadWord(uint addr);
    void   WriteWord(uint addr, uint value);

    // 框架不知道 HW model；只 call Tick 把 cycles 推給 emulator
    void Tick(int tCycles);

    // emulator 看自己的 IF/IE 決定有沒有 IRQ pending；framework 不解析 IF
    bool   IrqPending { get; }

    // 開機時告訴 framework BIOS LLE 還是 HLE
    bool   BiosEnabled { get; }

    // RAM region pinned byte arrays — 給 framework 的 IR-level inline 走
    byte[] Wram { get; }
    byte[] Hram { get; }
}
```

**誰實作**：emulator (例：[`GbaMemoryBus.cs`](/src/AprCpu.Core/Runtime/Gba/GbaMemoryBus.cs)、
[`GbMemoryBus.cs`](/src/AprGb.Cli/Memory/GbMemoryBus.cs))。

**誰使用**：框架的 `CpuExecutor` 跟 block-JIT 的 bus extern。

**Contract 重點**：
- `ReadByte/WriteByte` 是純 memory access；emulator 內部會做 region routing
  (ROM / RAM / IO / VRAM / OAM 等)
- `Tick(N)` 是「請把 HW 推 N cycles」；framework 在 per-instr 模式下每條 instr
  call 一次，block-JIT 模式下不直接 call (走 catch-up callback 路徑)
- `IrqPending` 是個 boolean — emulator 內部該怎麼算 (`(IF & IE) != 0 && IME`)
  是 emulator 自己的事，framework 不碰

### 3.2 `cycles_left` budget — 框架 ↔ emulator 的 cycle 帳

```
emulator 端 (per Step):
    bus.cycles_left = scheduler.CyclesUntilNextEvent;   // emulator 算
    cpu.Step();                                          // framework 跑
    consumed = bus.cycles_left_before - bus.cycles_left; // framework 扣完
    scheduler.Tick(consumed);                            // emulator 推 HW
```

**誰算 budget**：emulator 的 Scheduler — 它知道 PPU 距離下個 HBlank/VBlank
還幾 cycle、Timer 距離 overflow 還幾 cycle、取最小值就是 budget。

**誰扣 budget**：framework 的 block-JIT IR — 每條 instr 從 budget 減自己的
cycle cost、≤0 時 block 提前 exit。

**為何這樣切**：
- Framework 不知道「HW 下一個事件是什麼」— 那是 emulator 的 PPU / Timer 在
  管的
- Emulator 不想知道「block-JIT 怎麼 deduct」— 那是 framework 內部優化

### 3.3 Bus extern sync flag — block-JIT mid-block yield

```csharp
// In emulator (e.g. JsonCpu.MemWrite8Sync):
[UnmanagedCallersOnly]
private static byte MemWrite8Sync(uint addr, byte value)
{
    bus.WriteByte((ushort)addr, value);
    bus.NotifyMemoryWrite(addr);     // SMC notify
    return IsIrqRelevantAddress(addr) ? (byte)1 : (byte)0;
    //     ^^^^^^^^^^^^^^^^^^^^^^^^^^
    //     emulator 決定哪些 addr 寫了之後 IRQ 狀態可能變
}
```

**誰決定哪些 addr 是 IRQ-relevant**：emulator (因為 IF/IE 在 emulator 的
bus state 裡)。

**誰處理 sync flag**：framework — `sync` micro-op emit 出 IR 接收 i8 return
value，非零就 mid-block ret void、emulator 下一次 dispatch 重新查 IRQ。

**為何分這樣**：framework 對「IRQ-relevant 地址」沒概念（每個平台不一樣 — GBA
是 0x04000200 IF / 0x04000202 IE，GB 是 0xFF0F IF / 0xFFFF IE）；framework 只
管「sync flag = 1 該怎麼 yield」。

### 3.4 MMIO catch-up callback — block-JIT 外的 HW 同步

```csharp
// emulator 的 bus.WriteByte 處理 MMIO 寫之前：
public void WriteByte(uint addr, byte value)
{
    if (IsMmio(addr)) {
        // mid-block: framework 已經消耗了一些 cycle 但 PPU 還沒看到
        var consumed = budgetAtStep - cpu.CyclesLeft;
        if (consumed > 0) {
            scheduler.Tick(consumed);   // 推 PPU 到「現在」
            budgetAtStep = cpu.CyclesLeft;
        }
    }
    // 真實寫入
    DoActualWrite(addr, value);
}
```

**誰算「現在距離 step 開頭幾 cycle」**：emulator (從 `cpu.CyclesLeft` snapshot
diff 算)。

**誰決定要不要 catch-up**：emulator (只有寫 MMIO 才需要；寫 RAM 不必)。

**Framework 的角色**：把 `cpu.CyclesLeft` 暴露給 emulator 讀就好。

### 3.5 SMC notify — RAM 寫的時候要不要 invalidate cached block

```csharp
// emulator 的 bus.WriteByte 之後：
public void WriteByte(uint addr, byte value)
{
    DoActualWrite(addr, value);
    cache?.NotifyMemoryWrite(addr);      // 框架的 BlockCache 自己 scan
}
```

**誰維護 coverage counter**：framework (在 `BlockCache._coverageCount`)。

**誰觸發 notify**：emulator (在 bus.WriteByte 跟 IR-level inline RAM write
路徑)。

**邊界微妙處**：block-JIT 的 IR-level inline RAM write (P1 #7) 走 host C# 的
bus 路徑，但 framework 也有 IR-level coverage check + slow-path notify
(`EmitSmcCoverageNotify`，env-gated)。**Emulator 端的 NotifyMemoryWrite call
跟 framework 端的 IR-level inline notify 兩條路徑都要存在**，coverage 才完整。

### 3.6 IRQ delivery sequence — 純框架責任

```csharp
// emulator 的 SystemRunner 主迴圈：
while (running) {
    cpu.Step();
    if (bus.IrqPending && (cpsr & I_BIT) == 0)
        cpu.DeliverIrq();   // framework 內部處理 mode swap、PC、SPSR
}
```

**誰決定 IRQ pending**：emulator (透過 `bus.IrqPending` 檢查 `IF & IE`)。

**誰決定該不該 deliver**：emulator + framework 共同 — emulator 看 pending、
framework 知道「現在是不是 instruction boundary」「I-bit 是否 enable」。

**誰執行 delivery**：framework (`CpuExecutor.DeliverIrqIfPending`：save
SPSR_irq、CPSR mode → IRQ + I-bit、bank swap、R14_irq = PC + 4、PC = 0x18)。

**Emulator 不該做**：自己手動寫 R14、PC、CPSR 模擬 IRQ entry。**這是 ARM7TDMI
spec 行為，屬框架責任**。

### 3.7 Pinned RAM base pointer — block-JIT IR-level inline 用

```csharp
// emulator 的 Reset(bus) 階段：
_wramHandle = GCHandle.Alloc(bus.Wram, GCHandleType.Pinned);
_rt.BindExtern("lr35902_wram_base", _wramHandle.AddrOfPinnedObject());
//                                  ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
//                                  pinned address — IR baked as constant
```

**誰 pin**：emulator (用 GCHandle.Alloc Pinned)。

**誰 bind**：emulator (透過 framework 的 `HostRuntime.BindExtern`)。

**誰用**：framework 的 IR emitter (`Lr35902Emitters.EmitWriteByteWithSyncAndRamFastPath`)。

**邊界重點**：framework 不管理 byte array 的 lifetime 或 GC pinning — 那是 host
C# 的事，emulator 必須保證在 framework JIT'd code 跑期間 pin 還在。

---

## 4. Walk-through — 一次 frame 跑下來各方參與

以 GBA 為例，描述「跑一個 emulated frame」時 framework 跟 emulator 怎麼互動：

```
emulator                              framework (AprCpu)
========                              ==================

1. SystemRunner.Step() 開始
   |
2. budget = scheduler.CyclesUntilNextEvent
   bus.cycles_left = budget                    <-- framework 從 state 讀
                                                   進 block 開始扣
3. cpu.Step()  ----------------------------> 4. block-JIT cache lookup PC
                                                |
                                             5. cache hit -> jump to fn
                                                cache miss -> compile new block
                                                |
                                             6. block 跑 N 條 instr
                                                每條 instr deduct cycle cost
                                                cycles_left ≤ 0 -> exit
                                                or PcWritten=1 -> exit
                                                |
                                             7. block fn returns
                                                framework 從 state 讀
                                                cycles_left, PC, PcWritten
                                                |
                                             8. (per-instr) bus.Tick(stepCycles)
                                                                        <-- framework call emulator
9. (block-JIT)
   consumed = budget - cycles_left
   scheduler.Tick(consumed)
   |
10. scheduler 自己處理 PPU/Timer 推進
    HBlank entry -> bus.RaiseInterrupt(HBlank)
    Timer overflow -> bus.RaiseInterrupt(Timer)
    |
11. dma.TickIfTriggered()
    (HBlank/VBlank-triggered DMA fire)
    |
12. cpu.DeliverIrqIfPending()  --------> 13. framework 看 bus.IrqPending && I-bit
                                                IRQ entry sequence:
                                                save SPSR_irq, mode swap,
                                                bank swap, R14, PC=0x18
                                                |
14. SystemRunner.Step() 結束
    回 main loop 繼續下個 Step
    或這個 frame 跑完了，render PPU 框
```

**參與位置整理**：
- step 1, 2, 9, 10, 11, 14：emulator-only
- step 4, 5, 6, 7, 13：framework-only
- step 3, 8, 12：cross-boundary call

**Catch-up 路徑（mid-block MMIO read/write）**插在 step 6 內部：
```
6. block 跑 instr
   遇到寫 MMIO addr
     -> bus.WriteByte (extern call out of JIT'd code)
        emulator 端：tick scheduler 補齊 PPU 到「現在」 (step 9 提早做)
        emulator 端：執行真正的寫
        emulator 端：return sync flag = 1 if IRQ-relevant
     -> framework 收到 sync=1
        write next-PC + PcWritten=1 + ret void
        block 提前 exit
```

---

## 5. 「責任邊界錯了」的失敗模式

下面是 cross-boundary contract 違反時會發生什麼，跟錯在哪邊：

| 症狀 | 原因 | 該誰修 |
|---|---|---|
| DIV register 永遠 0 | emulator scheduler 沒 Tick / 沒推 Timer | emulator |
| Block-JIT 比 per-instr 慢一倍 | emulator scheduler.CyclesUntilNextEvent 永遠回 1（沒讓 budget 攤出去） | emulator |
| 寫 LCDC.0=0 後下條 instr 還看到 LCD on | emulator bus.WriteByte 沒做 catch-up | emulator |
| IRQ 永遠不 deliver | emulator bus.IrqPending 永遠 false（IF/IE 沒設） | emulator |
| IRQ deliver 之後 PC 跳錯 vector | framework 的 IRQ entry sequence 處理錯 | framework |
| EI 之後立刻被 IRQ 打斷（應該晚 1 instr） | framework `defer` micro-op 沒處理 EI | framework (spec) |
| Self-modifying ROM 跑出 garbage | framework BlockCache 沒 invalidate | framework — 或 emulator 沒 call NotifyMemoryWrite |
| Pipeline-PC reads 看到錯值 | framework Strategy 2 PC 沒處理對 | framework |
| 商業遊戲的 audio 完全沒聲音 | emulator 沒實作 APU | emulator |
| 跑 Pokemon RBY 撞到 invalid opcode | emulator MBC3 沒實作（看到 bank 0 而非真實 bank） | emulator |

**規則**：HW model 的問題 → emulator；CPU model + 指令拍子的問題 → framework。

---

## 6. 想接新平台 / 新 CPU 的人怎麼用這份 doc

### Case A — 加新 emulator (例如 NES，CPU 已有 6502 spec)

你要做的：

1. 寫 `NesMemoryBus` 實作 `IMemoryBus`（NES memory map：CPU RAM / PPU regs /
   APU regs / cart）
2. 寫 `NesScheduler`（PPU 拍子：每 3 PPU dot = 1 CPU cycle、scanline 計數、
   NMI on VBlank entry）
3. 寫 `NesPpu`（NES 的 256×240 framebuffer、background nametable、sprite OAM
   evaluation、scrolling registers $2005/$2006）
4. 寫 `NesApu`（5 channel：square × 2 + triangle + noise + DMC）
5. 寫 `NesSystemRunner`（loop: `cpu.Step → scheduler.Tick → DeliverNmi`）

**框架不用動**。NES 6502 的 spec JSON 寫好就直接 work。

### Case B — 加新 CPU (例如 RISC-V，目前沒有 spec)

你要做的：

1. 寫 `spec/riscv/cpu.json` (register file、condition codes、exception model)
2. 寫 encoding-format groups under `spec/riscv/groups/`（R-type / I-type /
   S-type / B-type / U-type / J-type）
3. 看 spec 是否需要新 micro-op；如果是真的新行為 (例如 RISC-V 的 fence.i)，
   先試 spec-level；spec 表達不出來才考慮加 framework primitive
4. 寫 unit test 覆蓋 spec 解碼跟基本 ALU
5. 接新平台 emulator (如 NES 上的 RISC-V 模擬就只是 spec 換掉)

**Framework 改動最小化**。**Emulator 改動為零**（如果借用既有 platform）。

### Case C — 加新 timing 行為 (framework 級)

例如：「我要加 `cycle_count` 類型的 defer」。流程：

1. 確認這真的不是某個平台 specific（兩個以上 CPU / 平台會用？）
2. 加 framework primitive（spec 欄位、micro-op 名字、IR emit 邏輯）
3. 進 [`MD/design/15-...md`](/MD/design/15-timing-and-framework-design.md) 紀
   錄為什麼加、屬 Pattern A/B/C 的哪個
4. 進 unit test 覆蓋
5. 找一個現存 spec 採用、避免 dead code

---

## 7. 這份 doc 的維護準則

- **新 framework primitive 進 doc 表格**（§2 + §3）— 每加一個 spec micro-op
  / extern callback 都要對應一行
- **Emulator 端設計 pattern 不進這 doc** — 那進 platform-specific 的 doc
  （例如 GBA 的 PPU 細節進 [`MD/design/16-emulator-completeness.md`](/MD/design/16-emulator-completeness.md)）
- **Walk-through (§4) 過時要修** — 新增 sync 點 / catch-up 點時更新
- **§5 失敗模式表是給 debug 用** — 撞到新 cross-boundary bug 要 append

---

## 8. Reference

- [`MD/design/12-gb-block-jit-roadmap.md`](/MD/design/12-gb-block-jit-roadmap.md) — block-JIT 進度跟 P0/P1 機制
- [`MD/design/13-defer-microop.md`](/MD/design/13-defer-microop.md) — defer micro-op 設計（framework primitive 範例）
- [`MD/design/14-irq-sync-fastslow.md`](/MD/design/14-irq-sync-fastslow.md) — sync micro-op + bus extern split（cross-boundary 範例）
- [`MD/design/15-timing-and-framework-design.md`](/MD/design/15-timing-and-framework-design.md) — 三大架構 pattern 跟 9 個通用化方法
- [`MD/design/16-emulator-completeness.md`](/MD/design/16-emulator-completeness.md) — emulator 外殼完成度盤點
