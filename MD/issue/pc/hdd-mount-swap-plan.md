# AprPc — 虛擬 HDD + Host-dir mount + Floppy swap plan

> **狀態**：📋 PLANNED（2026-05-18）。三個相關 storage 功能，依 Gemini
> 架構 review（`tools/knowledgebase/message/20260518_184350.txt`）整合。
>
> **觸發**：CheckIt 3.0 multi-disk install 跑不動（floppy 只能 mount A:/B:
> 兩格、不能換片）；FreeDOS install 也面對同樣問題；裝完後沒 HDD 存
> 程式、每次都從 floppy 跑很慢。
>
> **Gemini 給的關鍵更正**（避免重新發明踩坑）：
> 1. **HDD INT 41h/46h FDPT 必裝**（CheckIt、FDISK、SpinRite 直讀、不
>    透過 INT 13h AH=08）。
> 2. **>504 MB HDD 必須 LBA 擴展**、否則 DOS 截斷或 crash。
> 3. **FreeDOS 主動 probe INT 13h AH=41h LBA**；不支援時必須 set CF=1 +
>    AH=01h，否則 boot hang。
> 4. **Host-dir mount 不能用 INT 21h trap**（real DOS 有 SDA/SFT 內部
>    狀態、會 desync）。改 vvfat 或 Guest TSR + INT 2Fh Redirector。
> 5. **Floppy swap 必須 assert DSKCHG（port 0x3F7 bit 7）**，否則 DOS 把
>    disk 1 的 cached FAT 寫到 disk 2、**永久損毀 disk 2 filesystem**。

---

## 實作順序

依 Gemini 建議 + 我們現況：

| Phase | Feature | Effort | 阻擋什麼 | Status |
|---|---|---|---|---|
| **32.1** | Floppy swap hotkey + DSKCHG | ~1 day | CheckIt 2-disk、FreeDOS install 4-disk | ✅ `e8ed55e` (2026-05-18) |
| **32.2** | Virtual HDD（HLE INT 13h、FDPT、LBA stub） | ~2-3 day | CheckIt persistent install、FreeDOS C: | ⚠️ partial (2026-05-18) — real-BIOS HDD detection blocked、看 32.2g |
| **32.2g** | Real-BIOS INT 13h hijack for HDD（XT-IDE-style option ROM + HLE chain） | ~0.5-1 day → 實作完了 | **FreeDOS / CheckIt / FDISK 偵測 HDD** | ✅ `08a8809`/`6a890fd`/`dc6f9e4`/`3762127`（2026-05-18）：BDA[0x475]=1 set OK、IVT[0x13] hijack OK；FreeDOS install 仍卡 FDISK FLAG_SECTOR error（packed binary、不是 BDA wipe）—> 32.2h |
| **32.2h** | FDISK FLAG_SECTOR root cause + bypass | ~1-2 day | FreeDOS install 完整跑完 | 📋 **next**（看下面詳細 plan） |
| **32.3** | Host-dir mount（vvfat OR Guest TSR） | ~2+ week | dev-loop QoL、跨 host/guest 拖檔 | 🚧 V0 skeleton (2026-05-18) |

順序 32.1 → 32.2 → 32.3 — 從便宜 / unblock 度高的開始。

---

## Phase 32.1 — Floppy swap hotkey

### 目標

讓 multi-disk software（CheckIt × 2、FreeDOS install × 4、Windows 3.1
× 6）不用退出 + 重啟模擬器。

### CLI

```
--floppy-a=disk1.img,disk2.img,disk3.img    # comma-separated list
--floppy-b=...                              # same syntax
```

`PcOptions.FloppyAPath` 改成 `IReadOnlyList<string> FloppyAPaths`（破壞性
改、檢 callers）。Index 從 0、boot 時用 [0]。

### GUI hotkey

`MainForm.KeyDown`：
- `Ctrl+L` → 切 slot A 的下一張（cycle）
- `Ctrl+Shift+L` → 切 slot B
- Status bar 顯示「`A: disk1.img (1/3)`」

### Headless / CLI mode

emulator 讀 stdin command line：
- `INSERT A 2\n` → slot A 換到 image index 2
- `INSERT A NEXT\n` → cycle next
- `INSERT B path\to\disk.img\n` → 動態 attach 新 image
- 設計成 line-based、可 script 化（pipe 進去）

### Swap mechanism

```csharp
public sealed class DiskImage
{
    public void Swap(string newPath) {
        lock (_swapLock) {
            _bytes = File.ReadAllBytes(newPath);
            _path  = newPath;
            // geometry from file size — re-detect
            (Cylinders, Heads, Sectors) = DetectGeometry(_bytes.Length);
            DiskChanged = true;   // set DSKCHG line
        }
    }
}
```

