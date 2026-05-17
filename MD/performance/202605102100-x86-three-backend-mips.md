# Intel 8086 backend MIPS 比較（2026-05-10）

修完 silent block-JIT-degenerate-to-per-instr bug（commit `24a7dc8`）後，
對三個 Apr-X86 backend 第一次的 MIPS 量測。

## Workload

`test-roms/x86/bench-loop.com` — 合成的 outer-loop test，18 byte：

```
mov bx, 100        BB 64 00       ; outer counter
outer_top:
  mov cx, 0xFFFF   B9 FF FF       ; inner counter
inner_top:
  add ax, 1        05 01 00       ; 2 ALU ops in inner body
  add ax, 2        05 02 00
  loop inner_top   E2 F8          ; CX-=1, jump if CX!=0
  dec bx           4B
  jnz outer_top    75 F2
hlt                F4
```

每個 outer iter：1（mov cx）+ 65535 × 3（add/add/loop）+ 1（dec）+
1（jnz）= 196,608 個 architectural instruction × 100 outer = **19,660,801**
inst total + 1（最後 hlt）= **19,660,802 個 architectural instruction**。

## 結果（3 次 run 取最佳，Release build）

| Backend       | Wall ms | Startup ms | Net ms | Net MIPS | vs legacy |
|---------------|---------|-----------|--------|---------:|----------:|
| `legacy`      |     574 |        63 |    511 |    38.48 |    1.00×  |
| `json`        |    3872 |      1179 |   2693 |     7.30 |    0.19×  |
| `json-block`  |    1212 |       489 |    723 |    27.19 |    0.71×  |

## 解讀

**Startup baseline**（單 byte HLT-only ROM，只有 .NET CLR + LLVM init）：
- `legacy` 63 ms — dotnet CLR + assembly load
- `json` 1179 ms — 加 LLVMSharp init + SpecCompiler 透過 ORC LLJIT eager
  compile 全部 ~256 個 opcode function
- `json-block` 489 ms — 加 LLVM init 但 defer compile 到第一個 block
  cache miss；便宜是因為只有這個 workload 碰到的少數 unique block 才被 compile

**Net throughput**：
- `legacy` 38 MIPS — 手刻 C# switch dispatch、無 JIT overhead
- `json` 7 MIPS（比 legacy 慢 5.3×）— per-instruction LLVM call overhead 主導。
  每個 Step() = (managed → native fn ptr → managed) trampoline 加上每個
  register touch 的 state-buffer load/store。
- `json-block` 27 MIPS（比 legacy 慢 1.4×、比 `json` 快 3.7×）— alloca +
  mem2reg 把 register state 提升到 block 內的 LLVM SSA value；
  cross-instruction transition 是純 LLVM IR 而不是 managed-native trampoline。

**Block-JIT batching**：inner loop body（add/add/loop）變成一個 block。
每個 Step() 跑一次那 3-instr block（LOOP iter 回去、set PcWritten、退出
block — 下個 Step 重新進入）。Step-to-instruction 比例：
`19,660,802 / 6,553,500 = 3.0 instr/Step`。

## 要拉近 legacy 差距需要什麼

依 Gemini 2026-05-10 architectural review（見 commit `74d27a4`）：

1. **把 immediate bake 成 IR constant**（24.6.8d 未來工作）—
   把 `x86_fetch_imm8`/16 裡的 runtime `memory_read_8` 呼叫換成 decode
   時 capture 的 `LLVM ConstantInt` literal。消除 immediate 的 per-instruction
   trampoline + bus dispatch。簡單的 ~2× 提升。

2. **Cross-jump follow back-edge**（LR35902 的 `BlockDetector` 對 JR/JP
   已經做過 — 移植到 x86 LOOP/JMP rel8 with constant target）。讓 tight
   loop body compile 成一個大 unrolled block，而不是每次 LOOP 從頭。
   我們 bench 的 block 大小可以從 3 推到 65535 instruction。

3. **CPU state shadow 跨 block promote**（Phase 7 plan 的 P1 #5）。
   目前 register 在每個 block exit 都 flush 回 state buffer。Hot path
   上把它們維持在 LLVM SSA 會關掉剩下的大部分差距。

這些都是 framework extension、所有 CPU 都受惠（NES 已經有一些）、不只是 x86。
合理的 next-quarter target。

## 重現

```
dotnet build -c Release src/AprX86.Cli/AprX86.Cli.csproj
pwsh tools/bench_x86.ps1
```

Bench script 自動把 `.com` ROM 寫進 `test-roms/x86/bench-loop.com`、
baseline 寫進 `temp/baseline-hlt.com`。
