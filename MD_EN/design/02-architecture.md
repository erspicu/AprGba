# System Architecture

## High-Level Architecture Diagram

```
┌──────────────────────────────────────────────────────────┐
│                    AprGba 模擬器主程式                      │
├──────────────────────────────────────────────────────────┤
│  GUI / Display  │  Input  │  ROM Loader  │  Debugger      │
│  (Avalonia)     │         │              │                │
├──────────────────────────────────────────────────────────┤
│              Memory Bus (memory map dispatch)              │
│  ┌──────┬──────┬──────┬──────┬──────┬──────┬──────────┐    │
│  │BIOS  │EWRAM │IWRAM │IO Reg│PRAM  │VRAM  │GamePak ROM│    │
│  └──────┴──────┴──────┴──────┴──────┴──────┴──────────┘    │
├──────────────────────────────────────────────────────────┤
│               AprCpu 核心 (CPU 框架)                      │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐      │
│  │ Code Cache   │  │ Block         │  │ Register File│      │
│  │ (PC → fnPtr) │  │ Compiler     │  │ R0–R15, CPSR │      │
│  └──────┬───────┘  └──────┬────────┘  └──────────────┘      │
│         │                  │                                 │
│  ┌──────▼──────────────────▼─────────────────────────────┐  │
│  │ JSON Parser + LLVM IR Emitter (LLVMSharp)             │  │
│  └──────────────────────┬────────────────────────────────┘  │
└─────────────────────────┼───────────────────────────────────┘
                          │
                   ┌──────▼──────┐
                   │ JSON Spec   │
                   │ arm.json    │
                   │ thumb.json  │
                   └─────────────┘
```

## Component Responsibilities

### 1. JSON Spec (`spec/arm7tdmi/*.json`)
- Defines instruction encoding formats: bit patterns, mask/match
- Defines semantics (sequence of micro-op steps)
- Defines cycle count hints
- **Contains no execution logic** — it is just a "manual"
- Pure data, can be consumed by multiple backends (IR emitter, doc generator, static analysis)

### 2. JSON Parser + IR Emitter (`AprCpu.Core/IR`)
- Reads JSON
- Parses bit patterns → mask/match dispatch table
- For each micro-op, calls the LLVMSharp API to emit IR
- Outputs `.ll` (for debugging) or in-memory `LLVMModuleRef` (for execution)

### 3. Block Compiler (`AprCpu.Core/IR/BlockFunctionBuilder.cs` +
   `AprCpu.Core/Runtime/BlockDetector.cs`)
- Starts from a given PC, fetches/decodes/accumulates instructions (detector cap of 64 instr)
- Ends a block on `writes_pc:"always"` / `switches_instruction_set` / `changes_mode`
- Strings the IR for all instructions in the block into one LLVM Function (4 internal BBs per-instr: pre/exec/post/advance + a shared `block_exit`)
- Calls ORC LLJIT (`HostRuntime.AddModule`), gets back a native function pointer

### 4. Code Cache (`AprCpu.Core/Runtime/BlockCache.cs`)
- `Dictionary<uint, LinkedListNode<Entry>>` + LRU doubly-linked list, capacity-bound (default 4096)
- On hit, jump and execute (`CpuExecutor.StepBlock`)
- SMC detection: invalidate on writes to "already-compiled regions" (A.5 not implemented, pending)
- Advanced: block linking (patch native call to jump directly to the next block) (A.7 not implemented)

### 5. Register File / CPU State (`AprCpu.Core/IR/CpuStateLayout`)
- **Built dynamically from spec**: layout is not hardcoded to ARM shape; `CpuStateLayout` reads `RegisterFile` + `ProcessorModes` to dynamically assemble the LLVM struct
- Structure: `[GPRs] + [status registers + per-mode banked status slots] +
  [per-mode banked GPR groups] + [cycle_counter i64, pending_exceptions i32]`
- The same framework code can produce layouts for both ARM7TDMI (16×GPR + CPSR + 5×SPSR + 5 banked groups) and LR35902 (7×8-bit GPR + F flag)
- The C# host's `CpuState` mirrors this layout (Phase 3 work item) and is passed to JIT machine code via `unsafe` pointer for direct access, avoiding marshaling

### 6. Memory Bus (`AprCpu.Core/Runtime/Gba/GbaMemoryBus.cs`,
   implements `AprCpu.Core/Runtime/IMemoryBus.cs`)
- `IMemoryBus` interface, called by CPU JIT via trampoline
- **Fast Path**: RAM regions read/write directly via unsafe ptr (IWRAM/EWRAM/VRAM bulk); the GBA bus uses typed cast (`bus as GbaMemoryBus`) + cart-ROM range check to bypass interface dispatch
- **Slow Path**: IO registers go through callback dispatch + IRQ trigger
- Reserved SMC write barrier hook
- Reserved hook for IO-write-triggered sync points (for PPU catch-up; Phase 1a/1b downcounting + MMIO catch-up callback already implemented)