PcSystemRunner.SwapFloppy(byte slot, int imageIndex)：
1. 拿 `_emulatorLock`（暫停 CPU thread）
2. `_fdc.GetDrive(slot).Swap(path)`
3. 釋放 lock、CPU resume

不需要 drain pending INT 13h — 我們 CPU 跑 single-thread、swap 發生在
指令邊界。

### DSKCHG（**critical correctness fix**）

Port 0x3F7 bit 7 = DSKCHG（disk changed since last seek）。

```csharp
// Fdc8272.cs
public byte Read3F7() {
    var d = _drives[_activeDrive];
    return (byte)(d?.DiskChanged == true ? 0x80 : 0x00);
}

// On SEEK / RECALIBRATE command:
private void ExecSeek() {
    ...
    _drives[drive].DiskChanged = false;   // clear on seek
}
```

理由：DOS 對 floppy aggressive cache FAT sectors。如果 swap 後 DOS 不
知道、會把 disk 1 的 FAT cache 寫回 disk 2 → 永久 corrupt。

pcxtbios 不讀 0x3F7、但 MS-DOS 5+ 內部會 poll。即使 XT BIOS path 也要實裝。

### 工作項目

| Sprint | Deliverable | Effort |
|---|---|---|
| 32.1a | DiskImage.Swap + DiskChanged flag + DSKCHG via port 0x3F7 | 0.3 day |
| 32.1b | PcOptions 解析 comma-separated path list | 0.2 day |
| 32.1c | GUI hotkey Ctrl+L / Ctrl+Shift+L + status bar | 0.3 day |
| 32.1d | Headless stdin command listener | 0.3 day |
| 32.1e | Test：CheckIt 2-disk install + verify DSKCHG | 0.2 day |

Total ~1.3 day。

---

## Phase 32.2 — Virtual HDD

### 目標

`--hdd=PATH` 真正 work、能 boot 進 HDD、FDISK / FORMAT / 安裝
DOS / CheckIt 到 HDD。

### CLI

```
--hdd=PATH         # primary HDD (drive 0x80)
--hdd2=PATH        # second HDD  (drive 0x81)
```

Image：flat sector dump `.img`，無 header（DiskGenius / dd / VirtualBox
raw export 都產這個）。

### Geometry 偵測

從 file size 自動 detect：

| Size | C × H × S | Notes |
|---|---|---|
| 10 MB | 306 × 4 × 17 | DOS 2.x baseline |
| 20 MB | 615 × 4 × 17 | XT HDD 標準 |
| 32 MB | 615 × 6 × 17 | DOS 3.3 max single partition |
| 504 MB | 1024 × 16 × 63 | INT 13h CHS 極限 |
| 8 GB | LBA mode | 超過 504 MB 走 LBA |
| 其他 | 從 size 反推 CHS（C × 16 × 63 標準）| heuristic |

未來可加 `--hdd-geometry=C:H:S` CLI override。

### INT 13h coverage

擴 HleBios 讓 drive ≥ 0x80 也走 disk table（已有 floppy-only logic）：

| AH | Function | 必裝 |
|---|---|---|
| 0x00 | Reset disk | ✅ |
| 0x02 | Read sectors | ✅ |
| 0x03 | Write sectors | ✅ |
| 0x04 | Verify | ✅ |
| 0x08 | Get drive params | ✅ |
| 0x15 | Get disk type（回 0x03 = HDD） | ✅ |
| **0x41** | **LBA probe** — 必須 set CF=1 + AH=01h（不支援） | ⚠️ **critical**：FreeDOS boot 前 probe，不 set CF=1 + AH=01h boot 會 hang |
| 0x42-48 | LBA read/write/verify/seek | 延後（image > 504 MB 才用到） |

### INT 41h / 46h FDPT（**critical correctness fix**）

CheckIt、FDISK、SpinRite 直接讀 INT 41h vector 指向的 16-byte FDPT、
不透過 INT 13h AH=08。**必裝**。

```
INT 41h vector → FDPT for drive 0x80
INT 46h vector → FDPT for drive 0x81

FDPT layout (16 bytes):
  +0  WORD  max cylinder
  +2  BYTE  max head
  +4  WORD  start reduced-write current cyl (default 0xFFFF)
  +6  WORD  start write precomp cyl (default 0xFFFF)
  +8  BYTE  max ECC burst length (default 0x0B)
  +9  BYTE  control byte (bit 6 = ECC, bit 3 = > 8 heads)
  +A  BYTE  std timeout (default 0x00)
  +B  BYTE  format timeout (default 0x00)
  +C  BYTE  check-disk timeout (default 0x00)
  +D  WORD  landing zone cyl
  +F  BYTE  sectors per track
```

裝在 BIOS data area 內安全位置（例如 0x0040:0x00F0+）、IVT[0x41] 跟
IVT[0x46] 指向它。

### MBR 處理

