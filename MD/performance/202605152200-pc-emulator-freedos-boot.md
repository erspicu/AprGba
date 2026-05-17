# Phase 28 收尾 — AprPc + FreeDOS 1.3 boot

> **收尾**：2026-05-15
> **Scope**：Intel PC emulator (AprPc.Cli) 包 AprX86 i8086 backend，
> 加上 HLE BIOS + 最小 IO controller + WinForms UI。
> **端到端成就**：real FreeDOS 1.3 透過 kernel + COMMAND.COM + AUTOEXEC.BAT
> 開到 FreeDOS LOGO 程式。

## 架構里程碑

JSON-driven CPU framework 現在能端到端跑**商業級 real-mode OS code**。
1.44 MB FreeDOS floppy image 啟動順序：

1. CPU reset vector 在 FFFF:0000 → real-CPU `INT 19h` opcode
2. HLE INT 19h 載入 boot sector 到 0000:7C00 + redirect CS:IP
3. Boot sector 自我搬移（0xEA far jmp）到 1FE0:7C00
4. Boot sector 透過 114 個 INT 13h sector read 載 kernel.sys
5. Kernel 透過 INT 10h teletype 印 3 行 banner
6. Kernel install 自己的 IVT[0x21] DOS API handler
7. Kernel 從 FAT12 directory 讀 CONFIG.SYS / AUTOEXEC.BAT
8. Kernel 載 COMMAND.COM（FreeCom 0.85a XMS_Swap）
9. COMMAND.COM 印 banner
10. AUTOEXEC.BAT 跑 FreeDOS ASCII-art LOGO 程式
11. 大綠色「FreeDOS」logo 渲染在藍底上

最終可見狀態（`result/pc/28.8e-prompt.png`）：

```
FreeCom version 0.85a - WATCOMC - XMS_Swap [Jul 10 2021 19:28:06]

       [Large green ASCII-art "FreeDOS" logo on blue]
```

## 出貨內容 — Phase 28 sprint chain

13 個 micro-sprint，全部 2026-05-11 到 2026-05-15 land。每個 sprint 一個
commit、可獨立 revert。

### 基礎建設（Sprint 28.0 → 28.5）

| Sprint | Commit | Deliverable |
|---|---|---|
| 28.0 | `245729e` | `AprPc.Cli` scaffolding + WinForms UI shell + emulator thread plumbing |
| 28.1 | `35a73ea` | PC memory map（`PcMemoryBus`）+ IVT + BDA + reset vector |
| 28.2 | `413afeb` | HLE BIOS framework + INT 10h + 60Hz framebuffer blt |
| 28.3 | `92d8e11` | INT 16h + 8042 keyboard buffer + WinForms KeyDown queue |
| 28.4 | `ee1098e` | PIT 8253 + INT 1Ah + wall-clock BDA tick |
| 28.5 | `d5cceee` | INT 13h floppy/HDD HLE + `DiskImage` + `.img` loader |

### Boot path（Sprint 28.6 → 28.7）

| Sprint | Commit | Deliverable |
|---|---|---|
| 28.6 | `ebbcaab` | INT 19h bootstrap（partial-LLE）+ 自己寫的 boot sector |
| 28.7 | `8c8f2b6` | Pic8259 + IRQ delivery model（PIT IRQ 0 + 鍵盤 IRQ 1） |

### FreeDOS boot（Sprint 28.8a → 28.8e）

| Sprint | Commit | Deliverable |
|---|---|---|
| 28.8a | `e71f15a` | i8086 spec 加 0xEA（JMP ptr16:16）— 解開 boot sector 自我搬移 |
| 28.8b | `9012790` | Install HLE INT 8/9 default — 修 IRQ 漂到 0:0 |
| 28.8c | `7d7bda3` | RETF（0xCB/0xCA）+ CALL far（0x9A）+ pre-install 全 256 IVT default |
| 28.8d | `837ede2` | FreeDOS kernel 完整 3 行 banner 印出 |
| 28.8e | `7a3b8ef` | COMMAND.COM（FreeCom）+ AUTOEXEC.BAT + FreeDOS LOGO 印出 |

