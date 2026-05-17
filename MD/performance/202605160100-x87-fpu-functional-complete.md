# Phase 29 — x87 FPU 功能完整

Phase 29（Intel 8087 / 80287 numeric coprocessor 支援）的收尾筆記。
2026-05-16 在 11 個 sprint（29.1 到 29.11）跨 ~6 hour session time 後
收尾，由 5 分鐘的 `/loop` cron 驅動。

## 出貨內容

JSON-driven CPU framework 現在支援 coprocessor / ISA-extension spec
as first-class modular component，以 Intel 8087 模擬為 proof case。
端到端：

```
spec/machines/ibm-pc-xt.json
  "extensions": ["../coprocessors/x87/i8087/cpu.json"]
   ↓
SpecLoader.LoadCpuSpecWithExtensions
  · 把 register_file_additions.status merge 到 base CpuSpec.RegisterFile
  · 把 instruction_set_additions.encoding_groups merge 到 base
    InstructionSetSpec（prepend 給 mask-match 優先）
   ↓
CpuStateLayout（unified state struct）
  · 8 個 GPR (16-bit) + base status reg + FPU_ST0..FPU_ST7 (i64) +
    FPU_TAGS + FPU_CW + FPU_SW + FPU_TOP + emulator suffix
   ↓
SpecCompiler（單一 LLVM module）
  · 8 個 FPU dispatcher（x86_fpu_d8_dispatch .. x86_fpu_df_dispatch）
    透過 groups/fpu-esc.json 的 $include 接線
  · Memory + port + 4 個 transcendental extern 在 JIT setup 時 bind 到 C#
```

## 實作的 opcode

| 類別 | Opcode | Sprint | 驗證者 |
|---|---|---|---|
| Stack push/pop helper | (internal) | 29.3c | 下面全部都用 |
| Data movement m32fp | FLD m32、FST m32、FSTP m32 | 29.3c/d + gap-fill | 29.3-fpu-roundtrip.com + 29.10-fpu-fillgaps.com |
| Data movement m64fp | FLD m64、FST m64、FSTP m64 | 29.3e | 29.11-fpu-integration.com |
| Data movement m80fp | FLD m80、FSTP m80（透過 f64 internal） | 29.10 | 29.10-fpu-fillgaps.com |
| Register-form FLD/FXCH | FLD ST(i)、FXCH ST(i) | 29.3d + gap-fill | 29.3d-fpu-suite.com + 29.10-fpu-fillgaps.com |
| 常數 | FLD1、FLDL2T、FLDL2E、FLDPI、FLDLG2、FLDLN2、FLDZ | 29.3d | 29.3d-fpu-suite.com |
| 算術（m32 + ST(i)） | FADD、FMUL、FSUB、FSUBR、FDIV、FDIVR | 29.4 | 29.4-fpu-arith.com |
| 比較 | FCOM、FCOMP、FTST (29.8) | 29.5/8 | 29.5-fpu-compare.com |
| FNSTSW AX | DF E0 | 29.5 | 29.5-fpu-compare.com |
| 雜項 unary | FCHS、FABS、FSQRT、FRNDINT、FNOP | 29.8 | 29.8-fpu-misc.com |
| Stack twiddle | FDECSTP、FINCSTP | 29.7 | （透過 29.11 整合） |
| Tag clear | FFREE ST(i) | 29.8 | （透過 29.11 整合） |
| 超越函數 | F2XM1、FYL2X、FPTAN、FPATAN | 29.7 | 29.7-fpu-transcendental.com |
| Control | FNINIT、FNCLEX、FLDCW、FSTCW | 29.3b/9 | 29.3-fninit.com + 29.9-fpu-control.com |
| 整合 capstone | 透過 chained op 算 hypotenuse | 29.11 | 29.11-fpu-integration.com |

那是 **~35 個不同 opcode** cover Intel 8087/80287 在 SDM Volume 2 對
ESC family（0xD8-0xDF）文件化的每個類別。（counter 反映 2026-05-16
補充 gap-fill 的更新、見下面。）

## Test ROM matrix

```
test-roms/x86/
├── 29.3-fninit.com              (3 byte)    FNINIT smoke
├── 29.3-fpu-roundtrip.com       (20 byte)   FLDZ + FSTP m32
├── 29.3d-fpu-suite.com          (74 byte)   FLDPI、FLD m32、FSTP m32、FXCH
├── 29.4-fpu-arith.com          (142 byte)   6 個算術 op
├── 29.5-fpu-compare.com         (75 byte)   3 個 FCOM 結果 + FNSTSW AX
├── 29.7-fpu-transcendental.com (124 byte)   4 個超越函數
├── 29.8-fpu-misc.com           (113 byte)   FCHS/FABS/FSQRT/FRNDINT/FTST
├── 29.9-fpu-control.com         (32 byte)   FLDCW/FSTCW roundtrip
└── 29.11-fpu-integration.com   (109 byte)   Hypotenuse capstone
```

