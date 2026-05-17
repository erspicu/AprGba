# DOS test-binary 注入流程（Phase 30.14）

如何用 NASM 寫一個小 test program、塞進 running FreeDOS guest、跑它、
然後不用 screen-scrape 就觀察結果。

> 為什麼需要這個：Phase 30 帶起來的 real-pcxtbios + FreeDOS 環境
> *太*真實、難用手測。我們想出貨 5 行的 test program、exercise 特定
> instruction / interrupt、一分鐘內拿到 binary pass/fail，像 Bochs / QEMU
> 使用者那樣。

## TL;DR

```pwsh
# 1. 用 NASM 寫一個小 .COM (test-roms/x86/src/mytest.com.asm)
"C:/Program Files/NASM/nasm.exe" -f bin `
    test-roms/x86/src/mytest.com.asm `
    -o test-roms/x86/fat12-b/MYTEST.COM

# 2. 建一個含它的 B: floppy image
python tools/make_fat12_floppy.py `
    --src=test-roms/x86/fat12-b `
    --out=test-roms/x86/test-floppy-b.img `
    --label="APRPCTEST"

# 3. 帶 --floppy-a (FreeDOS) 跟 --floppy-b (test image) 開機
dotnet "src/AprPc.Cli/bin/Debug/net10.0-windows/apr-pc.dll" `
    --bios=BIOS/firmware/pcxtbios.bin `
    --video-bios=BIOS/firmware/videorom.bin `
    --floppy-a=BIOS/freedos-1.3-floppy.img `
    --floppy-b=test-roms/x86/test-floppy-b.img `
    --backend=json --video=cga `
    --auto-test=freedos-b-hello `
    --window-scale=2

# 4. 讀結果
Get-Content temp/port-e9.log
```

## 架構一張圖

```
+---------------------+        +-----------------+        +---------------+
| test-roms/x86/src/  |        | test-roms/x86/  |        | apr-pc        |
|   mytest.com.asm    |  NASM  |  fat12-b/       | Python |               |
|                     +-------->  MYTEST.COM     +-------->  --floppy-b   |
+---------------------+        +-----------------+ FAT12  +-------+-------+
                                       ^                          |
                                       |                          v
                              add more files here          +---------------+
                                                           | FreeDOS guest |
                                                           |   B:\>MYTEST  |
                                                           +-------+-------+
                                                                   |
                                                                   v
                                                   +-------+-------+--------+
                                                   |               |        |
                                                   v               v        v
                                          OUT 0xE9, AL       DOS stdout    螢幕
                                                   |               |        |
                                                   v               v        v
                                          temp/port-e9.log       (GUI / kbd-trace.log dump 可見)
                                                   |
                                                   v
                                          host stdout `[E9] ...`
                                          (real-time、無 polling)
```

## 三個 building block

### 1. Port 0xE9 debug-out hook（Phase 30.14a）

Real PC 硬體 port 0xE9 沒分配。Bochs 跟 QEMU 都把它 re-purpose 成
side-channel：guest 裡每個 `OUT 0xE9, AL` 都被 host 捕捉、寫到看得到的地方。

AprPc 裡：
- `PcPortBus.Write8` case `0xE9` 呼叫 `WritePortE9(byte)`。
- Output per-line buffer、flush 到 **`temp/port-e9.log`**。
- 預設也 mirror 到 host stdout 變 `[E9] <line>` 讓你看 live 結果。
  用 `PcPortBus.PortE9MirrorStdout = false` 關。
- 每次 launch 截掉（`Program.cs` 呼叫 `PcPortBus.ResetPortE9Log()`）。

**Guest-side 用法** — 你 .COM 裡任何地方：

```nasm
mov  si, msg
.loop:
        lodsb
        or   al, al
        jz   .done
        out  0xE9, al
        jmp  .loop
.done:
msg:    db  '[MYTEST] step 1 ok', 10, 0      ; LF 結束一個 log line
```

換行（`0x0A`）flush；`0x0D` 丟掉（無雙倍 log line）；任何 non-printable
byte 在 log 內 escape 為 `\xNN`。`[TEST_PASS]\n` 或 `[FAIL_<reason>]\n`
這種 sentinel 讓結果可 grep。

### 2. `--floppy-b=PATH` 第二個 floppy mount（Phase 30.14b）

Boot floppy A: 保持 *pristine* — 你 test 住在獨立的 disposable B:。
有兩件事必須 land 才讓 FreeDOS 把 B: 當真實 drive、而不是做 phantom-B
「Insert diskette for B:」prompt：

1. `PcSystemRunner` 把 image mount 到 FDC drive 0x01。
2. `PcPortBus.Read62` 透過 SW2 nibble bit 2-3 報 `floppyCount = 2`。
   pcxtbios POST 從那 nibble 建 BDA[0x10] equipment word；bit 6-7 = 01
   → 「2 個 floppy drive」→ FreeDOS 不會假裝 swap。

> 途中找到的微妙 bug：我們 Port 0x61 read 的 SW2 select line 是
> **bit 3**、不是 bit 2。pcxtbios 的 `TURBO_ENABLED` build 很早在 POST
> 就 `OUT 0x61, 0xA5`、把 bit 2 永遠 sticky-high；只有 bit 3 在 memory
> 跟 video+floppy read 之間 deterministically toggle（按 PC/XT 8255 PIA
> Port B 慣例）。早期 CGA-only path 是意外 work。看 commit `caaeb85`。

