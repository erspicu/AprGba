# 進階 PC-emulator 測試工具（reference notes）

當基本 test path（NASM `.COM` + Port 0xE9 + AutoTester，看
[`03-dos-test-injection-workflow.md`](03-dos-test-injection-workflow.md)）
不夠時 — 即我們想驗 **non-deterministic** 或 **hardware-state-machine**
行為，像 x87 FPU、PIT/PIC/DMA chip 或 80186-specific PCB register —
這些是 field-tested 工具跟策略。當作參考留著、之後時間到了不用再壓力下
重推。

> **狀態**：AprPc 還沒採用。本文件是 forward-looking shopping list。
> 章節 tag 為 🟢 ready-to-pull-in、🟡 partially blocked（需要其他
> plumbing 先）、或 🔴 long-term。

## 1. x87 (8087) FPU 驗證

8087 難測因為它有自己的 80-bit stack register file (ST0..ST7)、獨立的
rounding mode（control word 的 RC bit）、status word 的 exception flag、
精度極值下難用眼判斷的 instruction（`FPTAN`、`FYL2X`、`FSIN`）。

### 1.1 🟢 `x87-test-suite`（Intel test vector、社群維護）

- **哪裡找**：GitHub 搜 `x87-test-suite`；MAME 的 `src/devices/cpu/i86/`
  test fixture 有時也 embed 類似 vector。
- **資料形狀**：巨大 JSON / binary file，每 row 是
  `[initial stack, control word, status word] → [instruction]
  → [expected ST0..ST7, status word, exception flag]`。
- **抓什麼**：`FPTAN`、`FYL2X`、`F2XM1` 跟超越家族的 corner case；
  round-to-nearest-even tie；subnormal / NaN / ±∞ 傳播。
- **AprPc 整合成本**：低 — 跟我們已經用來測 8086 整數的 Tom Harte
  8088 SST loader（`X86TomHarteTests.cs`）一樣的 harness 形狀。
  新 xUnit class `X87TomHarteTests.cs` parse JSON、透過 `X86JsonCpu`
  驅動 FPU。無 Windows / DOS 依賴 — 純 unit-test 層。
- **建議觸發時機**：加新 x87 instruction 或碰 `X86Fpu` rounding logic
  時。目前 x87 functional baseline 在
  [`MD/performance/202605160100-x87-fpu-functional-complete.md`](../performance/202605160100-x87-fpu-functional-complete.md)
  收尾，這是 *下一* 層 up。

### 1.2 🟡 IBM PC/XT 8087 Diagnostic Disk

- **哪裡找**：Vetusware / Internet Archive — 搜
  `"IBM PC/XT 8087 Diagnostic"`。通常是 `.IMG` floppy image。
- **怎麼 work**：可開機 DOS 時代 diagnostic、端到端 stress 每個 8087
  instruction、印結果到螢幕、失敗時帶特定 error number halt。
- **AprPc 整合**：mount 為 `--floppy-a=...`、無特殊 flag。Output 透過
  active 的 video adapter。
- **抓什麼**：整合 bug（FPU + IRQ wiring on XT — 8087 透過 NMI raise
  IRQ 13，我們目前不 emulate）；interrupt 下的 NaN / infinity 處理；
  control-word exception masking。
- **今天的 blocker**：diagnostic 可能期待透過 NMI 的 8087 IRQ wiring
  （XT-specific）；我們現在 FPU 整合不產 IRQ。下載前花一小時先讀
  diagnostic source / disassembly 看實際 probe 什麼值得。

## 2. I/O bus + 周邊 chip 測試

手動驗 support chip（8253 PIT、8259 PIC、8237 DMA、MC146818 RTC、
8042 keyboard）的 `Read8(port)` / `Write8(port, val)` 很快累。
這些 DOS 時代 diagnostic 幫我們做。

### 2.1 🟢 Landmark System Speed Test（v2.0 – v6.0）

- **哪裡找**：My Abandonware、各種 DOS shareware archive。
- **做什麼**：繞過 DOS、從 ring-0 driver 直接 probe 硬體。著名的
  「MHz score」是用已知 divisor 程式化 PIT channel 2、busy-loop 已知
  instruction count、然後讀 latched counter 算出來。
