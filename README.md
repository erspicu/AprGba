# AprGba — A JSON-driven CPU simulation framework

> A research project exploring whether an entire CPU emulator can be
> *generated* from a machine-readable specification — and whether the
> generated code can run fast enough to be practical.

**Last updated:** 2026-05-04 21:31 (Asia/Taipei)
**License:** [WTFPL v2](LICENSE) — do what the fuck you want to.
**Status:** Active research. ARM7TDMI (GBA) and LR35902 (Game Boy) running through the same framework. Block-JIT path live for both ISAs.

---

## English

### 1. What is this project, really?

The repository is named **AprGba**, and you'll find a Game Boy Advance harness inside. **But GBA is not the goal.** The actual product of this project is **`AprCpu`** — a JSON-driven CPU simulation *framework*. The GBA emulator is the test vehicle that proves the framework can be pushed to a non-trivial, real-world workload (commercial-grade ARM7TDMI emulation with LLVM block-JIT).

Think of it this way:

| Component | Role |
|---|---|
| **`AprCpu`** | The framework. CPU spec loader + decoder generator + IR emitters + LLVM JIT runtime + block detector + cache. **This is the core.** |
| **`AprGba`** | One concrete consumer of the framework — full GBA system (ARM7TDMI + Thumb + memory bus + PPU + scheduler). Used to push `AprCpu` to its limits. |
| **`AprGb`** | A second consumer — Game Boy DMG (LR35902 / SM83). Used as a *control case* and to prove the framework genuinely supports a second, different ISA. |

### 2. Why does this project exist?

#### The problem

Writing a CPU emulator is a frequently-rediscovered chore. Every new platform — every new homebrew console, every retro-computing project, every "let me try emulating an X" — leads to the same hand-coded dispatcher loop, the same opcode `switch` statement copy-pasted with new bit fields, the same flag-update boilerplate, the same partial-register stalls and pipeline-PC quirks rediscovered the hard way.

There are excellent emulators out there (mGBA, Dolphin, QEMU, FCEUX). But they're each tightly coupled to *their* CPU. Porting an mGBA-quality JIT to a new ISA usually means writing a new emulator.

#### The hypothesis

> **What if the CPU were a JSON file?**

What if the entire ISA — encoding patterns, register file layout, condition codes, micro-op semantics, cycle costs, pipeline behaviour — were declarative data, and the emulator framework could compile that data into a working interpreter *and* a working LLVM JIT?

#### The goals, in priority order

1. **Build a framework that's actually generic.** Not "generic in theory" — generic in the sense that two genuinely different CPUs (ARM7TDMI + LR35902) compile through the same pipeline with no per-CPU C# code.
2. **Take the framework all the way to block-JIT.** Per-instruction interpreters are easy to make generic. The hard part is whether the framework can survive the architectural pressure of LLVM JIT, cycle accounting, IRQ delivery, SMC detection, and pipeline-PC quirks — *while staying spec-driven*.
3. **Validate against real workloads.** Pass Blargg's `cpu_instrs.gb` (all 11 sub-tests). Pass jsmolka's `arm.gba`/`thumb.gba`. Boot the GBA BIOS via LLE. Render canonical screenshots with cycle-accurate matrix tests.
4. **Document the design philosophy.** Every trade-off recorded. Every architectural pattern named. Future maintainers — including future-me — should be able to tell *why* a design choice was made, not just *what* the code does.

#### What this project is **not**

- **Not** a competitor to mGBA. mGBA is a polished end-user emulator; we are a research framework.
- **Not** chasing maximum cycle accuracy. We are deliberately at "instruction-grained timing accuracy with sync exits at HW-relevant moments" — enough for commercial ROMs, not enough for cycle-perfect demoscene work.
- **Not** trying to be the fastest emulator. The current LLVM block-JIT path runs Blargg cpu_instrs at ~21 MIPS (10k frames) / ~27 MIPS (60k frames amortised). The hand-coded `AprGb` legacy interpreter (imported from a previous project — see §3) still beats this. We know. **Performance optimisation is a downstream concern after the framework design is sound.**

### 3. Honest acknowledgement: the `AprGb` legacy interpreter

