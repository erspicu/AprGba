# GB block-JIT roadmap — variable-width + narrow-int LR35902

> **Status (2026-05-04 update)**：P0 + P1 主體已 ship。GB block-JIT mode
> 在 cpu_instrs (master) 跑到 **~21 MIPS @ 10k frames / ~27 MIPS @ 60k
> frames (compile amortised)**，原始 baseline 6.5 MIPS 的 3-4× 加速。
> P1 #5 V1 + P1 #5b SMC V2 機制設計已完成（env-gated 預設 OFF 保正確
> 性），優化留待 future V2/V3。
>
> 設計依據來自：
> 1. Gemini 諮詢（QEMU TCG / FEX-Emu / Dynarmic / mGBA / x86 backend
>    內部行為），紀錄在 `tools/knowledgebase/message/20260503_202431.txt`
> 2. 現有 codebase 結構掃描（`BlockDetector.cs:57-65` ctor throw、
>    `BlockFunctionBuilder.cs` 已 per-instr PC tracked、`Lr35902Emitters`
>    `read_imm8/imm16` runtime PC walk）
> 3. 起始 perf：GB legacy ~31 MIPS、GB JsonCpu (per-instr) ~6.5 MIPS、
>    GB block-JIT 不存在
>
> **目標**：把 GB JIT 從 6.5 → 15-25 MIPS（接近或超過 legacy 31）。
> 預期 leverage 來源：消除 per-instr dispatch overhead（`ResolveFunctionPointer`
> + indirect call + `CyclesFor` lookup + per-instr `_bus.Tick`）→ 攤到
> per-block 一次。
>
> **目標達成度（2026-05-04）**：cpu_instrs master 21 MIPS @ 10k 達標
> (15-25 MIPS 區間下緣)；60k frames 27 MIPS 進入區間中段。比起 legacy
> 31 MIPS 還差 13-32%，但 framework-driven 路徑 vs 手寫 dispatcher
> 已是合理 tradeoff。

---

## 1. 為什麼 GB JsonCpu 比 legacy 慢這麼多

對照 `JsonCpu.StepOne()` (line 304-353) 跟 `LegacyCpu.Step()`：

| 步驟 | JsonCpu | LegacyCpu |
|---|---|---|
| Read PC | `ReadI16(_pcOff)` ptr deref | `pc` field 直接讀 |
| Fetch byte | `_bus.ReadByte(pc)` interface call | inline byte array index |
| Decode | `decoder.Decode(opcode)` table lookup | switch case 直接跳 |
| Resolve fn | `ResolveFunctionPointer(setName, decoded)` | (no) |
| Compute fall-through | `ComputeFallThroughPc(opcode, pc)` switch | (no) |
| Cycle | `CyclesFor(decoded.Instruction)` lookup | inline |
| Tick bus | `_bus.Tick(stepCycles)` | inline counter |
| Call emitter | `(delegate*)fn(state, word)` indirect call | inline C# arith |

JsonCpu per-instruction overhead ~50-80ns；LegacyCpu ~5-10ns；JIT-compiled
emitter 本身 ~1-3ns useful work。**Dispatch overhead 完全淹沒 JIT 優勢**。

Block-JIT 解：把 `ResolveFunctionPointer` + `(delegate*)fn` + `CyclesFor` +
`_bus.Tick(per-instr)` 改成 per-block 一次，加上跨指令 LLVM 優化（CSE / DSE /
register caching）。

---

## 2. 變寬 ISA 的核心挑戰 + 設計依據

LR35902 length **完全由第一 byte 決定**（256-entry static table）：

| 第一 byte 範圍 | length | 範例 |
|---|---|---|
| 大多 | 1 byte | LD A,B / ADD A,C / NOP / RET / RST |
| imm8 / e8 | 2 bytes | LD A,n / ADD A,n / JR e8 |
| imm16 | 3 bytes | LD HL,nn / JP nn / CALL nn / LD (nn),A |
| 0xCB 開頭 | 2 bytes | BIT/SET/RES/RLC/RRC/RL/RR/SLA/SRA/SWAP/SRL × 8 reg |

**Gemini 三個明確建議**（綜合 QEMU TCG + FEX-Emu + Dynarmic + mGBA pattern）：

