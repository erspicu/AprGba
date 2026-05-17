# FreeDOS — MDA 模式適用性分析

**日期**：2026-05-16
**問題**：「FreeDOS 設計是否不適合 MDA 模式？」
**Sources**：github.com/FDOS/kernel + github.com/FDOS/freecom（clone 到 `ref/freedos/`）

## TL;DR

**不 — FreeDOS 100% 支援 MDA**。我們在 `--video=mda` 看到的「隱形 row」
是 pcxtbios INT 10h teletype scroll bug、不是 FreeDOS。透過 source audit
kernel CON driver 跟 FreeCOM output path 確認。

## 證據

### 1. FreeDOS kernel 從不直接寫 video memory

```
$ rg '0xB000|0B000|B000|0xB800|B800' kernel/
  → 只有 match：test/ldosboot/multboot.asm、kernel/memdisk.asm
    （兩者都跟 video output 無關）
```

`kernel/kernel/console.asm` 是整個 CON device driver — 它只做：

```asm
ConWrite:
    jcxz    ConNdRd3
ConWr1:
    mov     al, [es:di]
    inc     di
    int     29h              ; fast output service
    loop    ConWr1

; _int29_handler:
    mov     ah, 0Eh
    mov     bx, 7            ; BH=0 (page), BL=7 (graphics attr, text 下忽略)
    int     10h              ; teletype
```

每個 char 都走 BIOS teletype。沒 `mov [es:di], ax`、沒 segment
0xB000/0xB800、沒 scroll/clear shortcut。

### 2. FreeCOM 從不直接寫 video memory

```
$ rg '0xB000|MDA|MONO|monochrome' freecom/
  → source code 內零 match
```

FreeCOM CVS log：
> *Revision 1.7 (2006/06/11): All of FreeCOM now uses `write` instead of
> `putchar` and `intr` instead of `int86[x]` or `intdos[x]`*
> *Revision 1.8 (2006/06/12): All CONIO dependencies have now been
> removed and replaced with size-optimized functions*

FreeCOM output：`outc()` → `write(1, &c, 1)` → INT 21h AH=40h →
DOS kernel `write_char` → INT 29h → INT 10h AH=0Eh teletype。

### 3. FreeCOM 在 text mode 明確 init attr=0x0700

`freecom/cmd/cls.c` 是 FreeCOM text-mode awareness 的 source-of-truth：

```c
int cmd_cls (char * param) {
    ...
    if((attr & 0x9f) == 0x93) {                 // stdout 是真 console
        unsigned attr = 0x0700;                 // <<< 預設 light-gray on black
        IREGS r;

        r.r_ax = 0x0f00;                        // get current video mode
        intrpt(0x10, &r);
        mode = r.r_ax & 0x7f;

        switch (mode)
        {
        case 0x04: case 0x05: case 0x09:        // CGA / PCjr / Tandy / VGA graphics
        case 0x0a: case 0x0b: case 0x0d:
        case 0x0e: case 0x0f: case 0x10:
        case 0x11: case 0x12: case 0x13: case 0x59:
            attr = 0;                           // graphics mode 用 0
            break;
        default:                                // <<< text mode (0,1,2,3,7) 保持 0x0700
            ;
        }

        r.r_ax = 0x0600;                        // scroll up、整螢幕
        r.r_bx = attr;                          // MDA 下 BX=0x0700
        intrpt(0x10, &r);
    }
}
```

如果 FreeDOS 真的「對 MDA broken」、這正是該 break 的地方 — 但它沒。
Mode 7 fall into `default`、保持 `attr=0x0700`、透過 BX 給 BIOS。

### 4. 任何地方都沒 MDA-specific code path

kernel 跟 freecom 兩邊 `MDA`、`MONO`、`MONOCHROME`、`0xB000`、segment
`B000:` 零 hit。FreeDOS 對所有 text mode 一致 — 就把工作交給 BIOS INT
10h 帶對的 register state。

## Root cause 重新確認：pcxtbios INT 10h Function 14 (teletype)

`ref/pcxtbios/pcxtbios.asm` line 4129-4153：

```asm
@@line_feed:
    cmp     dh, 18h                ; 頁底?
    jz      @@scroll
    inc     dh
    jnz     @@position

@@scroll:
    mov     ah, 2                  ; 把 cursor 放 row 0
    int     10h
    call    mode_check             ; CF=0 if text、CF=1 if graphics
    mov     bh, 0                  ; graphics 預設 BH
    jb      @@scroll_up            ; jb (=JC)：graphics 就 skip
    ; Text-mode path（MDA fall through 這裡）：
    mov     ah, 8
    int     10h                    ; 在 cursor 讀 char+attr
    mov     bh, ah                 ; <<< BH = read-back attribute

@@scroll_up:
    mov     ah, 6                  ; scroll up 1 line
    mov     al, 1
    xor     cx, cx
    mov     dh, 18h
    mov     dl, [ds:4Ah]
    dec     dl
    int     10h                    ; <<< scroll fill 用上面的 BH
    ret
```

