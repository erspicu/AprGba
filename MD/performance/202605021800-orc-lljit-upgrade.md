# A.3 ORC LLJIT upgrade — perf-neutral infrastructure swap

> **目的不是 perf**：A.3 是 block-JIT (A.4–A.7) 的 enabler。MCJIT 的
> `Compile()` 把整個 module finalize，事後不能 add new function。
> Block-JIT cache miss 必須 lazy compile new block 並 add 進 live JIT
> — 用 MCJIT 只能每個 block 開獨立 engine（per-block JIT）成本高。
> ORC LLJIT 的 `OrcLLJITAddLLVMIRModule` 對 main JITDylib 可重複呼叫，
> 跨 module 的 symbol resolution 自動處理。
>
> **結果**：351/351 unit tests + Blargg cpu_instrs 11/11 全綠；perf
> 跟 MCJIT 在 ±2% noise 內（GBA +0.5–0.7%, GB JIT +1.8%）— 純
> infrastructure swap 沒帶來 perf 倒退，也沒帶來 perf 進步。下一步
> A.4 接著做就有 ORC 的 module-add API 可用。

---

## 1. 結果（多 run avg）

| ROM                     | Backend     | runs | min  | **avg** | max  | C.b retry baseline (MCJIT) | **Δ vs MCJIT** |
|-------------------------|-------------|------|-----:|--------:|-----:|----------------------------:|---------------:|
| GBA arm-loop100.gba     | json-llvm   | 4    | 8.36 | **8.55**| 8.63 | 8.49                        | +0.7% (noise)  |
| GBA thumb-loop100.gba   | json-llvm   | 4    | 8.46 | **8.55**| 8.61 | 8.51                        | +0.5% (noise)  |
| GB 09-loop100.gb        | legacy      | 3    | 32.05| **32.49**|32.92| 31.66                       | +2.6% (noise)  |
| GB 09-loop100.gb        | json-llvm   | 3    | 6.59 | **6.64**| 6.71 | 6.52                        | +1.8% (noise)  |

GB legacy 的 +2.6% 是 noise — A.3 不影響 legacy backend，純 timing 變動。

Setup time（spec compile + JIT engine init）：MCJIT 322ms → ORC LLJIT
~317ms — 也在 noise 內。

---

## 2. 為什麼換 ORC LLJIT

### 2.1 MCJIT 的結構性限制

MCJIT 的工作模型是「一次 finalize 整個 module」：

```
LLVMCreateMCJITCompiler(out engine, module, options);
// 之後 module 變 read-only — 任何 AddFunction 都不會 reach JIT'd code。
addr = LLVMGetFunctionAddress(engine, "name");
```

對 per-instruction JIT 沒問題：spec compile 時把所有指令的 function
全部 emit 進同一個 module，Compile() 一口氣 codegen 完，之後 lookup
就好。

對 block-JIT 是 hard blocker：每個 hot block detect 出來才知道要
emit 哪段 IR。Cache miss → emit block IR → 加進 JIT → 拿 fn ptr →
跳過去執行。MCJIT 沒有 add-after-compile API。

### 2.2 ORC LLJIT 的 model

ORC 的工作模型是「JITDylib + module add」：

```
OrcCreateLLJIT(&lljit, builder);                     // 建 engine
OrcLLJITAddLLVMIRModule(lljit, mainJD, tsm1);        // 加 module 1
OrcLLJITAddLLVMIRModule(lljit, mainJD, tsm2);        // 加 module 2 (any time)
OrcLLJITLookup(lljit, &addr, "name");                // 查任何 module 裡的 symbol
```

關鍵特性：
- 多次 `AddLLVMIRModule` 對同一個 JITDylib，所有 module 在同一個
  symbol namespace 共存
- 跨 module 的 symbol reference 自動 resolve（block module 可以 call
  initial module 裡的 helper symbol — 不需手動 link）
- 內部用 `LazyCallThroughManager` 做 on-demand materialization (我們
  目前還沒利用，但 future tier compilation 可以)

### 2.3 為什麼 perf neutral

我們目前還是 per-instruction JIT — 一次 emit 全部 spec function 成
一個 module，handed to ORC 一次性 compile。對這個 case，ORC 跟 MCJIT
跑出來的 native code 同 codegen pipeline，perf 差異只來自 engine
overhead（dispatch / lookup），ms 級無法量到 MIPS 級差異。

A.4 開始加 block-JIT 後，blocks 共享 spec functions 的 symbol，ORC
會發揮 lazy + cross-module resolution 的價值。

---

## 3. 改動內容

### 3.1 `src/AprCpu.Core/Runtime/HostRuntime.cs` 重寫（~290 行）

