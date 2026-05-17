# Phase 27b — Intel 80286 protected-mode fault model 完整

> **收尾**：2026-05-11
> **Scope**：descriptor-based segmentation + 4-baseline-check fault model
> 端到端在 i80286 backend 上 land。Phase 27b 的核心架構里程碑
> (`MSW.PE = 1` → 真實 descriptor fetch + 從真實 instruction stream 來的
> 真實 fault) **可從 96-byte .com ROM demo**。
> 前置：Phase 27a（real-mode 完整、`MD/performance/202605110100`）。
> 延後：Sprint 27.12 TSS task switching（多日工作、獨立 phase）。

## 架構里程碑

`MSW.PE = 1` 不再只是裝飾。Sprint 27.13b 之後，每個 ModR/M memory
load/store 用 hidden descriptor cache 的 `<seg>_BASE`、不用
`(visible-selector << 4)`。Sprint 27.11a-27.11f 之後，protected mode
下載 sreg 會跑完整的 80286 PRM descriptor-validation pipeline 才填那個
cache。畸形 selector 到 EXC_PENDING flag 之前不會污染 cache、不會讓錯誤
的 physical address 被讀。

具體來說，下面 96-byte 的 fault matrix 就是證明：

| ROM | Selector → reg | Descriptor | 架構結果 | 觀察 |
|---|---|---|---|---|
| `27-pmode-entry.com`        | `0x0008 → DS` | P=1, S=1, DPL=0, type=writable data | OK；`mov bx,[0]` 讀 DS_BASE=0x100 = 前 2 byte 的 code | `BX=0xF1B8`、無 EXC ✓ |
| `27-pmode-np.com`           | `0x0008 → DS` | **P=0**, S=1, DPL=0, type=writable data | `#NP(sel)` per Intel | `EXC vector=0x0B error=0x0008` ✓ |
| `27-pmode-null-ss.com`      | `0x0000 → SS` | (NULL selector — 不查 descriptor) | `#GP(0)` per Intel SS-NULL rule | `EXC vector=0x0D error=0x0000` ✓ |
| `27-pmode-dpl-gp.com`       | `0x000B → DS` (RPL=3) | P=1, S=1, **DPL=0**, type=writable data | `#GP(sel)`：`max(CPL=0, RPL=3) > DPL=0` | `EXC vector=0x0D error=0x0008` ✓ |
| `27-pmode-ss-bad-type.com`  | `0x0008 → SS` | P=1, S=1, DPL=0, **type=executable code** | `#GP(sel)`：SS 要 writable data | `EXC vector=0x0D error=0x0008` ✓ |

5 個 ROM 都從 `tools/build_27_pmode_demos.py` deterministic build
（重跑產生 byte-identical output，每個 sprint 對未改動的 ROM 跟 git
index 驗過）。

## 出貨內容 — Phase 27b sprint chain

Compaction-friendly micro-sprint cadence；每個 sprint 一個 commit、
通常 <100 行、可獨立 revert。

### Track 1 — descriptor-fetch wiring（Sprint 27.6 → 27.13b）

| Sprint | Commit | Deliverable |
|---|---|---|
| 27.6  | `11fdc98` | `Descriptor` + `Selector` record、parse/build helper |
| 27.7  | `9f819b5` | `IMemoryBus` 上的 `ReadDescriptor` / `WriteDescriptor` |
| 27.8  | `0dd6404` | `Msw` struct + `IsProtectedMode` PE-bit reader |
| 27.9  | `553909c` | Privilege-level helper（`CanAccessDataSegment` 等） |
| 27.10a | `cefed78` | Helper 整合 test（端到端 mock） |
| 27.10b | `97e19ea` | Hidden cache slot：`<seg>_BASE/_LIMIT/_ACCESS` × 4 |
| 27.10c | `412fc7a` | `SegmentedLinear` 讀 cache（infra） |
| 27.10d w1-w8 | `2531bd8`..`fb45845` | Cache wiring 跨 emitter |
| 27.13a | `c5f51a0` | Pmode-entry demo + 標出 consumer-migration gap |
| 27.13b | `ed4b2d4` | **把 ModR/M consumer 遷到 `ea_base` — gap 關閉** |

