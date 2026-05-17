# Real-BIOS 鍵盤 pipeline 收尾 — GUI 端到端 FreeDOS 互動 prompt

**日期**：2026-05-16  
**觸發**：使用者要求在 Phase 30 real-BIOS path 出貨後測試 GUI mode，順便回頭做掉 deferred 的 Phase 28.8f（互動式 `A:\>` prompt）。  
**狀態**：✅ GUI 啟動、real BIOS POST + FreeDOS boot + 語言選單 + LOGO + AUTOEXEC.BAT + installer prompt + 互動式 `A:\>` 全部端到端可用。使用者可以打 command、DOS 會 echo 回顯。

**Phase**：28.8f / 30.7（互動 prompt 是 Phase 28 deferred 的 polish 項目；解鎖它的 FDC + keyboard 工作屬於 Phase 30）。  
**前置**：Phase 30 ROL fix 收尾（`202605161800-phase-30-real-bios-freedos-boot.md`）。

## 結果

```
gui-test.bat realbios
  ↓
[GUI window：AprPc - real BIOS pcxtbios.bin]
  ↓
pcxtbios.bin POST → INT 19h 透過 emulated FDC/DMA → boot sector → FreeDOS kernel
  ↓
語言選單（按鍵透過 real BIOS INT 9 → BDA → INT 16h）
  ↓
FreeDOS 綠色 ASCII LOGO
  ↓
AUTOEXEC.BAT → FreeDOS 1.3 installer welcome "Do you want to proceed [Y,N]?"
  ↓
使用者按 N → installer aborted → A:\> prompt
  ↓
使用者打 "dir" + Enter → DOS 印出 "Volume in drive A is FD13-BOOT"
```

最後 screenshot：`pic/2.png`（gitignored — user-local debug capture）。

仍不 work 的：`dir` 印完 volume label 之後 hang 在讀 FAT/root directory。
Boot sector + KERNEL.SYS + COMMAND.COM 全部透過 FDC 載入成功，所以基本
INT 13h read 正常；DOS file-system layer 用到的某個 FDC command 或
multi-sector parameter 組合需要 investigation。追蹤為 Phase 30.8。

## 找到 10 個 sub-bug

每個都獨立 block 整條 chain。按發現順序列：