1. **變寬偵測**：sequential decode crawl，length 從 256-entry static table
   在 spec compile time 算好（不要 runtime infer）。每條 instruction 都有
   PC + length 統一記錄，Strategy 2 PC baking 自然繼續 work。
2. **i8/i16 native LLVM IR**：不要手動 promote 到 i32。LLVM `LegalizeTypes`
   + x86 backend 已處理（自動 `movzx` 破 false dep）。Sandy Bridge / Zen
   後 partial-register stalls 已 mitigate。**手動 mask 反而干擾 instcombine
   的 H-flag overflow detection pattern**。
3. **0xCB prefix 是 decoder state modifier 不是 ISA switch**：hierarchical
   trie decoder，整個 `CB xx` 視為 atomic 2-byte 指令，IRQ / page fault
   時 PC 永遠指向 0xCB byte 而非 sub-byte。Z80 DD/FD/ED 跟 x86 REX/VEX
   都用同套 trie pattern。

我這邊的補充 insight：**immediate baking 自然延伸 Strategy 2** — 把 imm8/imm16
直接塞進 `DecodedBlockInstruction.InstructionWord` 的高 bytes，spec 的
`read_imm8`/`read_imm16` 改成「shift + mask 讀 instruction word 的某 bits」
（跟 ARM imm extraction 完全同 pattern），不需要新加 `PreFetchedImm` 機制。

---

## 3. 完整優化清單 — 按 (cost × risk × value) 排序

下面是所有跟 GB block-JIT 相關 + 既有 Phase 7 H 群未完項目的綜合 priority。
**P0 = 必做 + 短時 + 解鎖後續**；**P4 = 投機 + 可能不做**。

### Tier P0 — Foundation（✅ **完工 2026-05-04**）

完工紀錄：`MD/performance/202605040000-gb-block-jit-p0-complete.md`。
Blargg cpu_instrs 全 11 子測試 PASS（含 BIOS LLE）；GB block-JIT MIPS
從 0 → 22.64（per-instr 9 的 +150%）。

| # | Item | Status | Commit |
|---|---|---|---|
| **1** | Variable-width `BlockDetector` | ✅ | `fdce42c` |
| **2** | 0xCB prefix as 2-byte atomic | ✅ | `381595b` |
| **3** | Immediate baking via instruction_word packing | ✅ | `da8cf91` |
| **4** | GB CLI `--block-jit` + Strategy 2 PC fixes | ✅ | `5b4092f` |

**P0 完工 milestone**：T1 360+ tests / T2 8-combo screenshot matrix（GBA
路徑不能退步）/ GB Blargg cpu_instrs 在 block-JIT mode 通過 / T3 bench
GB 09-loop100 從 6.5 → ≥10 MIPS（保守目標 ~50% 進步）。

#### P0 後續 — 已 ship

| # | Item | Status | Commit | 備註 |
|---|---|---|---|---|
| **P0.5** | HALT/STOP block boundary | ✅ | `c47d849` | detector 看 step `op:"halt"`/`"stop"` 自動切 block |
| **P0.5b** | EI delay band-aid (block ends at EI+1) | ✅ partial | `771d170` | hardcoded LR35902-specific；P0.6 取代 |
| **P0.5c** | `Lr35902Alu8Emitter.FetchImmediate` Strategy 2 baking | ✅ | `3617240` | + `--diff-bjit=N` lockstep harness；Blargg 01-special PASSED |
| **P0.6** | Generic `defer` micro-op + AST pre-pass | ✅ | `51c2921` | Phantom-instruction-injection pattern；replaced P0.5b hardcode；details in `MD/design/13-defer-microop.md` |
| **P0.7** | **Hybrid IRQ delivery — fast/slow split + `sync` micro-op** | ✅ | `0c001fc` + `999f9eb` | Per-instr-grained IRQ correctness in block-JIT；MMIO write callback returns sync flag, JIT exits block on sync；details in `MD/design/14-irq-sync-fastslow.md` |
| **P0.7b** | Conditional branch taken-cycle accounting fix | ✅ | `f27450f` + `7dd1e04` | pre-exit BB for taken-branch cycle deduct (smaller GBA perf hit revision)。**Known regression**：GBA bjit -16% 自此 commit；待 (C) 修復 |

### Tier P1 — Big-win 延伸（✅ **主體完工 2026-05-04**）