emulator **不 parse MBR**。Sector 0 = MBR 的原始 byte、guest OS（DOS
boot sector via `FDISK /MBR`、Linux `lilo` 等）自己 parse。

理由：parse 會破壞 alternative bootloader、非 DOS filesystem、guest 自己
的 partitioning tool。Block device 級別 emulation 該 stop 在 sector
read/write。

### Real-BIOS HDD（XT-IDE）

延後。XT-IDE BIOS GPL、loadable 在 0xC8000 option ROM slot、需要 trap
8-bit IDE 0x300 range port + IDE register + IRQ 5。HLE INT 13h 已夠
FreeDOS boot from HDD use case、先用 HLE。

### 工作項目

| Sprint | Deliverable | Effort |
|---|---|---|
| 32.2a | DiskImage geometry auto-detect from file size | 0.3 day |
| 32.2b | HleBios INT 13h 擴 drive ≥ 0x80 routing（AH=00/02/03/04/08/15） | 0.5 day |
| 32.2c | HleBios INT 13h AH=41h "no LBA" stub（**critical for FreeDOS**） | 0.2 day |
| 32.2d | FDPT install at INT 41h / 46h vector | 0.5 day |
| 32.2e | `--hdd=PATH` / `--hdd2=PATH` CLI + PcSystemRunner.MountDisk(0x80) wiring | 0.3 day |
| 32.2f | Test：FDISK + FORMAT + SYS C: + 從 C: boot | 0.5 day |

Total ~2.3 day。

---

## Phase 32.2g — Real-BIOS INT 13h hijack for HDD

### 觸發

2026-05-18 32.2 完成、FreeDOS install GUI 跑、結果：

```
"No fixed disks present" → installer abort
```

Root cause：**`--bios=pcxtbios.bin` 之下、IVT[0x13] 被 pcxtbios 自己 install**
（pcxtbios.asm:5100 `dw int_13` 把 `int_13` proc 接管 INT 13h）。pcxtbios 的
`int_13` proc 只 handle 軟碟、DL ≥ 0x80（HDD）直接 fail 回 "no fixed disk"。

我們 HLE INT 13h 已寫好（32.2b/c/d 出貨）、但 IVT[0x13] 沒指過來、call 不到。

### 設計

**Trampoline + chain pattern**：

1. POST 結束之後（或 reset 後第一次 `INT 19h` boot 觸發前），HleBios 把
   IVT[0x13] 改指自己的 HLE trap（`F000:0013` = HleTrapSegment 內）
2. 原 pcxtbios INT 13h vector 存到 `_realBiosInt13Vector` field
3. `HleBios.Int13(state)` dispatch：
   - `state.D.L < 0x80`（floppy）→ **chain** 回 `_realBiosInt13Vector`
     （pcxtbios floppy handler 仍然處理 A:/B:、跟既有行為一致）
   - `state.D.L >= 0x80`（HDD）→ HLE 自己 serve（既有 Int13_Read /
     Int13_GetDriveParams / Int13_AH41_LBAStub / etc.）

### POST-完成偵測

候選：
- **(a) 偵測 INT 19h** — pcxtbios POST 最後 call INT 19h boot；在這 INT
  的 HLE handler hook 內 install IVT[0x13] 改寫。但 IVT[0x19] 也被
  pcxtbios 接管、需要 chain。
- **(b) 寫入監視** — 監視 phys 0x4C（IVT[0x13] entry）的 write、
  在 pcxtbios 寫完之後重新 patch。簡單但耦合到 CPU memory write hook。
- **(c) 啟動時統一 hook** — Reset 後立刻 install IVT[0x13] = HLE trap。
  pcxtbios POST 之後會覆蓋它；但我們在 POST 完成偵測點再次 install。
- **(d) 簡化**：reset 後最簡單就 install HLE INT 13h、然後在 pcxtbios
  init_int 寫 IVT[0x13] 之後（用 memory write watch 監視 phys 0x4C-0x4F），
  重新 install。

**建議 (a)**：HleBios 已經有「F000:0019 = INT 19h HLE handler」基礎建設
（HLE BIOS path 用同樣機制）。real-BIOS mode 下、第一次 CPU JMP 到
IVT[0x19] 之前我們 intercept。

實作：在 PcSystemRunner 初始化時、**只**改 IVT[0x19] = F000:0019、
HleBios.Int19 內：
1. 從 IVT[0x13] 讀出 real-BIOS 的 INT 13h vector、存 field
2. 把 IVT[0x13] 改成 F000:0013（HLE trap）
3. **chain** 回原 pcxtbios INT 19h（保持 boot 行為）— 透過 `JMP FAR`
   或 push `_realBiosInt19Vector` + IRET-style return

### Chain 給 floppy 的機制

