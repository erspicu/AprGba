# Phase 27 — Intel 80286 完成計畫

> **狀態**：Phase 27a ✅ **COMPLETE**（2026-05-11）。Real-mode 80286
> system instruction set 出貨：14 of 14 instruction cover 完、7 個新
> state register、4 個驗證過的 round-trip demo。收尾文件：
> `MD/performance/202605110100-i80286-realmode-complete.md`。
>
> **Phase 27b（保護模式）✅ COMPLETE**（2026-05-11）。Descriptor-based
> segmentation + 4-check fault model 端到端在 i80286 backend 上運行。
> 5-ROM fault matrix 可 demo。收尾文件：
> `MD/performance/202605110200-i80286-pmode-fault-model-complete.md`。
> Sprint 27.12（TSS task switching）延後到未來 phase（多日工作、
> 在這裡 land 的 `EmitSegCacheUpdate` + `EmitRaiseException` helper
> 上 additive）。
> **前置**：Phase 26 v1（`6b1e2d6`..`7e6cf16`）— minimum-viable real-mode
> 80286 出貨、chain depth=3、0F prefix infra、CLTS+SMSW。
> **目標（已達成）**：完成 80286 實作。兩條 track：
>   - **Phase 27a — real-mode 完成**（5 sprint）✅
>   - **Phase 27b — 保護模式**（22 sprint、micro-sprint cadence）✅
>     （Sprint 27.12 TSS task switching 刻意延後到獨立 phase — 在這裡
>     land 的 helper 上 additive。）
>
> Phase 26 的收尾文件列出延後工作；本文件規劃如何 land 它們。兩條 track
> 都關了；本文件保留為歷史紀錄 + sprint-status 參考。

---

## Phase 27a — real-mode 完成

### 今天還缺什麼

Phase 26 v1 出了：
- CLTS（no-op stub、無 MSW state）
- SMSW（return 常數 0xFFF0）

Phase 27a 補完 80286 system-instruction 其他、real-mode semantics：

| Instruction | Encoding | Real-mode 行為 |
|---|---|---|
| LGDT m48     | 0F 01 /2 | 載 6-byte（16-bit limit + 24-bit base）到 GDTR |
| LIDT m48     | 0F 01 /3 | 同上 for IDTR |
| SGDT m48     | 0F 01 /0 | 儲存 GDTR 到 6-byte memory |
| SIDT m48     | 0F 01 /1 | 同上 for IDTR |
| LMSW r/m16   | 0F 01 /6 | 寫低 16 bit 到 MSW。PE bit (bit 0) 在我們的範圍內 real mode 下忽略（進入保護模式是 Phase 27b）。 |
| SMSW r/m16   | 0F 01 /4 | 讀 MSW（real value、不是常數） |
| LLDT r/m16   | 0F 00 /2 | 載 LDT register |
| SLDT r/m16   | 0F 00 /0 | 儲存 LDT register |
| LTR r/m16    | 0F 00 /3 | 載 Task register |
| STR r/m16    | 0F 00 /1 | 儲存 Task register |
| LAR r16, r/m16 | 0F 02 | Load access rights — real-mode no-op（return 0、清 ZF） |
| LSL r16, r/m16 | 0F 03 | Load segment limit — real-mode no-op |
| VERR r/m16   | 0F 00 /4 | Verify segment readable — real-mode no-op（清 ZF） |
| VERW r/m16   | 0F 00 /5 | Verify segment writable — real-mode no-op（清 ZF） |

### State 新增

需要新 status register：
- `MSW`（16-bit）— Machine Status Word。Reset = 0xFFF0。
- `GDTR_BASE`（32-bit、用低 24 bit）— GDTR base address
- `GDTR_LIMIT`（16-bit）— GDTR limit
- `IDTR_BASE`（32-bit）— IDTR base
- `IDTR_LIMIT`（16-bit）— IDTR limit
- `LDTR`（16-bit）— LDT register selector
- `TR`（16-bit）— Task register selector

需要 schema 擴充：`register_file_diff` 讓 child spec 加新 status register
（跟 instruction_set_diff 平行）。目前 child spec 是整個繼承 register_file
或整個 override；沒 additive diff。

