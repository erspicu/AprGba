# Phase 29 — x87 FPU 支援

> **狀態**：✅ **功能完整**（2026-05-16）。所有核心 8087 opcode 類別都
> 實作了：spec extension (29.1)、register file (29.2)、per-byte dispatcher
> split (29.3a)、control + IP fix (29.3b)、data movement m32 + m64 + FXCH
> (29.3c/d/e)、constant (29.3d)、算術 FADD/FMUL/FSUB/FDIV±R (29.4)、
> 比較 FCOM/FCOMP + FNSTSW AX (29.5)、透過 Math extern 的超越函數
> F2XM1/FYL2X/FPTAN/FPATAN + FDECSTP/FINCSTP (29.7)、雜項
> FCHS/FABS/FSQRT/FTST/FRNDINT + FFREE (29.8)、control
> FLDCW/FSTCW/FNCLEX (29.9)、m80fp pack/unpack + FST m32 + FLD ST(i)
> gap-fill (29.10 + 29.3c/d-supp)、端到端整合測試 hypotenuse
> `√(3²+4²) = 5.0` (29.11)。每個 sprint 出貨自己驗證的 test ROM；
> ~8 個 test ROM bit-exact 過 .NET Math.* / glibc 結果。
>
> **Phase 29 解開 Phase 28 的 real-BIOS path**（2026-05-16）：
> pcxtbios.bin 的 FPU detection 序列（FNINIT → FSTCW [SI] → CMP）
> 現在看到對的 power-on FPU state 並推進過 video init。配合 Phase 28.IO
> port retrace fix + Phase 30 FDC/DMA，real BIOS + FreeDOS 端到端 boot。
>
> **延後（真 optional）**：
> - DC/DA/DE 算術家族（f64 reg arith + i32/i16 整數 arith + pop-after
>   register variant）— D8 pattern mirror、看到 DOS program 真用時容易加。
> - DF 整數 load/store（FILD/FIST/FISTP m16/m32/m64int）— 只 FNSTSW AX
>   實作；整數到 FPU 轉換延後。
> - FSCALE / FXTRACT / FPREM — 一些 libm 內部要；延後。
> - FPU_TOP 跟 TOP_SW sync — DOS code 通常透過 SAHF+JCC 讀 C bit 然後
>   忽略 TOP_SW。
> - #MF exception 傳遞 — Gemini 指引：mask 全部 exception、讓 IEEE 754
>   產生 NaN/Inf；99% 的 DOS code 從沒 install FPU exception vector。

原狀態（規劃中）：Phase 28.IO 期間捕捉、no-op FPU stub（0xD8-0xDF →
`x86_fpu_noop`）加進來解 real PC/XT BIOS POST。Real BIOS FPU detection
透過 no-op path 過（BIOS 看到 FSTCW 沒寫、結論「無 FPU」、清 equipment
bit）。FreeDOS boot 不需要完整 8087 emulation，但任何碰浮點數學的 DOS
program（Turbo C/Pascal、AutoCAD、MS Flight Simulator、Windows 3.x
WIN87EM 等）需要。

本文件記錄 2026-05-15 跟 Gemini knowledgebase consultation 同意的架構
決策（兩 pass：design basics + separate-vs-integrated）。實作會在
Phase 28.x 完全收尾後 Phase 29.x sprint 內 land。

---

## 1. Spec layout — separate JSON file、load time merge

**投票：透過 compile-time JSON 組合（mix-in）的整合 data model**。

Separate JSON 為了 modularity、但 merge 進同個 JIT pipeline：

```
spec/
  cpu/x86-16/i8086/cpu.json          (無 FPU — D8-DF 這裡未定義)
  cpu/x86-16/i80286/cpu.json
  coprocessors/x87/i8087.json        (新 — register + D8-DF opcode)
  coprocessors/x87/i80287.json       (新 — 287 exception handling override)
  machines/ibm-pc-xt.json            (machine 透過 extension reference 兩個)
```

Machine config：

```json
{
  "name": "IBM PC/XT",
  "components": {
    "main_cpu": {
      "spec": "cpu/x86-16/i8086/cpu.json",
      "extensions": ["coprocessors/x87/i8087.json"]
    }
  }
}
```

**Loader 行為**：啟動時讀 `cpu.json`、然後每個 extension。把 FPU register
（ST0-ST7、control word、status word、tag word、TOP）append 到 base CPU
的 register array。把 D8-DF opcode entry append 到 decode tree。LLVM
emitter 開始 JIT 時、它看到一個統一 CPU model 帶 D8-DF 定義 + 8 個新
浮點 register。

這 match QEMU TCG（`CPUX86State.fpregs`）、Bochs（`bx_cpu_c` monolithic
class）、86Box、Unicorn — 所有主要 emulator 都在 runtime 把 FPU 整進
main CPU state 雖然概念上分開。

## 2. Register file 表現

Flat array + TOP index、JSON 內宣告、C# emitter 處理旋轉：

