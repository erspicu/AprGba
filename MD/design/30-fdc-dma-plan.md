# Phase 30 — 8272 FDC + 8237 DMA controller 模擬

> **狀態**：✅ **完整 — 透過真 BIOS 端到端 FreeDOS BOOT**（2026-05-16）。
>
> **原目標**：Real-mode PC/XT BIOS（`pcxtbios.bin`）可以從真實 .img 檔
> 完成 INT 19h bootstrap、把 boot sector 載到 0:7C00 並跳到它。
>
> **超越**：不只 INT 19h work、boot sector 載 FreeDOS kernel.sys +
> COMMAND.COM 端到端。最終 test output（MDA framebuffer 的 text preview）：
>
> ```
> | FreeCom version 0.85a - WATCOMC - XMS_Swap [Jul 10 2021 19:28:06] |
> ```
>
> End test 過、無 HLE BIOS intercept：
>
> ```
> apr-pc --bios=BIOS/firmware/pcxtbios.bin \
>        --floppy-a=BIOS/freedos-1.3-floppy.img \
>        --headless --backend=json --max-cycles=100000000 \
>        --screenshot=result/pc/30-rolfix-realbios.png
> ```
>
> ### Phase 30 期間發掘的關鍵 sub-fix
>
> FDC/DMA emulation 本身（commit `12ae222` 出貨）第一天就 work。
> 端到端 boot 又花兩天因為 FDC exposed 一個之前沒被打到的 CPU 模擬 bug：
>
> - **Phase 30.6a — IRQ deassert**（`0ab519d`）：result-phase read 不再
>   re-assert IRQ 6（之前在 result 中間 spurious fire ISR）。
> - **Phase 30.6b — diagnostic infrastructure**（`c18bcb4`）：
>   `--trace-cpu-cs=` filter、`--watch-mem=` / `--watch-read=` write/
>   read range tracker、HeadlessRunner 內更寬的 memory dump +
>   boot-sector orig-vs-copy diff。這些工具讓下一步可能。
> - **Phase 30.6c — ROL r/m16, CL count > 1**（`e62a462`）：真 bug。
>   我們的 `X86ShiftRotateW16CountClEmitter` rol arm 是 count=1 stub。
>   pcxtbios.bin INT 13h 用 `MOV CL, 4; ROL AX, CL` 把 caller 的 ES split
>   成 24-bit DMA base + page register、所以 BIOS 在算錯誤的 DMA physical
>   address、FDC 把 sector data 寫到錯的 memory area。Fix：proper
>   count-based ROL 用 `lhs << n | lhs >> (16 - n)`。
>
> ### Real-BIOS boot 的跨 phase 依賴
>
> pcxtbios.bin → FreeDOS boot path 需要全部：
>
> 1. Phase 28.0-28.7（HLE BIOS infrastructure — 還部分用於 unhandled INT、
>    但大多走 real ROM）
> 2. Phase 28.IO（透過 `PcPortBus` extern routing 的 port I/O dispatch）
> 3. Phase 29 i8087 extension（BIOS POST FPU detection 透過 real FNINIT/FSTCW
>    看到對的 state）
> 4. Phase 29-supp port 0x3BA/0x3DA retrace bit + MDA framebuffer auto-detect
>    （BIOS POST text output）
> 5. Phase 30 8272 FDC + 8237 DMA（本文件）
> 6. Phase 30.6c CPU ROL fix（debug Phase 30 時發掘）
>
> 拿掉任一個 real-BIOS boot 就 break。HLE BIOS path
>（`--bios-mode=hle`、無 `--bios=`）還是不用 29/30/30.6c work、因為
> HLE INT 13h 直接跟 DiskImage 對話、不走 FDC/DMA/ROL。

## Scope

In：~600 行 C# 模擬足以讓 real BIOS POST 的 INT 19h 完成 sector read 的
8272 / μPD765A FDC + 8237 DMA controller（只 channel 2）。

Out：format/write/verify、multi-drive、超過 1.44MB 的 density auto-detect、
DMA channel 0/1/3、cycle-accurate FDC timing、FORMAT TRACK 支援。

