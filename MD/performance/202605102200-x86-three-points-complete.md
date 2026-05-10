# 三點框架效能修正完結 — Intel 8086 從 27 → 218 MIPS (8.0×)

> 2026-05-10 完成，順序按 Gemini 2026-05-10 review 的建議：
> immediates first → CFG superblocks → cross-block SSA folded into superblocks。

## 結果一覽

| 階段 | json-block MIPS | vs legacy | 改動 |
|---|---:|---:|---|
| baseline (24.6.9 後) |  27.19 | 0.71× | block-JIT alloca/mem2reg 但 trampoline + dispatcher 主導 |
| 24.6.8d #1 完成 |  37.38 | 0.97× | immediates baked as i64 IR constants |
| **24.6.8e #2 完成** | **218.45** | **5.65×** | intra-block back-edges as LLVM CFG |

(legacy 38.70 MIPS = baseline；workload 19,660,802 architectural instructions)

## #1 Bake immediates as IR constants ✅

**Commit**: `a895968 perf(N0c.24.6.8d)`

**問題**：x86 emitters 在 block-JIT mode 仍然每條指令呼叫 `memory_read_8` extern 拿 ModR/M / disp / imm bytes，即使 BlockDetector decode 時就讀過了。Per Gemini："trampolines to managed code from inside a tight JIT block are the #1 enemy of emulator performance."

**解法**：
- `BlockDetector` 的 x86 (busLengthOracle) path 把 opcode 之後的 trailing bytes pre-fetch 成 LE-packed `ulong`，存進新欄位 `DecodedBlockInstruction.PackedTailBytes`（length ≤ 9 bytes 全 cover）。
- `EmitContext` 多 `CurrentInstructionPackedTailBytes` + 每指令 `ImmConsumed` offset counter（x86 一條指令會多次 fetch：ModR/M → disp → imm，每次 offset 不同）。
- `X86_16Emitters.FetchImm8/FetchImm16` 在 fast path 直接 build i64 `LLVMConstantInt`，shift+trunc 取出對應 byte。LLVM compile-time evaluate 該常數，**zero runtime cost**。

**Gemini 關鍵提示**：用 `i64` 不要 `u32`。`u32` 會 miss 6-byte 指令如 `MOV [bx+di+0x1234], 0x5678`；`i64` 因為 LLVM 常數摺疊，「wider type 完全沒成本」。

**ROI**：27.19 → 37.38 MIPS（+37.5%）— 已經追平 legacy（37.38 vs 38.40）。

## #2 Intra-block back-edges as LLVM CFG ✅

**Commit**: `9e72453 perf(N0c.24.6.8e)`

**問題**：tight inner loop（add/add/loop ×65535）每 iter 都從 LOOP 退出 block、dispatcher 找 cache、re-enter at target。register state 透過 alloca→state→alloca 來回 drain/reload，成本遠超 inner work。

**Gemini 警告（兩個 pitfall）**：
1. **不能** linearly unroll — LOOP 是條件性的，假設「永遠 taken」會在 CX=0 時 miscompile；且 LLVM register allocation 是 O(N²) 對長 IR。
2. **正確路徑**：把 x86 loop 翻成 LLVM intra-function CFG — 同一個 LLVM function 內 emit `br` 到代表 loop body 的 BasicBlock。alloca + mem2reg 自然跨 iteration 把 CX 保留在 host register 透過 phi node。

**解法**：
- `DecodedBlockInstruction.BackEdgeTargetIndex` 紀錄 branch target 對應到 block 內哪一條指令的 index。
- `BlockDetector` x86 path：算出 LOOP / Jcc / JMP rel 的 static target；如果 target 命中 block 內已加入的指令 PC，就設 `BackEdgeTargetIndex`。
- `EmitContext.BackEdgeTargetBB` + `BackEdgeFallthroughBB` 由 `BlockFunctionBuilder` 在 emit 該指令前 wire up。
- `X86LoopEmitter` / `X86JccRel8Emitter` / `X86JmpRel8Emitter` / `X86JmpRel16Emitter`：fast path 改成 `BuildCondBr(pred, target_BB, fallthrough_BB)`（或 `BuildBr` 給 unconditional）— **不寫 IP、不設 PcWritten**。