| # | Item | Status | Commit | 備註 |
|---|---|---|---|---|
| **5** | **Native i8/i16 + block-local state caching** | ✅ V1 機制 | `db9375c` | EmitContext.GprShadowSlots/StatusShadowSlots + ctx.GepGpr/GepStatusRegister + ctx.DrainShadowsToState；7 GPR + F + SP shadow allocas；mem2reg promote 到 SSA。V1 unconditional allocate，cpu_instrs 小 block 反而 -4% 因 entry-load + exit-drain 開銷大於內部節省。**V2 待做**：per-block live-range analysis 只 alloc 真正用到的 reg。`APR_DISABLE_SHADOW=1` env 可關掉做 A/B bench。 |
| **5b** | **SMC V2: IR-level inline notify + 精確 per-instr coverage + cross-jump-into-RAM** | ✅ 機制 (env-gated) | `377379c` | 三個 piece：(a) `EmitSmcCoverageNotify` 在每個 WRAM/HRAM inline 寫後加 1-byte cov check + cold-path notify call (gate `APR_SMC_INLINE_NOTIFY=1`)、(b) CachedBlock 加 `CoverageInstrPcs/Lens` 精確 per-instr 範圍 (always-on)、(c) BlockDetector 解禁 cross-jump-into-RAM (gate `APR_CROSS_JUMP_RAM=1`)。預設兩 env OFF 保 V1 行為（cpu_instrs 11/11 PASS）；ON 後 cpu_instrs sub-test 03 livelock 因 invalidation 下 cycle accounting drift。Bonus: 加 illegal-opcode (0xDD/...) NOP fallback 避免 cross-jump 撞 illegal byte 時 crash。 |
| **6** | **Detector cross unconditional B/JR/JP** | ✅ ROM-only | `b9dd0dd` | LR35902 0x18 (JR e8) + 0xC3 (JP nn) 跨 follow；CALL/RET/JP HL (dynamic) 不 follow。V1 限制 ROM-to-ROM (source ≤ 0x7FFF AND target ≤ 0x7FFF)；V2 (P1 #5b 解禁) 已有但 env-gated。 |
| **7** | **E.c IR-level memory region inline check** | ✅ | `787a8e5` | WRAM (0xC000-0xDFFF) / HRAM (0xFF80-0xFFFE) inline GEP-store 略 bus extern；仍走 sync-flag extern 對 MMIO/cart-RAM。Lr35902WramBase/HramBase 兩個 extern global 由 JsonCpu.Reset pinned bind。 |

**P1 完工 milestone（已達）**：
- T1 365/365 + T2 8-combo GBA canonical hash 不變（ARM 路徑 P1 #5 shadow gated 在 LR35902）
- Blargg cpu_instrs master 11/11 PASS @ ~21 MIPS (10k frames) / ~27 MIPS (60k frames)
- GB 09-loop100 比起 P0 baseline 22.6 MIPS 沒實質退步（V1 shadow -4% 但 SMC 基礎設施加進來持平）
- 跟 legacy 31 MIPS 還差 13-32%；framework-driven path 跟手寫 dispatcher 的 gap 合理

**P1 已知 followups（待 V2/V3）**：
- P1 #5 V2：per-block live analysis、shadow 只 alloc 真正用到的 reg → 期待翻成 +X%
- P1 #5b V3：解 SMC 開啟下的 cycle drift；mGBA/Dolphin 用 deferred invalidation
  pattern (current block 跑完才生效) 是可參考方案
- GBA bjit P0.7b regression (-16%) 仍未修

### Tier P2 — 日常維護 + 中等價值（按需求）

| # | Item | Status | Cost | Risk | Value | 備註 |
|---|---|---|---|---|---|---|
| 8 | **A.5 SMC detection + invalidation** | ✅ V1+V2 | M | L | (correctness) | V1 (`24a58d1`) per-byte coverage + bus-extern path notify；V2 (`377379c`) IR-level inline notify + per-instr 精確 coverage + cross-jump-into-RAM 解禁（env-gated）。詳見 P1 表 #5b。**P1 #5/#5b 重疊** — 從 P2 升到 P1 範疇。 |
| 9 | **A.9 Performance profiling tool** | ⏳ pending | S | L | (diagnostic) | `--bench-blocks` 印 per-block 編譯次數 / 執行次數；做完 P1 #5 V1 後做才有意義。**推薦下一個 pick** — 投資少回報高的偵察工具，後續任何 perf 工作都需要它做 PGO。 |
| 10 | **A.8 State→register caching aggressive** | ✅ 跟 P1 #5 重疊 | M | M | M | P1 #5 V1 已 ship 同樣機制（block entry load shadow，exit drain）；A.8 原本標的就是這個。標 done。 |
| 11 | **H.b Spec-time IR pre-processing** (dead-flag elim) | ⏳ pending | L | M | M | SpecCompiler 階段做 def-use analysis，跨指令 dead flag write 省掉。LR35902 連續 ALU (ADD-ADC-ADC) 中間 H/N flag 多半被覆寫——可省。 |
| 12 | **H.c Hot-opcode inlining to dispatcher** | ⏳ pending | L | M | M | top-N 個 hot opcode (MOV/ADD/LDR) 直接 inline 進 switch case，省 indirect call；需要 PGO 統計（→ #9 prerequisite）。 |
| 13 | **H.d LR35902 dispatcher GBA-parity** | ⏳ pending | M | L | L-M | block-JIT 上線後 per-instr 路徑次要化，這群 sub-優化價值降低。 |

### Tier P3 — 風險高 / 收益低（看情況決定要不要做）

| # | Item | Status | Cost | Risk | Value | 備註 |
|---|---|---|---|---|---|---|
| 14 | **A.7 Block linking** (patch native call) | ⏳ pending | L | H | M | 跨 OS native code patching 複雜；ORC stub 機制可能有 caveat。block-JIT 已上線後若 dispatch overhead 仍是 bottleneck 才動。 |
| 15 | **C.b lazy flag retry / LR35902 H-flag lazy** | ⏸ deferred | M | H | L | ARM C.b 試了 2 次都失敗 (shadow drain edge cases)；收益 <1%；P1 #5 V1 已用同樣 shadow alloca pattern 處理 F register block-local，覆蓋類似 case。除非 hot path profile 證實 ReadFlag 是 bottleneck 才再試。 |
| 16 | **H.e Cycle accounting real batch** | ⏳ pending | S | M | L | per-instr +=4 改 per-block += N*4；省幾 ns/instr 但 IRQ delivery timing 風險。Phase 1a predictive downcounting 已 ship，再 batch 收益更小。 |
| 17 | **H.f Per-process opcode profiling persistence** | ⏳ pending | M | L | L | disk cache opcode 統計；對 startup 友善 runtime 不變。 |
| 18 | **G.a Native AOT** | ⏳ pending | M | M | L | build-time 改動；對 cold-start 有幫助、runtime 持平；LLVMSharp interop 是否 AOT-friendly 待驗。 |
| 19 | **G.b UnmanagedCallersOnly IL emit** | ⏳ pending | L | H | L | micro-optimization；要熟 .NET IL 內部。 |

### Tier P4 — 投機 / 可能不做（除非有外部需求）

| # | Item | Cost | Risk | Value | 備註 |
|---|---|---|---|---|---|
| 20 | **H.g LLVM IR 自定 calling convention** | XL | H | M (理論) | 要動 LLVM tablegen 級；風險高、可能踩 LLVM bug |
| 21 | **H.i AOT bitcode cache** | L | M | (startup only) | spec→IR 結果 serialize 進 disk；startup 時間有幫助、runtime 不變 |

---

## 4. P0 實作詳細計劃（依序動）

### 4.1 Step 1 — Variable-width `BlockDetector`

**檔案**：`src/AprCpu.Core/Runtime/BlockDetector.cs` + `Block.cs`

**改動**：
1. `Block.InstrSizeBytes` (current: 整個 block 一個值) — 為了向後相容保留，
   但語意改成「fixed-width set 才有效；變寬 set 為 0」
2. `DecodedBlockInstruction` record 加 `byte LengthBytes` 欄位
3. `BlockDetector` ctor 不再 throw on variable-width；改成：
   - if `WidthBits.Fixed.HasValue` → 走原 fixed-stride path
   - else → 走 sequential-crawl path，從 spec `instruction_length_table`
     (新欄位) 查 length
4. 變寬 path 的 `ReadInstructionWord`：
   - `len=1` → `bus.ReadByte(pc)` → uint LSB
   - `len=2` → `bus.ReadByte(pc) | (bus.ReadByte(pc+1) << 8)`
   - `len=3` → 三 bytes packed
5. 加 spec schema 欄位 `instruction_length_table` (256-entry array of
   length values)；`SpecLoader` 讀進 `InstructionSetSpec`

**Spec 改動**：`spec/lr35902/main.json` 加 256-entry length table
（或從 group files 自動推算 + 在 SpecCompiler 階段建表）

**驗證**：
- 新 unit test：variable-width detector 在 LR35902 spec 上 detect block
  `LD A,5; ADD A,3; LD B,A; HALT` (1+2+2+1+1 = 7 bytes) → 4 個 instr
  with correct PCs + length
- ARM/Thumb path（fixed-width）regression 不能掛
- T1 全綠

### 4.2 Step 2 — 0xCB prefix as 2-byte atomic

**檔案**：`spec/lr35902/groups/block3-cb-prefix.json` + `BlockDetector` +
`SpecLoader`

**改動**：
1. Spec 加 `prefix_to_set: "CB"` 欄位（取代 `switches_instruction_set: true`）
2. `BlockDetector` 偵測 prefix opcode 時：
   - 不切 block
   - Fetch 第二 byte
   - 用 `CB` decoder 解 sub-opcode
   - 整個 `CB xx` 寫進 1 個 `DecodedBlockInstruction`，length=2，
     instruction word = `(0xCB << 8) | sub_opcode` 或 `sub_opcode << 8 | 0xCB`
     （依 endian / 對齊 emitter 而定）
3. `SpecLoader` 讀 `prefix_to_set` + 在 InstructionSetSpec 暴露 sub-set
   reference

**驗證**：
- 單元 test：detect block `BIT 7,A; SET 0,B; HALT` → 3 個 instr
  (前兩個 length=2)
- T1 全綠

### 4.3 Step 3 — Immediate baking via instruction_word packing

**檔案**：`src/AprCpu.Core/IR/Lr35902Emitters.cs` (`Lr35902ReadImm8Emitter`,
`Lr35902ReadImm16Emitter`)

**改動**：
1. `read_imm8` emitter 改成：
   - if `ctx.CurrentInstructionBaseAddress is uint` (block-JIT mode) →
     從 `ctx.Instruction` (constant integer) 取 high byte (`(ins >> 8) & 0xFF`)
     並設給 `out` var；**不發 bus call**
   - else (per-instr mode) → 走原 bus.ReadByte path
2. `read_imm16` 同理但取 16 bits
3. PC advance 在 block-JIT mode 也不需要（block IR 內 PC 是 baked constant）
4. **意外好處**：`read_imm8/16` 退化成 BitPattern field extract pattern，
   跟 ARM 的 instruction word imm extract 結構統一，可能未來能完全移除這
   兩個 emitter 改用 generic `extract_field`

**驗證**：
- 單元 test：block IR for `LD A,#42` 應該完全沒 `bus.ReadByte` call，
  只有 const 42 store 進 R[A]
- T1 全綠

### 4.4 Step 4 — GB CLI `--block-jit` flag

**檔案**：`src/AprGb.Cli/Program.cs` + `src/AprGb.Cli/Cpu/JsonCpu.cs`

**改動**：
1. `Program.cs` 加 `--block-jit` flag parsing（仿 `AprGba.Cli`）
2. JsonCpu 改成 wrap CpuExecutor（或直接走 CpuExecutor.EnableBlockJit）
3. GB-specific 部分（halt / IME / IRQ）保留在 outer loop，block fn
   只跑 instruction stream

**驗證**：
- `apr-gb --rom=... --bios=BIOS/gb_bios.bin --cpu=json-llvm --block-jit
  --frames=300 --screenshot=temp/gb-bjit.png` 跑通
- DMG Nintendo® logo 截圖跟 per-instr / legacy 一致
- Blargg cpu_instrs 11/11 在 block-JIT mode 全綠
- T2 8-combo screenshot matrix（GBA）regression 不能掛
- T3 bench：09-loop100 GB block-JIT MIPS，預期 6.5 → ≥10

---

## 5. 完成判定

P0 完成（✅ 2026-05-04）= `MD/performance/202605040000-gb-block-jit-p0-complete.md`
紀錄：
- T1 全綠（含新 variable-width detector 單元 test）
- T2 8-combo screenshot matrix（GBA 路徑全部 canonical hash 不退步）
- GB Blargg cpu_instrs 11/11 在 legacy / json-llvm-per-instr / json-llvm-bjit
  三 mode 都通過（serial output 抓 "Passed all tests"）
- T3 bench：GB 09-loop100 三 mode + 3 run，json-llvm-bjit MIPS 比 per-instr
  顯著進步（達成）

P1 完成（✅ 2026-05-04）= P0 全部 ✓ + 下列：
- P1 #5 V1 shadow 機制 ship、T1+T2+Blargg 不退步（V1 perf -4% 是 known cost，
  V2 待做）
- P1 #5b SMC V2 三件 piece ship (env-gated 預設 OFF)
- P1 #6 ROM-only cross-jump ship；P1 #7 IR-level WRAM/HRAM inline ship
- cpu_instrs master 11/11 PASS @ ~21 MIPS @ 10k frames

P2/P3/P4 — profile-driven 觸發。下一步建議 pick：
- **P2 #9 A.9 profiling tool** (S/L/diagnostic) — 後續任何 perf 工作的
  prerequisite。投資少回報高。
- **P1 #5 V2** — 把 V1 shadow 的 -4% 翻成正向（per-block live-range
  analysis）
- **P1 #5b V3** — 解 SMC inline notify 開啟下的 cycle drift（mGBA/Dolphin
  deferred-invalidation pattern 可參考）
- **GBA bjit P0.7b regression -16% 修復**（commit `7dd1e04` 留下）

---

## 6. 跟現有 roadmap 的關係

本檔取代 `03-roadmap.md` 內 H 群關於 GB / variable-width 的部分模糊 hint
(`H.d LR35902 dispatcher 跟 GBA path 等價優化` 仍保留，只是 priority 降為
P2 因為 block-JIT 上線後價值降低)。`03-roadmap.md` 的 Phase 7 主進度表保
持為 ARM block-JIT 進度紀錄 + Group A-F-H 整體框架；本檔聚焦 GB-specific
+ variable-width 子題。

P0 + P1 完工後 `03-roadmap.md` Phase 7 進度快照待補一段「GB block-JIT
P0+P1 ship 紀錄 + 對應 perf 數字」。**TODO**：把這條當 housekeeping 動作。

---

## 7. 風險登記（部分已 retire；列為歷史紀錄）

| 風險 | 結局 |
|---|---|
| Variable-width detector 設計過早抽象、卡 spec schema 設計 | ✅ **避過** — `Lr35902InstructionLengths.GetLength` static 256-entry table，沒進 spec schema，detector 用 `lengthOracle` callback 注入。 |
| CB-prefix 雙層 decode 在 instruction_word 編碼方式跟 emitter 預期不一致 | ✅ **避過** — `prefix_to_set: "CB"` spec 欄位 + sub-decoder 對 8-bit sub-opcode 直接 mask/match，沒做 shift-by-8 gymnastics。 |
| GB block-JIT 一上線發現 i8/i16 native 真的有 partial-register stall | ✅ **沒踩到** — Gemini 預測正確；現代 x86 backend (Sandy Bridge / Zen 後) 已 mitigate；i8 LR35902 GPR + i16 SP 在 IR 直接用 native width 沒 stall 跡象。 |
| Detector 跨 unconditional B 撞到 ROM bank switch / SMC | ✅ **mitigated** — P1 #6 V1 限制 ROM-to-ROM；V2 (P1 #5b) 加 SMC inline notify 解禁。 |
| GB block-JIT 一上線比 per-instr 慢 | ✅ **避過** — P0 完工時 22.6 MIPS vs per-instr 6.5 MIPS = 3.5× 加速；後續 P1 進步到 27 MIPS。 |
| **新風險（P1 後浮出）：SMC invalidation 觸發的 cycle accounting drift** | ⏳ **active** — `APR_SMC_INLINE_NOTIFY=1` ON 時 cpu_instrs sub-test 03 livelock；待 V3 deferred-invalidation pattern 解。 |
| **新風險：GBA bjit perf -16% regression** (`7dd1e04`) | ⏳ **active** — P0.7b conditional branch taken-cycle accounting fix 留下；待 (C) 修復 |
