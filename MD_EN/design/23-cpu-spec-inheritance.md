# CPU spec inheritance — JSON spec inheritance + override mechanism

> **Status**: **DRAFT v2** (end of 2026-05-09, after Gemini review).
> **Review record**: `tools/knowledgebase/message/20260509_233848.txt`
>
> **Trigger**: planning Intel 16-bit family (8086 / 80186 / 80286) +
> 32-bit family (80386 onward). Observation: within one family,
> CPUs are highly common (80%+ opcode reuse) — writing each spec
> from scratch is wasteful; but cross-family (16→32 bit) differences
> are too large for hard inheritance to help, and would actively hurt.
>
> Proposal: give the spec a **"Data-Driven Overlay/Mixin"** mechanism
> (not OOP runtime inheritance — a build/load-time AST macro system).
> Within a family, stack via `extends`; cross-family, start a new
> chain; optional capability modules (FPU, SSE, etc.) go through
> additive-only `traits` to avoid combinatorial explosion.
>
> **v2 main changes** (absorbing Gemini's critique):
> 1. Override changed to **partial merge (RFC 7386)**, not whole-replace
>    (x86 cycles change a lot across generations but encoding doesn't).
> 2. Selector changed to **stable string ID** instead of `opcode_byte`
>    (x86 ModR/M opcode extensions share a byte with multiple mnemonics).
> 3. **`from` field is hard validation** (parent must have that ID or fail-load).
> 4. Added **`traits` / additive-only overlays** for FPU and other
>    optional extensions.
> 5. 8086/8088 use **sibling pattern** (extract a shared `_base_808x.json`)
>    instead of merging into a single spec.
> 6. Full Intel chain split-points listed (16-bit / 32-bit / MMX / SSE
>    / x86-64 five chains).
> 7. Inheritance depth **hard cap = 4** (Gemini's warning that deep
>    trees are an anti-pattern).
>
> **Target readers**: (a) decision-makers post-review (go/no-go); (b)
> implementers when execution starts.

---

## 1. Motivation

### 1.1 Observation — Intel 16-bit family commonality

| CPU | Year | Δ vs predecessor | Commonality |
|---|---|---|---|
| 8086 / 8088 | 1978 | base | 100% |
| 80186 / 80188 | 1982 | +12 opcodes (PUSH imm / IMUL imm / ENTER / LEAVE / INS / OUTS / BOUND / immediate-shift / etc.) | ~96% (12/256) |
| 80286 | 1982 | +15 opcodes (LGDT / LIDT / LLDT / LMSW / SMSW / ARPL / VERR / VERW / etc., protected-mode plumbing) | ~94% (15/256, partial 0x0F escape coverage) |

**~94-96% identical** — meaning every new CPU brings ~3000 lines of
emitter + spec, of which 2800+ are copy-paste from the previous one.
**This is the inverse of framework genericity — not just zero
leverage, but actively bad for maintenance** (one opcode bug needs
fixing 3 times).

### 1.2 Observation — 32/64-bit should break the chain

| Boundary | Why break |
|---|---|
| 16→32 bit (80386) | EAX/AX dual view; 32-bit addressing modes (SIB byte); new prefix bytes (0x66/0x67 size override) — beyond "add opcode" level changes |
| 32→64 bit (x86-64) | RAX/EAX/AX triple view; REX prefix; R8-R15; long mode; instruction encoding shifts in multiple dimensions |
| MMX / SSE / AVX | Register file shape changes (XMM/YMM/ZMM new file); inheritance doesn't handle file-structure mutation |

**Inheritance's value is in "opcode add/remove + detail tweaks";**
forcing inheritance through architectural changes blows up the base
spec and complicates logic beyond the gain.

### 1.3 N0–N11 framework abstractions already in place

Earlier sprints have split the spec into `CpuSpec` (ISA) +
`MachineSpec` (board) layers (doc #19). **Inheritance is a third
layer of spec organization** — the level hierarchy (base → derived)
inside `CpuSpec`.

### 1.4 Positioning vs industry

| Project | Mechanism | Main pain point |
|---|---|---|
| **MAME** | C++ OOP inheritance (device_t) | ISA execution degrades to macros / large switch + `if (has_feature_X)` |
| **QEMU TCG** | Single-directory monolithic + runtime feature flag | No declarative inheritance; CPUID checked in translation loop dynamically |
| **Ghidra SLEIGH** | Preprocessor `@include` + constructor override | include-chain spaghetti; data trace difficult |
| **ArchC** | Pure C++ inheritance | Same problem as MAME |
| **AprCpu (this doc)** | JSON Patch / kustomize-like + load-time merge | Runtime sees no hierarchy; JIT / decoder unchanged |

**Core positioning**: this doc isn't inventing OOP runtime
inheritance, it's **build/load-time data overlay**. After SpecLoader
finishes merging, SpecCompiler / DecoderTable / runtime are
completely unaware of inheritance — equivalent to existing spec path.

---

## 2. Design proposal

### 2.1 Two levels of inheritance

```
spec/cpu/
├── _schema.json
├── x86-16/
│   ├── i8086/cpu.json          (base for 16-bit family)
│   ├── i80186/cpu.json         (extends i8086)
│   └── i80286/cpu.json         (extends i80186, depth 3)
├── x86-32/                     (separate chain — Phase 28+)
│   └── i80386/cpu.json
└── ...
```

**Within a family**: stack via `extends`. Each level is a JSON Merge
Patch (RFC 7386) over the parent's resolved spec.

**Cross-family**: start a new chain. No inheritance link from
x86-32/i80386 to x86-16/i80286 — even though some opcode encodings
overlap, the architectural deltas (32-bit registers, SIB byte, REX
prefix later) make a clean break clearer than a forced inheritance
shim.

### 2.2 RFC 7386 JSON Merge Patch semantics

Override files use:
- `null` value → remove the key
- non-null value → replace the key
- nested object → recursively merge

This is more conservative than full JSON Patch (no array-element
operations) but matches the actual semantic operations needed:
add an instruction, remove an instruction, override a field on
an existing instruction.

### 2.3 Stable string IDs as selectors

```json
{
  "instruction_set_diff": {
    "Main": {
      "additions": [...],
      "overrides": {
        "ADD_rm8_r8": { "cycles": { "form": "1" } }
      },
      "removals": ["BOUND_r16_m32"]
    }
  }
}
```

Override / removal lookup uses the instruction's `id` field, not its
opcode byte. ModR/M opcode extensions share opcode bytes (e.g. the
80/81/82/83 group encodes 8 different mnemonics in `reg` field), so
opcode-byte selectors would be ambiguous.

`from` field is required hard validation: if the parent doesn't have
the named ID, the load fails. Prevents typo-induced silent merges.

### 2.4 Capability traits (additive-only overlays)

For optional features (FPU, MMX, SSE) where multiple combinations
exist (8087-only, 80287, 80387, ...) and some may apply across
multiple base CPUs, traits are **additive-only**:

```json
{
  "traits": ["fpu/8087", "mmx/v1"]
}
```

A trait file can only `additions` — it cannot override or remove
parent state. This avoids the combinatorial complexity of
multi-inheritance with conflict resolution.

### 2.5 Sibling pattern for near-duplicates

8086 and 8088 are nearly identical (8088 differs only in 8-bit data
bus, affecting cycle counts on some opcodes). Both extend a shared
`_base_808x.json`:

```
spec/cpu/x86-16/
├── _base_808x/cpu.json       (private, not loadable directly)
├── i8086/cpu.json            (extends _base_808x)
└── i8088/cpu.json            (extends _base_808x)
```

This avoids treating either as "primary" with the other as a hack.

### 2.6 Inheritance depth hard cap

Cap = 4. Per Gemini's review: deeper trees become anti-patterns
(hard to trace which level a given instruction came from, easy to
introduce silent overrides three levels apart that conflict).

---

## 3. Cross-family chain splits — Intel example

| Chain | Members | Why this chain |
|---|---|---|
| x86-16 | 8086, 8088, 80186, 80188, 80286 | All real-mode + 286 protected-mode |
| x86-32 | 80386, 80486, Pentium, P6 | 32-bit registers, SIB, paging |
| MMX | (trait) | Register-file extension, applied via trait |
| SSE | (trait) | XMM register file, applied via trait |
| x86-64 | AMD64 / Intel 64 | REX prefix, R8–R15, long mode |

Five chains. Each is a clean inheritance line; the trait mechanism
handles cross-cutting capabilities like MMX (which can apply to
late P5 or P6 CPUs equivalently).

---

## 4. Implementation summary

> See `MD_EN/design/25-i80186-implementation-plan.md` for the actual
> Sprint 25.1–25.7 implementation, and
> `MD_EN/performance/202605102300-i80186-baseline.md` for the
> closure note showing the mechanism shipped at zero runtime cost
> (i80186 perf == i8086 perf on shared workloads).

Key shipped components:
- `JsonMergePatch.cs` — RFC 7386 implementation
- `SpecLoader.LoadCpuSpecInternal` — recursively resolves parent + applies diff
- `ApplyDiffToSet` — additions / overrides / removals
- Cycle detection + depth cap (4) + provenance metadata
- 21/21 unit tests covering merge primitives end-to-end

Validated chains shipped:
- depth 1: i8086, ARM7TDMI, Ricoh 2A03, LR35902 (no inheritance)
- depth 2: i80186 (extends i8086), Phase 25
- depth 3: i80286 (extends i80186 extends i8086), Phase 26 + Phase 27a + Phase 27b

---

> The original Chinese version at `MD/design/23-cpu-spec-inheritance.md`
> contains additional detailed sections covering: full schema
> additions (`extends`/`extends_path`/`from`/`overrides`/`removals`/
> `traits` / additive-only fields), example x86 cycle override case
> studies, validation rules, error-message formats, and edge cases
> like multiple inheritance interactions. Refer to the Chinese
> original for those design details.
