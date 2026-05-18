# Phase 26-30.x — 尚未實作項目摘要

> **整理日期**：2026-05-17
> **Scope**：Intel PC emulator (AprPc) + i80286 / 8087 / FDC/DMA / verifier
> framework 相關。Phase 26-30.x design doc 標 deferred / TODO / out-of-scope 的項目
> 集中在一處便於 prioritise。
>
> **狀態 source**：本文件透過 audit `MD/design/26-i80286-realmode-plan.md`
> 到 `MD/design/30.18-gb-fuzzer-bug-339-investigation.md` 共 8 個 design doc、
> + 收尾筆記 `MD/performance/202605171533-verifier-and-fuzzer-closure.md`
> 產生（audit 結果：0 個 silent landing、0 個 broken commit hash）。

## 優先順序總覽

| Tier | 項目 | 解了之後的連帶效益 |
|---|---|---|
| **P0 高 leverage** | [30.10 block-JIT + FPU correctness](#3010-block-jit--fpu-correctness) | 同時解 [28.8x](#28-pc-emulator) + [30.9 互動 perf](#30-fdc-dma--aprpc)；FreeDOS 互動 ~500K → 30M+ inst/s 量級 |
| **P0 高 leverage** | [5.7 x86 fuzzer](#3015d-verified-block-jit-framework) | 補齊「4-CPU 全 fuzzer」宣稱；最可能再挖到 emitter bug |
| **P1 框架投保** | [5.8 CI gate](#3015d-verified-block-jit-framework) | 防 regression；BlockFunctionBuilder / `*Emitters.cs` 改動該觸發 verify-blocks |
| **P1 framework** | [Slave PIC at 0xA0](#30-fdc-dma--aprpc) | CheckIt / AT-class ROM；2-3 day work |
| **P2 ISA 補齊** | [27.12 TSS task switching](#27-i80286) | 多日；無 demo 需要 |
| **P2 ISA 補齊** | [29 DC/DA/DE/DF FPU 家族](#29-x87-fpu) | DOS 浮點程式需要 |
| **P3 optional** | [28.10/28.11 mouse + PC speaker](#28-pc-emulator) | 原 plan 標 optional |
| **P3 optional** | [30 WRITE/FORMAT FDC](#30-fdc-dma--aprpc) | 唯讀 floppy 已 cover boot use case |

---

## 27 — i80286

收尾文件：`MD/performance/202605110200-i80286-pmode-fault-model-complete.md`。

| Item | Phase | 延後原因 | Effort |
|---|---|---|---|
| **TSS task switching** | 27.12 | 多日；需要 TSS descriptor type + busy-bit toggle + state save/restore IR。`EmitSegCacheUpdate` + `EmitRaiseException` helper 上 additive。 | 3-4 day |
| LDT (TI=1) descriptor lookup | 27.x | 今天 fall through 到 GDT path。沒 demo 載 LDT-based selector。 | 0.5 day |
| PE=1 下 CS load via far jmp/call/iret | 27.x | IR 形狀不同（privilege-transition + conforming/non-conforming code descriptor + 可能 call gate）。MOV sreg 只 cover ES/SS/DS。 | 2-3 day |
| Visible-sreg rewind on fault | 27.x | Architectural drift；以 EXC_PENDING 為界、沒 demo 依賴。`X86WriteSregFieldEmitter` 重構或 instruction retire time 加 rewind pass。 | 1 day |
| DS/ES 的 code-segment readable subcheck | 27.11f 擴 | 載 code descriptor 到 DS 不尋常、沒 demo exercise。~30 行擴充。 | 0.5 day |

---

## 28 — PC emulator

收尾文件：`MD/performance/202605152200-pc-emulator-freedos-boot.md` +
`MD/performance/202605152230-pc-emulator-phase-28-io.md`。

| Item | Phase | 延後原因 | Effort |
|---|---|---|---|
| **Block-JIT INT trap loss** | 28.8x | `INT` 指令在 JIT'd block 內 trap 被 block 吃掉、`HleBios.Dispatch` 不會跑。**Workaround：`--backend=json`**。修法：強制 INT 指令 PcWritten=1 讓 block 退出。**[30.10 修了會連帶解](#30-fdc-dma--aprpc)**。 | 0.5 day |
| Real BIOS POST 在 joystick port 0x201 delay loop stall | 28-supp | POST 推進到 `F000:E706` 後在 port 0x201 read loop stall（FPU 偵測過了、等 timing-dependent 值）。FreeDOS HLE path 不受影響。 | 1 day（port 0x201 模擬時變值） |
| INT 33h mouse HLE | 28.10 | 原 plan optional；無 FreeDOS demo 依賴。 | 1 day |
| PC speaker PCM output | 28.11 | 原 plan optional。 | 1-2 day |

---

## 29 — x87 FPU

收尾文件：`MD/performance/202605160100-x87-fpu-functional-complete.md`。

「真 optional」family — 沒 DOS program 在 test corpus 內 emit、實際 program 用到時再加。
全部都是既有 D8 / D9 dispatcher pattern mirror、容易加。

| Item | 延後原因 | Effort |
|---|---|---|
| **DC family** | f64-form 算術帶 reg writeback direction 反過來 | 0.5 day |
| **DA family** | i32 整數算術 | 0.5 day |
| **DE family** | i16 整數算術 + pop-after register variant | 0.5 day |
| **DF 整數 load/store** | FILD/FIST/FISTP m16/m32/m64int — 把 FPU 結果存回整數變數的 program 需要 | 1 day |
| FSCALE / FXTRACT / FPREM | libm 內部用、需要時加 | 1 day |
| FPU_TOP ↔ TOP_SW sync | 1 行 update（Push/Pop helper 內）；DOS 通常透過 SAHF+JCC 讀 C bit 忽略 TOP_SW | 0.1 day |
| #MF exception 傳遞 | 99% DOS code 永不裝 FPU exception INT vector | 0.5 day |
| FSTENV / FLDENV / FNSAVE / FRSTOR | 完整 FPU-environment block save/restore；DOS code 很少用 | 1 day |
| FBLD / FBSTP m80bcd | BCD format；不見用 | 0.5 day |
| FCOMPP / FUCOM / FUCOMP / FTST 擴充 | D8 pattern mirror；既有 FCOM/FCOMP cover 大部分 | 0.5 day |

---

## 30 — FDC/DMA + AprPc

收尾文件：`MD/performance/202605161800-phase-30-real-bios-freedos-boot.md`。

### 30.10 block-JIT + FPU correctness

**P0 — 最高 leverage**。

`FPU_ST0` 的 i64 alloca slot 不 match f64 runtime layout；slot type 修了 block-JIT
還是產黑螢幕。**解這個會同時解**：
- [28.8x](#28-pc-emulator) block-JIT INT trap loss → real-BIOS block-JIT 可用
- [30.9](#309-real-bios-互動-perf) FreeDOS 互動遲鈍 → ~500K → 30M+ inst/s

Investigation 起點：`MD/design/30-fdc-dma-plan.md` row 30.10。

### 30.9 real-BIOS 互動 perf

目前 ~500K inst/sec、`dir`/`ver` 在 idle 感覺遲鈍因為 FreeCom prompt `$P` redraw 在
每個 PIT tick poll LBA 0。真修法 = 30.10 block-JIT correctness。

### 30.13 VGA 完整模擬

`--video-bios=videorom.bin`（Tseng ET4000）目前 smoke test only。
完整 VGA-aware DOS 遊戲需要：
- VGA register file（3C0-3CF、3D4/3D5）
- Planar framebuffer (0xA0000)
- Mode 13h (320x200x256)
- Mode 12h (640x480x16)

目前 text mode 3 cover 全 FreeDOS use case；沒商業案例需求。

### FDC/DMA scope-out 項目

| Item | 延後原因 |
|---|---|
| WRITE DATA / FORMAT TRACK | 唯讀 floppy 已 cover FreeDOS boot |
| HDD via FDC | floppy-only by design |
| Cycle-accurate FDC timing | 我們 burst synchronous |
| Multi-drive 超過 A: + B: | 需要加 drive select logic |

### CPU emitter 補齊

| Item | 延後原因 |
|---|---|
| ROR W16 with count>1 | 還是 count=1 stub；FreeDOS boot path 沒打到 |
| RCL/RCR W16 with count>1 | 需要 9-bit / 17-bit carry-ring；無 demo |
| ROL/ROR/RCL/RCR W8 with count>1 | 同上、8-bit width |

### Slave PIC at 0xA0

**P1**。目前 `Pic8259` master-only（IRQ 0-7）、match XT 但擋：
- CheckIt 的 interrupt cascade test
- AT-class BIOS POST
- 任何用 IRQ 8-15 的 driver

Scope：`IsSlave` flag + master 0x20/0x21 + slave 0xA0/0xA1 + IRR/ISR/IMR/OCW2/OCW3/ICW1-4 handshake + slave IRQ via master IRQ 2 + spurious IRQ 7/15 handling。

Effort：2-3 day。

---

## 30.15d — Verified Block-JIT framework

收尾文件：`MD/performance/202605171533-verifier-and-fuzzer-closure.md`。

| Item | Phase | 延後原因 | Effort |
|---|---|---|---|
| **x86 fuzzer** | 5.7 | NES/GB/GBA fuzzer 都有、x86 還沒。Pattern 跟既有 fuzzer 一樣、隨機產 .com bytes 餵 verifier。 | 1-2 day |
| **CI gate** | 5.8 | Doc 已 ship（`MD/process/05-verified-blockjit-howto.md`）、CI gate 沒。任何 `BlockFunctionBuilder.cs` / `AllocaSlotProvider.cs` / `*Emitters.cs` 改動該觸發 1-min verify-blocks。 | 0.5 day |
| V2 per-block CoW snapshot | 設計時規劃 | V1 full memcpy 可接受（1MB × 15k blocks/s ≈ 15GB/s memory 頻寬 — 驗證模式可接受）；optimization | 1 day |
| Task #340 GBA STMDB R15 PC-pipeline offset | 30.18p | 30.18p commit 修了但 verifier 內可能還有 8-byte delta；need re-verify | 0.5 day（驗證） |
| Task #342 x86 mid-block undecodable | 30.18 follow-up | Random ROM 產出 .com byte sequence 撞到 spec 沒 cover 的 opcode；framework gap、不是 emitter bug | 0.5 day |

---

## 30.18 — GB fuzzer bug investigation

**全部 RESOLVED**（2026-05-17、Phase 30.18s/u/y）。看
`MD/design/30.18-gb-fuzzer-bug-339-investigation.md` Update 4 + Update 6。

| Sub-bug | Fix commit | Status |
|---|---|---|
| iter 79：SyncEmitter PC clobber | `8d376da` | ✅ |
| iter 78：CPU+MBC bank-switch | `GbMemoryBus.SuppressMbcWrites` | ✅ |
| 條件 branch defer-sync clobber | (in fuzzer fix commit) | ✅ |
| IRQ-delivery cadence | `f451988`（`PollPendingIrqsAtBlockBoundary`） | ✅ |

---

## 建議下手順序

1. **30.10 block-JIT FPU correctness**（P0、連帶解 28.8x + 30.9）
2. **5.7 x86 fuzzer**（P0、補齊 framework 宣稱）
3. **5.8 CI gate**（P1、防 regression）
4. **Slave PIC at 0xA0**（P1、解鎖 CheckIt / AT ROM）
5. 之後按 ISA 補齊 / optional feature 看實際需求挑

## 框架 perf 演進（獨立 axis）

跟上面 deferred 項目正交、是 future-quarter 的 perf 推進：

- **[Phase 31](../../design/31-block-chaining-superblock-plan.md)** —
  block chaining + superblock JIT。把 compilation unit 從 single basic
  block 推到 chained blocks 跟 superblock。預期 hot loop 2-3× throughput
  gain；spec-driven 慣例不破壞。實作成本 ~10 day（31.1 + 31.2）。

## Storage capabilities（獨立 axis）

跟上面也正交、是 multi-disk install / 安裝 program 到 HDD / dev-loop
拖檔的能力推進：

- **[Phase 32 storage plan](hdd-mount-swap-plan.md)** —
  - **32.1** Floppy swap hotkey（Ctrl+L cycle）+ DSKCHG port 0x3F7
    （~1 day、解 CheckIt / FreeDOS install 多碟 install）
  - **32.2** Virtual HDD（HLE INT 13h、FDPT INT 41h/46h、LBA probe stub）
    （~2-3 day、解 persistent C: + 從 C: boot + CheckIt 安裝到 C:）
  - **32.3** Host-dir mount（vvfat read-only V1 + Guest TSR write V2）
    （~2+ week、解 dev-loop 拖檔；INT 21h trap 不能用 — Gemini 警告）

---

## 交叉參考

- `MD/design/26-i80286-realmode-plan.md` — Phase 26 plan
- `MD/design/27-i80286-completion-plan.md` — Phase 27 plan + sprint status
- `MD/design/28-intel-pc-emulator-plan.md` — Phase 28 plan
- `MD/design/29-x87-fpu-plan.md` — Phase 29 plan + sprint status
- `MD/design/30-fdc-dma-plan.md` — Phase 30 plan + sprint status
- `MD/design/30.15-blockjit-pc-investigation.md` — Phase 30.15 investigation（RESOLVED）
- `MD/design/30.15d-verified-blockjit-framework-design.md` — Verifier framework 設計
- `MD/design/30.18-gb-fuzzer-bug-339-investigation.md` — GB fuzzer bug 調查（RESOLVED）
- `MD/performance/202605171533-verifier-and-fuzzer-closure.md` — 最近的全部收尾報告