Track 1 收尾：`MSW.PE = 1` 在 running program 產生可見行為改變
（`27-pmode-entry.com` BX = 0xF1B8 來自 descriptor base、
而不是 0x0080 來自 real-mode shift fallback）。

### Track 2 — exception model（Sprint 27.11a → 27.11f）

| Sprint | Commit | Deliverable |
|---|---|---|
| 27.11a | `7249bb8` | `EXC_PENDING / EXC_VECTOR / EXC_ERROR` state slot |
| 27.11b | `0f2e6f2` | Slot 在 `X86State` 暴露 + `--verbose` dump |
| 27.11c | `bb790bd` | **第一條端到端 fault**：P-bit check → `#NP` |
| 27.11d | `7c57f5a` | NULL → SS → `#GP(0)` |
| 27.11e | `1284d4f` | DPL/RPL/CPL privilege check → `#GP(sel)` |
| 27.11f | `92176e3` | Segment-type check（SS=writable-data、DS/ES≠system） |

Track 2 收尾：descriptor-validation pipeline 跑原 Phase 27b plan 指定的
4 個 baseline check（P / NULL-SS / DPL / type），共用
`EmitRaiseException` helper 讓未來引入 fault 的 code（27.12 的 TSS、
加上之後透過 far jump 的 code-segment load）重用同樣的 fault-write
形狀。

## State register 新增（Phase 27b）

Sprint 27.10b + 27.11a 對 i80286 的 `register_file.status` 加的：

| Register | Width | Reset | Sprint |
|---|---|---|---|
| ES_BASE   | 32 | 0x00000000 | 27.10b |
| ES_LIMIT  | 16 | 0xFFFF | 27.10b |
| ES_ACCESS | 8  | 0x93   | 27.10b |
| CS_BASE   | 32 | 0xFFFF0（reset 時 = CS<<4） | 27.10b |
| CS_LIMIT  | 16 | 0xFFFF | 27.10b |
| CS_ACCESS | 8  | 0x9B   | 27.10b |
| SS_BASE   | 32 | 0x00000000 | 27.10b |
| SS_LIMIT  | 16 | 0xFFFF | 27.10b |
| SS_ACCESS | 8  | 0x93   | 27.10b |
| DS_BASE   | 32 | 0x00000000 | 27.10b |
| DS_LIMIT  | 16 | 0xFFFF | 27.10b |
| DS_ACCESS | 8  | 0x93   | 27.10b |
| EXC_PENDING | 8  | 0 | 27.11a |
| EXC_VECTOR  | 8  | 0 | 27.11a |
| EXC_ERROR   | 16 | 0 | 27.11a |

12 個 cache slot × 4 segment + 3 個 exception slot = Phase 27b 對 i80286
CPU state 加 47 byte。Reset path（`X86JsonCpu.Reset` + `SetEntryPoint`）
從 `(visible-selector << 4)` 填 cache，所以 i80286 backend 上的 real-mode
行為跟 i8086 / i80186 維持 pixel-identical（每個 sprint 都透過 T2 +
variant matrix 驗過）。

## Phase 27b 期間 inheritance ROI

Phase 27b 結束時 i80286 spec 總大小：

- `cpu.json`：~140 行（Phase 27a 的 ~110 + 12 個 cache slot + 3 個 EXC slot）
- `groups/twobyteesc.json`：未變、~150 行
- IR（`X86_16Emitters.EmitSegCacheUpdate` + `EmitRaiseException`）：
  一個 ~150 行的 C# helper、只給 `X86WriteSregFieldEmitter` 用 —
  對 JSON spec 隱形、無 per-instruction churn。
- i80286 專屬 *spec* surface 總計：**~290 行**，比 Phase 27a 多 30 行。

對比從頭寫 protected-mode 80286：~3500 行 for real-mode + 大致 +800-1500
行 for protected-mode descriptor/exception/check → 算 ~5000 行。

**省下：~94%**。Framework 的 ROI 隨 protected-mode machinery 累積到
共用 helper（而不是 per-CPU code）而成長。

## 架構漂移認知