## 設計 per Gemini consult（2026-05-16）

完整 log：`tools/knowledgebase/message/20260516_004919.txt`。

### 最小 command set（6 active + 1 invalid handler）

| Opcode | Mnemonic | Result phase | IRQ 6 | Note |
|---|---|---|---|---|
| 0x03 | SPECIFY | 無 | 否 | Step rate / head load time；純 config |
| 0x04 | SENSE DRIVE STATUS | 1 byte ST3 | 否 | 回 drive ready / track 0 / write protect |
| 0x07 | RECALIBRATE | 無 | **是** | Seek 到 cyl 0；由 SENSE INT 清 |
| 0x08 | SENSE INTERRUPT STATUS | 2 byte ST0+PCN | 否 | 清 IRQ 6；RECALIBRATE/SEEK 之後必需 |
| 0x0A | READ ID | 7 byte ST0/ST1/ST2/C/H/S/N | 是 | Real BIOS 用於 media detect；從「current seek」位置假造 |
| 0x0F | SEEK | 無 | **是** | 把 head 移到指定 cyl；由 SENSE INT 清 |
| 0x46/0x66/0xE6 | READ DATA | 7 byte | 是 | Multi-sector read；bit pattern：0x40 MFM、0x20 SK、0x80 MT、0x06 opcode |
| (任何 unmapped) | — | 1 byte ST0=0x80 | 否 | Force-known-state invalid-command response |

### MSR (0x3F4) state machine

```
RQM (b7) | DIO (b6) | NDM (b5) | CB (b4) | D3B-D0B (b3-b0)
```

| State | RQM | DIO | NDM | CB | Note |
|---|---|---|---|---|---|
| Idle | 1 | 0 | 0 | 0 | Ready for command byte |
| Command phase（write 中） | 1 | 0 | 0 | 1 | Command byte 之間 |
| Execution（DMA mode） | 0/1 | 0 | 0 | 1 | RQM 跟著 DMA DRQ；我們 burst 所以永遠 1 |
| Result phase | 1 | 1 | 0 | 1 | Ready for host 讀 |
| Result drained | 1 | 0 | 0 | 0 | 回 idle |

BIOS poll-write idiom：`wait (MSR & 0xC0) == 0x80` 然後寫到 0x3F5。
BIOS poll-read idiom：`wait (MSR & 0xC0) == 0xC0` 然後從 0x3F5 讀。

### DOR (0x3F2) bit

| Bit | Field | Effect |
|---|---|---|
| 1:0 | Drive Select | 00=A、01=B、10=C、11=D |
| 2 | nRESET | 0 → reset FDC；0→1 轉換 assert IRQ 6 |
| 3 | DMAEN | Enable IRQ + DMA |
| 4 | Motor A | 1 = drive A motor on |
| 5 | Motor B | 1 = drive B motor on |
| 6 | Motor C | — |
| 7 | Motor D | — |

### 8237 DMA channel 2 register map（我們只用 ch2）

| Port | Register | Note |
|---|---|---|
| 0x04 | Ch2 Base Addr（16-bit 透過 flip-flop） | LO then HI |
| 0x05 | Ch2 Word Count（16-bit 透過 flip-flop） | LO then HI；count = N-1 |
| 0x08 | Status (read) / Command (write) | Bit 2 = ch2 TC reached |
| 0x0A | Mask (write) | b0-b1 = channel select、b2 = mask bit |
| 0x0B | Mode (write) | Per-channel mode（read/write/auto-init 等） |
| 0x0C | Clear flip-flop（write any） | Reset LO/HI toggle |
| 0x0D | Master Reset（write any） | Reset 整個 DMAC |
| 0x81 | Ch2 Page Register | 24-bit DMA address 的 high 4 bit |

### DMA「synchronous burst」cheat

Per Gemini guidance：真 DMA 是 byte-by-byte 跟 CPU 交錯、但 BIOS boot
我們可以在 FDC execution phase 內整個 sector transfer 一次。處理 READ
DATA 時的演算法：

