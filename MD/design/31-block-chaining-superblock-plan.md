# Phase 31 — Block chaining + superblock JIT 演進 plan

> **狀態**：📋 PLANNED（2026-05-17）。Phase 30.18 verifier 已 land、
> 4-CPU 全部跑通 multi-million-block NoDiff。下一個 perf 推進的自然方向是
> 把 compilation unit 從 single basic block 推到 **chained blocks** 跟
> **superblock**。
>
> **動機**：目前 block-JIT cap 64 instr、但實際 block 在 first branch 結束、
> 通常只 5-30 instr。每個 block exit 都付一次 managed→native trampoline +
> `BlockCache` lookup + dispatcher overhead。Hot loop 內這個成本主導。
>
> **參考**：[`MD/design/12-gb-block-jit-roadmap.md`](12-gb-block-jit-roadmap.md)
> P0 / P1 / P2 plan 涵蓋 single-block JIT；本文件是 single-block 之後的
> **跨 block** 演進。
>
> **不在 scope**：trace JIT、method-based JIT、loop unrolling。看下面
> [§5 為什麼這些先不做](#5-未列入-scope-的選項)。

---

## 1. 目前狀態 baseline

### 1.1 既有 infrastructure

| Component | 做什麼 |
|---|---|
| `BlockDetector` | Walk byte、decode、在 first natural boundary 停（branch / call / RET / writes_pc=always / HALT / sync / cap 64） |
| `BlockFunctionBuilder` | 把 detected block compile 成 LLVM `void(state*)` function |
| `BlockCache` | `(linearPc, generation) → fnPtr` cache。Dispatcher 查 cache 命中就直接 call。 |
| `crossJumpFollow`（gate by `APR_NO_CROSS_JUMP_FOLLOW`）| Detect 階段看到 static-target uncond branch（`JP imm`、`JR const offset`）就 follow 過去、把 target 接在當前 block IR 後面。**已經是 degenerate superblock**。 |

### 1.2 Hot-loop dispatch path 量測

對 GB cpu_instrs.gb（Phase 30.16 verifier 跑）：
- Block cache hit rate：~99.5%（暖機後）
- Per block 平均長度：~12 instr
- 每個 block exit 付：managed lookup（~30ns）+ trampoline（~5ns）+ block prologue（alloca init、register restore from state）
- 大致估計：dispatch overhead **占 hot-loop total time ~15-25%**

→ 把 dispatch 量降一個量級（block chaining）= 直接 10-20% throughput gain。

---

## 2. 演進階梯

照「邊際 ROI 高到低 / 風險低到高」排：

| 階 | 名稱 | 預期 throughput gain | 實作成本 | 對 framework spec-driven 影響 |
|---|---|---|---|---|
| **31.1** | Block chaining（patch exit jump → next block entry） | hot loop **2-3×** | 中（~3-5 day） | 零（純 dispatcher 改） |
| **31.2** | Conditional-branch follow（superblock 完整版） | tight branch 多 **1.5-2×** | 中高（~5-7 day） | 小（擴 BlockDetector + side-exit IR pattern） |
| **31.3** | Hot-loop detection + re-compile | unrolled loop **2-5×** | 高（~10-15 day） | 中（profiling state、新 emitter pass） |
| **31.4** | Trace JIT | hot path **未知**（高 variance） | 很高（~3+ week） | 大（profile-driven 不再純 spec-driven） |

**建議**：先做 31.1 + 31.2。31.3 之後再評估。31.4 不推薦（看 §5）。

---

## 3. Phase 31.1 — Block chaining

### 3.1 目標

Block A exit 時，如果 next PC 是 known constant（`writes_pc=always` to const target、或 fall-through 到 A.endPc）、直接 **patch A 的 epilogue** 跳到 B 的 entry、跳過 dispatcher。

QEMU TCG 用同個 pattern（`tb_jmp_offset` + `tcg_out_patch_jmp`）；Dynarmic、Bochs cgen 也用。

### 3.2 IR shape

目前 block A epilogue：
```llvm
exit:
  store i32 next_pc, ptr %pc_slot
  ret void
```

改成：
```llvm
exit:
  store i32 next_pc, ptr %pc_slot
  ; Slot for runtime patch — initially indirect-call dispatcher,
  ; first execution patches this to direct call(next_block_fn).
  %target = load ptr, ptr @block_a_chain_slot
  musttail call void %target(ptr %state)
  ret void
```

`@block_a_chain_slot` 初始指向一個 trampoline 函式 — 它查 cache、找到 next block fn、patch the slot pointer 到 fn entry、然後 tail-call fn。第二次起 chain slot 直接指向 fn、零 lookup。

**為什麼 musttail**：避免 hot loop 堆 stack。LLVM `musttail` guarantee tail-call optimization、跨 function 跑成 jump。x86-64 上實際就是一個 `jmp` instruction。

### 3.3 Invalidation

當 block B 被 invalidate（SMC、cache eviction、generation bump）：
- 所有指向 B 的 chain slot 必須 reset 回 trampoline
- 解法：B 的 metadata 帶一個 `List<chain_slot_ptr>`、invalidate 時 walk 它 reset

成本不大、SMC 已經有 invalidation infrastructure（`EmitSmcCoverageNotify`）。

### 3.4 預期 gain

QEMU TCG 量測 block chaining 帶 1.5-3× speedup。我們因為 block 較短（GB 平均 12 instr），預期偏 hi-end → **2-3× on tight loop**。

對 single-shot code（boot path、init）幾乎沒差、不破壞、安全 default-on。

### 3.5 工作項目

| Sprint | Deliverable | 估時 |
|---|---|---|
| 31.1a | Chain slot global pointer + trampoline shim（loader-side） | 1 day |
| 31.1b | `BlockFunctionBuilder` emit musttail call in epilogue when next PC known | 1 day |
| 31.1c | `BlockCache` 加 `ChainSlots` list、invalidate 時 reset | 1 day |
| 31.1d | Verifier compatibility（chain 不該影響 per-block diff、verify-blocks env 強制 disable chain）| 0.5 day |
| 31.1e | Bench：T2 visual matrix unchanged、loop100 throughput +N% | 0.5 day |

Total：~4 day。

---

## 4. Phase 31.2 — Conditional-branch follow（superblock）

### 4.1 目標

延伸既有的 `crossJumpFollow`：
- 目前只 follow uncond static jump（`JP imm`、`JR const`）
- 加 cond branch（`JR cc, imm`、`Jcc rel`、ARM cond-EQ 等）：emit `br i1 cond, label %taken, label %side_exit`、taken 那邊 inline target block 的 IR、untaken 走 side-exit 回 dispatcher

### 4.2 IR shape

Block A 結尾的 cond branch：
```llvm
cond_branch:
  %taken = load_flag Z
  br i1 %taken, label %follow_target, label %side_exit

follow_target:
  ; ... inline of target block B's instructions ...
  br label %exit

side_exit:
  store i32 untaken_pc, ptr %pc_slot
  ret void
```

加上 31.1 的 chain：`side_exit` 也付 chain trampoline 而不是 ret。

### 4.3 限制

- **Follow 深度 cap**（例如 ≤ 3 個 cond branch、避免 IR 爆炸）
- **Cycle accounting**：每個 inlined block 還是要付各自 cycle decrement、不能合併
- **IRQ delivery**：每個 inlined block 邊界還是要有 `sync` micro-op opportunity（Phase 14 IRQ-sync 機制不能跳）
- **Memory write sync**：inlined block 內 MMIO write 還是要 fast/slow split + sync check
- **SMC**：inlined target 如果 SMC invalidate、必須 invalidate 整個 superblock

### 4.4 預期 gain

對 tight branch（inner loop 的 `JR NZ, loop_top`）多 **1.5-2×**、因為打 branch predictor + 省 dispatch。

對 fault-path / sparse code 沒幫助（branch 不 taken 的話 side-exit 走原 path）。

### 4.5 工作項目

| Sprint | Deliverable | 估時 |
|---|---|---|
| 31.2a | `BlockDetector` 擴 follow cond branch（depth cap） | 1.5 day |
| 31.2b | `BlockFunctionBuilder` emit side-exit pattern for cond branch | 2 day |
| 31.2c | SMC invalidation 跨 follow target | 1 day |
| 31.2d | Verifier：interp 也要 follow 同個 path（cond+target 對得上）— 或 verify-blocks 強制 single-block | 1 day |
| 31.2e | Bench + visual matrix | 0.5 day |

Total：~6 day。

---

## 5. 未列入 scope 的選項

### 5.1 Hot-loop detection + re-compile（Phase 31.3）

Block A 在 N 次 invocation 內 jump 回自己 = loop。Re-compile 成 unrolled、tighter register cache、vectorize hint。

**為何先不做**：需要 profiling counter、re-compilation threshold、tier 升降 logic — 一整套 tiered JIT 基礎建設。31.1 + 31.2 解了 80% 的 perf；31.3 是「再榨 30%」、ROI 比較低。

如果 31.1 + 31.2 完成後 hot loop 還是慢、再評估。

### 5.2 Trace JIT（Phase 31.4）

TraceMonkey-style：runtime record 真實 path（含多個 cond branch outcome）、把整條 trace compile 成一個 super-large function、side-exit 給 mispredict。

**為何不推**：
- CPU emulation 沒「動態 polymorphism」這種 trace JIT 解的問題（CPU 沒 `typeof` 多 path）
- Code shape 比 JS / Python 規律很多、trace 提供的 specialization 不如 superblock 該有的
- 實作複雜度高一級、debug / verify 都更難
- 業界 emulator（QEMU、Dynarmic、Dolphin、PCSX2）都沒走這條

### 5.3 Method JIT

CPU emulation 沒明確 function boundary（CALL/RET 可以亂跳、`POP CS` 改 return target、interrupt 從任何地方進）。可以做但 ROI 跟複雜度比都不值得。

---

## 6. 風險 / 開放問題

### 6.1 Verifier 相容性

`VerifiedBlockJitRunner` per-block diff JIT vs INTERP。Block chaining 跨 block、verifier 怎麼算？兩個方向：

- **A**：verify-blocks 模式強制 disable chain（per-block diff 維持單純）— 簡單
- **B**：verifier 也跟著 chain trail、driver INTERP 跑同樣多 block — 複雜但保 production parity

V1 用 A、V2 評估 B。

### 6.2 Code cache 大小

Chain + superblock 變大、每個 block IR 變大、整體 JIT'd 機器碼變多。
QEMU TCG 用 32 MB code cache 為預設、超過就 flush。我們現在沒 flush 機制（cache 無上限）。

V1：監控 code cache size、出 warning if 過 64 MB；V2 再 implement flush。

### 6.3 Patch race condition

Block chaining 在 multi-thread emulator 內、chain slot patch 要 atomic（一個 thread compile B、patch A 的 slot 同時另一個 thread 正在從 A jump）。

我們目前 CPU thread + UI thread；JIT 跟 dispatch 都在 CPU thread。如果未來上 multi-threaded execution、要 plain atomic store + read。今天單 thread 安全。

### 6.4 Verifier 對 superblock 的 trace 軸

跨 block 的 superblock、memory write trace 來自多個 architectural step。Comparator 比較時要按 `instructionIndexWithinBlock` match，inline 過後 instrIndex 該如何 number？

提議：保留每個 instruction 原始 `linearPc`，verifier comparator 按 `linearPc` match 而非 instruction position。需要 trace record 加 `linearPc` field。

---

## 7. 建議 phasing

```
Phase 31 (next quarter):
├─ 31.1 Block chaining        ← 先做、ROI 最高、framework 改最小
└─ 31.2 Cond branch follow    ← 31.1 之後

Phase 32+ (future):
├─ 31.3 Hot-loop re-compile   ← 31.1 + 31.2 後 perf 還不夠再做
└─ Code cache flush mechanism ← 跟 31.3 一起
```

開始的 entry point：[`src/AprCpu.Core/IR/BlockFunctionBuilder.cs`](../../src/AprCpu.Core/IR/BlockFunctionBuilder.cs)
的 epilogue emit + [`src/AprCpu.Core/Runtime/BlockCache.cs`](../../src/AprCpu.Core/Runtime/BlockCache.cs)
的 cache entry struct。

---

## 8. 交叉參考

- [`MD/design/12-gb-block-jit-roadmap.md`](12-gb-block-jit-roadmap.md) — single-block JIT 的 P0/P1/P2 plan（前置）
- [`MD/design/14-irq-sync-fastslow.md`](14-irq-sync-fastslow.md) — IRQ sync 機制（superblock 內仍須遵守）
- [`MD/design/30.15d-verified-blockjit-framework-design.md`](30.15d-verified-blockjit-framework-design.md) — Verifier framework（chain 跟 superblock 要相容）
- [`MD/issue/pc/deferred-26-30x-summary.md`](../issue/pc/deferred-26-30x-summary.md) — Phase 26-30.x deferred 項目（本文件補上「框架 perf 演進」這條 axis）

## 9. 業界 reference

- **QEMU TCG block chaining**：`tcg/tcg.c` 的 `tcg_out_goto_tb` + `tb_target_set_jmp_target`
- **Dynarmic superblock**：[`A64::Optimization::DeadStoreElimination`](https://github.com/yuzu-emu/dynarmic) 等 pass、IR 階段做 cross-block 分析
- **HP Dynamo trace cache**：1999 PLDI paper "Dynamo: A Transparent Dynamic Optimization System"
- **TraceMonkey**：2009 PLDI "Trace-based Just-in-Time Type Specialization for Dynamic Languages"