The Game Boy interpreter under `src/AprGb.Cli/Cpu/LegacyCpu*` is **not** original to this project. It is imported from an earlier hand-coded emulator of mine — see [erspicu/AprGBemu](https://github.com/erspicu/AprGBemu).

Why import it?

1. **Provide a reference oracle.** Lockstep diff against a known-good interpreter is invaluable when developing a JSON-driven path. Every Blargg PASS we celebrate gets cross-checked against the legacy interpreter producing identical state.
2. **Establish a perf baseline.** The legacy interpreter runs cpu_instrs at ~31 MIPS — faster than our current JIT. This is honest: **as of 2026-05-04, our LLVM block-JIT is still 13-32% slower than a well-tuned hand-coded interpreter**. We track this gap in [`MD_EN/performance/`](MD_EN/performance/).
3. **Demonstrate the framework's real value isn't raw speed.** It's *generality*. The same `AprCpu` pipeline that compiles ARM7TDMI also compiles LR35902 — no architectural hardcoding. The legacy interpreter cannot do this.

### 4. What's interesting about the framework?

Beyond "JSON in, working emulator out", these are the framework-level designs that took deliberate effort and are documented in [`MD_EN/design/`](MD_EN/design/):

- **Variable-width detection without spec coupling.** A `lengthOracle` callback turns a 256-entry static table into a per-CPU plug-in. ARM (4-byte fixed), Thumb (2-byte fixed), and LR35902 (1-3 byte variable, with 0xCB-prefix sub-decoder) all share the same `BlockDetector`.
- **Generic `defer` micro-op for delayed-effect instructions.** Whether it's LR35902 `EI` (IME=1 after one more instruction), Z80 `STI`, or x86 `STI`, the spec writes `defer { delay: 1, body: [...] }` and an AST pre-pass injects the delayed body as a phantom step. Zero runtime cost — it's compile-time lowered.
- **Generic `sync` micro-op for control-yield to host.** A spec step can declare "after this point, the host might want to deliver an IRQ". The block-JIT emitter turns this into a conditional mid-block `ret void`. Same mechanism services LR35902 MMIO writes, IRQ-relevant memory writes, and (eventually) any new CPU's HW-state-change boundary.
- **Three architectural patterns for timing-accurate block-JIT.** Predictive cycle downcounting (compute-once-deduct-as-you-go), MMIO catch-up callbacks (HW gets ticked at the moment it's observed), and sync exits (block ret-voids when HW state changes). Every timing problem is classified into one of these three. See [`MD_EN/design/15-timing-and-framework-design.md`](MD_EN/design/15-timing-and-framework-design.md).
- **`EmitContext` as a routing layer.** Spec emitters call `ctx.GepGpr(idx)` instead of `Layout.GepGpr(builder, statePtr, idx)`. The context decides whether the access goes to a state-struct GEP or a block-local alloca shadow. Per-instruction mode and block-JIT mode share emitter code.
- **Self-modifying-code detection at framework level.** A per-byte coverage counter is incremented when a block compiles, decremented when it's invalidated. Memory writes do a 1-byte counter check inline; if non-zero, a slow-path notify scans cached blocks and invalidates the matching ones. The infrastructure is generic — any cached + writable-code platform reuses it.
- **Cross-jump follow.** The detector follows unconditional `JR`/`JP` (and equivalents) into their target, lengthening blocks from "average 1.0-1.1 instructions" to "5-20 instructions" — a structural fix for the BIOS-LLE perf cliff.
- **Strategy 2 PC handling.** Pipeline-PC reads (ARM `pc+8`, Thumb `pc+4`, LR35902 `pc+length`) become baked compile-time constants in the block IR. No more per-instruction "pre-set R15" writes that confuse "did this instruction branch?" detection.
- **Lockstep diff as framework infrastructure.** `apr-gb --diff-bjit=N` runs both backends side-by-side and reports the first divergence. Same harness works for ARM jsmolka and LR35902 Blargg.
- **Hardware-style 8-combo screenshot matrix.** GBA test ROMs render through 8 combinations (`arm/thumb` × `HLE/BIOS-boot` × `per-instr/block-JIT`); a single canonical MD5 hash means all eight produced bit-identical output. Regression-proof for any framework change.

### 5. Project layout

```
AprGba/
├── src/
│   ├── AprCpu.Core/        ← THE FRAMEWORK. Spec loader + IR emitters + LLVM JIT
│   │   ├── JsonSpec/       ← spec deserialisation (RegisterFile, EncodingFormat, …)
│   │   ├── IR/             ← LLVM IR generation (BlockFunctionBuilder, EmitContext, micro-op emitters)
│   │   └── Runtime/        ← block detector + cache + ORC LLJIT host runtime
│   ├── AprCpu.Compiler/    ← CLI: spec → LLVM IR (used for inspection / smoke tests)
│   ├── AprCpu.Tests/       ← 365 unit tests covering decoder, emitters, block detector, cache, …
│   ├── AprGba.Cli/         ← GBA harness (ARM7TDMI + Thumb + bus + PPU + scheduler + screenshot)
│   └── AprGb.Cli/          ← Game Boy harness (LR35902 + bus + PPU; legacy interpreter from AprGBemu)
├── spec/
│   ├── arm7tdmi/           ← ARM7TDMI ISA spec (cpu.json + ARM groups + Thumb groups)
│   ├── lr35902/            ← LR35902 ISA spec (cpu.json + Main + CB-prefix groups)
│   └── schema/             ← JSON schema for spec validation
├── test-roms/              ← Blargg cpu_instrs, jsmolka arm/thumb, armwrestler, loop100 stress ROMs
├── MD/                     ← Traditional Chinese authoring source
│   ├── design/             ← Long-form design docs (overview, architecture, roadmap, …)
│   │   ├── 12-gb-block-jit-roadmap.md       ← GB block-JIT progress & next steps
│   │   ├── 13-defer-microop.md              ← `defer` micro-op design
│   │   ├── 14-irq-sync-fastslow.md          ← `sync` micro-op + bus-extern split
│   │   └── 15-timing-and-framework-design.md ← Timing-accuracy & framework-genericity synthesis
│   ├── performance/        ← Benchmark logs + completion reports (one file per perf event)
│   ├── note/               ← Working notes
│   └── process/            ← Workflows (commit-QA tier, …)
├── MD_EN/                  ← English mirror of MD/ (same files, English prose)
├── tools/                  ← Build helpers (jsmolka/blargg ROM builders), Gemini knowledgebase
├── BIOS/                   ← (not in repo) place gba_bios.bin / gb_bios.bin here for LLE tests
├── ref/                    ← Vendor manuals + datasheets (ARM ARM, GB CPU manual, …)
├── temp/                   ← (gitignored) scratch dir for IR dumps, screenshots, log files
├── etc/                    ← (gitignored) local working notes
├── CLAUDE.md               ← Project rules for AI agents (Claude Code et al.)
└── AprGba.slnx             ← .NET solution file (target framework: net10.0)
```

### 6. Quick start

#### Prerequisites

- **.NET 10 SDK** (target framework `net10.0`).
- **Windows x64.** Linux / macOS untested for now — `libLLVM.runtime.win-x64` is the only RID currently referenced. Adding other RIDs is a small change in `AprCpu.Compiler.csproj`.
- **LLVM 20** is provided via the `libLLVM.runtime.win-x64` NuGet package — no separate install required.

#### Build & test

```sh
# Restore + build the whole solution
dotnet build AprGba.slnx

# Run the unit-test suite (365 tests as of 2026-05-04)
dotnet test  AprGba.slnx
```

#### Run the GBA harness

```sh
# Boot a test ROM with HLE BIOS, render to PNG
dotnet run --project src/AprGba.Cli -- \
    --rom=test-roms/gba-tests/arm/arm.gba \
    --frames=300 \
    --screenshot=temp/arm-out.png

# Same, but with block-JIT enabled
dotnet run --project src/AprGba.Cli -- \
    --rom=test-roms/gba-tests/arm/arm.gba \
    --frames=300 \
    --block-jit \
    --screenshot=temp/arm-bjit.png

# Boot a real BIOS via LLE (drop gba_bios.bin in BIOS/)
dotnet run --project src/AprGba.Cli -- \
    --rom=test-roms/gba-tests/arm/arm.gba \
    --bios=BIOS/gba_bios.bin \
    --frames=300 \
    --block-jit
```

#### Run the Game Boy harness

```sh
# Run Blargg cpu_instrs with block-JIT
dotnet run --project src/AprGb.Cli -- \
    --rom="test-roms/gb-test-roms-master/cpu_instrs/cpu_instrs.gb" \
    --cpu=json-llvm \
    --block-jit \
    --frames=10000

# Lockstep diff per-instruction vs block-JIT (for correctness debugging)
dotnet run --project src/AprGb.Cli -- \
    --rom="test-roms/gb-test-roms-master/cpu_instrs/cpu_instrs.gb" \
    --cpu=json-llvm --block-jit \
    --diff-bjit=2000000 \
    --frames=2000
```

### 7. How to contribute / take over development

#### Read these in order

1. **[`MD_EN/design/00-overview.md`](MD_EN/design/00-overview.md)** — what this project is at the highest level.
2. **[`MD_EN/design/02-architecture.md`](MD_EN/design/02-architecture.md)** — how the pieces fit.
3. **[`MD_EN/design/12-gb-block-jit-roadmap.md`](MD_EN/design/12-gb-block-jit-roadmap.md)** — the active roadmap with every shipped commit + remaining items.
4. **[`MD_EN/design/15-timing-and-framework-design.md`](MD_EN/design/15-timing-and-framework-design.md)** — the timing & framework-genericity synthesis. **Read this before touching any timing code.**
5. **[`CLAUDE.md`](CLAUDE.md)** — project rules (commit QA workflow, scratch-file conventions, naming).
6. **[`MD_EN/process/01-commit-qa-workflow.md`](MD_EN/process/01-commit-qa-workflow.md)** — what level of QA each kind of commit requires.

#### Adding a new CPU

The current architecture supports any ISA expressible as:

- A register file (general-purpose + status registers, optionally banked per mode)
- A set of encoding formats with bit-pattern matching (`mask` / `match`)
- A set of micro-op steps per instruction (declarative semantics: `read_reg`, `add`, `set_flag`, `store`, `defer`, `sync`, …)
- Optionally: a `lengthOracle` callback for variable-width ISAs
- Optionally: a `prefix_to_set` field for prefix-byte sub-decoders

Look at `spec/lr35902/cpu.json` + `spec/lr35902/groups/*.json` for a complete variable-width example. ARM7TDMI is at `spec/arm7tdmi/`.

To add a new CPU:

1. Define `cpu.json` (register file, status registers, exception vectors, processor modes if any).
2. Define encoding-format groups under `groups/` covering the full opcode space.
3. If variable-width, write a `Cpu_X_InstructionLengths.cs` next to `Lr35902InstructionLengths.cs` and wire it as the `lengthOracle`.
4. Write a CLI harness consuming `AprCpu.Core` (look at `AprGb.Cli/Cpu/JsonCpu.cs` as a template).
5. Add unit tests under `AprCpu.Tests/`.
6. Document in [`MD_EN/design/`](MD_EN/design/) if you hit any framework-level surprises.

#### Tools

- **`tools/knowledgebase/gemini_query.py`** — wraps Gemini API for "ask the oracle" queries when stuck on LLVM / vendor / arch corner cases. One question at a time. Logs to `tools/knowledgebase/message/`.
- **`tools/build_blargg.sh`**, **`tools/build_jsmolka.sh`**, **`tools/build_loop100.sh`** — re-build the test ROMs from source if you change them.
- **`tools/gba_handasm.py`** — small disassembler helper for ad-hoc inspection.
- **`tools/fasmarm/`**, **`tools/wla-dx/`** — vendored assemblers for ARM and LR35902 ROM building.
- **`temp/`** — drop all scratch files here (IR dumps, intermediate JSON, debug screenshots). Gitignored.

### 8. Where this could go

The framework is designed so that the following are *additive* extensions, not architectural rewrites:

- **More CPUs.** 6502 (NES), Z80 (Master System / GG), 8080 (CP/M), 68000 (Genesis / Neo Geo / early Mac) — all expressible in the same JSON model. Variable-width + prefix-decoded ISAs already work (LR35902 0xCB).
- **Additional execution backends.** The `EmitContext` routing layer means a future AOT compiler, WebAssembly target, or even a different IR backend can slot in alongside the LLVM JIT without touching emitters.
- **Spec-time IR pre-passes.** Dead-flag elimination, micro-op fusion, hot-opcode inlining — all naturally extend the existing AST pre-pass mechanism.
- **Timing model upgrades.** Per-cycle bus contention, deferred SMC invalidation, full pipeline modelling — each one fits one of the three architectural patterns in [`MD_EN/design/15-timing-and-framework-design.md`](MD_EN/design/15-timing-and-framework-design.md).
- **Beyond emulation.** A JSON-driven CPU model is also a *specification artefact* — usable for: educational visualisations, what-if architectural studies, cross-architecture binary translators, dynamic taint analysis, formal verification scaffolding. The framework doesn't *do* these, but it's a substrate that makes them practical for hobbyist effort.

### 9. References & acknowledgements

- **Vendor manuals** (in `ref/`) — ARM Architecture Reference Manual, Game Boy CPU manual, Pan Docs.
- **Test suites** — Blargg's cpu_instrs, jsmolka's arm/thumb tests, armwrestler.
- **Industry references** — design hints cross-checked against QEMU TCG, FEX-Emu, Dynarmic, mGBA, Dolphin via Gemini consultation logs (`tools/knowledgebase/message/`).
- **Predecessor project** — [erspicu/AprGBemu](https://github.com/erspicu/AprGBemu): hand-coded LR35902 interpreter, source of `AprGb.Cli/Cpu/LegacyCpu.cs`. Used here as oracle + perf baseline.

---

## 中文版

### 1. 這專案到底是什麼？

repo 名字叫 **AprGba**，內容裡也有完整的 Game Boy Advance 模擬器外殼。**但 GBA 不是這個專案的目的。** 真正的核心是 **`AprCpu`** — 一個 JSON-driven 的 CPU 模擬框架。GBA 模擬器只是「壓力測試載體」，用來證明框架可以推到 non-trivial 的真實工作負載（commercial 級 ARM7TDMI 模擬 + LLVM block-JIT）。

換個角度看：

| 元件 | 角色 |
|---|---|
| **`AprCpu`** | 框架本體。spec loader + decoder generator + IR emitters + LLVM JIT runtime + block detector + cache。**這才是核心。** |
| **`AprGba`** | 框架的一個具體消費者 — 完整 GBA 系統 (ARM7TDMI + Thumb + memory bus + PPU + scheduler)。用來把 `AprCpu` 推到極限。 |
| **`AprGb`** | 第二個消費者 — Game Boy DMG (LR35902 / SM83)。用作 *對照組*，並證明框架真的支援第二個、不一樣的 ISA。 |

### 2. 為什麼有這個專案？

#### 想解決的問題

寫 CPU 模擬器是個被反覆重新發明的苦差事。每個新平台 — 每個新的 homebrew 主機、每個 retro-computing 專案、每次「我來試試模擬個 X」 — 都會重複同一條 hand-coded dispatcher loop、同一個 opcode `switch`、同一堆 flag-update boilerplate、同一批 partial-register stalls 跟 pipeline-PC quirks 重新踩坑。

業界有很棒的 emulator (mGBA / Dolphin / QEMU / FCEUX)。但每個都跟「自己那顆 CPU」緊密耦合。要把 mGBA 等級的 JIT port 到新 ISA，通常等於重寫一個 emulator。

#### 假設

> **如果把 CPU 變成一個 JSON 檔案會怎樣？**

如果整個 ISA — 編碼模式、register file 配置、condition codes、micro-op 語意、cycle 成本、pipeline 行為 — 都是宣告式資料，而 emulator 框架可以把這些資料編譯成可執行的 interpreter *和* LLVM JIT，那會是什麼樣子？

#### 目標（按優先順序）

1. **建一個真的通用的框架。** 不是「理論通用」 — 是「兩個本質不同的 CPU (ARM7TDMI + LR35902) 走同一條 pipeline，沒有任何 per-CPU 的 C# code」這種通用。
2. **把框架推到 block-JIT。** Per-instruction interpreter 要做通用很容易。難的是框架能不能扛住 LLVM JIT、cycle accounting、IRQ delivery、SMC detection、pipeline-PC quirks 的架構壓力 — *同時保持 spec-driven*。
3. **拿真實 workload 驗證。** Blargg `cpu_instrs.gb` 全 11 個 sub-test PASS、jsmolka `arm.gba`/`thumb.gba` PASS、GBA BIOS 走 LLE 成功啟動、cycle-accurate matrix screenshot test 通過。
4. **記錄設計觀念。** 每個取捨都有紀錄。每個架構 pattern 都有名字。後人 — 包括未來的我自己 — 看得出每個設計選擇是 *為什麼* 這樣，不只是 *做了什麼*。

#### 這個專案 **不是** 什麼

- **不是** 要跟 mGBA 競爭。mGBA 是成熟的終端使用者 emulator，我們是研究框架。
- **不是** 在追求極致 cycle accuracy。我們刻意停在「instruction-grained timing accuracy + HW-relevant 時刻 sync exit」 — 對 commercial ROM 夠用，對 cycle-perfect demoscene 不夠。
- **不是** 要當最快的 emulator。現在 LLVM block-JIT 在 Blargg cpu_instrs 跑 ~21 MIPS (10k frames) / ~27 MIPS (60k frames amortised)。我們從舊專案 import 的 `AprGb` 手寫 interpreter (見 §3) 還是比這快。我們知道。**Performance 優化是框架設計穩定後的下游問題。**

### 3. 老實交代：`AprGb` legacy interpreter

`src/AprGb.Cli/Cpu/LegacyCpu*` 下的 Game Boy interpreter **不是** 這專案原創的。它從我之前寫的手刻 emulator import 過來 — 見 [erspicu/AprGBemu](https://github.com/erspicu/AprGBemu)。

為什麼要 import？

1. **提供 reference oracle。** 開發 JSON-driven 路徑時，跟一個已知正確的 interpreter 做 lockstep diff 是無價的。每一個 Blargg PASS 我們都跟 legacy interpreter 對拍 state 完全一致才算數。
2. **建立 perf baseline。** Legacy interpreter 跑 cpu_instrs ~31 MIPS — 比我們現在的 JIT 快。誠實講：**截至 2026-05-04，我們的 LLVM block-JIT 仍比一個調過的手刻 interpreter 慢 13-32%。** 這個 gap 紀錄在 [`MD/performance/`](MD/performance/)。
3. **證明框架真正的價值不在 raw speed。** 是 *通用性*。同一個 `AprCpu` pipeline 同時編譯 ARM7TDMI 跟 LR35902 — 沒有任何 architectural hardcoding。Legacy interpreter 做不到這件事。

### 4. 框架有哪些值得一提的設計？

除了「JSON 餵進去、可以動的 emulator 跑出來」之外，下面這些是框架級的設計、每個都用力想過、都記錄在 [`MD/design/`](MD/design/)：

- **Variable-width detection 不跟 spec 耦合。** 用 `lengthOracle` callback 把 256-entry static table 變成 per-CPU plug-in。ARM (定寬 4-byte)、Thumb (定寬 2-byte)、LR35902 (變寬 1-3 byte，加 0xCB-prefix sub-decoder) 走同一個 `BlockDetector`。
- **通用 `defer` micro-op 處理延遲生效指令。** LR35902 `EI`、Z80 `STI`、x86 `STI` 全都用 `defer { delay: 1, body: [...] }` 表達；AST pre-pass 把 delayed body 注入成 phantom step。Zero runtime cost — compile-time 攤平。
- **通用 `sync` micro-op 處理 control-yield 給 host。** Spec step 可以宣告「執行到這個點之後，host 可能想 deliver IRQ」。Block-JIT emitter 把它變成 conditional mid-block `ret void`。同一機制服務 LR35902 MMIO 寫、IRQ-relevant memory 寫、未來任何 CPU 的 HW-state-change 邊界。
- **三個架構 pattern 處理 timing-accurate block-JIT。** Predictive cycle downcounting (先算總額邊跑邊扣)、MMIO catch-up callbacks (HW 在被觀測那刻才被 tick)、sync exits (HW state 改變時 block ret-void)。每個 timing 問題都歸到這三條軸的其中一條。詳見 [`MD/design/15-timing-and-framework-design.md`](MD/design/15-timing-and-framework-design.md)。
- **`EmitContext` 作為 routing layer。** Spec emitters 呼叫 `ctx.GepGpr(idx)` 而不是 `Layout.GepGpr(builder, statePtr, idx)`。Context 自己決定要走 state-struct GEP 還是 block-local alloca shadow。Per-instruction 模式跟 block-JIT 模式共用 emitter code。
- **框架級 SMC detection。** 每個 byte 一個 coverage counter，block 編譯時 increment、invalidate 時 decrement。記憶體寫做 1-byte counter 的 inline check；非零才走 slow-path notify scan。infrastructure 是 generic — 任何 cached + writable-code 平台都能重用。
- **Cross-jump follow。** Detector 跨 unconditional `JR`/`JP` (跟同類) 連續到 target，把 block 平均長度從「1.0-1.1 instr」拉到「5-20 instr」 — 結構性修掉 BIOS-LLE perf cliff。
- **Strategy 2 PC handling。** Pipeline-PC reads (ARM `pc+8`、Thumb `pc+4`、LR35902 `pc+length`) 在 block IR 裡變成編譯時常數。不再有 per-instruction 的「pre-set R15」寫操作搞混「這條 instr 有沒有分支？」的判斷。
- **Lockstep diff 是 framework infrastructure。** `apr-gb --diff-bjit=N` 把兩 backend 並排跑、回報第一個分歧點。同一 harness 對 ARM jsmolka 跟 LR35902 Blargg 都 work。
- **8-combo screenshot matrix 防 regression。** GBA test ROM 走 8 種組合 (`arm/thumb` × `HLE/BIOS-boot` × `per-instr/block-JIT`) 渲染；單一 canonical MD5 hash 表示 8 個輸出 bit-identical。任何框架改動撞到 hash 改變就立刻 catch。

### 5. 專案目錄

```
AprGba/
├── src/
│   ├── AprCpu.Core/        ← 框架本體。Spec loader + IR emitters + LLVM JIT
│   │   ├── JsonSpec/       ← spec 反序列化 (RegisterFile / EncodingFormat / …)
│   │   ├── IR/             ← LLVM IR 生成 (BlockFunctionBuilder / EmitContext / micro-op emitters)
│   │   └── Runtime/        ← block detector + cache + ORC LLJIT host runtime
│   ├── AprCpu.Compiler/    ← CLI: spec → LLVM IR (用來 inspect / smoke test)
│   ├── AprCpu.Tests/       ← 365 個 unit test 涵蓋 decoder / emitters / detector / cache / …
│   ├── AprGba.Cli/         ← GBA harness (ARM7TDMI + Thumb + bus + PPU + scheduler + screenshot)
│   └── AprGb.Cli/          ← Game Boy harness (LR35902 + bus + PPU；legacy interpreter 從 AprGBemu 來)
├── spec/
│   ├── arm7tdmi/           ← ARM7TDMI ISA spec (cpu.json + ARM groups + Thumb groups)
│   ├── lr35902/            ← LR35902 ISA spec (cpu.json + Main + CB-prefix groups)
│   └── schema/             ← spec 的 JSON schema 驗證
├── test-roms/              ← Blargg cpu_instrs / jsmolka arm-thumb / armwrestler / loop100 stress ROM
├── MD/                     ← 中文 authoring source（原始撰寫版）
│   ├── design/             ← 長篇設計 doc (overview / architecture / roadmap / …)
│   │   ├── 12-gb-block-jit-roadmap.md       ← GB block-JIT 進度跟下一步
│   │   ├── 13-defer-microop.md              ← `defer` micro-op 設計
│   │   ├── 14-irq-sync-fastslow.md          ← `sync` micro-op + bus-extern split
│   │   └── 15-timing-and-framework-design.md ← Timing 準確 + 框架通用化的 synthesis
│   ├── performance/        ← Benchmark log + 完工報告 (one file per perf event)
│   ├── note/               ← 工作筆記
│   └── process/            ← 流程 (commit-QA tier / …)
├── MD_EN/                  ← MD/ 的英文鏡像版（同檔名、英文 prose）
├── tools/                  ← Build helper (jsmolka/blargg ROM builder) / Gemini knowledgebase
├── BIOS/                   ← (不在 repo) 想跑 LLE test 的話放 gba_bios.bin / gb_bios.bin 進來
├── ref/                    ← Vendor manual + datasheet (ARM ARM / GB CPU manual / …)
├── temp/                   ← (gitignored) scratch dir 給 IR dump / screenshot / log 用
├── etc/                    ← (gitignored) 本機工作筆記
├── CLAUDE.md               ← 給 AI agent (Claude Code 等) 的專案規則
└── AprGba.slnx             ← .NET solution 檔 (target framework: net10.0)
```

### 6. Quick start

#### 前置

- **.NET 10 SDK** (target framework `net10.0`)
- **Windows x64**。Linux / macOS 目前沒測 — `libLLVM.runtime.win-x64` 是目前唯一引用的 RID。要加其他 RID 是 `AprCpu.Compiler.csproj` 的小改動。
- **LLVM 20** 走 `libLLVM.runtime.win-x64` NuGet 套件 — 不用另裝。

#### Build & test

```sh
# Restore + build 整個 solution
dotnet build AprGba.slnx

# 跑單元測試 (2026-05-04 為 365 個)
dotnet test  AprGba.slnx
```

#### 跑 GBA harness

```sh
# 用 HLE BIOS 開 test ROM、輸出 PNG
dotnet run --project src/AprGba.Cli -- \
    --rom=test-roms/gba-tests/arm/arm.gba \
    --frames=300 \
    --screenshot=temp/arm-out.png

# 一樣但開 block-JIT
dotnet run --project src/AprGba.Cli -- \
    --rom=test-roms/gba-tests/arm/arm.gba \
    --frames=300 \
    --block-jit \
    --screenshot=temp/arm-bjit.png

# 跑 real BIOS LLE (gba_bios.bin 放在 BIOS/)
dotnet run --project src/AprGba.Cli -- \
    --rom=test-roms/gba-tests/arm/arm.gba \
    --bios=BIOS/gba_bios.bin \
    --frames=300 \
    --block-jit
```

#### 跑 Game Boy harness

```sh
# 跑 Blargg cpu_instrs 開 block-JIT
dotnet run --project src/AprGb.Cli -- \
    --rom="test-roms/gb-test-roms-master/cpu_instrs/cpu_instrs.gb" \
    --cpu=json-llvm \
    --block-jit \
    --frames=10000

# Lockstep diff per-instr vs block-JIT (correctness debug 用)
dotnet run --project src/AprGb.Cli -- \
    --rom="test-roms/gb-test-roms-master/cpu_instrs/cpu_instrs.gb" \
    --cpu=json-llvm --block-jit \
    --diff-bjit=2000000 \
    --frames=2000
```

### 7. 想接手開發 / 貢獻？

#### 依序讀這幾份

1. **[`MD/design/00-overview.md`](MD/design/00-overview.md)** — 最高層次的「這個專案是什麼」。
2. **[`MD/design/02-architecture.md`](MD/design/02-architecture.md)** — 各部分怎麼組合。
3. **[`MD/design/12-gb-block-jit-roadmap.md`](MD/design/12-gb-block-jit-roadmap.md)** — 目前 active roadmap、每個 ship 的 commit、剩下要做的。
4. **[`MD/design/15-timing-and-framework-design.md`](MD/design/15-timing-and-framework-design.md)** — Timing 準確 + 框架通用化的 synthesis。**動任何 timing 相關 code 之前先讀這份。**
5. **[`CLAUDE.md`](CLAUDE.md)** — 專案規則 (commit QA workflow / scratch-file 慣例 / 命名)。
6. **[`MD/process/01-commit-qa-workflow.md`](MD/process/01-commit-qa-workflow.md)** — 哪種 commit 要過哪一級 QA。

#### 加新 CPU

目前架構支援任何能用下面表達的 ISA：

- 一個 register file (general-purpose + status registers，可 banked per mode)
- 一組 encoding format 用 bit-pattern matching (`mask` / `match`)
- 每個 instruction 一組 micro-op step (宣告式語意：`read_reg` / `add` / `set_flag` / `store` / `defer` / `sync` / …)
- (選用) 變寬 ISA 用 `lengthOracle` callback
- (選用) prefix-byte sub-decoder 用 `prefix_to_set` 欄位

完整變寬範例看 `spec/lr35902/cpu.json` + `spec/lr35902/groups/*.json`。ARM7TDMI 在 `spec/arm7tdmi/`。

加新 CPU 的步驟：

1. 寫 `cpu.json` (register file / status registers / exception vectors / processor modes 如果有)。
2. 在 `groups/` 下定義 encoding-format group 涵蓋所有 opcode 空間。
3. 變寬的話，仿 `Lr35902InstructionLengths.cs` 寫一個 `Cpu_X_InstructionLengths.cs` + 接成 `lengthOracle`。
4. 寫一個 CLI harness 消費 `AprCpu.Core` (看 `AprGb.Cli/Cpu/JsonCpu.cs` 當 template)。
5. 在 `AprCpu.Tests/` 加 unit test。
6. 撞到任何框架級 surprise 的話寫進 [`MD/design/`](MD/design/)。

#### 工具

- **`tools/knowledgebase/gemini_query.py`** — 包 Gemini API 用來「請教神諭」，卡 LLVM / vendor / arch corner case 時用。一次問一個。Log 寫到 `tools/knowledgebase/message/`。
- **`tools/build_blargg.sh`** / **`tools/build_jsmolka.sh`** / **`tools/build_loop100.sh`** — 從 source 重 build test ROM (改了 source 的話)。
- **`tools/gba_handasm.py`** — 小型 disassembler helper 給 ad-hoc inspect 用。
- **`tools/fasmarm/`** / **`tools/wla-dx/`** — vendored assembler 給 ARM 跟 LR35902 ROM 編譯用。
- **`temp/`** — 所有 scratch file 丟這裡 (IR dump / 中間 JSON / debug screenshot)。Gitignored。

### 8. 這個框架可以走多遠

框架設計成下面這些是「加法擴充」、不是「架構重寫」：

- **更多 CPU。** 6502 (NES)、Z80 (Master System / GG)、8080 (CP/M)、68000 (Genesis / Neo Geo / 早期 Mac) — 全都能用同一個 JSON 模型表達。變寬 + prefix-decoded ISA 已經 work (LR35902 0xCB)。
- **其他 execution backend。** `EmitContext` routing layer 表示未來 AOT compiler、WebAssembly target、甚至不同的 IR backend 都能跟 LLVM JIT 並列，不用動 emitter。
- **Spec-time IR pre-pass。** Dead-flag elimination、micro-op fusion、hot-opcode inlining — 全都自然延伸現有的 AST pre-pass 機制。
- **Timing 模型升級。** Per-cycle bus contention、deferred SMC invalidation、完整 pipeline 模擬 — 每個都套到 [`MD/design/15-timing-and-framework-design.md`](MD/design/15-timing-and-framework-design.md) 三大 pattern 的其中一個。
- **超出 emulation 的應用。** JSON-driven CPU model 同時是個 *規格檔* — 可以拿來做：教育性視覺化、what-if 架構研究、跨架構 binary translator、dynamic taint analysis、formal verification scaffolding。框架本身不做這些事，但是它是個讓 hobbyist effort 也能做這些事的基礎。

### 9. References & 致謝

- **Vendor manual** (在 `ref/`) — ARM Architecture Reference Manual、Game Boy CPU manual、Pan Docs。
- **Test suite** — Blargg cpu_instrs、jsmolka arm/thumb test、armwrestler。
- **業界 reference** — 設計 hint 透過 Gemini 諮詢跟 QEMU TCG / FEX-Emu / Dynarmic / mGBA / Dolphin 對拍 (`tools/knowledgebase/message/`)。
- **前置專案** — [erspicu/AprGBemu](https://github.com/erspicu/AprGBemu)：手刻 LR35902 interpreter，是 `AprGb.Cli/Cpu/LegacyCpu.cs` 的來源。在這裡作為 oracle + perf baseline。