```
1. 算 linear addr = (page[0x81] << 16) | base[0x04]
2. 從 disk image 在 command 的 CHS 位置讀 count = (count[0x05] + 1) byte
3. memcpy 到 RAM[linear addr .. linear addr + count]
4. Update DMA state：
     base[0x04] += count
     count[0x05] = 0xFFFF（TC reached）
     status[0x08] |= 0x04（ch2 TC）
5. 把 FDC 轉到 result phase + assert IRQ 6
```

### IRQ 6 接線

- BIOS 在第一個 command 前透過 OCW1（`OUT 0x21, mask`）unmask IRQ 6。
- 我們在以下時 assert IRQ 6 via `Pic8259.RaiseIrq(6)`：
  - DOR nRESET 0→1
  - SEEK / RECALIBRATE 完成
  - READ DATA execution phase 完成（→ result phase）
- 我們清 IRQ 6 在 CPU 從 0x3F5 讀 result phase 第一個 byte、或 CPU
  執行 SENSE INTERRUPT STATUS（無 result phase command）時。

## 實作 plan（sprint）

| Sprint | Deliverable | 狀態 |
|---|---|---|
| 30.1 | `Fdc8272` skeleton + DOR/MSR port + SPECIFY/SENSE INT | ✅ `12ae222` |
| 30.2 | RECALIBRATE + SEEK + IRQ 6 接線 | ✅ `12ae222` |
| 30.3 | `Dma8237` skeleton + ch2 register file + flip-flop | ✅ `12ae222` |
| 30.4 | READ DATA + synchronous burst transfer | ✅ `12ae222` |
| 30.5 | READ ID + SENSE DRIVE STATUS + invalid-command fallback | ✅ `12ae222` |
| 30.6a | IRQ deassert fix（在 result phase 重 fire） | ✅ `0ab519d` |
| 30.6b | Debug 基礎建設（--trace-cpu-cs、--watch-mem/--watch-read、寬 memdump） | ✅ `c18bcb4` |
| 30.6c | CPU ROL r/m16, CL count>1 fix（被 stub） | ✅ `e62a462` |
| 30.6（端到端目標） | pcxtbios.bin INT 19h → FreeDOS boot 完整 | ✅ `e62a462` |
| 30.7 | 收尾 + screenshot + EN mirror | ✅ `50cff5d` + `d8b54b9` |
| **30.7a** | **GUI + real-BIOS keyboard 端到端互動 A:\\>** — 16 個 sub-bug investigation（XLAT、HLT wake-on-IRQ、FDC motor stall hack、port 0x61 ack pulse 等） | ✅ `64cb41b` |
| **30.7a-followup** | 8250 UART + LPT printer status stub（防禦性） | ✅ `58e38a8` |
| **30.7b** | **完整 GUI FreeDOS 互動 — dir / ver 端到端可見**：F12 framebuffer dump hotkey、KeyDown/KeyUp Shift modifier handling、attr=0 → 0x07 renderer workaround（後在 30.7c revert） | ✅ `3d3c215` |
| **30.7c** | **`--video=mda\|cga` selector** + 對的 port 0x62 PPI mapping（per pcxtbios.asm source 不 IBM PC 原作）+ revert renderer attr=0 hack。CGA path 100% work（FreeDOS 互動 A:\\> + dir + ver 在 80x25 完全可見）。MDA path partial（palette 對、但 pcxtbios POST 選 CGA over MDA 因為兩個 VRAM probe 都 pass）— `--video=cga` 是 working 建議。 | ✅ `9dbb8fc` |
| **30.7d** | MDA-aware renderer — IBM 5151 green-phosphor palette + attribute byte pattern-match（per 真 MDA 硬體 logic 的 invisible / underline / reverse / normal，不是 CGA 16-color palette）。MDA mode 現在正確 render 單色。已知限制：cell 上 attr=0（被 POST CGA path 沒初始化）的 char 還是 render 隱形 — 真修法需要強迫 pcxtbios 真選 MDA mode 7。 | ✅（本 commit） |
| 30.8 | MDA mode「某些 char 隱形」investigation。透過 Gemini 的初診是 pcxtbios scroll bug — 透過下面 Phase 30.10 驗證 + 部分修。深層 root cause：FreeDOS CON driver 對某些 output path（特別是 dir body）做 attr=0 直 word-write 到 MDA VRAM；不可從 emulator 側修除非 HLE-intercept word-write 到 VRAM（scope 外）。**`--video=cga` 完整 work**；MDA 可用於打字但 dir output 部分隱形。 | 📋 root cause 識別、MDA-mode-specific 限制 |
| **30.10** | **pcxtbios INT 10h scroll bug 的 HLE patch** — (a) 二進位 patch BIOS 的 F000:F6BE 處 `mov bh, ah` 成 `mov bh, 0x07`（pcxtbios.asm line 4143）帶 checksum filler 補；(b) `PcSystemRunner.EmulatorThreadProc` 內對任何 text mode 下 BH=0 的 INT 10h AH=06 → 強迫 BH=0x07 的 runtime intercept。兩個都 target「DOS 從 AH=08 讀到壞 attr → 用 0 scroll」path。對 BIOS-internal scroll 有效；對 FreeDOS 直 VRAM write 沒幫助（看 30.8）。 | ✅ 本 commit |
| **30.11** | **AutoTester — 腳本化 GUI 整合測試 framework**。CLI `--auto-test=<sequence>`。每 5s poll framebuffer、match pattern（「language」、「[Y,N]」、「A:\\>」）、透過 `PcPortBus.InjectScancode` 注 scancode、dump 最終 screen + close form。內建 sequence `freedos-mda-dir` 做完整 boot → language Enter → installer N+Enter → A:\\> dir+Enter → dump。讓無人值守 bring-up 測試 without operator。`gui-test.bat realbios mda auto` 來 invoke。 | ✅ 本 commit |
| **30.11b** | **FreeDOS source audit（零直 VRAM write）+ permissive MdaDecodeAttr**（bg!=0→reverse、attr=0+printable→normal）。駁斥 Gemini 的「FreeDOS 直 VRAM word write」宣稱 — MDA 隱形是 BIOS scroll + installer CGA-only attr。Clone FreeDOS kernel + freecom 到 `ref/freedos/` 為 source-of-truth。Permissive renderer + 30.10 patch 一起讓 MDA `dir` 完全可見。AutoTester 拿到 PNG screenshot + per-row attr histogram。 | ✅ `139e4a6` |
| **30.12** | **`--video-bios=PATH` option-ROM loader（smoke test）**。在 `0xC0000` load `videorom.bin`（Tseng ET4000 32 KB VGA BIOS、V8.02X 1992）、驗 `55 AA` + checksum。pcxtbios POST 掃 0xC0000-0xFE000 找 option ROM（asm line 819-880）並自動 FAR-CALL offset 3。**意外好處**：VBIOS init 強迫 video mode → 3（CGA 80x25 colour text 在 0xB8000）而不是 7（MDA），所以所有 FreeDOS-installer 16-color render 透過我們既有的 CGA decoder「就 work」。`caller=C000` 在 INT 10h trace 顯示 157 次 — 真 Tseng init code 跑。這個里程碑不需要 VGA register / graphics-mode emulation；smoke test hard pass 帶完整 colour FreeDOS installer + `dir` 可見。 | ✅ 本 commit |
| 30.9 | 加速 real-BIOS 互動（目前 ~500K inst/sec；`dir`/`ver` 在 idle 感覺遲鈍因為 FreeCom prompt `$P` redraw 在每個 PIT tick poll LBA 0）。真修法：block-JIT + FPU correctness。 | ⏳ 延後 |
| 30.10 | block-JIT + Phase 29 FPU correctness — FPU_ST0 的 i64 alloca slot 不 match f64 runtime layout；slot type 修了 block-JIT 還是產黑螢幕。也解 Phase 28.8x（block-JIT INT trap loss）。可達的最大互動速度勝利。 | ⏳ 延後 |
| 30.13 | (未來) Phase 30.12 follow-up：模擬 VGA register file (3C0-3CF、3D4/3D5) + planar framebuffer (0xA0000) + mode 13h (320x200x256) + mode 12h (640x480x16) 讓 VGA-aware DOS 遊戲跑。Scope 外直到需要；目前 text-mode-3 path cover 全 FreeDOS 需求。 | ⏳ 延後 |
| **30.14a** | **Port 0xE9 debug-out hook（Bochs/QEMU pattern）** — PcPortBus intercept `OUT 0xE9, AL` → 把 AL 寫到 `temp/port-e9.log` + mirror 到 host stdout 為 `[E9] ...`。Smoke test：`test-roms/x86/src/30.14-port-e9-hello.asm` → `--test-rom=...` 確認 hook fire。 | ✅ 本 commit |
| **30.14b** | **`--floppy-b=PATH` 第二 floppy mount** — 加上宣告 2 個 drive 的 equipment-word fix（PcPortBus `floppyCount` → SW2 nibble bit 2-3 → BDA[0x10] bit 6-7）。FreeDOS 現在把 B: 當真 drive 不 phantom-swap。 | ✅ 本 commit |
| **30.14c** | **透過 Port 0xE9 + B: 的端到端 NASM .COM** — `tools/make_fat12_floppy.py` 純 Python FAT12 1.44 MB builder（Windows 上 mtools 不可用；DiscUtils.Fat NuGet 拒因為太重）。B: floppy 上的 NASM `HELLO.COM`。新 AutoTester sequence `freedos-b-hello` 自動跑完整 boot → installer skip → B: → HELLO 端到端。Surfaced bug：port 0x61 SW2 selector 是 bit 3 不是 bit 2（pcxtbios `TURBO_ENABLED` 寫 0xA5 = bit 2 sticky-high；bit 3 是真 PC/XT 8255 PIA select line）。MainForm + AutoTester 拿到 `; : , . / \ [ ] ' ~ - = ` 標點 scancode 讓 shift+key work for typing `B:` 等。流程 doc：[`MD/process/03-dos-test-injection-workflow.md`](../process/03-dos-test-injection-workflow.md)。 | ✅ 本 commit |

