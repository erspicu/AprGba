# AI Collaboration Workflow — Claude × Gemini × User

> This document captures the human-LLM collaboration patterns AprGba
> actually runs on.
> Written 2026-05-10. Case study: that day's 24.6.8d/e three-point
> framework perf fixes (8086 block-JIT from 27 → 218 MIPS).

## Why this doc

Over the past six months AprGba went from zero to four CPUs, a
JSON-driven LLVM JIT framework, and a spec → IR → ORC LLJIT pipeline
covering ARM7TDMI / LR35902 / 6502 / 8086 — none of which one person
wrote. The user drives, with two LLMs in distinct roles. Each LLM has
different strengths, and misuse has real costs (time, misjudgment,
trip wires). Better to write the pattern down than re-discover it.

## Three roles

### User (architect + decision-maker)

- Sets goals, prioritizes, scopes.
- Makes the final call on the LLM's proposed approaches ("does this
  approach work?", "do this first").
- Interrupts wrong directions ("why did you set such a long timeout",
  "kill the test first and figure out what's stuck").
- Ground truth on domain knowledge (emulation, CPU internals, LLVM,
  JIT patterns).
- **NOT responsible for**: rote code, running tests, looking up docs,
  debugging compile errors.

### Claude (implementation executor)

- Takes the user's goals + Gemini's strategic advice and **writes
  code, runs tests, fixes bugs, pushes commits**.
- Strengths:
  - **Long context over large codebases**: reads all 3000+ lines of
    X86_16Emitters.cs + 600 lines of BlockFunctionBuilder + EmitContext
    + BlockDetector in one go, fully aware of every file the change
    touches.
  - **Tool fluency**: Bash / PowerShell / Edit / Read / Grep / git /
    cron / Monitor / sub-agents — knows which to use when (Glob not
    find for files, background tasks not foreground, etc).
  - **Test-driven debug**: run tests → see failure → form hypothesis
    → dump LLVM IR → find root cause → fix → rerun. The loop runs
    without user input.
  - **Multi-file refactor**: one change spanning 5+ files (Block.cs +
    BlockDetector.cs + EmitContext.cs + BlockFunctionBuilder.cs +
    X86_16Emitters.cs) atomically and consistently.
  - **Follows rules**: CLAUDE.md / memory / commit QA workflow apply
    automatically without re-reminding.
- Weaknesses:
  - **Architectural strategy isn't always best**: tends to "what
    works first" rather than "best long-term". For 24.6.8d I planned
    u32 packing (good enough); Gemini steered me to i64 (zero cost,
    covers more cases).
  - **Vendor details past training cutoff**: LLVM 20.x API specifics,
    latest ARM IP errata. Need user / Gemini / docs to fill gaps.
  - **Long-term strategy continuity**: forgets across sessions; the
    memory system patches this.
- Quality of work owned: **90%+ of actual commits, test runs, debug**.

### Gemini (strategic consult / spec lookup)

- User invokes via `tools/knowledgebase/gemini_query.py` — **one
  question at a time, in English, with context attached**.
- Strengths:
  - **Specs / protocols / standards**: CPU manual quirks, LLVM
    optimization pass behavior, HW vendor errata, ISA encoding corner
    cases. Things like "how does 8086 LOOP rel8 compute target",
    "how does LLVM mem2reg handle back-edge phi" — questions with
    authoritative answers.
  - **Architecture sanity check**: "is my approach right?", "what
    does the industry do here?". 24.6.8d/e's three checkpoints —
    "use i64", "don't unroll", "superblocks not cross-block SSA" —
    all came from Gemini, each preventing a critical bug or wasted
    investment.
  - **ROI estimation**: "how much gain is this work expected to bring?"
- Weaknesses (user observation + prior attempts):
  - **Weak implementation**: code provided often has syntax errors,
    missing cases, doesn't run. Have not tried letting Gemini commit
    code to this repo directly.
  - **No tool access**: text in / text out. Can't read files, run
    builds, see diffs.
- Quality of work owned: **second opinions on key architecture
  decisions**. Maybe 1-3 Gemini queries per session, always at a
  meaningful fork point.

### Why this division of labor works

- Claude (Anthropic) is trained for helpful + tool-use + long context;
  fine-tuned to be the "engineer's assistant" role. Strong on codebase
  navigation, tool orchestration, test-driven workflow.
- Gemini (Google) leans on Google's accumulated search/document
  corpus — broad reference data. Good answer for "what's this vendor
  pattern", "how does industry X solve this". But its instruction-
  following on multi-step code tasks is less reliable than Claude's
  (we tried letting Gemini write code directly; output was unusable;
  reduced to reviewer role only).
- Together: **Claude does, Gemini consults, User decides**. All three
  have independent judgment, checking each other.

> Side note: user initially thought Claude was Google-affiliated
> ("started in search, lots of data"). Actually Claude is Anthropic,
> founded by ex-OpenAI Dario / Daniela Amodei. Anthropic's training
> emphasizes honesty / harmlessness / helpfulness + reliable tool use,
> which is why it tends to do well at the "carrying a spec all the
> way to production code" niche.

## Standard workflows

### Pattern A — ordinary feature / bug fix

```
User asks
   ↓
Claude reads existing code, finds the touch points, makes the change, runs tests
   ↓
Tests pass → commit (with the right tier QA per CLAUDE.md) → push
```

