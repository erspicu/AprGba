# NES JsonCpu per-instr — performance baseline (pre-block-JIT)

> **目的**：N1.A 完工後拿 LegacyCpu (interpreter oracle) 跟 NesJsonCpu
> (per-instr LLVM JIT) 跑同一個 ROM，量出 baseline MIPS。Block JIT
> (N2) 動工前把這個數字記下來，之後 block-JIT 進度的 perf 對照就有
> 起點 — 跟 GB block-JIT 同樣的 measurement 路徑（per-instr → block）。
>
> **結果**：legacy 1.69 MIPS、json per-instr 0.83 MIPS（~50% 慢）。
> per-instr dispatch overhead 是 dominant cost — 每 instruction 一次
> indirect call + decoder lookup + cycle accounting，即便 IR body 是
> LLVM 優化過的 native 也救不回來。Block JIT 是 perf 解。

---

## 1. 結果（3 runs each, sequential / no overlap）

ROM: `test-roms/blargg_nes_cpu_test5/cpu.nes`
（Mapper 1 / MMC1，256KB PRG，全 256 個 6502 opcodes 含 105 個 unofficial）

Workload: `--max-cycles=110000000`（~62 emulator-seconds，包含 `Running tests... → 11-special → All tests complete` 的完整 self-loop end）

| Run | **legacy** (interpreter) | **json** (LLVM JIT per-instr) |
|-----|--------------------------:|------------------------------:|
| 1   | 20.928s — 1.69 MIPS       | 42.619s — 0.83 MIPS           |
| 2   | 21.191s — 1.67 MIPS       | 42.529s — 0.84 MIPS           |
| 3   | 20.936s — 1.69 MIPS       | 42.447s — 0.84 MIPS           |
| **avg** | **21.018s — 1.69 MIPS** | **42.532s — 0.83 MIPS** |
| 變異度 | ±1.3%                       | ±0.4%                          |

Instruction count:
- legacy: 35,468,415
- json:   35,558,628 （+0.25%；cycle accounting 細節差異）

兩 backend 都到達 `PC=0x8003` self-loop（test halt state），blargg
`All tests complete` 結果一致。語義對齊。

## 2. 量測流程

每個 run 都是獨立的 `dotnet run` 程序（**不是** for-loop in single bash
call —初版那樣量會被 .NET workers 的 build cache + GC state 污染）。
量前 `Stop-Process VBCSCompiler` 確認 .NET build server 沒在後台佔
資源。

```
dotnet build AprGba.slnx --no-incremental    # 確保都是 Release / no incremental staleness
PowerShell> Stop-Process -Name VBCSCompiler -Force -ErrorAction SilentlyContinue
# 然後一次一個 dotnet run，獨立程序
dotnet run --project src/AprNes.Cli --no-build -- \
    --rom=test-roms/blargg_nes_cpu_test5/cpu.nes --run \
    --max-cycles=110000000 --backend=legacy
```

Logs at `temp/perf-{legacy,json}-{1,2,3}.log`.

## 3. 為什麼 json per-instr 慢這麼多

per-instr StepOne 的 hot path：

```
ushort pc = ReadI16(_pcOff);              // load from pinned state buffer
byte opcode = _bus.ReadByte(pc);          // virtual call → bus dispatch
WriteI16(_pcOff, (ushort)(pc + 1));        // store
var decoded = _mainDecoder.Decode(opcode); // dictionary / array lookup
var fnPtr = _fnPtrByDef[decoded.Instruction]; // identity-keyed cache hit
var fn = (delegate* unmanaged[Cdecl]<byte*, uint, void>)fnPtr;
fn(_statePtr, opcode);                     // ★ indirect function pointer call
long cycles = CyclesFor(decoded.Instruction);
cycles += ConditionalBranchExtraCycles(opcode, ...);
_bus.Tick(cycles);                         // virtual call → PPU catch-up
```