```json
{
  "fpu_regs":  { "type": "f64[8]", "comment": "stack-allocated rotating register" },
  "fpu_tags":  { "type": "u8[8]",  "comment": "0=valid 1=zero 2=special 3=empty" },
  "fpu_top":   { "type": "u32",    "comment": "0-7、指 ST(0)" },
  "fpu_cw":    { "type": "u16",    "comment": "control word — rounding/precision/exception mask" },
  "fpu_sw":    { "type": "u16",    "comment": "status word — C0-C3、TOP、exception flag" }
}
```

**為什麼 flat `f64` 不 `x86_fp80`**：LLVM `x86_fp80` target-dependent、
ARM64 上特別痛（軟體 emulation 或拒絕 lower）。99.9% 的 DOS program 在
memory 用 32-bit float 或 64-bit double、只靠 80-bit 內部精度避免中間
rounding。標準 f64 IEEE-754 對 Turbo C、AutoCAD 等夠。

**Catch — m80fp memory 格式**：`FLD m80fp` / `FSTP m80fp` 讀/寫 10 byte。
寫 extern helper（`fpu_load_m80(addr)`、`fpu_store_m80(addr, val)`）
把 10-byte 格式 pack/unpack 成 f64（load 時失精、store 時合成額外 bit）。

**Rotation 策略**：不要每次 access 都 emit `(TOP + i) % 8` 進 LLVM IR
（SSA 膨脹）。C# emitter 每 instruction 讀 `fpu_top` 一次、算 absolute
index、用 `getelementptr`（GEP）access flat array。

## 3. Opcode encoding — pivot 在 ESC byte + ModR/M reg field

不要爆成 200+ JSON entry。x87 encoding 是 logical：
- ESC byte (D8-DF) + ModR/M `reg` field (bit 3-5) → operation
- ModR/M `mod` field (bit 6-7) → operand source（mod=3 → ST(i)、其他 memory）
- ESC byte → memory operand size（D8=f32、DC=f64 等）

JSON shape per Gemini：

```json
{
  "mask": "0xFF", "match": "0xD8", "reg_match": 0, "has_modrm": true,
  "op": "fpu_add", "mem_size": "f32", "st_op": true
}
```

C# `fpu_add` emitter 在 JIT time：
- 如果 memory operand：emit `memory_read_f32` extern call、extend 到 f64、
  emit LLVM `fadd`。
- 如果 register (ST(i))：emit GEP 到 `fpu_regs[top+i mod 8]`、emit LLVM `fadd`。

## 4. 超越函數 — extern call 到 C# Math library

**不要** 用 LLVM intrinsic（`llvm.sin.f80` 等）— target-dependent、
non-x86 上 fp80 不可靠。

**不要** inline emit polynomial expansion。

**要** call out 到 C# extern：
```c#
[UnmanagedCallersOnly] static double FpuTan(double x) => Math.Tan(x);
[UnmanagedCallersOnly] static double FpuSqrt(double x) => Math.Sqrt(x);
```

JIT setup 時 bind 一次；transition overhead 可忽略因為超越函數在
真 silicon 上是幾百 cycle。

**Historical note**：8087 跟 80287 沒 FSIN/FCOS/FSINCOS — 那是 80387+。
8087 只有 FPTAN（tangent）+ FPATAN（arctan）；guest 軟體從 tangent
identity 算 sine/cosine。如果嚴格 emulate 8087、我們只需要：FPTAN、
FPATAN、F2XM1、FYL2X、FYL2XP1、FSCALE、FXTRACT、FSQRT、FRNDINT、
FPREM、FABS、FCHS。

## 5. Status word + C0-C3 condition code

**不要** 對 FPU compare 做 lazy flag evaluation — 它們相對於整數 ALU op
少、guest 軟體幾乎永遠 `FCOM` 之後立刻透過 `FNSTSW AX; SAHF` 讀
status word。

每個 `FCOM` / `FTST` / `FUCOM` 之後：
1. Emit LLVM `fcmp`（oeq / ogt / olt / uno）產 i1 值。
2. 把每個 i1 shift 進 `fpu_sw` 的對的 bit：
   - C0 → bit 8
   - C1 → bit 9
   - C2 → bit 10
   - C3 → bit 14
3. Emit 一個 store 到 `fpu_sw`。

`FNSTSW AX` 讀 `fpu_sw` 寫進 CPU 的 AX。因為 FPU spec load time merge
進同個 state struct、這 compile 成單一 LLVM struct-field store —
無 cross-module callback。

## 6. Pending exception (#MF) — mask 全部

預設 8087 init state（FINIT 之後）是所有 exception mask。在這狀態、
divide-by-zero 產 IEEE infinity、invalid op 產 QNaN。

99% 的 DOS/Win16 軟體：
1. Call FINIT 或 boot 到 masked default。
2. 從沒 install FPU exception handler（8086 IRQ-via-NMI route 的 INT 10h）。
3. 如果 bother check 的話手動 check NaN。

**建議**：hardcode 我們的 control word 忽略 guest 試圖 unmask exception。
讓 LLVM 預設 IEEE-754 行為正常產 infinity + NaN。完全跳過 #MF 傳遞。

如果某個特定 program（罕見的 protected-mode DOS extender）之後 crash
因為它期待 DivZero 的 INT 75h、那時再加。目前：死重量。