| # | Sub-bug | 位置 | 症狀 |
|---|---|---|---|
| 1 | `MainForm.cs` 第 83 行設 `ToolStripMenuItem.ShortcutKeys = Keys.PrintScreen`，但 `Shortcut` enum（`ShortcutKeys` 拿來驗證的）是 `Keys` 的 subset、不包含 PrintScreen。 | UI ctor | GUI 啟動 → unhandled `InvalidEnumArgumentException` → 預設 .NET error dialog → process 死。改成 `Keys.F12`。 |
| 2 | `AllocaSlotProvider.RegisterStatus` 對 `WidthBits` 的 switch 沒有 case 64 — 但 Phase 29.1 對 i8087 extension 加了 64-bit `FPU_ST0..ST7` slot。任何 block-JIT compile 碰到 status reg（`BlockFunctionBuilder` 一開始就把全部都 register）都會 throw `NotSupportedException` crash。 | `src/AprCpu.Core/IR/IStateSlotProvider.cs` | Block-JIT + Phase 29 spec → emulator thread 在第一個 block compile 就 crash。加 `64 => LLVMTypeRef.Int64` case。**註**：Block-JIT IR 對 64-bit FPU stack 雖然 structurally 不 crash 但 semantics 還是錯；HLE + real-BIOS 鍵盤 demo 在這裡用 `--backend=json`（per-instruction）。Block-JIT + FPU correctness 是另一個 Phase 30.x 或 29.x ticket。 |
| 3 | `MainForm.RefreshFromRunner` 呼叫 `X86CgaRenderer.RenderToRgbBytes(bus.Memory.Ram)`，用的是 backward-compat overload、預設 CGA framebuffer（0xB8000）。Real BIOS POST 寫到 MDA（0xB0000）。 | `src/AprPc.Cli/Ui/MainForm.cs` | GUI canvas 全黑、即使 real BIOS POST 已經在寫 memory。改用 `PickFramebufferBase` auto-detection（mirror headless screenshot 用的 `X86CgaRenderer.Render`）。 |
| 4 | `Program.cs` GUI path 呼叫 `runner.Start(); runner.Resume();` 中間沒 `MountDisk`。HeadlessRunner 在 Start 之後 Resume 之前透過 `runner.MountDisk(0x00, disk)` mount floppy。 | `src/AprPc.Cli/Program.cs` | GUI real-BIOS mode 在 INT 19h boot attempt 時 hit FDC NRDY（no media），因為從來沒掛 disk。從 `HeadlessRunner.Run` 把 mount sequence 複製過來。 |
| 5 | `HleBios.Install()` 在 `PcSystemRunner.Start` 無條件跑、pre-populate IVT entry 0x00-0xFF 都指到 `F000:00xx`（HLE trap segment）。Real-BIOS 模式下，real `pcxtbios.bin` POST 預期 boot 時 IVT 是 zero、只對它在意的 vector install entry。INT 9 是 POST 沒重 install（或晚才 install）的其中一個。結果：IRQ 1 → vector 0x09 → IVT[9]=F000:0009 → `HleBios.Dispatch(9)` 預設 IRET no-op。Scancode 從 port 0x60 讀進來但永遠到不了 BDA。 | `src/AprPc.Cli/PcSystemRunner.cs` | Real-BIOS path 鍵盤輸入靜默 drop。用 `if (_options.BiosPath is null)` 把 `HleBios.Install()` gate 掉。 |
| 6 | `PcKeyboard.Enqueue` 直接寫進 BDA ring buffer 在 0x0041E、pulse IRQ 1。這是 HLE BIOS 的 contract — HLE INT 16h 讀 BDA。對 real BIOS 正確的 plumbing 是：scancode → 8042 port 0x60 → IRQ 1 → real BIOS INT 9 ISR 讀 port 0x60、用 in-ROM table 把 scancode 翻成 ASCII、自己寫 BDA。直接 BDA write bypass 掉 BIOS 自己的 scancode-to-ASCII 步驟。 | `src/AprPc.Cli/Hardware/PcPortBus.cs` + `Ui/MainForm.cs` | 加 `PcPortBus.InjectScancode(byte)` 把 scancode 推到 port 0x60 後面的 8-deep host-side FIFO、設 8042 status bit 0 (OBF)、assert IRQ 1。`MainForm.KeyPress/KeyDown` 根據 `options.BiosPath` 分流。 |
| 7 | 8042 status port (0x64) bit 0 = Output Buffer Full (OBF) 在我們 stub 裡永遠是 0。某些 BIOS INT 9 ISR 會在讀 port 0x60 之前 poll bit 0；若 clear 就把這個 IRQ 當 spurious、return 不寫 BDA。 | `PcPortBus.cs` | `InjectScancode` 時設 bit 0、`Dequeue60` 在 FIFO drain 後 clear。（其實對 `pcxtbios.bin` 不是 blocker — 它的 ISR 直接讀 port 0x60 — 但對其他 BIOS 是無害且正確。） |
| 8 | **XLAT (opcode 0xD7) 在 i8086 spec 沒實作。** `pcxtbios.bin` INT 9 ISR 用 `MOV BX, 0xE885; XLAT CS:` 透過 in-ROM table 翻 scancode。Decoder 對 D7 return null；我們 `StepOne` 的 "unknown opcode" path 把 IP rewind 到 prefix 之前、return -1；dispatch loop 對同 byte 再呼叫 Step 一次 → **在 F000:E9CB 無限 hot loop**。CPU 永遠不從 INT 9 回來、BDA 永遠沒寫、INT 16h 永遠不滿足。 | `spec/cpu/x86-16/i8086/groups/data-transfer.json` + `src/AprCpu.Core/IR/X86_16Emitters.cs` | 加 `Xlat` spec entry（opcode D7, mnemonic XLAT, op `x86_xlat`）跟 `X86XlatEmitter`（AL = byte at [seg : BX + ZExt(AL)]、honour segment override prefix）。Gemini consultation（`tools/knowledgebase/message/20260516_115334.txt`）在我們從 log evidence 收斂後（"caller at F000:E9CB" 重複 41/44 次 = CPU 93% 時間花在這 IP）確認 hypothesis。 |
| 9 | **Unknown-opcode hot loop 是靜默的。** Bug #8 花一小時找是因為症狀是「CPU 卡住、沒任何輸出、沒 exception、沒 log」。 | `src/AprX86.Cli/Cpu/X86JsonCpu.cs` | 加 `OnUnknownOpcode` static callback、對每個 unique (CS:IP, opcode) tuple fire 一次。AprPc.Cli 把它連到 `KbdTrace.Log`，寫 `UNKNOWN_OPCODE 0xXX at CS:IP (CPU will infinite-loop until implemented)`。同 byte 再 hit 會 suppress（HashSet）。下次缺 opcode 的 investigation 會省很多痛苦。 |
| 10 | **`PcSystemRunner` emulator thread 不會在 HLT 時被 IRQ 喚醒。** CPU 執行 HLT 後，dispatcher 進 `if (_cpu is { Halted: true }) { Thread.Sleep(50); continue; }`、IRQ-delivery check 在後面、halted 時 unreachable。Real silicon 在 unmasked IRQ 到時會從 HLT resume。FreeDOS installer 的 INT 16h wait loop 是經典 `STI; HLT` 樣式；沒 wake-on-IRQ 即使 IRQ 1 pending 且 FIFO 有 scancode 也會永遠 sleep。 | `src/AprPc.Cli/PcSystemRunner.cs` + `src/AprX86.Cli/Cpu/X86JsonCpu.cs` | Halted branch 內加 `IF && PIC.DequeueNextVector()` check；若是就 `_cpu.ClearHalted()`（新 expose method）、`DeliverInterrupt(vec)`、continue。同時把 sleep 從 50ms 降到 2ms 整體 parking 更 responsive。 |