**保留**：
- `Build / BindExtern / Compile / GetFunctionPointer` API 同原樣
- inttoptr-globals 的 extern binding（engine-agnostic）
- BindUnboundExternsToTrap（trap stub for unwired externs）
- RunOptimizationPipeline（H.a 的 mem2reg/instcombine/gvn/dse/simplifycfg）
- 所有 field offset accessor

**改動**：
- 引擎建立：`OrcCreateLLJITBuilder` → `OrcCreateLLJIT(&lljit, builder)`
- JITDylib 拿 main：`OrcLLJITGetMainJITDylib(lljit)`
- TargetData 改從 `OrcLLJITGetDataLayoutStr` → `LLVMTargetDataRef.FromStringRepresentation`
- 模組加進 JIT：用單一 ThreadSafeContext + `OrcCreateNewThreadSafeModule`
  → `OrcLLJITAddLLVMIRModule`
- Lookup：`OrcLLJITLookup(lljit, &addr, name)` 取代 `engine.GetFunctionAddress`
- Init：拿掉 `LinkInMCJIT()`（ORC 不需要 — LLJITBuilder 自選 object linking layer）
- Dispose：`OrcDisposeLLJIT` + `OrcDisposeThreadSafeContext`

**新增**：
- `AddModule(LLVMModuleRef)` — post-Compile 加 module 進 live JIT。
  跑 BindUnboundExternsToTrap + RunOptimizationPipeline 後 wrap 進
  TSM 加 LLJIT。為 A.4 block-JIT cache-miss path 鋪路。
- `ThrowIfError / ExtractAndDispose` — LLVM error message 的 helper

### 3.2 `src/AprGba.Cli/Program.cs` + `src/AprGb.Cli/Program.cs`

setup time 訊息字串：`MCJIT` → `ORC LLJIT`。純 cosmetic，誠實標示
backend。

---

## 4. 為什麼可以保留 inttoptr-globals 模式

ORC 提供 `OrcAbsoluteSymbols` + `OrcJITDylibDefine` 把 trampoline
function pointer 直接註冊成 JIT symbol，這是「正規」的 extern 注入
方式。我們**沒採用**，理由：

1. **Engine-agnostic 已知能跑**：inttoptr globals 在 MCJIT + ORC
   都 work。AbsoluteSymbols 是 ORC-only。
2. **Phase 5.7 投注的 Windows COFF / RIP-relative load workaround
   不用重做**：現有 setup 已避開所有已知坑。
3. **Bind 階段更早 — 在 IR 層完成**：caller 看到的 mental model 是
   「modify the IR」，不是「inject a symbol」。Code review / debug
   時 dump IR 直接能看到 trampoline 地址。

未來 A.4 block module 如果需要 call 任何「initial module 沒有」的
新 trampoline（unlikely — block 只 call memory bus + bank swap，那些
initial module 已 declare），再考慮 AbsoluteSymbols。

---

## 5. 驗證

```
$ dotnet test AprGba.slnx
已通過! - 失敗: 0，通過: 351，略過: 0，總計: 351

$ dotnet src/AprGb.Cli/.../apr-gb.dll --cpu=json-llvm \
    --rom=test-roms/blargg-cpu/cpu_instrs.gb --frames=12000
01:ok  02:ok  03:ok  04:ok  05:ok  06:ok  07:ok  08:ok  09:ok  10:ok  11:ok
Passed all tests
```

351 unit tests + Blargg cpu_instrs 11 sub-tests 全綠 = ORC LLJIT 對
ARM7TDMI + LR35902 兩個 spec 都正確 codegen + dispatch。

---

## 6. 下一步

A.3 完成 → A.4 (code cache, hashmap PC → block fn pointer) 解鎖。
A.4 流程：

1. Block-JIT executor: `if (cache.TryGet(pc, out fn)) jump fn(state);`
2. Miss path: `block = detector.Detect(bus, pc); fresh module; bfb.Build(...); rt.AddModule(module); fn = rt.GetFunctionPointer(BlockFunctionName(...)); cache.Add(pc, fn);`
3. LRU eviction when size > N (TBD threshold)

A.6 (indirect branch dispatch) 跟 A.4 接力 — block exit 寫 PC，
dispatcher 拿 PC 查 cache。

---

## 7. 相關文件

- `src/AprCpu.Core/Runtime/HostRuntime.cs` — 重寫後源碼
- `MD/design/03-roadmap.md` Phase 7 A.3 — 標 done
- `MD/performance/202605030241-cb-alloca-shadow-retry.md` — MCJIT
  時代最後一個 perf 改動（A.3 的 baseline）
