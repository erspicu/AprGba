# Phase 30 收尾 — real BIOS + FreeDOS 端到端 boot

**日期**：2026-05-16  
**Phase**：30（8272 FDC + 8237 DMA + CPU ROL 修補）  
**狀態**：✅ 可用 — pcxtbios.bin + freedos-1.3-floppy.img 啟動到 COMMAND.COM banner、零 HLE BIOS intercept。  
**Plan**：[`MD/design/30-fdc-dma-plan.md`](../design/30-fdc-dma-plan.md)  
**前置收尾文件**：
- Phase 28 HLE：[`202605152200-pc-emulator-freedos-boot.md`](202605152200-pc-emulator-freedos-boot.md)
- Phase 28.IO：[`202605152230-pc-emulator-phase-28-io.md`](202605152230-pc-emulator-phase-28-io.md)
- Phase 29 FPU：[`202605160100-x87-fpu-functional-complete.md`](202605160100-x87-fpu-functional-complete.md)

## 出貨內容

| 項目 | Commit | 備註 |
|---|---|---|
| `Fdc8272` + `Dma8237` MVP | `12ae222` | 7 個 command（SPECIFY/SENSE INT/RECALIBRATE/SEEK/READ DATA/READ ID/SENSE DRIVE STATUS）、同步 burst DMA ch2（per Gemini consultation）、IRQ 6 走既有 PIC8259A |
| IRQ deassert 修補 | `0ab519d` | result-phase FIFO read 不再 re-fire ISR（之前會 double-entry 到 BIOS INT 0Eh handler）|
| Debug 工具 | `c18bcb4` | `--trace-cpu-cs=`、`--watch-mem=LO:HI`、`--watch-read=LO:HI`，HeadlessRunner 加大 memory dump（IP ±32 byte、boot sector orig-vs-copy 8-row diff）|
| **CPU ROL r/m16, CL count > 1 修補** | `e62a462` | `X86ShiftRotateW16CountClEmitter` 改用 `lhs << n \| lhs >> (16 - n)` 做正確的 count rotation，不再 forward 到 count=1 stub |
| 端到端 boot | `e62a462` | Screenshot `result/pc/30-rolfix-realbios.png` |

## 執行 command

```
dotnet run --project src/AprPc.Cli -- \
  --bios=BIOS/firmware/pcxtbios.bin \
  --floppy-a=BIOS/freedos-1.3-floppy.img \
  --headless --seconds=12
```

~100M CPU cycle 後在 MDA framebuffer 看到：

```
| FreeCom version 0.85a - WATCOMC - XMS_Swap [Jul 10 2021 19:28:06] |
```

## 最費時的 bug — `ROL r/m16, CL` count > 1

FDC + DMA 模擬（`12ae222`）從第一天就 functional 正確。後面三天的 debug 是
因為 pcxtbios.bin 暴露了一個 CPU bug，之前的 test ROM 都沒踩到。

### 症狀

`12ae222` 之後 BIOS POST 印出 `Insert BOOT disk in A:`、INT 19h 讀 boot
sector。Boot sector 應該：

1. 從 0000:7C00 自我搬移到 1FE0:7C00（REP MOVSW）。
2. Far-jump 到 1FE0:7C00 + 一個小 offset。
3. 從 disk 讀 root dir + FAT。
4. 走 FAT12 cluster chain 找 KERNEL.SYS。
5. KERNEL.SYS 載到 0060:0000。
6. Far-jump 進 kernel。

實際在第 4 步卡住，所有讀到的 cluster entry 都是 0。

### 走錯的方向

1. **REP MOVSW 壞了** — 對 0x27A00 relocated boot sector 做 forensic dump，
   發現對比原本 0x07C00，512 byte 中有 224 byte 不同。但 standalone
   `30-rep-movsw-test.com` ROM 排除這個 — REP MOVSW emitter 正確。差異
   其實是 downstream 的 FAT walker 用 STOSW 寫回零造成的 corruption。

2. **ES register 沒在加** — 一開始比對 HLE 跟 real-BIOS path 的 trace，看起來
   ES 卡在 0x0060。仔細讀才發現兩條 path 的 ES 演化一樣（0x60 → 0x80 →
   0xA0 ...）— 我自己看錯。

### 真正原因（透過 `--watch-mem` + `--trace-cpu-cs=F000` 找到）

`X86ShiftRotateW16CountClEmitter` 有個 TODO stub：count != 1 的 ROL，直接
emit count=1 的 IR（無論 CL 是多少都做一次左 rotate）。原本 comment 說
`count=1 涵蓋 99% real code`。

pcxtbios.bin 的 INT 13h handler 在 F000:ED5F-ED75 不同意。它用
`MOV CL, 4; ROL AX, CL` 把 caller 16-bit segment 拆成 8237 DMA controller 的
16-bit base register（lo + hi byte）+ 4-bit page register：