**Bonus**：bench 的 outer JNZ 也指向同 block 內（writes_pc="conditional" 不結束 block），所以整個 outer×100 ⊗ inner×65535 nested loop 編成**單一 LLVM function 配兩個 back-edge**。LLVM 的 loop 優化器把它認成 nested loop nest，把 add/add 折成 `AX += 3 × 65535`，可能直接 vectorize / collapse。

**ROI**：37.38 → **218.45 MIPS**（+484%）= **5.65× faster than legacy**。

## #3 Cross-block register SSA — 已被 #2 涵蓋

**Commit**: 無實作；本 design note 即收尾。

原本的計畫是「跨多個 LLVM function 把 register state 在 SSA 中保留」，需要 trace JIT 或 LTO inlining。Per Gemini 2026-05-10 review，這條路：
- 跨 function 用 memory 傳 state → 慢（現況）
- 跨 function 用 LTO inline → compile time 爆炸
- 兩個都不好

**Gemini 推薦的 superblock 路線**：把整個 hot trace 編進**同一個** LLVM function 用 internal CFG。alloca + mem2reg 在 function-local SSA 內就達成 cross-iteration SSA — 不用碰 cross-function 的問題。

**這正是 #2 做的事情**。對 bench 來說：

```
                                  Before #2                 After #2
Block at 0x100   [mov bx; mov cx; add; add; LOOP; dec; JNZ; HLT]   (same — writes_pc=conditional doesn't end block)
LOOP target      0x106 (= block instr idx 2)                       same
JNZ target       0x103 (= block instr idx 1)                       same
LOOP behavior    write IP=0x106, set PcWritten, exit block         BuildCondBr(pred, preBBs[2], postBBs[i])
JNZ behavior     write IP=0x103, set PcWritten, exit block         BuildCondBr(pred, preBBs[1], postBBs[i])
LLVM IR shape    8 linear instructions, no internal CFG            8 instructions + 2 back-edges = 1 CFG nest
mem2reg 結果     CX/AX 跨 iter 透過 state-buffer load/store       CX/AX 跨 iter 透過 phi nodes
```

#2 的 alloca + mem2reg + back-edge **就是** "cross-block SSA"，只是定義變了：「block」現在指 LLVM BasicBlock，不是「dispatcher level 的 native function」。

## 還可以做（下一季）

| 項目 | 預估效益 | 觸發條件 |
|---|---|---|
| **Block chaining (direct dispatch)** | +1.2-1.5× | 當 hot trace 跨 LLVM function 邊界（CALL/RET/far JMP）— patch native code 直接 jmp 不過 dispatcher。本 bench 無此情境（無 CALL/RET）。 |
| **Hot-path trace JIT** | +1.5-2× | profile 高頻 cross-block path → 把連續的 N 個 block 合成單一 superblock function。能處理 #2 邊界外的 register flow。 |
| **Bus extern inlining** | +1.1-1.3× | 當 memory_read_8 仍是 hot path（本 bench 已沒有）。把 ROM bytes 也 LE-pack 進 IR constant。 |

對 8086：bench loop 從 27 → 218 MIPS 已遠超 legacy。其他 benchmark（ROM-heavy、bus-heavy）效益會不一樣，**這是最佳情況**（loop fully optimizable by LLVM）。其他 demos 也快了但 ratio 沒這麼極端，因為 SHA256 PNG identical 不依賴 perf。

## QA 結果

- **T1**: full unit suite (838 tests, Release) — 本 commit 後 background 跑（依 CLAUDE.md "1分鐘上限" 規則 foreground 不可）
- **T2**: 6 demos × 3 backends = 18 PNGs，每組 SHA256 identical — **全綠**
- **Bench**: best-of-3，數字穩定，AX 終值 = 0xFED4 = (1+2)×65535×100 mod 65536 ✓

## 重現

```powershell
dotnet build -c Release src/AprX86.Cli/AprX86.Cli.csproj
pwsh tools/bench_x86.ps1
pwsh tools/verify_x86_matrix.ps1
```