## 加的工具

- **`KbdTrace`**（`src/AprPc.Cli/Diagnostics/KbdTrace.cs`）— thread-safe
  file logger 到 `temp/kbd-trace.log`、每次啟動 truncate。Trace 五層：
  UI thread 的 `KeyPress`/`KeyDown`、`PcPortBus.InjectScancode` +
  `Dequeue60`、`Pic8259.AssertIrq(1)` + `DequeueNextVector` IRQ 1、
  `PcSystemRunner.DeliverInterrupt` vec=0x09（記 IVT[9] target + BDA
  head/tail snapshot）、BDA write 在 0x00418-0x00440 範圍透過既有
  `X86JsonCpu.WriteWatch`。加上 unknown-opcode event。這個 audit trail
  讓每個 sub-bug 都能從 log 單獨診斷。
- **`X86JsonCpu.OnWriteWatch` + `OnUnknownOpcode` callback** — 讓
  downstream tool（AprPc.Cli）把 diagnostic event 路到自己的 log，
  AprCpu.Core / AprX86.Cli 不需要 depend on 它。
- **`gui-test.bat`** — repo-root launcher 三 mode：`hle`（per-instr —
  block-JIT + HLE INT trap loss = Phase 28.8x）、`realbios`（per-instr —
  block-JIT + FPU width-64 仍壞、見 bug #2）、`hle-jit`（故意壞的
  HLE+JIT 用來 repro）。每個自動帶對的 `--backend=`、`--bios=`、`--floppy-a=`，
  使用者不用記。
- **GUI `--max-cycles` 自動 exit**（`MainForm.cs`）— cycle budget 到了時
  把 framebuffer render 到 `--screenshot=PATH`、關 form。讓 unattended
  automation 可以走 headless 一樣的 screenshot pipeline。

