# CPU spec inheritance — JSON 規格的「繼承 + override」機制

> **Status**：**DRAFT**（2026-05-09）— 構想階段，等 review。
> **Trigger**：未來預計做 Intel 16-bit family（8086 / 80186 / 80286）+
> 32-bit family（80386 起）。觀察：同一 family 內 CPU 共通性極高（80%+
> opcode 重複），逐顆從零寫 spec 是浪費；但跨 family（16→32 bit）差
> 異太大，硬繼承反而拖累。
>
> 提議：給 spec 加類似 OO 的 `extends` 機制，**同 family 內**用繼承堆
> 疊；**跨 family 起新 chain**。
>
> **目標讀者**：(a) review 本草稿後決定走 / 不走的人；(b) 真要 implement
> 時的執行者。

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

---

## 2. 設計提議

### 2.1 兩個層級的繼承

| 層級 | 範圍 | 機制 |
|---|---|---|
| **Family-internal** | 同 word size + 同 register file shape + 同 endianness | `extends: "<parent-cpu-id>"` 自動 merge |
| **Family-cross** | 不同 word size / register file 形狀 | **不繼承**；起新 spec/<family-32>/ chain |

### 2.2 Schema 草案 — `extends` field

```json
// spec/x86-16/i8086/cpu.json — base，沒 extends
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
    "instruction_sets": { ... }
}

// spec/x86-16/i80186/cpu.json — extends i8086
{
    "$schema": "../../schema/cpu-spec.schema.json",
    "spec_version": "1.0",
    "architecture": {
        "id": "i80186",
        "family": "x86-16",
        "extends": "i8086",                 // ← 新 field
        "endianness": "little",
        "word_size_bits": 16
    },
    // register_file 省略 — inherits from parent
    // instruction_sets 用 diff 形式：
    "instruction_set_diff": {
        "Main": {
            "added_groups": [
                "$include:groups/i80186-additions.json"
            ],
            "added_instructions": [
                { "selector": {"opcode_byte": "0xC8"}, "mnemonic": "ENTER", ... }
            ],
            "overridden_instructions": [
                {
                    "selector": {"opcode_byte": "0x69"},
                    "from": "i8086",          // 自証式：明示來源便於追蹤
                    "replace_with": {
                        "mnemonic": "IMUL",   // 8086 沒這個 r/imm form
                        "cycles": "..."
                    }
                }
            ],
            "removed_instructions": [
                { "selector": {"opcode_byte": "0xD6"} }   // SALC undefined in 80186
            ]
        }
    }
}

// spec/x86-16/i80286/cpu.json — extends i80186
{
    "architecture": {
        "id": "i80286",
        "family": "x86-16",
        "extends": "i80186",                // chain: i80286 → i80186 → i8086
        ...
    },
    "instruction_set_diff": {
        "Main": {
            "added_groups": [
                "$include:groups/i80286-protmode.json"  // 0x0F escape opcodes
            ],
            ...
        }
    }
}
```

### 2.3 Resolution algorithm（at SpecLoader time）

```
load(spec_path):
    raw = parse_json(spec_path)
    parent_id = raw.architecture.extends
    if parent_id is None:
        return finalize(raw)            # base spec
    parent_spec = load(locate(parent_id))   # recursive
    merged = deep_copy(parent_spec)
    apply_diff(merged, raw.instruction_set_diff)
    merged.architecture = raw.architecture   # child's own id/extends preserved
    if raw.register_file: merged.register_file = raw.register_file   # 罕見，需才覆蓋
    return finalize(merged)

apply_diff(target, diff):
    for set_name, set_diff in diff.items():
        target_set = target.instruction_sets[set_name]
        for group in set_diff.added_groups:
            target_set.add_group(load_group(group))
        for instr in set_diff.added_instructions:
            target_set.add_instruction(instr)
        for ovr in set_diff.overridden_instructions:
            match = target_set.find_by_selector(ovr.selector)
            assert match is not None, f"override target not found: {ovr.selector}"
            target_set.replace(match, ovr.replace_with)
        for rem in set_diff.removed_instructions:
            target_set.remove_by_selector(rem.selector)
```