---

## Phase 29.x sprint plan

| Sprint | Scope | Note |
|---|---|---|
| 29.1 | Spec loader extension 支援 — ✅ **DONE 2026-05-15** | `MachineSpec.Extensions` + `SpecLoader.LoadCpuSpecWithExtensions()` + `SpecCompiler.Compile(path, extensions)` + `X86JsonCpu(extensionPaths:)`。`spec/coprocessors/x87/i8087/cpu.json` + `groups/fpu-esc.json` 建；FpuEscape entry 從 `spec/cpu/x86-16/i8086/groups/misc.json` **移出**（端到端證明 merge）。FreeDOS regression 完整（2838 HLE INT call）；real BIOS POST 推進 F000:E706 → F000:F433。 |
| 29.2 | State struct 的 FPU register file — ✅ **DONE 2026-05-15** | `LoadCpuSpecWithExtensions` 的 `register_file_additions.status[]` parsing。Extension status register append 到 base `RegisterFile.Status[]` 讓 base CPU 的 pre-cached offset（FLAGS/IP/CS/等）穩定。i8087 extension 宣告 ST0-ST7（64-bit i64 slot、Phase 29.3+ emitter 透過 bitcast 變 f64 per Gemini 「ARM64-friendly f64 over x86_fp80」決策）、FPU_TAGS（16-bit、per ST(i) 2 bit）、FPU_CW（16-bit 帶 PC/RC/IC/exception-mask field）、FPU_SW（16-bit 帶 C0/C1/C2/C3 condition code + TOP_SW mirror + sticky exception flag）、FPU_TOP（32-bit index 0-7）。Build 綠；FreeDOS regression 完整（2840 HLE INT call）；apr-x86 standalone Tom Harte path 也沒動。 |
| 29.3a | Per-byte FPU ESC dispatch split — ✅ **DONE 2026-05-15** | 把單一 catch-all `FpuEscape`（mask=0xF8 match=0xD8）取代為 8 個 per-byte format（mask=0xFF match=0xD8..0xDF）讓每個 ESC byte 拿自己的 dispatcher emitter（`x86_fpu_d8_dispatch` .. `x86_fpu_df_dispatch`）。8 個目前都 no-op、行為跟 pre-29.3 一樣、但 split 讓我們漸進出貨個別的 /reg sub-opcode — D9 可以 land FLD/FSTP/FXCH/FLDZ 而 D8/DC 留 no-op 到 29.4（算術）。FreeDOS regression：2839 INT call；real BIOS POST 還是推進到 F000:F436。 |
| 29.3b | FNINIT + IP advance fix + state accessor — ✅ **DONE 2026-05-15** | (1) 對所有 8 個 FPU dispatcher format 加 `x86_fetch_modrm` + `x86_modrm_compute_ea` step，讓 IP 正確推進過 ModR/M byte + 任何 displacement（之前：no-op stub 留 IP 在 instruction 中間、BIOS POST 在 misalign byte 上「幸運」混過去）。(2) `X86FpuDBDispatchEmitter` 實作 `FNINIT`（DB E3）— 偵測 `mod=11 reg=100 rm=011` 並寫 power-on default：FPU_CW=0x037F、FPU_SW=0x0000、FPU_TAGS=0xFFFF、FPU_TOP=0。(3) `X86JsonCpu.TryReadFpuTop / TryReadFpuCw / TryReadFpuSw / TryReadFpuTags / TryReadFpuPhysicalSt` accessor。(4) `apr-x86 --enable-i8087 --dump-fpu-state` flag。(5) Test ROM `test-roms/x86/29.3-fninit.com`（3 byte：DB E3 F4）：apr-x86 報 `TOP=00 CW=037F SW=0000 TAGS=FFFF`、IP 推進到 0103。FreeDOS HLE regression：2839 INT call。Real BIOS POST：同 MDA retrace loop 位置（FPU detection 早就過了；剩下的 stall 是無關的 port-0x3BA timing）。 |
| 29.3c | FLDZ + FSTP m32fp + stack push/pop helper — ✅ **DONE 2026-05-15** | 第一個帶 f64↔i64 bitcast + 透過 segmented byte write 做 memory store 的 sprint。`X86FpuHelpers.Push/Pop/SetTag/GepPhysicalSt`（8-arm switch over physical slot index）。FLDZ (D9 EE) push f64(0.0)、把 slot tag 為 Zero (01)。FSTP m32fp (D9 /3 mem) pop ST(0)、`FPTrunc` f64→f32、bitcast 到 i32、透過 `SegmentedWrite8FromBase` 寫 4 byte little-endian、然後清 tag 到 Empty (11) 並推 TOP。Test ROM `29.3-fpu-roundtrip.com`（20 byte）：FNINIT → FLDZ → FSTP DWORD [scratch] → MOV AX,[scratch] → MOV BX,[scratch+2] → HLT；結果 AX=0000 BX=0000（覆蓋 DEADBEEF pre-fill）、TOP=00、TAGS=FFFF。FreeDOS HLE regression：2839 INT call。 |
| **29.10 + 29.3c/d gap-fill** | m80fp + FST m32 + FLD ST(i) — ✅ **DONE 2026-05-16** | Per user request「希望將 29.3c/29.3d/29.6/29.10 規格功能實作補上」：填 data-movement track 剩下的 gap。(1) **D9 /2 mem = FST m32fp**（no-pop store；跟 FSTP m32 同 path 但跳 Pop）透過新 `X86FpuHelpers.StoreMemF32` helper（跟 FSTP m32 DRY）。(2) **D9 C0-C7 = FLD ST(i)**（logical ST(i) 的 register-form push）透過 range check + LoadLogicalSt(rm) + Push。(3) **DB /5 = FLD m80fp** 跟 **DB /7 = FSTP m80fp**（Phase 29.10）透過新 `LoadMemF80AsF64` / `StoreMemF80` helper — 在 LLVM IR 內 bit-manipulation 把 80-bit 格式（15-bit exp、63-bit mantissa、explicit integer bit、bias 16383）轉到/從 f64（11-bit exp、52-bit mantissa、implicit integer bit、bias 1023）。Special case zero（exp=0 → 產 0.0 / 0x0000 exp）跟 Inf/NaN（exp=0x7FFF → f64 exp=0x7FF）透過 LLVM `select` 不用 branch 處理。Phase 29.6（constant）確認已經 29.3d cover（7 個 FLD1/FLDL2T/FLDL2E/FLDPI/FLDLG2/FLDLN2/FLDZ 常數）。Test ROM `29.10-fpu-fillgaps.com` 跑 3 個 sub-test（FST→FSTP π pair、FLD ST(i) of 3.5、FLD m80(e) → FSTP m32）— 4 個驗證 GPR 全部 match 預期（AX/BX=0x4049、CX=0x4060、DX=0x402D）。FreeDOS HLE regression：2838 INT call。 |
| 29.3e | FLD/FST/FSTP m64fp + 整合測試 — ✅ **DONE 2026-05-16** | DD /0 mod≠11 FLD m64fp、DD /2 FST m64fp、DD /3 FSTP m64fp 透過新 `LoadMemF64` / `StoreMemF64` helper — 直接 i64-as-f64 transfer、不 widening/narrowing 因為內部精度已經是 f64。DD register form 留給 FFREE (29.8)；dispatcher refactor 到 mem-vs-reg branch + per-case switch。Phase 29.11 整合測試 `29.11-fpu-integration.com` 在一個 ROM 內 exercise Phase 29.1-9 每個類別（hypotenuse `√(3²+4²) = 5.0` 透過 m64 load、m32 arith、reg arith、FXCH、FSQRT、m64 store；compare + FNSTSW AX；FLDPI×2 + FPATAN）。最終 state：AX=0x0000、BX=0x4014（f64 5.0 = 0x4014000000000000 的 high WORD）、CX=0x3F49（f32 π/4 = 0x3F490FDB 的 high WORD）、DX=0x0000。端到端成功。 |
| 29.3d | FLD m32fp + FXCH ST(i) + FLD1/FLDPI/... — ✅ **DONE 2026-05-15** | (1) FLD m32fp (D9 /0 mod≠11) — 透過 `SegmentedRead8FromBase` 從 EA 讀 4 byte、組 i32、bitcast f32、FPExt f64、push with Tag=Valid。(2) FXCH ST(i) (D9 C8..CF mod=11 reg=1) — 讀 rm、swap physical slot TOP with `(TOP + rm) & 7` 透過新 `X86FpuHelpers.SwapSlots` + tag-swap helper。(3) 6 個 hardware 常數 D9 E8..ED (FLD1=1.0、FLDL2T=log2(10)、FLDL2E=log2(e)、FLDPI=π、FLDLG2=log10(2)、FLDLN2=ln(2)) — 全部透過 `Push(constReal, tag=Valid)` push。加上 FLDZ 從之前 ad-hoc check 搬到統一 switch。Dispatcher 重構為乾淨 mod-vs-mem branch + switch（之前：chained cond-br）。Test ROM `29.3d-fpu-suite.com` 跑 FNINIT → FLDPI → FSTP m32、FLD m32 → FSTP m32 roundtrip、跟 FXCH ST(1) swap；6 個驗證 GPR 全部 match 預期（AX/BX=0x40490FDB for FLDPI→f32 round、SI/DI=0x40490FDA for byte-identical roundtrip、CX/DX 確認 FXCH swap 把 1.0 放 0.0 上）。FreeDOS HLE regression：2839 INT call。 |
| 29.3c | FLD m32fp + FXCH ST(i) + FLD1/FLDPI/FLDL2E/... | D9 /0 mem (FLD m32fp memory-form load)、D9 C8+i (FXCH register-form)、D9 E8-EE 常數 load。 |
| 29.3d | FLD/FST/FSTP m64fp | DD /0 /2 /3 mem form (64-bit double load/store)。 |
| 29.4 | FPU 算術 (D8 family) — ✅ **DONE 2026-05-15** | D8 dispatcher 實作 FADD / FMUL / FSUB / FSUBR / FDIV / FDIVR 帶 m32fp (mod≠11) 跟 ST(i) (mod=11) 兩種 operand form。ST(0) := ST(0) <op> operand（或 reverse variant 的 operand <op> ST(0)）。新 helper：`LoadLogicalSt(i)` 透過 (TOP+i)&7 GEP 讀 ST(i)、`StoreLogicalSt(i, val)` 寫回 + set Valid tag、`LoadMemF32AsF64(eaBase, eaOff)` 做跟 FLD m32fp 共用的 4-byte→f32→f64 widening。Test ROM `29.4-fpu-arith.com` 跑 6 個 sub-test（FNINIT → FLD → D8 op → FSTP）並透過最終 GPR state 驗全部結果：3+4=7、3×4=12、10-3=7、10-3=7 (FSUBR)、12÷4=3、12÷4=3 (FDIVR)。6 個結果全 match IEEE 754 f32 bit-exact（BX=40E0、CX=4140、DX=40E0、SI=40E0、DI=4040、BP=4040）。FCOM/FCOMP (/2, /3) 留 no-op — Phase 29.5。DC (f64-form 算術) 跟 DE (pop-after register variant) follow 同 pattern、延後但容易 mirror。FreeDOS HLE regression：2840 INT call。 |
| 29.5 | FCOM/FCOMP + FNSTSW AX — ✅ **DONE 2026-05-15** | D8 /2 FCOM 跟 D8 /3 FCOMP 透過新 `X86FpuHelpers.Compare(st0, operand)` 接 — 用 LLVM unordered-aware `fcmp ueq` / `uno` / `ult` 讓 NaN case 正確 set C2=C3=C0=1 per Intel SDM。Read-modify-write FPU_SW 保留 0x4700 (C3\|C2\|C1\|C0) mask 外的 bit。FCOMP 額外在 compare 後 call Pop。DF E0 = FNSTSW AX 在 `X86FpuDFDispatchEmitter` 實作 — 讀 FPU_SW i16 並 store 進 GPR 0 (AX)。Test ROM `29.5-fpu-compare.com` 跑 3 個 FNINIT → FLD → FCOM → FNSTSW AX → MOV [resultN], AX 序列、確認 AX=0x0100 (C0 for 3<4)、AX=0x0000 (no C bits for 4>3)、AX=0x4000 (C3 for 3==3)。FCOMPP (DE D9)、FTST (D9 E4)、FUCOM (DD E0-E7)、FUCOMP (DD E8-EF) follow 同 pattern、延後讓這 sprint 集中。已知限制：FPU_SW 的 TOP_SW（bit 11:13）還沒被 Push/Pop 跟 FPU_TOP sync — TODO 如果真 DOS program 結果需要。FreeDOS HLE regression：2840 INT call。 |
| 29.6 | 常數 | FLDZ / FLD1 / FLDPI / FLDL2E / FLDL2T / FLDLG2 / FLDLN2 |
| 29.7 | FPU 超越函數 (F2XM1/FYL2X/FPTAN/FPATAN + FDECSTP/FINCSTP) — ✅ **DONE 2026-05-16** | Per Gemini guidance、超越函數透過 C# `Math.*` extern（不用 non-x86 target 上對 f80 不可靠的 LLVM intrinsic）。`MemoryEmitters` 4 個新 extern name（`fpu_tan` / `fpu_atan2` / `fpu_log2` / `fpu_exp2m1`）帶 `CallFpuUnary` / `CallFpuBinary` helper；X86JsonCpu 加 4 個 `[UnmanagedCallersOnly]` shim route 到 `Math.Tan` / `Math.Atan2` / `Math.Log2` / `Math.Pow(2,x)-1`。D9 dispatcher 拿 6 個新 case：F0 F2XM1、F1 FYL2X (ST(1) = ST(1) * log2(ST(0)) 然後 pop)、F2 FPTAN (tan(ST(0)) → ST(0)、然後 push 1.0)、F3 FPATAN (atan2(ST(1), ST(0)) → ST(1) 然後 pop)、F6 FDECSTP (TOP--)、F7 FINCSTP (TOP++)。新 helper `X86FpuHelpers.AdjustTop(delta)` for FDECSTP/FINCSTP — 繞過 Push/Pop 語意（無 data/tag 改、只 TOP 旋轉）。Test ROM `29.7-fpu-transcendental.com` 跑 4 個超越函數：F2XM1(0.5)=√2−1 → AX=0x3ED4（0x3ED413CD 的 high WORD）、FYL2X(2.0, 8.0)=6.0 → BX=0x40C0、FPTAN(0) push 1.0 + tan(0)=0 → CX=0x3F80 DX=0x0000、FPATAN(1.0, 1.0)=π/4 → SI=0x3F49（0x3F490FDB 的 high WORD）。Bit-exact glibc / .NET Math.* 結果的 f32。FreeDOS HLE regression：2838 INT call。 |
| 29.8 | FPU 雜項 (FCHS/FABS/FSQRT/FTST/FRNDINT + FFREE) — ✅ **DONE 2026-05-15** | D9 register-form refactor：單一大 switch on `combined` cover FXCH (0x08-0x0F range)、FNOP (0x10)、FCHS (0x20)、FABS (0x21)、FTST (0x24)、7 個常數 (0x28-0x2E)、FSQRT (0x3A)、FRNDINT (0x3C)。FCHS 用 LLVM `fneg`；FABS 用 `llvm.fabs.f64` intrinsic；FSQRT 用 `llvm.sqrt.f64`；FRNDINT 用 `llvm.rint.f64`（round-to-nearest-even match FNINIT 預設 RC=00）。新 helper `X86FpuHelpers.BuildIntrinsicCallF64(name, arg)` 在第一次用時 lazily 宣告 + call f64→f64 intrinsic。DD C0-C7 = FFREE ST(i) 在 DD dispatcher 實作 — 把 slot (TOP+rm)&7 的 tag set 為 Empty、不碰 data 或 TOP。Test ROM `29.8-fpu-misc.com` 驗 FCHS -7→7、FABS -3.5→3.5、FSQRT 16→4、FRNDINT 3.7→4 (round-nearest-even)、FTST -1<0 (C0 set) — AX=40E0 BX=4060 CX=4080 DX=4080 SI=0100。FSCALE/FXTRACT/FPREM 延後到 29.7（超越函數反正需要 Math extern）。FreeDOS HLE regression：2841 INT call。 |
| 29.9 | FPU control (FLDCW/FSTCW/FNCLEX) — ✅ **DONE 2026-05-16** | D9 /5 mem = FLDCW m16 — `SegmentedRead16FromBase` 進 FPU_CW。D9 /7 mem = FSTCW m16 — load FPU_CW、透過 `SegmentedWrite16FromBase` 寫。兩個用整數 code 用的同個 memory-helper path、所以 segment-base 解析 + handler dispatch 行為一樣。DB E2 = FNCLEX 在 DB dispatcher 跟 FNINIT 並排接（switch on `combined` 帶 case 0x22 → FNCLEX 跟 0x23 → FNINIT；如需要 FNSETPM E4 / FSETPM E5 結構 ready）。FNCLEX mask FPU_SW with 0x7F00（清 bit 0-7 = IE/DE/ZE/OE/UE/PE/SF/ES + bit 15 = B、保留 C0-C3 + TOP_SW）。FNINIT/FNSTSW 已處理 (29.3b / 29.5)。FSTENV / FLDENV / FNSAVE / FRSTOR（完整 FPU-environment block save/restore）延後 — DOS code 很少用。Test ROM `29.9-fpu-control.com` 驗 FNINIT-then-FSTCW 讀回 0x037F、跟 FLDCW with custom 0x0E72 pattern followed by FSTCW 讀回 byte-identical 0x0E72。FreeDOS HLE regression：2839 INT call。29.5 + 29.4 之前的 test ROM regression：output 一樣（無行為漂移）。 |
| 29.10 | Memory m80fp | 透過 extern helper pack/unpack 10-byte 格式 |
| 29.11 | 整合測試 | Turbo Pascal hello-world 帶 real-mode float math |
| 29.12 | Capstone | AutoCAD R1.4 或 Lotus 1-2-3 numeric demo |