```
F000:ED5F  MOV  AX, [BP+0xC]   ; AX = caller's ES，例如 0x1FE0
F000:ED62  MOV  CL, 4
F000:ED64  ROL  AX, CL         ; 預期 0xFE01；舊 stub 給 0x3FC0
F000:ED66  ...                 ; 後續 base/page 算術
F000:ED75  OUT  0x04, AL       ; DMA base lo
```

ROL 壞掉，BIOS 把 DMA base 設成 0xA360 而不是正確的 0x251A0。FDC 乖乖把
sector data 送到 physical address 0xA360（正好是 MDA video memory 中間）。
Boot sector REP MOVSB 從原本的 ES:BX（0x251A0）讀 sector、複製的其實是
未初始化的 RAM。FAT12 cluster walker 走 `cluster 0 < 0x0FF8`、無限 loop。

### 修法

IR 改成正確的 count-based rotation：

```csharp
var nWide  = builder.BuildAnd(clClamp, const_i8(15), "rolc16_n");
var n16    = builder.BuildZExt(nWide, i16, "rolc16_n16");
var left   = builder.BuildShl(lhs, n16, "rolc16_left");
var rsh    = builder.BuildSub(const_i16(16), n16, "rolc16_rsh");
var right  = builder.BuildLShr(lhs, rsh, "rolc16_right");
result     = builder.BuildOr(left, right, "rolc16_r");
// CF = result LSB; OF = result MSB XOR CF
```

Standalone `30-rol-cl-test.com` 驗 4 個 case（0x1FE0 ROL 4 = 0xFE01、
0xC123 ROL 8 = 0x23C1、0x0001 ROL 15 = 0x8000、0xFFFF ROL 4 = 0xFFFF），
修完全 pass。

## Cross-phase 相依鏈 — real-BIOS chain

Real-BIOS path 需要下面六個都到位。少一個 boot 就壞：

1. **Phase 28.0-28.7** — HLE BIOS 基礎建設（even when real BIOS loaded，
   INT 1Ah time-of-day 還是部份走 HLE）。
2. **Phase 28.IO** — port I/O dispatch via `PcPortBus` extern routing，
   沒這個 pcxtbios.bin POST 根本無法跟 PIC/PIT/PPI/MDA 通訊。
3. **Phase 29** — i8087 extension。BIOS POST 早期會跑 `FNINIT / FNSTSW`
   去 detect 8087；沒 Phase 29 這是 invalid opcode。
4. **Phase 29-supp** — port 0x3BA/0x3DA retrace bit + MDA framebuffer
   auto-detect，BIOS POST text output 需要。
5. **Phase 30** — 8272 FDC + 8237 DMA（本 phase）。
6. **Phase 30.6c** — CPU `ROL r/m16, CL` count > 1 修補（本 phase 才挖到，
   靠 spec inspection 看不出來）。

HLE BIOS path（`--bios-mode=hle`、沒 `--bios=`）沒 29/30/30.6c 也能跑，
因為 HLE INT 13h 直接 talk DiskImage，不走 FDC/DMA/ROL。

## 為何這對 framework 重要

這是 framework 第一次跑**未經修改的 production BIOS ROM code**。pcxtbios.bin
是 public test BIOS（不是原版 IBM），但遵循 IBM PC/XT 慣例 — port I/O
sequence、INT handler convention、ROL trick。全部都是 1980s real-world code，
沒一個是為了 emulator 量身設計。能端到端 boot 起來代表 framework 已經磨到
generic x86-16 BIOS code 不需要 per-quirk patch 就能 POST 完 + 啟動 real OS。

## Deferred

- W8（8-bit）`ROL r/m8, CL` count > 1 還是用同樣 stub pattern
  （`X86_16Emitters.cs` ~line 7181）。DOS code 很少用 8-bit variable-count
  ROL，但完整性還是該修。
- ROR / RCL / RCR 在 W8 跟 W16 都還 stub 著。ROR 跟 ROL 同樣 pattern。
  RCL / RCR 需要在 9-bit / 17-bit rotation ring 內處理 carry-bit。
- FreeDOS 跟 pcxtbios.bin POST 都不會打到上面這些。

## 參考

- Plan 文件：[`MD/design/30-fdc-dma-plan.md`](../design/30-fdc-dma-plan.md)
- Gemini consultation：`tools/knowledgebase/message/20260516_004919.txt`
- Screenshot：`result/pc/30-rolfix-realbios.png`
- Standalone test ROM：`test-roms/x86/src/30-rep-movsw-test.asm`、
  `test-roms/x86/src/30-rol-cl-test.asm`