## Phase 28 期間 i8086 spec 新增

Phase 28 關掉 i8086 spec 在 `spec/cpu/x86-16/i8086/groups/control-flow.json`
裡記載的「deferred」gap 中的三個：

| Opcode | Name | Phase | 原因 |
|---|---|---|---|
| 0xEA | JMP ptr16:16（far direct） | 28.8a | FreeDOS boot sector 自我搬移 |
| 0x9A | CALL ptr16:16（far direct） | 28.8c | 主動（FreeDOS kernel call device driver） |
| 0xCB | RETF | 28.8c | FreeDOS kernel push-then-retf far jump |
| 0xCA | RETF imm16 | 28.8c | 0xCB 的同伴 |

每個 add 都是 3 部分改動：length-oracle case、spec entry、新 emitter class。
每個 micro-sprint commit 都包含 regression 驗證（T2 visual matrix + variant
matrix 不變、先前 PC demo byte-identical）。

## INT handler 數量

HLE BIOS 現在提供下列 vector 的 handler：

| INT | Subset | 什麼 |
|---|---|---|
| 0x08-0x0F | 全 8 個 | IRQ 0-7 預設 IRET（PIT、keyboard 等） |
| 0x10 | AH=00/02/03/06/09/0E/0F | Video — set mode、cursor、scroll、char/attr、teletype、get mode |
| 0x13 | AH=00/01/02/03/04/08/15 | Disk — reset、status、read、write、verify、params、type |
| 0x16 | AH=00/01/02 | Keyboard — read（block）、peek、shift flag |
| 0x19 | AH=00 | Bootstrap — load sector 0 + jump |
| 0x1A | AH=00 | Time — get ticks since midnight |
| 0x1B、1C、1E | 預設 IRET | Ctrl-Break、user timer、FDPT（dummy） |
| 0x00-0xFF（其他 251 個） | 預設 IRET | 預先 install 的 no-op trap；user code override |

「Pre-install 全 256」做法（Phase 28.8c）是關鍵的 robustness 修補。FreeDOS
意外 INT 到我們從沒明確 handle 的 vector（INT 11h equipment、INT 12h memory、
INT 17h printer 等等）全部靜默 IRET、不會在 `IVT[v]=0:0` crash。

## Phase 28 期間 inheritance ROI

Phase 28 沒加新的 CPU spec inheritance level。i8086 spec 多了 4 個 opcode
（~80 行）跟一樣數量的 emitter class（~120 行 C#）。沒新 CPU 加入。

| Component | Phase 28 加的行數 |
|---|---|
| `spec/cpu/x86-16/i8086/groups/control-flow.json` | ~80（3 entry） |
| `src/AprCpu.Core/IR/X86_16Emitters.cs` | ~120（3 emitter） |
| `src/AprCpu.Core/Runtime/X86_16InstructionLengths.cs` | 3 行 |

Phase 28 其他 ~2000 行工作都在 `src/AprPc.Cli/` 底下 — 純 application code
（memory bus、BIOS、IO controller、UI）。**Application code 是這個工作該在的地方**；
framework 本身不需要 protected mode v2 或任何 spec-driven 擴充。

## Demo as artifact