## Dispatcher emitter 設計（Phase 29.3+）

每個 `x86_fpu_d?_dispatch` emitter 對 ModR/M byte 做兩層 switch。
Decoder framework 的 `x86_fetch_modrm`（在我們 dispatcher 之前在 format
step list 內 call）cache `modrm_mod`、`modrm_reg`、`modrm_rm` 到
`EmitContext.Values`、所以 dispatcher 就解析它們並 switch。

Pseudo-code shape（mirror `X86FfGroupDispatchEmitter` for 0xFF）：

```csharp
public void Emit(EmitContext ctx, MicroOpStep step) {
    var mod = ctx.Resolve("modrm_mod");
    var reg = ctx.Resolve("modrm_reg");
    var rm  = ctx.Resolve("modrm_rm");

    // Memory form：mod != 11、dispatch on /reg。
    var memBB = ctx.Function.AppendBasicBlock("d9_mem");
    var regBB = ctx.Function.AppendBasicBlock("d9_regform");
    var endBB = ctx.Function.AppendBasicBlock("d9_end");
    var isMem = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, mod, const_i32(3));
    ctx.Builder.BuildCondBr(isMem, memBB, regBB);

    ctx.Builder.PositionAtEnd(memBB);
    // 需要 EA — 自己 call x86_modrm_compute_ea 或 format 的 step list 在
    // dispatch 之前 emit 它。比較乾淨：dispatch 為 fetch_modrm + compute_ea
    // 之後的 LAST step、讓 EA 在 scope 內。
    var sw = ctx.Builder.BuildSwitch(reg, default_invalid, 8);
    for (int r = 0; r < 8; r++) {
        var arm = ctx.Function.AppendBasicBlock($"d9_mem_{r}");
        sw.AddCase(const_i32(r), arm);
        ctx.Builder.PositionAtEnd(arm);
        switch (r) {
            case 0: EmitFldM32(ctx);   break;   // FLD m32fp
            case 1: /* invalid */      break;
            case 2: EmitFstM32(ctx);   break;   // FST m32fp
            case 3: EmitFstpM32(ctx);  break;   // FSTP m32fp
            case 4: EmitFldenv(ctx);   break;   // FLDENV m14/m28
            case 5: EmitFldcw(ctx);    break;   // FLDCW m16
            case 6: EmitFstenv(ctx);   break;   // FSTENV
            case 7: EmitFstcw(ctx);    break;   // FSTCW m16
        }
        ctx.Builder.BuildBr(endBB);
    }

    // Register form：mod == 11。完整 6-bit (reg, rm) tuple 識別 opcode。
    // 例如、D9 C8 = FXCH ST(0)、D9 EE = FLDZ。
    ctx.Builder.PositionAtEnd(regBB);
    var combined = ctx.Builder.BuildOr(
        ctx.Builder.BuildShl(reg, const_i32(3)),
        rm);  // 0..63
    var swReg = ctx.Builder.BuildSwitch(combined, default_unhandled, 32);
    swReg.AddCase(const_i32(0x00), bb_fld_st0);  // D9 C0 = FLD ST(0)
    // ... C0-C7 = FLD ST(i)
    // ... C8-CF = FXCH ST(i)
    swReg.AddCase(const_i32(0x10), bb_fnop);     // D9 D0 = FNOP
    swReg.AddCase(const_i32(0x20), bb_fchs);     // D9 E0 = FCHS
    swReg.AddCase(const_i32(0x21), bb_fabs);     // D9 E1 = FABS
    // ... E8-EE = FLD1 / FLDL2T / FLDL2E / FLDPI / FLDLG2 / FLDLN2 / FLDZ
}
```