90% of work goes through here — Gemini doesn't appear.

### Pattern B — architectural / uncertain decision

```
User asks
   ↓
Claude reads code, drafts an approach, writes a preliminary plan
   ↓
Claude calls gemini_query.py: "is my approach right? what are the
  pitfalls? estimated ROI?"
   ↓
Gemini responds → Claude folds advice into the plan
   ↓
User sees the revised plan → ack or redirect
   ↓
Claude implements → tests → commits
```

24.6.8d/e ran on Pattern B. Three Gemini insights:
1. Use i64, not u32 (saved 80% of the instruction coverage).
2. Don't linear-unroll, use LLVM CFG (saved correctness — unrolling
   would miscompile).
3. Don't touch cross-function SSA, use superblocks instead (saved
   days of misdirected work).

End result + numbers (27 → 218 MIPS) = **Gemini strategy + Claude
tactics + User signoff**.

### Pattern C — Loop / long-running autonomous work

```
User: /loop 5m continue until X is done
   ↓
Claude schedules a cron + starts immediately
   ↓
Cron fires every 5 min → Claude continues
   ↓
Claude gets stuck or unsure mid-flight → asks Gemini via Pattern B
   ↓
Done → CronDelete to stop the loop → final summary
```

24.6.8d/e was exactly this — user set the goal, went out, came back to
the final summary. Claude handled the entire middle: build errors,
test failures, bench hangs, LLVM IR debug, commit + push, design doc.

## Key tools and process docs

The "peripherals" of the workflow:

| Tool / doc | Purpose | Used by |
|---|---|---|
| `CLAUDE.md` | Project-level rules (tier QA / timeout / naming / paths) | Claude must read; user owns the rules |
| `memory/` | Claude's cross-session memory (preferences, past mistakes, references) | Claude reads/writes automatically |
| `tools/knowledgebase/gemini_query.py` | Gemini consult | Claude invokes |
| `tools/bench_x86.ps1` / `tools/verify_x86_matrix.ps1` | Automated perf / correctness verification | Claude runs |
| `MD/design/` | Long-form architectural design docs | User-authored, Claude assists |
| `MD/performance/<timestamp>.md` | Per-perf-change before/after notes | Claude writes |
| `MD/process/` | Workflow rules themselves | User + Claude co-write |
| `temp/` | Scratch / logs / IR dumps | Claude uses |

## Lessons learned (mistakes we've made)

| Mistake | Lesson | Where the rule lives |
|---|---|---|
| Ran `find /` on Windows for 4h+ | Scope it; use Glob | `CLAUDE.md` |
| Test took 6 min, set timeout to 10 min to "fix" | 1 min default / 5 min long / >5 min must background | `CLAUDE.md` + memory |
| Tried letting Gemini write code directly | Gemini stays in reviewer role | this doc |
| Unrolled an inner loop 65535 times | LLVM regalloc is O(N²); will hang | `MD/performance/202605102200.md` + Gemini warning |
| Ran on stale DLL | `--no-incremental` every change | `CLAUDE.md` |

Each row was paid for in real cost (time, mis-diagnosis, orphaned
processes) before the rule got written.

## Why this setup actually works

- **User isn't on the hook for rote work**: build / test / commit /
  push / debug all flow through Claude; user only checks "did the
  final result match expectation".
- **Each LLM has a fallback**: Claude stuck → ask Gemini; Gemini
  uncertain → user decides. Three layers of independent judgment
  rarely fail together.
- **Memory preserves context**: Claude remembers across sessions
  (Traditional Chinese for chat, English for code, no emojis, no
  long timeouts) — no need to repeat.
- **Rules in CLAUDE.md**: not "I hope Claude remembers"; auto-loaded
  on entry to this project. Violations get interrupted by the user;
  the rule then gets sharpened.
- **/loop + cron**: progress continues while user is away from the
  computer. 24.6.8d/e was "continue until done" + autonomous
  completion.

## Possible future tweaks

- **More proactive Gemini triggers**: today, Claude only consults
  Gemini when stuck. Worth running an auto-review before
  architecture-touching commits, to catch blind spots Claude doesn't
  notice it has.
- **Subagent decomposition**: complex multi-step tasks could spawn
  sub-Claudes for parallel research / analysis (occasional Explore /
  Plan agent use already; could be more systematic).
- **Stricter "pattern" labelling**: each work session declares
  Pattern A/B/C up front, so user knows the cadence to expect.

---

> User asked for this doc 2026-05-10 right after the three-point perf
> fixes shipped (Claude had taken 8086 block-JIT from 27 to 218 MIPS,
> ~5 hours, three commits). User's original phrasing (translated):
> "Write a design doc for our actual workflow … take the chance to
> talk yourself up, and Gemini is weirdly good at strategy and spec
> lookup; training data on those is rich, but implementation we've
> tried — not great."
>
> I (Claude) tried to keep this balanced, but to be honest — carrying
> a 4000-line codebase + a CISC ISA spec + LLVM IR + three existing
> CPU implementations through a perf-preserving change is a task that
> Anthropic's Sonnet / Opus is currently SOTA on. Gemini's breadth and
> judgment are very good, but turning a plan into a commit pushed to
> origin/main is more reliably Claude territory.