### 狀態：所有 PHASE ✅ 完整（2026-05-16）

端到端 real BIOS + FreeDOS boot work。可見的 MDA framebuffer output
在 ~100M CPU cycle 之後顯示 `FreeCom version 0.85a`。FDC/DMA MVP
（30.1-30.5）第一天就出貨且功能正確；第 2-3 天是 debug 真 BIOS 在它的
DMA address 計算內 exercise 的 CPU-level ROL bug。

### 30.6c — ROL with CL count > 1 是錯的 — FIXED（2026-05-16）✅

**Real BIOS + FreeDOS 端到端 BOOT！** 修完後的 text preview：
```
| FreeCom version 0.85a - WATCOMC - XMS_Swap [Jul 10 2021 19:28:06]        |
```

Root cause 透過 memory-write/read watch + per-CS CPU trace filter 追到
一個 CPU emulation bug：

**Buggy code path**：`src/AprCpu.Core/IR/X86_16Emitters.cs`
`X86ShiftRotateW16CountClEmitter` 的「rol」arm 有 TODO stub、
把 `ROL r/m16, CL` 委托給 count=1 logic（不論實際 CL 值都 shift left 1）。
Comment 說：
> ROL/ROR/RCL/RCR with count!=1 silicon 行為惡名昭彰未定義；
> 我們委托給 count=1 IR ... 這對 count>1 錯但對 count=1 對、
> cover 大部分以小動態 count 到 D2/D3 的真實 code。