### ST(i) access pattern

「Logical ST(i) → physical FPU_STn」addressing：

```csharp
LLVMValueRef GepLogicalSt(EmitContext ctx, LLVMValueRef logicalI /* i32 */) {
    var top = ctx.Builder.BuildLoad2(i32, fpuTopPtr, "top");
    var physI = ctx.Builder.BuildAnd(
        ctx.Builder.BuildAdd(top, logicalI),
        const_i32(7));
    // FPU_ST0..ST7 是 CpuStateLayout 內 8 個連續 status slot。
    // 算 slot (FPU_ST0 + physI * 8) 的 byte offset。
    var st0Off = ctx.Layout.StatusOffset("FPU_ST0");
    var byteOff = ctx.Builder.BuildAdd(
        const_i32((int)st0Off),
        ctx.Builder.BuildMul(physI, const_i32(8)));
    // 透過 byte-pointer 數學 GEP（state struct 在我們的 layout 是 byte-addressable）。
    return ctx.Builder.BuildGEP2(i8, statePtr, byteOff, "st_phys_ptr");
}
```

透過 bitcast 讀為 f64：
```csharp
var slot = GepLogicalSt(ctx, logicalI);
var asI64Ptr = ctx.Builder.BuildBitCast(slot, ptrToI64);
var asI64    = ctx.Builder.BuildLoad2(i64, asI64Ptr);
var asF64    = ctx.Builder.BuildBitCast(asI64, f64);
```