### 7. PPU (`AprGba.Cli/Video/`, hand-written, not driven by JSON spec)
- LCD registers: DISPCNT, DISPSTAT, VCOUNT
- VBlank / HBlank interrupt triggers
- Mode 3 (240×160 RGB555 framebuffer) — simplest, used by some homebrew
- Mode 0 (4 tile-based BG layers) — used by jsmolka test ROMs to print results
- Sprite / Window / Mosaic / Affine BG / Blend: skipped if jsmolka doesn't need them
- **Not driven by JSON spec** (2026-05 scope decision): the PPU is not an instruction stream but a fixed-function pipeline; forcing it into JSON gives no framework leverage. Follow the GB-side `GbPpu` pattern and write directly as host code

### 8. CLI / Host (`AprGba.Cli`, single project, pure headless)
- **No GUI, no 60fps loop, no real-time playback** (2026-05 scope decision)
- Contains `Program.cs` (entry), `Video/` (PPU host code), `RomPatcher.cs`
  (Nintendo logo / header checksum patches) — bus / scheduler / system runner
  all live under `AprCpu.Core/Runtime/Gba/`; the CLI only does wiring
- Follows the `apr-gb` design: `apr-gba --rom=X.gba --bios=Y.bin --cycles=N
  --screenshot=Z.png [--block-jit]`
- After running N cycles, calls `GbaPpu.RenderFrame(bus)` → write PNG
- Optional `--info` prints ROM header / cartridge type for diffing PRAM/VRAM against mGBA dumps

---

## Execution Model

### Phase 3-6: Pure Interpreter Mode (no JIT)

```
loop:
  ins = MemoryBus.Read32(PC)              # 或 Read16 看模式
  format = Decoder.Match(ins)
  if not CheckCondition(ins, CPSR):
    PC += step; continue
  Execute(format, ins)                     # 透過 emit 出來的 LLVM 函式或解譯
  PC += instruction_size
  cycle_count += instruction_cycles
  if cycle_count >= scanline_cycles:
    PPU.tick_scanline()
    HandleInterrupts()
```
No cache, no blocks. Used for bring-up and correctness validation.

### Phase 7: Block JIT Mode

```
loop:
  block = CodeCache.Lookup(PC)
  if block == null:
    block = BlockCompiler.Compile(PC)      # JSON → IR → JIT
    CodeCache.Store(PC, block)
  cycles = block.Execute(state*, bus*)     # native call
  cycle_count += cycles
  PPU.CatchUp(cycle_count)
  HandleInterrupts()
```

### Sync Strategy

- Default: **instruction-level catch-up**
- Sync PPU / Timer / DMA only at block end
- **Forced sync points**: writes to IO registers (`0x04000000–0x040003FE` range) → block ends immediately
- **No Master Clock**

---

## Data Flow

### Boot Flow

1. Load `arm.json`, `thumb.json`
2. Parser parses JSON, builds the encoding format table (sorted by mask/match)
3. (Optional) Pre-emit shared micro-op handler functions
4. Load GBA BIOS and ROM into the Memory Bus
5. Set PC = 0x00000000 (BIOS entry point) or 0x08000000 (ROM)
6. Enter the main loop

### Block Compile Flow (Phase 7)

```
1. PC=X，code cache miss
2. Decoder 從 X 開始：
   a. Fetch 指令 (依模式 16/32 bit)
   b. 找符合的 encoding format（mask/match 比對）
   c. Field extraction
   d. 將 micro-op steps 逐一 emit 為 LLVM IR
   e. Emit cycle 累加
   f. 判斷是否為 block terminator (B/BL/BX/MOV PC, etc.)
3. Block 結尾 emit return + 最終 PC
4. 呼叫 LLVM JIT 編譯整個 LLVMFunction
5. 取得函式指標寫入 code cache
```

---

## Interface Contracts

### `IMemoryBus`

```csharp
public interface IMemoryBus
{
    byte   Read8 (uint addr);
    ushort Read16(uint addr);
    uint   Read32(uint addr);
    void   Write8 (uint addr, byte v);
    void   Write16(uint addr, ushort v);
    void   Write32(uint addr, uint v);

    int GetCyclesForAccess(uint addr, AccessSize size, AccessKind kind);
    bool IsCodeRegion(uint addr);  // 給 SMC barrier 用
}
```

### `CpuState` (context passed to JIT — **layout determined by spec**)

`CpuStateLayout` dynamically assembles the LLVM struct from the spec; the C# host mirrors it with a matching `StructLayout(LayoutKind.Sequential)`. **The example below is just for ARM7TDMI**; switching specs (e.g. LR35902) produces a different field sequence.

The ARM7TDMI layout looks roughly like:

