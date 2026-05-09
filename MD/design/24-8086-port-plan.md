# 8086 移植計畫 — 最低環境 CPU 驗證 + 截圖證明

> **Status**：**IN PROGRESS**（2026-05-10）— 24.0–24.5 完工 (13 commits, 6
> paper-quality screenshots, 1.31M Tom Harte SST cases 全綠)；
> 24.6 JSON-driven port: **24.6.1–24.6.5d 完工** (9 commits, 30/30
> JsonCpu tests + 703/703 T1，full MOV through framework — NOP/HLT/
> reg-imm/reg-direct/memory ModR/M + 4 segment-override prefixes)；
> 24.6.5e–g + 24.6.6–9 待做。
>
> **Trigger**：第 4 顆 CPU 候選 = Intel 8086（用以前寫的 Apr86 emulator
> 當 reference oracle 的部分）。要解決的核心問題：8086 是 CISC、segmented
> memory、ModR/M、需要 PC 周邊環境才能跑大部分軟體 — 怎麼用最低成本只
> 驗證 CPU correctness，同時產出**有說服力的截圖證明**？
>
> **核心觀念**：截圖是 framework 通用性 claim 的最有力證據（mirror NES /
> GBA / GB 三顆既有 CPU 的截圖路線）。**沒有截圖 = 沒有 paper-quality
> demo**。
>
> **重要更新 (2026-05-10)**：v1 phase plan 漏掉了「JSON-driven port」這
> 個關鍵 sub-phase。目前 (24.5 末) 的狀態是 **legacy backend 完整 + 截圖
> 完整**，但 8086 還沒走 framework 的 SpecCompiler + LLVM JIT pipeline —
> 那是 24.6 phase 要做的事，是 framework genericity 真正成立的關鍵。
>
> **目標讀者**：(a) 真要開工 8086 port 的人；(b) 跟接手者 / 學術
> peer 解釋「framework 真的支援 4 顆 CPU」時的 visual evidence 來源
> 設計依據。

---

## 1. 三個方法支柱

跟 NES / GBA / GB 同樣的「驗證金字塔」，套到 8086：

| 支柱 | 對應 NES | 8086 對應 | 用途 |
|---|---|---|---|
| **Per-opcode oracle test** | nestest + blargg 三 backend | **Tom Harte SingleStepTests 8088_v2** | 純 CPU correctness — 256 opcode × 10k tests/opcode = 數百萬 case |
| **Synthetic test ROM** | nes test ROMs (.nes) | **Hand-crafted .com (raw 8086 binary)** | 端到端執行驗證，不需要 BIOS / 周邊 |
| **Visual screenshot** | NesPpu → PNG (`--screenshot=`) | **CGA text mode 80x25 → PNG (`--screenshot=`)** | 視覺證明 framework 真的能跑 8086 |

---

## 2. Apr86 現況參考（OldProject/Apr86）

> 已 clone 在 `OldProject/Apr86/`（commit 之前的整理）。

| 元件 | Apr86 狀態 | 對 AprX86 移植的意義 |
|---|---|---|
| **CPU 核心** | `Apr8086Core` 3139 行 single-file，261 個 opcode case | 行為 reference；**不是 oracle**（自己有 bug — 見 §2.1） |
| **1MB PA_mem** | byte[0x100000] linear，segmented (seg<<4)+offset | **直接借用結構** |
| **CGA text 80x25** | 已實作！256 個 PNG glyph (8x14)、16-color palette、Parallel.For 渲染 | **font 跟 palette 都直接拿來用** |
| **System BIOS load** | 0xFFFF0 area；VGA BIOS 0xC0000 | AprX86 不走這條（不需要真 BIOS） |
| **IO port** | `io_step.dat` 預錄真 PC trace 回放（hack） | **不採用** — 不是真驗證、是 bypass |
| **Interrupt** | push FLAGS/CS/IP + 取 vector，但作者標 "unfinish" | 從頭做（CPU 本身的 INT 指令，不靠周邊） |

**結論**：Apr86 的 **1MB memory 結構 + CGA font + 16-color palette** 直接
拿來用；**CPU core / IO replay** 不採用，從 framework 重做。

### 2.1 Apr86 已知 CPU bug 清單（移植時順便修）

掃一輪 source 後找到的 bug — Tom Harte SST 會 catch 全部，移植到
AprX86 時直接寫對：

