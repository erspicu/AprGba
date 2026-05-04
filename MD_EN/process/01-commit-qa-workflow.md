# Commit QA workflow (tier by change nature)

> Before any commit, decide which QA to run based on the nature of the
> change. **Every run must redirect to `temp/<phase>-<scenario>.log`** to
> avoid having to re-run later. **Any test / CLI run that exceeds 1 minute
> is treated as hung — don't extend the timeout, find the root cause directly.**

## Tier reference

| Tier | Change nature | QA required | Record |
|---|---|---|---|
| **0** | Comment / typo / docs / `.md` only | Commit directly | — |
| **1** | refactor / rename / debug helper / non-semantic | T1: 360 unit tests | — |
| **2** | bug fix / new emitter / spec change / runtime logic | T1 + T2: 8-combo screenshot matrix | Not needed, write verification result in commit msg |
| **3** | hot-path change affecting perf (JIT IR, dispatcher, bus) | T1 + T2 + T3: 3-run loop100 bench | `MD/performance/<timestamp>-<topic>.md` |
| **4** | Major architectural change (block-JIT phase, new optimization, cycle accounting) | T1 + T2 + T3 + T4: full matrix + baseline comparison | Same as Tier 3 + update `MD/note/loop100-bench-*.md` baseline |

## T1: Logic — 360 unit tests

```bash
timeout 30 dotnet test AprGba.slnx --nologo --verbosity minimal > temp/t1-tests.log 2>&1
tail -3 temp/t1-tests.log    # confirm "Failed: 0"
```

**Pass criterion**: `Failed: 0, Passed: 360`. Any test failure = no commit.

## T2: Visual — 8-combo screenshot matrix

```bash
# Force rebuild — avoid stale DLL
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

**Pass criteria**:
- All 8 PNG md5 hashes match exactly (= all "All tests passed")
- Use `Read tool` to eyeball one of them as confirmation

**Failure → stop**: whichever combo fails is the regression. Fix first, then commit.

## T3: Performance — 3-run loop100 bench

```bash
# Run after removing unconditional debug counters / printfs
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

**Pass criteria**:
- Cannot regress > 5% vs last record (noise band); 5–10% regression requires investigation
- 3-run dispersion < 5% (large dispersion means measurement instability, exclude background load)

**Record → `MD/performance/YYYYMMDDhhmm-<change-id>.md`**, format follows
existing examples (e.g., `202605030209-scheduler-irq-inline.md`):

```markdown
# <Phase>.<step> <topic> — <ROM>:<diff>%, <ROM>:<diff>%

> **Strategy**: ...
> **Hypothesis**: ...
> **Result**: <roms>: <diffs>.
> **Decision**: keep / revert.

## 1. Results (3-run avg)
| ROM | Backend | runs | min | **avg** | max | last | **Δ** | cumulative from baseline |
| ... |
## 2. Change scope
## 3. Why [up/flat/down]
## 4. Phase X cumulative table (update one row)
```

## T4: Architecture — full matrix + baseline comparison

T3 + add:
1. Run BIOS LLE with IRQ delivery / scheduler tick count comparison
   ```bash
   for jit in pi bjit; do
     jit_arg=""; [ "$jit" = "bjit" ] && jit_arg="--block-jit"
     timeout 25 dotnet src/AprGba.Cli/bin/Release/net10.0/apr-gba.dll \
       --rom=test-roms/gba-tests/arm/arm.gba --bios=BIOS/gba_bios.bin \
       --frames=300 ${jit_arg} 2>&1 | grep -E "MIPS|IRQs|frames"
   done
   ```
   IRQ count should match on both sides (drift = scheduler timing bug signal)

2. If this change shifts the baseline significantly (e.g., +20% or more,
   new mode online), update the "results table" in
   `MD/note/loop100-bench-2026-05.md` + add a footnote pointing to the new perf note

3. The commit message should include the comparison table + benchmark log
   path for archaeology

## Commit message conventions

By tier intensity, write at different levels of detail:

| Tier | commit msg template |
|---|---|
| 0 | `docs: <topic>` |
| 1 | `refactor(<area>): <topic>` |
| 2 | `fix(<phase>): <topic>` + one-line verification result ("360 tests pass, all 8 screenshot combos match") |
| 3 | `perf(<phase>): <topic> — <rom>:+X% <rom>:+Y%` + perf note path |
| 4 | Same as Tier 3 + comparison table in commit body + baseline update notes |

## Common rules

1. **Force `--no-incremental`** rebuild before testing, avoid stale DLL (got bitten)
2. **Output always `> temp/<name>.log 2>&1`**, avoid having to re-run
3. **Timeout < 60s** — over = logic bug, not insufficient timeout
4. **No emoji in print/Console.WriteLine** — Windows cp950 console will crash
5. **Kill stale testhost first** (if build complains about file lock):
   ```powershell
   Get-Process testhost -ErrorAction SilentlyContinue | Stop-Process -Force
   ```

## Quick reference: what tier is this change?

```
Touched any .cs file?     No → Tier 0
Changed emitter / spec /
runtime logic?            No → Tier 1
Changed hot path (block IR,
GbaSystemRunner.RunCycles,
GbaMemoryBus.Read*/Write*,
JIT-emitted IR)?          No → Tier 2
Changed dispatcher arch /
new optimization pass /
cycle accounting?         No → Tier 3
                          Yes → Tier 4
```