```
result/pc/
├── 28.2-hello.png              ← INT 10h teletype "Hi"
├── 28.3-echo.png               ← INT 16h read + echo "Hi AprPc!"
├── 28.4-tick.png               ← INT 1Ah read return "OK"
├── 28.5-int13.png              ← INT 13h read FreeDOS boot sector → "OK"
├── 28.6-bootstrap.png          ← 自己寫的 boot sector "AprPc bootstrap OK"
├── 28.7-irq.png                ← User INT 8 handler counter "IRQ OK 3"
├── 28.8-attempt1.png           ← First FreeDOS attempt（pre-28.8a、2 dot）
├── 28.8a-attempt.png           ← 28.8a 結果（CPU 推進過 0xEA）
├── 28.8b-attempt.png           ← （block-JIT INT bug — 空螢幕）
├── 28.8b-perinstr.png          ← FreeDOS bootstrap "..." dot line（93 dot）
├── 28.8c-attempt.png           ← 28.8c 第一次嘗試
├── 28.8c-attempt2.png          ← 28.8c 第二次嘗試
├── 28.8c-attempt3.png          ← Kernel banner 第一行印出
├── 28.8d-banner.png            ← 完整 3 行 kernel banner
├── 28.8e-with-keys.png         ← FreeCom 0.85a banner
└── 28.8e-prompt.png            ← FreeDOS ASCII-art LOGO ★
```

11 個不同的「PC 又開了一點」階段視覺捕捉。

## 已知 issue / 延後

### Block-JIT 漏 INT dispatch (28.8x)
`--backend=json-block` 下，compile 進 JIT'd block 的 INT 指令不會 surface
到 emulator-thread 的 IsTrapped() check（在 Step() 呼叫之間）— trap 在
block 內消耗掉、但 HleBios.Dispatch 不會跑。Workaround：FreeDOS 用
`--backend=json`（per-instr）。Block-JIT INT emitter 需要 audit；可能的
修法是強制 INT 指令 PcWritten=1 讓 block 在 INT 那裡退出，dispatcher 從
F000:00xx 取下個 block，emulator-thread 看到 trapped。

### 互動式 A:\\> prompt (28.8f)
AUTOEXEC.BAT 跑完 FreeDOS LOGO 程式之後螢幕停在 LOGO。可能 LOGO 程式 loop
等 key + 我們的 --keys script 到不了它（被早期 poll 吃掉）、或我們的
wall-clock cycle budget 用完之前 LOGO 還沒退出到 COMMAND.COM prompt。
兩個修法都是機械化：
1. 把 Console.In stdin pipe 接到 emulator 的鍵盤 queue（`PcKeyboard.Enqueue`
   from a host stdin reader thread）。
2. 或者 HLE INT 16h AH=00 在 N ms 沒輸入時 timeout return ESC。
一旦 prompt 出來，INT 16h 已經接好、dir/type/cls/ver 應該「就 work」，
透過既有的鍵盤 buffer + INT 21h DOS call（FreeDOS 提供）。

### 滑鼠 (28.10) 跟 PC speaker PCM (28.11)
原 plan 標 optional。沒 FreeDOS-on-floppy demo 依賴；延後。

## Phase 28 成就總結

- **Real FreeDOS 1.3 kernel 端到端 boot** on JSON-driven CPU framework。
  跑 Phase 27b 5-ROM fault matrix 的同個 i8086 spec + JIT pipeline 現在跑
  商業級 1990s 時代的作業系統 code。
- **Application layer 是對的邊界**：~2000 行 `AprPc.Cli`（BIOS / IO / UI）
  坐在 ~3500 行 spec-driven CPU 上。不需要 framework-level invention。
- **三個「deferred」i8086 opcode（far jmp/call、retf）自然 land**，
  當 FreeDOS 練到時。Spec-driven design 讓每個都是 3-file 改動、
  regression-clean diff。
- **無 regression**：T2 18 PNG + variant matrix + Phase 27b 5-ROM fault
  matrix 跨 28 個 commit 全部不變。

> Phase 28 在「FreeDOS LOGO 印出」這個里程碑收尾。剩下的 interactive
> shell + 4 個 command screenshot（28.8f / 28.9）是機械化 follow-up、
> 不需要 CPU 或 framework 工作。Phase 28 framework-genericity 宣稱
> **達成**：JSON-driven CPU emulation framework 到達商業級 real-mode OS
> 相容性。