| # | 位置 | Bug | 修正 |
|---|---|---|---|
| 1 | `CPU.cs:1247-1255` AAM (0xD4) | `Reg_IP++; ... Reg_A.L / 10` — 跳過 imm8 base byte，hardcoded 10 | `byte b = Mem_CS_r8(Reg_IP++); ... Reg_A.L / b` |
| 2 | `CPU.cs:1257-1268` AAD (0xD5) | 同上，imm8 base 被忽略 | 同上 |
| 3 | `CPU.cs:1265` AAD flag_S | 用 `& 0x8000` 但結果是 8-bit AL，永遠 false | `& 0x80` |
| 4 | `CPU.cs:1644` IMUL byte | `(short)modreg_ReadTool()` zero-extend；應 sign-extend from byte | `(short)(sbyte)modreg_ReadTool()` |
| 5 | `CPU.cs:1623, 1668` MUL/IMUL CF/OF | 用 `Reg_D.X > 0` 判斷；IMUL 應該是「upper half ≠ sign extension of lower half」 | 對 IMUL: `cf = of = (DX != (AX bit15 ? 0xFFFF : 0))` |
| 6 | `Interrupt.cs:25` | 作者自註 "interrupt unfinish !"；flag_T/I 處理疑慮 | 從頭做，照 8086 datasheet |
| 7 | `IO.cs` (全 4 函數) | IO replay 從 `io_step.dat` 回放，非真 emulation | AprX86 用 magic ports (OUT 0xE9 / 0xF4) + ignore 其他 |
| 8 | `MEM.cs:53` | `0x410 hack patch return 0x41` — BIOS Equipment Word 暴力 bypass | AprX86 不需要真 BIOS，這個 hack 自動消失 |

**移植策略**：AprX86 的 8086 spec + emitter **不參考 Apr86 的有 bug 段
落**（AAM/AAD/IMUL/MUL flag）；那些直接從 8086 datasheet + Tom Harte SST
expected output 對著寫。Apr86 的非 buggy 段落（基本 ALU、ModR/M decoder、
segment override 處理）可以對著看當作 implementation hint。

---

## 3. 支柱 #1 — Tom Harte SingleStepTests 8088_v2

### 3.1 來源
- GitHub: https://github.com/SingleStepTests/8088_v2
- 業界用過：PCjs / 8086tiny / FreeDOS / 多個現代 8086 emulator
- 免費 / open licence

### 3.2 結構
每個 opcode 一個 JSON 檔，每個檔 ~10000 個 test case。一個 case：

```json
{
  "name": "01.0",
  "bytes": [0x01, 0xC8],     // ADD AX, CX
  "initial": {
    "regs": { "ax": 0x1234, "cx": 0xABCD, "ip": 0x0100, ... },
    "ram": [[0x100, 0x01], [0x101, 0xC8], ...]
  },
  "final": {
    "regs": { "ax": 0xBE01, "cx": 0xABCD, "ip": 0x0102, ... },
    "ram": [...]
  },
  "cycles": [...]
}
```

### 3.3 Test runner 設計

```csharp
public class TomHarteTestRunner
{
    public TestResult Run(string opcodeJsonPath, IX86Cpu cpu) {
        foreach (var test in LoadTests(opcodeJsonPath)) {
            cpu.LoadState(test.Initial);
            cpu.Step();   // execute exactly one instruction
            var diff = DiffState(cpu.Snapshot(), test.Final);
            if (diff.HasDiffs) return Fail(test, diff);
        }
        return Pass();
    }
}
```

### 3.4 Phased coverage 策略

| Phase | 範圍 | 預期 fail rate（剛開始） |
|---|---|---|
| 24.A.1 | 基本 ALU (ADD/SUB/AND/OR/XOR) | 高 — flag 先行 stress test |
| 24.A.2 | Data movement (MOV / LEA / XCHG / PUSH / POP) | 中 — addressing mode 全覆蓋 |
| 24.A.3 | Control flow (Jcc / CALL / RET / LOOP) | 低 — segmented JMP 需 careful |
| 24.A.4 | String ops (MOVS / CMPS / SCAS / LODS / STOS + REP prefix) | 中 — REP loop 跟 flag interaction |
| 24.A.5 | MUL / DIV / IMUL / IDIV | 高 — flag 後遺症，DIV exception |
| 24.A.6 | BCD ops (DAA / DAS / AAA / AAS / AAM / AAD) | **極高** — corner case 集中地，Gemini 也警告 |
| 24.A.7 | INT / IRET / segment override prefix | 中 — segmented flag handling |
| 24.A.8 | Shift / rotate (SAL/SAR/SHL/SHR/RCL/RCR/ROL/ROR) | 中 — count masking, flag cases |