當 `state.D.L < 0x80`、HleBios.Int13 不能就 `IRET` 回 caller（這樣 floppy
read 永遠 fail）。需要把 control 轉到 pcxtbios `int_13` handler、
讓它跑、它 IRET 之後 caller 收到 pcxtbios 的 result。

兩種做法：
- **TRAMPOLINE**：HleBios trap 內、push caller flags+CS+IP、JMP FAR
  到 `_realBiosInt13Vector`（pcxtbios `int_13`）。pcxtbios `int_13` IRET
  時 pop 三件、回到 user 的 INT 13h 之後。等於 emulator 透明做了 INT
  redirect。需要 CPU state direct manipulation：HleBios.Int13 不 RET 回
  emulator framework、而是把 CS:IP 改成 pcxtbios handler、emulator 繼續 step。
- **直接 call C# emulation of floppy**：HleBios.Int13 floppy path 也用
  我們自己的 HLE 軟碟 INT 13h handler（既有的 Int13_Read 等）— 直接 serve、
  不 chain。Cons：bypass pcxtbios 的 floppy state（motor、DSKCHG）、可能
  跟 32.1 swap 衝突。

**建議 TRAMPOLINE 路線**：保留 pcxtbios floppy handler、只插 HDD。

### 工作項目

| Sprint | Deliverable | Effort |
|---|---|---|
| 32.2g-1 | Reset 後 install IVT[0x19] = F000:0019 HLE trap（只 in real-BIOS mode） | 0.2 day |
| 32.2g-2 | HleBios.Int19 first-call：snapshot real-BIOS IVT[0x13] vector + install F000:0013 HLE trap + chain 回 pcxtbios INT 19h | 0.3 day |
| 32.2g-3 | HleBios.Int13 dispatch by DL：floppy → trampoline CS:IP 改回 _realBiosInt13Vector、CPU 繼續 step；HDD → 既有 HLE serve | 0.5 day |
| 32.2g-4 | Test：`--hdd=blank.img:create:20` + FreeDOS install → 看 installer 偵測 HDD、跑 FDISK + FORMAT + SYS + reboot | 0.5 day |

Total ~1.5 day。

### 風險

- **Trampoline 跨 CS 跳轉**：CPU framework 在 HleBios.Int13 callback 結束時
  預期 CS:IP 是 trap segment + IRET-pop 之後。改 CS:IP 到 pcxtbios 然後
  跳過 IRET pop 需要小心 — flags / SP 對齊要對。
- **POST-時 INT 13h call**：pcxtbios POST 自己 call INT 13h（floppy detect、
  boot sector load）。Phase 32.2g 在 INT 19h hook 點才 install HDD trap、
  POST INT 13h 仍 100% 走 pcxtbios — 無 regression。
- **Real-BIOS 在 IRET 之後修改 IVT[0x13]**：FreeDOS load 自己的 disk driver
  可能 over-install IVT[0x13]（網路 redirector pattern）。如果發生、需要
  進一步 hook、但 FreeDOS 5/6/freedos1.3 不會。
- **8087 FPU extension 不影響**：FPU 不碰 IVT[0x13]。

### 確認 FDPT 在 BDA scratch 仍然 work

32.2d 修法（FDPT 放 0x004E0 / 0x004F0）— pcxtbios 不會碰那邊、所以這個
方案跟 32.2g 並存無問題。CheckIt / FDISK 透過 IVT[0x41]/[0x46] 讀 FDPT
仍然 work。

---

## Phase 32.2h — FDISK FLAG_SECTOR root cause + bypass

### 現況

Phase 32.2g 全部 land 之後、HDD 偵測完全 work：
- 我們 option ROM 在 pcxtbios POST 末端 FAR-CALL、寫 IVT[0x13] hijack + BDA[0x475]=1
- Trace 證明 `cached pcxtbios int_13 = F000:EC59`、`BDA[0x475]=1 (hard disk count visible to DOS)`
- DL>=0x80 INT 13h 進我們 HLE handler、floppy chain 回 pcxtbios

但 FreeDOS 1.3 install 還是 die：
```
The "FLAG_SECTOR" value in the "fdisk.ini" file is out of range...
Operation Terminated.

CRITICAL error: A partitioning error has occurred. A hard disk may not
be present or may be invisible to the current operating system.
The installation of FreeDOS 1.3 has been aborted.
```

Trace 顯示 FDISK 完全沒 call INT 13h DL>=0x80 之前就 die — 表示 die 在
`Process_Fdiskini_File()` 階段（FDISK source `fdiskio.c`、SETUP.BAT 內
的 `%FDISK% /info DRIVE` 也會跑到這）。

### Gemini consult 2026-05-18 #3 假設

兩個候選 root cause（看 `tools/knowledgebase/message/20260518_204827.txt`）：