pcxtbios.bin INT 13h handler 用 `MOV CL, 4; ROL AX, CL` 從 caller 的
16-bit ES segment 算 24-bit DMA base + page register split：
```
F000:ED5F  MOV AX, [BP+0xC]   ; AX = caller 的 ES (= 0x1FE0)
F000:ED62  MOV CL, 4
F000:ED64  ROL AX, CL         ; 期望 0xFE01 (true ROL by 4)
                              ; 得 0x3FC0 (ROL by 1 stub)
F000:ED66  ...                ; rest of arithmetic
F000:ED75  OUT 0x04, AL       ; 程式化 DMA base lo
```

ROL by 1 buggy 下、BIOS 算 DMA base 0xA360（linear `0x0A360`）。
正確 ROL by 4 下、BIOS 會算 0x61A0 with page=2（linear `0x261A0` ≈
`(ES << 4) + BX` = `0x251A0` adjust for ROL trick）。

**Fix**：用 `lhs << n | lhs >> (16 - n)` 實作 proper count-based ROL、
其中 `n = CL & 0x1F`（8086 不 mask 但 i16 operand width 限制 effective
rotation 到 mod 16）。Set CF = LSB of result。OF 架構上只 count=1 定義
但我們對可能在 multi-bit ROL 之後 sample 它的 code 也 emit count=1 公式。