**完成後**：merged spec 跟「從零寫的 i80286 完整 spec」**等價** — 後續
SpecCompiler / DecoderTable / IR emit pipeline 完全不知道 inheritance
存在。Inheritance 只發生在 load time、不影響 runtime。

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

---

## 3. 具體例子：Intel 16-bit family 完整移植路徑

```
sprint A: 寫 8086 base spec + X86_16Emitters.cs
  └── spec/x86-16/i8086/cpu.json + groups/*.json (~3000 lines)
  └── X86_16Emitters.cs (~1500 lines, ModR/M + segment 處理)
  → 通過 8086 test ROM (e.g. Apr86 既有測試 + 經典 8086 demo)

sprint B: 寫 80186 spec — 純 diff
  └── spec/x86-16/i80186/cpu.json (~150 lines diff)
  └── spec/x86-16/i80186/groups/i80186-additions.json (~200 lines, 12 new ops)
  └── X86_16Emitters.cs += 12 個 new emitter (ENTER / LEAVE / IMUL r,imm / 等)
  → 通過 80186 specific test

sprint C: 寫 80286 spec — 純 diff
  └── spec/x86-16/i80286/cpu.json (~250 lines diff)
  └── spec/x86-16/i80286/groups/i80286-protmode.json (~300 lines, 15 new ops + 0x0F escape group)
  └── X86_16Emitters.cs += 15 個 new emitter (LGDT / LIDT / LLDT / LMSW / 等)
  → 通過 80286 real-mode + protected-mode test
```

**每個 sprint 的 diff 體量**：80186 ~350 行，80286 ~550 行。對比沒繼承
要 3000+3000+3000 = 9000 行，**省掉 ~85%**。

---

## 4. 何時該斷開繼承（Hard rules）

| 規則 | 例子 | 理由 |
|---|---|---|
| **R1: word size 變動** | 16→32 (8086→80386), 32→64 (i486→x86-64) | EAX vs AX 雙視角 / R8-R15 / long mode / 16-bit-only opcode 失效 — 變動超過 inheritance 能 carry |
| **R2: register file 形狀變動** | x86 → x86+MMX (加 MM0-MM7), → x86+SSE (加 XMM0-XMM7) | RegisterFile shape 變等於 parent 整段 invalid |
| **R3: 主要 prefix-encoding scheme 變動** | x86-32 加 0x66 / 0x67 size override；x86-64 加 REX prefix | decoder 結構性變動，繼承後 base 跟 child 解碼路徑不一致 |
| **R4: 跨 family / 廠商 / endianness** | x86 ↔ ARM；little ↔ big endian | 完全不同 ISA |

**斷開 = 起新 spec/<new-family>/ 目錄 + 新 emitter library**。例如：
- spec/x86-16/ 收 8086/80186/80286
- spec/x86-32/ 收 80386/80486/Pentium（**不 extend** 80286）
- spec/x86-64/ 收 x86-64 系（不 extend Pentium）

### 4.1 灰色案例 — 80286 protected mode

80286 的 protected mode 加了 LGDT / LIDT / LLDT / LMSW 等系統 opcode +
新 mode（real / protected），但 **real mode 跟 80186 完全一樣**。
判斷：**繼承**（real-mode 80%+ 共通；protected mode 是 mode 切換的 add-on，
不是 base ISA 改寫）。

### 4.2 灰色案例 — 80386 真的能繼承 80286 嗎？

技術上可以（real mode 部分 80286 → 80386 確實 95% 兼容），但 **不該**：
- 80386 的 protected mode 32-bit operand size 變動，0x66 prefix 反轉
  default size — 不是 add，是 override 既有 default
- 32-bit addressing modes 加 SIB byte，整個 ModR/M 解碼路徑分支
- 新增 EAX/EBX/etc 32-bit 視角是 register file shape 變動

正確判斷：**80386 起新 chain spec/x86-32/i80386**。

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

## 6. 實作 phase 規劃