1. **FDC DMA corruption**：fdisk.ini 從 A: 讀進 RAM 時某些 byte flip 了。
   `FLAG_SECTOR 2` 被讀成 `FLAG_SECTOR <某 value>` 之類。

2. **CPU bug in atoi/MUL/REP**：fdisk.ini 內容正確、但 FDISK.EXE atoi("2")
   或之前的 MUL/shift 算錯。FDISK 是 packed binary（UPX-8086 之類）、
   decompression stub 大量用 `REP MOVSB / LODSB`、subtle bug 會 explode。
   Tom Harte 8088 SST 我們已 verify 過、但可能仍有 edge case。

### 32.2h sprint plan

| Sub | Deliverable | Effort |
|---|---|---|
| 32.2h-1 | 加 RAM dump trigger：FDISK.EXE load fdisk.ini 後、watch buffer 內容。比對 floppy original bytes、確認是 FDC corruption 還是 CPU bug | 0.5 day |
| 32.2h-2 | 視 32.2h-1 結果：FDC bug → 修 FDC sector read path；CPU bug → 加 differential trace 找 broken instruction | 1+ day |
| 32.2h-3 | Pragmatic bypass：[`tools/make_dos_hdd.py`](../../../tools/make_dos_hdd.py) — 預寫 MBR + FAT16 VBR + 空 FAT 到 .img、user boot 到 DOS prompt 後 `SYS C:` 直接安裝 | ✅ 已交付（2026-05-18） |
| 32.2h-4 | Test：FreeDOS install 端到端跑完到 `C:\>` reboot prompt | 0.5 day（32.2h-1/2 解之後） |

### 2026-05-18 深度 investigation 結果

UPX-unpack 後 FDISK.EXE 可讀。Turbo C++ 1990 編譯。完整 error string：
```
The "FLAG_SECTOR" value in the "fdisk.ini" file is out of range...
Operation Terminated.
```

Disasm 找到 error path 在 image 0xD12（function start 0xAA1）：

```
; setup
mov word [0x5F16], 0x80     ; drive number
...
call 0x4D4E                  ; probe drives (loops drives, calls INT 13h AH=08)
                              ; populates part_table[X].total_sect

; validation
mov ax, [0x5F16]              ; AX = drive number / index
mov dx, 0x0B0C                ; struct size 2828
imul dx
mov bx, ax                    ; BX = idx * 2828
mov ax, [bx - 0x16CC]         ; high word of part_table[idx].total_sect
mov dx, [bx - 0x16CE]         ; low word
cmp ax, [0x5F1E]              ; high word of flag_sector
ja  skip_error                ; total_sect.hi > flag_sector.hi → OK
jc  cmp_low
cmp dx, [0x5F1C]              ; low word of flag_sector
jnc skip_error
cmp_low:
or  word [0x5F1C], [0x5F1E]   ; check flag_sector != 0
jz  skip_error                ; if flag_sector = 0, OK

; ERROR PATH:
mov ax, 0x2C2                 ; string offset for "FLAG_SECTOR... out of range"
push ax; call 0xC39D
mov ax, 0x306                 ; "Operation Terminated.\n"
push ax; call 0xC39D
push 3
call 0xBB84                   ; exit(3)
```

Logic：if `flag_sector != 0 AND part_table[drive].total_sect < flag_sector` → error.

**Trace shows ZERO `DL=8` INT 13h hits** before the error. 表示 `call 0x4D4E` 內 INT 13h
AH=08 從沒 fire — function 0x47C9 (probe drive via INT 13h) 沒被 reach。

兩個可能：
- (a) **`call 0x4D4E` 整個沒跑** — 表示 0xAA1 function 內 control flow 在 call 0x4D4E
  之前已 exit。但 0xCE6 是 unconditional call、應該必跑。Unless function 0xAA1 itself
  is reached via wrong code path.
- (b) **`0x4D4E` 跑了但內部 0x47C9 提早 return** — 0x47C9 內有 `cmp [0x5F36], 0xFF`
  check + various branches。如果某個 init 沒做、可能 early return.

Pre-formatted disk image with valid MBR 也沒解 → 確認 FDISK 連 INT 13h 都沒 call、
所以讀 disk 內容對 FDISK 沒影響。

下次 session 該做的事：
- 加 `--trace-cpu-cs=` filter for the FDISK code segment、看真實 CS:IP 流到哪、
  是否真進 0xAA1
- 加 RAM dump on DOS INT 21h AH=3F 讀 fdisk.ini 那個 buffer、確認 byte-level 正確性
- 或更直接：寫 DOS .COM 在 emulator 內跑、做 `atoi("2")` 然後 print、看 CPU 算術正確

Diagnostic tools 不存在、要新建 — 屬於 32.2h-1 工作項目。

### 32.2h-3 已交付

