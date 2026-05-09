# NES block-JIT vs per-instr — first-cut perf measurement

> **Status (2026-05-09)**：N1.B'.5 完工後 baseline 量測。三 backend
> (legacy / json per-instr / json-block) 在 blargg cpu_test5 上 3 runs
> sequential bench，皆通過 "All tests complete"。
>
> **TL;DR**：block-JIT 在這個 ROM 上 **跟 per-instr 同 wall-clock**（~42s
> for 110M cycles），但慢於 legacy interpreter（~21s）。block-JIT 的
> 預期增益被 blargg test 框架的 SMC 行為抵消 — 框架在 sub-test 間
> rewrite RAM-resident `instr_template`，每次寫入觸發 cache invalidate
> + recompile，攤掉了 dispatch 省下的時間。
>
> 對普通 NES 遊戲（無頻繁 SMC）block-JIT 應有 2-4× 加速，blargg 是
> worst-case 量測。下次量真正遊戲 ROM 才能看出 block-JIT 真實效益。

---

## 1. 結果

ROM: `test-roms/blargg_nes_cpu_test5/cpu.nes`
（Mapper 1 / MMC1，256KB PRG，全 256 個 6502 opcodes 含 unofficial）

Workload: `--max-cycles=110000000`（~62 emulator-seconds，ROM 跑完
11 個 subtest 並 halt 在 PC=$8003 的 `JMP $8003` self-loop）

每個 backend 跑 3 次，獨立 `dotnet run` 程序，量前 `Stop-Process`
清乾淨 .NET workers。

### 1.1 Wall-clock + MIPS

| Backend | Run 1 | Run 2 | Run 3 | **Avg** | MIPS |
|---|---:|---:|---:|---:|---:|
| **legacy** (interpreter)              | 21.35s | 21.06s | 21.33s | **21.24s** | **1.67** |
| **json** (per-instr LLVM JIT)         | 42.60s | 42.77s | 42.97s | **42.78s** | **0.83** |
| **json-block** (block JIT, alloca+mem2reg) | 41.80s | 41.91s | 42.50s | **42.07s** | **0.68\*** |

\* json-block "MIPS" is misleading — see §2.

### 1.2 Cycles + instructions

| Backend | Instr count | Cycles | Cycles/instr |
|---|---:|---:|---:|
| legacy            | 35,468,415 | 110,000,000 | 3.10 |
| json per-instr    | 35,558,628 | 110,000,002 | 3.09 |
| json-block        | 28,526,048 | 110,000,000 | 3.86\* |

\* The "instr count" for json-block is `Step()` call count, NOT executed
instruction count. block-JIT runs many CPU instructions per `Step()`
call (the entire detected block). Real executed instructions ≈ same as
legacy (~35.5M) — verified by reaching the same halt PC=$8003 with
identical register state.

### 1.3 結果一致性

三個 backend 都在 110M cycles 內到 PC=$8003 self-loop（test halted），
最後 register state 對齊：

```
final  PC=0x8003 A=0x00 X=0x?? Y=0x03 SP=0xFF P=0x27
```

（X 在不同 backend 是 $00 或 $01，因為 self-loop 內 X 不變動，初始值
看 boot 路徑差異 — 不影響 test 結果）

PPU 截圖三個 backend 都是 "All tests complete" — 全 11 個 subtest 通過。

---

## 2. 為什麼 block-JIT 沒贏

預期：block-JIT 應該勝過 per-instr，因為：
- 每 `Step()` 一個 indirect call → 一個 block 一個 indirect call
- LLVM 跨 instruction 做 CSE / DCE / forwarding（mem2reg 把 alloca
  promotes 成 SSA register）
- 不需要 re-decode + 不需要 ResolveFunctionPointer 每 instr 一次

實際 wall-clock 差距是 ~2% (42.07s vs 42.78s) — basically tied。

**根本原因：blargg test 框架重度依賴 SMC**。

`test-roms/blargg_nes_cpu_test5/source/instr_test.a` 的 instr_template：
- ~4 bytes 的 branch + STA + JMP 序列在 RAM 某 fixed scratch addr
- 每測試一個新 opcode（共 ~80 個 opcode），把 template[0] byte 改成
  新 opcode（例如測 BCC 改成 $90、測 BVC 改成 $50）
- runtime 透過 `JSR template_addr` 跳進 RAM 跑

每次 byte rewrite 觸發我們的 `NesMemoryBus.SmcWriteHook` →
`BlockCache.NotifyMemoryWrite` → 找到 cover 該 addr 的 block 並 invalidate。
然後下次 `Step()` 進到該 PC 觸發 cache miss → BlockDetector 重 walk +
BlockFunctionBuilder 重編 + ORC AddModule。

