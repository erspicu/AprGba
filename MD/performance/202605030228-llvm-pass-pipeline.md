# Explicit LLVM new-pass-manager pipeline（Phase 7 H.a）— **GBA +1% direct, unblocks C.b**

> **策略**：MCJIT 的 `OptLevel=3` 影響 **backend codegen** (instruction
> selection / register allocation) 但 **不跑 IR-level pass pipeline**
> (mem2reg / GVN / DSE / instcombine / simplifycfg)。在 module 移交給
> MCJIT 之前用 `LLVM.RunPasses` (新 pass manager API) 顯式跑 curated
> pass list。
>
> **Hypothesis**：(1) IR-level optimization 應有空間，特別是 alloca →
> SSA 提升。(2) C.b alloca-shadow lazy flag 之前無 perf 收益的根本原因
> 是 MCJIT 沒跑 mem2reg；H.a 後 retry C.b 應該見效。
>
> **結果**：
> - 直接收益：**GBA arm +1.4% (8.33 → 8.45 MIPS)，GBA thumb +0.8%
>   (8.39 → 8.46)**, GB 兩條 path 持平。
> - 間接收益（更重要）：**unblocks C.b alloca-shadow** + 任何未來用
>   alloca/SSA pattern 的優化。
>
> **決定**：保留改動。一次性 startup cost +50ms 可接受。

---

## 1. 結果（多 run 平均）

| ROM                     | Backend     | runs | min  | **avg** | max  | B.h avg | starting baseline | **Δ vs B.h** | Δ vs baseline |
|-------------------------|-------------|------|-----:|--------:|-----:|--------:|------------------:|-------------:|--------------:|
| GBA arm-loop100.gba     | json-llvm   | 4    | 8.39 | **8.45**| 8.52 | 8.33    | 3.82              | **+1.4%**    | **+121.2%**   |
| GBA thumb-loop100.gba   | json-llvm   | 3    | 8.30 | **8.46**| 8.58 | 8.39    | 3.75              | +0.8%        | **+125.6%**   |
| GB 09-loop100.gb        | legacy      | 3    | 32.04| **32.24**| 32.55| 32.75  | 32.76             | −1.5% noise  | unchanged     |
| GB 09-loop100.gb        | json-llvm   | 3    | 6.42 | **6.50**| 6.56 | 6.48    | 2.66              | +0.3% noise  | **+144.4%**   |

real-time × 持續 plateau 在 2.0× (GBA) / 13× (GB JIT)。

GB legacy 的 −1.5% 是 noise — H.a 完全不影響 legacy backend (LegacyCpu
不走 LLVM)。

---

## 2. 改動內容

### `src/AprCpu.Core/Runtime/HostRuntime.cs`

新增 method `RunOptimizationPipeline()`，在 `Compile()` 內 BindUnboundExternsToTrap
之後、CreateMCJITCompiler 之前呼叫：

```csharp
private void RunOptimizationPipeline()
{
    const string passes = "mem2reg,instcombine<no-verify-fixpoint>,gvn,dse,simplifycfg";
    var optionsHandle = LLVMPassBuilderOptionsRef.Create();
    try
    {
        var passesBytes = System.Text.Encoding.ASCII.GetBytes(passes + "\0");
        fixed (byte* passesPtr = passesBytes)
        {
            var err = LLVM.RunPasses(_module, (sbyte*)passesPtr,
                default(LLVMTargetMachineRef), optionsHandle);
            if (err != null) { /* error handling */ }
        }
    }
    finally { optionsHandle.Dispose(); }
}
```

Pass list 解釋：
- **mem2reg** — promote alloca → SSA + PHI nodes (這個是 lazy flag 等
  alloca-pattern 優化的關鍵)
- **instcombine\<no-verify-fixpoint\>** — peephole 簡化 (no-verify form
  避開 ARM Branch_Exchange BX 的 select-chain 不到 1-iter fixpoint
  問題)
- **gvn** — global value numbering (CSE)
- **dse** — dead store elimination
- **simplifycfg** — basic block 合併 + branch 簡化

選 5 個是 curated minimum。"default\<O3\>" 跑全套但 compile time 多很多
(每 spec module ~500ms vs ~50ms)，目前不需要。

---

## 3. 收益相對 modest 的原因