`tools/make_dos_hdd.py` 寫一個 single-partition FAT16 .img：
- MBR with type=0x06 FAT16BIG partition at LBA 63
- FAT16 VBR with auto-sized cluster (4-64 sectors/cluster picked for valid count)
- Empty FAT (cluster 0=0xF8/0xFF, cluster 1=EOC marker)
- Empty root directory
- Boot code = `INT 18h` fallback (SYS C: 之後會被取代)

```bash
python tools/make_dos_hdd.py --out=disks/c.img --size-mb=32
```

**注意**：32.2h-3 alone 不會解 FreeDOS install 問題（SETUP.BAT 仍 call
`FDISK /info` → 仍 parse fdisk.ini → 仍 die）。要走 manual route：

1. `gui-test.bat realbios cga` （boot FreeDOS install floppy）
2. 在 install confirmation 按 N（不要 auto-install）
3. A:\\> prompt 出來後、有 HDD pre-formatted、`SYS C:` install bootloader
4. `COPY A:\\FREEDOS\\BIN\\*.* C:\\` 手動 copy files
5. reboot 從 C: boot

複雜但不 block。

### 32.2h-1/2 真修法（next session）

加 emulator-level diagnostic：
- INT 21h AH=3D（DOS Open）watch — 看 fdisk.ini 的 file handle
- INT 21h AH=3F（DOS Read）watch — 看讀進 RAM 的 buffer + content
- Compare buffer bytes vs floppy original
- 不一致 → FDC bug；一致 → CPU bug（再深 trace atoi 內 MUL）

需要新 PcSystemRunner debug flag 或 INT trap。1-2 day 工作。

---

### 32.2h-4 — Root cause REVISED：SimulateIret CF clobber（2026-05-18 session）

**之前的 FLAG_SECTOR / total_sect=0 分析其實是症狀，不是 root cause**。
真正原因找到了，跟 FAT12 / fdisk.ini parsing 完全無關。

#### Symptom

FreeDOS 1.3 install boot 過程印 `can't get drive parameters for drive 04`，
然後 FDISK / installer 認為「No fixed disks present」直接 abort。但我們的
HLE BIOS INT 13h AH=08 對 DL=0x80 明明回 CHS = 261x16x63（正確值），
trace 也看得到 handler 被叫。

#### Root cause

`HleBios.SimulateIret` 把 handler 設好的 `state.FlagC` 用 stack 上原本
push 的 FLAGS word 蓋掉：

```csharp
private void SimulateIret(X86State state) {
    ushort flags = ReadStackWord(state, 4);   // saved FLAGS from INT
    ...
    state.SetFlags(flags);   // <-- clobbers handler-set FlagC
}
```

FreeDOS kernel (`initdisk.c:659`) 跟 standard 防呆 idiom 一樣，在 call
`init_call_intr(0x13, &regs)` 之前**故意把 `regs.flags = FLG_CARRY` 設成
1**，這樣 BIOS 不支援該 call 時 CF 保持 1。

`init_call_intr` 的 ASM (`intr.asm:63-64`) 用 `SAHF` 把 `regs.flags` low byte
load 進 CPU FLAGS，所以 INT 13h 是帶著 CF=1 push 上 stack 進入 handler。
我們 handler 改了 `state.FlagC=false` 但沒改 stack frame；`SimulateIret`
pop 出來 CF 還是 1。FreeDOS PUSHF 拿到 CF=1，treat as failure → drive
unenumerable → "No fixed disks present"。

同樣的 bug 同時影響其他帶 CF 的 INT call（AH=41 LBA probe、AH=15 type
query、INT 16h AH=01 keypress poll 用 ZF），但因為大部分 caller 沒 pre-set
CF=1，所以巧合 not blocking。

#### Fix（Approach B per Gemini 諮詢）

`SimulateIret` 不動（純 CPU IRET）。Handler 改成像 real BIOS 一樣，直接
patch stack 上的 FLAGS word（SS:[SP+4]）：

```csharp
private void SetStackFlagC(X86State state, bool set)
    => PatchStackFlags(state, 0x0001, set);

private void Int13Ok(X86State state, byte ret = 0) {
    state.A.H = ret;
    state.FlagC = false;
    SetStackFlagC(state, false);   // <-- patches stack so IRET preserves
    ...
}
```

Why not pure `SimulateIret`-side merge：CPU emulator 不該知道哪些 INT 用
CF / ZF / etc 當 status；BIOS API 語意是 BIOS 層責任。INT 16h AH=01 用 ZF
當 key-ready signal、INT 13h 用 CF 當 status — 在 SimulateIret 統一處理
會 leak 其他 stale flags（OF/SF/etc.）給 caller。Ralf Brown's INT list 確認
INT 13h **只** guarantee CF；其他 flag 都 undefined。

#### Files modified