| Phase | 內容 | 成果 |
|---|---|---|
| **23.0** | 本 doc 落地 | DRAFT → APPROVED |
| **23.1** | 8086 base spec + X86_16Emitters.cs（**沒 inheritance 機制**） | 8086 通過 reference test ROM |
| **23.2** | 加 inheritance 到 SpecLoader + schema | 走 spec_compile pipeline 把 child spec 跟 parent merge；單元測試 |
| **23.3** | 80186 spec — 用 diff 形式 | 通過 80186 specific test |
| **23.4** | 80286 real-mode spec | 通過 80286 real-mode test |
| **23.5** | (可選) 80286 protected-mode spec | protected mode opcodes work |
| **23.6** | doc 更新 — `MD/design/20-adding-a-new-cpu.md` 加 inheritance 章節 | SOP 完整化 |

**重點**：23.1 先做不繼承的 base，再回頭加 inheritance（23.2）— 如果
8086 沒寫完，inheritance 機制 design 會憑空想；有實際 base 後再設計
diff 形式才精準。

**Inheritance 啟動條件**：第 2 顆 CPU 開工時；只有 1 顆 base 不需要
inheritance overhead。

---

## 7. Open questions（討論用）

1. **Override 粒度**：要支援「只覆蓋 cycle 一個欄位」嗎？還是強制
   `replace_with` 整個 instruction-def？
   - 個人傾向：強制整個 — 簡單、explicit、避免 deep merge 邊角
2. **Multiple inheritance**：80286 能同時 extend 80186 跟（假想的）
   80288 嗎？
   - 個人傾向：**不支援**。CPU 譜系是 linear；交叉繼承在 hardware 譜系上
     不存在
3. **Chain 長度限制**：硬 cap 在多少？
   - 個人傾向：**5 顆**。實務上沒族裡需要更多
4. **Status register bit 增量**：80286 加 NT/IOPL bit 到 FLAGS — 用
   `status_register_diff.added_bits` 還是 child 整個重 declare FLAGS？
   - 個人傾向：**新增 bit 用 added_bits**（保 inheritance 含意）；
     **整體位元 layout 變才 redeclare**
5. **Decoder priority**：override 後的 instruction，在 decoder lookup 時
   priority 跟 base 比？
   - 個人傾向：**跟 base 同 priority**（physically 取代 base 那條 entry）；
     避免 child override 排到 base 之後反而沒 hit 到
6. **要不要做雙向繼承**（base reference child 來知道哪些被 override）？
   - 個人傾向：**不做**。Resolution 是單向 child 拉 parent；base 不該
     知道 child 存在
7. **8086 ↔ 8088 一對，要走繼承還是 sibling**？
   - 兩者 ISA 完全相同，差別只在 8-bit vs 16-bit 外部 bus（emulator 級別
     一般忽略）
   - 個人傾向：**單一 8086/8088 共用 spec**（spec id "i8086_8088"）；不
     用繼承，沒實質差異

---

## 8. 結論建議

**走繼承是對的方向**，但分階段做：

1. **先做 8086 base（23.1）**，驗證 X86_16Emitters 是否 sound — 不
   touch inheritance
2. **真要加第 2 顆 (80186) 時才 implement inheritance**（23.2）— 此時
   設計有 base 對照、不會憑空想
3. **80286 是 inheritance 的真正 stress test** — 同 family 但加 mode
   切換 / 系統 opcode

最大收益方向：**Intel 16-bit 三顆 ✓ → x86-32 起新 chain ✓**。

最大風險方向：**過早 over-engineer**（base 都還沒寫就先設計 inheritance）
跟 **過度濫用**（強行讓 80386 extends 80286 而非起新 chain）。Hard rules
R1-R4 + schema validation 應該夠擋。

---

## 9. 跟既有 doc 的關係

- 補強 [`19-declarative-jit-policy.md`](19-declarative-jit-policy.md) — CPU/Machine spec split 之後再加 CPU spec inheritance 第三層
- 對應 [`20-adding-a-new-cpu.md`](20-adding-a-new-cpu.md) — 之後 SOP 加新章節「inheritance scenario」
- 跟 [`MD/note/framework-future-extensions-and-vision.md`](/MD/note/framework-future-extensions-and-vision.md)
  的「加更多 CPU」方向一致 — 用 inheritance 把邊際成本壓到實際可行