```csharp
// 由 CpuStateLayout 在執行期決定 — 不是硬編碼
[StructLayout(LayoutKind.Sequential)]
public unsafe struct CpuState_Arm7tdmi
{
    public fixed uint R[16];           // 通用暫存器
    public uint CPSR;
    public uint SPSR_fiq, SPSR_irq, SPSR_svc, SPSR_abt, SPSR_und;
    public fixed uint R_fiq[7];        // R8–R14 banked
    public fixed uint R_irq[2];        // R13–R14 banked
    public fixed uint R_svc[2];
    public fixed uint R_abt[2];
    public fixed uint R_und[2];
    public ulong CycleCounter;
    public uint   PendingExceptions;   // bitmask
}
```

LR35902 has a totally different layout (A/B/C/D/E/H/L/F all 8-bit, SP/PC 16-bit, no banked regs, no SPSR). Framework code is unchanged; only the spec differs.

### JIT Function Signatures

Per-instruction (the dispatcher path before Phase 7.A.6; per-instr mode still uses this):
```
void Execute_<Set>_<Format>_<Mnemonic>(CpuState* state, uint32_t instruction_word);
// 寫入 state、回傳 void。caller 從 state.PcWritten / state.GPR[pc_index]
// 推算下一條 PC。
```

Block (Phase 7.A.6+, enabled by the `--block-jit` flag):
```
void ExecuteBlock_<Set>_pc<XXXXXXXX>(CpuState* state);
// instruction word 都已 baked into IR (block compile 時從 bus 讀完)。
// 函式內每條指令是一個 instr_pre/exec/post/advance BB sequence；
// block 結束 fall through 到共用的 block_exit BB → ret void。
// caller (CpuExecutor.StepBlock) 從 state.PcWritten 判斷是否跳轉、
// 從 state.CyclesLeft delta 算實際消耗 cycles (Phase 1a downcounting)。
```

---

## JSON Schema

Full schema spec: see `04-json-schema-spec.md`; micro-op vocabulary: see
`05-microops-vocabulary.md`; concrete spec examples: see `spec/arm7tdmi/`.

---

## Directory Layout (actual)

```
AprGba/
├── MD/
│   ├── design/                  ← 設計文件（本目錄）
│   ├── note/                    ← 收工筆記
│   ├── performance/             ← bench 紀錄（每個策略一檔）
│   └── process/                 ← 跨 phase 流程（QA workflow 等）
├── src/
│   ├── AprCpu.Core/             ← CPU 框架核心（spec → IR → JIT 都在這）
│   │   ├── JsonSpec/              ← JSON loader & schema
│   │   ├── Decoder/               ← bit pattern matching + DecoderTable
│   │   ├── IR/                    ← LLVMSharp emitter (Emitters/ArmEmitters/
│   │   │                            Lr35902Emitters/StackOps/FlagOps/BitOps/
│   │   │                            BlockFunctionBuilder/InstructionFunctionBuilder)
│   │   └── Runtime/               ← HostRuntime (ORC LLJIT) + CpuExecutor +
│   │       │                        BlockCache + BlockDetector
│   │       └── Gba/                 ← GBA-specific bus / scheduler / system
│   │                                  runner (GbaMemoryBus / GbaScheduler /
│   │                                  GbaSystemRunner)
│   ├── AprCpu.Compiler/         ← CLI: `aprcpu --spec X.json --output Y.ll`
│   ├── AprCpu.Tests/            ← xUnit (360 tests)
│   ├── AprGba.Cli/              ← `apr-gba` GBA harness (Program/Video/RomPatcher)
│   └── AprGb.Cli/               ← `apr-gb` Game Boy harness (legacy + json-llvm)
├── spec/
│   ├── arm7tdmi/                ← cpu.json + arm/thumb sub-specs
│   └── lr35902/                 ← cpu.json + 25 group files (block0/1/2/3 + cb-*)
├── test-roms/                   ← gba-tests/ + gb-tests/
├── BIOS/                        ← gba_bios.bin (LLE)
├── ref/                         ← vendor manuals + datasheets (gitignored 大檔)
└── temp/                        ← 本地 scratch (gitignored)
```

---

## Key Design Decisions

| Decision | Choice | Rationale |
|---|---|---|
| Description language | JSON | Familiar, mature tooling; YAML or custom DSL possible later |
| JIT backend | LLVM (via LLVMSharp) | Industrial-grade optimization; fallback = .NET DynamicMethod |
| Host form | **Pure headless CLI** (no GUI) | scope = test ROM screenshot validation; no real-time playback needed |
| Sync model | Instruction-level catch-up | Balances accuracy and performance; forced sync on IO writes |
| Memory bus | callback + fast/slow path | Decoupled, interceptable |
| CPU state | byte buffer + direct pointer | Avoid marshal overhead; layout dynamically determined by host runtime |
| BIOS load | **LLE (bring your own BIOS file)** | Run the official BIOS intro → ROM entry; more credible than HLE |
| PPU scope | Mode 3 + Mode 0, hand-written | Minimum mode set required to render jsmolka result text |
| PPU not driven by JSON | hand-written `GbaPpu` | Fixed-function pipeline has no cross-device reusability |
| Block-JIT (Phase 7) | Optional optimization | Test ROMs don't need real-time; slower is fine |