| 階段 | 改動 | 收益 |
|---|---|---|
| 7.B.a OptLevel 0 → 3 | MCJIT backend 升 O3 | perf-neutral |
| **7.H.a Explicit IR passes** | **mem2reg/GVN/DSE/instcombine/simplifycfg** | **+1-2% GBA** |

兩個一起看，全套 LLVM 優化大概貢獻 ~1-2% 的 perf。其他 95%+ 都來自
dispatcher / mem-bus / inline 改動。

原因：
1. **per-instruction function 太小** — 5-10 個 IR ops，LLVM 沒太多空間
   做大型 transformation (loop opts、inline、scalar replacement of
   aggregates)。
2. **MCJIT backend O3 已包含 isel + regalloc**，這對小 function 影響大
   — IR-level passes 對 hot loop 幫助有限。
3. **dispatcher 端瓶頸** — 即使 JIT'd code 完美，per-instruction indirect
   call + state buffer access 是固定 overhead，IR 優化無法消除。

H.a 真正價值在 **unblock C.b alloca-shadow** — 之前 C.b 沒 ship 是因為
MCJIT 沒 mem2reg，alloca 沒被 lift。現在 retry 應該能看到 lazy flag
真正的收益。

---

## 4. Phase 7 累計（11 步）

| 階段 | GBA arm | GBA thumb | GB legacy | GB json-llvm |
|---|---:|---:|---:|---:|
| Phase 5.8 starting baseline | 3.82 / 0.9× | 3.75 / 0.9× | 32.76 / 67× | 2.66 / 5.5× |
| 7.B.a OptLevel O3 | 3.71 | 3.73 | 32.50 | 2.70 |
| 7.F.x id-keyed fn cache | 3.90 | 4.69 | 32.49 | 4.83 |
| 7.F.y pre-built decoded | 5.94 / 1.4× | 6.27 / 1.5× | 32.65 | 6.30 / 12.9× |
| 7.B.e cache state offsets | 7.19 / 1.9× | 6.26 | 34.29 | 6.41 |
| 7.B.f permanent pin | 7.13 | 7.97 / 1.9× | 33.67 | 6.31 |
| 7.B.g AggressiveInlining bus | 8.05 / 2.0× | 8.10 | (n/a) | (n/a) |
| 7.E.a fetch fast path | 8.25 | 8.16 | (n/a) | (n/a) |
| 7.E.b mem trampoline fast | 8.30 | 8.21 | (n/a) | (n/a) |
| 7.C.a width-correct flag | 8.26 | 8.24 | 32.75 | 6.48 |
| 7.B.h Tick/IRQ inline | 8.33 / 2.0× | 8.39 / 2.0× | (n/a) | (n/a) |
| **7.H.a LLVM pass pipeline** | **8.45 / 2.0×** | **8.46 / 2.0×** | 32.24 | 6.50 |

GBA 兩條 path 雙雙 8.45+ MIPS。GB JIT 6.50 MIPS / 13.4×。

---

## 5. 下一步

**強烈建議下一步攻 C.b alloca-shadow retry** — H.a 已解 unblock
prerequisite，現在重試 C.b alloca approach 應該能看到 lazy flag 的
+5-15% 預期收益。

之後其他 H 系列項目：
- H.b spec-time IR pre-processing (dead flag elim)
- H.d LR35902 dispatcher / bus parity
- H.e cycle accounting batching
- H.g LLVM custom calling convention

---

## 6. 改動範圍（驗證）

```
src/AprCpu.Core/Runtime/HostRuntime.cs:
  + private void RunOptimizationPipeline() — 用 LLVM.RunPasses
  ~ Compile() 在 BindUnboundExternsToTrap 之後、CreateMCJITCompiler
    之前呼叫 RunOptimizationPipeline

驗證：
  - 345/345 unit tests pass
  - 9/9 Blargg cpu_instrs sub-tests pass on json-llvm (02–11)
  - One-shot startup cost +~50ms per spec module (acceptable)
```

---

## 7. 相關文件

- `MD/performance/202605030002-jit-optimisation-starting-point.md` — baseline
- `MD/performance/202605030148-lazy-flag-attempt-postmortem.md` — C.b attempt history (H.a 解 blocker)
- 其他 Phase 7 perf notes
- `MD/design/03-roadmap.md` Phase 7 — H.a 標 done
