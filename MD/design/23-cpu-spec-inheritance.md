# CPU spec inheritance — JSON 規格的「繼承 + override」機制

> **Status**：**DRAFT v2**（2026-05-09 末，吸收 Gemini review）。
> **Review 紀錄**：`tools/knowledgebase/message/20260509_233848.txt`
>
> **Trigger**：未來預計做 Intel 16-bit family（8086 / 80186 / 80286）+
> 32-bit family（80386 起）。觀察：同一 family 內 CPU 共通性極高（80%+
> opcode 重複），逐顆從零寫 spec 是浪費；但跨 family（16→32 bit）差
> 異太大，硬繼承反而拖累。
>
> 提議：給 spec 加 **「Data-Driven Overlay/Mixin」** 機制（不是 OOP runtime
> 繼承 — 是 build/load-time AST macro 系統），**同 family 內**用 `extends`
> 堆疊；**跨 family 起新 chain**；**可選功能模組**（FPU、SSE 等）走
> additive-only `traits` 避免 combinatorial 爆炸。
>
> **v2 主要變更**（吸收 Gemini critique）：
> 1. Override 改成 **partial merge (RFC 7386)** 而非整個 replace（x86 cycles
>    跨世代變動大但 encoding 不變）
> 2. Selector 改用 **stable string ID** 而非 `opcode_byte`（x86 ModR/M
>    opcode extension 同 byte 多 mnemonic）
> 3. **`from` field 是 hard validation**（父沒這 ID 就 fail-load）
> 4. 加 **`traits` / additive-only overlays** 處理 FPU 等可選擴充
> 5. 8086/8088 走 **sibling pattern**（共抽 `_base_808x.json`）而非合併單一 spec
> 6. Intel chain 切點完整列出 (16-bit / 32-bit / MMX / SSE / x86-64 五條)
> 7. Inheritance depth **hard cap = 4**（Gemini 警告 deep tree 是 anti-pattern）
>
> **目標讀者**：(a) review 後決定走 / 不走的人；(b) 真要 implement 時的執行者。

---

## 1. Motivation

### 1.1 觀察 — Intel 16-bit family 共通度

| CPU | 推出年 | 跟前一顆比新增 | 共通度 |
|---|---|---|---|
| 8086 / 8088 | 1978 | base | 100% |
| 80186 / 80188 | 1982 | +12 opcodes（PUSH imm / IMUL imm / ENTER / LEAVE / INS / OUTS / BOUND / immediate-shift / 等） | ~96% (12/256) |
| 80286 | 1982 | +15 opcodes（LGDT / LIDT / LLDT / LMSW / SMSW / ARPL / VERR / VERW / 等 protected mode plumbing） | ~94% (15/256, 部分覆蓋 reserved 0x0F escape) |

**~94-96% identical** — 等於每加一顆 CPU 寫 3000 行 emitter 跟 spec 但
其中 2800+ 是 copy-paste 既有的。**這是 framework genericity 的反面 —
不僅沒槓桿、還傷維護**（同個 opcode bug 要修 3 次）。

### 1.2 觀察 — 32-bit / 64-bit 應該斷開

| 變化點 | 為什麼斷開 |
|---|---|
| 16→32 bit (80386) | EAX / AX 雙視角；32-bit addressing modes (SIB byte)；新 prefix bytes (0x66/0x67 size override) — 不只是「加 opcode」級別變化 |
| 32→64 bit (x86-64) | RAX / EAX 三視角；REX prefix；R8-R15；long mode；instruction encoding 多個維度變動 |
| MMX / SSE / AVX | Register file 形狀變（XMM/YMM/ZMM 加新 file）；inheritance 處理不了 file 結構變 |

**繼承的價值在「opcode 加減 + 細節調整」**；架構性變動硬硬繼承會把 base
spec 撐爆 + 邏輯複雜化超過收益。

### 1.3 N0-N11 已建立的 framework 抽象

本 sprint 已經把 spec 拆成 `CpuSpec` (ISA) + `MachineSpec` (board)
兩層（doc #19）。**Inheritance 是第三層 spec organization** — `CpuSpec`
內部的層級結構（base → derived）。

