# Commit QA workflow（按改動性質分 tier）

> 任何 commit 前依改動性質決定要跑哪些 QA。**每跑必 redirect 到
> `temp/<phase>-<scenario>.log`**，避免事後要重跑。**任何測試 / CLI
> 跑超過 1 分鐘視為掛了，不要拉長 timeout — 直接抓 root cause。**

## Tier 對照表

| Tier | 改動性質 | 必跑 QA | 紀錄 |
|---|---|---|---|
| **0** | 註解 / typo / docs / `.md` only | 直接 commit | — |
| **1** | refactor / rename / debug helper / non-semantic | T1: 360 unit tests | — |
| **2** | bug fix / 新 emitter / spec 改動 / runtime 邏輯 | T1 + T2: 8-combo screenshot matrix | 不必，commit msg 寫驗證結果 |
| **3** | 影響效能的 hot-path 改動 (JIT IR、dispatcher、bus) | T1 + T2 + T3: 3-run loop100 bench | `MD/performance/<時戳>-<topic>.md` |
| **4** | 大型架構變更 (block-JIT phase、新 optimization、cycle accounting) | T1 + T2 + T3 + T4: 完整 matrix + baseline 對比 | 同 Tier 3 + 更新 `MD/note/loop100-bench-*.md` baseline |

## T1: Logic — 360 unit tests

```bash
timeout 30 dotnet test AprGba.slnx --nologo --verbosity minimal > temp/t1-tests.log 2>&1
tail -3 temp/t1-tests.log    # 確認 "失敗: 0"
```

**通過標準：** `失敗: 0，通過: 360`。任何測試 fail = 不准 commit。

## T2: Visual — 8-combo screenshot matrix

```bash
# 強制 rebuild — 避免 stale DLL
timeout 15 dotnet build -c Release src/AprGba.Cli/AprGba.Cli.csproj --no-incremental > temp/t2-build.log 2>&1

for rom in arm thumb; do
  for boot in hle bios; do
    for jit in pi bjit; do
      bios_arg=""; [ "$boot" = "bios" ] && bios_arg="--bios=BIOS/gba_bios.bin"
      jit_arg="";  [ "$jit"  = "bjit" ] && jit_arg="--block-jit"
      out=temp/t2-${rom}-${boot}-${jit}.png
      timeout 25 dotnet src/AprGba.Cli/bin/Release/net10.0/apr-gba.dll \
        --rom=test-roms/gba-tests/${rom}/${rom}.gba \
        --frames=300 ${bios_arg} ${jit_arg} \
        --screenshot=$out > /dev/null 2>&1
      printf "%-10s %-5s %-5s %s\n" "${rom}.gba" "$boot" "$jit" "$(ls -la $out | awk '{print $5}')"
    done
  done
done
md5sum temp/t2-*.png
```

**通過標準：**
- 所有 8 個 PNG md5 hash 完全相同（= 全部 "All tests passed"）
- 用 `Read tool` 看其中一張肉眼確認

**失敗 → 停手：** 哪個 combo fail 哪個就是 regression，先修再 commit。

## T3: Performance — 3-run loop100 bench

```bash
# 移除 unconditional debug 計數 / printf 後跑
for run in 1 2 3; do
  for combo in "arm pi" "arm bjit" "thumb pi" "thumb bjit"; do
    set -- $combo; rom=$1; jit=$2
    jit_arg=""; [ "$jit" = "bjit" ] && jit_arg="--block-jit"
    mips=$(timeout 20 dotnet src/AprGba.Cli/bin/Release/net10.0/apr-gba.dll \
      --rom=test-roms/gba-tests/${rom}/${rom}-loop100.gba --frames=1200 ${jit_arg} 2>&1 | grep MIPS | awk '{print $4}')
    printf "run=%d %-5s %-5s MIPS=%s\n" $run $rom $jit $mips
  done
done > temp/t3-bench.log
cat temp/t3-bench.log
```

**通過標準：**
- 不能比上一次紀錄退步 > 5%（noise band）；退步 5-10% 要追原因
- 3 runs 之間離散度 < 5%（離散大表示測量不穩，要排除背景負載）

**留紀錄 → `MD/performance/YYYYMMDDhhmm-<change-id>.md`**，格式參考既有的（如
`202605030209-scheduler-irq-inline.md`）：

```markdown
# <Phase>.<step> <topic> — <ROM>:<diff>%, <ROM>:<diff>%

> **策略**：...
> **Hypothesis**：...
> **結果**：<roms>: <diffs>。
> **決定**：保留 / revert。

## 1. 結果（3-run avg）
| ROM | Backend | runs | min | **avg** | max | 上次 | **Δ** | 累計 from baseline |
| ... |
## 2. 改動範圍
## 3. 為什麼 [漲/平/退]
## 4. Phase X 累計表（更新一行）
```

## T4: Architecture — full matrix + baseline 對比

T3 + 補：
1. 跑 BIOS LLE 帶 IRQ delivery / scheduler tick 數對比
   ```bash
   for jit in pi bjit; do
     jit_arg=""; [ "$jit" = "bjit" ] && jit_arg="--block-jit"
     timeout 25 dotnet src/AprGba.Cli/bin/Release/net10.0/apr-gba.dll \
       --rom=test-roms/gba-tests/arm/arm.gba --bios=BIOS/gba_bios.bin \
       --frames=300 ${jit_arg} 2>&1 | grep -E "MIPS|IRQs|frames"
   done
   ```
   兩邊 IRQ count 應該一致（drift = scheduler timing bug 的訊號）

2. 如果這次改動讓 baseline 大幅變動（例如 +20% 以上、新模式上線），更新
   [`MD/note/loop100-bench-2026-05.md`](/MD/note/loop100-bench-2026-05.md) 的「結果表」 + 加註腳指向新 perf note

3. commit message 寫對比表 + benchmark log 路徑，方便 archaeology

## Commit message 慣例

依 tier 強度寫不同詳盡度：

| Tier | commit msg 範本 |
|---|---|
| 0 | `docs: <topic>` |
| 1 | `refactor(<area>): <topic>` |
| 2 | `fix(<phase>): <topic>` + 一行驗證結果（"360 tests pass, all 8 screenshot combos match"） |
| 3 | `perf(<phase>): <topic> — <rom>:+X% <rom>:+Y%` + 帶 perf note 路徑 |
| 4 | 同 Tier 3 + 對比表貼進 commit body + baseline 更新說明 |

## 共通規則

1. **強制 `--no-incremental`** 重 build 之前測，避免 stale DLL（吃過虧）
2. **Output 一律 `> temp/<name>.log 2>&1`**，避免要重跑
3. **Timeout < 60s** — 超 = 邏輯 bug，不是 timeout 不夠
4. **不准用 emoji 在 print/Console.WriteLine** — Windows cp950 console 會 crash
5. **跑前先 kill 殘留 testhost**（如 build 抱怨 file lock）：
   ```powershell
   Get-Process testhost -ErrorAction SilentlyContinue | Stop-Process -Force
   ```

## Quick reference: 我這個改動是哪個 tier？

```
有摸到 .cs 檔嗎？        否 → Tier 0
有改 emitter / spec /
runtime 邏輯嗎？         否 → Tier 1
有改 hot path（block IR、
GbaSystemRunner.RunCycles、
GbaMemoryBus.Read*/Write*、
JIT-emitted IR）嗎？      否 → Tier 2
有改 dispatcher 架構 /
新增 optimization pass /
cycle accounting？        否 → Tier 3
                          是 → Tier 4
```