- `src/AprPc.Cli/Bios/HleBios.cs`
  - 新增 `SetStackFlagC` / `SetStackFlagZ` / `PatchStackFlags` helpers
  - `Int13Ok`/`Int13Fail` 加 `SetStackFlagC`
  - `Int13_LastStatus` / `Int13_GetDiskType` 同
  - `Int16_PeekChar` 加 `SetStackFlagZ`（key ready / empty）
  - `SimulateIret` **完全不動**

#### Status

- 21:57 build 完成
- T1 unit tests 跑中（背景 task `bn2cm6dyc`）
- 等用戶實機驗證 FreeDOS install 路徑是否解開

---

## Phase 32.3 — Host-directory mount

### 重新評估（Gemini 警告）

我原本想用 **INT 21h trap**。**不行** — 我們 boot real FreeDOS、不是 HLE
kernel。Real DOS 維持內部 SDA（Swappable Data Area）+ SFT（System File
Table）。直接 trap INT 21h 然後 return 會讓 DOS 內部 state desync、
broke FCB calls、DUP2、child process inheritance、SHARE.EXE。

### 兩個 viable 方案

| 方案 | 機制 | Pro | Con |
|---|---|---|---|
| **vvfat** | 在 host memory synthesize FAT16 boot sector + FAT table + root dir、暴露為 HLE INT 13h drive | OS-agnostic（不用改 guest）、用既有 INT 13h infra | Write-support 複雜（要 reverse-engineer guest FAT writes 回 host file）、需要 dirty-cluster tracking |
| **Guest TSR + INT 2Fh Redirector** | 寫 x86-16 .SYS / .COM TSR 鉤 guest INT 2Fh AX=1100h、透過自訂 backdoor（unused port / custom INT）跟 C# 對話 | 跟 VirtualBox shared folders 一樣的官方 API、robust、long filenames 容易 | 需要寫 guest-side x86-16 ASM、用戶要 `LOAD` driver |

### 推薦：vvfat read-only V1 + Guest TSR V2

**V1（read-only vvfat）**：
- 啟動 host dir scan、build synthetic FAT16 image in C# memory（HLE INT 13h backing）
- 提供為 HDD slot（例如 `--mount=C:host\path` → 0x81）
- Read-only → write attempts set CF=1（DOS 顯示 "Write protect error" 跟真硬體一致）
- 用戶 dev loop：改 host、重啟 emulator、新內容可見

**V2（Guest TSR write-back）**：
- 之後寫 x86-16 TSR、INT 2Fh Redirector + 自訂 backdoor port
- Write 透過 TSR 直 forward 給 C# → host filesystem
- Long filename 透過 TSR 處理（DOS 看 8.3 alias、TSR 翻譯）

V1 解 80% dev-loop use case（拖 .COM / config 進去）；V2 為了 build /
runtime data 寫出。

### 8.3 LFN aliasing（V2 / vvfat 寫支援時用到）

```
Host: "Hello world.txt" → DOS: "HELLOW~1.TXT"
Host: "My Documents/"   → DOS: "MYDOCU~1"
```

Rule：
1. Strip space / invalid char、uppercase
2. 取前 6 char、append `~1`..`~9`
3. 維持雙向 cache（short ↔ host）在 C# backend

**不要 reject 非 8.3 名字** — host directory 大部分檔名都 > 8.3、reject
等於 "feature 沒 utility"。

### 工作項目

| Sprint | Deliverable | Effort | Status |
|---|---|---|---|
| **32.3-V0** | **Skeleton：`HostDirMount` class + `--mount` CLI parse + validation** | 0.3 day | ✅ `bbc67c1` (2026-05-18) |
| 32.3a (V1) | Host dir scan + FAT16 synthesizer + INT 13h backing | 4-5 day | 📋 next |
| 32.3b (V1) | `--mount=C:host\path` → drive 0x82/0x83 MountDisk wiring | 0.5 day | 📋 |
| 32.3c (V1) | Test：host file 出現在 guest DOS dir、可 copy / read | 0.5 day | 📋 |
| 32.3d (V2) | Guest TSR ASM + INT 2Fh Redirector hook | 5-7 day | 📋 V2 |
| 32.3e (V2) | C# backdoor port handler + write-back path | 3-4 day | 📋 V2 |
| 32.3f (V2) | LFN aliasing bidirectional cache | 1-2 day | 📋 V2 |

V0 done。V1 total ~5-6 day（next milestone）。V2 ~10-13 day。整段 2+ week。

### V0 已交付（2026-05-18）

- `src/AprPc.Cli/Hardware/HostDirMount.cs` — class with `TryParse()`,
  `Synthesize()` (stub returns all-zero buffer), spec parsing
  (`DRV:host[:ro|rw][:SIZE_MB]`), validation of drive letter range
  (C-Z), path existence check.
- `PcOptions.HostMounts` list + `--mount=...` CLI flag.
- `HeadlessRunner` walks specs, validates host path, prints
  "Phase 32.3 SKELETON (not yet exposed to guest)" status.