- **對我們最強信號值**：
  - **PIT 8253**：如果 `OUT 0x43` mode-set / latch + `IN 0x40`
    counter-read 不 bit-exact、MHz score 出 0 或 test hang。
  - **VRAM bandwidth**：用 `rep stosw` 猛打 `0xB8000` / `0xA0000`、
    計時。會浮現 single-instruction test 看不到的 FDC/DMA / CGA-status-port
    耦合 bug。
- **AprPc 整合**：等我們有快速辦法把 `.COM` + 小的 AUTOEXEC.BAT
  wrapper 推到既有 FreeDOS A: floppy 跑 `B:\LANDMARK.COM` 之後，
  mount 到 B:（`--floppy-b`）。
- **First-pass 成功標準**：不是報告 MHz 的 *正確性* — 那反正綁 host
  wall-clock — 而是 **跑完沒 crash / hang / "?MISMATCH"**。

### 2.2 🟢 CheckIt 3.0 / 4.0 for DOS

- **哪裡找**：跟 Landmark 同 archive。（原 publisher 是 TouchStone；
  早就 abandon 了。）
- **為什麼是我們 use case 最重的**：
  - **System Evaluation → Interrupt Test**：主動觸發每條 IRQ line、
    驗 PIC 在對的 priority 把它 route 到對的 INT vector。測 IMR
    （`OUT 0x21`）masking、in-service register (`ISR`)、end-of-interrupt
    (`OCW2 0x20`) handshake。**這是唯一會說「你 PIC 有 bug X」、
    不只是「something didn't work」的工具**。
  - **DMA test**：程式化 ch1 / ch2 / ch3 transfer、驗 page-register +
    count-register + transfer-mode 行為。會抓我們在 30.3-30.5 撞到那種
    mis-wired flip-flop。
  - **CMOS / RTC test**：真的讀 day / hour / minute back 並驗推進。
    我們 Phase 30.7 追的 `ver` time-display hang（BCD-vs-binary CMOS
    read）跑一次 CheckIt 就抓到了。
- **AprPc 整合**：跟 Landmark 一樣。
- **🟡 Full pass 的 blocker**：需要 **slave PIC at 0xA0**（我們今天
  master-only — `src/AprPc.Cli/Hardware/Pic8259.cs:27` 是 XT-class
  single PIC）。CheckIt 的 interrupt cascade test 如果 IRQ8-15 不 route
  會拒絕進行。看下面 §4。

## 3. 80186-specific 測試 — MAME PCB model

80186 / 80188 把 PIT + PIC + DMA 整進 *CPU 內*，當 **Peripheral Control
Block (PCB)**，預設 map 到 I/O `0xFF00`（透過 `0xFFFE` 的 RELREG
register 可重定位）。這 *不是* IBM PC layout、標準 XT 硬體上 DOS 時代
任何東西都不會 exercise。80186 用在街機板。

### 3.1 🟢 MAME `i186.cpp` PCB unit test

- **哪裡**：<https://github.com/mamedev/mame/tree/master/src/devices/cpu/i86>、
  特別是 `i186.cpp` + exercise 它的 test harness。
- **為什麼**：MAME 生產上跑 80186-based 街機板（90 年代 fighter、shmup）。
  他們的 PCB model 最接近 reference 實作、unit test 把預期 I/O 序列
  （timer reload、IRQ priority、DMA chain mode）一個 PCB register 一個
  地文件化。
- **整合 path**：不要 re-port 他們的 C++ — 就 **挖 test vector**。
  每個 test 都是 `OUT <pcb_reg>, <val>; expect <side effect>`。
  翻譯到 xUnit：
  ```csharp
  [Fact]
  public void Pcb_Timer0_ReloadOnZero()
  {
      var pcb = new I186Pcb();
      pcb.WriteIO(0xFF50, 0x0010);   // T0_COUNT = 16
      pcb.WriteIO(0xFF56, 0xC001);   // T0_MODE: ENA + retrigger
      for (int i = 0; i < 17; i++) pcb.Tick();
      Assert.True(pcb.Timer0Wrapped);
  }
  ```
- **這裡的狀態**：AprPc 目前跑 i80186 / i80188 為 i8086 + 26 個新
  instruction。PCB 還沒實作 — 跟 PCB 對話的 i80186 binary 會讀到 0xFF。
  之後要跑真的 80186 board ROM 時採用。

## 4.「最小成本 in-house mock」pattern

對於拉進 DOS 時代 diagnostic 太 overkill 的情況、寫 xUnit test 直接
驅動 device class：