### 1.4 跟業界做法的定位

| Project | 機制 | 主要痛點 |
|---|---|---|
| **MAME** | C++ OOP 繼承 (device_t) | ISA execution 退化成 macro / 大 switch + `if (has_feature_X)` |
| **QEMU TCG** | 單目錄 monolithic + runtime feature flag | 沒 declarative inheritance；CPUID 在 translation loop 動態查 |
| **Ghidra SLEIGH** | Preprocessor `@include` + constructor override | include chain spaghetti；資料 trace 困難 |
| **ArchC** | Pure C++ inheritance | 跟 MAME 同問題 |
| **AprCpu (本 doc)** | JSON Patch / kustomize-like + load-time merge | runtime 看不到階層，JIT/decoder 不變 |

**核心定位**：本 doc 不是發明 OOP runtime 繼承，而是 **build/load-time
data overlay**。SpecLoader merge 完之後 SpecCompiler / DecoderTable /
runtime 完全不知道 inheritance 存在 — 跟既有 spec 路徑等價。

---

## 2. 設計提議

### 2.1 兩個層級的繼承

| 層級 | 範圍 | 機制 |
|---|---|---|
| **Family-internal** | 同 word size + 同 register file shape + 同 endianness | `extends: "<parent-cpu-id>"` 自動 merge |
| **Family-cross** | 不同 word size / register file 形狀 | **不繼承**；起新 spec/<family-32>/ chain |

### 2.2 Schema 草案 — `extends` + stable instruction ID