## 第一輪 `dir` debug 後又找到 6 個 sub-bug

最初收尾 draft 把 `dir` 標成「volume label 之後 hang」。後續 debug round
（Gemini consultations 在 `tools/knowledgebase/message/20260516_135110.txt`
跟 `20260516_140553.txt`）又找到 6 個：

| # | Sub-bug | 位置 | 症狀 |
|---|---|---|---|
| 11 | **每次 INT 13h 都有 FDC motor spin-up 500ms BIOS stall。** Real IBM PC BIOS INT 13h 檢查 BDA motor flag；若 motor off，OUT `0x3F2` 帶 motor bit + stall 500ms 等實體 spin-up。我們 FDC 瞬間完成、但 BIOS 不知道。`dir` ~840 reads × 500ms = 7 分鐘純 stall。 | `src/AprPc.Cli/Hardware/Fdc8272.cs` `WriteDor` | 強制 motor bit 4-7 為 1、不管 BIOS 寫什麼 — "motor always running" 讓 BIOS 跳過 stall。dir/ver ~10× 加速。Per Gemini 2026-05-16 consultation。 |
| 12 | **Edge-triggered IRQ 1 + per-AssertIrq model 把第一個之後的 scancode 全部 strand。** 每個 WinForms key press 同時 fire `KeyDown` AND `KeyPress` → 2 個 InjectScancode call。Edge-triggered PIC：第二個 AssertIrq 看到 pending bit 已設、不會多 IRQ。BIOS 每個 IRQ 只讀 1 個 scancode。第二個 scancode strand 在 FIFO。下一個 key press 觸發 IRQ → BIOS 讀到 stranded char → 使用者看到 1-key lag（按 'a' 沒反應、按 'b' 顯示 'a'）。 | `src/AprPc.Cli/Hardware/PcPortBus.cs` + `Ui/MainForm.cs` | 實作 Gemini 的 "Option C" — port 0x61 ack pulse 驅動 IRQ line state：LOW→HIGH（BIOS 設 bit 7）de-assert line；HIGH→LOW（BIOS 清 bit 7）pop 下一個 FIFO entry + re-assert line 觸發新 8259A edge。Plus 在 real-BIOS mode 完全 suppress `KeyPress`（只用 `KeyDown` 作 source — 加完整 PC XT scancode set 1 map for 字母 / 數字 / 特殊鍵）。 |
| 13 | **HLT 不會被 IRQ 喚醒。** PcSystemRunner emulator-thread dispatch：`if (cpu.Halted) { Sleep(50); continue; }` — IRQ check 在後面、halted 時 unreachable。FreeDOS installer 的 `STI; HLT` INT 16h wait loop hang 住、即使 IRQ 1 已 fire 且 FIFO 有 scancode。 | `PcSystemRunner.cs` | Halted branch 內也檢查 `IF && PIC.DequeueNextVector`；若有 vector，`ClearHalted()`（新 X86JsonCpu method）、`DeliverInterrupt(vec)`、continue。Sleep 從 50ms 降 2ms 讓 no-IRQ-yet path 喚醒更快。 |
| 14 | **GUI mode 在 resume 之前沒 mount disk。** `HeadlessRunner.Run` 在 `Start()` 跟 `Resume()` 之間透過 `MountDisk` mount floppy/HDD。`Program.cs` GUI path 是 `Start(); Resume();` 中間什麼都沒做、real-BIOS INT 19h 立刻 hit FDC NRDY。 | `Program.cs` | 把 disk-mount sequence 複製進 GUI path。 |
| 15 | **`HleBios.Install` 在 real-BIOS mode 還是無條件跑。** Pre-populate IVT 0-255 帶 HLE-trap pointer 到 F000:00xx。Real `pcxtbios.bin` POST 只對它在意的 vector 重 install IVT entry；INT 9 是 POST 沒重 install（或晚才 install）的其中一個。結果：IRQ 1 → vector 0x09 → IVT[9]=F000:0009 → `HleBios.Dispatch(9)` 預設 IRET no-op。Scancode 從 port 0x60 讀進來但永遠到不了 BDA。 | `PcSystemRunner.cs` | 用 `if (_options.BiosPath is null)` 把 `_bios.Install()` gate 掉。Real-BIOS mode 讓 IVT 維持 zero、POST 自己接手。 |
| 16 | **加了週期性 CPU dump 工具。** `dir` 跟 `ver` 看似「hang」時，需要知道 CPU 是真的卡住還是只是慢。在 `MainForm.RefreshFromRunner` 加 3 秒間隔的 CPU state log（CS:IP、flags、GPR、halted 狀態、delta-instr-since-last-tick、same_csip-as-last-tick）。Plus F11 hotkey 做 on-demand snapshot。 | `MainForm.cs` | Dump 證明系統不是 hang — CPU 在 F000 BIOS / 0070 DOS kernel / 06B3 COMMAND.COM / 各種 TSR segment 之間轉、~500K inst/sec emulated、~1-2M delta_instr per 3s tick、`same_csip` 幾乎都 false。**只是慢**、不是卡住。 |