```csharp
// PIT 8253 latch-command shape (Phase 28.IO regression net)
[Fact]
public void Pit8253_LatchCommand_PreservesCounter()
{
    var pit = new PcPit();

    // Counter 0 in mode 3, programmed to N
    pit.WriteIO(0x43, 0x36);          // ctrl = counter0, lo/hi, mode3, binary
    pit.WriteIO(0x40, 0xFF);          // count lo
    pit.WriteIO(0x40, 0xFF);          // count hi

    pit.Tick(50);                     // 50 host cycle elapse

    // Latch live count 到 snapshot register
    pit.WriteIO(0x43, 0x00);          // 0x00 = counter0 latch (no R/W bit)

    byte lo = pit.ReadIO(0x40);
    byte hi = pit.ReadIO(0x40);
    ushort latched = (ushort)((hi << 8) | lo);

    Assert.InRange(latched, (ushort)(0xFFFF - 60), (ushort)(0xFFFF - 40));
}
```

Pros：無 DOS 依賴、跑 <100 ms、regression-lock 進 CI。
Cons：只測獨立 device — 不會抓 CheckIt 找到的 **inter-chip** wiring
bug（PIT → IRQ 0 → PIC → CPU → INT 8h handler）。

**Rule of thumb**：device 成熟時對 device class 每個 public method
寫 xUnit mock test；CheckIt / Landmark 留給 phase 邊界的 inter-chip /
全 stack confidence pass。

## 5. 跑 CheckIt 的 prerequisite：0xA0 的 cascade slave PIC

`src/AprPc.Cli/Hardware/Pic8259.cs` 目前 **master-only**（IRQ 0-7）。
XT machine 只有一個 PIC、match pcxtbios.bin 世界。但：

- CheckIt 的 interrupt test exercise IRQ 8-15（slave PIC route 到
  master IRQ 2）。沒 slave plumbing 會 fail / hang。
- 80186 PCB IRQ routing 是 internal、完全繞 PIC — 所以那條 path
  *不* 需要 slave。
- AT-class BIOS 在 0xA0/0xA1 沒 slave response 連 POST 都不會跑。

我們想跑 CheckIt 或任何 AT-class ROM 時，cascade 工作是 gating
prerequisite。粗略 scope：
- `Pic8259.cs` 加 `IsSlave` flag + `Cascade` reference。
- 接兩個 instance：master at 0x20/0x21、slave at 0xA0/0xA1。
- IRR / ISR / IMR / OCW2 / OCW3 / ICW1-4 handshake。
- Slave 的 IRQ 8-15 透過 master IRQ 2 bump。
- 處理 spurious IRQ 7 / IRQ 15。

估時：2-3 天。不擋目前任何 Phase 30 deliverable、延後到 Phase 30.x
sprint 明確 target AT-class 相容或跑 CheckIt 作為 bring-up test 時做。

## 6. 建議採用順序

1. **🟢 x87 test vector**（§1.1）— Drop-in xUnit；每 hour 投入抓
   precision bug value 最大。下次碰 FPU code 時做。
2. **🟢 PIT 8253 / PIC / DMA xUnit mock**（§4）— 趁實作還新 backfill。
   對未來 refactor 的便宜保險。
3. **🟡 B: floppy 上的 Landmark Speed Test**（§2.1）— 好玩的 smoke
   test、確認 test-injection pipeline 到達真實 DOS 時代軟體。不需要
   新 emulator 工作。
4. **🟡 Slave PIC cascade**（§5）— CheckIt 跟 AT ROM 必需。當 Phase 30.x 規劃。
5. **🟡 CheckIt full pass**（§2.2）— 依賴 (4)。「你 PIC 90% 對」的
   headline test。
6. **🔴 透過 MAME vector 的 80186 PCB**（§3）— 只在我們要跑 80186
   街機板 ROM 時。還沒商業案例。

## 參考

- [`MD/process/03-dos-test-injection-workflow.md`](03-dos-test-injection-workflow.md) —
  本文件建立其上的底層 test-injection plumbing
- [`MD/performance/202605160100-x87-fpu-functional-complete.md`](../performance/202605160100-x87-fpu-functional-complete.md) —
  目前 FPU baseline；§1.1 從這裡接續
- `src/AprPc.Cli/Hardware/Pic8259.cs` — Master-only PIC、slave cascade
  在 §5 描述
- `src/AprCpu.Tests/X86TomHarteTests.cs` — 大型 external-test-vector
  xUnit harness 的既有 template（x87 suite 會循同樣形狀）