**v2 改動 (Gemini critique #2)**：selector 從 `opcode_byte` 改成 instruction
ID 字串。動機：x86 用 ModR/M 的 reg 欄位當 opcode extension（例如 byte
`0x80` 一個 primary opcode 對應 8 個 mnemonic — ADD/OR/ADC/SBB/AND/SUB/
XOR/CMP），byte-only selector 解析不出單一 instruction。

每個 instruction 都加 `"id": "<UNIQUE_STABLE_ID>"`（e.g. `"ADD_rm8_imm8"`）；
override / remove 用 ID 當 key。

```json
// spec/cpu/x86-16/i8086/cpu.json — base
{
    "$schema": "../../schema/cpu-spec.schema.json",
    "spec_version": "1.0",
    "architecture": {
        "id": "i8086",
        "family": "x86-16",
        "endianness": "little",
        "word_size_bits": 16
    },
    "register_file": { ... },
    "instruction_sets": {
        "Main": {
            "groups": [
                "$include:groups/data-transfer.json",
                "$include:groups/arithmetic.json",
                ...
            ]
        }
    }
}

// 例：spec/cpu/x86-16/i8086/groups/arithmetic.json 中的 instruction
[
    {
        "id": "MUL_rm16",                  // ← 必加，全 spec 唯一
        "encoding": { "primary_opcode": "0xF7", "modrm_reg": "100" },
        "mnemonic": "MUL",
        "cycles": { "form": "118m" },     // 8086: ~118 cycles
        "steps": [...]
    },
    ...
]

// spec/cpu/x86-16/i80186/cpu.json — extends i8086
{
    "architecture": {
        "id": "i80186",
        "family": "x86-16",
        "extends": "i8086",                 // chain: i80186 → i8086
        "endianness": "little",
        "word_size_bits": 16
    },
    // register_file / status_registers 省略 — inherits from parent
    "instruction_set_diff": {
        "Main": {
            "additions": [
                "$include:groups/i80186-additions.json"
            ],
            // v2: partial merge (RFC 7386) — 只 declare 要動的 field
            "overrides": {
                "MUL_rm16": {
                    "cycles": { "form": "21m" }      // 80186: ~21 cycles
                    // mnemonic / encoding / steps 從 parent 繼承
                },
                "IMUL_rm16_imm16": {
                    "cycles": { "form": "22m" }
                }
            },
            "removals": [
                "POP_CS"                  // 80186 把這個 reserved 掉
            ]
        }
    }
}

// spec/cpu/x86-16/i80286/cpu.json — extends i80186
{
    "architecture": {
        "id": "i80286",
        "family": "x86-16",
        "extends": "i80186",                // chain: i80286 → i80186 → i8086
        ...
    },
    "instruction_set_diff": {
        "Main": {
            "additions": [
                "$include:groups/i80286-protmode.json"   // 0x0F escape opcodes (LGDT/LIDT/...)
            ],
            "overrides": {
                "MUL_rm16": {
                    "cycles": { "form": "13m" }      // 286: ~13 cycles 又快
                }
            }
        }
    }
}
```

**v2 重點變更（vs v1）**：
- **Selector** = instruction ID 字串（不是 opcode byte）
- **`overrides`** 是 dict (ID → partial-fields)；JSON Merge Patch 語意 — declared field 覆蓋父，未 declared field 從父繼承
- **`additions`** / **`removals`** 同前；removals 也用 ID
- 不再有 `from` field（partial-merge 模式下，每個 ID 必對應父中存在的 entry，否則 hard fail — 這個 invariant 取代 `from`）

### 2.3 Resolution algorithm（at SpecLoader time）

```
load(spec_path):
    raw = parse_json(spec_path)
    parent_id = raw.architecture.extends
    if parent_id is None:
        return finalize(raw)            # base spec
    parent_spec = load(locate(parent_id))   # recursive
    if depth(parent_spec) >= 4:
        raise SpecError("inheritance chain too deep (max 4)")  # v2: depth cap
    merged = deep_copy(parent_spec)
    apply_diff(merged, raw.instruction_set_diff)
    apply_traits(merged, raw.traits)        # v2: additive overlays
    merged.architecture = raw.architecture   # child's own id/extends preserved
    if raw.register_file: merged.register_file = raw.register_file   # 罕見，需才覆蓋
    return finalize(merged)

apply_diff(target, diff):
    for set_name, set_diff in diff.items():
        target_set = target.instruction_sets[set_name]
        # additions
        for group in set_diff.additions:
            for instr in load_group(group):
                if target_set.has_id(instr.id):
                    raise SpecError(f"addition '{instr.id}' collides with parent's instruction — use overrides instead")
                target_set.add_instruction(instr)
        # overrides — partial merge (RFC 7386 JSON Merge Patch)
        for instr_id, partial in set_diff.overrides.items():
            parent_instr = target_set.find_by_id(instr_id)
            if parent_instr is None:
                raise SpecError(f"override target '{instr_id}' not found in parent — typo?")  # v2: hard fail
            merged_instr = json_merge_patch(parent_instr, partial)
            merged_instr._inherited_from = parent_instr._origin_cpu   # v2: provenance tag
            merged_instr._overridden_by = current_cpu_id
            target_set.replace_by_id(instr_id, merged_instr)
        # removals
        for instr_id in set_diff.removals:
            if not target_set.has_id(instr_id):
                raise SpecError(f"removal target '{instr_id}' not found in parent")
            target_set.remove_by_id(instr_id)

apply_traits(target, traits):
    # v2 additive-only overlays — for FPU / SSE / etc. optional modules.
    # Strict: only `additions` allowed, no overrides/removals.
    for trait_id, trait_spec in traits.items():
        for instr in trait_spec.additions:
            if target.has_id(instr.id):
                raise SpecError(f"trait '{trait_id}' addition '{instr.id}' collides")
            target.add_instruction(instr)

# JSON Merge Patch (RFC 7386) — declared fields overwrite, undeclared inherit.
json_merge_patch(target, patch):
    if not isinstance(patch, dict): return patch
    result = deep_copy(target)
    for k, v in patch.items():
        if v is None:
            result.pop(k, None)            # explicit null = delete field
        elif isinstance(v, dict):
            result[k] = json_merge_patch(result.get(k, {}), v)
        else:
            result[k] = v
    return result
```

**完成後**：merged spec 跟「從零寫的 i80286 完整 spec」**等價** — 後續
SpecCompiler / DecoderTable / IR emit pipeline 完全不知道 inheritance
存在。Inheritance 只發生在 load time、不影響 runtime。

**v2 新增的 invariants（hard-fail 檢查）**：
- Inheritance depth ≤ 4
- `additions` 的 ID 不能跟父衝突（要 override 而非 add）
- `overrides` 的 ID 必存在於父（拒絕 typo / silent no-op）
- `removals` 的 ID 必存在於父
- `traits` 只能 `additions`（不允許 override / remove）
- 禁 cyclic inheritance（A → B → A）

### 2.4 IsaMetadata / RegisterFile / 等其他層的繼承策略

| 欄位 | 繼承行為 |
|---|---|
| `register_file` | 預設繼承 parent；child 覆蓋整個欄位需顯式 declare |
| `status_registers` | 預設繼承 parent；child 可加 bit (e.g. 80286 加 NT/IOPL) — 透過 `status_register_diff: {"FLAGS": {"added_bits": [...]}}` |
| `processor_modes` | 預設繼承 parent；child 可加 mode (e.g. 80286 加 protected mode) |
| `exception_vectors` | 預設繼承；child 可加（例如 80286 加 #UD / #NM 等） |
| `isa_metadata` | 預設繼承；child 可選擇性覆蓋 |
| `instruction_sets` | **必繼承**且**必透過 diff 修改**（不允許整個重寫，避免破壞 inheritance 意義） |

### 2.5 Emitter library 對應

Emitter 層也應該對應 family 結構：

```
src/AprCpu.Core/IR/
├── X86_16Emitters.cs      ← 共用 8086 / 80186 / 80286 (同 word size + reg file)
├── X86_32Emitters.cs      ← 共用 80386 / 80486 / Pentium (新 chain)
├── ArmEmitters.cs         ← 既有
├── Lr35902Emitters.cs     ← 既有
└── Mos6502Emitters.cs     ← 既有
```

新 family micro-op 只在 family-level emitter 寫一次；family 內所有 CPU
共用。

### 2.6 Traits — additive-only overlays（v2 新增）

**動機 (Gemini critique #b)**：可選功能模組（FPU 8087、MMX、特定 CPU 變體
有/沒 SSE）如果用 inheritance 表達會 combinatorial 爆炸：

```
不用 traits（純繼承）：
  i80386_no_fpu, i80386_with_8087, i80386_with_80387,
  i80386_no_fpu_with_mmx, i80386_with_8087_with_mmx, ...     ← N×M 個 CPU spec
```

**Traits 機制**：spec 宣告 `traits: { ... }` block，內容 strict additive
（只能加 instruction，不能 override / remove parent）。同一個 base CPU
spec 用不同 traits 組合產生不同 build target：

```json
// spec/coprocessors/i8087.json — FPU instruction set as a trait
{
    "trait_id": "i8087_fpu",
    "additions": [
        "$include:groups/x87-arithmetic.json",
        "$include:groups/x87-transcendental.json"
    ]
}

// spec/cpu/x86-16/i8086_with_8087.json — composition example
{
    "architecture": { "id": "i8086_with_8087", "extends": "i8086" },
    "traits": {
        "fpu": { "$ref": "../coprocessors/i8087.json" }
    }
}
```

**Traits 跟 inheritance 的差別**：

| Aspect | `extends` (inheritance) | `traits` (additive overlay) |
|---|---|---|
| 允許 override 父 instruction | ✓ | ✗ (hard-fail) |
| 允許 remove 父 instruction | ✓ | ✗ |
| 允許 add 新 instruction | ✓ | ✓ |
| 修改 register_file | ✓ (replace) | ✗ |
| 適用情境 | CPU 譜系（A 是 B 的後繼） | 可選擴充模組（FPU、SSE、custom inst set） |
| 數量限制 | 一條 chain ≤ 4 深 | 一個 spec 可 mix 多 traits |

**Trait 是 N×M → N+M 的解**：N 個 CPU + M 個 coprocessor / 擴充 = N+M
個 spec，不是 N×M。

---

## 3. 具體例子：Intel 16-bit family 完整移植路徑

**v2 改動 (Gemini critique #c)**：8086 跟 8088 採 **sibling pattern** —
ISA 100% 一樣但外部 bus 寬度不同（8088 8-bit external bus 拖 cycle、
prefetch queue 4 vs 6 byte），抽 abstract base 兩者各自 extend 並 override
cycle。比合併成單 spec 更精細、cycle accounting 也對。

```
sprint A: 寫 8086 base spec + X86_16Emitters.cs（不開 inheritance 機制）
  └── spec/cpu/x86-16/_base_808x.json + groups/*.json (~3000 lines, abstract)
  └── spec/cpu/x86-16/i8086.json (~50 lines, sibling 1 — 16-bit bus cycle)
  └── spec/cpu/x86-16/i8088.json (~80 lines, sibling 2 — 8-bit bus cycle penalty)
  └── X86_16Emitters.cs (~1500 lines, ModR/M + segment 處理)
  → 通過 8086 test ROM (e.g. Apr86 既有測試 + 經典 8086 demo)
  → 同 spec 同 emitter 兩種 cycle 對照

sprint B: 加 inheritance 機制到 SpecLoader + schema validation
  └── SpecLoader.LoadCpuSpec 走 extends chain；單元測試覆蓋 add/override/
      remove/cyclic-detect/depth-cap/traits-additive-only
  → T1 通過；既有 ARM/LR35902/2A03 spec 沒 extends 仍 work（backwards-compat）

sprint C: 寫 80186 spec — 純 diff (extends i8086)
  └── spec/cpu/x86-16/i80186/cpu.json (~150 lines diff)
  └── spec/cpu/x86-16/i80186/groups/i80186-additions.json (~200 lines, 12 new ops)
  └── X86_16Emitters.cs += 12 個 new emitter (ENTER / LEAVE / IMUL r,imm / 等)
  → 通過 80186 specific test

sprint D: 寫 80286 spec — extends i80186
  └── spec/cpu/x86-16/i80286/cpu.json (~250 lines diff)
  └── spec/cpu/x86-16/i80286/groups/i80286-protmode.json (~300 lines, 15 new ops + 0x0F escape group)
  └── X86_16Emitters.cs += 15 個 new emitter (LGDT / LIDT / LLDT / LMSW / 等)
  → 通過 80286 real-mode + protected-mode test
```

**每個 sprint 的 diff 體量**：80186 ~350 行，80286 ~550 行。對比沒繼承
要 3000+3000+3000 = 9000 行，**省掉 ~85%**。

**Inheritance chain 結構**:
```
_base_808x (abstract)
   ├── i8086 (sibling, 16-bit bus)
   └── i8088 (sibling, 8-bit bus)
          ↓ (i80186 extends i8088 OR i8086 — 規格統一 16-bit bus 邏輯，extends i8086)
       i80186
          ↓
       i80286
```

Chain 深度：`i80286 → i80186 → i8086 → _base_808x` = 4 層，剛好觸頂。
若需要再下一代必須思考是否走新 chain（per R1-R4 hard rules）。

---

## 4. 何時該斷開繼承（Hard rules）

| 規則 | 例子 | 理由 |
|---|---|---|
| **R1: word size 變動** | 16→32 (8086→80386), 32→64 (i486→x86-64) | EAX vs AX 雙視角 / R8-R15 / long mode / 16-bit-only opcode 失效 — 變動超過 inheritance 能 carry |
| **R2: register file 形狀變動** | x86 → x86+MMX (加 MM0-MM7), → x86+SSE (加 XMM0-XMM7) | RegisterFile shape 變等於 parent 整段 invalid |
| **R3: 主要 prefix-encoding scheme 變動** | x86-32 加 0x66 / 0x67 size override；x86-64 加 REX prefix | decoder 結構性變動，繼承後 base 跟 child 解碼路徑不一致 |
| **R4: 跨 family / 廠商 / endianness** | x86 ↔ ARM；little ↔ big endian | 完全不同 ISA |

### 4.1 完整 Intel chain 切點（v2 — 5 條 chain）

每條 chain 一個 `spec/<family>/` 目錄 + 一個 family-level emitter library。

```
Chain 1 — x86-16 (16-bit real-mode + 286 protected-mode plumbing)
spec/cpu/x86-16/
├── _base_808x.json                  ← abstract base (內部用)
├── i8086.json                       ← extends _base_808x
├── i8088.json                       ← extends _base_808x (sibling of 8086)
├── i80186.json                      ← extends i8086
└── i80286.json                      ← extends i80186  [chain depth 3-4]
src/AprCpu.Core/IR/X86_16Emitters.cs

Chain 2 — x86-32 (full 32-bit + paging)
spec/x86-32/
├── i80386.json                      ← NEW chain (R1 word size 變動 / R3 prefix scheme 變動)
├── i80486.json                      ← extends i80386
├── i_pentium.json                   ← extends i80486
└── i_pentium_pro.json               ← extends i_pentium  (P6: CMOV/SYSENTER/RDTSC，opcode 加而已)
src/AprCpu.Core/IR/X86_32Emitters.cs

Chain 3 — x86-MMX (Pentium MMX 起 — register file shape 變)
spec/x86-mmx/
├── i_pentium_mmx.json               ← NEW chain (R2 加 MM0-MM7，狀態 EMMS)
└── i_pentium_2.json                 ← extends i_pentium_mmx
src/AprCpu.Core/IR/X86_MmxEmitters.cs (跟 X86_32Emitters 共用 base，但 MM 操作獨立)

Chain 4 — x86-SSE (XMM register file)
spec/x86-sse/
├── i_pentium_3.json                 ← NEW chain (R2 加 XMM0-XMM7)
└── i_pentium_4.json                 ← extends i_pentium_3
src/AprCpu.Core/IR/X86_SseEmitters.cs

Chain 5 — x86-64 (long mode)
spec/x86-64/
└── amd64.json                       ← NEW chain (R1 word size 64 / R3 REX prefix / R2 R8-R15)
src/AprCpu.Core/IR/X86_64Emitters.cs
```

### 4.2 灰色案例 verdicts（v2 — 結論已寫死）

| 案例 | Verdict | 理由 |
|---|---|---|
| **80286 protected mode** | **繼承** (extends i80186) | real-mode 跟 80186 完全一樣；protected mode 是 mode 切換的 add-on (LGDT/LIDT/LLDT/LMSW 等系統 opcode)，不是 base ISA 改寫 |
| **80386 → 80286** | **斷開** (新 x86-32 chain) | SIB byte / 32-bit offset / 新 descriptor table / 分頁 CR0-CR4 / default size 反轉 — 即使 real-mode 95% 兼容，decoder 邏輯整個重寫 |
| **MMX (8 個 MM register aliasing FPU)** | **斷開** (新 x86-mmx chain) | 不只 register aliasing 這層 — EMMS 指令必要、80-bit float ↔ packed int data type 切換、tag word 狀態管理。塞進舊 chain 會污染 FPU 定義 |
| **SSE (XMM register file)** | **斷開** (新 x86-sse chain) | XMM0-XMM7 是新 register file，不只擴充 |
| **8086 ↔ 8088** | **Sibling pattern** (共抽 _base_808x.json) | ISA 100% 同；只 cycle timing 跟 prefetch queue 大小不同 |
| **Pentium → Pentium Pro (P6)** | **不斷開** (extends i_pentium) | OOO/register renaming 對 ISA-level JIT 完全 invisible；只加 CMOV/SYSENTER/FCMOV/RDTSC，inheritance carry 得了 |

---

## 5. 風險 / Trade-offs

### 5.1 Pro

| 收益 | 量化 |
|---|---|
| Code/spec 重用 | 第 N 顆 CPU 增量寫 ~10-15% diff（vs 100%）|
| Bug 修一次散播全 chain | shared opcode bug 在 base 修；child 自動受惠 |
| Genealogy 清晰 | spec 自記「我是誰 + 從哪繼承」，閱讀 / paper 引用都直觀 |
| 漸進加入新 CPU 邊際成本低 | 80186 移植可能 1-2 天 vs 1 週 |

### 5.2 Con

| 風險 | 嚴重度 | Mitigation |
|---|---|---|
| **Resolver 複雜度** | 中 | 把 merge logic 集中在 SpecLoader；單元測試覆蓋 add/override/remove 全 case |
| **Override 語意要清晰** | 中 | Schema 規定 `replace_with` = 整個 instruction-def 替換（不允許 partial merge）；想動 cycle 一個欄位也要 declare 整個 instr |
| **Debug 困難** | 低-中 | SpecLoader 提供 `--dump-merged` flag 印出 merged spec；每個 instruction 加 internal `_inherited_from: "i8086"` metadata |
| **Inheritance 濫用** | 中 | Doc 明示 R1-R4 hard rules；schema validation 拒絕跨 family / 跨 word-size 繼承 |
| **Extension chain 過長變難 reason** | 低 | 規定一個 chain 最多 5 顆 CPU（族裡通常也就 3-5 顆，實務不會太長） |
| **早期 over-engineer** | 中 | **加 inheritance 前先把 8086 base 從零寫完**；至少有 2 個 spec 在 chain 才啟動繼承機制 |

### 5.3 跟既有 spec_version 的關係

`spec_version` 是 **schema** 版本（v1 / v2，N4 引入）；
`extends` 是 **CPU 譜系** 關係。兩者正交、互不影響。

---

## 6. 實作 phase 規劃（v2）

| Phase | 內容 | 成果 |
|---|---|---|
| **23.0** | 本 doc 落地（v2 含 Gemini review） | DRAFT v2 → APPROVED |
| **23.1** | 8086/8088 base spec + X86_16Emitters.cs（**沒 inheritance 機制**），抽 `_base_808x.json` + sibling | 8086/8088 兩 build target 通過 Apr86 reference + 經典 8086 demo |
| **23.2** | 加 inheritance + traits 到 SpecLoader + schema | T1 通過；既有 ARM/LR35902/2A03 spec 沒 extends 仍 work；新增 7 個 invariant 驗證 (depth cap / cyclic-detect / ID 必存在於父 / additions 不衝突 / removals 必存在 / traits additive-only / ID 必唯一) |
| **23.3** | 80186 spec — extends i8086 用 partial-merge override + ID-selector | 通過 80186 specific test；merged spec dump 看 cycle 從 118→21 |
| **23.4** | 80286 real-mode spec — extends i80186 | 通過 80286 real-mode test |
| **23.5** | 80286 protected-mode spec — `additions` 加 0x0F escape group | LGDT/LIDT/LLDT/LMSW 等 protected mode opcodes work |
| **23.6** | (可選) 8087 FPU as trait — composition example | `i8086_with_8087.json` 跟 `i8086_no_fpu.json` 兩 build target 共用 base |
| **23.7** | doc 更新 — `MD/design/20-adding-a-new-cpu.md` 加 inheritance + trait 章節 | SOP 完整化 |

**重點**：23.1 先做不繼承的 base + sibling，再回頭加 inheritance（23.2）—
如果 8086 沒寫完，inheritance 機制 design 會憑空想；有實際 base 後再設計
diff / partial-merge 形式才精準。

**Inheritance 啟動條件**：第 2 顆 CPU 開工時（80186）；只有 1 顆 base 不需要
inheritance overhead，但 **sibling pattern (8086/8088) 在 23.1 已啟用**。

---

## 7. Open questions — v2 status

**v2 update**：Q1 / Q5 / Q7 後續經 Gemini review 改答；Q2 / Q3 / Q6 確認；
Q4 仍 open；新增 Q8 (FPU as trait)。

| # | 問題 | v1 傾向 | v2 結論 |
|---|---|---|---|
| 1 | Override 粒度 — 整個 replace 還是 partial merge？ | 整個 replace | **改 partial merge (RFC 7386 JSON Merge Patch)** — Gemini critique #2.A：x86 cycle 跨世代變動但 encoding 不變，整個 replace 太冗 |
| 2 | Multiple inheritance | 不支援 | **不支援** — diamond problem 解不了（需要 mix-in 走 traits） |
| 3 | Chain 長度限制 | 5 | **改 4** — Gemini 警告 deep tree 是 anti-pattern；x86-16 chain depth 已到 4 |
| 4 | Status register bit 增量語法 | added_bits 加 bit / 整體變才 redeclare | 仍 open — 等 80286 NT/IOPL 實際做時再 finalize |
| 5 | Decoder priority of overridden instruction | 同 priority physically replace | **由 ID-keyed dictionary 自然解決** — v2 改用 stable string ID 做 selector，override 等於 dict entry replace，沒 priority 問題 |
| 6 | 雙向繼承（base 知道 child） | 不做 | **不做** — Resolution 單向 |
| 7 | 8086 ↔ 8088 — 合併還是 sibling？ | 合併 | **改 sibling pattern** — Gemini critique #4.c：抽 `_base_808x.json`，兩者各自 extend 並 override cycle timing；比合併精細，cycle accounting 也對 |
| 8 | **(NEW)** FPU 8087 / 80287 — extends or trait？ | — | **走 traits**（additive-only overlay）— 解 N×M combinatorial 爆炸；`spec/cpu/x86-16/i8086_with_8087.json` = base + trait composition |

---

## 8. 結論建議

**走繼承是對的方向**（Gemini review 確認，並補強 7 個關鍵設計修正），
分階段做：

1. **先做 8086/8088 base + sibling abstract (`_base_808x`)**（23.1）— 驗證
   X86_16Emitters sound + sibling 機制不出錯，**還沒開 inheritance**
2. **第 2 顆 (80186) 才 implement inheritance**（23.2）— 此時有真實 base
   對照，schema 設計不會憑空想；同步加 traits 機制
3. **80286 是 inheritance 的真正 stress test** — 同 family 但加 mode
   切換 / 系統 opcode；驗證 protected mode opcode 透過 0x0F escape group
   `additions` 加進來

**v2 額外驗證點**：
- ID-based selector 在 x86 ModR/M opcode extension 場景下解析正確
- partial merge 對 cycle 變動 (118 → 21 → 13) 的 8086→186→286 chain 正
  確產生 merged cycle
- `_inherited_from` / `_overridden_by` provenance 在 `--dump-merged` 看得到
- traits 機制處理 i8086_with_8087 / i8086_no_fpu 兩 build target

最大收益方向：**Intel 16-bit 三顆 ✓ → x86-32 起新 chain ✓ → x86-MMX
/ SSE / x86-64 各自起 chain**（共 5 條）。

最大風險方向：**過早 over-engineer**（base 都還沒寫就先設計 inheritance）
跟 **過度濫用**（強行讓 80386 extends 80286 而非起新 chain）。Hard rules
R1-R4 + 5 條 invariant validation + 4 層 depth cap 應該夠擋。

---

## 9. 跟既有 doc + 諮詢紀錄

- 補強 [`19-declarative-jit-policy.md`](19-declarative-jit-policy.md) — CPU/Machine spec split 之後再加 CPU spec inheritance 第三層
- 對應 [`20-adding-a-new-cpu.md`](20-adding-a-new-cpu.md) — 之後 SOP 加新章節「inheritance scenario」+「trait composition」
- 跟 [`MD/note/framework-future-extensions-and-vision.md`](/MD/note/framework-future-extensions-and-vision.md)
  的「加更多 CPU」方向一致 — 用 inheritance + traits 把邊際成本壓到實際可行

**Review 紀錄**：
- v1 → v2 的 7 個關鍵修正來自 Gemini consultation:
  [`tools/knowledgebase/message/20260509_233848.txt`](/tools/knowledgebase/message/20260509_233848.txt)
- 對照業界 (MAME / QEMU TCG / Ghidra SLEIGH / ArchC) 確認本 doc 走的
  「load-time data overlay」路線是這幾個 reference 都沒做的設計點 —
  保留 declarative spec 的同時不污染 runtime path
