# GB block-JIT roadmap — variable-width + narrow-int LR35902

> **Status**：planning doc (2026-05-03)。設計依據來自：
> 1. Gemini 諮詢（QEMU TCG / FEX-Emu / Dynarmic / mGBA / x86 backend
>    內部行為），紀錄在 `tools/knowledgebase/message/20260503_202431.txt`
> 2. 現有 codebase 結構掃描（`BlockDetector.cs:57-65` ctor throw、
>    `BlockFunctionBuilder.cs` 已 per-instr PC tracked、`Lr35902Emitters`
>    `read_imm8/imm16` runtime PC walk）
> 3. 現況 perf：GB legacy ~31 MIPS、GB JsonCpu (per-instr) ~6.5 MIPS、
>    GB block-JIT 不存在
>
> **目標**：把 GB JIT 從 6.5 → 15-25 MIPS（接近或超過 legacy 31）。
> 預期 leverage 來源：消除 per-instr dispatch overhead（`ResolveFunctionPointer`
> + indirect call + `CyclesFor` lookup + per-instr `_bus.Tick`）→ 攤到
> per-block 一次。

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

### Tier P0 — Foundation（共 ~3 天，必做才能解鎖任何後續）

| # | Item | Cost | Risk | Value | 備註 |
|---|---|---|---|---|---|
| **1** | **Variable-width `BlockDetector`** | M | L | H (enabler) | Sequential crawl + 256-entry length table from spec；ctor 不再 throw |
| **2** | **0xCB prefix as 2-byte atomic** | S | L | H (~2× block size) | Spec 加 `prefix_to_set: "CB"` 欄位；detector 自動 fetch+lookup |
| **3** | **Immediate baking via instruction_word packing** | S | L | M | `read_imm8/16` 退化成 BitPattern field extract；統一 ARM 模式 |
| **4** | **GB CLI `--block-jit` flag + CpuExecutor wiring** | S | L | (enabler) | `AprGb.Cli/Program.cs` 加 flag；GB 接 `CpuExecutor.EnableBlockJit` |

**P0 完工 milestone**：T1 360+ tests / T2 8-combo screenshot matrix（GBA
路徑不能退步）/ GB Blargg cpu_instrs 在 block-JIT mode 通過 / T3 bench
GB 09-loop100 從 6.5 → ≥10 MIPS（保守目標 ~50% 進步）。

### Tier P1 — Big-win 延伸（共 ~3-4 天，P0 完成後才有意義）

| # | Item | Cost | Risk | Value | 備註 |
|---|---|---|---|---|---|
| **5** | **Native i8/i16 + block-local state caching** | M | L | H | Gemini 建議：load all GPR at block entry SSA、store at exit；mem2reg + RA 自動 promote 到 x86 GPR；對 narrow-int CPU 應該是大幅 |
| **6** | **Detector cross unconditional B/JR/JP** | M-L | M | H | BIOS bjit 目前比 pi 慢就是因 block 平均 1.0-1.1 instr；detector 跨 unconditional B 把 target detect 進來連續編譯，預期 block 拉到 5-10 instr，bjit 應勝 pi 50%+ |
| **7** | **E.c IR-level memory region inline check** | M | L | M-H | 把 `bus.ReadByte` 的 region check inline 進 IR；mem-heavy ROM (LDR/STR-多) + BIOS LLE 受惠大；不依賴 mem2reg、可獨立做 |

**P1 完工 milestone**：GB 09-loop100 ~15-25 MIPS（追上或超 legacy 31）；
BIOS LLE bjit ≥ per-instr。

### Tier P2 — 日常維護 + 中等價值（按需求）

| # | Item | Cost | Risk | Value | 備註 |
|---|---|---|---|---|---|
| 8 | **A.5 SMC detection + invalidation** | M | L | (correctness) | Self-modifying code 罕見但漏掉 silent corruption；A.4 cache 已就位 |
| 9 | **A.9 Performance profiling tool** | S | L | (diagnostic) | `--bench-blocks` 印 per-block 編譯次數 / 執行次數；做完整 Group A 後才有意義 |
| 10 | **A.8 State→register caching aggressive** | M | M | M | mem2reg 自動已撈大部分；要明確 entry-load + exit-store 模式才更激進 |
| 11 | **H.b Spec-time IR pre-processing** (dead-flag elim) | L | M | M | SpecCompiler 階段做 def-use analysis，跨指令 dead flag write 省掉 |
| 12 | **H.c Hot-opcode inlining to dispatcher** | L | M | M | top-N 個 hot opcode (MOV/ADD/LDR) 直接 inline 進 switch case，省 indirect call；需要 PGO 統計 |
| 13 | **H.d LR35902 dispatcher GBA-parity** | M | L | L-M | block-JIT 上線後 per-instr 路徑次要化，這群 sub-優化價值降低 |