```csharp
case "rol":
{
    var nWide = ctx.Builder.BuildAnd(clClamp,
        LLVMValueRef.CreateConstInt(i8, 15, false), "rolc16_n");
    var n16 = ctx.Builder.BuildZExt(nWide, i16, "rolc16_n16");
    var leftPart  = ctx.Builder.BuildShl(lhs, n16, "rolc16_left");
    var rightShift = ctx.Builder.BuildSub(
        LLVMValueRef.CreateConstInt(i16, 16, false), n16, "rolc16_rsh");
    var rightPart = ctx.Builder.BuildLShr(lhs, rightShift, "rolc16_right");
    result = ctx.Builder.BuildOr(leftPart, rightPart, "rolc16_r");
    // CF = LSB of result；OF = MSB(result) ^ CF（count=1 公式 reuse）。
    ...
}
```

獨立 test ROM `test-roms/x86/30-rol-cl-test.com` 驗：
- `0x1FE0 ROL 4 = 0xFE01` ✓
- `0xC123 ROL 8 = 0x23C1` ✓
- `0x0001 ROL 15 = 0x8000` ✓
- `0xFFFF ROL N = 0xFFFF` ✓

端到端 test（`apr-pc --bios=BIOS/firmware/pcxtbios.bin
--floppy-a=BIOS/freedos-1.3-floppy.img`）：
- BIOS POST ✓
- INT 19h 透過 real FDC 載 boot sector ✓
- Boot sector 執行、自我搬移、載 root dir + FAT ✓
- FAT walker walk chain 到 kernel.sys ✓
- Kernel 載入、跳到 FreeDOS kernel.sys ✓
- COMMAND.COM（FreeCom 0.85a）banner 印到 MDA framebuffer ✓

**延後但相關**：
- 同個 count=1 stub 還在 W8（8-bit）path（line ~7181）。真 DOS code 很少
  用 `ROL r/m8, CL` with CL>1、但為完整性該修。同樣做法：`lhs << n |
  lhs >> (8 - n)` with n = CL mod 8。
- ROR/RCL/RCR variant 也 stub。ROR 可以類似做。RCL/RCR 需要在 rotation
  ring（9-bit / 17-bit）裡 handle carry bit。FreeDOS boot path 都沒打到。

### 30.6b — Trace-cpu 識別真 root cause（2026-05-16）

30.6 Gemini 分析說「boot sector 自我搬移沒完成」。更深的 trace-cpu
investigation（加 `--trace-cpu-cs=` filter + memory write/read watch range）
修正診斷：

**Boot sector 自我搬移（REP MOVSW）正確 work**。獨立 REP MOVSW test
（`test-roms/x86/30-rep-movsw-test.com`）確認 emitter bit-correct。
早期取證裡「看起來零」的搬移 byte 實際被後來在 cluster 0 spin 的 FAT walker
STOSW loop OVERWRITE。

**真原因是 INT 13h 附近的 segment-register state mismatch**。HLE 跟
real-BIOS path 之間的 trace 比較顯示 FreeDOS boot sector 的 INT 13h
call site 在 `1FE0:7D7B`（JZ-skipped 的 AH=41 extension check）跨 iteration
看到不同的 `ES`：

| Iteration | HLE ES | real-BIOS ES |
|---|---|---|
| 1 | 0x0060 | 0x0060 |
| 2 | 0x0060 | 0x0060 |
| 3 | **0x0080** | 0x0060 |
| 4 | 0x00A0 | 0x0060 |
| 5 | 0x00C0 | 0x0060 |
| 6 | 0x00E0 | 0x0060 |