**目標**：256 opcode 全綠。**沒有 display 也能跑這層**。

---

## 4. 支柱 #2 — Magic IO + CGA framebuffer

### 4.1 Magic IO port

```
OUT 0xE9, AL   →  emulator 把 AL 印到 stdout (用 Bochs port 慣例)
OUT 0xF4, AL   →  emulator 收到 AL=0 即 PASS-exit，AL≠0 即 FAIL-exit (qemu isa-debug-exit 慣例)
```

兩個 port 借既有業界慣例，未來如果想跟 qemu / Bochs 對拍直接通用。

### 4.2 CGA text mode 0xB8000 framebuffer

8086 / DOS 程式寫文字到 0xB8000（80x25 char/attr pair, 4000 bytes total）。
emulator 不需要實作 INT 10h，**程式直接寫記憶體**：

```asm
; 在 row 12, col 30 寫白底紅字 'A'
mov ax, 0xB800
mov es, ax
mov word [es:12*160 + 30*2], 0x4F41   ; attr=0x4F (red bg, white fg), char='A'
```

framebuffer 一直存在 emulator memory；`--screenshot=out.png` flag 觸發
時 walk 80×25 → 渲染 PNG。

### 4.3 截圖 PNG 渲染演算法

```csharp
public void RenderTextModeScreenshot(byte[] mem, string outPath)
{
    const int CELLS_W = 80, CELLS_H = 25;
    const int FONT_W = 8, FONT_H = 16;       // CGA standard 8x16 font
    const int IMG_W = CELLS_W * FONT_W;     // 640
    const int IMG_H = CELLS_H * FONT_H;     // 400

    using var bmp = new Bitmap(IMG_W, IMG_H);
    for (int y = 0; y < CELLS_H; y++)
    for (int x = 0; x < CELLS_W; x++)
    {
        int cellOff = 0xB8000 + (y * CELLS_W + x) * 2;
        byte ch    = mem[cellOff];
        byte attr  = mem[cellOff + 1];
        Color fg = CgaPalette[attr & 0x0F];
        Color bg = CgaPalette[(attr >> 4) & 0x07];   // bit 7 = blink, ignore
        DrawGlyph(bmp, ch, x * FONT_W, y * FONT_H, fg, bg);
    }
    bmp.Save(outPath, ImageFormat.Png);
}
```

`CgaPalette[16]` 標準 IBM CGA 16-color values（Apr86 既有 `TableColor`
數值正確，直接抄）：
```
0x000000 0x0000aa 0x00aa00 0x00aaaa 0xaa0000 0xaa00aa 0xaa5500 0xaaaaaa
0x555555 0x5555ff 0x55ff55 0x55ffff 0xff5555 0xff55ff 0xffff55 0xffffff
```

`DrawGlyph` 從 256-glyph CGA font 取 8x16 bitmap → 對每 pixel 寫 fg / bg。
Font 來源：
- **首選**：Apr86 既有 `OldProject/Apr86/Apr8086/ASII_FONT/*.png`（256 個
  PNG，作者已 dump 好）
- 備選：IBM PC ROM Font (free，GitHub `viler-int10h/vga-text-mode-fonts`
  之類)

### 4.4 為什麼 CGA 80x25 text mode 夠

- BIOS POST 畫面、DOS prompt、大部分 8086-era utility 都是這個 mode
- 視覺辨識度 100%（"PC BIOS / DOS look"，paper / talk 一秒看出是什麼）
- 不需要 graphics mode 那些細節（Mode 4 / 6 / 13h / planar）
- 對 CPU correctness 驗證，**文字輸出 = 計算結果輸出**，已足

未來如果要做 graphics mode demo（Mandelbrot 320x200 之類）— 是 phase E
nice-to-have，不在初版範圍。

---

## 5. 支柱 #3 — Hand-crafted .com test ROMs

### 5.1 載入慣例

借 CP/M `.com` 慣例：raw binary 載到 `0x0000:0x0100`（segment 0，offset
0x100）；emulator 設 CS:IP = 0:0x100，DS / ES = 0，SS:SP = 0:0xFFFE，
**不需要任何 BIOS / 周邊**。