惡性循環：
1. 上次 scroll 用 attr=BH 填新 bottom row。
2. teletype 又 overflow → 在 cursor 讀 attr → 回上次的 BH。
3. 如果上次 BH 是 0（例如第一次 overflow 時 cell 還沒被 touch 過）、
   後續 scroll 全部繼承 attr=0。
4. 新 bottom row 是「black on black」= 隱形。

### 我們的修法（Phase 30.10）

`--bios=pcxtbios.bin` 時 active 的雙層防禦：

| Layer | 實作 | 抓什麼 |
|---|---|---|
| Binary patch | `PcMemoryBus.ApplyPcxtbiosScrollFix` 把 line 4143 的 `8A FC` (mov bh, ah) 重寫 → `B7 07` (mov bh, 0x07)。Checksum filler 在最後 byte 補。 | Text-mode scroll 永遠用 BH=0x07。 |
| Runtime intercept | `PcSystemRunner` 每個 instruction peek；如果下個是 `CD 10` AND AH=06 AND BH=0 AND mode∈{0,1,2,3,7} → 強迫 BH=0x07。 | 任何其他 caller（例如 FreeCOM CLS bug 或 third-party）對 scroll up 傳 BH=0。 |

## Phase 30 plan 的結論

### Phase 30.10 patch + intercept 真的 work（2026-05-16 驗證）

Auto-test 跑（`--auto-test=freedos-mda-dir`、kernel 完整 boot + install
prompt 按 N + `dir`）紀錄了 31 887 trace line；關鍵 counter：

| Event | Count | Note |
|---|---|---|
| `[BIOS] pcxtbios teletype-scroll patch ... 8A FC -> B7 07` | 1（boot 時） | Patch apply。 |
| INT 10h AH=06（scroll up）總共 | 20 | 全部 `BH ∈ {0x07, 0x70, 0x70xx}`（= 正常或反白）。 |
| INT 10h AH=06 with `BH=0x00` | **0** | Intercept 從沒 fire — patch 抓住一切。 |
| `dir` 期間從 `F000:F6CE`（= patched teletype scroll）來的 INT 10h AH=06 | 2 | 拜 patch 之賜，兩個都過 `BX=0x0700`（BH=0x07）。 |
| Dir output 期間 AH=09（write char+attr）(16:01) | 0 | Dir output 走 teletype（AH=0E → AH=0A path）、不是 AH=09。 |

跑完時 `AutoTester FINAL SCREEN` snapshot 抓到 *整個* dir listing 完全
可見 — 無隱形 row、無漏行：

```
| A:\>dir
|  Volume in drive A is FD13-BOOT
|  Volume Serial Number is 858E-3E5F
|  Directory of A:\
| FREEDOS              <DIR>  02/20/2022 12:17p
| FDAUTO   BAT         1,476  02/20/2022 12:17p
| FDCONFIG SYS           392  02/20/2022 12:17p
| KERNEL   SYS        46,485  05/14/2021  3:32a
| SETUP    BAT        39,641  02/20/2022 12:17p
|          4 file(s)         87,994 bytes
|          1 dir(s)         820,224 bytes free
| A:\>
```

### 剩下的「隱形」artifact 是 FreeDOS installer welcome 對話（不是 dir）

早期在 MDA 看到的隱形內容 observation 幾乎肯定是 **FreeDOS 1.3 install
welcome screen**、由 `SETUP.BAT` / WELCOME 透過 INT 10h AH=09 帶
**CGA-only** attribute 值畫：

```
15:59:45.500  INT_10h caller=222D:0136 AX=0x0920 BX=0x0010 ... DX=0x0307
15:59:45.503  INT_10h caller=222D:0136 AX=0x09DB BX=0x0012 ... DX=0x0309
                                                ^^^^^^^^
                                                BH=0 page, BL=attr
```

- `BL=0x10` → fg=0、bg=1、intensity=0
- `BL=0x12` → fg=2、bg=1、intensity=0

在 CGA、`bg=1` = blue background（installer 在畫 coloured title bar）。
真 MDA 上、**`bg=1` 未定義** — 只 `bg=0` 跟 `bg=7` 有效。真 IBM 5151
硬體會 render 成 garbage 或最近的有效組合，視 revision。

`X86CgaRenderer.MdaDecodeAttr`（`src/AprX86.Cli/Video/X86CgaRenderer.cs:160`）
防禦性對無效 `(fg, bg)` pair 回 **black-on-black**。這對某個合理的真硬體
行為忠實、但讓 installer coloured banner 在 MDA 消失。

這是 **FreeDOS installer 的選擇、不是 kernel bug、不是 BIOS bug、不是
AprPc bug**：
- FreeDOS *kernel + FreeCOM* output（每個 `printf`、`dir`、`prompt`）work
  因為走 INT 10h AH=0E teletype、保留 cell attribute = 0x07（boot 時
  `int_10_func_0` 透過 `mov ax, 7*100h+' ' ; rep stosw` 由 `clear_screen` set）。