### 3. 純 Python FAT12 builder — `tools/make_fat12_floppy.py`

從 host file 的 directory 建 1.44 MB FAT12 floppy image。
為什麼不 `mtools`？Windows `choco` / `winget` / `scoop` 沒有、
SourceForge GnuWin32 binary 離線。200 行 Python script、無 dependency
比裝 MSYS2 只為了拿 `mcopy` 乾淨。

限制（這些是故意的；發現需要的 use case 再 lift）：
- **只 8.3 short name** — 不產 LFN dirent。用 `HELLO.COM`、
  不要 `Hello world.exe`。
- **連續 cluster allocation** — Gemini 提示讓我們跳過惡名昭彰的
  FAT12 12-bit packed-pointer read-modify-write nightmare。Per-file
  這個 script 就 chain N..N+k-1 然後寫 0xFFF。除非有人要 delete-and-fragment
  scenario、都 fine。
- **標準 1.44 MB geometry** — 0xF0 media byte、2880 sector、18 sec/track、
  2 head。換別的 FreeDOS INT 13h driver 靜默拒絕 mount。

## 一個 working 的 AutoTester 端到端序列

`AutoTester`（`src/AprPc.Cli/Diagnostics/AutoTester.cs`）每 5 秒 poll
text framebuffer、pattern-match 期望螢幕、注 scancode、最後 dump +
screenshot + close window。

出貨的 `freedos-b-hello` 序列是端到端 demo 的 canonical：

| Step | 等                 | 注                                                | 為什麼                                          |
|---|----|----|---|
| 0 | `language`               | `Enter`                                                | 用預設 English             |
| 1 | `[Y,N]`                  | `N` then `Enter`                                       | 中止 installer                  |
| 2 | `A:\>`                   | `B` then `Shift+;` (= `:`) then `Enter`                | 切到 drive B:               |
| 3 | `B:\>`                   | `H E L L O` then `Enter`                               | 跑 HELLO.COM                    |
| 4 | (terminal — no pattern)  | dump screen、save PNG、close window                    | 為 CI 捕捉結果            |

在 `BuildSequence()` 加新序列。每個 step 是 `(pattern, scancode list)`；
最後 step 帶 `IsTerminal = true`，第一次 tick 觸發 dump + close。

## 自己寫 test

1. **選名字** fit 8.3：`MYTEST.COM`、`INT21H.EXE` 等。
2. **寫 asm** 在 `test-roms/x86/src/<name>.com.asm`：
   ```nasm
   bits 16
   org  0x100             ; .COM 載到 PSP+0x100
   start:
           ; ... 你的 code ...
           mov  si, msg
   .e9:
           lodsb
           or   al, al
           jz   .done
           out  0xE9, al
           jmp  .e9
   .done:
           mov  ah, 0x4C    ; DOS terminate
           mov  al, 0       ; exit code
           int  0x21
   msg:    db  '[MYTEST] PASS', 10, '[TEST_PASS]', 10, 0
   ```
3. **Build** 用 `"C:/Program Files/NASM/nasm.exe"` 的 NASM（看
   `[reference_nasm]` memory）。Output `.COM` 到
   `test-roms/x86/fat12-b/`。
4. **重 build floppy** 用 `tools/make_fat12_floppy.py`。
5. **跑** 帶 `--floppy-b=test-roms/x86/test-floppy-b.img` 加上以下任一：
   - `--auto-test=freedos-b-hello`（如果你名字是 `HELLO`）— 最簡單
   - 一個新 AutoTester 序列（在 `BuildSequence` 幾行）如果你 test 要
     自己的 scripted invocation
   - 手動 GUI：打 `B:` Enter 然後你的名字 Enter
6. **檢查** `temp/port-e9.log` 裡的 sentinel。

## 踩坑

- **Per-test 重 build**：floppy image commit 在
  `test-roms/x86/test-floppy-b.img`。每次 `.COM` 改了就重 build、
  不然你會一直跑 stale binary。Python builder 很快（< 100 ms）—
  接到你 iteration loop。
- **`Shift+key` 標點**：只在 GUI mode work（由 WinForms KeyDown handle）。
  AutoTester 用 scancode triple `(shift make, key make, key break,
  shift break)` — 看 `freedos-b-hello` 裡的 `B:` step 的形狀。
- **HLE BIOS mode**：這整個流程 `--bios=...` real-BIOS only。HLE BIOS
  不驅動 FDC、所以 `--floppy-b` 不會 surface 到 DOS。
- **Test 結束**：`.COM` 應該 `INT 21h AH=4C` 乾淨回 prompt。
  `freedos-b-hello` AutoTester 序列假設 5 秒內回到 `B:\>`。

## 未來擴充（還沒做）

- `--auto-test-screenshot=PATH` — 讓 test script 選 output PNG name、
  不是 `auto-test-<timestamp>.png`。
- DOSBox-style `AUTOEXEC.BAT` fixture loop：改 A: call `B:\AUTORUN.BAT`
  if present；然後 host 只要 per test rebuild B: + relaunch、不用
  AutoTester pattern matching。
- `--floppy-b=DIR` 縮寫、自動跑 `make_fat12_floppy.py`（省 explicit
  rebuild step）。
- vvfat-style on-the-fly FAT 合成（無 .img file）。重；等有人要再做。