**HLE 每 iteration ES 加 0x20（= 512 byte / 16 paragraph）但 real-BIOS
釘在 0x0060**。那個 increment 是 FreeDOS 把每個 sector 分配給 fresh
memory segment（0x60、0x80、0xA0、…）的方式、讓 FAT walker（後面
set `DS=[BP+0x5C]=0x0060`）可以跨多個 load 的 sector 讀 cluster chain。

Real-BIOS path 內、ES 永不推進 → 所有 sector load 到同個目的地 → 只最後
一個可見 → walker 從錯的 sector 讀 byte → FAT entry 0 → cluster 0
infinite loop。

**最可能兇手**（還沒窄化）：
1. 真 BIOS INT 13h handler 蓋了 boot sector 為它的 ES-increment math
   依賴的 register（例如、AX、CX、或某個 BP-something 的 memory cell）。
2. 我們的 CPU 的 INT 指令或 IRET push/pop 錯的 segment register state。
3. Boot sector 的 increment 指令本身是 state-dependent bug 的 string-op
   variant、我們之前沒打到。

**Next debug step**：dump HLE 跟 real-BIOS trace 內連續 INT 13h call site
之間的指令、diff 找 ES-increment divergence 處。如需要加 `--watch-reg=ES`
（log 對 ES 的每個 write）。

CPU 是 `--backend=json`（per-instruction）；無 block-JIT cache 要 invalidate。
Bug 在完全 decode 的 per-instruction 執行內出現。

### 30.6 — Gemini consult refine root cause（2026-05-16）

問 Gemini 分析 FAT walker stuck symptom；完整 log
`tools/knowledgebase/message/20260516_011840.txt`。關鍵發現：

**Q1 — DMA at 0x0A360 是對的**（= `07C0:2760`）。Single-sector
single-buffer 是 FreeDOS boot sector 的 intended pattern：讀一個 sector
到 scratch、process、讀下一個、重複。**不是** multi-sector issue —
不需要實作 multi-sector READ DATA。

**Q5 — IRQ pacing 之前 buggy**。原 code 在 host 讀第一個 result byte 時
call `AssertIrq(6)`；這 re-assert edge-triggered line 並造成 spurious
second ISR。Fix：就清 local `_interruptPending` flag、IRQ 已經 fire 一次
（透過 `DequeueNextVector` 清 pending bit）。在同個補充 commit 出貨這個 fix。

**Root cause**（per Gemini analysis + memory forensics）：FAT walker 處
DS=0x0060 表示 CPU 在從錯 segment 讀 FAT scratch buffer。但透過 HeadlessRunner
擴張的 boot-sector forensics 深入檢查顯示：**boot sector 自我搬移只 copy
SECOND HALF（byte 0xFE-0x1FF = 258 byte）到 `1FE0:7C00`**。Relocate copy
的前 254 byte 全 0。Diagnostic：

```
orig @ 0x07C00 [0..31]: EB 3C 90 46 52 44 4F 53 35 2E 31 00 02 01 01 00 ...
copy @ 0x27A00 [0..31]: 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 ...
orig vs copy diff: 224 byte 不同（第一個在 offset 0x000）
```

翻譯：boot sector 的 `REP MOVSW`（或等價 relocation routine）只從 offset
0xFE transfer 256 byte、不是從 offset 0x00 完整 512 byte。

最可能原因是以下之一的 **CPU emulation bug**：
- `REP MOVSW`（rep-prefix 跟 string-op + segment override 的互動）
- INT 13h register save/restore（real pcxtbios.bin 蓋 CX / SI / DI；
  boot sector 可能依賴 PUSHA/POPA、它有自己的 quirk）
- STOSW/MOVSW 附近的 Direction Flag (DF) handling

Next debug step：對 relocation routine 周圍窄窗口 enable `--trace-cpu`、
識別 set up copy 的精確指令、比較預期 vs 實際 register/flag state。
延後到 Phase 30.6b。

### 30.6a — IRQ pacing fix 出貨