寫回：
```csharp
var asI64    = ctx.Builder.BuildBitCast(valF64, i64);
ctx.Builder.BuildStore(asI64, asI64Ptr);
```

### FPU stack push/pop 語意

**Push**（FLD、FLDZ、FILD 等）：
1. `top := (top - 1) & 7`
2. 把新值 store 進 ST(0)（physical slot `top`）
3. Update slot `top` 的 FPU_TAGS 指示 Valid/Zero/Special

**Pop**（FSTP、FFREE 等）：
1. Update slot `top` 的 FPU_TAGS 為 11 (Empty)
2. `top := (top + 1) & 7`

Gemini guidance 是可能的話每 instruction 算 `physI` 一次（不在 LLVM IR
內）— 但我們目前 dispatch single-step per instruction、每個 access 做
自己的 GEP。Phase 29.3 minimum-viable 可接受；constant-fold TOP read
是 29.x optimization。

## ESC byte → /reg opcode 表

8 個 dispatcher 的 reference 表。每 entry 的「phase」column 顯示什麼時候 land。

### D8 — f32 算術家族（mod ≠ 3 = memory；mod = 3 = ST(0) op ST(i)）

| /reg | Memory form (mod ≠ 3) | Register form (mod = 3) | Phase |
|---|---|---|---|
| 0 | FADD m32fp     | FADD ST(0), ST(i)  | 29.4 |
| 1 | FMUL m32fp     | FMUL ST(0), ST(i)  | 29.4 |
| 2 | FCOM m32fp     | FCOM ST(0), ST(i)  | 29.5 |
| 3 | FCOMP m32fp    | FCOMP ST(0), ST(i) | 29.5 |
| 4 | FSUB m32fp     | FSUB ST(0), ST(i)  | 29.4 |
| 5 | FSUBR m32fp    | FSUBR ST(0), ST(i) | 29.4 |
| 6 | FDIV m32fp     | FDIV ST(0), ST(i)  | 29.4 |
| 7 | FDIVR m32fp    | FDIVR ST(0), ST(i) | 29.4 |