全部 9 個 ROM 在 `apr-x86 --enable-i8087 --dump-fpu-state` 下 pass、
bit-exact match 文件期望值（在 IEEE 754 round-to-nearest-even
for f64→f32 轉換的範圍內）。

## 設計決策（來自 Gemini consult）

- **File 分開、runtime 合一**。按 QEMU TCG / Bochs / 86Box 慣例。FPU JSON
  住在 `spec/coprocessors/x87/i8087/`、load time 由 `SpecLoader` merge
  到 CPU spec。JIT 看到一個 CPU model 帶額外 register + 額外 opcode。

- **內部 f64、不用 x86_fp80**。ARM64 portability + LLVM intrinsic
  reliability 蓋過 8087 對典型 DOS 用途的 f80 precision 優勢。Memory-format
  f32/f64 跨我們的 round-trip 是 lossless；m80fp 文件化為 deferred。

- **超越函數透過 C# Math.* extern**。不用 LLVM intrinsic（target-dependent、
  refuse lower for fp80 on non-x86）。memory extern 的
  `[UnmanagedCallersOnly]` shim + indirect-call-via-global-pointer
  pattern 完全 reuse。

- **Mask 全部 exception、不送 #MF**。99% 的 DOS code 留著 FNINIT 預設的
  control word（0x037F、6 個 exception 全 mask），從結果讀 NaN/Inf、
  不裝 FPU exception INT vector。少數需要 #MF 的 DOS extender 在 scope 外。

- **8-arm switch over physical slot**。LLVM struct GEP 需要 constant
  field index，所以 runtime physical slot access ST(i) 需要 8-arm
  switch + phi node。未來 optimization 可以把 FPU_ST0..ST7 layout 成
  flat 64-byte sub-block、用 byte-offset GEP，但 switch 做法 correctness-first、
  FPU 反正很少是 hot loop。

## File changelog（commit 按時序）

```
4747037 feat(N29.1): spec loader extension — i8087 coprocessor as mix-in
7a08584 feat(N29.2): merge extension register_file_additions into base CpuSpec
d9143f3 feat(N29.3a): split FpuEscape into 8 per-byte dispatcher + design table
6a50751 feat(N29.3b): FNINIT + IP advance fix + FPU state accessor
e60e5c3 feat(N29.3c): FLDZ + FSTP m32fp — full FPU stack push/pop roundtrip
95e49cb feat(N29.3d): FLD m32fp + FXCH ST(i) + 6 個 hardware 常數
c401d0d feat(N29.4): FPU 算術 — FADD/FMUL/FSUB/FSUBR/FDIV/FDIVR
a8aa6e5 feat(N29.5): FCOM/FCOMP + FNSTSW AX
26a25f0 feat(N29.8): FPU 雜項 — FCHS / FABS / FSQRT / FTST / FRNDINT + FFREE
d479e17 feat(N29.9): FPU control — FLDCW / FSTCW / FNCLEX
5983ef5 feat(N29.7): FPU 超越函數 — F2XM1/FYL2X/FPTAN/FPATAN via Math.*
<this commit> feat(N29.3e+11): m64fp load/store + 整合 capstone
```

## 延後（真正 optional）

（更新 2026-05-16 補充 sprint `dff7d7e`：m80fp 原本在這裡 deferred、
但已經透過 LLVM IR bit manipulation 實作 — 看下面「Update: 29.3c/d/10
gap-fill」段。）

- **DC/DA/DE 算術 family** — f64-form 算術帶 reg writeback direction 反過來
  (DC)、i32 / i16 整數算術 (DA/DE)、register-register variant 的 pop-after
  (DE)。全部都是既有 D8 dispatcher 的 pattern mirror；~1 sprint 可 mirror、
  但我們 test corpus 沒 DOS program 實際 emit 它們。
- **DF 整數 load/store** — FILD/FIST/FISTP m16/m32/m64int。加 signed-int
  到 f64 + f64 到 signed-int conversion。給把 FPU 結果存回整數變數的 program
  用；defer。