### Tier P3 — 風險高 / 收益低（看情況決定要不要做）

| # | Item | Cost | Risk | Value | 備註 |
|---|---|---|---|---|---|
| 14 | **A.7 Block linking** (patch native call) | L | H | M | 跨 OS native code patching 複雜；ORC stub 機制可能有 caveat |
| 15 | **C.b lazy flag retry / LR35902 H-flag lazy** | M | H | L | ARM C.b 試了 2 次都失敗 (shadow drain edge cases)；收益 <1%；除非 hot path profile 證實 ReadFlag 是 bottleneck |
| 16 | **H.e Cycle accounting real batch** | S | M | L | per-instr +=4 改 per-block += N*4；省幾 ns/instr 但 IRQ delivery timing 風險 |
| 17 | **H.f Per-process opcode profiling persistence** | M | L | L | disk cache opcode 統計；對 startup 友善 runtime 不變 |
| 18 | **G.a Native AOT** | M | M | L | build-time 改動；對 cold-start 有幫助、runtime 持平；LLVMSharp interop 是否 AOT-friendly 待驗 |
| 19 | **G.b UnmanagedCallersOnly IL emit** | L | H | L | micro-optimization；要熟 .NET IL 內部 |

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

P0 完成 = `MD/performance/<時戳>-gb-block-jit-p0.md` 紀錄：
- T1 全綠（含新 variable-width detector 單元 test）
- T2 8-combo screenshot matrix（GBA 路徑全部 canonical hash 不退步）
- GB Blargg cpu_instrs 11/11 在 legacy / json-llvm-per-instr / json-llvm-bjit
  三 mode 都通過（serial output 抓 "Passed all tests"）
- T3 bench：GB 09-loop100 三 mode + 3 run，json-llvm-bjit MIPS 比 per-instr
  顯著進步（保守 ≥+50%）

P0 通過後決定：
- P1 (5/6/7) 繼續推 → 目標 ≥legacy 速度
- 還是先回頭做 P2 SMC / profiling 做 framework 補強

P3/P4 留 future，profile-driven 觸發。

---

## 6. 跟現有 roadmap 的關係

本檔取代 `03-roadmap.md` 內 H 群關於 GB / variable-width 的部分模糊 hint
(`H.d LR35902 dispatcher 跟 GBA path 等價優化` 仍保留，只是 priority 降為
P2 因為 block-JIT 上線後價值降低)。`03-roadmap.md` 的 Phase 7 主進度表保
持為 ARM block-JIT 進度紀錄 + Group A-F-H 整體框架；本檔聚焦 GB-specific
+ variable-width 子題。

實作完 P0 後，回頭更新 `03-roadmap.md` 加一條「Phase 7 X. GB block-JIT
support」標 ✅，並回填 perf 數字。

---

## 7. 風險登記

| 風險 | mitigation |
|---|---|
| Variable-width detector 設計過早抽象、卡 spec schema 設計 | P0 step 1 先用 hardcoded 256-table（in `Lr35902Emitters` 或新 `Lr35902Lengths.cs`），確認 detector loop 可行；spec schema 等需求清楚再加 |
| CB-prefix 雙層 decode 在 instruction_word 編碼方式跟 emitter 預期不一致 | 加 `Lr35902CbPrefixDecoderTest` 單元測試覆蓋所有 256 個 CB-opcode，emitter 行為跟 LegacyCpu 對拍 |
| GB block-JIT 一上線發現 i8/i16 native 真的有 partial-register stall（Gemini 說沒，但實機驗才知道） | P1 step 5 把 i8/i16 → i32 promote 留為 fallback 選項，code 上拉 abstraction 不要 hardcode 寬度假設 |
| Detector 跨 unconditional B 撞到 ROM bank switch / SMC | P1 step 6 先限制只跨「同 region 內 + cycle budget 還夠」的 B；遇到 cross-bank 就停 |
| GB block-JIT 一上線比 per-instr 慢（如 GBA BIOS bjit 翻轉）| P0 完成 bench 證實有進步才往 P1 推；如果 P0 反而 regress，先 root-cause 不要硬推 P1 |