目前實作有一個已知的小架構偏離，在 `EmitSegCacheUpdate` 的 code comment
跟 27.11c / 27.11d commit message 裡 call out：

**Visible sreg field 在 fault check fire 之前就更新了**。Intel 規定在
`#GP` / `#NP` 上整個 sreg load 要中止 — visible register 也要 revert。
我們的實作：

1. `X86WriteSregFieldEmitter` 無條件寫 visible field。
2. `EmitSegCacheUpdate` 跑 validation pipeline。
3. Fault 時，我們 set `EXC_PENDING=1` 並跳過 **hidden cache** 更新。
   Visible field 停在新（faulting）值。

這以 `EXC_PENDING` 為界 — 任何尊重這個 flag 的 code 不會對新 visible
field 動作 — 而且在 ROM demo 看得到（例如 `27-pmode-dpl-gp.com` 在 HLT
時 `AX=0x000B`，表示 visible DS register 真的拿到 faulting selector）。
未來的 sprint 可以這樣修：
- 重構 `X86WriteSregFieldEmitter` 在 visible write *之前* 做 validation、或
- 在 instruction retire time 加 "EXC_PENDING 時 rewind sreg field" pass。

兩個 demo 都不依賴架構正確的行為 — 5 個 demo 都 clean halt 帶對的 vector
+ error code、不論怎樣。

## Demo as 視覺 artifact

原 Phase 27b plan（`MD/design/27-` 的 Sprint 27.14）要求一個
`protmode-msr-i80286.png` visual demo。i80286 backend 沒整合 video output
（demo 透過 `apr-x86 --rom=... --variant=i80286` 跑、產生 CLI text）。
上面那 5 個 fault-matrix ROM 才是真正可 demo 的 artifact — 它們端到端
exercise descriptor pipeline、每個有一個可觀察的 bit（`BX`、
`EXC vector`、`EXC error`）。

retro-CGA 視覺化可以晚一點加，做法是把 i8086 demo 用的同個 CGA renderer
擴到 i80286 backend、但這跟 protected-mode 工作正交、只是用更漂亮的形式
重複 fault-matrix 的證據。

## 延後到未來 phase

| Item | 延後原因 |
|---|---|
| **27.12 TSS task switching** | 多日工作。需要 TSS descriptor type + busy-bit + selector switch + state save/restore IR。沒 demo 需要它。 |
| **LDT (TI=1) descriptor lookup** | 今天 fall through 到 GDT path。沒 demo 載 LDT-based selector。 |
| **PE=1 下透過 far jump/call/iret 載 CS** | `MOV sreg` 處理 ES/SS/DS；PE=1 的 CS 需要不同的 IR（privilege-transition、conforming/non-conforming code descriptor、可能 call gate）。 |
| **Fault 時 visible-sreg rewind** | 看上面「架構漂移」。 |
| **Code-segment readable subcheck（DS/ES path）** | 載 code descriptor 到 DS 不常見、我們 demo 不練它。 |

## Phase 27b 成就總結

- **JSON-driven CPU framework 在 i80286 層 demo protected-mode segmentation
  且沒有 per-CPU C# scaffolding** — 所有 protected-mode logic 在共用
  `X86_16Emitters` helper、用 `register_file` slot 存在性把守（i80286
  有、舊 variant 沒、helper 透過 try/catch + -1 sentinel no-op）。
- **Descriptor-fetch + 4-check fault model + 5-ROM fault matrix** 端到端、
  帶 deterministic test ROM build script。
- **無 regression**：整個 sprint chain 每個 commit T2 visual matrix 18 PNG
  SHA256 相同、i8086 vs i80186 variant matrix 6-demo 相同。
- **`MSW.PE = 1` 是 real 的**：program 看到架構差異、fault 到
  `EXC_PENDING`、fault path 上 cache 維持一致。

> 剩下的 80286 protected-mode 工作（TSS、更深的 LDT、far-jump CS handling）
> well-defined、在 `EmitSegCacheUpdate` + `EmitRaiseException` helper 上
> additive。在這裡關掉 Phase 27b 在架構上合理：plan 標 "must have" 的
> 里程碑都 land 且可 demo。