`X86FpuHelpers`（抱歉、`Fdc8272.ReadFifo`）在 host 開始讀 result byte
時不再 re-assert IRQ 6。原 `AssertIrq(6)` call 意圖「deassert」但實際
re-fire edge。移除。

### 30.6 — 原 stuck point 描述（保留為 context）

讀完 14 個 root dir sector（LBA 19-32）+ 9 個 FAT1 sector（LBA 1-9）
透過 single-sector READ DATA command（DMA count = 511、每 sector
overwrite 前個在固定 dest 0x0A360）之後、CPU 進入 `1FE0:7D04` 的
infinite loop：

```
1FE0:7D04: AD          LODSW
1FE0:7D05: 73 04       JAE +4
1FE0:7D07: B1 04       MOV CL, 4
1FE0:7D09: D3 E8       SHR AX, CL
1FE0:7D0B: 80 E4 0F    AND AH, 0F
1FE0:7D0E: 3D F8 0F    CMP AX, 0FF8h
1FE0:7D11: 72 E8       JC -24
```

經典 FAT12 cluster-chain walker。AX=0 永遠（walker SI pointer 處的 data
是零）、AND AH,0F → 0、CMP 0FF8 → 小於、JC takes branch → cluster 0
infinite loop。

24-read pattern + 跨所有 read 同個固定 DMA destination 0x0A360 暗示
boot sector 用 0x0A360 為 single-sector scratch buffer、每個新 sector
overwrite。如果 walker 期望 FAT 完整 load（例如到 9 個不同 memory location）、
boot sector 假設的 memory layout 可能要求 DMA base auto-advance 的
multi-sector INT 13h read。需要 investigate：
1. pcxtbios.bin 的 INT 13h handler 對 multi-sector request 正確 honor 嗎？
   INT 13h call 內 AL=9 時、它發一個 EOT=9 DMA count=4607 的 FDC READ
   DATA、還是 9 個各別的 single-sector DMA count FDC command？Trace 暗示後者。
2. FreeDOS boot sector 期望 single buffer 或 9 個獨立 memory area for FAT？

可能 path forward：實作 multi-sector READ DATA、FDC 持續 transfer byte
直到 DMA TC。目前我們 cap 在 `Math.Min(dmaBytes, ...)` 對 single-sector 是 512。
DMA count = 4607 時、應該 burst transfer 全 9 個 sector。

### 延後

- Write 支援（WRITE DATA、FORMAT TRACK）
- HDD via FDC equivalent（Phase 30 floppy-only）
- Cycle-accurate FDC timing（我們 burst 同步）
- A: 以外的 Multi-drive 支援

## Sub-phase 邊界

- 30.2 之後：BIOS POST 寫 DOR + 讀 MSR + 成功 issue SPECIFY/RECAL/SENSE INT。
  BIOS 還沒讀 disk；它推進過 FDC init 然後之後 issue 會 timeout 的 READ DATA。
- 30.4 之後：BIOS POST INT 19h 成功載 boot sector。Boot sector 執行
  （DOS kernel 後續 disk read 可能 fail）。
- 30.6 之後：FreeDOS 透過 real BIOS 端到端 boot、HLE path 的 28.8 里程碑 mirror。

## Open question

- pcxtbios.bin geometry 假設 — 它知道 1.44MB（18 sector/track）還是只 360KB
  / 720KB / 1.2MB？如果它 cap 在 9-15 sector/track 我們的 READ DATA 要 translate。
  快速 Google check 或 trace 會揭露。
- BIOS Data Area equipment word（BDA 0x410）— BIOS 讀之前我們必須 set
  drive count bit 7:6 + bit 0、還是 BIOS POST 從 CMOS / 硬體 probe 自己 set？

## 交叉參考

- Phase 28 收尾: `MD/performance/202605152200-pc-emulator-freedos-boot.md`
- Phase 28.IO: `MD/performance/202605152230-pc-emulator-phase-28-io.md`
- Phase 29 收尾: `MD/performance/202605160100-x87-fpu-functional-complete.md`
- Gemini consultation: `tools/knowledgebase/message/20260516_004919.txt`