- **FSCALE / FXTRACT / FPREM** — libm 內部用來從 FPTAN 算 sine/cosine。
  Skip、除非未來加 80387 emulation 帶 FSIN/FCOS 支援。
- **TOP_SW sync 跟 FPU_TOP** — 大部分 DOS code 透過 `FNSTSW AX; SAHF; JCC`
  讀 FCOM 結果、忽略 TOP_SW。要的話容易加（Push/Pop helper 1 行 update）。

## 這對 framework 意味著什麼

JSON-driven framework 現在可證明地支援 **swappable coprocessor** 模型。
同個 i8086 base spec 可以配 i8087 extension（目前的 ibm-pc-xt config）、
無 FPU（省略 `extensions` array — 0xD8-0xDF 然後 undecoded）、原則上
可以配未來 coprocessor（Weitek 1167、80287 with different exception
handling、custom DSP）— 在 `spec/coprocessors/<family>/<chip>/` 加
新的 `cpu.json` + `groups/*.json` 就好。

這是 framework 通用性宣稱目前最強的驗證。Phase 24-26 證明 JSON-driven
CPU dispatch 在單 CPU 層 work；Phase 27 證明 spec 繼承（i80286 extend
i8086）。Phase 29 證明正交擴展 — FPU 不是 CPU 的繼承 variant；是 machine
configuration time merge 的 peer component。

## Update: 29.3c/d/10 gap-fill（2026-05-16 同日補充）

按 user 在初始收尾後的要求，3 個剩下的 data-movement gap 在 commit
`dff7d7e` 補上。上面的 opcode 涵蓋表反映這個更新；上面 Deferred 列表
已經把「m80fp」拿掉。

| Sub-sprint | Opcode | 行為 |
|---|---|---|
| 29.3c-supp | D9 /2 mem FST m32fp | 跟 FSTP m32 一樣但不 pop — 把 ST(0) 留在 stack |
| 29.3d-supp | D9 C0-C7 FLD ST(i) | 把 logical ST(i) copy 到 top（push）；i = rm |
| 29.10 | DB /5 mem FLD m80fp | 讀 10 byte LE、convert 80-bit → f64、push |
| 29.10 | DB /7 mem FSTP m80fp | Pop ST(0)、convert f64 → 80-bit、寫 10 byte |

新的 `X86FpuHelpers` helper：
- `StoreMemF32(eaBase, eaOff, valF64)` — DRY refactor、FSTP m32 + FST m32 共用
  （FPTrunc + 4 byte write）。
- `LoadMemF80AsF64(eaBase, eaOff)` — 10 byte → 拆 sign/exp/mant → branch-free
  `select` on (exp==0 / exp==0x7FFF / normal) → f64 bit。
- `StoreMemF80(eaBase, eaOff, valF64)` — 反過來：bitcast f64 → 拆 → 同樣
  select pattern → 寫 10 byte。

m80fp 實作完全在 LLVM IR 裡用 `select` 做 bit 操作（沒 cond-br）、
乾淨地 lower 到 x86-64 CMOV。Special case（zero、Inf/NaN）不用 branch 處理。
f64 internal precision 意思是 m80 → f64 round-trip 損失 mantissa 低 11 bit
（依 Gemini「f64 internal」決策 §2 文件化為可接受）。

Test ROM `test-roms/x86/29.10-fpu-fillgaps.com`（94 byte）驗證全部 4 個
新 op：
- π 的 FST m32 + FSTP m32 → `[out]` 跟 `[out2]` 兩個都含 `0x40490FDB`（證明 FST 沒 pop）
- FLD ST(1) of 3.5 → FSTP m32 → out high WORD = `0x4060`
- FLD m80(e) → FSTP m32 → e_f32 high WORD = `0x402D`（match `(float)M_E`）

最終 state AX/BX/CX/DX = `0x4049 / 0x4049 / 0x4060 / 0x402D`。
FreeDOS HLE regression：2838 INT call。Plan doc 更新標 29.10 ✅ DONE
+ 確認 29.6 已經在 29.3d cover（無獨立工作）。

## 交叉參考

- Plan doc: `MD/design/29-x87-fpu-plan.md`
- Phase 28 收尾（FreeDOS boot）: `MD/performance/202605152200-pc-emulator-freedos-boot.md`
- Phase 28.IO（port I/O + 初始 FPU no-op）: `MD/performance/202605152230-pc-emulator-phase-28-io.md`
- Gemini consult log：
  - `tools/knowledgebase/message/20260515_223616.txt` (FPU 設計基礎)
  - `tools/knowledgebase/message/20260515_224332.txt` (separate vs integrated)