### Sprint 拆解

| Sprint | Deliverable | 估時 |
|---|---|---|
| 27.1 | MSW state register + real LMSW + SMSW 讀 real MSW | 3-4h |
| 27.2 | LGDT/LIDT/SGDT/SIDT + GDTR/IDTR state slot | 4-5h |
| 27.3 | 0F 00 group（SLDT/STR/LLDT/LTR/VERR/VERW）+ LDTR/TR slot | 3-4h |
| 27.4 | LAR/LSL real-mode stub | 2h |
| 27.5 | Phase 27a 收尾文件 + perf note | 1h |

連續工作 ~1-2 天；有 /loop 中斷 ~1 週。

---

## Phase 27b — 保護模式

多週工作。按 blocker 排序：

| Sprint | Deliverable | 估時 |
|---|---|---|
| 27.6 | Descriptor table format parsing（8-byte segment descriptor） | 1d |
| 27.7 | Selector format + descriptor lookup helper | 1d |
| 27.8 | LGDT/LIDT real（load + segment load 時 descriptor table fetch） | 2d |
| 27.9 | Privilege level（CPL/RPL/DPL）+ ring transition | 2-3d |
| 27.10 | Segmentation 重寫（selector → base/limit lookup） | 3-4d |
| 27.11 | 新 exception model（#GP/#SS/#NP/#TS/#UD 帶 error code） | 2-3d |
| 27.12 | TSS task switching | 3-4d |
| 27.13 | Protected-mode entry（LMSW PE bit handling） | 1-2d |
| 27.14 | Phase 27b 收尾 + visual demo（`protmode-msr-i80286.png`） | 1d |

總計 ~3-4 週。真正複雜的東西在這。

**結果（2026-05-11）**：Phase 27b 在一個延長 /loop session 內透過
micro-sprint cadence 收掉（27.6 → 27.14，見下面狀態表）。
Sprint 27.12（TSS task switching）刻意延後 — 在 `EmitSegCacheUpdate`
+ `EmitRaiseException` helper 上 additive、為未來 phase 定義清楚。
收尾文件：`MD/performance/202605110200-i80286-pmode-fault-model-complete.md`。

---

## Phase 27a Sprint 狀態

| Sprint | 狀態 | Commit | 完成日 |
|---|---|---|---|
| 27.1 MSW + real LMSW | ✅ | `95e5138` | 2026-05-11 |
| 27.2 LGDT/LIDT + GDTR/IDTR | ✅ | `5c2a70a` | 2026-05-11 |
| 27.3 0F 00 group | ✅ | `13dd781` | 2026-05-11 |
| 27.4 LAR/LSL stub | ✅ | `78cf9fd` | 2026-05-11 |
| 27.5 27a 收尾文件 | ✅ | (本 commit) | 2026-05-11 |

## Phase 27b Sprint 狀態

22 個 micro-sprint，全部 2026-05-11 land。按 track 分組便於閱讀；
每個 track 內按時序。

### Track 1 — Descriptor-fetch infrastructure（Sprint 27.6 → 27.10d）

| Sprint | 狀態 | Commit | 完成日 |
|---|---|---|---|
| 27.6  Descriptor + Selector record、parse/build helper | ✅ | `11fdc98` | 2026-05-11 |
| 27.7  在 IMemoryBus 上的 ReadDescriptor / WriteDescriptor | ✅ | `9f819b5` | 2026-05-11 |
| 27.8  Msw struct + IsProtectedMode（PE-bit reader） | ✅ | `0dd6404` | 2026-05-11 |
| 27.9  Privilege-level helper（CPL/RPL/DPL） | ✅ | `553909c` | 2026-05-11 |
| 27.10a Helper 整合 test（端到端 mock） | ✅ | `cefed78` | 2026-05-11 |
| 27.10b Hidden cache slot：<seg>_BASE/_LIMIT/_ACCESS × 4 | ✅ | `97e19ea` | 2026-05-11 |
| 27.10c SegmentedLinear by-name overload（cache-aware infra） | ✅ | `412fc7a` | 2026-05-11 |
| 27.10d wave 1 — FetchImm 用 CS_BASE | ✅ | `2531bd8` | 2026-05-11 |
| 27.10d wave 2 — Stack op + by-name Read/Write overload | ✅ | `126e2cc` | 2026-05-11 |
| 27.10d wave 3 — PushReg/PushModRm/PushSpPreDec by-name | ✅ | `66dc4df` | 2026-05-11 |
| 27.10d wave 4 — ea_base alias（EA-compute 基礎） | ✅ | `3605c42` | 2026-05-11 |
| 27.10d wave 5 — ea_base cache lookup（override path） | ✅ | `7d529f3` | 2026-05-11 |
| 27.10d wave 6 — segIdx 追蹤、統一 cache lookup | ✅ | `c765352` | 2026-05-11 |
| 27.10d wave 7 — MOV sreg 更新 cache（real-mode shape） | ✅ | `3851757` | 2026-05-11 |
| 27.10d wave 8 — MOV sreg 的 Descriptor-fetch path（PE=1） | ✅ | `fb45845` | 2026-05-11 |