每一個 NES instruction 至少：
- 1 indirect function pointer call（call → ret，flushes inline cache）
- 2 virtual calls (`bus.ReadByte`, `bus.Tick`)
- 1 dictionary lookup + 1 PC store + 1 cycle add
- LLVM 優化過的 instruction body 本身執行極快（memory ops 走 host
  extern；ALU ops 直接 IR）— 但只佔總時間 <30%

LegacyCpu 對應的 hot path：

```
opcode = Mem_r(r_PC++);                   // direct array index after sealed override
cpu_cycles = cycle_table[opcode];
switch (opcode) {                          // ★ jump table — predicted, inlined
    case 0xA9: r_A = Mem_r(r_PC++); ...   // entire op = ~5 ALU + flag ops
    ...
}
```

big switch 在現代 CPU 的 branch predictor 上很友善（每個 hot opcode
有自己的 branch slot），且 case body 直接內聯不需要 indirect call。

## 4. 跟 GB 的對照

| Workload                      | Backend     | MIPS  |
|-------------------------------|-------------|-------|
| GB blargg cpu_instrs 09-op r  | legacy      | ~3.5  |
| GB blargg cpu_instrs 09-op r  | json per-instr | ~16  |
| GB blargg cpu_instrs 09-op r  | json block-JIT | ~70  |
| **NES blargg cpu_test5**      | legacy      | 1.69  |
| **NES blargg cpu_test5**      | json per-instr | 0.83  |
| **NES blargg cpu_test5**      | json block-JIT | TBD  |

GB json per-instr 比 legacy 快（5×）的原因之一：GB legacy 用 ms-cycle
寬鬆 timing 模型 + 較鬆的 PPU sync，而 NES legacy 已經是 cycle-accurate
+ 巨大 switch + tight inline ALU。NES legacy 的 starting bar 較高，json
per-instr 要追比較吃力。Block JIT 的 inlining/CSE/DCE 才有可能跨過。

## 5. Block JIT 預估增益（from GB）

GB block-JIT 把 per-instr 從 16 → 70 MIPS（4.4×）。對 NES：

- 預期 block JIT vs per-instr：~3-5× speedup（變異 dispatch overhead 比例）
- 預期 block JIT vs legacy：1.5-3× speedup
- 預期 NES json block-JIT MIPS：**2.5–5 MIPS**（從 0.83 → 2.5+）

如果只能達到跟 legacy 並列（1.7 MIPS），block-JIT 也算成功 — 至少
證明 spec-driven path 不比 hand-tuned switch interpreter 慢。
要超越 legacy 需要：
1. inline RAM/ZeroPage access bypass bus extern call（GB block-JIT P1
   #7 的 pattern — pin WRAM/HRAM、bake base addr 進 IR）
2. 跨 instruction CSE：A 的 read-modify-write chain 內共用同一個 PC
3. 條件分支 fold：`BNE` 的 cond 可以接著下個 BIT/CMP 的 result
   forward（block 內可見）

## 6. Setup overhead

JsonCpu 第一次 `new NesJsonCpu()` 載 spec → SpecCompiler.Compile →
HostRuntime.Build → BindExtern × N → `_rt.Compile()` 約 ~2 秒（JIT
compile 117 functions）。對長 workload 可忽略；short test 例如 nestest
（8990 instr）的 JsonCpu 跑 0.05 MIPS 就是因為 setup 攤不開。

Block JIT 加 lazy block compile，setup 更輕（只編 cold blocks），但
每個 block first-hit 會付一次 IR-emit + ORC link cost。GB block-JIT
量過約 50–200μs / block。

---

## 7. 環境

- OS: Windows 11 Home (10.0.26200)
- .NET: 10.0.107
- LLVM: 20.x via LLVMSharp.Interop + libLLVM.runtime.win-x64
- Build: `dotnet build AprGba.slnx --no-incremental`（Debug；Release 待之後比）
- Commit: `cb3d0ad` (post-cleanup; N1 final state)

Release 跑通常 ~1.5–2× 快，但 baseline 維持 Debug 因為跟 GB 的對
照數字也是 Debug，跨平台跨工具一致。Release 比較放後續單獨 doc。