```
apr-x86 --rom=test-roms/8086/hello-cga.com --screenshot=temp/hello-cga.png
```

收尾條件：
- 程式跑 `HLT` 指令 → emulator 偵測到就 stop + 截圖
- 或 `OUT 0xF4, AL` (qemu isa-debug-exit pattern) → 即時退出
- Or `--max-cycles=N` 上限保險

### 5.2 候選 test ROM 清單（按複雜度）

| ROM | 內容 | 視覺 | 驗證重點 |
|---|---|---|---|
| **hello-cga.com** | "Hello, AprX86 8086!" 寫 0xB8000 + HLT | 文字白底藍字置中 | basic ModR/M / segment / HLT |
| **primes-100.com** | 質數篩 < 100 印出 | 多行數字輸出 | 迴圈 / 算術 / DIV / 比較 |
| **factorial-12.com** | 12! 跨 32-bit 算 + 印 | 一個大數字 | MUL 16-bit / 32-bit accumulate |
| **fibonacci.com** | 前 20 個 Fibonacci 數印 | 表格 | 遞迴 / SP 操作 / CALL/RET |
| **bcd-demo.com** | BCD 加法（DAA / AAA） | 結果 + flag dump | DAA/AAA flag corner cases |
| **string-copy.com** | MOVSB + REP 把 source 複製到 dest | 兩段相同字串 | REP prefix + ES:DI / DS:SI |
| **mandelbrot-ascii.com** | 用 ASCII char (40x20) 印 Mandelbrot set | 圖案 | 大量算術 + 巢狀迴圈 |
| **interrupt-demo.com** | INT 0x03 (debug) → 自定 handler 寫文字 → IRET | 文字行 | INT push 順序、IRET pop 順序、flag I 處理 |
| **80186-enter-leave.com** | 80186 ENTER/LEAVE 指令 demo | "ENTER OK" | inheritance 第一個 child spec 的證明 |

每個 .com 用 NASM 編：
```bash
nasm -f bin hello-cga.asm -o hello-cga.com
```

預期單檔 < 256 bytes。

---

## 6. 跟 spec inheritance (doc #23) 的關係

8086 port 是 **doc #23 inheritance 機制的第一個真實 stress test**：

| Phase | 跟 #23 對應 |
|---|---|
| 24.A 寫 8086 base spec | **#23 phase 23.1**（base spec，不開 inheritance） |
| 24.A 用 Tom Harte 驗 base | base spec 對齊 reference；inheritance 動工前先有 known-good base |
| 24.B 加 inheritance + 寫 80186 | **#23 phase 23.2-23.3**；80186 ENTER/LEAVE demo 截圖證 inheritance work |
| 24.C 80286 real-mode | **#23 phase 23.4** |
| 24.D 80286 protected-mode | **#23 phase 23.5** |
| 24.E (optional) 8087 FPU as trait | **#23 phase 23.6**；trait composition 第一個範例 |

每個 family 子 CPU 都用同一條 截圖驗證 pipeline (CGA text mode + Tom
Harte tests if 有 8088 跟 80186 / 80286 的 SST)。

---

## 7. 範圍明確排除（v1 不做）

避免 over-scope：

| 項目 | 不做的理由 |
|---|---|
| **真 BIOS LLE** | Apr86 卡這條；對 CPU correctness 沒貢獻；對截圖也只是延後（CGA text mode 直接寫就有截圖） |
| **PIC (8259) / PIT (8254) / DMA (8237)** | 跟 CPU correctness 完全不相關；測試 ROM 不靠這些 |
| **CGA graphics modes (4 / 6 / 13h)** | 文字 mode 已夠視覺證明；graphics mode 是 nice-to-have，等 8086 base 完工再說 |
| **VGA planar modes (Mode 0Dh/10h/12h)** | 跟 8086-era unrelated；286/386 才開始普及 |
| **音訊 (PC Speaker / SB)** | 跟視覺證明跟 CPU correctness 都無關 |
| **Real DOS / FreeDOS** | 需要完整 BIOS + filesystem + 數百 INT 21h functions；100x scope |
| **8087 FPU** | trait 機制（doc #23）已預留；初版 8086 不裝 |
| **Multi-cycle exact timing** | 對 CPU correctness 加分有限；Tom Harte 已給 cycle count，但不該為了 cycle 完美延期 ROM 驗證 |

---

## 8. Phase plan