## `dir` 跟 `ver` 最終狀態

兩個 command 都**端到端 work**：
- `ver` 跑（FreeDOS Version banner + time / date）
- `dir` 跑（Volume label + Volume Serial Number + directory listing）

只是兩個都要**幾分鐘才完成**，因為：
1. CPU 在 per-instruction backend 跑 ~500K inst/sec — 比原本 8086 慢。
2. installer 用 `N` abort 後 overwrite 掉 COMMAND.COM 的 transient portion，
   COMMAND.COM 每個 command 之前都要從 disk reload transient（~hundreds of
   FDC reads）。
3. FreeDOS `dir` 走整個 FAT12 算 "bytes free" — ~840 sector reads。

Total：`dir` 要 ~5-10 分鐘 wall clock。CPU 診斷確認全程都在推進、沒卡住。
Phase 30.9 下面追加速工作。

## 沒做的

- **block-JIT + Phase 29 FPU correctness** — bug #2 讓 block-JIT compile
  乾淨、但 FPU_ST0 的 i64 alloca slot 跟 emitter runtime 預期的 f64 layout
  不符。Headless real-BIOS 驗證這點 — 只有 `--backend=json` 印 FreeCom banner；
  `--backend=json-block` 即使把 width-64 slot fix 進去也是黑屏。修這個是
  最大的 interactive-speed 提升（~10-100× 加速預期）。Phase 30.9。
- **Phase 28.8x block-JIT INT trap loss** — 跟 FPU issue 不同。Block-JIT 把
  `INT n` compile 成不會 surface 到 emulator-thread 的 `IsTrapped()` check
  的 IR。HLE BIOS mode 漏掉 INT 10h / 13h / 16h dispatch。Workaround：HLE mode
  用 `--backend=json`。Phase 30.9 fix 會解開 real-BIOS 跟 HLE 兩條 path。
- **W8 ROR/RCL/RCR + W16 ROR/RCL/RCR shift-rotate emitter** — 還是 stub
  著（只支援 count=1）。FreeDOS / pcxtbios.bin 沒打到所以 deferred。
- **PIT > 18Hz override** — 試過 200Hz 但 cause FreeDOS time-of-day 計算
  hang（BDA tick counter 比 DOS 預期快 11×、hit 某個內部 conversion corner case）。
  維持 18Hz 預設；motor-hack 已給足夠加速。

## 交叉參考

- Phase 30 ROL fix 收尾：[`202605161800-phase-30-real-bios-freedos-boot.md`](202605161800-phase-30-real-bios-freedos-boot.md)
- Gemini consultation（XLAT 診斷）：`tools/knowledgebase/message/20260516_115334.txt`
- Phase 28 plan：[`MD/design/28-intel-pc-emulator-plan.md`](../design/28-intel-pc-emulator-plan.md)
- Phase 30 plan：[`MD/design/30-fdc-dma-plan.md`](../design/30-fdc-dma-plan.md)