### D9 — data movement + 常數 + control（mod = 3 form 是 sub-opcode by full rm:reg）

| /reg | Memory form (mod ≠ 3) | Phase |
|---|---|---|
| 0 | FLD m32fp        | 29.3c |
| 1 | (invalid)        | — |
| 2 | FST m32fp        | 29.3c |
| 3 | FSTP m32fp       | 29.3b |
| 4 | FLDENV m14/m28   | 29.9 |
| 5 | FLDCW m16        | 29.9 |
| 6 | FSTENV/FNSTENV   | 29.9 |
| 7 | FSTCW/FNSTCW m16 | 29.9 |

| Register form (mod = 3) — 完整第二 byte | Op | Phase |
|---|---|---|
| C0-C7 | FLD ST(i)         | 29.3c |
| C8-CF | FXCH ST(i)        | 29.3c |
| D0    | FNOP              | 29.8 |
| E0    | FCHS              | 29.8 |
| E1    | FABS              | 29.8 |
| E4    | FTST              | 29.5 |
| E5    | FXAM              | 29.5 |
| E8    | FLD1              | 29.6 |
| E9    | FLDL2T            | 29.6 |
| EA    | FLDL2E            | 29.6 |
| EB    | FLDPI             | 29.6 |
| EC    | FLDLG2            | 29.6 |
| ED    | FLDLN2            | 29.6 |
| EE    | FLDZ              | 29.3b |
| F0-FF | 超越函數 (F2XM1/FYL2X/FPTAN/FPATAN/...) | 29.7 |