### Track 1 — 端到端 activation（Sprint 27.13a/b）

| Sprint | 狀態 | Commit | 完成日 |
|---|---|---|---|
| 27.13a Protected-mode entry demo + ea_base consumer gap surface | ✅ | `c5f51a0` | 2026-05-11 |
| 27.13b 把 ModR/M consumer 遷到 ea_base — gap 關閉 | ✅ | `ed4b2d4` | 2026-05-11 |

27.13b 之後：`MSW.PE = 1` 對 running program 產生可見行為改變 —
`27-pmode-entry.com` 從 descriptor base 0x100 讀到 `BX=0xF1B8`，
而不是從 real-mode `(sel << 4)` fallback 讀到 `BX=0x0080`。

### Track 2 — Exception model（Sprint 27.11a → 27.11f）

| Sprint | 狀態 | Commit | 完成日 |
|---|---|---|---|
| 27.11a Exception state slot（EXC_PENDING / VECTOR / ERROR） | ✅ | `7249bb8` | 2026-05-11 |
| 27.11b 在 X86State 暴露 EXC_* + verbose CLI dump | ✅ | `0f2e6f2` | 2026-05-11 |
| 27.11c P-bit check + #NP fault（第一條端到端 fault path） | ✅ | `bb790bd` | 2026-05-11 |
| 27.11d NULL-selector → #GP for SS load（PE=1） | ✅ | `7c57f5a` | 2026-05-11 |
| 27.11e DPL/RPL/CPL privilege check → #GP | ✅ | `1284d4f` | 2026-05-11 |
| 27.11f Segment-type check（SS=writable-data、DS/ES≠system） | ✅ | `92176e3` | 2026-05-11 |

27.11f 之後：4-baseline-check fault model 上線（P / NULL-SS / DPL /
type），全部共用 `EmitRaiseException` helper 做一致的 EXC_* slot 寫入。

### 收尾

| Sprint | 狀態 | Commit | 完成日 |
|---|---|---|---|
| 27.14 Phase 27b 收尾文件 + 5-ROM fault matrix demo | ✅ | `4066b66` | 2026-05-11 |

### 延後到未來 phase

| Sprint | 延後原因 |
|---|---|
| 27.12 TSS task switching | 多日。需要 TSS descriptor type + busy-bit toggle + state save/restore IR。在 `EmitSegCacheUpdate` + `EmitRaiseException` 上 additive；demo 不需要。 |
| LDT (TI=1) descriptor lookup | 今天 fall through 到 GDT。沒 demo 載 LDT-based selector。 |
| PE=1 下透過 far jump/call/iret 載 CS | IR 形狀不同（privilege-transition + conforming/non-conforming code descriptor + 可能 call gate）。MOV sreg 只 cover ES/SS/DS。 |
| Visible-sreg fault 時 rewind | Visible field 在 fault check fire 之前就更新；以 EXC_PENDING 為界。詳見收尾文件「Architectural drift acknowledged」段。 |
| DS/ES 的 code-segment readable subcheck | 用 MOV 載 code 到 DS 不尋常；無 demo exercise 它。會是 27.11f ~30 行的擴充。 |