- *Installer* 用 AH=09 帶為 CGA/EGA 設計的硬寫死 colour attribute。
  在 MDA 那些 attribute 值 out of spec。

### Renderer 修法 land（2026-05-16）

對 auto-test framebuffer 做 attr-histogram instrumentation 之後、
最終兇手浮現：**row 15-22 的 attr 字面上 = `0x00`**（不像 installer-welcome
cell 那樣的 `0x10`）。就是說、*沒 INT 10h handler 把那些 cell 的 attribute
byte set 成有效值、但它們裝可印字元*。最可能原因：installer cleanup 直寫
VRAM（用 attr=0 填）後接著 FreeCOM 的 `AH=0A` write-char-only call
（advance 過 attribute byte 但不碰）混合的結果。真 IBM 5151 硬體會把那些
cell render 成「display off」— 但實際上 user 想看到它們。

`MdaDecodeAttr`（`src/AprX86.Cli/Video/X86CgaRenderer.cs:160`）現在實作
雙層：

| Rule | Behaviour |
|---|---|
| `(attr & 0x70) != 0`（任何 bg bit set） | 反白 — handle installer 的 `0x10` / `0x12` 「blue bg」attr，不是有效 MDA 但在真 clone 上看得到 |
| `(attr & 0x07) != 0` 且非上述 | 正常 text（暗綠 on 黑）；intensity bit `0x08` 變亮 |
| `attr == 0x00` **且 char 可印 (0x20–0x7E)** | 正常 text — 防禦 heuristic for「FreeDOS 忘記 set attr」cell |
| `attr == 0x00` 且 char 是空白/null | 真 display off（保留 boot screen 空白 cell） |

視覺驗證：`result/pc/auto-test-20260516-162448.png` — 完整 FreeDOS 1.3
boot + install-abort + `dir` listing，每行可見、含修前隱形的 file table。

### 建議（更新）

1. ✅ pcxtbios scroll patch + runtime intercept 如設計 work。
2. ✅ `MdaDecodeAttr` heuristic 還原 FreeDOS dir output on MDA。
3. ✅ `--video=mda` 現在跟 `pcxtbios.bin + freedos-1.3-floppy.img` 端到端可用。
   Installer welcome banner 仍建議 CGA（它的 CGA color attr 在 CGA render 更忠實）。
4. Trade-off note：heuristic 跟嚴格 IBM 5151 硬體有小偏離（真硬體即使有
   char、attr=0 cell 也會真的隱形）。文件化為對「為 color 寫但跑在 MDA 的
   FreeDOS 時代軟體」刻意的寬容 rule。

### 後記（2026-05-16）：VBIOS-driven mode 3 是最乾淨的答案

Phase 30.12（videorom.bin Option ROM loader）— 加
`--video-bios=BIOS/firmware/videorom.bin`（Tseng Labs ET4000 32 KB VGA
BIOS、1992）— 結果是比追每個 attr quirk 簡單很多的「FreeDOS-on-MDA」
render issue 全類別 fix：

1. pcxtbios POST 掃 0xC0000-0xFE000 找 `0x55 0xAA` 並 FAR-CALL offset 3。
   把 Tseng VBIOS load 在 0xC0000 讓這個自動跑 — `caller=C000` 在
   單次 boot 的 INT 10h trace 出現 157 次。
2. VBIOS init 強迫 active video mode = **3**（CGA 80x25 colour text 在 0xB8000）
   而不是 7（MDA 在 0xB0000）。之後所有 FreeDOS output 走正確的 16-colour
   CGA attribute path，我們的 renderer 已經透過標準 CGA palette 正確 handle。
3. 視覺結果：`result/pc/auto-test-20260516-164332.png` — colour 的完整
   FreeDOS installer（「FreeDOS」綠、「overwrite」紅、「stop NOW!」紅、
   「[Y,N]」綠）、`dir` listing 完整可見、ASCII-art banner 顯示原始的
   blue/green CGA 設計。

這意味著 MDA-mode 工作（`--video=mda` path）現在 optional / 歷史；
建議設置是：
```
--bios=BIOS/firmware/pcxtbios.bin
--video-bios=BIOS/firmware/videorom.bin
--floppy-a=BIOS/freedos-1.3-floppy.img
```
MDA fix 還是出貨、對 `pcxtbios.bin`-only path 還是對的，但不再是
user-facing 建議。

## 參考

- `ref/freedos/kernel/kernel/console.asm` — CON device driver
- `ref/freedos/freecom/cmd/cls.c` — FreeCOM CLS（attr=0x0700 proof）
- `ref/pcxtbios/pcxtbios.asm` line 4081-4192 — teletype + mode_check
- `MD/ref/pcxtbios-device-spec.md` — device handbook digest
- `src/AprPc.Cli/Memory/PcMemoryBus.cs:184` — binary patch
- `src/AprPc.Cli/PcSystemRunner.cs:489-516` — runtime intercept