> **目前累計**（2026-05-10）：22 個 commits（24.0 → 24.5：13 + 24.6.1 →
> 24.6.5d：9）；T1 baseline 703/703 全綠；JSON-driven backend 已覆蓋 28
> opcodes + 4 個 segment-override prefix（NOP + HLT + B0-BF MOV r,imm
> 16 個 + 88-8B MOV r/m↔r 4 個全 ModR/M + 0x26/0x2E/0x36/0x3E 全 4 個
> segment override prefixes）。剩 24.6.5e–g + 24.6.6–9 + 24.7 + 24.8。

| Phase | 內容 | 成果 | 狀態 | Commit / 紀錄 |
|---|---|---|---|---|
| **24.0** | 本 doc 落地 | DRAFT → APPROVED | ✅ | `eb82331` |
| **24.1** | AprX86.Cli skeleton + 1MB memory + CS:IP fetch loop（legacy backend） | hello-cga.com 跑得起來但只 stub Step() | ✅ | `8b9a4ba` |
| **24.2.1** | ModR/M decoder + EA computation + register-by-encoding accessors | 18 unit tests，所有 mod×r/m combo 覆蓋 | ✅ | `6ab3f4c` |
| **24.2.2** | MOV 全 forms + segment override prefix | 12 MOV unit tests + smoke ROM | ✅ | `f81581a` |
| **24.2.3** | ALU + 完整 9-flag computation (ADD/SUB/AND/OR/XOR/CMP/ADC/SBB) | 32 ALU unit tests + 32-bit ripple add demo | ✅ | `0c8176b` |
| **24.2.4** | Tom Harte SST 8088 v2 test runner + 第一個 opcode 全綠 | 23 op × 10k cases = 230k SST validations | ✅ | `7d70610` |
| **24.3** | CGA text framebuffer + PNG 截圖 + magic IO | result/x86-16/hello-cga-i8086.png 第一張截圖 | ✅ | `3433fd3` |
| **24.4.1** | PUSH/POP/INC/DEC/XCHG（28 ops） | T1 568, 510k SST cases (8086 SP quirk fixed) | ✅ | `6faac55` |
| **24.4.2** | Control flow JMP/JCC/CALL/RET/LOOP/JCXZ（32 ops） | T1 600, 830k SST cases | ✅ | `cf5dd8c` |
| **24.4.3** | Shifts/rotates groups D0-D3 + flag manipulation（28 ops） | 1.03M SST; 8088 silicon AF/OF rules reverse-engineered | ✅ | `538877f` |
| **24.4.4** | TEST/NOT/NEG/MUL/IMUL（12 ops；DIV/IDIV functional but SST-deferred） | 1.15M SST; 8088 MUL high-byte flag quirk decoded | ✅ | `223ff47` |
| **24.4.5** | String ops MOVSB/CMPSB/SCASB/LODSB/STOSB（10 ops）+ REP/REPNE prefix | 1.17M SST | ✅ | `44220a7` |
| **24.4.6** | INT/IRET/INTO + CBW/CWD + IN/OUT + BCD（functional） | 1.31M SST cases (BCD silicon-quirk SST deferred) | ✅ | `0927832` |
| **24.5** | 5-10 個 hand-crafted demo + 截圖 | 6 paper-quality screenshots: hello-cga / primes / fibonacci / mandelbrot / string-copy / factorial | ✅ | `a897276`, `acf685a` |
| **24.6** | **JSON-driven port** — `spec/x86-16/i8086/cpu.json` + groups + `X86_16Emitters.cs` (LLVM IR) + `X86JsonCpu` per-instr / block-JIT；三 backend (legacy / json / json-block) 全綠 Tom Harte | 8086 真正成為 framework 第 4 顆 CPU；同 pipeline 跑 | ⏳ partial | (子項見下) |
| 24.6.1 | Spec scaffolding：`spec/x86-16/i8086/{cpu.json,main.json}`，8 GPRs in ModR/M order，9-flag FLAGS，4 segment regs + IP + HALTED | SpecLoader test green | ✅ | `4730945` |
| 24.6.2 | Smoke group：NOP (0x90) + HLT (0xF4) decode through DecoderTable | 4 decode tests | ✅ | `9a43c73` |
| 24.6.3 | `X86_16Emitters.cs` scaffolding：family dispatch + halt emitter + helpers (FetchImm8/16, SegmentedRead/Write8/16, ReadGpr8/16, WriteGpr8/16, byte-half preservation rule) | 5 spec compile tests | ✅ | `24e5e70` |
| 24.6.4 | `X86JsonCpu` per-instr backend：SpecCompiler → ORC LLJIT → live fn pointers, NOP/HLT roundtrip, State getter/LoadState | 7 e2e tests | ✅ | `01cdeb3` |
| 24.6.5a | MOV r, imm (B0-BF) — 16 opcodes through field-dispatched write_reg{8,16} + fetch_imm{8,16} | 5 tests | ✅ | `1e992a3` |
| 24.6.5b | MOV r/m, r 與 r, r/m (88-8B) — fetch_modrm + read_reg{8,16}_field with mod=11 register-direct only | 5 tests | ✅ | `06d9dab` |
| 24.6.5c | Memory ModR/M — `x86_modrm_compute_ea` (full 8086 EA grammar：BX+SI/BP+disp/disp16/etc.) + `x86_modrm_load/store_w{8,16}` mod-aware emitters | 7 mem tests | ✅ | `384eca5` |
| 24.6.5d | Segment override prefixes (0x26 ES / 0x2E CS / 0x36 SS / 0x3E DS) — C# Step() 在 dispatch 前消化 prefix 並寫入 SEG_OVERRIDE state slot；EA emitter 在計算完 default ea_seg 後若 override active 則替換為對應 segment 值；one-shot per instruction | 6 segment-override tests；last-prefix-wins；mod=11 reg-direct 不受影響 | ✅ | `d8e8f68` |
| 24.6.5e | MOV r/m, imm (C6/C7) + MOV moffs (A0-A3) + MOV sreg/r,r (8C/8E) | 7 opcodes；reuses ModR/M + segment-reg slot routing | ⏳ | — |
| 24.6.5f | PUSH/POP r16 (50-5F) + PUSH/POP sreg + PUSHF/POPF + PUSH r/m (FF /6) + POP r/m (8F) | 22 opcodes；SS:SP push/pop helper + 8088 PUSH SP quirk | ⏳ | — |
| 24.6.5g | XCHG r/m,r (86/87) + XCHG AX,r16 (90-97) + LEA (8D) + LDS/LES (C4/C5) | 12 opcodes；LEA 用 EA 但不 load | ⏳ | — |
| 24.6.6 | ALU group — ADD/OR/ADC/SBB/AND/SUB/XOR/CMP across 6 forms × 8-bit/16-bit + 9-flag IR computation (CF/PF/AF/ZF/SF/OF rules in LLVM IR mirroring `X86Alu.cs`) | ~80 opcodes；Tom Harte SST 8088 v2 ALU 子集全綠 | ⏳ | — |
| 24.6.7 | Control flow (JMP/Jcc/CALL/RET/LOOP/JCXZ) + shift/rotate (D0-D3) + string ops + REP prefix + INT/IRET + BCD + IO | ~120 opcodes；Tom Harte 1.31M 全綠 through json backend | ⏳ | — |
| 24.6.8 | Block-JIT mode — alloca + mem2reg + IR-level cycle budget (à la N1.B' for NES) | 三 backend (legacy / json-instr / json-block) 同步 | ⏳ | — |
| 24.6.9 | Re-run 24.5 demos through json-block backend | result/x86-16/jit-*.png 與 legacy pixel-identical 6 張新截圖 | ⏳ | — |
| **24.6b** | (optional) Lockstep diff legacy vs Apr86（限 .com 程式範圍） | Apr86 reference cross-check | ⏳ | — |
| **24.7** | 80186 spec — 透過 inheritance (#23) | ENTER/LEAVE demo + result/x86-16/enter-leave-i80186.png | ⏳ | — |
| **24.8** | 80286 real-mode + protected-mode demos | 4 顆 CPU 全綠 + result/x86-16/protmode-msr-i80286.png | ⏳ | — |

**重要 update (2026-05-10)**：phase 24.6 在 doc 原版 v1 裡漏掉 **「JSON-driven
port」** 這個關鍵 sub-phase — 直接從 24.5 demo 跳到 24.7 inheritance 是錯
的，因為 inheritance 機制（doc #23）要 base 是 JSON spec 才能對。原 24.6
標的「lockstep vs Apr86」改放在 24.6b（optional）。

**進度**：phase 24.0–24.5 全部 ✅ 完成（13 commits + 6 screenshots + 1.31M
Tom Harte SST cases 全綠）。**legacy backend 已是「8086 emulator 並排存
在」狀態**，但要讓 8086 真正成為 framework 第 4 顆 CPU、走同一條 SpecCompiler
+ LLVM JIT pipeline，**24.6 JSON-driven port 是必做的下一步**。

**24.6 進度 (2026-05-10)**：sub-phase **24.6.1 → 24.6.5d 完成** — JSON-driven
8086 已能透過 framework SpecCompiler + LLVM JIT 跑 NOP / HLT / 全套 MOV
(B0-BF reg/imm + 88-8B reg-to-reg + 88-8B 全 ModR/M memory grammar) +
4 個 segment-override prefix（ES/CS/SS/DS，one-shot, last-wins）。
703/703 T1 tests 全綠。剩 24.6.5e-g + 24.6.6-9 = 雜項 MOV / PUSH-POP /
XCHG-LEA / ALU / 控流 / 移位 / 字串 / block-JIT / demo 重跑。

**最早可截圖 milestone**：**24.3 結束**（hello-cga.com → PNG）— 已達成。
**最早 paper-quality milestone**：**24.5 結束**（6 個 demo 截圖 + Tom Harte
1.31M SST 全綠）— 已達成。
**Framework genericity milestone (partial)**：JSON-driven 8086 backend 已
存在並能跑 MOV-only 子集；剩 ALU / 控流 / 移位 / 字串 ops 補完後 = 真正
透過框架驗證 8086。

---

## 9. 截圖 paper-quality 目標 examples

最終要產出的「8086 framework demo」截圖集合：

```
result/x86-16/
├── hello-cga-i8086.png              ← 「Hello AprX86」基本驗證
├── primes-i8086.png                 ← 質數列表
├── mandelbrot-ascii-i8086.png       ← 用 ASCII 字符畫 Mandelbrot set
├── fibonacci-i8086.png              ← Fibonacci 數列
├── enter-leave-i80186.png           ← 80186 ENTER/LEAVE 證明 inheritance work
└── protmode-msr-i80286.png          ← 80286 LMSW/SMSW protected mode setup demo
```

這 6 張截圖跟既有的：
- `result/gb/json-llvm/cpu_instrs.png`（Blargg 11/11）
- `result/gba/bios_lle_arm.png`（jsmolka ARM）
- `result/gba/bios_lle_thumb.png`（jsmolka Thumb）
- `result/nes/nestest.png`（assumed — 之後加）

**4 顆 CPU + 多個 ISA mode + inheritance proof of concept**，就是 framework
完整通用性的視覺證據集。

---

## 10. 風險 + Mitigation

| 風險 | 嚴重度 | Mitigation |
|---|---|---|
| **Tom Harte tests 解析複雜 / API mismatch** | 中 | 先驗一個 opcode (ADD) 通了再規模化；JSON parsing 是已知技術 |
| **8086 ModR/M 解碼難度** | 高 | Apr86 既有 ModR/M code 拿來看；Tom Harte 給的 trace 含解碼後 effective addr，可比對 |
| **DAA / AAA 等 BCD 卡關** | 中 | 預期會卡幾天；Tom Harte 給 corner case 全覆蓋，逐一 fix |
| **Font / palette 版權** | 低 | Apr86 既有 PNG 已 dump（IBM PC ROM 1981 font 早超 copyright），或用 GPL'd vga-text-mode-fonts |
| **Inheritance 機制設計沒驗到** | 中 | 必須先做完 8086 base + Tom Harte 全綠（24.4），才開始 80186 (24.7) — 不要兩件事一起做 |
| **過度 over-scope（想做 graphics / 真 BIOS）** | 高 | 嚴守第 7 節「不做」清單；想做 graphics 等 24.5 完工後再開新 phase |

---

## 11. 跟既有 doc 的關係

- **#20 adding-a-new-cpu.md** — 8086 是第 4 顆 CPU 的 SOP execution；本 doc 是針對 8086 的具體 phase plan
- **#23 cpu-spec-inheritance.md** — 提供 inheritance 機制；本 doc phase 24.7+ 是 inheritance 的 stress test
- **既有 NES / GBA / GB 截圖慣例** — 本 doc CGA framebuffer + PNG 渲染走同一路徑；未來 paper / framework demo 截圖集合擴張到 4 CPU
- **既有 lockstep diff toolkit (N5)** — phase 24.6 的可選 cross-check 機制
