# Feasibility Analysis

## TL;DR

**The core idea is feasible and the technical choices are sound, but several underestimated risks must be confronted before starting.**

The path of JSON-driven CPU spec + LLVM JIT has precedents in both academia and industry (Ghidra SLEIGH, QEMU TCG/decodetree, Sail, Ryujinx ARMeilleure, etc.) — it's not vapor. However, the early Gemini conversations were overly optimistic about LLVM. In practice, LLVM is not a "throw it all in and you're done" silver bullet. Item-by-item assessment below.

---

## ✅ Highly Feasible Parts

### 1. JSON Data-Driven CPU Spec

- **Precedents**: Ghidra **SLEIGH**, Cambridge **Sail**, **ArchC**, QEMU **decodetree** all do similar things
- **ARM7TDMI suitability**: high. RISC, fixed-width, regular encoding; ~10 encoding formats cover 90% of instructions
- **Encoding-based description**: correct framing (don't enumerate opcodes, describe formats)

### 2. C# + LLVMSharp Integration

- ✅ **Validated** (Phase 0 complete): the **LLVMSharp.Interop 20.x + libLLVM 20 +
  .NET 10** combination runs cleanly on Windows 11. CLI `aprcpu --jit-only` and
  `aprcpu --spec ... --output ...` are both stable.
- Subsequent Phase 2.5 produced LLVM IR for the full ARM7TDMI ISA on top of this
  combination, with 159 tests green and `Verify()` reporting 0 diagnostics —
  proving the stack remains trustworthy as implementation scale grows.

### 3. Sufficient ARM7TDMI Spec Resources

- Official ARM ARMv4T Reference Manual and ARM7TDMI Datasheet are complete
- **GBATEK** (community-maintained full GBA spec) is extremely detailed
- Existing open-source implementations (mGBA, VBA-M, NanoBoyAdvance) available for cross-checking

### 4. Test ROMs Available

- `arm.gba` / `thumb.gba` (armwrestler)
- FuzzARM, mGBA test suite
- mGBA / NanoBoyAdvance can serve as behavioral reference for diff comparisons

### 5. GBA Performance Bar Is Low

- CPU is only 16.78 MHz
- Even a pure interpreter runs many times faster than realtime on modern machines
- **JIT is purely for "learning and elegance"**, not "to make it run" — important psychological framing

---

## ⚠️ Underestimated Risks

### 1. LLVM Compile Time May Hurt the Game Experience (biggest risk)

**The Gemini conversations did not stress this enough.**

- LLVM was designed for AOT C++ compilation, and every pass is heavy
- GBA basic blocks are typically very short (10–30 instructions); LLVM may take milliseconds to compile one block
- During gameplay you continually encounter new blocks, producing **stutter**
- **Why Ryujinx doesn't use LLVM**: they tried; compile was too slow, so they wrote ARMeilleure (lightweight IR + fast codegen)
- **Why Dolphin doesn't use LLVM JIT**: same reason; the LLVM-IL backend was removed at some point

**Mitigations**:
- Use LLVM `-O0` or `-O1`, not `-O3`
- Only upgrade OptLevel for hot blocks (tiered compilation)
- Accept occasional stutter (this project is research/learning-oriented; tolerable)
- Fallback: switch to .NET `System.Reflection.Emit` / `DynamicMethod` IL JIT — performance is also enough for GBA

### 2. Self-Modifying Code (SMC) and Cache Invalidation

Some GBA games copy ROM code to IWRAM and execute it there, sometimes overwriting in-flight:
- Must intercept "writes to already-compiled regions" → invalidate cache
- The intercept mechanism itself has a perf cost (write barrier)
- **Must be treated as a core problem during planning**, not a day-2 afterthought

### 3. Indirect Branch Dispatch

- `BX R0`, `MOV PC, Rx`, function pointer calls: target unknown at compile time
- Need a block lookup table + a mechanism to exit JIT back to host
- A bad design ends up with every block returning to host for a lookup, eating up the JIT win
- Advanced optimization: block linking (patch native call to jump directly to the next block)

### 4. PPU Has a Floor Even When Simplified

"Framebuffer validation" sounds simple but still requires:
- VRAM write interception (CPU writes VRAM → mark dirty or trigger update)
- Basic LCD timing concepts (VBlank / HBlank interrupts) — most ROMs need VBlank IRQ to proceed at boot
- DISPCNT, DISPSTAT, VCOUNT registers
- "Minimum validation" scope must be clearly bounded (see Phase 8)

### 5. JSON Description Power Has a Ceiling

- ARM Barrel Shifter, LDM/STM register lists, PSR transfer hidden in the Data Processing encoding space, rotated reads on unaligned access… all edge cases
- Where JSON expressiveness falls short, you fall back to "hardcoded special logic in micro-ops"
- The result may end up "JSON for most parts, but a few instructions still need C# handlers"
- **This is not failure**, but be psychologically prepared: 100% pure JSON is unrealistic

### 6. Effort Estimation Is Underestimated

Reference scales of existing projects:
- **mGBA**: ~10 years, pure interpreter, team-maintained
- **NanoBoyAdvance**: ~3-4 years, cycle-accurate, single lead
- **ARMeilleure**: years of iteration by the Ryujinx team

This project layers **JSON framework + LLVM JIT + GBA emulation** — three things stacked. Reasonable single-person hobby progress estimate:
- **6 months**: simple ARM test ROMs, basic framebuffer
- **12-18 months**: mainstream commercial GBA ROMs reaching gameplay screens
- **>2 years**: high compatibility, no significant bugs

Either accept long-term investment, or scale the goal down.

### 7. LLVMSharp Maintenance Uncertainty — ✅ Mitigated

- Pinned to **LLVMSharp.Interop 20.x + libLLVM.runtime.win-x64 20.x**
- Phases 0/1/2/2.5 all ran stably without binding bugs
- Fallback (direct P/Invoke libLLVM or switch to ClangSharp) not currently needed

---

## Strategic Options

### Plan A: Full Goal (ideal but long)
Follow the roadmap fully; expect 12–18 months to see GBA games running.

### Plan B: Phased Deliverables ✅ **In use**

1. **Stage 1** (~3 months): JSON → LLVM IR CLI tool usable standalone ✅ done (faster than expected)
2. **Stage 2** (~6 months): wire to .NET interpreter to pass ARM test ROMs (no JIT yet) — in progress (Phase 3)
3. **Stage 3** (~12 months): bring up LLVM JIT, run GBA homebrew
4. Each stage is independently demoable / open-sourceable, sustaining morale

> **Update (2026-05)**: Phases 0/1/2/2.5/2.6 are complete, considerably faster
> than the original Plan B estimate. Phase 2.5 wrote the full ARM7TDMI ISA into
> spec and passed 159 tests. Next, after Phase 3 (host runtime + interpreter),
> Phase 4.5 will validate the framework's CPU-swappability with GB LR35902.

### Plan C: Research-Oriented (lowest risk)
Do only "ARM7TDMI JSON spec + LLVM IR generator" CLI tool, ship as a paper / blog / open-source tool. No full emulator.

---

## Questions to Answer Before Starting

1. **LLVM version pinning**: LLVM 17 or 18? Which LLVMSharp version?
2. **JIT fallback plan**: if LLVM compile is too slow, would switching to IL JIT be acceptable?
3. **Schedule expectation**: can you accept 12–18 months before seeing a GBA picture?
4. **Goal clarity**: a full GBA emulator, or a research-style CPU spec tool?

---

## Conclusion

The idea is fundamentally feasible and the tech choices are sound. **Provided that**:

1. The LLVM compile-time risk is properly understood
2. Phased deliverable goals are set, avoiding a single big bang
3. 100% pure JSON is accepted as unrealistic; a few instructions will fall through to C# handlers
4. Effort is estimated at 12+ months minimum (hobby investment)

If the above are acceptable, this is a highly original, high-learning-return project. Even if the full GBA emulator doesn't ship at the end, "JSON-driven ARM7TDMI spec + LLVM IR generator" is a community-usable tool on its own.