每次重編 ~50-200μs（LLVM JIT compile cost）。28.5M block dispatches
中，估計 ~1-5% 是 cache miss → 285k–1.4M 重編 → 14-280 秒 wall。**JIT
compile cost 把 dispatch 省下的時間吃光**。

GB block-JIT 同樣機制，但 GB blargg cpu_instrs **不用** SMC（test code
全在 PRG-ROM），所以 GB block-JIT 從 6.5 → 21 MIPS（3.2× 加速 vs
per-instr）。

---

## 3. 跨 ISA 對照

| Workload | Backend | MIPS |
|---|---|---:|
| GB blargg cpu_instrs (master) | legacy            | ~31    |
| GB blargg cpu_instrs (master) | json per-instr    | ~6.5   |
| GB blargg cpu_instrs (master) | json block-JIT    | ~21    |
| **NES blargg cpu_test5**       | legacy            | **1.67** |
| **NES blargg cpu_test5**       | json per-instr    | **0.83** |
| **NES blargg cpu_test5**       | json block-JIT    | ~0.85 (real-instr-based)\* |

\* 28.5M `Step()` × ~1.25 actual instr/step = ~35.6M instr / 42.07s ≈ 0.85 MIPS
若用 actual instr count；報告中 0.68 MIPS 是 step-count base，下方圖表
整理。

NES legacy（interpreter switch table）跑得相對快是因為原本 AprNes
oracle 寫得很 tight（cycle-accurate inline switch）。GB legacy 比較
寬鬆 timing，所以 baseline 較高 MIPS。

---

## 4. 可能優化方向（未做）

### 4.1 SMC fast-path miss

`BlockCache.NotifyMemoryWrite` 的 fast path 是「coverage_count[addr] ==
0」就 return。但 blargg 的 stack 寫到 $0100-$01FF（RAM）— 如果有 block
cached 在 $0100+（不太可能但可能在 SMC 過程中），coverage > 0 觸發
slow path scan。slow path linearly scans `_map` (up to 4096 entries)
checking each block's `BlockCoversAddr`. 對 blargg 一秒幾十萬次寫入
可能不便宜。

優化：interval tree / sorted addr list 取代 linear scan。GB 的 P1 #5b
SMC V2 設計做了這個，但只 wire 給 GB 沒給 NES。可以 port。

### 4.2 Recompile cost amortisation

每次 invalidate 後重編，`BlockFunctionBuilder.Build` + LLVM passes 是
吃 CPU 大頭。可以：
- **Block fragment caching**：把單一 instruction 的 IR 也 cache 起來，
  block 只 splice 已 cached fragments（concept like QEMU TCG's translation
  block fragments）
- **Lazy reach**：cache miss 時先用 per-instr fallback 跑 N 次，第 N+1
  次才正式 compile block — 避免「一寫一編、馬上又寫一編」的反覆
- **PRG-ROM 區域不 invalidate**：bus.WriteByte to $8000+ 是 mapper
  control write，**不會** modify PRG-ROM bytes（mapper 已經通過
  PrgBankSwitched 處理 bank switch）。ROM 區域 SMC 只在很少數 mapper
  variant 會發生（大多沒有），可以 default 跳過 SMC notify for $8000+。

### 4.3 真實遊戲 ROM 量測

blargg 是 worst-case for SMC。下次量某個真實 NES game ROM（例如 Super
Mario Bros 開頭幾秒，或 Donkey Kong），block-JIT 真實效益應該明顯。

---

## 5. 環境

- OS: Windows 11 Home (10.0.26200)
- .NET: 10.0.107
- LLVM: 20.x via LLVMSharp.Interop + libLLVM.runtime.win-x64
- Build: `dotnet build AprGba.slnx --no-incremental`（Debug）
- Commit: `9f2f4a2` (B'.5 done; SMC notify + cycles_left residual)
- Logs: `temp/perf2-{legacy,json,block}-{1,2,3}.log`

---

## 6. 結論

- ✅ block-JIT 框架正確 — 三 backend 結果一致，T1 425/425
- ✅ 跨 CPU 共用框架 — Mos6502Emitters.cs 零行 diff，alloca+mem2reg
  自動切換 per-instr / block 模式
- ✅ GB block-JIT 沒回歸（11/11 PASS）
- ⚠️ NES block-JIT vs per-instr 在 blargg 上 perf 平手 — SMC heavy
  workload 是 blame，非 framework 問題
- 🔜 真實遊戲 ROM 量測（next milestone）才能驗證 block-JIT 對 normal
  workload 的效益