### V1 hand-off — what comes next

The framework is ready for the real vvfat synthesizer. Sprint 32.3a is:

1. **FAT16 boot sector + BPB at offset 0**:
   - Standard 32 MB FAT16 BPB constants (512 byte/sector, 64 sec/cluster,
     2 FATs, 512 root entries, etc.).
   - JMP short + NOP + OEM stamp + BPB + minimal stub code that runs
     INT 18h "no system" message if user tries to boot from it.

2. **Scan host directory**, allocate one or more 8.3 short-name dir
   entries per host file:
   - Build canonical 8.3 short name from host filename (Phoenix
     "first 6 chars + ~N" algorithm).
   - For each file, allocate a contiguous chain of FAT16 clusters
     (V1 keeps it simple — no fragmentation).
   - Write file content into the cluster region; write FAT chain.

3. **Wire HostDirMount.Synthesize() output to MountDisk(0x82) /
   MountDisk(0x83)** so guest sees E: / F: as a mountable HDD.

4. **Test plan**: `--mount=E:./test-roms/x86/fat12-b:ro` →
   FreeDOS boot → `E:\>dir` should list HELLO.COM, CHECKIT.EXE, etc.;
   `TYPE E:HELLO.COM` should print bytes; `COPY E:HELLO.COM A:` should
   succeed.

Once V1 read-only is solid, V2 picks up Guest TSR + write-back per
Gemini's advice (don't bolt write-back onto vvfat — pivot to INT 2Fh
Redirector pattern with an x86-16 driver in the guest).

---

## Test plan（全 3 phase 共通）

| Phase | Test workload | 成功標準 |
|---|---|---|
| 32.1 | CheckIt 2-disk install via Ctrl+L | CheckIt 看到 disk 2、不 corrupt disk 1 FAT |
| 32.2 | `--hdd=blank20mb.img` → boot floppy → FDISK / FORMAT / SYS C: / reboot → C: prompt | 從 C: boot 起來、`dir` work |
| 32.2 | CheckIt 安裝到 C:、reboot、`C:\CHECKIT\CHECKIT.EXE` 跑 | TUI 進得去 + Tests → Co-Processor pass |
| 32.3 (V1) | `--mount=D:src\test-roms\x86\fat12-b` → guest D:\>dir | 看到 HELLO.COM、可 type、可 run |
| 32.3 (V2) | 同上 + 跑 program 寫 `D:\OUT.LOG`、host 端能讀到 | Write-back work、LFN 對 |

---

## 踩坑列表（Gemini 整理 + 我們 historic）

- **HDD CHS 極限 1024×255×63**（≈ 8.4 GB）— 標準 BIOS limit 是 1024×16×63
  = 504 MB。Image > 504 MB 不實作 LBA（AH=41h-48h）會 DOS 截斷或 crash。
- **FreeDOS 主動 probe AH=41h** — 不 set CF=1 會 boot hang。
- **DSKCHG floppy** — 不 assert 會 corrupt disk filesystem on swap。
- **Host mount timezone** — DOS FAT timestamp 2-sec precision、local time。
  C# `FileInfo.LastWriteTime` UTC / high-precision。convert 算式：
  ```
  Year  = (year - 1980) << 9
  Month = month << 5
  Day   = day
  ⇒ DOS date WORD = (year-1980) << 9 | month << 5 | day
  ⇒ DOS time WORD = hour << 11 | min << 5 | (sec/2)
  ```
  錯了 XCOPY / MAKE 會誤判 file 沒變。
- **Floppy motor spin-up timing** — IBM AT Diag rely on polling FDC
  status + 期待 delay。我們已經有 motor-on hack（Phase 30.7a）— 維持。
- **INT 21h trap 對 real DOS 是 nightmare** — 不要走這條路。

---

## 交叉參考

- Gemini consult log：`tools/knowledgebase/message/20260518_184350.txt`
- 既有 disk infrastructure：
  - `src/AprPc.Cli/Hardware/DiskImage.cs` — geometry + sector r/w
  - `src/AprPc.Cli/Hardware/Fdc8272.cs` — FDC 8272 LLE
  - `src/AprPc.Cli/Bios/HleBios.cs` — INT 13h HLE（line 330-)
  - `src/AprPc.Cli/PcSystemRunner.cs` — MountDisk(0x00/0x01) wiring
- 既有 deferred list：[`MD/issue/pc/deferred-26-30x-summary.md`](deferred-26-30x-summary.md)
  — 本 plan 補上「storage capabilities」這條 axis
- VirtualBox shared folders（V2 reference 實作）：
  https://www.virtualbox.org/manual/ch04.html#sharedfolders
- DOSBox `drive_local.cpp`（HLE kernel 版本、不適用我們、但設計參考）