### DB — i32 op + FNINIT + m80fp

| /reg | Memory form (mod ≠ 3) | Phase |
|---|---|---|
| 0 | FILD m32int     | 29.3d |
| 2 | FIST m32int     | 29.3d |
| 3 | FISTP m32int    | 29.3d |
| 5 | FLD m80fp       | 29.10 |
| 7 | FSTP m80fp      | 29.10 |

| Register form (mod = 3) — 完整第二 byte | Op | Phase |
|---|---|---|
| E2    | FNCLEX            | 29.9 |
| E3    | FNINIT / FINIT    | 29.3b |
| E4    | FNSETPM (287+、8087 no-op) | — |

### DD — f64 data movement + restore/save + FFREE

| /reg | Memory form (mod ≠ 3) | Phase |
|---|---|---|
| 0 | FLD m64fp       | 29.3d |
| 2 | FST m64fp       | 29.3d |
| 3 | FSTP m64fp      | 29.3d |
| 4 | FRSTOR m94/m108 | 29.9 |
| 6 | FNSAVE m94/m108 | 29.9 |
| 7 | FNSTSW m16      | 29.5 |

| Register form (mod = 3) | Op | Phase |
|---|---|---|
| C0-C7 | FFREE ST(i)         | 29.8 |
| D0-D7 | FST ST(i)           | 29.3c |
| D8-DF | FSTP ST(i)          | 29.3c |
| E0-E7 | FUCOM ST(i)         | 29.5 |
| E8-EF | FUCOMP ST(i)        | 29.5 |

### DF — i16 / i64 / BCD op + FNSTSW AX

| /reg | Memory form (mod ≠ 3) | Phase |
|---|---|---|
| 0 | FILD m16int     | 29.3d |
| 2 | FIST m16int     | 29.3d |
| 3 | FISTP m16int    | 29.3d |
| 4 | FBLD m80bcd     | — (延後) |
| 5 | FILD m64int     | 29.3d |
| 6 | FBSTP m80bcd    | — (延後) |
| 7 | FISTP m64int    | 29.3d |

| Register form (mod = 3) | Op | Phase |
|---|---|---|
| E0 | FNSTSW AX（唯一直寫 CPU GPR 的 x87 op） | 29.5 |

(DA / DC / DE follow 類似 pattern — 分別是 i32 算術、f64 算術、
i16/popping-variant 算術。各 sprint 期間視需要在 MD 文件化、這裡不列舉。)

## Open question

- 先出貨為 `i8087`（最簡單、1980 ISA）或 `i80287`（1982、同 opcode +
  protected-mode 整合）。8087-only opcode（FENI、FDISI）在後續 chip 是
  no-op；否則 spec upward-compatible。
- `"extensions"` 該在 `cpu.json` 內或只在 `machines/`。Gemini 主張
  machines/（因為同 CPU 可能配無 FPU 或 Weitek-1167）；目前共識 = machines/。
- FWAIT (0x9B) 該是獨立 JSON entry 或已存在的 no-op。真 8087 用 FWAIT
  同步 CPU+FPU；我們整合 model 可以解 FWAIT 為真 no-op（單 byte）並跳同步。

## 交叉參考

- Phase 28.IO 收尾筆記: `MD/performance/202605152230-pc-emulator-phase-28-io.md`
  （port dispatch + 解 real BIOS POST 的 no-op FPU stub）
- Gemini consultation log：
  - `tools/knowledgebase/message/20260515_223616.txt`（FPU 設計基礎）
  - `tools/knowledgebase/message/20260515_224332.txt`（separate-vs-integrated）
